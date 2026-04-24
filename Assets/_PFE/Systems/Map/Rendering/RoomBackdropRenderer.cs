using System;
using System.Collections.Generic;
using UnityEngine;

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
        private const float LightSourceSpreadPixels = 10f;
        private const float LightOcclusionStepPixels = 20f;
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
        private readonly List<LightSource> _lightSources = new List<LightSource>();

        private Texture2D _visibilityMaskTexture;
        private Sprite _visibilityMaskSprite;
        private Vector2Int _visibilityMaskTextureSize = Vector2Int.zero;
        private float[] _visibilityMaskCurrentVisibility = Array.Empty<float>();
        private Color[] _visibilityMaskPixels = Array.Empty<Color>();
        private bool _visibilityMaskDirty = true;
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
        }

        public void CreateVisuals()
        {
            DestroyVisuals();

            if (_room == null || _backgroundParent == null)
            {
                return;
            }

            Vector2Int contentPixelSize = GetContentPixelSize();
            Vector2 contentOriginPixels = GetContentOriginPixels();
            BackdropCompositeData compositeData = BuildBackdropCompositeData(contentOriginPixels, contentPixelSize);

            CreateRoomBackdrop(contentOriginPixels, contentPixelSize, compositeData);
            CreateBackgroundTileShadowOverlay(contentOriginPixels, contentPixelSize, compositeData);
            CreateBackgroundDecorations(contentOriginPixels, contentPixelSize, compositeData);
            CreateWaterOverlays();
            if (UsesVisibilityMask())
            {
                CreateVisibilityMaskOverlay();
                UpdateVisibilityMask();
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
            _visibilityMaskPixels = Array.Empty<Color>();
            _lightSources.Clear();
            _visibilityMaskDirty = true;
            _lastPlayerSampleValid = false;
            _lastPlayerSample = new Vector2Int(int.MinValue, int.MinValue);
        }

        public void UpdateVisibilityMask(Vector3? playerWorldPosition = null)
        {
            if (_room == null || !UsesVisibilityMask() || _visibilityMaskTexture == null || _visibilityMaskRenderers.Count == 0)
            {
                return;
            }

            Vector2Int roomPixelSize = GetRoomPixelSize();
            if (roomPixelSize.x <= 0 || roomPixelSize.y <= 0)
            {
                return;
            }

            EnsureVisibilityMaskBuffers(roomPixelSize);
            if (!ShouldRefreshVisibilityMask(playerWorldPosition, roomPixelSize))
            {
                return;
            }

            BuildLightSources(playerWorldPosition);

            float darknessAlpha = ResolveVisibilityMaskDarknessAlpha();
            bool returnsToDarkness = _room.environment != null && _room.environment.returnsToDarkness;
            float ambientVisibility = ResolveAmbientVisibility();
            int width = _visibilityMaskTextureSize.x;
            int height = _visibilityMaskTextureSize.y;

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * width;
                for (int x = 0; x < width; x++)
                {
                    float sampleX = (x + 0.5f) * TileSizePixels;
                    float sampleY = (y + 0.5f) * TileSizePixels;
                    float targetVisibility = ambientVisibility;

                    for (int i = 0; i < _lightSources.Count; i++)
                    {
                        float contribution = SampleLightContribution(_lightSources[i], sampleX, sampleY);
                        if (contribution > targetVisibility)
                        {
                            targetVisibility = contribution;
                        }
                    }

                    int index = rowStart + x;
                    float currentVisibility = _visibilityMaskCurrentVisibility[index];
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

                    _visibilityMaskCurrentVisibility[index] = currentVisibility;
                    _visibilityMaskPixels[index] = new Color(0f, 0f, 0f, darknessAlpha * (1f - currentVisibility));
                }
            }

            _visibilityMaskTexture.SetPixels(_visibilityMaskPixels);
            _visibilityMaskTexture.Apply(false, false);
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
            if (!_visibilityMaskDirty && !playerChanged)
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
                _visibilityMaskPixels.Length == desiredSize.x * desiredSize.y)
            {
                return;
            }

            _visibilityMaskTextureSize = desiredSize;
            _visibilityMaskCurrentVisibility = new float[desiredSize.x * desiredSize.y];
            _visibilityMaskPixels = new Color[desiredSize.x * desiredSize.y];
        }

        private void BuildLightSources(Vector3? playerWorldPosition)
        {
            _lightSources.Clear();

            ResolveLightRadii(out float innerRadiusPixels, out float outerRadiusPixels);

            if (TryGetRoomLocalPixelPosition(playerWorldPosition, out Vector2 playerLocalPixels))
            {
                AddLightTriplet(playerLocalPixels, innerRadiusPixels, outerRadiusPixels, 1f);
            }

            if (_room?.backgroundDecorations != null)
            {
                for (int i = 0; i < _room.backgroundDecorations.Count; i++)
                {
                    BackgroundDecorationInstance decoration = _room.backgroundDecorations[i];
                    if (decoration == null || !ShouldTreatAsLightSource(decoration.decorationId))
                    {
                        continue;
                    }

                    if (TryGetDecorationLocalPixelPosition(decoration, out Vector2 lightPosition))
                    {
                        AddLightTriplet(lightPosition, innerRadiusPixels, outerRadiusPixels, 0.9f);
                    }
                }
            }

            if (_room?.objects != null)
            {
                for (int i = 0; i < _room.objects.Count; i++)
                {
                    ObjectInstance obj = _room.objects[i];
                    if (obj == null || !obj.isActive)
                    {
                        continue;
                    }

                    bool emitsLight = obj.HasEnabledLightFlag() ||
                        ShouldTreatAsLightSource(obj.objectId) ||
                        ShouldTreatAsLightSource(obj.objectType);
                    if (!emitsLight)
                    {
                        continue;
                    }

                    Vector2 lightPosition = obj.position + new Vector2(0f, -TileSizePixels * 0.5f);
                    AddLightTriplet(lightPosition, innerRadiusPixels, outerRadiusPixels, 0.9f);
                }
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

        private void AddLightTriplet(Vector2 centerPixels, float innerRadiusPixels, float outerRadiusPixels, float intensity)
        {
            AddLightSource(centerPixels, innerRadiusPixels, outerRadiusPixels, intensity);
            AddLightSource(centerPixels + new Vector2(-LightSourceSpreadPixels, 0f), innerRadiusPixels, outerRadiusPixels, intensity * 0.92f);
            AddLightSource(centerPixels + new Vector2(LightSourceSpreadPixels, 0f), innerRadiusPixels, outerRadiusPixels, intensity * 0.92f);
        }

        private void AddLightSource(Vector2 positionPixels, float innerRadiusPixels, float outerRadiusPixels, float intensity)
        {
            Vector2Int roomPixelSize = GetRoomPixelSize();
            if (positionPixels.x < 0f || positionPixels.y < 0f || positionPixels.x > roomPixelSize.x || positionPixels.y > roomPixelSize.y)
            {
                return;
            }

            _lightSources.Add(new LightSource(positionPixels, innerRadiusPixels, outerRadiusPixels, intensity));
        }

        private float ResolveVisibilityMaskDarknessAlpha()
        {
            return 1f;
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
            Vector2 samplePosition = new Vector2(sampleX, sampleY);
            float distance = Vector2.Distance(lightSource.PositionPixels, samplePosition);
            if (distance >= lightSource.OuterRadiusPixels)
            {
                return 0f;
            }

            float radialContribution = distance <= lightSource.InnerRadiusPixels
                ? 1f
                : (lightSource.OuterRadiusPixels - distance) / Mathf.Max(1f, lightSource.OuterRadiusPixels - lightSource.InnerRadiusPixels);
            if (radialContribution <= 0f)
            {
                return 0f;
            }

            float transmission = SampleLightTransmission(lightSource.PositionPixels, samplePosition);
            return Mathf.Clamp01(radialContribution * transmission * lightSource.Intensity);
        }

        private float SampleLightTransmission(Vector2 sourcePixels, Vector2 targetPixels)
        {
            if (_room?.tiles == null)
            {
                return 1f;
            }

            Vector2 delta = targetPixels - sourcePixels;
            float distance = delta.magnitude;
            if (distance <= LightOcclusionStepPixels)
            {
                return 1f;
            }

            int steps = Mathf.Max(1, Mathf.CeilToInt(distance / LightOcclusionStepPixels));
            float transmission = 1f;
            for (int step = 1; step < steps; step++)
            {
                Vector2 sample = sourcePixels + delta * (step / (float)steps);
                TileData tile = _room.GetTileAt(sample);
                float opacity = ResolveLightBlockingOpacity(tile);
                if (opacity <= 0f)
                {
                    continue;
                }

                transmission -= opacity;
                if (transmission <= 0f)
                {
                    return 0f;
                }
            }

            return Mathf.Clamp01(transmission);
        }

        private float ResolveLightBlockingOpacity(TileData tile)
        {
            if (tile == null)
            {
                return 0f;
            }

            float opacity = 0f;
            bool hasSolidVisual = tile.physicsType != TilePhysicsType.Air
                || tile.heightLevel > 0
                || tile.slopeType != 0
                || tile.stairType != 0
                || !string.IsNullOrWhiteSpace(tile.GetFrontGraphic());

            if (hasSolidVisual)
            {
                opacity = Mathf.Max(opacity, 0.6f);
            }

            if (tile.hasWater && _room?.environment != null && _room.environment.waterOpacity > 0f)
            {
                opacity = Mathf.Max(opacity, Mathf.Clamp01(_room.environment.waterOpacity * 0.5f));
            }

            return opacity;
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

        private bool TryGetDecorationLocalPixelPosition(BackgroundDecorationInstance decoration, out Vector2 localPixels)
        {
            localPixels = Vector2.zero;
            if (decoration == null || _backgroundLookup == null)
            {
                return false;
            }

            IReadOnlyList<Sprite> frames = _backgroundLookup.GetFrames(decoration.decorationId);
            if (frames == null || frames.Count == 0 || frames[0] == null)
            {
                return false;
            }

            Sprite sprite = frames[0];
            Vector2Int renderTileCoord = ConvertAs3TileCoord(decoration.tileCoord);
            Vector2 pixelOffset = _backgroundLookup.GetPixelOffset(decoration.decorationId);
            Vector2 spriteExtentsPixels = WorldCoordinates.UnityToPixel(sprite.bounds.extents);
            localPixels = WorldCoordinates.TileToPixel(renderTileCoord) + pixelOffset + spriteExtentsPixels;
            return true;
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

        private static bool ShouldTreatAsLightSource(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            string normalized = id.Trim().ToLowerInvariant();
            return normalized.Contains("light") ||
                normalized.Contains("lamp") ||
                normalized.Contains("torch");
        }

        private static bool HasEnabledLightFlag(string parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters))
            {
                return false;
            }

            int keyIndex = parameters.IndexOf("light=", StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
            {
                return false;
            }

            int valueStart = keyIndex + "light=".Length;
            while (valueStart < parameters.Length && char.IsWhiteSpace(parameters[valueStart]))
            {
                valueStart++;
            }

            if (valueStart >= parameters.Length)
            {
                return false;
            }

            char quote = parameters[valueStart];
            if (quote == '"' || quote == '\'')
            {
                valueStart++;
                int valueEnd = parameters.IndexOf(quote, valueStart);
                if (valueEnd > valueStart)
                {
                    return IsTruthyFlag(parameters.Substring(valueStart, valueEnd - valueStart));
                }
            }
            else
            {
                int valueEnd = valueStart;
                while (valueEnd < parameters.Length && !char.IsWhiteSpace(parameters[valueEnd]))
                {
                    valueEnd++;
                }

                return IsTruthyFlag(parameters.Substring(valueStart, valueEnd - valueStart));
            }

            return false;
        }

        private static bool IsTruthyFlag(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    return true;
                default:
                    return false;
            }
        }

        private void CreateRoomBackdrop(Vector2 contentOriginPixels, Vector2Int contentPixelSize, BackdropCompositeData compositeData)
        {
            string backgroundWall = _room.environment.backgroundWall;
            if (string.IsNullOrWhiteSpace(backgroundWall) ||
                string.Equals(backgroundWall, "sky", System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Texture2D sourceTexture = ResolveBackdropTexture(backgroundWall);
            if (sourceTexture == null)
            {
                WarnMissingBackgroundId(backgroundWall, "room backdrop texture");
                return;
            }

            List<RectInt> fillRects = BuildBackdropFillRects(contentPixelSize, _room.environment.backgroundForm);
            if (fillRects.Count == 0)
            {
                return;
            }
            Color tint = ResolveBackdropColor();

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
            Vector2Int renderTileCoord = ConvertAs3TileCoord(tileCoord);

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

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color sampledColor = readableTexture.GetPixel(textureX + x, textureY + y);
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

            Texture2D result = new Texture2D(fillRect.width, fillRect.height, TextureFormat.RGBA32, false);
            result.filterMode = FilterMode.Point;
            result.wrapMode = TextureWrapMode.Clamp;

            float sourceOffsetX = _backdropTextureOffset.x * readableTexture.width;
            float sourceOffsetY = _backdropTextureOffset.y * readableTexture.height;
            Color[] pixels = new Color[fillRect.width * fillRect.height];
            for (int y = 0; y < fillRect.height; y++)
            {
                for (int x = 0; x < fillRect.width; x++)
                {
                    int scaledX = Mathf.FloorToInt((fillRect.x + x) / _backdropTextureScale.x + sourceOffsetX);
                    int scaledY = Mathf.FloorToInt((fillRect.y + y) / _backdropTextureScale.y + sourceOffsetY);
                    if (_flipBackdropTextureX)
                    {
                        scaledX = readableTexture.width - 1 - scaledX;
                    }

                    if (_flipBackdropTextureY)
                    {
                        scaledY = readableTexture.height - 1 - scaledY;
                    }

                    int sourceX = PositiveModulo(scaledX, readableTexture.width);
                    int sourceY = PositiveModulo(scaledY, readableTexture.height);
                    Color sampledColor = readableTexture.GetPixel(sourceX, sourceY);
                    sampledColor = ApplyBackdropComposite(sampledColor, fillRect.x + x, fillRect.y + y, compositeData);
                    pixels[(fillRect.height - 1 - y) * fillRect.width + x] = sampledColor;
                }
            }

            if (_backdropSharpenStrength > 0.001f)
            {
                pixels = ApplySharpen(pixels, fillRect.width, fillRect.height, _backdropSharpenStrength);
            }

            result.SetPixels(pixels);
            result.Apply();

            Sprite sprite = Sprite.Create(
                result,
                new Rect(0, 0, fillRect.width, fillRect.height),
                new Vector2(0.5f, 0.5f),
                100f);

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
                for (int x = 1; x < width - 1; x++)
                {
                    int index = y * width + x;
                    Color center = source[index];
                    Color left = source[index - 1];
                    Color right = source[index + 1];
                    Color up = source[index - width];
                    Color down = source[index + width];

                    Color neighborAverage = (left + right + up + down) * 0.25f;
                    Color boosted = center + (center - neighborAverage) * clampedStrength;
                    boosted.r = Mathf.Clamp01(boosted.r);
                    boosted.g = Mathf.Clamp01(boosted.g);
                    boosted.b = Mathf.Clamp01(boosted.b);
                    boosted.a = center.a;
                    sharpened[index] = boosted;
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

        private Vector2Int ConvertAs3TileCoord(Vector2Int tileCoord)
        {
            if (_room == null || _room.height <= 0)
            {
                return tileCoord;
            }

            int borderOffset = Mathf.Max(0, _room.borderOffset);
            return new Vector2Int(
                tileCoord.x + borderOffset,
                (_room.height - 2 - borderOffset) - tileCoord.y);
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
        }
    }
}
