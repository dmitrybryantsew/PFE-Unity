using PFE.Data.Definitions;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// <see cref="ICollisionTileGrid"/> over a live <see cref="RoomInstance"/> — the adapter half of
    /// <see cref="UnitCollisionAllMath"/>, kept in its own file so the rule itself stays free of
    /// <c>UnityEngine</c> and <c>RoomInstance</c> and can be compiled by an offline harness.
    ///
    /// <para><b>The two coordinate flips are the whole class.</b> The rule asks for an <b>AS3</b> row
    /// (0 = ceiling, Y down); <c>RoomInstance.tiles</c> is indexed by the port's row (0 = floor), and
    /// <see cref="TileData.GetBounds"/> answers in room-local <b>y-up</b> pixels. Both are undone here,
    /// once each, through <see cref="WorldCoordinates.As3RowToUnityRow"/> and
    /// <c>roomHeightPixels - y</c>.</para>
    ///
    /// <para><b>Why <see cref="TileData.GetBounds"/> rather than <c>gridPosition * 40</c>.</b> The
    /// physics box is the tile's, not the cell's: <c>heightLevel</c> lowers its top
    /// (<c>TileData.cs:173-204</c>). Re-deriving the box here would be a second copy of that rule, and
    /// the copy is what got it wrong last time.</para>
    /// </summary>
    public sealed class RoomCollisionTileGrid : ICollisionTileGrid
    {
        private readonly RoomInstance _room;
        private readonly float _roomHeightPixels;

        public RoomCollisionTileGrid(RoomInstance room)
        {
            _room = room;
            _roomHeightPixels = room != null ? room.height * WorldConstants.TILE_SIZE : 0f;
        }

        /// <summary>The room this grid reads. Exposed so a caller can rebuild only on a room change.</summary>
        public RoomInstance Room => _room;

        /// <summary><c>loc.sky</c> — <c>&lt;options sky/&gt;</c>, carried on the room's environment.</summary>
        public bool OpenSky => _room != null && _room.environment != null && _room.environment.hasSky;

        public int Columns => _room != null ? _room.width : 0;

        public int Rows => _room != null ? _room.height : 0;

        public float TileSize => WorldConstants.TILE_SIZE;

        public bool TryGetTile(int column, int as3Row, out CollisionTile tile)
        {
            tile = default;
            if (_room == null) return false;

            int unityRow = WorldCoordinates.As3RowToUnityRow(as3Row, _room.height);
            TileData data = _room.GetTileAtCoord(new Vector2Int(column, unityRow));
            if (data == null) return false;

            // Room-local, y-up → AS3 room-local, y-down. `GetBounds` never includes the room origin,
            // so neither does this; the rule's body is built from As3Position, which does the same.
            Rect bounds = data.GetBounds();

            tile = CollisionTile.FromPortBounds(
                x1: bounds.xMin,
                x2: bounds.xMax,
                yMinUp: bounds.yMin,
                yMaxUp: bounds.yMax,
                roomHeightPixels: _roomHeightPixels,
                solid: data.physicsType == TilePhysicsType.Wall,
                // `phis == 3` does not survive the importer (TileDecoder.MapPhysicsType collapses 1, 2
                // and 3 into Wall) and nothing in the port creates a ghost wall at runtime yet, so this
                // is always false. The parameter exists because the oracle's arm is part of the rule.
                ghostWall: false,
                // `TilePhysicsType.Platform` is this port's `shelf` — see SurfaceKindRule and the
                // shelf-over-air arm in TileDecoder.ApplyForm.
                shelf: data.physicsType == TilePhysicsType.Platform);
            return true;
        }
    }
}
