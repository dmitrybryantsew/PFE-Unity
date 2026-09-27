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
        private static string AllDataPath => SourceImportPaths.AllDataAsPath;
        private static readonly string OutputPath = "Assets/_PFE/Data/Resources/Units";

        [MenuItem("PFE/Data/Import Units from AllData.as", priority = 20)]
        public static void ImportUnits() => Run(overwriteExisting: false);

        /// <summary>
        /// Refresh every existing unit asset in place from <c>AllData.as</c>.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="ImportUnits"/> because the two have different blast radii, and the
        /// old single entry point was create-only — so a fix to this importer could never reach an asset
        /// that already existed. That is not a hypothetical: re-running the plain import after the
        /// faction fix reported <c>Imported: 0, Skipped: 148</c> and looked like success while changing
        /// nothing.
        ///
        /// <para>Updates in place (<see cref="AssetDatabase.LoadAssetAtPath{T}"/> + <c>SetDirty</c>)
        /// rather than delete-and-recreate, so the asset keeps its GUID. Nothing references these by
        /// GUID today — units load through <c>Resources.LoadAll</c> at boot — but recreating them would
        /// make that a trap for whoever adds the first prefab reference.</para>
        ///
        /// <para>Mirrors the convention already in this repo: <c>WeaponDataImporter</c> load-or-creates,
        /// and <c>RoomTemplateImporterWindow</c> gates the same decision behind an explicit
        /// overwrite flag. The flag is the menu entry here, plus a confirmation, because this path
        /// discards manual edits.</para>
        /// </remarks>
        [MenuItem("PFE/Data/Reimport Units (Overwrite Existing)", priority = 21)]
        public static void ReimportUnits()
        {
            int existing = 0;
            if (Directory.Exists(OutputPath))
                existing = Directory.GetFiles(OutputPath, "*.asset").Length;

            bool proceed = EditorUtility.DisplayDialog(
                "Reimport units?",
                $"This rewrites {existing} unit asset(s) in\n{OutputPath}\n\n" +
                "Every field is re-derived from AllData.as, so any manual edit to those assets is lost. " +
                "The assets are git-tracked, so the change is recoverable.",
                "Reimport", "Cancel");

            if (!proceed) return;

            Run(overwriteExisting: true);
        }

        private static void Run(bool overwriteExisting)
        {
            int imported = 0;
            int updated  = 0;
            int skipped  = 0;
            int unresolved = 0;

            if (!File.Exists(AllDataPath))
            {
                Debug.LogError(SourceImportPaths.MissingSourceMessage(AllDataPath, "AllData.as"));
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

            // ── Pass 1: every explicit `fraction` and `parent`, keyed by unit id ─────────────────
            //
            // Needed because `fraction` is INHERITED, not local. Only 78 of the 148 units carry the
            // attribute; the other 70 declare `parent='raider'` and take the template's value —
            // `raider1..9` are the clearest case, and `zombie`/`bloat`/`ant` children inherit 1 while
            // `encl`/`ranger`/`hellhound` children inherit 4. `Unit.as:454` defaults the field to 0,
            // so without this walk a spawnable unit silently imports as Neutral instead of as its
            // faction — and because the old C# default happened to be 2, which is what `raider` and
            // `slaver` carry, the omission looked correct for the majority and wrong only for the
            // monsters and robots.
            var explicitFraction = new Dictionary<string, int>();
            var explicitParent   = new Dictionary<string, string>();

            foreach (Match m in matches)
            {
                string uid   = m.Groups[1].Value;
                string attrs = m.Groups[2].Value;

                var fm = Regex.Match(attrs, @"fraction='(\d+)'");
                if (fm.Success) explicitFraction[uid] = int.Parse(fm.Groups[1].Value);

                var pm = Regex.Match(attrs, @"parent='([^']+)'");
                if (pm.Success) explicitParent[uid] = pm.Groups[1].Value;
            }

            // Resolve a unit's faction through the `parent` chain. Depth-capped so a malformed cycle
            // cannot hang the importer, and returning null when the chain carries no explicit value,
            // which leaves the unit on `UnitDefinition`'s own default (AS3's 0 = Neutral).
            int? ResolveFraction(string unitId)
            {
                string current = unitId;
                for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(current); depth++)
                {
                    if (explicitFraction.TryGetValue(current, out int value)) return value;
                    if (!explicitParent.TryGetValue(current, out string next)) return null;
                    current = next;
                }
                return null;
            }

            Debug.Log($"Found {matches.Count} unit definitions in AllData.as");

            foreach (Match match in matches)
            {
                string id = match.Groups[1].Value;
                string attributesStr = match.Groups[2].Value;

                string assetPath = $"{OutputPath}/{id}.asset";
                bool exists = File.Exists(assetPath);

                if (exists && !overwriteExisting)
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

                    // Load the existing asset when refreshing so its GUID survives; create otherwise.
                    UnitDefinition unit = exists
                        ? AssetDatabase.LoadAssetAtPath<UnitDefinition>(assetPath)
                        : ScriptableObject.CreateInstance<UnitDefinition>();

                    if (unit == null)
                    {
                        Debug.LogWarning($"Could not load or create a UnitDefinition for '{id}' ({assetPath})");
                        skipped++;
                        continue;
                    }

                    // Parse basic attributes
                    SetPrivateField(unit, "id", id);

                    // Parse fraction (faction) — the unit's own attribute, else its `parent` chain.
                    //
                    // `\d+`, not `\d` — AS3's F_PLAYER is 100 (Unit.as:78), and the single-digit form
                    // silently failed to match `fraction='100'`. The five units that carry it (the
                    // player's allies: npc, vendor, doctor, captive, ponpon) therefore kept the C#
                    // field default instead of importing. A regex that cannot express the value it is
                    // reading is a silent data loss, not a parse error.
                    int? resolvedFraction = ResolveFraction(id);
                    if (resolvedFraction.HasValue)
                    {
                        SetPrivateField(unit, "fraction", (FactionType)resolvedFraction.Value);
                    }
                    else
                    {
                        // The source names no faction anywhere in this unit's chain, so leave the field
                        // alone rather than inventing one — but COUNT it, because a silent no-op is how
                        // this importer's problems stayed hidden. 16 units are in this state
                        // (littlepip, pony, monster, alicorn, bloat7/8/9, other, robot, bigrobot,
                        // smallrobot, turret, mtrap, mwall, scythe, destr1) and they are templates whose
                        // faction AS3 assigns in the CONTROLLER class, not in data:
                        // `UnitPlayer.as:385` and `UnitTrap.as:32,73` and `UnitTurret.as:401,410` all
                        // set `F_PLAYER`; `UnitZombie.as:502` sets `F_MONSTER`. Rooms also override it
                        // per placed instance — `RoomsCamp.as:86` `<obj id="turret" … fraction="100"/>`.
                        //
                        // So there is no value this importer can honestly write. Writing AS3's
                        // field default (0) would be wrong for the traps and turrets, which are the
                        // player's. This is left to a deliberate decision, not a guess: see the
                        // 2026-09-27 note in .workbuddy-ai/memory/TOPIC_faction_and_friendly_fire.md.
                        unresolved++;
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

                    // Defaults FIRST, then the XML on top of them.
                    //
                    // Order is load-bearing, not cosmetic: SetUnitDefaults writes `dexterity = 1` and
                    // `skill = 1`, and ParseCombat reads those same two fields from the `<comb>` tag —
                    // 71 units carry `dexter=` (values up to 100) and 34 carry `skill=`. With the
                    // defaults last, every one of those was overwritten with 1: `dexter='100'` imported
                    // as 1, a 100x error on a combat stat. A default means "the value to use when the
                    // source is silent", so it cannot run after the source has spoken.
                    SetUnitDefaults(unit);

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

                    if (exists)
                    {
                        // In place: the fields were written straight onto the loaded instance.
                        EditorUtility.SetDirty(unit);
                        updated++;
                    }
                    else
                    {
                        AssetDatabase.CreateAsset(unit, assetPath);
                        imported++;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Failed to import unit {id}: {e.Message}\n{e.StackTrace}");
                    skipped++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Say plainly when nothing happened. The old message read "Imported: 0, Skipped: 148" and
            // looked like a success, which is how a create-only importer stayed invisible: the run
            // reported "complete" while changing nothing, and the only clue was a skip count.
            string note = (imported == 0 && updated == 0 && skipped > 0)
                ? $"  Nothing was written — all {skipped} definition(s) already have an asset. " +
                  "Run PFE/Data/Reimport Units (Overwrite Existing) to refresh them from AllData.as."
                : string.Empty;

            // Surface the units the source is silent about, so the gap is visible in the log rather
            // than discoverable only by diffing assets.
            string factionNote = unresolved > 0
                ? $"  {unresolved} unit(s) declare no faction anywhere in their parent chain and were " +
                  "left as they were — AS3 sets those in the controller class (traps and turrets are the " +
                  "player's), so the source has no value to import."
                : string.Empty;

            Debug.Log($"Unit import complete. Imported: {imported}  Updated: {updated}  " +
                      $"Skipped: {skipped}  Faction-unspecified: {unresolved}{note}{factionNote}");
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
