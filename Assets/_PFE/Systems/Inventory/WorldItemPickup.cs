using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Audio;
using PFE.Systems.Campaign;
using PFE.Systems.Interaction;
using PFE.Systems.Loot;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Telekinesis;
using PFE.UI.Common;
using PFE.UI.HUD;
using PFE.UI.PipBuck;

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
        /// AS3 <c>Item.variant</c> for a weapon pickup — <c>0</c> for the plain weapon, <c>1</c> for the
        /// "II" tier. Always <c>0</c> for an item pickup.
        ///
        /// <para><b>Why the world object has to carry it.</b> The roller splits <c>id^N</c> into
        /// <c>ItemId</c> + <c>Variant</c> (<c>Item.as:132-136</c>) and the drop spawns from
        /// <see cref="WeaponDefinition"/>, which is keyed by the base id. Without this field the tier
        /// dies at the spawn — the exact shape of the bug that made every rolled unique a base weapon.
        /// It travels: this → <see cref="Interact"/> → <c>InventoryCommand.Variant</c> →
        /// <c>GameWeaponInstance.variant</c> → the PipBuck row's name and the trader's ×3 price.</para>
        /// </summary>
        public int Variant { get; private set; }

        /// <summary>
        /// The condition a weapon arrives in, as a fraction of its row's <c>maxDurability</c> —
        /// AS3 <c>Item.sost * Item.multHP</c>, which <c>Invent.take</c> multiplies by
        /// <c>char[variant].@maxhp</c> to get the weapon's hp (<c>Invent.as:1068-1075</c>).
        ///
        /// <para><b><c>1</c> means "undamaged", and it is the default.</b> Only a rolled drop knows
        /// better: an enemy's own gun arrives at <c>0.05..0.20</c> because the drop passes
        /// <c>kol = 0</c> (<c>Item.as:185-188</c>), and that number is the whole reason a corpse's
        /// weapon is worth less than a shop's. Before this field the condition was computed by the
        /// roller and discarded here, so every dropped weapon arrived at full durability.</para>
        /// </summary>
        public float Condition { get; private set; } = 1f;

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

        /// <summary>
        /// The room-owned record behind this view, or <c>null</c> when the pickup was spawned without a
        /// room (the debug overlay's "drop this here", and every bare test spawn).
        /// </summary>
        /// <remarks>
        /// <para><b>Ownership direction.</b> The record lives on <see cref="RoomInstance.droppedItems"/>
        /// and knows nothing about this component; the component points at the record. That is what lets
        /// <see cref="RoomVisualController"/> destroy every view in a room and rebuild them from data —
        /// the same contract props and units already have, where <c>ObjectInstance</c>/<c>UnitInstance</c>
        /// are the record and the presenter is the projection.</para>
        ///
        /// <para><b>A taken item removes its record; a destroyed view does not.</b> Those are the two
        /// different meanings of "this pickup is going away", and conflating them is how loot would
        /// either duplicate (view destroyed, record kept, rebuilt at the old spot on re-entry after the
        /// player took it) or vanish (record dropped because the room streamed out).</para>
        /// </remarks>
        public DroppedItemInstance Record { get; private set; }

        /// <summary>
        /// Id → item row, published once by <c>PlayerInventory.Construct</c> — the same resolver that
        /// component uses, exposed statically because <see cref="RestoreForRoom"/> runs at room-build
        /// time, when there is no player GameObject to ask.
        /// </summary>
        /// <remarks>
        /// <b>Null is legal and means "cannot rebuild".</b> A rig with no content registry keeps working
        /// exactly as before: the record stays on the room and nothing is drawn for it. That is the same
        /// conservative answer <see cref="RoomProvider"/> gives, and it is why this is a seam rather than
        /// a lookup into a singleton — the pickup has no injection point (<c>new GameObject</c>).
        /// </remarks>
        public static Func<string, ItemDefinition> ItemDefinitionResolver { get; set; }

        /// <inheritdoc cref="ItemDefinitionResolver"/>
        /// <remarks>
        /// A separate seam for the same reason the two tables are separate: <c>ContentType.Weapon</c> and
        /// <c>ContentType.Item</c> are disjoint, so one resolver can never answer for both. See
        /// <c>PlayerInventory.ResolveWeapon</c>.
        /// </remarks>
        public static Func<string, WeaponDefinition> WeaponDefinitionResolver { get; set; }

        // ── Falling (AS3 Loot.step / Loot.run) ───────────────────────────────

        /// <summary>
        /// AS3's frame rate. <c>Loot.step</c> runs once per 30 Hz frame and its whole arithmetic is
        /// per-frame (1 px/frame², 20 px/frame terminal), so the accumulator below must feed it ticks of
        /// exactly this length. Feeding it <c>Time.deltaTime</c> would make a 60 fps machine fall at
        /// twice the oracle's speed and a 144 fps one at 4.8×.
        /// </summary>
        private const float TickSeconds = 1f / 30f;

        /// <summary>
        /// A room to resolve against when the caller did not hand one in. Set once by the player; the
        /// loot tables use the same idiom (<c>LootRuntime</c>) for the same reason — a pickup is created
        /// with <c>new GameObject</c> and has no injection point.
        ///
        /// <para><b>Null means "do not fall".</b> A pickup with no room has no tile grid to land on, so it
        /// stays exactly where it was spawned — the pre-existing behaviour. That keeps the debug overlay's
        /// "drop this here" honest in a scene with no land loaded.</para>
        /// </summary>
        public static Func<RoomInstance> RoomProvider { get; set; }

        /// <summary>Room-local pixels → world units. Same scale as <see cref="PixelsToUnits"/>.</summary>
        private RoomInstance _room;

        /// <summary>Horizontal speed, px/frame, positive right — AS3 <c>Loot.dx</c>.</summary>
        private float _vx;

        /// <summary>Vertical speed, px/frame, positive <b>up</b> — AS3 <c>Loot.dy</c> mirrored.</summary>
        private float _vy;

        /// <summary>True while the pickup is still in the air. Cleared by landing, by the magnet, or by
        /// running out of room to fall into.</summary>
        private bool _falling;

        /// <summary>
        /// AS3 <c>Loot.vsos</c> — the magnet is towing this pickup. It suspends gravity
        /// (<c>Loot.as:352</c> tests <c>!this.vsos</c>), because the magnet owns the position instead.
        /// </summary>
        private bool _magnetized;

        // ── Telekinesis (AS3 `Loot.levit`) ────────────────────────────────────────────────────────
        //
        // Loot is a first-class telekinesis target in the oracle and was not one in the port: AS3's
        // `Loot extends Obj` (`Loot.as:10`), `Obj.levitPoss` defaults true (`Obj.as:32`), the `Loot`
        // constructor sets it explicitly (`:245`), and `UnitPlayer.actTele` walks `loc.firstObj` — one
        // list containing boxes, loot and units alike — so a cogwheel was as liftable as a crate. The
        // port's telekinesis targets `ObjectInstance` in `RoomObjectPhysicsLayer`, and a dropped item is
        // not one of those, which is the whole reason only crates and containers could be lifted.
        //
        // The two halves are the oracle's: the CASTER writes the target (and, in AS3, the object's
        // dx/dy); the OBJECT integrates it in its own step (`Loot.step` → `run()`). Keeping that split
        // here means a levitated pickup collides through exactly the same `LootFallStepRules` the falling
        // one does, instead of through a second copy of the resolver.

        /// <summary>
        /// AS3 <c>Loot.massa = 0.1</c> (<c>Loot.as:81</c>) — in AS3's own scale, which is the scale
        /// <c>maxTeleMassa</c> is expressed in. Well under the rank-0 cap of <c>0.6</c>, so every pickup
        /// is liftable from the first rank; and at or below the rank-0 <c>telePorog</c> of <c>0.1</c>, so
        /// holding one costs no mana (<c>UnitPlayer.as:1807-1813</c>: the drain is charged only when
        /// <c>massa &gt; telePorog</c>).
        /// </summary>
        public const float As3Massa = 0.1f;

        /// <summary>True while the player's telekinesis is carrying this pickup — AS3 <c>Loot.levit</c>.</summary>
        private bool _levitating;

        /// <summary>
        /// Where the holder wants this pickup's <b>centre</b>, in <b>room-local pixels</b> — the target the
        /// levitation step accelerates toward. AS3 compares <c>teleObj.Y - teleObj.scY / 2</c>
        /// (<c>UnitPlayer.as:1255-1262</c>), i.e. the vertical middle, not the bottom the oracle calls
        /// <c>Y</c>.
        /// </summary>
        private Vector2 _levTargetRoomLocalPixels;

        /// <summary>
        /// AS3 <c>Obj.levitPoss</c> (<c>Obj.as:32</c>) — can this pickup be lifted <b>right now</b>.
        ///
        /// <para>It starts true (<c>Loot.as:245</c>) and is cleared by the two paths the oracle clears it
        /// on: the take window grabbing the item (<c>Loot.as:304</c>, <c>:316</c>) and a refused take
        /// (<c>:299</c> restores it). The player's hold tick drops a held object the moment this turns
        /// false (<c>UnitPlayer.as:1274</c>), which is why a magnetised pickup cannot stay in the air.</para>
        /// </summary>
        private bool _levitPoss = true;

        /// <summary>
        /// The loot's drawn height in <b>px</b> — AS3 <c>scY</c>, measured off the sprite so the bottom
        /// edge is what rests on the floor rather than an invented box.
        /// </summary>
        private float _heightPx = LootFallRules.DefaultHeightPx;

        /// <summary>
        /// Distance in <b>px</b> from this transform to the sprite's <b>bottom edge</b>.
        ///
        /// <para><b>Not <c>height / 2</c>, because a pivot is not always centred.</b> The box the oracle
        /// calls <c>Y</c> is the loot's bottom, and a <c>SpriteRenderer</c> places its transform at the
        /// sprite's <i>pivot</i>. For a centred pivot the two differ by half the height; for a bottom
        /// pivot the transform already <i>is</i> the bottom and the loot would sink half its height into
        /// the floor. Measured from <c>renderer.bounds.min.y</c> once, at spawn, so neither case has to be
        /// guessed at.</para>
        /// </summary>
        private float _bottomOffsetPx = LootFallRules.DefaultHeightPx * 0.5f;

        /// <summary>Half the loot's width in <b>px</b> — AS3 <c>scX / 2</c>.</summary>
        private float _halfWidthPx = LootFallRules.DefaultWidthPx * 0.5f;

        /// <summary>
        /// The two probes handed to <see cref="LootFallStepRules.Step"/> — cached, because a method group
        /// converted at the call site allocates a delegate per sub-step and this runs per loot per frame.
        /// </summary>
        private LootTileProbe _tileProbe;

        private LootShelfProbe _shelfProbe;

        /// <summary>
        /// The <c>sndFall</c> this pickup plays when it lands — <c>Loot.sndFall</c>
        /// (<see cref="LootFallRules.ResolveFallSoundId"/>). Empty means silent, which is what the oracle
        /// does for loot dropped into an inactive location (<c>Loot.as:234-237</c>).
        /// </summary>
        private string _fallSoundId = string.Empty;

        /// <summary>Whether the pickup is still in the air. Exposed for the overlay and for fixtures.</summary>
        public bool IsFalling => _falling;

        /// <summary>Vertical speed in px/frame (positive up) — AS3 <c>Loot.dy</c> mirrored.</summary>
        public float FallSpeedPixelsPerFrame => _vy;

        private float _tickAccumulator;

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
        /// <para><b>Neither half reads <c>definition.displayName</c> as a first choice, and the item half
        /// must not read it at all.</b> Both fields are inspector placeholders that no importer has ever
        /// overwritten: <see cref="WeaponDefinition.displayName"/> ships as <c>""</c> on all 213 weapon
        /// assets, and <see cref="ItemDefinition.displayName"/> ships as the literal
        /// <see cref="PipItemNames.PlaceholderName"/> — <c>"Item Name"</c> — on <b>all 500</b> item
        /// assets. Reading the item field is what made every take read <i>"Found: Item Name"</i> and
        /// every prompt read <i>"Take Item Name"</i>, while the PipBuck row for the same item read
        /// "Screwdriver": the two disagreed because only one of them went through the localisation
        /// document. See <see cref="ItemName"/>.</para>
        /// </summary>
        private string DisplayName => WeaponDefinition != null
            ? WeaponName(PfeTextTableProvider.Shared, WeaponDefinition, Variant)
            : ItemName(PfeTextTableProvider.Shared, Definition);

        /// <summary>
        /// A weapon pickup's name — <c>Item.as:206-220</c>.
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
        ///
        /// <para><b>A variant reads the <c>id^N</c> row, then falls back to <c>" - II"</c></b> —
        /// <c>Item.as:206-220</c>, which is the same rule the PipBuck row uses, so the prompt and the
        /// inventory list cannot disagree about what the player just picked up. It is delegated to
        /// <see cref="PipItemNames.Weapon"/> rather than re-implemented: 55 of the 71 variants have a
        /// bespoke name (<c>spear^1</c> is "Viper`s bite") and the other 16 get the literal suffix.</para>
        /// </summary>
        /// <param name="text">The localisation document, or null.</param>
        /// <param name="weapon">The weapon row.</param>
        /// <param name="variant">The <c>^N</c> tier; 0 is the plain weapon.</param>
        public static string WeaponName(PipTextTable text, WeaponDefinition weapon, int variant)
        {
            if (weapon == null) return string.Empty;

            if (variant > 0)
            {
                return PipItemNames.Weapon(text, weapon.weaponId, variant, weapon.displayName);
            }

            return !string.IsNullOrEmpty(weapon.displayName)
                ? weapon.displayName
                : PfeTextTableProvider.WeaponNameOrId(weapon.weaponId);
        }

        /// <summary>
        /// An item pickup's name — <c>Item.as:240-267</c>, delegated to <see cref="PipItemNames.Item"/>
        /// so the take message, the rising label, the prompt and the PipBuck row are one rule and not
        /// two.
        ///
        /// <para><b>The bug this closes.</b> <see cref="ItemDefinition.displayName"/>'s C# initialiser is
        /// the literal <c>"Item Name"</c> and the item importer never writes the field, so all 500
        /// shipped assets carry that placeholder (measured: <c>grep -c</c> over
        /// <c>Data/Resources/Items/*.asset</c>). This method used to return the field verbatim whenever
        /// it was non-empty — which is always — so every take printed <i>"Found: Item Name"</i> and the
        /// floating label over the player said <i>"Item Name"</i>, while the PipBuck listed the same
        /// stack as "Screwdriver". The Oracle has no name on the row at all: <c>Item.nazv</c> is
        /// <i>assigned</i> from the localisation document, which is exactly what
        /// <see cref="PipItemNames"/> ports, placeholder test included.</para>
        ///
        /// <para><b>Why the table is a parameter.</b> Same reason <see cref="PipItemNames"/> is
        /// Unity-free: a fixture can feed this the shipped <c>text_en.xml</c> parsed from disk (or a
        /// four-line inline document) and assert the answer offline, without a <c>Resources.Load</c>.
        /// The property above passes <see cref="PfeTextTableProvider.Shared"/>.</para>
        /// </summary>
        /// <param name="text">The localisation document, or null.</param>
        /// <param name="definition">The item row, or null.</param>
        public static string ItemName(PipTextTable text, ItemDefinition definition)
        {
            if (definition == null) return string.Empty;

            // The `@base` variant branch, then the item table, then the schematic override — and, when
            // the document cannot name the row, the definition's own name *only if it is not the
            // placeholder*, else the id. That last step is `PipItemNames.Finish`; re-implementing it
            // here is how the placeholder got back on screen in the first place.
            return PipItemNames.Item(text, definition.itemId, definition.legacyTip,
                definition.baseItemId, definition.modifierType, definition.workStation,
                definition.displayName);
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
                ? inventory.Submit(InventoryCommand.AddWeapon(WeaponDefinition.weaponId, Variant, Condition))
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

            // The feedback the original game shows on every take: the "Found: …" line in the corner
            // message list and the rising label over the player (AS3 `Invent.take`'s tail,
            // unit/Invent.as:1224-1300). Published AFTER the add was accepted, because the oracle's
            // `infoText` calls all sit behind `if (tr == 0 && !testLoot)` — a rejected take must be
            // silent, and the port's rejection path returns above.
            //
            // `tip` is the raw AS3 @tip, which is what the oracle's whole if/else chain tests. A weapon
            // has no item row at all in the port, so its tip is the constant the chain compares against.
            //
            // `hasSkillRows` reads the raw <sk/> payload, matching `Boolean(l.xml.sk.length())`. It is
            // import-dependent: 0 of the 500 shipped assets carry the field until
            // `PFE > Data > Simple Import All Data` runs, so until then every item classifies as the
            // catch-all arm. The only visible effect is a one-step colour difference on the ~32 rows that
            // do have <sk> children — unlike `possLockPick`, nothing gates on this.
            ItemPickedUpMessage message = ItemPickupFeedback.Build(
                ItemId,
                DisplayName,
                WeaponDefinition != null ? "weapon" : Definition?.legacyTip,
                hasSkillRows: Definition != null && !string.IsNullOrEmpty(Definition.skillRowsSource),
                isMoney: string.Equals(ItemId, "money"),
                quantity: Quantity,
                // AS3's `infoText("takeWeapon", nazv, round(sost * multHP * 100))`
                // (Invent.as:1082, :1099) — the percentage the corner message shows for a weapon.
                // Items keep 100: their condition lives on a definition the port does not carry.
                conditionPercent: WeaponDefinition != null ? Mathf.RoundToInt(Condition * 100f) : 100,
                forcedColour: -1,        // the port's ItemDefinition carries no AS3 `fc`
                playerPosition: user != null ? user.transform.position : transform.position);

            // #3, the one-time tutorial popup (`Invent.as:1293-1300`). Two of the oracle's three
            // conditions are decided here:
            //   * the row carries a `mess` key at all;
            //   * `World.w.helpMess || l.tip == "art"` — the option, or an artefact bypassing it;
            //   * `triggers["mess_" + key] <= 0` — not shown yet this save.
            //
            // The third is only READ here. The WRITE is the HUD's, because 33 of the 45 `mess` keys in
            // AllData have no `<item id=key><mess>` row in the shipped text_en.xml and a latch written
            // here would burn them: `ShowImportantMessage` logs the miss and shows nothing, and the key
            // would then be latched for a text file that never arrives. Latching on success costs one
            // warning per pickup of an un-texted item and keeps the tutorial alive for a language file
            // that is more complete.
            //
            // The file the KEY is read from is table-explicit, exactly as the Oracle's is: `take`'s
            // `if(l.tip == Item.L_WEAPON)` branch indexes `AllData.d.weapon`, and 32 ids exist in BOTH
            // tables — so one item-first lookup would read the wrong row for those. Same reason the
            // inventory command above is split into AddWeapon/AddItem.
            bool isWeapon = WeaponDefinition != null;
            string messKey = isWeapon
                ? ItemPickupFeedback.ResolveWeaponMessKey(ItemId, WeaponDefinition.mess)
                : ItemPickupFeedback.ResolveItemMessKey(ItemId, Definition?.mess);
            if (messKey != null)
            {
                CampaignManager campaign = CampaignManager.Current;
                bool alreadyShown = campaign != null
                    && campaign.GetTrigger(PickupFeedbackRules.MessTriggerKey(messKey)).IsGreaterThanZero();

                // `tip` is passed as the raw AS3 classification the oracle's gate compares. A weapon's
                // tip is its numeric `@tip`, which never equals "art", so the option alone decides —
                // which is what Invent.as:1293 says for weapons too.
                if (PickupFeedbackRules.ShowsImportantMessage(
                        HelpMessages.Enabled, isWeapon ? "weapon" : Definition?.legacyTip, messKey, alreadyShown))
                {
                    message.MessKey = messKey;
                    // The icon is the weapon's own held sprite for a weapon, and the item's own icon for
                    // an item — the same sprite the pickup is drawn with, which is the closest thing the
                    // port has to the oracle's `imp.ico.gotoAndStop(nico)` frame set.
                    message.Icon = isWeapon ? GetComponent<SpriteRenderer>()?.sprite : Definition?.icon;
                }
            }

            ItemPickupFeedback.Publish(message);

            Debug.Log($"[WorldItemPickup] Took {Quantity} × '{ItemId}' at " +
                      $"{transform.position.x:0.##},{transform.position.y:0.##}.");

            // `Loot.as:304` — `levitPoss = false; loc.remObj(this);`. The object is about to be destroyed,
            // so this only matters for the one frame between here and the hold tick: it makes the caster
            // release it through the ordinary `!levitPoss` drop rule instead of chasing a corpse.
            _levitPoss = false;
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
        /// <para><b>A spawned pickup falls, if it is given a room.</b> AS3 constructs every rolled item
        /// with <c>jump = true</c> (<c>LootGen.as:293</c>) and every player drop with <c>jump = true</c>
        /// too (<c>Invent.as:1550</c>), so both are lobbed and then fall under gravity to the floor,
        /// playing their own <c>sndFall</c> when they land. Pass <paramref name="room"/> to get that;
        /// leave it null and the pickup stays exactly where it was placed, which is what the debug
        /// overlay wants. The arithmetic is <see cref="LootFallRules"/>; the component only carries it
        /// out.</para>
        ///
        /// <para><paramref name="fallSoundId"/> is the resolved <c>Loot.sndFall</c> — see
        /// <see cref="LootFallRules.ResolveFallSoundId"/>. It is a parameter rather than a lookup here
        /// because the ladder needs the roll's <b>tip</b>, which only the caller has: a weapon's fall
        /// sound lives on <c>snd/@fall</c>, everything else on <c>@fall</c>, and <c>scheme</c>/<c>book</c>
        /// default to paper. Null or empty means the landing is silent.</para>
        ///
        /// <para><paramref name="autoCollect"/> defaults to <c>false</c> deliberately — see
        /// <see cref="AutoCollect"/> for which of the oracle's three call sites pass which value. A
        /// caller that has not considered walk-over collection gets the conservative behaviour.</para>
        /// </summary>
        public static WorldItemPickup Spawn(ItemDefinition definition, int quantity, Vector3 position,
                                            bool autoCollect = false, bool jump = false,
                                            RoomInstance room = null, string fallSoundId = null)
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
                             itemDefinition: definition, weaponDefinition: null,
                             variant: 0, condition: 1f,
                             jump: jump, room: room, fallSoundId: fallSoundId);
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
        ///
        /// <para><b>Two extra parameters, both from the roll.</b> <paramref name="variant"/> is the
        /// <c>^N</c> tier (<c>Item.as:132-136</c>) and <paramref name="condition"/> is
        /// <c>sost * multHP</c> (<c>Invent.as:1068-1075</c>). Both default to "plain and pristine", so
        /// every pre-existing caller keeps its behaviour, and a drop that knows better passes them.</para>
        /// </summary>
        public static WorldItemPickup Spawn(WeaponDefinition weaponDefinition, int quantity, Vector3 position,
                                            bool autoCollect = false, int variant = 0, float condition = 1f,
                                            bool jump = false, RoomInstance room = null,
                                            string fallSoundId = null)
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
                             itemDefinition: null, weaponDefinition: weaponDefinition,
                             variant: variant, condition: condition,
                             jump: jump, room: room, fallSoundId: fallSoundId);
        }

        /// <summary>
        /// The part of spawning that is the same for both content types: the GameObject, the trigger
        /// collider, the renderer, the sorting and the debug list.
        /// </summary>
        private static WorldItemPickup SpawnCore(string id, int quantity, Vector3 position, bool autoCollect,
                                                 Sprite icon, Color fallbackColor,
                                                 ItemDefinition itemDefinition,
                                                 WeaponDefinition weaponDefinition,
                                                 int variant, float condition,
                                                 bool jump, RoomInstance room, string fallSoundId,
                                                 DroppedItemInstance existingRecord = null,
                                                 bool settled = false)
        {
            var go = new GameObject($"Loot_{id}_{quantity}");
            go.transform.position = position;

            var pickup = go.AddComponent<WorldItemPickup>();
            pickup.ItemId = id;
            pickup.Quantity = quantity;
            pickup.Definition = itemDefinition;
            pickup.WeaponDefinition = weaponDefinition;
            pickup.AutoCollect = autoCollect;
            pickup.Variant = variant;
            pickup.Condition = Mathf.Clamp01(condition);

            // A trigger, so loot never blocks movement. AS3's Loot is a world object with no collision
            // against the unit either — it is taken, not bumped into.
            CircleCollider2D collider = go.AddComponent<CircleCollider2D>();
            collider.radius = PickupRadius;
            collider.isTrigger = true;

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = icon != null ? icon : MakeFallbackSprite();
            renderer.color = icon != null ? Color.white : fallbackColor;

            // ── The unlit presenter material: without it the loot is drawn PURE BLACK ───────────────
            //
            // This project renders 2D through URP's Renderer2D, whose DEFAULT sprite material is
            // `Sprite-Lit-Default`. A lit sprite's output is `texture × (sum of the Light2Ds that apply
            // to its sorting layer)`, so on a sorting layer that no light covers it is multiplied by
            // zero — the correct silhouette in the correct place, drawn solid black, with alpha intact.
            // That is exactly what "collectable objects are black" looked like: the cogwheel's shape and
            // its hole were right, the silver was gone.
            //
            // `BackgroundPhysicalObjects` — the layer below — is NOT in `Global Light 2D`'s
            // `m_ApplyToSortingLayers` list (neither is `BackgroundObject`), so anything on it that keeps
            // the lit default renders black. Every other map presenter already avoids this by asking
            // `SpritePresenterMaterial.Get()` for the unlit shader (RoomObjectVisualManager:353,
            // RoomUnitSpawner:1583, RoomBackdropRenderer:1378, UnitController:5600) — this component was
            // the one that forgot, which is why loot was the only black thing on screen.
            //
            // Do NOT "fix" this by adding the layer to the light instead. The convention here is that
            // map presenters are unlit, and a presenter that depends on a scene light is a presenter that
            // goes black the day the light is retuned. Assigning the shared material also keeps loot in
            // the same batch as the props around it.
            Material presenterMaterial = SpritePresenterMaterial.Get();
            if (presenterMaterial != null)
            {
                renderer.sharedMaterial = presenterMaterial;
            }
            else
            {
                Debug.LogWarning("[WorldItemPickup] No unlit sprite shader resolved (URP 2D sprite or " +
                                 "Sprites/Default); the pickup will use the default material, which renders " +
                                 "black on an unlit sorting layer.");
            }

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

            // ── The record, before the view starts moving ───────────────────────────────────────────
            //
            // AS3 registers the Loot with the location in the Loot constructor (`Loot.as:246`,
            // `this._loc.addObj(this)`), and that registration is what makes floor loot part of the
            // room rather than of the screen. Here it is the same idea with the ownership the other way
            // round: the room holds the record, this component holds a reference to it.
            //
            // The room is resolved ONCE, here, because `BeginFall` used to do it separately and the two
            // must agree — a record filed under one room and a fall resolved against another is a
            // pickup that lands in the wrong grid. `room ?? RoomProvider` is the pre-existing rule, not
            // a new one: it is what a caller that passes no room has always meant.
            RoomInstance ownerRoom = room ?? RoomProvider?.Invoke();

            DroppedItemInstance record = existingRecord;
            if (record == null && ownerRoom != null)
            {
                record = new DroppedItemInstance
                {
                    itemId = id,
                    isWeapon = weaponDefinition != null,
                    quantity = quantity,
                    variant = variant,
                    condition = pickup.Condition,
                    autoCollect = autoCollect,
                    position = ToRoomLocal(ownerRoom, position),
                };
                ownerRoom.AddDroppedItem(record);
            }

            pickup.Record = record;
            if (record != null)
            {
                // The restored position is authoritative when there is one: `position` above came from
                // the record, but the conversion round-trip is not exactly identity for a room whose
                // origin is not a whole pixel, and the record is the thing the save file holds.
                if (settled) pickup.transform.position = FromRoomLocal(ownerRoom, record.position);
            }

            pickup.BeginFall(jump, ownerRoom, fallSoundId, settled);

            Live.Add(pickup);
            return pickup;
        }

        /// <summary>
        /// Take this pickup out of the world for good — it was collected, consumed, or cleared by the
        /// debug overlay. <b>The record goes with it</b>, because the room must not rebuild it.
        /// </summary>
        /// <remarks>
        /// Contrast <see cref="DestroyView"/>, which is what a room teardown calls: same destruction of
        /// the GameObject, opposite decision about the record. Getting these two the wrong way round
        /// produces either duplicate loot or loot that vanishes when the player walks out of the room,
        /// and neither shows up in a log.
        /// </remarks>
        public void Despawn()
        {
            ForgetRecord();
            Live.Remove(this);
            DestroyGameObject();
        }

        /// <summary>
        /// Destroy the GameObject but keep the record — the room's view is being torn down and will be
        /// rebuilt from <see cref="RoomInstance.droppedItems"/> on re-entry.
        /// </summary>
        /// <remarks>
        /// Position is copied first. A pickup can be destroyed mid-fall (a transition is one frame away
        /// from any frame the player triggers), and the record has to hold where it actually was.
        /// </remarks>
        public void DestroyView()
        {
            SyncRecordPosition();
            Record = null;
            Live.Remove(this);
            DestroyGameObject();
        }

        /// <summary>Detach this view from its record and drop the record from the room.</summary>
        private void ForgetRecord()
        {
            if (Record == null)
            {
                return;
            }

            _room?.RemoveDroppedItem(Record);
            Record = null;
        }

        private void DestroyGameObject()
        {
            if (this == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(gameObject);
            }
            else
            {
                // `Destroy` is a no-op in edit mode (it logs "Destroy may not be called from edit mode"),
                // so an EditMode fixture that takes or tears down a pickup would leak the GameObject and
                // its 1×1 fallback sprite for the rest of the run. Same seam, same reason, as
                // `RoomUnitSpawner.DestroyObject` and `TileRenderer.DestroyTile`.
                DestroyImmediate(gameObject);
            }
        }

        // ── Rebuilding a room's loot from its records ────────────────────────

        /// <summary>
        /// Destroy every live pickup belonging to <paramref name="room"/>, keeping the records.
        /// Called by <c>RoomVisualController.ClearVisuals</c>.
        /// </summary>
        /// <returns>How many views were destroyed.</returns>
        public static int DestroyViewsForRoom(RoomInstance room)
        {
            if (room == null || Live.Count == 0)
            {
                return 0;
            }

            // Snapshot first: DestroyView mutates Live.
            WorldItemPickup[] copy = Live.ToArray();
            int destroyed = 0;
            for (int i = 0; i < copy.Length; i++)
            {
                WorldItemPickup pickup = copy[i];
                if (pickup == null || !ReferenceEquals(pickup._room, room))
                {
                    continue;
                }

                pickup.DestroyView();
                destroyed++;
            }

            return destroyed;
        }

        /// <summary>
        /// Build a view for every record on <paramref name="room"/> that does not already have one.
        /// Called by <c>RoomVisualController.Initialize</c>, after the props and units are up.
        /// </summary>
        /// <returns>How many pickups were rebuilt.</returns>
        /// <remarks>
        /// <para><b>The already-live check is not defensive, it is required.</b> <c>Initialize</c> also
        /// runs when a save is applied to the room the player is standing in, and a pickup dropped in
        /// that room a moment earlier is still live — rebuilding it would leave two views over one
        /// record, so taking one would silently leave the other behind.</para>
        ///
        /// <para><b>A record whose id no longer resolves is kept, not dropped.</b> The resolver is
        /// published by <c>PlayerInventory.Construct</c>, and a room can be built before the player
        /// exists. Deleting the record in that window would destroy a save's loot because of a load
        /// order. The record stays on the room and is retried on the next build.</para>
        /// </remarks>
        public static int RestoreForRoom(RoomInstance room)
        {
            if (room?.droppedItems == null || room.droppedItems.Count == 0)
            {
                return 0;
            }

            int restored = 0;
            for (int i = 0; i < room.droppedItems.Count; i++)
            {
                DroppedItemInstance record = room.droppedItems[i];
                if (!PFE.Systems.Map.RoomStateRules.IsRestorableLoot(record))
                {
                    continue;
                }

                if (HasLiveView(record))
                {
                    continue;
                }

                Vector3 position = FromRoomLocal(room, record.position);

                if (record.isWeapon)
                {
                    WeaponDefinition weapon = WeaponDefinitionResolver?.Invoke(record.itemId);
                    if (weapon == null)
                    {
                        continue;
                    }

                    Sprite icon = weapon.weaponVisual != null ? weapon.weaponVisual.IdleSprite : null;
                    if (SpawnCore(weapon.weaponId, record.quantity, position, record.autoCollect,
                                  icon: icon, fallbackColor: WeaponFallbackColor,
                                  itemDefinition: null, weaponDefinition: weapon,
                                  variant: record.variant, condition: record.condition,
                                  jump: false, room: room, fallSoundId: null,
                                  existingRecord: record, settled: true) != null)
                    {
                        restored++;
                    }

                    continue;
                }

                ItemDefinition item = ItemDefinitionResolver?.Invoke(record.itemId);
                if (item == null)
                {
                    continue;
                }

                if (SpawnCore(item.itemId, record.quantity, position, record.autoCollect,
                              icon: item.icon, fallbackColor: item.fallbackColor,
                              itemDefinition: item, weaponDefinition: null,
                              variant: 0, condition: 1f,
                              jump: false, room: room, fallSoundId: null,
                              existingRecord: record, settled: true) != null)
                {
                    restored++;
                }
            }

            return restored;
        }

        private static bool HasLiveView(DroppedItemInstance record)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                WorldItemPickup pickup = Live[i];
                if (pickup != null && ReferenceEquals(pickup.Record, record))
                {
                    return true;
                }
            }

            return false;
        }

        // ── Falling ──────────────────────────────────────────────────────────

        /// <summary>
        /// Put this pickup in the air — the tail of AS3's <c>Loot</c> constructor
        /// (<c>Loot.as:221-237</c>): the sprite is measured for the take box, and a <c>jump</c> drop is
        /// lobbed.
        ///
        /// <para><b>Why the box is measured off the renderer.</b> AS3 assigns <c>scX = vis.width</c>,
        /// <c>scY = vis.height</c> (<c>:226-227</c>) — the loot's collision box <i>is</i> its artwork, so
        /// its bottom edge is what rests on the floor. A fixed 30×20 box would sink a tall sprite 8 px
        /// into the tile it lands on, and a short one would hover. The renderer's <b>world bounds</b> are
        /// read rather than the sprite's size, so a sprite whose pivot is not centred still lands on its
        /// drawn bottom edge.</para>
        /// </summary>
        private void BeginFall(bool jump, RoomInstance room, string fallSoundId, bool settled = false)
        {
            _room = room;
            _fallSoundId = fallSoundId ?? string.Empty;

            var renderer = GetComponent<SpriteRenderer>();
            if (renderer != null && renderer.sprite != null)
            {
                Bounds bounds = renderer.bounds;
                _heightPx = Mathf.Max(1f, bounds.size.y / PixelsToUnits);
                _halfWidthPx = Mathf.Max(0.5f, bounds.size.x * 0.5f / PixelsToUnits);
                _bottomOffsetPx = (transform.position.y - bounds.min.y) / PixelsToUnits;
            }

            // No room means no grid to land on: leave the pickup exactly where it was asked to be, which
            // is the behaviour every caller had before this existed.
            if (_room == null) return;

            // A record rebuilt from the room is already at rest, and re-arming the fall would drop it a
            // second time — from a position that is already the floor, so it would either jitter or,
            // worse, land on whatever tile the probe happens to hit now instead of where it was left.
            // The probes are deliberately NOT wired in this case either: nothing here needs them.
            if (settled) return;

            _tileProbe = ProbeTile;
            _shelfProbe = ProbeShelf;

            if (jump)
            {
                LootFallRules.JumpVelocity(UnityEngine.Random.value, UnityEngine.Random.value,
                                           out _vx, out _vy);
            }

            // A drop with no lob is still a drop: a container's eject may be a straight release, and the
            // oracle's non-jump constructor path leaves the loot at rest only because it is placed on the
            // ground. Here the caller may have placed it in mid-air, so the fall is always armed.
            _falling = true;
        }

        /// <summary>
        /// AS3 runs this once per 30 Hz frame (<c>Loot.step</c>). Unity calls it per rendered frame, so the
        /// accumulator hands the model ticks of a fixed length — see <see cref="TickSeconds"/>.
        ///
        /// <para><b>Not an <c>ISimTickable</c>, deliberately.</b> A tickable has to be registered, and an
        /// unregistered one fails <i>silently</i> — the trap <c>PlayerAutoPickup</c> documents at length.
        /// A pickup is created at arbitrary moments by arbitrary code, so a self-driving <c>Update</c> is
        /// the only hook that cannot be forgotten.</para>
        /// </summary>
        private void Update()
        {
            if (_falling)
            {
                // A tab-out can hand in a delta of seconds; capping it keeps one frame from teleporting a
                // pickup through a floor, and the cap is a whole tick so nothing is lost in the common case.
                _tickAccumulator += Mathf.Min(Time.deltaTime, TickSeconds * 4f);

                while (_tickAccumulator >= TickSeconds && _falling)
                {
                    _tickAccumulator -= TickSeconds;
                    FallTick();
                }
            }

            // The record follows the view, not the other way round, and it has to follow even when the
            // view is not falling: the magnet (`PlayerAutoPickup`) writes the transform directly and
            // clears `_falling`, so a sync placed inside the fall path would leave the record at the
            // position the item was lobbed from. That position is what a room rebuild restores, so the
            // item would jump back across the room the next time the player walked in.
            SyncRecordPosition();
        }

        /// <summary>
        /// Copy this view's position into its room-owned record, in room-local pixels. See
        /// <see cref="DroppedItemInstance.position"/>.
        /// </summary>
        private void SyncRecordPosition()
        {
            if (Record == null || _room == null)
            {
                return;
            }

            Record.position = ToRoomLocal(_room, transform.position);
        }

        /// <summary>
        /// Unity world units → room-local pixels. The inverse of <see cref="FromRoomLocal"/>.
        /// </summary>
        /// <remarks>
        /// <b>The origin subtraction is the whole point.</b> <c>transform.position</c> is world; the room
        /// grid, <c>TileData.GetBounds()</c> and every other record's <c>position</c> are room-local. The
        /// two agree only in a room at land (0,0), which is exactly the room a quick test visits — the
        /// same trap <c>LootFallStepRules</c> and <c>TilePhysicsController.SetRoom</c> both document.
        /// </remarks>
        public static Vector2 ToRoomLocal(RoomInstance room, Vector3 worldPosition)
        {
            Vector2 origin = WorldCoordinates.RoomOriginPixel(room);
            Vector2 pixels = WorldCoordinates.UnityToPixel(worldPosition);
            return new Vector2(pixels.x - origin.x, pixels.y - origin.y);
        }

        /// <summary>Room-local pixels → Unity world units. See <see cref="ToRoomLocal"/>.</summary>
        public static Vector3 FromRoomLocal(RoomInstance room, Vector2 roomLocalPixels)
        {
            Vector2 origin = WorldCoordinates.RoomOriginPixel(room);
            return WorldCoordinates.PixelToUnity(
                new Vector2(roomLocalPixels.x + origin.x, roomLocalPixels.y + origin.y));
        }

        /// <summary>
        /// One 30 Hz tick of <c>Loot.step</c>'s physics half (<c>Loot.as:332-400</c>) — gravity, then the
        /// sub-stepped move, then the water check.
        ///
        /// <para><b>The parts of the oracle that are not here, and why.</b> <c>step()</c> also runs the
        /// take window (<c>ttake</c>), the magnet's <c>vsos</c> state and <c>auto &amp;&amp; auto2</c>
        /// collection. Those are the <i>collection</i> half, which the port already has in
        /// <see cref="PFE.Entities.Player.PlayerAutoPickup"/>; duplicating them here would give the port two
        /// owners for one decision. The one piece that had to move is <c>vsos</c> itself: a magnetised
        /// pickup must stop falling, or the magnet and gravity fight for the position.</para>
        /// </summary>
        public void FallTick()
        {
            if (!_falling) return;

            // ── Levitation replaces the whole gravity half of `Loot.step` ─────────────────────────────
            //
            // `if(!levit && !this.vsos && dy < World.maxdy) dy += World.ddy; else if(levit && !isPlav)
            //  { dy *= 0.8; dx *= 0.8; }` (`Loot.as:352-360`) — the two branches are exclusive, so a
            // carried pickup must not run the gravity line at all. It still integrates and collides
            // through `StepOnce`, which is `run()`.
            if (_levitating)
            {
                LevitateTick();
                return;
            }

            if (_magnetized)
            {
                // AS3 `if(!levit && !this.vsos && dy < maxdy) dy += ddy` — the magnet suppresses gravity.
                _falling = false;
                return;
            }

            _vy = LootFallRules.StepVelocity(_vy);

            int steps = LootFallRules.SubstepCount(_vx, _vy);
            for (int i = 0; i < steps && _falling; i++)
            {
                StepOnce(steps);
            }

            // `checkWater()` runs after the run loop whether or not this tick landed the loot
            // (`Loot.as:380`, inside `if(!stay)`). It cannot splash on a landing tick — `run()` has
            // already zeroed `dy`, and the gate is `dy > 5` — but it does keep `isPlav` truthful, and
            // skipping it left the flag stale on the one tick the loot stopped.
            CheckWater();
        }

        /// <summary>
        /// One 30 Hz tick of a <b>carried</b> pickup — the two halves the oracle splits between the caster
        /// and the object, in the order AS3 runs them.
        ///
        /// <para><b>1. The caster's half</b> — <c>UnitPlayer.as:1247-1262</c>, which is
        /// <c>TelekinesisMath.HoldAccelStep</c>: nudge the velocity by <c>teleAccel</c> toward the target
        /// while outside a 15 px deadzone and under the per-axis <c>teleSpeed</c> cap. There is no
        /// proportional term, so the pickup does not ease in; it accelerates and then coasts.</para>
        ///
        /// <para><b>2. The object's half</b> — <c>Box.forces</c>/<c>Loot.step</c>: <c>dx *= 0.8;
        /// dy *= 0.8</c> (<c>Loot.as:356-360</c>), then <c>run()</c>. The damping is what stops it, and it
        /// is applied at exactly one AS3 frame here, so the plain constant is correct — unlike the prop
        /// path, which may be stepped at 60 Hz and therefore needs
        /// <c>TelekinesisMath.DampingOverInterval</c>. The fixed <see cref="TickSeconds"/> accumulator
        /// above is what makes that true.</para>
        ///
        /// <para><b>The target is compared against the CENTRE, on both axes.</b> AS3 compares
        /// <c>X</c> (horizontal centre) and <c>Y - scY/2</c> (vertical centre); <c>Y</c> is the bottom in
        /// the oracle and in this component (<see cref="_bottomOffsetPx"/>). Comparing the bottom would
        /// bias the vertical target by half the sprite's height — for a 33 px cogwheel that is 16 px
        /// against a 15 px deadzone, i.e. a pickup that settles one whole deadzone low.</para>
        /// </summary>
        private void LevitateTick()
        {
            Vector2 centre = CentreRoomLocalPixels;

            _vx = TelekinesisMath.HoldAccelStep(
                centre.x, _levTargetRoomLocalPixels.x, _vx,
                TelekinesisMath.PlayerTeleSpeed, TelekinesisMath.PlayerTeleAccel, TelekinesisMath.Deadzone);

            _vy = TelekinesisMath.HoldAccelStep(
                centre.y, _levTargetRoomLocalPixels.y, _vy,
                TelekinesisMath.PlayerTeleSpeed, TelekinesisMath.PlayerTeleAccel, TelekinesisMath.Deadzone);

            _vx *= TelekinesisMath.LevitDamping;
            _vy *= TelekinesisMath.LevitDamping;

            int steps = LootFallRules.SubstepCount(_vx, _vy);
            for (int i = 0; i < steps && _levitating; i++)
            {
                StepOnce(steps);
            }

            // `checkWater()` runs outside the `if(!stay)` block in the oracle (`Loot.as:380`), so a
            // carried item splashes when it is dragged into a pool. `dy > 5` gates it, and a carried
            // item's dy is at most 4, so in practice this only fires on the tick after a throw.
            CheckWater();
        }

        /// <summary>
        /// One collision sub-step — the port of <c>Loot.run</c> (<c>Loot.as:402-494</c>).
        ///
        /// <para><b>All of the arithmetic is in <see cref="LootFallStepRules.Step"/>; this method is the
        /// conversion and the side effects.</b> That split is not tidiness. The previous version kept the
        /// decision inline and shipped with the landing test's comparisons <i>un-mirrored</i> — an
        /// unsatisfiable predicate that made loot fall through every floor in the game — because no test
        /// can run a <c>MonoBehaviour</c> to notice. See that type's remarks for both defects.</para>
        ///
        /// <para><b>One conversion, room-local.</b> The tile grid, <c>TileData.GetBounds()</c> and the
        /// prop layer all speak room-local pixels; this component's transform speaks world units. The
        /// room origin is subtracted exactly once, here, and added back exactly once, below. Mixing the
        /// two spaces was the second half of that bug, and it is invisible in a room at land (0,0)
        /// because the origin there is zero.</para>
        /// </summary>
        private void StepOnce(int divisor)
        {
            Vector2 origin = WorldCoordinates.RoomOriginPixel(_room);

            float localX = transform.position.x / PixelsToUnits - origin.x;
            float localBottom = transform.position.y / PixelsToUnits - origin.y - _bottomOffsetPx;

            LootFallState state = LootFallStepRules.Step(
                localX, localBottom, _halfWidthPx, _heightPx, _vx, _vy, divisor,
                _room.width * WorldConstants.TILE_SIZE,
                _room.height * WorldConstants.TILE_SIZE,
                _tileProbe, _shelfProbe, _levitating);

            _vx = state.Vx;
            _vy = state.Vy;

            transform.position = new Vector3(
                (state.CentreX + origin.x) * PixelsToUnits,
                (state.Bottom + _bottomOffsetPx + origin.y) * PixelsToUnits,
                transform.position.z);

            switch (state.Contact)
            {
                case LootContact.Floor:
                    // A carried pickup never reports `Floor` — `LootFallStepRules.Step` returns
                    // `LootContact.None` with the bottom clamped while `levitating`, because AS3's
                    // `stay = true` / `dy = dx = 0` half of the landing block is inside `if(!levit)`
                    // (`Loot.as:479-487`). So this branch is the free fall only.
                    _falling = false;
                    // The sound is emitted at the <b>landed</b> position, which is already written above:
                    // the oracle moves `Y` and only then calls `Snd.ps(…, X, Y, …)` (`Loot.as:478-483`).
                    // Playing it before the move put every thud up to one sub-step above the floor it
                    // came from.
                    if (LootFallRules.ShouldPlayLandingSound(state.ImpactSpeed))
                    {
                        PlayFallSound(LootFallRules.LandingVolume(state.ImpactSpeed));
                    }
                    break;

                case LootContact.FellOutOfRoom:
                    // AS3 `if(Y + dy/param1 >= loc.spaceY * tileY) { if(auto2) take(true); dx = 0;
                    // return; }` — it takes an auto-collectible item and otherwise leaves the rest where
                    // they are. A pickup that vanished would lose the player's loot, so this stops it at
                    // the room's bottom edge instead and says so once.
                    _falling = false;
                    Debug.LogWarning($"[WorldItemPickup] '{ItemId}' fell out of the room at " +
                                     $"{localX:0},{localBottom:0} (AS3 would stop it there too, " +
                                     "Loot.as:454-462).");
                    break;
            }
        }

        /// <summary>
        /// The tile at a <b>room-local pixel</b> point — the probe <see cref="LootFallStepRules.Step"/>
        /// asks. Returns false outside the room, which is the same answer as "no tile here".
        /// </summary>
        private bool ProbeTile(float roomLocalX, float roomLocalY, out LootTileBox box,
                               out bool solid, out bool shelf)
        {
            box = default;
            solid = false;
            shelf = false;

            TileData tile = TileAtLocal(roomLocalX, roomLocalY);
            if (tile == null) return false;

            // `GetBounds()` is already the raised-tile box (`zForm` shortens the top, it does not lift
            // the cell), so `yMax` is `phY1` and `yMin` is `phY2` with no further arithmetic.
            Rect bounds = tile.GetBounds();
            box = new LootTileBox(bounds.xMin, bounds.xMax, bounds.yMax, bounds.yMin);

            // `phis == 1` — the one tile kind a loot collides with. `Platform` is the decoder's encoding
            // of AS3 `shelf`, i.e. the one-way catwalk.
            solid = tile.physicsType == TilePhysicsType.Wall;
            shelf = tile.physicsType == TilePhysicsType.Platform;
            return true;
        }

        /// <summary>
        /// AS3 <c>Loot.checkShelf</c> (<c>Loot.as:511-525</c>) — a crate or shelf prop under the loot.
        /// Its parameters are <b>room-local pixels</b> and its <c>deltaY</c> is negative downward, which
        /// is what <see cref="LootFallStepRules.Step"/> hands it.
        /// </summary>
        private bool ProbeShelf(float centreX, float bottom, float deltaY, out float surfaceTop)
        {
            surfaceTop = 0f;
            RoomObjectPhysicsLayer layer = _room != null ? _room.ExistingObjectPhysicsLayer : null;
            return layer != null && layer.TryFindLootSupport(centreX, bottom, deltaY, out surfaceTop);
        }

        /// <summary>
        /// AS3 <c>Loot.checkWater</c> (<c>Loot.as:527-550</c>) — entering water damps the motion and
        /// splashes once. The splash is a separate sample from the landing one
        /// (<c>fall_item_water</c>, volume <c>dy / 10</c> rather than <c>/ 15</c>), so it is not a
        /// variant of <see cref="PlayFallSound"/>.
        /// </summary>
        private void CheckWater()
        {
            Vector2 origin = WorldCoordinates.RoomOriginPixel(_room);
            float x = transform.position.x / PixelsToUnits - origin.x;
            float bottom = transform.position.y / PixelsToUnits - origin.y - _bottomOffsetPx;

            TileData tile = TileAtLocal(x, bottom);
            bool inWater = tile != null && tile.hasWater;

            if (inWater != _inWater)
            {
                // `if(_loc1_ != this.isPlav && dy > 5)` — the splash is on the <b>transition</b>, in either
                // direction, not on entering only. Leaving a pool counts too (a pickup that falls out of
                // the bottom of one), and the `dy > 5` gate is what keeps that from being most exits: an
                // upward exit has a negative dy, so the same comparison silences it for free.
                _inWater = inWater;

                float downwardSpeed = -_vy;
                if (downwardSpeed > LootFallRules.WaterSoundMinDy)
                {
                    SoundBus.Current?.Play(LootFallRules.WaterFallSound,
                                           (Vector2)transform.position,
                                           LootFallRules.WaterVolume(downwardSpeed));
                }
            }

            // `if(this.isPlav) { dy *= 0.7; dx *= 0.7; }` — water drag, every tick it is submerged.
            if (!inWater) return;
            _vx *= 0.7f;
            _vy *= 0.7f;
        }

        private bool _inWater;

        /// <summary>
        /// Play this pickup's landing sample at <paramref name="volumeScale"/>, if it has one and a sound
        /// service is live. Silence is the correct answer for an unarmed pickup — AS3's <c>sndFall</c> is
        /// empty when the location is inactive (<c>Loot.as:234-237</c>).
        /// </summary>
        private void PlayFallSound(float volumeScale)
        {
            if (string.IsNullOrEmpty(_fallSoundId)) return;
            SoundBus.Current?.Play(_fallSoundId, (Vector2)transform.position, volumeScale);
        }

        /// <summary>
        /// Room-local pixel point → its tile. <b>Room-local</b>, not world: <c>GetTileAtCoord</c> indexes
        /// the grid, whose cell (0,0) is the room's floor-left corner, and <c>TileData.GetBounds()</c>
        /// reports that same space. Handing this a world pixel is the bug that made loot land on the
        /// right floor in the first room and on nothing anywhere else.
        /// </summary>
        private TileData TileAtLocal(float roomLocalX, float roomLocalY)
        {
            if (_room == null) return null;
            return _room.GetTileAtCoord(new Vector2Int(
                Mathf.FloorToInt(roomLocalX / WorldConstants.TILE_SIZE),
                Mathf.FloorToInt(roomLocalY / WorldConstants.TILE_SIZE)));
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
            // AS3 `Loot.take()` clears `levitPoss` as it magnetises (`Loot.as:316`), which is what makes
            // the caster's hold tick release it (`UnitPlayer.as:1274`). Reproduced rather than special-
            // cased: the two mechanisms would otherwise fight for the position and the pickup would
            // stutter between the magnet's fifth-of-the-gap step and the hold's acceleration.
            _levitPoss = false;

            // AS3 `Loot.vsos` — a magnetised loot has gravity suppressed (`Loot.as:352`), so the fall has
            // to stop or the two would fight for the position every tick and the pickup would stutter.
            if (_falling)
            {
                _magnetized = true;
                _falling = false;
            }

            Vector2 current = transform.position;
            Vector2 step = new Vector2(
                AutoPickupRule.MagnetStep(target.x - current.x),
                AutoPickupRule.MagnetStep(target.y - current.y));

            transform.position = new Vector3(
                current.x + step.x, current.y + step.y, transform.position.z);
        }

        // ── Telekinesis ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Obj.levitPoss</c> — whether this pickup may be lifted right now. The grab gate reads it,
        /// and the hold tick drops the pickup when it turns false.
        /// </summary>
        public bool IsLiftable => _levitPoss;

        /// <summary>True while telekinesis is carrying this pickup.</summary>
        public bool IsHeldByTelekinesis => _levitating;

        /// <summary>AS3 <c>Loot.massa</c> — see <see cref="As3Massa"/>.</summary>
        public float GetAs3Massa() => As3Massa;

        /// <summary>The room this pickup was filed under, or <c>null</c> for a bare spawn.</summary>
        public RoomInstance Room => _room;

        /// <summary>
        /// This pickup's <b>centre</b> in room-local pixels — the point AS3 measures a telekinesis
        /// distance to. Not <see cref="Position"/>, which is the transform and therefore the sprite's
        /// pivot; and not the bottom, which is what the oracle's <c>Y</c> names but not what the hold
        /// controller compares against.
        /// </summary>
        public Vector2 CentreRoomLocalPixels
        {
            get
            {
                if (_room == null) return Vector2.zero;
                Vector2 origin = WorldCoordinates.RoomOriginPixel(_room);
                float bottom = transform.position.y / PixelsToUnits - origin.y - _bottomOffsetPx;
                return new Vector2(
                    transform.position.x / PixelsToUnits - origin.x,
                    bottom + _heightPx * 0.5f);
            }
        }

        /// <summary>
        /// This pickup's <b>bottom edge</b> in room-local pixels — the point AS3 calls <c>Y</c> and the
        /// one its <c>celDist</c> is measured from (<c>Location.as:3494</c> uses <c>celObj.X/Y</c>, and
        /// <c>Y</c> is the bottom). Used for the grab gate and the drop rule, both of which compare
        /// against <c>teleDist</c>.
        /// </summary>
        public Vector2 BottomRoomLocalPixels
        {
            get
            {
                if (_room == null) return Vector2.zero;
                Vector2 origin = WorldCoordinates.RoomOriginPixel(_room);
                return new Vector2(
                    transform.position.x / PixelsToUnits - origin.x,
                    transform.position.y / PixelsToUnits - origin.y - _bottomOffsetPx);
            }
        }

        /// <summary>
        /// Start carrying this pickup — the object half of <c>UnitPlayer.actTele</c>'s tail
        /// (<c>UnitPlayer.as:1825-1832</c>: <c>levit = 1</c>, <c>stay = false</c>).
        ///
        /// <para><b>Refused without a room, and that is not defensive.</b> Levitation is a <i>motion</i>
        /// model with collision against the room's tile grid; a pickup with no room has no grid, so it
        /// would fly through walls. The debug overlay's bare spawns are exactly that case.</para>
        ///
        /// <para>The two probes are (re-)wired here because <c>BeginFall</c> deliberately skips them for a
        /// record restored at rest (<c>settled: true</c>) — and a restored pickup is precisely the one a
        /// player is most likely to lift, since it is lying on the floor of a room they walked back
        /// into.</para>
        /// </summary>
        /// <param name="targetRoomLocalPixels">Where to put its centre, room-local pixels.</param>
        public bool TryBeginTelekineticHold(Vector2 targetRoomLocalPixels)
        {
            if (_room == null)
            {
                return false;
            }

            _tileProbe ??= ProbeTile;
            _shelfProbe ??= ProbeShelf;

            _levitating = true;
            _magnetized = false;
            _levTargetRoomLocalPixels = targetRoomLocalPixels;

            // AS3 keeps the loot's dx/dy across the grab — `actTele` only sets `levit` — but a pickup at
            // rest has neither, and carrying over a fall's terminal 20 px/frame would make every grab of
            // a mid-air item fling it past the cursor. Zeroing is the oracle's own resting case.
            _vx = 0f;
            _vy = 0f;

            // The levitation tick rides the same accumulator as the fall, so `_falling` is what keeps
            // Update() ticking. It is cleared on release, when the ordinary fall resumes.
            _falling = true;
            return true;
        }

        /// <summary>Where the holder wants this pickup's centre, room-local pixels — written every frame.</summary>
        public void SetTelekineticTarget(Vector2 targetRoomLocalPixels)
        {
            _levTargetRoomLocalPixels = targetRoomLocalPixels;
        }

        /// <summary>
        /// Release the carry — AS3 <c>dropTeleObj</c> (<c>UnitPlayer.as:1904-1920</c>) plus the throw's
        /// velocity impulse (<c>:1852-1868</c>).
        /// </summary>
        /// <param name="velocityPixelsPerSecond">
        /// The release velocity in the port's units (px/second, y-up). A plain drop passes
        /// <see cref="Vector2.zero"/>; a throw passes the impulse. Converted to the pickup's own
        /// px/frame here, at the boundary.
        /// </param>
        public void ReleaseTelekineticHold(Vector2 velocityPixelsPerSecond)
        {
            if (!_levitating) return;

            _levitating = false;
            _vx = velocityPixelsPerSecond.x / TelekinesisMath.As3FramesPerSecond;
            _vy = velocityPixelsPerSecond.y / TelekinesisMath.As3FramesPerSecond;

            // `Loot.as:350` — `if(!stay)` runs the gravity/damping block. The oracle's loot has no `levit`
            // any more, so it falls from here, and `stay` is false because a carried item was never at
            // rest. Arming the fall is what re-enables gravity in FallTick.
            _falling = true;
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
