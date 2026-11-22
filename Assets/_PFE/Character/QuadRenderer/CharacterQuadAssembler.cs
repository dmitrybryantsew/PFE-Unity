using System.Collections.Generic;
using PFE.Character.Animation;
using PFE.Data.Definitions;
using UnityEngine;
using UnityEngine.Rendering;

namespace PFE.Character
{
    /// <summary>
    /// The character renderer: draws <b>baked quads</b> instead of per-part
    /// <see cref="SpriteRenderer"/>s — the shape of pipeline the Remains-Unity reference port uses.
    ///
    /// <para><b>What that means, in one place.</b></para>
    /// <list type="number">
    /// <item><b>Geometry is four vertices, not a sprite.</b> The registration point is resolved to
    /// pixels in the full PNG and the trimmed rect is offset against it
    /// (<see cref="CharacterQuadGeometry"/>), so a part whose PNG was alpha-trimmed lands where the
    /// importer meant.</item>
    /// <item><b>The quad is the whole rect.</b> No <c>Tight</c> contour, so the anti-aliased rim the
    /// vector rasteriser produced survives.</item>
    /// <item><b>One consolidated mesh per character.</b> Every visible part quad is written into a
    /// single dynamic mesh (<see cref="EnsureSharedMesh"/>); the whole character costs one transform
    /// and one draw call instead of the 21–26 a per-part hierarchy pays.</item>
    /// <item><b>Sampling can be bilinear</b> without touching a single import setting — the texture is
    /// copied GPU-side and the copy carries the filter (<see cref="CharacterQuadMaterialCache"/>).</item>
    /// <item><b>Nothing is allocated per frame.</b> One reusable <see cref="HashSet{T}"/> for the armour
    /// head-part filter, no <c>Sprite.Create</c>, no <c>SetActive</c>; a hidden slot is a degenerate
    /// quad, not a disabled object.</item>
    /// </list>
    ///
    /// <para><b>The atlas is on by default, and that is a correctness requirement, not only a batching
    /// win.</b> One mesh binds one material, and part UVs point into each part's own texture — so a
    /// character whose parts live on several textures (every imported character does) renders
    /// correctly only when every part is packed into the one atlas texture
    /// (<see cref="CharacterQuadAtlas"/>). docs/CharacterImportPipeline/04 and 05 record the
    /// investigation of the flat-block artefacts: the atlas was exonerated (padding is cleared before
    /// regions are copied; a region can change UVs and the material but never geometry), and the
    /// captures under .workbuddy-ai/tmp/wave1/captures show the atlas path drawing every character
    /// correctly in-game.</para>
    ///
    /// <para><b>What it does not do.</b> Lamp falloff. <see cref="Ambient"/> multiplies every part
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
        /// Head sub-parts that a composite <c>morda_armor</c> replaces. Shared with the animation
        /// preview tooling on purpose: every consumer of the definition must agree about which parts
        /// exist or a costume change looks like a renderer bug.
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
                 "Turns 21-26 draw calls into one batch. Default on since the one-mesh consolidation: " +
                 "a single mesh binds a single material, so a character whose parts live on several " +
                 "textures (every imported character) only renders correct art with the atlas packed. " +
                 "The flat-tinted-block suspect that kept this off was investigated to a different " +
                 "cause — uninitialised atlas padding — which is cleared before regions are copied " +
                 "(docs/CharacterImportPipeline/04-05). Off is a diagnostic fallback, not a supported " +
                 "rendering mode for multi-texture characters.")]
        [SerializeField] bool _useAtlas = true;

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

        // ─── Consolidated meshes (one draw call for the body, one for the overlays) ──
        //
        // Every visible part quad is written into a single dynamic mesh. The mesh has
        // maxSlots * 4 vertices and maxSlots * 6 indices, pre-allocated at build time.
        // Each frame, ApplyFrame writes the visible slots' vertices (interleaved
        // position/UV/colour) and hides the rest by zeroing their positions, which makes
        // their triangles degenerate. The index buffer is uploaded once at build time and
        // never touched again; the renderer draws whatever the vertex data describes.
        //
        // Why one mesh and not one per slot: 26 parts × (GameObject + MeshFilter +
        // MeshRenderer + Mesh) is the overhead the sprite path pays; the quad path
        // was designed to avoid it. One mesh also means one draw call when the atlas
        // is active, and one draw call per material when it is not.
        //
        // Overlays live in a SECOND mesh under the assembler root, not under the body
        // root: in the SWF the shield dome and its siblings are children of visualPlayer,
        // siblings of osn — so scaling the body must not scale the shield (see EnsureOverlayMesh).

        /// <summary>The single GameObject that carries the consolidated body mesh renderer.</summary>
        GameObject _meshRoot;
        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Mesh _sharedMesh;

        /// <summary>
        /// Pre-allocated interleaved vertex buffer, sized to maxSlots * 4: position, UV and colour
        /// in one <see cref="CharacterQuadVertex"/>, so a frame uploads through a single
        /// <c>SetVertexBufferData</c> call. A hidden slot is not removed from the buffer — its four
        /// positions are zeroed, making its two triangles degenerate, which is what lets the index
        /// buffer be uploaded once at build time and never rewritten.
        /// </summary>
        CharacterQuadVertex[] _vertices;

        /// <summary>
        /// The quad topology, pre-filled at creation and uploaded to the index buffer exactly once.
        /// Compaction used to rewrite this every frame to skip hidden quads; the degenerate-quad
        /// approach removed that whole path.
        /// </summary>
        int[] _allTriangles;

        /// <summary>One past the highest vertex index written since the last flush; the upload count.</summary>
        int _touchedVertexCount;

        /// <summary>How many slots the body mesh can hold (primary + secondary).</summary>
        int _meshCapacity;

        /// <summary>How many quads were written into the meshes this frame (body + overlays).</summary>
        int _activeQuadCount;

        // ── Overlay mesh. Sibling of the body root, exactly as the shield dome and its
        //    brothers are siblings of osn in the SWF: an overlay written under the body
        //    root would scale with the character (a 30 % oversized shield for the player),
        //    and would also sit at one fixed renderer sorting order with no way to honour
        //    each overlay definition's own order above the body. ────────────────────────

        GameObject _overlayMeshRoot;
        MeshFilter _overlayMeshFilter;
        MeshRenderer _overlayMeshRenderer;
        Mesh _overlayMesh;
        CharacterQuadVertex[] _overlayVertices;
        int[] _overlayTriangles;
        int _overlayTouchedVertexCount;

        /// <summary>
        /// The node the part quads hang from — AS3's <c>osn</c> child of the outer visual symbol, the
        /// one that carries the placement's scale. <b>Not a number in the vertex maths.</b> The part
        /// slots hang from this node, so every part draws at <c>rect / ppu × bodyScale</c> and every
        /// part <i>position</i> at <c>localPosition × bodyScale</c>. Measured on the player, whose
        /// <c>bodyScale</c> is 1.2999878: without the node the whole character drew at
        /// <b>1/1.3 = 0.769</b> — every part too small and every part too close to the origin.
        /// Reproducing the node rather than multiplying two numbers here means a non-uniform or
        /// mirrored <c>bodyScale</c> composes the way a parent transform does.
        ///
        /// <para><b>Overlays do not hang from it.</b> In the original an overlay is a sibling of
        /// <c>osn</c>, so scaling the body must not scale the shield dome.</para>
        /// </summary>
        Transform _bodyRoot;

        /// <summary>
        /// Reused every frame — the per-frame allocation a naive filter costs is the single largest
        /// piece of garbage in a character draw path, so this set is filled and cleared instead.
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

        /// <summary>Same ceiling as a view is allowed: a paused editor must not fast-forward a clip.</summary>
        const int MaxOverlayStepsPerTick = 8;

        string _currentState;
        int _currentFrame;
        bool _assembled;

        /// <summary>
        /// Horn aura charge, 0..100 — see <see cref="HornAura"/>. The default is "off": the original
        /// builds the glow at alpha 0 and only raises it when a weapon or a levitation asks for it
        /// (<c>UnitPlayer.as:4851</c>), so a character that nobody drives must not glow.
        /// </summary>
        float _hornAuraLevel = HornAura.LevelIdle;

        // ─── Public API ──────────────────────────────────────────────────────

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
        /// Default <b>on</b>, and required for correct art on any character whose parts live on more
        /// than one texture: one consolidated mesh binds one material, and without the atlas every
        /// quad's UVs would sample the first visible part's texture.
        ///
        /// <para>Turning it off rebuilds immediately and falls back to the first visible part's
        /// per-texture material — useful only as a diagnostic for single-texture fixtures.</para>
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
        /// A decision-level diagnostic, not a pixel check: "the costume did not reach the renderer"
        /// and "the costume reached it and the pixels are wrong" can look identical in a screenshot,
        /// but report different numbers here.
        /// </remarks>
        public int ArmourPartsDrawn { get; private set; }

        /// <summary>How many quads were written into the meshes on the last frame (body + overlays).</summary>
        public int ActiveQuadCount => _activeQuadCount;

        /// <summary>How many slots the body mesh can hold (primary + secondary).</summary>
        public int MeshCapacity => _meshCapacity;

        /// <summary>Whether the body mesh renderer is currently enabled (i.e. something was drawn).</summary>
        public bool IsMeshRendererActive => _meshRenderer != null && _meshRenderer.enabled;

        /// <summary>
        /// Whether the overlay mesh renderer is currently enabled. The shield and its siblings are
        /// the only overlays, so this is "an overlay gate is on" — the quad-path answer to "is the
        /// dome on screen", asserted by the shield fixture.
        /// </summary>
        public bool IsOverlayMeshActive => _overlayMeshRenderer != null && _overlayMeshRenderer.enabled;

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
        /// Horn aura charge, 0..100, in the original's units — see <see cref="HornAura"/>. 0 is what
        /// the game spends most of its time at, so a freshly built character shows no glow until the
        /// simulation says otherwise.
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
        /// shield is emissive magic and draws white for the same reason.
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
        /// the same part (the near and far legs share symbols), so this returns the first.
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

        /// <summary>
        /// Where a named part sits in world space on the frame currently drawn — the port of AS3's
        /// <c>vis.osn.body.head.morda.konec</c> transform (<c>UnitPlayer.as:3563</c>), which is how the
        /// player's magic spawn point is derived.
        ///
        /// <para><b>Why this cannot be a Transform lookup.</b> <c>konec</c> is a marker, not art:
        /// symbol 240 is empty, <see cref="CharacterPartDefinition.baseSprite"/> is null, and all
        /// <b>881</b> of its placements are authored <c>visible: 0</c> — so <see cref="ApplyFrame"/>
        /// never writes it a quad slot and there is no GameObject to read. The point still exists in
        /// the frame data, which is where this reads it.</para>
        ///
        /// <para><b>Why it must be read every frame.</b> <c>konec</c> has <b>687</b> distinct positions
        /// across the shipped clips — x spans 0.61 units, y spans 0.66 — because it rides the head.
        /// Caching one sample would put the horn in the wrong place on most of the animation (lesson
        /// #256: a placement is a starting point, not a position).</para>
        ///
        /// <para>World space through the <i>body root</i>, not this component's transform: the
        /// placement is expressed in the body clip's own space, and since the body root can carry a
        /// scale (<see cref="CharacterAnimationDefinition.BodyScale"/>) the two are not the same
        /// answer. Reading it through the assembler would report every anchor at 1/1.3 of where the
        /// quad actually draws, and a held weapon would sit inside the chest with nothing logged. The
        /// armour override's <c>localOffset</c> is included for the same reason
        /// <see cref="ApplyFrame"/> includes it — a caller asking about <c>helm</c> must not get a
        /// different answer than the renderer draws.</para>
        /// </summary>
        /// <param name="partName">The definition's part name, e.g. <c>konec</c>, <c>head</c>.</param>
        /// <param name="world">The point, when the part is present on the current frame.</param>
        /// <returns>
        /// <c>false</c> when there is no definition, no current clip, no such part, or the part is
        /// absent from this frame — every one of which is a legal "no anchor" answer rather than an
        /// error, and each of which the caller must have a fallback for.
        /// </returns>
        public bool TryGetPartWorldPosition(string partName, out Vector2 world)
        {
            world = default;

            if (_definition == null || string.IsNullOrEmpty(_currentState)) return false;

            CharacterStateClip clip = _definition.GetStateClip(_currentState);
            if (clip?.frames == null || _currentFrame < 0 || _currentFrame >= clip.frames.Length) return false;

            CharacterFrameData frame = clip.frames[_currentFrame];
            if (frame.partPlacements == null) return false;

            int partIndex = FindPartIndex(partName);
            if (partIndex < 0) return false;

            foreach (PartFramePlacement placement in frame.partPlacements)
            {
                if (placement.partIndex != partIndex) continue;

                Vector2 local = placement.localPosition;

                // Same expression as ApplyFrame's position write: a part the armour has displaced must
                // report the displaced point, or this and the renderer would disagree about where the
                // part is.
                ArmorVisualSet armorSet = GetActiveArmorSet();
                if (TryGetArmorOverride(partIndex, armorSet, out ArmorPartOverride armorOverride))
                {
                    local += ArmorOffsets.Total(armorOverride, placement);
                }

                // Through the body root, not this component's transform: the placement is expressed in
                // the body clip's own space, and since the body root can carry a scale
                // (CharacterAnimationDefinition.bodyScale) the two are not the same answer. Using the
                // assembler here would report every anchor at 1/1.3 of where the quad actually draws,
                // and a held weapon would sit inside the chest with nothing logged.
                world = BodyRootOrSelf.TransformPoint(new Vector3(local.x, local.y, 0f));
                return true;
            }

            return false;
        }

        /// <summary>Index of a named part in <see cref="CharacterAnimationDefinition.parts"/>, or -1.</summary>
        int FindPartIndex(string partName)
        {
            if (_definition?.parts == null || string.IsNullOrEmpty(partName)) return -1;

            for (int i = 0; i < _definition.parts.Length; i++)
            {
                CharacterPartDefinition part = _definition.parts[i];
                if (part != null && part.partName == partName) return i;
            }

            return -1;
        }

        /// <summary>
        /// <see cref="BodyRoot"/> when a build has happened, otherwise this component's transform.
        /// The fallback matters for a caller that reads an anchor before the first <c>Rebuild</c>.
        /// </summary>
        Transform BodyRootOrSelf => _bodyRoot != null ? _bodyRoot : transform;

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

            // Capacity: primary slots + potential secondary hair layers. Secondary slots are
            // created lazily but share the same mesh, so they must be counted here. The worst
            // case is every primary slot having a secondary. Overlays live in their own mesh
            // (EnsureOverlayMesh) and are not part of this capacity.
            _meshCapacity = maxPlacements * 2;

            EnsureSharedMesh();

            for (int i = 0; i < maxPlacements; i++)
            {
                var slot = new QuadSlot
                {
                    vertexBase = i * CharacterQuadGeometry.VertexCount,
                    sortingOrder = i * 2,
                    partIndex = -1,
                    partDef = null,
                    _owner = this,
                    _buffer = _vertices
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
        /// a stale scale behind — a value that exists but nothing reads is exactly the fault this
        /// node was added to fix.</para>
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

            _vertices = new CharacterQuadVertex[vertexCount];
            _allTriangles = new int[indexCount];

            // Fill the index buffer with the quad topology. Each quad i uses vertices
            // [i*4 .. i*4+3] and indices [i*6 .. i*6+5]. Uploaded once: visibility is expressed in
            // the vertex data (degenerate quads), never by editing this buffer.
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

            _sharedMesh.SetVertexBufferParams(vertexCount, CharacterQuadVertex.Layout);
            // UInt32 even though this character needs a few hundred vertices: the format is chosen
            // once for the life of the mesh, and a pathological definition must not overflow it.
            _sharedMesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
            _sharedMesh.SetIndexBufferData(_allTriangles, 0, 0, indexCount);
            _sharedMesh.SetSubMesh(
                0,
                new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles),
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

            _touchedVertexCount = 0;
            _meshFilter.sharedMesh = _sharedMesh;
        }

        /// <summary>
        /// Creates the GameObject that carries the overlay quads — the shield dome and its siblings —
        /// plus its mesh, sized to the definition's overlay count. Called once per build from
        /// <see cref="BuildOverlays"/>.
        ///
        /// <para><b>Parented to the assembler root, deliberately NOT to the body root.</b> In the SWF
        /// the overlays are siblings of <c>osn</c> inside <c>visualPlayer</c>, placed at their own
        /// scales: scaling the body must not scale the shield dome (for the player, whose bodyScale
        /// is 1.2999878, parenting the dome under the body root is a 30 % oversized dome and a bug
        /// nobody would connect to the player's height).</para>
        ///
        /// <para><b>Its own renderer, at the highest overlay sorting order.</b> An overlay definition
        /// carries a <c>sortingOrder</c> meant to sit above every body part (the shield is 1000);
        /// a single body mesh at order 0 could not express that.</para>
        /// </summary>
        void EnsureOverlayMesh(int capacity, int sortingOrder)
        {
            if (_overlayMeshRoot != null || capacity <= 0)
            {
                return;
            }

            _overlayMeshRoot = new GameObject("quad_overlay_mesh");
            _overlayMeshRoot.transform.SetParent(transform, false);
            _overlayMeshRoot.layer = gameObject.layer;

            if (!Application.isPlaying)
            {
                _overlayMeshRoot.hideFlags = HideFlags.DontSave;
            }

            _overlayMeshFilter = _overlayMeshRoot.AddComponent<MeshFilter>();
            _overlayMeshRenderer = _overlayMeshRoot.AddComponent<MeshRenderer>();
            _overlayMeshRenderer.enabled = false;
            _overlayMeshRenderer.sortingLayerName = _sortingLayerName;
            _overlayMeshRenderer.sortingOrder = sortingOrder;
            _overlayMeshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _overlayMeshRenderer.receiveShadows = false;

            int vertexCount = capacity * CharacterQuadGeometry.VertexCount;
            int indexCount = capacity * 6;

            _overlayMesh = new Mesh
            {
                name = $"{name}_overlays",
                hideFlags = HideFlags.HideAndDontSave
            };
            _overlayMesh.MarkDynamic();

            _overlayVertices = new CharacterQuadVertex[vertexCount];
            _overlayTriangles = new int[indexCount];

            // Same degenerate-quad visibility contract as the body mesh: the index buffer is
            // uploaded once, hidden overlays are zeroed positions.
            for (int quad = 0; quad < capacity; quad++)
            {
                int v = quad * 4;
                int t = quad * 6;
                _overlayTriangles[t + 0] = v + 0;
                _overlayTriangles[t + 1] = v + 1;
                _overlayTriangles[t + 2] = v + 2;
                _overlayTriangles[t + 3] = v + 0;
                _overlayTriangles[t + 4] = v + 2;
                _overlayTriangles[t + 5] = v + 3;
            }

            _overlayMesh.SetVertexBufferParams(vertexCount, CharacterQuadVertex.Layout);
            _overlayMesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
            _overlayMesh.SetIndexBufferData(_overlayTriangles, 0, 0, indexCount);
            _overlayMesh.SetSubMesh(
                0,
                new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles),
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

            _overlayTouchedVertexCount = 0;
            _overlayMeshFilter.sharedMesh = _overlayMesh;
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

            _vertices = null;
            _allTriangles = null;
            _touchedVertexCount = 0;

            if (_meshRoot != null)
            {
                CharacterQuadMaterialCache.DestroyObject(_meshRoot);
                _meshRoot = null;
                _meshFilter = null;
                _meshRenderer = null;
            }

            if (_overlayMesh != null)
            {
                CharacterQuadMaterialCache.DestroyObject(_overlayMesh);
                _overlayMesh = null;
            }

            _overlayVertices = null;
            _overlayTriangles = null;
            _overlayTouchedVertexCount = 0;

            if (_overlayMeshRoot != null)
            {
                CharacterQuadMaterialCache.DestroyObject(_overlayMeshRoot);
                _overlayMeshRoot = null;
                _overlayMeshFilter = null;
                _overlayMeshRenderer = null;
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

            if (_overlayMeshRenderer != null)
            {
                _overlayMeshRenderer.sortingLayerName = _sortingLayerName;
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
            ReleaseAtlas();
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

            // Play mode borrows from the shared cache: the same costume on the next spawn or swap
            // is a lookup, not a pack + copy + read-back. The editor builds a fresh, owned atlas so
            // the preview windows keep their rebuild-on-demand semantics.
            _atlas = Application.isPlaying
                ? CharacterQuadAtlas.Cache.Get(sprites, _bilinear, _visualContext.armorId, _atlasMips)
                : CharacterQuadAtlas.Build(sprites, _bilinear, _visualContext.armorId, _atlasMips);
        }

        /// <summary>
        /// Drops the current atlas reference. <b>Never disposes a play-mode atlas</b> — those belong
        /// to <see cref="CharacterQuadAtlas.Cache"/> and may still be drawn by another character;
        /// the cache evicts and disposes on its own LRU. Only the editor path (a fresh
        /// <see cref="CharacterQuadAtlas.Build"/>, owned by this instance) is disposed here.
        /// </summary>
        void ReleaseAtlas()
        {
            if (_atlas != null && !Application.isPlaying)
            {
                _atlas.Dispose();
            }

            _atlas = null;
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

            // Count the overlays that will actually get a slot — null or nameless definitions are
            // skipped below, and the mesh is sized to what is drawn, not to the array length.
            int validCount = 0;
            int topSortingOrder = 0;
            foreach (CharacterOverlayDefinition def in _definition.overlays)
            {
                if (def == null || string.IsNullOrEmpty(def.overlayName)) continue;
                validCount++;
                topSortingOrder = Mathf.Max(topSortingOrder, def.sortingOrder);
            }

            EnsureOverlayMesh(validCount, topSortingOrder);

            int overlayIndex = 0;
            foreach (CharacterOverlayDefinition def in _definition.overlays)
            {
                if (def == null || string.IsNullOrEmpty(def.overlayName)) continue;

                // Overlay vertices live in the overlay mesh, one quad each, in definition order.
                int vertexBase = overlayIndex * CharacterQuadGeometry.VertexCount;

                var slot = new QuadSlot
                {
                    vertexBase = vertexBase,
                    sortingOrder = def.sortingOrder,
                    partIndex = -1,
                    partDef = null,
                    _owner = this,
                    _buffer = _overlayVertices,
                    _isOverlay = true
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
        /// The overlay playhead rules are the original Flash ones, centralised in
        /// <see cref="OverlayClip"/> — deliberately not re-derived. The rise restarts at frame 1, the
        /// fall writes the playhead too, and an overlay whose frames never imported draws nothing.
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

                // ── Part visibility filters (the original's rules) ──
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
                _owner = this,
                _buffer = _vertices
            };

            primary.secondary = secondary;
            return secondary;
        }

        /// <summary>
        /// Writes one slot's quad into its interleaved buffer — the body mesh's for parts, the
        /// overlay mesh's for overlays. Geometry (quad + UV window) is only recomputed when the
        /// sprite/pivot/settings change; the four vertices are rewritten every call — position, UV
        /// and colour together, one struct per vertex. Nothing is uploaded to the GPU here — that
        /// happens once in <see cref="FlushMesh"/>.
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
            CharacterQuadVertex[] buffer = slot._buffer;
            if (sprite == null || slot.vertexBase < 0 || buffer == null
                || slot.vertexBase + 3 >= buffer.Length)
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
            }

            CharacterQuadGeometry.WriteQuad(
                slot.quad, position, rotationDegrees, scale, color, buffer, slot.vertexBase);
            NoteTouched(slot, slot.vertexBase + CharacterQuadGeometry.VertexCount);

            slot.color = color;
            slot.colorValid = true;
            slot.visible = true;
        }

        /// <summary>
        /// Zeros the four positions of one quad, which turns both of its triangles degenerate: the
        /// quad is hidden without touching the index buffer. UV and colour stay stale on purpose —
        /// a degenerate triangle rasterizes no fragment, so nothing reads them.
        /// </summary>
        void ZeroQuad(QuadSlot slot)
        {
            CharacterQuadVertex[] buffer = slot._buffer;
            int vertexBase = slot.vertexBase;
            if (buffer == null || vertexBase < 0 || vertexBase + 3 >= buffer.Length)
            {
                return;
            }

            for (int i = 0; i < CharacterQuadGeometry.VertexCount; i++)
            {
                CharacterQuadVertex vertex = buffer[vertexBase + i];
                vertex.Position = Vector3.zero;
                buffer[vertexBase + i] = vertex;
            }

            NoteTouched(slot, vertexBase + CharacterQuadGeometry.VertexCount);
        }

        /// <summary>Extends the upload prefix of the slot's own mesh: every write this frame sits below this index.</summary>
        void NoteTouched(QuadSlot slot, int onePastLast)
        {
            if (slot._isOverlay)
            {
                if (onePastLast > _overlayTouchedVertexCount)
                {
                    _overlayTouchedVertexCount = onePastLast;
                }
            }
            else if (onePastLast > _touchedVertexCount)
            {
                _touchedVertexCount = onePastLast;
            }
        }

        /// <summary>
        /// Uploads the touched prefix of both interleaved vertex buffers, sets each mesh's bounds
        /// from its own visible quads, and resolves each renderer's material. Called once per frame
        /// after all slots have been written. The index buffers are never touched here — they were
        /// uploaded once at build time, and hidden quads are degenerate positions, not absent
        /// indices. <see cref="_activeQuadCount"/> counts the quads of both meshes.
        /// </summary>
        void FlushMesh()
        {
            // A dead atlas must not reach the material resolution below. The play-mode cache
            // evicts and disposes atlases LRU (CharacterQuadAtlas.Cache.MaxEntries), and an
            // assembler that keeps drawing a costume it acquired before eight newer builds —
            // an equipped armour, a room full of costumes — holds a disposed texture. Left
            // alone, that renders the broken one-material fallback forever; rebuilding here
            // turns it into one missed frame. InvalidateAtlas re-draws through ApplyFrame
            // (nested flush), so the slots below re-upload with the fresh atlas anyway.
            if (_atlas != null && !_atlas.IsReady)
            {
                Debug.LogWarning(
                    "[CharacterQuad] The character atlas was disposed while in use (cache " +
                    "eviction); rebuilding it. Raise CharacterQuadAtlas.Cache.MaxEntries if this " +
                    "recurs.");
                InvalidateAtlas();
            }

            int drawn = FlushBodyMesh();
            drawn += FlushOverlayMesh();
            _activeQuadCount = drawn;
        }

        int FlushBodyMesh()
        {
            if (_sharedMesh == null || _meshRenderer == null)
            {
                return 0;
            }

            // Count visible quads for the readout and accumulate the local-space bounds Unity
            // would otherwise recalculate per upload — the upload below passes
            // DontRecalculateBounds, so this loop owns the only bounds the culler ever sees.
            int quadCount = 0;
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool hasBounds = false;

            void Include(QuadSlot slot)
            {
                if (_vertices == null) return;
                for (int i = 0; i < CharacterQuadGeometry.VertexCount; i++)
                {
                    Vector3 p = _vertices[slot.vertexBase + i].Position;
                    if (hasBounds) bounds.Encapsulate(p);
                    else { bounds = new Bounds(p, Vector3.zero); hasBounds = true; }
                }
            }

            foreach (QuadSlot slot in _slotPool)
            {
                if (slot.visible) { quadCount++; Include(slot); }
                if (slot.secondary != null && slot.secondary.visible) { quadCount++; Include(slot.secondary); }
            }

            if (quadCount == 0)
            {
                _touchedVertexCount = 0;
                _meshRenderer.enabled = false;
                return 0;
            }

            int count = Mathf.Min(_touchedVertexCount, _vertices.Length);
            _touchedVertexCount = 0;
            if (count > 0)
            {
                _sharedMesh.SetVertexBufferData(
                    _vertices, 0, 0, count, 0,
                    MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            }

            // A generous box on purpose: parts swing between animation extremes, and recomputing
            // tight bounds per frame is the work the manual-bounds flag exists to skip. One extra
            // unit of margin around what is actually drawn.
            if (hasBounds)
            {
                bounds.Expand(1f);
                _sharedMesh.bounds = bounds;
                _sharedMesh.SetSubMesh(
                    0,
                    new SubMeshDescriptor(0, _allTriangles.Length, MeshTopology.Triangles) { bounds = bounds },
                    MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            }

            // Resolve the material. If the atlas is active, all slots share the atlas material.
            // If not, the material of the first visible part — the limitation of the single-mesh
            // approach: one mesh binds one material. Correct only when every part lives on that
            // one texture; on any real character that means the atlas must be on (class doc).
            if (_atlas != null && _atlas.IsReady)
            {
                _meshRenderer.sharedMaterial = _atlas.Material;
            }
            else
            {
                Material mat = null;
                foreach (QuadSlot slot in _slotPool)
                {
                    if (slot.visible && slot.sprite != null)
                    {
                        mat = _materials.GetMaterial(slot.sprite.texture, _bilinear);
                        break;
                    }
                }

                _meshRenderer.sharedMaterial = mat;
            }

            _meshRenderer.enabled = true;
            return quadCount;
        }

        int FlushOverlayMesh()
        {
            if (_overlayMesh == null || _overlayMeshRenderer == null)
            {
                return 0;
            }

            int quadCount = 0;
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool hasBounds = false;

            void Include(QuadSlot slot)
            {
                if (_overlayVertices == null) return;
                for (int i = 0; i < CharacterQuadGeometry.VertexCount; i++)
                {
                    Vector3 p = _overlayVertices[slot.vertexBase + i].Position;
                    if (hasBounds) bounds.Encapsulate(p);
                    else { bounds = new Bounds(p, Vector3.zero); hasBounds = true; }
                }
            }

            foreach (QuadOverlaySlot overlay in _overlaySlots)
            {
                if (overlay.slot != null && overlay.slot.visible) { quadCount++; Include(overlay.slot); }
            }

            if (quadCount == 0)
            {
                _overlayTouchedVertexCount = 0;
                _overlayMeshRenderer.enabled = false;
                return 0;
            }

            int count = Mathf.Min(_overlayTouchedVertexCount, _overlayVertices.Length);
            _overlayTouchedVertexCount = 0;
            if (count > 0)
            {
                _overlayMesh.SetVertexBufferData(
                    _overlayVertices, 0, 0, count, 0,
                    MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            }

            if (hasBounds)
            {
                bounds.Expand(1f);
                _overlayMesh.bounds = bounds;
                _overlayMesh.SetSubMesh(
                    0,
                    new SubMeshDescriptor(0, _overlayTriangles.Length, MeshTopology.Triangles) { bounds = bounds },
                    MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            }

            // Same material rule as the body mesh, over the overlay slots' own sprites: the atlas
            // material when packed, otherwise the first visible overlay's per-texture material.
            if (_atlas != null && _atlas.IsReady)
            {
                _overlayMeshRenderer.sharedMaterial = _atlas.Material;
            }
            else
            {
                Material mat = null;
                foreach (QuadOverlaySlot overlay in _overlaySlots)
                {
                    if (overlay.slot != null && overlay.slot.visible && overlay.slot.sprite != null)
                    {
                        mat = _materials.GetMaterial(overlay.slot.sprite.texture, _bilinear);
                        break;
                    }
                }

                _overlayMeshRenderer.sharedMaterial = mat;
            }

            _overlayMeshRenderer.enabled = true;
            return quadCount;
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

        // ─── Armour + visual resolution ──────────────────────────────────────

        ArmorVisualSet GetActiveArmorSet()
        {
            if (_definition == null || string.IsNullOrWhiteSpace(_visualContext.armorId))
            {
                return null;
            }

            return _definition.GetArmorSet(_visualContext.armorId);
        }

        /// <summary>
        /// Fills the reusable set. The rule itself builds a fresh <see cref="HashSet{T}"/> per
        /// animation frame; this is the same rule with the allocation hoisted out.
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
            // ClearSlots already disposes the per-texture materials; the atlas is either the cache's
            // (play mode — the cache evicts it) or ours (editor — released here).
            ClearSlots();
            ReleaseAtlas();
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
        /// One quad's worth of rendering state. There is no GameObject per part: the placement is
        /// baked into four vertices written into one of the character's two dynamic meshes. That
        /// is the Remains shape — one transform for the actor, vertices for the parts, one draw
        /// call per mesh.
        /// </summary>
        public sealed class QuadSlot
        {
            /// <summary>Index of this slot's first vertex in its mesh's buffer (slotIndex * 4).</summary>
            public int vertexBase;

            /// <summary>
            /// The vertex buffer this slot writes — the body mesh's for parts and secondaries, the
            /// overlay mesh's for overlays. Set once at slot creation.
            /// </summary>
            internal CharacterQuadVertex[] _buffer;

            /// <summary>Whether this slot belongs to the overlay mesh (drives uploads, bounds, and which renderer the shim returns).</summary>
            internal bool _isOverlay;

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

            /// <summary>Whether this slot wrote vertices into its mesh this frame.</summary>
            public bool visible;

            public QuadSlot secondary;

            /// <summary>
            /// The mesh renderer that draws this slot's quad — the body mesh renderer for parts,
            /// the overlay mesh renderer for overlays. Exposed for bounds and material checks.
            /// </summary>
            public MeshRenderer renderer =>
                _owner != null
                    ? (_isOverlay ? _owner._overlayMeshRenderer : _owner._meshRenderer)
                    : null;

            /// <summary>Back-reference to the assembler that owns this slot, for the renderer shim.</summary>
            internal CharacterQuadAssembler _owner;

            public void Hide()
            {
                visible = false;

                // Degenerate the quad in the vertex buffer so the static index buffer draws
                // nothing for it. Without this a previously written slot would keep rasterizing
                // its stale vertices — the old design avoided that by compacting the index buffer
                // every frame, which is the work this design exists to remove.
                if (_owner != null)
                {
                    _owner.ZeroQuad(this);
                    if (secondary != null) _owner.ZeroQuad(secondary);
                }
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
