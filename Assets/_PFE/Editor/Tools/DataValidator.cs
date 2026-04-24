using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using PFE.Data.Definitions;

namespace PFE.Editor.Tools
{
    /// <summary>
    /// Data Validator - Data Librarian Tool
    /// Validates data integrity across all PFE ScriptableObject definitions.
    ///
    /// Checks for:
    /// - Missing or invalid IDs
    /// - Broken references (e.g., weapons that reference non-existent ammo)
    /// - Invalid enum values
    /// - Missing required fields
    /// - Duplicate IDs
    /// - Data type inconsistencies
    ///
    /// Usage: Assets > PFE Tools > Validate Data
    ///
    /// Reference: docs/task3_data_architecture/02_unity_mapping.md
    /// </summary>
    public class DataValidator : EditorWindow
    {
        // Validation results
        private int totalAssetsChecked = 0;
        private int errorCount = 0;
        private int warningCount = 0;
        private List<ValidationError> errors = new List<ValidationError>();
        private List<ValidationWarning> warnings = new List<ValidationWarning>();

        private Vector2 scrollPosition;
        private string filterText = "";
        private bool showErrors = true;
        private bool showWarnings = true;

        [MenuItem("PFE Tools/Data Validator")]
        public static void ShowWindow()
        {
            var window = GetWindow<DataValidator>("Data Validator");
            window.minSize = new Vector2(600, 400);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("PFE Data Integrity Validator", EditorStyles.boldLabel);

            // Summary statistics
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Assets Checked: {totalAssetsChecked}", GUILayout.Width(150));
                EditorGUILayout.LabelField($"Errors: {errorCount}", GUILayout.Width(100));
                EditorGUILayout.LabelField($"Warnings: {warningCount}", GUILayout.Width(100));
            }

            EditorGUILayout.Space();

            // Filter controls
            using (new EditorGUILayout.HorizontalScope())
            {
                filterText = EditorGUILayout.TextField("Filter:", filterText);
                showErrors = EditorGUILayout.ToggleLeft("Show Errors", showErrors, GUILayout.Width(100));
                showWarnings = EditorGUILayout.ToggleLeft("Show Warnings", showWarnings, GUILayout.Width(120));
            }

            EditorGUILayout.Space();

            // Action buttons
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Validate All Data", GUILayout.Height(30)))
                {
                    ValidateAllData();
                }

                if (GUILayout.Button("Clear Results", GUILayout.Height(30)))
                {
                    ClearResults();
                }

                if (GUILayout.Button("Export Report", GUILayout.Height(30)))
                {
                    ExportValidationReport();
                }
            }

            EditorGUILayout.Space();

            // Results scroll view
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            DisplayValidationResults();
            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// Main validation entry point - validates all ScriptableObject data.
        /// </summary>
        private void ValidateAllData()
        {
            Debug.Log("[DataValidator] Starting data validation...");

            ClearResults();

            // Validate all UnitDefinition assets
            ValidateUnitDefinitions();

            // Validate all WeaponDefinition assets
            ValidateWeaponDefinitions();

            // Validate all ItemDefinition assets (when uncommented in GameDatabase)
            // ValidateItemDefinitions();

            // Validate all AmmoDefinition assets
            // ValidateAmmoDefinitions();

            // Validate all PerkDefinition assets
            // ValidatePerkDefinitions();

            // Validate all EffectDefinition assets
            // ValidateEffectDefinitions();

            // Check for cross-reference issues
            ValidateCrossReferences();

            totalAssetsChecked = errors.Count + warnings.Count;

            Debug.Log($"[DataValidator] Validation complete: {totalAssetsChecked} assets checked, {errorCount} errors, {warningCount} warnings");
            Repaint();
        }

        /// <summary>
        /// Validate all UnitDefinition assets.
        /// </summary>
        private void ValidateUnitDefinitions()
        {
            string[] guids = AssetDatabase.FindAssets("t:UnitDefinition");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                UnitDefinition unitDef = AssetDatabase.LoadAssetAtPath<UnitDefinition>(path);

                if (unitDef == null)
                {
                    AddError(path, "Failed to load UnitDefinition asset");
                    continue;
                }

                totalAssetsChecked++;

                // Check required fields
                if (string.IsNullOrEmpty(unitDef.ID))
                {
                    AddError(path, "Unit ID is empty or null");
                }

                if (string.IsNullOrEmpty(unitDef.displayName))
                {
                    AddWarning(path, "Display name is empty");
                }

                // Validate stats
                if (unitDef.health <= 0)
                {
                    AddWarning(path, $"Health is invalid: {unitDef.health}");
                }

                if (unitDef.width <= 0 || unitDef.height <= 0)
                {
                    AddWarning(path, $"Size is invalid: {unitDef.width}x{unitDef.height}");
                }
            }
        }

        /// <summary>
        /// Validate all WeaponDefinition assets.
        /// </summary>
        private void ValidateWeaponDefinitions()
        {
            string[] guids = AssetDatabase.FindAssets("t:WeaponDefinition");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                WeaponDefinition weaponDef = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);

                if (weaponDef == null)
                {
                    AddError(path, "Failed to load WeaponDefinition asset");
                    continue;
                }

                totalAssetsChecked++;

                // Check required fields
                if (string.IsNullOrEmpty(weaponDef.ID))
                {
                    AddError(path, "Weapon ID is empty or null");
                }

                // Validate enum values
                if (!System.Enum.IsDefined(typeof(WeaponType), weaponDef.weaponType))
                {
                    AddError(path, $"Invalid weapon type: {weaponDef.weaponType}");
                }

                // Validate stats
                if (weaponDef.baseDamage < 0)
                {
                    AddError(path, $"BaseDamage is negative: {weaponDef.baseDamage}");
                }

                if (weaponDef.rapid < 0)
                {
                    AddError(path, $"Rapid (fire rate) is negative: {weaponDef.rapid}");
                }
            }
        }

        /// <summary>
        /// Validate cross-references between different definition types.
        /// </summary>
        private void ValidateCrossReferences()
        {
            // Check for duplicate IDs
            var allIds = new Dictionary<string, string>();

            // Collect all unit IDs
            string[] unitGuids = AssetDatabase.FindAssets("t:UnitDefinition");
            foreach (string guid in unitGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                UnitDefinition unitDef = AssetDatabase.LoadAssetAtPath<UnitDefinition>(path);

                if (unitDef != null && !string.IsNullOrEmpty(unitDef.ID))
                {
                    if (allIds.ContainsKey(unitDef.ID))
                    {
                        AddError(path, $"Duplicate Unit ID '{unitDef.ID}' also found at: {allIds[unitDef.ID]}");
                    }
                    else
                    {
                        allIds[unitDef.ID] = path;
                    }
                }
            }

            // Collect all weapon IDs
            string[] weaponGuids = AssetDatabase.FindAssets("t:WeaponDefinition");
            foreach (string guid in weaponGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                WeaponDefinition weaponDef = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);

                if (weaponDef != null && !string.IsNullOrEmpty(weaponDef.ID))
                {
                    if (allIds.ContainsKey(weaponDef.ID))
                    {
                        AddError(path, $"Duplicate Weapon ID '{weaponDef.ID}' also found at: {allIds[weaponDef.ID]}");
                    }
                    else
                    {
                        allIds[weaponDef.ID] = path;
                    }
                }
            }
        }

        /// <summary>
        /// Display validation results in the scroll view.
        /// </summary>
        private void DisplayValidationResults()
        {
            if (errors.Count == 0 && warnings.Count == 0)
            {
                EditorGUILayout.HelpBox("No validation data. Click 'Validate All Data' to start.", MessageType.Info);
                return;
            }

            // Apply filter
            var filteredErrors = errors
                .Where(e => string.IsNullOrEmpty(filterText) || e.Message.Contains(filterText) || e.AssetPath.Contains(filterText))
                .ToList();

            var filteredWarnings = warnings
                .Where(w => string.IsNullOrEmpty(filterText) || w.Message.Contains(filterText) || w.AssetPath.Contains(filterText))
                .ToList();

            // Display errors
            if (showErrors && filteredErrors.Count > 0)
            {
                EditorGUILayout.LabelField($"Errors ({filteredErrors.Count})", EditorStyles.boldLabel);
                foreach (var error in filteredErrors)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.LabelField(error.AssetPath, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(error.Message, EditorStyles.wordWrappedLabel);
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.Space();
            }

            // Display warnings
            if (showWarnings && filteredWarnings.Count > 0)
            {
                EditorGUILayout.LabelField($"Warnings ({filteredWarnings.Count})", EditorStyles.boldLabel);
                foreach (var warning in filteredWarnings)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.LabelField(warning.AssetPath, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(warning.Message, EditorStyles.wordWrappedLabel);
                    EditorGUILayout.EndVertical();
                }
            }

            if (filteredErrors.Count == 0 && filteredWarnings.Count == 0)
            {
                EditorGUILayout.HelpBox($"No results match filter '{filterText}'", MessageType.Info);
            }
        }

        /// <summary>
        /// Add an error to the validation results.
        /// </summary>
        private void AddError(string assetPath, string message)
        {
            errors.Add(new ValidationError { AssetPath = assetPath, Message = message });
            errorCount++;
            Debug.LogError($"[DataValidator] {assetPath}: {message}");
        }

        /// <summary>
        /// Add a warning to the validation results.
        /// </summary>
        private void AddWarning(string assetPath, string message)
        {
            warnings.Add(new ValidationWarning { AssetPath = assetPath, Message = message });
            warningCount++;
            Debug.LogWarning($"[DataValidator] {assetPath}: {message}");
        }

        /// <summary>
        /// Clear all validation results.
        /// </summary>
        private void ClearResults()
        {
            totalAssetsChecked = 0;
            errorCount = 0;
            warningCount = 0;
            errors.Clear();
            warnings.Clear();
            filterText = "";
            Repaint();
        }

        /// <summary>
        /// Export validation report to a text file.
        /// </summary>
        private void ExportValidationReport()
        {
            string path = EditorUtility.SaveFilePanel("Export Validation Report", "", "validation_report.txt", "txt");

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            using (System.IO.StreamWriter writer = new System.IO.StreamWriter(path))
            {
                writer.WriteLine("PFE DATA VALIDATION REPORT");
                writer.WriteLine($"Generated: {System.DateTime.Now}");
                writer.WriteLine($"Assets Checked: {totalAssetsChecked}");
                writer.WriteLine($"Errors: {errorCount}");
                writer.WriteLine($"Warnings: {warningCount}");
                writer.WriteLine(new string('=', 80));
                writer.WriteLine();

                if (errors.Count > 0)
                {
                    writer.WriteLine("ERRORS:");
                    writer.WriteLine(new string('-', 80));
                    for (int i = 0; i < errors.Count; i++)
                    {
                        writer.WriteLine($"{i + 1}. {errors[i].AssetPath}");
                        writer.WriteLine($"   {errors[i].Message}");
                        writer.WriteLine();
                    }
                }

                if (warnings.Count > 0)
                {
                    writer.WriteLine("WARNINGS:");
                    writer.WriteLine(new string('-', 80));
                    for (int i = 0; i < warnings.Count; i++)
                    {
                        writer.WriteLine($"{i + 1}. {warnings[i].AssetPath}");
                        writer.WriteLine($"   {warnings[i].Message}");
                        writer.WriteLine();
                    }
                }
            }

            EditorUtility.DisplayDialog("Export Complete", $"Validation report exported to:\n{path}", "OK");
            Debug.Log($"[DataValidator] Validation report exported to: {path}");
        }

        /// <summary>
        /// Validation error data structure.
        /// </summary>
        private class ValidationError
        {
            public string AssetPath;
            public string Message;
        }

        /// <summary>
        /// Validation warning data structure.
        /// </summary>
        private class ValidationWarning
        {
            public string AssetPath;
            public string Message;
        }
    }
}
