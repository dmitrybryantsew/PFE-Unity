using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace PFE.Systems.Map.Rendering.RoomTerrain
{
    /// <summary>
    /// Draws a room the way Remains-Unity draws one: half-cell Tilemaps holding shape masks, with the
    /// material bitmap laid in world space by a shader.
    ///
    /// <para><b>A parallel path, not a replacement.</b> Nothing in the game calls this.
    /// <see cref="RoomVisualController"/> still builds <see cref="TileVisualManager"/> over
    /// <see cref="TileCompositor"/>, and that is deliberate: the A/B is only worth anything while both
    /// sides are real, and a regression here cannot reach a player. The opt-in, when someone wants it,
    /// is to build this instead of <c>CreateAllTiles</c> for a room and delete nothing else.</para>
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
        /// <para><b>Under the walls, which is what AS3 does and what the default is not.</b>
        /// <c>Grafon.as:470-499</c> adds every overlay to a <c>front</c> sprite, and <c>:518</c> blits
        /// that sprite into <c>frontBmp</c> <i>before</i> <c>:526</c> draws any material into the same
        /// bitmap. So in the original a material's pixels land on top of an overlay that shares its
        /// cell, and the overlay is seen only through the channel the mask carves. The shipped path
        /// does the opposite — it puts the overlay children at <c>baseOrder + 1</c> / <c>+2</c>, above
        /// the tile sprite. See <see cref="OverlayAboveOrder"/> for why the port's default is the
        /// third option.</para>
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
            /// Draw nothing for state 0 instead of the mask's frame 1.
            ///
            /// <para><b>A diagnostic switch, not a comparison mode — and it was documented as one for
            /// the wrong reason.</b> The shipped compositor "skips the mask at state 0" by writing the
            /// material through <i>unmasked</i>, i.e. as a plain square. This renderer's frame 1 for
            /// state 0 <i>is</i> a plain square for most masks, so drawing it already reproduces the
            /// shipped rule; turning this on leaves those quarters <b>empty</b> instead, which is a hole
            /// in the wall, not a comparison. It is off by default for that reason. Use it to count how
            /// much of a room is state 0 — the difference between the two runs is that area — and turn
            /// it back off before judging anything.</para>
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
                    overlayAboveMaterials = true,
                    skipState0Mask = false,
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

        private Shader _shader;
        private Transform _overlayRoot;

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

        /// <summary>Mask placements written. One per quarter that is drawn, so up to four per cell.</summary>
        public int ShapesPlaced { get; private set; }

        /// <summary>
        /// Overlay sprites placed — ladders, stair treads and ledges.
        ///
        /// <para><b>A number here is the only way to tell "no ladders in this room" from "the overlay
        /// pass did not run".</b> 387 of 640 shipped rooms contain a ladder form, so a room with zero
        /// overlays is either unusual or broken, and the presenter prints this count next to the
        /// shapes so the two can be read together.</para>
        /// </summary>
        public int OverlaysPlaced { get; private set; }

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

            _shader = ResolveShader();
            if (_shader == null)
            {
                Debug.LogError(
                    $"[RoomTerrain] Shader '{ShaderName}' not found (expected at Resources/{ShaderResourcePath}). " +
                    "The room was not built.");
                return;
            }

            if (options.cellSize <= 0f) options.cellSize = RoomTerrainGeometry.DefaultCellSize;

            double started = Time.realtimeSinceStartupAsDouble;

            RoomQuarterStates states = BuildStates(room);

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

            // Materials are created lazily inside the loop, so the room's top edge has to be known
            // before the first one: the bitmap fill starts there and runs down.
            RoomTopLocalY = height * options.cellSize;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    TileData tile = room.tiles != null ? room.tiles[x, y] : null;
                    if (tile == null) continue;

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

                    if (!options.drawFront) continue;

                    string frontId = tile.GetFrontGraphic();
                    if (string.IsNullOrEmpty(frontId)) continue;

                    MaterialRenderEntry front = materials.GetFrontMaterial(frontId);
                    if (front == null)
                    {
                        Count(_missingMaterials, frontId);
                        continue;
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
                            x, y, tile.heightLevel, opacity, FrontTint, state, mainPart);
                    }

                    if (options.drawFloor && front.HasFloor)
                    {
                        Layer(art, options, front.floorTexture, MaskName(front.floorMask),
                            partPrefix + front.id, sortingLayer, baseOrder + FloorOrder,
                            x, y, tile.heightLevel, opacity, FrontTint, state, Part.Floor);
                    }
                }
            }

            IsBuilt = true;
            LastBuildMilliseconds = (Time.realtimeSinceStartupAsDouble - started) * 1000.0;
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

            bool IsSolid(int x, int y)
            {
                TileData tile = room.tiles[x, y];
                return tile != null && tile.physicsType == TilePhysicsType.Wall;
            }

            int BackId(int x, int y)
            {
                TileData tile = room.tiles[x, y];
                if (tile == null) return 0;

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
                return tile != null && (tile.isLedge || tile.physicsType == TilePhysicsType.Platform);
            }

            // A cell draws a front whenever it carries a front graphic — walls, but also the platforms
            // and beams that are not solid. Without this the front array says NotDrawn for every
            // non-wall cell and the renderer places nothing there, which is how a room's catwalks went
            // missing while its walls drew correctly.
            bool HasFront(int x, int y)
            {
                TileData tile = room.tiles[x, y];
                return tile != null && !string.IsNullOrEmpty(tile.GetFrontGraphic());
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

            OverlaysPlaced++;
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

            Tilemap map = LayerMap(layerName, textureId, sortingLayer, sortingOrder, art, options);

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

                Place(map, layerName + "|" + textureId, whole,
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

                Place(map, layerName + "|" + textureId, sprite,
                    RoomTerrainGeometry.Scaled(
                        placement, MaskScaleFor(art, sprite, quarter.clip, cellSize)), opacity, tint);
            }
        }

        private void Place(
            Tilemap map,
            string layerKey,
            Sprite sprite,
            RoomTerrainPlacement placement,
            float opacity,
            Color tint)
        {
            Tile tile = TileFor(sprite);
            if (tile == null) return;

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
            var colour = new Color(tint.r, tint.g, tint.b, opacity);
            if (colour != Color.white)
                map.SetColor(placement.Cell, colour);

            _layerTiles.TryGetValue(layerKey, out int placed);
            _layerTiles[layerKey] = placed + 1;

            ShapesPlaced++;
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
            Options options)
        {
            string key = layerName + "|" + textureId;
            if (_layers.TryGetValue(key, out Tilemap existing)) return existing;

            var go = new GameObject("Layer: " + layerName);
            go.transform.SetParent(transform, false);

            var map = go.AddComponent<Tilemap>();

            // The cell's origin is its corner; the registration maths already accounts for where the
            // shape's pivot sits, so an anchor would move every shape by half a cell.
            map.tileAnchor = Vector3.zero;

            var renderer = go.AddComponent<TilemapRenderer>();
            renderer.mode = TilemapRenderer.Mode.Chunk;
            renderer.sortingLayerName = sortingLayer;
            renderer.sortingOrder = sortingOrder;
            renderer.sharedMaterial = MaterialFor(textureId, art, options);

            _layers[key] = map;
            _owned.Add(go);
            return map;
        }

        private Material MaterialFor(string textureId, RoomTerrainArt art, Options options)
        {
            if (_materials.TryGetValue(textureId, out Material existing)) return existing;

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

            _materials[textureId] = material;
            _owned.Add(material);
            return material;
        }

        /// <summary>Top of the room in this transform's local space, set by <see cref="Build"/>.</summary>
        private float RoomTopLocalY { get; set; }

        private Tile TileFor(Sprite sprite)
        {
            if (sprite == null) return null;
            if (_tiles.TryGetValue(sprite, out Tile tile)) return tile;

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
            foreach (Tilemap map in _layers.Values)
            {
                if (map != null) DestroyObject(map.gameObject);
            }

            _layers.Clear();

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
            _overlayRoot = null;
            _overlayMaterial = null;
            ShapesPlaced = 0;
            OverlaysPlaced = 0;
            OverlaySortKey = "not placed";
            LastBuildMilliseconds = 0d;
            IsBuilt = false;
        }

        private void OnDestroy()
        {
            Clear();
        }

        /// <summary>
        /// <c>Destroy</c> throws in edit mode, and the preview window builds this outside play mode.
        /// Same seam, and same reason, as <c>CharacterSpriteAssembler.DestroyObject</c>.
        /// </summary>
        private static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null) return;

            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
