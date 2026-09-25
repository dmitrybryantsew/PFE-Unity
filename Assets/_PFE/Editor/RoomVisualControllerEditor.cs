#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;

namespace PFE.Editor
{
    [CustomEditor(typeof(RoomVisualController))]
    public class RoomVisualControllerEditor : UnityEditor.Editor
    {
        private const string RoomTemplateRoot = "Assets/_PFE/Data/Resources/Rooms";
        private const float MinBackdropScale = 0.1f;
        private const float MaxBackdropScale = 4f;
        private const float MinBackdropOffset = -2f;
        private const float MaxBackdropOffset = 2f;
        private const float MinBrightness = 0f;
        private const float MaxBrightness = 2.5f;
        private const string AllCollectionsLabel = "All";
        private const string BackgroundRoomType = "back";

        private int _selectedDecorationIndex;

        // Collection (folder) filter for the preview template picker.
        // Template + collection are kept together in one list on purpose: parallel
        // lists previously drifted out of sync when the load step did not finish,
        // which threw IndexOutOfRange from the inspector.
        private List<TemplateEntry> _entries;
        private List<TemplateEntry> _filteredEntries;
        private List<string> _collectionNames;
        private string[] _collectionOptions;
        private string[] _filteredOptions;
        private int _selectedCollectionIndex;
        private bool _filteredListDirty = true;
        private bool _collectionInitialized;
        private bool _entriesLoaded;

        private struct TemplateEntry
        {
            public RoomTemplate Template;
            public string Collection;

            public TemplateEntry(RoomTemplate template, string collection)
            {
                Template = template;
                Collection = collection;
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawPropertiesExcluding(
                serializedObject,
                "m_Script",
                "backdropTextureScale",
                "backdropTextureOffset",
                "flipBackdropTextureX",
                "flipBackdropTextureY",
                "backdropTint",
                "backdropBrightness",
                "backgroundAssetTint",
                "backgroundAssetBrightness",
                "previewTemplate",
                "showContourDebugOverlay",
                "useLongDebugExport");

            DrawBackdropControls();
            DrawDebugControls();

            serializedObject.ApplyModifiedProperties();

            RoomVisualController controller = (RoomVisualController)target;

            EditorGUILayout.Space();
            DrawBackdropStorageControls(controller);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Room Preview", EditorStyles.boldLabel);

            EnsureTemplatesLoaded();

            DrawTemplateSelector(controller);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Load Preview Room"))
                {
                    controller.LoadPreviewRoom();
                }

                if (GUILayout.Button("Clear Preview"))
                {
                    controller.ClearPreviewRoom();
                }
            }

            EditorGUILayout.Space();
            DrawDebugExportControls(controller);
        }

        private void DrawTemplateSelector(RoomVisualController controller)
        {
            if (_entries == null || _entries.Count == 0)
            {
                EditorGUILayout.HelpBox("No RoomTemplate assets found under Assets/_PFE/Data/Resources/Rooms.", MessageType.Warning);
                return;
            }

            RoomTemplate currentTemplate = controller.PreviewTemplate;

            SyncCollectionToCurrentTemplate(currentTemplate);
            EnsureFilteredListCurrent();
            DrawCollectionFilter(controller);

            currentTemplate = controller.PreviewTemplate;

            int rawIndex = FindFilteredIndex(currentTemplate);
            int currentIndex = rawIndex >= 0 ? rawIndex : 0;

            if (_filteredOptions == null || _filteredOptions.Length == 0)
            {
                EditorGUILayout.HelpBox("No rooms match the selected collection.", MessageType.Info);
                return;
            }

            int nextIndex = EditorGUILayout.Popup("Room", currentIndex, _filteredOptions);

            if (nextIndex >= 0 && nextIndex < _filteredEntries.Count)
            {
                RoomTemplate picked = _filteredEntries[nextIndex].Template;
                if (picked != null && picked != currentTemplate)
                {
                    Undo.RecordObject(controller, "Change Preview Template");
                    controller.PreviewTemplate = picked;
                    EditorUtility.SetDirty(controller);
                    currentTemplate = picked;
                }
            }

            if (currentTemplate != null && string.Equals(currentTemplate.type, BackgroundRoomType, StringComparison.Ordinal))
            {
                EditorGUILayout.HelpBox(
                    $"'{currentTemplate.id}' is a BACKGROUND layer room (type=back). Loaded as foreground it " +
                    "renders almost nothing - it is meant to sit behind a foreground room. " +
                    "Pick a foreground room (pass/beg/vert/...) if the preview looks empty.",
                    MessageType.Warning);
            }
        }

        /// <summary>
        /// Index of a template in the current filtered list, or -1 when it is not present.
        /// Uses reference equality because UnityEngine.Object overloads == / Equals.
        /// </summary>
        private int FindFilteredIndex(RoomTemplate template)
        {
            if (template == null || _filteredEntries == null)
            {
                return -1;
            }

            for (int i = 0; i < _filteredEntries.Count; i++)
            {
                if (ReferenceEquals(_filteredEntries[i].Template, template))
                {
                    return i;
                }
            }

            return -1;
        }

        private void DrawCollectionFilter(RoomVisualController controller)
        {
            if (_collectionOptions == null || _collectionOptions.Length == 0)
            {
                return;
            }

            if (_selectedCollectionIndex < 0 || _selectedCollectionIndex >= _collectionOptions.Length)
            {
                _selectedCollectionIndex = 0;
                _filteredListDirty = true;
                EnsureFilteredListCurrent();
            }

            int nextCollection = EditorGUILayout.Popup("Collection", _selectedCollectionIndex, _collectionOptions);
            if (nextCollection == _selectedCollectionIndex)
            {
                return;
            }

            _selectedCollectionIndex = nextCollection;
            _filteredListDirty = true;
            EnsureFilteredListCurrent();

            // Drill-down: switching collection must also move the selection into that
            // collection, otherwise the Room popup would highlight one room while
            // "Load Preview Room" still uses the previous collection's template.
            if (_filteredEntries != null && _filteredEntries.Count > 0 &&
                FindFilteredIndex(controller.PreviewTemplate) < 0)
            {
                RoomTemplate first = _filteredEntries[0].Template;
                if (first != null)
                {
                    Undo.RecordObject(controller, "Change Preview Template");
                    controller.PreviewTemplate = first;
                    EditorUtility.SetDirty(controller);
                }
            }
        }

        private void EnsureFilteredListCurrent()
        {
            if (!_filteredListDirty && _filteredEntries != null && _filteredOptions != null)
            {
                return;
            }

            _filteredListDirty = false;

            if (_entries == null)
            {
                _filteredEntries = new List<TemplateEntry>();
                _filteredOptions = new string[0];
                return;
            }

            // _collectionNames[0] is "All" (no filter); real collections start at index 1.
            string selectedCollection = null;
            if (_collectionNames != null && _selectedCollectionIndex > 0 && _selectedCollectionIndex < _collectionNames.Count)
            {
                selectedCollection = _collectionNames[_selectedCollectionIndex];
            }

            _filteredEntries = new List<TemplateEntry>();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (selectedCollection == null ||
                    string.Equals(_entries[i].Collection, selectedCollection, StringComparison.Ordinal))
                {
                    _filteredEntries.Add(_entries[i]);
                }
            }

            _filteredOptions = new string[_filteredEntries.Count];
            for (int i = 0; i < _filteredEntries.Count; i++)
            {
                RoomTemplate template = _filteredEntries[i].Template;
                string typeLabel = template != null && !string.IsNullOrWhiteSpace(template.type) ? template.type : "?";
                string id = template != null ? template.id : "(missing)";
                _filteredOptions[i] = $"{_filteredEntries[i].Collection} / {id}  [{typeLabel}]";
            }
        }

        private void DrawBackdropControls()
        {
            SerializedProperty scaleProperty = serializedObject.FindProperty("backdropTextureScale");
            SerializedProperty offsetProperty = serializedObject.FindProperty("backdropTextureOffset");
            SerializedProperty flipXProperty = serializedObject.FindProperty("flipBackdropTextureX");
            SerializedProperty flipYProperty = serializedObject.FindProperty("flipBackdropTextureY");
            SerializedProperty backdropTintProperty = serializedObject.FindProperty("backdropTint");
            SerializedProperty backdropBrightnessProperty = serializedObject.FindProperty("backdropBrightness");
            SerializedProperty backgroundAssetTintProperty = serializedObject.FindProperty("backgroundAssetTint");
            SerializedProperty backgroundAssetBrightnessProperty = serializedObject.FindProperty("backgroundAssetBrightness");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Backdrop Tiling", EditorStyles.boldLabel);

            DrawVector2Slider(scaleProperty, "Texture Scale", MinBackdropScale, MaxBackdropScale);
            DrawVector2Slider(offsetProperty, "Texture Offset", MinBackdropOffset, MaxBackdropOffset);
            DrawToggle(flipXProperty, "Flip X");
            DrawToggle(flipYProperty, "Flip Y");
            DrawTintControls(backdropTintProperty, backdropBrightnessProperty, "Backdrop Tint");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Background Asset Tint", EditorStyles.boldLabel);
            DrawTintControls(backgroundAssetTintProperty, backgroundAssetBrightnessProperty, "Asset Tint");
        }

        private void DrawDebugControls()
        {
            SerializedProperty overlayProperty = serializedObject.FindProperty("showContourDebugOverlay");
            SerializedProperty longExportProperty = serializedObject.FindProperty("useLongDebugExport");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Tile Debug", EditorStyles.boldLabel);

            if (overlayProperty != null)
            {
                EditorGUILayout.PropertyField(overlayProperty, new GUIContent("Show Overlay"));
            }

            if (longExportProperty != null)
            {
                EditorGUILayout.PropertyField(longExportProperty, new GUIContent("Long Export"));
            }
        }

        private void DrawBackdropStorageControls(RoomVisualController controller)
        {
            string storageKey = controller != null ? controller.GetCurrentRoomStorageKey() : string.Empty;

            EditorGUILayout.LabelField("Backdrop Storage", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Current Room Key", string.IsNullOrWhiteSpace(storageKey) ? "(no loaded room)" : storageKey);

            using (new EditorGUI.DisabledScope(controller == null || string.IsNullOrWhiteSpace(storageKey)))
            {
                if (GUILayout.Button("Save Room Backdrop Settings"))
                {
                    SaveBackdropSettings(controller, storageKey);
                }
            }

            using (new EditorGUI.DisabledScope(controller == null))
            {
                if (GUILayout.Button("Save Room Backdrop Tint"))
                {
                    SaveGlobalBackdropTint(controller);
                }

                if (GUILayout.Button("Save Room Asset Tint"))
                {
                    SaveGlobalDecorationTint(controller);
                }
            }

            DrawDecorationOverrideControls(controller);
        }

        private static void DrawVector2Slider(SerializedProperty property, string label, float minValue, float maxValue)
        {
            if (property == null || property.propertyType != SerializedPropertyType.Vector2)
            {
                return;
            }

            Vector2 value = property.vector2Value;
            EditorGUILayout.LabelField(label);
            EditorGUI.indentLevel++;
            value.x = EditorGUILayout.Slider("X", value.x, minValue, maxValue);
            value.y = EditorGUILayout.Slider("Y", value.y, minValue, maxValue);
            EditorGUI.indentLevel--;
            property.vector2Value = value;
        }

        private static void DrawToggle(SerializedProperty property, string label)
        {
            if (property == null || property.propertyType != SerializedPropertyType.Boolean)
            {
                return;
            }

            property.boolValue = EditorGUILayout.Toggle(label, property.boolValue);
        }

        private static void DrawTintControls(SerializedProperty colorProperty, SerializedProperty brightnessProperty, string label)
        {
            if (colorProperty == null || brightnessProperty == null)
            {
                return;
            }

            colorProperty.colorValue = EditorGUILayout.ColorField(label, colorProperty.colorValue);
            brightnessProperty.floatValue = EditorGUILayout.Slider("Brightness", brightnessProperty.floatValue, MinBrightness, MaxBrightness);
        }

        private static void SaveBackdropSettings(RoomVisualController controller, string roomKey)
        {
            RoomBackdropSettingsLookup lookup = LoadOrCreateBackdropLookup();
            if (lookup == null || controller == null || string.IsNullOrWhiteSpace(roomKey))
            {
                return;
            }

            Undo.RecordObject(lookup, "Save Room Backdrop Settings");
            lookup.SetBackdrop(roomKey, controller.GetCurrentBackdropSettings());
            controller.SetBackdropSettingsLookup(lookup);

            EditorUtility.SetDirty(lookup);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
        }

        private static void SaveGlobalBackdropTint(RoomVisualController controller)
        {
            RoomBackdropSettingsLookup lookup = LoadOrCreateBackdropLookup();
            if (lookup == null || controller == null)
            {
                return;
            }

            string roomKey = controller.GetCurrentRoomStorageKey();
            if (string.IsNullOrWhiteSpace(roomKey))
            {
                return;
            }

            Undo.RecordObject(lookup, "Save Room Backdrop Tint");
            lookup.SetRoomBackdropTint(roomKey, controller.GetCurrentBackdropTintSettings());
            controller.SetBackdropSettingsLookup(lookup);

            EditorUtility.SetDirty(lookup);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
        }

        private static void SaveGlobalDecorationTint(RoomVisualController controller)
        {
            RoomBackdropSettingsLookup lookup = LoadOrCreateBackdropLookup();
            if (lookup == null || controller == null)
            {
                return;
            }

            string roomKey = controller.GetCurrentRoomStorageKey();
            if (string.IsNullOrWhiteSpace(roomKey))
            {
                return;
            }

            Undo.RecordObject(lookup, "Save Room Decoration Tint");
            lookup.SetRoomDecorationTint(roomKey, controller.GetCurrentBackgroundAssetTintSettings());
            controller.SetBackdropSettingsLookup(lookup);

            EditorUtility.SetDirty(lookup);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
        }

        private void DrawDecorationOverrideControls(RoomVisualController controller)
        {
            string[] decorationIds = GetDecorationIds(controller);
            if (decorationIds.Length == 0)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Decoration Override", EditorStyles.boldLabel);
            _selectedDecorationIndex = Mathf.Clamp(_selectedDecorationIndex, 0, decorationIds.Length - 1);
            _selectedDecorationIndex = EditorGUILayout.Popup("Decoration Id", _selectedDecorationIndex, decorationIds);

            using (new EditorGUI.DisabledScope(controller == null))
            {
                if (GUILayout.Button("Save Selected Room Asset Tint Override"))
                {
                    SaveDecorationOverride(controller, decorationIds[_selectedDecorationIndex]);
                }
            }
        }

        private static string[] GetDecorationIds(RoomVisualController controller)
        {
            if (controller?.RoomInstance?.backgroundDecorations == null || controller.RoomInstance.backgroundDecorations.Count == 0)
            {
                return System.Array.Empty<string>();
            }

            HashSet<string> ids = new HashSet<string>();
            for (int i = 0; i < controller.RoomInstance.backgroundDecorations.Count; i++)
            {
                string id = controller.RoomInstance.backgroundDecorations[i]?.decorationId;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }

            string[] result = new string[ids.Count];
            ids.CopyTo(result);
            System.Array.Sort(result, System.StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static void SaveDecorationOverride(RoomVisualController controller, string decorationId)
        {
            RoomBackdropSettingsLookup lookup = LoadOrCreateBackdropLookup();
            if (lookup == null || controller == null || string.IsNullOrWhiteSpace(decorationId))
            {
                return;
            }

            string roomKey = controller.GetCurrentRoomStorageKey();
            if (string.IsNullOrWhiteSpace(roomKey))
            {
                return;
            }

            Undo.RecordObject(lookup, "Save Room Decoration Tint Override");
            lookup.SetDecoration(roomKey, decorationId, new RoomBackdropSettingsLookup.DecorationSettings
            {
                overrideGlobalTint = true,
                tint = controller.GetCurrentBackgroundAssetTintSettings()
            });
            controller.SetBackdropSettingsLookup(lookup);

            EditorUtility.SetDirty(lookup);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
        }

        private static void DrawDebugExportControls(RoomVisualController controller)
        {
            EditorGUILayout.LabelField("Debug Export", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(controller == null || controller.RoomInstance == null))
            {
                if (GUILayout.Button("Copy Room Debug String"))
                {
                    string debugText = controller.BuildRoomDebugString(controller.UseLongDebugExport);
                    if (string.IsNullOrWhiteSpace(debugText))
                    {
                        Debug.LogWarning("[RoomVisualControllerEditor] No room debug data available to copy.");
                        return;
                    }

                    EditorGUIUtility.systemCopyBuffer = debugText;
                    Debug.Log($"[RoomVisualControllerEditor] Copied {(controller.UseLongDebugExport ? "long" : "short")} room debug string to clipboard ({debugText.Length} chars).");
                }
            }
        }

        private static RoomBackdropSettingsLookup LoadOrCreateBackdropLookup()
        {
            RoomBackdropSettingsLookup lookup = AssetDatabase.LoadAssetAtPath<RoomBackdropSettingsLookup>(RoomBackdropSettingsLookup.AssetPath);
            if (lookup != null)
            {
                return lookup;
            }

            EnsureFolderExists("Assets/Resources");
            EnsureFolderExists("Assets/Resources/Data");

            lookup = ScriptableObject.CreateInstance<RoomBackdropSettingsLookup>();
            AssetDatabase.CreateAsset(lookup, RoomBackdropSettingsLookup.AssetPath);
            AssetDatabase.SaveAssets();
            return lookup;
        }

        private static void EnsureFolderExists(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            int slashIndex = folderPath.LastIndexOf('/');
            if (slashIndex <= 0)
            {
                return;
            }

            string parentPath = folderPath.Substring(0, slashIndex);
            string folderName = folderPath.Substring(slashIndex + 1);
            EnsureFolderExists(parentPath);
            AssetDatabase.CreateFolder(parentPath, folderName);
        }

        private void EnsureTemplatesLoaded()
        {
            // The flag is only set once the whole build succeeds. Marking it loaded up-front
            // meant a failure half-way through left the picker permanently broken (options
            // null while the template list was already populated) and it never retried.
            if (_entriesLoaded && _entries != null && _collectionOptions != null)
            {
                return;
            }

            List<TemplateEntry> loaded = new List<TemplateEntry>();

            string[] guids = AssetDatabase.FindAssets("t:RoomTemplate", new[] { RoomTemplateRoot });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                RoomTemplate template = AssetDatabase.LoadAssetAtPath<RoomTemplate>(path);
                if (template == null)
                {
                    continue;
                }

                loaded.Add(new TemplateEntry(template, ResolveCollection(template, path)));
            }

            loaded.Sort((a, b) =>
            {
                string left = $"{a.Template.type}/{a.Template.id}";
                string right = $"{b.Template.type}/{b.Template.id}";
                return string.CompareOrdinal(left, right);
            });

            _entries = loaded;
            BuildCollectionOptions();
            _filteredListDirty = true;
            _entriesLoaded = true;
        }

        /// <summary>
        /// Room "collection" = the folder the template lives in (Base, Camp, Serial, ...).
        /// Prefers the authored sourceCollectionId and falls back to the parent folder name,
        /// so templates imported without that field still group correctly.
        /// </summary>
        private static string ResolveCollection(RoomTemplate template, string assetPath)
        {
            if (!string.IsNullOrWhiteSpace(template.sourceCollectionId))
            {
                return template.sourceCollectionId;
            }

            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return "Unsorted";
            }

            int lastSlash = assetPath.LastIndexOf('/');
            if (lastSlash <= 0)
            {
                return "Unsorted";
            }

            string folder = assetPath.Substring(0, lastSlash);
            int parentSlash = folder.LastIndexOf('/');
            string folderName = parentSlash >= 0 ? folder.Substring(parentSlash + 1) : folder;
            return string.IsNullOrWhiteSpace(folderName) ? "Unsorted" : folderName;
        }

        private void BuildCollectionOptions()
        {
            SortedDictionary<string, int> counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _entries.Count; i++)
            {
                string collection = _entries[i].Collection;
                counts.TryGetValue(collection, out int existing);
                counts[collection] = existing + 1;
            }

            List<string> names = new List<string>(counts.Keys);
            _collectionOptions = new string[names.Count + 1];
            _collectionOptions[0] = $"{AllCollectionsLabel} ({_entries.Count})";
            for (int i = 0; i < names.Count; i++)
            {
                _collectionOptions[i + 1] = $"{names[i]} ({counts[names[i]]})";
            }

            // _collectionNames mirrors the popup order: index 0 is "All" (no filter),
            // so the collection shown at popup index N is _collectionNames[N].
            List<string> ordered = new List<string> { AllCollectionsLabel };
            ordered.AddRange(names);
            _collectionNames = ordered;
        }

        /// <summary>
        /// On first draw, move the collection filter to whatever template is already assigned,
        /// so the Room popup shows the current selection instead of silently resetting it.
        /// </summary>
        private void SyncCollectionToCurrentTemplate(RoomTemplate currentTemplate)
        {
            if (_collectionInitialized || currentTemplate == null || _entries == null || _collectionNames == null)
            {
                return;
            }

            _collectionInitialized = true;

            for (int i = 0; i < _entries.Count; i++)
            {
                if (!ReferenceEquals(_entries[i].Template, currentTemplate))
                {
                    continue;
                }

                int collectionIndex = _collectionNames.IndexOf(_entries[i].Collection);
                if (collectionIndex > 0)
                {
                    _selectedCollectionIndex = collectionIndex;
                    _filteredListDirty = true;
                }

                return;
            }
        }
    }
}
#endif
