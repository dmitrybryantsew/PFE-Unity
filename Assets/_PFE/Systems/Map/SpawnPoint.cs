using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Spawn point for player or enemies in a room.
    /// </summary>
    [System.Serializable]
    public class SpawnPoint
    {
        public Vector2Int tileCoord;
        public SpawnType type;
        public string unitId = "";  // For enemy spawns
        public float facingDirection = 1f;  // 1 = right, -1 = left

        /// <summary>
        /// Get spawn position in world space (pixels)
        /// </summary>
        public Vector2 GetWorldPosition()
        {
            return WorldCoordinates.TileToPixel(tileCoord);
        }

        /// <summary>
        /// Get spawn position in Unity world space
        /// </summary>
        public Vector3 GetUnityPosition()
        {
            Vector2 pixelPos = GetWorldPosition();
            return WorldCoordinates.PixelToUnity(pixelPos);
        }
    }

    /// <summary>
    /// Type of spawn point
    /// </summary>
    public enum SpawnType
    {
        Player,
        Enemy,
        Boss,
        NPC
    }
}
