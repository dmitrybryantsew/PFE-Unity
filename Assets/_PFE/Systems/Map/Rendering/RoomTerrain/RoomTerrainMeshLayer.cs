using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PFE.Systems.Map.Rendering.RoomTerrain
{
    /// <summary>
    /// One terrain layer drawn as a single dynamic mesh — the quarter renderer's replacement for a
    /// half-cell <c>Tilemap</c>. One layer covers one material texture × one mask texture × one
    /// sorting order (the same axes the old <c>TilemapRenderer</c> split its chunk draws by).
    /// Public so an EditMode fixture can drive the mutation contract without a scene or a shader.
    ///
    /// <para><b>Why a mesh, in one paragraph.</b> A <c>Tilemap</c> rebuilds its whole chunk mesh on
    /// every <c>SetTile</c>, and this renderer sizes its chunks to cover the room so one layer is one
    /// mesh — which made a single destroyed tile rebuild every placed shape in every layer. Here each
    /// placed shape owns a fixed slot (four vertices, two triangles) for the life of the room, a tile
    /// mutation rewrites only that cell's slots in the managed buffer, and one partial
    /// <c>SetVertexBufferData</c> per layer uploads the dirt — O(1) per tile instead of O(room).</para>
    ///
    /// <para><b>Slots are reserved, not recycled.</b> A cleared slot is zeroed and stays mapped to its
    /// half-cell, so the clear-then-place pass of a region rebuild rewrites the same addresses and
    /// never allocates. The buffer doubles on demand — during the first build of a room (64 slots is
    /// only a floor, a real room outgrows it) and again whenever a mutation introduces a (layer, cell)
    /// pair the build never placed. A grow costs one index re-upload plus one full upload of that
    /// layer, the same price the old path paid for a single <c>SetTile</c>.</para>
    ///
    /// <para><b>Vertex layout matches <c>PFE_RoomTerrain.shader</c>.</b> The shader reads POSITION,
    /// TEXCOORD0 and COLOR — the same channels a <c>Tilemap</c> fed it — so the fragment path is
    /// untouched. Hidden and unused slots are zero-area quads (all four positions equal), which is
    /// what lets the index buffer be written once per capacity instead of once per tile.</para>
    /// </summary>
    public sealed class RoomTerrainMeshLayer
    {
        /// <summary>
        /// One terrain vertex: position, per-cell colour, mask UV. Interleaved so a whole mutation
        /// uploads as one buffer range; the field order is the wire format pinned by
        /// <see cref="VertexLayout"/>.
        ///
        /// <para><b>Declared in Unity's standard order — position, colour, UV.</b> The first version of
        /// this struct put <c>UV</c> before <c>Color</c>, which made Unity log "Mesh vertex buffer
        /// attributes were supplied in non-standard order. Layout was adjusted to this order: Position,
        /// Color, TexCoord0" on every layer, 372 times per room build. That warning is a repack notice,
        /// <b>not</b> a correctness one: <c>CharacterQuadVertex</c> still declares the same non-standard
        /// order and the characters draw their sprite detail correctly, so the channel mapping survives
        /// the adjustment. Matching the standard order simply keeps the log honest — the cost of the
        /// repack is real, the misattribution it seemed to imply is not.</para>
        ///
        /// <para><b>The offsets are still load-bearing.</b> <c>SetVertexBufferData</c> uploads the struct
        /// verbatim, so the field offsets must be the ones <c>SetVertexBufferParams</c> assigns —
        /// position at 0, colour at 12, UV at 28, 36 bytes a vertex. An alignment change that moves a
        /// field would shift a channel with no error at all, and the read-back probes cannot see it:
        /// <c>GetVertexBufferData&lt;TerrainVertex&gt;</c> makes the same assumption in reverse and
        /// returns the values that went in. <c>RoomTerrainVertexLayoutTests</c> compares the struct
        /// against the declaration with <c>Marshal</c> instead, and runs offline.</para>
        /// </summary>
        public struct TerrainVertex
        {
            public Vector3 Position;
            public Color Color;
            public Vector2 UV;

            public static readonly VertexAttributeDescriptor[] VertexLayout =
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            };
        }

        const int InitialSlotCapacity = 64;

        /// <summary>The shape's half-cell → slot map. A slot stays reserved once assigned.</summary>
        readonly Dictionary<int, int> _slotByHalfCell = new Dictionary<int, int>();

        /// <summary>The extent every resize has to re-assert on the mesh — see <see cref="Bounds"/>.</summary>
        readonly Bounds _bounds;

        TerrainVertex[] _vertices = new TerrainVertex[InitialSlotCapacity * 4];
        int[] _indices = new int[InitialSlotCapacity * 6];
        int _slotCount;
        int _usedSlots;

        /// <summary>Dirty vertex span, in vertex indices; uploaded by <see cref="FlushUploads"/>.</summary>
        int _dirtyFrom = int.MaxValue;
        int _dirtyTo;

        /// <summary>The bucket this layer belongs to — the renderer's diagnostic grouping.</summary>
        public string Bucket { get; }

        /// <summary>
        /// The extent this layer's mesh is bounded by, fixed for the life of the room.
        ///
        /// <para><b>Held here, not read back from the mesh, because nothing else restores it.</b>
        /// Every upload in this class passes <c>MeshUpdateFlags.DontRecalculateBounds</c> — the whole
        /// point of the fixed slot grid is that the culler's AABB never has to be recomputed — so the
        /// only bounds the mesh will ever have are the ones written here. <c>SetVertexBufferParams</c>
        /// resizes the vertex buffer and leaves the AABB of the old buffer behind (a zero-size box at
        /// the origin once the buffer is recreated), which is invisible: the mesh keeps its vertices,
        /// its triangles and its material, and is simply culled. That is what "the room built, the log
        /// says 4095 shapes, and nothing is drawn" looks like.</para>
        /// </summary>
        public Bounds Bounds => _bounds;

        /// <summary>How many slots currently hold a shape. Mirrors what GetUsedTilesCount reported.</summary>
        public int UsedSlots => _usedSlots;

        /// <summary>The mesh the <see cref="MeshFilter"/> draws. Exposed for the renderer's teardown.</summary>
        public Mesh Mesh { get; private set; }

        /// <summary>The layer's own GameObject (MeshFilter + MeshRenderer). Owned here, destroyed here.</summary>
        public GameObject GameObject { get; private set; }

        public RoomTerrainMeshLayer(
            Transform parent,
            string bucket,
            Material material,
            string sortingLayer,
            int sortingOrder,
            Bounds localBounds)
        {
            Bucket = bucket;
            _bounds = localBounds;

            GameObject = new GameObject("Layer: " + bucket);
            GameObject.transform.SetParent(parent, false);

            var filter = GameObject.AddComponent<MeshFilter>();
            var renderer = GameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.sortingLayerName = sortingLayer;
            renderer.sortingOrder = sortingOrder;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            Mesh = new Mesh
            {
                name = "RoomTerrain:" + bucket,
                hideFlags = HideFlags.HideAndDontSave,
            };
            Mesh.MarkDynamic();
            Mesh.SetVertexBufferParams(_vertices.Length, TerrainVertex.VertexLayout);

            // The index buffer drawn below references every slot from the first frame, so the native
            // vertex buffer has to be fully defined before anything renders. SetVertexBufferParams
            // promises nothing about content: a slot never written by a partial FlushUploads stays
            // whatever the memory held — the per-run garbage quads that showed up as red veils and
            // stripe bars around the room's corner, brighter or darker every run, with no two runs
            // alike. The managed array is zeroed (degenerate zero-area quads), so uploading it whole
            // once makes every unused slot deterministically invisible. Grow() re-asserts the same
            // invariant through its Touch(0, len) full re-upload.
            Mesh.SetVertexBufferData(
                _vertices, 0, 0, _vertices.Length, 0,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

            UploadIndexBuffer();
            Mesh.bounds = localBounds;
            filter.sharedMesh = Mesh;
        }

        /// <summary>
        /// Writes one mask shape into the layer. Reuses the slot reserved for the shape's
        /// half-cell, or appends one — a mutation therefore never allocates after the first build
        /// of a (layer, cell) pair.
        /// </summary>
        /// <returns>True when the slot already held a shape (a replace, not an add).</returns>
        public bool Write(
            int halfCellX,
            int halfCellY,
            int gridWidth,
            float quarterSize,
            Sprite sprite,
            Vector2 translation,
            Vector2 scale,
            Color color)
        {
            if (sprite == null || sprite.texture == null)
            {
                return false;
            }

            int halfCell = halfCellY * gridWidth + halfCellX;
            if (!_slotByHalfCell.TryGetValue(halfCell, out int slot))
            {
                slot = ReserveSlot(halfCell);
            }

            // Counted when the slot transitions empty → occupied; a rewritten (replaced) slot was
            // already counted. ReplacedShapes is the diagnostic a non-zero value feeds: a correct
            // half-cell grid never replaces, because four quarters are four different half-cells.
            bool replaced = SlotOccupied(slot);
            if (!replaced)
            {
                _usedSlots++;
            }

            Rect rect = sprite.rect;
            float ppu = sprite.pixelsPerUnit > 0f ? sprite.pixelsPerUnit : 100f;
            Texture texture = sprite.texture;
            Vector2 pivot = sprite.pivot;

            WriteQuad(
                rect: rect,
                pivotPixels: pivot,
                pixelsPerUnit: ppu,
                textureWidth: texture.width,
                textureHeight: texture.height,
                cellX: halfCellX,
                cellY: halfCellY,
                quarterSize: quarterSize,
                translation: translation,
                scale: scale,
                color: color,
                buffer: _vertices,
                offset: slot * 4);

            Touch(slot * 4, slot * 4 + 4);
            return replaced;
        }

        bool SlotOccupied(int slot)
        {
            // A slot is occupied iff its first position is non-zero. Zero positions are what
            // ClearHalfCell writes, and a legitimate shape can never have all four corners at the
            // origin — the registration maths always offsets at least one corner.
            return _vertices[slot * 4].Position != Vector3.zero;
        }

        int ReserveSlot(int halfCell)
        {
            if (_slotCount == _vertices.Length / 4)
            {
                Grow();
            }

            int slot = _slotCount++;
            _slotByHalfCell[halfCell] = slot;
            return slot;
        }

        void Grow()
        {
            int slotCapacity = _vertices.Length / 4 * 2;

            var grown = new TerrainVertex[slotCapacity * 4];
            System.Array.Copy(_vertices, grown, _vertices.Length);
            _vertices = grown;

            // The mesh's vertex count is a property of the mesh, not of the C# array. Growing only
            // the array left Mesh.vertexCount at the old capacity, so the doubled index buffer
            // referenced vertices the mesh did not own and SetIndexBufferData threw
            // "Index buffer element #384 (value 256) is out of bounds; mesh only has 256 vertices."
            // — which aborted the room build, not just the upload. The vertex buffer has to be
            // resized BEFORE the index buffer that addresses the new slots is uploaded.
            Mesh.SetVertexBufferParams(_vertices.Length, TerrainVertex.VertexLayout);

            // …and resizing the buffer takes the AABB with it. Every upload in this class passes
            // DontRecalculateBounds, so nothing else will ever write one back: the layer would keep
            // its vertices, its triangles and its material and be culled, which is a silent "no
            // tiles" rather than an error. Re-assert the layer's fixed extent. (The other two mesh
            // builders in this project — CharacterQuadAssembler, TileDecalLayer — recompute bounds on
            // every upload for the same reason; this one is a fixed room extent, so it is a constant.)
            Mesh.bounds = _bounds;

            _indices = new int[slotCapacity * 6];
            UploadIndexBuffer();

            // Resizing the native vertex buffer is not a promise about what it still contains, and
            // the shapes already placed live only in the managed array, so the whole range is
            // dirty: a grow mid-build must not drop the shapes flushed before it. This is the one
            // full upload a grow costs — the reason it doubles instead of growing by a slot.
            Touch(0, _vertices.Length);
        }

        void UploadIndexBuffer()
        {
            for (int slot = 0; slot < _indices.Length / 6; slot++)
            {
                int v = slot * 4;
                int t = slot * 6;
                _indices[t + 0] = v + 0;
                _indices[t + 1] = v + 1;
                _indices[t + 2] = v + 2;
                _indices[t + 3] = v + 0;
                _indices[t + 4] = v + 2;
                _indices[t + 5] = v + 3;
            }

            Mesh.SetIndexBufferParams(_indices.Length, IndexFormat.UInt32);
            Mesh.SetIndexBufferData(_indices, 0, 0, _indices.Length);

            // A full index buffer is not a draw. The mesh needs a submesh *range*, and
            // SetIndexBufferParams does not give it one — it allocates the buffer and leaves submesh 0
            // empty, and re-creating the buffer (every grow) throws away whatever range it had. Unity's
            // own documentation for SetVertexBufferParams says so in as many words: "you must ensure
            // that the index buffer does not contain out-of-bounds indices, and that the SubMesh index
            // range and bounds are updated via Mesh.SetSubMesh".
            //
            // Without this the layer is completely silent: correct vertex count, correct bounds,
            // correct material, enabled renderer, valid indices, and zero draws. That is what the probe
            // caught — `verts=2048 subMeshes=1 indexCount=0` — and it is why the room built 4095 shapes
            // and drew no terrain at all.
            Mesh.SetSubMesh(
                0,
                new SubMeshDescriptor(0, _indices.Length, MeshTopology.Triangles) { bounds = _bounds },
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }

        /// <summary>
        /// Clears one half-cell slot when present: zero positions turn the quad degenerate and the
        /// slot stays reserved. O(1); returns whether a shape was actually removed.
        /// </summary>
        public bool ClearHalfCell(int halfCellX, int halfCellY, int gridWidth)
        {
            int halfCell = halfCellY * gridWidth + halfCellX;
            if (!_slotByHalfCell.TryGetValue(halfCell, out int slot))
            {
                return false;
            }

            if (!SlotOccupied(slot))
            {
                return false;
            }

            for (int i = 0; i < 4; i++)
            {
                TerrainVertex vertex = _vertices[slot * 4 + i];
                vertex.Position = Vector3.zero;
                _vertices[slot * 4 + i] = vertex;
            }

            _usedSlots--;
            Touch(slot * 4, slot * 4 + 4);
            return true;
        }

        /// <summary>Uploads the dirty vertex span. Called once per mutation flush, after all writes.</summary>
        public void FlushUploads()
        {
            if (_dirtyFrom >= _dirtyTo)
            {
                return;
            }

            int count = Mathf.Min(_dirtyTo, _vertices.Length) - _dirtyFrom;
            Mesh.SetVertexBufferData(
                _vertices, _dirtyFrom, _dirtyFrom, count, 0,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

            _dirtyFrom = int.MaxValue;
            _dirtyTo = 0;
        }

        void Touch(int from, int to)
        {
            if (from < _dirtyFrom) _dirtyFrom = from;
            if (to > _dirtyTo) _dirtyTo = to;
        }

        /// <summary>
        /// Destroys the GameObject and the mesh. Safe to call twice; edit-mode safe, because the
        /// A/B preview builds and tears these down outside play mode.
        /// </summary>
        public void Destroy()
        {
            var mesh = Mesh;
            var go = GameObject;
            Mesh = null;
            GameObject = null;
            _slotByHalfCell.Clear();
            _usedSlots = 0;
            _slotCount = 0;
            _dirtyFrom = int.MaxValue;
            _dirtyTo = 0;

            if (Application.isPlaying)
            {
                if (mesh != null) Object.Destroy(mesh);
                if (go != null) Object.Destroy(go);
            }
            else
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
                if (go != null) Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// The four vertices of one placed mask shape, in the layer's local space. Pure — this is
        /// the arithmetic the fixtures pin, and the reason the mesh can claim parity with the
        /// Tilemap it replaced: it is the same TRS composition the Tilemap applied to the same
        /// sprite, with the pivot landing on the half-cell origin plus the placement's translation.
        /// </summary>
        public static void WriteQuad(
            Rect rect,
            Vector2 pivotPixels,
            float pixelsPerUnit,
            int textureWidth,
            int textureHeight,
            int cellX,
            int cellY,
            float quarterSize,
            Vector2 translation,
            Vector2 scale,
            Color color,
            TerrainVertex[] buffer,
            int offset)
        {
            if (buffer == null || buffer.Length < offset + 4)
            {
                return;
            }

            // The sprite pivot lands on the half-cell origin plus the placement translation —
            // exactly what `tileAnchor = zero` + `SetTransformMatrix(TRS(translation, …))` did.
            var pivotLocal = new Vector3(
                cellX * quarterSize + translation.x,
                cellY * quarterSize + translation.y,
                0f);

            // Sprite-local corners relative to the pivot, divided to world units, then scaled by
            // the placement (whose sign carries the quarter mirrors and whose magnitude carries the
            // height squash and the mask fit factor).
            var bottomLeft = new Vector2(-pivotPixels.x / pixelsPerUnit, -pivotPixels.y / pixelsPerUnit);
            var topRight = new Vector2(
                (rect.width - pivotPixels.x) / pixelsPerUnit,
                (rect.height - pivotPixels.y) / pixelsPerUnit);

            float w = Mathf.Max(1, textureWidth);
            float h = Mathf.Max(1, textureHeight);
            var uvMin = new Vector2(rect.x / w, rect.y / h);
            var uvMax = new Vector2((rect.x + rect.width) / w, (rect.y + rect.height) / h);

            // Corner order matches the shipped corner order everywhere in this pipeline:
            // bottom-left, top-left, top-right, bottom-right — clockwise from the camera, making
            // the two triangles (0,1,2) and (0,2,3) that the index buffer references.
            WriteVertex(buffer, offset + 0, PivotOffset(pivotLocal, bottomLeft, scale), new Vector2(uvMin.x, uvMin.y), color);
            WriteVertex(buffer, offset + 1, PivotOffset(pivotLocal, new Vector2(bottomLeft.x, topRight.y), scale), new Vector2(uvMin.x, uvMax.y), color);
            WriteVertex(buffer, offset + 2, PivotOffset(pivotLocal, topRight, scale), new Vector2(uvMax.x, uvMax.y), color);
            WriteVertex(buffer, offset + 3, PivotOffset(pivotLocal, new Vector2(topRight.x, bottomLeft.y), scale), new Vector2(uvMax.x, uvMin.y), color);
        }

        static Vector3 PivotOffset(Vector3 pivotLocal, Vector2 corner, Vector2 scale)
        {
            return new Vector3(
                pivotLocal.x + corner.x * scale.x,
                pivotLocal.y + corner.y * scale.y,
                0f);
        }

        static void WriteVertex(TerrainVertex[] buffer, int index, Vector3 position, Vector2 uv, Color color)
        {
            buffer[index] = new TerrainVertex { Position = position, UV = uv, Color = color };
        }
    }
}
