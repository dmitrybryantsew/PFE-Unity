using NUnit.Framework;
using PFE.Core;
using PFE.Systems.Map;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The collider-overlay sub-filters. The contract worth testing is the one the parser's own doc
    /// comment states: <b>an unknown token is an error, never a silent skip</b>, because a filter that
    /// quietly matches nothing is indistinguishable from "the tile really has no collider" — the exact
    /// conclusion the overlay exists to support.
    /// </summary>
    [TestFixture]
    public class ColliderDebugFiltersTests
    {
        // ── Tile filter: parsing ─────────────────────────────────────────────

        [Test]
        public void Tiles_Empty_MeansAll()
        {
            foreach (string text in new[] { null, "", "   " })
            {
                Assert.IsTrue(ColliderDebugFilters.TryParseTiles(text, out ColliderDebugTileFilter filter, out string error),
                    $"'{text}' should parse");
                Assert.AreEqual(ColliderDebugTileFilter.All, filter);
                Assert.IsNull(error);
            }
        }

        [Test]
        public void Tiles_OffTokens_MeanNone()
        {
            // "none" is the one that used to be broken: IsOffToken did not list it, so `col tiles none`
            // fell through to the unknown-token error even though a comment claimed otherwise.
            foreach (string text in new[] { "off", "none", "0", "false", "hide", "clear", "NONE" })
            {
                Assert.IsTrue(ColliderDebugFilters.TryParseTiles(text, out ColliderDebugTileFilter filter, out string error),
                    $"'{text}' should parse");
                Assert.AreEqual(ColliderDebugTileFilter.None, filter, $"'{text}' should mean None");
                Assert.IsNull(error);
            }
        }

        [Test]
        public void Tiles_ShelfFamilyAliases_MeanPlatform()
        {
            // "shelf"/"catwalk" is what the original game and the user call the one-way tile; it decodes
            // to TilePhysicsType.Platform. Accepting the word matters more than the canonical name.
            foreach (string text in new[] { "platform", "plat", "shelf", "shelve", "shelves", "catwalk", "balcony" })
            {
                Assert.IsTrue(ColliderDebugFilters.TryParseTiles(text, out ColliderDebugTileFilter filter, out string error),
                    $"'{text}' should parse: {error}");
                Assert.AreEqual(ColliderDebugTileFilter.Platform, filter, $"'{text}' should mean Platform");
            }
        }

        [Test]
        public void Tiles_RemainingAliases_MeanTheRightFlags()
        {
            foreach (string text in new[] { "air", "empty" })
            {
                ColliderDebugFilters.TryParseTiles(text, out ColliderDebugTileFilter f, out _);
                Assert.AreEqual(ColliderDebugTileFilter.Air, f, $"'{text}' should mean Air");
            }

            foreach (string text in new[] { "wall", "solid" })
            {
                ColliderDebugFilters.TryParseTiles(text, out ColliderDebugTileFilter f, out _);
                Assert.AreEqual(ColliderDebugTileFilter.Wall, f, $"'{text}' should mean Wall");
            }

            foreach (string text in new[] { "stair", "stairs", "slope", "ramp" })
            {
                ColliderDebugFilters.TryParseTiles(text, out ColliderDebugTileFilter f, out _);
                Assert.AreEqual(ColliderDebugTileFilter.Stair, f, $"'{text}' should mean Stair");
            }
        }

        [Test]
        public void Tiles_AcceptsAList()
        {
            Assert.IsTrue(ColliderDebugFilters.TryParseTiles("shelf,stair", out ColliderDebugTileFilter filter, out string error),
                error);
            Assert.AreEqual(ColliderDebugTileFilter.Platform | ColliderDebugTileFilter.Stair, filter);

            Assert.IsTrue(ColliderDebugFilters.TryParseTiles("shelf stair", out filter, out _));
            Assert.AreEqual(ColliderDebugTileFilter.Platform | ColliderDebugTileFilter.Stair, filter);
        }

        [Test]
        public void Tiles_UnknownToken_FailsAndNamesIt()
        {
            Assert.IsFalse(ColliderDebugFilters.TryParseTiles("shelvs", out ColliderDebugTileFilter filter, out string error));

            StringAssert.Contains("shelvs", error);
            StringAssert.Contains("Unknown tile type", error);

            // Pinned as observed behaviour, not as a preference: on failure this parser leaves the
            // out-param at All, whereas DebugOverlayChannels.TryParse leaves it at None. Neither is
            // wrong, but they differ, and a caller that ignores the bool gets "draw everything" here
            // and "draw nothing" there. If you unify them, this test is the one that should change.
            Assert.AreEqual(ColliderDebugTileFilter.All, filter);
        }

        [Test]
        public void Tiles_UnknownTokenAmongGoodOnes_DoesNotPartiallySucceed()
        {
            Assert.IsFalse(ColliderDebugFilters.TryParseTiles("shelf,shelvs", out ColliderDebugTileFilter filter, out string error));

            StringAssert.Contains("shelvs", error);
            Assert.AreNotEqual(ColliderDebugTileFilter.Platform, filter,
                "a failed parse must not keep the good half of the list");
        }

        // ── Tile filter: formatting and matching ─────────────────────────────

        [Test]
        public void Tiles_Format_RoundTripsThroughTryParse()
        {
            var samples = new[]
            {
                ColliderDebugTileFilter.None,
                ColliderDebugTileFilter.All,
                ColliderDebugTileFilter.Air,
                ColliderDebugTileFilter.Platform,
                ColliderDebugTileFilter.Platform | ColliderDebugTileFilter.Stair,
                ColliderDebugTileFilter.Wall | ColliderDebugTileFilter.Stair,
            };

            foreach (ColliderDebugTileFilter sample in samples)
            {
                string text = ColliderDebugFilters.Format(sample);

                Assert.IsTrue(ColliderDebugFilters.TryParseTiles(text, out ColliderDebugTileFilter parsed, out string error),
                    $"'{text}' should parse back: {error}");
                Assert.AreEqual(sample, parsed, $"'{text}' must round-trip to {sample}");
            }
        }

        [Test]
        public void Tiles_Format_UsesTheShortWords()
        {
            Assert.AreEqual("off", ColliderDebugFilters.Format(ColliderDebugTileFilter.None));
            Assert.AreEqual("all", ColliderDebugFilters.Format(ColliderDebugTileFilter.All));
            Assert.AreEqual("platform", ColliderDebugFilters.Format(ColliderDebugTileFilter.Platform));
            Assert.AreEqual("platform,stair",
                ColliderDebugFilters.Format(ColliderDebugTileFilter.Platform | ColliderDebugTileFilter.Stair));
        }

        [Test]
        public void Matches_RespectsTheFilter()
        {
            Assert.IsTrue(ColliderDebugFilters.Matches(ColliderDebugTileFilter.All, TilePhysicsType.Wall));
            Assert.IsTrue(ColliderDebugFilters.Matches(ColliderDebugTileFilter.Platform, TilePhysicsType.Platform));
            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugTileFilter.Platform, TilePhysicsType.Wall));
            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugTileFilter.None, TilePhysicsType.Air));
        }

        [Test]
        public void Matches_EachTileTypeMapsToItsOwnFlag()
        {
            // A copy-paste in the switch that gave two types the same flag would show up only here.
            Assert.IsTrue(ColliderDebugFilters.Matches(ColliderDebugTileFilter.Air, TilePhysicsType.Air));
            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugTileFilter.Air, TilePhysicsType.Wall));
            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugTileFilter.Air, TilePhysicsType.Platform));
            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugTileFilter.Air, TilePhysicsType.Stair));

            Assert.IsTrue(ColliderDebugFilters.Matches(ColliderDebugTileFilter.Wall, TilePhysicsType.Wall));
            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugTileFilter.Wall, TilePhysicsType.Platform));
            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugTileFilter.Wall, TilePhysicsType.Stair));

            Assert.IsTrue(ColliderDebugFilters.Matches(ColliderDebugTileFilter.Stair, TilePhysicsType.Stair));
            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugTileFilter.Stair, TilePhysicsType.Platform));
        }

        [Test]
        public void Matches_UnmappedPhysicsType_ReturnsFalse()
        {
            // The `default` arm. A new TilePhysicsType member that nobody added a flag for must be
            // visibly absent from the overlay, not silently folded into one of the four — a filter that
            // quietly matches the wrong thing is how a debugging tool starts lying.
            var unmapped = (TilePhysicsType)99;

            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugTileFilter.All, unmapped),
                "even All must not claim an unmapped physics type");
        }

        // ── Unit filter ──────────────────────────────────────────────────────

        [Test]
        public void Units_Empty_MeansAll()
        {
            Assert.IsTrue(ColliderDebugFilters.TryParseUnits("", out ColliderDebugUnitFilter filter, out string error));
            Assert.AreEqual(ColliderDebugUnitFilter.All, filter);
            Assert.IsNull(error);
        }

        [Test]
        public void Units_OffTokens_MeanNone()
        {
            foreach (string text in new[] { "off", "none", "0", "false", "hide", "clear" })
            {
                Assert.IsTrue(ColliderDebugFilters.TryParseUnits(text, out ColliderDebugUnitFilter filter, out string error),
                    $"'{text}' should parse");
                Assert.AreEqual(ColliderDebugUnitFilter.None, filter, $"'{text}' should mean None");
            }
        }

        [Test]
        public void Units_PlayerAliases_MeanPlayer()
        {
            foreach (string text in new[] { "player", "pc", "hero" })
            {
                Assert.IsTrue(ColliderDebugFilters.TryParseUnits(text, out ColliderDebugUnitFilter filter, out _));
                Assert.AreEqual(ColliderDebugUnitFilter.Player, filter, $"'{text}' should mean Player");
            }
        }

        [Test]
        public void Units_NpsIsAFirstClassSpellingForNpc()
        {
            // "nps" is what the user actually types, so it is a first-class spelling, not a tolerated
            // typo. Pinned so nobody "cleans it up".
            foreach (string text in new[] { "npc", "nps", "enemy", "enemies", "unit", "units", "mob", "mobs" })
            {
                Assert.IsTrue(ColliderDebugFilters.TryParseUnits(text, out ColliderDebugUnitFilter filter, out _));
                Assert.AreEqual(ColliderDebugUnitFilter.Npc, filter, $"'{text}' should mean Npc");
            }
        }

        [Test]
        public void Units_UnknownToken_FailsAndNamesIt()
        {
            Assert.IsFalse(ColliderDebugFilters.TryParseUnits("playr", out ColliderDebugUnitFilter filter, out string error));

            StringAssert.Contains("playr", error);
            StringAssert.Contains("Unknown unit type", error);
        }

        [Test]
        public void Units_Format_RoundTripsThroughTryParse()
        {
            var samples = new[]
            {
                ColliderDebugUnitFilter.None,
                ColliderDebugUnitFilter.All,
                ColliderDebugUnitFilter.Player,
                ColliderDebugUnitFilter.Npc,
            };

            foreach (ColliderDebugUnitFilter sample in samples)
            {
                string text = ColliderDebugFilters.Format(sample);

                Assert.IsTrue(ColliderDebugFilters.TryParseUnits(text, out ColliderDebugUnitFilter parsed, out string error),
                    $"'{text}' should parse back: {error}");
                Assert.AreEqual(sample, parsed, $"'{text}' must round-trip to {sample}");
            }
        }

        [Test]
        public void Units_Matches_SplitsPlayerFromEveryoneElse()
        {
            Assert.IsTrue(ColliderDebugFilters.Matches(ColliderDebugUnitFilter.Player, isPlayer: true));
            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugUnitFilter.Player, isPlayer: false));

            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugUnitFilter.Npc, isPlayer: true));
            Assert.IsTrue(ColliderDebugFilters.Matches(ColliderDebugUnitFilter.Npc, isPlayer: false));

            Assert.IsTrue(ColliderDebugFilters.Matches(ColliderDebugUnitFilter.All, isPlayer: true));
            Assert.IsTrue(ColliderDebugFilters.Matches(ColliderDebugUnitFilter.All, isPlayer: false));

            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugUnitFilter.None, isPlayer: true));
            Assert.IsFalse(ColliderDebugFilters.Matches(ColliderDebugUnitFilter.None, isPlayer: false));
        }

        // ── Cross-cutting ────────────────────────────────────────────────────

        [Test]
        public void BothParsers_AreCaseInsensitive()
        {
            Assert.IsTrue(ColliderDebugFilters.TryParseTiles("SHELF", out ColliderDebugTileFilter tiles, out _));
            Assert.AreEqual(ColliderDebugTileFilter.Platform, tiles);

            Assert.IsTrue(ColliderDebugFilters.TryParseUnits("Player", out ColliderDebugUnitFilter units, out _));
            Assert.AreEqual(ColliderDebugUnitFilter.Player, units);
        }

        [Test]
        public void TileAndUnitFilterFlags_ReuseTheSameIntegerValues()
        {
            // Both enums start at 1 << 0, which is legal because they are distinct types — but the
            // overlay indexes both, so a transposed argument would still compile. Pinned as
            // documentation of the hazard; the type system will not catch it for you.
            Assert.AreEqual(1 << 0, (int)ColliderDebugTileFilter.Air);
            Assert.AreEqual(1 << 0, (int)ColliderDebugUnitFilter.Player);

            // The two overloads stay distinct, which is what stops the transposition from being silent.
            Assert.IsTrue(ColliderDebugFilters.Matches(ColliderDebugTileFilter.All, TilePhysicsType.Wall));
            Assert.IsTrue(ColliderDebugFilters.Matches(ColliderDebugUnitFilter.All, isPlayer: true));
        }

        [Test]
        public void UsageStrings_NameEveryAcceptedValue()
        {
            StringAssert.Contains("air", ColliderDebugFilters.TileUsage);
            StringAssert.Contains("wall", ColliderDebugFilters.TileUsage);
            StringAssert.Contains("platform", ColliderDebugFilters.TileUsage);
            StringAssert.Contains("shelf", ColliderDebugFilters.TileUsage);
            StringAssert.Contains("stair", ColliderDebugFilters.TileUsage);

            StringAssert.Contains("player", ColliderDebugFilters.UnitUsage);
            StringAssert.Contains("npc", ColliderDebugFilters.UnitUsage);
        }
    }
}
