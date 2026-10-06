using System.Collections.Generic;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// One unit's axis-aligned bounds in world pixels — AS3's <c>X1..X2</c> / <c>Y1..Y2</c> on a
    /// <c>Unit</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>UnityEngine.Rect</c>. <see cref="UnitSweepMath"/> has to be executable
    /// outside Unity — the whole point of keeping the arithmetic here is that it can be linked as
    /// source into a console harness and run against deliberately-wrong variants, which a
    /// <c>Rect</c> parameter would prevent.
    /// </remarks>
    public readonly struct UnitBoxPx
    {
        public readonly float MinX;
        public readonly float MaxX;
        public readonly float MinY;
        public readonly float MaxY;

        public UnitBoxPx(float minX, float maxX, float minY, float maxY)
        {
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }

        /// <summary>Builds a box from a centre and a size, which is the shape a Unity collider reports.</summary>
        public static UnitBoxPx FromCentre(float centreX, float centreY, float widthPx, float heightPx)
        {
            float halfW = widthPx * 0.5f;
            float halfH = heightPx * 0.5f;

            return new UnitBoxPx(centreX - halfW, centreX + halfW, centreY - halfH, centreY + halfH);
        }

        /// <summary>
        /// AS3's test, verbatim: <c>X &gt;= u.X1 &amp;&amp; X &lt;= u.X2 &amp;&amp; Y &gt;= u.Y1 &amp;&amp; Y &lt;= u.Y2</c>
        /// (<c>Bullet.as:515</c>). Inclusive on all four edges.
        /// </summary>
        public bool Contains(float x, float y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

        public float Width  => MaxX - MinX;
        public float Height => MaxY - MinY;

        public override string ToString() =>
            $"[{MinX:F1}..{MaxX:F1}] x [{MinY:F1}..{MaxY:F1}]";
    }

    /// <summary>
    /// AS3's bullet-versus-unit collision, as pure arithmetic: advance a <b>point</b> in sub-steps of
    /// at most <c>World.maxdelta</c> pixels and test it against each unit's bounds after every
    /// sub-step.
    ///
    /// <para><b>Why this exists.</b> The port detected unit hits only through Unity's
    /// <c>OnTriggerEnter2D</c>, i.e. a discrete overlap sampled once per physics step. A bullet covers
    /// <b>40 px</b> in one tick (<c>skorost</c> 100 px/frame → 30 units/s at 30 Hz) while a unit box is
    /// 12–24 px, so whether a shot registered depended on where the tick boundary happened to fall.
    /// That is the reported "bullets sometimes go through enemies". AS3 never had this problem because
    /// it sub-steps; this class is that sub-step loop.</para>
    ///
    /// <para><b>Sub-step, not sweep — deliberately.</b> A swept point-versus-box test would be
    /// strictly more robust, and is <i>not</i> what this does. AS3's collision here is a point, and the
    /// project has already faced this exact fork once: <c>PhisBullet</c>'s tile collision is a
    /// sub-stepped point test, and <c>IPhysicsWorldService.TryGetRoomTileQueryAt</c> records that
    /// reproducing it with a swept shape "would give a grenade a hitbox it does not have in the
    /// original, which is a behaviour change disguised as an implementation change". The same
    /// reasoning applies here, so the point stays a point.</para>
    ///
    /// <para><b>The invariant, and why 9 px is the right number.</b> Each sub-step advances at most
    /// <c>max(|dx|, |dy|) / (floor(max(|dx|,|dy|)/9) + 1) ≤ 9</c> px, and a unit box is ≥ 12 px, so a
    /// point can never step <i>over</i> a box. The guarantee is arithmetic, not probabilistic — which
    /// is the entire point of the constant, and the reason a naive "sample twice per tick" patch would
    /// only have made the failure rarer rather than impossible.</para>
    /// </summary>
    public static class UnitSweepMath
    {
        /// <summary>
        /// The largest advance, in pixels, permitted between two collision tests. AS3:
        /// <c>World.maxdelta = 9</c> (<c>World.as:52</c>).
        ///
        /// <para>Held here rather than read from <c>TileQueryConstants.MaxDelta</c> so that this file
        /// has no Unity dependency at all and can be executed offline. The two are the same quantity,
        /// and <c>UnitSweepMathTests.MaxDelta_MatchesTheTileQueryConstant</c> fails if they ever
        /// drift apart — which is the only reason a duplicate is acceptable.</para>
        /// </summary>
        public const float MaxDeltaPx = 9f;

        /// <summary>
        /// How many sub-steps this tick's movement needs. AS3: <c>Bullet.as:199</c> /
        /// <c>:301</c> — <c>Math.floor(Math.max(Math.abs(dx), Math.abs(dy)) / World.maxdelta) + 1</c>.
        ///
        /// <para><b>The formula also covers AS3's short-circuit.</b> AS3 tests
        /// <c>if (|dx| &lt; maxdelta &amp;&amp; |dy| &lt; maxdelta) run()</c> before this, advancing the
        /// whole delta in one call. That branch is redundant: with both components under 9,
        /// <c>floor(max/9) + 1 == 1</c>, and one sub-step of <c>delta / 1</c> <i>is</i> the whole delta.
        /// Verified for both sides of the boundary — <c>8.9 → 1</c>, <c>9.0 → 2</c> — which is exactly
        /// where AS3 switches branches too (<c>|dx| &lt; 9</c> is false at 9). So one formula reproduces
        /// both, and there is no second branch here to drift out of step with the first.</para>
        ///
        /// <para>Always at least 1, including for zero movement: AS3 calls <c>run()</c> once for a
        /// stationary bullet, so the current position is tested every tick rather than only on the tick
        /// it was reached.</para>
        /// </summary>
        public static int StepsFor(float deltaX, float deltaY, float maxDeltaPx = MaxDeltaPx)
        {
            if (maxDeltaPx <= 0f) return 1;

            float largest = deltaX < 0f ? -deltaX : deltaX;
            float absY    = deltaY < 0f ? -deltaY : deltaY;
            if (absY > largest) largest = absY;

            int steps = (int)(largest / maxDeltaPx) + 1;

            return steps < 1 ? 1 : steps;
        }

        /// <summary>
        /// Advances the point from <paramref name="fromX"/>/<paramref name="fromY"/> along
        /// (<paramref name="deltaX"/>, <paramref name="deltaY"/>) in <see cref="StepsFor"/> equal
        /// sub-steps and reports the first sub-step whose position lies inside any box.
        ///
        /// <para><b>Advance first, then test</b> — AS3's order. <c>run()</c> opens with
        /// <c>X += dx / param1; Y += dy / param1</c> (<c>Bullet.as:417-418</c>) and only then reaches
        /// the tile lookup and the unit loop (<c>:503-515</c>). The origin is therefore <i>not</i>
        /// tested by this call; it was tested as the previous tick's final sub-step. Testing before
        /// advancing would report hits one sub-step early, which for a 9 px sub-step is a ~9 px
        /// positional error in every impact.</para>
        ///
        /// <para><b>First box in list order wins, not the nearest box.</b> AS3 iterates
        /// <c>loc.units</c> and acts on the first whose bounds contain the point, so a caller that
        /// needs a deterministic result must pass the boxes in a stable order. Within one sub-step two
        /// boxes can both contain the point; AS3 picks by list order, so this does too.</para>
        /// </summary>
        /// <param name="boxIndex">Index into <paramref name="boxes"/>, or -1 when nothing was hit.</param>
        /// <param name="hitX">Sub-step position that hit, world pixels.</param>
        /// <param name="hitY">Sub-step position that hit, world pixels.</param>
        /// <returns>True when some sub-step landed inside some box.</returns>
        public static bool TryFirstHit(
            float fromX, float fromY, float deltaX, float deltaY,
            IReadOnlyList<UnitBoxPx> boxes, float maxDeltaPx,
            out int boxIndex, out float hitX, out float hitY)
        {
            boxIndex = -1;
            hitX     = fromX;
            hitY     = fromY;

            if (boxes == null || boxes.Count == 0) return false;

            int steps = StepsFor(deltaX, deltaY, maxDeltaPx);

            float stepX = deltaX / steps;
            float stepY = deltaY / steps;

            float x = fromX;
            float y = fromY;

            for (int step = 0; step < steps; step++)
            {
                x += stepX;
                y += stepY;

                for (int i = 0; i < boxes.Count; i++)
                {
                    if (!boxes[i].Contains(x, y)) continue;

                    boxIndex = i;
                    hitX     = x;
                    hitY     = y;
                    return true;
                }
            }

            return false;
        }
    }
}
