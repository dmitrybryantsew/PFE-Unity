using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
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

        /// <summary>
        /// The room's tile query — AS3's <c>loc</c>, and the authority for <see cref="_isGrounded"/>.
        /// Assigned by the spawner (<c>RoomUnitSpawner</c>), which is the layer that knows the room.
        /// Null for a unit built without one (a bare test spawn), in which case the collision callbacks
        /// below remain the only source of groundedness.
        /// </summary>
        protected ITileQueryService _tileQuery;

        /// <summary>
        /// Colliders currently providing upward support, keyed by the <i>other</i> collider. Only used
        /// on the callback fallback path — see <see cref="OnCollisionExit2D"/> for why a set rather than
        /// a bool.
        /// </summary>
        readonly HashSet<Collider2D> _supportingColliders = new HashSet<Collider2D>();

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

            ApplyDefinitionToCollider();
        }

        /// <summary>
        /// Size the collider from the unit definition's footprint. AS3 sizes a unit's box from its
        /// <c>&lt;phis sX sY&gt;</c> pair, so <see cref="UnitDefinition.Width"/> /
        /// <see cref="UnitDefinition.Height"/> are the port's equivalent.
        /// </summary>
        protected void ApplyDefinitionToCollider()
        {
            if (_stats == null || _collider == null)
            {
                return;
            }

            if (_collider is BoxCollider2D boxCollider)
            {
                boxCollider.size = new Vector2(_stats.Width, _stats.Height);
            }
        }

        /// <summary>
        /// Inject this unit's definition and stats — the seam a runtime spawner needs.
        ///
        /// <para><b>Why a method and not a public field.</b> <see cref="_stats"/> and
        /// <see cref="_unitStats"/> are <c>protected</c> serialized fields that <b>no code assigns
        /// anywhere in the repo</b>, and no unit prefab exists to assign them (<c>Assets/_PFE/Prefabs</c>
        /// has no subdirectories at all). Without this seam a spawned unit reads
        /// <see cref="VulnerabilityData.Neutral"/> for its <c>&lt;vulner&gt;</c> table, has no health,
        /// and logs <c>"TakeDamage called but no UnitStats assigned!"</c> on every hit — precisely the
        /// "correct-but-empty until a spawner assigns a definition" state the
        /// <see cref="Vulnerabilities"/> doc comment describes.</para>
        ///
        /// <para><b>Call it after the component exists.</b> <c>AddComponent</c> runs <see cref="Awake"/>
        /// immediately, i.e. with <c>_stats == null</c>, so the collider sizing done there is repeated
        /// here rather than assumed — otherwise every spawned unit would keep the collider's default
        /// size and a 2×2 dummy would be as wide as a raider.</para>
        /// </summary>
        public virtual void Initialize(UnitDefinition stats, UnitStats unitStats)
        {
            _stats = stats;
            _unitStats = unitStats;
            ApplyDefinitionToCollider();
            SeedEvasionFromDefinition();
        }

        /// <summary>
        /// Copy the definition's authored evasion onto the live stats — the producer for
        /// <c>@dexter</c>.
        ///
        /// <para>AS3 sets <c>dexter</c> in the <i>base</i> <c>Unit</c> constructor from the unit node
        /// (<c>Unit.as:1170-1172</c>), so it is a property of the unit, not of a subclass — which is why
        /// this lives here and not in a per-controller override. 71 units in <c>AllData.as</c> carry the
        /// attribute (up to <c>dexter='100'</c> on the stationary <c>npc</c>), and without this copy the
        /// imported value would sit on the definition and never reach the hit test — the same
        /// computed-then-dropped shape as the sprite pivot and the armoured dummy's <c>skin</c>.</para>
        ///
        /// <para>Only <c>dexter</c> is seeded. <c>dexterPlus</c> and <c>dodge</c> have no definition
        /// field because the oracle has no data for them — they are runtime, player-only values driven
        /// by the armour/RPG bridge. Seeding them here would invent a source.</para>
        ///
        /// <para><b>Data caveat.</b> Unit assets imported before <c>UnitDataImporter</c> stopped writing
        /// its defaults last carry <c>dexterity = 1</c> regardless of the template (the importer's own
        /// comment records that <c>dexter='100'</c> was being overwritten). So a stale asset reads as
        /// baseline evasion; a re-import is what makes the authored values live.</para>
        /// </summary>
        void SeedEvasionFromDefinition()
        {
            if (_stats == null || _unitStats == null)
            {
                return;
            }

            _unitStats.dexterity = _stats.dexterity;
        }

        /// <summary>
        /// The unit's evasion projection, read by the hit-avoidance test — AS3
        /// <c>Unit.dexter</c>/<c>dexterPlus</c>/<c>dodge</c>.
        ///
        /// <para>Falls back to <see cref="EvasionState.Default"/> when no stats are assigned, which is
        /// the oracle's own field defaults (dexter 1, the rest 0) — <b>not</b> all-zeroes, which would
        /// mean <c>dexter &lt;= 0</c>, i.e. "hit by everything".</para>
        /// </summary>
        public virtual EvasionState Evasion => _unitStats?.Evasion ?? EvasionState.Default;

        /// <summary>
        /// Give this unit the room's tile query, so groundedness can be answered the way AS3 answers
        /// it — <c>isLaz</c> (<c>Unit.as:1962</c>) — instead of from Unity collision callbacks.
        ///
        /// <para><b>Why this is the fix for "units fall through the floor when I walk past them".</b>
        /// A unit is seated 1 px above the tile surface, which is exactly Box2D's contact tolerance, so
        /// a resting unit's floor contact is a coin flip on float rounding. Meanwhile
        /// <c>OnCollisionExit2D</c> cleared <c>_isGrounded</c> for <b>any</b> collider — including the
        /// player, who only interpenetrates because both bodies are Kinematic. Once gravity starts on a
        /// Kinematic body, <c>MovePosition</c> is not stopped by static geometry and the unit walks out
        /// of the room. <see cref="UnitGroundProbe"/> documents the measurement;
        /// <c>ITileQueryService.IsOnGround</c> has a 10 px band and is not boundary-sensitive.</para>
        ///
        /// <para>Idempotent, and deliberately not called from <see cref="Awake"/>: <c>AddComponent</c>
        /// runs <c>Awake</c> before the spawner has a definition or a room, which is the same ordering
        /// trap <see cref="Initialize"/> exists for.</para>
        /// </summary>
        public virtual void SetTileQuery(ITileQueryService tileQuery)
        {
            _tileQuery = tileQuery;
        }

        protected virtual void FixedUpdate()
        {

            // Skip Rigidbody2D physics if TilePhysicsController is handling movement
            if (_hasTilePhysics)
                return;

            ResolveGroundState();
            ApplyGravity();
            ApplyFriction();
            Move();
        }

        /// <summary>
        /// Ask the room whether this unit is standing on something, before gravity reads the answer.
        ///
        /// <para>Runs every physics step rather than being event-driven, because the question is about
        /// the <i>current</i> tile under the feet, not about a contact that happened. This is what makes
        /// a stale collision exit harmless: whatever the callbacks left in
        /// <see cref="_isGrounded"/>, it is overwritten here from the tile data.</para>
        ///
        /// <para>No query means no room (a unit spawned outside one) — leave
        /// <see cref="_isGrounded"/> to the collision callbacks rather than forcing it false, which
        /// would make every such unit fall.</para>
        /// </summary>
        protected void ResolveGroundState()
        {
            if (_tileQuery == null || _collider == null)
            {
                return;
            }

            Rect probe = UnitGroundProbe.ToProbeRectPixels(
                _collider.bounds, TileQueryConstants.PixelToUnit);

            _isGrounded = _tileQuery.IsOnGround(probe);
        }

        /// <summary>
        /// Apply gravity if not grounded.
        /// Replaces: if (!levit) dy += World.ddy * grav (from Unit.as)
        ///
        /// <para>AS3's gate is <c>!levit &amp;&amp; this.isLaz == 0</c> (<c>Unit.as:1962</c>), where
        /// <c>isLaz</c> means "standing on something"; <see cref="_isGrounded"/> is this port's
        /// equivalent and is now resolved from the room's tile query by
        /// <see cref="ResolveGroundState"/> (the collision callbacks below are the fallback for a unit
        /// with no room). The magnitude and the terminal clamp live in
        /// <see cref="UnitFallPhysics.FallSpeed"/>, which is pure and tested — the
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
            if (_isGrounded)
            {
                // Grounded: AS3 integrates nothing while `isLaz` (Unit.as:1962), so dy does not grow.
                // Cancelling a DOWNWARD dy is the part that matters — a residual negative velocity is
                // still applied by Move(), and MovePosition on a Kinematic body is not blocked by static
                // geometry, so even a small one walks the unit into the tile it is standing on. Upward
                // velocity is left alone so a jump is not swallowed.
                if (_velocity.y < 0f)
                {
                    _velocity.y = 0f;
                }

                return;
            }

            _velocity.y = UnitFallPhysics.FallSpeed(_velocity.y, Time.fixedDeltaTime);
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
            // AS3 wraps the whole position integration in `if(!this.fixed)` (Unit.as:1809): the
            // `forces()` and `control()` calls sit ABOVE that gate and still run, but `run()` — the
            // function holding `X += dx` — is never called, so X/Y never change. MovePosition is this
            // port's only write to position, so it is the single call the gate has to cover.
            //
            // Two consequences, both the oracle's behaviour rather than a shortcut:
            //   - a fixed unit is immune to knockback DISPLACEMENT. `otbros` has no `fixed` gate
            //     (only `invulner`), so a shot still adds to dx — the value simply never lands.
            //   - it takes no collision response, because run() also owns wall resolution
            //     (turnX / kray / wall damage) and skipping MovePosition skips the contacts.
            // Its immunity to being MOVED is therefore not a new rule layered on top; it falls out of
            // gating the same single write the oracle gates.
            if (!IsFixed)
            {
                Vector2 deltaUnits = _velocity * Time.fixedDeltaTime;

                // Horizontal motion is resolved against the room's tiles; vertical is not.
                //
                // MovePosition on a Kinematic body is not blocked by static geometry, so before this
                // the only thing that ever stopped a unit was the ground probe below the feet. A unit
                // walked and was knocked straight through walls. See UnitWallMotion.
                deltaUnits.x = ResolveHorizontalMotion(deltaUnits.x);

                _rb.MovePosition(transform.position + (Vector3)deltaUnits);
            }

            // Facing is deliberately OUTSIDE the gate. AS3 sets `storona` in control(), which runs
            // before :1809, and setVisPos()/animate() still run for a fixed unit — so a pinned turret
            // that turns to face what it is shooting at is the oracle's behaviour, not a leak. Gating
            // this would be the tempting "obviously right" move and it would be wrong.
            //
            // Update facing direction based on velocity
            if (_velocity.x > 0.1f) _facingDirection = 1;
            else if (_velocity.x < -0.1f) _facingDirection = -1;

            ApplyFacingToTransform();

            // Debug info
            if (_showDebugInfo)
            {
                Debug.DrawRay(transform.position, _velocity, Color.green);
            }
        }

        /// <summary>
        /// Sweeps this step's horizontal motion against the room's tiles and returns the distance the
        /// unit may actually travel. See <see cref="UnitWallMotion"/> for why this exists.
        /// </summary>
        /// <remarks>
        /// <para><b>Blocked means stopped, not bounced.</b> AS3 answers a wall with
        /// <c>dx = Math.abs(dx) * this.elast</c> (<c>Unit.as:2144</c> / <c>:2221</c>), and
        /// <c>elast</c> is <c>0</c> for every shipped unit — <c>Unit.as:242</c> initialises it to zero
        /// and no <c>&lt;move&gt;</c> node in <c>AllData.as</c> authors one. So the response is a dead
        /// stop, which is exactly "cancel the velocity". Only <c>dx</c> is touched: AS3's wall branch
        /// never writes <c>dy</c>, so a unit sliding down a wall keeps falling.</para>
        ///
        /// <para>Three no-op paths, all of them "there is nothing to ask": no room (a unit spawned
        /// outside one), no collider, and no horizontal motion at all — the last is worth the branch
        /// because it is the common case for a unit that is standing still or only falling.</para>
        /// </remarks>
        protected float ResolveHorizontalMotion(float deltaXUnits)
        {
            if (_tileQuery == null || _collider == null || deltaXUnits == 0f)
            {
                return deltaXUnits;
            }

            TileBox boxPx = UnitWallMotion.ToMoveBoxPixels(
                _collider.bounds, TileQueryConstants.PixelToUnit);

            UnitWallResolution resolution = UnitWallMotion.Resolve(
                _tileQuery, boxPx, deltaXUnits * TileQueryConstants.UnitToPixel);

            if (resolution.Blocked)
            {
                _velocity.x = 0f;
            }

            return resolution.AppliedDeltaXPx * TileQueryConstants.PixelToUnit;
        }

        /// <summary>
        /// Mirror the sprite for <see cref="_facingDirection"/>.
        /// Note: in PFE sprites always face right, so left is the flipped case.
        /// </summary>
        protected void ApplyFacingToTransform()
        {
            if (transform.localScale.x != _facingDirection)
            {
                transform.localScale = new Vector3(_facingDirection, 1, 1);
            }
        }

        /// <summary>
        /// Read this unit's authored placement — AS3's <c>Unit</c> constructor reading its <c>node</c>.
        ///
        /// <para><b>Facing is base behaviour, not a per-controller one.</b> <c>Unit.as:596-611</c>
        /// resolves <c>@turn</c> in the <i>base</i> constructor: positive → right, negative → left, and
        /// <b>absent → a coin flip</b> (<c>storona = this.isrnd() ? 1 : -1</c>). <c>UnitTrain.as:16-30</c>
        /// repeats all three cases, which is exactly why an earlier plan put this in the dummy — the
        /// subclass duplicates the base rather than owning the rule.</para>
        ///
        /// <para><b>The value is resolved once, at population time.</b> The coin flip needs the spawn
        /// stream, which belongs to <c>RoomPopulator</c> — the layer that generated the room — not to the
        /// presenter that draws it. So the facing arrives on <see cref="UnitInstance.facingDirection"/>
        /// and this method only applies it. See <see cref="ResolveFacing"/> for the rule.</para>
        /// </summary>
        public virtual void ApplyPlacement(UnitInstance placement)
        {
            if (placement == null)
            {
                return;
            }

            _facingDirection = placement.facingDirection;
            ApplyFacingToTransform();
        }

        /// <summary>
        /// AS3's <c>@turn</c> resolution (<c>Unit.as:596-611</c>), as a pure function so the only
        /// varying input is the coin flip the caller supplies.
        ///
        /// <para>A <b>present but non-positive</b> <c>turn</c> is not the same as an absent one: AS3
        /// only tests <c>&gt; 0</c> and <c>&lt; 0</c>, so <c>turn="0"</c> leaves <c>storona</c> at
        /// whatever it was (<c>Obj.as:24</c> initialises it to 1) and never reaches the coin flip — the
        /// flip lives in the attribute's <c>else</c> branch. Collapsing those two cases would make a
        /// <c>turn="0"</c> unit face a random direction.</para>
        ///
        /// <para><b>Parsed as a float, not an int, because AS3 compares against a coerced Number.</b>
        /// <c>@turn &gt; 0</c> on <c>"1.5"</c> is true in AS3, so an integer parse would send a
        /// fractional turn down the fall-through path instead of the positive one. Non-numeric values
        /// coerce to <c>NaN</c>, whose comparisons are both false — the same fall-through as
        /// <c>"0"</c>, which <c>float.TryParse</c> reproduces exactly.</para>
        /// </summary>
        public static int ResolveFacing(string turn, int currentFacing, PFE.Core.Rng.IRngService rng)
        {
            if (!string.IsNullOrEmpty(turn))
            {
                if (float.TryParse(turn, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    if (value > 0f) return 1;
                    if (value < 0f) return -1;
                }

                return currentFacing;
            }

            if (rng == null)
            {
                return currentFacing;
            }

            return rng.Chance(0.5f) ? 1 : -1;
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
        /// Ground detection using collision normals — the <b>fallback</b> path, used only when this
        /// unit has no room and therefore no tile query (see <see cref="SetTileQuery"/>).
        /// Replaces the tile-based ground checking from AS3.
        /// </summary>
        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (HasUpwardContact(collision))
            {
                _supportingColliders.Add(collision.collider);
                _isGrounded = true;
                _velocity.y = 0; // Stop falling
            }
        }

        private void OnCollisionStay2D(Collision2D collision)
        {
            if (HasUpwardContact(collision))
            {
                _supportingColliders.Add(collision.collider);
                _isGrounded = true;
                _velocity.y = Mathf.Min(_velocity.y, 0); // Don't fall through floor
            }
            else
            {
                // A side contact: it never made this unit grounded, so it must not keep it that way.
                _supportingColliders.Remove(collision.collider);
            }
        }

        /// <summary>
        /// Clear groundedness only when the collider that left was one of the supports.
        ///
        /// <para><b>This was unconditional, and that was a real defect.</b> Any collider leaving
        /// cleared the flag — including the player, who only interpenetrates a unit because both
        /// Rigidbody2D bodies are Kinematic (<c>m_BodyType: 1</c>) and neither can push the other. So
        /// brushing past a unit cancelled its groundedness, gravity started, and because a Kinematic
        /// body's <c>MovePosition</c> is not blocked by static geometry the unit then sank out of the
        /// room. The user's report — "I pass through them, they start to fall down" — is this line.</para>
        ///
        /// <para>A set rather than a bool, because a unit can rest on more than one collider and only
        /// the last support's exit may unground it. Entries are dropped by
        /// <see cref="OnCollisionStay2D"/> when a contact stops being upward, so a support that
        /// silently disappears cannot strand a stale entry.</para>
        /// </summary>
        private void OnCollisionExit2D(Collision2D collision)
        {
            _supportingColliders.Remove(collision.collider);

            if (_supportingColliders.Count == 0)
            {
                _isGrounded = false;
            }
        }

        /// <summary>
        /// Whether any contact in this collision has a normal pointing up — the port's stand-in for
        /// AS3's <c>isLaz</c> on the no-tile-query path.
        /// </summary>
        private static bool HasUpwardContact(Collision2D collision)
        {
            foreach (ContactPoint2D contact in collision.contacts)
            {
                if (contact.normal.y > 0.7f)
                {
                    return true;
                }
            }

            return false;
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
        /// Apply an already-resolved outcome: armour integrity first, then health.
        /// Base implementation delegates to <see cref="UnitStats"/>; subclasses can override.
        /// </summary>
        /// <returns><c>true</c> if this hit broke the armour.</returns>
        public virtual bool ApplyDamage(in DamageOutcome outcome)
        {
            if (_unitStats == null)
            {
                Debug.LogWarning($"[{GetType().Name}] ApplyDamage called but no UnitStats assigned!");
                return false;
            }

            bool broke = _unitStats.ApplyDamage(outcome);

            // Handle death if applicable
            if (!IsAlive)
            {
                OnDeath();
            }

            return broke;
        }

        /// <summary>
        /// The unit's armour projection, read by the damage resolver.
        /// <see cref="ArmourState.None"/> when unarmoured or when no stats are assigned.
        /// </summary>
        public virtual ArmourState Armour => _unitStats?.armour ?? ArmourState.None;

        /// <summary>
        /// The unit's vulnerability table, read by the damage resolver.
        /// <see cref="VulnerabilityData.Neutral"/> when nothing is assigned.
        /// </summary>
        /// <remarks>
        /// <para><b>The baseline comes from the definition; the live table comes from
        /// <see cref="UnitStats"/>.</b> That is AS3's own split, and an earlier revision of this comment
        /// collapsed it by claiming the table "belongs where the importer put it" because nothing writes
        /// it. <c>begvulner</c> is a unit's static <c>&lt;vulner&gt;</c> element
        /// (<c>Unit.as:977-984</c>); <c>vulner</c> is <i>derived</i> from it — and for the player the
        /// derivation is not the identity, because <c>Pers.armorParameters():2067</c> folds the equipped
        /// armour's <c>resist</c> in. A unit with no <see cref="UnitStats"/> has no derivation to run, so
        /// its baseline <i>is</i> its live table; that is why the fallback is the definition itself and
        /// not an error.</para>
        ///
        /// <para><b>Neutral, not identity, when there is no definition</b> — and that is the value, not a
        /// placeholder. AS3's baseline for a unit with no element is <c>1</c> everywhere except
        /// <c>emp = 0</c> (<c>Unit.as:583-590</c>), and the player has no <c>&lt;unit&gt;</c> in
        /// <c>AllData.as</c> at all, so neutral is exactly right for the player.</para>
        ///
        /// <para><b>What is live today.</b> <c>_stats</c> is a serialized field that <b>no code
        /// assigns</b>, so unless a prefab carries a reference an NPC reads
        /// <see cref="VulnerabilityData.Neutral"/> — correct-but-empty. The imported tables become live
        /// the moment a spawner (or a prefab) assigns a definition, with no change needed here.</para>
        /// </remarks>
        public virtual VulnerabilityData Vulnerabilities
        {
            get
            {
                if (_unitStats != null)
                    return _unitStats.Vulnerabilities;

                return _stats != null ? _stats.vulnerabilities : VulnerabilityData.Neutral;
            }
        }

        /// <summary>
        /// The unit's natural resistance, read by the damage resolver — AS3 <c>Unit.skin</c>.
        /// <c>0</c> when no stats are assigned, which is AS3's own default.
        /// </summary>
        /// <remarks>
        /// <para><b>Read from <see cref="UnitStats"/> and not from the definition, deliberately.</b>
        /// <c>skin</c> is not a constant: <c>UnitTrain</c> raises it for the armoured variant
        /// (<c>UnitTrain.as:41-45</c>, <c>skin = 20</c>) and <c>Unit.setLevel()</c> scales it by level
        /// (<c>Unit.as:1611</c>, <c>this.skin *= 1 + this.level * 0.05</c>). So the definition's value is
        /// the <i>baseline</i> and the live value belongs on the mutable stats object — the same split
        /// as <see cref="Armour"/> and <see cref="Vulnerabilities"/>.</para>
        ///
        /// <para><b>Nothing was reading this, which is why the armoured dummy looked unarmoured.</b>
        /// <see cref="TrainingDummyController"/> has written <c>skinResistance = 20</c> for the
        /// <c>tr='1'</c> variant since the unit slice landed, and <c>DamageCalculator</c> has applied a
        /// <c>skinResistance</c> argument for longer than that — but the only production caller passed a
        /// literal <c>0f</c>. Exposing it here is what lets <c>DamageSystem</c> read it instead.</para>
        /// </remarks>
        public virtual float SkinResistance => _unitStats?.skinResistance ?? 0f;

        /// <summary>
        /// AS3 <c>Unit.knocked</c> — this unit's susceptibility to being thrown. Authored on the
        /// definition's <c>&lt;move&gt;</c> node; AS3's own default is <c>1</c>.
        /// </summary>
        /// <remarks>
        /// <b>From the definition, not <see cref="UnitStats"/>,</b> unlike <see cref="Armour"/> and
        /// <see cref="SkinResistance"/> — nothing scales it at runtime in AS3, so there is no live copy
        /// to prefer. A unit with no definition answers <c>1</c> rather than <c>0</c>, because <c>0</c>
        /// is not "no data" here: it is the authored "cannot be moved" flag that turrets, <c>fixed</c>
        /// units and <c>UnitBossNecr</c>'s shadow all carry.
        /// </remarks>
        public virtual float Knocked => _stats != null ? _stats.knocked : 1f;

        /// <summary>
        /// AS3 <c>Unit.massa</c> — the weight divisor, already divided by 50 as AS3 does. See
        /// <see cref="UnitDefinition.Massa"/> for why the raw attribute is not this number.
        /// </summary>
        /// <remarks>
        /// Falls back to AS3's field default of <c>1</c> when no definition is assigned — the same value
        /// <see cref="KnockbackMath"/> substitutes for a non-positive mass — so a unit the spawner has
        /// not initialised yet is knocked back normally rather than not at all.
        /// </remarks>
        public virtual float Mass => _stats != null ? _stats.Massa : 1f;

        /// <summary>
        /// AS3 <c>Unit.invulner</c>, from the definition's authored flag.
        /// </summary>
        /// <remarks>
        /// <b>The runtime toggles are not modelled.</b> AS3 raises this on <c>UnitBossNecr</c> for the
        /// duration of its shadow phase and clears it after; the port has the authored
        /// <c>isInvulnerable</c> and nothing that changes it, so such a unit is either always
        /// invulnerable or never. Recorded rather than quietly approximated, because the knockback gate
        /// reads it and because it is also one half of AS3's other bullet pass-through branch.
        /// </remarks>
        public virtual bool IsInvulnerable => _stats != null && _stats.isInvulnerable;

        /// <summary>
        /// AS3 <c>Unit.fixed</c> — this unit is pinned in place and its position integration is
        /// skipped entirely (<c>Unit.as:1809</c>).
        /// </summary>
        /// <remarks>
        /// <b>What a fixed unit still does.</b> Only the <c>run()</c> call is gated. <c>forces()</c>
        /// and <c>control()</c> run above the gate, so velocity still accumulates and facing still
        /// updates; <c>checkWater()</c>, <c>actions()</c>, <c>setVisPos()</c> and <c>animate()</c> run
        /// below it. The visible result is a statue that can still aim, still take damage, and still
        /// be shot at — it just never changes position. See <see cref="UnitDefinition.isFixed"/> for
        /// the two knockback consequences and the box-wall gate at <c>:4223</c>.
        ///
        /// <para><b>From the definition, and deliberately <c>virtual</c>.</b> AS3's <c>fixed</c> is a
        /// runtime-mutable field that eleven unit subclasses flip — <c>UnitTurret.as:462</c> and
        /// <c>UnitZombie.as:455</c> clear it, <c>Unit.as:3130</c> clears it on a unit that has
        /// levitated for 75 ticks. None of those subclasses is ported, so there is no runtime writer
        /// to model today and adding a mutable backing field would be dead code. <c>virtual</c> is the
        /// hook: a ported <c>UnitTurretController</c> overrides this with its own flag rather than
        /// making the definition mutable. Same shape as <see cref="Knocked"/> and <see cref="Mass"/>,
        /// which read the definition for the same reason.</para>
        /// </remarks>
        public virtual bool IsFixed => _stats != null && _stats.isFixed;

        /// <summary>
        /// Adds a knockback impulse to this unit's velocity — AS3's <c>dx += …; dy += …</c>.
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="AddForce"/>, which is the port of AS3's <c>Unit.forces()</c> and has
        /// carried a "explosions, knockback, etc." doc comment since long before anything called it —
        /// the producer for this existed and had no consumer until now.
        /// </remarks>
        public virtual void ApplyKnockback(Vector2 impulse) => AddForce(impulse);

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
