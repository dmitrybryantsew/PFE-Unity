using System;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Which AS3 unit family is interpreting a placement's <c>ai</c> attribute.
    ///
    /// <para><b>Why the family has to be part of the answer.</b> AS3 reads the attribute exactly once, on
    /// the base <c>Unit</c> (<c>Unit.as:619-621</c> — <c>if(param3.@ai.length()) this.aiTip =
    /// param3.@ai;</c>), and then <b>each family's own constructor decides what its spellings mean</b>.
    /// Only three families read it at all, and they do not agree:</para>
    /// <list type="bullet">
    /// <item><description><c>UnitRaider</c> (+ <c>UnitEncl</c>, <c>UnitMerc</c>, <c>UnitRanger</c>,
    /// <c>UnitZebra</c>, <c>UnitSlaver</c>) reads <c>stay</c>, <c>quiet</c> and <c>sniper</c>
    /// (<c>UnitRaider.as:227-241</c>).</description></item>
    /// <item><description><c>UnitAlicorn</c> reads <c>stay</c>, <c>quiet</c> and <c>pole</c>
    /// (<c>UnitAlicorn.as:206-222</c>).</description></item>
    /// <item><description><c>UnitAIRobot</c> (+ <c>UnitSentinel</c>, <c>UnitProtect</c>, <c>UnitEqd</c>,
    /// <c>UnitGutsy</c>) reads <c>quiet</c> and <b>nothing else</b>
    /// (<c>UnitAIRobot.as:49-52</c>).</description></item>
    /// <item><description><c>UnitNecros</c> — which extends <c>Unit</c>, <i>not</i> <c>UnitRaider</c> —
    /// tests <c>aiTip != "stay"</c> inline and has no <c>stroll</c> field at all
    /// (<c>UnitNecros.as:102</c>).</description></item>
    /// <item><description>Everything else reads none of it. <c>UnitHellhound</c> and <c>UnitZombie</c>
    /// extend <c>UnitPon</c>, which never mentions <c>aiTip</c>; <c>UnitNPC</c> has its own vocabulary
    /// (<c>fly</c>, <c>agro</c>) read from its own node.</description></item>
    /// </list>
    ///
    /// <para>So a single global mapping is wrong in a way that shipped data can reach:
    /// <c>RoomsSerial2.as:6516-6651</c> authors <c>ai="stay"</c> on three <c>hellhound</c> placements,
    /// where the oracle does <b>nothing</b> — the hellhound wanders regardless
    /// (<c>UnitHellhound.as:145</c> rolls its idle state without consulting any flag).</para>
    /// </summary>
    public enum UnitAiFamily
    {
        /// <summary>
        /// No <c>aiTip</c> reader — the base <c>Unit</c>, <c>UnitPon</c>, and every family that never
        /// mentions the attribute. Every spelling interprets as
        /// <see cref="PlacementAiEffect.None"/>.
        /// </summary>
        Generic = 0,

        /// <summary><c>UnitRaider</c> and its subclasses — <c>stay</c>, <c>quiet</c>, <c>sniper</c>.</summary>
        Raider,

        /// <summary><c>UnitAlicorn</c> — <c>stay</c>, <c>quiet</c>, <c>pole</c>.</summary>
        Alicorn,

        /// <summary><c>UnitAIRobot</c> and its subclasses — <c>quiet</c> only.</summary>
        Robot,

        /// <summary><c>UnitNecros</c> — <c>stay</c> only, and it does not go through <c>stroll</c>.</summary>
        Necros,
    }

    /// <summary>
    /// What one placement's <c>ai</c> spelling switches on, once the reading family is known.
    /// </summary>
    /// <remarks>
    /// A flags value rather than four separate lookups so that a caller cannot ask one family's question
    /// of another family's string — the two inputs are passed together and the answer is whole.
    /// </remarks>
    [Flags]
    public enum PlacementAiEffect
    {
        /// <summary>Nothing. Also what every spelling means to <see cref="UnitAiFamily.Generic"/>.</summary>
        None = 0,

        /// <summary>
        /// AS3 <c>stroll = false</c> — the unit holds its post instead of rolling its idle wander
        /// (<c>UnitRaider.as:729-737</c>, <c>UnitAIRobot.as:204-211</c>, <c>UnitAlicorn.as:609-616</c>).
        /// </summary>
        PinsToPost = 1 << 0,

        /// <summary>
        /// AS3 <c>quiet = true</c> — mutes the idle voice barks
        /// (<c>UnitRaider.as:1060</c>, <c>:1079</c>). Always arrives together with
        /// <see cref="PinsToPost"/>, because the same branch sets both.
        /// </summary>
        Quiet = 1 << 1,

        /// <summary>
        /// AS3 <c>moving = false</c> — the unit stops closing distance and holds at stand-off
        /// (<c>UnitRaider.as:768-771</c>: <c>if(!this.moving) aiState = 4;</c>). <c>sniper</c> only.
        /// </summary>
        HoldsPosition = 1 << 2,

        /// <summary>
        /// AS3 <c>weaponSkill = 2</c> — doubles the shooter's effective weapon skill, which <i>halves</i>
        /// its spread (<c>Weapon.as:1454-1458</c> divides by <c>_loc1_ + 0.01</c>). <c>sniper</c> only.
        /// </summary>
        SniperSkill = 1 << 3,
    }

    /// <summary>
    /// The placement <c>ai</c> attribute, as a pure function of (family, spelling).
    ///
    /// <para><b>Why this is not on a controller.</b> Same reason <see cref="UnitNpcRules"/> is not: the
    /// controllers are <c>MonoBehaviour</c>s and this project's offline harness cannot construct one, so
    /// a rule that lives only on one can be tested only by running Unity. This is a table with three
    /// rows, and every row is wrong in a way nothing reports — a misplaced <c>case</c> silently pins a
    /// unit that should wander, or fails to pin one that should stand.</para>
    ///
    /// <para><b>Matching is exact and ordinal, and that is load-bearing.</b> AS3 compares the attribute
    /// with <c>==</c> against literals, so <c>ai="Stay"</c> matches nothing and the unit wanders. The
    /// shipped rooms prove the point by containing a typo — <c>Rooms.as:1936</c> authors
    /// <c>ai="quet"</c> on a raider, which the oracle treats as "no <c>ai</c> at all" and which any
    /// normalising (trim / lower-case / prefix) match would silently "repair". A port that repairs it
    /// diverges from the game, so the comparison here is <see cref="StringComparison.Ordinal"/> with no
    /// trimming. The previous implementation lower-cased and trimmed; that was a divergence, not a
    /// convenience.</para>
    /// </summary>
    public static class UnitPlacementAi
    {
        /// <summary>
        /// The effect of <paramref name="ai"/> for <paramref name="family"/>.
        /// </summary>
        /// <param name="family">The reading family — AS3's own discriminator.</param>
        /// <param name="ai">The placement's <c>ai</c> attribute text. <c>null</c>/empty means the
        /// placement authored none, which is <see cref="PlacementAiEffect.None"/> for every
        /// family.</param>
        public static PlacementAiEffect Interpret(UnitAiFamily family, string ai)
        {
            if (string.IsNullOrEmpty(ai))
            {
                return PlacementAiEffect.None;
            }

            switch (family)
            {
                // UnitRaider.as:227-241. Three spellings, and `sniper` is the only one that is not
                // purely about standing still.
                case UnitAiFamily.Raider:
                    if (string.Equals(ai, "stay", StringComparison.Ordinal))
                    {
                        return PlacementAiEffect.PinsToPost;
                    }

                    if (string.Equals(ai, "quiet", StringComparison.Ordinal))
                    {
                        return PlacementAiEffect.PinsToPost | PlacementAiEffect.Quiet;
                    }

                    if (string.Equals(ai, "sniper", StringComparison.Ordinal))
                    {
                        return PlacementAiEffect.PinsToPost
                             | PlacementAiEffect.HoldsPosition
                             | PlacementAiEffect.SniperSkill;
                    }

                    return PlacementAiEffect.None;

                // UnitAlicorn.as:206-222. `pole` is deliberately absent: `:220-222` sets
                // `this.pole = true` (and `poleId`), which is a separate flag driving the
                // `unlock`/`open` script action on a named object (`:534-551`) — it does NOT touch
                // `stroll`, so it is not an `ai` effect and has no representation here.
                case UnitAiFamily.Alicorn:
                    if (string.Equals(ai, "stay", StringComparison.Ordinal))
                    {
                        return PlacementAiEffect.PinsToPost;
                    }

                    if (string.Equals(ai, "quiet", StringComparison.Ordinal))
                    {
                        return PlacementAiEffect.PinsToPost | PlacementAiEffect.Quiet;
                    }

                    return PlacementAiEffect.None;

                // UnitAIRobot.as:49-52. `quiet` only — `stay` and `sniper` mean NOTHING to a robot,
                // and 11 of the 50 shipped `ai="quiet"` placements are sentinels.
                case UnitAiFamily.Robot:
                    if (string.Equals(ai, "quiet", StringComparison.Ordinal))
                    {
                        return PlacementAiEffect.PinsToPost | PlacementAiEffect.Quiet;
                    }

                    return PlacementAiEffect.None;

                // UnitNecros.as:102 — `if(aiTip != "stay" && isrnd())`, an inline test with no `stroll`
                // and no `quiet` field behind it. Only the exact string "stay" counts; `quiet` on a
                // necros is inert. (`UnitNecros` extends `Unit`, not `UnitRaider`.)
                case UnitAiFamily.Necros:
                    return string.Equals(ai, "stay", StringComparison.Ordinal)
                        ? PlacementAiEffect.PinsToPost
                        : PlacementAiEffect.None;

                default:
                    return PlacementAiEffect.None;
            }
        }
    }
}
