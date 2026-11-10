using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Sim.Combat.Sats;
using PFE.Systems.Combat.Sats;
using PFE.UI.HUD;

namespace PFE.UI.Combat
{
    /// <summary>
    /// The S.A.T.S. overlay — the port of <c>fe.inter.Sats.vis</c> and everything it draws:
    /// the per-unit labels, the queued-cell brackets, the reticle and the screen tint.
    ///
    /// <para><b>Screen-space canvas, not a world-space one — and the difference is not observable.</b>
    /// The Oracle's <c>vsats</c> shares the world's pan and zoom (<c>Camera.as:88-89</c>), which is why
    /// it has to counter-scale every label by <c>1 / cam.scaleV</c> (<c>Sats.as:484</c>) to keep the
    /// text screen-sized. A screen-space canvas gets that for free, and the world-anchored projection
    /// (<see cref="WorldAnchoredHudElement"/>) puts each label exactly where the world-space version
    /// would be. What would <i>not</i> be equivalent is anchoring the labels to fixed screen slots —
    /// that is the defect <c>WorldAnchoredHudElement</c> exists to prevent.</para>
    ///
    /// <para><b>What is drawn procedurally and why.</b> The per-unit bracket is Oracle symbol 4160,
    /// which was never exported (<c>10_SATS_TACTICAL_MODE.md</c> §4.3); the label plate is symbol
    /// 4158, a 262×163 bitmap of baked text. Importing a plate of pre-rendered words would make the
    /// labels unlocalisable and would bake the font at one size, so the plate is a <see cref="Text"/>
    /// and the bracket is four 2 px strips. Both are recorded <c>PortChoice</c>s rather than presented
    /// as art the port has.</para>
    /// </summary>
    /// <summary>
    /// One frame of the Oracle's <b>single</b> <c>radius</c> sprite. It has exactly three states
    /// (<c>Sats.as:126-135</c> and <c>Sats.as:547-563</c>): the melee reach circle for <c>tip &lt;= 1</c>,
    /// the blast circle pinned to the tracer's <b>endpoint</b> for a weapon with <c>explRadius</c>, or
    /// hidden. Showing it unconditionally — the easy mistake — puts a melee-sized ring on every sniper
    /// rifle and detaches the blast ring from the predicted impact.
    ///
    /// <para>World pixels and a unitless scale, not screen coordinates: the view owns the projection,
    /// the bootstrapper owns the scene state. The 254 px base is symbol 4168's authored size.</para>
    /// </summary>
    public readonly struct SatsRadiusSpec
    {
        public static SatsRadiusSpec Hidden => default;

        public readonly bool Visible;
        public readonly Vector2 WorldPixels;
        public readonly float Scale;

        public SatsRadiusSpec(Vector2 worldPixels, float scale)
        {
            Visible = true;
            WorldPixels = worldPixels;
            Scale = scale;
        }
    }

    public sealed class SatsView : MonoBehaviour
    {
        /// <summary>AS3's VATS green — the <c>ColorTransform(1,1,1,1, 0,100,0,0)</c> at
        /// <c>Sats.as:39</c>, applied to the unit copies as <b>+100 on the green channel</b>. Used here
        /// as the tint of every drawn element, because the port has no bitmap copy to colour.</summary>
        public static readonly Color SatsGreen = new Color(0.35f, 1f, 0.45f, 1f);

        /// <summary>The tint AS3 applies over the whole screen (<c>gui.setSats</c>,
        /// <c>GUI.as:2110-2113</c>). Its art was never separately exported; a flat wash plus the
        /// freeze reads the same.</summary>
        public static readonly Color ScreenTint = new Color(0.05f, 0.35f, 0.12f, 0.18f);

        private const float LabelWidth = 220f;
        private const float LabelHeight = 64f;
        private const float BracketThickness = 2f;
        private const float BracketMargin = 6f;

        private Transform _canvas;
        private Font _font;
        private Sprite _reticleSprite;
        private Sprite _cellSprite;

        private Image _tint;
        private Image _reticle;
        private RectTransform _reticleRect;

        private readonly Dictionary<UnitController, Label> _labels = new Dictionary<UnitController, Label>();
        private readonly List<Bracket> _brackets = new List<Bracket>();

        private sealed class Label
        {
            public GameObject Root;
            public RectTransform Rect;
            public Text Name;
            public Text Info;
            public WorldAnchoredHudElement Anchor;
        }

        private sealed class Bracket
        {
            public GameObject Root;
            public RectTransform Rect;
            public Image Fill;
            public Image[] Edges;
        }

        /// <summary>
        /// Build the overlay under <paramref name="canvas"/> — the <b>root</b> canvas transform, because
        /// the labels carry a <see cref="WorldAnchoredHudElement"/> that writes an
        /// <c>anchoredPosition</c> measured from the canvas pivot.
        /// </summary>
        public void Construct(Transform canvas, Font font, PipBuckCompositionDefinition composition)
        {
            _canvas = canvas;
            _font = font;
            _reticleSprite = composition != null ? composition.satsRadius : null;
            _cellSprite = composition != null ? composition.satsCell : null;

            BuildTint();
            BuildReticle();
        }

        // ── Per-frame sync ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Redraw from the controller's state. Called from the bootstrapper's <c>LateUpdate</c>, after
        /// the world-anchored labels have been projected for this frame.
        /// </summary>
        public void Sync(SatsController sats, bool showPercent, SatsRadiusSpec radius)
        {
            if (sats == null) return;

            bool active = sats.IsActive;

            if (_tint != null) _tint.enabled = active;

            if (!active)
            {
                HideAll();
                return;
            }

            SyncLabels(sats, showPercent);
            SyncBrackets(sats);
            SyncReticle(radius);
        }

        private void SyncLabels(SatsController sats, bool showPercent)
        {
            IReadOnlyList<SatsCandidate> candidates = sats.Targeting.Candidates;

            // Retire labels whose unit is no longer a candidate.
            var stale = new List<UnitController>();
            foreach (KeyValuePair<UnitController, Label> pair in _labels)
            {
                if (!Contains(candidates, pair.Key)) stale.Add(pair.Key);
            }

            for (int i = 0; i < stale.Count; i++)
            {
                if (_labels.TryGetValue(stale[i], out Label label) && label.Root != null) Destroy(label.Root);
                _labels.Remove(stale[i]);
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                SatsCandidate c = candidates[i];
                if (c.Unit == null) continue;

                if (!_labels.TryGetValue(c.Unit, out Label label))
                {
                    label = BuildLabel(c.Unit);
                    _labels[c.Unit] = label;
                }

                // `txt.text = u.nazv; if (!weapon.noPerc) txt.text += "\n" + round(prec*100) + "%"`
                // (Sats.as:492-500).
                label.Name.text = showPercent && c.ShowsPercent
                    ? c.Unit.DisplayNameOrSpecies + "\n" + c.Percent + "%"
                    : c.Unit.DisplayNameOrSpecies;

                // The stats panel is gated on `pers.modAnalis` (Sats.as:503) and starts hidden; it is
                // revealed by hovering that unit (mOver), which the port shows for the unit under the
                // pointer only.
                string info = BuildInfoBlock(c.Unit, sats.Stats);
                label.Info.text = info;
                label.Info.enabled = info.Length > 0 && c.Unit == sats.Targeting.UnitUnderPointer(sats.PointerWorldPixels);

                label.Root.SetActive(true);
                label.Anchor.Apply();
            }
        }

        /// <summary>
        /// The hover panel — <c>Sats.as:503-527</c>, verbatim, and <b>only with the <c>mod_analis</c>
        /// implant</b>.
        ///
        /// <para>Without it the mode shows names and percentages and nothing else, which is the
        /// Oracle's own behaviour: the analysis module is a 1 000-cap item
        /// (<c>AllData.as:4152-4154</c>), not a perk, and the panel is the thing it buys.</para>
        /// </summary>
        private static string BuildInfoBlock(UnitController unit, PFE.Systems.RPG.CharacterStats stats)
        {
            if (unit == null || stats == null || stats.modAnalis <= 0f) return string.Empty;

            var s = new System.Text.StringBuilder();
            s.Append("Level: ").Append(unit.UnitLevel + 1);
            s.Append('\n').Append("HP: ").Append(Mathf.CeilToInt(unit.CurrentHealth))
             .Append('/').Append(Mathf.CeilToInt(unit.MaxHealth));

            float skin = unit.SkinResistance;
            if (skin > 0f)
            {
                s.Append('\n').Append("Skin: ").Append(Mathf.CeilToInt(skin));
            }

            // The armour pool and its quality are not exposed as a pair on UnitController; the damage
            // path builds them in UnitArmourMath from the definition. Rather than invent a second
            // reader here, the armour lines are omitted until one accessor serves both. Recorded as a
            // gap in 10_SATS_TACTICAL_MODE.md §8 rather than silently showing zeroes.
            return s.ToString();
        }

        private Label BuildLabel(UnitController unit)
        {
            var root = new GameObject($"SATS Label — {unit.DisplayNameOrSpecies}", typeof(RectTransform));
            var rect = (RectTransform)root.transform;
            rect.SetParent(_canvas, false);
            rect.sizeDelta = new Vector2(LabelWidth, LabelHeight);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            var anchor = root.AddComponent<WorldAnchoredHudElement>();
            anchor.SetTarget(unit.transform);
            // Above the head: `Y - scY` is the body's top, so the gap is one body height.
            anchor.WorldOffset = new Vector3(0f, unit.SpriteSizePixels.y / 100f + 0.25f, 0f);

            var name = NewText(rect, "Name", 14, TextAnchor.LowerCenter, new Vector2(0f, 8f), LabelHeight * 0.5f);
            var info = NewText(rect, "Info", 11, TextAnchor.UpperCenter, new Vector2(0f, -LabelHeight * 0.5f), LabelHeight * 0.5f);
            info.color = new Color(0.75f, 1f, 0.8f, 0.95f);

            return new Label { Root = root, Rect = rect, Name = name, Info = info, Anchor = anchor };
        }

        private Text NewText(RectTransform parent, string name, int size, TextAnchor align,
            Vector2 offset, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(LabelWidth, height);
            rect.anchoredPosition = offset;

            var text = go.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.color = SatsGreen;
            text.alignment = align;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = string.Empty;
            return text;
        }

        // ── Brackets ────────────────────────────────────────────────────────────────────

        private void SyncBrackets(SatsController sats)
        {
            IReadOnlyList<SatsCell> queue = sats.Queue;

            // A unit cell's bracket is sized by the unit's body and how many cells it already carries
            // (`(scX + 10n)/100`, SatsCel.as:37-38); a free-aim cell is the 4162 art at its point.
            int needed = queue.Count;
            while (_brackets.Count < needed) _brackets.Add(BuildBracket(_brackets.Count));

            for (int i = 0; i < _brackets.Count; i++)
            {
                Bracket b = _brackets[i];
                if (i >= queue.Count) { b.Root.SetActive(false); continue; }

                SatsCell cell = queue[i];
                b.Root.SetActive(true);

                Vector2 screen = WorldToCanvas(cell.AimPoint);

                if (cell.Target != null)
                {
                    Vector2 body = cell.Target.SpriteSizePixels;
                    int n = sats.Targeting.CellsOn(cell.Target);

                    // AS3's bracket is 100×100 art scaled by (scX + 10n)/100, so the drawn size is
                    // exactly (scX + 10n) source pixels — which is a WORLD size. Convert once.
                    float wPx = body.x + SatsRules.BracketGrowthPerShot * n;
                    float hPx = body.y + SatsRules.BracketGrowthPerShot * n;

                    SetBracketScreenRect(b, screen, WorldPixelsToScreen(new Vector2(wPx, hPx)));
                }
                else
                {
                    // The free-aim cell is a fixed 63×65 source-pixel icon at the cursor.
                    SetBracketScreenRect(b, screen, WorldPixelsToScreen(new Vector2(63f, 65f)));
                }
            }
        }

        private Bracket BuildBracket(int index)
        {
            var root = new GameObject($"SATS Cell {index}", typeof(RectTransform));
            var rect = (RectTransform)root.transform;
            rect.SetParent(_canvas, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            // A hollow rectangle from four strips. Symbol 4160 (the per-unit bracket) was never
            // imported; four 2 px edges reproduce its read at any body size, which a single stretched
            // 100×100 sprite could not without a 9-slice.
            var edges = new Image[4];
            for (int i = 0; i < 4; i++)
            {
                var go = new GameObject("Edge" + i, typeof(RectTransform));
                var er = (RectTransform)go.transform;
                er.SetParent(rect, false);
                er.anchorMin = er.anchorMax = new Vector2(0.5f, 0.5f);
                er.pivot = new Vector2(0.5f, 0.5f);
                var img = go.AddComponent<Image>();
                img.color = SatsGreen;
                img.raycastTarget = false;
                edges[i] = img;
            }

            var fillGo = new GameObject("Fill", typeof(RectTransform));
            var fr = (RectTransform)fillGo.transform;
            fr.SetParent(rect, false);
            fr.anchorMin = fr.anchorMax = new Vector2(0.5f, 0.5f);
            fr.pivot = new Vector2(0.5f, 0.5f);
            var fill = fillGo.AddComponent<Image>();
            fill.sprite = _cellSprite;
            fill.color = new Color(SatsGreen.r, SatsGreen.g, SatsGreen.b, 0.12f);
            fill.raycastTarget = false;

            return new Bracket { Root = root, Rect = rect, Fill = fill, Edges = edges };
        }

        private void SetBracketScreenRect(Bracket b, Vector2 centre, Vector2 size)
        {
            b.Rect.anchoredPosition = centre;
            b.Rect.sizeDelta = size;

            float w = Mathf.Max(size.x, 4f);
            float h = Mathf.Max(size.y, 4f);

            // Top, bottom, left, right.
            SetEdge(b.Edges[0], new Vector2(0f, h * 0.5f), new Vector2(w, BracketThickness));
            SetEdge(b.Edges[1], new Vector2(0f, -h * 0.5f), new Vector2(w, BracketThickness));
            SetEdge(b.Edges[2], new Vector2(-w * 0.5f, 0f), new Vector2(BracketThickness, h));
            SetEdge(b.Edges[3], new Vector2(w * 0.5f, 0f), new Vector2(BracketThickness, h));

            b.Fill.rectTransform.anchoredPosition = Vector2.zero;
            b.Fill.rectTransform.sizeDelta = new Vector2(w, h);
        }

        private static void SetEdge(Image img, Vector2 pos, Vector2 size)
        {
            var rt = img.rectTransform;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        // ── Reticle ─────────────────────────────────────────────────────────────────────

        private void SyncReticle(SatsRadiusSpec radius)
        {
            // The spec already resolved the three Oracle states; drawing one sprite from one value is
            // the whole job here. In particular, the blast ring's WorldPixels is the tracer ENDPOINT,
            // not the cursor — `radius.x = weapon.trasser.X` (Sats.as:556-557).
            if (_reticle == null) return;

            if (!radius.Visible)
            {
                _reticle.enabled = false;
                return;
            }

            _reticle.enabled = true;
            _reticleRect.anchoredPosition = WorldToCanvas(radius.WorldPixels);

            // Symbol 4168 is 254×254 at scale 1; the Oracle scales it by meleeR/100 or explRadius/100.
            float sizePx = 254f * radius.Scale;
            _reticleRect.sizeDelta = WorldPixelsToScreen(new Vector2(sizePx, sizePx));
        }

        // ── Static chrome ───────────────────────────────────────────────────────────────

        private void BuildTint()
        {
            var go = new GameObject("SATS Tint", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_canvas, false);
            Stretch(rect);

            _tint = go.AddComponent<Image>();
            _tint.color = ScreenTint;
            _tint.raycastTarget = false;
            _tint.enabled = false;
        }

        private void BuildReticle()
        {
            var go = new GameObject("SATS Reticle", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_canvas, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(254f, 254f);

            _reticle = go.AddComponent<Image>();
            _reticle.sprite = _reticleSprite;
            _reticle.color = SatsGreen;
            _reticle.raycastTarget = false;
            _reticle.enabled = false;
            _reticleRect = rect;
        }

        private void HideAll()
        {
            foreach (KeyValuePair<UnitController, Label> pair in _labels)
            {
                if (pair.Value.Root != null) pair.Value.Root.SetActive(false);
            }

            for (int i = 0; i < _brackets.Count; i++) _brackets[i].Root.SetActive(false);
            if (_reticle != null) _reticle.enabled = false;
        }

        // ── Coordinate helpers ──────────────────────────────────────────────────────────

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>World pixels → canvas-local position (the canvas pivot is the screen centre).</summary>
        private Vector2 WorldToCanvas(Vector2 worldPx)
        {
            Camera cam = Camera.main;
            if (cam == null) return Vector2.zero;

            Vector3 world = new Vector3(worldPx.x / 100f, worldPx.y / 100f, 0f);
            Vector3 screen = cam.WorldToScreenPoint(world);
            return new Vector2(screen.x - Screen.width * 0.5f, screen.y - Screen.height * 0.5f);
        }

        /// <summary>
        /// World pixels → screen pixels. Orthographic only: <c>size / (2 * orthographicSize)</c> is the
        /// pixels-per-world-unit factor, and the room camera is orthographic (the perspective branch in
        /// <c>PlayerController</c> exists for a debug camera, not for play).
        /// </summary>
        private static Vector2 WorldPixelsToScreen(Vector2 worldPx)
        {
            Camera cam = Camera.main;
            if (cam == null) return worldPx;

            float ppu = cam.orthographic
                ? Screen.height / (2f * cam.orthographicSize)
                : Screen.height / (2f * 5f);

            return new Vector2(worldPx.x / 100f * ppu, worldPx.y / 100f * ppu);
        }

        private static bool Contains(IReadOnlyList<SatsCandidate> list, UnitController unit)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Unit == unit) return true;
            }
            return false;
        }
    }
}
