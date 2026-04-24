using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Describes one discrete shot event emitted by an IWeaponController during a Tick().
    ///
    /// Controllers accumulate ShotPlans into an internal list during Tick() and expose
    /// them via FlushShotPlans(). Consumers (ProjectileSpawner, WeaponPresenter,
    /// MeleeHitVolume) read the list once per FixedUpdate and then it is cleared.
    ///
    /// One ShotPlan per pellet — a shotgun with kol=6 emits 6 ShotPlans in one tick,
    /// each with a different AngleRad and PelletIndex.
    ///
    /// AS3 equivalent: the data assembled in Weapon.shoot() / WClub.shoot() /
    /// WThrow.shoot() before spawning the Bullet / PhisBullet / Mine.
    /// </summary>
    public readonly struct ShotPlan
    {
        // ── Origin ────────────────────────────────────────────────────────────

        /// <summary>Which mount point the shot originates from.</summary>
        public readonly ShotOrigin Origin;

        /// <summary>Exact world-space spawn position for this pellet/projectile.</summary>
        public readonly Vector2 WorldPosition;

        // ── Direction ─────────────────────────────────────────────────────────

        /// <summary>Shot direction in radians (world space). Deviation already applied.</summary>
        public readonly float AngleRad;

        // ── Kind ─────────────────────────────────────────────────────────────

        /// <summary>What runtime object this plan should produce.</summary>
        public readonly ShotKind Kind;

        // ── Damage ────────────────────────────────────────────────────────────

        /// <summary>Full damage payload — carry through to DamageResolver on impact.</summary>
        public readonly DamageContext Damage;

        // ── Spread / burst metadata ───────────────────────────────────────────

        /// <summary>0-based pellet index within a single shot (0..(kol-1)). Single-shot weapons = 0.</summary>
        public readonly int PelletIndex;

        /// <summary>Total pellets in this shot (kol). 1 for single-projectile weapons.</summary>
        public readonly int TotalPellets;

        // ── Projectile physics overrides (baked from weapon def at fire time) ──

        /// <summary>Initial speed in Unity units/sec.</summary>
        public readonly float Speed;

        /// <summary>Gravity multiplier (grav in AS3). 0 = no gravity.</summary>
        public readonly float Gravity;

        /// <summary>Acceleration per second along shot direction (accel in AS3). Rockets.</summary>
        public readonly float Accel;

        /// <summary>Flame type (0 = none, 1 = strong upward arc, 2 = weak arc).</summary>
        public readonly int Flame;

        /// <summary>Homing strength (navod in AS3). 0 = straight shot.</summary>
        public readonly float Navod;

        /// <summary>Spring/visual stretch mode (spring in AS3). 1=vel-scale, 2=laser, 3=multi-frame.</summary>
        public readonly int SpringMode;

        /// <summary>Bullet animates each shot (bulanim in AS3 — vis.play() on the bullet).</summary>
        public readonly bool BulletAnimated;

        // ── Cues ─────────────────────────────────────────────────────────────

        /// <summary>Feedback events that should fire in response to this shot.</summary>
        public readonly ShotCues Cues;

        // ── Melee-specific ────────────────────────────────────────────────────

        /// <summary>
        /// Previous tip world position (for melee sweep BindMove).
        /// Only meaningful when Kind == MeleeSweep.
        /// </summary>
        public readonly Vector2 MeleePrevTip;

        /// <summary>
        /// Current tip world position (for melee sweep BindMove).
        /// Only meaningful when Kind == MeleeSweep.
        /// </summary>
        public readonly Vector2 MeleeCurrTip;

        // ── Thrown-specific ───────────────────────────────────────────────────

        /// <summary>
        /// Initial velocity for physics-based thrown objects (grenade arc).
        /// Only meaningful when Kind == ThrownObject.
        /// </summary>
        public readonly Vector2 ThrowVelocity;

        /// <summary>Fuse time in frames before detonation (detTime in AS3).</summary>
        public readonly int FuseFrames;

        /// <summary>True when this is a mine placement rather than an arc throw.</summary>
        public readonly bool IsMine;

        // ── Constructor ───────────────────────────────────────────────────────

        public ShotPlan(
            ShotOrigin origin,
            Vector2 worldPosition,
            float angleRad,
            ShotKind kind,
            DamageContext damage,
            int pelletIndex,
            int totalPellets,
            float speed,
            float gravity,
            float accel,
            int flame,
            float navod,
            int springMode,
            bool bulletAnimated,
            ShotCues cues,
            Vector2 meleePrevTip  = default,
            Vector2 meleeCurrTip  = default,
            Vector2 throwVelocity = default,
            int fuseFrames        = 75,
            bool isMine           = false)
        {
            Origin         = origin;
            WorldPosition  = worldPosition;
            AngleRad       = angleRad;
            Kind           = kind;
            Damage         = damage;
            PelletIndex    = pelletIndex;
            TotalPellets   = totalPellets;
            Speed          = speed;
            Gravity        = gravity;
            Accel          = accel;
            Flame          = flame;
            Navod          = navod;
            SpringMode     = springMode;
            BulletAnimated = bulletAnimated;
            Cues           = cues;
            MeleePrevTip   = meleePrevTip;
            MeleeCurrTip   = meleeCurrTip;
            ThrowVelocity  = throwVelocity;
            FuseFrames     = fuseFrames;
            IsMine         = isMine;
        }
    }

    // ── Supporting enums ──────────────────────────────────────────────────────

    /// <summary>Which mount point the shot/effect originates from.</summary>
    public enum ShotOrigin
    {
        /// <summary>Normal held weapon — mouth/magic-grip hold point (weaponX/Y in AS3).</summary>
        HoldPoint,

        /// <summary>Muzzle child transform, offset from hold point by vis.emit (getBulXY in AS3).</summary>
        MuzzlePoint,

        /// <summary>Horn tip — magic weapons (magicX/Y in AS3). WMagic only.</summary>
        HornPoint,

        /// <summary>Throw origin — wrist or held position. WThrow only.</summary>
        ThrowPoint,
    }

    /// <summary>What runtime object this ShotPlan should cause to be created.</summary>
    public enum ShotKind
    {
        /// <summary>Standard kinematic projectile (Bullet in AS3).</summary>
        Projectile,

        /// <summary>Instant raycast hit — not yet used but reserved for laser/hitscan weapons.</summary>
        Hitscan,

        /// <summary>Melee strike — MeleeHitVolume sweeps between MeleePrevTip and MeleeCurrTip.</summary>
        MeleeSweep,

        /// <summary>Arcing physics object (grenade, bottle — PhisBullet in AS3).</summary>
        ThrownObject,

        /// <summary>Placed arming object (mine — Mine in AS3). IsMine = true.</summary>
        Mine,
    }

    /// <summary>Feedback cues that should fire alongside this ShotPlan.</summary>
    public readonly struct ShotCues
    {
        public readonly bool PlayShootSound;
        public readonly bool SpawnShellCasing;
        public readonly bool SpawnMuzzleFlash;
        public readonly bool MakeNoise;
        public readonly float NoiseRadius;
        public readonly int ShineRadius;

        public ShotCues(
            bool playShootSound,
            bool spawnShellCasing,
            bool spawnMuzzleFlash,
            bool makeNoise,
            float noiseRadius,
            int shineRadius)
        {
            PlayShootSound   = playShootSound;
            SpawnShellCasing = spawnShellCasing;
            SpawnMuzzleFlash = spawnMuzzleFlash;
            MakeNoise        = makeNoise;
            NoiseRadius      = noiseRadius;
            ShineRadius      = shineRadius;
        }

        public static ShotCues None => new ShotCues(false, false, false, false, 0f, 0);
    }
}
