namespace PFE.Sim.Replication
{
    /// <summary>The host-side admission decision for one "toggle this door" request from a peer — the
    /// door counterpart of <see cref="SimHostInteractVerdict"/>. A peer's admitted interact edge names
    /// an object; the host sink decides whether that object is a container (the slice-1 path) or a
    /// door box (this path) and refuses anything that is neither. Pure so every refusal reason is
    /// pin-able offline; the view sink translates the verdict into a count, never into a policy.</summary>
    public enum SimHostDoorVerdict
    {
        /// <summary>The request may run: the host performs the authoritative toggle.</summary>
        Admit = 0,
        /// <summary>The named room does not exist in the host's world.</summary>
        RefuseUnknownRoom = 1,
        /// <summary>The peer has no simulated body on the host (not joined, not built yet).</summary>
        RefuseNoBody = 2,
        /// <summary>The peer's body stands in another room — the request asks the host to work a door
        /// the peer is not standing at.</summary>
        RefuseBodyInOtherRoom = 3,
        /// <summary>No object with the named <c>spawnIndex</c> exists in the room.</summary>
        RefuseNoObject = 4,
        /// <summary>The object is not a door box (no <c>door=</c> attribute on its definition).</summary>
        RefuseNotADoor = 5,
        /// <summary>The door carries a lock or a mine. Lock state is not replicated, and a peer's
        /// press has no picker on the host, so the toggle is refused rather than faked — the same
        /// documented gap as remote container opens.</summary>
        RefuseLocked = 6,
        /// <summary>The room has no live <c>DoorPropPresenter</c> for the object (its visuals were
        /// never built on the host), so there is nothing to toggle.</summary>
        RefuseNoPresenter = 7,
        /// <summary>The object is a checkpoint door: pressing it activates the checkpoint, it does
        /// not toggle. Refused so a peer's press cannot turn a story trigger into a plain door.</summary>
        RefuseCheckpoint = 8,
    }

    /// <summary>
    /// Decides whether one admitted interact edge may toggle its door on the host. The checks run in
    /// dependency order — each input is only meaningful when the ones before it held, so the verdict
    /// names the FIRST broken link, which is the message an operator can act on.
    /// </summary>
    public static class SimHostDoorRules
    {
        public static SimHostDoorVerdict Admit(
            bool roomFound,
            bool peerBodyFound,
            bool peerInRoom,
            bool objectFound,
            bool isDoor,
            bool locked,
            bool presenterFound,
            bool isCheckpoint)
        {
            if (!roomFound) return SimHostDoorVerdict.RefuseUnknownRoom;
            if (!peerBodyFound) return SimHostDoorVerdict.RefuseNoBody;
            if (!peerInRoom) return SimHostDoorVerdict.RefuseBodyInOtherRoom;
            if (!objectFound) return SimHostDoorVerdict.RefuseNoObject;
            if (!isDoor) return SimHostDoorVerdict.RefuseNotADoor;
            if (isCheckpoint) return SimHostDoorVerdict.RefuseCheckpoint;
            if (locked) return SimHostDoorVerdict.RefuseLocked;
            if (!presenterFound) return SimHostDoorVerdict.RefuseNoPresenter;
            return SimHostDoorVerdict.Admit;
        }

        public static string Describe(SimHostDoorVerdict verdict)
        {
            switch (verdict)
            {
                case SimHostDoorVerdict.Admit: return "admit";
                case SimHostDoorVerdict.RefuseUnknownRoom: return "unknown room";
                case SimHostDoorVerdict.RefuseNoBody: return "no peer body";
                case SimHostDoorVerdict.RefuseBodyInOtherRoom: return "peer in another room";
                case SimHostDoorVerdict.RefuseNoObject: return "no object at spawn index";
                case SimHostDoorVerdict.RefuseNotADoor: return "not a door";
                case SimHostDoorVerdict.RefuseLocked: return "locked (not replicated yet)";
                case SimHostDoorVerdict.RefuseNoPresenter: return "no live presenter";
                case SimHostDoorVerdict.RefuseCheckpoint: return "checkpoint door (no toggle)";
                default: return "unknown (" + (int)verdict + ")";
            }
        }
    }
}
