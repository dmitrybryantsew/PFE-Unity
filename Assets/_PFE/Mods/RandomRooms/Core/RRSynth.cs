using System;
using System.Collections.Generic;
using System.Text;

namespace PFE.Mods.RandomRooms.Core
{
    public class SynthesizedObject
    {
        public string id;
        public string code;
        public int x;
        public int y;
        public string fixture;
        public string rrContent;
        public string rrMount;
        public string uid;
        public Dictionary<string, string> attrs = new();
    }

    public class SynthesizedRoom
    {
        public string name;
        public string theme;
        public string archetype;
        public string sceneForm;
        public int attempts;
        public int danger;
        public int value;
        public int ecologyType;
        public int width = RRSynth.GRID_W;
        public int height = RRSynth.GRID_H;
        public string[] rows = new string[RRSynth.GRID_H];
        public string tileDataString;
        public List<SynthesizedObject> objects = new();
        public List<BackDecoration> backs = new();
        public int[] ports = new int[22];
        public SceneOptions options;
        public (int x, int y) spawn;
    }

    /// <summary>
    /// Master synthesis orchestrator running the full 48-attempt room generation pipeline.
    /// Emits dot-separated tile strings, 22-slot doors, objects, and backgrounds for a
    /// caller-sized canvas (default <see cref="GRID_W"/> x <see cref="GRID_H"/>).
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public class RRSynth
    {
        public const int GRID_W = 48;
        public const int GRID_H = 25;
        public const int MIN_GRID_W = 48;
        public const int MIN_GRID_H = 25;
        public const string WALL_CHARS = "ABCDEFGHIJKLMNOPQRST";
        public const string FCHARS = "ABCDEFGHIJKLMNOPQRST_";
        public const string OCHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZАБВГДЕЖЗИЙКЛМОПСТ-ДЕКНР*,;:";
        public const string GENERATOR = "space-v11";

        public static readonly string[] BIOMES = { "stable", "sewer", "plant", "mane" };

        public RRArchitecture plan { get; private set; }
        public RRFurnish furnishing { get; private set; }
        public RRPopulation population { get; private set; }
        public int attempts { get; private set; }

        public SynthesizedRoom Generate(
            int n,
            string biome = "stable",
            string rtype = "",
            int[] boundary = null,
            int depth = 0,
            bool peaceful = false,
            bool safe = false,
            GenerationContext context = null,
            int width = 0,
            int height = 0)
        {
            int gridW = width > 0 ? Math.Max(MIN_GRID_W, width) : GRID_W;
            int gridH = height > 0 ? Math.Max(MIN_GRID_H, height) : GRID_H;

            if (context == null)
            {
                context = new GenerationContext
                {
                    contentVersion = "13",
                    partition = "rules"
                };
            }

            if (!context.seed.HasValue)
            {
                throw new ArgumentException("Partition prototype requires an explicit room seed in context");
            }

            var prototypeSeed = new RRSeed(context.seed.Value);
            Exception lastError = null;
            var errors = new Dictionary<string, int>();

            for (attempts = 1; attempts <= 48; attempts++)
            {
                try
                {
                    var geomSeed = prototypeSeed.Fork("geometry:" + attempts);
                    plan = new RRArchitecture(() => geomSeed.Next());
                    plan.Build(biome, rtype, boundary, context, gridW, gridH);
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    string reason = ex.Message;
                    errors[reason] = errors.TryGetValue(reason, out int cnt) ? cnt + 1 : 1;
                }
            }

            if (lastError != null)
            {
                string portStr = boundary != null ? string.Join(".", boundary) : "null";
                throw new InvalidOperationException($"Architecture cannot fit {biome} ports={portStr}: {lastError.Message}");
            }

            var popSeed = prototypeSeed.Fork("population");
            population = new RRPopulation(plan, () => popSeed.Next(), n, depth, peaceful, safe, context);
            population.Build();

            var furnSeed = prototypeSeed.Fork("furnishing");
            furnishing = new RRFurnish(plan, () => furnSeed.Next());
            furnishing.Build();

            // Export tiles with biome-specific beam and stair materials
            string beam = biome == "stable" ? "Е" : (biome == "mane" ? "К" : "-");
            string up = biome == "stable" ? "И" : (biome == "mane" ? "Л" : "В");
            string down = biome == "stable" ? "Й" : (biome == "mane" ? "М" : "Г");

            var result = new SynthesizedRoom
            {
                name = "syn_" + n,
                width = gridW,
                height = gridH,
                rows = new string[gridH],
                theme = plan.theme,
                archetype = plan.archetype,
                sceneForm = plan.sceneForm,
                attempts = attempts,
                danger = population.contentPlan != null ? population.contentPlan.danger : 0,
                value = population.contentPlan != null ? population.contentPlan.value : 0,
                ecologyType = population.ecology != null ? population.ecology.type : 0,
                ports = (int[])plan.ports.Clone(),
                options = RRScene.Options(plan.theme, plan.sceneForm),
                spawn = plan.spawn
            };

            var sb = new StringBuilder();
            for (int y = 0; y < gridH; y++)
            {
                var rowCells = new string[gridW];
                for (int x = 0; x < gridW; x++)
                {
                    rowCells[x] = plan.grid[y, x];
                }
                string rowJoined = string.Join(".", rowCells).Replace("-", beam).Replace("В", up).Replace("Г", down);
                result.rows[y] = rowJoined;
            }
            result.tileDataString = string.Join("\n", result.rows);

            // Add furniture objects
            for (int i = 0; i < furnishing.objects.Count; i++)
            {
                var o = furnishing.objects[i];
                var synthObj = new SynthesizedObject
                {
                    id = o.id,
                    code = "syn_" + n + "_o" + i,
                    x = o.x,
                    y = o.y,
                    fixture = o.fixture
                };

                if (o.fixture == "door" || o.fixture == "hatch")
                {
                    synthObj.attrs["lock"] = "0";
                    synthObj.attrs["mine"] = "0";
                }

                result.objects.Add(synthObj);
            }

            // Add population content objects
            for (int i = 0; i < population.objects.Count; i++)
            {
                var popObj = population.objects[i];
                var synthObj = new SynthesizedObject
                {
                    id = popObj.id,
                    code = "syn_" + n + "_p" + result.objects.Count,
                    x = popObj.x,
                    y = popObj.y,
                    rrContent = popObj.rrContent,
                    rrMount = popObj.rrMount,
                    uid = popObj.uid,
                    attrs = new Dictionary<string, string>(popObj.attrs)
                };

                if (popObj.turn != 0) synthObj.attrs["turn"] = popObj.turn.ToString();
                if (!string.IsNullOrEmpty(popObj.vis)) synthObj.attrs["vis"] = popObj.vis;
                if (!string.IsNullOrEmpty(popObj.unlockTarget)) synthObj.attrs["unlockTarget"] = popObj.unlockTarget;
                if (popObj.cacheFace.HasValue) synthObj.attrs["cacheFace"] = popObj.cacheFace.Value.ToString();

                result.objects.Add(synthObj);
            }

            // Add backgrounds
            foreach (var b in furnishing.backs)
            {
                result.backs.Add(new BackDecoration { id = b.id, x = b.x, y = b.y });
            }

            return result;
        }

        public static bool ValidateRoom(SynthesizedRoom room)
        {
            if (room == null || room.options == null) return false;

            int gridW = room.width > 0 ? room.width : GRID_W;
            int gridH = room.height > 0 ? room.height : GRID_H;
            if (room.rows == null || room.rows.Length != gridH) return false;

            for (int y = 0; y < gridH; y++)
            {
                string[] row = room.rows[y].Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                if (row.Length != gridW) return false;

                for (int x = 0; x < gridW; x++)
                {
                    string c = row[x];
                    if (c.Length == 0 || FCHARS.IndexOf(c[0]) < 0) return false;
                    for (int j = 1; j < c.Length; j++)
                    {
                        if (OCHARS.IndexOf(c[j]) < 0) return false;
                    }
                }
            }

            if (room.ports.Length != 22) return false;

            var codes = new HashSet<string>();
            int players = 0;

            foreach (var obj in room.objects)
            {
                if (string.IsNullOrEmpty(obj.code) || !codes.Add(obj.code)) return false;
                if (obj.id == "player") players++;
            }

            return players == 1;
        }
    }
}
