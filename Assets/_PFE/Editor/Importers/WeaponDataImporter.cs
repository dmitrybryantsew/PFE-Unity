using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PFE.Data.Definitions;
using PFE.Systems.Weapons;
// Brings Attr / AttrF / AttrI / AttrBool into scope unqualified, so the ~40 call sites below read
// exactly as they did when these were private helpers on this class. They are NOT private any more:
// see the Helpers region for why.
using static PFE.Systems.Weapons.WeaponXmlAttrs;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports WeaponDefinition assets from AllData.as ActionScript source.
    ///
    /// AllData.as weapon XML schema (actual format):
    ///
    ///   <weapon id='p10mm' tip='2' cat='2' skill='2' lvl='1' perk='pistol' alicorn='1'>
    ///     <char maxhp='300' damage='11' rapid='7' prec='8' knock='4' destroy='10'
    ///           tipdam='0' kol='1' dkol='0' expl='0' damexpl='0' pier='0' crit='2'
    ///           antiprec='0' prep='0' auto='1'/>
    ///     <char ... />          <!-- optional second char = tier-2 variant stats -->
    ///     <phis speed='150' deviation='5' recoil='5' massa='2'
    ///           grav='0' flame='0' navod='0' accel='0' drot='0' volna='1' phisbul='1'/>
    ///     <vis  tipdec='1' shell='1' shine='500' vbul='laser2' spring='2'
    ///           bulanim='1' phisbul='1' flare='plasma' visexpl='expl'/>
    ///     <snd  shoot='p10mm_s' reload='p10mm_r' noise='700'
    ///           hit='hit_metal' prep='gatl_s' t1='100' t2='1500'/>
    ///     <ammo holder='12' reload='35' rashod='1' mana='50' magic='100' recharg='0'/>
    ///     <a>p10</a>            <!-- ammo type ID -->
    ///     <dop  effect='blind' damage='2' ch='1' probiv='0.4'/>
    ///   </weapon>
    ///
    /// Thrown-only attributes, on the weapon root and its tier-1 <char>:
    ///
    ///   <weapon id='hmine' tip='4' skill='5' throwtip='1'>
    ///     <char rapid='30' maxhp='10' time='15' sens='100' damexpl='125' expl='180' radio='1'/>
    ///     <phis speed='5' bumc='1'/>
    ///
    /// `throwtip` (root) picks mine-vs-grenade, `time` (char) is the fuse, `radio` (char) enables the
    /// reload-key detonator, `sens` (char) is the placed mine's proximity box, and `bumc` (phis) is
    /// contact detonation. All five were previously unimported — see
    /// docs/AUDIT_throwable_and_explosive_2026-10-03.md §1.
    ///
    /// A weapon block may also be <b>self-closing</b>, with no body and no closing tag:
    ///
    ///   <weapon id='sp_slow' tip='5' skill='6' perslvl='3' spell='1'/>
    ///
    /// All nine supportive-magic weapons are written that way, and they are the last weapon entries
    /// in the file. The block matcher is <see cref="PFE.Systems.Weapons.WeaponXmlBlocks"/>, which
    /// handles both shapes; the earlier inline pattern did not, and skipped exactly those nine.
    ///
    /// Multiple <char> nodes = weapon variants (tier 1, tier 2…). We import tier-1 only
    /// and store variant count for future use.
    /// </summary>
    public class WeaponDataImporter
    {
        private static string AllDataPath => SourceImportPaths.AllDataAsPath;
        private static readonly string OutputPath =
            "Assets/_PFE/Data/Resources/Weapons";

        // ── Helpers ─────────────────────────────────────────────────────────────
        //
        // Attr / AttrF / AttrI / AttrBool now live in PFE.Systems.Weapons.WeaponXmlAttrs and are
        // imported with `using static` above, so the call sites below are unchanged.
        //
        // They moved because they had a boundary bug that nothing in this assembly could catch:
        // `Regex.Match(src, name + "='([^']*)'")` matches a name that is the SUFFIX of another, so
        // `expl` read `damexpl`, `kol` read `dkol` and `lvl` read `perslvl` — 81 reads across 72 of
        // the 213 weapons, all silently wrong (balemine's blast radius 750 instead of 300; dronlaser
        // firing 15 rounds per shot; fireball's weaponLevel 12 instead of 0). PFE.Tests does not
        // reference PFE.Editor, so no fixture in this project could have exercised them here. In the
        // runtime assembly they are covered by WeaponXmlBlocksTests.
        //
        // Node / AllNodes / NodeText stay local: they are not duplicated anywhere and have no
        // colliding-name failure mode.

        /// <summary>
        /// AS3's class default for a melee weapon's reach — <c>Weapon.dlina = 50</c> /
        /// <c>mindlina = 50</c> (<c>Weapon.as:138-140</c>), in Flash pixels.
        ///
        /// <para>Held here as well as on <see cref="WeaponDefinition"/> because the importer must be
        /// able to assign it <b>unconditionally</b>: a fallback of <c>def.meleeDlina</c> would keep
        /// whatever the asset already held, so a weapon whose <c>&lt;phis&gt;</c> block lost its
        /// <c>long</c> could never come back down to the default.</para>
        /// </summary>
        private const float MeleeReachDefault = 50f;

        // Extract the raw content of the first XML node that matches tag,
        // searching inside parent. Returns null if not found.
        private static string Node(string parent, string tag)
        {
            // Self-closing:  <tag attr='…'/>
            var self = Regex.Match(parent, $@"<{tag}(\s[^>]*)/>", RegexOptions.Singleline);
            if (self.Success) return self.Groups[1].Value;
            // With children: <tag attr='…'>…</tag>
            var open = Regex.Match(parent, $@"<{tag}(\s[^>]*)>", RegexOptions.Singleline);
            if (open.Success) return open.Groups[1].Value;
            return null;
        }

        // Extract text content of a node like <a>p10</a>
        private static string NodeText(string parent, string tag)
        {
            var m = Regex.Match(parent, $@"<{tag}>([^<]*)</{tag}>");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        // Returns all attribute strings for repeated nodes (e.g. multiple <char> nodes)
        private static List<string> AllNodes(string parent, string tag)
        {
            var list = new List<string>();
            foreach (Match m in Regex.Matches(parent,
                $@"<{tag}(\s[^>]*)(?:/>|>)", RegexOptions.Singleline))
                list.Add(m.Groups[1].Value);
            return list;
        }

        // ── Archetype derivation ────────────────────────────────────────────────

        /// <summary>
        /// Derive the ProjectileArchetype from AllData.as sub-node data.
        /// This maps the AS3 vbul + spring + flame + phisbul + navod combo to a Unity archetype.
        /// </summary>
        private static ProjectileArchetype DeriveArchetype(
            int tip, string vbul, int spring, int flame,
            bool phisbul, float navod)
        {
            // Magic weapon (WMagic — tip == 5)
            if (tip == 5) return ProjectileArchetype.Magic;

            // Homing
            if (navod > 0) return ProjectileArchetype.Homing;

            // Physics bullet (grenade, rocket)
            if (phisbul) return ProjectileArchetype.Explosive;

            // Flame
            if (flame > 0) return ProjectileArchetype.Flame;

            // Laser — spring=2 OR vbul contains "laser" or "dray" or "moln"
            if (spring == 2) return ProjectileArchetype.Laser;
            if (!string.IsNullOrEmpty(vbul))
            {
                if (vbul.Contains("laser") || vbul == "dray" || vbul == "moln")
                    return ProjectileArchetype.Laser;
                if (vbul.Contains("plasma") || vbul == "blump" || vbul == "pulse")
                    return ProjectileArchetype.Plasma;
                if (vbul == "spark" || vbul == "sparkl" || vbul == "lightning"
                    || vbul == "bloodlight")
                    return ProjectileArchetype.Spark;
                if (vbul.Contains("plevok") || vbul == "venom" || vbul.Contains("kapl")
                    || vbul == "blood" || vbul == "necro" || vbul == "psy"
                    || vbul == "bloodpsy" || vbul == "pink" || vbul == "necrbullet")
                    return ProjectileArchetype.Spit;
            }

            return ProjectileArchetype.Ballistic;
        }

        // ── Main import ─────────────────────────────────────────────────────────

        [MenuItem("PFE/Data/Import Weapons from AllData.as")]
        public static void ImportWeapons()
        {
            if (!File.Exists(AllDataPath))
            {
                Debug.LogError(SourceImportPaths.MissingSourceMessage(AllDataPath, "AllData.as"));
                return;
            }

            if (!Directory.Exists(OutputPath))
                Directory.CreateDirectory(OutputPath);

            string all = File.ReadAllText(AllDataPath);
            int imported = 0, updated = 0, skipped = 0;

            // Variant-override attributes that no port field reads, tallied across the whole run and
            // reported once at the end. Non-empty today (five phis attributes) — see
            // WeaponVariantXml.UnreadAttributeNames, which documents which and why.
            var unreadTally = new Dictionary<string, int>();

            // Every <weapon …> block, self-closing ones included. The matching lives in
            // WeaponXmlBlocks rather than here so the offline wall can exercise it — this file is
            // in PFE.Editor, which PFE.Tests does not reference, and a bug in exactly this pattern
            // went unnoticed for days in that blind spot.
            //
            // What it was: the old pattern required a literal </weapon>, so it dropped the nine
            // self-closing spell weapons (AllData.as:4017-4025). They are the LAST weapon entries
            // in the file, so there is no later </weapon> to close against and the match never
            // happens — 204 weapons imported instead of 213, with no error and no missing asset
            // (the nine .asset files already existed, frozen at an earlier run).
            var weaponBlocks = WeaponXmlBlocks.Parse(all);

            Debug.Log($"[WeaponDataImporter] Found {weaponBlocks.Count} weapon blocks.");

            foreach (var block in weaponBlocks)
            {
                string id        = block.Id;
                string rootAttrs = block.RootAttrs;
                string body      = block.Body;

                string assetPath = $"{OutputPath}/{id}.asset";
                bool exists = File.Exists(assetPath);

                WeaponDefinition def = exists
                    ? AssetDatabase.LoadAssetAtPath<WeaponDefinition>(assetPath)
                    : ScriptableObject.CreateInstance<WeaponDefinition>();

                if (def == null)
                {
                    Debug.LogWarning($"[WeaponDataImporter] Could not load/create asset for {id}");
                    skipped++;
                    continue;
                }

                try
                {
                    ApplyWeaponData(def, id, rootAttrs, body, unreadTally);

                    if (exists)
                    {
                        EditorUtility.SetDirty(def);
                        updated++;
                    }
                    else
                    {
                        AssetDatabase.CreateAsset(def, assetPath);
                        imported++;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[WeaponDataImporter] Failed on '{id}': {e.Message}\n{e.StackTrace}");
                    skipped++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[WeaponDataImporter] Done. Imported: {imported}  Updated: {updated}  Skipped: {skipped}");

            // The variant-override drop report. Printed even when empty, so "no line at all" can never
            // be mistaken for "nothing was dropped" — the whole point of constraint 12 is that the
            // silent case is the dangerous one.
            if (unreadTally.Count == 0)
            {
                Debug.Log("[WeaponDataImporter] Variant overrides: every attribute is read by the port.");
                return;
            }

            var names = new List<string>(unreadTally.Keys);
            names.Sort(System.StringComparer.Ordinal);

            var parts = new List<string>(names.Count);
            int total = 0;
            foreach (string name in names)
            {
                total += unreadTally[name];
                parts.Add($"{name} x{unreadTally[name]}");
            }

            Debug.LogWarning(
                $"[WeaponDataImporter] Variant overrides carry {total} attribute(s) the port has no reader " +
                $"for, across {names.Count} distinct name(s): {string.Join(", ", parts)}. " +
                "These are dropped from the variant tier; see WeaponVariantXml.UnreadAttributeNames.");
        }

        // ── Per-weapon population ───────────────────────────────────────────────

        private static void ApplyWeaponData(WeaponDefinition def,
            string id, string rootAttrs, string body, Dictionary<string, int> unreadTally)
        {
            // ── Root tag attributes ──────────────────────────────────────────
            def.weaponId    = id;
            def.weaponType  = (WeaponType)AttrI(rootAttrs, "tip", 0);

            // AS3 `@mess` — the once-per-save tutorial popup key (`Invent.as:1293`). Four weapon rows
            // carry one (mont, r32, rech, telebul). Unconditional, so a reimport moves a stale value
            // back to the default rather than freezing whatever the asset already held.
            //
            // ⚠ This attribute used to be listed as "deliberately dropped" on WeaponDefinition. It is
            // not: the popup that reads it fires for weapons too, and all four keys have text in
            // text_en.xml — in the `item` table keyed by the weapon id, because the `<weapon>` table
            // there ships no `<mess>` child at all. See the gap ledger, pickup feedback §10.
            def.mess        = Attr(rootAttrs, "mess", "");

            // AS3's second class selector. Weapon.as:345-394 dispatches on tip AND punch:
            // tip==1/12/4/5 → WClub/WPaint/WThrow/WMagic, then punch>0 → WPunch, else the base
            // (RANGED) Weapon. Dropping `punch` here is what made every tip==0 weapon look
            // "unarmed" — 57 of the 60 tip==0 weapons are plain ranged `Weapon` in AS3.
            def.punch       = AttrI(rootAttrs, "punch", 0);

            // Melee sub-type. WClub.as:115-118 reads `node.@mtip` — the weapon ROOT, not <phis>.
            // 0 = swing arc, 1 = Thrust, 2 = Overhead. Only 6 weapons set it (spear/mspear/tlance
            // and autoaxe/bsaw/ripper); until now those six values existed only as hand-edited YAML
            // in the assets, which a reimport preserved by accident rather than by design.
            def.meleeType   = (MeleeType)AttrI(rootAttrs, "mtip", 0);

            // ── Melee REACH — WClub.as:119-130 ───────────────────────────────────────────────
            //
            //   if (node.phis[0].@long    > 0) dlina    = node.phis[0].@long;
            //   if (node.phis[0].@minlong > 0) mindlina = node.phis[0].@minlong;
            //   else                           mindlina = dlina;
            //
            // Read off the FIRST <phis> node — the same tier-1 node the sub-type comes from — and
            // the class defaults are AS3's own `dlina = 50` / `mindlina = 50` (Weapon.as:138-140),
            // NOT the 100 the C# fields were initialised to.
            //
            // Neither attribute was imported, so every one of the 204 weapon assets carried 100/100.
            // Two consequences, both live: every melee weapon reached **twice as far as AS3** (sword
            // 0.8 → 1.0 unit, `a_melee` 1.4 → 1.0), and the Overhead sweep — `bindMove(mindlina,
            // dlina)` at WClub.as:473 — became a **zero-length move** because the two were equal, so
            // autoaxe/bsaw/ripper swept a degenerate segment.
            var phisNodes = AllNodes(body, "phis");
            string phisT1 = phisNodes.Count > 0 ? phisNodes[0] : "";

            // Assigned unconditionally, both of them, so a reimport can move a stale value BACK to
            // the default. A `AttrF(..., def.meleeDlina)` fallback would instead freeze whatever the
            // asset already held — the create-only importer failure mode in miniature.
            float dlina = AttrF(phisT1, "long", MeleeReachDefault);
            def.meleeDlina = dlina > 0f ? dlina : MeleeReachDefault;

            float minDlina = AttrF(phisT1, "minlong", 0f);
            def.meleeMinDlina = minDlina > 0f ? minDlina : def.meleeDlina;

            // ── Melee MASS — WClub.as:330's step limit ───────────────────────────────────────────
            //
            //   norma(del, Math.max(this.levitRun, 1 / massa))      // WClub.as:330
            //   massa = param1.@massa / 50                          // Weapon.as:715-721
            //
            // So the per-frame travel limit is `max(meleeRun, 50/@massa)` px: a bat (massa 1) may
            // move 50 px per frame and a hammer (massa 15) only 3.3, where `meleeRun` (10 at tier 0)
            // takes over. `massa` is a SINGLE-PURPOSE attribute — nothing else in the oracle reads it
            // — which is why it is only now imported.
            //
            // Stored RAW and assigned unconditionally, like the two reach values above: 0 means the
            // row authored none, and `MeleeSpringMath.StepClampPx` reproduces the oracle's
            // `1/0 = Infinity` (unclamped) for it rather than freezing a stale asset value.
            def.meleeMassa = AttrF(phisT1, "massa", 0f);

            def.skillLevel  = AttrI(rootAttrs, "skill", 0);
            def.weaponLevel = AttrI(rootAttrs, "lvl",   0);
            // AS3 Weapon.as:430 `this.perslvl = node.@perslvl`. Read with the same boundary-anchored
            // AttrI as `lvl` above — which is the whole point: `lvl` is a substring of `perslvl`, so a
            // name-search reader would have made every one of the 23 magic weapons report lvl=0..28
            // from the wrong attribute. 23 rows carry it, all tip='5'.
            def.perslvl     = AttrI(rootAttrs, "perslvl", 0);
            def.alicornOnly = AttrBool(rootAttrs, "alicorn");

            // AS3 weapon@spell (Weapon.as:118) — the flag that separates the two unrelated things
            // sharing tip==5. Assault magic (WMagic: fireball, eclipse, mray …) is held and fired;
            // supportive magic is a Spell that is *cast from the inventory* and never equipped
            // (UnitPlayer.as:3627-3635 — `if(_loc3_.spell) { … useItem(id); return; }`).
            //
            // Nine weapons carry it, sp_slow … sp_invulner (AllData.as:4017-4025), and every one is
            // self-closing with no <char> body at all. Until the block matcher above was fixed they
            // were not imported in any form, so this line had nothing to read.
            //
            // Presence-based, exactly like alicorn above: AS3 never writes spell='0'.
            def.spell = AttrBool(rootAttrs, "spell");

            // ── Thrown dispatch — WThrow.as:44-47 ────────────────────────────────
            //
            // `throwtip` is the *only* thing that separates a thrown grenade from a placed mine:
            // WThrow.shoot() branches `if(throwTip == 1) → new Mine(...) else → new PhisBullet(...)`.
            // AS3 reads it off the weapon ROOT and keeps its 0 default when the attribute is absent
            // (`if(node.@throwtip > 0)`), so a plain AttrI with a 0 fallback is the same rule.
            //
            // Not importing it left the field at 0 for all 13 throwables, which made the mine branch
            // of ThrownWeaponController unreachable — MineObject, Mine.prefab and the wired
            // _minePrefab were dead code and the eight mine weapons arc-threw a grenade instead.
            // See docs/AUDIT_throwable_and_explosive_2026-10-03.md §1a.
            def.throwTip = AttrI(rootAttrs, "throwtip", 0);

            // Root-level tipdec (melee weapons carry tipdec on the weapon tag itself)
            int rootTipdec = AttrI(rootAttrs, "tipdec", -1);

            // ── <char> — first node = tier-1 stats ──────────────────────────
            var charNodes = AllNodes(body, "char");
            string charT1 = charNodes.Count > 0 ? charNodes[0] : "";

            def.maxDurability       = AttrI(charT1, "maxhp",   100);
            def.baseDamage          = AttrF(charT1, "damage",  10f);
            def.rapid               = AttrF(charT1, "rapid",   10f);

            // ── The two melee flags — WClub.as:131-138 ────────────────────────────────────────
            //
            //   if (node.char[0].@pow.length())   this.powerfull = true;   // :131-134
            //   if (node.char[0].@combo.length()) this.combinat  = true;   // :135-138
            //
            // Both are AS3 *presence* tests on the tier-1 <char>, never written as '0' — which is
            // why this uses AttrPresent and not AttrBool. Neither was imported, so both fields sat
            // at their `false` initialisers on all 204 assets.
            //
            // `powerfull` matters even before the charge itself is ported, because `combinat` has a
            // second assignment that reads it:
            //
            //   if (!auto && !this.powerfull) this.combinat = true;        // :177-180
            //
            // i.e. every melee weapon that is neither automatic nor power-capable has a combo
            // counter. Twelve do, and with `meleeCombo` alone the port had combos on exactly one
            // weapon (`mach`, the only one that authors `combo='1'`).
            def.meleePowerAttack = AttrPresent(charT1, "pow");
            def.meleeCombo       = AttrPresent(charT1, "combo");

            // char@auto — AS3 tests PRESENCE first, then `!= "0"` (Weapon.as:856-859), so absent
            // and '0' are different answers and a bool cannot carry both. 0 = absent.
            // 14 weapons carry auto='1' on their tier-1 <char> with rapid > 6 (shotgun 12, bfg 15,
            // mont 15, knife 8, lasp 7, …); without this they fire single-shot here.
            string autoAttr         = Attr(charT1, "auto");
            def.autoMode            = string.IsNullOrEmpty(autoAttr) ? 0 : (autoAttr != "0" ? 2 : 1);
            def.precision           = AttrF(charT1, "prec",    0f) * 40f; // AS3 scales by 40
            // antiprec is scaled by 40 exactly like prec (Weapon.as:826), so the two are comparable
            // against Bullet.dist, which is in pixels. Four weapons carry antiprec='8' (320 px).
            def.antiPrecision       = AttrF(charT1, "antiprec", 0f) * 40f;
            def.knockback           = AttrF(charT1, "knock",   0f);
            // The default is AS3's class default, not zero: `Weapon.destroy = 10` (Weapon.as:156),
            // and Weapon.as:828-830 only overwrites it `if (param1.@destroy.length())`. Ten weapons
            // author no `destroy` at all (the three scorppunch rows, ttweap1-6 and `paint`), so the
            // port was reading 0 where the oracle reads 10 — those weapons' rounds could not break a
            // tile that AS3's could.
            def.destroyTiles        = AttrF(charT1, "destroy", 10f);
            def.piercing            = AttrF(charT1, "pier",    0f);   // flat armour points, not a chance
            def.projectilesPerShot  = AttrI(charT1, "kol",     1);
            def.burstCount          = AttrI(charT1, "dkol",    0);
            def.explRadius          = AttrF(charT1, "expl",    0f);
            def.explosionDamage     = AttrF(charT1, "damexpl", 0f);
            // ── The explosion SHAPE and the pulse count — Weapon.as:844-852 ──────────────────────────
            //
            // Both reads are guarded in AS3 on `@attr.length()` (presence), so an absent attribute
            // keeps the class default — explTip 1, explKol 0. Reproduced by passing the same defaults
            // here: reading them unconditionally would be identical on this data (no row writes an
            // empty value) but would not survive a mod that did.
            //
            // explTip selects which of Bullet's two blast shapes runs (explRun's multiplexer,
            // Bullet.as:711-717); explKol is the number of PULSES — the sustained damage area that
            // fgren/molotov/gasgr/acidgr and four monster weapons have and a plain grenade does not.
            // Neither was imported before, so every blast was a single pulse of the default shape.
            def.explTip             = AttrI(charT1, "expltip", 1);
            def.explKol             = AttrI(charT1, "explkol", 0);
            def.prepFrames          = AttrI(charT1, "prep",    0); // wind-up frames (minigun=32, flamer=10, etc.)

            // ── Thrown fuse + radio — WThrow.as:52-59 ─────────────────────────────
            //
            // detTime: `if(node.char[0].@time > 0) this.detTime = ...` — a 0 or absent `time` keeps
            // the class default 75. Reading it as an unconditional AttrI would let a literal
            // `time='0'` produce a zero-frame fuse, so the `> 0` test is reproduced.
            //
            // This is the fuse for BOTH thrown sub-types off the same node: `WThrow.detTime` for the
            // arc throw, and (multiplied by 0.3 in WThrow.as:155) the mine's countdown. Not importing
            // it left every throwable on the 75 default — mercgr's 120 became 75, and the mines' 15
            // became 75, i.e. a mine countdown ~5x too long.
            int detTime = AttrI(charT1, "time", 0);
            if (detTime > 0) def.fuseFrames = detTime;

            // radio: AS3 tests attribute PRESENCE, not value (`Boolean(node.char.@radio.length())`),
            // but every row that sets it uses '1', so the presence test and AttrBool agree on this
            // data. radio is meaningful only for WThrow — it is the x37 mine's only way to fire.
            def.radio = AttrBool(charT1, "radio");

            // sens: the placed mine's proximity half-width (Mine.as:166-169). Declaration default
            // 100; x37 sets 0, which is AS3's "never proximity-trigger" — see WeaponDefinition.sens.
            def.sens = AttrF(charT1, "sens", 100f);

            // tipdam → DamageType
            int tipdam = AttrI(charT1, "tipdam", 0);
            def.damageType = (DamageType)tipdam;

            // crit: AS3 stores a multiplier (e.g. crit='2' → critCh=0.2, critM=1)
            float crit = AttrF(charT1, "crit", 0f);
            if (crit > 0f)
            {
                def.critChance     = 0.1f * crit;
                def.critMultiplier = crit;
            }
            else
            {
                def.critChance     = 0.1f;
                def.critMultiplier = 2f;
            }

            // ── <phis> ───────────────────────────────────────────────────────
            string phis = Node(body, "phis") ?? "";

            def.projectileSpeed = AttrF(phis, "speed",     100f);
            def.deviation       = AttrF(phis, "deviation", 0f);
            def.bulletGravity   = AttrF(phis, "grav",      0f);
            def.bulletAccel     = AttrF(phis, "accel",     0f);
            def.bulletFlame     = AttrI(phis, "flame",     0);
            def.bulletNavod     = AttrF(phis, "navod",     0f);

            // Contact detonation — `<phis bumc>`, read by WThrow.as:60-63 into `WThrow.bumc` and then
            // stamped on the bullet (:184). Two weapons carry it: acidgr and molotov, both throwables.
            //
            // This is NOT `vis@phisbul`, which is the attribute the port used to read here. The two
            // live on different nodes, mean different things (phisbul selects the ProjectileArchetype
            // in DeriveArchetype below), and their weapon sets are disjoint — so reading one for the
            // other meant neither acidgr nor molotov ever detonated on contact.
            def.bumc = AttrBool(phis, "bumc");

            // ── <vis> ────────────────────────────────────────────────────────
            string vis = Node(body, "vis") ?? "";

            def.vbul           = Attr(vis, "vbul", "");
            // visexpl lives on the same <vis> node as vbul (Weapon.as:623-625 reads
            // `param1.@visexpl` off the weapon node), NOT on <char>. Five rows set it:
            // ttexpl, ttplaexpl, react, sparkle, eclipse.
            def.visExpl        = Attr(vis, "visexpl", "");
            def.springMode     = AttrI(vis, "spring",  1);
            def.bulletAnimated = AttrBool(vis, "bulanim");
            def.hasShell       = AttrBool(vis, "shell");
            // vis@phisbul is an ARCHETYPE selector, not a detonation flag: DeriveArchetype below reads
            // it to choose ProjectileArchetype.Explosive. It used to be consumed a second time by
            // ProjectileSpawner as `bumc`, which was wrong — see def.bumc above.
            def.isPhysBullet   = AttrBool(vis, "phisbul");
            def.shineRadius    = AttrI(vis, "shine", 500);

            // Decal: vis.@tipdec takes priority; fall back to weapon root tipdec
            int visTipdec = AttrI(vis, "tipdec", -1);
            int finalTipdec = visTipdec >= 0 ? visTipdec
                            : rootTipdec >= 0 ? rootTipdec
                            : 0;
            def.decalType = System.Enum.IsDefined(typeof(DecalType), finalTipdec)
                ? (DecalType)finalTipdec
                : DecalType.None;

            // ── <snd> ────────────────────────────────────────────────────────
            string snd = Node(body, "snd") ?? "";

            def.soundShoot  = Attr(snd, "shoot",  "");
            def.soundReload = Attr(snd, "reload", "");
            def.soundHit    = Attr(snd, "hit",    "");
            def.soundPrep   = Attr(snd, "prep",   "");
            def.soundPrepT1 = AttrI(snd, "t1",    0);
            def.soundPrepT2 = AttrI(snd, "t2",    0);
            // Default 0, NOT 600. AS3 declares `public var noise:int = 0` (Weapon.as:214) and only
            // ever assigns it from `getSndParam`'s `if(param1.@noise.length())` branch (:675), which
            // is reached from `node.snd[0]` (:528) — so a weapon that authors no `<snd>`, or a
            // `<snd>` with no `noise` attribute, is SILENT in the oracle (`:1579` then tests
            // `this.noise > 0` and does nothing).
            //
            // The old default of 600 made 57 of 213 baked weapons audible that the oracle keeps
            // silent, and the set is not random: every grenade and mine (`grenade`, `fgren`,
            // `hgren`, `spgren`, `molotov`, `dinamit`, `bomb`, `dbomb`, `mercgr`, the seven
            // `*mine` rows), every punch (`scorppunch`, `ship`), all six of dron's `ttweap1..6`,
            // and the four robot weapons that carry `noise` on `<char>` instead of `<snd>`
            // (`robolaser2`, `robospark2`, `dronplasma`, `dronpspark`). Measured with
            // docs/Diagnostics/scripts/diff_weapon_noise.py: 156 match, 57 too loud, 0 too quiet.
            //
            // A grenade being silent here is correct, not a gap: a throw is quiet and the
            // EXPLOSION is what carries, through Box.noiseDie (:855-857) rather than the weapon row.
            //
            // `<char noise='…'>` is deliberately NOT a fallback. It looks like a carrier — four
            // robot rows author `noise='400'` on it — but getCharParam (Weapon.as:777-860) has no
            // @noise branch at all, so the oracle never reads it. Copying it would invent 400 for
            // those four rows.
            def.noiseRadius = AttrF(snd, "noise", 0f);

            // The two that were missing. AS3 splits the <snd> block across THREE parsers, so reading
            // only the first one drops a throwable's landing sound and a mine's beeps silently:
            //   Weapon.getSndParam  (Weapon.as:641-680)  shoot shoot_n reload hit prep t1 t2 noise
            //   WThrow              (WThrow.as:64-66)    fall   — `node.snd[0].@fall`
            //   Mine                (Mine.as:112-119)    dem sens
            // `shoot_n` and `dem` are the other two names in that set and are still not imported, for
            // the measured reasons recorded on WeaponDefinition (zero occurrences in the data / no
            // reachable consumer). That is the complete set of eleven; the port now reads nine.
            def.soundFall = Attr(snd, "fall", "");
            def.soundSens = Attr(snd, "sens", "");

            // ── <ammo> ───────────────────────────────────────────────────────
            // Multiple <ammo> nodes possible (variant 2 overrides). Use first.
            var ammoNodes = AllNodes(body, "ammo");
            string ammo = ammoNodes.Count > 0 ? ammoNodes[0] : "";

            def.magazineSize = AttrI(ammo, "holder",  0);
            def.reloadTime   = AttrF(ammo, "reload",  0f);

            // Magic's TWO costs, and which attribute is which. AS3 `Weapon.getAmmoParam`
            // (Weapon.as:885-891) maps them separately:
            //     ammo@magic -> this.magic / this.dmagic   (the regenerating mana BUDGET, Unit.mana)
            //     ammo@mana  -> this.mana  / this.dmana    (the mana ORGAN, Pers.manaHP)
            // WMagic.shoot() then spends `dmagic` from the budget and wounds the organ by `dmana`
            // (WMagic.as:110-122). This used to read only `mana` — into `manaCost`, which no
            // consumer ever read — so every magic weapon in the game cost nothing: `magic` (500 on
            // fireball, 800 on eclipse) was dropped outright and `magicPoolCost` / `manaHealthCost`
            // stayed 0. See docs/OnWeaponsSystemImplementation/13_WeaponTypeBehaviourAudit_2026-09-27.md §6.
            def.magicPoolCost  = AttrF(ammo, "magic", 0f);
            def.manaHealthCost = AttrF(ammo, "mana",  0f);

            // <a> text node → ammo type ID
            def.ammoType = NodeText(body, "a");

            // ── <dop> — extra effect ─────────────────────────────────────────
            //
            // `probiv` is NOT a pierce probability, and it is not `pier`. AS3 reads it off the <dop>
            // node (Weapon.as:703-705) into `Weapon.probiv`, adds the ammo's own probiv
            // (Weapon.as:1786-1788) and clamps the sum to 1 on the bullet (Weapon.as:1681-1684). The
            // bullet then uses it as a PENETRATION BUDGET: `Bullet.run` does not stop a round whose
            // `probiv > 0 && damage > 0` (weapon/Bullet.as:533), and `Unit.damage()` spends that damage
            // down — `param3.damage -= _loc8_ / param3.probiv` (Unit.as:3646-3648) and the three-branch
            // decay at :3684-3696. `pier` is the separate flat armour-piercing figure
            // (Weapon.as:1672: `pier = this.pier + this.pierAdd + this.ammoPier`).
            //
            // These were conflated here: the dop's probiv was folded into `def.piercing` with
            // Mathf.Max, which made a penetration budget look like armour points AND — because the
            // projectile consumed `piercing` as a 0..1 chance — made every weapon carrying `@pier`
            // (5/30/50/70, i.e. 29 <char> entries) pierce 100% of the time once Clamp01 saw it.
            string dop = Node(body, "dop") ?? "";
            def.penetration = AttrF(dop, "probiv", 0f);

            // The on-hit status the <dop> node also carries. All three are PRESENCE-tested in the
            // oracle (Weapon.as:691-702), which is why `ch` defaults to 1 here: an absent attribute
            // means "always", not "never", and a port that read a missing attr as 0 would silently
            // disable every weapon's status effect. `damage` defaults to 0 because that IS the
            // oracle's field default (Weapon.as:240) — an effect with no payload is a real thing
            // (blindness, freezing), so 0 is correct rather than suspicious.
            def.dopEffect  = Attr(dop, "effect");
            def.dopDamage  = AttrF(dop, "damage", 0f);
            def.dopChance  = AttrF(dop, "ch", 1f);

            // ── <com> — the trader / quality block ───────────────────────────
            //
            // AS3 reads six attributes off the FIRST <com> child (Weapon.as:454-472). None were parsed
            // before, so `uniq` — the unique variant's drop weight — existed only on the loot side
            // (LootItemRow.UniqChance) and nowhere on the weapon itself.
            var comNodes = AllNodes(body, "com");
            string comT1 = comNodes.Count > 0 ? comNodes[0] : "";

            // uniq: `if(node.com[0].@uniq.length()) this.uniq = …` — a PRESENCE test, and the field's
            // AS3 default is -1 (Weapon.as:100), not 0. So the fallback here is -1 and the two values
            // stay distinct: -1 = "no unique variant, upgrading is refused" (updVariant guards on
            // `this.uniq < 0`, Weapon.as:895-900); 0 = "has one, never random loot". A `0` fallback
            // would silently make all 93 <com>-less weapons upgradable.
            def.uniq      = AttrF(comT1, "uniq", -1f);
            def.comPrice  = AttrF(comT1, "price",  0f);
            def.comWorth  = AttrF(comT1, "worth",  0f);
            def.comChance = AttrF(comT1, "chance", 0f);
            def.comStage  = AttrI(comT1, "stage",  0);
            // rep_eff is declared `= 1` in AS3 (Weapon.as:122) and assigned only on presence, so an
            // absent attribute keeps 1 rather than meaning "no repair".
            def.comRep    = AttrF(comT1, "rep",    1f);

            // ── The unique ("II") variant — the index-1 override ─────────────
            //
            // A weapon HAS a variant when its block carries a second <char> (Stand.as:212,
            // `weap.char.length() > 1`) — not when any single node repeats. The reading and the merge
            // rule live in WeaponVariantXml (runtime assembly) so the offline wall can exercise them:
            // this file is in PFE.Editor, which PFE.Tests does not reference, so a merge written here
            // would be untestable, and a wrong merge is silent by construction (an inherited stat
            // reading 0 instead of the base value).
            def.variant = WeaponVariantXml.Read(body, WeaponVariantXml.FirstVariant);

            // Report — never swallow — an override attribute the port has no reader for. Constraint 12:
            // an attribute with no matching field is dropped with no trace at all, which is how a whole
            // subsystem went missing before. Tallied rather than logged per weapon, because a full
            // import touches 71 variants and one line each would bury the count that matters.
            foreach (string name in WeaponVariantXml.UnreadAttributeNames(def.variant))
            {
                unreadTally.TryGetValue(name, out int seen);
                unreadTally[name] = seen + 1;
            }

            // ── Derive archetype ─────────────────────────────────────────────
            def.projectileArchetype = DeriveArchetype(
                (int)def.weaponType,
                def.vbul,
                def.springMode,
                def.bulletFlame,
                def.isPhysBullet,
                def.bulletNavod);
        }
    }
}
