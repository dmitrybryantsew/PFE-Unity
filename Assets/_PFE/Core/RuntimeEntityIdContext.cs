using System.Collections.Generic;
using System.Runtime.CompilerServices;
using PFE.Core.Ids;

namespace PFE.Core
{
    /// <summary>
    /// Phase 3.2 (F-05): the ambient carrier for host-assigned ids and live-entity registration.
    ///
    /// <para><b>Why ambient and not injected.</b> The consumers are <c>AddComponent</c>-built
    /// (<c>RoomUnitSpawner</c>, <c>RoomObjectVisualManager</c>), static spawners
    /// (<c>FlyerSpawner</c>, <c>PetSpawner</c>, <c>BossSummonSpawner</c>, …) and the plain-C#
    /// <c>SimLoop</c>. VContainer observes none of the first two and cannot constructor-inject a static
    /// class at all. This is the same push-at-build shape as <c>SinglePlayerSlotContext.Current</c>,
    /// <c>SoundBus.Current</c> and <c>SimAuthorityContext</c>: <c>GameLifetimeScope</c> pushes the
    /// real singletons once, consumers read them back.</para>
    ///
    /// <para><b>Defaults are usable, not null.</b> Offline fixtures, editor tooling and a headless room
    /// build have no container. A fresh <see cref="EntityRegistry"/> and allocator keep those paths
    /// working and, unlike a null-tolerant no-op, keep registration <i>observable</i> through
    /// <see cref="RegisteredCount"/>.</para>
    ///
    /// <para><b>Identity lives on the record, not the controller.</b> <c>RefreshAll()</c> destroys and
    /// rebuilds the view on every room entry, so anything keyed on a <c>UnitController</c> is stale
    /// after a transition. This context keys its tracking on the <i>record object reference</i>, which
    /// is the object that owns persistence per <c>RoomStateRules</c>.</para>
    ///
    /// <para><b>A resolved id outlives a registration.</b> Room teardown unregisters a record and
    /// re-entry registers the very same record object again. If unregistering threw away the derived
    /// id, the entity would come back under a different id after a transition — the exact failure
    /// <c>SimUnitState.EntityHash</c> documents. <see cref="ConditionalWeakTable{TKey,TValue}"/> keeps
    /// the answer for as long as the record is alive without pinning it in memory. A despawn/respawn
    /// <i>lifetime</i> distinction is Phase 3.3's <c>generation</c>, not a new id.</para>
    ///
    /// <para><b>Empty ids are resolved here, not by rewriting the record.</b>
    /// <c>UnitInstance.entityId</c> / <c>ObjectInstance.entityId</c> are <c>string</c> save-DTO keys:
    /// <c>RoomStateSnapshot.cs:200/389/395</c> gate override matching on
    /// <c>!string.IsNullOrEmpty(entityId)</c>. Filling those strings at creation would silently change
    /// which objects get persistent overrides, and a <c>runtime:</c> id is not session-stable enough
    /// to save. The replication layer still gets a valid, stable id for every live entity.</para>
    /// </summary>
    public static class RuntimeEntityIdContext
    {
        private static IEntityRegistry _registry = new EntityRegistry();
        private static RuntimeEntityIdAllocator _allocator = new RuntimeEntityIdAllocator();
        private static long _currentTickIndex = RuntimeEntityIdRules.NoActiveTick;

        // Weak keys: a destroyed room's records must not be kept alive by the id cache.
        // ConditionalWeakTable needs a reference TValue, so the struct is boxed once per record.
        private static ConditionalWeakTable<object, IdBox> _resolved =
            new ConditionalWeakTable<object, IdBox>();

        private static readonly HashSet<object> _registered = new HashSet<object>(ReferenceComparer.Instance);

        /// <summary>
        /// Id hash → the lifetime the id's previous occupant left behind. Phase 3.3's
        /// <c>generation</c> needs to survive an unregistration, because that is exactly the moment a
        /// new record can take the id over — the weak <see cref="_resolved"/> table cannot answer it
        /// (a destroyed record's box is gone, and a *different* record has a different box).
        ///
        /// <para><b>Bounded on purpose: runtime ids are never stored here.</b> A runtime id is unique
        /// per <c>(tick, counter)</c> for the whole run, so it can never be recycled and its generation
        /// is always <see cref="RuntimeEntityIdRules.FirstGeneration"/>. Storing one entry per fired
        /// projectile would make this dictionary grow with every shot in the session for no answer it
        /// could not already give. Only stable ids (<c>room:…</c>, <c>player:N</c>) — whose count is
        /// bounded by the map and the slot list — are tracked.</para>
        /// </summary>
        private static Dictionary<ulong, LifetimeRecord> _lifetimes =
            new Dictionary<ulong, LifetimeRecord>();

        private static long _derivedIdCount;
        private static long _collisionCount;
        private static long _recycledIdCount;

        /// <summary>The registry snapshots and ownership queries will read. Never null.</summary>
        public static IEntityRegistry Registry => _registry;

        /// <summary>The run's host-side id allocator. Never null.</summary>
        public static RuntimeEntityIdAllocator Allocator => _allocator;

        /// <summary>The last host tick observed. Monotonic; <c>NoActiveTick</c> before the first tick.</summary>
        public static long CurrentTickIndex => _currentTickIndex;

        /// <summary>How many records are currently registered as live.</summary>
        public static int TrackedIdCount => _registered.Count;

        /// <summary>How many entities the configured registry currently holds.</summary>
        public static int RegisteredCount => _registry.Count;

        /// <summary>How many ids had to be derived because a record carried an empty id (F-05's gap).</summary>
        public static long DerivedIdCount => _derivedIdCount;

        /// <summary>
        /// How many registrations were refused because a <i>different</i> record already claimed the id.
        /// A non-zero value is a bug, not a retryable condition.
        /// </summary>
        public static long CollisionCount => _collisionCount;

        /// <summary>
        /// How many times a stable id was taken over by a different record — each one a generation
        /// bump. The readback for "the lifetime machinery is actually reachable": a session that
        /// leaves and re-enters a room must show a non-zero count, otherwise <c>generation</c> is
        /// pinned at 0 and the stale-lifetime guard is guarding nothing.
        /// </summary>
        public static long RecycledIdCount => _recycledIdCount;

        /// <summary>
        /// Push the DI singletons at container build. Resets the run so a scene reload starts from
        /// counter 0 rather than inheriting a process-lifetime serial.
        /// </summary>
        public static void Configure(IEntityRegistry registry, RuntimeEntityIdAllocator allocator)
        {
            _registry = registry ?? new EntityRegistry();
            _allocator = allocator ?? new RuntimeEntityIdAllocator();
            _allocator.Reset();
            ResetBookkeeping();
        }

        /// <summary>Restore the containerless defaults. Bootstrap/test hook; called by every fixture's TearDown.</summary>
        public static void Reset()
        {
            _registry = new EntityRegistry();
            _allocator = new RuntimeEntityIdAllocator();
            ResetBookkeeping();
        }

        private static void ResetBookkeeping()
        {
            _currentTickIndex = RuntimeEntityIdRules.NoActiveTick;
            _resolved = new ConditionalWeakTable<object, IdBox>();
            _lifetimes = new Dictionary<ulong, LifetimeRecord>();
            _registered.Clear();
            _derivedIdCount = 0;
            _collisionCount = 0;
            _recycledIdCount = 0;
        }

        /// <summary>
        /// Called once per <c>SimLoop.StepOnce()</c>, before any tickable runs. Takes the maximum, so a
        /// second loop in the same process cannot rewind the host tick (see
        /// <see cref="RuntimeEntityIdRules.AdvanceTick"/>).
        /// </summary>
        public static void AdvanceTick(long observedTickIndex)
        {
            _currentTickIndex = RuntimeEntityIdRules.AdvanceTick(_currentTickIndex, observedTickIndex);
        }

        /// <summary>Mint the next host-assigned id at the current tick. The one entry point for runtime spawners.</summary>
        public static EntityId AllocateRuntimeId()
        {
            return _allocator.Allocate(_currentTickIndex);
        }

        /// <summary>
        /// Register a live record at spawn. Returns false for a null record, an invalid id, or a
        /// collision; never throws into the spawn path. Idempotent for an already-live record.
        /// </summary>
        public static bool TryRegister(object entity, string rawEntityId)
        {
            if (entity == null) return false;

            // Idempotent fast path. RoomObjectVisualManager.SyncPresenters walks every object on
            // every frame; without this, registration would be a dictionary write per object per
            // frame. A record is registered for its whole lifetime, so a second call has no work.
            if (_registered.Contains(entity)) return true;

            EntityId id;
            if (_resolved.TryGetValue(entity, out IdBox box))
            {
                // Room re-entry: the same record object comes back under its previous id.
                id = box.Value;
            }
            else if (!string.IsNullOrEmpty(rawEntityId))
            {
                id = EntityId.FromString(rawEntityId);
            }
            else
            {
                id = AllocateRuntimeId();
                _derivedIdCount++;
            }

            if (!id.IsValid) return false;

            return RegisterCore(entity, id, box);
        }

        /// <summary>
        /// Register a live record whose id was already minted — the path every runtime spawner uses
        /// after <see cref="AllocateRuntimeId"/>. Kept as an overload rather than a string round-trip
        /// so a freshly minted id is never hashed twice and never depends on its debug string
        /// re-parsing to the same value.
        /// </summary>
        public static bool TryRegister(object entity, EntityId id)
        {
            if (entity == null) return false;

            // Same idempotence rule as the string overload: a live record is registered for its whole
            // lifetime, and RefreshAll re-registers on every room entry.
            if (_registered.Contains(entity)) return true;

            _resolved.TryGetValue(entity, out IdBox box);
            return RegisterCore(entity, id, box);
        }

        /// <summary>
        /// The one registration tail: collision check, lifetime assignment, weak-box update and the
        /// registry write. Both public overloads funnel here so a lifetime cannot be assigned by one
        /// path and skipped by the other.
        /// </summary>
        private static bool RegisterCore(object entity, EntityId id, IdBox box)
        {
            if (!id.IsValid) return false;

            if (_registry.TryGetEntity<object>(id, out object existing)
                && !RuntimeEntityIdRules.ShouldReplace(existing, entity))
            {
                _collisionCount++;
                return false;
            }

            // Phase 3.3: which lifetime is this registration?
            //
            // `sameOwner` is exactly "this record object has held this id before" — the weak box only
            // exists for a record that registered at least once. That is a room re-entry of the same
            // entity: continue the counter, keep the generation.
            //
            // A record with no box claiming a *stable* id that some other record left behind is a new
            // occupant: bump the generation. Runtime ids can never reach that branch (they are unique
            // per run), which is why `_lifetimes` never has to hold one.
            bool sameOwner = box != null;
            bool hasPrevious = sameOwner;
            int previousGeneration = 0;
            int previousLifecycleSequence = 0;

            LifetimeRecord record = null;

            if (sameOwner)
            {
                previousGeneration = box.Generation;
                previousLifecycleSequence = box.LifecycleSequence;
            }
            else if (!RuntimeEntityIdRules.IsRuntimeId(id)
                     && _lifetimes.TryGetValue(id.Hash, out record))
            {
                hasPrevious = true;
                previousGeneration = record.Generation;
                previousLifecycleSequence = record.LifecycleSequence;
            }

            EntityLifetime lifetime = RuntimeEntityIdRules.NextLifetime(
                hasPrevious, previousGeneration, previousLifecycleSequence, sameOwner);

            if (hasPrevious && !sameOwner)
            {
                // A different record took a stable id over. Counted so a session that never leaves a
                // room cannot silently look like one where the generation is working.
                _recycledIdCount++;
            }

            if (box == null)
            {
                box = new IdBox(id);
                _resolved.Add(entity, box);
            }

            box.Generation = lifetime.Generation;
            box.LifecycleSequence = lifetime.LifecycleSequence;

            if (!RuntimeEntityIdRules.IsRuntimeId(id))
            {
                if (record == null)
                {
                    record = new LifetimeRecord();
                    _lifetimes[id.Hash] = record;
                }

                record.Generation = lifetime.Generation;
                record.LifecycleSequence = lifetime.LifecycleSequence;
            }

            _registry.Register(id, entity);
            _registered.Add(entity);
            return true;
        }

        /// <summary>
        /// Unregister a live record at despawn. Idempotent. The resolved id is <b>kept</b> (weakly) so
        /// a re-registered record does not change identity.
        ///
        /// <para><b>A despawn is a lifecycle event.</b> The counter advances here as well as at
        /// registration, so the sequence a despawn record carries is strictly newer than the spawn it
        /// ends — otherwise <c>SimStalenessRules.AcceptLifetime</c> would call the despawn a duplicate
        /// of the spawn and drop it.</para>
        /// </summary>
        public static bool Unregister(object entity)
        {
            if (entity == null) return false;
            if (!_registered.Remove(entity)) return false;

            if (_resolved.TryGetValue(entity, out IdBox box))
            {
                box.LifecycleSequence = RuntimeEntityIdRules.NextLifecycleSequence(box.LifecycleSequence);

                if (!RuntimeEntityIdRules.IsRuntimeId(box.Value)
                    && _lifetimes.TryGetValue(box.Value.Hash, out LifetimeRecord record))
                {
                    record.LifecycleSequence = box.LifecycleSequence;
                }

                return _registry.Unregister(box.Value);
            }

            return false;
        }

        /// <summary>The id resolved for a record, or false when none was ever assigned.</summary>
        public static bool TryGetId(object entity, out EntityId id)
        {
            if (entity != null && _resolved.TryGetValue(entity, out IdBox box))
            {
                id = box.Value;
                return true;
            }

            id = EntityId.Empty;
            return false;
        }

        /// <summary>
        /// The lifetime of a record's id — the pair a snapshot projector stamps onto
        /// <c>SimUnitState.Generation</c>/<c>LifecycleSequence</c> and <c>SimProjectileState.Generation</c>.
        /// False when the record was never registered; the out values are then a first lifetime rather
        /// than garbage, so a caller that ignores the return value still stamps something coherent.
        /// </summary>
        public static bool TryGetLifetime(object entity, out int generation, out int lifecycleSequence)
        {
            if (entity != null && _resolved.TryGetValue(entity, out IdBox box))
            {
                generation = box.Generation;
                lifecycleSequence = box.LifecycleSequence;
                return true;
            }

            generation = RuntimeEntityIdRules.FirstGeneration;
            lifecycleSequence = RuntimeEntityIdRules.FirstLifecycleSequence;
            return false;
        }

        /// <summary>
        /// The one call a spawner needs: the record's id and the lifetime it belongs to. Split from
        /// <see cref="TryGetId"/> + <see cref="TryGetLifetime"/> because every caller that stamps a
        /// controller needs both, and two independent lookups make it possible to stamp one and forget
        /// the other — which reads as a working id with a permanently zero generation.
        /// </summary>
        public static bool TryDescribe(object entity, out EntityId id, out EntityLifetime lifetime)
        {
            if (entity != null && _resolved.TryGetValue(entity, out IdBox box))
            {
                id = box.Value;
                lifetime = new EntityLifetime(box.Generation, box.LifecycleSequence);
                return true;
            }

            id = EntityId.Empty;
            lifetime = new EntityLifetime(
                RuntimeEntityIdRules.FirstGeneration,
                RuntimeEntityIdRules.FirstLifecycleSequence);
            return false;
        }

        /// <summary>
        /// Give a <b>pooled</b> object a brand-new id — the one path where the id deliberately changes
        /// for the same object reference.
        ///
        /// <para><b>Why <see cref="TryRegister(object,EntityId)"/> is not enough.</b> Registration is
        /// idempotent per object, and the weak box caches the id, so a second <c>TryRegister</c> on a
        /// recycled <c>Projectile</c> would answer "already registered" and leave it firing under the
        /// first shot's id forever. Dropping the box is what makes the new id take effect; it costs
        /// nothing for a runtime id, whose generation is always the first one because it is never
        /// recycled.</para>
        /// </summary>
        public static bool TryReassign(object entity, EntityId id)
        {
            if (entity == null || !id.IsValid) return false;

            Unregister(entity);
            _registered.Remove(entity);
            _resolved.Remove(entity);

            return TryRegister(entity, id);
        }

        private sealed class IdBox
        {
            public readonly EntityId Value;

            /// <summary>Phase 3.3 lifetime, written by <see cref="RegisterCore"/> and <see cref="Unregister"/>.</summary>
            public int Generation;
            public int LifecycleSequence;

            public IdBox(EntityId value)
            {
                Value = value;
                Generation = RuntimeEntityIdRules.FirstGeneration;
                LifecycleSequence = RuntimeEntityIdRules.FirstLifecycleSequence;
            }
        }

        /// <summary>The lifetime a stable id's previous occupant left behind, kept across unregistration.</summary>
        private sealed class LifetimeRecord
        {
            public int Generation;
            public int LifecycleSequence;
        }

        /// <summary>
        /// Identity, not value: two distinct records with equal field values must not share an entry,
        /// and a record whose fields mutate must stay findable.
        /// </summary>
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);

            int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
