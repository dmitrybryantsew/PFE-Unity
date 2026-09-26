using UnityEngine;
using PFE.Core;

namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// Debug overlay for tile collision queries (P2 Stage B4).
    /// Displays the active backend, divergence counts, and timing on screen.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TileQueryOverlay : MonoBehaviour
    {
        [SerializeField] private PfeDebugSettings debugSettings;
        [SerializeField] private bool showOverlay = true;

        private ITileQueryService _queryService;
        private GUIStyle _style;
        private GUIStyle _boxStyle;

        public void Bind(ITileQueryService queryService)
        {
            _queryService = queryService;
        }

        private void OnGUI()
        {
            if (!showOverlay) return;
            if (debugSettings != null && !debugSettings.TileQueryUnified && !debugSettings.TileQueryLogDivergence)
            {
                return;
            }

            InitStyles();

            int divergences = (_queryService is TileQueryDivergenceLogger logger) ? logger.DivergenceCount : 0;
            string backendName = _queryService != null ? _queryService.Backend.ToString() : "None";

            string text = $"[P2 Tile Collision Query]\n" +
                          $"Backend: {backendName}\n" +
                          $"Divergences: {divergences}\n" +
                          $"Room: {(_queryService?.Room?.id ?? "None")}";

            GUI.Box(new Rect(10, 200, 220, 80), GUIContent.none, _boxStyle);
            GUI.Label(new Rect(15, 205, 210, 70), text, _style);
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
