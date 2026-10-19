using NUnit.Framework;
using PFE.Systems.Items;
using PFE.Systems.RPG;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.RPG
{
    /// <summary>
    /// Pins the stat-engine half of the held-item grant: the <c>ApplyNamedStat</c> calls that
    /// <c>RecalculateStats</c> step 5.5 makes for each held item's <c>&lt;sk&gt;</c> rows, and the
    /// <c>GetLockTip(1)</c> gate those calls feed.
    ///
    /// <para><b>The reported bug:</b> "I picked up the screwdriver and it did not give me lockpicking
    /// ability" (<c>[Security] chest: noPoss — no lockpicking ability</c>). The oracle's chain is
    /// <c>Pers.setInvParameters</c> (Pers.as:2290-2307) → <c>setSkillParam(itemXml, 1)</c> →
    /// <c>Pers.possLockPick = 1</c> → <c>getLockTip(1)</c> stops answering −100 (Pers.as:2314-2323).
    /// The port had the field, the <c>case "possLockPick"</c>, and the gate — but nothing applied an
    /// ITEM's rows. These tests prove the engine side answers the way AS3 does once the rows arrive;
    /// <see cref="PFE.Tests.EditMode.Systems.Items.ItemSkillRowsTests"/> proves the rows themselves
    /// arrive parsed correctly.</para>
    ///
    /// <para><b>The ref semantics are the sharp edge.</b> <c>&lt;sk id='possLockPick' v1='1'/&gt;</c>
    /// carries no <c>ref</c>, and AS3's fall-through for an absent ref is an ASSIGNMENT
    /// (Pers.as:1554-1556) — which is exactly what makes the rebuild model work: the next recalc after
    /// dropping the screwdriver re-zeroes the grant, with no undo path. Reading it as an add would make
    /// the grant permanent and let two screwdrivers stack to 2.</para>
    /// </summary>
    public class ItemSkillGrantTests
    {
        private CharacterStats _stats;

        [SetUp]
        public void SetUp()
        {
            _stats = new GameObject("stats").AddComponent<CharacterStats>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_stats.gameObject);
        }

        /// <summary>A single helper so every test drives the exact call step 5.5 makes.</summary>
        private static void ApplyRow(CharacterStats stats, PersSkillRow row, string itemId)
        {
            string refType = row.Op == SkillRowOp.Add ? "add"
                : row.Op == SkillRowOp.Mult ? "mult"
                : null;
            stats.ApplyNamedStat(row.Id, row.Tip, refType, row.ValueFor(1), itemId, "item");
        }

        private static PersSkillRow PossLockPickRow() => new PersSkillRow
        {
            Id = "possLockPick", Op = SkillRowOp.Set, V1 = 1f, HasV1 = true
        };

        /// <summary>titansd's second row (AllData.as:4112) — the screwdriver itself has no such row.</summary>
        private static PersSkillRow LockPickAddRow() => new PersSkillRow
        {
            Id = "lockPick", Op = SkillRowOp.Add, V1 = 1f, HasV1 = true
        };

        [Test]
        public void CarryingTheScrewdriver_OpensTheLockTipGate()
        {
            Assert.AreEqual(-100, _stats.GetLockTip(1),
                "the oracle answers -100 with no tool (Pers.as:2314-2323) — the noPoss the player saw.");

            ApplyRow(_stats, PossLockPickRow(), "screwdriver");

            // possLockPick = 1, lockPick = 0 → getLockTip(1) answers lockPick itself, i.e. 0. The
            // SECURITY gate reads `<= -99` as "no ability", so 0 opens the gate with no rank bonus —
            // exactly AS3's behaviour for a bare screwdriver.
            Assert.AreEqual(0, _stats.GetLockTip(1));
        }

        [Test]
        public void Titansd_RankBonus_AddsToOne()
        {
            ApplyRow(_stats, PossLockPickRow(), "titansd");
            ApplyRow(_stats, LockPickAddRow(), "titansd");

            Assert.AreEqual(1, _stats.GetLockTip(1),
                "titansd = screwdriver's grant + lockPick ref='add' v1='1' (AllData.as:4110-4113).");
        }

        [Test]
        public void TwoScrewdrivers_DoNotStack_AnAbsentRefIsAnAssignment()
        {
            ApplyRow(_stats, PossLockPickRow(), "screwdriver");
            ApplyRow(_stats, PossLockPickRow(), "screwdriver");

            Assert.AreEqual(0, _stats.GetLockTip(1),
                "possLockPick must be SET to 1 twice, not added to 2 — the gate only tests > 0, but " +
                "the TrackFactor table reads the value, and AS3 assigns.");
        }

        [Test]
        public void DroppingTheScrewdriver_ClearsTheGrant_OnTheNextRecalcRebuild()
        {
            ApplyRow(_stats, PossLockPickRow(), "screwdriver");
            Assert.AreEqual(0, _stats.GetLockTip(1));

            // The recalc step that mirrors dropping the item: ResetToDefaults zeroes possLockPick
            // (Pers.as:931) and step 5.5 simply never re-applies the row. There is no undo call —
            // this assignment stands in for the rebuild; it asserts the sentinel the F2 tab and the
            // security gate then both see.
            _stats.ApplyNamedStat("possLockPick", null, null, 0f, "reset", "reset");

            Assert.AreEqual(-100, _stats.GetLockTip(1));
        }

        [Test]
        public void Stethoscope_Alone_GrantsTheRankButNotTheAbility()
        {
            // stethoscope carries ONLY `lockPick ref='add' v1='1'` (AllData.as:4114-4117). AS3's
            // getLockTip(1) still answers -100 — a rank bonus without possLockPick opens nothing.
            // This is the control that pins the gate to possLockPick and not to lockPick.
            ApplyRow(_stats, LockPickAddRow(), "stethoscope");

            Assert.AreEqual(-100, _stats.GetLockTip(1));
        }

        [Test]
        public void Bag1_TipM_IsAForcedAdd_IgnoringRef()
        {
            // AS3 tip='m' means this[id] += v with the ref attribute IGNORED (Pers.as:1522-1525). The
            // consumer forces refType to "add" for it — bag1's maxm2 500 must land as an addition even
            // though the row itself carries no ref (which alone would mean an assignment).
            _stats.ApplyNamedStat("maxm2", "m", null, 500f, "bag1", "item");

            // The field's declaration default is 1000 (CharacterStats, mirroring Pers.as's maxm2), so
            // a forced ADD lands at 1500 — an assignment would have produced 500.
            Assert.AreEqual(1500f, _stats.maxm2);
        }
    }
}
