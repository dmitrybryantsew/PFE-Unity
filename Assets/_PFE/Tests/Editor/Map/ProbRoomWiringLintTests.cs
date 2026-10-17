using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Pins the <b>wiring</b> of the room-cleared event — the five call sites that reach
    /// <c>ProbRoomSessions</c> from gameplay.
    ///
    /// <para><b>The bug this exists to make unrepeatable.</b> <c>ProbationState</c> was landed and green
    /// for a while with <b>no construction site at all</b> — a whole subsystem unreachable with the wall
    /// green, which is precisely the shape this project has now hit three times (the prob adapter, the
    /// checkpoint return, the NPC flyer). The rules are covered behaviourally in
    /// <c>ProbRoomSessionsTests</c>; what that fixture cannot see is whether anything calls them.</para>
    ///
    /// <para><b>Why a source lint rather than a behavioural test.</b> Four of the five sites are
    /// <c>MonoBehaviour</c>s — <c>AreaTriggerSystem</c>, <c>RoomUnitSpawner</c>,
    /// <c>RoomVisualController</c> and <c>RoomTransitionManager</c> — and arranging them needs a
    /// <c>GameObject</c>, a live room registry and a <c>CampaignManager.Current</c>. Outside the editor
    /// the first of those throws <c>SecurityException: ECall methods must be packaged into a system
    /// module</c> before reaching an assertion, so a fixture that tried would never run here at all
    /// (rule: a test whose arrange cannot run proves nothing). What is left — and what this fixture
    /// asserts — is that each oracle entry point is actually consulted.</para>
    ///
    /// <para><b>Each assertion is one link.</b> AS3 reaches <c>prob.check()</c> from opening a box
    /// (<c>Interact.as:880</c>, <c>:1321</c>, <c>:1753</c>) and from a unit's <c>die()</c>
    /// (<c>Unit.as:4395-4402</c>, <c>:4499</c>), and <c>prob.over()</c> / <c>prob.out()</c> from
    /// <c>Location.reactivate</c> / <c>deactivate</c>. A site that reads the room and then never calls
    /// the session is indistinguishable from one that does neither, so every link is named — and the
    /// first assertion in each test is a positive control on the destination name, so a rename cannot
    /// turn a guard into a check of a dead name.</para>
    /// </summary>
    [TestFixture]
    public sealed class ProbRoomWiringLintTests
    {
        private const string SessionPath = "Systems/Map/Generation/ProbRoomSession.cs";
        private const string TriggerPath = "Systems/Map/Scripting/AreaTriggerSystem.cs";
        private const string SpawnerPath = "Systems/Map/Rendering/RoomUnitSpawner.cs";
        private const string VisualPath = "Systems/Map/Rendering/RoomVisualController.cs";
        private const string TransitionPath = "Systems/Map/Streaming/RoomTransitionManager.cs";

        /// <summary>
        /// The interaction path: a door / container / knob open reaches <c>prob.check()</c> through the
        /// verb host's <c>CheckProbRoom</c> slot.
        /// </summary>
        [Test]
        public void InteractionOpen_ReachesTheRoomClearedCheck()
        {
            string session = SourceLint.ReadStripped(SessionPath);
            string trigger = SourceLint.ReadStripped(TriggerPath);

            // ── Positive control on the destination. ─────────────────────────────────────────────
            Assert.That(session, Does.Contain("public ProbEffects Check(RoomInstance room)"),
                "ProbRoomSessions no longer exposes Check(RoomInstance). If it moved, move this guard " +
                "with it — otherwise the caller-side assertions below check a name that exists nowhere.");

            // ── Link 1: the dispatcher's slot is filled, not left null. ─────────────────────────
            // It was null before this wiring landed, alongside a null `probHost` — the two together
            // meant `Interact`'s three checks were dropped on the floor.
            Assert.That(trigger, Does.Contain("CheckProbRoom = CheckProbRoomFor"),
                "AreaTriggerSystem no longer fills ObjectVerbHost.CheckProbRoom. AS3 reaches " +
                "prob.check() from Interact.as:880 (looting a container), :1321 (a knob with action 0) " +
                "and :1753 (the `dam` verb's non-explosive arm); with the slot null all three are " +
                "silently dropped, which is the state this project shipped.");

            // ── Link 2: the slot's target actually consults the session. ────────────────────────
            string body = SourceLint.MethodBody(trigger, "private void CheckProbRoomFor(RoomInstance room)");

            Assert.That(body, Does.Contain("sessions.Check(room)"),
                "CheckProbRoomFor resolves a ProbRoomSessions and then never calls Check on it — the " +
                "read-then-ignore shape that is indistinguishable from not reading at all.");
        }

        /// <summary>
        /// The death path: a unit dying reaches <c>prob.check()</c>, and the wave counter with it, from
        /// the room's own spawner scan.
        /// </summary>
        [Test]
        public void UnitDeath_ReachesTheRoomClearedCheck_FromTheRoomRefresh()
        {
            string session = SourceLint.ReadStripped(SessionPath);
            string spawner = SourceLint.ReadStripped(SpawnerPath);
            string visual = SourceLint.ReadStripped(VisualPath);

            // ── Positive controls on the destinations. ─────────────────────────────────────────
            Assert.That(session, Does.Contain("public ProbEffects Check(RoomInstance room)"),
                "ProbRoomSessions no longer exposes Check(RoomInstance).");
            Assert.That(session, Does.Contain("public ProbEffects CheckWave(RoomInstance room, bool killedOne)"),
                "ProbRoomSessions no longer exposes CheckWave(RoomInstance, bool).");
            Assert.That(spawner, Does.Contain("public void CheckDeaths()"),
                "RoomUnitSpawner no longer exposes CheckDeaths().");

            // ── Link 1: AS3's order is checkWave THEN check (Unit.as:4497-4501). ────────────────
            string deaths = SourceLint.MethodBody(spawner, "public void CheckDeaths()");

            int wave = deaths.IndexOf("sessions.CheckWave(");
            int check = deaths.IndexOf("sessions.Check(");
            Assert.That(wave, Is.GreaterThanOrEqualTo(0),
                "CheckDeaths no longer reports a wave kill. AS3's die() is scrDie -> checkWave -> " +
                "check, and the wave counters are what gate the six wave-only probs.");
            Assert.That(check, Is.GreaterThan(wave),
                "CheckDeaths calls Check before CheckWave, or does not call it at all. The oracle's " +
                "order is checkWave first (Unit.as:4499) and check second (:4501).");

            // ── Link 2: the room's per-frame refresh drives the scan. ──────────────────────────
            // Per lesson #159: a call added to one driver's path and not the other is dead on the live
            // path. `UpdateVisuals` is the room's refresh, and it is the only caller this has.
            string update = SourceLint.MethodBody(visual, "public void UpdateVisuals()");
            Assert.That(update, Does.Contain("ReportProbRoomDeaths()"),
                "RoomVisualController.UpdateVisuals no longer reports prob-room deaths. The scan is " +
                "in that method precisely because it is already the per-frame room refresh — a second " +
                "driver for one call would be a second schedule to drift.");

            string report = SourceLint.MethodBody(visual, "private void ReportProbRoomDeaths()");
            Assert.That(report, Does.Contain("roomUnitSpawner.CheckDeaths()"),
                "ReportProbRoomDeaths hands the provider over and then never runs the scan.");

            // ── Link 3: the provider is a getter, not the object. ─────────────────────────────
            // `RoomTransitionManager.BuildProbSessions` replaces the registry wholesale on every land
            // build, so a captured reference would go stale on the second land and never notice.
            Assert.That(report, Does.Contain("ProbSessionsProvider"),
                "ReportProbRoomDeaths no longer sets ProbSessionsProvider, so CheckDeaths returns on " +
                "its null guard and no death is ever reported.");
            Assert.That(report, Does.Contain("RoomTransitionManager.Instance"),
                "ReportProbRoomDeaths no longer resolves RoomTransitionManager.Instance per call. A " +
                "captured registry would survive into the next land (lesson #178).");
        }

        /// <summary>
        /// The entry path: entering a prob room runs <c>prob.over()</c>, and leaving it runs
        /// <c>prob.out()</c> on the room the player is <b>leaving</b>.
        /// </summary>
        [Test]
        public void EnterAndLeave_ReachTheRoomClearedSession()
        {
            string session = SourceLint.ReadStripped(SessionPath);
            string transition = SourceLint.ReadStripped(TransitionPath);

            // ── Positive controls on the destinations. ─────────────────────────────────────────
            Assert.That(session, Does.Contain("public ProbEffects Enter(RoomInstance room, bool playerAboveTopThreshold)"),
                "ProbRoomSessions no longer exposes Enter(RoomInstance, bool).");
            Assert.That(session, Does.Contain("public ProbEffects Leave(RoomInstance room)"),
                "ProbRoomSessions no longer exposes Leave(RoomInstance).");
            Assert.That(transition, Does.Contain("public PFE.Systems.Map.Generation.ProbRoomSessions ProbSessions"),
                "RoomTransitionManager no longer exposes ProbSessions, so every caller-side " +
                "assertion in this fixture would be checking a name that exists nowhere.");

            // ── Link 1: the registry is built when the land's context is set. ───────────────────
            string build = SourceLint.MethodBody(transition, "private void BuildProbSessions()");
            Assert.That(build, Does.Contain("sessions.Register("),
                "BuildProbSessions builds an empty registry. AS3 creates a Probation for every prob " +
                "room it builds a Location for (Land.as:790-803); without the register calls the " +
                "registry answers null for every room and the whole event is unreachable again.");

            // ── Link 2: over() fires on entry. ────────────────────────────────────────────────
            string enter = SourceLint.MethodBody(transition, "public bool TryEnterProb(string probId, Vector3 doorWorldPosition)");
            Assert.That(enter, Does.Contain("_probSessions?.Enter("),
                "TryEnterProb no longer calls prob.over(). AS3's Location.reactivate ends with " +
                "if(this.prob) this.prob.over() and runs on EVERY entry, so a cleared room re-runs it " +
                "with both branches no-ops — dropping it also drops the enter banner and the broom " +
                "reset that stops a room sweeping itself.");

            // ── Link 3: out() fires on the room being LEFT, and before the swap. ──────────────
            string leave = SourceLint.MethodBody(transition, "public bool TryReturnFromProb()");
            Assert.That(leave, Does.Contain("_probSessions?.Leave("),
                "TryReturnFromProb no longer calls prob.out(). That is the branch that loots the " +
                "prizes of a CLEARED room (Probation.as:247-251) and resets the wave timer on an " +
                "uncleared one — without it a cleared room's prize stays locked for ever.");

            // `out()` refers to the loc the player is LEAVING, not the destination
            // (Location.as:2269), so the room must be read while the prob room is still the current
            // one and the call must land before the swap that changes it. Spending `_savedRoom` in
            // between is deliberate and is NOT the invariant — both statements run before the
            // coroutine either way, so ordering against the save proves nothing.
            int leavingRead = leave.IndexOf("RoomInstance leaving = landMap.currentRoom");
            int leaveCall = leave.IndexOf("_probSessions?.Leave(");
            int swap = leave.IndexOf("StartCoroutine(");
            Assert.That(leavingRead, Is.GreaterThanOrEqualTo(0),
                "TryReturnFromProb no longer reads landMap.currentRoom into `leaving`. Whatever it " +
                "passes to Leave is then some other room, and `out()` would loot the wrong room's prizes.");
            Assert.That(leaveCall, Is.GreaterThan(leavingRead),
                "out() now runs before `leaving` is read, so it cannot be observing the prob room the " +
                "player is standing in.");
            Assert.That(swap, Is.GreaterThan(leaveCall),
                "out() now runs after the transition coroutine starts. `out()` is the branch that " +
                "loots a cleared room's prizes, and by then currentRoom is already the destination.");
        }
    }
}
