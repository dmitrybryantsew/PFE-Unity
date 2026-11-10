using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Combat;

// `NecrAiState` is nested in the rules class, so an alias keeps the cases readable without a
// `using static` that would also import every constant.
using NecrAiState = PFE.Systems.Combat.BossNecrRules.NecrAiState;

namespace PFE.Tests.EditMode.Sim
{
    /// <summary>
    /// Offline fixtures for <see cref="BossNecrRules.ResolveAnimState"/> — Mchavi's <c>animate()</c>
    /// ladder (<c>UnitBossNecr.as:120-212</c>).
    ///
    /// <para><b>What was broken.</b> The brain answered <c>"stay"</c> for every living state, so the boss
    /// never left the first frame of the <c>stay</c> clip — she slid around the arena with her feet
    /// still. That is the "no running animations" half of the report, and it is a defect a structural
    /// review cannot see: the method returned a valid id from a valid set.</para>
    ///
    /// <para><b>Every expected value was read out of the AS3, not off the C#</b>, and the oracle's line
    /// is in each message so a failure names the branch that disagrees. The order of the branches is the
    /// content: <c>die</c> beats <c>cast</c>, <c>cast</c> beats the grounded ladder, and the grounded
    /// ladder beats <c>derg</c>, which beats <c>jump</c>.</para>
    /// </summary>
    [TestFixture]
    public class BossNecrRulesTests
    {
        // ── the animation ladder (UnitBossNecr.as:120-212) ────────────────────────────────────────

        /// <summary>
        /// One case per arm of <c>animate()</c>. <b>The order is the content</b>, and two orderings that
        /// look harmless are not:
        ///
        /// <list type="bullet">
        /// <item><c>aiState == 3</c> read before the death test plays the <c>cast</c> pose on a corpse
        /// (<c>:140-147</c> sits <i>after</i> <c>:123-139</c>).</item>
        /// <item><c>aiState == 1</c> read before the <c>dx</c> band plays <c>run</c> while she is rooted
        /// in the blast state — and <c>Blast</c> is the state that most needs to look different from
        /// moving (<c>:154-174</c> tests <c>dx</c> first, for every non-1 state).</item>
        /// </list>
        /// </summary>
        [Test]
        public void ResolveAnimState_FollowsTheOraclesBranchOrder()
        {
            // `if(sost == 3)` and `else if(sost == 2)` both draw "die" (:123-139), and it wins over
            // everything — including the protect cast.
            Assert.AreEqual("die", BossNecrRules.ResolveAnimState(false, NecrAiState.Stand, true, false, 0f),
                ":123 — dead, standing");
            Assert.AreEqual("die", BossNecrRules.ResolveAnimState(false, NecrAiState.Protect, true, false, 0f),
                ":123 — dead beats cast");
            Assert.AreEqual("die", BossNecrRules.ResolveAnimState(false, NecrAiState.Move, false, true, 18f),
                ":123 — dead beats levitating and moving");

            // `else if(aiState == 3)` -> "cast" (:140-147). This is the arm that had no field in
            // `AnimationSet` at all, so its thirty frames were in the sheet and unreachable.
            Assert.AreEqual("cast", BossNecrRules.ResolveAnimState(true, NecrAiState.Protect, true, false, 0f),
                ":140 — the protect cast, grounded and still");
            Assert.AreEqual("cast", BossNecrRules.ResolveAnimState(true, NecrAiState.Protect, true, false, 9f),
                ":140 — cast beats the dx band");
            Assert.AreEqual("cast", BossNecrRules.ResolveAnimState(true, NecrAiState.Protect, false, true, 0f),
                ":140 — cast beats levit");

            // `else if(stay)` with `dx < 1 && dx > -1` -> "stay" (:154-161).
            Assert.AreEqual("stay", BossNecrRules.ResolveAnimState(true, NecrAiState.Stand, true, false, 0f),
                ":154 — idle");
            Assert.AreEqual("stay", BossNecrRules.ResolveAnimState(true, NecrAiState.Blast, true, false, 0.5f),
                ":154 — the blast is rooted, so it reads as still");
            Assert.AreEqual("stay", BossNecrRules.ResolveAnimState(true, NecrAiState.Move, true, false, -0.99f),
                ":154 — inside the band, negative half");

            // Grounded and moving: `aiState == 1` -> "run" (:162-169), every other state -> "trot"
            // (:170-174). The blast must NOT read as run.
            Assert.AreEqual("run", BossNecrRules.ResolveAnimState(true, NecrAiState.Move, true, false, 1f),
                ":162 — state 1 at exactly 1 px/frame");
            Assert.AreEqual("run", BossNecrRules.ResolveAnimState(true, NecrAiState.Move, true, false, 18f),
                ":162 — the 3x run speed");
            Assert.AreEqual("trot", BossNecrRules.ResolveAnimState(true, NecrAiState.Blast, true, false, 3f),
                ":170 — the blast, drifting");
            Assert.AreEqual("trot", BossNecrRules.ResolveAnimState(true, NecrAiState.Stand, true, false, -3f),
                ":170 — state 0 with drift");

            // Airborne: `else if(levit)` -> "derg" (:172-179), otherwise "jump" (:180-201).
            Assert.AreEqual("derg", BossNecrRules.ResolveAnimState(true, NecrAiState.Stand, false, true, 0f),
                ":172 — levitating");
            Assert.AreEqual("derg", BossNecrRules.ResolveAnimState(true, NecrAiState.Move, false, true, 6f),
                ":172 — levitating and drifting");
            Assert.AreEqual("jump", BossNecrRules.ResolveAnimState(true, NecrAiState.Move, false, false, 18f),
                ":180 — the leap");
            Assert.AreEqual("jump", BossNecrRules.ResolveAnimState(true, NecrAiState.Stand, false, false, 0f),
                ":180 — falling straight down");
        }

        /// <summary>
        /// The <c>dx</c> band is <b>two-sided and strict</b>: <c>dx &lt; 1 &amp;&amp; dx &gt; -1</c>
        /// (<c>:150</c>). <c>1</c> and <c>-1</c> are outside it, so a boss drifting at exactly 1.0
        /// px/frame draws <c>trot</c>. An <c>abs(dx) &lt;= 1</c> reading — the obvious tidy-up — moves the
        /// boundary and makes the walk clip unreachable at one speed.
        /// </summary>
        [Test]
        public void TheStayBand_IsStrictOnBothSides()
        {
            Assert.AreEqual("trot", BossNecrRules.ResolveAnimState(true, NecrAiState.Stand, true, false, 1f),
                "1 is not < 1");
            Assert.AreEqual("trot", BossNecrRules.ResolveAnimState(true, NecrAiState.Stand, true, false, -1f),
                "-1 is not > -1");
            Assert.AreEqual("stay", BossNecrRules.ResolveAnimState(true, NecrAiState.Stand, true, false, 0.999f),
                "just inside");
            Assert.AreEqual("stay", BossNecrRules.ResolveAnimState(true, NecrAiState.Stand, true, false, -0.999f),
                "just inside, negative");
        }

        /// <summary>
        /// Exhaustive over the state space: every answer is one of the <b>seven</b> ids
        /// <c>UnitBossNecr.animate()</c> can select, and <b>all seven are reachable</b>.
        ///
        /// <para>The second half is the control that matters. <c>laz</c>, <c>plav</c>, <c>punch</c>,
        /// <c>kick</c> and <c>walk</c> are also frame labels in <c>visualNecrBoss</c>'s <c>osn</c>
        /// selector, and none of them is reachable — <c>animate()</c> never names them. A rule that
        /// returned one would draw a clip the oracle cannot reach, which is a wrong animation rather
        /// than a missing one. An answer outside the set of seven is therefore a failure, not a
        /// surprise.</para>
        /// </summary>
        [Test]
        public void EveryReachableAnswer_IsOneOfTheSevenSelectableLabels_AndAllSevenAreReachable()
        {
            var selectable = new HashSet<string>
            {
                "die", "cast", "stay", "run", "trot", "derg", "jump",
            };

            var seen = new HashSet<string>();
            foreach (bool alive in new[] { true, false })
            foreach (NecrAiState state in new[]
                     {
                         NecrAiState.Stand, NecrAiState.Move, NecrAiState.Blast, NecrAiState.Protect,
                     })
            foreach (bool grounded in new[] { true, false })
            foreach (bool levitating in new[] { true, false })
            foreach (float dx in new[] { 0f, 0.5f, 1f, 18f, -18f })
            {
                string id = BossNecrRules.ResolveAnimState(alive, state, grounded, levitating, dx);
                Assert.IsTrue(selectable.Contains(id),
                    $"ResolveAnimState({alive}, {state}, grounded={grounded}, levit={levitating}, dx={dx}) " +
                    $"returned '{id}', which animate() never selects (UnitBossNecr.as:120-212)");
                seen.Add(id);
            }

            CollectionAssert.AreEquivalent(selectable, seen,
                "every label animate() can select must be reachable from some state — a rule that can " +
                "never answer 'cast' is the defect this fixture was written for");
        }
    }
}
