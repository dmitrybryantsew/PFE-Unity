using PFE.Entities.Player;
using UnityEngine;
using EntityId = PFE.Core.Ids.EntityId;

namespace PFE.Core
{
    /// <summary>
    /// Resolves the local session's player slots — the seam that replaces the single-player global
    /// lookups (<c>FindFirstObjectByType&lt;PlayerController&gt;()</c>, <c>Camera.main</c>) in
    /// Sim-facing code.
    ///
    /// <para><b>F-09 / Phase 2.2 of the multiplayer readiness plan.</b> Under single-player there is
    /// exactly one slot (index 0); under host-authoritative multiplayer there will be 2–4, one
    /// predicted per client. A consumer that asks "which player is <i>mine</i>" must not scan the
    /// scene for whatever <c>PlayerController</c> happens to exist — on a client that could be the
    /// host's player, or null. This interface makes the answer slot-indexed and injected rather than
    /// global. See <c>docs/multiplayer/Physics_And_Units_Multiplayer_Readiness_2026-11-04.md</c>
    /// §6.9 (F-09) and §8 Phase 2 row 2.2.</para>
    ///
    /// <para><b>Home: <c>PFE.Core</c>, in <c>PFE.View</c>.</b> The consumers it serves
    /// (<c>UnitController</c>, the enemy brains, <c>SecurityRuntime</c>, the
    /// <c>GameLifetimeScope</c> factories) all live in <c>PFE.View</c> and reference
    /// <c>PlayerController</c> directly, so the interface must too. It sits beside
    /// <c>Authority.cs</c> as the identity/authority seam. The slot's <see cref="EntityId"/> scheme
    /// (<c>EntityId.CreateForPlayer</c>) lives in <c>PFE.Sim</c>, which <c>PFE.View</c> references.</para>
    ///
    /// <para><b>Deliberately tiny.</b> No member exists that no consumer needs yet — there is no
    /// <c>GetInput(int)</c>, because raw <c>Input.*</c> sampling is the client's own device feeding
    /// a <c>PlayerCommand</c> (F-03), and that seam already exists. Fewer members means fewer
    /// reconciliation questions when a real multiplayer implementation arrives.</para>
    /// </summary>
    public interface IPlayerSlotContext
    {
        /// <summary>How many player slots this session resolves. 1 in single-player.</summary>
        int LocalSlotCount { get; }

        /// <summary>The stable entity id for a slot — <c>EntityId.CreateForPlayer(slotIndex)</c>.</summary>
        EntityId GetEntityId(int slotIndex);

        /// <summary>The slot's controller, or <c>null</c> if none is live. Slot 0 in single-player.</summary>
        PlayerController GetPlayer(int slotIndex);

        /// <summary>
        /// The slot's camera. Presentation-facing; may be <c>Camera.main</c> for slot 0. Exists for
        /// the future multiplayer case and for consumers that are not the player — the player's own
        /// view keeps its cached <c>Camera.main</c> (marked <c>[LocalOnly]</c>) rather than routing
        /// through here in single-player.
        /// </summary>
        Camera GetCamera(int slotIndex);
    }
}
