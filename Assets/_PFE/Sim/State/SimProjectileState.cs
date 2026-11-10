using System;

namespace PFE.Sim.State
{
    /// <summary>
    /// The serializable truth of one in-flight projectile for one tick — the value a host snapshot
    /// replicates so a client can render and reconcile a round it did not simulate.
    ///
    /// <para><b>F-01/F-02, Phase 1.3 of the multiplayer readiness plan.</b> The readiness doc names
    /// the type in §8 row 1.3 but does not sketch its fields (§9 omits it), so the field list here is
    /// derived from the tick-mutated state of <c>Projectile</c> — the only projectile authority —
    /// filtered to what a snapshot must carry to reproduce the round's next tick and to draw it. It is
    /// pure C#: no <c>UnityEngine.Object</c>, no <c>Transform</c>, no <c>Vector2</c>. Positions are
    /// world pixels (PFE's canonical Sim space, per <c>TileQueryConstants</c>); velocity is world
    /// pixels per second, matching <c>Projectile._velocity</c>'s units-per-second convention converted
    /// to pixels at the boundary.</para>
    ///
    /// <para><b>What the tick mutates, and so must be here.</b> Position and velocity (integrated every
    /// <c>SimTick</c>), the per-second accelerations <c>AccelerationX/Y</c> (AS3 <c>ddx</c>/<c>ddy</c>),
    /// the homing strength <c>HomingStrength</c> (AS3 <c>navod</c>), the arc length
    /// <c>TraveledPixels</c> (AS3 <c>Bullet.dist</c>, which feeds hit-avoidance accuracy), the lifetime
    /// countdown, the remaining-damage budget a penetrating round spends down (AS3 <c>Bullet.damage</c>,
    /// mutated in place by <c>Unit.damage()</c>), and the detonated/impacting latches that gate the
    /// tick. A snapshot that omitted any of these could not reproduce the round's next step.</para>
    ///
    /// <para><b>Identity and idempotency (F-05, F-13).</b> The round is keyed by <see cref="EntityHash"/>
    /// — a host-assigned <c>EntityId.CreateRuntime(tickIndex, counter)</c>, which is exactly the
    /// transaction key F-13's dedupe needs ("<c>projectile:</c> + id" against a completed-transaction
    /// set). <see cref="Generation"/> and <see cref="Sequence"/> are the stale-lifetime and ordering
    /// guards of Remains' <c>ProjectileProgressMessage</c>. <see cref="LauncherHash"/> ties the round to
    /// the attack that spawned it so a client can attribute a hit without a back-reference to a
    /// <c>MonoBehaviour</c>.</para>
    ///
    /// <para><b>What is deliberately NOT here.</b> The weapon/ammo/visual identity is a content-versioned
    /// interned id (<see cref="DefinitionId"/>), not a <c>WeaponDefinition</c> reference. The damage
    /// context, decal, explosion radii and faction are launch-time constants folded into
    /// <see cref="DefinitionId"/> / the spawn event, not per-tick state. The struck-unit list (AS3
    /// <c>parr</c>) is reconciliation detail, not snapshot state — a client does not re-run the sweep,
    /// it accepts the host's hit events. Unity-side scratch (broad-phase buffers, beam-fade caches,
    /// reactive UI) never crosses the wire.</para>
    ///
    /// <para><b>Authority.</b> Host-authoritative: the host simulates every projectile and clients
    /// receive position/velocity for rendering and interpolation (§10 — remote entities interpolate,
    /// never predict). Spawn/despawn is a reliable, idempotent event (F-13); the per-tick record here
    /// is the unreliable, replaceable motion sample.</para>
    /// </summary>
    [Serializable]
    public struct SimProjectileState : IEquatable<SimProjectileState>
    {
        // ── Identity / idempotency (F-05, F-13) ─────────────────────────────────
        /// <summary>The round's host-assigned id, <c>EntityId.CreateRuntime(tickIndex, counter).Hash</c>.
        /// Doubles as the F-13 transaction key: a client dedupes a spawn event against this.</summary>
        [SimReconcile(SimReconcileRule.IdempotentByEntity)]
        public ulong EntityHash;
        /// <summary>The entity that launched the round (<c>EntityId.Hash</c>), for hit attribution and
        /// faction gating without a <c>MonoBehaviour</c> back-reference.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public ulong LauncherHash;
        /// <summary>Staleness guard for the spawn/despawn lifetime.</summary>
        [SimReconcile(SimReconcileRule.ReliableLifecycleEvent)]
        public int Generation;
        /// <summary>Wrap-safe ordering on the unreliable motion channel (Remains
        /// <c>ProjectileProgressMessage.sequence</c>).</summary>
        [SimReconcile(SimReconcileRule.RejectIfStale)]
        public uint Sequence;
        /// <summary>The weapon/ammo archetype as an interned, content-versioned id.</summary>
        [SimReconcile(SimReconcileRule.ContentConstant)]
        public ushort DefinitionId;
        /// <summary>Which behaviour a receiver applies to this record — see
        /// <see cref="SimTransientKind"/>. A fired round, a thrown object and a placed mine share this
        /// shape; the kind is what tells a client that the object with zero velocity is a mine that is
        /// armed rather than a round that has stalled. Defaults to
        /// <see cref="SimTransientKind.Projectile"/>, so an unstamped record reads as the commonest
        /// kind instead of as an unknown one.</summary>
        [SimReconcile(SimReconcileRule.ContentConstant)]
        public byte Kind;

        // ── Position / velocity (world pixels; velocity px/s) ───────────────────
        [SimReconcile(SimReconcileRule.Snap)]
        public float Px;
        [SimReconcile(SimReconcileRule.Snap)]
        public float Py;
        /// <summary>Velocity along X, world pixels per second.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float Vx;
        /// <summary>Velocity along Y, world pixels per second.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float Vy;

        // ── Integration constants that the tick reads (AS3 ddx/ddy/navod) ───────
        /// <summary>Per-second X acceleration (AS3 <c>ddx</c> — rocket thrust along aim).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float AccelerationX;
        /// <summary>Per-second Y acceleration (AS3 <c>ddy</c> — gravity / flame lift).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float AccelerationY;
        /// <summary>Homing strength (AS3 <c>navod</c>); 0 = no homing.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float HomingStrength;

        // ── Tick-mutated flight state ───────────────────────────────────────────
        /// <summary>Arc length flown since spawn, world pixels (AS3 <c>Bullet.dist</c>). Accumulated
        /// along the path, not start-to-target, so a curving round reads the same accuracy the oracle
        /// does.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float TraveledPixels;
        /// <summary>Seconds of flight remaining (AS3 <c>liv</c>, converted from frames); the round is
        /// removed at zero.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float LifetimeRemaining;
        /// <summary>What is left of the round's damage, spent down by each penetrated target (AS3
        /// <c>Bullet.damage</c>). Equal to the launch damage until the round penetrates something.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float RemainingDamage;

        // ── Lifecycle latches ───────────────────────────────────────────────────
        /// <summary>The round has detonated (AoE resolved) and is flying out its last frames.</summary>
        [SimReconcile(SimReconcileRule.SnapMonotonic)]
        public bool HasDetonated;
        /// <summary>The round is playing its impact frames before returning to the pool.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public bool IsImpacting;

        // ── Wire serialization (Phase 1.4) ────────────────────────────────────

        /// <summary>Appends this record to <paramref name="writer"/> in declaration order.</summary>
        public readonly void WriteTo(System.IO.BinaryWriter writer)
        {
            writer.Write(EntityHash);
            writer.Write(LauncherHash);
            writer.Write(Generation);
            writer.Write(Sequence);
            writer.Write(DefinitionId);
            writer.Write(Kind);
            writer.Write(Px); writer.Write(Py);
            writer.Write(Vx); writer.Write(Vy);
            writer.Write(AccelerationX); writer.Write(AccelerationY);
            writer.Write(HomingStrength);
            writer.Write(TraveledPixels);
            writer.Write(LifetimeRemaining);
            writer.Write(RemainingDamage);
            writer.Write(HasDetonated);
            writer.Write(IsImpacting);
        }

        /// <summary>Reads one record from <paramref name="reader"/> in the same order
        /// <see cref="WriteTo"/> wrote it.</summary>
        public static SimProjectileState ReadFrom(System.IO.BinaryReader reader)
        {
            return new SimProjectileState
            {
                EntityHash = reader.ReadUInt64(),
                LauncherHash = reader.ReadUInt64(),
                Generation = reader.ReadInt32(),
                Sequence = reader.ReadUInt32(),
                DefinitionId = reader.ReadUInt16(),
                Kind = reader.ReadByte(),
                Px = reader.ReadSingle(), Py = reader.ReadSingle(),
                Vx = reader.ReadSingle(), Vy = reader.ReadSingle(),
                AccelerationX = reader.ReadSingle(), AccelerationY = reader.ReadSingle(),
                HomingStrength = reader.ReadSingle(),
                TraveledPixels = reader.ReadSingle(),
                LifetimeRemaining = reader.ReadSingle(),
                RemainingDamage = reader.ReadSingle(),
                HasDetonated = reader.ReadBoolean(),
                IsImpacting = reader.ReadBoolean(),
            };
        }

        public readonly bool Equals(SimProjectileState other)
        {
            return EntityHash == other.EntityHash
                && LauncherHash == other.LauncherHash
                && Generation == other.Generation
                && Sequence == other.Sequence
                && DefinitionId == other.DefinitionId
                && Kind == other.Kind
                && Px.Equals(other.Px) && Py.Equals(other.Py)
                && Vx.Equals(other.Vx) && Vy.Equals(other.Vy)
                && AccelerationX.Equals(other.AccelerationX)
                && AccelerationY.Equals(other.AccelerationY)
                && HomingStrength.Equals(other.HomingStrength)
                && TraveledPixels.Equals(other.TraveledPixels)
                && LifetimeRemaining.Equals(other.LifetimeRemaining)
                && RemainingDamage.Equals(other.RemainingDamage)
                && HasDetonated == other.HasDetonated
                && IsImpacting == other.IsImpacting;
        }

        public override readonly bool Equals(object obj) => obj is SimProjectileState other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked
            {
                int hash = EntityHash.GetHashCode();
                hash = (hash * 397) ^ LauncherHash.GetHashCode();
                hash = (hash * 397) ^ Generation;
                hash = (hash * 397) ^ (int)Sequence;
                hash = (hash * 397) ^ DefinitionId.GetHashCode();
                hash = (hash * 397) ^ Kind.GetHashCode();
                hash = (hash * 397) ^ Px.GetHashCode();
                hash = (hash * 397) ^ Py.GetHashCode();
                hash = (hash * 397) ^ Vx.GetHashCode();
                hash = (hash * 397) ^ Vy.GetHashCode();
                hash = (hash * 397) ^ AccelerationX.GetHashCode();
                hash = (hash * 397) ^ AccelerationY.GetHashCode();
                hash = (hash * 397) ^ HomingStrength.GetHashCode();
                hash = (hash * 397) ^ TraveledPixels.GetHashCode();
                hash = (hash * 397) ^ LifetimeRemaining.GetHashCode();
                hash = (hash * 397) ^ RemainingDamage.GetHashCode();
                hash = (hash * 397) ^ HasDetonated.GetHashCode();
                hash = (hash * 397) ^ IsImpacting.GetHashCode();
                return hash;
            }
        }

        public override readonly string ToString()
        {
            return $"SimProjectileState({SimTransientKind.NameOf(Kind)} ent={EntityHash:X16} def={DefinitionId} pos=({Px:F1},{Py:F1}) " +
                   $"vel=({Vx:F1},{Vy:F1}) dist={TraveledPixels:F0} life={LifetimeRemaining:F2})";
        }
    }
}
