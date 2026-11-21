using System;
using System.Collections.Generic;
using PFE.Sim.Replication;
using PFE.Sim.State;
using PFE.Systems.Loot;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Security;

namespace PFE.Net
{
    /// <summary>
    /// The view-layer <see cref="ISimInteractSink"/> — the host-side executor of a peer's admitted
    /// "open this container" edge. Closes the loop the command uplink opened: the client's press
    /// arrives as a <see cref="SimInteractRequest"/>, and THIS class resolves it against the live host
    /// world and runs the one authoritative open, so both machines spill the same chest exactly once.
    ///
    /// <para><b>Why every guard is resolved here and not in the pure layer.</b> The admission ORDER is
    /// pure (<see cref="SimHostInteractRules"/>) but each input needs the host's scene: the room table
    /// (a live <c>LandMap</c>), the peer's simulated body (its motor's current room), the object's
    /// <c>spawnIndex</c> scan, and the lock profile. This class gathers the bools; the rule decides.
    /// The applied/refused counters are the readback that separates "the peer never pressed" from
    /// "the host never ran it" — a sink that silently drops everything looks like a healthy session
    /// with a broken E key.</para>
    ///
    /// <para><b>Authority direction.</b> The open runs on the host's world state with no local user
    /// (see <see cref="ContainerPropPresenter.InteractHost"/>): the peer's inventory and perks live on
    /// the client's machine, so perk-dependent roll branches do not fire for remote opens — recorded as
    /// a slice-1 gap, not papered over with the host player's stats.</para>
    /// </summary>
    public sealed class PfeInteractSink : ISimInteractSink
    {
        private readonly Func<ulong, RoomInstance> _roomByHash;
        private readonly Func<byte, PfeRemoteBody> _bodyBySlot;

        public PfeInteractSink(Func<ulong, RoomInstance> roomByHash, Func<byte, PfeRemoteBody> bodyBySlot)
        {
            _roomByHash = roomByHash ?? throw new ArgumentNullException(nameof(roomByHash));
            _bodyBySlot = bodyBySlot ?? throw new ArgumentNullException(nameof(bodyBySlot));
        }

        /// <summary>How many admitted edges became authoritative opens.</summary>
        public int AppliedOpens { get; private set; }

        /// <summary>How many edges a guard refused. Non-zero with a healthy command counter means the
        /// host is seeing presses and declining them — read <see cref="LastRefusal"/>.</summary>
        public int RefusedOpens { get; private set; }

        /// <summary>How many admitted edges became authoritative door toggles (the door half of the
        /// interact uplink). The readback that separates "the peer pressed E on a door" from "the
        /// host never ran it".</summary>
        public int AppliedDoorToggles { get; private set; }

        /// <summary>How many door edges the admission gate refused — read <see cref="LastRefusal"/>
        /// for the first broken link.</summary>
        public int RefusedDoorToggles { get; private set; }

        /// <summary>The first broken link of the most recent refusal, in operator words.</summary>
        public string LastRefusal { get; private set; } = "none";

        public void Apply(uint peerId, byte slot, in SimInteractRequest request)
        {
            RoomInstance room = _roomByHash(request.RoomHash);
            PfeRemoteBody body = _bodyBySlot(slot);

            bool roomFound = room != null;
            bool bodyFound = body != null && body.IsSimulated;
            // The peer must be standing in the room it asks the host to loot: a body in another room
            // naming this hash is asking the host to empty a chest across the map.
            bool peerInRoom = bodyFound && ReferenceEquals(body.Room, room);

            ObjectInstance obj = roomFound ? FindBySpawnIndex(room, request.ObjectIndex) : null;
            bool objectFound = obj != null;

            // The container test reads the object's own data through the same rule the presenter
            // caches — `cont` resolved placement-first then definition — so the sink and the presenter
            // cannot disagree about what a container is.
            string cont = objectFound
                ? ContainerRule.ResolveCont(
                    obj.GetAttribute("cont", null),
                    obj.definition != null ? obj.definition.containerId : null)
                : null;
            bool isContainer = objectFound && ContainerRule.IsContainer(cont);
            bool alreadyLooted = objectFound
                && obj.runtimeState != null
                && obj.runtimeState.lootState > 0;
            bool locked = objectFound && SecurityGate.IsLocked(obj, room);

            // An interact edge names an OBJECT, not a verb: the same press that opens a chest also
            // toggles a door. Container first (the slice-1 path); when the object is not a container,
            // the door admission gate decides — an edge that is neither is refused with its reason,
            // never silently dropped.
            if (!isContainer)
            {
                ApplyDoor(peerId, slot, request, room, body, roomFound, bodyFound,
                    peerInRoom: bodyFound && ReferenceEquals(body.Room, room),
                    obj, objectFound, locked);
                return;
            }

            ContainerPropPresenter presenter = null;
            bool presenterFound = objectFound
                && ContainerPropPresenter.TryGetPresenter(obj, out presenter);

            SimHostInteractVerdict verdict = SimHostInteractRules.Admit(
                roomFound, bodyFound, peerInRoom, objectFound,
                isContainer, alreadyLooted, locked, presenterFound);

            if (verdict != SimHostInteractVerdict.Admit)
            {
                RefusedOpens++;
                LastRefusal = SimHostInteractRules.Describe(verdict) +
                              " (room 0x" + request.RoomHash.ToString("X16") +
                              ", object " + request.ObjectIndex + ", peer " + peerId + ")";
                return;
            }

            if (presenter.InteractHost())
            {
                AppliedOpens++;
                LastRefusal = "none";
            }
            else
            {
                // The presenter's own Admits re-check refused — the chest was opened between the
                // admission read and the call. Counted as a refusal so applied+refused still sums to
                // every admitted edge.
                RefusedOpens++;
                LastRefusal = "already looted (presenter recheck)" +
                              " (room 0x" + request.RoomHash.ToString("X16") +
                              ", object " + request.ObjectIndex + ", peer " + peerId + ")";
            }
        }

        private void ApplyDoor(uint peerId, byte slot, in SimInteractRequest request,
            RoomInstance room, PfeRemoteBody body,
            bool roomFound, bool bodyFound, bool peerInRoom,
            ObjectInstance obj, bool objectFound, bool locked)
        {
            bool isDoor = objectFound && obj != null && obj.IsDoorBox();

            DoorPropPresenter door = null;
            bool presenterFound = objectFound
                && DoorPropPresenter.TryGetPresenter(obj, out door);
            bool isCheckpoint = presenterFound && door != null && door.IsCheckpoint;

            SimHostDoorVerdict verdict = SimHostDoorRules.Admit(
                roomFound, bodyFound, peerInRoom, objectFound,
                isDoor, locked, presenterFound, isCheckpoint);

            if (verdict != SimHostDoorVerdict.Admit)
            {
                RefusedDoorToggles++;
                LastRefusal = "door: " + SimHostDoorRules.Describe(verdict) +
                              " (room 0x" + request.RoomHash.ToString("X16") +
                              ", object " + request.ObjectIndex + ", peer " + peerId + ")";
                return;
            }

            if (door.InteractHost())
            {
                AppliedDoorToggles++;
                LastRefusal = "none";
            }
            else
            {
                // The presenter's own re-check refused — the door was toggled between the admission
                // read and the call. Counted as a refusal so applied+refused still sums to every
                // admitted door edge.
                RefusedDoorToggles++;
                LastRefusal = "door: toggle refused by presenter recheck" +
                              " (room 0x" + request.RoomHash.ToString("X16") +
                              ", object " + request.ObjectIndex + ", peer " + peerId + ")";
            }
        }

        private static ObjectInstance FindBySpawnIndex(RoomInstance room, int spawnIndex)
        {
            // List order is not stable (RemoveObject closes gaps), which is exactly why the wire
            // addresses containers by the stable population index instead.
            List<ObjectInstance> objects = room.objects;
            if (objects == null) return null;
            for (int i = 0; i < objects.Count; i++)
            {
                ObjectInstance candidate = objects[i];
                if (candidate != null && candidate.spawnIndex == spawnIndex) return candidate;
            }
            return null;
        }
    }
}
