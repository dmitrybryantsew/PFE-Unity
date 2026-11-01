using System;
using UnityEngine;

namespace PFE.Systems.Combat
{
    /// <summary>Which of <c>castCurse</c>'s three shapes a curse beat lays down.</summary>
    public enum CurseBeat
    {
        /// <summary><c>castCurse()</c> — one circle near the player with ±25/±15 px of jitter.</summary>
        Single,

        /// <summary><c>castCurse(1, 0|10|20)</c> — three circles, each with ±100/±50 px of extra spread.</summary>
        Spread,

        /// <summary>
        /// <c>castCurse(2, 0|4|8|12|16)</c> — five circles in a line across the player, phase 2 only.
        /// </summary>
        Line,
    }

    /// <summary>
    /// <c>fe/weapon/MagSymbol.as</c> and <c>UnitBossNecr.castCurse</c> — the curse circle: a position, a
    /// 100 px radius, a lifetime counted in ticks, and an effect applied to the player when the life runs
    /// out.
    ///
    /// <para><b>Why the delivery was the missing half.</b> The port already has the whole effect runtime
    /// — <c>ActiveEffectSet</c>, per-tick stat params, per-second payloads — and every one of Mchavi's
    /// nine curses already exists as a <c>Data/Resources/Effects</c> asset. What it did not have was
    /// anything that <i>places</i> a curse at a point in the world and applies it later, which is all
    /// <c>MagSymbol</c> is. Without it the boss's curse beat had to fake the damage directly, which is
    /// why it hurt through walls.</para>
    ///
    /// <para><b>Everything here is in AS3 room-local pixels: Y grows DOWN, 0 is the top of the room.</b>
    /// That is the oracle's own space and this file is a transcription, so keeping it makes every line
    /// traceable. It is <i>not</i> this port's <c>worldPx</c> (which is Y-up world, origin at land 0,0) —
    /// the caller converts at the boundary with
    /// <c>ZombieSuperMath.PortYToAs3Y</c>/<c>As3YToPortY</c> and by subtracting
    /// <c>WorldCoordinates.RoomOriginPixel</c>. Mixing the two silently mirrors every circle about the
    /// room's mid-line, which is a bug that reads as "the curse lands on the wrong side of her".</para>
    ///
    /// <para><b>Two different body points, on purpose.</b> <c>castCurse</c> aims at <c>gg.scY / 2</c> —
    /// the middle of the body — while <c>getRasst2</c> measures to <c>gg.scY * 0.75</c> — three quarters
    /// up. They do not agree, and that is not a transcription slip to be tidied away: it is the oracle's
    /// own arithmetic, so the circle sits half a body-height below the point it tests against. Reproducing
    /// it is the whole job; "fixing" it would move where the curse lands relative to a player standing at
    /// the edge of a circle.</para>
    /// </summary>
    public static class MagSymbolRules
    {
        // ── MagSymbol.as ──────────────────────────────────────────────────────────────────────────

        /// <summary><c>rad</c> — the application radius in room pixels (<c>MagSymbol.as:17</c>).</summary>
        public const float Radius = 100f;

        /// <summary>The life at which <c>Emitter.emit("magsymbol", …)</c> runs (<c>:43</c>).</summary>
        public const int EmitAtLife = 20;

        /// <summary>The life at which <c>spellCast()</c> runs (<c>:48</c>).</summary>
        public const int AppliesAtLife = 1;

        /// <summary><c>liv = 20 + param5</c> (<c>:37</c>).</summary>
        public const int BaseLifetime = 20;

        /// <summary>
        /// <c>MagSymbol</c>'s own <c>scY</c>, which its hit test adds as <c>scY / 2</c>
        /// (<c>Obj.as:203</c>). It never sets one, and <c>Obj</c>'s field defaults to 10
        /// (<c>Obj.as:22</c>), so the term is a constant 5 px — the marker's origin sits 5 px above the
        /// point it measures from. Not zero, and not a rounding detail: it is the only reason a circle
        /// centred exactly on the player's aim point is not a distance of exactly zero.
        /// </summary>
        public const float SymbolScY = 10f;

        // ── UnitBossNecr.as:castCurse ─────────────────────────────────────────────────────────────

        /// <summary><c>gg.dx * 15</c> / <c>gg.dy * 15</c> — the lead on a moving player (<c>:687-688</c>).</summary>
        public const float VelocityLookaheadTicks = 15f;

        /// <summary><c>gg.scY / 2</c> — the aim point for <i>placement</i> (<c>:688</c>).</summary>
        public const float PlacementBodyFraction = 0.5f;

        /// <summary>
        /// <c>gg.scY * 0.75</c> — the aim point for the <i>hit test</i> (<c>Obj.as:206</c>). Deliberately
        /// not equal to <see cref="PlacementBodyFraction"/>; see the class remarks.
        /// </summary>
        public const float HitBodyFraction = 0.75f;

        /// <summary><c>(Math.random() - 0.5) * 50</c> — ±25 px across (<c>:687</c>).</summary>
        public const float SingleJitterSpanX = 50f;

        /// <summary><c>(Math.random() - 0.5) * 30</c> — ±15 px up (<c>:688</c>).</summary>
        public const float SingleJitterSpanY = 30f;

        /// <summary><c>Math.random() * 200 - 100</c> — ±100 px extra across, spread arm (<c>:696</c>).</summary>
        public const float SpreadJitterSpanX = 200f;

        /// <summary><c>Math.random() * 100 - 50</c> — ±50 px extra up, spread arm (<c>:697</c>).</summary>
        public const float SpreadJitterSpanY = 100f;

        /// <summary>
        /// The clamp inset: <c>loc.limX - 100</c> / <c>100</c> (<c>:699-713</c>). A literal, not a
        /// fraction of the room — a small room still gets the same inset.
        /// </summary>
        public const float EdgeMargin = 100f;

        /// <summary>The line arm's spacing multiplier: <c>(param5 - 8) * 20</c> (<c>:691</c>).</summary>
        public const float LineStepPixels = 20f;

        /// <summary><c>liv = 20 + param5</c> (<c>MagSymbol.as:37</c>).</summary>
        public static int Lifetime(int spacing) => BaseLifetime + spacing;

        /// <summary>
        /// <c>castCurse()</c>'s default arm — one circle near the player (<c>:687-688</c>).
        /// </summary>
        /// <param name="playerAs3">The player's <c>X</c>/<c>Y</c>: its feet, room-local, Y down.</param>
        /// <param name="playerScY">The player's sprite height in pixels — AS3 <c>gg.scY</c>.</param>
        /// <param name="playerDelta">AS3 <c>dx</c>/<c>dy</c> per tick, <b>dy positive down</b>.</param>
        public static Vector2 SinglePosition(Vector2 playerAs3, float playerScY, Vector2 playerDelta,
                                             float rollX, float rollY) =>
            new Vector2(
                playerAs3.x + playerDelta.x * VelocityLookaheadTicks
                    + (rollX - 0.5f) * SingleJitterSpanX,
                playerAs3.y - playerScY * PlacementBodyFraction
                    + playerDelta.y * VelocityLookaheadTicks
                    + (rollY - 0.5f) * SingleJitterSpanY);

        /// <summary>
        /// <c>castCurse(1, …)</c>: the single arm plus a further ±100 px across and ±50 px up, which is
        /// what turns three circles into a scatter rather than three copies of one point
        /// (<c>:694-698</c>).
        /// </summary>
        public static Vector2 SpreadPosition(Vector2 playerAs3, float playerScY, Vector2 playerDelta,
                                             float rollX, float rollY,
                                             float spreadRollX, float spreadRollY)
        {
            Vector2 at = SinglePosition(playerAs3, playerScY, playerDelta, rollX, rollY);
            return new Vector2(
                at.x + spreadRollX * SpreadJitterSpanX - SpreadJitterSpanX * 0.5f,
                at.y + spreadRollY * SpreadJitterSpanY - SpreadJitterSpanY * 0.5f);
        }

        /// <summary>
        /// <c>castCurse(2, param5)</c>: a line across the player, no jitter (<c>:689-693</c>).
        ///
        /// <code>
        /// _loc3 = gg.X + gg.dx*15 + (param5 - 8) * 20 * (floor(gg.X) % 2 == 0 ? 1 : -1);
        /// _loc4 = gg.Y - gg.scY/2 + gg.dy*15;
        /// </code>
        ///
        /// <para>The direction flips with the parity of the player's x, which is the oracle's way of
        /// making the line come from whichever side he happens to be standing on. With
        /// <c>param5 = 0..16</c> the offsets are −160, −80, 0, +80, +160.</para>
        /// </summary>
        public static Vector2 LinePosition(Vector2 playerAs3, float playerScY, Vector2 playerDelta,
                                           int spacing)
        {
            int parity = ((int)Math.Floor(playerAs3.x)) % 2 == 0 ? 1 : -1;
            return new Vector2(
                playerAs3.x + playerDelta.x * VelocityLookaheadTicks
                    + (spacing - 8) * LineStepPixels * parity,
                playerAs3.y - playerScY * PlacementBodyFraction
                    + playerDelta.y * VelocityLookaheadTicks);
        }

        /// <summary>
        /// <c>Obj.getRasst2(gg) &lt; rad * rad</c> (<c>Obj.as:196-208</c>, tested at
        /// <c>MagSymbol.as:68</c>) — squared distance, strict, against a point
        /// <see cref="HitBodyFraction"/> up the player's body and <see cref="SymbolScY"/> half-height
        /// above the circle's own origin.
        ///
        /// <para><b>Squared, and no square root</b>, because the oracle keeps it that way — and note the
        /// oracle's NaN guard turns an unmeasurable distance into <c>-1</c>, which is <i>less</i> than
        /// <c>rad*rad</c> and therefore a hit. Finite inputs cannot produce NaN here, so this returns
        /// <c>false</c> rather than reproducing that; the difference is unreachable.</para>
        /// </summary>
        public static bool Hits(Vector2 playerAs3, float playerScY, Vector2 symbolAs3)
        {
            float dx = playerAs3.x - symbolAs3.x;
            float dy = playerAs3.y - playerScY * HitBodyFraction - symbolAs3.y + SymbolScY * 0.5f;
            return dx * dx + dy * dy < Radius * Radius;
        }

        /// <summary>
        /// <c>control()</c>'s three-arm draw (<c>:335-352</c>).
        ///
        /// <para><b>Takes a roll function, not two floats, because the draws short-circuit.</b> AS3 reads
        /// <c>if(phase == 2 &amp;&amp; isrnd(0.25)) … else if(isrnd(0.25)) …</c>: in phase 1 the first
        /// <c>isrnd</c> is never called at all, and in phase 2 the second is skipped when the first
        /// succeeds. Handing in a callable keeps that order — and therefore the number of draws —
        /// exactly as the oracle spends them, which matters because the same <c>Math.random</c> stream
        /// also feeds the cooldown and the curse pick.</para>
        /// </summary>
        public static CurseBeat ChooseBeat(int phase, Func<float> roll)
        {
            if (phase == 2 && roll() < 0.25f) return CurseBeat.Line;
            if (roll() < 0.25f) return CurseBeat.Spread;
            return CurseBeat.Single;
        }

        static readonly int[] Single = { 0 };
        static readonly int[] Spread = { 0, 10, 20 };
        static readonly int[] Line = { 0, 4, 8, 12, 16 };

        /// <summary>
        /// The <c>param5</c> values one beat uses — which are also its lifetimes and its line offsets
        /// (<c>:337-351</c>).
        /// </summary>
        /// <remarks>
        /// The arrays are returned by reference and are <b>not</b> to be mutated; they are shared so a
        /// curse beat allocates nothing. Callers only read.
        /// </remarks>
        public static int[] SpacingsFor(CurseBeat beat)
        {
            switch (beat)
            {
                case CurseBeat.Line: return Line;
                case CurseBeat.Spread: return Spread;
                default: return Single;
            }
        }

        /// <summary>
        /// <c>castCurse</c>'s four clamps, <b>in the oracle's order</b> (<c>:699-713</c>).
        ///
        /// <para>The order is load-bearing in a room narrower than <c>2 * EdgeMargin</c>: the oracle
        /// tests <c>&gt; lim - 100</c> first and <c>&lt; 100</c> second, so a degenerate range collapses
        /// to <b>100</b>, not to the midpoint. Writing the two comparisons the intuitive way round —
        /// lower bound first — answers differently there, which is the kind of quiet divergence a
        /// fixture on a normal room never sees.</para>
        /// </summary>
        public static Vector2 Clamp(Vector2 position, float limX, float limY) =>
            new Vector2(ClampAxis(position.x, limX), ClampAxis(position.y, limY));

        static float ClampAxis(float value, float lim)
        {
            if (value > lim - EdgeMargin) value = lim - EdgeMargin;
            if (value < EdgeMargin) value = EdgeMargin;
            return value;
        }
    }
}
