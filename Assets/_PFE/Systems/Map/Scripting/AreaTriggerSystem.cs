using System;
using System.Collections.Generic;
using System.Globalization;
using MessagePipe;
using PFE.Core.Messages;
using PFE.Systems.Map.Actions;
using PFE.Systems.Map.Generation;
using PFE.Systems.Map.Streaming;
using PFE.Systems.Security;
using UnityEngine;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// Evaluates area trigger overlaps, dispatches tutorial messages, and executes map object scripts.
    /// Bridges ActionScript 3 Area.as and Script.as functionality into Unity.
    /// </summary>
    public sealed class AreaTriggerSystem : IScriptObjectHost, IUnitCommandHost, ILandScriptHost
    {
        private readonly IPublisher<TutorialPromptMessage> _promptPublisher;
        private readonly IPublisher<ObjectiveMarkerMessage> _markerPublisher;
        private readonly IPublisher<LandTransitionMessage> _landTransitionPublisher;
        private readonly LuaTriggerBridge _luaBridge;
        private readonly ILandScriptHost _landHost;

        /// <summary>
        /// The live-unit half of the <c>@targ</c> table, pushed in by <c>RoomVisualController</c>.
        ///
        /// <para><b>Why a pushed field and not a constructor argument.</b> This type is built
        /// <i>before</i> the room's unit spawner (the trigger layer is deliberately created ahead of the
        /// render-asset guards — see <c>RoomVisualController.Initialize</c>), so at construction there is
        /// nothing to hand it. The controller owns both and is therefore the one place that can pair
        /// them, which is the same shape as <c>RegisterObjectHost</c>.</para>
        ///
        /// <para><b>Null is a legal state and it is the pre-change behaviour.</b> An offline fixture that
        /// constructs this type directly has no spawner, and a <c>@targ</c> naming a unit must then
        /// resolve to nothing — reported as an unmatched uid — rather than throw. That is exactly what
        /// happened before <see cref="IUnitCommandHost"/> existed, so no fixture changes meaning.</para>
        /// </summary>
        private IUnitCommandHost _unitHost;

        /// <summary>
        /// The room-scoped particle emitter, pushed by <c>RoomVisualController</c> — the same push, for
        /// the same reason, as <see cref="_unitHost"/>: this type is built with <c>new</c> before the
        /// adapters arrive.
        ///
        /// <para>It exists for exactly one effect: AS3 <c>Interact.shine()</c>, the ten-particle
        /// "unlock" sparkle that <c>command("unlock")</c> and the <c>hack</c> verb both end with
        /// (<c>Interact.as:1678-1686</c>). Null is legal — an offline fixture has no particle world —
        /// and <see cref="ObjectShineEmitter"/> then reports once by name instead of throwing.</para>
        /// </summary>
        private PFE.Systems.Particles.Adapters.RoomParticleEmitter _particles;

        /// <summary>
        /// The stepwise interpreter, created on first use from <see cref="ScriptSystem.Current"/> and
        /// registered there so it is stepped at <see cref="PFE.Core.SimTickOrder.Scripts"/>.
        ///
        /// <para><b>Why per room and lazily.</b> This type is constructed with <c>new
        /// AreaTriggerSystem()</c> by <c>RoomVisualController.Initialize</c>, so a runner created in the
        /// constructor would be built before the container's <c>ScriptSystem</c> is guaranteed to exist —
        /// the same ordering that made <see cref="LandHost"/> a per-call read. Creating it on the first
        /// script keeps the construction path untouched and means a room that never fires a script never
        /// allocates one.</para>
        /// </summary>
        private ScriptRunner _runner;

        private bool _disposed;

        /// <summary>
        /// Triggers already consumed by a one-shot area — AS3 <c>World.w.game.triggers[uid]</c>
        /// (<c>Area.as:354-366</c>).
        ///
        /// <para><b>Only consulted when the area carries <c>@trig</c> and a <c>uid</c>.</b> The oracle's
        /// guard is <c>if(this.trig &amp;&amp; Boolean(uid))</c> — an area without a uid has nothing to
        /// key the flag on, so it fires every time it is entered, which is what the vast majority of
        /// areas do. Treating every area as one-shot would silently break the ones that are meant to
        /// re-fire (a tutorial prompt on a ledge the player can fall off and climb again).</para>
        /// </summary>
        private readonly HashSet<string> _firedOneShotTriggers = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// The campaign-level vocabulary (<c>upland</c>, <c>openland</c>, <c>refill</c>, <c>passed</c>,
        /// <c>trigger</c>, <c>gotoland</c>), resolved at call time.
        ///
        /// <para><b>Why this exists, and why the fallback is not laziness.</b> The only production
        /// construction site is <c>RoomVisualController.Initialize</c>
        /// (<c>RoomVisualController.cs:527</c>), which calls the parameterless constructor — so
        /// <c>_landHost</c> was <c>null</c> in every real scene and <b>every</b> campaign-level action
        /// was a silent no-op. The one that mattered was <c>upland</c>: <c>exit_plant</c>'s area script
        /// (<c>&lt;s act="refill"/&gt;&lt;s act="upland"/&gt;</c>) is the only thing that raises
        /// <c>landStage</c> (<c>Script.as:396-399</c> → <c>Game.upLandLevel</c>, <c>Game.as:474-481</c>),
        /// and <c>landStage</c> is what decides the plant's grid (<c>Land.as:180-183</c> clamps
        /// <c>maxLocY = 3</c> while <c>landStage &lt;= 0</c>) and how deep a room may be drawn
        /// (<c>Land.newRandomLoc(param1 = landStage)</c>, <c>Land.as:889-940</c>). With a null host the
        /// land rebuilt for ever at stage 0: always 4x3, always the shallowest rooms.</para>
        ///
        /// <para><b>Resolved per call, not per construction.</b> <c>RoomVisualController.Initialize</c>
        /// runs while the world is being built, which is earlier than the campaign is guaranteed to
        /// exist. Reading the accessor at call time is the pattern
        /// <c>DoorPropPresenter.CreateSceneDispatcher</c> already uses for the same reason, and it is
        /// why a test that injects a fake host is unaffected: an injected host always wins.</para>
        /// </summary>
        private ILandScriptHost LandHost =>
            _landHost ?? PFE.Systems.Campaign.CampaignManager.Current;

        private readonly Dictionary<string, string> _localizedTextOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public event Action<TutorialPromptMessage> OnPromptChanged;
        public event Action<ObjectiveMarkerMessage> OnMarkerChanged;
        public event Action<ObjectInstance> OnObjectStateChanged;
        public event Action<string> OnGotoLand;

        /// <summary>Raised for every <c>upland</c> the script vocabulary executes, with the result of
        /// the <c>landStage</c> increment. Lets a test observe the descent without a campaign.</summary>
        public event Action<bool> OnUpLandLevel;

        /// <summary>Raised for every <c>openland</c> (<c>Script.as:458-468</c>).</summary>
        public event Action<string> OnOpenLand;

        public AreaTriggerSystem() : this(null, null, null, null, null)
        {
        }

        public AreaTriggerSystem(
            IPublisher<TutorialPromptMessage> promptPublisher,
            IPublisher<ObjectiveMarkerMessage> markerPublisher,
            IPublisher<LandTransitionMessage> landTransitionPublisher = null,
            LuaTriggerBridge luaBridge = null,
            ILandScriptHost landHost = null)
        {
            _promptPublisher = promptPublisher;
            _markerPublisher = markerPublisher;
            _landTransitionPublisher = landTransitionPublisher;
            _luaBridge = luaBridge;
            _landHost = landHost;
        }

        public void SetTextOverride(string key, string text)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            _localizedTextOverrides[key] = text;
        }

        /// <summary>
        /// Get the 2D bounding rectangle of an area trigger in room-local pixel space.
        /// </summary>
        public static Rect GetTriggerPixelRect(ObjectInstance trigger)
        {
            if (trigger == null)
            {
                return Rect.zero;
            }

            float w = TryParseFloatAttribute(trigger, "w", 2f);
            float h = TryParseFloatAttribute(trigger, "h", 2f);
            if (w <= 0f) w = 2f;
            if (h <= 0f) h = 2f;

            float widthPixels = w * WorldConstants.TILE_SIZE;
            float heightPixels = h * WorldConstants.TILE_SIZE;

            return new Rect(trigger.position.x, trigger.position.y, widthPixels, heightPixels);
        }

        /// <summary>
        /// Checks if a player bounding rectangle overlaps the trigger area.
        /// </summary>
        public static bool EvaluateOverlap(ObjectInstance trigger, Rect playerBoundsPixels)
        {
            if (trigger == null || !trigger.isActive)
            {
                return false;
            }

            Rect triggerRect = GetTriggerPixelRect(trigger);
            return triggerRect.Overlaps(playerBoundsPixels);
        }

        /// <summary>
        /// Called when the player enters an area trigger.
        /// Dispatches prompt messages and executes entry scripts.
        /// </summary>
        public void OnPlayerEnter(RoomInstance room, ObjectInstance trigger)
        {
            if (trigger == null || !trigger.isActive)
            {
                return;
            }

            string messKey = trigger.GetAttribute("mess", string.Empty);
            if (!string.IsNullOrEmpty(messKey))
            {
                string text = ResolveTutorialText(messKey);
                bool isDown = string.Equals(trigger.GetAttribute("down", "0"), "1", StringComparison.Ordinal);
                var promptMsg = new TutorialPromptMessage(messKey, text, true, isDown);
                _promptPublisher?.Publish(promptMsg);
                OnPromptChanged?.Invoke(promptMsg);
            }

            if (trigger.scripts != null)
            {
                for (int i = 0; i < trigger.scripts.Count; i++)
                {
                    MapObjectScriptData script = trigger.scripts[i];
                    if (script == null) continue;

                    if (string.IsNullOrEmpty(script.eventName) || string.Equals(script.eventName, "enter", StringComparison.OrdinalIgnoreCase))
                    {
                        // `trigger` is the owner: AS3 builds an area's inline script as
                        // `new Script(_loc5_, loc.land, this)` (Area.as:140) and an object's as
                        // `scr.owner = this` (Box.as:338). Without it a `@targ="this"` action has no
                        // `this` to resolve and the command is dropped.
                        ExecuteScript(room, script, trigger);
                    }
                }
            }

            // ---- The authored-script reference (AS3 Area.as:151-158, :354-368) ----
            //
            // `scr="beginBeg"` is a *reference* to a script in the game data, not an inline action
            // list. The port imported the attribute and never read it, so the opening cutscene of the
            // campaign could not start. This is the join.
            //
            // `@trig` makes the area one-shot, and the oracle keys that flag on the area's `uid`
            // (`if(this.trig && Boolean(uid)) { if(triggers[uid] != 1) { triggers[uid] = 1; start(); } }`).
            // An area with `@trig` but no uid therefore fires every time, which is the oracle's own
            // behaviour and not an oversight to "fix".
            string overScript = trigger.GetAttribute("scr", string.Empty);
            if (!string.IsNullOrEmpty(overScript) && !IsOneShotAlreadyFired(trigger))
            {
                Runner?.RunAuthored(room, overScript, trigger);
            }
        }

        /// <summary>
        /// The <c>@trig</c> gate (<c>Area.as:354-366</c>).
        ///
        /// <para>Returns <c>false</c> — i.e. "not yet fired, go ahead" — for every area that does not
        /// carry <c>@trig</c>, and for every area that carries it without a <c>uid</c>. When it returns
        /// <c>true</c> the flag is already set and the caller must not start the script.</para>
        /// </summary>
        private bool IsOneShotAlreadyFired(ObjectInstance trigger)
        {
            if (trigger == null) return false;

            string trig = trigger.GetAttribute("trig", string.Empty);
            if (string.IsNullOrEmpty(trig)) return false;

            string uid = trigger.GetAttribute("uid", string.Empty);
            if (string.IsNullOrEmpty(uid)) return false;

            return !_firedOneShotTriggers.Add(uid);
        }

        /// <summary>
        /// Called when the player exits an area trigger.
        /// Clears active prompt message and executes exit scripts.
        /// </summary>
        public void OnPlayerExit(RoomInstance room, ObjectInstance trigger)
        {
            if (trigger == null)
            {
                return;
            }

            string messKey = trigger.GetAttribute("mess", string.Empty);
            if (!string.IsNullOrEmpty(messKey))
            {
                var promptMsg = new TutorialPromptMessage(messKey, string.Empty, false);
                _promptPublisher?.Publish(promptMsg);
                OnPromptChanged?.Invoke(promptMsg);
            }

            if (trigger.scripts != null)
            {
                for (int i = 0; i < trigger.scripts.Count; i++)
                {
                    MapObjectScriptData script = trigger.scripts[i];
                    if (script == null) continue;

                    if (string.Equals(script.eventName, "exit", StringComparison.OrdinalIgnoreCase))
                    {
                        // Owner passed for the same reason as the enter path above.
                        ExecuteScript(room, script, trigger);
                    }
                }
            }

            // AS3 Area.as:155-158 / :369-372 — `@scrout` runs when the area stops overlapping, with no
            // one-shot gate (the `@trig` guard is on the *enter* path only).
            string outScript = trigger.GetAttribute("scrout", string.Empty);
            if (!string.IsNullOrEmpty(outScript))
            {
                Runner?.RunAuthored(room, outScript, trigger);
            }
        }

        /// <summary>
        /// The stepwise interpreter for this room, created on first use and registered with
        /// <see cref="ScriptSystem"/> so it advances at <see cref="PFE.Core.SimTickOrder.Scripts"/>.
        ///
        /// <para><b>There is always a runner, even with no container.</b> An offline fixture that
        /// constructs this type directly has no <c>ScriptSystem</c> and therefore no clock — but it must
        /// still exercise the <i>shipped</i> vocabulary rather than a second copy of it. So the fallback
        /// is a runner with the same hosts and no clock: the fixture steps it, or calls a command that
        /// needs no timing. Returning null here instead is how a test ends up asserting against a switch
        /// that no longer exists.</para>
        ///
        /// <para><b>But the choice is cached, so the off-clock case is recorded.</b> The first access
        /// decides whether this room has a clock, and an off-clock runner fires action 0 (which
        /// <c>Run</c> does synchronously) and then never advances — which reads as "the opening line
        /// appeared and then the game froze", not as "the runner has no clock". Every call site today is
        /// player-driven (<c>OnPlayerEnter</c> / <c>OnPlayerExit</c> / <c>OnObjectInteracted</c> /
        /// <c>OnObjectDestroyed</c>), so <see cref="ScriptSystem.Initialize"/> has long since run;
        /// <see cref="RunnerHasNoClock"/> is the flag that says so if that stops being true.</para>
        ///
        /// <para><b>Why a flag and not a <c>Debug.LogWarning</c>.</b> This is the path the offline
        /// fixtures take by construction — they have no container, so no <c>ScriptSystem</c> — and
        /// <c>UnityEngine.Debug</c> and <c>UnityEngine.Application</c> are ECall in that harness, so any
        /// engine call here throws <c>SecurityException: ECall methods must be packaged into a system
        /// module</c>. A "loud" warning on this branch turns <c>Upland_WithoutAHost_DoesNotThrow…</c> and
        /// ten siblings in <c>LandScriptActionTests</c> red — a worse failure than the one it reports,
        /// and one that is invisible in the editor. The play-mode surfacing of this flag belongs to a
        /// play-mode-only consumer (the debug overlay), not here.</para>
        /// </summary>
        public ScriptRunner Runner
        {
            get
            {
                if (_runner != null) return _runner;
                if (_disposed) return null;

                ScriptSystem system = ScriptSystem.Current;
                _runner = system != null
                    ? system.CreateRunner(this, () => this, this)
                    : new ScriptRunner(
                        campaign: () => this,
                        quests: () => PFE.Systems.Campaign.CampaignManager.Current,
                        objects: this,
                        // The unit half is forwarded through THIS type rather than handed the spawner
                        // directly, so the off-clock fallback and the clocked path resolve `@targ`
                        // identically. `this` is safe even when `_unitHost` is still null: UnitCommand
                        // answers false, which is the pre-change behaviour.
                        units: this);

                RunnerHasNoClock = system == null;

                system?.Register(_runner);
                return _runner;
            }
        }

        /// <summary>
        /// Whether this room's runner was built without a <see cref="ScriptSystem"/>, and therefore has
        /// no clock: a script started here fires its first action and then never advances.
        ///
        /// <para>Set on the first <see cref="Runner"/> access and never cleared — the runner is cached,
        /// so the answer cannot change for this room. False in a correctly booted game: the container
        /// sets <see cref="ScriptSystem.Current"/> in its <c>Initialize</c>, which runs inside
        /// <c>GameLifetimeScope.Awake</c>.</para>
        /// </summary>
        public bool RunnerHasNoClock { get; private set; }

        /// <summary>
        /// Takes this room's runner off the clock. Called from <c>RoomVisualController.ClearVisuals</c>,
        /// because a streamed-out room must stop advancing its scripts — a cutscene that kept ticking in
        /// a room the player has left would publish messages against a destroyed view.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_runner != null)
            {
                ScriptSystem.Current?.Unregister(_runner);
                _runner = null;
            }

            _firedOneShotTriggers.Clear();
        }

        /// <summary>
        /// Called when an object is interacted with (e.g. looting a box or clicking a wallcab).
        /// </summary>
        public void OnObjectInteracted(RoomInstance room, ObjectInstance obj)
        {
            if (obj == null || obj.scripts == null) return;

            for (int i = 0; i < obj.scripts.Count; i++)
            {
                MapObjectScriptData script = obj.scripts[i];
                if (script == null) continue;

                if (string.IsNullOrEmpty(script.eventName) ||
                    string.Equals(script.eventName, "interact", StringComparison.OrdinalIgnoreCase))
                {
                    // `obj` is the owner — this is the path a container's own `<scr>` takes, and the
                    // Oracle's `act="off" targ="this"` on a looted crate is exactly this shape.
                    ExecuteScript(room, script, obj);
                }
            }
        }

        /// <summary>
        /// Called when an object is destroyed or broken (e.g. wooden partition destroyed).
        /// </summary>
        public void OnObjectDestroyed(RoomInstance room, ObjectInstance obj)
        {
            if (obj == null || obj.scripts == null) return;

            for (int i = 0; i < obj.scripts.Count; i++)
            {
                MapObjectScriptData script = obj.scripts[i];
                if (script == null) continue;

                if (string.Equals(script.eventName, "die", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(script.eventName, "destroy", StringComparison.OrdinalIgnoreCase))
                {
                    // `obj` is the owner, for the same reason as the interact path above.
                    ExecuteScript(room, script, obj);
                }
            }
        }

        /// <summary>
        /// Runs an inline <c>&lt;scr&gt;</c> block through the stepwise interpreter.
        ///
        /// <para><b>Why this is a delegation and not a loop.</b> It used to walk the action list and call
        /// <see cref="ExecuteAction"/> for each — which meant every authored <c>@t</c> delay and every
        /// <c>wait</c> was silently ignored, because a synchronous loop cannot pause. A cutscene that
        /// should take twenty seconds ran instantly and nothing went red. The interpreter that honours
        /// the timing is <see cref="ScriptRunner"/>; this method exists only to hand it the script.</para>
        /// </summary>
        /// <param name="room">The room the script belongs to — AS3 <c>Script.land</c>.</param>
        /// <param name="script">The authored action list.</param>
        /// <param name="owner">The object the script hangs off, for <c>@targ="this"</c>.</param>
        public void ExecuteScript(RoomInstance room, MapObjectScriptData script, ObjectInstance owner = null)
        {
            if (room == null || script == null || script.actions == null) return;

            Runner?.Run(room, script, owner);
        }

        /// <summary>
        /// Runs one action through the interpreter — the compatibility seam the streaming and door
        /// fixtures drive.
        ///
        /// <para><b>There is deliberately no second switch here.</b> The port used to have two copies of
        /// this vocabulary: this one and the one in <c>ScriptRunner</c>. Two switches for one vocabulary
        /// is how the port acquired a phantom <c>exit</c> branch that could never fire, so this method
        /// now builds a one-action script and hands it to the same interpreter everything else uses. It
        /// contains no command names at all, which is what makes it structurally incapable of
        /// drifting.</para>
        /// </summary>
        public void ExecuteAction(RoomInstance room, MapObjectScriptActionData action)
        {
            if (room == null || action == null) return;

            // `lua` is the one command that never reaches the interpreter, because it is not part of the
            // oracle's vocabulary at all: `Script.com` (Script.as:199-562) has no `lua` branch. It is a
            // port extension that lets a room author a MoonSharp snippet, and it lives here rather than
            // in ScriptRunner so that ScriptRunner's command set stays exactly the oracle's — which is
            // what the self-policing vocabulary test asserts.
            if (string.Equals(action.act?.Trim(), "lua", StringComparison.OrdinalIgnoreCase))
            {
                _luaBridge?.ExecuteTriggerScript(room, null, action.val);
                return;
            }

            var single = new MapObjectScriptData
            {
                eventName = string.Empty,
                actions = new List<MapObjectScriptActionData> { action },
            };

            Runner?.Run(room, single);
        }

        // =====================================================================
        //  IScriptObjectHost — the `@targ` path, `allact` and `goto`
        //
        //  This type implements the object half because it is the one thing that already owns the
        //  per-room object lookup (FindObjectsByUidOrCode) and the door-collision side effect
        //  (UpdateDoorTileCollision). Moving those into a second class would mean two places that know
        //  how a door's tiles are stamped.
        // =====================================================================

        /// <summary>
        /// AS3 <c>_loc2_.command(param1.act, param1.val)</c> (<c>Script.as:218-236</c>) — the
        /// <c>@targ</c> dispatch.
        ///
        /// <para><b>The vocabulary here is <c>Obj.command</c>/<c>Box.command</c>, not <c>Script.com</c>.</b>
        /// <c>sign</c>, <c>off</c>, <c>on</c>, <c>onoff</c>, <c>open</c>, <c>close</c>, <c>unlock</c> and
        /// <c>lock</c> are the object's own commands. The two sets overlap in spelling — <c>off</c> is an
        /// object command while <c>openland</c> is a campaign one — which is exactly why the oracle
        /// branches on <c>@targ</c> <i>before</i> its command chain and why this must not be merged with
        /// it.</para>
        /// </summary>
        public bool ObjectCommand(RoomInstance room, string uidOrCode, string command, string value)
        {
            if (room == null || string.IsNullOrEmpty(uidOrCode) || string.IsNullOrEmpty(command)) return false;

            List<ObjectInstance> targets = FindObjectsByUidOrCode(room, uidOrCode);
            if (targets.Count == 0) return false;

            return DispatchObjectCommand(room, targets, uidOrCode, command, value);
        }

        /// <summary>
        /// The <c>@targ="this"</c> half — the caller already holds the object, so nothing is looked up.
        ///
        /// <para><b>Why this exists at all</b> (the long version is on
        /// <see cref="IScriptObjectHost.ObjectCommand(RoomInstance, ObjectInstance, string, string)"/>):
        /// the oracle passes the script's owner <i>by reference</i>, and the port's string round trip
        /// silently dropped every <c>this</c> whose object had a blank <c>uid</c>. There is deliberately
        /// no room-membership test — the oracle commands the owner unconditionally.</para>
        /// </summary>
        public bool ObjectCommand(RoomInstance room, ObjectInstance target, string command, string value)
        {
            if (room == null || target == null || string.IsNullOrEmpty(command)) return false;

            // The label is read only by `sign`, which publishes the id a HUD marker keys on. The
            // object's own identity is the honest value: the oracle hands over the object, never a
            // name, and for a `this` target the uid is usually blank while the code is not.
            string label = !string.IsNullOrEmpty(target.uid) ? target.uid
                         : !string.IsNullOrEmpty(target.code) ? target.code
                         : target.objectId;

            // A fresh one-element list rather than a reused field: the dispatch below raises
            // OnObjectStateChanged, and a handler that ran another script would clobber a shared list.
            var single = new List<ObjectInstance>(1) { target };
            return DispatchObjectCommand(room, single, label, command, value);
        }

        /// <summary>
        /// The shared body of both <c>@targ</c> overloads: the <c>Obj.command</c> / <c>Box.command</c>
        /// vocabulary (<c>Area.as:253-278</c>, <c>Box.as:446</c>).
        /// </summary>
        /// <param name="label">The id the command was addressed by. Read only by <c>sign</c>, which
        /// publishes it as the marker's key.</param>
        private bool DispatchObjectCommand(RoomInstance room, List<ObjectInstance> targets, string label,
                                           string command, string value)
        {
            string verb = command.Trim().ToLowerInvariant();

            switch (verb)
            {
                case "sign":
                {
                    bool show = value != "0" &&
                                !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                    Vector2 position = targets[0].position;
                    var markerMsg = new ObjectiveMarkerMessage(label, position, show);
                    _markerPublisher?.Publish(markerMsg);
                    OnMarkerChanged?.Invoke(markerMsg);
                    return true;
                }

                case "off":
                    for (int i = 0; i < targets.Count; i++)
                    {
                        targets[i].isActive = false;
                        OnObjectStateChanged?.Invoke(targets[i]);
                    }

                    return true;

                case "on":
                    for (int i = 0; i < targets.Count; i++)
                    {
                        targets[i].isActive = true;
                        OnObjectStateChanged?.Invoke(targets[i]);
                    }

                    return true;

                case "onoff":
                    for (int i = 0; i < targets.Count; i++)
                    {
                        targets[i].isActive = !targets[i].isActive;
                        OnObjectStateChanged?.Invoke(targets[i]);
                    }

                    return true;

                case "open":
                    for (int i = 0; i < targets.Count; i++)
                    {
                        targets[i].runtimeState.isOpen = true;
                        UpdateDoorTileCollision(room, targets[i], true);
                        OnObjectStateChanged?.Invoke(targets[i]);
                    }

                    return true;

                case "close":
                    for (int i = 0; i < targets.Count; i++)
                    {
                        targets[i].runtimeState.isOpen = false;
                        UpdateDoorTileCollision(room, targets[i], false);
                        OnObjectStateChanged?.Invoke(targets[i]);
                    }

                    return true;

                case "swap":
                    // AS3 `Interact.command("swap")` (Interact.as:1761-1765):
                    //     this.open = !this.open;
                    //     this.setAct("open", this.open ? 1 : 0);
                    // i.e. a pure toggle of the door state — no `allAct` re-entry, no autoClose.
                    //
                    // This is how every authored button drives a door through its own `<scr>`:
                    // `rooms_begin/room_4_1`'s knop is `<s act="swap" targ="trPl"/>` and `trPl` is the
                    // `platform1` at (27,16). The `allact` path already had `swap`
                    // (`ObjectVerbCommand`), but this `@targ` path did not, so the press resolved the
                    // target, found no verb, and the platform never moved — the button looked alive
                    // (the prompt appeared) and did nothing.
                    for (int i = 0; i < targets.Count; i++)
                    {
                        bool open = !targets[i].runtimeState.isOpen;
                        targets[i].runtimeState.isOpen = open;
                        UpdateDoorTileCollision(room, targets[i], open);
                        OnObjectStateChanged?.Invoke(targets[i]);
                    }

                    return true;

                case "unlock":
                case "lock":
                    // AS3 `Interact.command("unlock")` (:1709-1717):
                    //     this.active = true;
                    //     this.setAct("mine",101);
                    //     this.setAct("lock",101);
                    //     this.update();
                    //     this.shine();
                    // i.e. the script clears the object's MINE and LOCK outright — no roll, no key,
                    // no lockTip consult — and flashes the "unlock" emitter over it. This is the only
                    // way through `rooms_begin/room_2_0`'s `begDoor` (door3, locktip="0", no key
                    // authored anywhere): the trigger area at (17,15) runs the `beginMtn` cutscene
                    // whose second-to-last action is `<s act="unlock" targ="begDoor"/>`
                    // (GameData.as:1202-1216). A key-only door the player cannot open by hand and
                    // whose scripted unlock is a no-op is a bricked exit.
                    //
                    // `lock` has NO Interact.command branch in the oracle — it is a port-side command
                    // kept for symmetry with the legacy flag, and it writes the flag only.
                    for (int i = 0; i < targets.Count; i++)
                    {
                        ObjectInstance target = targets[i];
                        target.EnsureStructuredData();

                        if (verb == "lock")
                        {
                            if (!target.runtimeState.hasLockValue)
                            {
                                target.runtimeState.lockValue = 1f;
                            }

                            target.runtimeState.hasLockValue = true;
                        }
                        else
                        {
                            // The legacy flag the save format already round-trips…
                            target.runtimeState.hasLockValue = false;

                            // …and the security profile the interact gate ACTUALLY reads. This is the
                            // half that was missing: `SecurityGate.IsLocked` consults the profile on
                            // the runtime state, so a scripted unlock that wrote only the flag left
                            // the door locked at the gate — the state was correct and the door stayed
                            // shut. `ApplyUnlocked` is setAct("lock",101) + setAct("mine",101): the
                            // lock zeroes, the save records 101, and a signal mine on the same object
                            // disarms with it, exactly as the oracle's command does.
                            SecurityProfile profile = SecurityRuntime.ProfileFor(target, room);
                            if (profile != null)
                            {
                                SecurityRules.ApplyUnlocked(profile);
                                SecurityRuntime.Commit(target, profile);
                            }

                            // AS3's `shine()` — Emitter.emit("unlock", loc, X, Y - scY/2) with ten
                            // particles spread over the object's box (Interact.as:1678-1686, called
                            // from `command("unlock")` at :1717). The port has the art
                            // (Art/Particles/flUnlock) and the emitter, so this is the real flash,
                            // not a log line: see ObjectShineEmitter for the anchor and the one
                            // measured divergence (authored extent vs the oracle's vis.width).
                            ObjectShineEmitter.Emit(_particles, room, target);
                        }

                        OnObjectStateChanged?.Invoke(target);
                    }

                    return true;
            }

            Debug.LogWarning($"[AreaTriggerSystem] `{command}` on '{label}' is not an object command " +
                             "(Obj.command / Box.command). It was not run; see ScriptVocabulary.ObjectCommands " +
                             "for the set the oracle has.");
            return true;
        }

        // =====================================================================
        //  IUnitCommandHost — the other half of the oracle's single uidObjs table
        // =====================================================================

        /// <summary>
        /// Hands this room's live-unit host over. Called by <c>RoomVisualController</c>, which owns both
        /// this type and the <c>RoomUnitSpawner</c> and is therefore the only place that can pair them.
        /// </summary>
        /// <remarks>
        /// <para><b>Pushed after construction on purpose.</b> See <see cref="_unitHost"/>: this type is
        /// built before the spawner exists, so a constructor argument could only ever be null. A second
        /// call simply replaces the host, which is what a room rebuild does.</para>
        /// </remarks>
        public void SetUnitHost(IUnitCommandHost unitHost)
        {
            _unitHost = unitHost;
        }

        /// <summary>
        /// Hands this room's particle emitter over, for AS3 <c>Interact.shine()</c>. Called by
        /// <c>RoomVisualController</c> from both places it can learn the adapter exists — the adapter
        /// can arrive after the first room is built, and a shine that silently skipped the opening
        /// room is the failure mode this push avoids. A second call replaces the emitter, which is what
        /// a room rebuild does.
        /// </summary>
        public void SetParticleEmitter(PFE.Systems.Particles.Adapters.RoomParticleEmitter emitter)
        {
            _particles = emitter;
        }

        /// <summary>
        /// AS3 <c>_loc2_.command(param1.act, param1.val)</c> for a target that turns out to be a
        /// <b>unit</b> rather than a map object.
        ///
        /// <para><b>This type owns no units and never will.</b> The live controllers belong to
        /// <see cref="PFE.Systems.Map.Rendering.RoomUnitSpawner"/>, which is the only thing that knows
        /// which record produced which GameObject. This method is a forwarder so that
        /// <see cref="ScriptRunner"/> can ask one host, exactly as the oracle does, and so that the
        /// off-clock runner built in <see cref="Runner"/> resolves units identically to the clocked
        /// one.</para>
        ///
        /// <para><b>A miss is answered <c>false</c>, never swallowed.</b> The runner reports an
        /// unmatched uid only when <i>both</i> hosts decline, so answering <c>true</c> here for an
        /// unknown id would turn "no unit named surfCalam" into a silent success — the failure mode
        /// this whole change exists to remove.</para>
        /// </summary>
        public bool UnitCommand(RoomInstance room, string uidOrCode, string command, string value)
        {
            if (_unitHost == null) return false;
            return _unitHost.UnitCommand(room, uidOrCode, command, value);
        }

        /// <summary>
        /// AS3 <c>World.w.loc.allAct(null, val, n)</c> (<c>Script.as:296-299</c>) — run an interaction
        /// by id.
        ///
        /// <para><b>The object is null, as it is in the oracle.</b> An <c>allact</c> that needs the object
        /// it came from is therefore unreachable this way, which is why the oracle only uses this form
        /// for room-level interactions.</para>
        ///
        /// <para><b>Which is why the id is passed as an override rather than left to the context.</b> With
        /// no object there is nothing for <c>ActionId</c> to read, so this method used to dispatch an
        /// empty id and get <c>NotApplicable</c> back — the <c>allact</c> script command was inert from
        /// the day it was written, and nothing went red because "no script here" is a legitimate answer
        /// for every other caller.</para>
        /// </summary>
        public void RunAllAct(RoomInstance room, string allactId, string n)
        {
            if (room == null || string.IsNullOrEmpty(allactId)) return;

            ObjectActionDispatcher dispatcher = ResolveAllActDispatcher();
            var context = new ObjectActionContext(
                room, null, null, Vector3.zero, actionIdOverride: allactId, allIdOverride: n);
            ObjectActionOutcome outcome = dispatcher.Dispatch(in context);

            if (outcome == ObjectActionOutcome.Unhandled)
            {
                Debug.LogWarning($"[AreaTriggerSystem] `allact {allactId}` is not a registered interaction, " +
                                 "so nothing ran. See ObjectActionDispatcher.RegisteredActionIds.");
            }
        }

        /// <summary>
        /// AS3 <c>goto val</c> (<c>Script.as:435-442</c>) — <c>World.w.land.gotoXY(x, y)</c>. The oracle
        /// only calls it when the value split into exactly two parts, and the runner enforces that before
        /// getting here.
        /// </summary>
        public void GotoXY(string x, string y)
        {
            if (!float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out float px) ||
                !float.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out float py))
            {
                Debug.LogWarning($"[AreaTriggerSystem] `goto '{x} {y}'` is not a numeric pair, so the " +
                                 "player did not move.");
                return;
            }

            OnGotoXY?.Invoke(px, py);
        }

        /// <summary>
        /// Raised for <c>goto</c>. A separate event from <see cref="OnGotoLand"/> because the two do
        /// different things: <c>gotoland</c> changes land, <c>goto</c> moves the player within the one
        /// they are already in.
        /// </summary>
        public event Action<float, float> OnGotoXY;

        // =====================================================================
        //  ILandScriptHost — the campaign vocabulary, as a decorator
        //
        //  The vocabulary itself lives in ScriptRunner (upland / openland / refill / passed /
        //  trigger / gotoland — Script.as:392-472). What cannot live there is the three events above:
        //  they belong to THIS type, and they are what a descent or streaming fixture observes a
        //  campaign action through. So the runner is handed `this` rather than the raw host, and this
        //  type raises the event AND forwards to the real campaign.
        //
        //  Without the decorator the vocabulary still reaches the campaign and every counter still
        //  moves — which is why `host.UpLandLevelCalls == 1` passed while `OnUpLandLevel` never fired
        //  and the tests that assert on it did not. That asymmetry is the whole reason the forwarding
        //  sits here instead of inside ScriptRunner.
        // =====================================================================

        /// <summary>
        /// <c>upland</c> — forwards to the campaign and raises <see cref="OnUpLandLevel"/> with the
        /// result, whether or not a campaign is there to answer.
        /// </summary>
        public bool UpLandLevel()
        {
            ILandScriptHost host = LandHost;
            bool moved = host != null && host.UpLandLevel();
            OnUpLandLevel?.Invoke(moved);
            return moved;
        }

        /// <summary>
        /// <c>openland</c> — forwards and raises <see cref="OnOpenLand"/> with the land id.
        ///
        /// <para>The event carries the id rather than the result because the id is what the oracle's
        /// caller has (<c>Script.as:458-468</c> reads <c>@val</c>), and a scene with no campaign still
        /// has to be able to say which land a script asked for.</para>
        /// </summary>
        public bool OpenLand(string landId)
        {
            ILandScriptHost host = LandHost;
            bool opened = host != null && host.OpenLand(landId);
            OnOpenLand?.Invoke(landId);
            return opened;
        }

        /// <summary>
        /// <c>gotoland</c> — raises <see cref="OnGotoLand"/> first, so the event fires even with no
        /// campaign, then forwards.
        ///
        /// <para><b>It does not publish a <c>LandTransitionMessage</c> of its own.</b> The campaign's
        /// <c>GotoLand</c> already transitions (<c>CampaignManager.cs:458-462</c> →
        /// <c>TransitionToLand</c>); publishing here as well would request the same land twice.</para>
        /// </summary>
        public void GotoLand(string landId, int n, string coordinates)
        {
            OnGotoLand?.Invoke(landId);
            LandHost?.GotoLand(landId, n, coordinates);
        }

        /// <summary><c>refill</c> → <c>Land.refill()</c> (<c>Land.as:1481-1496</c>).</summary>
        public void RefillVendors() => LandHost?.RefillVendors();

        /// <summary><c>passed</c> (<c>Script.as:469-472</c>).</summary>
        public void MarkPassed() => LandHost?.MarkPassed();

        /// <summary>
        /// <c>trigger</c> (<c>Script.as:424-434</c>).
        ///
        /// <para><b>The no-host case is silent on purpose.</b> A room is built while the world is
        /// assembled, which can precede the campaign, so "no host yet" is a legitimate state rather
        /// than an error — and the alternative, a <c>Debug.LogWarning</c> here, is fatal in the offline
        /// harness, where <c>UnityEngine.Debug</c> is an ECall that throws. It would turn the fixtures
        /// that exercise this vocabulary red to report a condition they cannot fix.</para>
        /// </summary>
        public void SetTrigger(string triggerName, PFE.Systems.Campaign.TriggerValue value) =>
            LandHost?.SetTrigger(triggerName, value);

        /// <summary><c>exit</c>'s <c>Game.gotoNextLevel()</c> (<c>Game.as:464-472</c>).</summary>
        public void GotoNextLevel() => LandHost?.GotoNextLevel();

        /// <summary>
        /// The <c>allact</c> dispatcher, built on first use.
        ///
        /// <para><b>Built here rather than injected, because this type is constructed with <c>new</c>.</b>
        /// <c>RoomVisualController.Initialize</c> calls <c>new AreaTriggerSystem()</c>, so nothing the
        /// container holds can reach it — the same limitation the lazy <see cref="LandHost"/> fallback
        /// documents. The dispatcher is built with whatever hosts resolve, and reports by name whatever it
        /// had to leave out.</para>
        ///
        /// <para><b><c>CreateFull</c> rather than <c>CreateDefault</c>, so the tagged broadcast exists.</b>
        /// Without it every <c>allact</c> that is not one of the oracle's eighteen explicit branches
        /// answers <c>Unhandled</c> — which is 210 of the 214 placement-level <c>allact</c> attributes in
        /// the shipped rooms, and all of them are buttons.</para>
        /// </summary>
        private ObjectActionDispatcher ResolveAllActDispatcher()
        {
            return _allActDispatcher ??= ObjectActionDispatcher.CreateFull(
                transition: null,
                reportUnhandled: message => Debug.LogWarning(message),
                travelMapHost: PFE.Systems.Campaign.CampaignManager.Current,
                exitHost: LandHost,
                // AS3 `loc.prob` — the detached-room half of `Land.gotoProb` (Land.as:1391-1438), which
                // lives on the scene singleton that owns the room registry. Null before the first land is
                // built, which is the reported refusal AS3 takes for an unbuilt prob.
                probHost: RoomTransitionManager.Instance,
                verbHost: new ObjectVerbHost
                {
                    // AS3 setAct("open", n) → setDoor(): restamp the object's tiles.
                    StampDoorTiles = (room, obj, isOpen) => UpdateDoorTileCollision(room, obj, isOpen),
                    // AS3 `loc.prob.check()` (Probation.as:146-156), reached from Interact.as:880
                    // (looting a container), :1321 (a knob with action 0) and :1753 (the `dam` verb's
                    // non-explosive arm). Resolved per call rather than held, because the registry is
                    // replaced wholesale on every land build — see RoomTransitionManager.ProbSessions.
                    CheckProbRoom = CheckProbRoomFor,
                    // AS3 `Interact.shine()` (Interact.as:1678-1686) — the ten-particle "unlock"
                    // sparkle the `hack` and `unlock` verbs both end with (:1709, :1717). This delegate
                    // had no binder at all, so both verbs ran their state changes and flashed nothing.
                    Shine = (room, obj) => ObjectShineEmitter.Emit(_particles, room, obj),
                });
        }

        private ObjectActionDispatcher _allActDispatcher;

        /// <summary>
        /// The room-cleared decision for a room, if it is a prob room at all.
        ///
        /// <para><b>Resolved per call.</b> A room that is not a prob has no session and costs one
        /// dictionary miss; a prob whose registry has not been built yet returns without touching the
        /// state machine, which is the same "no prob host" refusal AS3 takes for a land with no
        /// probs.</para>
        /// </summary>
        private void CheckProbRoomFor(RoomInstance room)
        {
            PFE.Systems.Map.Generation.ProbRoomSessions sessions =
                RoomTransitionManager.Instance?.ProbSessions;

            if (sessions == null) return;
            if (room == null || string.IsNullOrEmpty(room.probId)) return;

            sessions.Check(room);
        }

        /// <summary>
        /// Stamps or clears solid wall physics on tiles occupied by a door object.
        /// Port of AS3 Box.as:setDoor().
        /// </summary>
        public static void UpdateDoorTileCollision(RoomInstance room, ObjectInstance obj, bool isOpen)
        {
            if (room?.tiles == null || obj == null) return;

            // Door tile dimensions: size is width in tiles, width is height in tiles (AS3 wid)
            int widthTiles = 1;
            int heightTiles = 2;
            if (obj.definition != null)
            {
                if (obj.definition.size > 0) widthTiles = obj.definition.size;
                if (obj.definition.width > 0) heightTiles = obj.definition.width;
            }

            float widthPx = widthTiles * WorldConstants.TILE_SIZE;
            float leftPx = obj.position.x - widthPx * 0.5f;
            float bottomPx = obj.position.y;

            int txMin = Mathf.FloorToInt(leftPx / WorldConstants.TILE_SIZE + 0.01f);
            int txMax = txMin + widthTiles - 1;
            int tyMin = Mathf.FloorToInt(bottomPx / WorldConstants.TILE_SIZE);
            int tyMax = tyMin + heightTiles - 1;

            if (!isOpen)
            {
                EjectPlayerIfOverlappingDoor(room, obj, leftPx, leftPx + widthPx, bottomPx, bottomPx + heightTiles * WorldConstants.TILE_SIZE);
            }

            TilePhysicsType targetType = isOpen ? TilePhysicsType.Air : TilePhysicsType.Wall;

            for (int x = txMin; x <= txMax; x++)
            {
                for (int y = tyMin; y <= tyMax; y++)
                {
                    TileData tile = room.GetTileAtCoord(new Vector2Int(x, y));
                    if (tile != null)
                    {
                        tile.physicsType = targetType;
                    }
                }
            }

            // The chain mirror (bullet/round collision) is built from this grid and refreshed only
            // from `RoomInstance.TilesMutated` — so writing `physicsType` above without saying so left
            // it describing the door's OPPOSITE state. `DoorPropPresenter.SetOpen` has always
            // notified; this, the script/Lua path, did not, so a door a script opened still stopped
            // rounds at a phantom surface in the doorway. Same one-line contract, both paths.
            room.NotifyTilesMutated(new RectInt(txMin, tyMin, txMax - txMin + 1, tyMax - tyMin + 1));

            // AS3 `Box.setDoor()` force-relights the room when the door is *opened* — and only then:
            // `if(param1) { loc.isRelight = true; loc.isRebuild = true; }` (Box.as:688-692, param1 =
            // the new open state). The room's per-frame gate (Location.as:3398) then runs the full
            // light pass on the next frame regardless of camera motion.
            //
            // This method is the port of `Box.setDoor` for the **script** and **Lua** paths
            // (ExecuteAction "open"/"close" above, and LuaTriggerBridge); DoorPropPresenter is the
            // same port for the player-interact path and requests it in SetOpen. Both must, or a door
            // a script opens leaves the room behind it dark until the player happens to move.
            //
            // The relight is now already raised by NotifyTilesMutated above (which calls
            // RequestRelight unconditionally, matching SetOpen's "both directions" reasoning), so this
            // is kept only as the documented statement of intent — it is idempotent, not a second pass.
            if (isOpen)
            {
                room.RequestRelight();
            }
        }

        private static void EjectPlayerIfOverlappingDoor(
            RoomInstance room,
            ObjectInstance obj,
            float leftPx,
            float rightPx,
            float bottomPx,
            float topPx)
        {
            var player = GameObject.FindWithTag("Player");
            if (player == null)
            {
                var locomotion = UnityEngine.Object.FindFirstObjectByType<PFE.Entities.Player.PlayerLocomotionController>();
                if (locomotion != null) player = locomotion.gameObject;
            }
            if (player == null) return;

            Vector3 playerWorldPos = player.transform.position;
            float roomOriginPxX = WorldCoordinates.RoomOriginPixelX(room.landPosition.x, room.borderOffset);
            float roomOriginPxY = WorldCoordinates.RoomOriginPixelY(room.landPosition.y, room.borderOffset);

            float playerPxX = (playerWorldPos.x * 100f) - roomOriginPxX;
            float playerPxY = (playerWorldPos.y * 100f) - roomOriginPxY;

            float halfW = 15f; // 15px (0.15m)
            var tpc = player.GetComponent<PFE.Systems.Physics.TilePhysicsController>();
            if (tpc != null)
            {
                halfW = tpc.CollisionWidth * 0.5f;
            }
            else
            {
                var col = player.GetComponent<Collider2D>();
                if (col != null && !col.isTrigger)
                {
                    halfW = Mathf.Max(12f, col.bounds.extents.x * 100f);
                }
            }

            bool overlapX = (playerPxX + halfW) > leftPx + 0.5f && (playerPxX - halfW) < rightPx - 0.5f;
            bool overlapY = playerPxY + 50f > bottomPx && playerPxY < topPx;

            if (overlapX && overlapY)
            {
                float doorCenterPx = (leftPx + rightPx) * 0.5f;
                const float marginPx = 2f;
                float targetPxX = playerPxX < doorCenterPx
                    ? (leftPx - halfW - marginPx)
                    : (rightPx + halfW + marginPx);

                float targetWorldX = (targetPxX + roomOriginPxX) * 0.01f;
                Vector3 newPos = new Vector3(targetWorldX, playerWorldPos.y, playerWorldPos.z);

                if (tpc != null)
                {
                    tpc.SetUnityPosition(newPos);
                    tpc.TeleportTo(tpc.PixelPosition.x, tpc.PixelPosition.y);
                }
                else
                {
                    var motor = player.GetComponent<PFE.Systems.Physics.IMovementMotor>();
                    if (motor != null)
                    {
                        motor.SetUnityPosition(newPos);
                        motor.SetDesiredHorizontalSpeed(0f);
                    }
                    else
                    {
                        player.transform.position = newPos;
                    }
                }
            }
        }

        private static List<ObjectInstance> FindObjectsByUidOrCode(RoomInstance room, string uidOrCode)
        {
            var results = new List<ObjectInstance>();
            if (room?.objects == null || string.IsNullOrEmpty(uidOrCode)) return results;

            for (int i = 0; i < room.objects.Count; i++)
            {
                ObjectInstance obj = room.objects[i];
                if (obj == null) continue;

                if (string.Equals(obj.uid, uidOrCode, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(obj.code, uidOrCode, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(obj.objectId, uidOrCode, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(obj);
                }
            }

            return results;
        }

        public string ResolveTutorialText(string messKey)
        {
            if (string.IsNullOrWhiteSpace(messKey))
            {
                return string.Empty;
            }

            if (_localizedTextOverrides.TryGetValue(messKey, out string customText))
            {
                return customText;
            }

            // Standard fallback texts matching the original Flash baseline
            return messKey switch
            {
                "trDownJump" => "Press [S]+[Spacebar] to jump down",
                "trSit" => "Press [S] to sit / crouch",
                "trCont" => "Check the container",
                "trTele" => "Use telekinesis or jump to the upper ledge",
                "trPunch" => "Break the barricade with melee attack",
                _ => messKey
            };
        }

        private static float TryParseFloatAttribute(ObjectInstance obj, string key, float defaultValue)
        {
            if (obj == null) return defaultValue;
            string raw = obj.GetAttribute(key, string.Empty);
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : defaultValue;
        }
    }
}
