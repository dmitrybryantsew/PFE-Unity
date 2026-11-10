using System;

namespace PFE.Sim.Combat.Sats
{
    /// <summary>
    /// Every number <c>fe.inter.Sats</c> computes, as pure static functions — the arithmetic half of
    /// S.A.T.S., with no engine type in sight so a fixture can execute it outside Unity.
    ///
    /// <para><b>Why the mode is split this way.</b> The Oracle's <c>Sats.as</c> mixes four things:
    /// the entry gate, the queue, the hit percentage and the drawing. Only the first three are
    /// behaviour; the fourth is presentation and is rebuilt wholesale in the port. Of the three, the
    /// arithmetic is the part a reader can get <i>subtly</i> wrong — a cap applied in the other
    /// order, a missing <c>+0.1</c>, a division by the wrong skill value — and every such mistake
    /// still looks plausible on screen. So the formulas live here, pinned by
    /// <c>SatsRulesTests</c> against numbers computed by hand from <c>Sats.as</c>, and
    /// <see cref="PFE.Systems.Combat.Sats.SatsController"/> only decides <i>when</i> to call them.</para>
    ///
    /// <para><b>Nothing here reads state.</b> Every input is a parameter, including the ones the
    /// Oracle reads off globals (<c>gg.pers.satsMult</c>, <c>weapon.satsCons</c>). That is what lets
    /// a fixture state a case without building a player, a weapon and a room.</para>
    /// </summary>
    public static class SatsRules
    {
        // ── The pools (Sats.as:45-51) ──────────────────────────────────────────────────────

        /// <summary>
        /// <c>Sats.as:45</c> — <c>od = 80</c>, the real action-point pool.
        ///
        /// <para><b>It starts five points above the cap and never comes back.</b>
        /// <c>Pers.maxOd</c> defaults to <c>75</c> (<c>Pers.as:329</c>, <c>defaultParams</c>), and
        /// regeneration only tops up while <c>od &lt; maxOd</c> — so the first S.A.T.S. session of a
        /// life begins at 80 and every later one at 75. The pool is not saved either
        /// (<c>saveGame</c>, <c>World.as:1650</c>, never touches <c>sats</c>), so a fresh session
        /// always starts at 80. Reproduced rather than "corrected" to <c>maxOd</c>: a player who
        /// expects 80 on the first use and gets 75 has lost five points of a resource they can see.</para>
        /// </summary>
        public const float InitialOd = 80f;

        /// <summary><c>Sats.as:49</c> — <c>odd = 0.1</c> per tick. At 30 Hz that is 3 AP/s.</summary>
        public const float OdRegenPerTick = 0.1f;

        /// <summary>
        /// <c>Sats.as:51</c> — <c>limOd = 200</c>. <b>A dead field: no reader anywhere in <c>fe/</c>.</b>
        /// Recorded here so the absence is a decision rather than an oversight, and so the constant is
        /// not "helpfully" reintroduced by a future reader who greps the Oracle and finds it.
        /// </summary>
        /// <remarks>Deliberately not a <c>const</c> anyone can consume — see the class note.</remarks>
        public const float DeadLimitOdNotPorted = 200f;

        // ── Caps and bar geometry ─────────────────────────────────────────────────────────

        /// <summary><c>Sats.as:350-353</c> — the hard hit-chance ceiling, applied <i>before</i> the skill cap.</summary>
        public const float HitChanceCap = 0.95f;

        /// <summary>
        /// <c>GUI.setOd</c> (<c>GUI.as:885-899</c>) — both fills are scaled by <c>od / 50</c> and
        /// masked, so a pool above 50 saturates the bar rather than stretching it.
        /// </summary>
        public const float OdBarFullScale = 50f;

        /// <summary>
        /// <c>GUI.step</c> (<c>GUI.as:2009-2015</c>) — <c>t_od</c> starts at 200 and the bar hides at
        /// 1, i.e. ~6.7 s after the last <c>setOd()</c>.
        /// </summary>
        public const int OdBarVisibleTicks = 200;

        /// <summary>
        /// <c>SatsCel.as:37-38</c> — <c>10</c>: the per-shot bracket growth, in pixels of body size.
        /// </summary>
        public const int BracketGrowthPerShot = 10;

        // ── Cost (Sats.as:241, :288, :309; SatsCel.as:40-41) ──────────────────────────────

        /// <summary>
        /// The price of <b>one full cell</b> — <c>satsCons * consMult / skillConf * satsMult</c>
        /// (<c>Sats.as:241</c> and again at <c>:288</c> for the refund).
        ///
        /// <para><b>This is the number the queue reserves, not the number a shot costs.</b> One cell
        /// covers <c>satsQue</c> shots, and <see cref="PerShotCost"/> divides by that. Confusing the
        /// two makes a three-shot burst cost three times what it should.</para>
        /// </summary>
        /// <param name="satsCons">Weapon <c>&lt;sats cons&gt;</c> (default 10) — <c>Weapon.satsCons</c>.</param>
        /// <param name="consMult">The weapon perk family's <c>Cons</c> stat (pistols with the
        ///   <c>pistol</c> perk: 0.75); <c>1</c> for everything else.</param>
        /// <param name="skillConf">0.8 / 0.6 / 1 from the weapon-skill gap — see
        ///   <c>HitAvoidance.SkillConfidence</c>. Never 0 at the call site (a gap above 2 refuses
        ///   entry outright), but guarded here because a division by zero would be silent infinity.</param>
        /// <param name="satsMult"><c>Pers.satsMult</c> — the <c>mathlogic</c> perk's 1 / 0.85 / 0.75.</param>
        public static float FullCellCost(float satsCons, float consMult, float skillConf, float satsMult)
        {
            return satsCons * consMult / GuardSkillConf(skillConf) * satsMult;
        }

        /// <summary>
        /// What one <i>shot</i> debits — <c>SatsCel.cons = price / satsQue</c>
        /// (<c>SatsCel.as:40</c>), read back by <c>Sats.act()</c> (<c>Sats.as:309</c>).
        /// </summary>
        public static float PerShotCost(
            float satsCons, float consMult, float skillConf, float satsMult, int satsQue)
        {
            float full = FullCellCost(satsCons, consMult, skillConf, satsMult);
            return full / (satsQue > 0 ? satsQue : 1);
        }

        /// <summary>
        /// May a cell be queued? <c>if (price &gt; odv) { infoText("noOd"); return; }</c>
        /// (<c>Sats.as:239-243</c>).
        ///
        /// <para><b>Refuse, do not clamp.</b> The Oracle aborts the whole click; it does not spend what
        /// is left. A port that clamped would let a player queue a shot the pool cannot pay for and
        /// then have <c>step2</c> cancel the entire plan a tick later — same outcome, one extra
        /// confusing message.</para>
        /// </summary>
        public static bool CanQueue(float fullPrice, float projectedOd)
        {
            return !(fullPrice > projectedOd);
        }

        /// <summary>
        /// The post-accept affordability check, <c>Sats.step2</c> (<c>Sats.as:209-213</c>):
        /// <code>
        /// if (que.length > 0 &amp;&amp; satsCons*consMult*consMult/skillConf*satsMult/satsQue &gt; od)
        ///     { infoText("noOd"); clearAll(); }
        /// </code>
        ///
        /// <para><b><c>consMult</c> is multiplied TWICE here and once everywhere else.</b>
        /// <c>setCel</c> (<c>:241</c>) and <c>unsetCel</c> (<c>:288</c>) each apply it once; this line
        /// applies it again on top of the already-multiplied price. The effective test is therefore
        /// <c>perShot * consMult &gt; od</c>, which is <i>laxer</i> than the cost the queue actually
        /// charges. That is a quirk, not a typo to tidy: for a pistol with the <c>pistol</c> perk
        /// (<c>consMult = 0.75</c>) a plan survives a pool that could not afford it, and then
        /// <c>act()</c> drives <c>od</c> negative. Mirrored literally; the caller passes
        /// <paramref name="perShotCost"/> already divided by <c>satsQue</c>.</para>
        /// </summary>
        public static bool CanAffordNextShot(float perShotCost, float consMult, float od)
        {
            return !(perShotCost * consMult > od);
        }

        // ── Regeneration (Sats.as:214-219) ───────────────────────────────────────────────

        /// <summary>
        /// One tick of regeneration: <c>if (que.length == 0 &amp;&amp; od &lt; maxOd) od += odd;</c>
        ///
        /// <para><b>Two conditions, both load-bearing.</b> A non-empty queue stops regeneration
        /// entirely (the reserved cells already own that AP), and the pool never regrows past
        /// <c>maxOd</c>. Note the test is on <c>od</c> <i>before</i> the add, so the last step can
        /// overshoot the cap by up to <c>odd</c> — 75.05 is reachable. Reproduced, because the
        /// alternative (clamping) is invisible in play but not in a fixture.</para>
        /// </summary>
        public static float RegenStep(float od, float maxOd, float odd, bool queueEmpty)
        {
            if (!queueEmpty) return od;
            if (!(od < maxOd)) return od;
            return od + odd;
        }

        // ── Target admission (Sats.as:471, Unit.as:4527-4530) ────────────────────────────

        /// <summary>
        /// <c>u.getTileVisi()</c> — may a gun target be offered to S.A.T.S.?
        /// <code>
        /// return loc.getAbsTile(X, Y - scY / 2).visi &gt; param1;   // param1 defaults to 0.3
        /// </code>
        ///
        /// <para><b>A failed fog read-back is "fully visible", not "fully dark".</b> The port's
        /// <c>RoomBackdropRenderer.TryGetFogVisibility</c> returns <c>false</c> with
        /// <c>visibility = 1</c> whenever there is no mask to sample: fog switched off, the mask not
        /// built yet, a collision-only room, or a point outside the room. In every one of those cases
        /// the renderer draws nothing over the unit, so it is visible. Treating <c>false</c> as
        /// "skip this unit" empties <c>Sats.units</c> in exactly the rooms where fog is disabled —
        /// no names, no percentages, no brackets, no hover panel — while the mode otherwise looks
        /// healthy. This is the arithmetic half of that distinction; the caller supplies the read-back
        /// flag rather than a nullable so a fixture can state both outcomes without a room.</para>
        ///
        /// <para><b>The comparison is strict <c>&gt;</c>, not <c>&gt;=</c>.</b> AS3's
        /// <c>visi &gt; 0.3</c> rejects a tile exactly at the threshold. A <c>&gt;=</c> here would
        /// admit one extra ring of half-lit tiles.</para>
        /// </summary>
        /// <param name="fogMaskReadBack"><c>false</c> when there is no visibility mask to sample.</param>
        /// <param name="visibility">The smoothed mask value; ignored when there is no mask.</param>
        /// <param name="threshold"><c>Unit.getTileVisi</c>'s default, 0.3.</param>
        public static bool FogAdmitsTarget(bool fogMaskReadBack, float visibility, float threshold)
        {
            if (!fogMaskReadBack) return true;
            return visibility > threshold;
        }

        // ── The front cell (Sats.as:297-304) ─────────────────────────────────────────────

        /// <summary>
        /// <c>Sats.getReady()</c>: may the front cell be fired <i>this</i> tick?
        /// <code>
        /// que.length &gt; 0 &amp;&amp; (que[0].un == null || que[0].begined || que[0].un.u.sost &lt; 3)
        /// </code>
        ///
        /// <para>Read it as "the front cell still has a live target". A free-aim cell has no unit, so
        /// it always passes. A unit cell passes once its burst has begun (<c>begined</c>) even if the
        /// target dies mid-burst — the remaining rounds of a queued burst still leave the barrel — and
        /// passes while the target is alive (<c>sost &lt; 3</c>, the same aliveness test
        /// <c>getUnits</c> uses to admit a unit in the first place). Only a target that died
        /// <i>between</i> ticks and whose burst has not started fails, and the caller answers that by
        /// dropping the cell (<c>unsetCel(true)</c>).</para>
        /// </summary>
        /// <param name="hasUnitTarget">False for a free-aim cell.</param>
        /// <param name="burstBegun"><c>SatsCel.begined</c> — at least one shot of this cell has fired.</param>
        /// <param name="targetAlive">The bound unit's <c>sost &lt; 3</c>.</param>
        public static bool ShouldFireFront(bool hasUnitTarget, bool burstBegun, bool targetAlive)
        {
            if (!hasUnitTarget) return true;
            return burstBegun || targetAlive;
        }

        /// <summary>
        /// <c>Sats.act()</c> (<c>Sats.as:306-321</c>): does this shot <b>pop</b> the front cell, or
        /// only decrement its remaining rounds?
        /// <code>
        /// if (que[0].kol &gt; 1 &amp;&amp; weapon.status() &lt;= 1) { --kol; begined = true; }
        /// else { shift(); remove(); }
        /// </code>
        ///
        /// <para><b>The second condition is the interesting one.</b> A weapon whose status is 2
        /// (jammed, or an empty magazine) or above pops the cell on the <i>first</i> shot instead of
        /// burning through its rounds — because <c>status &gt; 1</c> means the weapon could not have
        /// fired, so the queued burst is already broken. The cell is spent either way; the difference
        /// is that the remaining rounds are abandoned rather than waiting for a reload that the plan
        /// does not perform.</para>
        /// </summary>
        /// <param name="remainingRounds"><c>SatsCel.kol</c> before the shot.</param>
        /// <param name="weaponStatus">AS3 <c>Weapon.status()</c>: 0 ready, 1 attacking, 2 jammed/no
        ///   round in the magazine, 3 reloading, 4 no reserve ammo, 5 broken, 6 no mana.</param>
        public static bool ShotPopsFrontCell(int remainingRounds, int weaponStatus)
        {
            return !(remainingRounds > 1 && weaponStatus <= 1);
        }

        // ── Charging a burst (UnitPlayer.as:1133-1141) ────────────────────────────────────

        /// <summary>
        /// How many <c>Sats.act()</c> calls are owed for the rounds that left the barrel since the
        /// last check.
        ///
        /// <para><b>One <c>act()</c> per round, not one per tick.</b> The Oracle calls
        /// <c>sats.act()</c> once each time <c>currentWeapon.is_shoot</c> is set
        /// (<c>UnitPlayer.as:1133-1140</c>), and <c>act()</c> decrements <c>SatsCel.kol</c>. A
        /// weapon with <c>&lt;sats que='15'/&gt;</c> — the minigun — therefore owes <b>15 rounds per
        /// cell</b>, and one click in S.A.T.S. must stop after 15 shots instead of draining the
        /// 500-round magazine. A bool "did anything fire" collapses that to one charge per tick, which
        /// silently stretches a burst to the magazine's end.</para>
        ///
        /// <para><b>Why the clamp matters.</b> <paramref name="roundsOwed"/> is the sum of the queue's
        /// remaining <c>kol</c> values. A stale baseline (equip, save load, a controller replaced
        /// mid-plan) can otherwise charge shots that belong to a different weapon at a brand-new
        /// plan, emptying it before a single round is fired. Clamping to what the plan actually owes
        /// makes that failure mode a no-op rather than a free burst.</para>
        ///
        /// <para><b>A backwards counter is not a burst.</b> A fresh weapon starts at zero; a negative
        /// delta means the counter was replaced, not that shots were un-fired.</para>
        /// </summary>
        /// <param name="shotsNow">The weapon's monotonic <c>ShotsFired</c> counter sampled this tick.</param>
        /// <param name="baseline">The same counter sampled at the previous check.</param>
        /// <param name="roundsOwed">Total rounds the standing queue still owes; 0 when it is empty.</param>
        public static int ChargesForShots(int shotsNow, int baseline, int roundsOwed)
        {
            if (roundsOwed <= 0) return 0;

            int fresh = shotsNow - baseline;
            if (fresh <= 0) return 0;

            return fresh < roundsOwed ? fresh : roundsOwed;
        }

        // ── Hit chance (Sats.as:323-358) ─────────────────────────────────────────────────

        /// <summary>
        /// The percentages S.A.T.S. prints over each unit — <c>Sats.getPrec</c>, term for term.
        ///
        /// <para><b>The Oracle's order matters and is reproduced exactly:</b></para>
        /// <code>
        /// p = 1
        /// if (precision &gt; 0)                        p = resultPrec(1, skill) / d / (dexter + 0.1) * skillConf
        /// if (antiprec &gt; 0 &amp;&amp; d &lt; antiprec)          p = (d/antiprec*0.75 + 0.25) / (dexter + 0.1) * skillConf
        /// if (deviation &gt; 0 || mazil &gt; 0) { ang = atan2(scY, d)*180/PI
        ///                                    dev = deviation/(skill + 0.01) + mazil
        ///                                    if (dev &gt; ang) p = p * ang / dev }
        /// if (p &gt; 0.95)      p = 0.95
        /// if (p &gt; skillConf) p = skillConf
        /// </code>
        ///
        /// <para><b>The antiprec branch <i>overwrites</i> <c>p</c>, it does not multiply it.</b>
        /// Inside the minimum range the weapon's accuracy is replaced by a ramp that starts at 0.25 at
        /// point-blank and reaches 1.0 at the threshold — so closing the distance makes the shot
        /// <i>worse</i>, which is the whole point of the mechanic. A port that multiplied would keep
        /// the long-range term and quietly delete the dead zone.</para>
        ///
        /// <para><b>Both caps must run; their order does not matter.</b> The result is
        /// <c>min(p, 0.95, skillConf)</c> either way — verified by exhaustive comparison over the
        /// reachable confidence values (1, 0.8, 0.6), where the two orders agree exactly. What
        /// <i>is</i> load-bearing is that the confidence cap is applied at all: with a gap of 2 the
        /// shot reports 60 %, and a port that only kept the 0.95 ceiling would report 95 % for a
        /// weapon the player is not qualified to aim.</para>
        ///
        /// <para><b>Why the precision multiplier is passed in three pieces.</b>
        /// <c>Weapon.resultPrec(1, skill)</c> is
        /// <c>precision * precMult * (1 + (skill-1)*0.5) * 1 * owner.precMultCont</c>
        /// (<c>Weapon.as:1632-1635</c>), where <c>precMult</c> was set once in <c>setPers</c> to
        /// <c>allPrecMult * pers[perk + "Prec"]</c> (<c>Weapon.as:976</c>, <c>:1006</c>). It
        /// deliberately does <b>not</b> include the <i>situational</i> <c>owner.precMult</c>
        /// (run / jump / stay / back) — because <c>UnitPlayer.as:1183</c> skips the whole locomotion
        /// block while a queue exists, so a queued shot is always treated as standing still. Feeding
        /// <c>CharacterStats.PrecisionMultiplier</c> in here would be wrong twice: once for
        /// double-counting <c>allPrecMult</c>, once for applying a penalty the Oracle suppresses.
        /// Hence three separate parameters rather than one composed value — the mistake cannot be
        /// made by passing the wrong property.</para>
        /// </summary>
        public static float HitChance(in SatsPrecInputs i)
        {
            float d = i.DistancePx;
            float dexter = i.TargetDexterity + 0.1f;

            float p = 1f;

            if (i.PrecisionPx > 0f)
            {
                float resultPrec = i.PrecisionPx * i.PrecMult
                                 * (1f + (i.SkillMultiplier - 1f) * 0.5f)
                                 * i.PrecMultCont;
                p = resultPrec / d / dexter * i.SkillConf;
            }

            if (i.AntiPrecisionPx > 0f && d < i.AntiPrecisionPx)
            {
                p = (d / i.AntiPrecisionPx * 0.75f + 0.25f) / dexter * i.SkillConf;
            }

            if (i.DeviationDeg > 0f || i.Mazil > 0f)
            {
                // The angular size of the target's body, in degrees, at this distance.
                float angular = (float)(Math.Atan2(i.TargetBodyHeightPx, d) * 180.0 / Math.PI);
                // The cone the weapon actually throws, in degrees. `skill` is the skill MULTIPLIER,
                // not the tier — `weaponSkills[skill]` at Sats.as:333.
                float cone = i.DeviationDeg / (i.SkillMultiplier + 0.01f) + i.Mazil;

                if (cone > angular)
                {
                    p = p * angular / cone;
                }
            }

            if (p > HitChanceCap) p = HitChanceCap;
            if (p > i.SkillConf) p = i.SkillConf;

            return p;
        }

        /// <summary>
        /// <c>Math.round(prec * 100)</c> from <c>Sats.as:497</c> — the label's integer percentage.
        ///
        /// <para><c>Math.round</c> in AS3 is half-up; <see cref="Math.Round(double)"/> is banker's
        /// rounding, so 0.125 would print 12 where the Oracle prints 13. Explicitly half-away-from-zero
        /// to keep a fixture honest at the .5 boundaries that a 0.05 AP price produces.</para>
        /// </summary>
        public static int PercentForLabel(float hitChance)
        {
            return (int)Math.Round(hitChance * 100f, MidpointRounding.AwayFromZero);
        }

        // ── Presentation geometry the queue depends on ────────────────────────────────────

        /// <summary>
        /// <c>SatsCel.as:37-38</c> — <c>vis.scaleX = (u.scX + 10*n) / 100</c>, where <c>n</c> is how
        /// many cells that unit already carries. Three clicks on one unit read as three nested
        /// brackets, each 10 px larger than the last, so a player can see the burst size without a
        /// number.
        ///
        /// <para><b><c>n</c> is incremented before the scale is computed</b> (<c>++this.un.n</c> at
        /// <c>:35</c>, the scale two lines later), so the <i>first</i> cell on a unit is already
        /// <c>scX + 10</c>. Passing 0 here would make the first bracket body-sized and every later
        /// one off by ten.</para>
        /// </summary>
        public static float BracketScale(float bodySizePx, int cellsOnThisUnit)
        {
            return (bodySizePx + BracketGrowthPerShot * cellsOnThisUnit) / 100f;
        }

        /// <summary><c>GUI.setOd</c>'s <c>bar.scaleX = odv / 50</c> — both fills, same divisor.</summary>
        public static float OdBarScale(float od)
        {
            return od / OdBarFullScale;
        }

        /// <summary>
        /// The divisor guard used by every cost function.
        ///
        /// <para>AS3 divides by <c>skillConf</c> with no guard, and is safe only because the one path
        /// that could set it to 0 (<c>Sats.as:149-154</c>) exits before reaching a cost. The port
        /// keeps the guard anyway, matching the PipBuck's own AP line
        /// (<c>PipPageInvView.cs:1822</c>: <c>Mathf.Max(0.01f, skillConf)</c>) — a 1 % confidence is
        /// absurd in play but finite, and a division by zero would print <c>∞</c> into a live HUD.</para>
        /// </summary>
        private static float GuardSkillConf(float skillConf)
        {
            return skillConf > 0f ? skillConf : 0.01f;
        }
    }

    /// <summary>
    /// The inputs to <see cref="SatsRules.HitChance"/>, one field per value the Oracle's
    /// <c>getPrec</c> reads. A struct rather than a parameter list because there are eleven of them
    /// and three are floats that must not be swapped silently.
    ///
    /// <para>Every distance and size is in <b>world pixels</b> (1 Unity unit = 100 px,
    /// <c>TileQueryConstants.UnitToPixel</c>), which is the unit the Oracle works in.</para>
    /// </summary>
    public readonly struct SatsPrecInputs
    {
        /// <summary><c>weapon.precision</c> — the data <c>@prec</c> already scaled by 40
        /// (<c>Weapon.as:822</c>). <c>0</c> disables the term.</summary>
        public readonly float PrecisionPx;

        /// <summary><c>weapon.precMult</c> = <c>allPrecMult * pers[perk+"Prec"]</c>
        /// (<c>Weapon.as:976</c>, <c>:1006</c>). <b>Not</b> the situational multiplier.</summary>
        public readonly float PrecMult;

        /// <summary><c>pers.weaponSkills[weapon.skill]</c> — the skill <b>multiplier</b> (1 at tier 0,
        /// rising with points), not the tier.</summary>
        public readonly float SkillMultiplier;

        /// <summary><c>owner.precMultCont</c> — blindness / contusion / drunk / weak. <c>1</c> at rest,
        /// and the only value the port can produce today (the three counters are unported).</summary>
        public readonly float PrecMultCont;

        /// <summary><c>weapon.antiprec</c> — the minimum-range threshold in pixels (data × 40).</summary>
        public readonly float AntiPrecisionPx;

        /// <summary><c>weapon.deviation</c> — the spread cone in degrees.</summary>
        public readonly float DeviationDeg;

        /// <summary><c>owner.mazil</c> — a flat addend to the cone, in degrees.</summary>
        public readonly float Mazil;

        /// <summary><c>sats.skillConf</c> — 1 / 0.8 / 0.6 from the weapon-skill gap.</summary>
        public readonly float SkillConf;

        /// <summary><c>hypot(weapon.X - u.X, weapon.Y - u.Y)</c> — muzzle to unit, in pixels.</summary>
        public readonly float DistancePx;

        /// <summary><c>u.dexter</c> — <c>UnitStats.dexterity</c>, AS3 default 1.</summary>
        public readonly float TargetDexterity;

        /// <summary><c>u.scY</c> — the target's body height in pixels, used as the angular size.</summary>
        public readonly float TargetBodyHeightPx;

        public SatsPrecInputs(
            float precisionPx, float precMult, float skillMultiplier, float precMultCont,
            float antiPrecisionPx, float deviationDeg, float mazil, float skillConf,
            float distancePx, float targetDexterity, float targetBodyHeightPx)
        {
            PrecisionPx = precisionPx;
            PrecMult = precMult;
            SkillMultiplier = skillMultiplier;
            PrecMultCont = precMultCont;
            AntiPrecisionPx = antiPrecisionPx;
            DeviationDeg = deviationDeg;
            Mazil = mazil;
            SkillConf = skillConf;
            DistancePx = distancePx;
            TargetDexterity = targetDexterity;
            TargetBodyHeightPx = targetBodyHeightPx;
        }
    }
}
