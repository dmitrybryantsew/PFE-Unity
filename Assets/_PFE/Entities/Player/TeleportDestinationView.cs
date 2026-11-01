using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Entities.Player
{
    /// <summary>
    /// The teleport destination square — the port of the GUI clip <c>vis.portCel</c> and its driver
    /// <c>GUI.showPortCel</c> (<c>fe/inter/GUI.as:1531-1552</c>, gated at <c>:1977</c>).
    ///
    /// <para><b>What the oracle does.</b> Three frames on one clip, moved to the snapped cell every
    /// frame by <c>World.w.cam.setKoord(vis.portCel, _loc1_, _loc2_)</c>:</para>
    /// <list type="bullet">
    /// <item><description><b>frame 1</b> — still charging (<c>t_port &lt; portTime</c>): a thin pale
    /// outline;</description></item>
    /// <item><description><b>frame 2</b> — charge complete, release will fire: a filled green cell with
    /// a light centre;</description></item>
    /// <item><description><b>frame 3</b> — <c>gg.checkPort()</c> refused (no mana, or the destination is
    /// not clear): a red outline.</description></item>
    /// </list>
    /// <para>It is invisible for the first 15 frames of the 25-frame charge (<c>:1977</c>) and while
    /// teleporting is not being charged at all.</para>
    ///
    /// <para><b>Why a world sprite rather than a canvas element.</b> The oracle's clip lives on the GUI
    /// layer and is positioned by the camera transform, which in this port is exactly a world sprite:
    /// the camera is 1:1 with world pixels and has no zoom, so projecting a canvas element and placing a
    /// world sprite produce the same rectangle. The world sprite avoids the HUD's 1200×800
    /// <c>CanvasScaler</c>, whose units are not source pixels on any screen that is not exactly 1200×800
    /// wide.</para>
    ///
    /// <para><b>Art and size.</b> The extracted symbol (<c>DefineSprite_4459</c>, three frames) is
    /// 71×71 px with the 1 px outline on the canvas edge, while the oracle's grid is 40 px — the clip is
    /// drawn at the cell's corner, so at native size it would cover the destination cell <i>and</i> the
    /// cell below it and misreport where the player lands. The view therefore scales the art to exactly
    /// one cell; <see cref="NativeArtPixels"/> and <see cref="DrawPixels"/> are the two numbers involved
    /// if that judgement is ever revisited.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TeleportDestinationView : MonoBehaviour
    {
        /// <summary>Frame the oracle's <c>gotoAndStop(1)</c> shows — charging.</summary>
        public const int FrameCharging = 1;

        /// <summary>Frame <c>gotoAndStop(2)</c> — charge complete.</summary>
        public const int FrameReady = 2;

        /// <summary>Frame <c>gotoAndStop(3)</c> — destination refused.</summary>
        public const int FrameBlocked = 3;

        /// <summary>The extracted clip's canvas, in source pixels (<c>sprites/DefineSprite_4459</c>).</summary>
        public const float NativeArtPixels = 71f;

        /// <summary>How large the square is drawn, in source pixels — one cell.</summary>
        public const float DrawPixels = WorldConstants.TILE_SIZE;

        /// <summary>
        /// Order inside <see cref="MapSortingLayers.Foreground"/>. The oracle's clip is on the GUI layer,
        /// i.e. above every world layer, so the marker must not be occluded by terrain, props or water.
        /// </summary>
        public const int SortingOrder = 32000;

        private const string ResourceFolder = "Teleport/portCel_";

        /// <summary>Session-wide: one missing-art warning per run, not one per player spawn.</summary>
        private static bool _reportedMissingArt;

        private SpriteRenderer _renderer;
        private Sprite[] _frames;
        private int _shownFrame;
        private bool _visible;

        /// <summary>True while the square is on screen. Exposed for fixtures.</summary>
        public bool IsVisible => _visible;

        /// <summary>The frame currently drawn, 1-3; 0 while hidden. Exposed for fixtures.</summary>
        public int ShownFrame => _shownFrame;

        /// <summary>The cell currently marked, in room-local pixels. Exposed for fixtures.</summary>
        public Rect MarkedCellPixels { get; private set; }

        /// <summary>
        /// Add the view to <paramref name="host"/> (idempotent) and build its renderer. Returns null when
        /// the art is missing, after one warning — a marker that silently draws nothing is worse than a
        /// missing one, because the player reads it as "the destination is fine".
        /// </summary>
        public static TeleportDestinationView Attach(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            TeleportDestinationView view = host.GetComponent<TeleportDestinationView>();
            if (view == null)
            {
                view = host.AddComponent<TeleportDestinationView>();
            }

            view.EnsureBuilt();
            return view.HasArt ? view : null;
        }

        /// <summary>True once all three frames loaded.</summary>
        public bool HasArt => _frames != null && _frames.Length == 3 && _frames[0] != null;

        private void EnsureBuilt()
        {
            if (_renderer != null)
            {
                return;
            }

            if (_frames == null)
            {
                _frames = new Sprite[3];
                int found = 0;
                for (int i = 0; i < 3; i++)
                {
                    _frames[i] = Resources.Load<Sprite>(ResourceFolder + (i + 1).ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                    if (_frames[i] != null)
                    {
                        found++;
                    }
                }

                if (found != 3)
                {
                    if (!_reportedMissingArt)
                    {
                        _reportedMissingArt = true;
                        Debug.LogWarning(
                            "[TeleportDestinationView] teleport destination art is missing under " +
                            "Resources/'" + ResourceFolder + "1..3'. The teleport still works; the " +
                            "destination square will not be drawn. Extract " +
                            "pfe/sprites/DefineSprite_4459/{1,2,3}.png into that folder.");
                    }

                    return;
                }
            }

            var go = new GameObject("teleportDestination");
            go.transform.SetParent(transform, false);

            _renderer = go.AddComponent<SpriteRenderer>();
            _renderer.sortingLayerName = MapSortingLayers.Foreground;
            _renderer.sortingOrder = SortingOrder;

            // The art is 71 px on a 40 px cell; scale it down rather than editing the PNG. The parent's
            // scale is divided out because the host is whatever GameObject owns the locomotion
            // controller, and a unit whose visual is scaled (a mirrored facing, a size modifier) would
            // otherwise carry that factor into the marker's size. Only the magnitude matters: the art
            // is a symmetric square.
            float scale = DrawPixels / NativeArtPixels;
            Vector3 parentScale = transform.lossyScale;
            float scaleX = Mathf.Approximately(parentScale.x, 0f) ? scale : scale / Mathf.Abs(parentScale.x);
            float scaleY = Mathf.Approximately(parentScale.y, 0f) ? scale : scale / Mathf.Abs(parentScale.y);
            go.transform.localScale = new Vector3(scaleX, scaleY, 1f);

            _renderer.enabled = false;
            _visible = false;
            _shownFrame = 0;
        }

        /// <summary>
        /// Draw <paramref name="frame"/> over <paramref name="cellRectPixels"/> (room-local, y-up).
        /// </summary>
        public void Show(int frame, Rect cellRectPixels)
        {
            EnsureBuilt();
            if (!HasArt)
            {
                return;
            }

            int index = Mathf.Clamp(frame, 1, 3) - 1;
            if (_shownFrame != frame)
            {
                _renderer.sprite = _frames[index];
                _shownFrame = frame;
            }

            MarkedCellPixels = cellRectPixels;

            // Pixel space is y-up like Unity's, so the cell centre converts directly. The sprite's pivot
            // is its centre (the import default), which is why the centre — not the corner — is used.
            Vector3 world = WorldCoordinates.PixelToUnity(
                new Vector2(cellRectPixels.x + cellRectPixels.width * 0.5f,
                            cellRectPixels.y + cellRectPixels.height * 0.5f));
            _renderer.transform.position = new Vector3(world.x, world.y, transform.position.z);

            _renderer.enabled = true;
            _visible = true;
        }

        /// <summary>Take the square off screen. Idempotent.</summary>
        public void Hide()
        {
            if (_renderer != null && _renderer.enabled)
            {
                _renderer.enabled = false;
            }

            _visible = false;
            _shownFrame = 0;
        }
    }
}
