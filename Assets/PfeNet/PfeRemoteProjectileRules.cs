using PFE.Sim.State;

namespace PFE.Net
{
    /// <summary>
    /// Pure decisions for the client-side projectile display — the half of the slice-3 spawner the
    /// offline wall can execute. No <c>UnityEngine</c>, no <c>MonoBehaviour</c>, no scene.
    ///
    /// <para><b>Why a record needs a gate before it may draw.</b> The projector already filters what it
    /// emits (no hash-0 records, sorted set), but the receiver applies snapshots from a network: a
    /// truncated or hostile payload must not get as far as a <c>Transform</c> write. The checks here
    /// are the receiver's own: identity present, kind interpretable, position finite. A NaN would
    /// silently poison <c>transform.position</c> and the view would vanish with no error — the same
    /// failure <see cref="PfeRemoteBodyRules.IsUsablePosition"/> exists for on the body side.</para>
    ///
    /// <para><b>Why the sample needs an ordering gate.</b> Snapshots ride the unreliable channel: two
    /// in flight can land out of order, and a view that applied them in arrival order would step
    /// backward. <see cref="AcceptsSample"/> is the same wrap-safe newer test the command gate uses,
    /// because the record's <c>Sequence</c> is the same uint the projector advances once per emitted
    /// sample.</para>
    ///
    /// <para><b>Why the frame math is here and not in the view.</b> The flight/impact split mirrors
    /// <c>Projectile.UpdateVisualAnimation</c> exactly; a pure function is the only form the offline
    /// wall can pin, and a pinned copy is what keeps the remote view from drifting from the local one
    /// the first time someone edits either.</para>
    /// </summary>
    public static class PfeRemoteProjectileRules
    {
        /// <summary>
        /// Whether a replicated transient record may have a display view at all. Hash 0 is an
        /// unidentified entity (the projector skips those, so one arriving is a malformed payload);
        /// an unknown kind is a record from a newer build, which must be skipped rather than drawn as
        /// the default kind; a non-finite position would write NaN into the scene.
        /// </summary>
        public static bool IsDisplayableRecord(in SimProjectileState state)
        {
            return state.EntityHash != 0UL
                && SimTransientKind.IsKnown(state.Kind)
                && IsFinitePosition(state.Px, state.Py);
        }

        /// <summary>Both coordinates finite — a NaN or infinity must never reach a Transform write.</summary>
        public static bool IsFinitePosition(float px, float py)
        {
            return !float.IsNaN(px) && !float.IsNaN(py)
                && !float.IsInfinity(px) && !float.IsInfinity(py);
        }

        /// <summary>
        /// Whether a motion sample may replace the one the view last applied. Wrap-safe: it is
        /// <see cref="SimStalenessRules.IsNewer"/>, because the record's <c>Sequence</c> is the
        /// projector's per-sample counter — a uint a long session can wrap. A zero
        /// <paramref name="applied"/> means "nothing applied yet": record sequences start at 1, so 0
        /// cannot be a real applied value, and the first sample of a fresh view is accepted exactly
        /// when it is non-zero (the same special case <see cref="PfeRemoteBodyRules.IsNewerCommand"/>
        /// documents for commands).
        /// </summary>
        public static bool AcceptsSample(uint incoming, uint applied)
        {
            if (applied == 0u) return incoming != 0u;
            return SimStalenessRules.IsNewer(incoming, applied);
        }

        /// <summary>
        /// The next frame of the flight/impact animation, mirroring
        /// <c>Projectile.UpdateVisualAnimation</c>: flight frames loop inside
        /// <c>[0 .. FlightWindowEnd]</c>, impact frames play once from <c>flightFrameCount</c> to the
        /// last frame. Returns false when there is nothing further to show (a single-frame strip, a
        /// still flight frame with <c>loop == false</c>, or an impact sequence that has already held
        /// its last frame) — the caller then keeps the current sprite; despawn is the reconciler's
        /// job, never the animation's, or a finished impact could respawn on the next snapshot.
        /// </summary>
        /// <param name="frameCount">Total frames in the strip. Values below 2 are a still sprite.</param>
        /// <param name="flightFrameCount">Impact split: frames <c>[0 .. flightFrameCount-1]</c> are the
        /// flight loop, the rest play once on impact. 0 or an out-of-range value means no split.</param>
        /// <param name="loop">Whether the flight window wraps.</param>
        /// <param name="impacting">The round is playing its impact frames.</param>
        /// <param name="currentIndex">The frame currently shown; clamped into range first.</param>
        /// <param name="nextIndex">The frame to show instead, when this returns true.</param>
        public static bool TryAdvanceFrame(
            int frameCount, int flightFrameCount, bool loop, bool impacting,
            int currentIndex, out int nextIndex)
        {
            nextIndex = currentIndex;

            if (frameCount < 2) return false;

            bool split = flightFrameCount > 0 && flightFrameCount < frameCount;
            // Clamp first: the index arrives from outside (the impacting transition jumps it to
            // flightFrameCount), and an out-of-range index must read as "recover", not "advance".
            int clamped = currentIndex < 0 ? 0 : (currentIndex > frameCount - 1 ? frameCount - 1 : currentIndex);
            nextIndex = clamped;

            if (impacting)
            {
                if (!split) return false;              // no dedicated impact strip: hold the flight frame
                if (clamped >= frameCount - 1) return false;   // last impact frame already held
                nextIndex = clamped + 1;
                return true;
            }

            int rangeEnd = split ? flightFrameCount - 1 : frameCount - 1;
            if (rangeEnd <= 0) return false;           // single flight frame, nothing to advance

            if (clamped < rangeEnd)
            {
                nextIndex = clamped + 1;
                return true;
            }

            if (loop)
            {
                nextIndex = 0;
                return true;
            }

            return false;
        }
    }
}
