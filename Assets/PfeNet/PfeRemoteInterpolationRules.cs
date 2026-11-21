namespace PFE.Net
{
    /// <summary>
    /// Pure interpolation decisions for the display-only projections — the half of F-12's motion
    /// smoothing the offline wall can execute. No <c>UnityEngine</c>, no <c>MonoBehaviour</c>, no
    /// scene: the two views (<see cref="PfeRemoteBody"/>'s ghost, <see cref="PfeRemoteProjectileView"/>'s
    /// tracer) own the transforms; this file owns only the math that decides where between two
    /// samples the visual belongs.
    ///
    /// <para><b>Why interpolation is display-only.</b> The host simulates at 30 Hz and snapshots
    /// ride the wire at 15–30 Hz, so a projection that wrote each sample straight to the transform
    /// would render remote players and rounds stepping at network rate on a 60–144 Hz display.
    /// Each view keeps the newest sample as the target and the position the viewer actually saw
    /// as the origin, and eases between them over one window.</para>
    ///
    /// <para><b>Why a teleport threshold.</b> Room transitions and respawns move a body hundreds of
    /// pixels between two snapshots; easing across that gap would smear the avatar across the
    /// screen. Anything beyond <see cref="SnapDistancePixelsSq"/> snaps; anything shorter eases.
    /// The threshold is deliberately generous: even a sprinting peer moves only a few pixels per
    /// snapshot, so 128 px is teleport territory without ever taxing ordinary motion.</para>
    /// </summary>
    public static class PfeRemoteInterpolationRules
    {
        /// <summary>One easing window: 1 tick at 30 Hz (~0.0333s). The display catches up to the
        /// newest sample within a single sim tick, so the buffer adds smoothness without adding
        /// visible latency beyond what the network already imposed.</summary>
        public const float DefaultWindowSeconds = 1f / 30f;

        /// <summary>Teleport threshold in squared pixels: 128 px. The comparison is strictly
        /// greater, so exactly 128 px still eases and anything larger snaps.</summary>
        public const float SnapDistancePixelsSq = 128f * 128f;

        /// <summary>Whether the newest sample must be written immediately instead of eased into.
        /// The first sample a view ever receives has no origin — nothing to ease from — so it
        /// always snaps. Otherwise the origin-to-sample displacement decides: past
        /// <see cref="SnapDistancePixelsSq"/> the move is a teleport (room transition, respawn),
        /// not motion.</summary>
        public static bool ShouldSnap(bool hasPrevious, float prevPx, float prevPy, float nextPx, float nextPy)
        {
            if (!hasPrevious) return true;
            float dx = nextPx - prevPx;
            float dy = nextPy - prevPy;
            return (dx * dx + dy * dy) > SnapDistancePixelsSq;
        }

        /// <summary>Ease from <paramref name="prevPx"/>/<paramref name="prevPy"/> toward
        /// <paramref name="targetPx"/>/<paramref name="targetPy"/> by
        /// <paramref name="elapsedSeconds"/> inside <paramref name="windowSeconds"/>. The clamp is
        /// the load-bearing half: easing never extrapolates past the newest sample, so a display
        /// body that fell behind the wire can never run into a wall the host already cleared.</summary>
        public static void Sample(
            float prevPx, float prevPy,
            float targetPx, float targetPy,
            float elapsedSeconds, float windowSeconds,
            out float outPx, out float outPy)
        {
            float t = windowSeconds > 0.0001f ? elapsedSeconds / windowSeconds : 1f;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f; // Clamp, never extrapolate into walls

            outPx = prevPx + (targetPx - prevPx) * t;
            outPy = prevPy + (targetPy - prevPy) * t;
        }
    }
}
