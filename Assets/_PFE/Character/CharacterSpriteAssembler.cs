using System.Collections.Generic;
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
    /// </summary>
    public class CharacterSpriteAssembler : MonoBehaviour
    {
        static readonly HashSet<string> HeadSubPartNames = new()
        {
            "morda_base", "morda_overlay", "eye", "forelock", "horn", "magic", "konec", "helm"
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

        // Caches Sprite instances rebuilt with the correct pivotNormalized.
        // Key: (source sprite, pivot) — created once, destroyed on ClearSlots.
        readonly Dictionary<(Sprite, Vector2), Sprite> _pivotCache = new();

        string _currentState;
        int _currentFrame;
        bool _assembled;

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
            _assembled = true;

            if (string.IsNullOrEmpty(_currentState))
            {
                SetState("stay", 0);
            }
            else
            {
                ReapplyCurrentFrame();
            }
        }

        public void ApplyAppearance()
        {
            if (!_assembled || _appearance == null)
            {
                return;
            }

            ReapplyCurrentFrame();
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
        }

        void BuildSlots()
        {
            if (_definition?.parts == null || _definition.stateClips == null)
            {
                return;
            }

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
                go.transform.SetParent(transform, false);
                go.SetActive(false);

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

        void ClearSlots()
        {
            foreach (PartSlot slot in _slotPool)
            {
                if (slot.gameObject != null)
                {
                    slot.gameObject.SetActive(false);
                    Destroy(slot.gameObject);
                }
            }

            _slotPool.Clear();
            _assembled = false;

            foreach (Sprite s in _pivotCache.Values)
            {
                if (s != null) Destroy(s);
            }
            _pivotCache.Clear();
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
                ResolvedPartVisual visual = ResolvePartVisual(slot, armorSet);

                slot.gameObject.SetActive(true);
                slot.renderer.sprite = visual.primarySprite;
                slot.renderer.color = visual.primaryColor;
                slot.renderer.enabled = visual.primarySprite != null;
                slot.renderer.sortingOrder = slotIdx * 2;

                if (slot.hasSecondaryLayer && slot.secondaryRenderer != null)
                {
                    slot.secondaryRenderer.sprite = visual.secondarySprite;
                    slot.secondaryRenderer.color = visual.secondaryColor;
                    slot.secondaryRenderer.enabled = visual.showSecondary && visual.secondarySprite != null;
                    slot.secondaryRenderer.sortingOrder = slotIdx * 2 + 1;
                }

                // ── Transform ────────────────────────────────────────────────
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

        ResolvedPartVisual ResolvePartVisual(PartSlot slot, ArmorVisualSet armorSet)
        {
            if (TryGetArmorOverride(slot.partIndex, armorSet, out ArmorPartOverride armorOverride))
            {
                return new ResolvedPartVisual
                {
                    primarySprite = GetPivotedSprite(armorOverride.armorSprite, armorOverride.pivotOverride),
                    primaryColor = Color.white,
                    secondarySprite = null,
                    secondaryColor = Color.white,
                    showSecondary = false,
                    bakedOffset = armorOverride.positionOffset
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

        // ─── Data types ──────────────────────────────────────────────────────

        struct ResolvedPartVisual
        {
            public Sprite primarySprite;
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
    }
}
