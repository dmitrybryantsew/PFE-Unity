using System;
using System.Collections.Generic;
using PFE.Core.Rng;

namespace PFE.Systems.Map.Generation
{
    /// <summary>Which wall of a room a door sits in. Mirrors <see cref="PFE.Systems.Map.DoorSide"/>'s
    /// four values but is kept engine-free so the math is assertable offline.</summary>
    public enum DoorWall
    {
        Right = 0,
        Bottom = 1,
        Left = 2,
        Top = 3,
    }

    /// <summary>One candidate connection between two rooms, expressed as a pair of door-slot indices.</summary>
    public struct DoorMatch
    {
        /// <summary>Slot index on the <em>first</em> room (the one whose side faces the neighbour).</summary>
        public int AIndex;

        /// <summary>Slot index on the <em>second</em> room — always <c>AIndex + 11</c>.</summary>
        public int BIndex;

        /// <summary><c>min(qualityA, qualityB)</c>; the carve rule needs <c>&gt;= 2</c>.</summary>
        public int Quality;
    }

    /// <summary>
    /// Pure port of the door half of AS3 <c>Location</c> / <c>Land.buildRandomLand</c>:
    /// the 22-slot door array (<c>Location.as:605-641</c>), the mirror swap (<c>Location.as:609-631</c>),
    /// the neighbour match test (<c>Land.as:420-453</c>) and the carve counts (<c>Land.as:458-494</c>).
    ///
    /// <para><b>Slot map (22 slots, 0..21):</b> <c>0-5</c> right, <c>6-10</c> bottom, <c>11-16</c> left,
    /// <c>17-21</c> top. The partner of slot <c>i</c> is <c>i ± 11</c>.</para>
    ///
    /// <para><b>Oracle bug, deliberately not copied:</b> AS3's downward match loop iterates
    /// <c>6..11</c> (<c>Land.as:441</c>), so it reads slot 11 (a <em>left</em> door) and slot 22
    /// (out of range ⇒ <c>undefined</c> ⇒ <c>Math.min</c> ⇒ <c>NaN</c> ⇒ the test is false). The
    /// iteration here is <c>6..10</c>, which is equivalent and correct. See
    /// <c>docs/LandGameplayLoop/05_VERIFICATION_AND_QUIRKS.md</c> Q1.</para>
    /// </summary>
    public static class DoorMatchMath
    {
        /// <summary>Slots 0..21 are meaningful; AS3 allocates a little slack.</summary>
        public const int DoorSlotCount = 22;

        /// <summary><c>Location.as:802</c> — a door with quality below this is never carved.</summary>
        public const int MinCarveQuality = 2;

        /// <summary><c>Location.as:635-641</c> — the quality used when a room declares no <c>&lt;doors&gt;</c>.</summary>
        public const int DefaultQuality = 2;

        /// <summary>AS3's right side draws three times with replacement (<c>Land.as:471-477</c>).</summary>
        public const int RightCarveAttempts = 3;

        /// <summary>AS3's lower side draws exactly once (<c>Land.as:485-488</c>).</summary>
        public const int BottomCarveAttempts = 1;

        /// <summary>The slot range on the first room for a given wall.</summary>
        public static (int min, int max) SlotRange(DoorWall wall)
        {
            switch (wall)
            {
                case DoorWall.Right: return (0, 5);
                case DoorWall.Bottom: return (6, 10);
                case DoorWall.Left: return (11, 16);
                default: return (17, 21);
            }
        }

        /// <summary>
        /// Split an AS3 <c>&lt;doors&gt;</c> string on <c>.</c> into a 22-slot array
        /// (<c>Location.as:605-608</c>).
        ///
        /// <para>A <c>null</c> or empty string means the room declares no <c>&lt;doors&gt;</c> at all, so
        /// every slot gets <see cref="DefaultQuality"/> (<c>Location.as:633-641</c>).</para>
        ///
        /// <para>A slot the string does not reach stays <c>0</c> (no door). That mirrors AS3, where a
        /// missing entry is <c>undefined</c> and <c>Math.min(undefined, x)</c> is <c>NaN</c> — and
        /// <c>NaN &gt;= 2</c> is false, so the slot is not a candidate. Filling those slots with
        /// <see cref="DefaultQuality"/> instead would invent a door, so the two cases are kept
        /// distinct. (Every one of the oracle's 413 <c>&lt;doors&gt;</c> strings is exactly 22 numeric
        /// values, so this arm is defensive; the four room files with no element take the all-default
        /// path above.)</para>
        /// </summary>
        public static int[] Split(string doorsString)
        {
            int[] doors = new int[DoorSlotCount];

            // No <doors> element at all: AS3 fills all 22 slots with 2.
            if (string.IsNullOrEmpty(doorsString))
            {
                for (int i = 0; i < DoorSlotCount; i++) doors[i] = DefaultQuality;
                return doors;
            }

            // Present: read positionally. Unreached or unparseable slots keep 0 (AS3's NaN => no door).
            string[] parts = doorsString.Split('.');
            for (int i = 0; i < parts.Length && i < DoorSlotCount; i++)
            {
                if (int.TryParse(parts[i], out int q)) doors[i] = q;
            }
            return doors;
        }

        /// <summary>
        /// AS3 <c>Location.as:609-631</c> — the horizontal mirror: slots 0-5 swap with 11-16,
        /// 6↔10, 7↔9, 17↔21, 18↔20. (Slot 8 and slot 19 are the centre of their side and stay put.)
        /// </summary>
        public static void MirrorInPlace(int[] doors)
        {
            if (doors == null || doors.Length < DoorSlotCount) return;

            Swap(doors, 6, 10);
            Swap(doors, 7, 9);
            Swap(doors, 17, 21);
            Swap(doors, 18, 20);
            for (int i = 0; i <= 5; i++) Swap(doors, i, i + 11);
        }

        /// <summary>A copy of <paramref name="doors"/> with <see cref="MirrorInPlace"/> applied.</summary>
        public static int[] Mirrored(int[] doors)
        {
            if (doors == null) return null;
            var copy = (int[])doors.Clone();
            MirrorInPlace(copy);
            return copy;
        }

        /// <summary>
        /// AS3 pass 2 (<c>Land.as:411-457</c>): every slot on the shared wall where
        /// <c>min(a[i], b[i + 11]) &gt;= 2</c>.
        /// </summary>
        /// <param name="a">The first room's slots (the one on the left / above).</param>
        /// <param name="b">The neighbour's slots.</param>
        /// <param name="wall">Which wall of <paramref name="a"/> faces <paramref name="b"/>.</param>
        public static List<DoorMatch> Match(int[] a, int[] b, DoorWall wall)
        {
            var result = new List<DoorMatch>();
            if (a == null || b == null) return result;
            if (a.Length < DoorSlotCount || b.Length < DoorSlotCount) return result;

            (int min, int max) = SlotRange(wall);
            for (int i = min; i <= max; i++)
            {
                int partner = i + 11;
                if (partner >= DoorSlotCount) continue;

                int quality = Math.Min(a[i], b[partner]);
                if (quality >= MinCarveQuality)
                {
                    result.Add(new DoorMatch { AIndex = i, BIndex = partner, Quality = quality });
                }
            }
            return result;
        }

        /// <summary>
        /// AS3 pass 3 (<c>Land.as:458-494</c>): pick the doors to actually carve.
        /// The right side draws <see cref="RightCarveAttempts"/> times <em>with replacement</em>; the
        /// lower side draws <see cref="BottomCarveAttempts"/> time. An empty match set carves nothing.
        /// </summary>
        public static List<DoorMatch> SelectCarves(IReadOnlyList<DoorMatch> matches, DoorWall wall, IRngService rng)
        {
            var result = new List<DoorMatch>();
            if (matches == null || matches.Count == 0 || rng == null) return result;

            int attempts = wall == DoorWall.Right ? RightCarveAttempts : BottomCarveAttempts;
            for (int i = 0; i < attempts; i++)
            {
                // AS3 draws with replacement and does not remove the picked element.
                int idx = rng.NextInt(matches.Count);
                result.Add(matches[idx]);
            }
            return result;
        }

        private static void Swap(int[] arr, int i, int j)
        {
            int t = arr[i];
            arr[i] = arr[j];
            arr[j] = t;
        }
    }
}
