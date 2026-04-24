#if UNITY_EDITOR
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

        private List<RoomTemplate> _templates;
        private string[] _templateOptions;
        private bool _templatesLoaded;
        private int _selectedDecorationIndex;

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
            if (_templates == null || _templates.Count == 0)
            {
                EditorGUILayout.HelpBox("No RoomTemplate assets found under Assets/_PFE/Data/Resources/Rooms.", MessageType.Warning);
                return;
            }

            RoomTemplate currentTemplate = controller.PreviewTemplate;
            int currentIndex = Mathf.Max(0, _templates.IndexOf(currentTemplate));
            int nextIndex = EditorGUILayout.Popup("Template", currentIndex, _templateOptions);

            if (nextIndex >= 0 && nextIndex < _templates.Count && _templates[nextIndex] != currentTemplate)
            {
                Undo.RecordObject(controller, "Change Preview Template");
                controller.PreviewTemplate = _templates[nextIndex];
                EditorUtility.SetDirty(controller);
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
            if (_templatesLoaded)
            {
                return;
            }

            _templatesLoaded = true;
            _templates = new List<RoomTemplate>();

            string[] guids = AssetDatabase.FindAssets("t:RoomTemplate", new[] { RoomTemplateRoot });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                RoomTemplate template = AssetDatabase.LoadAssetAtPath<RoomTemplate>(path);
                if (template != null)
                {
                    _templates.Add(template);
                }
            }

            _templates.Sort((a, b) =>
            {
                string left = $"{a.type}/{a.id}";
                string right = $"{b.type}/{b.id}";
                return string.CompareOrdinal(left, right);
            });

            _templateOptions = new string[_templates.Count];
            for (int i = 0; i < _templates.Count; i++)
            {
                RoomTemplate template = _templates[i];
                _templateOptions[i] = $"{template.type} / {template.id}";
            }
        }
    }
}
#endif
