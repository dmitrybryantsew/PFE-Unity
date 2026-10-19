using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Security;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Security
{
    /// <summary>
    /// Pins <see cref="SecurityRules"/> — the port of <c>fe/serv/Interact.as</c>'s attempt arithmetic
    /// (<c>:492-557</c> construction, <c>:889-1263</c> the attempt, <c>:1484-1545</c> the runa bypass,
    /// <c>:1697-1718</c> the <c>hack</c> verb).
    ///
    /// <para><b>Why this fixture exists at all.</b> The oracle's control flow here is <i>inverted</i>
    /// relative to how it reads, and the inversion is invisible in a one-attempt test that only checks
    /// "something happened". <c>Interact.act()</c> computes <c>_loc2_ = 1 - getChance(delta)</c> and runs
    /// the <b>failure</b> branch on a hit: that branch drains <c>lockHP</c>, snaps a pin, and at
    /// <c>lockHP &lt;= 0</c> calls <c>setAct("lock",102)</c> — the lock is <b>seized forever</b>. The
    /// <c>else</c> branch opens it. A port that opens the lock when the pool empties has the mechanic
    /// exactly backwards, looks plausible, and would ship. So every failure assertion here has a success
    /// control beside it drawn from the same profile, and the seize test asserts the lock is
    /// <b>100</b> rather than merely "not zero".</para>
    ///
    /// <para><b>Why the RNG is a queue.</b> Three draws happen in a fixed order on one stream —
    /// (1) the failure roll, (2) the pin-survival roll, (3) the damage roll. A constant RNG cannot tell
    /// them apart, so a port that rolled the damage first would produce the same numbers and a different
    /// game. <see cref="QueueRng"/> makes each draw addressable, and
    /// <see cref="APickFailure_DrawsFailureThenPinThenDamage"/> reads the damage value back out to prove
    /// the order.</para>
    /// </summary>
    public class SecurityRulesTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A profile that is locked at <paramref name="lockDifficulty"/> with a full pool, no mine, no
        /// key and the pick tip — the shape almost every case below starts from.
        /// </summary>
        private static SecurityProfile Pick(int lockDifficulty, float lockHp = 10f, int lockLevel = 0)
        {
            return new SecurityProfile
            {
                Lock = lockDifficulty,
                LockLevel = lockLevel,
                LockTip = SecurityLockTip.Pick,
                LockHp = lockHp,
                LockAtt = -100f,
                Mine = 0,
                MineTip = 3,
                LockKey = string.Empty,
            };
        }

        /// <summary>An actor with a lockpick rank and nothing else interesting.</summary>
        private static SecurityActor Picker(int skill, int master = 100, int pins = 0, float pinBreak = 1f)
        {
            return new SecurityActor
            {
                SkillPick = skill,
                MasterPick = master,
                Pins = pins,
                PinBreak = pinBreak,
                LockAtt = 1f,
                UpChance = 0f,
                HackAtt = 3,
                LockPickTime = 30,
            };
        }

        // ── The chance table ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The two rows are <c>Interact.as:14</c> and <c>:16</c>, indexed by <c>delta + 2</c>. The
        /// boundaries matter more than the middle: <c>delta &lt; -2</c> is a <b>certain success</b> and
        /// <c>delta &gt; 4</c> a <b>certain failure</b>, and a port that clamped the index instead would
        /// answer 0.9 and 0.01 there — a lock that can never be impossible is a different game.
        /// </summary>
        [Test]
        public void GetChance_MatchesTheOracleRows_AndSaturatesOutsideThem()
        {
            Assert.That(SecurityRules.ChanceUnlock, Is.EqualTo(new[] { 0.9f, 0.75f, 0.5f, 0.3f, 0.15f, 0.05f, 0.01f }),
                "Interact.as:14 — chanceUnlock, indexed by delta + 2.");
            Assert.That(SecurityRules.ChanceUnlock2, Is.EqualTo(new[] { 0.95f, 0.8f, 0.55f, 0.35f, 0.2f, 0.08f, 0.03f }),
                "Interact.as:16 — chanceUnlock2, the row Pers.upChance swaps in.");

            Assert.That(SecurityRules.GetChance(-3, false), Is.EqualTo(1f), "delta < -2 is certain.");
            Assert.That(SecurityRules.GetChance(-2, false), Is.EqualTo(0.9f), "delta == -2 is row 0.");
            Assert.That(SecurityRules.GetChance(0, false), Is.EqualTo(0.5f), "delta == 0 is row 2.");
            Assert.That(SecurityRules.GetChance(4, false), Is.EqualTo(0.01f), "delta == 4 is row 6.");
            Assert.That(SecurityRules.GetChance(5, false), Is.EqualTo(0f), "delta > 4 is impossible.");

            Assert.That(SecurityRules.GetChance(0, true), Is.EqualTo(0.55f), "upChance swaps the row.");
            Assert.That(SecurityRules.GetChance(-3, true), Is.EqualTo(1f), "…but not the saturation.");
            Assert.That(SecurityRules.GetChance(5, true), Is.EqualTo(0f));
        }

        // ── The inversion ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The single most important assertion in the subsystem: a draw <b>below</b> the failure
        /// probability damages the lock, and a draw <b>above</b> it opens the lock.
        ///
        /// <para>Same profile both times (delta 2 → chance 0.5 → failure 0.5), so the only difference is
        /// the draw. Either half alone would pass for a port that always fails or always succeeds; the
        /// pair cannot.</para>
        /// </summary>
        [Test]
        public void ALowDrawDamagesTheLock_AndAHighDrawOpensIt()
        {
            SecurityProfile failing = Pick(5);
            SecurityAttemptResult failed = SecurityRules.Resolve(
                failing, Picker(3), new QueueRng(0.0f), hasPin: true);

            Assert.That(failed.Kind, Is.EqualTo(SecurityAttemptKind.Failed),
                "0.0 < 1 - getChance(2) = 0.5, so the hit branch runs — and the hit branch is FAILURE.");
            Assert.That(failing.LockHp, Is.LessThan(10f), "a failure drains the pool.");
            Assert.That(failing.Lock, Is.EqualTo(5), "a failure does not change the difficulty.");
            Assert.That(failing.SaveLock, Is.Not.EqualTo(SecurityRules.SaveLockOpen));

            SecurityProfile succeeding = Pick(5);
            SecurityAttemptResult opened = SecurityRules.Resolve(
                succeeding, Picker(3), new QueueRng(0.99f), hasPin: true);

            Assert.That(opened.Kind, Is.EqualTo(SecurityAttemptKind.Opened),
                "0.99 >= 0.5, so the else branch runs — and the else branch OPENS the lock.");
            Assert.That(succeeding.Lock, Is.EqualTo(0), "setAct(\"lock\",101) zeroes the difficulty.");
            Assert.That(succeeding.SaveLock, Is.EqualTo(SecurityRules.SaveLockOpen));
            Assert.That(opened.SuccessChance, Is.EqualTo(0.5f).Within(1e-5f));
        }

        /// <summary>
        /// The pool running out <b>seizes</b> the lock — it does not open it.
        ///
        /// <para>This is the exact mistake the old port made. The assertion is not "the lock changed"
        /// but "the lock is <see cref="SecurityRules.SeizedLockValue"/> and
        /// <see cref="SecurityRules.SaveLockSeized"/>", because 100 is the value every entry guard
        /// refuses (<c>Interact.as:1004</c> <c>lock &lt; 100</c>) and 102 is what a save round-trips.</para>
        /// </summary>
        [Test]
        public void AnEmptyPool_SeizesTheLock_ItDoesNotOpenIt()
        {
            SecurityProfile profile = Pick(5, lockHp: 1f);

            SecurityAttemptResult result = SecurityRules.Resolve(
                profile, Picker(3), new QueueRng(0.0f), hasPin: true);

            Assert.That(result.Kind, Is.EqualTo(SecurityAttemptKind.Jammed));
            Assert.That(profile.Lock, Is.EqualTo(SecurityRules.SeizedLockValue));
            Assert.That(profile.SaveLock, Is.EqualTo(SecurityRules.SaveLockSeized));
            Assert.That(profile.IsLocked, Is.False, "a seized lock is not a lock you can work.");
            Assert.That(profile.Jammed, Is.True);
            Assert.That(result.MessageKey, Is.EqualTo("unLockZhopa"));

            // The control: the same profile with a healthy pool fails without seizing.
            SecurityProfile healthy = Pick(5);
            SecurityRules.Resolve(healthy, Picker(3), new QueueRng(0.0f), hasPin: true);
            Assert.That(healthy.Jammed, Is.False);
            Assert.That(healthy.IsLocked, Is.True);
        }

        /// <summary>
        /// <c>if(master &lt; lockLevel) _loc2_ = 1;</c> (<c>:1017-1020</c>) — the quality tier is a
        /// <b>precondition</b>, not a bonus. A draw of 0.99 against a delta that would otherwise be a
        /// certain success still fails, and the result says why.
        /// </summary>
        [Test]
        public void AMasteryBelowTheLockLevel_ForcesFailure_EvenOnASuccessfulDraw()
        {
            SecurityProfile profile = Pick(3, lockLevel: 3);
            SecurityActor weak = Picker(5, master: 2);   // skill 5 vs lock 3 → delta -2 → chance 1.0

            Assert.That(SecurityRules.GetChance(profile.Lock - weak.SkillPick, false), Is.EqualTo(1f),
                "premise: without the gate this attempt cannot fail.");

            SecurityAttemptResult result = SecurityRules.Resolve(
                profile, weak, new QueueRng(0.99f), hasPin: true);

            Assert.That(result.MasterGate, Is.True);
            Assert.That(result.SuccessChance, Is.EqualTo(0f));
            Assert.That(result.Kind, Is.Not.EqualTo(SecurityAttemptKind.Opened));
            Assert.That(profile.IsLocked, Is.True, "the lock survived; it was not opened.");

            // Control: the same profile with the mastery met does open on the same draw.
            SecurityProfile strongProfile = Pick(3, lockLevel: 3);
            SecurityAttemptResult strong = SecurityRules.Resolve(
                strongProfile, Picker(5, master: 3), new QueueRng(0.99f), hasPin: true);

            Assert.That(strong.MasterGate, Is.False);
            Assert.That(strong.Kind, Is.EqualTo(SecurityAttemptKind.Opened));
        }

        /// <summary>
        /// The three draws happen in the oracle's order: failure, pin survival, damage.
        ///
        /// <para>The damage value is the read-out: base 2, spread 4 for delta 2, so a third draw of 0.5
        /// must yield <c>(2 + 0.5*4) * lockAtt = 4</c>. If the pin roll and the damage roll were
        /// swapped, the damage would be <c>(2 + 0.9*4) = 5.6</c>; if the damage were rolled first, 3.0.
        /// None of those is 4.</para>
        /// </summary>
        [Test]
        public void APickFailure_DrawsFailureThenPinThenDamage()
        {
            SecurityProfile profile = Pick(5);
            SecurityActor actor = Picker(3, pins: 1, pinBreak: 0.5f);

            SecurityAttemptResult result = SecurityRules.Resolve(
                profile, actor, new QueueRng(0.0f, 0.9f, 0.5f), hasPin: true);

            Assert.That(result.Kind, Is.EqualTo(SecurityAttemptKind.Failed));
            Assert.That(result.PinBroke, Is.False, "0.9 >= pinBreak 0.5, so the pin survives.");
            Assert.That(result.Damage, Is.EqualTo(4f).Within(1e-4f),
                "damage = (base 2 + draw 0.5 * spread 4) * lockAtt 1 — i.e. the THIRD draw.");
            Assert.That(profile.LockHp, Is.EqualTo(6f).Within(1e-4f));
        }

        /// <summary>
        /// A snapped pin is its own outcome (<c>unLockFailP</c>, <c>:1097</c>) and still damages the
        /// lock. <c>pinBreak &gt;= 1</c> short-circuits without a draw (<c>:1079-1082</c>).
        /// </summary>
        [Test]
        public void ASnappedPin_IsReportedSeparately_AndStillDamages()
        {
            SecurityProfile profile = Pick(5);
            SecurityActor actor = Picker(3, pins: 1, pinBreak: 1f);

            SecurityAttemptResult result = SecurityRules.Resolve(
                profile, actor, new QueueRng(0.0f), hasPin: true);

            Assert.That(result.PinBroke, Is.True);
            Assert.That(result.Kind, Is.EqualTo(SecurityAttemptKind.PinBroke));
            Assert.That(result.MessageKey, Is.EqualTo("unLockFailP"));
            Assert.That(profile.LockHp, Is.LessThan(10f));
        }

        /// <summary>
        /// Without a pin the damage gains a flat +2 instead (<c>:1079-1082</c>) — so a player with no
        /// bobby pins is not safer, they are worse off.
        /// </summary>
        [Test]
        public void WithoutAPin_TheDamageGainsTwo_AndNoPinRollHappens()
        {
            SecurityProfile profile = Pick(5);

            // One draw only: if the pin branch were taken the queue would need a second value, and the
            // damage would come from the repeated last value (0.0) instead of 0.5.
            SecurityAttemptResult result = SecurityRules.Resolve(
                profile, Picker(3, pins: 0), new QueueRng(0.0f), hasPin: true);

            Assert.That(result.PinBroke, Is.False);
            Assert.That(result.Damage, Is.EqualTo(2f).Within(1e-4f),
                "(base 0 + flat 2 + draw 0.0 * spread 4) * 1 — the +2 is the no-pin penalty.");
        }

        // ── Terminals ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A terminal burns <b>attempts</b>, not a damage pool, and running out blocks it permanently
        /// (<c>unLockBlock</c>, <c>:1131-1140</c>). With <c>hackAtt</c> 3 the third failure is the
        /// lockout, and the pool is untouched throughout — which is what makes a terminal a different
        /// problem from a pick rather than a reskin.
        /// </summary>
        [Test]
        public void ATerminal_BurnsAttemptsNotHp_AndLocksOutOnTheThirdFailure()
        {
            SecurityProfile profile = Pick(4);
            profile.LockTip = SecurityLockTip.Terminal;

            SecurityActor hacker = Picker(0);
            hacker.SkillTerminal = 2;    // delta 2 → chance 0.5
            hacker.HackAtt = 3;

            // 1st failure: lockAtt is seeded from hackAtt (3) and decremented to 2.
            SecurityAttemptResult first = SecurityRules.Resolve(
                profile, hacker, new QueueRng(0.0f), hasPin: false);
            Assert.That(first.Kind, Is.EqualTo(SecurityAttemptKind.Failed));
            Assert.That(first.LockAttAfter, Is.EqualTo(2f), "the -100 sentinel is replaced by hackAtt.");
            Assert.That(profile.LockHp, Is.EqualTo(10f), "a terminal does not damage the pool.");

            // 2nd: 2 → 1, which is the "last attempt" message.
            SecurityAttemptResult second = SecurityRules.Resolve(
                profile, hacker, new QueueRng(0.0f), hasPin: false);
            Assert.That(second.MessageKey, Is.EqualTo("unTermLockFail1"));
            Assert.That(second.LockAttAfter, Is.EqualTo(1f));

            // 3rd: 1 → 0, which is the permanent block.
            SecurityAttemptResult third = SecurityRules.Resolve(
                profile, hacker, new QueueRng(0.0f), hasPin: false);
            Assert.That(third.Kind, Is.EqualTo(SecurityAttemptKind.LockedOut));
            Assert.That(third.MessageKey, Is.EqualTo("unLockBlock"));
            Assert.That(profile.Lock, Is.EqualTo(SecurityRules.SeizedLockValue));
            Assert.That(profile.SaveLock, Is.EqualTo(SecurityRules.SaveLockSeized));
            Assert.That(profile.LockHp, Is.EqualTo(10f), "…still untouched: terminals have no pool.");
        }

        /// <summary>
        /// <c>lockatt &gt; 10</c> is the "too complex to even report" band (<c>:1117-1120</c>) — a
        /// terminal authored with more attempts than the player's <c>hackAtt</c> fails silently rather
        /// than printing a count it cannot mean.
        /// </summary>
        [Test]
        public void ATerminalAuthoredAboveTenAttempts_FailsSilently()
        {
            SecurityProfile profile = Pick(4);
            profile.LockTip = SecurityLockTip.Terminal;
            profile.LockAtt = 20f;       // authored on the object, not seeded from hackAtt

            SecurityActor hacker = Picker(0);
            hacker.SkillTerminal = 2;

            SecurityAttemptResult result = SecurityRules.Resolve(
                profile, hacker, new QueueRng(0.0f), hasPin: false);

            Assert.That(result.Kind, Is.EqualTo(SecurityAttemptKind.FailedNoVisibleDamage));
            Assert.That(result.MessageKey, Is.EqualTo("unTermLockFail2"));
            Assert.That(result.LockAttAfter, Is.EqualTo(19f));
        }

        // ── Entry guards ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>unlock &lt;= -99</c> is "no possibility" (<c>noPoss</c>, <c>:1234</c>) — the
        /// <b>-100</b> sentinel <c>Pers.getLockTip</c> answers without a tool. It must be decided
        /// <i>before</i> any roll: an actor who cannot pick must not consume the stream, or every later
        /// roll in the room desynchronises from the seed.
        /// </summary>
        [Test]
        public void WithoutTheTool_TheAttemptIsRefused_AndNoDrawIsTaken()
        {
            var rng = new QueueRng(0.0f);

            SecurityAttemptResult result = SecurityRules.Resolve(
                Pick(5), Picker(-100), rng, hasPin: true);

            Assert.That(result.Kind, Is.EqualTo(SecurityAttemptKind.NoSkill));
            Assert.That(result.MessageKey, Is.EqualTo("noPoss"));
            Assert.That(rng.Draws, Is.EqualTo(0), "the refusal is decided before the roll.");
        }

        /// <summary>
        /// A key opens the lock with no roll, and is <b>not consumed</b> (<c>:980-1004</c>) — the AS3
        /// branch only calls <c>setAct("lock",101)</c>. A port that consumed it would make a key a
        /// single-use item and strand the player behind a door they had already opened once.
        /// </summary>
        [Test]
        public void AKeyOpensWithoutARoll_AndIsNotConsumed()
        {
            SecurityProfile profile = Pick(9);
            profile.LockKey = "key_sewer";

            var rng = new QueueRng(0.0f);
            SecurityActor actor = Picker(0);

            SecurityAttemptResult result = SecurityRules.Resolve(
                profile, actor, rng, hasPin: false, hasKey: true);

            Assert.That(result.Kind, Is.EqualTo(SecurityAttemptKind.KeyUsed));
            Assert.That(result.MessageKey, Is.EqualTo("unLockKey"));
            Assert.That(profile.Lock, Is.EqualTo(0));
            Assert.That(rng.Draws, Is.EqualTo(0), "no roll: the key branch is taken first.");

            // Without the key the same lock is merely refused, not opened.
            SecurityProfile keyless = Pick(9);
            keyless.LockKey = "key_sewer";
            SecurityAttemptResult refused = SecurityRules.Resolve(
                keyless, actor, new QueueRng(0.0f), hasPin: false, hasKey: false);
            Assert.That(refused.Kind, Is.Not.EqualTo(SecurityAttemptKind.KeyUsed));
            Assert.That(keyless.IsLocked, Is.True);
        }

        /// <summary>
        /// <c>locktip == 0</c> is key-only: <c>UnitPlayer.actAction</c> refuses to even arm the timer
        /// (<c>:1970-1973</c>), so reaching the rules without the key is <c>noKey</c>, not a roll.
        /// </summary>
        [Test]
        public void AKeyOnlyLock_WithoutTheKey_IsNoKey_NotAChanceRoll()
        {
            SecurityProfile profile = Pick(5);
            profile.LockTip = SecurityLockTip.Key;
            profile.LockKey = "key_boss";

            var rng = new QueueRng(0.99f);
            SecurityAttemptResult result = SecurityRules.Resolve(
                profile, Picker(10), rng, hasPin: false);

            Assert.That(result.Kind, Is.EqualTo(SecurityAttemptKind.NeedKey));
            Assert.That(result.MessageKey, Is.EqualTo("noKey"));
            Assert.That(rng.Draws, Is.EqualTo(0));
            Assert.That(profile.IsLocked, Is.True);
        }

        // ── Mines ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A mine has <b>two independent probabilities</b> and the disaster is the smaller one
        /// (<c>:911-979</c>): <c>(mine - skill + 1) * 0.15</c> for the disaster and
        /// <c>(mine - skill + 2) * 0.2</c> for a plain failure. With mine 3 and skill 1 both bands are
        /// addressable, and the three draws below land one in each.
        /// </summary>
        [Test]
        public void AMineHasThreeBands_AndOnlyTheDisasterDisarmsIt()
        {
            // delta 2 → disaster 0.45, failure 0.8.
            SecurityProfile disaster = new SecurityProfile { Mine = 3, MineTip = 3 };
            SecurityActor sapper = Picker(0);
            sapper.SkillMine = 1;

            SecurityAttemptResult boom = SecurityRules.ResolveMine(disaster, sapper, new QueueRng(0.10f));
            Assert.That(boom.Kind, Is.EqualTo(SecurityAttemptKind.Jammed));
            Assert.That(boom.MessageKey, Is.EqualTo("remineZhopa"));
            Assert.That(disaster.Mine, Is.EqualTo(0), "the trap is spent either way.");

            SecurityProfile fizzle = new SecurityProfile { Mine = 3, MineTip = 3 };
            SecurityAttemptResult failed = SecurityRules.ResolveMine(fizzle, sapper, new QueueRng(0.50f));
            Assert.That(failed.Kind, Is.EqualTo(SecurityAttemptKind.Failed));
            Assert.That(failed.MessageKey, Is.EqualTo("remineFail"));
            Assert.That(fizzle.Mine, Is.EqualTo(3), "a plain failure leaves it armed.");

            SecurityProfile cleared = new SecurityProfile { Mine = 3, MineTip = 3 };
            SecurityAttemptResult done = SecurityRules.ResolveMine(cleared, sapper, new QueueRng(0.90f));
            Assert.That(done.Kind, Is.EqualTo(SecurityAttemptKind.Opened));
            Assert.That(done.MessageKey, Is.EqualTo("remine"));
            Assert.That(cleared.Mine, Is.EqualTo(0));
        }

        /// <summary>
        /// A signal trap reads <c>getLockTip(6)</c> — <c>Pers.signal</c>, a <b>different skill</b> from
        /// the mine's <c>remine</c> (<c>Pers.as:2328-2343</c>). Folding the two together would make a
        /// demolition expert automatically a signal expert.
        ///
        /// <para>The setup makes the two skills disagree: with <c>SkillMine</c> 5 the delta would be -2
        /// (no disaster band at all, so a 0.9 draw clears it); with <c>SkillSignal</c> 0 the delta is 3,
        /// the failure band reaches 1.0, and the same draw leaves the mine armed.</para>
        /// </summary>
        [Test]
        public void ASignalTrapReadsTheSignalSkill_NotTheDemolitionSkill()
        {
            SecurityProfile profile = new SecurityProfile { Mine = 3, MineTip = 6 };

            SecurityActor actor = Picker(0);
            actor.SkillMine = 5;      // would make this trivially clearable
            actor.SkillSignal = 0;    // what the oracle actually reads for minetip 6

            SecurityAttemptResult result = SecurityRules.ResolveMine(profile, actor, new QueueRng(0.90f));

            Assert.That(result.Kind, Is.EqualTo(SecurityAttemptKind.Failed),
                "delta 3 → failure band 1.0, so 0.9 fails. If SkillMine had been read, delta -2 → clear.");
            Assert.That(result.MessageKey, Is.EqualTo("signalFail"));
            Assert.That(profile.Mine, Is.EqualTo(3));
        }

        // ── Runa / reboot ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// The bypass is offered only when the actor <b>cannot</b> make the roll
        /// (<c>lock - skill &gt; 1</c> or <c>lockLevel &gt; master</c>) and carries the item
        /// (<c>:1484-1509</c>). Offering it on a lock you could pick would spend the item for nothing.
        /// </summary>
        [Test]
        public void TheBypass_IsOfferedOnlyWhenTheLockOutmatchesYou_AndConsumesTheRightItem()
        {
            SecurityProfile hardPick = Pick(10);
            SecurityActor actor = Picker(1);
            actor.Runa = 1;
            actor.Reboot = 1;

            Assert.That(SecurityRules.NeedBypass(hardPick, actor), Is.True, "lock 10 vs skill 1.");

            string key = SecurityRules.ApplyBypass(hardPick, ref actor);
            Assert.That(key, Is.EqualTo("useRuna"));
            Assert.That(hardPick.Lock, Is.EqualTo(0));
            Assert.That(actor.Runa, Is.EqualTo(0), "the runa is consumed.");
            Assert.That(actor.Reboot, Is.EqualTo(1), "…and the reboot is not.");

            // A pick you can make is not bypassable even with the item in hand.
            SecurityProfile easyPick = Pick(2);
            SecurityActor rich = Picker(5);
            rich.Runa = 5;
            Assert.That(SecurityRules.NeedBypass(easyPick, rich), Is.False);
            Assert.That(SecurityRules.ApplyBypass(easyPick, ref rich), Is.Null);
            Assert.That(rich.Runa, Is.EqualTo(5), "nothing was spent.");

            // A terminal needs the reboot, not the runa.
            SecurityProfile terminal = Pick(10);
            terminal.LockTip = SecurityLockTip.Terminal;
            SecurityActor hacker = Picker(0);
            hacker.SkillTerminal = 1;
            hacker.Runa = 5;
            hacker.Reboot = 0;
            Assert.That(SecurityRules.NeedBypass(terminal, hacker), Is.False,
                "5 runas do not open a terminal.");
        }

        // ── The hack verb ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>command("hack")</c> refuses unless the object authors <c>hack</c>
        /// (<c>:1699-1710</c>) — which is what stops a terminal opening an arbitrary crate — and on
        /// success clears the lock <b>and</b> the mine.
        /// </summary>
        [Test]
        public void TheHackVerb_ClearsLockAndMine_ButOnlyOnAHackableObject()
        {
            SecurityProfile hackable = Pick(6);
            hackable.Mine = 2;
            hackable.IsHackable = true;

            Assert.That(SecurityRules.ApplyHackCommand(hackable), Is.True);
            Assert.That(hackable.Lock, Is.EqualTo(0));
            Assert.That(hackable.Mine, Is.EqualTo(0));
            Assert.That(hackable.SaveLock, Is.EqualTo(SecurityRules.SaveLockOpen));

            SecurityProfile plain = Pick(6);
            plain.Mine = 2;
            plain.IsHackable = false;

            Assert.That(SecurityRules.ApplyHackCommand(plain), Is.False);
            Assert.That(plain.Lock, Is.EqualTo(6), "an unhackable object is untouched.");
            Assert.That(plain.Mine, Is.EqualTo(2));
        }

        // ── Construction ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The caps in <c>:492-557</c>: a pick is clamped to 24 and a mechanical tip to 7. The two
        /// numbers are different on purpose (<c>maxLockLvl</c> vs <c>maxMechLvl</c>), and applying the
        /// pick cap to a terminal would let a terminal reach difficulty 24 — six times the authored
        /// range.
        ///
        /// <para><b><c>saveLock</c> is the authored 30, not the clamped 24.</b> AS3 snapshots it at
        /// <c>:244</c>/<c>:339</c> and the clamp block changes <c>lock</c> without touching
        /// <c>saveLock</c>; <c>save()</c> then writes <c>saveLock</c> into the saved row
        /// (<c>:601</c>). Re-stamping it after the clamps would make the field a second copy of
        /// <c>lock</c> and silently change what a save round-trips.</para>
        /// </summary>
        [Test]
        public void Build_ClampsAPickTo24_AndAMechanicalTipTo7_ButSavesTheAuthoredValue()
        {
            ObjectInstance pick = Object("lock", "30");
            SecurityProfile picked = SecurityRules.Build(pick, SecurityBuildContext.Authored, new QueueRng(0.5f));

            Assert.That(picked.Lock, Is.EqualTo(SecurityRules.MaxLockLevel));
            Assert.That(picked.SaveLock, Is.EqualTo(30),
                "saveLock keeps the authored value — the clamp applies to `lock` only.");

            ObjectInstance terminal = Object("lock", "30", "locktip", "2");
            SecurityProfile hacked = SecurityRules.Build(terminal, SecurityBuildContext.Authored, new QueueRng(0.5f));

            Assert.That(hacked.LockTip, Is.EqualTo(SecurityLockTip.Terminal));
            Assert.That(hacked.Lock, Is.EqualTo(SecurityRules.MaxMechanicalLevel));
            Assert.That(hacked.SaveLock, Is.EqualTo(30));
        }

        /// <summary>
        /// <c>allDif</c> is the derived score a UI ranks locks by (<c>:519</c> <c>lock + level*2</c>,
        /// <c>:530</c> <c>lock*3</c>, <c>:551</c> <c>+= mine*2</c>). It is read by no roll, which is
        /// exactly why it needs a test: nothing else would ever notice it going stale.
        /// </summary>
        [Test]
        public void Build_DerivesAllDif_FromLockLevelAndMine()
        {
            ObjectInstance obj = Object("lock", "6", "locklevel", "2");
            SecurityProfile p = SecurityRules.Build(obj, SecurityBuildContext.Authored, new QueueRng(0.5f));

            Assert.That(p.LockLevel, Is.EqualTo(2), "an authored level is not re-rolled.");
            Assert.That(p.AllDif, Is.EqualTo(6 + 2 * 2), "a pick: lock + level*2.");

            ObjectInstance mined = Object("lock", "6", "locklevel", "2", "mine", "3");
            SecurityProfile m = SecurityRules.Build(mined, SecurityBuildContext.Authored, new QueueRng(0.5f));
            Assert.That(m.AllDif, Is.EqualTo(6 + 2 * 2 + 3 * 2), "…plus mine*2.");
        }

        /// <summary>
        /// A placement that authors its own <c>lock</c> clears <c>low</c> (<c>:340</c>), so the halving
        /// roll can never apply to it — which is what makes an authored lock the difficulty the author
        /// wrote.
        ///
        /// <para><b>And a placement <c>low</c> is not read at all.</b> <c>low</c> comes from the
        /// definition row (<c>:240-242</c>) and from nowhere else; the placement block only clears it.
        /// A port that read <c>low</c> from the placement too would re-arm the halving on exactly the
        /// locks the oracle exempts, and the mistake is invisible unless a placement authors both.</para>
        /// </summary>
        [Test]
        public void Build_AnAuthoredLockIsNotHalved_AndAPlacementLowIsIgnored()
        {
            // `low = 1` would halve 6 to 3 if it applied; the placement lock clears it first, and the
            // placement read that used to follow must not put it back.
            ObjectInstance obj = Object("lock", "6", "low", "1");
            SecurityProfile p = SecurityRules.Build(obj, SecurityBuildContext.Authored, new QueueRng(0.0f));

            Assert.That(p.Low, Is.EqualTo(0f), "a placement `low` is not an oracle input.");
            Assert.That(p.Lock, Is.EqualTo(6), "so the draw of 0.0 cannot halve it.");
        }

        /// <summary>
        /// A placement with <c>lock="0"</c> clears its mine — but <b>only if the placement does not also
        /// author one</b>. AS3 zeroes <c>mine</c> inside the lock block (<c>:343</c>) and reads
        /// <c>mine</c> afterwards (<c>:366-370</c>), so the authored value wins. A port that zeroed the
        /// mine after the reads would silently disarm every authored trap on an unlocked object.
        /// </summary>
        [Test]
        public void Build_AnUnlockedPlacementClearsItsMine_ButAnAuthoredMineStillLands()
        {
            ObjectInstance cleared = Object("lock", "0");
            SecurityProfile a = SecurityRules.Build(cleared, SecurityBuildContext.Authored, new QueueRng(0.5f));
            Assert.That(a.Lock, Is.EqualTo(0));
            Assert.That(a.Mine, Is.EqualTo(0), "`if(this.lock == 0) this.mine = 0;`");
            Assert.That(a.IsLocked, Is.False);

            ObjectInstance stillMined = Object("lock", "0", "mine", "4");
            SecurityProfile b = SecurityRules.Build(stillMined, SecurityBuildContext.Authored, new QueueRng(0.5f));
            Assert.That(b.Lock, Is.EqualTo(0));
            Assert.That(b.Mine, Is.EqualTo(4),
                "the placement `mine` read comes AFTER the zeroing — the trap is still there.");
        }

        // ── The two existence rolls (lockch / minech) ───────────────────────────────────────

        /// <summary>
        /// <c>lockch</c> is the probability that this placement has a lock <b>at all</b>
        /// (<c>Interact.as:219</c>). <c>door1</c> authors <c>lockch='0.3'</c>, so in the oracle seven
        /// out of ten wooden doors are simply unlocked.
        ///
        /// <para><b>This is the "every door is locked" regression.</b> The port read <c>lock</c> and
        /// ignored <c>lockch</c>, so every <c>door1</c>/<c>door1a</c>/<c>door1b</c>/<c>chest</c> in the
        /// game had a lock — a different game from the one where most doors just open.</para>
        /// </summary>
        [Test]
        public void Build_RollsLockch_SoAMissLeavesTheObjectUnlocked()
        {
            MapObjectDefinition row = Definition("lock", "1", "lockch", "0.3", "inter", "1");

            // 0.9 > 0.3 → no lock. The second draw is the fractional bump, and 1.0 has no fraction.
            SecurityProfile missed = SecurityRules.Build(
                ObjectWith(row), SecurityBuildContext.Authored, new QueueRng(0.9f, 0.5f));
            Assert.That(missed.Lock, Is.EqualTo(0), "the lockch roll failed — there is no lock.");
            Assert.That(missed.IsLocked, Is.False);

            // 0.1 < 0.3 → locked, at the authored difficulty when locksLevel is 0.
            SecurityProfile hit = SecurityRules.Build(
                ObjectWith(row), SecurityBuildContext.Authored, new QueueRng(0.1f, 0.5f, 0.5f));
            Assert.That(hit.Lock, Is.EqualTo(1));
        }

        /// <summary>
        /// The fractional bump sits <b>outside</b> the <c>lockch</c> gate (<c>Interact.as:230-233</c>):
        /// a row authored <c>lock='1.3'</c> whose <c>lockch</c> roll fails still becomes lock <b>1</b>
        /// with 30 % probability. It reads like a bug in the oracle and it is reproduced on purpose —
        /// moving the bump inside the gate would make those doors reliably open.
        /// </summary>
        [Test]
        public void Build_TheFractionalBumpSurvivesALockchMiss()
        {
            MapObjectDefinition row = Definition("lock", "1.3", "lockch", "0.3", "inter", "1");

            SecurityProfile p = SecurityRules.Build(
                ObjectWith(row), SecurityBuildContext.Authored, new QueueRng(0.9f, 0.1f));

            Assert.That(p.Lock, Is.EqualTo(1),
                "lockch missed, but the 0.3 fractional part still bumps the empty lock to 1.");
        }

        /// <summary>
        /// A lock's difficulty is rolled against the <b>land's</b> <c>locksLevel</c>
        /// (<c>floor(lock + (0.3 + random()) * locksLevel)</c>, <c>Interact.as:223</c>) — which is
        /// <c>locDifLevel * 0.7</c> (<c>Land.as:984</c>). The port never read it, so lockpicking did not
        /// scale with the land's difficulty at all.
        /// </summary>
        [Test]
        public void Build_ScalesALockByTheLandsLocksLevel()
        {
            MapObjectDefinition row = Definition("lock", "1", "inter", "1");
            var context = new SecurityBuildContext(locksLevel: 10f);

            // (0.3 + 0.5) * 10 = 8 → floor(1 + 8) = 9.
            SecurityProfile p = SecurityRules.Build(
                ObjectWith(row), context, new QueueRng(0.5f, 0.5f));

            Assert.That(p.Lock, Is.EqualTo(9));
        }

        /// <summary>
        /// <c>minech</c> is the trap's own existence roll (<c>Interact.as:253-258</c>) — <c>safe</c>
        /// authors 0.6, <c>wallsafe</c> 0.75. The port ignored it, so <b>every</b> safe carried a live
        /// explosive; in the oracle two safes in five open without one.
        /// </summary>
        [Test]
        public void Build_RollsMinech_SoAMissLeavesTheObjectUntrapped()
        {
            MapObjectDefinition row = Definition("mine", "2", "minech", "0.6", "inter", "1");

            SecurityProfile missed = SecurityRules.Build(
                ObjectWith(row), SecurityBuildContext.Authored, new QueueRng(0.9f, 0.5f));
            Assert.That(missed.Mine, Is.EqualTo(0), "the minech roll failed — there is no trap.");
            Assert.That(missed.TrapDamage, Is.EqualTo(0f));

            // 0.1 arms it; 0.5 * (2 + 0*mech + 1) = 1.5 → 1. Then the minetip draw (0.5) leaves tip 3.
            SecurityProfile armed = SecurityRules.Build(
                ObjectWith(row), SecurityBuildContext.Authored, new QueueRng(0.1f, 0.5f, 0.0f, 0.5f));
            Assert.That(armed.Mine, Is.EqualTo(1));
            Assert.That(armed.MineTip, Is.EqualTo(3));
        }

        /// <summary>
        /// An explosive trap's blast is rolled once, at construction:
        /// <c>mine * 50 * (0.8 + random()*0.4) * (1 + locDifLevel*0.1)</c> (<c>Interact.as:546</c>).
        /// A <b>signal</b> trap (<c>minetip == 6</c>) has no blast — its disaster is the alarm
        /// (<c>:540-543</c>) — so it must not carry damage.
        /// </summary>
        [Test]
        public void Build_AnExplosiveTrapCarriesItsBlast_ButASignalTrapDoesNot()
        {
            MapObjectDefinition explosive = Definition("mine", "2", "minech", "0.6", "inter", "1");
            SecurityProfile boom = SecurityRules.Build(
                ObjectWith(explosive), SecurityBuildContext.Authored,
                new QueueRng(0.1f, 0.5f, 0.0f, 0.5f, 0.5f));
            Assert.That(boom.Mine, Is.EqualTo(1));
            Assert.That(boom.TrapDamage, Is.EqualTo(50f).Within(0.01f),
                "1 * 50 * (0.8 + 0.5*0.4) * 1 = 50.");

            MapObjectDefinition signal = Definition(
                "mine", "2", "minech", "0.6", "minetip", "6", "inter", "1");
            SecurityProfile alarm = SecurityRules.Build(
                ObjectWith(signal), SecurityBuildContext.Authored,
                new QueueRng(0.1f, 0.5f, 0.0f));
            Assert.That(alarm.MineTip, Is.EqualTo(6));
            Assert.That(alarm.TrapDamage, Is.EqualTo(0f), "a signal trap raises the alarm, not a blast.");
        }

        /// <summary>
        /// Defusing a mine clears <b>only</b> the mine. The lock is untouched, which is what makes a
        /// mined safe a two-press problem: press one disarms, press two picks. The port's
        /// <c>TryWork</c> used to report a successful defusal as <c>Opened</c> and fall through to the
        /// container's own action, so one press both disarmed the trap and opened the safe.
        /// </summary>
        [Test]
        public void ResolveMine_ASuccessClearsTheTrapButLeavesTheLock()
        {
            SecurityProfile p = Pick(5);
            p.Mine = 1;
            p.MineTip = 3;

            SecurityAttemptResult result = SecurityRules.ResolveMine(p, Picker(5), new QueueRng(0.99f));

            Assert.That(result.Kind, Is.EqualTo(SecurityAttemptKind.Opened));
            Assert.That(p.Mine, Is.EqualTo(0), "the trap is defused.");
            Assert.That(p.Lock, Is.EqualTo(5), "…and the lock is exactly where it was.");
        }

        /// <summary>
        /// <c>at_once</c> is read from the <b>definition row only</b> (<c>:296-299</c>), and it is what
        /// lets one press finish the job — the <c>actOsn()</c> that follows a defusal runs only under
        /// <c>if(this.at_once &gt; 0)</c> (<c>:935-938</c>, <c>:969-972</c>).
        ///
        /// <para><b>Both halves matter.</b> <c>alarm</c> is the only shipped row that is both mined and
        /// <c>once</c>, and for it "open" means "disabled" — so a defused signal trap must spend the
        /// panel in the same press. Every other mined row (<c>safe</c>, <c>wallsafe</c>, <c>stdoor</c>,
        /// <c>basedoor</c>, <c>encldoor</c>, <c>term3</c>) is not <c>once</c>, which is what keeps a
        /// mined safe a two-press problem. And a placement that authors <c>once</c> changes nothing:
        /// the placement block has no <c>@once</c> read at all.</para>
        /// </summary>
        [Test]
        public void ReadAtOnce_ComesFromTheDefinitionRow_AndNowhereElse()
        {
            Assert.That(SecurityRules.ReadAtOnce(ObjectWith(Definition("inter", "5", "once", "1"))), Is.True);

            Assert.That(SecurityRules.ReadAtOnce(ObjectWith(Definition("inter", "1"))), Is.False,
                "a row with no `once` is not a one-press object.");

            Assert.That(SecurityRules.ReadAtOnce(Object("once", "1")), Is.False,
                "a placement `once` is not read by the oracle — the definition row is the only source.");

            Assert.That(SecurityRules.ReadAtOnce(null), Is.False);
        }

        // ── Fakes ────────────────────────────────────────────────────────────────────────────

        /// <summary>Builds a bare <see cref="ObjectInstance"/> with alternating key/value attributes.</summary>
        private static ObjectInstance Object(params string[] keyValues)
        {
            var obj = new ObjectInstance { objectId = "test_obj" };
            for (int i = 0; i + 1 < keyValues.Length; i += 2)
            {
                obj.attributes.Add(new MapObjectAttributeData { key = keyValues[i], value = keyValues[i + 1] });
            }

            return obj;
        }

        /// <summary>
        /// An <c>AllData</c> row as the port stores it — <c>legacyAttributes</c> is what
        /// <c>MapObjectDefinition.GetAttribute</c> reads, and it is where <c>lock</c>/<c>lockch</c>/
        /// <c>mine</c>/<c>minech</c>/<c>locktip</c> actually live.
        /// </summary>
        private static MapObjectDefinition Definition(params string[] keyValues)
        {
            var definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            for (int i = 0; i + 1 < keyValues.Length; i += 2)
            {
                definition.legacyAttributes.Add(
                    new MapObjectAttributeData { key = keyValues[i], value = keyValues[i + 1] });
            }

            return definition;
        }

        /// <summary>A placement with no attributes of its own, backed by <paramref name="definition"/>.</summary>
        private static ObjectInstance ObjectWith(MapObjectDefinition definition)
        {
            return new ObjectInstance { objectId = "test_obj", definition = definition };
        }

        /// <summary>
        /// Replays a fixed sequence of draws and <b>counts them</b>. The count is half the point: an
        /// entry guard that is decided before the roll must not consume the stream, and a fixture that
        /// only checked the outcome could not see the difference.
        /// </summary>
        private sealed class QueueRng : IRngService
        {
            private readonly Queue<float> _values;
            private float _last;

            public QueueRng(params float[] values)
            {
                _values = new Queue<float>(values);
                if (values.Length > 0) _last = values[values.Length - 1];
            }

            /// <summary>How many draws were actually taken.</summary>
            public int Draws { get; private set; }

            public float NextFloat()
            {
                Draws++;
                if (_values.Count > 0) _last = _values.Dequeue();
                return _last;
            }

            public uint NextUInt() => 0u;
            public int NextInt(int maxExclusive) => 0;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public bool Chance(float probability) => false;
            public void Shuffle<T>(IList<T> list) { }
            public IRngService GetStream(RngStream stream, int? salt = null) => this;
        }
    }
}
