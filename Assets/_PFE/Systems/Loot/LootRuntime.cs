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
        /// A <see cref="LootContext"/> wired with the shared pools, catalog and RNG — the caller fills in
        /// the room-specific inputs (difficulty, perks, environment, side effects). Returns <c>null</c>
        /// when the runtime is not ready, having logged why.
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
        }
    }
}
