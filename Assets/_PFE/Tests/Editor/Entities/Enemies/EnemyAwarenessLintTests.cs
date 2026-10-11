using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Entities.Enemies
{
    /// <summary>
    /// Pins the wiring of AS3 <c>aiSpok</c> — the budget that keeps a unit hunting a target it can no
    /// longer see (<c>UnitZombie.as:621-676</c>).
    ///
    /// <para><b>The bug this exists to make unrepeatable.</b> <c>TickCombatChase</c> exited on
    /// <c>TargetUnit == null</c>, and <c>EnemySensors.Evaluate</c> nulls <c>TargetUnit</c> on the first
    /// tick the target is not visible — so the guard fired immediately, the <c>TimeSinceTargetSpottedTicks
    /// &gt; 90</c> line beneath it was <b>unreachable</b>, and the unit abandoned the chase on the first
    /// obscured tick. The owner's report: <i>"it feels zombie losing me too quickly after I stand directly
    /// above it and can find me only if I go to its LOS again or make sound."</i> The numbers were never
    /// the problem — the port had no <c>aiSpok</c> at all — so
    /// <c>EnemyAwarenessMathTests</c> pins the ladder's arithmetic and this fixture pins the two call
    /// sites that make it live.</para>
    ///
    /// <para><b>Why a source lint.</b> The behaviour needs a spawned zombie, a room and a target, and this
    /// project's offline harness cannot construct a <c>GameObject</c> — <c>EnemyBrainTests</c> has never
    /// run outside the editor for that reason. A lint is the only form of this guard that executes here.
    /// See <see cref="SourceLint"/> for the reader and stripper, and for why stripping is not optional: a
    /// lint that matched raw text would pass on a file whose call had been deleted but whose comment still
    /// named it, and these methods carry exactly such comments.</para>
    /// </summary>
    [TestFixture]
    public sealed class EnemyAwarenessLintTests
    {
        private const string SensorsPath = "Entities/Enemies/Core/EnemySensors.cs";
        private const string BrainPath = "Entities/Enemies/Core/EnemyBrain.cs";
        private const string ZombiePath = "Entities/Enemies/Archetypes/ZombieBrain.cs";

        /// <summary>
        /// An expression-bodied member's text — from <paramref name="marker"/> through its terminating
        /// <c>;</c>.
        ///
        /// <para>Exists because <see cref="SourceLint.MethodBody"/> needs a <c>{</c> to brace-match, and
        /// the shared budget helper is a <b>property</b> (<c>ChaseBudgetExhausted =&gt; …</c>). Without
        /// this, "the chase reads the budget" could only be asserted against the inline spelling — which
        /// is exactly the assumption that made this fixture go stale when the helper was extracted.</para>
        /// </summary>
        private static string ExpressionMember(string source, string marker)
        {
            int at = source.IndexOf(marker + " =>", System.StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThanOrEqualTo(0),
                "Lint target member not found: `" + marker + " =>`. A rename must update this fixture " +
                "rather than quietly drop the member from coverage.");

            int end = source.IndexOf(';', at);
            Assert.That(end, Is.GreaterThan(at), "No `;` terminating `" + marker + " =>`");

            return source.Substring(at, end - at + 1);
        }

        /// <summary>
        /// The two halves of the fix, asserted together: the budget is <i>armed</i> by a sighting, and the
        /// chase <i>reads</i> it instead of the sighting. Either half alone leaves the bug in place —
        /// arming without reading changes nothing, and reading without arming means the budget is always
        /// zero and the unit drops out on the first obscured tick exactly as before.
        /// </summary>
        [Test]
        public void ASighting_ArmsTheBudget_AndTheChase_ReadsItRatherThanTheSighting()
        {
            string sensors = SourceLint.ReadStripped(SensorsPath);
            string brain = SourceLint.ReadStripped(BrainPath);

            // ── Half one: the arming, on the sighting commit. ─────────────────────────────────────
            string evaluate = SourceLint.MethodBody(sensors, "public void Evaluate(");

            // Scope to the SIGHTING branch. `Evaluate` holds BOTH arming sites — this one and the
            // sound-only one below — so an assertion on the whole body is satisfied by either, and
            // deleting the sighting arming would leave it green. The anchors are the two branch
            // conditions themselves: `seenCandidate.IsObserved` opens the sighting commit, and
            // `CommitsImmediately(` occurs only inside the sound commit's condition, so the span between
            // them is exactly the sighting branch.
            int seenAt = evaluate.IndexOf("IsObserved", System.StringComparison.Ordinal);
            int soundAt = evaluate.IndexOf("CommitsImmediately(", System.StringComparison.Ordinal);

            Assert.That(seenAt, Is.GreaterThan(0),
                "precondition: Evaluate no longer has the `obs >= maxObs` sighting gate. AS3 arms `aiSpok` " +
                "inside `if(findCel())` — only once the unit has decided it can see the target, not on " +
                "every tick the ray happens to pass — so the gate is part of what is being pinned.");

            Assert.That(soundAt, Is.GreaterThan(seenAt),
                "precondition: the sighting and sound commits are no longer distinguishable in this order, " +
                "so the scoping below cannot isolate the sighting arming. Rewrite the anchors rather than " +
                "dropping the scoping: an unscoped assertion here is satisfied by the sound arming and " +
                "therefore survives deletion of the sighting one.");

            string sighting = evaluate.Substring(seenAt, soundAt - seenAt);

            Assert.That(sighting, Does.Contain("AlertTimerTicks"),
                "The sighting commit no longer arms the awareness budget. A unit with a zero budget drops " +
                "out of the chase on the first tick it cannot see its target — that is the reported bug.");

            Assert.That(sighting, Does.Contain("EnemyAwarenessMath.FullAwarenessTicks"),
                "The sighting arming must be the full budget — `aiSpok = maxSpok + 10` " +
                "(UnitZombie.as:657), i.e. EnemyAwarenessMath.FullAwarenessTicks — not a literal and not " +
                "the shorter sound-only value. A hand-typed number is how the ladder and the counter drift " +
                "apart, and the sound value would make a sighting decay faster than the oracle allows.");

            // ── Half two: the exit condition. ─────────────────────────────────────────────────────
            string chase = SourceLint.MethodBody(brain, "protected virtual void TickCombatChase(");

            // The budget is read through one of TWO spellings, and both are correct:
            //
            //   * inline — `EnemyAwarenessMath.IsChasing(_blackboard.AlertTimerTicks)`;
            //   * through the shared helper — `if (ChaseBudgetExhausted)`.
            //
            // The helper exists because every archetype and boss overrides TickCombatChase WITHOUT
            // calling `base`, so each one had to re-state the ladder; `ChaseBudgetExhausted` is defined
            // once on EnemyBrain and the overrides call it. That extraction is an improvement, and it
            // made this assertion fail for the wrong reason — the fixture had pinned the inline spelling
            // only, so it went red the moment the helper appeared. A guard that is permanently red is
            // one nobody reads, which is worse than no guard at all.
            //
            // "Accept either" does not open a hole, because whichever spelling the chase uses is the one
            // that gets asserted: the helper's own definition is resolved below, so deleting the read
            // from BOTH places still fails, and redefining the helper to something that is not the
            // ladder still fails.
            string budgetRead = chase.Contains("ChaseBudgetExhausted")
                ? ExpressionMember(brain, "ChaseBudgetExhausted")
                : chase;

            Assert.That(budgetRead, Does.Contain("AlertTimerTicks"),
                "TickCombatChase no longer reads the awareness budget. Without it the chase has no exit " +
                "condition that a sighting cannot override.");

            Assert.That(budgetRead, Does.Contain("EnemyAwarenessMath.IsChasing("),
                "TickCombatChase must exit on the ladder's chase band, not on the sighting. See the method " +
                "body for why `TargetUnit` is the wrong question to ask here.");

            Assert.That(chase, Does.Contain("SetState(EnemyAIState.Alert)"),
                "The chase still has to have a way OUT — an exit condition that never demotes the unit " +
                "would leave it running at the last known position forever.");

            // ── The dead branch must not come back. ───────────────────────────────────────────────
            Assert.That(chase, Does.Not.Contain("TargetUnit == null"),
                "TickCombatChase is testing `TargetUnit == null` again. EnemySensors.Evaluate nulls that " +
                "field on the FIRST tick the target is not visible, so as an exit condition it means " +
                "'abandon the chase immediately' — and it makes any grace period below it unreachable, " +
                "which is precisely the bug this fixture was written for. A dead target is a separate " +
                "question and belongs in its own test.");

            Assert.That(chase, Does.Not.Contain("TimeSinceTargetSpottedTicks"),
                "TickCombatChase is reading TimeSinceTargetSpottedTicks again. It used to be the chase " +
                "timeout and it was unreachable — it is now a readout for the F3 overlay only. The " +
                "retention budget is AlertTimerTicks/aiSpok; using both would give the unit two " +
                "independent memories that can disagree.");
        }

        /// <summary>
        /// The third arming site, and the one that would have been broken <i>by</i> the fix: an ambusher
        /// rising out of the ground enters the chase directly, so its budget must clear the chase
        /// threshold or it pops up and demotes itself on its first tick.
        /// </summary>
        [Test]
        public void AnAmbusherRising_IsArmedPastTheChaseThreshold()
        {
            string zombie = SourceLint.ReadStripped(ZombiePath);
            string rise = SourceLint.MethodBody(zombie, "private void Rise()");

            Assert.That(rise, Does.Contain("EnemyAwarenessMath.FullAwarenessTicks"),
                "ZombieBrain.Rise no longer arms the full awareness budget. `vykop()` does " +
                "`aiSpok = maxSpok + 10` (UnitZombie.as:453) — it wakes up already committed. The port " +
                "used to approximate this with `_patrolDurationTicks` (90); now that TickCombatChase exits " +
                "below EnemyAwarenessMath.ChaseThresholdTicks (300), 90 would make the ambusher demote " +
                "itself to Alert on the tick it surfaced.");

            Assert.That(rise, Does.Not.Contain("_patrolDurationTicks"),
                "Rise is arming the awareness with the patrol duration again. That value is below the " +
                "chase threshold, so it cannot express 'already committed to the hunt'.");
        }

        /// <summary>
        /// The sound-only commit arms the budget too — and arms it <i>short</i>, so that hearing a target
        /// is never enough on its own to charge at it.
        /// </summary>
        /// <remarks>
        /// <para><b>The oracle's three arming sites do not agree</b>, and the middle one is the whole
        /// reason the ladder has two bands: a sighting gives <c>maxSpok + 10</c>
        /// (<c>UnitZombie.as:657</c>), a sound-only commit gives <c>maxSpok - 1</c> (<c>:661</c>), and an
        /// alarm / rise gives <c>maxSpok + 10</c> (<c>:344</c>, <c>:453</c>). <c>maxSpok - 1</c> is
        /// <i>below</i> the <c>aiSpok &gt;= maxSpok</c> test at <c>:633</c>, so a sound reaches the alert
        /// state and stops there. Wiring this branch to <c>FullAwarenessTicks</c> instead would make every
        /// footstep a charge.</para>
        ///
        /// <para><b>This arming was missing entirely</b>, which is the second half of the owner's report:
        /// <i>"…or make sound"</i> named both arming paths. <c>SoundOnlyAwarenessTicks</c> was declared
        /// and covered by <c>EnemyAwarenessMathTests</c> and had <b>no caller</b> — a constant wearing a
        /// parameter's clothes. The sound path fell back on the 90-tick <c>StateTimerTicks</c> (~3 s)
        /// where the oracle gives 290 ticks (~9.7 s).</para>
        /// </remarks>
        [Test]
        public void ASoundCommit_ArmsTheBudget_ButOnlyAsFarAsAlert()
        {
            string sensors = SourceLint.ReadStripped(SensorsPath);
            string evaluate = SourceLint.MethodBody(sensors, "public void Evaluate(");

            // Scope to the SOUND branch. `Evaluate` holds both arming sites, so an unscoped
            // `Does.Not.Contain("FullAwarenessTicks")` would fail on the sighting branch above it, and an
            // unscoped `Does.Contain("SoundOnlyAwarenessTicks")` would be satisfied by the sound branch
            // even if the chase had started reading it. `CommitsImmediately(` occurs once, in the sound
            // commit's condition, so it is the anchor that isolates this branch.
            int anchor = evaluate.IndexOf("CommitsImmediately(", System.StringComparison.Ordinal);
            Assert.That(anchor, Is.GreaterThan(0),
                "precondition: Evaluate no longer has a sound-only commit (`CommitsImmediately(`). Either " +
                "the branch was removed — in which case this guard must be rewritten, not deleted — or it " +
                "was renamed, and this anchor has gone stale.");

            string sound = evaluate.Substring(anchor);

            Assert.That(sound, Does.Contain("SoundOnlyAwarenessTicks"),
                "The sound-only commit no longer arms the awareness budget. AS3 does " +
                "`aiSpok = maxSpok - 1` there (UnitZombie.as:661); without it the port searches on the " +
                "90-tick state timer instead of the oracle's 290 ticks, and SoundOnlyAwarenessTicks goes " +
                "back to being a constant with a test and no caller.");

            Assert.That(sound, Does.Not.Contain("FullAwarenessTicks"),
                "The sound-only commit is arming the FULL budget. `maxSpok - 1` is below the oracle's " +
                "`aiSpok >= maxSpok` chase test (UnitZombie.as:633), so a sound must reach the alert state " +
                "and no further — full awareness here would make a footstep a charge.");
        }
    }
}
