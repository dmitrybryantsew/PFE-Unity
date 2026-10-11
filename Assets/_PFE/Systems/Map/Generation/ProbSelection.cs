using System.Collections.Generic;
using PFE.Data.Definitions.Campaign;

namespace PFE.Systems.Map.Generation
{
    /// <summary>
    /// AS3 <c>Land.newRandomProb(loc, maxlevel, imp)</c> (<c>Land.as:809-863</c>) as a pure decision:
    /// given a land's <c>&lt;prob&gt;</c> list and the run's state, which prob room does a
    /// <c>doorprob</c> / <c>doorboss</c> open into?
    ///
    /// <para><b>Why this is separated from the placement.</b> The oracle mixes three concerns in one
    /// function: filtering, choosing, and then mutating the world (<c>createDoorProb</c> and
    /// <c>buildProb</c>). Only the first two are decidable without a world, and they are the two that
    /// carry the rules worth pinning — which probs are eligible, which one wins, and whether the door is
    /// a boss door. Keeping them here means the rules can be tested exhaustively against the oracle's
    /// 66-entry data set instead of through a scene.</para>
    ///
    /// <para><b>Three rules that look like details and are not.</b></para>
    /// <list type="number">
    /// <item><b>Absent <c>level</c> is not <c>level='0'</c>.</b> The oracle tests
    /// <c>xml.@level.length == 0 || xml.@level &lt;= maxlevel</c> (<c>:821</c>). Eight of the 66 probs
    /// have no <c>level</c> and 23 carry <c>level='0'</c>, so treating absent as 0 would drop those 23
    /// from every land whose <c>maxlevel</c> is negative — which never happens — while treating 0 as
    /// absent would offer stage-locked rooms from stage 0. <see cref="ProbRoomDefinition.hasLevel"/>
    /// is what keeps them apart.</item>
    /// <item><b>A completed prob never returns.</b> <c>triggers["prob_" + id] == null</c> is the test
    /// (<c>:821</c>), and <c>Probation.closeProb</c> (<c>Probation.as:201-208</c>) <i>increments</i> that
    /// counter rather than setting it, so "has ever been closed" is the condition — not "is closed
    /// now".</item>
    /// <item><b>The boss flag comes from <c>tip</c>, not from <c>imp</c>.</b> <c>imp</c> decides which
    /// prob is <i>preferred</i>; only <c>tip == "2"</c> decides that the door is a
    /// <c>doorboss</c> (<c>:847-856</c>). In the data 34 probs are <c>tip='2'</c>, so most probs are
    /// boss rooms, and two carry <c>imp</c>.</item>
    /// </list>
    /// </summary>
    public static class ProbSelection
    {
        /// <summary>AS3 <c>did = "doorprob"</c> — the object a non-boss prob door is placed as.</summary>
        public const string DoorProbObjectId = "doorprob";

        /// <summary>AS3 <c>did = "doorboss"</c> — the object a <c>tip='2'</c> prob door is placed as.</summary>
        public const string DoorBossObjectId = "doorboss";

        /// <summary>The <c>tip</c> value that makes a prob room a boss room (<c>Land.as:849</c>).</summary>
        public const string BossTip = "2";

        /// <summary>
        /// The key a prob's completion counter lives under (<c>Probation.as:201-208</c>).
        /// Exposed so the caller that owns the trigger store does not have to re-derive the prefix.
        /// </summary>
        public const string TriggerPrefix = "prob_";

        /// <summary>The outcome of a selection: which prob room, and as which object.</summary>
        public sealed class Choice
        {
            /// <summary>The chosen <c>&lt;prob id&gt;</c>. Also the room name in the prob land.</summary>
            public string probId;

            /// <summary><see cref="DoorProbObjectId"/> or <see cref="DoorBossObjectId"/>.</summary>
            public string doorObjectId;

            /// <summary>Whether the chosen room is a boss room (<c>tip == "2"</c>).</summary>
            public bool isBoss;
        }

        /// <summary>The trigger-store key for <paramref name="probId"/>'s completion counter.</summary>
        public static string TriggerKey(string probId)
        {
            return TriggerPrefix + (probId ?? string.Empty);
        }

        /// <summary>
        /// The probs a door may still open into, in the oracle's document order.
        ///
        /// <para>Order is part of the contract, not an accident: the roll indexes this list
        /// (<c>Land.as:845</c>), so a filter that reorders it silently re-rolls every later
        /// decision.</para>
        /// </summary>
        /// <param name="probs">The land's <c>&lt;prob&gt;</c> children, in document order.</param>
        /// <param name="maxLevel">AS3 <c>maxlevel</c> — the land stage the caller passed.</param>
        /// <param name="built">
        /// Ids already built this run (AS3 <c>this.probs[id] != null</c>). A prob room is built once;
        /// a second door into it would reuse the same detached room. Null means "none built".
        /// </param>
        /// <param name="completed">
        /// Completion-counter keys (AS3 <c>World.w.game.triggers</c>). Pass
        /// <see cref="TriggerKey"/> values. Null means "none completed".
        /// </param>
        public static List<ProbRoomDefinition> Eligible(
            IReadOnlyList<ProbRoomDefinition> probs,
            int maxLevel,
            ICollection<string> built = null,
            ICollection<string> completed = null)
        {
            var eligible = new List<ProbRoomDefinition>();
            if (probs == null) return eligible;

            for (int i = 0; i < probs.Count; i++)
            {
                ProbRoomDefinition prob = probs[i];
                if (prob == null || string.IsNullOrEmpty(prob.id)) continue;

                if (built != null && built.Contains(prob.id)) continue;
                if (completed != null && completed.Contains(TriggerKey(prob.id))) continue;

                // Absent level is eligible at every stage; a present level is a ceiling.
                if (prob.hasLevel && prob.level > maxLevel) continue;

                eligible.Add(prob);
            }

            return eligible;
        }

        /// <summary>
        /// Whether choosing from <paramref name="eligible"/> will consume a roll.
        ///
        /// <para><b>The draw count is part of the contract.</b> AS3 draws in exactly one branch
        /// (<c>Land.as:843-846</c>): not when nothing is eligible, not when <c>imp</c> selects a prob
        /// outright, and not when a single prob remains. A caller that always drew would advance the
        /// shared stream by one extra step and silently re-roll every later placement in the land — the
        /// failure shape that no value-only assertion can see. Exposing the predicate lets the caller
        /// spend the draw exactly where the oracle does.</para>
        /// </summary>
        public static bool NeedsRoll(IReadOnlyList<ProbRoomDefinition> eligible, bool wantImp)
        {
            if (eligible == null || eligible.Count == 0) return false;
            if (wantImp && HasImp(eligible)) return false;
            return eligible.Count > 1;
        }

        /// <summary>The id of the last eligible prob carrying <c>imp</c>, or <c>null</c>.</summary>
        public static string PreferredImpId(IReadOnlyList<ProbRoomDefinition> eligible)
        {
            return HasImp(eligible) ? LastImpId(eligible) : null;
        }

        private static bool HasImp(IReadOnlyList<ProbRoomDefinition> eligible)
        {
            return LastImpId(eligible) != null;
        }

        // AS3 keeps the *last* imp seen while scanning (Land.as:824-827), not the first.
        private static string LastImpId(IReadOnlyList<ProbRoomDefinition> eligible)
        {
            string impId = null;
            for (int i = 0; i < eligible.Count; i++)
            {
                if (eligible[i] != null && eligible[i].imp) impId = eligible[i].id;
            }
            return impId;
        }

        /// <summary>
        /// Choose from an already-filtered list — the form that lets the caller spend its draw exactly
        /// where <see cref="NeedsRoll"/> says to. Use <see cref="Eligible"/> to build the list.
        /// </summary>
        /// <param name="rollIndex">
        /// The roll, already scaled by the caller: AS3 computes
        /// <c>Math.floor(Math.random() * rndRoom.length)</c> (<c>:845</c>). Taken as an index rather
        /// than drawn here so the caller's RNG — and therefore its draw count — stays observable.
        /// Out-of-range values wrap rather than clamp, so passing a raw <c>NextInt</c> cannot bias the
        /// last entry. Ignored unless <see cref="NeedsRoll"/> is true.
        /// </param>
        /// <returns>The choice, or <c>null</c> when <paramref name="eligible"/> is empty.</returns>
        public static Choice SelectFrom(
            IReadOnlyList<ProbRoomDefinition> eligible,
            bool wantImp,
            int rollIndex = 0)
        {
            if (eligible == null || eligible.Count == 0) return null;

            ProbRoomDefinition chosen;

            if (wantImp && HasImp(eligible))
            {
                string impId = LastImpId(eligible);
                chosen = null;
                for (int i = 0; i < eligible.Count; i++)
                {
                    if (eligible[i].id == impId) { chosen = eligible[i]; break; }
                }
                if (chosen == null) return null;
            }
            else if (eligible.Count == 1)
            {
                // The single candidate is taken without a roll — AS3 spends no draw here either.
                chosen = eligible[0];
            }
            else
            {
                int index = rollIndex % eligible.Count;
                if (index < 0) index += eligible.Count;
                chosen = eligible[index];
            }

            bool isBoss = chosen.tip == BossTip;
            return new Choice
            {
                probId = chosen.id,
                doorObjectId = isBoss ? DoorBossObjectId : DoorProbObjectId,
                isBoss = isBoss,
            };
        }

        /// <summary>
        /// Filter and choose in one step, for a caller that does not care about the draw count.
        /// </summary>
        /// <param name="rollIndex">See <see cref="SelectFrom"/>.</param>
        /// <returns>The choice, or <c>null</c> when nothing is eligible (AS3 returns <c>false</c> and
        /// places nothing, <c>Land.as:830-833</c>).</returns>
        public static Choice Select(
            IReadOnlyList<ProbRoomDefinition> probs,
            int maxLevel,
            bool wantImp,
            ICollection<string> built = null,
            ICollection<string> completed = null,
            int rollIndex = 0)
        {
            List<ProbRoomDefinition> eligible = Eligible(probs, maxLevel, built, completed);
            return SelectFrom(eligible, wantImp, rollIndex);
        }
    }
}
