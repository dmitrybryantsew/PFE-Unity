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

        // PFE physics constants (from Unit.as in AS3).
        //
        // These are no longer literals here. They were the last site of the gravity census: GRAVITY
        // was a hand-derived 30.0f, documented as "'grav' * World.ddy", which is the per-frame
        // VELOCITY idiom applied to an ACCELERATION slot — 1 px/frame² × 30 frames/s instead of
        // × 30² / 100 px-per-unit, i.e. 3.33× too strong. FRICTION_GROUND had the mirror-image
        // error: 1.0f scaled by `deltaTime * 60f`, so the frame rate was wrong (60, not AS3's 30)
        // AND the value was used as a px/frame velocity rather than a px/frame² acceleration,
        // netting 6.67× too strong. Both now come from UnitFallPhysics, which carries the AS3
        // file:line citations and is asserted directly by UnitFallPhysicsTests.
        //
        // The unit conversion is TileQueryConstants.PixelToUnit (0.01f) — never a hand-written
        // literal, which is how the port ended up with a `100f` and a `0.01f` in places that had
        // already drifted apart from the canonical pair.

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
        ///
        /// <para>AS3's gate is <c>!levit &amp;&amp; this.isLaz == 0</c> (<c>Unit.as:1962</c>), where
        /// <c>isLaz</c> means "standing on something"; <see cref="_isGrounded"/> is this port's
        /// equivalent and is set by the collision callbacks below. The magnitude and the terminal
        /// clamp live in <see cref="UnitFallPhysics.FallSpeed"/>, which is pure and tested — the
        /// value was the wrong part, not the shape of the branch.</para>
        ///
        /// <para><b>Known divergence, recorded.</b> This runs in <c>FixedUpdate</c> against
        /// <c>Time.fixedDeltaTime</c> (Unity's default 50 Hz), not on <c>SimLoop</c> at the clock's
        /// 30 Hz tick. That is a <i>cadence</i> difference, not a magnitude one: an acceleration
        /// integrated against any fixed delta produces the same speed after the same wall-clock
        /// time, so the census's "one value, 1 px/frame², everywhere that falls" holds. Moving this
        /// path onto the sim clock is separate work, and it is deliberately not bundled here because
        /// it would change when every motor-less unit moves rather than how fast it falls.</para>
        /// </summary>
        protected void ApplyGravity()
        {
            if (!_isGrounded)
            {
                _velocity.y = UnitFallPhysics.FallSpeed(_velocity.y, Time.fixedDeltaTime);
            }
        }

        /// <summary>
        /// Apply friction to slow down horizontal movement.
        /// Replaces: dx -= brake (from Unit.as)
        ///
        /// <para><b>The invented air-friction branch is gone.</b> <c>FRICTION_AIR = 0.1f</c> had no
        /// AS3 counterpart: the only damping AS3 applies outside the <c>stay</c> branch is the water
        /// branch <c>dx *= 0.5</c> (<c>Unit.as:1958</c>) and the flight branch's speed cap, neither
        /// of which is "air friction". This is the same removal the prop census made when it deleted
        /// <c>RoomObjectPhysicsLayer.AirDrag</c>, and for the same reason: a force AS3 does not have
        /// is not a tuning choice, it is a different game.</para>
        ///
        /// <para><b>Still divergent, and named rather than silently changed:</b> AS3 applies braking
        /// only while <c>stay</c> and branches on the walk input toward <c>maxSpeed</c>
        /// (<c>Unit.as:1970-1990</c>). This path has no walk input — it exists for units with no
        /// motor — so it brakes toward rest unconditionally. See
        /// <see cref="UnitFallPhysics.GroundBrake"/>.</para>
        /// </summary>
        protected void ApplyFriction()
        {
            _velocity.x = UnitFallPhysics.GroundBrake(_velocity.x, Time.fixedDeltaTime);
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
        /// This unit's faction — the team id that decides who may damage whom
        /// (<see cref="PFE.Systems.Weapons.FactionRule"/>).
        ///
        /// <para>AS3 keeps it on the unit and sets the player's <i>in code</i>, not in data:
        /// <c>UnitPlayer.as:385</c> assigns <c>fraction = F_PLAYER</c>, and <c>littlepip</c> carries no
        /// <c>fraction</c> attribute at all. <see cref="PFE.Entities.Player.PlayerController"/>
        /// mirrors that with an override rather than by writing to <c>_stats</c>, which is a
        /// ScriptableObject shared by every instance of the unit — mutating it at runtime would leak
        /// a value into the project asset.</para>
        ///
        /// <para><b>Data caveat, read before relying on this for NPC-vs-NPC.</b> The imported unit
        /// assets predate the parent-chain resolution added to <c>UnitDataImporter</c>, so a spawnable
        /// that inherits its faction currently reads as <c>Raider</c> whatever its template says —
        /// monsters and robots included. Player-versus-everyone is unaffected, because the player's
        /// value comes from the override; the finer distinctions need a re-import.</para>
        /// </summary>
        public virtual FactionType Faction => _stats != null ? _stats.fraction : FactionType.Neutral;

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
