using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Sim.State;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Net
{
    /// <summary>
    /// Wire-hash → <see cref="WeaponDefinition"/> index for the remote avatar's held weapon.
    ///
    /// <para><b>Why a second index and not the string-keyed provider.</b> The snapshot carries
    /// <c>SimUnitState.WeaponIdHash</c> — <c>LootItemKey(id, true)</c>, not the raw id — and the
    /// client's display body must resolve it without a string side-table. The source set is exactly
    /// the one <see cref="ResourcesWeaponDefinitionProvider"/> scans (the "Weapons" Resources
    /// folder), so the two indexes cannot disagree about what exists; they only key it differently.</para>
    ///
    /// <para><b>Lazy, and tolerant of an empty folder.</b> A test scene with no weapon assets gets an
    /// empty index: every remote weapon then resolves to null and the view hides the renderer, which
    /// is the safe reading of "I cannot draw what I cannot name". Duplicate ids collapse to the same
    /// hash; the first asset wins, matching the provider's keep-first rule.</para>
    /// </summary>
    public static class PfeRemoteWeaponCatalog
    {
        private static Dictionary<ulong, WeaponDefinition> _byHash;

        /// <summary>How many definitions the index holds. A play-test readback: 0 on a machine that
        /// should have ~213 means the Resources scan ran in the wrong scene.</summary>
        public static int Count
        {
            get
            {
                EnsureLoaded();
                return _byHash.Count;
            }
        }

        /// <summary>The definition for <paramref name="weaponIdHash"/>, or null when the hash is 0
        /// (unarmed) or names a weapon this build does not have.</summary>
        public static WeaponDefinition Resolve(ulong weaponIdHash)
        {
            if (weaponIdHash == 0UL) return null;
            EnsureLoaded();
            WeaponDefinition definition;
            return _byHash.TryGetValue(weaponIdHash, out definition) ? definition : null;
        }

        /// <summary>The definition for a bare content id — the key space a
        /// <c>SimProjectileState.DefinitionId</c> resolves to (<c>ContentInternContext.Weapons
        /// .GetContentId</c> first). The projectile display consumes this; the held-weapon view keeps
        /// using the hash index, and both are built from the same scan so they cannot disagree about
        /// what exists.</summary>
        public static WeaponDefinition ResolveById(string weaponId)
        {
            if (string.IsNullOrEmpty(weaponId)) return null;
            EnsureLoaded();
            WeaponDefinition definition;
            return _byId.TryGetValue(weaponId, out definition) ? definition : null;
        }

        private static Dictionary<string, WeaponDefinition> _byId;

        private static void EnsureLoaded()
        {
            if (_byHash != null) return;

            _byHash = new Dictionary<ulong, WeaponDefinition>();
            _byId = new Dictionary<string, WeaponDefinition>(StringComparer.Ordinal);
            WeaponDefinition[] loaded =
                Resources.LoadAll<WeaponDefinition>(ResourcesWeaponDefinitionProvider.WeaponsResourcesRoot);
            for (int i = 0; i < loaded.Length; i++)
            {
                WeaponDefinition weapon = loaded[i];
                if (weapon == null || string.IsNullOrWhiteSpace(weapon.weaponId)) continue;
                _byHash[SimSnapshotProjectionRules.LootItemKey(weapon.weaponId, isWeapon: true)] = weapon;
                // Keep-first, matching the provider's rule: duplicate ids collapse instead of
                // depending on Resources enumeration order.
                if (!_byId.ContainsKey(weapon.weaponId)) _byId.Add(weapon.weaponId, weapon);
            }
        }
    }
}
