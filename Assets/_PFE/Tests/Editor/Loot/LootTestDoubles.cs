using System;
using System.Collections.Generic;
using PFE.Core.Rng;
using PFE.Systems.Loot;

namespace PFE.Tests.Editor.Loot
{
    /// <summary>
    /// A deterministic RNG whose every draw returns the same configured value. Used where a test needs
    /// to <i>place</i> a random number rather than observe a distribution — the chance gate, a loop
    /// bound, a scatter offset. <see cref="Calls"/> is the point: it is how a test proves the roller did
    /// <b>not</b> consume randomness (the "a second Interact rolls nothing" control).
    /// </summary>
    public sealed class StubRng : IRngService
    {
        /// <summary>Every draw returns this. Must be in [0, 1).</summary>
        public float Value = 0.5f;

        /// <summary>How many draws have been taken. Includes every overload.</summary>
        public int Calls;

        /// <summary>Draws taken since the last <see cref="Reset"/>.</summary>
        public int Mark;

        public void Reset()
        {
            Calls = 0;
            Mark = 0;
        }

        /// <summary>Draws taken since the last call to this method.</summary>
        public int Since()
        {
            int n = Calls - Mark;
            Mark = Calls;
            return n;
        }

        public uint NextUInt()
        {
            Calls++;
            return (uint)(Value * 4294967295.0f);
        }

        public float NextFloat()
        {
            Calls++;
            return Value;
        }

        public int NextInt(int maxExclusive)
        {
            Calls++;
            if (maxExclusive <= 0) return 0;
            return (int)(Value * maxExclusive);
        }

        public int Range(int minInclusive, int maxExclusive)
        {
            Calls++;
            if (maxExclusive <= minInclusive) return minInclusive;
            return minInclusive + (int)(Value * (maxExclusive - minInclusive));
        }

        public float Range(float min, float max)
        {
            Calls++;
            return min + Value * (max - min);
        }

        public bool Chance(float probability)
        {
            Calls++;
            return Value < probability;
        }

        public void Shuffle<T>(IList<T> list)
        {
            Calls++;
        }

        public IRngService GetStream(RngStream stream, int? salt = null) => this;
    }

    /// <summary>
    /// An RNG that returns a scripted sequence of draws and then a fallback value. Needed where a test
    /// must make the <b>nth</b> draw differ from the first — the only way to reach a branch that is
    /// guarded by one value and reported by another, such as <c>chest</c>'s <c>replic</c> gate, which is
    /// evaluated after eight chance gates.
    /// </summary>
    public sealed class ScriptedRng : IRngService
    {
        private readonly Queue<float> _script = new Queue<float>();

        /// <summary>Returned once the script is exhausted.</summary>
        public float Fallback = 0.99f;

        public int Calls;

        public ScriptedRng Push(params float[] values)
        {
            if (values != null)
            {
                foreach (float v in values) _script.Enqueue(v);
            }
            return this;
        }

        /// <summary>Push <paramref name="count"/> copies of one value.</summary>
        public ScriptedRng PushMany(int count, float value)
        {
            for (int i = 0; i < count; i++) _script.Enqueue(value);
            return this;
        }

        private float Draw()
        {
            Calls++;
            return _script.Count > 0 ? _script.Dequeue() : Fallback;
        }

        public uint NextUInt() => (uint)(Draw() * 4294967295.0f);
        public float NextFloat() => Draw();
        public int NextInt(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(Draw() * maxExclusive);
        public int Range(int minInclusive, int maxExclusive)
            => maxExclusive <= minInclusive ? minInclusive : minInclusive + (int)(Draw() * (maxExclusive - minInclusive));
        public float Range(float min, float max) => min + Draw() * (max - min);
        public bool Chance(float probability) => Draw() < probability;
        public void Shuffle<T>(IList<T> list) { }
        public IRngService GetStream(RngStream stream, int? salt = null) => this;
    }

    /// <summary>
    /// A pool provider that answers <b>every</b> tip with one entry. Used to make every pool lookup in a
    /// table succeed, so a test can assert what a key <i>does</i> rather than what it fails to resolve.
    /// </summary>
    public sealed class UniversalPools : ILootPoolProvider
    {
        private readonly List<LootPoolEntry> _one;

        public UniversalPools(string id = "any_item", float chance = 1f)
        {
            _one = new List<LootPoolEntry> { new LootPoolEntry(id, chance) };
        }

        public IReadOnlyList<LootPoolEntry> GetPool(string tip)
            => tip == null ? null : (IReadOnlyList<LootPoolEntry>)_one;
    }

    /// <summary>An <see cref="ILootPoolProvider"/> a test fills by hand.</summary>
    public sealed class FakeLootPools : ILootPoolProvider
    {
        private readonly Dictionary<string, List<LootPoolEntry>> _pools =
            new Dictionary<string, List<LootPoolEntry>>(StringComparer.Ordinal);

        public FakeLootPools Add(string tip, params LootPoolEntry[] entries)
        {
            if (!_pools.TryGetValue(tip, out var list))
            {
                list = new List<LootPoolEntry>();
                _pools[tip] = list;
            }
            if (entries != null) list.AddRange(entries);
            return this;
        }

        /// <summary>Add one plain entry — an id with no stage/level/worth filter of its own.</summary>
        public FakeLootPools Add(string tip, string id, float chance = 1f)
        {
            return Add(tip, new LootPoolEntry(id, chance));
        }

        public IReadOnlyList<LootPoolEntry> GetPool(string tip)
        {
            if (tip == null) return null;
            return _pools.TryGetValue(tip, out var list) ? (IReadOnlyList<LootPoolEntry>)list : null;
        }
    }

    /// <summary>A catalog that knows any id it is asked about, with no stack size and no limit.</summary>
    public sealed class StubCatalog : ILootItemCatalog
    {
        private readonly Dictionary<string, LootItemRow> _overrides =
            new Dictionary<string, LootItemRow>(StringComparer.Ordinal);

        /// <summary>Ids that are NOT known — used to prove a roll survives a missing row.</summary>
        public readonly HashSet<string> Unknown = new HashSet<string>(StringComparer.Ordinal);

        public StubCatalog With(LootItemRow row)
        {
            _overrides[row.Id] = row;
            return this;
        }

        public LootItemRow? Find(string tip, string id)
        {
            if (id == null || Unknown.Contains(id)) return null;
            if (_overrides.TryGetValue(id, out var row)) return row;
            return new LootItemRow(id, tip);
        }

        public LootItemRow? FindByAnyId(string id, out string resolvedTip)
        {
            resolvedTip = LootTips.Item;
            if (id == null || Unknown.Contains(id)) return null;
            if (_overrides.TryGetValue(id, out var row))
            {
                resolvedTip = string.IsNullOrEmpty(row.Tip) ? LootTips.Item : row.Tip;
                return row;
            }
            return new LootItemRow(id, LootTips.Item);
        }
    }

    /// <summary>The <c>World.w.pers</c> perk flags, all off by default.</summary>
    public sealed class FakePers : ILootPersFlags
    {
        public bool Freel;
        public bool Barahlo;
        public float CapsMult = 1f;
        public float BitsMult = 1f;
        public float DifCapsMult = 1f;
        public float DropTre;

        bool ILootPersFlags.Freel => Freel;
        bool ILootPersFlags.Barahlo => Barahlo;
        float ILootPersFlags.CapsMult => CapsMult;
        float ILootPersFlags.BitsMult => BitsMult;
        float ILootPersFlags.DifCapsMult => DifCapsMult;
        float ILootPersFlags.DropTre => DropTre;
    }

    /// <summary>The per-run <c>@limit</c> counters.</summary>
    public sealed class FakeLimits : ILootLimitStore
    {
        public readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);
        public int GetCalls;

        public int GetLimit(string key)
        {
            GetCalls++;
            return key != null && Counts.TryGetValue(key, out int v) ? v : 0;
        }

        public void AddLimit(string key, int delta)
        {
            if (key == null) return;
            Counts[key] = GetLimit(key) + delta;
        }
    }

    /// <summary>The room/level facts the tables branch on.</summary>
    public sealed class FakeEnv : ILootEnvironment
    {
        public int Biom;
        public string ItemsTip = string.Empty;
        public string ActId = string.Empty;
        public bool LandRnd;
        public bool HasProb;

        /// <summary>Weapons the player is treated as already owning at variant &gt; 0.</summary>
        public readonly HashSet<string> OwnedUpgraded = new HashSet<string>(StringComparer.Ordinal);

        int ILootEnvironment.Biom => Biom;
        string ILootEnvironment.ItemsTip => ItemsTip;
        string ILootEnvironment.ActId => ActId;
        bool ILootEnvironment.LandRnd => LandRnd;
        bool ILootEnvironment.HasProb => HasProb;
        bool ILootEnvironment.LacksWeapon(string weaponId) => !OwnedUpgraded.Contains(weaponId);
    }

    /// <summary>Records the two non-item side effects instead of performing them.</summary>
    public sealed class FakeSideEffects : ILootSideEffects
    {
        public readonly List<string> Units = new List<string>();
        public readonly List<string> ReplicStates = new List<string>();
        public readonly List<float> UnitXs = new List<float>();
        public readonly List<float> UnitYs = new List<float>();

        public void CreateUnit(string unitId, float x, float y)
        {
            Units.Add(unitId);
            UnitXs.Add(x);
            UnitYs.Add(y);
        }

        public void Replic(string state) => ReplicStates.Add(state);
    }

    /// <summary>Builds a context with sensible, fully-injected defaults so each test states only what it
    /// cares about.</summary>
    public static class LootFixture
    {
        public static LootContext Context(IRngService rng, ILootPoolProvider pools,
                                          ILootItemCatalog catalog = null, FakeSideEffects effects = null,
                                          FakePers pers = null, FakeEnv env = null,
                                          FakeLimits limits = null)
        {
            return new LootContext
            {
                Rng = rng,
                Pools = pools,
                Catalog = catalog ?? new StubCatalog(),
                SideEffects = effects,
                Pers = pers,
                Env = env,
                Limits = limits,
                LocDifLevel = 0,
                WeaponLevel = 1f,
                GameStage = 0,
                AllDif = 50f,
                LootLimit = 3,
            };
        }

        /// <summary>A pool that satisfies every tip <c>chest</c> draws from, one entry each — so each
        /// entry's observed frequency is its authored chance and nothing else.</summary>
        public static FakeLootPools ChestPools()
        {
            return new FakeLootPools()
                .Add("weapon", new LootPoolEntry("wpn3", 1f, null, null, 3f))
                .Add("compa", "compa_x", 1f)
                .Add("book", "book_x", 1f)
                .Add("a", "ammo_x", 1f)
                .Add("scheme", "scheme_x", 1f);
        }
    }
}
