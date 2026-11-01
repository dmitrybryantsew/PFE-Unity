using NUnit.Framework;
using PFE.Systems.Map;
using PFE.Systems.Physics;
using UnityEngine;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Pins AS3's <b>throw window</b> (<c>Box.t_throw</c>) and — the reason this file exists — the fact
    /// that it is a <b>different flag</b> from <c>Box.isThrow</c>.
    ///
    /// <para><b>The report.</b> "Objects thrown by alicorns (including the alicorn boss) do not damage
    /// the player." They were thrown correctly — the impulse, the aim point and the velocity were all
    /// right — and then the damage sweep refused every one of them. The port had one flag standing for
    /// two oracle fields:</para>
    /// <list type="bullet">
    /// <item><c>isThrow</c> — set by every thrower, cleared only by a wall/floor/ceiling hit, read only
    /// by the collision predicates (a thrown prop passes over shelves). <b>No effect on damage.</b></item>
    /// <item><c>t_throw</c> — the damage grace, written by exactly one site in the oracle
    /// (<c>UnitPlayer.as:1871</c>, <c>= 2</c>), counted down one per prop step
    /// (<c>Box.as:646</c>), and read once in <c>attDrop</c> (<c>:928</c>).</item>
    /// </list>
    ///
    /// <para>Because the port gated damage on the first flag, an alicorn's crate refused to hurt anyone
    /// until it happened to touch geometry — which, thrown across open air at a player, it never does
    /// before reaching them. <c>UnitAlicorn.as:1238</c> and <c>UnitBossAlicorn.as:693</c> set
    /// <c>isThrow</c> and nothing else, so an enemy throw must arrive with a window of <b>zero</b>.</para>
    ///
    /// <para><b>Why the constants are asserted rather than only the behaviour.</b> 2 and 0 are the
    /// whole rule, and both are invertible into something plausible ("give it a few frames so it does
    /// not clip the thrower" reads fine and is the bug). The layer half is driven through the real
    /// <see cref="RoomObjectPhysicsLayer"/>, so a change that stores the window but never counts it down
    /// — or that counts it down by a delta time instead of one step — is red here.</para>
    /// </summary>
    public class PropThrowGraceTests
    {
        // ── the numbers ────────────────────────────────────────────────────────────────────────

        /// <summary><c>UnitPlayer.as:1871</c> — <c>t_throw = 2</c>, and it is 2 ticks, not a duration.</summary>
        [Test]
        public void PlayerThrow_CarriesTwoTicks()
        {
            Assert.AreEqual(2, PropThrowGrace.PlayerThrowTicks);
        }

        /// <summary>
        /// <c>UnitAlicorn.as:1238</c> / <c>UnitBossAlicorn.as:693</c> — no <c>t_throw</c> write at all,
        /// so the field keeps its <c>Box.as:94</c> default. This is the value that was wrong in play.
        /// </summary>
        [Test]
        public void EnemyThrow_CarriesNoTicks()
        {
            Assert.AreEqual(0, PropThrowGrace.EnemyThrowTicks);
        }

        [Test]
        public void Grace_IsActiveOnlyWhilePositive()
        {
            Assert.IsFalse(PropThrowGrace.IsActive(PropThrowGrace.EnemyThrowTicks),
                "Zero is not a grace — an enemy throw damages on the first overlapping unit.");
            Assert.IsTrue(PropThrowGrace.IsActive(1));
            Assert.IsTrue(PropThrowGrace.IsActive(PropThrowGrace.PlayerThrowTicks));
        }

        /// <summary>
        /// <c>Box.as:646`'s <c>--t_throw</c> guarded by <c>&gt; 0</c>, so it floors at zero. Without the
        /// guard a prop that is never stepped again would drift negative and — if anything ever compared
        /// it with <c>!= 0</c> — wrap into a permanent window.
        /// </summary>
        [Test]
        public void Tick_CountsDownAndFloorsAtZero()
        {
            Assert.AreEqual(1, PropThrowGrace.Tick(PropThrowGrace.PlayerThrowTicks));
            Assert.AreEqual(0, PropThrowGrace.Tick(1));
            Assert.AreEqual(0, PropThrowGrace.Tick(0));
        }

        // ── the layer ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A player throw stores the window, and the prop keeps <c>isThrown</c> as well: the two flags
        /// are set together by <c>UnitPlayer.as:1870-1871</c> and are read by different consumers.
        /// </summary>
        [Test]
        public void ReleaseWithAPlayerGrace_StoresTheWindowAndKeepsIsThrown()
        {
            RoomInstance room = MakeEmptyRoom();
            ObjectInstance crate = MakeCrate();
            room.AddObject(crate);

            Assert.IsTrue(room.TryReleaseHeldObject(
                crate, new Vector2(600f, 0f), treatAsThrow: true,
                throwGraceTicks: PropThrowGrace.PlayerThrowTicks));

            Assert.AreEqual(PropThrowGrace.PlayerThrowTicks, crate.runtimeState.dynamicState.throwGraceTicks);
            Assert.IsTrue(crate.runtimeState.dynamicState.isThrown,
                "`isThrow` is what lets the crate pass over shelves; it is not the grace.");
        }

        /// <summary>
        /// An enemy release passes no window, and the layer must not substitute one — this is the
        /// positive control for the bug: same seam, same prop, only the thrower differs.
        /// </summary>
        [Test]
        public void ReleaseWithNoGrace_StoresZero()
        {
            RoomInstance room = MakeEmptyRoom();
            ObjectInstance crate = MakeCrate();
            room.AddObject(crate);

            Assert.IsTrue(room.TryReleaseHeldObject(
                crate, new Vector2(600f, 0f), treatAsThrow: true,
                throwGraceTicks: PropThrowGrace.EnemyThrowTicks));

            Assert.AreEqual(0, crate.runtimeState.dynamicState.throwGraceTicks);
            Assert.IsTrue(crate.runtimeState.dynamicState.isThrown);
        }

        /// <summary>
        /// The window is consumed by <b>prop steps</b>, one per step, and it does not clear
        /// <c>isThrown</c> on the way out. Two steps of a two-tick window leave it at zero, which is the
        /// third tick's damage.
        /// </summary>
        [Test]
        public void TheWindow_IsCountedDownOnePerPropStep_AndDoesNotClearIsThrown()
        {
            RoomInstance room = MakeEmptyRoom();
            ObjectInstance crate = MakeCrate();

            // Well above the floor so neither step lands it: a landing would clear `isThrown` through
            // the collision path and the control below would pass for the wrong reason.
            crate.position = new Vector2(200f, 400f);
            room.AddObject(crate);

            room.TryReleaseHeldObject(crate, new Vector2(600f, 0f), treatAsThrow: true,
                                      throwGraceTicks: PropThrowGrace.PlayerThrowTicks);

            const float step = 1f / 30f;
            room.ObjectPhysicsLayer.Update(room, step);
            Assert.AreEqual(1, crate.runtimeState.dynamicState.throwGraceTicks,
                "One prop step consumes exactly one tick of the window.");

            room.ObjectPhysicsLayer.Update(room, step);
            Assert.AreEqual(0, crate.runtimeState.dynamicState.throwGraceTicks,
                "Two steps empty a two-tick window; from here the crate damages.");

            Assert.IsTrue(crate.runtimeState.dynamicState.isThrown,
                "The countdown must NOT clear `isThrow` — in the oracle that flag is cleared by a " +
                "collision, and clearing it here would make a thrown crate land on the first shelf it " +
                "crosses.");
        }

        // ── the wiring (source lint) ──────────────────────────────────────────────────────────
        //
        // The two halves above live in plain classes and are driven for real. The half that actually
        // decides whether a thrown crate hurts anybody — `UnitController.SweepPropImpacts`, which is on a
        // MonoBehaviour — cannot be constructed offline, so it is pinned by reading the shipping source.
        // Without this, the layer could store a perfect zero-tick window and the sweep could still be
        // reading `isThrown`, which is precisely the shape that shipped.

        const string UnitControllerPath = "Entities/Units/UnitController.cs";
        const string PlayerThrowPath = "Entities/Player/PlayerTelekinesisController.cs";
        const string AlicornThrowPath = "Entities/Enemies/Archetypes/Alicorn/AlicornTelekinesis.cs";
        const string BossAlicornThrowPath = "Entities/Enemies/Archetypes/Alicorn/BossAlicornTelekinesis.cs";

        /// <summary>
        /// The damage sweep must gate on the throw <b>window</b> and must not read <c>isThrown</c> at all.
        /// Both directions are asserted: a sweep that still mentions <c>isThrown</c> is the bug, and a
        /// sweep that stopped mentioning the window is the same bug written differently.
        /// </summary>
        [Test]
        public void TheDamageSweep_GatesOnTheThrowWindow_AndNeverOnIsThrown()
        {
            string unit = PFE.Tests.Editor.Common.SourceLint.ReadStripped(UnitControllerPath);
            string sweep = PFE.Tests.Editor.Common.SourceLint.MethodBody(
                unit, "public virtual void SweepPropImpacts()");

            Assert.That(sweep, Does.Contain("throwGraceTicks"),
                "SweepPropImpacts no longer reads the throw window, so nothing gates a thrown prop's " +
                "damage on it. `t_throw` is the only field that decides this in the oracle " +
                "(Box.as:928).");

            Assert.That(sweep, Does.Contain("propState.isThrown = false;"),
                "A prop that lands a damaging hit must stop being a throw — AS3 `Box.as:934`, " +
                "`this.isThrow = false;`. That is the ONE legitimate mention of the flag here.");

            // Absent control: with that single write removed, the flag must not appear at all. Any
            // remaining mention is a READ, i.e. a gate — and that is the bug.
            string withoutTheLandedHitClear = sweep.Replace("propState.isThrown = false;", string.Empty);
            Assert.That(withoutTheLandedHitClear, Does.Not.Contain("isThrown"),
                "SweepPropImpacts reads `isThrown` again. That flag is `Box.isThrow` — set by every " +
                "thrower and cleared only by a wall/floor hit — so gating damage on it makes an " +
                "enemy-thrown crate harmless for its whole flight. This is the reported bug.");
        }

        /// <summary>
        /// Each throw site must name its own window. The player's is two ticks; an alicorn's and the
        /// boss's are zero, because their <c>throwTele</c> writes <c>isThrow</c> and never
        /// <c>t_throw</c>.
        /// </summary>
        [Test]
        public void EveryThrowSite_DeclaresTheWindowItsOracleWriterUses()
        {
            string player = PFE.Tests.Editor.Common.SourceLint.ReadStripped(PlayerThrowPath);
            Assert.That(player, Does.Contain("PropThrowGrace.PlayerThrowTicks"),
                "The player's throw no longer passes its two-tick window (UnitPlayer.as:1871). Without " +
                "it the crate damages the thrower on release, because it leaves from inside its own " +
                "bounding box.");

            foreach (string enemyPath in new[] { AlicornThrowPath, BossAlicornThrowPath })
            {
                string enemy = PFE.Tests.Editor.Common.SourceLint.ReadStripped(enemyPath);

                Assert.That(enemy, Does.Contain("PropThrowGrace.EnemyThrowTicks"),
                    enemyPath + " no longer states that its throw carries no damage grace. The oracle " +
                    "sets `isThrow` and nothing else there, so the crate must hurt the first unit it " +
                    "overlaps.");

                Assert.That(enemy, Does.Not.Contain("PlayerThrowTicks"),
                    enemyPath + " hands the PLAYER's two-tick grace to an enemy throw. That is the " +
                    "exact defect that made alicorn-thrown objects harmless.");
            }
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A 2x2 <c>tip='box'</c> prop, built from attributes rather than a
        /// <c>MapObjectDefinition</c>: a <c>ScriptableObject</c> cannot be constructed outside the
        /// editor, so a fixture that needs one is 0/N offline and pins nothing.
        /// </summary>
        static ObjectInstance MakeCrate()
        {
            var prop = new ObjectInstance
            {
                objectId = "mcrate4",
                objectType = "box",
                attributes = new System.Collections.Generic.List<MapObjectAttributeData>
                {
                    new MapObjectAttributeData { key = "size", value = "2" },
                    new MapObjectAttributeData { key = "wid", value = "2" },
                    new MapObjectAttributeData { key = "tip", value = "box" },
                },
            };

            prop.InitializeDynamicRuntimeState();
            return prop;
        }

        /// <summary>An empty tile grid, so nothing the props do collides with a tile.</summary>
        static RoomInstance MakeEmptyRoom()
        {
            var room = new RoomInstance
            {
                id = "test_room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                landPosition = new Vector3Int(0, 0, 0),
            };

            room.InitializeTiles();
            return room;
        }
    }
}
