#if UNITY_EDITOR
using PFE.Data.Definitions;
using PFE.Entities.Player;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor
{
    [CustomEditor(typeof(PlayerCharacterVisual))]
    public class PlayerCharacterVisualEditor : UnityEditor.Editor
    {
        // Persists across domain reloads via SessionState (editor-only, not saved to asset)
        static readonly string SessionArmorKey = "PFE.PlayerCharacterVisualEditor.ArmorIdx";

        string[]   _armorLabels;
        string[]   _armorIds;
        int        _selectedArmorIdx;
        bool       _armorListBuilt;

        SerializedProperty _definition;
        SerializedProperty _styleData;

        void OnEnable()
        {
            _definition = serializedObject.FindProperty("_definition");
            _styleData  = serializedObject.FindProperty("_styleData");
            _selectedArmorIdx = SessionState.GetInt(SessionArmorKey, 0);
            _armorListBuilt   = false;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Draw default fields (_definition, _styleData)
            EditorGUILayout.PropertyField(_definition);
            EditorGUILayout.PropertyField(_styleData);

            serializedObject.ApplyModifiedProperties();

            // ── Armor test dropdown ────────────────────────────────────────

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Armor Preview (Test)", EditorStyles.boldLabel);

            var defAsset = _definition.objectReferenceValue as CharacterAnimationDefinition;

            if (defAsset == null)
            {
                EditorGUILayout.HelpBox("Assign a CharacterAnimationDefinition to enable armor selection.", MessageType.Info);
                return;
            }

            if (!_armorListBuilt || _armorLabels == null)
                BuildArmorList(defAsset);

            if (_armorIds == null || _armorIds.Length == 0)
            {
                EditorGUILayout.HelpBox("No armor sets defined in the assigned definition.", MessageType.Info);
                return;
            }

            // Clamp in case definition changed since last build
            _selectedArmorIdx = Mathf.Clamp(_selectedArmorIdx, 0, _armorIds.Length - 1);

            EditorGUI.BeginChangeCheck();
            int newIdx = EditorGUILayout.Popup("Armor Set", _selectedArmorIdx, _armorLabels);
            if (EditorGUI.EndChangeCheck())
            {
                _selectedArmorIdx = newIdx;
                SessionState.SetInt(SessionArmorKey, _selectedArmorIdx);
                ApplyArmor();
            }

            GUI.enabled = Application.isPlaying;
            if (GUILayout.Button("Apply (Play Mode)"))
                ApplyArmor();
            GUI.enabled = true;

            if (!Application.isPlaying)
                EditorGUILayout.HelpBox("Armor preview applies live in Play Mode. In Edit Mode the assembler refreshes on next play.", MessageType.None);
        }

        void BuildArmorList(CharacterAnimationDefinition def)
        {
            _armorListBuilt = true;

            if (def.armorSets == null || def.armorSets.Length == 0)
            {
                _armorLabels = System.Array.Empty<string>();
                _armorIds    = System.Array.Empty<string>();
                return;
            }

            // First entry: "None (no armor)"
            _armorIds    = new string[def.armorSets.Length + 1];
            _armorLabels = new string[def.armorSets.Length + 1];

            _armorIds[0]    = string.Empty;
            _armorLabels[0] = "— None —";

            for (int i = 0; i < def.armorSets.Length; i++)
            {
                string id = def.armorSets[i]?.armorId ?? $"armor_{i}";
                _armorIds[i + 1]    = id;
                _armorLabels[i + 1] = id;
            }
        }

        void ApplyArmor()
        {
            if (!Application.isPlaying) return;

            var visual = (PlayerCharacterVisual)target;
            string armorId = (_armorIds != null && _selectedArmorIdx < _armorIds.Length)
                ? _armorIds[_selectedArmorIdx]
                : string.Empty;

            visual.SetArmor(armorId);
        }
    }
}
#endif
