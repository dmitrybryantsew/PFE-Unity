#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Editor.Importers;
// The AllData.as readers live in the runtime assembly on purpose — PFE.Tests cannot reference
// PFE.Editor, so anything written here instead would be invisible to the offline wall.
using PFE.Systems.Weapons;
using PFE.Systems.Items;

namespace PFE.Editor
{
    /// <summary>
    /// Simple data importer that creates assets with minimal data.
    /// IDs and other fields will be populated by FixDataImport.cs based on filenames.
    /// </summary>
    public static class SimpleDataImporter
    {
        private static string SourceFilePath => SourceImportPaths.AllDataAsPath;

        [MenuItem("PFE/Data/Simple Import All Data")]
        public static void ImportAll()
        {
            ImportAmmo();
            ImportItems();
            ImportPerks();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Simple import complete. Run 'PFE > Data > Fix Imported Data IDs' to set IDs.");
        }

        private static void ImportAmmo()
        {
            string outputPath = "Assets/_PFE/Data/Resources/Ammo";
            Directory.CreateDirectory(outputPath);

            string content = File.ReadAllText(SourceFilePath);
            var matches = System.Text.RegularExpressions.Regex.Matches(content,
                "<item\\s+([^>]*?)tip\\s*=\\s*['\"]a['\"]([^>]*?)>",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            int imported = 0;
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string tagContent = match.Groups[1].Value + match.Groups[2].Value;
                string id = ExtractAttribute(tagContent, "id");

                if (!string.IsNullOrEmpty(id) && id != "not" && id != "recharg")
                {
                    string assetPath = $"{outputPath}/{id}.asset";
                    if (AssetDatabase.LoadAssetAtPath<AmmoDefinition>(assetPath) == null)
                    {
                        var ammo = ScriptableObject.CreateInstance<AmmoDefinition>();
                        AssetDatabase.CreateAsset(ammo, assetPath);
                        imported++;
                    }
                }
            }
            Debug.Log($"Ammo: {imported} created");
        }

        /// <summary>
        /// Create one <see cref="ItemDefinition"/> per <c>&lt;item&gt;</c> element, <b>including</b> the
        /// 49 ammunition rows.
        ///
        /// <para><b>Why ammo is no longer skipped.</b> This used to be
        /// <c>if (tipCode == "a") continue; // Skip ammo</c>, on the reasoning that an ammo row is
        /// already covered by the <c>AmmoDefinition</c> that <see cref="ImportAmmo"/> creates. That is
        /// only half true, and the missing half is load-bearing: an <see cref="AmmoDefinition"/> is a
        /// <i>ballistics</i> row (damage multiplier, piercing, wear), while <c>GameInventory</c> stores
        /// <b>counts</b> against <see cref="ItemDefinition"/> ids — <c>AddItem</c> keys on
        /// <c>itemDefinition.itemId</c> (:205) and <c>GetAmmoCount</c>/<c>ConsumeAmmo</c> look that same
        /// dictionary up by the weapon's resolved ammo id (:644-659). With no item row the weapon can
        /// never draw a round, so <c>p5</c>/<c>p5_1</c> — the minigun's regular and armour-piercing
        /// rounds — could be selected in the debug dropdown but never loaded.
        /// The 28 ammo ids that <i>did</i> have a row were exactly the <c>compw</c>/<c>stuff</c>
        /// component rows that happen to share a name with a round; every real <c>tip='a'</c> round was
        /// absent.</para>
        ///
        /// <para><b>Type and category are set here, not left to <see cref="FixDataImport"/>.</b> That
        /// pass already maps <c>tip='a'</c> to <see cref="ItemType.Ammo"/> and is idempotent, so it can
        /// still be run to repair assets created before this change — but a fresh import should not
        /// depend on a second menu item to be correct. <see cref="InventoryCategory.Ammo"/> is set
        /// alongside it so the inventory UI sorts the round into its own tab rather than Misc.</para>
        /// </summary>
        private static void ImportItems()
        {
            string outputPath = "Assets/_PFE/Data/Resources/Items";
            Directory.CreateDirectory(outputPath);

            string content = File.ReadAllText(SourceFilePath);

            // The row scan lives in the runtime assembly (`ItemXmlRows`) rather than here, so the
            // offline fixture exercises the same reader this importer runs. Two private regexes for
            // the same rows is how a fix to one silently misses the other.
            List<ItemXmlRow> rows = ItemXmlRows.Parse(content);

            int imported = 0, ammoImported = 0, repaired = 0;
            foreach (ItemXmlRow row in rows)
            {
                string id = row.Id;
                if (string.IsNullOrEmpty(id)) continue;

                // A row with no `tip` is skipped. This is the same set of rows the previous regex
                // matched — it required `tip=` — but stated rather than implied, so the skip is
                // visible. Measured: 0 of the 500 shipped rows lacks one.
                string tipCode = row.Tip;
                if (string.IsNullOrEmpty(tipCode)) continue;

                bool isAmmo = tipCode == "a";

                string assetPath = $"{outputPath}/{id}.asset";
                ItemDefinition existing = AssetDatabase.LoadAssetAtPath<ItemDefinition>(assetPath);

                if (existing == null)
                {
                    var item = ScriptableObject.CreateInstance<ItemDefinition>();
                    item.itemId = id;
                    ApplyLegacyTips(item, row);
                    ApplyTipDefaults(item, tipCode, row.Attrs);
                    AssetDatabase.CreateAsset(item, assetPath);

                    imported++;
                    if (isAmmo) ammoImported++;
                }
                else if (NeedsTipRepair(existing, tipCode) || NeedsLegacyTipRepair(existing, row))
                {
                    // Repair a row created before this tip was imported, without a second menu pass.
                    //
                    // **This is the path that actually fills the nine `sp_*` rows.** They already
                    // exist as `Misc` stubs from an earlier run, so `existing` is never null for them
                    // and the create branch above never runs. Without this branch the spell import
                    // would report success and change nothing — the same "reported success, wrote
                    // nothing" shape as the self-closing-`<weapon>` bug in `WeaponXmlBlocks`.
                    //
                    // **It is also the path that fills `legacyTip`/`tip2` on the ~500 rows that
                    // already exist.** Those two fields are new, so every pre-existing asset has them
                    // empty — and because `existing` is non-null for all of them, the create branch
                    // cannot reach them. Gating this branch on `NeedsTipRepair` alone would report a
                    // successful import while writing neither field: the same trap, twice.
                    ApplyLegacyTips(existing, row);
                    ApplyTipDefaults(existing, tipCode, row.Attrs);
                    EditorUtility.SetDirty(existing);
                    repaired++;
                }
            }
            Debug.Log($"Items: {imported} created ({ammoImported} of them ammunition), {repaired} repaired");
        }

        /// <summary>
        /// Copy the row's raw <c>@tip</c> / <c>@tip2</c> onto the definition.
        ///
        /// <para>Separate from <see cref="ApplyTipDefaults"/> because the two answer different
        /// questions: this records <b>what the source row said</b>, unconditionally, for every tip;
        /// that one stamps the derived enum and the type-specific block, and deliberately handles only
        /// the tips it understands. Folding them together would make the raw attributes as narrow as
        /// the enum mapping — the exact loss this field exists to prevent.</para>
        /// </summary>
        private static void ApplyLegacyTips(ItemDefinition item, ItemXmlRow row)
        {
            item.legacyTip = row.Tip;
            item.tip2 = row.Tip2;
        }

        /// <summary>
        /// Whether an existing row is missing its raw tip attributes, so the import can backfill them.
        ///
        /// <para><b>Why this is not folded into <see cref="NeedsTipRepair"/>.</b> That method is a
        /// <c>switch</c> over the tips this importer understands and returns <c>false</c> for every
        /// other one — by design, so a run does not rewrite all 500 assets. The raw attributes are not
        /// tip-specific: they apply to every row, so they need their own condition, and that condition
        /// is what makes the first run after this change backfill all 500 and every later run a
        /// no-op.</para>
        /// </summary>
        private static bool NeedsLegacyTipRepair(ItemDefinition item, ItemXmlRow row)
            => item.legacyTip != row.Tip || item.tip2 != row.Tip2;

        /// <summary>
        /// Whether an <b>existing</b> row is missing what its <c>tip</c> implies, so the import can
        /// repair it in place. Deliberately narrow: each tip names the one thing that must be true, and
        /// an unrecognised tip is never repaired — a blanket "always re-apply" would rewrite every one
        /// of the 500 assets on every run.
        /// </summary>
        private static bool NeedsTipRepair(ItemDefinition item, string tipCode)
        {
            switch (tipCode)
            {
                case "a":     return item.type != ItemType.Ammo;
                case "spell": return item.type != ItemType.Spell || !item.spellData.IsPopulated;
                default:      return false;
            }
        }

        /// <summary>
        /// Stamp the id-derived data a row must carry, so a fresh import needs neither
        /// <see cref="FixDataImport"/> nor a re-run to be usable.
        ///
        /// <para><b>Only the tips this importer understands are handled</b> — <c>a</c> and
        /// <c>spell</c>. Every other tip keeps the enum default, which
        /// <c>FixDataImport.GetItemTypeFromSource</c> refines afterwards. That split is deliberate:
        /// this method runs inside the asset-creating pass and must stay cheap and side-effect-free
        /// for the 491 rows it does not care about.</para>
        ///
        /// <para><b>Why <c>spell</c> is here at all.</b> AS3 casts a spell through
        /// <c>Spell.as</c> from the inventory (<c>UnitPlayer.as:3627-3635</c>), and <c>Spell</c>'s
        /// constructor reads twelve attributes off the <c>&lt;item&gt;</c> row (<c>Spell.as:83-131</c>).
        /// None of them had a home in the port, so all nine rows imported as empty <c>Misc</c> stubs.
        /// See <see cref="SpellItemXml"/> and <c>SpellData</c>.</para>
        /// </summary>
        private static void ApplyTipDefaults(ItemDefinition item, string tipCode, string tagContent)
        {
            if (tipCode == "a")
            {
                item.type = ItemType.Ammo;
                item.inventoryCategory = InventoryCategory.Ammo;
                return;
            }

            if (tipCode != "spell") return;

            item.type = ItemType.Spell;

            // AS3 `Item.as:371` — the base price is the raw `@price`; the three multipliers that turn
            // it into an asking price (`sost * multHP * pmult`) are applied at sale time and are not
            // part of the import.
            //
            // `sellPrice` is deliberately NOT written. AS3 reads a *separate* `@sell` attribute and
            // derives a ratio from it (`Item.as:378-380`: `sell / price`), and no spell row carries
            // `@sell` at all — so the port has nothing to read and inventing "half of price" would be a
            // fabricated rule. The field keeps its default until the item-table workstream lands a real
            // price model.
            item.basePrice = WeaponXmlAttrs.AttrI(tagContent, "price", item.basePrice);

            item.spellData = SpellItemXml.Read(tagContent);
        }

        private static void ImportPerks()
        {
            string outputPath = "Assets/_PFE/Data/Resources/Perks";
            Directory.CreateDirectory(outputPath);

            string content = File.ReadAllText(SourceFilePath);
            var matches = System.Text.RegularExpressions.Regex.Matches(content,
                "<perk\\s+([^>]*?)>(.*?)</perk>",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            int imported = 0;
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string tagContent = match.Groups[1].Value;
                string id = ExtractAttribute(tagContent, "id");

                if (!string.IsNullOrEmpty(id))
                {
                    string assetPath = $"{outputPath}/{id}.asset";
                    if (AssetDatabase.LoadAssetAtPath<PerkDefinition>(assetPath) == null)
                    {
                        var perk = ScriptableObject.CreateInstance<PerkDefinition>();
                        AssetDatabase.CreateAsset(perk, assetPath);
                        imported++;
                    }
                }
            }
            Debug.Log($"Perks: {imported} created");
        }

        private static string ExtractAttribute(string tag, string attributeName)
        {
            var match = System.Text.RegularExpressions.Regex.Match(tag,
                $"{attributeName}\\s*=\\s*['\"]([^'\"]*)['\"]");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
#endif
