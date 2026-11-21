using System;

namespace PFE.Character.Animation
{
    /// <summary>
    /// The compiled-in two-way table between a <c>CharacterAnimationDriver</c> state name and the
    /// <c>byte</c> that crosses the wire in <c>SimUnitState.PoseId</c>.
    ///
    /// <para><b>Why a fixed table and not an interned string.</b> The state names are not content —
    /// they are the vocabulary of the animation FSM itself (<c>LocomotionAnimStates</c> and
    /// <c>ActionAnimStates</c>), compiled into both peers from the same build. Interning content ids
    /// exists because two machines can load different data; two machines running different CODE cannot
    /// join at all (the handshake version refuses them), so the names are already guaranteed to agree.
    /// A fixed table keeps the snapshot record pure C# with no string side-channel.</para>
    ///
    /// <para><b>Why ids are stable indexes, not hashes.</b> The id is data on the wire: re-ordering
    /// this table between builds would re-number every state a peer sends. Entries are append-only;
    /// the index IS the protocol. New states take the next free id; dead states keep their slot.</para>
    ///
    /// <para><b>Unknown names map to <see cref="Unknown"/> and unknown ids map to null.</b> A state
    /// this build does not know (a newer peer's name, or a clip the definition does not carry) must
    /// leave the receiver's current state untouched — a wrong guess animates the remote player, which
    /// is worse than a frozen one because it looks correct.</para>
    /// </summary>
    public static class RemoteAnimStateIds
    {
        /// <summary>Sent when the host's current state name has no agreed id. The receiver keeps its
        /// current animation state rather than applying a guessed one.</summary>
        public const byte Unknown = 0xFF;

        // The locomotion set, in FSM evaluation order (LocomotionAnimStates), then the action set
        // (ActionAnimStates). INDEX IS THE WIRE ID — append only, never reorder.
        private static readonly string[] Names =
        {
            "stay",    //  0 — idle + crouch-idle + teleport (all hold the stay pose bank)
            "walk",    //  1
            "trot",    //  2
            "run",     //  3
            "jump",    //  4 — airborne (rise/fall frame mapped from velocity)
            "polz",    //  5 — crawl while crouched + moving
            "roll",    //  6 — crouch-roll out of a sprint
            "plav",    //  7 — swim
            "levit",   //  8 — levitate
            "laz",     //  9 — ladder climb
            "pinok",   // 10 — airborne knockback
            "free1",   // 11 — free idle variants (chosen by the host's own RNG)
            "free2",   // 12
            "free3",   // 13
            "die",     // 14 — death (locked clip)
            "dieali",  // 15 — alt death
            "res",     // 16 — resurrect
            "punch",   // 17
            "kick",    // 18
            "derg",    // 19 — stagger
            "lurk1",   // 20 — lurk variants are distinct CLIPS (lurk{_variant}), not frames
            "lurk2",   // 21
            "lurk3",   // 22
            "unlurk",  // 23
        };

        /// <summary>The wire id for <paramref name="stateName"/>, or <see cref="Unknown"/> when the
        /// name is not in the compiled-in vocabulary.</summary>
        public static byte ToId(string stateName)
        {
            if (string.IsNullOrEmpty(stateName)) return Unknown;
            for (int i = 0; i < Names.Length; i++)
            {
                if (string.Equals(Names[i], stateName, StringComparison.Ordinal)) return (byte)i;
            }
            return Unknown;
        }

        /// <summary>The state name for <paramref name="id"/>, or null for <see cref="Unknown"/> or an
        /// out-of-range id — the caller keeps its current state on null.</summary>
        public static string ToName(byte id)
        {
            if (id == Unknown || id >= Names.Length) return null;
            return Names[id];
        }
    }
}
