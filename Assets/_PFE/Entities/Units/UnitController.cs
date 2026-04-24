using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Physics;
namespace PFE.Entities.Units
{
    /// <summary>
    /// Base unit controller with custom physics.
    /// Replaces Unit.as from ActionScript - handles movement, collision, and physics.
    ///
    /// Key differences from AS3:
    /// - Uses Rigidbody2D in Kinematic mode for Unity collision integration
    /// - Vector2 instead of dx/dy variables
    /// - FixedDeltaTime instead of frame-based timing
    /// - Trigger-based collision instead of manual tile checking
    ///
    /// Original AS3 physics:
    /// - dx, dy for velocity
    /// - brake for friction
    /// - accel for acceleration
    /// - grav for gravity
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public class UnitController : MonoBehaviour, IDamageable
    {
        [Header("Configuration")]
        [SerializeField]
        protected UnitDefinition _stats;

        [Header("Debug")]
        [SerializeField]
        protected bool _showDebugInfo = false;

        // State
        protected Vector2 _velocity;
        protected bool _isGrounded;
        protected int _facingDirection = 1; // 1 = Right, -1 = Left

        // Components
        protected Rigidbody2D _rb;
        protected Collider2D _collider;
        private TilePhysicsController _cachedTilePhysics;
        private bool _hasTilePhysics;

        // Stats (optional - subclasses like PlayerController will provide their own)
        protected UnitStats _unitStats;

        // PFE Physics Constants (From Unit.as in AS3)
        // These were global constants in the original game
        protected const float FRICTION_GROUND = 1.0f; // 'brake' in AS3
        protected const float FRICTION_AIR = 0.1f; // Less friction in air
        protected const float GRAVITY = 30.0f; // 'grav' * World.ddy in AS3

        // Unity conversion constants
        // PFE used pixels, Unity uses units. Assuming 100 pixels = 1 Unity unit.
        protected const float PIXELS_TO_UNITS = 0.01f;

        protected virtual void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _collider = GetComponent<Collider2D>();

            // Use Kinematic mode - we control movement manually but Unity handles collision
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _rb.useFullKinematicContacts = true; // Enable collision detection
            _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous; // Prevent tunneling

            _cachedTilePhysics = GetComponent<TilePhysicsController>();
            _hasTilePhysics = _cachedTilePhysics != null;

            if (_stats != null)
            {
                // Set collider size from unit definition
                if (_collider is BoxCollider2D boxCollider)
                {
                    boxCollider.size = new Vector2(_stats.Width, _stats.Height);
                }
            }
        }

        protected virtual void FixedUpdate()
        {

            // Skip Rigidbody2D physics if TilePhysicsController is handling movement
            if (_hasTilePhysics)
                return;

            ApplyGravity();
            ApplyFriction();
            Move();
        }

        /// <summary>
        /// Apply gravity if not grounded.
        /// Replaces: if (!levit) dy += World.ddy * grav (from Unit.as)
        /// </summary>
        protected void ApplyGravity()
        {
            if (!_isGrounded)
            {
                _velocity.y -= GRAVITY * Time.fixedDeltaTime;
            }
        }

        /// <summary>
        /// Apply friction to slow down horizontal movement.
        /// Replaces: dx -= brake (from Unit.as)
        /// </summary>
        protected void ApplyFriction()
        {
            float friction = _isGrounded ? FRICTION_GROUND : FRICTION_AIR;
            friction *= Time.fixedDeltaTime * 60f; // Scale to frame-based

            if (_velocity.x > 0)
            {
                _velocity.x -= friction;
                if (_velocity.x < 0) _velocity.x = 0;
            }
            else if (_velocity.x < 0)
            {
                _velocity.x += friction;
                if (_velocity.x > 0) _velocity.x = 0;
            }
        }

        /// <summary>
        /// Apply calculated velocity to the Rigidbody.
        /// Replaces the position update logic from Unit.as step()
        /// Uses MovePosition for proper kinematic collision detection.
        /// </summary>
        protected void Move()
        {
            // Use MovePosition for kinematic bodies to ensure proper collision detection
            // This is necessary because setting linearVelocity on Kinematic bodies
            // doesn't always register collisions correctly with static geometry
            _rb.MovePosition(transform.position + (Vector3)_velocity * Time.fixedDeltaTime);

            // Update facing direction based on velocity
            if (_velocity.x > 0.1f) _facingDirection = 1;
            else if (_velocity.x < -0.1f) _facingDirection = -1;

            // Visual flip - scale sprite to face direction
            // Note: In PFE, sprites always face right, so we flip on left
            if (transform.localScale.x != _facingDirection)
            {
                transform.localScale = new Vector3(_facingDirection, 1, 1);
            }

            // Debug info
            if (_showDebugInfo)
            {
                Debug.DrawRay(transform.position, _velocity, Color.green);
            }
        }

        /// <summary>
        /// Add external force (explosions, knockback, etc.).
        /// Replaces Unit.as forces() function.
        /// </summary>
        public void AddForce(Vector2 force)
        {
            _velocity += force;
        }

        /// <summary>
        /// Set horizontal velocity directly.
        /// </summary>
        public void SetVelocityX(float velocityX)
        {
            _velocity.x = velocityX;
        }

        /// <summary>
        /// Set vertical velocity directly (for jumping).
        /// </summary>
        public void SetVelocityY(float velocityY)
        {
            _velocity.y = velocityY;
        }

        /// <summary>
        /// Ground detection using collision normals.
        /// Replaces the tile-based ground checking from AS3.
        /// </summary>
        private void OnCollisionEnter2D(Collision2D collision)
        {
            foreach (var contact in collision.contacts)
            {
                // If the collision normal is pointing up, we're on ground
                if (contact.normal.y > 0.7f)
                {
                    _isGrounded = true;
                    _velocity.y = 0; // Stop falling
                }
            }
        }

        private void OnCollisionStay2D(Collision2D collision)
        {
            foreach (var contact in collision.contacts)
            {
                if (contact.normal.y > 0.7f)
                {
                    _isGrounded = true;
                    _velocity.y = Mathf.Min(_velocity.y, 0); // Don't fall through floor
                }
            }
        }

        private void OnCollisionExit2D(Collision2D collision)
        {
            // Simple ground exit - might need refinement for complex geometry
            _isGrounded = false;
        }

        // Public getters

        public bool IsGrounded => _isGrounded;
        public Vector2 Velocity => _velocity;
        public int FacingDirection => _facingDirection;
        public UnitDefinition Stats => _stats;

        /// <summary>
        /// Whether this unit is player-controlled.
        /// Enemies don't degrade weapons, have different recoil, etc.
        /// </summary>
        public virtual bool IsPlayer => false;

        // === IDamageable Implementation ===

        /// <summary>
        /// Apply damage to this unit.
        /// Base implementation uses UnitStats if available.
        /// Subclasses can override for custom behavior (e.g., PlayerController).
        /// </summary>
        public virtual void TakeDamage(float damage)
        {
            if (_unitStats != null)
            {
                _unitStats.Damage(damage);

                // Handle death if applicable
                if (!IsAlive)
                {
                    OnDeath();
                }
            }
            else
            {
                Debug.LogWarning($"[{GetType().Name}] TakeDamage called but no UnitStats assigned!");
            }
        }

        /// <summary>
        /// Current health from stats.
        /// Returns 0 if no stats assigned.
        /// </summary>
        public virtual float CurrentHealth => _unitStats?.CurrentHp.Value ?? 0f;

        /// <summary>
        /// Maximum health from stats.
        /// Returns 1 if no stats assigned (to avoid divide by zero).
        /// </summary>
        public virtual float MaxHealth => _unitStats?.MaxHp.Value ?? 1f;

        /// <summary>
        /// Whether this unit is alive.
        /// Returns false if no stats assigned.
        /// </summary>
        public virtual bool IsAlive => _unitStats?.IsAlive ?? false;

        /// <summary>
        /// Called when this unit dies.
        /// Base implementation logs death. Subclasses can override for death effects.
        /// </summary>
        protected virtual void OnDeath()
        {
            Debug.Log($"[{GetType().Name}] has died!");

            // Base class doesn't destroy the GameObject.
            // Subclasses can override to play death animations, drop loot, etc.
        }
    }
}
