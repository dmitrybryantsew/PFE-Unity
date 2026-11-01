using System;
using PFE.Systems.Inventory;
using PFE.Systems.Weapons;

namespace PFE.Systems.Crafting
{
    /// <summary>
    /// The workbench's repair arithmetic — <c>PipPageWork.as:554-613</c>, <c>Invent.repWeapon</c>
    /// (<c>Invent.as:857-876</c>) and <c>Armor.repair</c> (<c>Armor.as:340-348</c>).
    ///
    /// <para><b>Both repairs are "percent of max hp per component", and the percentage is what the list
    /// shows.</b> A weapon repairs <c>maxhp · repairMult · rep_eff · 0.25</c> for one <c>frag</c>; an
    /// armour repairs <c>maxhp · repairMult / kolComp</c> for one component. The list's third column is
    /// the same expression without <c>maxhp</c> (<c>:312-313</c>), which is why the row reads
    /// <c>rep_eff · 0.25 · repairMult</c> as a percentage.</para>
    /// </summary>
    public static class RepairRules
    {
        /// <summary>The weapon repair's fixed fraction — <c>inv.repWeapon(w, 0.25)</c>
        /// (<c>PipPageWork.as:583</c>).</summary>
        public const float WeaponRepairFraction = 0.25f;

        /// <summary>The owl's fixed repair budget — <c>this.owlRep = 100</c> (<c>:18</c>), spent as
        /// <c>owlRep * repairMult</c> (<c>:600</c>).</summary>
        public const int OwlRepairBudget = 100;

        /// <summary>
        /// The item a weapon repair consumes — <c>inv.checkKol("frag")</c> (<c>PipPageWork.as:580</c>).
        /// Hard-coded in the oracle too: every weapon, whatever its type, costs one fragment.
        /// </summary>
        public const string WeaponComponentId = "frag";

        /// <summary>
        /// Whether the weapon row is listed at all — <c>w.tip != 0 &amp;&amp; w.tip != 4 &amp;&amp;
        /// w.respect != 1 &amp;&amp; w.hp &lt; w.maxhp</c> (<c>PipPageWork.as:241</c>).
        ///
        /// <para><b>tip 0 and tip 4 are excluded for two different reasons.</b> tip 0 is the base
        /// <c>Weapon</c> class — turrets, drone lasers — which has no owner to repair it; tip 4 is a
        /// thrown weapon, which wears out by being thrown away. <c>respect == 1</c> (hidden) is excluded
        /// because a hidden weapon is not carried.</para>
        /// </summary>
        public static bool IsWeaponRepairable(int weaponTip, WeaponRespect respect, float hp, float maxHp)
        {
            return weaponTip != 0 && weaponTip != 4 && respect != WeaponRespect.Hidden && hp < maxHp;
        }

        /// <summary>
        /// <c>Invent.repWeapon</c>'s own gate (<c>Invent.as:860</c>): a weapon must be tip 1..3 and have
        /// a positive <c>rep_eff</c>. The page can list a weapon the repair call then refuses — the two
        /// tests are not the same, and the oracle keeps both.
        /// </summary>
        public static bool CanRepairWeapon(int weaponTip, float repEff)
        {
            return weaponTip > 0 && weaponTip < 4 && repEff > 0f;
        }

        /// <summary>How much hp one component restores to a weapon —
        /// <c>maxhp · repairMult · rep_eff · 0.25</c>.</summary>
        public static float WeaponRepairAmount(float maxHp, float repairMult, float repEff)
        {
            return maxHp * repairMult * repEff * WeaponRepairFraction;
        }

        /// <summary>The row's third column for a weapon: <c>rep_eff · 0.25</c>, which the view renders as
        /// <c>round(rep · repairMult · 1000)/10 %</c> (<c>PipPageWork.as:249</c>, <c>:313</c>).</summary>
        public static float WeaponRepairFractionOfMax(float repEff)
        {
            return repEff * WeaponRepairFraction;
        }

        /// <summary>
        /// Whether the armour row is listed — <c>!a.norep &amp;&amp; !a.und &amp;&amp; a.hp &lt; a.maxhp</c>
        /// (<c>PipPageWork.as:258</c>). <c>und</c> is <c>@tre</c>-adjacent "indestructible"; the port
        /// carries it as <see cref="PFE.Data.Definitions.ItemDefinition.armorIndestructible"/>.
        /// </summary>
        public static bool IsArmorRepairable(bool noRepair, bool indestructible, float hp, float maxHp)
        {
            return !noRepair && !indestructible && hp < maxHp;
        }

        /// <summary>How much hp one component restores to an armour —
        /// <c>maxhp · repairMult / kolComp</c> (<c>PipPageWork.as:568</c>).</summary>
        public static float ArmorRepairAmount(float maxHp, float repairMult, int kolComp)
        {
            int comp = kolComp < 1 ? 1 : kolComp;
            return maxHp * repairMult / comp;
        }

        /// <summary>The row's third column for an armour: <c>1 / kolComp</c> (<c>:266</c>).</summary>
        public static float ArmorRepairFractionOfMax(int kolComp)
        {
            int comp = kolComp < 1 ? 1 : kolComp;
            return 1f / comp;
        }

        /// <summary>
        /// The percentage a row prints — <c>Math.round(rep * repairMult * 1000) / 10</c>
        /// (<c>PipPageWork.as:313</c>). One decimal, rounded, as a <b>percent</b> (not a fraction).
        ///
        /// <para><b><c>Math.Floor(x + 0.5)</c> is not a quirk, it is <c>Math.round</c>.</b> AS3 rounds
        /// halves towards <b>+∞</b>; C#'s <c>Math.Round(double)</c> rounds halves <b>to even</b>. The two
        /// disagree on every exact half, and exact halves are not rare here: the default
        /// <c>repairMult</c> is <b>0.25</b>, so a quarter-damaged weapon is
        /// <c>0.25 * 0.25 * 1000 = 62.5</c> — AS3 prints <b>6.3 %</b>, banker's rounding prints 6.2.
        /// The offline fixture <c>DisplayPercent_IsOneDecimalRounded</c> pins that exact case.</para>
        /// </summary>
        public static float DisplayPercent(float fractionOfMax, float repairMult)
        {
            return (float)Math.Floor(fractionOfMax * repairMult * 1000.0 + 0.5) / 10f;
        }
    }

    /// <summary>
    /// The upgrade tab's two rules — <c>PipPageWork.as:159-210</c> (which rows are listed) and
    /// <c>:514-552</c> (what a click does).
    /// </summary>
    public static class UpgradeRules
    {
        /// <summary>
        /// AS3 <c>Weapon.skill</c> for a scrap gun — the only weapons the upgrade tab lists
        /// (<c>PipPageWork.as:187</c>: <c>weap.skill == 3</c>).
        ///
        /// <para><b>3 is a weapon <i>category</i>, not a skill index.</b> The seven shipped rows with
        /// <c>@skill='3'</c> are exactly the workbench's own products — <c>arson</c>, <c>dartgun</c>,
        /// <c>acidgun</c>, <c>sawgun</c>, <c>buckshot</c>, <c>railway</c>, <c>friend</c> — which is why
        /// the tab is "upgrade the gun you built" and not "upgrade any gun". The port carries the
        /// attribute as <c>WeaponDefinition.skillLevel</c> (<c>WeaponDataImporter.cs:344</c> reads
        /// <c>@skill</c> into it), despite the field's name.</para>
        /// </summary>
        public const int ScrapWeaponSkill = 3;

        /// <summary>Whether an armour may be upgraded — <c>lvl &gt;= 0 &amp;&amp; lvl &lt; maxlvl &amp;&amp;
        /// lvl &lt; pers.maxArmorLvl</c> (<c>PipPageWork.as:170</c>).
        ///
        /// <para><b><c>maxArmorLvl</c> is the perk gate.</b> <c>Pers.maxArmorLvl</c> defaults to 0
        /// (<c>Pers.as:253</c>), so with no perk the upgrade tab is empty — that is the shipped
        /// behaviour, not a broken list.</para>
        /// </summary>
        public static bool CanUpgradeArmor(int level, int maxLevel, int maxArmorLevel)
        {
            return level >= 0 && level < maxLevel && level < maxArmorLevel;
        }

        /// <summary>Whether a weapon may be upgraded — <c>skill == 3 &amp;&amp; variant == 0 &amp;&amp;
        /// respect != 3</c> (<c>PipPageWork.as:187</c>).</summary>
        public static bool CanUpgradeWeapon(int skill, int variant, WeaponRespect respect)
        {
            return skill == ScrapWeaponSkill && variant == 0 && respect != WeaponRespect.SchemeOnly;
        }

        /// <summary>
        /// <c>Armor.needComp()</c> — <c>xml.upd[lvl + 1].@kol</c> (<c>Armor.as:350-357</c>), or 0 when
        /// there is no next level. Read off the armour's <c>&lt;upd&gt;</c> children by
        /// <see cref="ArmourCraftTable"/>.
        /// </summary>
        public static int NeedComponent(ArmourCraftFacts facts, int level)
        {
            return facts.ComponentCountForNextLevel(level);
        }
    }

    /// <summary>
    /// An armour's crafting facts — the three attributes the importer's omission list named
    /// "crafting subsystem" (<c>ArmourDataParser.cs:236-237</c>, <c>:249</c>): <c>@comp</c>,
    /// <c>@kolcomp</c> and each <c>&lt;upd kol&gt;</c>.
    /// </summary>
    public readonly struct ArmourCraftFacts
    {
        /// <summary>AS3 <c>Armor.idComp</c> — <c>@comp</c>, or <c>id + "_comp"</c> when the row has none
        /// (<c>Armor.as:147-151</c>). 18 of the 35 armour rows carry <c>@comp</c>; the other 17 rely on
        /// the fallback.</summary>
        public readonly string ComponentId;

        /// <summary>AS3 <c>Armor.kolComp</c> — <c>@kolcomp</c>, default 1 (<c>Armor.as:80</c>). The
        /// divisor of the repair amount.</summary>
        public readonly int ComponentCount;

        /// <summary>Each <c>&lt;upd&gt;</c> child's <c>@kol</c>, <b>indexed by child position</b>:
        /// <c>UpgradeKol[i] == xml.upd[i].@kol</c>. Not shifted, not padded — <c>needComp()</c> reads
        /// <c>upd[lvl + 1]</c> (<c>Armor.as:350-355</c>), so the caller adds the one, not the reader.
        /// <c>upd[0]</c> carries no <c>kol</c> in the shipped data and so reads 0, which is what makes
        /// level 0 free.</summary>
        public readonly int[] UpgradeKol;

        public ArmourCraftFacts(string componentId, int componentCount, int[] upgradeKol)
        {
            ComponentId = componentId ?? string.Empty;
            ComponentCount = componentCount < 1 ? 1 : componentCount;
            UpgradeKol = upgradeKol ?? Array.Empty<int>();
        }

        /// <summary><c>xml.upd[lvl + 1].@kol</c> — 0 when the armour has no such level.</summary>
        public int ComponentCountForNextLevel(int level)
        {
            int next = level + 1;
            if (next < 0 || next >= UpgradeKol.Length) return 0;
            return UpgradeKol[next];
        }
    }

    /// <summary>
    /// Reads <see cref="ArmourCraftFacts"/> out of <c>AllData.as</c>.
    ///
    /// <para><b>Why the runtime and not the asset.</b> <see cref="PFE.Data.Definitions.ItemDefinition"/>
    /// has no <c>comp</c>/<c>kolcomp</c> field and <c>ArmourDataParser</c> deliberately ignores all three
    /// attributes; adding them is an editor change that only takes effect after a Full Import, which is
    /// owner-only here. Reading the row at runtime is the same seam the port already uses for weapon
    /// rows and <c>&lt;sk&gt;</c> runs, and it needs no import. When the asset fields land, this class
    /// retires and nothing else changes.</para>
    /// </summary>
    public static class ArmourCraftTable
    {
        private const string ArmourTag = "armor";
        private const string UpdTag = "upd";

        /// <summary>
        /// The facts for one armour id, or <c>false</c> when the document has no such row. A missing row
        /// is reported rather than defaulted: a default component id of <c>""</c> would make
        /// <c>checkKol("")</c> false forever and silently disable repair and upgrade for that armour.
        /// </summary>
        public static bool TryRead(string allData, string armourId, out ArmourCraftFacts facts)
        {
            facts = default;
            if (string.IsNullOrEmpty(allData) || string.IsNullOrEmpty(armourId)) return false;

            if (!AllDataElements.TryGet(allData, ArmourTag, armourId, out AllDataElement element))
                return false;

            string comp = WeaponXmlAttrs.Attr(element.Attrs, "comp");
            if (string.IsNullOrEmpty(comp)) comp = armourId + "_comp";

            int kolComp = WeaponXmlAttrs.AttrI(element.Attrs, "kolcomp", 1);

            var kols = new System.Collections.Generic.List<int>();
            foreach (AllDataElement upd in AllDataElements.Children(element.Body, UpdTag))
            {
                kols.Add(WeaponXmlAttrs.AttrI(upd.Attrs, "kol", 0));
            }

            facts = new ArmourCraftFacts(comp, kolComp, kols.ToArray());
            return true;
        }
    }
}
