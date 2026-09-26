using System;
using System.Collections.Generic;

namespace PFE.Core.Ids
{
    /// <summary>
    /// Default implementation of IEntityRegistry using a 64-bit hash indexed dictionary.
    /// Provides O(1) lookup on the simulation hot path.
    /// </summary>
    public sealed class EntityRegistry : IEntityRegistry
    {
        private readonly Dictionary<ulong, (EntityId Id, object Entity)> _entities = new();

        /// <inheritdoc/>
        public void Register(EntityId id, object entity)
        {
            if (!id.IsValid)
                throw new ArgumentException("Cannot register invalid EntityId with Hash=0", nameof(id));
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            _entities[id.Hash] = (id, entity);
        }

        /// <inheritdoc/>
        public bool Unregister(EntityId id)
        {
            return _entities.Remove(id.Hash);
        }

        /// <inheritdoc/>
        public bool TryGetEntity<T>(EntityId id, out T entity)
        {
            if (_entities.TryGetValue(id.Hash, out var entry) && entry.Entity is T typed)
            {
                entity = typed;
                return true;
            }

            entity = default;
            return false;
        }

        /// <inheritdoc/>
        public bool Contains(EntityId id)
        {
            return _entities.ContainsKey(id.Hash);
        }

        /// <inheritdoc/>
        public void Clear()
        {
            _entities.Clear();
        }

        /// <inheritdoc/>
        public int Count => _entities.Count;

        /// <inheritdoc/>
        public IEnumerable<KeyValuePair<EntityId, object>> GetAll()
        {
            foreach (var kvp in _entities)
            {
                yield return new KeyValuePair<EntityId, object>(kvp.Value.Id, kvp.Value.Entity);
            }
        }
    }
}
