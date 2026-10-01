namespace PFE.Systems.Combat
{
    /// <summary>
    /// How far a round carries on through the units it hits — the port of AS3's <c>probiv</c>, which is
    /// a <b>penetration budget</b> and not a probability.
    ///
    /// <para><b>The oracle, in two pieces.</b> The stop is gated at <c>weapon/Bullet.as:533</c>:</para>
    /// <code>
    /// if(!(this.probiv &gt; 0 &amp;&amp; this.damage &gt; 0))
    /// {
    ///    if(_loc4_ &gt;= 0) { this.popadalo(_loc4_); ...; break; }
    /// }
    /// </code>
    /// <para>— so a round carrying <c>probiv</c> with damage left simply does not stop, and falls out of
    /// the loop to the next unit. What eventually stops it is the spend, inside
    /// <c>Unit.damage()</c> (<c>unit/Unit.as:3684-3696</c>), which mutates <c>param3.damage</c> in
    /// place:</para>
    /// <code>
    /// if(this.maxhp &gt; param1 * 20)  param3.damage = 0;
    /// else if(this.maxhp &gt; param1)  param3.damage *= param3.probiv;
    /// else                          param3.damage *= 1 - (1 - param3.probiv) * this.maxhp / param1;
    /// </code>
    /// <para><c>param1</c> is the damage that actually landed, <b>after</b> armour. A second spend sits
    /// just above it, at <c>:3646-3648</c> — <c>param3.damage -= _loc8_ / param3.probiv</c>, where
    /// <c>_loc8_</c> is the armour absorbed.</para>
    ///
    /// <para><b>What this replaces, and why that matters.</b> The port read the weapon's <c>@pier</c>
    /// into the projectile and used it as a 0..1 pass-through <i>chance</i>
    /// (<c>ProjectileRng.Chance(_piercing)</c>). <c>@pier</c> is a flat armour figure in the 5..70 range,
    /// so <c>Clamp01</c> made every one of the 29 weapons carrying it pierce 100% of the time, and the
    /// minigun — which carries no <c>@pier</c> — could never pierce at all. The chance was invented; the
    /// budget is the oracle.</para>
    ///
    /// <para><b>One term is approximated, and it is named rather than hidden.</b>
    /// <see cref="Spend"/> takes the damage the round <i>has</i> rather than the damage that landed, so
    /// the budget drains faster than the oracle's against an armoured target. The port's armour
    /// reduction is resolved in <see cref="DamageSystem"/>, which in tick mode is a tick later than the
    /// contact, so the landed figure is not available where the stop decision has to be made. Draining
    /// fast is the safe direction: it stops a penetrator sooner rather than letting it run the room. The
    /// <c>:3646-3648</c> armour term is not modelled at all, for the same reason.</para>
    ///
    /// <para>Unity-free on purpose, like <see cref="KnockbackMath"/> and <c>UnitSweepMath</c>, so it can
    /// be linked as source into a console harness and executed against deliberately-wrong variants.</para>
    /// </summary>
    public static class PenetrationMath
    {
        /// <summary>
        /// AS3's stop gate (<c>weapon/Bullet.as:533</c>), negated: <c>true</c> when the round carries on
        /// past this unit.
        /// </summary>
        /// <param name="penetration">The round's <c>probiv</c>. <c>0</c> is an ordinary round.</param>
        /// <param name="remainingDamage">The round's damage <i>after</i> any spend so far.</param>
        public static bool KeepsFlying(float penetration, float remainingDamage)
            => penetration > 0f && remainingDamage > 0f;

        /// <summary>
        /// The budget after passing through one unit — AS3 <c>Unit.damage():3684-3696</c>. Pure: no RNG,
        /// no Unity.
        /// </summary>
        /// <param name="remainingDamage">
        /// The round's damage before this hit. AS3 passes the post-armour figure here; see the class
        /// remarks for why the port passes the pre-armour one.
        /// </param>
        /// <param name="penetration">The round's <c>probiv</c>, clamped to <c>[0, 1]</c> by the caller.</param>
        /// <param name="targetMaxHealth">
        /// The target's maximum HP — AS3 <c>this.maxhp</c>. The three branches turn on how large the
        /// target is <i>relative to the hit</i>: twenty times the damage swallows the round outright, a
        /// larger-than-the-hit target damps it by <c>probiv</c>, and a target the hit would kill lets
        /// most of it through.
        /// </param>
        public static float Spend(float remainingDamage, float penetration, float targetMaxHealth)
        {
            // A round with nothing left cannot spend anything, and the third branch below would divide
            // by zero. AS3 never reaches it — its gate has already dropped the damage to 0 by then — so
            // this is a precondition, not a behaviour.
            //
            // It is ALSO not independently observable, and saying so is the point: measured over a
            // 112-case grid (damage x probiv x maxhp, both signs), removing this early-out changes the
            // result in exactly three cases — all of them requiring a NEGATIVE target max health, which
            // no unit has. For every reachable input branch 1 already returns 0, and the clamp on the
            // final return covers the rest. The two defences are mutually redundant, so a test that
            // removes only one of them still passes; see the fixture, which pins the contract by
            // removing both. Kept because an explicit precondition is cheaper to read than an argument
            // about which later branch happens to catch it.
            if (remainingDamage <= 0f)
                return 0f;

            float spent;

            if (targetMaxHealth > remainingDamage * 20f)
            {
                spent = 0f;
            }
            else if (targetMaxHealth > remainingDamage)
            {
                spent = remainingDamage * penetration;
            }
            else
            {
                // AS3 writes this as `damage *= 1 - (1 - probiv) * maxhp / damage`, which is
                // `damage - (1 - probiv) * maxhp`. Kept in the oracle's own shape so the two can be
                // compared line for line.
                spent = remainingDamage * (1f - (1f - penetration) * targetMaxHealth / remainingDamage);
            }

            // The second, independent defence: NaN fails `> 0f`, and so does a negative. With the
            // precondition above in place this clamp is unreachable — measured, removing it alone
            // changes nothing on the same 112-case grid — but it is what makes the function total if the
            // precondition is ever relaxed, and it is the half that swallows the `0 / 0` in branch 3.
            return spent > 0f ? spent : 0f;
        }

        /// <summary>
        /// The budget after passing through one unit, given whether the hit actually <b>landed</b>.
        /// A round that was evaded costs nothing.
        /// </summary>
        /// <remarks>
        /// This exists as its own function because the rule is easy to get wrong and impossible to
        /// guard where it naturally lives. AS3 spends inside <c>Unit.damage()</c>
        /// (<c>Unit.as:3684-3696</c>), and <c>Unit.udarBullet</c> reaches that call <b>only on the
        /// landed path</b>: the miss branch returns <c>-1</c> at <c>:4109</c> without ever calling
        /// <c>damage()</c>. So a penetrator that a unit evades keeps its whole budget and flies on.
        ///
        /// <para>Spending unconditionally is the natural mistake — the caller is already inside the
        /// "this round penetrates" branch, and the evasion verdict is one line away — and its effect is
        /// invisible in isolation: the round simply stops a unit or two sooner than the oracle's would.
        /// Putting the decision here, in a Unity-free class, is what makes it executable as a guard.</para>
        /// </remarks>
        /// <param name="landed"><c>false</c> when the target evaded the hit.</param>
        public static float SpendOnHit(float remainingDamage, float penetration, float targetMaxHealth,
                                       bool landed)
            => landed ? Spend(remainingDamage, penetration, targetMaxHealth) : remainingDamage;
    }
}
