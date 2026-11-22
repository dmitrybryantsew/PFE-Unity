using System;

namespace PFE.Sim.State
{
    /// <summary>
    /// The serializable truth of one unit for one tick — the per-entity value a host snapshot
    /// replicates inside a <c>SimRoomDelta</c>.
    ///
    /// <para><b>F-01/F-02, Phase 1.3 of the multiplayer readiness plan.</b> §9 of the readiness doc
    /// sketches the shape; this is the concrete record. It is pure C#: no <c>UnityEngine.Object</c>,
    /// no <c>Transform</c>, no <c>Vector2</c> — positions are two floats in room-local pixels (the
    /// space <c>UnitInstance.position</c> already lives in, and the same space
    /// <c>RoomInstance.droppedItems</c> uses, so a unit and the loot it drops agree). The wire carries
    /// the entity as <see cref="EntityHash"/> (<c>EntityId.Hash</c>, FNV-1a 64); the debug string
    /// stays in a side table (§9).</para>
    ///
    /// <para><b>Where the field list comes from.</b> Two sources, per §8 row 1.3's
    /// "<c>UnitInstance</c> + live-only unit fields". The persistent half is the subset of
    /// <c>UnitInstance</c> that is <i>mutable truth</i> rather than authored placement: identity,
    /// <c>unitType</c> (as an interned id), position, facing, health and its latch, dead, faction,
    /// digger/ambush. The live-only half is the corpse/dissolve lifecycle <c>UnitController</c> owns
    /// and the room record does not (down/dissolving/dissolve-progress), plus the
    /// generation/lifecycle metadata F-06 requires on every replicated entity and the controller-slot
    /// ownership F-07 requires to say "this body is driven by player N, or by host AI".</para>
    ///
    /// <para><b>Phase 1.5 (F-02) added the motor-less unit's live motion/gait half</b> —
    /// <c>Vx/Vy</c>, <c>IsGrounded</c>, <c>DropThroughPlatforms</c>, <c>OverhangLeft/Right</c>,
    /// <c>SupportCarryX/Y</c> — because <c>UnitController</c> owns a second physics path for units
    /// with no <c>TilePhysicsController</c>. Those fields are now *live* truth: the controller's
    /// forwarding properties read and write this record. A unit with a motor keeps its motion in
    /// <see cref="SimMotorState"/> and leaves these at their defaults; the two are not summed.</para>
    ///
    /// <para><b>What is deliberately NOT here.</b> The motor is a separate record
    /// (<see cref="SimMotorState"/>), keyed by the same <see cref="EntityHash"/> — a unit's gait and
    /// contact flags are predicted/corrected on a different cadence than its health, and merging them
    /// would force one reconciliation rule onto two different kinds of truth (§10). Per-tick
    /// <i>intent</i> and AI target selection are inputs, not state. The authored placement —
    /// <c>attributes</c>, <c>items</c>, <c>scripts</c>, <c>uid</c>/<c>code</c>, <c>controllerId</c> —
    /// is content, static for the room's lifetime, already carried by the save baseline
    /// (<c>RoomStateSnapshot</c>) and never replicated per tick (§10 — "content-version constant").
    /// Noise/stealth/suspicion, speech cooldowns and contact-invulnerability are combat-resolution
    /// inputs owned by systems that are not yet part of the replicated motor/unit core; they join this
    /// record when their owning system is extracted (Phase 2+), not before.</para>
    ///
    /// <para><b>Authority.</b> The whole record is host-authoritative: the host simulates every unit
    /// (player-driven or AI) and clients receive the result. The one exception is the owning player's
    /// <i>motor</i>, which is <see cref="SimMotorState"/> and client-predicted — that distinction is
    /// why the two records are separate. Reconciliation (§10): <c>Health</c>/<c>MaxHealth</c>/
    /// <c>StatusFlags</c> snap from host with monotonic guards on death; <c>PoseId</c>/<c>PoseFrame</c>/
    /// <c>PoseSequence</c> hold phase and resync on drift; identity metadata is reject-if-stale.</para>
    /// </summary>
    [Serializable]
    public struct SimUnitState : IEquatable<SimUnitState>
    {
        /// <summary>
        /// <see cref="ControllerSlot"/> value meaning "no player drives this body; the host's AI does".
        /// A named constant because the alternative — a bare −1 scattered across the unit, ownership
        /// and projection layers — is how a default-constructed record ends up claiming to be player 0.
        /// </summary>
        public const int HostAiControllerSlot = -1;

        // ── Identity / lifetime (F-05, F-06, F-07) ──────────────────────────────
        /// <summary>The entity this unit is, as <c>EntityId.Hash</c> (FNV-1a 64). Stable across a
        /// room transition because the room record (<c>UnitInstance.entityId</c>) owns it.</summary>
        [SimReconcile(SimReconcileRule.Key)]
        public ulong EntityHash;
        /// <summary>Bumped when the entity is destroyed and its id could be reused; a delayed packet
        /// from a previous lifetime is rejected when this differs (Remains <c>ActorState.generation</c>).</summary>
        [SimReconcile(SimReconcileRule.ReliableLifecycleEvent)]
        public int Generation;
        /// <summary>Bumped on every spawn/despawn of the same id; with <see cref="Generation"/> this is
        /// the stale-lifetime guard of <c>ActorVersion.CanPublishOwnerState</c>.</summary>
        [SimReconcile(SimReconcileRule.ReliableLifecycleEvent)]
        public int LifecycleSequence;
        /// <summary>Which player slot drives this body, −1 for host AI. This is the ownership model
        /// F-07 reduces to under host-authoritative sim: it says whose <c>PlayerCommand</c> steers the
        /// motor, and which client may predict it. Nothing else is owned.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public int ControllerSlot;

        // ── Content identity (interned, content-versioned) ──────────────────────
        /// <summary>The unit definition (<c>UnitInstance.unitType</c>) as an interned id into a
        /// content-versioned table — never a <c>string</c> on the wire (§9, F-05 caveat).</summary>
        [SimReconcile(SimReconcileRule.ContentConstant)]
        public ushort DefinitionId;

        // ── Position / facing (room-local pixels) ───────────────────────────────
        /// <summary>Feet position, room-local pixels (<c>UnitInstance.position.x</c>).</summary>
        [SimReconcile(SimReconcileRule.Interpolate)]
        public float Px;
        /// <summary>Feet position, room-local pixels (<c>UnitInstance.position.y</c>).</summary>
        [SimReconcile(SimReconcileRule.Interpolate)]
        public float Py;
        /// <summary>Resolved facing: +1 right, −1 left (<c>UnitInstance.facingDirection</c>).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public int FacingDirection;

        // ── Motor-less unit live motion / gait (Phase 1.5, F-02) ───────────────
        // The projection of UnitController's own physics path (a unit with no TilePhysicsController).
        // A *motored* unit keeps its velocity/contact truth in SimMotorState instead; these fields
        // stay at their defaults there. Kept as float/bool pairs rather than Vector2 so the record
        // stays pure C# (§8 row 1.3, and SimStateRecordPurityLintTests).
        /// <summary>Motor-less unit velocity, room-local px per AS3 frame (<c>_velocity.x</c>).</summary>
        [SimReconcile(SimReconcileRule.Interpolate)]
        public float Vx;
        /// <summary>Motor-less unit velocity, room-local px per AS3 frame (<c>_velocity.y</c>).</summary>
        [SimReconcile(SimReconcileRule.Interpolate)]
        public float Vy;
        /// <summary>Motor-less groundedness (<c>_isGrounded</c>). Snap from host, §10 flags rule.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public bool IsGrounded;
        /// <summary>AS3 <c>throu</c>: sustained "ignore one-way surfaces" level (<c>_dropThroughPlatforms</c>).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public bool DropThroughPlatforms;
        /// <summary>AS3 <c>shX1</c>: body fraction past the support's left edge (<c>_overhangLeft</c>).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float OverhangLeft;
        /// <summary>AS3 <c>shX2</c>: body fraction past the support's right edge (<c>_overhangRight</c>).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float OverhangRight;
        /// <summary>AS3 <c>osndx</c>: support-prop carry this tick (<c>_groundPropCarryPixels.x</c>).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float SupportCarryX;
        /// <summary>AS3 <c>osndy</c>: support-prop carry this tick (<c>_groundPropCarryPixels.y</c>).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float SupportCarryY;

        // ── Health / life ───────────────────────────────────────────────────────
        [SimReconcile(SimReconcileRule.SnapMonotonic)]
        public float Health;
        [SimReconcile(SimReconcileRule.Snap)]
        public float MaxHealth;
        /// <summary>The latch that says <see cref="Health"/>/<see cref="MaxHealth"/> describe a live
        /// unit rather than the field defaults (<c>UnitInstance.hasRuntimeHealth</c>). Without it a
        /// 100-HP default is indistinguishable from a real unit at full health.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public bool HasRuntimeHealth;

        // ── Status flags (dead / down / dissolve / ambush) ──────────────────────
        /// <summary>Dead (<c>UnitInstance.isDead</c>). Death is a reliable event, not a delta — but the
        /// flag rides the snapshot so a late joiner reconstructs the corpse.</summary>
        [SimReconcile(SimReconcileRule.SnapMonotonic)]
        public bool IsDead;
        /// <summary>Downed but not dead (AS3 <c>sost == 3</c> stagger). Host-owned status.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public bool IsDown;
        /// <summary>Corpse is dissolving (AS3 desintegrate). Host-owned; the view reads it to run the
        /// dissolve shader.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public bool IsDissolving;
        /// <summary>Dissolve progress 0..1, for a late joiner to resume a half-dissolved corpse.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float DissolveProgress;
        /// <summary>Faction, −1 when the placement authored none and the unit falls back to its
        /// definition (<c>UnitInstance.fraction</c> is <c>FactionType?</c>). Stored as a plain int so
        /// the record stays blittable; cast to <c>FactionType</c> at the boundary.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public int Faction;
        /// <summary>AS3 <c>UnitZombie.digger</c>: 0 never buries, 1 buried ambusher, 2 sharp ear,
        /// 3 inert. Non-zero only for the zombie family (<c>UnitInstance.digger</c>).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public int Digger;
        /// <summary>Resolved buried-ambush spawn (<c>UnitInstance.ambushArmed</c>). One-way: a risen
        /// ambusher never re-buries.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public bool AmbushArmed;

        // ── Avatar view state (animation / aim / weapon; separated from motion per F-12) ──
        /// <summary>The body animation state as a compiled-in id from the view-layer
        /// <c>RemoteAnimStateIds</c> table (the <c>CharacterAnimationDriver</c> state-name vocabulary).
        /// Replicated separately from motion so a naive transform lerp cannot desync animation from
        /// terrain. <c>RemoteAnimStateIds.Unknown</c> (255) means "the host's state has no agreed id";
        /// the receiver keeps its current state rather than guessing.</summary>
        [SimReconcile(SimReconcileRule.HoldPhase)]
        public byte PoseId;
        /// <summary>Frame within the animation state, sampled at the snapshot cadence.</summary>
        [SimReconcile(SimReconcileRule.HoldPhase)]
        public ushort PoseFrame;
        /// <summary>Wrap-safe pose sequence, so a stale pose packet is rejected (Remains
        /// <c>poseSequence</c>).</summary>
        [SimReconcile(SimReconcileRule.RejectIfStale)]
        public uint PoseSequence;

        /// <summary>
        /// The equipped weapon's content id as <c>SimSnapshotProjectionRules.LootItemKey(id, true)</c>
        /// (FNV-1a 64 of <c>"weapon:" + weaponId</c>) — the same hash
        /// <see cref="SimDroppedItem"/> uses, so a held weapon and its dropped twin speak one id
        /// language. Zero means "nothing equipped" (unarmed). The owning machine is the only
        /// authority on what is in hand: it flows player→host as the interned key
        /// <c>PlayerCommand.WeaponKey</c> (converted back to this hash at the host boundary, v10)
        /// and host→client on this field.
        /// </summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public ulong WeaponIdHash;

        /// <summary>
        /// Aim facing in degrees, the <c>PlayerCommand.AimAngle</c> convention: world-space
        /// <c>atan2(dir.y, dir.x)</c>, −90 = straight up, 0 = +X, +90 = straight down. The view
        /// consumes it directly (weapon barrel rotation, the standing-aim frame sub-range), so it
        /// interpolates like a position rather than snapping like a flag.
        /// </summary>
        [SimReconcile(SimReconcileRule.Interpolate)]
        public float AimAngle;

        // ── Wire serialization (Phase 1.4; v10 codec: packed flags + quantized scalars) ──
        //
        // The public fields are unchanged since Phase 1.4 — only the ENCODING changed (protocol v10).
        // Layout, little-endian:
        //   EntityHash u64, Generation i32, LifecycleSequence i32,
        //   ControllerSlotBias u16 (ControllerSlot + 1; 0 = HostAiControllerSlot; the admitted slot
        //     range is [-1, 255] per SimCommandAdmissionRules.TrySlotForPeer, which a biased byte
        //     cannot hold, hence u16), DefinitionId u16, Flags u8,
        //   Px f32, Py f32, FacingDirection i8,
        //   Vx f32, Vy f32,
        //   OverhangLeft f32, OverhangRight f32,      // NOT quantized — values are negative when the
        //                                             // side is fully supported and unbounded above 1
        //   SupportCarryX f32, SupportCarryY f32,
        //   Health f32, MaxHealth f32,
        //   DissolveProgress u8 (0..1 contract * 255, rounded; max wire error 1/510),
        //   Faction i8 (FactionType + the -1 "authored none" default; both fit sbyte),
        //   Digger u8 (the AS3 dig attribute, contract 0..3),
        //   PoseId u8, PoseFrame u16, PoseSequence u32,
        //   WeaponIdHash u64 (the content hash stays a hash — only the COMMAND carries the interned key),
        //   AimAngle i16 (fixed point, 1/64 degree; atan2's ±180° maps to ±11 520, far inside short;
        //     max wire error 1/128 degree)
        //
        // Fields whose gameplay range cannot be proven from code (positions, velocities, health,
        // support carry, overhang) stay f32 — a clamp firing in gameplay would be a desync.

        /// <summary>Flags-byte bit for <see cref="IsGrounded"/> — LSB-first order, bit 0.</summary>
        private const byte FlagIsGrounded = 1 << 0;
        /// <summary>Flags-byte bit for <see cref="DropThroughPlatforms"/> — bit 1.</summary>
        private const byte FlagDropThroughPlatforms = 1 << 1;
        /// <summary>Flags-byte bit for <see cref="HasRuntimeHealth"/> — bit 2.</summary>
        private const byte FlagHasRuntimeHealth = 1 << 2;
        /// <summary>Flags-byte bit for <see cref="IsDead"/> — bit 3.</summary>
        private const byte FlagIsDead = 1 << 3;
        /// <summary>Flags-byte bit for <see cref="IsDown"/> — bit 4.</summary>
        private const byte FlagIsDown = 1 << 4;
        /// <summary>Flags-byte bit for <see cref="IsDissolving"/> — bit 5.</summary>
        private const byte FlagIsDissolving = 1 << 5;
        /// <summary>Flags-byte bit for <see cref="AmbushArmed"/> — bit 6. Bit 7 is reserved and
        /// written 0, so a v10 reader can tell a flag byte with future bits from a corrupt one.</summary>
        private const byte FlagAmbushArmed = 1 << 6;

        /// <summary><see cref="DissolveProgress"/> wire scale: the 0..1 contract maps onto 0..255.</summary>
        private const float DissolveWireScale = 255f;
        /// <summary><see cref="AimAngle"/> wire scale: 64 fixed-point ticks per degree.</summary>
        public const float AimAngleWireScale = 64f;

        /// <summary>Appends this record to <paramref name="writer"/> in the v10 layout documented
        /// above the flag constants.</summary>
        public readonly void WriteTo(System.IO.BinaryWriter writer)
        {
            writer.Write(EntityHash);
            writer.Write(Generation);
            writer.Write(LifecycleSequence);
            writer.Write((ushort)(ControllerSlot + 1));
            writer.Write(DefinitionId);
            writer.Write(BuildFlags());
            writer.Write(Px); writer.Write(Py);
            writer.Write((sbyte)FacingDirection);
            writer.Write(Vx); writer.Write(Vy);
            writer.Write(OverhangLeft); writer.Write(OverhangRight);
            writer.Write(SupportCarryX); writer.Write(SupportCarryY);
            writer.Write(Health); writer.Write(MaxHealth);
            writer.Write(EncodeDissolveProgress(DissolveProgress));
            writer.Write((sbyte)Faction);
            writer.Write((byte)Digger);
            writer.Write(PoseId);
            writer.Write(PoseFrame);
            writer.Write(PoseSequence);
            writer.Write(WeaponIdHash);
            writer.Write(EncodeAimAngle(AimAngle));
        }

        /// <summary>Reads one record from <paramref name="reader"/> in the same order
        /// <see cref="WriteTo"/> wrote it.</summary>
        public static SimUnitState ReadFrom(System.IO.BinaryReader reader)
        {
            ulong entityHash = reader.ReadUInt64();
            int generation = reader.ReadInt32();
            int lifecycleSequence = reader.ReadInt32();
            ushort slotBias = reader.ReadUInt16();
            if (slotBias > 256)
            {
                throw new System.IO.InvalidDataException(
                    $"Corrupt unit record: controller slot bias {slotBias} is out of range " +
                    "[0, 256] (slot [-1, 255]).");
            }
            ushort definitionId = reader.ReadUInt16();
            byte flags = reader.ReadByte();

            var state = new SimUnitState
            {
                EntityHash = entityHash,
                Generation = generation,
                LifecycleSequence = lifecycleSequence,
                ControllerSlot = slotBias - 1,
                DefinitionId = definitionId,
                Px = reader.ReadSingle(), Py = reader.ReadSingle(),
                FacingDirection = reader.ReadSByte(),
                Vx = reader.ReadSingle(), Vy = reader.ReadSingle(),
                OverhangLeft = reader.ReadSingle(), OverhangRight = reader.ReadSingle(),
                SupportCarryX = reader.ReadSingle(), SupportCarryY = reader.ReadSingle(),
                Health = reader.ReadSingle(), MaxHealth = reader.ReadSingle(),
                DissolveProgress = reader.ReadByte() / DissolveWireScale,
                Faction = reader.ReadSByte(),
                Digger = reader.ReadByte(),
                PoseId = reader.ReadByte(),
                PoseFrame = reader.ReadUInt16(),
                PoseSequence = reader.ReadUInt32(),
                WeaponIdHash = reader.ReadUInt64(),
                AimAngle = reader.ReadInt16() / AimAngleWireScale,
            };

            state.IsGrounded = (flags & FlagIsGrounded) != 0;
            state.DropThroughPlatforms = (flags & FlagDropThroughPlatforms) != 0;
            state.HasRuntimeHealth = (flags & FlagHasRuntimeHealth) != 0;
            state.IsDead = (flags & FlagIsDead) != 0;
            state.IsDown = (flags & FlagIsDown) != 0;
            state.IsDissolving = (flags & FlagIsDissolving) != 0;
            state.AmbushArmed = (flags & FlagAmbushArmed) != 0;
            return state;
        }

        private readonly byte BuildFlags()
        {
            byte flags = 0;
            if (IsGrounded) flags |= FlagIsGrounded;
            if (DropThroughPlatforms) flags |= FlagDropThroughPlatforms;
            if (HasRuntimeHealth) flags |= FlagHasRuntimeHealth;
            if (IsDead) flags |= FlagIsDead;
            if (IsDown) flags |= FlagIsDown;
            if (IsDissolving) flags |= FlagIsDissolving;
            if (AmbushArmed) flags |= FlagAmbushArmed;
            return flags;
        }

        /// <summary>Encodes the 0..1 dissolve contract onto a byte (value*255, rounded away from
        /// zero). The contract bounds the value; the clamp is defence against a corrupt producer,
        /// not a gameplay path.</summary>
        public static byte EncodeDissolveProgress(float progress)
        {
            float clamped = progress < 0f ? 0f : (progress > 1f ? 1f : progress);
            return (byte)Math.Round(clamped * DissolveWireScale, MidpointRounding.AwayFromZero);
        }

        /// <summary>Encodes degrees as 1/64-degree fixed point (round half away from zero).
        /// atan2's ±180° output maps to ±11 520 — comfortably inside short's ±32 767.</summary>
        public static short EncodeAimAngle(float angleDegrees)
        {
            return (short)Math.Round(angleDegrees * AimAngleWireScale, MidpointRounding.AwayFromZero);
        }

        public readonly bool Equals(SimUnitState other)
        {
            return EntityHash == other.EntityHash
                && Generation == other.Generation
                && LifecycleSequence == other.LifecycleSequence
                && ControllerSlot == other.ControllerSlot
                && DefinitionId == other.DefinitionId
                && Px.Equals(other.Px) && Py.Equals(other.Py)
                && FacingDirection == other.FacingDirection
                && Vx.Equals(other.Vx) && Vy.Equals(other.Vy)
                && IsGrounded == other.IsGrounded
                && DropThroughPlatforms == other.DropThroughPlatforms
                && OverhangLeft.Equals(other.OverhangLeft)
                && OverhangRight.Equals(other.OverhangRight)
                && SupportCarryX.Equals(other.SupportCarryX)
                && SupportCarryY.Equals(other.SupportCarryY)
                && Health.Equals(other.Health) && MaxHealth.Equals(other.MaxHealth)
                && HasRuntimeHealth == other.HasRuntimeHealth
                && IsDead == other.IsDead
                && IsDown == other.IsDown
                && IsDissolving == other.IsDissolving
                && DissolveProgress.Equals(other.DissolveProgress)
                && Faction == other.Faction
                && Digger == other.Digger
                && AmbushArmed == other.AmbushArmed
                && PoseId == other.PoseId
                && PoseFrame == other.PoseFrame
                && PoseSequence == other.PoseSequence
                && WeaponIdHash == other.WeaponIdHash
                && AimAngle.Equals(other.AimAngle);
        }

        public override readonly bool Equals(object obj) => obj is SimUnitState other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked
            {
                int hash = EntityHash.GetHashCode();
                hash = (hash * 397) ^ Generation;
                hash = (hash * 397) ^ LifecycleSequence;
                hash = (hash * 397) ^ ControllerSlot;
                hash = (hash * 397) ^ DefinitionId.GetHashCode();
                hash = (hash * 397) ^ Px.GetHashCode();
                hash = (hash * 397) ^ Py.GetHashCode();
                hash = (hash * 397) ^ FacingDirection;
                hash = (hash * 397) ^ Vx.GetHashCode();
                hash = (hash * 397) ^ Vy.GetHashCode();
                hash = (hash * 397) ^ IsGrounded.GetHashCode();
                hash = (hash * 397) ^ DropThroughPlatforms.GetHashCode();
                hash = (hash * 397) ^ OverhangLeft.GetHashCode();
                hash = (hash * 397) ^ OverhangRight.GetHashCode();
                hash = (hash * 397) ^ SupportCarryX.GetHashCode();
                hash = (hash * 397) ^ SupportCarryY.GetHashCode();
                hash = (hash * 397) ^ Health.GetHashCode();
                hash = (hash * 397) ^ MaxHealth.GetHashCode();
                hash = (hash * 397) ^ HasRuntimeHealth.GetHashCode();
                hash = (hash * 397) ^ IsDead.GetHashCode();
                hash = (hash * 397) ^ IsDown.GetHashCode();
                hash = (hash * 397) ^ IsDissolving.GetHashCode();
                hash = (hash * 397) ^ DissolveProgress.GetHashCode();
                hash = (hash * 397) ^ Faction;
                hash = (hash * 397) ^ Digger;
                hash = (hash * 397) ^ AmbushArmed.GetHashCode();
                hash = (hash * 397) ^ PoseId.GetHashCode();
                hash = (hash * 397) ^ PoseFrame.GetHashCode();
                hash = (hash * 397) ^ (int)PoseSequence;
                hash = (hash * 397) ^ WeaponIdHash.GetHashCode();
                hash = (hash * 397) ^ AimAngle.GetHashCode();
                return hash;
            }
        }

        public override readonly string ToString()
        {
            return $"SimUnitState(ent={EntityHash:X16} def={DefinitionId} slot={ControllerSlot} " +
                   $"pos=({Px:F1},{Py:F1}) hp={Health:F0}/{MaxHealth:F0} dead={IsDead} face={FacingDirection})";
        }
    }
}
