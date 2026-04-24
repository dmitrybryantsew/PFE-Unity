#if UNITY_EDITOR
using System.IO;
using System.Linq;
using PFE.Character;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Generates a CharacterStyleData ScriptableObject from CharacterStyleLayerImporter results.
    /// Wires up sprite references for each discovered layer so the assembler can use them at runtime.
    /// </summary>
    public static class CharacterStyleDataGenerator
    {
        const string OutputPath = "Assets/_PFE/Data/Resources/Character/CharacterStyleData.asset";

        /// <summary>
        /// Generate or update the CharacterStyleData asset from import results.
        /// </summary>
        public static CharacterStyleData Generate(CharacterStyleLayerImporter.ImportResult importResult)
        {
            // Ensure output directory
            string dir = Path.GetDirectoryName(OutputPath);
            if (!Directory.Exists(Path.GetFullPath(dir)))
                Directory.CreateDirectory(Path.GetFullPath(dir));

            // Load or create the asset
            var data = AssetDatabase.LoadAssetAtPath<CharacterStyleData>(OutputPath);
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<CharacterStyleData>();
                AssetDatabase.CreateAsset(data, OutputPath);
            }

            // Build layer groups from discovered layers
            var groups = new System.Collections.Generic.List<StyleLayerGroup>();

            foreach (var layer in importResult.Layers)
            {
                if (!importResult.SpritesBySymbol.TryGetValue(layer.ChildSymbolId, out var frameMap))
                    continue;

                if (frameMap.Count == 0) continue;

                // Order sprites by frame number
                var orderedSprites = frameMap
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => kvp.Value)
                    .Where(s => s != null)
                    .ToArray();

                if (orderedSprites.Length == 0) continue;

                groups.Add(new StyleLayerGroup
                {
                    partName = layer.PartName,
                    layerName = layer.LayerRole,
                    tintChannel = layer.TintChannel,
                    sprites = orderedSprites,
                });

                Debug.Log($"[StyleDataGen] {layer.PartName}/{layer.LayerRole}: {orderedSprites.Length} sprites");
            }

            data.layers = groups.ToArray();
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            Debug.Log($"[StyleDataGen] Generated {groups.Count} style layer groups at {OutputPath}");
            return data;
        }
    }
}
#endif
