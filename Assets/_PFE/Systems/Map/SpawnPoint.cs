using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Spawn point for player or enemies in a room.
    /// </summary>
    [System.Serializable]
    public class SpawnPoint
    {
        /// <summary>
        /// AS3 tile coordinate of the spawn marker: <c>x</c> a column, <c>y</c> an <b>AS3 row</b>
        /// (0 = ceiling), exactly as the importer copied it out of <c>&lt;obj id="player" x= y=&gt;</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>This is not a port tile coordinate and must not be read as one.</b> The port's tile
        /// grid counts rows UP from the floor, so this field has to be mirrored before it can name a
        /// pixel — see <see cref="GetRoomLocalPixel"/>. Reading it raw put the player at the room's
        /// vertical mirror image, which is how an authored floor spawn became "the ceiling above an
        /// indestructible tile".</para>
        ///
        /// <para><b>The convention is shared, not local to this class.</b> <c>ObjectSpawnData.tileCoord</c>
        /// holds an AS3 row for the same reason, and <c>RoomPopulator</c> mirrors that one at its pixel
        /// seam (<c>RoomPopulator.cs:151-152</c>). Both importers store the row verbatim —
        /// <c>AS3ToUnityConverter.ParseObjects</c> and <c>:FindSpawnPoints</c> — and both are faithful
        /// to <c>Location.as:664-676</c>, which stores <c>int(obj.@y)</c> with no Y flip and consumes it
        /// in AS3 space.</para>
        /// </remarks>
        public Vector2Int tileCoord;
        public SpawnType type;
        public string unitId = "";  // For enemy spawns
        public float facingDirection = 1f;  // 1 = right, -1 = left

        /// <summary>
        /// AS3 <c>@size</c> of the <c>player</c> definition (<c>AllData.as:4925</c> is
        /// <c>size='2' wid='2'</c>), which is what makes the anchor's X land on the boundary one tile
        /// right of the marker column rather than on the column itself.
        /// </summary>
        /// <remarks>
        /// Not a guess and not a fudge: AS3 spawns the player through the <c>size='2'</c> form of
        /// <c>createObj</c>'s anchor (<c>Land.as:1229</c> uses <c>(x + 1) * tileX</c>, and
        /// <c>createObj</c> uses <c>(nx + 0.5 * size) * tileX</c> — the same value when
        /// <c>size == 2</c>).
        /// </remarks>
        public const float AnchorSizeTiles = 2f;

        /// <summary>
        /// The spawn anchor in the port's Y-up, <b>room-local</b> pixel space.
        /// </summary>
        /// <param name="room">
        /// The room the point belongs to — needed because the mirror is about the room's height and
        /// because the anchor is inset by the room's border. A null room falls back to the standard
        /// room height and no border, which is what a point with no room to belong to can mean.
        /// </param>
        public Vector2 GetRoomLocalPixel(RoomInstance room)
        {
            int roomHeight = room != null ? room.height : WorldConstants.ROOM_HEIGHT;
            int borderOffset = room != null ? Mathf.Max(0, room.borderOffset) : 0;

            return WorldCoordinates.As3BottomCentreAnchorPixels(
                tileCoord, AnchorSizeTiles, roomHeight, borderOffset);
        }

        /// <summary>
        /// The spawn anchor in <b>world</b> pixels — the room-local anchor plus the room's origin.
        /// </summary>
        /// <remarks>
        /// The origin is not optional: <c>TilePhysicsController</c> collides in world pixels and
        /// subtracts the origin on every test, so a room-local value is a world value only in a room at
        /// land (0,0).
        /// </remarks>
        public Vector2 GetWorldPixel(RoomInstance room)
        {
            return GetRoomLocalPixel(room) + WorldCoordinates.RoomOriginPixel(room);
        }

        /// <summary>
        /// The spawn anchor as a Unity <b>world</b> position, using the <b>player</b> anchor
        /// (<see cref="AnchorSizeTiles"/> = 2).
        /// </summary>
        /// <remarks>
        /// <b>Not for an <see cref="SpawnType.Enemy"/> point.</b> An <c>enspawn</c> marker authors no
        /// <c>size</c>, so <c>Location.addEnSpawn</c> (<c>:914-930</c>) anchors it at
        /// <c>(x + 0.5) * tileX</c> — half a tile left of this one. Use
        /// <see cref="GetEnemyUnityPosition"/> for those. The two methods exist separately precisely
        /// because "the spawn point's position" sounds like one idea and is two formulas.
        /// </remarks>
        public Vector3 GetUnityPosition(RoomInstance room)
        {
            return WorldCoordinates.PixelToUnity(GetWorldPixel(room));
        }

        /// <summary>
        /// AS3 <c>Location.addEnSpawn</c>'s anchor size (<c>:914-930</c>): <c>int(placement.@size)</c>,
        /// floored at <b>1</b> — <i>not</i> <see cref="AnchorSizeTiles"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is a separate constant and not the player's 2.</b> The two anchors are
        /// different formulas over the same tile coordinate:
        /// <c>addEnSpawn</c> writes <c>(x + 0.5*size) * tileX</c>, while the player anchor is
        /// <c>Land.as:1229</c>'s <c>(x + 1) * tileX</c> — the same value only because the
        /// <c>player</c> row is <c>size='2'</c>. An <c>enspawn</c> marker authors no <c>size</c>
        /// (measured: 0 of the 89 shipped markers have a non-empty attribute list), so <c>size</c> is
        /// 1 and reusing the player's 2 puts every reinforcement <b>half a tile (20 px) right</b> of
        /// where the oracle spawns it.</para>
        /// </remarks>
        public const float EnemyAnchorSizeTiles = 1f;

        /// <summary>
        /// The <b>enemy</b> spawn anchor in room-local pixels — <c>Location.addEnSpawn</c>
        /// (<c>:914-930</c>): <c>x = (nx + 0.5*size) * tileX</c>,
        /// <c>y = (ny + 1) * tileY - 1</c>, mirrored into the port's Y-up space.
        /// </summary>
        public Vector2 GetEnemyRoomLocalPixel(RoomInstance room)
        {
            int roomHeight = room != null ? room.height : WorldConstants.ROOM_HEIGHT;
            int borderOffset = room != null ? Mathf.Max(0, room.borderOffset) : 0;

            return WorldCoordinates.As3BottomCentreAnchorPixels(
                tileCoord, EnemyAnchorSizeTiles, roomHeight, borderOffset);
        }

        /// <summary>The enemy spawn anchor as a Unity <b>world</b> position.</summary>
        public Vector3 GetEnemyUnityPosition(RoomInstance room)
        {
            return WorldCoordinates.PixelToUnity(
                GetEnemyRoomLocalPixel(room) + WorldCoordinates.RoomOriginPixel(room));
        }
    }
}
