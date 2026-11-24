using System;
using System.Collections.Generic;

namespace PFE.Mods.RandomRooms.Core
{
    public class CityInfo
    {
        public string form;
        public string zone;
        public int block;
        public int roofRow;
        public bool leftFacade;
        public bool rightFacade;
    }

    /// <summary>
    /// Immutable shared edges of an unbounded map.
    /// Border sockets are decided consistently across neighbor coordinates.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public class RRMapPlan
    {
        public uint seed { get; }
        private readonly Dictionary<(int x, int y), int> verticalSlots = new();

        public RRMapPlan(Func<double> random)
        {
            seed = (uint)(random() * 4294967295.0);
        }

        public RRMapPlan(uint s)
        {
            seed = s;
        }

        public uint Value(int x, int y, int salt)
        {
            uint v = unchecked(seed ^ (uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)salt * 83492791u);
            v ^= v >> 16;
            v = unchecked(v * 0x85ebca6bu);
            v ^= v >> 13;
            v = unchecked(v * 0xc2b2ae35u);
            v ^= v >> 16;
            return v;
        }

        public bool ParentLeft(int x, int y)
        {
            return y == 0 || (x > 0 && Value(x, y, 1) % 2 == 0);
        }

        public bool Horizontal(int x, int y)
        {
            return ParentLeft(x + 1, y) || Value(x, y, 2) % 100 < 62;
        }

        public bool Downward(int x, int y)
        {
            return !ParentLeft(x, y + 1) || Value(x, y, 3) % 100 < 62;
        }

        public int Vslot(int x, int y)
        {
            int prev = -1;
            for (int row = 0; row <= y; row++)
            {
                var key = (x, row);
                if (!verticalSlots.TryGetValue(key, out int slot))
                {
                    int n = (int)(Value(x, row, 4) % (uint)(prev < 0 ? 5 : 4));
                    if (prev >= 0 && n >= prev) n++;
                    if (prev >= 0 && Value(x, row, 5) % 100 < 4) n = prev;
                    slot = n;
                    verticalSlots[key] = slot;
                }
                prev = slot;
            }
            return prev;
        }

        public CityInfo City(int x, int y)
        {
            int start = 0, block = 0, width;
            while (true)
            {
                width = 2 + (int)(Value(block, 0, 81) % 3);
                if (x < start + width + 1) break;
                start += width + 1;
                block++;
            }
            int roof = 0;
            bool street = (x == start + width);
            string form;
            if (street)
            {
                form = "street_links";
            }
            else if (y <= roof)
            {
                form = "rooftops";
            }
            else
            {
                string[] forms = { "apartments", "offices", "commercial", "ruined" };
                form = forms[Value(block, (y - roof - 1) / 2, 83) % 4];
            }

            return new CityInfo
            {
                form = form,
                zone = street ? "street" : (y <= roof ? "roof" : "building"),
                block = block,
                roofRow = roof,
                leftFacade = (x == start),
                rightFacade = (x == start + width - 1)
            };
        }

        public int[] Ports(int x, int y)
        {
            int[] a = RRPorts.Empty();
            if (Horizontal(x, y)) a[1 + (int)(Value(x, y, 6) % 5)] = 3;
            if (x > 0 && Horizontal(x - 1, y)) a[12 + (int)(Value(x - 1, y, 6) % 5)] = 3;
            if (Downward(x, y)) a[6 + Vslot(x, y)] = 2;
            if (y > 0 && Downward(x, y - 1)) a[17 + Vslot(x, y - 1)] = 2;
            return a;
        }
    }
}
