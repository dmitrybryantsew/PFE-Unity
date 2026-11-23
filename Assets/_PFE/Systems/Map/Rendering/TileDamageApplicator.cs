using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Systems.Map.Rendering.RoomTerrain;
using PFE.Systems.Map.TileQuery;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The tile side of one hit — AS3 <c>Location.hitTile</c> → <c>Tile.udar</c> → <c>Tile.die</c> —
    /// as a static, GameObject-free operation.
    ///
    /// <para><b>Where this lived before.</b> The behaviour was owned by <c>TileCollider.DamageThisTile</c>,
    /// because a hit reached the tile through the per-tile collider GameObject the quarter-terrain path
    /// used to build. Stage D retires those objects: the chain sweep decides a round stopped, the tile
    /// seam decides a thrown object did, and neither produces a collider to resolve. So the rule moved
    /// here, keyed by <see cref="TileData"/> — the same rule, the same order (rule → impact decal →
    /// refusal log → death decal → destroy → notify), with the tile's world position derived from
    /// <see cref="TileData.GetBounds"/> and the room origin instead of a transform.</para>
    ///
    /// <para><b>The decal layer arrives from the caller.</b> The room's <see cref="TileDecalLayer"/> is
    /// owned by the room's visual controller; the data side reaches it through
    /// <see cref="RoomInstance.Decals"/>, which is null for a room nobody is drawing (a test, the
    /// headless harness) — the oracle's own <c>if(this.active)</c> guard, so a null layer stamps
    /// nothing rather than throwing.</para>
    /// </summary>
    public static class TileDamageApplicator
    {
        /// <summary>
        /// One hit on one tile. Returns true when the hit did something — the tile took damage or died —
        /// false when the rule refused it (below threshold, indestructible, ladder, <c>desoff</c> room),
        /// which is the distinction <c>TileCollisionSystem.ApplyDamage</c> used its return value for.
        /// </summary>
        /// <param name="tile">The tile struck. Never null.</param>
        /// <param name="destroyAmount">The weapon's <c>destroy</c> — tile hp per hit.</param>
        /// <param name="decal">The weapon's <c>tipdec</c> — what the tile is marked with.</param>
        /// <param name="room">The room that owns the tile. Used for the <c>desoff</c>/<c>active</c>
        /// gates, the death-decal neighbour scan and the mutation notification.</param>
        /// <param name="decals">Where marks land, or null when the room has no picture.</param>
        /// <param name="impactPointWorldUnits">The contact point in Unity world units — projected into
        /// the tile's solid rect so the mark lands on the tile that was struck, exactly as
        /// <c>TileCollider.DecalPointFor</c> did with the transform it no longer has.</param>
        /// <param name="debugSettings">Carried for the refusal diagnostic; null in fixtures.</param>
        public static bool ApplyHit(
            TileData tile,
            float destroyAmount,
            DecalType decal,
            RoomInstance room,
            TileDecalLayer decals,
            Vector2 impactPointWorldUnits,
            PfeDebugSettings debugSettings)
        {
            if (tile == null) return false;

            bool destroyOn  = room == null || room.environment == null || room.environment.destroyOn;
            bool roomActive = room == null || room.isActive;

            // The gate order is TileHitRules' — `udar` only knows `indestruct` and `thre`;
            // `Location.hitTile` wraps it in the `100` marker, the `desoff` room gate and the `phis`
            // gate, and it is the wrapper that decides whether a mark appears.
            TileHitDecision decision = TileHitRules.Resolve(
                tile,
                destroyAmount,
                (int)decal,
                destroyOn: destroyOn,
                roomActive: roomActive);

            if (decision.Outcome == TileHitOutcome.Ignored ||
                decision.Outcome == TileHitOutcome.Refused ||
                decision.Outcome == TileHitOutcome.VisualOnly)
            {
                if (decision.Outcome == TileHitOutcome.Refused)
                {
                    LogRefusal(tile, debugSettings, decision.Refusal, destroyAmount);
                }

                return false;
            }

            if (decision.DrawDecal)
            {
                StampDecal(tile, room, decals, decision, impactPointWorldUnits);
            }

            if (!decision.ShouldDie) return true;

            // `Location.as:2548-2553` — the death decal is stamped by the *same* branch that kills the
            // tile, and it is the only decal a destroyed tile ever gets.
            if (roomActive)
            {
                StampDeathDecal(tile, room, decals, decision);
            }

            DestroyTile(tile, room);
            return true;
        }

        // AS3 `Tile.die()`: phis=0, opac=0, visuals cleared. The picture is a function of the tile
        // grid, so the terrain renderer redraws from TilesMutated; the chain mirror rebuilds from the
        // same notification. The one-tile border is TileCollider's own rule: removing a tile splits
        // the run it belonged to and can expose new faces on its neighbours.
        private static void DestroyTile(TileData tile, RoomInstance room)
        {
            tile.Destroy();

            if (room != null)
            {
                Vector2Int coord = tile.gridPosition;
                room.NotifyTilesMutated(new RectInt(coord.x - 1, coord.y - 1, 3, 3));
            }
        }

        // AS3 `Grafon.tileDie(tile, type)` — the decal a destroyed tile leaves behind. The position is
        // the centre of the tile's solid rect (the tile objects used to sit there; the arithmetic now
        // lives here), and the erase-then-draw order is Grafon's own.
        private static void StampDeathDecal(
            in TileData tile, RoomInstance room, TileDecalLayer decals, in TileHitDecision decision)
        {
            if (decals == null) return;

            TileDecalResult decal = TileDecalRules.ResolveDestroy(
                decision.DecalType,
                (int)tile.material,
                fake: false);

            Vector3 centre = TileCentreWorldUnits(tile, room);
            float cell = RoomTerrainGeometry.DefaultCellSize;

            decals.ClearCell(centre, cell);
            decals.StampDeath(decal, centre, cell, SolidNeighbours(tile, room), Random.value);
        }

        // AS3 `Grafon.dyrka` — the hit mark a non-lethal hit leaves. The rolls are the oracle's own
        // (`_loc11_`, `_loc12_` and the gate roll), which is why this stays on UnityEngine.Random:
        // presentation jitter, exactly as the TileCollider copy it replaces.
        private static void StampDecal(
            in TileData tile,
            RoomInstance room,
            TileDecalLayer decals,
            in TileHitDecision decision,
            Vector2 impactPointWorldUnits)
        {
            if (decals == null) return;

            Rect solid = tile.GetBounds();
            float halfW = solid.width  * 0.5f * TileQueryConstants.PixelToUnit;
            float halfH = solid.height * 0.5f * TileQueryConstants.PixelToUnit;

            Vector2 centre = TileCentreWorldUnits(tile, room);
            float depth = Random.value * TileDecalRules.MaxImpactDepthPx * TileQueryConstants.PixelToUnit;

            TileDecalRules.TilePointPx placed = TileDecalRules.PlaceInsideTile(
                new TileDecalRules.TilePointPx(impactPointWorldUnits.x, impactPointWorldUnits.y),
                new TileDecalRules.TilePointPx(centre.x - halfW, centre.y - halfH),
                new TileDecalRules.TilePointPx(centre.x + halfW, centre.y + halfH),
                depth);

            // The AS3 material argument is the raw `mat` index, not the port's MaterialType — see
            // TileDecalRules.Resolve. `TileData.material` stores the raw index (TileDecoder casts it).
            int material = (int)tile.material;

            TileDecalResult decal = TileDecalRules.Resolve(
                decision.DecalType,
                material,
                decision.DecalBlocked,
                decision.DecalRatio,
                Random.value,   // AS3 `_loc11_` — the scale roll
                Random.value,   // AS3 `_loc12_` — the rotation roll
                Random.value);  // AS3 the single gate roll

            decals.Stamp(decal, new Vector3(placed.X, placed.Y, 0f), Random.value);
        }

        // Which of this cell's four orthogonal neighbours still hold a solid tile — AS3's "does the
        // wall have alpha there" test, asked of the tile grid. Grid y + 1 is north; a destroyed
        // neighbour answers false through `Destroy()` having set physicsType to Air, and a cell
        // outside the room answers through GetTileAtCoord returning null.
        private static TileNeighbourSides SolidNeighbours(in TileData tile, RoomInstance room)
        {
            if (room == null) return TileNeighbourSides.None;

            Vector2Int cell = tile.gridPosition;
            TileNeighbourSides sides = TileNeighbourSides.None;

            if (IsSolid(room, cell.x, cell.y + 1)) sides |= TileNeighbourSides.North;
            if (IsSolid(room, cell.x, cell.y - 1)) sides |= TileNeighbourSides.South;
            if (IsSolid(room, cell.x + 1, cell.y)) sides |= TileNeighbourSides.East;
            if (IsSolid(room, cell.x - 1, cell.y)) sides |= TileNeighbourSides.West;

            return sides;
        }

        private static bool IsSolid(RoomInstance room, int x, int y)
        {
            TileData tile = room.GetTileAtCoord(new Vector2Int(x, y));
            return tile != null && tile.IsSolid();
        }

        // Says why a hit did nothing, when the owner has asked for that. The wording comes from
        // TileDestruction.Describe rather than a second copy of the reasons, so the log cannot
        // disagree with the rule that produced it.
        private static void LogRefusal(
            in TileData tile, PfeDebugSettings debugSettings, TileDamageOutcome outcome,
            float destroyAmount)
        {
            if (debugSettings == null || !debugSettings.LogTileDamageRefusals) return;

            Vector2Int coord = tile.gridPosition;
            Debug.Log($"[TileDamage] tile ({coord.x}, {coord.y}) REFUSED {destroyAmount:0} damage: " +
                      $"{TileDestruction.Describe(outcome, tile, destroyAmount)}. " +
                      "AS3 Tile.udar drops anything under `thre` outright — no chip damage.");
        }

        // The world position the per-tile GameObject used to supply: the centre of the tile's solid
        // rect, room origin added. For a full-height tile that is AS3's cell centre exactly; for a
        // raised half or quarter tile it is the centre of the solid part, which is where a mark
        // belongs anyway.
        private static Vector2 TileCentreWorldUnits(in TileData tile, RoomInstance room)
        {
            Rect solid = tile.GetBounds();

            if (room == null)
            {
                return solid.center * TileQueryConstants.PixelToUnit;
            }

            float originX = WorldCoordinates.RoomOriginPixelX(room.landPosition.x, room.borderOffset);
            float originY = WorldCoordinates.RoomOriginPixelY(room.landPosition.y, room.borderOffset);

            return new Vector2(
                (solid.center.x + originX) * TileQueryConstants.PixelToUnit,
                (solid.center.y + originY) * TileQueryConstants.PixelToUnit);
        }
    }
}
