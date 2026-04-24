using UnityEngine;
using UnityEditor;
using PFE.Editor.Importers;

namespace PFE.Editor
{
    /// <summary>
    /// Batch imports all remaining core data types from AllData.as.
    /// This completes Phase 1A of the implementation plan.
    /// </summary>
    public class BatchCoreDataImport
    {
        [MenuItem("PFE/Data/Import All Core Data (Effects, Units, Weapons)")]
        public static void ImportAllCoreData()
        {
            ImportAllCoreDataInternal();

            // Show summary
            EditorUtility.DisplayDialog(
                "Core Data Import Complete",
                "Effects, Units, and Weapons have been imported from AllData.as\n\n" +
                "Check the console for details.",
                "OK"
            );
        }

        /// <summary>
        /// Internal method that performs the actual import without UI dialogs.
        /// Safe to call from batch mode.
        /// </summary>
        public static void ImportAllCoreDataInternal()
        {
            Debug.Log("=== Starting Core Data Import ===");

            // Import Effects
            Debug.Log("\n1. Importing Effects...");
            EffectDataImporter.ImportEffects();

            // Import Units
            Debug.Log("\n2. Importing Units...");
            UnitDataImporter.ImportUnits();

            // Import Weapons
            Debug.Log("\n3. Importing Weapons...");
            WeaponDataImporter.ImportWeapons();

            Debug.Log("\n=== Core Data Import Complete ===");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }
}
