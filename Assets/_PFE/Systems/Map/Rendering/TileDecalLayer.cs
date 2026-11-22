using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

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
    /// <para><b>Why this is one mesh instead of a pool of sprites.</b> The port's first answer was a
    /// pooled <see cref="SpriteRenderer"/> per mark, because neither terrain path can accept an
    /// arbitrary-pixel stamp. That reproduces the picture but not the cost: every hit created a
    /// GameObject plus a renderer — <c>Batches</c> is ~1 per renderer (rule 4 in the tile perf notes),
    /// so sustained fire raised the room's draw-call count one hit at a time. One dynamic quad mesh
    /// with a fixed slot per mark is the same picture for one renderer: a stamp writes four vertices
    /// in place, the index buffer is uploaded once, and an erased mark is a zero-area quad, exactly
    /// the shape <see cref="RoomTerrain.RoomTerrainRenderer"/> uses for its terrain layers.</para>
    ///
    /// <para><b>Two divergences, both deliberate and both visible:</b></para>
    /// <list type="number">
    /// <item><description><b>The erase pass is not an erase.</b> AS3 draws the hole sprite with
    /// <c>BlendMode.ERASE</c>, which removes alpha from the wall bitmap and lets the backdrop show
    /// through. A decal drawn on top cannot subtract from what is behind it, so the hole is simply not
    /// drawn. The scorch still lands correctly; what is lost is the see-through centre of a bullet hole.
    /// Faithfully reproducing it needs a stencil or a camera-colour grab, not a different mesh.</description></item>
    /// <item><description><b>Hardlight is composited as straight alpha.</b> Hardlight needs the
    /// destination colour, which a forward sprite pass does not have. The exported decals already carry
    /// their own burn (dark ring, bright core), so alpha compositing reproduces most of the look and
    /// loses only the highlight blow-out.</description></item>
    /// </list>
    ///
    /// <para><b>The cap.</b> A bitmap costs the same whether it holds one decal or ten thousand, so the
    /// oracle has no limit. Mesh slots do not: a room under sustained fire would grow a quad per hit
    /// for as long as the player stands there. <see cref="MaxDecals"/> is therefore still a ring
    /// buffer — the oldest mark is recycled — and the whole buffer is pre-allocated at that cap, so the
    /// ring never allocates. It is a resource bound, not a rule.</para>
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

        /// <summary>
        /// One mark's CPU-side state. The GPU half lives in the shared mesh at the same index:
        /// slot i owns vertices <c>[i*4 .. i*4+3]</c>. Only the position is kept — <see cref="ClearCell"/>
        /// tests it against the cell rect, which is why the erase pass does not need to know the quad.
        /// </summary>
        struct DecalSlot
        {
            public bool Active;
            public Vector3 WorldPosition;
        }

        /// <summary>
        /// One vertex of the decal mesh. <c>Sprite-Lit-Default</c> — the material a bare
        /// <see cref="SpriteRenderer"/> takes in this project — declares POSITION, NORMAL, COLOR and
        /// TEXCOORD0, so the mesh carries all four even though the colour is always white: the lit
        /// sprite shader is only the shipped decal look while its vertex contract is met.
        ///
        /// <para><b>Declared in Unity's standard order.</b> It was position, UV, normal, colour, which
        /// made Unity log "Mesh vertex buffer attributes were supplied in non-standard order. Layout
        /// was adjusted to this order: Position, Normal, Color, TexCoord0". That is a repack notice,
        /// not a correctness one — <c>CharacterQuadVertex</c> keeps the same non-standard order and its
        /// characters render correctly — so this reorder buys a clean log, not a fix. The offsets are
        /// still load-bearing: <c>SetVertexBufferData</c> uploads the struct verbatim, so position at
        /// 0, normal at 12, colour at 24, UV at 40, 48 bytes a vertex.
        /// <c>RoomTerrainMeshLayer.TerrainVertex</c> carries the same note.</para>
        /// </summary>
        struct DecalVertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Color Color;
            public Vector2 UV;

            public static readonly VertexAttributeDescriptor[] Layout =
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            };
        }

        readonly List<DecalSlot> _slots = new List<DecalSlot>();
        int _next;

        GameObject _meshRoot;
        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Mesh _mesh;
        DecalVertex[] _vertices;
        int[] _indices;
        int _touchedFrom = int.MaxValue;
        int _touchedTo;

        /// <summary>How many decal slots this room has allocated. Diagnostics and fixtures.</summary>
        public int Count => _slots.Count;

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
        /// <returns>True when the mark landed, false when the sprite is missing.</returns>
        public bool Stamp(
            string spriteName, Vector3 worldPosition, float scale, float rotationRadians, float frameRoll)
        {
            if (string.IsNullOrEmpty(spriteName)) return false;

            Sprite[] frames = LoadFrames(spriteName);
            if (frames == null || frames.Length == 0) return false;

            int index = Mathf.Clamp(Mathf.FloorToInt(frameRoll * frames.Length), 0, frames.Length - 1);
            Sprite sprite = frames[index];
            if (sprite == null) return false;

            EnsureMesh();
            if (_vertices == null) return false;

            // A mesh that never reached the filter draws nothing and reports nothing, and `EnsureMesh`
            // bails out on every call after the first — so without this check a failure inside it turns
            // all later stamps into a silent no-op. That is exactly how the index-buffer size mismatch
            // above hid: one throw on the first hit, then a room full of decals that did not exist.
            if (_meshFilter == null || _meshFilter.sharedMesh == null) return false;

            int slot = Take();
            WriteQuad(slot, sprite, worldPosition, scale, rotationRadians * Mathf.Rad2Deg);
            _slots[slot] = new DecalSlot { Active = true, WorldPosition = worldPosition };
            Touch(slot, slot + 1);
            Flush();
            return true;
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
        /// only where the wall was not erased. A decal cannot be clipped by what is behind it, so the
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
        /// that wall is blown away — and so does a neighbour's debris rim that overhung the cell. Every
        /// decal's world position is kept on its slot, so "disable what is centred here" is the same
        /// rule in the same order without a renderer per mark.</para>
        /// </summary>
        public int ClearCell(Vector3 cellCentre, float cellSize)
        {
            float half = cellSize * 0.5f;
            int dropped = 0;

            for (int i = 0; i < _slots.Count; i++)
            {
                DecalSlot slot = _slots[i];
                if (!slot.Active) continue;

                Vector3 p = slot.WorldPosition;
                if (Mathf.Abs(p.x - cellCentre.x) >= half) continue;
                if (Mathf.Abs(p.y - cellCentre.y) >= half) continue;

                ZeroQuad(i);
                _slots[i] = new DecalSlot();
                dropped++;
            }

            if (dropped > 0)
            {
                Touch(0, _slots.Count);
                Flush();
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
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Active) ZeroQuad(i);
                _slots[i] = new DecalSlot();
            }
            _next = 0;
            Flush();
        }

        private int Take()
        {
            if (_slots.Count < MaxDecals)
            {
                // The mesh is pre-allocated to the cap, so a new slot is a list add plus four
                // vertices that were zeroed at creation.
                _slots.Add(new DecalSlot());
                return _slots.Count - 1;
            }

            // Ring buffer: the oldest mark gives way. Advancing before use means the very first
            // recycle throws away decal 0 rather than decal MaxDecals-1.
            int recycled = _next;
            _next = (_next + 1) % _slots.Count;
            return recycled;
        }

        /// <summary>
        /// Writes one mark's quad into the vertex buffer, in the mesh's local space. The composed
        /// transform is exactly what the old per-mark <see cref="SpriteRenderer"/> carried —
        /// position, Z rotation, uniform scale — pushed through the layer's
        /// <c>worldToLocalMatrix</c>, so a stamped mark lands identically whether the room root is
        /// offset, rotated or scaled.
        /// </summary>
        private void WriteQuad(int slot, Sprite sprite, Vector3 worldPosition, float scale, float rotationDegrees)
        {
            if (_vertices == null) return;

            Rect rect = sprite.rect;
            float ppu = sprite.pixelsPerUnit;
            if (ppu <= 0f) ppu = 100f;

            // Sprite-local corners, y up, measured from the pivot — Sprite.Create-style placement
            // with the imported pivot, which for the decal frames is the canvas centre.
            var bottomLeft = new Vector3(-sprite.pivot.x / ppu, -sprite.pivot.y / ppu, 0f);
            var topRight = new Vector3(
                (rect.width - sprite.pivot.x) / ppu,
                (rect.height - sprite.pivot.y) / ppu,
                0f);

            Matrix4x4 local = transform.worldToLocalMatrix * Matrix4x4.TRS(
                worldPosition,
                Quaternion.Euler(0f, 0f, rotationDegrees),
                new Vector3(scale, scale, 1f));

            Texture texture = sprite.texture;
            if (_meshRenderer != null && _meshRenderer.sharedMaterial != null && _meshRenderer.sharedMaterial.mainTexture == null && texture != null)
            {
                _meshRenderer.sharedMaterial.mainTexture = texture;
            }

            float texWidth = texture != null ? texture.width : 1f;
            float texHeight = texture != null ? texture.height : 1f;
            var uvMin = new Vector2(rect.x / texWidth, rect.y / texHeight);
            var uvMax = new Vector2((rect.x + rect.width) / texWidth, (rect.y + rect.height) / texHeight);

            int vb = slot * 4;
            WriteVertex(vb + 0, local.MultiplyPoint3x4(bottomLeft), new Vector2(uvMin.x, uvMin.y));
            WriteVertex(vb + 1, local.MultiplyPoint3x4(new Vector3(bottomLeft.x, topRight.y, 0f)), new Vector2(uvMin.x, uvMax.y));
            WriteVertex(vb + 2, local.MultiplyPoint3x4(topRight), new Vector2(uvMax.x, uvMax.y));
            WriteVertex(vb + 3, local.MultiplyPoint3x4(new Vector3(topRight.x, bottomLeft.y, 0f)), new Vector2(uvMax.x, uvMin.y));
        }

        private void WriteVertex(int index, Vector3 position, Vector2 uv)
        {
            _vertices[index] = new DecalVertex
            {
                Position = position,
                UV = uv,
                Normal = new Vector3(0f, 0f, -1f),
                Color = Color.white,
            };
        }

        /// <summary>Zeros one quad's four positions, turning both triangles degenerate.</summary>
        private void ZeroQuad(int slot)
        {
            if (_vertices == null) return;

            int vb = slot * 4;
            for (int i = 0; i < 4; i++)
            {
                DecalVertex vertex = _vertices[vb + i];
                vertex.Position = Vector3.zero;
                _vertices[vb + i] = vertex;
            }
        }

        private void Touch(int fromSlot, int toSlotExclusive)
        {
            int from = fromSlot * 4;
            int to = toSlotExclusive * 4;
            if (from < _touchedFrom) _touchedFrom = from;
            if (to > _touchedTo) _touchedTo = to;
        }

        /// <summary>
        /// Uploads the touched vertex range and refreshes bounds and visibility. Stamps are
        /// event-driven (tile hits), so this runs once per mutation burst, not per frame.
        /// </summary>
        private void Flush()
        {
            if (_mesh == null || _meshRenderer == null)
            {
                return;
            }

            if (_touchedFrom < _touchedTo)
            {
                int count = Mathf.Min(_touchedTo, _vertices.Length) - _touchedFrom;
                _mesh.SetVertexBufferData(
                    _vertices, _touchedFrom, _touchedFrom, count, 0,
                    MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                _touchedFrom = int.MaxValue;
                _touchedTo = 0;
            }

            // Bounds over the active slots, recomputed on every mutation: at most MaxDecals
            // iterations, and it keeps the culler honest without Unity recalculating per upload.
            var bounds = new Bounds(transform.position, Vector3.zero);
            bool any = false;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (!_slots[i].Active) continue;
                Vector3 local = transform.InverseTransformPoint(_slots[i].WorldPosition);
                if (any) bounds.Encapsulate(local);
                else { bounds = new Bounds(local, Vector3.zero); any = true; }
            }

            if (!any)
            {
                _meshRenderer.enabled = false;
                return;
            }

            // One decal spans at most a couple of cells; the margin is generous on purpose.
            bounds.Expand(4f);
            _mesh.bounds = bounds;

            // The submesh range is fixed (every quad is always addressable, unused ones are degenerate)
            // but its bounds are not: they were declared empty at creation, so they have to follow the
            // live slots or the range is culled as an empty box. Same value as `_mesh.bounds`, from the
            // same pass, so the two cannot drift.
            _mesh.SetSubMesh(
                0,
                new SubMeshDescriptor(0, _indices.Length, MeshTopology.Triangles) { bounds = bounds },
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

            _meshRenderer.enabled = true;
        }

        /// <summary>
        /// Creates the single mesh child on first stamp, so a decal-free room pays nothing. The
        /// material is the lit sprite shader — what a bare <see cref="SpriteRenderer"/> takes in this
        /// project — because the decals have always been lit by the scene's global light and an
        /// unlit material would visibly brighten them.
        /// </summary>
        private void EnsureMesh()
        {
            if (_meshRoot != null)
            {
                return;
            }

            _meshRoot = new GameObject("DecalMesh");
            _meshRoot.transform.SetParent(transform, false);
            _meshRoot.layer = gameObject.layer;

            if (!Application.isPlaying)
            {
                _meshRoot.hideFlags = HideFlags.DontSave;
            }

            _meshFilter = _meshRoot.AddComponent<MeshFilter>();
            _meshRenderer = _meshRoot.AddComponent<MeshRenderer>();
            _meshRenderer.enabled = false;
            _meshRenderer.sortingLayerName = MapSortingLayers.MainTiles;
            _meshRenderer.sortingOrder = DecalSortingOrder;
            _meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;

            Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default")
                         ?? Shader.Find("Sprites/Default");

            if (shader == null)
            {
                Debug.LogError(
                    "[TileDecalLayer] No sprite shader found; tile decals will not draw. " +
                    "Expected Universal Render Pipeline/2D/Sprite-Lit-Default.");
            }
            else
            {
                var material = new Material(shader) { name = "TileDecalLayer:decals" };
                material.SetColor("_Color", Color.white);
                _meshRenderer.sharedMaterial = material;
            }

            int vertexCount = MaxDecals * 4;
            int indexCount = MaxDecals * 6;

            _mesh = new Mesh
            {
                name = "TileDecalLayer",
                hideFlags = HideFlags.HideAndDontSave,
            };

            _vertices = new DecalVertex[vertexCount];
            _indices = new int[indexCount];
            for (int quad = 0; quad < MaxDecals; quad++)
            {
                int v = quad * 4;
                int t = quad * 6;
                _indices[t + 0] = v + 0;
                _indices[t + 1] = v + 1;
                _indices[t + 2] = v + 2;
                _indices[t + 3] = v + 0;
                _indices[t + 4] = v + 2;
                _indices[t + 5] = v + 3;
            }

            _mesh.MarkDynamic();
            _mesh.SetVertexBufferParams(vertexCount, DecalVertex.Layout);

            // Same invariant as RoomTerrainMeshLayer's constructor: the index buffer uploaded below
            // draws all MaxDecals quads from the first stamp onwards, so every slot must be a defined
            // degenerate quad before the renderer enables. SetVertexBufferParams promises nothing
            // about content, and the first stamp's partial upload would otherwise leave hundreds of
            // never-stamped slots rasterizing whatever the memory happened to hold — full-screen
            // garbage that differs every run. The managed array is zeroed at creation; upload it once.
            // (The allocation sits above this call — passing a null array here is the NRE the
            // projectile-impact path surfaced.)
            _mesh.SetVertexBufferData(
                _vertices, 0, 0, vertexCount, 0,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

            // UInt32, NOT UInt16. `SetIndexBufferData` copies the C# element type verbatim — it does not
            // narrow an `int[]` to 16-bit indices. Declaring UInt16 sized the native buffer at
            // `indexCount * 2` bytes while the upload below wrote `indexCount * 4`, so the very first
            // stamp threw "Accessing 12288 bytes at offset 0 for mesh index buffer of size 6144 bytes"
            // (512 quads x 6 = 3072 indices) and no decal in the room was ever recorded. UInt16 is only
            // safe against a `ushort[]`; UInt32 is what `RoomTerrainMeshLayer` uses for the same `int[]`.
            _mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
            _mesh.SetIndexBufferData(_indices, 0, 0, indexCount);

            // The same missing-submesh trap as `RoomTerrainMeshLayer.UploadIndexBuffer`: allocating the
            // index buffer does not give submesh 0 a range, and a mesh whose submesh range is empty
            // renders nothing while looking perfectly healthy from the C# side. Bounds start empty and
            // are refreshed from the live slots in `Flush`.
            _mesh.SetSubMesh(
                0,
                new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles),
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

            _meshFilter.sharedMesh = _mesh;
        }

        private void OnDestroy()
        {
            if (_meshRenderer != null && _meshRenderer.sharedMaterial != null)
            {
                DestroyObject(_meshRenderer.sharedMaterial);
            }

            if (_mesh != null)
            {
                DestroyObject(_mesh);
                _mesh = null;
            }

            if (_meshRoot != null)
            {
                DestroyObject(_meshRoot);
                _meshRoot = null;
                _meshFilter = null;
                _meshRenderer = null;
            }
        }

        /// <summary>
        /// <c>Destroy</c> throws in edit mode, and <c>DestroyImmediate</c> is illegal while playing —
        /// the same seam, and same reason, as <c>RoomTerrainRenderer.DestroyObject</c>.
        /// <para><c>new</c> is required: <c>UnityEngine.Object</c> has a deprecated
        /// <c>DestroyObject</c> of its own.</para>
        /// </summary>
        private static new void DestroyObject(UnityEngine.Object target)
        {
            if (target == null) return;

            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
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