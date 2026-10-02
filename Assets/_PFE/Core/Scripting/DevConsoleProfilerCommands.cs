using System.Text;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Debug commands exposed to the Lua console as the global <c>prof</c> table:
    /// <c>prof:Dump("camp")</c>, <c>prof:Reset()</c>, <c>prof:Status()</c>. The console verbs
    /// <c>prof</c> / <c>profile</c> are sugar over the same methods.
    ///
    /// <para><b>Why this exists.</b> <see cref="PFE.Core.Profiling.PfeProfiler"/> already collects
    /// regions, but the only thing that ever dumped it was
    /// <see cref="PFE.Core.Profiling.PfeProfilerAutoDump"/>, at frames 5 and 60 — i.e. during boot.
    /// Every capture in <c>ProfilerCaptures/</c> was therefore a boot capture, and a room that is slow
    /// once you are standing in it could not be measured at all. The three verbs below are what make
    /// the instrument usable in the room you are actually reporting.</para>
    ///
    /// <para><b>The workflow they exist for.</b> Regions accumulate from process start, so a dump taken
    /// in a slow room is dominated by boot. <c>prof reset</c> at the room's door, a few seconds of
    /// standing still, then <c>prof dump camp</c> gives a report whose totals are that room and
    /// nothing else. <c>prof status</c> prints the ticks-per-frame figure, which is the number that
    /// says whether the sim is saturated, plus the prop counts that every prop-layer region's cost is
    /// proportional to.</para>
    ///
    /// <para><b>Those numbers are also written into the dump.</b> <see cref="Wire"/> registers
    /// <see cref="BuildContextLine"/> with <see cref="PFE.Core.Profiling.PfeProfiler.SetContextSource"/>,
    /// so every report opens with the tick rate, the tickable count and the current room's prop-loop
    /// sizes. The alternative — reading them off <c>prof status</c> by hand — is how a capture ends up
    /// on disk with no record of how many props were in the room, which is precisely what happened to
    /// the first capture taken after the prop candidate cache landed.</para>
    ///
    /// <para><b>MoonSharp visibility.</b> The wiring members are <c>internal</c>; MoonSharp's default
    /// reflection interop exposes public members only, so only the command methods below become
    /// callable from Lua. Do not make <c>_simLoop</c>, <c>_landMap</c>, <c>Wire</c> or
    /// <c>Unwire</c> public.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsoleProfilerCommands
    {
        private SimLoop _simLoop;

        /// <summary>
        /// The world map, read only to reach the current room's <c>RoomObjectPhysicsLayer</c> for its
        /// prop counts. Optional: the console is a debug tool that must survive a partially built
        /// container, and before the world exists there is no room to report on.
        /// </summary>
        private PFE.Systems.Map.LandMap _landMap;

        /// <summary>
        /// The delegate handed to <see cref="PFE.Core.Profiling.PfeProfiler.SetContextSource"/>.
        /// Held in a field so re-<see cref="Wire"/>ing (which happens whenever the world is
        /// re-resolved) reassigns one cached delegate instead of allocating a fresh closure.
        /// </summary>
        private System.Func<string> _contextSource;

        internal void Wire(SimLoop simLoop, PFE.Systems.Map.LandMap landMap)
        {
            _simLoop = simLoop;
            _landMap = landMap;

            // PUSHED, not pulled: the report writer is a static class and cannot reach this object,
            // so the numbers `prof status` prints have to be registered with it once. This is what
            // puts the prop counts into the dumped .txt instead of leaving them only on a console
            // line that nobody saves.
            if (_contextSource == null)
            {
                _contextSource = BuildContextLine;
            }

            PFE.Core.Profiling.PfeProfiler.SetContextSource(_contextSource);
        }

        /// <summary>
        /// Drops the report context. Called from the console's teardown: the profiler is static and
        /// outlives this scene object, so a registered source would keep this command object — and
        /// the <see cref="PFE.Systems.Map.LandMap"/> it holds — alive after the scene unloads.
        /// </summary>
        internal void Unwire()
        {
            PFE.Core.Profiling.PfeProfiler.SetContextSource(null);
        }

        // ── Commands ──────────────────────────────────────────────────────────

        /// <summary>
        /// Write the region report to the Unity console and to <c>ProfilerCaptures/</c>, and report
        /// the file it wrote. <paramref name="tag"/> becomes part of the filename, so a camp capture
        /// and a corridor capture are distinguishable by name rather than by timestamp.
        /// </summary>
        public string Dump(string tag = "manual")
        {
            if (!PFE.Core.Profiling.PfeProfiler.Enabled)
            {
                return "[prof] Profiling is OFF (release build, or PfeDebugSettings.ProfilingEnabled is " +
                       "false). Run 'prof on' -- note that switching it on cannot recover the time " +
                       "already spent, so reset and measure again.";
            }

            int regions = PFE.Core.Profiling.PfeProfiler.RegionCount;
            if (regions == 0)
            {
                // Refusing is the only outcome that is not misleading: an empty report is
                // indistinguishable from a report of a room where nothing runs.
                return "[prof] Nothing recorded yet (0 regions). If you just ran 'prof reset', let the " +
                       "game run for a few seconds first -- a reset with no frames after it produces an " +
                       "empty report, not a fast one.";
            }

            PFE.Core.Profiling.PfeProfiler.Dump(tag);

            string path = PFE.Core.Profiling.PfeProfiler.LastDumpPath;
            return $"[prof] Dumped {regions} region(s) as '{tag}'. Report also in the Unity console; " +
                   $"file: {path ?? "(write failed - see the warning above)"}";
        }

        /// <summary>
        /// Drop every recorded region. Run this on entering the room being measured, so the next
        /// <see cref="Dump"/> is that room's numbers and not boot plus the walk there.
        /// </summary>
        public string Reset()
        {
            int before = PFE.Core.Profiling.PfeProfiler.RegionCount;
            PFE.Core.Profiling.PfeProfiler.Reset();
            return $"[prof] Cleared {before} region(s). Let the game run, then 'prof dump <tag>'.";
        }

        /// <summary>Turn region collection on. Off by default in a release build only.</summary>
        public string On()
        {
            PFE.Core.Profiling.PfeProfiler.Enabled = true;
            return "[prof] Profiling ON. Note that regions measured while this was off are not " +
                   "recovered -- 'prof reset' then measure.";
        }

        /// <summary>Turn region collection off, so a measurement run stops paying for the markers.</summary>
        public string Off()
        {
            PFE.Core.Profiling.PfeProfiler.Enabled = false;
            return "[prof] Profiling OFF (the collected data is still there until 'prof reset').";
        }

        /// <summary>
        /// The readout that says whether the simulation clock is saturated. <c>ticksThisFrame</c>
        /// equal to <c>SimClock.MaxCatchupTicks</c> (5) on a slow frame means the frame time is
        /// 5 x (cost of one tick) — i.e. the cost is inside a tick, and <c>sim.tick.*</c> in the
        /// dump says which part of it.
        /// </summary>
        public string Status()
        {
            var sb = new StringBuilder("[prof] ");
            sb.Append(PFE.Core.Profiling.PfeProfiler.Enabled ? "enabled" : "OFF");
            sb.Append($" regions={PFE.Core.Profiling.PfeProfiler.RegionCount}");

            AppendSimCounts(sb);
            AppendPropCounts(sb);

            sb.Append("  |  'prof reset' at the room's door, stand a few seconds, 'prof dump camp'");
            return sb.ToString();
        }

        /// <summary>
        /// The one-line "what was running when this was taken" header that every dump carries, via
        /// <see cref="PFE.Core.Profiling.PfeProfiler.SetContextSource"/>. Deliberately the same
        /// numbers <see cref="Status"/> prints, built by the same two appenders, so the console
        /// readout and the file can never disagree about the room they describe.
        /// </summary>
        string BuildContextLine()
        {
            var sb = new StringBuilder();
            AppendSimCounts(sb);
            AppendPropCounts(sb);
            return sb.ToString().TrimStart();
        }

        /// <summary>
        /// The simulation's frame-scoped counters. Split out of <see cref="Status"/> so the dumped
        /// context line and the console readout cannot drift apart.
        /// </summary>
        void AppendSimCounts(StringBuilder sb)
        {
            if (_simLoop == null)
            {
                sb.Append(" sim=<unresolved>");
                return;
            }

            // TicksThisFrame / AverageTicksPerSecond / DroppedTicks are frame-scoped and live on
            // the SimMetrics snapshot, not on SimLoop itself (SimLoop exposes only the monotonic
            // TickIndex and the TickableCount). Take one snapshot so every number below belongs
            // to the same frame -- reading the properties one by one would straddle a tick.
            SimMetrics m = _simLoop.Metrics;

            sb.Append($" ticksThisFrame={m.TicksThisFrame}");
            sb.Append($"/{SimClock.MaxCatchupTicks}");
            sb.Append($" avgTicksPerSecond={m.AverageTicksPerSecond:0.0}");
            sb.Append($" tickables={m.TickableCount}");
            sb.Append($" dropped={m.DroppedTicks}");
            sb.Append($" tick={m.TickIndex}");
            sb.Append($" rate={_simLoop.Clock.TicksPerSecond}Hz");

            if (m.TicksThisFrame >= SimClock.MaxCatchupTicks)
            {
                sb.Append("  <- AT THE CATCH-UP CAP: frame time is ~5 x one tick's cost");
            }
        }

        /// <summary>
        /// Append the current room's prop-loop sizes.
        ///
        /// <para><b>Why these three numbers belong beside the tick rate.</b> Every region in the prop
        /// layer is a loop over one of these lists, so a per-tick cost is only interpretable divided by
        /// them: <c>phys.objects.step</c> total/calls is the cost of integrating <i>all</i> tracked
        /// props once, and <c>phys.prop.surfaceUnder</c> total/calls is the cost of one scan over the
        /// shelf list. Without the sizes, "7.5 ms per tick" cannot be told apart from "0.04 ms per
        /// prop" — and those two readings imply opposite fixes.</para>
        ///
        /// <para><b>These counters had no reader until now.</b> <c>RoomObjectPhysicsLayer</c> has
        /// exposed them since the per-tick candidate cache landed, and nothing printed them, so the
        /// first capture taken after that change could not say how many props the camp holds — which
        /// is the one number the next decision needs.</para>
        ///
        /// <para>Read through <c>ExistingObjectPhysicsLayer</c>, not <c>ObjectPhysicsLayer</c>: the
        /// latter allocates on first touch, and a status readout must not create the thing it is
        /// measuring.</para>
        /// </summary>
        void AppendPropCounts(StringBuilder sb)
        {
            PFE.Systems.Map.RoomInstance room = _landMap != null ? _landMap.currentRoom : null;
            PFE.Systems.Map.RoomObjectPhysicsLayer layer =
                room != null ? room.ExistingObjectPhysicsLayer : null;

            if (layer == null)
            {
                // Not "0": no room and an empty room are different answers, and printing a zero for
                // both is how a missing room reads as a fast one.
                sb.Append(" props=<no room>");
                return;
            }

            sb.Append($" props tracked={layer.TrackedDynamicObjectCount}");
            sb.Append($"/shelf={layer.ShelfCandidateCount}");
            sb.Append($"/impact={layer.ImpactCandidateCount}");
        }
    }
}
