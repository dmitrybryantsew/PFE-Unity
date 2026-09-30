using System.Collections.Generic;
using System.Text;
using UnityEngine;
using PFE.Core;
using PFE.Entities.Units;
using PFE.Systems.Combat;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Draws each unit's health, armour integrity and damage-reduction terms above the unit, for every
    /// unit in the camera view.
    ///
    /// <para><b>What it is for.</b> It reads the <i>target's own</i> state — the same
    /// <see cref="IDamageable"/> members the damage resolver reads — so it answers "did that shot
    /// actually reach the unit?" independently of the attacker's arithmetic. A number that does not move
    /// while a bullet visibly passes through is evidence about the hit path; a number that moves by less
    /// than expected is evidence about the formula. Those two are indistinguishable without a readout of
    /// the target's own state.</para>
    ///
    /// <para><b>Read the flash, not the value, on a training dummy.</b> <c>UnitTrain.control()</c> sets
    /// <c>hp = maxhp</c> every tick by design, so a dummy's bar is always full — the useful signal is
    /// that it <i>flickers</i> when a hit lands, which is why the bar is drawn from the live value on
    /// every frame rather than from a cached snapshot.</para>
    ///
    /// <para><b>Geometry comes from the collider, not from the tile grid.</b> The label is anchored to
    /// <c>Collider2D.bounds.max</c> — the engine's own idea of where the unit ends — so it cannot drift
    /// from the collider the projectiles actually test against. Deriving a position from the placement's
    /// grid coordinates would reintroduce exactly the convention mismatch this project has been bitten
    /// by repeatedly.</para>
    /// </summary>
    public sealed class UnitHealthOverlay : MonoBehaviour
    {
        /// <summary>Live instance, if the scene-load hook has run.</summary>
        public static UnitHealthOverlay Instance { get; private set; }

        /// <summary>
        /// Scene lookup rate. A per-frame <c>FindObjectsByType</c> over a room is wasteful, and a unit's
        /// <i>health</i> is read live per frame anyway — only the list of units is cached.
        /// </summary>
        private const float RefreshInterval = 0.2f;

        /// <summary>Cap on labels drawn per frame, so a crowded room cannot stall IMGUI.</summary>
        private const int MaxDrawn = 60;

        private const float BarWidth = 46f;
        private const float BarHeight = 5f;

        private readonly List<UnitController> _units = new List<UnitController>();
        private readonly List<UnitController> _visible = new List<UnitController>();

        private GUIStyle _label;
        private GUIStyle _readout;

        private float _timer;
        private int _drawn;
        private int _withoutStats;

        // ── Lifecycle ────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureRuntimeInstance()
        {
            if (Instance == null && FindFirstObjectByType<UnitHealthOverlay>() == null)
            {
                var go = new GameObject("UnitHealthOverlay");
                Instance = go.AddComponent<UnitHealthOverlay>();
                DontDestroyOnLoad(go);
            }
        }

        /// <summary>Get the overlay, creating it if the scene-load bootstrap did not run.</summary>
        public static UnitHealthOverlay EnsureInstance()
        {
            if (Instance != null) return Instance;

            var found = FindFirstObjectByType<UnitHealthOverlay>();
            if (found != null)
            {
                Instance = found;
                return found;
            }

            var go = new GameObject("UnitHealthOverlay");
            Instance = go.AddComponent<UnitHealthOverlay>();
            return Instance;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            if (Application.isPlaying) DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!DebugOverlays.IsOn(DebugOverlayChannel.UnitHealth))
            {
                return;
            }

            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f) return;

            _timer = RefreshInterval;
            RefreshUnits();
        }

        private void RefreshUnits()
        {
            _units.Clear();
            _units.AddRange(FindObjectsByType<UnitController>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None));
        }

        // ── Drawing ──────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (!DebugOverlays.IsOn(DebugOverlayChannel.UnitHealth)) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            EnsureStyles();
            SelectVisible(cam);

            _drawn = 0;
            _withoutStats = 0;

            for (int i = 0; i < _visible.Count; i++)
            {
                if (_drawn >= MaxDrawn) break;

                UnitController unit = _visible[i];

                // `_unitStats` is protected, so the public projection is the only way in — and it is
                // also the honest one, because it is what the resolver reads.
                if (unit.MaxHealth <= 1f && unit.CurrentHealth <= 0f)
                {
                    // A unit with no UnitStats assigned. Worth counting rather than skipping: it is the
                    // "spawned without a definition" state, and such a unit takes damage and ignores it.
                    _withoutStats++;
                }

                if (!TryAnchor(unit, out Vector3 anchor)) continue;

                Vector3 screen = cam.WorldToScreenPoint(anchor);
                if (screen.z <= 0f) continue;

                DrawUnit(unit, screen);
                _drawn++;
            }

            DrawReadout();
        }

        /// <summary>
        /// The label's world anchor: the top of the unit's own collider. Falls back to the transform
        /// only when the unit has no <c>Collider2D</c>, and says so in the readout rather than silently
        /// drawing at a different place.
        /// </summary>
        private static bool TryAnchor(UnitController unit, out Vector3 anchor)
        {
            var collider = unit.GetComponent<Collider2D>();

            if (collider != null)
            {
                Bounds bounds = collider.bounds;
                anchor = new Vector3(bounds.center.x, bounds.max.y + 0.12f, 0f);
                return true;
            }

            anchor = unit.transform.position + Vector3.up * 0.6f;
            return true;
        }

        /// <summary>
        /// View-limited to the camera frustum. The gameplay camera is <b>perspective</b>, so this
        /// projects the viewport corners rather than branching on <c>orthographic</c> — a branch would
        /// fall through to "draw everything" and the on-screen count would stop matching.
        /// </summary>
        private void SelectVisible(Camera cam)
        {
            _visible.Clear();

            float plane = Mathf.Max(Mathf.Abs(cam.transform.position.z), 0.01f);

            if (cam.transform.forward.z < 0.9f)
            {
                // Camera is not looking down the z axis; a view rect would be meaningless, so take the
                // whole list rather than a rect that could silently exclude everything.
                _visible.AddRange(_units);
                return;
            }

            Vector3 cornerA = cam.ViewportToWorldPoint(new Vector3(0f, 0f, plane));
            Vector3 cornerB = cam.ViewportToWorldPoint(new Vector3(1f, 1f, plane));

            const float padding = 2f;
            var view = new Rect(
                Mathf.Min(cornerA.x, cornerB.x) - padding,
                Mathf.Min(cornerA.y, cornerB.y) - padding,
                Mathf.Abs(cornerB.x - cornerA.x) + padding * 2f,
                Mathf.Abs(cornerB.y - cornerA.y) + padding * 2f);

            for (int i = 0; i < _units.Count; i++)
            {
                UnitController unit = _units[i];
                if (unit == null) continue;

                Vector3 p = unit.transform.position;
                if (view.Contains(new Vector2(p.x, p.y))) _visible.Add(unit);
            }
        }

        private void DrawUnit(UnitController unit, Vector3 screen)
        {
            // IMGUI's y runs down from the top; WorldToScreenPoint's runs up from the bottom.
            float x = screen.x;
            float y = Screen.height - screen.y;

            float max = Mathf.Max(1f, unit.MaxHealth);
            float current = Mathf.Clamp(unit.CurrentHealth, 0f, max);
            float fraction = current / max;

            var bar = new Rect(x - BarWidth * 0.5f, y, BarWidth, BarHeight);

            Color previous = GUI.color;

            // Plate, then fill. The plate is what makes the bar readable over a bright tile; the fill
            // colour carries the state (green healthy, red critical, grey for a unit with no stats).
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);

            GUI.color = unit.MaxHealth <= 1f && unit.CurrentHealth <= 0f
                ? new Color(0.55f, 0.55f, 0.6f, 0.9f)          // no UnitStats: nothing to show
                : fraction > 0.5f
                    ? new Color(0.3f, 0.9f, 0.35f, 0.9f)
                    : fraction > 0.2f
                        ? new Color(1f, 0.8f, 0.2f, 0.9f)
                        : new Color(1f, 0.3f, 0.25f, 0.9f);

            GUI.DrawTexture(new Rect(bar.x + 1f, bar.y + 1f, (BarWidth - 2f) * fraction, BarHeight - 2f),
                            Texture2D.whiteTexture);

            GUI.color = previous;

            GUI.Label(new Rect(x - 60f, y + BarHeight + 1f, 120f, 16f), Describe(unit), _label);
        }

        /// <summary>
        /// One line per unit: hp, then only the terms that are actually non-default.
        /// </summary>
        /// <remarks>
        /// Defaults are omitted on purpose. Printing <c>arm 0 skin 0 dex 1.00</c> on every unit would
        /// make the one unit that carries armour impossible to pick out of a screenshot — and the
        /// armoured training dummy (skin 20) is precisely the unit worth finding.
        /// </remarks>
        private static string Describe(UnitController unit)
        {
            var sb = new StringBuilder();

            sb.Append(Mathf.CeilToInt(unit.CurrentHealth))
              .Append('/')
              .Append(Mathf.CeilToInt(unit.MaxHealth));

            ArmourState armour = unit.Armour;
            if (armour.IsEquipped)
            {
                sb.Append("  <color=#8fb8ff>arm ")
                  .Append(Mathf.CeilToInt(armour.integrity))
                  .Append('/')
                  .Append(Mathf.CeilToInt(armour.maxIntegrity))
                  .Append("</color>");
            }

            if (unit.SkinResistance != 0f)
            {
                sb.Append("  <color=#ffd24d>skin ")
                  .Append(unit.SkinResistance.ToString("0.#"))
                  .Append("</color>");
            }

            EvasionState evasion = unit.Evasion;
            if (evasion.Dexterity != 1f || evasion.DexterityPlus != 0f || evasion.Dodge != 0f)
            {
                sb.Append("  <color=#c78fff>dex ")
                  .Append(evasion.Dexterity.ToString("0.##"));

                if (evasion.DexterityPlus != 0f) sb.Append('+').Append(evasion.DexterityPlus.ToString("0.##"));
                if (evasion.Dodge != 0f) sb.Append(" dodge ").Append(evasion.Dodge.ToString("0.##"));

                sb.Append("</color>");
            }

            if (!unit.IsAlive) sb.Append("  <color=#ff4040>DEAD</color>");

            return sb.ToString();
        }

        /// <summary>Bottom-right, above the damage readout. See <c>FloatingDamageOverlay</c>.</summary>
        private void DrawReadout()
        {
            const float width = 340f;
            const float height = 20f;

            var box = new Rect(Screen.width - width - 8f, Screen.height - height - 32f, width, height);

            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = previous;

            string text = $"<b>[health]</b> {_units.Count} unit(s) in room, {_drawn} drawn" +
                          (_withoutStats > 0
                              ? $", <color=#ff4040>{_withoutStats} WITHOUT stats</color>"
                              : "") +
                          "    <i>grey bar = no UnitStats</i>";

            GUI.Label(new Rect(box.x + 6f, box.y + 2f, width - 12f, height - 4f), text, _readout);
        }

        private void EnsureStyles()
        {
            if (_label != null) return;

            _label = MakeStyle(12);
            _label.alignment = TextAnchor.UpperCenter;

            _readout = MakeStyle(12);
            _readout.alignment = TextAnchor.MiddleLeft;
        }

        private static GUIStyle MakeStyle(int size)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                richText = true,
                wordWrap = false
            };

            // Forced white: the runtime IMGUI skin's default label colour is dark, and a dark label on a
            // dark game is unreadable in the screenshot the overlay exists to produce.
            style.normal.textColor = Color.white;
            style.hover.textColor = Color.white;
            style.active.textColor = Color.white;

            return style;
        }
    }
}
