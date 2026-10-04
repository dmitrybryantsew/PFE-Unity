#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PFE.Data.Definitions;
using PFE.Systems.Particles;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports the <c>&lt;part&gt;</c> block of <c>AllData.as</c> into the single
    /// <see cref="ParticleDefinitionAsset"/> the particle runtime reads.
    ///
    /// <para><b>The parsing lives in <see cref="ParticleXmlReader"/>, in the runtime assembly, not
    /// here.</b> <c>PFE.Tests</c> references <c>PFE.Core</c> but not <c>PFE.Editor</c>, so a reader that
    /// lived in this file could not be exercised by the offline wall at all — and this reader has a
    /// whole class of silent-wrong-value bugs (five attribute names that are suffixes of other
    /// attribute names) that is invisible without a test. This file is left with I/O, asset writing and
    /// the two self-consistency checks.</para>
    ///
    /// <para><b>Two controls, because "it ran and said success" is not evidence.</b></para>
    /// <list type="bullet">
    /// <item><description><b>A literal-count cross-check.</b> The number of rows the reader produced is
    /// compared against the number of literal <c>&lt;part id='</c> occurrences in the raw file. The
    /// reader strips XML comments, so a <i>commented-out</i> row legitimately makes these differ — and
    /// that is exactly the case worth surfacing rather than silently absorbing.</description></item>
    /// <item><description><b>A non-empty check that is distinguishable from an empty block.</b> Zero
    /// rows is reported as an error with the reason, never as "0 definitions imported, done" — the
    /// failure mode this project keeps hitting.</description></item>
    /// </list>
    /// </summary>
    public static class ParticleDataImporter
    {
        private const string OutputPath = "Assets/_PFE/Data/Resources/ParticleDefinitions.asset";

        private static string SourceFilePath => SourceImportPaths.AllDataAsPath;

        /// <summary>Rows the oracle's block is known to contain. Used only to make a surprise loud.</summary>
        private const int ExpectedRowCount = 118;

        [MenuItem("PFE/Data/Import Particles from AllData.as", priority = 22)]
        public static void ImportParticles()
        {
            if (!File.Exists(SourceFilePath))
            {
                Debug.LogError(SourceImportPaths.MissingSourceMessage(SourceFilePath, "AllData.as"));
                return;
            }

            string content;
            using (var reader = new StreamReader(SourceFilePath, Encoding.UTF8))
            {
                content = reader.ReadToEnd();
            }

            ParticleXmlParseResult result = ParticleXmlReader.Parse(content);

            if (result.Definitions.Count == 0)
            {
                Debug.LogError(
                    "Particle import found 0 <part> rows. The reader depends on self-closing " +
                    "<part id='…' …/> rows; if the block moved or changed form, fix the reader rather " +
                    "than treating this as an empty data set.");
                return;
            }

            // Positive control. Counted on the RAW text, so a commented-out row shows up as a
            // difference instead of vanishing.
            int literalRows = Regex.Matches(content, "<part id='").Count;
            if (literalRows != result.Definitions.Count)
            {
                Debug.LogWarning(
                    $"Particle import: {result.Definitions.Count} rows parsed but {literalRows} " +
                    "literal '<part id=' occurrences are present. A difference means a row is " +
                    "commented out or the row pattern is too strict — check before trusting the asset.");
            }

            var asset = AssetDatabase.LoadAssetAtPath<ParticleDefinitionAsset>(OutputPath);
            bool isNew = asset == null;
            if (isNew)
            {
                asset = ScriptableObject.CreateInstance<ParticleDefinitionAsset>();
            }

            // Assign a fresh array rather than mutating the existing one, so the lazily-built
            // ParticleDefinitionTable cannot go stale behind us.
            var definitions = new ParticleDefinition[result.Definitions.Count];
            for (int i = 0; i < definitions.Length; i++) definitions[i] = result.Definitions[i];
            asset.definitions = definitions;

            asset.ignoredAttributes = ToArray(result.IgnoredAttributes);
            asset.malformedAttributes = ToArray(result.MalformedAttributes);
            asset.InvalidateTable();

            if (isNew)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(OutputPath) ?? "Assets");
                AssetDatabase.CreateAsset(asset, OutputPath);
            }
            else
            {
                EditorUtility.SetDirty(asset);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                $"Particle import complete — {definitions.Length} <part> rows written to {OutputPath} " +
                $"(expected {ExpectedRowCount}). " +
                $"visual rows: {CountVisual(definitions, true)}, blit rows: {CountVisual(definitions, false)}.");

            // The readback that the oracle does not have. Emitter silently discards any attribute it
            // has no field for, and 'rr' — which the data's own comment documents as random rotation
            // speed — is one of them.
            if (asset.ignoredAttributes.Length > 0)
            {
                Debug.LogWarning(
                    "Particle import: the source carries attributes Emitter has NO FIELD for, so the " +
                    "oracle discards them and so does the port: " +
                    string.Join(", ", asset.ignoredAttributes) + ". " +
                    "('rr' is documented in AllData.as as 'случайная скорость вращения' — it has never " +
                    "worked, in AS3 or here.)");
            }

            if (asset.malformedAttributes.Length > 0)
            {
                Debug.LogWarning(
                    "Particle import: these attributes were present but unparseable and fell back to " +
                    "their defaults: " + string.Join(", ", asset.malformedAttributes) + ".");
            }
        }

        private static string[] ToArray(System.Collections.Generic.IReadOnlyList<string> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<string>();
            var array = new string[source.Count];
            for (int i = 0; i < source.Count; i++) array[i] = source[i];
            return array;
        }

        private static int CountVisual(ParticleDefinition[] definitions, bool wantVisual)
        {
            int n = 0;
            foreach (ParticleDefinition def in definitions)
            {
                if (def == null) continue;
                bool hasVisual = !string.IsNullOrEmpty(def.Vis);
                if (hasVisual == wantVisual) n++;
            }
            return n;
        }
    }
}
#endif
