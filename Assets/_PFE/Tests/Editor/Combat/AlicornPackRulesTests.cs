using NUnit.Framework;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the alicorn's <b>pack</b> behaviours — <c>telepat()</c> (<c>UnitAlicorn.as:403-413</c>),
    /// the <c>budilo()</c> override (<c>:386-401</c>), <c>shok</c>'s contact multiplier (<c>:448</c>,
    /// <c>:1008-1013</c>), <c>overLook</c> (<c>:870</c>/<c>:882</c>/<c>:933</c>) and the <c>t_alarm</c>
    /// reinforcement (<c>:119</c>, <c>:579-586</c>).
    ///
    /// <para><b>Why these are rules and not brain tests.</b> <c>AlicornBrain</c> is a
    /// <c>MonoBehaviour</c>, so an offline fixture cannot construct one; every predicate below is a pure
    /// function for that reason. What the fixture <i>cannot</i> see is the loops that call them — the
    /// scene walk, the faction filter's wiring, whether the shout actually lands on a sibling. Those
    /// need a play-test and are recorded as owed rather than claimed here.</para>
    /// </summary>
    [TestFixture]
    public class AlicornPackRulesTests
    {
        // ── telepat() (:403-413) ─────────────────────────────────────────────────────────────────

        [Test]
        public void Telepat_AnAllyWithNoTargetReceivesOne()
        {
            Assert.IsTrue(AlicornPackRules.CanReceiveTelepathy(
                isSelf: false, sameFaction: true, isAlive: true, alreadyHasTarget: false));
        }

        [Test]
        public void Telepat_RefusesSelf_WrongFaction_Corpse_AndAnAllyThatAlreadyHasATarget()
        {
            // `_loc1_ != this && fraction == fraction && sost == 1 && !unres && celUnit == null`.
            Assert.IsFalse(AlicornPackRules.CanReceiveTelepathy(true, true, true, false), "self");
            Assert.IsFalse(AlicornPackRules.CanReceiveTelepathy(false, false, true, false), "other faction");
            Assert.IsFalse(AlicornPackRules.CanReceiveTelepathy(false, true, false, false), "corpse");

            // The last term is the whole behaviour: a neighbour already chasing keeps its own target,
            // which is what stops two alicorns trading objectives every thirty ticks.
            Assert.IsFalse(AlicornPackRules.CanReceiveTelepathy(false, true, true, true),
                "an ally that already holds a target must keep it");
        }

        [Test]
        public void Telepat_FiresOnTheThirtyTickPhase_AndOnlyWhenTheSharerHasATarget()
        {
            // `if(aiTCh % 30 == 1) this.telepat();`
            Assert.IsTrue(AlicornPackRules.ShouldShareTarget(aiCheckTicks: 1, sharerHasTarget: true));
            Assert.IsTrue(AlicornPackRules.ShouldShareTarget(aiCheckTicks: 31, sharerHasTarget: true));
            Assert.IsTrue(AlicornPackRules.ShouldShareTarget(aiCheckTicks: 61, sharerHasTarget: true));

            Assert.IsFalse(AlicornPackRules.ShouldShareTarget(1, false), "no target to share");
            Assert.IsFalse(AlicornPackRules.ShouldShareTarget(30, true), "off-phase");
            Assert.IsFalse(AlicornPackRules.ShouldShareTarget(32, true), "off-phase");
            Assert.IsFalse(AlicornPackRules.ShouldShareTarget(0, true), "aiTCh 0 is the 'expired' value");
        }

        // ── budilo() (:386-401) ──────────────────────────────────────────────────────────────────

        [Test]
        public void Budilo_HasNoDistanceAndNoHearingGate()
        {
            // The base class's `Unit.budilo` refuses on `dist² >= radius² * ear²`. The alicorn's override
            // drops both terms, so its predicate has no distance or ear parameter at all — a deaf ally
            // across the room is as reachable as a sibling standing beside it.
            Assert.IsTrue(AlicornPackRules.AlarmEligible(isSelf: false, sameFaction: true, isAlive: true));
            Assert.IsFalse(AlicornPackRules.AlarmEligible(true, true, true), "self");
            Assert.IsFalse(AlicornPackRules.AlarmEligible(false, false, true), "other faction");
            Assert.IsFalse(AlicornPackRules.AlarmEligible(false, true, false), "corpse");
        }

        [Test]
        public void Budilo_CarriesTheExactAimPoint_NotAJitteredOne()
        {
            AlicornPackRules.ShoutPosition(true, 812f, 604f, 100f, 100f, out float x, out float y);

            // Exact equality, and that is the assertion: `alarma(celX, celY)` passes the aim point
            // through untouched, unlike the base class's `alarma(X + (random-0.5)*250, …)`. A port that
            // "restored" the spread would fail here, which is the point.
            Assert.AreEqual(812f, x);
            Assert.AreEqual(604f, y);
        }

        [Test]
        public void Budilo_FallsBackToItsOwnFeetWhenItHasNoTarget()
        {
            // `if(celUnit == null) { celX = X; celY = Y; }` (:389-393).
            AlicornPackRules.ShoutPosition(false, 812f, 604f, 100f, 120f, out float x, out float y);
            Assert.AreEqual(100f, x);
            Assert.AreEqual(120f, y);
        }

        // ── shok (:448, :1010) ───────────────────────────────────────────────────────────────────

        [Test]
        public void Shok_HalvesContactDamageOnlyWhileItIsRunning()
        {
            Assert.AreEqual(1f, AlicornPackRules.ContactDamageScale(0), "not startled");
            Assert.AreEqual(0.5f, AlicornPackRules.ContactDamageScale(1), "startled");
            Assert.AreEqual(0.5f, AlicornPackRules.ContactDamageScale(19), "top of the 5..19 roll");
        }

        [Test]
        public void ContactWindow_IsTwoHundredPixelsWide_Exclusive()
        {
            // `celDX < optDistAtt && celDX > -optDistAtt` with `optDistAtt:int = 200` (:95).
            Assert.IsTrue(AlicornPackRules.InContactReachX(0f));
            Assert.IsTrue(AlicornPackRules.InContactReachX(199.9f));
            Assert.IsTrue(AlicornPackRules.InContactReachX(-199.9f));
            Assert.IsFalse(AlicornPackRules.InContactReachX(200f), "strictly less than");
            Assert.IsFalse(AlicornPackRules.InContactReachX(-200f), "strictly greater than");
            Assert.AreEqual(200f, AlicornPackRules.ContactReachPixels);
        }

        // ── overLook (:870, :882, :933) ──────────────────────────────────────────────────────────

        [Test]
        public void OverLook_IsOnWhileAware_AndOffWhileCalm()
        {
            Assert.IsFalse(AlicornPackRules.LooksAround(0), "idle keeps the cone");
            Assert.IsFalse(AlicornPackRules.LooksAround(1), "patrol keeps the cone");
            Assert.IsTrue(AlicornPackRules.LooksAround(2), "alert sees 360");
            Assert.IsTrue(AlicornPackRules.LooksAround(3), "chase sees 360");
            Assert.IsFalse(AlicornPackRules.LooksAround(4), "the descent is not one of the two aware arms");
        }

        // ── t_alarm (:119, :579-586) ─────────────────────────────────────────────────────────────

        [Test]
        public void ReinforcementTimer_IsRolledInTheOraclesRange()
        {
            // `Math.round(Math.random() * 600 + 1500)`.
            Assert.AreEqual(1500, AlicornPackRules.RollAlarmSpawnTicks(0f));
            Assert.AreEqual(1800, AlicornPackRules.RollAlarmSpawnTicks(0.5f));
            Assert.AreEqual(2100, AlicornPackRules.RollAlarmSpawnTicks(0.9999999f));
            Assert.AreEqual(1500, AlicornPackRules.RollAlarmSpawnTicks(-1f), "clamped low");
            Assert.AreEqual(2100, AlicornPackRules.RollAlarmSpawnTicks(1f), "clamped high");
        }

        [Test]
        public void ReinforcementTimer_OnlyRunsWhileSearching()
        {
            Assert.IsTrue(AlicornPackRules.ShouldCallReinforcements(2, 1500), "state 2 — searching");
            Assert.IsFalse(AlicornPackRules.ShouldCallReinforcements(3, 1500),
                "the chase does NOT spend the countdown: alarma() promotes to 3, so a pack that was " +
                "startled into combat never calls for help");
            Assert.IsFalse(AlicornPackRules.ShouldCallReinforcements(0, 1500));
            Assert.IsFalse(AlicornPackRules.ShouldCallReinforcements(1, 1500));
        }

        [Test]
        public void Reinforcement_FiresOnExactlyZero_Once()
        {
            Assert.IsTrue(AlicornPackRules.ReinforcementDue(0));
            Assert.IsFalse(AlicornPackRules.ReinforcementDue(1));
            Assert.IsFalse(AlicornPackRules.ReinforcementDue(-1), "already spent");
        }

        [Test]
        public void Reinforcement_IsHardCodedToTheAlicornFamily()
        {
            // `loc.enemySpawn(false, false, "alicorn")` — not the room's spawn group, and not the
            // caller's tier: createUnit runs randomCid("alicorn"), a fresh 1..3 roll.
            Assert.AreEqual("alicorn", AlicornPackRules.ReinforcementFamilyId);
        }
    }
}
