using System;
using System.Collections.Generic;
using System.Globalization;
using MessagePipe;
using PFE.Core.Messages;
using PFE.Systems.Map.Actions;
using UnityEngine;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// Evaluates area trigger overlaps, dispatches tutorial messages, and executes map object scripts.
    /// Bridges ActionScript 3 Area.as and Script.as functionality into Unity.
    /// </summary>
    public sealed class AreaTriggerSystem : IScriptObjectHost, ILandScriptHost
    {
        private readonly IPublisher<TutorialPromptMessage> _promptPublisher;
        private readonly IPublisher<ObjectiveMarkerMessage> _markerPublisher;
        private readonly IPublisher<LandTransitionMessage> _landTransitionPublisher;
        private readonly LuaTriggerBridge _luaBridge;
        private readonly ILandScriptHost _landHost;

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
                        ExecuteScript(room, script);
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
                        ExecuteScript(room, script);
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
                    ? system.CreateRunner(this, () => this)
                    : new ScriptRunner(
                        campaign: () => this,
                        quests: () => PFE.Systems.Campaign.CampaignManager.Current,
                        objects: this);

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
                    ExecuteScript(room, script);
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
                    ExecuteScript(room, script);
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

            string verb = command.Trim().ToLowerInvariant();

            switch (verb)
            {
                case "sign":
                {
                    bool show = value != "0" &&
                                !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                    Vector2 position = targets[0].position;
                    var markerMsg = new ObjectiveMarkerMessage(uidOrCode, position, show);
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

                case "unlock":
                case "lock":
                    // AS3 `Box.command("unlock")` clears the door's lock so the player may open it. The
                    // port keeps a lock as `hasLockValue` + `lockValue` on the runtime state — the pair
                    // the save format already round-trips (RoomStateSnapshot.cs:935-937).
                    //
                    // `beginMtn` is the caller that needs this: it unlocks `begDoor` at the end of the
                    // checkpoint cutscene so the player can leave the catacombs.
                    //
                    // Note the read side is not written yet: nothing in the port consults `hasLockValue`
                    // before opening a door, so the *state* is now correct and the *gate* is still to
                    // come. Setting it is not a no-op — the state is saved, and the gate is a separate
                    // change on the interact path.
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
                            target.runtimeState.hasLockValue = false;
                        }

                        OnObjectStateChanged?.Invoke(target);
                    }

                    return true;
            }

            Debug.LogWarning($"[AreaTriggerSystem] `{command}` on '{uidOrCode}' is not an object command " +
                             "(Obj.command / Box.command). It was not run; see ScriptVocabulary.ObjectCommands " +
                             "for the set the oracle has.");
            return true;
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
                probHost: null,
                verbHost: new ObjectVerbHost
                {
                    // AS3 setAct("open", n) → setDoor(): restamp the object's tiles.
                    StampDoorTiles = (room, obj, isOpen) => UpdateDoorTileCollision(room, obj, isOpen),
                    // AS3 `loc.prob.check()`. No prob host here, so this stays null and the verb
                    // reports it rather than silently skipping.
                    CheckProbRoom = null,
                });
        }

        private ObjectActionDispatcher _allActDispatcher;

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

            // AS3 `Box.setDoor()` force-relights the room when the door is *opened* — and only then:
            // `if(param1) { loc.isRelight = true; loc.isRebuild = true; }` (Box.as:688-692, param1 =
            // the new open state). The room's per-frame gate (Location.as:3398) then runs the full
            // light pass on the next frame regardless of camera motion.
            //
            // This method is the port of `Box.setDoor` for the **script** and **Lua** paths
            // (ExecuteAction "open"/"close" above, and LuaTriggerBridge); DoorPropPresenter is the
            // same port for the player-interact path and requests it in SetOpen. Both must, or a door
            // a script opens leaves the room behind it dark until the player happens to move.
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
