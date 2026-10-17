using System;
using PFE.Core.Rng;
using UnityEngine;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// The one place the loot system's shared state is built and held: the pool set
    /// (<see cref="LootPoolSet"/>, AS3 <c>LootGen.arr</c>) and the item catalog, both derived from the
    /// <c>AllData</c> rows, plus the <c>Loot</c> RNG stream.
    ///
    /// <para><b>Why a static holder rather than a DI registration.</b> The container a container-prop
    /// presenter is attached to (<see cref="PFE.Systems.Map.Rendering.RoomObjectVisualManager"/>) is not
    /// resolved through VContainer — it is constructed by the room streaming code — so there is no
    /// injection point. This mirrors <see cref="PFE.Systems.Map.RoomPopulator.GetSpawnRng"/> and
    /// <c>CharacterStats.CombatRng</c>, which solve the same problem the same way: a lazily-built static
    /// with a settable override for tests and for a boot-time wiring pass.</para>
    ///
    /// <para><b>The data source is a TextAsset, and it is the oracle's own file.</b> Building the pools
    /// from the imported <c>ItemDefinition</c>/<c>WeaponDefinition</c> assets is not possible today:
    /// only <c>legacyTip</c>/<c>tip2</c> are imported, so every item would get weight 1, and the weapon
    /// <c>&lt;com&gt;</c> metadata is imported by nothing at all, so <c>arr["weapon"]</c> would be empty.
    /// Both are silent divergences. So the runtime reads the same <c>&lt;all&gt;</c> block the tables
    /// were transcribed from — shipped at <c>Resources/Loot/AllData</c> — through the same reader the
    /// offline fixtures use. <see cref="LootDataXml"/> documents the parse; a guard test pins the shipped
    /// copy byte-identical to the project's root <c>AllData.as</c> so the two cannot drift.</para>
    ///
    /// <para><b>Failure is loud.</b> If the asset is missing or malformed, <see cref="IsReady"/> is
    /// false and <see cref="LoadError"/> says why; a caller must not treat an unready runtime as "an
    /// empty chest".</para>
    /// </summary>
    public static class LootRuntime
    {
        /// <summary>Where the mirrored <c>AllData.as</c> lives, under any <c>Resources</c> folder.</summary>
        public const string AllDataResourcePath = "Loot/AllData";

        private static bool _attempted;
        private static bool _ready;
        private static string _error;
        private static LootPoolSet _pools;
        private static ILootItemCatalog _catalog;

        private static IRngService _lootRng;

        /// <summary>
        /// The loot RNG stream. Settable so a boot pass (or a test) can push the seeded instance;
        /// otherwise a fresh <c>PcgRngService</c> <see cref="RngStream.Loot"/> stream, exactly as
        /// <c>RoomPopulator.GetSpawnRng</c> does for spawns.
        /// </summary>
        public static IRngService Rng
        {
            get => _lootRng ??= new PcgRngService().GetStream(RngStream.Loot);
            set => _lootRng = value;
        }

        // ── The CURRENT land's two loot fields (AS3 `World.w.land.*`) ────────────────────────────────
        //
        // `Land`'s constructor writes both once per land (Land.as:139-140) and LootGen reads them back
        // through the global `World.w.land` (:131, :264). They are land-scoped, not room-scoped, so they
        // are published here rather than copied onto every RoomInstance; the port streams one land at a
        // time, so "the current land" is well defined. Written by `WorldBuilder.BuildLand`.

        /// <summary>
        /// AS3 <c>Land.gameStage</c> = the land's authored <c>@stage</c> (<c>Land.as:140</c>,
        /// <c>LandAct.as:123-125</c>). The gate a pool entry's own <c>st</c> is tested against
        /// (<c>LootGen.as:138</c>: <c>st == null || st &lt;= gameStage</c>).
        /// </summary>
        public static int LandGameStage { get; set; }

        /// <summary>
        /// AS3 <c>Land.lootLimit</c> = <c>param3 + 3</c> (<c>Land.as:139</c>) — the per-run cap an
        /// <c>@limit</c> item's counter is compared against, scaled by the row's own <c>mlim</c>
        /// (<c>LootGen.as:264-267</c>). <b>The raw run difficulty, not the floored <c>landDifLevel</c></b>;
        /// see <c>DifficultyRules.LootLimit</c>.
        /// </summary>
        public static int LandLootLimit { get; set; }

        /// <summary>
        /// AS3 <c>loc.land.act.id</c> — the current land's id, the discriminator the <c>term</c> table
        /// branches on (<c>LootGen.as:652</c>: <c>== "minst"</c> swaps the data item). Land-scoped for the
        /// same reason as <see cref="LandGameStage"/>.
        /// </summary>
        public static string LandActId { get; set; } = string.Empty;

        /// <summary>
        /// AS3 <c>World.w.land.rnd</c> — whether the current land is procedural. Half of the <c>safe</c>
        /// table's bloat-ambush guard (<c>LootGen.as:698</c>: <c>land.rnd &amp;&amp; param1.prob == null</c>).
        /// Land-scoped, so it is published here alongside the other two.
        /// </summary>
        public static bool LandRnd { get; set; }

        private static ILootLimitStore _limits;

        /// <summary>
        /// The per-run <c>@limit</c> counter store — AS3 <c>Game.limits</c>. Created on first use so a
        /// caller that never touches a limited row pays nothing, and settable so a save load (or a test)
        /// can install a store with a known state.
        ///
        /// <para><b>The default store has no trigger table, and that is deliberate rather than a missing
        /// wiring.</b> <c>Game.getLimit</c> seeds a counter from the story trigger of the same name
        /// (<c>Game.as:687-691</c>), and <see cref="LootLimitStore"/> implements that arm — but the
        /// shipped content never exercises it: the oracle declares ten <c>@limit</c> keys
        /// (<c>1, alc7, book, book_cm, chip, essence, lp_item, potHP, potMP, sphera</c>) and <b>not one of
        /// them is ever used as a trigger name</b> (they occur only as item rows in <c>AllData.as</c> and
        /// as quest / prob / reward ids in <c>GameData.as</c>; none of the 126 distinct <c>targ=</c>
        /// values in the scripts is a limit key). So the seed arm cannot change a single roll today, and
        /// wiring it would add a Loot→Campaign dependency for no behaviour. <see cref="WireLimitsToCampaign"/>
        /// is the seam to call if that ever changes — e.g. a mod that gates a unique item with a trigger.</para>
        /// </summary>
        public static ILootLimitStore Limits
        {
            get => _limits ??= new LootLimitStore();
            set => _limits = value;
        }

        /// <summary>
        /// Install a <see cref="LootLimitStore"/> whose seed arm reads the story-trigger table — the
        /// production wiring for <see cref="Limits"/>. Call once, after the campaign exists.
        ///
        /// <para><b>Not called by shipping code, on purpose.</b> See <see cref="Limits"/>: no shipped
        /// <c>@limit</c> key is also a trigger name, so enabling this arm changes nothing and would
        /// couple the loot runtime to the campaign for no gain. It is kept as the one supported way to
        /// turn the arm on (and is exercised by the offline fixture), rather than leaving a reader to
        /// guess how the oracle's seed arm is meant to be reached.</para>
        /// </summary>
        /// <param name="readTrigger">AS3 <c>Game.triggers[name]</c> as an int.</param>
        /// <param name="writeTrigger">AS3 <c>setTrigger(name, value)</c>.</param>
        public static void WireLimitsToCampaign(Func<string, int> readTrigger, Action<string, int> writeTrigger)
        {
            _limits = new LootLimitStore(readTrigger, writeTrigger);
        }

        /// <summary>True once the pool set and catalog are built.</summary>
        public static bool IsReady => _ready;

        /// <summary>Why the runtime is not ready, or <c>null</c>. Never null when <see cref="IsReady"/> is false.</summary>
        public static string LoadError => _error;

        /// <summary>The pool set, or <c>null</c> when not ready.</summary>
        public static LootPoolSet Pools => _pools;

        /// <summary>The catalog, or <c>null</c> when not ready.</summary>
        public static ILootItemCatalog Catalog => _catalog;

        /// <summary>
        /// Build the runtime on first use. Returns true when it is ready. The result is memoised — a
        /// missing asset is reported once, not on every container the player opens.
        /// </summary>
        public static bool TryEnsureLoaded(out string error)
        {
            if (_attempted)
            {
                error = _error;
                return _ready;
            }

            _attempted = true;

            TextAsset asset = Resources.Load<TextAsset>(AllDataResourcePath);
            if (asset == null)
            {
                _error = $"no TextAsset at Resources/{AllDataResourcePath}; the loot pools cannot be built " +
                         "and every container would report empty. Ship AllData.as as a .txt there.";
                error = _error;
                Debug.LogError("[LootRuntime] " + _error);
                return false;
            }

            LootDataXml.Outcome outcome = LootDataXml.TryParse(asset.text, out LootData data, out string parseError);
            if (outcome != LootDataXml.Outcome.Ok)
            {
                _error = $"Resources/{AllDataResourcePath} could not be parsed ({outcome}): {parseError}";
                error = _error;
                Debug.LogError("[LootRuntime] " + _error);
                return false;
            }

            _pools = LootPoolSet.Build(data.Rows);
            _catalog = new LootItemCatalogXml(data);
            _ready = true;
            _error = null;
            error = null;
            return true;
        }

        /// <summary>
        /// A <see cref="LootContext"/> wired with the shared pools, catalog, RNG and the current land's
        /// two loot fields. The caller fills in the room-specific inputs (the container's own difficulty,
        /// the perks, the environment, the side effects). Returns <c>null</c> when the runtime is not
        /// ready, having logged why.
        ///
        /// <para><b><c>GameStage</c>, <c>LootLimit</c> and <c>Limits</c> are filled here</b> because they
        /// are land/run scoped, not room scoped — AS3 reads all three through globals
        /// (<c>World.w.land.gameStage</c>, <c>World.w.land.lootLimit</c>, <c>Game.limits</c>), so a sink
        /// that forgot one would roll with <c>gameStage 0</c> (dropping every staged row) and a
        /// <c>lootLimit 0</c> cap (refusing every limited row). Both are silent failures — the container
        /// just has less in it.</para>
        /// </summary>
        public static LootContext CreateContext(string tableKey)
        {
            if (!TryEnsureLoaded(out _)) return null;

            return new LootContext
            {
                Rng = Rng,
                Pools = _pools,
                Catalog = _catalog,
                TableKey = tableKey ?? string.Empty,
                GameStage = LandGameStage,
                LootLimit = LandLootLimit,
                Limits = Limits,
            };
        }

        /// <summary>Drop the memoised state. Test seam; nothing in shipping code calls it.</summary>
        public static void ResetForTests()
        {
            _attempted = false;
            _ready = false;
            _error = null;
            _pools = null;
            _catalog = null;
            _lootRng = null;
            LandGameStage = 0;
            LandLootLimit = 0;
            LandActId = string.Empty;
            LandRnd = false;
            _limits = null;
        }
    }
}
