using System;
using System.Collections.Generic;
using PFE.Systems.Inventory;
using PFE.Systems.Items;

namespace PFE.Systems.Crafting
{
    /// <summary>Why a craft was refused — the oracle's <c>infoText</c> keys, one to one.</summary>
    public enum CraftRefusal
    {
        /// <summary>Nothing to report; the craft may proceed.</summary>
        None = 0,

        /// <summary><c>pip.noAct</c> — the device is in a state that consumes no actions
        /// (<c>PipPageWork.as:413-417</c>).</summary>
        NoAction,

        /// <summary><c>needSkill</c> — <c>@skill</c>/<c>@lvl</c> not met (<c>:374-378</c>).</summary>
        NotEnoughSkill,

        /// <summary><c>noMaterials</c> — a <c>&lt;craft&gt;</c> line is short (<c>:381-386</c>).</summary>
        MissingMaterials,

        /// <summary>The row was clicked but the product's state refuses the craft even though the
        /// recipe is satisfied — a weapon that is neither thrown nor scheme-only, or an armour that is
        /// already built (<c>:445-448</c>, <c>:470-473</c>). The oracle <i>returns silently</i> here;
        /// naming it is the port's addition and changes no visible behaviour.</summary>
        NotCraftable
    }

    /// <summary>Which table the product comes from — the oracle's <c>Item.L_*</c> strings, which are
    /// also what the info pane is asked to render (<c>PipPageWork.as:324</c>).</summary>
    public enum CraftRowKind
    {
        Item = 0,
        Weapon = 1,
        Armor = 2,
        Implant = 3
    }

    /// <summary>
    /// What the bag knows about a recipe's product. Built by the view from the three inventory tables
    /// plus the product's own <c>&lt;item&gt;</c> row; consumed here so the three skip rules are
    /// testable without an inventory.
    ///
    /// <para><b>Every field is a fact the oracle reads, and nothing else.</b> <c>IsWeapon</c>/
    /// <c>IsArmor</c> are <c>inv.weapons[wid]</c>/<c>inv.armors[wid]</c>; <c>ItemTip</c> is the product's
    /// own row (<c>AllData.d.item.(@id == wid)</c>), which is <b>absent</b> for a weapon or armour id
    /// because those live in other tables.</para>
    /// </summary>
    public readonly struct CraftTargetFacts
    {
        /// <summary><c>inv.weapons[wid] != null</c>.</summary>
        public readonly bool IsWeapon;

        /// <summary><c>inv.armors[wid] != null</c>.</summary>
        public readonly bool IsArmor;

        /// <summary>AS3 <c>Weapon.tip</c>: 0 base/ranged, 1 melee, 2 small gun, 3 big gun, 4 thrown,
        /// 5 magic. <b>4</b> is the one the create page branches on. -1 when not a weapon.</summary>
        public readonly int WeaponTip;

        /// <summary>AS3 <c>Weapon.respect</c> as the port's <see cref="WeaponRespect"/>:
        /// 0 active, 1 hidden, 2 favourited, <b>3 scheme-only</b>.</summary>
        public readonly WeaponRespect WeaponRespect;

        /// <summary>AS3 <c>Armor.lvl</c>. <b>&lt; 0</b> means "known but not built", which is the only
        /// state the create page lists. 0 when not an armour.</summary>
        public readonly int ArmorLevel;

        /// <summary>The product's own <c>&lt;item&gt;</c> <c>@tip</c>, or empty when the product has no
        /// item row (every weapon and armour) — <c>AllData.d.item.(@id == wid).length()</c>.</summary>
        public readonly string ItemTip;

        /// <summary>The product's <c>@one</c> flag: a one-off, of which the player may hold only one
        /// (<c>PipPageWork.as:128</c>).</summary>
        public readonly bool ItemOne;

        /// <summary>How many the bag holds of the product id — <c>inv.items[wid].kol</c>, 0 when the row
        /// is absent.</summary>
        public readonly int Have;

        public CraftTargetFacts(bool isWeapon, bool isArmor, int weaponTip,
                                WeaponRespect weaponRespect, int armorLevel,
                                string itemTip, bool itemOne, int have)
        {
            IsWeapon = isWeapon;
            IsArmor = isArmor;
            WeaponTip = weaponTip;
            WeaponRespect = weaponRespect;
            ArmorLevel = armorLevel;
            ItemTip = itemTip ?? string.Empty;
            ItemOne = itemOne;
            Have = have;
        }
    }

    /// <summary>One row of the create list — the oracle's anonymous <c>{tip,id,nazv,ok,sort,kol}</c>
    /// object (<c>PipPageWork.as:94-137</c>), minus the display name the view supplies.</summary>
    public readonly struct CraftRowView
    {
        /// <summary>The <b>product</b> id (<c>wid</c>), not the scheme id. Clicks are keyed on it and
        /// re-derive the scheme with <c>"s_" + id</c> (<c>:423</c>).</summary>
        public readonly string Id;

        public readonly CraftRowKind Kind;

        /// <summary><b>1</b> craftable, <b>2</b> skill too low. The list is sorted on this first, so a
        /// refused row sinks below every craftable one (<c>:149</c>).</summary>
        public readonly int Ok;

        /// <summary><c>@skill + @lvl</c> concatenated as <b>strings</b>, exactly as the oracle does
        /// (<c>:99</c>). Sorting that as a string puts <c>repair1</c> before <c>repair10</c>, which is
        /// the shipped order and not a bug to fix.</summary>
        public readonly string SortKey;

        /// <summary>How many the player already holds, or 0. Drawn in the row's second column
        /// (<c>:303-306</c>).</summary>
        public readonly int Have;

        public CraftRowView(string id, CraftRowKind kind, int ok, string sortKey, int have)
        {
            Id = id ?? string.Empty;
            Kind = kind;
            Ok = ok;
            SortKey = sortKey ?? string.Empty;
            Have = have;
        }

        /// <summary>The scheme id this row's craft is keyed on — <c>"s_" + id</c>.</summary>
        public string SchemeId => CraftingRecipeTable.SchemePrefix + Id;
    }

    /// <summary>
    /// The create page's two decisions: <b>is this row listed</b> (<c>PipPageWork.as:75-146</c>) and
    /// <b>may this craft run</b> (<c>:371-397</c>).
    ///
    /// <para><b>They are different questions and the oracle keeps them apart.</b> A row with
    /// <c>ok == 2</c> is listed and refuses on click with <c>needSkill</c>; a row whose materials are
    /// short is listed and refuses with <c>noMaterials</c>. Neither is filtered out of the list, which
    /// is why a player can see what they cannot yet make.</para>
    /// </summary>
    public static class CraftingRules
    {
        /// <summary>AS3 <c>Item.L_IMPL</c> — an implant, which is never listed once held
        /// (<c>PipPageWork.as:128</c>).</summary>
        public const string ImplantTip = "impl";

        /// <summary>
        /// Builds the row for one recipe, or null when the oracle would not list it.
        ///
        /// <para><b>The three shapes and their three skip rules</b> (<c>PipPageWork.as:90-142</c>):</para>
        /// <list type="bullet">
        /// <item>a <b>weapon</b> is listed only when it is scheme-only (<c>respect == 3</c>) or thrown
        /// (<c>tip == 4</c>) — i.e. the recipe's job is to unlock a known weapon or to stack a
        /// grenade;</item>
        /// <item>an <b>armour</b> only while <c>lvl &lt; 0</c> — its job is to make it real;</item>
        /// <item>an <b>item</b> unless it is an implant or a one-off the player already has.</item>
        /// </list>
        /// <para>A product that answers none of the three tables is not listed at all: the oracle's
        /// <c>node1.length() != 0</c> guard (<c>:126</c>).</para>
        /// </summary>
        public static CraftRowView? Admit(in CraftingRecipe recipe, in CraftTargetFacts facts, int skillLevel)
        {
            int ok = recipe.HasSkillGate && skillLevel < recipe.SkillLevel ? 2 : 1;
            string sortKey = recipe.Skill + recipe.SkillLevel.ToString();

            if (facts.IsWeapon)
            {
                bool schemeOnly = facts.WeaponRespect == WeaponRespect.SchemeOnly;
                bool thrown = facts.WeaponTip == 4;
                if (!schemeOnly && !thrown) return null;

                return new CraftRowView(recipe.OutputId, CraftRowKind.Weapon, ok, sortKey,
                    facts.Have > 0 ? facts.Have : 0);
            }

            if (facts.IsArmor)
            {
                if (facts.ArmorLevel >= 0) return null;
                return new CraftRowView(recipe.OutputId, CraftRowKind.Armor, ok, sortKey, 0);
            }

            if (string.IsNullOrEmpty(facts.ItemTip)) return null;

            bool implant = facts.ItemTip == ImplantTip;
            if ((implant || facts.ItemOne) && facts.Have > 0) return null;

            return new CraftRowView(recipe.OutputId,
                implant ? CraftRowKind.Implant : CraftRowKind.Item, ok, sortKey, facts.Have);
        }

        /// <summary>
        /// The list order — <c>arr.sortOn(["ok","sort"])</c> (<c>PipPageWork.as:149</c>): numeric on
        /// <c>ok</c>, then <b>string</b> on the sort key.
        ///
        /// <para><b>The key comparison is ordinal, and that is the point.</b> <c>sort</c> is
        /// <c>node.@skill + node.@lvl</c> (<c>:99</c>, <c>:118</c>, <c>:136</c>) — two attributes
        /// <i>concatenated</i>, so it is text, not a number: <c>"repair1"</c> sorts <b>before</b>
        /// <c>"repair10"</c> because it is a prefix. <c>sortOn</c> with no options compares code units,
        /// which is what <c>string.CompareOrdinal</c> does. Do not "fix" this into a numeric compare —
        /// that would reorder real rows.</para>
        ///
        /// <para><b>The id tie-break is a deliberate divergence.</b> The oracle leaves rows with equal
        /// <c>ok</c> and equal <c>sort</c> in whatever order <c>sortOn</c> leaves them, and Flash's sort
        /// is not stable; <c>List.Sort</c> is not either. Without a final key the same inventory could
        /// come back in a different order on the next repaint — rows would swap under a hovering cursor.
        /// Pinning the id costs nothing and the oracle never specified the other behaviour.</para>
        /// </summary>
        public static int CompareRows(in CraftRowView a, in CraftRowView b)
        {
            if (a.Ok != b.Ok) return a.Ok < b.Ok ? -1 : 1;

            int byKey = string.CompareOrdinal(a.SortKey, b.SortKey);
            if (byKey != 0) return byKey;

            return string.CompareOrdinal(a.Id, b.Id);
        }

        /// <summary>Sorts a list of rows in the oracle's order.</summary>
        public static void SortRows(List<CraftRowView> rows)
        {
            if (rows == null || rows.Count < 2) return;
            rows.Sort((a, b) => CompareRows(a, b));
        }

        /// <summary>
        /// <c>checkScheme</c> (<c>PipPageWork.as:371-388</c>): skill first, then materials.
        ///
        /// <para><b>The material test counts the vault, everywhere.</b> <c>kol + vault &gt;= kol</c>, with
        /// no <c>loc.base</c> gate — unlike <c>Invent.checkKol</c> (<c>Invent.as:1413-1428</c>), which
        /// only counts the vault at base. That asymmetry is the oracle's: crafting at a bench can spend
        /// the vault's stock from anywhere, and <c>minusItem</c> does exactly that when the bag is short
        /// (<c>Invent.as:1385-1392</c>).</para>
        /// </summary>
        /// <param name="available">Count of an item id <b>including</b> its vault quantity.</param>
        public static CraftRefusal Check(in CraftingRecipe recipe, int skillLevel, Func<string, int> available)
        {
            if (recipe.HasSkillGate && skillLevel < recipe.SkillLevel) return CraftRefusal.NotEnoughSkill;

            for (int i = 0; i < recipe.Materials.Length; i++)
            {
                CraftMaterialRow material = recipe.Materials[i];
                int have = available != null ? available(material.Id) : 0;
                if (have < material.Count) return CraftRefusal.MissingMaterials;
            }

            return CraftRefusal.None;
        }

        /// <summary>
        /// Whether a weapon row can be crafted on click — <c>if (w.tip != 4 &amp;&amp; w.respect != 3)
        /// return;</c> (<c>PipPageWork.as:445-448</c>). Same predicate as the list, applied again because
        /// a weapon can leave the scheme-only state between the list build and the click.
        /// </summary>
        public static bool CanCraftWeapon(int weaponTip, WeaponRespect respect)
        {
            return weaponTip == 4 || respect == WeaponRespect.SchemeOnly;
        }
    }
}
