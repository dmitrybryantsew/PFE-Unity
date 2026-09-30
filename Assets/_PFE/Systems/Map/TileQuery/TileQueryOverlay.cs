using UnityEngine;
using PFE.Core;

namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// Debug overlay for tile collision queries (P2 Stage B4).
    /// Displays the active backend and room on screen.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TileQueryOverlay : MonoBehaviour
    {
        [Tooltip("Optional. When set, this instance reads its channel from this asset instead of the " +
                 "project-wide Resources/PfeDebugSettings. Leave empty for the normal case.")]
        [SerializeField] private PfeDebugSettings debugSettings;

        [Tooltip("Per-instance kill switch, for a scene that wants this component present but silent.")]
        [SerializeField] private bool showOverlay = true;

        private ITileQueryService _queryService;
        private GUIStyle _style;
        private GUIStyle _boxStyle;

        public void Bind(ITileQueryService queryService)
        {
            _queryService = queryService;
        }

        /// <summary>
        /// The project-wide channel, or the instance override when one is assigned. Resolved per frame
        /// rather than cached so a console write takes effect immediately.
        /// </summary>
        private bool ChannelEnabled => debugSettings != null
            ? debugSettings.IsOverlayEnabled(DebugOverlayChannel.TileQuery)
            : DebugOverlays.IsOn(DebugOverlayChannel.TileQuery);

        private void OnGUI()
        {
            // Two gates, two different jobs. `showOverlay` is a per-instance kill switch left for a
            // scene that wants this component present but silent; the channel is the project-wide
            // control, and it is what `col on tilequery` reaches. The old second gate — hiding the
            // readout whenever TileQueryUnified was off — was a display decision made from a
            // behaviour flag, so switching the collision backend silently removed a readout that is
            // about the backend. That is exactly the coupling the channel list replaces.
            if (!showOverlay) return;
            if (!ChannelEnabled) return;

            InitStyles();

            string backendName = _queryService != null ? _queryService.Backend.ToString() : "None";

            string text = $"[P2 Tile Collision Query]\n" +
                          $"Backend: {backendName}\n" +
                          $"Room: {(_queryService?.Room?.id ?? "None")}";

            GUI.Box(new Rect(10, 200, 220, 70), GUIContent.none, _boxStyle);
            GUI.Label(new Rect(15, 205, 210, 60), text, _style);
        }

        private void InitStyles()
        {
            if (_style != null) return;

            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = Color.yellow }
            };

            Texture2D bgTex = new Texture2D(1, 1);
            bgTex.SetPixel(0, 0, new Color(0, 0, 0, 0.75f));
            bgTex.Apply();

            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = bgTex }
            };
        }
    }
}
