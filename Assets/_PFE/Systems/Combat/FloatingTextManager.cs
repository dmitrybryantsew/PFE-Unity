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
    ///
    /// <para><b>This class is currently inert, and that is now deliberate.</b> It renders through a
    /// <c>TextMesh</c> prefab assigned to <see cref="_floatingTextPrefab"/> — but it is a plain
    /// <c>IStartable</c>, not a <c>MonoBehaviour</c>, so there is no Inspector to assign one, and no
    /// such prefab exists in the project. The result used to be a <c>LogError</c> plus a
    /// <c>LogWarning</c> on <i>every single hit</i>, which is worse than drawing nothing: it buries the
    /// console exactly when someone is trying to read damage behaviour out of it.</para>
    ///
    /// <para><b>The working renderer is <see cref="FloatingDamageOverlay"/></b>, which draws through
    /// IMGUI and needs no prefab. Assign a prefab here and this manager starts drawing again — gated on
    /// the same <c>DamageNumbers</c> channel, so the two can never double-draw.</para>
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
            if (!IsRendererEnabled) return;

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
            if (!IsRendererEnabled) return;

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
                // Silent, not a warning: with no prefab assigned this fires on every hit, and the class
                // remarks explain why that is the wrong failure mode for a diagnostic.
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
        /// Whether this manager should draw at all: a prefab must be assigned <b>and</b> the
        /// <c>DamageNumbers</c> channel must be on.
        /// </summary>
        /// <remarks>
        /// The channel gate is what keeps this class and <see cref="FloatingDamageOverlay"/> from
        /// double-drawing if a prefab is ever assigned — both are renderers for the same events, and
        /// two renderers under one toggle is the shape that makes a toggle look broken.
        /// </remarks>
        private bool IsRendererEnabled
            => _floatingTextPrefab != null
               && PFE.Core.DebugOverlays.IsOn(PFE.Core.DebugOverlayChannel.DamageNumbers);

        /// <summary>
        /// Get a text instance from the object pool.
        /// Creates new if pool is empty (up to max size).
        /// </summary>
        /// <remarks>
        /// Returns <c>null</c> silently when no prefab is assigned. It used to <c>LogError</c> here,
        /// which fired once per hit — see the class remarks for why that was worse than drawing nothing.
        /// </remarks>
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
