using UnityEngine;
using Cysharp.Threading.Tasks;
using R3;
using VContainer;
using MessagePipe;
using PFE.Core.Messages;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Floating text manager for displaying damage numbers, combat text, etc.
    /// Listens to DamageDealtMessage and spawns floating text at impact location.
    ///
    /// Uses MessagePipe for decoupled event handling:
    /// - Projectile publishes DamageDealtMessage
    /// - FloatingTextManager subscribes and spawns text
    /// - No direct coupling between combat and UI
    ///
    /// Setup:
    /// 1. Create a Canvas in your scene (Screen Space - Overlay)
    /// 2. Attach this script to a GameObject
    /// 3. Assign floatingTextPrefab
    /// 4. Text automatically spawns on damage events
    /// </summary>
    public class FloatingTextManager : MonoBehaviour
    {
        [Header("Prefabs")]
        [SerializeField]
        [Tooltip("Prefab for floating text (must have Text component)")]
        private GameObject _floatingTextPrefab;

        [Header("Settings")]
        [SerializeField]
        [Tooltip("Duration for floating text animation")]
        private float _animationDuration = 1.5f;

        [SerializeField]
        [Tooltip("Vertical distance to float up")]
        private float _floatDistance = 1f;

        [SerializeField]
        [Tooltip("Starting scale for text")]
        private float _startScale = 0.5f;

        [SerializeField]
        [Tooltip("Peak scale for text (pop effect)")]
        private float _peakScale = 1.2f;

        [SerializeField]
        [Tooltip("End scale for text")]
        private float _endScale = 0.8f;

        [Header("Colors")]
        [SerializeField]
        [Tooltip("Color for normal damage")]
        private Color _normalDamageColor = Color.white;

        [SerializeField]
        [Tooltip("Color for critical damage")]
        private Color _criticalDamageColor = Color.red;

        [SerializeField]
        [Tooltip("Color for healing")]
        private Color _healColor = Color.green;

        [SerializeField]
        [Tooltip("Color for miss/zero damage")]
        private Color _missColor = Color.gray;

        // MessagePipe subscriptions
        private ISubscriber<DamageDealtMessage> _damageSubscriber;
        private ISubscriber<HealMessage> _healSubscriber;

        // Composite disposable for cleanup
        private CompositeDisposable _disposables;

        /// <summary>
        /// Constructor injection via VContainer.
        /// </summary>
        [Inject]
        public void Construct(
            ISubscriber<DamageDealtMessage> damageSubscriber,
            ISubscriber<HealMessage> healSubscriber)
        {
            _damageSubscriber = damageSubscriber;
            _healSubscriber = healSubscriber;
        }

        private void Awake()
        {
            _disposables = new CompositeDisposable();

            // Validate prefab
            if (_floatingTextPrefab == null)
            {
                Debug.LogError("[FloatingTextManager] No floating text prefab assigned! Please assign in Inspector.");
            }
            else
            {
                // Verify prefab has required components
                var text = _floatingTextPrefab.GetComponent<UnityEngine.UI.Text>();

                if (text == null)
                {
                    Debug.LogError("[FloatingTextManager] Floating text prefab must have Text component!");
                }
            }
        }

        private void Start()
        {
            // Subscribe to damage messages
            if (_damageSubscriber != null)
            {
                _damageSubscriber.Subscribe(msg =>
                {
                    SpawnDamageText(msg.damage, msg.position, msg.isCritical, msg.isMiss);
                }).AddTo(_disposables);

                Debug.Log("[FloatingTextManager] Subscribed to DamageDealtMessage");
            }
            else
            {
                Debug.LogWarning("[FloatingTextManager] DamageDealtMessage subscriber not injected - text will not spawn");
            }

            // Subscribe to heal messages
            if (_healSubscriber != null)
            {
                _healSubscriber.Subscribe(msg =>
                {
                    SpawnHealText(msg.amount, msg.position);
                }).AddTo(_disposables);

                Debug.Log("[FloatingTextManager] Subscribed to HealMessage");
            }
            else
            {
                Debug.LogWarning("[FloatingTextManager] HealMessage subscriber not injected - heal text will not spawn");
            }
        }

        /// <summary>
        /// Spawn floating damage text at world position.
        /// </summary>
        public void SpawnDamageText(float damage, Vector3 worldPosition, bool isCritical = false, bool isMiss = false)
        {
            if (_floatingTextPrefab == null)
                return;

            // Convert world position to screen position
            Vector3 screenPos = Camera.main?.WorldToScreenPoint(worldPosition) ?? Vector3.zero;

            // Spawn text
            var textObj = Instantiate(_floatingTextPrefab, transform);
            textObj.transform.position = screenPos;

            // Get text component
            var text = textObj.GetComponent<UnityEngine.UI.Text>();

            // Set text content
            string textContent = isMiss ? "MISS" : damage.ToString("F0");
            Color textColor = _missColor;

            if (!isMiss)
            {
                if (isCritical)
                {
                    textContent = $"CRITICAL!\n{damage:F0}";
                    textColor = _criticalDamageColor;
                }
                else
                {
                    textColor = _normalDamageColor;
                }
            }

            // Set text and color
            if (text != null)
            {
                text.text = textContent;
                text.color = textColor;
            }

            // Critical hits are bigger
            float scaleMultiplier = isCritical ? 1.5f : 1f;

            // Animate
            AnimateFloatingText(textObj, scaleMultiplier).Forget();
        }

        /// <summary>
        /// Spawn floating heal text at world position.
        /// </summary>
        public void SpawnHealText(float amount, Vector3 worldPosition)
        {
            if (_floatingTextPrefab == null)
                return;

            // Convert world position to screen position
            Vector3 screenPos = Camera.main?.WorldToScreenPoint(worldPosition) ?? Vector3.zero;

            // Spawn text
            var textObj = Instantiate(_floatingTextPrefab, transform);
            textObj.transform.position = screenPos;

            // Get text component
            var text = textObj.GetComponent<UnityEngine.UI.Text>();

            string textContent = $"+{amount:F0}";

            // Set text and color
            if (text != null)
            {
                text.text = textContent;
                text.color = _healColor;
            }

            // Animate
            AnimateFloatingText(textObj, 1f).Forget();
        }

        /// <summary>
        /// Animate floating text: float up, scale pop, fade out.
        /// </summary>
        private async UniTaskVoid AnimateFloatingText(GameObject textObj, float scaleMultiplier)
        {
            float elapsed = 0f;
            Vector3 startPos = textObj.transform.position;
            Vector3 endPos = startPos + Vector3.up * _floatDistance * scaleMultiplier;

            // Set initial scale
            textObj.transform.localScale = Vector3.one * _startScale * scaleMultiplier;

            while (elapsed < _animationDuration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / _animationDuration;

                // Float up
                textObj.transform.position = Vector3.Lerp(startPos, endPos, t);

                // Scale (pop effect)
                float scale;
                if (t < 0.2f)
                {
                    // Grow to peak
                    scale = Mathf.Lerp(_startScale, _peakScale, t / 0.2f);
                }
                else
                {
                    // Shrink to end
                    scale = Mathf.Lerp(_peakScale, _endScale, (t - 0.2f) / 0.8f);
                }
                textObj.transform.localScale = Vector3.one * scale * scaleMultiplier;

                // Fade out
                var text = textObj.GetComponent<UnityEngine.UI.Text>();
                var canvasGroup = textObj.GetComponent<CanvasGroup>();

                if (canvasGroup != null)
                {
                    canvasGroup.alpha = 1f - t;
                }
                else
                {
                    // Fallback: fade text color
                    if (text != null)
                    {
                        Color c = text.color;
                        c.a = 1f - t;
                        text.color = c;
                    }
                }

                await UniTask.Yield();
            }

            // Destroy after animation
            Destroy(textObj);
        }

        private void OnDestroy()
        {
            _disposables?.Dispose();
        }
    }
}
