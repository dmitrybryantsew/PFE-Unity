using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Systems.Map.Generation;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// Asserts <see cref="DoorMatchMath"/> against AS3 <c>Location.as:605-641</c> (the 22-slot door array
    /// and the mirror swap), <c>Land.as:411-457</c> (the neighbour match test) and
    /// <c>Land.as:458-494</c> (the carve counts).
    /// </summary>
    [TestFixture]
    public class DoorMatchMathTests
    {
        private sealed class ZeroRng : IRngService
        {
            public bool Chance(float probability) => true;
            public float NextFloat() => 0f;
            public uint NextUInt() => 0u;
            public int NextInt(int maxExclusive) => 0;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public void Shuffle<T>(IList<T> list) { }
            public IRngService GetStream(RngStream stream, int? salt = null) => this;
        }

        private static int[] Slots(params (int index, int quality)[] entries)
        {
            int[] doors = new int[DoorMatchMath.DoorSlotCount];
            for (int i = 0; i < doors.Length; i++) doors[i] = 0;
            foreach ((int index, int quality) in entries) doors[index] = quality;
            return doors;
        }

        // =====================================================================
        //  The slot map and the split
        // =====================================================================

        [Test]
        public void Split_ProducesTwentyTwoSlots()
        {
            int[] doors = DoorMatchMath.Split("2.2.2.2.2.2.2.2.2.2.2");
            Assert.That(doors.Length, Is.EqualTo(DoorMatchMath.DoorSlotCount),
                "Location.as:605-608 splits the <doors> string; 22 slots are meaningful.");
        }

        [Test]
        public void Split_EmptyStringFillsEverySlotWithDefaultQuality()
        {
            int[] doors = DoorMatchMath.Split("");
            foreach (int q in doors) Assert.That(q, Is.EqualTo(DoorMatchMath.DefaultQuality),
                "Location.as:635-641 — a room with no <doors> gets all-2.");
        }

        [Test]
        public void Split_ReadsEachSlotPositionally()
        {
            int[] doors = DoorMatchMath.Split("3.1.4.0.5.2.6.7.8.9.1.2.3.4.5.6.7.8.9.0.1.2");
            Assert.That(doors[0], Is.EqualTo(3));
            Assert.That(doors[5], Is.EqualTo(2), "Slot 5 is the last right-side slot.");
            Assert.That(doors[6], Is.EqualTo(6), "Slot 6 is the first bottom slot.");
            Assert.That(doors[10], Is.EqualTo(1), "Slot 10 is the last bottom slot.");
            Assert.That(doors[11], Is.EqualTo(2), "Slot 11 is the first left slot.");
            Assert.That(doors[17], Is.EqualTo(8), "Slot 17 is the first top slot.");
            Assert.That(doors[21], Is.EqualTo(2), "Slot 21 is the last top slot.");
        }

        [Test]
        public void Split_RealOracleString_KeepsTheWideDoorQualities()
        {
            // RoomsPlant.as:119, room "сортиры" — a verbatim <doors> element.
            int[] doors = DoorMatchMath.Split("3.3.0.3.3.3.4.4.4.4.4.0.3.0.3.3.3.0.4.4.4.4");

            Assert.That(doors.Length, Is.EqualTo(DoorMatchMath.DoorSlotCount));
            Assert.That(doors[0], Is.EqualTo(3));
            Assert.That(doors[2], Is.EqualTo(0), "Slot 2 is authored as no-door.");
            Assert.That(doors[6], Is.EqualTo(4), "A 4 must survive decoding.");
            Assert.That(doors[11], Is.EqualTo(0), "Slot 11 is the first left slot and is authored 0.");
            Assert.That(doors[21], Is.EqualTo(4));

            // The oracle's modal quality is 3, not 2. A decoder that flattened every candidate to
            // Narrow would make Location.as:821-829 (the `param2 > 2` wide-door arm) unreachable, which
            // is exactly what the tile-code heuristic did. Guard the regression explicitly.
            int wide = 0;
            foreach (int q in doors) if (q > DoorMatchMath.MinCarveQuality) wide++;
            Assert.That(wide, Is.GreaterThan(0),
                "quality > 2 must survive decoding or every carved door is the minimum width");
        }

        [Test]
        public void Split_AllZeroOracleString_YieldsNoCandidates()
        {
            // RoomsPlant.as:504, room "архив_1" — a room the oracle authors with no doors at all.
            int[] doors = DoorMatchMath.Split("0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0");

            foreach (int q in doors)
            {
                Assert.That(q, Is.LessThan(DoorMatchMath.MinCarveQuality),
                    "an all-zero <doors> yields no candidates; the port used to carve 2-6 doors here");
            }

            // Positive control: the same call on a real string DOES produce candidates, so the loop
            // above cannot pass merely because the decoder returns nothing at all.
            int[] real = DoorMatchMath.Split("3.3.0.3.3.3.4.4.4.4.4.0.3.0.3.3.3.0.4.4.4.4");
            int candidates = 0;
            foreach (int q in real) if (q >= DoorMatchMath.MinCarveQuality) candidates++;
            Assert.That(candidates, Is.GreaterThan(0), "control: a real string must yield candidates");
        }

        [Test]
        public void Split_PresentButShortString_DoesNotInventDoors()
        {
            // A slot past the end of a present string is `undefined` in AS3, so Math.min(undefined, x)
            // is NaN and `NaN >= 2` is false — those slots are NOT doors. Only a null/empty string means
            // "the room declares no <doors>", which is the all-2 fallback (Location.as:633-641).
            // The two cases must not be conflated, or a malformed string invents doors.
            int[] shortString = DoorMatchMath.Split("3.3");
            Assert.That(shortString[0], Is.EqualTo(3));
            Assert.That(shortString[1], Is.EqualTo(3));
            for (int i = 2; i < DoorMatchMath.DoorSlotCount; i++)
            {
                Assert.That(shortString[i], Is.LessThan(DoorMatchMath.MinCarveQuality),
                    $"slot {i} is past the end of the string and must not be a candidate");
            }

            // Control: the empty-string case is genuinely different and still fills every slot with 2.
            int[] absent = DoorMatchMath.Split("");
            foreach (int q in absent)
                Assert.That(q, Is.EqualTo(DoorMatchMath.DefaultQuality),
                    "an absent <doors> is the all-2 case, not the no-doors case");
        }

        [Test]
        public void SlotRange_MatchesTheOracleIndexMap()
        {
            Assert.That(DoorMatchMath.SlotRange(DoorWall.Right), Is.EqualTo((0, 5)));
            Assert.That(DoorMatchMath.SlotRange(DoorWall.Bottom), Is.EqualTo((6, 10)),
                "The oracle's down loop reads 6..11 (Land.as:441) but slot 11 is a LEFT door; the port uses 6..10.");
            Assert.That(DoorMatchMath.SlotRange(DoorWall.Left), Is.EqualTo((11, 16)));
            Assert.That(DoorMatchMath.SlotRange(DoorWall.Top), Is.EqualTo((17, 21)));
        }

        // =====================================================================
        //  The mirror swap
        // =====================================================================

        [Test]
        public void Mirror_SwapsTheFourPairsAndTheTwoSides()
        {
            int[] doors = Slots((0, 1), (1, 2), (5, 3), (6, 4), (7, 5), (8, 6), (9, 7), (10, 8),
                                (11, 9), (12, 10), (16, 11), (17, 12), (18, 13), (19, 14), (20, 15), (21, 16));

            DoorMatchMath.MirrorInPlace(doors);

            // Location.as:609-631
            Assert.That(doors[6], Is.EqualTo(8), "6 <-> 10");
            Assert.That(doors[10], Is.EqualTo(4));
            Assert.That(doors[7], Is.EqualTo(7), "7 <-> 9");
            Assert.That(doors[9], Is.EqualTo(5));
            Assert.That(doors[17], Is.EqualTo(16), "17 <-> 21");
            Assert.That(doors[21], Is.EqualTo(12));
            Assert.That(doors[18], Is.EqualTo(15), "18 <-> 20");
            Assert.That(doors[20], Is.EqualTo(13));

            Assert.That(doors[0], Is.EqualTo(9), "0 <-> 11");
            Assert.That(doors[11], Is.EqualTo(1));
            Assert.That(doors[5], Is.EqualTo(11), "5 <-> 16");
            Assert.That(doors[16], Is.EqualTo(3));

            Assert.That(doors[8], Is.EqualTo(6), "Slot 8 is the centre of its side — unchanged.");
            Assert.That(doors[19], Is.EqualTo(14), "Slot 19 is the centre of its side — unchanged.");
        }

        [Test]
        public void Mirrored_DoesNotMutateTheSource()
        {
            int[] doors = Slots((0, 1), (11, 9));
            int[] mirrored = DoorMatchMath.Mirrored(doors);

            Assert.That(doors[0], Is.EqualTo(1), "The source array must be untouched.");
            Assert.That(mirrored[0], Is.EqualTo(9));
            Assert.That(mirrored[11], Is.EqualTo(1));
        }

        // =====================================================================
        //  The neighbour match test
        // =====================================================================

        [Test]
        public void Match_RightSide_FindsASharedDoor()
        {
            // Positive control: slot 3 on A and slot 14 (= 3 + 11) on B both offer quality >= 2.
            int[] a = Slots((3, 3));
            int[] b = Slots((14, 4));

            List<DoorMatch> matches = DoorMatchMath.Match(a, b, DoorWall.Right);

            Assert.That(matches.Count, Is.EqualTo(1));
            Assert.That(matches[0].AIndex, Is.EqualTo(3));
            Assert.That(matches[0].BIndex, Is.EqualTo(14));
            Assert.That(matches[0].Quality, Is.EqualTo(3), "min(3, 4).");
        }

        [Test]
        public void Match_RightSide_NoSharedDoorProducesNothing()
        {
            // Negative control: A has a right door at slot 3, B has nothing on its left side.
            int[] a = Slots((3, 3));
            int[] b = Slots((17, 5));

            Assert.That(DoorMatchMath.Match(a, b, DoorWall.Right), Is.Empty,
                "A pair with no shared door must not connect.");
        }

        [Test]
        public void Match_RefusesQualityBelowTwo()
        {
            int[] a = Slots((3, 1));
            int[] b = Slots((14, 5));

            Assert.That(DoorMatchMath.Match(a, b, DoorWall.Right), Is.Empty,
                "min(1, 5) = 1 < 2 — setDoor refuses it (Location.as:802-807).");
        }

        [Test]
        public void Match_BottomSide_UsesSixThroughTen()
        {
            int[] a = Slots((6, 2), (10, 2));
            int[] b = Slots((17, 2), (21, 2));

            List<DoorMatch> matches = DoorMatchMath.Match(a, b, DoorWall.Bottom);

            Assert.That(matches.Count, Is.EqualTo(2));
            Assert.That(matches[0].AIndex, Is.EqualTo(6));
            Assert.That(matches[1].AIndex, Is.EqualTo(10));
        }

        [Test]
        public void Match_BottomSide_StaysInsideSixThroughTen()
        {
            // Every slot offers quality 5 on both rooms. A bottom match must still return exactly the five
            // slots 6..10 — NOT slot 11 (a left door) and NOT slot 22 (out of range), which is where the
            // oracle's 6..11 loop reaches (Land.as:441). If the port copied that loop, this would be 6.
            int[] a = new int[DoorMatchMath.DoorSlotCount];
            int[] b = new int[DoorMatchMath.DoorSlotCount];
            for (int i = 0; i < a.Length; i++) { a[i] = 5; b[i] = 5; }

            List<DoorMatch> matches = DoorMatchMath.Match(a, b, DoorWall.Bottom);

            Assert.That(matches.Count, Is.EqualTo(5), "Slots 6..10 only.");
            foreach (DoorMatch m in matches)
            {
                Assert.That(m.AIndex, Is.InRange(6, 10));
                Assert.That(m.BIndex, Is.InRange(17, 21));
            }
        }

        [Test]
        public void Match_NullOrShortArraysAreSafe()
        {
            Assert.That(DoorMatchMath.Match(null, Slots(), DoorWall.Right), Is.Empty);
            Assert.That(DoorMatchMath.Match(Slots(), null, DoorWall.Right), Is.Empty);
            Assert.That(DoorMatchMath.Match(new int[4], Slots(), DoorWall.Right), Is.Empty);
        }

        // =====================================================================
        //  The carve counts
        // =====================================================================

        [Test]
        public void SelectCarves_RightSideDrawsThreeTimes()
        {
            int[] a = Slots((0, 2), (1, 2), (2, 2));
            int[] b = Slots((11, 2), (12, 2), (13, 2));
            List<DoorMatch> matches = DoorMatchMath.Match(a, b, DoorWall.Right);

            List<DoorMatch> carved = DoorMatchMath.SelectCarves(matches, DoorWall.Right, new ZeroRng());

            Assert.That(carved.Count, Is.EqualTo(DoorMatchMath.RightCarveAttempts),
                "Land.as:471-477 draws three times (with replacement).");
            Assert.That(DoorMatchMath.RightCarveAttempts, Is.EqualTo(3));
        }

        [Test]
        public void SelectCarves_BottomSideDrawsExactlyOnce()
        {
            int[] a = Slots((6, 2), (7, 2));
            int[] b = Slots((17, 2), (18, 2));
            List<DoorMatch> matches = DoorMatchMath.Match(a, b, DoorWall.Bottom);

            List<DoorMatch> carved = DoorMatchMath.SelectCarves(matches, DoorWall.Bottom, new ZeroRng());

            Assert.That(carved.Count, Is.EqualTo(1), "Land.as:485-488 carves exactly one down door.");
        }

        [Test]
        public void SelectCarves_EmptyMatchSetCarvesNothing()
        {
            Assert.That(DoorMatchMath.SelectCarves(new List<DoorMatch>(), DoorWall.Right, new ZeroRng()), Is.Empty);
            Assert.That(DoorMatchMath.SelectCarves(null, DoorWall.Right, new ZeroRng()), Is.Empty);
        }
    }
}
