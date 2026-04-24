using System;
using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Tile data structure matching AS3 Tile class.
    /// From Tile.as - fundamental building block of the map system.
    /// </summary>
    [Serializable]
    public class TileData
    {
        // Grid coordinates
        public Vector2Int gridPosition;

        // Physics - from AS3: phis property
        public TilePhysicsType physicsType = TilePhysicsType.Air;

        // Destruction - from AS3: hp, indestruct
        public bool indestructible = false;
        public int hitPoints = 1000;
        public int damageThreshold = 0;

        // Visual - from AS3: vid, vid2, front, back
        [SerializeField] private string frontGraphic = "";
        [SerializeField] private string backGraphic = "";
        // Zad graphic (intermediate storage during decode, AS3: Tile.zad)
        [SerializeField] private string zadGraphic = "";
        public int visualId = 0;
        public int visualId2 = 0;
        // Rear layer flags (from AS3 Tile: fRear, vRear, v2Rear)
        public bool frontRear = false;
        public bool vidRear = false;
        public bool vid2Rear = false;
        public float opacity = 1f;

        // Special properties - from AS3: zForm, stair, water
        public int heightLevel = 0;  // 0-3 (zForm in AS3)
        public int slopeType = 0;    // -1, 0, 1 (diagonal in AS3)
        public int stairType = 0;
        public bool isLedge = false;
        public bool hasWater = false;

        // Lurk value (stealth hiding spots, from AS3)
        public int lurk = 0;

        // Kontur (edge) values for wall tiles — calculated by KonturCalculator
        // 0=full, 1=inner_corner, 2=horiz_edge, 3=vert_edge, 4=outer_corner
        public int kontur1 = 0;  // top-left corner
        public int kontur2 = 0;  // top-right corner
        public int kontur3 = 0;  // bottom-left corner
        public int kontur4 = 0;  // bottom-right corner

        // Pontur (edge) values for background tiles
        public int pontur1 = 0;
        public int pontur2 = 0;
        public int pontur3 = 0;
        public int pontur4 = 0;

        // Material
        public MaterialType material = MaterialType.Default;

        // State
        public bool canPlaceObjects = true;

        // Links (serialized as IDs)
        public string doorId = "";
        public string trapId = "";

        /// <summary>
        /// Get the physical bounds of this tile in pixel space.
        /// From AS3: phX1, phX2, phY1, phY2
        /// </summary>
        public Rect GetBounds()
        {
            float y1 = (gridPosition.y + heightLevel * 0.25f) * WorldConstants.TILE_SIZE;
            float y2 = y1 + WorldConstants.TILE_SIZE;
            float x1 = gridPosition.x * WorldConstants.TILE_SIZE;
            float x2 = x1 + WorldConstants.TILE_SIZE;
            return new Rect(x1, y1, x2 - x1, y2 - y1);
        }
        public string GetZadGraphic() { return zadGraphic; }
        public void SetZadGraphic(string graphic) { zadGraphic = graphic; }
        /// <summary>
        /// Get the ground height at a specific X position (for slopes).
        /// Handles diagonal tiles (slopes/ramps).
        /// Returns the Y coordinate where the ground surface is.
        /// For flat tiles, returns yMin (bottom of tile in Unity coords).
        /// </summary>
        public float GetGroundHeight(float x)
        {
            Rect bounds = GetBounds();

            if (slopeType == 0)
            {
                // Flat
                return bounds.yMin;
            }

            if (slopeType > 0)
            {
                // Slope: / (low on left, high on right)
                // At left edge (xMin), height is yMax (low)
                // At right edge (xMax), height is yMin (high)
                if (x < bounds.xMin) return bounds.yMax;
                if (x > bounds.xMax) return bounds.yMin;
                float t = (x - bounds.xMin) / (bounds.xMax - bounds.xMin);
                return bounds.yMax - (bounds.yMax - bounds.yMin) * t;
            }
            else
            {
                // Slope: \ (high on left, low on right)
                // At left edge (xMin), height is yMin (high)
                // At right edge (xMax), height is yMax (low)
                if (x < bounds.xMin) return bounds.yMin;
                if (x > bounds.xMax) return bounds.yMax;
                float t = (bounds.xMax - x) / (bounds.xMax - bounds.xMin);
                return bounds.yMax - (bounds.yMax - bounds.yMin) * t;
            }
        }

        /// <summary>
        /// Check if this tile is solid (blocks movement).
        /// Wall and Platform block from above.
        /// </summary>
        public bool IsSolid()
        {
            return physicsType >= TilePhysicsType.Wall;
        }

        /// <summary>
        /// Check if this tile is a platform (one-way collision).
        /// </summary>
        public bool IsPlatform()
        {
            return physicsType == TilePhysicsType.Platform;
        }

        /// <summary>
        /// Check if this tile is a stair/slope.
        /// </summary>
        public bool IsStair()
        {
            return physicsType == TilePhysicsType.Stair || slopeType != 0;
        }

        /// <summary>
        /// Check if this tile should behave like a walkable slope surface.
        /// </summary>
        public bool IsSlopeSurface()
        {
            return slopeType != 0;
        }

        /// <summary>
        /// Check if this tile carries climbable ladder metadata rather than a ground slope.
        /// </summary>
        public bool IsClimbableLadder()
        {
            return stairType != 0 && slopeType == 0;
        }

        /// <summary>
        /// Apply damage to this tile.
        /// Returns true if damage was applied.
        /// </summary>
        public bool TakeDamage(int damage)
        {
            // Indestructible tiles cannot be damaged
            if (indestructible)
            {
                return false;
            }

            // Damage below threshold doesn't affect tile
            if (damageThreshold > 0 && damage < damageThreshold)
            {
                return false;
            }

            hitPoints -= damage;
            return true;
        }

        /// <summary>
        /// Check if this tile is destroyed (hp <= 0).
        /// </summary>
        public bool IsDestroyed()
        {
            return !indestructible && hitPoints <= 0;
        }

        /// <summary>
        /// Destroy this tile (make it air).
        /// </summary>
        public void Destroy()
        {
            physicsType = TilePhysicsType.Air;
            opacity = 0f;
            visualId = visualId2 = 0;
            hitPoints = 0;
        }

        /// <summary>
        /// Reset tile to initial state.
        /// </summary>
        public void Reset()
        {
            if (!indestructible)
            {
                hitPoints = 1000;
            }
        }

        /// <summary>
        /// Get the front graphic name.
        /// </summary>
        public string GetFrontGraphic()
        {
            return frontGraphic;
        }

        /// <summary>
        /// Set the front graphic name.
        /// </summary>
        public void SetFrontGraphic(string graphic)
        {
            frontGraphic = graphic;
        }

        /// <summary>
        /// Get the back graphic name.
        /// </summary>
        public string GetBackGraphic()
        {
            return backGraphic;
        }

        /// <summary>
        /// Set the back graphic name.
        /// </summary>
        public void SetBackGraphic(string graphic)
        {
            backGraphic = graphic;
        }
    }
}
