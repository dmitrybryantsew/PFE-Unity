namespace PFE.Systems.Combat
{
    /// <summary>
    /// The per-variant (<c>tr</c>) tuning of AS3 <c>UnitAlicorn</c> — the block at
    /// <c>fe/unit/UnitAlicorn.as:121-229</c> that runs after the base constructor and before the first
    /// tick.
    ///
    /// <para><b>Why this is a table and not four <c>if</c>s in a constructor.</b> The oracle's variant
    /// differences are the whole reason the family has four rows: <c>alicorn1</c> is the plain elite
    /// caster, <c>alicorn2</c> trades the cloak for a <i>teleport</i> super and a much stronger
    /// telekinetic throw, <c>alicorn3</c> carries a second (psionic) weapon and a shield twice the size
    /// that comes back twice as fast. Six of those values are <i>not</i> in the shipped <c>.asset</c>
    /// data — they are hard-coded per branch in the constructor — so they cannot be imported and have to
    /// live somewhere the reader can compare against the oracle line by line. That is this file.</para>
    ///
    /// <para><b>Unity-free on purpose</b>, for the same reason <see cref="AlicornShieldRules"/> is: a
    /// <c>MonoBehaviour</c> cannot be constructed in the offline harness, so a fixture that drove
    /// <c>AlicornController</c> directly could not run there, and a guard that cannot run is decoration.
    /// The controller reads these numbers and writes them onto itself; the arithmetic that decides
    /// <i>when</i> each behaviour fires lives here.</para>
    ///
    /// <para><b>What is deliberately NOT here.</b> The shield constants (<c>shitMaxHp</c>,
    /// <c>shitArmor</c>, <c>t_shit</c>) are <see cref="AlicornShieldRules"/>'s — two tables answering
    /// the same question is how the <c>tr1</c>-gets-the-<c>tr3</c>-shield bug happened once already
    /// (<c>AlicornController.Tier</c>'s remarks). The damage numbers, HP and sprite rows are imported
    /// data and belong to the <c>.asset</c>.</para>
    /// </summary>
    public static class AlicornVariantRules
    {
        // ── Field defaults (UnitAlicorn.as:29-119) ────────────────────────────────────────────────

        /// <summary><c>tGotov = 90</c> (<c>:37</c>) — awareness ticks before <c>superSila()</c> fires.</summary>
        public const int SuperWindUpTicks = 90;

        /// <summary><c>tSuper = 300</c> (<c>:41</c>) — how long the super lasts, in ticks.</summary>
        public const int SuperDurationTicks = 300;

        /// <summary>
        /// <c>spellPower = 2</c> (<c>UnitAlicorn.as:227</c>) — <b>a constructor literal, not data.</b>
        ///
        /// <para>AS3 <c>Unit.spellPower</c> defaults to 1 (<c>Unit.as:316</c>) and the alicorn's
        /// constructor overwrites it with 2 for <b>every</b> tier. It is the multiplier
        /// <c>Spell.cast()</c> folds into every effect (<c>Spell.as:238-249</c>), so it is what makes an
        /// alicorn's <c>sp_blast</c> twice as strong as a player's: <c>dam='20'</c> × 2 = a 40 px/frame
        /// impulse ceiling.</para>
        ///
        /// <para><b>Not on the unit's stats.</b> The port's <c>spellPower</c> lives on
        /// <c>CharacterStats</c>, which only the player has — an enemy has no <c>CharacterStats</c> at
        /// all. The alicorn's value is therefore carried here, where the rest of its constructor
        /// literals are, rather than added as a field no data can author.</para>
        /// </summary>
        public const float SpellPower = 2f;

        /// <summary>
        /// <c>tUsed = -500</c> (<c>:39</c>), set to <c>-250</c> on <c>tr2</c> (<c>:164</c>). The floor
        /// <c>t_gotov</c> is reset to after a super, so it doubles as the cooldown.
        /// </summary>
        public const int SuperUsedCooldown = -500;

        /// <summary><c>tTele = -100</c> (<c>:117</c>) — the telekinesis cycle's starting phase.</summary>
        public const int TeleCycleStart = -100;

        /// <summary><c>tTeleThrow = 60</c> (<c>:55</c>) — ticks a grabbed object is held before the throw.</summary>
        public const int TeleThrowTicks = 60;

        /// <summary>
        /// <c>tTeleRes = 600</c> (<c>:57</c>), <c>400</c> on <c>tr2</c> (<c>:163</c>) — the post-throw
        /// cooldown, which is what makes <c>tr2</c> the telekinetic variant rather than a colour swap.
        /// </summary>
        public const int TeleResTicks = 600;

        /// <summary><c>derp = 15</c> (<c>:69</c>) — the hold controller's deadzone, shared with the player.</summary>
        public const float HoldDeadzone = 15f;

        /// <summary><c>optDistTele = 600</c> (<c>:97</c>) — the squared-distance radius of <c>findBox</c>.</summary>
        public const float TeleRangePixels = 600f;

        /// <summary><c>optDistAtt = 200</c> (<c>:95</c>) — the contact-attack reach.</summary>
        public const float ContactReachPixels = 200f;

        /// <summary>The vertical half of the contact-attack box — <c>celDY &lt; 80 &amp;&amp; celDY &gt; -80</c> (<c>:1008</c>).</summary>
        public const float ContactReachVerticalPixels = 80f;

        /// <summary>
        /// Where a held <b>unit</b> is dragged to, relative to the caster — <c>teleX = X + storona * 150</c>
        /// (<c>:1042</c>).
        /// </summary>
        public const float UnitHoldOffsetX = 150f;

        /// <summary>Where a held <b>prop</b> is dragged to — <c>teleX = X + storona * 80</c> (<c>:1047</c>).</summary>
        public const float PropHoldOffsetX = 80f;

        /// <summary><c>teleY = Y - 40</c> (<c>:1043</c>, <c>:1048</c>) — above the caster's feet.</summary>
        public const float HoldOffsetY = -40f;

        /// <summary><c>_loc1_.massa &gt;= 1</c> (<c>:1155</c>) — props lighter than this are ignored.</summary>
        public const float PropMinMassa = 1f;

        /// <summary><c>isrnd(0.25)</c> (<c>:1143</c>) — the chance <c>findBox</c> prefers the player over a crate.</summary>
        public const float UnitGrabChance = 0.25f;

        /// <summary><c>isrnd(0.3)</c> (<c>:1155</c>) — the per-prop acceptance roll.</summary>
        public const float PropGrabChance = 0.3f;

        /// <summary><c>hp &lt; maxhp * 0.7</c> (<c>:545</c>) — the wound level at which a <c>pole</c> alicorn opens its lock.</summary>
        public const float PoleHpFraction = 0.7f;

        /// <summary><c>tTeleThrow + 10</c> (<c>:1034</c>) — the cycle aborts past this if nothing was grabbed.</summary>
        public const int TeleCycleAbortOffset = 10;

        /// <summary><c>-Math.floor(Math.random() * 100 + 50)</c> (<c>:1031</c>) — the abort phase range.</summary>
        public const int TeleAbortMinTicks = 50;

        /// <summary>The span of the same range — 50..149 ticks.</summary>
        public const int TeleAbortSpanTicks = 100;

        /// <summary><c>Pers.teleEnemy = 60</c> (<c>Pers.as:245</c>) — the lift a grabbed <i>player</i> gets.</summary>
        public const int DefaultTeleEnemy = 60;

        /// <summary><c>maxSpok = 75</c> when the land is alarmed, <c>30</c> otherwise (<c>:437-444</c>).</summary>
        public const int MaxSpokAlarmed = 75;

        /// <summary>The calm value of the same field — <c>Unit.as:362</c>'s default.</summary>
        public const int MaxSpokCalm = 30;

        /// <summary><c>aiSpok = maxSpok + 10</c> (<c>:445</c>).</summary>
        public const int AlarmAwarenessBonus = 10;

        /// <summary>
        /// <c>osob</c> is rolled once at construction and only <c>tr1</c>/<c>tr2</c>/<c>tr3</c> read it.
        /// The three probabilities are <c>0.7 / 0.5 / 0.4</c> (<c>:155/167/179</c>).
        /// </summary>
        public static float OsobChance(int tier)
        {
            switch (tier)
            {
                case 1: return 0.7f;
                case 2: return 0.5f;
                case 3: return 0.4f;
                default: return 0f;
            }
        }

        // ── the variant table ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Everything the constructor's four branches override, in one row per tier.
        ///
        /// <para><c>alicorn0</c> is the branch that does not exist in the oracle — <c>tr</c> resolves
        /// from the id and only <c>1/2/3</c> are authored — so it answers with the <i>field defaults</i>,
        /// which is what an <c>alicorn</c> family-template spawn behaves like.</para>
        /// </summary>
        public static AlicornTuning For(int tier)
        {
            switch (tier)
            {
                // UnitAlicorn.as:157-168 — super = teleport, the strong throw, the fast cycle.
                case 2:
                    return new AlicornTuning(
                        tier: 2,
                        superSilaTip: 2,
                        teleSpeed: 20f,
                        teleAccel: 5f,
                        throwForce: 45f,
                        teleResTicks: 400,
                        superUsedCooldown: -250);

                // UnitAlicorn.as:169-180 — super = psionic bolt; the shield half is AlicornShieldRules.
                case 3:
                    return new AlicornTuning(
                        tier: 3,
                        superSilaTip: 3,
                        teleSpeed: 16f,
                        teleAccel: 4f,
                        throwForce: 30f,
                        teleResTicks: TeleResTicks,
                        superUsedCooldown: SuperUsedCooldown);

                // UnitAlicorn.as:153-156 — the plain caster; every value is the field default.
                case 1:
                default:
                    return new AlicornTuning(
                        tier: tier,
                        superSilaTip: 1,
                        teleSpeed: 16f,
                        teleAccel: 4f,
                        throwForce: 30f,
                        teleResTicks: TeleResTicks,
                        superUsedCooldown: SuperUsedCooldown);
            }
        }

        // ── osob: the reactive behaviours ─────────────────────────────────────────────────────────

        /// <summary>
        /// AS3's <c>osob = Math.random() &lt; p</c> (<c>:155/167/179</c>), forced <c>true</c> on the
        /// hardest difficulty (<c>:181-184</c>).
        /// </summary>
        /// <param name="roll">A uniform draw in <c>[0,1)</c>. Passed in rather than drawn here so the
        /// roll is testable and so the caller can choose its stream — <c>UnityEngine.Random.value</c> is
        /// the analogue of Flash's unseeded <c>Math.random()</c>, and this must NOT consume the seeded
        /// combat RNG (a spawn that draws from it desynchronises every later roll in the room).</param>
        /// <param name="maxDifficulty">AS3 <c>World.w.game.globalDif == 4</c>.</param>
        public static bool RollsOsob(int tier, float roll, bool maxDifficulty)
        {
            if (maxDifficulty)
            {
                return true;
            }

            float chance = OsobChance(tier);
            return chance > 0f && roll < chance;
        }

        /// <summary>
        /// <c>tr1</c>'s reactive blast — <c>UnitAlicorn.damage():417-421</c>:
        /// <c>if(tr == 1 &amp;&amp; osob &amp;&amp; !blasted &amp;&amp; param3 &amp;&amp; param3.weap &amp;&amp;
        /// param3.weap.tip == 1 &amp;&amp; aiState &lt;= 1)</c>.
        ///
        /// <para><b>Three separate gates, and each one is load-bearing.</b> The weapon tip must be
        /// <b>1</b> — <c>Weapon.create</c> routes tip 1 to <c>WClub</c>, so this is "hit by a melee
        /// weapon" and not "hit at all"; a bolt does not trigger it. The unit must be <i>calm</i>
        /// (<c>aiState &lt;= 1</c>), so the blast is a startle reflex rather than a combat action. And it
        /// fires <b>once per life</b> — <c>blasted</c> is set by the same block and cleared only in
        /// <c>setNull()</c> (<c>:481</c>).</para>
        /// </summary>
        public static bool ShouldBlastOnMeleeHit(int tier, bool osob, bool alreadyBlasted,
                                                bool hitByMeleeWeapon, int familyState)
        {
            return tier == 1
                   && osob
                   && !alreadyBlasted
                   && hitByMeleeWeapon
                   && familyState <= 1;
        }

        /// <summary>
        /// <c>tr2</c>'s reactive escape — <c>UnitAlicorn.damage():422-427</c>:
        /// <c>if(tr == 2 &amp;&amp; osob &amp;&amp; !blasted &amp;&amp; param3 &amp;&amp; param3.weap &amp;&amp;
        /// param3.weap.owner &amp;&amp; param3.weap.owner.player &amp;&amp; aiState &lt;= 1)</c>.
        ///
        /// <para><b>This one refuses the damage outright</b> — the branch ends in <c>return 0</c>, so the
        /// hit is not merely survived, it never lands. That is why the caller needs a way to say
        /// "absorbed": a port that applied the damage and then teleported would be a different fight
        /// (and would make the escape look like a lucky dodge instead of a mechanic).</para>
        ///
        /// <para>The source gate is the <b>player's</b> weapon, not merely a hostile one — a turret or a
        /// fellow monster cannot trigger it. Same once-per-life <c>blasted</c> latch as <c>tr1</c>.</para>
        /// </summary>
        public static bool ShouldPortAwayFromPlayerHit(int tier, bool osob, bool alreadyBlasted,
                                                      bool hitByPlayerWeapon, int familyState)
        {
            return tier == 2
                   && osob
                   && !alreadyBlasted
                   && hitByPlayerWeapon
                   && familyState <= 1;
        }

        // ── the super ladder ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>if(aiSpok == 0) { ++t_gotov; if(t_gotov &gt; tGotov) superSila(); }</c> (<c>:727-735</c>).
        /// </summary>
        public static bool IsSuperReady(int superWindUpTicks) => superWindUpTicks > SuperWindUpTicks;

        /// <summary>
        /// <c>t_gotov = Math.floor(tUsed * (Math.random() * 0.4 + 0.8))</c> (<c>:1127</c>) — the
        /// cooldown, randomised to 80..120 % of the tier's floor.
        /// </summary>
        public static int SuperCooldownAfterUse(int tier, float roll)
        {
            int used = For(tier).SuperUsedCooldown;
            float factor = 0.8f + (roll * 0.4f);
            return (int)System.Math.Floor(used * factor);
        }

        /// <summary><c>if(t_super &gt; 0) { --t_super; if(t_super &lt;= 0) superSilaVse(); }</c> (<c>:526-533</c>).</summary>
        public static bool ShouldEndSuper(int superTicksRemaining) => superTicksRemaining <= 0;

        // ── flight: the state-4 descent ──────────────────────────────────────────────────────────

        /// <summary>
        /// <c>spd.y = 200</c> in the <c>aiState == 4</c> branch (<c>UnitAlicorn.as:792</c>) — a
        /// <b>weight</b>, not a speed. <c>norma(this.spd, accel)</c> rescales the whole pair, so 200 only
        /// fixes the ratio between the sink and the horizontal drift toward <c>celX</c>.
        /// </summary>
        public const float DescentSinkWeight = 200f;

        /// <summary>
        /// The horizontal half of the state-4 descent, in px per frame, signed — the port's form of
        /// <c>spd.x = celX - X</c> after <c>norma</c> (<c>UnitAlicorn.as:790-798</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Why a ratio rather than a second speed constant.</b> AS3 builds the direction pair
        /// <c>(celX - X, 200)</c> and normalises it to the unit's <c>accel</c>, so the two components
        /// always share one magnitude and the <i>only</i> free parameter is their ratio. The port fixes
        /// the vertical component at its <c>_descentSpeed</c>, so the matching horizontal component is
        /// <c>sinkSpeed * |dx| / 200</c>. At the full ±100 px offset <c>setCel</c> rolls (<c>:606</c>)
        /// that is <c>sinkSpeed / 2</c>, which is the same 0.45 : 0.9 split <c>norma</c> produces from
        /// <c>(100, 200)</c>. Inventing an independent drift speed would let two numbers disagree where
        /// the oracle has one.</para>
        ///
        /// <para><b>Dropping this was the "they cant go down from prop" report.</b> The port descended
        /// vertically only, so a unit that left a table descended straight back onto it and landed on it
        /// again, for ever. The oracle's drift of up to a hundred pixels is what carries it off the prop
        /// it is over.</para>
        ///
        /// <para><b>The clamp is not in AS3 and is here on purpose.</b> <c>norma</c> guarantees the pair
        /// sums to <c>accel</c>, so the horizontal component can never exceed the vertical weight's
        /// share; this port fixes the vertical at a constant, so a caller passing an <c>dx</c> larger
        /// than <see cref="DescentSinkWeight"/> would get a drift faster than the sink and a descent that
        /// travels sideways faster than it falls. Clamping at <paramref name="sinkSpeed"/> reproduces
        /// the ceiling <c>norma</c> enforces.</para>
        /// </remarks>
        /// <param name="remainingPixels">
        /// <c>celX - X</c> in room-local pixels — how far the descent point still is, signed.
        /// </param>
        /// <param name="sinkSpeed">The unit's vertical descent speed, px per frame, positive.</param>
        /// <returns>The signed horizontal speed, px per frame.</returns>
        public static float DescentDriftSpeed(float remainingPixels, float sinkSpeed)
        {
            float magnitude = System.Math.Abs(remainingPixels);
            if (magnitude < 1f || sinkSpeed <= 0f)
            {
                return 0f;
            }

            float speed = sinkSpeed * magnitude / DescentSinkWeight;
            if (speed > sinkSpeed)
            {
                speed = sinkSpeed;
            }

            return remainingPixels > 0f ? speed : -speed;
        }

        // ── telekinesis: the cycle ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The abort phase — <c>t_tele = -(Math.floor(Math.random() * 100) + 50)</c> (<c>:1031</c>,
        /// <c>:1036</c>), i.e. 50..149 ticks before the next attempt.
        /// </summary>
        public static int TeleAbortPhase(float roll)
        {
            int offset = TeleAbortMinTicks + (int)System.Math.Floor(roll * TeleAbortSpanTicks);
            return -offset;
        }

        /// <summary>
        /// <c>if(t_tele &gt;= tTeleThrow + 10) t_tele = -(...)</c> (<c>:1034</c>) — the safety valve that
        /// re-phases a cycle that reached the throw window without ever grabbing anything.
        /// </summary>
        public static bool ShouldAbortTeleCycle(int telePhase) => telePhase >= TeleThrowTicks + TeleCycleAbortOffset;

        /// <summary>
        /// <c>if(t_tele == 0)</c> (<c>:1027</c>) — the single tick on which the cycle decides. The caller
        /// then applies the oracle's own disjunction: a calm unit (<c>aiState &lt;= 1</c>) or a failed
        /// <c>findBox()</c> both re-phase to <see cref="TeleAbortPhase"/>.
        /// </summary>
        /// <remarks>
        /// <b>The phase must not be left at zero.</b> <c>t_tele</c> increments every tick, so a branch
        /// that does nothing at zero passes through it exactly once and never returns; a caller that
        /// treated "calm" as "skip" would stall the cycle at 0 and the caster would never grab again.
        /// </remarks>
        public static bool IsTeleGrabTick(int telePhase) => telePhase == 0;

        /// <summary><c>if(t_tele &gt;= tTeleThrow)</c> (<c>:1066</c>) — the throw window.</summary>
        public static bool ShouldThrowHeld(int telePhase) => telePhase >= TeleThrowTicks;

        /// <summary><c>t_tele = -tTeleRes</c> after a throw (<c>:1071</c>).</summary>
        public static int TelePhaseAfterThrow(int tier) => -For(tier).TeleResTicks;

        /// <summary><c>t_tele = -tTeleRes / 2</c> after a drop with no target (<c>:1076</c>).</summary>
        public static int TelePhaseAfterDrop(int tier) => -(For(tier).TeleResTicks / 2);

        /// <summary>
        /// <c>if(teleObj.levit == 1) { dropTeleObj(); }</c> (<c>:1079-1083</c>) — the caster lets go when
        /// the held thing starts levitating <b>itself</b>. AS3's <c>levit == 1</c> is the unit's own
        /// levitation (the player's perk, a flyer's hover); <c>levit == 2</c> is "held by someone else",
        /// which is what this caster just set. The port models the two as
        /// <c>UnitController.IsFlyingUnit</c> / <c>IsLevitated</c>, so the caller passes
        /// <paramref name="targetHoldsItselfUp"/>.
        /// </summary>
        public static bool ShouldReleaseGrabbedTarget(bool targetHoldsItselfUp) => targetHoldsItselfUp;

        /// <summary>
        /// <c>_loc2_ = throwForce; if(teleObj.massa &gt; 1) _loc2_ = throwForce / Math.sqrt(massa)</c>
        /// (<c>:1212-1216</c>) — a heavy prop is thrown slower, so <c>mcrate5</c> is a battering ram
        /// rather than a bullet.
        /// </summary>
        public static float ThrowSpeed(float throwForce, float massa)
        {
            if (massa > 1f)
            {
                return throwForce / (float)System.Math.Sqrt(massa);
            }

            return throwForce;
        }

        /// <summary>
        /// The <c>levit</c> value the caster stamps on a grabbed <b>player</b> — <c>:1184</c>:
        /// <c>tr == 2 ? pers.teleEnemy : int(pers.teleEnemy * 0.5)</c>. The <c>tr2</c> variant lifts
        /// twice as hard, which is the third of its four telekinetic advantages.
        /// </summary>
        public static int LevitationForPlayer(int tier, int teleEnemy)
        {
            return tier == 2 ? teleEnemy : (int)(teleEnemy * 0.5f);
        }

        /// <summary><c>else this.teleObj.levit = 2;</c> (<c>:1188</c>) — a prop is always lifted the same amount.</summary>
        public const int LevitationForProp = 2;

        // ── findBox's candidate gates ─────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>_loc1_.levitPoss &amp;&amp; _loc1_.wall == 0 &amp;&amp; _loc1_.levit == 0 &amp;&amp;
        /// _loc1_.massa &gt;= 1</c> (<c>:1155</c>).
        /// </summary>
        /// <remarks>
        /// <b>Two of the four terms are already answered by the port's <c>IsLiftable()</c>.</b> AS3's
        /// <c>levitPoss</c> is cleared at construction for a wall-embedded box (<c>Box.as:392</c>,
        /// <c>if(wall &gt; 0) { levitPoss = false; stay = true; }</c>), and the port's
        /// <c>ObjectInstance.IsLiftable()</c> is <c>SupportsTelekinesis() &amp;&amp; levitPoss</c>. So
        /// <c>levitPoss &amp;&amp; wall == 0</c> collapses into that one call, and this method only has to
        /// add the two terms the port does <i>not</i> model per instance: "already held" and the mass
        /// floor. Passing a fabricated <c>wall: 0</c> would read as a check that never fires.
        /// </remarks>
        /// <param name="liftable">AS3 <c>levitPoss &amp;&amp; wall == 0</c> — the port's <c>IsLiftable()</c>.</param>
        /// <param name="alreadyLevitating">AS3 <c>levit != 0</c> — already held by someone else.</param>
        public static bool PropIsGrabbable(bool liftable, bool alreadyLevitating, float massa)
        {
            return liftable
                   && !alreadyLevitating
                   && massa >= PropMinMassa;
        }

        // ── the pole objective (the room interaction) ─────────────────────────────────────────────

        /// <summary>
        /// <c>if(hp &gt;= maxhp * 0.7) return;</c> (<c>:545-548</c>) — a <c>pole</c> alicorn guards the
        /// lock it carries and only opens it once it is hurt below 70 % of its maximum.
        ///
        /// <para><b>This is the family's one interaction with the room itself.</b> A placement carrying
        /// <c>pole='&lt;id&gt;'</c> (or <c>ai='pole'</c>) makes the unit broadcast <c>unlock</c> and then
        /// <c>open</c> to every object tagged with that id (<c>:549-551</c>), and again on death
        /// (<c>:458-463</c>). In the shipped rooms that is what opens a boss door when its guard dies.
        /// </para>
        /// </summary>
        public static bool ShouldOpenPole(float hp, float maxHp)
        {
            if (maxHp <= 0f)
            {
                return true;
            }

            return hp < maxHp * PoleHpFraction;
        }

        // ── alarma ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>maxSpok = loc.land.aliAlarm ? 75 : 30</c> (<c>:437-444</c>) — an alarmed <i>land</i> makes
        /// every alicorn in it remember a sighting for 2.5x as long.
        /// </summary>
        public static int AlarmMaxSpok(bool landAlarmed) => landAlarmed ? MaxSpokAlarmed : MaxSpokCalm;

        /// <summary>
        /// <c>aiSpok = maxSpok + 10</c> (<c>:445</c>). Converted to the port's tick budget by
        /// <c>EnemyAwarenessMath</c>: the oracle's <c>aiSpok</c> is counted down once per 10-tick
        /// <c>aiTCh</c> cycle, so one <c>aiSpok</c> point is <c>AiCheckIntervalTicks</c> ticks.
        /// </summary>
        public static int AlarmAwareness(int maxSpok) => maxSpok + AlarmAwarenessBonus;
    }

    /// <summary>
    /// One row of <see cref="AlicornVariantRules.For"/> — the values <c>UnitAlicorn</c>'s constructor
    /// overrides per <c>tr</c>, and nothing else.
    /// </summary>
    public readonly struct AlicornTuning
    {
        public AlicornTuning(int tier, int superSilaTip, float teleSpeed, float teleAccel,
                             float throwForce, int teleResTicks, int superUsedCooldown)
        {
            Tier = tier;
            SuperSilaTip = superSilaTip;
            TeleSpeed = teleSpeed;
            TeleAccel = teleAccel;
            ThrowForce = throwForce;
            TeleResTicks = teleResTicks;
            SuperUsedCooldown = superUsedCooldown;
        }

        /// <summary>The oracle's <c>tr</c>.</summary>
        public int Tier { get; }

        /// <summary>
        /// AS3 <c>superSilaTip</c> (<c>:35</c>, then <c>:159</c>/<c>:171</c>): <b>1</b> = cloak,
        /// <b>2</b> = teleport, <b>3</b> = psionic bolt.
        /// </summary>
        public int SuperSilaTip { get; }

        /// <summary>AS3 <c>teleSpeed</c> (<c>:65</c>) — the held object's per-axis velocity cap, px/frame.</summary>
        public float TeleSpeed { get; }

        /// <summary>AS3 <c>teleAccel</c> (<c>:67</c>) — the per-tick nudge, px/frame².</summary>
        public float TeleAccel { get; }

        /// <summary>AS3 <c>throwForce</c> (<c>:59</c>) — the throw impulse's magnitude.</summary>
        public float ThrowForce { get; }

        /// <summary>AS3 <c>tTeleRes</c> (<c>:57</c>) — post-throw cooldown in ticks.</summary>
        public int TeleResTicks { get; }

        /// <summary>AS3 <c>tUsed</c> (<c>:39</c>) — the super cooldown floor, negative.</summary>
        public int SuperUsedCooldown { get; }
    }
}
