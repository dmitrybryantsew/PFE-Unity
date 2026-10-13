namespace PFE.Systems.Weapons
{
    /// <summary>
    /// AS3's <b>power charge</b> (<c>powerfull</c>, the <c>&lt;char pow&gt;</c> weapons) as pure
    /// arithmetic: the <c>t_auto</c>/<c>pow</c> clock, the freeze predicate, and the multiplier the
    /// charge builds up.
    ///
    /// <para><b>What the mechanic is.</b> Hold the attack button after a swing. Every held frame
    /// re-enters <c>Weapon.attack()</c>'s debounce branch (<c>Weapon.as:1297-1302</c>), which refreshes
    /// <c>t_auto</c> and increments <c>pow</c> instead of starting another swing. Once the swing has
    /// wound up past <c>rapid_act*5/6</c> and <c>pow</c> is above 2, <c>WClub.actions()</c>
    /// (<c>:339-355</c>) takes over: the swing <b>freezes</b> — <c>t_attack</c> stops counting down
    /// (<c>:493-496</c>), <c>anim</c> pins at <c>-1/4</c>, and every <c>bindMove</c> is skipped, so the
    /// blade neither moves nor tests anything — while the damage multiplier climbs. Letting go drops
    /// <c>t_auto</c> to 0, which zeroes <c>pow</c> and releases the swing; reaching
    /// <c>pow &gt;= rapid_act*2.15</c> releases it at the cap. Either way the charged multiplier is what
    /// the following sweep carries.</para>
    ///
    /// <para><b>Why this is a separate class.</b> The controller that owns this rule
    /// (<c>MeleeWeaponController</c>) uses <c>UnityEngine.Vector2</c>, so every fixture that exercises
    /// it dies with <c>SecurityException: ECall methods must be packaged into a system module</c> in
    /// the offline wall — measured: <c>MeleeHitWindowTests</c> and <c>MeleeOwnerStatTests</c> are
    /// 0-passed/26-failed there, before and after this change. A rule that important, in a class no
    /// offline test can reach, is exactly the blind spot this project keeps paying for. The arithmetic
    /// lives here instead, with no engine dependency, so it can be executed and mutated from a plain
    /// console run.</para>
    /// </summary>
    public static class MeleeChargeMath
    {
        /// <summary>
        /// The charge's upper bound as a multiple of <c>rapid_act</c> — AS3's literal <c>2.15</c>
        /// (<c>WClub.as:340</c>, <c>:345</c>, <c>:349</c>, <c>:351</c>). Past it the charge releases.
        /// </summary>
        public const float ChargeCapFactor = 2.15f;

        /// <summary>
        /// The wound-up fraction of the swing above which the charge may engage — AS3's <c>5/6</c>
        /// (<c>WClub.as:340</c>). Below it the swing is still in its wind-up and charging is not yet
        /// allowed; this is the same <c>5/6</c> the strike window's upper bound uses (<c>:378</c>,
        /// <c>:435</c>), which is why a charging swing can be inside the window's range without
        /// sweeping — the charge suppresses the <c>bindMove</c>.
        /// </summary>
        public const float ChargeArmFraction = 5f / 6f;

        /// <summary>
        /// The minimum <c>pow</c> for the charge to hold — AS3's <c>pow &gt; 2</c>
        /// (<c>WClub.as:340</c>). Three held frames: <c>pow</c> is bumped once per frame by
        /// <see cref="TakesDebounceBranch"/>.
        /// </summary>
        public const int ChargeMinPow = 2;

        /// <summary>
        /// AS3 <c>Weapon.attack()</c>'s single-shot debounce branch
        /// (<c>Weapon.as:1297</c>): <c>!param1 &amp;&amp; !alicorn &amp;&amp; !auto &amp;&amp; t_auto &gt; 0</c>.
        /// <c>param1</c> (the "forced" call) and <c>alicorn</c> are both false for an ordinary player
        /// swing, so the rule reduces to the two terms here.
        ///
        /// <para>When this is true the call <b>early-returns</b> (<c>:1301</c>): <c>t_auto = 3</c>,
        /// <c>++pow</c>, no swing. That is the whole charge clock — the branch is self-sustaining while
        /// the button is down, because it keeps re-arming the very window it tests.</para>
        /// </summary>
        public static bool TakesDebounceBranch(bool isAuto, int tAuto) => !isAuto && tAuto > 0;

        /// <summary>
        /// AS3 <c>WClub.actions()</c>'s charge predicate (<c>WClub.as:340</c>):
        /// <c>powerfull &amp;&amp; t_attack &lt; rapid_act*5/6 &amp;&amp; pow &gt; 2 &amp;&amp;
        /// pow &lt; rapid_act*2.15</c>.
        ///
        /// <para><b>Both bounds are strict.</b> <c>pow &gt; 2</c> excludes 2, and <c>pow &lt; cap</c>
        /// excludes the cap itself, so a charge releases on the frame <c>pow</c> reaches the cap rather
        /// than one frame later. Measured against <c>rapid_act</c>, not <c>rapid</c>: a speed perk
        /// shortens the charge along with the swing.</para>
        /// </summary>
        public static bool IsCharging(bool powerfull, int tAttack, int pow, float rapidAct) =>
            powerfull &&
            tAttack < rapidAct * ChargeArmFraction &&
            pow > ChargeMinPow &&
            pow < rapidAct * ChargeCapFactor;

        /// <summary>
        /// AS3 <c>WClub.actions()</c>'s charged multiplier (<c>WClub.as:351</c>):
        /// <c>1 + pow / (rapid_act * 2.15)</c>. At the cap this is just under 2, which is why the
        /// charge's ceiling is "about double damage" and not an arbitrary number.
        /// </summary>
        public static float PowerMultiplier(int pow, float rapidAct) =>
            1f + pow / (rapidAct * ChargeCapFactor);

        /// <summary>
        /// One frame of AS3 <c>Weapon.update()</c>'s <c>t_auto</c>/<c>pow</c> clock
        /// (<c>Weapon.as:1226-1233</c>):
        ///
        /// <code>
        /// if(this.t_auto > 0) --this.t_auto;
        /// else                this.pow = 0;
        /// </code>
        ///
        /// <para>The reset being the <i>else</i> of the same test is the load-bearing part: <c>pow</c>
        /// dies the moment the debounce window lapses, three frames after the last press. That is what
        /// bounds the charge — there is no separate "release" step to forget.</para>
        ///
        /// <para>Takes and returns both counters because they are one rule: a caller that decremented
        /// <c>t_auto</c> itself and reset <c>pow</c> on a different condition would be re-deriving the
        /// else-branch, which is how the port's melee controller previously ended up with a
        /// <c>pow</c> rule no other weapon class had.</para>
        /// </summary>
        public static void AdvanceClock(ref int tAuto, ref int pow)
        {
            if (tAuto > 0) tAuto--;
            else           pow = 0;
        }
    }
}
