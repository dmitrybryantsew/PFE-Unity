using PFE.Entities.Player;
using UnityEngine;
using EntityId = PFE.Core.Ids.EntityId;

namespace PFE.Core
{
    /// <summary>
    /// The single-player <see cref="IPlayerSlotContext"/>: exactly one slot (index 0), resolving to
    /// the same <c>PlayerController</c> and <c>Camera.main</c> the global lookups used to find.
    ///
    /// <para><b>A seam, not a behaviour change.</b> What is resolved in single-player is identical to
    /// the old code — slot 0 still scans for the one <c>PlayerController</c> and returns
    /// <c>Camera.main</c>. What changes is <i>how a consumer asks</i>: through the context, so a
    /// future <c>MultiplayerSlotContext</c> can answer "slot N is the entity you predict" without
    /// any consumer changing. The lookups stay lazy (resolved per call, not cached at build) for the
    /// reason the <c>GameLifetimeScope</c> <c>Func&lt;&gt;</c> factories document: the player is a
    /// scene object a respawn, room rebuild or scene reload replaces, so resolving once would leave a
    /// consumer holding a destroyed object.</para>
    ///
    /// <para><b>Why a static <see cref="Current"/> sink.</b> The Sim-facing consumers
    /// (<c>UnitController</c>, the enemy brains, <c>SecurityRuntime</c>) are built by
    /// <c>RoomUnitSpawner</c> with <c>AddComponent</c>, which VContainer never observes, so neither
    /// constructor injection nor an <c>[Inject]</c> field can reach them — the same constraint that
    /// put <c>SoundBus.Current</c> and <c>CharacterStats.CombatRng</c> on a push-at-build static in
    /// <c>GameLifetimeScope</c>. The container builds the context and pushes it here once; consumers
    /// read it back. Under multiplayer a different implementation is pushed instead, and no consumer
    /// changes. <see cref="Current"/> falls back to a fresh single-player context when nothing has
    /// been pushed (an offline fixture with no container), which is exactly today's behaviour.</para>
    /// </summary>
    public class SinglePlayerSlotContext : IPlayerSlotContext
    {
        private static IPlayerSlotContext _current;

        /// <summary>
        /// The active slot context. Pushed once by <c>GameLifetimeScope</c> at container build;
        /// defaults to a fresh <see cref="SinglePlayerSlotContext"/> when no push has happened (a
        /// containerless offline fixture). Assign to swap implementations (multiplayer) or to reset
        /// in a test.
        /// </summary>
        public static IPlayerSlotContext Current
        {
            get => _current ??= new SinglePlayerSlotContext();
            set => _current = value;
        }

        /// <inheritdoc/>
        public int LocalSlotCount => 1;

        /// <inheritdoc/>
        public EntityId GetEntityId(int slotIndex)
        {
            return EntityId.CreateForPlayer(slotIndex);
        }

        /// <inheritdoc/>
        public virtual PlayerController GetPlayer(int slotIndex)
        {
            if (slotIndex != 0)
            {
                return null;
            }

            return Object.FindFirstObjectByType<PlayerController>(FindObjectsInactive.Include);
        }

        /// <inheritdoc/>
        public virtual Camera GetCamera(int slotIndex)
        {
            if (slotIndex != 0)
            {
                return null;
            }

            return Camera.main;
        }
    }
}
