using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.TileQuery;

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
    /// <para><b>Two destructibles, two lookups — since Stage D.</b> The sweep used to ask
    /// <see cref="Physics2D.OverlapCircleAll"/> for every <see cref="IDestructibleTile"/> in radius,
    /// which worked because every non-air tile carried a collider. Tiles no longer do, so the tile
    /// half is the oracle's own loop — every cell whose <i>centre</i> is inside the radius takes
    /// <c>this.destroy</c> (<c>:735</c>), full damage, no falloff — read from the room's tile grid.
    /// Door and barricade props keep their colliders and stay on the probe: a stamped cell is the
    /// door's business, exactly as before, when those cells had no collider either.</para>
    /// </summary>
    public static class ExplosionTileDamage
    {
        /// <summary>
        /// Damages every destructible tile inside the blast. Returns how many were reached.
        /// </summary>
        /// <param name="tileQuery">The room's tile seam — what the tile half of the sweep reads.
        /// Null (a blast outside every mirrored room, a fixture) skips tiles but still sweeps props.</param>
        /// <param name="centre">Blast centre, Unity world.</param>
        /// <param name="radius">Blast radius, Unity world units.</param>
        /// <param name="destroyTiles">
        /// The weapon's <c>destroy</c> — tile hp per hit, <b>not</b> unit damage. <c>&lt;= 0</c> is the
        /// oracle's own gate: a weapon that cannot break tiles does not even look at them.
        /// </param>
        /// <param name="damageType">Carried for the refusal diagnostic; tiles ignore it, as <c>udar</c> does.</param>
        /// <param name="decal">The weapon's <c>tipdec</c> — what each tile is marked with.</param>
        /// <param name="debugSettings">Carried for the tile-refusal diagnostic; null in fixtures.</param>
        public static int Apply(
            ITileQueryService tileQuery,
            Vector3 centre, float radius, float destroyTiles, DamageType damageType, DecalType decal,
            PfeDebugSettings debugSettings = null)
        {
            if (destroyTiles <= 0f || radius <= 0f) return 0;

            int reached = SweepProps(centre, radius, destroyTiles, damageType, decal);
            reached += SweepTiles(tileQuery, centre, radius, destroyTiles, decal, debugSettings);
            return reached;
        }

        // Door and barricade props — the IDestructibleTile colliders, which survive Stage D because
        // they are not tiles. Probe is the pre-Stage-D OverlapCircle half of the sweep, narrowed to
        // non-tile destructibles; the radius filter is the caller's, as AS3's explRadius gate is.
        private static int SweepProps(
            Vector3 centre, float radius, float destroyTiles, DamageType damageType, DecalType decal)
        {
            Collider2D[] hits = Physics2D.OverlapCircleAll(centre, radius);
            int reached = 0;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D hit = hits[i];
                if (hit == null || hit.isTrigger) continue;

                var prop = hit.GetComponent<IDestructibleTile>();
                if (prop == null) continue;

                prop.ApplyDestructionRadius(centre, radius, destroyTiles, damageType, decal);
                reached++;
            }

            return reached;
        }

        // The tile grid half — AS3 explDestroy's own walk: every cell whose centre is inside the
        // radius takes full damage (the falloff in explGas is for units only and must not be copied
        // here). Door-stamped cells are skipped because they are the prop's business, above — the
        // same answer the collider sweep gave, when a stamped cell carried no collider at all.
        private static int SweepTiles(
            ITileQueryService tileQuery,
            Vector3 centre, float radius, float destroyTiles, DecalType decal,
            PfeDebugSettings debugSettings)
        {
            RoomInstance room = tileQuery?.Room;
            if (room == null || room.tiles == null) return 0;

            Vector2 originPx  = tileQuery.OriginPixel;
            Vector2 centrePx  = (Vector2)centre * TileQueryConstants.UnitToPixel;
            float   radiusPx  = radius * TileQueryConstants.UnitToPixel;
            float   tileSize  = WorldConstants.TILE_SIZE;

            Vector2 localCentre = centrePx - originPx;
            int minTx = Mathf.Max(0, Mathf.FloorToInt((localCentre.x - radiusPx) / tileSize));
            int maxTx = Mathf.Min(room.width - 1, Mathf.FloorToInt((localCentre.x + radiusPx) / tileSize));
            int minTy = Mathf.Max(0, Mathf.FloorToInt((localCentre.y - radiusPx) / tileSize));
            int maxTy = Mathf.Min(room.height - 1, Mathf.FloorToInt((localCentre.y + radiusPx) / tileSize));

            int reached = 0;
            float radiusSq = radiusPx * radiusPx;

            for (int x = minTx; x <= maxTx; x++)
            {
                for (int y = minTy; y <= maxTy; y++)
                {
                    TileData tile = room.GetTileAtCoord(new Vector2Int(x, y));
                    if (tile == null) continue;
                    if (tile.doorOcclusion != TileData.NoDoorOcclusion) continue;

                    // The centre-in-radius test, in world pixels, against the cell centre AS3
                    // computes as (cellX + 0.5) * tileX (Bullet.as:735).
                    Vector2 cellCentreWorld = tile.GetBounds().center + originPx;
                    if ((cellCentreWorld - centrePx).sqrMagnitude > radiusSq) continue;

                    // The decal lands on the tile, not on the blast: AS3 hands hitTile the cell
                    // centre, so a grenade leaves one mark per damaged tile, scattered over the blast.
                    Vector2 tileCentreUnits = cellCentreWorld * TileQueryConstants.PixelToUnit;

                    if (TileDamageApplicator.ApplyHit(
                            tile, destroyTiles, decal, room, room.Decals, tileCentreUnits, debugSettings))
                    {
                        reached++;
                    }
                }
            }

            return reached;
        }
    }
}
