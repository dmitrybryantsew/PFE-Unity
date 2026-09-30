#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports the <c>&lt;armor&gt;</c> elements of <c>AllData.as</c> into <see cref="ItemDefinition"/>
    /// assets, so armour definitions can exist at all.
    ///
    /// <para><b>Why this had to be written.</b> Steps 5 and 6 wired the equipped-armour model end to
    /// end and were compile-verified, but the feature was unreachable: nothing called
    /// <c>UnitStats.EquipArmour</c>, and <c>GameInventory.AddArmor</c> is a stub whose lookup is
    /// hardcoded <c>null</c> — so there was no armour definition to equip. The 451
    /// <c>Resources/Items/*.asset</c> files that <c>SimpleDataImporter</c> creates are empty shells
    /// built from <c>&lt;item&gt;</c> elements, and <b>armour is not an <c>&lt;item&gt;</c></b>: all 35
    /// of them are <c>&lt;armor&gt;</c>, which nothing read.</para>
    ///
    /// <para><b>Thin on purpose.</b> All the parsing lives in <see cref="ArmourDataParser"/> in
    /// <c>PFE.Core</c>, because <c>PFE.Tests</c> references <c>PFE.Core</c> but not <c>PFE.Editor</c> —
    /// a parser that lived here could only be tested by running Unity. This file reads a file, calls
    /// the parser, writes assets and reports.</para>
    ///
    /// <para>Idempotent: an existing asset is loaded and re-applied rather than skipped, matching
    /// <c>WeaponDataImporter</c>. Re-running after a data fix therefore updates in place.</para>
    /// </summary>
    public static class ArmourDataImporter
    {
        private static string SourceFilePath => SourceImportPaths.AllDataAsPath;

        /// <summary>
        /// Note the spelling — <c>Armor</c>, matching the existing <c>Resources</c> folder naming in
        /// this project, even though the type is <c>ArmourDefinitionData</c>. Asset paths are not worth
        /// a rename of every sibling folder.
        /// </summary>
        private static readonly string OutputPath = "Assets/_PFE/Data/Resources/Armor";

        /// <summary>Pulls the attribute name out of a parser warning, for the aggregated summary.</summary>
        private static readonly Regex DroppedAttributeRegex =
            new Regex(@"@([A-Za-z_][A-Za-z0-9_]*)='", RegexOptions.Compiled);

        [MenuItem("PFE/Data/Import Armour from AllData.as")]
        public static void ImportArmour()
        {
            if (!File.Exists(SourceFilePath))
            {
                Debug.LogError(SourceImportPaths.MissingSourceMessage(SourceFilePath, "AllData.as"));
                return;
            }

            if (!Directory.Exists(OutputPath))
                Directory.CreateDirectory(OutputPath);

            List<ArmourDefinitionData> definitions;
            try
            {
                definitions = ArmourDataParser.ParseAll(File.ReadAllText(SourceFilePath));
            }
            catch (Exception e)
            {
                // ParseAll throws only on an <armor> with no id, which means the source is malformed.
                // Failing the whole run is deliberate: importing 34 of 35 and saying "Done" would hide it.
                Debug.LogError($"[ArmourDataImporter] AllData.as did not parse: {e.Message}\n{e.StackTrace}");
                return;
            }

            if (definitions.Count == 0)
            {
                Debug.LogError(
                    $"[ArmourDataImporter] Parsed 0 <armor> elements from {SourceFilePath}. " +
                    "Expected 35. The source root is probably resolving to the wrong tree.");
                return;
            }

            int imported = 0, updated = 0, skipped = 0, levels = 0, multiLevel = 0;
            var droppedAttributes = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var unrecognised = new List<string>();

            foreach (ArmourDefinitionData data in definitions)
            {
                string assetPath = $"{OutputPath}/{data.id}.asset";
                bool exists = File.Exists(assetPath);

                ItemDefinition definition = exists
                    ? AssetDatabase.LoadAssetAtPath<ItemDefinition>(assetPath)
                    : ScriptableObject.CreateInstance<ItemDefinition>();

                if (definition == null)
                {
                    Debug.LogWarning($"[ArmourDataImporter] Could not load or create an asset for '{data.id}'.");
                    skipped++;
                    continue;
                }

                try
                {
                    data.ApplyTo(definition);

                    if (exists)
                    {
                        EditorUtility.SetDirty(definition);
                        updated++;
                    }
                    else
                    {
                        AssetDatabase.CreateAsset(definition, assetPath);
                        imported++;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ArmourDataImporter] Failed on '{data.id}': {e.Message}\n{e.StackTrace}");
                    skipped++;
                    continue;
                }

                levels += data.levels.Length;
                if (data.levels.Length > 1) multiLevel++;

                CollectWarnings(data, droppedAttributes, unrecognised);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                $"[ArmourDataImporter] Done. {definitions.Count} armour definitions, {levels} levels " +
                $"({multiLevel} with more than one). Imported: {imported}  Updated: {updated}  Skipped: {skipped}");

            ReportDroppedAttributes(droppedAttributes, unrecognised);
        }

        /// <summary>
        /// Split the parser's warnings into the two kinds that deserve different treatment.
        ///
        /// <para><b>Known omissions are aggregated; unknowns are escalated.</b> Logging all of them
        /// individually would be roughly 120 lines for this data set — <c>@kol</c> alone appears on 36
        /// upgrade levels — and the signal would drown. But the known list is the interesting part of
        /// the import: it is the difference between "we decided not to port the sneak channel" and
        /// "we forgot <c>radx</c>". So the deliberate drops become one summary line, and anything the
        /// parser could not classify is logged on its own.</para>
        /// </summary>
        private static void CollectWarnings(
            ArmourDefinitionData data,
            SortedDictionary<string, int> droppedAttributes,
            List<string> unrecognised)
        {
            foreach (string warning in data.warnings)
            {
                if (warning.Contains("UNRECOGNISED"))
                {
                    unrecognised.Add(warning);
                    continue;
                }

                Match match = DroppedAttributeRegex.Match(warning);
                string attribute = match.Success ? match.Groups[1].Value : "(unparsed)";

                droppedAttributes.TryGetValue(attribute, out int count);
                droppedAttributes[attribute] = count + 1;
            }
        }

        private static void ReportDroppedAttributes(
            SortedDictionary<string, int> droppedAttributes,
            List<string> unrecognised)
        {
            if (droppedAttributes.Count > 0)
            {
                var summary = new System.Text.StringBuilder();
                foreach (KeyValuePair<string, int> entry in droppedAttributes)
                    summary.Append(summary.Length > 0 ? ", " : string.Empty)
                           .Append('@').Append(entry.Key).Append(" (").Append(entry.Value).Append(')');

                Debug.Log(
                    $"[ArmourDataImporter] Deliberately not imported, by attribute: {summary}. " +
                    "Each is a recorded decision — see ArmourDataParser's omission lists for the reason " +
                    "and the AS3 line it comes from.");
            }

            foreach (string warning in unrecognised)
            {
                Debug.LogWarning(
                    $"[ArmourDataImporter] {warning} A new attribute in the source data is being dropped " +
                    "silently; classify it in ArmourDataParser (handled, or a reasoned omission).");
            }
        }
    }
}
#endif
