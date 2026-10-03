#if UNITY_EDITOR
using System;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;
using System.IO;
using PFE.Data.Definitions;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports ammunition data from AllData.as XML format.
    /// Uses regex-based extraction to handle embedded ActionScript XML.
    /// </summary>
    public static class AmmoDefinitionImporter
    {
        private static string SourceFilePath => SourceImportPaths.AllDataAsPath;
        private static readonly string OutputPath = "Assets/_PFE/Data/Resources/Ammo";

        /// <summary>
        /// Parse and import all ammunition items from AllData.as
        ///
        /// <para><b>What counts as ammunition.</b> This used to select on <c>tip='a'</c> alone, which
        /// is not what AS3 does. <c>Weapon.getXmlParam</c> resolves the weapon's <c>&lt;a&gt;</c> id with
        /// <c>AllData.d.item.(@id == ammo)[0]</c> — no tip filter anywhere (<c>Weapon.as:560-562</c>) —
        /// and <c>Weapon.setAmmo</c> then reads its modifier attributes (<c>:1746-1809</c>). So any item
        /// row a weapon names is an ammo row, whatever its tip says. Seven weapons in this build depend
        /// on that: <c>acidgun→acid</c>, <c>arson→aero</c>, <c>dartgun→dart</c>, <c>railway→spikenail</c>
        /// and <c>buckshot→scrap</c> name <c>tip='compw'</c>/<c>tip='stuff'</c> rows — components that
        /// double as ammunition. Selecting on <c>tip='a'</c> dropped all of them, which is why those
        /// weapons point at ids with no <see cref="AmmoDefinition"/> to resolve.
        ///
        /// <para>The in-game inventory agrees with the union rather than with <c>tip='a'</c>:
        /// <c>PipPageVault.checkAmmo</c> (<c>:286</c>) explicitly lets <c>compw</c> through its ammo
        /// filter, and <c>Invent.getKolAmmos</c> (<c>:1562-1577</c>) totals the "ammo" a weapon reports
        /// by summing every item that carries a <c>base</c> — component variants included.</para>
        ///
        /// <para><b>Ignored noise.</b> Rows with <c>chance='0'</c> are excluded. That is not a scope
        /// reduction: <c>chance</c> is the loot weight LootGen draws against, so a zero-weight row is
        /// never placed in the world. Three rows carry it — <c>recharg</c> and <c>not</c> (both
        /// <c>invis='1'</c> sentinels) and <c>kogt</c>. The first two were already being skipped by
        /// name; this replaces that name list with the rule that actually justifies it, so nothing else
        /// with a zero weight can slip in.</para>
        /// </summary>
        [MenuItem("PFE/Data/Import Ammunition from AllData.as")]
        public static void ImportAmmunition()
        {
            Debug.Log($"Starting ammunition import from {SourceFilePath}");

            if (!File.Exists(SourceFilePath))
            {
                Debug.LogError($"Source file not found: {SourceFilePath}");
                return;
            }

            // Ensure output directory exists
            Directory.CreateDirectory(OutputPath);

            // Read the file with UTF-8 encoding
            string content;
            using (StreamReader reader = new StreamReader(SourceFilePath, System.Text.Encoding.UTF8))
            {
                content = reader.ReadToEnd();
            }

            // Select every <item> a weapon can name as its <a> — not just tip='a'. See the class
            // comment: AS3 resolves the weapon's ammo id with no tip filter, and seven weapons in this
            // build point at compw/stuff rows.
            string pattern = "<item\\s+([^>]*?)>";
            MatchCollection matches = Regex.Matches(content, pattern, RegexOptions.Singleline);

            Debug.Log($"Found {matches.Count} item rows to consider");

            int imported = 0;
            int skipped = 0;
            int notAmmo = 0;

            foreach (Match match in matches)
            {
                try
                {
                    string fullItemTag = $"<item {match.Groups[1].Value}>";

                    // Parse attributes from the item tag
                    string id = ExtractAttribute(fullItemTag, "id");

                    if (string.IsNullOrEmpty(id))
                    {
                        Debug.LogWarning($"Skipping item with no ID: {fullItemTag}");
                        skipped++;
                        continue;
                    }

                    // A zero loot weight means the row is never placed in the world, so it is not
                    // ammunition in any sense the game can reach. This is what `recharg` and `not`
                    // (invis='1' sentinels, chance='0') fail on, and it is a property of the data
                    // rather than a hard-coded name list.
                    string chanceValue = ExtractAttribute(fullItemTag, "chance");
                    if (float.TryParse(chanceValue, out float lootChance) && lootChance <= 0f)
                    {
                        Debug.Log($"Skipping zero-weight row: {id} (chance={chanceValue})");
                        skipped++;
                        continue;
                    }

                    // Rows that are neither ammunition nor components double as ammo for nothing.
                    // Restricting to the two tips AS3's ammo paths actually reach keeps the report
                    // honest about how many rows were considered and rejected.
                    string tip = ExtractAttribute(fullItemTag, "tip");
                    if (tip != "a" && tip != "compw" && tip != "stuff")
                    {
                        notAmmo++;
                        continue;
                    }

                    // Check if already exists
                    string assetPath = $"{OutputPath}/{id}.asset";
                    if (AssetDatabase.LoadAssetAtPath<AmmoDefinition>(assetPath) != null)
                    {
                        Debug.Log($"Skipping existing: {id}");
                        skipped++;
                        continue;
                    }

                    // Create the ammo asset
                    AmmoDefinition ammo = ScriptableObject.CreateInstance<AmmoDefinition>();
                    // Direct field assignment (fields are public)
                    ammo.ammoId = id;
                    ammo.displayName = id;

                    // Parse base attribute
                    string baseId = ExtractAttribute(fullItemTag, "base");
                    if (!string.IsNullOrEmpty(baseId))
                        ammo.baseId = baseId;

                    // Parse numeric attributes
                    ParseAndSetInt(fullItemTag, "kol", value => ammo.stackSize = value);
                    // `chance` is the loot weight used by LootGen, NOT a probability — the file runs
                    // 0.1 .. 1.2. AmmoDefinition.dropChance is [Range(0,1)] and `=> ammo.dropChance = v`
                    // goes through the RangeAttribute setter, so 1.2 was silently rewritten to 1 and
                    // eight rows (p9, p10, p32 among them) lost their relative weight. The widest
                    // value in AllData is 1.2, so the divisor is that: 1.2 -> 1.0, 0.6 -> 0.5.
                    // AS3 has no such field at all (LootGen reads the XML attribute directly), so
                    // this is a port-local normalisation and is documented as one.
                    ParseAndSetFloat(fullItemTag, "chance", value => ammo.dropChance = value / 1.2f);
                    ParseAndSetInt(fullItemTag, "stage", value => ammo.requiredStoryStage = value);
                    ParseAndSetInt(fullItemTag, "lvl", value => ammo.requiredLevel = value);
                    ParseAndSetInt(fullItemTag, "price", value => ammo.basePrice = value);
                    ParseAndSetInt(fullItemTag, "sell", value => ammo.sellPrice = value);
                    ParseAndSetFloat(fullItemTag, "m", value => ammo.weight = value);

                    // Variant modifiers
                    string modValue = ExtractAttribute(fullItemTag, "mod");
                    if (!string.IsNullOrEmpty(modValue) && int.TryParse(modValue, out int mod))
                        ammo.modifier = (AmmoModifier)mod;

                    // ── The nine Weapon.setAmmo() fields ─────────────────────────────────────
                    // AS3 Weapon.setAmmo (Weapon.as:1746-1809) copies the ammo row's worth of
                    // <dop> attributes onto the weapon as ammoPier / ammoArmor / ammoDamage /
                    // ammoProbiv / ammoOtbros / ammoHP / ammoFire / ammoMod. Four of those slots
                    // (probiv, det, tipdam, and the `mod` variant written above) were never read
                    // here, so the port's weapon had no penetration budget, no durability cost and
                    // no damage-type override to consume.
                    //
                    // Every one of these attributes appears on tip='a' rows ONLY — verified against
                    // the file. The 28 compw/stuff rows selected above carry no modifiers at all;
                    // they exist so the five component-fed weapons have a resolvable ammo row, and
                    // in AS3 they land on setAmmo's all-defaults branch. Their absence here is
                    // therefore correct, not an omission.
                    ParseAndSetInt(fullItemTag, "pier", value => ammo.armorPiercingBonus = value);   // ammoPier
                    ParseAndSetFloat(fullItemTag, "damage", value => ammo.damageMultiplier = value); // ammoDamage
                    ParseAndSetFloat(fullItemTag, "armor", value => ammo.armorMultiplier = value);   // ammoArmor
                    ParseAndSetFloat(fullItemTag, "knock", value => ammo.knockbackMultiplier = value);// ammoOtbros
                    ParseAndSetInt(fullItemTag, "fire", value => ammo.fireDamage = value);           // ammoFire

                    // prec — the round's accuracy MULTIPLIER (Weapon.as:1675
                    // `param1.precision = this.precision * this.ammoPrec`). This slot was the one the
                    // nine-attribute comment above omitted: the field existed but nothing ever wrote it,
                    // so it stayed at its 1f default. No tip='a' row currently carries `prec` (verified
                    // against AllData.as — 0 of 49), so this changes no shipped value; it is added so a
                    // mod that does carry one is not silently ignored. That is the same inert-field
                    // shape this importer's comment block exists to prevent.
                    ParseAndSetFloat(fullItemTag, "prec", value => ammo.precisionMultiplier = value);  // ammoPrec

                    // probiv — the penetration BUDGET (Weapon.as:1681 `probiv = this.probiv + ammoProbiv`).
                    // Distinct from `pier` above: pier is flat armour points, probiv is the damage
                    // budget the bullet spends walking through a target. Seven rows carry it.
                    ParseAndSetFloat(fullItemTag, "probiv", value => ammo.penetrationBudget = value);

                    // det — extra weapon durability burned per shot (Weapon.as:1598 `hp -= 1 + ammoHP`).
                    // AmmoDefinition models this as a bool but AS3 stores an int, and no non-zero
                    // value other than '1' occurs; the only three rows are batt_6, energ_6, crystal_6.
                    // The three energy variants keep '1'; the three ballistic/energy BASE rows omit
                    // it. Mapping !=0 to true preserves exactly that split.
                    ParseAndSetInt(fullItemTag, "det", value => ammo.extraDurabilityCost = value != 0);

                    // tipdam — damage-type override (Weapon.as:1806-1809 -> `param1.tipDamage = ammoMod`,
                    // which is a DamageType). AmmoDefinition.damageTypeOverride is non-nullable and
                    // defaults to PhysicalBullet, so only write it when the row actually carries one.
                    string tipdamValue = ExtractAttribute(fullItemTag, "tipdam");
                    if (!string.IsNullOrEmpty(tipdamValue) && int.TryParse(tipdamValue, out int tipdam))
                        ammo.damageTypeOverride = (DamageType)tipdam;

                    // Save the asset
                    AssetDatabase.CreateAsset(ammo, assetPath);
                    imported++;

                    Debug.Log($"Imported: {id}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error importing item: {e.Message}\n{e.StackTrace}");
                    skipped++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Ammunition import complete. Imported: {imported}, Skipped: {skipped}, " +
                      $"Not ammo (other tip): {notAmmo}");
        }

        /// <summary>
        /// Extract an attribute value from an XML tag string
        /// </summary>
        private static string ExtractAttribute(string tag, string attributeName)
        {
            // Match attribute='value' or attribute="value"
            var match = Regex.Match(tag, $"{attributeName}\\s*=\\s*['\"]([^'\"]*)['\"]");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Parse an integer attribute and invoke setter if found
        /// </summary>
        private static void ParseAndSetInt(string tag, string attributeName, System.Action<int> setter)
        {
            string value = ExtractAttribute(tag, attributeName);
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out int intValue))
            {
                setter(intValue);
            }
        }

        /// <summary>
        /// Parse a float attribute and invoke setter if found
        /// </summary>
        private static void ParseAndSetFloat(string tag, string attributeName, System.Action<float> setter)
        {
            string value = ExtractAttribute(tag, attributeName);
            if (!string.IsNullOrEmpty(value) && float.TryParse(value, out float floatValue))
            {
                setter(floatValue);
            }
        }
    }
}
#endif
