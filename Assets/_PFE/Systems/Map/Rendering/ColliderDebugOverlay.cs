using System.Collections.Generic;
using System.Text;
using PFE.Core;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.Streaming;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The world-space half of the debug-overlay system: tile colliders, unit colliders and the room
    /// boundary, drawn as wireframes so they survive a Game-view screenshot. (The text readouts —
    /// tile-query, room streaming, object pool, SIM CLOCK — live in their own components and read the
    /// same channel mask; see <see cref="DebugOverlayChannel"/>.)
    ///
    /// <para><b>Every channel is independent.</b> Tiles and units in particular are two separate bits,
    /// so they are on together or apart in any combination, as are doors, triggers, transitions,
    /// objects and the four text readouts. <c>col on doors,triggers</c> adds two overlays and leaves
    /// everything else exactly as it was.</para>
    ///
    /// <para><b>What it draws, and why both boxes.</b> For every tile it draws the box the physics
    /// engine actually has (<c>Collider2D.bounds</c>) <i>and</i> the box the eye actually sees
    /// (<c>SpriteRenderer.bounds</c>). The two are drawn together on purpose: "the player stands on
    /// air" is a statement about the gap between them, and a tool that showed only one of the two
    /// could not tell the difference between "the collider is in the wrong place", "the sprite is
    /// in the wrong place", and "there is no collider at all". A solid tile with no collider is
    /// drawn in magenta rather than simply omitted, because the omission is the bug.</para>
    ///
    /// <para><b>Why it reads its state from <see cref="PfeDebugSettings"/>.</b> So the Inspector
    /// checkboxes, the console commands and the F5/F6 hotkeys are three views of one value rather
    /// than three pieces of state that can disagree. The overlay re-reads the settings on its
    /// refresh tick, so a console write is picked up without any notification plumbing.</para>
    ///
    /// <para><b>Nothing here is gated by <c>runtimeLoggingEnabled</c>.</b> It is a visualisation,
    /// not a log; silencing logs must never be able to hide the thing you turned on to look at.</para>
    ///
    /// <para><b>Not in AS3.</b> There is no counterpart in the oracle — this is port-side
    /// instrumentation, so its shape is chosen for usefulness and is not evidence about behaviour.
    /// The <i>numbers</i> it reports are evidence; the way they are drawn is not.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ColliderDebugOverlay : MonoBehaviour
    {
        // F5 / F6 were the only free keys in the F-row: F1 is the console, F8 the area triggers,
        // F9 the door triggers, F10 the map objects. A hotkey matters here specifically because the
        // console panel covers 45% of the screen — the workflow is "press a key, screenshot",
        // and typing into an open console would hide the thing being photographed.
        public const KeyCode TileHotkey = KeyCode.F5;
        public const KeyCode UnitHotkey = KeyCode.F6;

        /// <summary>How often the overlay re-scans the scene. 5 Hz is far below the cost of a
        /// per-frame <c>FindObjectsByType</c> over a 1200-tile room and far above the rate at which
        /// anyone reads a debug overlay.</summary>
        private const float RefreshInterval = 0.2f;

        private const float TileLineWidth = 0.045f;
        private const float UnitLineWidth = 0.07f;
        private const float TransitionLineWidth = 0.08f;
        private const float FeetMarkerWidth = 0.10f;
        private const float VisualLineWidthScale = 0.45f;

        /// <summary>
        /// Below this, the sprite box and the collider box are the same box and the second one is not
        /// drawn. Half a game pixel: anything a viewer could not see is not worth four quads, and the
        /// divergences — the ones that matter — still get both.
        /// </summary>
        private const float VisualBoxEpsilon = 0.005f;

        /// <summary>Above every map sprite. <c>Foreground</c> is the last declared sorting layer and
        /// <c>MainTiles</c> orders in the low hundreds, so this cannot be occluded by the room.</summary>
        private const int OverlaySortingOrder = 32000;

        /// <summary>
        /// Hard ceiling on quads in one refresh, as a backstop only. The camera-view filter is what
        /// keeps this small in normal use; the ceiling exists so a scene with no usable camera cannot
        /// take the frame rate to zero, and the legend says when it was hit, because a silently
        /// truncated overlay is a lying overlay.
        ///
        /// <para>This was 6000 and was hit in practice: the view rect used to fall back to
        /// "everything" for a perspective camera, so all 837 room tiles were drawn at 8 quads each.
        /// The fallback is fixed (see <see cref="GetViewRect"/>); the ceiling is raised as well,
        /// because a backstop that fires during normal use is not a backstop.</para>
        /// </summary>
        private const int MaxQuadsPerRefresh = 40000;

        private static readonly Color TileWallColor = new Color(1f, 0.25f, 0.25f, 0.95f);
        private static readonly Color TilePlatformColor = new Color(0.25f, 1f, 0.35f, 0.95f);
        private static readonly Color TileStairColor = new Color(1f, 0.9f, 0.2f, 0.95f);
        private static readonly Color TileAirColor = new Color(0.65f, 0.65f, 0.78f, 0.55f);
        private static readonly Color TileMismatchColor = new Color(1f, 0f, 1f, 1f);
        private static readonly Color TileVisualColor = new Color(1f, 1f, 1f, 0.32f);
        private static readonly Color PlayerColor = new Color(0.2f, 0.9f, 1f, 0.95f);
        private static readonly Color NpcColor = new Color(1f, 0.6f, 0.15f, 0.95f);
        private static readonly Color FeetColor = new Color(1f, 1f, 1f, 1f);

        /// <summary>Magenta for the room boundary as well, so "this is not tile geometry, it is a
        /// boundary the unit crosses" reads at a glance — and matches the MISMATCH colour, which is
        /// the other thing that is a statement rather than an object.</summary>
        private static readonly Color TransitionColor = new Color(1f, 0.35f, 1f, 0.95f);

        /// <summary>
        /// The LowLevelPhysics2D chain mirror. Deliberately a colour no other channel uses — it is the
        /// one drawing that is <i>not</i> a Unity collider, and the whole point of showing it is to be
        /// able to tell at a glance which of the two collision pictures a given edge belongs to.
        /// </summary>
        private static readonly Color ChainColor = new Color(0.35f, 1f, 1f, 0.9f);

        /// <summary>Line thickness for the chain polylines. Thinner than a tile box: a chain is a
        /// zero-thickness surface, and drawing it fat would imply a volume it does not have.</summary>
        private const float ChainLineWidth = 0.03f;

        /// <summary>1 world unit is 100 game pixels; AS3's porog thresholds are in game pixels.</summary>
        private const float PixelsPerUnit = 100f;

        /// <summary>Unit porog: the step-up allowance AS3 gives a NON-PLAYER unit (Unit.as:278-280).
        /// Kept only for contrast in the probe text — it is <b>not</b> the player's allowance, and
        /// using it as the probe's yardstick is what hid the D6 defect (see <see cref="Verdict"/>).</summary>
        private const float UnitPorogPixels = 10f;

        /// <summary>
        /// Gap below which the probe calls the feet "supported", in game pixels. The allowance itself
        /// is 0 for a grounded player (UnitPlayer.as:2530), so a bare <c>&gt; 0</c> test would call
        /// float-point dust "FLOATING"; half a pixel is below anything visible or actionable.
        /// </summary>
        private const float SupportTolerancePixels = 0.5f;

        public static ColliderDebugOverlay Instance { get; private set; }

        private static Sprite _whiteSprite;

        private Transform _root;
        private readonly List<SpriteRenderer> _pool = new List<SpriteRenderer>();
        private int _usedQuads;
        private float _timer;

        // Counters for the on-screen legend. These are what make a screenshot self-describing:
        // "41 matched, 37 drawn" is a different claim from "41 matched, 0 drawn", and without the
        // numbers the second one looks exactly like the overlay being off.
        private int _tilesMatched;
        private int _tilesDrawn;
        private int _tilesWithoutCollider;
        private int _unitsDrawn;
        private int _playersDrawn;
        private int _npcsDrawn;
        private bool _transitionsDrawn;

        /// <summary>Chain edges drawn by the LowLevelPhysics2D channel on the last refresh.</summary>
        private int _chainSegmentsDrawn;
        private bool _quadsExhausted;
        private bool _legendVisible;
        private GUIStyle _legendStyle;

        // ── Lifecycle ────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureRuntimeInstance()
        {
            if (Instance == null && FindFirstObjectByType<ColliderDebugOverlay>() == null)
            {
                var go = new GameObject("ColliderDebugOverlay");
                Instance = go.AddComponent<ColliderDebugOverlay>();
                DontDestroyOnLoad(go);
            }
        }

        /// <summary>
        /// Get the overlay, creating it if the scene-load bootstrap did not run (a bare test scene,
        /// or a console command issued from an editor script). The tool must not be the thing that
        /// is unavailable when something else is broken.
        /// </summary>
        public static ColliderDebugOverlay EnsureInstance()
        {
            if (Instance != null) return Instance;

            var found = FindFirstObjectByType<ColliderDebugOverlay>();
            if (found != null)
            {
                Instance = found;
                return found;
            }

            var go = new GameObject("ColliderDebugOverlay");
            Instance = go.AddComponent<ColliderDebugOverlay>();
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
            HandleHotkeys();

            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f) return;

            _timer = RefreshInterval;
            Refresh();
        }

        private void HandleHotkeys()
        {
            // Via the constants, not the literals: a hotkey declared at the top of the class and
            // then hardcoded down here is a value that reads as configurable and is not.
            if (KeyDownThisFrame(TileHotkey, KeyboardKey(TileHotkey)))
            {
                ToggleTiles();
            }

            if (KeyDownThisFrame(UnitHotkey, KeyboardKey(UnitHotkey)))
            {
                ToggleUnits();
            }
        }

        /// <summary>
        /// The Input System path for a hotkey, needed because <c>Keyboard.current</c> is null when
        /// only the legacy input path is active — the existing debug presenters (F8/F9/F10) guard
        /// it the same way. The <c>default</c> arm is deliberate: a key this class does not declare
        /// simply has no Input System route, rather than silently borrowing another key's.
        /// </summary>
        private static bool KeyboardKey(KeyCode key)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return false;

            switch (key)
            {
                case KeyCode.F5: return kb.f5Key.wasPressedThisFrame;
                case KeyCode.F6: return kb.f6Key.wasPressedThisFrame;
                default: return false;
            }
        }

        private static bool KeyDownThisFrame(KeyCode legacy, bool fromInputSystem)
        {
            return Input.GetKeyDown(legacy) || fromInputSystem;
        }

        private static void ToggleTiles()
        {
            PfeDebugSettings settings = Settings;
            if (settings == null) return;

            bool turningOn = !settings.ShowTileColliderDebug;
            settings.ShowTileColliderDebug = turningOn;

            // Turning the overlay ON while the filter is None would draw nothing, which reads
            // exactly like a broken toggle. Opening a filter that has nothing in it is the same
            // class of mistake as the console shortcut that silently does nothing.
            if (turningOn && settings.TileColliderFilter == ColliderDebugTileFilter.None)
            {
                settings.TileColliderFilter = ColliderDebugTileFilter.All;
            }

            Debug.Log($"[ColliderDebug] Tile colliders {(turningOn ? "ON" : "OFF")} " +
                      $"— filter={ColliderDebugFilters.Format(settings.TileColliderFilter)}");

            RequestImmediateRefresh();
        }

        private static void ToggleUnits()
        {
            PfeDebugSettings settings = Settings;
            if (settings == null) return;

            bool turningOn = !settings.ShowUnitColliderDebug;
            settings.ShowUnitColliderDebug = turningOn;

            if (turningOn && settings.UnitColliderFilter == ColliderDebugUnitFilter.None)
            {
                settings.UnitColliderFilter = ColliderDebugUnitFilter.All;
            }

            Debug.Log($"[ColliderDebug] Unit colliders {(turningOn ? "ON" : "OFF")} " +
                      $"— filter={ColliderDebugFilters.Format(settings.UnitColliderFilter)}");

            RequestImmediateRefresh();
        }

        private static void RequestImmediateRefresh()
        {
            if (Instance != null) Instance.Refresh();
        }

        /// <summary>
        /// The settings asset, via the shared <see cref="DebugOverlays"/> cache so this class and the
        /// four text overlays cannot end up holding two different references.
        /// </summary>
        private static PfeDebugSettings Settings => DebugOverlays.Settings;

        // ── Public API used by the developer console ─────────────────────────

        /// <summary>
        /// Set the tile filter and enable or disable the tile overlay to match. A <c>None</c> filter
        /// turns the overlay off rather than leaving it on and empty.
        /// </summary>
        public static void SetTileFilter(ColliderDebugTileFilter filter)
        {
            PfeDebugSettings settings = Settings;
            if (settings == null) return;

            settings.TileColliderFilter = filter;
            settings.ShowTileColliderDebug = filter != ColliderDebugTileFilter.None;

            EnsureInstance().Refresh();
        }

        /// <summary>Set the unit filter and enable or disable the unit overlay to match.</summary>
        public static void SetUnitFilter(ColliderDebugUnitFilter filter)
        {
            PfeDebugSettings settings = Settings;
            if (settings == null) return;

            settings.UnitColliderFilter = filter;
            settings.ShowUnitColliderDebug = filter != ColliderDebugUnitFilter.None;

            EnsureInstance().Refresh();
        }

        /// <summary>
        /// Turn every overlay off without clearing the sub-filters, so <c>col tiles shelf</c> still
        /// means the same thing after a <c>col off</c>.
        /// </summary>
        public static void DisableAll()
        {
            PfeDebugSettings settings = Settings;
            if (settings == null) return;

            settings.EnabledOverlays = DebugOverlayChannel.None;

            if (Instance != null) Instance.Refresh();
        }

        /// <summary>
        /// The live counts from the last refresh — deliberately only the counts. The channel list is
        /// <see cref="DebugOverlayChannels.DescribeAll"/>'s job, and having both here meant a console
        /// reply that printed "on: tiles,units" twice.
        /// </summary>
        public static string DescribeState()
        {
            if (Instance == null) return "counts: the overlay has not run a scan yet.";

            var sb = new StringBuilder();
            sb.Append($"counts: tiles {Instance._tilesMatched} matched in the room, " +
                      $"{Instance._tilesDrawn} drawn in view, " +
                      $"{Instance._tilesWithoutCollider} WITHOUT a collider");
            sb.Append($"\n        units {Instance._unitsDrawn} drawn in view " +
                      $"({Instance._playersDrawn} player, {Instance._npcsDrawn} npc)");
            sb.Append($"\n        room boundary {(Instance._transitionsDrawn ? "drawn" : "not drawn")}");
            sb.Append($"\n        llp2d chains {Instance._chainSegmentsDrawn} edges drawn in view " +
                      "(the Box2D geometry a projectile sweeps against)");

            if (Instance._quadsExhausted)
            {
                sb.Append($"\n        TRUNCATED at {MaxQuadsPerRefresh} quads — " +
                          "the view is wider than the overlay will draw");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Redraw right now instead of waiting for the next refresh tick. Used by the console, so that
        /// a reply saying "on" and the frame on screen cannot disagree.
        /// </summary>
        public void RefreshNow()
        {
            _timer = RefreshInterval;
            Refresh();
        }

        // ── Refresh ──────────────────────────────────────────────────────────

        private void Refresh()
        {
            PfeDebugSettings settings = Settings;

            // Read the mask once per refresh. Every channel is an independent bit, so any combination
            // of them can be on together — tiles and units included, which is the case the request
            // called out explicitly.
            DebugOverlayChannel channels = settings != null
                ? settings.EnabledOverlays
                : DebugOverlayChannel.None;

            bool tilesOn = (channels & DebugOverlayChannel.Tiles) != 0;
            bool unitsOn = (channels & DebugOverlayChannel.Units) != 0;
            bool transitionsOn = (channels & DebugOverlayChannel.Transitions) != 0;
            bool legendOn = (channels & DebugOverlayChannel.Legend) != 0;
            bool chainsOn = (channels & DebugOverlayChannel.LowLevelPhysics) != 0;

            if (!tilesOn && !unitsOn && !transitionsOn && !legendOn && !chainsOn)
            {
                _legendVisible = false;
                _usedQuads = 0;
                HideAllQuads();
                if (_root != null) _root.gameObject.SetActive(false);
                return;
            }

            EnsureRoot();
            _root.gameObject.SetActive(true);

            _usedQuads = 0;
            _tilesMatched = 0;
            _tilesDrawn = 0;
            _tilesWithoutCollider = 0;
            _unitsDrawn = 0;
            _playersDrawn = 0;
            _npcsDrawn = 0;
            _transitionsDrawn = false;
            _chainSegmentsDrawn = 0;
            _quadsExhausted = false;

            Rect view = GetViewRect();

            if (tilesOn) DrawTiles(settings.TileColliderFilter, view);
            if (unitsOn) DrawUnits(settings.UnitColliderFilter, view);
            if (transitionsOn) DrawTransitions(view);
            if (chainsOn) DrawLowLevelPhysicsChains(view);

            HideUnusedQuads();

            // The legend is a channel of its own, so the drawn geometry can be screenshotted without
            // the text plate covering it — and so `col on legend` alone still tells you what is on.
            _legendVisible = legendOn;
        }

        /// <summary>
        /// The world rect the main camera can see, padded by a couple of units. Tiles are filtered
        /// against it because a 48x25 room holds 1200 tiles and a 20x12 view holds a couple of
        /// hundred — drawing the rest would be invisible work on every refresh tick.
        ///
        /// <para><b>Why this no longer branches on <c>orthographic</c>.</b> It used to: a perspective
        /// camera fell back to "draw everything", and the gameplay camera here is perspective, so the
        /// fallback was the normal path — every one of the room's 837 tiles was drawn and the quad
        /// ceiling fired on every frame. <c>ViewportToWorldPoint</c> at the z=0 plane gives the same
        /// answer for both projections, so there is one code path and no projection branch to get
        /// wrong.</para>
        /// </summary>
        private static Rect GetViewRect()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                // No camera at all: fall back to "everything". A debug overlay that silently draws
                // nothing because it could not find a camera would be worse than a slow one.
                return new Rect(-10000f, -10000f, 20000f, 20000f);
            }

            // The rooms live on the z = 0 plane, so that is where the view rect has to be measured.
            // Distance from the camera to that plane, floored so a camera sitting on the plane cannot
            // produce a degenerate rect.
            float planeDistance = Mathf.Max(Mathf.Abs(cam.transform.position.z), 0.01f);

            // A rotated camera would make a viewport-corner rect a lie; fall back to "everything"
            // rather than draw a rect that is confidently wrong.
            if (cam.transform.forward.z < 0.9f)
            {
                return new Rect(-10000f, -10000f, 20000f, 20000f);
            }

            Vector3 cornerA = cam.ViewportToWorldPoint(new Vector3(0f, 0f, planeDistance));
            Vector3 cornerB = cam.ViewportToWorldPoint(new Vector3(1f, 1f, planeDistance));

            const float padding = 2f;
            float xMin = Mathf.Min(cornerA.x, cornerB.x) - padding;
            float yMin = Mathf.Min(cornerA.y, cornerB.y) - padding;
            float xMax = Mathf.Max(cornerA.x, cornerB.x) + padding;
            float yMax = Mathf.Max(cornerA.y, cornerB.y) + padding;

            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private void DrawTiles(ColliderDebugTileFilter filter, Rect view)
        {
            if (filter == ColliderDebugTileFilter.None) return;

            TileRenderer[] tiles = FindObjectsByType<TileRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < tiles.Length; i++)
            {
                TileRenderer tr = tiles[i];
                if (tr == null) continue;

                TileData data = tr.TileData;
                if (data == null) continue;
                if (!ColliderDebugFilters.Matches(filter, data.physicsType)) continue;

                _tilesMatched++;

                Collider2D collider = tr.GetComponent<Collider2D>();
                SpriteRenderer sprite = tr.GetComponent<SpriteRenderer>();

                Rect colliderRect = collider != null ? WorldRect(collider.bounds) : Rect.zero;
                Rect visualRect = ResolveVisualRect(tr, sprite, colliderRect);

                // The mismatch pair. TileVisualManager.CreateTile only attaches a TileCollider when
                // physicsType != Air, so "solid in the data, absent in physics" is the shape of a
                // tile that renders as floor and cannot be stood on — which is the whole question.
                //
                // Counted BEFORE the view test, so the number in the legend is a property of the
                // room rather than of where the camera happens to be. A count that silently changes
                // when you scroll is a count that cannot be compared between two screenshots.
                bool missingCollider = data.physicsType != TilePhysicsType.Air && collider == null;
                bool unexpectedCollider = data.physicsType == TilePhysicsType.Air && collider != null;

                if (missingCollider || unexpectedCollider) _tilesWithoutCollider++;

                if (!view.Overlaps(colliderRect) && !view.Overlaps(visualRect)) continue;

                if (missingCollider || unexpectedCollider)
                {
                    EmitBox(visualRect, TileLineWidth * 2f, TileMismatchColor);
                    _tilesDrawn++;
                    continue;
                }

                if (collider != null)
                {
                    EmitBox(colliderRect, TileLineWidth, ColorForPhysicsType(data.physicsType));
                }
                else
                {
                    // Only reachable for an air tile, which has no collider by construction
                    // (TileVisualManager.CreateTile attaches one only when physicsType != Air).
                    // Drawing its cell is the only thing there is to show, and it is what makes the
                    // air flag useful rather than an empty toggle.
                    EmitBox(visualRect, TileLineWidth, TileAirColor);
                }

                // The eye's box, drawn thin and white over the physics box — but only when it is a
                // different box. Where the two agree there is nothing to compare and four more quads
                // is just a slower frame; where they differ, that difference IS the finding.
                bool sameBox = collider != null && SameRect(colliderRect, visualRect, VisualBoxEpsilon);
                if (!sameBox)
                {
                    EmitBox(visualRect, TileLineWidth * VisualLineWidthScale, TileVisualColor);
                }

                _tilesDrawn++;
            }
        }

        private void DrawUnits(ColliderDebugUnitFilter filter, Rect view)
        {
            if (filter == ColliderDebugUnitFilter.None) return;

            UnitController[] units = FindObjectsByType<UnitController>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < units.Length; i++)
            {
                UnitController unit = units[i];
                if (unit == null) continue;

                bool isPlayer = unit.IsPlayer;
                if (!ColliderDebugFilters.Matches(filter, isPlayer)) continue;

                Collider2D collider = unit.GetComponent<Collider2D>();
                if (collider == null) continue;

                Rect colliderRect = WorldRect(collider.bounds);
                if (!view.Overlaps(colliderRect)) continue;

                Color color = isPlayer ? PlayerColor : NpcColor;
                EmitBox(colliderRect, UnitLineWidth, color);

                SpriteRenderer sprite = unit.GetComponent<SpriteRenderer>();
                if (sprite == null) sprite = unit.GetComponentInChildren<SpriteRenderer>();
                if (sprite != null && sprite.sprite != null)
                {
                    Rect visualRect = WorldRect(sprite.bounds);
                    EmitBox(visualRect, UnitLineWidth * VisualLineWidthScale,
                            new Color(color.r, color.g, color.b, 0.5f));
                }

                // The feet line. A unit's box has to meet a tile's collider somewhere, and the
                // bottom edge is where — everything else about "standing on air" is a comparison
                // between this bar and the top of the tile box under it.
                EmitQuad(new Vector2(colliderRect.center.x, colliderRect.yMin),
                         new Vector2(colliderRect.width * 1.6f, FeetMarkerWidth), FeetColor);

                _unitsDrawn++;
                if (isPlayer) _playersDrawn++;
                else _npcsDrawn++;
            }
        }

        /// <summary>
        /// Draw the room boundary the player crosses to start a room transition.
        ///
        /// <para><b>Why the boundary and not a trigger volume.</b> There is no trigger volume. The
        /// transition is decided arithmetically in <c>TilePhysicsController.CheckRoomBoundaryExit</c>
        /// (:687-738): the unit's position is converted to room-local pixels and compared against the
        /// room's pixel size — <c>localPixelX &gt;= roomWidthPixels</c> on the right, <c>&lt; 0</c> on
        /// the left, and the same on Y. So the thing to draw is exactly the room rectangle, and the
        /// four edges of it are the four lines that fire <c>TransitionThroughEdge(1..4)</c>. Drawing
        /// an inset band would be inventing a tolerance that the code does not have.</para>
        ///
        /// <para><b>The origin is taken from the same helper the transitions use</b>
        /// (<c>RoomTransitionManager.GetRoomOriginUnity</c>), so if this rectangle does not sit on the
        /// room's tiles then the two disagree about where the room is — which is itself the finding.
        /// Nothing here re-derives a grid coordinate.</para>
        /// </summary>
        private void DrawTransitions(Rect view)
        {
            RoomVisualController visual = FindFirstObjectByType<RoomVisualController>();
            RoomInstance room = visual != null ? visual.RoomInstance : null;
            if (room == null) return;

            Vector3 origin = RoomTransitionManager.GetRoomOriginUnity(room);
            float width = room.width * WorldConstants.TILE_SIZE / PixelsPerUnit;
            float height = room.height * WorldConstants.TILE_SIZE / PixelsPerUnit;
            var roomRect = new Rect(origin.x, origin.y, width, height);

            if (!view.Overlaps(roomRect)) return;

            EmitBox(roomRect, TransitionLineWidth, TransitionColor);

            // A short inward tick at the middle of each edge, so a still frame shows that all four
            // edges are the thing being marked rather than one continuous border of unknown meaning.
            const float tickLength = 0.5f;
            const float tickHalf = 0.12f;

            EmitQuad(new Vector2(roomRect.xMin + tickLength * 0.5f, roomRect.center.y),
                     new Vector2(tickLength, tickHalf), TransitionColor);
            EmitQuad(new Vector2(roomRect.xMax - tickLength * 0.5f, roomRect.center.y),
                     new Vector2(tickLength, tickHalf), TransitionColor);
            EmitQuad(new Vector2(roomRect.center.x, roomRect.yMin + tickLength * 0.5f),
                     new Vector2(tickHalf, tickLength), TransitionColor);
            EmitQuad(new Vector2(roomRect.center.x, roomRect.yMax - tickLength * 0.5f),
                     new Vector2(tickHalf, tickLength), TransitionColor);

            _transitionsDrawn = true;
        }

        /// <summary>
        /// Draw the LowLevelPhysics2D chain mirror — the Box2D v3 geometry a projectile actually sweeps
        /// against — as world-space polylines.
        ///
        /// <para><b>Why it is worth its own channel.</b> Everything else this overlay draws is a Unity
        /// <c>Collider2D</c> on a GameObject, found by <c>FindObjectsByType</c>. The chain mirror has
        /// neither: it is geometry inside a <see cref="PhysicsWorld"/>, reachable only through
        /// <see cref="MapBridge.PhysicsWorldService"/>. So a projectile that stops somewhere the tile
        /// boxes say is empty — or passes through somewhere they say is solid — was previously
        /// <i>undiagnosable from the overlay</i>, because the half of the picture that disagreed was
        /// the half that could not be drawn.</para>
        ///
        /// <para><b>What is drawn is what the engine was handed.</b> The vertices come from
        /// <c>RoomChainGeometry.ChainPoints</c>, recorded at emission from the same array passed to
        /// <c>CreateChain</c>. That includes the one-vertex lead-in and lead-out added because Box2D
        /// discards an open chain's first and final edge, so the polyline is slightly longer than the
        /// collidable surface at each end — deliberately, and the honest direction to err.</para>
        ///
        /// <para>Segments are culled against the camera view before being emitted, for the same reason
        /// tiles are: a room's silhouette is thousands of edges and a view holds a few hundred.</para>
        /// </summary>
        private void DrawLowLevelPhysicsChains(Rect view)
        {
            IPhysicsWorldService service = ResolvePhysicsWorldService();
            if (service == null)
            {
                return;
            }

            IReadOnlyDictionary<RoomInstance, RoomChainGeometry> rooms = service.MirroredRooms;
            if (rooms == null)
            {
                return;
            }

            foreach (KeyValuePair<RoomInstance, RoomChainGeometry> entry in rooms)
            {
                RoomChainGeometry geometry = entry.Value;
                if (geometry == null) continue;

                IReadOnlyList<Vector2[]> chains = geometry.ChainPoints;
                if (chains == null) continue;

                for (int c = 0; c < chains.Count; c++)
                {
                    Vector2[] points = chains[c];
                    if (points == null) continue;

                    for (int i = 0; i + 1 < points.Length; i++)
                    {
                        Vector2 from = points[i];
                        Vector2 to = points[i + 1];
                        if (!SegmentNearView(from, to, view)) continue;

                        EmitSegment(from, to, ChainLineWidth, ChainColor);
                        _chainSegmentsDrawn++;
                    }
                }
            }
        }

        /// <summary>
        /// The physics service, or null when the map bridge has not been constructed yet (a debug
        /// overlay can run in a scene with no game). Resolved per refresh rather than cached: it is one
        /// scene lookup at 5 Hz, against the per-refresh <c>FindObjectsByType</c> over every tile that
        /// this component already does, and a cached service would go stale across a scene reload.
        /// </summary>
        private static IPhysicsWorldService ResolvePhysicsWorldService()
        {
            MapBridge bridge = FindFirstObjectByType<MapBridge>();
            return bridge != null ? bridge.PhysicsWorldService : null;
        }

        /// <summary>Cheap AABB overlap between a segment and the view rect, before paying for a quad.</summary>
        private static bool SegmentNearView(Vector2 from, Vector2 to, Rect view)
        {
            float minX = Mathf.Min(from.x, to.x);
            float maxX = Mathf.Max(from.x, to.x);
            float minY = Mathf.Min(from.y, to.y);
            float maxY = Mathf.Max(from.y, to.y);

            return maxX >= view.xMin && minX <= view.xMax
                && maxY >= view.yMin && minY <= view.yMax;
        }

        /// <summary>
        /// Do two world rects describe the same box, to within <paramref name="epsilon"/> on every
        /// edge? Used to skip the duplicate box rather than to decide anything about physics.
        /// </summary>
        private static bool SameRect(Rect a, Rect b, float epsilon)
        {
            return Mathf.Abs(a.xMin - b.xMin) <= epsilon
                && Mathf.Abs(a.xMax - b.xMax) <= epsilon
                && Mathf.Abs(a.yMin - b.yMin) <= epsilon
                && Mathf.Abs(a.yMax - b.yMax) <= epsilon;
        }

        /// <summary>
        /// The "what the eye sees" box for a tile, with a fallback for the case where there is
        /// nothing to measure.
        ///
        /// <para>A <see cref="TileRenderer"/> can legitimately have no sprite (the importer reports
        /// tiles with no visual) and no collider at the same time. Returning an empty rect there
        /// would make the tile silently vanish from the overlay — and worse, it would be filtered out
        /// by the camera-view test, because <c>Rect.zero</c> is at the world origin. So the fallback
        /// is a one-cell marker centred on the tile's own transform: an approximation, but an
        /// approximation that is visible and labelled by its colour, which is the property that
        /// matters for a diagnostic.</para>
        /// </summary>
        private static Rect ResolveVisualRect(TileRenderer tr, SpriteRenderer sprite, Rect colliderRect)
        {
            if (sprite != null && sprite.sprite != null) return WorldRect(sprite.bounds);
            if (colliderRect.width > 0f && colliderRect.height > 0f) return colliderRect;

            // One tile cell, centred on the tile's own world position. TileData.GetBounds() would
            // give the same answer, but it is in room-local pixel space and would need the room
            // origin — this needs no convention at all.
            const float halfCell = 0.2f; // 40 px / 2 / 100
            Vector3 p = tr.transform.position;
            return new Rect(p.x - halfCell, p.y - halfCell, halfCell * 2f, halfCell * 2f);
        }

        private static Color ColorForPhysicsType(TilePhysicsType type)
        {
            switch (type)
            {
                case TilePhysicsType.Wall: return TileWallColor;
                case TilePhysicsType.Platform: return TilePlatformColor;
                case TilePhysicsType.Stair: return TileStairColor;
                case TilePhysicsType.Air: return TileAirColor;
                // A new physics type must be given a colour on purpose; it must not inherit one
                // silently and be read as a wall.
                default: return TileMismatchColor;
            }
        }

        // ── Quad pool ────────────────────────────────────────────────────────

        private void EnsureRoot()
        {
            if (_root != null) return;

            var go = new GameObject("__ColliderDebugQuads");
            go.transform.SetParent(transform, false);
            _root = go.transform;
        }

        private void EmitBox(Rect rect, float thickness, Color color)
        {
            if (rect.width <= 0f || rect.height <= 0f) return;

            float half = thickness * 0.5f;
            float innerHeight = Mathf.Max(rect.height - thickness, 0f);

            EmitQuad(new Vector2(rect.center.x, rect.yMin + half),
                     new Vector2(rect.width + thickness, thickness), color);
            EmitQuad(new Vector2(rect.center.x, rect.yMax - half),
                     new Vector2(rect.width + thickness, thickness), color);
            EmitQuad(new Vector2(rect.xMin + half, rect.center.y),
                     new Vector2(thickness, innerHeight), color);
            EmitQuad(new Vector2(rect.xMax - half, rect.center.y),
                     new Vector2(thickness, innerHeight), color);
        }

        private void EmitQuad(Vector2 center, Vector2 size, Color color)
        {
            SpriteRenderer sr = RentQuad();
            if (sr == null) return;

            sr.enabled = true;
            sr.color = color;

            Transform t = sr.transform;
            t.position = new Vector3(center.x, center.y, 0f);
            // Reset, not left alone: the pool is shared with EmitSegment, which rotates. A quad drawn
            // from a recycled renderer that still carries a segment's angle would be silently wrong,
            // and the pool is large enough that the reuse happens on the very next refresh.
            t.rotation = Quaternion.identity;
            t.localScale = new Vector3(Mathf.Max(size.x, 0.001f), Mathf.Max(size.y, 0.001f), 1f);
        }

        /// <summary>
        /// Draw one straight segment as a rotated quad.
        ///
        /// <para><b>Why a segment and not a box.</b> A Box2D chain is a zero-thickness polyline, so the
        /// things being drawn are edges at arbitrary angles — an axis-aligned box cannot express them.
        /// The quad is stretched to the segment's length and rotated to its direction, which is why
        /// <see cref="EmitQuad"/> has to clear the rotation it inherits from the shared pool.</para>
        /// </summary>
        private void EmitSegment(Vector2 from, Vector2 to, float thickness, Color color)
        {
            Vector2 delta = to - from;
            float length = delta.magnitude;
            if (length <= 0.0001f) return;

            SpriteRenderer sr = RentQuad();
            if (sr == null) return;

            sr.enabled = true;
            sr.color = color;

            Transform t = sr.transform;
            t.position = new Vector3((from.x + to.x) * 0.5f, (from.y + to.y) * 0.5f, 0f);
            t.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            t.localScale = new Vector3(length, Mathf.Max(thickness, 0.001f), 1f);
        }

        private SpriteRenderer RentQuad()
        {
            if (_usedQuads >= MaxQuadsPerRefresh)
            {
                _quadsExhausted = true;
                return null;
            }

            if (_usedQuads < _pool.Count)
            {
                return _pool[_usedQuads++];
            }

            var go = new GameObject("q");
            go.transform.SetParent(_root, false);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = WhiteSprite;
            sr.sortingLayerName = MapSortingLayers.Foreground;
            sr.sortingOrder = OverlaySortingOrder;

            _pool.Add(sr);
            _usedQuads++;
            return sr;
        }

        private void HideUnusedQuads()
        {
            for (int i = _usedQuads; i < _pool.Count; i++)
            {
                if (_pool[i] != null) _pool[i].enabled = false;
            }
        }

        private void HideAllQuads()
        {
            for (int i = 0; i < _pool.Count; i++)
            {
                if (_pool[i] != null) _pool[i].enabled = false;
            }
        }

        private static Sprite WhiteSprite
        {
            get
            {
                if (_whiteSprite != null) return _whiteSprite;

                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "__ColliderDebugWhite"
                };
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();

                // 1 pixel at 1 pixel-per-unit is exactly one world unit at scale 1, so a quad's
                // localScale IS its world size. No conversion, and therefore no convention to get
                // wrong.
                _whiteSprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
                _whiteSprite.name = "__ColliderDebugWhiteSprite";
                return _whiteSprite;
            }
        }

        private static Rect WorldRect(Bounds bounds)
        {
            return new Rect(bounds.min.x, bounds.min.y, bounds.size.x, bounds.size.y);
        }

        // ── Probe: the numeric form of the same question ──────────────────────

        /// <summary>
        /// Report, geometrically, what is under the player's feet.
        ///
        /// <para><b>Why this exists alongside the drawing.</b> A picture answers "does it look
        /// wrong"; this answers "by how many pixels, and does physics agree with the sprite". The
        /// gap is reported in <i>game pixels</i> because that is the unit AS3's porog thresholds are
        /// expressed in, so the number can be compared with the oracle's allowance directly instead
        /// of being eyeballed.</para>
        ///
        /// <para><b>The allowance compared against is the PLAYER's, not the unit's.</b> This probe
        /// always describes the player, and AS3 gives a grounded player <c>porog = 0</c>
        /// (<c>UnitPlayer.as:2530</c>) where a generic unit gets 10 (<c>Unit.as:278</c>). An earlier
        /// version of this probe judged the gap against the unit's 10, so a standing player floating
        /// 4-6 px above a catwalk came back "SUPPORTED" — the diagnostic agreed with the defect it
        /// existed to find. Both numbers are now printed so the difference is visible in a
        /// screenshot rather than buried in the code.</para>
        ///
        /// <para><b>Convention-free by construction.</b> The query is a point-containment test in
        /// world space against the boxes the renderers and colliders actually report. Nothing here
        /// converts a grid coordinate, so there is no tileCoord convention to get backwards — which
        /// is the failure this kind of probe usually has.</para>
        /// </summary>
        public static string Probe()
        {
            PlayerController player = FindFirstObjectByType<PlayerController>();
            if (player == null) return "[collider] No player in the scene.";

            Collider2D body = player.GetComponent<Collider2D>();
            if (body == null) return "[collider] The player has no Collider2D.";

            Bounds bounds = body.bounds;

            // One game pixel below the feet: inside whatever is being stood on, but not inside the
            // player's own box. 0.01 world units is exactly 1 px (TileQueryConstants.PixelToUnit).
            var footPoint = new Vector2(bounds.center.x, bounds.min.y - 0.01f);

            TileRenderer[] tiles = FindObjectsByType<TileRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            TileRenderer physicsTile = null;
            float physicsTileTop = float.NegativeInfinity;
            Collider2D physicsCollider = null;

            TileRenderer visualTile = null;
            float visualTileTop = float.NegativeInfinity;

            for (int i = 0; i < tiles.Length; i++)
            {
                TileRenderer tr = tiles[i];
                if (tr == null || tr.TileData == null) continue;

                Collider2D collider = tr.GetComponent<Collider2D>();
                if (collider != null)
                {
                    Rect cr = WorldRect(collider.bounds);
                    if (Contains(cr, footPoint) && cr.yMax > physicsTileTop)
                    {
                        physicsTileTop = cr.yMax;
                        physicsTile = tr;
                        physicsCollider = collider;
                    }
                }

                SpriteRenderer sprite = tr.GetComponent<SpriteRenderer>();
                if (sprite != null && sprite.sprite != null)
                {
                    Rect vr = WorldRect(sprite.bounds);
                    if (Contains(vr, footPoint) && vr.yMax > visualTileTop)
                    {
                        visualTileTop = vr.yMax;
                        visualTile = tr;
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append("[collider] Probe — ").Append(player.name);

            sb.Append($"\n  body collider : x {bounds.min.x * PixelsPerUnit:0.#}..{bounds.max.x * PixelsPerUnit:0.#}" +
                      $"  y {bounds.min.y * PixelsPerUnit:0.#}..{bounds.max.y * PixelsPerUnit:0.#}" +
                      $"  (w {bounds.size.x * PixelsPerUnit:0.#} px, h {bounds.size.y * PixelsPerUnit:0.#} px)");
            sb.Append($"\n  feet point    : ({footPoint.x * PixelsPerUnit:0.#}, {footPoint.y * PixelsPerUnit:0.#}) px");

            if (physicsTile == null)
            {
                sb.Append("\n  physics  tile : NONE — no tile collider contains the point under the feet.");
            }
            else
            {
                TileData d = physicsTile.TileData;
                float gapPixels = (bounds.min.y - physicsTileTop) * PixelsPerUnit;

                sb.Append($"\n  physics  tile : {Describe(physicsTile, d)}");
                sb.Append($"\n                  collider={physicsCollider.GetType().Name}" +
                          $" usedByEffector={physicsCollider.usedByEffector}" +
                          $" top={physicsTileTop * PixelsPerUnit:0.#} px");
                sb.Append($"\n                  gap feet->top = {gapPixels:0.#} px");
                sb.Append($"\n                  AS3 porog HERE = {TileQueryConstants.PlayerPorogGrounded:0.#} px" +
                          "  (grounded player, UnitPlayer.as:2530)");
                sb.Append("\n                  for contrast   :" +
                          $" non-player unit {UnitPorogPixels:0.#} px (Unit.as:278)" +
                          $" / walking player {TileQueryConstants.PlayerPorogWalk:0.#} px" +
                          " (UnitPlayer.as:2550)" +
                          $" / airborne player {TileQueryConstants.PlayerPorogJump:0.#} px only while" +
                          " running or holding Up (UnitPlayer.as:2533)");
            }

            sb.Append(visualTile == null
                ? "\n  visual   tile : NONE — nothing is drawn under the feet."
                : $"\n  visual   tile : {Describe(visualTile, visualTile.TileData)}" +
                  $" spriteTop={visualTileTop * PixelsPerUnit:0.#} px");

            sb.Append("\n  verdict       : ").Append(Verdict(
                physicsTile, visualTile, physicsTileTop, bounds.min.y));

            return sb.ToString();
        }

        // ── Gaps: the same question asked of every tile at once ──────────────

        /// <summary>Alpha above which a texture row counts as "drawn".</summary>
        private const float OpaqueThreshold = 0.05f;

        private struct GapStats
        {
            public int Count;
            public int SpriteOnly;      // has a sprite, no collider
            public int ColliderOnly;    // has a collider, no sprite
            public int Unreadable;      // sprite texture not readable, so not measured
            public float Min;
            public float Max;
            public float Sum;

            public float Mean => Count > 0 ? Sum / Count : 0f;

            public void Add(float gapPixels)
            {
                if (Count == 0) { Min = gapPixels; Max = gapPixels; }
                else { if (gapPixels < Min) Min = gapPixels; if (gapPixels > Max) Max = gapPixels; }
                Count++;
                Sum += gapPixels;
            }
        }

        /// <summary>
        /// For every tile in the scene, compare the collider's top edge with the <b>visible</b> top edge
        /// of the sprite — the topmost texture row that is not transparent — grouped by physics type.
        ///
        /// <para><b>Why this exists, and why it is not just <see cref="Probe"/> in a loop.</b> The
        /// probe answers "what is under the player right now", which needs the player to be standing in
        /// the interesting place and produces one number per screenshot. This answers "is the drawn
        /// surface where the collider says it is" for <i>every</i> tile, so the §5 discriminator in
        /// <c>docs/Research/SHELF_CATWALK_DIAGNOSIS_2026-09-30.md</c> ("is the gap the same on a plain
        /// floor tile as on a catwalk?") is a single command rather than a walk-around.</para>
        ///
        /// <para><b>The one measurement the earlier passes could not make.</b> Everything so far used
        /// <c>SpriteRenderer.bounds</c>, which is the full 40x40 cell rect and therefore always lands
        /// exactly on the collider top — it cannot see a texture whose opaque region does not fill its
        /// cell. This samples the texture's alpha, so a plank drawn only in the top 29 of 40 rows, or
        /// drawn offset, shows up as a number instead of a guess. The earlier screenshot measurement
        /// was taken at roughly 0.5 screen px per game px, where the 4-6 px question at stake is 2-3
        /// screen px — below what the image can resolve. This is the same question asked at full
        /// resolution.</para>
        ///
        /// <para><b>How to read it.</b> A positive gap means the visible surface sits <i>below</i> the
        /// collision surface, so a body correctly resting on the collider appears to stand on air —
        /// the reported symptom, as arithmetic. Near-zero everywhere means the art and the collider
        /// agree and the fault is elsewhere. Grouped by physics type so "only the shelves" and "every
        /// tile" are different answers, which is exactly the fork §5 describes.</para>
        /// </summary>
        public static string Gaps()
        {
            TileRenderer[] tiles = FindObjectsByType<TileRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            var byType = new Dictionary<TilePhysicsType, GapStats>();
            var unreadableTypes = new HashSet<string>();
            int considered = 0;

            for (int i = 0; i < tiles.Length; i++)
            {
                TileRenderer tr = tiles[i];
                if (tr == null || tr.TileData == null) continue;

                TilePhysicsType type = tr.TileData.physicsType;
                if (!byType.TryGetValue(type, out GapStats stats)) stats = new GapStats();

                Collider2D collider = tr.GetComponent<Collider2D>();
                SpriteRenderer sprite = tr.GetComponent<SpriteRenderer>();
                bool hasSprite = sprite != null && sprite.sprite != null;

                if (collider == null && hasSprite) stats.SpriteOnly++;
                else if (collider != null && !hasSprite) stats.ColliderOnly++;

                if (collider != null && hasSprite)
                {
                    considered++;
                    if (TryGetOpaqueTopWorld(sprite, out float visibleTop))
                    {
                        float colliderTop = WorldRect(collider.bounds).yMax;
                        stats.Add((colliderTop - visibleTop) * PixelsPerUnit);
                    }
                    else
                    {
                        stats.Unreadable++;
                        if (sprite.sprite.texture != null && !sprite.sprite.texture.isReadable)
                        {
                            unreadableTypes.Add($"{type}:{sprite.sprite.texture.name}");
                        }
                    }
                }

                byType[type] = stats;
            }

            var sb = new StringBuilder();
            sb.Append("[collider] Tile surface gaps — collider top minus VISIBLE sprite top, in game px.");
            sb.Append("\n  positive = the drawn surface is BELOW the collision surface");
            sb.Append(" (a body on the collider looks like it stands on air).");
            sb.Append($"\n  tiles with both a collider and a sprite: {considered}");

            if (considered == 0)
            {
                sb.Append("\n  NOTHING to measure. Either no tile has a collider, or none has a sprite,");
                sb.Append("\n  or the room is not built. Try `col probe` for the tile under the player.");
                return sb.ToString();
            }

            sb.Append("\n");
            sb.Append("\n  physicsType   count   min      mean     max     flags");

            // Ordered so the interesting types read first rather than in enum order.
            var order = new[]
            {
                TilePhysicsType.Platform, TilePhysicsType.Wall,
                TilePhysicsType.Stair, TilePhysicsType.Air,
            };

            foreach (TilePhysicsType type in order)
            {
                if (!byType.TryGetValue(type, out GapStats s)) continue;
                AppendGapRow(sb, type.ToString(), s);
                byType.Remove(type);
            }

            foreach (KeyValuePair<TilePhysicsType, GapStats> leftover in byType)
            {
                AppendGapRow(sb, leftover.Key.ToString(), leftover.Value);
            }

            if (unreadableTypes.Count > 0)
            {
                sb.Append("\n  UNREADABLE textures (not measured): ");
                foreach (string name in unreadableTypes) sb.Append(name).Append(' ');
            }

            sb.Append("\n  note: only tiles with BOTH a collider and a sprite are measured.");
            sb.Append(" Air tiles have no collider, so they appear only as counts.");
            return sb.ToString();
        }

        private static void AppendGapRow(StringBuilder sb, string label, GapStats s)
        {
            string flags = (s.SpriteOnly > 0 ? $"spriteOnly={s.SpriteOnly} " : string.Empty) +
                           (s.ColliderOnly > 0 ? $"colliderOnly={s.ColliderOnly} " : string.Empty) +
                           (s.Unreadable > 0 ? $"unreadable={s.Unreadable}" : string.Empty);

            sb.Append($"\n  {label,-12}  {s.Count,5}");
            if (s.Count == 0)
            {
                sb.Append("       —        —        —     ").Append(flags);
                return;
            }

            sb.Append($"  {s.Min,7:0.##}  {s.Mean,7:0.##}  {s.Max,7:0.##}   ").Append(flags);
        }

        /// <summary>
        /// The world-space Y of the topmost non-transparent row of a sprite, or false if the texture
        /// cannot be read or is fully transparent.
        ///
        /// <para>Reads only the sprite's own sub-rect, not the whole texture: tiles are packed into a
        /// shared 2048x2048 atlas, and reading that per tile would be 4M colours each.</para>
        /// </summary>
        private static bool TryGetOpaqueTopWorld(SpriteRenderer spriteRenderer, out float worldTop)
        {
            worldTop = 0f;

            Sprite sprite = spriteRenderer.sprite;
            if (sprite == null) return false;

            Texture2D texture = sprite.texture;
            if (texture == null || !texture.isReadable) return false;

            Rect rect = sprite.textureRect;
            int x0 = Mathf.Clamp(Mathf.RoundToInt(rect.x), 0, Mathf.Max(0, texture.width - 1));
            int y0 = Mathf.Clamp(Mathf.RoundToInt(rect.y), 0, Mathf.Max(0, texture.height - 1));
            int w = Mathf.Clamp(Mathf.RoundToInt(rect.width), 1, texture.width - x0);
            int h = Mathf.Clamp(Mathf.RoundToInt(rect.height), 1, texture.height - y0);

            Color[] pixels = texture.GetPixels(x0, y0, w, h);   // row 0 is the BOTTOM row
            if (pixels == null || pixels.Length < w * h) return false;

            int rowsFromTop = -1;
            for (int row = h - 1; row >= 0 && rowsFromTop < 0; row--)
            {
                int rowBase = row * w;
                for (int x = 0; x < w; x++)
                {
                    if (pixels[rowBase + x].a > OpaqueThreshold)
                    {
                        rowsFromTop = (h - 1) - row;
                        break;
                    }
                }
            }

            if (rowsFromTop < 0) return false;   // fully transparent

            Bounds bounds = spriteRenderer.bounds;
            worldTop = bounds.max.y - (rowsFromTop / (float)h) * bounds.size.y;
            return true;
        }

        private static string Describe(TileRenderer tr, TileData data)
        {
            if (data == null) return $"({tr.GridPosition.x},{tr.GridPosition.y}) <no TileData>";

            return $"({data.gridPosition.x},{data.gridPosition.y})" +
                   $" physics={data.physicsType}" +
                   $" heightLevel={data.heightLevel}" +
                   $" slope={data.slopeType}";
        }

        private static string Verdict(TileRenderer physicsTile, TileRenderer visualTile,
                                      float physicsTileTop, float feetY)
        {
            if (physicsTile == null && visualTile == null)
                return "NOTHING here — neither a collider nor a sprite. The feet are in empty space.";

            if (physicsTile == null)
                return "SPRITE ONLY — something is drawn here but no collider is. " +
                       "This is the 'standing on air' shape: the floor is visible and not solid.";

            if (visualTile == null)
                return "COLLIDER ONLY — the feet are supported by a tile that is not drawn. " +
                       "Invisible floor.";

            if (physicsTile != visualTile)
                return "DISAGREEMENT — physics and the sprite put a DIFFERENT tile under the feet.";

            // The yardstick is the PLAYER's allowance, not the unit's. The probe always describes the
            // player, and AS3 gives a grounded player porog = 0 (UnitPlayer.as:2530) where a generic
            // unit gets 10 (Unit.as:278). Judging the player's gap against the unit's 10 meant a
            // 4-6 px float read as "SUPPORTED — within porog", i.e. the diagnostic silently agreed
            // with the bug.
            //
            // Get the DIRECTION right, because it is easy to invert: porog is not "how far a mover may
            // be lifted", it is **how far above the surface a shelf still counts**. AS3 seats a mover
            // while `Y2 - phY1 <= porog` (Unit.as:2131) and stops colliding with the shelf entirely
            // beyond it (`Y2 - porog > phY1`, Unit.as:2578). So a gap LARGER than porog means the feet
            // are outside the shelf's influence and AS3 would NOT seat them — the mover should be
            // falling, not resting. Resting there is therefore the anomaly, and that is what this
            // branch reports.
            float gapPixels = (feetY - physicsTileTop) * PixelsPerUnit;
            float allowancePixels = TileQueryConstants.PlayerPorogGrounded;

            if (gapPixels > allowancePixels + SupportTolerancePixels)
                return $"FLOATING by {gapPixels:0.#} px — beyond the grounded player's " +
                       $"{allowancePixels:0.#} px porog (UnitPlayer.as:2530). AS3 seats a mover only " +
                       "while it is within porog of the surface (Unit.as:2131) and stops colliding " +
                       "with the shelf beyond that (Unit.as:2578), so a player RESTING here should be " +
                       $"falling. The unit's {UnitPorogPixels:0.#} px is a different mover's allowance.";

            return $"SUPPORTED — collider and sprite agree, gap {gapPixels:0.#} px is within the " +
                   $"player's {allowancePixels:0.#} px porog, so AS3 seats the feet on the surface.";
        }

        private static bool Contains(Rect rect, Vector2 point)
        {
            return point.x >= rect.xMin && point.x <= rect.xMax &&
                   point.y >= rect.yMin && point.y <= rect.yMax;
        }

        // ── On-screen legend ─────────────────────────────────────────────────

        /// <summary>
        /// A small readout so the screenshot explains itself. Without it a screenshot of the
        /// overlay is ambiguous: "no green boxes on the catwalk" is a finding only if the platform
        /// filter was on, and the image alone cannot say which filter was active.
        /// </summary>
        private void OnGUI()
        {
            if (!_legendVisible) return;

            PfeDebugSettings settings = Settings;
            if (settings == null) return;

            if (_legendStyle == null)
            {
                _legendStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    richText = true,
                    alignment = TextAnchor.UpperLeft,
                    wordWrap = false
                };

                // The legend sits on its own dark plate, so the text is forced white rather than
                // inherited: the runtime IMGUI skin's default label colour is dark, which on a dark
                // plate over a dark game is the one combination that cannot be read in a screenshot
                // — and a screenshot is the whole point of this overlay.
                _legendStyle.normal.textColor = Color.white;
                _legendStyle.hover.textColor = Color.white;
                _legendStyle.active.textColor = Color.white;
            }

            DebugOverlayChannel channels = settings.EnabledOverlays;

            string tiles = (channels & DebugOverlayChannel.Tiles) != 0
                ? ColliderDebugFilters.Format(settings.TileColliderFilter)
                : "off";
            string units = (channels & DebugOverlayChannel.Units) != 0
                ? ColliderDebugFilters.Format(settings.UnitColliderFilter)
                : "off";

            var sb = new StringBuilder();

            // The channel list is the important line: it is what makes a screenshot unambiguous.
            // "No green boxes on the catwalk" is a finding only if `tiles` was on AND `platform` was
            // in the filter, and the image alone cannot say either.
            sb.Append("<b>[collider]</b> on: ")
              .Append(DebugOverlayChannels.Format(channels))
              .Append("    <i>F5 tiles</i>=").Append(tiles)
              .Append("  <i>F6 units</i>=").Append(units);

            // "matched" and "WITHOUT a collider" are whole-room counts; "drawn" is view-limited.
            // Labelled explicitly, because "41 matched, 37 drawn" otherwise invites the subtraction
            // 41 - 37 = 4 and the conclusion that four tiles lost their collider.
            sb.Append($"\ntiles: {_tilesMatched} matched in the room, {_tilesDrawn} drawn in view, " +
                      $"{_tilesWithoutCollider} <color=#ff00ff>WITHOUT a collider</color>");

            sb.Append($"\nunits: {_unitsDrawn} drawn ({_playersDrawn} player, {_npcsDrawn} npc)" +
                      $"    room boundary: {(_transitionsDrawn ? "drawn" : "not drawn")}");
            if (_quadsExhausted)
            {
                sb.Append($"   <color=#ff4040>TRUNCATED at {MaxQuadsPerRefresh} quads</color>");
            }

            sb.Append("\nkey: <color=#ff4040>wall</color> " +
                      "<color=#40ff59>platform/shelf</color> " +
                      "<color=#ffe633>stair</color> " +
                      "<color=#a6a6c7>air</color> " +
                      "<color=#ff00ff>MISMATCH / boundary</color> " +
                      "<color=#33e5ff>player</color> " +
                      "<color=#ff991f>npc</color> " +
                      "<color=#59ffff>llp2d chain</color> " +
                      "| white bar = feet | thin white box = sprite, only where it differs");

            const float width = 640f;
            const float height = 92f;

            // Bottom-left, not top-left. (10, 10) is already owned by RoomStreamingManager.OnGUI and
            // (10, 200)/(10, 220) by TileQueryOverlay and RoomObjectPool; the top-right belongs to
            // SimDebugOverlay. The bottom of the screen is the one corner nothing else claims.
            var box = new Rect(8f, Screen.height - height - 8f, width, height);

            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = previous;

            GUI.Label(new Rect(box.x + 6f, box.y + 4f, width - 12f, height - 8f), sb.ToString(), _legendStyle);
        }
    }
}
