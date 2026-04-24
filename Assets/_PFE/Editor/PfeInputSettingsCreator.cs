using UnityEditor;
using UnityEngine;
using PFE.Core.Input;

namespace PFE.Editor
{
    public static class PfeInputSettingsCreator
    {
        private const string AssetPath = "Assets/_PFE/Data/Resources/PfeInputSettings.asset";

        [MenuItem("PFE/Create/Input Settings Asset")]
        public static void CreateAsset()
        {
            var existing = AssetDatabase.LoadAssetAtPath<PfeInputSettings>(AssetPath);
            if (existing != null)
            {
                EditorUtility.DisplayDialog("Already Exists",
                    $"PfeInputSettings asset already exists at:\n{AssetPath}", "OK");
                EditorGUIUtility.PingObject(existing);
                Selection.activeObject = existing;
                return;
            }

            var asset = ScriptableObject.CreateInstance<PfeInputSettings>();
            AssetDatabase.CreateAsset(asset, AssetPath);
            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog("Created",
                $"PfeInputSettings created at:\n{AssetPath}\n\n" +
                "Assign it to GameLifetimeScope → Input Settings field.", "OK");

            EditorGUIUtility.PingObject(asset);
            Selection.activeObject = asset;
        }
    }
}
