using System.Collections.Generic;
using PFE.Character.Animation;
using PFE.Data.Definitions;
using UnityEngine;

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
            "morda_base", "morda_overlay", "eye", "forelock", "horn", "magic", "konec", "helm"
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
        [Tooltip("Resolve the pivot against the full PNG (what the importer meant) instead of the trimmed rect " +
                 "(what Sprite.Create reads). Turning this off reproduces the shipped path's registration error.")]
        [SerializeField] bool _compensatePivot = true;

        [Tooltip("Sample a GPU-side bilinear copy of each part texture. The imported textures are Point.")]
        [SerializeField] bool _bilinear = true;

        [Tooltip("Pack every part texture into one atlas and draw the whole character with one material. " +
                 "Turns 21-26 draw calls into one batch. Off falls back to one material per texture, " +
                 "which is what the shipped sprite path does. " +
                 "Default off since 2026-10-21: the atlas is the only structure here that the reference " +
                 "port does not have, and it is currently the only suspect for the flat tinted blocks in " +
                 "docs/CharacterImportPipeline/04.")]
        [SerializeField] bool _useAtlas;

        [Tooltip("Room ambient multiplier applied to every part. 1 = full brightness. " +
                 "Use CharacterRenderAmbient.FromDarkness(room.darkness) to match the background tiles.")]
        [SerializeField, Range(0f, 1f)] float _ambient = 1f;

        readonly List<QuadSlot> _slotPool = new();
        readonly List<QuadOverlaySlot> _overlaySlots = new();

        /// <summary>
        /// Reused every frame. The shipped path allocates one of these per animation frame per
        /// character, which is the single largest piece of garbage in its draw path.
        /// </summary>
        readonly HashSet<string> _hiddenHeadParts = new();

        readonly CharacterQuadMaterialCache _materials = new();

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

            for (int i = 0; i < maxPlacements; i++)
            {
                QuadSlot slot = CreateSlot($"quad_{i}");
                slot.sortingOrder = i * 2;
                _slotPool.Add(slot);
            }
        }

        QuadSlot CreateSlot(string objectName)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(transform, false);

            // Inherit the character's layer. A slot created lazily (the secondary hair layer is made
            // on the first frame that needs it, not during Setup) would otherwise sit on layer 0 and
            // be invisible to any camera that culls to the character's layer — the preview windows do
            // exactly that.
            go.layer = gameObject.layer;

            if (!Application.isPlaying)
            {
                go.hideFlags = HideFlags.DontSave;
            }

            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.enabled = false;
            renderer.sortingLayerName = _sortingLayerName;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var mesh = new Mesh
            {
                name = $"{name}_{objectName}",
                hideFlags = HideFlags.HideAndDontSave
            };
            mesh.MarkDynamic();

            var slot = new QuadSlot
            {
                gameObject = go,
                filter = filter,
                renderer = renderer,
                mesh = mesh,
                partIndex = -1,
                partDef = null
            };

            // Fixed topology: four vertices, two triangles, allocated once. Only the positions move.
            mesh.SetVertices(slot.vertices);
            mesh.SetUVs(0, slot.uvs);
            mesh.SetColors(slot.colors);
            mesh.SetTriangles(QuadTriangles, 0);

            filter.sharedMesh = mesh;
            return slot;
        }

        void ClearSlots()
        {
            ClearOverlays();

            foreach (QuadSlot slot in _slotPool)
            {
                if (slot.gameObject != null)
                {
                    slot.gameObject.SetActive(false);
                    CharacterQuadMaterialCache.DestroyObject(slot.gameObject);
                }

                if (slot.mesh != null)
                {
                    CharacterQuadMaterialCache.DestroyObject(slot.mesh);
                }
            }

            _slotPool.Clear();
            _materials.Dispose();
            _assembled = false;
        }

        void ApplySortingLayerToAllSlots()
        {
            foreach (QuadSlot slot in _slotPool)
            {
                if (slot.renderer != null)
                {
                    slot.renderer.sortingLayerName = _sortingLayerName;
                }

                if (slot.secondary != null && slot.secondary.renderer != null)
                {
                    slot.secondary.renderer.sortingLayerName = _sortingLayerName;
                }
            }

            foreach (QuadOverlaySlot overlay in _overlaySlots)
            {
                if (overlay.slot?.renderer != null)
                {
                    overlay.slot.renderer.sortingLayerName = _sortingLayerName;
                }
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

            _atlas = CharacterQuadAtlas.Build(sprites, _bilinear, _visualContext.armorId);
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

        void Update()
        {
            if (_overlaySlots.Count == 0 || _definition == null) return;

            float frameDuration = 1f / Mathf.Max(1f, _definition.frameRate);
            _overlayTimer += Time.deltaTime;

            int steps = 0;
            while (_overlayTimer >= frameDuration && steps < MaxOverlayStepsPerTick)
            {
                _overlayTimer -= frameDuration;
                steps++;
            }

            if (_overlayTimer > frameDuration) _overlayTimer = 0f;

            SyncOverlays(steps);
        }

        void BuildOverlays()
        {
            if (_definition?.overlays == null) return;

            foreach (CharacterOverlayDefinition def in _definition.overlays)
            {
                if (def == null || string.IsNullOrEmpty(def.overlayName)) continue;

                QuadSlot slot = CreateSlot($"quad_overlay_{def.overlayName}");
                slot.sortingOrder = def.sortingOrder;
                slot.renderer.sortingOrder = def.sortingOrder;

                _overlaySlots.Add(new QuadOverlaySlot
                {
                    def = def,
                    slot = slot,
                    frame = OverlayClip.NoFrame,
                    visible = false
                });
            }
        }

        void ClearOverlays()
        {
            foreach (QuadOverlaySlot overlay in _overlaySlots)
            {
                if (overlay.slot?.gameObject != null)
                {
                    overlay.slot.gameObject.SetActive(false);
                    CharacterQuadMaterialCache.DestroyObject(overlay.slot.gameObject);
                }

                if (overlay.slot?.mesh != null)
                {
                    CharacterQuadMaterialCache.DestroyObject(overlay.slot.mesh);
                }
            }

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
                Show(
                    overlay.slot,
                    def.frames[index],
                    def.pivotNormalized,
                    def.localPosition,
                    0f,
                    new Vector2(scale, scale),
                    Color.white,
                    def.sortingOrder);
            }
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

            if (_visualContext.transparent)
            {
                return;
            }

            CharacterFrameData frameData = clip.frames[frameIndex];
            if (frameData.partPlacements == null)
            {
                return;
            }

            ArmorVisualSet armorSet = GetActiveArmorSet();
            BuildHiddenHeadParts(armorSet, out bool usesMordaComposite);
            bool hideMane = _visualContext.hideMane || (armorSet?.hidesMane ?? false);

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

                ResolvedQuadVisual visual = ResolvePartVisual(placement.partIndex, part, armorSet);

                Vector2 position = placement.localPosition + visual.bakedOffset;

                Show(
                    slot,
                    visual.primarySprite,
                    visual.primaryPivot,
                    position,
                    placement.localRotation,
                    placement.localScale,
                    ApplyAmbient(visual.primaryColor),
                    slot.sortingOrder);

                if (visual.showSecondary && visual.secondarySprite != null)
                {
                    QuadSlot secondary = slot.secondary ??= CreateSecondarySlot(slot);
                    secondary.partIndex = placement.partIndex;
                    secondary.partDef = part;
                    secondary.sortingOrder = slot.sortingOrder + 1;

                    Show(
                        secondary,
                        visual.secondarySprite,
                        visual.primaryPivot,
                        position,
                        placement.localRotation,
                        placement.localScale,
                        ApplyAmbient(visual.secondaryColor),
                        slot.sortingOrder + 1);
                }
                else
                {
                    slot.secondary?.Hide();
                }

                slotIdx++;
            }
        }

        QuadSlot CreateSecondarySlot(QuadSlot primary)
        {
            QuadSlot secondary = CreateSlot($"{primary.gameObject.name}_h1");
            secondary.sortingOrder = primary.sortingOrder + 1;
            primary.secondary = secondary;
            return secondary;
        }

        /// <summary>
        /// Writes one slot: geometry only when the sprite/pivot/settings changed, vertices every time
        /// the placement changed. <see cref="Mesh.SetVertices(Vector3[])"/> copies from a cached array,
        /// so this does not allocate.
        /// </summary>
        void Show(
            QuadSlot slot,
            Sprite sprite,
            Vector2 pivot,
            Vector2 position,
            float rotationDegrees,
            Vector2 scale,
            Color color,
            int sortingOrder)
        {
            if (sprite == null)
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

                CharacterQuadSource source = CharacterQuadSource.FromSprite(sprite);
                slot.quad = CharacterQuadGeometry.Build(source, pivot, _compensatePivot);

                // The atlas changes only the UV window and the material — never the geometry. The four
                // corners are pixels-against-pivot arithmetic, and where the pixels live is irrelevant
                // to where the quad sits, so a part cannot shift by being atlased.
                // Initialised before the `&&` because the call is short-circuited when there is no
                // atlas, and an `out` parameter inside a short-circuit is not definitely assigned.
                Vector4 atlasUv = default;
                bool atlased = _atlas != null && _atlas.TryGetUvRect(sprite, out atlasUv);
                if (atlased)
                {
                    slot.quad.UMin = atlasUv.x;
                    slot.quad.VMin = atlasUv.y;
                    slot.quad.UMax = atlasUv.z;
                    slot.quad.VMax = atlasUv.w;
                }

                CharacterQuadGeometry.WriteUvs(slot.quad, slot.uvs);
                slot.mesh.SetUVs(0, slot.uvs);

                slot.filter.sharedMesh = slot.mesh;
                slot.renderer.sharedMaterial = atlased
                    ? _atlas.Material
                    : _materials.GetMaterial(sprite.texture, _bilinear);

                slot.colorValid = false;
            }

            if (!slot.colorValid || slot.color != color)
            {
                slot.colors[0] = color;
                slot.colors[1] = color;
                slot.colors[2] = color;
                slot.colors[3] = color;
                slot.mesh.SetColors(slot.colors);
                slot.color = color;
                slot.colorValid = true;
            }

            CharacterQuadGeometry.WriteCorners(slot.quad, position, rotationDegrees, scale, slot.vertices);
            slot.mesh.SetVertices(slot.vertices);

            slot.renderer.sortingOrder = sortingOrder;
            slot.renderer.enabled = true;
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
            ArmorVisualSet armorSet)
        {
            if (TryGetArmorOverride(partIndex, armorSet, out ArmorPartOverride armorOverride))
            {
                return new ResolvedQuadVisual
                {
                    primarySprite = armorOverride.armorSprite,
                    primaryPivot = armorOverride.pivotOverride,
                    primaryColor = Color.white,
                    secondarySprite = null,
                    secondaryColor = Color.white,
                    showSecondary = false,
                    bakedOffset = armorOverride.positionOffset
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
            public Color primaryColor;
            public Sprite secondarySprite;
            public Color secondaryColor;
            public bool showSecondary;
            public Vector2 bakedOffset;
        }

        /// <summary>
        /// One quad renderer. Unlike the shipped <c>PartSlot</c> there is no per-slot transform
        /// writing: the placement is baked into the four vertices, and the slot GameObject stays at
        /// identity. That is the Remains shape — one transform for the actor, vertices for the parts.
        /// </summary>
        public sealed class QuadSlot
        {
            public GameObject gameObject;
            public MeshFilter filter;
            public MeshRenderer renderer;
            public Mesh mesh;

            public readonly Vector3[] vertices = new Vector3[CharacterQuadGeometry.VertexCount];
            public readonly Vector2[] uvs = new Vector2[CharacterQuadGeometry.VertexCount];
            public readonly Color[] colors = new Color[CharacterQuadGeometry.VertexCount];

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

            public QuadSlot secondary;

            public void Hide()
            {
                if (renderer != null) renderer.enabled = false;
                if (secondary != null && secondary.renderer != null) secondary.renderer.enabled = false;
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
