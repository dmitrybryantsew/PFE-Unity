using System;

namespace PFE.Sim.State
{
    /// <summary>
    /// The serializable truth of one movement motor for one tick — the value a host snapshot
    /// replicates and a client prediction re-simulates.
    ///
    /// <para><b>F-01/F-02, Phase 1.2 of the multiplayer readiness plan.</b> This is the state record
    /// the readiness doc (§5, §9) says PFE is missing: today the motor's truth lives in ~40 private
    /// <c>MonoBehaviour</c> fields on <c>TilePhysicsController</c>, so there is nothing to serialize
    /// and nothing to predict against. This struct is that truth made a value type. It is pure C#:
    /// no <c>UnityEngine.Object</c>, no <c>Transform</c>, no room reference — positions are world
    /// pixels (PFE's canonical Sim space, per <c>TileQueryConstants</c>) and velocity is AS3's own
    /// unit, pixels per canonical 30 Hz frame.</para>
    ///
    /// <para><b>Field list.</b> The contact/gait flags, position, velocity and facing come straight
    /// from <c>MovementMotorState</c> (the existing read-only projection). The rest are the hidden
    /// tick-relevant fields the readiness doc names (§8 row 1.2): <c>PendingVerticalImpulse</c>,
    /// <c>DashTimer</c>, <c>DashVelocityX/Y</c>, <c>PlatformDropTimer</c>, <c>ActiveLadderDirection</c>,
    /// <c>ActiveLadderSnapX</c>, <c>GravityScale</c> and <c>SupportCarryX/Y</c>. Every one of these is
    /// mutated during <c>StepMotor()</c> and read by a later step, so a snapshot that omitted one would
    /// not reproduce the motor's next tick.</para>
    ///
    /// <para><b>What is deliberately NOT here.</b> Per-tick <i>intent</i> (<c>desiredHorizontalSpeed</c>,
    /// <c>ladderInputY</c>, <c>wantsToCrouch</c>, <c>upHeld</c>, <c>dropThroughPlatforms</c>…) is input,
    /// carried by <c>PFE.Core.Input.PlayerCommand</c>, not state — replicating it would double the
    /// command channel. Content constants (collision size, acceleration, friction, gravity multipliers)
    /// are content-versioned and never replicated per tick (§10). And derived caches
    /// (<c>standingCollisionHeight</c>, <c>resolvedCrouchedCollisionHeight</c>) are recomputed from
    /// content, so sending them would only add a way for two peers to disagree.</para>
    ///
    /// <para><b>Authority.</b> The whole record is <c>[ClientPredicted]</c>: a client predicts its own
    /// entity's motor to hide latency and must accept a host correction (§10 — re-simulate from the
    /// acked tick, never accept a raw host position over a valid prediction without replay). Remote
    /// entities' motors are interpolated, not predicted; that distinction lives in the replication
    /// layer (Phase 3), not in this record.</para>
    /// </summary>
    [Serializable]
    public struct SimMotorState : IEquatable<SimMotorState>
    {
        // ── Identity ────────────────────────────────────────────────────────────
        /// <summary>The entity this motor belongs to, as <c>EntityId.Hash</c> (FNV-1a 64). The wire
        /// carries the hash only; <c>EntityId.DebugString</c> is a string and stays in a debug-side
        /// table (§9).</summary>
        [SimReconcile(SimReconcileRule.Key)]
        public ulong EntityHash;

        // ── Position / velocity (AS3 pixel space) ───────────────────────────────
        /// <summary>Collider feet position, world pixels (<c>posX</c>). Feet-anchored, matching AS3
        /// <c>Y1 = Y - scY</c>.</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float Px;
        /// <summary>Collider feet position, world pixels (<c>posY</c>).</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float Py;
        /// <summary>Horizontal velocity, pixels per canonical 30 Hz frame (<c>dx</c>).</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float Vx;
        /// <summary>Vertical velocity, pixels per canonical 30 Hz frame (<c>dy</c>).</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float Vy;

        // ── Contact / gait flags ────────────────────────────────────────────────
        [SimReconcile(SimReconcileRule.Resimulate)]
        public bool IsGrounded;
        /// <summary>Standing on a one-way surface (AS3 <c>stayPhis == 2</c>), as opposed to solid
        /// ground. Drives the crouch-vs-drop-through distinction.</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public bool IsOnPlatform;
        [SimReconcile(SimReconcileRule.Resimulate)]
        public bool IsInWater;
        /// <summary>75%-height sample inside water (AS3 <c>isPlav</c>).</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public bool IsFullySubmerged;
        [SimReconcile(SimReconcileRule.Resimulate)]
        public bool IsOnLadder;
        [SimReconcile(SimReconcileRule.Resimulate)]
        public bool IsCrouching;
        [SimReconcile(SimReconcileRule.Resimulate)]
        public bool HitCeiling;
        [SimReconcile(SimReconcileRule.Resimulate)]
        public bool WallLeft;
        [SimReconcile(SimReconcileRule.Resimulate)]
        public bool WallRight;
        /// <summary>AS3 <c>storona</c>: +1 right, −1 left.</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public int FacingDirection;

        // ── Tick-relevant hidden motor state (§8 row 1.2) ───────────────────────
        /// <summary>Accumulated vertical impulse awaiting the next step (swimming), pixels per
        /// canonical frame. Consumed — added to <see cref="Vx"/> / <see cref="Vy"/> — at the top of a
        /// step.</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float PendingVerticalImpulse;
        /// <summary>Seconds of dash remaining; while &gt; 0 the dash velocity overrides input.</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float DashTimer;
        /// <summary>The dash's locked-in horizontal velocity (px/frame), applied while
        /// <see cref="DashTimer"/> &gt; 0.</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float DashVelocityX;
        /// <summary>The dash's locked-in vertical velocity (px/frame).</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float DashVelocityY;
        /// <summary>Seconds one-way platforms stay disabled after a drop-through request.</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float PlatformDropTimer;
        /// <summary>Mounted side of the active ladder (AS3 <c>stair</c>): −1 / +1, 0 when not on a
        /// ladder. Ladders own the facing while attached.</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public int ActiveLadderDirection;
        /// <summary>World-pixel X the body snaps to while on the active ladder.</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float ActiveLadderSnapX;
        /// <summary>Jump/fall tuning multiplier on gravity (AS3 has no counterpart inside the lift
        /// branch; applied only on the falling arm).</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float GravityScale;
        /// <summary>Support's last-tick horizontal displacement, room-local pixels (AS3
        /// <c>osndx</c>), carried into this step's move.</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float SupportCarryX;
        /// <summary>Support's last-tick vertical displacement, room-local pixels (AS3
        /// <c>osndy</c>).</summary>
        [SimReconcile(SimReconcileRule.Resimulate)]
        public float SupportCarryY;

        // ── Wire serialization (Phase 1.4) ────────────────────────────────────
        // Field order on the wire is the declaration order above; both ends must agree, which is why
        // the serializer is versioned. Little-endian, deterministic, no padding.

        /// <summary>Appends this record to <paramref name="writer"/> in declaration order.</summary>
        public readonly void WriteTo(System.IO.BinaryWriter writer)
        {
            writer.Write(EntityHash);
            writer.Write(Px); writer.Write(Py);
            writer.Write(Vx); writer.Write(Vy);
            writer.Write(IsGrounded); writer.Write(IsOnPlatform); writer.Write(IsInWater);
            writer.Write(IsFullySubmerged); writer.Write(IsOnLadder); writer.Write(IsCrouching);
            writer.Write(HitCeiling); writer.Write(WallLeft); writer.Write(WallRight);
            writer.Write(FacingDirection);
            writer.Write(PendingVerticalImpulse);
            writer.Write(DashTimer);
            writer.Write(DashVelocityX); writer.Write(DashVelocityY);
            writer.Write(PlatformDropTimer);
            writer.Write(ActiveLadderDirection);
            writer.Write(ActiveLadderSnapX);
            writer.Write(GravityScale);
            writer.Write(SupportCarryX); writer.Write(SupportCarryY);
        }

        /// <summary>Reads one record from <paramref name="reader"/> in the same order
        /// <see cref="WriteTo"/> wrote it.</summary>
        public static SimMotorState ReadFrom(System.IO.BinaryReader reader)
        {
            return new SimMotorState
            {
                EntityHash = reader.ReadUInt64(),
                Px = reader.ReadSingle(), Py = reader.ReadSingle(),
                Vx = reader.ReadSingle(), Vy = reader.ReadSingle(),
                IsGrounded = reader.ReadBoolean(), IsOnPlatform = reader.ReadBoolean(),
                IsInWater = reader.ReadBoolean(), IsFullySubmerged = reader.ReadBoolean(),
                IsOnLadder = reader.ReadBoolean(), IsCrouching = reader.ReadBoolean(),
                HitCeiling = reader.ReadBoolean(), WallLeft = reader.ReadBoolean(),
                WallRight = reader.ReadBoolean(),
                FacingDirection = reader.ReadInt32(),
                PendingVerticalImpulse = reader.ReadSingle(),
                DashTimer = reader.ReadSingle(),
                DashVelocityX = reader.ReadSingle(), DashVelocityY = reader.ReadSingle(),
                PlatformDropTimer = reader.ReadSingle(),
                ActiveLadderDirection = reader.ReadInt32(),
                ActiveLadderSnapX = reader.ReadSingle(),
                GravityScale = reader.ReadSingle(),
                SupportCarryX = reader.ReadSingle(), SupportCarryY = reader.ReadSingle(),
            };
        }

        public readonly bool Equals(SimMotorState other)
        {
            return EntityHash == other.EntityHash
                && Px.Equals(other.Px) && Py.Equals(other.Py)
                && Vx.Equals(other.Vx) && Vy.Equals(other.Vy)
                && IsGrounded == other.IsGrounded
                && IsOnPlatform == other.IsOnPlatform
                && IsInWater == other.IsInWater
                && IsFullySubmerged == other.IsFullySubmerged
                && IsOnLadder == other.IsOnLadder
                && IsCrouching == other.IsCrouching
                && HitCeiling == other.HitCeiling
                && WallLeft == other.WallLeft
                && WallRight == other.WallRight
                && FacingDirection == other.FacingDirection
                && PendingVerticalImpulse.Equals(other.PendingVerticalImpulse)
                && DashTimer.Equals(other.DashTimer)
                && DashVelocityX.Equals(other.DashVelocityX)
                && DashVelocityY.Equals(other.DashVelocityY)
                && PlatformDropTimer.Equals(other.PlatformDropTimer)
                && ActiveLadderDirection == other.ActiveLadderDirection
                && ActiveLadderSnapX.Equals(other.ActiveLadderSnapX)
                && GravityScale.Equals(other.GravityScale)
                && SupportCarryX.Equals(other.SupportCarryX)
                && SupportCarryY.Equals(other.SupportCarryY);
        }

        public override readonly bool Equals(object obj) => obj is SimMotorState other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked
            {
                int hash = EntityHash.GetHashCode();
                hash = (hash * 397) ^ Px.GetHashCode();
                hash = (hash * 397) ^ Py.GetHashCode();
                hash = (hash * 397) ^ Vx.GetHashCode();
                hash = (hash * 397) ^ Vy.GetHashCode();
                hash = (hash * 397) ^ IsGrounded.GetHashCode();
                hash = (hash * 397) ^ IsOnPlatform.GetHashCode();
                hash = (hash * 397) ^ IsInWater.GetHashCode();
                hash = (hash * 397) ^ IsFullySubmerged.GetHashCode();
                hash = (hash * 397) ^ IsOnLadder.GetHashCode();
                hash = (hash * 397) ^ IsCrouching.GetHashCode();
                hash = (hash * 397) ^ HitCeiling.GetHashCode();
                hash = (hash * 397) ^ WallLeft.GetHashCode();
                hash = (hash * 397) ^ WallRight.GetHashCode();
                hash = (hash * 397) ^ FacingDirection;
                hash = (hash * 397) ^ PendingVerticalImpulse.GetHashCode();
                hash = (hash * 397) ^ DashTimer.GetHashCode();
                hash = (hash * 397) ^ DashVelocityX.GetHashCode();
                hash = (hash * 397) ^ DashVelocityY.GetHashCode();
                hash = (hash * 397) ^ PlatformDropTimer.GetHashCode();
                hash = (hash * 397) ^ ActiveLadderDirection;
                hash = (hash * 397) ^ ActiveLadderSnapX.GetHashCode();
                hash = (hash * 397) ^ GravityScale.GetHashCode();
                hash = (hash * 397) ^ SupportCarryX.GetHashCode();
                hash = (hash * 397) ^ SupportCarryY.GetHashCode();
                return hash;
            }
        }

        public override readonly string ToString()
        {
            return $"SimMotorState(ent={EntityHash:X16} pos=({Px:F1},{Py:F1}) vel=({Vx:F2},{Vy:F2}) " +
                   $"face={FacingDirection} grounded={IsGrounded} ladder={IsOnLadder} dash={DashTimer:F2})";
        }
    }
}
