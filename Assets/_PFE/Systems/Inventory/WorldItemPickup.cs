using System.Collections.Generic;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Interaction;
using PFE.Systems.Map.Rendering;
using PFE.UI.Common;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// An item lying in the world, waiting to be picked up — the port's counterpart to AS3
    /// <c>fe/loc/Loot.as</c>.
    ///
    /// <para><b>Why a world object and not a debug list.</b> AS3 spawns a real <c>Loot</c> object into the
    /// location (<c>Invent.drop():1546</c>) and the player takes it through the normal interaction path
    /// (<c>UnitPlayer.actAction</c> → <c>loc.celObj</c>). Modelling a drop as anything less would prove
    /// nothing about collection, and enemies dropping loot is the whole point of the feature.</para>
    ///
    /// <para><b>It implements <see cref="IInteractable"/>, so it is found by the existing path and
    /// nothing else.</b> <c>PlayerController.FindCursorTarget</c> probes
    /// <c>Physics2D.OverlapCircleAll</c> at the cursor and asks each hit for
    /// <see cref="IInteractable"/> — so this object needs a collider and the component on the same
    /// GameObject, and it deliberately does <i>not</i> add a second discovery mechanism. The reach check
    /// (<c>WorldConstants.ACTION_REACH</c>) is applied by the player, not here.</para>
    ///
    /// <para><b>Collection goes through the inventory command seam</b>, not into the bag directly, so
    /// picking a dropped item up travels the same validate → resolve → apply path a pickup from a
    /// container will. If the seam rejects it, the object <b>stays in the world</b> — an item is never
    /// destroyed by a failed take.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldItemPickup : MonoBehaviour, IInteractable
    {
        /// <summary>
        /// Every live pickup. Debug-facing only: AS3 keeps these in the location's object list
        /// (<c>loc.objs</c>), which the port has no equivalent of yet, and the F2 drop tab needs to be
        /// able to show and clear what is lying around. Nothing in the collection path reads it.
        /// </summary>
        private static readonly List<WorldItemPickup> Live = new List<WorldItemPickup>();

        /// <summary>Every live pickup, newest last. Debug/overlay use.</summary>
        public static IReadOnlyList<WorldItemPickup> All => Live;

        /// <summary>
        /// AS3 <c>Loot.osnRad</c> = 50 source px (<c>Loot.as:15</c>), used as the half-extent of the take
        /// zone — <c>Loot.as:314</c> compares <i>each axis</i> against it, so the oracle's zone is a
        /// 1.0-unit square. Art imports at 100 px per unit (the same scale
        /// <see cref="PFE.Systems.Map.WorldConstants.ACTION_REACH"/> uses), so the half-extent is 0.5
        /// world units. This
        /// collider is a circle rather than that square: the port takes loot with the cursor, so all it
        /// needs is for the probe in <c>PlayerController.FindCursorTarget</c> to hit it.
        /// </summary>
        private const float PickupRadiusPixels = 50f;

        /// <summary>Source pixels per world unit — must match <see cref="PFE.Systems.Map.WorldConstants.ACTION_REACH"/>'s scale.</summary>
        private const float PixelsToUnits = 0.01f;

        /// <summary>Take-zone half-extent in world units (0.5).</summary>
        private const float PickupRadius = PickupRadiusPixels * PixelsToUnits;

        /// <summary>World units the icon is drawn across — a little under one tile.</summary>
        private const float IconSize = 0.36f;

        /// <summary>
        /// The colour of the square a weapon with no visual falls back to (34 of the 213 weapon assets
        /// reference no <see cref="WeaponVisualDefinition"/>, and <see cref="WeaponDefinition"/> carries no
        /// <c>fallbackColor</c> of its own — the item row does, the weapon row does not).
        ///
        /// <para><b>A deliberate placeholder, and it is not a colour the Oracle has.</b> AS3's <c>Loot</c>
        /// always had a symbol to draw, so there is nothing to copy here. The value is chosen only so an
        /// icon-less weapon is distinguishable from an icon-less item on screen during a play-test —
        /// <see cref="ItemDefinition.fallbackColor"/> defaults to white, and two identical white squares
        /// would make a dropped weapon look like a dropped pebble. If a weapon ever needs a real fallback
        /// sprite, this constant is the thing to delete.</para>
        /// </summary>
        private static readonly Color WeaponFallbackColor = new Color(0.85f, 0.70f, 0.35f);

        /// <summary>The AS3 item id this pickup holds.</summary>
        public string ItemId { get; private set; }

        /// <summary>How many are lying here.</summary>
        public int Quantity { get; private set; }

        /// <summary>
        /// The item row, for the icon, the display name and the pickup sound — <b>or <c>null</c> when this
        /// pickup holds a weapon</b> (see <see cref="WeaponDefinition"/>).
        /// </summary>
        public ItemDefinition Definition { get; private set; }

        /// <summary>
        /// The weapon row, or <c>null</c> when this pickup holds an item.
        ///
        /// <para><b>Why this had to exist.</b> The port's weapon ids live in <c>ContentType.Weapon</c> and
        /// resolve to a <see cref="WeaponDefinition"/>; the item table holds no weapon id at all. Before
        /// this field, a rolled weapon had nowhere to go: <c>Spawn</c> took an <c>ItemDefinition</c>, the
        /// resolver returned <c>null</c> for the id, and the container logged
        /// <i>"no ItemDefinition resolves it — it was not spawned"</i> and dropped the roll. That is the
        /// exact line in the play-test log this closes.</para>
        ///
        /// <para>At most one of the two is set, and <see cref="ItemId"/> is the accessor that does not care
        /// which.</para>
        /// </summary>
        public WeaponDefinition WeaponDefinition { get; private set; }

        /// <summary>
        /// Whether walking over this pickup collects it — AS3 <c>Loot.auto</c> (<c>Loot.as:33</c>,
        /// assigned from the constructor's <c>nauto</c> at <c>:238</c>).
        ///
        /// <para><b>The flag is not cosmetic: it is the whole gate.</b> The oracle's per-frame auto-take is
        /// <c>if (this.auto &amp;&amp; this.auto2 || this.actTake) this.take()</c> (<c>Loot.as:396-399</c>),
        /// so a pickup with <c>auto == false</c> is only ever taken through the cursor
        /// (<c>actTake</c>), however close the player walks. Two of the oracle's three construction sites
        /// pass <c>false</c>:</para>
        /// <list type="bullet">
        ///   <item><description><c>Invent.drop()</c> — a player's own drop
        ///     (<c>Invent.as:1550</c>): <c>false</c>. Dropping something and having it fly straight back
        ///     would be absurd, so this matters.</description></item>
        ///   <item><description><c>Location.createSur()</c> — an item placed by the room
        ///     (<c>Location.as:1325</c>): <c>false</c>.</description></item>
        ///   <item><description><c>LootGen</c> — an enemy's drop (<c>LootGen.as:293</c>): the parameter is
        ///     omitted, and the constructor default is <c>true</c>.</description></item>
        /// </list>
        /// <para>The default here is <c>false</c> for the same reason: a caller that has not thought about
        /// it should get the conservative behaviour, not the magnetic one.</para>
        /// </summary>
        public bool AutoCollect { get; private set; }

        /// <summary>Where it landed, for the overlay's list.</summary>
        public Vector2 Position => transform.position;

        // ── IInteractable ────────────────────────────────────────────────────

        /// <summary>
        /// AS3's <c>Loot</c> prompt is the item name; the count is appended only when it is more than one,
        /// so the common case reads "Take stimpak" rather than "Take 1 × stimpak".
        /// </summary>
        public string ActionText
        {
            get
            {
                string name = DisplayName;
                return Quantity > 1 ? $"Take {Quantity} × {name}" : $"Take {name}";
            }
        }

        /// <summary>
        /// What to call this thing on screen — the port's counterpart to AS3 <c>Item.nazv</c>, which
        /// <c>Item.as:206-218</c> fills from <c>Res.txt("w", id)</c> for a weapon and <c>Res.txt("i", id)</c>
        /// for an item.
        ///
        /// <para><b>A weapon's name comes from the localisation document, not from the asset.</b> All 213
        /// shipped <c>WeaponDefinition</c> assets predate the <c>displayName</c> field and so load it as
        /// <c>""</c>; the names themselves already ship in
        /// <c>Resources/Localisation/text_en.xml</c> as <c>&lt;weapon id="r32"&gt;&lt;n&gt;.32 pistol&lt;/n&gt;</c>,
        /// and <c>PfeTextTableProvider.WeaponNameOrId</c> reads them. A pickup labelled with a bare
        /// <c>r32</c> would be a worse prompt than one labelled ".32 pistol", and inventing a name table
        /// here would duplicate the one the Oracle already ships.</para>
        ///
        /// <para>The <c>displayName</c> field still wins when it is set, so an importer that later freezes
        /// the names into the assets takes precedence without touching this method.</para>
        /// </summary>
        private string DisplayName
        {
            get
            {
                if (WeaponDefinition != null)
                {
                    return !string.IsNullOrEmpty(WeaponDefinition.displayName)
                        ? WeaponDefinition.displayName
                        : PfeTextTableProvider.WeaponNameOrId(WeaponDefinition.weaponId);
                }

                return !string.IsNullOrEmpty(Definition?.displayName) ? Definition.displayName : ItemId;
            }
        }

        /// <summary>
        /// Always takeable when there is a player to take it. Deliberately not gated on inventory room:
        /// the inventory has no weight refusal on the add path (AS3's limit is a soft <c>maxm</c> cap,
        /// <c>Item.as:460</c>), and a pickup that silently refuses to be picked up is worse than one
        /// that is picked up and over-encumbers.
        /// </summary>
        public bool CanInteract(GameObject user) => user != null;

        /// <summary>
        /// Move this stack into the interacting player's inventory. The object is destroyed only if the
        /// seam accepted the add; a rejection leaves it lying there and logs the reason.
        /// </summary>
        public void Interact(GameObject user)
        {
            PlayerInventory inventory = user != null ? user.GetComponent<PlayerInventory>() : null;
            if (inventory == null)
            {
                Debug.LogWarning($"[WorldItemPickup] '{ItemId}' cannot be taken: the interactor has no " +
                                 "PlayerInventory, so there is nowhere to put it.");
                return;
            }

            // Two commands, because the port's weapons and items are different content types behind
            // different tables — the same line AS3 draws between `Invent.take()` (which indexes
            // `AllData.d.item`) and `Invent.addWeapon()` (which indexes `AllData.d.weapon`). Sending a
            // weapon id through AddItem is rejected with "no item row" 100 % of the time, for every
            // weapon, which is precisely why weapons never reached the inventory.
            InventoryCommandResult result = WeaponDefinition != null
                ? inventory.Submit(InventoryCommand.AddWeapon(WeaponDefinition.weaponId))
                : inventory.Submit(InventoryCommand.AddItem(ItemId, Quantity));

            if (!result.Applied)
            {
                Debug.LogWarning($"[WorldItemPickup] Could not take '{ItemId}' ×{Quantity}: {result.Reason}");
                return;
            }

            // Item rows carry a take sound; weapon rows do not. AS3's `Loot` has no take sound either —
            // only `sndFall` (Loot.as:45), which this path cannot use because the pickup is spawned
            // already at rest — so a weapon is collected silently rather than given an invented clip.
            if (Definition != null && Definition.pickupSound != null)
            {
                // AudioSource is added on demand: a pickup is spawned by code, not authored in a prefab,
                // so there is nowhere to hang one in the editor.
                AudioSource source = GetComponent<AudioSource>();
                if (source == null) source = gameObject.AddComponent<AudioSource>();
                source.PlayOneShot(Definition.pickupSound);
                // Detach the sound from the object we are about to destroy.
                source.transform.SetParent(null);
                Destroy(source.gameObject, Definition.pickupSound.length + 0.1f);
            }

            Debug.Log($"[WorldItemPickup] Took {Quantity} × '{ItemId}' at " +
                      $"{transform.position.x:0.##},{transform.position.y:0.##}.");
            Despawn();
        }

        // ── Spawning ─────────────────────────────────────────────────────────

        /// <summary>
        /// Put <paramref name="quantity"/> of <paramref name="definition"/> into the world at
        /// <paramref name="position"/> and return the pickup, or <c>null</c> if it could not be made.
        ///
        /// <para><b>Only the drop caller has an inventory half, and it must have removed the items
        /// first.</b> This mirrors AS3's order — <c>Invent.drop()</c> builds the <c>Item</c> and the
        /// <c>Loot</c> first and calls <c>minusItem</c> last (<c>:1544-1548</c>) — but the port splits the
        /// two so the inventory half stays inside the command seam. <c>PlayerInventory.DropItem</c>
        /// destroys the pickup again if the removal is rejected, so the two halves cannot drift into
        /// "spawned but not removed" (a duplication bug) or "removed but not spawned" (a loss). The other
        /// three callers — the F2 overlay's <c>SpawnInWorld</c>, <c>ContainerPropPresenter.SpillRolls</c>
        /// and <c>UnitController.SpillDrops</c> — are <i>sources</i> rather than moves: a rolled or
        /// conjured pickup was never in the bag, so there is no removal to pair with it.</para>
        ///
        /// <para><b>No landing sound.</b> AS3's <c>Loot.sndFall</c> (default <c>"fall_item"</c>,
        /// <c>Loot.as:45</c>) plays as the object falls and lands (<c>Loot.as:481</c>). This spawns the
        /// pickup already at rest, so there is no fall to hear, and the port has no resolver from a
        /// <c>snd.@fall</c> id to an <c>AudioClip</c> — inventing one would be a guess. The take sound is
        /// the item's own <c>pickupSound</c> and is played in <see cref="Interact"/>.</para>
        ///
        /// <para><paramref name="autoCollect"/> defaults to <c>false</c> deliberately — see
        /// <see cref="AutoCollect"/> for which of the oracle's three call sites pass which value. A
        /// caller that has not considered walk-over collection gets the conservative behaviour.</para>
        /// </summary>
        public static WorldItemPickup Spawn(ItemDefinition definition, int quantity, Vector3 position,
                                            bool autoCollect = false)
        {
            if (definition == null)
            {
                Debug.LogWarning("[WorldItemPickup] Refusing to spawn a pickup with no definition.");
                return null;
            }
            if (quantity <= 0)
            {
                Debug.LogWarning($"[WorldItemPickup] Refusing to spawn '{definition.itemId}' with quantity {quantity}.");
                return null;
            }

            return SpawnCore(definition.itemId, quantity, position, autoCollect,
                             icon: definition.icon, fallbackColor: definition.fallbackColor,
                             itemDefinition: definition, weaponDefinition: null);
        }

        /// <summary>
        /// The weapon half — same world object, same take path, but carrying a <see cref="WeaponDefinition"/>
        /// so the roll is not thrown away.
        ///
        /// <para><b>Why a separate overload and not a widened parameter.</b> The two content types share
        /// nothing but "an id and a picture": a weapon has no <c>pickupSound</c>, no <c>fallbackColor</c>,
        /// no <c>weight</c> and no <c>icon</c>, and an item has no <c>maxDurability</c> or <c>ammoType</c>.
        /// A single <c>object</c>-typed parameter would move every one of those differences to a cast at
        /// the point of use; two overloads keep each caller's type checked, and
        /// <see cref="SpawnCore"/> holds the part that really is common.</para>
        ///
        /// <para><b>The icon is the weapon's own held sprite</b> —
        /// <see cref="WeaponVisualDefinition.IdleSprite"/>, the frame a unit draws with the weapon out. That
        /// is the port's counterpart to AS3's <c>Loot</c> drawing the symbol named by <c>vis.@vweap</c>, and
        /// it is real data rather than a placeholder: 179 of the 213 shipped weapon assets reference a
        /// visual and all 174 visuals carry frames (<c>visr32</c> has 40, <c>idleFrame 0</c>). The 34
        /// without one fall back to the shared square — see <see cref="WeaponFallbackColor"/>.</para>
        /// </summary>
        public static WorldItemPickup Spawn(WeaponDefinition weaponDefinition, int quantity, Vector3 position,
                                            bool autoCollect = false)
        {
            if (weaponDefinition == null)
            {
                Debug.LogWarning("[WorldItemPickup] Refusing to spawn a weapon pickup with no definition.");
                return null;
            }
            if (quantity <= 0)
            {
                Debug.LogWarning($"[WorldItemPickup] Refusing to spawn '{weaponDefinition.weaponId}' with quantity {quantity}.");
                return null;
            }

            Sprite icon = weaponDefinition.weaponVisual != null
                ? weaponDefinition.weaponVisual.IdleSprite
                : null;

            return SpawnCore(weaponDefinition.weaponId, quantity, position, autoCollect,
                             icon: icon, fallbackColor: WeaponFallbackColor,
                             itemDefinition: null, weaponDefinition: weaponDefinition);
        }

        /// <summary>
        /// The part of spawning that is the same for both content types: the GameObject, the trigger
        /// collider, the renderer, the sorting and the debug list.
        /// </summary>
        private static WorldItemPickup SpawnCore(string id, int quantity, Vector3 position, bool autoCollect,
                                                 Sprite icon, Color fallbackColor,
                                                 ItemDefinition itemDefinition,
                                                 WeaponDefinition weaponDefinition)
        {
            var go = new GameObject($"Loot_{id}_{quantity}");
            go.transform.position = position;

            var pickup = go.AddComponent<WorldItemPickup>();
            pickup.ItemId = id;
            pickup.Quantity = quantity;
            pickup.Definition = itemDefinition;
            pickup.WeaponDefinition = weaponDefinition;
            pickup.AutoCollect = autoCollect;

            // A trigger, so loot never blocks movement. AS3's Loot is a world object with no collision
            // against the unit either — it is taken, not bumped into.
            CircleCollider2D collider = go.AddComponent<CircleCollider2D>();
            collider.radius = PickupRadius;
            collider.isTrigger = true;

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = icon != null ? icon : MakeFallbackSprite();
            renderer.color = icon != null ? Color.white : fallbackColor;

            // A sorting LAYER, not just an order — and the omission was a real, invisible-in-play bug.
            // `sortingOrder` is only compared WITHIN a sorting layer, and a sorting layer outranks the
            // order, so setting the order alone left this on `Default` — which is the BACKMOST layer in
            // this project's scheme (TagManager order: Default, Backwall, BackgroundTiles, BackgroundDecor,
            // MainTiles, BackgroundObject, BackgroundPhysicalObjects, Entities, Weapons, Water,
            // Foreground). The loot was therefore drawn behind the backwall and the floor and could not be
            // seen at all, however large its order. Reported from play as "loot has no sprite and is
            // probably on the wrong layer" — the sprite was fine, the layer was wrong.
            //
            // `BackgroundPhysicalObjects` is where this project already puts in-world entities
            // (`RoomUnitSpawner` places every unit there), and it sits in front of `MainTiles` (the floor)
            // and of the props. Units get a *negative* depth order there
            // (`-floor(y / TILE_SIZE)`), so order 500 keeps the loot above the floor and above any unit
            // standing on it — which is the original intent ("never hidden by scenery") and matters
            // because the pickup has to be visible to be collected. It stays behind `Foreground`, which is
            // scenery deliberately drawn over the player.
            //
            // Note the order is deliberately NOT depth-sorted like units: a pickup that can vanish behind
            // the player at some Y would be worse for a collection test than one that always draws on top.
            renderer.sortingLayerName = MapSortingLayers.BackgroundPhysicalObjects;
            renderer.sortingOrder = 500;

            float size = IconSize;
            if (renderer.sprite != null)
            {
                // Normalise by the sprite's own size so a 40px icon and a 100px icon draw the same.
                Vector2 spriteSize = renderer.sprite.bounds.size;
                float longest = Mathf.Max(spriteSize.x, spriteSize.y);
                if (longest > 0f) size = IconSize / longest;
            }
            go.transform.localScale = new Vector3(size, size, 1f);

            Live.Add(pickup);
            return pickup;
        }

        /// <summary>Remove this pickup from the world and from the debug list.</summary>
        public void Despawn()
        {
            Live.Remove(this);
            if (this != null) Destroy(gameObject);
        }

        /// <summary>
        /// One tick of AS3's loot magnet — close one fifth of the remaining gap to
        /// <paramref name="target"/> (<c>Loot.as:318-320</c>: <c>dx = _loc2_ / 5</c>, then <c>run()</c>
        /// applies it as this tick's displacement).
        ///
        /// <para><b>Why the magnet is not a nicety.</b> The take band is measured from the player's body
        /// <i>centre</i>, and the player is 70 px tall, so a pickup resting on the ground sits 35 px below
        /// that centre — outside the ±20 px take band. Without the magnet, grounded loot could never be
        /// collected by walking over it at all; the magnet is what lifts it into range. See
        /// <see cref="AutoPickupRule.BodyCentreY"/>.</para>
        ///
        /// <para>The z is preserved rather than zeroed: the caller works in 2D and hands in a
        /// <see cref="Vector2"/>, so rebuilding the vector from scratch would silently move the pickup off
        /// whatever plane it was placed on.</para>
        /// </summary>
        public void MagnetToward(Vector2 target)
        {
            Vector2 current = transform.position;
            Vector2 step = new Vector2(
                AutoPickupRule.MagnetStep(target.x - current.x),
                AutoPickupRule.MagnetStep(target.y - current.y));

            transform.position = new Vector3(
                current.x + step.x, current.y + step.y, transform.position.z);
        }

        /// <summary>Destroy every live pickup. Debug affordance for the F2 drop tab.</summary>
        public static int DespawnAll()
        {
            // Snapshot first: Despawn() mutates Live.
            var copy = Live.ToArray();
            for (int i = 0; i < copy.Length; i++)
            {
                if (copy[i] != null) copy[i].Despawn();
            }
            Live.Clear();
            return copy.Length;
        }

        private void OnDestroy()
        {
            Live.Remove(this);
        }

        /// <summary>
        /// A 1×1 white sprite, for items with no icon. Built once and shared — creating one per pickup
        /// would leak a texture for every drop.
        /// </summary>
        private static Sprite _fallbackSprite;

        private static Sprite MakeFallbackSprite()
        {
            if (_fallbackSprite != null) return _fallbackSprite;

            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;

            _fallbackSprite = Sprite.Create(
                texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            _fallbackSprite.hideFlags = HideFlags.HideAndDontSave;
            return _fallbackSprite;
        }
    }
}
