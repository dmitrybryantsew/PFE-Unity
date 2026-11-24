using System;

namespace PFE.Mods.RandomRooms.Core
{
    public struct PortRect
    {
        public int x0;
        public int y0;
        public int x1;
        public int y1;

        public PortRect(int x0, int y0, int x1, int y1)
        {
            this.x0 = x0;
            this.y0 = y0;
            this.x1 = x1;
            this.y1 = y1;
        }
    }

    /// <summary>
    /// The 22 native door/socket slots along the room perimeter (48x25 grid).
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public static class RRPorts
    {
        public static int[] Empty()
        {
            return new int[22];
        }

        public static bool Vertical(int p)
        {
            return (p >= 6 && p <= 10) || p >= 17;
        }

        public static int Opposite(int p)
        {
            return p < 11 ? p + 11 : p - 11;
        }

        public static int Mirrored(int p)
        {
            if (p < 6) return p + 11;
            if (p < 11) return 16 - p;
            if (p < 17) return p - 11;
            return 38 - p;
        }

        public static int[] Mirror(int[] a)
        {
            int[] b = Empty();
            for (int p = 0; p < 22; p++)
            {
                b[Mirrored(p)] = a[p];
            }
            return b;
        }

        public static PortRect Rect(int p, int amount = 2)
        {
            int x, y;
            if (Vertical(p))
            {
                x = 5 + 9 * (p >= 17 ? p - 17 : p - 6);
                y = p >= 17 ? 0 : 23;
                return new PortRect(x - (amount > 2 ? 1 : 0), y, x + 1 + (amount > 2 ? 1 : 0), y + 1);
            }
            x = p >= 11 ? 0 : 46;
            y = 3 + 4 * (p >= 11 ? p - 11 : p);
            return new PortRect(x, y - (amount > 2 ? 2 : 1), x + 1, y);
        }

        public static int[] Sample(Func<double> random)
        {
            int[] a = Empty();
            a[1 + (int)(random() * 5)] = 3;
            a[12 + (int)(random() * 5)] = 3;
            int top = (int)(random() * 5);
            int bottom = (top + 1 + (int)(random() * 4)) % 5;
            if (random() < 0.7) a[17 + top] = 2;
            if (random() < 0.7) a[6 + bottom] = 2;
            return a;
        }
    }
}
