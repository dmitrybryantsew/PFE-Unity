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
                    ApplyWeaponData(def, id, rootAttrs, body);

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
        }

        // ── Per-weapon population ───────────────────────────────────────────────

        private static void ApplyWeaponData(WeaponDefinition def,
            string id, string rootAttrs, string body)
        {
            // ── Root tag attributes ──────────────────────────────────────────
            def.weaponId    = id;
            def.weaponType  = (WeaponType)AttrI(rootAttrs, "tip", 0);

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
            def.skillLevel  = AttrI(rootAttrs, "skill", 0);
            def.weaponLevel = AttrI(rootAttrs, "lvl",   0);
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
            def.destroyTiles        = AttrF(charT1, "destroy", 0f);
            def.piercing            = AttrF(charT1, "pier",    0f);   // flat armour points, not a chance
            def.projectilesPerShot  = AttrI(charT1, "kol",     1);
            def.burstCount          = AttrI(charT1, "dkol",    0);
            def.explRadius          = AttrF(charT1, "expl",    0f);
            def.explosionDamage     = AttrF(charT1, "damexpl", 0f);
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
            def.noiseRadius = AttrF(snd, "noise", 600f);

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
