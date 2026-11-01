using PFE.Entities.Units;
using PFE.Systems.Map.Rendering;
using UnityEngine;
using UnityEngine.UI;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The gold star and name that hang over a starred enemy — the port of AS3's
    /// <c>hpBar.goldstar</c> (<c>Unit.addVisual():2886-2889</c>) and the unit's <c>nazv</c>.
    ///
    /// <para><b>What the oracle does, and where this follows it.</b> <c>addVisual</c> creates one
    /// <c>hpBar</c> per non-player unit and hides its <c>goldstar</c> child for every unit whose
    /// <c>hero</c> is <c>&lt;= 0</c>, so the star is a property of the promotion and nothing else.
    /// <c>visDetails()</c> then shows the bar whenever the unit is hurt, armoured or <b>a hero</b>
    /// (<c>:2985</c>) — i.e. a starred enemy is always marked, even at full health. That part is
    /// reproduced exactly: the marker appears on promotion and disappears with the unit.</para>
    ///
    /// <para><b>The fog, and why this class has to ask about it.</b> In AS3 the <c>hpBar</c> is not
    /// screen furniture: it is added to <c>Grafon.visObjs[3]</c> (<c>Unit.as:2896-2899</c>), a layer
    /// <c>Grafon</c> adds <i>before</i> <c>visLight</c> — the fog bitmap (<c>Grafon.as:193-200</c>) — so
    /// the bar is composited under the fog and dims with it, exactly like the unit's own sprite. This
    /// port draws the marker on the HUD canvas, which is above every world layer, so a star stayed at
    /// full brightness inside an unlit tile and advertised an enemy the player could not see. The
    /// per-frame alpha applied in <see cref="LateUpdate"/> is what puts it back under the fog; see
    /// <see cref="FogVisibilitySampling"/> for why alpha = visibility is the same composite, and
    /// <see cref="RoomBackdropRenderer.TryGetFogVisibility"/> for the read-back.</para>
    ///
    /// <para><b>What this adds, and it is a port choice.</b> The oracle draws <i>no</i> name over the
    /// head: <c>hpBar</c> has a bar, an armour strip and a star, and the name reaches the player through
    /// the look-at prompt (<c>GUI.as:1321</c>, <c>this.celObj.nazv</c>) and through dialogue. The port has
    /// no enemy look-at prompt, so a star on its own would mark an enemy as special and never say who it
    /// is — which is half of what a named enemy is for. The label therefore sits beside the star.
    /// <see cref="HudLayout"/> is where a "port choice" of this kind is normally tagged; it is recorded
    /// here instead because this element is built by the bootstrapper and not by the layout table.</para>
    ///
    /// <para><b>Where it anchors.</b> Above the head, in <b>world</b> units, through
    /// <see cref="WorldAnchoredHudElement"/> — the same mechanism the reload bar and the interaction
    /// prompt use, and the same reason: an offset in world space keeps the gap constant at every
    /// resolution, which a pixel offset after projection would not. The oracle's own
    /// <c>hpbar.y = Y - stayY - 20</c> (<c>:3030</c>) is the same idea measured in its sprite space.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EliteNameplateView : MonoBehaviour
    {
        /// <summary>
        /// The star's drawn size, in reference-canvas pixels. The oracle's <c>goldstar</c> is a small
        /// icon on a bar that is 20 px tall in its own space; 18 keeps it legible without competing with
        /// the health readout at the bottom of the screen.
        /// </summary>
        public const float StarSize = 18f;

        /// <summary>How far above the unit's own height the marker sits, in world units.</summary>
        public const float HeadGap = 0.32f;

        /// <summary>Used when the unit has no definition — a bare test spawn.</summary>
        private const float DefaultBodyHeight = 0.9f;

        /// <summary>The label's width, in reference-canvas pixels. Wide enough for "Garl Death-Hoof".</summary>
        private const float LabelWidth = 220f;

        /// <summary>The label block's height, in reference-canvas pixels.</summary>
        private const float LabelHeight = 40f;

        /// <summary>One marker and the state that keeps it honest.</summary>
        private sealed class Label
        {
            public GameObject Root;
            public CanvasGroup Group;

            /// <summary>The room this unit was spawned into, resolved once — see <see cref="ResolveRoom"/>.</summary>
            public RoomVisualController Room;

            /// <summary>Set once <see cref="Room"/> has been resolved, so the parent walk runs once.</summary>
            public bool RoomResolved;
        }

        private Transform _canvas;
        private Font _font;

        /// <summary>Live labels, keyed by the unit they follow.</summary>
        private readonly System.Collections.Generic.Dictionary<UnitController, Label> _labels =
            new System.Collections.Generic.Dictionary<UnitController, Label>();

        /// <summary>Labels built so far — for a test, and for the F2 readout.</summary>
        public int LiveCount => _labels.Count;

        /// <summary>
        /// Build the view and adopt any unit that was promoted before it existed.
        ///
        /// <para><paramref name="canvas"/> must be the <b>root canvas</b> transform: the labels are
        /// parented to it directly, because <see cref="WorldAnchoredHudElement"/> writes an
        /// <c>anchoredPosition</c> measured from the canvas pivot.</para>
        /// </summary>
        public void Construct(Transform canvas, Font font)
        {
            if (_canvas != null) return;

            _canvas = canvas;
            _font = font;

            EliteEnemyRegistry.Registered -= OnRegistered;
            EliteEnemyRegistry.Registered += OnRegistered;
            EliteEnemyRegistry.Unregistered -= OnUnregistered;
            EliteEnemyRegistry.Unregistered += OnUnregistered;

            // The HUD can be built after the room that contains the enemies — the bootstrapper runs from
            // its own Start and a test may spawn first. Adopting the live set here is what stops those
            // units from being the only ones with no marker.
            System.Collections.Generic.IReadOnlyList<UnitController> live = EliteEnemyRegistry.Live;
            for (int i = 0; i < live.Count; i++)
            {
                Build(live[i]);
            }
        }

        private void OnDestroy()
        {
            EliteEnemyRegistry.Registered -= OnRegistered;
            EliteEnemyRegistry.Unregistered -= OnUnregistered;
        }

        private void OnRegistered(UnitController unit) => Build(unit);

        private void OnUnregistered(UnitController unit)
        {
            if (unit == null || !_labels.TryGetValue(unit, out Label label)) return;

            _labels.Remove(unit);
            if (label.Root != null)
            {
                Destroy(label.Root);
            }
        }

        /// <summary>
        /// Put every marker back under the fog — the half of AS3's behaviour that the canvas cannot
        /// inherit by being in the right layer.
        ///
        /// <para><b>LateUpdate, not Update.</b> The anchor moves in its own
        /// <see cref="WorldAnchoredHudElement.LateUpdate"/>, and the fog mask is refreshed from
        /// <see cref="RoomVisualController.LateUpdate"/>; sampling the visibility here means the alpha
        /// and the position are read from the same frame the player is looking at.</para>
        /// </summary>
        private void LateUpdate()
        {
            if (_labels.Count == 0) return;

            foreach (System.Collections.Generic.KeyValuePair<UnitController, Label> pair in _labels)
            {
                UnitController unit = pair.Key;
                Label label = pair.Value;
                if (unit == null || label == null || label.Root == null || label.Group == null) continue;

                // Retried until it succeeds rather than latched on the first try: a label is built from
                // ApplyPlacement, which can run before the spawner has finished parenting the unit into
                // the room, and caching that null would leave the marker permanently fog-blind. Once
                // resolved the walk never runs again.
                if (!label.RoomResolved)
                {
                    label.Room = ResolveRoom(unit);
                    label.RoomResolved = label.Room != null;
                }

                float fogVisibility = 1f;
                if (label.Room != null)
                {
                    label.Room.TryGetFogVisibility(unit.transform.position, out fogVisibility);
                }

                label.Group.alpha = FogVisibilitySampling.ResolveWorldAnchoredAlpha(fogVisibility, unit.IsVisible);
            }
        }

        /// <summary>
        /// The controller drawing this unit's room, or null when it is not under one (a bare test
        /// spawn).
        ///
        /// <para><b>No scene scan.</b> Units are spawned as children of the room's
        /// <c>BackgroundPhysicalObjects</c> transform, which <c>RoomVisualController</c> creates under
        /// itself (<c>EnsureNamedChild(transform, …)</c>), so the parent walk is the lookup. The
        /// registry's own remarks are the reason this matters: a per-frame <c>FindObjectsByType</c> over
        /// a handful of units is exactly what <see cref="EliteEnemyRegistry"/> exists to avoid, and this
        /// runs once per marker — the retry only costs anything while the answer is still "not yet".</para>
        /// </summary>
        private static RoomVisualController ResolveRoom(UnitController unit)
        {
            return unit != null ? unit.GetComponentInParent<RoomVisualController>() : null;
        }

        /// <summary>One marker: a star above a name, both following the unit.</summary>
        private void Build(UnitController unit)
        {
            if (unit == null || _canvas == null) return;
            if (_labels.ContainsKey(unit)) return;

            var root = new GameObject($"Elite Nameplate — {unit.DisplayNameOrSpecies}", typeof(RectTransform));
            var rootRect = (RectTransform)root.transform;
            rootRect.SetParent(_canvas, false);
            rootRect.sizeDelta = new Vector2(LabelWidth, LabelHeight);
            rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);

            var anchor = root.AddComponent<WorldAnchoredHudElement>();
            anchor.SetTarget(unit.transform);
            anchor.WorldOffset = new Vector3(0f, BodyHeight(unit) + HeadGap, 0f);

            BuildStar(rootRect);
            BuildName(rootRect, unit.DisplayNameOrSpecies);

            // One CanvasGroup rather than an alpha write per graphic: the star and the name must fade
            // together, and a group multiplies into both children without either of them knowing.
            var group = root.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            _labels[unit] = new Label { Root = root, Group = group };
        }

        private static float BodyHeight(UnitController unit)
        {
            if (unit != null && unit.Definition != null && unit.Definition.height > 0f)
            {
                return unit.Definition.height;
            }

            return DefaultBodyHeight;
        }

        /// <summary>The gold star itself — <c>hpBar.goldstar</c>.</summary>
        private void BuildStar(RectTransform parent)
        {
            var starGo = new GameObject("Star", typeof(RectTransform), typeof(Image));
            var starRect = (RectTransform)starGo.transform;
            starRect.SetParent(parent, false);
            starRect.sizeDelta = new Vector2(StarSize, StarSize);
            starRect.anchoredPosition = new Vector2(0f, StarSize * 0.5f);

            var image = starGo.GetComponent<Image>();
            image.sprite = GoldStarSprite.Shared;
            image.color = Color.white;
            image.raycastTarget = false;
        }

        /// <summary>The name under the star.</summary>
        private void BuildName(RectTransform parent, string displayName)
        {
            var textGo = new GameObject("Name", typeof(RectTransform), typeof(Text));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(parent, false);
            textRect.sizeDelta = new Vector2(LabelWidth, 22f);
            textRect.anchoredPosition = new Vector2(0f, -StarSize * 0.5f - 8f);

            var text = textGo.GetComponent<Text>();
            text.font = _font;
            text.fontSize = 16;
            text.color = GoldStarSprite.GoldColour;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;

            // The oracle's names are Latin-only in the shipped document, but a localised build is not,
            // and Unity's legacy Text renders a missing glyph as a blank rather than an error.
            text.text = string.IsNullOrEmpty(displayName) ? string.Empty : displayName;

            var shadow = textGo.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
            shadow.effectDistance = new Vector2(1f, -1f);
        }

        /// <summary>
        /// The star bitmap, drawn once and shared by every marker.
        ///
        /// <para><b>Why procedural.</b> The oracle's <c>goldstar</c> is a symbol inside the interface SWF
        /// and the port has never imported it — there is no star PNG anywhere under <c>Art/</c>. A
        /// five-point polygon rasterised once is a faithful stand-in for a 16-pixel icon and needs no
        /// import step, which is the difference between this landing today and waiting on an asset pass.
        /// The colour is the oracle's star gold.</para>
        /// </summary>
        private static class GoldStarSprite
        {
            /// <summary>AS3's star gold — the <c>hpBar</c> symbol's fill.</summary>
            public static readonly Color GoldColour = new Color(1f, 0.83f, 0.25f, 1f);

            /// <summary>Texture edge, in pixels. Small on purpose — this is a 16 px icon in the oracle.</summary>
            private const int Size = 32;

            private static Sprite _shared;

            public static Sprite Shared
            {
                get
                {
                    if (_shared != null) return _shared;

                    var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
                    {
                        name = "EliteStar",
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                    };

                    // Normalised star radii, sampled from the polygon AS3 draws: outer point straight up,
                    // inner radius the usual 0.382 of the outer one.
                    const float innerRatio = 0.382f;
                    float centre = Size * 0.5f;
                    float outer = Size * 0.48f;

                    var pixels = new Color32[Size * Size];
                    for (int y = 0; y < Size; y++)
                    {
                        for (int x = 0; x < Size; x++)
                        {
                            float dx = x + 0.5f - centre;
                            float dy = y + 0.5f - centre;

                            // Angle measured from straight up, clockwise.
                            float angle = Mathf.Atan2(dx, dy);
                            float radius = Mathf.Sqrt(dx * dx + dy * dy);

                            // Ten alternating vertices: five points, five valleys.
                            float sector = Mathf.PI * 2f / 10f;
                            float folded = Mathf.Repeat(angle, sector * 2f);
                            if (folded > sector)
                            {
                                folded = sector * 2f - folded;
                            }

                            float t = folded / sector;
                            float edge = Mathf.Lerp(outer, outer * innerRatio, t);

                            // One pixel of feather, so a 32 px star does not read as a jagged blob when
                            // the canvas scaler magnifies it.
                            float alpha = Mathf.Clamp01(edge - radius + 0.5f);
                            Color32 pixel = GoldColour;
                            pixel.a = (byte)Mathf.RoundToInt(alpha * 255f);
                            pixels[y * Size + x] = pixel;
                        }
                    }

                    texture.SetPixels32(pixels);
                    texture.Apply(false, true);

                    _shared = Sprite.Create(texture, new Rect(0f, 0f, Size, Size),
                                            new Vector2(0.5f, 0.5f), 100f);
                    _shared.name = "EliteStar";
                    return _shared;
                }
            }
        }
    }
}
