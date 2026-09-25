#if UNITY_EDITOR
// Exports the currently-loaded Deep Profile session as a small plain-text report.
// Purpose: make Unity Profiler captures readable by an LLM (raw .raw/.data captures are binary).
//
// Usage:
//   1. Window > Analysis > Profiler > enable Deep Profile
//   2. Enter Play Mode / launch the player, let it boot
//   3. Wait until boot finishes, stop
//   4. PFE > Tools > Export Profiler Hierarchy ...
//   5. The .txt is written to <ProjectRoot>/ProfilerDumps/ and revealed in Explorer.
//
// API notes (Unity changed these across versions):
//   - ProfilerDriver      -> UnityEditorInternal
//   - HierarchyFrameDataView -> UnityEditor.Profiling
//   - self/total time are COLUMNS, not methods: GetItemColumnData(id, columnSelfTime)
//   - there is no itemCount; enumerate via GetRootItemID() + GetItemChildren()

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;   // ProfilerDriver
using UnityEngine;

namespace PFE.EditorTools
{
    public static class ProfilerHierarchyExporter
    {
        // ---- knobs: tune these to keep the dump small enough to paste into a chat ----
        const int   MaxThreads        = 8;     // how many thread indices to try
        const float MinSelfMs         = 0.25f; // ignore items cheaper than this (per frame)
        const int   MaxDepth          = 16;    // 1 = roots only
        const int   TopAggregateCount = 250;   // rows in the aggregated table
        const int   MaxFramesToScan   = 4000;  // hard cap so a long session can't hang the editor

        [MenuItem("PFE/Tools/Export Profiler Hierarchy ...", false, 900)]
        public static void Export()
        {
            int first, last;
            try
            {
                first = ProfilerDriver.firstFrameIndex;
                last  = ProfilerDriver.lastFrameIndex;
            }
            catch (Exception e)
            {
                Debug.LogError("[ProfilerExport] ProfilerDriver unavailable: " + e.Message);
                return;
            }

            if (last < first || last < 0)
            {
                Debug.LogError("[ProfilerExport] No profiler data loaded. Record a Deep Profile session first.");
                return;
            }

            int from = first;
            int to   = Mathf.Min(last, first + MaxFramesToScan - 1);

            var sb = new StringBuilder();
            sb.AppendLine("# Unity Profiler — Deep Profile export");
            sb.AppendLine($"# Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"# Session   : Unity {Application.unityVersion}, {Application.productName}");
            sb.AppendLine($"# Frames    : {from} .. {to}  (session range {first} .. {last})");
            sb.AppendLine($"# Filters   : selfMs >= {MinSelfMs.ToString(CultureInfo.InvariantCulture)}, depth <= {MaxDepth}");
            sb.AppendLine();

            bool wroteAny = false;
            for (int threadIndex = 0; threadIndex < MaxThreads; threadIndex++)
            {
                string threadName = ProbeThreadName(from, threadIndex);
                if (threadName == null) continue;

                string report = ReportThread(threadIndex, threadName, from, to);
                if (string.IsNullOrEmpty(report)) continue;

                sb.AppendLine(report);
                wroteAny = true;
            }

            if (!wroteAny)
                sb.AppendLine("(no data — try lowering MinSelfMs or increasing MaxThreads in ProfilerHierarchyExporter.cs)");

            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProfilerDumps"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"profiler_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(file, sb.ToString());

            Debug.Log($"[ProfilerExport] Wrote {file} ({sb.Length / 1024} KB)");
            EditorUtility.RevealInFinder(file);
        }

        // ------------------------------------------------------------------

        static string ProbeThreadName(int frame, int threadIndex)
        {
            using (var v = GetView(frame, threadIndex))
            {
                if (v == null) return null;
                return ThreadLabel(v, threadIndex);
            }
        }

        static HierarchyFrameDataView GetView(int frame, int threadIndex)
        {
            try
            {
                var v = ProfilerDriver.GetHierarchyFrameDataView(
                    frame,
                    threadIndex,
                    HierarchyFrameDataView.ViewModes.Default,
                    HierarchyFrameDataView.columnTotalTime,
                    false); // sort descending by total time
                if (v == null || !v.valid) { v?.Dispose(); return null; }
                return v;
            }
            catch { return null; }
        }

        // threadName is not guaranteed on every Unity version -> read it reflectively.
        static string ThreadLabel(HierarchyFrameDataView v, int idx)
        {
            try
            {
                var p = v.GetType().GetProperty("threadName");
                if (p != null)
                {
                    var s = p.GetValue(v, null) as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { /* fall through */ }
            return "thread" + idx;
        }

        // Enumerate every node by walking down from the root.
        static void CollectItems(HierarchyFrameDataView v, List<int> outIds)
        {
            outIds.Clear();
            int root = v.GetRootItemID();
            var stack = new Stack<int>();
            stack.Push(root);
            var kids = new List<int>();

            while (stack.Count > 0)
            {
                int id = stack.Pop();
                outIds.Add(id);

                kids.Clear();
                try { v.GetItemChildren(id, kids); } catch { continue; }
                for (int i = 0; i < kids.Count; i++)
                    stack.Push(kids[i]);
            }
        }

        // ------------------------------------------------------------------

        static string ReportThread(int threadIndex, string threadName, int frameFrom, int frameTo)
        {
            var aggSelf  = new Dictionary<string, float>();
            var aggTotal = new Dictionary<string, float>();
            var seenIn   = new Dictionary<string, int>();
            var depthOf  = new Dictionary<string, int>();

            int   worstFrame = frameFrom;
            float worstTotal = -1f;
            int   frameCount = 0;
            var   ids        = new List<int>();

            for (int f = frameFrom; f <= frameTo; f++)
            {
                using (var v = GetView(f, threadIndex))
                {
                    if (v == null) continue;
                    frameCount++;

                    CollectItems(v, ids);
                    for (int k = 0; k < ids.Count; k++)
                    {
                        int   id      = ids[k];
                        int   depth   = v.GetItemDepth(id);
                        if (depth > MaxDepth) continue;

                        float selfMs  = ReadFloat(v, id, HierarchyFrameDataView.columnSelfTime);
                        float totalMs = ReadFloat(v, id, HierarchyFrameDataView.columnTotalTime);

                        if (depth == 0 && totalMs > worstTotal) { worstTotal = totalMs; worstFrame = f; }
                        if (selfMs < MinSelfMs) continue;

                        string path = v.GetItemPath(id);
                        if (string.IsNullOrEmpty(path)) path = v.GetItemName(id);

                        aggSelf.TryGetValue(path, out float s);
                        aggSelf[path]  = s + selfMs;
                        aggTotal.TryGetValue(path, out float t);
                        aggTotal[path] = t + totalMs;
                        seenIn.TryGetValue(path, out int c);
                        seenIn[path]   = c + 1;
                        depthOf[path]  = depth;
                    }
                }
            }

            if (aggSelf.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("==================================================================");
            sb.AppendLine($"THREAD [{threadIndex}] \"{threadName}\"  —  {frameCount} frames");
            sb.AppendLine("==================================================================");
            sb.AppendLine();

            var rows = new List<string>(aggSelf.Keys);
            rows.Sort((a, b) => aggSelf[b].CompareTo(aggSelf[a]));

            sb.AppendLine($"## TOP {Mathf.Min(TopAggregateCount, rows.Count)} HOTTEST PATHS (summed self time over {frameCount} frames)");
            sb.AppendLine("## format: selfMs | totalMs | framesPresent | depth | path");
            sb.AppendLine();

            for (int i = 0; i < rows.Count && i < TopAggregateCount; i++)
            {
                string p = rows[i];
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,10:F2} | {1,10:F2} | {2,5} | {3,2} | {4}",
                    aggSelf[p], aggTotal[p], seenIn[p], depthOf[p], p));
            }

            sb.AppendLine();
            sb.AppendLine(ReportSingleFrame(threadIndex, threadName, worstFrame));
            return sb.ToString();
        }

        // The single most expensive frame as an indented tree — the closest thing to a
        // callstack view, and the part an LLM should read first.
        static string ReportSingleFrame(int threadIndex, string threadName, int frame)
        {
            var sb = new StringBuilder();
            var ids = new List<int>();

            using (var v = GetView(frame, threadIndex))
            {
                if (v == null) return string.Empty;

                sb.AppendLine($"## WORST SINGLE FRAME #{frame} on \"{threadName}\"");
                sb.AppendLine("## indented tree: [selfMs / totalMs] name");
                sb.AppendLine();

                CollectItems(v, ids);
                for (int k = 0; k < ids.Count; k++)
                {
                    int   id      = ids[k];
                    int   depth   = v.GetItemDepth(id);
                    if (depth > MaxDepth) continue;

                    float selfMs  = ReadFloat(v, id, HierarchyFrameDataView.columnSelfTime);
                    float totalMs = ReadFloat(v, id, HierarchyFrameDataView.columnTotalTime);
                    if (totalMs < MinSelfMs) continue; // total, so parents of hot children survive

                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}[{1,8:F2} / {2,8:F2}] {3}",
                        new string(' ', depth * 2), selfMs, totalMs, v.GetItemName(id)));
                }
            }

            sb.AppendLine();
            return sb.ToString();
        }

        // Column values are read as string on purpose: GetItemColumnData(string) is the
        // overload that exists on every Unity version we care about.
        static float ReadFloat(HierarchyFrameDataView v, int id, int column)
        {
            try
            {
                string s = v.GetItemColumnData(id, column);
                if (string.IsNullOrEmpty(s)) return 0f;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    return (float)d;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out double d2))
                    return (float)d2;
            }
            catch { /* unreadable column -> treat as 0 */ }
            return 0f;
        }
    }
}
#endif
