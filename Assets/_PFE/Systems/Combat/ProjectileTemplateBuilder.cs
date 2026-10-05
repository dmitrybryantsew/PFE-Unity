using UnityEngine;
using PFE.Entities.Weapons;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Builds the projectile template GameObject in code, replacing
    /// <c>Assets/_PFE/Prefabs/projectile.prefab</c> and its <c>ProjectilePrefabRegistry</c>.
    ///
    /// <para><b>Why this is a small change, not a rewrite.</b> The prefab was already a five-part shell
    /// and the registry mapped <b>all nine archetypes to one guid</b>, so <c>weapon.projectileArchetype</c>
    /// selected nothing. Everything that actually varies per weapon — the sprite, tint, offset, scale and
    /// sorting order — was already applied in code by <c>Projectile.ApplyVisual</c>. Building the shell
    /// here changes no behaviour; it removes two assets that could only be wrong.</para>
    ///
    /// <para>The sibling of this in the codebase is <c>ThrownObject.EnsureVisualRenderer</c>, which
    /// already creates its child <c>GameObject</c> and <c>SpriteRenderer</c> on demand. This is the same
    /// move for the round.</para>
    ///
    /// <para>Every value comes from <see cref="ProjectileTemplateSpec"/>, which is plain data so the
    /// offline wall can pin it — <c>AddComponent</c> below is an <c>ECall</c> and cannot be executed
    /// offline (lesson #43).</para>
    /// </summary>
    public static class ProjectileTemplateBuilder
    {
        /// <summary>
        /// Creates the inactive template. The caller owns it and must keep it alive for as long as the
        /// pool that instantiates from it.
        /// </summary>
        /// <param name="parent">Optional parent. The caller passes the pool root so a teardown takes the
        /// template with it; the template is not a pooled instance and is never handed out.</param>
        public static GameObject Build(Transform parent = null)
        {
            var root = new GameObject(ProjectileTemplateSpec.RootName);
            if (parent != null)
                root.transform.SetParent(parent, false);

            root.transform.position = new Vector3(
                ProjectileTemplateSpec.ParkingPosition.x,
                ProjectileTemplateSpec.ParkingPosition.y,
                0f);

            // Rigidbody2D first: Projectile is decorated [RequireComponent(typeof(Rigidbody2D))], and
            // adding the script before the body would make Unity add a second, unconfigured one.
            var body = root.AddComponent<Rigidbody2D>();
            body.bodyType               = RigidbodyType2D.Kinematic;
            body.mass                   = ProjectileTemplateSpec.Mass;
            body.linearDamping          = ProjectileTemplateSpec.LinearDamping;
            body.angularDamping         = ProjectileTemplateSpec.AngularDamping;
            body.gravityScale           = ProjectileTemplateSpec.GravityScale;
            body.interpolation          = RigidbodyInterpolation2D.None;
            body.sleepMode              = RigidbodySleepMode2D.StartAwake;
            body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            body.simulated              = true;

            // The needle hitbox. `Horizontal` is the axis the 0.93 unit length runs along, so the shape
            // is a 93 px sliver pointing down local +X — the direction the round travels.
            var hitbox = root.AddComponent<CapsuleCollider2D>();
            hitbox.isTrigger = true;
            hitbox.size      = ProjectileTemplateSpec.ColliderSize;
            hitbox.offset    = ProjectileTemplateSpec.ColliderOffset;
            hitbox.direction = CapsuleDirection2D.Horizontal;

            var visualObject = new GameObject(ProjectileTemplateSpec.VisualChildName);
            visualObject.transform.SetParent(root.transform, false);
            var sprite = visualObject.AddComponent<SpriteRenderer>();
            sprite.sortingLayerName = ProjectileTemplateSpec.SortingLayerName;
            sprite.sortingOrder     = ProjectileTemplateSpec.SortingOrder;

            // Attached LAST, and while the root is still active, so Projectile.Awake runs here — its
            // GetComponentInChildren<SpriteRenderer>() then finds the child above and Projectile._visualRenderer
            // is bound before the first instance exists. Awake on a template is harmless: it only caches
            // components and default visual state, and the template is parked inactive immediately after.
            //
            // Clones are therefore inactive at Instantiate and run their own Awake on the pool's
            // SetActive(true). That is one frame-order change from the prefab path (where Instantiate
            // produced an active clone), and it is safe because Awake touches no injected field, and
            // ApplyVisual re-runs EnsureVisualRenderer on an already-active object — so even a failed
            // lookup self-heals at spawn.
            root.AddComponent<Projectile>();

            root.SetActive(false);
            return root;
        }
    }
}
