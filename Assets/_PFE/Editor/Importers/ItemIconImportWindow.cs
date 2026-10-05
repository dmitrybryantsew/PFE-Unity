#if UNITY_EDITOR
using System.Collections.Generic;
using PFE.Editor.Importers.SWF;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Editor window: PFE &gt; Art &gt; Import Item Icons.
    ///
    /// <para>Copies the loot-sprite frame each item resolves to out of the pfe export and assigns it to
    /// <c>ItemDefinition.icon</c>. The decision — which clip, which frame — lives in
    /// <see cref="PFE.Data.Definitions.ItemLootSpriteRule"/> and is pinned by tests that run without
    /// Unity; this window is the button and the report.</para>
    ///
    /// <para>Pipeline: <c>AllData.as</c> (each item's <c>tip</c> and <c>base</c>) →
    /// <c>assets.swf</c> (the frame labels on <c>visualItem</c> / <c>visualAmmo</c>) →
    /// <c>symbols.csv</c> (name to id) → copy <c>{symbol}/{frame}.png</c> →
    /// <c>Assets/_PFE/Art/Items/Sprites/</c> → assign <c>ItemDefinition.icon</c>.</para>
    /// </summary>
    public class ItemIconImportWindow : EditorWindow
    {
        Vector2 _scroll;
        List<string> _report;

        [MenuItem("PFE/Art/Import Item Icons")]
        public static void ShowWindow()
        {
            var window = GetWindow<ItemIconImportWindow>("Item Icon Import");
            window.minSize = new Vector2(560f, 420f);
        }

        void OnEnable()
        {
            // Built here rather than in a field initializer. Field initializers run inside the
            // ScriptableObject constructor, and anything that reaches EditorPrefs (which
            // SourceImportPaths does) throws there — which would abort the constructor and leave
            // every later-declared field null. Same trap WeaponGraphicsImportWindow documents.
            _report ??= new List<string>();
        }

        void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Item Icon Importer", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Gives every ItemDefinition the sprite the original game draws for it on the ground.\n\n" +
                "Most items come from the shared 'visualItem' clip, whose frames are labelled with item " +
                "ids; ammunition comes from 'visualAmmo', and explosives and held weapons from a " +
                "per-item 'vis<id>' symbol. Frames land in Assets/_PFE/Art/Items/Sprites/{symbol}/.\n\n" +
                "Re-running is safe: nothing is rewritten unless the source is newer.",
                MessageType.Info);

            EditorGUILayout.Space(6);

            string root = SourceImportPaths.SourceProjectRoot;
            EditorGUILayout.LabelField("Source root", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(
                string.IsNullOrEmpty(root) ? "<not resolved — see PFE/Data/Import Source Root…>" : root,
                EditorStyles.textField, GUILayout.Height(18f));

            EditorGUILayout.Space(6);

            if (GUILayout.Button("Import Item Icons", GUILayout.Height(30f)))
                Run();

            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField("Report", EditorStyles.boldLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            foreach (string line in _report)
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }

        void Run()
        {
            _report.Clear();
            _report.Add("Running…");

            ItemIconImporter.ImportResult result = ItemIconImporter.Run();

            _report.Clear();
            foreach (string line in result.Summary().Split('\n'))
                if (line.Trim().Length > 0)
                    _report.Add(line.TrimEnd('\r'));

            if (result.Warnings.Count > 0)
            {
                _report.Add("");
                _report.Add($"Warnings ({result.Warnings.Count}):");
                foreach (string warning in result.Warnings)
                    _report.Add("  • " + warning);
            }

            if (result.Log.Count > 0)
            {
                _report.Add("");
                _report.Add("Log:");
                foreach (string line in result.Log)
                    _report.Add("  " + line);
            }

            _report.Add("");
            _report.Add(result.Succeeded
                ? "Done. Re-run is a no-op unless the export changes."
                : "Finished with warnings — see above. Items listed as unresolved keep a null icon and " +
                  "draw the fallback square.");

            Repaint();
        }
    }
}
#endif
