using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Campaign;

// The wave metadata is DATA — `LandDefinition` owns the parsed `<wave t='…'>` children alongside the
// `<con>` children of the same `<prob>` — while this file owns the driver. Aliasing it here keeps both
// names referring to one type, so a caller cannot build a session out of waves that were never parsed.
using ProbWaveData = PFE.Data.Definitions.Campaign.ProbWaveData;

namespace PFE.Systems.Map.Generation
{
    /// <summary>
    /// "That prob room is cleared" — the payload the room-cleared event carries.
    ///
    /// <para>AS3's <c>closeProb</c> reports nothing: it writes
    /// <c>World.w.game.triggers["prob_" + id]</c> and prints a banner
    /// (<c>Probation.as:201-212</c>). The payload is the port's, so a view, a quest or a debug line can
    /// consume the clear without re-deriving it.</para>
    /// </summary>
    public sealed class ProbRoomCleared
    {
        /// <summary>AS3 <c>id</c> — the prob room's identity, and the name of its room in the `prob` land.</summary>
        public string ProbId = string.Empty;

        /// <summary>AS3 <c>nazv</c> — the banner's text. Supplied by the caller.</summary>
        public string DisplayName = string.Empty;

        /// <summary>AS3's <c>triggers["prob_" + id]</c> AFTER the increment (<c>Probation.as:201-208</c>).</summary>
        public int CompletionCount;
    }

    /// <summary>
    /// The run-state store the room-cleared event writes to — AS3 <c>World.w.game.triggers</c>, narrowed
    /// to the two members <c>closeProb</c> uses.
    ///
    /// <para><b>Why a narrow seam and not <see cref="ICampaignManager"/>.</b> The registry needs exactly
    /// one thing from the campaign: <c>triggers["prob_" + id]</c> read to decide "already cleared once"
    /// and written to record it. The full manager also carries the land, the catalogue and the quest
    /// runtime — none of which the registry touches — and those drag in <c>ScriptableObject</c> types
    /// that cannot be constructed in this project's offline harness, which would make the only
    /// testable half of the prob system untestable (lesson #120).</para>
    /// </summary>
    public interface IProbTriggerStore
    {
        TriggerValue GetTrigger(string triggerName);

        void SetTrigger(string triggerName, int value = 1);
    }

    /// <summary>
    /// The production <see cref="IProbTriggerStore"/>: AS3 <c>World.w.game.triggers</c>, reached through
    /// the campaign manager that already owns it.
    ///
    /// <para><b>Why an adapter and not a cast.</b> <see cref="ICampaignManager"/> already declares both
    /// members with exactly these signatures, but C# has no structural interface matching, so the
    /// manager is not an <see cref="IProbTriggerStore"/> until it says so. It cannot simply say so
    /// either: <c>ICampaignManager.cs</c> is compiled into <c>PFE.Core</c> as well as <c>PFE.View</c>,
    /// and <see cref="IProbTriggerStore"/> is <c>PFE.View</c>-only — declaring the inheritance would
    /// make <c>PFE.Core</c> depend on a type it cannot see.</para>
    /// </summary>
    public sealed class CampaignTriggerStore : IProbTriggerStore
    {
        private readonly ICampaignManager _campaign;

        public CampaignTriggerStore(ICampaignManager campaign)
        {
            _campaign = campaign;
        }

        public TriggerValue GetTrigger(string triggerName) =>
            _campaign == null ? TriggerValue.Unset : _campaign.GetTrigger(triggerName);

        public void SetTrigger(string triggerName, int value = 1)
        {
            if (_campaign != null) _campaign.SetTrigger(triggerName, value);
        }
    }

    /// <summary>
    /// One prob room and its state, kept together because the state outlives the room object.
    ///
    /// <para><b>Why a pairing rather than a field on <see cref="RoomInstance"/>.</b> AS3 keeps the state on
    /// the <c>Location</c> it builds for the prob (<c>Land.as:790-803</c>), which the port's
    /// <see cref="RoomInstance"/> cannot mirror: that type is compiled into <c>PFE.Core</c> as well as
    /// <c>PFE.View</c>, so it may not reference a <c>PFE.View</c>-only state machine. The pairing is the
    /// port's own answer to that, and it is keyed by <c>probId</c> — the identity AS3 keys
    /// <c>this.probs[nprob]</c> by.</para>
    /// </summary>
    public sealed class ProbRoomSession
    {
        /// <summary>AS3 <c>id</c> — also <see cref="RoomInstance.probId"/>, the two must agree.</summary>
        public readonly string ProbId;

        /// <summary>The detached room, at the origin. Never null while the session exists.</summary>
        public readonly RoomInstance Room;

        /// <summary>The state machine for this room — the port of <c>Probation.as</c>, pure.</summary>
        public readonly ProbationState State;

        /// <summary>The <c>&lt;wave&gt;</c> children in order. Empty for every shipped prob that has none.</summary>
        public readonly IReadOnlyList<ProbWaveData> Waves;

        public ProbRoomSession(RoomInstance room, ProbationState state, IReadOnlyList<ProbWaveData> waves)
        {
            Room = room;
            State = state;
            Waves = waves ?? new List<ProbWaveData>(0);
            ProbId = state != null ? state.id : (room != null ? room.probId : string.Empty);
        }

        /// <summary>Whether this session's room is the one given — the identity test every driver uses.</summary>
        public bool IsRoom(RoomInstance room) => room != null && ReferenceEquals(Room, room);
    }

    /// <summary>
    /// The driver for the room-cleared event: the adapter that owns one <see cref="ProbationState"/> per
    /// prob room, reads a live room into a <see cref="ProbRoomView"/>, applies the resulting
    /// <see cref="ProbEffects"/>, and raises <see cref="Cleared"/> when a room closes.
    ///
    /// <para><b>What was missing.</b> The state machine landed first and had no construction site — see
    /// <c>docs/GameInterface</c> and <c>LandDefinition.ProbRoomDefinition</c>, both of which name the
    /// adapter as the gap. So <c>prob.check()</c> (<c>Interact.as:880</c>, <c>:1321</c>,
    /// <c>:1753</c>) had nowhere to run, no prob room ever closed, and
    /// <c>triggers["prob_"+id]</c> stayed unwritten — which is also what
    /// <see cref="ProbDoorContext.ForLand"/> reads to keep a cleared room out of the next roll.</para>
    ///
    /// <para><b>Three of the oracle's entry points are wired and two are not, on purpose.</b></para>
    /// <list type="bullet">
    /// <item><description><c>check()</c> — wired, from <c>ObjectVerbHost.CheckProbRoom</c> on every door /
    /// container open (<c>Interact.as:865-868</c>, <c>:1747-1748</c>, <c>:1321</c>).</description></item>
    /// <item><description><c>over()</c> — wired, from <c>RoomTransitionManager.TryEnterProb</c>
    /// (<c>Location.reactivate</c> calls it on every entry).</description></item>
    /// <item><description><c>out()</c> — wired, from <c>RoomTransitionManager.TryReturnFromProb</c>.</description></item>
    /// <item><description><c>step()</c> — <b>not</b> wired. The wave timer decrements in frames and a
    /// wave spawns from <c>Location.waveSpawn</c>, which has no port; the six wave-only probs
    /// (<c>w_raiders</c>, <c>w_zombie</c>, <c>w_robots</c>, <c>w_grif</c>, <c>w_necros</c>,
    /// <c>w_encl</c>) therefore never advance <c>nwave</c> and never close. That is the honest state:
    /// their gate is <c>nwave &lt; maxwave</c>, which is true whenever the waves have not run.</description></item>
    /// <item><description>The four <c>&lt;scr&gt;</c> event scripts (alarm / in / out / close,
    /// <c>Probation.as:105-130</c>) — <b>not</b> parsed, so no close script runs.</description></item>
    /// </list>
    ///
    /// <para><b>The door-visibility effects are applied as a no-op and that is not a shortcut.</b>
    /// <c>doorsOnOff</c> hides and shows <c>doorout</c> boxes through <c>vis.visible</c> /
    /// <c>inter.active</c>, and the port's <see cref="Rendering.RoomObjectVisualManager"/> has no
    /// per-object visibility seam — it syncs presenters only when the visual definition or layer
    /// changes. So a sealed room is <i>not</i> sealed in the port, and a cleared room's doors are
    /// already open. Applying "show" is therefore behaviourally correct, and applying "hide" would
    /// need a seam that does not exist rather than a value that is wrong.</para>
    ///
    /// <para><b>The prize effects are applied for real.</b> <c>prepare</c>/<c>closeProb</c> use
    /// <c>setAct("lock", 0)</c> and <c>inter.command("unlock")</c>, which the port expresses as the
    /// same <c>lock</c>/<c>mine</c> fields <c>ObjectVerbCommand</c> writes when a door opens
    /// (<c>Interact.as:865-868</c>).</para>
    /// </summary>
    public sealed class ProbRoomSessions
    {
        private readonly Dictionary<string, ProbRoomSession> _sessions =
            new Dictionary<string, ProbRoomSession>(StringComparer.Ordinal);

        private readonly List<ProbRoomSession> _order = new List<ProbRoomSession>();

        /// <summary>
        /// Raised when a room closes — the room-cleared event. Fired once per room, from
        /// <see cref="Check"/>, after the state machine has marked it closed and the counter has been
        /// written, so a subscriber reading the counter sees the increment.
        /// </summary>
        public event Action<ProbRoomCleared> Cleared;

        /// <summary>
        /// Where the banner messages go. Set by the scene-side owner; the default is a
        /// <see cref="Debug.Log(string)"/> so an unwired registry still says something once.
        /// </summary>
        public Action<string> Notify { get; set; }

        /// <summary>The run-state store, for <c>prob_&lt;id&gt;</c>. Optional: a registry with no
        /// store still closes rooms and still raises <see cref="Cleared"/>, it just cannot persist the
        /// counter — which is exactly what an offline fixture needs.</summary>
        public IProbTriggerStore TriggerStore { get; set; }

        /// <summary>The sessions in registration order, so a diagnostic can name them.</summary>
        public IReadOnlyList<ProbRoomSession> All => _order;

        /// <summary>How many sessions exist.</summary>
        public int Count => _sessions.Count;

        /// <summary>Drops every session — a new land means a new AS3 <c>Land</c> object and new
        /// <c>Location</c>s, so the previous land's rooms must not answer.</summary>
        public void Clear()
        {
            _sessions.Clear();
            _order.Clear();
        }

        /// <summary>The session for a prob id, or null. A room that is not a prob is not an error.</summary>
        public ProbRoomSession Get(string probId)
        {
            if (string.IsNullOrEmpty(probId)) return null;
            return _sessions.TryGetValue(probId, out ProbRoomSession session) ? session : null;
        }

        /// <summary>The session for a room, or null. Matches by <c>probId</c> — the room instance itself
        /// is rebuilt on every land build, so identity-by-reference would not survive one.</summary>
        public ProbRoomSession For(RoomInstance room)
        {
            if (room == null || string.IsNullOrEmpty(room.probId)) return null;
            return Get(room.probId);
        }

        /// <summary>
        /// Creates (or reuses) the session for a prob room. <paramref name="definition"/> is the land's
        /// <c>&lt;prob&gt;</c> child, or null for a room that is not one — the land's <c>exit</c> room has
        /// no child, and AS3 builds its <c>Location</c> with no <c>xml</c> either.
        ///
        /// <para><paramref name="waves"/> overrides the definition's own list when a caller already has
        /// one; when it is null the definition is the source, because that is where AS3 reads
        /// <c>maxwave</c> from (<c>xml.wave.length()</c>, <c>Probation.as:127-130</c>). A caller that
        /// passed nothing must not silently get <c>maxwave == 0</c>.</para>
        /// </summary>
        public ProbRoomSession Register(RoomInstance room, ProbRoomDefinition definition,
                                       IReadOnlyList<ProbWaveData> waves = null)
        {
            if (room == null || string.IsNullOrEmpty(room.probId)) return null;

            ProbRoomSession existing = Get(room.probId);
            if (existing != null) return existing;

            ProbationState state = ProbationState.FromDefinition(definition, definition?.id);
            if (definitionsDisplayName(definition) != null)
            {
                state.nazv = definitionsDisplayName(definition);
            }

            IReadOnlyList<ProbWaveData> resolvedWaves = waves ?? definition?.waves;
            state.SetMaxWave(resolvedWaves?.Count ?? 0);

            var session = new ProbRoomSession(room, state, resolvedWaves);
            _sessions[room.probId] = session;
            _order.Add(session);
            return session;
        }

        /// <summary>
        /// <c>Res.txt("m", id)</c> has no port, so a session's display name is the id until a caller
        /// supplies one. Kept as a helper rather than inlined because this is the one place the
        /// localisation gap is a decision.
        /// </summary>
        static string definitionsDisplayName(ProbRoomDefinition definition)
        {
            return definition == null || string.IsNullOrEmpty(definition.id) ? null : definition.id;
        }

        // ── check / checkAllCon ───────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>prepare()</c> (<c>Probation.as:133-144</c>) — lock every prize container, once, when the
        /// room is built (<c>Land.as:764-766</c>). Not on entry: re-locking a prize the player has not
        /// taken yet is the oracle's own behaviour and re-running it would be harmless, but running it at
        /// the wrong time would relock one they have.
        /// </summary>
        public ProbEffects Prepare(RoomInstance room)
        {
            ProbRoomSession session = For(room);
            if (session == null) return null;

            ProbEffects effects = session.State.Prepare();
            Apply(session, effects);
            return effects;
        }

        /// <summary>
        /// AS3 <c>loc.prob.check()</c> (<c>Probation.as:146-156</c>) — the room-cleared decision.
        /// Returns the effects, and raises <see cref="Cleared"/> exactly once, on the closing call.
        /// </summary>
        public ProbEffects Check(RoomInstance room)
        {
            ProbRoomSession session = For(room);
            if (session == null) return null;

            ProbEffects effects = session.State.Check(BuildView(session));

            // The close is the one transition worth reacting to, and `closed` is the only signal — it
            // is one-way and nothing resets it (Probation.as:69). Comparing against the effects rather
            // than a latch keeps it once-per-room without a second flag to keep honest.
            if (session.State.closed && effects.ShowCloseMessage)
            {
                PersistCompletionCount(session);
                Apply(session, effects);
                Cleared?.Invoke(new ProbRoomCleared
                {
                    ProbId = session.ProbId,
                    DisplayName = session.State.nazv,
                    CompletionCount = session.State.CompletionCounter,
                });
            }

            return effects;
        }

        /// <summary>
        /// AS3 <c>closeProb</c>'s counter write (<c>Probation.as:201-208</c>):
        /// <c>triggers["prob_" + id] == null ? 1 : ++triggers["prob_" + id]</c>. The only run-state
        /// write the prob system makes, and the one <see cref="ProbDoorContext.ForLand"/> reads back to
        /// keep a cleared room out of the next roll.
        ///
        /// <para><b>Read from the store, not from <see cref="ProbationState.CompletionCounter"/>.</b>
        /// The oracle's counter <i>is</i> the store's value — a cleared prob from an earlier play
        /// session still has its counter, and the state machine starts at zero. So a second clear of a
        /// room the player has cleared before writes 2, not 1.</para>
        ///
        /// <para>A registry with no store still closes rooms and still raises
        /// <see cref="Cleared"/>; it just keeps the count in memory. That is the difference between
        /// "no campaign in this build" and "this prob was never cleared".</para>
        /// </summary>
        void PersistCompletionCount(ProbRoomSession session)
        {
            string key = ProbSelection.TriggerKey(session.ProbId);

            int next = TriggerStore == null
                ? session.State.CompletionCounter
                : TriggerStore.GetTrigger(key).AsInt(0) + 1;

            TriggerStore?.SetTrigger(key, next);
            session.State.CompletionCounter = next;
        }

        /// <summary>
        /// Builds the <see cref="ProbRoomView"/> from the live room — the oracle's own narrow reads
        /// (<c>uid</c>, <c>inter.open</c>, <c>inter.cont</c>, <c>sost</c>).
        /// </summary>
        public static ProbRoomView BuildView(ProbRoomSession session)
        {
            var view = new ProbRoomView();
            if (session?.Room == null) return view;

            RoomInstance room = session.Room;
            view.IsActive = room.isActive;

            if (room.objects != null)
            {
                for (int i = 0; i < room.objects.Count; i++)
                {
                    ObjectInstance obj = room.objects[i];
                    if (obj == null) continue;

                    obj.EnsureStructuredData();

                    if (obj.runtimeState == null) continue;

                    // AS3 `loc.objs` filtered to `id == "doorout"` (Probation.as:293).
                    if (string.Equals(obj.objectType, ProbationState.ReturnDoorObjectId,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        view.Doors.Add(new ProbDoorView
                        {
                            Uid = obj.uid ?? string.Empty,
                            // The port has no `vis.visible`; a present door is the "visible" case, and
                            // `isActive` is the only state the room carries that can read as absent.
                            IsVisible = obj.isActive,
                        });
                        continue;
                    }

                    // `obj.inter != null` (Probation.as:165) — a plain prop sharing a uid must not
                    // block the clear, so the container test is a predicate over the resolved `cont`,
                    // exactly as `RoomObjectVisualManager.UsesContainerPresenter` decides which
                    // presenter a prop gets.
                    string resolvedCont = ContainerRule.ResolveCont(
                        obj.GetAttribute("cont", null),
                        obj.definition != null ? obj.definition.containerId : null);

                    view.Boxes.Add(new ProbBoxView
                    {
                        Uid = obj.uid ?? string.Empty,
                        IsInteractable = ContainerRule.IsContainer(resolvedCont),
                        IsOpen = obj.runtimeState.isOpen,
                        // `inter.cont == "empty"` (Interact.as:840). The port writes the same event as
                        // `runtimeState.lootState` (ContainerPropPresenter.IsLooted), so this is the
                        // port's documented reading of the oracle's sentinel rather than a guess.
                        IsEmptied = obj.runtimeState.lootState > 0,
                        IsPrize = HasAttribute(obj, "prize"),
                    });
                }
            }

            if (room.units != null)
            {
                for (int i = 0; i < room.units.Count; i++)
                {
                    UnitInstance unit = room.units[i];
                    if (unit == null) continue;

                    view.Units.Add(new ProbUnitView
                    {
                        Uid = unit.uid ?? string.Empty,
                        // AS3 `Unit.as:637`: `questId = param3.@qid` — a *placement* attribute. `qid` is
                        // not one of the two names `ParseLegacyParameters` routes to its out-params (only
                        // `code` and `uid` are), so it survives in the attribute list exactly as authored
                        // and is reachable here. Hard-coding it empty would make every `qid` con inert:
                        // the con would match nothing and an empty-con room would close with the target
                        // still standing.
                        QuestId = unit.GetAttribute("qid", string.Empty) ?? string.Empty,
                        // AS3 `sost < 3` (Probation.as:179). The port's `IsDead` collapses 3 and 4, so
                        // this is the documented two-state reading.
                        IsStanding = !unit.IsDead,
                        IsWaveUnit = false,
                    });
                }
            }

            return view;
        }

        /// <summary>Presence test — AS3 reads every one of these as <c>xs.length()</c>, so a
        /// present-but-empty attribute still counts.</summary>
        static bool HasAttribute(ObjectInstance obj, string name)
        {
            string value = obj.GetAttribute(name, null);
            return value != null;
        }

        // ── over / out ────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>over()</c> (<c>Probation.as:228-244</c>) — the player entered the room. Fired by
        /// <c>RoomTransitionManager.TryEnterProb</c>, because AS3 calls it from
        /// <c>Location.reactivate</c> on every entry, not once.
        /// </summary>
        public ProbEffects Enter(RoomInstance room, bool playerAboveTopThreshold)
        {
            ProbRoomSession session = For(room);
            if (session == null) return null;

            ProbEffects effects = session.State.Over(BuildView(session), playerAboveTopThreshold);
            Apply(session, effects);
            return effects;
        }

        /// <summary>
        /// AS3 <c>out()</c> (<c>Probation.as:246-264</c>) — the player left the room.
        /// </summary>
        public ProbEffects Leave(RoomInstance room)
        {
            ProbRoomSession session = For(room);
            if (session == null) return null;

            ProbEffects effects = session.State.Out();
            Apply(session, effects);
            return effects;
        }

        /// <summary>
        /// AS3 <c>beginWave()</c> (<c>Probation.as:313-324</c>) — start the wave timer. Present because
        /// the rule is pure and tested; no production caller, because nothing spawns a wave.
        /// </summary>
        public ProbEffects BeginWave(RoomInstance room)
        {
            ProbRoomSession session = For(room);
            if (session == null) return null;

            ProbEffects effects = session.State.BeginWave(BuildView(session));
            Apply(session, effects);
            return effects;
        }

        /// <summary>
        /// AS3 <c>step()</c> (<c>Probation.as:382-399</c>) — the wave timer. No production caller: see
        /// the class summary on why the six wave-only probs stay unclosed.
        /// </summary>
        public ProbEffects Step(RoomInstance room)
        {
            ProbRoomSession session = For(room);
            if (session == null) return null;

            ProbWaveView next = WaveView(session, session.State.nwave);
            ProbEffects effects = session.State.Step(next);
            Apply(session, effects);
            return effects;
        }

        /// <summary>
        /// AS3 <c>checkWave(true)</c> (<c>Probation.as:348-366</c>) — one wave unit died. No production
        /// caller, for the same reason <see cref="Step"/> has none: <c>kolEn</c> is only ever filled by
        /// <c>createWave</c>, which needs a spawner.
        /// </summary>
        public ProbEffects CheckWave(RoomInstance room, bool killedOne)
        {
            ProbRoomSession session = For(room);
            if (session == null) return null;

            ProbEffects effects = session.State.CheckWave(killedOne);
            return effects;
        }

        static ProbWaveView WaveView(ProbRoomSession session, int index)
        {
            var wave = new ProbWaveView();
            if (session == null || session.Waves == null || index < 0 || index >= session.Waves.Count)
            {
                return wave;
            }

            ProbWaveData data = session.Waves[index];
            if (data == null) return wave;

            wave.Exists = true;
            wave.Size = data.spawnCount;
            wave.HasDelay = data.hasDelay;
            wave.DelaySeconds = data.delaySeconds;
            return wave;
        }

        // ── effect application ────────────────────────────────────────────────

        /// <summary>
        /// Applies the effects the port can express, and reports the rest. Deliberately small: the two
        /// banner flags and the prize lock/unlock are the commands with real ported state behind them.
        /// </summary>
        void Apply(ProbRoomSession session, ProbEffects effects)
        {
            if (effects == null || effects.IsEmpty) return;

            RoomInstance room = session?.Room;

            if (effects.ShowEnterMessage && Notify != null)
            {
                // AS3 `gui.messText("", nazv, gg.Y < 300)` (Probation.as:230). The top/bottom flag is the
                // port's own detail — the HUD has one banner.
                Notify(session.State.nazv);
            }

            if (effects.ShowCloseMessage)
            {
                Notify?.Invoke(session.State.nazv);

                // AS3 `Snd.ps("quest_ok")` — the port's audio has no one-shot query for that key, so the
                // sound is deferred rather than replaced by a click.
            }

            if (effects.CountdownMessage != ProbEffects.NoMessage)
            {
                // AS3 `gui.messText("", floor(t_wave / 30))` — no caller, see Step.
            }

            ApplyPrizes(room, effects);
        }

        /// <summary>
        /// <c>prepare</c> locks every prize and <c>closeProb</c>/<c>over</c> unlock or loot them
        /// (<c>Probation.as:137-141</c>, <c>:215-223</c>, <c>:249-251</c>).
        /// </summary>
        static void ApplyPrizes(RoomInstance room, ProbEffects effects)
        {
            if (room?.objects == null) return;
            if (!effects.LockAllPrizes && !effects.UnlockAllPrizes && !effects.LootAllPrizes) return;

            PrizeAction action = effects.LockAllPrizes ? PrizeAction.Lock
                : effects.UnlockAllPrizes ? PrizeAction.Unlock
                : PrizeAction.Loot;

            for (int i = 0; i < room.objects.Count; i++)
            {
                ObjectInstance obj = room.objects[i];
                if (obj == null || obj.runtimeState == null) continue;
                if (!HasAttribute(obj, "prize")) continue;

                obj.EnsureStructuredData();

                switch (action)
                {
                    case PrizeAction.Lock:
                        // AS3 `inter.setAct("lock", 0)` — lock with no level, i.e. pickable by hand. The
                        // port's lock level is the field `ObjectVerbCommand` reads.
                        obj.runtimeState.hasLockValue = true;
                        obj.runtimeState.lockValue = 0f;
                        break;

                    case PrizeAction.Unlock:
                        // AS3 `inter.command("unlock")`. The same three fields a door open clears
                        // (Interact.as:865-868), so the container behaves like an opened one.
                        obj.runtimeState.hasLockValue = false;
                        obj.runtimeState.lockValue = 0f;
                        obj.runtimeState.hasMineValue = false;
                        obj.runtimeState.mineValue = 0;
                        break;

                    case PrizeAction.Loot:
                        // AS3 `loc.openAllPrize()` — mark it looted so `cont == "empty"` and the prize
                        // box con stops holding the clear open.
                        obj.runtimeState.isOpen = true;
                        obj.runtimeState.lootState = 1;
                        break;
                }
            }
        }

        enum PrizeAction { Lock, Unlock, Loot }
    }
}
