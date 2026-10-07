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

                    // Parse parameters (<param> tag) — AS3 `node = node0.param[0]` (`Unit.as:1338`)
                    ParseParams(unit, unitContent);

                    // Parse unit extras (<un> tag) — AS3 `node0.un` (`UnitZombie.as:151-165`).
                    // Reads `parentId`, which ParseParams does not touch, so the two are independent.
                    ParseUnitExtras(unit, unitContent);

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

            // Parse massa (mass) and massafix.
            //
            // AS3 reads BOTH off this same <phis> node — `Unit.as:1018` does `node = node0.phis[0]`,
            // `:1047` reads `massaMove = node.@massa / 50`, `:1051` reads `massaFix = node.@massafix / 50`
            // — and `:1058` then sets `massa = this.massaFix`. So massafix WINS when present; massa is the
            // fallback. Only 6 units author massafix (all turrets), but 5 of them author both with very
            // different values, so importing only `massa` throws them 4.5x-12.5x too far.
            //
            // Decimals are real data here too: `massa='2.5'` is in the oracle. An integer-only pattern
            // does not merely truncate it — the trailing `'` never matches, so the whole attribute is
            // dropped and the field keeps its default. That is the same silent failure the `knocked`
            // pattern below was widened to avoid.
            var massaMatch = Regex.Match(attrs, @"massa='(-?\d+\.?\d*)'");
            if (massaMatch.Success)
            {
                float mass = ParseFloat(massaMatch.Groups[1].Value);
                SetPrivateField(unit, "mass", mass);
            }

            // Left at its 0 sentinel when absent, which `UnitDefinition.Massa` reads as "use `mass`".
            var massafixMatch = Regex.Match(attrs, @"massafix='(-?\d+\.?\d*)'");
            if (massafixMatch.Success)
            {
                float massafix = ParseFloat(massafixMatch.Groups[1].Value);
                SetPrivateField(unit, "massafix", massafix);
            }
        }

        /// <summary>
        /// Parses the <c>&lt;param&gt;</c> node — AS3 <c>Unit.as:1338-1415</c>, where
        /// <c>node = node0.param[0]</c>.
        ///
        /// <para><b>Only <c>blood</c> is read here, and that is a recorded gap rather than a design.</b>
        /// The node carries about nineteen attributes and <c>UnitDefinition</c> has thirteen fields for
        /// them — <c>invulner</c>, <c>overlook</c>, <c>acttrap</c>, <c>npc</c>, <c>trup</c>,
        /// <c>blood</c>, <c>retdam</c>, <c>hero</c>, <c>pony</c>, <c>zombie</c>, <c>robot</c>,
        /// <c>insect</c>, <c>monster</c>, <c>alicorn</c>, <c>mech</c>, <c>hbonus</c>, <c>izvrat</c> —
        /// and <b>not one of them was parsed before this method existed</b>; every one sat at the value
        /// <see cref="SetUnitDefaults"/> wrote. Of the thirteen fields only <c>isInvulnerable</c> and
        /// <c>isAlicorn</c> have a runtime consumer, so most are low-value — but both of those are
        /// still wrong today, and <c>isAlicorn</c> is not even written by the defaults. Closing the
        /// whole node is its own workstream (see the notes); <c>blood</c> is the attribute this
        /// workstream needs.</para>
        ///
        /// <para><b>Absent means 0, not "red".</b> AS3 declares <c>public var blood:int = 0</c>
        /// (<c>Unit.as:416</c>) and assigns only when the attribute is present (<c>:1363-1366</c>), so
        /// <c>blood</c> is <c>0</c> for the <b>100 of 134</b> <c>&lt;param&gt;</c> nodes that do not
        /// author it. And <c>blood == 0</c> is not cosmetic: <c>:1417-1420</c> makes the unit
        /// <b>immune to bleed</b>. The port used to hardcode <c>Red</c> for every unit.</para>
        ///
        /// <para><b>Known divergence, pre-existing:</b> this slices the unit's <i>own</i> text, so a
        /// <c>&lt;param&gt;</c> node declared only on a <c>parent=</c> ancestor is not seen. AS3
        /// inherits. The same divergence is already recorded for <c>&lt;vulner&gt;</c> on
        /// <c>IDamageable.Vulnerabilities</c>.</para>
        /// </summary>
        private static void ParseParams(UnitDefinition unit, string content)
        {
            var paramMatch = Regex.Match(content, @"<param\s+([^>]*)/>");
            if (!paramMatch.Success) return;

            string attrs = paramMatch.Groups[1].Value;

            // Assigned only when present, exactly as AS3 does — so an absent attribute leaves the
            // field at its own default (None), which is AS3's field default too.
            //
            // The `\b` closes a hazard a previous pass found and chose only to document: the bare
            // `blood='` also matches inside `hblood='20'`, and the oracle does author `hblood` on four
            // `<item>` elements. It was harmless because none of them is a `<param>`, and it stays
            // harmless now — the anchor just means the next person to move a `hblood` into a `<param>`
            // gets a wrong value *and* a boundary to notice, instead of a silent one.
            var bloodMatch = Regex.Match(attrs, @"\bblood='(\d+)'");
            if (bloodMatch.Success)
            {
                unit.bloodType = (BloodType)int.Parse(bloodMatch.Groups[1].Value);
            }
        }

        /// <summary>
        /// Apply the unit's <c>&lt;un&gt;</c> element — AS3's "unit extras" node — to the definition.
        ///
        /// <para><b>The logic is in <see cref="UnitExtrasParser"/>, not here.</b> This class lives in
        /// <c>PFE.Editor</c>, which <c>PFE.Tests</c> does not reference, so anything decided here can only
        /// be tested by running Unity. <c>res</c> is an overloaded attribute name whose two AS3 owners
        /// disagree about its meaning (<c>UnitZombie</c> reads it as "resurrects",
        /// <c>UnitTrigger</c> as a resource requirement), so getting it wrong is a silent wrong write on
        /// four units — exactly the kind of decision that needs a test. Same split, and the same reason,
        /// as <see cref="UnitVulnerabilityParser"/>.</para>
        ///
        /// <para>Only <c>res</c> is read. The node carries about nineteen attributes; closing it is its
        /// own workstream, and giving the others writers before anything reads them is the trap
        /// <see cref="UnitDefinition.canResurrect"/> was already in.</para>
        /// </summary>
        private static void ParseUnitExtras(UnitDefinition unit, string content)
        {
            if (UnitExtrasParser.ParseCanResurrect(content, unit.parentId))
            {
                SetPrivateField(unit, "canResurrect", true);
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

            // Run speed — AS3 `run` (`Unit.as:1070-1073`: `if(node.@run.length()) { this.runSpeed =
            // node.@run; }`), an ABSOLUTE px/frame value, not a multiplier.
            //
            // It used to be dropped here, and the consequence was silent in both directions: the field
            // did not exist, so nothing read it, and `RunSpeed` was `moveSpeed * runMultiplier` with
            // `runMultiplier` hardcoded to 2 by SetUnitDefaults. Every unit therefore ran at exactly
            // twice its walk speed. The data says otherwise — `zombie0` is speed 1.5, run 10.
            //
            // The leading `(?:^|\s)` is not decoration: an unanchored `run='…'` would also match the
            // tail of a hypothetical `…run='…'` attribute, and the whole point of this read is that the
            // authored value is what lands. AS3 tests `.length()`, i.e. presence, so an absent
            // attribute must leave the field at 0 — which `RunSpeed` reads as "not authored, use the
            // multiplier".
            var runMatch = Regex.Match(attrs, @"(?:^|\s)run='(-?\d+\.?\d*)'");
            if (runMatch.Success)
            {
                float run = ParseFloat(runMatch.Groups[1].Value);
                SetPrivateField(unit, "runSpeed", run);
            }

            // Knockback susceptibility. AS3 reads it from the SAME <move> node — `Unit.as:1063` takes
            // `node = node0.move[0]` and `:1082-1085` reads `node.@knocked` — and only when the unit has
            // a <move> element at all, which is why the early return above leaves the default of 1 in
            // place for a unit without one.
            //
            // Decimals are real data here, not noise: the authored set is 0, 0.1, 0.3, 1, 1.2, 1.5.
            // An integer-only pattern would silently import 0.3 as nothing and leave 1 behind.
            var knockedMatch = Regex.Match(attrs, @"knocked='(-?\d+\.?\d*)'");
            if (knockedMatch.Success)
            {
                float knocked = ParseFloat(knockedMatch.Groups[1].Value);
                SetPrivateField(unit, "knocked", knocked);
            }

            // Parse jump
            var jumpMatch = Regex.Match(attrs, @"jump='(-?\d+\.?\d*)'");
            if (jumpMatch.Success)
            {
                float jump = ParseFloat(jumpMatch.Groups[1].Value);
                SetPrivateField(unit, "jumpForce", jump);
            }

            // Fixed in place. AS3 reads it from the SAME <move> node as `knocked` — `Unit.as:1114`
            // tests `node.@fixed.length()` and `:1116` assigns `this.fixed = node.@fixed > 0`.
            //
            // The comparison is `> 0`, not `== 1`, so any positive value pins the unit; the oracle
            // only ever writes '1' (16 <move> nodes, 17 unit ids — `turret` is the shared template).
            // A signed pattern costs nothing and matches the sibling reads above.
            //
            // `fixed` is a C# keyword, so the local cannot be named after it. The port's field is
            // `isFixed`; SetUnitDefaults already writes `false`, which is AS3's own default
            // (`Unit.as:204`) and the correct value for the 117 units that do not author the
            // attribute — so a missing attribute must leave the default alone rather than write it.
            var fixedMatch = Regex.Match(attrs, @"fixed='(-?\d+\.?\d*)'");
            if (fixedMatch.Success)
            {
                float fixedValue = ParseFloat(fixedMatch.Groups[1].Value);
                SetPrivateField(unit, "isFixed", fixedValue > 0f);
            }

            // Parse accel (ground acceleration). The `\b` is load-bearing: without it this also matches
            // inside `levitaccel='1.6'`, and the oracle authors `levitaccel` BEFORE `accel` in 8 of the
            // 106 `<move>` rows that match — all 8 of which declare no ground `accel` at all, so the port
            // silently gave those units their LEVITATION acceleration as their ground acceleration.
            // Replayed against the real data: 8 of 106 matches read the wrong attribute.
            var accelMatch = Regex.Match(attrs, @"\baccel='(-?\d+\.?\d*)'");
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

            // Every read below is name-searched over the whole attribute string, so a bare pattern also
            // matches inside a longer attribute name — `armor='` inside `marmor='5'`, `accel='` inside
            // `levitaccel='1.6'`. `Regex.Match` returns the FIRST match, so which one wins is decided by
            // the DATA's attribute order, not by the order these blocks run in. The `\b` anchors make the
            // match start at a name boundary, which is the only thing that actually prevents it.
            //
            // (An earlier comment here claimed `armor` "must be parsed before krep to avoid overwrite".
            // That is a belief about a mechanism that does not exist: these regexes do not interact, and
            // reordering them changes nothing. The real hazard was always the suffix neighbour.)

            // Parse hp (health)
            var hpMatch = Regex.Match(attrs, @"\bhp='(\d+)'");
            if (hpMatch.Success)
            {
                int hp = int.Parse(hpMatch.Groups[1].Value);
                SetPrivateField(unit, "health", hp);
            }

            // Parse armor (physical). The `\b` matters: without it this also matches inside
            // `marmor='5'`, and the oracle authors `marmor` BEFORE `armor` in 2 of the 51 rows that have
            // either — both of which declare no physical `armor` at all, so the port silently invented
            // `armor = 5` and `armor = 10` from their magic armour. Replayed against the real data:
            // 2 of 51 matches read the wrong attribute.
            var armorMatch = Regex.Match(attrs, @"\barmor='(\d+)'");
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

            // Parse observ (observation power) — AS3 `Unit.observ:Number = 0` (`:122282`), read on the
            // same `<comb>` node as `ear`: `if(node.@observ.length()) this.observ += node.@observ;`
            // (`:122994`).
            //
            // THE ATTRIBUTE IS `observ`, NOT `obs`. This pattern used to be `obs='(\d+)'`, which matches
            // NOTHING: `grep -c "obs='"` over the whole 8 MB oracle returns 0, because `obs='` is not a
            // substring of `observ='5'`. So 37 authored values were dropped on the floor and
            // `observationRange` kept its default of 0 for every unit in the game — which is a *legal*
            // value, so nothing ever looked wrong. It is the listener's scaling of the vision path only
            // (`observation(intensity, this.observ)`, `:126474`), so the symptom was "enemies notice a
            // little slower than the data says", not a visible failure.
            //
            // AS3 uses `+=`; a single read of one node makes assignment equivalent, and the port's
            // importer does not merge parent definitions into the same instance, so there is nothing to
            // accumulate onto. Recorded rather than silently flattened.
            var observMatch = Regex.Match(attrs, @"\bobserv='(\d+)'");
            if (observMatch.Success)
            {
                int observ = int.Parse(observMatch.Groups[1].Value);
                SetPrivateField(unit, "observationRange", observ);
            }

            // Parse ear (hearing multiplier).
            //
            // AS3 reads it on the SAME element as `observ` — `if(node.@ear.length()) this.ear =
            // node.@ear;` (`actionscript_project_context.txt:122998`), beside the `observ` read at
            // `:122994`. It is the listener's factor in `listen()`'s product
            // (`param1.noise * this.ear * loc.earMult`, `:126338`), so a row that authors `ear='0'` is
            // stone deaf at any range and a row that authors `ear='2.5'` hears 2.5x further.
            //
            // The data is not hypothetical: spritebot/vortex/roller/roller2 all author `ear='0'`
            // (`:3800-3845`) and the two hp='1000' sentinels do too (`:4038`). Leaving this unparsed
            // would make every one of them keep the default 1 and behave like an ordinary listener.
            //
            // Fractional values are real (0.2 for a buried digger is set at runtime, 1.4 and 2.5 in the
            // data), so this is a float parse, not an int one.
            //
            // The `\b` is load-bearing, not decoration. A bare `ear='` also matches inside `rear='1'`,
            // which the oracle DOES author — on 18 `<mat>` tile elements (`:8696-8739`). Those are a
            // different element and ParseCombat never sees them, so the collision is latent rather than
            // live; but it is the exact shape rule #11 warns about (an attribute read by name-search
            // silently picking up a suffix neighbour), and one `\b` removes it. No `<comb>` row authors
            // `rear` today, so this costs nothing and removes a trap.
            var earMatch = Regex.Match(attrs, @"\bear='(-?\d+\.?\d*)'");
            if (earMatch.Success)
            {
                float ear = ParseFloat(earMatch.Groups[1].Value);
                SetPrivateField(unit, "ear", ear);
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
        ///
        /// <para><b>It also applies AS3's blood rule</b> — <c>if (blood == 0) vulner[D_BLEED] = 0</c>
        /// (<c>Unit.as:1417-1420</c>) — by passing <c>unit.bloodType</c> through. That is the second half
        /// of the <c>blood</c> attribute: <see cref="ParseParams"/> reads the value, and this decides
        /// what it <i>means</i>. <c>ParseParams</c> must therefore run first; the comment below is the
        /// only thing enforcing it, because the ordering cannot be asserted from <c>PFE.Tests</c>.</para>
        /// </summary>
        private static void ParseVulnerabilities(UnitDefinition unit, string content)
        {
            // `unit.bloodType` is read here, so this MUST run after ParseParams (:294) — the two are nine
            // lines apart in the same straight-line method, which is what makes the dependency easy to
            // break by reordering. The consequence of getting it wrong is silent and one-sided: a unit
            // that authors `blood='1'` would still hold the field default (None = 0) at this point, so
            // the parser would zero its bleed multiplier and it would become bleed-immune for no reason.
            UnitVulnerabilityData parsed =
                UnitVulnerabilityParser.Parse(content, unit.name, unit.bloodType);

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

            // Parse noise (Unit.noiseRun) — how loud this unit is while running.
            //
            // AS3 reads it here, on the same `<vis>` node the sheet name comes from:
            // `if(node.@noise.length()) this.noiseRun = node.@noise;`
            // (`actionscript_project_context.txt:123110`). The port previously did not read it at all and
            // instead hardcoded the field to 300 in SetUnitDefaults — so every unit in the game was
            // equally loud, and the value was wrong for all of them: the oracle's default is 200 and the
            // data's most common authored value is 600.
            //
            // The spread is wide and load-bearing: `<vis noise='600'/>` on every zombie and raider,
            // `noise='500'` on the robots, `noise='0'` on all three slimes (`:3439`, `:3456`, `:3467`).
            // A slime that is silent by design and a zombie that is audible at 600 px are the two ends of
            // the stealth system.
            //
            // `\b` for the same reason as `ear` below: `<vis>` is the only element this string holds, so
            // there is no live collision, but a suffix neighbour (`...noise='` — e.g. a hypothetical
            // `crynoise`) would be read as this unit's run noise without a word of warning.
            var noiseMatch = Regex.Match(attrs, @"\bnoise='(\d+)'");
            if (noiseMatch.Success)
            {
                int noise = int.Parse(noiseMatch.Groups[1].Value);
                SetPrivateField(unit, "noiseRun", noise);
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
            // AS3 `public var runSpeed:Number = 10` (`Unit.as:220`) is the DECLARED default, but it is
            // not the right "source is silent" value here: `Unit.getXmlParam` only overwrites it when
            // the row authors `run` (`:1070-1073`), so a row without the attribute keeps 10. Writing 10
            // would therefore be faithful — and it is deliberately NOT written, because `RunSpeed`
            // reads 0 as "not authored, use moveSpeed * runMultiplier" and that is the model the
            // player's sprint already uses. Writing AS3's 10 here would silently give every silent row
            // a 10 px/frame run and change the player's sprint. 0 is the honest "absent".
            SetPrivateField(unit, "runSpeed", 0f);
            SetPrivateField(unit, "braking", 0.5f);
            SetPrivateField(unit, "canSwim", true);
            SetPrivateField(unit, "canLevitate", false);
            SetPrivateField(unit, "canBeKnockedDown", true);
            SetPrivateField(unit, "isFixed", false);
            // AS3 `public var blood:int = 0` (`Unit.as:416`) — NONE, not red. `blood == 0` is also the
            // bleed-immunity flag (`:1417-1420`), so a "sensible default" of Red is a combat change as
            // well as a colour one. ParseParams overrides this when the data authors `blood=`.
            SetPrivateField(unit, "bloodType", BloodType.None);
            SetPrivateField(unit, "leavesCorpse", true);
            SetPrivateField(unit, "isInvulnerable", false);
            SetPrivateField(unit, "canActivateTraps", true);
            SetPrivateField(unit, "damageType", DamageType.PhysicalMelee);
            SetPrivateField(unit, "dexterity", 1f);
            SetPrivateField(unit, "skill", 1f);
            // AS3 `Unit.noiseRun:int = 200` (`:122272`) and `Unit.ear:Number = 1` (`:122286`) — the
            // declared defaults, which are also the right "the source is silent" values here, because
            // AS3's own import only overwrites them when the row authors the attribute. `littlepip`
            // authors neither, so these two numbers are the player's.
            //
            // The previous line here was `SetPrivateField(unit, "noiseLevel", 300)`: an invented name, an
            // invented value, and no reader. See the field's doc for why 300 was wrong in both
            // directions at once.
            SetPrivateField(unit, "noiseRun", 200);
            SetPrivateField(unit, "ear", 1f);
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
