using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using PFE.Core;
using PFE.Core.Messages;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Systems.Interaction;
using PFE.Systems.Map;
using PFE.Systems.Map.Actions;
using PFE.Systems.Map.Scripting;
using PFE.Systems.Map.Serialization;
using PFE.Systems.Physics;
using PFE.Systems.Security;
using PFE.Sim.Campaign;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Presenter component for in-room interactive door props (door1, door1a, door1b, septum, etc.).
    /// Direct port of AS3 Box.as door logic (initDoor, setDoor, attDoor).
    ///
    /// <para>Owns the door's <i>state</i>: solid tile stamping, the open/closed flag, horizontal
    /// ejection when closing while occupied, and the sprite frame for that state. It does <b>not</b>
    /// own input — <see cref="Interact"/> is called by the player's interaction path, and no other
    /// method here reads the keyboard. See the note above <see cref="Update"/> for why.</para>
    ///
    /// <para>Frame changes are <b>seeks, not animations</b>, because that is what AS3 does:
    /// <c>gotoAndStop("open" | "close" | "die")</c> for the three door states, and
    /// <c>gotoAndPlay("comein")</c> for the one Z-door clip (<c>Box.as:508-532</c>).</para>
    /// </summary>
    [RequireComponent(typeof(BoxCollider2D))]
    public class DoorPropPresenter : MonoBehaviour, IInteractable, IHoldInteractable, IInteractableName, IDestructibleTile, ISecurityObject, IProjectilePassThrough
    {
        /// <summary>
        /// Seconds per frame of a played clip. The map-object sprites ship in
        /// <c>texture1.swf</c>, whose header declares <b>24 fps</b> — measured, not assumed. The
        /// old 0.075 was labelled "matching Flash AS3 30 FPS" and matched neither 30 fps (0.0333)
        /// nor the actual asset rate.
        /// </summary>
        public const float FrameDuration = 1f / 24f;

        /// <summary>Map-object source SWF frame rate, for callers that want it by name.</summary>
        public const float SourceFrameRate = 24f;

        private static readonly Color DoorTriggerFillColor = new Color(1.0f, 0.85f, 0.15f, 0.35f);
        private static readonly Color DoorTriggerWireColor = new Color(1.0f, 0.9f, 0.3f, 0.9f);
        private static readonly Color BarricadeFillColor = new Color(0.9f, 0.3f, 0.1f, 0.35f);
        private static readonly Color BarricadeWireColor = new Color(1.0f, 0.4f, 0.2f, 0.9f);
        private static readonly List<DoorPropPresenter> _activePresenters = new List<DoorPropPresenter>();
        private static Sprite _whiteDebugSprite;
        private static bool? _debugOverride;

        private RoomInstance _room;
        private ObjectInstance _objectInstance;
        private MapObjectVisualDefinition _visual;
        private SpriteRenderer _renderer;
        private AreaTriggerSystem _triggerSystem;
        private BoxCollider2D _triggerCollider;
        private GameObject _debugVisualGo;
        private SpriteRenderer _debugSpriteRenderer;
        private CheckpointAreaTrigger _checkpointArea;

        // AS3's `vischeckpoint` is one MovieClip with three named children
        // (`vischeckpoint.as`: osn / lock / fiol), at display-list depths 1 / 13 / 15. The port's
        // regenerated visual asset carries them as three sprite arrays on ONE shared 130x229
        // FullRect canvas, so two child renderers at localPosition zero reproduce that display
        // list. See CheckpointVisualRules for the measured frame counts and label positions.
        private SpriteRenderer _checkpointLockRenderer;
        private SpriteRenderer _checkpointGlowRenderer;

        /// <summary>
        /// The door's <b>solid</b> collider — the thing a weapon actually hits.
        ///
        /// <para><b>Why the door needs one of its own.</b> The component's required
        /// <see cref="BoxCollider2D"/> is the <i>cursor-targeting</i> affordance and is forced to
        /// <c>isTrigger = true</c> by <see cref="Initialize"/>, so both projectile paths skip it:
        /// <c>Projectile.OnTriggerEnter2D</c> returns on <c>other.isTrigger</c>, and
        /// <c>GatherUnitBoxes</c> filters <c>useTriggers = false</c>. The door's <i>tiles</i> cannot
        /// stand in either, because AS3's <c>initDoor</c> stamps them at run time: their cells are
        /// <c>_</c> (air) in the shipped room maps — measured on <c>RoomsCanter.as</c>
        /// "проходы3" — so <c>TileVisualManager.CreateAllTiles</c> built them no collider and
        /// <c>ApplyTileCollision</c> can only change the <c>TileData</c>. The result was a closed
        /// door that stopped the <i>player</i> (whose collision is the tile grid) and let every
        /// round pass straight through. This collider is that missing surface.</para>
        ///
        /// <para><b>It is on this GameObject, not a child, on purpose.</b> Both the projectile and
        /// the melee sweep resolve the impact by <c>GetComponent&lt;IDestructibleTile&gt;()</c> on the
        /// collider's own GameObject; a child would resolve to nothing and the round would stop on an
        /// invulnerable slab.</para>
        ///
        /// <para>Enabled only while the box is closed and alive — see
        /// <see cref="RefreshSolidCollider"/> — which is AS3's <c>phis</c> toggle
        /// (<c>Box.setDoor</c>, <c>Box.as:684</c>): an open door is not a target.</para>
        /// </summary>
        private BoxCollider2D _solidCollider;

        /// <summary>Remaining hit points, AS3 <c>Box.hp</c>.</summary>
        private float _doorHitPoints;

        /// <summary>Minimum destroy value that does anything at all, AS3 <c>Box.thre</c>.</summary>
        private float _doorThreshold;

        /// <summary>AS3 <c>Box.as:285-289</c> — a placed <c>indestruct</c> pins hp and thre at 10000.</summary>
        private bool _doorIndestructible;

        /// <summary>Whether <see cref="_doorHitPoints"/> has been resolved from the definition yet.</summary>
        private bool _durabilityResolved;

        /// <summary>Whether the two bullet-proxy attributes have been read yet.</summary>
        private bool _bulletProxyResolved;

        /// <summary>AS3 <c>Box.un</c> exists — the definition authors <c>actdam</c>.</summary>
        private bool _hasBulletDamageProxy;

        /// <summary>AS3 <c>VirtualUnit.nTipDam</c> — the placement's <c>tipdam</c>, or -1 for "any".</summary>
        private int _requiredTipDamage;

        public static bool ShowDebugVisuals
        {
            get
            {
                if (_debugOverride.HasValue) return _debugOverride.Value;
                var settings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
                return settings != null && settings.ShowDoorColliderDebug;
            }
            set
            {
                _debugOverride = value;
                var settings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
                if (settings != null)
                {
                    settings.ShowDoorColliderDebug = value;
                }
                UpdateAllDebugVisuals();
            }
        }

        public static void SetDebugOverride(bool? overrideValue)
        {
            _debugOverride = overrideValue;
            UpdateAllDebugVisuals();
        }

        public static void UpdateAllDebugVisuals()
        {
            for (int i = _activePresenters.Count - 1; i >= 0; i--)
            {
                if (_activePresenters[i] != null)
                {
                    _activePresenters[i].UpdateDebugVisual();
                }
                else
                {
                    _activePresenters.RemoveAt(i);
                }
            }
        }

        private void OnEnable()
        {
            if (!_activePresenters.Contains(this))
            {
                _activePresenters.Add(this);
            }
            UpdateDebugVisual();
        }

        private void OnDisable()
        {
            _activePresenters.Remove(this);
            if (_debugVisualGo != null)
            {
                _debugVisualGo.SetActive(false);
            }
        }

        /// <summary>
        /// How close the player must be to work this door — AS3 <c>World.w.actionDist</c>, shared
        /// with the player's own target search so the two cannot disagree.
        /// </summary>
        public const float ActionReach = WorldConstants.ACTION_REACH;

        private int _lastInteractFrame = -1;

        private ObjectActionDispatcher _objectActions;

        /// <summary>
        /// The <c>allact</c> dispatcher — the port of AS3's <c>Interact.act()</c> switch.
        ///
        /// <para>Built lazily rather than in <c>Awake</c> so <c>RoomTransitionManager.Instance</c> is
        /// resolved once the scene is up, and settable so a test can supply a double instead of a live
        /// manager. Resolving that manager at call time is what the rest of the codebase does
        /// (<c>DoorTrigger.cs:117</c>, <c>TilePhysicsController.cs:696</c>).</para>
        /// </summary>
        public ObjectActionDispatcher ObjectActions
        {
            get
            {
                if (_objectActions == null)
                {
                    _objectActions = CreateSceneDispatcher();
                }

                return _objectActions;
            }
            set => _objectActions = value;
        }

        /// <summary>
        /// The one place the scene's <c>allact</c> dispatcher is composed.
        ///
        /// <para>Public and static because two callers need it and they must not disagree:
        /// <see cref="DoorPropPresenter"/> to run a script, and <see cref="RoomObjectVisualManager"/> to
        /// decide whether an object <i>has</i> a runnable script at all. Two copies of this wiring is how
        /// one of them silently stops registering an action — and "registered but unreachable" is exactly
        /// the failure this seam exists to prevent (see the admission rule in
        /// <see cref="RoomObjectVisualManager"/>).</para>
        ///
        /// <para>Both dependencies are read at call time, so the caller must be past
        /// <c>CampaignManager.Initialize</c> for <c>map</c> to be registered. Both are tolerated as
        /// null: the wall map is then reported as unhandled rather than throwing out of an
        /// interaction.</para>
        ///
        /// <para><b>Three of the five capabilities come from two objects.</b>
        /// <see cref="PFE.Systems.Campaign.CampaignManager"/> is both the travel-map host (<c>map</c>) and
        /// the land-script host (<c>exit</c>), and
        /// <see cref="PFE.Systems.Map.Streaming.RoomTransitionManager"/> is both the layer-transition seam
        /// (<c>comein</c>) and the prob-room host (<c>prob</c> / <c>probreturn</c>). They are passed
        /// separately rather than as one bag so a test can double exactly the capability under test —
        /// <c>exit</c> and <c>map</c> have nothing to do with each other, and a test for the descent loop
        /// must not need a transition manager to exist.</para>
        /// </summary>
        public static ObjectActionDispatcher CreateSceneDispatcher()
        {
            // `map` needs the campaign state, which is not a MonoBehaviour and so cannot be found with
            // FindFirstObjectByType; CampaignManager.Current is the accessor for exactly this case.
            PFE.Systems.Campaign.CampaignManager campaign = PFE.Systems.Campaign.CampaignManager.Current;
            PFE.Systems.Map.Streaming.RoomTransitionManager transition =
                PFE.Systems.Map.Streaming.RoomTransitionManager.Instance;

            // CreateFull rather than CreateDefault: it adds the tagged broadcast as the fallback and the
            // thirteen terminal branches. Without the broadcast, 210 of the 214 placement-level `allact`
            // attributes in the shipped rooms — every button in the game — answer Unhandled and fall
            // through to opening the button itself.
            return ObjectActionDispatcher.CreateFull(
                transition,
                travelMapHost: campaign,
                exitHost: campaign,
                probHost: transition,
                terminalHost: CreateTerminalHost(),
                verbHost: new ObjectVerbHost
                {
                    // AS3 setAct("open", n) → setDoor(): restamp the tiles the object covers.
                    StampDoorTiles = (room, obj, isOpen) =>
                        PFE.Systems.Map.Scripting.AreaTriggerSystem.UpdateDoorTileCollision(room, obj, isOpen),

                    // AS3 `loc.prob.check()` — reached from `setAct("open")` (Interact.as:880) and from
                    // the `dam` verb's one-shot arm (:1753). This dispatcher had no binder for it, and
                    // it is the one that runs a button's press, so a `buttons4` button lit its lamp and
                    // the prob room never noticed. Shared with AreaTriggerSystem rather than copied:
                    // the body is the same query against the same scene singleton.
                    CheckProbRoom = PFE.Systems.Map.Scripting.AreaTriggerSystem.CheckProbRoomFor,

                    // The `scrAct.start()` tail of actOsn (:1323-1325). Resolved through the live
                    // presenter because the runner is per-room state this static composition cannot
                    // hold — see RunInlineScriptFor.
                    RunInlineScript = RunInlineScriptFor,
                });
        }

        /// <summary>
        /// The scene dispatcher's binder for <see cref="ObjectVerbHost.RunInlineScript"/> — finds the
        /// live presenter for an object and runs that room's inline script through its trigger system.
        ///
        /// <para><b>A lookup rather than a captured delegate, because the two lifetimes differ.</b> The
        /// dispatcher is composed once per scene and serves every room; the script runner is rebuilt
        /// with each room, and only the presenter knows both the instance and the live
        /// <c>AreaTriggerSystem</c>. <see cref="_activePresenters"/> is the registry the debug overlay
        /// already maintains, so this adds no new bookkeeping and cannot outlive a room.</para>
        ///
        /// <para><b>No presenter means no script, not a throw.</b> An object can be asked to run its
        /// script after its room was torn down (a queued broadcast, a destroyed presenter); the oracle
        /// would run it against a detached box and change nothing visible, and a silent no-op is the
        /// closest honest equivalent.</para>
        /// </summary>
        public static void RunInlineScriptFor(RoomInstance room, ObjectInstance obj)
        {
            if (obj == null) return;

            for (int i = 0; i < _activePresenters.Count; i++)
            {
                DoorPropPresenter presenter = _activePresenters[i];
                if (presenter == null || presenter._objectInstance != obj) continue;

                presenter._triggerSystem?.OnObjectInteracted(room ?? presenter._room, obj);
                return;
            }
        }

        /// <summary>
        /// The capabilities the terminal <c>allact</c> branches need — AS3's
        /// <c>World.w.gui.infoText</c>, <c>World.w.pip</c>, <c>unit.hack()</c> and
        /// <c>loc.allAct(owner, "hack", "")</c>.
        ///
        /// <para><b>Why this exists now and did not before.</b> <c>CreateFull</c> was called with no
        /// terminal host, so it built an empty one and every capability reported itself as unwired —
        /// which is why <c>hack_robot</c> and <c>hack_lock</c> were documented as "unhandled" while
        /// being registered. Registering a branch and wiring it are two different steps, and the
        /// dispatcher's tests could only see the first.</para>
        ///
        /// <para><b><c>HackUnits</c> and <c>Broadcast</c> both land in the security runtime</b>, because
        /// both are the same sweep the player can also reach by picking a lock. The oracle routes the
        /// second one through <c>loc.allAct</c> → each object's <c>command("hack")</c>; the port's
        /// object-verb broadcast has no <c>hack</c> verb, so the sweep is called directly. Same set of
        /// objects, one hop fewer.</para>
        /// </summary>
        private static TerminalActionHost CreateTerminalHost()
        {
            // `pip.workTip = allact` then `pip.onoff(7)` (Interact.as:1621-1625). The two calls are one
            // branch in the oracle and two host members here, so the station is captured between them:
            // SetWorkTip always runs first (TerminalActions.cs:309-311), and OpenPipPage carries it.
            string pendingWorkTip = string.Empty;

            return new TerminalActionHost
            {
                // AS3 `World.w.gui.infoText(key)`. The port has no info strip; the log is the honest
                // channel and the F2 Security tab renders the same events.
                InfoText = key => Debug.Log($"[Terminal] {key}"),

                // AS3 `World.w.gui.bulb(X, Y)` — the flash at the terminal. Cosmetic, and the port's
                // particle emitter is per-room rather than global; logged rather than faked.
                Bulb = (room, obj) => Debug.Log($"[Terminal] bulb at {obj?.objectId} in {room?.id}"),

                // AS3 `for each(unit) unit.hack(World.w.pers.security)` (Interact.as:1585).
                HackUnits = room =>
                {
                    GameObject player = SecurityRuntime.PlayerObject;
                    int security = player != null ? SecurityRuntime.ReadActor(player).Security : 0;
                    SecurityRuntime.HackUnitsInRoom(room, security);
                },

                // AS3 `for each(obj) obj.inter.command("hack")` (Interact.as:1592-1598).
                Broadcast = (room, sender, verb, allId) =>
                {
                    if (verb == "hack")
                    {
                        SecurityRuntime.HackRoom(room);
                    }
                },

                // AS3 `this.setAct("open", 1)` (Interact.as:1653-1658) — the robocell and the alarm
                // panel both end here, and `setAct("open",1)` is also what clears their own `allact`
                // (Interact.as:869-874). The alarm panel additionally stops the room alarm, which is
                // the only way to end one early.
                SetOpen = (room, obj) =>
                {
                    if (obj?.runtimeState != null)
                    {
                        obj.runtimeState.isOpen = true;
                    }

                    PFE.Systems.Map.Scripting.AreaTriggerSystem.UpdateDoorTileCollision(room, obj, true);
                    SecurityRuntime.ClearAlarm(room);
                },

                // AS3 `World.w.pip.workTip = this.allact` (Interact.as:1621).
                SetWorkTip = workTip =>
                {
                    pendingWorkTip = workTip ?? string.Empty;
                },

                // AS3 `World.w.pip.onoff(page, 0)` — page 7 for `work`/`lab`/`stove` (Interact.as:1624),
                // page 8 for `app` (:1626-1629), page 9 for `vault` (:1646-1649).
                OpenPipPage = (page, subPage) =>
                {
                    PFE.Core.PipPageRouter.Open(page, subPage, page == 7 ? pendingWorkTip : string.Empty);
                },
            };
        }

        private int _currentFrameIndex;
        private int _targetFrameIndex;
        private float _frameTimer;

        /// <summary>Index of the running <c>comein</c> clip, or -1 when it is not playing.</summary>
        private int _comeInFrameIndex = -1;

        // ── Checkpoint child-clip playback (AS3 CheckPoint.as:61-76, :214-233, :285-286) ─────
        //
        // `vis.osn` is the body and uses the presenter's OWN renderer, so its frame is
        // `_currentFrameIndex` / `_visual.frames`. `vis.lock` and `vis.fiol` are the two children
        // above. Unlike a door (whose frames are all `gotoAndStop` seeks), a fresh checkpoint
        // `play()`s both clips once at 24 fps and leaves them on their last frame.
        private int _checkpointLockFrame = -1;   // -1 = AS3 `vis.lock.visible = false`
        private int _checkpointGlowFrame;        // 0 = AS3 `vis.fiol.gotoAndStop(1)`, transparent
        private bool _checkpointBodyPlaying;
        private bool _checkpointGlowPlaying;
        private float _checkpointFrameTimer;
        private bool _checkpointLayersBuilt;

        /// <summary>Structural test, not an id: only the regenerated composite has both child
        /// clips. A visual imported before the fix has one flattened frame and must keep the old
        /// door behaviour rather than animating a 45-frame sheet it does not own.</summary>
        private bool IsCheckpointVisual =>
            IsCheckpointObject && _visual != null && _visual.IsCheckpointVisual;

        public RoomInstance Room => _room;
        public ObjectInstance ObjectInstance => _objectInstance;
        public bool IsOpen => _objectInstance?.runtimeState?.isOpen ?? false;

        /// <summary>
        /// Whether this prop has been wrecked — AS3's <c>Box.setVisState("die")</c> state
        /// (<c>Box.as:810</c>). Reads the same two flags <c>RoomObjectVisualManager.ResolveFrameIndex</c>
        /// uses, so a door and a container agree on what "destroyed" means.
        /// </summary>
        public bool IsDestroyed =>
            _objectInstance?.runtimeState != null &&
            (_objectInstance.runtimeState.isDestroyed || _objectInstance.runtimeState.isExploded);

        /// <summary>
        /// Determines whether this door prop can be opened/closed by the player via interact key.
        /// Faithful to Flash AS3 (fe.loc.Box / fe.serv.Interact):
        /// - Only objects with inter.action == 1 (door1, door1a, door2, etc.) are interactive doors.
        /// - Destructible/solid barriers like septum (wooden wall / barricade), grate, hgrate, window1/2
        ///   have inter="0" (or no inter action) and cannot be opened via interaction.
        /// </summary>
        public bool IsInteractableDoor
        {
            get
            {
                if (_objectInstance == null) return false;

                // Explicit room instance attribute override (e.g. inter="0" in room XML)
                string interAttr = _objectInstance.GetAttribute("inter", null);
                if (!string.IsNullOrEmpty(interAttr))
                {
                    if (int.TryParse(interAttr, out int interVal))
                    {
                        return interVal > 0;
                    }
                }

                // Explicit blacklist for barrier/obstacle props that stamp wall collision but never open
                string id = _objectInstance.GetResolvedDefinitionId();
                if (string.Equals(id, "septum", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, "grate", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, "hgrate", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, "window1", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, "window2", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, "platform1", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Check definition attribute
                if (_objectInstance.definition != null)
                {
                    string defInter = _objectInstance.definition.GetAttribute("inter", null);
                    if (!string.IsNullOrEmpty(defInter))
                    {
                        if (int.TryParse(defInter, out int defInterVal))
                        {
                            return defInterVal > 0;
                        }
                    }

                    // If definition is explicitly marked non-interactive or has empty/0 interactionMode
                    if (!_objectInstance.definition.isInteractive ||
                        string.IsNullOrEmpty(_objectInstance.definition.interactionMode) ||
                        _objectInstance.definition.interactionMode == "0")
                    {
                        return false;
                    }
                }

                // Known openable door families
                return id.StartsWith("door", StringComparison.OrdinalIgnoreCase) ||
                       id.StartsWith("hatch", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(id, "stdoor", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(id, "basedoor", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(id, "encldoor", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(id, "enclpole", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(id, "alib1", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(id, "alib2", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Whether this object is a <b>door box</b> — AS3's <c>door=</c> rule
        /// (<c>Box.as:290-297</c>), resolved by <see cref="ObjectInstance.IsDoorBox"/>.
        ///
        /// <para><b>This is not the same question as <see cref="IsInteractableDoor"/>, and conflating
        /// them is what walled off the Z doors.</b> <see cref="IsInteractableDoor"/> answers "may the
        /// player work this?" (it is true for the <c>inter='8'</c> Z doors); this answers "does it have
        /// a solid closed state that <c>initDoor</c>/<c>setDoor</c> manage?" — which for the Z doors is
        /// <b>no</b>: they author no <c>door=</c>, so AS3 never stamps their tiles and never gives them
        /// an open/close state. Only the tile stamping and the open/close toggle hang off this; the
        /// trigger collider and the <c>allact</c> dispatch hang off the other.</para>
        /// </summary>
        public bool IsDoorBox => _objectInstance != null && _objectInstance.IsDoorBox();

        /// <summary>
        /// Whether this object is AS3's <b>"button pressed by bullets"</b> — a definition that authors
        /// <c>actdam</c>, which makes <c>Box</c> bind a <c>VirtualUnit</c> to itself
        /// (<c>Box.as:410-419</c>). <c>knop2</c> is the only such row in the shipped data
        /// (<c>actdam='1'</c>, <c>AllData.as</c>), and it has 41 placements across 12 rooms.
        ///
        /// <para><b>Presence, not value.</b> The oracle tests <c>node.@actdam.length()</c>, so
        /// <c>actdam="0"</c> would still bind a proxy. <see cref="ShootButtonRules.HasBulletDamageProxy"/>
        /// owns that reading; this property only remembers the answer.</para>
        ///
        /// <para><b>Why the port needs the flag at all.</b> AS3 pushes the bound <c>VirtualUnit</c> into
        /// <c>loc.units</c> (<c>Location.as:2007-2009</c>), so a bullet meets the button through the
        /// ordinary unit sweep and calls <c>owner.command("dam")</c> (<c>VirtualUnit.as:40-48</c> →
        /// <c>Interact.as:1744-1758</c>). The port has no virtual unit: the surface a round can reach is
        /// <see cref="_solidCollider"/>, and the press is <see cref="ApplyDestruction"/>'s first branch.
        /// Both hang off this flag.</para>
        /// </summary>
        public bool HasBulletDamageProxy
        {
            get
            {
                ResolveBulletProxy();
                return _hasBulletDamageProxy;
            }
        }

        /// <summary>AS3 <c>Interact.knop</c> — the placement's <c>knop</c> overriding the definition's (<c>Interact.as:304-306</c>, <c>:403-405</c>).</summary>
        private bool IsAuthoredKnob => ShootButtonRules.IsKnob(
            _objectInstance != null ? _objectInstance.GetAttribute("knop", string.Empty) : string.Empty,
            ReadDefinitionAttribute(_objectInstance, "knop"));

        /// <summary>AS3 <c>Interact.action</c> — the placement's <c>inter</c> overriding the definition's (<c>Interact.as:292</c>, <c>:397-400</c>).</summary>
        private int InteractionMode => ShootButtonRules.ResolveInteractionMode(
            _objectInstance != null ? _objectInstance.GetAttribute("inter", string.Empty) : string.Empty,
            ReadDefinitionAttribute(_objectInstance, "inter"));

        /// <summary>
        /// Reads the two authored attributes a bullet proxy needs, once per presenter.
        ///
        /// <para><b>The two attributes live on different nodes.</b> <c>actdam</c> is on the
        /// <i>definition</i> (<c>AllData.as</c>, <c>id='knop2'</c>) and <c>tipdam</c> on the
        /// <i>placement</i> — <c>Box.as:412-418</c> calls <c>bindUnit(xml.@tipdam)</c>, where
        /// <c>xml</c> is the placed node. Reading either from the other node answers "no proxy" or
        /// "no filter" for every shipped button, which is why this is spelled out rather than inlined
        /// at one call site.</para>
        /// </summary>
        private void ResolveBulletProxy()
        {
            if (_bulletProxyResolved) return;
            _bulletProxyResolved = true;

            if (_objectInstance == null) return;

            _hasBulletDamageProxy = ShootButtonRules.HasBulletDamageProxy(
                ReadDefinitionAttribute(_objectInstance, "actdam"));

            _requiredTipDamage = ShootButtonRules.ResolveTipDamage(
                _objectInstance.GetAttribute("tipdam", string.Empty));
        }

        /// <summary>
        /// A weapon's hit on the button — AS3 <c>VirtualUnit.udarBullet</c> / <c>VirtualUnit.damage</c>
        /// (<c>VirtualUnit.as:30-48</c>), whose entire effect is <c>owner.command("dam")</c>.
        ///
        /// <para><b>A <c>tipdam</c> mismatch still consumes the round.</b> The oracle returns <c>0</c>,
        /// and <c>Bullet.as:535</c> tests <c>_loc4_ &gt;= 0</c>, so the round stops on the button with a
        /// surface sound and simply does not press it. Returning <c>false</c> there would let a
        /// wrong-damage-type round fly through a solid button — a different game.</para>
        ///
        /// <para><b>Two arms, chosen by the same rule the verb uses.</b> A knob whose placement authored
        /// <c>inter="0"</c> opens once and pokes the prob room (<c>Interact.as:1749-1753</c>); everything
        /// else runs <c>actOsn</c> — the lamp toggle, the <c>allact</c> broadcast and the inline
        /// <c>&lt;scr&gt;</c>. Deliberately the same code a player's press runs rather than a second
        /// copy: in AS3 both land in <c>actOsn</c>.</para>
        /// </summary>
        public bool PressByDamage(DamageType damageType)
        {
            if (!HasBulletDamageProxy) return false;

            if (!ShootButtonRules.AcceptsDamageType(_requiredTipDamage, (int)damageType))
            {
                return true;
            }

            if (ShootButtonRules.PressesOnce(IsAuthoredKnob, InteractionMode))
            {
                OpenOnceByDamage();
            }
            else
            {
                RunActOsn(null);
            }

            return true;
        }

        /// <summary>
        /// The one-shot arm: <c>this.setAct("open",1); if(this.loc.prob) this.loc.prob.check();</c>
        /// (<c>Interact.as:1749-1753</c>).
        ///
        /// <para><b>Not <see cref="SetOpen"/>, and that is not a shortcut.</b> <c>SetOpen</c> is the
        /// <i>door's</i> state change: it refuses when <c>!IsInteractableDoor</c> — which is exactly what
        /// an <c>inter="0"</c> placement is — ejects overlapping units, restamps tiles and raises the
        /// inline script. AS3's <c>setAct("open")</c> for a knob does none of those, because its
        /// <c>setDoor()</c> half is gated on <c>door &gt; 0</c> (<c>Interact.as:853-856</c>). So this
        /// writes the flag, seeks the lamp and pokes the prob room, which is the whole arm.</para>
        /// </summary>
        private void OpenOnceByDamage()
        {
            _objectInstance.EnsureStructuredData();
            _objectInstance.runtimeState.isOpen = true;

            // `if(this.open) { this.lock = this.mine = 0; }` (Interact.as:866-869) — opening clears
            // both, which is what lets a shot button release a mined or locked object it targets.
            _objectInstance.runtimeState.hasMineValue = false;
            _objectInstance.runtimeState.mineValue = 0;
            _objectInstance.runtimeState.hasLockValue = false;
            _objectInstance.runtimeState.lockValue = 0f;

            UpdateVisualFrame();
            PFE.Systems.Map.Scripting.AreaTriggerSystem.CheckProbRoomFor(_room);
        }

        /// <summary>
        /// AS3 <c>phis == 2</c> — true while this closed door is a <b>level-2 solid</b>, so a bullet
        /// flies through it (<c>weapon/Bullet.as:476</c> tests <c>phis == 1</c>).
        ///
        /// <para><b>Only <c>grate</c> and <c>hgrate</c> answer true</b> — the two rows in the data that
        /// author <c>phis='2'</c> (<c>AllData.as:4859-4860</c>). Every other door, including the
        /// <c>phis</c>-less ones that default to 1 (<c>Box.as:52</c>), answers false and stops rounds
        /// exactly as before.</para>
        ///
        /// <para><b>Why the answer is "closed and alive" as well.</b> The solid collider only exists in
        /// that state (<see cref="RefreshSolidCollider"/>), and AS3 zeroes <c>phis</c> on both the open
        /// and the dead path (<c>Box.as:684</c>, <c>:807</c>), so there is no level-2 solid left to ask
        /// about. Answering true there would be harmless but would claim a surface that is not there.</para>
        /// </summary>
        public bool PassesProjectiles =>
            IsDoorBox && !IsOpen && !IsDestroyed && ResolveClosedDoorPhisLevel() == 2;

        public string ActionText
        {
            get
            {
                if (!IsInteractableDoor) return string.Empty;

                // A lock owns the prompt while it exists — AS3's update() writes `actionText` from
                // `lock`/`lockTip` before it ever considers open/close (`Interact.as:732-776`), so a
                // locked door reads "Pick lock", not "Open".
                string locked = SecurityGate.Prompt(_objectInstance, _room, null);
                if (locked != null) return locked;

                return IsOpen ? "Close" : "Open";
            }
        }

        /// <summary>
        /// The door's display name — AS3 <c>celObj.nazv</c>, the first line of the world-anchored
        /// interaction prompt (<c>GUI.as:1286</c>). Read from the object's definition, which
        /// <c>MapObjectDefinitionImporter</c> fills from the <c>AllData</c> row's own name; falls back to
        /// the object id so an unnamed door still labels something rather than rendering a blank line.
        /// </summary>
        public string DisplayName
        {
            get
            {
                string named = _objectInstance != null && _objectInstance.definition != null
                    ? _objectInstance.definition.displayName
                    : null;

                if (!string.IsNullOrEmpty(named)) return named;
                return _objectInstance != null ? _objectInstance.objectId : string.Empty;
            }
        }

        /// <summary>
        /// Frames the action key must be held before this door opens or closes — AS3
        /// <c>Interact.t_action</c>, authored as <c>time</c> on the object.
        ///
        /// <para>Read from the imported object rather than hard-coded, so one presenter serves both
        /// kinds of door. The seven Z doors author <c>time='10'</c> and are held for a third of a
        /// second; the metal doors and terminals author 15/20/30; and plain <c>door1</c>/<c>door2</c>/
        /// <c>hatch</c> author nothing at all, so this returns 0 and the caller keeps acting on the
        /// press — which is why adding the hold changed nothing for them.</para>
        ///
        /// <para><b>A locked object always has a hold, even with no authored <c>time</c>.</b> AS3 adds
        /// the pick duration to the authored one (<c>UnitPlayer.as:1976</c>:
        /// <c>t_action = inter.t_action + pers.getLockPickTime(lock, tip)</c>), so a <c>door1</c> with
        /// no <c>time</c> still costs a full second to pick. Returning 0 there would fire the attempt on
        /// the press and make lockpicking free.</para>
        /// </summary>
        public int HoldFrames
        {
            get
            {
                int authored = _objectInstance != null ? _objectInstance.GetHoldFrames() : 0;
                return authored + SecurityGate.ExtraHoldFrames(_objectInstance, _room);
            }
        }

        /// <summary>
        /// This door's world position — what the hold's range guard measures against, and the
        /// coordinates the effect runs with.
        ///
        /// <para>Same value the presenter already hands to <see cref="ObjectActionContext"/>, so a
        /// held <c>comein</c> arrives at the same cell it would have reached before the hold
        /// existed.</para>
        /// </summary>
        public Vector3 WorldPosition => transform.position;
        public int CurrentFrame => _currentFrameIndex;
        public int TargetFrame => _targetFrameIndex;

        public void Initialize(
            RoomInstance room,
            ObjectInstance obj,
            MapObjectVisualDefinition visual,
            SpriteRenderer renderer,
            AreaTriggerSystem triggerSystem)
        {
            _room = room;
            _objectInstance = obj;
            _visual = visual;
            _renderer = renderer;
            _triggerSystem = triggerSystem;

            _triggerCollider = GetComponent<BoxCollider2D>();
            if (_triggerCollider != null)
            {
                _triggerCollider.isTrigger = true;
                ConfigureCollider();
            }

            ResolveDurability();
            ResolveBulletProxy();

            // AS3 applies the placement's `open` attribute before any door state exists (Interact's
            // constructor, Interact.as:325-329), so it has to land before the tile stamping below or the
            // first stamp would use the wrong state.
            ApplyAuthoredInitialOpen(obj);

            // In AS3 (Box.as:653), initDoor stamps solid collision if closed — but initDoor is only
            // reached when the definition authors `door=` (Box.as:290-297). A `comein` Z door does not,
            // and it stands on open ground (RoomsCamp.as places indoor2 at 16,15 and 16,7; both are `_`
            // in the imported tile map). Stamping Wall there would seal the doorway the player is
            // supposed to walk into, so the stamping follows IsDoorBox, not IsInteractableDoor.
            if (IsDoorBox)
            {
                // A door restored from a save in its wrecked state must not be re-stamped solid. The
                // flag round-trips through RoomStateSnapshot, so without this a re-Initialize after a
                // load would rebuild the wall the player had already shot out.
                if (IsDestroyed)
                {
                    ApplyDestroyedTiles();
                }
                else
                {
                    ApplyTileCollision(IsOpen);
                }
            }

            RefreshSolidCollider();
            StampSurfaceMaterial();

            EnsureCheckpointArea();

            // Build the `vis.lock` / `vis.fiol` children and seek all three clips to the pose the
            // oracle's constructor would have left them at, BEFORE the generic frame update runs.
            EnsureCheckpointLayers();
            InitializeCheckpointVisual();

            UpdateVisualFrame();
        }

        /// <summary>
        /// Declare what this door is <b>made of</b>, so a weapon that lands on it makes the sound the
        /// oracle gives it.
        ///
        /// <para><b>The gap this closes.</b> In AS3 a door is not a separate hit target: its cells are
        /// <c>phis == 1</c> tiles stamped by <c>Box.initDoor</c>, and a round or a blade reaches
        /// <c>Location.hitTile</c> → <c>Bullet.sound(tile.mat)</c> — the <i>door's own</i> material
        /// (<c>door1</c> is <c>mat='3'</c> → <c>hit_wood</c>, <c>door1b</c> is <c>mat='1'</c> →
        /// <c>hit_metal</c>). The port has no collider on those cells, so <see cref="_solidCollider"/>
        /// stands in for them — and a bare <c>BoxCollider2D</c> answers "no material", which
        /// <see cref="ImpactSoundResolver"/> reads as "silent". Hitting a closed door therefore made no
        /// sound at all while hitting the wall beside it thudded.</para>
        ///
        /// <para><b>The index is the raw AS3 <c>mat</c>, not a <see cref="MaterialType"/>.</b>
        /// <c>MapObjectDefinition.materialId</c> is the <c>&lt;obj mat='…'&gt;</c> attribute untouched, and
        /// the two numberings disagree from 2 upward — <see cref="TileMaterialRules"/> owns the table and
        /// <see cref="TileSurface.FromAs3Material"/> is the wrapper, the same pair the tile path uses.
        /// </para>
        /// </summary>
        private void StampSurfaceMaterial()
        {
            if (_objectInstance == null || _objectInstance.definition == null) return;

            var surface = GetComponent<TileSurface>();
            if (surface == null) surface = gameObject.AddComponent<TileSurface>();

            surface.Set(TileSurface.FromAs3Material(_objectInstance.definition.materialId));
        }

        /// <summary>
        /// Applies a placement's authored <c>open</c> attribute, once per object instance.
        ///
        /// <para>AS3: <c>Interact</c>'s constructor ends with
        /// <c>if(this.xml.@open.length()) { this.setAct("open",1); this.update(); }</c>
        /// (<c>Interact.as:325-329</c>). Two details are load-bearing:</para>
        /// <list type="bullet">
        /// <item><b>It tests presence, not the value.</b> <c>open="0"</c> opens the object just as
        /// <c>open="1"</c> does — the attribute is a flag, not a boolean. An empty <c>open=""</c> has
        /// <c>length() == 0</c> and does <i>not</i> fire.</item>
        /// <item><b>It runs once, at build time.</b> <see cref="ObjectInstance.initialOpenApplied"/> is the
        /// latch that keeps it one-shot across the port's presenter rebuilds.</item>
        /// </list>
        ///
        /// <para><b>The case this exists for:</b> <c>rooms_begin/room_4_1</c> places <c>platform1</c> at
        /// (27,16) with <c>open="1"</c> — the solid platform starts <i>retracted</i> (tiles stamped Air),
        /// and the knop at (28,22) swaps it. Without this the platform started solid, so the button's
        /// first press was the only thing that could have cleared it and the room read as a dead end.</para>
        /// </summary>
        private void ApplyAuthoredInitialOpen(ObjectInstance obj)
        {
            if (obj == null || obj.initialOpenApplied) return;
            obj.initialOpenApplied = true;

            string open = obj.GetAttribute("open", null);
            if (string.IsNullOrEmpty(open)) return;

            obj.runtimeState.isOpen = true;
        }

        private void GetDoorBounds(out Vector2 size, out Vector2 offset)
        {
            if (_renderer != null && _renderer.sprite != null)
            {
                size = (Vector2)_renderer.sprite.bounds.size;
                offset = (Vector2)_renderer.sprite.bounds.center;
                return;
            }

            float w = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.x * 0.01f) : 0.4f;
            float h = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.y * 0.01f) : 0.8f;
            size = new Vector2(w, h);
            offset = Vector2.zero;
        }

        private void ConfigureCollider()
        {
            if (_triggerCollider == null) return;

            // If the player cannot work this object at all (a septum barricade, or a prop that only
            // stamps collision), disable the trigger collider so it never captures cursor raycasts or
            // proximity interaction scans.
            //
            // This test used to be `!IsInteractableDoor` alone, which silently disabled the collider on a
            // CHECKPOINT — a checkpoint authors no `inter` attribute and its `interactionMode` is empty,
            // so IsInteractableDoor is false for it. With the collider disabled,
            // Physics2D.OverlapCircleAll at the cursor could never hit it, so PlayerController's
            // FindCursorTarget returned null and the checkpoint was untargetable no matter what
            // CanInteract answered. Two gates had to pass and both were closed; fixing only CanInteract
            // would have left E still doing nothing.
            if (!AdmitsPlayerInteraction(IsInteractableDoor, IsCheckpointObject))
            {
                _triggerCollider.enabled = false;
                return;
            }

            _triggerCollider.enabled = true;

            if (_visual != null && _visual.IsCheckpointVisual && _visual.artBoundsPixelsOverride.x > 0)
            {
                // The three checkpoint layers share a padded 130x229 FullRect canvas. Sizing this
                // cursor target off the sprite would make it 1.3 x 2.29 world units, most of it
                // empty air above and below the machine. A tight-trimmed import used to hand this
                // method the drawn 82x122 art, so pin the box to the measured 80x120 instead.
                // The manager lowers the transform by artBottomInsetPixels, putting the art's
                // centre (inset + half the art height) above it.
                Vector2Int art = _visual.artBoundsPixelsOverride;
                float ppu = Mathf.Max(1, _visual.pixelsPerUnit);
                _triggerCollider.size = new Vector2(art.x / ppu, art.y / ppu);
                _triggerCollider.offset = new Vector2(
                    0f, (_visual.artBottomInsetPixels + art.y * 0.5f) / ppu);
                return;
            }

            GetDoorBounds(out Vector2 size, out Vector2 offset);
            _triggerCollider.size = size;
            _triggerCollider.offset = offset;
        }

        /// <summary>
        /// Builds the checkpoint's walk-into area — AS3 <c>CheckPoint</c>'s own <c>Area</c> with
        /// <c>over = areaActivate</c> (<c>CheckPoint.as:90-92</c>).
        ///
        /// <para><b>A child object with its own collider, deliberately not this component's.</b> The
        /// presenter's own <see cref="BoxCollider2D"/> is the port's <i>cursor-targeting</i> affordance
        /// and is sized off the sprite; AS3's area is the <c>size</c>x<code>wid</code> tile box
        /// (<c>:48-53</c>). Sizing them separately means adding the walk-into path cannot move the
        /// region the cursor can select. AS3 does use one box for both — <c>onCursor</c> reads the same
        /// <c>X1/X2/Y1/Y2</c> at <c>:295</c> — so the sprite-sized target box remains a divergence,
        /// recorded here rather than silently widened into the new code.</para>
        ///
        /// <para><b>A main checkpoint gets no area.</b> AS3 sets <c>this.area = null</c> for one
        /// (<c>:127</c>) — it is teleported from, never walked into. The <c>main</c> normalisation that
        /// makes its activation refuse would already stop the walk-in from doing anything, but the
        /// oracle also declines to <i>create</i> the area, so the trigger is not built at all.</para>
        /// </summary>
        private void EnsureCheckpointArea()
        {
            if (!IsCheckpointObject || _objectInstance == null) return;
            if (_checkpointArea != null) return;

            if (IsMainCheckpoint) return;

            // `size` is the width and `wid` is the height (CheckPoint.as:48-49) — the oracle's names,
            // which read backwards. GetDoorTileDimensions maps them to (widthTiles, heightTiles) in
            // that order for every box, checkpoint included.
            GetDoorTileDimensions(out int widthTiles, out int heightTiles);

            CheckpointRules.CheckpointAreaBox box = CheckpointRules.ResolveAreaBox(
                widthTiles, heightTiles, WorldConstants.TILE_SIZE / 100f);

            if (box.halfWidth <= 0f || box.height <= 0f) return;

            var areaGo = new GameObject("__CheckpointArea");
            areaGo.transform.SetParent(transform, false);

            // AS3 `Y1 = Y - scY`, `Y2 = Y`: the box is bottom-anchored on the checkpoint, so its centre
            // is half its height up. X is already centred — `X1 = X - scX/2`.
            areaGo.transform.localPosition = new Vector3(0f, box.centreYOffset, 0f);

            var collider = areaGo.AddComponent<BoxCollider2D>();
            collider.isTrigger = true;
            collider.size = new Vector2(box.halfWidth * 2f, box.height);

            _checkpointArea = areaGo.AddComponent<CheckpointAreaTrigger>();
            _checkpointArea.Initialize(() => ActivateCheckpoint(CheckpointEntry.Area));
        }

        public void SetOpen(bool open)
        {
            if (_objectInstance == null) return;
            if (!IsInteractableDoor && open) return;

            // A wrecked door has no state left to toggle. Without this, closing one would re-stamp the
            // solid tiles the player had already shot out (ApplyTileCollision(false) writes Wall) and
            // hand back a barricade that renders as rubble.
            if (IsDestroyed) return;

            _objectInstance.runtimeState.isOpen = open;

            if (!open)
            {
                EjectOverlappingUnits();
            }

            ApplyTileCollision(open);
            RefreshSolidCollider();

            // AS3 `Box.setDoor()` force-relights the room when the door is *opened*, and only then:
            // `if(param1) { loc.isRelight = true; loc.isRebuild = true; }` (Box.as:688-692, where
            // param1 is the new open state). The room's per-frame gate (Location.as:3398) then runs
            // the full light pass on the next frame regardless of camera motion, so opening a door
            // immediately reveals what is behind it — including the hole a closed grate's low `opac`
            // already lets through.
            //
            // Closing deliberately does *not* request a relight, matching the oracle: light that
            // should now be blocked is picked up by the next camera/player movement instead. Init
            // does not either — AS3 `initDoor` stamps `opac` (Box.as:667) without touching the flag,
            // and Initialize() calls ApplyTileCollision directly rather than through here.
            if (open && _room != null)
            {
                _room.RequestRelight();
            }

            // The chain mirror is built when the room activates and refreshed ONLY from
            // `RoomInstance.TilesMutated` — see PhysicsWorldService.OnRoomActivated / RebuildRegion.
            // `ApplyTileCollision` rewrote the grid above without saying so, so until this call the
            // mirror still described the door's OPPOSITE state: an opened door kept a wall (rounds
            // stopped at a phantom surface in the doorway) and a closed one gained none. This is the
            // staleness recorded against this path in TOPIC_open_work.md; `Die()` has always
            // notified, and SetOpen now does too, in both directions.
            //
            // Both directions, not just open: `setDoor` writes `phis` on every covered cell either way
            // (Box.as:683-685), so the derived geometry is stale either way. Cost is bounded — a door
            // toggle is a player action, and RebuildRegion is a full rebuild, which is the same
            // work `Die()` has always paid.
            if (_room != null)
            {
                _room.NotifyTilesMutated(CoveredTileRegion());
            }

            UpdateVisualFrame();

            if (_triggerSystem != null && _room != null)
            {
                _triggerSystem.OnObjectInteracted(_room, _objectInstance);
            }
        }

        public void ToggleOpen()
        {
            // AS3 only ever calls setDoor() on a box whose definition authors `door=` (Box.as:290-297).
            // Without this guard, `Interact`'s fallback would "open" a Z door — playing an open/close
            // frame animation and reporting success for an object that has no open state at all.
            if (!IsDoorBox || !IsInteractableDoor) return;
            SetOpen(!IsOpen);
        }

        public void GetDoorTileDimensions(out int widthTiles, out int heightTiles)
        {
            widthTiles = 1;
            heightTiles = 2;

            if (_objectInstance?.definition != null)
            {
                if (_objectInstance.definition.size > 0)
                    widthTiles = _objectInstance.definition.size;
                if (_objectInstance.definition.width > 0)
                    heightTiles = _objectInstance.definition.width;
                return;
            }

            if (_objectInstance != null)
            {
                string rawSize = _objectInstance.GetAttribute("size", string.Empty);
                if (int.TryParse(rawSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 0)
                    widthTiles = s;

                string rawWid = _objectInstance.GetAttribute("wid", string.Empty);
                if (int.TryParse(rawWid, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && w > 0)
                    heightTiles = w;

                if (!string.IsNullOrEmpty(rawSize) || !string.IsNullOrEmpty(rawWid))
                    return;
            }

            if (_visual != null && _visual.pixelSize.x > 0 && _visual.pixelSize.y > 0)
            {
                widthTiles = Mathf.Max(1, Mathf.RoundToInt((float)_visual.pixelSize.x / WorldConstants.TILE_SIZE));
                heightTiles = Mathf.Max(1, Mathf.RoundToInt((float)_visual.pixelSize.y / WorldConstants.TILE_SIZE));
            }
        }

        public void GetCoveredTileRange(out int txMin, out int txMax, out int tyMin, out int tyMax)
        {
            GetDoorTileDimensions(out int widthTiles, out int heightTiles);

            float widthPx = widthTiles * WorldConstants.TILE_SIZE;
            float leftPx = (_objectInstance != null ? _objectInstance.position.x : 0f) - widthPx * 0.5f;
            float bottomPx = _objectInstance != null ? _objectInstance.position.y : 0f;

            txMin = Mathf.FloorToInt(leftPx / WorldConstants.TILE_SIZE + 0.01f);
            txMax = txMin + widthTiles - 1;
            tyMin = Mathf.FloorToInt(bottomPx / WorldConstants.TILE_SIZE);
            tyMax = tyMin + heightTiles - 1;
        }

        public void ApplyTileCollision(bool isOpen)
        {
            if (_room?.tiles == null || _objectInstance == null) return;

            GetCoveredTileRange(out int txMin, out int txMax, out int tyMin, out int tyMax);

            TilePhysicsType targetType = isOpen ? TilePhysicsType.Air : TilePhysicsType.Wall;

            // AS3 `setDoor` writes the door's own `phis` onto every covered tile (Box.as:684), and
            // `initDoor` does the same when it stamps (Box.as:666). For 22 of the 24 shipped doors that
            // value is 1, i.e. an ordinary wall — but `grate`/`hgrate` author `phis='2'`
            // (AllData.as:4859-4860), the level-2 solid that a bullet flies through while a crate and a
            // unit still stop on it. Collapsing both to `Wall` is what made a closed grate absorb fire
            // in this port; the bit is the part of `phis` the enum could not carry.
            bool levelTwoSolid = !isOpen && ResolveClosedDoorPhisLevel() == 2;

            // AS3 Box.setDoor() writes `opac` alongside `phis` on every tile the door covers
            // (Box.as:684-685): 0 while open, the door's own `@opac` while closed. The default is 1
            // (Box.as:72), so an unauthored door blocks light completely — only a door that authors
            // `opac` opens a hole in its own shadow. The data does that on 11 doors, 0.1 (grates) to
            // 0.8 (wooden doors) — AllData.as:4859-5039.
            //
            // Without this term the port closed every door to a full Wall, i.e. opacity 1, so a closed
            // grate or window was solid black where AS3 costs a shadow ray only 0.1-0.2 and lets you
            // see through. `physicsType` stays `Wall` either way, so this changes light only — enemy
            // LOS and movement still treat a closed door as solid, which is what Box.as:684 does.
            float doorOcclusion = isOpen ? 0f : ResolveClosedDoorOcclusion();

            for (int x = txMin; x <= txMax; x++)
            {
                for (int y = tyMin; y <= tyMax; y++)
                {
                    TileData tile = _room.GetTileAtCoord(new Vector2Int(x, y));
                    if (tile != null)
                    {
                        tile.physicsType = targetType;
                        tile.levelTwoSolid = levelTwoSolid;
                        tile.doorOcclusion = doorOcclusion;
                    }
                }
            }
        }

        // ── Durability and destruction (AS3 Box.hp / Box.thre / Box.die) ──────────────────────

        /// <summary>
        /// Reads this door's durability off its definition — AS3 <c>Box</c>'s constructor, which takes
        /// <c>hp</c> and <c>thre</c> from the <c>&lt;obj&gt;</c> row (<c>Box.as:176-183</c>) and then
        /// lets a placed <c>indestruct</c> override both to 10000 (<c>:285-289</c>).
        ///
        /// <para><b>Definition only, not the placement.</b> AS3 reads these two off <c>node</c>, which
        /// is <c>AllData.d.obj.(@id == id)[0]</c> (<c>Box.as:155</c>) — the definition row. The
        /// placement XML (<c>param5</c>) is consulted for <c>indestruct</c> and for scripts, never for
        /// <c>hp</c>/<c>thre</c>. The shipped data agrees: every <c>door=</c> row authors them on the
        /// definition (<c>AllData.as:4858-4876</c>) and no placement overrides them.</para>
        /// </summary>
        private void ResolveDurability()
        {
            bool indestructible = false;
            float authoredHitPoints = 0f;
            float authoredThreshold = 0f;

            if (_objectInstance != null)
            {
                // AS3 `xml.@indestruct.length()` (Box.as:285) is a `.length()` guard, so a
                // present-but-empty attribute does not count. GetAttribute answers "" for both.
                indestructible = !string.IsNullOrEmpty(
                    _objectInstance.GetAttribute("indestruct", string.Empty));

                if (_objectInstance.definition != null)
                {
                    authoredHitPoints = _objectInstance.definition.hitPoints;
                    authoredThreshold = _objectInstance.definition.threshold;
                }
            }

            _doorIndestructible = indestructible;
            _doorHitPoints = DoorDestructionMath.ResolveHitPoints(authoredHitPoints, indestructible);
            _doorThreshold = DoorDestructionMath.ResolveThreshold(authoredThreshold, indestructible);
            _durabilityResolved = true;
        }

        /// <summary>
        /// Turns <see cref="_solidCollider"/> on or off to match the object's current state: a closed,
        /// living door box is solid, and so is a living <b>bullet-button</b> in either state; an open
        /// door, a wrecked object, and anything that is neither are not.
        ///
        /// <para><b>The button case is what made shoot-to-press unreachable.</b> The collider used to be
        /// gated on <see cref="IsDoorBox"/> alone, and a <c>knop2</c> is not a door box — it stamps no
        /// tiles and has no open/close state. Without a surface, every round aimed at one passed through
        /// and hit the wall behind it, so the proxy could have been perfect and never consulted.</para>
        ///
        /// <para>Size and offset come from <see cref="GetDoorBounds"/> — the same source
        /// <see cref="ConfigureCollider"/> uses for the targeting trigger — so the surface a weapon
        /// hits and the surface the cursor selects cannot drift apart.</para>
        /// </summary>
        private void RefreshSolidCollider()
        {
            // A bullet-button keeps its surface in both states. AS3's `VirtualUnit` is pushed into
            // `loc.units` and stays there until the box dies — pressing the button does not remove it,
            // which is how a second shot releases it. A door, by contrast, has no surface while open:
            // `setDoor` zeroes `phis` on its cells (`Box.as:684`).
            bool wantsSolid = !IsDestroyed && ((IsDoorBox && !IsOpen) || HasBulletDamageProxy);

            if (!wantsSolid)
            {
                if (_solidCollider != null) _solidCollider.enabled = false;
                return;
            }

            if (_solidCollider == null)
            {
                _solidCollider = gameObject.AddComponent<BoxCollider2D>();
                _solidCollider.isTrigger = false;
                GetDoorBounds(out Vector2 size, out Vector2 offset);
                _solidCollider.size = size;
                _solidCollider.offset = offset;
            }

            _solidCollider.enabled = true;
        }

        /// <summary>
        /// The tile region this door covers, for <see cref="RoomInstance.NotifyTilesMutated"/>.
        /// </summary>
        private RectInt CoveredTileRegion()
        {
            GetCoveredTileRange(out int txMin, out int txMax, out int tyMin, out int tyMax);
            return new RectInt(txMin, tyMin, txMax - txMin + 1, tyMax - tyMin + 1);
        }

        /// <summary>
        /// Clears every tile the door covers — the port of the <c>if(this.door &gt; 0)</c> block at the
        /// head of AS3 <c>Box.die()</c> (<c>Box.as:802-810</c>), which zeroes <c>opac</c>, <c>phis</c>
        /// and <c>hp</c> on all of them before switching the sprite to its <c>die</c> state.
        ///
        /// <para><c>ApplyTileCollision(true)</c> is exactly the first two: it writes
        /// <c>physicsType = Air</c> and <c>doorOcclusion = 0</c>. Only <c>hp</c> is left, and it is set
        /// here. Reusing the open path rather than repeating the loop is what keeps "an open door" and
        /// "a destroyed door" from disagreeing about the tiles.</para>
        /// </summary>
        private void ApplyDestroyedTiles()
        {
            if (_room?.tiles == null || _objectInstance == null) return;

            ApplyTileCollision(true);

            GetCoveredTileRange(out int txMin, out int txMax, out int tyMin, out int tyMax);
            for (int x = txMin; x <= txMax; x++)
            {
                for (int y = tyMin; y <= tyMax; y++)
                {
                    TileData tile = _room.GetTileAtCoord(new Vector2Int(x, y));
                    if (tile != null) tile.hitPoints = 0;
                }
            }
        }

        /// <summary>
        /// Applies a weapon's destroy value to the door — the port of <c>Tile.udar()</c> as
        /// <c>Location.hitTile()</c> calls it, including the <c>hp &lt;= 0 -&gt; door.die()</c> arm
        /// (<c>Location.as:2523-2537</c>).
        ///
        /// <para><b>Gated on the door's own state, matching the oracle's tile gate.</b> <c>hitTile</c>
        /// guards every destruction branch on <c>phis &gt;= 1</c>, and an open door's tiles are
        /// <c>phis == 0</c> (<c>Box.setDoor</c>, <c>Box.as:684</c>), so an open door cannot be shot —
        /// it is scenery. The same three facts are checked here: the object is a door box, it is not
        /// open, and it is not already wrecked.</para>
        ///
        /// <para><b><paramref name="damageType"/> is carried only to the die sound.</b> AS3's
        /// <c>Box.die(param1)</c> takes the killing hit's damage type and uses it for one thing: the
        /// debris-and-sample block is wrapped in <c>if(param1 &gt;= 0 &amp;&amp; param1 &lt; 10)</c>
        /// (<c>Box.as:848</c>). Nothing else in the destruction depends on it — <c>Tile.udar</c> has no
        /// damage-type term at all.</para>
        /// </summary>
        private void DamageDoor(float destroyAmount, DamageType damageType)
        {
            if (!_durabilityResolved) ResolveDurability();
            if (!IsDoorBox || IsOpen || IsDestroyed) return;

            // AS3 `Tile.udar()`'s own head: `if (indestruct || thre > param1) return false`. Both
            // terms are kept, in the oracle's order, rather than relying on the 10000 sentinel alone.
            if (_doorIndestructible)
            {
                LogRefusal(destroyAmount, "the door is authored indestructible");
                return;
            }

            if (DoorDestructionMath.IgnoresDamage(_doorThreshold, destroyAmount))
            {
                LogRefusal(destroyAmount,
                    $"thre {_doorThreshold} > {destroyAmount} — the hit is below this door's threshold");
                return;
            }

            _doorHitPoints = DoorDestructionMath.ApplyDamage(_doorHitPoints, destroyAmount);
            if (DoorDestructionMath.IsDestroyed(_doorHitPoints))
            {
                Die(damageType);
            }
        }

        /// <summary>
        /// Says why a hit on this door did nothing, when the owner has asked for that.
        ///
        /// <para><b>A door is the worst case for this confusion, because the shipped data gives wooden
        /// doors two different thresholds.</b> <c>door1</c>/<c>hatch1</c> carry <c>thre='10'</c> and
        /// <c>door1a</c> carries <c>thre='50'</c> (<c>AllData.as:4864-4865</c>), so a minigun carrying
        /// <c>destroy = 10</c> breaks the first in six rounds and can never scratch the second. Two
        /// doors that look identical therefore behave differently, and nothing on screen says why.</para>
        /// </summary>
        private void LogRefusal(float destroyAmount, string reason)
        {
            if (!LogDamageRefusals) return;

            string id = _objectInstance?.definition != null ? _objectInstance.definition.ID : "(no definition)";
            Debug.Log($"[TileDamage] door '{id}' REFUSED {destroyAmount:0} damage: {reason}. " +
                      "AS3 Tile.udar drops anything under `thre` outright — no chip damage.");
        }

        /// <summary>
        /// <see cref="PfeDebugSettings.LogTileDamageRefusals"/>, resolved once and cached.
        ///
        /// <para>This class has no injected settings — it reaches the asset the same way
        /// <see cref="ShowDebugVisuals"/> does — so the lookup is cached here rather than repeated on
        /// every refused hit, which is the one path that can fire dozens of times in a burst.</para>
        /// </summary>
        private static bool LogDamageRefusals
        {
            get
            {
                if (_damageLogSettings == null)
                {
                    _damageLogSettings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
                }

                return _damageLogSettings != null && _damageLogSettings.LogTileDamageRefusals;
            }
        }

        private static PfeDebugSettings _damageLogSettings;

        /// <summary>
        /// Wrecks the door — AS3 <c>Box.die()</c> (<c>Box.as:789-880</c>). Sets the flag the renderer
        /// reads for its <c>die</c> frame, clears the covered tiles, drops the solid surface, and tells
        /// the room the grid changed so the derived geometry and the light pass follow.
        ///
        /// <para><b>The notification is not optional.</b> The chain mirror the projectile sweep reads is
        /// built when the room activates (<c>PhysicsWorldService.OnRoomActivated</c>) and refreshed only
        /// from <c>RoomInstance.TilesMutated</c>. A door that dies without raising it leaves a solid
        /// chain across the doorway — the same staleness already recorded against the open/close path
        /// in <c>TOPIC_open_work.md</c>.</para>
        ///
        /// <para><b>The break is audible, and it was not.</b> <c>Box.die()</c> plays
        /// <c>Snd.ps(this.sndDie, X, Y)</c> (<c>Box.as:850-853</c>) and the shipped data authors it on
        /// every door — <c>door1</c>/<c>door1a</c>/<c>hatch1</c>/the <c>septum</c> barricade are
        /// <c>die='break_wood'</c>, <c>door1b</c>/<c>door2</c>/<c>grate</c>/<c>encldoor</c> are
        /// <c>die='break_metal'</c>. <see cref="MapObjectSoundRule.DieSound"/> has read that attribute
        /// since it was written and had <b>no caller</b>, so a barricade the player spent three kicks on
        /// vanished in silence. The oracle's own gate is
        /// <c>if(param1 &gt;= 0 &amp;&amp; param1 &lt; 10)</c> — an exotic damage type kills without a
        /// sound — and it lives in <see cref="MapObjectSoundRule.PlaysOnDie"/> so a fixture can pin it.
        /// </para>
        /// </summary>
        private void Die(DamageType damageType)
        {
            if (_objectInstance?.runtimeState == null) return;
            if (IsDestroyed) return;

            _objectInstance.runtimeState.isDestroyed = true;

            ApplyDestroyedTiles();
            RefreshSolidCollider();
            UpdateVisualFrame();

            string dieSound = MapObjectSoundRule.DieSound(_objectInstance.definition?.legacyAttributes);
            if (MapObjectSoundRule.PlaysOnDie(dieSound, (int)damageType))
            {
                SoundBus.Current?.Play(dieSound, transform.position);
            }

            if (_room != null)
            {
                // AS3 `Box.as:855-858` — the one shout a broken box makes. 800 px, once, and room
                // bounded; see SecurityRuntime.NotifyBoxDestroyed for why this is not the alarm's 1500.
                SecurityRuntime.NotifyBoxDestroyed(_room, (Vector2)transform.position * 100f);

                _room.NotifyTilesMutated(CoveredTileRegion());
            }
        }

        #region IDestructibleTile

        /// <summary>
        /// A weapon's direct hit. AS3 reaches a door through the tile it stands on
        /// (<c>weapon/Bullet.as:489</c> → <c>Location.hitTile</c> → <c>Tile.udar</c>), and the port
        /// routes the same hit here because the door box owns the surface a round can actually reach —
        /// see <see cref="_solidCollider"/>.
        /// </summary>
        public void ApplyDestruction(Vector3 worldPosition, float destroyAmount, DamageType damageType,
                                     DecalType decal)
        {
            // ── The bullet-button branch, and why it is first ────────────────────────────────────
            //
            // In AS3 the round never reaches the Box at all: the `VirtualUnit` bound to it is in
            // `loc.units` (`Location.as:2007-2009`), so `Bullet.step` hits the proxy and calls
            // `owner.command("dam")` (`VirtualUnit.as:40-48`). The port's equivalent surface is this
            // component's solid collider, and this is the one call the projectile makes on it — so
            // "pressed" and "damaged" are the same entry point, and the press has to come first.
            //
            // `DamageDoor` already refuses a non-door box, so a knop2 was never at risk of being
            // destroyed; what was missing is the press. Returning here also keeps a button from ever
            // reaching the destruction path if a future definition authors both.
            if (PressByDamage(damageType))
            {
                return;
            }

            DamageDoor(destroyAmount, damageType);
        }

        /// <summary>
        /// A blast's hit. Deliberately does not iterate a radius: <c>Projectile.Detonate</c> already
        /// enumerates every collider inside the blast and calls this once per collider, so applying an
        /// area here would multiply the damage by the number of overlaps. Same contract as
        /// <c>TileCollider.ApplyDestructionRadius</c>.
        ///
        /// <para><b>An explosion presses a button too, and that is the oracle's behaviour.</b>
        /// <c>VirtualUnit.damage</c> (<c>VirtualUnit.as:30-38</c>) is the non-bullet twin of
        /// <c>udarBullet</c> and applies the identical <c>tipdam</c> filter — the proxy is a unit, so a
        /// blast damages it like any other unit. The damage type handed here is the weapon's, which is
        /// what the filter compares against.</para>
        /// </summary>
        public void ApplyDestructionRadius(Vector3 worldPosition, float radius,
                                           float destroyAmount, DamageType damageType, DecalType decal)
        {
            if (PressByDamage(damageType))
            {
                return;
            }

            DamageDoor(destroyAmount, damageType);
        }

        #endregion

        /// <summary>
        /// AS3 <c>Box.door_opac</c> for this door — its <c>@opac</c>, defaulting to 1 exactly as
        /// <c>Box.as:72</c> declares it. A door with no definition keeps that default, which is also
        /// the behaviour this port had before the term existed, so a missing definition can only fail
        /// back to the old result rather than to "see through the door".
        /// </summary>
        private float ResolveClosedDoorOcclusion()
        {
            return _objectInstance?.definition != null
                ? _objectInstance.definition.GetDoorOcclusion()
                : 1f;
        }

        /// <summary>
        /// AS3 <c>Box.phis</c> for this door — the solidity level it stamps onto its tiles
        /// (<c>Box.as:666</c>, <c>:684</c>). 1 is an ordinary wall and is the default
        /// (<c>Box.as:52</c>); 2 is the level-2 solid that only <c>grate</c>/<c>hgrate</c> author
        /// (<c>AllData.as:4859-4860</c>), which a bullet and a sight ray pass through.
        ///
        /// <para>A door with no definition falls back to 1 — the oracle's declared default — so a
        /// missing asset can only reproduce the old behaviour, never invent a see-through wall.</para>
        /// </summary>
        private int ResolveClosedDoorPhisLevel()
        {
            return _objectInstance?.definition != null
                ? _objectInstance.definition.GetDoorPhisLevel()
                : 1;
        }

        /// <summary>
        /// Puts the renderer on the frame for the door's current state.
        ///
        /// <para><b>AS3 seeks here; it does not animate.</b> <c>Box.setVisState</c> runs
        /// <c>vis.gotoAndStop("open" | "close" | "die")</c> for every state, and
        /// <c>gotoAndPlay</c> only for <c>comein</c> (<c>Box.as:508-532</c>). So opening or closing
        /// a door is a single frame change.</para>
        ///
        /// <para><b>What was wrong.</b> The open target was resolved as index 2 of any sheet with
        /// 3+ frames, and the renderer then walked 0 → 1 → 2 one frame at a time. Index 2 of a
        /// 3-frame door sheet is the <c>die</c> frame, not <c>open</c> — so opening a door visibly
        /// animated from closed, through open, to <i>destroyed</i>, and closing walked back the
        /// same way. The labels were measured from the source SWF; see
        /// <see cref="MapObjectVisualDefinition"/>'s label accessors for the evidence.</para>
        /// </summary>
        public void UpdateVisualFrame()
        {
            if (_visual == null || !_visual.HasFrames) return;

            // A checkpoint's frames[] is `vis.osn`, not a close/open/die sheet. Its pose was
            // resolved by InitializeCheckpointVisual from `active`/`main`; running
            // ResolveStateFrameIndex here would snap an already-open checkpoint back to frame 1.
            if (!IsCheckpointVisual)
            {
                _currentFrameIndex = ResolveStateFrameIndex();
            }

            _targetFrameIndex = _currentFrameIndex;
            _frameTimer = 0f;
            _comeInFrameIndex = -1;
            UpdateRendererSprite();
        }

        /// <summary>
        /// Re-seeks the sprite when <see cref="ObjectInstance.runtimeState"/>'s <c>isOpen</c> has been
        /// changed by someone other than this presenter.
        ///
        /// <para><b>Why this has to exist.</b> Only three methods used to push state into the sprite —
        /// <see cref="Initialize"/>, <see cref="SetOpen"/> and <see cref="Interact"/> — so every writer
        /// that set <c>isOpen</c> <i>directly</i> changed the world without changing the picture. That is
        /// the script path: <c>AreaTriggerSystem.DispatchObjectCommand</c>'s <c>open</c> / <c>close</c> /
        /// <c>swap</c> verbs write <c>runtimeState.isOpen</c> and re-stamp the tiles
        /// (<c>AreaTriggerSystem.UpdateDoorTileCollision</c>), and nothing told the presenter. The result
        /// was a door whose collision had moved and whose sprite had not: the player walked through a
        /// platform that was still drawn solid, or into a doorway that was still drawn shut.</para>
        ///
        /// <para><b>Polled rather than raised.</b> <c>AreaTriggerSystem.OnObjectStateChanged</c> exists
        /// and has <i>no</i> subscribers, so an event would need a new wiring path through the manager's
        /// lifetime as well as a listener. The room's presenter sync already walks every object each
        /// frame (<c>RoomObjectVisualManager.UpdatePresenter</c>), so one extra
        /// <see cref="ResolveStateFrameIndex"/> per object per frame — two integer comparisons — buys the
        /// same result with no lifecycle to get wrong, and it self-heals for any future writer including
        /// a save load.</para>
        ///
        /// <para>Guarded on <see cref="IsPlayingComeIn"/> because <see cref="UpdateVisualFrame"/> aborts
        /// the <c>comein</c> clip: a Z door mid-entry must not be re-seeked by a poll.</para>
        /// </summary>
        public void SyncVisualToState()
        {
            if (_visual == null || !_visual.HasFrames) return;
            if (IsPlayingComeIn) return;

            // The checkpoint body is a played clip with its own state, not a door sheet. The
            // per-frame layer sync (sorting / active flag) is done by Update() instead, which also
            // runs while this poll is deliberately skipped.
            if (IsCheckpointVisual) return;

            // A sheet that cannot express "open" or "die" has nothing for this poll to keep in sync,
            // and re-seeking it would actively do harm: a Z door's `comein` clip ENDS on its last
            // frame (TickAnimation leaves _currentFrameIndex there), which is a state the label model
            // cannot name — resolving it would snap the finished doorway back to frame 0. AS3 leaves
            // the symbol where gotoAndPlay left it, so this poll must not touch these sheets at all.
            if (_visual.OpenStateFrame < 0 && _visual.DestroyedStateFrame < 0) return;

            if (ResolveStateFrameIndex() == _currentFrameIndex) return;

            UpdateVisualFrame();

            // The solid collider is derived from the same state (`wantsSolid = IsDoorBox && !IsOpen`),
            // so a state change made behind SetOpen's back leaves it describing the old state too.
            RefreshSolidCollider();
        }

        /// <summary>
        /// frames[] index for the door's current state, resolved from the sheet's Flash labels.
        /// Falls back to the closed frame when the sheet does not implement the state — which is
        /// what AS3's swallowed <c>gotoAndStop</c> exception amounts to (<c>Box.as:529-531</c>).
        /// </summary>
        private int ResolveStateFrameIndex()
        {
            if (_visual == null || !_visual.HasFrames) return 0;

            if (IsDestroyed && _visual.GetFrame(_visual.DestroyedStateFrame) != null)
            {
                return _visual.DestroyedStateFrame;
            }

            if (IsOpen && _visual.GetFrame(_visual.OpenStateFrame) != null)
            {
                return _visual.OpenStateFrame;
            }

            return _visual.ClosedStateFrame;
        }

        /// <summary>
        /// Starts the <c>comein</c> clip — the one label AS3 <i>plays</i> rather than seeks to,
        /// fired from <c>Interact.beginAct()</c> (<c>Interact.as:1670-1676</c>).
        ///
        /// <para><b>Known timing gap.</b> AS3 fires <c>beginAct()</c> when the hold is <i>armed</i>
        /// (<c>UnitPlayer.as:1983</c>); this is called when the action has <i>completed</i>, because
        /// the presenter is not told about the arm. The clip still plays from the right frame, just
        /// after the hold instead of during it.</para>
        /// </summary>
        public void BeginComeIn()
        {
            if (_visual == null || !_visual.HasFrames) return;

            int start = _visual.ComeInFrame;
            if (start < 0) return;

            _comeInFrameIndex = start;
            _currentFrameIndex = start;
            _targetFrameIndex = start;
            _frameTimer = 0f;
            UpdateRendererSprite();
        }

        /// <summary>True while the <c>comein</c> clip is running.</summary>
        public bool IsPlayingComeIn => _comeInFrameIndex >= 0;

        /// <summary>
        /// Advances the <c>comein</c> clip. Open/close/die do not animate at all, so this is a
        /// no-op unless <see cref="BeginComeIn"/> started the clip.
        /// </summary>
        public void TickAnimation(float deltaTime)
        {
            if (!IsPlayingComeIn || _visual == null || !_visual.HasFrames) return;

            int lastFrame = _visual.frames.Length - 1;

            _frameTimer += deltaTime;
            while (_frameTimer >= FrameDuration && _comeInFrameIndex < lastFrame)
            {
                _frameTimer -= FrameDuration;
                _comeInFrameIndex++;
                _currentFrameIndex = _comeInFrameIndex;
                UpdateRendererSprite();
            }

            if (_comeInFrameIndex >= lastFrame)
            {
                _comeInFrameIndex = -1; // clip finished
            }
        }

        private void UpdateRendererSprite()
        {
            if (_renderer == null || _visual == null || !_visual.HasFrames) return;
            int clamped = Mathf.Clamp(_currentFrameIndex, 0, _visual.frames.Length - 1);
            _renderer.sprite = _visual.frames[clamped];
        }

        // ═══════════════════════════════════════════════════════════════════════════════
        //  vischeckpoint — the three child MovieClips (fe/loc/CheckPoint.as)
        // ═══════════════════════════════════════════════════════════════════════════════
        //
        // The oracle addresses its art by CHILD and by LABEL, never by a frame index on the
        // parent:
        //
        //   ctor        vis.gotoAndStop(1);  vis.lock.gotoAndStop(int(id.charAt(10)))
        //               — or vis.lock.visible = false for the plain ten-character id   (:61-76)
        //   load        active 1 -> osn.gotoAndStop("reopen"@26)                     (:112-119)
        //               active 2 -> osn.gotoAndStop("open"@45)
        //   main        area = null; active = 2; osn.gotoAndStop("open");
        //               fiol.gotoAndStop(25)                                        (:125-137)
        //   activate    osn.play(); mReturn && teleOn && !used -> fiol.gotoAndPlay(1)
        //               used -> fiol.gotoAndStop(1)                                  (:214-233)
        //   deactivate  osn.gotoAndStop("reopen"); fiol.gotoAndStop(1)               (:277-291)
        //   step        locked && inter.lock == 0 && inter.mine == 0
        //               -> vis.lock.visible = false                                 (:312-316)
        //
        // The port had imported ONE flattened PNG — Flash frame 1, WITH the `lock` child baked
        // in — so every plain checkpoint drew a frozen padlock / mine screen and never animated.
        // `generate_checkpoint_layers.py` rebuilds the three clips on a shared canvas; this block
        // is the display-list and playback half.

        /// <summary>
        /// Creates the <c>vis.lock</c> and <c>vis.fiol</c> renderers as children of this GameObject.
        ///
        /// <para><b>Local position zero is load-bearing.</b> All 75 generated sprites are pinned to
        /// the same 130x229 FullRect canvas with a (0.5, 0) pivot, so their texture origins coincide
        /// with the body's. A Tight mesh would let Unity re-trim each frame to its own alpha bbox —
        /// the glow's frame 1 is entirely empty and frame 25 fills 74x229 — and the layers would
        /// slide apart as the clip played.</para>
        ///
        /// <para>The SWF places the children at depths 1 / 13 / 15. Unity has no display list, so
        /// the equivalent is sortingOrder +0 / +1 / +2 on the parent's sorting layer, kept in sync
        /// by <see cref="SyncCheckpointLayers"/> because the manager rewrites the parent's order
        /// every frame for depth sorting.</para>
        /// </summary>
        private void EnsureCheckpointLayers()
        {
            if (_checkpointLayersBuilt || !IsCheckpointVisual || _renderer == null) return;
            _checkpointLayersBuilt = true;

            _checkpointLockRenderer = CreateCheckpointLayer("__CheckpointLock", 1);
            _checkpointGlowRenderer = CreateCheckpointLayer("__CheckpointGlow", 2);
        }

        private SpriteRenderer CreateCheckpointLayer(string name, int orderOffset)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            SpriteRenderer layer = go.AddComponent<SpriteRenderer>();
            layer.color = Color.white;
            // A lit sprite outside the Global Light 2D renders black. The manager gives every
            // presenter its unlit material; children must inherit it or they draw as silhouettes.
            layer.sharedMaterial = _renderer.sharedMaterial;
            layer.sortingLayerID = _renderer.sortingLayerID;
            layer.sortingOrder = _renderer.sortingOrder + orderOffset;
            layer.enabled = false;
            return layer;
        }

        /// <summary>
        /// Seek all three clips to the pose AS3's constructor would have left them at.
        ///
        /// <para><b>The port's <c>active</c> is derived, not stored.</b> <c>CampaignManager</c> holds
        /// one <c>CurrentCheckpoint</c> record for the land, which is what
        /// <c>Location.as:2065-2067</c> re-finds a rebuilt checkpoint by. A <c>main</c> checkpoint is
        /// forced to <c>active == 2</c> by the constructor (<c>:128</c>) before any player touches it.
        /// <c>active == 1</c> (<c>deactivate()</c>) is not modelled by the port's single record, so a
        /// restored non-current checkpoint reads as fresh — recorded here rather than invented.</para>
        /// </summary>
        private void InitializeCheckpointVisual()
        {
            if (!IsCheckpointVisual) return;

            PFE.Systems.Campaign.CampaignManager campaign =
                PFE.Systems.Campaign.CampaignManager.Current;
            string landId = campaign != null ? campaign.CurrentLandId.CurrentValue : null;
            Vector3Int room = _room != null ? _room.landPosition : Vector3Int.zero;
            string code = _objectInstance != null ? _objectInstance.code : null;

            int active = IsMainCheckpoint ||
                         (campaign != null && campaign.IsCurrentCheckpoint(landId, room, code))
                ? CheckpointRules.Activated
                : CheckpointRules.Fresh;

            _currentFrameIndex = CheckpointVisualRules.ResolveBodyStartFrame(active, IsMainCheckpoint);
            _targetFrameIndex = _currentFrameIndex;
            _checkpointBodyPlaying = false;

            // AS3's constructor only arms the full glow for `main`; every other checkpoint gets it
            // from an activation that offered the return (:219 / :227). A restored `active == 2`
            // checkpoint has a DEAD button (:243) and therefore no glow — OffersReturn is the
            // campaign's record of that re-pointing, not a derivation from active.
            bool offersReturn = IsMainCheckpoint ||
                                (campaign != null && campaign.OffersReturn(landId, room, code));
            _checkpointGlowFrame = offersReturn
                ? CheckpointVisualRules.GlowFullFrame
                : CheckpointVisualRules.GlowHiddenFrame;
            _checkpointGlowPlaying = false;

            _checkpointLockFrame = ResolveCheckpointLockFrame();
            ApplyCheckpointLockSprite();
            ApplyCheckpointGlowSprite();
            UpdateRendererSprite();
        }

        /// <summary>
        /// AS3 <c>int(this.id.charAt(10))</c> (<c>CheckPoint.as:64-68</c>). The port's
        /// <c>objectType</c> is the generic <c>"checkpoint"</c> for all six variants, so the variant
        /// digit lives on <c>definitionId</c> — AS3's <c>id</c> is the AllData row id (<c>:45</c>).
        /// </summary>
        private int ResolveCheckpointLockFrame()
        {
            ObjectInstance obj = _objectInstance;
            if (obj == null) return -1;

            string id = !string.IsNullOrEmpty(obj.definitionId) ? obj.definitionId
                      : !string.IsNullOrEmpty(obj.objectId) ? obj.objectId
                      : obj.definition != null ? obj.definition.objectId : null;

            // `step()` (:312-316) hides the graphic once the lock/mine gate OPENS. An unresolved
            // security profile is NOT an open gate: the authored definition still carries lock /
            // mine, so the padlock must stay until something (a script, a picked lock) clears it.
            bool cleared = obj.runtimeState != null && obj.runtimeState.hasSecurityState &&
                           obj.runtimeState.securityLock == 0 && obj.runtimeState.securityMine == 0;

            return CheckpointVisualRules.ResolveLockFrame(id, cleared);
        }

        /// <summary>
        /// AS3 <c>activate()</c>'s visual tail (<c>CheckPoint.as:222-233</c>): <c>vis.osn.play()</c>
        /// from wherever the clip currently sits, and <c>vis.fiol.gotoAndPlay(1)</c> only when a
        /// return is armed. Called ONLY after the campaign reported a successful activation, so a
        /// refused press or a repeated walk-in cannot restart the clip.
        /// </summary>
        private void PlayCheckpointActivation(bool offersTeleport, bool used)
        {
            if (!IsCheckpointVisual) return;

            _checkpointFrameTimer = 0f;
            _checkpointBodyPlaying = _currentFrameIndex < CheckpointVisualRules.OpenBodyFrame;

            bool playGlow = CheckpointVisualRules.GlowPlays(offersTeleport, used);
            _checkpointGlowFrame = CheckpointVisualRules.GlowHiddenFrame;
            _checkpointGlowPlaying = playGlow;
            ApplyCheckpointGlowSprite();

            // The activation also opens the gate: re-read the padlock so a variant whose lock was
            // picked between construction and activation drops its graphic on the same frame.
            _checkpointLockFrame = ResolveCheckpointLockFrame();
            ApplyCheckpointLockSprite();
        }

        /// <summary>Advances <c>vis.osn</c> and <c>vis.fiol</c> once at the SWF's 24 fps, stopping
        /// on "open"@45 and frame 25 respectively. A door's <c>comein</c> clip is untouched.</summary>
        private void TickCheckpointAnimation(float deltaTime)
        {
            if (!IsCheckpointVisual) return;
            if (!_checkpointBodyPlaying && !_checkpointGlowPlaying) return;

            _checkpointFrameTimer += deltaTime;
            // A long frame (a hitch, a tab restore) must not spin the whole clip in one Update.
            int steps = 0;
            while (_checkpointFrameTimer >= CheckpointVisualRules.FrameDuration && steps < 8)
            {
                _checkpointFrameTimer -= CheckpointVisualRules.FrameDuration;
                steps++;

                if (_checkpointBodyPlaying)
                {
                    _currentFrameIndex++;
                    if (_currentFrameIndex >= CheckpointVisualRules.OpenBodyFrame)
                    {
                        _currentFrameIndex = CheckpointVisualRules.OpenBodyFrame;
                        _checkpointBodyPlaying = false;
                    }
                    _targetFrameIndex = _currentFrameIndex;
                    UpdateRendererSprite();
                }

                if (_checkpointGlowPlaying)
                {
                    _checkpointGlowFrame++;
                    if (_checkpointGlowFrame >= CheckpointVisualRules.GlowFullFrame)
                    {
                        _checkpointGlowFrame = CheckpointVisualRules.GlowFullFrame;
                        _checkpointGlowPlaying = false;
                    }
                    ApplyCheckpointGlowSprite();
                }
            }
        }

        private void ApplyCheckpointLockSprite()
        {
            if (_checkpointLockRenderer == null || _visual == null) return;
            _checkpointLockRenderer.sprite = _checkpointLockFrame >= 0
                ? _visual.GetLockFrame(_checkpointLockFrame)
                : null;
        }

        private void ApplyCheckpointGlowSprite()
        {
            if (_checkpointGlowRenderer == null || _visual == null) return;
            _checkpointGlowRenderer.sprite = _visual.GetGlowFrame(_checkpointGlowFrame);
        }

        /// <summary>
        /// Keeps the children on the parent's sorting layer / material / active state.
        /// <c>RoomObjectVisualManager.UpdatePresenter</c> rewrites the parent's
        /// <c>sortingOrder</c> every frame for depth sorting and disables it when the object is
        /// inactive, so a one-time copy in <see cref="EnsureCheckpointLayers"/> would leave the
        /// padlock and glow hanging in a deactivated room.
        /// </summary>
        private void SyncCheckpointLayers()
        {
            if (!_checkpointLayersBuilt || _renderer == null) return;

            bool parentVisible = _renderer.enabled &&
                                 (_objectInstance == null || _objectInstance.isActive);
            SyncCheckpointLayer(_checkpointLockRenderer, 1, parentVisible);
            SyncCheckpointLayer(_checkpointGlowRenderer, 2, parentVisible);
        }

        private void SyncCheckpointLayer(SpriteRenderer layer, int orderOffset, bool parentVisible)
        {
            if (layer == null) return;
            if (layer.sortingLayerID != _renderer.sortingLayerID)
            {
                layer.sortingLayerID = _renderer.sortingLayerID;
            }

            int wanted = _renderer.sortingOrder + orderOffset;
            if (layer.sortingOrder != wanted) layer.sortingOrder = wanted;

            if (layer.sharedMaterial != _renderer.sharedMaterial)
            {
                layer.sharedMaterial = _renderer.sharedMaterial;
            }

            layer.enabled = parentVisible && layer.sprite != null;
        }

        /// <summary>
        /// Detects any units overlapping the door volume and displaces them horizontally to the
        /// left or right side of the door. Matches original Flash AS3 behavior where closing a door
        /// while inside pushes the character to the side on the ground, never upward into the air.
        /// </summary>
        public void EjectOverlappingUnits()
        {
            var candidates = new HashSet<GameObject>();

            // 1. Explicitly check Player
            var player = GameObject.FindWithTag("Player");
            if (player != null)
            {
                candidates.Add(player);
            }
            else
            {
                var locomotion = FindFirstObjectByType<PFE.Entities.Player.PlayerLocomotionController>();
                if (locomotion != null)
                {
                    candidates.Add(locomotion.gameObject);
                }
            }

            // 2. Physics overlap query for other units
            GetDoorTileDimensions(out int widthTiles, out int heightTiles);
            float widthUnits = widthTiles * WorldConstants.TILE_SIZE * 0.01f;
            float heightUnits = heightTiles * WorldConstants.TILE_SIZE * 0.01f;

            Vector2 localOffsetUnits = _visual != null ? (Vector2)_visual.localOffset * 0.01f : Vector2.zero;
            float doorCenterX = transform.position.x - localOffsetUnits.x;
            float doorBottomY = transform.position.y - localOffsetUnits.y;
            Vector2 boxCenter = new Vector2(doorCenterX, doorBottomY + heightUnits * 0.5f);
            Vector2 boxSize = new Vector2(widthUnits + 0.4f, heightUnits);

            Collider2D[] overlaps = Physics2D.OverlapBoxAll(boxCenter, boxSize, 0f);
            if (overlaps != null)
            {
                for (int i = 0; i < overlaps.Length; i++)
                {
                    var col = overlaps[i];
                    if (col == null || col.isTrigger || col.gameObject == gameObject) continue;
                    if (col.CompareTag("Player") ||
                        col.GetComponent<TilePhysicsController>() != null ||
                        col.GetComponent<IMovementMotor>() != null ||
                        col.GetComponent<UnitController>() != null)
                    {
                        candidates.Add(col.gameObject);
                    }
                }
            }

            foreach (var unit in candidates)
            {
                if (unit != null)
                {
                    EjectUnitIfOverlapping(unit);
                }
            }
        }

        /// <summary>
        /// Eject an individual unit if it overlaps the door's solid volume.
        /// Pushes horizontally to the nearest open side, keeping Y unchanged.
        /// </summary>
        public bool EjectUnitIfOverlapping(GameObject unit)
        {
            if (unit == null) return false;

            GetDoorTileDimensions(out int widthTiles, out int heightTiles);
            float widthUnits = widthTiles * WorldConstants.TILE_SIZE * 0.01f;
            float heightUnits = heightTiles * WorldConstants.TILE_SIZE * 0.01f;

            Vector2 localOffsetUnits = _visual != null ? (Vector2)_visual.localOffset * 0.01f : Vector2.zero;
            float doorCenterX = transform.position.x - localOffsetUnits.x;
            float doorBottomY = transform.position.y - localOffsetUnits.y;
            float doorLeft = doorCenterX - widthUnits * 0.5f;
            float doorRight = doorCenterX + widthUnits * 0.5f;
            float doorTop = doorBottomY + heightUnits;

            Vector3 unitPos = unit.transform.position;
            float halfWidth = 0.15f;
            float unitBottom = unitPos.y;
            float unitTop = unitPos.y + 0.5f;

            var tpc = unit.GetComponent<TilePhysicsController>();
            if (tpc != null)
            {
                halfWidth = tpc.CollisionWidth * 0.5f * 0.01f;
                unitBottom = unitPos.y;
                unitTop = unitPos.y + tpc.CollisionHeight * 0.01f;
            }
            else
            {
                var col = unit.GetComponent<Collider2D>();
                if (col != null && !col.isTrigger)
                {
                    halfWidth = Mathf.Max(0.12f, col.bounds.extents.x);
                    unitBottom = col.bounds.min.y;
                    unitTop = col.bounds.max.y;
                }
            }

            float unitLeft = unitPos.x - halfWidth;
            float unitRight = unitPos.x + halfWidth;

            // Check AABB overlap between unit and solid door footprint
            bool overlapX = unitRight > doorLeft + 0.005f && unitLeft < doorRight - 0.005f;
            bool overlapY = unitTop > doorBottomY && unitBottom < doorTop;

            if (!overlapX || !overlapY)
            {
                return false;
            }

            // Margin outside the door's solid bounds to ensure no immediate re-collision with wall
            const float clearMargin = 0.02f;
            float pushLeftX = doorLeft - halfWidth - clearMargin;
            float pushRightX = doorRight + halfWidth + clearMargin;

            bool prefersLeft = unitPos.x < doorCenterX;
            bool leftBlocked = IsPositionBlockedByWall(pushLeftX, unitPos.y);
            bool rightBlocked = IsPositionBlockedByWall(pushRightX, unitPos.y);

            float targetX;
            if (prefersLeft)
            {
                if (!leftBlocked)
                    targetX = pushLeftX;
                else if (!rightBlocked)
                    targetX = pushRightX;
                else
                    targetX = pushLeftX;
            }
            else
            {
                if (!rightBlocked)
                    targetX = pushRightX;
                else if (!leftBlocked)
                    targetX = pushLeftX;
                else
                    targetX = pushRightX;
            }

            // CRITICAL: Keep Y unchanged so the unit is pushed strictly horizontally to the side,
            // never upward into the ceiling.
            Vector3 newPos = new Vector3(targetX, unitPos.y, unitPos.z);

            if (tpc != null)
            {
                tpc.SetUnityPosition(newPos);
                tpc.TeleportTo(tpc.PixelPosition.x, tpc.PixelPosition.y);
            }
            else
            {
                var movementMotor = unit.GetComponent<IMovementMotor>();
                if (movementMotor != null)
                {
                    movementMotor.SetUnityPosition(newPos);
                    movementMotor.SetDesiredHorizontalSpeed(0f);
                }
                else
                {
                    var rb = unit.GetComponent<Rigidbody2D>();
                    if (rb != null)
                    {
                        rb.position = new Vector2(targetX, unitPos.y);
                        rb.linearVelocity = Vector2.zero;
                    }
                    unit.transform.position = newPos;
                }
            }

            return true;
        }

        private bool IsPositionBlockedByWall(float worldX, float worldY)
        {
            if (_room?.tiles == null) return false;

            float roomOriginPixelX = WorldCoordinates.RoomOriginPixelX(_room.landPosition.x, _room.borderOffset);
            float roomOriginPixelY = WorldCoordinates.RoomOriginPixelY(_room.landPosition.y, _room.borderOffset);

            float localPixelX = (worldX * 100f) - roomOriginPixelX;
            float localPixelY = (worldY * 100f) - roomOriginPixelY;

            int tx = Mathf.FloorToInt(localPixelX / WorldConstants.TILE_SIZE);
            int ty = Mathf.FloorToInt(localPixelY / WorldConstants.TILE_SIZE);

            TileData tile = _room.GetTileAtCoord(new Vector2Int(tx, ty));
            return tile != null && tile.physicsType == TilePhysicsType.Wall;
        }

        /// <summary>
        /// Whether the player may work this door from where it stands.
        ///
        /// <para>AS3 gates the press on <c>loc.celDist &lt;= World.w.actionDist</c>
        /// (<c>UnitPlayer.as:1931</c>) with <c>actionDist = 40000</c> (<c>World.as:264</c>), a
        /// <i>squared</i> distance — so the reach is 200 source pixels. This project keeps source
        /// pixels as world units and scales art at 100 px per unit, so that is 2.0 world units. The
        /// previous 2.5 was 25% too generous and let the player work doors from outside the AS3
        /// zone.</para>
        ///
        /// <para><c>user == null</c> answers false: AS3 always measures against the player, and the
        /// old proximity-flag fallback here existed only to serve the input poll that this component
        /// no longer owns.</para>
        /// </summary>
        public bool CanInteract(GameObject user)
        {
            if (!AdmitsPlayerInteraction(IsInteractableDoor, IsCheckpointObject)) return false;

            if (user == null) return false;

            float dist = Vector2.Distance(transform.position, user.transform.position);
            return dist <= ActionReach;
        }

        /// <summary>
        /// Whether the player may <b>target</b> this object at all — the guard <see cref="CanInteract"/>
        /// applies before it measures distance.
        ///
        /// <para><b>Why the door rule alone is not enough.</b> <see cref="IsInteractableDoor"/> answers
        /// "may the player work this <i>door</i>?" — it is derived from the <c>inter</c> attribute and
        /// <c>interactionMode</c>, and a checkpoint authors neither (the imported <c>checkpoint</c>
        /// definition has an empty <c>interactionMode</c>), so it answers <b>false</b> for one. AS3 never
        /// asks that question of a checkpoint: <c>CheckPoint</c>'s constructor builds its own
        /// <c>Interact</c> with <c>actFun = activate</c> (<c>CheckPoint.as:87</c>) and
        /// <c>active = true; action = 100</c> (<c>:89-90</c>), so it is workable by construction. Gating it
        /// on a door attribute is what made pressing E on a checkpoint do nothing: <see cref="Interact"/>
        /// handled the checkpoint correctly, but this guard refused the target first, so
        /// <see cref="Interact"/> was never reached.</para>
        ///
        /// <para>The two terms are exactly the set <see cref="Interact"/> can act on and nothing more.
        /// Widening this to "has any script" would re-open the terminal/bench regression documented on
        /// <see cref="RoomObjectVisualManager.UsesDoorPresenter"/>.</para>
        /// </summary>
        public static bool AdmitsPlayerInteraction(bool isInteractableDoor, bool isCheckpointObject)
        {
            return isInteractableDoor || isCheckpointObject;
        }

        /// <summary>
        /// Whether this object is a checkpoint — AS3 <c>tip='checkpoint'</c>, which
        /// <c>Location.as:2008</c> turns into a <c>CheckPoint</c> instead of a <c>Box</c>.
        /// </summary>
        private bool IsCheckpointObject =>
            _objectInstance != null &&
            (string.Equals(_objectInstance.objectType, "checkpoint", StringComparison.OrdinalIgnoreCase) ||
             (_objectInstance.definition != null &&
              _objectInstance.definition.family == MapObjectFamily.Checkpoint));

        /// <summary>
        /// Whether this checkpoint is AS3's <c>main</c> one — <c>xml.@main</c>
        /// (<c>CheckPoint.as:95-98</c>), which re-points the button at <c>teleport</c> and clears the
        /// walk-into area before any player touches it (<c>:126-137</c>).
        ///
        /// <para>One read, used by both the press routing and <see cref="ActivateCheckpoint"/>'s
        /// <c>main</c> term, so the two cannot drift apart.</para>
        /// </summary>
        private bool IsMainCheckpoint =>
            _objectInstance != null &&
            !string.IsNullOrEmpty(_objectInstance.GetAttribute("main", string.Empty));

        /// <summary>
        /// AS3's checkpoint press: run whichever function <c>inter.actFun</c> currently names.
        ///
        /// <para><b>The button is not always <c>activate</c>.</b> <c>CheckPoint</c> builds it as
        /// <c>activate</c> (<c>:86</c>), but re-points it at <c>teleport</c> for a <c>main</c> checkpoint
        /// (<c>:132</c>) and again after a successful activation that armed a return (<c>:236</c>). This
        /// method is the port of that one <c>Interact</c> object; <see cref="ResolveCheckpointAction"/>
        /// is what it reads to decide.</para>
        ///
        /// <para><b><see cref="CheckpointAction.None"/> really is "do nothing", including no log.</b>
        /// AS3 sets <c>inter.active = false</c> for a hidden checkpoint and for one whose activation did
        /// not arm a return, and an inactive <c>Interact</c> is never delivered a press. Falling through
        /// to <see cref="ActivateCheckpoint"/> instead would be harmless for the outcome but wrong about
        /// the mechanism, and it would print "already active, nothing to do" for a checkpoint the oracle
        /// has no button on at all.</para>
        /// </summary>
        private void InteractWithCheckpoint()
        {
            PFE.Systems.Campaign.CampaignManager campaign = PFE.Systems.Campaign.CampaignManager.Current;
            if (campaign == null)
            {
                // No campaign: there is no button state to read, and ActivateCheckpoint is the method
                // that reports the missing manager. Keeping that report is the point of this branch.
                ActivateCheckpoint();
                return;
            }

            switch (ResolveCheckpointAction(campaign))
            {
                case CheckpointAction.Teleport:
                    TeleportFromCheckpoint(campaign);
                    break;

                case CheckpointAction.Activate:
                    ActivateCheckpoint();
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Which action this checkpoint's button currently runs — AS3's <c>inter.actFun</c>, read from
        /// the three places that write it.
        ///
        /// <list type="number">
        /// <item><b><c>main</c></b> — <c>teleport</c> from construction (<c>CheckPoint.as:132</c>), but
        /// only while the player is away on a mission (<c>step()</c>, <c>:300-310</c>): in the hub the
        /// button is dead, because teleporting to the hub you are standing in is not a thing.</item>
        /// <item><b>a recorded return offer</b> — <c>teleport</c> with <c>userAction = "returnb"</c>
        /// (<c>:236-238</c>), which is <c>CampaignManager.ReturnCheckpoint</c>. Read from the campaign
        /// rather than from a field here, because the oracle's <c>CheckPoint</c> outlives a room change
        /// (all rooms are built at land load, <c>Land.as:947</c>) while this presenter does not.</item>
        /// <item><b>otherwise</b> — <c>activate</c>. This covers both the untouched checkpoint and the
        /// restored <c>active == 2</c> one; for the second the press is refused at
        /// <c>CheckPoint.as:190</c>, which is the oracle's own behaviour and not a state this method has
        /// to model separately.</item>
        /// </list>
        /// </summary>
        private CheckpointAction ResolveCheckpointAction(PFE.Systems.Campaign.CampaignManager campaign)
        {
            if (IsMainCheckpoint)
            {
                return campaign.MainReturnIsLive() ? CheckpointAction.Teleport : CheckpointAction.None;
            }

            Vector3Int room = _room != null ? _room.landPosition : Vector3Int.zero;
            string code = _objectInstance != null ? _objectInstance.code : null;

            return campaign.OffersReturn(campaign.CurrentLandId.CurrentValue, room, code)
                ? CheckpointAction.Teleport
                : CheckpointAction.Activate;
        }

        /// <summary>
        /// AS3 <c>CheckPoint.teleport()</c> (<c>CheckPoint.as:250-267</c>) — the travel itself lives in
        /// <c>CampaignManager.TeleportToCheckpoint</c>; what stays here is the object's own read of
        /// <c>@main</c>, which is what picks the destination.
        ///
        /// <para>The no-op case is real and silent on purpose: a <c>main</c> checkpoint whose mission is
        /// the hub answers <c>""</c> from <see cref="CheckpointRules.TeleportTarget"/> and the campaign
        /// logs it. Nothing else is required — the offer is dropped by the destination's own land entry,
        /// so a second press cannot repeat a travel that never happened.</para>
        /// </summary>
        private void TeleportFromCheckpoint(PFE.Systems.Campaign.CampaignManager campaign)
        {
            campaign.TeleportToCheckpoint(IsMainCheckpoint);
        }

        /// <summary>
        /// AS3 <c>CheckPoint.activate()</c> (<c>CheckPoint.as:184-248</c>).
        ///
        /// <para>The decision and the runtime state live in <c>CampaignManager.ActivateCheckpoint</c>
        /// (rules in <c>PFE.Sim.Campaign.CheckpointRules</c>); what stays here is the object's own reads
        /// (<c>code</c>, <c>@tele</c>, <c>@main</c>) and the save that ends the oracle's method.</para>
        ///
        /// <para><b>The save is issued only when the activation actually ran.</b> AS3 returns early for an
        /// already-active or locked checkpoint and never reaches <c>saveGame()</c>
        /// (<c>CheckPoint.as:186-190</c>), so saving on every touch would be a write per press.</para>
        ///
        /// <para><b>Both of AS3's entry points land here.</b> The button arrives with
        /// <see cref="CheckpointEntry.Activate"/>; the walk-into area with
        /// <see cref="CheckpointEntry.Area"/>, which the campaign refuses unless the checkpoint is still
        /// fresh (<c>CheckPoint.as:271</c>).</para>
        ///
        /// <para><b>This is one of the two functions the button can name.</b> The other is
        /// <c>teleport</c>, reached through <see cref="InteractWithCheckpoint"/>'s routing; AS3 re-points
        /// the button between the two (<c>CheckPoint.as:236-238</c>), so a checkpoint press does not
        /// always come here. What that re-pointing *writes* — <c>CampaignManager.ReturnCheckpoint</c> —
        /// is set inside the campaign's activation, from the same <c>offersTeleport</c> this method's
        /// return value keys the save off.</para>
        /// </summary>
        /// <param name="entry">Which of AS3's two entry points is running.</param>
        private void ActivateCheckpoint(CheckpointEntry entry = CheckpointEntry.Activate)
        {
            PFE.Systems.Campaign.CampaignManager campaign = PFE.Systems.Campaign.CampaignManager.Current;
            if (campaign == null)
            {
                Debug.LogWarning("[DoorPropPresenter] checkpoint touched but there is no CampaignManager: " +
                                 "no checkpoint state was written and nothing was saved.");
                return;
            }

            ObjectInstance obj = _objectInstance;
            Vector3Int room = _room != null ? _room.landPosition : Vector3Int.zero;

            // `@tele` (CheckPoint.as:97-100) and `@main` (:123-126) come off the object's own XML.
            //
            // For a placement the generator produced, `tele` is NOT authored: it is written by
            // `RoomPopulator.ApplyCheckpointTeleOn`, which ports the oracle's only other writer
            // (Location.as:2040-2046) — the difficulty rule
            // `globalDif <= 1 || (land.rnd && globalDif == 2 && rand < 0.33)`. So this one read is the
            // merge of the authored attribute and the difficulty override, which is exactly what the
            // oracle's constructor-plus-statement pair produces.
            bool teleOn = !string.IsNullOrEmpty(obj.GetAttribute("tele", string.Empty));
            bool main = IsMainCheckpoint;

            // createCheck's `param1`. AS3 keeps it in a local; the port writes it onto the placement
            // because the decision is made here rather than in the placement.
            bool isBegin = !string.IsNullOrEmpty(
                obj.GetAttribute(RoomPopulator.BeginCheckpointAttribute, string.Empty));

            // AS3 `inter.lock > 0 || inter.mine > 0` (CheckPoint.as:186-189). Both are read off the
            // DEFINITION row, which is the row `Interact` reads (`param2`, Interact.as:218-252) — a
            // placed checkpoint is built with no XML of its own (Location.createCheck passes no fourth
            // argument), so there is no placement-level override to look for.
            bool locked = CheckpointRules.IsLocked(
                ReadDefinitionAttribute(obj, "lock"),
                ReadDefinitionAttribute(obj, "mine"));

            string landId = campaign.CurrentLandId.CurrentValue;
            if (!campaign.ActivateCheckpoint(landId, room, obj.code,
                    isBegin, teleOn, main, locked, entry))
            {
                return;
            }

            // AS3 :224-229 — `vis.osn.play()` and, when the activation armed a return,
            // `vis.fiol.gotoAndPlay(1)`. `OffersReturn` is the campaign's own record of that
            // `inter.actFun = teleport` re-pointing, so the picture cannot disagree with the button.
            // `used` is AS3's hardcore one-shot teleport (:251-256), which the port does not model;
            // it is read off the placement if a future writer adds one, rather than invented here.
            bool used = !string.IsNullOrEmpty(obj.GetAttribute("used", string.Empty));
            PlayCheckpointActivation(campaign.OffersReturn(landId, room, obj.code), used);

            // AS3 CheckPoint.as:247 — `World.w.saveGame()`, the last line of activate().
            SaveManager.Instance?.RequestCheckpointSave();
        }

        /// <summary>
        /// An attribute off the object's <b>definition</b> row rather than the placement.
        ///
        /// <para><see cref="ObjectInstance.GetAttribute"/> reads the placement's own attributes; the
        /// definition is a separate object with its own <c>GetAttribute</c>. <c>lock</c> and <c>mine</c>
        /// live on the definition (<c>AllData.as:5008-5012</c>) and AS3 reads them from there, so the
        /// placement-first order that <c>GetHoldFrames</c>/<c>GetAllAct</c> use does not apply.</para>
        /// </summary>
        private static string ReadDefinitionAttribute(ObjectInstance obj, string key)
        {
            return obj?.definition != null ? obj.definition.GetAttribute(key, string.Empty) : string.Empty;
        }

        /// <summary>
        /// Toggles a <c>knop</c>'s own open state and re-seeks its sprite — the port of the
        /// <c>door</c>/<c>knop</c> branch at the head of AS3 <c>Interact.actOsn</c>
        /// (<c>Interact.as:1291-1297</c>).
        ///
        /// <para><b>This is a lamp, not a mechanism.</b> The button's own state and the thing it
        /// operates are separate in the oracle: <c>rooms_begin/room_4_1</c>'s knop is
        /// <c>&lt;scr&gt;&lt;s act="swap" targ="trPl"/&gt;…</c>, so the press lights the button
        /// <i>and</i> swaps the platform. Toggling the button does not touch the platform, and the
        /// platform's state does not touch the button — which is why this must not be folded into
        /// <see cref="SetOpen"/>.</para>
        ///
        /// <para><b>The two gates are AS3's.</b> <c>knop &gt; 0</c> — the attribute lives on the
        /// <i>definition</i> row (<c>AllData.as:4907</c>, <c>knop='1'</c>), and a terminal does not
        /// author it (<c>AllData.as:4899</c>). <c>action &gt; 0</c> — <c>action</c> is the definition's
        /// <c>@inter</c>, so an object placed with <c>inter='0'</c> has nothing to press. Both are
        /// required; a knop that failed either would stay dark in AS3 too.</para>
        /// </summary>
        private void ToggleAuthoredKnopState()
        {
            if (_objectInstance?.definition == null) return;

            string knop = ReadDefinitionAttribute(_objectInstance, "knop");
            if (string.IsNullOrEmpty(knop) || knop == "0") return;

            string action = ReadDefinitionAttribute(_objectInstance, "inter");
            if (!int.TryParse(action, NumberStyles.Integer, CultureInfo.InvariantCulture, out int actionValue) ||
                actionValue <= 0)
            {
                return;
            }

            _objectInstance.runtimeState.isOpen = !_objectInstance.runtimeState.isOpen;

            // Seeks the lamp sheet's `open` / `close` label. A one-frame terminal sheet resolves both
            // to frame 0, so this is a no-op for them even if a future definition authorises it —
            // which is what AS3's swallowed gotoAndStop amounts to.
            UpdateVisualFrame();
        }

        public void Interact(GameObject user)
        {
            // A checkpoint is not an `allact` in AS3 — CheckPoint builds its own Interact with
            // `actFun = activate` (CheckPoint.as:86-90) — so it is handled by family here, before the
            // dispatcher, and never falls through to ToggleOpen().
            if (IsCheckpointObject)
            {
                if (Time.frameCount == _lastInteractFrame) return;
                _lastInteractFrame = Time.frameCount;
                InteractWithCheckpoint();
                return;
            }

            if (!IsInteractableDoor) return;

            if (Time.frameCount == _lastInteractFrame) return;
            _lastInteractFrame = Time.frameCount;

            // ── The lock, before anything else ───────────────────────────────────────────────────
            //
            // AS3's `Interact.act()` resolves the mine first, then the lock, and only reaches
            // `actOsn()` — which is what opens a door, loots a container or runs an `allact` — when
            // there is nothing left in the way (`:889-1244`, `:1265-1328`). Placing this gate after
            // the dispatcher instead would let a locked terminal run its `hack_robot` sweep on a
            // failed hack, which is the whole feature inverted.
            SecurityGateOutcome security = SecurityGate.TryWork(user, _objectInstance, _room);
            if (security == SecurityGateOutcome.Handled)
            {
                return;
            }

            // `Opened` (a picked lock, a key, or a successful mine defusal) falls through to the
            // object's own action — AS3's `successUnlock`/`actOsn` chain. `NotLocked` does the same,
            // which is why the two are not separated here.

            RunActOsn(user);
        }

        /// <summary>
        /// AS3 <c>Interact.actOsn()</c> (<c>Interact.as:1265-1327</c>) — everything a press does once the
        /// mine/lock gate has let it through.
        ///
        /// <para><b>Extracted because a bullet press is the same function.</b> AS3 reaches this body from
        /// two places: <c>Interact.act()</c> after the lock, and <c>Interact.command("dam")</c>'s
        /// <c>else</c> arm — which is what a <c>VirtualUnit</c> calls when a round hits it
        /// (<c>Interact.as:1744-1758</c>). Keeping one body is the point: a second copy for the bullet
        /// would drift the first time either side changed, and the divergence would present as "the
        /// button behaves differently when shot".</para>
        ///
        /// <para><b>What is deliberately <i>not</i> here.</b> The checkpoint branch, the
        /// <c>IsInteractableDoor</c> test, the per-frame dedupe and the lock are all input-path gates.
        /// AS3's <c>command("dam")</c> consults none of them, so a locked door is still shootable and a
        /// button shot twice in one frame presses twice. <paramref name="user"/> is null for a bullet,
        /// which the dispatcher tolerates — a script that needs a user reports it.</para>
        /// </summary>
        private void RunActOsn(GameObject user)
        {
            // ── The object's own state, before its script ────────────────────────────────────────
            //
            // AS3 `Interact.actOsn` (Interact.as:1291-1297) toggles the object's own `open` before it
            // runs anything else:
            //
            //     if((this.door > 0 || this.knop > 0) && this.action > 0) {
            //         this.open = !this.open;
            //         this.setAct("open", this.open ? 1 : 0);     // → setVisState("open"|"close")
            //     }
            //     if(Boolean(this.allact) || this.prob != null) { this.allAct(); }
            //     ...
            //     if(this.scrAct) { this.scrAct.start(); }
            //
            // For a `knop`, `setAct("open")` seeks its sheet to the `open` label — the lamp. So a
            // button's press is *visible* in AS3: released lamp → lit lamp → released lamp. The port
            // had no equivalent, so every button looked permanently unpressed no matter how often it
            // was worked; that is the second half of "it always pressed".
            //
            // Doors are excluded on purpose: ToggleOpen → SetOpen already does this (plus the tile
            // stamp, the eject and the relight), and toggling here as well would flip it twice.
            if (!IsDoorBox)
            {
                ToggleAuthoredKnopState();
            }

            // AS3 runs an object's `allact` script from Interact.act() (Interact.as:889). This method
            // is the "the action fired" entry point, not "the button went down": a door that authors
            // a `time` is held first (see HoldFrames) and reaches here only when the hold completes,
            // while a door that authors none reaches here on the press. That is the same split AS3
            // makes, where the zero-time branch fires is_act directly (UnitPlayer.as:1985-1988) and
            // every other branch arms t_action first.
            var context = new ObjectActionContext(_room, _objectInstance, user, transform.position);
            ObjectActionOutcome outcome = ObjectActions.Dispatch(in context);

            // AS3 `if(this.loc.prob) this.loc.prob.check()` (Interact.as:1319-1321) — after `allAct`
            // and before `scrAct`, and <b>not</b> conditional on the script having done anything. For an
            // object with no `allact` this is the only thing that advances a prob room, and the seven
            // `buttons3` buttons are exactly that shape (a `tipdam="4"` filter, no script, no allact):
            // without this call a shot button lights up and the passage stays shut.
            PFE.Systems.Map.Scripting.AreaTriggerSystem.CheckProbRoomFor(_room);

            // Handled AND Refused both stop here. AS3 refuses a `comein` by returning null from
            // Land.gotoLoc and nothing happens; falling through to ToggleOpen() would instead open a
            // Z door onto a layer that does not exist. Only "no script" and "script not ported yet"
            // reach the fallback, which is what keeps the term* terminals behaving as they do today.
            if (outcome == ObjectActionOutcome.Handled || outcome == ObjectActionOutcome.Refused)
            {
                // AS3's beginAct() plays the `comein` clip for a Z door (Interact.as:1670-1676) —
                // the only state that animates. See BeginComeIn for the trigger-timing gap.
                if (outcome == ObjectActionOutcome.Handled &&
                    string.Equals(_objectInstance?.GetAllAct(), ComeInAction.Id, StringComparison.OrdinalIgnoreCase))
                {
                    BeginComeIn();
                }

                return;
            }

            // AS3 `Interact.actOsn` ends with `if(this.scrAct) this.scrAct.start()`
            // (Interact.as:1323-1325): an object's own inline `<scr>` runs on the same press that works
            // it, for a door and a `knop` alike. A button carries no `allact`, so the dispatcher above
            // answered NoScript and the old code fell straight to ToggleOpen() — which refuses anything
            // that is not a door box (`IsDoorBox`), so the press did nothing at all even once the object
            // had a presenter. This is the second half of the "buttons do nothing" fix; the first is the
            // admission clause in RoomObjectVisualManager.UsesDoorPresenter.
            //
            // Not for a door box: ToggleOpen → SetOpen already raises OnObjectInteracted, so running the
            // script here as well would execute it twice for a door that authors `<scr>` and no `allact`.
            if (!IsDoorBox && RoomObjectVisualManager.HasInteractScript(_objectInstance))
            {
                _triggerSystem?.OnObjectInteracted(_room, _objectInstance);
                return;
            }

            ToggleOpen();
        }

        // Interaction entry points removed on purpose — this component must not read input.
        //
        // It used to own three more paths besides Interact(): an E/mouse poll in Update() whose
        // second branch fired on `_isPlayerNear || dist <= 1.8f` "even without pointing cursor",
        // an OnMouseDown() that toggled instantly, and an OnInteractPressed() with a third copy of
        // the cursor raycast. Together they meant a door responded to E from anywhere in the room
        // rather than from under the cursor, and a click skipped the hold that `time=10` doors
        // require. AS3 has exactly one entry point — UnitPlayer.actAction(), which resolves the cell
        // under the cursor and arms t_action (UnitPlayer.as:1922-2001) — so ownership lives there
        // now: PlayerController.ResolveInteractTarget() picks the target and
        // PlayerActionInteractor runs the hold. This class is a presenter plus an IInteractable.
        //
        // Unity's own OnMouseDown/OnMouseUp are deliberately not used either: they fire on the
        // collider under the pointer with no reach check and no hold, which is the same defect.

        private void Update()
        {
            TickAnimation(Time.deltaTime);
            TickCheckpointAnimation(Time.deltaTime);
            SyncCheckpointLayers();

            // F9 toggles the door-collider debug overlay. This is the only key this component
            // reads, and it is editor/debug scaffolding rather than gameplay.
            bool f9Pressed = Input.GetKeyDown(KeyCode.F9) ||
                (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.f9Key.wasPressedThisFrame);
            if (f9Pressed)
            {
                ShowDebugVisuals = !ShowDebugVisuals;
                Debug.Log($"[DoorPropPresenter] Door Collider debug overlay: {(ShowDebugVisuals ? "ENABLED (Yellow)" : "DISABLED")}");
            }
        }

        public void UpdateDebugVisual()
        {
            bool shouldShow = ShowDebugVisuals && (_objectInstance == null || _objectInstance.isActive);

            if (!shouldShow)
            {
                if (_debugVisualGo != null)
                {
                    _debugVisualGo.SetActive(false);
                }
                return;
            }

            EnsureDebugVisual();
            if (_debugVisualGo != null)
            {
                _debugVisualGo.SetActive(true);
            }
        }

        private void EnsureDebugVisual()
        {
            if (_debugVisualGo != null) return;

            _debugVisualGo = new GameObject("__DoorDebugVisual");
            _debugVisualGo.transform.SetParent(transform, false);

            Vector2 size;
            Vector2 offset;

            if (IsInteractableDoor && _triggerCollider != null)
            {
                size = _triggerCollider.size;
                offset = _triggerCollider.offset;
            }
            else
            {
                GetDoorBounds(out size, out offset);
            }

            _debugVisualGo.transform.localPosition = new Vector3(offset.x, offset.y, 0f);
            _debugVisualGo.transform.localScale = new Vector3(size.x, size.y, 1f);

            _debugSpriteRenderer = _debugVisualGo.AddComponent<SpriteRenderer>();
            _debugSpriteRenderer.sprite = GetWhiteDebugSprite();
            _debugSpriteRenderer.color = IsInteractableDoor ? DoorTriggerFillColor : BarricadeFillColor;
            _debugSpriteRenderer.sortingLayerName = MapSortingLayers.Foreground;
            _debugSpriteRenderer.sortingOrder = 998;
        }

        private static Sprite GetWhiteDebugSprite()
        {
            if (_whiteDebugSprite == null)
            {
                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                _whiteDebugSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            }
            return _whiteDebugSprite;
        }

        private void OnDrawGizmos()
        {
            if (!ShowDebugVisuals) return;

            Vector2 size;
            Vector2 offset;

            if (IsInteractableDoor && _triggerCollider != null)
            {
                size = _triggerCollider.size;
                offset = _triggerCollider.offset;
            }
            else
            {
                GetDoorBounds(out size, out offset);
            }

            Vector3 center = transform.position + new Vector3(offset.x, offset.y, 0f);
            Vector3 cubeSize = new Vector3(size.x, size.y, 0.05f);

            Color fill = IsInteractableDoor ? DoorTriggerFillColor : BarricadeFillColor;
            Color wire = IsInteractableDoor ? DoorTriggerWireColor : BarricadeWireColor;

            Gizmos.color = fill;
            Gizmos.DrawCube(center, cubeSize);

            Gizmos.color = wire;
            Gizmos.DrawWireCube(center, cubeSize);

#if UNITY_EDITOR
            string id = _objectInstance?.objectId ?? _visual?.objectId ?? "door";
            string status = IsInteractableDoor ? (IsOpen ? "Open" : "Closed") : "Solid Barrier";
            UnityEditor.Handles.Label(center, $"[{id}] ({status})");
#endif
        }
    }
}
