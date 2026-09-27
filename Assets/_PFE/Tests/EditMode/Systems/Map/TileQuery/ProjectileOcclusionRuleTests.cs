using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Tests.EditMode.Systems.Map.TileCollision;

namespace PFE.Tests.EditMode.Systems.Map.TileQuery
{
    /// <summary>
    /// Pins <see cref="ProjectileOcclusionRule"/> — the one answer to "does this tile stop a
    /// projectile?", shared by the legacy per-tile collider path and the LowLevelPhysics2D chain
    /// mirror.
    ///
    /// <para><b>Why this fixture exists.</b> The rule was previously inlined in
    /// <c>Projectile.OnTriggerEnter2D</c> and had <i>no test at all</i>: exercising it needs real
    /// <c>Collider2D</c>s and a Unity physics callback, which EditMode cannot drive (a
    /// <c>SendMessage</c> of a physics callback trips <c>ShouldRunBehaviour()</c> asserts). The
    /// guide records that as a deliberate gap, and the flag ships <b>off</b>, so the untested path is
    /// the one the default build runs. Extracting the decision to a pure function of the tile is the
    /// same move that made <c>ProjectilePhysicsMath</c> and <c>TilePhysicsStepMath</c> assertable.</para>
    ///
    /// <para><b>The trap this guards.</b> The natural-looking predicate is
    /// <see cref="TileData.IsSolid"/>, which is <c>physicsType &gt;= Wall</c> and therefore includes
    /// <c>Platform</c> and <c>Stair</c> — it answers "does this block <i>movement</i>". A catwalk and
    /// a ladder are both real colliders in the port, so using that predicate is exactly how bullets
    /// came to stop in mid-air on every catwalk and ladder. The AS3 oracle reads <c>phis</c> alone
    /// (<c>weapon/Bullet.as:476</c>, <c>weapon/PhisBullet.as:242</c>) and every <c>shelf</c>,
    /// <c>diagon</c> and <c>stair</c> form carries <c>phis = 0</c>.</para>
    /// </summary>
    [TestFixture]
    public sealed class ProjectileOcclusionRuleTests
    {
        // ── The truth table ──────────────────────────────────────────────────────────────────

        [TestCase(TilePhysicsType.Wall,     true,  "phis 1 (and 2) — the only value AS3 tests")]
        [TestCase(TilePhysicsType.Air,      false, "phis 0 — empty space")]
        [TestCase(TilePhysicsType.Platform, false, "a shelf/catwalk: phis 0, blocks movement, not a projectile")]
        [TestCase(TilePhysicsType.Stair,    false, "a ladder/stair: phis 0, blocks movement, not a projectile")]
        public void BlocksProjectile_IsTrueForWallsOnly(
            TilePhysicsType physicsType, bool expected, string why)
        {
            var tile = new TileData { physicsType = physicsType };

            Assert.That(ProjectileOcclusionRule.BlocksProjectile(tile), Is.EqualTo(expected),
                $"{physicsType} should {(expected ? "" : "not ")}stop a projectile: {why}.");
        }

        /// <summary>
        /// A collider carrying no <c>TileData</c> must not stop a projectile. Returning true here
        /// would stop bullets on colliders whose semantics are unknown — the louder of the two
        /// failures, and one that would look like a phantom wall.
        /// </summary>
        [Test]
        public void BlocksProjectile_NullTile_IsNotAnOccluder()
        {
            Assert.That(ProjectileOcclusionRule.BlocksProjectile(null), Is.False,
                "A null tile is an unbuilt cell or a collider with no TileData. Stopping on it would " +
                "produce phantom walls.");
        }

        /// <summary>
        /// Names the wrong predicate explicitly, so a future "simplification" to
        /// <c>tile.IsSolid()</c> fails here with its reason attached rather than as a gameplay
        /// regression someone has to bisect.
        /// </summary>
        [TestCase(TilePhysicsType.Platform)]
        [TestCase(TilePhysicsType.Stair)]
        public void TileDataIsSolidIsTheWrongPredicate_ForNonWallMovementSurfaces(TilePhysicsType type)
        {
            var tile = new TileData { physicsType = type };

            Assert.That(tile.IsSolid(), Is.True,
                $"{type} is a movement surface, so TileData.IsSolid() is true — this is the property " +
                "that makes it the wrong predicate here.");
            Assert.That(ProjectileOcclusionRule.BlocksProjectile(tile), Is.False,
                $"{type} carries phis = 0 in AS3 and must let a projectile through. If this fails, " +
                "the rule has been widened to TileData.IsSolid() and bullets will stop in mid-air on " +
                "every catwalk and ladder — the exact defect fixed in b1a4f066.");
        }

        // ── The two representations must agree ───────────────────────────────────────────────

        /// <summary>
        /// The legacy path decides from <see cref="TileData.physicsType"/>; the chain mirror decides
        /// from <c>ITileQueryService.Classify(...) &amp; TileQueryFlags.Solid</c>. Those are two
        /// representations of the same rule, and if they ever disagree the Stage C rollback flag
        /// would change <i>gameplay</i> rather than only the implementation.
        ///
        /// <para>Every tile kind in the ASCII vocabulary is covered in one room, so a kind added to
        /// the vocabulary without a matching rule shows up as a mismatch rather than as silence.</para>
        /// </summary>
        [Test]
        public void MatchesTheChainMirrorPredicate_ForEveryTileKindInTheVocabulary()
        {
            //       0         1         2
            //       0123456789012345678901234
            string[] rows =
            {
                "#########################",   // Wall
                "#.......=...............#",   // Platform (shelf)
                "#.......H...............#",   // Ladder
                "#......./...............#",   // Slope up
                "#.......\\...............#",   // Slope down
                "#.......~...............#",   // Water
                "#########################"
            };

            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(rows);
            var query = new UnifiedTileQueryService(room);

            int occluders = 0;
            int nonOccluders = 0;

            for (int x = 0; x < room.width; x++)
            {
                for (int y = 0; y < room.height; y++)
                {
                    TileData tile = room.tiles[x, y];

                    bool viaRule = ProjectileOcclusionRule.BlocksProjectile(tile);
                    bool viaMirror = (query.Classify(new Vector2Int(x, y)) & TileQueryFlags.Solid) != 0;

                    if (viaRule) occluders++; else nonOccluders++;

                    Assert.That(viaMirror, Is.EqualTo(viaRule),
                        $"Tile ({x},{y}) is {tile.physicsType}: the legacy rule says " +
                        $"{viaRule} and the chain mirror says {viaMirror}. The two paths must agree, " +
                        "or the rollback flag changes gameplay rather than the implementation.");
                }
            }

            // Both directions must be exercised, or the agreement above is vacuous — a room of
            // nothing but air would pass it while proving nothing about occluders.
            Assert.That(occluders, Is.GreaterThan(0),
                "The room must contain at least one tile the rule accepts, or the comparison never " +
                "tests the positive case.");
            Assert.That(nonOccluders, Is.GreaterThan(0),
                "The room must contain at least one tile the rule rejects, or the comparison never " +
                "tests the negative case.");
        }
    }
}
