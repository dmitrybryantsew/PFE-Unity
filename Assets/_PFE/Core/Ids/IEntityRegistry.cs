using System.Collections.Generic;

namespace PFE.Core.Ids
{
    /// <summary>
    /// Sim-side entity registry mapping stable EntityIds to active simulation objects.
    /// Pure C# interface with zero Unity Object / MonoBehaviour dependencies.
    /// </summary>
    public interface IEntityRegistry
    {
        /// <summary>Registers an entity with its stable EntityId.</summary>
        void Register(EntityId id, object entity);

        /// <summary>Unregisters an entity by its stable EntityId.</summary>
        bool Unregister(EntityId id);

        /// <summary>Attempts to retrieve a registered entity cast to type T.</summary>
        bool TryGetEntity<T>(EntityId id, out T entity);

        /// <summary>Returns true if the given EntityId is currently registered.</summary>
        bool Contains(EntityId id);

        /// <summary>Clears all entity registrations.</summary>
        void Clear();

        /// <summary>Number of currently registered entities.</summary>
        int Count { get; }

        /// <summary>Enumerates all registered entities.</summary>
        IEnumerable<KeyValuePair<EntityId, object>> GetAll();
    }
}
