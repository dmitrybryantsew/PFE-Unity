namespace PFE.Sim.Replication
{
    /// <summary>The host-side admission decision for one "open this container" request from a peer —
    /// the executable half of <see cref="ISimInteractSink"/>'s contract. Pure so every refusal reason is
    /// pin-able offline; the view sink translates the verdict into a count, never into a policy.</summary>
    public enum SimHostInteractVerdict
    {
        /// <summary>The request may run: the host performs the authoritative open.</summary>
        Admit = 0,
        /// <summary>The named room does not exist in the host's world.</summary>
        RefuseUnknownRoom = 1,
        /// <summary>The peer has no simulated body on the host (not joined, not built yet).</summary>
        RefuseNoBody = 2,
        /// <summary>The peer's body stands in another room — the request asks the host to loot a chest
        /// the peer is not standing at.</summary>
        RefuseBodyInOtherRoom = 3,
        /// <summary>No object with the named <c>spawnIndex</c> exists in the room.</summary>
        RefuseNoObject = 4,
        /// <summary>The object is not a container (<c>cont</c> resolved empty).</summary>
        RefuseNotAContainer = 5,
        /// <summary>The container is already looted — the idempotency guard for an edge that slipped
        /// through the redundancy window.</summary>
        RefuseAlreadyLooted = 6,
        /// <summary>The container carries a lock or a mine. Lock state is not replicated yet, so the
        /// host cannot honestly run a peer's pick; the open is refused rather than faked. See the
        /// slice-1 loot-sync note.</summary>
        RefuseLocked = 7,
        /// <summary>The room has no live <c>ContainerPropPresenter</c> for the object (its visuals were
        /// never built on the host).</summary>
        RefuseNoPresenter = 8,
    }

    /// <summary>
    /// Decides whether one admitted interact edge may open its container on the host. The checks run in
    /// dependency order — each input is only meaningful when the ones before it held, so the verdict
    /// names the FIRST broken link, which is the message an operator can act on.
    /// </summary>
    public static class SimHostInteractRules
    {
        /// <summary>Admission inputs, one per failure the view sink can observe. Kept as plain bools —
        /// the pure layer has no room graph, body or presenter types.</summary>
        public static SimHostInteractVerdict Admit(
            bool roomFound,
            bool peerBodyFound,
            bool peerInRoom,
            bool objectFound,
            bool isContainer,
            bool alreadyLooted,
            bool locked,
            bool presenterFound)
        {
            if (!roomFound) return SimHostInteractVerdict.RefuseUnknownRoom;
            if (!peerBodyFound) return SimHostInteractVerdict.RefuseNoBody;
            if (!peerInRoom) return SimHostInteractVerdict.RefuseBodyInOtherRoom;
            if (!objectFound) return SimHostInteractVerdict.RefuseNoObject;
            if (!isContainer) return SimHostInteractVerdict.RefuseNotAContainer;
            if (alreadyLooted) return SimHostInteractVerdict.RefuseAlreadyLooted;
            if (locked) return SimHostInteractVerdict.RefuseLocked;
            if (!presenterFound) return SimHostInteractVerdict.RefuseNoPresenter;
            return SimHostInteractVerdict.Admit;
        }

        public static string Describe(SimHostInteractVerdict verdict)
        {
            switch (verdict)
            {
                case SimHostInteractVerdict.Admit: return "admit";
                case SimHostInteractVerdict.RefuseUnknownRoom: return "unknown room";
                case SimHostInteractVerdict.RefuseNoBody: return "no peer body";
                case SimHostInteractVerdict.RefuseBodyInOtherRoom: return "peer in another room";
                case SimHostInteractVerdict.RefuseNoObject: return "no object at spawn index";
                case SimHostInteractVerdict.RefuseNotAContainer: return "not a container";
                case SimHostInteractVerdict.RefuseAlreadyLooted: return "already looted";
                case SimHostInteractVerdict.RefuseLocked: return "locked (not replicated yet)";
                case SimHostInteractVerdict.RefuseNoPresenter: return "no live presenter";
                default: return "unknown (" + (int)verdict + ")";
            }
        }
    }
}
