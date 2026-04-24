#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using PFE.ModAPI;
using PFE.Data;

namespace PFE.Editor
{
    /// <summary>
    /// Editor window for validating installed mods and inspecting the content registry.
    /// Shows discovered mods, validation errors, content counts, and conflict log.
    ///
    /// Menu: PFE > Modding > Mod Validator
    /// </summary>
    public class ModValidatorWindow : EditorWindow
    {
        string _modsFolder = "";
        Vector2 _scrollPos;
        ModLoader _loader;
        ContentRegistry _registry;
        bool _hasScanned;

        // Foldout state
        bool _showMods = true;
        bool _showRegistry = true;
        bool _showLog = true;

        [MenuItem("PFE/Modding/Mod Validator")]
        public static void ShowWindow()
        {
            var window = GetWindow<ModValidatorWindow>("Mod Validator");
            window.minSize = new Vector2(500, 400);
        }

        void OnEnable()
        {
            if (string.IsNullOrEmpty(_modsFolder))
                _modsFolder = Path.Combine(Application.persistentDataPath, "Mods");
        }

        void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            DrawScanSection();
            EditorGUILayout.Space(10);

            if (_hasScanned)
            {
                DrawModsSection();
                EditorGUILayout.Space(10);
                DrawRegistrySection();
                EditorGUILayout.Space(10);
                DrawLogSection();
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawScanSection()
        {
            EditorGUILayout.LabelField("Mod Discovery", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            _modsFolder = EditorGUILayout.TextField("Mods Folder", _modsFolder);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string selected = EditorUtility.OpenFolderPanel("Select Mods Folder", _modsFolder, "");
                if (!string.IsNullOrEmpty(selected))
                    _modsFolder = selected;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Scan & Validate", GUILayout.Height(25)))
            {
                RunScan();
            }

            if (GUILayout.Button("Open Mods Folder", GUILayout.Height(25)))
            {
                if (Directory.Exists(_modsFolder))
                    EditorUtility.RevealInFinder(_modsFolder);
                else
                    EditorUtility.DisplayDialog("Not Found", $"Mods folder does not exist:\n{_modsFolder}", "OK");
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        void RunScan()
        {
            _loader = new ModLoader { ModsRoot = _modsFolder };
            _registry = new ContentRegistry { LogRegistrations = true, LogConflicts = true };

            var sources = _loader.BuildSourceList();
            _registry.Initialize(sources);

            _hasScanned = true;
            Repaint();
        }

        void DrawModsSection()
        {
            int modCount = _loader.DiscoveredMods.Count;
            _showMods = EditorGUILayout.BeginFoldoutHeaderGroup(_showMods,
                $"Discovered Mods ({modCount} + base game)");
            if (!_showMods) { EditorGUILayout.EndFoldoutHeaderGroup(); return; }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Base game
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("pfe.base", EditorStyles.boldLabel, GUILayout.Width(200));
            EditorGUILayout.LabelField("Base Game", GUILayout.Width(150));
            EditorGUILayout.LabelField("order: 0");
            EditorGUILayout.EndHorizontal();

            // Discovered mods
            foreach (var mod in _loader.DiscoveredMods)
            {
                EditorGUILayout.BeginHorizontal();

                var style = mod.isEnabled ? EditorStyles.boldLabel : EditorStyles.label;
                EditorGUILayout.LabelField(mod.modId, style, GUILayout.Width(200));
                EditorGUILayout.LabelField($"v{mod.version}", GUILayout.Width(80));
                EditorGUILayout.LabelField($"by {mod.author}", GUILayout.Width(100));
                EditorGUILayout.LabelField($"order: {mod.loadOrder}");

                if (mod.isCosmeticOnly)
                    GUILayout.Label("[cosmetic]", EditorStyles.miniLabel);

                EditorGUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(mod.description))
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField(mod.description, EditorStyles.wordWrappedMiniLabel);
                    EditorGUI.indentLevel--;
                }
            }

            // Validation warnings
            if (_loader.ValidationLog.Count > 0)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Validation Issues:", EditorStyles.boldLabel);
                foreach (var warning in _loader.ValidationLog)
                {
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        void DrawRegistrySection()
        {
            _showRegistry = EditorGUILayout.BeginFoldoutHeaderGroup(_showRegistry, "Content Registry");
            if (!_showRegistry) { EditorGUILayout.EndFoldoutHeaderGroup(); return; }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField(_registry.GetSummary());

            EditorGUILayout.Space(5);

            // Per-type breakdown
            foreach (ContentType ct in System.Enum.GetValues(typeof(ContentType)))
            {
                int count = _registry.GetCount(ct);
                if (count == 0) continue;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{ct}", EditorStyles.boldLabel, GUILayout.Width(180));
                EditorGUILayout.LabelField($"{count} entries");
                EditorGUILayout.EndHorizontal();

                // Show IDs
                EditorGUI.indentLevel++;
                foreach (var id in _registry.GetAllIds(ct))
                {
                    string sourceMod = _registry.GetSourceMod(ct, id);
                    EditorGUILayout.LabelField($"{id}  ({sourceMod})", EditorStyles.miniLabel);
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        void DrawLogSection()
        {
            var log = _registry.RegistrationLog;
            _showLog = EditorGUILayout.BeginFoldoutHeaderGroup(_showLog,
                $"Registration Log ({log.Count} entries)");
            if (!_showLog) { EditorGUILayout.EndFoldoutHeaderGroup(); return; }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (log.Count == 0)
            {
                EditorGUILayout.LabelField("No conflicts or overrides detected.", EditorStyles.miniLabel);
            }
            else
            {
                foreach (var entry in log)
                {
                    EditorGUILayout.LabelField(entry, EditorStyles.wordWrappedMiniLabel);
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndFoldoutHeaderGroup();
        }
    }
}
#endif
