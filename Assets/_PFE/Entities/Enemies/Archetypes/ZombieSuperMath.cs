using System;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The pure half of <c>UnitZombie</c>'s super-attack system — <c>setSuper()</c>, <c>findSuper()</c>,
    /// <c>superSila()</c>, <c>superSila2()</c> and <c>superSilaVse()</c>
    /// (<c>UnitZombie.as:954-1228</c>), reduced to arithmetic on numbers.
    ///
    /// <para><b>Why a separate, Unity-free type.</b> The same reason <c>UnitJumpMath</c>,
    /// <c>UnitOverhangMath</c> and <c>WeaponHoldPointMath</c> exist: a rule that lives inside a
    /// <c>MonoBehaviour</c> can only be checked by running the game, and this one is a table of nine
    /// dispatch entries whose failure mode is "the wrong variant plays the wrong super" — visible only if
    /// you happen to spawn that exact variant and wait 90 ticks. Here every entry can be asserted
    /// offline.</para>
    ///
    /// <para><b>Two coordinate systems meet in this file, and that is the point.</b> The oracle's
    /// <c>findSuper()</c> works in room-local pixels with <b>Y growing downward</b> and tile row 0 at the
    /// <b>top</b>; this port's rooms use room-local pixels with <b>Y growing upward</b> and row 0 at the
    /// <b>bottom</b>. The conversions are one-liners, so they live here as named functions rather than
    /// being re-derived at each call site — a sign flip copied by hand is the exact class of bug
    /// <c>UnitJumpMath</c>'s remarks warn about.</para>
    /// </summary>
    public static class ZombieSuperMath
    {
        // ── The nine dispatch values (`<un ss='N'/>`) ──────────────────────────────────────────────
        //
        // The names are the oracle's own semantics, not invented flavour: `setSuper()` selects on these
        // integers and nothing else, and `superSila*` re-tests the same numbers. `ss='9'` is not a
        // distinct effect — `setSuper()` rewrites it to 1 with a stronger jump (`:962-969`), so it has a
        // constant here only so the rewrite has somewhere to be stated.

        /// <summary><c>ss='1'</c> — a ballistic pounce at the target ("Прыгун").</summary>
        public const int Pounce = 1;

        /// <summary><c>ss='2'</c> — telekinesis: drag the target toward a captured point ("Телекинетик").</summary>
        public const int Telekinesis = 2;

        /// <summary><c>ss='3'</c> — spit <c>zombivenom</c> ("Ядовитый").</summary>
        public const int VenomSpit = 3;

        /// <summary><c>ss='4'</c> — spit <c>zombiacid</c> ("Потрошитель").</summary>
        public const int AcidSpit = 4;

        /// <summary><c>ss='5'</c> — radiation aura that heals other zombies ("Светящийся гуль").</summary>
        public const int Radiation = 5;

        /// <summary><c>ss='6'</c> — quake: a shockwave on a 4-tick beat ("Гуль-солдат").</summary>
        public const int Quake = 6;

        /// <summary><c>ss='7'</c> — telekinesis plus <c>zombinecro</c> bolts.</summary>
        public const int NecroSpit = 7;

        /// <summary><c>ss='8'</c> — spit <c>zombipink</c>, and explode on death.</summary>
        public const int PinkSpit = 8;

        /// <summary><c>ss='9'</c> — rewritten to <see cref="Pounce"/> by <c>setSuper()</c>.</summary>
        public const int PounceStrong = 9;

        // ── Field defaults (`UnitZombie.as:35-63`) ─────────────────────────────────────────────────

        /// <summary><c>tZlo = 120</c> (<c>:37</c>) — how many chase ticks build up before the super fires.</summary>
        public const int DefaultTZlo = 120;

        /// <summary><c>tPrepSuper = 30</c> (<c>:39</c>) — the wind-up, in ticks.</summary>
        public const int DefaultTPrepSuper = 30;

        /// <summary><c>tSuper = 75</c> (<c>:41</c>) — how long state 7 lasts, in ticks.</summary>
        public const int DefaultTSuper = 75;

        /// <summary><c>vJump = 30</c> (<c>:43</c>) — the pounce speed, px per 30 Hz frame.</summary>
        public const float DefaultVJump = 30f;

        /// <summary><c>teleAccel = 2</c> (<c>:45</c>) — telekinesis acceleration per tick.</summary>
        public const float TelekinesisAccel = 2f;

        /// <summary><c>teleSpeed = 10</c> (<c>:47</c>) — the speed cap telekinesis accelerates up to.</summary>
        public const float TelekinesisSpeed = 10f;

        /// <summary><c>vDestroy = 20</c> (<c>:51</c>) — the wall-destroy speed (state 2/3/7 only).</summary>
        public const float DefaultVDestroy = 20f;

        /// <summary><c>radradMin = 200</c> / <c>radradMax = 800</c> (<c>:57-59</c>), overwritten to 120/1200 for ss 5.</summary>
        public const float RadiationRadiusMin = 120f;

        /// <summary>The radius the aura starts at when ss 5 fires (<c>:1006</c>).</summary>
        public const float RadiationRadiusMax = 1200f;

        /// <summary><c>radHeal = 30</c> (<c>:61</c>) — the heal per tick at the aura's centre.</summary>
        public const float DefaultRadHeal = 30f;

        /// <summary><c>tIsRes = 300</c> (<c>:67</c>) — the resurrection countdown.</summary>
        public const int ResurrectTicks = 300;

        /// <summary><c>optDistAtt = 200</c> (<c>:79</c>) — raised to 400 by ss 7 (<c>:1019</c>).</summary>
        public const float DefaultOptDistAttack = 200f;

        /// <summary><c>vDestroy = 50</c> for ss 6 (<c>:1015</c>).</summary>
        public const float QuakeVDestroy = 50f;

        /// <summary>The four-tick beat <c>superSila2</c> uses for ss 6 (<c>:1199</c>).</summary>
        public const int QuakePeriod = 4;

        /// <summary>
        /// Every quantity <c>setSuper()</c> derives for one skill index. A struct rather than five
        /// <c>out</c> parameters, because <c>setSuper()</c> is one table and the caller reads all of it at
        /// the same moment.
        /// </summary>
        public readonly struct Tuning
        {
            public Tuning(int tPrepSuper, int tSuper, int tZlo, float vJump, float vDestroy, float optDistAttack)
            {
                TPrepSuper = tPrepSuper;
                TSuper = tSuper;
                TZlo = tZlo;
                VJump = vJump;
                VDestroy = vDestroy;
                OptDistAttack = optDistAttack;
            }

            /// <summary>AS3 <c>tPrepSuper</c> — the wind-up length in ticks.</summary>
            public int TPrepSuper { get; }

            /// <summary>AS3 <c>tSuper</c> — the duration of state 7, in ticks.</summary>
            public int TSuper { get; }

            /// <summary>AS3 <c>tZlo</c> — chase ticks needed before the super fires.</summary>
            public int TZlo { get; }

            /// <summary>AS3 <c>vJump</c> — pounce speed, px/frame.</summary>
            public float VJump { get; }

            /// <summary>AS3 <c>vDestroy</c> — the speed at which this unit destroys walls.</summary>
            public float VDestroy { get; }

            /// <summary>AS3 <c>optDistAtt</c> — the contact window's half-width, px.</summary>
            public float OptDistAttack { get; }
        }

        /// <summary>
        /// AS3 <c>setSuper()</c> (<c>UnitZombie.as:954-1037</c>) as a table.
        ///
        /// <para><b>Order matters, and it is the oracle's.</b> <c>ss 9</c> runs the <c>ss 1</c> block
        /// first and then overwrites <c>tZlo</c> and <c>vJump</c>; <c>ss 7</c> runs the shared
        /// <c>2 || 7</c> block and then its own. Folding those into one arm each would lose the
        /// overwrite — <c>ss 9</c> would keep <c>tZlo = 90</c> instead of 75, i.e. it would fire later
        /// than the oracle's.</para>
        /// </summary>
        public static Tuning Resolve(int superSkill)
        {
            int tPrep = DefaultTPrepSuper;
            int tSuper = DefaultTSuper;
            int tZlo = DefaultTZlo;
            float vJump = DefaultVJump;
            float vDestroy = DefaultVDestroy;
            float optDist = DefaultOptDistAttack;

            // `if(ss == 1)` (:956-961) — then `if(ss == 9)` (:962-969) rewrites the index to 1 and
            // overrides two of the three values. Both blocks run for 9, in that order.
            if (superSkill == Pounce || superSkill == PounceStrong)
            {
                tPrep = 30;
                tSuper = 20;
                tZlo = 90;
            }

            if (superSkill == PounceStrong)
            {
                tZlo = 75;
                vJump = 40f;
            }

            // `if(ss == 2 || ss == 7)` (:970-976).
            if (superSkill == Telekinesis || superSkill == NecroSpit)
            {
                tPrep = 10;
                tSuper = 50;
                tZlo = 120;
            }

            // The three spit weapons share one timing block each, and all three are 18/10/150-ish:
            // ss 3 (:977-987) and ss 4 (:988-998) are 18/10/150, ss 8 (:1026-1036) is 18/10/120.
            if (superSkill == VenomSpit || superSkill == AcidSpit)
            {
                tPrep = 18;
                tSuper = 10;
                tZlo = 150;
            }

            if (superSkill == PinkSpit)
            {
                tPrep = 18;
                tSuper = 10;
                tZlo = 120;
            }

            // `if(ss == 5)` (:999-1008).
            if (superSkill == Radiation)
            {
                tPrep = 30;
                tSuper = 45;
                tZlo = 140;
            }

            // `if(ss == 6)` (:1009-1016).
            if (superSkill == Quake)
            {
                tPrep = 15;
                tSuper = 45;
                tZlo = 220;
                vDestroy = QuakeVDestroy;
            }

            // `if(ss == 7)`'s own tail (:1017-1025) — the longer contact window. It comes after the
            // shared 2||7 block, so it does not disturb the timings that block set.
            if (superSkill == NecroSpit)
            {
                optDist = 400f;
            }

            return new Tuning(tPrep, tSuper, tZlo, vJump, vDestroy, optDist);
        }

        /// <summary>
        /// The effect index after <c>setSuper()</c>'s rewrite — <c>9</c> becomes <c>1</c>
        /// (<c>UnitZombie.as:962-969</c>). Everything downstream (<c>findSuper</c>, <c>superSila</c>,
        /// <c>superSila2</c>) tests the rewritten value, so this must be applied before any of them.
        /// </summary>
        public static int EffectiveSkill(int superSkill)
            => superSkill == PounceStrong ? Pounce : superSkill;

        /// <summary>
        /// The weapon this skill fires in state 7, or <c>null</c> when it does not use one.
        /// <c>Weapon.create(this, "zombivenom")</c> etc. — <c>UnitZombie.as:984, 995, 1022, 1034</c>.
        /// </summary>
        public static string WeaponId(int superSkill)
        {
            switch (EffectiveSkill(superSkill))
            {
                case VenomSpit: return "zombivenom";
                case AcidSpit: return "zombiacid";
                case NecroSpit: return "zombinecro";
                case PinkSpit: return "zombipink";
                default: return null;
            }
        }

        /// <summary>True when state 7 fires the equipped weapon (<c>superSila</c>, <c>:1126-1129</c>).</summary>
        public static bool FiresWeaponOnSuper(int superSkill)
        {
            int s = EffectiveSkill(superSkill);
            return s == VenomSpit || s == AcidSpit || s == PinkSpit;
        }

        /// <summary>True when this skill's state 7 runs the telekinetic pull (<c>:1163-1192</c>).</summary>
        public static bool IsTelekinesis(int superSkill)
        {
            int s = EffectiveSkill(superSkill);
            return s == Telekinesis || s == NecroSpit;
        }

        /// <summary>
        /// AS3 <c>findSuper()</c> (<c>UnitZombie.as:1039-1098</c>) — where the pounce aims and where the
        /// telekinetic anchor is placed.
        ///
        /// <para><b>Coordinates are the oracle's</b>: room-local pixels, Y growing down, tile row 0 at
        /// the top. <paramref name="isAirAtAs3Tile"/> takes AS3 tile coordinates
        /// (<c>loc.getTile(col, row).phis == 0</c>); the caller converts.</para>
        ///
        /// <para><b>Only ss 1, 2 and 7 have a destination at all</b>, and only 1 and 2 are ever called
        /// from the trigger (<c>:733-736</c>) — 7 leaves <c>superX/superY</c> at their zero initialiser
        /// until <c>superSila2</c> captures them 20 ticks into state 7 (<c>:1165-1168</c>). This method
        /// reproduces that by returning the same values the oracle's field arithmetic would.</para>
        ///
        /// <para><b><c>superX</c> staying at -1 is a real outcome, not a failure to handle.</b> The
        /// pounce arm is gated on <c>superX &gt; 0 &amp;&amp; superY &gt; 0</c>
        /// (<c>:1109</c>), so a target overhead with no air column beside it produces a wind-up, then a
        /// state 7 that does nothing but hold <c>grav = 0</c> for <c>tSuper</c> ticks. That is the
        /// oracle's behaviour — do not "fix" it by falling back to the target's position.</para>
        /// </summary>
        /// <param name="effectiveSkill">A value already passed through <see cref="EffectiveSkill"/>.</param>
        /// <param name="celX">AS3 <c>celX</c> — the target's centre, room-local px.</param>
        /// <param name="celY">AS3 <c>celY</c> — the target's centre, room-local px, Y down.</param>
        /// <param name="selfX">AS3 <c>X</c> — this unit's body centre.</param>
        /// <param name="selfY">AS3 <c>Y</c> — this unit's feet, Y down.</param>
        /// <param name="bodyHeight">AS3 <c>scY</c> — this unit's body height in px.</param>
        /// <param name="tileSize">AS3 <c>Tile.tileY</c> — 40 px in this game.</param>
        /// <param name="isAirAtAs3Tile">Whether the AS3 tile at (col, row) is air (<c>phis == 0</c>).</param>
        public static void FindSuper(
            int effectiveSkill,
            float celX,
            float celY,
            float selfX,
            float selfY,
            float bodyHeight,
            float tileSize,
            Func<int, int, bool> isAirAtAs3Tile,
            out float superX,
            out float superY)
        {
            superX = -1f;
            superY = 0f;

            int col = Mathf.FloorToInt(celX / tileSize);

            // `_loc2_ = floor((celY + 40) / tileY)` (:1043) — 40 px BELOW the target's centre in Y-down
            // space, i.e. at its feet. The constant is the oracle's literal.
            int row = Mathf.FloorToInt((celY + 40f) / tileSize);

            if (effectiveSkill == Pounce)
            {
                // `superY = _loc2_ * tileY + tileY + scY` (:1046) — the bottom edge of the sampled row,
                // plus the body height. Read literally: the pounce's destination is a body-height BELOW
                // that edge, so the zombie's box lands overlapping the target's rather than standing on
                // its head. It is a slam, not a perch.
                superY = row * tileSize + tileSize + bodyHeight;
            }
            else if (IsTelekinesis(effectiveSkill))
            {
                // `superY = celY + 70` (:1050) — 70 px below the target's centre. In the telekinetic
                // pull this is the anchor the target is dragged toward, so "below the target" means the
                // player is pulled DOWN onto the zombie's level.
                superY = celY + 70f;
            }

            // `if(Y - celY > 120)` (:1052) — the target is at least 120 px ABOVE this unit's feet.
            if (selfY - celY > 120f)
            {
                // Look for an air column beside the target's own column: the pounce must land on
                // something, and the oracle's search widens outward one column at a time, left first
                // (:1054-1086). The nested order is load-bearing — the inner test is what decides
                // whether the nearer or the farther column is chosen.
                if (isAirAtAs3Tile(col, row))
                {
                    if (isAirAtAs3Tile(col - 1, row))
                    {
                        superX = col * tileSize;
                    }
                    else if (isAirAtAs3Tile(col + 1, row))
                    {
                        superX = (col + 1) * tileSize;
                    }
                }

                if (superX < 0f && isAirAtAs3Tile(col - 2, row))
                {
                    if (isAirAtAs3Tile(col - 1, row))
                    {
                        superX = (col - 1) * tileSize;
                    }
                    else if (isAirAtAs3Tile(col - 3, row))
                    {
                        superX = (col - 2) * tileSize;
                    }
                }

                if (superX < 0f && isAirAtAs3Tile(col + 2, row))
                {
                    if (isAirAtAs3Tile(col + 1, row))
                    {
                        superX = (col + 2) * tileSize;
                    }
                    else if (isAirAtAs3Tile(col + 3, row))
                    {
                        superX = (col + 3) * tileSize;
                    }
                }
            }
            else if (effectiveSkill == Pounce)
            {
                // Level with the target: aim straight at its centre (:1088-1092).
                superY = celY;
                superX = celX;
            }
            else if (IsTelekinesis(effectiveSkill))
            {
                // The anchor is where the zombie stands (:1093-1097) — the pull drags the target to it.
                superX = selfX;
                superY = selfY;
            }
        }

        /// <summary>
        /// AS3 <c>superSila()</c>'s pounce arm (<c>UnitZombie.as:1107-1123</c>) — the launch velocity and
        /// how many ticks the flight lasts.
        ///
        /// <para><b>The gate is <c>superX &gt; 0 &amp;&amp; superY &gt; 0 &amp;&amp; stay</c></b>
        /// (<c>:1109</c>): a failed <c>findSuper</c> and an airborne unit both refuse. The caller owns
        /// the <c>stay</c> half (it is <c>IsGrounded</c> here); this function owns the coordinate half and
        /// the arithmetic.</para>
        ///
        /// <para><b>Both components are returned in the port's Y-up space.</b> The oracle's
        /// <c>dy = l2 / l3 * vJump</c> is Y-down; passing port-space deltas produces the same direction
        /// with the sign already correct, which is why nothing here negates anything.</para>
        /// </summary>
        public static bool TryPounce(
            float superX,
            float superY,
            float selfX,
            float selfY,
            float vJump,
            out float velocityX,
            out float velocityY,
            out int superTicks)
        {
            velocityX = 0f;
            velocityY = 0f;
            superTicks = 0;

            if (superX <= 0f || superY <= 0f) return false;

            float dx = superX - selfX;
            float dy = superY - selfY;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            if (distance <= 0f) return false;

            velocityX = dx / distance * vJump;
            velocityY = dy / distance * vJump;

            // `tSuper = round(l3 / vJump); if(tSuper > 20) tSuper = 20;` (:1116-1120). Note this
            // OVERWRITES the table's tSuper — a pounce's duration is a function of the distance, not a
            // constant, and a far target still stops after 20 ticks (600 px at vJump 30).
            //
            // `floor(x + 0.5)` rather than `Mathf.RoundToInt(x)`, because the two disagree on exact ties
            // and the oracle's answer is the one that matters. AS3's `Math.round` rounds a positive .5
            // UP; `Mathf.RoundToInt` defers to .NET's `Math.Round`, whose default is banker's rounding,
            // so 12.5 would come back 12 and the strong pounce (vJump 40, distance 500) would fly one
            // tick short of the oracle. Distances are never negative here, so the positive rule is the
            // whole rule.
            superTicks = Mathf.FloorToInt(distance / vJump + 0.5f);
            if (superTicks > 20) superTicks = 20;

            return true;
        }

        /// <summary>
        /// AS3 <c>superSila2()</c>'s telekinetic arm (<c>UnitZombie.as:1163-1192</c>) — one tick of the
        /// pull, in the port's Y-up pixel space.
        ///
        /// <para><b>It accelerates, it does not set.</b> The oracle adds <c>teleAccel</c> per tick while
        /// the axis is under <c>teleSpeed</c>, so the target is dragged slowly at first and then at a
        /// constant 10 px/frame. Setting the velocity outright would make the pull feel like a teleport
        /// on the first tick.</para>
        ///
        /// <para><b>The speed cap is per axis, not on the magnitude.</b> A target already moving faster
        /// than <c>teleSpeed</c> on an axis is not slowed by the pull — the oracle's condition is
        /// <c>l1 &gt; 0 &amp;&amp; dx &lt; teleSpeed</c>, so a fast target simply stops being pushed on
        /// that axis.</para>
        /// </summary>
        /// <param name="anchorX">The captured anchor, port room-local px.</param>
        /// <param name="anchorY">The captured anchor, port room-local px.</param>
        /// <param name="targetX">The target's body centre, port room-local px.</param>
        /// <param name="targetY">The target's body centre, port room-local px.</param>
        /// <param name="targetVelocity">The target's current velocity, px/frame.</param>
        /// <param name="delta">The acceleration to ADD this tick, px/frame.</param>
        public static void TelekinesisStep(
            float anchorX,
            float anchorY,
            float targetX,
            float targetY,
            Vector2 targetVelocity,
            out Vector2 delta)
        {
            delta = Vector2.zero;

            float dx = anchorX - targetX;
            float dy = anchorY - targetY;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            if (distance <= 0f) return;

            dx = dx / distance * TelekinesisAccel;
            dy = dy / distance * TelekinesisAccel;

            if ((dx > 0f && targetVelocity.x < TelekinesisSpeed) ||
                (dx < 0f && targetVelocity.x > -TelekinesisSpeed))
            {
                delta.x = dx;
            }

            if ((dy > 0f && targetVelocity.y < TelekinesisSpeed) ||
                (dy < 0f && targetVelocity.y > -TelekinesisSpeed))
            {
                delta.y = dy;
            }
        }

        /// <summary>
        /// AS3 <c>superSila2()</c>'s radiation arm (<c>:1193-1196</c>) — the aura's strength bleeds from
        /// <c>radMax</c> to <c>radMin</c> across the whole of state 7.
        /// </summary>
        public static float RadiationDecay(float current, float radMax, float radMin, int tSuper)
        {
            if (tSuper <= 0) return radMin;
            return current - (radMax - radMin) / tSuper;
        }

        /// <summary>
        /// AS3 <c>superSila()</c>'s radiation heal (<c>:1134-1144</c>) — the falloff is linear in
        /// distance and the heal reaches zero exactly at the aura's edge.
        /// </summary>
        public static float RadiationHeal(float radHeal, float radius, float distance)
        {
            if (radius <= 0f || distance >= radius) return 0f;
            if (distance < 0f) distance = 0f;
            return radHeal * (radius - distance) / radius;
        }

        /// <summary>
        /// Whether the quake beat fires on this tick — <c>if(aiTCh % 4 == 1)</c>
        /// (<c>UnitZombie.as:1199</c>). Exposed so the cadence is one expression rather than a literal
        /// repeated in the brain and in a test.
        /// </summary>
        public static bool IsQuakeBeat(int stateTimerTicks) => stateTimerTicks % QuakePeriod == 1;

        // ── Coordinate conversions between the oracle and this port's room space ───────────────────

        /// <summary>AS3 room-local Y (down-positive, 0 at the top) → port room-local Y (up-positive, 0 at the bottom).</summary>
        public static float As3YToPortY(float as3Y, float roomHeightPixels)
            => roomHeightPixels - as3Y;

        /// <summary>Port room-local Y (up-positive) → AS3 room-local Y (down-positive).</summary>
        public static float PortYToAs3Y(float portY, float roomHeightPixels)
            => roomHeightPixels - portY;

        /// <summary>
        /// AS3 tile row (0 at the top) → this port's tile row (0 at the bottom). The port's
        /// <c>RoomInstance.tiles</c> is indexed bottom-up, which is the opposite of <c>Location.space</c>.
        /// </summary>
        public static int As3RowToPortRow(int as3Row, int roomRows) => roomRows - 1 - as3Row;
    }
}
