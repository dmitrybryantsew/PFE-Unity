using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions.Campaign;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports NPC persona definitions and campaign event scripts from GameData.as into Unity.
    /// Creates ScriptableObjects for all 54 NPCs (with their dialogue trees) and 73 stand-alone scripts.
    /// </summary>
    public static class DialogueDataImporter
    {
        public const string NpcsDirectory = "Assets/_PFE/Data/Resources/Campaign/NPCs";
        public const string ScriptsDirectory = "Assets/_PFE/Data/Resources/Campaign/Scripts";

        [MenuItem("PFE/Campaign/Import NPCs and Scripts from GameData.as")]
        public static void ImportAllMenu()
        {
            ImportAll(interactive: true);
        }

        public static (List<NpcDefinition> npcs, List<CampaignScriptDefinition> scripts) ImportAll(bool interactive = false)
        {
            string content = CampaignDataImporter.LoadGameDataContent();
            if (string.IsNullOrEmpty(content))
            {
                Debug.LogError("[DialogueDataImporter] Could not load GameData.as content!");
                return (new List<NpcDefinition>(), new List<CampaignScriptDefinition>());
            }

            EnsureDirectories();

            var (npcs, scripts) = ImportFromContent(content);
            CampaignDataImporter.UpdateCatalog(null, null, npcs, scripts);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            WarnAboutMissingPersonaArt(npcs);

            Debug.Log($"[DialogueDataImporter] Successfully imported {npcs.Count} NPCs and {scripts.Count} Scripts into Campaign resources.");

            if (interactive)
            {
                EditorUtility.DisplayDialog(
                    "NPC & Script Import Complete",
                    $"Successfully imported:\n- {npcs.Count} NPC Definitions\n- {scripts.Count} Campaign Script Sequences",
                    "OK"
                );
            }

            return (npcs, scripts);
        }

        private static void EnsureDirectories()
        {
            if (!Directory.Exists("Assets/_PFE/Data/Resources/Campaign"))
            {
                Directory.CreateDirectory("Assets/_PFE/Data/Resources/Campaign");
            }
            if (!Directory.Exists(NpcsDirectory))
            {
                Directory.CreateDirectory(NpcsDirectory);
            }
            if (!Directory.Exists(ScriptsDirectory))
            {
                Directory.CreateDirectory(ScriptsDirectory);
            }
        }

        public static (List<NpcDefinition> npcs, List<CampaignScriptDefinition> scripts) ImportFromContent(string content)
        {
            var (parsedNpcs, parsedScripts) = CampaignDataParser.ParseNpcsAndScripts(content);
            var npcResults = new List<NpcDefinition>();
            var scriptResults = new List<CampaignScriptDefinition>();

            // 1. Import NPCs
            foreach (var parsed in parsedNpcs)
            {
                string id = parsed.npcId;
                if (string.IsNullOrEmpty(id)) continue;

                string assetPath = $"{NpcsDirectory}/{id}.asset";
                var npcDef = AssetDatabase.LoadAssetAtPath<NpcDefinition>(assetPath);
                if (npcDef == null)
                {
                    npcDef = ScriptableObject.CreateInstance<NpcDefinition>();
                    AssetDatabase.CreateAsset(npcDef, assetPath);
                }

                // `CopySerialized` overwrites EVERY serialised field on the target, and the parser is
                // only a source for the fields GameData.as carries. The four visual fields are not
                // among them: `UnitSpriteImporter`'s NPC pass writes them from the SWF sprite export,
                // so they are *asset* state, not *data* state. Copying over them wipes them silently.
                //
                // That is not hypothetical — it is how Calamity lost his body. `PFE/Art/Import Unit
                // Sprites` writes the sprite, `PFE/Campaign/Import NPCs and Scripts` (and therefore
                // `Import Entire Campaign`) erases it, and neither step reports anything, so "I
                // imported everything" ends with every persona pointing at no art. Measured in the
                // asset mtimes of the run that produced it: art at 12:37, NPCs at 12:38, lands at
                // 12:41.
                //
                // The first fix restored `visualSprite` alone, which was one quarter of the answer.
                // `visualFrames` (the persona's own `osn` clip), `visualScaleX` and `visualScaleY` come
                // from the SAME pass and were still being erased — and the visible result is subtler
                // than a missing body, therefore worse: a static first frame at scale 1. The doctor and
                // every vendor lose their mirrored shrink (-0.316, 0.316), and every animated persona
                // freezes on frame 1. Restore the family, not the field that happened to be reported.
                Sprite importedVisual = npcDef.visualSprite;
                var importedFrames = new List<NpcVisualFrame>(
                    npcDef.visualFrames ?? new List<NpcVisualFrame>());
                float importedScaleX = npcDef.visualScaleX;
                float importedScaleY = npcDef.visualScaleY;

                EditorUtility.CopySerialized(parsed, npcDef);

                // Restored rather than merged: the parser has no opinion about any of these fields, so
                // the value already on the asset is the only correct one. Re-running the art import is
                // still what *creates* them the first time.
                npcDef.visualSprite = importedVisual;
                npcDef.visualFrames = importedFrames;
                npcDef.visualScaleX = importedScaleX;
                npcDef.visualScaleY = importedScaleY;

                EditorUtility.SetDirty(npcDef);
                npcResults.Add(npcDef);
            }

            // 2. Import Stand-Alone Scripts
            foreach (var parsed in parsedScripts)
            {
                string id = parsed.scriptId;
                if (string.IsNullOrEmpty(id)) continue;

                string assetPath = $"{ScriptsDirectory}/{id}.asset";
                var scriptDef = AssetDatabase.LoadAssetAtPath<CampaignScriptDefinition>(assetPath);
                if (scriptDef == null)
                {
                    scriptDef = ScriptableObject.CreateInstance<CampaignScriptDefinition>();
                    AssetDatabase.CreateAsset(scriptDef, assetPath);
                }

                EditorUtility.CopySerialized(parsed, scriptDef);
                EditorUtility.SetDirty(scriptDef);
                scriptResults.Add(scriptDef);
            }

            // A number to compare across runs, because the art fields are the one thing this import
            // neither owns nor can reconstruct. Before the restore above, a re-import produced
            // "54 with a body sprite, 0 with an osn clip, 0 with a non-unit scale" — the clip and the
            // scale were erased on every run and nothing said so. Reading the same four numbers before
            // and after a re-import is what turns "did that break my art?" into an observation.
            int withSprite = 0;
            int withClip = 0;
            int withScale = 0;
            for (int i = 0; i < npcResults.Count; i++)
            {
                NpcDefinition npc = npcResults[i];
                if (npc.visualSprite != null) withSprite++;
                if (npc.visualFrames != null && npc.visualFrames.Count > 0) withClip++;
                if (!Mathf.Approximately(npc.visualScaleX, 1f) || !Mathf.Approximately(npc.visualScaleY, 1f))
                {
                    withScale++;
                }
            }

            Debug.Log($"[DialogueDataImporter] {npcResults.Count} personas: {withSprite} with a body " +
                      $"sprite, {withClip} with an osn clip, {withScale} with a non-unit scale. " +
                      "(The clip and the scale are UnitSpriteImporter's, not this import's — if they " +
                      "drop to 0, run PFE/Art/Import Unit Sprites again.)");

            return (npcResults, scriptResults);
        }

        /// <summary>
        /// Say so when a persona names a <c>vis</c> but has no body sprite — the one failure mode this
        /// import can neither create nor detect from its own inputs.
        ///
        /// <para><b>Why it is worth a warning rather than silence.</b> <c>vis</c> and <c>visualSprite</c>
        /// come from two different importers (<c>GameData.as</c> and the SWF sprite export), so a run
        /// order that ends with this one leaves every persona artless while both importers report
        /// success. The symptom is a single NPC drawn as a floating weapon — which reads as "the model
        /// is broken", not as "an asset field is null", and cost a debugging session to place. The
        /// sprite is preserved above, so this warning fires only when it was never imported (or when the
        /// persona's <c>vis</c> names a symbol with no frames, which
        /// <c>UnitSpriteImporter</c> reports as <c>npc personas … with no visual</c>).</para>
        ///
        /// <para>An absent <c>vis</c> is <b>not</b> reported: it is optional in the Oracle and falls back
        /// to <c>visualVendor</c> (<c>UnitNPC.as:107</c>).</para>
        /// </summary>
        private static void WarnAboutMissingPersonaArt(List<NpcDefinition> npcs)
        {
            var missing = new List<string>();

            for (int i = 0; i < npcs.Count; i++)
            {
                NpcDefinition npc = npcs[i];
                if (npc == null) continue;
                if (string.IsNullOrWhiteSpace(npc.visualId)) continue;
                if (npc.visualSprite != null) continue;
                missing.Add(npc.npcId);
            }

            if (missing.Count == 0) return;

            // Capped, because this is a summary and the full list belongs in the art importer's report.
            const int shown = 12;
            string ids = string.Join(", ", missing.GetRange(0, Math.Min(shown, missing.Count)));
            if (missing.Count > shown) ids += ", …";

            Debug.LogWarning(
                $"[DialogueDataImporter] {missing.Count} of {npcs.Count} NPC personas name a `vis` but " +
                "carry no body sprite, so they will spawn as colliders with no art. This import neither " +
                "creates that sprite nor overwrites it — run PFE/Art/Import Unit Sprites. If it has " +
                $"already been run, those personas name a symbol the sprite export does not have. " +
                $"Affected: {ids}");
        }
    }
}
