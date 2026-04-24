using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Per-weapon visual data imported from the original pfe SWF.
    /// Holds all frame sprites and animation state ranges derived from Flash FrameLabel tags.
    ///
    /// Frame indexing matches Flash 1-based numbering stored 0-based here:
    ///   unity index 0  =  flash frame 1  (idle)
    ///   unity index N  =  flash frame N+1
    ///
    /// At runtime, WeaponPresenter drives SpriteRenderer.sprite directly from these arrays —
    /// no Animator or AnimationClip is involved (mirrors the original gotoAndStop/gotoAndPlay logic).
    /// </summary>
    [CreateAssetMenu(fileName = "WeaponVis_new", menuName = "PFE/Weapon Visual Definition")]
    public class WeaponVisualDefinition : ScriptableObject
    {
        // ── Identification ────────────────────────────────────────────────────
        [Header("Identification")]
        [Tooltip("Flash symbol class name (e.g. visp10mm). Used to look this asset up from WeaponDefinition.")]
        public string symbolName;

        [Tooltip("Back-reference to WeaponDefinition.weaponId that owns this visual.")]
        public string weaponId;

        [Tooltip("Original Flash SWF symbol ID (DefineSprite tag id). Informational.")]
        public int sourceSymbolId;

        // ── Sprite frames ─────────────────────────────────────────────────────
        [Header("Sprite Frames")]
        [Tooltip("All rendered frames in Flash timeline order (index 0 = flash frame 1). " +
                 "Single-frame weapons have length 1.")]
        public Sprite[] frames;

        [Tooltip("Pixels per unit used when these sprites were imported. Must match runtime PPU setting.")]
        public int pixelsPerUnit = 100;

        // ── Animation state ranges ────────────────────────────────────────────
        [Header("Animation — Idle")]
        [Tooltip("frames[] index to show when the weapon is held but not firing or reloading.")]
        public int idleFrame = 0;

        [Header("Animation — Shoot")]
        [Tooltip("frames[] index where the 'shoot' Flash label begins. -1 = not animated.")]
        public int shootFrameStart = -1;
        [Tooltip("Number of frames in the shoot animation.")]
        public int shootFrameCount = 0;

        [Header("Animation — Reload")]
        [Tooltip("frames[] index where the 'reload' Flash label begins. -1 = not animated.")]
        public int reloadFrameStart = -1;
        [Tooltip("Number of frames in the reload animation.")]
        public int reloadFrameCount = 0;

        [Header("Animation — Charge (Prep)")]
        [Tooltip("frames[] index of the first charge-up frame. -1 = weapon has no charge.")]
        public int prepFrameStart = -1;
        [Tooltip("Number of charge frames. t_prep (1..prepFrameCount) maps to frames[prepFrameStart + t_prep - 1].")]
        public int prepFrameCount = 0;
        [Tooltip("frames[] index of the 'ready' label (fully charged). -1 = not used.")]
        public int readyFrame = -1;

        // ── Muzzle ────────────────────────────────────────────────────────────
        [Header("Muzzle")]
        [Tooltip("Position of the 'emit' child point in sprite-local units (same coord system as the Sprite). " +
                 "This is the bullet spawn / muzzle-flash origin. (0,0) = sprite registration point (pivot).")]
        public Vector2 muzzleLocalOffset = Vector2.zero;

        // ── Effects ───────────────────────────────────────────────────────────
        [Header("Effects")]
        [Tooltip("Muzzle flash effect id (vis.@flare). e.g. 'spark', 'plasma', 'laser'. Empty = no flash.")]
        public string muzzleFlareId;

        [Tooltip("Eject a shell casing particle on fire (vis.@shell).")]
        public bool hasShellEject;

        [Tooltip("Light-flash radius emitted when firing (vis.@shine). 0 = no shine. 500 = default for pistols.")]
        public int shineRadius;

        // ── Helpers ───────────────────────────────────────────────────────────
        public bool HasFrames   => frames != null && frames.Length > 0;
        public bool IsAnimated  => frames != null && frames.Length > 1;
        public Sprite IdleSprite => HasFrames ? frames[Mathf.Clamp(idleFrame, 0, frames.Length - 1)] : null;

        /// <summary>Returns the sprite for a given 1-based Flash frame number, clamped to valid range.</summary>
        public Sprite GetFlashFrame(int flashFrame)
        {
            if (!HasFrames) return null;
            int idx = Mathf.Clamp(flashFrame - 1, 0, frames.Length - 1);
            return frames[idx];
        }

        /// <summary>Returns the sprite at an arbitrary shoot animation frame offset (0-based within clip).</summary>
        public Sprite GetShootFrame(int offsetInClip)
        {
            if (!HasFrames || shootFrameStart < 0 || shootFrameCount <= 0) return IdleSprite;
            int idx = Mathf.Clamp(shootFrameStart + offsetInClip, 0, frames.Length - 1);
            return frames[idx];
        }

        /// <summary>Returns the sprite at an arbitrary reload animation frame offset (0-based within clip).</summary>
        public Sprite GetReloadFrame(int offsetInClip)
        {
            if (!HasFrames || reloadFrameStart < 0 || reloadFrameCount <= 0) return IdleSprite;
            int idx = Mathf.Clamp(reloadFrameStart + offsetInClip, 0, frames.Length - 1);
            return frames[idx];
        }

        /// <summary>Returns the sprite for a given t_prep value (1-based, matching Flash gotoAndStop(t_prep)).</summary>
        public Sprite GetPrepFrame(int tPrep)
        {
            if (!HasFrames || prepFrameStart < 0 || prepFrameCount <= 0) return IdleSprite;
            int idx = Mathf.Clamp(prepFrameStart + tPrep - 1, 0, frames.Length - 1);
            return frames[idx];
        }

        /// <summary>Returns the "ready" sprite (fully charged state).</summary>
        public Sprite GetReadySprite()
        {
            if (!HasFrames || readyFrame < 0) return IdleSprite;
            return frames[Mathf.Clamp(readyFrame, 0, frames.Length - 1)];
        }
    }
}
