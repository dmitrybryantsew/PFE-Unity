using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Resolves a <see cref="UnitDefinition"/> by unit id.
    ///
    /// <para><b>Why this seam exists.</b> <c>ContentRegistry.Get&lt;T&gt;</c> is an instance method on a
    /// DI-owned <c>GameDatabase</c>, but the consumers here are static (<c>RoomPopulator.PopulateRoom</c>)
    /// or build-then-inject (<c>RoomUnitSpawner</c>), so neither can reach it. Making the lookup
    /// injectable also keeps the health source testable — the alternative was hard-coding another guess
    /// table, which is the defect this replaces: <c>CalculateUnitHealth</c> had no <c>"training"</c> case,
    /// so the dummy reported 50 hp against a definition that says 500, and nothing said so.</para>
    /// </summary>
    public interface IUnitDefinitionProvider
    {
        bool TryGetUnit(string unitId, out UnitDefinition definition);
    }

    /// <summary>
    /// <c>Resources</c>-backed lookup, following the pattern <c>RoomObjectVisualManager</c> already uses
    /// for <c>MapObjects/Definitions</c>: load the folder once, index by id, keep the index. The unit
    /// folder holds ~296 assets, so the load is deferred to first use.
    /// </summary>
    public sealed class ResourcesUnitDefinitionProvider : IUnitDefinitionProvider
    {
        public const string UnitsResourcesRoot = "Units";

        static ResourcesUnitDefinitionProvider s_shared;

        /// <summary>
        /// The production instance. Lazy so that a caller which injects its own provider (tests, an
        /// editor tool) never pays for the <c>Resources</c> scan.
        /// </summary>
        public static ResourcesUnitDefinitionProvider Shared =>
            s_shared ??= new ResourcesUnitDefinitionProvider();

        Dictionary<string, UnitDefinition> _byId;

        public bool TryGetUnit(string unitId, out UnitDefinition definition)
        {
            definition = null;
            if (string.IsNullOrWhiteSpace(unitId))
            {
                return false;
            }

            EnsureLoaded();
            return _byId.TryGetValue(unitId, out definition);
        }

        /// <summary>Drop the index so the next lookup re-reads <c>Resources</c>. For tests and re-imports.</summary>
        public void ClearCache()
        {
            _byId = null;
        }

        void EnsureLoaded()
        {
            if (_byId != null)
            {
                return;
            }

            _byId = new Dictionary<string, UnitDefinition>(StringComparer.OrdinalIgnoreCase);

            UnitDefinition[] loaded = Resources.LoadAll<UnitDefinition>(UnitsResourcesRoot);
            for (int i = 0; i < loaded.Length; i++)
            {
                UnitDefinition unit = loaded[i];
                if (unit == null || string.IsNullOrWhiteSpace(unit.id))
                {
                    continue;
                }

                // Name what we dropped: a duplicate id means two assets claim the same unit, and
                // silently keeping whichever LoadAll returned first is how a wrong stat block ships.
                if (_byId.TryGetValue(unit.id, out UnitDefinition incumbent) && incumbent != unit)
                {
                    Debug.LogWarning(
                        $"[ResourcesUnitDefinitionProvider] Two UnitDefinitions claim id '{unit.id}': " +
                        $"keeping '{incumbent.name}', dropping '{unit.name}'. A duplicate unit id is a " +
                        $"data bug.");
                    continue;
                }

                _byId[unit.id] = unit;
            }
        }
    }
}
