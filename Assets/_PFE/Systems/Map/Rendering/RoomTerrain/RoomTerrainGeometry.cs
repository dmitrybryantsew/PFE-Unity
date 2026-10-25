using UnityEngine;

namespace PFE.Systems.Map.Rendering.RoomTerrain
{
    /// <summary>
    /// Where one mask shape goes: which half-cell slot it occupies, and the transform that lands its
    /// registration point on the right place in the room.
    ///
    /// <para><b>Why a struct and not a direct <c>SetTransformMatrix</c> call.</b> The placement is the
    /// part of this renderer that can be wrong in a way that still draws something — a mirror applied
    /// to the wrong quarter, a height drop subtracted instead of added. Keeping it as data means the
    /// tests can pin the numbers without a scene, a Tilemap or an imported asset.</para>
    /// </summary>
    public readonly struct RoomTerrainPlacement
    {
        /// <summary>The half-cell grid coordinate the shape is written into.</summary>
        public readonly Vector3Int Cell;

        /// <summary>Offset from the cell's own origin to the shape's registration point.</summary>
        public readonly Vector2 Translation;

        /// <summary>Sign carries the flip; magnitude carries the height squash.</summary>
        public readonly Vector2 Scale;

        public RoomTerrainPlacement(Vector3Int cell, Vector2 translation, Vector2 scale)
        {
            Cell = cell;
            Translation = translation;
            Scale = scale;
        }

        /// <summary>The tile transform matrix: translate, no rotation, scale (with sign = mirror).</summary>
        public Matrix4x4 ToMatrix()
        {
            return Matrix4x4.TRS(Translation, Quaternion.identity, new Vector3(Scale.x, Scale.y, 1f));
        }

        public override string ToString()
        {
            return $"cell=({Cell.x},{Cell.y}) t=({Translation.x:0.###},{Translation.y:0.###}) s=({Scale.x:0.###},{Scale.y:0.###})";
        }
    }

    /// <summary>
    /// The arithmetic the quarter renderer runs per shape. Unity-free apart from the value types, so
    /// every number below is testable without an editor, a scene or an imported sprite.
    ///
    /// <para><b>The two facts that decide whether this draws correctly.</b> First, a mask PNG is
    /// rasterised at <c>zoom</c>× the source pixel grid, so a sprite's world size is a function of its
    /// own import PPU and says nothing about how big a quarter is — the scale has to be derived from
    /// the manifest's <c>cellPixels</c>, not assumed to be 1. Second, the registration point comes from
    /// the sprite's pivot, which is why these clips keep the pivot the exporter computed instead of a
    /// centre pivot.</para>
    /// </summary>
    public static class RoomTerrainGeometry
    {
        /// <summary>
        /// One tile in world units — <b>0.4, not 1</b>.
        ///
        /// <para>This project draws at 100 pixels per world unit (<c>WorldCoordinates.PixelToUnity</c>)
        /// and a tile is <c>WorldConstants.TILE_SIZE</c> = 40 px, so a tile is 0.4 units and the whole
        /// room is 19.2 × 10 units. The shipped renderer applies exactly this
        /// (<c>TileRenderer.ApplyScale</c>: <c>targetSize = TILE_SIZE / 100f</c>). Using 1 here would
        /// draw a correct-looking room at 2.5× the shipped scale, which is the kind of "it works" that
        /// only fails when something else is drawn next to it.</para>
        /// </summary>
        public const float DefaultCellSize = 0.4f;

        /// <summary>A quarter is half a tile.</summary>
        public static float QuarterSize(float cellSize)
        {
            return cellSize * 0.5f;
        }

        /// <summary>
        /// Scale to apply to a mask sprite so that one quarter of the source cell lands on one quarter
        /// of a world cell.
        ///
        /// <para>Derivation: the exporter rasterises at <paramref name="rasterZoom"/> pixels per source
        /// pixel, and a cell is <paramref name="cellPixels"/> source pixels, so a cell is
        /// <c>cellPixels * rasterZoom</c> raster pixels wide and must span <paramref name="cellSize"/>
        /// world units. A sprite of W raster pixels spans <c>W / pixelsPerUnit</c> world units at scale
        /// 1, so the scale that makes it span <c>W * cellSize / (cellPixels * rasterZoom)</c> is
        /// <c>pixelsPerUnit * cellSize / (cellPixels * rasterZoom)</c> — W cancels, which is the point:
        /// overhanging shapes (a 220×214 curve on a 160×160 quarter) get the same scale as square ones.</para>
        ///
        /// <para><b>This is why the importer can leave the PPU alone.</b> The export is rasterised at
        /// 50 px/m × zoom, so every mask arrives at <b>PPU 400</b>; this project's cell is 0.4 units at
        /// 100 px/unit, which wants <b>PPU 800</b> for the same art. Deriving the factor instead of
        /// rewriting 81 <c>.meta</c> files means the art stays a verbatim copy of the export and the
        /// difference is one number in one function.</para>
        ///
        /// <para>For the shipped numbers the factor is <b>0.5</b> (400 · 0.4 / (40 · 8)), and leaving it
        /// out draws every mask at <i>twice</i> its slot. A plain square mask hides that — a 2×-too-big
        /// square is still a square, and the material is sampled by world position, so overlapping
        /// copies are indistinguishable. A mask that is <i>not</i> a square does not hide it: a beam's
        /// <c>TileMask</c> covers 2×2 cells instead of 1, and the material texture inside it (40 px =
        /// one cell) then repeats twice down that square, which reads as "the beam is drawn three
        /// times".</para>
        /// </summary>
        public static float SpriteScale(float pixelsPerUnit, float cellSize, int cellPixels, double rasterZoom)
        {
            if (pixelsPerUnit <= 0f || cellSize <= 0f || cellPixels <= 0 || rasterZoom <= 0d)
                return 1f;

            return (float)(pixelsPerUnit * (double)cellSize / (cellPixels * rasterZoom));
        }

        /// <summary>
        /// Folds a mask sprite's fit factor into a placement's scale.
        ///
        /// <para><b>This is the step that was missing.</b> <see cref="SpriteScale"/> was written, derived
        /// and covered by four fixtures, and then never called from the renderer — so every mask drew at
        /// its raw import size, twice its slot, and the fixtures stayed green because they only ever
        /// asked the function about itself. A tested function that nothing calls is not implemented;
        /// the guard has to be on the composition, which is what
        /// <c>RoomTerrainGeometryTests.MaskPlacement_FitsTheSlotAfterSpriteScale</c> asserts.</para>
        ///
        /// <para>The multiplier is positive and is applied to <i>both</i> axes, so the sign that carries
        /// a mirror and the magnitude that carries a height squash both survive it.</para>
        /// </summary>
        public static RoomTerrainPlacement Scaled(RoomTerrainPlacement placement, float spriteScale)
        {
            if (spriteScale <= 0f || Mathf.Approximately(spriteScale, 1f)) return placement;

            return new RoomTerrainPlacement(
                placement.Cell,
                placement.Translation,
                placement.Scale * spriteScale);
        }

        /// <summary>
        /// How much world a placed sprite actually covers along one axis: its import size, times the
        /// placement's scale magnitude.
        ///
        /// <para>This is the number that decides how many times a material texture repeats, because
        /// <c>PFE_RoomTerrain.shader</c> samples the material by <b>world position</b> — the mask only
        /// says where the material is allowed to exist. So "the beam is drawn twice" is never a
        /// placement count; it is this span divided by the texture's period.</para>
        /// </summary>
        public static float PlacedSpan(float spritePixels, float pixelsPerUnit, float scale)
        {
            if (spritePixels <= 0f || pixelsPerUnit <= 0f) return 0f;

            return spritePixels / pixelsPerUnit * Mathf.Abs(scale);
        }

        /// <summary>
        /// Distance between two repeats of a material texture in world units.
        ///
        /// <para><c>RoomTerrainRenderer.MaterialFor</c> sets <c>_TexelsPerMeter = cellPixels /
        /// cellSize</c>, i.e. one texel per source pixel, so a texture repeats every
        /// <c>texturePixels / texelsPerMeter</c> units. At the shipped numbers that is 100 texels per
        /// unit, and <c>tShelf</c> (240×40) has a <b>0.4-unit</b> vertical period — exactly one cell.
        /// That coincidence is what makes a too-tall beam mask visible as a repeated girder.</para>
        /// </summary>
        public static float MaterialPeriod(float texturePixels, float texelsPerMeter)
        {
            if (texturePixels <= 0f || texelsPerMeter <= 0f) return 0f;

            return texturePixels / texelsPerMeter;
        }

        /// <summary>
        /// How many copies of a material texture fit inside a placed mask. <b>1 is correct</b>; 2 means
        /// the mask is twice its slot and the surface is drawn twice, split around the cell.
        /// </summary>
        public static float MaterialPeriods(float placedSpan, float period)
        {
            return period > 0f ? placedSpan / period : 0f;
        }

        /// <summary>
        /// Where an overlay sprite sorts: <b>layer and order together</b>, because a large order inside
        /// a layer that sorts underneath is still underneath.
        ///
        /// <para><b>This is the bug the toggle could not fix.</b> Every ladder and tread form carries
        /// <c>rear='1'</c>, so the "above the walls" branch used to compute
        /// <c>RearOrder + OverlayAboveOrder</c> = -500 + 12 = <b>-488</b> and keep the sprite on
        /// <c>BackgroundDecor</c>. -488 is a big number, but <c>BackgroundDecor</c> sorts <i>below</i>
        /// <c>MainTiles</c>, where the front walls live, so switching the toggle moved the overlay from
        /// -600 to -488 and changed nothing on screen.</para>
        ///
        /// <para>With <paramref name="aboveWalls"/> the overlay goes to the wall layer itself at
        /// <c>OverlayAboveOrder</c>, which is what Remains-Unity does (<c>FrontSpriteOrder = 10</c>
        /// over <c>FloorOrder = -20</c>). Without it, AS3's bitmap order: blitted into the wall bitmap
        /// before the materials, so under the walls and visible only through the channel the mask
        /// carves.</para>
        /// </summary>
        public static void OverlaySort(bool rear, bool aboveWalls, out string layer, out int order)
        {
            if (aboveWalls)
            {
                layer = MapSortingLayers.MainTiles;
                order = RoomTerrainRenderer.OverlayAboveOrder;
                return;
            }

            layer = rear ? MapSortingLayers.BackgroundDecor : MapSortingLayers.MainTiles;
            order = rear
                ? RoomTerrainRenderer.RearOrder + RoomTerrainRenderer.OverlayRearOffset
                : RoomTerrainRenderer.OverlayOrder;
        }

        /// <summary>
        /// Vertical squash for a lowered ceiling: <c>heightLevel</c> 0 keeps the full tile, 3 keeps a
        /// quarter. Matches <c>TileVisualData.HeightScale</c> exactly (<c>1 - h * 0.25</c>), which is
        /// what the shipped renderer already applies — so a height difference between the two sides of
        /// the A/B window is a bug, not a design choice.
        /// </summary>
        public static float HeightScale(int heightLevel)
        {
            return Mathf.Clamp01(1f - Mathf.Clamp(heightLevel, 0, 3) * 0.25f);
        }

        /// <summary>How far a floor strip moves down with a lowered ceiling.</summary>
        public static float HeightDrop(int heightLevel, float cellSize)
        {
            return cellSize * Mathf.Clamp(heightLevel, 0, 3) / 4f;
        }

        /// <summary>Half-cell X slot for a quarter of cell <paramref name="x"/>.</summary>
        public static int QuarterCellX(int x, RoomQuarter quarter)
        {
            return x * 2 + (RoomQuarterRules.IsRight(quarter) ? 1 : 0);
        }

        /// <summary>Half-cell Y slot for a quarter of cell <paramref name="y"/>.</summary>
        public static int QuarterCellY(int y, RoomQuarter quarter)
        {
            return y * 2 + (RoomQuarterRules.IsTop(quarter) ? 1 : 0);
        }

        /// <summary>
        /// Placement for one corner-mask quarter.
        ///
        /// <para><b>Registration is the cell corner this quarter touches, not the quarter's centre.</b>
        /// The exported pivot sits on the shape's registration point (the top-left of the 20×20 quarter
        /// box, plus whatever the curve overhangs), so putting that point on the cell corner is what
        /// makes four quarters meet without a seam. A centre pivot would need the shape to be symmetric,
        /// and these are not.</para>
        ///
        /// <para><paramref name="heightScale"/> squashes the shape onto the lowered ceiling and
        /// <paramref name="drop"/> moves a floor strip down with it; both are 1 and 0 for an ordinary
        /// tile. The vertical flip multiplies the <i>squashed</i> scale, matching Remains-Unity's
        /// <c>new Vector2(flipX ? -1 : 1, (flipY ? -1 : 1) * scale)</c>.</para>
        /// </summary>
        public static RoomTerrainPlacement Quarter(
            RoomQuarter quarter,
            int x,
            int y,
            float cellSize,
            float heightScale,
            float drop,
            bool flipX,
            bool flipY)
        {
            bool top = RoomQuarterRules.IsTop(quarter);
            bool right = RoomQuarterRules.IsRight(quarter);
            float quarterSize = QuarterSize(cellSize);

            int cellX = x * 2 + (right ? 1 : 0);
            int cellY = y * 2 + (top ? 1 : 0);

            var cellOrigin = new Vector2(cellX * quarterSize, cellY * quarterSize);
            var registration = new Vector2(
                (x + (right ? 1 : 0)) * cellSize,
                y * cellSize + (top ? cellSize * heightScale : 0f) - drop);

            var scale = new Vector2(flipX ? -1f : 1f, (flipY ? -1f : 1f) * heightScale);

            return new RoomTerrainPlacement(
                new Vector3Int(cellX, cellY, 0),
                registration - cellOrigin,
                scale);
        }

        /// <summary>
        /// Centre position for a <c>tileFront</c> overlay sprite on cell (<paramref name="x"/>,
        /// <paramref name="y"/>), given the sprite's world size and its registration point.
        ///
        /// <para><b>The registration point goes on the cell's top-left corner — not the canvas's
        /// corner, and not the cell's centre.</b> <c>Grafon.as:479-480</c> is
        /// <c>tile.x = i * Tile.tileX; tile.y = j * Tile.tileY</c>, which places the symbol's
        /// registration point at the cell corner. The exported PNG is the frame's <i>bounds</i>, so the
        /// canvas's own top-left is offset from that point by
        /// (<c>originX</c>, <c>originY</c>) = (5, 30) px — see
        /// <see cref="RoomTerrainArt.OverlayOriginXPixels"/> for the provenance.</para>
        ///
        /// <para><b>The arithmetic.</b> Canvas top-left = registration − (originX, −originY) in screen
        /// terms, and AS3's <c>y</c> runs down while the port's runs up, so the cell's <i>top</i> edge
        /// is <c>(y + 1) * cellSize</c> and the canvas's top edge is <c>originY</c> <i>above</i> it.
        /// The sprite is registered at its centre, so half its size comes back off again:
        /// <c>centre = (x·cell − originX + w/2, (y+1)·cell + originY − h/2)</c>.</para>
        ///
        /// <para><b>How far off the naive answers are.</b> For the real numbers (50×110 art at 100 px
        /// per unit, 0.4 cell, origin 5/30 px = 0.05/0.30 units) the correct centre is
        /// <c>(x·cell + 0.20, y·cell + 0.15)</c>. Pinning the canvas corner with no origin is 5 px
        /// right and 30 px low; centring the canvas on the cell is 5 px high. Small, but the mask
        /// carves a channel for the art to fill, so a few pixels of slip is the difference between
        /// "the ladder is there" and "the ladder is black".</para>
        ///
        /// <para>No <c>heightLevel</c> squash and no <c>opacity</c>: <c>Grafon.as</c> never scales this
        /// child and never reads <c>Tile.opac</c> for it.</para>
        /// </summary>
        public static Vector3 Overlay(
            int x,
            int y,
            float cellSize,
            float width,
            float height,
            float originX,
            float originY)
        {
            return new Vector3(
                x * cellSize - originX + width * 0.5f,
                (y + 1) * cellSize + originY - height * 0.5f,
                0f);
        }

        /// <summary>
        /// World size of a <c>tileFront</c> overlay canvas, from its size in <b>source pixels</b>.
        ///
        /// <para><b>Source pixels, not <c>Sprite.bounds</c>.</b> The overlay frames are 50×110 px
        /// canvases that must scale with the room's cell, and <c>cellSize / cellPixels</c> is the only
        /// conversion that does that. <c>Sprite.bounds.size</c> is the canvas already divided by the
        /// importer's <c>spritePixelsToUnits</c> — 100 for these frames — so it is a <i>fixed</i> world
        /// size that happens to equal <c>50 × cellSize/cellPixels</c> only while those two constants
        /// cancel. At the shipped 0.4 cell over 40 px they both give 0.01, so the mistake is
        /// invisible; at any other cell size the sprite would keep its size while the cell changed,
        /// and the registration offset (converted with the other factor) would stop agreeing with the
        /// half-size in <see cref="Overlay"/>.</para>
        ///
        /// <para>Pure and separate from the renderer so a fixture can ask about it: building the real
        /// <see cref="UnityEngine.Sprite"/> needs an engine call, and the choice between
        /// <c>rect</c> and <c>bounds</c> is exactly the part that can be wrong.</para>
        /// </summary>
        public static Vector2 OverlaySize(
            float sourceWidth,
            float sourceHeight,
            float cellSize,
            int cellPixels)
        {
            float pixelsToWorld = cellPixels > 0 ? cellSize / cellPixels : 0.01f;
            return new Vector2(sourceWidth * pixelsToWorld, sourceHeight * pixelsToWorld);
        }

        /// <summary>
        /// Placement for a whole-cell mask (the plain square, <c>TileMask</c>). No corner states: one
        /// shape per cell, registered at the cell's centre.
        /// </summary>
        public static RoomTerrainPlacement Whole(int x, int y, float cellSize, float heightScale, float drop)
        {
            float quarterSize = QuarterSize(cellSize);

            int cellX = x * 2;
            int cellY = y * 2;

            var cellOrigin = new Vector2(cellX * quarterSize, cellY * quarterSize);
            var registration = new Vector2(
                x * cellSize + cellSize * 0.5f,
                y * cellSize + cellSize * 0.5f * heightScale - drop);

            return new RoomTerrainPlacement(
                new Vector3Int(cellX, cellY, 0),
                registration - cellOrigin,
                new Vector2(1f, heightScale));
        }
    }
}
