using System;
using System.Globalization;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Systems.Interaction;
using PFE.Systems.Inventory;
using PFE.Systems.Loot;
using PFE.Systems.Security;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Presenter component for in-room <b>container</b> props — the port of AS3 <c>fe.serv.Interact</c>'s
    /// container half: <c>actOsn()</c> → <c>loot()</c> (<c>Interact.as:1265-1328</c>, <c>:1960-2025</c>).
    ///
    /// <para><b>A prop is a container iff its resolved <c>cont</c> is non-empty and not <c>"empty"</c></b>
    /// — <see cref="ContainerRule"/>, never a family enum or an id list. AS3 has no container class;
    /// <c>cont != null</c> is the only switch (<c>Interact.as:1285-1288</c>). The resolved value is read
    /// placement-first (<c>:332-335</c>) then definition (<c>:210-213</c>), which
    /// <see cref="ContainerRule.ResolveCont"/> owns.</para>
    ///
    /// <para><b>Modelled on <see cref="DoorPropPresenter"/>, deliberately.</b> That class is the worked
    /// precedent for every seam here — admission (<c>RoomObjectVisualManager</c>), the trigger collider
    /// the cursor probe needs, <c>CanInteract</c>'s reach guard, the per-frame press guard, and
    /// <c>IHoldInteractable</c>. The only differences are the verb (roll instead of toggle) and the end
    /// state (<c>runtimeState.lootState</c> instead of <c>isOpen</c>).</para>
    ///
    /// <para><b>Opening ejects; there is no loot window.</b> AS3's container interaction spills the
    /// rolled items onto the floor as <c>fe.loc.Loot</c> props and the box becomes permanently dead
    /// (<c>setAct("loot", n)</c>, <c>:834-844</c>). The port does the same through
    /// <see cref="WorldItemPickup.Spawn"/> — a grid-loot UI would be something the oracle does not
    /// have.</para>
    /// </summary>
    [RequireComponent(typeof(BoxCollider2D))]
    public sealed class ContainerPropPresenter : MonoBehaviour, IInteractable, IHoldInteractable, IInteractableName, ISecurityObject
    {
        /// <summary>How close the player must be — AS3 <c>World.w.actionDist</c>, shared with the
        /// player's own target search so the two cannot disagree.</summary>
        public const float ActionReach = WorldConstants.ACTION_REACH;

        /// <summary>
        /// Half-extent of the eject scatter, in world units. AS3's loot <i>falls</i> and settles; the
        /// port has no falling-loot model (that needs <c>Loot.checkShelf</c> against a physics layer
        /// that does not exist yet), so the burst is placed with a small deterministic scatter instead.
        /// Recorded rather than silently shipped as a static pile — see
        /// <c>docs/DeviceInteractionAndLoot/04_IMPLEMENTATION_PLAN.md</c> D7 Problem B.
        /// </summary>
        private const float ScatterRadius = 0.3f;

        private RoomInstance _room;
        private ObjectInstance _objectInstance;
        private MapObjectVisualDefinition _visual;
        private SpriteRenderer _renderer;
        private BoxCollider2D _triggerCollider;

        private int _lastInteractFrame = -1;

        /// <summary>
        /// The resolved <c>cont</c> for this prop — <see cref="ContainerRule.ResolveCont"/> of the
        /// placement's value over the definition's. Cached because it cannot change while the room lives.
        /// </summary>
        public string ResolvedCont { get; private set; } = string.Empty;

        public RoomInstance Room => _room;
        public ObjectInstance ObjectInstance => _objectInstance;

        /// <summary>Whether this prop is a container at all — AS3 <c>cont != null</c>.</summary>
        public bool IsContainer => ContainerRule.IsContainer(ResolvedCont);

        /// <summary>
        /// Whether this container has already been emptied — AS3 <c>cont == "empty"</c>, which the port
        /// models as <c>runtimeState.lootState &gt; 0</c> (<c>setAct("loot", n)</c> writes both,
        /// <c>Interact.as:840</c>), because <c>lootState</c> is the value that is serialised
        /// (<c>RoomStateSnapshot</c>) and the value the frame chooser already reads
        /// (<c>RoomObjectVisualManager.ResolveFrameIndex</c>).
        /// </summary>
        public bool IsLooted => _objectInstance?.runtimeState != null && _objectInstance.runtimeState.lootState > 0;

        /// <summary>
        /// The HUD prompt. "Open" while there is something to open, empty once looted — AS3
        /// <c>update()</c> (<c>Interact.as:672</c>, <c>:777-780</c>). The "empty" state string is a
        /// separate presentation concern (D12), not this label.
        ///
        /// <para><b>A locked container says so.</b> AS3 writes <c>actionText</c> from
        /// <c>lock</c>/<c>lockTip</c> before it ever reaches the open/close branch
        /// (<c>Interact.as:732-776</c>), so a safe reads "Pick lock" and a mined crate reads "Defuse".
        /// Leaving this as "Open" would let the player press and watch nothing happen.</para>
        /// </summary>
        public string ActionText
        {
            get
            {
                if (!Admits(IsContainer, IsLooted)) return string.Empty;

                string locked = SecurityGate.Prompt(_objectInstance, _room, null);
                if (locked != null) return locked;

                return "Open";
            }
        }

        /// <summary>
        /// The container's display name — AS3 <c>celObj.nazv</c>, the first line of the world-anchored
        /// interaction prompt. Read from the object's definition; falls back to the object id so an
        /// unnamed container still labels something.
        /// </summary>
        public string DisplayName
        {
            get
            {
                string named = _objectInstance != null && _objectInstance.definition != null
                    ? _objectInstance.definition.displayName
                    : null;

                if (!string.IsNullOrEmpty(named)) return named;
                return _objectInstance != null ? _objectInstance.objectId : string.Empty;
            }
        }

        /// <summary>Frames the action key must be held — AS3 <c>Interact.t_action</c>, authored as
        /// <c>time</c>. Almost every container authors none, so this is 0 and it acts on the press.
        ///
        /// <para><b>A locked container always has a hold.</b> AS3 adds the pick duration to the authored
        /// one (<c>UnitPlayer.as:1976</c>), so a safe with no <c>time</c> still takes a full second per
        /// attempt. See <c>DoorPropPresenter.HoldFrames</c> for the same reasoning.</para></summary>
        public int HoldFrames
        {
            get
            {
                int authored = _objectInstance != null ? _objectInstance.GetHoldFrames() : 0;
                return authored + SecurityGate.ExtraHoldFrames(_objectInstance, _room);
            }
        }

        /// <summary>The container's own world position — the hold's range guard and the eject origin.</summary>
        public Vector3 WorldPosition => transform.position;

        public void Initialize(
            RoomInstance room,
            ObjectInstance obj,
            MapObjectVisualDefinition visual,
            SpriteRenderer renderer)
        {
            _room = room;
            _objectInstance = obj;
            _visual = visual;
            _renderer = renderer;

            ResolvedCont = ContainerRule.ResolveCont(
                obj?.GetAttribute("cont", null),
                obj?.definition != null ? obj.definition.containerId : null);

            _triggerCollider = GetComponent<BoxCollider2D>();
            if (_triggerCollider != null)
            {
                _triggerCollider.isTrigger = true;
                ConfigureCollider();
            }
        }

        /// <summary>
        /// Whether the player may work this container. The shape test (is it a container, is it already
        /// looted) is split out so a test can assert it without a scene, exactly as
        /// <c>DoorPropPresenter.AdmitsPlayerInteraction</c> is.
        /// </summary>
        public static bool Admits(bool isContainer, bool isLooted) => isContainer && !isLooted;

        public bool CanInteract(GameObject user)
        {
            if (!Admits(IsContainer, IsLooted)) return false;
            if (user == null) return false;

            float dist = Vector2.Distance(transform.position, user.transform.position);
            return dist <= ActionReach;
        }

        /// <summary>
        /// Open the container: roll its table, eject the results, and mark it looted. Called by the
        /// player's interaction path — this component reads no input (same ownership split as the door).
        /// </summary>
        public void Interact(GameObject user)
        {
            if (!Admits(IsContainer, IsLooted)) return;

            // Guarded per frame for the same reason the door is: the press can arrive twice (the
            // MessagePipe subscription and the direct poll), and a second roll on the same frame would
            // double the loot.
            if (Time.frameCount == _lastInteractFrame) return;
            _lastInteractFrame = Time.frameCount;

            // The lock before the loot — AS3's act() resolves mine, then lock, and only then reaches
            // actOsn() → loot() (Interact.as:911-1244, :1285-1288). A failed pick must not spill the
            // contents, which is the one thing this ordering guarantees.
            SecurityGateOutcome security = SecurityGate.TryWork(user, _objectInstance, _room);
            if (security == SecurityGateOutcome.Handled)
            {
                return;
            }

            LootContext ctx = LootRuntime.CreateContext(ResolvedCont);
            if (ctx == null)
            {
                // LootRuntime has already logged why. Do NOT mark the container looted — an unready
                // runtime must not consume the container.
                return;
            }

            ApplyRoomDifficulty(ctx, user);

            // ── The authored items come FIRST ────────────────────────────────────────────────────
            //
            // AS3 `Interact.loot()` ejects the object's own `<item>` children (`Interact.as:1973-2000`)
            // and only then rolls `cont` (`:2005-2019`). Both halves are needed and the order is
            // load-bearing: `any` is the OR of the two, and the `act` value the container ends on
            // depends on whether an *important* authored item was among them.
            //
            // This is the half that carries the campaign's first quest item. `col1` (the Cogwheel)
            // appears in NO loot table anywhere in the game — it reaches the world only through
            // `<item id="col1" imp="1"/>` on the begin land's `chest`/`wallcab`/`case`/`instr1`/
            // `instr2`/`medbox` props (186 `<item>` children across all room data). Without this call
            // those props rolled only their `cont` table, so the Cogwheel never existed and `collectHS`
            // could never be granted.
            int authored = SpillAuthoredItems(user, ctx, out bool anyImportant);

            bool anyItems = LootRoller.RollContainer(ResolvedCont, ctx);

            int spawned = SpillRolls(user, ctx) + authored;
            ReportUnmatched(ctx);

            if (!anyItems && spawned == 0)
            {
                // AS3 `itsEmpty` (Interact.as:2020-2023) — reported, not silent.
                Debug.Log($"[ContainerPropPresenter] '{ResolvedCont}' yielded nothing at " +
                          $"{transform.position.x:0.##},{transform.position.y:0.##} (itsEmpty).");
            }

            // AS3 setAct("loot", 1): saveLoot = 1, active = false, cont = "empty".
            //
            // `active = false` is deliberately NOT ported here. In the port `ObjectInstance.isActive`
            // is also the renderer's visibility flag (RoomObjectVisualManager.UpdatePresenter sets
            // `renderer.enabled = ... && obj.isActive`), so writing it would make the opened crate
            // vanish instead of showing its looted frame — the opposite of what AS3's
            // `setVisState("open")` does. The interaction gate is `lootState`, which is what
            // `ResolveFrameIndex` already reads for the looted sprite.
            //
            // The VALUE is 2, not 1, when an `imp` item was ejected — AS3 `setAct("loot", _loc5_)` where
            // `_loc5_` starts at 1 and becomes 2 (`Interact.as:1972`, `:1988-1990`, `:2024`). The port's
            // frame chooser treats any `lootState > 0` as "looted" (`ResolveFrameIndex`), so the two
            // values draw the same sprite today; 2 is stored because it is the oracle's value and
            // because `RoomStateSnapshot` round-trips it.
            _objectInstance.runtimeState.lootState = anyImportant ? 2 : 1;
        }

        /// <summary>
        /// Ejects the container's <b>own authored</b> <c>&lt;item&gt;</c> children — the first half of
        /// AS3 <c>Interact.loot()</c> (<c>Interact.as:1973-2000</c>).
        ///
        /// <para><b>Resolution order is <c>itemTip()</c>'s, not a guess.</b> The oracle builds
        /// <c>new Item("", id, kol)</c>, whose empty tip triggers <c>Item.itemTip()</c>
        /// (<c>Item.as:314-348</c>) — which searches <c>AllData.d.item</c>, then <c>d.weapon</c>, then
        /// <c>d.armor</c>, in that order. The port's item table already contains the armour rows
        /// (they carry <c>ItemType.Equipment</c>), so <see cref="PlayerInventory.ResolveItem"/>
        /// covers the first and third, and <see cref="PlayerInventory.ResolveWeapon"/> the second.
        /// This matters for real data: <c>instr1</c> carries <c>&lt;item id="mont" kol="2"/&gt;</c> and
        /// <c>mont</c> is a <i>weapon</i>, so an item-only lookup would drop it.</para>
        ///
        /// <para><b>The two attributes, exactly.</b> <c>@kol</c> is read as
        /// <c>int(...)</c> when present and defaults to <c>1</c> when absent (<c>:1979-1986</c>) — 41 of
        /// the 186 authored rows carry one, with values 1..112. <c>@imp</c> is a <b>presence</b> test,
        /// not a value test (<c>:1977</c>, <c>:1987</c>): the oracle stores <c>Item.imp = 2</c> whenever
        /// the attribute exists, whatever it says. 100 of the 186 carry it, and every <c>col1</c> row is
        /// one of them.</para>
        ///
        /// <para><b>The scatter is the port's stand-in, as in <see cref="SpillRolls"/>.</b> The oracle
        /// hands every ejected item the same <c>X, Y</c> and lets it fall; the port has no falling-loot
        /// model, so each item gets a small deterministic offset from the loot RNG.</para>
        /// </summary>
        /// <param name="anyImportant">
        /// Whether any ejected row carried <c>@imp</c> — AS3 <c>_loc5_ == 2</c>, which becomes the
        /// container's final <c>lootState</c>.
        /// </param>
        private int SpillAuthoredItems(GameObject user, LootContext ctx, out bool anyImportant)
        {
            anyImportant = false;

            if (_objectInstance?.items == null || _objectInstance.items.Count == 0) return 0;

            PlayerInventory inventory = user != null ? user.GetComponent<PlayerInventory>() : null;
            if (inventory == null)
            {
                Debug.LogWarning("[ContainerPropPresenter] the interactor has no PlayerInventory, so " +
                                 $"the {_objectInstance.items.Count} authored item row(s) on " +
                                 $"'{_objectInstance.objectId}' cannot be spawned.");
                return 0;
            }

            IRngService rng = ctx.Rng;
            int spawned = 0;

            for (int i = 0; i < _objectInstance.items.Count; i++)
            {
                MapObjectItemData row = _objectInstance.items[i];
                if (row == null || string.IsNullOrEmpty(row.id)) continue;

                // `_loc2_ = int(_loc6_.@kol)` when present, else 1 (`Interact.as:1979-1986`).
                string kolText = row.GetAttribute("kol", null);
                int count = string.IsNullOrEmpty(kolText) ? 1 : ParseCount(kolText, row.id);

                // `if(_loc6_.@imp.length()) { _loc3_ = 2; _loc5_ = 2; }` — a presence test. An absent
                // and an empty attribute are the same answer here, which is what
                // MapObjectItemData.GetAttribute's "" default gives.
                bool important = !string.IsNullOrEmpty(row.GetAttribute("imp", null));
                if (important) anyImportant = true;

                Vector3 where = transform.position + Scatter(rng);

                // itemTip()'s order: the item table first (which holds the armour rows too), then the
                // weapon table. `mont` is the row in the shipped data that needs the second half.
                ItemDefinition itemDefinition = inventory.ResolveItem(row.id);
                if (itemDefinition != null)
                {
                    if (WorldItemPickup.Spawn(itemDefinition, count, where, autoCollect: true) != null) spawned++;
                    continue;
                }

                WeaponDefinition weaponDefinition = inventory.ResolveWeapon(row.id);
                if (weaponDefinition != null)
                {
                    // A weapon is one instance with its own condition and magazine, so the count is 1
                    // whatever the row asked for — AS3 forces weapons to kol 1 (Item.as:176-178).
                    if (WorldItemPickup.Spawn(weaponDefinition, 1, where, autoCollect: true) != null) spawned++;
                    continue;
                }

                Debug.LogWarning($"[ContainerPropPresenter] '{_objectInstance.objectId}' carries " +
                                 $"<item id='{row.id}'/> but neither the item table nor the weapon " +
                                 "table resolves that id — it was not spawned. AS3 would build an " +
                                 "Item with a null xml and drop it silently (Item.itemTip).");
            }

            return spawned;
        }

        /// <summary>
        /// AS3 <c>int(_loc6_.@kol)</c>. A malformed value reads as <c>0</c> in the oracle, and
        /// <c>WorldItemPickup.Spawn</c> refuses a non-positive count — so the row would vanish with no
        /// more than that refusal's warning. Reported by id so the row is findable, then clamped to 1,
        /// which is what an absent attribute means anyway. No shipped row needs this: all 41
        /// <c>@kol</c> values in the room data are 1..112.
        /// </summary>
        private static int ParseCount(string text, string itemId)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
            {
                return value;
            }

            Debug.LogWarning($"[ContainerPropPresenter] <item id='{itemId}' kol='{text}'/> is not a " +
                             "positive integer; the oracle's `int()` would read 0 and spawn nothing, " +
                             "so it is clamped to 1 instead.");
            return 1;
        }

        /// <summary>
        /// Fill the room-difficulty inputs the tables scale off, from the room's
        /// <see cref="RoomDifficulty"/>, plus the two adapters the roller's remaining branches read
        /// (<c>ctx.Pers</c> and <c>ctx.Env</c>).
        ///
        /// <para><c>LocDifLevel</c> is AS3 <c>loc.locDifLevel</c>, which the port stores as
        /// <see cref="RoomDifficulty.baseDifficulty"/> — the field <c>LocationDifficultyPlanner.Apply</c>
        /// writes from <c>Land.setLocDif</c> (<c>Land.as:982-987</c>). The other three derived values
        /// (<c>lockLevel</c>, <c>mechLevel</c>, <c>weaponLevel</c>) are written by the same call, so the
        /// tables scale off real numbers rather than a hard-coded 0.</para>
        ///
        /// <para><b><c>GameStage</c>, <c>LootLimit</c> and the per-run limit store are NOT set here</b> —
        /// they are land/run scoped, so <c>LootRuntime.CreateContext</c> already filled them from the
        /// values <c>WorldBuilder.EnterLand</c> published. See <c>LootRuntime</c>.</para>
        ///
        /// <para><b><c>Pers</c> and <c>Env</c> are the D9 wiring.</b> Both were left <c>null</c> before,
        /// which is not a neutral default: with no <c>Pers</c> the <c>freel</c>/<c>barahlo</c> extra rolls
        /// never fire and money never scales by <c>capsMult × difCapsMult</c>; with no <c>Env</c> the
        /// <c>minst</c> data item, the biome arms and the <c>safe</c> bloat ambush can never be taken.
        /// The player comes from <paramref name="user"/> because that is who opened the container — the
        /// perks belong to the opener, not to the scene.</para>
        /// </summary>
        private void ApplyRoomDifficulty(LootContext ctx, GameObject user)
        {
            RoomDifficulty difficulty = _room?.difficulty;
            ctx.LocDifLevel = difficulty != null ? Mathf.RoundToInt(difficulty.baseDifficulty) : 0;
            ctx.WeaponLevel = difficulty != null ? difficulty.weaponLevel : 1f;

            // Interact.as:2013 — `this.prize ? this.allDif : 50`. A prize container passes its own lock
            // difficulty; an ordinary one passes the literal 50, not 0.
            ctx.AllDif = 50f;
            ctx.LootBroken = false;

            // The opener's perks. CharacterStats implements ILootPersFlags directly (it exists only on
            // the player — PlayerController.Awake adds it), so there is no adapter class to keep in sync.
            ctx.Pers = user != null ? user.GetComponent<PFE.Systems.RPG.CharacterStats>() : null;

            ctx.Env = new RoomLootEnvironment(
                biom: _room != null ? _room.biome : 0,
                // AS3 `loc.itemsTip` (`<options items="…">`, Location.as:512). Not imported by the port
                // yet, so the `bibl` book roll cannot be taken here; eleven authored rooms use it. This is
                // an owner-only importer change (project rule #5) and is recorded rather than faked with a
                // guess at which rooms they are.
                itemsTip: string.Empty,
                actId: LootRuntime.LandActId,
                landRnd: LootRuntime.LandRnd,
                // AS3 `param1.prob != null` — a prob room, not a grid room. The port's discriminator is a
                // non-empty probId (written only by ProbRoomBuilder; ProbTransition.AdmitsGridStep reads
                // the same field).
                hasProb: _room != null && !string.IsNullOrEmpty(_room.probId),
                lacksWeapon: weaponId => WeaponLacks(user, weaponId));
        }

        /// <summary>
        /// AS3 <c>World.w.invent.weapons[id] == null || weapons[id].variant == 0</c> — the gate on the
        /// four <c>specweap</c> unique-weapon candidates (<c>LootGen.as:745-755</c>).
        ///
        /// <para><b>A missing inventory answers <c>false</c>, deliberately.</b> The branch <i>hands out</i>
        /// the weapon when this is true, so "cannot answer" must not read as "lacks it" — that would drop
        /// <c>lsword^1</c>, <c>antidrak^1</c>, <c>quick^1</c> and <c>mlau^1</c> into the world on every
        /// roll. Returning <c>false</c> means "assume the player has it", which is the safe direction: it
        /// withholds a unique weapon rather than duplicating one. It is an assumption and is named as one
        /// here rather than silently returning a constant from the adapter.</para>
        /// </summary>
        private static bool WeaponLacks(GameObject user, string weaponId)
        {
            if (user == null || string.IsNullOrEmpty(weaponId)) return false;

            PlayerInventory inventory = user.GetComponent<PlayerInventory>();
            GameInventory gameInventory = inventory != null ? inventory.Inventory : null;
            if (gameInventory == null) return false;

            GameWeaponInstance weapon = gameInventory.GetWeapon(weaponId);
            return weapon == null || weapon.Variant == 0;
        }

        /// <summary>
        /// Eject every rolled item as a world pickup near the container. Returns how many were actually
        /// spawned. A roll whose id resolves to no definition <b>in the table its tip names</b> is
        /// <b>named in a warning</b>, never dropped silently.
        /// </summary>
        private int SpillRolls(GameObject user, LootContext ctx)
        {
            PlayerInventory inventory = user != null ? user.GetComponent<PlayerInventory>() : null;
            if (inventory == null)
            {
                Debug.LogWarning("[ContainerPropPresenter] the interactor has no PlayerInventory, so " +
                                 $"'{ResolvedCont}' rolled {ctx.Results.Count} item(s) that cannot be resolved or spawned.");
                return 0;
            }

            IRngService rng = ctx.Rng;
            int spawned = 0;

            for (int i = 0; i < ctx.Results.Count; i++)
            {
                LootRoll roll = ctx.Results[i];

                // A weapon roll's id lives in ContentType.Weapon, not ContentType.Item, so the item
                // resolver answers null for it — permanently, for every weapon. Asking the right table is
                // the whole fix for the "rolled 'r32' … it was not spawned" line in the play-test log;
                // asking the wrong one loses the weapon while losing nothing visible.
                if (LootTips.IsWeaponTip(roll.Tip))
                {
                    WeaponDefinition weapon = inventory.ResolveWeapon(roll.ItemId);
                    if (weapon == null)
                    {
                        Debug.LogWarning($"[ContainerPropPresenter] '{ResolvedCont}' rolled '{roll.ItemId}' " +
                                         "(tip '" + roll.Tip + "') but no WeaponDefinition resolves it — it " +
                                         "was not spawned. The tip names the weapon table, so this id is " +
                                         "absent from ContentType.Weapon rather than from the item table.");
                        continue;
                    }

                    // A weapon is one instance with its own condition, magazine and respect state, so the
                    // count is 1 whatever the roll asked for — AS3 forces weapons to kol 1 (Item.as:176-178).
                    Vector3 weaponWhere = transform.position + Scatter(rng);
                    if (WorldItemPickup.Spawn(weapon, 1, weaponWhere, autoCollect: true) != null)
                    {
                        spawned++;
                    }

                    continue;
                }

                ItemDefinition definition = inventory.ResolveItem(roll.ItemId);
                if (definition == null)
                {
                    // An armour roll resolves here: armour rows ARE item rows (they carry
                    // ItemType.Equipment), so they were never the missing half. Reported with the id and
                    // the tip so a genuinely absent row is a visible gap, not a silently thinner table.
                    Debug.LogWarning($"[ContainerPropPresenter] '{ResolvedCont}' rolled '{roll.ItemId}' " +
                                     "(tip '" + roll.Tip + "') but no ItemDefinition resolves it — it was " +
                                     "not spawned.");
                    continue;
                }

                Vector3 where = transform.position + Scatter(rng);
                if (WorldItemPickup.Spawn(definition, roll.Quantity, where, autoCollect: true) != null)
                {
                    spawned++;
                }
            }

            return spawned;
        }

        /// <summary>
        /// A small deterministic offset for one ejected item — the stand-in for AS3's fall/bounce. Uses
        /// the injected loot RNG so a seeded test can predict it.
        /// </summary>
        private static Vector3 Scatter(IRngService rng)
        {
            if (rng == null) return Vector3.zero;
            float dx = (rng.NextFloat() * 2f - 1f) * ScatterRadius;
            float dy = (rng.NextFloat() * 2f - 1f) * ScatterRadius;
            return new Vector3(dx, dy, 0f);
        }

        /// <summary>
        /// Report any table key that matched no branch. AS3 is silent (the <c>if/else if</c> chain falls
        /// through), which is how <c>cont="*"</c> produced an empty container unnoticed for years (Q1).
        /// </summary>
        private void ReportUnmatched(LootContext ctx)
        {
            for (int i = 0; i < ctx.UnmatchedKeys.Count; i++)
            {
                Debug.LogWarning($"[ContainerPropPresenter] container key '{ctx.UnmatchedKeys[i]}' matched " +
                                 "no loot table — nothing was rolled. (AS3 is silent here; see Q1.)");
            }
        }

        private void ConfigureCollider()
        {
            if (_triggerCollider == null) return;

            if (!Admits(IsContainer, IsLooted))
            {
                // Not a container (or already empty): leave no target box, so the cursor probe cannot
                // select it and pressing E does nothing.
                _triggerCollider.enabled = false;
                return;
            }

            _triggerCollider.enabled = true;
            GetVisualBounds(out Vector2 size, out Vector2 offset);
            _triggerCollider.size = size;
            _triggerCollider.offset = offset;
        }

        private void GetVisualBounds(out Vector2 size, out Vector2 offset)
        {
            if (_renderer != null && _renderer.sprite != null)
            {
                size = (Vector2)_renderer.sprite.bounds.size;
                offset = (Vector2)_renderer.sprite.bounds.center;
                return;
            }

            float w = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.x * 0.01f) : 0.4f;
            float h = _visual != null ? Mathf.Max(0.2f, _visual.pixelSize.y * 0.01f) : 0.4f;
            size = new Vector2(w, h);
            offset = Vector2.zero;
        }
    }
}
