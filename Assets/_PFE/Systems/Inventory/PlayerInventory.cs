using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Data;
using PFE.Data.Definitions;
using PFE.ModAPI;          // ContentType lives here, not in PFE.Data (ContentRegistry implements IContentRegistry)
using PFE.Systems.Magic;
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
    public sealed class PlayerInventory : MonoBehaviour, IAmmoSource
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
            // constant.
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
    }
}
