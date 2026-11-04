using System.Collections.Generic;
using PFE.Character.Animation;
using PFE.Data.Definitions;
using UnityEngine;
using UnityEngine.Rendering;

namespace PFE.Character
{
    /// <summary>
    /// The character renderer that draws <b>baked quads</b> instead of <see cref="SpriteRenderer"/>s —
    /// the shape of pipeline Remains-Unity uses, running beside the shipped
    /// <see cref="CharacterSpriteAssembler"/> rather than instead of it.
    ///
    /// <para><b>What is different from the shipped path, in one place.</b></para>
    /// <list type="number">
    /// <item><b>Geometry is four vertices, not a sprite.</b> The registration point is resolved to
    /// pixels in the full PNG and the trimmed rect is offset against it
    /// (<see cref="CharacterQuadGeometry"/>), so a part whose PNG was alpha-trimmed no longer lands
    /// somewhere else than the importer meant. The shipped path cannot do this because
    /// <c>Sprite.Create</c> always reads the pivot as a fraction of the trimmed rect.</item>
    /// <item><b>The quad is the whole rect.</b> No <c>Tight</c> contour, so the anti-aliased rim the
    /// vector rasteriser produced survives.</item>
    /// <item><b>Sampling can be bilinear</b> without touching a single import setting — the texture is
    /// copied GPU-side and the copy carries the filter (<see cref="CharacterQuadMaterialCache"/>).</item>
    /// <item><b>Nothing is allocated per frame.</b> One reusable <see cref="HashSet{T}"/> for the armour
    /// head-part filter, no <c>Sprite.Create</c>, no <c>SetActive</c>; a hidden slot is
    /// <c>renderer.enabled = false</c>.</item>
    /// </list>
    ///
    /// <para><b>Why it is a separate component and not a mode inside the old one.</b> The shipped
    /// assembler is on the player prefab and is load-bearing; a regression there is a regression in
    /// the game. This component is opt-in: the preview windows drive it, and wiring it into
    /// <c>PlayerCharacterVisual</c> is a deliberate later step, not something this class does by
    /// existing. Its public surface deliberately mirrors <see cref="CharacterSpriteAssembler"/>
    /// (<c>Setup</c>, <c>Appearance</c>, <c>VisualContext</c>, <c>SortingLayerName</c>,
    /// <c>SetState</c>, <c>AdvanceFrame</c>, <c>Definition</c>, <c>StyleData</c>,
    /// <c>CurrentFrame</c>, <c>CurrentState</c>) so that step is a type swap and nothing else.</para>
    ///
    /// <para><b>What it does not do yet.</b> Lamp falloff. <see cref="Ambient"/> multiplies every part
    /// by the room's single ambient number — the same number the background tiles use — which is the
    /// large visible half of "the character sits in the room's light". A per-pixel lamp term needs the
    /// visibility buffer written to a texture and a shader that samples it; that is a separate change
    /// with a separate measurement, and guessing at it here would put an unverifiable curve in the
    /// path.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CharacterQuadAssembler : MonoBehaviour
    {
        /// <summary>
        /// Head sub-parts that a composite <c>morda_armor</c> replaces. Kept identical to
        /// <see cref="CharacterSpriteAssembler"/>'s copy on purpose: the two renderers must agree about
        /// which parts exist or an A/B between them compares different characters.
        /// </summary>
        static readonly HashSet<string> HeadSubPartNames = new()
        {
            "morda_base", "morda_base_back", "shell", "morda_overlay", "eye", "forelock",
            "horn", "magic", "konec", "helm", "helm2"
        };

        /// <summary>Two triangles, clockwise from the camera: (bl, tl, tr) and (bl, tr, br).</summary>
        static readonly int[] QuadTriangles = { 0, 1, 2, 0, 2, 3 };

        [Header("Data")]
        [SerializeField] CharacterAnimationDefinition _definition;
        [SerializeField] CharacterStyleData _styleData;

        [Header("Rendering")]
        [Tooltip("Sorting layer applied to every part renderer. Must exist in Project Settings → Tags & Layers.")]
        [SerializeField] string _sortingLayerName = "Default";

        [Header("Current Appearance")]
        [SerializeField] CharacterAppearance _appearance;
        [SerializeField] CharacterVisualContext _visualContext = default;
        [SerializeField] bool _useFlashTint = true;

        [Header("Quad renderer")]
        [Tooltip("A/B switch for the rejected registration convention: resolve the pivot against the full PNG " +
                 "instead of the trimmed rect. Off is correct and is what ships — the rect is the frame's " +
                 "alpha-trim box, which is the box the pivot was derived from. On displaces every trimmed " +
                 "part (rwing by 0.325 units). See docs/CharacterImportPipeline/08.")]
        [SerializeField] bool _compensatePivot = false;

        [Tooltip("Sample a GPU-side bilinear copy of each part texture. The imported textures are Point.")]
        [SerializeField] bool _bilinear = true;

        [Tooltip("Pack every part texture into one atlas and draw the whole character with one material. " +
                 "Turns 21-26 draw calls into one batch. Off falls back to one material per texture, " +
                 "which is what the shipped sprite path does. " +
                 "Default off since 2026-10-21: the atlas is the only structure here that the reference " +
                 "port does not have, and it is currently the only suspect for the flat tinted blocks in " +
                 "docs/CharacterImportPipeline/04.")]
        [SerializeField] bool _useAtlas;

        [Tooltip("Give the atlas a mip chain. Off by default: with it on this driver returned an atlas " +
                 "whose regions held half-size fragments and the build logged an invalid-mip " +
                 "CopyTexture. Off is level 0 only, which is what the atlas did before the chain " +
                 "existed. See AtlasMips.")]
        [SerializeField] bool _atlasMips;

        [Tooltip("Room ambient multiplier applied to every part. 1 = full brightness. " +
                 "Use CharacterRenderAmbient.FromDarkness(room.darkness) to match the background tiles.")]
        [SerializeField, Range(0f, 1f)] float _ambient = 1f;

        readonly List<QuadSlot> _slotPool = new();
        readonly List<QuadOverlaySlot> _overlaySlots = new();

        // ─── Consolidated mesh (one draw call per character) ────────────────
        //
        // Every visible part quad is written into a single dynamic mesh. The mesh has
        // maxSlots * 4 vertices and maxSlots * 6 indices, pre-allocated at build time.
        // Each frame, ApplyFrame writes the visible slots' vertices and then updates
        // the index buffer to include only the visible quads. The renderer draws
        // whatever the index buffer describes.
        //
        // Why one mesh and not one per slot: 26 parts × (GameObject + MeshFilter +
        // MeshRenderer + Mesh) is the overhead the sprite path pays; the quad path
        // was designed to avoid it. One mesh also means one draw call when the atlas
        // is active, and one draw call per material when it is not.

        /// <summary>The single GameObject that carries the consolidated mesh renderer.</summary>
        GameObject _meshRoot;
        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Mesh _sharedMesh;

        /// <summary>Pre-allocated vertex buffers, sized to maxSlots * 4.</summary>
        Vector3[] _allVertices;
        Vector2[] _allUvs;
        Color[] _allColors;
        int[] _allTriangles;

        /// <summary>How many slots the shared mesh can hold (primary + secondary + overlays).</summary>
        int _meshCapacity;

        /// <summary>How many quads were written into the shared mesh this frame.</summary>
        int _activeQuadCount;

        /// <summary>
        /// The node the part quads hang from — AS3's <c>osn</c> child of the outer visual symbol, the
        /// one that carries the placement's scale. <b>This is what the quad path was missing.</b>
        ///
        /// <para><b>Why a node and not a number in the vertex maths.</b>
        /// <see cref="CharacterSpriteAssembler.EnsureBodyRoot"/> gives its part slots this same node,
        /// so the shipped path draws every part at <c>rect / ppu × bodyScale</c> and every part
        /// <i>position</i> at <c>localPosition × bodyScale</c>. The quad path baked the placement into
        /// the vertices and never read <see cref="CharacterAnimationDefinition.BodyScale"/> at all —
        /// measured on the player, whose <c>bodyScale</c> is 1.2999878, that drew the whole character
        /// at <b>1/1.3 = 0.769</b> of the sprite side: every part too small and every part too close
        /// to the origin, which is the "the atlas side is smaller" reading in the A/B.
        /// Reproducing the node rather than multiplying two numbers here means a non-uniform or
        /// mirrored <c>bodyScale</c> composes the way a parent transform does, and the two paths stay
        /// comparable part-for-part instead of agreeing only while the scale happens to be uniform.</para>
        ///
        /// <para><b>Overlays do not hang from it.</b> The shipped path parents its overlay slots to
        /// <c>transform</c> (:601), not to the body root, because an overlay is a sibling of <c>osn</c>
        /// in the original. Overlays keep that here.</para>
        /// </summary>
        Transform _bodyRoot;

        /// <summary>
        /// Reused every frame. The shipped path allocates one of these per animation frame per
        /// character, which is the single largest piece of garbage in its draw path.
        /// </summary>
        readonly HashSet<string> _hiddenHeadParts = new();

        readonly CharacterQuadMaterialCache _materials = new();

        /// <summary>
        /// Cached <see cref="CharacterQuadSource"/> per sprite, so <c>FromSprite</c> is not called
        /// repeatedly for the same sprite. A sprite's textureRect, texture dimensions and PPU are
        /// stable for the lifetime of the asset, so this cache never invalidates.
        /// </summary>
        readonly Dictionary<Sprite, CharacterQuadSource> _sourceCache = new();

        /// <summary>
        /// The one-texture, one-material view of this character. Null when atlasing is off, when the
        /// definition has no sprites, or when the build failed — in all three cases the per-texture
        /// materials still draw the character, so this is an optimisation and never a dependency.
        /// </summary>
        CharacterQuadAtlas _atlas;

        /// <summary>
        /// Bumped whenever the atlas is replaced. Slots compare against it so a rebuilt atlas rewrites
        /// their UVs even when the sprite on a slot did not change — otherwise a rebuild would leave
        /// every slot sampling the old atlas' coordinates, which is a wrong picture with no error.
        /// </summary>
        int _atlasVersion;

        float _overlayTimer;

        /// <summary>Same ceiling as the shipped path: a paused editor must not fast-forward a clip.</summary>
        const int MaxOverlayStepsPerTick = 8;

        string _currentState;
        int _currentFrame;
        bool _assembled;

        /// <summary>
        /// Horn aura charge, 0..100 — see <see cref="HornAura"/>. Same default as the shipped path
        /// (off) so the two sides of the A/B start from the same picture.
        /// </summary>
        float _hornAuraLevel = HornAura.LevelIdle;

        // ─── Public API (mirrors CharacterSpriteAssembler) ───────────────────

        public CharacterAppearance Appearance
        {
            get => _appearance;
            set
            {
                _appearance = value;
                if (_assembled) ApplyAppearance();
            }
        }

        public CharacterVisualContext VisualContext
        {
            get => _visualContext;
            set
            {
                string previousArmor = _visualContext.armorId;
                _visualContext = value;

                // Only the worn armour set is in the atlas (see CollectSprites), so changing it changes
                // what the atlas must contain. Every other field in the context is a visibility flag
                // and only needs a re-draw.
                if (!string.Equals(previousArmor, _visualContext.armorId))
                {
                    if (_assembled) InvalidateAtlas();
                    return;
                }

                if (_assembled) ApplyAppearance();
            }
        }

        public bool UseFlashTint
        {
            get => _useFlashTint;
            set
            {
                _useFlashTint = value;
                if (_assembled) ApplyAppearance();
            }
        }

        public string SortingLayerName
        {
            get => _sortingLayerName;
            set
            {
                if (_sortingLayerName == value) return;
                _sortingLayerName = value;
                ApplySortingLayerToAllSlots();
            }
        }

        public CharacterAnimationDefinition Definition
        {
            get => _definition;
            set
            {
                _definition = value;
                Rebuild();
            }
        }

        public CharacterStyleData StyleData
        {
            get => _styleData;
            set
            {
                _styleData = value;
                if (_assembled) ApplyAppearance();
            }
        }

        /// <summary>
        /// Resolve the pivot against the full PNG. Public so the preview window can falsify the
        /// hypothesis live: with this off, the quad path must agree with the sprite path to the pixel.
        /// </summary>
        public bool CompensatePivot
        {
            get => _compensatePivot;
            set
            {
                if (_compensatePivot == value) return;
                _compensatePivot = value;
                InvalidateGeometry();
            }
        }

        /// <summary>Sample bilinear copies instead of the Point-imported textures.</summary>
        public bool Bilinear
        {
            get => _bilinear;
            set
            {
                if (_bilinear == value) return;
                _bilinear = value;

                // The atlas is filtered by this flag, so it has to be rebuilt, not just re-drawn.
                InvalidateAtlas();
            }
        }

        /// <summary>
        /// Draw every part from one atlas texture with one material, so the character batches.
        ///
        /// <para>Off restores one material per part texture, which is what the shipped sprite path does
        /// and what the A/B window needs in order to show the difference. Turning it on or off rebuilds
        /// the atlas immediately, so the switch is a fair comparison rather than a restart.</para>
        /// </summary>
        public bool AtlasEnabled
        {
            get => _useAtlas;
            set
            {
                if (_useAtlas == value) return;
                _useAtlas = value;
                InvalidateAtlas();
            }
        }

        /// <summary>The live atlas, or null. Exposed so the preview window can report its size and how
        /// many regions it replaced instead of asserting that it worked.</summary>
        public CharacterQuadAtlas Atlas => _atlas;

        /// <summary>
        /// How many parts the last drawn frame took from the worn armour set.
        /// </summary>
        /// <remarks>
        /// The A/B could not tell "the costume did not reach this side" from "the costume reached it and
        /// the pixels are wrong", because both look like a part drawn in the wrong clothes. This counts
        /// the decision instead of the result: the sprite side and the quad side must report the same
        /// number for the same frame, and the window prints both.
        /// </remarks>
        public int ArmourPartsDrawn { get; private set; }

        /// <summary>How many quads were written into the shared mesh on the last frame.</summary>
        public int ActiveQuadCount => _activeQuadCount;

        /// <summary>How many slots the shared mesh can hold (primary + secondary + overlays).</summary>
        public int MeshCapacity => _meshCapacity;

        /// <summary>Whether the shared mesh renderer is currently enabled (i.e. something was drawn).</summary>
        public bool IsMeshRendererActive => _meshRenderer != null && _meshRenderer.enabled;

        /// <summary>
        /// Give the atlas a mip chain. <b>Off by default.</b>
        ///
        /// <para><b>The measurement, and what it changed.</b> With the chain on, the dumped atlas
        /// (<c>Temp/quad_atlas_dump</c>) held every part at roughly <b>half size, anchored in its
        /// region's corner</b> — the widest run of opaque pixels in the whole 1024² texture was
        /// <b>63 px</b> while the parts' own rects are 86–124 px wide — one region was <b>empty</b>, and
        /// the build logged <c>Graphics.CopyTexture called with invalid source mip level: got 4, have
        /// 4 mips</c>. On screen that is a character made of small detached fragments, which is exactly
        /// what the A/B showed.</para>
        ///
        /// <para><b>That turned out not to be the chain.</b> The same half-size regions appeared with
        /// the chain <i>off</i>; the real cause was the region copy itself, and the fix is a staging
        /// copy through a render texture — see <see cref="CharacterQuadAtlas.Build"/>. What the log
        /// lines did prove is that the per-level copy loop could never be made correct: it needed each
        /// source texture's level count, and <c>Texture.mipmapCount</c> over-reports (`got 4, have 4
        /// mips` for a texture that scored 4). So the loop is gone and the chain, when asked for, is
        /// generated from the finished level 0 by <c>Apply(true)</c>.</para>
        ///
        /// <para><b>Why off is still the default.</b> The character is drawn at 0.72×–1.08× on a 1080p
        /// screen, so the mips are almost never sampled; a chain is a correctness risk for no measured
        /// gain. Kept as a switch so the claim can be checked rather than taken on trust.</para>
        /// </summary>
        public bool AtlasMips
        {
            get => _atlasMips;
            set
            {
                if (_atlasMips == value) return;
                _atlasMips = value;
                InvalidateAtlas();
            }
        }

        /// <summary>
        /// Horn aura charge, 0..100, in the original's units — see <see cref="HornAura"/>. Mirrors
        /// <see cref="CharacterSpriteAssembler.HornAuraLevel"/> so the A/B window can drive both sides
        /// with one slider and any difference that survives is a difference between the renderers.
        /// </summary>
        public float HornAuraLevel
        {
            get => _hornAuraLevel;
            set
            {
                float clamped = Mathf.Clamp(value, HornAura.LevelIdle, HornAura.LevelMax);
                if (Mathf.Approximately(_hornAuraLevel, clamped))
                {
                    return;
                }

                _hornAuraLevel = clamped;
                if (_assembled) ReapplyCurrentFrame();
            }
        }

        /// <summary>
        /// Room ambient, 0..1, multiplied into every part's vertex colour. Overlays are excluded — the
        /// shield is emissive magic, and the shipped path draws it white for the same reason.
        /// </summary>
        public float Ambient
        {
            get => _ambient;
            set
            {
                float clamped = Mathf.Clamp01(value);
                if (Mathf.Approximately(_ambient, clamped)) return;
                _ambient = clamped;
                if (_assembled) ReapplyCurrentFrame();
            }
        }

        public int CurrentFrame => _currentFrame;
        public string CurrentState => _currentState;

        public void Setup(
            CharacterAnimationDefinition definition,
            CharacterStyleData styleData,
            CharacterAppearance appearance,
            CharacterVisualContext context = default,
            bool useFlashTint = true)
        {
            _definition = definition;
            _styleData = styleData;
            _appearance = appearance ?? CharacterAppearance.CreateDefault();
            _visualContext = context;
            _useFlashTint = useFlashTint;
            Rebuild();
        }

        public void Rebuild()
        {
            ClearSlots();

            if (_definition == null)
            {
                return;
            }

            _appearance ??= CharacterAppearance.CreateDefault();

            // Before the slots, so the very first ApplyFrame already samples the atlas rather than
            // building per-texture materials and throwing them away a frame later.
            RebuildAtlas();

            BuildSlots();
            BuildOverlays();
            _assembled = true;

            if (string.IsNullOrEmpty(_currentState))
            {
                SetState("stay", 0);
            }
            else
            {
                ReapplyCurrentFrame();
            }

            SyncOverlays(0);
            StartOverlayCoroutineIfNeeded();
        }

        public void ApplyAppearance()
        {
            if (!_assembled || _appearance == null)
            {
                return;
            }

            ReapplyCurrentFrame();
            SyncOverlays(0);
        }

        public void SetState(string stateName, int frame)
        {
            if (_definition == null)
            {
                return;
            }

            CharacterStateClip clip = _definition.GetStateClip(stateName);
            if (clip == null)
            {
                return;
            }

            _currentState = stateName;
            _currentFrame = Mathf.Clamp(frame, 0, Mathf.Max(0, clip.frameCount - 1));
            ApplyFrame(clip, _currentFrame);
        }

        public void AdvanceFrame()
        {
            if (_definition == null || string.IsNullOrEmpty(_currentState))
            {
                return;
            }

            CharacterStateClip clip = _definition.GetStateClip(_currentState);
            if (clip == null)
            {
                return;
            }

            _currentFrame++;

            switch (clip.loopMode)
            {
                case AnimationLoopMode.Loop:
                    if (_currentFrame >= clip.frameCount)
                    {
                        _currentFrame = 0;
                    }
                    break;

                case AnimationLoopMode.LoopRange:
                    if (_currentFrame >= clip.EffectiveLoopEnd)
                    {
                        _currentFrame = clip.loopStartFrame;
                    }
                    break;

                case AnimationLoopMode.ClampForever:
                case AnimationLoopMode.Manual:
                    _currentFrame = Mathf.Min(_currentFrame, clip.frameCount - 1);
                    break;
            }

            ApplyFrame(clip, _currentFrame);
        }

        /// <summary>
        /// The slot currently drawing <paramref name="partName"/>, or null. Several slots may map to
        /// the same part (the near and far legs share symbols), so this returns the first — same
        /// contract as <see cref="CharacterSpriteAssembler.GetSlot"/>.
        /// </summary>
        public QuadSlot GetQuadSlot(string partName)
        {
            foreach (QuadSlot slot in _slotPool)
            {
                if (slot.partDef != null && slot.partDef.partName == partName)
                {
                    return slot;
                }
            }
            return null;
        }

        // ─── Build ──────────────────────────────────────────────────────────

        void BuildSlots()
        {
            if (_definition?.parts == null || _definition.stateClips == null)
            {
                return;
            }

            int maxPlacements = 0;
            foreach (CharacterStateClip clip in _definition.stateClips)
            {
                if (clip.frames == null) continue;
                foreach (CharacterFrameData frame in clip.frames)
                {
                    if (frame.partPlacements != null)
                    {
                        maxPlacements = Mathf.Max(maxPlacements, frame.partPlacements.Length);
                    }
                }
            }

            // Capacity: primary slots + potential secondary hair layers + overlay slots.
            // Secondary slots are created lazily but share the same mesh, so they must
            // be counted here. The worst case is every primary slot having a secondary.
            int overlayCount = _definition.overlays?.Length ?? 0;
            _meshCapacity = maxPlacements * 2 + overlayCount;

            EnsureSharedMesh();

            for (int i = 0; i < maxPlacements; i++)
            {
                var slot = new QuadSlot
                {
                    vertexBase = i * CharacterQuadGeometry.VertexCount,
                    sortingOrder = i * 2,
                    partIndex = -1,
                    partDef = null,
                    _owner = this
                };
                _slotPool.Add(slot);
            }
        }

        /// <summary>
        /// The transform the part quads hang from, created on first use — the quad path's half of
        /// AS3's <c>osn</c> node. Identity when the definition says 1, so a rig whose frames are the
        /// outer symbol's own grows one node and nothing on screen moves.
        ///
        /// <para><b>The scale is re-read on every rebuild, never cached on the node.</b>
        /// <see cref="ClearSlots"/> destroys it, so an import that changes the definition cannot leave
        /// a stale scale behind — the same rule and the same reason as the shipped assembler's
        /// <c>EnsureBodyRoot</c>, which this mirrors deliberately rather than by coincidence.</para>
        /// </summary>
        Transform EnsureBodyRoot()
        {
            if (_bodyRoot != null)
            {
                return _bodyRoot;
            }

            var go = new GameObject("body");
            go.transform.SetParent(transform, false);
            go.layer = gameObject.layer;

            Vector2 scale = _definition != null ? _definition.BodyScale : Vector2.one;
            go.transform.localScale = new Vector3(scale.x, scale.y, 1f);

            _bodyRoot = go.transform;
            return _bodyRoot;
        }

        /// <summary>
        /// The body root the part quads hang from, or <c>null</c> before the first build. Exposed so a
        /// fixture can assert the scale is <i>applied</i> and not merely stored — a value nothing reads
        /// is exactly the fault this node was added to fix, and it had already happened once here.
        /// </summary>
        public Transform BodyRoot => _bodyRoot;

        /// <summary>
        /// Creates the single GameObject that carries the consolidated mesh, plus the mesh itself.
        /// Called once per build; the mesh is pre-allocated to <see cref="_meshCapacity"/> quads.
        /// </summary>
        void EnsureSharedMesh()
        {
            if (_meshRoot != null)
            {
                return;
            }

            _meshRoot = new GameObject("quad_mesh");
            _meshRoot.transform.SetParent(EnsureBodyRoot(), false);
            _meshRoot.layer = gameObject.layer;

            if (!Application.isPlaying)
            {
                _meshRoot.hideFlags = HideFlags.DontSave;
            }

            _meshFilter = _meshRoot.AddComponent<MeshFilter>();
            _meshRenderer = _meshRoot.AddComponent<MeshRenderer>();
            _meshRenderer.enabled = false;
            _meshRenderer.sortingLayerName = _sortingLayerName;
            _meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;

            int vertexCount = _meshCapacity * CharacterQuadGeometry.VertexCount;
            int indexCount = _meshCapacity * 6; // two triangles per quad

            _sharedMesh = new Mesh
            {
                name = $"{name}_shared",
                hideFlags = HideFlags.HideAndDontSave
            };
            _sharedMesh.MarkDynamic();

            _allVertices = new Vector3[vertexCount];
            _allUvs = new Vector2[vertexCount];
            _allColors = new Color[vertexCount];
            _allTriangles = new int[indexCount];

            // Pre-fill the index buffer with the quad topology. Each quad i uses
            // vertices [i*4 .. i*4+3] and indices [i*6 .. i*6+5]. The index buffer
            // is never rewritten per-frame; instead, the mesh's index count is set
            // to _activeQuadCount * 6, which truncates from the end. This works
            // because slots are written in order and hidden slots are simply not
            // included in the active count.
            for (int quad = 0; quad < _meshCapacity; quad++)
            {
                int v = quad * 4;
                int t = quad * 6;
                _allTriangles[t + 0] = v + 0;
                _allTriangles[t + 1] = v + 1;
                _allTriangles[t + 2] = v + 2;
                _allTriangles[t + 3] = v + 0;
                _allTriangles[t + 4] = v + 2;
                _allTriangles[t + 5] = v + 3;
            }

            _sharedMesh.vertices = _allVertices;
            _sharedMesh.uv = _allUvs;
            _sharedMesh.colors = _allColors;
            _sharedMesh.triangles = _allTriangles;

            _meshFilter.sharedMesh = _sharedMesh;
        }

        void ClearSlots()
        {
            StopOverlayCoroutine();
            ClearOverlays();
            _slotPool.Clear();
            _sourceCache.Clear();
            _materials.Dispose();
            _assembled = false;
            _activeQuadCount = 0;

            if (_sharedMesh != null)
            {
                CharacterQuadMaterialCache.DestroyObject(_sharedMesh);
                _sharedMesh = null;
            }

            _allVertices = null;
            _allUvs = null;
            _allColors = null;
            _allTriangles = null;

            if (_meshRoot != null)
            {
                CharacterQuadMaterialCache.DestroyObject(_meshRoot);
                _meshRoot = null;
                _meshFilter = null;
                _meshRenderer = null;
            }

            // The body root goes with the slots: it carries the definition's bodyScale, and a rebuild
            // after an import must pick up a changed one rather than keep the transform it was born
            // with. Clearing it here also keeps the hierarchy from growing one "body" per rebuild.
            if (_bodyRoot != null)
            {
                CharacterQuadMaterialCache.DestroyObject(_bodyRoot.gameObject);
                _bodyRoot = null;
            }
        }

        void ApplySortingLayerToAllSlots()
        {
            if (_meshRenderer != null)
            {
                _meshRenderer.sortingLayerName = _sortingLayerName;
            }
        }

        /// <summary>
        /// Forces every slot to rebuild its quad and re-resolve its material on the next frame.
        /// Used when a setting that changes geometry or sampling is toggled — cheaper and less
        /// error-prone than trying to patch live slots.
        /// </summary>
        void InvalidateGeometry()
        {
            foreach (QuadSlot slot in _slotPool)
            {
                slot.sprite = null;
            }

            foreach (QuadOverlaySlot overlay in _overlaySlots)
            {
                if (overlay.slot != null) overlay.slot.sprite = null;
            }

            _materials.Dispose();

            if (_assembled)
            {
                ReapplyCurrentFrame();
                SyncOverlays(0);
            }
        }

        /// <summary>
        /// Rebuilds the atlas and then re-draws. Separate from <see cref="InvalidateGeometry"/> because
        /// only two settings change the atlas — its filter (<c>_bilinear</c>) and whether it exists
        /// (<c>_useAtlas</c>) — while a pivot or sorting change has no bearing on it. Toggling the pivot
        /// used to dispose and re-copy a 1024² texture for nothing.
        /// </summary>
        void InvalidateAtlas()
        {
            RebuildAtlas();
            InvalidateGeometry();
        }

        // ─── Atlas ──────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the one-texture atlas from every sprite this character can reach: the parts' base
        /// sprites, their style variants, the worn armour set's overrides, every overlay frame, and the
        /// whole style library.
        ///
        /// <para><b>Why the style library is included.</b> Hair and eye styles are chosen at runtime
        /// from <see cref="CharacterStyleData"/>, so a sprite the atlas omits would silently fall back
        /// to its own texture — correct, but it would quietly undo the batching for the one part the
        /// player just changed. The library is small (36 sprites across 5 mane / 5 tail / 5 forelock /
        /// 6 iris styles), so packing all of it costs less than tracking which subset is live.</para>
        ///
        /// <para><b>Why the armour set is NOT all of them.</b> The definition carries 20 armour sets
        /// holding <b>197 distinct sprites</b> (measured: 10–11 each). Packing every set would triple
        /// the atlas to hold art the character cannot be wearing — and would rebuild megabytes of
        /// texture on a costume change that only ever shows one set. Only
        /// <see cref="CharacterVisualContext.armorId"/>'s set is packed, and
        /// <see cref="VisualContext"/> rebuilds the atlas when that id changes.</para>
        /// </summary>
        void RebuildAtlas()
        {
            _atlas?.Dispose();
            _atlas = null;
            _atlasVersion++;

            if (!_useAtlas || _definition == null)
            {
                return;
            }

            var sprites = new List<Sprite>(64);
            CollectSprites(sprites);

            if (sprites.Count == 0)
            {
                return;
            }

            _atlas = CharacterQuadAtlas.Build(sprites, _bilinear, _visualContext.armorId, _atlasMips);
        }

        void CollectSprites(List<Sprite> into)
        {
            if (_definition.parts != null)
            {
                foreach (CharacterPartDefinition part in _definition.parts)
                {
                    if (part == null) continue;

                    if (part.baseSprite != null) into.Add(part.baseSprite);

                    if (part.styleVariants != null)
                    {
                        foreach (Sprite variant in part.styleVariants)
                        {
                            if (variant != null) into.Add(variant);
                        }
                    }
                }
            }

            if (_definition.armorSets != null)
            {
                ArmorVisualSet worn = GetActiveArmorSet();

                if (worn?.partOverrides != null)
                {
                    foreach (ArmorPartOverride partOverride in worn.partOverrides)
                    {
                        if (partOverride.armorSprite != null) into.Add(partOverride.armorSprite);
                    }
                }
            }

            if (_definition.overlays != null)
            {
                foreach (CharacterOverlayDefinition overlay in _definition.overlays)
                {
                    if (overlay?.frames == null) continue;

                    foreach (Sprite frame in overlay.frames)
                    {
                        if (frame != null) into.Add(frame);
                    }
                }
            }

            if (_styleData?.layers != null)
            {
                foreach (StyleLayerGroup group in _styleData.layers)
                {
                    if (group?.sprites == null) continue;

                    foreach (Sprite styleSprite in group.sprites)
                    {
                        if (styleSprite != null) into.Add(styleSprite);
                    }
                }
            }
        }

        // ─── Overlays ───────────────────────────────────────────────────────

        Coroutine _overlayCoroutine;

        void OnEnable()
        {
            StartOverlayCoroutineIfNeeded();
        }

        void OnDisable()
        {
            StopOverlayCoroutine();
        }

        void StartOverlayCoroutineIfNeeded()
        {
            if (_overlayCoroutine == null && _overlaySlots.Count > 0 && _definition != null && isActiveAndEnabled)
            {
                _overlayCoroutine = StartCoroutine(OverlayTickRoutine());
            }
        }

        void StopOverlayCoroutine()
        {
            if (_overlayCoroutine != null)
            {
                StopCoroutine(_overlayCoroutine);
                _overlayCoroutine = null;
            }
        }

        /// <summary>
        /// Advances overlay frames at the definition's frame rate. Runs only while the component
        /// is enabled and there are overlays to animate. Replaces the per-frame Update() polling
        /// with a coroutine that sleeps between ticks — no work is done for characters standing
        /// still with no visible overlays.
        /// </summary>
        System.Collections.IEnumerator OverlayTickRoutine()
        {
            float frameDuration = 1f / Mathf.Max(1f, _definition.frameRate);
            var wait = new WaitForSeconds(frameDuration);

            while (true)
            {
                yield return wait;

                if (_overlaySlots.Count == 0 || _definition == null)
                {
                    continue;
                }

                // Recompute in case the definition was hot-swapped.
                float newDuration = 1f / Mathf.Max(1f, _definition.frameRate);
                if (Mathf.Abs(newDuration - frameDuration) > 0.001f)
                {
                    frameDuration = newDuration;
                    wait = new WaitForSeconds(frameDuration);
                }

                SyncOverlays(1);
            }
        }

        void BuildOverlays()
        {
            if (_definition?.overlays == null) return;

            int overlayIndex = 0;
            foreach (CharacterOverlayDefinition def in _definition.overlays)
            {
                if (def == null || string.IsNullOrEmpty(def.overlayName)) continue;

                // Overlay vertices live after the primary and secondary slots in the shared mesh.
                int vertexBase = (_slotPool.Count * 2 + overlayIndex) * CharacterQuadGeometry.VertexCount;

                var slot = new QuadSlot
                {
                    vertexBase = vertexBase,
                    sortingOrder = def.sortingOrder,
                    partIndex = -1,
                    partDef = null,
                    _owner = this
                };

                _overlaySlots.Add(new QuadOverlaySlot
                {
                    def = def,
                    slot = slot,
                    frame = OverlayClip.NoFrame,
                    visible = false
                });

                overlayIndex++;
            }
        }

        void ClearOverlays()
        {
            _overlaySlots.Clear();
            _overlayTimer = 0f;
        }

        /// <summary>
        /// The overlay playhead rules are the shipped ones — <see cref="OverlayClip"/> — deliberately
        /// not re-derived. The rise restarts at frame 1, the fall writes the playhead too, and an
        /// overlay whose frames never imported draws nothing.
        /// </summary>
        void SyncOverlays(int steps)
        {
            foreach (QuadOverlaySlot overlay in _overlaySlots)
            {
                CharacterOverlayDefinition def = overlay.def;
                if (def == null) continue;

                int frameCount = def.frames?.Length ?? 0;
                bool gate = _visualContext.IsOverlayVisible(def.overlayName);

                if (!OverlayClip.ShouldDraw(gate, frameCount))
                {
                    overlay.frame = OverlayClip.RestartFrame(frameCount);
                    overlay.visible = false;
                    overlay.slot.Hide();
                    continue;
                }

                if (!overlay.visible)
                {
                    overlay.frame = OverlayClip.RestartFrame(frameCount);
                    overlay.visible = true;
                }
                else
                {
                    for (int i = 0; i < steps; i++)
                    {
                        overlay.frame = OverlayClip.NextFrame(overlay.frame, frameCount, def.loopMode);
                    }
                }

                int index = OverlayClip.ClampFrame(overlay.frame, frameCount);
                if (index == OverlayClip.NoFrame)
                {
                    overlay.slot.Hide();
                    continue;
                }

                float scale = def.localScale <= 0f ? 1f : def.localScale;

                // White, never the character's tint: the shield is a magical dome, not fur.
                WriteSlot(
                    overlay.slot,
                    def.frames[index],
                    def.pivotNormalized,
                    def.localPosition,
                    0f,
                    new Vector2(scale, scale),
                    Color.white);
            }

            // Overlays are written outside ApplyFrame, so the mesh must be flushed here too —
            // otherwise an overlay frame change would not appear until the next animation frame.
            FlushMesh();
        }

        // ─── Frame application ──────────────────────────────────────────────

        void ReapplyCurrentFrame()
        {
            if (_definition == null)
            {
                return;
            }

            CharacterStateClip clip = !string.IsNullOrEmpty(_currentState)
                ? _definition.GetStateClip(_currentState)
                : _definition.GetStateClip("stay");

            if (clip == null)
            {
                return;
            }

            _currentState = clip.stateName;
            _currentFrame = Mathf.Clamp(_currentFrame, 0, Mathf.Max(0, clip.frameCount - 1));
            ApplyFrame(clip, _currentFrame);
        }

        void ApplyFrame(CharacterStateClip clip, int frameIndex)
        {
            if (clip.frames == null || frameIndex >= clip.frames.Length)
            {
                return;
            }

            foreach (QuadSlot slot in _slotPool)
            {
                slot.Hide();
                slot.partIndex = -1;
                slot.partDef = null;
            }

            _activeQuadCount = 0;

            if (_visualContext.transparent)
            {
                FlushMesh();
                return;
            }

            CharacterFrameData frameData = clip.frames[frameIndex];
            if (frameData.partPlacements == null)
            {
                FlushMesh();
                return;
            }

            ArmorVisualSet armorSet = GetActiveArmorSet();
            BuildHiddenHeadParts(armorSet, out bool usesMordaComposite);
            bool hideMane = _visualContext.hideMane || (armorSet?.hidesMane ?? false);

            ArmourPartsDrawn = 0;

            int slotIdx = 0;
            foreach (PartFramePlacement placement in frameData.partPlacements)
            {
                if (!placement.visible
                    || placement.partIndex < 0
                    || placement.partIndex >= _definition.parts.Length
                    || slotIdx >= _slotPool.Count)
                {
                    continue;
                }

                CharacterPartDefinition part = _definition.parts[placement.partIndex];

                // ── Part visibility filters (identical to the shipped assembler) ──
                if (part.partName == "mane" && clip.useRunMane) continue;
                if (part.partName == "mane_run" && !clip.useRunMane) continue;
                if (part.partName == "tail" && clip.useRunTail) continue;
                if (part.partName == "tail_run" && !clip.useRunTail) continue;
                if (hideMane && (part.partName == "mane" || part.partName == "mane_run")) continue;
                if (_hiddenHeadParts.Contains(part.partName)) continue;
                if (part.partName == "morda_armor" && !usesMordaComposite) continue;
                if ((part.partName == "lwing" || part.partName == "rwing") && !_visualContext.showWings) continue;

                QuadSlot slot = _slotPool[slotIdx];
                slot.partIndex = placement.partIndex;
                slot.partDef = part;

                ResolvedQuadVisual visual = ResolvePartVisual(placement.partIndex, part, armorSet, placement);
                if (visual.fromArmor) ArmourPartsDrawn++;

                Vector2 position = placement.localPosition + visual.bakedOffset;

                // The horn aura is the one part whose colour is owned by the simulation rather than
                // by the frame data — see HornAura. Its tint channel is already TintCategory.Magic;
                // what is added here is the charge alpha.
                bool isHornAura = part.partName == HornAura.PartName;
                Color primaryColor = ApplyAmbient(visual.primaryColor);
                if (isHornAura)
                {
                    primaryColor.a *= HornAura.Normalized(_hornAuraLevel);
                }

                if (isHornAura && primaryColor.a <= 0f)
                {
                    // AS3 ties the aura's `visible` to its alpha (`UnitPlayer.as:4852`): an uncharged
                    // glow is absent, not transparent.
                    slot.Hide();
                }
                else
                {
                    WriteSlot(
                        slot,
                        visual.primarySprite,
                        visual.primaryPivot,
                        position,
                        placement.localRotation,
                        placement.localScale,
                        primaryColor);
                }

                if (visual.showSecondary && visual.secondarySprite != null)
                {
                    QuadSlot secondary = slot.secondary ??= CreateSecondarySlot(slot);
                    secondary.partIndex = placement.partIndex;
                    secondary.partDef = part;
                    secondary.sortingOrder = slot.sortingOrder + 1;

                    WriteSlot(
                        secondary,
                        visual.secondarySprite,
                        visual.primaryPivot,
                        position,
                        placement.localRotation,
                        placement.localScale,
                        ApplyAmbient(visual.secondaryColor));
                }
                else
                {
                    slot.secondary?.Hide();
                }

                slotIdx++;
            }

            FlushMesh();
        }

        QuadSlot CreateSecondarySlot(QuadSlot primary)
        {
            // Secondary slots live in the second half of the primary slot range in the shared mesh.
            // Primary slot i uses vertices [i*4 .. i*4+3]; its secondary uses
            // [(maxPlacements + i) * 4 .. +3]. We track which primary index this secondary belongs
            // to by storing it on the slot itself.
            int primaryIndex = _slotPool.IndexOf(primary);
            if (primaryIndex < 0) return null;

            int maxPlacements = _slotPool.Count;
            int secondaryVertexBase = (maxPlacements + primaryIndex) * CharacterQuadGeometry.VertexCount;

            var secondary = new QuadSlot
            {
                vertexBase = secondaryVertexBase,
                sortingOrder = primary.sortingOrder + 1,
                partIndex = -1,
                partDef = null,
                _owner = this
            };

            primary.secondary = secondary;
            return secondary;
        }

        /// <summary>
        /// Writes one slot's quad into the shared mesh buffers. Geometry (quad + UVs) is only
        /// recomputed when the sprite/pivot/settings change; vertices and colors are written every
        /// time the placement changes. Nothing is uploaded to the GPU here — that happens once
        /// in <see cref="FlushMesh"/> after all slots are written.
        /// </summary>
        void WriteSlot(
            QuadSlot slot,
            Sprite sprite,
            Vector2 pivot,
            Vector2 position,
            float rotationDegrees,
            Vector2 scale,
            Color color)
        {
            if (sprite == null || slot.vertexBase < 0 || slot.vertexBase + 3 >= _allVertices.Length)
            {
                slot.Hide();
                return;
            }

            if (!ReferenceEquals(slot.sprite, sprite)
                || slot.pivot != pivot
                || slot.compensate != _compensatePivot
                || slot.atlasVersion != _atlasVersion)
            {
                slot.sprite = sprite;
                slot.pivot = pivot;
                slot.compensate = _compensatePivot;
                slot.atlasVersion = _atlasVersion;

                if (!_sourceCache.TryGetValue(sprite, out CharacterQuadSource source))
                {
                    source = CharacterQuadSource.FromSprite(sprite);
                    _sourceCache[sprite] = source;
                }
                slot.quad = CharacterQuadGeometry.Build(source, pivot, _compensatePivot);

                // The atlas changes only the UV window and the material — never the geometry.
                Vector4 atlasUv = default;
                bool atlased = _atlas != null && _atlas.TryGetUvRect(sprite, out atlasUv);
                if (atlased)
                {
                    slot.quad.UMin = atlasUv.x;
                    slot.quad.VMin = atlasUv.y;
                    slot.quad.UMax = atlasUv.z;
                    slot.quad.VMax = atlasUv.w;
                }

                // Write UVs into the shared buffer at this slot's vertex offset.
                int vb = slot.vertexBase;
                _allUvs[vb + 0] = new Vector2(slot.quad.UMin, slot.quad.VMin);
                _allUvs[vb + 1] = new Vector2(slot.quad.UMin, slot.quad.VMax);
                _allUvs[vb + 2] = new Vector2(slot.quad.UMax, slot.quad.VMax);
                _allUvs[vb + 3] = new Vector2(slot.quad.UMax, slot.quad.VMin);

                slot.colorValid = false;
            }

            if (!slot.colorValid || slot.color != color)
            {
                int vb = slot.vertexBase;
                _allColors[vb + 0] = color;
                _allColors[vb + 1] = color;
                _allColors[vb + 2] = color;
                _allColors[vb + 3] = color;
                slot.color = color;
                slot.colorValid = true;
            }

            // Write vertices into the shared buffer at this slot's vertex offset.
            CharacterQuadGeometry.WriteCorners(
                slot.quad, position, rotationDegrees, scale, _allVertices, slot.vertexBase);

            slot.visible = true;
        }

        /// <summary>
        /// Uploads the shared mesh buffers to the GPU and sets the renderer's material and
        /// active quad count. Called once per frame after all slots have been written.
        /// </summary>
        void FlushMesh()
        {
            if (_sharedMesh == null || _meshRenderer == null)
            {
                return;
            }

            // Count visible quads. Slots are written in order, so we count contiguously
            // from the start. A hidden slot (visible == false) does not contribute.
            // However, slots may be interleaved (primary visible, secondary hidden),
            // so we count all visible slots and rewrite the index buffer to compact them.
            int quadCount = 0;
            int triangleCount = 0;

            // Count primary slots and their secondaries
            foreach (QuadSlot slot in _slotPool)
            {
                if (slot.visible)
                {
                    quadCount++;
                    // Copy this quad's indices to the compacted position
                    int srcBase = slot.vertexBase;
                    int dstTriBase = triangleCount;
                    _allTriangles[dstTriBase + 0] = srcBase + 0;
                    _allTriangles[dstTriBase + 1] = srcBase + 1;
                    _allTriangles[dstTriBase + 2] = srcBase + 2;
                    _allTriangles[dstTriBase + 3] = srcBase + 0;
                    _allTriangles[dstTriBase + 4] = srcBase + 2;
                    _allTriangles[dstTriBase + 5] = srcBase + 3;
                    triangleCount += 6;
                }

                if (slot.secondary != null && slot.secondary.visible)
                {
                    quadCount++;
                    int srcBase = slot.secondary.vertexBase;
                    int dstTriBase = triangleCount;
                    _allTriangles[dstTriBase + 0] = srcBase + 0;
                    _allTriangles[dstTriBase + 1] = srcBase + 1;
                    _allTriangles[dstTriBase + 2] = srcBase + 2;
                    _allTriangles[dstTriBase + 3] = srcBase + 0;
                    _allTriangles[dstTriBase + 4] = srcBase + 2;
                    _allTriangles[dstTriBase + 5] = srcBase + 3;
                    triangleCount += 6;
                }
            }

            // Count overlay slots
            foreach (QuadOverlaySlot overlay in _overlaySlots)
            {
                if (overlay.slot != null && overlay.slot.visible)
                {
                    quadCount++;
                    int srcBase = overlay.slot.vertexBase;
                    int dstTriBase = triangleCount;
                    _allTriangles[dstTriBase + 0] = srcBase + 0;
                    _allTriangles[dstTriBase + 1] = srcBase + 1;
                    _allTriangles[dstTriBase + 2] = srcBase + 2;
                    _allTriangles[dstTriBase + 3] = srcBase + 0;
                    _allTriangles[dstTriBase + 4] = srcBase + 2;
                    _allTriangles[dstTriBase + 5] = srcBase + 3;
                    triangleCount += 6;
                }
            }

            _activeQuadCount = quadCount;

            if (quadCount == 0)
            {
                _meshRenderer.enabled = false;
                return;
            }

            // Upload the buffers. The triangle buffer is pre-filled at creation and never
            // changes — hidden quads are excluded by rewriting the index buffer to compact
            // visible quads at the start. SetTriangles with a count truncates the index
            // buffer so only the active quads are drawn.
            _sharedMesh.vertices = _allVertices;
            _sharedMesh.uv = _allUvs;
            _sharedMesh.colors = _allColors;
            _sharedMesh.SetTriangles(_allTriangles, 0, triangleCount, 0, false);

            // Resolve the material. If the atlas is active, all slots share the atlas material.
            // If not, we use the material of the first visible slot — this is a limitation of
            // the single-mesh approach: without the atlas, all parts must share one material.
            // The atlas is what enables the full benefit; without it, the quad path still
            // draws correctly but uses one material (the first part's texture).
            if (_atlas != null && _atlas.IsReady)
            {
                _meshRenderer.sharedMaterial = _atlas.Material;
            }
            else
            {
                // Find the first visible slot to get its material
                Material mat = null;
                foreach (QuadSlot slot in _slotPool)
                {
                    if (slot.visible && slot.sprite != null)
                    {
                        mat = _materials.GetMaterial(slot.sprite.texture, _bilinear);
                        break;
                    }
                }

                if (mat == null)
                {
                    foreach (QuadOverlaySlot overlay in _overlaySlots)
                    {
                        if (overlay.slot != null && overlay.slot.visible && overlay.slot.sprite != null)
                        {
                            mat = _materials.GetMaterial(overlay.slot.sprite.texture, _bilinear);
                            break;
                        }
                    }
                }

                _meshRenderer.sharedMaterial = mat;
            }

            _meshRenderer.enabled = true;
        }

        Color ApplyAmbient(Color color)
        {
            if (_ambient >= 0.999f)
            {
                return color;
            }

            color.r *= _ambient;
            color.g *= _ambient;
            color.b *= _ambient;
            return color;
        }

        // ─── Armour + visual resolution (mirrors the shipped assembler) ──────

        ArmorVisualSet GetActiveArmorSet()
        {
            if (_definition == null || string.IsNullOrWhiteSpace(_visualContext.armorId))
            {
                return null;
            }

            return _definition.GetArmorSet(_visualContext.armorId);
        }

        /// <summary>
        /// Fills the reusable set. The shipped path builds a fresh <see cref="HashSet{T}"/> here on
        /// every animation frame; this is the same rule with the allocation hoisted out.
        /// </summary>
        void BuildHiddenHeadParts(ArmorVisualSet armorSet, out bool usesMordaComposite)
        {
            _hiddenHeadParts.Clear();
            usesMordaComposite = false;

            if (armorSet == null)
            {
                return;
            }

            if (armorSet.hiddenHeadParts != null)
            {
                foreach (string hiddenPart in armorSet.hiddenHeadParts)
                {
                    if (!string.IsNullOrEmpty(hiddenPart)) _hiddenHeadParts.Add(hiddenPart);
                }
            }

            if (_definition?.parts != null)
            {
                for (int i = 0; i < _definition.parts.Length; i++)
                {
                    if (_definition.parts[i].partName != "morda_armor") continue;

                    if (TryGetArmorOverride(i, armorSet, out ArmorPartOverride mordaOverride))
                    {
                        usesMordaComposite = mordaOverride.armorSprite != null;
                    }
                    break;
                }
            }

            if (usesMordaComposite)
            {
                foreach (string partName in HeadSubPartNames)
                {
                    _hiddenHeadParts.Add(partName);
                }
            }
        }

        ResolvedQuadVisual ResolvePartVisual(
            int partIndex,
            CharacterPartDefinition part,
            ArmorVisualSet armorSet,
            PartFramePlacement placement)
        {
            if (TryGetArmorOverride(partIndex, armorSet, out ArmorPartOverride armorOverride))
            {
                return new ResolvedQuadVisual
                {
                    primarySprite = armorOverride.armorSprite,
                    // Not `armorOverride.pivotOverride`: that value is frame 1's pivot normalised
                    // against frame 1's trim, and an armour frame trims differently. See ArmorPivots.
                    primaryPivot = ArmorPivots.Resolve(part, armorOverride),
                    fromArmor = true,
                    primaryColor = Color.white,
                    secondarySprite = null,
                    secondaryColor = Color.white,
                    showSecondary = false,
                    // Shared with the sprite renderer: the armour's offset lives in the head
                    // container's space and has to be rotated/scaled by this placement.
                    bakedOffset = ArmorOffsets.Total(armorOverride, placement)
                };
            }

            Sprite primarySprite = part.baseSprite;
            Sprite secondarySprite = null;
            Vector2 pivot = part.pivotNormalized;

            if (_styleData != null)
            {
                if (part.tintCategory == TintCategory.Hair)
                {
                    string stylePartName = GetHairStylePartName(part.partName);
                    if (!string.IsNullOrEmpty(stylePartName))
                    {
                        int hairIndex = Mathf.Clamp(_appearance.hairStyle - 1, 0, Mathf.Max(0, _definition.hairStyleCount - 1));
                        Sprite styled = _styleData.GetLayerSprite(stylePartName, "Primary", hairIndex);
                        if (styled != null) primarySprite = styled;

                        if (NeedsSecondaryLayer(part.partName))
                        {
                            Sprite secondary = _styleData.GetLayerSprite(stylePartName, "Secondary", hairIndex);
                            if (secondary != null) secondarySprite = secondary;
                        }
                    }
                }
                else if (part.partName == "eye")
                {
                    int eyeIndex = Mathf.Clamp(_appearance.eyeStyle - 1, 0, Mathf.Max(0, _definition.eyeStyleCount - 1));
                    Sprite styled = _styleData.GetLayerSprite("Eye", "Iris", eyeIndex);
                    if (styled != null) primarySprite = styled;
                }
            }

            bool showSecondary = NeedsSecondaryLayer(part.partName)
                                 && _appearance.showSecondaryHair
                                 && secondarySprite != null;

            return new ResolvedQuadVisual
            {
                primarySprite = primarySprite,
                primaryPivot = pivot,
                primaryColor = ResolveBaseTint(part.tintCategory, false),
                secondarySprite = secondarySprite,
                secondaryColor = ResolveBaseTint(part.tintCategory, true),
                showSecondary = showSecondary,
                bakedOffset = Vector2.zero
            };
        }

        static bool NeedsSecondaryLayer(string partName)
        {
            return partName is "mane" or "mane_run" or "tail" or "tail_run" or "forelock";
        }

        static string GetHairStylePartName(string partName)
        {
            return partName switch
            {
                "mane" or "mane_run" => "Mane",
                "tail" or "tail_run" => "Tail",
                "forelock" => "Forelock",
                _ => null
            };
        }

        Color ResolveBaseTint(TintCategory tintCategory, bool isSecondaryLayer)
        {
            if (_visualContext.transparent)
            {
                return new Color(1f, 1f, 1f, 0f);
            }

            return tintCategory switch
            {
                TintCategory.None => Color.white,
                TintCategory.Hair when isSecondaryLayer => CharacterTintUtility.GetRenderTint(_appearance.secondaryHairColor, _useFlashTint),
                TintCategory.Hair => CharacterTintUtility.GetRenderTint(_appearance.primaryHairColor, _useFlashTint),
                _ => CharacterTintUtility.GetRenderTint(_appearance.GetColor(tintCategory), _useFlashTint)
            };
        }

        static bool TryGetArmorOverride(int partIndex, ArmorVisualSet armorSet, out ArmorPartOverride armorOverride)
        {
            if (armorSet?.partOverrides != null)
            {
                for (int i = 0; i < armorSet.partOverrides.Length; i++)
                {
                    ArmorPartOverride candidate = armorSet.partOverrides[i];
                    if (candidate.partIndex == partIndex && candidate.armorSprite != null)
                    {
                        armorOverride = candidate;
                        return true;
                    }
                }
            }

            armorOverride = default;
            return false;
        }

        // ─── Unity lifecycle ────────────────────────────────────────────────

        void Awake()
        {
            if (_definition != null && !_assembled)
            {
                Rebuild();
            }
        }

        void OnDestroy()
        {
            // ClearSlots already disposes the per-texture materials; the atlas is not a slot, so it is
            // the one thing this method has to own.
            ClearSlots();
            _atlas?.Dispose();
            _atlas = null;
        }

        // ─── Data types ─────────────────────────────────────────────────────

        struct ResolvedQuadVisual
        {
            public Sprite primarySprite;
            public Vector2 primaryPivot;
            /// <summary>True when the sprite came from the worn armour set, not from the part.</summary>
            public bool fromArmor;
            public Color primaryColor;
            public Sprite secondarySprite;
            public Color secondaryColor;
            public bool showSecondary;
            public Vector2 bakedOffset;
        }

        /// <summary>
        /// One quad's worth of rendering state. Unlike the shipped <c>PartSlot</c> there is no
        /// GameObject per part: the placement is baked into four vertices written into a single
        /// dynamic mesh shared by the whole character. That is the Remains shape — one transform
        /// for the actor, vertices for the parts, one draw call.
        /// </summary>
        public sealed class QuadSlot
        {
            /// <summary>Index of this slot's first vertex in the shared mesh (slotIndex * 4).</summary>
            public int vertexBase;

            public CharacterQuadLocal quad;
            public Sprite sprite;
            public Vector2 pivot;
            public bool compensate;
            public Color color;
            public bool colorValid;

            /// <summary>The atlas generation this slot's UVs were written for. See
            /// <c>CharacterQuadAssembler._atlasVersion</c>.</summary>
            public int atlasVersion;

            public int partIndex = -1;
            public CharacterPartDefinition partDef;

            /// <summary>Sorting order this slot keeps within the character (index × 2).</summary>
            public int sortingOrder;

            /// <summary>Whether this slot wrote vertices into the shared mesh this frame.</summary>
            public bool visible;

            public QuadSlot secondary;

            /// <summary>
            /// The shared mesh renderer that draws this slot's quad. Exposed for the preview
            /// window's bounds and material checks — with the consolidated mesh there is only one
            /// renderer for the whole character, so every slot returns the same one.
            /// </summary>
            public MeshRenderer renderer => _owner != null ? _owner._meshRenderer : null;

            /// <summary>Back-reference to the assembler that owns this slot, for the renderer shim.</summary>
            internal CharacterQuadAssembler _owner;

            public void Hide()
            {
                visible = false;
                if (secondary != null) secondary.visible = false;
            }
        }

        sealed class QuadOverlaySlot
        {
            public CharacterOverlayDefinition def;
            public QuadSlot slot;
            public int frame = OverlayClip.NoFrame;
            public bool visible;
        }
    }
}
