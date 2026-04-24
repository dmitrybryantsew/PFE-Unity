using UnityEngine;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// Extended physics for visual effect particles.
    /// Based on ActionScript Part.as particle system (liv, mliv, isAlph, isPreAlph, etc.).
    /// Handles lifetime, fade in/out, animation, and water interaction.
    /// </summary>
    [DisallowMultipleComponent]
    public class ParticlePhysics : PhysicsBody
    {
        [Header("Particle Lifetime")]
        [Tooltip("Current lifetime in frames (liv in ActionScript)")]
        [SerializeField] protected int lifetime = 20;

        [Tooltip("Maximum lifetime (mliv in ActionScript)")]
        [SerializeField] protected int maxLifetime = 20;

        [Header("Fade Effects")]
        [Tooltip("Fade out in last frames (isAlph in ActionScript)")]
        [SerializeField] protected bool fadeOutOnEnd = false;

        [Tooltip("Fade in on first frames (isPreAlph in ActionScript)")]
        [SerializeField] protected bool fadeInOnStart = false;

        [Header("Animation")]
        [Tooltip("Animation mode (isAnim in ActionScript: 0=static, 1=play once, 2=loop)")]
        [Range(0, 2)]
        [SerializeField] protected int animationMode = 0;

        [SerializeField] protected float animationSpeed = 1f;
        [SerializeField] protected int animationMaxFrame = -1;

        [Header("Delay")]
        [Tooltip("Delay frames before starting (otklad in ActionScript)")]
        [SerializeField] protected int delayFrames = 0;

        [Header("Water Interaction")]
        [Tooltip("Water interaction (0=none, 1=only in water, 2=only outside water)")]
        [Range(0, 2)]
        [SerializeField] protected int waterMode = 0;

        [Header("Renderer")]
        [SerializeField] protected SpriteRenderer spriteRenderer;
        [SerializeField] protected Animator animator;

        protected float currentAnimationFrame = 0f;
        protected int currentDelayFrames = 0;
        protected bool isVisible = false;

        /// <summary>
        /// Current lifetime (liv in ActionScript)
        /// </summary>
        public int Lifetime
        {
            get => lifetime;
            set => lifetime = Mathf.Max(0, value);
        }

        /// <summary>
        /// Maximum lifetime (mliv in ActionScript)
        /// </summary>
        public int MaxLifetime
        {
            get => maxLifetime;
            set => maxLifetime = Mathf.Max(1, value);
        }

        /// <summary>
        /// Is particle alive?
        /// </summary>
        public bool IsAlive => lifetime > 0;

        /// <summary>
        /// Remaining lifetime as normalized value (0-1)
        /// </summary>
        public float NormalizedLifetime => maxLifetime > 0 ? (float)lifetime / maxLifetime : 0f;

        protected override void Awake()
        {
            base.Awake();

            // Get renderer if not assigned
            if (spriteRenderer == null)
            {
                spriteRenderer = GetComponent<SpriteRenderer>();
            }
            if (animator == null)
            {
                animator = GetComponent<Animator>();
            }

            currentDelayFrames = delayFrames;
        }

        protected virtual void OnEnable()
        {
            // Reset particle state
            lifetime = maxLifetime;
            currentDelayFrames = delayFrames;
            currentAnimationFrame = 0f;
            isVisible = false;

            if (spriteRenderer != null)
            {
                spriteRenderer.enabled = false;
            }
        }

        /// <summary>
        /// Step particle simulation
        /// Based on Part.as step() function
        /// </summary>
        public virtual void ParticleStep()
        {
            // Handle delay (otklad in ActionScript)
            if (currentDelayFrames > 0)
            {
                currentDelayFrames--;
                SetVisible(false);
                return;
            }

            // Make visible after delay
            if (!isVisible)
            {
                SetVisible(true);
                if (animator != null && animationMode > 0)
                {
                    animator.enabled = true;
                }
            }

            // Update physics
            PhysicsStep(Time.fixedDeltaTime);

            // Update fade effects
            UpdateFade();

            // Update animation
            UpdateAnimation();

            // Check water interaction
            if (waterMode > 0 && CheckWaterCollision())
            {
                lifetime = 1; // Kill particle
            }

            // Decrease lifetime
            lifetime--;

            // Kill particle if lifetime expired
            if (lifetime <= 0)
            {
                OnParticleEnd();
            }
        }

        /// <summary>
        /// Update fade in/out effects based on lifetime
        /// </summary>
        protected virtual void UpdateFade()
        {
            if (spriteRenderer == null) return;

            float alpha = 1f;

            // Fade out in last frames (isAlph in ActionScript)
            if (fadeOutOnEnd && lifetime < config.particleFadeFrames)
            {
                alpha = (float)lifetime / config.particleFadeFrames;
            }

            // Fade in on first frames (isPreAlph in ActionScript)
            if (fadeInOnStart && (maxLifetime - lifetime) < config.particleFadeFrames)
            {
                alpha = (float)(maxLifetime - lifetime) / config.particleFadeFrames;
            }

            spriteRenderer.color = new Color(spriteRenderer.color.r, spriteRenderer.color.g, spriteRenderer.color.b, alpha);
        }

        /// <summary>
        /// Update animation frame
        /// </summary>
        protected virtual void UpdateAnimation()
        {
            if (animator == null || animationMode == 0) return;

            // Animation is handled by Animator component
            // This is for custom sprite frame animation if needed
            currentAnimationFrame += animationSpeed;

            if (animationMaxFrame > 0 && currentAnimationFrame >= animationMaxFrame)
            {
                currentAnimationFrame = 0f;
            }
        }

        /// <summary>
        /// Check if particle is in water
        /// </summary>
        protected virtual bool CheckWaterCollision()
        {
            // Check tile at current position for water property
            Collider2D[] colliders = Physics2D.OverlapCircleAll(transform.position, 0.1f);

            foreach (var col in colliders)
            {
                if (col.CompareTag("Water"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Set particle visibility
        /// </summary>
        protected virtual void SetVisible(bool visible)
        {
            isVisible = visible;

            if (spriteRenderer != null)
            {
                spriteRenderer.enabled = visible;
            }

            if (animator != null)
            {
                if (visible && animationMode > 0)
                {
                    animator.enabled = true;
                }
                else if (!visible)
                {
                    animator.enabled = false;
                }
            }
        }

        /// <summary>
        /// Called when particle lifetime ends
        /// </summary>
        protected virtual void OnParticleEnd()
        {
            // Pool or destroy particle
            gameObject.SetActive(false);
        }

        /// <summary>
        /// Initialize particle with settings
        /// </summary>
        public virtual void Initialize(
            Vector2 position,
            Vector2 velocity,
            int lifetimeFrames,
            bool fadeOut = false,
            bool fadeIn = false,
            int animMode = 0)
        {
            transform.position = position;
            velocityX = velocity.x;
            velocityY = velocity.y;
            maxLifetime = lifetimeFrames;
            lifetime = lifetimeFrames;
            fadeOutOnEnd = fadeOut;
            fadeInOnStart = fadeIn;
            animationMode = animMode;

            isMoving = true;
        }

        /// <summary>
        /// Initialize particle with full physics settings
        /// </summary>
        public virtual void InitializeFull(
            Vector2 position,
            Vector2 velocity,
            float accelerationY,
            float rotationSpeed,
            float brakeCoef,
            int lifetimeFrames,
            bool fadeOut = false,
            bool fadeIn = false,
            int animMode = 0,
            int delay = 0)
        {
            Initialize(position, velocity, lifetimeFrames, fadeOut, fadeIn, animMode);
            accelY = accelerationY;
            rotationVelocity = rotationSpeed;
            brake = Mathf.Clamp01(brakeCoef);
            delayFrames = delay;
            currentDelayFrames = delay;
        }
    }
}
