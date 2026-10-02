using System.Collections.Generic;
using System.Text;
using UnityEngine;
using VContainer;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Systems.RPG;
using PFE.Systems.Telekinesis;

namespace PFE.Entities.Player
{
    /// <summary>
    /// Player telekinesis controller — AS3's <c>UnitPlayer.actTele</c>, hold tick, and <c>throwTele</c>.
    ///
    /// <para>Features:
    /// <list type="bullet">
    /// <item>Q press edge: grabs a liftable prop under the cursor, or drops the held prop. A prop in
    /// flight is grabbable — AS3's <c>actTele</c> does not test <c>stay</c>.</item>
    /// <item>Holding: updates the held prop's target to the cursor and drains mana from <see cref="CharacterStats.manaHp"/>.</item>
    /// <item>E / Action key: telekinetic throw imparting velocity and consuming mana.</item>
    /// <item>Automatic drop: triggers if target is out of range (<c>teleDist * 1.2</c>), mana is exhausted, or prop becomes unliftable.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Q is shared with the teleport and the two are not exclusive.</b> Holding Q runs the
    /// grab once on the press edge and charges the teleport on every frame; releasing fires the
    /// teleport if the charge is long enough (<c>UnitPlayer.as:2141-2176</c>). This class must not be
    /// used to suppress the charge — see <see cref="OnTeleportKeyPressed"/>.</para>
    ///
    /// <para><b>The room comes from the <c>LandMap</c>, not from <see cref="SetRoom"/>.</b>
    /// <c>SetRoom</c> is a fixture seam; gameplay gets its room via <see cref="SetLandMap"/>, wired in
    /// <c>MapBridge.SpawnPlayer</c>. See <see cref="SetLandMap"/> for why this component cannot rely
    /// on <c>[Inject]</c>.</para>
    /// </summary>
    public class PlayerTelekinesisController : MonoBehaviour
    {
        private CharacterStats _stats;
        private LandMap _landMap;
        private ITileQueryService _tileQueryService;
        private RoomInstance _currentRoom;
        private Camera _mainCamera;

        // The debug "infinite mana" switch lives on PlayerLocomotionAbilities, and it guards only
        // UnitStats.Mana -- the pool levitation and teleport use. Telekinesis reads a DIFFERENT pool,
        // CharacterStats.manaHp, so the switch silently did not apply here: the hold still drained, and
        // because nothing in the project regenerates manaHp, enough cumulative holding pushes it below
        // MinManaToGrab and every later grab is refused -- permanently, and with no way back short of a
        // restart. That is a baffling failure mode for a switch whose stated purpose is "test without
        // mana drain", and it presents as "the grab stopped working".
        private PlayerLocomotionAbilities _abilities;

        // The line-of-sight query is normally DERIVED from CurrentRoom instead of held, because a
        // query handed over once wraps one specific room and would go stale at the first door — see
        // SetLandMap. _tileQueryService stays as an explicit override for tests.
        private ITileQueryService _derivedTileQuery;
        private RoomInstance _derivedTileQueryRoom;

        private ObjectInstance _heldObject;
        private Vector2 _cursorRoomLocalPixels;
        private bool _cursorSetExternally;
        private Vector2? _explicitPlayerPositionPixels;

        // Counts UpdateHold calls so the hold trace can report about once a second instead of once a
        // frame. Only ever touched while the trace is on.
        private int _holdTraceTick;

        // The previous `tele probe` heartbeat sample. A single tick count proves nothing -- a room that
        // is stepping and a room that stepped once at load look the same. Two samples a second apart
        // give a delta, and a delta of zero is the whole answer. -1 means "no sample yet".
        private int _lastProbeTickCount = -1;
        private float _lastProbeTime;

        // The outcome of the most recent Q press, and a running count of them.
        //
        // <para><b>Why this lives on the component and not only in the log.</b> The trace is a stream
        // the reader has to copy correctly, and a console window is exactly where "did the press grab
        // it" gets lost: the unconditional `Q PRESSED` line sits above the interesting one, so a paste
        // that starts at `TryGrab: room=` has already dropped the context, and an abridged paste is
        // indistinguishable from a missing line. Keeping the verdict in state means `tele probe`
        // reports it in one self-contained block, with no dependence on what got copied.</para>
        private int _qPressCount;
        private string _lastKeyPress = "(no Q press since load)";

        // AS3's `UnitPlayer.teleReady` (UnitPlayer.as:145, :2143-2172) -- the press-edge latch. False
        // means "the next press is a fresh edge"; true means "this press has already been acted on and
        // the key is still held". Cleared on release, set when the action fires. See
        // OnTeleportKeyPressed for why the port needs it and what its absence cost.
        private bool _teleReady;

        // The outcome of the most recent grab attempt, recorded even when the trace is off.
        private string _lastGrabOutcome = "(no grab attempted since load)";

        // ── Hold post-mortem, kept so `tele probe` is useful AFTER the prop has been dropped ───────
        //
        // The natural thing to do is grab, watch the box not move, press Q again (which DROPS it) and
        // only then type `tele probe`. At that point `_heldObject` is null and [7] says "<none>", so the
        // one run that had the evidence in hand reports nothing about it. These counters survive the
        // drop and answer the two questions that matter:
        //
        //   * did UpdateHold run AT ALL while the prop was held? A hold whose `_holdUpdateTicks` is 0
        //     means this component's Update never drove it, so the target stayed frozen at whatever
        //     TrySetTelekineticHold wrote -- the grab-time cursor. If that cursor happened to be inside
        //     the 15 px deadzone, the prop can never move and no amount of aiming will change it.
        //   * if it did run, how much mana did it drain? `allDManaMult` defaults to 1.0, so a hold that
        //     lasted more than a fraction of a second and shows `_holdManaDrained = 0` is itself proof
        //     the drain never executed.
        //
        // The first and last target together also show whether the target was tracking the cursor or
        // standing still, which distinguishes "Update is not running" from "Update is running and the
        // cursor is simply inside the deadzone" -- the same visible symptom, opposite fixes.
        private ObjectInstance _lastHeldObject;
        private string _lastHeldObjectId = "(nothing has been held yet)";
        private int _holdEntryTicks;
        private int _holdUpdateTicks;
        private int _holdTargetWrites;
        private Vector2 _holdFirstTarget;
        private Vector2 _holdLastTarget;
        private float _holdManaDrained;

        // Counts this component's own Update calls. "UpdateHold never ran" and "Update never ran" are
        // different bugs with different fixes, and nothing distinguished them before this.
        private int _updateCallCount;

        public ObjectInstance HeldObject => _heldObject;
        public bool IsHoldingObject => _heldObject != null;

        /// <summary>
        /// True when the debug "infinite mana" switch is on, meaning telekinesis must neither spend nor
        /// refuse on mana. See <c>_abilities</c> for why this component has to ask at all: the switch
        /// guards <c>UnitStats.Mana</c>, and telekinesis uses <c>CharacterStats.manaHp</c>.
        /// </summary>
        private bool InfiniteMana
        {
            get
            {
                if (_abilities == null)
                {
                    _abilities = GetComponent<PlayerLocomotionAbilities>()
                                 ?? GetComponentInParent<PlayerLocomotionAbilities>();
                }

                return _abilities != null && _abilities.InfiniteMana;
            }
        }

        /// <summary>
        /// True when the debug "grab anything" switch is on, meaning the authored mass limit and range
        /// are ignored so any dynamic prop is reachable.
        ///
        /// <para><b>Only half of the switch is applied here.</b> The capability half lives in
        /// <c>ObjectInstance.SupportsTelekinesis</c>, because four gates there read that one predicate
        /// and they must widen together. The massa and teleDist terms live in the player, so they widen
        /// here. Both halves read the same flag, so they cannot disagree.</para>
        /// </summary>
        private bool GrabAnything => PFE.Core.DebugOverlays.Settings?.TelekinesisGrabAnything == true;

        /// <summary>
        /// The range used when <see cref="GrabAnything"/> is on.
        ///
        /// <para>A large <b>finite</b> number rather than <c>float.MaxValue</c>, deliberately:
        /// <c>MustDrop</c> multiplies the range by 1.2, and <c>float.MaxValue * 1.2f</c> overflows to
        /// <c>+Infinity</c>. That happens to compare the way we want, but an overflow that silently means
        /// "no limit" is exactly the sort of thing that reads as a bug three sessions later. 1e18 squared
        /// pixels is ~1e9 px of reach — far past any room in the game.</para>
        /// </summary>
        private const float GrabAnythingDistanceSquared = 1e18f;

        public CharacterStats Stats
        {
            get
            {
                if (_stats == null)
                {
                    _stats = GetComponent<CharacterStats>() ?? GetComponentInParent<CharacterStats>();
                    if (_stats == null)
                    {
                        _stats = gameObject.AddComponent<CharacterStats>();
                    }
                }
                return _stats;
            }
            set => _stats = value;
        }

        /// <summary>
        /// The room the player is standing in right now.
        ///
        /// <para><b>Read it from <see cref="LandMap"/> in game; <c>_currentRoom</c> is a test seam.</b>
        /// An explicitly-set room wins over the land map's, so anything that calls
        /// <see cref="SetRoom"/> once and stops has frozen the controller on that room for the rest
        /// of the session. That is the intended behaviour for a fixture that builds one room and
        /// never transitions, and precisely why no gameplay code calls it: an intra-land room change
        /// goes through <c>RoomTransitionManager</c> &rarr; <c>landMap.SetCurrentRoom</c> and never
        /// re-enters <c>MapBridge.SpawnPlayer</c>, so a room captured there would be the room the
        /// player left. <see cref="SetLandMap"/> is what keeps this live.</para>
        /// </summary>
        public RoomInstance CurrentRoom
        {
            get => _currentRoom ?? _landMap?.currentRoom;
            set => _currentRoom = value;
        }

        [Inject]
        public void Construct(LandMap landMap = null, ITileQueryService tileQueryService = null)
        {
            _landMap = landMap;
            _tileQueryService = tileQueryService;
        }

        /// <summary>
        /// Hands over the <see cref="LandMap"/> so <see cref="CurrentRoom"/> tracks room transitions.
        ///
        /// <para><b>Why the land map and not the room.</b> <c>MapBridge.SpawnPlayer</c> runs once per
        /// land load, but a door within the same land never re-enters it — it goes through
        /// <c>RoomTransitionManager</c>, which calls <c>landMap.SetCurrentRoom</c> and nothing else.
        /// So a room handed over at spawn time would be correct until the first door and wrong
        /// afterwards. Following the land map costs one property read and cannot go stale.</para>
        ///
        /// <para><b>Why this is called from <c>MapBridge</c> and not done by <c>[Inject]</c>.</b>
        /// <see cref="Construct"/> does carry the same parameter, but this component is created by
        /// <c>PlayerController.Awake</c> via <c>AddComponent</c>, and VContainer's
        /// <c>ExistingComponentProvider</c> injects only the component it was handed — the sibling
        /// <c>PlayerController</c>. A component that is not itself registered is therefore never
        /// injected, and its <c>[Inject]</c> method never runs. Registering it instead would need it
        /// on the player prefab, which is asset work; <c>MapBridge</c> already wires this same
        /// GameObject's <c>TilePhysicsController</c> and <c>PlayerActionInteractor</c>, so it is the
        /// established place for exactly this.</para>
        /// </summary>
        public void SetLandMap(LandMap landMap)
        {
            _landMap = landMap;
        }

        /// <summary>
        /// The tile query for the room the player is actually in.
        ///
        /// <para>An explicitly-set service (tests) always wins. Otherwise it is built from
        /// <see cref="CurrentRoom"/> and rebuilt when that room changes, which is what keeps
        /// <see cref="TryGrab"/>'s line-of-sight test pointing at the room the player is in. AS3
        /// gets this for free: its <c>loc.isLine</c> reads the current <c>loc</c>, which is swapped
        /// on every room change.</para>
        /// </summary>
        private ITileQueryService ResolveTileQuery()
        {
            if (_tileQueryService != null)
            {
                return _tileQueryService;
            }

            RoomInstance room = CurrentRoom;
            if (room == null)
            {
                return null;
            }

            if (_derivedTileQuery == null || !ReferenceEquals(_derivedTileQueryRoom, room))
            {
                _derivedTileQueryRoom = room;
                _derivedTileQuery = new UnifiedTileQueryService(room);
            }

            return _derivedTileQuery;
        }

        /// <summary>
        /// Hands over the mouse cursor as a <b>world</b> pixel position (that is what the camera projects
        /// into), and stores it in <b>room-local</b> pixels.
        ///
        /// <para><b>The conversion is here on purpose.</b> Everything this component compares a position
        /// against lives in room-local pixels: <c>ObjectInstance.position</c>, the candidate filter, and
        /// the <c>telekineticTarget</c> the hold step chases. The evidence is in the map layer --
        /// <c>RoomInstance.CheckCollision</c> is documented as taking <i>room-local</i> coordinates and
        /// adds the room origin itself, and <c>ClampToRoomBounds</c> clamps to
        /// <c>[0, room.width * TILE_SIZE]</c>. Meanwhile the player and the cursor are world pixels
        /// (<c>MapBridge</c> spawns at <c>room.landPosition + tile</c>). Mixing the two silently offsets
        /// every distance by the room origin -- a full 1920 px for a room at <c>landPosition.x = 1</c> --
        /// so every prop reads as "too far" no matter where the player aims. Doing it at this single
        /// boundary keeps the rest of the file free of the distinction.</para>
        /// </summary>
        public void SetCursorWorldPixels(Vector2 cursorPixels)
        {
            _cursorRoomLocalPixels = WorldCoordinates.WorldToLocal(cursorPixels);
            _cursorSetExternally = true;
        }

        public void SetPlayerPositionPixels(Vector2 playerPixels)
        {
            _explicitPlayerPositionPixels = playerPixels;
        }

        public void SetRoom(RoomInstance room)
        {
            _currentRoom = room;
        }

        public void SetTileQueryService(ITileQueryService service)
        {
            _tileQueryService = service;
        }

        private void Awake()
        {
            _mainCamera = Camera.main;
            _stats = GetComponent<CharacterStats>() ?? GetComponentInParent<CharacterStats>();

            // Start a fresh flight log at the project root. See TelekinesisRecorder for why this is a
            // file and not a console line.
            TelekinesisRecorder.Begin(
                "=== telekinesis flight log ===\n" +
                $"session   {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"unity     {Application.unityVersion}   platform {Application.platform}");
        }

        private void OnDisable()
        {
            Drop("component disabled");
        }

        private void OnDestroy()
        {
            Drop("component destroyed");
        }

        /// <summary>
        /// Handles the teleport key (Q) state transition.
        ///
        /// <para><b>The caller must NOT use the return value to suppress the teleport charge.</b> In
        /// AS3 the grab and the charge run side by side: <c>actTele()</c> fires once on the press edge
        /// while <c>t_port</c> increments every frame the key is held, and releasing fires
        /// <c>actPort()</c> if the charge reached <c>portTime</c> (<c>UnitPlayer.as:2141-2176</c>).
        /// <c>PlayerController</c> used to cancel the charge whenever this returned true, which meant
        /// that the moment grabbing started working, Q could no longer teleport at all.</para>
        /// </summary>
        /// <param name="isStarted">True on press edge, false on release.</param>
        /// <returns>True if telekinesis acted on this edge (grabbed or dropped). Informational only.</returns>
        public bool OnTeleportKeyPressed(bool isStarted)
        {
            if (!isStarted)
            {
                // AS3 clears the latch in the else branch (UnitPlayer.as:2172), which is what re-arms
                // the next press. This is the only thing that clears it.
                _teleReady = false;
                TelekinesisTrace.Log("Q RELEASED (teleport release edge -- telekinesis does nothing on release)");
                TelekinesisRecorder.Write("[EDGE]  isStarted=false  latch cleared");
                return false;
            }

            // ── The press-edge latch — AS3's `teleReady` (UnitPlayer.as:2141-2157) ───────────────────
            //
            // AS3 acts on the press edge exactly ONCE per press-and-hold. Its `keyTele` branch is
            // entered on EVERY frame the key is held, so the action sits behind `if(!teleReady)`:
            //
            //     if(keyTele && ...) { if(!teleReady) { actTele(); teleReady = true; } ++t_port; }
            //     else               { if(teleReady) { ...actPort(); } teleReady = false; }
            //
            // — the action fires once, while the charge `t_port` keeps accumulating every frame.
            //
            // The port never had this latch, and it matters because the port does not read a held
            // boolean: it is driven by Teleport.started/canceled EVENTS. So any repeat of the press edge
            // ran the toggle twice. The first edge grabbed the prop and the second dropped it again —
            // and since both arrive within a frame, UpdateHold never got a single frame to run, so the
            // target stayed frozen at the grab-time cursor and the prop could never follow the mouse.
            // That is precisely the observed symptom: "the grab works, the crate never follows".
            //
            // The latch is the oracle's own answer, so it fixes the symptom at its cause rather than
            // papering over whichever layer produced the duplicate edge.
            if (_teleReady)
            {
                TelekinesisTrace.Log("Q PRESSED again while the press is already latched -- ignored (AS3 teleReady)");
                TelekinesisRecorder.Write("[EDGE]  isStarted=true  IGNORED (this press was already acted on)");
                return false;
            }

            _teleReady = true;

            TelekinesisTrace.Log($"Q PRESSED. IsHoldingObject={IsHoldingObject}");
            TelekinesisRecorder.Write($"[EDGE]  isStarted=true  LATCHED  holding={IsHoldingObject}");

            string heldBefore = _heldObject != null ? _heldObject.objectId : "nothing";

            // Q press edge
            if (IsHoldingObject)
            {
                Drop("Q pressed while holding -- the key toggles");
                _qPressCount++;
                _lastKeyPress = $"#{_qPressCount} was holding {heldBefore} -> DROPPED it (the key toggles)";
                _lastGrabOutcome = "n/a -- this press released instead of grabbing";
                return true;
            }

            bool grabbed = TryGrab();
            _qPressCount++;
            _lastKeyPress = $"#{_qPressCount} holdingBefore={heldBefore} -> grabbed={grabbed}, " +
                            $"holdingAfter={(_heldObject != null ? _heldObject.objectId : "NOTHING")}";
            _lastGrabOutcome = grabbed
                ? $"SUCCEEDED -- holding {_heldObject?.objectId}"
                : "REFUSED or FAILED -- see the [tele] trace for the named reason";
            return grabbed;
        }

        /// <summary>
        /// Attempts to grab a telekinetic object under/near the cursor.
        ///
        /// <para><b>Every refusal traces its reason</b> when <c>tele on</c> is set (see
        /// <see cref="TelekinesisTrace"/>). That is not decoration: a bare <c>false</c> from here is
        /// indistinguishable from "Q never arrived", and the two have completely different fixes. This
        /// path stayed broken through several rounds of correct-looking edits precisely because the only
        /// observable was "nothing happened".</para>
        /// </summary>
        public bool TryGrab()
        {
            RoomInstance room = CurrentRoom;
            if (room == null || room.ObjectPhysicsLayer == null)
            {
                TelekinesisTrace.Log(room == null
                    ? "TryGrab REFUSED: CurrentRoom is null -- nothing handed this controller a LandMap, or the LandMap has no current room. This is the FIRST guard; everything below is unreachable."
                    : "TryGrab REFUSED: room.ObjectPhysicsLayer is null");
                return false;
            }

            CharacterStats stats = Stats;
            if (stats == null)
            {
                TelekinesisTrace.Log("TryGrab REFUSED: Stats is null");
                return false;
            }

            UpdateCursorPositionIfNeeded();

            Vector2 cursorPx = _cursorRoomLocalPixels;
            Vector2 playerFeetPx = GetPlayerPositionPixels();
            Vector2 playerEyePx = playerFeetPx + new Vector2(0f, 35f);

            float maxTeleMassa = stats.MaxTeleMassa;
            float teleDist = stats.TeleDist;
            float mana = stats.manaHp;

            // The debug infinite-mana switch has to cover THIS pool too -- see the _abilities field.
            bool infiniteMana = InfiniteMana;
            float manaForGate = infiniteMana ? TelekinesisMath.MinManaToGrab : mana;

            // "Grab anything" removes the two authored limits that live in the player -- the mass ceiling
            // and the reach. The capability half of the same switch is applied in
            // ObjectInstance.SupportsTelekinesis, so a DynamicThrowable prop passes the candidate filter
            // and TrySetTelekineticHold as well.
            bool grabAnything = GrabAnything;
            if (grabAnything)
            {
                maxTeleMassa = float.MaxValue;
                teleDist = GrabAnythingDistanceSquared;
            }

            TelekinesisTrace.Log(
                $"TryGrab: room={room.id} cursor=({cursorPx.x:F1},{cursorPx.y:F1}) " +
                $"feet=({playerFeetPx.x:F1},{playerFeetPx.y:F1}) cursorSource=" +
                $"{(_cursorSetExternally ? "external(PlayerController)" : "camera-fallback")} " +
                $"maxTeleMassa={maxTeleMassa:F3} teleDist={teleDist:F0}({Mathf.Sqrt(teleDist):F1}px) " +
                $"mana={mana:F1} infiniteMana={infiniteMana} grabAnything={grabAnything} telemaster={stats.Telemaster}");

            // Find candidate within 50 px radius of cursor (AS3: 50 * 50)
            const float MaxCursorDistancePixels = 50f;
            if (!room.ObjectPhysicsLayer.TryFindTelekineticCandidate(cursorPx, MaxCursorDistancePixels, maxTeleMassa, out ObjectInstance candidate))
            {
                TelekinesisTrace.Log(
                    $"TryGrab REFUSED: no candidate within {MaxCursorDistancePixels:F0}px of the cursor that is " +
                    $"liftable and <= {maxTeleMassa:F3} massa. Candidates:\n" +
                    DescribeCandidates(room, cursorPx, maxTeleMassa));
                return false;
            }

            TelekinesisTrace.Log(
                $"TryGrab: candidate={candidate.objectId} at ({candidate.position.x:F1},{candidate.position.y:F1})");

            // Distance check to player eye/feet
            float distSq = (candidate.position - playerFeetPx).sqrMagnitude;
            bool liftable = candidate.IsLiftable();
            float massa = candidate.GetAs3Massa();
            if (!TelekinesisMath.CanGrab(hasTarget: true,
                                        levitPoss: liftable,
                                        distanceSquared: distSq,
                                        teleDistSquared: teleDist,
                                        massa: massa,
                                        maxTeleMassa: maxTeleMassa,
                                        mana: manaForGate))
            {
                TelekinesisTrace.Log(
                    $"TryGrab REFUSED by the gate: levitPoss={liftable} " +
                    $"(capability={candidate.GetResolvedPhysicalCapability()}, supportsTelekinesis={candidate.SupportsTelekinesis()}) | " +
                    $"distSq {distSq:F0} <= teleDist {teleDist:F0} ? {distSq <= teleDist} | " +
                    $"massa {massa:F3} <= maxTeleMassa {maxTeleMassa:F3} ? {massa <= maxTeleMassa} | " +
                    $"mana {mana:F1} >= {TelekinesisMath.MinManaToGrab:F0} ? {mana >= TelekinesisMath.MinManaToGrab}");
                return false;
            }

            // Line-of-sight check: required when telemaster == 0 (UnitPlayer.as:1792)
            if (stats.Telemaster == 0)
            {
                ITileQueryService tileQuery = ResolveTileQuery();
                if (tileQuery == null)
                {
                    TelekinesisTrace.Log(
                        "TryGrab: WARNING telemaster == 0 so the line-of-sight test SHOULD run, but no tile query " +
                        "could be derived from the room -- the test is being SKIPPED. Grabs through walls would pass.");
                }
                else
                {
                    Vector2 candidateCenter = candidate.GetApproximateBounds().center;
                    Vector2 rayDelta = candidateCenter - playerEyePx;
                    float rayDist = rayDelta.magnitude;
                    if (rayDist > 1e-4f)
                    {
                        var hit = tileQuery.Raycast(playerEyePx, rayDelta / rayDist, rayDist);
                        if (hit.HasValue && hit.Value.Tile != null)
                        {
                            TelekinesisTrace.Log(
                                $"TryGrab REFUSED by line of sight: ray eye({playerEyePx.x:F1},{playerEyePx.y:F1}) -> " +
                                $"({candidateCenter.x:F1},{candidateCenter.y:F1}) hit {hit.Value.Tile.physicsType} " +
                                $"(telemaster={stats.Telemaster})");
                            return false;
                        }

                        TelekinesisTrace.Log($"TryGrab: line of sight clear over {rayDist:F1}px");
                    }
                }
            }

            // Grab the object.
            //
            // The return value is CHECKED, and this is not defensive padding. TrySetTelekineticHold can
            // refuse — it requires SupportsTelekinesis() (capability == DynamicTelekinetic) and that the
            // layer actually tracks the object — and the previous code discarded that answer and logged
            // SUCCEEDED anyway. So the trace claimed a grab had happened while the object carried no
            // hold state at all and therefore could never move: a success line that cannot be false is
            // worse than no line, because it sends the next debugging session to the wrong half of the
            // system. Either this reports SUCCEEDED with isHeldByTelekinesis verified true, or it names
            // the term that refused.
            _heldObject = candidate;
            bool holdAccepted = room.ObjectPhysicsLayer.TrySetTelekineticHold(_heldObject, cursorPx);
            if (!holdAccepted || !candidate.IsHeldByTelekinesis())
            {
                TelekinesisTrace.Log(
                    $"TryGrab FAILED at TrySetTelekineticHold: returned {holdAccepted}, " +
                    $"isHeldByTelekinesis={candidate.IsHeldByTelekinesis()}, " +
                    $"supportsTelekinesis={candidate.SupportsTelekinesis()}, " +
                    $"capability={candidate.GetResolvedPhysicalCapability()}, " +
                    $"layerObjects={room.ObjectPhysicsLayer.DynamicObjectCount}. " +
                    "The grab was abandoned rather than reported as a success.");
                _heldObject = null;
                return false;
            }

            // Start a fresh hold post-mortem. `_lastHeldObject` deliberately keeps the reference after a
            // drop so `tele probe` can still report on it -- see the field block.
            _lastHeldObject = candidate;
            _lastHeldObjectId = candidate.objectId;
            _holdEntryTicks = 0;
            _holdUpdateTicks = 0;
            _holdTargetWrites = 0;
            _holdManaDrained = 0f;
            _holdFirstTarget = cursorPx;
            _holdLastTarget = cursorPx;

            // The heartbeat state goes on the SUCCESS line on purpose. "The grab succeeded and the box
            // did not move" is the whole complaint, and it has two causes -- the hold was refused, or
            // nothing is stepping the room -- so the one line that reports the success also has to
            // report whether the room is alive. Otherwise the next step is always another round trip
            // through `tele probe`, and a console is where that distinction gets lost.
            TelekinesisTrace.Log(
                $"TryGrab SUCCEEDED: holding {candidate.objectId} (massa={massa:F3}) | " +
                $"room.isActive={room.isActive} layerTicks={room.ObjectPhysicsLayer.TickCount} " +
                $"lastDt={room.ObjectPhysicsLayer.LastTickDeltaTime:F4}s " +
                $"(if layerTicks is 0 or never changes, the room is not being stepped and the prop cannot move)");

            TelekinesisRecorder.Write(
                $"[GRAB]  {candidate.objectId} massa={massa:F3} cursor=({cursorPx.x:F1},{cursorPx.y:F1}) " +
                $"pos=({candidate.position.x:F1},{candidate.position.y:F1}) " +
                $"centre=({candidate.GetApproximateBounds().center.x:F1},{candidate.GetApproximateBounds().center.y:F1}) " +
                $"gapToCentre={(_cursorRoomLocalPixels - candidate.GetApproximateBounds().center).magnitude:F1}px " +
                $"deadzone={TelekinesisMath.Deadzone:F0}  roomActive={room.isActive} layerTicks={room.ObjectPhysicsLayer.TickCount}");
            return true;
        }

        /// <summary>
        /// Every dynamic object the room's physics layer holds, nearest-to-cursor first, with the terms
        /// the candidate search tests. This is what turns "no candidate" from a dead end into a named
        /// reason — in practice the answer is almost always "every prop near you is too heavy, and the
        /// one you are aiming at is not in the room's dynamic list at all".
        /// </summary>
        /// <summary>
        /// The point a telekinesis distance is measured to: the object's AABB centre.
        ///
        /// <para><b>Deliberately not <c>position</c>.</b> <c>position</c> is the object's <i>bottom</i> --
        /// <c>GetApproximateBounds</c> builds <c>Rect(x - w/2, y, w, h)</c> and <c>Rect.y</c> is the
        /// bottom edge in a y-up world. The filter and the hold step use the centre, and so does AS3: the
        /// scan is <c>(celX - obj.X)² + (celY - (obj.Y - obj.scY/2))²</c> with <c>Y2 = Y</c> the bottom
        /// (<c>Box.as:171</c>), so <c>Y - scY/2</c> is the vertical middle. Changing this to the top or
        /// the bottom moves every telekinesis distance by half the object's height.</para>
        /// </summary>
        private static Vector2 ReferencePoint(ObjectInstance obj)
        {
            return obj == null ? Vector2.zero : obj.GetApproximateBounds().center;
        }

        /// <summary>
        /// Identifies a camera in a probe line, including its instance id, so two cameras that share a
        /// name (or a null) can still be told apart. See <c>Probe</c>'s [3b] block.
        /// </summary>
        private static string DescribeCameraRef(Camera camera)
        {
            if (camera == null)
            {
                return "<NULL>";
            }

            return $"{camera.name} id={camera.GetInstanceID()} depth={camera.depth} enabled={camera.enabled}";
        }

        private string DescribeCandidates(RoomInstance room, Vector2 cursorPx, float maxTeleMassa)
        {
            IReadOnlyList<ObjectInstance> objects = room.ObjectPhysicsLayer.DynamicObjects;
            if (objects == null || objects.Count == 0)
            {
                return "    (the physics layer holds NO dynamic objects at all -- nothing registered for this room)";
            }

            // Measure to the SAME point the filter uses -- GetApproximateBounds().center -- not to
            // `position`. `position` is the object's BOTTOM (Rect.y is the bottom edge in y-up), so a
            // cursor aimed at the middle of a 40 px crate is 20 px further away here than the filter
            // sees it. Printing a different metric than the filter uses is how a correct filter got
            // blamed for a failed grab.
            //
            // The centre is also what AS3 measures to: the scan uses `celY - (obj.Y - obj.scY/2)` and
            // `Y2 = Y` is the bottom (`Box.as:171`), so `Y - scY/2` is half the height above the
            // bottom -- the vertical middle. This matches; do not "fix" it to the top or the bottom.
            var ordered = new List<ObjectInstance>(objects);
            ordered.Sort((a, b) =>
                (ReferencePoint(a) - cursorPx).sqrMagnitude.CompareTo(
                    (ReferencePoint(b) - cursorPx).sqrMagnitude));

            var sb = new StringBuilder();
            int shown = 0;
            foreach (ObjectInstance obj in ordered)
            {
                if (obj == null) continue;

                Vector2 reference = ReferencePoint(obj);
                float dCursor = Mathf.Sqrt((reference - cursorPx).sqrMagnitude);
                string reject =
                    !obj.isActive ? "REJECT inactive" :
                    !obj.IsLiftable() ? $"REJECT not liftable (capability={obj.GetResolvedPhysicalCapability()})" :
                    obj.IsHeldByTelekinesis() ? "REJECT already held" :
                    obj.GetAs3Massa() > maxTeleMassa ? $"REJECT too heavy ({obj.GetAs3Massa():F3} > {maxTeleMassa:F3})" :
                    dCursor > 50f ? $"REJECT too far from cursor ({dCursor:F1}px > 50)" :
                    "ACCEPTABLE";

                sb.AppendLine(
                    $"    {obj.objectId,-14} at ({obj.position.x,7:F1},{obj.position.y,7:F1})  " +
                    $"d(cursor)={dCursor,7:F1}px  massa={obj.GetAs3Massa(),8:F3}  stay={obj.IsAtRest()}  -> {reject}");

                if (++shown >= 12)
                {
                    sb.AppendLine($"    ... ({ordered.Count - shown} more not shown)");
                    break;
                }
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// A dump of everything <see cref="TryGrab"/> looks at, in the order it tests it, without
        /// pressing anything. Console: <c>tele probe</c>.
        ///
        /// <para><b>Why a probe and not just the trace.</b> "I press Q and nothing happens" has four
        /// unrelated causes that are indistinguishable from the outside: the key never arrives, the room
        /// is null, the cursor is nowhere near a prop, or one specific gate term refuses. Each is a
        /// separate numbered line below, and the last line names the verdict — so the report itself
        /// identifies the broken link instead of requiring another round of guessing.</para>
        /// </summary>
        public string Probe()
        {
            // The recorder sits on THIS side of the call so that every early return inside ProbeCore is
            // captured too -- an early return is exactly the interesting case, and it is the one a
            // wrapper added at the end of the method would miss.
            string report = ProbeCore();
            TelekinesisRecorder.Write("[PROBE]\n" + report);
            return report;
        }

        private string ProbeCore()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== telekinesis probe ===");
            sb.AppendLine($"trace        : {(TelekinesisTrace.Enabled ? "ON" : "OFF -- run `tele on` for the live per-press trace")}");
            sb.AppendLine($"component    : enabled={enabled}  activeInHierarchy={gameObject.activeInHierarchy}");

            RoomInstance room = CurrentRoom;
            string source = _currentRoom != null
                ? "SetRoom (a TEST SEAM -- gameplay must use SetLandMap)"
                : (_landMap != null ? "LandMap (correct)" : "none");
            sb.AppendLine($"[1] room     : {(room == null ? "<NULL>" : room.id)}   via {source}");
            if (room == null)
            {
                sb.AppendLine("    STOP. With no room nothing can ever be grabbed. MapBridge.SpawnPlayer must");
                sb.AppendLine("    call SetLandMap; look for '[MapBridge] Telekinesis connected to LandMap' or");
                sb.AppendLine("    its warning in the log.");
                return sb.ToString();
            }

            RoomObjectPhysicsLayer layer = room.ObjectPhysicsLayer;
            sb.AppendLine($"    layer    : {(layer == null ? "<NULL>" : "present")}   dynamicObjects={(layer != null ? layer.DynamicObjectCount : 0)}");
            if (layer == null)
            {
                sb.AppendLine("    STOP. The room has no physics layer, so it holds no grabbable props.");
                return sb.ToString();
            }

            // [1b] The room heartbeat, sampled twice.
            //
            // A grab can succeed and still move nothing, because moving a held prop is done by
            // RoomObjectPhysicsLayer.StepHeldObject -- driven by RoomInstance.Update, which itself
            // early-returns when the room is inactive. The hold tick in this component only writes a
            // target; it never moves anything. So "the trace says SUCCEEDED and the box sits still" has
            // two causes that look identical from outside -- the hold was refused, or the room is not
            // stepping -- and they need opposite fixes. One counter, read twice, separates them: run
            // `tele probe`, wait a second, run it again, and read `sinceLastProbe`. Zero ticks with the
            // game running is the answer.
            int ticksNow = layer.TickCount;
            float sampleTime = UnityEngine.Time.realtimeSinceStartup;
            string sinceLast = _lastProbeTickCount < 0
                ? "(first sample -- run `tele probe` again in a second to measure)"
                : $"{ticksNow - _lastProbeTickCount} ticks in {sampleTime - _lastProbeTime:F2}s";
            sb.AppendLine($"[1b] heartbeat: room.isActive={room.isActive}  layer.TickCount={ticksNow}  " +
                          $"lastDt={layer.LastTickDeltaTime:F4}s  layerObjectsAtLastTick={layer.LastTickDynamicObjectCount}  " +
                          $"sinceLastProbe: {sinceLast}");
            if (_lastProbeTickCount >= 0 && ticksNow == _lastProbeTickCount)
            {
                sb.AppendLine("    !! The room has NOT stepped the physics layer since the previous probe. Nothing");
                sb.AppendLine("       inside the room can move -- not the props, and not a held one. If room.isActive");
                sb.AppendLine("       is false above, that is the cause: RoomInstance.Update returns before the");
                sb.AppendLine("       physics layer is ever reached, so nothing activated the room");
                sb.AppendLine("       (RoomStreamingManager.ActivateRoom). If isActive is true, the heartbeat itself");
                sb.AppendLine("       is not being driven -- check for '[GameLoopManager] Room heartbeat attached to");
                sb.AppendLine("       SimLoop' and note that with SimTickRoom on, the per-frame path stands down.");
            }
            _lastProbeTickCount = ticksNow;
            _lastProbeTime = sampleTime;

            // [1c] What the last Q press actually did. This is the link that `tele on` covers only if
            // the reader copies the console faithfully -- the unconditional `Q PRESSED` line sits above
            // it, so an abridged paste loses the context and looks exactly like a missing line. Read
            // here, it cannot be lost.
            sb.AppendLine($"[1c] last Q  : {_lastKeyPress}   (total presses since load: {_qPressCount})");
            sb.AppendLine($"    grab     : {_lastGrabOutcome}");
            if (_qPressCount > 0 && _heldObject == null)
            {
                sb.AppendLine("    NOTE: the last press left NOTHING held. Either the grab was refused (its");
                sb.AppendLine("          reason is named in the [tele] trace and reproduced by [4]-[6] below), or");
                sb.AppendLine("          it grabbed and something released it -- a second Q press toggles it off,");
                sb.AppendLine("          and the action key / right-click DROPS it while throwForce is 0.00 (see");
                sb.AppendLine("          [2]: an unskilled throw is a drop in place, AS3 Pers.as:237).");
            }

            CharacterStats stats = Stats;
            sb.AppendLine($"[2] stats    : {(stats == null ? "<NULL>" : "present")}");
            if (stats == null)
            {
                sb.AppendLine("    STOP. No CharacterStats on or above this GameObject.");
                return sb.ToString();
            }

            sb.AppendLine($"    maxTeleMassa={stats.MaxTeleMassa:F3}  teleDist={stats.TeleDist:F0} ({Mathf.Sqrt(stats.TeleDist):F1}px)  " +
                          $"telePorog={stats.TelePorog:F2}  teleMult={stats.TeleMult:F2}  " +
                          $"throwForce={stats.ThrowForce:F2}  telemaster={stats.Telemaster}");
            sb.AppendLine($"    mana={stats.manaHp:F1}   (a grab needs >= {TelekinesisMath.MinManaToGrab:F0})   " +
                          $"infiniteMana={InfiniteMana}");
            if (stats.manaHp < TelekinesisMath.MinManaToGrab && !InfiniteMana)
            {
                sb.AppendLine("    !! MANA IS BELOW THE GRAB FLOOR and nothing regenerates this pool, so every grab");
                sb.AppendLine("       is now refused permanently. This is the telekinesis pool (CharacterStats.manaHp),");
                sb.AppendLine("       which is NOT the one the 'infiniteMana' switch guards (that is UnitStats.Mana, used");
                sb.AppendLine("       by levitation and teleport). Turn on PlayerLocomotionAbilities.infiniteMana and");
                sb.AppendLine("       telekinesis will stop draining this pool too.");
            }

            UpdateCursorPositionIfNeeded();
            Vector2 cursor = _cursorRoomLocalPixels;
            Vector2 feet = GetPlayerPositionPixels();
            Vector2 eye = feet + new Vector2(0f, 35f);
            // Label the space explicitly. The cursor and the feet are ROOM-LOCAL (that is the space the
            // props live in), and printing them without saying so is how a 1920 px room-origin offset
            // went unnoticed for a whole debugging session: the numbers looked like a range problem.
            Vector2 roomOriginPx = room != null
                ? WorldCoordinates.LandToWorld(room.landPosition, Vector2.zero)
                : Vector2.zero;
            sb.AppendLine($"[3] cursor   : ({cursor.x:F1},{cursor.y:F1}) px  ROOM-LOCAL  " +
                          $"(world ({cursor.x + roomOriginPx.x:F1},{cursor.y + roomOriginPx.y:F1}))   source=" +
                          $"{(_cursorSetExternally ? "external (PlayerController.HandleMovementInput)" : "camera fallback inside this component")}");
            sb.AppendLine($"    roomOrigin=({roomOriginPx.x:F1},{roomOriginPx.y:F1}) px  landPosition={room?.landPosition}");
            sb.AppendLine($"    feet     : ({feet.x:F1},{feet.y:F1}) px  ROOM-LOCAL    eye=({eye.x:F1},{eye.y:F1}) px");
            if (_cursorSetExternally && cursor == Vector2.zero)
            {
                sb.AppendLine("    !! The cursor is exactly (0,0) AND was set externally. That is the signature");
                sb.AppendLine("       of PlayerController forwarding a zero placeholder because its camera was");
                sb.AppendLine("       null -- which also disables this component's own fallback. No grab can work.");
            }

            // [3b] The projection itself. This block exists because "[3] cursor is nowhere near the box"
            // has two completely different causes that look identical from the outside: the projection
            // is wrong, or the box is not on screen where the player thinks it is. Printing the camera's
            // real state, the mouse in screen space, AND each prop's screen position separates them.
            Camera cam = _mainCamera != null ? _mainCamera : Camera.main;
            Camera mainCam = Camera.main;
            sb.AppendLine($"[3b] camera  : _mainCamera={DescribeCameraRef(_mainCamera)}   Camera.main={DescribeCameraRef(mainCam)}");
            if (_mainCamera != null && mainCam != null && !ReferenceEquals(_mainCamera, mainCam))
            {
                sb.AppendLine("    !! MISMATCH: this component projects with a DIFFERENT camera than Camera.main.");
                sb.AppendLine("       Anything else in the project that uses Camera.main is looking at a different");
                sb.AppendLine("       frustum, so the cursor and the rendered scene disagree.");
            }

            if (cam == null)
            {
                sb.AppendLine("    !! NO CAMERA AT ALL -- the cursor cannot be projected, so no grab can ever hit.");
            }
            else
            {
                Transform ct = cam.transform;
                sb.AppendLine($"    orthographic={cam.orthographic}  orthoSize={cam.orthographicSize:F2}  fov={cam.fieldOfView:F1}  " +
                              $"pos=({ct.position.x:F3},{ct.position.y:F3},{ct.position.z:F3})");
                sb.AppendLine($"    screen   = {Screen.width} x {Screen.height}   mouseRaw=({Input.mousePosition.x:F1},{Input.mousePosition.y:F1},{Input.mousePosition.z:F1})   mousePresent={Input.mousePresent}");

                Vector3 probeScreen = Input.mousePosition;
                float zUsed = probeScreen.z;
                if (!cam.orthographic)
                {
                    zUsed = -ct.position.z;
                    probeScreen.z = zUsed;
                }

                Vector3 probeWorld = cam.ScreenToWorldPoint(probeScreen);
                sb.AppendLine($"    proj     : z passed={zUsed:F3}   (orthographic => x/y ignore z)");
                sb.AppendLine($"    mouseWorld = ({probeWorld.x:F3},{probeWorld.y:F3}) units  =>  ({probeWorld.x * TileQueryConstants.UnitToPixel:F1},{probeWorld.y * TileQueryConstants.UnitToPixel:F1}) px");

                // The decisive comparison: where each prop is ON SCREEN. Put the mouse on a box you can
                // see and read its screen coords here -- if mouseRaw is not near them, the box you are
                // aiming at is not the box this system thinks it is.
                sb.AppendLine("    props on screen (put the mouse on a visible box and compare with mouseRaw):");
                foreach (ObjectInstance probeObj in layer.DynamicObjects)
                {
                    if (probeObj == null) { continue; }

                    // `position` is ROOM-LOCAL, so the room origin has to go back on before asking the
                    // camera where this prop is on screen. Skipping that would make this diagnostic
                    // itself lie by exactly the offset it exists to detect.
                    Vector3 worldPos = WorldCoordinates.PixelToUnity(
                        WorldCoordinates.LandToWorld(room.landPosition, probeObj.position));
                    Vector3 screenPos = cam.WorldToScreenPoint(worldPos);
                    sb.AppendLine(
                        $"      {probeObj.objectId,-14} localPx=({probeObj.position.x,7:F1},{probeObj.position.y,7:F1})" +
                        $"  ->  screen=({screenPos.x,7:F1},{screenPos.y,7:F1})  inFront={screenPos.z > 0f}");
                }
            }

            sb.AppendLine($"[4] candidates ({layer.DynamicObjectCount} in the layer, nearest to cursor first)");
            sb.AppendLine(DescribeCandidates(room, cursor, stats.MaxTeleMassa));

            const float MaxCursorDistancePixels = 50f;
            if (!layer.TryFindTelekineticCandidate(cursor, MaxCursorDistancePixels, stats.MaxTeleMassa, out ObjectInstance candidate))
            {
                sb.AppendLine($"[5] gate     : NO candidate within {MaxCursorDistancePixels:F0}px of the cursor. See [4] for the reason each one was rejected.");
                sb.AppendLine("VERDICT: TryGrab would REFUSE at the candidate search.");
                return sb.ToString();
            }

            sb.AppendLine($"[5] candidate: {candidate.objectId} at ({candidate.position.x:F1},{candidate.position.y:F1})");
            float distSq = (candidate.position - feet).sqrMagnitude;
            bool liftable = candidate.IsLiftable();
            float massa = candidate.GetAs3Massa();
            bool gate = TelekinesisMath.CanGrab(true, liftable, distSq, stats.TeleDist,
                                                massa, stats.MaxTeleMassa, stats.manaHp);
            sb.AppendLine($"    levitPoss = {liftable}   (capability={candidate.GetResolvedPhysicalCapability()}, supportsTelekinesis={candidate.SupportsTelekinesis()})");
            sb.AppendLine($"    distSq    = {distSq:F0}  <= teleDist {stats.TeleDist:F0} ?  {distSq <= stats.TeleDist}");
            sb.AppendLine($"    massa     = {massa:F3}  <= maxTeleMassa {stats.MaxTeleMassa:F3} ?  {massa <= stats.MaxTeleMassa}");
            sb.AppendLine($"    mana      = {stats.manaHp:F1}  >= {TelekinesisMath.MinManaToGrab:F0} ?  {stats.manaHp >= TelekinesisMath.MinManaToGrab}");
            if (!gate)
            {
                sb.AppendLine("VERDICT: TryGrab would REFUSE at the gate -- the term marked 'False' above is the one.");
                return sb.ToString();
            }

            if (stats.Telemaster == 0)
            {
                ITileQueryService query = ResolveTileQuery();
                if (query == null)
                {
                    sb.AppendLine("[6] LOS      : SKIPPED -- no tile query could be derived from the room (the ray would have run).");
                }
                else
                {
                    Vector2 center = candidate.GetApproximateBounds().center;
                    Vector2 delta = center - eye;
                    float len = delta.magnitude;
                    string blockedBy = null;
                    if (len > 1e-4f)
                    {
                        var hit = query.Raycast(eye, delta / len, len);
                        if (hit.HasValue && hit.Value.Tile != null)
                        {
                            blockedBy = hit.Value.Tile.physicsType.ToString();
                        }
                    }

                    sb.AppendLine($"[6] LOS      : telemaster=0 so the ray runs: eye -> ({center.x:F1},{center.y:F1}) over {len:F1}px : " +
                                  (blockedBy != null ? $"BLOCKED by {blockedBy}" : "clear"));
                    if (blockedBy != null)
                    {
                        sb.AppendLine("VERDICT: TryGrab would REFUSE at line of sight.");
                        return sb.ToString();
                    }
                }
            }
            else
            {
                sb.AppendLine($"[6] LOS      : not tested (telemaster={stats.Telemaster} bypasses it, UnitPlayer.as:1792)");
            }

            // [7] The hold, as the physics layer sees it.
            //
            // `_heldObject` non-null while isHeldByTelekinesis reads false is the signature of a hold
            // that was never accepted or was cleared afterwards -- the state that reads from the outside
            // as "the grab succeeded but nothing moves". The target and velocity here are the two values
            // StepHeldObject consumes, so a target that follows the cursor while velocity stays (0,0)
            // and position never changes means the step is not running, which [1b] confirms.
            MapObjectDynamicStateData heldState = _heldObject?.runtimeState?.dynamicState;
            sb.AppendLine($"[7] held     : {(_heldObject != null ? _heldObject.objectId + "   (LIVE)" : "<none> (nothing is held right now)")}");
            if (_heldObject != null)
            {
                sb.AppendLine($"    pos=({_heldObject.position.x:F1},{_heldObject.position.y:F1})  " +
                              $"isHeldByTelekinesis={_heldObject.IsHeldByTelekinesis()}  " +
                              $"isDynamic={heldState?.isDynamic}  " +
                              $"hasTarget={heldState?.hasTelekineticTarget}  " +
                              $"target=({heldState?.telekineticTarget.x:F1},{heldState?.telekineticTarget.y:F1})  " +
                              $"vel=({heldState?.velocity.x:F1},{heldState?.velocity.y:F1})");
                sb.AppendLine("    Note: while something is held, pressing Q again DROPS it -- release before the next grab.");
            }

            // [7a] Post-mortem of the last hold, whether or not it is still alive.
            //
            // The natural sequence is grab -> watch nothing happen -> press Q again (which DROPS the
            // prop) -> type `tele probe`. At that moment `_heldObject` is null, so a [7] that reads only
            // the live hold reports "<none>" on the one run that actually had the evidence in hand.
            // These counters survive the drop and answer the decisive question directly: did
            // `UpdateHold` run at all while the prop was held?
            //
            // If it did not, the target stayed frozen at whatever `TrySetTelekineticHold` wrote -- the
            // grab-time cursor -- and a grab-time cursor that happens to sit inside the 15 px deadzone
            // means the prop can never move, no matter where the player aims afterwards. That is a
            // completely different bug from "the cursor is in the wrong place now", and the two look
            // identical from the screen.
            //
            // `_holdManaDrained` is NOT a witness that the tick ran, and an earlier revision wrongly used
            // it as one. The drain is legitimately zero whenever infinite mana is on, and also whenever
            // `massa <= telePorog` -- "a light object costs nothing to hold" is the oracle's own rule
            // (UnitPlayer.as:1807-1813). `_holdUpdateTicks` counts reaching the drain LINE, which is the
            // fact that actually distinguishes "ran" from "did not run".
            sb.AppendLine($"[7a] last hold: {_lastHeldObjectId}");
            sb.AppendLine($"    UpdateHold : entered={_holdEntryTicks}  reached-the-drain={_holdUpdateTicks}  " +
                          $"target writes={_holdTargetWrites}  mana drained={_holdManaDrained:F1}");
            if (_lastHeldObjectId != "(nothing has been held yet)")
            {
                sb.AppendLine($"    target     : first=({_holdFirstTarget.x:F1},{_holdFirstTarget.y:F1})  " +
                              $"last=({_holdLastTarget.x:F1},{_holdLastTarget.y:F1})");
            }

            bool heldSomething = _lastHeldObjectId != "(nothing has been held yet)";
            if (heldSomething && _holdEntryTicks == 0)
            {
                sb.AppendLine("    !! ENTERED 0 TIMES for the last hold. This component's Update never called UpdateHold");
                sb.AppendLine("       while the prop was held, so the target was never refreshed and stayed frozen at");
                sb.AppendLine("       the cursor TrySetTelekineticHold wrote at grab time. If that cursor sat inside the");
                sb.AppendLine("       15 px deadzone of the prop's centre, the prop can NEVER move, however the player");
                sb.AppendLine("       aims afterwards. Check [1c] above: if it says the last press was 'holding X ->");
                sb.AppendLine("       DROPPED it', the hold survived across presses, so this counter cannot be 0 and");
                sb.AppendLine("       something else is wrong with this report.");
            }
            else if (heldSomething && _holdEntryTicks > 0 && _holdUpdateTicks == 0)
            {
                sb.AppendLine("    !! Entered UpdateHold but never reached the drain. Something between the entry");
                sb.AppendLine("       counter and the mana line returns every frame -- the only `return` there is");
                sb.AppendLine("       MustDrop, which DROPS the prop. Look for a '[tele] DROPPED' line naming the term.");
            }
            else if (heldSomething && _holdUpdateTicks > 0 && _holdManaDrained <= 0f)
            {
                sb.AppendLine("    NOT a bug -- the hold tick ran and drained nothing, which is expected here:");
                sb.AppendLine($"      infiniteMana={InfiniteMana} (if true the drain is skipped by design), and");
                sb.AppendLine("      HoldManaDrain returns 0 when massa <= telePorog (a light prop is free to hold).");
                sb.AppendLine("    Do NOT read an unchanged mana value as 'the hold tick never ran'.");
            }

            // [7c] Would the hold move this prop RIGHT NOW, from where the cursor actually is?
            //
            // A held prop only accelerates when the cursor is MORE than 15 px outside its centre on some
            // axis (UnitPlayer.as:1247-1262), and it can only travel where the tiles allow. A cursor
            // parked in the lower half of a box that is resting on the floor therefore produces
            // "grabbed, holding, and never moves" -- correct behaviour that reads exactly like a bug.
            // Printing the nudge the controller would apply removes that ambiguity.
            ObjectInstance holdSubject = _heldObject ?? _lastHeldObject;
            if (holdSubject != null && !holdSubject.IsDestroyed())
            {
                Vector2 subjectCenter = holdSubject.GetApproximateBounds().center;
                float speedCap = TelekinesisMath.PerFrameVelocityToPerSecond(TelekinesisMath.PlayerTeleSpeed);
                float accel = TelekinesisMath.PerFrameAccelToPerSecondSquared(TelekinesisMath.PlayerTeleAccel);
                float nudgeX = TelekinesisMath.HoldAccelStep(subjectCenter.x, cursor.x, 0f, speedCap, accel, TelekinesisMath.Deadzone);
                float nudgeY = TelekinesisMath.HoldAccelStep(subjectCenter.y, cursor.y, 0f, speedCap, accel, TelekinesisMath.Deadzone);

                sb.AppendLine($"[7c] deadzone : {holdSubject.objectId} centre=({subjectCenter.x:F1},{subjectCenter.y:F1})  " +
                              $"cursor=({cursor.x:F1},{cursor.y:F1})  deadzone=+/-{TelekinesisMath.Deadzone:F0}px");
                sb.AppendLine($"    nudge    : x={(nudgeX > 0f ? "+" : nudgeX < 0f ? "-" : "0")}  " +
                              $"y={(nudgeY > 0f ? "+ (UP)" : nudgeY < 0f ? "- (DOWN)" : "0")}   " +
                              $"(to lift this prop the cursor must be above its centre by more than " +
                              $"{TelekinesisMath.Deadzone:F0}px, i.e. y > {subjectCenter.y + TelekinesisMath.Deadzone:F1})");

                if (nudgeX == 0f && nudgeY == 0f)
                {
                    sb.AppendLine("    !! The cursor is INSIDE the deadzone on BOTH axes, so the hold applies no nudge at");
                    sb.AppendLine("       all and the prop is inert BY DESIGN. Move the cursor more than 15 px away from");
                    sb.AppendLine("       the box's centre and it will follow.");
                }
                else if (nudgeY < 0f)
                {
                    sb.AppendLine("    NOTE: the cursor is BELOW the prop's centre, so the hold is pushing it DOWN -- into");
                    sb.AppendLine("          the floor it is already resting on, which refuses. Nothing can move. Aim above");
                    sb.AppendLine("          the centre to lift it. AS3 behaves the same way (UnitPlayer.as:1254-1262).");
                }
            }

            // [7b] Why a held prop did not move.
            //
            // "The grab succeeded and the box still sits still" has no observable inside the hold
            // path: `UpdateHold` writes a target, `StepHeldObject` integrates a velocity, and
            // `MoveWithCollision` then asks `HasCollision` about each sub-step. A refused sub-step
            // leaves the position untouched -- no error, no refusal, nothing in the trace -- so the
            // prop is frozen by a decision made three layers below anything that logs. The layer now
            // records the refused position on the held object's state, and this block names the tile
            // that refused it. That is the difference between "the mover is not running" ([1b]/[7])
            // and "the mover is running and being blocked" (here), which have opposite fixes.
            //
            // The comparison is room-local on both sides. `TileData.GetBounds()` is room-local, and
            // although `RoomInstance.CheckCollision` converts to world before testing, the room
            // origin is added to both the bounds and the tile there, so it cancels -- local-vs-local
            // is the same test.
            if (_heldObject != null && heldState != null && heldState.hasRejectedMove)
            {
                Vector2 refused = heldState.rejectedMoveCandidate;
                Vector2 heldSize = _heldObject.GetApproximatePixelSize();
                Rect heldBounds = new Rect(
                    refused.x - heldSize.x * 0.5f,
                    refused.y,
                    heldSize.x,
                    heldSize.y);

                sb.AppendLine($"[7b] blocked  : the last REFUSED move was " +
                              $"{(heldState.rejectedMoveVertical ? "VERTICAL" : "HORIZONTAL")} " +
                              $"-> room-local ({refused.x:F1},{refused.y:F1})  tick={heldState.rejectedMoveTick} " +
                              $"(layer now at {layer.TickCount})");
                sb.AppendLine($"    candidate AABB: x[{heldBounds.xMin:F1}..{heldBounds.xMax:F1}] " +
                              $"y[{heldBounds.yMin:F1}..{heldBounds.yMax:F1}]   size=({heldSize.x:F1},{heldSize.y:F1})");

                float roomW = room.width * WorldConstants.TILE_SIZE;
                float roomH = room.height * WorldConstants.TILE_SIZE;
                bool outside = heldBounds.xMin < 0f || heldBounds.yMin < 0f ||
                               heldBounds.xMax > roomW || heldBounds.yMax > roomH;
                if (outside)
                {
                    sb.AppendLine($"    !! OUT OF ROOM BOUNDS -- the room is {roomW:F0}x{roomH:F0} px and this AABB is not");
                    sb.AppendLine("       inside it. HasCollision refuses on that alone, before any tile is consulted.");
                }

                int btLeft = Mathf.FloorToInt(heldBounds.xMin / WorldConstants.TILE_SIZE);
                int btRight = Mathf.FloorToInt(heldBounds.xMax / WorldConstants.TILE_SIZE);
                int btBottom = Mathf.FloorToInt(heldBounds.yMin / WorldConstants.TILE_SIZE);
                int btTop = Mathf.FloorToInt(heldBounds.yMax / WorldConstants.TILE_SIZE);
                for (int ty = btBottom; ty <= btTop; ty++)
                {
                    for (int tx = btLeft; tx <= btRight; tx++)
                    {
                        TileData tile = room.GetTileAtCoord(new Vector2Int(tx, ty));
                        if (tile == null) { continue; }

                        Rect tileBounds = tile.GetBounds();
                        bool overlaps = heldBounds.Overlaps(tileBounds);
                        if (!overlaps && tile.physicsType != TilePhysicsType.Wall) { continue; }

                        sb.AppendLine($"      tile({tx},{ty}) {tile.physicsType,-9} " +
                                      $"x[{tileBounds.xMin:F1}..{tileBounds.xMax:F1}] y[{tileBounds.yMin:F1}..{tileBounds.yMax:F1}] " +
                                      $"overlaps={overlaps}" +
                                      (overlaps ? "   <-- this is the refusal" : ""));
                    }
                }

                sb.AppendLine("    A Wall tile that merely TOUCHES the body is enough: the port tests the whole");
                sb.AppendLine("    AABB, where AS3 probes only the leading edge (Box.as:1035-1141). An overlapping");
                sb.AppendLine("    tile here that the leading edge would not have reached is that divergence biting.");
            }

            sb.AppendLine("VERDICT: TryGrab would SUCCEED -- every gate above passed. If pressing Q still does");
            sb.AppendLine("         nothing, the remaining links are: (a) the key edge never reaching this component");
            sb.AppendLine("         (run `tele on` and look for '[tele] Q PRESSED'), (b) the hold being refused, which");
            sb.AppendLine("         now logs 'TryGrab FAILED at TrySetTelekineticHold' instead of a false SUCCEEDED, or");
            sb.AppendLine("         (c) the room not stepping -- read [1b] above and re-run this probe to get a tick delta.");
            return sb.ToString();
        }

        /// <summary>
        /// Throws the held object towards the cursor, consuming mana and applying impulse.
        /// AS3's <c>UnitPlayer.throwTele</c>.
        /// </summary>
        public bool TryThrow()
        {
            if (!IsHoldingObject)
            {
                return false;
            }

            CharacterStats stats = Stats;
            RoomInstance room = CurrentRoom;
            if (stats == null || room == null || room.ObjectPhysicsLayer == null)
            {
                Drop($"throw could not resolve stats/room/layer (stats={stats != null} room={room != null})");
                return true;
            }

            float massa = _heldObject.GetAs3Massa();
            float throwForce = stats.ThrowForce;

            // If player has no throw force unlocked (unskilled), drop instead
            if (throwForce <= 0f)
            {
                // Named as a THROW-KEY press, not just "a drop": the Q key also drops, and without the
                // distinction a reader cannot tell "the player tried to throw" from "the player toggled
                // the grab off". AS3 reaches the same no-op through the force, not through a branch:
                // `throwTele` computes the cost as 0 when `pers.throwForce == 0` and then calls
                // `norma(_loc1_, this.pers.throwForce)` with 0 (UnitPlayer.as:1852-1868), so the object
                // is released with no impulse -- a drop in place.
                Drop($"THROW KEY pressed while holding, but throwForce is {throwForce:F2} " +
                     $"(unskilled -- AS3 Pers.as:237 default 0), so the throw is a drop in place");
                return true;
            }

            float cost = TelekinesisMath.ThrowCost(massa, throwForce, stats.ThrowDmagic, stats.AllDManaMult);
            float currentMana = stats.manaHp;

            Vector2 playerFeetPx = GetPlayerPositionPixels();
            Vector2 objCenterPx = _heldObject.GetApproximateBounds().center;
            Vector2 playerCenterPx = playerFeetPx + new Vector2(0f, 25f);

            // In AS3 coordinates: +Y is down. In Unity coordinates: +Y is up.
            // Map into AS3 coordinates for ThrowImpulse:
            float as3ObjY = -objCenterPx.y;
            float as3PlayerY = -playerCenterPx.y;

            TelekinesisMath.ThrowImpulse(
                objCenterPx.x, as3ObjY, 0f,
                playerCenterPx.x, as3PlayerY, 0f,
                throwForce, currentMana, cost,
                out float as3ImpulseX, out float as3ImpulseY);

            // Convert back to Unity coordinates:
            float impulseX = as3ImpulseX;
            float impulseY = -as3ImpulseY;

            // Convert per-frame velocity (px/frame) to per-second (px/s)
            Vector2 releaseVelocity = new Vector2(
                TelekinesisMath.PerFrameVelocityToPerSecond(impulseX),
                TelekinesisMath.PerFrameVelocityToPerSecond(impulseY));

            // Deduct mana -- unless the debug infinite-mana switch is on. See the _abilities field:
            // the switch guards UnitStats.Mana, not this pool, so it has to be honoured explicitly.
            if (!InfiniteMana)
            {
                if (cost <= currentMana)
                {
                    stats.manaHp -= cost;
                }
                else
                {
                    stats.manaHp = 0f;
                }
            }

            // Release hold as throw
            string thrownId = _heldObject.objectId;
            bool treatAsThrow = _heldObject.CanBeThrown();
            room.ObjectPhysicsLayer.TryReleaseTelekineticHold(_heldObject, releaseVelocity, treatAsThrow: true);

            // Recorded because a throw was the one action on this path with no record at all: a press
            // that reached here left only a `[DROP]`-shaped silence behind, so "I threw it at the wall"
            // could not be told apart from "the throw never fired". `CanBeThrown` is printed because
            // `isThrown` (and with it AS3's damage-on-impact window) is gated on it -- see the four-gap
            // report in docs/Research/TELEKINESIS_PROPS_AND_UNITS_GAPS_2026-10-02.md.
            TelekinesisRecorder.Write(
                $"[THROW] {thrownId} -- releaseVelocity=({releaseVelocity.x:F1},{releaseVelocity.y:F1}) " +
                $"throwForce={throwForce:F2} massa={massa:F3} cost={cost:F2} manaBefore={currentMana:F1} " +
                $"canBeThrown={treatAsThrow} infiniteMana={InfiniteMana}\n" +
                $"        objCentre=({objCenterPx.x:F1},{objCenterPx.y:F1}) " +
                $"playerCentre=({playerCenterPx.x:F1},{playerCenterPx.y:F1})");

            _heldObject = null;
            return true;
        }

        /// <summary>
        /// Drops the held object without throwing. AS3's <c>UnitPlayer.dropTeleObj</c>.
        /// </summary>
        public void Drop(string reason = null)
        {
            if (!IsHoldingObject)
            {
                return;
            }

            RoomInstance room = CurrentRoom;
            if (room != null && room.ObjectPhysicsLayer != null)
            {
                room.ObjectPhysicsLayer.TryReleaseTelekineticHold(_heldObject, Vector2.zero, treatAsThrow: false);
            }

            TelekinesisTrace.Log($"DROPPED {_heldObject.objectId} -- {reason ?? "unspecified"}");

            // The one record that survives the drop and answers the decisive question without any
            // copying: did UpdateHold run while the prop was held, and did it write a target?
            TelekinesisRecorder.Write(
                $"[DROP]  {_heldObject.objectId} -- {reason ?? "unspecified"}\n" +
                $"        UpdateHold entered={_holdEntryTicks}  reachedDrain={_holdUpdateTicks}  " +
                $"targetWrites={_holdTargetWrites}  manaDrained={_holdManaDrained:F2}\n" +
                $"        target first=({_holdFirstTarget.x:F1},{_holdFirstTarget.y:F1}) " +
                $"last=({_holdLastTarget.x:F1},{_holdLastTarget.y:F1})\n" +
                $"        final pos=({_heldObject.position.x:F1},{_heldObject.position.y:F1})  " +
                $"component Update calls={_updateCallCount}");
            _heldObject = null;
        }

        /// <summary>
        /// Updates the held object's tracking and mana drain over <paramref name="deltaTime"/>.
        /// </summary>
        public void UpdateHold(float deltaTime)
        {
            // The per-frame telekinesis path. It returns immediately unless a prop is actually held,
            // so in the camp — where nothing is held — this should read ~0 calls of real work. If it
            // does NOT, the guard has been bypassed and the hold path is running every frame.
            using (PFE.Core.Profiling.PfeProfiler.Region("tele.updateHold",
                "telekinesis: per-frame hold tick. Returns immediately unless a prop is held."))
            {
            if (!IsHoldingObject)
            {
                return;
            }

            // First post-mortem counter, and it is deliberately the FIRST statement after the guard.
            // `_holdEntryTicks > 0` with `_holdUpdateTicks == 0` means the method was entered but never
            // reached the drain -- i.e. something between here and there throws or returns, and Unity
            // would be logging that exception every frame. Both counters at zero with a non-null
            // `_heldObject` means Update never called this at all. Those are different bugs.
            _holdEntryTicks++;
            if (_holdEntryTicks == 1 || _holdEntryTicks % 30 == 0)
            {
                TelekinesisRecorder.Write(
                    $"[HOLD]  entered #{_holdEntryTicks}  obj={_heldObject.objectId}  " +
                    $"pos=({_heldObject.position.x:F1},{_heldObject.position.y:F1})  " +
                    $"centre=({_heldObject.GetApproximateBounds().center.x:F1},{_heldObject.GetApproximateBounds().center.y:F1})");
            }

            if (_heldObject.IsDestroyed())
            {
                Drop("the object was destroyed");
                return;
            }

            UpdateCursorPositionIfNeeded();

            Vector2 cursorPx = _cursorRoomLocalPixels;
            Vector2 playerFeetPx = GetPlayerPositionPixels();
            float distSq = (_heldObject.position - playerFeetPx).sqrMagnitude;

            CharacterStats stats = Stats;

            // The same widening as TryGrab. Without it, "grab anything" would let you pick up a prop and
            // then drop it the instant it travelled past the authored 600 px — a grab that works and a
            // hold that silently refuses, which is the failure shape this file has already fought twice.
            float teleDist = GrabAnything ? GrabAnythingDistanceSquared : stats.TeleDist;
            float mana = stats.manaHp;

            // Check if object must be dropped
            if (TelekinesisMath.MustDrop(distSq, teleDist, mana, _heldObject.IsLiftable()))
            {
                Drop($"MustDrop -- distSq={distSq:F0} teleDist={teleDist:F0} (drop past x1.2 = {teleDist * TelekinesisMath.DropDistanceMultiplier:F0}) " +
                     $"mana={mana:F1} liftable={_heldObject.IsLiftable()}");
                return;
            }

            // Drain mana over time -- unless the debug infinite-mana switch is on. That switch guards
            // UnitStats.Mana, which is NOT the pool read here (see the _abilities field), so without
            // this guard the telekinesis pool keeps draining under a switch that promises it will not.
            bool infiniteMana = InfiniteMana;
            float drainThisStep = 0f;
            if (!infiniteMana)
            {
                float drainPerFrame = TelekinesisMath.HoldManaDrain(_heldObject.GetAs3Massa(), stats.TelePorog, stats.TeleMult) * stats.AllDManaMult;
                drainThisStep = drainPerFrame * deltaTime * TelekinesisMath.As3FramesPerSecond;
                stats.manaHp = Mathf.Max(0f, stats.manaHp - drainThisStep);
            }

            // Post-mortem counters -- see the field block. These are what make a probe taken AFTER the
            // drop able to say whether this method ran at all during the hold. `_holdUpdateTicks`
            // counts reaching this line, NOT the size of the drain: with infinite mana on the drain is
            // deliberately zero, and a zero drain must not be mistaken for "the hold tick never ran".
            _holdUpdateTicks++;
            _holdManaDrained += drainThisStep;

            // Update the target position on the dynamic state.
            //
            // `hasTelekineticTarget` is re-asserted here, not only the target, and that is load-bearing.
            // StepHeldObject reads
            //     targetPosition = state.hasTelekineticTarget ? state.telekineticTarget : obj.position;
            // so a cleared flag makes the held object chase ITS OWN position: the difference is always
            // inside the deadzone, velocity stays exactly zero, and the prop hangs motionless with no
            // error, no refusal and nothing in the trace. `TryApplyImpulse` and
            // `TryReleaseTelekineticHold` both clear that flag, so any knockback or impact that lands on
            // a held prop would silently freeze the hold.
            //
            // AS3 has no such flag to go stale: its player tick writes the object's dx/dy directly every
            // frame (UnitPlayer.as:1247-1262), so the intent is unconditional for as long as the object
            // is held. Re-asserting it here restores that -- and the only thing that may end the hold is
            // this component dropping it, which also clears the flag through TryReleaseTelekineticHold.
            if (_heldObject.runtimeState != null && _heldObject.runtimeState.dynamicState != null)
            {
                _heldObject.runtimeState.dynamicState.telekineticTarget = cursorPx;
                _heldObject.runtimeState.dynamicState.hasTelekineticTarget = true;
                _holdTargetWrites++;
                _holdLastTarget = cursorPx;
                if (_holdTargetWrites == 1 || _holdTargetWrites % 30 == 0)
                {
                    MapObjectDynamicStateData written = _heldObject.runtimeState.dynamicState;
                    TelekinesisRecorder.Write(
                        $"[HOLD]  write #{_holdTargetWrites}  target=({cursorPx.x:F1},{cursorPx.y:F1})  " +
                        $"pos=({_heldObject.position.x:F1},{_heldObject.position.y:F1})  " +
                        $"vel=({written.velocity.x:F1},{written.velocity.y:F1})  " +
                        $"grounded={written.isGrounded}  infiniteMana={infiniteMana}  mana={stats.manaHp:F1}");
                }
            }

            // Bounded hold trace: about once a second while a prop is held. This answers a question the
            // grab trace cannot -- "the grab succeeded, so why does nothing move?". Movement is NOT done
            // here; RoomObjectPhysicsLayer.StepHeldObject does it, driven by the room heartbeat. So a
            // target that follows the cursor while `pos` and `vel` stay put means the room heartbeat is
            // not ticking, which is a completely different bug from a refused grab.
            if (TelekinesisTrace.Enabled)
            {
                _holdTraceTick++;
                if (_holdTraceTick >= 30)
                {
                    _holdTraceTick = 0;
                    MapObjectDynamicStateData state = _heldObject.runtimeState?.dynamicState;
                    Vector2 velocity = state != null ? state.velocity : Vector2.zero;
                    float gap = (_heldObject.position - cursorPx).magnitude;
                    TelekinesisTrace.Log(
                        $"hold {_heldObject.objectId}: pos=({_heldObject.position.x:F1},{_heldObject.position.y:F1}) " +
                        $"target=({cursorPx.x:F1},{cursorPx.y:F1}) gap={gap:F1}px vel=({velocity.x:F1},{velocity.y:F1}) " +
                        $"mana={stats.manaHp:F1} heldFlag={(state != null && state.isHeldByTelekinesis)} " +
                        $"grounded={(state != null && state.isGrounded)} stay={_heldObject.IsAtRest()}");
                }
            }
            }
        }

        private void Update()
        {
            // Frame-level wrapper for the telekinesis component, so `tele.update` minus
            // `tele.updateHold` is the component's own per-frame bookkeeping.
            using (PFE.Core.Profiling.PfeProfiler.Region("tele.update",
                "telekinesis: the component's Update — once per frame. Child is tele.updateHold."))
            {
            _updateCallCount++;
            if (_updateCallCount == 1 || _updateCallCount % 300 == 0)
            {
                TelekinesisRecorder.Write(
                    $"[UPDATE] call #{_updateCallCount}  holding={IsHoldingObject}  " +
                    $"enabled={enabled}  activeInHierarchy={gameObject.activeInHierarchy}");
            }

            UpdateHold(Time.deltaTime);
            }
        }

        /// <summary>
        /// The player's feet in <b>room-local</b> pixels -- the same space as
        /// <c>ObjectInstance.position</c>, which is what every distance in this file is measured
        /// against. All three sources below are world pixels (see <see cref="SetCursorWorldPixels"/>
        /// for why the two spaces differ), so the conversion happens once, here, rather than at each
        /// caller.
        /// </summary>
        private Vector2 GetPlayerPositionPixels()
        {
            return WorldCoordinates.WorldToLocal(GetPlayerPositionWorldPixels());
        }

        /// <summary>The player's feet in world pixels, before the room-origin conversion above.</summary>
        private Vector2 GetPlayerPositionWorldPixels()
        {
            if (_explicitPlayerPositionPixels.HasValue)
            {
                return _explicitPlayerPositionPixels.Value;
            }

            var tilePhysics = GetComponent<TilePhysicsController>();
            if (tilePhysics != null)
            {
                return tilePhysics.PixelPosition;
            }

            return new Vector2(
                transform.position.x * TileQueryConstants.UnitToPixel,
                transform.position.y * TileQueryConstants.UnitToPixel);
        }

        private void UpdateCursorPositionIfNeeded()
        {
            if (_cursorSetExternally)
            {
                return;
            }

            if (_mainCamera == null)
            {
                _mainCamera = Camera.main ?? FindFirstObjectByType<Camera>();
            }

            if (_mainCamera != null && Input.mousePresent)
            {
                Vector3 mouseScreen = Input.mousePosition;
                if (!_mainCamera.orthographic)
                {
                    mouseScreen.z = -_mainCamera.transform.position.z;
                }

                Vector3 mouseWorld = _mainCamera.ScreenToWorldPoint(mouseScreen);
                // The camera projects into WORLD pixels; the rest of this file works in ROOM-LOCAL
                // pixels (see SetCursorWorldPixels). Convert here too, or this fallback path quietly
                // reintroduces the room-origin offset that the external path has already removed --
                // and the two paths would then disagree about which space the cursor is in.
                _cursorRoomLocalPixels = WorldCoordinates.WorldToLocal(new Vector2(
                    mouseWorld.x * TileQueryConstants.UnitToPixel,
                    mouseWorld.y * TileQueryConstants.UnitToPixel));
            }
        }
    }

    /// <summary>
    /// A file-backed flight recorder for the telekinesis path.
    ///
    /// <para><b>Why a file and not a console line.</b> The console is a stream the reader has to copy,
    /// and across three debugging rounds every paste silently omitted whole blocks — including the
    /// unconditional <c>Q PRESSED</c> line that always precedes a grab, and the <c>[7a]</c> block added
    /// specifically to answer "did the hold tick run". An abridged paste is indistinguishable from a
    /// missing line, so a conclusion drawn from one is not evidence. This writes the same facts to disk
    /// where they can be read back verbatim.</para>
    ///
    /// <para><b>Location.</b> <c>&lt;project&gt;/telekinesis_flight.log</c> — beside <c>Assets/</c>, so
    /// it is trivially findable and is not inside the imported asset tree.</para>
    ///
    /// <para><b>Never throws into gameplay.</b> Every write is wrapped, and the first failure latches
    /// the recorder off, so a read-only directory cannot spam the console from the per-frame hold
    /// path.</para>
    /// </summary>
    public static class TelekinesisRecorder
    {
        private static string _path;
        private static bool _disabled;
        private static bool _announced;

        /// <summary>
        /// Where the log is being written, or null before the first record. Resolves on demand, so
        /// reading this before anything has been logged still names the file.
        /// </summary>
        public static string Path
        {
            get
            {
                EnsurePath();
                return _path;
            }
        }

        /// <summary>
        /// The places the log may live, best first.
        ///
        /// <para>The project root comes first because it is trivially findable from outside the editor.
        /// <c>Application.persistentDataPath</c> is the fallback for the case that matters: a read-only
        /// or virtualised project directory. One candidate that silently fails is how a diagnostic ends
        /// up producing nothing while looking like it is working — which is the exact failure this class
        /// exists to avoid.</para>
        /// </summary>
        private static string[] CandidatePaths()
        {
            string projectRoot = null;
            try
            {
                projectRoot = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, "..", "telekinesis_flight.log"));
            }
            catch
            {
                projectRoot = null;
            }

            string persistent = null;
            try
            {
                persistent = System.IO.Path.Combine(Application.persistentDataPath, "telekinesis_flight.log");
            }
            catch
            {
                persistent = null;
            }

            return new[] { projectRoot, persistent };
        }

        /// <summary>
        /// Picks a writable path and remembers it. Never truncates, so it is safe to call from the
        /// per-frame <see cref="Write"/> path as well as from <see cref="Begin"/>.
        /// </summary>
        private static bool EnsurePath()
        {
            if (_path != null)
            {
                return true;
            }

            if (_disabled)
            {
                return false;
            }

            foreach (string candidate in CandidatePaths())
            {
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                try
                {
                    // Touch it now so a read-only directory is detected here, once, rather than throwing
                    // on every subsequent record.
                    System.IO.File.AppendAllText(candidate, string.Empty);
                    _path = candidate;
                    Announce();
                    return true;
                }
                catch
                {
                    // Try the next candidate.
                }
            }

            _disabled = true;
            Debug.LogWarning(
                "[tele] TelekinesisRecorder: no writable path for the flight log (tried the project " +
                "root and Application.persistentDataPath). The hold diagnostics are console-only.");
            return false;
        }

        /// <summary>Announces the chosen path once, so 'where did the log go' is answerable from the Console.</summary>
        private static void Announce()
        {
            if (_announced)
            {
                return;
            }

            _announced = true;
            Debug.Log($"[tele] flight log -> {_path}");
        }

        /// <summary>
        /// Rolls the previous run aside and writes a header for this one. Called once from <c>Awake</c>.
        ///
        /// <para><b>Why the roll and not a plain truncate.</b> This log is the only surviving record of
        /// a play session, and <c>WriteAllText</c> used to destroy it on the <i>next</i> launch. That
        /// happened for real: the run that located the double-<c>Construct</c> root cause was wiped by
        /// the following play-test, and the file went from 36 kB of evidence to a fresh 690-byte header
        /// between two reads. A diagnostic that deletes its own evidence is worse than no diagnostic,
        /// because the loss is silent — the file still looks like a log.</para>
        ///
        /// <para>One previous run is kept, as <c>telekinesis_flight.prev.log</c>, rather than appending
        /// forever: the question a reader asks is "what did the last session do", and an unbounded
        /// append makes that progressively harder to answer. If the roll fails the header is still
        /// written, so a locked destination degrades to the old behaviour instead of losing both runs.
        /// </para>
        /// </summary>
        public static void Begin(string header)
        {
            if (!EnsurePath())
            {
                return;
            }

            try
            {
                string previous = _path + ".prev";
                if (System.IO.File.Exists(_path))
                {
                    if (System.IO.File.Exists(previous))
                    {
                        System.IO.File.Delete(previous);
                    }

                    System.IO.File.Move(_path, previous);
                }
            }
            catch
            {
                // A locked or unreadable destination must not stop the header from being written.
            }

            try
            {
                System.IO.File.WriteAllText(_path, header + "\n");
            }
            catch
            {
                _disabled = true;
            }
        }

        /// <summary>Appends one timestamped record. Never throws.</summary>
        public static void Write(string line)
        {
            if (!EnsurePath())
            {
                return;
            }

            try
            {
                System.IO.File.AppendAllText(
                    _path, $"{UnityEngine.Time.realtimeSinceStartup,8:F2}s  {line}\n");
            }
            catch
            {
                _disabled = true;
            }
        }
    }

    /// <summary>
    /// The telekinesis / teleport trace: one flag, one prefix, so <c>grep "[tele]"</c> in the Console
    /// returns the whole Q path in order.
    ///
    /// <para><b>Why this exists.</b> The grab path has four independent ways to do nothing, and from the
    /// player's seat they are indistinguishable: the key never arrives, the controller has no room, the
    /// cursor is nowhere near a prop, or one gate term refuses. Every one of them used to be silent. A
    /// diagnostic that can only report "it did not work" is what let this feature stay broken through
    /// several correct-looking fixes, so each refusal now names itself.</para>
    ///
    /// <para><b>Reads through, never caches.</b> The flag lives in
    /// <see cref="PFE.Core.PfeDebugSettings"/> and is read live through
    /// <see cref="PFE.Core.DebugOverlays.Settings"/>, so the console (<c>tele on</c>) and the Inspector
    /// drive one value and there is no second copy to fall out of step.</para>
    ///
    /// <para><b>Deliberately not gated by <c>runtimeLoggingEnabled</c>.</b> This is the instrument you
    /// switch on *because* something is broken; a master switch that could silence it would make it lie
    /// about the exact thing it was opened to find.</para>
    /// </summary>
    public static class TelekinesisTrace
    {
        /// <summary>Every line this trace emits starts with this, so the path is greppable as one block.</summary>
        public const string Prefix = "[tele] ";

        /// <summary>Is the trace on? False when the project has no debug-settings asset.</summary>
        public static bool Enabled
        {
            get
            {
                PFE.Core.PfeDebugSettings settings = PFE.Core.DebugOverlays.Settings;
                return settings != null && settings.LogTelekinesisTrace;
            }
        }

        /// <summary>
        /// Emit one trace line, or nothing at all when the toggle is off.
        ///
        /// <para>Callers on a per-frame path should test <see cref="Enabled"/> first: the string
        /// interpolation happens at the call site, so it allocates whether or not this emits.</para>
        /// </summary>
        public static void Log(string message)
        {
            if (Enabled)
            {
                Debug.Log(Prefix + message);
            }
        }
    }
}
