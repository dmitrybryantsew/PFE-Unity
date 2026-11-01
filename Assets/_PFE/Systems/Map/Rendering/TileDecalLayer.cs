using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Stamps tile damage decals into a room — the port's replacement for AS3's <c>Grafon.frontBmp</c>.
    ///
    /// <para><b>What the oracle does, and why a bitmap was the right shape for it.</b> <c>Grafon</c>
    /// owns one room-sized <c>BitmapData</c> per layer and draws the walls into it once
    /// (<c>Grafon.as:414-663</c>). A decal is then just <c>frontBmp.draw(sprite, matrix, null, blend)</c>
    /// at a pixel position — no object, no sorting, and the marks survive until the next
    /// <c>frontBmp.fillRect</c>, which only a full room redraw does. That is the entire lifetime rule:
    /// <b>decals live as long as the room view</b>, not as long as the tile, and a redraw wipes them.</para>
    ///
    /// <para><b>Why this is a pool of sprites instead.</b> The port's default terrain renderer is a
    /// Tilemap driven by a world-space shader (<see cref="RoomTerrain.RoomTerrainRenderer"/>) — there is
    /// no room bitmap to draw into, and the fallback per-tile path composites one sprite per cell, so
    /// neither can accept an arbitrary-pixel stamp. A pooled <see cref="SpriteRenderer"/> at the impact
    /// point reproduces the visible result: it sits on the wall layer above the floor and border passes,
    /// and it dies with the room object like the bitmap does.</para>
    ///
    /// <para><b>Two divergences, both deliberate and both visible:</b></para>
    /// <list type="number">
    /// <item><description><b>The erase pass is not an erase.</b> AS3 draws the hole sprite with
    /// <c>BlendMode.ERASE</c>, which removes alpha from the wall bitmap and lets the backdrop show
    /// through. A sprite drawn on top cannot subtract from what is behind it, so the hole is simply not
    /// drawn. The scorch still lands correctly; what is lost is the see-through centre of a bullet hole.
    /// Faithfully reproducing it needs a stencil or a camera-colour grab, not a different sprite.</description></item>
    /// <item><description><b>Hardlight is composited as straight alpha.</b> Hardlight needs the
    /// destination colour, which a forward sprite pass does not have. The exported decals already carry
    /// their own burn (dark ring, bright core), so alpha compositing reproduces most of the look and
    /// loses only the highlight blow-out.</description></item>
    /// </list>
    ///
    /// <para><b>The cap.</b> A bitmap costs the same whether it holds one decal or ten thousand, so the
    /// oracle has no limit. Sprites do not: a room under sustained fire would grow a
    /// <see cref="SpriteRenderer"/> per hit for as long as the player stands there. <see cref="MaxDecals"/>
    /// is therefore a ring buffer — the oldest mark is recycled. It is a resource bound, not a rule.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TileDecalLayer : MonoBehaviour
    {
        /// <summary>
        /// Sorting order inside <see cref="MapSortingLayers.MainTiles"/>.
        ///
        /// <para><b>Above every terrain pass, and that is the oracle's order.</b>
        /// <c>Grafon.as:518</c> blits the overlays and <c>:526</c> the materials, and only then do the
        /// decals arrive — so a mark lands on top of the border, the floor band and the ledge overlay
        /// alike. The terrain renderer's highest fixed order is <c>MainOrder + FloorOrder + 1</c> (its
        /// floor overlay, <c>RoomTerrainRenderer.cs:1257</c>), so one above that is "on top of the
        /// picture" without reaching into <see cref="MapSortingLayers.Foreground"/>, which belongs to
        /// things that are genuinely in front of the wall.</para>
        /// </summary>
        public const int DecalSortingOrder = RoomTerrain.RoomTerrainRenderer.MainOrder +
                                             RoomTerrain.RoomTerrainRenderer.FloorOrder + 2;

        /// <summary>How many decals one room keeps before the oldest is recycled.</summary>
        public const int MaxDecals = 512;

        /// <summary>Resources folder the decal frames were extracted to.</summary>
        public const string ResourcesFolder = "TileDecals";

        private static readonly Dictionary<string, Sprite[]> FrameCache =
            new Dictionary<string, Sprite[]>();

        private static readonly HashSet<string> MissingReported = new HashSet<string>();

        private readonly List<SpriteRenderer> _pool = new List<SpriteRenderer>();
        private int _next;

        /// <summary>How many decals this room has stamped. Diagnostics and fixtures.</summary>
        public int Count => _pool.Count;

        /// <summary>Stamps one resolved decal at a world position.</summary>
        /// <param name="spriteName">
        /// A <see cref="TileDecalRules"/> sprite constant — the SWF class name, which is also the folder
        /// name under <c>Resources/TileDecals</c>.
        /// </param>
        /// <param name="worldPosition">Impact point in world space. The sprite is centre-pivoted.</param>
        /// <param name="scale">Uniform scale from the rule (<c>TileDecalResult.Scale</c>).</param>
        /// <param name="rotationRadians">Rotation from the rule, in radians.</param>
        /// <param name="frameRoll">
        /// Uniform 0..1, the same roll AS3 takes for
        /// <c>gotoAndStop(floor(random * totalFrames + 1))</c> (<c>Grafon.as:1279-1283</c>). Multi-frame
        /// decals (a 5-frame <c>bullet_tre</c>) pick a different crack per hit; a single-frame decal
        /// ignores it.
        /// </param>
        /// <returns>The renderer that now holds the decal, or <c>null</c> when the sprite is missing.</returns>
        public SpriteRenderer Stamp(
            string spriteName, Vector3 worldPosition, float scale, float rotationRadians, float frameRoll)
        {
            if (string.IsNullOrEmpty(spriteName)) return null;

            Sprite[] frames = LoadFrames(spriteName);
            if (frames == null || frames.Length == 0) return null;

            int index = Mathf.Clamp(Mathf.FloorToInt(frameRoll * frames.Length), 0, frames.Length - 1);

            SpriteRenderer renderer = Take();
            renderer.sprite = frames[index];
            renderer.transform.position = worldPosition;
            renderer.transform.localScale = new Vector3(scale, scale, 1f);
            renderer.transform.rotation = Quaternion.Euler(0f, 0f, rotationRadians * Mathf.Rad2Deg);
            renderer.enabled = true;
            return renderer;
        }

        /// <summary>
        /// Stamps both halves of a resolved decal. The erase half is skipped — see the type doc.
        /// </summary>
        public void Stamp(TileDecalResult decal, Vector3 worldPosition, float frameRoll)
        {
            if (decal.ScorchSprite != null)
            {
                Stamp(decal.ScorchSprite, worldPosition, decal.Scale, decal.RotationRadians, frameRoll);
            }
        }

        /// <summary>
        /// Stamps a <b>destroyed</b> tile's debris: one cell-sized patch per neighbour that still has a
        /// solid tile, each placed at that neighbour's cell centre.
        ///
        /// <para><b>This is the clip, done by hand.</b> AS3 draws the whole 71x72 death scorch and then
        /// restores the destination alpha over it (<c>Grafon.as:1301-1330</c>), so the rubble survives
        /// only where the wall was not erased. A sprite cannot be clipped by what is behind it, so the
        /// scorch is pre-cut into four cell-sized pieces (<see cref="TileDecalRules.PatchSprite"/>) and
        /// only the pieces belonging to a <i>live</i> neighbour are stamped. A destroyed tile therefore
        /// leaves a rim of rubble around the hole instead of a tile-shaped block of rubble — which is
        /// the difference the screenshots showed.</para>
        ///
        /// <para><b>Corners are not stamped.</b> The ring is a circle inscribed in its 71x72 box, so
        /// the four corner patches are nearly empty, and a corner belongs to no single cell — it would
        /// be the first thing left floating inside the hole once a diagonal neighbour died.</para>
        /// </summary>
        /// <param name="decal">The resolved death decal. <c>ScorchSprite</c> may be null (the pole arm).</param>
        /// <param name="cellCentre">World centre of the destroyed cell.</param>
        /// <param name="cellSize">One cell in world units — <c>RoomTerrainGeometry.DefaultCellSize</c>.</param>
        /// <param name="solidNeighbours">Which of N/S/E/W still hold a solid tile.</param>
        /// <param name="frameRoll">Uniform 0..1, the frame pick — as <see cref="Stamp(TileDecalResult, Vector3, float)"/>.</param>
        public void StampDeath(TileDecalResult decal, Vector3 cellCentre, float cellSize,
                               TileNeighbourSides solidNeighbours, float frameRoll)
        {
            if (decal.ScorchSprite == null) return;

            for (int i = 0; i < FourSides.Length; i++)
            {
                TileNeighbourSides side = FourSides[i];
                if ((solidNeighbours & side) == 0) continue;

                string patch = TileDecalRules.PatchSprite(decal.ScorchSprite, side);
                if (patch == null) continue;

                Stamp(patch, cellCentre + SideOffset(side, cellSize),
                      decal.Scale, decal.RotationRadians, frameRoll);
            }
        }

        /// <summary>
        /// Drops every decal centred inside one cell and answers how many it dropped.
        ///
        /// <para><b>This is the port's erase pass.</b> AS3's <c>decal()</c> punches the tile out of the
        /// room bitmap before the scorch is drawn, so a bullet hole stamped into a wall disappears when
        /// that wall is blown away — and so does a neighbour's debris rim that overhung the cell. A
        /// sprite pool cannot subtract alpha, but every decal is placed at the centre of the cell it
        /// belongs to, so "disable what is centred here" is the same rule in the same order.</para>
        /// </summary>
        public int ClearCell(Vector3 cellCentre, float cellSize)
        {
            float half = cellSize * 0.5f;
            int dropped = 0;

            for (int i = 0; i < _pool.Count; i++)
            {
                SpriteRenderer renderer = _pool[i];
                if (renderer == null || !renderer.enabled) continue;

                Vector3 p = renderer.transform.position;
                if (Mathf.Abs(p.x - cellCentre.x) >= half) continue;
                if (Mathf.Abs(p.y - cellCentre.y) >= half) continue;

                renderer.enabled = false;
                dropped++;
            }

            return dropped;
        }

        private static readonly TileNeighbourSides[] FourSides =
        {
            TileNeighbourSides.North,
            TileNeighbourSides.South,
            TileNeighbourSides.East,
            TileNeighbourSides.West,
        };

        private static Vector3 SideOffset(TileNeighbourSides side, float cellSize)
        {
            switch (side)
            {
                case TileNeighbourSides.North: return new Vector3(0f, cellSize, 0f);
                case TileNeighbourSides.South: return new Vector3(0f, -cellSize, 0f);
                case TileNeighbourSides.East: return new Vector3(cellSize, 0f, 0f);
                case TileNeighbourSides.West: return new Vector3(-cellSize, 0f, 0f);
                default: return Vector3.zero;
            }
        }

        /// <summary>
        /// Drops every decal. Called when the room is rebuilt, which is the port's equivalent of
        /// <c>frontBmp.fillRect(allRect, 0)</c> at <c>Grafon.as:418</c>.
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _pool.Count; i++)
            {
                if (_pool[i] != null) _pool[i].enabled = false;
            }
            _next = 0;
        }

        private SpriteRenderer Take()
        {
            if (_pool.Count < MaxDecals)
            {
                var go = new GameObject("TileDecal");
                go.transform.SetParent(transform, false);

                var renderer = go.AddComponent<SpriteRenderer>();
                renderer.sortingLayerName = MapSortingLayers.MainTiles;
                renderer.sortingOrder = DecalSortingOrder;
                _pool.Add(renderer);
                return renderer;
            }

            // Ring buffer: the oldest mark gives way. Advancing before use means the very first
            // recycle throws away decal 0 rather than decal MaxDecals-1.
            SpriteRenderer recycled = _pool[_next];
            _next = (_next + 1) % _pool.Count;
            return recycled;
        }

        private static Sprite[] LoadFrames(string spriteName)
        {
            if (FrameCache.TryGetValue(spriteName, out Sprite[] cached)) return cached;

            Sprite[] loaded = Resources.LoadAll<Sprite>(ResourcesFolder + "/" + spriteName);

            // A missing decal is silent in play — the hit simply leaves no mark — which is exactly the
            // failure this project keeps re-learning. Say it once per name instead.
            if ((loaded == null || loaded.Length == 0) && MissingReported.Add(spriteName))
            {
                Debug.LogWarning(
                    "[TileDecalLayer] no sprite frames at Resources/" + ResourcesFolder + "/" + spriteName +
                    " — tile hits of this type will leave no mark. Run the decal extraction.");
            }

            FrameCache[spriteName] = loaded;
            return loaded;
        }

        /// <summary>Test hook: forget the frame cache (a Resources load is editor-scoped).</summary>
        internal static void ClearFrameCache()
        {
            FrameCache.Clear();
            MissingReported.Clear();
        }
    }
}
