using System;
using System.Collections.Generic;
using PFE.Core;
using PFE.Core.Rng;
using PFE.Entities.Units;
using PFE.Systems.Inventory;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.RPG;
using UnityEngine;

namespace PFE.Systems.Security
{
    /// <summary>
    /// What a security press did, from the presenter's point of view. <b>Three answers, not two</b>,
    /// because "the press was consumed" and "the lock opened" are different facts: a failed pick must
    /// not fall through to opening the door, while a successful one must.
    /// </summary>
    public enum SecurityGateOutcome
    {
        /// <summary>The object has no lock — the caller proceeds with its normal action.</summary>
        NotLocked = 0,

        /// <summary>The lock opened (or a key was used) — the caller proceeds with its normal action.</summary>
        Opened = 1,

        /// <summary>The press was consumed by the security layer and nothing else should happen.</summary>
        Handled = 2,
    }

    /// <summary>
    /// The live half of the security subsystem: the per-object profile cache, the actor snapshot, the
    /// event log the F2 tab renders, and the room alarms.
    ///
    /// <para><b>Why a runtime singleton rather than a component on every prop.</b> A lock's state lives
    /// on the <i>object</i> (<c>ObjectInstance.runtimeState</c>) and must outlive its presenter — a room
    /// streams out and back and gets new presenters, while the object and its saved state survive. So
    /// the state cannot live on a <c>MonoBehaviour</c>; what does live there is the tick (alarms) and
    /// the RNG, and there is exactly one of each.</para>
    /// </summary>
    [LocalOnly]
    public sealed class SecurityRuntime : MonoBehaviour
    {
        private const int LogCapacity = 24;
        private const int MaxStepsPerFrame = 5;

        private static SecurityRuntime _instance;
        private static IRngService _rng;

        private readonly Dictionary<RoomInstance, RoomAlarm> _alarms = new Dictionary<RoomInstance, RoomAlarm>();
        private readonly List<SecurityLogEntry> _log = new List<SecurityLogEntry>(LogCapacity);
        private readonly List<UnitController> _unitScratch = new List<UnitController>();

        private float _accumulator;
        private int _tick;

        /// <summary>The live runtime, created on first use.</summary>
        public static SecurityRuntime Instance
        {
            get
            {
                if (_instance != null) return _instance;

                var go = new GameObject("[SecurityRuntime]");
                _instance = go.AddComponent<SecurityRuntime>();
                if (Application.isPlaying)
                {
                    DontDestroyOnLoad(go);
                }

                return _instance;
            }
        }

        /// <summary>
        /// The security RNG. <b>Its own stream</b>, so a lock roll cannot shift the combat or loot
        /// sequence — the same reasoning <c>LootRuntime.Rng</c> uses.
        /// </summary>
        public static IRngService Rng =>
            _rng ??= new PcgRngService().GetStream(RngStream.Spawn);

        /// <summary>The most recent events, oldest first. Bounded — the log is a readout, not a record.</summary>
        public static IReadOnlyList<SecurityLogEntry> Log => Instance._log;

        private static GameObject _cachedPlayer;

        /// <summary>
        /// The live player object, cached.
        ///
        /// <para><b>Needed because <see cref="PFE.Systems.Interaction.IHoldInteractable.HoldFrames"/> has
        /// no user parameter.</b> AS3 computes <c>t_action</c> from <c>World.w.pers</c> — a global — so the
        /// hold length is known before anyone is named as the actor. The port's interface is per-target,
        /// so the player has to be resolved here instead. Cached, and re-resolved when the cached object
        /// dies (a room rebuild hands the game a new one).</para>
        /// </summary>
        public static GameObject PlayerObject
        {
            get
            {
                if (_cachedPlayer != null) return _cachedPlayer;

                var player = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
                _cachedPlayer = player != null ? player.gameObject : null;
                return _cachedPlayer;
            }
        }

        /// <summary>Test hook: replace the RNG so an offline test can drive exact outcomes.</summary>
        public static void SetRngForTests(IRngService rng)
        {
            _rng = rng;
        }

        // =====================================================================================
        // Profiles — the object's lock, resolved once and kept on its runtime state
        // =====================================================================================

        /// <summary>
        /// The security profile of <paramref name="obj"/>, built on first use and cached on
        /// <c>runtimeState</c>.
        ///
        /// <para><b>Why the cache is on the object and not on this component.</b> AS3 builds an
        /// <c>Interact</c> per location and saves its <c>lock</c>/<c>lockLevel</c>/<c>mine</c>/<c>dif</c>
        /// (<c>Interact.as:599-609</c>). The constructor <i>randomises</i> the difficulty
        /// (<c>:505-516</c>), so building twice would give the same door two different locks. The
        /// runtime state is the only thing that survives a room rebuild <b>and</b> a save.</para>
        /// </summary>
        public static SecurityProfile ProfileFor(ObjectInstance obj, RoomInstance room)
        {
            if (obj == null) return null;

            obj.EnsureStructuredData();
            MapObjectRuntimeStateData state = obj.runtimeState;

            if (!state.hasSecurityState)
            {
                bool procedural = IsProceduralLand(room);
                SecurityProfile built = SecurityRules.Build(obj, procedural, Rng);
                StoreProfile(state, built);
            }

            return LoadProfile(state);
        }

        /// <summary>Force a rebuild from the authored data — the F2 tab's "re-roll" control.</summary>
        public static SecurityProfile RebuildProfile(ObjectInstance obj, RoomInstance room)
        {
            if (obj == null) return null;
            obj.EnsureStructuredData();
            obj.runtimeState.hasSecurityState = false;
            return ProfileFor(obj, room);
        }

        /// <summary>
        /// AS3's <c>land.rnd</c> (<c>Interact.as:505</c>) — whether the land is procedurally generated,
        /// which is what lets a lock gain up to +3 difficulty. The port has no per-land <c>rnd</c> flag;
        /// the closest honest answer is "the room is on the land grid", i.e. not a detached prob room.
        /// </summary>
        private static bool IsProceduralLand(RoomInstance room)
        {
            return room != null && string.IsNullOrEmpty(room.probId);
        }

        private static void StoreProfile(MapObjectRuntimeStateData state, SecurityProfile p)
        {
            state.hasSecurityState = true;
            state.securityLock = p.Lock;
            state.securitySaveLock = p.SaveLock;
            state.securityLockLevel = p.LockLevel;
            state.securityLockTip = (int)p.LockTip;
            state.securityLockHp = p.LockHp;
            state.securityLockAtt = p.LockAtt;
            state.securityMine = p.Mine;
            state.securityMineTip = p.MineTip;
            state.securityHackable = p.IsHackable;
            state.securityAllDif = p.AllDif;
        }

        private static SecurityProfile LoadProfile(MapObjectRuntimeStateData state)
        {
            return new SecurityProfile
            {
                Lock = state.securityLock,
                SaveLock = state.securitySaveLock,
                LockLevel = state.securityLockLevel,
                LockTip = (SecurityLockTip)state.securityLockTip,
                LockHp = state.securityLockHp,
                LockAtt = state.securityLockAtt,
                Mine = state.securityMine,
                MineTip = state.securityMineTip,
                IsHackable = state.securityHackable,
                AllDif = state.securityAllDif,
            };
        }

        /// <summary>Write a mutated profile back onto the object's runtime state.</summary>
        public static void Commit(ObjectInstance obj, SecurityProfile profile)
        {
            if (obj == null || profile == null) return;
            obj.EnsureStructuredData();
            StoreProfile(obj.runtimeState, profile);
        }

        // =====================================================================================
        // The actor
        // =====================================================================================

        /// <summary>
        /// Snapshot the acting unit's security numbers. <paramref name="user"/> is the player object the
        /// interaction path hands over; a null or stat-less user yields a zeroed actor, which will fail
        /// every roll rather than throw.
        /// </summary>
        public static SecurityActor ReadActor(GameObject user)
        {
            var actor = new SecurityActor
            {
                // A null LockAtt default of 0 would make every failure harmless; 1 is AS3's default
                // (Pers.as:287) and is what an unknown actor should behave like.
                LockAtt = 1f,
                PinBreak = 1f,
                HackAtt = 3,
                LockPickTime = 30,
            };

            if (user == null) return actor;

            CharacterStats stats = user.GetComponent<CharacterStats>();
            if (stats == null)
            {
                stats = user.GetComponentInParent<CharacterStats>();
            }

            if (stats != null)
            {
                actor.SkillPick = stats.GetLockTip(1);
                actor.SkillTerminal = stats.GetLockTip(2);
                actor.SkillMine = stats.GetLockTip(3);
                actor.SkillSignal = stats.GetLockTip(6);
                actor.SkillRepair = stats.GetLockTip(4);
                actor.MasterPick = stats.GetLockMaster(1);
                actor.MasterTerminal = stats.GetLockMaster(2);
                actor.LockAtt = stats.lockAtt;
                actor.PinBreak = stats.pinBreak;
                actor.UpChance = stats.upChance;
                actor.HackAtt = stats.hackAtt;
                actor.Security = stats.security;
                actor.LockPickTime = stats.lockPickTime;
            }

            PlayerInventory inventory = user.GetComponent<PlayerInventory>();
            if (inventory == null)
            {
                inventory = user.GetComponentInParent<PlayerInventory>();
            }

            if (inventory != null)
            {
                actor.Pins = inventory.CountOf("pin");
                actor.Runa = inventory.CountOf("runa");
                actor.Reboot = inventory.CountOf("reboot");
            }

            return actor;
        }

        // =====================================================================================
        // Working a lock
        // =====================================================================================

        /// <summary>
        /// Run one press against <paramref name="obj"/>'s security. Mutates the object's runtime state
        /// and logs the result.
        ///
        /// <para><b>Mine first, then lock</b> — AS3's order (<c>Interact.as:911</c> before <c>:1005</c>).
        /// A live mine on a container has to be dealt with before the lock is even reachable, which is
        /// what makes a mined safe a two-stage problem.</para>
        /// </summary>
        public static SecurityGateOutcome TryWork(
            GameObject user,
            ObjectInstance obj,
            RoomInstance room,
            out SecurityAttemptResult result)
        {
            result = default;

            SecurityProfile profile = ProfileFor(obj, room);
            if (profile == null || !profile.IsLocked)
            {
                if (profile != null && profile.Mine > 0)
                {
                    // A mine on an otherwise-unlocked object still has to be defused.
                }
                else
                {
                    return SecurityGateOutcome.NotLocked;
                }
            }

            SecurityActor actor = ReadActor(user);
            IRngService rng = Rng;

            if (profile.Mine > 0)
            {
                result = SecurityRules.ResolveMine(profile, actor, rng);
                Commit(obj, profile);
                LogResult(obj, result);

                if (result.Kind == SecurityAttemptKind.Jammed)
                {
                    // `fiascoRemine` — for `minetip == 6` that is `Interact.alarm()`, which raises the
                    // room alarm and starts the 240-tick budilo pulse (Interact.as:540-543, :1440-1450).
                    if (profile.MineTip == 6)
                    {
                        RaiseAlarm(room, obj.position, obj.objectId);
                    }
                }

                return result.Kind == SecurityAttemptKind.Opened
                    ? SecurityGateOutcome.Opened
                    : SecurityGateOutcome.Handled;
            }

            bool hasKey = HasKey(user, profile.LockKey);
            result = SecurityRules.Resolve(profile, actor, rng, hasPin: actor.Pins > 0, hasKey: hasKey);
            Commit(obj, profile);
            LogResult(obj, result);

            return result.Succeeded ? SecurityGateOutcome.Opened : SecurityGateOutcome.Handled;
        }

        /// <summary>
        /// The <c>hack</c> verb — what a <c>hack_lock</c> terminal broadcasts to every object in the
        /// room (<c>Interact.as:1588-1599</c>). Returns true when the object accepted it.
        /// </summary>
        public static bool TryHack(ObjectInstance obj, RoomInstance room)
        {
            SecurityProfile profile = ProfileFor(obj, room);
            if (profile == null || !profile.IsHackable) return false;

            bool hadSomething = profile.IsLocked || profile.Mine > 0;
            if (!hadSomething) return false;

            SecurityRules.ApplyHackCommand(profile);
            Commit(obj, profile);

            var entry = new SecurityLogEntry
            {
                Tick = Instance._tick,
                ObjectId = obj.objectId,
                MessageKey = "hack",
                Detail = "cleared by a hack_lock terminal",
                Kind = SecurityAttemptKind.Opened,
            };
            Push(entry);
            return true;
        }

        /// <summary>
        /// AS3 <c>allact == "hack_lock"</c> (<c>Interact.as:1588-1599</c>) — <c>command("hack")</c> on
        /// <b>every object in the room that has an Interact</b>. There is no <c>allid</c> and no range
        /// test: the sweep is the whole room, and only objects whose definition authors <c>hack</c>
        /// react (<c>:1699</c>).
        ///
        /// <para>This is the "alternate route" the design hangs on: a terminal opens the doors and
        /// safes the player could otherwise only pick, which is why a locked wing has two solutions and
        /// why the loud one is a terminal.</para>
        /// </summary>
        /// <returns>How many objects the sweep actually cleared.</returns>
        public static int HackRoom(RoomInstance room)
        {
            if (room == null || room.objects == null) return 0;

            int cleared = 0;
            for (int i = 0; i < room.objects.Count; i++)
            {
                ObjectInstance obj = room.objects[i];
                if (obj == null) continue;

                // `if(_loc2_.inter)` — an object with no Interact has no `command` to receive the verb.
                if (string.IsNullOrEmpty(obj.GetAttribute("inter", null)) &&
                    (obj.definition == null || string.IsNullOrEmpty(obj.definition.GetAttribute("inter", null))))
                {
                    continue;
                }

                if (TryHack(obj, room)) cleared++;
            }

            Debug.Log($"[Security] hack_lock swept room '{room.id}': {cleared} object(s) cleared.");
            return cleared;
        }

        /// <summary>
        /// AS3 <c>allact == "hack_robot"</c> (<c>Interact.as:1579-1588</c>) —
        /// <c>for each(unit) unit.hack(World.w.pers.security)</c>.
        ///
        /// <para><b>No range test and no faction filter in the oracle.</b> A robot terminal rewrites
        /// every unit in the room; only units that override <c>hack()</c> react, which in the shipped
        /// game means turrets. The port keeps that shape exactly — see
        /// <see cref="UnitController.Hack"/>.</para>
        /// </summary>
        /// <returns>How many units answered the verb.</returns>
        public static int HackUnitsInRoom(RoomInstance room, int securityLevel)
        {
            if (room == null) return 0;

            UnitController[] units = FindObjectsByType<UnitController>(FindObjectsSortMode.None);
            int hacked = 0;
            for (int i = 0; i < units.Length; i++)
            {
                UnitController unit = units[i];
                if (unit == null || !unit.IsAlive) continue;
                if (unit.Room != room) continue;

                unit.Hack(securityLevel);
                hacked++;
            }

            Debug.Log($"[Security] hack_robot swept room '{room.id}': {hacked} unit(s) answered " +
                      $"(security level {securityLevel}).");
            return hacked;
        }

        private static bool HasKey(GameObject user, string key)        {
            if (string.IsNullOrEmpty(key) || user == null) return false;

            PlayerInventory inventory = user.GetComponent<PlayerInventory>();
            if (inventory == null) inventory = user.GetComponentInParent<PlayerInventory>();
            return inventory != null && inventory.CountOf(key) > 0;
        }

        private static void LogResult(ObjectInstance obj, in SecurityAttemptResult result)
        {
            var entry = new SecurityLogEntry
            {
                Tick = Instance._tick,
                ObjectId = obj != null ? obj.objectId : "?",
                MessageKey = result.MessageKey,
                Kind = result.Kind,
                Delta = result.Delta,
                Chance = result.SuccessChance,
                Damage = result.Damage,
                Detail = Describe(result),
            };

            Push(entry);
        }

        /// <summary>
        /// A one-line, human-readable rendering of an attempt. <b>The port has no <c>gui.infoText</c>
        /// strip</b>, so this string is the player-facing feedback — which is exactly why the F2 tab
        /// exists rather than a silent log.
        /// </summary>
        public static string Describe(in SecurityAttemptResult result)
        {
            switch (result.Kind)
            {
                case SecurityAttemptKind.Opened:
                    return "opened";
                case SecurityAttemptKind.KeyUsed:
                    return "opened with a key";
                case SecurityAttemptKind.PinBroke:
                    return $"pin snapped ({result.Damage:0.#} damage, {result.LockHpAfter:0.#} left)";
                case SecurityAttemptKind.Failed:
                    return $"failed ({result.Damage:0.#} damage, {result.LockHpAfter:0.#} left)";
                case SecurityAttemptKind.FailedNoVisibleDamage:
                    return result.LockAttAfter > 0f
                        ? $"failed, {result.LockAttAfter:0} attempts left"
                        : $"failed, sturdy ({result.LockHpAfter:0.#} left)";
                case SecurityAttemptKind.Jammed:
                    return "SEIZED — the lock is ruined";
                case SecurityAttemptKind.LockedOut:
                    return "BLOCKED — no attempts left";
                case SecurityAttemptKind.NoSkill:
                    return "no lockpicking ability";
                case SecurityAttemptKind.NeedKey:
                    return "needs a key";
                default:
                    return "nothing to do";
            }
        }

        private static void Push(SecurityLogEntry entry)
        {
            List<SecurityLogEntry> log = Instance._log;
            log.Add(entry);
            if (log.Count > LogCapacity)
            {
                log.RemoveAt(0);
            }
        }

        // =====================================================================================
        // Alarms
        // =====================================================================================

        /// <summary>
        /// The alarm state for a room, created on first ask.
        ///
        /// <para><b>The spawn budget is seeded here, once, from the room's difficulty record</b> —
        /// AS3's <c>kolEnSpawn</c> is a per-location counter set when the location is built
        /// (<c>Location.as:504</c> <c>kolEnSpawn = nroom.options.@kolspawn</c>, <c>:1041</c> the
        /// difficulty draw) and <b>not</b> reset by <c>signal()</c> (<c>:2912-2920</c> touches only
        /// <c>t_alarm</c> and <c>t_alarmsp</c>). Seeding it in <c>Signal</c> instead would give every
        /// alarm a fresh garrison; seeding it here reproduces "the room can call for help as many times
        /// as its difficulty allows, then it is spent".</para>
        /// </summary>
        public static RoomAlarm GetAlarm(RoomInstance room)
        {
            if (room == null) return null;
            if (!Instance._alarms.TryGetValue(room, out RoomAlarm alarm))
            {
                alarm = new RoomAlarm
                {
                    SpawnBudget = room.difficulty != null ? room.difficulty.spawnEnemyCount : 0,
                };
                Instance._alarms[room] = alarm;
            }

            return alarm;
        }

        /// <summary>True while any room's alarm is running — the land-exit refusal.</summary>
        public static bool AnyAlarmActive
        {
            get
            {
                if (_instance == null) return false;
                foreach (KeyValuePair<RoomInstance, RoomAlarm> pair in _instance._alarms)
                {
                    if (pair.Value.IsActive) return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Raise a room's alarm — AS3 <c>World.w.loc.signal()</c>, reached from three places: the
        /// <c>alarm</c> script command (<c>Script.as:480-483</c>), a tripped signal mine
        /// (<c>Interact.as:1440-1450</c>) and a raider's own <c>alarm</c> script
        /// (<c>UnitRaider.as:800-806</c>).
        /// </summary>
        public static void RaiseAlarm(RoomInstance room, Vector2 origin, string source = null)
        {
            RoomAlarm alarm = GetAlarm(room);
            if (alarm == null) return;

            alarm.Signal(origin, RoomAlarm.DefaultSignalTicks, () => Rng.NextFloat());

            Push(new SecurityLogEntry
            {
                Tick = Instance._tick,
                ObjectId = source ?? room.id,
                MessageKey = "alarm",
                Detail = $"alarm raised in {room.id} for {RoomAlarm.DefaultSignalTicks} ticks",
                Kind = SecurityAttemptKind.Jammed,
            });

            Debug.Log($"[Security] Alarm raised in room '{room.id}'" +
                      (string.IsNullOrEmpty(source) ? string.Empty : $" by '{source}'") +
                      $" for {RoomAlarm.DefaultSignalTicks} ticks.");
        }

        /// <summary>Stop a room's alarm — the alarm panel's <c>allAct == "alarm"</c> branch
        /// (<c>Interact.as:1655-1659</c>).</summary>
        public static void ClearAlarm(RoomInstance room)
        {
            RoomAlarm alarm = GetAlarm(room);
            if (alarm == null || !alarm.IsActive) return;

            alarm.Clear();
            Push(new SecurityLogEntry
            {
                Tick = Instance._tick,
                ObjectId = room.id,
                MessageKey = "alarmOff",
                Detail = "alarm shut off",
                Kind = SecurityAttemptKind.Opened,
            });
        }

        /// <summary>
        /// Raise the alarm when an enemy <b>sees</b> the player — the stealth half of the alarm system.
        ///
        /// <para><b>Where this comes from in the oracle.</b> Not from <c>World.possiblyOut</c> and not
        /// from the suspicion meter: AS3 raises an alarm from a unit's own authored <c>alarm="…"</c>
        /// script, which <c>UnitRaider.as:800-806</c> starts the <i>first time</i> <c>findCel()</c>
        /// commits a target (<c>scrAlarmOn</c> latches it false). Those scripts contain
        /// <c>&lt;s act="alarm"/&gt;</c>, which is <c>World.w.loc.signal()</c>
        /// (<c>Script.as:480-483</c>). The port does not bind per-unit <c>alarm=</c> scripts, so the
        /// same moment is wired here instead: the first sighting by a hostile in a room whose alarm
        /// panel is still armed raises the room alarm.</para>
        ///
        /// <para><b>It is gated on an armed panel, and that gate is the design.</b> A room with no alarm
        /// panel cannot be alarmed by being seen — otherwise every corridor would punish the player for
        /// the one thing stealth cannot always avoid. The panel is the room's permission to call for
        /// help, and disabling it (defusing its signal mine) is a real stealth objective.</para>
        ///
        /// <para><b>Once per alarm, not once per sighting.</b> <c>Signal</c> re-rolls the first
        /// reinforcement's delay, so calling it every tick a guard can see you would keep pushing the
        /// spawn back and never deliver one. An already-active alarm is left alone.</para>
        /// </summary>
        public static void NotifySighting(UnitController observer, UnitController target)
        {
            if (observer == null || target == null) return;
            if (!target.IsPlayer) return;
            if (observer.IsPlayer) return;
            if (observer.Faction == PFE.Data.Definitions.FactionType.Player) return;

            RoomInstance room = observer.Room;
            if (room == null) return;

            RoomAlarm alarm = GetAlarm(room);
            if (alarm.IsActive) return;

            if (!RoomHasArmedAlarmPanel(room)) return;

            RaiseAlarm(room, (Vector2)observer.transform.position * 100f, "sighted");
        }

        /// <summary>
        /// Whether the room still has an alarm panel to call for help with — an object whose
        /// <c>allact</c> is <c>alarm</c> and which has not been opened/disarmed
        /// (<c>Interact.as:872-877</c> clears the <c>allact</c> when it opens).
        /// </summary>
        public static bool RoomHasArmedAlarmPanel(RoomInstance room)
        {
            if (room?.objects == null) return false;

            for (int i = 0; i < room.objects.Count; i++)
            {
                ObjectInstance obj = room.objects[i];
                if (obj == null) continue;

                string allact = obj.GetAllAct();
                if (!string.Equals(allact, "alarm", StringComparison.OrdinalIgnoreCase)) continue;

                if (obj.runtimeState != null && obj.runtimeState.isOpen) continue;
                return true;
            }

            return false;
        }

        // =====================================================================================
        // Tick
        // =====================================================================================

        private void Update()
        {
            // 30 Hz, the canonical tick — AS3 counts these timers per frame and the port's frame is a
            // sim tick (SimClock.CanonicalTicksPerSecond). Accumulated rather than FixedUpdate'd for the
            // reason PlayerActionInteractor documents: Unity's fixed step is not AS3's.
            _accumulator += Time.deltaTime * PFE.Core.SimClock.CanonicalTicksPerSecond;

            int steps = 0;
            while (_accumulator >= 1f && steps < MaxStepsPerFrame)
            {
                _accumulator -= 1f;
                steps++;
                StepOnce();
            }

            if (steps == MaxStepsPerFrame)
            {
                _accumulator = 0f;
            }
        }

        private void StepOnce()
        {
            _tick++;

            if (_alarms.Count == 0) return;

            _unitScratch.Clear();

            foreach (KeyValuePair<RoomInstance, RoomAlarm> pair in _alarms)
            {
                RoomInstance room = pair.Key;
                RoomAlarm alarm = pair.Value;

                RoomAlarm.TickResult tick = alarm.Tick(() => Rng.NextFloat());

                if (tick.Pulse)
                {
                    AlertUnitsInRoom(room, alarm.Origin);
                }

                if (tick.Spawn)
                {
                    SpawnReinforcement(room, alarm.Origin);
                }

                if (tick.Expired)
                {
                    Push(new SecurityLogEntry
                    {
                        Tick = _tick,
                        ObjectId = room != null ? room.id : "?",
                        MessageKey = "alarmOver",
                        Detail = "alarm expired",
                        Kind = SecurityAttemptKind.None,
                    });
                }
            }
        }

        /// <summary>
        /// AS3 <c>Location.budilo(x, y, 1500)</c> (<c>Location.as:2861-2885</c>) — alert every living
        /// unit within earshot of the alarm, with a randomised source offset so they do not all converge
        /// on one pixel.
        ///
        /// <para><b>Faction is filtered here, unlike the oracle.</b> AS3's location-wide <c>budilo</c>
        /// wakes every unit including the player's own allies (its per-unit sibling
        /// <c>Unit.budilo</c> filters by faction, this one does not). Waking the player's allies to the
        /// player's own position is a behaviour no player would read as intended, so hostiles only — a
        /// deliberate, documented deviation.</para>
        /// </summary>
        private void AlertUnitsInRoom(RoomInstance room, Vector2 originPixels)
        {
            if (room == null) return;

            UnitController[] units = FindObjectsByType<UnitController>(FindObjectsSortMode.None);
            for (int i = 0; i < units.Length; i++)
            {
                UnitController unit = units[i];
                if (unit == null || !unit.IsAlive) continue;
                if (unit.IsPlayer) continue;
                if (unit.Faction == PFE.Data.Definitions.FactionType.Player) continue;
                if (unit.Room != room) continue;

                Vector2 delta = (Vector2)unit.transform.position * 100f - originPixels;
                float ear = unit.Ear > 0f ? unit.Ear : 1f;
                if (delta.sqrMagnitude > RoomAlarm.BudiloRadius * RoomAlarm.BudiloRadius * ear * ear)
                {
                    continue;
                }

                // `alarma()` is a brain method — the controller is the wrong layer for it, and only
                // EnemyController owns a brain. (The folder is Enemies/Core; the namespace is
                // PFE.Entities.Enemies — the earlier PFE.Entities.Enemies.Core spelling was CS0234.)
                if (unit is PFE.Entities.Enemies.EnemyController enemy && enemy.Brain != null)
                {
                    float jitter = Mathf.Min(RoomAlarm.BudiloRadius * 0.5f, 400f);
                    enemy.Brain.Alarma(
                        originPixels.x + (Rng.NextFloat() - 0.5f) * jitter,
                        originPixels.y + (Rng.NextFloat() - 0.5f) * jitter);
                }
            }
        }

        /// <summary>
        /// AS3 <c>Location.enemySpawn</c> (<c>Location.as:2977-2998</c>) — one reinforcement at a
        /// <b>random</b> enemy spawn point, already alerted.
        ///
        /// <para><b>Two things are not ported, and both are visible rather than silent.</b>
        /// <list type="number">
        /// <item><b>The unit template.</b> AS3 spawns <c>this.tipSpawn</c> — the <i>land's</i> enemy
        /// type (<c>:2991</c>) — not anything the marker carries. The port has no land-level
        /// <c>tipSpawn</c>, so the marker's own <c>unitId</c> is used instead.</item>
        /// <item><b>The markers themselves.</b> <c>enspawn</c> is not imported:
        /// <c>AS3ToUnityConverter.FindSpawnPoints</c> only ever creates a <see cref="SpawnType.Player"/>
        /// point (from the room's <c>player</c> object), and <c>RoomPopulator</c>'s checkpoint marker is
        /// also a Player point. So in today's data this method finds no enemy marker, logs why, and
        /// returns — an alarm alerts and locks the exits but produces no reinforcements.</item>
        /// </list>
        /// The gap is logged at <c>Warning</c> on purpose: "the alarm raised and nothing came" is
        /// otherwise indistinguishable from "the alarm never raised".</para>
        ///
        /// <para><b>Goes through <see cref="RoomVisualController.SpawnUnitNow"/>.</b> That is the only
        /// path that appends the record <i>and</i> rebuilds the presenter, and it does the pixel
        /// conversion itself — writing <c>room.units</c> directly (as an earlier draft did) appends a
        /// record no spawner ever sees, which looks like a working alarm that produces nothing.</para>
        /// </summary>
        private void SpawnReinforcement(RoomInstance room, Vector2 originPixels)
        {
            if (room == null) return;

            SpawnPoint point = PickEnemySpawnPoint(room);
            if (point == null || string.IsNullOrEmpty(point.unitId))
            {
                Debug.LogWarning(
                    $"[Security] Alarm in '{room.id}' wanted a reinforcement, but the room has no " +
                    "enemy spawn marker with a unit id. `enspawn` is not imported " +
                    "(AS3ToUnityConverter.FindSpawnPoints emits Player points only), and AS3's own " +
                    "template is the land's `tipSpawn`, which the port does not carry. Skipped " +
                    "(Location.as:2977-2998).");
                return;
            }

            RoomVisualController controller = ResolveController(room);
            if (controller == null)
            {
                Debug.LogWarning(
                    $"[Security] Alarm in '{room.id}' wanted a reinforcement but no live " +
                    "RoomVisualController owns that room. Skipped.");
                return;
            }

            var instance = new UnitInstance { unitId = point.unitId };
            if (!controller.SpawnUnitNow(instance, point.GetUnityPosition(room)))
            {
                Debug.LogWarning(
                    $"[Security] Alarm reinforcement '{point.unitId}' could not be spawned in " +
                    $"'{room.id}' (SpawnUnitNow refused — no spawner or no physical parent).");
                return;
            }

            Debug.Log($"[Security] Alarm reinforcement '{point.unitId}' spawned in '{room.id}' " +
                      $"near ({originPixels.x:0},{originPixels.y:0}).");
        }

        /// <summary>
        /// A random enemy spawn marker that names a unit — AS3's
        /// <c>enspawn[floor(random() * enspawn.length)]</c> (<c>:2989</c>). Random rather than the first,
        /// because a room with two markers should not always reinforce from the same side.
        /// </summary>
        private static SpawnPoint PickEnemySpawnPoint(RoomInstance room)
        {
            if (room?.spawnPoints == null || room.spawnPoints.Count == 0) return null;

            var candidates = new List<SpawnPoint>(room.spawnPoints.Count);
            for (int i = 0; i < room.spawnPoints.Count; i++)
            {
                SpawnPoint point = room.spawnPoints[i];
                if (point == null || point.type != SpawnType.Enemy) continue;
                if (string.IsNullOrEmpty(point.unitId)) continue;
                candidates.Add(point);
            }

            if (candidates.Count == 0) return null;

            int index = Mathf.FloorToInt(Rng.NextFloat() * candidates.Count);
            if (index >= candidates.Count) index = candidates.Count - 1;
            return candidates[index];
        }

        /// <summary>
        /// The controller currently showing <paramref name="room"/>. <c>MapBridge.VisualController</c>
        /// first — it is the live one — then any controller whose <c>RoomInstance</c> matches, because a
        /// background room (the camp's <c>rbl</c> backdrop) is a controller too.
        /// </summary>
        private static RoomVisualController ResolveController(RoomInstance room)
        {
            var bridge = FindFirstObjectByType<MapBridge>();
            if (bridge != null && bridge.VisualController != null &&
                bridge.VisualController.RoomInstance == room)
            {
                return bridge.VisualController;
            }

            RoomVisualController[] controllers =
                FindObjectsByType<RoomVisualController>(FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++)
            {
                if (controllers[i] != null && controllers[i].RoomInstance == room)
                {
                    return controllers[i];
                }
            }

            return null;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
