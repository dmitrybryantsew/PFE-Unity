using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.UI.Common
{
    /// <summary>
    /// The cursor states the interface switches between. Mirrors the four <c>visCur*</c> symbols in the
    /// Oracle; the names are the port's own, because Flash exposes no enum to copy — the Oracle just
    /// swaps <c>mouseCursor</c> to a different symbol.
    /// </summary>
    public enum PfeCursorMode
    {
        /// <summary>No override — the OS arrow. Used while no gameplay cursor policy is active.</summary>
        Default,

        /// <summary><c>visCurArrow</c> (349, 32×32) — UI, PipBuck and dialogue.</summary>
        Pointer,

        /// <summary><c>visCurTarget</c> (348, 27×27) — real-time combat aiming.</summary>
        AimCrosshairs,

        /// <summary><c>visCurTarget1</c> (347, 27×27) — precision / sniper aiming.</summary>
        PrecisionCrosshairs,

        /// <summary><c>visCurTarget2</c> (346, 27×27) — magic targeting.</summary>
        MagicCrosshairs,
    }

    /// <summary>
    /// Owns the software cursor. One instance, injected wherever the cursor policy changes: opening the
    /// PipBuck switches to <see cref="PfeCursorMode.Pointer"/>, closing it restores the aim reticle, and
    /// entering S.A.T.S. or aiming a sniper switches the reticle variant.
    ///
    /// <para><b>Hotspots come from the textures, not from this class.</b> The values are set by the
    /// importer on each <c>TextureImporter</c> (<see cref="PFE.Editor.Importers.SWF.InterfaceAssetImporter"/>)
    /// and passed to <see cref="Cursor.SetCursor"/> here. They are deliberately not re-derived at
    /// runtime: the arrow's tip is its top-left corner <c>(0, 0)</c> and the 27×27 reticles are centred
    /// at <c>(13, 13)</c>, and a runtime guess of "the middle of the texture" is the (16, 16) error the
    /// specification originally shipped.</para>
    ///
    /// <para><b>Idempotent.</b> <see cref="SetMode"/> returns immediately when the mode is already
    /// active. Callers set the cursor from a state machine that may run every frame, and re-uploading a
    /// cursor texture per frame is a real cost for no visible change.</para>
    /// </summary>
    public class CursorManager
    {
        private PipBuckCompositionDefinition _definition;
        private PfeCursorMode _mode = PfeCursorMode.Default;
        private bool _loggedMissingTextures;

        /// <summary>True once a composition definition with cursor textures has been supplied.</summary>
        public bool IsReady => _definition != null;

        /// <summary>Current mode. <see cref="PfeCursorMode.Default"/> means no override is applied.</summary>
        public PfeCursorMode Mode => _mode;

        /// <summary>
        /// Supplies the cursor textures. Safe to call before or after the first <see cref="SetMode"/>:
        /// the current mode is re-applied, so a manager that was set to
        /// <see cref="PfeCursorMode.Pointer"/> before its assets loaded does not sit on the OS arrow.
        /// </summary>
        public void Initialize(PipBuckCompositionDefinition definition)
        {
            _definition = definition;
            _loggedMissingTextures = false;

            // Re-apply rather than SetMode(_mode), which would early-return on "already in this mode"
            // and leave the OS cursor in place for the whole session.
            Apply(_mode);
        }

        /// <summary>
        /// Applies a cursor mode. A mode whose texture is missing leaves the OS cursor in place and warns
        /// once — a null <c>Texture2D</c> passed to <see cref="Cursor.SetCursor"/> makes the cursor
        /// disappear entirely, which reads as "the game lost my mouse".
        /// </summary>
        public void SetMode(PfeCursorMode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
            Apply(mode);
        }

        /// <summary>Restores the OS cursor. Call on teardown so a disabled interface does not leave a
        /// crosshair over the editor.</summary>
        public void Restore()
        {
            _mode = PfeCursorMode.Default;
            Apply(PfeCursorMode.Default);
        }

        private void Apply(PfeCursorMode mode)
        {
            if (mode == PfeCursorMode.Default)
            {
                Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
                return;
            }

            if (_definition == null)
            {
                WarnMissing("the composition asset has not been assigned yet");
                return;
            }

            if (!TryResolve(mode, out Texture2D texture, out Vector2 hotspot, out string missingName))
            {
                WarnMissing(missingName);
                Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
                return;
            }

            Cursor.SetCursor(texture, hotspot, CursorMode.Auto);
        }

        private bool TryResolve(PfeCursorMode mode, out Texture2D texture, out Vector2 hotspot, out string missingName)
        {
            switch (mode)
            {
                case PfeCursorMode.Pointer:
                    texture = _definition.cursorArrow;
                    hotspot = Vector2.zero;             // 32×32, tip at the top-left
                    missingName = "cursorArrow (349_visCurArrow)";
                    break;

                case PfeCursorMode.AimCrosshairs:
                    texture = _definition.cursorAim;
                    hotspot = new Vector2(13f, 13f);    // 27×27, centred
                    missingName = "cursorAim (348_visCurTarget)";
                    break;

                case PfeCursorMode.PrecisionCrosshairs:
                    texture = _definition.cursorPrecision;
                    hotspot = new Vector2(13f, 13f);
                    missingName = "cursorPrecision (347_visCurTarget1)";
                    break;

                case PfeCursorMode.MagicCrosshairs:
                    texture = _definition.cursorMagic;
                    hotspot = new Vector2(13f, 13f);
                    missingName = "cursorMagic (346_visCurTarget2)";
                    break;

                default:
                    texture = null;
                    hotspot = Vector2.zero;
                    missingName = mode.ToString();
                    break;
            }

            return texture != null;
        }

        private void WarnMissing(string what)
        {
            if (_loggedMissingTextures) return;
            _loggedMissingTextures = true;

            Debug.LogWarning(
                $"[CursorManager] No cursor texture for {what}. The OS cursor stays visible. " +
                "Run PFE/Art/Import Game Interface and assign the result to the composition asset.");
        }
    }
}
