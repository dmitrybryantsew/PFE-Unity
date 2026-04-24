using UnityEngine;
using VContainer;
using VContainer.Unity;
using MessagePipe;
using R3;
using PFE.Core.Messages;
using System;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Manages floating damage numbers and combat text effects.
    /// Subscribes to damage events via MessagePipe and spawns floating text at hit locations.
    ///
    /// Design rationale:
    /// - Decoupled from damage calculation - only handles visual feedback
    /// - Uses MessagePipe for event-based communication (no direct references)
    /// - Object pooling for performance (reuses text instances)
    /// - Configurable visual styles (crit, miss, heal, etc.)
    ///
    /// Phase 3 implementation from combat system status docs.
    /// </summary>
    public class FloatingTextManager : IStartable, IDisposable
    {
        [SerializeField]
        private GameObject _floatingTextPrefab;

        private readonly ISubscriber<DamageDealtMessage> _damageSubscriber;
        private readonly ISubscriber<HealMessage> _healSubscriber;
        private readonly CompositeDisposable _disposables = new();

        // Object pool for floating text instances
        private readonly System.Collections.Generic.Stack<GameObject> _textPool =
            new System.Collections.Generic.Stack<GameObject>();
        private const int POOL_SIZE = 20;

        [Inject]
        public FloatingTextManager(
            ISubscriber<DamageDealtMessage> damageSubscriber,
            ISubscriber<HealMessage> healSubscriber)
        {
            _damageSubscriber = damageSubscriber;
            _healSubscriber = healSubscriber;
        }

        void IStartable.Start()
        {
            // Subscribe to damage events
            _damageSubscriber.Subscribe(OnDamageDealt).AddTo(_disposables);

            // Subscribe to heal events
            _healSubscriber.Subscribe(OnHeal).AddTo(_disposables);

            // Pre-warm object pool
            PreWarmPool();
        }

        void IDisposable.Dispose()
        {
            _disposables.Dispose();
        }

        /// <summary>
        /// Called when damage is dealt to a target.
        /// Spawns floating text showing damage amount.
        /// </summary>
        private void OnDamageDealt(DamageDealtMessage message)
        {
            if (message.isMiss)
            {
                SpawnFloatingText("MISS", message.position, Color.gray, isCritical: false, isMiss: true);
            }
            else
            {
                string text = Mathf.CeilToInt(message.damage).ToString();
                Color color = message.isCritical ? Color.red : Color.white;
                float fontSize = message.isCritical ? 1.5f : 1.0f;

                SpawnFloatingText(text, message.position, color, message.isCritical);
            }
        }

        /// <summary>
        /// Called when a unit is healed.
        /// Spawns floating text showing heal amount (in green).
        /// </summary>
        private void OnHeal(HealMessage message)
        {
            string text = $"+{Mathf.CeilToInt(message.amount)}";
            SpawnFloatingText(text, message.position, Color.green, isCritical: false);
        }

        /// <summary>
        /// Spawn a floating text at the given position.
        /// Uses object pooling for performance.
        /// </summary>
        private void SpawnFloatingText(
            string text,
            Vector3 position,
            Color color,
            bool isCritical,
            bool isMiss = false)
        {
            // Get text instance from pool or create new
            GameObject textObj = GetFromPool();

            if (textObj == null)
            {
                Debug.LogWarning("[FloatingTextManager] Failed to get text from pool");
                return;
            }

            textObj.transform.position = position + Vector3.up * 0.5f;
            textObj.SetActive(true);

            // Get text component and set content
            // Note: Assumes TextMesh component is attached
            var textComponent = textObj.GetComponent<TextMesh>();

            if (textComponent != null)
            {
                textComponent.text = text;
                textComponent.color = color;

                if (isCritical)
                {
                    textComponent.characterSize *= 1.5f;
                    textComponent.fontStyle = FontStyle.Bold;
                }
                else if (isMiss)
                {
                    textComponent.characterSize *= 0.8f;
                    textComponent.fontStyle = FontStyle.Italic;
                }

                // Animate floating up and fade out
                AnimateFloatingText(textObj, isMiss ? 1.0f : 0.8f);
            }
            else
            {
                Debug.LogError("[FloatingTextManager] Floating text prefab missing TextMesh component!");
                ReturnToPool(textObj);
            }
        }

        /// <summary>
        /// Animate floating text moving up and fading out.
        /// Uses coroutine for smooth animation.
        /// </summary>
        private void AnimateFloatingText(GameObject textObj, float lifetime)
        {
            // Simple animation - move up and fade
            // In production, use UniTask or DOTween for better performance
            textObj.AddComponent<FloatingTextAnimation>().Initialize(lifetime, ReturnToPool);
        }

        /// <summary>
        /// Get a text instance from the object pool.
        /// Creates new if pool is empty (up to max size).
        /// </summary>
        private GameObject GetFromPool()
        {
            if (_textPool.Count > 0)
            {
                return _textPool.Pop();
            }

            // Create new if pool isn't at max capacity
            if (_floatingTextPrefab != null)
            {
                GameObject newText = UnityEngine.Object.Instantiate(_floatingTextPrefab);
                newText.SetActive(false);
                return newText;
            }

            Debug.LogError("[FloatingTextManager] No prefab assigned for floating text!");
            return null;
        }

        /// <summary>
        /// Return a text instance to the object pool for reuse.
        /// </summary>
        private void ReturnToPool(GameObject textObj)
        {
            if (textObj == null) return;

            textObj.SetActive(false);

            // Remove animation component if present
            var anim = textObj.GetComponent<FloatingTextAnimation>();
            if (anim != null)
            {
                UnityEngine.Object.Destroy(anim);
            }

            _textPool.Push(textObj);
        }

        /// <summary>
        /// Pre-warm the object pool with initial instances.
        /// Called on startup to prevent hitches during combat.
        /// </summary>
        private void PreWarmPool()
        {
            if (_floatingTextPrefab == null)
            {
                Debug.LogWarning("[FloatingTextManager] No prefab assigned - skipping pool pre-warm");
                return;
            }

            for (int i = 0; i < POOL_SIZE; i++)
            {
                GameObject textObj = UnityEngine.Object.Instantiate(_floatingTextPrefab);
                textObj.SetActive(false);
                _textPool.Push(textObj);
            }

            Debug.Log($"[FloatingTextManager] Pre-warmed pool with {POOL_SIZE} instances");
        }
    }

    /// <summary>
    /// Simple MonoBehaviour component for animating floating text.
    /// Handles upward movement, scaling, and fading over lifetime.
    /// </summary>
    internal class FloatingTextAnimation : MonoBehaviour
    {
        private float _lifetime;
        private float _timer;
        private System.Action<GameObject> _onComplete;
        private Vector3 _startPosition;
        private TextMesh _textComponent;

        public void Initialize(float lifetime, System.Action<GameObject> onComplete)
        {
            _lifetime = lifetime;
            _timer = 0f;
            _onComplete = onComplete;
            _startPosition = transform.position;
            _textComponent = GetComponent<TextMesh>();
        }

        void Update()
        {
            _timer += Time.deltaTime;

            if (_timer >= _lifetime)
            {
                if (_onComplete != null)
                {
                    _onComplete(gameObject);
                }
                return;
            }

            // Calculate progress (0 to 1)
            float progress = _timer / _lifetime;

            // Move upward
            transform.position = _startPosition + Vector3.up * (progress * 2f);

            // Fade out
            if (_textComponent != null)
            {
                Color color = _textComponent.color;
                color.a = 1f - progress;
                _textComponent.color = color;
            }

            // Scale down slightly
            transform.localScale = Vector3.one * (1f - progress * 0.3f);
        }
    }
}
