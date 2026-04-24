using UnityEngine;
using UnityEditor;
using PFE.Data.Definitions;

namespace PFE.Editor.Tools
{
    /// <summary>
    /// Sample Asset Creator - Data Librarian Tool
    /// Creates sample ScriptableObject assets for testing purposes.
    ///
    /// This tool generates minimal test data to verify that:
    /// - XMLConverter can create assets properly
    /// - DataValidator can validate assets
    /// - GameDatabase can register assets
    ///
    /// Usage: Assets > PFE Tools > Create Sample Assets
    /// </summary>
    public class SampleAssetCreator : EditorWindow
    {
        [MenuItem("PFE Tools/Create Sample Assets")]
        public static void ShowWindow()
        {
            EditorWindow.GetWindow<SampleAssetCreator>("Sample Assets");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("PFE Sample Asset Creator", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Create sample assets for testing XMLConverter and DataValidator.",
                MessageType.Info
            );

            EditorGUILayout.Space();

            if (GUILayout.Button("Create Sample Unit", GUILayout.Height(30)))
            {
                CreateSampleUnit();
            }

            if (GUILayout.Button("Create Sample Weapon", GUILayout.Height(30)))
            {
                CreateSampleWeapon();
            }

            if (GUILayout.Button("Create All Samples", GUILayout.Height(30)))
            {
                CreateSampleUnit();
                CreateSampleWeapon();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        private static void CreateSampleUnit()
        {
            UnitDefinition unit = ScriptableObject.CreateInstance<UnitDefinition>();

            // Set sample data based on AllData.as structure
            unit.id = "sample_raider";
            unit.displayName = "Sample Raider Unit";
            unit.mass = 55f;
            unit.width = 0.55f;
            unit.height = 0.70f;
            unit.moveSpeed = 4f;
            unit.runMultiplier = 2f; // RunSpeed is calculated, use runMultiplier instead
            unit.jumpForce = 18f;
            unit.acceleration = 3f;
            unit.health = 50; // Changed from MaxHP to health
            unit.damage = 11;

            // Save asset
            string path = "Assets/_PFE/Data/Samples/sample_raider.asset";
            System.IO.Directory.CreateDirectory("Assets/_PFE/Data/Samples");
            AssetDatabase.CreateAsset(unit, path);

            Debug.Log($"[SampleAssetCreator] Created sample unit: {path}");
            EditorUtility.DisplayDialog("Success", $"Created sample unit:\n{path}", "OK");
        }

        private static void CreateSampleWeapon()
        {
            WeaponDefinition weapon = ScriptableObject.CreateInstance<WeaponDefinition>();

            // Set sample data based on AllData.as structure (e.g., 'punch' weapon)
            weapon.weaponId = "sample_punch";
            weapon.weaponType = WeaponType.Melee;
            weapon.skillLevel = 1;
            weapon.baseDamage = 10f;
            weapon.rapid = 13f; // Fire rate in frames
            weapon.precision = 0f;

            // Save asset
            string path = "Assets/_PFE/Data/Samples/sample_punch.asset";
            System.IO.Directory.CreateDirectory("Assets/_PFE/Data/Samples");
            AssetDatabase.CreateAsset(weapon, path);

            Debug.Log($"[SampleAssetCreator] Created sample weapon: {path}");
            EditorUtility.DisplayDialog("Success", $"Created sample weapon:\n{path}", "OK");
        }
    }
}
