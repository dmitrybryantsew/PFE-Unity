using System;

namespace PFE.Core.Input
{
    /// <summary>
    /// One tick of player intent — the unit of the client→host command channel.
    ///
    /// <para><b>F-03 / Phase 2.1 of the multiplayer readiness plan.</b> This promotes
    /// <c>PlayerLocomotionController.SetIntent(...)</c>'s parameter list into a tick-stamped,
    /// serializable value type, per
    /// <c>docs/multiplayer/Physics_And_Units_Multiplayer_Readiness_2026-11-04.md</c> §9. The seam
    /// already existed (<c>SetIntent</c> is intent-only, no state); this struct is that seam made
    /// explicit and transport-ready.</para>
    ///
    /// <para><b>Intent only — no state, no Unity objects.</b> A command carries what the player
    /// <i>wants</i> for one tick, never what the Sim <i>is</i>. It holds no <c>UnityEngine.Object</c>,
    /// no <c>Transform</c>, no motor state — so it can be serialized, sequenced, buffered and
    /// replayed. This is the same contract as Remains' <c>PlayerCommand</c> ("commands contain intent
    /// only; both devices and automated scenarios use this contract"), minus any intent PFE does not
    /// implement — an intent the host cannot execute must not be in the contract.</para>
    ///
    /// <para><b>Tick-stamped.</b> <see cref="TickIndex"/> names the tick the command is for, so the
    /// host can say "which tick was this for", a client can re-simulate from an acked tick using its
    /// buffered commands (reconciliation, §10), and a stale command can be rejected.
    /// <see cref="Sequence"/> is the wrap-safe ordering for the unreliable command channel
    /// (redundant last-N, §1.3).</para>
    ///
    /// <para><b>Quantization.</b> Movement axes are quantized to <c>sbyte</c> (−127…127) so the wire
    /// form is compact and platform-independent; <see cref="MoveXFloat"/>/<see cref="MoveYFloat"/>
    /// recover the −1…1 analogue value. The aim angle stays a full <c>float</c> (degrees) because
    /// AS3 facing reads it at pointer precision.</para>
    /// </summary>
    [Serializable]
    public struct PlayerCommand : IEquatable<PlayerCommand>
    {
        /// <summary>Button bit flags carried in <see cref="Buttons"/>.</summary>
        [Flags]
        public enum Button : ushort
        {
            None = 0,
            /// <summary>Jump is held this tick (level, not edge).</summary>
            JumpHeld = 1 << 0,
            /// <summary>Run/dash modifier is held this tick (level).</summary>
            RunHeld = 1 << 1,
            /// <summary>Jump was pressed this tick (edge; feeds the jump buffer).</summary>
            JumpPressed = 1 << 2,
            /// <summary>Dash was triggered this tick (edge; horizontal double-tap).</summary>
            DashPressed = 1 << 3,
            /// <summary>Drop-through was triggered this tick (edge; down double-tap).</summary>
            DropThroughPressed = 1 << 4,
        }

        /// <summary>Monotonic simulation tick this command applies to. The only time source.</summary>
        public int TickIndex;

        /// <summary>Which player slot issued the command (0-based; host AI uses its own path).</summary>
        public byte SlotIndex;

        /// <summary>Per-sender wrap-safe sequence for the unreliable command channel.</summary>
        public uint Sequence;

        /// <summary>Horizontal intent, quantized −127…127 (see <see cref="MoveXFloat"/>).</summary>
        public sbyte MoveX;

        /// <summary>Vertical intent, quantized −127…127 (see <see cref="MoveYFloat"/>).</summary>
        public sbyte MoveY;

        /// <summary>Aim facing in degrees (AS3 pointer precision). Not quantized.</summary>
        public float AimAngle;

        /// <summary>Edge and level buttons for this tick.</summary>
        public Button Buttons;

        /// <summary>Horizontal intent as a −1…1 float.</summary>
        public readonly float MoveXFloat => MoveX / 127f;

        /// <summary>Vertical intent as a −1…1 float.</summary>
        public readonly float MoveYFloat => MoveY / 127f;

        public readonly bool JumpHeld => (Buttons & Button.JumpHeld) != 0;
        public readonly bool RunHeld => (Buttons & Button.RunHeld) != 0;
        public readonly bool JumpPressed => (Buttons & Button.JumpPressed) != 0;
        public readonly bool DashPressed => (Buttons & Button.DashPressed) != 0;
        public readonly bool DropThroughPressed => (Buttons & Button.DropThroughPressed) != 0;

        /// <summary>
        /// Builds a command from the same intent <c>SetIntent</c> receives today. This is the bridge
        /// that lets <c>PlayerController</c> construct a command and <c>PlayerLocomotionController</c>
        /// consume it without changing what the intent <i>means</i> — a state-location change, not a
        /// behaviour change.
        /// </summary>
        public static PlayerCommand FromIntent(
            int tickIndex,
            byte slotIndex,
            uint sequence,
            float moveInputX,
            float moveInputY,
            bool jumpHeld,
            bool runHeld,
            bool jumpPressed,
            bool dashPressed,
            bool dropThroughPressed,
            float aimAngle)
        {
            Button buttons = Button.None;
            if (jumpHeld) buttons |= Button.JumpHeld;
            if (runHeld) buttons |= Button.RunHeld;
            if (jumpPressed) buttons |= Button.JumpPressed;
            if (dashPressed) buttons |= Button.DashPressed;
            if (dropThroughPressed) buttons |= Button.DropThroughPressed;

            return new PlayerCommand
            {
                TickIndex = tickIndex,
                SlotIndex = slotIndex,
                Sequence = sequence,
                MoveX = Quantize(moveInputX),
                MoveY = Quantize(moveInputY),
                AimAngle = aimAngle,
                Buttons = buttons,
            };
        }

        /// <summary>Quantizes a −1…1 analogue axis to −127…127, clamping out-of-range input.</summary>
        public static sbyte Quantize(float value)
        {
            float clamped = value < -1f ? -1f : (value > 1f ? 1f : value);
            return (sbyte)System.Math.Round(clamped * 127f, MidpointRounding.AwayFromZero);
        }

        public readonly bool Equals(PlayerCommand other)
        {
            return TickIndex == other.TickIndex
                && SlotIndex == other.SlotIndex
                && Sequence == other.Sequence
                && MoveX == other.MoveX
                && MoveY == other.MoveY
                && AimAngle.Equals(other.AimAngle)
                && Buttons == other.Buttons;
        }

        public override readonly bool Equals(object obj) => obj is PlayerCommand other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked
            {
                int hash = TickIndex;
                hash = (hash * 397) ^ SlotIndex;
                hash = (hash * 397) ^ (int)Sequence;
                hash = (hash * 397) ^ MoveX;
                hash = (hash * 397) ^ MoveY;
                hash = (hash * 397) ^ AimAngle.GetHashCode();
                hash = (hash * 397) ^ (int)Buttons;
                return hash;
            }
        }

        public override readonly string ToString()
        {
            return $"PlayerCommand(tick={TickIndex} slot={SlotIndex} seq={Sequence} " +
                   $"move=({MoveXFloat:F2},{MoveYFloat:F2}) aim={AimAngle:F1} buttons={Buttons})";
        }
    }
}
