using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Campaign;
using PFE.Systems.Inventory;
using PFE.Systems.RPG;
using PFE.Systems.Trade;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// Where the trade page's rows and mutations come from — the port's seam for
    /// <c>PipPageVend</c>'s four readers (<c>vend.buys</c>, <c>inv.items</c>, <c>inv.weapons</c> /
    /// <c>inv.armors</c>, <c>vend.xml.task</c>) and its three writers (<c>inv.take</c>,
    /// <c>inv.money.kol</c>, <c>item.repair</c>).
    ///
    /// <para><b>Why an interface.</b> The page's list rules and its ledger are the part worth pinning,
    /// and pinning them means running them against a hand-made source — no player, no
    /// <c>ScriptableObject</c>, no campaign. The live reader
    /// (<see cref="PlayerVendorPageSource"/>) is the only class that knows about
    /// <c>GameInventory</c> and <c>CampaignManager</c>.</para>
    ///
    /// <para><b>The mutations are here and not on the page</b> for the same reason
    /// <c>PipPageInvView</c> hands row activations out: the bag has exactly one authoritative writer,
    /// and a view that reached into <c>GameInventory</c> would be a second one.</para>
    /// </summary>
    public interface IVendorPageSource : IVendorBuyFacts
    {
        /// <summary>Caps, level, barter, hard-inventory and the base flag.</summary>
        VendorPageContext Context { get; }

        /// <summary>
        /// AS3 <c>pers.barterMult</c> — pushed onto <c>vendor.multPrice</c> when the page opens
        /// (<c>PipBuck.as:414</c>). The port folds it into <c>CharacterStats.capsMult</c>
        /// (<c>VendorInventory.cs:106-110</c>), so this reads that field.
        /// </summary>
        float BarterMultiplier { get; }

        /// <summary>The item table row for an id, or <c>Exists = false</c>.</summary>
        VendorItemFacts Item(string itemId);

        /// <summary>How many of this id the player holds — the buy row's "you have".</summary>
        int PlayerStock(string itemId);

        /// <summary>The sell list — everything the player holds with an <c>@sell</c> price
        /// (<c>PipPageVend.as:236-275</c>).</summary>
        List<VendorSellRow> SellRows();

        /// <summary>The repair list — damaged weapons and armour (<c>PipPageVend.as:323-360</c>).</summary>
        /// <param name="vendorMult">The vendor's live <c>multPrice</c>.</param>
        /// <param name="armorMult">AS3 <c>pers.priceRepArmor</c> — the armour-only extra factor
        /// (<c>PipPageVend.as:829-850</c>).</param>
        List<VendorRepairRow> RepairRows(float vendorMult, float armorMult);

        /// <summary>The quest's state in the Oracle's numbering: 0 absent, 1 in progress, 2 done,
        /// 3 reportable, 4 giveable.</summary>
        int QuestState(string questId);

        /// <summary>The quest's display name, or null to fall back to the id.</summary>
        string QuestName(string questId);

        /// <summary>
        /// A skill's level, for the quest board's <c>skill</c>/<c>skilln</c> gate
        /// (<c>PipPageVend.as:382-396</c>). <b>Not</b> the same value as
        /// <see cref="VendorPageContext.BarterLevel"/>: the vendor's row gates read
        /// <c>pers.barterLvl</c>, the task gates read the <i>skill</i> table.
        /// </summary>
        int SkillLevel(string skillId);

        // ── mutations ─────────────────────────────────────────────────────────

        /// <summary><c>inv.take(item, 1)</c> — land a purchased row in the bag.</summary>
        bool AddPurchased(string itemId, int count);

        /// <summary><c>inv.items[key] -= bou</c> — remove a sold row from the bag.</summary>
        bool RemoveSold(string itemId, int count);

        /// <summary><c>inv.money.kol -= ceil(kolBou)</c>. False when the player cannot pay.</summary>
        bool SpendCaps(int amount);

        /// <summary><c>inv.money.kol += floor(kolSell)</c>.</summary>
        void ReceiveCaps(int amount);

        /// <summary>Restore durability on the item a repair row names.</summary>
        bool RepairItem(string rid, float amount);

        /// <summary>The quest row's click — <c>addQuest</c> for a new task, the give/report pair for
        /// one already in the journal (<c>PipPageVend.as:861-880</c>).</summary>
        void ActivateQuest(string questId, int state);
    }

    /// <summary>
    /// Reads the trade page out of the live player and campaign — the port of the four loops in
    /// <c>PipPageVend.setSubPages</c>.
    ///
    /// <para><b>Everything is resolved through a <c>Func</c>, for the reason
    /// <see cref="PlayerPipInventorySource"/> documents:</b> the player is assembled in
    /// <c>PlayerController.Awake</c> and the PipBuck in a later <c>Start</c>, so a constructor that
    /// captured the instance would bind null once and show an empty shelf forever.</para>
    /// </summary>
    public sealed class PlayerVendorPageSource : IVendorPageSource
    {
        private readonly Func<GameInventory> _inventory;
        private readonly Func<CharacterStats> _stats;
        private readonly Func<string, ItemDefinition> _resolveItem;

        private readonly List<VendorSellRow> _sellRows = new List<VendorSellRow>();
        private readonly List<VendorRepairRow> _repairRows = new List<VendorRepairRow>();

        /// <param name="inventory">The live bag. Resolved per read — see the class summary.</param>
        /// <param name="stats">The live player stats, for level / barter / caps multiplier.</param>
        /// <param name="resolveItem">Id → item row. <c>GameInventory</c> has no item resolver of its
        /// own (only <c>PlayerInventory</c> does), so it is passed in rather than reached for.</param>
        public PlayerVendorPageSource(Func<GameInventory> inventory, Func<CharacterStats> stats,
            Func<string, ItemDefinition> resolveItem)
        {
            _inventory = inventory;
            _stats = stats;
            _resolveItem = resolveItem;
        }

        /// <summary><c>World.w.hardInv</c>. Gates <c>hardinv</c> rows (<c>PipPageVend.as:149</c>).</summary>
        public Func<bool> HardInventoryProvider { get; set; }

        /// <summary><c>World.w.loc.base</c>. Gates the sell-side purse test (<c>:962-966</c>).</summary>
        public Func<bool> InBaseProvider { get; set; }

        /// <summary><c>World.w.game.triggers</c>, for the <c>trigger</c> gate (<c>:147</c>).</summary>
        public Func<string, bool> TriggerIsOneProvider { get; set; }

        /// <summary>AS3 <c>pers.priceRepArmor</c> (<c>Pers.as:105</c>, default 0.5).</summary>
        public Func<float> RepairArmorMultiplierProvider { get; set; }

        /// <summary>
        /// Id → weapon row — <c>AllData.d.weapon</c>, the table the item resolver <b>cannot</b> answer
        /// for (<c>Item.as:157-160</c>: a weapon id is looked up in <c>d.weapon</c>, never <c>d.item</c>).
        ///
        /// <para><b>Without this the weapon vendor sold unnamed, priceless rows.</b> The shelf's name
        /// and price both come from <see cref="Item"/>, which only asked the item table, so every
        /// weapon on <c>vendor_weapon</c>'s 20-odd rows fell back to its raw id ("p10s", "shotgun^1")
        /// and to a price of <c>0</c> — which reads as "the weapon vendor doesn't sell weapons". The
        /// price is real: a weapon's trader price is <c>&lt;com&gt;@price</c>, not a root attribute
        /// (<c>Item.as:354-356</c>), which is why the item table never had it.</para>
        /// </summary>
        public Func<string, WeaponDefinition> WeaponResolver { get; set; }

        /// <summary>The localisation document every name is read out of. See
        /// <see cref="PipItemNames"/> for why the definition alone prints the placeholder.</summary>
        public PipTextTable TextTable { get; set; } = PipTextTable.Empty;

        /// <summary>
        /// The row's primary sort key — <c>Res.pipText(b.tip)</c> (<c>PipPageVend.as:213</c>). A
        /// missing key renders as <c>*p_…</c> from the document, which would sort the whole shelf under
        /// a star; the raw tip is the honest fallback.
        /// </summary>
        private string SortNameFor(string tip)
        {
            if (string.IsNullOrEmpty(tip)) return string.Empty;

            string text = TextTable != null ? TextTable.Pip(tip) : null;
            if (string.IsNullOrEmpty(text) || text.StartsWith("*", StringComparison.Ordinal)) return tip;
            return text;
        }

        public VendorPageContext Context
        {
            get
            {
                GameInventory inventory = _inventory?.Invoke();
                CharacterStats stats = _stats?.Invoke();

                return new VendorPageContext
                {
                    Caps = inventory != null ? inventory.Money : 0,
                    Level = stats != null ? stats.Level : 1,
                    BarterLevel = stats != null ? stats.barterLvl : 0,
                    HardInventory = HardInventoryProvider != null && HardInventoryProvider(),
                    InBase = InBaseProvider != null && InBaseProvider(),
                    CategoryFilter = string.Empty
                };
            }
        }

        /// <summary>
        /// AS3 <c>pers.barterMult</c> (<c>Pers.as:313</c>, default 1). Folded into
        /// <c>CharacterStats.capsMult</c> by the port — see <c>VendorInventory.GetPriceMultiplier</c>,
        /// which documents the fold.
        /// </summary>
        public float BarterMultiplier
        {
            get
            {
                CharacterStats stats = _stats?.Invoke();
                return stats != null ? stats.capsMult : 1f;
            }
        }

        public VendorItemFacts Item(string itemId)
        {
            var facts = new VendorItemFacts { Exists = false };
            if (string.IsNullOrEmpty(itemId)) return facts;

            ItemDefinition definition = _resolveItem?.Invoke(itemId);
            if (definition == null)
            {
                // ── The weapon table, which the item resolver cannot answer ───────────────────────
                //
                // `new Item(tip, id, …)` looks a weapon up in `AllData.d.weapon` (`Item.as:157-160`),
                // and `PipPageVend.as:172-179` reads `b.nazv`/`b.price` off that same object. Asking
                // only the item table left every weapon row named by its id and priced 0.
                WeaponDefinition weapon = WeaponResolver?.Invoke(itemId);
                if (weapon == null) return facts;

                facts.Exists = true;
                // `Res.txt("w", id)` — the weapon table's name, via the same helper the inventory
                // page's rows use. Variant 0: the shelf's `^N` rows are named by their base id here,
                // and the row's `rid` still carries the variant for the purchase.
                facts.Name = PipItemNames.Weapon(TextTable, weapon.weaponId, 0, weapon.displayName);
                facts.Tip = "weapon";
                // `Item.getPrice()` (`Item.as:352-372`): a `<com>` row's `@price` wins over the root
                // attribute, and a weapon has ONLY the `<com>` form. `sost`/`pmult`/`mp` are applied
                // later by VendorTradeRules.BuyUnitPrice, exactly as `getPrice` chains them.
                facts.Price = (int)Math.Round(weapon.comPrice);
                // Weapons are not in the sell list — `PipPageVend.as:237` iterates `inv.items` only —
                // so a sell price here would be a number nothing reads.
                facts.SellPrice = 0;
                return facts;
            }

            facts.Exists = true;
            facts.Name = PipItemNames.Item(TextTable, definition.itemId, definition.legacyTip,
                definition.baseItemId, definition.modifierType, definition.workStation,
                definition.displayName);
            facts.Tip = string.IsNullOrEmpty(definition.legacyTip) ? "misc" : definition.legacyTip;
            facts.Price = definition.basePrice;
            facts.SellPrice = definition.sellPrice;
            return facts;
        }

        public int PlayerStock(string itemId)
        {
            GameInventory inventory = _inventory?.Invoke();
            if (inventory == null || string.IsNullOrEmpty(itemId)) return 0;

            if (inventory.Items.TryGetValue(itemId, out GameItemInstance item)) return item.Quantity;
            if (inventory.Weapons.ContainsKey(itemId)) return 1;
            if (inventory.Armors.ContainsKey(itemId)) return 1;
            return 0;
        }

        // ── the guards ────────────────────────────────────────────────────────

        public bool OwnsScheme(string itemId) => PlayerStock(itemId) > 0;

        /// <summary>
        /// <c>PipPageVend.as:137</c> — "not a weapon at a variant ≤ the player's". The Oracle walks its
        /// weapon list for the same base id and compares <c>variant</c>.
        /// </summary>
        public bool OwnsWeaponVariantAtLeast(string itemId, int variant)
        {
            GameInventory inventory = _inventory?.Invoke();
            if (inventory == null) return false;

            foreach (KeyValuePair<string, GameWeaponInstance> pair in inventory.Weapons)
            {
                GameWeaponInstance weapon = pair.Value;
                string baseId = weapon.ItemId;
                if (!string.Equals(baseId, itemId, StringComparison.Ordinal)) continue;
                if (weapon.Variant >= variant) return true;
            }

            return false;
        }

        public bool OwnsArmor(string itemId)
        {
            GameInventory inventory = _inventory?.Invoke();
            return inventory != null && inventory.Armors.ContainsKey(itemId);
        }

        public bool OwnsArt(string itemId) => PlayerStock(itemId) > 0;

        public bool TriggerIsOne(string triggerName)
        {
            return TriggerIsOneProvider != null && TriggerIsOneProvider(triggerName);
        }

        // ── the lists ─────────────────────────────────────────────────────────

        public List<VendorSellRow> SellRows()
        {
            _sellRows.Clear();

            GameInventory inventory = _inventory?.Invoke();
            if (inventory == null) return _sellRows;

            // PipPageVend.as:236-275 — every `inv.items` row whose @sell is above zero. AS3's
            // `inv.items` holds weapons and armour too (they are Items there), so the port's three
            // maps are all walked here.
            foreach (KeyValuePair<string, GameItemInstance> pair in inventory.Items)
            {
                ItemDefinition definition = pair.Value?.Definition;
                if (definition == null || definition.sellPrice <= 0) continue;

                _sellRows.Add(new VendorSellRow
                {
                    Rid = definition.itemId,
                    Id = definition.itemId,
                    Tip = string.IsNullOrEmpty(definition.legacyTip) ? "misc" : definition.legacyTip,
                    Name = PipItemNames.Item(TextTable, definition.itemId, definition.legacyTip,
                        definition.baseItemId, definition.modifierType, definition.workStation,
                        definition.displayName),
                    Kol = pair.Value.Quantity,
                    Price = definition.sellPrice,
                    SortName = SortNameFor(definition.legacyTip)
                });
            }

            foreach (KeyValuePair<string, GameWeaponInstance> pair in inventory.Weapons)
            {
                GameWeaponInstance weapon = pair.Value;
                // A weapon built from the weapon table has no ItemDefinition (the port keeps two
                // definition fields, see GameWeaponInstance:25-45), so the item row is resolved by id —
                // and it is the item row that carries `@sell`, which is the price the Oracle shows.
                ItemDefinition definition = weapon.Definition ?? _resolveItem?.Invoke(weapon.ItemId);
                if (definition == null || definition.sellPrice <= 0) continue;

                string rid = weapon.Variant > 0 ? weapon.ItemId + "^" + weapon.Variant : weapon.ItemId;
                _sellRows.Add(new VendorSellRow
                {
                    Rid = rid,
                    Id = weapon.ItemId,
                    Tip = "weapon",
                    Name = PipItemNames.Weapon(TextTable, weapon.ItemId, weapon.Variant, definition.displayName),
                    Kol = 1,
                    Price = definition.sellPrice,
                    SortName = SortNameFor("weapon")
                });
            }

            foreach (KeyValuePair<string, GameArmorInstance> pair in inventory.Armors)
            {
                GameArmorInstance armor = pair.Value;
                ItemDefinition definition = armor.Definition;
                if (definition == null || definition.sellPrice <= 0) continue;

                _sellRows.Add(new VendorSellRow
                {
                    Rid = definition.itemId,
                    Id = definition.itemId,
                    Tip = "armor",
                    Name = PipItemNames.Armor(TextTable, definition.itemId, definition.displayName),
                    Kol = 1,
                    Price = definition.sellPrice,
                    SortName = SortNameFor("armor")
                });
            }

            VendorTradeRules.SortSellRows(_sellRows);
            return _sellRows;
        }

        public List<VendorRepairRow> RepairRows(float vendorMult, float armorMult)
        {
            _repairRows.Clear();

            GameInventory inventory = _inventory?.Invoke();
            if (inventory == null) return _repairRows;

            foreach (KeyValuePair<string, GameWeaponInstance> pair in inventory.Weapons)
            {
                GameWeaponInstance weapon = pair.Value;
                ItemDefinition definition = weapon.Definition ?? _resolveItem?.Invoke(weapon.ItemId);
                if (!VendorTradeRules.IsRepairable("weapon", weapon.CurrentHealth, weapon.MaxHealth)) continue;

                // PipPageVend.as:829-850 — `mp = 1` for a weapon. The base price is the item row's
                // `@price`; a weapon with no item row falls back to `com@price` (WeaponDefinition:589).
                float basePrice = definition != null && definition.basePrice > 0
                    ? definition.basePrice
                    : weapon.WeaponDefinition != null ? weapon.WeaponDefinition.comPrice : 0f;

                if (basePrice <= 0f) continue;

                int price = VendorTradeRules.RepairPrice(basePrice, weapon.CurrentHealth,
                    weapon.MaxHealth, vendorMult, 1f);

                _repairRows.Add(new VendorRepairRow
                {
                    Rid = weapon.Variant > 0 ? weapon.ItemId + "^" + weapon.Variant : weapon.ItemId,
                    Id = weapon.ItemId,
                    Tip = "weapon",
                    Name = PipItemNames.Weapon(TextTable, weapon.ItemId, weapon.Variant,
                        definition != null ? definition.displayName : null),
                    Hp = weapon.CurrentHealth,
                    MaxHp = weapon.MaxHealth,
                    Price = price
                });
            }

            foreach (KeyValuePair<string, GameArmorInstance> pair in inventory.Armors)
            {
                GameArmorInstance armor = pair.Value;
                ItemDefinition definition = armor.Definition;
                if (definition == null) continue;
                if (!VendorTradeRules.IsRepairable("armor", armor.CurrentHealth, armor.MaxHealth)) continue;

                int price = VendorTradeRules.RepairPrice(definition.basePrice, armor.CurrentHealth,
                    armor.MaxHealth, vendorMult, armorMult);

                _repairRows.Add(new VendorRepairRow
                {
                    Rid = definition.itemId,
                    Id = definition.itemId,
                    Tip = "armor",
                    Name = PipItemNames.Armor(TextTable, definition.itemId, definition.displayName),
                    Hp = armor.CurrentHealth,
                    MaxHp = armor.MaxHealth,
                    Price = price
                });
            }

            VendorTradeRules.SortRepairRows(_repairRows);
            return _repairRows;
        }

        // ── quests ────────────────────────────────────────────────────────────

        /// <summary>
        /// The Oracle's row <c>state</c>, mapped onto the port's quest model.
        ///
        /// <para><b>Two of the Oracle's four values are not distinguishable in the port yet.</b> AS3
        /// splits an in-progress quest into "giveable" (4) and "reportable" (3) from its own sub-stage
        /// bookkeeping; the port's <c>QuestRuntimeState</c> carries subs but nothing exposes the
        /// give/report readiness, so 1 is reported for both. The consequence is named on the page: the
        /// Quests sub-tab's "!" sign cannot light from this source yet.</para>
        /// </summary>
        public int QuestState(string questId)
        {
            CampaignManager campaign = CampaignManager.Current;
            if (campaign == null || string.IsNullOrEmpty(questId)) return 0;

            if (!campaign.TryGetQuestState(questId, out int state, out _, out _)) return 0;

            if (state == (int)PFE.Systems.Campaign.QuestState.Done) return 2;
            if (state == (int)PFE.Systems.Campaign.QuestState.Active) return 1;
            return 4;   // known but not begun — the vendor can give it
        }

        public string QuestName(string questId)
        {
            CampaignManager campaign = CampaignManager.Current;
            if (campaign == null || string.IsNullOrEmpty(questId)) return null;

            QuestDefinition quest = campaign.Catalog?.GetQuest(questId);
            return string.IsNullOrEmpty(quest?.questTitle) ? null : quest.questTitle;
        }

        /// <summary>
        /// <c>pers.getSkLevel(pers.skills[id])</c> — the skill table, not <c>barterLvl</c>. The quest
        /// board's <c>skill</c>/<c>skilln</c> gate reads this (<c>PipPageVend.as:382-396</c>).
        /// </summary>
        public int SkillLevel(string skillId)
        {
            CharacterStats stats = _stats?.Invoke();
            return stats != null && !string.IsNullOrEmpty(skillId) ? stats.GetSkillLevel(skillId) : 0;
        }

        public void ActivateQuest(string questId, int state)
        {
            CampaignManager campaign = CampaignManager.Current;
            if (campaign == null || string.IsNullOrEmpty(questId)) return;

            // PipPageVend.as:861-880 — an existing quest runs chGive + chReport; a new one is added.
            // The port has no chGive/chReport pair; the closest honest equivalent for an in-progress
            // quest is a re-evaluation of its collection gates, which is what `CheckQuests` does.
            if (state == 4) campaign.AddQuest(questId);
            else if (state == 1 || state == 3) campaign.CheckQuests(PlayerStock);
        }

        // ── mutations ─────────────────────────────────────────────────────────

        public bool AddPurchased(string itemId, int count)
        {
            GameInventory inventory = _inventory?.Invoke();
            if (inventory == null || count <= 0) return false;

            // ── The route is chosen by the ROW, not by the table that happens to answer ─────────────
            //
            // AS3 `Invent.take()` dispatches on the item's tip (`Invent.as:1053-1100`): a weapon goes
            // through `addWeapon`, an armour through `addArmor`, and everything else through the stack
            // path. This method used to ask only the item resolver, and an armour id DOES resolve
            // there — armour rows are item rows (`ItemDefinition.IsArmour` is true for them, which is
            // why `PlayerInventory.Construct` builds its armour resolver out of the item one). So the
            // armour landed as a *stack of an item* in `items`, where the Armor sub-tab never looks,
            // while the caps had already left the purse.
            //
            // That is the reported "I buy armour, the caps go, the armour never appears". The caps
            // debit is not the bug and must not be moved after the grant: `trade()` debits first in
            // the Oracle too (`PipPageVend.as:948`), and a grant-then-debit order would let a failed
            // debit hand out free stock.
            ItemDefinition definition = _resolveItem?.Invoke(itemId);

            if (definition != null)
            {
                if (definition.IsArmour)
                {
                    // `AddArmor` refuses a duplicate by design (`armors.ContainsKey`), and the page's
                    // guard 4 already hides armour the player owns — so a refusal here means the two
                    // disagree, not that the player did something wrong.
                    return inventory.AddArmor(itemId) != null;
                }

                return inventory.AddItem(definition, count);
            }

            // A weapon id has no item row — the two tables are separate (Invent.as:1049+ routes
            // weapons through addWeapon). `AddWeapon` resolves the weapon table itself and reports
            // its own miss.
            if (inventory.AddWeapon(itemId) != null) return true;

            // Last resort: an armour id whose asset is missing from the ITEM registry but present in
            // the armour one. `AddArmor` resolves through `ArmorDefinitionResolver`, which is a
            // different lookup with a different failure mode, so asking it costs one dictionary hit
            // and turns a silent "paid for nothing" into a grant.
            return inventory.AddArmor(itemId) != null;
        }

        public bool RemoveSold(string itemId, int count)
        {
            GameInventory inventory = _inventory?.Invoke();
            if (inventory == null || count <= 0) return false;
            return inventory.RemoveItem(itemId, count);
        }

        public bool SpendCaps(int amount)
        {
            GameInventory inventory = _inventory?.Invoke();
            return inventory != null && inventory.TrySpendMoney(amount);
        }

        public void ReceiveCaps(int amount)
        {
            _inventory?.Invoke()?.AddMoney(amount);
        }

        public bool RepairItem(string rid, float amount)
        {
            GameInventory inventory = _inventory?.Invoke();
            if (inventory == null || amount <= 0f) return false;

            // The row's rid carries the `^N` variant; the bags are keyed by the bare id.
            CampaignDataParser.SplitVariant(rid, out string baseId, out _);

            if (inventory.Weapons.TryGetValue(baseId, out GameWeaponInstance weapon))
            {
                weapon.Repair(amount);
                return true;
            }

            if (inventory.Armors.TryGetValue(baseId, out GameArmorInstance armor))
            {
                armor.Repair(amount);
                return true;
            }

            return false;
        }
    }
}
