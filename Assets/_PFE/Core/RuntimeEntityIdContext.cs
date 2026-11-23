using PFE.Core.Ids;

namespace PFE.Core
{
    /// <summary>
    /// Phase 3.2 (F-05): the ambient carrier for host-assigned ids and live-entity registration —
    /// <b>now a delegating facade</b> over <see cref="RuntimeEntityIdService"/> (Wave 2 thread E).
    ///
    /// <para><b>Why a facade and not injection for every caller.</b> The consumers are
    /// <c>AddComponent</c>-built (<c>RoomUnitSpawner</c>, <c>RoomObjectVisualManager</c>), static
    /// spawners (<c>FlyerSpawner</c>, <c>PetSpawner</c>, <c>BossSummonSpawner</c>, …) and the
    /// plain-C# <c>SimLoop</c> as constructed by its fixtures. VContainer observes none of the first
    /// two and cannot constructor-inject a static class at all. The state itself lives on the
    /// service instance; <c>GameLifetimeScope</c> registers that service as a singleton and
    /// <see cref="Bind"/> points this facade at it, so there is exactly one allocator per run
    /// whether a caller was injected or not — two allocators would mint colliding runtime ids.
    /// Callers that CAN receive the service (constructor-injected replication plumbing, the pushed
    /// <see cref="SimLoop"/> seam) get the instance directly; this facade is the documented seam
    /// for everyone else, the same push-at-build shape as <c>SinglePlayerSlotContext.Current</c>
    /// and <c>SimAuthorityContext</c>.</para>
    ///
    /// <para><b>The default is usable, not null.</b> Offline fixtures, editor tooling and a headless
    /// room build have no container. Until something calls <see cref="Bind"/> or
    /// <see cref="Configure"/>, every static member here delegates to a fresh default service, which
    /// keeps registration <i>observable</i> through <see cref="RegisteredCount"/> instead of being
    /// a null-tolerant no-op.</para>
    /// </summary>
    public static class RuntimeEntityIdContext
    {
        private static RuntimeEntityIdService _active = new RuntimeEntityIdService();

        /// <summary>
        /// The service instance every static member delegates to. Before <see cref="Bind"/> this is
        /// the containerless default; after the lifetime scope's build callback it IS the
        /// container's singleton. Injected consumers that want a guaranteed-same instance resolve
        /// <c>RuntimeEntityIdService</c> from the container; this property is the fallback for
        /// optional-injection seams.
        /// </summary>
        public static RuntimeEntityIdService Active => _active;

        /// <summary>The registry snapshots and ownership queries will read. Never null.</summary>
        public static IEntityRegistry Registry => _active.Registry;

        /// <summary>The run's host-side id allocator. Never null.</summary>
        public static RuntimeEntityIdAllocator Allocator => _active.Allocator;

        /// <summary>The last host tick observed. Monotonic; <c>NoActiveTick</c> before the first tick.</summary>
        public static long CurrentTickIndex => _active.CurrentTickIndex;

        /// <summary>How many records are currently registered as live.</summary>
        public static int TrackedIdCount => _active.TrackedIdCount;

        /// <summary>How many entities the configured registry currently holds.</summary>
        public static int RegisteredCount => _active.RegisteredCount;

        /// <summary>How many ids had to be derived because a record carried an empty id (F-05's gap).</summary>
        public static long DerivedIdCount => _active.DerivedIdCount;

        /// <summary>How many registrations were refused because a different record already claimed the id.</summary>
        public static long CollisionCount => _active.CollisionCount;

        /// <summary>How many times a stable id was taken over by a different record — each one a generation bump.</summary>
        public static long RecycledIdCount => _active.RecycledIdCount;

        /// <summary>
        /// Point the facade at an externally-owned service instance — the lifetime scope's build
        /// callback passes the container's singleton so injected and static-call consumers share one
        /// allocator. A null argument restores the containerless default.
        /// </summary>
        public static void Bind(RuntimeEntityIdService service)
        {
            _active = service ?? new RuntimeEntityIdService();
        }

        /// <summary>
        /// Install a registry/allocator pair on the active instance and reset the run so a scene
        /// reload starts from counter 0 rather than inheriting a process-lifetime serial. Kept as a
        /// facade method (rather than reaching the instance directly) so the wiring lint can keep
        /// pinning the push site by name.
        /// </summary>
        public static void Configure(IEntityRegistry registry, RuntimeEntityIdAllocator allocator)
        {
            _active.Configure(registry, allocator);
        }

        /// <summary>Restore the containerless defaults on the active instance. Bootstrap/test hook; called by every fixture's TearDown.</summary>
        public static void Reset()
        {
            _active.Reset();
        }

        /// <summary>See <see cref="RuntimeEntityIdService.AdvanceTick"/>.</summary>
        public static void AdvanceTick(long observedTickIndex)
        {
            _active.AdvanceTick(observedTickIndex);
        }

        /// <summary>See <see cref="RuntimeEntityIdService.AllocateRuntimeId"/>.</summary>
        public static EntityId AllocateRuntimeId()
        {
            return _active.AllocateRuntimeId();
        }

        /// <summary>See <see cref="RuntimeEntityIdService.TryRegister(object,string)"/>.</summary>
        public static bool TryRegister(object entity, string rawEntityId)
        {
            return _active.TryRegister(entity, rawEntityId);
        }

        /// <summary>See <see cref="RuntimeEntityIdService.TryRegister(object,EntityId)"/>.</summary>
        public static bool TryRegister(object entity, EntityId id)
        {
            return _active.TryRegister(entity, id);
        }

        /// <summary>See <see cref="RuntimeEntityIdService.Unregister"/>.</summary>
        public static bool Unregister(object entity)
        {
            return _active.Unregister(entity);
        }

        /// <summary>See <see cref="RuntimeEntityIdService.TryGetId"/>.</summary>
        public static bool TryGetId(object entity, out EntityId id)
        {
            return _active.TryGetId(entity, out id);
        }

        /// <summary>See <see cref="RuntimeEntityIdService.TryGetLifetime"/>.</summary>
        public static bool TryGetLifetime(object entity, out int generation, out int lifecycleSequence)
        {
            return _active.TryGetLifetime(entity, out generation, out lifecycleSequence);
        }

        /// <summary>See <see cref="RuntimeEntityIdService.TryDescribe"/>.</summary>
        public static bool TryDescribe(object entity, out EntityId id, out EntityLifetime lifetime)
        {
            return _active.TryDescribe(entity, out id, out lifetime);
        }

        /// <summary>See <see cref="RuntimeEntityIdService.TryReassign"/>.</summary>
        public static bool TryReassign(object entity, EntityId id)
        {
            return _active.TryReassign(entity, id);
        }
    }
}
