// Region-based profiler. Replaces the ad-hoc BootTiming marks that used to live in
// Core/BootTimingProbe.cs (deleted).
//
// WHY THIS EXISTS
// Full Unity Profiler captures of this project are ~5 GB and OOM the editor, so we measure
// in-process instead. Unlike a flat BootTiming.Mark, this tracks NESTING, which is what lets a
// report distinguish "RoomVisualController.Initialize took 31 s" from "RoomVisualController.Initialize
// took 31 s OF ITS OWN WORK". Flat marks cannot tell those apart, and that distinction was the
// single most useful thing we learned during the boot investigation.
//
// THE MODEL
//   * A region is declared AT ITS CALL SITE, in real source. There is no config file and no
//     registry to keep in sync — the call site IS the declaration.
//   * Every region carries its own file:line (via [CallerFilePath]/[CallerLineNumber]) and a
//     "reason" string, so a report tells you where to look and why the marker exists.
//   * Disabled regions cost one branch: Region() returns default(Scope), whose Dispose is a
//     no-op. Nothing ever needs to be deleted from the source.
//
// USAGE
//   using (PfeProfiler.Region("room.tiles.generate", "boot: 19s inside TileCompositor"))
//   {
//       ...
//   }
//
//   PfeProfiler.Mark("game.worldBuilt");                       // point-in-time
//   PfeProfiler.Mark("game.roomsLoaded", $"count={n}");        // point + per-hit detail
//   PfeProfiler.Dump("manual");                                // log + ProfilingDumps/*.json
//
// RULES
//   * Region/Mark ids MUST be static strings. A dynamic id (e.g. $"x{n}") creates one region per
//     distinct value and will grow the registry without bound. Put runtime values in `detail`.
//   * ONE exception: an id forwarded as a method parameter is fine *provided every call site
//     passes a string literal*. The registry then still holds a small, fixed id set. See
//     BuiltInContentSource.RegisterType<T>(registry, label, ...), which does this for the eleven
//     content types. Never pass a computed or interpolated string that way.
//   * This is cheap at region granularity (~20-30 ns per hit) but NOT per-pixel. Do not put a
//     region inside a hot inner loop.
//
// SELF TIME
//   A region's SelfMs is its exclusive time: TotalMs minus everything charged to its children.
//   The invariant self + sum(child totals) == total must hold — if a leaf region ever reports
//   0.0 self, the accounting is broken (that bug shipped once: End() charged TotalMs but never
//   the region's own SelfMs for the stretch after its last child).

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
// Alias instead of `using System.Diagnostics;` — that namespace also declares Debug, which would
// make `Debug.Log` / `Debug.isDebugBuild` ambiguous with UnityEngine.Debug.
using Stopwatch = System.Diagnostics.Stopwatch;

namespace PFE.Core.Profiling
{
    public static class PfeProfiler
    {
        // ── Public API ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Times the enclosing block. Disabled => the returned scope does nothing.
        /// </summary>
        /// <param name="id">Stable identifier. Must NOT be built from runtime values.</param>
        /// <param name="reason">Why this region was instrumented. Shown in the report.</param>
        public static Scope Region(string id, string reason = null,
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (!Enabled) return default;
            Begin(id, reason, file, line);
            return new Scope(true);
        }

        /// <summary>
        /// Records a point in time on the timeline. Use for one-shot phase boundaries where there
        /// is no meaningful span to measure.
        /// </summary>
        /// <param name="detail">Per-hit runtime note (counts, ids). Kept on the timeline entry.</param>
        public static void Mark(string id, string detail = null,
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (!Enabled) return;

            long now = Stopwatch.GetTimestamp();

            lock (Sync)
            {
                RegionInfo region = GetOrCreate(id, null, file, line);
                region.Calls++;
                region.SeenSites.Add(SiteKey(file, line));

                if (Timeline.Count < MaxTimelineEntries)
                {
                    Timeline.Add(new TimelineEntry
                    {
                        Id = id,
                        Detail = detail,
                        StartMs = ToMs(now - OriginTs),
                        EndMs = ToMs(now - OriginTs),
                        IsSpan = false
                    });
                }
            }
        }

        // ── Open spans: measure the gap BETWEEN two engine callbacks ─────────────────
        //
        // A `using` region cannot span callbacks, but run 17 showed a 485.8 ms hole between the
        // AfterSceneLoad hook and the first thing GameManager does (derived from `game.db.init`
        // ending at 2675.6 with a 1085.8 span, and both Mark and Region share one Stopwatch clock).
        // This pair opens a region in one callback and closes it in another, so that hole shows up
        // as a named region with a self time instead of being invisible.
        //
        // Only ONE open span is supported, and OpenSpan is idempotent — a second OpenSpan without
        // a CloseSpan is ignored, so a missed close can never unbalance the frame stack.

        static bool _openSpanActive;

        /// <summary>
        /// Opens a region that is closed later by <see cref="CloseSpan"/>, possibly in a different
        /// engine callback. Use only when a normal <c>using</c> region is impossible.
        /// </summary>
        public static void OpenSpan(string id, string reason = null,
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (!Enabled || _openSpanActive) return;
            Begin(id, reason, file, line);
            _openSpanActive = true;
        }

        /// <summary>Closes the span opened by <see cref="OpenSpan"/>. Safe to call when none is open.</summary>
        public static void CloseSpan()
        {
            if (!_openSpanActive) return;
            _openSpanActive = false;
            End();
        }

        /// <summary>True when regions actually measure. False in release builds and when the setting is off.</summary>
        public static bool Enabled
        {
            get
            {
                if (_enabled.HasValue) return _enabled.Value;

                // Release builds never pay for this.
                if (!Debug.isDebugBuild)
                {
                    _enabled = false;
                    return false;
                }

                // Before the first scene exists, Resources may not be queryable yet. Report ON so
                // the early engine hooks still land on the timeline, but do NOT cache: caching an
                // unresolved default would pin profiling on for the whole session and silently
                // ignore the PfeDebugSettings switch. HookAfterSceneLoad makes it reliable.
                if (!_settingsReliable) return true;

                var settings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
                // A missing asset leaves profiling ON so the tool works out of the box in a
                // development build. The asset is the intended switch.
                bool value = settings == null || settings.ProfilingEnabled;

                _enabled = value;
                return value;
            }
            set => _enabled = value;
        }

        /// <summary>Drops all recorded data. Does not change <see cref="Enabled"/>.</summary>
        public static void Reset()
        {
            lock (Sync)
            {
                Regions.Clear();
                Order.Clear();
                Stack.Clear();
                Timeline.Clear();
            }
        }

        /// <summary>Logs the report and writes text + JSON to ProfilingDumps/.</summary>
        public static void Dump(string tag = "dump")
        {
            if (!Enabled) return;

            Debug.Log(BuildReport());
            WriteFiles(tag);
        }

        // ── Report ───────────────────────────────────────────────────────────────────

        public static string BuildReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== PFE PROFILE =====");

            lock (Sync)
            {
                sb.AppendLine("-- timeline (ms from profiler start; top-level spans + marks only) --");
                sb.AppendLine("   nested regions are not repeated here — see the regions table for their totals");
                double prev = 0;
                foreach (TimelineEntry entry in Timeline)
                {
                    sb.Append("  ").Append(entry.EndMs.ToString("F1", Inv).PadLeft(9)).Append(" ms  (+")
                      .Append((entry.EndMs - prev).ToString("F1", Inv).PadLeft(8)).Append(" ms)  ")
                      .Append(entry.Id);

                    if (entry.IsSpan)
                    {
                        sb.Append("  [span ").Append((entry.EndMs - entry.StartMs).ToString("F1", Inv)).Append("]");
                    }
                    if (!string.IsNullOrEmpty(entry.Detail))
                    {
                        sb.Append("  ").Append(entry.Detail);
                    }

                    sb.AppendLine();
                    prev = entry.EndMs;
                }

                sb.AppendLine();
                sb.AppendLine("-- regions (ms) --");
                sb.AppendLine("  id                                calls      total       self        avg  site / reason");

                foreach (RegionInfo r in Order)
                {
                    double avg = r.Calls > 0 ? r.TotalMs / r.Calls : 0.0;

                    sb.Append("  ").Append(r.Id.PadRight(32))
                      .Append(r.Calls.ToString(Inv).PadLeft(6))
                      .Append(r.TotalMs.ToString("F1", Inv).PadLeft(11))
                      .Append(r.SelfMs.ToString("F1", Inv).PadLeft(11))
                      .Append(avg.ToString("F3", Inv).PadLeft(11))
                      .Append("  ").Append(SiteLabel(r))
                      .AppendLine();

                    if (!string.IsNullOrEmpty(r.Reason))
                    {
                        sb.Append("  ").Append(new string(' ', 32)).Append("      ")
                          .Append("           ").Append("           ").Append("           ")
                          .Append("  -> ").AppendLine(r.Reason);
                    }
                }

                // Answers "where do I look next?" without scanning the table by eye. SELF time is
                // the right sort key: a region with a big total but a tiny self is just a parent
                // wrapping someone else's cost, and instrumenting it further would be wasted work.
                sb.AppendLine();
                sb.AppendLine("-- top self time (exclusive) --");
                var hot = new List<RegionInfo>(Order);
                hot.Sort((a, b) => b.SelfMs.CompareTo(a.SelfMs));

                int shown = 0;
                foreach (RegionInfo r in hot)
                {
                    if (shown >= 12 || r.SelfMs < 0.5) break;

                    sb.Append("  ").Append(r.SelfMs.ToString("F1", Inv).PadLeft(10)).Append(" ms  ")
                      .Append(r.Id.PadRight(32))
                      .Append("calls=").Append(r.Calls.ToString(Inv).PadLeft(6))
                      .Append("  ").Append(SiteLabel(r))
                      .AppendLine();
                    shown++;
                }

                if (shown == 0)
                {
                    sb.AppendLine("  (no region above 0.5 ms self time)");
                }
            }

            sb.AppendLine("=======================");
            return sb.ToString();
        }

        public static string BuildJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"generatedAt\": \"").Append(System.DateTime.UtcNow.ToString("o", Inv)).Append("\",\n");

            lock (Sync)
            {
                sb.Append("  \"regions\": [\n");
                for (int i = 0; i < Order.Count; i++)
                {
                    RegionInfo r = Order[i];
                    sb.Append("    { \"id\": \"").Append(Escape(r.Id)).Append("\"")
                      .Append(", \"calls\": ").Append(r.Calls.ToString(Inv))
                      .Append(", \"totalMs\": ").Append(r.TotalMs.ToString("F3", Inv))
                      .Append(", \"selfMs\": ").Append(r.SelfMs.ToString("F3", Inv))
                      .Append(", \"file\": \"").Append(Escape(ShortPath(r.File))).Append("\"")
                      .Append(", \"line\": ").Append(r.Line.ToString(Inv))
                      .Append(", \"sites\": ").Append(r.SeenSites.Count.ToString(Inv))
                      .Append(", \"reason\": \"").Append(Escape(r.Reason)).Append("\" }");
                    if (i < Order.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }

                sb.Append("  ],\n  \"timeline\": [\n");
                for (int i = 0; i < Timeline.Count; i++)
                {
                    TimelineEntry e = Timeline[i];
                    sb.Append("    { \"id\": \"").Append(Escape(e.Id)).Append("\"")
                      .Append(", \"startMs\": ").Append(e.StartMs.ToString("F3", Inv))
                      .Append(", \"endMs\": ").Append(e.EndMs.ToString("F3", Inv))
                      .Append(", \"span\": ").Append(e.IsSpan ? "true" : "false")
                      .Append(", \"detail\": \"").Append(Escape(e.Detail)).Append("\" }");
                    if (i < Timeline.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append("  ]\n}\n");
            }

            return sb.ToString();
        }

        // ── Internals ────────────────────────────────────────────────────────────────

        public readonly struct Scope : System.IDisposable
        {
            readonly bool _active;
            internal Scope(bool active) { _active = active; }
            public void Dispose() { if (_active) End(); }
        }

        sealed class RegionInfo
        {
            public string Id;
            public string Reason;
            public string File;
            public int Line;
            public int Calls;
            public double TotalMs;
            public double SelfMs;
            public HashSet<string> SeenSites;
        }

        struct Frame
        {
            public RegionInfo Region;
            public long StartTs;
            public long ResumeTs;
            public int Depth;
        }

        struct TimelineEntry
        {
            public string Id;
            public string Detail;
            public double StartMs;
            public double EndMs;
            public bool IsSpan;
        }

        const int MaxTimelineEntries = 20000;

        static readonly object Sync = new object();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Dictionary<string, RegionInfo> Regions = new Dictionary<string, RegionInfo>(System.StringComparer.Ordinal);
        static readonly List<RegionInfo> Order = new List<RegionInfo>();
        static readonly List<Frame> Stack = new List<Frame>(32);
        static readonly List<TimelineEntry> Timeline = new List<TimelineEntry>();
        static readonly long OriginTs = Stopwatch.GetTimestamp();
        static bool? _enabled;
        static bool _settingsReliable;

        static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        static string SiteKey(string file, int line) => file + ":" + line;

        static RegionInfo GetOrCreate(string id, string reason, string file, int line)
        {
            if (Regions.TryGetValue(id, out RegionInfo existing)) return existing;

            var region = new RegionInfo
            {
                Id = id,
                Reason = reason,
                File = file,
                Line = line,
                SeenSites = new HashSet<string>()
            };

            Regions[id] = region;
            Order.Add(region);
            return region;
        }

        static void Begin(string id, string reason, string file, int line)
        {
            long now = Stopwatch.GetTimestamp();

            lock (Sync)
            {
                // Registry mutation happens under the lock — region ids can be touched from
                // worker threads during boot (asset loading, async scene setup).
                RegionInfo region = GetOrCreate(id, reason, file, line);
                region.Calls++;
                region.SeenSites.Add(SiteKey(file, line));

                // Exclusive-time accounting: charge the parent for the time it ran since it last
                // became the top of the stack, before this child took over.
                if (Stack.Count > 0)
                {
                    int top = Stack.Count - 1;
                    Frame parent = Stack[top];
                    parent.Region.SelfMs += ToMs(now - parent.ResumeTs);
                    Stack[top] = parent;
                }

                Stack.Add(new Frame
                {
                    Region = region,
                    StartTs = now,
                    ResumeTs = now,
                    Depth = Stack.Count
                });
            }
        }

        static void End()
        {
            long now = Stopwatch.GetTimestamp();

            lock (Sync)
            {
                if (Stack.Count == 0) return;   // unbalanced Dispose — ignore rather than corrupt the stack

                int top = Stack.Count - 1;
                Frame frame = Stack[top];
                Stack.RemoveAt(top);

                frame.Region.TotalMs += ToMs(now - frame.StartTs);

                // Charge this region for the work it did AFTER its last child finished.
                // ResumeTs is moved forward by every child's End(), so for a leaf region
                // ResumeTs is still StartTs and this equals the whole span. WITHOUT this line
                // SelfMs only ever grows from a child's Begin, so every leaf region reported
                // 0.0 self — which made the "top self time" block blind to real hotspots.
                frame.Region.SelfMs += ToMs(now - frame.ResumeTs);

                if (frame.Depth == 0 && Timeline.Count < MaxTimelineEntries)
                {
                    Timeline.Add(new TimelineEntry
                    {
                        Id = frame.Region.Id,
                        Detail = null,
                        StartMs = ToMs(frame.StartTs - OriginTs),
                        EndMs = ToMs(now - OriginTs),
                        IsSpan = true
                    });
                }

                // The parent resumes now; everything until its next child counts as its own time.
                if (Stack.Count > 0)
                {
                    int p = Stack.Count - 1;
                    Frame parent = Stack[p];
                    parent.ResumeTs = now;
                    Stack[p] = parent;
                }
            }
        }

        static string SiteLabel(RegionInfo r)
        {
            string site = ShortPath(r.File) + ":" + r.Line.ToString(Inv);
            if (r.SeenSites != null && r.SeenSites.Count > 1)
            {
                site += " (+" + (r.SeenSites.Count - 1).ToString(Inv) + " more)";
            }
            return site;
        }

        static string ShortPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string normalized = path.Replace('\\', '/');
            int i = normalized.LastIndexOf("/Assets/", System.StringComparison.Ordinal);
            return i >= 0 ? normalized.Substring(i + 1) : Path.GetFileName(normalized);
        }

        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static void WriteFiles(string tag)
        {
            try
            {
                string dir = DumpDirectory();
                Directory.CreateDirectory(dir);

                string stamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", Inv);
                string baseName = "profiling_" + Sanitize(tag) + "_" + stamp;

                File.WriteAllText(Path.Combine(dir, baseName + ".txt"), BuildReport());
                File.WriteAllText(Path.Combine(dir, baseName + ".json"), BuildJson());
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PfeProfiler] Could not write dump: {e.Message}");
            }
        }

        static string DumpDirectory()
        {
            // Same folder the Unity Profiler captures go to (ProfilerCaptures/), so all
            // profiling output lives in one place and one .gitignore rule covers both.
#if UNITY_EDITOR
            return Path.Combine(Application.dataPath, "..", "ProfilerCaptures");
#else
            return Path.Combine(Application.persistentDataPath, "ProfilerCaptures");
#endif
        }

        static string Sanitize(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "dump";

            var sb = new StringBuilder(tag.Length);
            foreach (char c in tag)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }
            return sb.ToString();
        }

        // ── Engine hooks: give the timeline a frame-0 anchor ─────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void HookAfterAssemblies() => Mark("AfterAssembliesLoaded");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void HookBeforeSceneLoad() => Mark("BeforeSceneLoad");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void HookAfterSceneLoad()
        {
            // The first scene exists, so Resources is queryable. From here on the settings asset
            // is the authoritative switch and Enabled may cache its verdict.
            _settingsReliable = true;

            Mark("AfterSceneLoad");

            if (!Enabled) return;

            // Run 17 left 485.8 ms unaccounted for between this hook and GameManager.Start.
            // Open a span here; GameManager.Start closes it. Whatever that region reports as SELF
            // time is the part of the hole that is not inside any of our own regions — i.e. engine
            // and PlayerLoop work we do not control.
            OpenSpan("boot.afterSceneLoadGap",
                "boot: gap between the AfterSceneLoad hook and GameManager.Start. Run 17 showed 485.8ms here with no region covering it");

            var go = new GameObject("[PfeProfilerAutoDump]");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<PfeProfilerAutoDump>();
        }
    }
}
