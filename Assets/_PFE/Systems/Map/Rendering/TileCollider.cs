using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering.RoomTerrain;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Core;
using PFE.Data.Definitions;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The visual half of a tile, as far as destruction is concerned.
    ///
    /// <para><b>Why this exists.</b> <see cref="TileCollider"/> used to carry
    /// <c>[RequireComponent(typeof(TileRenderer))]</c>, which is not a hint — it is the reason a tile
    /// GameObject could not have a collider without a renderer. That attribute was the only
    /// compile-time tie between the room's collision and the room's picture, and it is what forced the
    /// quarter renderer to keep ~1 136 tile GameObjects alive purely to carry colliders (see
    /// <c>RoomTerrainRenderer</c> §10.1 and <c>LLP2D_IMPLEMENTATION_GUIDE.md</c> §8.3 step 1).</para>
    ///
    /// <para>An interface rather than a direct <c>TileRenderer</c> reference so the destruction path
    /// names the capability it needs and not the component that happens to provide it — the same
    /// idiom <c>Projectile</c> and <c>MeleeHitVolume</c> already use when they ask for
    /// <c>IDestructibleTile</c> instead of <c>TileCollider</c>.</para>
    /// </summary>
    public interface ITileVisual
    {
        /// <summary>Stop drawing this tile. The tile data has already been destroyed when this is called.</summary>
        void DestroyTile();
    }

    /// <summary>
    /// Whoever built a tile GameObject, as far as that tile's death is concerned.
    ///
    /// <para><b>Why an owner at all.</b> Two things a <see cref="TileCollider"/> cannot do for itself:
    /// drop the tile from the registry its owner keeps (otherwise <c>RefreshSprites</c>/<c>UpdateVisuals</c>
    /// iterate an entry pointing at a dead GameObject), and reach the <see cref="RoomInstance"/> that
    /// carries <c>TilesMutated</c> — the collider holds no query service, and building one here would
    /// notify a throwaway instance nobody listens to.</para>
    ///
    /// <para><b>Why an interface and not <c>TileVisualManager</c>.</b> A tile no longer has to be
    /// <i>drawn</i> to be <i>collided with</i>. Since the quarter renderer became the default terrain
    /// path, most rooms build <see cref="TileCollisionBuilder"/> — collider-only tiles with no
    /// <see cref="TileRenderer"/> and no compositing — and the shipped manager only runs as the fallback.
    /// Typing this seam as the manager would have made the collision path depend on the picture path
    /// again, one indirection further out.</para>
    /// </summary>
    public interface ITileOwner
    {
        /// <summary>The room whose tiles these are. Never null for a built owner.</summary>
        RoomInstance Room { get; }

        /// <summary>This tile died: forget it and stop drawing it. The <see cref="TileData"/> is already
        /// destroyed when this is called.</summary>
        void DestroyTile(Vector2Int coord);

        /// <summary>
        /// Where this owner's tiles stamp their damage decals, or <c>null</c> when the room has no
        /// picture (a test, the headless harness, an editor preview).
        ///
        /// <para><b>Nullable on purpose, and not a missing-feature apology.</b> AS3 guards every decal
        /// with <c>if(this.active)</c> — a hit in a room nobody is looking at is real and invisible. The
        /// port's equivalent of "nobody is looking" is "no layer to stamp into", and a null here is that
        /// same answer rather than an error.</para>
        /// </summary>
        TileDecalLayer Decals { get; }
    }

    /// <summary>
    /// Adds and manages Unity Collider2D components for tiles.
    ///
    /// <para><b>Deliberately no <c>[RequireComponent]</c>.</b> A tile GameObject may now carry a
    /// collider and no renderer — that is the whole point of the change. Every visual side effect
    /// below is routed through <see cref="ITileVisual"/> and tolerates its absence.</para>
    ///
    /// Also implements <see cref="IDestructibleTile"/> — see P0 Defect 3. This component
    /// already sits on every non-air tile GameObject, which is precisely the object
    /// Projectile.cs resolves via GetComponent&lt;IDestructibleTile&gt;(), so implementing
    /// the interface here wires destruction up with no extra components and no manual
    /// per-room setup.
    /// </summary>
    public class TileCollider : MonoBehaviour, IDestructibleTile
    {
        [Header("Tile Data")]
        [SerializeField] private TileData tileData;
        [SerializeField] private Collider2D tileCollider;
        private PfeDebugSettings debugSettings;

        // The owner that built this tile. Needed so destruction can drop the tile from that owner's
        // registry — `TileVisualManager.DestroyTile` for the shipped path, `TileCollisionBuilder.DestroyTile`
        // for the collision-only path — and so the room's `TilesMutated` is reachable. Not a Unity
        // object, so not serialized.
        private ITileOwner owner;

        // Layer constants
        private const int LAYER_GROUND = 6;
        private const int LAYER_PLATFORM = 7;

        /// <summary>
        /// Initialize the tile collider based on tile data.
        /// </summary>
        public void Initialize(TileData data, PfeDebugSettings debugSettings = null,
                               ITileOwner owner = null)
        {
            tileData = data;
            this.debugSettings = debugSettings;

            // RefreshCollider() re-enters here with no owner; keep the one we already have.
            if (owner != null)
            {
                this.owner = owner;
            }
            
            if (tileData == null)
            {
                Debug.LogWarning("[TileCollider] Cannot initialize with null tile data");
                return;
            }

            // Remove any existing collider
            if (tileCollider != null)
            {
                if (Application.isPlaying)
                    Destroy(tileCollider);
                else
                    DestroyImmediate(tileCollider);
            }

            // Create collider based on physics type
            switch (tileData.physicsType)
            {
                case TilePhysicsType.Wall:
                    CreateSolidCollider();
                    break;

                case TilePhysicsType.Platform:
                    CreatePlatformCollider();
                    break;

                case TilePhysicsType.Stair:
                    CreateStairCollider();
                    break;

                case TilePhysicsType.Air:
                    // No collider for air
                    break;
            }

            // Stamp surface material for impact sound resolution.
            // Air tiles have no collider so the component is harmless but present.
            //
            // The argument is the RAW AS3 `mat` index, not a MaterialType: TileDecoder writes AS3's
            // `mat` into TileData.material with a straight cast, and the two numberings disagree from 2
            // upward. Reading it as a MaterialType is what made a concrete wall clang like timber —
            // see TileMaterialRules.
            var tileSurface = GetComponent<TileSurface>() ?? gameObject.AddComponent<TileSurface>();
            tileSurface.Set(TileSurface.FromAs3Material((int)tileData.material));
        }

        /// <summary>
        /// Create a solid BoxCollider2D for walls.
        ///
        /// <para><b>The box is exactly <see cref="TileData.GetBounds"/>.</b> The owner places the
        /// tile object at that rect's centre (<c>TileCollisionBuilder.LocalPositionFor</c> /
        /// <c>TileRenderer.GetLocalPosition</c>), so the collider needs <b>no</b> offset: its size is
        /// the rect's size and it is centred on the object.</para>
        ///
        /// <para>This used to shrink the box by <c>heightLevel</c> and push it <i>up</i> by half the
        /// difference, on top of a <see cref="TileData.GetBounds"/> that had already lifted the whole
        /// cell by <c>heightLevel·0.25</c>. The two errors compounded: a raised tile's collider sat
        /// <c>20·heightLevel</c> px above its own surface — for a half-height step, a whole 40 px cell
        /// too high, i.e. floating in the cell above the ledge. Bullets and props use these colliders,
        /// so they were the half of the map that disagreed with the unit motor.</para>
        /// </summary>
        private void CreateSolidCollider()
        {
            BoxCollider2D boxCollider = gameObject.AddComponent<BoxCollider2D>();

            Rect solid = tileData != null
                ? tileData.GetBounds()
                : new Rect(0f, 0f, WorldConstants.TILE_SIZE, WorldConstants.TILE_SIZE);

            float pixelsToUnits = 1f / TileQueryConstants.UnitToPixel;
            boxCollider.size = new Vector2(solid.width, solid.height) * pixelsToUnits;
            boxCollider.offset = Vector2.zero;

            // Set layer
            gameObject.layer = LAYER_GROUND;
            
            tileCollider = boxCollider;
            
            if (debugSettings != null && debugSettings.LogTileColliderCreation)
            {
                Debug.Log($"[TileCollider] Created Wall collider at {transform.position}");
            }
        }

        /// <summary>
        /// Create a one-way platform collider.
        /// </summary>
        private void CreatePlatformCollider()
        {
            BoxCollider2D boxCollider = gameObject.AddComponent<BoxCollider2D>();

            Rect solid = tileData != null
                ? tileData.GetBounds()
                : new Rect(0f, 0f, WorldConstants.TILE_SIZE, WorldConstants.TILE_SIZE);

            float pixelsToUnits = 1f / TileQueryConstants.UnitToPixel;
            float halfHeight = solid.height * pixelsToUnits * 0.5f;

            // Platform is thinner (just the top surface), flush with the tile's solid top. The object
            // sits at the solid rect's centre, so "the top edge" is `halfHeight` above the object —
            // not `TILE/2`, which is what made a raised shelf's surface float above its own art.
            float height = 0.1f; // Thin platform
            boxCollider.size = new Vector2(solid.width * pixelsToUnits, height);
            boxCollider.offset = new Vector2(0f, halfHeight - height * 0.5f);

            // Unity's effector coupling is OPT-IN: a Collider2D is only handed to an Effector2D on
            // the same GameObject when this flag is set. Without it the PlatformEffector2D below is
            // inert and the collider stays a plain SOLID box, so a catwalk could not be jumped up
            // through and a body hitting its underside collided — neither is true in AS3, where a
            // shelf blocks from above only (Unit.as:2570-2581).
            boxCollider.usedByEffector = true;

            // Set as trigger for one-way (or use PlatformEffector2D)
            gameObject.layer = LAYER_PLATFORM;
            
            // Add PlatformEffector2D for one-way collision
            PlatformEffector2D effector = gameObject.AddComponent<PlatformEffector2D>();
            effector.useOneWay = true;
            effector.useOneWayGrouping = true;
            effector.surfaceArc = 180f; // Only collide from top
            
            tileCollider = boxCollider;
            
            if (debugSettings != null && debugSettings.LogTileColliderCreation)
            {
                Debug.Log($"[TileCollider] Created Platform collider at {transform.position}");
            }
        }

        /// <summary>
        /// Create a stair/slope collider.
        /// </summary>
        private void CreateStairCollider()
        {
            float size = WorldConstants.TILE_SIZE / 100f;
            gameObject.layer = LAYER_GROUND;

            if (tileData != null && tileData.slopeType != 0)
            {
                PolygonCollider2D polygonCollider = gameObject.AddComponent<PolygonCollider2D>();
                Vector2[] points = tileData.slopeType > 0
                    ? new[]
                    {
                        new Vector2(-size * 0.5f, -size * 0.5f),
                        new Vector2(size * 0.5f, -size * 0.5f),
                        new Vector2(size * 0.5f, size * 0.5f)
                    }
                    : new[]
                    {
                        new Vector2(-size * 0.5f, size * 0.5f),
                        new Vector2(-size * 0.5f, -size * 0.5f),
                        new Vector2(size * 0.5f, -size * 0.5f)
                    };
                polygonCollider.SetPath(0, points);
                tileCollider = polygonCollider;
            }
            else
            {
                BoxCollider2D boxCollider = gameObject.AddComponent<BoxCollider2D>();
                boxCollider.size = new Vector2(size, size * 0.5f);
                boxCollider.offset = new Vector2(0, -size * 0.25f);
                tileCollider = boxCollider;
            }
            
            if (debugSettings != null && debugSettings.LogTileColliderCreation)
            {
                Debug.Log($"[TileCollider] Created Stair collider at {transform.position}");
            }
        }

        /// <summary>
        /// Get the tile data.
        /// </summary>
        public TileData GetTileData()
        {
            return tileData;
        }

        #region IDestructibleTile

        /// <summary>
        /// P0-4. Damages this tile by <paramref name="destroyAmount"/>.
        ///
        /// Damage semantics ported verbatim from AS3 <c>Tile.udar(param1:int)</c>:
        /// <code>
        ///   if (indestruct || thre &gt; param1) return false;
        ///   hp -= param1;
        /// </code>
        /// That is: indestructible tiles ignore it, damage below <c>thre</c> is ignored
        /// entirely (no chip damage), otherwise HP is reduced by the full amount.
        ///
        /// AS3 has NO material-resistance or damage-type modifier in this path —
        /// <c>Tile.mat</c> is not consulted by <c>udar</c>. So neither is implemented here.
        /// The "Explosive ignores Metal resistance" wording on IDestructibleTile is
        /// speculative API design, not original behaviour. See REPLICA_BEHAVIOR_CONTRACT.
        /// </summary>
        public void ApplyDestruction(Vector3 worldPosition, float destroyAmount, DamageType damageType,
                                     DecalType decal)
        {
            DamageThisTile(destroyAmount, worldPosition, decal);
        }

        /// <summary>
        /// P0-4. Damages this tile as part of an area explosion.
        ///
        /// This deliberately does NOT iterate a radius. <see cref="Projectile.Detonate"/> and
        /// <see cref="ExplosionTileDamage"/> already enumerate every tile inside the blast with
        /// Physics2D.OverlapCircleAll and call this once per tile, so the caller owns the radius filter.
        /// Applying a full area of damage here would multiply the damage by the number of
        /// overlapping tiles.
        ///
        /// <para><b>The decal lands on this tile, not on the blast.</b> AS3's <c>explDestroy()</c> hands
        /// <c>hitTile</c> the <i>cell centre</i> — <c>(cellX + 0.5) * tileX</c> (<c>Bullet.as:735</c>) —
        /// so a grenade leaves one mark per damaged tile, scattered over the blast. Passing the blast
        /// centre through instead stacked every mark of one explosion on the same pixel.</para>
        /// </summary>
        public void ApplyDestructionRadius(Vector3 worldPosition, float radius,
                                           float destroyAmount, DamageType damageType, DecalType decal)
        {
            DamageThisTile(destroyAmount, transform.position, decal);
        }

        /// <summary>
        /// One hit on this tile: the rule, then the two side effects only a tile <i>object</i> has — the
        /// decal it leaves and, at zero hp, its death.
        ///
        /// <para><b>The gate order is <see cref="TileHitRules"/>'s, and it is not the same as
        /// <see cref="TileDestruction"/>'s.</b> <c>udar</c> only knows <c>indestruct</c> and <c>thre</c>;
        /// <c>Location.hitTile</c> wraps it in the <c>100</c> marker, the <c>desoff</c> room gate and the
        /// <c>phis</c> gate, and it is the wrapper that decides whether a mark appears. Calling
        /// <c>TileDestruction</c> directly — which this method used to do — loses all three, and the
        /// visible one is the <c>desoff</c> gate: without it the port lets the player shoot open rooms
        /// the designer sealed.</para>
        ///
        /// <para><b>"Chipped but standing" used to return here with nothing to show for it.</b> That is
        /// the whole of the missing feature: AS3 stamps a decal on every hit that did not kill the tile,
        /// and the port returned at the <c>Damaged</c> case, so a wall took twenty hits and looked
        /// untouched.</para>
        /// </summary>
        private void DamageThisTile(float destroyAmount, Vector3 worldPosition, DecalType decal)
        {
            if (tileData == null) return;

            RoomInstance room = owner != null ? owner.Room : null;
            bool destroyOn = room == null || room.environment == null || room.environment.destroyOn;
            bool roomActive = room == null || room.isActive;

            TileHitDecision decision = TileHitRules.Resolve(
                tileData,
                destroyAmount,
                (int)decal,
                destroyOn: destroyOn,
                roomActive: roomActive);

            if (decision.DrawDecal)
            {
                StampDecal(decision, DecalPointFor(worldPosition));
            }

            if (decision.Outcome == TileHitOutcome.Refused)
            {
                LogRefusal(decision.Refusal, destroyAmount);
            }

            if (!decision.ShouldDie) return;

            // `Location.as:2548-2553` — the death decal is stamped by the *same* branch that kills the
            // tile, not by the damaging one, and it is the only decal a destroyed tile ever gets.
            if (roomActive)
            {
                StampDeathDecal(decision);
            }

            DestroyThisTile();
        }

        /// <summary>
        /// AS3 <c>Grafon.tileDie(tile, type)</c> — the decal a <b>destroyed</b> tile leaves behind
        /// (<c>Location.as:2548-2553</c>, <c>Grafon.as:968-1053</c>).
        ///
        /// <para><b>Why this is not <see cref="StampDecal"/> with a different table.</b> The two differ in
        /// three ways that are all visible: the table is keyed on the type <i>band</i> for the sprite and
        /// on the <i>material</i> for the debris, the scale is always 1 and the rotation always 0, and the
        /// position is the <b>tile centre</b> — <c>(X + 0.5) * tileX</c> — not the impact point, so a
        /// round that clipped a corner still blows the whole cell.</para>
        ///
        /// <para><b>The position here is the tile object's own transform</b>, which the builders place at
        /// the centre of <see cref="TileData.GetBounds"/>. For a full-height tile that is the oracle's
        /// cell centre exactly; for a raised half or quarter tile it is the centre of the <i>solid</i>
        /// part, which is where a mark belongs anyway.</para>
        ///
        /// <para><b><c>fake</c> is always false.</b> AS3 sets <c>Tile.fake</c> only from an
        /// <c>&lt;area tilehp='1'&gt;</c> (<c>Area.as:171-176</c>), and that attribute has no port
        /// counterpart — so the arm exists in <see cref="TileDecalRules.ResolveDestroy"/> and is pinned
        /// by fixtures, but nothing in this game can reach it yet.</para>
        /// </summary>
        private void StampDeathDecal(in TileHitDecision decision)
        {
            TileDecalLayer decals = owner != null ? owner.Decals : null;
            if (decals == null) return;

            TileDecalResult decal = TileDecalRules.ResolveDestroy(
                decision.DecalType,
                (int)tileData.material,
                fake: false);

            Vector3 centre = transform.position;
            float cell = RoomTerrainGeometry.DefaultCellSize;

            // AS3's order is erase-then-draw (`Grafon.as:1279-1330`): the tile is punched out of the
            // room bitmap, and only then does the scorch land. Anything already stamped into this cell
            // — an earlier bullet hole, or a neighbour's debris rim that overhung it — is gone by the
            // time the death decal arrives. Dropping it here is the same rule, in the same order.
            decals.ClearCell(centre, cell);

            // The scorch is 71x72 on a 40x40 cell, so it is stamped as one cell-sized patch per
            // *still-solid* neighbour rather than as one sprite: that is the clip `decal()` gets from
            // restoring the destination alpha. A pole's arm has no scorch at all (it emits shards
            // instead), so it stamps nothing either way.
            decals.StampDeath(decal, centre, cell, SolidNeighbours(), Random.value);
        }

        /// <summary>
        /// Which of this cell's four orthogonal neighbours still hold a solid tile — AS3's "does the
        /// wall have alpha there" test, asked of the port's tile grid instead of a bitmap.
        ///
        /// <para><b>Grid <c>y + 1</c> is north.</b> <c>TileData.GetBounds</c> maps the grid row straight
        /// onto world +Y (<c>WorldCoordinates.PixelToUnity</c> does not flip at the tile level — only
        /// the land grid mirrors), so no conversion is needed beyond the two index steps.</para>
        ///
        /// <para>A room with no tiles answers <see cref="TileNeighbourSides.None"/>, which draws no
        /// debris — the same "nothing to stamp into" the missing layer already means.</para>
        /// </summary>
        private TileNeighbourSides SolidNeighbours()
        {
            RoomInstance room = owner != null ? owner.Room : null;
            if (room == null || tileData == null) return TileNeighbourSides.None;

            Vector2Int cell = tileData.gridPosition;
            TileNeighbourSides sides = TileNeighbourSides.None;

            if (IsSolid(room, cell.x, cell.y + 1)) sides |= TileNeighbourSides.North;
            if (IsSolid(room, cell.x, cell.y - 1)) sides |= TileNeighbourSides.South;
            if (IsSolid(room, cell.x + 1, cell.y)) sides |= TileNeighbourSides.East;
            if (IsSolid(room, cell.x - 1, cell.y)) sides |= TileNeighbourSides.West;

            return sides;
        }

        private static bool IsSolid(RoomInstance room, int x, int y)
        {
            // `Destroy()` sets physicsType to Air, so a neighbour that has already died answers false
            // without a second flag — and `GetTileAtCoord` returns null outside the room, which is the
            // same answer for a room edge.
            TileData tile = room.GetTileAtCoord(new Vector2Int(x, y));
            return tile != null && tile.IsSolid();
        }

        /// <summary>
        /// Where a hit mark is actually drawn: <paramref name="worldPosition"/> projected into this
        /// tile's solid rect, so the mark lands <b>on the tile it struck</b>.
        ///
        /// <para><b>The defect this removes.</b> The caller's point is a <i>surface contact</i> — the
        /// swept shape's first touch, which is exactly on the tile's face (and, for a long capsule,
        /// tens of pixels outside it). AS3 never passes that: its decal point is the bullet's own
        /// centre, and the hit test requires it to satisfy <c>X &gt;= phX1 &amp;&amp; X &lt;= phX2</c>
        /// (<c>weapon/Bullet.as:476</c>). Stamped at the contact, a hole sat centred on the seam between
        /// two tiles, burning half of it into the neighbour — the reported "holes outside a tile".</para>
        ///
        /// <para><b>The depth is a roll, and that is the oracle too.</b> A bullet advances in sub-steps
        /// of at most <c>World.maxdelta</c> = 9 px and tests the tile after each, so its centre is 0..9
        /// px past the face when the mark is stamped. Rolling that reproduces the oracle's scatter as
        /// well as its guarantee: holes along one wall vary by a few pixels instead of stacking.</para>
        ///
        /// <para><b>The rect is <see cref="TileData.GetBounds"/>, not the cell.</b> That is
        /// <c>phX1..phX2 / phY1..phY2</c>, and for a raised half or quarter tile it is the solid part —
        /// which is where the object is anchored (<c>TileCollisionBuilder.LocalPositionFor</c>) and
        /// where a mark belongs. An explosion already passes this exact point, so the projection is a
        /// no-op for it.</para>
        /// </summary>
        private Vector3 DecalPointFor(Vector3 worldPosition)
        {
            if (tileData == null) return worldPosition;

            Rect solid = tileData.GetBounds();
            float halfW = solid.width  * 0.5f * TileQueryConstants.PixelToUnit;
            float halfH = solid.height * 0.5f * TileQueryConstants.PixelToUnit;

            Vector3 centre = transform.position;
            float depth = Random.value * TileDecalRules.MaxImpactDepthPx * TileQueryConstants.PixelToUnit;

            TileDecalRules.TilePointPx placed = TileDecalRules.PlaceInsideTile(
                new TileDecalRules.TilePointPx(worldPosition.x, worldPosition.y),
                new TileDecalRules.TilePointPx(centre.x - halfW, centre.y - halfH),
                new TileDecalRules.TilePointPx(centre.x + halfW, centre.y + halfH),
                depth);

            return new Vector3(placed.X, placed.Y, worldPosition.z);
        }

        /// <summary>
        /// Hands the decal to the room's <see cref="TileDecalLayer"/>, resolving the sprite table here
        /// so the layer stays a dumb pool.
        ///
        /// <para><b>A missing layer is not an error.</b> Rooms built without a visual controller (tests,
        /// editor preview, the headless harness) have nowhere to stamp, and the oracle's own
        /// <c>if(this.active)</c> guard is the same admission that a hit can be real and invisible.</para>
        /// </summary>
        private void StampDecal(in TileHitDecision decision, Vector3 worldPosition)
        {
            TileDecalLayer decals = owner != null ? owner.Decals : null;
            if (decals == null) return;

            // The AS3 material argument is the raw `mat` index, not the port's MaterialType — see
            // TileDecalRules.Resolve. `TileData.material` stores the raw index (TileDecoder casts it).
            int material = (int)tileData.material;

            TileDecalResult decal = TileDecalRules.Resolve(
                decision.DecalType,
                material,
                decision.DecalBlocked,
                decision.DecalRatio,
                Random.value,   // AS3 `_loc11_` — the scale roll
                Random.value,   // AS3 `_loc12_` — the rotation roll
                Random.value);  // AS3 the single gate roll

            decals.Stamp(decal, worldPosition, Random.value);
        }

        /// <summary>
        /// Says why a hit did nothing, when the owner has asked for that.
        ///
        /// <para><b>This exists because "the minigun will not open this door" and "the minigun used to
        /// open this door" look identical from the outside.</b> AS3's rule is `if (indestruct || thre &gt;
        /// param1) return false` — below-threshold damage is dropped <i>entirely</i>, with no chip damage
        /// and no accumulation, so a weapon carrying <c>destroy = 10</c> can never break a tile whose
        /// <c>thre</c> is 50 no matter how long the trigger is held. Printing both numbers turns a
        /// regression hunt into a one-line answer.</para>
        ///
        /// <para>The wording comes from <see cref="TileDestruction.Describe"/> rather than from a second
        /// copy of the reasons here, so the log cannot disagree with the rule that produced it.</para>
        /// </summary>
        private void LogRefusal(TileDamageOutcome outcome, float destroyAmount)
        {
            if (debugSettings == null || !debugSettings.LogTileDamageRefusals) return;

            Vector2Int coord = tileData.gridPosition;
            Debug.Log($"[TileDamage] tile ({coord.x}, {coord.y}) REFUSED {destroyAmount:0} damage: " +
                      $"{TileDestruction.Describe(outcome, tileData, destroyAmount)}. " +
                      "AS3 Tile.udar drops anything under `thre` outright — no chip damage.");
        }

        private void DestroyThisTile()
        {
            // Mirrors AS3 Tile.die(): phis=0, opac=0, visuals cleared.
            tileData.Destroy();

            // Drop the collider immediately so projectiles and units pass through.
            // Without this the tile stays solid even though it renders as destroyed.
            if (tileCollider != null)
            {
                if (Application.isPlaying)
                    Destroy(tileCollider);
                else
                    DestroyImmediate(tileCollider);
                tileCollider = null;
            }

            // Route through the owning manager when there is one: TileVisualManager.DestroyTile
            // also removes the tile from its `tileRenderers` registry. Calling
            // TileRenderer.DestroyTile() directly leaves a stale entry pointing at a GameObject
            // that destroys itself 0.5 s later, so anything iterating the registry (UpdateVisuals,
            // RefreshSprites) can touch a destroyed renderer.
            if (owner != null)
            {
                owner.DestroyTile(tileData.gridPosition);
            }
            else
            {
                // Fallback for tiles created outside TileVisualManager (tests, editor preview).
                //
                // Asked for by capability, not by component: this is the line that used to be
                // `GetComponent<TileRenderer>()` and was the last reason the two could not be
                // separated. A collider-only tile has no visual, so this resolves to null and the
                // method does nothing — which is the correct answer, not a missing case.
                ITileVisual visual = GetComponent<ITileVisual>();
                if (visual != null)
                {
                    visual.DestroyTile();
                }
            }

            // The tile grid is authoritative and read live, so no *collision* cache needs
            // invalidating. Two other things are stale:
            //
            //   1. Derived geometry — the LowLevelPhysics2D chain mirror still describes this tile
            //      as solid, and the run it belonged to has changed shape. Routed below.
            //   2. The kontur (edge) data of the surrounding tiles — their visible borders still
            //      assume this tile is solid. TODO(P2): recompute via KonturCalculator.
            //
            // Deliberately NOT via ITileQueryService.NotifyTilesMutated: this component holds no
            // query service, and constructing one here would MarkDirty a throwaway instance that
            // nobody reads. The room is the object both the visual side and the derived-geometry
            // side already share.
            //
            // The region carries a one-tile border. Removing a tile splits the run it belonged to
            // and can expose new faces on its neighbours, so the affected area is never just the
            // tile itself — a listener that rebuilt only the exact coordinate would leave stale
            // geometry one tile out.
            RoomInstance room = owner != null ? owner.Room : null;
            if (room != null)
            {
                Vector2Int coord = tileData.gridPosition;
                room.NotifyTilesMutated(new RectInt(coord.x - 1, coord.y - 1, 3, 3));
            }
        }

        #endregion

        /// <summary>
        /// Update collider if tile data changes.
        /// </summary>
        public void RefreshCollider()
        {
            if (tileData != null)
            {
                Initialize(tileData, debugSettings);
            }
        }
    }
}
