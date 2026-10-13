using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Systems.Inventory;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// Where the inventory page's entries come from. An interface so the page can be built and its
    /// list rules exercised against a hand-made source, with no player and no
    /// <c>ScriptableObject</c>.
    ///
    /// <para><b>It returns everything the player holds, unfiltered.</b> The page's sub-page filter,
    /// hidden-weapon gate and sort all live in <see cref="PipInventoryList"/>, which is the pure half.
    /// A source that pre-filtered would move those rules out of the part that can be run offline,
    /// which is the whole reason the split exists.</para>
    /// </summary>
    public interface IPipInventorySource
    {
        /// <summary>Every entry the player holds. Never null; may be empty.</summary>
        IReadOnlyList<PipInventoryEntry> Read();
    }

    /// <summary>
    /// Reads the inventory page's entries out of the live <c>GameInventory</c> — the port of the three
    /// loops in <c>PipPageInv.setSubPages</c> that walk <c>inv.weapons</c>, <c>inv.armors</c> and
    /// <c>inv.items</c>.
    ///
    /// <para><b>The inventory arrives as a <c>Func</c>, and that is deliberate.</b> The player is
    /// assembled by <c>PlayerController.Awake</c>, and its <c>PlayerInventory</c> builds the
    /// <c>GameInventory</c> inside its own <c>Construct</c> — which Unity does not order against the
    /// PipBuck bootstrapper's <c>Start</c>. A constructor that captured the instance would bind null
    /// once and then show an empty bag forever, which is the exact failure
    /// <c>HudBootstrapper</c> documents. Resolving per read means a player who appears a second later
    /// simply appears in the list.</para>
    ///
    /// <para><b>The page partition is <see cref="InventoryPageRules"/>, not the Oracle's
    /// <c>tips</c> table.</b> AS3 derives an item's kind from its XML and matches it against the
    /// active sub-page (<c>PipPageInv.as:244-253</c>), and its pages <i>overlap</i> — <c>equip</c>
    /// and <c>food</c> each appear on two tabs. The port's rule is a partition, documented and
    /// deliberate (see <c>InventoryPages.cs</c>). Using it here rather than re-deriving the Oracle's
    /// overlapping sets keeps one definition of "which page is this item on" in the codebase.</para>
    /// </summary>
    public sealed class PlayerPipInventorySource : IPipInventorySource
    {
        private readonly Func<GameInventory> _inventory;

        /// <summary>
        /// Cached across reads so the page does not allocate a fresh list per rebuild. Cleared at the
        /// start of every <see cref="Read"/> rather than appended to — a stale entry would show an item
        /// the player no longer holds.
        /// </summary>
        private readonly List<PipInventoryEntry> _entries = new List<PipInventoryEntry>();

        public PlayerPipInventorySource(Func<GameInventory> inventory)
        {
            _inventory = inventory;
        }

        /// <summary>
        /// The equipped weapon's id, for the selection frame (<c>ramka</c>,
        /// <c>PipPageInv.as:391-395</c>). Null hides the frame.
        /// </summary>
        public Func<string> CurrentWeaponIdProvider { get; set; }

        /// <summary>
        /// The equipped weapon's loaded ammo base, for the ammo page's "loaded round sorts first"
        /// nudge (<c>PipPageInv.as:285-288</c>). Null disables the nudge.
        /// </summary>
        public Func<string> LoadedAmmoBaseProvider { get; set; }

        /// <summary>
        /// <c>World.w.hardInv</c>. Gates the mass column: the Oracle writes <c>item.mass</c> only
        /// when hard mode is on (<c>PipPageInv.as:462-465</c>), so with it off every entry's
        /// <see cref="PipInventoryEntry.Mass"/> is zeroed and the column renders empty — which is
        /// exactly the Oracle's output, not a missing value.
        /// </summary>
        public Func<bool> HardInventoryProvider { get; set; }

        public IReadOnlyList<PipInventoryEntry> Read()
        {
            _entries.Clear();

            GameInventory inventory = _inventory?.Invoke();
            if (inventory == null) return _entries;

            bool hardInventory = HardInventoryProvider != null && HardInventoryProvider();

            AddWeapons(inventory);
            AddArmors(inventory);
            AddItems(inventory, hardInventory);

            return _entries;
        }

        // ── Page 1: weapons (PipPageInv.as:64-177) ────────────────────────────

        private void AddWeapons(GameInventory inventory)
        {
            string currentWeaponId = CurrentWeaponIdProvider?.Invoke();

            foreach (KeyValuePair<string, GameWeaponInstance> pair in inventory.Weapons)
            {
                GameWeaponInstance weapon = pair.Value;
                if (weapon == null || weapon.Definition == null) continue;

                // PipPageInv.as:79-80 — `if(_loc2_.respect != 3)`. A scheme-only weapon is not listed.
                if (weapon.Respect == WeaponRespect.SchemeOnly) continue;

                string id = weapon.Definition.itemId;
                bool hidden = weapon.Respect == WeaponRespect.Hidden;

                // PipPageInv.as:120-128 — the primary key is 1 when usable, 2 when not, and 3 for a
                // stored weapon. The order matters: a hidden weapon sorts after an unusable one, and
                // 3 is assigned last so it overrides both.
                int sort1 = weapon.IsAvailable ? 1 : 2;
                if (hidden) sort1 = 3;

                _entries.Add(new PipInventoryEntry
                {
                    Id = id,
                    Name = weapon.Definition.displayName,
                    Kind = PipInventoryKind.Weapon,
                    SubPage = PipInventoryList.WeaponsSubPage,
                    CategoryKey = string.Empty,

                    // PipPageInv.as:129-139 — the Oracle's next two keys are the weapon's `skill` and
                    // `lvl`. Neither exists on GameWeaponInstance, so both are 0 and the list falls
                    // through to the name tiebreak. Recorded rather than invented: a made-up skill
                    // number would silently reorder the whole page.
                    Sort1 = sort1,
                    Sort2 = 0,
                    Sort3 = 0,

                    // PipPageInv.as:140-143 — `hp` is the condition percentage, and the Oracle only
                    // writes it for `tip < 4` (thrown and magic weapons have no condition). The port's
                    // weapon model carries no tip, and no thrown or magic weapon exists in its weapon
                    // dictionary yet, so the column is always written.
                    Value = weapon.HealthPercent,
                    ValueIsPercent = true,

                    Available = weapon.IsAvailable,
                    Hidden = hidden,
                    Equipped = !string.IsNullOrEmpty(currentWeaponId) && currentWeaponId == id,

                    // PipPageInv.as:144-155 — the `ammo` column is the rounds in the weapon. The
                    // Oracle splits a magazine base from a per-round type; the port keeps only the
                    // loaded type id, which is shown as-is rather than resolved to a display name
                    // (that needs the item table and the Oracle's `nazv`).
                    Extra = weapon.CurrentAmmo.ToString(),
                    ExtraTip = weapon.LoadedAmmoType ?? string.Empty,

                    Favorite = FavoriteLabel(inventory, id),
                });
            }
        }

        // ── Page 2: armour (PipPageInv.as:178-214) ────────────────────────────

        private void AddArmors(GameInventory inventory)
        {
            string currentArmorId = CurrentArmorId(inventory);

            foreach (KeyValuePair<string, GameArmorInstance> pair in inventory.Armors)
            {
                GameArmorInstance armor = pair.Value;
                if (armor == null || armor.Definition == null) continue;

                // PipPageInv.as:191 — `if(_loc7_.lvl >= 0)`. A negative level means the armour is not
                // owned in a listable state.
                if (armor.Level < 0) continue;

                string id = armor.Definition.itemId;

                _entries.Add(new PipInventoryEntry
                {
                    Id = id,
                    Name = armor.Definition.displayName,
                    Kind = PipInventoryKind.Armor,
                    SubPage = PipInventoryList.ArmorSubPage,

                    // PipPageInv.as:201,211 — the primary key is `trol`, i.e. "armor" + the equipment
                    // slot, so body armour and amulets group together. sortOrder is the second key.
                    CategoryKey = "armor" + armor.Definition.armorTip,
                    Sort1 = armor.Definition.sortOrder,
                    Sort2 = 0,
                    Sort3 = 0,

                    // PipPageInv.as:199 — same condition percentage as a weapon.
                    Value = armor.HealthPercent,
                    ValueIsPercent = true,

                    Available = true,
                    Hidden = false,
                    Equipped = !string.IsNullOrEmpty(currentArmorId) && currentArmorId == id,

                    Extra = string.Empty,
                    ExtraTip = string.Empty,
                    Favorite = FavoriteLabel(inventory, id),
                });
            }
        }

        // ── Pages 3/4/5: items (PipPageInv.as:215-306) ────────────────────────

        private void AddItems(GameInventory inventory, bool hardInventory)
        {
            string loadedAmmoBase = LoadedAmmoBaseProvider?.Invoke();

            foreach (KeyValuePair<string, GameItemInstance> pair in inventory.Items)
            {
                GameItemInstance item = pair.Value;
                ItemDefinition definition = item?.Definition;
                if (definition == null) continue;

                // PipPageInv.as:229 — an empty stack, a nameless row, or one flagged `invis` is not
                // listed. The port's `isHidden` is the importer's read of that flag.
                if (item.Quantity <= 0) continue;
                if (definition.isHidden) continue;

                string id = definition.itemId;
                InventoryPage page = InventoryPageRules.PageOf(definition.type);
                int subPage = PipInventoryList.SubPageFor(page);

                // PipPageInv.as:283-288 — the primary key is the item's category, and on the ammo
                // page a round the equipped weapon is loaded with is prefixed so it sorts first.
                bool isLoadedAmmo = subPage == PipInventoryList.AmmoSubPage
                                    && !string.IsNullOrEmpty(loadedAmmoBase)
                                    && (loadedAmmoBase == id || loadedAmmoBase == definition.baseItemId);

                _entries.Add(new PipInventoryEntry
                {
                    Id = id,
                    Name = definition.displayName,
                    Kind = PipInventoryKind.Item,
                    SubPage = subPage,

                    // The Oracle sorts on the *localised* category string (`Res.pipText(@tip)`). The
                    // port sorts on the raw `@tip` instead: it is the same Oracle data without the
                    // un-extracted string table, and it is locale-independent, so a fixture can pin it.
                    CategoryKey = definition.legacyTip ?? string.Empty,
                    Sort1 = definition.sortOrder,
                    Sort2 = 0,
                    Sort3 = 0,

                    // PipPageInv.as:461 — the value column is the stack count.
                    Value = item.Quantity,
                    ValueIsPercent = false,

                    // PipPageInv.as:462-465 — gated on hard mode. See HardInventoryProvider.
                    Mass = hardInventory ? definition.weight : 0f,
                    Count = item.Quantity,

                    Available = true,
                    Hidden = false,
                    Equipped = false,
                    IsNew = item.NewStatus == NewItemStatus.New,
                    IsRecent = item.NewStatus == NewItemStatus.Recent,

                    // PipPageInv.as:273-276,466-469 — the price is shown for valuables only.
                    ShowPrice = definition.type == ItemType.Valuable,
                    Extra = definition.basePrice.ToString(),

                    // PipPageInv.as:491 — the Oracle writes `cat.substring(2)`, stripping the
                    // localisation prefix off the category string. With no localised category the port
                    // writes the raw `@tip`, which is the same token minus the prefix.
                    ExtraTip = definition.legacyTip ?? string.Empty,

                    IsLoadedAmmo = isLoadedAmmo,

                    // PipPageInv.as:485-492 — the pending drop count. The port has no drop queue: the
                    // right-click drop path is a hook on the page (see PipPageInvView.OnRowActivated),
                    // so nothing can set this yet, and 0 is the honest value.
                    PendingDrop = 0,

                    Favorite = FavoriteLabel(inventory, id),
                });
            }
        }

        // ── Shared ────────────────────────────────────────────────────────────

        /// <summary>
        /// The favourite column's text.
        ///
        /// <para><b>The Oracle shows the bound key's name; the port shows the slot number.</b>
        /// <c>PipPageInv.setStatItem</c> (<c>:354-380</c>) maps a favourite slot to a key name through
        /// <c>World.w.ctr.retKey("keyWeapon" + n)</c>, which resolves against the controller's
        /// keybinding table — a table the port's <c>InputReader</c> does not expose as names. The slot
        /// number is the same fact without the un-extracted name, so it is shown in brackets rather
        /// than omitted, and the gap is recorded here.</para>
        /// </summary>
        private static string FavoriteLabel(GameInventory inventory, string id)
        {
            if (inventory.FavoriteSlots == null) return string.Empty;
            return inventory.FavoriteSlots.TryGetValue(id, out int slot) ? "[" + slot + "]" : string.Empty;
        }

        /// <summary>
        /// The equipped armour id. Read from <c>GameInventory</c> rather than through a provider
        /// because the inventory itself is the owner of that field
        /// (<c>GameInventory.CurrentArmorId</c>), unlike the weapon id, which callers set through the
        /// loadout and which is therefore injectable for a test.
        /// </summary>
        private static string CurrentArmorId(GameInventory inventory) => inventory.CurrentArmorId;
    }
}
