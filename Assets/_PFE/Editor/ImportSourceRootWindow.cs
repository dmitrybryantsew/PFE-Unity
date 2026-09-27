#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using PFE.Editor.Importers;

namespace PFE.Editor
{
    /// <summary>
    /// Set (or clear) the AS3 source root the one-shot importers read from.
    ///
    /// <para><b>Why this exists.</b> The importers used to depend on the <c>PFE_IMPORT_ROOT</c>
    /// environment variable with no way to set it from inside Unity, and with no validation: a wrong
    /// or absent value produced an empty path and a message that named no cause. This window is the
    /// in-editor answer, and it shows <i>where the current root came from</i> — environment variable,
    /// saved setting, or auto-discovery — which is the question that actually costs time when an
    /// import fails.</para>
    ///
    /// <para>The saved value lives in <see cref="EditorPrefs"/>, so it is per-machine and per-user
    /// rather than committed: a copied project points at its own copy of the source tree without
    /// changing anything in the repository.</para>
    /// </summary>
    public class ImportSourceRootWindow : EditorWindow
    {
        private string _candidate = string.Empty;
        private string _diagnosis = string.Empty;
        private Vector2 _scroll;

        /// <summary>How many source trees validated. Above one, the choice is worth showing, not hiding.</summary>
        private int _validRootCount;

        [MenuItem("PFE/Data/Import Source Root…", priority = 40)]
        public static void ShowWindow()
        {
            var window = GetWindow<ImportSourceRootWindow>("Import Source Root");
            window.minSize = new Vector2(560f, 420f);
            window.Refresh();
        }

        /// <summary>Log the full resolution chain to the console — useful when a user pastes a log.</summary>
        [MenuItem("PFE/Data/Log Import Source Resolution", priority = 41)]
        public static void LogResolution()
        {
            Debug.Log("[SourceImportPaths]\n" + SourceImportPaths.DescribeResolution());
        }

        private void OnEnable()
        {
            Refresh();
        }

        private void Refresh()
        {
            string resolved = SourceImportPaths.SourceProjectRoot;
            if (!string.IsNullOrWhiteSpace(resolved))
                _candidate = resolved;

            _diagnosis         = SourceImportPaths.DescribeResolution();
            _validRootCount    = SourceImportPaths.FindAllValidRoots().Count;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("AS3 source root", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "The folder that CONTAINS 'pfe/'. Importers read AllData.as, Snd.as, assets.swf and the " +
                "sprite folders from under it.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6f);

            // ── Current state ────────────────────────────────────────────────
            bool resolved = SourceImportPaths.IsResolved;
            EditorGUILayout.HelpBox(
                resolved
                    ? "A source root is resolved. Importers will find their inputs."
                    : "No source root resolved — importer inputs will report as missing. " +
                      "Set one below, or place the source tree next to this project.",
                resolved ? MessageType.Info : MessageType.Warning);

            if (_validRootCount > 1)
            {
                EditorGUILayout.HelpBox(
                    $"{_validRootCount} source trees are reachable and the importer picks the first. " +
                    "If that is not the one you mean, press Save below to pin the right one — " +
                    "importing from a stale copy produces plausible, wrong data that no test can catch.",
                    MessageType.Warning);
            }

            DrawDiagnosis();
            EditorGUILayout.Space(6f);

            // ── Edit ─────────────────────────────────────────────────────────
            EditorGUILayout.LabelField("Set a root", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            _candidate = EditorGUILayout.TextField("Folder", _candidate);
            if (GUILayout.Button("Browse…", GUILayout.Width(80f)))
            {
                string picked = EditorUtility.OpenFolderPanel(
                    "Select the folder that contains 'pfe/'", _candidate, string.Empty);
                if (!string.IsNullOrWhiteSpace(picked))
                    _candidate = picked;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Save"))
            {
                if (SourceImportPaths.SaveRoot(_candidate, out string error))
                {
                    Debug.Log($"[SourceImportPaths] Saved source root: {SourceImportPaths.SourceProjectRoot}");
                    Refresh();
                }
                else
                {
                    EditorUtility.DisplayDialog("Cannot save that root", error, "OK");
                }
            }

            if (GUILayout.Button("Clear saved"))
            {
                SourceImportPaths.ClearSavedRoot();
                Refresh();
            }

            if (GUILayout.Button("Re-resolve now"))
            {
                SourceImportPaths.Invalidate();
                Refresh();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(
                "Priority: the PFE_IMPORT_ROOT environment variable wins over the saved setting, which " +
                "wins over auto-discovery. A discovered root is shown but not saved — press Save to pin it.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndScrollView();
        }

        private void DrawDiagnosis()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Height from the line count: CalcSize on a label does not account for wrapping, and a
            // SelectableLabel clipped to one line is exactly the unhelpful output this window exists
            // to replace.
            int lines = 1;
            for (int i = 0; i < _diagnosis.Length; i++)
                if (_diagnosis[i] == '\n') lines++;

            float height = lines * EditorStyles.label.lineHeight + 8f;

            EditorGUILayout.SelectableLabel(_diagnosis, EditorStyles.label,
                GUILayout.MinHeight(height));

            EditorGUILayout.EndVertical();
        }
    }
}
#endif
