using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using Profiler = PFE.Core.Profiling.PfeProfiler;

namespace PFE.Systems.Map.Rendering.RoomTerrain
{
    /// <summary>
    /// Draws a room the way Remains-Unity draws one: half-cell Tilemaps holding shape masks, with the
    /// material bitmap laid in world space by a shader.
    ///
    /// <para><b>This is the default terrain renderer.</b> <c>RoomVisualController</c> builds it for a
    /// room and then builds the room's tile objects <b>collision-only</b>
    /// (<c>TileCollisionBuilder</c>) — no composited sprites, no <c>TileCompositor</c> work. The shipped
    /// per-tile path is the fallback and the A/B control: it runs when this cannot be built (no art
    /// asset, no material database, no shader) and when the flag is switched off with
    /// <c>terrain quad off</c>. It is kept whole on purpose — it is currently the only thing that
    /// <i>proves</i> this renderer is right.</para>
    ///
    /// <para><b>What is structurally different from the shipped path.</b> There is no per-tile bitmap:
    /// the mask sprite is a stencil and the material texture is sampled from the fragment's world
    /// position, so cell boundaries do not exist in the shader and the phase offsets
    /// (<c>tBlocks (12, 2)</c>) have nothing left to fix. Each cell contributes up to four mask
    /// placements on a Tilemap instead of one composited texture, which is the trade: more vertices,
    /// no CPU pixel work, and no seam.</para>
    ///
    /// <para><b>What it does not do.</b> No lighting — the shader multiplies by a single ambient
    /// number, the same one the backdrop uses. Lamp falloff, the wall edge map and cast shadows are
    /// the next tier and need a light buffer this renderer does not build.</para>
    /// </summary>
    public sealed class RoomTerrainRenderer : MonoBehaviour
    {
        /// <summary>Resources path. Kept in Resources so a build includes the shader without a material asset.</summary>
        public const string ShaderResourcePath = "RoomTerrain/PFE_RoomTerrain";

        /// <summary>Shader name, used as the fallback when the Resources load fails.</summary>
        public const string ShaderName = "PFE/RoomTerrain";

        /// <summary>Mask name used when a material declares none — the original's own fallback.</summary>
        public const string DefaultMaskName = "TileMask";

        /// <summary>Sorting orders inside a layer. Back is a separate layer, so these only order the front trio.</summary>
        public const int MainOrder = 0;
        public const int BorderOrder = 1;
        public const int FloorOrder = 2;

        /// <summary>
        /// Sorting order for <c>tileFront</c> overlays — ladders, stair treads and ledges — when
        /// <see cref="Options.overlayAboveMaterials"/> is <b>off</b>.
        ///
        /// <para><b>Under the walls, which is what AS3 does and therefore the default.</b>
        /// <c>Grafon.as:470-499</c> adds every overlay to a <c>front</c> sprite, and <c>:518</c> blits
        /// that sprite into <c>frontBmp</c> <i>before</i> <c>:526</c> draws any material into the same
        /// bitmap. So in the original a material's pixels land on top of an overlay that shares its
        /// cell, and the overlay is seen only through the channel the mask carves. The shipped path
        /// does the opposite — it puts the overlay children at <c>baseOrder + 1</c> / <c>+2</c>, above
        /// the tile sprite. See <see cref="OverlayAboveOrder"/> for the third option, which the port used
        /// to default to and no longer does.</para>
        /// </summary>
        public const int OverlayOrder = -100;

        /// <summary>
        /// Gap between the rear pass and a rear overlay, for the same reason: <c>back2</c> collects
        /// the <c>vRear</c> overlays and the rear materials are drawn into it afterwards (<c>:903</c>).
        /// </summary>
        public const int OverlayRearOffset = -100;

        /// <summary>
        /// Sorting order for an overlay when <see cref="Options.overlayAboveMaterials"/> is on — above
        /// the front trio (texture, border, floor) instead of below it.
        ///
        /// <para><b>This is the one place where the port follows Remains-Unity over AS3.</b>
        /// <c>Grafon.as:518</c> blits the overlay <i>before</i> the materials, so in the original the
        /// overlay is under the walls and shows only through the channel the material's mask carves for
        /// it. That works in AS3 because the two are drawn into one bitmap and the mask is the only
        /// thing between them. Remains-Unity, which draws the same art as a per-cell sprite, puts it
        /// <b>above</b> the terrain — <c>Presentation/RoomTerrainView.cs</c>:
        /// <c>FrontOrder=-40, BorderOrder=-30, FloorOrder=-20, FrontSpriteOrder=10</c> — and that is
        /// the only order in which a sprite can be guaranteed visible when the mask underneath it does
        /// not carve.</para>
        ///
        /// <para>12 keeps Remains' gap: its floor is <c>-20</c> and its sprite <c>10</c>, and this
        /// renderer's floor is <see cref="FloorOrder"/> = 2, so 2 + 10 = 12.</para>
        ///
        /// <para><b>The default is now the opposite of this constant, and this constant stays.</b> The
        /// reason given for following Remains was that an overlay underneath is invisible when the mask
        /// above it does not carve a channel — "indistinguishable from the overlay never ran". That was
        /// written while two other defects were making overlays black for unrelated reasons (a lit sprite
        /// material in a preview that culls the scene's light, and a mask fit factor of 0.5 that was never
        /// applied). Both are fixed, so the symptom that justified the deviation no longer occurs, and
        /// what remains is the cost of the deviation: a tread drawn at this order spills over the wall's
        /// edge instead of being occluded by it. AS3 is the oracle, so AS3's order is the default and this
        /// order is kept as the comparison — it is still what Remains-Unity ships.</para>
        /// </summary>
        public const int OverlayAboveOrder = FloorOrder + 10;

        /// <summary>
        /// Sorting order for the <b>rear</b> pass — beams, catwalks and stairs — on
        /// <see cref="MapSortingLayers.BackgroundDecor"/>.
        ///
        /// <para><b>The original draws terrain in three passes, not two.</b> <c>Grafon.as</c> calls
        /// <c>drawKusok(mat, true)</c> over <c>arrFront</c> (line 526), then <c>drawKusok(mat, false)</c>
        /// over <c>arrBack</c> (538), and then — after the backdrop objects and the darkness pass —
        /// <c>drawKusok(mat, false, true)</c> over <c>arrFront</c> again (659). The third call's guard is
        /// <c>if (mat.rear == param2) return;</c> (792), so with <c>param2 = false</c> it draws
        /// <b>exactly the materials whose <c>rear</c> flag is set</b> and skips every other one. Its cell
        /// predicate is <c>front == id &amp;&amp; (param2 || param3)</c> — the <c>front</c> graphic, drawn
        /// into <c>back2</c> and then into <c>backBmp2</c> (661), which sits between <c>visBack</c> and
        /// <c>visFront</c> in the display list (191-196). So a beam is <b>behind the front walls and in
        /// front of the back wall</b>, and it is never the same layer as the wall it hangs in front of.</para>
        ///
        /// <para><c>Material.as:95</c> sets <c>rear</c> for <c>@rear &gt; 0</c> — the six beam materials
        /// (<c>-</c>, <c>Д</c>, <c>Е</c>, <c>К</c>, <c>Н</c>, <c>Р</c>) — and for every <c>ed = 2</c>
        /// background. The port already carries the flag as
        /// <see cref="MaterialRenderEntry.isRear"/>; this renderer used to ignore it, which put beams in
        /// <see cref="MapSortingLayers.MainTiles"/> at the same order as the walls.</para>
        ///
        /// <para>The value sits above <c>RoomBackdropRenderer</c>'s decorations, which use
        /// <c>-1000 - y</c> on the same layer, because the original draws the rear pass <i>after</i> the
        /// backdrop objects into the same bitmap — a beam covers a background tree, not the reverse.</para>
        /// </summary>
        public const int RearOrder = -500;

        [Serializable]
        public struct Options
        {
            public float cellSize;
            public float ambient;
            public bool drawBack;
            public bool drawFront;
            public bool drawBorder;
            public bool drawFloor;

            /// <summary>
            /// Draw the <c>tileFront</c> overlays — ladders, stair treads and the ledge frame.
            ///
            /// <para>Separate from <see cref="drawFront"/> on purpose: an overlay is not a material, it
            /// is a whole 50×110 symbol with its own colours, and the A/B is more informative when the
            /// wall shapes can be compared with the ladders switched off.</para>
            /// </summary>
            public bool drawOverlays;

            /// <summary>
            /// Draw overlays above the front trio instead of under it.
            ///
            /// <para><b>On by default, and it diverges from AS3 on purpose.</b> <c>Grafon.as:518</c>
            /// blits the overlays into <c>frontBmp</c> before <c>:526</c> draws any material, so in the
            /// original they sit under the walls and are visible only through the channel the mask
            /// carves. Remains-Unity — the working reference — draws the same art as a sprite at
            /// <c>FrontSpriteOrder = 10</c>, i.e. <b>above</b> its front terrain. With the overlay
            /// underneath, a mask frame that does not carve a channel leaves the art completely hidden
            /// and the channel black, which is indistinguishable from "the overlay never ran". Turn
            /// this off to reproduce AS3's bitmap order for a like-for-like A/B.</para>
            /// </summary>
            public bool overlayAboveMaterials;

            /// <summary>
            /// How many mask texels to grow each mask outward before using it, closing the seams between
            /// abutting masks. 5 by default, which is the measured border width of the quarter masks.
            /// </summary>
            /// <remarks>
            /// <para><b>The measurement.</b> `clip4906-f1` (maskDirt quarter) covers 97.0 % of its rect with
            /// a border ring averaging 63/255, and its top edge runs alpha `0, 0, 0, 95, 255` across four
            /// texels. Two of those abut at every cell boundary, leaving a ~6-texel band where both are
            /// transparent; the shader multiplies alpha, so the clear colour shows through as a grid of
            /// hairlines one cell apart. Dilating each side by 5 texels makes the border fully opaque
            /// (measured: edge min alpha 0 → 255, coverage 97.0 % → 100 %), so the neighbours overlap.</para>
            ///
            /// <para><b>Why overlapping is not a smear.</b> The material is sampled by <i>world</i>
            /// position, so two masks over one fragment draw the identical material pixel. The overlap is
            /// invisible. That is a property of this renderer and not of masked sprites in general.</para>
            ///
            /// <para><b>The cost.</b> Every silhouette also grows by 5 mask texels — about 1.5 % of a cell
            /// per side on the quarter masks — and its anti-aliased outline becomes a hard edge. Set this
            /// to 0 to see the seams again and judge the trade. A whole-cell mask (<c>TileMask</c>) is
            /// already opaque to its edge, so dilation is a no-op there and the value does not matter.</para>
            /// </remarks>
            public float maskDilateTexels;
            ///
            /// <summary>
            /// Draw nothing for state 0 instead of the mask's frame 1.
            ///
            /// <para><b>A diagnostic switch, not a comparison mode — and its documentation was wrong
            /// twice.</b> The shipped compositor "skips the mask at state 0" by writing the material
            /// through <i>unmasked</i>, i.e. as a plain square. This comment used to claim that this
            /// renderer's frame 1 for state 0 <i>is</i> a plain square for most masks, so drawing it
            /// already reproduced the shipped rule. <b>Measured, it is not.</b> `clip4906-f1` (maskDirt,
            /// the dirt quarter) covers 97.0 % of its rect with a border ring averaging 63/255, and its
            /// top edge runs alpha `0, 0, 0, 95, 255` across four texels — an anti-aliased boundary, not
            /// a square. Two of those abut at every cell boundary, leaving a ~6-texel transparent band,
            /// and because the shader multiplies (`material.a *= mask`) the dark background shows through
            /// as a seam. `clip4917-f1` (maskSimple, a border mask) is 0 % — empty — so for borders
            /// frame 1 already draws nothing.</para>
            ///
            /// <para>So the gap between "frame 1" and "unmasked square" is exactly the mask's inset, and
            /// that inset is where the seams come from. Turning this on leaves those quarters
            /// <b>empty</b> instead, which is a hole in the wall, not a comparison. It is off by default.
            /// Use it to count how much of a room is state 0 — the difference between the two runs is
            /// that area — and turn it back off before judging anything.</para>
            /// </summary>
            public bool skipState0Mask;

            /// <summary>
            /// Multiply the back layer by the room's environment tint, as the shipped path does.
            ///
            /// <para>On by default because <i>not</i> doing it is what makes this renderer look
            /// brighter than the game rather than better. Off is a comparison switch: with it off the
            /// A/B isolates shape and projection, with it on the two sides can be asked to match in
            /// colour as well.</para>
            /// </summary>
            public bool applyBackTint;

            /// <summary>
            /// Multiply the front trio — texture, border, floor — by the room's colour scheme, as the
            /// original does.
            ///
            /// <para><b>This is a deliberate divergence from the shipped path, and the only one in this
            /// renderer that is.</b> <c>Grafon.as:617</c> runs
            /// <c>frontBmp.colorTransform(rect, nloc.cTransform)</c> where
            /// <c>nloc.cTransform = colorFilter(loc.color)</c> (<c>Location.as:650</c>), so every wall in
            /// the original is multiplied by the room's colour. The shipped path passes
            /// <c>Color.white</c> as <c>TileVisualManager</c>'s primary tint and resolves a colour only
            /// for the background layer, so it draws front walls untinted — and
            /// <see cref="ResolveFrontTint"/> reproduces the original, not the shipped path.</para>
            ///
            /// <para>On by default because the authority is AS3, not the shipped path. Turn it off to
            /// compare the two pictures like-for-like: with it on, the A/B will show a colour difference
            /// in every room whose scheme is not white, and that difference is this renderer being
            /// right rather than a bug in either side. <c>black</c> rooms are the loudest case — the
            /// scheme is <c>(0.5, 0.6, 0.7)</c>.</para>
            /// </summary>
            public bool applyFrontTint;

            public static Options Default()
            {
                return new Options
                {
                    cellSize = RoomTerrainGeometry.DefaultCellSize,
                    ambient = 1f,
                    drawBack = true,
                    drawFront = true,
                    drawBorder = true,
                    drawFloor = true,
                    drawOverlays = true,
                    // AS3's order, which is the oracle. See OverlayAboveOrder for why the port used to
                    // default to the Remains order and why that reason expired.
                    overlayAboveMaterials = false,
                    skipState0Mask = false,
                    maskDilateTexels = 5f,
                    applyBackTint = true,
                    applyFrontTint = true
                };
            }
        }

        private readonly Dictionary<string, Tilemap> _layers = new Dictionary<string, Tilemap>(StringComparer.Ordinal);
        private readonly Dictionary<string, Material> _materials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Sprite, Tile> _tiles = new Dictionary<Sprite, Tile>();
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();
        private readonly Dictionary<string, int> _missingMasks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _missingMaterials = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, int> _missingOverlays = new Dictionary<int, int>();

        /// <summary>
        /// Shapes placed per layer key (<c>part:materialId|textureId</c>).
        ///
        /// <para>This exists because "the picture has a thin orange line in it" is not answerable from
        /// the picture. A layer that placed 4 800 shapes when the room has 1 200 cells is drawing
        /// something it should not, and that is a number, not an opinion — see the A/B window's triage
        /// dump.</para>
        /// </summary>
        private readonly Dictionary<string, int> _layerTiles = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// The diagnostic bucket each <see cref="Tilemap"/> feeds, so <see cref="LayerTileCounts"/> can be
        /// recounted from the maps instead of from increments.
        ///
        /// <para><b>Why counting had to move.</b> A region rebuild clears and re-places cells, so an
        /// incrementing counter would climb every time a wall was shot and report more shapes than the
        /// room has. Reading the maps makes the number true by construction: it is what is drawn, not
        /// what was asked for. See <see cref="RecomputeCounts"/>.</para>
        /// </summary>
        private readonly Dictionary<Tilemap, string> _mapBuckets = new Dictionary<Tilemap, string>();

        /// <summary>
        /// Overlay GameObjects per tile cell, so a region rebuild can remove exactly the ones it is
        /// about to redraw. Keyed by <see cref="CellKey"/>.
        ///
        /// <para>Overlays are <see cref="SpriteRenderer"/> children, not Tilemap tiles, so they cannot be
        /// cleared with <c>SetTile(null)</c> and there is no way to ask a GameObject which cell it belongs
        /// to. Without this registry a destroyed ladder would keep drawing on a cell that no longer
        /// exists — the same picture-lying failure the whole subscription exists to prevent.</para>
        /// </summary>
        private readonly Dictionary<long, List<GameObject>> _overlaysByCell = new Dictionary<long, List<GameObject>>();

        // ── What a rebuild needs, kept from Build ────────────────────────────────────────────────
        // Nothing here is a copy of the caller's state: the room is the live object, and the art and
        // material databases are the same instances the first build used.
        private RoomInstance _room;
        private RoomTerrainArt _art;
        private MaterialRenderDatabase _materialDb;
        private Options _options;
        private RoomQuarterStates _states;
        private Color _appliedBackTint = Color.white;

        /// <summary>The union of every mutation region raised since the last flush. Empty when clean.</summary>
        private RectInt _dirtyRegion;

        private bool _hasDirtyRegion;

        private Shader _shader;
        private Transform _overlayRoot;

        /// <summary>Parent for transient per-cell flashes. Created on first use, destroyed by <see cref="Clear"/>.</summary>
        private Transform _flashRoot;

        /// <summary>
        /// The 1×1 white sprite every flash is drawn with. One instance for the whole renderer: a flash
        /// is a coloured quad, and the colour rides on <see cref="SpriteRenderer.color"/>, so there is
        /// nothing per-flash to allocate.
        /// </summary>
        private Sprite _flashSprite;

        /// <summary>The unlit sprite material every overlay is drawn with, built once per build.</summary>
        private Material _overlayMaterial;

        /// <summary>
        /// Shader for the overlay sprites, set explicitly rather than left to the renderer's default.
        ///
        /// <para><b>Why the default is wrong here, verified 2026-10-24.</b> URP is active through
        /// <c>QualitySettings.asset</c> (every quality level points at <c>Settings/UniversalRP.asset</c>),
        /// and <c>Settings/Renderer2D.asset</c> declares <c>m_DefaultMaterialType: 0</c> next to a
        /// separate <c>m_DefaultLitMaterial</c> and <c>m_DefaultUnlitMaterial</c> — so a bare
        /// <see cref="SpriteRenderer"/> takes the <b>lit</b> sprite material. In the game that is fine
        /// because a global <c>Light2D</c> on layer 0 lights it. Anywhere the light does not reach — the
        /// A/B preview camera culls to layer 31 — a lit sprite resolves to <b>pure black</b>: right shape,
        /// right size, no colour. That is exactly how the ladders looked, and how the shipped side of the
        /// A/B looked (rgb 0.01 over 90.9 % coverage).</para>
        ///
        /// <para><b>An earlier version of this comment claimed the opposite</b> — that
        /// <c>LandFonRenderer</c> and <c>RoomBackdropRenderer</c> use the default and draw correctly, so
        /// the default must be unlit. They do draw correctly, but in the <i>game</i>, lit by that global
        /// light; the argument did not transfer to the preview. Both paths now name their shader, and
        /// <c>RoomTerrainPreviewWindow.EnsureShippedUnlitMaterial</c> does the same for the shipped
        /// side.</para>
        /// </summary>
        private Shader ResolveOverlayShader()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                         ?? Shader.Find("Sprites/Default");

            if (shader == null)
            {
                Debug.LogError(
                    "[RoomTerrain] No unlit sprite shader found; overlays (ladders, treads, ledges) " +
                    "would fall back to the renderer's default material.");
            }

            return shader;
        }

        /// <summary>
        /// The shared overlay material. One material for every overlay is enough: the tint rides on
        /// <c>SpriteRenderer.color</c>, not on a per-sprite material property.
        /// </summary>
        private Material OverlayMaterial()
        {
            if (_overlayMaterial != null) return _overlayMaterial;

            Shader shader = ResolveOverlayShader();
            if (shader == null) return null;

            _overlayMaterial = new Material(shader) { name = "RoomTerrain:overlay" };
            _owned.Add(_overlayMaterial);
            return _overlayMaterial;
        }

        /// <summary>
        /// The overlay shader's name, for the A/B triage. A lit shader here is the whole bug: the
        /// sprite keeps its shape and loses its colour.
        /// </summary>
        public string OverlayShaderName
        {
            get
            {
                Material material = _overlayMaterial;
                return material != null && material.shader != null ? material.shader.name : "not built";
            }
        }

        /// <summary>True once <see cref="Build"/> has run and the layers exist.</summary>
        public bool IsBuilt { get; private set; }

        /// <summary>
        /// Mask placements currently drawn. One per quarter, so up to four per cell.
        ///
        /// <para><b>Recounted from the Tilemaps, not incremented.</b> After a region rebuild the number
        /// is still exactly what is on screen — see <see cref="RecomputeCounts"/> for why an incrementing
        /// counter could not stay true across a mutation.</para>
        /// </summary>
        public int ShapesPlaced { get; private set; }

        /// <summary>
        /// Placements that landed on a cell already holding a tile, i.e. that replaced rather than added.
        ///
        /// <para>Zero on a correct build. Four quarters of a cell belong on four different half-cell
        /// positions, so nothing should ever collide; a non-zero count means the mask grid and the
        /// Tilemap grid disagree about how big a cell is, and the picture would be missing three quarters
        /// out of four while <see cref="ShapesPlaced"/> reported all four. That is the difference this
        /// number exists to make visible.</para>
        /// </summary>
        public int OverwrittenShapes { get; private set; }

        /// <summary>
        /// Overlay sprites currently drawn — ladders, stair treads and ledges.
        ///
        /// <para><b>A number here is the only way to tell "no ladders in this room" from "the overlay
        /// pass did not run".</b> 387 of 640 shipped rooms contain a ladder form, so a room with zero
        /// overlays is either unusual or broken, and the presenter prints this count next to the
        /// shapes so the two can be read together.</para>
        ///
        /// <para>Like <see cref="ShapesPlaced"/>, this is counted from what is actually alive after the
        /// last build or region rebuild.</para>
        /// </summary>
        public int OverlaysPlaced { get; private set; }

        /// <summary>
        /// Region rebuilds performed since <see cref="Build"/>. Zero is the interesting value for a
        /// room nobody has shot at.
        /// </summary>
        public int MutationRebuilds { get; private set; }

        /// <summary>Wall-clock milliseconds the last region rebuild took, or 0 if there has not been one.</summary>
        public double LastMutationMilliseconds { get; private set; }

        /// <summary>Cells the last region rebuild cleared and re-placed. The cost driver, so it is reported.</summary>
        public int LastMutationCells { get; private set; }

        /// <summary>
        /// The sorting layer and order the last overlay was given, e.g. <c>MainTiles order 12</c>.
        ///
        /// <para>Printed by the A/B triage because "is the overlay above the wall" is a claim about a
        /// layer and an order together: a large order inside a layer that sorts underneath is still
        /// underneath, which is how the overlay toggle managed to change nothing at all.</para>
        /// </summary>
        public string OverlaySortKey { get; private set; } = "not placed";

        /// <summary>Overlay frame ids the room asked for that the art asset does not carry, with a count.</summary>
        public IReadOnlyDictionary<int, int> MissingOverlays => _missingOverlays;

        /// <summary>Tilemap layers created — the draw-call floor, one per (material, part).</summary>
        public int LayerCount => _layers.Count;

        /// <summary>
        /// How many cells one <see cref="TilemapRenderer"/> chunk covers.
        ///
        /// <para><b>Set so that one layer is one mesh.</b> <c>Mode.Chunk</c> splits a Tilemap into a grid
        /// of chunks of this size, and every chunk is a separate mesh — so the room's submitted renderer
        /// count was <c>layers × chunks</c> and nobody knew the second factor. A 40×30 room is 80×60
        /// half-cells; at Unity's default of 32 that is 3 × 2 = 6 meshes per layer, i.e. the batch count
        /// the HUD reports had no relationship to the number of layers this class logs, and the S3 gate
        /// ("Batches ≤ 60") could not be attributed to anything.</para>
        ///
        /// <para>Making the chunk cover the room collapses that to one mesh per layer, which makes the
        /// two numbers comparable: <c>LayerCount</c> is then the terrain's own contribution to Batches.
        /// The cost is culling granularity, and it is nil here — a room is one screen, so a chunk that
        /// covers it is drawn whether or not the culler would have split it.</para>
        /// </summary>
        public int ChunkSize { get; private set; } = DefaultChunkSize;

        /// <summary>Unity's default, and what this renderer used before <see cref="ChunkSize"/> existed.</summary>
        public const int DefaultChunkSize = 32;

        /// <summary>Meshes one layer contributes at the current <see cref="ChunkSize"/>.</summary>
        public int MeshesPerLayer =>
            _room == null ? 0 : MeshesFor(_room.width, _room.height, ChunkSize);

        /// <summary>
        /// The chunk size that makes one layer one mesh for a room of this size.
        ///
        /// <para>Public and static so the rule can be read without a renderer: it is a pure function of
        /// the room's tile dimensions, and the property that matters — <see cref="MeshesFor"/> answers 1
        /// for every room it is given — is a fixture, not a hope.</para>
        /// </summary>
        public static int ChunkSizeFor(int width, int height)
        {
            // The grid is half-cells, so it is twice the room's tile dimensions.
            return Mathf.NextPowerOfTwo(Mathf.Max(2, Mathf.Max(width, height) * 2));
        }

        /// <summary>How many chunk meshes one layer is split into, at a given chunk size.</summary>
        public static int MeshesFor(int width, int height, int chunkSize)
        {
            if (chunkSize <= 0) return 0;

            int across = Mathf.CeilToInt(width * 2f / chunkSize);
            int down = Mathf.CeilToInt(height * 2f / chunkSize);
            return Mathf.Max(1, across) * Mathf.Max(1, down);
        }


        /// <summary>Milliseconds the last <see cref="Build"/> took, wall clock.</summary>
        public double LastBuildMilliseconds { get; private set; }

        /// <summary>Mask names a material asked for that the art asset does not have, with a count.</summary>
        public IReadOnlyDictionary<string, int> MissingMasks => _missingMasks;

        /// <summary>Material ids with no <see cref="MaterialRenderEntry"/>, with a count.</summary>
        public IReadOnlyDictionary<string, int> MissingMaterials => _missingMaterials;

        /// <summary>Shapes placed per layer key, highest count first when enumerated by the caller.</summary>
        public IReadOnlyDictionary<string, int> LayerTileCounts => _layerTiles;

        /// <summary>
        /// The tint the back layer is drawn with, resolved from the room's environment exactly as
        /// <c>RoomVisualController</c> resolves it for the shipped path.
        ///
        /// <para><b>This is the difference that made the shipped side look unlit.</b> The shipped
        /// renderer hands <c>TileVisualManager</c> <c>ResolveBackgroundTileTint(room)</c> as its
        /// secondary tint, which is the room's colour scheme multiplied by
        /// <c>1 - (170 + environment.darkness) / 255</c> — so the back wall is drawn at about a third
        /// of full brightness even for a room with <c>darkness = 0</c>. A quarter renderer that ignores
        /// it draws the same wall at 100 % and the two pictures cannot be compared: the larger half of
        /// the difference is a missing multiply, not a better shape.</para>
        /// </summary>
        public Color BackTint { get; private set; } = Color.white;

        /// <summary>
        /// The multiplier the front trio is drawn with — the room's colour scheme, or white when
        /// <see cref="Options.applyFrontTint"/> is off.
        ///
        /// <para>Separate from <see cref="BackTint"/> on purpose: the original gives the two passes
        /// different transforms (<c>Grafon.as:617</c> scheme only, <c>:624-634</c> scheme × darkness),
        /// and collapsing them would make a dark room's walls as dark as its backdrop.</para>
        /// </summary>
        public Color FrontTint { get; private set; } = Color.white;

        /// <summary>
        /// Builds the room under this transform.
        ///
        /// <para>Idempotent in the sense that matters: it clears first, so calling it twice leaves one
        /// room, not two overlapping ones.</para>
        /// </summary>
        public void Build(RoomInstance room, RoomTerrainArt art, MaterialRenderDatabase materials, Options options)
        {
            Clear();

            if (room == null) throw new ArgumentNullException(nameof(room));
            if (art == null) throw new ArgumentNullException(nameof(art));
            if (materials == null) throw new ArgumentNullException(nameof(materials));

            // Separate region because the first call of a session compiles the shader's URP variants and
            // is not representative: `room.terrain.quad` measured 660 ms for a room with 69 shapes, and a
            // per-shape cost of 9.5 ms is not a thing this loop can do. Attribution first, then a fix.
            using (Profiler.Region("room.terrain.shader", "first call compiles URP variants — expected one-time per session, not per room"))
            {
                _shader = ResolveShader();
            }
            if (_shader == null)
            {
                Debug.LogError(
                    $"[RoomTerrain] Shader '{ShaderName}' not found (expected at Resources/{ShaderResourcePath}). " +
                    "The room was not built.");
                return;
            }

            if (options.cellSize <= 0f) options.cellSize = RoomTerrainGeometry.DefaultCellSize;

            double started = Time.realtimeSinceStartupAsDouble;

            RoomQuarterStates states;
            using (Profiler.Region("room.terrain.states", "shape layer — O(cells), pure arithmetic"))
            {
                states = BuildStates(room);
            }

            BackTint = ResolveBackTint(room);
            Color backTint = options.applyBackTint ? BackTint : Color.white;

            // Unlike the back, the option is folded into the resolver, so `FrontTint` is already the
            // applied value — white when the toggle is off.
            FrontTint = ResolveFrontTint(room, options.applyFrontTint);

            // A Grid owns the cell size for every Tilemap under it; half a tile per cell is the whole
            // trick — the masks are quarter-tile shapes, so they get a quarter-tile slot each.
            var grid = gameObject.GetComponent<Grid>();
            if (grid == null) grid = gameObject.AddComponent<Grid>();
            float quarter = RoomTerrainGeometry.QuarterSize(options.cellSize);
            grid.cellSize = new Vector3(quarter, quarter, 0f);

            int width = room.width;
            int height = room.height;

            // One chunk per layer — see ChunkSize. Rounded up to a power of two because that is what
            // Unity's own chunking does with the value, and it keeps the number in the log recognisable
            // as "covers the room" rather than as an arbitrary 80 or 60.
            ChunkSize = ChunkSizeFor(width, height);

            // Materials are created lazily inside the loop, so the room's top edge has to be known
            // before the first one: the bitmap fill starts there and runs down.
            RoomTopLocalY = height * options.cellSize;

            // Everything the region rebuild needs, captured before the first cell is drawn so a
            // mutation raised *during* the build (a door stamping its cells, an area trigger) can be
            // serviced on the next frame instead of finding half a room.
            _room = room;
            _art = art;
            _materialDb = materials;
            _options = options;
            _states = states;
            _appliedBackTint = backTint;

            // `place` includes whatever the first material creations cost — see the nested
            // `room.terrain.materials` and `room.terrain.tiles` regions for how much of it is not
            // placement at all.
            using (Profiler.Region("room.terrain.place", "every cell × every pass — materials are created lazily inside this loop"))
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        PlaceCell(x, y);
                    }
                }
            }

            IsBuilt = true;
            LastBuildMilliseconds = (Time.realtimeSinceStartupAsDouble - started) * 1000.0;

            RecomputeCounts();

            // Subscribed last, and only on a successful build: a renderer that never got its shader
            // has nothing to rebuild, and the shipped sprites are still drawing the room.
            room.TilesMutated += OnTilesMutated;
        }

        /// <summary>
        /// Draws one cell from the current <see cref="_states"/> and room data.
        ///
        /// <para><b>Extracted from <see cref="Build"/> so the region rebuild and the first build are the
        /// same code.</b> Two loops that draw a cell — one used at build time and one used after a wall
        /// is shot — is how "the rebuilt cell is slightly different from the built one" gets in, and it
        /// would be invisible until someone compared a screenshot of a fresh room against the same room
        /// after one explosion.</para>
        ///
        /// <para>A null cell (or an out-of-bounds coordinate) draws nothing. The caller is responsible
        /// for having cleared what used to be there — see <see cref="ClearCell"/>.</para>
        /// </summary>
        private void PlaceCell(int x, int y)
        {
            RoomInstance room = _room;
            if (room == null || room.tiles == null) return;
            if (x < 0 || y < 0 || x >= room.width || y >= room.height) return;

            TileData tile = room.tiles[x, y];
            if (tile == null) return;

            // A destroyed tile still carries its graphic strings, and a whole-mask material is placed
            // without ever consulting the shape layer — so this guard, not BuildStates, is what stops a
            // shot-out wall from drawing. See IsDrawn.
            if (!IsDrawn(tile)) return;

            Options options = _options;
            RoomTerrainArt art = _art;
            MaterialRenderDatabase materials = _materialDb;
            RoomQuarterStates states = _states;
            Color backTint = _appliedBackTint;

            float opacity = Mathf.Clamp01(tile.opacity);

            if (options.drawBack)
            {
                string backId = tile.GetBackGraphic();
                if (!string.IsNullOrEmpty(backId))
                {
                    MaterialRenderEntry back = materials.GetBackMaterial(backId);
                    if (back == null)
                    {
                        Count(_missingMaterials, backId);
                    }
                    else
                    {
                        Layer(art, options, back.mainTexture, MaskName(back.mainMask),
                            "back:" + back.id, MapSortingLayers.BackgroundTiles, MainOrder,
                            x, y, tile.heightLevel, opacity, backTint, q => states.Back(x, y, q), Part.Back);
                    }
                }
            }

            // Overlays are neither front nor back material art, so they are placed before the
            // `drawFront` gate: a ladder stands in air and has no front graphic at all, which
            // is precisely the case that gate would drop.
            if (options.drawOverlays)
            {
                PlaceOverlay(art, options, tile.visualId, tile.vidRear, x, y);
                PlaceOverlay(art, options, tile.visualId2, tile.vid2Rear, x, y);
            }

            if (!options.drawFront) return;

            string frontId = tile.GetFrontGraphic();
            if (string.IsNullOrEmpty(frontId)) return;

            MaterialRenderEntry front = materials.GetFrontMaterial(frontId);
            if (front == null)
            {
                Count(_missingMaterials, frontId);
                return;
            }

            // A rear material is a beam or a staircase: the original draws it in its own pass,
            // between the back wall and the front wall, and reads its mask frames from the
            // *background* contours (`setMCT` is called with `param2 = false`, which selects
            // `pont`). Drawing it as a front material — which is what this renderer did — puts
            // it in the same Tilemap-sorting competition as the walls it should be behind.
            bool rear = front.isRear;
            string sortingLayer = rear ? MapSortingLayers.BackgroundDecor : MapSortingLayers.MainTiles;
            int baseOrder = rear ? RearOrder : MainOrder;
            string partPrefix = rear ? "rear:" : "main:";
            Func<RoomQuarter, int> state = rear
                ? (Func<RoomQuarter, int>)(q => BackStateFor(states, x, y, q))
                : q => states.Front(x, y, q);
            Part mainPart = rear ? Part.Rear : Part.Main;

            // `Grafon.as:617` multiplies the whole front bitmap by the room's colour scheme,
            // and the rear pass lands in `backBmp2`, which gets the same `cTransform` at `:661`.
            // So both get the scheme; only the back layer gets the darkness term on top.
            Layer(art, options, front.mainTexture, MaskName(front.mainMask),
                partPrefix + front.id, sortingLayer, baseOrder,
                x, y, tile.heightLevel, opacity, FrontTint, state, mainPart);

            if (options.drawBorder && front.HasBorder)
            {
                Layer(art, options, front.borderTexture, MaskName(front.borderMask),
                    partPrefix + front.id, sortingLayer, baseOrder + BorderOrder,
                    x, y, tile.heightLevel, opacity, FrontTint, state, Part.Border);
            }

            if (options.drawFloor && front.HasFloor)
            {
                Layer(art, options, front.floorTexture, MaskName(front.floorMask),
                    partPrefix + front.id, sortingLayer, baseOrder + FloorOrder,
                    x, y, tile.heightLevel, opacity, FrontTint, state, Part.Floor);
            }
        }

        /// <summary>
        /// Removes everything drawn for one cell: the 2×2 half-cell block in every layer, plus any
        /// overlay sprites registered against it.
        ///
        /// <para><b>The half-cell block is derivable, so nothing is bookkept.</b> A tile at (x, y) writes
        /// half-cells <c>2x..2x+1</c> × <c>2y..2y+1</c> — see
        /// <see cref="RoomTerrainGeometry.QuarterCellX"/> and <see cref="RoomTerrainGeometry.Whole"/>,
        /// which both anchor at <c>2x, 2y</c>. Clearing the block therefore cannot miss a placement, and
        /// does not need to know which layers the cell happened to use.</para>
        ///
        /// <para><b>The guard is not an optimisation.</b> <c>SetTile(null)</c> on an already-empty cell
        /// still marks the map dirty and forces a mesh rebuild, and a region rebuild touches every layer
        /// for every dirty cell — most of which that layer never drew.</para>
        /// </summary>
        private void ClearCell(int x, int y)
        {
            int cellX = x * 2;
            int cellY = y * 2;

            foreach (Tilemap map in _layers.Values)
            {
                if (map == null) continue;

                ClearHalfCell(map, cellX, cellY);
                ClearHalfCell(map, cellX + 1, cellY);
                ClearHalfCell(map, cellX, cellY + 1);
                ClearHalfCell(map, cellX + 1, cellY + 1);
            }

            long key = CellKey(x, y);
            if (!_overlaysByCell.TryGetValue(key, out List<GameObject> overlays)) return;

            for (int i = 0; i < overlays.Count; i++)
            {
                GameObject overlay = overlays[i];
                if (overlay == null) continue;

                // Deactivated before it is destroyed: Destroy is deferred to the end of the frame, and a
                // ladder still rendering for one frame after the wall it hung on is gone is exactly the
                // picture-lying failure this subscription exists to prevent.
                overlay.SetActive(false);
                DestroyObject(overlay);
            }

            overlays.Clear();
        }

        private static void ClearHalfCell(Tilemap map, int cellX, int cellY)
        {
            var cell = new Vector3Int(cellX, cellY, 0);
            if (map.GetTile(cell) != null) map.SetTile(cell, null);
        }

        /// <summary>Stable key for a tile cell, for the overlay registry.</summary>
        private static long CellKey(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        /// <summary>
        /// A tile changed underneath the picture. Accumulates the region; the work happens once per
        /// frame in <see cref="LateUpdate"/>.
        ///
        /// <para><b>Why the region is coalesced rather than serviced immediately.</b> One explosion
        /// destroys dozens of tiles, and <c>TileCollider</c> raises one notification per destroyed tile,
        /// each carrying its own 3×3 border. Rebuilding per notification would rebuild the same
        /// neighbourhood dozens of times inside a single frame; the union of the regions is what actually
        /// changed. This is the same shape as <c>PhysicsWorldService.RebuildRegion</c>, which also
        /// accepts a region and does more work than the region strictly requires.</para>
        /// </summary>
        private void OnTilesMutated(RoomInstance room, RectInt region)
        {
            // Only the room this renderer drew. A background-layer room has its own instance and its own
            // event, and a stale subscription on a released room would resurrect shapes for a room that
            // is no longer on screen.
            if (!ReferenceEquals(room, _room)) return;

            _dirtyRegion = _hasDirtyRegion ? Union(_dirtyRegion, region) : region;
            _hasDirtyRegion = true;
        }

        private void LateUpdate()
        {
            if (_hasDirtyRegion) FlushMutations();
        }

        /// <summary>
        /// Redraws every cell the accumulated region covers.
        ///
        /// <para><b>What is incremental and what is not.</b> The <see cref="RoomQuarterStates"/> grid is
        /// recomputed wholesale: it is a function of the whole room's solidity, it costs O(cells) of byte
        /// writes (1 200 cells for a 40×30 room), and narrowing it would be a second incremental algorithm
        /// with its own way to be subtly wrong for no measurable gain. The expensive half — Tilemap writes,
        /// material lookups, overlay GameObjects — is what the region narrows.</para>
        ///
        /// <para><b>Two passes, clear then place.</b> Placing can create a new <see cref="Tilemap"/> (a
        /// material the room did not contain before, e.g. a door's stamped graphic), and mutating
        /// <see cref="_layers"/> while iterating it would throw in the middle of a frame. Clearing
        /// everything first also keeps <see cref="OverwrittenShapes"/> honest: a placement into a cell
        /// that was just cleared is not an overwrite.</para>
        /// </summary>
        private void FlushMutations()
        {
            _hasDirtyRegion = false;

            RectInt region = _dirtyRegion;
            _dirtyRegion = default;

            if (!IsBuilt || _room == null) return;

            RectInt clamped = ClampToRoom(region, _room.width, _room.height);
            if (clamped.width <= 0 || clamped.height <= 0) return;

            double started = Time.realtimeSinceStartupAsDouble;

            _states = BuildStates(_room);

            for (int y = clamped.yMin; y < clamped.yMax; y++)
                for (int x = clamped.xMin; x < clamped.xMax; x++)
                    ClearCell(x, y);

            for (int y = clamped.yMin; y < clamped.yMax; y++)
                for (int x = clamped.xMin; x < clamped.xMax; x++)
                    PlaceCell(x, y);

            RecomputeCounts();

            LastMutationCells = clamped.width * clamped.height;
            MutationRebuilds++;
            LastMutationMilliseconds = (Time.realtimeSinceStartupAsDouble - started) * 1000.0;
        }

        /// <summary>Smallest rectangle covering both. An empty rectangle contributes nothing.</summary>
        public static RectInt Union(RectInt a, RectInt b)
        {
            if (a.width <= 0 || a.height <= 0) return b;
            if (b.width <= 0 || b.height <= 0) return a;

            int xMin = Mathf.Min(a.xMin, b.xMin);
            int yMin = Mathf.Min(a.yMin, b.yMin);
            int xMax = Mathf.Max(a.xMax, b.xMax);
            int yMax = Mathf.Max(a.yMax, b.yMax);

            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        /// <summary>
        /// Intersects a tile region with the room. A notification may name cells outside the room — the
        /// 3×3 border of a tile on the edge does — and iterating those would index the tile array out of
        /// bounds.
        /// </summary>
        public static RectInt ClampToRoom(RectInt region, int width, int height)
        {
            int xMin = Mathf.Max(0, region.xMin);
            int yMin = Mathf.Max(0, region.yMin);
            int xMax = Mathf.Min(width, region.xMax);
            int yMax = Mathf.Min(height, region.yMax);

            if (xMax <= xMin || yMax <= yMin) return default;

            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        /// <summary>
        /// Recounts <see cref="ShapesPlaced"/>, <see cref="OverlaysPlaced"/> and
        /// <see cref="LayerTileCounts"/> from what is actually drawn.
        ///
        /// <para>Called after a build and after every region rebuild. A region rebuild clears cells it is
        /// about to redraw, so any counter that was incremented at placement time would have to be
        /// decremented by exactly the right amount at clear time — and getting that wrong is silent. The
        /// maps already know the answer.</para>
        /// </summary>
        private void RecomputeCounts()
        {
            ShapesPlaced = 0;
            _layerTiles.Clear();

            foreach (KeyValuePair<string, Tilemap> entry in _layers)
            {
                Tilemap map = entry.Value;
                if (map == null) continue;

                int used = map.GetUsedTilesCount();
                if (used <= 0) continue;

                string bucket = _mapBuckets.TryGetValue(map, out string mapped) ? mapped : entry.Key;

                _layerTiles.TryGetValue(bucket, out int previous);
                _layerTiles[bucket] = previous + used;

                ShapesPlaced += used;
            }

            int overlays = 0;
            foreach (KeyValuePair<long, List<GameObject>> entry in _overlaysByCell)
            {
                List<GameObject> list = entry.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] != null) overlays++;
                }
            }

            OverlaysPlaced = overlays;
        }

        /// <summary>
        /// Whether a cell draws anything at all. <b>The one definition of "drawn" in this renderer.</b>
        ///
        /// <para><b>Why this is not "has a graphic".</b> <see cref="TileData.Destroy"/> clears
        /// <c>phis</c>, <c>opac</c> and the visual ids, but it leaves <c>frontGraphic</c> and
        /// <c>backGraphic</c> in place — and this renderer resolves its masks from those two strings.
        /// A test written as "has art" therefore answers <c>true</c> for a wall the player has already
        /// shot out. The cell survived that only because the layer colour's alpha is the tile's
        /// <c>opacity</c>, which <c>Destroy</c> zeroes, so the mask was placed and drawn at alpha 0:
        /// right picture, by accident. Anything that restores <c>opacity</c> — <c>Doorcarver</c> sets it
        /// to 1 — puts the wall back. <see cref="TileData.IsDestroyed"/> is the state the shipped path
        /// already uses (<c>TileVisualManager.UpdateTile</c>), so this is that same answer, asked once
        /// and answered in one place.</para>
        ///
        /// <para><b>What it gates.</b> <see cref="PlaceCell"/> returns immediately, and every predicate
        /// <see cref="BuildStates"/> hands to <see cref="RoomQuarterStates.Compute"/> treats the cell as
        /// absent — so a destroyed tile also stops propping up its neighbours' edges. Both halves matter:
        /// without the first a whole-mask material still draws, and without the second the tile above it
        /// keeps a straight bottom edge where there is now nothing.</para>
        /// </summary>
        public static bool IsDrawn(TileData tile)
        {
            return tile != null && !tile.IsDestroyed();
        }

        /// <summary>
        /// The shape layer for a room, from the room's own grid.
        ///
        /// <para>Backgrounds are identified by a small integer so the back layer can ask "is that
        /// neighbour the same surface as me" without comparing strings 4 800 times. The dictionary is
        /// built per room, so two rooms never share an id space and the comparison stays local.</para>
        /// </summary>
        public static RoomQuarterStates BuildStates(RoomInstance room)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));

            var backIds = new Dictionary<string, int>(StringComparer.Ordinal);
            int nextBackId = 1;

            // Every predicate below answers for a *drawn* cell, so all four start from IsDrawn. A
            // destroyed tile is not solid, has no background, is not a shelf and draws no front — and
            // that last one is not cosmetic: the renderer reads the graphic *strings*, which
            // TileData.Destroy leaves behind, so without this the cell kept its mask and only the
            // colour alpha hid it. See IsDrawn for the whole failure.
            bool IsSolid(int x, int y)
            {
                TileData tile = room.tiles[x, y];
                return IsDrawn(tile) && tile.physicsType == TilePhysicsType.Wall;
            }

            int BackId(int x, int y)
            {
                TileData tile = room.tiles[x, y];
                if (!IsDrawn(tile)) return 0;

                string graphic = tile.GetBackGraphic();
                if (string.IsNullOrEmpty(graphic)) return 0;

                if (!backIds.TryGetValue(graphic, out int id))
                {
                    id = nextBackId++;
                    backIds[graphic] = id;
                }

                return id;
            }

            bool IsShelf(int x, int y)
            {
                TileData tile = room.tiles[x, y];
                return IsDrawn(tile) && (tile.isLedge || tile.physicsType == TilePhysicsType.Platform);
            }

            // A cell draws a front whenever it carries a front graphic — walls, but also the platforms
            // and beams that are not solid. Without this the front array says NotDrawn for every
            // non-wall cell and the renderer places nothing there, which is how a room's catwalks went
            // missing while its walls drew correctly.
            bool HasFront(int x, int y)
            {
                TileData tile = room.tiles[x, y];
                return IsDrawn(tile) && !string.IsNullOrEmpty(tile.GetFrontGraphic());
            }

            return RoomQuarterStates.Compute(
                room.width, room.height, IsSolid, BackId, IsShelf, skyBackdrop: IsSkyBackdrop(room),
                hasFront: HasFront);
        }

        /// <summary>
        /// <c>sky</c> is the one backdrop value with behaviour rather than art: in the original it
        /// means "no backdrop, and every background joins every other into one silhouette". It is a
        /// room option rather than a tile property, so it is read from the room's environment record —
        /// <c>RoomEnvironment.hasSky</c>, which the room importer fills from <c>backwall="sky"</c>.
        /// </summary>
        private static bool IsSkyBackdrop(RoomInstance room)
        {
            return room.environment != null && room.environment.hasSky;
        }

        private static string MaskName(string maskName)
        {
            return string.IsNullOrWhiteSpace(maskName) ? DefaultMaskName : maskName;
        }

        /// <summary>
        /// How much to scale one mask sprite so it fits its slot, from the sprite's own import PPU and
        /// the clip's export zoom.
        ///
        /// <para><b>Every mask arrives at PPU 400 and every slot wants 800.</b> The export is rasterised
        /// at 50 px/m × zoom, and this project's cell is 0.4 units at 100 px/unit, so the art is twice
        /// the size its slot needs. <see cref="RoomTerrainGeometry.SpriteScale"/> derives the 0.5 from
        /// the manifest's <c>cellPixels</c> and the clip's <c>rasterZoom</c> instead of trusting the
        /// <c>.meta</c>, which keeps the art a verbatim copy of the export.</para>
        ///
        /// <para>Reading the zoom from the <i>clip</i> rather than assuming 8 is what lets a clip
        /// exported at a different zoom still land correctly; a missing frame set falls through to 1 so
        /// the caller draws something at the size the sprite already claims rather than nothing.</para>
        /// </summary>
        private static float MaskScaleFor(RoomTerrainArt art, Sprite sprite, int clip, float cellSize)
        {
            if (art == null || sprite == null) return 1f;

            RoomTerrainArt.FrameSet set = art.GetFrameSet(clip);
            double zoom = set != null ? set.rasterZoom : 0d;

            return RoomTerrainGeometry.SpriteScale(
                sprite.pixelsPerUnit, cellSize, art.CellPixels, zoom);
        }

        private static void Count(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int existing);
            counts[key] = existing + 1;
        }

        private static void Count(Dictionary<int, int> counts, int key)
        {
            counts.TryGetValue(key, out int existing);
            counts[key] = existing + 1;
        }

        /// <summary>
        /// Places one <c>tileFront</c> overlay — a ladder, a stair tread, or the ledge frame on top of
        /// a stair run.
        ///
        /// <para><b>Why a <see cref="SpriteRenderer"/> and not a Tilemap tile.</b> Every other shape in
        /// this renderer is a stencil: a mask frame on a half-cell Tilemap, coloured by a material
        /// texture sampled in world space. An overlay is the opposite — it is the whole picture, with
        /// its own colours, its own alpha and its own size. Putting it through the mask shader would
        /// draw the <i>material</i> through a ladder-shaped stencil, which is a ladder-shaped hole in a
        /// wall. So it gets its own renderer, exactly as the original gives it its own
        /// <c>tileFront</c> child.</para>
        ///
        /// <para><b>Placement: the symbol's registration point on the cell's top-left corner.</b> The
        /// frames are 50×110 pixels on a 40 px grid, so a ladder is 1.25 cells wide and 2.75 tall and
        /// must overhang its cell — that overhang is the art, not an error. The registration point is
        /// <c>(5, 30)</c> px inside that canvas, which is why the sprite is not simply put on the cell
        /// corner; see <see cref="RoomTerrainArt.OverlayOriginXPixels"/>. <b>No <c>heightLevel</c>
        /// squash and no <c>opacity</c>:</b> <c>Grafon.as</c> never reads <c>Tile.opac</c> for this
        /// child and never scales it, so a raised cell's ladder is the same size as a flat one's.</para>
        /// </summary>
        private void PlaceOverlay(
            RoomTerrainArt art,
            Options options,
            int visualId,
            bool rear,
            int x,
            int y)
        {
            if (visualId <= 0) return;

            Sprite sprite = art.Overlay(visualId);
            if (sprite == null)
            {
                Count(_missingOverlays, visualId);
                return;
            }

            float cellSize = options.cellSize;

            var go = new GameObject("Overlay: tile_" + visualId);
            go.transform.SetParent(OverlayRoot(), false);

            // The symbol's registration point — (5, 30) px inside the 50×110 canvas — goes on the
            // cell's top-left corner. See RoomTerrainArt.OverlayOriginXPixels for where the number
            // comes from and RoomTerrainGeometry.Overlay for the arithmetic.
            //
            // Size comes from `sprite.rect` — the canvas in SOURCE PIXELS — and is converted with the
            // same `pixelsToWorld` as the registration point. `sprite.bounds.size` would be the wrong
            // input: it is the canvas already divided by the importer's `spritePixelsToUnits` (100 for
            // these frames), so it only agrees with `cellSize / CellPixels` while those two happen to
            // cancel. At the default 0.4 / 40 they do (both 0.01); at any other cell size the sprite
            // would keep a fixed world size while the cell changed, and the two numbers in this
            // expression would be in different units. See RoomTerrainGeometry.OverlaySize.
            float pixelsToWorld = art.CellPixels > 0 ? cellSize / art.CellPixels : 0.01f;
            Vector2 sourcePixels = sprite.rect.size;
            Vector2 size = RoomTerrainGeometry.OverlaySize(
                sourcePixels.x, sourcePixels.y, cellSize, art.CellPixels);
            go.transform.localPosition = RoomTerrainGeometry.Overlay(
                x, y, cellSize, size.x, size.y,
                RoomTerrainArt.OverlayOriginXPixels * pixelsToWorld,
                RoomTerrainArt.OverlayOriginYPixels * pixelsToWorld);

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;

            // Explicitly unlit, so the overlay art does not depend on what the pipeline asset hands a
            // bare SpriteRenderer. That default is Sprite-Lit-Default here (Renderer2D.asset has
            // m_DefaultMaterialType 0, and 0 is Lit), and a lit sprite draws (0,0,0) wherever no light
            // reaches it — which in this project is every camera that is not looking at SampleScene's
            // Global Light2D. Black at the right shape is indistinguishable from "the art is wrong" in
            // a screenshot. See ResolveOverlayShader.
            renderer.sharedMaterial = OverlayMaterial();

            // "Above the walls" has to mean above them in the *layer* order, not merely a larger order
            // number inside the rear layer — see RoomTerrainGeometry.OverlaySort for the whole reason.
            RoomTerrainGeometry.OverlaySort(
                rear, options.overlayAboveMaterials, out string overlayLayer, out int overlayOrder);

            renderer.sortingLayerName = overlayLayer;
            renderer.sortingOrder = overlayOrder;

            OverlaySortKey = overlayLayer + " order " + overlayOrder +
                             (rear ? " (rear form)" : string.Empty);

            // `frontBmp` and `backBmp2` both take the room's colour scheme after the overlay was
            // blitted into them (`Grafon.as:617`, `:661`), so the overlay is tinted like the walls.
            renderer.color = FrontTint;

            // Registered against its cell so a region rebuild can destroy exactly this one. The
            // registry replaces the old `OverlaysPlaced++` as the count's source: a rebuild destroys
            // and re-creates overlays, and a counter that only ever climbed would report more ladders
            // than the room has after the first wall is shot.
            long key = CellKey(x, y);
            if (!_overlaysByCell.TryGetValue(key, out List<GameObject> cellOverlays))
            {
                cellOverlays = new List<GameObject>(2);
                _overlaysByCell[key] = cellOverlays;
            }

            cellOverlays.Add(go);
        }

        /// <summary>
        /// The parent for every overlay renderer, created on first use.
        ///
        /// <para>One child object rather than one per overlay so <see cref="Clear"/> has a single thing
        /// to destroy and the hierarchy says what the group is. The renderers are not Tilemap tiles,
        /// so they cannot live under a layer.</para>
        /// </summary>
        private Transform OverlayRoot()
        {
            if (_overlayRoot != null) return _overlayRoot;

            var go = new GameObject("Overlays");
            go.transform.SetParent(transform, false);
            _overlayRoot = go.transform;
            _owned.Add(go);
            return _overlayRoot;
        }

        /// <summary>
        /// Flashes one cell — the quarter renderer's answer to <c>TileVisualManager.FlashTile</c>.
        ///
        /// <para><b>Why this cannot be the shipped mechanism.</b> A shipped tile flash replaces the
        /// colour of that tile's <see cref="SpriteRenderer"/>. Here the terrain is a mesh whose pixels
        /// come from a material sampled in world space, and the only per-cell channel a Tilemap leaves
        /// open is <c>Tilemap.SetColor</c> — which is a <i>tint of the material</i>, not a flat colour,
        /// and would flash the mask's silhouette rather than the cell. So the flash is drawn as its own
        /// quad over the cell, which is also what makes it independent of which layers that cell
        /// happened to use.</para>
        ///
        /// <para><b>Order and lifetime.</b> Above the front trio (<see cref="FloorOrder"/>) so a wall
        /// reads as hit, below the overlays' above-materials order so a ladder is not covered. The quad
        /// is destroyed when the flash ends; nothing accumulates. Play mode only, because the lifetime is
        /// a coroutine and <c>StartCoroutine</c> does not run in edit mode — a flash is a runtime effect,
        /// so doing nothing is the honest answer there rather than a quad left on screen forever.</para>
        /// </summary>
        /// <returns>True when a flash was drawn.</returns>
        public bool FlashCell(Vector2Int coord, Color color, float duration = 0.1f)
        {
            if (!IsBuilt || _room == null || !Application.isPlaying) return false;
            if (coord.x < 0 || coord.y < 0 || coord.x >= _room.width || coord.y >= _room.height) return false;

            float cellSize = _options.cellSize > 0f ? _options.cellSize : RoomTerrainGeometry.DefaultCellSize;

            var go = new GameObject($"Flash_{coord.x}_{coord.y}");
            go.transform.SetParent(FlashRoot(), false);

            // Cell centre, from the same helper the overlays use: `Overlay(x, y, cell, cell, cell, 0, 0)`
            // is `((x + 0.5) * cell, (y + 0.5) * cell)`. Reusing it rather than writing the arithmetic
            // again is deliberate — that expression is already pinned by the geometry fixtures.
            go.transform.localPosition = RoomTerrainGeometry.Overlay(
                coord.x, coord.y, cellSize, cellSize, cellSize, 0f, 0f);

            // The sprite is 1×1 world unit at scale 1 (1 px texture at 1 px/unit), so the object's scale
            // is the cell. Scale, not sprite size: the sprite is shared by every flash.
            go.transform.localScale = new Vector3(cellSize, cellSize, 1f);

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = FlashSprite();
            renderer.sharedMaterial = OverlayMaterial();
            renderer.sortingLayerName = MapSortingLayers.MainTiles;
            renderer.sortingOrder = MainOrder + FloorOrder + 1;
            renderer.color = color;

            StartCoroutine(EndFlashAfter(go, duration));
            return true;
        }

        private System.Collections.IEnumerator EndFlashAfter(GameObject flash, float duration)
        {
            if (duration > 0f) yield return new WaitForSeconds(duration);

            if (flash == null) yield break;

            // Deactivated first for the same reason an overlay is: `Destroy` is deferred to the end of
            // the frame, and a flash that outlives its duration by a frame is a flash with the wrong
            // length — which is exactly the kind of thing a damage readout is read against.
            flash.SetActive(false);
            DestroyObject(flash);
        }

        private Sprite FlashSprite()
        {
            if (_flashSprite != null) return _flashSprite;

            _flashSprite = Sprite.Create(
                Texture2D.whiteTexture,
                new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f),
                1f);

            _flashSprite.name = "RoomTerrain:flash";
            _owned.Add(_flashSprite);
            return _flashSprite;
        }

        private Transform FlashRoot()
        {
            if (_flashRoot != null) return _flashRoot;

            var go = new GameObject("Flashes");
            go.transform.SetParent(transform, false);
            _flashRoot = go.transform;
            _owned.Add(go);
            return _flashRoot;
        }

        /// <summary>
        /// One layer of one material part: resolves the mask, places every quarter that has a state,
        /// and records what it could not find.
        /// </summary>
        private void Layer(
            RoomTerrainArt art,
            Options options,
            string textureId,
            string maskName,
            string layerName,
            string sortingLayer,
            int sortingOrder,
            int x,
            int y,
            int heightLevel,
            float opacity,
            Color tint,
            Func<RoomQuarter, int> state,
            Part part)
        {
            if (string.IsNullOrEmpty(textureId)) return;

            RoomTerrainArt.MaskEntry mask = art.GetMask(maskName);
            if (mask == null)
            {
                Count(_missingMasks, maskName);
                return;
            }

            // The bucket has to name the *part*, not just the pass and the form. A material can point its
            // main, border and floor slots at the same texture id, and the map cache below keys on this
            // name — so without the part, a border that shares its main's texture lands in the main's
            // Tilemap and its sorting order is silently dropped by the cache hit. It also made the
            // per-layer dump unreadable: one bucket summed three passes, so its count could exceed the
            // population it was supposedly drawn on and the arithmetic looked impossible.
            string bucket = layerName + "/" + part;

            Tilemap map = LayerMap(
                bucket, textureId, sortingLayer, sortingOrder, art, options, MaskDilateUV(art, mask, options));

            float cellSize = options.cellSize;

            // `Grafon.as:848` squashes a raised tile only when `param2` — the front pass — is true, and
            // `:876` drops the floor strip by `zForm / 4` unconditionally. The back and rear passes run
            // with `param2 = false`, so a beam on a step is drawn at full height in its own square, and
            // the wall behind a step keeps its full height — which is what makes the step read as one.
            bool squashed = part == Part.Main || part == Part.Border;
            float heightScale = squashed ? RoomTerrainGeometry.HeightScale(heightLevel) : 1f;
            float drop = part == Part.Floor
                ? RoomTerrainGeometry.HeightDrop(heightLevel, cellSize)
                : 0f;

            if (mask.IsWhole)
            {
                Sprite whole = art.Frame(mask.clip, RoomQuarterRules.Inside);
                if (whole == null)
                {
                    Count(_missingMasks, maskName);
                    return;
                }

                Place(map, whole,
                    RoomTerrainGeometry.Scaled(
                        RoomTerrainGeometry.Whole(x, y, cellSize, heightScale, drop),
                        MaskScaleFor(art, whole, mask.clip, cellSize)), opacity, tint);
                return;
            }

            if (mask.quarters == null) return;

            foreach (RoomTerrainArt.QuarterEntry quarter in mask.quarters)
            {
                if (quarter == null) continue;

                int edge = state(quarter.quarter);
                if (edge < 0) continue;
                if (edge == RoomQuarterRules.Inside && options.skipState0Mask) continue;

                Sprite sprite = art.Frame(quarter.clip, edge);
                if (sprite == null)
                {
                    Count(_missingMasks, maskName);
                    continue;
                }

                RoomTerrainPlacement placement = RoomTerrainGeometry.Quarter(
                    quarter.quarter, x, y, cellSize, heightScale, drop, quarter.flipX, quarter.flipY);

                Place(map, sprite,
                    RoomTerrainGeometry.Scaled(
                        placement, MaskScaleFor(art, sprite, quarter.clip, cellSize)), opacity, tint);
            }
        }

        private void Place(
            Tilemap map,
            Sprite sprite,
            RoomTerrainPlacement placement,
            float opacity,
            Color tint)
        {
            Tile tile = TileFor(sprite);
            if (tile == null) return;

            // A Tilemap holds exactly one tile per cell, so a second placement into the same cell does
            // not draw twice — it replaces. Counting placements alone therefore cannot tell "the mask
            // legitimately split this cell into four quarters" from "the grid is the wrong size and the
            // quarters are landing on top of each other"; both report four. Counting the replacements
            // separates them: a correct half-cell grid never replaces anything, because the four quarters
            // of a cell are four different cells.
            if (map.GetTile(placement.Cell) != null) OverwrittenShapes++;

            map.SetTile(placement.Cell, tile);

            // None, or the transform and the colour stay locked at whatever the tile asset carries and
            // both writes below are silently ignored — the mask lands unflipped at the cell's origin.
            map.SetTileFlags(placement.Cell, TileFlags.None);
            map.SetTransformMatrix(placement.Cell, placement.ToMatrix());

            // The per-cell colour is the only per-layer channel a shared material leaves open: the
            // material is keyed by texture, and the same texture can serve a front and a back layer, so
            // a tint set on the material would darken both. Alpha comes from the tile's own opacity, not
            // from the tint — `tint * opacity` would multiply the two alphas and make a dark back wall
            // translucent, which is the kind of defect that reads as "the backdrop shows through".
            //
            // Written unconditionally, including when it is plain white. `SetTile` writes the tile, not
            // the cell's colour — they are separate fields — so skipping the white case leaves whatever
            // colour the cell held before. A region rebuild writes the same cell a second time, and a
            // tile's opacity can change in between: a raised tile is decoded at `opac = 0`
            // (`TileDecoder.SetZForm`), and `Doorcarver` sets `opacity = 1` on the cells it carves. With
            // the guard, that cell keeps alpha 0 and the door draws as a hole.
            var colour = new Color(tint.r, tint.g, tint.b, opacity);
            map.SetColor(placement.Cell, colour);
        }

        /// <summary>
        /// Which of the original's passes a shape belongs to. The pass decides the height transform,
        /// because <c>Grafon.as</c> gates the <c>zForm</c> squash on it (<c>:848</c>: <c>if (zForm &amp;&amp;
        /// param2)</c>) and drops the floor strip separately (<c>:876</c>).
        ///
        /// <para><b>Only the front pass squashes.</b> A raised tile's front is drawn
        /// <c>1 - heightLevel/4</c> tall, anchored at the cell's bottom, which is what makes a step read
        /// as a step. The back wall behind that tile is <i>not</i> squashed — that is the point of a
        /// raised platform: you see more of the wall behind it. Squashing the back as well shortens the
        /// panel and leaves a gap above the step, which reads as a missing wall rather than a height.</para>
        /// </summary>
        private enum Part
        {
            /// <summary>Front pass, main texture: squashed by <c>heightLevel</c>.</summary>
            Main,

            /// <summary>Front pass, border texture: squashed by <c>heightLevel</c>.</summary>
            Border,

            /// <summary>Floor strip: never squashed, moved down by <c>heightLevel/4</c> of a cell.</summary>
            Floor,

            /// <summary>Background pass (<c>back</c> graphics): full height, no drop.</summary>
            Back,

            /// <summary>Rear pass (beams, catwalks, stairs): full height, no drop.</summary>
            Rear
        }

        /// <summary>
        /// The state a rear material's mask frames are cut from.
        ///
        /// <para><c>drawKusok</c> calls <c>setMCT(_loc5_, _loc4_, param2)</c> and <c>setMCT</c> picks
        /// <c>kont</c> when <c>param3</c> is true and <c>pont</c> otherwise. The rear pass passes
        /// <c>param2 = false</c>, so a beam is cut from the <b>background</b> contours, not the solid
        /// ones — which is also why <c>uslBontur</c> counts a shelf as "same background": the beam's own
        /// neighbours must not cut it.</para>
        ///
        /// <para>A rear cell with no background of its own has no back state, and <c>Inside</c> is the
        /// right answer rather than an invention: with <c>back = ""</c>, <c>uslBontur</c> returns true
        /// for every empty neighbour and for every wall, so the pont is a plain square. The fallback
        /// also keeps this from silently dropping the beam, which is the failure mode that matters.</para>
        /// </summary>
        private static int BackStateFor(RoomQuarterStates states, int x, int y, RoomQuarter quarter)
        {
            int edge = states.Back(x, y, quarter);
            return edge < 0 ? RoomQuarterRules.Inside : edge;
        }

        /// <summary>
        /// The back-layer tint, from the same resolver the shipped path uses, so a difference in the
        /// A/B is a difference in drawing and not a difference in colour maths.
        /// </summary>
        private static Color ResolveBackTint(RoomInstance room)
        {
            if (room == null || room.environment == null) return Color.white;

            return RoomBackdropRenderer.ResolveBackgroundTileTint(room.environment);
        }

        /// <summary>
        /// The front-layer multiplier: the room's colour scheme, with no darkness term.
        ///
        /// <para><b>Why <c>colorScheme</c> and not <c>backgroundColorScheme</c>.</b> The back resolver
        /// prefers <c>backgroundColorScheme</c> when a room overrides its backdrop separately. The front
        /// is not part of the backdrop, and <c>Location.colorFilter(loc.color)</c> reads the room's own
        /// colour, so the front takes the plain scheme even when the backdrop was overridden.</para>
        ///
        /// <para>Pure and public so a fixture can pin it without a scene: the renderer cannot be built
        /// in EditMode without the art asset, but the colour decision can be asked about directly.</para>
        /// </summary>
        public static Color ResolveFrontTint(RoomInstance room, bool apply)
        {
            if (!apply || room == null || room.environment == null) return Color.white;

            return RoomBackdropRenderer.ResolveColorSchemeTint(room.environment.colorScheme);
        }

        private Tilemap LayerMap(
            string layerName,
            string textureId,
            string sortingLayer,
            int sortingOrder,
            RoomTerrainArt art,
            Options options,
            float maskDilateUV)
        {
            // The order is part of the identity of a map, not a property you may ask for and be denied:
            // two callers wanting different orders are two different layers. Leaving it out meant a cache
            // hit returned a map already pinned to the first caller's order and the second caller's
            // `sortingOrder` was accepted and ignored — the same silent-parameter shape as SetTileFlags.
            string key = layerName + "|" + textureId + "|" + sortingOrder;
            if (_layers.TryGetValue(key, out Tilemap existing)) return existing;

            var go = new GameObject("Layer: " + layerName);
            go.transform.SetParent(transform, false);

            var map = go.AddComponent<Tilemap>();

            // The cell's origin is its corner; the registration maths already accounts for where the
            // shape's pivot sits, so an anchor would move every shape by half a cell.
            map.tileAnchor = Vector3.zero;

            var renderer = go.AddComponent<TilemapRenderer>();
            renderer.mode = TilemapRenderer.Mode.Chunk;
            // Vector3Int, not int: Unity asks per axis, and a 2D grid has no depth to chunk.
            renderer.chunkSize = new Vector3Int(ChunkSize, ChunkSize, 1);
            renderer.sortingLayerName = sortingLayer;
            renderer.sortingOrder = sortingOrder;
            renderer.sharedMaterial = MaterialFor(textureId, art, options, maskDilateUV);

            _layers[key] = map;
            _mapBuckets[map] = layerName + "|" + textureId;
            _owned.Add(go);
            return map;
        }

        /// <summary>
        /// How far to grow a mask, in UV, from the option's texel count and the mask texture's width.
        /// </summary>
        /// <remarks>
        /// One texel is <c>1 / texture.width</c> in UV whether the mask is its own texture or packed into
        /// an atlas, because a sprite's UVs are normalised to the texture it lives in. So the width of the
        /// <i>texture</i> is the right divisor, not the width of the sprite rect. Any frame of the mask's
        /// clip will do: every frame of a clip comes from the same texture.
        /// </remarks>
        private static float MaskDilateUV(RoomTerrainArt art, RoomTerrainArt.MaskEntry mask, Options options)
        {
            if (options.maskDilateTexels <= 0f) return 0f;

            Sprite reference = art.Frame(mask.clip, RoomQuarterRules.Inside);

            if (reference == null && mask.quarters != null)
            {
                foreach (RoomTerrainArt.QuarterEntry quarter in mask.quarters)
                {
                    if (quarter == null) continue;
                    reference = art.Frame(quarter.clip, RoomQuarterRules.Inside);
                    if (reference != null) break;
                }
            }

            if (reference == null || reference.texture == null) return 0f;

            int width = reference.texture.width;
            return width > 0 ? options.maskDilateTexels / width : 0f;
        }

        private Material MaterialFor(string textureId, RoomTerrainArt art, Options options, float maskDilateUV)
        {
            // The dilation is part of the material's identity, not a value that can be shared: two layers
            // can use the same material texture with masks of different resolutions, and a shared material
            // would give the second layer the first layer's texel size. Keying on it costs one material.
            string key = textureId + "|" + maskDilateUV.ToString("0.00000");

            if (_materials.TryGetValue(key, out Material existing)) return existing;

            // A miss is the only expensive path: `new Material` on a URP shader can compile variants the
            // first time, and `art.Texture` may resolve an asset. Cached thereafter, so this region's
            // total is "one per distinct texture", not "one per shape".
            using (Profiler.Region("room.terrain.materials", "one Material per distinct texture — new Material may compile variants"))
            {
                Texture2D texture = art.Texture(textureId);
                if (texture == null)
                {
                    Count(_missingMaterials, textureId);
                    return null;
                }

                var material = new Material(_shader) { name = "RoomTerrain:" + textureId };
                material.SetTexture("_MaterialTex", texture);

                // Bitmap rows run down from the room's top-left corner; Unity's v runs up, so the origin
                // is the top of the room and the subtraction below is negative going down. That is what
                // makes the projection agree with a Flash bitmap fill instead of mirroring it.
                material.SetVector("_MaterialOrigin", new Vector4(0f, RoomTopLocalY, 0f, 0f));

                // One texture texel per source pixel, and a cell is CellPixels source pixels wide.
                float texelsPerMeter = options.cellSize > 0f ? art.CellPixels / options.cellSize : art.CellPixels;
                material.SetFloat("_TexelsPerMeter", texelsPerMeter);
                material.SetFloat("_Ambient", Mathf.Clamp01(options.ambient));
                material.SetColor("_Color", Color.white);
                material.SetFloat("_MaskDilateUV", Mathf.Max(0f, maskDilateUV));

                _materials[key] = material;
                _owned.Add(material);
                return material;
            }
        }

        /// <summary>Top of the room in this transform's local space, set by <see cref="Build"/>.</summary>
        private float RoomTopLocalY { get; set; }

        private Tile TileFor(Sprite sprite)
        {
            if (sprite == null) return null;
            if (_tiles.TryGetValue(sprite, out Tile tile)) return tile;

            // A ScriptableObject per distinct mask frame, cached — so this is "one per frame used", and
            // a room that draws 20 material buckets out of 5 masks creates 5, not 200.
            using (Profiler.Region("room.terrain.tiles", "one Tile ScriptableObject per distinct mask frame, cached"))
            {
                tile = ScriptableObject.CreateInstance<Tile>();
                tile.name = sprite.name;
                tile.sprite = sprite;

                // Sprite-sheet masks must not be packed or the atlas would repack the quarter into a
                // different rect than the one the pivot was computed against.
                tile.flags = TileFlags.None;
                tile.colliderType = Tile.ColliderType.None;

                _tiles[sprite] = tile;
                _owned.Add(tile);
                return tile;
            }
        }

        private Shader ResolveShader()
        {
            Shader shader = Resources.Load<Shader>(ShaderResourcePath);
            if (shader != null) return shader;

            shader = Shader.Find(ShaderName);
            if (shader != null) return shader;

            return null;
        }

        /// <summary>Destroys everything this renderer created. Safe to call twice, and in edit mode.</summary>
        public void Clear()
        {
            // Before anything else, and unconditionally: `Build` calls `Clear` first, so a stale
            // subscription would make one renderer rebuild twice per mutation. The room is a plain
            // object that outlives this GameObject, so a forgotten unsubscribe is a leak with a
            // callback into a destroyed MonoBehaviour — the loudest possible version of this bug.
            if (_room != null)
            {
                _room.TilesMutated -= OnTilesMutated;
                _room = null;
            }

            foreach (Tilemap map in _layers.Values)
            {
                if (map != null) DestroyObject(map.gameObject);
            }

            _layers.Clear();
            _mapBuckets.Clear();

            foreach (UnityEngine.Object owned in _owned)
            {
                if (owned != null) DestroyObject(owned);
            }

            _owned.Clear();
            _materials.Clear();
            _tiles.Clear();
            _missingMasks.Clear();
            _missingMaterials.Clear();
            _missingOverlays.Clear();
            _layerTiles.Clear();
            _overlaysByCell.Clear();
            _overlayRoot = null;
            _overlayMaterial = null;
            _flashRoot = null;
            _flashSprite = null;

            // Art, material database and options are borrowed, not owned: nulling them is what makes
            // a stray LateUpdate after teardown do nothing instead of rebuilding from a dead room.
            _art = null;
            _materialDb = null;
            _options = default;
            _states = null;
            _appliedBackTint = Color.white;

            _dirtyRegion = default;
            _hasDirtyRegion = false;

            ShapesPlaced = 0;
            OverwrittenShapes = 0;
            OverlaysPlaced = 0;
            OverlaySortKey = "not placed";
            LastBuildMilliseconds = 0d;
            MutationRebuilds = 0;
            LastMutationMilliseconds = 0d;
            LastMutationCells = 0;
            IsBuilt = false;
        }

        private void OnDestroy()
        {
            Clear();
        }

        /// <summary>
        /// <c>Destroy</c> throws in edit mode, and the preview window builds this outside play mode.
        /// Same seam, and same reason, as <c>CharacterSpriteAssembler.DestroyObject</c>.
        /// <para><c>new</c> is required: <c>UnityEngine.Object</c> has a deprecated
        /// <c>DestroyObject</c> of its own. Without it every call here is a CS0108.</para>
        /// </summary>
        private static new void DestroyObject(UnityEngine.Object target)
        {
            if (target == null) return;

            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
