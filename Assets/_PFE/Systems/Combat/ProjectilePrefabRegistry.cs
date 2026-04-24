using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Maps each ProjectileArchetype to a single Projectile prefab.
    ///
    /// Create one instance:  Assets > Create > PFE > Projectile Prefab Registry
    /// Assign it in the GameLifetimeScope inspector field.
    ///
    /// Then for each archetype, drag the correct prefab:
    ///   Ballistic  → a simple kinematic round with a small SpriteRenderer
    ///   Laser      → a stretched-beam prefab (LineRenderer or SpriteRenderer, spring=2)
    ///   Plasma     → glowing orb prefab
    ///   Flame      → short-lived animated flame prefab
    ///   Explosive  → grenade/rocket prefab (Dynamic RB, AoE component)
    ///   Spark      → animated spark prefab
    ///   Spit       → spit/acid splash prefab
    ///   Homing     → guided missile prefab
    ///   Magic      → unicorn spell prefab (spawned from horn)
    ///
    /// All prefabs must have the <see cref="Projectile"/> component.
    /// </summary>
    [CreateAssetMenu(fileName = "ProjectilePrefabRegistry",
                     menuName  = "PFE/Projectile Prefab Registry")]
    public class ProjectilePrefabRegistry : ScriptableObject
    {
        [System.Serializable]
        public struct Entry
        {
            public ProjectileArchetype archetype;
            public Projectile          prefab;
        }

        [SerializeField]
        private List<Entry> _entries = new();

        private Dictionary<ProjectileArchetype, Projectile> _map;

        private void OnEnable() => BuildMap();

        private void BuildMap()
        {
            _map = new Dictionary<ProjectileArchetype, Projectile>(_entries.Count);
            foreach (var e in _entries)
                if (e.prefab != null)
                    _map[e.archetype] = e.prefab;
        }

        /// <summary>
        /// Returns the prefab for the given archetype, or null if not registered.
        /// </summary>
        public Projectile Get(ProjectileArchetype archetype)
        {
            if (_map == null) BuildMap();
            _map.TryGetValue(archetype, out var prefab);
            if (prefab == null)
                Debug.LogWarning($"[ProjectilePrefabRegistry] No prefab registered for archetype '{archetype}'. " +
                                 "Assign one in the ProjectilePrefabRegistry asset.");
            return prefab;
        }

        /// <summary>Returns true if a prefab is registered for this archetype.</summary>
        public bool Has(ProjectileArchetype archetype)
        {
            if (_map == null) BuildMap();
            return _map.ContainsKey(archetype) && _map[archetype] != null;
        }

        /// <summary>
        /// Replaces the registry entries. Intended for editor import tools.
        /// </summary>
        public void SetEntries(IEnumerable<Entry> entries)
        {
            _entries = entries != null ? entries.ToList() : new List<Entry>();
            BuildMap();
        }
    }
}
