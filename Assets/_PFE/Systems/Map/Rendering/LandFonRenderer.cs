using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The land's <c>fon</c> backdrop, drawn as one screen-fixed plane behind everything —
    /// AS3 <c>World.vfon</c> + <c>Grafon.drawFon</c> + <c>Grafon.setFonSize</c>.
    ///
    /// <para><b>The AS3 shape, in the order it happens.</b> <c>World</c> creates a bare
    /// <c>MovieClip</c> called <c>vfon</c> and adds it to <c>main</c> as the <b>second</b> child,
    /// before <c>visual</c> (<c>World.as:417,430-431</c>) — so it is behind the world, the GUI and
    /// everything else. <c>World.ativateLand</c> then fills it with the land's <c>fon</c> id
    /// (<c>World.as:1064</c>, <c>this.grafon.drawFon(this.vfon, this.land.act.fon)</c>) and
    /// <c>World.ativateLoc</c> / <c>resizeScreen</c> size it to the stage
    /// (<c>World.as:1084</c>, <c>World.as:796</c>).</para>
    ///
    /// <para><b>Why this is a child of the camera and not of the room.</b> <c>vfon</c> is a sibling of
    /// <c>visual</c>, and <c>visual</c> is the container the camera scrolls: <c>Camera.step</c> writes
    /// <c>this.w.visual.x = this.vx + this.quakeX</c> (<c>Camera.as:285-286</c>, and the same in
    /// <c>setLoc</c> at <c>:88-89</c>) while <c>main.x/y</c> is <b>never</b> written anywhere in the
    /// oracle. So the backdrop does not scroll with the world. Parenting it to the room instead would
    /// put it in world space, where it would slide as the player walks — a change no screenshot of a
    /// single room would reveal.</para>
    ///
    /// <para><b>Untinted, deliberately.</b> AS3 tints the <c>fon</c> only from the <i>room</i>'s
    /// <c>colorfon</c> option, through <c>cTransformFon</c> (<c>Location.as:454-456</c>,
    /// <c>:651-653</c>; applied at <c>Grafon.as:667-673</c>), and resets it to the identity transform
    /// otherwise (<c>Grafon.as:671-673</c>). The land's own <c>color</c> tints the <i>tiles</i>, not
    /// this layer. <b>Known divergence:</b> the port reads <c>colorfon</c> into
    /// <c>RoomEnvironment.backgroundColorScheme</c> and applies it to the room backwall and
    /// background tiles (<c>RoomBackdropRenderer.ResolveBackgroundLayerTint</c>) — i.e. it uses
    /// <c>colorfon</c> where AS3 uses <c>color</c>, and never applies it here. That is a pre-existing
    /// mis-mapping, it is recorded in <c>docs/AUDIT_surf_calamity_backdrop_camp_2026-10-13.md</c>, and
    /// it is left alone rather than half-fixed: no room in the <c>surf</c> land sets <c>colorfon</c>,
    /// so it is not this layer's blocking defect. Drawing the <c>fon</c> white is AS3's own default
    /// and the correct answer for every room that does not set the option.</para>
    /// </summary>
    public class LandFonRenderer : MonoBehaviour
    {
        /// <summary>
        /// Sorting order for the plane, on the <c>Backwall</c> layer — which is the rearmost named
        /// sorting layer in this project (<c>ProjectSettings/TagManager.asset</c>) and the layer
        /// <see cref="RoomBackdropRenderer"/> draws the room backwall on, at
        /// <c>BackdropSortingOrder = -5000</c>. This sits below that, so the <c>fon</c> is behind the
        /// backwall exactly as <c>vfon</c> is behind <c>visual</c>.
        ///
        /// <para>Nothing else in the project draws on <c>Backwall</c> — <c>grep -rn
        /// "MapSortingLayers.Backwall"</c> finds one hit, <c>RoomBackdropRenderer.cs:989</c> — so this
        /// order cannot collide with a third party.</para>
        /// </summary>
        public const int FonSortingOrder = -9000;

        /// <summary>
        /// The plane's distance in front of the camera, in Unity units.
        ///
        /// <para>Two independent reasons it is behind the world, because either alone would be a
        /// silent failure. The room's sprites live at world z = 0, which is 10 units in front of a
        /// camera at z = -10 (<c>CameraFollow.offset</c>), so this is 2 units further away. And the
        /// sprites use <c>Sprites/Default</c> (<c>ZWrite Off</c>, transparent queue), so the real
        /// ordering is <see cref="FonSortingOrder"/> — the distance is the belt to that pair of
        /// braces, not the mechanism.</para>
        /// </summary>
        private const float PlaneDistanceFromCamera = 12f;

        private Camera _camera;
        private SpriteRenderer _renderer;
        private string _resolvedId = string.Empty;
        private bool _missingSpriteReported;
        private bool _roomRectBranchReported;

        /// <summary>The id actually being drawn — the fallback when the land named nothing.</summary>
        public string FonId => _resolvedId;

        /// <summary>True when a sprite was found and the plane is drawing it.</summary>
        public bool HasSprite => _renderer != null && _renderer.enabled && _renderer.sprite != null;

        /// <summary>
        /// Build the plane as a child of <paramref name="camera"/> and bind it to a land's backdrop
        /// id. Returns <c>null</c> when there is no camera to attach to — the caller reports that,
        /// because a missing backdrop and a missing camera are the same picture and different bugs.
        /// </summary>
        public static LandFonRenderer Create(Camera camera, RoomBackgroundLookup lookup, string backdropId)
        {
            if (camera == null)
            {
                return null;
            }

            var host = new GameObject("LandFon");
            host.transform.SetParent(camera.transform, false);

            var renderer = host.AddComponent<LandFonRenderer>();
            renderer.Bind(camera, lookup, backdropId);
            return renderer;
        }

        /// <summary>
        /// Point the plane at a land's backdrop id. Safe to call repeatedly on a land change: the
        /// GameObject is reused, so the plane never blinks out between two lands that share a
        /// backdrop.
        /// </summary>
        public void Bind(Camera camera, RoomBackgroundLookup lookup, string backdropId)
        {
            _camera = camera != null ? camera : _camera;
            _resolvedId = LandFonMath.ResolveFonId(backdropId);
            _missingSpriteReported = false;

            if (_renderer == null)
            {
                _renderer = GetComponent<SpriteRenderer>();
                if (_renderer == null)
                {
                    _renderer = gameObject.AddComponent<SpriteRenderer>();
                }
            }

            _renderer.sortingLayerName = MapSortingLayers.Backwall;
            _renderer.sortingOrder = FonSortingOrder;
            _renderer.color = Color.white;

            _renderer.sprite = lookup != null ? lookup.GetPrimarySprite(_resolvedId) : null;
            _renderer.enabled = _renderer.sprite != null;

            if (_renderer.sprite == null)
            {
                ReportMissingSprite(lookup);
                return;
            }

            Fit();
        }

        /// <summary>
        /// Re-fit every frame. <c>setFonSize</c> is called from <c>resizeScreen</c>
        /// (<c>World.as:796</c>) and from <c>ativateLoc</c> (<c>World.as:1084</c>) — i.e. on a window
        /// resize and on a room change. The port has no resize event on the camera, and a room change
        /// does not change the answer (the fit depends on the stage, not the room), so re-deriving it
        /// per frame is the cheap way to be right on the one event that does matter. It is a handful
        /// of float operations and two projection calls.
        /// </summary>
        private void LateUpdate()
        {
            Fit();
        }

        private void Fit()
        {
            if (_renderer == null || _renderer.sprite == null || _camera == null)
            {
                return;
            }

            // Project the viewport corners and bring them back into camera-local space rather than
            // reading `orthographicSize` — that keeps this correct for a perspective camera and for a
            // camera with a roll, and it is the same approach the other overlays in this folder take.
            Vector3 bottomLeft = _camera.transform.InverseTransformPoint(
                _camera.ViewportToWorldPoint(new Vector3(0f, 0f, PlaneDistanceFromCamera)));
            Vector3 topRight = _camera.transform.InverseTransformPoint(
                _camera.ViewportToWorldPoint(new Vector3(1f, 1f, PlaneDistanceFromCamera)));

            var stageSize = new Vector2(topRight.x - bottomLeft.x, topRight.y - bottomLeft.y);
            if (stageSize.x <= 0f || stageSize.y <= 0f)
            {
                return;
            }

            var spriteSize = (Vector2)_renderer.sprite.bounds.size;
            if (spriteSize.x <= 0f || spriteSize.y <= 0f)
            {
                return;
            }

            if (LandFonMath.IsStretchedToRoomRect(stageSize, LandFonMath.RoomRectPixels))
            {
                // AS3's FIRST branch (Grafon.as:332-337): the stage is bigger than one room on both
                // axes, so the backdrop is stretched to the room rect and pinned at `visual.x/y`.
                // That branch cannot run in this port and this is the report rather than the guess:
                // the game camera is orthographic size 5 at 100 px per unit (CameraFollow.pixelsPerUnit,
                // WorldCoordinates.UnityToPixel), i.e. exactly 1000 px tall, which equals
                // LandFonMath.RoomRectPixels.y -- and the test is a strict `>`. So the height can
                // never exceed it and the else-branch below is always the live one. We keep the cover
                // fit and say so, instead of inventing the room-rect placement, which would also need
                // the camera clamping the port does not implement (CameraFollow.useBounds is false and
                // SetBounds has no call sites).
                if (!_roomRectBranchReported)
                {
                    _roomRectBranchReported = true;
                    Debug.LogWarning(
                        $"[LandFonRenderer] The viewport is now larger than one room on both axes " +
                        $"({stageSize.x:0}x{stageSize.y:0} px vs {LandFonMath.RoomRectPixels.x:0}x{LandFonMath.RoomRectPixels.y:0}). " +
                        "AS3 Grafon.as:332-337 would switch to stretching the fon over the room rect " +
                        "at visual.x/y; that branch is not ported, so the cover fit is being kept. " +
                        "The camera's zoom or orthographic size has changed — see LandFonRenderer's remarks.");
                }
            }

            Vector2 fit = LandFonMath.ComputeCoverFit(stageSize, spriteSize);
            Vector2 centre = LandFonMath.TopLeftAnchoredCentre(stageSize, fit);
            Vector2 scale = LandFonMath.ComputeScale(fit, spriteSize);

            transform.localPosition = new Vector3(centre.x, centre.y, PlaneDistanceFromCamera);
            transform.localRotation = Quaternion.identity;
            transform.localScale = new Vector3(scale.x, scale.y, 1f);
        }

        private void ReportMissingSprite(RoomBackgroundLookup lookup)
        {
            if (_missingSpriteReported)
            {
                return;
            }

            _missingSpriteReported = true;

            // Name the lookup state, not just the id: "the id is not in the table" and "there is no
            // table" need different fixes, and the importer is what fills the table
            // (PFE/Art/Import Room Graphics -> RoomGraphicsImporter.CollectRoomBackgroundIds).
            string lookupState = lookup == null
                ? "no RoomBackgroundLookup was supplied"
                : $"{lookup.Count} entries in the lookup";

            Debug.LogWarning(
                $"[LandFonRenderer] Land backdrop '{_resolvedId}' has no imported sprite ({lookupState}). " +
                "Re-run PFE/Art/Import Room Graphics; the fon ids are collected from " +
                "LandDefinition.backdropId and from the fonDefault fallback.");
        }
    }
}
