using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using UnityEngine;

namespace PFE.Tests.Editor.RPG
{
    /// <summary>
    /// EditMode tests for <see cref="LockAttemptSystem"/> — the consumer that makes
    /// <c>CharacterStats.pinBreak</c>, <c>lockAtt</c>, <c>hackAtt</c>, <c>unlockMaster</c>,
    /// <c>hackerMaster</c> and <c>upChance</c> live.
    ///
    /// <para>
    /// The oracle is <c>Interact.as</c>: the lockpicking/hacking routine at <c>:1000-1210</c> and
    /// <c>getChance()</c> at <c>:1248-1262</c>. Each test states the AS3 line it pins.
    /// </para>
    /// </summary>
    public class LockAttemptSystemTests
    {
        /// <summary>
        /// Minimal scripted <see cref="IRngService"/> so outcomes are exact, not probabilistic.
        /// Every value returned is the same float, so tests control success/failure directly.
        /// </summary>
        private sealed class ScriptedRng : IRngService
        {
            private readonly float _value;
            public ScriptedRng(float value) { _value = value; }

            public uint NextUInt() => (uint)(_value * 65536f);
            public float NextFloat() => _value;
            public int NextInt(int maxExclusive) => (int)(_value * maxExclusive);
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public bool Chance(float probability) => _value < probability;
            public void Shuffle<T>(System.Collections.Generic.IList<T> list) { }
            public IRngService GetStream(RngStream stream, int? salt = null) => this;
        }

        private CharacterStats CreateTestCharacter()
        {
            var go = new GameObject("TestCharacter");
            var stats = go.AddComponent<CharacterStats>();

            var levelCurve = ScriptableObject.CreateInstance<LevelCurve>();
            levelCurve.baseHp = 100;
            levelCurve.hpPerLevel = 15;
            levelCurve.organHpPerLevel = 40;
            levelCurve.baseOrganHp = 200;
            levelCurve.skillPointsPerLevel = 5;

            stats.Initialize(levelCurve);
            return stats;
        }

        private LockAttemptSystem CreateSystem(CharacterStats stats, float rngValue = 0.5f)
        {
            return new LockAttemptSystem(stats, new ScriptedRng(rngValue));
        }

        /// <summary>
        /// A damage pool deep enough that one attempt never opens the lock, so a test can assert on
        /// the *damage dealt*. AS3's pool is <c>Interact.lockHP</c> (<c>Interact.as:52</c>), which is
        /// independent of the container's difficulty (<c>Interact.lock</c>, <c>:44</c>).
        /// </summary>
        private const float DeepPoolHp = 100f;

        /// <summary>
        /// Builds a lock. <paramref name="difficulty"/> is AS3 <c>Interact.lock</c>
        /// (<c>:44</c>) — the 0..99 value the entry guard reads — and <paramref name="hp"/> is AS3
        /// <c>Interact.lockHP</c> (<c>:52</c>), the damage pool. These are separate oracle fields;
        /// an earlier revision of the fixture passed 100 for both and the guard (correctly, but for
        /// the wrong reason) rejected every one of them as an unbreakable lock.
        /// </summary>
        private LockAttemptSystem.LockState CreateLock(int tip = 1, int level = 1, int difficulty = 10, float hp = 10f)
        {
            return new LockAttemptSystem.LockState
            {
                LockTip = tip,
                LockLevel = level,
                Lock = difficulty,
                LockHp = hp,
                LockAtt = -100f,
            };
        }

        // ---------------------------------------------------------------------------------
        // pinBreak
        // ---------------------------------------------------------------------------------

        [Test]
        [Description("AS3 Interact.as:1073 — pinBreak >= 1 must always snap the pin")]
        public void PinBreak_AtOrAboveOne_AlwaysSnapsThePin()
        {
            var stats = CreateTestCharacter();
            stats.pinBreak = 1f;          // the declared default (Pers.as:289)
            stats.lockPick = 5;           // same level as the lock, so the roll is a guaranteed hit
            var state = CreateLock(tip: 1, level: 5, hp: DeepPoolHp);

            var outcome = CreateSystem(stats).Attempt(state, hasPin: true);

            Assert.AreEqual(LockAttemptSystem.AttemptOutcome.PinBroke, outcome,
                "pinBreak >= 1 must break the pin (Interact.as:1073 short-circuits before the roll)");
        }

        [Test]
        [Description("AS3 Interact.as:1073 — a low pinBreak lets the pin survive a failed attempt")]
        public void PinBreak_BelowTheRoll_LeavesThePinIntact()
        {
            var stats = CreateTestCharacter();
            stats.pinBreak = 0.2f;        // the lockpick skill lowers it (AllData.as:5588)
            stats.lockPick = 5;
            var state = CreateLock(tip: 1, level: 5, hp: DeepPoolHp);

            // RNG returns 0.5, which is NOT < 0.2, so the pin survives.
            var outcome = CreateSystem(stats, rngValue: 0.5f).Attempt(state, hasPin: true);

            Assert.AreEqual(LockAttemptSystem.AttemptOutcome.Failed, outcome,
                "pinBreak 0.2 with a 0.5 roll must leave the pin intact");
        }

        [Test]
        [Description("AS3 Interact.as:1079-1081 — with no pin carried the damage gains +2")]
        public void NoPinCarried_AddsTwoToTheDamage()
        {
            var stats = CreateTestCharacter();
            stats.pinBreak = 1f;
            stats.lockPick = 5;           // at-level: _loc3_ base is 0, spread 2

            var withPin = CreateLock(tip: 1, level: 5, hp: DeepPoolHp);
            var noPin = CreateLock(tip: 1, level: 5, hp: DeepPoolHp);

            var system = CreateSystem(stats, rngValue: 0f);

            system.Attempt(withPin, hasPin: true);
            system.Attempt(noPin, hasPin: false);

            float pinDamage = DeepPoolHp - withPin.LockHp;
            float noPinDamage = DeepPoolHp - noPin.LockHp;
            Assert.AreEqual(pinDamage + 2f, noPinDamage, 0.0001f,
                "Carrying no pin must add exactly +2 to the damage (Interact.as:1081)");
        }

        // ---------------------------------------------------------------------------------
        // lockAtt — the progress multiplier
        // ---------------------------------------------------------------------------------

        [Test]
        [Description("AS3 Interact.as:1083 — pers.lockAtt multiplies the progress")]
        public void LockAtt_ScalesTheProgressDirectly()
        {
            var stats = CreateTestCharacter();
            stats.pinBreak = 0.2f;        // avoid a pin-break short-circuit on the outcome
            stats.lockPick = 5;

            var normal = CreateLock(tip: 1, level: 5, hp: DeepPoolHp);
            var boosted = CreateLock(tip: 1, level: 5, hp: DeepPoolHp);

            var system = CreateSystem(stats, rngValue: 0.5f);

            stats.lockAtt = 1f;
            system.Attempt(normal, hasPin: true);

            stats.lockAtt = 3f;
            system.Attempt(boosted, hasPin: true);

            float normalDamage = 100f - normal.LockHp;
            float boostedDamage = 100f - boosted.LockHp;

            Assert.Greater(normalDamage, 0f, "The attempt should have dealt some damage");
            Assert.AreEqual(normalDamage * 3f, boostedDamage, 0.0001f,
                "lockAtt must multiply the damage exactly (Interact.as:1083)");
        }

        // ---------------------------------------------------------------------------------
        // hackAtt / the terminal attempt counter
        // ---------------------------------------------------------------------------------

        [Test]
        [Description("AS3 Interact.as:1112-1114 — a terminal seeds its counter from pers.hackAtt")]
        public void Terminal_SeedsItsCounterFromHackAtt()
        {
            var stats = CreateTestCharacter();
            stats.hackAtt = 3;            // the declared default (Pers.as:299)
            stats.hacker = 5;
            var state = CreateLock(tip: 2, level: 5, hp: DeepPoolHp);

            var system = CreateSystem(stats, rngValue: 0f);
            system.Attempt(state);

            // First attempt must seed from hackAtt (3) then decrement to 2.
            Assert.AreEqual(2f, state.LockAtt, 0.0001f,
                "A terminal's counter must seed from pers.hackAtt and then decrement");
        }

        [Test]
        [Description("AS3 Interact.as:1117-1136 — the terminal counts down to a lock-out")]
        public void Terminal_CountsDownAndLocksOut()
        {
            var stats = CreateTestCharacter();
            stats.hackAtt = 3;
            stats.hacker = 5;
            var state = CreateLock(tip: 2, level: 5, hp: DeepPoolHp);

            var system = CreateSystem(stats, rngValue: 0f);

            // 3 -> 2 (Failed), 2 -> 1 (Failed), 1 -> 0 (locked out)
            Assert.AreEqual(LockAttemptSystem.AttemptOutcome.Failed, system.Attempt(state));
            Assert.AreEqual(LockAttemptSystem.AttemptOutcome.Failed, system.Attempt(state));
            Assert.AreEqual(LockAttemptSystem.AttemptOutcome.TerminalLockedOut, system.Attempt(state),
                "hackAtt 3 must allow exactly three attempts before locking out");
        }

        [Test]
        [Description("A higher hackAtt grants more terminal attempts")]
        public void Terminal_HigherHackAtt_GrantsMoreAttempts()
        {
            var stats = CreateTestCharacter();
            stats.hackAtt = 6;            // the hacker skill can raise it (AllData.as:5608)
            stats.hacker = 5;
            var state = CreateLock(tip: 2, level: 5, hp: DeepPoolHp);

            var system = CreateSystem(stats, rngValue: 0f);

            for (int i = 0; i < 5; i++)
            {
                var outcome = system.Attempt(state);
                Assert.AreNotEqual(LockAttemptSystem.AttemptOutcome.TerminalLockedOut, outcome,
                    $"Attempt {i + 1} of 6 must not lock out yet");
            }

            Assert.AreEqual(LockAttemptSystem.AttemptOutcome.TerminalLockedOut, system.Attempt(state),
                "hackAtt 6 must allow exactly six attempts");
        }

        // ---------------------------------------------------------------------------------
        // upChance — the better table
        // ---------------------------------------------------------------------------------

        [Test]
        [Description("AS3 Interact.as:1256-1262 — upChance swaps in the better chanceUnlock2 table")]
        public void UpChance_UsesTheBetterTable()
        {
            var stats = CreateTestCharacter();
            var system = CreateSystem(stats);

            // delta 0 is the middle of both tables: chanceUnlock[2] = 0.5, chanceUnlock2[2] = 0.55
            stats.upChance = 0f;
            Assert.AreEqual(0.5f, system.GetChance(0), 0.0001f,
                "Without upChance the chanceUnlock table applies");

            stats.upChance = 1f;
            Assert.AreEqual(0.55f, system.GetChance(0), 0.0001f,
                "With upChance > 0 the chanceUnlock2 table applies (Interact.as:1256)");
        }

        [Test]
        [Description("AS3 Interact.as:1250-1254 — the chance table clamps outside [-2, 4]")]
        public void GetChance_ClampsOutsideTheTable()
        {
            var stats = CreateTestCharacter();
            var system = CreateSystem(stats);

            Assert.AreEqual(1f, system.GetChance(-3), 0.0001f, "Below the table the chance is 1");
            Assert.AreEqual(0f, system.GetChance(5), 0.0001f, "Above the table the chance is 0");
        }

        // ---------------------------------------------------------------------------------
        // unlockMaster / hackerMaster drive the container gate
        // ---------------------------------------------------------------------------------

        [Test]
        [Description("AS3 Interact.as:1010-1021 — a master level below the lock level guarantees an attempt")]
        public void MasterBelowLockLevel_GuaranteesTheAttempt()
        {
            var stats = CreateTestCharacter();
            stats.lockPick = 0;           // far below the lock, so the roll would normally miss
            stats.pinBreak = 0.2f;

            var state = CreateLock(tip: 1, level: 4, hp: DeepPoolHp);
            state.Master = 1;             // 1 < 4, so the attempt must always fire

            // RNG 0.99 would fail a normal roll, but the master gate forces chance to 1.
            var outcome = CreateSystem(stats, rngValue: 0.99f).Attempt(state, hasPin: true);

            Assert.AreNotEqual(LockAttemptSystem.AttemptOutcome.Missed, outcome,
                "master < lockLevel must force the attempt regardless of the roll");
        }

        [TearDown]
        public void TearDown()
        {
            var testCharacters = GameObject.FindObjectsByType<CharacterStats>(FindObjectsSortMode.None);
            foreach (var obj in testCharacters)
            {
                if (obj.gameObject.name.StartsWith("TestCharacter"))
                {
                    GameObject.DestroyImmediate(obj.gameObject);
                }
            }
        }
    }
}
