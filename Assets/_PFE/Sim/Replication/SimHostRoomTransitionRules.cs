namespace PFE.Sim.Replication
{
    /// <summary>The host-side admission decision for one "I changed rooms" declaration from a peer —
    /// the room-transition counterpart of <see cref="SimHostInteractVerdict"/> and
    /// <see cref="SimHostDoorVerdict"/>. A peer's transition packet names a room and a world-pixel
    /// arrival position; without a gate, any bug or out-of-order packet could teleport the peer's
    /// simulated body across the entire map or park it outside the room it claims to stand in. Pure
    /// so every refusal reason is pin-able offline; the view sink translates the verdict into a
    /// count, never into a policy.</summary>
    public enum SimHostRoomTransitionVerdict
    {
        /// <summary>The declaration may run: the host records the room and relocates the peer's body
        /// to the named position.</summary>
        Admit = 0,
        /// <summary>The named room does not exist in the host's world — a world mismatch or a
        /// corrupted packet, never a legitimate arrival.</summary>
        RefuseUnknownRoom = 1,
        /// <summary>The arrival position lies outside the target room's world-pixel bounds, beyond
        /// the doorway margin — the peer is asking the host to place its body where the room is
        /// not.</summary>
        RefuseCoordinatesOutOfBounds = 2,
    }

    /// <summary>
    /// Decides whether one peer room-transition declaration may move the peer's simulated body on the
    /// host. The checks run in dependency order — the bounds are only meaningful once the room
    /// exists, so the verdict names the FIRST broken link, which is the message an operator can act
    /// on. The caller supplies the room's world-pixel bounds as plain floats: the pure layer has no
    /// room graph or coordinate types.
    /// </summary>
    public static class SimHostRoomTransitionRules
    {
        /// <summary>Margin in pixels around the room bounds to allow edge/doorway arrival: a peer
        /// stepping through a door reports the position where it stands, which can sit a few pixels
        /// outside the strict rect at the seam between two rooms.</summary>
        public const float BoundsMarginPixels = 128f;

        public static SimHostRoomTransitionVerdict Admit(
            bool roomFound,
            float worldPx, float worldPy,
            float roomMinPx, float roomMinPy,
            float roomMaxPx, float roomMaxPy)
        {
            if (!roomFound) return SimHostRoomTransitionVerdict.RefuseUnknownRoom;
            if (worldPx < roomMinPx - BoundsMarginPixels || worldPx > roomMaxPx + BoundsMarginPixels ||
                worldPy < roomMinPy - BoundsMarginPixels || worldPy > roomMaxPy + BoundsMarginPixels)
            {
                return SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds;
            }
            return SimHostRoomTransitionVerdict.Admit;
        }

        public static string Describe(SimHostRoomTransitionVerdict verdict)
        {
            switch (verdict)
            {
                case SimHostRoomTransitionVerdict.Admit: return "admit";
                case SimHostRoomTransitionVerdict.RefuseUnknownRoom: return "unknown room";
                case SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds: return "coordinates out of bounds";
                default: return "unknown (" + (int)verdict + ")";
            }
        }
    }
}
