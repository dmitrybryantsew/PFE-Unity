using System;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// The player's two bare-hoof strikes, as the oracle writes them: <c>WKick.actions()</c>
    /// (<c>weapon/WKick.as:24-108</c>).
    ///
    /// <para><b>What the two attacks actually are.</b> They are <i>one</i> class and <i>one</i> weapon —
    /// <c>punchWeapon = new WKick(this,"punch")</c> (<c>UnitPlayer.as:431</c>) — with a boolean mode
    /// flag, not two weapons and not two code paths chosen by the cursor:</para>
    /// <list type="bullet">
    /// <item><c>kick = false</c> — the <b>front</b> strike: five horizontal sweeps from 60 px to 20 px
    /// ahead, at 10/20/30/40/50 px above the feet, sound <c>m_med</c>, damage ×1, tile budget = the
    /// weapon's own <c>destroy</c> (4).</item>
    /// <item><c>kick = true</c> — the <b>back</b> strike: two sweeps from 70 px to 20 px on the
    /// <i>opposite</i> side (<c>storona = -owner.storona</c>, <c>:59</c>), at 30 and 50 px, sound
    /// <c>m_big</c>, damage ×2, knock ×1.5, and the tile budget is <c>Pers.kickDestroy</c> (30) —
    /// which is what lets a bare kick break walls a punch cannot.</item>
    /// </list>
    ///
    /// <para><b>The flag is the RUN key, not the cursor.</b> <c>UnitPlayer.as:2379</c> does
    /// <c>(this.punchWeapon as WKick).kick = this.ctr.keyRun;</c> immediately before
    /// <c>attack()</c>, and <c>keyRun</c> is Shift (<c>inter/Ctr.as:23</c>). The cursor only picks
    /// which way the body faces (<c>storona</c>) and where the punch sprite is drawn
    /// (<c>UnitPlayer.setPunchWeaponPos</c>, <c>:3866-3871</c>); <c>WKick</c> never reads
    /// <c>celX</c>/<c>celY</c>. Holding run while striking is therefore the whole rule, and the
    /// strike lands <i>behind</i> you — which is exactly the "back hooves" the owner described, and
    /// the reason it reads as "the cursor is behind me".</para>
    ///
    /// <para><b><c>WPunch</c> is not this.</b> <c>WPunch.as</c> is the <i>scorpion</i> punch
    /// (<c>punch='1'</c> on <c>scorppunch</c>/<c>scorp2punch</c>/<c>scorp3punch</c>): it aims at the
    /// cursor, fires at <c>rapid-5</c>, and its back-hit branch is gated on <c>WPunch.zadok</c> —
    /// a field <b>no code in the tree ever sets</b> (grep: declared at <c>WPunch.as:10</c>, read at
    /// <c>:47</c>, assigned nowhere), so that branch is dead in the oracle. The port modelled the
    /// dead branch and missed the live one; this file is the live one.</para>
    ///
    /// <para><b>Everything here is in AS3 pixels with Y running DOWN</b>, the oracle's own space.
    /// The caller converts once (<c>TileQueryConstants.PixelToUnit</c> + a Y mirror) so a sign error
    /// cannot hide inside a rule.</para>
    /// </summary>
    public static class HoofStrikeRules
    {
        // ── Timing ────────────────────────────────────────────────────────────

        /// <summary>
        /// Frames before the end of the swing at which the bullet is armed:
        /// <c>if(t_attack == rapid - 8)</c> (<c>WKick.as:37</c>).
        ///
        /// <para>WKick has <b>one</b> fire offset for both modes — the punch is not the <c>rapid-5</c>
        /// of <c>WPunch.as:28</c>. The port fired both at 5.</para>
        /// </summary>
        public const int FireFrameOffset = 8;

        // ── Geometry ──────────────────────────────────────────────────────────

        /// <summary><c>X + storona * 60</c> — the front strike's outer end (<c>WKick.as:85-101</c>).</summary>
        public const float PunchReachPx = 60f;

        /// <summary><c>X + storona * 70</c> — the back strike's outer end (<c>WKick.as:73-79</c>).
        /// The kick reaches 10 px further than the punch.</summary>
        public const float KickReachPx = 70f;

        /// <summary><c>X + storona * 20</c> — the inner end of every sweep, i.e. the body.</summary>
        public const float InsetPx = 20f;

        /// <summary><c>Y - 30</c> — the lower of the two lines the kick sweeps, and one of the punch's five.</summary>
        public const float LowLinePx = 30f;

        /// <summary><c>Y - 50</c> — the upper line; also the line the probe compares against.</summary>
        public const float HighLinePx = 50f;

        /// <summary><c>while(_loc4_ &lt;= 5)</c> — the punch's height ladder is 10,20,30,40,50 px.</summary>
        public const int PunchLineCount = 5;

        /// <summary>Spacing of the punch's height ladder (<c>_loc4_ * 10</c>).</summary>
        public const float PunchLineStepPx = 10f;

        /// <summary>
        /// The height the punch's ladder skips: <c>if(_loc4_ != 3 ...)</c> (<c>WKick.as:99</c>), because
        /// 30 px was already bound as its own line just above the loop.
        /// </summary>
        public const int PunchLadderSkippedStep = 3;

        // ── Damage ────────────────────────────────────────────────────────────

        /// <summary><c>b.damage *= 2</c> on the back strike (<c>WKick.as:67</c>).</summary>
        public const float KickDamageScale = 2f;

        /// <summary><c>b.otbros *= 1.5</c> on the back strike (<c>WKick.as:68</c>).</summary>
        public const float KickKnockbackScale = 1.5f;

        /// <summary>
        /// <c>dopDamage</c> the two modes carry for the stun proc — 60 for the kick, 30 for the punch
        /// (<c>WKick.as:70</c>, <c>:55</c>). Recorded here because the value is part of the branch even
        /// though the port has no consumer for it yet (<c>DopChance</c>/<c>DopDamage</c> are written by
        /// nobody and read by nobody).
        /// </summary>
        public const float KickStunDamage = 60f;

        /// <summary>See <see cref="KickStunDamage"/> — the front strike's value.</summary>
        public const float PunchStunDamage = 30f;

        /// <summary><c>b.knocky = -0.1</c> (<c>WKick.as:43</c>) — the strikes knock almost flat.</summary>
        public const float KnockY = -0.1f;

        // ── Sound ─────────────────────────────────────────────────────────────

        /// <summary><c>Snd.ps("m_big",…)</c> — the back strike (<c>WKick.as:81</c>).</summary>
        public const string KickSound = "m_big";

        /// <summary><c>Snd.ps("m_med",…)</c> — the front strike (<c>WKick.as:105</c>).</summary>
        public const string PunchSound = "m_med";

        // ── The tile probe ────────────────────────────────────────────────────

        /// <summary>
        /// <c>_loc1_</c>: "there is a low obstacle in front of me, strike over it".
        ///
        /// <para>AS3 (<c>WKick.as:60-65</c>, <c>:85-90</c>) reads the tile 60 px ahead at 30 px up and
        /// at 50 px up, and sets the flag when <b>both exist</b> and <c>upper.thre &lt; lower.thre</c> —
        /// a <i>strict</i> comparison, so two tiles of equal toughness take the normal path. A wall
        /// whose top row is weaker (or thin) than its body is the case this exists for: the strike then
        /// binds the high line <i>first</i>, so the bullet crosses the weak top before the solid body.</para>
        ///
        /// <para>Note the probe reach is <b>60 for both modes</b> even though the kick sweeps to 70 —
        /// <c>:60-61</c> is inside the kick branch and still says 60.</para>
        /// </summary>
        public static bool OverLowTile(bool lowerExists, bool upperExists, float lowerThre, float upperThre)
            => lowerExists && upperExists && upperThre < lowerThre;

        // ── The segment list ──────────────────────────────────────────────────

        /// <summary>
        /// The bullet's path for one strike, in the order <c>bindMove</c> was called.
        ///
        /// <para><b>Order is not cosmetic.</b> AS3 pushes each <c>bindMove</c> into the bullet's
        /// waypoint list and the bullet walks them in order at <c>speed</c> px/frame
        /// (<c>Bullet.as</c>), so the first segment is the first tile it can hit. The two branches order
        /// their lines differently for the same reason: the high line goes first when there is a low
        /// obstacle to clear.</para>
        /// </summary>
        /// <param name="kick">The run-key flag (<c>WKick.kick</c>).</param>
        /// <param name="overLowTile">The probe's answer, <see cref="OverLowTile"/>.</param>
        /// <param name="x"><c>owner.X</c> — the body's centre x, in AS3 px.</param>
        /// <param name="feetY"><c>owner.Y</c> — the feet, in AS3 px, Y down.</param>
        /// <param name="storona"><c>owner.storona</c> — +1 right, −1 left. The kick mirrors it itself.</param>
        public static HoofSegment[] Segments(bool kick, bool overLowTile, float x, float feetY, float storona)
        {
            float s = storona >= 0f ? 1f : -1f;

            if (kick)
            {
                // WKick.as:59 — the strike is behind the body.
                s = -s;

                // WKick.as:71-80 — two lines only, and the order swaps on the probe.
                return overLowTile
                    ? new[]
                    {
                        Line(x, feetY, s, KickReachPx, HighLinePx),
                        Line(x, feetY, s, KickReachPx, LowLinePx),
                    }
                    : new[]
                    {
                        Line(x, feetY, s, KickReachPx, LowLinePx),
                        Line(x, feetY, s, KickReachPx, HighLinePx),
                    };
            }

            // WKick.as:91-104 — five lines: the two fixed ones and a 10..50 px ladder that skips the
            // 30 px rung (already bound) and, when there is a low obstacle, the 50 px rung (bound first).
            var segments = new HoofSegment[PunchLineCount];
            int n = 0;

            if (overLowTile) segments[n++] = Line(x, feetY, s, PunchReachPx, HighLinePx);
            segments[n++] = Line(x, feetY, s, PunchReachPx, LowLinePx);

            for (int step = 1; step <= PunchLineCount; step++)
            {
                if (step == PunchLadderSkippedStep) continue;
                if (overLowTile && step == PunchLineCount) continue;

                segments[n++] = Line(x, feetY, s, PunchReachPx, step * PunchLineStepPx);
            }

            // The two skips are complementary, so the punch is always five lines. Asserted rather
            // than assumed: a future edit to the ladder constants must not leave a hole.
            if (n != segments.Length)
                throw new InvalidOperationException(
                    $"HoofStrikeRules: punch built {n} segments, expected {segments.Length}.");

            return segments;
        }

        /// <summary>
        /// One sweep: from <paramref name="reach"/> px out, back to <see cref="InsetPx"/> px out, at
        /// <paramref name="heightPx"/> above the feet. AS3 writes these as
        /// <c>b.bindMove(X + storona*R, Y - H, X + storona*20, Y - H)</c>.
        /// </summary>
        private static HoofSegment Line(float x, float feetY, float s, float reach, float heightPx)
            => new HoofSegment(
                fromX: x + s * reach,
                fromY: feetY - heightPx,
                toX:   x + s * InsetPx,
                toY:   feetY - heightPx);
    }

    /// <summary>
    /// One <c>bindMove</c> of a hoof strike, in <b>AS3 pixels, Y down</b>. See
    /// <see cref="HoofStrikeRules.Segments"/>.
    /// </summary>
    public readonly struct HoofSegment
    {
        public readonly float FromX;
        public readonly float FromY;
        public readonly float ToX;
        public readonly float ToY;

        public HoofSegment(float fromX, float fromY, float toX, float toY)
        {
            FromX = fromX;
            FromY = fromY;
            ToX   = toX;
            ToY   = toY;
        }

        /// <summary>
        /// The height of this line above the feet, in px — positive is up. The oracle writes the line as
        /// <c>Y - h</c> with Y down, so the caller can recover <c>h</c> as
        /// <c>feetY - FromY</c> without re-deriving the sign.
        /// </summary>
        public float HeightAboveFeet(float feetY) => feetY - FromY;
    }

    /// <summary>
    /// The oracle's per-unit tile query at a point, as the strike needs it: does a tile exist there,
    /// and what is its destruction threshold (<c>Tile.thre</c>)?
    ///
    /// <para>Supplied by the caller because the controller is a plain class with no world — the same
    /// seam <c>IMeleeHitVolume</c> exists for. <see cref="Exists"/> is AS3's
    /// <c>Boolean(loc.getAbsTile(...))</c>: a null tile makes the whole probe false, and a tile that
    /// exists with a threshold of 0 is still a tile.</para>
    /// </summary>
    public readonly struct HoofTileProbe
    {
        public readonly bool  Exists;
        public readonly float Threshold;

        public HoofTileProbe(bool exists, float threshold)
        {
            Exists    = exists;
            Threshold = threshold;
        }

        /// <summary>AS3's <c>getAbsTile</c> returning null.</summary>
        public static HoofTileProbe Missing => new HoofTileProbe(false, 0f);
    }

    /// <summary>
    /// The body a hoof strike is thrown from, in <b>Unity world units</b>: <c>owner.X</c>,
    /// <c>owner.Y</c> (the <b>feet</b> — not the body centre the melee spring uses) and
    /// <c>owner.storona</c>.
    ///
    /// <para><b>Why the feet and not the centre.</b> Every line <c>WKick</c> binds is written as
    /// <c>Y - h</c> against <c>owner.Y</c> (<c>WKick.as:60-101</c>), and a unit's <c>Y</c> in AS3 is
    /// the point its feet stand on. The melee spring's <c>OwnerFrame</c> carries
    /// <c>Y - scY/2</c> instead, which is right for a swing's pivot and wrong for this — 35 px out on
    /// the player, i.e. every strike would land a body-height too high.</para>
    ///
    /// <para>Set per tick by the caller, like every other owner-frame seam in this port. A null frame
    /// falls back to the hold point with the aim deciding the side, which is the port's old
    /// approximation and is only for tests.</para>
    /// </summary>
    public readonly struct HoofOwnerFrame
    {
        /// <summary><c>owner.X</c>.</summary>
        public readonly float X;

        /// <summary><c>owner.Y</c> — the feet, in world units (Y up).</summary>
        public readonly float FeetWorldY;

        /// <summary><c>owner.storona</c> — +1 right, −1 left.</summary>
        public readonly float Storona;

        public HoofOwnerFrame(float x, float feetWorldY, float storona)
        {
            X          = x;
            FeetWorldY = feetWorldY;
            Storona    = storona >= 0f ? 1f : -1f;
        }
    }
}
