using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using PFE.Core.Input;

namespace PFE.Editor
{
    [CustomEditor(typeof(PfeInputSettings))]
    public class PfeInputSettingsEditor : UnityEditor.Editor
    {
        private static readonly Color _sectionColor = new Color(0.18f, 0.18f, 0.18f, 1f);

        private static GUIStyle _headerStyle;
        private static GUIStyle _bindingLabelStyle;

        private static void EnsureStyles()
        {
            if (_headerStyle != null) return;

            _headerStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(4, 0, 4, 2),
            };
            _headerStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);

            _bindingLabelStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 10,
                padding = new RectOffset(4, 0, 0, 0),
            };
            _bindingLabelStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
        }

        public override void OnInspectorGUI()
        {
            EnsureStyles();
            serializedObject.Update();

            var settings = (PfeInputSettings)target;

            EditorGUILayout.HelpBox(
                "Changes take effect next time you enter Play Mode.\n" +
                "Paths use Unity Input System format: <Keyboard>/space, <Mouse>/leftButton, <Gamepad>/buttonSouth, etc.\n" +
                "Leave Alt Keyboard empty to skip that binding.",
                MessageType.Info);

            EditorGUILayout.Space(4);

            // ---- Movement ----
            DrawSectionHeader("Movement");
            var moveProp = serializedObject.FindProperty("move");
            EditorGUI.indentLevel++;
            DrawMoveGroup("WASD",
                moveProp.FindPropertyRelative("kbUp"),
                moveProp.FindPropertyRelative("kbDown"),
                moveProp.FindPropertyRelative("kbLeft"),
                moveProp.FindPropertyRelative("kbRight"));
            DrawMoveGroup("Arrow Keys (Alt)",
                moveProp.FindPropertyRelative("altUp"),
                moveProp.FindPropertyRelative("altDown"),
                moveProp.FindPropertyRelative("altLeft"),
                moveProp.FindPropertyRelative("altRight"));
            EditorGUI.indentLevel--;

            if (GUILayout.Button("Reset Movement to Defaults", GUILayout.Height(22)))
            {
                Undo.RecordObject(settings, "Reset Movement Bindings");
                settings.move = PfeInputSettings.DefaultMove;
                EditorUtility.SetDirty(settings);
            }

            EditorGUILayout.Space(8);

            // ---- Action Buttons ----
            DrawSectionHeader("Actions");
            EditorGUI.indentLevel++;
            DrawButtonBinding("Jump",      serializedObject.FindProperty("jump"),      settings, PfeInputSettings.DefaultJump);
            DrawButtonBinding("Attack",    serializedObject.FindProperty("attack"),    settings, PfeInputSettings.DefaultAttack);
            DrawButtonBinding("Interact",  serializedObject.FindProperty("interact"),  settings, PfeInputSettings.DefaultInteract);
            DrawButtonBinding("Dash",      serializedObject.FindProperty("dash"),      settings, PfeInputSettings.DefaultDash);
            DrawButtonBinding("Teleport",  serializedObject.FindProperty("teleport"),  settings, PfeInputSettings.DefaultTeleport);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Reset ALL to Defaults", GUILayout.Height(26)))
            {
                Undo.RecordObject(settings, "Reset All Input Bindings");
                settings.move     = PfeInputSettings.DefaultMove;
                settings.jump     = PfeInputSettings.DefaultJump;
                settings.attack   = PfeInputSettings.DefaultAttack;
                settings.interact = PfeInputSettings.DefaultInteract;
                settings.dash     = PfeInputSettings.DefaultDash;
                settings.teleport = PfeInputSettings.DefaultTeleport;
                EditorUtility.SetDirty(settings);
            }

            serializedObject.ApplyModifiedProperties();
        }

        // -----------------------------------------------------------------------

        private void DrawSectionHeader(string title)
        {
            EditorGUILayout.Space(4);
            var rect = EditorGUILayout.GetControlRect(false, 24);
            EditorGUI.DrawRect(rect, _sectionColor);
            EditorGUI.LabelField(rect, title, _headerStyle);
            EditorGUILayout.Space(2);
        }

        private void DrawMoveGroup(string label,
            SerializedProperty up, SerializedProperty down,
            SerializedProperty left, SerializedProperty right)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            DrawPathField("Up",    up);
            DrawPathField("Down",  down);
            DrawPathField("Left",  left);
            DrawPathField("Right", right);
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
        }

        private void DrawButtonBinding(string actionName, SerializedProperty prop,
            PfeInputSettings settings, ButtonBinding defaults)
        {
            var kbProp  = prop.FindPropertyRelative("keyboard");
            var altProp = prop.FindPropertyRelative("altKeyboard");
            var gpProp  = prop.FindPropertyRelative("gamepad");

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(actionName, EditorStyles.boldLabel, GUILayout.Width(80));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Reset", GUILayout.Width(52), GUILayout.Height(18)))
                {
                    Undo.RecordObject(settings, $"Reset {actionName} Binding");
                    kbProp.stringValue  = defaults.keyboard;
                    altProp.stringValue = defaults.altKeyboard;
                    gpProp.stringValue  = defaults.gamepad;
                }
                EditorGUILayout.EndHorizontal();

                DrawPathField("Keyboard",     kbProp);
                DrawPathField("Alt Keyboard", altProp);
                DrawPathField("Gamepad",      gpProp);
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        private void DrawPathField(string label, SerializedProperty prop)
        {
            string path = prop.stringValue;

            EditorGUILayout.BeginHorizontal();
            {
                // Human-readable name derived from path
                string human = string.IsNullOrEmpty(path)
                    ? "— unbound —"
                    : InputControlPath.ToHumanReadableString(
                        path,
                        InputControlPath.HumanReadableStringOptions.OmitDevice);

                EditorGUILayout.LabelField(label, GUILayout.Width(90));
                EditorGUILayout.LabelField(human, _bindingLabelStyle ?? EditorStyles.miniLabel, GUILayout.Width(90));
                prop.stringValue = EditorGUILayout.TextField(path, GUILayout.ExpandWidth(true));
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
