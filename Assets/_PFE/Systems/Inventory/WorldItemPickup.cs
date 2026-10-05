using System.Collections.Generic;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Interaction;
using PFE.Systems.Map.Rendering;

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
    public sealed class WorldItemPickup : MonoBehaviour
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

        /// <summary>The AS3 item id this pickup holds.</summary>
        public string ItemId { get; private set; }

        /// <summary>How many are lying here.</summary>
        public int Quantity { get; private set; }

        /// <summary>The definition, for the icon, the display name and the pickup sound.</summary>
        public ItemDefinition Definition { get; private set; }

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
                string name = !string.IsNullOrEmpty(Definition?.displayName)
                    ? Definition.displayName
                    : ItemId;
                return Quantity > 1 ? $"Take {Quantity} × {name}" : $"Take {name}";
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

            InventoryCommandResult result = inventory.Submit(InventoryCommand.AddItem(ItemId, Quantity));
            if (!result.Applied)
            {
                Debug.LogWarning($"[WorldItemPickup] Could not take '{ItemId}' ×{Quantity}: {result.Reason}");
                return;
            }

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
        /// <para><b>The caller must already have removed the items from the inventory.</b> This mirrors
        /// AS3's order — <c>Invent.drop()</c> builds the <c>Item</c> and the <c>Loot</c> first and calls
        /// <c>minusItem</c> last (<c>:1544-1548</c>) — but the port splits the two so the inventory half
        /// stays inside the command seam. <c>PlayerInventory.DropItem</c> is the one caller and it
        /// destroys the pickup again if the removal is rejected, so the two halves cannot drift into
        /// "spawned but not removed" (a duplication bug) or "removed but not spawned" (a loss).</para>
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

            var go = new GameObject($"Loot_{definition.itemId}_{quantity}");
            go.transform.position = position;

            var pickup = go.AddComponent<WorldItemPickup>();
            pickup.ItemId = definition.itemId;
            pickup.Quantity = quantity;
            pickup.Definition = definition;
            pickup.AutoCollect = autoCollect;

            // A trigger, so loot never blocks movement. AS3's Loot is a world object with no collision
            // against the unit either — it is taken, not bumped into.
            CircleCollider2D collider = go.AddComponent<CircleCollider2D>();
            collider.radius = PickupRadius;
            collider.isTrigger = true;

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = definition.icon != null ? definition.icon : MakeFallbackSprite();
            renderer.color = definition.icon != null ? Color.white : definition.fallbackColor;

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
