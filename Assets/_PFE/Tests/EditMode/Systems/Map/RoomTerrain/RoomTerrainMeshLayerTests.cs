using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map.Rendering.RoomTerrain;

namespace PFE.Tests.EditMode.Systems.Map.RoomTerrain
{
    /// <summary>
    /// Pins the mutation contract of <see cref="RoomTerrainMeshLayer"/>: a tile change rewrites
    /// only that shape's slot, in place, with the same visible result as a fresh write — and
    /// nothing else in the buffer moves.
    ///
    /// <para><b>Why this fixture exists.</b> The renderer's whole reason for being a mesh is that a
    /// destroyed tile must not rebuild the room: <c>FlushMutations</c> clears a cell's half-cell
    /// slots and re-places them, and every other slot has to come through untouched. That contract
    /// is testable without a scene, a shader or the art asset — the layer takes a material and a
    /// parent, and the assertions read the mesh back.</para>
    ///
    /// <para><b>The expected values are produced by the pinned arithmetic itself.</b>
    /// <see cref="RoomTerrainMeshLayer.WriteQuad"/> is the pure function the fixtures already trust;
    /// comparing the uploaded mesh against a buffer that same function produced checks the upload
    /// path (slot addressing, partial ranges, degenerate clears) rather than re-deriving geometry.</para>
    /// </summary>
    public sealed class RoomTerrainMeshLayerTests
    {
        const float QuarterSize = 0.2f; // half of the shipped 0.4 cell
        const int GridWidth = 8;        // an index space; the tests only need it to be consistent
        const float MaskPpu = 400f;     // the import PPU every exported mask arrives at

        static readonly List<Object> Trash = new List<Object>();

        GameObject _parent;
        Material _material;

        [SetUp]
        public void SetUp()
        {
            _parent = new GameObject("RoomTerrainMeshLayerTests_parent");
            _material = new Material(Shader.Find("Sprites/Default"));
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_material);
            Object.DestroyImmediate(_parent);
        }

        [OneTimeTearDown]
        public void FixtureTearDown()
        {
            foreach (Object item in Trash)
            {
                if (item != null) Object.DestroyImmediate(item);
            }
            Trash.Clear();
        }

        RoomTerrainMeshLayer CreateLayer()
        {
            return new RoomTerrainMeshLayer(
                _parent.transform,
                "test:bucket",
                _material,
                "Default",
                0,
                new Bounds(new Vector3(2f, 2f, 0f), new Vector3(4f, 4f, 1f)));
        }

        // ── WriteQuad: the pinned placement arithmetic ──────────────────────────

        [Test]
        public void WriteQuad_PutsThePivotOnTheHalfCellOriginPlusTranslation()
        {
            // 40×40 mask at PPU 400, centre pivot, unit scale, no translation: the sprite pivot
            // lands exactly on the half-cell origin, so the quad spans ±0.05 around it.
            var buffer = new RoomTerrainMeshLayer.TerrainVertex[4];
            RoomTerrainMeshLayer.WriteQuad(
                rect: new Rect(0f, 0f, 40f, 40f),
                pivotPixels: new Vector2(20f, 20f),
                pixelsPerUnit: MaskPpu,
                textureWidth: 40,
                textureHeight: 40,
                cellX: 2,
                cellY: 3,
                quarterSize: QuarterSize,
                translation: Vector2.zero,
                scale: Vector2.one,
                color: Color.white,
                buffer: buffer,
                offset: 0);

            float originX = 2f * QuarterSize; // 0.4
            float originY = 3f * QuarterSize; // 0.6

            Assert.AreEqual(originX - 0.05f, buffer[0].Position.x, 1e-6f, "bl x");
            Assert.AreEqual(originY - 0.05f, buffer[0].Position.y, 1e-6f, "bl y");
            Assert.AreEqual(originX + 0.05f, buffer[2].Position.x, 1e-6f, "tr x");
            Assert.AreEqual(originY + 0.05f, buffer[2].Position.y, 1e-6f, "tr y");
            Assert.AreEqual(0f, buffer[0].Position.z, 1e-6f, "z stays on the grid plane");

            // Corner order is bl, tl, tr, br — clockwise from the camera.
            Assert.AreEqual(originX - 0.05f, buffer[1].Position.x, 1e-6f, "tl x");
            Assert.AreEqual(originY + 0.05f, buffer[1].Position.y, 1e-6f, "tl y");
            Assert.AreEqual(originX + 0.05f, buffer[3].Position.x, 1e-6f, "br x");
            Assert.AreEqual(originY - 0.05f, buffer[3].Position.y, 1e-6f, "br y");

            // UVs are the rect normalised to the texture — 40×40 in a 40×40 texture is the whole map.
            Assert.AreEqual(new Vector2(0f, 0f), buffer[0].UV);
            Assert.AreEqual(new Vector2(1f, 1f), buffer[2].UV);
        }

        [Test]
        public void WriteQuad_NegativeScaleMirrorsAboutThePivot()
        {
            var buffer = new RoomTerrainMeshLayer.TerrainVertex[4];
            RoomTerrainMeshLayer.WriteQuad(
                rect: new Rect(0f, 0f, 40f, 40f),
                pivotPixels: new Vector2(20f, 20f),
                pixelsPerUnit: MaskPpu,
                textureWidth: 40,
                textureHeight: 40,
                cellX: 0,
                cellY: 0,
                quarterSize: QuarterSize,
                translation: Vector2.zero,
                scale: new Vector2(-1f, 1f), // the quarter mirrors carry their flip in the sign
                color: Color.white,
                buffer: buffer,
                offset: 0);

            Assert.AreEqual(0.05f, buffer[0].Position.x, 1e-6f, "a -1 x-scale moves bl to +x of the pivot");
            Assert.AreEqual(-0.05f, buffer[2].Position.x, 1e-6f, "and tr to -x");
            Assert.AreEqual(0.05f, buffer[1].Position.y, 1e-6f, "y is untouched by an x mirror");
        }

        // ── The mutation contract ───────────────────────────────────────────────

        [Test]
        public void Mutation_RewritesOnlyTheTouchedSlots_InPlace()
        {
            RoomTerrainMeshLayer layer = CreateLayer();
            try
            {
                // Two shapes in two different half-cells, flushed once, as "the room".
                Assert.IsFalse(layer.Write(1, 1, GridWidth, QuarterSize, SpriteFor(40, 40, 20, 20),
                    Vector2.zero, Vector2.one, Color.white));
                Assert.IsFalse(layer.Write(5, 3, GridWidth, QuarterSize, SpriteFor(40, 40, 20, 20),
                    Vector2.zero, Vector2.one, new Color(1f, 0.5f, 0.25f, 0.75f)));
                layer.FlushUploads();
                Assert.AreEqual(2, layer.UsedSlots);

                RoomTerrainMeshLayer.TerrainVertex[] before = Snapshot(layer);
                Assert.AreEqual(64 * 4, before.Length,
                    "the buffer is pre-allocated at the initial slot capacity — no per-tile allocation");
                Assert.AreEqual(2, layer.UsedSlots, "only two of the pre-allocated slots are occupied");

                // The tile at half-cell (1,1) is destroyed: clear it and re-place it — the exact
                // clear-then-place shape FlushMutations performs.
                Assert.IsTrue(layer.ClearHalfCell(1, 1, GridWidth), "the first clear must find the shape");
                Assert.IsFalse(layer.ClearHalfCell(1, 1, GridWidth), "a second clear of the same cell is a no-op");
                Assert.AreEqual(1, layer.UsedSlots);
                layer.FlushUploads();

                Assert.IsFalse(layer.Write(1, 1, GridWidth, QuarterSize, SpriteFor(40, 40, 20, 20),
                    Vector2.zero, Vector2.one, Color.white));
                Assert.AreEqual(2, layer.UsedSlots, "re-placing a cleared slot is a transition, not an add");
                layer.FlushUploads();

                RoomTerrainMeshLayer.TerrainVertex[] after = Snapshot(layer);

                // Same visible result: every untouched vertex is identical, and the re-placed slot
                // matches the pinned arithmetic for its inputs.
                for (int i = 4; i < 8; i++)
                {
                    Assert.AreEqual(before[i].Position, after[i].Position, $"untouched vertex {i} moved");
                    Assert.AreEqual(before[i].UV, after[i].UV, $"untouched vertex {i} uv moved");
                    Assert.AreEqual(before[i].Color, after[i].Color, $"untouched vertex {i} colour moved");
                }

                var expected = new RoomTerrainMeshLayer.TerrainVertex[4];
                RoomTerrainMeshLayer.WriteQuad(
                    rect: new Rect(0f, 0f, 40f, 40f),
                    pivotPixels: new Vector2(20f, 20f),
                    pixelsPerUnit: MaskPpu,
                    textureWidth: 40,
                    textureHeight: 40,
                    cellX: 1,
                    cellY: 1,
                    quarterSize: QuarterSize,
                    translation: Vector2.zero,
                    scale: Vector2.one,
                    color: Color.white,
                    buffer: expected,
                    offset: 0);

                for (int i = 0; i < 4; i++)
                {
                    Assert.AreEqual(expected[i].Position, after[i].Position, $"re-placed vertex {i} position");
                    Assert.AreEqual(expected[i].UV, after[i].UV, $"re-placed vertex {i} uv");
                    Assert.AreEqual(expected[i].Color, after[i].Color, $"re-placed vertex {i} colour");
                }
            }
            finally
            {
                layer.Destroy();
            }
        }

        [Test]
        public void Mutation_RewritingAnOccupiedSlotCountsAsAReplace()
        {
            RoomTerrainMeshLayer layer = CreateLayer();
            try
            {
                Assert.IsFalse(layer.Write(2, 2, GridWidth, QuarterSize, SpriteFor(40, 40, 20, 20),
                    Vector2.zero, Vector2.one, Color.white), "first write is an add");
                Assert.AreEqual(1, layer.UsedSlots);

                Assert.IsTrue(layer.Write(2, 2, GridWidth, QuarterSize, SpriteFor(40, 40, 20, 20),
                    Vector2.zero, Vector2.one, Color.white), "a second write into the same half-cell replaces");
                Assert.AreEqual(1, layer.UsedSlots, "a replace must not grow the count");
            }
            finally
            {
                layer.Destroy();
            }
        }

        // ── Growth: the doubling every real room forces on its first build ──────

        [Test]
        public void Growth_ResizesTheMesh_BeforeTheIndexBufferReferencesTheNewSlots()
        {
            RoomTerrainMeshLayer layer = CreateLayer();
            try
            {
                Sprite sprite = SpriteFor(40, 40, 20, 20);

                // A room overruns the 64-slot initial capacity on its first build, and the crash
                // this pins was the first write past it:
                //   "Index buffer element #384 (value 256) is out of bounds; mesh only has 256
                //    vertices." — thrown from SetIndexBufferData, inside Grow, during Build.
                for (int slot = 0; slot < 64; slot++)
                {
                    Assert.IsFalse(
                        layer.Write(slot % GridWidth, slot / GridWidth, GridWidth, QuarterSize, sprite,
                            Vector2.zero, Vector2.one, Color.white),
                        $"slot {slot} is an add");
                }

                layer.FlushUploads();
                Assert.AreEqual(64 * 4, layer.Mesh.vertexCount,
                    "the mesh starts at the initial slot capacity");

                Assert.DoesNotThrow(
                    () => layer.Write(0, 8, GridWidth, QuarterSize, sprite, Vector2.zero, Vector2.one, Color.white),
                    "the first slot past the initial capacity must grow the buffer, not throw");
                layer.FlushUploads();

                // Growing the C# array is not enough: the mesh's own vertex count is what
                // SetIndexBufferData validates the indices against, so it has to grow too.
                Assert.AreEqual(128 * 4, layer.Mesh.vertexCount,
                    "the mesh vertex count must follow the grown array");
                Assert.AreEqual(65, layer.UsedSlots);

                // A full index buffer is not a draw. SetIndexBufferParams allocates the buffer but
                // gives submesh 0 no range, and re-creating the buffer on a grow discards whatever
                // range it had — so a layer can have correct vertices, correct bounds, a correct
                // material, an enabled renderer, valid indices and still submit zero draw calls.
                // That is exactly what happened: the room built 4095 shapes and drew no terrain.
                Assert.AreEqual(1, layer.Mesh.subMeshCount,
                    "the layer must own a submesh — an empty submesh range draws nothing");
                Assert.AreEqual(128 * 6, layer.Mesh.GetIndexCount(0),
                    "submesh 0 must cover the whole index buffer, including the slots the grow added");

                // Resizing the vertex buffer takes the mesh's AABB with it, and every upload here
                // passes DontRecalculateBounds — so the layer has to re-assert its own extent or the
                // whole mesh is culled. That failure is silent: the vertices, the triangles and the
                // material are all still there and nothing is drawn.
                Assert.AreEqual(new Vector3(4f, 4f, 1f), layer.Mesh.bounds.size,
                    "a grow must not leave the mesh bounded by a zero-size box — it would be culled");
                Assert.AreEqual(new Vector3(2f, 2f, 0f), layer.Mesh.bounds.center,
                    "the re-asserted bounds are the layer's own extent, not a recalculated one");

                RoomTerrainMeshLayer.TerrainVertex[] data = Snapshot(layer);
                Assert.AreEqual(128 * 4, data.Length);

                // The shapes written before the grow keep their addresses and their geometry: the
                // native vertex buffer was reallocated underneath them, so a grow has to re-upload
                // the range that was already flushed.
                for (int slot = 0; slot < 64; slot++)
                {
                    RoomTerrainMeshLayer.TerrainVertex[] expected = ExpectedQuad(slot);
                    for (int i = 0; i < 4; i++)
                    {
                        Assert.AreEqual(expected[i].Position, data[slot * 4 + i].Position,
                            $"slot {slot} vertex {i} was lost by the grow");
                    }
                }

                // And the slot that forced the grow holds the shape that triggered it.
                RoomTerrainMeshLayer.TerrainVertex[] grown = ExpectedQuad(64);
                for (int i = 0; i < 4; i++)
                {
                    Assert.AreEqual(grown[i].Position, data[64 * 4 + i].Position,
                        $"the grown slot's vertex {i}");
                }
            }
            finally
            {
                layer.Destroy();
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The vertices a slot must hold: slot <c>n</c> is the <c>n</c>-th half-cell written by
        /// <see cref="Growth_ResizesTheMesh_BeforeTheIndexBufferReferencesTheNewSlots"/>, which
        /// walks the grid row-major — so the slot index and the half-cell are the same arithmetic.
        /// </summary>
        static RoomTerrainMeshLayer.TerrainVertex[] ExpectedQuad(int slot)
        {
            var expected = new RoomTerrainMeshLayer.TerrainVertex[4];
            RoomTerrainMeshLayer.WriteQuad(
                rect: new Rect(0f, 0f, 40f, 40f),
                pivotPixels: new Vector2(20f, 20f),
                pixelsPerUnit: MaskPpu,
                textureWidth: 40,
                textureHeight: 40,
                cellX: slot % GridWidth,
                cellY: slot / GridWidth,
                quarterSize: QuarterSize,
                translation: Vector2.zero,
                scale: Vector2.one,
                color: Color.white,
                buffer: expected,
                offset: 0);
            return expected;
        }

        /// <summary>
        /// A real sprite over a real texture, so <c>sprite.rect/pivot/ppu</c> read exactly what the
        /// test asked for. Nothing ever draws it (no camera), so the objects are trashed at the end
        /// of the fixture instead of per test.
        /// </summary>
        static Sprite SpriteFor(int width, int height, float pivotX, float pivotY)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, width, height),
                new Vector2(pivotX / width, pivotY / height),
                MaskPpu);
            sprite.name = $"test_{width}x{height}";
            Trash.Add(texture);
            Trash.Add(sprite);
            return sprite;
        }

        // ── The defined-buffer invariant ────────────────────────────────────────

        [Test]
        public void FreshLayer_EveryUnusedSlotReadsBackZero()
        {
            // The index buffer draws every pre-allocated slot from the first frame, so a slot the
            // build never wrote must be a defined degenerate quad — not whatever the freshly
            // allocated native buffer happened to hold. That was the bug that painted the room red
            // on some runs and left stripe bars at the room's corner on others, different every
            // play, with no error anywhere: SetVertexBufferParams promises nothing about content.
            // The constructor now uploads the zeroed managed array once; this readback pins it.
            RoomTerrainMeshLayer layer = CreateLayer();
            try
            {
                RoomTerrainMeshLayer.TerrainVertex[] fresh = Snapshot(layer);
                Assert.AreEqual(64 * 4, fresh.Length, "the initial slot capacity is fully declared");
                for (int i = 0; i < fresh.Length; i++)
                {
                    Assert.AreEqual(default(RoomTerrainMeshLayer.TerrainVertex), fresh[i],
                        $"vertex {i} of a never-written buffer is not zero — the index buffer draws it");
                }
            }
            finally
            {
                layer.Destroy();
            }
        }

        static RoomTerrainMeshLayer.TerrainVertex[] Snapshot(RoomTerrainMeshLayer layer)
        {
            // The raw vertex buffer, not the legacy vertices/uv/colors getters: those read the
            // legacy channel cache, which a buffer-declared mesh does not maintain — the first
            // version of this fixture read NaNs and swapped channels through them while the upload
            // was correct.
            Mesh mesh = layer.Mesh;
            GraphicsBuffer buffer = mesh.GetVertexBuffer(0);
            try
            {
                var data = new RoomTerrainMeshLayer.TerrainVertex[mesh.vertexCount];
                buffer.GetData(data);
                return data;
            }
            finally
            {
                buffer.Release();
            }
        }
    }
}
