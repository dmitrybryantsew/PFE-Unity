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
    /// <para><b>What the tile channel draws, and why one box.</b> Since Stage D a tile has no
    /// GameObject at all — the grid is the collision — so the tile channel draws the box the tile
    /// <i>data</i> describes (<see cref="TileData.GetBounds"/> through the room origin), which is the
    /// box the physics actually queries. What the Box2D side of that same geometry looks like is the
    /// physics channel's job (<c>col on physics</c>); comparing the two is how a chain-mirror drift
    /// is seen now.</para>
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
            sb.Append($"counts: tiles {Instance._tilesMatched} matched in the mirrored rooms, " +
                      $"{Instance._tilesDrawn} drawn in view");
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

        /// <summary>
        /// A tile as this overlay can see it since Stage D: no GameObject exists — the tile's data
        /// and the world rect derived from it (<see cref="TileData.GetBounds"/> plus the room origin)
        /// are the whole population, read from the same mirrored rooms the chain geometry is built
        /// from. Probing scene objects would find zero tiles, and a diagnostic that silently measures
        /// nothing is worse than no diagnostic.
        /// </summary>
        private sealed class TileProbe
        {
            public Rect rect;
            public TileData data;
        }

        /// <summary>
        /// Every solid tile of every mirrored room, as data rects. Rooms come from
        /// <see cref="IPhysicsWorldService.MirroredRooms"/> — the same set the chain mirror covers —
        /// so a room the physics does not know about is not drawn either, and the two channels cannot
        /// disagree about which room exists.
        /// </summary>
        private static List<TileProbe> CollectTiles()
        {
            var probes = new List<TileProbe>(1024);

            IPhysicsWorldService service = ResolvePhysicsWorldService();
            if (service == null) return probes;

            foreach (var entry in service.MirroredRooms)
            {
                RoomInstance room = entry.Key;
                if (room == null || room.tiles == null) continue;

                float originX = WorldCoordinates.RoomOriginPixelX(room.landPosition.x, room.borderOffset);
                float originY = WorldCoordinates.RoomOriginPixelY(room.landPosition.y, room.borderOffset);
                const float toUnits = TileQueryConstants.PixelToUnit;

                for (int x = 0; x < room.width; x++)
                {
                    for (int y = 0; y < room.height; y++)
                    {
                        TileData tile = room.tiles[x, y];
                        if (tile == null || tile.physicsType == TilePhysicsType.Air) continue;

                        Rect solidLocal = tile.GetBounds();
                        probes.Add(new TileProbe
                        {
                            data = tile,
                            rect = new Rect(
                                (solidLocal.x + originX) * toUnits,
                                (solidLocal.y + originY) * toUnits,
                                solidLocal.width * toUnits,
                                solidLocal.height * toUnits),
                        });
                    }
                }
            }

            return probes;
        }

        private void DrawTiles(ColliderDebugTileFilter filter, Rect view)
        {
            if (filter == ColliderDebugTileFilter.None) return;

            List<TileProbe> tiles = CollectTiles();

            for (int i = 0; i < tiles.Count; i++)
            {
                TileProbe tr = tiles[i];

                TileData data = tr.data;
                if (data == null) continue;
                if (!ColliderDebugFilters.Matches(filter, data.physicsType)) continue;

                _tilesMatched++;

                // Counted BEFORE the view test, so the number in the legend is a property of the
                // room rather than of where the camera happens to be. A count that silently changes
                // when you scroll is a count that cannot be compared between two screenshots.
                if (!view.Overlaps(tr.rect)) continue;

                EmitBox(tr.rect, TileLineWidth, ColorForPhysicsType(data.physicsType));

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

            List<TileProbe> tiles = CollectTiles();

            TileProbe physicsTile = null;
            float physicsTileTop = float.NegativeInfinity;

            // The feet question is answered from the tile data alone since Stage D: the probe rect IS
            // the solid box the physics queries (TileData.GetBounds through the room origin), and no
            // tile carries a sprite — the quarter renderer draws the terrain as a mesh, so the
            // visual half of this probe is permanently "not applicable" rather than measured.
            for (int i = 0; i < tiles.Count; i++)
            {
                TileProbe tr = tiles[i];
                if (tr == null || tr.data == null) continue;

                if (Contains(tr.rect, footPoint) && tr.rect.yMax > physicsTileTop)
                {
                    physicsTileTop = tr.rect.yMax;
                    physicsTile = tr;
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
                sb.Append("\n  physics  tile : NONE — no solid tile contains the point under the feet.");
            }
            else
            {
                float gapPixels = (bounds.min.y - physicsTileTop) * PixelsPerUnit;

                sb.Append($"\n  physics  tile : {Describe(physicsTile)}");
                sb.Append($"\n                  top={physicsTileTop * PixelsPerUnit:0.#} px (the tile data's own box)");
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

            sb.Append("\n  visual   tile : not applicable — no tile in this room has a sprite " +
                      "(the quarter terrain renderer draws the room as a mesh)");

            sb.Append("\n  verdict       : ").Append(Verdict(
                physicsTile, physicsTileTop, bounds.min.y));

            return sb.ToString();
        }

        // ── Gaps: retired with the per-tile objects ──────────────────────────

        /// <summary>
        /// Retired with the per-tile objects. The measurement compared each tile's collider top with
        /// the topmost opaque row of its own sprite; since Stage D a tile has neither — the collision
        /// is the tile data and the art lives in the quarter renderer's mesh — so there is nothing to
        /// pair and nothing to sample. Use <see cref="Probe"/> for the tile under the player.
        /// </summary>
        public static string Gaps()
        {
            return "[collider] Tile surface gaps: retired — per-tile colliders and sprites went with " +
                   "Stage D. The collision box is the tile data (see `col tiles` / `col probe`); the " +
                   "drawn surface lives in the quarter renderer's terrain mesh.";
        }

        private static string Describe(TileProbe tr)
        {
            TileData data = tr != null ? tr.data : null;
            if (data == null) return "<no TileData>";

            return $"({data.gridPosition.x},{data.gridPosition.y})" +
                   $" physics={data.physicsType}" +
                   $" heightLevel={data.heightLevel}" +
                   $" slope={data.slopeType}";
        }

        private static string Verdict(TileProbe physicsTile, float physicsTileTop, float feetY)
        {
            if (physicsTile == null)
                return "NOTHING here — no solid tile contains the point under the feet. " +
                       "The feet are in empty space.";

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

            return $"SUPPORTED — gap {gapPixels:0.#} px is within the player's " +
                   $"{allowancePixels:0.#} px porog, so AS3 seats the feet on the surface.";
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

            // "matched" is a whole-room count; "drawn" is view-limited. Labelled explicitly, because
            // "41 matched, 37 drawn" otherwise invites the subtraction 41 - 37 = 4 and the conclusion
            // that four tiles vanished.
            sb.Append($"\ntiles: {_tilesMatched} matched in the mirrored rooms, {_tilesDrawn} drawn in view");

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
