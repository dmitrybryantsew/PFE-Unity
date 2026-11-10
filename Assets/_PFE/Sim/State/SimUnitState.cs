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

        // ── Pose (AS3 frame-driven; separated from motion per F-12) ─────────────
        /// <summary>The animation pose (AS3 <c>pose</c>) as a content id. Pose is replicated
        /// separately from motion so a naive transform lerp cannot desync animation from terrain.</summary>
        [SimReconcile(SimReconcileRule.HoldPhase)]
        public byte PoseId;
        /// <summary>Frame within the pose, advancing at the AS3 frame rate.</summary>
        [SimReconcile(SimReconcileRule.HoldPhase)]
        public ushort PoseFrame;
        /// <summary>Wrap-safe pose sequence, so a stale pose packet is rejected (Remains
        /// <c>poseSequence</c>).</summary>
        [SimReconcile(SimReconcileRule.RejectIfStale)]
        public uint PoseSequence;

        // ── Wire serialization (Phase 1.4) ────────────────────────────────────

        /// <summary>Appends this record to <paramref name="writer"/> in declaration order.</summary>
        public readonly void WriteTo(System.IO.BinaryWriter writer)
        {
            writer.Write(EntityHash);
            writer.Write(Generation);
            writer.Write(LifecycleSequence);
            writer.Write(ControllerSlot);
            writer.Write(DefinitionId);
            writer.Write(Px); writer.Write(Py);
            writer.Write(FacingDirection);
            writer.Write(Vx); writer.Write(Vy);
            writer.Write(IsGrounded); writer.Write(DropThroughPlatforms);
            writer.Write(OverhangLeft); writer.Write(OverhangRight);
            writer.Write(SupportCarryX); writer.Write(SupportCarryY);
            writer.Write(Health); writer.Write(MaxHealth);
            writer.Write(HasRuntimeHealth);
            writer.Write(IsDead); writer.Write(IsDown); writer.Write(IsDissolving);
            writer.Write(DissolveProgress);
            writer.Write(Faction);
            writer.Write(Digger);
            writer.Write(AmbushArmed);
            writer.Write(PoseId);
            writer.Write(PoseFrame);
            writer.Write(PoseSequence);
        }

        /// <summary>Reads one record from <paramref name="reader"/> in the same order
        /// <see cref="WriteTo"/> wrote it.</summary>
        public static SimUnitState ReadFrom(System.IO.BinaryReader reader)
        {
            return new SimUnitState
            {
                EntityHash = reader.ReadUInt64(),
                Generation = reader.ReadInt32(),
                LifecycleSequence = reader.ReadInt32(),
                ControllerSlot = reader.ReadInt32(),
                DefinitionId = reader.ReadUInt16(),
                Px = reader.ReadSingle(), Py = reader.ReadSingle(),
                FacingDirection = reader.ReadInt32(),
                Vx = reader.ReadSingle(), Vy = reader.ReadSingle(),
                IsGrounded = reader.ReadBoolean(), DropThroughPlatforms = reader.ReadBoolean(),
                OverhangLeft = reader.ReadSingle(), OverhangRight = reader.ReadSingle(),
                SupportCarryX = reader.ReadSingle(), SupportCarryY = reader.ReadSingle(),
                Health = reader.ReadSingle(), MaxHealth = reader.ReadSingle(),
                HasRuntimeHealth = reader.ReadBoolean(),
                IsDead = reader.ReadBoolean(), IsDown = reader.ReadBoolean(),
                IsDissolving = reader.ReadBoolean(),
                DissolveProgress = reader.ReadSingle(),
                Faction = reader.ReadInt32(),
                Digger = reader.ReadInt32(),
                AmbushArmed = reader.ReadBoolean(),
                PoseId = reader.ReadByte(),
                PoseFrame = reader.ReadUInt16(),
                PoseSequence = reader.ReadUInt32(),
            };
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
                && PoseSequence == other.PoseSequence;
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
