using System.Collections.Generic;
using PFE.Core.Ids;
using PFE.Sim.State;

namespace PFE.Sim.Session
{
    /// <summary>
    /// The minimal ownership model a <b>host-authoritative</b> game needs — row 3.4, finding F-07.
    ///
    /// <para><b>Why PFE needs far less than the precedent.</b> Remains' <c>OwnershipService</c> exists
    /// to decide which peer simulates which actor, because that game lets clients own NPCs and props.
    /// PFE does not: the host simulates every NPC, every projectile and every room. That leaves exactly
    /// two questions ownership has to answer, and this class answers only those:</para>
    /// <list type="number">
    /// <item>which player's commands drive which player entity — the <c>slot → EntityId</c> binding;</item>
    /// <item>what happens to a body when its player disconnects, and what happens when they come
    /// back.</item>
    /// </list>
    ///
    /// <para><b>Disconnect does not destroy the body.</b> The entity stays in the world with the slot
    /// released, so <see cref="ApplyTo"/> stamps it
    /// <see cref="SimUnitState.HostAiControllerSlot"/> and the host simply keeps simulating it — the
    /// player's character stands where it stood, and can be shot. That is the whole of the
    /// "disconnected bodies" rule, and it falls out of one line rather than a special case. Destroying
    /// the body instead would be a teleport home on every dropped packet.</para>
    ///
    /// <para><b>A claim is never a steal.</b> Claiming an entity another slot already holds is
    /// <i>rejected</i>, not granted. A steal would leave the first player's input driving nothing —
    /// silently, because the binding it lost is the only place the loss would show.</para>
    ///
    /// <para><b>Reconnect rebinds only if the body is still free.</b> If the entity is gone, or another
    /// slot claimed it in the meantime, the slot still becomes connected — a returning player is present
    /// even when their character is not — but unbound, and <see cref="OwnershipChange.ReconnectedUnbound"/>
    /// says so rather than leaving the caller to infer it.</para>
    ///
    /// <para><b>Unity-free, and deliberately not a MonoBehaviour.</b> Every rule above is arithmetic on
    /// a dictionary, so the whole model is pinned by fixtures that actually run — which is the only kind
    /// of guard this project has found reliable for session logic. The Unity side owns exactly one
    /// thing: calling these methods from the connection callbacks.</para>
    /// </summary>
    public sealed class OwnershipService
    {
        /// <summary>No slot. The same value <see cref="SimUnitState.HostAiControllerSlot"/> uses, so
        /// "unowned" and "host-simulated" are one concept rather than two that must be kept in step.</summary>
        public const int NoSlot = SimUnitState.HostAiControllerSlot;

        /// <summary>slot → the entity it drives.</summary>
        private readonly Dictionary<int, EntityId> _slotToEntity = new Dictionary<int, EntityId>();

        /// <summary>entity hash → the slot that drives it. Keyed on the hash because that is the
        /// record's own identity everywhere else (the wire, the state hash, the registry).</summary>
        private readonly Dictionary<ulong, int> _entityHashToSlot = new Dictionary<ulong, int>();

        /// <summary>slot → the entity it held when it disconnected, for <see cref="Reconnect"/>.</summary>
        private readonly Dictionary<int, EntityId> _lastBound = new Dictionary<int, EntityId>();

        /// <summary>Every slot that has ever connected and not been forgotten — bound or not.</summary>
        private readonly HashSet<int> _connected = new HashSet<int>();

        /// <summary>How many slots are currently connected, whether or not they have a body.</summary>
        public int ConnectedSlotCount => _connected.Count;

        /// <summary>How many slots currently drive an entity. Never larger than
        /// <see cref="ConnectedSlotCount"/>.</summary>
        public int BoundSlotCount => _slotToEntity.Count;

        /// <summary>The slot that drives <paramref name="entity"/>, or
        /// <see cref="NoSlot"/> when the host owns it.</summary>
        public int SlotOf(EntityId entity)
        {
            if (!entity.IsValid) return NoSlot;
            return _entityHashToSlot.TryGetValue(entity.Hash, out int slot) ? slot : NoSlot;
        }

        /// <summary>The entity <paramref name="slot"/> drives, or <see cref="EntityId.Empty"/> when it
        /// drives none.</summary>
        public EntityId EntityOf(int slot)
        {
            return _slotToEntity.TryGetValue(slot, out EntityId entity) ? entity : EntityId.Empty;
        }

        /// <summary>True when the slot is present in the session (it may still be bodyless).</summary>
        public bool IsConnected(int slot) => _connected.Contains(slot);

        /// <summary>True when the slot is present <i>and</i> drives a body.</summary>
        public bool IsBound(int slot) => _slotToEntity.ContainsKey(slot);

        /// <summary>
        /// Bind <paramref name="slot"/> to <paramref name="entity"/> — the moment a player's commands
        /// start driving a character.
        ///
        /// <para><b>A slot holds one body at a time.</b> Claiming a second one releases the first
        /// (which becomes host-simulated) rather than leaving two entities answering to one slot: the
        /// reverse map is keyed on the entity, so a stale second binding would be unreachable and its
        /// body would keep taking the player's input forever.</para>
        /// </summary>
        public OwnershipResult Claim(int slot, EntityId entity)
        {
            if (slot < 0) return OwnershipResult.Reject(OwnershipRejection.InvalidSlot);
            if (!entity.IsValid) return OwnershipResult.Reject(OwnershipRejection.InvalidEntity);

            if (_entityHashToSlot.TryGetValue(entity.Hash, out int currentOwner))
            {
                if (currentOwner != slot)
                    return OwnershipResult.Reject(OwnershipRejection.EntityOwnedByAnotherSlot);

                // Already this slot's body. Idempotent: a repeated claim is what a reconnect attempt
                // looks like, and answering "changed" would make the caller emit a spurious event.
                _connected.Add(slot);
                return OwnershipResult.Of(OwnershipChange.Unchanged);
            }

            bool replaced = false;

            if (_slotToEntity.TryGetValue(slot, out EntityId previous) && previous.IsValid
                && previous.Hash != entity.Hash)
            {
                _entityHashToSlot.Remove(previous.Hash);
                replaced = true;
            }

            _slotToEntity[slot] = entity;
            _entityHashToSlot[entity.Hash] = slot;
            _lastBound[slot] = entity;
            _connected.Add(slot);

            return OwnershipResult.Of(replaced ? OwnershipChange.Replaced : OwnershipChange.Bound);
        }

        /// <summary>
        /// The player left. The body stays in the world and the host takes it over.
        ///
        /// <para><b>The binding is released, not merely marked dead</b>, so the entity becomes
        /// claimable: a reconnecting player gets their body back, and — if somebody else claimed it in
        /// the meantime — the reconnect honestly reports that it could not.</para>
        /// </summary>
        public OwnershipResult Disconnect(int slot)
        {
            if (slot < 0) return OwnershipResult.Reject(OwnershipRejection.InvalidSlot);
            if (!_connected.Contains(slot)) return OwnershipResult.Reject(OwnershipRejection.NotConnected);

            if (_slotToEntity.TryGetValue(slot, out EntityId entity))
            {
                _slotToEntity.Remove(slot);
                if (entity.IsValid) _entityHashToSlot.Remove(entity.Hash);
                _lastBound[slot] = entity;
            }

            _connected.Remove(slot);
            return OwnershipResult.Of(OwnershipChange.Disconnected);
        }

        /// <summary>
        /// The player came back. The slot is connected either way; whether it gets a body depends on
        /// whether that body is still there and still free.
        /// </summary>
        public OwnershipResult Reconnect(int slot)
        {
            if (slot < 0) return OwnershipResult.Reject(OwnershipRejection.InvalidSlot);
            if (_connected.Contains(slot)) return OwnershipResult.Reject(OwnershipRejection.AlreadyConnected);

            _connected.Add(slot);

            if (!_lastBound.TryGetValue(slot, out EntityId entity) || !entity.IsValid)
                return OwnershipResult.Of(OwnershipChange.ReconnectedUnbound);

            // Taken by another slot while this one was away. The returning player does not steal it
            // back — the same rule as Claim, for the same reason.
            if (_entityHashToSlot.ContainsKey(entity.Hash))
                return OwnershipResult.Of(OwnershipChange.ReconnectedUnbound);

            _slotToEntity[slot] = entity;
            _entityHashToSlot[entity.Hash] = slot;
            return OwnershipResult.Of(OwnershipChange.Rebound);
        }

        /// <summary>
        /// Stamp a unit record with the slot that drives it — the one line that connects this service to
        /// the wire.
        ///
        /// <para><b><c>ref</c>, not a returned copy.</b> <see cref="SimUnitState"/> is a struct, so a
        /// by-value parameter would be stamped and then discarded — the call would compile, read
        /// correctly, and leave every record at its default slot. That is the exact failure this
        /// project keeps paying for, so the signature makes the mistake impossible instead of
        /// documenting it.</para>
        ///
        /// <para><b>Only <c>ControllerSlot</c> is written.</b> The lifetime fields
        /// (<c>Generation</c>, <c>LifecycleSequence</c>) describe the <i>record</i>, and a body that
        /// changes hands is the same record: bumping its generation here would tell every receiver that
        /// the unit was despawned and respawned, discarding its interpolation history and, on the
        /// receiving side, treating a handover as a new entity.</para>
        /// </summary>
        public void ApplyTo(ref SimUnitState unit)
        {
            unit.ControllerSlot = SlotOf(new EntityId(unit.EntityHash, null));
        }

        /// <summary>Forget every slot — the host's "new session" reset. Bodies are not touched: they
        /// belong to the world, not to the session.</summary>
        public void Clear()
        {
            _slotToEntity.Clear();
            _entityHashToSlot.Clear();
            _lastBound.Clear();
            _connected.Clear();
        }
    }

    /// <summary>What an ownership request did. <see cref="Rejected"/> means nothing changed.</summary>
    public enum OwnershipChange
    {
        /// <summary>The request was refused; the table is unchanged. See <see cref="OwnershipResult.Rejection"/>.</summary>
        Rejected = 0,
        /// <summary>Already in the requested state. A repeat, not an error.</summary>
        Unchanged = 1,
        /// <summary>A slot acquired a body it did not have.</summary>
        Bound = 2,
        /// <summary>A slot swapped one body for another; the previous one is now host-simulated.</summary>
        Replaced = 3,
        /// <summary>A slot left; its body is now host-simulated and claimable.</summary>
        Disconnected = 4,
        /// <summary>A slot came back and got its body back.</summary>
        Rebound = 5,
        /// <summary>A slot came back, but its body is gone or taken.</summary>
        ReconnectedUnbound = 6,
    }

    /// <summary>Why a request was refused. Distinct cases on purpose: "no slot −1" and "another slot
    /// owns it" are different bugs and a single <c>false</c> cannot tell them apart.</summary>
    public enum OwnershipRejection
    {
        None = 0,
        InvalidSlot = 1,
        InvalidEntity = 2,
        EntityOwnedByAnotherSlot = 3,
        NotConnected = 4,
        AlreadyConnected = 5,
    }

    /// <summary>The outcome of one ownership request.</summary>
    public readonly struct OwnershipResult
    {
        public readonly OwnershipChange Change;
        public readonly OwnershipRejection Rejection;

        private OwnershipResult(OwnershipChange change, OwnershipRejection rejection)
        {
            Change = change;
            Rejection = rejection;
        }

        public static OwnershipResult Of(OwnershipChange change) => new OwnershipResult(change, OwnershipRejection.None);

        public static OwnershipResult Reject(OwnershipRejection rejection) =>
            new OwnershipResult(OwnershipChange.Rejected, rejection);

        /// <summary>True when the table changed. <see cref="OwnershipChange.Unchanged"/> is not an
        /// application: a caller that emitted an event for it would send a spurious reliable message.</summary>
        public bool Applied =>
            Change != OwnershipChange.Rejected && Change != OwnershipChange.Unchanged;

        public override string ToString() =>
            Change == OwnershipChange.Rejected ? "rejected(" + Rejection + ")" : Change.ToString();
    }
}
