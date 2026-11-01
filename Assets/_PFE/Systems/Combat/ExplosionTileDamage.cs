using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The blast's tile sweep — AS3 <c>Bullet.explDestroy()</c> (<c>fe/weapon/Bullet.as:719-744</c>).
    ///
    /// <para><b>Why this exists as one call.</b> AS3 has exactly one implementation and every blast
    /// reaches it through <c>explRun()</c>, which opens with <c>if(this.destroy &gt; 0) this.explDestroy()</c>
    /// (<c>:707-710</c>) — a thrown grenade (<c>WThrow</c>), a mine (<c>Unit.explosion()</c> builds a
    /// throwaway <c>Bullet</c>), and a fired round all run the same loop. The port had it inlined in
    /// <see cref="PFE.Entities.Weapons.Projectile"/> only, so a <b>thrown</b> explosive and a mine dealt
    /// unit damage, drew their blast, and left the wall behind them pristine — no crater, no scar, no
    /// destruction. That is the shape of the defect: one copy of a rule, three callers, two of which
    /// never got it.</para>
    ///
    /// <para><b>Full damage per tile, no falloff.</b> The oracle hands <c>this.destroy</c> to every tile
    /// whose <i>centre</i> is inside the radius (<c>:735</c>), so a blast at the edge of its reach hits
    /// as hard as one at the middle. The falloff in <c>explGas</c> is for units only and must not be
    /// copied here.</para>
    ///
    /// <para><b>Colliders, not a tile loop.</b> The oracle walks the tile grid and tests each centre
    /// against <c>explRadius</c>; the port asks <see cref="Physics2D.OverlapCircleAll"/> and takes every
    /// <see cref="IDestructibleTile"/> it finds. Every non-air tile has exactly one collider, so the set
    /// is the same to within the half-tile slop of a circle-versus-box test — and it costs one query
    /// instead of <c>(2r/tile)²</c> of them.</para>
    /// </summary>
    public static class ExplosionTileDamage
    {
        /// <summary>
        /// Damages every destructible tile inside the blast. Returns how many tiles were reached.
        /// </summary>
        /// <param name="centre">Blast centre, Unity world.</param>
        /// <param name="radius">Blast radius, Unity world units.</param>
        /// <param name="destroyTiles">
        /// The weapon's <c>destroy</c> — tile hp per hit, <b>not</b> unit damage. <c>&lt;= 0</c> is the
        /// oracle's own gate: a weapon that cannot break tiles does not even look at them.
        /// </param>
        /// <param name="damageType">Carried for the refusal diagnostic; tiles ignore it, as <c>udar</c> does.</param>
        /// <param name="decal">The weapon's <c>tipdec</c> — what each tile is marked with.</param>
        public static int Apply(
            Vector3 centre, float radius, float destroyTiles, DamageType damageType, DecalType decal)
        {
            if (destroyTiles <= 0f || radius <= 0f) return 0;

            Collider2D[] hits = Physics2D.OverlapCircleAll(centre, radius);
            int reached = 0;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D hit = hits[i];
                if (hit == null || hit.isTrigger) continue;

                var tile = hit.GetComponent<IDestructibleTile>();
                if (tile == null) continue;

                tile.ApplyDestructionRadius(centre, radius, destroyTiles, damageType, decal);
                reached++;
            }

            return reached;
        }
    }
}
