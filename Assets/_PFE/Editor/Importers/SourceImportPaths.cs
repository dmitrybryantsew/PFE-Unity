using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Where the AS3 source tree lives, for the one-shot editor importers.
    ///
    /// <para><b>What went wrong before.</b> Every path here was built from
    /// <c>PFE_IMPORT_ROOT</c> alone, and <see cref="Combine"/> returned <see cref="string.Empty"/>
    /// when that variable was unset. So a missing variable did not fail at the point of
    /// misconfiguration — it produced a <i>silently empty</i> path, and each importer reported
    /// <c>"AllData.as not found at: "</c> with nothing after the colon. The file was never missing;
    /// the importer was looking nowhere. Worse, the variable is per-process environment state that
    /// the Unity Editor does not inherit when it is launched from a shortcut, so a setup that worked
    /// from a shell broke the moment the editor was opened the normal way — and re-running the import
    /// was the natural reaction, which is the opposite of what was needed.</para>
    ///
    /// <para><b>Resolution order</b> (first candidate whose probe file exists wins):</para>
    /// <list type="number">
    /// <item><c>PFE_IMPORT_ROOT</c> — unchanged, and still first, so existing setups, scripts and CI
    /// behave exactly as before.</item>
    /// <item><b>The saved setting</b> — <see cref="ImportRootEditorPrefsKey"/>, written by
    /// <c>PFE/Data/Import Source Root…</c>. This is the "copied project" case: point it once.</item>
    /// <item><b>Auto-discovery</b> — a bounded walk of the project's ancestors, their siblings, and a
    /// few conventional dev roots, looking for a directory that contains
    /// <c>pfe/scripts/fe/AllData.as</c>. This is what makes a fresh clone work with no setup at
    /// all.</item>
    /// </list>
    ///
    /// <para><b>Deliberately lazy, and deliberately not persisted on discovery.</b> Nothing here runs
    /// at project load — resolution happens on first use, so the README's promise ("you do not need
    /// to set <c>PFE_IMPORT_ROOT</c> just to open the project") still holds and no domain reload pays
    /// for a disk walk. A discovered root is remembered <i>in memory for the session</i> but is not
    /// written to <see cref="EditorPrefs"/>: a getter that silently persists is a surprising thing to
    /// leave in a codebase, and <see cref="DescribeResolution"/> plus the settings window make the
    /// discovery visible and let the user pin it deliberately.</para>
    /// </summary>
    public static class SourceImportPaths
    {
        public const string ImportRootEnvironmentVariable = "PFE_IMPORT_ROOT";

        /// <summary>Where <c>PFE/Data/Import Source Root…</c> saves an explicit root.</summary>
        public const string ImportRootEditorPrefsKey = "PFE.ImportRoot";

        /// <summary>
        /// The file that identifies a valid source root. Any of the imported inputs would do; this one
        /// is used because it is the most-imported file and its absence means the root is wrong for
        /// almost every importer.
        /// </summary>
        public const string RootProbeRelativePath = "pfe/scripts/fe/AllData.as";

        /// <summary>How many ancestors of the Unity project to search. Bounded, so a deep path is cheap.</summary>
        private const int AncestorDepth = 3;

        /// <summary>Cap on children enumerated per directory. A drive root would otherwise be scanned whole.</summary>
        private const int MaxChildrenPerDirectory = 64;

        // ── Resolution state ─────────────────────────────────────────────────

        /// <summary>Per-domain-reload cache. <see cref="s_resolved"/> distinguishes "not yet asked" from "asked, found nothing".</summary>
        private static string s_root = string.Empty;
        private static bool   s_resolved;

        /// <summary>Where the cached root came from — for <see cref="DescribeResolution"/> only.</summary>
        private static string s_rootSource = "not resolved";

        /// <summary>
        /// The root of the source tree — the directory that <i>contains</i> <c>pfe/</c>.
        /// <see cref="string.Empty"/> when no candidate resolved, which is the pre-existing behaviour
        /// and keeps every <c>File.Exists</c> check in the importers false.
        /// </summary>
        public static string SourceProjectRoot => Resolve();

        /// <summary>True when a root was found and its probe file is present.</summary>
        public static bool IsResolved => !string.IsNullOrWhiteSpace(Resolve());

        // ── Derived paths (unchanged surface) ────────────────────────────────

        public static string AllDataAsPath => Combine("pfe", "scripts", "fe", "AllData.as");
        public static string FeScriptsRoot => Combine("pfe", "scripts", "fe");
        public static string ScriptsRoot => Combine("pfe", "scripts");
        public static string PfeRoot => Combine("pfe");
        public static string PfeSpritesRoot => Combine("pfe", "sprites");
        public static string AssetsSwfPath => Combine("pfe", "scripts", "_assets", "assets.swf");
        public static string AssetsExportRoot => Combine("pfe", "scripts", "_assets");
        public static string SoundDefinitionPath => Combine("pfe", "scripts", "fe", "Snd.as");
        public static string PfeSwfPath => Combine("pfe.swf");

        public static string[] RoomGraphicsExportRoots => NonEmpty(
            PfeSpritesRoot,
            TextureSpritesRoot("texture"),
            TextureSpritesRoot("texture1"));

        public static string[] MapObjectExportRoots => NonEmpty(
            TextureSpritesRoot("texture1"),
            PfeSpritesRoot);

        public static string[] MapObjectSymbolInventoryPaths => NonEmpty(
            SymbolInventoryPath("texture1"));

        public static string[] MapObjectWrapperScriptRoots => NonEmpty(
            TextureScriptsRoot("texture1"),
            ScriptsRoot);

        public static string TextureImagesRoot(string swfFolderName)
        {
            return Combine($"{swfFolderName}.swf", "images");
        }

        public static string TextureSpritesRoot(string swfFolderName)
        {
            return Combine($"{swfFolderName}.swf", "sprites");
        }

        public static string TextureScriptsRoot(string swfFolderName)
        {
            return Combine($"{swfFolderName}.swf", "scripts");
        }

        public static string SymbolInventoryPath(string swfFolderName)
        {
            return Combine($"{swfFolderName}.swf", "symbolClass", "symbols.csv");
        }

        // ── Settings ─────────────────────────────────────────────────────────

        /// <summary>The saved root, or <see cref="string.Empty"/> when unset.</summary>
        public static string SavedRoot => Normalize(EditorPrefs.GetString(ImportRootEditorPrefsKey, string.Empty));

        /// <summary>Persist an explicit root. Validates before saving so a typo cannot be stored.</summary>
        public static bool SaveRoot(string root, out string error)
        {
            string normalized = Normalize(root);

            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = "Path is empty.";
                return false;
            }

            if (!Directory.Exists(normalized))
            {
                error = $"Not a directory: {normalized}";
                return false;
            }

            if (!File.Exists(ProbePathFor(normalized)))
            {
                error =
                    $"That folder does not contain '{RootProbeRelativePath}'.\n\n" +
                    "Point at the folder that CONTAINS 'pfe' — normally the root of the pfeToUnity " +
                    "extraction repo (the one holding 'pfe/', 'sprites/' and 'texture1.swf/').";
                return false;
            }

            EditorPrefs.SetString(ImportRootEditorPrefsKey, normalized);
            Invalidate();
            error = null;
            return true;
        }

        /// <summary>Forget the saved root. <c>PFE_IMPORT_ROOT</c> and discovery still apply.</summary>
        public static void ClearSavedRoot()
        {
            EditorPrefs.DeleteKey(ImportRootEditorPrefsKey);
            Invalidate();
        }

        /// <summary>Drop the session cache so the next access re-resolves. Call after changing anything above.</summary>
        public static void Invalidate()
        {
            s_resolved   = false;
            s_root       = string.Empty;
            s_rootSource = "not resolved";
        }

        // ── Diagnostics ──────────────────────────────────────────────────────

        /// <summary>Where the current root came from, and what each candidate resolved to.</summary>
        public static string DescribeResolution()
        {
            var sb = new System.Text.StringBuilder();

            string env   = Normalize(Environment.GetEnvironmentVariable(ImportRootEnvironmentVariable));
            string saved = SavedRoot;
            string root  = Resolve();

            sb.AppendLine($"Root in use  : {(string.IsNullOrWhiteSpace(root) ? "<none>" : root)}");
            sb.AppendLine($"Source       : {s_rootSource}");
            sb.AppendLine($"Probe file   : {(string.IsNullOrWhiteSpace(root) ? "<none>" : ProbePathFor(root))}");
            sb.AppendLine($"Probe exists : {(string.IsNullOrWhiteSpace(root) ? "n/a" : File.Exists(ProbePathFor(root)).ToString())}");
            sb.AppendLine();
            sb.AppendLine($"{ImportRootEnvironmentVariable} : {(string.IsNullOrWhiteSpace(env) ? "<unset>" : env)}");
            if (!string.IsNullOrWhiteSpace(env))
                sb.AppendLine($"    probe    : {(File.Exists(ProbePathFor(env)) ? "OK" : "MISSING — this value is being ignored")}");
            sb.AppendLine($"Saved setting : {(string.IsNullOrWhiteSpace(saved) ? "<unset>" : saved)}");
            sb.AppendLine($"Unity project : {ProjectRoot()}");

            var valid = DiscoverAll();
            sb.AppendLine($"Discovered    : {(valid.Count == 0 ? "<nothing found>" : valid[0])}");

            if (valid.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine($"AMBIGUOUS — {valid.Count} source trees are reachable:");
                foreach (string v in valid)
                    sb.AppendLine($"    {(v == valid[0] ? "*" : " ")} {v}");
                sb.AppendLine("The one marked * is used. If that is not the tree you intend, press Save to pin");
                sb.AppendLine("the right one — importing from a stale copy produces plausible, wrong data.");
            }

            return sb.ToString();
        }

        /// <summary>
        /// One shared, actionable message for "this importer's input is not there". Replaces the bare
        /// <c>"... not found at: {path}"</c> that printed an empty path and named no cause.
        /// </summary>
        /// <param name="path">The path that was probed (may be empty).</param>
        /// <param name="what">Human name of the input, e.g. <c>"AllData.as"</c>.</param>
        public static string MissingSourceMessage(string path, string what)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine($"{what} not found: {(string.IsNullOrWhiteSpace(path) ? "<no source root configured>" : path)}");

            if (string.IsNullOrWhiteSpace(Resolve()))
            {
                sb.AppendLine();
                sb.AppendLine("No AS3 source root could be resolved, so the path above is empty rather than wrong.");
                sb.AppendLine("Fix it any of these ways:");
                sb.AppendLine("  1. Menu  PFE/Data/Import Source Root…  and pick the folder that CONTAINS 'pfe/'.");
                sb.AppendLine($"  2. Set the environment variable {ImportRootEnvironmentVariable} to that folder and restart Unity");
                sb.AppendLine("     (Unity does not inherit variables set in a shell you opened afterwards).");
                sb.AppendLine("  3. Do nothing — a source root next to this project is found automatically.");
                sb.AppendLine();
                sb.Append("Point it at the pfeToUnity extraction repo root, the one holding 'pfe/', ");
                sb.Append("'sprites/' and 'texture1.swf/' — not at 'pfe/' itself and not at 'pfe/scripts/'.");
            }
            else
            {
                sb.AppendLine($"Resolved source root: {Resolve()}");
                sb.Append("The root resolved, so this particular file is missing from it — the tree may be ");
                sb.Append("incomplete, or the expected layout has changed.");
            }

            return sb.ToString();
        }

        // ── Resolution ───────────────────────────────────────────────────────

        private static string Resolve()
        {
            if (s_resolved) return s_root;
            s_resolved = true;

            string env = Normalize(Environment.GetEnvironmentVariable(ImportRootEnvironmentVariable));
            if (IsValidRoot(env))
            {
                s_root       = env;
                s_rootSource = $"environment variable {ImportRootEnvironmentVariable}";
                return s_root;
            }

            string saved = SavedRoot;
            if (IsValidRoot(saved))
            {
                s_root       = saved;
                s_rootSource = "saved setting (PFE/Data/Import Source Root…)";
                return s_root;
            }

            string discovered = Discover();
            if (IsValidRoot(discovered))
            {
                s_root       = discovered;
                s_rootSource = "auto-discovered (not saved)";
                return s_root;
            }

            // Report the most specific reason we can, because "nothing worked" is the case that
            // wastes the most time.
            if (!string.IsNullOrWhiteSpace(env) && !IsValidRoot(env))
                s_rootSource = $"{ImportRootEnvironmentVariable} is set to '{env}' but holds no '{RootProbeRelativePath}', and nothing else resolved";
            else if (!string.IsNullOrWhiteSpace(saved) && !IsValidRoot(saved))
                s_rootSource = $"saved root '{saved}' holds no '{RootProbeRelativePath}', and nothing else resolved";
            else
                s_rootSource = "no candidate contained " + RootProbeRelativePath;

            s_root = string.Empty;
            return s_root;
        }

        /// <summary>
        /// Every candidate directory that validates, in the order discovery prefers them. Exposed so
        /// the settings window can warn when more than one source tree is reachable.
        ///
        /// <para><b>Why that warning matters more than it looks.</b> A machine can easily hold two
        /// copies — one beside the Unity project, one in a dev folder — and a copy is only as fresh as
        /// the last time someone synced it. Silently importing from the stale one produces plausible,
        /// wrong data that no test can distinguish from correct data, because the importer is the thing
        /// under test. So the choice is reported rather than hidden, and can be pinned with
        /// <see cref="SaveRoot"/>.</para>
        /// </summary>
        public static IReadOnlyList<string> FindAllValidRoots() => DiscoverAll();

        private static string Discover() => DiscoverAll().FirstOrDefault();

        /// <summary>
        /// Bounded search for directories containing the probe file, best candidate first.
        /// Deliberately shallow and capped: this runs on first importer use, not on domain reload, but
        /// an unbounded walk from a drive root would still be a hang waiting to happen.
        /// </summary>
        private static List<string> DiscoverAll()
        {
            var candidates = new List<string>();

            // 1. The project's ancestors — the common case is the source repo one or two levels up,
            //    or beside it.
            string project = ProjectRoot();
            if (!string.IsNullOrWhiteSpace(project))
            {
                string dir = project;
                for (int depth = 0; depth < AncestorDepth && !string.IsNullOrWhiteSpace(dir); depth++)
                {
                    string parent = Path.GetDirectoryName(dir.TrimEnd('/', '\\'));
                    if (string.IsNullOrWhiteSpace(parent)) break;

                    candidates.Add(parent);
                    candidates.AddRange(ChildrenOf(parent, preferred: "pfeToUnity"));
                    dir = parent;
                }
            }

            // 2. Conventional dev roots, in case the project lives somewhere unrelated. `rustProjects`
            //    first because that is where this project's own oracle lives.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                foreach (string conventional in new[]
                         {
                             "Documents/rustProjects",
                             "Documents/GitHub",
                             "source/repos",
                             "Documents",
                             "dev",
                             "projects",
                             "repos",
                         })
                {
                    string root = Normalize(Path.Combine(home, conventional));
                    if (!Directory.Exists(root)) continue;

                    candidates.Add(root);
                    candidates.AddRange(ChildrenOf(root, preferred: "pfeToUnity"));
                }
            }

            // Ranked, then de-duplicated: a directory named `pfeToUnity` is the intended source repo
            // far more often than an arbitrary sibling, and among equals the candidate order above
            // already encodes proximity (project ancestors first, then conventional dev roots). The
            // sort is stable, so that proximity survives as the tie-break.
            return candidates
                .Where(IsValidRoot)
                .OrderByDescending(c => c.EndsWith("pfeToUnity", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Immediate subdirectories, with the one that is most likely to be the source repo first.
        /// Capped, and tolerant of unreadable directories — discovery must never throw out of an
        /// importer.
        ///
        /// <para>Returns a list rather than being an iterator on purpose: <c>yield return</c> is not
        /// allowed in a <c>try</c> that has a <c>catch</c>, and the exception tolerance here is not
        /// optional. Enumeration is streamed through <see cref="Directory.EnumerateDirectories(string)"/>
        /// so the cap stops the walk instead of trimming a fully materialised array — this runs against
        /// ancestor directories, which can include a drive root.</para>
        /// </summary>
        private static List<string> ChildrenOf(string directory, string preferred)
        {
            var result = new List<string>();

            // The likely candidate first, as a single existence check — in the common case the source
            // repo is a sibling called `pfeToUnity` and nothing else needs enumerating.
            string preferredPath = Path.Combine(directory, preferred);
            if (Directory.Exists(preferredPath)) result.Add(preferredPath);

            try
            {
                int taken = 0;
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    if (taken >= MaxChildrenPerDirectory) break;
                    taken++;

                    if (string.Equals(Path.GetFileName(child), preferred, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(child);
                }
            }
            catch (Exception)
            {
                // Unreadable or vanished directory. Discovery is best-effort; a failure here must
                // leave the importer to report a missing root, not throw out of the menu action.
            }

            return result;
        }

        private static bool IsValidRoot(string root)
        {
            return !string.IsNullOrWhiteSpace(root) && File.Exists(ProbePathFor(root));
        }

        private static string ProbePathFor(string root)
        {
            return Normalize(Path.Combine(root, "pfe", "scripts", "fe", "AllData.as"));
        }

        /// <summary>
        /// The Unity project directory (the parent of <c>Assets</c>). <see cref="Application.dataPath"/>
        /// is used rather than the current working directory, which for the editor is not guaranteed
        /// to be the project.
        /// </summary>
        private static string ProjectRoot()
        {
            string dataPath = Application.dataPath;
            if (string.IsNullOrWhiteSpace(dataPath)) return string.Empty;

            string parent = Path.GetDirectoryName(dataPath.TrimEnd('/', '\\'));
            return string.IsNullOrWhiteSpace(parent) ? string.Empty : Normalize(parent);
        }

        // ── Path assembly ────────────────────────────────────────────────────

        static string Combine(params string[] segments)
        {
            string root = Resolve();
            if (string.IsNullOrWhiteSpace(root))
                return string.Empty;

            var parts = new[] { root }.Concat(segments).ToArray();
            return Normalize(Path.Combine(parts));
        }

        static string[] NonEmpty(params string[] values)
        {
            return values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        }

        static string Normalize(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Replace('\\', '/');
        }
    }
}
