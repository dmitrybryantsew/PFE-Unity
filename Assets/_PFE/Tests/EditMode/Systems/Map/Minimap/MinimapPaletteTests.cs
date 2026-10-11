using NUnit.Framework;
using PFE.Systems.Map.Minimap;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Map.Minimap
{
    /// <summary>
    /// Pins the minimap palette to the oracle's numbers.
    ///
    /// <para><b>Why the numbers and not "it is blue".</b> The palette is a transcription of
    /// <c>Location.drawMap</c> (<c>Location.as:2711-2817</c>), which paints packed 24-bit RGB literals.
    /// A test that asserted "water is blue-ish" would pass for any blue and would not notice a
    /// transcription slip — and a slipped byte is exactly the failure this is exposed to, because the
    /// source is a wall of decimal literals. So every case below asserts the <b>packed int the oracle
    /// wrote</b>, and the control asserts the numbers are not all the same.</para>
    /// </summary>
    [TestFixture]
    public sealed class MinimapPaletteTests
    {
        /// <summary>The oracle's ARGB-without-alpha view of a colour: the 24 bits it wrote.</summary>
        private static int Packed(Color32 c) => (c.r << 16) | (c.g << 8) | c.b;

        private static MinimapTileFacts Facts(
            bool wall = false, bool shelf = false, bool slope = false, bool stair = false,
            bool water = false, bool indestructible = false, bool door = false, int hp = 1000)
            => new MinimapTileFacts(wall, shelf, slope, stair, water, indestructible, door, hp);

        // ── The transcription itself: every colour must be the oracle's literal ──────────────

        [TestCase("air", 13091)]
        [TestCase("water", 26367)]
        [TestCase("shelf", 8079407)]
        [TestCase("stair", 6710886)]
        [TestCase("solid", 65433)]
        [TestCase("damaged", 104794)]
        [TestCase("indestructible", 16777215)]
        [TestCase("door tile", 6525188)]
        [TestCase("interactable", 16763904)]
        [TestCase("prob", 16711799)]
        [TestCase("checkpoint", 16711935)]
        [TestCase("npc", 5570815)]
        public void EveryColourIsTheOracleLiteral(string which, int oraclePacked)
        {
            Color32 actual = which switch
            {
                "air" => MinimapPalette.Air,
                "water" => MinimapPalette.Water,
                "shelf" => MinimapPalette.ShelfOrSlope,
                "stair" => MinimapPalette.Stair,
                "solid" => MinimapPalette.Solid,
                "damaged" => MinimapPalette.SolidDamaged,
                "indestructible" => MinimapPalette.SolidIndestructible,
                "door tile" => MinimapPalette.SolidDoor,
                "interactable" => MinimapPalette.MarkerInteractable,
                "prob" => MinimapPalette.MarkerProb,
                "checkpoint" => MinimapPalette.MarkerCheckpoint,
                _ => MinimapPalette.MarkerNpc
            };

            Assert.That(Packed(actual), Is.EqualTo(oraclePacked),
                $"'{which}' must be the oracle's packed value {oraclePacked} (0x{oraclePacked:X6}).");
        }

        // ── Branch behaviour, in the oracle's own order ──────────────────────────────────────

        [Test]
        public void OpenGround_IsTheDefaultAirColour()
        {
            Assert.That(MinimapPalette.TileColor(Facts()), Is.EqualTo(MinimapPalette.Air));
        }

        [Test]
        public void Water_Shelf_AndSlope_EachOverrideTheDefault()
        {
            Assert.That(MinimapPalette.TileColor(Facts(water: true)), Is.EqualTo(MinimapPalette.Water));
            Assert.That(MinimapPalette.TileColor(Facts(shelf: true)), Is.EqualTo(MinimapPalette.ShelfOrSlope));
            Assert.That(MinimapPalette.TileColor(Facts(slope: true)), Is.EqualTo(MinimapPalette.ShelfOrSlope));
        }

        [Test]
        public void Stair_IsGrey()
        {
            Assert.That(MinimapPalette.TileColor(Facts(stair: true)), Is.EqualTo(MinimapPalette.Stair));
        }

        [Test]
        public void AHealthyWall_IsBrightGreen()
        {
            Assert.That(MinimapPalette.TileColor(Facts(wall: true, hp: 1000)),
                Is.EqualTo(MinimapPalette.Solid));
        }

        /// <summary>
        /// The <c>&lt; 100</c> boundary is strict in the oracle (<c>Location.as:2748</c>), so exactly
        /// 100 is still a healthy wall. Both sides asserted, because a mutated comparison operator
        /// (<c>&lt;</c> → <c>&lt;=</c>) only shows up on the boundary value.
        /// </summary>
        [Test]
        public void TheDamagedWallThresholdIsStrictlyBelow100()
        {
            Assert.That(MinimapPalette.TileColor(Facts(wall: true, hp: 99)),
                Is.EqualTo(MinimapPalette.SolidDamaged), "99 hp is damaged.");
            Assert.That(MinimapPalette.TileColor(Facts(wall: true, hp: 100)),
                Is.EqualTo(MinimapPalette.Solid), "100 hp is NOT damaged — the oracle tests `< 100`.");
        }

        [Test]
        public void WallSubBranches_IndestructibleBeatsDoorBeatsDamaged()
        {
            // The oracle's if/else-if chain: the FIRST matching branch wins.
            Assert.That(MinimapPalette.TileColor(Facts(wall: true, indestructible: true, door: true, hp: 5)),
                Is.EqualTo(MinimapPalette.SolidIndestructible));
            Assert.That(MinimapPalette.TileColor(Facts(wall: true, door: true, hp: 5)),
                Is.EqualTo(MinimapPalette.SolidDoor));
            Assert.That(MinimapPalette.TileColor(Facts(wall: true, hp: 5)),
                Is.EqualTo(MinimapPalette.SolidDamaged));
        }

        /// <summary>
        /// The oracle assigns in sequence and the LAST write wins, so a wall overrides the earlier
        /// water/shelf/stair assignments — it does not blend, and it is not "wall colour for the
        /// non-wall bits". A port that reordered the branches (e.g. tested walls first) would paint
        /// every underwater wall as water.
        /// </summary>
        [Test]
        public void BranchOrder_IsTheOracles_SoTheLastWriteWins()
        {
            Assert.That(MinimapPalette.TileColor(Facts(wall: true, water: true)),
                Is.EqualTo(MinimapPalette.Solid), "a wall overrides the water colour painted before it.");

            Assert.That(MinimapPalette.TileColor(Facts(shelf: true, stair: true)),
                Is.EqualTo(MinimapPalette.Stair), "stair is painted after shelf, so it wins.");

            Assert.That(MinimapPalette.TileColor(Facts(water: true, shelf: true)),
                Is.EqualTo(MinimapPalette.ShelfOrSlope), "shelf is painted after water, so it wins.");

            // …and a non-wall keeps the shelf colour, so the wall branch is not simply unconditional.
            Assert.That(MinimapPalette.TileColor(Facts(shelf: true)),
                Is.EqualTo(MinimapPalette.ShelfOrSlope));
        }

        /// <summary>
        /// The positive control for the table above: the colours are genuinely distinct, so a
        /// transcription that accidentally reused one value would fail the literal cases rather than
        /// passing them all with the same number.
        /// </summary>
        [Test]
        public void ThePaletteIsNotOneColourRepeated()
        {
            var distinct = new System.Collections.Generic.HashSet<int>
            {
                Packed(MinimapPalette.Air),
                Packed(MinimapPalette.Water),
                Packed(MinimapPalette.ShelfOrSlope),
                Packed(MinimapPalette.Stair),
                Packed(MinimapPalette.Solid),
                Packed(MinimapPalette.SolidDamaged),
                Packed(MinimapPalette.SolidIndestructible),
                Packed(MinimapPalette.SolidDoor),
                Packed(MinimapPalette.MarkerInteractable),
                Packed(MinimapPalette.MarkerProb),
                Packed(MinimapPalette.MarkerCheckpoint),
                Packed(MinimapPalette.MarkerNpc),
            };

            Assert.That(distinct.Count, Is.EqualTo(12),
                "All twelve oracle colours must be distinct; a collision means a mis-transcribed literal.");
        }
    }
}
