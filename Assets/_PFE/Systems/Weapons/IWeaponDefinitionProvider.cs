using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Resolves a <see cref="WeaponDefinition"/> by weapon id.
    ///
    /// <para><b>Why this seam exists, and it is the exact analogue of
    /// <see cref="PFE.Entities.Units.IUnitDefinitionProvider"/>.</b> The consumer is an enemy built with
    /// <c>AddComponent</c> by <c>RoomUnitSpawner</c>, so nothing runs <c>[Inject]</c> on it and
    /// <c>GameDatabase.GetWeapon</c> — an instance method on a DI-owned singleton — is unreachable.
    /// <c>RoomUnitSpawner</c> already solves the same problem for units with
    /// <c>ResourcesUnitDefinitionProvider.Shared</c>; this is that answer for the weapon table, and it
    /// exists so a test can substitute a provider instead of hard-coding a lookup.</para>
    /// </summary>
    public interface IWeaponDefinitionProvider
    {
        bool TryGetWeapon(string weaponId, out WeaponDefinition definition);
    }

    /// <summary>
    /// <c>Resources</c>-backed lookup, following <c>ResourcesUnitDefinitionProvider</c> exactly: load
    /// the folder once, index by id, keep the index. The weapon folder holds ~213 assets, so the load is
    /// deferred to first use.
    ///
    /// <para><b>Divergence from <c>GameDatabase</c>, stated rather than hidden.</b> The mod-aware path is
    /// <c>GameDatabase.GetWeapon</c> (registry first, legacy dictionary second). It is not reachable from
    /// a spawned enemy, and the two do agree on built-in content — which is all an enemy holds today
    /// (the alicorn's <c>alilight</c>/<c>alipsy</c> are built-in). If an enemy ever needs a modded
    /// weapon, the fix is to thread the database down the <c>MapBridge → RoomVisualController →
    /// RoomUnitSpawner</c> chain and give this seam a second implementation, not to widen this one.</para>
    /// </summary>
    public sealed class ResourcesWeaponDefinitionProvider : IWeaponDefinitionProvider
    {
        public const string WeaponsResourcesRoot = "Weapons";

        static ResourcesWeaponDefinitionProvider s_shared;

        /// <summary>
        /// The production instance. Lazy so that a caller which injects its own provider (tests, an
        /// editor tool) never pays for the <c>Resources</c> scan.
        /// </summary>
        public static ResourcesWeaponDefinitionProvider Shared =>
            s_shared ??= new ResourcesWeaponDefinitionProvider();

        Dictionary<string, WeaponDefinition> _byId;

        public bool TryGetWeapon(string weaponId, out WeaponDefinition definition)
        {
            definition = null;
            if (string.IsNullOrWhiteSpace(weaponId))
            {
                return false;
            }

            EnsureLoaded();
            return _byId.TryGetValue(weaponId, out definition);
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

            _byId = new Dictionary<string, WeaponDefinition>(StringComparer.OrdinalIgnoreCase);

            WeaponDefinition[] loaded = Resources.LoadAll<WeaponDefinition>(WeaponsResourcesRoot);
            for (int i = 0; i < loaded.Length; i++)
            {
                WeaponDefinition weapon = loaded[i];
                if (weapon == null || string.IsNullOrWhiteSpace(weapon.weaponId))
                {
                    continue;
                }

                // Name what we dropped. A duplicate id means two assets claim the same weapon, and
                // keeping whichever LoadAll returned first is how a wrong ballistics row ships.
                if (_byId.TryGetValue(weapon.weaponId, out WeaponDefinition incumbent) && incumbent != weapon)
                {
                    Debug.LogWarning(
                        $"[ResourcesWeaponDefinitionProvider] Two WeaponDefinitions claim id " +
                        $"'{weapon.weaponId}': keeping '{incumbent.name}', dropping '{weapon.name}'. " +
                        $"A duplicate weapon id is a data bug.");
                    continue;
                }

                _byId[weapon.weaponId] = weapon;
            }
        }
    }
}
