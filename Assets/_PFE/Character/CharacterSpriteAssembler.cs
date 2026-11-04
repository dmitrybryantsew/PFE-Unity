using System.Collections.Generic;
using PFE.Character.Animation;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Character
{
    /// <summary>
    /// Assembles a character from body-part sprites and applies appearance customization.
    ///
    /// Uses a POOL of slots (one slot per placement in a frame), matching the architecture
    /// of CharacterAnimationPreviewWindow. This correctly handles parts that appear multiple
    /// times in a single frame (e.g. the near and far leg pairs share the same part symbols
    /// but are placed at different positions in the SWF depth list).
    ///
    /// <para><b>Two kinds of thing are drawn, and they are not interchangeable.</b> Body <i>parts</i>
    /// come from <c>CharacterAnimationDefinition.parts</c> and are placed by the current state clip —
    /// that is what the pool above is for. Overlay <i>clips</i> come from
    /// <c>CharacterAnimationDefinition.overlays</c> and run on their own timeline, above the body; the
    /// shield (<c>vis.shit</c>) is one. See <see cref="CharacterOverlayDefinition"/> for why the oracle
    /// separates them and what goes wrong if a part is used for an overlay.</para>
    /// </summary>
    public class CharacterSpriteAssembler : MonoBehaviour
    {
        static readonly HashSet<string> HeadSubPartNames = new()
        {
            "morda_base", "morda_base_back", "shell", "morda_overlay", "eye", "forelock",
            "horn", "magic", "konec", "helm", "helm2"
        };

        [Header("Data")]
        [SerializeField] CharacterAnimationDefinition _definition;
        [SerializeField] CharacterStyleData _styleData;

        [Header("Rendering")]
        [Tooltip("Sorting layer applied to every slot renderer. Must match a layer defined in Project Settings → Tags & Layers.")]
        [SerializeField] string _sortingLayerName = "Default";

        [Header("Current Appearance")]
        [SerializeField] CharacterAppearance _appearance;
        [SerializeField] CharacterVisualContext _visualContext = default;
        [SerializeField] bool _useFlashTint = true;

        // Pool: one slot per placement (not per unique part).
        // Sized to the maximum partPlacements count across all frames/states.
        readonly List<PartSlot> _slotPool = new();

        // Overlay clips (vis.shit and the other siblings of the body sprite): one renderer each,
        // drawn above the body. See CharacterOverlayDefinition for why these are not parts.
        readonly List<OverlaySlot> _overlaySlots = new();

        // The overlay clips share one cadence — the character's frame rate — so one accumulator
        // serves them all rather than one per clip.
        float _overlayTimer;

        /// <summary>
        /// Ceiling on overlay frames advanced in a single Update. The animation is view-only, so a
        /// breakpoint or a long editor pause must not produce a backlog that then fast-forwards the
        /// clip; dropping the surplus is the right trade.
        /// </summary>
        const int MaxOverlayStepsPerTick = 8;

        // Caches Sprite instances rebuilt with the correct pivotNormalized.
        // Key: (source sprite, pivot) — created once, destroyed on ClearSlots.
        readonly Dictionary<(Sprite, Vector2), Sprite> _pivotCache = new();

        string _currentState;
        int _currentFrame;
        bool _assembled;

        /// <summary>
        /// The transform every part slot hangs from — AS3's <c>osn</c> child of the character's outer
        /// visual symbol. It exists so the body can carry a placement matrix of its own
        /// (<see cref="CharacterAnimationDefinition.bodyScale"/>) without scaling the overlays, which
        /// are that symbol's <i>siblings</i> in the SWF and not its children.
        ///
        /// <para><b>Why a child transform and not the assembler's own.</b> <c>visualPlayer</c>(3690)
        /// holds <c>osn</c>(3679) at 1.2999878 and holds <c>shit</c>/<c>fetter</c>/<c>cryst</c>/
        /// <c>rat</c> beside it at their own scales. Putting the 1.3 on this transform reproduces that
        /// tree exactly: the body grows, the shield dome does not. Putting it on the assembler would
        /// grow both, which is a 30 % oversized shield and a bug nobody would connect to the player's
        /// height.</para>
        /// </summary>
        Transform _bodyRoot;

        /// <summary>
        /// Horn aura charge, 0..100. Defaults to "off" — the original builds the glow at alpha 0 and
        /// only raises it when a weapon or a levitation asks for it (<c>UnitPlayer.as:4851</c>), so a
        /// character that nobody drives must not glow.
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

        /// <summary>
        /// How many parts the last drawn frame took from the worn armour set. Mirrors
        /// <see cref="CharacterQuadAssembler.ArmourPartsDrawn"/> so the A/B can compare decisions
        /// rather than pixels — see that property for why.
        /// </summary>
        public int ArmourPartsDrawn { get; private set; }

        public CharacterVisualContext VisualContext
        {
            get => _visualContext;
            set
            {
                _visualContext = value;
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

        /// <summary>
        /// Sorting layer name applied to every slot renderer (primary and secondary).
        /// Change this to move the whole character to a different sorting layer.
        /// The layer must exist in Project Settings → Tags & Layers.
        /// </summary>
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

        public int CurrentFrame => _currentFrame;
        public string CurrentState => _currentState;

        /// <summary>
        /// Where a named part sits in world space on the frame currently drawn — the port of AS3's
        /// <c>vis.osn.body.head.morda.konec</c> transform (<c>UnitPlayer.as:3563</c>), which is how the
        /// player's magic spawn point is derived.
        ///
        /// <para><b>Why this cannot be a Transform lookup.</b> <c>konec</c> is a marker, not art:
        /// symbol 240 is empty, <see cref="CharacterPartDefinition.baseSprite"/> is null, and all
        /// <b>881</b> of its placements are authored <c>visible: 0</c> — so <see cref="ApplyFrame"/>
        /// never gives it a pool slot and there is no GameObject to read. The point still exists in the
        /// frame data, which is where this reads it.</para>
        ///
        /// <para><b>Why it must be read every frame.</b> <c>konec</c> has <b>687</b> distinct positions
        /// across the shipped clips — x spans 0.61 units, y spans 0.66 — because it rides the head.
        /// Caching one sample would put the horn in the wrong place on most of the animation (lesson
        /// #256: a placement is a starting point, not a position).</para>
        ///
        /// <para>World space through the <i>body root</i> — the parent every slot is placed under — so
        /// the answer is the point the slot would occupy if the part were drawn. The armour override's
        /// <c>localOffset</c> is included for the same reason
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

                // Same expression as ApplyFrame's transform write (via ResolvePartVisual): a part the
                // armour has displaced must report the displaced point, or this and the renderer would
                // disagree about where the part is.
                ArmorVisualSet armorSet = GetActiveArmorSet();
                if (TryGetArmorOverride(partIndex, armorSet, out ArmorPartOverride armorOverride))
                {
                    local += ArmorOffsets.Total(armorOverride, placement);
                }

                // Through the body root, not this component's transform: the placement is expressed in
                // the body clip's own space, and since the body root can carry a scale
                // (CharacterAnimationDefinition.bodyScale) the two are not the same answer. Using the
                // assembler here would report every anchor at 1/1.3 of where the slot actually draws,
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
        /// The horn aura's charge, 0..100, in the original's own units — see
        /// <see cref="HornAura"/>. 0 is what the game spends most of its time at, so a freshly built
        /// character shows no glow until the simulation says otherwise.
        ///
        /// <para>A plain number rather than a component reference on purpose: the shipped path is on
        /// the player prefab and must not grow a dependency on a magic subsystem to be able to draw a
        /// character.</para>
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
        /// Sets all required data at once and rebuilds. Avoids the double-rebuild that occurs
        /// when using individual property setters (each setter that finds _assembled==true
        /// triggers its own rebuild).
        /// </summary>
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

            // Overlays are independent of the state clip, so nothing above has touched them yet.
            SyncOverlays(0);
        }

        public void ApplyAppearance()
        {
            if (!_assembled || _appearance == null)
            {
                return;
            }

            ReapplyCurrentFrame();

            // The context is pushed through this path (VisualContext's setter calls here), so this is
            // where a shield rise or fall is applied — immediately, rather than at the next frame
            // boundary, so the graphic does not lag its own gate by up to a frame.
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
        /// Returns the first pool slot currently assigned to the given part name, or null.
        /// Note: multiple slots may map to the same part (duplicated placements).
        /// </summary>
        public PartSlot GetSlot(string partName)
        {
            foreach (PartSlot slot in _slotPool)
            {
                if (slot.partDef != null && slot.partDef.partName == partName)
                {
                    return slot;
                }
            }
            return null;
        }

        // ─── Internal build ──────────────────────────────────────────────────

        void ApplySortingLayerToAllSlots()
        {
            foreach (PartSlot slot in _slotPool)
            {
                if (slot.renderer != null)
                    slot.renderer.sortingLayerName = _sortingLayerName;
                if (slot.secondaryRenderer != null)
                    slot.secondaryRenderer.sortingLayerName = _sortingLayerName;
            }

            // Overlays carry their own sortingOrder (they sit above the body), but they belong to the
            // same layer as the character, so the layer name still comes from here.
            foreach (OverlaySlot overlay in _overlaySlots)
            {
                if (overlay.renderer != null)
                    overlay.renderer.sortingLayerName = _sortingLayerName;
            }
        }

        void BuildSlots()
        {
            if (_definition?.parts == null || _definition.stateClips == null)
            {
                return;
            }

            // Built before the pool, not inside it: a rig with no placements still has a body, and a
            // caller that asks for the root of a built character must not get null depending on how
            // many parts its frames happen to carry.
            EnsureBodyRoot();

            // Pool size = max partPlacements across all frames in all states.
            // This matches CharacterAnimationPreviewWindow._maxPlacementsPerFrame.
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
                var go = new GameObject($"slot_{i}");
                go.transform.SetParent(EnsureBodyRoot(), false);
                go.SetActive(false);

                // Inherit the character's layer. A new GameObject is always on layer 0, and the
                // preview windows cull their camera to one layer — so a slot built after the last
                // layer sweep is invisible there with nothing logged. Same rule as
                // CharacterQuadAssembler.CreateSlot; a no-op in game, where both are 0.
                go.layer = gameObject.layer;

                var sr = go.AddComponent<SpriteRenderer>();
                sr.sortingLayerName = _sortingLayerName;

                _slotPool.Add(new PartSlot
                {
                    gameObject = go,
                    transform = go.transform,
                    renderer = sr,
                    partIndex = -1,
                    partDef = null,
                    hasSecondaryLayer = false,
                    secondaryRenderer = null,
                });
            }
        }

        /// <summary>
        /// The transform the part slots hang from, created on first use — AS3's <c>osn</c> child of the
        /// character's outer visual symbol, carrying that placement's scale.
        ///
        /// <para><b>Identity when the definition says 1.</b> A rig whose frames are the outer symbol's
        /// own gets a scale-1 root: the tree grows one node and nothing on screen moves. That is what
        /// keeps this from being a player-only branch in a component the NPCs share.</para>
        ///
        /// <para><b>The scale is re-read on every build, not cached on the node.</b> The node is
        /// destroyed by <see cref="ClearSlots"/>, so an import that changes the definition cannot leave
        /// a stale scale behind on a survivor.</para>
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
        /// The body root the part slots hang from, or <c>null</c> when nothing has been built.
        /// Exposed so a fixture can assert the scale is <i>applied</i> and not merely stored — a value
        /// nothing reads is exactly the fault this node was added to fix.
        /// </summary>
        public Transform BodyRoot => _bodyRoot;

        /// <summary>
        /// <see cref="BodyRoot"/> when a build has happened, otherwise this component's transform.
        /// The fallback matters for a caller that reads an anchor before the first <c>Rebuild</c>.
        /// </summary>
        Transform BodyRootOrSelf => _bodyRoot != null ? _bodyRoot : transform;

        void ClearSlots()
        {
            ClearOverlays();

            foreach (PartSlot slot in _slotPool)
            {
                if (slot.gameObject != null)
                {
                    slot.gameObject.SetActive(false);
                    DestroyObject(slot.gameObject);
                }
            }

            _slotPool.Clear();
            _assembled = false;

            // The body root goes with them: it carries the definition's scale, and a rebuild after an
            // import must pick up a changed one rather than keep the transform it was born with.
            if (_bodyRoot != null)
            {
                DestroyObject(_bodyRoot.gameObject);
                _bodyRoot = null;
            }

            foreach (Sprite s in _pivotCache.Values)
            {
                DestroyObject(s);
            }
            _pivotCache.Clear();
        }

        // ─── Overlay clips ───────────────────────────────────────────────────

        /// <summary>
        /// Advances the running overlay playheads. <b>The one tick in this class.</b>
        ///
        /// <para><b>Why the assembler ticks rather than <c>CharacterAnimationDriver</c>.</b>
        /// The driver's frame timer belongs to the character's <i>state clip</i> — its job is picking
        /// <c>walk</c> over <c>stay</c> and walking that clip's frames. An overlay is not in that
        /// machine at all: it animates while the body holds still, and it must animate in the animation
        /// preview too, where there is no driver. Owning the tick here keeps one cadence for overlays
        /// wherever the assembler is used. It is view-only, so an Update is allowed — the sim may not,
        /// a view may.</para>
        /// </summary>
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

                var go = new GameObject($"overlay_{def.overlayName}");
                go.transform.SetParent(transform, false);

                // Deliberately NOT `SetActive(false)`. Visibility here is the renderer's `enabled` flag,
                // because that is what every hide branch in SyncOverlays writes; a deactivated
                // GameObject ignores that flag and draws nothing. This object used to be deactivated at
                // build and never reactivated, so the player's sp_mshit dome (vis.shit) was invisible
                // with a correct gate, 20 imported frames, a resolved sprite and sorting order 1000 —
                // the one thing wrong was that its GameObject was switched off. Draw() re-asserts the
                // activation so the same mistake cannot come back silently.
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sortingLayerName = _sortingLayerName;
                sr.sortingOrder = def.sortingOrder;

                _overlaySlots.Add(new OverlaySlot
                {
                    def = def,
                    gameObject = go,
                    transform = go.transform,
                    renderer = sr,
                    frame = OverlayClip.NoFrame,
                    visible = false,
                });
            }
        }

        void ClearOverlays()
        {
            foreach (OverlaySlot slot in _overlaySlots)
            {
                if (slot.gameObject != null)
                {
                    slot.gameObject.SetActive(false);
                    DestroyObject(slot.gameObject);
                }
            }

            _overlaySlots.Clear();
            _overlayTimer = 0f;
        }

        /// <summary>
        /// Bring every overlay in line with the visual context, advancing the playheads that are
        /// already running by <paramref name="steps"/> frames.
        ///
        /// <para>Called every Update with the frames due, and again with <c>0</c> whenever the context
        /// is pushed, so an edge lands at once. The edge handling is deliberately <i>inside</i> this
        /// method rather than in the caller: the rise restarts the playhead and the fall resets it, and
        /// splitting that from the advance would let the two disagree.</para>
        /// </summary>
        void SyncOverlays(int steps)
        {
            foreach (OverlaySlot slot in _overlaySlots)
            {
                CharacterOverlayDefinition def = slot.def;
                if (def == null) continue;

                int frameCount = def.frames?.Length ?? 0;
                bool gate = _visualContext.IsOverlayVisible(def.overlayName);

                if (!OverlayClip.ShouldDraw(gate, frameCount))
                {
                    // The fall edge — AS3 vis.shit.visible = false; gotoAndStop(1). The playhead is
                    // written on the way down as well as on the way up, so the next rise replays the
                    // materialise animation instead of appearing already complete.
                    slot.frame = OverlayClip.RestartFrame(frameCount);
                    slot.visible = false;
                    slot.renderer.enabled = false;
                    continue;
                }

                if (!slot.visible)
                {
                    // The rise edge — AS3 vis.shit.visible = true; gotoAndPlay(1).
                    slot.frame = OverlayClip.RestartFrame(frameCount);
                    slot.visible = true;
                }
                else
                {
                    for (int i = 0; i < steps; i++)
                    {
                        slot.frame = OverlayClip.NextFrame(slot.frame, frameCount, def.loopMode);
                    }
                }

                ApplyOverlayVisual(slot, frameCount);
            }
        }

        void ApplyOverlayVisual(OverlaySlot slot, int frameCount)
        {
            int index = OverlayClip.ClampFrame(slot.frame, frameCount);
            if (index == OverlayClip.NoFrame)
            {
                slot.renderer.enabled = false;
                return;
            }

            Sprite source = slot.def.frames[index];
            Sprite sprite = GetPivotedSprite(source, slot.def.pivotNormalized);

            // A disabled GameObject renders nothing however `enabled` its SpriteRenderer is, so the draw
            // path asserts its own liveness instead of trusting whoever built the slot. See BuildOverlays.
            if (!slot.gameObject.activeSelf) slot.gameObject.SetActive(true);

            slot.renderer.sprite = sprite;
            // An overlay is never tinted: tintCategory belongs to the body, and the shield is a
            // magical dome rather than fur or hair. Drawing it in the character's coat colour would
            // be the obvious wrong answer here.
            slot.renderer.color = Color.white;
            slot.renderer.enabled = sprite != null;

            slot.transform.localPosition = new Vector3(slot.def.localPosition.x, slot.def.localPosition.y, 0f);
            slot.transform.localRotation = Quaternion.identity;

            float scale = slot.def.localScale <= 0f ? 1f : slot.def.localScale;
            slot.transform.localScale = new Vector3(scale, scale, 1f);
        }

        // ─── Frame application ───────────────────────────────────────────────

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

            // Hide all pool slots.
            foreach (PartSlot slot in _slotPool)
            {
                slot.gameObject.SetActive(false);
                slot.renderer.enabled = false;
                if (slot.secondaryRenderer != null) slot.secondaryRenderer.enabled = false;
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
            HashSet<string> hiddenHeadParts = BuildHiddenHeadParts(armorSet, out bool usesMordaComposite);
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

                // ── Part visibility filters ──────────────────────────────────
                if (part.partName == "mane"     && clip.useRunMane)  continue;
                if (part.partName == "mane_run" && !clip.useRunMane) continue;
                if (part.partName == "tail"     && clip.useRunTail)  continue;
                if (part.partName == "tail_run" && !clip.useRunTail) continue;
                if (hideMane && (part.partName == "mane" || part.partName == "mane_run")) continue;
                if (hiddenHeadParts.Contains(part.partName)) continue;
                if (part.partName == "morda_armor" && !usesMordaComposite) continue;

                // Wings are hidden unless the visual context explicitly enables them.
                if ((part.partName == "lwing" || part.partName == "rwing") && !_visualContext.showWings) continue;

                // ── Pool slot assignment ─────────────────────────────────────
                PartSlot slot = _slotPool[slotIdx];
                slot.partIndex = placement.partIndex;
                slot.partDef = part;

                // Lazily add a secondary layer to this pool slot if needed.
                EnsureSecondaryLayer(slot);

                // ── Visual resolution ────────────────────────────────────────
                ResolvedPartVisual visual = ResolvePartVisual(slot, armorSet, placement);
                if (visual.fromArmor) ArmourPartsDrawn++;

                // The horn aura is the one part whose colour is owned by the simulation rather than
                // by the frame data: its tint channel is already the "magical aura colour"
                // (TintCategory.Magic), but its alpha comes from the charge ramp. See HornAura.
                bool isHornAura = part.partName == HornAura.PartName;
                Color primaryColor = visual.primaryColor;
                if (isHornAura)
                {
                    primaryColor.a *= HornAura.Normalized(_hornAuraLevel);
                }

                slot.gameObject.SetActive(true);
                slot.renderer.sprite = visual.primarySprite;
                slot.renderer.color = primaryColor;
                // AS3 keeps the aura's `visible` tied to its alpha (`UnitPlayer.as:4852`), so an
                // uncharged disc is not drawn at all rather than drawn transparent — same picture,
                // one fewer draw call, and it keeps "is the aura on" answerable from the renderer.
                slot.renderer.enabled = visual.primarySprite != null && (!isHornAura || primaryColor.a > 0f);
                slot.renderer.sortingOrder = slotIdx * 2;

                if (slot.hasSecondaryLayer && slot.secondaryRenderer != null)
                {
                    slot.secondaryRenderer.sprite = visual.secondarySprite;
                    slot.secondaryRenderer.color = visual.secondaryColor;
                    slot.secondaryRenderer.enabled = visual.showSecondary && visual.secondarySprite != null;
                    slot.secondaryRenderer.sortingOrder = slotIdx * 2 + 1;
                }

                // ── Transform ────────────────────────────────────────────────
                // The armour's own offset is in the head container's space, so it is scaled and
                // rotated by this placement before it can be added to a root-space position —
                // ArmorOffsets.LocalToRoot, shared with the quad renderer so the two cannot drift.
                slot.transform.localPosition = new Vector3(
                    placement.localPosition.x + visual.bakedOffset.x,
                    placement.localPosition.y + visual.bakedOffset.y,
                    0f);
                slot.transform.localRotation = Quaternion.Euler(0f, 0f, placement.localRotation);
                slot.transform.localScale = new Vector3(placement.localScale.x, placement.localScale.y, 1f);

                slotIdx++;
            }
        }

        /// <summary>Lazily creates a secondary (h1) renderer on a pool slot if the part needs one.</summary>
        void EnsureSecondaryLayer(PartSlot slot)
        {
            bool needs = NeedsSecondaryLayer(slot.partDef?.partName);
            if (needs && !slot.hasSecondaryLayer)
            {
                var h1Go = new GameObject($"{slot.gameObject.name}_h1");
                h1Go.transform.SetParent(slot.transform, false);
                h1Go.layer = slot.gameObject.layer;

                var h1Sr = h1Go.AddComponent<SpriteRenderer>();
                h1Sr.sprite = null;
                h1Sr.sortingLayerName = _sortingLayerName;

                slot.secondaryRenderer = h1Sr;
                slot.hasSecondaryLayer = true;
            }
        }

        static bool NeedsSecondaryLayer(string partName)
        {
            return partName is "mane" or "mane_run" or "tail" or "tail_run" or "forelock";
        }

        // ─── Armor helpers ───────────────────────────────────────────────────

        ArmorVisualSet GetActiveArmorSet()
        {
            if (_definition == null || string.IsNullOrWhiteSpace(_visualContext.armorId))
            {
                return null;
            }

            return _definition.GetArmorSet(_visualContext.armorId);
        }

        HashSet<string> BuildHiddenHeadParts(ArmorVisualSet armorSet, out bool usesMordaComposite)
        {
            var hiddenHeadParts = new HashSet<string>();
            usesMordaComposite = false;

            if (armorSet == null)
            {
                return hiddenHeadParts;
            }

            if (armorSet.hiddenHeadParts != null)
            {
                foreach (string hiddenPart in armorSet.hiddenHeadParts)
                {
                    if (!string.IsNullOrEmpty(hiddenPart)) hiddenHeadParts.Add(hiddenPart);
                }
            }

            // Check if morda_armor has an armor override sprite (morda composite mode).
            // Look up the part index from the definition directly — no slot dict needed.
            if (_definition?.parts != null)
            {
                for (int i = 0; i < _definition.parts.Length; i++)
                {
                    if (_definition.parts[i].partName == "morda_armor")
                    {
                        if (TryGetArmorOverride(i, armorSet, out ArmorPartOverride mordaOverride))
                        {
                            usesMordaComposite = mordaOverride.armorSprite != null;
                        }
                        break;
                    }
                }
            }

            if (usesMordaComposite)
            {
                foreach (string partName in HeadSubPartNames)
                {
                    hiddenHeadParts.Add(partName);
                }
            }

            return hiddenHeadParts;
        }

        // ─── Visual resolution ───────────────────────────────────────────────

        ResolvedPartVisual ResolvePartVisual(PartSlot slot, ArmorVisualSet armorSet,
                                             PartFramePlacement placement)
        {
            if (TryGetArmorOverride(slot.partIndex, armorSet, out ArmorPartOverride armorOverride))
            {
                return new ResolvedPartVisual
                {
                    primarySprite = GetPivotedSprite(
                        armorOverride.armorSprite,
                        ArmorPivots.Resolve(slot.partDef, armorOverride)),
                    fromArmor = true,
                    primaryColor = Color.white,
                    secondarySprite = null,
                    secondaryColor = Color.white,
                    showSecondary = false,
                    bakedOffset = ArmorOffsets.Total(armorOverride, placement)
                };
            }

            ResolveStyledSprites(slot, out Sprite primarySprite, out Sprite secondarySprite);
            return new ResolvedPartVisual
            {
                primarySprite = primarySprite,
                primaryColor = ResolveBaseTint(slot.partDef.tintCategory, false),
                secondarySprite = secondarySprite,
                secondaryColor = ResolveBaseTint(slot.partDef.tintCategory, true),
                showSecondary = slot.hasSecondaryLayer && _appearance.showSecondaryHair,
                bakedOffset = Vector2.zero
            };
        }

        void ResolveStyledSprites(PartSlot slot, out Sprite primarySprite, out Sprite secondarySprite)
        {
            Vector2 pivot = slot.partDef.pivotNormalized;

            // Always apply the correct pivot from the part definition.
            // The animation preview window does this via Sprite.Create per render;
            // here we cache per (source, pivot) pair to avoid per-frame allocations.
            primarySprite = GetPivotedSprite(slot.partDef.baseSprite, pivot);
            secondarySprite = null;

            if (_styleData == null)
            {
                return;
            }

            if (slot.partDef.tintCategory == TintCategory.Hair)
            {
                string stylePartName = GetHairStylePartName(slot.partDef.partName);
                if (!string.IsNullOrEmpty(stylePartName))
                {
                    int hairIndex = Mathf.Clamp(_appearance.hairStyle - 1, 0, Mathf.Max(0, _definition.hairStyleCount - 1));
                    Sprite ps = _styleData.GetLayerSprite(stylePartName, "Primary", hairIndex);
                    if (ps != null) primarySprite = GetPivotedSprite(ps, pivot);

                    if (slot.hasSecondaryLayer)
                    {
                        Sprite ss = _styleData.GetLayerSprite(stylePartName, "Secondary", hairIndex);
                        if (ss != null) secondarySprite = GetPivotedSprite(ss, pivot);
                    }
                }
            }
            else if (slot.partDef.partName == "eye")
            {
                int eyeIndex = Mathf.Clamp(_appearance.eyeStyle - 1, 0, Mathf.Max(0, _definition.eyeStyleCount - 1));
                Sprite es = _styleData.GetLayerSprite("Eye", "Iris", eyeIndex);
                if (es != null) primarySprite = GetPivotedSprite(es, pivot);
            }
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

        /// <summary>
        /// Returns a Sprite with the given pivot, creating and caching it on first use.
        /// Matches what CharacterAnimationPreviewWindow does via Sprite.Create per render,
        /// but avoids repeated allocations by caching on (source, pivot).
        /// </summary>
        Sprite GetPivotedSprite(Sprite source, Vector2 pivot)
        {
            if (source == null) return null;

            var key = (source, pivot);
            if (_pivotCache.TryGetValue(key, out Sprite cached)) return cached;

            Sprite created = Sprite.Create(
                source.texture,
                source.textureRect,
                pivot,
                source.pixelsPerUnit);
            _pivotCache[key] = created;
            return created;
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

        // ─── Unity lifecycle ─────────────────────────────────────────────────

        void Awake()
        {
            if (_definition != null && !_assembled)
            {
                Rebuild();
            }
        }

        void OnDestroy()
        {
            ClearSlots(); // destroys slot GOs and clears the pivot cache
        }

        /// <summary>
        /// <c>Destroy</c> is deferred and never runs in edit mode, where the animation preview window
        /// builds a transient rig; <c>DestroyImmediate</c> is illegal while playing. This picks the
        /// right one for both. At runtime it is exactly the <c>Destroy</c> it replaces.
        /// </summary>
        static void DestroyObject(Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }

        // ─── Data types ──────────────────────────────────────────────────────

        struct ResolvedPartVisual
        {
            public Sprite primarySprite;
            /// <summary>True when the sprite came from the worn armour set, not from the part.</summary>
            public bool fromArmor;
            public Color primaryColor;
            public Sprite secondarySprite;
            public Color secondaryColor;
            public bool showSecondary;
            public Vector2 bakedOffset;
        }

        public class PartSlot
        {
            public GameObject gameObject;
            public Transform transform;
            public SpriteRenderer renderer;
            public int partIndex;
            public CharacterPartDefinition partDef;
            public SpriteRenderer secondaryRenderer;
            public bool hasSecondaryLayer;
        }

        /// <summary>
        /// One overlay clip's renderer and playhead. Unlike a <see cref="PartSlot"/> this is not pooled
        /// and not per-placement: there is exactly one renderer per overlay definition, because an
        /// overlay is a single clip on the container rather than a part that can appear twice in a
        /// frame at two different depths.
        /// </summary>
        public class OverlaySlot
        {
            public CharacterOverlayDefinition def;
            public GameObject gameObject;
            public Transform transform;
            public SpriteRenderer renderer;

            /// <summary>Playhead, in frames. <see cref="OverlayClip.NoFrame"/> before the first sync.</summary>
            public int frame = OverlayClip.NoFrame;

            /// <summary>
            /// Whether the previous sync drew this overlay. The rise and fall <i>edges</i> are what
            /// restart the playhead, so the previous value has to be remembered — the gate alone cannot
            /// tell a shield that is holding from a shield that has just gone up.
            /// </summary>
            public bool visible;
        }
    }
}
