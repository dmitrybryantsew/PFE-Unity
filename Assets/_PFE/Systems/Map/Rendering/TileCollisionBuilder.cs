using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Systems.Map;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Builds the room's <b>collision</b> without building its picture.
    ///
    /// <para><b>Why this exists.</b> A tile GameObject used to be one object with two jobs: a
    /// <see cref="TileRenderer"/> (a composited sprite, ~570 ms of pixel work per room) and a
    /// <see cref="TileCollider"/> (the room's physics). The quarter renderer draws the terrain, so the
    /// sprite half is dead weight in the default path — but it could not be dropped, because the
    /// collider lived on the same object. This class builds exactly that object minus the sprite: same
    /// name, same transform, same collider, same <c>TileSurface</c>, no <see cref="TileRenderer"/>, no
    /// <c>TileCompositor</c> call, no atlas upload.</para>
    ///
    /// <para><b>What it replaces.</b> <see cref="TileVisualManager.CreateAllTiles"/> plus
    /// <c>SetVisualsSuppressed(true)</c>. Suppression was the honest answer while the two halves were
    /// one object — hide the sprite, keep the object — but it still paid for every sprite it hid, and it
    /// left a state machine (<c>ShouldEnable</c>) whose only purpose was to keep the hidden half hidden.
    /// With this builder the quarter path is "build the colliders, do not build the sprites", and the
    /// shipped path is unchanged: build both.</para>
    ///
    /// <para><b>Parity with the shipped path is the whole contract.</b> The transform comes from
    /// <see cref="LocalPositionFor"/>, which is <c>TileRenderer.GetLocalPosition</c> copied field for
    /// field — a collider half a cell off would not read as "the physics moved", it would read as
    /// "the player stands on air", and the picture would look perfect. The collider geometry itself is
    /// still <see cref="TileCollider.Initialize"/>'s; this class decides only <i>which</i> tiles get one
    /// and <i>where</i> the object sits.</para>
    ///
    /// <para><b>One deliberate difference.</b> <see cref="NeedsCollision"/> is
    /// <c>physicsType != Air</c> — the oracle's rule (<c>phis</c> decides solidity) rather than the
    /// shipped path's <c>renderable &amp;&amp; physicsType != Air</c>. A non-air tile whose art is missing
    /// therefore gets a collider here and did not get one before. That is the oracle's answer and it
    /// removes an invisible walk-through wall; it is called out because it is the only tile the two
    /// paths disagree about.</para>
    /// </summary>
    public sealed class TileCollisionBuilder : ITileOwner
    {
        private readonly RoomInstance room;
        private readonly Transform parent;
        private readonly PfeDebugSettings debugSettings;
        private readonly Dictionary<Vector2Int, TileCollider> colliders =
            new Dictionary<Vector2Int, TileCollider>();

        public TileCollisionBuilder(RoomInstance room, Transform parent, PfeDebugSettings debugSettings = null)
        {
            this.room = room;
            this.parent = parent;
            this.debugSettings = debugSettings;
        }

        /// <inheritdoc />
        public RoomInstance Room => room;

        /// <inheritdoc />
        public TileDecalLayer Decals { get; set; }

        /// <summary>Colliders built and still alive.</summary>
        public int Count => colliders.Count;

        /// <summary>
        /// Whether a tile needs a Unity collider at all.
        ///
        /// <para>Pure, and public, because it is the one rule this class owns and it is worth pinning
        /// offline: a fixture can ask it about every physics type without building a GameObject.</para>
        /// </summary>
        public static bool NeedsCollision(TileData tile)
        {
            return tile != null && tile.physicsType != TilePhysicsType.Air;
        }

        /// <summary>
        /// Room-local position of a tile object, identical to <c>TileRenderer.GetLocalPosition</c>.
        ///
        /// <para><b>Two things here are not arbitrary.</b> The position is the centre of
        /// <see cref="TileData.GetBounds"/> — the tile's <i>solid</i> rect, which a
        /// <c>heightLevel &gt; 0</c> tile shortens from the top (AS3 <c>setZForm</c> writes only
        /// <c>phY1</c>), so the centre of a raised tile is lower than the cell centre. The collider is
        /// built with <b>no</b> offset from this object, so the two must agree: this rect's centre and
        /// this rect's size are the whole geometry. And the z is <c>y * 0.01</c>, the shipped path's
        /// depth ordering: it does nothing to a 2D collider but keeps a collider-only tile
        /// indistinguishable from a drawn one in the hierarchy and in any tool that sorts by z.</para>
        /// </summary>
        public static Vector3 LocalPositionFor(TileData tile)
        {
            if (tile == null) return Vector3.zero;

            Vector3 unityPos = WorldCoordinates.PixelToUnity(tile.GetBounds().center);
            unityPos.z = tile.gridPosition.y * 0.01f;
            return unityPos;
        }

        /// <summary>
        /// Builds one collider-only GameObject per tile that needs collision.
        ///
        /// <para>Iterates the same way <see cref="TileVisualManager.CreateAllTiles"/> does (x outer,
        /// y inner) so the two paths create their objects in the same order — a difference there would
        /// not be a bug, but it would make two hierarchy dumps impossible to diff.</para>
        /// </summary>
        /// <returns>The number of tile objects created.</returns>
        public int BuildAll()
        {
            if (room == null)
            {
                Debug.LogError("[TileCollision] Cannot build: room is null.");
                return 0;
            }

            if (room.tiles == null)
            {
                Debug.LogError($"[TileCollision] Room '{room.id}' has a null tiles array.");
                return 0;
            }

            if (parent == null)
            {
                Debug.LogError("[TileCollision] Cannot build: no parent transform for the tile objects.");
                return 0;
            }

            int created = 0;

            for (int x = 0; x < room.width; x++)
            {
                for (int y = 0; y < room.height; y++)
                {
                    var coord = new Vector2Int(x, y);
                    TileData tile = room.GetTileAtCoord(coord);

                    if (!NeedsCollision(tile)) continue;
                    if (colliders.ContainsKey(coord)) continue;

                    colliders[coord] = Create(tile);
                    created++;
                }
            }

            return created;
        }

        /// <summary>One tile object: name, transform, <see cref="TileCollider"/>. No renderer.</summary>
        private TileCollider Create(TileData tile)
        {
            var go = new GameObject($"Tile_{tile.gridPosition.x}_{tile.gridPosition.y}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = LocalPositionFor(tile);

            var collider = go.AddComponent<TileCollider>();
            collider.Initialize(tile, debugSettings, this);
            return collider;
        }

        /// <summary>The collider for a cell, or null. For diagnostics and fixtures.</summary>
        public TileCollider GetCollider(Vector2Int coord)
        {
            return colliders.TryGetValue(coord, out TileCollider collider) ? collider : null;
        }

        /// <summary>
        /// Re-derives one cell's collision from the tile data as it is now.
        ///
        /// <para><b>Three cases, and each one is a real edit.</b> A cell can gain collision (a door
        /// un-carving, the room editor raising <c>physicsType</c>), lose it (a tile carved to air), or
        /// keep it with a different shape — <c>heightLevel</c> <i>shortens</i> the box from the top,
        /// <c>slopeType</c> changes the polygon. The last one is why the position is rewritten as well
        /// as the collider: the object sits at the solid rect's centre, which moves down as the rect
        /// gets shorter, so refreshing the component alone would leave the box half the shortening too
        /// high — which reads as "the player floats" rather than as "the collider is stale".</para>
        /// </summary>
        public void RebuildCell(Vector2Int coord)
        {
            if (room == null) return;

            TileData tile = room.GetTileAtCoord(coord);
            bool needs = NeedsCollision(tile);

            if (!needs)
            {
                DestroyTile(coord);
                return;
            }

            if (!colliders.TryGetValue(coord, out TileCollider existing) || existing == null)
            {
                colliders[coord] = Create(tile);
                return;
            }

            existing.transform.localPosition = LocalPositionFor(tile);
            existing.RefreshCollider();
        }

        /// <summary>
        /// A tile died. Drops it from the registry and destroys its object.
        ///
        /// <para><b>Immediately, unlike the shipped path's 0.5 s delay.</b> <c>TileRenderer.DestroyTile</c>
        /// waits because it plays a fade; a collider-only tile has nothing to fade, and leaving the
        /// object alive would leave a GameObject per destroyed wall in the room for half a second for no
        /// reason. The collider itself is already gone by the time this runs —
        /// <see cref="TileCollider"/> destroys it first, so nothing can hit it during the delay.</para>
        /// </summary>
        public void DestroyTile(Vector2Int coord)
        {
            if (!colliders.TryGetValue(coord, out TileCollider collider)) return;

            colliders.Remove(coord);

            if (collider != null) DestroyObject(collider.gameObject);
        }

        /// <summary>Destroys every tile object this builder created.</summary>
        public void DestroyAll()
        {
            foreach (KeyValuePair<Vector2Int, TileCollider> entry in colliders)
            {
                if (entry.Value != null) DestroyObject(entry.Value.gameObject);
            }

            colliders.Clear();
        }

        /// <summary>
        /// <c>Destroy</c> throws in edit mode, and the room editor builds rooms outside play mode.
        /// Same seam as <c>TileRenderer.DestroyTile</c> and <c>RoomTerrainRenderer.DestroyObject</c>.
        /// </summary>
        private static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null) return;

            if (Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }
    }
}
