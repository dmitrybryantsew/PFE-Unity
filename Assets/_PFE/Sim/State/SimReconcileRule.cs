using System;

namespace PFE.Sim.State
{
    /// <summary>
    /// What a receiver does with one replicated field when a host snapshot disagrees with its own
    /// state — §10 of <c>docs/multiplayer/Physics_And_Units_Multiplayer_Readiness_2026-11-04.md</c>,
    /// expressed as a type so it can be <i>attached to a field</i> and linted.
    ///
    /// <para><b>Why this exists (Phase 3.5).</b> §10 already writes the reconciliation table as prose.
    /// Prose does not fail a build. The defect §10 cannot prevent on its own is the one that matters:
    /// someone adds a field to a replicated record, the field crosses the wire, and nobody ever decides
    /// what the client does when that field disagrees — so the field silently takes whatever the
    /// snapshot applier happens to do (usually "overwrite"), which is correct for a flag and wrong for
    /// predicted motion. <see cref="SimReconcileAttribute"/> forces the decision at the declaration
    /// site, and <c>SimReconcileRuleLintTests</c> fails when a field has none. Row 3.5's acceptance is
    /// exactly that lint: <i>no replicated field without a rule</i>.</para>
    ///
    /// <para><b>A rule is a decision procedure, not one verb.</b> <see cref="Resimulate"/> is the
    /// honest example: for the client that owns the body it means "replay buffered commands from the
    /// acked tick", and for every other receiver it means "interpolate". That split is a property of
    /// the <i>receiver's relation to the entity</i>, not of the field, and it is why the same rule can
    /// cover a whole record. The record that documents the split is <c>SimMotorState</c>; the receiver
    /// that resolves it does not exist yet (Phase 4), and this enum deliberately does not pretend to
    /// implement it — a resolver with no caller would be a presence-only flag.</para>
    ///
    /// <para><b>What is NOT in this vocabulary, and why.</b> §10's last row is
    /// <c>[LocalOnly]</c> — presentation that is never replicated. It has no member here on purpose: a
    /// <c>[LocalOnly]</c> field is by definition <i>not in a replicated record</i>, so a rule for it
    /// would be dead vocabulary that reads like a legal option. The structural guard is better than a
    /// value: presentation state added to one of these records has no rule to take, so the lint makes
    /// the author choose between moving it to the view or replicating it deliberately. The authority
    /// markers on the controllers (<c>AuthorityMarkerLintTests</c>, Phase 1.1) are where
    /// <c>[LocalOnly]</c> is actually enforced.</para>
    ///
    /// <para><b>The gap row 3.5 exposed, and how it closed at v4.</b> §10's tile-mutation row
    /// ("reliable ordered event with <c>worldRevision</c>, never a delta") originally had <b>no field
    /// to attach to</b>: <c>SimRoomDelta</c> carried no tile data. That was a missing payload, not a
    /// missing rule, which is why row 3.5 refused to invent a rule value for it. §10.2 gap 1 is now
    /// <c>SimSnapshot.TileMutations</c> (<see cref="SimTileMutation"/>) and gap 2 is
    /// <c>SimDroppedItem.EntityHash</c>; both are real fields with real rules, consumed by
    /// <c>SimSnapshotReceiver</c>. The rule vocabulary did not grow for them — the honest fix was a
    /// payload and a receiver, never a new verb.</para>
    /// </summary>
    public enum SimReconcileRule
    {
        /// <summary>
        /// <b>Drop the payload before reading any field.</b> The format version is not state to
        /// reconcile — it decides whether the rest of the bytes can be interpreted at all. Not a §10
        /// row: §10 governs state that both peers already agree is interpretable.
        ///
        /// <para>Deliberately the zero value. The attribute cannot be applied without naming a rule, so
        /// no field relies on this, but any future code that reaches a <c>default(SimReconcileRule)</c>
        /// for an unclassified field should refuse the payload rather than accept it — a wrong guess in
        /// this direction is a dropped packet, in the other direction it is silent divergence.</para>
        /// </summary>
        RejectIfIncompatible = 0,

        /// <summary>
        /// <b>Compare against local state and drop the payload on mismatch</b> (§10 row 1): wrap-safe
        /// <c>IsNewer</c> ordering for <c>TickIndex</c>/<c>SnapshotSequence</c>/<c>WorldRevision</c>,
        /// the same-shaped compare for <c>PoseSequence</c>/<c>Sequence</c>, and baseline identity for
        /// <c>BaselineHash</c>. The shared property is "this payload is older than, or not descended
        /// from, what I have — applying it would move state backwards". An older snapshot must never
        /// overwrite newer events.
        /// </summary>
        RejectIfStale = 1,

        /// <summary>
        /// <b>Blend toward the host value, snap on teleport or a lifecycle change</b> (§10 row 2). For
        /// motion the receiver does not simulate: <c>SimUnitState</c>'s <c>Px/Py/Vx/Vy</c> are the
        /// motor-less unit's truth (an AI body, never locally predicted). Smoothing here is not
        /// optional polish — a raw position copy per packet is what makes a remote unit stutter.
        ///
        /// <para><b>One exception, and it is not a second rule.</b> Where the receiver <i>does</i>
        /// simulate the body — its own player — the motor's prediction wins and this field follows it.
        /// That is <see cref="Resimulate"/>'s relation applied to a field that would otherwise
        /// interpolate, so a receiver resolves it by asking who owns the entity, not by looking at the
        /// field. Recorded here because the alternative reading — "the client blends its own feet
        /// position toward the host's" — would fight the prediction every packet.</para>
        /// </summary>
        Interpolate = 2,

        /// <summary>
        /// <b>The owning client re-simulates; every other receiver interpolates</b> (§10 row 3). Applies
        /// to the whole of <c>SimMotorState</c> except its key. A client that owns the body must never
        /// accept a raw host position over a valid prediction without replaying its buffered commands
        /// from the acked tick and applying only the residual — accepting it directly turns a 40 ms
        /// round trip into a visible snap every packet.
        /// </summary>
        Resimulate = 3,

        /// <summary>
        /// <b>Copy the host value.</b> §10 row 4 (boolean flags — interpolating a flag produces
        /// impossible motor states: half-grounded, half-crouched) plus every host-authoritative scalar
        /// that has no one-way latch. For an array field this means <i>replace the set</i>: the delta's
        /// arrays are the authoritative membership for the tick, and each element's own fields carry
        /// their own rules.
        /// </summary>
        Snap = 4,

        /// <summary>
        /// <b>Copy the host value, but the field's one-way latch may never be cleared by a later
        /// packet</b> (§10 row 5). Death is a reliable event, not a delta: a delayed packet from before
        /// the kill must not resurrect a corpse, and the same holds for a detonated projectile. Health
        /// is not monotonic (healing exists) — the latch is the part that only moves one way, and it is
        /// keyed on the record's own death/detonation flag, not on the numeric direction.
        /// </summary>
        SnapMonotonic = 5,

        /// <summary>
        /// <b>Keep the local phase; advance at the AS3 frame rate; resync only on drift</b> (§10 row 6).
        /// PFE animation is frame-driven, not time-driven, so a pose copied verbatim every packet
        /// resets the cycle and freezes fast animations. The pose <i>sequence</i> that orders these
        /// packets is <see cref="RejectIfStale"/>, not this rule — the two are a pair.
        /// </summary>
        HoldPhase = 6,

        /// <summary>
        /// <b>Arrives as a reliable ordered event, never as a delta</b> (§10 row 7/8). Used by the
        /// despawn list: an unordered or lossy despawn either leaves a ghost body or removes a live
        /// one. Both peers must also end up with identical geometry, which is why this cannot be a
        /// delta at all.
        /// </summary>
        ReliableOrderedEvent = 7,

        /// <summary>
        /// <b>Spawn/despawn/transition — reliable ordered event, and the generation/lifecycle counters
        /// must be bumped with it</b> (§10 row 8). The counters exist so a delayed packet from a
        /// previous lifetime cannot resurrect an entity whose id has been reused; bumping them is the
        /// producer's job, and this rule is what marks the field as carrying that contract.
        /// </summary>
        ReliableLifecycleEvent = 8,

        /// <summary>
        /// <b>Content-version constant — never replicated per tick, and both peers must resolve it from
        /// the same content version</b> (§10 row 9). Body dimensions live here: the asset divergence
        /// <c>UnitBodyBoxContentLintTests</c> measures (55×70 vs the oracle's 50×70) must stay a
        /// content bug, never become a host/client disagreement.
        /// </summary>
        ContentConstant = 9,

        /// <summary>
        /// <b>Dedupe by <c>EntityId</c> against the completed-transaction set</b> (§10 row 11, F-13). A
        /// projectile spawn is an event that may arrive twice; applying it twice creates two
        /// projectiles from one shot. This is why <c>SimProjectileState.EntityHash</c> is not merely
        /// <see cref="Key"/>.
        /// </summary>
        IdempotentByEntity = 10,

        /// <summary>
        /// <b>Identity — index by it, never reconcile it.</b> The record's own key
        /// (<c>EntityHash</c>, <c>RoomHash</c>). Not a §10 row, because §10 lists what to do with
        /// <i>values</i>; a key is what the other rules look up. Giving a key a reconciling rule would
        /// be a category error — you cannot "snap" the thing you use to find the record.
        /// </summary>
        Key = 11,
    }

    /// <summary>
    /// Attaches a <see cref="SimReconcileRule"/> to one field of a replicated record. Exactly one per
    /// field; <c>SimReconcileRuleLintTests</c> enforces both halves (none, or two, is a failure).
    ///
    /// <para><b>Why an attribute and not a table.</b> A side table can be complete and still be
    /// unread: the reviewer adding a field looks at the field. Putting the rule in the attribute run
    /// directly above the declaration means the decision is visible exactly where the change happens —
    /// the same reasoning <c>AuthorityMarkerLintTests</c> records for the controllers' authority
    /// markers. The cost is that a table is easier to read end-to-end, which is why §10 stays the
    /// readable table and this is its enforcement.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class SimReconcileAttribute : Attribute
    {
        public SimReconcileAttribute(SimReconcileRule rule)
        {
            Rule = rule;
        }

        /// <summary>The rule this field reconciles under.</summary>
        public SimReconcileRule Rule { get; }
    }
}
