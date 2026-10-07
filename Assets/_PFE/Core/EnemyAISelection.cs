using System.Collections.Generic;
using PFE.Entities.Enemies;
using UnityEngine;

namespace PFE.Core
{
    /// <summary>
    /// Selection rules for the enemy-AI overlay, as pure functions. Unity-free on purpose: the
    /// overlay's panel is untestable-but-obvious IMGUI, so every decision with a branch in it lives
    /// here instead, where the offline wall can execute it.
    ///
    /// <para><b>Why this is not three lines inside the overlay.</b> Both of these have an edge case that
    /// is silent when wrong. A cycling function with an off-by-one appears to work until the list
    /// shrinks — an enemy dies and the selection either skips one or lands out of range, and an
    /// out-of-range index reads as "nothing selected", i.e. exactly like a working overlay with nothing
    /// under the cursor. And "nearest within a radius" has to answer <i>which</i> of two equidistant
    /// candidates wins, because a selection that flips between two enemies on alternate frames is
    /// unusable and looks like a rendering bug.</para>
    /// </summary>
    public static class EnemyAISelection
    {
        /// <summary>The "nothing is selected" index. Negative so it can never collide with a real one.</summary>
        public const int NoSelection = -1;

        /// <summary>
        /// Step a selection index by <paramref name="direction"/>, wrapping at both ends.
        ///
        /// <para><b>Wrapping is the point.</b> With three enemies, stepping forward from the last must
        /// land on the first — a clamp would instead pin the selection and read as "the key stopped
        /// working" at exactly the moment the tester is trying to cycle. And stepping <i>back</i> from
        /// the first must land on the last, which needs the modulo fixup: C#'s <c>%</c> keeps the sign
        /// of the dividend, so <c>-1 % 3</c> is <c>-1</c>, not <c>2</c>.</para>
        /// </summary>
        /// <param name="current">The current index, or <see cref="NoSelection"/>.</param>
        /// <param name="count">How many candidates there are. Zero yields <see cref="NoSelection"/>.</param>
        /// <param name="direction">Positive to step forward, negative to step back, zero to normalise
        /// the current index without moving.</param>
        public static int Cycle(int current, int count, int direction)
        {
            // An empty list has no valid index, and returning 0 here would hand back an index that
            // does not exist. This is the case that a naive `(current + 1) % count` turns into a
            // divide-by-zero.
            if (count <= 0) return NoSelection;

            if (direction == 0)
            {
                return current >= 0 && current < count ? current : NoSelection;
            }

            // From "nothing selected", the first step forward selects the first and the first step
            // back selects the last — the same result as stepping from a wrapped position, but
            // without inventing a starting index.
            if (current < 0 || current >= count)
            {
                return direction > 0 ? 0 : count - 1;
            }

            int next = (current + direction) % count;
            if (next < 0) next += count;
            return next;
        }

        /// <summary>
        /// The index of the candidate nearest <paramref name="from"/>, or <see cref="NoSelection"/> if
        /// none is within <paramref name="maxDistance"/>.
        ///
        /// <para><b>Strictly less-than, so ties go to the lowest index.</b> Two enemies at the same
        /// distance must resolve the same way on every frame; a <c>&lt;=</c> comparison would let the
        /// later one win, which is stable too — but an unstable pick is what you get the moment the
        /// comparison is written as a distance delta against a moving cursor, and the symptom is a
        /// selection that flickers between two units. Picking deterministically makes that impossible
        /// rather than unlikely.</para>
        /// </summary>
        /// <param name="points">Candidate positions, in the same space as <paramref name="from"/>
        /// (the overlay passes world pixels).</param>
        /// <param name="maxDistance">The pick radius. Use <c>float.PositiveInfinity</c> for no limit.</param>
        public static int Nearest(IReadOnlyList<Vector2> points, Vector2 from, float maxDistance)
        {
            if (points == null || points.Count == 0) return NoSelection;

            // Compared squared, so no sqrt per candidate and no precision loss on a long list. The
            // radius is squared once instead.
            float limitSq = maxDistance * maxDistance;
            float bestSq = float.MaxValue;
            int best = NoSelection;

            for (int i = 0; i < points.Count; i++)
            {
                float distSq = (points[i] - from).sqrMagnitude;
                if (distSq > limitSq) continue;
                if (distSq >= bestSq) continue;   // `>=` gives ties to the lowest index

                bestSq = distSq;
                best = i;
            }

            return best;
        }

        /// <summary>
        /// A state label that says what the value <i>is</i>, including the two places where
        /// <see cref="EnemyAIState"/> deliberately does not mean what its number suggests.
        ///
        /// <para><b>This is not decoration.</b> The enum's own doc records that its members 4-7 were
        /// once documented as AS3's 4-7 and are not: AS3's 7 is the super attack while the port's 7 is a
        /// stop-and-swing melee state it invented. A readout that printed a bare <c>7</c> would invite
        /// exactly the misreading that comment exists to prevent, so the label carries the correction
        /// with it.</para>
        /// </summary>
        public static string DescribeState(EnemyAIState state)
        {
            switch (state)
            {
                case EnemyAIState.Idle:         return "Idle (AS3 aiState 0)";
                case EnemyAIState.Patrol:       return "Patrol (AS3 aiState 1)";
                case EnemyAIState.Alert:        return "Alert (AS3 aiState 2)";
                case EnemyAIState.CombatChase:  return "CombatChase (AS3 aiState 3)";

                case EnemyAIState.PrepareSuper:
                    return "PrepareSuper (AS3 aiState 4 = `pre` wind-up; NOT ported)";

                case EnemyAIState.Buried:       return "Buried (AS3 aiState 5 — hidden in the floor)";
                case EnemyAIState.Digging:      return "Digging (AS3 aiState 6 — rising)";

                case EnemyAIState.Attack:
                    return "Attack (PORT-ONLY: AS3's 7 is the super attack, not this)";

                case EnemyAIState.Dead:
                    return "Dead (AS3 `sost`, not an aiState at all)";

                // A new member must be visibly unlabelled rather than silently inherit a label. Same
                // rule as the parsers' `default` arms: an unknown value shows up as unknown.
                default:
                    return $"{(int)state} (UNLABELLED — a new EnemyAIState with no label here)";
            }
        }
    }
}
