using UnityEngine;
using PFE.Systems.Map.Rendering;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Every value the shipped <c>Assets/_PFE/Prefabs/projectile.prefab</c> used to carry, as constants.
    ///
    /// <para>The projectile template is now built in code (see
    /// <see cref="ProjectileTemplateBuilder"/>), so both the prefab and its
    /// <c>ProjectilePrefabRegistry</c> are gone. These values were read off the prefab <i>before</i> it
    /// was removed, and they are pinned here so that a silent drift becomes a test failure instead of a
    /// play-test surprise.</para>
    ///
    /// <para><b>Why a separate type.</b> <c>AddComponent</c> is a Unity <c>ECall</c>, so no offline
    /// fixture can execute the builder (lesson #43 — an ECall poisons the JIT of the whole method that
    /// mentions it). The <i>values</i> are plain data, so they live here where the offline wall can see
    /// them; the builder stays thin enough to read at a glance.</para>
    /// </summary>
    public static class ProjectileTemplateSpec
    {
        /// <summary>Root GameObject name — the old prefab's root.</summary>
        public const string RootName = "projectile";

        /// <summary>
        /// Name of the child that carries the sprite — the old prefab's only child. The art is on a
        /// <b>child</b> on purpose: the root transform is the physics body, so the beam stretch and the
        /// visual offset/rotation/scale write the child and never disturb the collider.
        /// </summary>
        public const string VisualChildName = "SpriteImage";

        /// <summary>
        /// Sorting layer for the round's renderer.
        ///
        /// <para>The prefab serialised <c>m_SortingLayerID: -1379265165</c>. Read as unsigned that is
        /// <c>2915702131</c>, which is <b>Foreground</b> in <c>ProjectSettings/TagManager.asset</c>. Its
        /// cached <c>m_SortingLayer: 8</c> was <b>stale</b> — <c>Weapons</c> is index 8 and Foreground is
        /// index 10 — which is exactly the drift a name cannot suffer. Building in code therefore uses
        /// the name, via <see cref="MapSortingLayers"/>, the project's single source for these.</para>
        /// </summary>
        public const string SortingLayerName = MapSortingLayers.Foreground;

        /// <summary>Matches the prefab's <c>m_SortingOrder: 0</c>.</summary>
        public const int SortingOrder = 0;

        // ── Rigidbody2D ──────────────────────────────────────────────────────
        // Prefab: m_BodyType: 1 (Kinematic), m_Simulated: 1, m_Mass: 1, m_LinearDamping: 0,
        // m_AngularDamping: 0.05, m_GravityScale: 1, m_Interpolate: 0 (None),
        // m_SleepingMode: 1 (StartAwake), m_CollisionDetection: 1 (Continuous), m_Constraints: 0.

        /// <summary><c>m_Mass: 1</c>. Inert while the body is kinematic; kept so a dynamic round matches.</summary>
        public const float Mass = 1f;

        /// <summary><c>m_LinearDamping: 0</c>.</summary>
        public const float LinearDamping = 0f;

        /// <summary><c>m_AngularDamping: 0.05</c>.</summary>
        public const float AngularDamping = 0.05f;

        /// <summary>
        /// <c>m_GravityScale: 1</c>. This is the <i>body's</i> scale, not the shot's gravity — the
        /// per-weapon arc is integrated by the simulation from <c>bulletGravity</c>.
        /// </summary>
        public const float GravityScale = 1f;

        // ── CapsuleCollider2D ────────────────────────────────────────────────
        // Prefab: m_Size {x: 0.93, y: 0.06}, m_Offset {x: 0, y: -0.01}, m_Direction: 1 (Horizontal),
        // m_IsTrigger: 1.

        /// <summary>
        /// <c>m_Size {x: 0.93, y: 0.06}</c> — a 93 px needle, not a dot. The Stage C low-level sweep
        /// reproduces this same shape (<c>RoomChainGeometry</c>), so these numbers are load-bearing for
        /// the offline seam as well as for Unity's own contacts. A change here must change that too.
        /// </summary>
        public static readonly Vector2 ColliderSize = new Vector2(0.93f, 0.06f);

        /// <summary><c>m_Offset {x: 0, y: -0.01}</c> — the needle sits a hair below the transform centre.</summary>
        public static readonly Vector2 ColliderOffset = new Vector2(0f, -0.01f);

        // ── Placement ────────────────────────────────────────────────────────

        /// <summary>
        /// <c>m_LocalPosition {x: 10000, y: 10000}</c> — the parking spot. <see cref="PFE.Entities.Weapons.Projectile"/>
        /// parks a released instance here, so an instance that is somehow activated without
        /// <c>PrepareForSpawn</c> appears far from the map rather than at scene origin. Single source:
        /// <c>Projectile.PoolParkingPosition</c> reads this.
        /// </summary>
        public static readonly Vector2 ParkingPosition = new Vector2(10000f, 10000f);
    }
}
