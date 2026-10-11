using System.Collections.Generic;
using System.Globalization;
using PFE.Data.Definitions.Campaign;

namespace PFE.Systems.Map.Generation
{
    /// <summary>
    /// AS3 <c>Probation</c> (<c>Probation.as</c>, 402 lines) as a <b>pure state machine</b>: the scalar
    /// state of one prob room, plus one method per oracle entry point. It reads a
    /// <see cref="ProbRoomView"/> snapshot and returns a <see cref="ProbEffects"/> command list instead of
    /// touching the world.
    ///
    /// <para><b>Why snapshot-in / effects-out rather than a mutable room seam.</b> Every rule worth
    /// pinning here is a decision — "does this room count as cleared", "which doors are visible in which
    /// of the three modes", "when does the next wave spawn". The oracle's own reads are narrow
    /// (<c>uid</c>, <c>inter.open</c>, <c>inter.cont</c>, <c>sost</c>) and its writes are three commands
    /// (<c>vis.visible</c>, <c>inter.active</c>, <c>inter.shine()</c>). Splitting it that way means the
    /// whole runtime runs in the offline harness with no <c>GameObject</c> anywhere, which is the
    /// difference between 30-odd assertions and none — see lesson #120.</para>
    ///
    /// <para><b>What is deliberately not here.</b> The oracle's Unity-side edges: <c>Res.txt</c> string
    /// lookup, <c>gui.messText</c>/<c>infoText</c>, <c>Snd.ps</c>, the four <c>&lt;scr&gt;</c> event
    /// scripts, and <c>loc.waveSpawn</c>. Those become <see cref="ProbEffects"/> requests the adapter
    /// satisfies, so this class stays free of engine types.</para>
    ///
    /// <para><b>Two representation gaps that are real and must not be papered over.</b></para>
    /// <list type="number">
    /// <item><b><c>sost</c> is four states in the oracle and two in the port.</b> <c>Unit.sost</c> is
    /// 1 = alive, 2 = downed, 3 = dying, 4 = removed (<c>Unit.as:384</c>, <c>:1591</c>, <c>:671</c>), and
    /// <c>checkAllCon</c> tests <c>sost &lt; 3</c> (<c>Probation.as:179</c>) — a <i>downed</i> unit still
    /// blocks the clear. The port's <c>UnitInstance.isDead</c> collapses 3 and 4. So a downed-but-not-dead
    /// enemy satisfies the con here and would not in the oracle. The mapping is on
    /// <see cref="ProbUnitView.IsStanding"/> and is asserted, not assumed.</item>
    /// <item><b><c>inter.cont != "empty"</c> has no writer yet.</b> The oracle clears a container to
    /// <c>"empty"</c> when it is looted (<c>Interact.as:840</c>); the port's equivalent is
    /// <see cref="ProbBoxView.IsEmptied"/>, backed by <c>ObjectInstance.runtimeState.lootState</c> —
    /// and <b>nothing in the port writes <c>lootState</c> today</b>. A box con therefore always reads
    /// "not yet emptied" and never satisfies. That is the honest current state: the predicate is correct
    /// and its input is inert. See <c>TOPIC_open_work.md</c>.</item>
    /// </list>
    /// </summary>
    public sealed class ProbationState
    {
        // ── Authored (from ProbRoomDefinition) ──────────────────────────────────────────────

        /// <summary>AS3 <c>id</c>; also the room's name in the prob land.</summary>
        public string id = string.Empty;

        /// <summary>AS3 <c>nazv</c> — the display name (<c>Res.txt("m", id)</c>). Supplied by the caller.</summary>
        public string nazv = string.Empty;

        /// <summary>AS3 <c>prizeActive</c> — <c>xml.@prize</c> present (<c>Probation.as:89-92</c>).</summary>
        public bool prizeActive;

        /// <summary>AS3 <c>isClose</c> — <c>xml.@close</c> present (<c>Probation.as:97-100</c>).</summary>
        public bool isClose;

        /// <summary>AS3 <c>tip</c> — <c>xml.@tip</c>, 0 when absent (<c>Probation.as:93-96</c>).</summary>
        public int tip;

        /// <summary>AS3 <c>maxwave</c> — <c>xml.wave.length()</c> (<c>Probation.as:127-130</c>).</summary>
        public int maxwave;

        /// <summary>AS3 <c>&lt;con&gt;</c> children, i.e. what has to be cleared.</summary>
        public List<ProbContentData> contents = new List<ProbContentData>();

        // ── Runtime (AS3's own fields) ─────────────────────────────────────────────────────

        /// <summary>AS3 <c>closed</c> — the room has been cleared. One-way; nothing resets it.</summary>
        public bool closed;

        /// <summary>AS3 <c>active</c> — the room is sealed (doors hidden) and awaiting the clear.</summary>
        public bool active;

        /// <summary>AS3 <c>onWave</c> — the wave timer is running.</summary>
        public bool onWave;

        /// <summary>AS3 <c>nwave</c> — how many waves have been spawned.</summary>
        public int nwave;

        /// <summary>AS3 <c>t_wave</c> — the wave countdown, in frames.</summary>
        public int t_wave;

        /// <summary>AS3 <c>nspawn</c> — the spawn slot index within the current wave.</summary>
        public int nspawn;

        /// <summary>AS3 <c>kolEn</c> — how many enemies the current wave spawned.</summary>
        public int kolEn;

        /// <summary>AS3 <c>killEn</c> — how many of them are dead.</summary>
        public int killEn;

        /// <summary>
        /// The value <c>closeProb</c> last wrote to <c>triggers["prob_" + id]</c>, or 0 when it has not
        /// closed. The adapter owns the store; this is the value it must persist.
        /// </summary>
        public int CompletionCounter;

        // ── Constants (Probation.as:60-62, World.as:44) ────────────────────────────────────

        /// <summary>AS3 <c>beg_t</c> — the first wave's delay, in frames (<c>Probation.as:60</c>).</summary>
        public const int BegT = 90;

        /// <summary>AS3 <c>next_t</c> — the delay between waves (<c>Probation.as:62</c>).</summary>
        public const int NextT = 300;

        /// <summary>AS3 <c>World.fps</c> — the frame rate a wave's <c>t</c> attribute is scaled by.</summary>
        public const int Fps = 30;

        /// <summary>The <c>id</c> of a return door (<c>Probation.doorsOnOff</c>, <c>:293</c>).</summary>
        public const string ReturnDoorObjectId = "doorout";

        /// <summary>The <c>uid</c> of the <i>entry</i> return door — the one mode 0 leaves visible.</summary>
        public const string ReturnDoorBeginUid = "begin";

        /// <summary>
        /// Builds the state for a prob room from its parsed definition. <c>maxwave</c> is <b>not</b> set
        /// here — the port counts <c>&lt;wave&gt;</c> children separately from <c>&lt;con&gt;</c>, so the
        /// caller supplies it through <see cref="SetMaxWave"/>.
        /// </summary>
        public static ProbationState FromDefinition(ProbRoomDefinition prob, string displayName = null)
        {
            var state = new ProbationState();
            if (prob == null) return state;

            state.id = prob.id ?? string.Empty;
            state.nazv = string.IsNullOrEmpty(displayName) ? state.id : displayName;
            state.prizeActive = prob.prize;
            state.isClose = prob.close;
            state.tip = ParseTip(prob.tip);
            state.contents = prob.contents ?? new List<ProbContentData>();
            return state;
        }

        /// <summary>
        /// AS3 <c>xml.@tip</c> is read into an <c>int</c> field defaulting to 0 (<c>Probation.as:18</c>,
        /// <c>:93-96</c>), so a non-numeric tip reads as 0 rather than throwing.
        /// </summary>
        static int ParseTip(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;
        }

        /// <summary>
        /// AS3 <c>maxwave = xml.wave.length()</c> — set from the wave count, which the port counts
        /// separately from <c>&lt;con&gt;</c> because a <c>&lt;wave&gt;</c> child is not a
        /// <c>&lt;con&gt;</c>.
        /// </summary>
        public void SetMaxWave(int waveCount)
        {
            maxwave = waveCount < 0 ? 0 : waveCount;
        }

        // ── prepare (Probation.as:133-144) ─────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>prepare</c>: every <b>prize</b> container in the room starts locked
        /// (<c>inter.setAct("lock", 0)</c>). Called once, when the room is built.
        ///
        /// <para>Guarded on <see cref="prizeActive"/>: a room with no <c>prize</c> attribute has no prize
        /// to lock, and the oracle's <c>&amp;&amp;</c> tests the <i>prob's</i> flag, not the box's.</para>
        /// </summary>
        public ProbEffects Prepare()
        {
            var effects = new ProbEffects();
            if (prizeActive) effects.LockAllPrizes = true;
            return effects;
        }

        // ── check / checkAllCon / closeProb (Probation.as:146-226) ─────────────────────────

        /// <summary>
        /// AS3 <c>check</c> (<c>:146-156</c>): a no-op once closed, otherwise closes the room when every
        /// con is satisfied. Called by the interaction paths (<c>Interact.as:880</c>, <c>:1321</c>,
        /// <c>:1753</c>) — opening a box, killing a unit, looting a container.
        /// </summary>
        public ProbEffects Check(ProbRoomView room)
        {
            if (closed) return new ProbEffects();
            return CheckAllCon(room) ? CloseProb(room) : new ProbEffects();
        }

        /// <summary>
        /// AS3 <c>checkAllCon</c> (<c>:158-194</c>): whether every <c>&lt;con&gt;</c> is satisfied.
        ///
        /// <para><b>An empty con list is satisfied</b> — the loop returns <c>true</c> — so a prob with no
        /// <c>&lt;con&gt;</c> children closes the moment <c>check</c> first runs. That is the oracle's
        /// behaviour, not an oversight to correct.</para>
        ///
        /// <para><b>The branch order is an <c>if/else if</c> chain and the first branch is
        /// double-gated.</b> <c>tip == "box" || tip.length() == 0</c> <i>and</i> a non-empty <c>uid</c>;
        /// a <c>box</c> con with no <c>uid</c> therefore falls through the whole chain and is satisfied
        /// unconditionally. Six of the 97 cons in the data carry neither <c>uid</c> nor <c>qid</c>.</para>
        ///
        /// <para><b>An unrecognised <c>tip</c> is satisfied, not an error.</b> The oracle's chain has no
        /// else, so a con whose <c>tip</c> is neither box, unit nor wave contributes nothing. Reporting it
        /// would be inventing a rule; the data has none of them (65 box / 26 unit / 6 wave = 97).</para>
        /// </summary>
        public bool CheckAllCon(ProbRoomView room)
        {
            if (contents == null) return true;

            for (int i = 0; i < contents.Count; i++)
            {
                ProbContentData con = contents[i];
                if (con == null) continue;

                bool isBoxTip = con.tip == "box" || string.IsNullOrEmpty(con.tip);

                if (isBoxTip && !string.IsNullOrEmpty(con.uid))
                {
                    if (HasUnclearedBox(room, con.uid)) return false;
                }
                else if (con.tip == "unit")
                {
                    if (HasStandingUnit(room, con.uid, con.qid)) return false;
                }
                else if (con.tip == "wave")
                {
                    if (nwave < maxwave || killEn < kolEn) return false;
                }
            }

            return true;
        }

        /// <summary>
        /// AS3's box test (<c>:169</c>): a box with this <c>uid</c> that <i>has an interaction</i>, is
        /// <b>not open</b> and is <b>not emptied</b>.
        ///
        /// <para>The <c>inter</c> existence test is load-bearing and easy to drop: a plain prop that
        /// happens to share the uid is not a container and must not block the clear.</para>
        /// </summary>
        static bool HasUnclearedBox(ProbRoomView room, string uid)
        {
            if (room == null) return false;

            for (int i = 0; i < room.Boxes.Count; i++)
            {
                ProbBoxView box = room.Boxes[i];
                if (box == null || !box.IsInteractable) continue;
                if (box.Uid != uid) continue;
                if (!box.IsOpen && !box.IsEmptied) return true;
            }

            return false;
        }

        /// <summary>
        /// AS3's unit test (<c>:179</c>): a unit matching the con's <c>uid</c> <b>or</b> its <c>qid</c>,
        /// still standing (<c>sost &lt; 3</c>).
        ///
        /// <para>Note the <c>||</c>: the two ids are alternatives, and each is itself gated on being
        /// non-empty. A con carrying both matches a unit satisfying either.</para>
        /// </summary>
        static bool HasStandingUnit(ProbRoomView room, string uid, string qid)
        {
            if (room == null) return false;

            for (int i = 0; i < room.Units.Count; i++)
            {
                ProbUnitView unit = room.Units[i];
                if (unit == null || !unit.IsStanding) continue;

                bool uidMatch = !string.IsNullOrEmpty(uid) && unit.Uid == uid;
                bool qidMatch = !string.IsNullOrEmpty(qid) && unit.QuestId == qid;

                if (uidMatch || qidMatch) return true;
            }

            return false;
        }

        /// <summary>
        /// AS3 <c>closeProb</c> (<c>:196-226</c>): the room is cleared. Marks it closed, <b>increments</b>
        /// the <c>prob_&lt;id&gt;</c> counter, opens every return door, and — unless the room's own prize
        /// is still pending — unlocks every prize container in the room.
        ///
        /// <para><b>The counter increments rather than being set.</b> <c>triggers[key] == null ? 1 :
        /// ++triggers[key]</c> (<c>:201-208</c>). <c>ProbSelection.Eligible</c> only asks whether the key
        /// is present, so the magnitude is not read today — but writing 1 unconditionally would make the
        /// counter's own name a lie, and it is the field a future "how many times has this been cleared"
        /// would read.</para>
        ///
        /// <para><b>The prize gate is inverted relative to <c>prepare</c>.</b> <c>prepare</c> locks the
        /// prizes when <c>prizeActive</c>; <c>closeProb</c> unlocks them when <b>not</b>
        /// <c>prizeActive</c>. A room that declares a prize keeps it locked until the player takes it.</para>
        /// </summary>
        public ProbEffects CloseProb(ProbRoomView room)
        {
            var effects = new ProbEffects();

            closed = true;
            active = false;
            CompletionCounter = CompletionCounter == 0 ? 1 : CompletionCounter + 1;

            effects.ShowCloseMessage = true;
            effects.PlayCloseSound = true;

            effects.Merge(DoorsOnOff(room, ProbDoorMode.ShowAll));

            if (!prizeActive) effects.UnlockAllPrizes = true;

            return effects;
        }

        // ── activateProb / defaultProb / doorsOnOff (Probation.as:272-311) ─────────────────

        /// <summary>
        /// AS3 <c>activateProb</c> (<c>:272-280</c>): seal the room. Refused when the room is already
        /// closed, already active, or <b>not active in the world</b> — that last term is why this returns
        /// nothing for a room the player is not standing in.
        /// </summary>
        public ProbEffects ActivateProb(ProbRoomView room)
        {
            if (closed || active || room == null || !room.IsActive) return new ProbEffects();

            active = true;
            return DoorsOnOff(room, ProbDoorMode.HideAll);
        }

        /// <summary>
        /// AS3 <c>defaultProb</c> (<c>:282-286</c>): back to the resting state — only the entry return
        /// door visible. Unconditional; the caller decides when.
        /// </summary>
        public ProbEffects DefaultProb(ProbRoomView room)
        {
            active = false;
            return DoorsOnOff(room, ProbDoorMode.Default);
        }

        /// <summary>
        /// AS3 <c>doorsOnOff</c> (<c>:288-311</c>): the visibility rule for every <c>doorout</c> in the
        /// room, in the three modes.
        ///
        /// <para><b>The shine is a separate test from the visibility change and fires on the
        /// transition.</b> A door is shined when it is <i>about to</i> change visibility —
        /// <c>!visible &amp;&amp; mode == ShowAll</c> or <c>visible &amp;&amp; mode == HideAll</c>
        /// (<c>:295</c>). Mode <see cref="ProbDoorMode.Default"/> never shines.</para>
        /// </summary>
        public ProbEffects DoorsOnOff(ProbRoomView room, ProbDoorMode mode)
        {
            var effects = new ProbEffects();
            if (room == null) return effects;

            for (int i = 0; i < room.Doors.Count; i++)
            {
                ProbDoorView door = room.Doors[i];
                if (door == null) continue;

                int param = (int)mode;

                if ((!door.IsVisible && param == (int)ProbDoorMode.ShowAll) ||
                    (door.IsVisible && param == (int)ProbDoorMode.HideAll))
                {
                    effects.ShineDoorUids.Add(door.Uid);
                }

                // The oracle's two `if`s are mutually exclusive for every mode: -1 and 1 each satisfy
                // exactly one, and for 0 the `uid != "begin"` and `uid == "begin"` tests partition.
                if (DoorBecomesHidden(door.Uid, mode)) effects.HideDoorUids.Add(door.Uid);
                else effects.ShowDoorUids.Add(door.Uid);
            }

            return effects;
        }

        /// <summary>
        /// The visibility half of <c>doorsOnOff</c>, as a predicate — <c>param == -1 || (param == 0
        /// &amp;&amp; uid != "begin")</c> (<c>:299</c>).
        /// </summary>
        public static bool DoorBecomesHidden(string uid, ProbDoorMode mode)
        {
            if (mode == ProbDoorMode.HideAll) return true;
            if (mode == ProbDoorMode.Default) return uid != ReturnDoorBeginUid;
            return false;
        }

        // ── over / out (Probation.as:228-264) ──────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>over</c> (<c>:228-244</c>): the player entered the room.
        ///
        /// <para>Three independent effects, and the guards matter: the room is reset to the resting door
        /// state <b>only while uncleared</b>, and it is sealed <b>only when it declares
        /// <c>close</c></b>. So an uncleared, non-closing prob shows its exit and stays enterable; a
        /// closing one hides every door until it is cleared.</para>
        ///
        /// <para><b>Also on re-entry.</b> <c>Location.reactivate</c> calls this every time the room
        /// becomes active, so a cleared room re-runs it — and both branches are then no-ops
        /// (<c>closed</c> blocks <c>defaultProb</c>, and <c>activateProb</c> refuses on
        /// <c>closed</c>), which is what keeps a cleared room's doors open.</para>
        /// </summary>
        /// <param name="room">The room, for the seal's <c>loc.active</c> guard and its doors.</param>
        /// <param name="playerAboveTopThreshold">AS3 <c>gg.Y &lt; 300</c> — where the enter message shows.</param>
        public ProbEffects Over(ProbRoomView room, bool playerAboveTopThreshold)
        {
            var effects = new ProbEffects();

            effects.ShowEnterMessage = true;
            effects.EnterMessageAtTop = playerAboveTopThreshold;

            if (!closed) effects.Merge(DefaultProb(room));
            if (isClose) effects.Merge(ActivateProb(room));

            effects.SetBroom = true;
            effects.BroomValue = false;

            return effects;
        }

        /// <summary>
        /// AS3 <c>out</c> (<c>:246-264</c>): the player left the room.
        ///
        /// <para><b>Cleared and uncleared diverge completely.</b> A cleared room loots every prize
        /// container and marks itself swept (<c>broom = true</c>); an uncleared one resets the wave — its
        /// spawned enemies are disabled so the fight restarts on re-entry.</para>
        /// </summary>
        public ProbEffects Out()
        {
            var effects = new ProbEffects();

            if (closed)
            {
                effects.LootAllPrizes = true;
                effects.SetBroom = true;
                effects.BroomValue = true;
                return effects;
            }

            if (onWave) effects.Merge(ResetWave());
            return effects;
        }

        // ── waves (Probation.as:313-399) ───────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>beginWave</c> (<c>:313-324</c>): start the first wave. Idempotent — a second call while
        /// running is ignored. Hides every door and arms the <see cref="BegT"/> delay.
        /// </summary>
        public ProbEffects BeginWave(ProbRoomView room)
        {
            if (onWave) return new ProbEffects();

            var effects = DoorsOnOff(room, ProbDoorMode.HideAll);

            onWave = true;
            kolEn = 0;
            killEn = 0;
            nwave = 0;
            t_wave = BegT;

            return effects;
        }

        /// <summary>
        /// AS3 <c>createWave</c> (<c>:326-346</c>): spawn the wave at <see cref="nwave"/> and advance.
        ///
        /// <para><b>The count is reported, not performed.</b> The oracle calls
        /// <c>loc.waveSpawn(obj, nspawn)</c> per <c>&lt;obj&gt;</c> and increments <c>kolEn</c> and
        /// <c>nspawn</c> together, so <c>kolEn</c> is exactly the payload's size. The port asks the
        /// adapter to spawn <see cref="ProbEffects.SpawnWaveCount"/> enemies at
        /// <see cref="ProbEffects.SpawnWaveIndex"/> and keeps the bookkeeping here.</para>
        ///
        /// <para><b>A missing wave node and an empty one are different.</b> The oracle returns early
        /// <i>before</i> <c>++nwave</c> only when <c>xml.wave[nwave] == null</c> (<c>:331-334</c>); a
        /// present-but-empty wave still advances <c>nwave</c>. Collapsing the two would stall a prob on a
        /// wave whose payload is empty.</para>
        ///
        /// <para><b>The <c>t</c> attribute is a delay in <i>seconds</i>, scaled by
        /// <c>World.fps</c></b> (<c>:341-344</c>). A wave with no <c>t</c> leaves
        /// <see cref="t_wave"/> untouched — which is 1 when it was just fired by <see cref="Step"/>, and 0
        /// when reached through <see cref="CheckWave"/>. That is a real asymmetry in the oracle, not a bug
        /// to smooth over.</para>
        /// </summary>
        public ProbEffects CreateWave(ProbWaveView wave)
        {
            var effects = new ProbEffects();
            nspawn = 0;

            if (wave == null || !wave.Exists) return effects;

            if (wave.Size > 0)
            {
                effects.SpawnWaveIndex = nwave;
                effects.SpawnWaveCount = wave.Size;
                kolEn += wave.Size;
                nspawn = wave.Size;
            }

            if (wave.HasDelay) t_wave = wave.DelaySeconds * Fps;

            nwave++;
            return effects;
        }

        /// <summary>
        /// AS3 <c>checkWave</c> (<c>:348-366</c>): called when a wave unit dies. Once the wave is fully
        /// dead, arm the next wave's delay — or stop the timer when there are none left.
        ///
        /// <para><b>The oracle also calls <c>checkAllCon()</c> here and discards the result</b>
        /// (<c>:356</c>). That call is a no-op: <c>checkAllCon</c> is pure and its answer is thrown away,
        /// and the clear is driven by <c>check()</c> from the interaction paths instead. It is therefore
        /// deliberately not reproduced — a faithful-looking call to a pure function with no observable
        /// effect is exactly the kind of thing that makes a later reader believe a rule exists.</para>
        /// </summary>
        /// <param name="killedOne">AS3's <c>param1</c>: whether this call is itself a kill.</param>
        public ProbEffects CheckWave(bool killedOne)
        {
            var effects = new ProbEffects();

            if (killedOne) killEn++;

            if (killEn >= kolEn)
            {
                t_wave = nwave < maxwave ? NextT : 0;
            }

            return effects;
        }

        /// <summary>
        /// AS3 <c>resetWave</c> (<c>:368-380</c>): every unit the wave spawned is disabled and set to
        /// <c>sost = 4</c>, and the timer stops. Note <c>sost = 4</c>, <i>not</i> <c>3</c> — a reset wave
        /// enemy is removed, so it does not hold the clear open the way a dying one does.
        /// </summary>
        public ProbEffects ResetWave()
        {
            var effects = new ProbEffects();
            onWave = false;
            effects.DisableAllWaveUnits = true;
            return effects;
        }

        /// <summary>
        /// AS3 <c>step</c> (<c>:382-399</c>): the per-frame wave timer.
        ///
        /// <para><b>Decrement, then fire.</b> The order is the oracle's: <c>t_wave</c> is reduced first,
        /// and the spawn test is <c>t_wave == 1</c> <i>after</i> the reduction — so a countdown armed at
        /// 90 spawns on the 89th step, not the 90th. The same "counts down, then fires" shape as the
        /// authored <c>time</c> attribute (lesson #4).</para>
        ///
        /// <para><b>The countdown message is on <c>t_wave % 30 == 1</c></b> (<c>:394</c>), which is true
        /// at 1, 31, 61 … — once a second while a delay is running, and also on the fire tick itself. It
        /// prints <c>floor(t_wave / 30)</c>, so the tick that spawns prints 0.</para>
        /// </summary>
        /// <param name="nextWave">The wave at <see cref="nwave"/>, in case this step fires it.</param>
        public ProbEffects Step(ProbWaveView nextWave)
        {
            var effects = new ProbEffects();
            if (!onWave) return effects;

            if (t_wave > 0) t_wave--;

            if (t_wave == 1 && nwave < maxwave)
            {
                effects.Merge(CreateWave(nextWave));
            }

            if (t_wave % Fps == 1) effects.CountdownMessage = t_wave / Fps;

            return effects;
        }
    }

    /// <summary>The three <c>doorsOnOff</c> modes — AS3's <c>param1</c> (<c>Probation.as:288</c>).</summary>
    public enum ProbDoorMode
    {
        /// <summary><c>-1</c>: hide and disable every return door. Used to seal a room.</summary>
        HideAll = -1,

        /// <summary><c>0</c>: hide all but the entry door (<c>uid == "begin"</c>). The resting state.</summary>
        Default = 0,

        /// <summary><c>1</c>: show and enable every return door. Used when the room is cleared.</summary>
        ShowAll = 1,
    }

    /// <summary>One <c>doorout</c> in the room, as <c>doorsOnOff</c> sees it.</summary>
    public sealed class ProbDoorView
    {
        /// <summary>AS3 <c>uid</c> — <c>"begin"</c> for the entry door.</summary>
        public string Uid = string.Empty;

        /// <summary>AS3 <c>vis.visible</c>, read for the shine test.</summary>
        public bool IsVisible;
    }

    /// <summary>One interactive prop in the room, as <c>checkAllCon</c> sees it.</summary>
    public sealed class ProbBoxView
    {
        /// <summary>AS3 <c>uid</c>.</summary>
        public string Uid = string.Empty;

        /// <summary>AS3 <c>obj.inter != null</c> — the prop is interactive at all.</summary>
        public bool IsInteractable;

        /// <summary>AS3 <c>inter.open</c>.</summary>
        public bool IsOpen;

        /// <summary>AS3 <c>inter.cont == "empty"</c>, inverted. See the class doc's second gap.</summary>
        public bool IsEmptied;

        /// <summary>AS3 <c>inter.prize</c>.</summary>
        public bool IsPrize;
    }

    /// <summary>One unit in the room, as <c>checkAllCon</c> sees it.</summary>
    public sealed class ProbUnitView
    {
        /// <summary>AS3 <c>uid</c>.</summary>
        public string Uid = string.Empty;

        /// <summary>AS3 <c>questId</c>.</summary>
        public string QuestId = string.Empty;

        /// <summary>
        /// AS3 <c>sost &lt; 3</c> — still alive <i>or merely downed</i>. See the class doc's first gap for
        /// why this is not simply "not dead".
        /// </summary>
        public bool IsStanding = true;

        /// <summary>AS3 <c>Unit.wave</c> — spawned by a prob wave, so <c>resetWave</c> may disable it.</summary>
        public bool IsWaveUnit;
    }

    /// <summary>One <c>&lt;wave&gt;</c> child, as <c>createWave</c> sees it.</summary>
    public sealed class ProbWaveView
    {
        /// <summary>AS3 <c>xml.wave[nwave] != null</c>.</summary>
        public bool Exists;

        /// <summary>The number of <c>&lt;obj&gt;</c> children — what <c>kolEn</c> gains.</summary>
        public int Size;

        /// <summary>AS3 <c>@t.length()</c>.</summary>
        public bool HasDelay;

        /// <summary>The <c>t</c> value, in seconds, when <see cref="HasDelay"/>.</summary>
        public int DelaySeconds;
    }

    /// <summary>A snapshot of the prob room, in the terms <see cref="ProbationState"/> reads.</summary>
    public sealed class ProbRoomView
    {
        /// <summary>AS3 <c>loc.active</c>.</summary>
        public bool IsActive;

        /// <summary>AS3 <c>loc.objs</c>.</summary>
        public List<ProbBoxView> Boxes = new List<ProbBoxView>();

        /// <summary>AS3 <c>loc.units</c>.</summary>
        public List<ProbUnitView> Units = new List<ProbUnitView>();

        /// <summary>AS3 <c>loc.objs</c> filtered to <c>id == "doorout"</c>.</summary>
        public List<ProbDoorView> Doors = new List<ProbDoorView>();
    }

    /// <summary>
    /// The commands one <see cref="ProbationState"/> call produces. The adapter applies them to the real
    /// room; nothing here touches an engine type.
    /// </summary>
    public sealed class ProbEffects
    {
        /// <summary>AS3 <c>inter.shine()</c> on these door uids.</summary>
        public readonly List<string> ShineDoorUids = new List<string>();

        /// <summary>Doors to make visible and interactive (<c>vis.visible = true</c>, <c>inter.active = true</c>).</summary>
        public readonly List<string> ShowDoorUids = new List<string>();

        /// <summary>Doors to hide and disable (<c>vis.visible = false</c>, <c>inter.active = false</c>).</summary>
        public readonly List<string> HideDoorUids = new List<string>();

        /// <summary>AS3 <c>prepare</c>: lock every prize container — <c>setAct("lock", 0)</c>.</summary>
        public bool LockAllPrizes;

        /// <summary>AS3 <c>inter.command("unlock")</c> on every prize container in the room.</summary>
        public bool UnlockAllPrizes;

        /// <summary>AS3 <c>loc.openAllPrize()</c> — loot every prize container.</summary>
        public bool LootAllPrizes;

        /// <summary>AS3 <c>resetWave</c>: disable every wave unit (<c>sost = 4</c>).</summary>
        public bool DisableAllWaveUnits;

        /// <summary>AS3 <c>loc.broom</c>, when <see cref="SetBroom"/> is true.</summary>
        public bool BroomValue;

        /// <summary>Whether this call writes <c>loc.broom</c> at all.</summary>
        public bool SetBroom;

        /// <summary>AS3 <c>gui.infoText("closeProb", nazv)</c>.</summary>
        public bool ShowCloseMessage;

        /// <summary>AS3 <c>Snd.ps("quest_ok")</c>.</summary>
        public bool PlayCloseSound;

        /// <summary>AS3 <c>gui.messText("", nazv, …)</c> on room entry.</summary>
        public bool ShowEnterMessage;

        /// <summary>The third argument of that <c>messText</c>: <c>gg.Y &lt; 300</c>.</summary>
        public bool EnterMessageAtTop;

        /// <summary>AS3 <c>gui.messText("", floor(t_wave / 30))</c> — the wave countdown.</summary>
        public int CountdownMessage = NoMessage;

        /// <summary><see cref="CountdownMessage"/>'s "nothing to show" value.</summary>
        public const int NoMessage = int.MinValue;

        /// <summary>The wave to spawn, or -1 when this call does not spawn one.</summary>
        public int SpawnWaveIndex = -1;

        /// <summary>How many enemies that wave holds.</summary>
        public int SpawnWaveCount;

        /// <summary>Appends every command in <paramref name="other"/> to this one.</summary>
        public void Merge(ProbEffects other)
        {
            if (other == null) return;

            ShineDoorUids.AddRange(other.ShineDoorUids);
            ShowDoorUids.AddRange(other.ShowDoorUids);
            HideDoorUids.AddRange(other.HideDoorUids);

            LockAllPrizes |= other.LockAllPrizes;
            UnlockAllPrizes |= other.UnlockAllPrizes;
            LootAllPrizes |= other.LootAllPrizes;
            DisableAllWaveUnits |= other.DisableAllWaveUnits;
            ShowCloseMessage |= other.ShowCloseMessage;
            PlayCloseSound |= other.PlayCloseSound;
            ShowEnterMessage |= other.ShowEnterMessage;
            EnterMessageAtTop |= other.EnterMessageAtTop;

            if (other.SetBroom)
            {
                SetBroom = true;
                BroomValue = other.BroomValue;
            }

            if (other.CountdownMessage != NoMessage) CountdownMessage = other.CountdownMessage;
            if (other.SpawnWaveIndex >= 0)
            {
                SpawnWaveIndex = other.SpawnWaveIndex;
                SpawnWaveCount = other.SpawnWaveCount;
            }
        }

        /// <summary>Whether this call asks for nothing at all.</summary>
        public bool IsEmpty =>
            ShineDoorUids.Count == 0 && ShowDoorUids.Count == 0 && HideDoorUids.Count == 0 &&
            !LockAllPrizes && !UnlockAllPrizes && !LootAllPrizes && !DisableAllWaveUnits &&
            !SetBroom && !ShowCloseMessage && !PlayCloseSound && !ShowEnterMessage &&
            CountdownMessage == NoMessage && SpawnWaveIndex < 0;
    }
}
