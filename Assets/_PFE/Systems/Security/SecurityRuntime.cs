using System;
using System.Collections.Generic;
using PFE.Core;
using PFE.Core.Rng;
using PFE.Data.Definitions;
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

        /// <summary>The room the panel scan was last run for, and whether it had a panel — see
        /// <see cref="CheckAlarmPanelTrip"/>. Null means "rescan on the next tick".</summary>
        private RoomInstance _panelScanRoom;
        private bool _panelScanRoomHasPanel;

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
                SecurityProfile built = SecurityRules.Build(obj, BuildContext(room), Rng);
                StoreProfile(state, built);
            }

            SecurityProfile profile = LoadProfile(state);

            // Re-derive the authored `key` on every read. It is the one lock field that is NOT run
            // state: `lock` is randomised once and must be saved, but `key` is a constant in the room
            // asset and saving it bought nothing — while `StoreProfile` omitting it meant `LoadProfile`
            // handed back a profile that had forgotten which key it wanted. The visible result was a
            // door answering `noKey` while the player was carrying exactly that key.
            // See SecurityRules.ReadLockKey for the full account.
            profile.LockKey = SecurityRules.ReadLockKey(obj);

            return profile;
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

        /// <summary>
        /// The land-level difficulty scalars <see cref="SecurityRules.Build"/> rolls against.
        ///
        /// <para><b>These are the oracle's own numbers, not stand-ins.</b> <c>Land.setLocDif</c> writes
        /// <c>loc.locksLevel = locDifLevel * 0.7</c> and <c>loc.mechLevel = locDifLevel / 4</c>
        /// (<c>Land.as:982-987</c>), and the port stores exactly those two in
        /// <see cref="RoomDifficulty.lockLevel"/> / <see cref="RoomDifficulty.mechLevel"/>
        /// (<c>LocationDifficultyPlanner.cs:115-116</c>). Before this, <c>Build</c> never read them, so
        /// every lock was rolled at the authored value and the land's difficulty had no effect on
        /// lockpicking at all.</para>
        /// </summary>
        private static SecurityBuildContext BuildContext(RoomInstance room)
        {
            RoomDifficulty difficulty = room != null ? room.difficulty : null;

            return new SecurityBuildContext(
                proceduralLand: IsProceduralLand(room),
                locksLevel: difficulty != null ? difficulty.lockLevel : 0f,
                mechLevel: difficulty != null ? difficulty.mechLevel : 0f,
                locDifLevel: difficulty != null ? difficulty.baseDifficulty : 0f);
        }

        /// <summary>
        /// Snapshot the <b>resolved</b> half of a profile onto the object's runtime state.
        ///
        /// <para><b><c>LockKey</c> is deliberately absent.</b> It is authored data — a constant on the
        /// room asset — not something the constructor resolves, so it is re-derived by
        /// <see cref="SecurityRules.ReadLockKey"/> in <see cref="ProfileFor"/> instead of being stored.
        /// Adding it here would work, but it would also make a static room-asset value part of the save
        /// format and give it a second source of truth that could drift from the asset.</para>
        /// </summary>
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
            state.securityTrapDamage = p.TrapDamage;
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
                TrapDamage = state.securityTrapDamage,
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
                // Captured before the roll: `ResolveMine` clears `Mine` on every branch that ends the
                // press, and `DetonateTrap` needs the count to reconstruct a blast for a save written
                // before `securityTrapDamage` existed.
                int mineAtPress = profile.Mine;

                result = SecurityRules.ResolveMine(profile, actor, rng);
                Commit(obj, profile);
                LogResult(obj, result);

                if (result.Kind == SecurityAttemptKind.Jammed)
                {
                    // `fiascoRemine` — for `minetip == 6` that is `Interact.alarm()`, which raises the
                    // room alarm and starts the 240-tick budilo pulse (Interact.as:540-543, :1440-1450).
                    // Otherwise it is `Interact.explosion()` (:1413-1428): a real blast, not a fizzle.
                    if (profile.MineTip == 6)
                    {
                        RaiseAlarm(room, obj.position, obj.objectId);
                    }
                    else
                    {
                        DetonateTrap(obj, room, profile, mineAtPress);
                    }
                }

                // ── The mine press does not reach the lock ─────────────────────────────────────────
                //
                // AS3's `act()` is an if/else-if chain and the mine branch is `else if(this.mine > 0)`
                // (`:911-979`): it ends with `this.update(); this.unlock = 0; this.is_ready = false;`
                // and falls out of the function. `actOsn()` — the thing that opens a container or a
                // door — is called only under `if(this.at_once > 0)`.
                //
                // The port returned `Opened` on a successful defusal, and `Opened` falls through to
                // `actOsn()`. That is why a mined safe "opened after you broke the trap": one press
                // disarmed the mine AND opened the safe, skipping its lock entirely. Defusing is a
                // separate action from opening, and the second press is what meets the lock.
                //
                // The one exception is the oracle's own `at_once` fall-through, and it is narrow:
                // `actOsn()` runs after a DISASTER (:935-938) and after a SUCCESS (:969-972), but the
                // plain-failure branch (:942-953) has no `actOsn()` at all. So a failed defusal leaves
                // a `once` object shut. `alarm` is the only shipped row that is both mined and `once`,
                // and for it opening IS disabling — which is what defusing its signal trap is for.
                if (result.Kind == SecurityAttemptKind.Failed)
                {
                    return SecurityGateOutcome.Handled;
                }

                return SecurityRules.ReadAtOnce(obj)
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

        private static bool HasKey(GameObject user, string key)
        {
            if (string.IsNullOrEmpty(key) || user == null) return false;

            PlayerInventory inventory = user.GetComponent<PlayerInventory>();
            if (inventory == null) inventory = user.GetComponentInParent<PlayerInventory>();
            return inventory != null && inventory.CountOf(key) > 0;
        }

        /// <summary>
        /// A container trap going off — AS3 <c>Interact.explosion()</c> (<c>:1413-1428</c>), which is
        /// <c>new Bullet(...).iExpl(damage, destroy, explRadius)</c> at the object's own position.
        ///
        /// <para><b>What is ported and what is not.</b> The radius (260 px, <c>:66</c>) and the damage
        /// (<c>mine * 50 * (0.8 + random()*0.4) * (1 + locDifLevel*0.1)</c>, rolled once at
        /// construction — see <see cref="SecurityProfile.TrapDamage"/>) are the oracle's. What is not
        /// ported is the bullet's armour/falloff resolution: this applies
        /// <see cref="PFE.Systems.Combat.IDamageable.TakeDamage"/> directly, the same fallback
        /// <c>MineObject.Detonate</c> uses when no <c>DamageSystem</c> is in scope. That makes a trap
        /// blast slightly stronger against armour than the oracle's; it is a visible, recorded gap
        /// rather than a silent no-op, which is what "the trap went off and nothing happened" was.</para>
        ///
        /// <para><b>Blast before clearing, and the order matters.</b> AS3 clears the mine with
        /// <c>setAct("mine",101)</c> and then runs <c>fiascoRemine()</c> in the same branch, so the
        /// object is already disarmed when the blast lands. The port commits the profile before calling
        /// here (<see cref="TryWork"/>), which is the same order.</para>
        /// </summary>
        private static void DetonateTrap(ObjectInstance obj, RoomInstance room, SecurityProfile profile, int mineAtPress)
        {
            if (obj == null || profile == null) return;

            // A save written before `securityTrapDamage` existed carries 0. Reconstruct the oracle's
            // formula at its mean rather than detonating for nothing — the middle factor is the only
            // random one and its mean is 1.
            //
            // `mineAtPress` and not `profile.Mine`: `ResolveMine` has already cleared the mine by the
            // time this runs, so reading the profile here would make the guard below permanently false
            // and turn the fallback into dead code that reads as a working branch.
            float damage = profile.TrapDamage;
            if (damage <= 0f && mineAtPress > 0)
            {
                float locDifLevel = room != null && room.difficulty != null
                    ? room.difficulty.baseDifficulty
                    : 0f;
                damage = mineAtPress * 50f * (1f + locDifLevel * 0.1f);
            }

            RoomVisualController controller = ResolveController(room);
            if (controller == null)
            {
                Debug.LogWarning(
                    $"[Security] Trap on '{obj.objectId}' detonated, but no live RoomVisualController " +
                    "owns the room — no blast was applied.");
                return;
            }

            Vector3 centre = controller.RoomPixelToWorld(obj.position);
            float radius = SecurityRules.TrapBlastRadiusPixels * 0.01f;

            int hitCount = 0;
            Collider2D[] hits = Physics2D.OverlapCircleAll(centre, radius);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D hit = hits[i];
                if (hit == null || hit.isTrigger) continue;

                var damageable = hit.GetComponent<PFE.Systems.Combat.IDamageable>();
                if (damageable == null || !damageable.CanBeHit) continue;

                damageable.TakeDamage(damage);
                hitCount++;
            }

            Debug.Log($"[Security] Trap on '{obj.objectId}' detonated: {damage:0} damage over " +
                      $"{SecurityRules.TrapBlastRadiusPixels:0} px, {hitCount} target(s) hit.");
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

        /// <summary>
        /// A hostile walking over an armed alarm panel trips it — AS3's <c>Interact</c> constructor
        /// gives every <c>allact == "alarm"</c> object a trigger <b>area</b>
        /// (<c>:577-587</c>): <c>new Area(loc)</c>, <c>owner.copy(area)</c>, <c>area.tip = "raider"</c>,
        /// <c>area.over = this.alarm2</c>.
        ///
        /// <para><b>This is what "enemies can press the alarm button" means, and it is not a press.</b>
        /// The panel's own <c>allAct</c> branch is an <b>off</b> switch — <c>allact == "alarm"</c> runs
        /// <c>infoText("alarmOff"); setAct("open",1)</c> (<c>:1655-1659</c>), i.e. the player's use of
        /// the panel <i>disables</i> it. Nothing raises the alarm by pressing. What raises it is a unit
        /// entering the panel's footprint: <c>Area.step()</c> takes the rising edge
        /// (<c>active &amp;&amp; !preactive</c>) and calls <c>over()</c> → <c>alarm2()</c>
        /// (<c>:1452-1465</c>), which pulses <c>budilo</c> for 240 ticks, calls <c>loc.signal()</c> and
        /// then disarms the panel (<c>allact = ""</c>, <c>active = false</c>).</para>
        ///
        /// <para><b>The footprint is the panel's own box.</b> <c>owner.copy(area)</c> copies the
        /// panel's rect, and the <c>alarm</c> row is <c>size='1' wid='2'</c> — two tiles wide, one tall
        /// (<c>AllData</c>: <c>&lt;obj ed='4' id='alarm' inter='5' mine='1' minetip='6' allact='alarm'
        /// once='1' size='1' wid='2'/&gt;</c>).</para>
        ///
        /// <para><b>Recorded simplification: <c>tip</c> is not narrowed to raiders.</b> AS3 tests
        /// <c>unit.areaTestTip == area.tip</c>, and <c>"raider"</c> is set only by
        /// <c>UnitRaider.as:494</c> (<c>UnitBossNecr</c> sets <c>"en"</c>, which this panel does not
        /// accept). The port has no <c>areaTestTip</c> field, so the test here is "hostile", which also
        /// admits robots. That is a wider trigger than the oracle's; it is recorded rather than
        /// guessed at, because the narrowing needs a per-controller tag that does not exist yet.</para>
        /// </summary>
        private void CheckAlarmPanelTrip()
        {
            RoomVisualController controller = CurrentController();
            if (controller == null) return;

            RoomInstance room = controller.RoomInstance;
            if (room?.objects == null) return;

            // The object walk is memoised per room. A room only changes on a land transition, so the
            // common case — a room with no panel at all — costs one reference compare per tick
            // instead of a full `room.objects` scan plus a unit query.
            if (!ReferenceEquals(_panelScanRoom, room))
            {
                _panelScanRoom = room;
                _panelScanRoomHasPanel = RoomHasArmedAlarmPanel(room);
            }

            if (!_panelScanRoomHasPanel) return;

            // An alarm already running means the room has called for help; `alarm2` is a rising-edge
            // trigger and must not re-fire every tick.
            if (GetAlarm(room).IsActive) return;

            for (int i = 0; i < room.objects.Count; i++)
            {
                ObjectInstance panel = room.objects[i];
                if (panel == null) continue;
                if (!string.Equals(panel.GetAllAct(), "alarm", StringComparison.OrdinalIgnoreCase)) continue;
                if (panel.runtimeState != null && panel.runtimeState.isOpen) continue;

                UnitController intruder = FindHostileInPanelFootprint(room, panel, controller);
                if (intruder == null) continue;

                TripAlarmPanel(room, panel, intruder);

                // Force a rescan: this panel is now spent, and the flag must not keep the loop hot
                // for a room whose last panel has just been used.
                _panelScanRoom = null;
                return;
            }
        }

        /// <summary>
        /// AS3 <c>Interact.alarm2()</c> (<c>:1452-1465</c>) — raise the alarm, then spend the panel.
        /// </summary>
        private void TripAlarmPanel(RoomInstance room, ObjectInstance panel, UnitController intruder)
        {
            RaiseAlarm(room, panel.position, panel.objectId);

            // `this.area = null; this.active = false; this.allact = "";` — the panel is a one-shot.
            // The port's "spent" flag is the same one `RoomHasArmedAlarmPanel` and the object's own
            // open state already use, so the two cannot disagree.
            panel.EnsureStructuredData();
            panel.runtimeState.isOpen = true;

            Push(new SecurityLogEntry
            {
                Tick = _tick,
                ObjectId = panel.objectId,
                MessageKey = "alarmTripped",
                Detail = $"tripped by '{intruder.name}' — the panel is spent",
                Kind = SecurityAttemptKind.Jammed,
            });

            Debug.Log($"[Security] Alarm panel '{panel.objectId}' in '{room.id}' tripped by " +
                      $"'{intruder.name}' — alarm raised, panel spent.");
        }

        /// <summary>
        /// The first living hostile standing inside a panel's footprint, or null.
        ///
        /// <para>Distances are compared in world units (1 unit = 100 px), so the panel's 2×1-tile box is
        /// 0.8 × 0.4 half-extents and a unit contributes its own ~0.2 half-width — the same "does the
        /// unit's body overlap the area's rect" test <c>Area.areaTest</c> performs.</para>
        /// </summary>
        private UnitController FindHostileInPanelFootprint(
            RoomInstance room,
            ObjectInstance panel,
            RoomVisualController controller)
        {
            Vector3 centre = controller.RoomPixelToWorld(panel.position);

            const float halfWidth = 0.8f;   // 2 tiles of 40 px, in world units
            const float halfHeight = 0.4f;  // 1 tile
            const float unitHalfWidth = 0.2f;

            UnitController[] units = FindObjectsByType<UnitController>(FindObjectsSortMode.None);
            for (int i = 0; i < units.Length; i++)
            {
                UnitController unit = units[i];
                if (unit == null || !unit.IsAlive) continue;
                if (unit.IsPlayer) continue;
                if (unit.Faction == PFE.Data.Definitions.FactionType.Player) continue;
                if (unit.Room != room) continue;

                Vector3 delta = unit.transform.position - centre;
                if (Mathf.Abs(delta.x) > halfWidth + unitHalfWidth) continue;
                if (Mathf.Abs(delta.y) > halfHeight) continue;

                return unit;
            }

            return null;
        }

        /// <summary>The controller currently showing the live room, or null when nothing is loaded.</summary>
        private static RoomVisualController CurrentController()
        {
            var bridge = FindFirstObjectByType<MapBridge>();
            return bridge != null ? bridge.VisualController : null;
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

            // Before the alarm loop and before its early return: a panel trip is what *creates* an
            // alarm, so gating it behind `_alarms.Count == 0` would make it unreachable in the one
            // state that matters — a quiet room with an armed panel.
            CheckAlarmPanelTrip();

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
        /// <para><b>Where the points come from.</b> <c>RoomPopulator.RecordEnemySpawnPoints</c> turns
        /// the room's authored <c>enspawn</c> rows into <see cref="SpawnType.Enemy"/> entries on
        /// <c>room.spawnPoints</c>, carrying the spawn <i>group</i> (<c>enl2</c> and friends) rather than
        /// a unit. <c>ResolveReinforcementUnitId</c> then runs the oracle's own two rolls —
        /// <c>randomUnit(group)</c> then <c>randomCid(family)</c> — at spawn time, which is when AS3
        /// resolves them too. Before this the points did not exist at all, so an alarm raised, locked
        /// the exits and produced nothing.</para>
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
                    "enemy spawn marker with a group id. AS3 returns here too when `enspawn` is empty " +
                    "(Location.as:2979-2982) — a room that authors no alarm points calls for help and " +
                    "nobody comes. Skipped.");
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

            string unitId = ResolveReinforcementUnitId(point.unitId, room);
            if (string.IsNullOrEmpty(unitId))
            {
                Debug.LogWarning(
                    $"[Security] Alarm in '{room.id}': spawn group '{point.unitId}' resolved to no " +
                    "unit id. Skipped.");
                return;
            }

            if (!ResourcesUnitDefinitionProvider.Shared.TryGetUnit(unitId, out UnitDefinition definition) ||
                definition == null)
            {
                Debug.LogWarning(
                    $"[Security] Alarm in '{room.id}': no UnitDefinition for '{unitId}' — the " +
                    "reinforcement would have no stats and no art. Skipped (the alarm still " +
                    "alerts and locks the exits).");
                return;
            }

            var instance = new UnitInstance
            {
                entityId = PFE.Core.Ids.EntityId
                    .CreateForRoomSpawn(room.id, "reinforcement:" + unitId, _reinforcementSerial++)
                    .ToString(),
                unitId = unitId,
                unitType = unitId,
                isDead = false,
                maxHealth = definition.health,
                currentHealth = definition.health,
                // Empty, so RoomUnitSpawner falls through to the definition's controllerId — the same
                // precedence AS3's `createUnit(tipSpawn, …)` gets, since it passes no `cl`.
                controllerId = string.Empty,
                attributes = new List<MapObjectAttributeData>(),
                facingDirection = point.facingDirection != 0f
                    ? Mathf.RoundToInt(point.facingDirection)
                    : 1,
                // 0 = never buries: a reinforcement must be on screen the moment it arrives.
                digger = 0,
            };

            // `GetEnemyUnityPosition`, not `GetUnityPosition`: an `enspawn` marker's anchor uses
            // `addEnSpawn`'s own `size` (1), not the player's 2 — see SpawnPoint.EnemyAnchorSizeTiles.
            if (!controller.SpawnUnitNow(instance, point.GetEnemyUnityPosition(room)))
            {
                Debug.LogWarning(
                    $"[Security] Alarm reinforcement '{unitId}' could not be spawned in " +
                    $"'{room.id}' (SpawnUnitNow refused — no spawner or no physical parent).");
                return;
            }

            // `_loc5_.alarma()` — AS3 alerts the unit it just created, in the same call
            // (Location.as:2990-2997). Without this the reinforcement stands idle until the next
            // `budilo` pulse reaches it, which may never happen: the pulse radius is 1500 px around the
            // alarm's origin and an `enspawn` point can be further away than that.
            if (controller.TryGetSpawnedUnit(instance, out GameObject spawned) && spawned != null &&
                spawned.TryGetComponent(out PFE.Entities.Enemies.EnemyController enemy) &&
                enemy.Brain != null)
            {
                enemy.Brain.Alarma();
            }

            Debug.Log($"[Security] Alarm reinforcement '{unitId}' spawned in '{room.id}' " +
                      $"near ({originPixels.x:0},{originPixels.y:0}).");
        }

        /// <summary>Serial for reinforcement entity ids — a monotonic counter, so two reinforcements of
        /// the same family in one alarm never share an id even after one of them dies.</summary>
        private int _reinforcementSerial;

        /// <summary>
        /// Resolve the unit a spawn marker names into a concrete, spawnable id.
        ///
        /// <para><b>The marker carries a GROUP, not a unit.</b> AS3's <c>Location.enemySpawn</c> calls
        /// <c>createUnit(this.tipSpawn, …)</c> (<c>:2989</c>) where <c>tipSpawn</c> is one of
        /// <c>enl1</c>/<c>enl2</c>/<c>enf1</c>/<c>enc1</c>/<c>lov</c>, and <c>createUnit</c> then runs
        /// <c>randomUnit(group)</c> → <c>randomCid(family)</c> to pick a family and a variant. Handing
        /// the group id straight to the spawner is what the old port did, and it produced a unit
        /// literally called <c>enl2</c> with no definition at all — the "alarm spawns nothing"
        /// symptom.</para>
        ///
        /// <para><b>Draws from the security stream, not the room's.</b> AS3's spawn path uses
        /// <c>Math.random()</c>, which is unseeded and independent of the layout stream; using
        /// <see cref="Rng"/> keeps a reinforcement from shifting the seeded sequence that generated the
        /// room. The cost is that two reinforcements of one alarm are not reproducible from the land
        /// seed — which is exactly what AS3 does.</para>
        /// </summary>
        private static string ResolveReinforcementUnitId(string groupId, RoomInstance room)
        {
            if (string.IsNullOrEmpty(groupId)) return null;

            // An authored marker that already names a unit (a hand-placed `enl2` variant, or the F2
            // debug spawn) is used as-is: `randomUnit` has no case for it and would return "".
            if (!LocationEnemySelector.IsGroupId(groupId)) return groupId;

            LocationEnemyContext context = RoomPopulator.EnemyContextFor(room);

            string family = LocationEnemySelector.RandomUnit(groupId, context, water: false, Rng);
            if (string.IsNullOrEmpty(family)) family = groupId;

            return UnitVariantResolver.ResolveSpawnId(
                family, context.Difficulty, Rng, context.ToVariantContext());
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
