using System;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// AS3's <b>bullet-versus-TILE</b> collision, as pure arithmetic: advance a point in sub-steps of
    /// at most <c>World.maxdelta</c> pixels and ask whether a tile occupies it after each sub-step.
    ///
    /// <para><b>Why melee needs this at all.</b> In AS3 a club swing is a <c>Bullet</c>
    /// (<c>WClub.as:148-156</c> — <c>b = new Bullet(...)</c>, <c>b.tipBullet = 1</c>), and
    /// <c>setBullet</c> copies the weapon's <c>destroy</c> onto it (<c>Weapon.as:1674</c>). The weapon
    /// moves that bullet along the blade path with <c>bindMove</c> each frame, and
    /// <c>Bullet.bindMove</c> runs <c>run()</c> — which sub-steps the advance
    /// (<c>Bullet.as:295-308</c>) and, per sub-step, tests the bullet's <b>point</b> against the tile
    /// under it (<c>Bullet.as:432</c>, <c>:476</c>). On the first tile it overlaps it calls
    /// <c>loc.hitTile(tile, this.destroy, X, Y, this.tipDecal)</c> (<c>:489</c>) and latches
    /// <c>tilehit = true</c> (<c>:491</c>). <b>So a swing destroys exactly one tile — the first one the
    /// blade tip enters.</b></para>
    ///
    /// <para><b>Sub-step, not sweep.</b> The same reasoning as <see cref="UnitSweepMath"/>: a swept
    /// point-versus-box test would be more robust and is <i>not</i> what the oracle does. Keeping the
    /// point a point is what makes "first tile entered" mean the same thing on both sides.</para>
    ///
    /// <para><b>Advance first, then test</b> — AS3's order. <c>run()</c> opens with
    /// <c>X += dx / param1; Y += dy / param1</c> (<c>:417-418</c>) and only then reaches the tile
    /// lookup (<c>:432</c>). The origin is therefore <i>not</i> tested here: it is the previous
    /// frame's final sub-step, and re-testing it would report a hit one sub-step early.</para>
    ///
    /// <para><b>Unity-free on purpose.</b> The tile lookup is injected as a predicate, so this
    /// arithmetic can be linked as source into a console harness and executed against
    /// deliberately-wrong variants without a <c>GameObject</c>, a collider or an engine.</para>
    /// </summary>
    public static class MeleeTileSweepMath
    {
        /// <summary>
        /// Advances from <paramref name="fromX"/>/<paramref name="fromY"/> along
        /// (<paramref name="deltaX"/>, <paramref name="deltaY"/>) in <see cref="UnitSweepMath.StepsFor"/>
        /// equal sub-steps and reports the first sub-step position that
        /// <paramref name="isTileAtPx"/> claims.
        /// </summary>
        /// <param name="isTileAtPx">
        /// "Is there a damageable tile at this world-pixel position?" — the caller's tile lookup. A
        /// predicate rather than a collider list because AS3 asks by <i>position</i>
        /// (<c>loc.getAbsTile(X,Y)</c>), not by iterating candidates.
        /// </param>
        /// <param name="hitX">Sub-step position that hit, world pixels.</param>
        /// <param name="hitY">Sub-step position that hit, world pixels.</param>
        /// <returns>True when some sub-step landed on a tile.</returns>
        public static bool TryFirstTileHit(
            float fromX, float fromY, float deltaX, float deltaY,
            Func<float, float, bool> isTileAtPx,
            out float hitX, out float hitY,
            float maxDeltaPx = UnitSweepMath.MaxDeltaPx)
        {
            hitX = fromX;
            hitY = fromY;

            if (isTileAtPx == null) return false;

            int steps = UnitSweepMath.StepsFor(deltaX, deltaY, maxDeltaPx);

            float stepX = deltaX / steps;
            float stepY = deltaY / steps;

            float x = fromX;
            float y = fromY;

            for (int step = 0; step < steps; step++)
            {
                x += stepX;
                y += stepY;

                if (!isTileAtPx(x, y)) continue;

                hitX = x;
                hitY = y;
                return true;
            }

            return false;
        }
    }
}
