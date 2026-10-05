#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PFE.Data.Definitions;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Gives every <see cref="ItemDefinition"/> the icon the oracle would draw for it on the ground,
    /// by copying the resolved frame out of the pfe export and assigning it to
    /// <see cref="ItemDefinition.icon"/>.
    ///
    /// <para><b>Why an importer and not hand-authored art.</b> The port had <b>no</b> item art at all:
    /// measured 10-05, 0 of 500 item assets carried an icon, no importer wrote the field, and the
    /// only sprite set that looked like it might serve (<c>Art/Imported/MapObjects/vis*</c>) is map
    /// objects, not items. So every dropped item drew
    /// <c>WorldItemPickup</c>'s 1×1 white fallback square — reported from play as "loot antidote has
    /// no sprite". The art was never missing; it was in the SWF the whole time.</para>
    ///
    /// <para><b>The art is a single clip with one frame per item id.</b> AS3 does not have an image
    /// per item. It has <c>visualItem</c> (SWF symbol 4356), a clip whose frames are <i>labelled</i>
    /// with item ids — 184 labelled frames, <c>antidote</c> on frame 23 — and it shows an item by
    /// <c>vis.gotoAndStop(item.id)</c> (<c>Loot.as:169</c>). Ammunition uses a second clip,
    /// <c>visualAmmo</c> (3737), and explosives and held weapons use a per-item <c>vis&lt;id&gt;</c>
    /// symbol. <see cref="ItemLootSpriteRule"/> decides which, and is where that decision is tested;
    /// this class is only the file plumbing.</para>
    ///
    /// <para><b>Output is one PNG per (symbol, frame), not per item.</b> 429 items resolve to
    /// <c>visualItem</c> but only 184 distinct frames exist, and 210 of those items share frame 1.
    /// Writing a file per item would produce ~500 near-duplicate assets for ~200 distinct images, so
    /// the output is keyed by symbol and frame and the many-to-one mapping lives where it belongs — in
    /// <see cref="ItemDefinition.icon"/>, which every consumer already reads.</para>
    ///
    /// <para><b>Idempotent.</b> A PNG is copied only when the source is newer, and a texture importer
    /// is touched only when a setting actually differs, so re-running over a correct tree changes
    /// nothing. That matters because this importer has to be safe to run after a fresh export.</para>
    /// </summary>
    public static class ItemIconImporter
    {
        // ── Paths ─────────────────────────────────────────────────────────────

        /// <summary>Where the resolved frames land: <c>{root}/{symbolName}/f{frame:D3}.png</c>.</summary>
        public const string SpritesOutputRoot = "Assets/_PFE/Art/Items/Sprites";

        /// <summary>The item definitions to wire — the same folder <c>FixDataImport</c> uses.</summary>
        public const string ItemDefsRoot = "Assets/_PFE/Data/Resources/Items";

        /// <summary>Source pixels per world unit. 100, the scale every other art import and the world itself use.</summary>
        public const int PixelsPerUnit = 100;

        // ── Result ────────────────────────────────────────────────────────────

        public sealed class ImportResult
        {
            public int ItemsConsidered;
            public int IconsAssigned;
            public int IconsAlreadyCorrect;
            public int SpritesCopied;
            public int Unresolved;
            public int TexturesRepaired;

            public readonly List<string> Warnings = new();
            public readonly List<string> Log = new();

            public bool Succeeded => Warnings.Count == 0;

            public void Info(string message)
            {
                Log.Add(message);
                Debug.Log($"[ItemIconImport] {message}");
            }

            public void Warn(string message)
            {
                Warnings.Add(message);
                Debug.LogWarning($"[ItemIconImport] {message}");
            }

            /// <summary>A one-screen summary, for the window and for the console.</summary>
            public string Summary()
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Items considered      : {ItemsConsidered}");
                sb.AppendLine($"Icons assigned        : {IconsAssigned}");
                sb.AppendLine($"Already correct       : {IconsAlreadyCorrect}");
                sb.AppendLine($"Frames copied         : {SpritesCopied}");
                sb.AppendLine($"Unresolved            : {Unresolved}");
                sb.AppendLine($"Textures repaired     : {TexturesRepaired}");
                sb.AppendLine($"Warnings              : {Warnings.Count}");
                return sb.ToString();
            }
        }

        // ── Entry point ───────────────────────────────────────────────────────

        /// <summary>
        /// Resolve and import every item icon. Returns a report rather than throwing, because the
        /// interesting outcomes here ("no source root", "this item has no art") are all reportable
        /// states, not exceptions.
        /// </summary>
        public static ImportResult Run()
        {
            var result = new ImportResult();

            string allDataPath = SourceImportPaths.AllDataAsPath;
            string swfPath = SourceImportPaths.AssetsSwfPath;
            string symbolTablePath = SourceImportPaths.PfeSymbolTablePath;

            if (!File.Exists(allDataPath))
            {
                result.Warn(SourceImportPaths.MissingSourceMessage(allDataPath, "AllData.as"));
                return result;
            }

            if (!File.Exists(swfPath))
            {
                result.Warn(SourceImportPaths.MissingSourceMessage(swfPath, "assets.swf"));
                return result;
            }

            // ── 1. Symbol table: name → id ────────────────────────────────────
            Dictionary<string, int> nameToId = ReadSymbolTable(symbolTablePath, result);
            if (nameToId == null) return result;

            // The two shared clips are addressed by NAME here and resolved to ids from the table, so a
            // shift in the export's id space cannot silently point this at the wrong clip.
            if (!nameToId.TryGetValue(ItemLootSpriteRule.VisualItemSymbol, out int visualItemId))
            {
                result.Warn($"'{ItemLootSpriteRule.VisualItemSymbol}' is not in {symbolTablePath} — " +
                            "without it almost no item can be resolved.");
                return result;
            }

            if (!nameToId.TryGetValue(ItemLootSpriteRule.VisualAmmoSymbol, out int visualAmmoId))
            {
                result.Warn($"'{ItemLootSpriteRule.VisualAmmoSymbol}' is not in {symbolTablePath} — " +
                            "ammunition icons would all be wrong.");
                return result;
            }

            // ── 2. Item tip/base, straight from the source XML ────────────────
            //
            // `tip` is read from AllData.as rather than from the asset's ItemType on purpose: the port's
            // enum cannot distinguish paint from food (both Misc) or compa from compw (both Component),
            // and the fallback table is keyed on the raw tip. See ItemLootSpriteRule.
            Dictionary<string, ItemSourceRow> sourceRows = ReadItemSourceRows(allDataPath, result);
            if (sourceRows == null) return result;

            // ── 3. Frame labels out of the SWF ────────────────────────────────
            SWFFile swf;
            try
            {
                swf = new SWFParser().Parse(swfPath);
            }
            catch (Exception ex)
            {
                result.Warn($"Could not parse {swfPath}: {ex.Message}");
                return result;
            }

            Dictionary<string, int> itemLabels = ReadFrameLabels(swf, visualItemId);
            Dictionary<string, int> ammoLabels = ReadFrameLabels(swf, visualAmmoId);

            result.Info($"Symbol table: {nameToId.Count} symbols. " +
                        $"{ItemLootSpriteRule.VisualItemSymbol}({visualItemId}) has {itemLabels.Count} labels, " +
                        $"{ItemLootSpriteRule.VisualAmmoSymbol}({visualAmmoId}) has {ammoLabels.Count}.");

            if (itemLabels.Count == 0)
            {
                // The whole general branch would collapse to frame 1 — i.e. one wrong icon for 429
                // items, which is worse than no icon because it looks deliberate. Refuse.
                result.Warn($"'{ItemLootSpriteRule.VisualItemSymbol}' has no labelled frames, so no item " +
                            "could be resolved to its own art. Aborting rather than assigning frame 1 to everything.");
                return result;
            }

            // ── 4. Plan ──────────────────────────────────────────────────────
            var plans = new List<Plan>();
            var seenDest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] guids = AssetDatabase.FindAssets("t:ItemDefinition", new[] { ItemDefsRoot });
            Array.Sort(guids, StringComparer.Ordinal);   // deterministic order, for a stable log

            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(assetPath);
                if (item == null) continue;

                result.ItemsConsidered++;

                string itemId = string.IsNullOrEmpty(item.itemId)
                    ? Path.GetFileNameWithoutExtension(assetPath)
                    : item.itemId;

                sourceRows.TryGetValue(itemId, out ItemSourceRow row);
                string tip = row?.Tip;
                string baseLabel = row?.Base;

                bool hasPerItemVis = nameToId.ContainsKey(
                    ItemLootSpriteRule.PerItemSymbolName(itemId) ?? string.Empty);

                ItemLootSprite resolved = ItemLootSpriteRule.Resolve(
                    itemId, tip, baseLabel, hasPerItemVis, itemLabels, ammoLabels);

                if (!resolved.IsResolved)
                {
                    result.Unresolved++;
                    result.Warn($"{itemId}: {resolved.Detail}");
                    continue;
                }

                if (!nameToId.TryGetValue(resolved.SymbolName, out int symbolId))
                {
                    result.Unresolved++;
                    result.Warn($"{itemId}: resolved to symbol '{resolved.SymbolName}', which is not in the symbol table");
                    continue;
                }

                string sourcePng = Path.Combine(
                    SourceImportPaths.PfeSpriteFolder(symbolId, resolved.SymbolName),
                    resolved.FrameNumber + ".png");

                string destPath = $"{SpritesOutputRoot}/{resolved.SymbolName}/f{resolved.FrameNumber:D3}.png";

                plans.Add(new Plan
                {
                    Item = item,
                    ItemId = itemId,
                    SymbolName = resolved.SymbolName,
                    FrameNumber = resolved.FrameNumber,
                    SourcePng = sourcePng,
                    DestPath = destPath,
                    Detail = resolved.Detail,
                });

                seenDest.Add(destPath);
            }

            result.Info($"Planned {plans.Count} item icons over {seenDest.Count} distinct frames.");

            // ── 5. Copy ──────────────────────────────────────────────────────
            EnsureFolder(SpritesOutputRoot);

            var missing = new List<string>();
            foreach (string dest in seenDest.OrderBy(d => d, StringComparer.Ordinal))
                EnsureFolder(Path.GetDirectoryName(dest)?.Replace('\\', '/'));

            foreach (Plan plan in plans)
            {
                if (!File.Exists(plan.SourcePng))
                {
                    // Counted per ITEM, warned per distinct FRAME: 210 items share visualItem frame 1,
                    // so warning per item would print the same missing path 210 times.
                    plan.Failed = true;
                    result.Unresolved++;
                    if (!missing.Contains(plan.SourcePng)) missing.Add(plan.SourcePng);
                    continue;
                }

                string destFull = Path.GetFullPath(plan.DestPath);
                bool stale = !File.Exists(destFull) ||
                             File.GetLastWriteTimeUtc(plan.SourcePng) > File.GetLastWriteTimeUtc(destFull);
                if (stale)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destFull));
                    File.Copy(plan.SourcePng, destFull, true);
                    result.SpritesCopied++;
                }
            }

            if (missing.Count > 0)
                foreach (string path in missing)
                    result.Warn($"Frame PNG missing from the export: {path}");

            AssetDatabase.Refresh();

            // ── 6. Configure the textures (batched) ──────────────────────────
            //
            // Batched on purpose. Per-asset SaveAndReimport collides with Unity 6's asynchronous import
            // and silently drops .meta writes — the failure that cost the weapon importer 2,574 pivots
            // on 10-05. StartAssetEditing/StopAssetEditing defers every import into one ordered flush.
            var imported = plans.Where(p => !p.Failed).ToList();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (Plan plan in imported)
                {
                    if (AssetImporter.GetAtPath(plan.DestPath) is TextureImporter importer)
                        ApplySpriteSettings(importer);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // ── 7. Verify each one really is a Sprite, and repair stragglers ──
            //
            // The pivot is not checked: centre is Unity's default for a new Sprite, so it is already
            // right even if a write is dropped. `textureType` is NOT the default — a raw PNG imports as
            // `Default` and then LoadAssetAtPath<Sprite> returns null, which would leave the icon
            // silently unassigned. That is the one worth verifying.
            var notASprite = new List<Plan>();
            foreach (Plan plan in imported)
            {
                if (AssetDatabase.LoadAssetAtPath<Sprite>(plan.DestPath) == null)
                    notASprite.Add(plan);
            }

            if (notASprite.Count > 0)
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (Plan plan in notASprite)
                    {
                        if (AssetImporter.GetAtPath(plan.DestPath) is TextureImporter importer)
                            ApplySpriteSettings(importer);

                        // Applying settings is not enough on its own: Unity caches the importer, so for a
                        // frame whose earlier write failed the settings already match, nothing is marked
                        // dirty, and no import is queued. The forced import is what re-serialises it.
                        AssetDatabase.ImportAsset(plan.DestPath, ImportAssetOptions.ForceUpdate);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                foreach (Plan plan in notASprite)
                {
                    if (AssetDatabase.LoadAssetAtPath<Sprite>(plan.DestPath) != null)
                        result.TexturesRepaired++;
                }
            }

            // ── 8. Assign ────────────────────────────────────────────────────
            foreach (Plan plan in imported)
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(plan.DestPath);
                if (sprite == null)
                {
                    result.Unresolved++;
                    result.Warn($"{plan.ItemId}: {plan.DestPath} did not import as a Sprite");
                    continue;
                }

                if (plan.Item.icon == sprite)
                {
                    result.IconsAlreadyCorrect++;
                    continue;
                }

                plan.Item.icon = sprite;
                EditorUtility.SetDirty(plan.Item);
                result.IconsAssigned++;
            }

            AssetDatabase.SaveAssets();

            result.Info(result.Summary().Replace("\r\n", " | ").Replace("\n", " | "));
            return result;
        }

        // ── Source readers ────────────────────────────────────────────────────

        /// <summary>One <c>&lt;item&gt;</c> row's attributes, as the rule needs them.</summary>
        private sealed class ItemSourceRow
        {
            public string Tip;
            public string Base;
        }

        private sealed class Plan
        {
            public ItemDefinition Item;
            public string ItemId;
            public string SymbolName;
            public int FrameNumber;
            public string SourcePng;
            public string DestPath;
            public string Detail;
            public bool Failed;
        }

        /// <summary>Reads <c>pfe/symbolClass/symbols.csv</c> → symbol name → symbol id.</summary>
        private static Dictionary<string, int> ReadSymbolTable(string path, ImportResult result)
        {
            if (!File.Exists(path))
            {
                result.Warn(SourceImportPaths.MissingSourceMessage(path, "symbols.csv"));
                return null;
            }

            var table = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(path))
            {
                // Format: 4356;"visualItem"
                int sep = line.IndexOf(';');
                if (sep <= 0) continue;

                if (!int.TryParse(line.Substring(0, sep).Trim(), out int id)) continue;

                string name = line.Substring(sep + 1).Trim().Trim('"');
                if (name.Length == 0) continue;

                table[name] = id;
            }

            if (table.Count == 0)
            {
                result.Warn($"{path} parsed to an empty symbol table.");
                return null;
            }

            return table;
        }

        /// <summary>
        /// Every <c>&lt;item id tip base&gt;</c> row from <c>AllData.as</c>. The XML literal is read
        /// through <see cref="AllDataXml"/> so this and the weapon importer cannot drift on how the
        /// block is delimited.
        /// </summary>
        private static Dictionary<string, ItemSourceRow> ReadItemSourceRows(string allDataPath, ImportResult result)
        {
            AllDataXml.Outcome outcome = AllDataXml.TryRead(
                File.ReadAllText(allDataPath), out XElement root, out _, out string error);

            if (outcome != AllDataXml.Outcome.Ok)
            {
                result.Warn($"AllData.as could not be read ({outcome}: {error}) — without item tips the " +
                            "tip-based fallback cannot be resolved.");
                return null;
            }

            var rows = new Dictionary<string, ItemSourceRow>(StringComparer.Ordinal);

            foreach (XElement element in root.Descendants("item"))
            {
                string id = (string)element.Attribute("id");
                if (string.IsNullOrEmpty(id)) continue;

                // First wins, matching the oracle's own linear lookup order.
                if (rows.ContainsKey(id)) continue;

                rows[id] = new ItemSourceRow
                {
                    Tip = (string)element.Attribute("tip"),
                    Base = (string)element.Attribute("base"),
                };
            }

            if (rows.Count == 0)
            {
                result.Warn("AllData.as contained no <item> rows.");
                return null;
            }

            result.Info($"AllData.as: {rows.Count} item rows.");
            return rows;
        }

        /// <summary>Frame label → 1-based frame number, for one symbol.</summary>
        private static Dictionary<string, int> ReadFrameLabels(SWFFile swf, int symbolId)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);

            if (swf == null || !swf.Symbols.TryGetValue(symbolId, out SWFSymbol symbol))
                return map;

            foreach (SWFFrame frame in symbol.Frames)
            {
                if (!string.IsNullOrEmpty(frame.Label))
                    map[frame.Label] = frame.FrameNumber;
            }

            return map;
        }

        // ── Texture settings ──────────────────────────────────────────────────

        /// <summary>
        /// Applies sprite / PPU / filter settings, reporting whether anything actually changed so a
        /// re-run over an already-correct tree stays a no-op.
        ///
        /// <para><b>Centre pivot, deliberately.</b> The weapon importer reproduces the SWF registration
        /// point because a held weapon rotates about the grip. An item icon is not held: it is drawn
        /// centred on the pickup (<c>WorldItemPickup.Spawn</c> normalises the sprite's longest axis to
        /// 0.36 units and leaves the transform at the loot position), so a registration-point pivot
        /// would push the icon off the pickup it belongs to. Centre is also what an inventory UI slot
        /// wants, and it is the field's default — so this only ever has to write it if something else
        /// has moved it.</para>
        /// </summary>
        private static bool ApplySpriteSettings(TextureImporter importer)
        {
            bool changed = false;

            if (importer.textureType != TextureImporterType.Sprite)
            { importer.textureType = TextureImporterType.Sprite; changed = true; }

            if (importer.spritePixelsPerUnit != PixelsPerUnit)
            { importer.spritePixelsPerUnit = PixelsPerUnit; changed = true; }

            if (importer.filterMode != FilterMode.Bilinear)
            { importer.filterMode = FilterMode.Bilinear; changed = true; }

            if (!importer.alphaIsTransparency)
            { importer.alphaIsTransparency = true; changed = true; }

            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            if (settings.spriteAlignment != (int)SpriteAlignment.Center)
            {
                settings.spriteAlignment = (int)SpriteAlignment.Center;
                settings.spritePivot = new Vector2(0.5f, 0.5f);
                importer.SetTextureSettings(settings);
                changed = true;
            }

            return changed;
        }

        // ── Folders ───────────────────────────────────────────────────────────

        /// <summary>Creates every missing folder segment under <c>Assets/</c>.</summary>
        private static void EnsureFolder(string unityRelPath)
        {
            if (string.IsNullOrEmpty(unityRelPath)) return;

            string[] parts = unityRelPath.Replace('\\', '/').Split('/');
            string current = parts[0];   // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
#endif
