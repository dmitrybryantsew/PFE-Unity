using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace PFE.Core
{
    /// <summary>
    /// Small helper for async scene loading.
    /// Creates a temporary fullscreen fade overlay when requested.
    /// </summary>
    public static class SceneLoader
    {
        private static bool _isLoading;

        public static bool IsLoading => _isLoading;

        public static UniTask LoadSceneAsync(
            string sceneName,
            LoadSceneMode loadMode = LoadSceneMode.Single,
            bool useFade = false,
            float fadeDuration = 0.2f,
            Color? fadeColor = null)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                throw new ArgumentException("Scene name cannot be null or empty.", nameof(sceneName));
            }

            return LoadSceneInternalAsync(
                () => SceneManager.LoadSceneAsync(sceneName, loadMode),
                sceneName,
                useFade,
                fadeDuration,
                fadeColor ?? Color.black);
        }

        public static UniTask LoadSceneAsync(
            int buildIndex,
            LoadSceneMode loadMode = LoadSceneMode.Single,
            bool useFade = false,
            float fadeDuration = 0.2f,
            Color? fadeColor = null)
        {
            return LoadSceneInternalAsync(
                () => SceneManager.LoadSceneAsync(buildIndex, loadMode),
                $"build index {buildIndex}",
                useFade,
                fadeDuration,
                fadeColor ?? Color.black);
        }

        private static async UniTask LoadSceneInternalAsync(
            Func<AsyncOperation> loadOperationFactory,
            string sceneLabel,
            bool useFade,
            float fadeDuration,
            Color fadeColor)
        {
            if (_isLoading)
            {
                Debug.LogWarning($"[SceneLoader] Ignoring load request for {sceneLabel} because another load is already running.");
                return;
            }

            _isLoading = true;
            SceneLoaderFadeOverlay fadeOverlay = null;

            try
            {
                if (useFade)
                {
                    fadeOverlay = SceneLoaderFadeOverlay.Create(fadeColor);
                    await fadeOverlay.FadeToAsync(1f, fadeDuration);
                }

                AsyncOperation operation = loadOperationFactory();
                if (operation == null)
                {
                    throw new InvalidOperationException($"[SceneLoader] Failed to start async load for {sceneLabel}.");
                }

                while (!operation.isDone)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update);
                }

                if (fadeOverlay != null)
                {
                    await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
                    await fadeOverlay.FadeToAsync(0f, fadeDuration);
                    fadeOverlay.DestroySelf();
                    fadeOverlay = null;
                }
            }
            finally
            {
                if (fadeOverlay != null)
                {
                    fadeOverlay.DestroySelf();
                }

                _isLoading = false;
            }
        }
    }

    internal sealed class SceneLoaderFadeOverlay : MonoBehaviour
    {
        private const string RootObjectName = "__SceneLoaderFadeOverlay";

        private CanvasGroup _canvasGroup;
        private Image _image;

        public static SceneLoaderFadeOverlay Create(Color fadeColor)
        {
            var existing = UnityEngine.Object.FindObjectOfType<SceneLoaderFadeOverlay>();
            if (existing != null)
            {
                existing.SetupIfNeeded(fadeColor);
                existing._canvasGroup.alpha = 0f;
                return existing;
            }

            var root = new GameObject(RootObjectName);
            UnityEngine.Object.DontDestroyOnLoad(root);

            var overlay = root.AddComponent<SceneLoaderFadeOverlay>();
            overlay.SetupIfNeeded(fadeColor);
            overlay._canvasGroup.alpha = 0f;
            return overlay;
        }

        public async UniTask FadeToAsync(float targetAlpha, float duration)
        {
            SetupIfNeeded(_image != null ? _image.color : Color.black);

            float startAlpha = _canvasGroup.alpha;
            if (Mathf.Approximately(duration, 0f))
            {
                _canvasGroup.alpha = targetAlpha;
                return;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += UnityEngine.Time.unscaledDeltaTime;
                _canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, elapsed / duration);
                await UniTask.Yield(PlayerLoopTiming.Update);
            }

            _canvasGroup.alpha = targetAlpha;
        }

        public void DestroySelf()
        {
            if (this != null)
            {
                Destroy(gameObject);
            }
        }

        private void SetupIfNeeded(Color fadeColor)
        {
            var canvas = gameObject.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = gameObject.AddComponent<Canvas>();
            }

            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;

            if (gameObject.GetComponent<GraphicRaycaster>() == null)
            {
                gameObject.AddComponent<GraphicRaycaster>();
            }

            _canvasGroup ??= gameObject.GetComponent<CanvasGroup>();
            if (_canvasGroup == null)
            {
                _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }

            _canvasGroup.blocksRaycasts = true;
            _canvasGroup.interactable = false;

            if (_image == null)
            {
                var imageRoot = new GameObject("Fade", typeof(RectTransform), typeof(Image));
                imageRoot.transform.SetParent(transform, false);

                var rectTransform = imageRoot.GetComponent<RectTransform>();
                rectTransform.anchorMin = Vector2.zero;
                rectTransform.anchorMax = Vector2.one;
                rectTransform.offsetMin = Vector2.zero;
                rectTransform.offsetMax = Vector2.zero;

                _image = imageRoot.GetComponent<Image>();
                _image.raycastTarget = false;
            }

            _image.color = fadeColor;
        }
    }
}
