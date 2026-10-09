namespace PFE.Systems.Combat
{
    /// <summary>
    /// The alicorn's shield ladder, as pure arithmetic — AS3 <c>UnitAlicorn.control():571-594</c>,
    /// <c>castShit():1087-1093</c> and the constructor constants at <c>:33/:111/:152/:172-178</c>.
    ///
    /// <para><b>Unity-free on purpose.</b> The state lives on <c>UnitStats</c> and is driven by
    /// <c>AlicornController</c>, but a <c>MonoBehaviour</c> cannot be constructed in the offline harness
    /// (<c>AddComponent</c> reaches an <c>ECall</c>), so a fixture that drives the controller directly
    /// cannot run there — and a guard that cannot run is decoration. Splitting the ladder out means the
    /// <i>rule</i> is pinned by an ordinary NUnit fixture and the controller is reduced to
    /// "read the stats, ask this, write the stats back". Same split as
    /// <c>UnitShieldOverlayRules</c>, <c>OverlayClip</c> and <c>ArmourWear</c>.</para>
    /// </summary>
    public static class AlicornShieldRules
    {
        /// <summary>
        /// <c>t_shit</c> after a cast — AS3 <c>castShit():1090</c> writes <c>1000</c>.
        /// </summary>
        /// <remarks>
        /// It is a countdown, not a cooldown length: because the tick gate below fires
        /// unconditionally while <c>t_shit &gt; 150</c>, a shielded alicorn must burn 850 ticks
        /// (~14 s at 60 Hz) before the timer even starts caring what the unit is doing.
        /// </remarks>
        public const int CastTimerAfterCast = 1000;

        /// <summary>
        /// The threshold above which the timer falls regardless of awareness — AS3 <c>:571</c>.
        /// </summary>
        public const int CastTimerRecountThreshold = 150;

        /// <summary>AS3 <c>:589</c> — <c>allVulnerMult</c> while a <c>tr3</c> pool is up.</summary>
        public const float Tr3VulnerabilityMultiplier = 0.4f;

        /// <summary>AS3 <c>:593</c> — <c>allVulnerMult</c> while a <c>tr1</c>/<c>tr2</c> pool is up.</summary>
        public const float ShieldedVulnerabilityMultiplier = 0.6f;

        /// <summary>AS3 <c>:33</c> — <c>shitMaxHp = 300</c>, raised to <c>500</c> on <c>tr3</c> at <c>:173</c>.</summary>
        public static int MaxShieldHp(int tier) => tier == 3 ? 500 : 300;

        /// <summary>AS3 <c>:152</c> — <c>shitArmor = 25</c>, raised to <c>50</c> on <c>tr3</c> at <c>:172</c>.</summary>
        public static float ShieldArmor(int tier) => tier == 3 ? 50f : 25f;

        /// <summary>AS3 <c>:111</c> — <c>t_shit = 90</c>, and <c>45</c> on <c>tr3</c> at <c>:178</c>.</summary>
        public static int InitialCastTimer(int tier) => tier == 3 ? 45 : 90;

        /// <summary>
        /// One tick of the cast timer. AS3 <c>:571</c>:
        /// <c>if(aiSpok &gt; 0 || tr == 3 &amp;&amp; osob || t_shit &gt; 150) t_shit--;</c>
        /// </summary>
        /// <param name="castTimerTicks">The current <c>t_shit</c>.</param>
        /// <param name="alerted">AS3 <c>aiSpok &gt; 0</c> — aware of <i>something</i>, which survives
        /// losing sight of the target (see <c>EnemyBlackboard.AlertTimerTicks</c>).</param>
        /// <param name="tier">The alicorn's <c>tr</c>.</param>
        /// <param name="osob">AS3 <c>osob</c>, rolled at construction. Only <c>tr3</c> reads it.</param>
        /// <remarks>
        /// <b>The timer is a countdown, so an unaware unit's shield never arrives.</b> From the initial
        /// <c>90</c> the <c>&gt; 150</c> term is false, so an alicorn that never becomes aware keeps
        /// <c>t_shit = 90</c> forever and never casts. That is the oracle's behaviour, not an omission.
        /// </remarks>
        public static int Tick(int castTimerTicks, bool alerted, int tier, bool osob)
        {
            bool countsDown = alerted
                              || (tier == 3 && osob)
                              || castTimerTicks > CastTimerRecountThreshold;

            return countsDown ? castTimerTicks - 1 : castTimerTicks;
        }

        /// <summary>
        /// Whether the ladder has reached the cast — AS3 <c>:573</c> <c>if(shithp &lt;= 0 &amp;&amp; t_shit &lt;= 0)</c>.
        /// </summary>
        /// <remarks>
        /// The pool must be <i>empty</i>, not merely absent: a shielded alicorn does not re-cast over its
        /// own shield, it waits for the timer (which is why the <c>1000</c> matters).
        /// </remarks>
        public static bool ShouldCast(float shieldHp, int castTimerTicks)
        {
            return shieldHp <= 0f && castTimerTicks <= 0;
        }

        /// <summary>
        /// AS3 <c>:587-594</c> — the resistance the shield grants, recomputed every tick so it falls back
        /// to <c>1</c> on the same frame the pool empties.
        /// </summary>
        public static float AllVulnerabilityMultiplier(float shieldHp, int tier)
        {
            if (shieldHp <= 0f) return 1f;
            return tier == 3 ? Tr3VulnerabilityMultiplier : ShieldedVulnerabilityMultiplier;
        }
    }
}
