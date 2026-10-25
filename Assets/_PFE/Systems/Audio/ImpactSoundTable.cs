using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Audio
{
    /// <summary>
    /// Maps SurfaceMaterial (and optionally DamageType) to impact sound IDs.
    ///
    /// Parity with AS3 Bullet.sound():
    ///   - Each surface has a default sound + volume.
    ///   - The Flesh surface has per-DamageType overrides (bullet/blade/everything else).
    ///   - ImpactSoundResolver queries this table; the table is never called at import time.
    ///
    /// Created/defaulted by the SoundImporter editor tool (PFE / Import / Import Sounds).
    /// Assign to GameLifetimeScope._impactSoundTable in the scene.
    /// </summary>
    [CreateAssetMenu(fileName = "ImpactSoundTable", menuName = "PFE/Audio/Impact Sound Table")]
    public sealed class ImpactSoundTable : ScriptableObject
    {
        // ── Per-surface entries ────────────────────────────────────────────────

        [Serializable]
        public struct SurfaceEntry
        {
            [Tooltip("Which surface this entry covers.")]
            public SurfaceMaterial material;

            [Tooltip("Sound catalog ID to play. Must match an entry in SoundCatalog.")]
            public string soundId;

            [Range(0f, 1f)]
            [Tooltip("Volume scale passed to ISoundService.Play.")]
            public float volume;
        }

        // ── Per-damage-type overrides (Flesh only) ─────────────────────────────

        [Serializable]
        public struct FleshOverride
        {
            [Tooltip("Damage type that triggers this override.")]
            public DamageType damageType;

            [Tooltip("Sound catalog ID to play instead of the default flesh sound.")]
            public string soundId;

            [Range(0f, 1f)]
            public float volume;
        }

        // ── Serialized data ────────────────────────────────────────────────────

        [Header("Surface Sounds")]
        [Tooltip("One entry per surface material. Materials not listed produce no surface sound.")]
        public SurfaceEntry[] surfaceSounds = Array.Empty<SurfaceEntry>();

        [Header("Flesh Overrides (by Damage Type)")]
        [Tooltip("When the surface is Flesh, these damage-type-specific sounds override the default flesh sound.")]
        public FleshOverride[] fleshOverrides = Array.Empty<FleshOverride>();

        // ── Runtime cache ──────────────────────────────────────────────────────

        private Dictionary<SurfaceMaterial, SurfaceEntry> _surfaceCache;
        private Dictionary<DamageType, FleshOverride>     _fleshCache;
        private bool _cacheBuilt;

        private void BuildCache()
        {
            _surfaceCache = new Dictionary<SurfaceMaterial, SurfaceEntry>(surfaceSounds.Length);
            foreach (var e in surfaceSounds)
                _surfaceCache[e.material] = e;

            _fleshCache = new Dictionary<DamageType, FleshOverride>(fleshOverrides.Length);
            foreach (var o in fleshOverrides)
                _fleshCache[o.damageType] = o;

            _cacheBuilt = true;
        }

        /// <summary>
        /// Look up the impact sound for the given surface and damage type.
        /// Returns false if no sound is configured for this combination.
        /// </summary>
        /// <remarks>
        /// <para><b>Two of the shipped Flesh overrides disagree with the oracle, and it is a data
        /// defect, not a modelling one.</b> AS3 does not have a table here: <c>Bullet.sound()</c>
        /// hardcodes a three-way on <c>tipDamage</c> (<c>weapon/Bullet.as:638-652</c>) —
        /// <c>D_BUL → hit_bullet</c> at volume 0.8, <c>D_BLADE → hit_blade</c> at 0.8, and
        /// <b>everything else → hit_flesh at 0.5</b>. The <c>else</c> is the whole rule for the
        /// remaining 18 damage types.</para>
        ///
        /// <para><c>Assets/_PFE/Data/Resources/ImpactSoundTable.asset</c> instead carries
        /// <c>PhysicalMelee (2) → hit_blade @0.8</c> and <c>Fang (14) → hit_blade @0.8</c>, which the
        /// importer's own defaults created (<c>SoundImporter.EnsureImpactSoundTable</c>). Measured
        /// against <c>AllData.as</c>, <b>seven</b> melee rows author <c>&lt;char tipdam='2'&gt;</c> —
        /// <c>armature bat mont mace pipe hammer sledge</c> — plus the unarmed <c>punch</c> row. All
        /// eight play a <i>blade slash</i> on flesh where the oracle plays a thud, and at 0.8 instead
        /// of 0.5. (The other non-blade melee types — <c>edagger</c>/<c>mspear</c> 6, <c>tlance</c> 3,
        /// <c>elsword</c> 9, <c>lsword</c> 5, <c>kosa</c> 16 — have no override and correctly fall
        /// through to <c>hit_flesh</c>.)</para>
        ///
        /// <para><b>Recorded, not fixed here.</b> Correcting it means editing the two override rows in
        /// the shipped asset (and the importer's defaults, or a later re-import reintroduces them), and
        /// asset edits are the owner's. The code side is right: it asks the table, and the table is
        /// what says "blade". Before melee impact sound existed at all this path was silent, so the
        /// current state is closer to the oracle than it was, not further.</para>
        /// </remarks>
        public bool TryGetSound(SurfaceMaterial material, DamageType damageType,
                                out string soundId, out float volume)
        {
            if (!_cacheBuilt) BuildCache();

            // Flesh: check per-damage-type override first.
            if (material == SurfaceMaterial.Flesh && _fleshCache.TryGetValue(damageType, out var fo))
            {
                soundId = fo.soundId;
                volume  = fo.volume;
                return !string.IsNullOrEmpty(soundId);
            }

            if (_surfaceCache.TryGetValue(material, out var se))
            {
                soundId = se.soundId;
                volume  = se.volume;
                return !string.IsNullOrEmpty(soundId);
            }

            soundId = null;
            volume  = 0f;
            return false;
        }

        /// <summary>Call after editing entries in-editor to flush the runtime cache.</summary>
        public void InvalidateCache() => _cacheBuilt = false;

        private void OnValidate() => _cacheBuilt = false;
    }
}
