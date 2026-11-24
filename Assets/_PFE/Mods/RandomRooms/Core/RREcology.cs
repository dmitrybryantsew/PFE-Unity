using System;

namespace PFE.Mods.RandomRooms.Core
{
    /// <summary>
    /// Native 1.02 enemy and hazard ecology rules sampled once per room.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public class RREcology
    {
        public int type;
        public double difficulty;
        private readonly string scene;
        private readonly Func<double> random;
        private readonly bool even;

        public RREcology(string theme, double dif, int parity, Func<double> rnd)
        {
            scene = theme;
            difficulty = Math.Max(0, dif);
            random = rnd;
            even = parity % 2 == 0;

            type = (int)(random() * 3);
            if (scene == "plant" && random() < 0.25) type = 1;
            if (scene == "sewer") type = 0;
            if (scene == "stable" && type == 1 && random() < difficulty / 20.0) type = 3;
            if (scene == "mane") type = 3 + (int)(random() * 3);
            if (scene != "sewer" && difficulty > 12 && random() < 0.1) type = 6;
        }

        public string Large()
        {
            if (type == 0) return "zombie";
            if (type == 1) return "raider";
            if (type == 3) return "slaver";
            if (type == 4) return "merc";
            if (type == 5) return "alicorn";
            if (type == 6) return "zebra";
            if (scene == "stable" && difficulty >= 12 && random() < Math.Min(difficulty / 100.0, 0.15)) return "eqd";
            if (difficulty >= 5 && random() < 0.1) return "landturret";
            if (difficulty >= 6 && random() < Math.Min(difficulty / 40.0, 0.3)) return "gutsy";
            if (difficulty >= 2 && random() < Math.Min(difficulty / 10.0, 0.5)) return "protect";
            return "robot";
        }

        public string Small()
        {
            if (type == 2 && difficulty >= 4) return even ? "roller" : "msp";
            if (type == 0 || type == 5 || type == 6)
            {
                string id = difficulty >= 2 && random() < Math.Min(difficulty / 30.0, 0.5) ? "scorp" : (even ? "slime" : "ant");
                if (scene == "sewer" && random() < 0.25) id = "rat";
                return id;
            }
            if (difficulty >= 2 && random() < Math.Min(difficulty / 30.0, 0.5)) return "molerat";
            return random() < 0.6 ? "tarakan" : "rat";
        }

        public string Flying()
        {
            if (type == 0 || type == 5) return "bloat";
            if (difficulty < 3) return "";
            return type == 2 ? "spritebot" : "vortex";
        }

        public string Ceiling()
        {
            if (type == 0 || type == 5 || type == 6) return scene == "sewer" && random() < 0.6 ? "slime" : "bloodwing";
            return "turret";
        }

        public string Trap()
        {
            if (type == 0) return even ? "slime" : "trap";
            if ((type == 1 || type == 3 || type == 4 || type == 6) && even)
            {
                if (difficulty >= 10 && random() < 0.5) return "trridge";
                return random() < 0.5 ? "trplate" : "trcans";
            }
            if (scene == "stable" && type == 2 && even) return "trlaser";
            return "mine";
        }

        public string Hidden()
        {
            return type == 2 ? "robocell" : ((type == 1 || type == 3) ? "alarm" : "");
        }

        public bool Armed()
        {
            return type == 1 || type == 2 || type == 3 || type == 4 || type == 6;
        }
    }
}
