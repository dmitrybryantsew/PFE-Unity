using System;
using PFE.Sim.Radiation;

namespace PFE.Sim.RPG
{
    /// <summary>
    /// The per-frame health regeneration the regeneration implant grants — AS3
    /// <c>UnitPlayer.actions()</c>, <c>UnitPlayer.as:963-967</c>:
    /// <code>
    /// if(this.pers.regenFew > 0 &amp;&amp; hp &lt; Math.min(maxhp * this.pers.regenMax, maxhp - this.rad) &amp;&amp; hp > 0)
    /// {
    ///    hp += this.pers.regenFew;
    ///    World.w.gui.setHp();
    /// }
    /// </code>
    ///
    /// <para><b>Why this file exists at all.</b> <c>CharacterStats.regenFew</c> and
    /// <c>CharacterStats.regenMax</c> have been written since the universal <c>&lt;sk&gt;</c> engine
    /// landed — <c>AllData.as:4140-4141</c>, the two rows of <c>impl_regen</c>, arrive through the
    /// held-item path in <c>CharacterStats.RecalculateStats</c> step 5.5 — and <b>nothing read them</b>.
    /// So the doctor's 15 000-cap "Regeneration module" was a purchase that did nothing: the stat
    /// dump showed <c>regenFew 0.1</c> and the health bar never moved. A field with a writer and no
    /// reader is the exact failure mode this project's rules call out, and the reader is one
    /// expression, so it is a rule here rather than three lines buried in a <c>MonoBehaviour</c>.</para>
    ///
    /// <para><b>Unity-free on purpose</b> (it lives in <c>PFE.Sim</c>, which has no
    /// <c>UnityEngine</c> reference), so the offline wall can pin it against the oracle's own
    /// expression. <c>PlayerHealthRegenTicker</c> is the wiring half.</para>
    ///
    /// <para><b>The rate is per TICK, not per second.</b> AS3 calls <c>actions()</c> once per frame at
    /// the game's 30 Hz, and <c>regenFew</c> is 0.1 for <c>impl_regen</c> — 3 hp/s, which is exactly
    /// what the item's own description promises (<c>text_en.xml</c>, <c>impl_regen</c>:
    /// "restores … by #1 units per second" with <c>s1='3'</c>). A caller that runs this at Unity's
    /// fixed step instead of the sim clock gets a proportionally different rate; that is the ticker's
    /// problem, and it says so.</para>
    /// </summary>
    public static class HealthRegenRules
    {
        /// <summary>
        /// One frame of regeneration. Returns the health the caller must write back — unchanged when
        /// the guard refuses, which is the common case (no implant, or already at the cap).
        /// </summary>
        /// <param name="hp">The unit's current health (<c>hp</c>).</param>
        /// <param name="maxHp">Its ceiling before radiation (<c>maxhp</c>).</param>
        /// <param name="rad">Health lost to radiation (<c>this.rad</c>).</param>
        /// <param name="regenFew">hp per frame (<c>pers.regenFew</c>); 0 without the implant.</param>
        /// <param name="regenMax">The fraction of <paramref name="maxHp"/> the regen may reach
        /// (<c>pers.regenMax</c>); 0 without the implant.</param>
        public static float Step(float hp, float maxHp, float rad, float regenFew, float regenMax)
        {
            // `regenFew > 0` — the implant's own gate. Without it the block is skipped entirely, which
            // is why a player with no implant has no passive healing in the Oracle either.
            if (regenFew <= 0f) return hp;

            // `hp > 0` — a corpse does not regenerate, and neither does a unit at negative hp (the
            // Oracle's hp can go below zero; `heal` climbs back out of it, regen does not).
            if (hp <= 0f) return hp;

            // `Math.min(maxhp * regenMax, maxhp - rad)` — the LOWER of the two caps. regenMax is 0.25
            // on impl_regen, so the implant tops a player up to a quarter of their health and stops;
            // the radiation ceiling matters when exposure has already eaten past that quarter.
            // RadiationMath.HpCeiling is the same `maxhp - rad` the radiation tick and Heal() use, so
            // the three call sites cannot drift apart.
            float cap = Math.Min(maxHp * regenMax, RadiationMath.HpCeiling(maxHp, rad));

            // `hp < cap` — strict. At exactly the cap the Oracle adds nothing, so a player parked at
            // 25% does not creep upward one frame at a time.
            if (hp >= cap) return hp;

            // No clamp on the result: AS3 does `hp += regenFew` and lets it overshoot the cap by up to
            // one frame's worth. A mirror, not a floor — clamping here would be a "fix" the oracle does
            // not have, and it would hide a regenMax above 1 if one ever ships.
            return hp + regenFew;
        }
    }
}
