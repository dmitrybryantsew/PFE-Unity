using System;
using System.IO;

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
    /// AS3 facing reads it at pointer precision. Protocol v10 added the wire trim: the equipped
    /// weapon rides as a 2-byte interned key (<see cref="WeaponKey"/>) and the interact target /
    /// teleport cursor are written only on the ticks their button bits are set (see
    /// <see cref="WriteTo"/> for the exact layout).</para>
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
            /// <summary>Teleport key is held this tick (level; the charge releases on the first
            /// tick without the flag). Forwarded to the body's locomotion separately from the
            /// move intent because teleport is hold-to-charge, release-to-execute.</summary>
            TeleportHeld = 1 << 5,
            /// <summary>The interact key was pressed this tick (edge) — AS3 <c>act()</c>'s entry, the
            /// verb that opens a container. Meaningful only together with a well-formed
            /// <see cref="TargetRoomHash"/>/<see cref="TargetObjectIndex"/> pair; a bare edge names
            /// nothing to act on and is refused rather than defaulted to object 0 of room 0.</summary>
            InteractPressed = 1 << 6,
            /// <summary>Attack/fire is held this tick (level) — the host-side weapon mount on the
            /// peer's simulated body fires while the flag is set. Level, not edge: the weapon's own
            /// <c>t_attack</c>/<c>t_auto</c> pacing decides how many rounds a held tick produces,
            /// exactly as it does for the local rig.</summary>
            FireHeld = 1 << 7,
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

        /// <summary>Teleport cursor in world pixels at command build time (AS3 <c>celX/celY</c>) —
        /// the point the released charge executes at. Zero when no cursor has ever been forwarded;
        /// a teleport aimed nowhere is refused the same way as locally.</summary>
        public float CursorWorldX;

        /// <summary>Teleport cursor Y, world pixels. See <see cref="CursorWorldX"/>.</summary>
        public float CursorWorldY;

        /// <summary>
        /// The room the interact edge is aimed at, as <c>SimSnapshotProjectionRules.RoomKey</c> hashes
        /// it — the same key <c>SimRoomDelta.RoomHash</c> and the tile journal use.
        ///
        /// <para><b>Why an explicit id and not the cursor position.</b> The host must open the
        /// <i>same</i> container the client saw selected. Deriving it from <see cref="CursorWorldX"/>
        /// would make the host's spatial probe the authority, so a one-frame cursor difference — or two
        /// props overlapping the probe — would open a different chest. The client has already run the
        /// probe; sending its answer is both smaller than a second position and unambiguous.</para>
        /// </summary>
        public ulong TargetRoomHash;

        /// <summary>
        /// The target container's <c>ObjectInstance.spawnIndex</c> — its stable population index inside
        /// the room, and the same value <c>LootRuntime.CreateContextForContainer</c> keys the contents
        /// roll on. Zero is a legitimate index; "no target" is expressed by
        /// <see cref="TargetRoomHash"/> being zero, never by an index.</summary>
        public int TargetObjectIndex;

        /// <summary>
        /// The equipped weapon as an interned key into <c>ContentInternContext.Weapons</c>
        /// (<see cref="PFE.Sim.Content.ContentInternTable"/>), replicated ride-along on every tick.
        /// The host cannot derive what the peer holds — equipped weapons live in the peer's
        /// inventory, not in intent — so the peer states it, idempotently, on each command; the host
        /// converts the key back to the content id and stamps <c>SimUnitState.WeaponIdHash</c> with
        /// <c>SimSnapshotProjectionRules.LootItemKey(id, true)</c> for the clients, exactly as today.
        /// <see cref="PFE.Sim.Content.ContentInternTable.Uninterned"/> (0) means "nothing equipped"
        /// — and also "not in this build's weapon table", the same content-versioned answer the
        /// intern table gives a unit whose definition is unknown.
        ///
        /// <para><b>Why a key and not the 8-byte hash.</b> The hash was a per-tick constant cost for
        /// a value that changes only when the loadout does. The key is 2 bytes and is table-checked
        /// by construction: two peers with the same content build the same ordinal-sorted table
        /// (see <c>ContentInternTable</c>), so a key names exactly one weapon on both sides.</para>
        /// </summary>
        public ushort WeaponKey;

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
        public readonly bool TeleportHeld => (Buttons & Button.TeleportHeld) != 0;
        public readonly bool InteractPressed => (Buttons & Button.InteractPressed) != 0;
        public readonly bool FireHeld => (Buttons & Button.FireHeld) != 0;

        /// <summary>
        /// Builds a command from the same intent <c>SetIntent</c> receives today. This is the bridge
        /// that lets <c>PlayerController</c> construct a command and <c>PlayerLocomotionController</c>
        /// consume it without changing what the intent <i>means</i> — a state-location change, not a
        /// behaviour change.
        /// </summary>
        /// <param name="teleportHeld">Teleport key level at build time. Optional so the existing
        /// call sites (fixtures included) keep compiling; production passes it from
        /// <c>PlayerController</c>.</param>
        /// <param name="cursorWorldX">Teleport cursor, world pixels — see <see cref="CursorWorldX"/>.</param>
        /// <param name="cursorWorldY">Teleport cursor Y. See <see cref="CursorWorldX"/>.</param>
        /// <param name="interactPressed">Interact edge for this tick. Defaults to false so every
        /// existing call site and fixture keeps compiling and means what it meant.</param>
        /// <param name="targetRoomHash">The room the interact edge is aimed at — see
        /// <see cref="TargetRoomHash"/>. Zero means "no target", which is what a caller that never
        /// resolves an interact target passes.</param>
        /// <param name="targetObjectIndex">The target container's <c>spawnIndex</c>.</param>
        /// <param name="weaponKey">The equipped weapon's interned key — see <see cref="WeaponKey"/>.
        /// Zero means "nothing equipped".</param>
        /// <param name="fireHeld">Attack/fire level at build time. The host-side weapon mount on the
        /// peer's simulated body fires while the flag is set; the weapon's own pacing decides the
        /// round cadence. Defaults to false so every existing call site and fixture keeps its shape.</param>
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
            float aimAngle,
            bool teleportHeld = false,
            float cursorWorldX = 0f,
            float cursorWorldY = 0f,
            bool interactPressed = false,
            ulong targetRoomHash = 0UL,
            int targetObjectIndex = 0,
            ushort weaponKey = 0,
            bool fireHeld = false)
        {
            Button buttons = Button.None;
            if (jumpHeld) buttons |= Button.JumpHeld;
            if (runHeld) buttons |= Button.RunHeld;
            if (jumpPressed) buttons |= Button.JumpPressed;
            if (dashPressed) buttons |= Button.DashPressed;
            if (dropThroughPressed) buttons |= Button.DropThroughPressed;
            if (teleportHeld) buttons |= Button.TeleportHeld;
            if (interactPressed) buttons |= Button.InteractPressed;
            if (fireHeld) buttons |= Button.FireHeld;

            return new PlayerCommand
            {
                TickIndex = tickIndex,
                SlotIndex = slotIndex,
                Sequence = sequence,
                MoveX = Quantize(moveInputX),
                MoveY = Quantize(moveInputY),
                AimAngle = aimAngle,
                CursorWorldX = cursorWorldX,
                CursorWorldY = cursorWorldY,
                TargetRoomHash = targetRoomHash,
                TargetObjectIndex = targetObjectIndex,
                WeaponKey = weaponKey,
                Buttons = buttons,
            };
        }

        /// <summary>Quantizes a −1…1 analogue axis to −127…127, clamping out-of-range input.</summary>
        public static sbyte Quantize(float value)
        {
            float clamped = value < -1f ? -1f : (value > 1f ? 1f : value);
            return (sbyte)System.Math.Round(clamped * 127f, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Phase 5 (command uplink); protocol v10 layout: the fixed head, then two conditional
        /// blocks. Appends this intent to the wire in the exact order documented below.
        ///
        /// <para><b>Layout (little-endian).</b> Fixed head: <c>TickIndex i32, SlotIndex u8, Sequence
        /// u32, MoveX i8, MoveY i8, AimAngle f32, WeaponKey u16, Buttons u16</c> — 19 bytes.
        /// <see cref="Buttons"/> is written <b>before</b> the conditional blocks because
        /// <see cref="ReadFrom"/> branches on it. Conditional tail: when
        /// <see cref="Button.InteractPressed"/> is set, <c>TargetRoomHash u64, TargetObjectIndex
        /// i32</c> (+12); when <see cref="Button.TeleportHeld"/> is set, <c>CursorWorldX f32,
        /// CursorWorldY f32</c> (+8). A tick with neither flag costs 19 bytes; the maximum is 39.
        /// </para>
        ///
        /// <para><b>Why the tail is conditional.</b> The interact target is meaningful only on the
        /// edge's tick — carrying it every tick was 12 bytes of "no target" — and the cursor is read
        /// only while a teleport charge is held (the host-side weapon mount aims off
        /// <see cref="AimAngle"/>, never the cursor; <c>PfeRemoteBody.Drive</c> forwards the cursor
        /// only while the level is held). A batch is a run of these variable-width records back to
        /// back; the read is sequential-exact, so a truncated payload still throws
        /// <see cref="EndOfStreamException"/> rather than fabricating a command — the strict-read
        /// contract of Phase 5 is unchanged.</para>
        ///
        /// <para><b>Why the codec lives on the command.</b> The readiness plan's §9 shape and the
        /// batch payload both need the same byte order. A second copy in an adapter would be a second
        /// definition of "one command" that could drift without either side noticing.</para>
        /// </summary>
        public readonly void WriteTo(BinaryWriter writer)
        {
            writer.Write(TickIndex);
            writer.Write(SlotIndex);
            writer.Write(Sequence);
            writer.Write(MoveX);
            writer.Write(MoveY);
            writer.Write(AimAngle);
            writer.Write(WeaponKey);
            writer.Write((ushort)Buttons);

            // The read branches on Buttons, so both blocks must come after it — in this exact order.
            if ((Buttons & Button.InteractPressed) != 0)
            {
                writer.Write(TargetRoomHash);
                writer.Write(TargetObjectIndex);
            }
            if ((Buttons & Button.TeleportHeld) != 0)
            {
                writer.Write(CursorWorldX);
                writer.Write(CursorWorldY);
            }
        }

        /// <summary>Reads one command in the exact order <see cref="WriteTo"/> wrote it: the fixed
        /// head, then each conditional block iff the corresponding button bit is set. Absent fields
        /// read as zero — a command that never aimed a teleport carries a zero cursor, which the
        /// host refuses exactly like a local "aimed nowhere".</summary>
        public static PlayerCommand ReadFrom(BinaryReader reader)
        {
            var command = new PlayerCommand
            {
                TickIndex = reader.ReadInt32(),
                SlotIndex = reader.ReadByte(),
                Sequence = reader.ReadUInt32(),
                MoveX = reader.ReadSByte(),
                MoveY = reader.ReadSByte(),
                AimAngle = reader.ReadSingle(),
                WeaponKey = reader.ReadUInt16(),
                Buttons = (Button)reader.ReadUInt16(),
            };

            if ((command.Buttons & Button.InteractPressed) != 0)
            {
                command.TargetRoomHash = reader.ReadUInt64();
                command.TargetObjectIndex = reader.ReadInt32();
            }
            if ((command.Buttons & Button.TeleportHeld) != 0)
            {
                command.CursorWorldX = reader.ReadSingle();
                command.CursorWorldY = reader.ReadSingle();
            }
            return command;
        }

        public readonly bool Equals(PlayerCommand other)
        {
            return TickIndex == other.TickIndex
                && SlotIndex == other.SlotIndex
                && Sequence == other.Sequence
                && MoveX == other.MoveX
                && MoveY == other.MoveY
                && AimAngle.Equals(other.AimAngle)
                && CursorWorldX.Equals(other.CursorWorldX)
                && CursorWorldY.Equals(other.CursorWorldY)
                && TargetRoomHash == other.TargetRoomHash
                && TargetObjectIndex == other.TargetObjectIndex
                && WeaponKey == other.WeaponKey
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
                hash = (hash * 397) ^ CursorWorldX.GetHashCode();
                hash = (hash * 397) ^ CursorWorldY.GetHashCode();
                hash = (hash * 397) ^ TargetRoomHash.GetHashCode();
                hash = (hash * 397) ^ TargetObjectIndex;
                hash = (hash * 397) ^ WeaponKey;
                hash = (hash * 397) ^ (int)Buttons;
                return hash;
            }
        }

        public override readonly string ToString()
        {
            return $"PlayerCommand(tick={TickIndex} slot={SlotIndex} seq={Sequence} " +
                   $"move=({MoveXFloat:F2},{MoveYFloat:F2}) aim={AimAngle:F1} buttons={Buttons} " +
                   $"target=({TargetRoomHash:X16},{TargetObjectIndex}) weaponKey={WeaponKey})";
        }
    }
}
