using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PFE.Data;
using PFE.Data.Definitions;
using PFE.ModAPI;          // ContentType lives here, not in PFE.Data (ContentRegistry implements IContentRegistry)
using PFE.Systems.Campaign;
using PFE.Systems.Items;
using PFE.Systems.Magic;
using PFE.Systems.RPG;
using PFE.Systems.Weapons;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// The player's own inventory, and the one place gameplay reaches it.
    ///
    /// <para><b>Per-player, not per-process — and that is a multiplayer decision, not a style one.</b>
    /// This is a component on the player object rather than a <c>GameLifetimeScope</c> singleton
    /// precisely so that a second player gets a second inventory. A singleton would compile, run, and
    /// be discovered only when two players in co-op started sharing one ammo pool — the exact kind of
    /// stale-belief bug this project keeps paying for. The same reasoning is why
    /// <c>PlayerSpellCaster</c> and <c>PlayerManaTicker</c> are components too.</para>
    ///
    /// <para><b>Mutations go through the command seam.</b> This class implements
    /// <see cref="IAmmoSource"/> so a weapon controller can keep holding the interface it already
    /// knows, but its <see cref="ConsumeAmmo"/> is routed through <see cref="IInventoryCommandSink"/>
    /// rather than calling <see cref="GameInventory"/> directly. Ammo consumption is the most frequent
    /// inventory mutation in the game, so leaving it outside the seam would have left the seam
    /// decorative. Reads (<see cref="GetAmmoCount"/>) stay direct and cheap.</para>
    ///
    /// <para><b>Created by <see cref="PFE.Entities.Player.PlayerController"/>, like the spell caster.</b>
    /// A scene/prefab edit would be needed to add it by hand, and the player is already assembled in
    /// <c>Awake</c> from components the controller adds — so it is created the same way, and wired
    /// through the same "apply from whichever of Construct/Awake runs second" pattern, because Unity
    /// does not order a scene component's <c>Awake</c> against the container's injection.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerInventory : MonoBehaviour, IAmmoSource, IItemSkillSource
    {
        /// <summary>The live inventory. Null until <see cref="Construct"/> has run.</summary>
        public GameInventory Inventory => _inventory;

        /// <summary>
        /// The mutation entry point. Null only in a rig that never supplied a content registry, in
        /// which case <see cref="Submit"/> rejects with a stated reason rather than throwing.
        /// </summary>
        public IInventoryCommandSink Commands => _commands;

        /// <summary>Whether the inventory exists. <c>Construct</c> runs from two places; this is the tell.</summary>
        public bool IsReady => _inventory != null;

        private GameInventory _inventory;

        /// <summary>Whether the collect-objective hook is on <see cref="_inventory"/>. See Construct.</summary>
        private bool _questCollectSubscribed;
        private IInventoryCommandSink _commands;

        /// <summary>
        /// Id → item row, kept from <see cref="Construct"/> so <see cref="DropItem"/> can build a world
        /// pickup's icon and display name without re-deriving the lookup. Null until a registry arrives.
        /// </summary>
        private Func<string, ItemDefinition> _itemResolver;

        /// <summary>
        /// Id → weapon row, kept from <see cref="Construct"/> for <see cref="ResolveWeapon"/> — the same
        /// reason <see cref="_itemResolver"/> is kept for <see cref="ResolveItem"/>. Null until a registry
        /// arrives.
        ///
        /// <para>Kept separately rather than derived, because the two tables are disjoint: a weapon id
        /// answered by the item resolver is <c>null</c>, always, and the weapon gap existed for as long as
        /// nothing held the other half of that fact.</para>
        /// </summary>
        private Func<string, WeaponDefinition> _weaponResolver;

        /// <summary>
        /// The content registry, kept so the F2 pickers can <i>enumerate</i> items rather than only
        /// resolve an id they were already given. Null until a registry arrives.
        /// </summary>
        private ContentRegistry _registry;

        /// <summary>
        /// Cached result of <see cref="AvailableItemIds"/>. The overlay draws every frame and would
        /// otherwise re-materialise and re-sort all ~500 ids per repaint; the registry is populated once
        /// at boot, so one cache is enough. Cleared by <see cref="InvalidateAvailableItems"/>.
        /// </summary>
        private List<string> _availableItemIds;

        private PlayerWeaponLoadout _loadout;
        private PlayerSpellCaster _caster;

        /// <summary>
        /// Build (or re-build) the inventory and its command sink against the content registry.
        ///
        /// <para><b>Called from both <c>Construct</c> and <c>Awake</c>, in either order, so it must be
        /// safe to call twice.</b> The inventory is created once and kept; the sink is rebuilt whenever
        /// a registry is supplied, because the first call may legitimately have none (Awake ran before
        /// injection) and a sink with null resolvers would reject every id forever.</para>
        ///
        /// <para><b>Only the registry <i>object</i> is needed, not its contents.</b> The resolvers close
        /// over the registry and are invoked later, so it is fine that
        /// <c>GameDatabase.Initialize()</c> has not populated it yet at boot.</para>
        /// </summary>
        public void Construct(ContentRegistry registry)
        {
            if (_inventory == null)
            {
                _inventory = new GameInventory();
            }

            // AS3 `Invent.take`'s tail — `res = World.w.game.checkQuests(l.id)` (`Invent.as:1305`) —
            // runs on the ITEM-ADD path, so the hook belongs to the inventory that owns that path.
            //
            // <b>Why nothing did this before.</b> `CampaignManager.CheckQuests` was written, tested and
            // had exactly one production caller: the vendor's sell side. So a collect objective was
            // computed once when the quest was granted and never again — the reported "picking up a
            // cogwheel leaves the counter at 0/20", with the quest row itself present and correct.
            //
            // <b>Why `ItemQuantityAdded` and not `OnItemAdded`.</b> The latter fires only when a NEW
            // stack appears, so the second cogwheel onwards would still have been invisible. The land's
            // `eve='take'` scripts want that first-appearance meaning and keep it; a counter wants every
            // arrival.
            //
            // Guarded because `Construct` is called twice on the normal path (once by injection before
            // the registry arrives, once with it), and a doubled subscription would run the check twice
            // per pickup. Harmless — `QuestRules.Check` recomputes `Est` from the held count rather than
            // incrementing — but it is still not a thing to leave in by accident.
            if (!_questCollectSubscribed)
            {
                _questCollectSubscribed = true;
                _inventory.ItemQuantityAdded += OnItemQuantityAdded;
            }

            if (registry == null)
            {
                // No registry yet: the inventory exists so save/load and the spell caster have an
                // object to hold, but every command is rejected with a reason until a real one
                // arrives. Guessing a lookup table here would be worse than saying "not yet".
                return;
            }

            // Kept for enumeration only — deliberately NOT assigned before the null check above, so a
            // second Construct(null) cannot wipe a registry that already arrived.
            _registry = registry;
            _availableItemIds = null;   // the new registry may hold a different set

            // The item resolver is AS3's AllData.d.item. The armour resolver is deliberately the
            // narrower d.armor: it filters on IsArmour, because GameDatabase.GetItem searches every
            // item row and would happily build an "armour" out of a potion — the same trap
            // GameInventory.ArmorDefinitionResolver documents.
            Func<string, ItemDefinition> itemResolver =
                id => string.IsNullOrEmpty(id) ? null : registry.Get<ItemDefinition>(ContentType.Item, id);

            Func<string, ItemDefinition> armorResolver = id =>
            {
                ItemDefinition definition = itemResolver(id);
                return definition != null && definition.IsArmour ? definition : null;
            };

            _inventory.ArmorDefinitionResolver = armorResolver;

            // The item table AS3's `AllData.d.item` — the resolver `GameInventory.Money` needs. Caps are
            // an ordinary item row in the Oracle (`Invent.money` is `items["money"]`,
            // Invent.as:41-43), and the first write to `Money` has to be able to create that row:
            // `addMin` writes `items["money"].kol = 50` (Invent.as:1610) as its last line, before the
            // player has ever picked a cap up.
            _inventory.ItemDefinitionResolver = itemResolver;

            // The weapon resolver is AS3's AllData.d.weapon — a separate table from the item one, and
            // separate here for the same reason. Without it GameInventory.AddWeapon had no lookup that
            // could ever succeed, which is the model gap the whole weapon path was stuck behind.
            Func<string, WeaponDefinition> weaponResolver =
                id => string.IsNullOrEmpty(id) ? null : registry.Get<WeaponDefinition>(ContentType.Weapon, id);

            _inventory.WeaponDefinitionResolver = weaponResolver;
            _commands = new LocalInventoryCommandSink(_inventory, itemResolver, armorResolver, weaponResolver);
            _itemResolver = itemResolver;
            _weaponResolver = weaponResolver;

            if (_caster != null)
            {
                _caster.SetInventory(_inventory);
            }
        }

        /// <summary>
        /// <c>World.w.game.checkQuests(itemId)</c> on every item arrival — the collect-objective half of
        /// <c>Invent.take</c> (<c>Invent.as:1305</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The count is read from the inventory, not from the event.</b> <c>QuestRules.Check</c>
        /// sets <c>Est = held + Gived</c>, so it is a recomputation and is safe to run on every pickup;
        /// passing the delta would make it an increment and double-count when a quest is granted while
        /// the items are already held (<c>AddQuest</c> runs the same check).</para>
        ///
        /// <para><b>What is still not wired, named.</b> The two other AS3 call sites —
        /// <c>Consol.as:361</c> (the debug <c>give</c>) and <c>PipPageWork.as:502</c> (crafting) — and
        /// the <c>incQuests</c> family, which is the <b>kill</b> counter (<c>Unit.as:4495-4503</c>,
        /// <c>UnitCaptive.as:133</c>, <c>UnitPhoenix.as:112</c>). Those fire on a unit's death, not on an
        /// item arriving, so they do not belong on this event.</para>
        /// </remarks>
        void OnItemQuantityAdded(GameItemInstance item, int quantity)
        {
            if (quantity <= 0) return;

            string itemId = item?.Definition != null ? item.Definition.itemId : null;
            if (string.IsNullOrEmpty(itemId)) return;

            CampaignManager campaign = CampaignManager.Current;
            campaign?.CheckQuests(CountOfItem, itemId);
        }

        /// <summary>How many of an item the player holds — the <c>countOf</c> seam of the quest check.</summary>
        int CountOfItem(string itemId)
        {
            GameItemInstance row = _inventory?.GetItem(itemId);
            return row != null ? row.Quantity : 0;
        }

        /// <summary>
        /// How many of an item the player holds, for callers outside this component.
        ///
        /// <para><b>Needed because the security layer is a count consumer, not an inventory owner.</b>
        /// AS3 reads <c>World.w.invent.items["pin"].kol</c> directly from <c>Interact</c>
        /// (<c>:1071</c>), and the port's equivalent — the lock rules — must not reach into
        /// <c>GameInventory</c> through a private field. Same answer as <see cref="CountOfItem"/>, one
        /// seam.</para>
        /// </summary>
        public int CountOf(string itemId) => CountOfItem(itemId);

        /// <summary>
        /// Point the weapon loadout at this inventory. <b>Must happen before
        /// <c>PlayerWeaponLoadout.Start</c> equips the starting weapon</b> — the setter rebuilds the
        /// controller factory, and a weapon equipped before that keeps the factory it was built with,
        /// so it would reload from a null source (infinite ammo) for the rest of its life.
        /// </summary>
        public void BindLoadout(PlayerWeaponLoadout loadout)
        {
            _loadout = loadout;
            if (_loadout != null)
            {
                // `this`, not `_inventory`: the loadout must consume through the command seam, or the
                // most frequent mutation in the game would bypass it.
                _loadout.AmmoSource = this;
            }
        }

        /// <summary>
        /// Hand the inventory to the spell caster, which needs it as the producer for the favourite-spell
        /// hotkeys and the <c>respect == 1</c> refusal (see <c>PlayerSpellCaster.SetInventory</c>).
        /// Tolerates being called before <see cref="Construct"/>: the caster is re-notified then.
        /// </summary>
        public void BindSpellCaster(PlayerSpellCaster caster)
        {
            _caster = caster;
            if (_caster != null && _inventory != null)
            {
                _caster.SetInventory(_inventory);
            }
        }

        /// <summary>Submit a command, or reject it with a reason when no sink exists yet.</summary>
        public InventoryCommandResult Submit(InventoryCommand command)
            => _commands != null
                ? _commands.Submit(command)
                : InventoryCommandResult.Fail("the inventory has no command sink (no content registry was injected)");

        // ── The starting inventory ────────────────────────────────────────────

        /// <summary>
        /// How many frames <see cref="SeedStartingLoadout"/> keeps retrying before it gives up.
        /// Ten seconds at 60 Hz is long enough for <c>GameManager.Start</c> to run
        /// <c>GameDatabase.Initialize()</c> (profiled at 3.5 s, the largest single boot item) and short
        /// enough that a broken grant reports itself instead of leaving an empty PipBuck that looks
        /// like a broken inventory page.
        /// </summary>
        private const int SeedRetryLimit = 600;

        private bool _loadoutSeeded;
        private int _seedFrames;

        /// <summary>
        /// Grants the <c>addMin</c> starting kit — <c>Invent.as:1600-1615</c>. Called from
        /// <see cref="Update"/> until it succeeds, and safe to call from a test or from the console.
        ///
        /// <para><b>Why it is deferred and not done in <see cref="Construct"/>.</b> The grant resolves
        /// every id through the content registry, which <c>GameDatabase.Initialize()</c> fills — from
        /// <c>GameManager.Start</c>. Unity does not order that call against this component's own
        /// <c>Awake</c>, and a seed that ran first would resolve nothing and report a data bug that is
        /// really an ordering bug. Retrying costs one registry enumerator per frame for at most a
        /// second.</para>
        ///
        /// <para><b>It goes through <see cref="Submit"/>.</b> Items, armour and weapons are real
        /// mutations, so they take the seam the rest of the port takes; only favourites have no
        /// <see cref="InventoryCommandKind"/> and are set directly, which is noted where they are
        /// granted.</para>
        /// </summary>
        public void SeedStartingLoadout()
        {
            if (_loadoutSeeded) return;

            if (_inventory == null || _commands == null || _itemResolver == null || !RegistryPopulated())
            {
                if (++_seedFrames > SeedRetryLimit)
                {
                    // Latched, so the error is one line rather than one per frame forever.
                    _loadoutSeeded = true;
                    Debug.LogError("[PlayerInventory] The starting inventory was never granted: the " +
                                   "content registry was still empty at frame " + _seedFrames + ". An " +
                                   "empty PipBuck inventory at boot is normally this, not a broken " +
                                   "inventory page — see StartingLoadout for what should have been " +
                                   "granted (Invent.as:1600-1615).");
                }

                return;
            }

            // Latched before the grants: a retry after a partial failure would double-grant the rows
            // that already landed, and each failure below already says which row and why.
            _loadoutSeeded = true;

            // Caps first, so a page opened on the very next frame already has a balance. AS3
            // `items["money"].kol = 50` is the last line of addMin (Invent.as:1610); the order is
            // immaterial here because nothing reads caps until an interaction, and the value is a
            // constant. This lands on the caps ITEM row — `GameInventory.Money` creates it through the
            // item resolver — which is the one store the Oracle has, and the reason the bag row and the
            // `Caps: N` readout can no longer show different numbers.
            _inventory.Money = StartingLoadout.Money;

            // AS3's order, verbatim (Invent.as:1602-1615): weapons first, then armour, then the item
            // rows, then the two favourites — which are weapons, so they can only be bound after the
            // weapons exist. Keeping the oracle's order rather than a convenient one is what makes the
            // favourites step meaningful.
            foreach (string id in StartingLoadout.WeaponIds) GrantWeapon(id);
            foreach (string id in StartingLoadout.ArmorIds) GrantArmour(id);
            foreach (StartingLoadout.Stack stack in StartingLoadout.Items) GrantItem(stack);
            foreach (StartingLoadout.Favourite favourite in StartingLoadout.Favourites) GrantFavourite(favourite);
        }

        private void GrantItem(StartingLoadout.Stack stack)
        {
            InventoryCommandResult result = Submit(InventoryCommand.AddItem(stack.ItemId, stack.Quantity));
            if (result.Applied) return;

            Debug.LogError("[PlayerInventory] Starting item '" + stack.ItemId + "' x" + stack.Quantity +
                           " was refused: " + result.Reason + " (Invent.as:1600-1615).");
        }

        private void GrantArmour(string id)
        {
            InventoryCommandResult result = Submit(InventoryCommand.AddArmor(id));
            if (result.Applied) return;

            Debug.LogError("[PlayerInventory] Starting armour '" + id + "' was refused: " +
                           result.Reason + " (Invent.as:1600-1615).");
        }

        /// <summary>
        /// <c>addWeapon(id)</c> — <c>Invent.as:1602-1605</c>. The four ids are <c>r32</c>, <c>rech</c>,
        /// <c>mont</c> and <c>bat</c>, and this used to be a warning loop instead of a grant: the port's
        /// weapons are <c>WeaponDefinition</c> assets with no item row, <c>GameInventory.AddWeapon</c>
        /// resolved through the item table and so could never succeed, and the Weapons sub-page stayed
        /// empty. It now resolves through <c>ContentType.Weapon</c> like the Oracle's <c>d.weapon</c>, so
        /// this is the same three-line grant the other two types use.
        ///
        /// <para><b>An error here means the registry, not this loop.</b> <see cref="SeedStartingLoadout"/>
        /// only runs once <see cref="RegistryPopulated"/> sees a non-empty <c>ContentType.Item</c> table,
        /// and <c>ContentRegistry.Initialize</c> fills every content type in one pass
        /// (<c>GameDatabase.Initialize</c> → eleven <c>Resources.LoadAll</c> calls), so there is no window
        /// in which items exist and weapons do not. A refusal therefore names a genuinely absent weapon
        /// asset, which is what <c>StartingLoadoutTests</c> pins.</para>
        /// </summary>
        private void GrantWeapon(string id)
        {
            InventoryCommandResult result = Submit(InventoryCommand.AddWeapon(id));
            if (result.Applied) return;

            Debug.LogError("[PlayerInventory] Starting weapon '" + id + "' was refused: " +
                           result.Reason + " (Invent.as:1600-1615).");
        }

        /// <summary>
        /// Binds a favourite. There is no <see cref="InventoryCommandKind"/> for this, so it is the
        /// one starting-grant step that does not take the seam — recorded rather than worked around
        /// by stretching an existing kind to mean something new.
        ///
        /// <para><b>Both of addMin's favourites are weapons, and both are still skipped — but for a
        /// narrower reason than before.</b> Weapons now <i>are</i> in the inventory
        /// (<see cref="GrantWeapon"/> runs first, as the Oracle's order has it), so "the inventory cannot
        /// hold a weapon" is no longer true. What is still true is that
        /// <c>GameInventory.SetFavorite</c> resolves through the <i>item</i> dictionary and its storage is
        /// a <c>GameItemInstance[]</c> — an item-typed slot cannot hold a <c>GameWeaponInstance</c>
        /// without the union the Oracle gets for free by keeping one <c>Item</c> class. So the remaining
        /// gap is the favourites array's element type, not the weapon path. Skipping with a stated reason
        /// keeps the console honest; an error would read as "the favourite code is broken". The same code
        /// binds them the moment the slot type is widened.</para>
        /// </summary>
        private void GrantFavourite(StartingLoadout.Favourite favourite)
        {
            if (!_inventory.HasItem(favourite.ItemId))
            {
                Debug.Log("[PlayerInventory] Favourite '" + favourite.ItemId + "' (slot " +
                          favourite.Slot + ") was not bound: it names a weapon, and " +
                          "GameInventory.SetFavorite resolves through the item dictionary " +
                          "(GameInventory.cs:477-483). It binds as soon as the port can hold a " +
                          "weapon (Invent.as:1613-1614).");
                return;
            }

            if (_inventory.SetFavorite(favourite.ItemId, favourite.Slot)) return;

            Debug.LogError("[PlayerInventory] Could not bind favourite '" + favourite.ItemId +
                           "' to slot " + favourite.Slot + " (Invent.as:1613-1614).");
        }

        /// <summary>
        /// Whether the content registry has been filled yet. Probing for <b>any</b> item id rather
        /// than for one of the starting ids keeps "not initialised yet" and "that row is missing"
        /// as different answers — the first retries, the second is a data bug worth an error.
        /// </summary>
        private bool RegistryPopulated()
        {
            if (_registry == null) return false;

            System.Collections.Generic.IEnumerable<string> ids = _registry.GetAllBareIds(ContentType.Item);
            if (ids == null) return false;

            using (System.Collections.Generic.IEnumerator<string> cursor = ids.GetEnumerator())
            {
                return cursor.MoveNext();
            }
        }

        private void Update()
        {
            SeedStartingLoadout();
        }

        // ── Reads for the debug overlay ───────────────────────────────────────

        /// <summary>
        /// Everything held in the item dictionary, for the F2 inventory tab. Read-only: the overlay must
        /// mutate through <see cref="Submit"/>, and handing out the live dictionary is how that gets
        /// bypassed by accident. Null before <see cref="Construct"/>.
        /// </summary>
        public IReadOnlyDictionary<string, GameItemInstance> Items => _inventory?.Items;

        /// <summary>Id → item row, or null when the id is unknown. For the overlay's pickers and labels.</summary>
        public ItemDefinition ResolveItem(string itemId)
            => string.IsNullOrEmpty(itemId) ? null : _itemResolver?.Invoke(itemId);

        /// <summary>
        /// Id → weapon row, or null when the id is unknown or is not a weapon — the counterpart to
        /// <see cref="ResolveItem"/> for the other content type.
        ///
        /// <para><b>Why a caller has to be able to ask this at all.</b> A loot roll carries an id and a
        /// tip, and the container presenters used to ask only <c>ResolveItem</c>, so every weapon roll
        /// came back <c>null</c> and was dropped with a warning. <c>ResolveItem</c> cannot answer for a
        /// weapon id — not "cannot yet", but <i>cannot</i>: <c>ContentType.Weapon</c> and
        /// <c>ContentType.Item</c> are disjoint tables in the Oracle too (<c>d.weapon</c> vs
        /// <c>d.item</c>), so no widening of the item resolver would ever help.</para>
        /// </summary>
        public WeaponDefinition ResolveWeapon(string weaponId)
            => string.IsNullOrEmpty(weaponId) ? null : _weaponResolver?.Invoke(weaponId);

        /// <summary>
        /// Every item id the content registry knows, ordinal-sorted so the debug list has a stable order
        /// between repaints. Empty (never null) before <see cref="Construct"/>.
        ///
        /// <para><b>This is the enumeration <c>ResolveItem</c> could not provide.</b> A resolver answers
        /// "what is id X?"; a picker needs "what ids exist?" — the two are different queries and the
        /// overlay needed the second one to stop making the user type ids from memory. It is read-only
        /// and cached: the overlay must still mutate through <see cref="Submit"/>.</para>
        ///
        /// <para>Weapons are <b>not</b> here, and that is a content-type fact rather than a gap: this
        /// enumerates <c>ContentType.Item</c>, and a weapon id lives in <c>ContentType.Weapon</c>. It is
        /// still spawnable — <see cref="ResolveWeapon"/> answers for it and <c>WorldItemPickup.Spawn</c>
        /// takes a weapon row — it simply is not offered by a picker built from this list. Armour rows
        /// <i>are</i> item rows (they carry <c>ItemType.Equipment</c>), so they appear — on the Misc page,
        /// which is where the page partition puts <c>Equipment</c>.</para>
        /// </summary>
        public IReadOnlyList<string> AvailableItemIds
        {
            get
            {
                if (_registry == null) return System.Array.Empty<string>();

                if (_availableItemIds == null)
                {
                    // new List<T>(IEnumerable<T>) rather than .ToList(), so this file needs no LINQ.
                    _availableItemIds = new List<string>(_registry.GetAllBareIds(ContentType.Item));
                    _availableItemIds.Sort(StringComparer.Ordinal);
                }

                return _availableItemIds;
            }
        }

        /// <summary>
        /// Drop the <see cref="AvailableItemIds"/> cache. For the debug picker's refresh button: the
        /// registry is filled once at boot, so a list that looks short usually means
        /// <c>GameDatabase.Initialize()</c> has not run yet rather than a stale cache — but a mod can
        /// legitimately register more content later.
        /// </summary>
        public void InvalidateAvailableItems() => _availableItemIds = null;

        // ── Dropping ─────────────────────────────────────────────────────────

        /// <summary>
        /// Drop <paramref name="quantity"/> of <paramref name="itemId"/> into the world at
        /// <paramref name="position"/>, so the player — or, later, a co-op peer — can pick it up again.
        /// AS3 <c>Invent.drop()</c>.
        ///
        /// <para><b>Two halves, in the order that cannot lose an item.</b> AS3 spawns the <c>Loot</c> and
        /// then calls <c>minusItem</c> (<c>:1544-1548</c>). The port has to route the removal through the
        /// command seam so the netcode can own it, which opens a window between the two halves — so the
        /// order here is <b>spawn → submit → despawn on rejection</b>. That makes the failure mode "the
        /// item never left the bag", never "the item is gone from both the bag and the world".</para>
        ///
        /// <para><b>AS3's guards.</b> <c>drop()</c> refuses at base and as Alicorn (<c>:1537-1540</c>).
        /// Neither is modelled here: the port has no "at base" location flag on this path, and Alicorn is
        /// a spell/race state with no player-side accessor. Both are noted rather than silently skipped —
        /// a drop that works where AS3 refuses is a divergence someone will eventually report as a bug in
        /// the opposite direction.</para>
        /// </summary>
        /// <param name="itemId">Item id to drop.</param>
        /// <param name="quantity">How many; clamped to what is held, as AS3 does.</param>
        /// <param name="position">Where it lands. AS3 uses <c>owner.X, owner.Y - owner.scY / 2</c>.</param>
        /// <param name="error">Stated reason on failure; null on success.</param>
        public bool DropItem(string itemId, int quantity, Vector3 position, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(itemId)) { error = "no item id"; return false; }
            if (quantity <= 0) { error = $"quantity must be positive (was {quantity})"; return false; }
            if (_inventory == null) { error = "the inventory does not exist yet"; return false; }

            GameItemInstance held = _inventory.GetItem(itemId);
            int heldCount = held?.Quantity ?? 0;
            if (heldCount <= 0) { error = $"nothing to drop: no '{itemId}' held"; return false; }

            // AS3 clamps rather than refusing: `if (param2 > this.items[param1].kol) param2 = kol`.
            int amount = Math.Min(quantity, heldCount);

            // A pickup with no definition has no icon, no name and no sound. Refuse rather than spawn a
            // blank square the player cannot identify.
            ItemDefinition definition = _itemResolver?.Invoke(itemId);
            if (definition == null)
            {
                error = $"no item row for '{itemId}', so the drop would have no icon or name";
                return false;
            }

            // autoCollect: false is the oracle's own value, not a default chosen here. AS3's
            // `Invent.drop()` passes `nauto = false` (Invent.as:1550), so a dropped item must be picked up
            // deliberately — dropping something and having the walk-over magnet drag it straight back
            // would be absurd. The parameter is written out so the mapping is visible rather than implied
            // by an omitted argument.
            WorldItemPickup pickup = WorldItemPickup.Spawn(definition, amount, position, autoCollect: false);
            if (pickup == null) { error = "the world refused the pickup"; return false; }

            InventoryCommandResult result = Submit(InventoryCommand.DropItem(itemId, amount));
            if (!result.Applied)
            {
                // The seam is the authority on what left the bag. If it said no, the world must not be
                // left holding a copy — that would be a duplication bug wearing a rejection's clothes.
                pickup.Despawn();
                error = result.Reason;
                return false;
            }

            return true;
        }

        // ── Item use (AS3 Invent.useItem) ─────────────────────────────────────

        /// <summary>
        /// AS3 <c>World.w.t_battle &gt; 0</c> — the post-combat lockout that forbids reading, eating and
        /// drinking. <b>Not ported</b>: nothing in the port counts battle frames yet, so this reads
        /// false and every combat-guarded branch behaves as if the fight is over. Left as a seam rather
        /// than hard-coded so landing the counter is one assignment, not an edit here.
        /// </summary>
        public Func<bool> InBattleProvider { get; set; }

        /// <summary>
        /// AS3 <c>World.w.loc.base</c> — the two hard-inventory gates read it
        /// (<c>Invent.as:643, :656</c>). Defaults to <c>false</c>, which is the stricter answer: outside
        /// a base is where the Oracle refuses.
        /// </summary>
        public Func<bool> AtBaseProvider { get; set; }

        /// <summary>
        /// AS3 <c>World.w.alicorn</c> — the end-game state in which food, spells, pets and stealth are
        /// all refused. Defaults to false; the port has no alicorn state on the player.
        /// </summary>
        public Func<bool> AlicornProvider { get; set; }

        /// <summary>
        /// <c>Invent.useItem(id)</c> (<c>Invent.as:546-731</c>) — use one unit of an item from the bag.
        ///
        /// <para><b>This is the missing half of "F2 gives, the PipBuck uses".</b> The inventory page
        /// had no command to call, so every click on a potion, a spell, a book or a skill orb logged
        /// <c>"the port has no inventory command for it"</c> and did nothing. The branch ladder itself
        /// lives in <see cref="ItemUseRules"/> (pure, offline-testable); this method only carries out
        /// the answer, because carrying out means touching the unit, the spell book and the effects.</para>
        ///
        /// <para><b>Reads the raw <c>&lt;item&gt;</c> row, not the imported asset.</b> Exactly as the
        /// inventory page's info pane does: <see cref="ItemDefinition"/> carries none of
        /// <c>@hhp</c>/<c>@hmana</c>/<c>@effect</c>/<c>@ad</c>/<c>@perk</c>, which are the whole payload
        /// of a potion. The row is read from <c>Resources/Loot/AllData</c> and cached.</para>
        ///
        /// <para><b>What is deliberately not carried out, and why.</b> Poison/cut tracks
        /// (<c>@hpoison</c>/<c>@hcut</c>), the pet summons (<c>@pet</c>), weapon painting
        /// (<c>@paint</c>), the readable-document dialog (<c>@text</c>) and the three crafting benches
        /// have no ported subsystem behind them. Each one is <i>named</i> at the line where it would
        /// run and returns false rather than pretending: a potion that silently healed nothing is
        /// indistinguishable from a broken potion. Radiation (<c>@hrad</c>) used to be on that list and
        /// is no longer — it lands on <see cref="Radiation"/>.</para>
        /// </summary>
        /// <param name="itemId">The item id, variant suffix already stripped.</param>
        /// <returns>True when the Oracle's branch ran (whether or not the item was consumed).</returns>
        public bool UseItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            if (_inventory == null)
            {
                Debug.LogWarning("[PlayerInventory] UseItem: the inventory does not exist yet.");
                return false;
            }

            // Invent.as:576-583 — the stack check comes BEFORE the row lookup, and a stack at zero is a
            // silent false: the row still exists in `items` after being used up.
            GameItemInstance held = _inventory.GetItem(itemId);
            if (held == null || held.Quantity <= 0) return false;

            ItemDefinition definition = _itemResolver?.Invoke(itemId);
            string attrs = ItemRowAttributes(itemId);

            // The tip the ladder branches on is the row's own `@tip` (Invent.as:583 reads `xml.@tip`).
            // The asset's `legacyTip` is the same string when the importer read it; the raw row wins,
            // because that is what the Oracle reads and an older asset may predate the field.
            string tip = Attr(attrs, "tip");
            if (string.IsNullOrEmpty(tip) && definition != null) tip = definition.legacyTip;

            bool hasPaint = AttrPresent(attrs, "paint");
            bool hasText = AttrPresent(attrs, "text");
            bool hasPet = AttrPresent(attrs, "pet");
            bool hasDifficulty = AttrPresent(attrs, "chdif");

            bool inBattle = InBattleProvider != null && InBattleProvider();
            bool atBase = AtBaseProvider != null && AtBaseProvider();
            bool hardInventory = _inventory.HardInventory;
            bool alicorn = AlicornProvider != null && AlicornProvider();

            ItemUseDecision decision = ItemUseRules.Decide(itemId, tip, hasPaint, hasText, hasPet,
                hasDifficulty, inBattle, atBase, hardInventory, alicorn);

            if (decision.IsRefused)
            {
                // The Oracle prints the key through `gui.infoText`; the port has no info strip on the
                // inventory page, so the refusal is logged with the key intact — the same treatment
                // `PipPageInvView.OnRowClicked` already gives `noAct`.
                Debug.Log(decision.RefusalKey != null
                    ? $"[PlayerInventory] '{itemId}' refused ({decision.RefusalKey})."
                    : $"[PlayerInventory] '{itemId}' has no use in this build (AS3 useItem falls through).");
                return false;
            }

            var stats = GetComponentInParent<PFE.Systems.RPG.CharacterStats>();
            bool ran;

            switch (decision.Outcome)
            {
                case ItemUseOutcome.Spell:
                    // Invent.as:634-638 — `gg.changeSpell(id)`, a SELECTION. The cast happens later, on
                    // the Def key (UnitPlayer.as:2226-2284), which PlayerSpellCaster already drives.
                    ran = SelectSpellItem(itemId);
                    break;

                case ItemUseOutcome.Potion:
                    ran = UsePotion(itemId, attrs, tip, stats);
                    break;

                case ItemUseOutcome.Book:
                    ran = ReadSkillBook(itemId, attrs, stats);
                    break;

                case ItemUseOutcome.SkillPoint:
                    // Invent.as:654-661 — `pers.addSkillPoint(1,true)`. The port's `GrantSkillPoints`
                    // is the same write; the second argument is the Oracle's "announce" flag.
                    if (stats == null)
                    {
                        Debug.LogWarning("[PlayerInventory] No CharacterStats, so the skill point from " +
                                         $"'{itemId}' cannot be granted.");
                        ran = false;
                        break;
                    }
                    stats.GrantSkillPoints(1);
                    ran = true;
                    break;

                case ItemUseOutcome.RepairWeapon:
                    ran = RepairHeldWeapon(itemId);
                    break;

                case ItemUseOutcome.Stealth:
                    ran = ApplyItemEffect("stealth");
                    break;

                case ItemUseOutcome.Difficulty:
                    Debug.Log($"[PlayerInventory] '{itemId}' changes the difficulty to '{Attr(attrs, "chdif")}'. " +
                              "AS3 `game.changeDif` is not ported, so the tier is unchanged; the item is " +
                              "NOT consumed, because a refused change must not cost the player a token.");
                    return false;

                case ItemUseOutcome.Workshop:
                    // Invent.as:563-571 — `pip.workTip = id; pip.onoff(7)`. Page 7 is still a stub, and
                    // the device owns the open, so this is the seam a caller would use.
                    Debug.Log($"[PlayerInventory] '{itemId}' is a crafting bench: AS3 opens page 7. " +
                              "Page 7 is not ported, so nothing opened; the item is not consumed.");
                    return false;

                case ItemUseOutcome.Reading:
                    Debug.Log($"[PlayerInventory] '{itemId}' carries a readable document (AS3 " +
                              "`gui.dialog(@text)`), which the port has no dialog host for. The item is " +
                              "not consumed — the Oracle returns before `minusItem`.");
                    return false;

                case ItemUseOutcome.Pet:
                    Debug.Log($"[PlayerInventory] '{itemId}' summons pet '{Attr(attrs, "pet")}'. The port " +
                              "has no pet model, so nothing was summoned; the item is not consumed.");
                    return false;

                case ItemUseOutcome.Paint:
                    Debug.Log($"[PlayerInventory] '{itemId}' paints the held weapon '{Attr(attrs, "paint")}'. " +
                              "Weapon paint is not ported; the item is not consumed.");
                    return false;

                default:
                    return false;
            }

            if (!ran) return false;

            // Invent.as:721 — `minusItem`. Five branches return before this line; see
            // ItemUseRules.ConsumesItem, which is the single place that list is written down.
            if (ItemUseRules.ConsumesItem(decision.Outcome))
            {
                InventoryCommandResult removed = Submit(InventoryCommand.RemoveItem(itemId, 1));
                if (!removed.Applied)
                {
                    Debug.LogWarning($"[PlayerInventory] '{itemId}' was used but could not be consumed: " +
                                     removed.Reason);
                }
            }

            // Invent.as:722-726 — the hotkey strip and the mass total are both stale after a use.
            _inventory.CalculateMass();
            return true;
        }

        /// <summary>
        /// The player's radiation state, bound by <c>PlayerController</c> once its ticker exists.
        ///
        /// <para><b>Nullable on purpose.</b> The inventory is constructed in its own <c>Awake</c> and
        /// can be exercised without a player (tests, the editor's item browser); a null state means
        /// "no radiation model here", which the two call sites report rather than throw.</para>
        /// </summary>
        public PFE.Entities.Player.PlayerRadiationState Radiation { get; set; }

        /// <summary>
        /// <c>usePotion</c>'s healing block (<c>Invent.as:390-500</c>) plus its guards
        /// (<c>:300-350</c>).
        ///
        /// <para><b>The two multipliers are different fields, and that is the Oracle's.</b>
        /// <c>healMult</c> (default <b>0.4</b>) scales <c>@hhp</c>/<c>@hhplong</c>/<c>@hrad</c>;
        /// <c>healManaMult</c> (default 1) scales <c>@hmana</c> only. Using one for both would make every
        /// stim heal 2.5× its listed amount or a mana cell heal a fifth of it.</para>
        /// </summary>
        private bool UsePotion(string itemId, string attrs, string tip, PFE.Systems.RPG.CharacterStats stats)
        {
            if (stats == null)
            {
                Debug.LogWarning($"[PlayerInventory] No CharacterStats, so '{itemId}' cannot heal anything.");
                return false;
            }

            // Invent.as:337-345 — `minmed`: a stim above the player's Medic skill is refused outright.
            int minMedic = (int)AttrF(attrs, "minmed", 0f);
            if (minMedic > 0 && stats.GetSkillLevel("medic") < minMedic)
            {
                Debug.Log($"[PlayerInventory] '{itemId}' needs Medic {minMedic}, the player has " +
                          $"{stats.GetSkillLevel("medic")} (AS3 `needSkill`).");
                return false;
            }

            // Invent.as:310-336 — the "nothing to heal" refusals. Each is a real guard, not politeness:
            // a stim used at full health is NOT consumed in the Oracle.
            string heal = Attr(attrs, "heal");
            if (heal == "organ" && stats.headHp >= stats.InMaxHP && stats.torsHp >= stats.InMaxHP &&
                stats.legsHp >= stats.InMaxHP)
            {
                Debug.Log($"[PlayerInventory] '{itemId}' refused (noHeal): every organ is already whole.");
                return false;
            }
            if (heal == "blood" && stats.InMaxHP - stats.bloodHp < 1f)
            {
                Debug.Log($"[PlayerInventory] '{itemId}' refused: blood is already full.");
                return false;
            }
            if (heal == "mana" && stats.MaxMana - stats.manaHp < 1f)
            {
                Debug.Log($"[PlayerInventory] '{itemId}' refused: the mana organ is already full.");
                return false;
            }
            // :317-320 — `if(pot.@heal == "rad" && gg.rad < 1) infoText("noMedic")`. A RadAway at zero
            // exposure is NOT consumed in the oracle. With no radiation state bound there is nothing to
            // test, so the guard is skipped rather than refusing a dose the port cannot measure.
            if (heal == "rad" && Radiation != null && Radiation.Rad < 1f)
            {
                Debug.Log($"[PlayerInventory] '{itemId}' refused (noMedic): the player has no radiation.");
                return false;
            }
            // `heal == "poison"` is NOT gated: the port has no poison accumulator to test against, and
            // refusing an antidote for a condition the port cannot represent would be worse than
            // letting it through and healing nothing.

            float healMult = stats.healMult;              // Pers.as:321, default 0.4
            float healManaMult = stats.healManaMult;      // Pers.as:321, default 1

            // :390-405 — `gg.heal(hhp, 0)` then `gg.heal(hhplong, 1)`. The port has one HP pool on
            // UnitStats, so the two land on the same counter; AS3's "long-term" half is a separate
            // accumulator in Pers, not a separate health bar.
            float hhp = AttrF(attrs, "hhp") * healMult;
            float hhplong = AttrF(attrs, "hhplong") * healMult;
            if (hhp + hhplong > 0f) stats.BoundUnitStats?.Heal(hhp + hhplong);

            // :406-419 — organ tracks. `pers.heal(amount, tip)` is HealOrgan; the tip numbers are AS3's
            // (1 head, 2 torso, 3 legs, 4 all physical, 5 blood, 6 mana).
            float horgan = AttrF(attrs, "horgan");
            if (horgan > 0f) stats.HealOrgan(horgan, 4);

            float horgans = AttrF(attrs, "horgans");
            if (horgans > 0f) stats.HealOrgan(horgans, 4);

            float hblood = AttrF(attrs, "hblood");
            if (hblood > 0f) stats.HealOrgan(hblood, 5);

            float hmana = AttrF(attrs, "hmana");
            if (hmana > 0f) stats.HealOrgan(hmana * healManaMult, 6);

            // :420-427 — `@hcut` / `@hpoison` are damage *types* on the Oracle's unit, and the port
            // tracks neither. Named rather than silently dropped.
            if (AttrF(attrs, "hcut") > 0f || AttrF(attrs, "hpoison") > 0f)
            {
                Debug.Log($"[PlayerInventory] '{itemId}' also heals cut/poison damage, which the port " +
                          "does not track (no damage-type conditions on UnitStats).");
            }

            // :430-433 — `@hrad`, scaled by healMult like every other heal. `heal(amount, 2)` in AS3 is
            // `rad -= amount` floored at zero (UnitPlayer.as:3173-3178), so the state owns the clamp and
            // returns what it actually removed.
            float hrad = AttrF(attrs, "hrad");
            if (hrad > 0f)
            {
                if (Radiation != null)
                {
                    Radiation.RemoveRad(hrad * healMult);
                }
                else
                {
                    // Only reachable before the player's radiation ticker exists (a hand-built rig, or
                    // an inventory used outside the player). Named rather than silent, because the
                    // failure mode is a RadAway that is consumed and does nothing.
                    Debug.Log($"[PlayerInventory] '{itemId}' removes radiation, but this inventory has no " +
                              "PlayerRadiationState bound — the dose was lost.");
                }
            }

            // :429-439 — `@hpurif` clears every curse-type effect. The port's effect set is the
            // authority, so the removal goes through it rather than through a trigger table.
            if (AttrF(attrs, "hpurif") > 0f) ApplyItemEffect(null, "curse");

            // :446-449 — `@perk`.
            string perk = Attr(attrs, "perk");
            if (!string.IsNullOrEmpty(perk) && stats != null) stats.AddPerk(perk);

            // :450-462 — `@effect`, with the `him` level/time scaling applied to the duration.
            string effect = Attr(attrs, "effect");
            if (!string.IsNullOrEmpty(effect)) ApplyItemEffect(effect);

            // :463-466 — `@alc` adds the `drunk` effect for `alc * 10` ticks.
            float alc = AttrF(attrs, "alc");
            if (alc > 0f) ApplyItemEffect("drunk", null, alc * 10f);

            // :467-500 — the addiction roll. `n = round(random * (admax - admin) + admin)`, scaled by
            // the two him multipliers, then accumulated and clamped to `admax`.
            string chem = Attr(attrs, "ad");
            if (!string.IsNullOrEmpty(chem)) RollAddiction(stats, chem, attrs);

            return true;
        }

        /// <summary>
        /// <c>@ad</c> — one addiction roll (<c>Invent.as:467-500</c>).
        ///
        /// <para>The three thresholds (<c>ad1</c>/<c>ad2</c>/<c>ad3</c>) each print a warning exactly once,
        /// on the roll that crosses them — which is why the "before" value is captured rather than
        /// compared after the write.</para>
        /// </summary>
        private static void RollAddiction(PFE.Systems.RPG.CharacterStats stats, string chem, string attrs)
        {
            float min = AttrF(attrs, "admin");
            float max = AttrF(attrs, "admax");
            // AS3 scales the roll by `himBadMult * himBadDif`. Neither field is ported — a grep for both
            // names finds nothing — so the roll is unscaled, which is the Oracle's own behaviour when
            // no perk has touched either multiplier (both default to 1).
            float rolled = UnityEngine.Mathf.Round(UnityEngine.Random.Range(min, max));

            float before = stats.Addiction(chem);
            float after = Mathf.Min(before + rolled, stats.admax);
            stats.SetAddiction(chem, after);

            if (before < stats.ad3 && after >= stats.ad3)
                Debug.Log($"[PlayerInventory] Addiction to '{chem}' reached level 3 ({after:0}).");
            else if (before < stats.ad2 && after >= stats.ad2)
                Debug.Log($"[PlayerInventory] Addiction to '{chem}' reached level 2 ({after:0}).");
            else if (before < stats.ad1 && after >= stats.ad1)
                Debug.Log($"[PlayerInventory] Addiction to '{chem}' reached level 1 ({after:0}).");
        }

        /// <summary>
        /// <c>tip == "book"</c> (<c>Invent.as:639-653</c>) — a skill book or a perk book, and the
        /// <c>lbook</c> it always yields.
        ///
        /// <para><b>The book is consumed and a <c>lbook</c> appears.</b> AS3 does
        /// <c>++this.items["lbook"].kol</c> on the same frame it calls <c>minusItem</c>, so the pair
        /// reads as "one read book" — which is a crafting component, not a duplicate.</para>
        /// </summary>
        private bool ReadSkillBook(string itemId, string attrs, PFE.Systems.RPG.CharacterStats stats)
        {
            if (stats == null)
            {
                Debug.LogWarning($"[PlayerInventory] No CharacterStats, so '{itemId}' cannot teach anything.");
                return false;
            }

            string perk = Attr(attrs, "perk");
            if (!string.IsNullOrEmpty(perk))
            {
                stats.AddPerk(perk);
            }
            else if (!stats.AddSkill(itemId, 1))
            {
                // `pers.upSkill(id)` — the item's own id IS the skill id for a skill book, and the raise
                // does NOT spend the point pool (Pers.as:1191 `addSkill(id, n)` with spendPoints false).
                Debug.LogWarning($"[PlayerInventory] '{itemId}' is a book with no @perk and no skill of " +
                                 "that id, so reading it taught nothing (AS3 `pers.upSkill` would no-op).");
            }

            // The read-book counter. `plusItem` would be the general path, but this row must exist even
            // when the player has never held one, so it is created on demand.
            ItemDefinition lbook = _itemResolver?.Invoke("lbook");
            if (lbook != null) _inventory.AddItem(lbook, 1);

            return true;
        }

        /// <summary>
        /// <c>id == "rep"</c> (<c>Invent.as:665-670</c>) — the weapon repair kit, which repairs the
        /// <b>held</b> weapon and is refused when there is nothing to repair.
        /// </summary>
        private bool RepairHeldWeapon(string itemId)
        {
            string held = _inventory.CurrentWeaponId;
            if (string.IsNullOrEmpty(held))
            {
                Debug.Log($"[PlayerInventory] '{itemId}' refused: no weapon is held to repair.");
                return false;
            }

            if (!_inventory.Weapons.TryGetValue(held, out GameWeaponInstance weapon) || weapon == null)
            {
                Debug.Log($"[PlayerInventory] '{itemId}' refused: the held weapon '{held}' is not in the bag.");
                return false;
            }

            if (weapon.CurrentHealth >= weapon.MaxHealth)
            {
                Debug.Log($"[PlayerInventory] '{itemId}' refused: '{held}' is already at full condition.");
                return false;
            }

            weapon.Repair(weapon.MaxHealth);
            return true;
        }

        /// <summary>
        /// <c>gg.changeSpell(id)</c> — select the spell, or toggle the selection off.
        ///
        /// <para><b>Learn first, then select.</b> <c>Select</c> is a lookup, so an id the book has never
        /// seen is treated as "unknown" and <i>clears</i> the selection — the Oracle relies on that for
        /// the deselect case (<c>Invent.as:849-852</c>). The item in the bag is what proves ownership:
        /// <c>invent.spells[id]</c> is populated by <c>addSpell</c> when the item is taken, before
        /// <c>useItem</c> can be reached.</para>
        /// </summary>
        private bool SelectSpellItem(string itemId)
        {
            if (_caster == null) _caster = GetComponentInParent<PlayerSpellCaster>();
            if (_caster == null)
            {
                Debug.LogWarning($"[PlayerInventory] '{itemId}' is a spell but no PlayerSpellCaster is " +
                                 "attached, so it cannot be selected.");
                return false;
            }

            _caster.LearnSpell(itemId);
            Spell selected = _caster.SelectSpell(itemId);

            Debug.Log(selected != null
                ? $"[PlayerInventory] '{itemId}' selected as the current spell (AS3 changeSpell) — " +
                  "cast it with the Def key."
                : $"[PlayerInventory] '{itemId}' toggled OFF (AS3 changeSpell is a toggle).");
            return true;
        }

        /// <summary>
        /// Apply one effect to the player's unit, optionally removing another first.
        ///
        /// <para>Returns false when there is no unit to apply it to, which is the honest answer: an
        /// effect with nowhere to live is not an effect.</para>
        /// </summary>
        private bool ApplyItemEffect(string effectId, string removeId = null, float durationTicks = 0f)
        {
            var unit = GetComponentInParent<PFE.Entities.Units.UnitController>();
            PFE.Systems.Effects.ActiveEffectSet effects = unit != null ? unit.Effects : null;
            if (effects == null)
            {
                Debug.LogWarning($"[PlayerInventory] no effect set on the player, so " +
                                 $"'{effectId ?? removeId}' could not be applied.");
                return false;
            }

            if (!string.IsNullOrEmpty(removeId)) effects.RemoveEffect(removeId);
            if (string.IsNullOrEmpty(effectId)) return true;

            // `gg.addEffect(id, 0, ticks)` — a zero duration means "the template's own duration", which
            // is how the effect table's `@t` is honoured.
            effects.AddEffect(effectId, 0f, (int)Math.Round(durationTicks));
            return true;
        }

        // ── Raw item row access ───────────────────────────────────────────────

        /// <summary>The AllData source text, read once per process. Null when the asset is absent.</summary>
        private static string s_allData;
        private static bool s_allDataRead;

        /// <summary>
        /// <c>&lt;item&gt;</c> rows by id, built once from <see cref="s_allData"/>. The whole row is
        /// kept, not just its attribute run, because two consumers need two halves of it: the
        /// attribute readers below want <see cref="PFE.Systems.Items.ItemXmlRow.Attrs"/>, and the
        /// <c>&lt;sk&gt;</c> resolver wants <see cref="PFE.Systems.Items.ItemXmlRow.Body"/>.
        /// </summary>
        private static Dictionary<string, PFE.Systems.Items.ItemXmlRow> s_itemRows;

        /// <summary>
        /// The lazy <c>AllData</c> read, shared by both row tables. Runs once per process; the
        /// <see cref="s_allDataRead"/> guard is what keeps a failed read from retrying every frame.
        /// </summary>
        private static void EnsureAllDataRead()
        {
            if (s_allDataRead) return;
            s_allDataRead = true;

            try
            {
                TextAsset asset = Resources.Load<TextAsset>("Loot/AllData");
                s_allData = asset != null ? asset.text : null;
            }
            catch (Exception)
            {
                // Offline (the test wall) there is no Resources system. An empty table is the
                // honest answer there: every item then behaves as "no row", which the callers
                // already report.
                s_allData = null;
            }
        }

        /// <summary>
        /// The <c>&lt;item&gt;</c> row for an id. False when the id is absent or the mirror is
        /// unavailable (offline test wall, or the TextAsset was not shipped).
        /// </summary>
        private static bool TryGetItemRow(string itemId, out PFE.Systems.Items.ItemXmlRow row)
        {
            row = default;
            if (string.IsNullOrEmpty(itemId)) return false;

            EnsureAllDataRead();
            if (s_allData == null) return false;

            if (s_itemRows == null)
            {
                s_itemRows = new Dictionary<string, PFE.Systems.Items.ItemXmlRow>(StringComparer.Ordinal);
                foreach (PFE.Systems.Items.ItemXmlRow parsed in PFE.Systems.Items.ItemXmlRows.Parse(s_allData))
                {
                    if (!string.IsNullOrEmpty(parsed.Id) && !s_itemRows.ContainsKey(parsed.Id))
                        s_itemRows[parsed.Id] = parsed;
                }
            }

            return s_itemRows.TryGetValue(itemId, out row);
        }

        /// <summary>
        /// The root attribute run of the <c>&lt;weapon&gt;</c> row for an id, or empty when the id is
        /// absent. <b>Weapons only</b> — see <see cref="WeaponRowMess"/> for why the two tables cannot
        /// be read through one lookup.
        /// </summary>
        private static string WeaponRowAttributes(string weaponId)
        {
            if (string.IsNullOrEmpty(weaponId)) return string.Empty;

            EnsureAllDataRead();
            if (s_allData == null) return string.Empty;

            // `AllDataElements` already exists, is depth-aware and is offline-testable — the port has
            // had a `<weapon>` row reader since before this call site. Reaching for `AllDataElements`
            // rather than a second regex is the whole point of that class's existence.
            return PFE.Systems.Weapons.AllDataElements.TryGet(s_allData, "weapon", weaponId, out var element)
                ? element.Attrs
                : string.Empty;
        }

        /// <summary>
        /// The raw attribute run of an <c>&lt;item&gt;</c> row, or empty when the id is absent.
        ///
        /// <para><b>Why the runtime reads the XML at all.</b> <see cref="ItemDefinition"/> carries the
        /// identity of an item and none of its effect payload — <c>@hhp</c>, <c>@effect</c>,
        /// <c>@ad</c>, <c>@perk</c>, <c>@alc</c>. The Oracle reads <c>inv.items[id].xml</c>, i.e. the
        /// element itself, so this reads the same element from the port's mirror of the file. The
        /// inventory page's info pane does exactly this already; this is the second reader, and it is
        /// kept here rather than shared so the page stays a pure view.</para>
        /// </summary>
        private static string ItemRowAttributes(string itemId)
            => TryGetItemRow(itemId, out PFE.Systems.Items.ItemXmlRow row) ? row.Attrs : string.Empty;

        /// <summary>One raw attribute value, or empty when absent.</summary>
        private static string Attr(string attrs, string name)
            => string.IsNullOrEmpty(attrs) ? string.Empty : PFE.Systems.Weapons.WeaponXmlAttrs.Attr(attrs, name);

        /// <summary>Whether the attribute is present at all — the Oracle's <c>@x.length()</c> test.</summary>
        private static bool AttrPresent(string attrs, string name)
            => !string.IsNullOrEmpty(Attr(attrs, name));

        /// <summary>
        /// AS3 <c>@mess</c> for an item id, read from the shipped row — the tutorial-popup key
        /// (<c>Invent.as:1293-1300</c>). Empty when the row has none, which is the common case.
        ///
        /// <para><b>Public, and the only accessor on this class that is.</b> Its caller is the pickup
        /// feedback path (<c>ItemPickupFeedback.ResolveMessKey</c>), which is not the inventory: the
        /// popup is fired from <c>Invent.take</c> in the Oracle, but the port's take path lives on
        /// <c>WorldItemPickup</c> and has no inventory instance in hand. Exposing the row reader is
        /// cheaper than moving the whole AllData mirror out of this class, and it keeps exactly one
        /// parse of the file — which is the reason the mirror is here at all.</para>
        ///
        /// <para><b>Why the fallback exists.</b> <see cref="ItemDefinition.mess"/> is empty on every
        /// asset until the item import runs, and the popup must not depend on that — the same
        /// arrangement that made <c>possLockPick</c> work without a re-import.</para>
        /// </summary>
        public static string ItemRowMess(string itemId) => Attr(ItemRowAttributes(itemId), "mess");

        /// <summary>
        /// AS3 <c>@mess</c> for a <b>weapon</b> id, read from the shipped <c>&lt;weapon&gt;</c> row —
        /// the tutorial-popup key for a weapon pickup (<c>Invent.as:1293-1300</c>). Empty when the row
        /// has none, which is 209 of the 213 carried rows.
        ///
        /// <para><b>A second public accessor, and it is not a duplicate of
        /// <see cref="ItemRowMess"/>.</b> The Oracle's weapon take path reads a different XMLList:
        /// <c>Invent.take</c>'s <c>if(l.tip == Item.L_WEAPON)</c> branch indexes
        /// <c>AllData.d.weapon</c> (and calls <c>addWeapon</c> at <c>:1101</c>), not
        /// <c>AllData.d.item</c>. A single item-first lookup would be wrong, not merely incomplete:
        /// measured, <b>32 ids exist in both tables</b> (<c>bomb</c>, <c>cryogr</c>, <c>dinamit</c>,
        /// <c>fgren</c> …), so a fallback chain would resolve those from whichever table it happened to
        /// try first. Table-explicit is the only shape that cannot.</para>
        ///
        /// <para><b>Public for the same reason <see cref="ItemRowMess"/> is:</b> its caller is the
        /// pickup-feedback path on <c>WorldItemPickup</c>, which has no inventory instance in hand.
        /// The mirror stays in this class so there is still exactly one parse of the file.</para>
        ///
        /// <para><b>Why the fallback exists.</b> <see cref="PFE.Data.Definitions.WeaponDefinition.mess"/>
        /// is empty on every asset until the weapon import runs, and the popup must not depend on that
        /// — the same arrangement that made <c>possLockPick</c> work without a re-import.</para>
        /// </summary>
        public static string WeaponRowMess(string weaponId) => Attr(WeaponRowAttributes(weaponId), "mess");

        /// <summary>
        /// A raw attribute as a float. <b>Absent reads 0, not NaN</b> — the Oracle's own arithmetic on an
        /// absent attribute coerces the empty list to <c>""</c>, and <c>"" * 0.4</c> is 0.
        /// </summary>
        private static float AttrF(string attrs, string name, float fallback = 0f)
            => string.IsNullOrEmpty(attrs)
                ? fallback
                : PFE.Systems.Weapons.WeaponXmlAttrs.AttrF(attrs, name, fallback);

        // ── IAmmoSource ───────────────────────────────────────────────────────

        /// <summary>Read-only and local: no authority is consulted to count what is held.</summary>
        public int GetAmmoCount(string ammoType)
            => _inventory != null ? _inventory.GetAmmoCount(ammoType) : 0;

        /// <summary>
        /// Draw up to <paramref name="amount"/> rounds, through the command seam, and report how many
        /// were actually drawn. A weapon asking for 12 with 5 held gets 5 — which is why the sink
        /// returns the count and this method forwards it rather than echoing the request.
        /// </summary>
        public int ConsumeAmmo(string ammoType, int amount)
        {
            if (amount <= 0) return 0;

            InventoryCommandResult result = Submit(InventoryCommand.ConsumeAmmo(ammoType, amount));
            return result.Applied ? result.Amount : 0;
        }

        // ── IItemSkillSource ──────────────────────────────────────────────────
        // The CharacterStats side of AS3 Pers.setInvParameters (Pers.as:2290-2307): the stat rows a
        // holder gains by CARRYING an item. Consumed only by CharacterStats.RecalculateStats step 5.5;
        // the pickup→recalc trigger is PlayerController's event subscription, not this read.

        /// <summary>
        /// Parsed <c>&lt;sk&gt;</c> rows per item id, cached. The definitions are static content, so
        /// a parse result never goes stale; an item with no row is cached as null so the resolver is
        /// not re-run on every recalc for the 468 rows that have nothing to grant.
        /// </summary>
        private readonly Dictionary<string, PersSkillRow[]> _skillRowCache = new Dictionary<string, PersSkillRow[]>();

        public IEnumerable<KeyValuePair<string, PersSkillRow[]>> EnumerateHeldSkillRows()
        {
            if (_inventory == null) yield break;

            foreach (KeyValuePair<string, GameItemInstance> held in _inventory.Items)
            {
                // AS3's gate is `kol > 0` (Pers.as:2294) — a spent stack grants nothing. Reading the
                // live Quantity rather than the dictionary key is the whole point: the row can exist
                // at Quantity 0 after a partial consume.
                if (held.Value == null || held.Value.Quantity <= 0) continue;

                PersSkillRow[] rows = ResolveSkillRows(held.Key);
                if (rows != null && rows.Length > 0)
                    yield return new KeyValuePair<string, PersSkillRow[]>(held.Key, rows);
            }
        }

        /// <summary>
        /// The parsed rows for one item id, or null when the item grants nothing. Cached.
        ///
        /// <para><b>Two sources, in order, because the first one can legitimately be empty.</b>
        /// <see cref="ItemDefinition.skillRowsSource"/> is the importer's stored run and is the fast
        /// path. But it only exists on assets written <b>after</b> that field landed — measured
        /// 2026-10-18: <b>0 of 500</b> item assets carry it, so on the shipped asset set the fast path
        /// is empty for every item and the lockpick stays a dead keychain. The fallback reads the
        /// same <c>&lt;item&gt;</c> body out of the runtime mirror of <c>AllData.as</c>
        /// (<c>Resources/Loot/AllData</c>, the text <c>LootRuntime</c> already loads and
        /// <see cref="TryGetItemRow"/> already indexes), parsed by the same
        /// <see cref="ItemXmlRows.ParseSkillRows"/>. Verified in sync with the oracle file: both
        /// carry 508 <c>&lt;sk</c> rows and the screwdriver's <c>possLockPick</c> row.</para>
        ///
        /// <para><b>A miss is not cached while the registry is still absent.</b> Caching null when
        /// <see cref="_itemResolver"/> has not been bound yet would poison this id for the rest of the
        /// session — the resolver is a DI seam and the first recalc can precede it.</para>
        /// </summary>
        private PersSkillRow[] ResolveSkillRows(string itemId)
        {
            if (_skillRowCache.TryGetValue(itemId, out PersSkillRow[] cached)) return cached;

            PersSkillRow[] rows = null;

            ItemDefinition definition = _itemResolver?.Invoke(itemId);
            if (definition != null && !string.IsNullOrEmpty(definition.skillRowsSource))
                rows = ItemXmlRows.ParseSkillRows(definition.skillRowsSource).ToArray();

            // The asset set predates `skillRowsSource` (see the class comment): read the row's body
            // from the mirror instead. An item with no `<sk>` children yields an empty body here and
            // stays a null result, which is the honest "grants nothing".
            if (rows == null || rows.Length == 0)
            {
                if (TryGetItemRow(itemId, out PFE.Systems.Items.ItemXmlRow row) &&
                    !string.IsNullOrEmpty(row.Body))
                {
                    List<PersSkillRow> parsed = ItemXmlRows.ParseSkillRows(row.Body);
                    if (parsed.Count > 0) rows = parsed.ToArray();
                }
            }

            if (rows == null && _itemResolver == null) return null;

            _skillRowCache[itemId] = rows;
            return rows;
        }
    }
}
