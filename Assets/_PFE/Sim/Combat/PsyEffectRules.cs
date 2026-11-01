using System;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The alicorn's psionic attack — AS3 <c>UnitPlayer.damage():3253-3268</c>, transcribed as
    /// arithmetic so the branch can be pinned without a scene.
    ///
    /// <para><b>What it is.</b> The <c>alipsy</c> row carries <c>&lt;dop effect='psy' ch='1'/&gt;</c>
    /// (<c>AllData.as:695</c>), so every hit it lands reaches this branch. The branch is not a lookup
    /// into the <c>&lt;eff&gt;</c> table: it is three hard-coded outcomes chosen by two draws.</para>
    ///
    /// <para><b>Three facts that are easy to get wrong, all of them from the oracle's shape rather
    /// than from taste:</b></para>
    /// <list type="number">
    /// <item><b>Player-only.</b> The block lives in <c>UnitPlayer.damage()</c>, not in
    /// <c>Unit.damage()</c>, so an NPC struck by <c>psy</c> receives nothing at all.</item>
    /// <item><b>Before any HP logic.</b> It runs ahead of the <c>loc.train || loc.base</c> early
    /// return, so the effect still rolls in a place where damage would be refused.</item>
    /// <item><b>No gates.</b> There is no <c>vulner</c>/susceptibility test and no <c>dopCh</c>
    /// re-roll, unlike every other <c>dopEffect</c> arm in <c>Unit.damage()</c>.</item>
    /// </list>
    ///
    /// <para><b>The second draw is conditional, and that is not a micro-detail.</b> The oracle is
    /// <c>if(isrnd(0.33)) … else if(isrnd()) …</c>, so the second <c>Math.random()</c> is only taken
    /// when the first fails. Drawing both up front would consume one extra value per hit and shift
    /// every later roll in the tick — the same trap as rolling a <c>dopCh</c> of exactly 1. That is
    /// why <see cref="RollEffect"/> takes a draw rather than two values.</para>
    /// </summary>
    public static class PsyEffectRules
    {
        /// <summary>AS3 <c>addEffect("horror")</c> — <c>AllData.as:5963</c>, 450 ticks.</summary>
        public const string Horror = "horror";

        /// <summary>AS3 <c>addEffect("vote")</c> — <c>AllData.as:5966</c>, 240 ticks.</summary>
        public const string Vote = "vote";

        /// <summary>AS3 <c>addEffect("disorient")</c> — <c>AllData.as:5960</c>, 450 ticks.</summary>
        public const string Disorient = "disorient";

        /// <summary>AS3 <c>isrnd(0.33)</c> — the <c>horror</c> chance.</summary>
        public const float HorrorChance = 0.33f;

        /// <summary>
        /// AS3 <c>isrnd()</c> — the parameterless default of <c>0.5</c> (<c>Unit.as:4855</c>), applied
        /// to what is left after the <c>horror</c> roll.
        /// </summary>
        public const float VoteChance = 0.5f;

        /// <summary>The <c>dopEffect</c> id that reaches this branch.</summary>
        public const string DopEffectId = "psy";

        /// <summary>Whether a weapon's <c>dopEffect</c> is the psionic one.</summary>
        public static bool IsPsyHit(string dopEffect) => dopEffect == DopEffectId;

        /// <summary>
        /// Choose the effect for one hit, in the oracle's order.
        ///
        /// <para><paramref name="nextRoll"/> is a <i>draw</i>, not a value: it is invoked once for the
        /// <c>horror</c> test and, only if that fails, once more for the <c>vote</c> test. Callers pass
        /// the RNG's own next value so the stream is consumed exactly as AS3 consumes it.</para>
        /// </summary>
        public static string RollEffect(Func<float> nextRoll)
        {
            if (nextRoll == null)
            {
                throw new ArgumentNullException(nameof(nextRoll));
            }

            if (nextRoll() < HorrorChance)
            {
                return Horror;
            }

            if (nextRoll() < VoteChance)
            {
                return Vote;
            }

            return Disorient;
        }
    }
}
