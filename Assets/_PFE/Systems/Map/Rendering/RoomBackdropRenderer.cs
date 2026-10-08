using System;
using System.Collections.Generic;
using UnityEngine;
using Profiler = PFE.Core.Profiling.PfeProfiler;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Renders room-level background decorations and water overlays.
    /// This is separate from tile rendering because these visuals are room-scoped
    /// and not tied to the lifetime of individual TileRenderer instances.
    /// </summary>
    public class RoomBackdropRenderer
    {
        private const int TileSizePixels = 40;
        private const int BackdropBaseDarkness = 170;
        private const int BackdropSortingOrder = -5000;
        private const int DecorationSortingBase = -1000;
        private const int VisibilityMaskSortingOrder = 5000;
        private const int BackgroundTileShadowSortingOrder = 1000;
        private const int ShadowDistancePixels = 7;
        private const int ShadowBlurPixels = 16;
        private const int ShadowBlurIterations = 3;
        private const float ShadowOpacity = 0.75f;
        private const float ShadowResolutionScale = 0.25f;
        private const float DefaultInnerLightRadiusPixels = 300f;
        private const float DefaultOuterLightRadiusPixels = 1000f;
        private const float LightRevealRiseSpeed = 0.1f;
        private const float LightRevealFallSpeed = 0.025f;

        /// <summary>
        /// AS3 `Location.lighting():3142` sets <c>relight_t = 10</c>, and `step():3402` runs that many
        /// <c>lighting2()</c> frames after every full pass. At 30 fps that is the ~0.33 s fade-in.
        /// </summary>
        private const int RelightFollowUpFrames = 10;
        private const float LightSourceSpreadPixels = 10f;
        // AS3 walks the shadow ray one *tile* at a time: `Location.lighting()` uses
        // `_loc13_ = Tile.tileX` (40, `Tile.as:8`) and `_loc15_ = _loc9_ / _loc13_`. The port
        // shipped 20 px, which is twice AS3's sample count along an axis and ~2.8x on a diagonal
        // (AS3 walks the dominant axis, the port walked the euclidean length). Restored to 40 px
        // with the dominant-axis walk in SampleLightTransmission.
        private const float LightOcclusionStepPixels = 40f;
        private static readonly Vector3 BackgroundScale = new Vector3(1f, 1f, 1f);

        private readonly RoomInstance _room;
        private readonly TileTextureLookup _tileTextureLookup;
        private readonly RoomBackgroundLookup _backgroundLookup;
        private readonly RoomBackdropSettingsLookup _settingsLookup;
        private readonly string _roomKey;
        private readonly Transform _backgroundParent;
        private readonly Transform _visibilityMaskParent;
        private readonly Vector2 _backdropTextureScale;
        private readonly Vector2 _backdropTextureOffset;
        private readonly bool _flipBackdropTextureX;
        private readonly bool _flipBackdropTextureY;
        private readonly RoomBackdropSettingsLookup.TintSettings _globalBackdropTint;
        private readonly float _backdropSharpenStrength;
        private readonly RoomBackdropSettingsLookup.TintSettings _globalDecorationTint;
        private readonly bool _disableBackdropShadowBake;

        private readonly List<SpriteRenderer> _backdropRenderers = new List<SpriteRenderer>();
        private readonly List<SpriteRenderer> _backgroundTileShadowRenderers = new List<SpriteRenderer>();
        private readonly List<SpriteRenderer> _backgroundRenderers = new List<SpriteRenderer>();
        private readonly List<SpriteRenderer> _visibilityMaskRenderers = new List<SpriteRenderer>();
        private readonly List<SpriteRenderer> _waterRenderers = new List<SpriteRenderer>();
        private readonly HashSet<string> _missingIds = new HashSet<string>();
        private readonly List<UnityEngine.Object> _generatedAssets = new List<UnityEngine.Object>();
        private readonly Dictionary<Texture2D, Texture2D> _readableTextureCache = new Dictionary<Texture2D, Texture2D>();
        private bool _fogOfWarDisabled;

        public bool FogOfWarDisabled => _fogOfWarDisabled;

        /// <summary>
        /// Number of baked, non-moving light sources — the AS3 <c>lightAll()</c> set — as of the last
        /// <see cref="RebuildLightSources"/> (which <see cref="CreateVisuals"/> calls once per room
        /// activation). Reading it before any bake returns 0.
        ///
        /// Exposed so the fog regression tests can pin the emitter rule without a running engine:
        /// an id-substring match ("light"/"lamp"/"torch") must **not** create a source, and a room
        /// object must only emit when it carries the <c>light</c> attribute. Each emitter contributes
        /// three sources (the ±10 px softness triplet, AS3 `lightAll()`).
        /// </summary>
        public int StaticLightSourceCount => _staticLightSources.Count;

        public void SetFogOfWarDisabled(bool disabled)
        {
            _fogOfWarDisabled = disabled;
            for (int i = 0; i < _visibilityMaskRenderers.Count; i++)
            {
                if (_visibilityMaskRenderers[i] != null)
                {
                    _visibilityMaskRenderers[i].enabled = !disabled;
                }
            }
            if (_visibilityMaskParent != null)
            {
                _visibilityMaskParent.gameObject.SetActive(!disabled);
            }
            _visibilityMaskDirty = true;
        }

        public void RevealAll()
        {
            if (_visibilityMaskCurrentVisibility == null || _visibilityMaskPixels == null)
            {
                return;
            }

            for (int i = 0; i < _visibilityMaskCurrentVisibility.Length; i++)
            {
                _visibilityMaskCurrentVisibility[i] = 1f;
                _visibilityMaskPixels[i] = Color.clear;
            }

            // Pin the targets too, and stop any relight follow-up: otherwise the next frames would
            // keep converging toward the pre-reveal targets and darken the room again.
            for (int i = 0; i < _visibilityMaskTargetVisibility.Length; i++)
            {
                _visibilityMaskTargetVisibility[i] = 1f;
            }

            _relightFramesRemaining = 0;

            if (_visibilityMaskTexture != null)
            {
                _visibilityMaskTexture.SetPixels(_visibilityMaskPixels);
                _visibilityMaskTexture.Apply(false, false);
            }
            _visibilityMaskDirty = false;
        }

        /// <summary>
        /// Cached <c>Color[]</c> per readable texture, so per-pixel loops can index instead of
        /// calling <c>GetPixel</c>.
        ///
        /// Why this matters: <c>GetPixel</c> is a managed→native interop call (~0.1-1 µs), not a
        /// field read. The room backdrop is 48×25 tiles at 40 px = 1.92M pixels, and the loop in
        /// <see cref="CreateBackdropSprite"/> was measured at 1197 ms per call — ~0.62 µs per
        /// pixel, which is interop, not arithmetic. The composite maths it interleaves
        /// (<see cref="ApplyBackdropComposite"/>) is pure managed code and costs almost nothing.
        ///
        /// This is the same defect (and the same fix) as <c>TileCompositor.CacheTexturePixels</c>,
        /// which was the root cause of the original 54 s boot.
        /// </summary>
        private readonly Dictionary<Texture2D, Color[]> _readablePixelCache = new Dictionary<Texture2D, Color[]>();

        private Color[] GetReadablePixels(Texture2D readable)
        {
            if (readable == null)
            {
                return null;
            }

            if (_readablePixelCache.TryGetValue(readable, out Color[] cached))
            {
                return cached;
            }

            Color[] pixels = readable.GetPixels();
            _readablePixelCache[readable] = pixels;
            return pixels;
        }

        /// <summary>
        /// Scratch list rebuilt on every visibility refresh: the baked static set plus the player triplet.
        /// </summary>
        private readonly List<LightSource> _lightSources = new List<LightSource>();

        /// <summary>
        /// The non-moving emitters of this room — AS3's <c>Location.lightAll()</c> set, baked once per
        /// room build rather than re-derived on every refresh.
        ///
        /// AS3 bakes these exactly once, from <c>Land.ativateLoc()</c> (<c>Land.as:1273</c>), and its
        /// per-frame <c>Location.step()</c> path then calls <c>lighting()</c> with **no arguments** —
        /// a single source at the camera. Re-deriving the lamp set per refresh is a port invention and
        /// was the dominant cost of the fog pass (see docs/Perf_FogOfWar_RoomLag_Investigation.md).
        /// </summary>
        private readonly List<LightSource> _staticLightSources = new List<LightSource>();

        /// <summary>
        /// <c>_room.objects.Count</c> at the time the static set was baked. A change means props were
        /// added or removed, so the lamp set is re-derived. Cheap O(1) guard; AS3 has no equivalent
        /// because its object list is fixed for the life of a room activation.
        /// </summary>
        private int _staticLightSourceObjectCount = -1;

        /// <summary>
        /// The tile-opacity lookup handed to <see cref="FogOcclusionMath.ResolveRayTransmission"/>, so
        /// that pure ray rule needs no room reference of its own.
        ///
        /// <para>Cached as a field on purpose: the ray is walked once per light per tile, so a closure
        /// built at the call site would allocate tens of thousands of times per full pass.</para>
        /// </summary>
        private readonly Func<Vector2, float> _tileOpacityAt;

        private Texture2D _visibilityMaskTexture;
        private Sprite _visibilityMaskSprite;
        private Vector2Int _visibilityMaskTextureSize = Vector2Int.zero;
        private float[] _visibilityMaskCurrentVisibility = Array.Empty<float>();

        /// <summary>
        /// AS3 <c>Tile.t_visi</c> — the target visibility the light pass computed for each texel.
        /// Cached so the follow-up frames can converge toward it without re-running the light loop.
        /// </summary>
        private float[] _visibilityMaskTargetVisibility = Array.Empty<float>();

        private Color[] _visibilityMaskPixels = Array.Empty<Color>();
        private bool _visibilityMaskDirty = true;

        /// <summary>
        /// AS3 <c>Location.relight_t</c> — set to 10 at the top of <c>lighting():3142</c>, decremented
        /// by <c>lighting2():3266</c>, which <c>step():3402</c> calls on the frames that follow a full
        /// pass. This is what performs the reveal *animation*: <c>lighting()</c> only writes
        /// <c>t_visi</c> and takes a single <c>updVisi()</c> step, and the remaining ten
        /// <c>lighting2()</c> frames finish it. Without it the port's reveal would advance only when
        /// the player crossed a tile boundary and would stall while the player stood still.
        /// </summary>
        private int _relightFramesRemaining;

        /// <summary>
        /// True while <see cref="_visibilityMaskPixels"/> is known to disagree with the uploaded
        /// texture — set whenever the buffer is (re)allocated. Needed because
        /// <see cref="UpdateVisibilityMask"/> only uploads when a texel changed, and a buffer that was
        /// just (re)allocated has never been pushed. See <see cref="EnsureVisibilityMaskBuffers"/> for
        /// why the buffer is initialised to opaque darkness rather than left at the
        /// <c>Color[]</c> default.
        /// </summary>
        private bool _visibilityMaskUploadPending = true;

        private bool _lastPlayerSampleValid;
        private Vector2Int _lastPlayerSample = new Vector2Int(int.MinValue, int.MinValue);

        private readonly struct BackdropCompositeData
        {
            public readonly float DarknessAlpha;
            public readonly float[] ShadowAlpha;
            public readonly Vector2Int ShadowSize;

            public BackdropCompositeData(float darknessAlpha, float[] shadowAlpha, Vector2Int shadowSize)
            {
                DarknessAlpha = darknessAlpha;
                ShadowAlpha = shadowAlpha;
                ShadowSize = shadowSize;
            }
        }

        private readonly struct LightSource
        {
            public readonly Vector2 PositionPixels;
            public readonly float InnerRadiusPixels;
            public readonly float OuterRadiusPixels;
            public readonly float Intensity;

            public LightSource(Vector2 positionPixels, float innerRadiusPixels, float outerRadiusPixels, float intensity)
            {
                PositionPixels = positionPixels;
                InnerRadiusPixels = innerRadiusPixels;
                OuterRadiusPixels = Mathf.Max(innerRadiusPixels + 1f, outerRadiusPixels);
                Intensity = Mathf.Clamp01(intensity);
            }
        }

        public RoomBackdropRenderer(
            RoomInstance room,
            TileTextureLookup tileTextureLookup,
            RoomBackgroundLookup backgroundLookup,
            RoomBackdropSettingsLookup settingsLookup,
            string roomKey,
            Transform backgroundParent,
            Transform visibilityMaskParent,
            Vector2 backdropTextureScale,
            Vector2 backdropTextureOffset,
            bool flipBackdropTextureX,
            bool flipBackdropTextureY,
            RoomBackdropSettingsLookup.TintSettings globalBackdropTint,
            float backdropSharpenStrength,
            RoomBackdropSettingsLookup.TintSettings globalDecorationTint,
            bool disableBackdropShadowBake = false)
        {
            _room = room;
            _tileTextureLookup = tileTextureLookup;
            _backgroundLookup = backgroundLookup;
            _settingsLookup = settingsLookup;
            _roomKey = roomKey;
            _backgroundParent = backgroundParent;
            _visibilityMaskParent = visibilityMaskParent;
            _backdropTextureScale = new Vector2(
                Mathf.Max(0.01f, backdropTextureScale.x),
                Mathf.Max(0.01f, backdropTextureScale.y));
            _backdropTextureOffset = backdropTextureOffset;
            _flipBackdropTextureX = flipBackdropTextureX;
            _flipBackdropTextureY = flipBackdropTextureY;
            _globalBackdropTint = globalBackdropTint;
            _backdropSharpenStrength = Mathf.Max(0f, backdropSharpenStrength);
            _globalDecorationTint = globalDecorationTint;
            _disableBackdropShadowBake = disableBackdropShadowBake;
            _tileOpacityAt = ResolveLightBlockingOpacityAt;
        }

        public void CreateVisuals()
        {
            DestroyVisuals();

            if (_room == null || _backgroundParent == null)
            {
                return;
            }

            Vector2Int contentPixelSize;
            Vector2 contentOriginPixels;
            BackdropCompositeData compositeData;
            using (Profiler.Region("backdrop.compositeData", "boot: geometry + composite planning for the backdrop, before any pixels are touched"))
            {
                contentPixelSize = GetContentPixelSize();
                contentOriginPixels = GetContentOriginPixels();
                compositeData = BuildBackdropCompositeData(contentOriginPixels, contentPixelSize);
            }

            // Measured 2026-09-25: the whole of CreateVisuals was 1453 ms, the second largest
            // boot item after Resources.LoadAll. These five regions split it so we can see which
            // step actually costs — they are NOT assumed to be equal.
            using (Profiler.Region("backdrop.roomBackdrop", "boot: CreateRoomBackdrop — fill rects + backdrop sprite"))
            {
                CreateRoomBackdrop(contentOriginPixels, contentPixelSize, compositeData);
            }

            using (Profiler.Region("backdrop.tileShadow", "boot: CreateBackgroundTileShadowOverlay — per-tile shadow bake"))
            {
                CreateBackgroundTileShadowOverlay(contentOriginPixels, contentPixelSize, compositeData);
            }

            using (Profiler.Region("backdrop.decorations", "boot: CreateBackgroundDecorations"))
            {
                CreateBackgroundDecorations(contentOriginPixels, contentPixelSize, compositeData);
            }

            using (Profiler.Region("backdrop.water", "boot: CreateWaterOverlays"))
            {
                CreateWaterOverlays();
            }

            if (UsesVisibilityMask())
            {
                using (Profiler.Region("backdrop.visibilityMask", "boot: CreateVisibilityMaskOverlay + UpdateVisibilityMask — per-pixel light loop"))
                {
                    // AS3 bakes the room's lamp lights here — `Land.ativateLoc()` -> `Location.lightAll()`
                    // — once per room activation, not once per visibility pass.
                    RebuildLightSources();
                    CreateVisibilityMaskOverlay();
                    UpdateVisibilityMask();
                }
            }
        }

        public void DestroyVisuals()
        {
            DestroyRendererList(_backdropRenderers);
            DestroyRendererList(_backgroundTileShadowRenderers);
            DestroyRendererList(_backgroundRenderers);
            DestroyRendererList(_visibilityMaskRenderers);
            DestroyRendererList(_waterRenderers);
            DestroyGeneratedAssets();
            _visibilityMaskTexture = null;
            _visibilityMaskSprite = null;
            _visibilityMaskTextureSize = Vector2Int.zero;
            _visibilityMaskCurrentVisibility = Array.Empty<float>();
            _visibilityMaskTargetVisibility = Array.Empty<float>();
            _visibilityMaskPixels = Array.Empty<Color>();
            _relightFramesRemaining = 0;
            _lightSources.Clear();
            _staticLightSources.Clear();
            _staticLightSourceObjectCount = -1;
            _visibilityMaskDirty = true;
            _visibilityMaskUploadPending = true;
            _lastPlayerSampleValid = false;
            _lastPlayerSample = new Vector2Int(int.MinValue, int.MinValue);
        }

        public void UpdateVisibilityMask(Vector3? playerWorldPosition = null)
        {
            if (_fogOfWarDisabled || _room == null || !UsesVisibilityMask() || _visibilityMaskTexture == null || _visibilityMaskRenderers.Count == 0)
            {
                return;
            }

            Vector2Int roomPixelSize = GetRoomPixelSize();
            if (roomPixelSize.x <= 0 || roomPixelSize.y <= 0)
            {
                return;
            }

            EnsureVisibilityMaskBuffers(roomPixelSize);

            // AS3 has two phases. `lighting()` is the full pass — it re-rays the lights, writes
            // `t_visi`, and sets `relight_t = 10` (`Location.as:3142`). `step():3402` then runs
            // `lighting2()` on the next ten frames, which only walks the tiles and pushes the ones
            // whose smoothed value is still moving. That split is what makes the reveal an animation:
            // the expensive part happens once and the cheap part finishes it. The port used to run
            // the full pass on every refresh, which meant the reveal advanced one 0.1 step per tile
            // of player movement and stalled completely while the player stood still.
            bool fullPass = ShouldRefreshVisibilityMask(playerWorldPosition, roomPixelSize);
            if (!fullPass && _relightFramesRemaining <= 0)
            {
                return;
            }

            if (fullPass)
            {
                BuildLightSources(playerWorldPosition);
                _relightFramesRemaining = RelightFollowUpFrames;
            }
            else
            {
                _relightFramesRemaining--;
            }

            bool returnsToDarkness = _room.environment != null && _room.environment.returnsToDarkness;
            float ambientVisibility = ResolveAmbientVisibility();
            int width = _visibilityMaskTextureSize.x;
            int height = _visibilityMaskTextureSize.y;

            // Only upload when a texel actually moved — a follow-up frame that finds everything
            // already converged must not cost a texture upload.
            bool anyPixelChanged = _visibilityMaskUploadPending;

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * width;
                for (int x = 0; x < width; x++)
                {
                    // AS3 never assigns a light target to the room's outer ring on the low edge of each
                    // axis — `Location.lighting():3143-3148` and `Grafon.setLight():689-699` both start
                    // at 1 — and `lightBmp` (49x28 for the 48x25 grid, Grafon.as:141-143) keeps its
                    // constructor's opaque black on the cells that loop never reaches. So these texels
                    // must stay at the darkness `EnsureVisibilityMaskBuffers` gave them: not merely
                    // unlit, but never "known" either. See FogOcclusionMath.IsInsideRevealArea.
                    if (!FogOcclusionMath.IsInsideRevealArea(x, y, width, height))
                    {
                        continue;
                    }

                    int index = rowStart + x;
                    float currentVisibility = _visibilityMaskCurrentVisibility[index];
                    float targetVisibility = _visibilityMaskTargetVisibility[index];

                    if (fullPass)
                    {
                        // AS3 `Location.lighting():3140` is `if(!(!this.retDark && _loc5_ >= 1))` — a
                        // tile already at visibility 1, in a room that does not return to darkness, is
                        // skipped entirely. Such a tile is absorbing: SampleLightContribution clamps to
                        // [0,1] so the target can never exceed 1, and the fall branch is disabled, so
                        // neither the visibility nor its mask pixel can change again. This is what makes
                        // a repeat pass over an explored room ~free instead of a full re-raycast.
                        if (!returnsToDarkness && currentVisibility >= 1f)
                        {
                            continue;
                        }

                        // AS3 samples the tile's TOP-LEFT CORNER, not its centre
                        // (`Location.lighting():3152-3153`). The centre is half a tile off on both
                        // axes, which moves the occlusion ray's target and so changes the lit set —
                        // see FogOcclusionMath.ResolveLightSamplePixel for the measurement.
                        Vector2 sample = FogOcclusionMath.ResolveLightSamplePixel(x, y, TileSizePixels);
                        targetVisibility = ambientVisibility;

                        for (int i = 0; i < _lightSources.Count; i++)
                        {
                            float contribution = SampleLightContribution(_lightSources[i], sample.x, sample.y);
                            if (contribution > targetVisibility)
                            {
                                targetVisibility = contribution;
                            }
                        }

                        _visibilityMaskTargetVisibility[index] = targetVisibility;
                    }
                    else if (currentVisibility == targetVisibility)
                    {
                        // AS3 `lighting2()` only touches a tile when `visi != t_visi`.
                        continue;
                    }

                    // AS3 `Tile.updVisi()`: `visi += 0.1; if(visi > t_visi) visi = t_visi;`
                    if (targetVisibility > currentVisibility)
                    {
                        currentVisibility = Mathf.MoveTowards(currentVisibility, targetVisibility, LightRevealRiseSpeed);
                    }
                    else if (returnsToDarkness)
                    {
                        currentVisibility = Mathf.MoveTowards(currentVisibility, targetVisibility, LightRevealFallSpeed);
                    }
                    else
                    {
                        currentVisibility = Mathf.Max(currentVisibility, targetVisibility);
                    }

                    if (currentVisibility == _visibilityMaskCurrentVisibility[index])
                    {
                        continue;
                    }

                    _visibilityMaskCurrentVisibility[index] = currentVisibility;
                    _visibilityMaskPixels[index] = ResolveMaskPixel(currentVisibility);
                    anyPixelChanged = true;
                }
            }

            if (!anyPixelChanged)
            {
                return;
            }

            _visibilityMaskTexture.SetPixels(_visibilityMaskPixels);
            _visibilityMaskTexture.Apply(false, false);
            _visibilityMaskUploadPending = false;
        }

        private bool ShouldRefreshVisibilityMask(Vector3? playerWorldPosition, Vector2Int roomPixelSize)
        {
            bool hasPlayerPosition = TryGetRoomLocalPixelPosition(playerWorldPosition, out Vector2 playerLocalPixels);
            bool playerSampleValid = false;
            Vector2Int currentPlayerSample = new Vector2Int(int.MinValue, int.MinValue);

            if (hasPlayerPosition)
            {
                currentPlayerSample = QuantizeVisibilitySample(playerLocalPixels, roomPixelSize);
                playerSampleValid = true;
            }

            bool playerChanged = playerSampleValid != _lastPlayerSampleValid ||
                (playerSampleValid && currentPlayerSample != _lastPlayerSample);

            // AS3 `Location.as:3398` ORs two force flags into this decision: `isRelight` (set when a
            // door opens, Box.as:690) and `isRebuild` (set when a solid tile changes,
            // Location.as:2527/2586). The port folds both into the room's one-shot relight request —
            // see RoomInstance.RequestRelight — and this is the read-and-clear. It is what makes
            // opening a door reveal what is behind it without the player having to move first; before
            // it, the mask refreshed only on a player tile crossing, so a door opened while standing
            // still stayed dark. Consuming here is safe: the predicate is true whenever the request
            // was set, so the request is only ever spent on a pass that actually runs.
            bool relightRequested = _room != null && _room.ConsumeRelightRequest();

            if (!FogOcclusionMath.ShouldRunFullLightPass(_visibilityMaskDirty, playerChanged, relightRequested))
            {
                return false;
            }

            _lastPlayerSampleValid = playerSampleValid;
            _lastPlayerSample = currentPlayerSample;
            _visibilityMaskDirty = false;
            return true;
        }

        private Vector2Int QuantizeVisibilitySample(Vector2 localPixels, Vector2Int roomPixelSize)
        {
            return new Vector2Int(
                Mathf.Clamp(Mathf.FloorToInt(localPixels.x / TileSizePixels), 0, Mathf.Max(0, _visibilityMaskTextureSize.x - 1)),
                Mathf.Clamp(Mathf.FloorToInt(localPixels.y / TileSizePixels), 0, Mathf.Max(0, _visibilityMaskTextureSize.y - 1)));
        }

        private void CreateVisibilityMaskOverlay()
        {
            Vector2Int roomPixelSize = GetRoomPixelSize();
            if (roomPixelSize.x <= 0 || roomPixelSize.y <= 0)
            {
                return;
            }

            EnsureVisibilityMaskBuffers(roomPixelSize);

            _visibilityMaskTexture = new Texture2D(_visibilityMaskTextureSize.x, _visibilityMaskTextureSize.y, TextureFormat.RGBA32, false);
            _visibilityMaskTexture.filterMode = FilterMode.Bilinear;
            _visibilityMaskTexture.wrapMode = TextureWrapMode.Clamp;

            _visibilityMaskSprite = Sprite.Create(
                _visibilityMaskTexture,
                new Rect(0, 0, _visibilityMaskTextureSize.x, _visibilityMaskTextureSize.y),
                new Vector2(0.5f, 0.5f),
                100f / TileSizePixels);
            _visibilityMaskSprite.name = $"VisibilityMask_{_room?.id}";

            GameObject overlayObject = new GameObject("VisibilityMask");
            overlayObject.transform.SetParent(_visibilityMaskParent != null ? _visibilityMaskParent : _backgroundParent, false);
            overlayObject.transform.localPosition = WorldCoordinates.PixelToUnity(new Vector2(roomPixelSize.x * 0.5f, roomPixelSize.y * 0.5f));

            SpriteRenderer renderer = overlayObject.AddComponent<SpriteRenderer>();
            renderer.sprite = _visibilityMaskSprite;
            renderer.sortingLayerName = MapSortingLayers.Foreground;
            renderer.sortingOrder = VisibilityMaskSortingOrder;
            renderer.color = Color.white;
            if (_fogOfWarDisabled)
            {
                renderer.enabled = false;
            }

            _generatedAssets.Add(_visibilityMaskTexture);
            _generatedAssets.Add(_visibilityMaskSprite);
            _visibilityMaskRenderers.Add(renderer);
        }

        private void EnsureVisibilityMaskBuffers(Vector2Int roomPixelSize)
        {
            Vector2Int desiredSize = new Vector2Int(
                Mathf.Max(1, _room?.width ?? 0),
                Mathf.Max(1, _room?.height ?? 0));

            if (_visibilityMaskTextureSize == desiredSize &&
                _visibilityMaskCurrentVisibility.Length == desiredSize.x * desiredSize.y &&
                _visibilityMaskTargetVisibility.Length == desiredSize.x * desiredSize.y &&
                _visibilityMaskPixels.Length == desiredSize.x * desiredSize.y)
            {
                return;
            }

            _visibilityMaskTextureSize = desiredSize;
            _visibilityMaskCurrentVisibility = new float[desiredSize.x * desiredSize.y];
            _visibilityMaskTargetVisibility = new float[desiredSize.x * desiredSize.y];
            _visibilityMaskPixels = new Color[desiredSize.x * desiredSize.y];
            _relightFramesRemaining = 0;

            // The buffer must start **fully dark**, not left at the `Color[]` default.
            //
            // A new `Color[]` is `(0,0,0,0)` — alpha 0, i.e. fully *transparent*, i.e. fully *lit*.
            // But the correct pixel for `visibility == 0` is `(0,0,0,1)`: opaque darkness. The
            // change guard in UpdateVisibilityMask skips a texel whose visibility did not move, so
            // if the buffer starts transparent every texel the light never reaches is skipped
            // forever and renders lit — the whole room appears revealed while the areas the light
            // *does* reach get written at `1 - 0.1` alpha and look dark. That is the fog rendered
            // exactly inverted. Initialising through the same helper the update loop uses keeps the
            // "stored pixel == colour for stored visibility" invariant true from the start.
            Color opaqueDarkness = ResolveMaskPixel(0f);
            for (int i = 0; i < _visibilityMaskPixels.Length; i++)
            {
                _visibilityMaskPixels[i] = opaqueDarkness;
            }

            _visibilityMaskUploadPending = true;
        }

        /// <summary>
        /// Re-derives this room's non-moving light emitters — the AS3 <c>Location.lightAll()</c> set.
        ///
        /// AS3's only emitters are room objects carrying the <c>light</c> attribute: <c>Box.as:377-379</c>
        /// parses <c>xml.@light</c>, <c>Location.lightAll():3096-3107</c> iterates <c>this.objs</c>, and
        /// <c>Land.ativateLoc():1273</c> calls it once per room activation.
        ///
        /// Backdrop art is **not** an emitter. <c>BackObj.light</c> is a MovieClip
        /// (<c>BackObj.as:25</c>) — the <c>_l</c> "lit" graphic drawn into <c>colorBmp</c> at
        /// <c>Grafon.as:607</c> — and <c>&lt;back&gt;</c> nodes live in <c>Location.backobjs</c>
        /// (<c>Location.as:704</c>), which <c>lightAll()</c> never visits.
        ///
        /// The port used to *also* accept any id containing "light"/"lamp"/"torch", applied to objects
        /// and to background decorations. In <c>Base/room_2_0</c> that turned six
        /// <c>&lt;back id="light4|light5"&gt;</c> decorations into 18 of 21 light sources and made the
        /// per-refresh pass ~20 ms (43 FPS in play). **Do not reintroduce a name heuristic.**
        /// </summary>
        public void RebuildLightSources()
        {
            _staticLightSources.Clear();

            if (_room?.objects == null)
            {
                _staticLightSourceObjectCount = -1;
                return;
            }

            _staticLightSourceObjectCount = _room.objects.Count;
            ResolveLightRadii(out float innerRadiusPixels, out float outerRadiusPixels);

            for (int i = 0; i < _room.objects.Count; i++)
            {
                ObjectInstance obj = _room.objects[i];
                if (obj == null || !obj.isActive || !obj.HasEnabledLightFlag())
                {
                    continue;
                }

                Vector2 lightPosition = obj.position + new Vector2(0f, -TileSizePixels * 0.5f);
                AddLightTriplet(_staticLightSources, lightPosition, innerRadiusPixels, outerRadiusPixels, 0.9f);
            }
        }

        /// <summary>
        /// Assembles the list the visibility pass iterates: the baked static set, plus the player's
        /// light at its current position. AS3's per-frame path does the same thing — <c>lightAll()</c>
        /// baked the lamps, then <c>Location.step():3400</c> calls <c>lighting()</c> with no
        /// arguments, i.e. a <b>single</b> source at the camera.
        ///
        /// <para><b>The player gets ONE source, not a triplet.</b> AS3's +/-10 px triplet is the
        /// <c>lightAll():3103-3105</c> form, which only ever runs over <c>this.objs</c> — the room's
        /// lamps. The port gave the player a triplet too, which widened the reveal by 10 px each side
        /// and, with the ray aimed at the tile corner, still left <b>6 tiles</b> lit that the oracle
        /// leaves black in a 48 x 25 room. Reducing it to one source took that to <b>0</b>. See
        /// <see cref="FogOcclusionMath.ResolveLightSamplePixel"/> for the paired measurement.</para>
        /// </summary>
        private void BuildLightSources(Vector3? playerWorldPosition)
        {
            if (_room?.objects != null && _room.objects.Count != _staticLightSourceObjectCount)
            {
                RebuildLightSources();
            }

            _lightSources.Clear();
            _lightSources.AddRange(_staticLightSources);

            ResolveLightRadii(out float innerRadiusPixels, out float outerRadiusPixels);

            if (TryGetRoomLocalPixelPosition(playerWorldPosition, out Vector2 playerLocalPixels))
            {
                // ONE source. `AddLightTriplet` here was the port's invention — AS3 spreads a triplet
                // over `this.objs` (lamps) only; the player's per-frame `lighting()` takes the
                // no-argument path and places a single light. See this method's remarks.
                AddLightSource(_lightSources, playerLocalPixels, innerRadiusPixels, outerRadiusPixels, 1f);
            }
        }

        private void ResolveLightRadii(out float innerRadiusPixels, out float outerRadiusPixels)
        {
            float visibilityMultiplier = _room?.environment != null
                ? Mathf.Max(0.1f, _room.environment.visibilityMultiplier)
                : 1f;

            innerRadiusPixels = DefaultInnerLightRadiusPixels * visibilityMultiplier;
            outerRadiusPixels = DefaultOuterLightRadiusPixels * visibilityMultiplier;
        }

        private void AddLightTriplet(List<LightSource> target, Vector2 centerPixels, float innerRadiusPixels, float outerRadiusPixels, float intensity)
        {
            AddLightSource(target, centerPixels, innerRadiusPixels, outerRadiusPixels, intensity);
            AddLightSource(target, centerPixels + new Vector2(-LightSourceSpreadPixels, 0f), innerRadiusPixels, outerRadiusPixels, intensity * 0.92f);
            AddLightSource(target, centerPixels + new Vector2(LightSourceSpreadPixels, 0f), innerRadiusPixels, outerRadiusPixels, intensity * 0.92f);
        }

        private void AddLightSource(List<LightSource> target, Vector2 positionPixels, float innerRadiusPixels, float outerRadiusPixels, float intensity)
        {
            Vector2Int roomPixelSize = GetRoomPixelSize();
            if (positionPixels.x < 0f || positionPixels.y < 0f || positionPixels.x > roomPixelSize.x || positionPixels.y > roomPixelSize.y)
            {
                return;
            }

            target.Add(new LightSource(positionPixels, innerRadiusPixels, outerRadiusPixels, intensity));
        }

        private float ResolveVisibilityMaskDarknessAlpha()
        {
            return 1f;
        }

        /// <summary>
        /// The mask colour for a texel at <paramref name="visibility"/> — black, with alpha falling
        /// from <see cref="ResolveVisibilityMaskDarknessAlpha"/> at visibility 0 (fully dark) to 0 at
        /// visibility 1 (fully revealed).
        ///
        /// Both the update loop and <see cref="EnsureVisibilityMaskBuffers"/> go through this, so the
        /// invariant "a stored pixel equals the colour for its stored visibility" holds from
        /// allocation. Keep it that way: the change guard in <see cref="UpdateVisibilityMask"/> skips
        /// texels whose visibility did not move, which is only safe if a skipped texel is already
        /// correct.
        /// </summary>
        private Color ResolveMaskPixel(float visibility)
        {
            return new Color(0f, 0f, 0f, ResolveVisibilityMaskDarknessAlpha() * (1f - visibility));
        }

        private float ResolveAmbientVisibility()
        {
            return 0f;
        }

        private bool UsesVisibilityMask()
        {
            return Application.isPlaying &&
                _room?.environment != null &&
                !_room.environment.noBlackReveal;
        }

        private float SampleLightContribution(LightSource lightSource, float sampleX, float sampleY)
        {
            Vector2 offset = lightSource.PositionPixels - new Vector2(sampleX, sampleY);
            float outerRadius = lightSource.OuterRadiusPixels;
            float squaredDistance = offset.x * offset.x + offset.y * offset.y;

            // Squared compare first — the sqrt is only needed for the radial falloff, and most samples in
            // a room sit inside no light's outer radius at all.
            if (squaredDistance >= outerRadius * outerRadius)
            {
                return 0f;
            }

            float distance = Mathf.Sqrt(squaredDistance);
            float radialContribution = distance <= lightSource.InnerRadiusPixels
                ? 1f
                : (outerRadius - distance) / Mathf.Max(1f, outerRadius - lightSource.InnerRadiusPixels);
            if (radialContribution <= 0f)
            {
                return 0f;
            }

            float transmission = SampleLightTransmission(lightSource.PositionPixels, new Vector2(sampleX, sampleY));
            return Mathf.Clamp01(radialContribution * transmission * lightSource.Intensity);
        }

        private float SampleLightTransmission(Vector2 sourcePixels, Vector2 targetPixels)
        {
            if (_room?.tiles == null)
            {
                return 1f;
            }

            // The rule itself is `FogOcclusionMath.ResolveRayTransmission`, kept pure and pinned by
            // FogOcclusionMathTests. It replaced an inline walk that returned "no occlusion" for any
            // target within 40 px — which is not AS3's rule, and lit the solid tile directly below a
            // light. See OcclusionRaySampleCount for the oracle and the regression.
            return FogOcclusionMath.ResolveRayTransmission(
                sourcePixels,
                targetPixels,
                LightOcclusionStepPixels,
                _tileOpacityAt);
        }

        /// <summary>
        /// Tile opacity at a room-local pixel — the ray's lookup, see <see cref="_tileOpacityAt"/>.
        ///
        /// <para>Goes through <see cref="FogOcclusionMath.ResolveRayLookupCoord"/> rather than
        /// <c>RoomInstance.GetTileAt</c>: the latter is a plain <c>floor</c>, which in this port's
        /// Y-up space names the row <b>above</b> an exact tile boundary, where AS3's Y-down
        /// <c>getAbsTile</c> names the row itself. See that method for why the <c>- 1</c> is not
        /// re-derived here.</para>
        /// </summary>
        private float ResolveLightBlockingOpacityAt(Vector2 roomLocalPixels)
        {
            if (_room == null)
            {
                return 0f;
            }

            Vector2Int coord = FogOcclusionMath.ResolveRayLookupCoord(
                roomLocalPixels.x, roomLocalPixels.y, _room.height, TileSizePixels);
            return ResolveLightBlockingOpacity(_room.GetTileAtCoord(coord));
        }

        /// <summary>
        /// AS3 <c>Tile.opac</c> for one tile — see <see cref="FogOcclusionMath"/>, which owns the rule
        /// and is pinned by <c>FogOcclusionMathTests</c>. This wrapper only supplies the room's
        /// <c>wopac</c> option and the tile's door term.
        /// </summary>
        private float ResolveLightBlockingOpacity(TileData tile)
        {
            if (tile == null)
            {
                return 0f;
            }

            float waterOpacity = _room?.environment != null ? _room.environment.waterOpacity : 0f;
            return FogOcclusionMath.ResolveTileOcclusionOpacity(
                tile.physicsType,
                tile.doorOcclusion,
                tile.heightLevel,
                tile.hasWater,
                waterOpacity);
        }

        private bool TryGetRoomLocalPixelPosition(Vector3? worldPosition, out Vector2 localPixels)
        {
            localPixels = Vector2.zero;
            if (!worldPosition.HasValue || _room == null)
            {
                return false;
            }

            Vector2 roomOriginPixels = GetRoomOriginPixels();
            Vector2 worldPixels = WorldCoordinates.UnityToPixel(worldPosition.Value);
            localPixels = worldPixels - roomOriginPixels;

            Vector2Int roomPixelSize = GetRoomPixelSize();
            return localPixels.x >= 0f &&
                localPixels.y >= 0f &&
                localPixels.x <= roomPixelSize.x &&
                localPixels.y <= roomPixelSize.y;
        }

        private Vector2 GetRoomOriginPixels()
        {
            int borderOffset = Mathf.Max(0, _room?.borderOffset ?? 0);
            return new Vector2(
                _room.landPosition.x * WorldConstants.ROOM_WIDTH * TileSizePixels - borderOffset * TileSizePixels,
                _room.landPosition.y * WorldConstants.ROOM_HEIGHT * TileSizePixels - borderOffset * TileSizePixels);
        }

        private Vector2Int GetRoomPixelSize()
        {
            if (_room == null)
            {
                return Vector2Int.zero;
            }

            return new Vector2Int(
                Mathf.Max(0, _room.width * TileSizePixels),
                Mathf.Max(0, _room.height * TileSizePixels));
        }

        // NOTE: `ShouldTreatAsLightSource(id)` (an id-substring test for "light"/"lamp"/"torch") and the
        // unused `HasEnabledLightFlag(string)` / `IsTruthyFlag` parameter parsers were deleted with the
        // fog fix. They had no AS3 counterpart: AS3 reads the `light` attribute on room objects only
        // (Box.as:377-379). The name heuristic is what turned six `light4`/`light5` backdrop decorations
        // into 18 phantom light sources in Base/room_2_0. Do not reintroduce either.
        // See docs/Perf_FogOfWar_RoomLag_Investigation.md.

        private void CreateRoomBackdrop(Vector2 contentOriginPixels, Vector2Int contentPixelSize, BackdropCompositeData compositeData)
        {
            string backgroundWall = _room.environment.backgroundWall;
            if (string.IsNullOrWhiteSpace(backgroundWall) ||
                string.Equals(backgroundWall, "sky", System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Texture2D sourceTexture;
            using (Profiler.Region("backdrop.resolveTexture", "boot: ResolveBackdropTexture — lookup only, expected trivial"))
            {
                sourceTexture = ResolveBackdropTexture(backgroundWall);
            }

            if (sourceTexture == null)
            {
                WarnMissingBackgroundId(backgroundWall, "room backdrop texture");
                return;
            }

            List<RectInt> fillRects;
            using (Profiler.Region("backdrop.fillRects", "boot: BuildBackdropFillRects — geometry only, expected trivial"))
            {
                fillRects = BuildBackdropFillRects(contentPixelSize, _room.environment.backgroundForm);
            }

            if (fillRects.Count == 0)
            {
                return;
            }
            Color tint = ResolveBackdropColor();

            // The pixel work. Was 1197 ms per call because CreateBackdropSprite called GetPixel
            // once per pixel (1.92M interop calls for a 48x25 room) and re-read Texture2D.width/
            // .height up to four times per pixel. Both are now hoisted/cached, so this region
            // should collapse — if it does not, the cost is somewhere else and we look again.
            using (Profiler.Region("backdrop.sprites", "boot: CreateBackdropSprite per fill rect — the per-pixel loop"))
            {
                for (int i = 0; i < fillRects.Count; i++)
                {
                    RectInt fillRect = fillRects[i];
                    if (fillRect.width <= 0 || fillRect.height <= 0)
                    {
                        continue;
                    }

                    Sprite sprite = CreateBackdropSprite(sourceTexture, fillRect, compositeData);
                    if (sprite == null)
                    {
                        continue;
                    }

                    GameObject backdropObject = new GameObject($"Backdrop_{backgroundWall}_{i}");
                    backdropObject.transform.SetParent(_backgroundParent, false);

                    SpriteRenderer renderer = backdropObject.AddComponent<SpriteRenderer>();
                    renderer.sprite = sprite;
                    renderer.sortingLayerName = MapSortingLayers.Backwall;
                    renderer.sortingOrder = BackdropSortingOrder;
                    renderer.color = tint;
                    renderer.transform.localPosition = GetBackdropSegmentPosition(contentOriginPixels, contentPixelSize, fillRect, sprite);

                    _backdropRenderers.Add(renderer);
                }
            }
        }

        private void CreateBackgroundDecorations(Vector2 contentOriginPixels, Vector2Int contentPixelSize, BackdropCompositeData compositeData)
        {
            if (_backgroundLookup == null)
            {
                return;
            }

            HashSet<string> spawnedKeys = new HashSet<string>();

            if (_room.backgroundDecorations != null)
            {
                for (int i = 0; i < _room.backgroundDecorations.Count; i++)
                {
                    var decoration = _room.backgroundDecorations[i];
                    if (decoration == null || string.IsNullOrWhiteSpace(decoration.decorationId))
                    {
                        continue;
                    }

                    string key = $"{decoration.decorationId}_{decoration.tileCoord.x}_{decoration.tileCoord.y}";
                    if (spawnedKeys.Add(key))
                    {
                        CreateBackgroundSprite(decoration.decorationId, decoration.tileCoord, _backgroundRenderers, contentOriginPixels, contentPixelSize, compositeData);
                    }
                }
            }

        }

        private void CreateBackgroundTileShadowOverlay(Vector2 contentOriginPixels, Vector2Int contentPixelSize, BackdropCompositeData compositeData)
        {
            if (_room?.backgroundRoom == null ||
                compositeData.ShadowAlpha == null ||
                compositeData.ShadowAlpha.Length == 0 ||
                compositeData.ShadowSize.x <= 0 ||
                compositeData.ShadowSize.y <= 0)
            {
                return;
            }

            Texture2D shadowTexture = new Texture2D(compositeData.ShadowSize.x, compositeData.ShadowSize.y, TextureFormat.RGBA32, false);
            shadowTexture.filterMode = FilterMode.Bilinear;
            shadowTexture.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[compositeData.ShadowAlpha.Length];
            for (int y = 0; y < compositeData.ShadowSize.y; y++)
            {
                int srcRowStart = y * compositeData.ShadowSize.x;
                int dstRowStart = (compositeData.ShadowSize.y - 1 - y) * compositeData.ShadowSize.x;
                for (int x = 0; x < compositeData.ShadowSize.x; x++)
                {
                    float alpha = compositeData.ShadowAlpha[srcRowStart + x];
                    pixels[dstRowStart + x] = new Color(0f, 0f, 0f, alpha);
                }
            }

            shadowTexture.SetPixels(pixels);
            shadowTexture.Apply(false, false);

            Sprite shadowSprite = Sprite.Create(
                shadowTexture,
                new Rect(0, 0, compositeData.ShadowSize.x, compositeData.ShadowSize.y),
                new Vector2(0.5f, 0.5f),
                100f);
            shadowSprite.name = $"BackgroundTileShadow_{_room?.id}";

            GameObject overlayObject = new GameObject("BackgroundTileShadow");
            overlayObject.transform.SetParent(_backgroundParent, false);
            overlayObject.transform.localPosition = WorldCoordinates.PixelToUnity(new Vector2(
                contentOriginPixels.x + contentPixelSize.x * 0.5f,
                contentOriginPixels.y + contentPixelSize.y * 0.5f));
            overlayObject.transform.localScale = new Vector3(
                contentPixelSize.x / (float)compositeData.ShadowSize.x,
                contentPixelSize.y / (float)compositeData.ShadowSize.y,
                1f);

            SpriteRenderer renderer = overlayObject.AddComponent<SpriteRenderer>();
            renderer.sprite = shadowSprite;
            renderer.sortingLayerName = MapSortingLayers.BackgroundTiles;
            renderer.sortingOrder = BackgroundTileShadowSortingOrder;
            renderer.color = Color.white;

            _generatedAssets.Add(shadowTexture);
            _generatedAssets.Add(shadowSprite);
            _backgroundTileShadowRenderers.Add(renderer);
        }

        private void CreateWaterOverlays()
        {
            if (_backgroundLookup == null)
            {
                return;
            }

            // Check if any tile actually has water.
            // Don't rely solely on environment.HasWater() — tiles may have hasWater=true
            // from ApplyWaterLevel even when waterType wasn't explicitly set on the template.
            if (!_room.environment.HasWater() && !RoomHasAnyWaterTile())
            {
                return;
            }

            IReadOnlyList<Sprite> waterFrames = _backgroundLookup.GetFrames("tileVoda");
            if (waterFrames == null || waterFrames.Count == 0)
            {
                WarnMissingBackgroundId("tileVoda", "water overlay");
                return;
            }

            // AS3: tileVoda is a MovieClip with N frames, one per water type.
            // tipWater (waterType) selects the frame: gotoAndStop(tipWater + 1).
            // waterType 0 = default water (blue), 1 = toxic/green, 2 = dark, 3 = pink/lava, etc.
            int waterType = _room.environment.waterType;
            int frameIndex = Mathf.Clamp(waterType > 0 ? waterType - 1 : 0, 0, waterFrames.Count - 1);
            Sprite waterSprite = waterFrames[frameIndex];
            if (waterSprite == null)
            {
                return;
            }

            // Get the source tile texture (need readable copy)
            Texture2D sourceTex = GetReadableTexture(waterSprite.texture);
            if (sourceTex == null)
            {
                return;
            }
            Rect sourceRect = waterSprite.textureRect;
            int srcX = Mathf.RoundToInt(sourceRect.x);
            int srcY = Mathf.RoundToInt(sourceRect.y);
            int tileW = Mathf.RoundToInt(sourceRect.width);
            int tileH = Mathf.RoundToInt(sourceRect.height);
            Color32[] tilePixels = sourceTex.GetPixels32();
            // Extract just the tile region into a reusable buffer
            Color32[] tileBuf = new Color32[tileW * tileH];
            for (int row = 0; row < tileH; row++)
            {
                System.Array.Copy(tilePixels, (srcY + row) * sourceTex.width + srcX, tileBuf, row * tileW, tileW);
            }

            // Bake all water tiles into a single room-sized texture
            int texWidth = _room.width * tileW;
            int texHeight = _room.height * tileH;
            Color32[] combinedPixels = new Color32[texWidth * texHeight]; // defaults to transparent black

            for (int x = 0; x < _room.width; x++)
            {
                for (int y = 0; y < _room.height; y++)
                {
                    TileData tile = _room.tiles[x, y];
                    if (tile == null || !tile.hasWater)
                    {
                        continue;
                    }

                    int destX = x * tileW;
                    int destY = y * tileH;
                    for (int row = 0; row < tileH; row++)
                    {
                        System.Array.Copy(tileBuf, row * tileW, combinedPixels, (destY + row) * texWidth + destX, tileW);
                    }
                }
            }

            var combinedTex = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point
            };
            combinedTex.SetPixels32(combinedPixels);

            combinedTex.Apply();
            _generatedAssets.Add(combinedTex);

            // Create a single sprite covering the entire room
            const float pixelsPerUnit = 100f;
            Sprite combinedSprite = Sprite.Create(
                combinedTex,
                new Rect(0, 0, texWidth, texHeight),
                Vector2.zero,
                pixelsPerUnit);
            _generatedAssets.Add(combinedSprite);

            // Position at room origin (tile 0,0 = bottom-left)
            GameObject waterObject = new GameObject("WaterOverlay");
            waterObject.transform.SetParent(_backgroundParent, false);
            waterObject.transform.localPosition = WorldCoordinates.TileToUnity(Vector2Int.zero);

            SpriteRenderer renderer = waterObject.AddComponent<SpriteRenderer>();
            renderer.sprite = combinedSprite;
            renderer.sortingLayerName = MapSortingLayers.Water;
            renderer.sortingOrder = 0;

            float opacity = _room.environment.waterOpacity > 0f ? _room.environment.waterOpacity : 0.45f;
            Color tint = ResolveEnvironmentTint(_room.environment.colorScheme, opacityOverride: opacity);
            renderer.color = tint;

            _waterRenderers.Add(renderer);
        }

        private void CreateBackgroundSprite(
            string id,
            Vector2Int tileCoord,
            List<SpriteRenderer> targetList,
            Vector2 contentOriginPixels,
            Vector2Int contentPixelSize,
            BackdropCompositeData compositeData)
        {
            IReadOnlyList<Sprite> frames = _backgroundLookup.GetFrames(id);
            if (frames == null || frames.Count == 0 || frames[0] == null)
            {
                WarnMissingBackgroundId(id, "background decoration");
                return;
            }

            Sprite sprite = frames[0];
            Vector2Int renderTileCoord = ConvertAs3TileCoord(tileCoord, sprite);

            GameObject backgroundObject = new GameObject($"Background_{id}_{tileCoord.x}_{tileCoord.y}");
            backgroundObject.transform.SetParent(_backgroundParent, false);
            ApplyEntryScale(backgroundObject.transform, id);

            bool flipX = _backgroundLookup.GetFlipX(id);
            bool flipY = _backgroundLookup.GetFlipY(id);
            Vector2 pixelOffset = _backgroundLookup.GetPixelOffset(id);
            Vector2 anchorPixelPosition = WorldCoordinates.TileToPixel(renderTileCoord) + pixelOffset;
            Sprite compositeSprite = CreateDecorationCompositeSprite(
                sprite,
                id,
                anchorPixelPosition,
                flipX,
                flipY,
                contentOriginPixels,
                contentPixelSize,
                compositeData);

            SpriteRenderer renderer = backgroundObject.AddComponent<SpriteRenderer>();
            renderer.sprite = compositeSprite != null ? compositeSprite : sprite;
            renderer.sortingLayerName = MapSortingLayers.BackgroundDecor;
            renderer.sortingOrder = DecorationSortingBase - renderTileCoord.y;
            renderer.color = ResolveDecorationColor(id);
            renderer.transform.localPosition = GetAnchoredPosition(
                renderTileCoord,
                renderer.sprite,
                pixelOffset);

            targetList.Add(renderer);
        }

        private Texture2D ResolveBackdropTexture(string backgroundWall)
        {
            Texture2D texture = _tileTextureLookup != null
                ? _tileTextureLookup.GetTexture(backgroundWall)
                : null;

            if (texture != null)
            {
                return texture;
            }

            return _tileTextureLookup != null ? _tileTextureLookup.GetTexture("tBackWall") : null;
        }

        private Color ResolveBackdropColor()
        {
            Color baseTint = ResolveBackgroundEnvironmentTint(_room?.environment);
            return MultiplyColors(baseTint, ResolveTintColor(_globalBackdropTint));
        }

        private Color ResolveDecorationColor(string id)
        {
            Color baseTint = ResolveBackgroundEnvironmentTint(_room?.environment);
            RoomBackdropSettingsLookup.TintSettings tintSettings = _globalDecorationTint;
            if (_settingsLookup != null &&
                _settingsLookup.TryGetDecoration(_roomKey, id, out RoomBackdropSettingsLookup.DecorationSettings settings) &&
                settings.overrideGlobalTint)
            {
                tintSettings = settings.tint;
            }

            return MultiplyColors(baseTint, ResolveTintColor(tintSettings));
        }

        private Sprite CreateDecorationCompositeSprite(
            Sprite sourceSprite,
            string id,
            Vector2 anchorPixelPosition,
            bool flipX,
            bool flipY,
            Vector2 contentOriginPixels,
            Vector2Int contentPixelSize,
            BackdropCompositeData compositeData)
        {
            if (sourceSprite == null)
            {
                return null;
            }

            Texture2D readableTexture = GetReadableTexture(sourceSprite.texture);
            if (readableTexture == null)
            {
                return null;
            }

            Rect textureRect = sourceSprite.textureRect;
            int width = Mathf.RoundToInt(textureRect.width);
            int height = Mathf.RoundToInt(textureRect.height);
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false);
            result.filterMode = sourceSprite.texture.filterMode;
            result.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[width * height];
            float pixelsPerUnit = Mathf.Max(0.01f, sourceSprite.pixelsPerUnit);
            float roomPixelsPerSpritePixel = 100f / pixelsPerUnit;
            int textureX = Mathf.RoundToInt(textureRect.x);
            int textureY = Mathf.RoundToInt(textureRect.y);

            // Same fix as CreateBackdropSprite: index a cached Color[] instead of one GetPixel
            // interop call per pixel. GetPixels() returns y-major with y=0 at the bottom, which is
            // exactly the order GetPixel(x, y) addresses, so the mapping is (y * texWidth + x).
            int texWidth = readableTexture.width;
            Color[] sourcePixels = GetReadablePixels(readableTexture);

            for (int y = 0; y < height; y++)
            {
                int sourceRow = (textureY + y) * texWidth + textureX;
                for (int x = 0; x < width; x++)
                {
                    Color sampledColor = sourcePixels != null
                        ? sourcePixels[sourceRow + x]
                        : readableTexture.GetPixel(textureX + x, textureY + y);
                    float renderedPixelX = flipX ? (width - 1 - x) : x;
                    float renderedPixelY = flipY ? (height - 1 - y) : y;
                    float localPixelX = anchorPixelPosition.x + (renderedPixelX + 0.5f) * roomPixelsPerSpritePixel;
                    float localPixelY = anchorPixelPosition.y + (renderedPixelY + 0.5f) * roomPixelsPerSpritePixel;
                    float topDownX = localPixelX - contentOriginPixels.x;
                    float topDownY = contentPixelSize.y - (localPixelY - contentOriginPixels.y);
                    sampledColor = ApplyBackdropComposite(sampledColor, topDownX, topDownY, compositeData);
                    pixels[y * width + x] = sampledColor;
                }
            }

            result.SetPixels(pixels);
            result.Apply();

            Sprite sprite = Sprite.Create(
                result,
                new Rect(0, 0, width, height),
                new Vector2(sourceSprite.pivot.x / width, sourceSprite.pivot.y / height),
                sourceSprite.pixelsPerUnit,
                0,
                SpriteMeshType.FullRect,
                sourceSprite.border);

            sprite.name = $"BackgroundComposite_{id}_{width}_{height}";
            _generatedAssets.Add(result);
            _generatedAssets.Add(sprite);
            return sprite;
        }

        public static Color ResolveBackgroundLayerTint(string colorScheme, int darkness)
        {
            Color color = ResolveEnvironmentTint(colorScheme);
            float lightingMultiplier = ResolveBackgroundLightingMultiplier(darkness);
            color.r *= lightingMultiplier;
            color.g *= lightingMultiplier;
            color.b *= lightingMultiplier;
            return color;
        }

        public static Color ResolveBackgroundLayerTint(RoomEnvironment environment)
        {
            if (environment == null)
            {
                return Color.white;
            }

            string backgroundColorScheme = !string.IsNullOrWhiteSpace(environment.backgroundColorScheme)
                ? environment.backgroundColorScheme
                : environment.colorScheme;

            return ResolveBackgroundLayerTint(backgroundColorScheme, environment.darkness);
        }

        public static Color ResolveBackgroundTileTint(RoomEnvironment environment)
        {
            if (environment == null)
            {
                return Color.white;
            }

            string backgroundColorScheme = !string.IsNullOrWhiteSpace(environment.backgroundColorScheme)
                ? environment.backgroundColorScheme
                : environment.colorScheme;

            Color color = ResolveEnvironmentTint(backgroundColorScheme);
            float darknessMultiplier = ResolveTileDarknessMultiplier(environment.darkness);
            color.r *= darknessMultiplier;
            color.g *= darknessMultiplier;
            color.b *= darknessMultiplier;
            return color;
        }

        private static Color ResolveBackgroundEnvironmentTint(RoomEnvironment environment)
        {
            if (environment == null)
            {
                return Color.white;
            }

            string backgroundColorScheme = !string.IsNullOrWhiteSpace(environment.backgroundColorScheme)
                ? environment.backgroundColorScheme
                : environment.colorScheme;

            return ResolveEnvironmentTint(backgroundColorScheme);
        }

        private static float ResolveBackgroundLightingMultiplier(int darkness)
        {
            float darknessAlpha = Mathf.Clamp01((BackdropBaseDarkness + darkness) / 255f);
            return Mathf.Lerp(1f, 0.7f, darknessAlpha);
        }

        private static float ResolveTileDarknessMultiplier(int darkness)
        {
            float darknessAlpha = Mathf.Clamp01((BackdropBaseDarkness + darkness) / 255f);
            return 1f - darknessAlpha;
        }

        private static Color ResolveEnvironmentTint(string colorScheme, float? opacityOverride = null)
        {
            Color color = Color.white;

            switch ((colorScheme ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "green":
                    color = new Color(0.8f, 1.16f, 0.8f, 1f);
                    break;
                case "red":
                    color = new Color(1.1f, 0.9f, 0.7f, 1f);
                    break;
                case "fire":
                    color = new Color(1.1f, 0.7f, 0.5f, 1f);
                    break;
                case "lab":
                    color = new Color(0.9f, 1.1f, 0.7f, 1f);
                    break;
                case "black":
                    color = new Color(0.5f, 0.6f, 0.7f, 1f);
                    break;
                case "blue":
                    color = new Color(0.8f, 0.8f, 1.16f, 1f);
                    break;
                case "sky":
                    color = new Color(0.85f, 1.12f, 1.12f, 1f);
                    break;
                case "yellow":
                    color = new Color(1.25f, 1.2f, 0.9f, 1f);
                    break;
                case "purple":
                    color = new Color(1.08f, 0.8f, 1.12f, 1f);
                    break;
                case "pink":
                    color = new Color(1.1f, 0.9f, 1f, 1f);
                    break;
                case "blood":
                    color = new Color(1.08f, 0.6f, 0.6f, 1f);
                    break;
                case "blood2":
                    color = new Color(1f, 0.1f, 0.1f, 1f);
                    break;
                case "dark":
                    color = new Color(0f, 0f, 0f, 1f);
                    break;
                case "mf":
                    color = new Color(0.5f, 0.5f, 1.08f, 1f);
                    break;
            }

            if (opacityOverride.HasValue)
            {
                color.a = Mathf.Clamp01(opacityOverride.Value);
            }

            return color;
        }

        private static Color ResolveTintColor(RoomBackdropSettingsLookup.TintSettings tintSettings)
        {
            Color tint = tintSettings.tint;
            float brightness = Mathf.Max(0f, tintSettings.brightness);
            tint.r *= brightness;
            tint.g *= brightness;
            tint.b *= brightness;
            tint.a = 1f;
            return tint;
        }

        private static Color MultiplyColors(Color baseColor, Color modifier)
        {
            return new Color(
                baseColor.r * modifier.r,
                baseColor.g * modifier.g,
                baseColor.b * modifier.b,
                baseColor.a * modifier.a);
        }

        private void WarnMissingBackgroundId(string id, string context)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            string key = $"{context}:{id}";
            if (_missingIds.Add(key))
            {
                Debug.LogWarning($"[RoomBackdropRenderer] Missing {context} entry '{id}' for room '{_room?.id}'.");
            }
        }

        private void ApplyEntryScale(Transform target, string id)
        {
            if (target == null)
            {
                return;
            }

            float scaleX = _backgroundLookup.GetFlipX(id) ? -BackgroundScale.x : BackgroundScale.x;
            float scaleY = _backgroundLookup.GetFlipY(id) ? -BackgroundScale.y : BackgroundScale.y;
            target.localScale = new Vector3(scaleX, scaleY, BackgroundScale.z);
        }

        private static List<RectInt> BuildBackdropFillRects(Vector2Int contentPixelSize, int backgroundForm)
        {
            List<RectInt> rects = new List<RectInt>();
            if (contentPixelSize.x <= 0 || contentPixelSize.y <= 0)
            {
                return rects;
            }

            switch (backgroundForm)
            {
                case 1:
                    rects.Add(ClampRectToBounds(new RectInt(0, 0, 11 * TileSizePixels - 10, contentPixelSize.y), contentPixelSize));
                    rects.Add(ClampRectToBounds(new RectInt(37 * TileSizePixels + 10, 0, contentPixelSize.x - (37 * TileSizePixels + 10), contentPixelSize.y), contentPixelSize));
                    break;
                case 2:
                    rects.Add(ClampRectToBounds(new RectInt(0, 16 * TileSizePixels + 10, contentPixelSize.x, contentPixelSize.y - (16 * TileSizePixels + 10)), contentPixelSize));
                    break;
                case 3:
                    rects.Add(ClampRectToBounds(new RectInt(0, 24 * TileSizePixels + 10, contentPixelSize.x, contentPixelSize.y - (24 * TileSizePixels + 10)), contentPixelSize));
                    break;
                default:
                    rects.Add(new RectInt(0, 0, contentPixelSize.x, contentPixelSize.y));
                    break;
            }

            rects.RemoveAll(rect => rect.width <= 0 || rect.height <= 0);
            return rects;
        }

        private static RectInt ClampRectToBounds(RectInt rect, Vector2Int bounds)
        {
            int xMin = Mathf.Clamp(rect.xMin, 0, bounds.x);
            int yMin = Mathf.Clamp(rect.yMin, 0, bounds.y);
            int xMax = Mathf.Clamp(rect.xMax, 0, bounds.x);
            int yMax = Mathf.Clamp(rect.yMax, 0, bounds.y);
            return new RectInt(xMin, yMin, Mathf.Max(0, xMax - xMin), Mathf.Max(0, yMax - yMin));
        }

        private Sprite CreateBackdropSprite(Texture2D sourceTexture, RectInt fillRect, BackdropCompositeData compositeData)
        {
            if (sourceTexture == null || fillRect.width <= 0 || fillRect.height <= 0)
            {
                return null;
            }

            Texture2D readableTexture = GetReadableTexture(sourceTexture);
            if (readableTexture == null)
            {
                return null;
            }

            // Hoist every native accessor out of the pixel loop. readableTexture.width/.height are
            // interop calls, and the loop below used to read them up to four times per pixel.
            // GetPixels() is one call for the whole texture instead of one GetPixel per pixel.
            int texWidth = readableTexture.width;
            int texHeight = readableTexture.height;
            Color[] sourcePixels = GetReadablePixels(readableTexture);

            Texture2D result = new Texture2D(fillRect.width, fillRect.height, TextureFormat.RGBA32, false);
            result.filterMode = FilterMode.Point;
            result.wrapMode = TextureWrapMode.Clamp;

            float sourceOffsetX = _backdropTextureOffset.x * texWidth;
            float sourceOffsetY = _backdropTextureOffset.y * texHeight;
            bool flipX = _flipBackdropTextureX;
            bool flipY = _flipBackdropTextureY;
            Color[] pixels = new Color[fillRect.width * fillRect.height];
            using (Profiler.Region("backdrop.sprites.sample", "boot: per-pixel sample + composite. Was 1.92M GetPixel interop calls before the pixel cache"))
            {
                for (int y = 0; y < fillRect.height; y++)
                {
                    int destRow = (fillRect.height - 1 - y) * fillRect.width;
                    int srcRowBase = fillRect.y + y;
                    for (int x = 0; x < fillRect.width; x++)
                    {
                        // Keep the division exactly as it was. (a / s) and (a * (1/s)) are not
                        // bit-identical, and a 1-ULP difference flips FloorToInt for boundary values —
                        // that would silently shift the sampled texel by one pixel.
                        int scaledX = Mathf.FloorToInt((fillRect.x + x) / _backdropTextureScale.x + sourceOffsetX);
                        int scaledY = Mathf.FloorToInt(srcRowBase / _backdropTextureScale.y + sourceOffsetY);
                        if (flipX)
                        {
                            scaledX = texWidth - 1 - scaledX;
                        }

                        if (flipY)
                        {
                            scaledY = texHeight - 1 - scaledY;
                        }

                        int sourceX = PositiveModulo(scaledX, texWidth);
                        int sourceY = PositiveModulo(scaledY, texHeight);
                        Color sampledColor = sourcePixels != null
                            ? sourcePixels[sourceY * texWidth + sourceX]
                            : readableTexture.GetPixel(sourceX, sourceY);
                        sampledColor = ApplyBackdropComposite(sampledColor, fillRect.x + x, srcRowBase, compositeData);
                        pixels[destRow + x] = sampledColor;
                    }
                }
            }

            if (_backdropSharpenStrength > 0.001f)
            {
                // Suspected to be the bulk of what is left here: a 5-tap convolution over the whole
                // image using Color operator chains, which are method calls returning a 16-byte
                // struct — seven of them per pixel over 1.92M pixels. Now scalar float maths.
                using (Profiler.Region("backdrop.sprites.sharpen", "boot: ApplySharpen — 5-tap convolution over every pixel"))
                {
                    pixels = ApplySharpen(pixels, fillRect.width, fillRect.height, _backdropSharpenStrength);
                }
            }

            Sprite sprite;
            using (Profiler.Region("backdrop.sprites.upload", "boot: SetPixels + Apply + Sprite.Create for one backdrop segment"))
            {
                result.SetPixels(pixels);
                result.Apply();

                sprite = Sprite.Create(
                    result,
                    new Rect(0, 0, fillRect.width, fillRect.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
            }

            sprite.name = $"Backdrop_{_room?.environment.backgroundWall}_{fillRect.x}_{fillRect.y}_{fillRect.width}_{fillRect.height}";
            _generatedAssets.Add(result);
            _generatedAssets.Add(sprite);
            return sprite;
        }

        private Texture2D GetReadableTexture(Texture2D source)
        {
            if (source == null)
            {
                return null;
            }

            if (_readableTextureCache.TryGetValue(source, out Texture2D cached))
            {
                return cached;
            }

            Texture2D readable = CreateReadableCopy(source);
            _readableTextureCache[source] = readable;
            _generatedAssets.Add(readable);
            return readable;
        }

        private static Texture2D CreateReadableCopy(Texture2D source)
        {
            if (source == null)
            {
                return null;
            }

            RenderTexture previous = RenderTexture.active;
            // Preserve the project's normal texture color handling when sampling authored art.
            RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);

            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;

            Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.filterMode = source.filterMode;
            readable.wrapMode = source.wrapMode;
            readable.ReadPixels(new Rect(0, 0, temporary.width, temporary.height), 0, 0);
            readable.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
            return readable;
        }

        private static int PositiveModulo(int value, int modulo)
        {
            if (modulo <= 0)
            {
                return 0;
            }

            int remainder = value % modulo;
            return remainder < 0 ? remainder + modulo : remainder;
        }

        private static Color[] ApplySharpen(Color[] source, int width, int height, float strength)
        {
            if (source == null || source.Length != width * height || width <= 2 || height <= 2)
            {
                return source;
            }

            Color[] sharpened = new Color[source.Length];
            Array.Copy(source, sharpened, source.Length);

            float clampedStrength = Mathf.Clamp(strength, 0f, 2f);
            for (int y = 1; y < height - 1; y++)
            {
                int row = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    int index = row + x;
                    Color center = source[index];
                    Color left = source[index - 1];
                    Color right = source[index + 1];
                    Color up = source[index - width];
                    Color down = source[index + width];

                    // Scalar float maths instead of Color operator chains.
                    //
                    // UnityEngine.Color's +, - and * are static METHODS returning a 16-byte struct,
                    // not intrinsics. The original expression below dispatched seven of them plus
                    // three Mathf.Clamp01 calls per pixel — over 1.92M pixels that is the bulk of
                    // this loop. The arithmetic and its evaluation order are unchanged, so the
                    // result is bit-identical; only the operator dispatch is gone.
                    //
                    // Note the alpha term is deliberately NOT computed: the original calculated it
                    // through the operator chain and then overwrote it with center.a anyway.
                    float avgR = (left.r + right.r + up.r + down.r) * 0.25f;
                    float avgG = (left.g + right.g + up.g + down.g) * 0.25f;
                    float avgB = (left.b + right.b + up.b + down.b) * 0.25f;

                    float r = center.r + (center.r - avgR) * clampedStrength;
                    float g = center.g + (center.g - avgG) * clampedStrength;
                    float b = center.b + (center.b - avgB) * clampedStrength;

                    sharpened[index] = new Color(
                        r < 0f ? 0f : (r > 1f ? 1f : r),
                        g < 0f ? 0f : (g > 1f ? 1f : g),
                        b < 0f ? 0f : (b > 1f ? 1f : b),
                        center.a);
                }
            }

            return sharpened;
        }

        private BackdropCompositeData BuildBackdropCompositeData(Vector2 contentOriginPixels, Vector2Int contentPixelSize)
        {
            float darknessAlpha = ResolveBackdropDarknessAlpha();
            if (_disableBackdropShadowBake)
            {
                return new BackdropCompositeData(darknessAlpha, Array.Empty<float>(), Vector2Int.zero);
            }

            float[] shadowAlpha = BuildBackdropShadowAlpha(contentOriginPixels, contentPixelSize, out Vector2Int shadowSize);
            return new BackdropCompositeData(darknessAlpha, shadowAlpha, shadowSize);
        }

        private float ResolveBackdropDarknessAlpha()
        {
            int darkness = Mathf.Clamp(BackdropBaseDarkness + (_room?.environment?.darkness ?? 0), 0, 255);
            return darkness / 255f;
        }

        private float[] BuildBackdropShadowAlpha(Vector2 contentOriginPixels, Vector2Int contentPixelSize, out Vector2Int shadowSize)
        {
            shadowSize = new Vector2Int(
                Mathf.Max(1, Mathf.CeilToInt(contentPixelSize.x * ShadowResolutionScale)),
                Mathf.Max(1, Mathf.CeilToInt(contentPixelSize.y * ShadowResolutionScale)));

            if (_room == null || _room.tiles == null || contentPixelSize.x <= 0 || contentPixelSize.y <= 0)
            {
                return Array.Empty<float>();
            }

            float[] silhouette = new float[shadowSize.x * shadowSize.y];
            Rect contentRect = new Rect(contentOriginPixels.x, contentOriginPixels.y, contentPixelSize.x, contentPixelSize.y);
            for (int x = 0; x < _room.width; x++)
            {
                for (int y = 0; y < _room.height; y++)
                {
                    TileData tile = _room.tiles[x, y];
                    if (!DoesTileCastBackdropShadow(tile))
                    {
                        continue;
                    }

                    Rect clippedBounds = IntersectRects(tile.GetBounds(), contentRect);
                    if (clippedBounds.width <= 0f || clippedBounds.height <= 0f)
                    {
                        continue;
                    }

                    RasterizeShadowRect(silhouette, shadowSize, clippedBounds, contentRect);
                }
            }

            int shadowOffset = Mathf.Max(1, Mathf.RoundToInt(ShadowDistancePixels * ShadowResolutionScale));
            float[] shifted = OffsetShadowAlpha(silhouette, shadowSize, shadowOffset);
            int blurRadius = Mathf.Max(1, Mathf.RoundToInt(ShadowBlurPixels * ShadowResolutionScale * 0.5f));
            float[] blurred = ApplyBoxBlur(shifted, shadowSize, blurRadius, ShadowBlurIterations);
            for (int i = 0; i < blurred.Length; i++)
            {
                blurred[i] = Mathf.Clamp01(blurred[i] * ShadowOpacity);
            }

            return blurred;
        }

        private static bool DoesTileCastBackdropShadow(TileData tile)
        {
            if (tile == null || tile.opacity <= 0f)
            {
                return false;
            }

            return tile.physicsType != TilePhysicsType.Air
                || tile.heightLevel > 0
                || tile.slopeType != 0
                || tile.stairType != 0
                || !string.IsNullOrWhiteSpace(tile.GetFrontGraphic());
        }

        private static Rect IntersectRects(Rect a, Rect b)
        {
            float xMin = Mathf.Max(a.xMin, b.xMin);
            float xMax = Mathf.Min(a.xMax, b.xMax);
            float yMin = Mathf.Max(a.yMin, b.yMin);
            float yMax = Mathf.Min(a.yMax, b.yMax);

            if (xMax <= xMin || yMax <= yMin)
            {
                return Rect.zero;
            }

            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static void RasterizeShadowRect(float[] target, Vector2Int shadowSize, Rect sourceRect, Rect contentRect)
        {
            float scaledXMin = (sourceRect.xMin - contentRect.xMin) * ShadowResolutionScale;
            float scaledXMax = (sourceRect.xMax - contentRect.xMin) * ShadowResolutionScale;
            float scaledYMinBottom = (sourceRect.yMin - contentRect.yMin) * ShadowResolutionScale;
            float scaledYMaxBottom = (sourceRect.yMax - contentRect.yMin) * ShadowResolutionScale;

            int xMin = Mathf.Clamp(Mathf.FloorToInt(scaledXMin), 0, shadowSize.x);
            int xMax = Mathf.Clamp(Mathf.CeilToInt(scaledXMax), 0, shadowSize.x);
            int yMin = Mathf.Clamp(Mathf.FloorToInt(shadowSize.y - scaledYMaxBottom), 0, shadowSize.y);
            int yMax = Mathf.Clamp(Mathf.CeilToInt(shadowSize.y - scaledYMinBottom), 0, shadowSize.y);

            for (int y = yMin; y < yMax; y++)
            {
                int rowStart = y * shadowSize.x;
                for (int x = xMin; x < xMax; x++)
                {
                    target[rowStart + x] = 1f;
                }
            }
        }

        private static float[] OffsetShadowAlpha(float[] source, Vector2Int size, int yOffset)
        {
            if (source == null || source.Length == 0)
            {
                return Array.Empty<float>();
            }

            float[] shifted = new float[source.Length];
            for (int y = 0; y < size.y; y++)
            {
                int targetY = y + yOffset;
                if (targetY < 0 || targetY >= size.y)
                {
                    continue;
                }

                Buffer.BlockCopy(source, (y * size.x) * sizeof(float), shifted, (targetY * size.x) * sizeof(float), size.x * sizeof(float));
            }

            return shifted;
        }

        private static float[] ApplyBoxBlur(float[] source, Vector2Int size, int radius, int iterations)
        {
            if (source == null || source.Length == 0 || radius <= 0 || iterations <= 0)
            {
                return source ?? Array.Empty<float>();
            }

            float[] current = source;
            for (int i = 0; i < iterations; i++)
            {
                float[] horizontal = new float[current.Length];
                float[] vertical = new float[current.Length];
                BlurHorizontal(current, horizontal, size.x, size.y, radius);
                BlurVertical(horizontal, vertical, size.x, size.y, radius);
                current = vertical;
            }

            return current;
        }

        private static void BlurHorizontal(float[] source, float[] target, int width, int height, int radius)
        {
            float[] prefix = new float[width + 1];
            for (int y = 0; y < height; y++)
            {
                prefix[0] = 0f;
                int rowStart = y * width;
                for (int x = 0; x < width; x++)
                {
                    prefix[x + 1] = prefix[x] + source[rowStart + x];
                }

                for (int x = 0; x < width; x++)
                {
                    int minX = Mathf.Max(0, x - radius);
                    int maxX = Mathf.Min(width - 1, x + radius);
                    float sum = prefix[maxX + 1] - prefix[minX];
                    target[rowStart + x] = sum / (maxX - minX + 1);
                }
            }
        }

        private static void BlurVertical(float[] source, float[] target, int width, int height, int radius)
        {
            float[] prefix = new float[height + 1];
            for (int x = 0; x < width; x++)
            {
                prefix[0] = 0f;
                for (int y = 0; y < height; y++)
                {
                    prefix[y + 1] = prefix[y] + source[y * width + x];
                }

                for (int y = 0; y < height; y++)
                {
                    int minY = Mathf.Max(0, y - radius);
                    int maxY = Mathf.Min(height - 1, y + radius);
                    float sum = prefix[maxY + 1] - prefix[minY];
                    target[y * width + x] = sum / (maxY - minY + 1);
                }
            }
        }

        private static Color ApplyBackdropComposite(Color sampledColor, float topDownX, float topDownY, BackdropCompositeData compositeData)
        {
            float shadowAlpha = SampleShadowAlpha(compositeData, topDownX, topDownY);
            float darkenMultiplier = (1f - compositeData.DarknessAlpha) * (1f - shadowAlpha);

            sampledColor.r *= darkenMultiplier;
            sampledColor.g *= darkenMultiplier;
            sampledColor.b *= darkenMultiplier;
            return sampledColor;
        }

        private static float SampleShadowAlpha(BackdropCompositeData compositeData, float topDownX, float topDownY)
        {
            if (compositeData.ShadowAlpha == null || compositeData.ShadowAlpha.Length == 0 || compositeData.ShadowSize.x <= 0 || compositeData.ShadowSize.y <= 0)
            {
                return 0f;
            }

            if (topDownX < 0f || topDownY < 0f || topDownX >= compositeData.ShadowSize.x / ShadowResolutionScale || topDownY >= compositeData.ShadowSize.y / ShadowResolutionScale)
            {
                return 0f;
            }

            float scaledX = Mathf.Clamp((topDownX + 0.5f) * ShadowResolutionScale - 0.5f, 0f, compositeData.ShadowSize.x - 1f);
            float scaledY = Mathf.Clamp((topDownY + 0.5f) * ShadowResolutionScale - 0.5f, 0f, compositeData.ShadowSize.y - 1f);

            int x0 = Mathf.FloorToInt(scaledX);
            int y0 = Mathf.FloorToInt(scaledY);
            int x1 = Mathf.Min(x0 + 1, compositeData.ShadowSize.x - 1);
            int y1 = Mathf.Min(y0 + 1, compositeData.ShadowSize.y - 1);

            float tx = scaledX - x0;
            float ty = scaledY - y0;

            float a = compositeData.ShadowAlpha[y0 * compositeData.ShadowSize.x + x0];
            float b = compositeData.ShadowAlpha[y0 * compositeData.ShadowSize.x + x1];
            float c = compositeData.ShadowAlpha[y1 * compositeData.ShadowSize.x + x0];
            float d = compositeData.ShadowAlpha[y1 * compositeData.ShadowSize.x + x1];

            float top = Mathf.Lerp(a, b, tx);
            float bottom = Mathf.Lerp(c, d, tx);
            return Mathf.Lerp(top, bottom, ty);
        }

        private Vector2Int ConvertAs3TileCoord(Vector2Int tileCoord, Sprite sprite)
        {
            if (_room == null || _room.height <= 0)
            {
                return tileCoord;
            }

            int borderOffset = Mathf.Max(0, _room.borderOffset);
            int contentHeight = Mathf.Max(0, _room.height - borderOffset * 2);
            int decorationHeight = GetLogicalDecorationHeightTiles(sprite);

            return new Vector2Int(
                tileCoord.x + borderOffset,
                borderOffset + contentHeight - tileCoord.y - decorationHeight);
        }

        private static int GetLogicalDecorationHeightTiles(Sprite sprite)
        {
            if (sprite == null)
            {
                return 1;
            }

            float pixelHeight = sprite.rect.height;
            if (pixelHeight <= 0f)
            {
                return 1;
            }

            return Mathf.Max(1, Mathf.CeilToInt(pixelHeight / WorldConstants.TILE_SIZE));
        }

        private Vector2Int GetContentOriginTileCoord()
        {
            if (_room == null)
            {
                return Vector2Int.zero;
            }

            int borderOffset = Mathf.Max(0, _room.borderOffset);
            return new Vector2Int(borderOffset, borderOffset);
        }

        private Vector2 GetContentOriginPixels()
        {
            Vector2Int contentOriginTile = GetContentOriginTileCoord();
            return WorldCoordinates.TileToPixel(contentOriginTile);
        }

        private Vector2Int GetContentPixelSize()
        {
            if (_room == null)
            {
                return Vector2Int.zero;
            }

            int borderOffset = Mathf.Max(0, _room.borderOffset);
            int width = Mathf.Max(0, _room.width - borderOffset * 2);
            int height = Mathf.Max(0, _room.height - borderOffset * 2);
            return new Vector2Int(
                width * TileSizePixels,
                height * TileSizePixels);
        }

        private static Vector3 GetAnchoredPosition(Vector2Int tileCoord, Sprite sprite, Vector2 pixelOffset)
        {
            Vector3 origin = WorldCoordinates.TileToUnity(tileCoord);
            Vector3 extents = sprite.bounds.extents;
            Vector3 offset = WorldCoordinates.PixelToUnity(pixelOffset);
            return new Vector3(origin.x + extents.x + offset.x, origin.y + extents.y + offset.y, 0f);
        }

        private static Vector3 GetBackdropSegmentPosition(Vector2 contentOriginPixels, Vector2Int contentPixelSize, RectInt fillRect, Sprite sprite)
        {
            Vector2 segmentBottomLeft = new Vector2(
                contentOriginPixels.x + fillRect.x,
                contentOriginPixels.y + (contentPixelSize.y - fillRect.y - fillRect.height));

            Vector3 origin = WorldCoordinates.PixelToUnity(segmentBottomLeft);
            Vector3 extents = sprite.bounds.extents;
            return new Vector3(origin.x + extents.x, origin.y + extents.y, 0f);
        }

        private bool RoomHasAnyWaterTile()
        {
            if (_room == null || _room.tiles == null) return false;
            for (int x = 0; x < _room.width; x++)
            {
                for (int y = 0; y < _room.height; y++)
                {
                    if (_room.tiles[x, y] != null && _room.tiles[x, y].hasWater)
                        return true;
                }
            }
            return false;
        }

        private static void DestroyRendererList(List<SpriteRenderer> renderers)
        {
            for (int i = 0; i < renderers.Count; i++)
            {
                if (renderers[i] != null)
                {
                    if (Application.isPlaying)
                    {
                        UnityEngine.Object.Destroy(renderers[i].gameObject);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(renderers[i].gameObject);
                    }
                }
            }

            renderers.Clear();
        }

        private void DestroyGeneratedAssets()
        {
            for (int i = _generatedAssets.Count - 1; i >= 0; i--)
            {
                UnityEngine.Object asset = _generatedAssets[i];
                if (asset == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(asset);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(asset);
                }
            }

            _generatedAssets.Clear();
            _readableTextureCache.Clear();
            // Must be cleared alongside the texture cache: it is keyed on the readable textures
            // that were just destroyed, so keeping it would pin their pixel arrays in memory and
            // hand out stale entries if a source texture were ever recreated.
            _readablePixelCache.Clear();
        }
    }
}
