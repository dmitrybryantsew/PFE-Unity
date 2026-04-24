using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Door instance connecting two rooms.
    /// From AS3: door system with 12 doors per side (24 total with mirrored).
    /// </summary>
    [System.Serializable]
    public class DoorInstance
    {
        // Door position in room
        public int doorIndex;  // 0-23, which door slot
        public DoorSide side;
        public Vector2Int tilePosition;

        // Connection
        public Vector3Int targetRoomPosition;  // Where this door leads
        public int targetDoorIndex;  // Which door in target room

        // Door properties
        public DoorQuality quality;
        public bool isLocked;
        public int lockLevel;
        public string keyItemId = "";
        public bool isActive;

        // Visual
        public string doorGraphic = "";

        /// <summary>
        /// Get door position in world space (pixels)
        /// </summary>
        public Vector2 GetWorldPosition(Vector3Int currentRoomPos)
        {
            Vector2 roomPos = new Vector2(
                currentRoomPos.x * WorldConstants.ROOM_SIZE_PIXELS.x,
                currentRoomPos.y * WorldConstants.ROOM_SIZE_PIXELS.y
            );
            Vector2 tilePos = WorldCoordinates.TileToPixel(tilePosition);
            return roomPos + tilePos;
        }
    }

    /// <summary>
    /// Door side in room.
    /// From AS3: 0-5 = right side, 6-11 = bottom, 12-17 = left, 18-23 = top
    /// </summary>
    public enum DoorSide
    {
        Right = 0,
        Bottom = 1,
        Left = 2,
        Top = 3
    }

    /// <summary>
    /// Door quality/width.
    /// From AS3: higher quality = wider door
    /// </summary>
    public enum DoorQuality
    {
        None = 0,       // No door
        Blocked = 1,    // Door blocked
        Narrow = 2,     // 1 tile wide
        Normal = 3,     // 2 tiles wide
        Wide = 4,       // 3 tiles wide
        ExtraWide = 5   // 4+ tiles wide
    }
}
