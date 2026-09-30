using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Globalization;
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

            // Animation coverage, reported at the end. A unit whose rows silently failed to parse
            // would otherwise look identical to a unit the source gives no animations for — which is
            // the normal state of the 24 vclass units, so the two must be distinguishable in the log.
            int animUnits = 0;
            int animRows = 0;
            int animOverrides = 0;
            int animRowsWithoutId = 0;
            var animUnmapped = new HashSet<string>();
            var animMissingFamily = new List<string>();

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

            // ── Pass 1 also captures each unit's node text ──────────────────────────────────────
            //
            // Needed for the animation family/variant join. The oracle splits a unit's animations
            // across two nodes: the family (`raider`) declares the <blit> rows and no sheet, the
            // variant (`raider5`) declares the sheet and, usually, no rows. The controller joins them
            // with a double call — UnitAlicorn.as:233-234 `super.getXmlParam("alicorn"); super.getXmlParam();`
            // — and the family id is always the unit's own `parent='…'` attribute.
            //
            // So resolving the join needs the *family's node text*, which means this has to be
            // collected before the per-unit loop runs. Collecting it here is free: the loop below
            // already walks every match.
            var nodeText = new Dictionary<string, string>();

            var duplicateUnitIds = new List<string>();

            foreach (Match m in matches)
            {
                string uid   = m.Groups[1].Value;
                string attrs = m.Groups[2].Value;

                var fm = Regex.Match(attrs, @"fraction='(\d+)'");
                if (fm.Success) explicitFraction[uid] = int.Parse(fm.Groups[1].Value);

                var pm = Regex.Match(attrs, @"parent='([^']+)'");
                if (pm.Success) explicitParent[uid] = pm.Groups[1].Value;
            }

            // Node text comes from the shared extractor rather than a local scan for the next
            // `</unit>`. See UnitNodeExtractor's remarks: seven self-closing marker units in AllData.as
            // (pony, monster, other, robot, bigrobot, smallrobot, turret) have no body, and a naive
            // forward scan made each one inherit the FOLLOWING unit's physics, combat, vis and
            // animations. Each of those ids appears exactly once, so nothing overwrote the damage.
            nodeText = UnitNodeExtractor.ExtractAllNodes(content, duplicateUnitIds);

            if (duplicateUnitIds.Count > 0)
            {
                // AS3's `AllData.d.unit.(@id == mid)[0]` takes the FIRST node with an id, so a
                // duplicate means this importer and the game would disagree about which one wins.
                Debug.LogWarning(
                    $"AllData.as declares {duplicateUnitIds.Count} duplicate unit id(s): " +
                    $"{string.Join(", ", duplicateUnitIds)}. AS3 reads the first; this importer keeps " +
                    "the last. The definitions are ambiguous until the source is fixed.");
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
                    // Get the full unit content (from <unit> to </unit>), or just the tag when it is
                    // self-closing — see UnitNodeExtractor's remarks for why that distinction is
                    // load-bearing rather than cosmetic.
                    string unitContent = UnitNodeExtractor.ExtractNode(content, match.Index, match.Value);

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

                    // Parse vulnerabilities (<vulner> tag)
                    ParseVulnerabilities(unit, unitContent);

                    // Parse vision (<vis> tag)
                    ParseVision(unit, unitContent);

                    // Parse animations (<blit> rows), joined with the unit's family node.
                    //
                    // After ParseVision on purpose: the family node is the unit's `parent`, which is
                    // resolved from data rather than from the controller class (the two coincide in
                    // every case — see AnimationSet's remarks). `parent` is only read here, never
                    // written, so ordering against ParseVision is not load-bearing — but keeping the
                    // visual tags adjacent is.
                    string familyId = explicitParent.TryGetValue(id, out string parentId) ? parentId : null;
                    string familyContent = null;
                    if (familyId != null && !nodeText.TryGetValue(familyId, out familyContent))
                    {
                        // A parent that is not a unit in this file. AS3 would throw on the lookup; here
                        // it just means no family pass, which leaves the unit with its own rows only.
                        animMissingFamily.Add($"{id}->{familyId}");
                    }

                    var anim = UnitAnimationParser.Parse(unitContent, familyContent);
                    SetPrivateField(unit, "animations", anim.Animations);

                    if (anim.HasAnyState) animUnits++;
                    animRows += anim.RowsRead;
                    animOverrides += anim.RowsOverridingTemplate;
                    foreach (string unmapped in anim.UnmappedIds) animUnmapped.Add(unmapped);
                    if (anim.RowsWithoutId > 0) animRowsWithoutId += anim.RowsWithoutId;

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

            // Animation coverage. Reported separately from the faction note because the two failure
            // modes look the same from outside: a unit with no animations is CORRECT for the 24
            // vclass units (they are drawn as DisplayObjects), so "0 rows" alone proves nothing. The
            // numbers below are what makes a silent parse failure distinguishable from a real absence.
            string animNote = animRows == 0
                ? "  No <blit> rows were found anywhere — the animation channel imported nothing."
                : $"  Animations: {animUnits} unit(s) with states, {animRows} row(s) read, " +
                  $"{animOverrides} overriding a family row.";

            string unmappedNote = animUnmapped.Count > 0
                ? $"  States with no field in AnimationSet (dropped, not mapped): " +
                  $"{string.Join(", ", animUnmapped)}."
                : string.Empty;

            string noIdNote = animRowsWithoutId > 0
                ? $"  {animRowsWithoutId} <blit> row(s) carried no id and were skipped."
                : string.Empty;

            string missingFamilyNote = animMissingFamily.Count > 0
                ? $"  {animMissingFamily.Count} unit(s) name a parent that is not a unit in AllData.as, " +
                  $"so no family pass ran: {string.Join(", ", animMissingFamily)}."
                : string.Empty;

            Debug.Log($"Unit import complete. Imported: {imported}  Updated: {updated}  " +
                      $"Skipped: {skipped}  Faction-unspecified: {unresolved}{note}{factionNote}" +
                      $"{animNote}{unmappedNote}{noIdNote}{missingFamilyNote}");
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
                float mass = ParseFloat(massaMatch.Groups[1].Value);
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
                float speed = ParseFloat(speedMatch.Groups[1].Value);
                SetPrivateField(unit, "moveSpeed", speed);
            }

            // Parse jump
            var jumpMatch = Regex.Match(attrs, @"jump='(-?\d+\.?\d*)'");
            if (jumpMatch.Success)
            {
                float jump = ParseFloat(jumpMatch.Groups[1].Value);
                SetPrivateField(unit, "jumpForce", jump);
            }

            // Parse accel
            var accelMatch = Regex.Match(attrs, @"accel='(-?\d+\.?\d*)'");
            if (accelMatch.Success)
            {
                float accel = ParseFloat(accelMatch.Groups[1].Value);
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
                float skill = ParseFloat(skillMatch.Groups[1].Value);
                SetPrivateField(unit, "skill", skill);
            }

            // Parse aqual (water ability)
            var aqualMatch = Regex.Match(attrs, @"aqual='(-?\d+\.?\d*)'");
            if (aqualMatch.Success)
            {
                float aqual = ParseFloat(aqualMatch.Groups[1].Value);
                SetPrivateField(unit, "waterAbility", aqual);
            }

            // Parse dexter (dexterity)
            var dexterMatch = Regex.Match(attrs, @"dexter='(-?\d+\.?\d*)'");
            if (dexterMatch.Success)
            {
                float dexter = ParseFloat(dexterMatch.Groups[1].Value);
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

        /// <summary>
        /// Read the unit's <c>&lt;vulner&gt;</c> element into the definition.
        ///
        /// <para><b>This was a silent no-op, and it is worth remembering why.</b> The regex here tested
        /// <c>&lt;vuln\s+</c> while the data writes <c>&lt;vulner </c>, so it could never match; the
        /// early <c>return</c> then made "this unit declares no vulnerabilities" and "we failed to read
        /// them" look identical. All 94 elements in <c>AllData.as</c> were dropped and every unit asset
        /// on disk still carries the all-neutral default. The mapping beside it was wrong too: it ended
        /// in <c>default: return DamageType.PhysicalMelee</c>, so <c>bul</c> — the name AS3 reads and
        /// the source uses 28 times — was written into <c>phis</c>.</para>
        ///
        /// <para>The parse now lives in <see cref="UnitVulnerabilityParser"/> so it can be unit-tested
        /// at all: this class is in <c>PFE.Editor</c>, which <c>PFE.Tests</c> does not reference.</para>
        /// </summary>
        private static void ParseVulnerabilities(UnitDefinition unit, string content)
        {
            UnitVulnerabilityData parsed = UnitVulnerabilityParser.Parse(content, unit.name);

            // Warnings rather than silence: an attribute the oracle ignores is a fact about the data,
            // and the previous behaviour made it indistinguishable from a successful parse.
            foreach (string warning in parsed.warnings)
                Debug.LogWarning(warning);

            SetPrivateField(unit, "vulnerabilities", parsed.vulnerabilities);
        }

        /// <summary>
        /// Invariant-culture float parse.
        ///
        /// <para><c>float.Parse</c>'s default overload allows thousands separators, so on a
        /// comma-decimal locale (<c>de-DE</c>, <c>ru-RU</c>) <c>dexter='0.9'</c> reads as <b>9</b>. The
        /// source always uses <c>.</c>. <see cref="ArmourDataParser"/> carries the same fix and the full
        /// note; the other importers still use the plain overload.</para>
        /// </summary>
        private static float ParseFloat(string raw)
            => float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static void ParseVision(UnitDefinition unit, string content)
        {
            var visMatch = Regex.Match(content, @"<vis\s+([^>]*)/>");
            if (!visMatch.Success) return;

            string attrs = visMatch.Groups[1].Value;

            // Parse blit (sprite sheet id).
            //
            // This is the NAME OF THE SPRITE SHEET — 'sprRaider5', 'sprAnt1', 'sprZombie3' — and it is
            // the join key the sprite importer needs to slice the right PNG. It used to be matched and
            // then dropped into an empty if-block with the comment "would load sprite from resources",
            // so no unit carried its sheet name and nothing downstream could resolve one. Note it is
            // present on the VARIANT nodes, not the family ones: `raider` declares the blit rows and no
            // sheet, `raider5` declares the sheet and no rows.
            var blitMatch = Regex.Match(attrs, @"blit='([^']+)'");
            if (blitMatch.Success)
            {
                SetPrivateField(unit, "spriteSheetId", blitMatch.Groups[1].Value);
            }

            // Parse vclass (DisplayObject class name).
            //
            // The other visual kind. 24 of the ids rooms place have no sheet at all — including the
            // most-placed unit in the game (slime, 152 placements) and the training dummy — and are
            // drawn as a DisplayObject symbol instead.
            var vclassMatch = Regex.Match(attrs, @"vclass='([^']+)'");
            if (vclassMatch.Success)
            {
                SetPrivateField(unit, "visualClassName", vclassMatch.Groups[1].Value);
            }

            // Parse sprX/sprY (cell size).
            //
            // sprY is NOT just sprX. AS3: `sprY = xml.vis.@sprY > 0 ? int(@sprY) : sprX`
            // (Unit.as:936) — it DEFAULTS to sprX but is a separate attribute, and the data does use
            // non-square cells: ant1 is 78x32, tarakan 60x30, molerat 85x58, hellhound1 200x170.
            // Writing (sprX, sprX) silently gave every one of those the wrong cell height, which is the
            // kind of error that shows up only as a subtly wrong-looking sprite much later.
            var sprXMatch = Regex.Match(attrs, @"sprX='(\d+)'");
            var sprYMatch = Regex.Match(attrs, @"sprY='(\d+)'");
            if (sprXMatch.Success)
            {
                int sprX = int.Parse(sprXMatch.Groups[1].Value);
                int sprY = sprYMatch.Success ? int.Parse(sprYMatch.Groups[1].Value) : sprX;
                SetPrivateField(unit, "spriteDimensions", new Vector2Int(sprX, sprY));
            }

            // Parse sprDX/sprDY — the REGISTRATION POINT, not a draw size.
            //
            // These used to feed a field called "drawDimensions", which is not what they are. AS3
            // offsets the cell by them so that this point lands on the unit's origin:
            //     if(this.blitDX >= 0) visBmp.x = -this.blitDX; else visBmp.x = -this.blitX / 2;
            //     if(this.blitDY >= 0) visBmp.y = -this.blitDY; else visBmp.y = -this.blitY + 10;
            // (Unit.as:2846-2858). The declared default is -1 (Unit.as:511-514), which is exactly what
            // makes the `>= 0` test a PRESENCE test — so -1 is carried through as "not declared" rather
            // than normalised to 0, because 0 is a legal registration point (the cell's top-left).
            var sprDXMatch = Regex.Match(attrs, @"sprDX='(-?\d+)'");
            var sprDYMatch = Regex.Match(attrs, @"sprDY='(-?\d+)'");
            SetPrivateField(unit, "registrationPoint", new Vector2Int(
                sprDXMatch.Success ? int.Parse(sprDXMatch.Groups[1].Value) : -1,
                sprDYMatch.Success ? int.Parse(sprDYMatch.Groups[1].Value) : -1));

            // Parse icoX/icoY — the icon's cell within this same sheet.
            //
            // AS3 defaults a non-positive or absent value to 0 in BOTH axes
            // (`begSprX = xml.vis.@icoX > 0 ? int(@icoX) : 0`, Unit.as:940-941), so "not declared"
            // means cell (0,0) and not "no icon". -1 is stored for "absent" so that the consumer can
            // tell an explicit icoX='0' apart from a missing attribute.
            var icoXMatch = Regex.Match(attrs, @"icoX='(-?\d+)'");
            var icoYMatch = Regex.Match(attrs, @"icoY='(-?\d+)'");
            SetPrivateField(unit, "iconCell", new Vector2Int(
                icoXMatch.Success ? int.Parse(icoXMatch.Groups[1].Value) : -1,
                icoYMatch.Success ? int.Parse(icoYMatch.Groups[1].Value) : -1));

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
                    chance = ParseFloat(chMatch.Groups[1].Value);
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
