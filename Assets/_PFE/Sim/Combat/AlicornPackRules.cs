using System;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The <b>pack</b> behaviours of AS3 <c>UnitAlicorn</c> — the four things one alicorn does to the
    /// alicorns around it, transcribed from <c>fe/unit/UnitAlicorn.as</c>.
    ///
    /// <para><b>Why these four live together and not on the brain.</b> Each one is a predicate over
    /// another unit's state (<c>celUnit == null</c>, <c>fraction</c>, <c>sost</c>, <c>ear</c>) plus a
    /// number, and none of them needs a <c>MonoBehaviour</c> to answer. The brain owns the loop and the
    /// engine writes; this file owns the decision. That split is what lets the offline harness pin the
    /// rules — an <c>AlicornBrain</c> fixture cannot run, because <c>MonoBehaviour</c> cannot be
    /// constructed outside the editor.</para>
    ///
    /// <para><b>What is here:</b></para>
    /// <list type="bullet">
    /// <item><description><c>telepat()</c> (<c>:403-413</c>) — share a sighting with the pack.</description></item>
    /// <item><description><c>budilo()</c> (<c>:386-401</c>) — the <b>no-ear, no-radius</b> shout that
    /// replaces <c>Unit.budilo</c>'s distance-and-hearing gate.</description></item>
    /// <item><description><c>shok</c> (<c>:448</c>, consumed at <c>:1010</c>) — a startled alicorn
    /// halves its own contact damage.</description></item>
    /// <item><description><c>overLook</c> (<c>:447</c>, <c>:870</c>, <c>:882</c>, <c>:933</c>) — an
    /// aware alicorn drops the vision cone.</description></item>
    /// </list>
    /// </summary>
    public static class AlicornPackRules
    {
        // ── telepat() ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>telepat()</c> (<c>:403-413</c>), verbatim:
        /// <code>
        /// for each(_loc1_ in loc.units)
        ///    if(_loc1_ != this &amp;&amp; _loc1_.fraction == fraction &amp;&amp; _loc1_.sost == 1
        ///       &amp;&amp; !_loc1_.unres &amp;&amp; _loc1_.celUnit == null)
        ///       _loc1_.setCel(celUnit);
        /// </code>
        /// </summary>
        /// <remarks>
        /// <para><b>The last term is the whole behaviour.</b> Only an ally that has <i>no</i> objective
        /// receives one — a neighbour already chasing something keeps it, which is what stops two
        /// alicorns trading targets back and forth every thirty ticks.</para>
        ///
        /// <para><b><c>unres</c> is not modelled.</b> The oracle's field is set by the resurrection
        /// path; the port has no resurrection, so there is no state to test and the parameter is not
        /// carried here. Recorded rather than guessed at.</para>
        /// </remarks>
        /// <param name="isSelf">The receiver is the sharer.</param>
        /// <param name="sameFaction"><c>_loc1_.fraction == fraction</c>.</param>
        /// <param name="isAlive"><c>sost == 1</c> — a live unit, not a corpse and not an unravel.</param>
        /// <param name="alreadyHasTarget"><c>celUnit != null</c> — the port's <c>TargetUnit != null</c>.</param>
        public static bool CanReceiveTelepathy(bool isSelf, bool sameFaction, bool isAlive, bool alreadyHasTarget)
            => !isSelf && sameFaction && isAlive && !alreadyHasTarget;

        /// <summary>
        /// AS3's call site (<c>:675-678</c>): <c>if(aiTCh % 30 == 1) this.telepat();</c>, and only
        /// inside <c>if(celUnit)</c> — i.e. the sharer must currently hold a target of its own.
        /// </summary>
        /// <remarks>
        /// <b>A phase test, not a period.</b> <c>aiTCh</c> counts <i>down</i> from a random 40-190, so
        /// <c>% 30 == 1</c> fires at most once per 30 ticks and sometimes not at all in a cycle. Reading
        /// it as "every 30 ticks" makes the share regular and slightly more frequent than the oracle's.
        /// </remarks>
        public static bool ShouldShareTarget(int aiCheckTicks, bool sharerHasTarget)
            => sharerHasTarget && aiCheckTicks > 0 && aiCheckTicks % TelepathyPeriodTicks == 1;

        /// <summary>The <c>30</c> in <c>aiTCh % 30 == 1</c> (<c>:675</c>).</summary>
        public const int TelepathyPeriodTicks = 30;

        // ── budilo() ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>UnitAlicorn.budilo(param1 = 500)</c> (<c>:386-401</c>) — a <b>full override</b> of
        /// <c>Unit.budilo</c> (<c>Unit.as:4271-4296</c>), and it drops three of the base class's four
        /// gates.
        /// </summary>
        /// <remarks>
        /// <para><b>The oracle, side by side with the base it replaces:</b></para>
        /// <code>
        /// // Unit.budilo (:4271-4296)              // UnitAlicorn.budilo (:386-401)
        /// makeNoise(noiseRun * 1.2);                // — none at all
        /// if(dist² &lt; radius² * ear²)                // — no distance, no ear
        ///    alarma(X + rnd*250, Y + rnd*250);      //    alarma(celX, celY) — the exact target
        /// </code>
        /// <para><b>All three omissions are the point, not an oversight in the oracle.</b> An alicorn's
        /// shout reaches <i>every</i> living ally in the room — no radius, no hearing check — and it
        /// hands over the <b>exact</b> position it is aiming at rather than a ±125 px smear. That is
        /// what makes an alicorn pack converge instead of milling about: a deaf or distant sibling still
        /// gets the player's coordinates.</para>
        ///
        /// <para><b>The missing <c>makeNoise</c> matters too.</b> <c>Unit.budilo</c>'s first line makes
        /// the shout itself audible to the room; the override does not, so an alicorn alarm propagates
        /// through the unit list only. The port's base <c>EnemyBrain.Budilo</c> does emit that noise, so
        /// the override has to be a real override rather than a call with a wider radius.</para>
        ///
        /// <para>Note that the parameter is <b>unused</b> in the oracle: <c>budilo(500)</c> and
        /// <c>budilo(1200)</c> do the same thing for an alicorn. <see cref="AlarmEligible"/> therefore
        /// takes no radius.</para>
        /// </remarks>
        /// <param name="isSelf">The receiver is the shouter.</param>
        /// <param name="sameFaction"><c>_loc2_.fraction == fraction</c>.</param>
        /// <param name="isAlive"><c>_loc2_.sost == 1</c>.</param>
        public static bool AlarmEligible(bool isSelf, bool sameFaction, bool isAlive)
            => !isSelf && sameFaction && isAlive;

        /// <summary>
        /// The position an alicorn's shout carries: <c>celX</c>/<c>celY</c>, the point it is currently
        /// aiming at — and <b>the unit's own feet when it has no target</b>
        /// (<c>if(celUnit == null) { celX = X; celY = Y; }</c>, <c>:389-393</c>).
        /// </summary>
        /// <remarks>
        /// The base class's shout is jittered by <c>±125 px</c> so the pack does not stack on one pixel.
        /// The alicorn's is not, deliberately: <c>alarma(celX, celY)</c> passes the exact aim point. Do
        /// not "restore" the jitter here — it is the difference between the pack surrounding the player
        /// and the pack converging on a smear around him.
        /// </remarks>
        public static void ShoutPosition(bool hasTarget, float targetX, float targetY, float selfX, float selfY,
                                         out float x, out float y)
        {
            if (hasTarget)
            {
                x = targetX;
                y = targetY;
                return;
            }

            x = selfX;
            y = selfY;
        }

        // ── shok ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>attKorp(celUnit, shok &lt;= 0 ? 1 : 0.5)</c> (<c>:1010</c>) — <b>a startled alicorn hits
        /// for half.</b>
        /// </summary>
        /// <remarks>
        /// <para><c>shok</c> is set only by <c>alarma()</c> (<c>:448</c>,
        /// <c>floor(random() * 15 + 5)</c> = 5..19) and counts down one per tick
        /// (<c>Unit.as:3065-3067</c>), so this is a half-second-ish window right after being startled
        /// in which the alicorn's contact damage — not its bolt — is halved.</para>
        ///
        /// <para><b>It is the contact attack only.</b> The weapon trigger at <c>:1014-1025</c> reads
        /// <c>t_shit</c> and <c>aiAttackT</c>, never <c>shok</c>, so the <c>alilight</c> bolt is
        /// unaffected. Applying the scale to the weapon as well would make a just-alarmed alicorn
        /// harmless rather than merely clumsy.</para>
        /// </remarks>
        /// <param name="shockTicks">The port's <c>shok</c> — <c>EnemyBlackboard.ShockTimerTicks</c>.</param>
        public static float ContactDamageScale(int shockTicks) => shockTicks <= 0 ? 1f : 0.5f;

        /// <summary>
        /// <c>optDistAtt</c> (<c>UnitAlicorn.as:95</c>, <c>internal var optDistAtt:int = 200</c>) — the
        /// horizontal window the contact-attack block tests before it tries <c>attKorp</c>.
        /// </summary>
        /// <remarks>
        /// <b>A cheap pre-filter, not the hit test.</b> <c>attKorp</c> itself is an AABB overlap of the
        /// two collision boxes (<c>Unit.as:3273</c>), so a unit 190 px away fails there even though it
        /// passes here. The window only stops the box test from running against a target across the
        /// room every tick.
        /// </remarks>
        public const float ContactReachPixels = 200f;

        /// <summary>The vertical half of the same test — the literal <c>80</c> at <c>:1008</c>.</summary>
        public const float ContactReachPixelsY = 80f;

        /// <summary>
        /// <c>celDX &lt; optDistAtt &amp;&amp; celDX &gt; -optDistAtt</c> (<c>:1008</c>) — the horizontal
        /// half of the contact-attack pre-filter.
        /// </summary>
        /// <remarks>
        /// <para><b>The vertical half is not applied, and that is a deliberate reduction rather than an
        /// omission.</b> AS3 tests <c>celDY &lt; 80 &amp;&amp; celDY &gt; -80</c> against
        /// <c>celDY = celY - Y + scY</c> (<c>Unit.as:4734</c>), which is measured <i>up from the
        /// unit's feet, plus its own body height</i> — so it is a positive, body-relative number. The
        /// port's <c>EnemyBlackboard.TargetDeltaY</c> is measured from the <b>eye</b> to the target's
        /// centre (see <c>EnemySensors.Evaluate</c>'s commit), a different origin and a different
        /// reference point. Comparing the two would silently accept or reject a band of heights that the
        /// oracle does not.</para>
        ///
        /// <para>Nothing is lost by dropping it: <c>attKorp</c> is an <b>AABB overlap of the two
        /// collision boxes</b> (<c>Unit.as:3273</c>), which <c>UnitController.TryContactAttack</c>
        /// reproduces — so a unit that is not vertically adjacent fails the real test regardless. The
        /// 200 px window only exists to stop that box test running across the room every tick.</para>
        /// </remarks>
        public static bool InContactReachX(float deltaX)
            => deltaX < ContactReachPixels && deltaX > -ContactReachPixels;

        // ── overLook ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>overLook</c> (<c>Unit.as:424</c>, default <c>true</c>) — the flag the base
        /// <c>findCel</c> ORs into <c>look()</c>'s second parameter (<c>:4671</c>, <c>:4693</c>), which
        /// is what drops the facing cone (<c>:4598</c>) and the vertical-angle penalty
        /// (<c>:4589</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The alicorn writes it in exactly two places, and they agree:</b> <c>false</c> in the
        /// calm branches (<c>aiState == 0</c> at <c>:870</c>, <c>aiState == 1</c> at <c>:882</c>) and
        /// <c>true</c> in the aware ones (<c>aiState == 2 || aiState == 3</c> at <c>:933</c>), plus
        /// <c>alarma()</c> setting it <c>true</c> as it promotes the unit (<c>:447</c>).</para>
        ///
        /// <para>So the observable rule is <b>"an alicorn that has noticed you sees 360°; an idle or
        /// patrolling one keeps its cone"</b> — which is also why a pack alerted by one member can track
        /// a player who circles behind it.</para>
        ///
        /// <para><b>Why the default here is <c>false</c> and the oracle's field default is
        /// <c>true</c>.</b> The oracle's field is overwritten from the XML row at load
        /// (<c>Unit.as:1345</c>, <c>node.@overlook &gt; 0</c>) before the first tick, so the literal is
        /// dead. The port's <c>EnemySensors.Omnidirectional</c> defaults to <c>false</c> and is the
        /// documented "cone applies unless the data says otherwise" state; keeping that default and
        /// letting the aware branches raise it is the same end state without a one-frame 360° glance on
        /// spawn.</para>
        /// </remarks>
        /// <param name="familyState">The port's <c>aiState</c> (0 idle, 1 patrol, 2 alert, 3 chase, 4 descend).</param>
        public static bool LooksAround(int familyState) => familyState >= 2 && familyState <= 3;

        // ── t_alarm (the reinforcement) ──────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>t_alarm</c>'s initial value (<c>:119</c>):
        /// <c>Math.round(Math.random() * 600 + 1500)</c> — <b>1500..2100</b> ticks, i.e. 50-70 s at
        /// 30 Hz.
        /// </summary>
        public const int AlarmSpawnMinTicks = 1500;

        /// <summary>The top of <c>round(random() * 600 + 1500)</c>.</summary>
        public const int AlarmSpawnMaxTicks = 2100;

        /// <summary>
        /// <c>t_alarm = Math.round(random() * 600 + 1500)</c>, given one draw in <c>[0,1)</c>.
        /// </summary>
        public static int RollAlarmSpawnTicks(float roll)
        {
            if (roll < 0f) roll = 0f;
            if (roll >= 1f) roll = 0.9999999f;
            return (int)Math.Round(roll * 600.0 + 1500.0, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// <c>if(aiState == 2 &amp;&amp; t_alarm &gt;= 0) { --t_alarm; if(t_alarm == 0) enemySpawn(…) }</c>
        /// (<c>:579-586</c>) — the countdown runs <b>only while the unit is searching</b>, not while
        /// chasing and not while calm.
        /// </summary>
        /// <remarks>
        /// <para><b>State 2 and not "aware".</b> An alicorn that goes straight to the chase
        /// (<c>alarma()</c> sets <c>aiState = 3</c>) never spends the countdown, so the reinforcement is
        /// the reward for a <i>search</i> — a pack that lost the player still calls for help. That is
        /// the opposite of the intuitive reading and it is what the oracle does.</para>
        ///
        /// <para><c>t_alarm == 0</c> rather than <c>&lt;= 0</c> because <c>t_alarm</c> is allowed to go
        /// negative (<c>&gt;= 0</c> gates the decrement) and only the exact zero fires.</para>
        /// </remarks>
        public static bool ShouldCallReinforcements(int familyState, int alarmTicks) => familyState == 2;

        /// <summary>
        /// The tail of the same block: <c>if(t_alarm == 0) { loc.enemySpawn(false,false,"alicorn"); }</c>.
        /// </summary>
        /// <remarks>
        /// <b>The family is hard-coded to <c>"alicorn"</c>, not to the room's spawn group.</b>
        /// <c>enemySpawn</c>'s third parameter overrides <c>tipSpawn</c> (<c>Location.as:2989</c>), so an
        /// alicorn's call for help brings <i>another alicorn</i> — and <c>createUnit</c> then runs
        /// <c>randomCid("alicorn")</c>, which is a fresh <c>floor(random()*3+1)</c>
        /// (<c>Location.as:1871-1873</c>). The reinforcement is therefore a random tier, not the
        /// caller's tier.
        /// </remarks>
        public const string ReinforcementFamilyId = "alicorn";

        /// <summary>True on the tick the reinforcement should be requested.</summary>
        public static bool ReinforcementDue(int alarmTicks) => alarmTicks == 0;
    }
}
