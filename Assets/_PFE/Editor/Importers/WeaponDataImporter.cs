using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PFE.Data.Definitions;

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
    /// Multiple <char> nodes = weapon variants (tier 1, tier 2…). We import tier-1 only
    /// and store variant count for future use.
    /// </summary>
    public class WeaponDataImporter
    {
        private static readonly string AllDataPath = SourceImportPaths.AllDataAsPath;
        private static readonly string OutputPath =
            "Assets/_PFE/Data/Resources/Weapons";

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static string Attr(string src, string name, string fallback = "")
        {
            var m = Regex.Match(src, $@"{name}='([^']*)'");
            return m.Success ? m.Groups[1].Value : fallback;
        }

        private static float AttrF(string src, string name, float fallback = 0f)
        {
            var s = Attr(src, name);
            return string.IsNullOrEmpty(s) ? fallback : float.Parse(s);
        }

        private static int AttrI(string src, string name, int fallback = 0)
        {
            var s = Attr(src, name);
            return string.IsNullOrEmpty(s) ? fallback : int.Parse(s);
        }

        private static bool AttrBool(string src, string name) =>
            Regex.IsMatch(src, $@"{name}='1'");

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
                Debug.LogError($"[WeaponDataImporter] AllData.as not found at: {AllDataPath}");
                return;
            }

            if (!Directory.Exists(OutputPath))
                Directory.CreateDirectory(OutputPath);

            string all = File.ReadAllText(AllDataPath);
            int imported = 0, updated = 0, skipped = 0;

            // Match every <weapon …> … </weapon> block (or self-closing)
            var weaponBlocks = Regex.Matches(all,
                @"<weapon\s+id='([^']+)'([^>]*)>(.*?)</weapon>",
                RegexOptions.Singleline);

            Debug.Log($"[WeaponDataImporter] Found {weaponBlocks.Count} weapon blocks.");

            foreach (Match block in weaponBlocks)
            {
                string id        = block.Groups[1].Value;
                string rootAttrs = block.Groups[2].Value;
                string body      = block.Groups[3].Value;

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
            def.skillLevel  = AttrI(rootAttrs, "skill", 0);
            def.weaponLevel = AttrI(rootAttrs, "lvl",   0);
            def.alicornOnly = AttrBool(rootAttrs, "alicorn");

            // Root-level tipdec (melee weapons carry tipdec on the weapon tag itself)
            int rootTipdec = AttrI(rootAttrs, "tipdec", -1);

            // ── <char> — first node = tier-1 stats ──────────────────────────
            var charNodes = AllNodes(body, "char");
            string charT1 = charNodes.Count > 0 ? charNodes[0] : "";

            def.maxDurability       = AttrI(charT1, "maxhp",   100);
            def.baseDamage          = AttrF(charT1, "damage",  10f);
            def.rapid               = AttrF(charT1, "rapid",   10f);
            def.precision           = AttrF(charT1, "prec",    0f) * 40f; // AS3 scales by 40
            def.knockback           = AttrF(charT1, "knock",   0f);
            def.destroyTiles        = AttrF(charT1, "destroy", 0f);
            def.piercing            = AttrF(charT1, "pier",    0f);
            def.projectilesPerShot  = AttrI(charT1, "kol",     1);
            def.burstCount          = AttrI(charT1, "dkol",    0);
            def.explRadius          = AttrF(charT1, "expl",    0f);
            def.explosionDamage     = AttrF(charT1, "damexpl", 0f);
            def.prepFrames          = AttrI(charT1, "prep",    0); // wind-up frames (minigun=32, flamer=10, etc.)

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

            // ── <vis> ────────────────────────────────────────────────────────
            string vis = Node(body, "vis") ?? "";

            def.vbul           = Attr(vis, "vbul", "");
            def.springMode     = AttrI(vis, "spring",  1);
            def.bulletAnimated = AttrBool(vis, "bulanim");
            def.hasShell       = AttrBool(vis, "shell");
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
            def.manaCost     = AttrF(ammo, "mana",    0f);

            // <a> text node → ammo type ID
            def.ammoType = NodeText(body, "a");

            // ── <dop> — extra effect ─────────────────────────────────────────
            string dop = Node(body, "dop") ?? "";
            float dopProbiv = AttrF(dop, "probiv", 0f);
            if (dopProbiv > 0f) def.piercing = Mathf.Max(def.piercing, dopProbiv);

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
