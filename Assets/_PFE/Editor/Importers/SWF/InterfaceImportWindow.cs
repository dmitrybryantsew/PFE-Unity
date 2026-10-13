#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// One-click front end for <see cref="InterfaceAssetImporter"/>. Menu: <b>PFE/Art/Import Game
    /// Interface</b>.
    ///
    /// <para><b>Three separate buttons, not one.</b> Importing the art, building the composition asset
    /// and verifying the result are different operations with different failure modes, and the
    /// interesting case is running <i>Verify</i> against a tree somebody else imported. A single "do
    /// everything" button would make "is this tree actually complete?" unanswerable without re-importing
    /// first, which is exactly how the main-menu importer shipped a green run over a tree with nothing
    /// in it.</para>
    ///
    /// <para><b>Nothing is resolved in a field initializer.</b> <see cref="SourceImportPaths"/> reads
    /// <c>EditorPrefs</c>, which throws from a <see cref="ScriptableObject"/> constructor — and an
    /// <see cref="EditorWindow"/> <i>is</i> a ScriptableObject. A throw there aborts the constructor at
    /// the offending field and leaves every later-declared field null, producing a NullReferenceException
    /// on every repaint. Everything is resolved in <see cref="OnEnable"/>.</para>
    /// </summary>
    public class InterfaceImportWindow : EditorWindow
    {
        private Vector2 _scroll;
        private List<string> _log;
        private bool _running;

        [MenuItem("PFE/Art/Import Game Interface")]
        public static void ShowWindow()
        {
            var window = GetWindow<InterfaceImportWindow>("Game Interface Import");
            window.minSize = new Vector2(600, 560);
        }

        private void OnEnable()
        {
            // Built here, not in the field initializer: see the class summary.
            _log ??= new List<string>();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Game Interface Importer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Copies the PipBuck shell, HUD chrome, icon catalogs, cursors and the Lucida Console font " +
                "out of the pfe extraction tree into Assets/_PFE/, then builds PipBuckComposition.asset.\n\n" +
                "UI sounds are not part of this pipeline — pip1/pip2/pip3/button are already imported and " +
                "catalogued by the sound importer.",
                MessageType.Info);

            DrawSourceRoot();
            EditorGUILayout.Space(6);

            GUI.enabled = !_running;
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("1. Import All Interface Assets", GUILayout.Height(30)))
                RunImport();

            if (GUILayout.Button("2. Generate Composition", GUILayout.Height(30)))
                RunGenerate();

            if (GUILayout.Button("3. Verify Integrity", GUILayout.Height(30)))
                RunVerify();

            EditorGUILayout.EndHorizontal();
            GUI.enabled = true;

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy Log", GUILayout.Width(90)))
                GUIUtility.systemCopyBuffer = string.Join("\n", _log);
            if (GUILayout.Button("Clear Log", GUILayout.Width(90)))
                _log.Clear();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (string line in _log)
            {
                var style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                if (line.StartsWith("[ERR]", StringComparison.Ordinal)) style.normal.textColor = new Color(1f, 0.35f, 0.35f);
                else if (line.StartsWith("[WARN]", StringComparison.Ordinal)) style.normal.textColor = new Color(1f, 0.7f, 0.2f);
                else if (line.StartsWith("[OK]", StringComparison.Ordinal)) style.normal.textColor = new Color(0.35f, 0.85f, 0.35f);
                else if (line.StartsWith("[..", StringComparison.Ordinal)) style.normal.textColor = new Color(0.5f, 0.7f, 1f);
                EditorGUILayout.LabelField(line, style);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawSourceRoot()
        {
            bool resolved = SourceImportPaths.IsResolved;

            EditorGUILayout.LabelField("Source Root", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                resolved
                    ? SourceImportPaths.DescribeResolution()
                    : SourceImportPaths.MissingSourceMessage(string.Empty, "AS3 source root"),
                resolved ? MessageType.None : MessageType.Error);

            if (GUILayout.Button("Fix Source Root…"))
                ImportSourceRootWindow.ShowWindow();
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private void RunImport()
        {
            Run("Import", () =>
            {
                var result = InterfaceAssetImporter.Import();

                foreach (string line in result.Log) Log("  " + line);
                foreach (string warning in result.Warnings) Log("[WARN] " + warning);
                foreach (string error in result.Errors) Log("[ERR] " + error);

                // Frame counts are asserted against the spec's expected values, because a re-export that
                // changes one produces a short icon array with no other symptom.
                foreach (var kvp in result.FramesPerSymbol)
                    Log($"  symbol {kvp.Key}: {kvp.Value} frame(s) on disk");

                Log(result.Succeeded
                    ? "[OK] Import finished with no errors."
                    : $"[ERR] Import finished with {result.Errors.Count} error(s) — see above.");
            });
        }

        private void RunGenerate()
        {
            Run("Generate", () =>
            {
                var def = InterfaceAssetImporter.GenerateComposition(out var problems);
                foreach (string problem in problems) Log("[WARN] " + problem);

                string assetPath = AssetDatabase.GetAssetPath(def);
                Log($"[OK] Composition written to {assetPath}");

                bool complete = def.Validate(out string report);
                Log(complete ? "[OK] " + report : "[WARN] " + report);
            });
        }

        private void RunVerify()
        {
            Run("Verify", () =>
            {
                bool ok = InterfaceAssetImporter.Verify(out string report);
                Log(ok ? "[OK] " + report : "[ERR] " + report);

                var def = AssetDatabase.LoadAssetAtPath<PFE.Data.Definitions.PipBuckCompositionDefinition>(
                    InterfaceAssetImporter.CompositionPath);
                if (def == null)
                {
                    Log("[WARN] No composition asset — run step 2.");
                    return;
                }

                bool complete = def.Validate(out string defReport);
                Log(complete ? "[OK] " + defReport : "[WARN] " + defReport);
            });
        }

        private void Run(string label, Action action)
        {
            _running = true;
            Log($"[..] {label} started");

            try
            {
                action();
            }
            catch (Exception e)
            {
                Log($"[ERR] {label} threw {e.GetType().Name}: {e.Message}");
                Debug.LogException(e);
            }
            finally
            {
                _running = false;
                Repaint();
            }
        }

        private void Log(string line)
        {
            _log.Add(line);
            if (line.StartsWith("[ERR]", StringComparison.Ordinal))
                Debug.LogError("[InterfaceImport] " + line);
        }
    }
}
#endif
