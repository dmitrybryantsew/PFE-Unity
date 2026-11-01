using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Systems.Inventory;
using PFE.Systems.Items;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Systems.Crafting
{
    /// <summary>What one workbench action did — one value per <c>infoText</c> key the oracle can raise.</summary>
    public enum WorkbenchVerb
    {
        None = 0,
        Created,            // "created" / "created2" / "created3" / "created4"
        NotEnoughSkill,     // "needSkill"
        MissingMaterials,   // "noMaterials"
        NotCraftable,       // the oracle's silent return
        NoRepair,           // "noRepair"
        NoRepair2,          // "noRepair2" — a weapon with no rep_eff
        NoRecipe,
        UpArmor             // "upArmor"
    }

    /// <summary>One action's outcome, ready for the page to render.</summary>
    public readonly struct WorkbenchResult
    {
        public readonly WorkbenchVerb Verb;

        /// <summary>The oracle's <c>infoText</c> key, or empty for the silent paths.</summary>
        public readonly string MessageKey;

        /// <summary>First interpolation argument (the product's name, the skill id, …).</summary>
        public readonly string Arg1;

        /// <summary>Second argument (the new count, the required level, …).</summary>
        public readonly string Arg2;

        public WorkbenchResult(WorkbenchVerb verb, string messageKey = "", string arg1 = "", string arg2 = "")
        {
            Verb = verb;
            MessageKey = messageKey ?? string.Empty;
            Arg1 = arg1 ?? string.Empty;
            Arg2 = arg2 ?? string.Empty;
        }

        public bool Succeeded => Verb == WorkbenchVerb.Created || Verb == WorkbenchVerb.UpArmor;

        public static WorkbenchResult Of(WorkbenchVerb verb, string key = "", string a1 = "", string a2 = "")
            => new WorkbenchResult(verb, key, a1, a2);
    }

    /// <summary>A row of the repair list — <c>PipPageWork.as:237-271</c>.</summary>
    public readonly struct RepairCandidate
    {
        public readonly string Id;
        public readonly CraftRowKind Kind;
        public readonly float Hp;
        public readonly float MaxHp;

        /// <summary>The fraction of max hp one component restores, <b>before</b> <c>repairMult</c> —
        /// the oracle's <c>rep</c> field (<c>:249</c>, <c>:266</c>).</summary>
        public readonly float FractionOfMax;

        /// <summary>The item id a repair consumes — <c>frag</c> for a weapon, the armour's
        /// <c>idComp</c> for an armour.</summary>
        public readonly string ComponentId;

        public RepairCandidate(string id, CraftRowKind kind, float hp, float maxHp,
                               float fractionOfMax, string componentId)
        {
            Id = id ?? string.Empty;
            Kind = kind;
            Hp = hp;
            MaxHp = maxHp;
            FractionOfMax = fractionOfMax;
            ComponentId = componentId ?? string.Empty;
        }
    }

    /// <summary>A row of the upgrade list — <c>PipPageWork.as:166-198</c>.</summary>
    public readonly struct UpgradeCandidate
    {
        public readonly string Id;
        public readonly CraftRowKind Kind;
        public readonly int Level;

        /// <summary>How many components the next level costs — <c>needComp()</c>. 0 for a weapon, whose
        /// cost is its own scheme's materials.</summary>
        public readonly int Cost;

        /// <summary>The armour's <c>idComp</c>; empty for a weapon.</summary>
        public readonly string ComponentId;

        public UpgradeCandidate(string id, CraftRowKind kind, int level, int cost, string componentId)
        {
            Id = id ?? string.Empty;
            Kind = kind;
            Level = level;
            Cost = cost;
            ComponentId = componentId ?? string.Empty;
        }
    }

    /// <summary>
    /// The workbench's runtime — the port of <c>fe.inter.PipPageWork</c>'s three click handlers, with the
    /// page stripped off.
    ///
    /// <para><b>Why the rules and the mutations are in two places.</b> <see cref="CraftingRules"/> and
    /// <see cref="RepairRules"/> are pure functions in <c>PFE.Sim</c>, so the offline wall can exercise
    /// them without a Unity runtime. This class is the half that touches <see cref="GameInventory"/> and
    /// the content assets — it decides <i>which</i> pure rule applies, applies the bag mutations, and
    /// returns a message key. The page owns pixels and nothing else.</para>
    ///
    /// <para><b>Material removal dips into the vault, deliberately.</b> <c>minusItem</c>
    /// (<c>Invent.as:1373-1411</c>) does that unconditionally, and <c>checkScheme</c> counts the vault at
    /// any location — so crafting at a bench can spend the base's stock from the field. That is the
    /// oracle, and it is why <see cref="RemoveMaterial"/> passes <c>allowVault: true</c>.</para>
    /// </summary>
    public sealed class WorkbenchService
    {
        private readonly Func<GameInventory> _resolveInventory;
        private readonly string _allData;
        private readonly Dictionary<string, ItemXmlRow> _itemRows;
        private List<CraftingRecipe> _recipes;

        /// <summary>
        /// <paramref name="inventory"/> is a <b>provider</b>, not the bag itself: the player is assembled
        /// in <c>PlayerController.Awake</c>, which is not ordered against the PipBuck bootstrapper's
        /// <c>Start</c>, so the bag does not exist yet when the page is built. The trade page's source
        /// takes the same shape for the same reason.
        /// </summary>
        public WorkbenchService(Func<GameInventory> inventory, string allData)
        {
            _resolveInventory = inventory;
            _allData = allData;
            _itemRows = BuildItemRowIndex(allData);
        }

        /// <summary>The live bag, or null before the player exists.</summary>
        public GameInventory Inventory => _resolveInventory?.Invoke();

        /// <summary><c>gg.pers.getSkillLevel(id)</c>. A provider for the same reason as the bag.</summary>
        public Func<string, int> SkillLevel { get; set; }

        /// <summary><c>gg.pers.repairMult</c> — the repair efficiency multiplier.</summary>
        public Func<float> RepairMultiplierProvider { get; set; }

        /// <summary><c>gg.pers.maxArmorLvl</c> — 0 with no perk, which empties the upgrade list.</summary>
        public Func<int> MaxArmorLevelProvider { get; set; }

        /// <summary><c>gg.pers.potmaster</c> — the perk that doubles a potion's output.</summary>
        public Func<bool> PotMasterProvider { get; set; }

        /// <summary><c>repairMult</c> with the oracle's own default of <b>0.25</b>
        /// (<c>Pers.as:249</c>) when no stats are wired.</summary>
        public float RepairMultiplier => RepairMultiplierProvider != null ? RepairMultiplierProvider() : 0.25f;

        /// <summary><c>maxArmorLvl</c>, defaulting to 0 — the oracle's default (<c>Pers.as:253</c>).</summary>
        public int MaxArmorLevel => MaxArmorLevelProvider != null ? MaxArmorLevelProvider() : 0;

        /// <summary><c>potmaster</c>, defaulting to false.</summary>
        public bool PotMaster => PotMasterProvider != null && PotMasterProvider();

        /// <summary>Every recipe in the document, built once.</summary>
        public List<CraftingRecipe> Recipes => _recipes ?? (_recipes = CraftingRecipeTable.Build(_allData));

        // ── Create ────────────────────────────────────────────────────────────

        /// <summary>
        /// The create list for a station — <c>PipPageWork.as:67-158</c>. Rows the oracle would not list
        /// are absent; rows it lists but would refuse are present with <c>Ok == 2</c>.
        /// </summary>
        public List<CraftRowView> BuildCraftRows(string workTip)
        {
            var rows = new List<CraftRowView>();
            if (Inventory == null) return rows;

            int skillOf(string skill) => SkillLevel != null ? SkillLevel(skill) : 0;

            foreach (CraftingRecipe recipe in Recipes)
            {
                if (!CraftingRecipeTable.StationAccepts(recipe.Station, workTip)) continue;

                // The scheme has to be in the bag, with a positive count (:77).
                GameItemInstance scheme = Inventory.GetItem(recipe.SchemeId);
                if (scheme == null || scheme.Quantity <= 0) continue;

                CraftTargetFacts facts = FactsFor(recipe.OutputId);
                CraftRowView? row = CraftingRules.Admit(recipe, facts, skillOf(recipe.Skill));
                if (row.HasValue) rows.Add(row.Value);
            }

            CraftingRules.SortRows(rows);
            return rows;
        }

        /// <summary>
        /// Craft one product — <c>PipPageWork.itemClick</c>'s <c>page2 == 1</c> arm (<c>:421-513</c>).
        /// </summary>
        public WorkbenchResult Craft(string outputId)
        {
            if (Inventory == null || string.IsNullOrEmpty(outputId)) return WorkbenchResult.Of(WorkbenchVerb.NoRecipe);

            CraftingRecipe? found = CraftingRecipeTable.FindByOutput(Recipes, outputId);
            if (!found.HasValue) return WorkbenchResult.Of(WorkbenchVerb.NoRecipe);
            CraftingRecipe recipe = found.Value;

            int skill = SkillLevel != null ? SkillLevel(recipe.Skill) : 0;
            CraftRefusal refusal = CraftingRules.Check(recipe, skill, Available);
            if (refusal == CraftRefusal.NotEnoughSkill)
                return WorkbenchResult.Of(WorkbenchVerb.NotEnoughSkill, "needSkill", recipe.Skill,
                    recipe.SkillLevel.ToString());
            if (refusal == CraftRefusal.MissingMaterials)
                return WorkbenchResult.Of(WorkbenchVerb.MissingMaterials, "noMaterials");

            int count = CraftingRecipeTable.OutputCount(recipe, PotMaster);

            GameWeaponInstance weapon = Inventory.GetWeapon(outputId);
            if (weapon != null)
            {
                int tip = weapon.WeaponDefinition != null ? (int)weapon.WeaponDefinition.weaponType : 0;
                if (!CraftingRules.CanCraftWeapon(tip, weapon.Respect))
                    return WorkbenchResult.Of(WorkbenchVerb.NotCraftable);

                RemoveMaterials(recipe);

                if (tip != 4)
                {
                    // :450-456 — the weapon becomes carried and its magazine is filled from `holder`.
                    weapon.Respect = WeaponRespect.Active;
                    int holder = weapon.WeaponDefinition != null ? weapon.WeaponDefinition.magazineSize : 0;
                    weapon.Reload(holder);
                    Inventory.CalculateWeaponMass();
                    return WorkbenchResult.Of(WorkbenchVerb.Created, "created");
                }

                // :457-464 — a thrown weapon is a stack of items, not an instance.
                ItemDefinition def = ResolveItem(outputId);
                if (def != null) Inventory.AddItem(def, count);
                Inventory.CalculateWeaponMass();
                return WorkbenchResult.Of(WorkbenchVerb.Created, "created2", outputId,
                    CountOf(outputId).ToString());
            }

            GameArmorInstance armor = Inventory.GetArmor(outputId);
            if (armor != null)
            {
                if (armor.Level >= 0) return WorkbenchResult.Of(WorkbenchVerb.NotCraftable);

                RemoveMaterials(recipe);
                armor.SetLevel(0);
                return WorkbenchResult.Of(WorkbenchVerb.Created, "created3");
            }

            // Item or implant. A product with no item row cannot be built — the oracle's list would
            // never have shown it (PipPageWork.as:126-141).
            ItemDefinition itemDef = ResolveItem(outputId);
            if (itemDef == null) return WorkbenchResult.Of(WorkbenchVerb.NoRecipe);

            bool implant = itemDef.legacyTip == CraftingRules.ImplantTip;
            RemoveMaterials(recipe);

            int amount = implant ? 1 : count;   // :482 — an implant is always one
            Inventory.AddItem(itemDef, amount);

            // :483 — `inv.takeScript(id)` starts the land's item script. The port has no per-land
            // itemScripts table, so the implant is granted and the script is not run. Named, not
            // silent: see docs/CraftingAndRepair/00_INDEX.md §3.
            return WorkbenchResult.Of(WorkbenchVerb.Created, implant ? "created4" : "created2",
                outputId, CountOf(outputId).ToString());
        }

        // ── Upgrade ───────────────────────────────────────────────────────────

        /// <summary>The upgrade list — <c>PipPageWork.as:159-210</c>.</summary>
        public List<UpgradeCandidate> BuildUpgradeRows()
        {
            var rows = new List<UpgradeCandidate>();
            if (Inventory == null) return rows;

            if (MaxArmorLevel > 0)
            {
                foreach (KeyValuePair<string, GameArmorInstance> pair in Inventory.Armors)
                {
                    GameArmorInstance armor = pair.Value;
                    if (armor == null) continue;
                    if (!UpgradeRules.CanUpgradeArmor(armor.Level, armor.MaxLevel, MaxArmorLevel)) continue;

                    ArmourCraftFacts facts = ArmourFacts(armor.Id);
                    rows.Add(new UpgradeCandidate(armor.Id, CraftRowKind.Armor, armor.Level,
                        facts.ComponentCountForNextLevel(armor.Level), facts.ComponentId));
                }
            }

            foreach (KeyValuePair<string, GameWeaponInstance> pair in Inventory.Weapons)
            {
                GameWeaponInstance weapon = pair.Value;
                if (weapon == null || weapon.WeaponDefinition == null) continue;
                if (!UpgradeRules.CanUpgradeWeapon(weapon.WeaponDefinition.skillLevel, weapon.Variant,
                        weapon.Respect)) continue;

                rows.Add(new UpgradeCandidate(weapon.ItemId, CraftRowKind.Weapon, weapon.Variant, 0, string.Empty));
            }

            return rows;
        }

        /// <summary>Upgrade one armour — <c>PipPageWork.as:516-536</c>.</summary>
        public WorkbenchResult UpgradeArmor(string armorId)
        {
            GameArmorInstance armor = Inventory?.GetArmor(armorId);
            if (armor == null) return WorkbenchResult.Of(WorkbenchVerb.NoRecipe);

            if (!UpgradeRules.CanUpgradeArmor(armor.Level, armor.MaxLevel, MaxArmorLevel))
                return WorkbenchResult.Of(WorkbenchVerb.NotCraftable);

            ArmourCraftFacts facts = ArmourFacts(armorId);
            int cost = facts.ComponentCountForNextLevel(armor.Level);
            if (cost <= 0 || !Inventory.HasItem(facts.ComponentId, cost, checkVault: true))
                return WorkbenchResult.Of(WorkbenchVerb.MissingMaterials, "noMaterials");

            // `minusItem(arm.idComp, kol, false)` — vault-inclusive, like every other bench removal.
            Inventory.RemoveItem(facts.ComponentId, cost, allowVault: true);
            armor.Upgrade();
            return WorkbenchResult.Of(WorkbenchVerb.UpArmor, "upArmor");
        }

        /// <summary>Upgrade a scrap gun to tier II — <c>PipPageWork.as:537-552</c>.</summary>
        public WorkbenchResult UpgradeWeapon(string weaponId)
        {
            GameWeaponInstance weapon = Inventory?.GetWeapon(weaponId);
            if (weapon == null || weapon.WeaponDefinition == null) return WorkbenchResult.Of(WorkbenchVerb.NoRecipe);

            if (!UpgradeRules.CanUpgradeWeapon(weapon.WeaponDefinition.skillLevel, weapon.Variant, weapon.Respect))
                return WorkbenchResult.Of(WorkbenchVerb.NotCraftable);

            CraftingRecipe? found = CraftingRecipeTable.FindByOutput(Recipes, weaponId);
            if (!found.HasValue) return WorkbenchResult.Of(WorkbenchVerb.NoRecipe);

            int skill = SkillLevel != null ? SkillLevel(found.Value.Skill) : 0;
            CraftRefusal refusal = CraftingRules.Check(found.Value, skill, Available);
            if (refusal == CraftRefusal.NotEnoughSkill)
                return WorkbenchResult.Of(WorkbenchVerb.NotEnoughSkill, "needSkill", found.Value.Skill,
                    found.Value.SkillLevel.ToString());
            if (refusal == CraftRefusal.MissingMaterials)
                return WorkbenchResult.Of(WorkbenchVerb.MissingMaterials, "noMaterials");

            RemoveMaterials(found.Value);
            weapon.UpgradeVariant(1);
            return WorkbenchResult.Of(WorkbenchVerb.Created, "created");
        }

        // ── Repair ────────────────────────────────────────────────────────────

        /// <summary>The repair list — <c>PipPageWork.as:211-282</c>, minus the owl (no pet system).</summary>
        public List<RepairCandidate> BuildRepairRows()
        {
            var rows = new List<RepairCandidate>();
            if (Inventory == null) return rows;

            foreach (KeyValuePair<string, GameWeaponInstance> pair in Inventory.Weapons)
            {
                GameWeaponInstance weapon = pair.Value;
                if (weapon == null) continue;

                int tip = weapon.WeaponDefinition != null ? (int)weapon.WeaponDefinition.weaponType : 0;
                if (!RepairRules.IsWeaponRepairable(tip, weapon.Respect, weapon.CurrentHealth, weapon.MaxHealth))
                    continue;

                rows.Add(new RepairCandidate(weapon.ItemId, CraftRowKind.Weapon, weapon.CurrentHealth,
                    weapon.MaxHealth, RepairRules.WeaponRepairFractionOfMax(WeaponRepEff(weapon.ItemId)),
                    RepairRules.WeaponComponentId));
            }

            foreach (KeyValuePair<string, GameArmorInstance> pair in Inventory.Armors)
            {
                GameArmorInstance armor = pair.Value;
                if (armor == null || armor.Definition == null) continue;

                if (!RepairRules.IsArmorRepairable(armor.Definition.armorNoRepair,
                        armor.Definition.armorIndestructible, armor.CurrentHealth, armor.MaxHealth)) continue;

                ArmourCraftFacts facts = ArmourFacts(armor.Id);
                rows.Add(new RepairCandidate(armor.Id, CraftRowKind.Armor, armor.CurrentHealth,
                    armor.MaxHealth, RepairRules.ArmorRepairFractionOfMax(facts.ComponentCount),
                    facts.ComponentId));
            }

            return rows;
        }

        /// <summary>Repair one weapon for one <c>frag</c> — <c>PipPageWork.as:578-594</c>.</summary>
        public WorkbenchResult RepairWeapon(string weaponId)
        {
            GameWeaponInstance weapon = Inventory?.GetWeapon(weaponId);
            if (weapon == null) return WorkbenchResult.Of(WorkbenchVerb.NoRecipe);

            int tip = weapon.WeaponDefinition != null ? (int)weapon.WeaponDefinition.weaponType : 0;
            float repEff = WeaponRepEff(weaponId);

            if (!RepairRules.CanRepairWeapon(tip, repEff))
                return WorkbenchResult.Of(WorkbenchVerb.NoRepair2, "noRepair2");

            if (weapon.CurrentHealth >= weapon.MaxHealth)
                return WorkbenchResult.Of(WorkbenchVerb.NoRepair, "noRepair");

            if (!Inventory.HasItem(RepairRules.WeaponComponentId, 1, checkVault: true))
                return WorkbenchResult.Of(WorkbenchVerb.MissingMaterials, "noMaterials");

            Inventory.RemoveItem(RepairRules.WeaponComponentId, 1, allowVault: true);
            weapon.Repair(RepairRules.WeaponRepairAmount(weapon.MaxHealth, RepairMultiplier, repEff));
            return WorkbenchResult.Of(WorkbenchVerb.Created, "repairWeapon");
        }

        /// <summary>Repair one armour for one component — <c>PipPageWork.as:557-577</c>.</summary>
        public WorkbenchResult RepairArmor(string armorId)
        {
            GameArmorInstance armor = Inventory?.GetArmor(armorId);
            if (armor == null || armor.Definition == null) return WorkbenchResult.Of(WorkbenchVerb.NoRecipe);

            if (armor.CurrentHealth >= armor.MaxHealth)
                return WorkbenchResult.Of(WorkbenchVerb.NoRepair, "noRepair");

            ArmourCraftFacts facts = ArmourFacts(armorId);
            if (!Inventory.HasItem(facts.ComponentId, 1, checkVault: true))
                return WorkbenchResult.Of(WorkbenchVerb.MissingMaterials, "noMaterials");

            Inventory.RemoveItem(facts.ComponentId, 1, allowVault: true);
            armor.Repair(RepairRules.ArmorRepairAmount(armor.MaxHealth, RepairMultiplier,
                facts.ComponentCount));
            return WorkbenchResult.Of(WorkbenchVerb.Created, "repair");
        }

        // ── Facts ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The product's state, as the create list's three-way branch needs it
        /// (<c>PipPageWork.as:90-141</c>).
        /// </summary>
        public CraftTargetFacts FactsFor(string outputId)
        {
            GameWeaponInstance weapon = Inventory?.GetWeapon(outputId);
            if (weapon != null)
            {
                int tip = weapon.WeaponDefinition != null ? (int)weapon.WeaponDefinition.weaponType : 0;
                return new CraftTargetFacts(true, false, tip, weapon.Respect, 0, string.Empty, false,
                    CountOf(outputId));
            }

            GameArmorInstance armor = Inventory?.GetArmor(outputId);
            if (armor != null)
                return new CraftTargetFacts(false, true, 0, WeaponRespect.Active, armor.Level, string.Empty,
                    false, 0);

            if (_itemRows.TryGetValue(outputId, out ItemXmlRow row))
            {
                return new CraftTargetFacts(false, false, 0, WeaponRespect.Active, 0,
                    row.Tip, WeaponXmlAttrs.AttrI(row.Attrs, "one", 0) > 0, CountOf(outputId));
            }

            return new CraftTargetFacts(false, false, 0, WeaponRespect.Active, 0, string.Empty, false,
                CountOf(outputId));
        }

        /// <summary>
        /// A weapon's <c>rep_eff</c> — <c>node.com[0].@rep</c>, defaulting to <b>1</b> when absent
        /// (<c>Weapon.as:122</c> declares 1; <c>:456-459</c> only overwrites when the attribute exists).
        ///
        /// <para><b>0 is a real value and means "cannot be repaired".</b> Four <c>&lt;com&gt;</c> rows
        /// carry <c>rep='0'</c>; those weapons are listed on the repair page (the list only tests
        /// <c>hp &lt; maxhp</c>) and refuse on click with <c>noRepair2</c> — the oracle's own split.</para>
        /// </summary>
        public float WeaponRepEff(string weaponId)
        {
            if (string.IsNullOrEmpty(_allData) || string.IsNullOrEmpty(weaponId)) return 1f;
            if (!AllDataElements.TryGet(_allData, "weapon", weaponId, out AllDataElement weapon)) return 1f;
            if (!AllDataElements.TryFirstChild(weapon.Body, "com", out AllDataElement com)) return 1f;

            string rep = WeaponXmlAttrs.Attr(com.Attrs, "rep");
            if (string.IsNullOrEmpty(rep)) return 1f;
            return WeaponXmlAttrs.AttrF(com.Attrs, "rep", 1f);
        }

        /// <summary>The armour's <c>@comp</c>/<c>@kolcomp</c>/<c>upd@kol</c> facts.</summary>
        public ArmourCraftFacts ArmourFacts(string armorId)
        {
            return ArmourCraftTable.TryRead(_allData, armorId, out ArmourCraftFacts facts)
                ? facts
                : new ArmourCraftFacts(armorId + "_comp", 1, Array.Empty<int>());
        }

        /// <summary>
        /// How many the player has, <b>including the vault</b> — <c>checkScheme</c>'s test
        /// (<c>PipPageWork.as:381</c>).
        /// </summary>
        public int Available(string itemId)
        {
            GameItemInstance item = Inventory?.GetItem(itemId);
            return item == null ? 0 : item.Quantity + item.VaultQuantity;
        }

        /// <summary>How many the player carries, excluding the vault — the list's count column.</summary>
        public int CountOf(string itemId)
        {
            GameItemInstance item = Inventory?.GetItem(itemId);
            return item == null ? 0 : item.Quantity;
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private void RemoveMaterials(in CraftingRecipe recipe)
        {
            for (int i = 0; i < recipe.Materials.Length; i++)
            {
                CraftMaterialRow material = recipe.Materials[i];
                Inventory.RemoveItem(material.Id, material.Count, allowVault: true);
            }

            Inventory.CalculateMass();
        }

        private ItemDefinition ResolveItem(string itemId)
        {
            return Inventory?.ItemDefinitionResolver?.Invoke(itemId);
        }

        private static Dictionary<string, ItemXmlRow> BuildItemRowIndex(string allData)
        {
            var index = new Dictionary<string, ItemXmlRow>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(allData)) return index;

            foreach (ItemXmlRow row in ItemXmlRows.Parse(allData))
            {
                if (!string.IsNullOrEmpty(row.Id) && !index.ContainsKey(row.Id)) index[row.Id] = row;
            }

            return index;
        }
    }
}
