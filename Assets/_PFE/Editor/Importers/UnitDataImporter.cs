using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PFE.Data.Definitions;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports UnitDefinition assets from AllData.as ActionScript file.
    /// Parses <unit> tags and nested elements (phis, move, comb, vis, etc.)
    /// </summary>
    public class UnitDataImporter
    {
        private static readonly string AllDataPath = SourceImportPaths.AllDataAsPath;
        private static readonly string OutputPath = "Assets/_PFE/Data/Resources/Units";

        [MenuItem("PFE/Data/Import Units from AllData.as")]
        public static void ImportUnits()
        {
            int imported = 0;
            int skipped = 0;

            if (!File.Exists(AllDataPath))
            {
                Debug.LogError($"AllData.as not found at: {AllDataPath}");
                return;
            }

            // Ensure output directory exists
            if (!Directory.Exists(OutputPath))
            {
                Directory.CreateDirectory(OutputPath);
            }

            string content = File.ReadAllText(AllDataPath);

            // Find all unit definitions
            // Pattern: <unit id='...' [attributes]>
            var pattern = @"<unit\s+id='([^']+)'([^>]*)(?:\s*/)?>";
            var matches = Regex.Matches(content, pattern);

            Debug.Log($"Found {matches.Count} unit definitions in AllData.as");

            foreach (Match match in matches)
            {
                string id = match.Groups[1].Value;
                string attributesStr = match.Groups[2].Value;

                // Skip if already exists
                string assetPath = $"{OutputPath}/{id}.asset";
                if (File.Exists(assetPath))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    // Get the full unit content (from <unit> to </unit>)
                    int unitStart = match.Index;
                    int unitEnd = content.IndexOf("</unit>", unitStart);
                    if (unitEnd == -1)
                    {
                        // Self-closing tag or find next <unit>
                        int nextUnit = content.IndexOf("<unit", unitStart + 1);
                        unitEnd = nextUnit != -1 ? nextUnit : content.Length;
                    }
                    else
                    {
                        unitEnd += "</unit>".Length;
                    }

                    string unitContent = content.Substring(unitStart, unitEnd - unitStart);

                    // Create new unit definition
                    UnitDefinition unit = ScriptableObject.CreateInstance<UnitDefinition>();

                    // Parse basic attributes
                    SetPrivateField(unit, "id", id);

                    // Parse fraction (faction)
                    var fractionMatch = Regex.Match(attributesStr, @"fraction='(\d)'");
                    if (fractionMatch.Success)
                    {
                        int fraction = int.Parse(fractionMatch.Groups[1].Value);
                        SetPrivateField(unit, "fraction", (FactionType)fraction);
                    }

                    // Parse category
                    var catMatch = Regex.Match(attributesStr, @"cat='(\d)'");
                    if (catMatch.Success)
                    {
                        int cat = int.Parse(catMatch.Groups[1].Value);
                        SetPrivateField(unit, "category", (UnitCategory)cat);
                    }

                    // Parse parent
                    var parentMatch = Regex.Match(attributesStr, @"parent='([^']+)'");
                    if (parentMatch.Success)
                    {
                        SetPrivateField(unit, "parentId", parentMatch.Groups[1].Value);
                    }

                    // Parse controller (cont attribute)
                    var contMatch = Regex.Match(attributesStr, @"cont='([^']+)'");
                    if (contMatch.Success)
                    {
                        SetPrivateField(unit, "controllerId", contMatch.Groups[1].Value);
                    }

                    // Parse XP reward
                    var xpMatch = Regex.Match(attributesStr, @"xp='(\d+)'");
                    if (xpMatch.Success)
                    {
                        int xp = int.Parse(xpMatch.Groups[1].Value);
                        SetPrivateField(unit, "xpReward", xp);
                    }

                    // Parse display name from <n> tag
                    var nameMatch = Regex.Match(unitContent, @"<n>\s*([^<]+)\s*</n>");
                    if (nameMatch.Success)
                    {
                        SetPrivateField(unit, "displayName", nameMatch.Groups[1].Value.Trim());
                    }
                    else
                    {
                        SetPrivateField(unit, "displayName", id);
                    }

                    // Parse physics (<phis> tag)
                    ParsePhysics(unit, unitContent);

                    // Parse movement (<move> tag)
                    ParseMovement(unit, unitContent);

                    // Parse combat (<comb> tag)
                    ParseCombat(unit, unitContent);

                    // Parse vulnerabilities (<vuln> tag)
                    ParseVulnerabilities(unit, unitContent);

                    // Parse vision (<vis> tag)
                    ParseVision(unit, unitContent);

                    // Parse weapons (<w> tags)
                    ParseWeapons(unit, unitContent);

                    // Set defaults for other fields
                    SetUnitDefaults(unit);

                    // Create asset
                    AssetDatabase.CreateAsset(unit, assetPath);
                    imported++;

                    Debug.Log($"Imported unit: {id}");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Failed to import unit {id}: {e.Message}\n{e.StackTrace}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Unit import complete. Imported: {imported}, Skipped: {skipped}");
        }

        private static void ParsePhysics(UnitDefinition unit, string content)
        {
            var phisMatch = Regex.Match(content, @"<phis\s+([^>]*)/>");
            if (!phisMatch.Success) return;

            string attrs = phisMatch.Groups[1].Value;

            // Parse sX (width) - convert from pixels (55px = 0.55 units)
            var sxMatch = Regex.Match(attrs, @"sX='(\d+)'");
            if (sxMatch.Success)
            {
                float width = int.Parse(sxMatch.Groups[1].Value) / 100f;
                SetPrivateField(unit, "width", width);
            }

            // Parse sY (height)
            var syMatch = Regex.Match(attrs, @"sY='(\d+)'");
            if (syMatch.Success)
            {
                float height = int.Parse(syMatch.Groups[1].Value) / 100f;
                SetPrivateField(unit, "height", height);
            }

            // Parse massa (mass)
            var massaMatch = Regex.Match(attrs, @"massa='(\d+)'");
            if (massaMatch.Success)
            {
                float mass = float.Parse(massaMatch.Groups[1].Value);
                SetPrivateField(unit, "mass", mass);
            }
        }

        private static void ParseMovement(UnitDefinition unit, string content)
        {
            var moveMatch = Regex.Match(content, @"<move\s+([^>]*)/>");
            if (!moveMatch.Success) return;

            string attrs = moveMatch.Groups[1].Value;

            // Parse speed
            var speedMatch = Regex.Match(attrs, @"speed='(-?\d+\.?\d*)'");
            if (speedMatch.Success)
            {
                float speed = float.Parse(speedMatch.Groups[1].Value);
                SetPrivateField(unit, "moveSpeed", speed);
            }

            // Parse jump
            var jumpMatch = Regex.Match(attrs, @"jump='(-?\d+\.?\d*)'");
            if (jumpMatch.Success)
            {
                float jump = float.Parse(jumpMatch.Groups[1].Value);
                SetPrivateField(unit, "jumpForce", jump);
            }

            // Parse accel
            var accelMatch = Regex.Match(attrs, @"accel='(-?\d+\.?\d*)'");
            if (accelMatch.Success)
            {
                float accel = float.Parse(accelMatch.Groups[1].Value);
                SetPrivateField(unit, "acceleration", accel);
            }
        }

        private static void ParseCombat(UnitDefinition unit, string content)
        {
            var combMatch = Regex.Match(content, @"<comb\s+([^>]*)/>");
            if (!combMatch.Success) return;

            string attrs = combMatch.Groups[1].Value;

            // Parse hp (health)
            var hpMatch = Regex.Match(attrs, @"hp='(\d+)'");
            if (hpMatch.Success)
            {
                int hp = int.Parse(hpMatch.Groups[1].Value);
                SetPrivateField(unit, "health", hp);
            }

            // Parse armor (must be parsed before krep to avoid overwrite)
            var armorMatch = Regex.Match(attrs, @"armor='(\d+)'");
            if (armorMatch.Success)
            {
                int armor = int.Parse(armorMatch.Groups[1].Value);
                SetPrivateField(unit, "armor", armor);
            }

            // Parse marmor (magic armor)
            var marmorMatch = Regex.Match(attrs, @"marmor='(\d+)'");
            if (marmorMatch.Success)
            {
                int marmor = int.Parse(marmorMatch.Groups[1].Value);
                SetPrivateField(unit, "magicArmor", marmor);
            }

            // Parse armorhp (armor health)
            var armorhpMatch = Regex.Match(attrs, @"armorhp='(\d+)'");
            if (armorhpMatch.Success)
            {
                int armorhp = int.Parse(armorhpMatch.Groups[1].Value);
                SetPrivateField(unit, "armorHealth", armorhp);
            }

            // Parse krep (isStable - NOT armor!)
            var krepMatch = Regex.Match(attrs, @"krep='(\d+)'");
            if (krepMatch.Success)
            {
                int krep = int.Parse(krepMatch.Groups[1].Value);
                bool isStable = krep == 1;
                SetPrivateField(unit, "isStable", isStable);
            }

            // Parse damage
            var dmgMatch = Regex.Match(attrs, @"damage='(\d+)'");
            if (dmgMatch.Success)
            {
                int damage = int.Parse(dmgMatch.Groups[1].Value);
                SetPrivateField(unit, "damage", damage);
            }

            // Parse skill
            var skillMatch = Regex.Match(attrs, @"skill='(-?\d+\.?\d*)'");
            if (skillMatch.Success)
            {
                float skill = float.Parse(skillMatch.Groups[1].Value);
                SetPrivateField(unit, "skill", skill);
            }

            // Parse aqual (water ability)
            var aqualMatch = Regex.Match(attrs, @"aqual='(-?\d+\.?\d*)'");
            if (aqualMatch.Success)
            {
                float aqual = float.Parse(aqualMatch.Groups[1].Value);
                SetPrivateField(unit, "waterAbility", aqual);
            }

            // Parse dexter (dexterity)
            var dexterMatch = Regex.Match(attrs, @"dexter='(-?\d+\.?\d*)'");
            if (dexterMatch.Success)
            {
                float dexter = float.Parse(dexterMatch.Groups[1].Value);
                SetPrivateField(unit, "dexterity", dexter);
            }

            // Parse obs (observation range)
            var obsMatch = Regex.Match(attrs, @"obs='(\d+)'");
            if (obsMatch.Success)
            {
                int obs = int.Parse(obsMatch.Groups[1].Value);
                SetPrivateField(unit, "observationRange", obs);
            }
        }

        private static void ParseVulnerabilities(UnitDefinition unit, string content)
        {
            var vulnMatch = Regex.Match(content, @"<vuln\s+([^>]*)/>");
            if (!vulnMatch.Success) return;

            string attrs = vulnMatch.Groups[1].Value;

            // Create default vulnerabilities
            var vuln = new VulnerabilityData(1f);

            // Parse damage type multipliers (e.g., bullet='0.8', fire='1.5')
            var vulnPattern = @"(\w+)='(-?\d+\.?\d*)'";
            var vulnMatches = Regex.Matches(attrs, vulnPattern);

            foreach (Match vulnValueMatch in vulnMatches)
            {
                string type = vulnValueMatch.Groups[1].Value;
                float value = float.Parse(vulnValueMatch.Groups[2].Value);

                // Map to DamageType enum
                DamageType damageType = MapStringToDamageType(type);
                vuln.SetVulnerability(damageType, value);
            }

            SetPrivateField(unit, "vulnerabilities", vuln);
        }

        private static DamageType MapStringToDamageType(string type)
        {
            switch (type.ToLower())
            {
                case "bullet": return DamageType.PhysicalBullet;
                case "blade": return DamageType.Blade;
                case "phis": return DamageType.PhysicalMelee;
                case "fire": return DamageType.Fire;
                case "expl": return DamageType.Explosive;
                case "laser": return DamageType.Laser;
                case "plasma": return DamageType.Plasma;
                case "spark": return DamageType.Spark;
                case "acid": return DamageType.Acid;
                case "venom": return DamageType.Venom;
                case "poison": return DamageType.Poison;
                case "bleed": return DamageType.Bleed;
                case "fang": return DamageType.Fang;
                case "emp": return DamageType.EMP;
                case "pink": return DamageType.Pink;
                case "necro": return DamageType.Necrotic;
                default: return DamageType.PhysicalMelee;
            }
        }

        private static void ParseVision(UnitDefinition unit, string content)
        {
            var visMatch = Regex.Match(content, @"<vis\s+([^>]*)/>");
            if (!visMatch.Success) return;

            string attrs = visMatch.Groups[1].Value;

            // Parse blit (sprite ID)
            var blitMatch = Regex.Match(attrs, @"blit='([^']+)'");
            if (blitMatch.Success)
            {
                // Would load sprite from resources
                // For now, just store the ID
            }

            // Parse sprX (sprite width/height)
            var sprXMatch = Regex.Match(attrs, @"sprX='(\d+)'");
            if (sprXMatch.Success)
            {
                int sprX = int.Parse(sprXMatch.Groups[1].Value);
                var dims = new Vector2Int(sprX, sprX);
                SetPrivateField(unit, "spriteDimensions", dims);
            }

            // Parse sex (gender)
            var sexMatch = Regex.Match(attrs, @"sex='(\w)'");
            if (sexMatch.Success)
            {
                string sex = sexMatch.Groups[1].Value;
                Gender gender = sex == "m" ? Gender.Male : (sex == "w" ? Gender.Female : Gender.Other);
                SetPrivateField(unit, "gender", gender);
            }
        }

        private static void ParseWeapons(UnitDefinition unit, string content)
        {
            var weapons = new List<WeaponChance>();

            // Parse all <w> tags (weapons)
            var weaponPattern = @"<w\s+id='([^']+)'([^>]*)/>";
            var weaponMatches = Regex.Matches(content, weaponPattern);

            foreach (Match weaponMatch in weaponMatches)
            {
                string weaponId = weaponMatch.Groups[1].Value;
                string weaponAttrs = weaponMatch.Groups[2].Value;

                float chance = 1f;
                int difficulty = 0;

                // Parse ch (chance)
                var chMatch = Regex.Match(weaponAttrs, @"ch='(-?\d+\.?\d*)'");
                if (chMatch.Success)
                {
                    chance = float.Parse(chMatch.Groups[1].Value);
                }

                // Parse dif (difficulty)
                var difMatch = Regex.Match(weaponAttrs, @"dif='(\d+)'");
                if (difMatch.Success)
                {
                    difficulty = int.Parse(difMatch.Groups[1].Value);
                }

                weapons.Add(new WeaponChance(weaponId, chance, difficulty));
            }

            SetPrivateField(unit, "weapons", weapons.ToArray());
        }

        private static void SetUnitDefaults(UnitDefinition unit)
        {
            // Set sensible defaults for fields not in XML
            SetPrivateField(unit, "sitHeight", 0.5f);
            SetPrivateField(unit, "runMultiplier", 2f);
            SetPrivateField(unit, "braking", 0.5f);
            SetPrivateField(unit, "canSwim", true);
            SetPrivateField(unit, "canLevitate", false);
            SetPrivateField(unit, "canBeKnockedDown", true);
            SetPrivateField(unit, "isFixed", false);
            SetPrivateField(unit, "bloodType", BloodType.Red);
            SetPrivateField(unit, "leavesCorpse", true);
            SetPrivateField(unit, "isInvulnerable", false);
            SetPrivateField(unit, "canActivateTraps", true);
            SetPrivateField(unit, "damageType", DamageType.PhysicalMelee);
            SetPrivateField(unit, "dexterity", 1f);
            SetPrivateField(unit, "skill", 1f);
            SetPrivateField(unit, "noiseLevel", 300);
            SetPrivateField(unit, "detectionDistance", 400);
            SetPrivateField(unit, "actionPoints", 0);
        }

        private static void SetPrivateField(object obj, string fieldName, object value)
        {
            var field = obj.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public);

            if (field != null)
            {
                field.SetValue(obj, value);
            }
            else
            {
                Debug.LogWarning($"Field {fieldName} not found on {obj.GetType().Name}");
            }
        }
    }
}
