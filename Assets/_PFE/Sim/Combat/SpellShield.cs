using System;

namespace PFE.Systems.Magic
{
    /// <summary>
    /// The <c>shithp</c>/<c>shitArmor</c> <b>shield layer</b> — AS3 <c>Unit.shithp</c>
    /// (<c>Unit.as:134</c>, default 0) and <c>Unit.shitArmor</c> (<c>Unit.as:160</c>, default 20).
    ///
    /// <para><b>Not armour.</b> A spell or a boss grants it as an extra damage-absorbing layer that
    /// sits in front of the ordinary armour pool. It is granted by <c>sp_mshit</c>
    /// (<c>Spell.as:314-318</c>) and set directly by four bosses (<c>UnitAlicorn</c>,
    /// <c>UnitBossAlicorn</c>, <c>UnitBossDron</c>, <c>UnitBossUltra</c>) plus
    /// <c>Pers.as:882</c>. The armour workstream deliberately kept it out of the armour model and
    /// recorded that decision (<c>DAMAGE_AND_ARMOUR_DESIGN_2026-09-29.md</c>, and
    /// <c>.workbuddy-ai/memory/2026-09-29.md</c>: "it is reachable as
    /// <c>PoolIntegrityDamage(spellShieldAbsorb:)</c>"). This class is that reachability, made a
    /// first-class, offline-testable rule instead of a parameter a caller has to remember.</para>
    ///
    /// <para><b>Why a pure static and not a method on the unit.</b> The three rules below are read
    /// from <c>Unit.damage()</c> and depend on nothing but their three arguments, so they run with no
    /// editor and no engine — which is the same reason <see cref="SpellCastRules"/> and
    /// <c>HitAvoidance</c> are separate pure types. The <i>state</i> (<c>shithp</c>/<c>shitArmor</c>)
    /// lives on the unit; the <i>rule</i> lives here.</para>
    ///
    /// <para><b>Why this file lives under <c>Sim/</c> and not under <c>Systems/Magic/</c>.</b> It was
    /// written in the magic namespace and stayed there, but its only consumer that matters is
    /// <c>DamageCalculator</c>, which is in <c>PFE.Sim</c> — and <c>PFE.Sim</c> cannot reference
    /// <c>PFE.View</c> (the dependency runs the other way: <c>Systems/Magic</c> is View and uses
    /// <c>SimClock</c>). The shield's two arithmetic rules are now applied inside
    /// <c>DamageCalculator.ResolveDamage</c>, so the file had to move to the assembly that can see it.
    /// The namespace is deliberately left as <c>PFE.Systems.Magic</c> so every existing
    /// <c>using</c> and test keeps compiling; a namespace does not have to match an assembly.</para>
    /// </summary>
    public static class SpellShield
    {
        /// <summary>
        /// The rating the shield contributes to a hit while it is up — AS3 <c>this.shitArmor</c>, used
        /// in <b>both</b> of its roles: added to the damage reduction (<c>Unit.as:3636</c>,
        /// <c>_loc8_ += this.shitArmor</c>) and subtracted from the armour-pool wear
        /// (<c>:3583</c>, <c>_loc9_ -= this.shitArmor</c>). The value to hand to
        /// <c>ArmourWear.PoolIntegrityDamage</c>'s <c>spellShieldAbsorb</c> parameter.
        ///
        /// <para>0 when the shield is down, which is what makes the caller's "no shield" case the same
        /// code path as "shield with a zero rating" — AS3 never distinguishes them either, because both
        /// branches are guarded on <c>shithp &gt; 0</c>.</para>
        /// </summary>
        public static float ArmourRating(float shitHp, float shitArmor)
            => shitHp > 0f ? shitArmor : 0f;

        /// <summary>
        /// Whether the ordinary armour pool still wears, given the shield — the <c>shithp</c> term of
        /// the gate at <c>Unit.as:3578</c>: <c>(this.shithp &lt;= 0 || param1 &gt; this.shitArmor)</c>.
        ///
        /// <para><b>Read it as "the shield shrugs off small hits entirely."</b> While the shield is up,
        /// a hit no larger than the shield's rating does <i>not</i> touch the armour pool underneath —
        /// the shield takes it. A hit that exceeds the rating passes through to the pool, which then
        /// has the rating subtracted from its wear (see <see cref="ArmourRating"/>). Reproduced rather
        /// than "simplified" to "the shield is up": that reading would wear the armour on every hit
        /// while a shield is held, which is the opposite of what a shield is for.</para>
        /// </summary>
        public static bool PermitsArmourPoolWear(float shitHp, float shitArmor, float incomingDamage)
            => !(shitHp > 0f && incomingDamage <= shitArmor);

        /// <summary>
        /// The shield's own HP after it absorbs this hit — AS3 <c>Unit.as:3629-3637</c>:
        /// <c>if (this.shithp &gt; 0) { this.shithp -= param1; if (this.shithp &lt; 0) this.shithp = 0; }</c>.
        ///
        /// <para><b>Two faithful details.</b> The decrement uses the <i>incoming</i> damage, not the
        /// damage that survived the reduction — the shield is worn down by the whole hit even though it
        /// also subtracts its rating from it. And the floor is exactly 0 (the oracle clamps the negative
        /// away), so the shield "breaks" rather than going negative, which is what makes the next hit
        /// find <c>shithp &gt; 0</c> false and fall through to the ordinary armour path.</para>
        ///
        /// <para>A shield already at 0 is returned unchanged — the oracle's <c>if</c> is skipped
        /// entirely, so a downed shield does not go negative on a hit.</para>
        /// </summary>
        public static float AfterAbsorb(float shitHp, float incomingDamage)
            => shitHp > 0f ? Math.Max(0f, shitHp - incomingDamage) : shitHp;

        /// <summary>
        /// The shield's bleed rate on the <b>player</b> — AS3 <c>UnitPlayer.step()</c>
        /// (<c>UnitPlayer.as:1298-1300</c>): <c>if(shithp &gt; 0) { shithp -= 0.05; }</c>, once per
        /// tick. This is what makes <c>sp_mshit</c>'s shield <b>temporary</b>: without it the cast
        /// grants a permanent damage-absorbing layer.
        ///
        /// <para><b>Player-only, and that is the oracle's split, not a simplification.</b> No
        /// <c>Unit.step()</c> decays the field; the four bosses that set it (<c>UnitAlicorn</c>,
        /// <c>UnitBossAlicorn</c>, <c>UnitBossDron</c>, <c>UnitBossUltra</c>) keep theirs until it is
        /// destroyed or re-granted on their own <c>t_shit</c> timer. A caller on a non-player unit must
        /// therefore pass a rate of 0 — see <see cref="Decay"/>.</para>
        ///
        /// <para><b>This one member comes from <c>UnitPlayer.step()</c>, not <c>Unit.damage()</c></b>,
        /// unlike the three above. It lives here anyway because the field it moves is the same one and
        /// splitting one field's rule across two homes is exactly how a later pass ends up "fixing" one
        /// half and not the other.</para>
        /// </summary>
        public const float PlayerDecayPerTick = 0.05f;

        /// <summary>
        /// One tick of shield decay. <paramref name="perTick"/> is
        /// <see cref="PlayerDecayPerTick"/> for a player and <c>0</c> for anything else.
        ///
        /// <para><b>Deliberately not clamped at 0.</b> AS3 writes <c>shithp -= 0.05</c> with no floor,
        /// so a shield sitting at 0.02 ends the tick at -0.03. That is not observable — every consumer
        /// gates on <c>shithp &gt; 0</c> (<see cref="ArmourRating"/>,
        /// <see cref="PermitsArmourPoolWear"/>, <see cref="AfterAbsorb"/>, and the HUD's
        /// <c>Math.ceil</c>), so a small negative reads as "down" everywhere and dies at the next
        /// <c>die()</c> (<c>Unit.as:4369</c>) or respawn (<c>UnitPlayer.as:3427</c>). Clamping here
        /// would be a behaviour change made for tidiness; reproduced instead.</para>
        /// </summary>
        public static float Decay(float shitHp, float perTick = PlayerDecayPerTick)
            => shitHp > 0f ? shitHp - perTick : shitHp;

        // ── The shield's graphic — UnitPlayer.as:4916-4924 ───────────────────────────────────────────
        //
        // The oracle drives `vis.shit` from the same field, in the player's vis update:
        //
        //   if(vis.shit && !vis.shit.visible && shithp > 0) { vis.shit.visible = true;  vis.shit.gotoAndPlay(1); }
        //   if(vis.shit &&  vis.shit.visible && shithp <= 0) { vis.shit.visible = false; vis.shit.gotoAndStop(1); }
        //
        // Two things are easy to get wrong and are why this is a rule rather than a bare `shithp > 0`:
        //
        //  1. The two blocks are NOT a single assignment. They are a rise edge and a fall edge, and the
        //     play/stop halves are the point: the shield LOOPS while it is up and RESETS to frame 1 when
        //     it comes down. A one-line `visible = shithp > 0` gets the visibility right and both
        //     animation halves wrong, which shows as a shield frozen mid-flicker.
        //  2. `gotoAndPlay(1)` restarts from frame 1 only on the rise, so re-showing does not resume
        //     from wherever the last one stopped.
        //
        // These live next to the field's other rules on purpose: the field is the same one, and this
        // project has already paid once for splitting one field's rule across two homes (see the decay's
        // remarks). The visual is a *view* concern, but the decision it makes is a rule about shithp.

        /// <summary>
        /// Whether the shield graphic should be <b>shown and started</b> this tick — the oracle's first
        /// block. True only on the rise: it was hidden and <c>shithp</c> just became positive.
        /// </summary>
        public static bool ShouldShow(float shitHp, bool currentlyVisible)
            => !currentlyVisible && shitHp > 0f;

        /// <summary>
        /// Whether the shield graphic should be <b>hidden and reset</b> this tick — the oracle's second
        /// block. True only on the fall: it was visible and <c>shithp</c> has reached 0 or below.
        /// </summary>
        public static bool ShouldHide(float shitHp, bool currentlyVisible)
            => currentlyVisible && shitHp <= 0f;

        /// <summary>
        /// The visibility the graphic should end the tick at. Equivalent to the two edge tests applied
        /// in sequence, and stated separately so a caller that only needs the level (a debug readout, a
        /// save/load restore) does not have to model the transition.
        /// </summary>
        public static bool ShouldBeVisible(float shitHp) => shitHp > 0f;
    }
}
