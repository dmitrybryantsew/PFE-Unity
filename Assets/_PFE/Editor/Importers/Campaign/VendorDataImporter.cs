using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions.Campaign;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports the 13 <c>&lt;vendor&gt;</c> blocks of <c>GameData.d.vendor</c> into
    /// <see cref="VendorDefinition"/> assets.
    ///
    /// <para><b>Why this importer exists.</b> The port shipped the vendor's <i>arithmetic</i>
    /// (<c>VendorInventory</c>) and none of its <i>data</i>: <c>CampaignDataParser</c> had branches for
    /// <c>&lt;land&gt;</c>, <c>&lt;quest&gt;</c>, <c>&lt;npc&gt;</c> and <c>&lt;scr&gt;</c> and none for
    /// <c>&lt;vendor&gt;</c>, so a finished trade page would have rendered an empty shelf that looks
    /// exactly like a working page with no stock. This is the research's G1.</para>
    ///
    /// <para><b>Re-runnable and idempotent.</b> An asset that already exists is updated in place rather
    /// than recreated, so its GUID — and therefore every reference to it — survives a re-import. Run it
    /// from <c>PFE ▸ Campaign ▸ Import Vendor Stock from GameData.as</c>.</para>
    ///
    /// <para><b>The import is an editor action, and the acceptance checks are not.</b> The parser is
    /// plain C#, so the counts (13 blocks, 83 <c>vendor_weapon</c> rows, the <c>shotgun^1</c> split) are
    /// asserted offline against the shipped <c>GameData.as</c> in
    /// <c>Tests/Editor/Campaign/VendorDataTests.cs</c> — running the importer is what turns those rows
    /// into assets, and that needs the editor.</para>
    /// </summary>
    public static class VendorDataImporter
    {
        public const string VendorsDirectory = "Assets/_PFE/Data/Resources/Campaign/Vendors";

        [MenuItem("PFE/Campaign/Import Vendor Stock from GameData.as")]
        public static void ImportAllMenu()
        {
            ImportAll(interactive: true);
        }

        public static List<VendorDefinition> ImportAll(bool interactive = false)
        {
            string content = CampaignDataImporter.LoadGameDataContent();
            if (string.IsNullOrEmpty(content))
            {
                Debug.LogError("[VendorDataImporter] Could not load GameData.as content!");
                return new List<VendorDefinition>();
            }

            EnsureDirectories();

            List<VendorDefinition> vendors = ImportFromContent(content);
            CampaignDataImporter.UpdateCatalog(vendors: vendors);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[VendorDataImporter] Successfully imported {vendors.Count} VendorDefinitions into {VendorsDirectory}.");

            if (interactive)
            {
                EditorUtility.DisplayDialog(
                    "Vendor Stock Import Complete",
                    $"Successfully imported {vendors.Count} Vendor Definitions.\n" +
                    "Each carries its <buy> rows and <task> rows. Attach them to NpcDefinition.vendorId.",
                    "OK");
            }

            return vendors;
        }

        private static void EnsureDirectories()
        {
            if (!Directory.Exists("Assets/_PFE/Data/Resources/Campaign"))
            {
                Directory.CreateDirectory("Assets/_PFE/Data/Resources/Campaign");
            }
            if (!Directory.Exists(VendorsDirectory))
            {
                Directory.CreateDirectory(VendorsDirectory);
            }
        }

        /// <summary>
        /// Parses and writes one asset per block. Split out from <see cref="ImportAll"/> so the offline
        /// verification can exercise everything except the two <c>AssetDatabase</c> calls.
        /// </summary>
        public static List<VendorDefinition> ImportFromContent(string content)
        {
            List<VendorParseResult> parsed = CampaignDataParser.ParseVendors(content);
            var results = new List<VendorDefinition>();

            foreach (VendorParseResult block in parsed)
            {
                if (string.IsNullOrEmpty(block.vendorId)) continue;

                if (block.unrecognisedAttributes.Count > 0)
                {
                    // Rule #12: an attribute no field claims is reported, never dropped in silence.
                    Debug.LogWarning(
                        $"[VendorDataImporter] vendor '{block.vendorId}' carries attributes no field reads: " +
                        $"{string.Join(", ", block.unrecognisedAttributes)}. Add a field or the data is lost.");
                }

                string assetPath = $"{VendorsDirectory}/{block.vendorId}.asset";
                var def = AssetDatabase.LoadAssetAtPath<VendorDefinition>(assetPath);
                if (def == null)
                {
                    def = ScriptableObject.CreateInstance<VendorDefinition>();
                    AssetDatabase.CreateAsset(def, assetPath);
                }

                def.vendorId = block.vendorId;

                // CopySerialized is deliberately not used: the parser returns plain data (so it stays
                // ECall-free and offline-testable), not a ScriptableObject to copy from.
                def.stock = new List<VendorStockEntry>(block.stock);
                def.tasks = new List<VendorTaskEntry>(block.tasks);

                EditorUtility.SetDirty(def);
                results.Add(def);
            }

            return results;
        }

        /// <summary>
        /// A human-readable summary of what a run would produce, without touching the AssetDatabase.
        /// Used by the menu's confirmation and by the offline verification report.
        /// </summary>
        public static string DescribeContent(string content)
        {
            List<VendorParseResult> parsed = CampaignDataParser.ParseVendors(content);
            var sb = new StringBuilder();
            sb.AppendLine($"vendor blocks: {parsed.Count}");
            foreach (VendorParseResult block in parsed)
            {
                sb.AppendLine($"  {block.vendorId,-16} buys={block.stock.Count,4} tasks={block.tasks.Count,2}");
            }
            return sb.ToString();
        }
    }
}
