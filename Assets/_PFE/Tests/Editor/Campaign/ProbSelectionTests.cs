using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Map.Generation;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// <see cref="ProbSelection"/> against the oracle's rules (<c>Land.newRandomProb</c>,
    /// <c>Land.as:809-863</c>).
    ///
    /// <para><b>Why this fixture can run outside the editor and most campaign tests cannot.</b>
    /// <see cref="ProbRoomDefinition"/> is a plain <c>[Serializable]</c> class, so a case can build one
    /// without touching Unity. The fixtures that go through <c>CampaignDataParser.ParseLands</c> cannot:
    /// that calls <c>ScriptableObject.CreateInstance</c>, which raises
    /// <c>ECall methods must be packaged into a system module</c> in a plain shell. So the <i>rules</i>
    /// are pinned here, where they are cheap to run, and the <i>data</i> is pinned in
    /// <c>CampaignDataTests</c>, where it has to be.</para>
    ///
    /// <para><b>Every case names the oracle line it encodes.</b> A rule that cannot be traced back to a
    /// line is a rule someone invented.</para>
    /// </summary>
    [TestFixture]
    public class ProbSelectionTests
    {
        private static ProbRoomDefinition Prob(string id, int? level = null, bool imp = false, string tip = "1")
        {
            return new ProbRoomDefinition
            {
                id = id,
                hasLevel = level.HasValue,
                level = level ?? 0,
                imp = imp,
                tip = tip,
            };
        }

        // =====================================================================
        //  Level: absent is not 0  (Land.as:821)
        // =====================================================================

        /// <summary>
        /// A prob with no <c>level</c> is offered at every stage; one with <c>level='0'</c> is offered
        /// only when <c>maxlevel &gt;= 0</c>.
        ///
        /// <para><b>The trap.</b> The oracle tests <c>xml.@level.length == 0 || xml.@level &lt;= maxlevel</c>.
        /// Collapsing the two — storing <c>level = 0</c> for an absent attribute — makes the two rows below
        /// indistinguishable, and the 8 probs that legitimately carry no level start behaving like the 23
        /// that carry <c>level='0'</c>.</para>
        /// </summary>
        [Test]
        public void Level_AbsentIsEligibleAtEveryStage_PresentIsACeiling()
        {
            var absent = Prob("noLevel");
            var zero = Prob("levelZero", level: 0);
            var two = Prob("levelTwo", level: 2);

            var probs = new List<ProbRoomDefinition> { absent, zero, two };

            // maxlevel = -1: only the absent one survives.
            var atMinusOne = ProbSelection.Eligible(probs, maxLevel: -1);
            CollectionAssert.AreEqual(new[] { "noLevel" }, Ids(atMinusOne),
                "level='0' must NOT be treated as 'no level': at maxlevel -1 it is out of range, " +
                "while a prob with no level attribute is always in range.");

            // maxlevel = 0: absent and level 0.
            CollectionAssert.AreEqual(new[] { "noLevel", "levelZero" },
                Ids(ProbSelection.Eligible(probs, maxLevel: 0)));

            // maxlevel = 1: still not the level-2 one.
            CollectionAssert.AreEqual(new[] { "noLevel", "levelZero" },
                Ids(ProbSelection.Eligible(probs, maxLevel: 1)));

            // maxlevel = 2: all three.
            CollectionAssert.AreEqual(new[] { "noLevel", "levelZero", "levelTwo" },
                Ids(ProbSelection.Eligible(probs, maxLevel: 2)));
        }

        /// <summary>
        /// A level exactly at <c>maxlevel</c> is eligible — the oracle's comparison is <c>&lt;=</c>.
        /// </summary>
        [Test]
        public void Level_EqualToMaxLevel_IsEligible()
        {
            var probs = new List<ProbRoomDefinition> { Prob("exact", level: 3) };
            Assert.AreEqual(1, ProbSelection.Eligible(probs, maxLevel: 3).Count);
            Assert.AreEqual(0, ProbSelection.Eligible(probs, maxLevel: 2).Count);
        }

        // =====================================================================
        //  Already built / already completed  (Land.as:821)
        // =====================================================================

        /// <summary>
        /// A prob already built this run is not offered again (<c>this.probs[id] != null</c>).
        /// </summary>
        [Test]
        public void Built_ProbsAreNotOfferedAgain()
        {
            var probs = new List<ProbRoomDefinition> { Prob("a"), Prob("b") };
            var built = new HashSet<string> { "a" };

            CollectionAssert.AreEqual(new[] { "b" }, Ids(ProbSelection.Eligible(probs, 0, built)));
        }

        /// <summary>
        /// A completed prob never returns, and the completion key is <c>prob_&lt;id&gt;</c> — the same key
        /// <c>Probation.closeProb</c> writes (<c>Probation.as:201-208</c>).
        ///
        /// <para>Also pins that the counter is read as "present", not as "truthy": AS3 tests
        /// <c>triggers[key] == null</c>, so a counter of <c>1</c> and a counter of <c>7</c> both block the
        /// prob. A test that only supplied a <c>0</c> would not distinguish "absent" from "zero".</para>
        /// </summary>
        [Test]
        public void Completed_ProbsNeverReturn_KeyedOnProbUnderscoreId()
        {
            var probs = new List<ProbRoomDefinition> { Prob("a"), Prob("b") };

            Assert.AreEqual("prob_a", ProbSelection.TriggerKey("a"),
                "The key must be 'prob_' + id — that is what Probation.closeProb writes.");

            var completed = new HashSet<string> { ProbSelection.TriggerKey("a") };
            CollectionAssert.AreEqual(new[] { "b" }, Ids(ProbSelection.Eligible(probs, 0, null, completed)));

            // A bare id must NOT match: the prefix is part of the key.
            var wrongKey = new HashSet<string> { "a" };
            CollectionAssert.AreEqual(new[] { "a", "b" },
                Ids(ProbSelection.Eligible(probs, 0, null, wrongKey)),
                "Filtering on the bare id would silently match nothing, which reads as 'nothing completed'.");
        }

        /// <summary>
        /// Null collections mean "nothing built, nothing completed" — the fresh-run state. They must not
        /// be read as "everything is built".
        /// </summary>
        [Test]
        public void NullBuiltAndCompleted_MeansNothingBuiltAndNothingCompleted()
        {
            var probs = new List<ProbRoomDefinition> { Prob("a"), Prob("b") };
            CollectionAssert.AreEqual(new[] { "a", "b" }, Ids(ProbSelection.Eligible(probs, 0)));
        }

        // =====================================================================
        //  Choice: imp preference, single candidate, roll  (Land.as:834-846)
        // =====================================================================

        /// <summary>
        /// With <c>imp</c> requested and an eligible imp present, that prob wins outright.
        /// </summary>
        [Test]
        public void Imp_Requested_TakesTheImpProb_AndSpendsNoRoll()
        {
            var probs = new List<ProbRoomDefinition>
            {
                Prob("a", tip: "1"),
                Prob("impOne", imp: true, tip: "2"),
                Prob("c", tip: "1"),
            };

            List<ProbRoomDefinition> eligible = ProbSelection.Eligible(probs, 0);

            Assert.IsFalse(ProbSelection.NeedsRoll(eligible, wantImp: true),
                "The imp branch is taken before the roll, so it must not consume a draw.");

            ProbSelection.Choice choice = ProbSelection.SelectFrom(eligible, wantImp: true);
            Assert.AreEqual("impOne", choice.probId);
            Assert.AreEqual(ProbSelection.DoorBossObjectId, choice.doorObjectId,
                "impOne carries tip='2', so it is a boss door.");
        }

        /// <summary>
        /// <c>imp</c> requested but no imp eligible falls through to the roll — it does not fail.
        /// </summary>
        [Test]
        public void Imp_Requested_WithNoImpEligible_FallsThroughToTheRoll()
        {
            var probs = new List<ProbRoomDefinition> { Prob("a"), Prob("b") };
            List<ProbRoomDefinition> eligible = ProbSelection.Eligible(probs, 0);

            Assert.IsTrue(ProbSelection.NeedsRoll(eligible, wantImp: true));
            Assert.AreEqual("a", ProbSelection.SelectFrom(eligible, wantImp: true, rollIndex: 0).probId);
            Assert.AreEqual("b", ProbSelection.SelectFrom(eligible, wantImp: true, rollIndex: 1).probId);
        }

        /// <summary>
        /// <b>The oracle keeps the last imp, not the first</b> (<c>Land.as:824-827</c> assigns on every
        /// match while scanning). With two imps the later one wins.
        /// </summary>
        [Test]
        public void Imp_Requested_WithTwoImps_TakesTheLastOne()
        {
            var probs = new List<ProbRoomDefinition>
            {
                Prob("firstImp", imp: true),
                Prob("middle"),
                Prob("secondImp", imp: true),
            };

            List<ProbRoomDefinition> eligible = ProbSelection.Eligible(probs, 0);
            Assert.AreEqual("secondImp", ProbSelection.PreferredImpId(eligible));
            Assert.AreEqual("secondImp", ProbSelection.SelectFrom(eligible, wantImp: true).probId);
        }

        /// <summary>
        /// A single eligible prob is taken without a roll, and — because it is taken without a roll — the
        /// stream is not advanced.
        /// </summary>
        [Test]
        public void SingleEligibleProb_IsTakenWithoutARoll()
        {
            var probs = new List<ProbRoomDefinition> { Prob("only") };
            List<ProbRoomDefinition> eligible = ProbSelection.Eligible(probs, 0);

            Assert.IsFalse(ProbSelection.NeedsRoll(eligible, wantImp: false),
                "One candidate means no draw is spent (Land.as:839-842).");

            // A nonsense roll must not change the answer.
            Assert.AreEqual("only", ProbSelection.SelectFrom(eligible, wantImp: false, rollIndex: 99).probId);
        }

        /// <summary>
        /// With two or more candidates a roll is spent, and the index selects in document order.
        /// </summary>
        [Test]
        public void MultipleEligible_RollSelectsInDocumentOrder()
        {
            var probs = new List<ProbRoomDefinition> { Prob("a"), Prob("b"), Prob("c") };
            List<ProbRoomDefinition> eligible = ProbSelection.Eligible(probs, 0);

            Assert.IsTrue(ProbSelection.NeedsRoll(eligible, wantImp: false));
            Assert.AreEqual("a", ProbSelection.SelectFrom(eligible, wantImp: false, rollIndex: 0).probId);
            Assert.AreEqual("b", ProbSelection.SelectFrom(eligible, wantImp: false, rollIndex: 1).probId);
            Assert.AreEqual("c", ProbSelection.SelectFrom(eligible, wantImp: false, rollIndex: 2).probId);
        }

        /// <summary>
        /// An out-of-range roll wraps rather than clamping. Clamping would bias the last entry for any
        /// caller that passed a raw <c>NextInt</c> over a wider range.
        /// </summary>
        [Test]
        public void RollIndex_OutOfRange_WrapsRatherThanClamping()
        {
            var probs = new List<ProbRoomDefinition> { Prob("a"), Prob("b"), Prob("c") };
            List<ProbRoomDefinition> eligible = ProbSelection.Eligible(probs, 0);

            Assert.AreEqual("a", ProbSelection.SelectFrom(eligible, false, rollIndex: 3).probId);
            Assert.AreEqual("b", ProbSelection.SelectFrom(eligible, false, rollIndex: 4).probId);
            Assert.AreEqual("c", ProbSelection.SelectFrom(eligible, false, rollIndex: 5).probId);
            Assert.AreEqual("c", ProbSelection.SelectFrom(eligible, false, rollIndex: -1).probId,
                "A negative index must land on the last entry, not throw or wrap to the first.");
        }

        // =====================================================================
        //  doorprob vs doorboss  (Land.as:847-856)
        // =====================================================================

        /// <summary>
        /// Only <c>tip == "2"</c> makes a boss door. Everything else — <c>'1'</c>, <c>'0'</c>, or no
        /// <c>tip</c> at all — is a plain prob door.
        ///
        /// <para><b>This is the assertion that keeps the two axes apart.</b> It is tempting to read
        /// <c>imp</c> as "boss", because both the imp probs and most <c>tip='2'</c> probs are boss rooms.
        /// They are independent: the data has 2 imps and 34 <c>tip='2'</c> probs.</para>
        /// </summary>
        [Test]
        public void DoorKind_ComesFromTip_NotFromImp()
        {
            Assert.AreEqual(ProbSelection.DoorBossObjectId, SelectSingle(Prob("x", tip: "2")).doorObjectId);
            Assert.IsTrue(SelectSingle(Prob("x", tip: "2")).isBoss);

            Assert.AreEqual(ProbSelection.DoorProbObjectId, SelectSingle(Prob("x", tip: "1")).doorObjectId);
            Assert.AreEqual(ProbSelection.DoorProbObjectId, SelectSingle(Prob("x", tip: "0")).doorObjectId);
            Assert.AreEqual(ProbSelection.DoorProbObjectId, SelectSingle(Prob("x", tip: "")).doorObjectId,
                "No tip attribute means tip is unset, which is not '2'.");

            // imp without tip='2' is still a plain prob door.
            ProbSelection.Choice impOnly = SelectSingle(Prob("x", imp: true, tip: "1"));
            Assert.AreEqual(ProbSelection.DoorProbObjectId, impOnly.doorObjectId,
                "imp selects *which* prob, never *what kind* of door.");

            // …and tip='2' without imp is still a boss door.
            Assert.AreEqual(ProbSelection.DoorBossObjectId, SelectSingle(Prob("x", tip: "2")).doorObjectId);
        }

        private static ProbSelection.Choice SelectSingle(ProbRoomDefinition prob)
        {
            return ProbSelection.SelectFrom(new List<ProbRoomDefinition> { prob }, wantImp: false);
        }

        // =====================================================================
        //  Nothing to offer  (Land.as:830-833)
        // =====================================================================

        /// <summary>
        /// A land with nothing left returns <c>null</c> — AS3 returns <c>false</c> and places no door. It
        /// must not fall back to an arbitrary prob, and it must not throw.
        /// </summary>
        [Test]
        public void NothingEligible_ReturnsNull_AndSpendsNoRoll()
        {
            Assert.IsNull(ProbSelection.Select(new List<ProbRoomDefinition>(), 5, false));
            Assert.IsNull(ProbSelection.Select(null, 5, false));

            var allBuilt = new List<ProbRoomDefinition> { Prob("a") };
            var built = new HashSet<string> { "a" };
            Assert.IsNull(ProbSelection.Select(allBuilt, 5, false, built));

            Assert.IsFalse(ProbSelection.NeedsRoll(new List<ProbRoomDefinition>(), false));
        }

        /// <summary>
        /// A prob with no <c>id</c> is skipped rather than becoming a door to nowhere — the door's
        /// <c>prob</c> attribute is the lookup key for the room.
        /// </summary>
        [Test]
        public void ProbWithoutAnId_IsSkipped()
        {
            var probs = new List<ProbRoomDefinition> { Prob(""), Prob("real") };
            CollectionAssert.AreEqual(new[] { "real" }, Ids(ProbSelection.Eligible(probs, 0)));
        }

        /// <summary>
        /// The eligible list keeps the land's document order, because the roll indexes it. Reordering it
        /// would silently re-roll every later decision in the land.
        /// </summary>
        [Test]
        public void Eligible_PreservesDocumentOrder()
        {
            var probs = new List<ProbRoomDefinition>
            {
                Prob("zebra"), Prob("alpha"), Prob("middle"),
            };

            CollectionAssert.AreEqual(new[] { "zebra", "alpha", "middle" },
                Ids(ProbSelection.Eligible(probs, 0)),
                "Order must be the XML order, not sorted.");
        }

        private static List<string> Ids(List<ProbRoomDefinition> probs)
        {
            var ids = new List<string>();
            foreach (ProbRoomDefinition p in probs) ids.Add(p.id);
            return ids;
        }
    }
}
