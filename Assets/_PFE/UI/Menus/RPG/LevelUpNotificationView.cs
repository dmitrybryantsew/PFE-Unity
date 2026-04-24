using UnityEngine;
using UnityEngine.UI;
using R3;
using PFE.Systems.RPG;

namespace PFE.UI.Menus.RPG
{
    /// <summary>
    /// View component for level-up notifications.
    /// Displays a popup when the player levels up.
    ///
    /// Subscribes to level-up events from CharacterStatsViewModel
    /// and shows an animated notification.
    /// </summary>
    public class LevelUpNotificationView : MonoBehaviour
    {
        [Header("ViewModel")]
        [SerializeField]
        [Tooltip("ViewModel to bind to")]
        private CharacterStatsViewModel viewModel;

        [Header("UI Components")]
        [SerializeField] private GameObject notificationPanel;
        [SerializeField] private Text levelUpText;
        [SerializeField] private Text newLevelText;
        [SerializeField] private Text skillPointsGainedText;
        [SerializeField] private Text perkPointsGainedText;
        [SerializeField] private Button continueButton;

        [Header("Animation")]
        [SerializeField] private float showDuration = 3f;
        [SerializeField] private float fadeInSpeed = 2f;
        [SerializeField] private float scaleUpAmount = 1.2f;
        [SerializeField] private float scaleSpeed = 3f;

        private CanvasGroup _canvasGroup;
        private CompositeDisposable _disposables;
        private bool _isShowing;

        /// <summary>
        /// Initialize the view with a ViewModel.
        /// </summary>
        public void Initialize(CharacterStatsViewModel vm)
        {
            viewModel = vm;
            SetupBindings();
        }

        private void Awake()
        {
            _canvasGroup = notificationPanel?.GetComponent<CanvasGroup>();
            if (_canvasGroup == null)
            {
                _canvasGroup = notificationPanel?.AddComponent<CanvasGroup>();
            }

            // Hide by default
            HideImmediate();
        }

        private void Start()
        {
            if (viewModel != null)
            {
                SetupBindings();
            }

            if (continueButton != null)
            {
                continueButton.onClick.AddListener(OnContinueClicked);
            }
        }

        private void SetupBindings()
        {
            if (viewModel == null) return;

            _disposables = new CompositeDisposable();

            // Subscribe to level-up events
            viewModel.OnLevelUp += HandleLevelUp;
        }

        private void HandleLevelUp(int newLevel)
        {
            ShowLevelUpNotification(newLevel);
        }

        /// <summary>
        /// Show the level-up notification.
        /// </summary>
        public void ShowLevelUpNotification(int newLevel)
        {
            if (notificationPanel == null) return;

            _isShowing = true;

            // Update text
            if (levelUpText != null)
                levelUpText.text = "LEVEL UP!";

            if (newLevelText != null)
                newLevelText.text = $"You are now level {newLevel}!";

            // Standard 5 skill points and 1 perk point per level
            if (skillPointsGainedText != null)
            {
                skillPointsGainedText.text = "+5 Skill Points";
            }

            if (perkPointsGainedText != null)
                perkPointsGainedText.text = "+1 Perk Point";

            // Show panel
            notificationPanel.SetActive(true);

            // Reset and start animation
            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = 0f;
            }

            // Stop any existing animations
            this.StopAllCoroutines();

            // Start show animation
            StartCoroutine(ShowAnimationCoroutine());
        }

        private System.Collections.IEnumerator ShowAnimationCoroutine()
        {
            // Fade in
            if (_canvasGroup != null)
            {
                while (_canvasGroup.alpha < 1f)
                {
                    _canvasGroup.alpha += Time.deltaTime * fadeInSpeed;
                    yield return null;
                }
                _canvasGroup.alpha = 1f;
            }

            // Scale up effect
            Vector3 originalScale = notificationPanel.transform.localScale;
            Vector3 targetScale = originalScale * scaleUpAmount;

            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime * scaleSpeed;
                notificationPanel.transform.localScale = Vector3.Lerp(
                    originalScale,
                    targetScale,
                    Mathf.Sin(t * Mathf.PI * 0.5f) // Ease out
                );
                yield return null;
            }

            t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime * scaleSpeed;
                notificationPanel.transform.localScale = Vector3.Lerp(
                    targetScale,
                    originalScale,
                    Mathf.Sin(t * Mathf.PI * 0.5f) // Ease out
                );
                yield return null;
            }

            notificationPanel.transform.localScale = originalScale;

            // Wait for duration or button press
            float elapsed = 0f;
            while (elapsed < showDuration && _isShowing)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            // Auto-hide if not clicked
            if (_isShowing)
            {
                Hide();
            }
        }

        /// <summary>
        /// Hide the notification with fade out.
        /// </summary>
        public void Hide()
        {
            _isShowing = false;
            this.StopAllCoroutines();
            StartCoroutine(HideAnimationCoroutine());
        }

        private System.Collections.IEnumerator HideAnimationCoroutine()
        {
            if (_canvasGroup != null)
            {
                while (_canvasGroup.alpha > 0f)
                {
                    _canvasGroup.alpha -= Time.deltaTime * fadeInSpeed;
                    yield return null;
                }
                _canvasGroup.alpha = 0f;
            }

            notificationPanel.SetActive(false);
        }

        /// <summary>
        /// Hide immediately without animation.
        /// </summary>
        public void HideImmediate()
        {
            _isShowing = false;
            this.StopAllCoroutines();

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = 0f;
            }

            if (notificationPanel != null)
            {
                notificationPanel.SetActive(false);
            }
        }

        private void OnContinueClicked()
        {
            Hide();
        }

        private void OnDestroy()
        {
            if (viewModel != null)
            {
                viewModel.OnLevelUp -= HandleLevelUp;
            }

            _disposables?.Dispose();

            if (continueButton != null)
            {
                continueButton.onClick.RemoveListener(OnContinueClicked);
            }
        }
    }
}
