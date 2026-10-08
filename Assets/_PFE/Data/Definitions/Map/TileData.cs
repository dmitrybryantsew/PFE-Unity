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

        /// <summary>
        /// "No door has written this tile" — the sentinel for <see cref="doorOcclusion"/>.
        ///
        /// <para>Deliberately <b>not</b> zero. AS3's door writes <c>opac = 0</c> when it opens
        /// (<c>Box.as:685</c>), so zero is a *meaningful* door value; using it as "absent" would make
        /// an open door and an undecided tile indistinguishable, and the occlusion rule would have to
        /// guess. Absence and zero are different answers here.</para>
        /// </summary>
        public const float NoDoorOcclusion = -1f;

        /// <summary>
        /// AS3 <c>Tile.opac</c> as written by a door object — the door's <c>@opac</c>, or
        /// <see cref="NoDoorOcclusion"/> when no door covers this tile.
        ///
        /// <para><b>Why this is not <see cref="opacity"/>.</b> Both mirror AS3's single <c>Tile.opac</c>
        /// field, but <c>opacity</c> is the <i>render</i> alpha (<c>TileRenderer</c>) and defaults to 1
        /// even on air, so the fog pass cannot read it — see <c>FogOcclusionMath</c>. A door's value is
        /// fractional (the data authors 0.1–0.8), so it needs a field whose default is "absent".</para>
        ///
        /// <para>Written only by <c>DoorPropPresenter.ApplyTileCollision</c>, which is the port's
        /// <c>Box.setDoor()</c>. AS3 <i>assigns</i> rather than maxes, so a door value replaces the
        /// <c>phis</c>-derived one instead of combining with it.</para>
        /// </summary>
        public float doorOcclusion = NoDoorOcclusion;

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
                // At left edge (xMin), height is yMin (bottom of tile)
                // At right edge (xMax), height is yMax (top of tile)
                if (x <= bounds.xMin) return bounds.yMin;
                if (x >= bounds.xMax) return bounds.yMax;
                float t = (x - bounds.xMin) / (bounds.xMax - bounds.xMin);
                return bounds.yMin + (bounds.yMax - bounds.yMin) * t;
            }
            else
            {
                // Slope: \ (high on left, low on right)
                // At left edge (xMin), height is yMax (top of tile)
                // At right edge (xMax), height is yMin (bottom of tile)
                if (x <= bounds.xMin) return bounds.yMax;
                if (x >= bounds.xMax) return bounds.yMin;
                float t = (bounds.xMax - x) / (bounds.xMax - bounds.xMin);
                return bounds.yMin + (bounds.yMax - bounds.yMin) * t;
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
        /// Whether this tile is a <b>solid column or a shelf</b> — AS3's <c>phis == 1 || shelf</c>, the
        /// two things a unit can hop <b>onto</b>.
        ///
        /// <para><b>Where this comes from.</b> <c>UnitZombie.as:794</c>/<c>:813</c> probe the tile 80 px
        /// ahead at floor level and hop if <c>_loc1_.phis == 1 || _loc1_.shelf</c>; anything else means
        /// "nothing there" and the unit turns around instead. It is the zombie's crate-hop test, and the
        /// idiom is the family's — a raider and a hellhound want the same predicate.</para>
        ///
        /// <para><b>Why the mapping is <c>Wall || Platform</c> and why it is not
        /// <see cref="IsSolid"/>.</b> <c>TileDecoder.MapPhysicsType</c> maps <b>every</b> non-zero
        /// <c>phis</c> to <see cref="TilePhysicsType.Wall"/>, and <see cref="TilePhysicsType.Platform"/>
        /// is written <i>only</i> for a <c>shelf</c> form on a <c>phis == 0</c> base — so the oracle's two
        /// terms are exactly these two members. <see cref="IsSolid"/> is <c>&gt;= Wall</c>, which also
        /// admits <see cref="TilePhysicsType.Stair"/>: a ladder is <c>phis == 0</c> in the oracle and
        /// would not count, so using <c>IsSolid</c> here would make a unit hop at a ladder. The same
        /// distinction is spelled out on <c>RoomInstance.HasWallUnderFeet</c>.</para>
        ///
        /// <para><b>A known widening, recorded rather than hidden.</b> AS3 tests <c>phis == 1</c>
        /// <i>exactly</i>; this port cannot, because the decoder collapsed <c>phis</c> 2 (grate doors) and
        /// 3 (runtime ghost walls) into <see cref="TilePhysicsType.Wall"/>. A grate-door tile ahead
        /// therefore reads as hoppable here and not in the oracle — in the benign direction, since the
        /// unit hops onto a solid grate instead of turning. See
        /// <c>docs/OnEnemiesAndAi/26_Zombie_Ledge_Overhang_2026-10-08.md</c> §4.</para>
        /// </summary>
        public bool IsSolidOrShelf()
        {
            return physicsType == TilePhysicsType.Wall || physicsType == TilePhysicsType.Platform;
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
        /// Whether this tile can be destroyed by damage at all.
        ///
        /// <para><b>The gate is <c>phis</c>, not <c>hp</c>.</b> AS3 puts it at the caller rather than
        /// in <c>Tile.udar()</c>: every branch of <c>Location.hitTile()</c> is guarded by
        /// <c>if(param1.phis &gt;= 1)</c> (<c>Location.as:2525, 2538, 2557, 2565</c>) and
        /// <c>Location.dieTile()</c> repeats it (<c>Location.as:2593</c>), while <c>Tile.udar()</c>
        /// itself only tests <c>indestruct</c> and <c>thre</c> (<c>Tile.as:340-348</c>). That is why
        /// this is a separate predicate and <see cref="TakeDamage"/> stays ungated.</para>
        ///
        /// <para><b>Why it matters.</b> A ladder (<c>stair</c>), a catwalk (<c>shelf</c>) and a ramp
        /// (<c>diagon</c>) all carry <c>phis == 0</c> (<c>Tile.as:182-197</c>), so the oracle never
        /// destroys them however much <c>hp</c> their form declares — they are scenery, not
        /// destructibles. The port decodes those to <see cref="TilePhysicsType.Stair"/> and
        /// <see cref="TilePhysicsType.Platform"/>, and because the damage entry points had no gate
        /// they were fully destructible: 24 621 such tiles across the 13 shipped rooms.</para>
        ///
        /// <para><b>Scope.</b> <c>physicsType == Wall</c> is the port's encoding of "AS3 <c>phis</c>
        /// is non-zero". Strictly, AS3 would answer <c>false</c> for a <c>phis == 3</c> ghost wall
        /// (<c>Box.as:1262</c> treats 3 as pass-through) and <c>true</c> for <c>phis == 2</c>. Neither
        /// reaches a tile in the shipped data — <c>phis == 2</c> appears only on the two grate
        /// <i>door objects</i> (<c>AllData.as:4859-4860</c>) and <c>phis == 3</c> is only ever set at
        /// runtime (<c>Spell.as:446</c>) — so the conflation is unobservable here.</para>
        /// </summary>
        public bool IsDamageable()
        {
            return physicsType == TilePhysicsType.Wall;
        }

        /// <summary>
        /// Apply damage to this tile.
        /// Returns true if damage was applied.
        ///
        /// <para>Ungated on purpose: this is AS3 <c>Tile.udar()</c> (<c>Tile.as:340-348</c>), which
        /// checks only <c>indestruct</c> and <c>thre</c>. The <c>phis &gt;= 1</c> gate lives at the
        /// callers — see <see cref="IsDamageable"/>.</para>
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

            // AS3 Tile.die() clears `opac` along with `phis` (Tile.as:350-364), and a door's die()
            // does the same (Box.as:806-807). Without this the tile keeps the door's fractional value
            // and the occlusion rule — which only honours it while the tile is a Wall — would silently
            // start honouring it again if anything later made the tile solid.
            doorOcclusion = NoDoorOcclusion;
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
