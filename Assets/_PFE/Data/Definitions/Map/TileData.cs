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

        /// <summary>
        /// AS3 <c>phis == 2</c> — a <b>level-2 solid</b>, which is what a closed <c>grate</c> /
        /// <c>hgrate</c> door stamps onto its tiles (<c>AllData.as:4859-4860</c>, written by
        /// <c>Box.initDoor</c>, <c>loc/Box.as:666</c>).
        ///
        /// <para><b>Why the port needs a second bit at all.</b> <see cref="physicsType"/> collapsed
        /// every non-zero <c>phis</c> into <see cref="TilePhysicsType.Wall"/> (see
        /// <c>TileDecoder.MapPhysicsType</c>), and for <i>most</i> consumers 1 and 2 are indeed the
        /// same: units are gated on <c>phis &gt; 0</c>, dynamic props on <c>phis != 0 &amp;&amp; phis
        /// != 3</c> (<c>Box.as:1262</c>), tile damage on <c>phis &gt;= 1</c>. Two consumers test
        /// <c>phis == 1</c> <b>exactly</b> and therefore disagree:</para>
        /// <list type="bullet">
        /// <item><b>projectiles</b> — <c>weapon/Bullet.as:476</c> and all four arms of
        /// <c>weapon/PhisBullet.as:242,263,286,315</c>. A bullet flies through a grate.</item>
        /// <item><b>line of sight</b> — <c>unit/Unit.as:4626</c> (and <c>Location.isLine</c>,
        /// <c>Location.as:2378</c>). An enemy sees through a grate.</item>
        /// </list>
        ///
        /// <para><b>This is a flag, not a third physics type.</b> The tile stays a
        /// <see cref="TilePhysicsType.Wall"/>: it blocks movement, props, light and damage exactly as
        /// before, and every one of those readers keeps its single predicate. Only the consumers that
        /// ask "is this <i>exactly</i> a <c>phis == 1</c> wall" read this bit — the same split
        /// <see cref="SurfaceKind"/> exists to express.</para>
        ///
        /// <para><b>Writers:</b> <c>DoorPropPresenter.ApplyTileCollision</c> (the port's
        /// <c>Box.setDoor</c>, and the only writer in practice — the two grate rows are the only
        /// <c>phis = 2</c> in the shipped data) and <c>TileDecoder.ApplyForm</c> (a
        /// <c>phis = 2</c> tile form, of which the data has none — kept so a future form decodes
        /// honestly instead of silently becoming an ordinary wall).</para>
        /// </summary>
        public bool levelTwoSolid;

        /// <summary>
        /// <c>Tile.grav</c>'s default (<c>Tile.as:72</c>, <c>grav = 1</c>) — "ordinary gravity".
        ///
        /// <para><b>Why a named constant and not a literal <c>1f</c> at each site.</b> The value is read
        /// in three places that must agree: this field's initialiser, <see cref="PFE.Systems.Map.AreaLift"/>
        /// (which writes it back when an area is switched off), and the two physics drivers
        /// (<c>TilePhysicsController.ResolveTileGravity</c>, <c>UnitController.ResolveTileGravity</c>),
        /// which both answer <b>this</b> for a missing tile — never <c>0</c>, because 0 means
        /// "weightless" and a not-yet-filled tile would silently switch gravity off for whatever stands
        /// there.</para>
        /// </summary>
        public const float DefaultGravity = 1f;

        /// <summary>
        /// AS3 <c>Tile.grav</c> (<c>Tile.as:72</c>, default 1) — the per-tile gravity multiplier.
        ///
        /// <para><b>Written by <c>Area.setLift</c> and read by every unit that stands in the area.</b>
        /// <c>Area.as:386-400</c> stamps the area's <c>@grav</c> onto its tiles while the area is
        /// enabled and 1 back when it is not; <c>UnitPlayer.as:766-788</c> then reads
        /// <c>getAbsTile(X, Y - scY/4).grav</c> — the tile a quarter of the body height above the feet
        /// — and uses it both as the gravity multiplier and as the terminal-velocity factor. A
        /// <b>negative</b> value accelerates the unit <i>upward</i>, which is what a lift shaft is;
        /// <c>0</c> removes gravity outright.</para>
        ///
        /// <para><b>It is deliberately not part of the tile's visual/collision state.</b> Nothing in
        /// the renderer, the fog pass or <c>TileCollisionMath</c> reads it, and
        /// <c>RoomInstance.NotifyTilesMutated</c> is therefore not called when it changes: writing it
        /// must not invalidate the collision chain or force a relight. See
        /// <see cref="PFE.Systems.Map.AreaLift"/> for the writer.</para>
        /// </summary>
        public float gravityMultiplier = DefaultGravity;

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
        /// How much of a tile each <see cref="heightLevel"/> removes from the top: a quarter. AS3
        /// <c>Tile.setZForm</c> divides <c>zForm</c> by 4 (<c>loc/Tile.as:292</c>).
        /// </summary>
        public const float HeightStepFraction = 0.25f;

        /// <summary>Fraction of a tile this tile's <b>solid</b> part occupies: <c>1 - heightLevel/4</c>.</summary>
        public float HeightScale =>
            1f - Mathf.Clamp(heightLevel, 0, 3) * HeightStepFraction;

        /// <summary>How far this tile's solid top sits below the cell's top edge, in pixels.</summary>
        public float SolidTopDropPixels =>
            WorldConstants.TILE_SIZE * Mathf.Clamp(heightLevel, 0, 3) * HeightStepFraction;

        /// <summary>
        /// Get the physical bounds of this tile in pixel space.
        /// From AS3: phX1, phX2, phY1, phY2
        ///
        /// <para><b><c>heightLevel</c> shortens the tile from the TOP; it does not move the whole
        /// cell.</b> AS3 <c>Tile.setZForm</c> writes <b>only</b> <c>phY1 = (Y + zForm/4) * tileY</c>
        /// and leaves <c>phY2 = (Y + 1) * tileY</c> alone (<c>loc/Tile.as:281-297</c>). In AS3's
        /// y-down frame <c>phY1</c> is the top edge, so a raised tile's solid part keeps its foot on
        /// the cell floor and loses a quarter of the cell at the top — which is exactly what a step
        /// is, and why <c>Grafon.as:848</c> draws the mask squashed to
        /// <c>(phY2 - phY1) / tileY</c> and centred on <c>(phY2 + phY1) / 2</c>.</para>
        ///
        /// <para><b>Mirrored into this port's y-up frame, the top comes down and the bottom stays
        /// put:</b> <c>[gy·40, (gy+1)·40 - heightLevel·10]</c>. This used to add
        /// <c>heightLevel * 0.25</c> to the <i>whole</i> cell, which lifted the box a quarter cell
        /// (and, with <c>TileCollider</c>'s own upward offset, a full half cell) above the ledge it
        /// was supposed to be. 65 cells in 10 shipped rooms carry <c>zForm</c>.</para>
        ///
        /// <para><b>Every physics reader must use this, not the grid row.</b> <c>phis</c> decides
        /// <i>whether</i> a cell is solid; <c>phX1..phY2</c> decide <i>where</i>. AS3's
        /// <c>Unit.collisionTile</c> is a plain AABB test against these four numbers
        /// (<c>Unit.as:2568-2583</c>), and its landing snap writes <c>Y = tile.phY1</c>
        /// (<c>:2275, :2307</c>) — never the cell boundary.</para>
        /// </summary>
        public Rect GetBounds()
        {
            float x1 = gridPosition.x * WorldConstants.TILE_SIZE;
            float x2 = x1 + WorldConstants.TILE_SIZE;
            float y1 = gridPosition.y * WorldConstants.TILE_SIZE;
            float y2 = y1 + WorldConstants.TILE_SIZE * HeightScale;
            return new Rect(x1, y1, x2 - x1, y2 - y1);
        }

        /// <summary>
        /// AS3 <c>phY1</c> — the surface a unit lands on and the ceiling of the cell below.
        /// For an unraised tile this is the cell's top edge; for a raised one it is
        /// <see cref="SolidTopDropPixels"/> lower.
        /// </summary>
        public float SolidTopY => GetBounds().yMax;

        /// <summary>
        /// AS3 <c>phY2</c> — the underside of the tile. <c>setZForm</c> never writes it, so this is
        /// always the cell's bottom edge.
        /// </summary>
        public float SolidBottomY => gridPosition.y * WorldConstants.TILE_SIZE;
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

            // AS3 `Tile.die()` sets `phis = 0`, and `phis` is the only thing that made the tile a
            // level-2 solid — a destroyed grate is no longer one, so the bit must not survive it.
            levelTwoSolid = false;
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
