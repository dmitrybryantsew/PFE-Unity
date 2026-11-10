using System;
using System.Collections.Generic;

namespace PFE.Sim.Combat.Sats
{
    /// <summary>
    /// The ballistic preview S.A.T.S. draws from the muzzle to the cursor — the port of
    /// <c>fe.weapon.Trasser.trass()</c> (<c>Trasser.as:56-114</c>).
    ///
    /// <para><b>What it is for, and why it is not the shot.</b> This is a <i>preview</i>: 100 stepped
    /// samples of where the round would go at the current aim angle, used to show the player the arc
    /// and where it stops. It does <b>not</b> predict the shot — the real round is spawned by the
    /// weapon with the same physics and the same spread, and spread is random. Treating the preview's
    /// end point as "where the bullet will land" is the classic misreading of this class.</para>
    ///
    /// <para><b>The integration, exactly as the Oracle does it</b> — which is <i>not</i> the same as
    /// the projectile's:</para>
    /// <list type="number">
    /// <item>100 iterations (<c>liv</c>, <c>:39</c>).</item>
    /// <item>Per iteration, gravity is added to <c>dy</c> <b>once</b>, before any movement
    ///   (<c>:73-74</c>).</item>
    /// <item>If either component is below <c>maxdelta</c> (9) the whole step is one sub-step;
    ///   otherwise it is <c>floor(max(|dx|,|dy|) / 9) + 1</c> sub-steps of <c>dx/n, dy/n</c>
    ///   (<c>:84-96</c>). The <c>+ 1</c> is the Oracle's, not a rounding convenience — dropping it
    ///   makes a fast round step 9 px past a wall.</item>
    /// <item>Every sub-step tests the tile at the new point and stops on the first solid one
    ///   (<c>:122-205</c>). <b>Units and props are never tested</b>: the preview passes through a
    ///   raider and stops at the wall behind them.</item>
    /// </list>
    ///
    /// <para><b>The bounce is not ported, and that is correct rather than lazy.</b> <c>is_skok</c> —
    /// the flag that makes the tracer ricochet (<c>skok = 0.5</c>) and settle (<c>tormoz = 0.7</c>) —
    /// is set in exactly one place, <c>WThrow.as:259</c>, which is the <i>grenade throw</i> preview.
    /// S.A.T.S. draws the weapon's own tracer, so its flag is always <c>false</c> and both branches are
    /// unreachable from this mode.</para>
    /// </summary>
    public static class SatsTrajectory
    {
        /// <summary><c>Trasser.liv</c> — 100 iterations (<c>:39</c>).</summary>
        public const int Steps = 100;

        /// <summary><c>World.maxdelta</c> — 9 px per sub-step (<c>:84</c>).</summary>
        public const float MaxDelta = 9f;

        /// <summary>
        /// Trace the arc. <paramref name="isBlocked"/> reports whether a world-pixel point is inside a
        /// solid tile; it is a delegate rather than a tile system so this stays executable offline.
        /// </summary>
        /// <param name="originPx">The muzzle, in world pixels — <c>trasser.X/Y</c>.</param>
        /// <param name="velocityPx">Initial velocity in px/tick, already multiplied by the weapon's
        ///   <c>speedMult</c> (<c>Weapon.as:1648-1650</c>).</param>
        /// <param name="gravityPxPerTickSquared"><c>World.ddy</c>, added to <c>dy</c> once per step,
        ///   and only for a weapon with <c>grav</c>.</param>
        /// <param name="points">Receives the polyline, starting at the origin. Never null.</param>
        /// <param name="endPx">Where the trace stopped — the impact point, or the last sample.</param>
        /// <param name="blocked">True when a solid tile stopped it.</param>
        public static void Trace(
            float originX, float originY, float velocityX, float velocityY, float gravityPxPerTickSquared,
            Func<float, float, bool> isBlocked,
            List<SatsTracePoint> points, out SatsTracePoint endPx, out bool blocked)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            points.Clear();

            float x = originX;
            float y = originY;
            float dx = velocityX;
            float dy = velocityY;

            points.Add(new SatsTracePoint(x, y));
            blocked = false;

            for (int step = 0; step < Steps; step++)
            {
                // Gravity once per step, before movement (`Trasser.as:73-74`).
                dy += gravityPxPerTickSquared;

                float ax = Math.Abs(dx);
                float ay = Math.Abs(dy);

                if (ax < MaxDelta && ay < MaxDelta)
                {
                    if (Advance(ref x, ref y, dx, dy, 1, isBlocked)) { blocked = true; break; }
                }
                else
                {
                    int sub = (int)Math.Floor(Math.Max(ax, ay) / MaxDelta) + 1;
                    for (int i = 0; i < sub; i++)
                    {
                        if (Advance(ref x, ref y, dx, dy, sub, isBlocked)) { blocked = true; break; }
                    }

                    if (blocked) break;
                }

                points.Add(new SatsTracePoint(x, y));
            }

            endPx = new SatsTracePoint(x, y);
        }

        /// <summary>
        /// One sub-step: move by <c>dx/n, dy/n</c>, then test. Returns <c>true</c> when the new point is
        /// inside a solid tile, in which case the point is left where it landed — the Oracle stops with
        /// <c>vse</c> set and the sample is the impact (<c>Trasser.as:134-140</c>).
        /// </summary>
        private static bool Advance(ref float x, ref float y, float dx, float dy, int n,
            Func<float, float, bool> isBlocked)
        {
            x += dx / n;
            y += dy / n;

            return isBlocked != null && isBlocked(x, y);
        }
    }

    /// <summary>
    /// One sample of a traced arc, in world pixels.
    ///
    /// <para><b>Why not <c>UnityEngine.Vector2</c>.</b> The trace is a pure function of numbers, and
    /// the one thing this project's arithmetic tests rely on is being able to execute the code outside
    /// Unity. A Unity type in the signature would put the whole file behind an engine reference for no
    /// benefit — the view converts one struct per point when it builds the polyline.</para>
    /// </summary>
    public readonly struct SatsTracePoint
    {
        public readonly float X;
        public readonly float Y;

        public SatsTracePoint(float x, float y)
        {
            X = x;
            Y = y;
        }
    }
}
