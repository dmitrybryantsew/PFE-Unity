// Shared ProfilerMarkers for the render/view path.
//
// WHY THIS EXISTS
// PfeFrameSeriesRecorder reads per-frame deltas of the view work (RoomVisualController.LateUpdate,
// fog mask, camera, HUD, NPC animator) through ProfilerRecorder. ProfilerRecorder can only see
// ProfilerMarkers — PfeProfiler's Stopwatch regions are invisible to it — so the view path needs
// real markers. They live here, in ONE static class, rather than as fields in each view class,
// because the recorder must reference the same ProfilerMarker instance it records (a marker is
// matched by name, but sharing the instance keeps the association explicit and free of typos).
//
// COST: ProfilerMarker.Begin/End are [Conditional] on ENABLE_PROFILER — compiled away entirely in
// release builds. In Development builds the cost is a few ns per scope; these are per-frame scopes,
// not per-pixel, so that is noise against the frame budget they exist to measure.
//
// Names are static strings by construction (fields), which is also what keeps the profiler's
// marker table a small fixed set.

using Unity.Profiling;

namespace PFE.Core.Profiling
{
    public static class PfeViewMarkers
    {
        // Whole LateUpdate of RoomVisualController — platforms step + object visuals + fog mask.
        public static readonly ProfilerMarker RoomVisualsLateUpdate =
            new ProfilerMarker(ProfilerCategory.Scripts, "PFE.View.RoomVisualsLate");

        // roomObjectVisualManager.UpdateVisuals — the per-frame prop/object visual refresh.
        public static readonly ProfilerMarker ObjectVisuals =
            new ProfilerMarker(ProfilerCategory.Scripts, "PFE.View.ObjectVisuals");

        // RoomBackdropRenderer.UpdateVisibilityMask — the fog reveal pass.
        public static readonly ProfilerMarker FogVisibilityMask =
            new ProfilerMarker(ProfilerCategory.Scripts, "PFE.View.FogMask");

        public static readonly ProfilerMarker CameraLateUpdate =
            new ProfilerMarker(ProfilerCategory.Scripts, "PFE.View.CameraLate");

        public static readonly ProfilerMarker HudUpdate =
            new ProfilerMarker(ProfilerCategory.Scripts, "PFE.View.Hud");

        public static readonly ProfilerMarker NpcAnimatorFixedUpdate =
            new ProfilerMarker(ProfilerCategory.Scripts, "PFE.View.NpcAnimFixed");
    }
}
