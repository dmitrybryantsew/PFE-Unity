using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using PFE.Core;
using PFE.Data;
using PFE.Character;
using PFE.Data.Definitions;
using PFE.Data.Definitions.Campaign;
using PFE.Entities.Player;
using PFE.Entities.Player.Rig;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Effects;
using PFE.Systems.Inventory;
using PFE.Systems.Magic;
using PFE.Systems.Map;
using PFE.Systems.Map.Generation;
using PFE.Systems.Map.Minimap;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Serialization;
using PFE.Systems.Map.Streaming;
using PFE.Systems.Physics;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PerkDefinition = PFE.Systems.RPG.Data.PerkDefinition;
using SkillDefinition = PFE.Systems.RPG.Data.SkillDefinition;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Runtime developer debug overlay and inspector for LittlePip and player character.
    /// Provides good UX with real-time editing of Skills, Perks, Weapons, Armor, and Vitals.
    /// 
    /// Toggleable via F2 key or via the Developer Console toolbar button.
    /// Self-bootstrapping at runtime with zero scene-YAML or prefab dependencies.
    /// </summary>
    [LocalOnly]
    public sealed class PlayerDebugEditorOverlay : MonoBehaviour
    {
        public static PlayerDebugEditorOverlay Instance { get; private set; }

        public bool IsOpen = false;

        private Rect _windowRect = new Rect(60, 40, 920, 660);
        private int _activeTab = 0; // 0: Pip Stats, 1: Skills, 2: Perks, 3: Weapons, 4: Armor, 5: Vitals & Presets, 6: Effects, 7: Spells, 8: Inventory, 9: Rig, 10: Spawn Unit, 11: Lands, 12: Land State, 13: Land Map

        private static readonly string[] TabNames = new string[]
        {
            "📊 Pip Stats",
            "⚡ Skills (18)",
            "🌟 Perks",
            "⚔️ Weapons",
            "🛡️ Armor",
            "❤️ Vitals & Presets",
            "☣️ Effects",
            "🔮 Spells",
            "🎒 Inventory",
            "🧪 Rig",
            "🧟 Spawn Unit",
            "🗺️ Lands",
            "🧭 Land State",
            "📍 Land Map",
            "💾 Save / Load"
        };

        /// <summary>How many tabs the strip actually ships. Exposed <b>so the strip test can assert against
        /// the real roster</b> rather than a hand-copied literal: <c>TabStripLayoutTests</c> used to hardcode
        /// the count, read "thirteen" and stayed green while the strip grew to fourteen, because nothing tied
        /// that number to <see cref="TabNames"/>. A test that cannot go red is not a test.</summary>
        public static int TabCount => TabNames.Length;

        /// <summary>Index of the campaign "Lands" tab — the debug travel map. Named because two other
        /// rules key off it (the no-player guard, and the tab-strip comment that counts it).</summary>
        private const int LandsTab = 11;

        /// <summary>Index of the "Land State" tab — the live readout of the land the player is standing in.
        /// Named for the same reason <see cref="LandsTab"/> is: the no-player guard has to exempt it.</summary>
        private const int LandStateTab = 12;

        /// <summary>Index of the "Land Map" tab — the drawn minimap of the built land. Exempt from the
        /// no-player guard for the same reason as <see cref="LandStateTab"/>: it reads the live
        /// <c>LandMap</c>, not the player, and the land being wrong is exactly when it is needed.</summary>
        private const int LandMapTab = 13;

        /// <summary>Index of the "Save / Load" tab — the quick-save slot, the checkpoint record, and the
        /// autosave timer. Exempt from the no-player guard for the same reason as
        /// <see cref="LandStateTab"/>: it reads <c>SaveManager</c> and the campaign, not the player, and a
        /// save that will not load is exactly when a scene without a live player is being looked at.</summary>
        private const int SaveLoadTab = 14;

        // Scroll positions for each tab
        private Vector2 _pipStatsScroll;
        private Vector2 _skillsScroll;
        private Vector2 _perksScroll;
        private Vector2 _weaponsScroll;
        private Vector2 _armorScroll;
        private Vector2 _presetsScroll;
        private Vector2 _effectsScroll;
        private Vector2 _spellsScroll;

        // Pip stats inspector state
        private string _inspectedStatFactor = "allDamMult";
        private static readonly string[] InspectableStats = new string[]
        {
            "allDamMult", "allVulnerMult", "skin", "dexter", "maxhp", "critCh",
            "gunsDamMult", "meleeDamMult", "repairMult", "runSpeedMult"
        };

        // Search filters
        private string _perkSearch = string.Empty;
        private int _perkFilterMode = 0; // 0: All, 1: Unlocked, 2: Locked
        private bool _ignorePerkPrereqs = true;

        private string _weaponSearch = string.Empty;

        /// <summary>
        /// Index into <see cref="WeaponCategoryLabels"/>. The weapons tab groups by **skill**, exactly as
        /// AS3's Pip-Boy does (<c>PipPageInv.as:33</c>) — not by weapon type. See
        /// <see cref="MatchesWeaponCategory"/> for the mapping and why the order looks odd.
        /// </summary>
        private int _weaponCategoryFilter = 0; // 0 = All, then AS3's six skill categories (w1,w2,w4,w5,w6,w3)

        // ── Weapon categories — AS3's split, not an invented one ──────────────
        //
        // AS3's Pip-Boy weapons tab groups weapons by **skill**, not by weapon type: PipPageInv.as:33
        // sets its six category buttons to ["","w1","w2","w4","w5","w6","w3"] — literally "w" +
        // weapon.skill — and PipPageInv.as:100 folds tele (skill 7) into the magic button (w6).
        // Skill id → name is Pers.getWeapLevel (Pers.as:1073-1103):
        //   1 melee, 2 smallguns, 3 repair, 4 energy, 5 explosives, 6 magic, 7 tele.
        //
        // The ORDER is AS3's (w1,w2,w4,w5,w6,w3), so "Repair" sits last even though its skill id (3) is
        // the third. WeaponDefinition.skillLevel already carries this id (WeaponDataImporter.cs:250) —
        // the old code ignored it and guessed from the weapon id instead, which mis-split the list.
        private static readonly string[] WeaponCategoryLabels =
            { "All", "Melee", "Small Guns", "Energy", "Explosives", "Magic", "Repair" };

        /// <summary>Button index → AS3 skill id. Index 0 is "All" (skill 0 matches every weapon).</summary>
        private static readonly int[] WeaponCategorySkill = { 0, 1, 2, 4, 5, 6, 3 };

        private string _armorSearch = string.Empty;
        private int _armorFilterMode = 0; // 0: Visual Sets (20), 1: All Apparel

        // Cached definitions
        private SkillDefinitionDatabase _skillDatabase;
        private SkillDefinition[] _allSkills;
        private PerkDefinition[] _allPerks;
        private WeaponDefinition[] _allWeapons;
        private ItemDefinition[] _allArmorItems;

        // ── Ammo type dropdown ────────────────────────────────────────────────
        //
        // Every AmmoDefinition under Resources/Ammo, indexed by ammoId. Loaded once in LoadCatalogs;
        // the dropdown only ever lists the equipped weapon's own family (1-4 entries), so this is a
        // lookup table rather than something scanned per frame.
        private Dictionary<string, AmmoDefinition> _ammoById;
        private string[] _allAmmoIds;

        /// <summary>
        /// Ammo ids → their <see cref="ItemDefinition"/> row. A separate table from
        /// <see cref="_ammoById"/> because the two types are unrelated (see
        /// <see cref="ResolveAmmoItem"/>). Built lazily on the first "Give".
        /// </summary>
        private Dictionary<string, ItemDefinition> _ammoItemsById;

        // Which family the currently-shown dropdown belongs to, and the ids in it. Cached because
        // IMGUI draws every frame and rebuilding the list (with its sort) per frame is pure waste.
        // Invalidated whenever the equipped weapon's resolved ammo type changes.
        private string _ammoDropdownFamily;
        private string _ammoDropdownForAmmoType;
        private string[] _ammoDropdownLabels;
        private string[] _ammoDropdownIds;
        private int _ammoDropdownIndex;

        /// <summary>
        /// The inventory this overlay reads and stocks. Since 2026-10-05 the player owns a
        /// <see cref="PlayerInventory"/> at runtime, so this is normally that live bag (adopted — see
        /// <see cref="EnsureInventory"/>); it is only a bag created here when the scene has no player
        /// inventory at all.
        /// </summary>
        private GameInventory _debugInventory;

        /// <summary>
        /// The live <see cref="PlayerInventory"/>, when the game has one. Held separately from
        /// <see cref="_debugInventory"/> because mutations must go through its command sink rather than
        /// straight into the bag — the same seam a real pickup uses.
        /// </summary>
        private PlayerInventory _liveInventory;

        /// <summary>
        /// The live player, when the scene has one. Refreshed by <see cref="DrawInventoryTab"/> on every
        /// repaint (the player is already in hand there). Needed because the placement modes below place
        /// relative to the player's <see cref="PFE.Entities.Units.UnitController.FacingDirection"/>,
        /// which <see cref="PlayerInventory"/> does not expose — "in front" is meaningless without it.
        /// <para>A destroyed player reads as <c>null</c> here (Unity's fake-null), so a respawned player
        /// falls back to the raw offset rather than silently using a dead transform.</para>
        /// </summary>
        private PlayerController _livePlayer;

        // ── Inventory tab (8) state ───────────────────────────────────────────
        //
        // The sub-tabs mirror AS3 PipPageInv's five pages, plus two the oracle has no counterpart for:
        // Spawn (create an item out of nothing and drop it in the world) and Drop (move something the
        // player already holds into the world). See InventoryPageRules for the page partition and where
        // it deliberately departs from AS3.

        /// <summary>
        /// Sub-tab index of the Drop view. Named rather than written as a literal: these two views sit
        /// after the five <see cref="InventoryPage"/> indices, so inserting a sub-tab renumbers them and
        /// a bare `_invPage == 5` would silently start drawing the wrong view.
        /// </summary>
        private const int InventorySpawnSubTab = 5;

        /// <inheritdoc cref="InventorySpawnSubTab"/>
        private const int InventoryDropSubTab = 6;

        /// <summary>0..4 = an <see cref="InventoryPage"/>; 5 = Spawn; 6 = Drop.</summary>
        private int _invPage;

        /// <summary>Scroll position for the item list.</summary>
        private Vector2 _invScroll;

        /// <summary>The "add an item by id" field, and the quantity beside it.</summary>
        private string _invAddId = string.Empty;
        private string _invAddQty = "1";

        // ── Spawn view state ──────────────────────────────────────────────────

        /// <summary>
        /// Which page the Spawn picker is filtered to. Defaults to Aid because it is the page with the
        /// most rows (and the one a tester most often wants). Only the item-backed pages are offered.
        /// </summary>
        private InventoryPage _spawnPage = InventoryPage.Aid;

        /// <summary>Free text for the Spawn picker; matches an id or a display name.</summary>
        private string _spawnSearch = string.Empty;

        /// <summary>The id selected in the Spawn picker, and how many to spawn.</summary>
        private string _spawnSelectedId = string.Empty;
        private string _spawnQty = "1";

        /// <summary>
        /// Whether "Spawn in world" marks the pickup auto-collectable (<c>WorldItemPickup.AutoCollect</c>).
        ///
        /// <para><b>Defaults ON, which is NOT the oracle's default</b> — deliberately. Two of the oracle's
        /// three construction sites pass <c>false</c> (<c>Invent.drop()</c>, room placement) and only
        /// <c>LootGen</c> passes <c>true</c>, but the port has no <c>LootGen</c> yet, so with the
        /// conservative default there would be no way to spawn a pickup that the walk-over collector
        /// accepts — the feature would be untestable through the only tool that makes loot. The toggle is
        /// the debug affordance that stands in for the missing call site, and it is labelled so the
        /// distinction stays visible.</para>
        /// </summary>
        private bool _spawnAutoCollect = true;

        private Vector2 _spawnScroll;

        // The filter result is cached because the overlay repaints every frame and a naive call would
        // re-scan and re-resolve all ~500 ids per repaint. Invalidated whenever an input changes, and by
        // the Refresh button (a mod may register content after boot).
        private List<string> _pickerCache;
        private InventoryPage _pickerCachePage;
        private string _pickerCacheSearch;
        private bool _pickerCacheValid;

        // ── Drop view state ───────────────────────────────────────────────────

        /// <summary>The id selected in the Drop view's "from your inventory" picker, and its amount.</summary>
        private string _dropSelectedId = string.Empty;
        private string _dropSelectQty = "1";

        /// <summary>The drop view's by-id fallback, and the X/Y offset from the player.</summary>
        private string _dropId = string.Empty;
        private string _dropQty = "1";
        private string _dropOffsetX = "0";
        private string _dropOffsetY = "-0.5";

        /// <summary>Scroll positions: the held-item picker, and the on-the-ground pickup list.</summary>
        private Vector2 _dropHeldScroll;
        private Vector2 _pickupScroll;

        // ── Placement (shared by the Spawn and Drop views) ────────────────────
        //
        // "Where does it land" is one question, so it is one control rendered in both views rather than
        // two that can disagree. The three facing-relative anchors are resolved at SPAWN TIME from the
        // player's live facing, deliberately not baked into a text field: a stored sign would go stale
        // the moment the player turned around, which is exactly when a tester is checking both sides.

        /// <summary>
        /// Where a spawn/drop lands. <see cref="Custom"/> falls back to the editable X/Y offset fields
        /// (the pre-existing behaviour); the other three are derived from the player's facing.
        /// </summary>
        private enum PlacementAnchor
        {
            /// <summary>At the player's own feet — the oracle's <c>owner.X, owner.Y</c>.</summary>
            AtFeet,

            /// <summary>One <c>distance</c> along the player's facing.</summary>
            InFront,

            /// <summary>One <c>distance</c> against the player's facing.</summary>
            Behind,

            /// <summary>Raw X/Y offset from the player — arbitrary placement, the escape hatch.</summary>
            Custom
        }

        /// <summary>
        /// Defaults to <see cref="PlacementAnchor.InFront"/> because the whole point of the control is
        /// testing collection: standing on the item you just made (<c>AtFeet</c>) is a poor way to find
        /// out whether the cursor probe reaches it.
        /// </summary>
        private PlacementAnchor _placementAnchor = PlacementAnchor.InFront;

        /// <summary>
        /// Distance in world units for the facing-relative anchors. Default 1.5: inside
        /// <c>WorldConstants.ACTION_REACH</c> (2.0), so the item is collectable without walking, but far
        /// enough that it is not under the player's own collider.
        /// </summary>
        private string _placementDistance = "1.5";

        /// <summary>The last thing the tab did, so a rejection is visible where the action was taken.</summary>
        private string _invStatus = string.Empty;
        private bool _invStatusIsError;

        // ── Deferred mutations ────────────────────────────────────────────────
        //
        // IMGUI hands a button's click back IN THE MIDDLE of the loop that drew its row, so a button that
        // mutates a collection it is being drawn from breaks the enumeration it is inside. Two live
        // examples, both real crashes/bugs rather than theory:
        //
        //   * the Aid/Misc/Ammo pages draw `foreach (var kvp in inventory.Items)` and the row's "−1"/"+1"/
        //     "Drop 1" buttons mutate that same dictionary → `InvalidOperationException: Collection was
        //     modified` on the next MoveNext;
        //   * the Drop view's held list does the same with "Drop 1", and its on-the-ground list walks
        //     `WorldItemPickup.All` BY INDEX while "Take"/"Destroy" remove from that list → the loop
        //     silently skips the next row.
        //
        // The rule this enforces: **a mutating button drawn inside a loop must defer, and the mutation
        // runs after the pass that found it.** Buttons outside every loop (the add row, Spawn's actions,
        // the by-id drop, Clear all) stay immediate — they are provably safe, and keeping them immediate
        // keeps their validation and status message inline where the user typed.
        //
        // At most one click is delivered per pass, so a single slot cannot lose an action.

        private enum PendingInventoryAction { None, AddItem, RemoveItem, DropItem, TakePickup, DestroyPickup }

        private PendingInventoryAction _pendingInventoryAction;
        private string _pendingItemId = string.Empty;
        private int _pendingQuantity;
        private WorldItemPickup _pendingPickup;

        // Known 20 visual sets with sprites in PlayerAnimationDefinition
        private static readonly string[] VisualArmorIds = new string[]
        {
            "pip", "tre", "chitin", "kombu", "skin", "metal", "assault", "battle",
            "magus", "antirad", "antihim", "intel", "astealth", "moon", "sapper",
            "power", "polic", "spec", "encl", "ali"
        };

        // ── Effects tab (7) ───────────────────────────────────────────────────
        //
        // The tab drives the SAME runtime path a real hit does (`ActiveEffectSet.AddEffect` /
        // `RemoveEffect`), not a parallel debug model. That is the point: what it proves is that the
        // effect system on a live unit reacts, not that a debug button labels a row. A "target
        // selector" can point at the player or at another unit in the scene, because AS3 puts
        // `effects` on the base `Unit` (`Unit.as:494`) and both the player and every NPC own one.

        /// <summary>0 = the player, otherwise 1-based index into <see cref="_effectTargets"/>.</summary>
        private int _effectTargetIndex;

        /// <summary>Every live <c>UnitController</c> in the scene — rebuilt once per drawn frame.</summary>
        private UnitController[] _effectTargets;

        /// <summary>Every effect definition under <c>Resources/Effects</c>, sorted, for the picker.</summary>
        private EffectDefinition[] _allEffects;

        /// <summary>Free-text filter over <see cref="_allEffects"/> — 79 rows is too many to scroll.</summary>
        private string _effectSearch = string.Empty;

        /// <summary>Every <c>tip</c> the currently-spawned units' live effects carry, for the picker.</summary>
        private Vector2 _effectListScroll;

        /// <summary>Duration override in seconds for the next applied effect — <c>0</c> means the
        /// definition's own <c>@t</c>. The runtime multiplies by 30 (<c>Effect.as:82</c>).</summary>
        private float _effectDurationSeconds;

        /// <summary>Value override for the next applied effect — <c>0</c> means the definition's own
        /// <c>val</c> (<c>Effect.as:87-90</c>).</summary>
        private float _effectValue;

        /// <summary>
        /// The one-click quick-apply rows, as one table.
        ///
        /// <para><b>Why a table and not seven literal call sites.</b> The rows were seven inline
        /// <c>DrawQuickEffectRow(...)</c> calls, and one of them named <c>"stunned"</c> — an id that
        /// exists in no <c>&lt;eff&gt;</c> row and in no asset. <c>AddEffect</c> refuses an id it cannot
        /// resolve, so that button applied nothing and said nothing: the exact "plausible object with
        /// no behaviour" this project keeps finding, and invisible from a green build because nothing
        /// tested the button's id against the data. With the rows in one table the draw loop and the
        /// load-time check read the same source, so they cannot disagree about what the tab offers.</para>
        ///
        /// <para><b>AS3 has no <c>stunned</c> effect.</b> <c>stun</c> is a plain <c>Unit.stun:int</c>
        /// counter (<c>Unit.as:390</c>), ticked down at <c>:3190-3202</c> and set by the
        /// <c>dopEffect == "stun"</c> branch (<c>:3814-3825</c>) — <c>OnHitEffectProducers</c> lists it
        /// under "not effects — plain fields the port has no home for yet". The oracle's slow/curse
        /// effect is <c>stupor</c> (<c>&lt;eff id='stupor' tip='4' t='9'&gt;</c>,
        /// <c>AllData.as:6000</c>), so the row now points at that.</para>
        /// </summary>
        private static readonly (string Id, string Label, string Note)[] QuickEffectRows =
        {
            ("burning", "🔥 Burn (fire DoT)",
                "Unit.damage's on-hit fire producer; secEffect does owner.damage(val, D_FIRE, null, true) + shok=33"),
            ("chemburn", "🧪 Acid burn",
                "On-hit acid producer; secEffect does owner.damage(val, D_ACID)"),
            ("pinkcloud", "💗 Pink cloud",
                "secEffect does owner.damage(val, D_PINK)"),
            ("drunk", "🍺 Drunk (DoT above lvl 3)",
                "The only effect carrying lvl1, so it is the only one that ESCALATES: above level 3 secEffect does D_POISON"),
            ("hydra", "💧 Hydra (heal + organ heal)",
                "secEffect heals the unit, then (player only) pers.heal(val, 4)/(val, 5) — the organ path"),
            ("stupor", "😵 Stupor (slow curse)",
                "AllData.as:6000, eff id='stupor' tip=4 t=9 — tormoz x0.25, runSpeedMult x0.5, jumpdy -3, stamRes x0. The necromancer boss's curse (UnitBossNecr.as:56). AS3's `stun` is a plain Unit.stun counter, not an effect, so it has no row."),
            ("stealth_armor", "🫥 Stealth armour (perm)",
                "forever + abil target of the 'astealth' armour row"),
        };

        /// <summary>One-shot guard for <see cref="VerifyQuickRowsResolve"/>.</summary>
        private bool _quickRowsVerified;

        // GUI Styles
        private GUIStyle _windowStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _tabActiveStyle;
        private GUIStyle _tabInactiveStyle;
        private GUIStyle _cardStyle;
        private GUIStyle _cardActiveStyle;
        private GUIStyle _badgeStyle;
        private GUIStyle _subHeaderStyle;
        private Texture2D _texDarkBg;
        private Texture2D _texCardBg;
        private Texture2D _texActiveBg;
        private Texture2D _texBadgeBg;
        private bool _stylesInitialized;

        private const KeyCode ToggleKey = KeyCode.F2;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureRuntimeInstance()
        {
            if (Instance == null && FindFirstObjectByType<PlayerDebugEditorOverlay>() == null)
            {
                var go = new GameObject("PlayerDebugEditorOverlay");
                Instance = go.AddComponent<PlayerDebugEditorOverlay>();
                DontDestroyOnLoad(go);
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(gameObject);
            }
            LoadCatalogs();
        }

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(ToggleKey))
            {
                IsOpen = !IsOpen;
            }
            else if (IsOpen && UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                // If developer console is not active, allow Esc to close editor
                if (DeveloperConsoleController.Instance == null || DeveloperConsoleController.Instance.Service == null || !DeveloperConsoleController.Instance.Service.IsOpen)
                {
                    IsOpen = false;
                }
            }
        }

        private void LoadCatalogs()
        {
            if (_skillDatabase == null)
            {
                _skillDatabase = Resources.Load<SkillDefinitionDatabase>("SkillDefinitionDatabase");
            }
            if (_skillDatabase != null)
            {
                _allSkills = _skillDatabase.GetAllSkills();
                _allPerks = _skillDatabase.GetAllPerks();
            }

            if (_allWeapons == null || _allWeapons.Length == 0)
            {
                _allWeapons = Resources.LoadAll<WeaponDefinition>("Weapons");
                if (_allWeapons != null)
                {
                    Array.Sort(_allWeapons, (a, b) => string.Compare(a.weaponId, b.weaponId, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (_allArmorItems == null || _allArmorItems.Length == 0)
            {
                // Two sources, because armour lives in its OWN folder. The importer
                // (`PFE/Data/Import Armour from AllData.as`) writes the real `<armor>` definitions —
                // ratings, resists and the level table — to `Resources/Armor/`, NOT `Resources/Items/`.
                // Scanning `Items/` alone is what made every plate resolve to null and fall into the
                // empty runtime wrapper, so the stats panel never moved.
                var importedArmor = Resources.LoadAll<ItemDefinition>("Armor");
                var allItems = Resources.LoadAll<ItemDefinition>("Items");

                _allArmorItems = (importedArmor ?? Array.Empty<ItemDefinition>())
                    .Concat((allItems ?? Array.Empty<ItemDefinition>()).Where(it =>
                        it != null &&
                        (it.inventoryCategory == InventoryCategory.Apparel ||
                         it.armorTip > 0 ||
                         it.armorHP > 0 ||
                         VisualArmorIds.Contains(it.itemId, StringComparer.OrdinalIgnoreCase))))
                    .Where(it => it != null)
                    .ToArray();
            }

            // Every ammo row, indexed by id. The dropdown resolves a weapon's ammo type through this
            // dictionary rather than the content registry: the overlay is a debug tool that must work
            // even when the registry failed to initialise, and LoadCatalogs already uses Resources
            // directly for the same reason (see the class comment).
            if (_ammoById == null)
            {
                var allAmmo = Resources.LoadAll<AmmoDefinition>("Ammo");
                _ammoById = new Dictionary<string, AmmoDefinition>(StringComparer.Ordinal);
                foreach (var a in allAmmo)
                {
                    if (a == null) continue;

                    // Keyed by ammoId, falling back to the asset name. The data always sets ammoId, but an
                    // unset field would otherwise make the row unreachable and read as "no ammo types".
                    string key = !string.IsNullOrEmpty(a.ammoId) ? a.ammoId : a.name;
                    if (!string.IsNullOrEmpty(key) && !_ammoById.ContainsKey(key))
                        _ammoById.Add(key, a);
                }

                _allAmmoIds = _ammoById.Keys.ToArray();
                Array.Sort(_allAmmoIds, StringComparer.Ordinal);
            }

            // Every effect definition, for the Effects tab's picker. Loaded straight from Resources
            // rather than through the content registry for the same reason the ammo table is: the
            // overlay is a debug tool that must work even when the registry failed to initialise, and
            // the registry is what the runtime resolver wraps — so a row present here and absent there
            // is itself the diagnostic ("the asset exists but the resolver cannot see it").
            if (_allEffects == null || _allEffects.Length == 0)
            {
                _allEffects = Resources.LoadAll<EffectDefinition>("Effects");
                if (_allEffects != null)
                {
                    _allEffects = _allEffects
                        .Where(e => e != null && !string.IsNullOrEmpty(e.effectId))
                        .OrderBy(e => e.effectId, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }

            // Every unit asset, grouped for the Spawn Unit tab's two dropdowns. Built once, from
            // Resources rather than from the content registry, for the same reason the ammo table and the
            // effect catalogue above are: the overlay is a debug tool that must work precisely when the
            // registry failed to initialise, and a row present here but absent there is itself the
            // diagnostic.
            //
            // An asset with no `id` is counted rather than dropped silently: the spawner resolves a unit
            // by id, so such an asset is genuinely unspawnable — but "invisible in the picker" has to be
            // a stated fact, and the tab prints the count.
            if (_unitSpawnGroups == null)
            {
                var unitAssets = Resources.LoadAll<UnitDefinition>("Units");
                var unitIds = new List<string>(unitAssets != null ? unitAssets.Length : 0);
                _unitDefsById = new Dictionary<string, UnitDefinition>(StringComparer.OrdinalIgnoreCase);
                _unitAssetsWithoutId = 0;

                if (unitAssets != null)
                {
                    foreach (var unit in unitAssets)
                    {
                        if (unit == null) continue;
                        if (string.IsNullOrWhiteSpace(unit.id))
                        {
                            _unitAssetsWithoutId++;
                            continue;
                        }

                        unitIds.Add(unit.id);
                        if (!_unitDefsById.ContainsKey(unit.id)) _unitDefsById.Add(unit.id, unit);
                    }
                }

                _unitSpawnGroups = UnitSpawnCatalog.Build(unitIds);
                _unitVisibleGroups = null;
            }

            // The catalogue is what the quick rows are checked against, so the check belongs here —
            // once, after the load, not on a button click.
            VerifyQuickRowsResolve();
        }

        /// <summary>
        /// Resolve an ammo id to its definition, for <see cref="AmmoFamilyResolver"/>. Returns null for
        /// an unknown id so the resolver can fall back to the naming convention instead of guessing.
        /// </summary>
        private AmmoDefinition ResolveAmmo(string ammoId)
        {
            if (_ammoById == null || string.IsNullOrEmpty(ammoId)) return null;
            return _ammoById.TryGetValue(ammoId, out var def) ? def : null;
        }

        /// <summary>
        /// Resolve an ammo id to the <see cref="ItemDefinition"/> row that carries it in the inventory.
        ///
        /// <para><b>Why this is a second lookup and not a cast.</b> <see cref="AmmoDefinition"/> and
        /// <see cref="ItemDefinition"/> are unrelated types — both derive from <c>ScriptableObject</c>
        /// directly — so an ammo row is <i>not</i> an item row. The importer writes a separate
        /// <c>ItemDefinition</c> per ammo id under <c>Resources/Items</c> (verified: <c>Items/acid.asset</c>
        /// exists with <c>m_EditorClassIdentifier: …ItemDefinition</c>), and that is the row
        /// <c>GameInventory.AddItem</c> keys on.</para>
        ///
        /// <para>Loaded lazily on first use rather than in <see cref="LoadCatalogs"/>, because the overlay's
        /// catalogs are weapon/armour shaped and this one is only needed once "Give" is actually pressed —
        /// 451 item rows is real work to do on every overlay Awake.</para>
        /// </summary>
        private ItemDefinition ResolveAmmoItem(string ammoId)
        {
            if (string.IsNullOrEmpty(ammoId)) return null;

            if (_ammoItemsById == null)
            {
                _ammoItemsById = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
                foreach (var it in Resources.LoadAll<ItemDefinition>("Items"))
                {
                    if (it == null || string.IsNullOrEmpty(it.itemId)) continue;
                    if (!_ammoItemsById.ContainsKey(it.itemId))
                        _ammoItemsById.Add(it.itemId, it);
                }
            }

            return _ammoItemsById.TryGetValue(ammoId, out var found) ? found : null;
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _texDarkBg = MakeTex(2, 2, new Color(0.11f, 0.12f, 0.14f, 0.98f));
            _texCardBg = MakeTex(2, 2, new Color(0.16f, 0.18f, 0.22f, 0.92f));
            _texActiveBg = MakeTex(2, 2, new Color(0.18f, 0.38f, 0.58f, 0.95f));
            _texBadgeBg = MakeTex(2, 2, new Color(0.24f, 0.28f, 0.34f, 0.95f));

            _windowStyle = new GUIStyle(GUI.skin.window)
            {
                normal = { background = _texDarkBg, textColor = Color.white },
                onNormal = { background = _texDarkBg, textColor = Color.white },
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(10, 10, 24, 10)
            };

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.4f, 0.85f, 1f) }
            };

            _subHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.8f, 0.3f) }
            };

            _tabActiveStyle = new GUIStyle(GUI.skin.button)
            {
                normal = { background = _texActiveBg, textColor = Color.white },
                hover = { background = _texActiveBg, textColor = Color.white },
                fontStyle = FontStyle.Bold,
                fontSize = 12
            };

            _tabInactiveStyle = new GUIStyle(GUI.skin.button)
            {
                normal = { background = _texCardBg, textColor = new Color(0.75f, 0.78f, 0.82f) },
                hover = { background = _texBadgeBg, textColor = Color.white },
                fontStyle = FontStyle.Normal,
                fontSize = 12
            };

            _cardStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _texCardBg, textColor = Color.white },
                padding = new RectOffset(8, 8, 6, 6)
            };

            _cardActiveStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _texActiveBg, textColor = Color.white },
                padding = new RectOffset(8, 8, 6, 6)
            };

            // The dropdown rows are the ONE place in this overlay where a control is NOT given an explicit
            // height, and this is why: a GUIStyle paints its text inside `padding`, so a row must be at
            // least `font.lineHeight + padding.vertical`. The rows were drawn at a hardcoded 20 px against
            // a 12 px-padded card style, which left 8 px for a ~15 px glyph — every row clipped its text
            // top and bottom, and nothing on screen said so. Copying the card style with tighter padding
            // and letting the style size itself makes that drift impossible.
            _unitRowStyle = new GUIStyle(_cardStyle)
            {
                padding = new RectOffset(8, 8, UnitRowPadY, UnitRowPadY)
            };

            _unitRowActiveStyle = new GUIStyle(_cardActiveStyle)
            {
                padding = new RectOffset(8, 8, UnitRowPadY, UnitRowPadY)
            };

            _badgeStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _texBadgeBg, textColor = new Color(0.9f, 0.95f, 1f) },
                fontSize = 11,
                padding = new RectOffset(5, 5, 2, 2)
            };

            _stylesInitialized = true;
        }

        private Texture2D MakeTex(int width, int height, Color col)
        {
            Color[] pix = new Color[width * height];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            Texture2D result = new Texture2D(width, height);
            result.SetPixels(pix);
            result.Apply();
            return result;
        }

        private void OnGUI()
        {
            if (!IsOpen)
            {
                // Small subtle open button at top right
                if (GUI.Button(new Rect(Screen.width - 220, 5, 105, 22), "Player Edit (F2)"))
                {
                    IsOpen = true;
                }
                return;
            }

            InitStyles();

            // Intercept mouse clicks inside window so player doesn't attack/interact during click
            Vector2 mousePos = new Vector2(UnityEngine.Input.mousePosition.x, Screen.height - UnityEngine.Input.mousePosition.y);
            if (_windowRect.Contains(mousePos))
            {
                if (Event.current.type == EventType.MouseDown || Event.current.type == EventType.ScrollWheel)
                {
                    // Mark as consumed
                }
            }

            _windowRect = GUI.Window(884411, _windowRect, DrawWindow, "PFE LittlePip Debug Editor & Loadout Inspector [F2 to Close]", _windowStyle);
        }

        private PlayerController GetPlayer()
        {
            return FindFirstObjectByType<PlayerController>();
        }

        private void DrawWindow(int windowId)
        {
            PlayerController player = GetPlayer();

            // ── Top Header / Player Quick Status ─────────────────────────────────
            GUILayout.BeginHorizontal();
            if (player != null)
            {
                var stats = player.CharacterStats;
                var unitStats = player.Stats;
                var loadout = player.GetComponent<PlayerWeaponLoadout>();

                string weaponName = loadout?.Current != null ? loadout.Current.State.Def.weaponId : "Unarmed";
                string armorName = unitStats != null && !string.IsNullOrEmpty(unitStats.ArmourId.Value) ? unitStats.ArmourId.Value : "None";
                float hp = unitStats != null ? unitStats.CurrentHp.Value : 0;
                float maxHp = unitStats != null ? unitStats.MaxHp.Value : 100;
                float mana = stats != null ? stats.manaHp : 0;
                float maxMana = stats != null ? stats.MaxMana : 100;

                GUILayout.Label($"<b>Level:</b> {stats?.Level ?? 1}  |  <b>HP:</b> {hp:0}/{maxHp:0}  |  <b>Mana:</b> {mana:0}/{maxMana:0}  |  <b>Weapon:</b> {weaponName}  |  <b>Armor:</b> {armorName}", GUILayout.ExpandWidth(true));
            }
            else
            {
                GUILayout.Label("<color=#FF6666><b>[No Player in Scene]</b> Load a gameplay scene (SampleScene) to edit.</color>", GUILayout.ExpandWidth(true));
            }

            if (GUILayout.Button("✕ Close (F2)", GUILayout.Width(95), GUILayout.Height(22)))
            {
                IsOpen = false;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // ── Tab Bar ──────────────────────────────────────────────────────────
            //
            // WRAPS, because a horizontal strip does not. This is the defect the developer console's
            // quick-action buttons already hit: a fixed strip runs off the right edge and the last
            // buttons become unclickable, with nothing on screen to say so. Ten tabs already sat within
            // a few tens of pixels of this window's 920, so the eleventh — the unit spawn tab — was
            // exactly the one pushed past the edge, and the twelfth (Lands) would have gone the same way.
            // A wrapped strip cannot overflow at any window size or font — but only if the wrap budget is
            // the quantity GUILayout really advances by; see DrawTabBar, where an assumed per-button
            // spacing was the remaining few pixels of overrun.
            DrawTabBar();

            GUILayout.Space(8);

            // ── Active Tab View ──────────────────────────────────────────────────
            // The Rig tab is deliberately exempt from this guard: its whole purpose is to BUILD a
            // player, so gating it on an existing player would make it unreachable in exactly the
            // empty scene it exists for.
            //
            // The Spawn Unit tab (10) is deliberately NOT exempt: every one of its placement anchors is
            // resolved against the player's live position and facing, so with no player it would have to
            // fall back to the world origin — a spawn point that looks like it worked and is in the
            // wrong place. The refusal is the honest answer.
            //
            // The Lands tab is exempt for the Rig tab's reason, one layer up: it rebuilds the WORLD, and
            // reads nothing at all from the player. A scene whose player failed to spawn is exactly when
            // you want to travel somewhere else, so gating it would remove the one control that can get
            // you out.
            //
            // The Land State tab (12) is exempt for the same reason again: it reads the campaign's
            // per-land runtime state and the built LandMap, and every control on it is a way to change
            // what the NEXT build does. Gating it on a player would hide it in precisely the scene where
            // the land is wrong.
            //
            // The Land Map tab (13) is exempt for that reason once more: it draws the live LandMap, and
            // the player marker is optional — the map itself is the answer.
            //
            // The Save / Load tab (14) is exempt because it reads SaveManager and the campaign's
            // checkpoint record, neither of which is the player. It is also the tab you want when a load
            // came back wrong, and a load that came back wrong is a plausible reason there is no live
            // player to gate on.
            if (player == null && _activeTab != 9 && _activeTab != LandsTab &&
                _activeTab != LandStateTab && _activeTab != LandMapTab && _activeTab != SaveLoadTab)
            {
                GUILayout.Box("PlayerController not found in scene. Please enter a gameplay room/scene.", _cardStyle, GUILayout.ExpandHeight(true));
                GUI.DragWindow(new Rect(0, 0, _windowRect.width, 30));
                return;
            }

            switch (_activeTab)
            {
                case 0:
                    DrawPipStatsTab(player);
                    break;
                case 1:
                    DrawSkillsTab(player);
                    break;
                case 2:
                    DrawPerksTab(player);
                    break;
                case 3:
                    DrawWeaponsTab(player);
                    break;
                case 4:
                    DrawArmorTab(player);
                    break;
                case 5:
                    DrawVitalsPresetsTab(player);
                    break;
                case 6:
                    DrawEffectsTab(player);
                    break;
                case 7:
                    DrawSpellsTab(player);
                    break;
                case 8:
                    DrawInventoryTab(player);
                    break;
                case 9:
                    DrawRigTab(player);
                    break;
                case 10:
                    DrawSpawnUnitTab(player);
                    break;
                case LandsTab:
                    DrawLandsTab();
                    break;
                case LandStateTab:
                    DrawLandStateTab();
                    break;
                case LandMapTab:
                    DrawLandMapTab(player);
                    break;
                case SaveLoadTab:
                    DrawSaveLoadTab();
                    break;
            }

            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 24));
        }

        // =========================================================================
        // TAB BAR (wrapping)
        // =========================================================================

        /// <summary>
        /// The tab strip, wrapped into as many rows as the window width needs.
        /// </summary>
        /// <remarks>
        /// <para><b>Measured, not guessed — twice over, and the second one was the bug.</b> Each tab's
        /// width comes from <c>GUIStyle.CalcSize</c> on its own caption (which already includes the
        /// button's padding and border), so a longer label or a larger font wraps sooner instead of
        /// clipping. GUILayout has no wrapping flow, so the rows are driven explicitly: accumulate widths
        /// and start a new row when the next tab would pass the right edge.</para>
        ///
        /// <para><b>What was wrong: the per-tab advance.</b> The budget added a hard-coded
        /// <c>4f</c> per button as "GUILayout's inter-element spacing". GUILayout does not use a constant
        /// — it advances by the <i>style's own</i> <c>margin</c>, which is what
        /// <c>CalcSize(...).y + style.margin.vertical</c> already reads for the unit picker's rows
        /// (<c>DrawUnitPicker</c>). With eleven tabs the difference accumulates across ten gaps, so the
        /// strip overran the window by roughly that much and the <b>last</b> tab — the newest one — sat
        /// on the edge. That is the defect, and it is why the eleventh tab is the one that showed it.</para>
        ///
        /// <para><b>What was wrong: the available width.</b> It was <c>_windowRect.width</c> minus twice a
        /// hard-coded inset, i.e. derived from the window rect on the assumption that the content area is
        /// inset by exactly the style's padding. That assumption is about <c>GUI.Window</c>'s own
        /// client-rect arithmetic, which this file does not control.
        /// <see cref="MeasureContentWidth"/> asks IMGUI for the real number instead, so the wrap is exact
        /// at any window size, any skin and any padding.</para>
        ///
        /// <para><b>The <c>used &gt; 0</c> guard</b> keeps a single tab wider than the whole strip on its
        /// own row rather than wrapping before every tab.</para>
        /// </remarks>
        private void DrawTabBar()
        {
            float available = MeasureContentWidth();

            // Per-tab widths, measured off the styles that actually draw them. Both styles are used
            // because the active tab is bold and therefore wider; measuring only the inactive one would
            // under-budget the row that happens to hold the active tab.
            var widths = new float[TabNames.Length];
            for (int i = 0; i < TabNames.Length; i++)
            {
                GUIStyle style = (i == _activeTab) ? _tabActiveStyle : _tabInactiveStyle;
                widths[i] = TabStripLayout.TabWidth(style.CalcSize(new GUIContent(TabNames[i])).x, style.margin.horizontal);
            }

            int[] rowStarts = TabStripLayout.RowStarts(widths, available);

            int row = 0;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < TabNames.Length; i++)
            {
                if (row + 1 < rowStarts.Length && rowStarts[row + 1] == i)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.Space(2f);
                    GUILayout.BeginHorizontal();
                    row++;
                }

                GUIStyle style = (i == _activeTab) ? _tabActiveStyle : _tabInactiveStyle;
                if (GUILayout.Button(TabNames[i], style, GUILayout.Height(28), GUILayout.Width(widths[i])))
                {
                    _activeTab = i;
                }
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// The width IMGUI will actually give a row inside this window's content area, measured from the
        /// layout engine rather than derived from <c>_windowRect</c>.
        /// </summary>
        /// <remarks>
        /// A zero-height, expand-width probe in a throw-away horizontal group. Its width is the enclosing
        /// vertical group's content width — i.e. the window rect minus whatever <c>GUI.Window</c> really
        /// insets by (padding <i>and</i> border), which is the quantity the old
        /// <c>_windowRect.width - 2 * TabBarInset</c> was guessing at. Zero height, so it costs no visible
        /// space; deterministic per frame, so it is the same during Layout and Repaint.
        ///
        /// <para>Falls back to <c>_windowRect</c> minus the style padding when the probe returns nothing
        /// usable — a degenerate style must not make the strip un-renderable, and an over-wide budget
        /// only degrades to the old behaviour rather than to an empty strip.</para>
        /// </remarks>
        private float MeasureContentWidth()
        {
            GUILayout.BeginHorizontal();
            Rect probe = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.Height(0f));
            GUILayout.EndHorizontal();

            if (probe.width > 1f)
            {
                return probe.width;
            }

            return Mathf.Max(1f, _windowRect.width - _windowStyle.padding.horizontal);
        }

        // =========================================================================
        // TAB 9: CODE-BUILT PLAYER RIG
        // =========================================================================
        //
        // Builds the player from C# instead of Player.prefab — as a REAL object in the scene, beside
        // the prefab player — and pulls its state back here. The tab is a reader, not an owner.
        //
        // The context is sourced entirely from the live player, which is what makes this a fair
        // probe: the rig gets the same tile query and the same avatar data the prefab player has, so
        // a divergence the diff reports is a divergence in the BUILD, not in the inputs.

        private GameObject _rigRoot;
        private Vector2 _rigScroll;
        private List<RigDivergence> _rigDivergences;
        private int _rigTunableCount;
        private string _rigStatus = "No rig. Press Build.";

        private void DrawRigTab(PlayerController player)
        {
            _rigScroll = GUILayout.BeginScrollView(_rigScroll);

            GUILayout.Label("<b>Code-built player rig</b> — constructed from C#, tuned by value from the prefab player.", GUILayout.ExpandWidth(true));
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Build rig", GUILayout.Height(24), GUILayout.Width(110))) BuildRig(player);
            if (GUILayout.Button("Diff vs prefab", GUILayout.Height(24), GUILayout.Width(130))) DiffRig(player);
            if (GUILayout.Button("Destroy rig", GUILayout.Height(24), GUILayout.Width(110))) DestroyRig();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label(_rigStatus, GUILayout.ExpandWidth(true));

            // ── Live state, pulled from the rig itself ──────────────────────────
            if (_rigRoot != null)
            {
                var rig = _rigRoot.GetComponent<PlayerController>();
                var rigTile = _rigRoot.GetComponent<TilePhysicsController>();
                var rigAssembler = _rigRoot.GetComponent<CharacterSpriteAssembler>();

                float rigHp = rig != null && rig.UnitStats != null ? rig.UnitStats.CurrentHp.Value : 0f;
                float rigVelX = rigTile != null ? rigTile.VelocityX : 0f;
                string state = rigAssembler != null ? rigAssembler.CurrentState : "?";
                int frame = rigAssembler != null ? rigAssembler.CurrentFrame : -1;
                bool grounded = rigTile != null && rigTile.IsGrounded;

                GUILayout.Space(6);
                GUILayout.Label($"<b>Live rig</b>  pos=({_rigRoot.transform.position.x:0.#}, {_rigRoot.transform.position.y:0.#})  hp={rigHp:0.#}");
                GUILayout.Label($"state={state}  frame={frame}  grounded={grounded}  velX={rigVelX:0.##}");

                // The one seam whose absence is not survivable: groundedness is a tile question, so
                // without the query the rig has no floor and walks off into space.
                GUILayout.Label(rigTile != null && rigTile.TileQuery != null
                    ? "<color=#55FF55>tile query wired — the rig can stand on the map</color>"
                    : "<color=#FF6666>NO tile query — the rig will fall through the world</color>");
            }

            // ── The measured answer to "did the code build lose parity?" ─────────
            if (_rigDivergences != null)
            {
                GUILayout.Space(8);
                GUILayout.Label($"<b>Parity diff vs the prefab player</b> — {_rigDivergences.Count} divergence(s) over {_rigTunableCount} tunables", GUILayout.ExpandWidth(true));

                if (_rigDivergences.Count == 0)
                {
                    GUILayout.Label("<color=#55FF55>None. The code-built rig matches the prefab on every tunable read.</color>", GUILayout.ExpandWidth(true));
                }
                else
                {
                    foreach (RigDivergence d in _rigDivergences)
                    {
                        GUILayout.Label("<color=#FFAA33>" + d + "</color>", GUILayout.ExpandWidth(true));
                    }
                }
            }

            GUILayout.EndScrollView();
        }

        private void BuildRig(PlayerController player)
        {
            DestroyRig();

            if (player == null)
            {
                _rigStatus = "Need a prefab player in the scene to source the rig's context from.";
                return;
            }

            var liveTile = player.GetComponent<TilePhysicsController>();
            var liveAssembler = player.GetComponent<CharacterSpriteAssembler>();

            // `PlayerController.Stats` SHADOWS `UnitController.Stats` with a DIFFERENT type —
            // UnitStats on the derived, UnitDefinition on the base (which is why the header above
            // can read `player.Stats.CurrentHp`). Going through the base type is what asks the
            // question actually wanted here.
            var unitController = (UnitController)player;
            UnitDefinition definition = unitController.Stats;

            var ctx = new PlayerRigContext
            {
                // Placed beside the player, so the two can be compared by eye as well as by diff.
                SpawnPosition = player.transform.position + new Vector3(4f, 0f, 0f),
                UnitDefinition = definition,
                // A FRESH stats block, never the live player's: sharing the instance would make
                // damage to one player damage the other, which reads as a physics bug.
                UnitStats = definition != null ? new UnitStats(definition.health, 100f) : null,
                TileQuery = liveTile != null ? liveTile.TileQuery : null,
                AnimationDefinition = liveAssembler != null ? liveAssembler.Definition : null,
                StyleData = liveAssembler != null ? liveAssembler.StyleData : null,
                Appearance = liveAssembler != null ? liveAssembler.Appearance : null,
                // Without this the rig draws behind the level graphics — invisible, and silent.
                SortingLayerName = liveAssembler != null ? liveAssembler.SortingLayerName : null,
                // DamageSystem, the effect resolver, particles, the sim clock and the loadout's
                // services are NOT reachable from a scene component — on the prefab player they
                // arrive by [Inject]. Null is a documented legal state for every one of them, so the
                // rig stands, draws and is grounded; it simply cannot be hurt or shoot yet. That gap
                // is the finding, not a crash.
            };

            // Clone the prefab player's authored tunables BY VALUE, so the diff starts at zero and
            // anything it does report is a construction bug rather than a default mismatch.
            PlayerRigSnapshot tuning = PlayerRigTuning.Read(player.gameObject);

            _rigRoot = PlayerRigBuilder.Build(ctx, tuning);
            _rigStatus = "Built at " + _rigRoot.transform.position + " — compare it with the prefab player.";
        }

        private void DiffRig(PlayerController player)
        {
            if (_rigRoot == null || player == null)
            {
                _rigDivergences = null;
                _rigStatus = "Build a rig, and have a prefab player in the scene, before diffing.";
                return;
            }

            PlayerRigSnapshot prefabSide = PlayerRigTuning.Read(player.gameObject);
            PlayerRigSnapshot rigSide = PlayerRigTuning.Read(_rigRoot);

            _rigDivergences = PlayerRigSnapshot.Diff(prefabSide, rigSide);
            _rigTunableCount = rigSide.Count;
            _rigStatus = "Diffed " + rigSide.Count + " tunables.";
        }

        private void DestroyRig()
        {
            if (_rigRoot != null)
            {
                Destroy(_rigRoot);
                _rigRoot = null;
            }
            _rigDivergences = null;
        }

        private void OnDestroy()
        {
            // The overlay instantiates a real GameObject, so it has to clean it up: before this the
            // class had no OnDestroy at all, and a rig left behind would accumulate every play
            // session.
            if (_rigRoot != null) Destroy(_rigRoot);

            // The Land Map tab owns a runtime Texture2D. A texture is not garbage collected with the
            // MonoBehaviour, so without this every play session leaks one image the size of the land.
            ReleaseLandMapTexture();
        }

        // =========================================================================
        // TAB 10: SPAWN UNIT
        // =========================================================================
        //
        // Spawns a real enemy into the CURRENT room, through RoomUnitSpawner — the one producer that
        // hands a unit its damage system, sim clock, tile query, object-physics layer, effect resolver
        // and particle emitter. A tab that built its own GameObject would produce a unit that is
        // unarmoured, falls through the floor and cannot be set on fire, and every one of those
        // failures would be silent.
        //
        // Two dropdowns, because the flat list is 148 ids. "Family" is a curated grouping
        // (UnitSpawnCatalog) — fine enough that family + tier names exactly one unit, coarse enough
        // that the second list stays under ~15 rows. "Tier" is AS3's `tr`: the numeric suffix of the
        // unit id, which only means anything once the family has cut the list down.

        /// <summary>
        /// Every unit asset grouped by family, built once in <see cref="LoadCatalogs"/>.
        /// <b>From <c>Resources</c>, not the content registry</b> — for the same reason the ammo and
        /// effect catalogues are: this is a debug tool that must work precisely when the registry failed
        /// to initialise, and a row present here but absent there is itself the diagnostic.
        /// </summary>
        private List<UnitSpawnGroup> _unitSpawnGroups;

        /// <summary>
        /// <see cref="_unitSpawnGroups"/> with the non-spawnable rows removed, i.e. what the dropdowns
        /// actually show. Rebuilt only when the toggle flips, not per repaint.
        /// </summary>
        private List<UnitSpawnGroup> _unitVisibleGroups;

        /// <summary>Unit id → definition, for the per-row hp / sheet readout and for the spawn itself.</summary>
        private Dictionary<string, UnitDefinition> _unitDefsById;

        /// <summary>
        /// Assets under <c>Resources/Units</c> that carry no <c>id</c>. Counted rather than ignored: a
        /// unit with no id cannot be resolved by the spawner, so it is genuinely unspawnable — but
        /// "invisible in the picker" must be a stated fact, not a silent omission.
        /// </summary>
        private int _unitAssetsWithoutId;

        /// <summary>Selected row in each of the two dropdowns.</summary>
        private int _unitFamilyIndex;
        private int _unitVariantIndex;

        /// <summary>How many to build per press, and the "show non-spawnable" filter.</summary>
        private string _unitQty = "1";
        private bool _unitShowNonSpawnable;

        private Vector2 _unitSpawnScroll;
        private Vector2 _unitPickerScroll;

        /// <summary>
        /// Vertical padding of a dropdown row. Small on purpose: the card styles carry 6 px top and
        /// bottom, which on a single-line row is most of the row.
        /// </summary>
        private const int UnitRowPadY = 2;

        /// <summary>Tallest the expanded dropdown may get before it scrolls, in pixels.</summary>
        private const float UnitPickerMaxHeight = 300f;

        /// <summary>
        /// Row styles for the two dropdown lists, built in <see cref="InitStyles"/> from the card styles
        /// with the vertical padding tightened. See <see cref="DrawUnitDropdown"/> for why the rows are
        /// never given a height.
        /// </summary>
        private GUIStyle _unitRowStyle;
        private GUIStyle _unitRowActiveStyle;

        /// <summary>
        /// Which picker is expanded: 0 = family, 1 = variant, -1 = neither. One at a time, so the two
        /// lists can never both push the rest of the tab down.
        /// </summary>
        private int _unitOpenPicker = -1;

        private string _unitSpawnStatus = string.Empty;
        private bool _unitSpawnStatusIsError;

        /// <summary>
        /// The records this tab created, so its remove buttons can only ever touch those. Emptying
        /// whatever happens to be in <c>room.units</c> would delete the room's authored enemies.
        /// </summary>
        private readonly List<UnitInstance> _unitSpawnedByTab = new List<UnitInstance>();

        /// <summary>
        /// The room <see cref="_unitSpawnedByTab"/> belongs to. A room change invalidates the tracked
        /// records, so the list is cleared rather than left pointing at another room's units.
        /// </summary>
        private RoomInstance _unitSpawnTrackedRoom;

        /// <summary>
        /// Monotonic counter for the debug entity ids. It makes a debug spawn's id distinct from every
        /// authored one by construction (<c>room:debug:&lt;id&gt;:NNN</c>), instead of guessing at an
        /// index that the room's own spawn pass also uses.
        /// </summary>
        private int _unitSpawnSerial;

        // ── Lands tab (11) state ──────────────────────────────────────────────
        //
        // This tab is the debug stand-in for the travel-map PAGE, which does not exist yet — the camp's
        // wall map publishes TravelMapOpenedMessage and nothing consumes it. It is deliberately built on
        // the real rules rather than around them: the gate columns come from TravelMapModel (the port of
        // PipPageInfo's visibility/travellability filters) and the Go button goes through
        // CampaignManager.BeginMission, which is the same call the map's confirm button makes. So the tab
        // both lets a tester reach any land and tells them which lands the real page would have offered.

        private Vector2 _landsScroll;

        /// <summary>Case-insensitive filter over land id / display name / tip.</summary>
        private string _landSearch = string.Empty;

        /// <summary>
        /// AS3 <c>World.w.testMode</c>, fed to <see cref="TravelMapModel"/>. On by default because this
        /// tab's purpose is to reach a land, and a land that is invisible to the model is exactly the one
        /// a tester cannot otherwise get to. Turning it off shows what the shipped page would show.
        /// </summary>
        private bool _landTestMode = true;

        /// <summary>What the last Go/Regen press did, shown under the buttons.</summary>
        private string _landStatus = string.Empty;
        private bool _landStatusIsError;

        /// <summary>Refresh button state: the catalogue is an asset, but the gates depend on runtime state
        /// (<c>visited</c>/<c>access</c>) that changes as you play, so the model is rebuilt on demand.</summary>
        private bool _landModelStale = true;

        /// <summary>
        /// The gate model, rebuilt from the live campaign state whenever <see cref="_landModelStale"/> is
        /// set. Cached because <c>DrawLandRows</c> asks it three questions per row and the overlay
        /// repaints every frame.
        /// </summary>
        private TravelMapModel _landModel;

        /// <summary>
        /// The live room's controller. <c>MapBridge.VisualController</c> first, because that is the
        /// injected reference; the scene search is the same fallback the developer console uses.
        /// </summary>
        private static RoomVisualController ResolveRoomVisualController()
        {
            var bridge = FindFirstObjectByType<MapBridge>();
            if (bridge != null && bridge.VisualController != null)
            {
                return bridge.VisualController;
            }

            return FindFirstObjectByType<RoomVisualController>();
        }

        private void DrawSpawnUnitTab(PlayerController player)
        {
            // The shared placement row resolves "in front" from `_livePlayer`, which the Inventory tab
            // is what normally assigns. Re-assigning here is what makes this tab work when the tester
            // opens F2 straight onto it — without it, "in front" would silently mean "at the world
            // origin", which is the same class of stale-anchor bug DrawPlacementRow documents.
            if (player != null) _livePlayer = player;

            _unitSpawnScroll = GUILayout.BeginScrollView(_unitSpawnScroll);

            GUILayout.Label("<b>Spawn Unit</b> — build a real enemy into the room you are standing in.",
                GUILayout.ExpandWidth(true));
            GUILayout.Label(
                "<color=#AAAAAA><size=11>It is built by RoomUnitSpawner, exactly like an authored enemy: " +
                "same damage system, sim clock, tile query, object-physics layer, effect resolver and " +
                "particle emitter. It is appended to the room's unit list, so it survives a room rebuild, " +
                "and it draws nothing from the room's spawn RNG — a debug spawn cannot shift the seeded " +
                "world.</size></color>",
                GUILayout.ExpandWidth(true));

            GUILayout.Space(6);

            RoomVisualController room = ResolveRoomVisualController();
            RoomInstance roomInstance = room != null ? room.RoomInstance : null;

            // A room change invalidates the tracked records.
            if (!ReferenceEquals(roomInstance, _unitSpawnTrackedRoom))
            {
                _unitSpawnTrackedRoom = roomInstance;
                _unitSpawnedByTab.Clear();
            }

            DrawUnitSpawnRoomLine(room, roomInstance);
            GUILayout.Space(6);

            if (room == null || roomInstance == null)
            {
                GUILayout.Box(
                    "No live room. The room controller exists only once a gameplay room has been built, " +
                    "so enter a room (SampleScene) before spawning a unit.",
                    _cardStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndScrollView();
                return;
            }

            if (_unitSpawnGroups == null || _unitSpawnGroups.Count == 0)
            {
                // Two causes, named separately: an empty catalogue is not a broken filter.
                GUILayout.Box(
                    "The unit catalogue is empty — Resources/Units returned nothing, so this is a data " +
                    "or import problem, not a filter one. Run PFE/Data/Import Units.",
                    _cardStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndScrollView();
                return;
            }

            EnsureVisibleUnitGroups();

            DrawUnitSpawnPickers();
            GUILayout.Space(6);

            DrawPlacementRow();

            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label("<color=#AAAAAA>Qty</color>", GUILayout.Width(30));
            _unitQty = GUILayout.TextField(_unitQty ?? "1", GUILayout.Width(44));
            if (GUILayout.Button("Spawn", _tabActiveStyle, GUILayout.Height(24), GUILayout.Width(110)))
            {
                SpawnSelectedUnits(player, room);
            }
            if (GUILayout.Button("Remove all spawned here", GUILayout.Height(24), GUILayout.Width(180)))
            {
                RemoveAllSpawnedByTab(room);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            if (!string.IsNullOrEmpty(_unitSpawnStatus))
            {
                GUILayout.Label(
                    (_unitSpawnStatusIsError ? "<color=#FF6666>" : "<color=#9AD0FF>") + _unitSpawnStatus + "</color>",
                    GUILayout.ExpandWidth(true));
            }

            DrawSpawnedByTabList();
            GUILayout.EndScrollView();
        }

        /// <summary>
        /// The room line: which room, how many units it has as records and as live objects, and the
        /// non-spawnable toggle.
        /// </summary>
        /// <remarks>
        /// <b>The two counts are printed separately on purpose.</b> <c>records</c> is
        /// <c>room.units.Count</c> and <c>live</c> is the spawner's own GameObject count; they disagree
        /// exactly when a spawn failed, and a single collapsed number would hide the only symptom a
        /// failed spawn has.
        /// </remarks>
        private void DrawUnitSpawnRoomLine(RoomVisualController room, RoomInstance roomInstance)
        {
            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label(
                roomInstance != null
                    ? $"<b>Room:</b> {roomInstance.id}   <b>records:</b> {room.UnitRecordCount}   <b>live:</b> {room.LiveUnitCount}"
                    : "<color=#FFAA33>no room</color>",
                GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool toggled = GUILayout.Toggle(
                _unitShowNonSpawnable,
                " show non-spawnable (" + UnitSpawnCatalog.NonSpawnableCount + " templates / NPCs / the player)",
                GUILayout.ExpandWidth(true));
            if (toggled != _unitShowNonSpawnable)
            {
                _unitShowNonSpawnable = toggled;
                _unitVisibleGroups = null;
                _unitOpenPicker = -1;
            }
            GUILayout.EndHorizontal();

            if (_unitAssetsWithoutId > 0)
            {
                GUILayout.Label(
                    $"<color=#FFAA33>{_unitAssetsWithoutId} asset(s) under Resources/Units have no `id`, so the " +
                    "spawner cannot resolve them and they are not listed. A missing id is an import bug, not " +
                    "a filter.</color>",
                    GUILayout.ExpandWidth(true));
            }
        }

        /// <summary>
        /// Rebuild the filtered family list. Called from the draw path, but it does nothing unless the
        /// cache was invalidated — the overlay repaints every frame and re-filtering 148 ids per repaint
        /// is the cost the item picker's cache exists to avoid.
        /// </summary>
        private void EnsureVisibleUnitGroups()
        {
            if (_unitVisibleGroups != null) return;

            _unitVisibleGroups = _unitShowNonSpawnable
                ? new List<UnitSpawnGroup>(_unitSpawnGroups)
                : UnitSpawnCatalog.Filter(_unitSpawnGroups, id => !UnitSpawnCatalog.IsNonSpawnable(id));

            // A family can disappear when the filter hides it, so the selection is re-clamped rather
            // than left pointing past the end of the list.
            _unitFamilyIndex = Mathf.Clamp(_unitFamilyIndex, 0, Mathf.Max(0, _unitVisibleGroups.Count - 1));
            ClampVariantIndex();
        }

        private UnitSpawnGroup SelectedUnitGroup()
        {
            if (_unitVisibleGroups == null || _unitVisibleGroups.Count == 0) return null;
            int index = Mathf.Clamp(_unitFamilyIndex, 0, _unitVisibleGroups.Count - 1);
            return _unitVisibleGroups[index];
        }

        private string SelectedUnitId()
        {
            UnitSpawnGroup group = SelectedUnitGroup();
            if (group == null || group.Count == 0) return string.Empty;
            int index = Mathf.Clamp(_unitVariantIndex, 0, group.Count - 1);
            return group.VariantIds[index];
        }

        private void ClampVariantIndex()
        {
            UnitSpawnGroup group = SelectedUnitGroup();
            _unitVariantIndex = group == null
                ? 0
                : Mathf.Clamp(_unitVariantIndex, 0, Mathf.Max(0, group.Count - 1));
        }

        /// <summary>
        /// The two dropdowns, side by side, plus the resolved-row readout.
        /// </summary>
        /// <remarks>
        /// <b>What the second dropdown says depends on the family.</b> For a single-root family the
        /// number really is the tier, so the row reads <c>Tier 3 · raider3</c>; for a folded family
        /// (<c>Robots</c> = <c>dron*</c> + <c>gutsy*</c> …) a number would identify nothing, so the id
        /// stands alone. <c>UnitSpawnCatalog.VariantLabel</c> owns that decision, which is what makes it
        /// testable.
        /// </remarks>
        private void DrawUnitSpawnPickers()
        {
            UnitSpawnGroup group = SelectedUnitGroup();
            if (group == null) return;

            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(280));
            GUILayout.Label("<color=#AAAAAA>Family (type)</color>", GUILayout.ExpandWidth(true));
            string[] familyLabels = new string[_unitVisibleGroups.Count];
            for (int i = 0; i < familyLabels.Length; i++)
            {
                familyLabels[i] = UnitSpawnCatalog.FamilyLabel(_unitVisibleGroups[i]);
            }

            int family = DrawUnitDropdown(
                familyLabels[Mathf.Clamp(_unitFamilyIndex, 0, familyLabels.Length - 1)],
                _unitFamilyIndex, familyLabels, 0);
            if (family != _unitFamilyIndex)
            {
                _unitFamilyIndex = family;
                // A new family means a new variant list; keeping the old index would silently select a
                // different unit than the one on screen a frame ago.
                _unitVariantIndex = 0;
            }
            GUILayout.EndVertical();

            GUILayout.Space(10);

            GUILayout.BeginVertical(GUILayout.Width(300));
            GUILayout.Label("<color=#AAAAAA>Variant / tier</color>", GUILayout.ExpandWidth(true));
            string[] variantLabels = new string[group.Count];
            for (int i = 0; i < variantLabels.Length; i++)
            {
                variantLabels[i] = UnitSpawnCatalog.VariantLabel(group, group.VariantIds[i]);
            }

            int variant = DrawUnitDropdown(
                variantLabels[Mathf.Clamp(_unitVariantIndex, 0, variantLabels.Length - 1)],
                _unitVariantIndex, variantLabels, 1);
            if (variant != _unitVariantIndex)
            {
                _unitVariantIndex = variant;
            }
            GUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            DrawResolvedUnitRow(SelectedUnitId());
        }

        /// <summary>
        /// The readout for the row the dropdowns currently resolve to. Every number here is the real one
        /// off the definition — a debug readout that lies is worse than none, and the sheet size is the
        /// difference between "spawns an enemy" and "spawns an invisible collider".
        /// </summary>
        private void DrawResolvedUnitRow(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return;

            UnitDefinition definition = ResolveUnitDefinition(unitId);
            if (definition == null)
            {
                GUILayout.Label(
                    $"<color=#FF6666>Resolved: {unitId} — no UnitDefinition under Resources/Units. " +
                    "The spawn would create a unit with no stats and no art.</color>",
                    GUILayout.ExpandWidth(true));
                return;
            }

            int cells = definition.spriteSheetColumns * definition.spriteSheetRows;
            string sheet = cells > 0
                ? $"sheet {definition.spriteSheetColumns}×{definition.spriteSheetRows} ({cells} cells)"
                : "<color=#FFAA33>NO SHEET — it will spawn as an invisible collider " +
                  "(run PFE/Art/Import Unit Sprites)</color>";

            GUILayout.Label(
                $"Resolved: <b>{unitId}</b>   hp {definition.health}   fraction {definition.fraction}   {sheet}",
                GUILayout.ExpandWidth(true));

            if (UnitSpawnCatalog.IsNonSpawnable(unitId))
            {
                GUILayout.Label(
                    "<color=#FFAA33>This is a family template / NPC / the player, not a spawnable enemy — " +
                    "it carries no combat block, so it will not fight back.</color>",
                    GUILayout.ExpandWidth(true));
            }
        }

        /// <summary>
        /// One in-place dropdown. Returns the newly chosen index, or <paramref name="current"/> when
        /// nothing was clicked.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the list expands in place rather than floating.</b> IMGUI has no runtime popup —
        /// <c>EditorGUILayout.Popup</c> is editor-only and this class lives in a runtime assembly with no
        /// <c>UNITY_EDITOR</c> guards. An in-place list inside a bounded scroll view is not just the
        /// workaround: it cannot be clipped by the window edge, which a floating popup can, and a
        /// dropdown whose last rows are unreachable is the failure mode this project keeps finding.</para>
        ///
        /// <para>The loop only writes to a local and to <see cref="_unitOpenPicker"/>, never to a
        /// collection it is iterating — the mutation-in-loop crash the inventory tab documents.</para>
        /// </remarks>
        private int DrawUnitDropdown(string caption, int current, IReadOnlyList<string> options, int pickerId)
        {
            bool open = _unitOpenPicker == pickerId;

            // Measured, with the previous 24 px as a floor so the two columns' headers stay level and the
            // look does not change -- but if a longer caption or a larger skin font ever needs more, the
            // header grows instead of clipping. Same reasoning as the rows below.
            GUIStyle headerStyle = open ? _tabActiveStyle : _tabInactiveStyle;
            float headerHeight = Mathf.Max(24f, headerStyle.CalcSize(new GUIContent("Ag")).y);

            if (GUILayout.Button(
                    (open ? "▾  " : "▸  ") + caption,
                    headerStyle,
                    GUILayout.Height(headerHeight), GUILayout.ExpandWidth(true)))
            {
                _unitOpenPicker = open ? -1 : pickerId;
                open = !open;
            }

            if (!open || options == null || options.Count == 0)
            {
                return current;
            }

            int chosen = current;

            // Both numbers below are MEASURED off the style that actually draws the row, never assumed.
            // `rowAdvance` is the style's natural single-line height plus its own margin, which is what
            // GUILayout will really advance by -- the old code guessed 22 px and drew 20 px rows, so the
            // viewport and the rows disagreed twice over. The cap arithmetic itself lives in
            // UnitPickerLayout so it can be asserted offline instead of only being visible on screen.
            float rowAdvance = _unitRowStyle.CalcSize(new GUIContent("Ag")).y + _unitRowStyle.margin.vertical;
            if (rowAdvance <= 1f)
            {
                rowAdvance = 22f; // a style with neither font nor padding; keeps the arithmetic finite
            }

            float viewport = UnitPickerLayout.ViewportHeight(rowAdvance, options.Count, UnitPickerMaxHeight);

            _unitPickerScroll = GUILayout.BeginScrollView(_unitPickerScroll, GUILayout.Height(viewport));

            for (int i = 0; i < options.Count; i++)
            {
                // No GUILayout.Height here: the row's own style decides, so the glyph can never be
                // clipped by a number that drifted away from the padding. `rowAdvance` above is computed
                // from that same style, so the viewport and the rows agree by construction.
                if (GUILayout.Button(options[i], i == current ? _unitRowActiveStyle : _unitRowStyle))
                {
                    chosen = i;
                    _unitOpenPicker = -1;
                }
            }

            GUILayout.EndScrollView();
            return chosen;
        }

        /// <summary>
        /// Build the selected unit <c>qty</c> times at the shared placement anchor.
        /// </summary>
        /// <remarks>
        /// <para><b>The record is authored here, not by <c>RoomPopulator.CreateUnit</c>.</b> That method
        /// is private and, more importantly, it resolves a <i>variant</i> and rolls a facing and a digger
        /// tier off the room's spawn RNG. A debug spawn must do neither: the tester picked the exact id,
        /// and drawing from that stream would shift every later placement in the seeded room. So the
        /// facing is taken from the player (the enemy faces you, which is also the useful default) and
        /// <c>digger</c> stays 0, so a spawned zombie is a plain ghoul that is visible immediately rather
        /// than a buried ambusher.</para>
        ///
        /// <para><b>The status is read back off the room, not assumed.</b> A spawn that silently failed
        /// would otherwise print success — the exact bug the inventory tab's deferred actions hit. The
        /// line prints the record and live-object counts <i>after</i> the press, so a divergence between
        /// them is visible where the action was taken.</para>
        /// </remarks>
        private void SpawnSelectedUnits(PlayerController player, RoomVisualController room)
        {
            string unitId = SelectedUnitId();
            if (string.IsNullOrEmpty(unitId))
            {
                SetUnitSpawnStatus("Nothing selected.", true);
                return;
            }

            if (room == null || room.RoomInstance == null)
            {
                SetUnitSpawnStatus("No live room to spawn into.", true);
                return;
            }

            UnitDefinition definition = ResolveUnitDefinition(unitId);
            if (definition == null)
            {
                SetUnitSpawnStatus(
                    $"No UnitDefinition for '{unitId}' — the unit would have no stats and no art. " +
                    "Run PFE/Data/Import Units.", true);
                return;
            }

            int quantity = 1;
            if (!int.TryParse(_unitQty, out quantity)) quantity = 1;
            quantity = Mathf.Clamp(quantity, 1, 20);

            int facing = player != null && player.FacingDirection != 0 ? player.FacingDirection : 1;
            Vector3 anchor = DropPosition();

            int built = 0;
            for (int i = 0; i < quantity; i++)
            {
                // Spread a batch out along the facing, or the whole stack lands inside one collider and
                // looks like a single unit.
                var worldPosition = new Vector3(anchor.x + facing * 0.8f * i, anchor.y, anchor.z);

                var unit = new UnitInstance
                {
                    entityId = PFE.Core.Ids.EntityId
                        .CreateForRoomSpawn(room.RoomInstance.id, "debug:" + unitId, _unitSpawnSerial++)
                        .ToString(),
                    unitId = unitId,
                    unitType = unitId,
                    isDead = false,
                    maxHealth = definition.health,
                    currentHealth = definition.health,
                    // Empty, so RoomUnitSpawner falls through to the definition's own controllerId and
                    // then to the id — the same precedence an authored placement with no `cl` gets.
                    controllerId = string.Empty,
                    attributes = new List<MapObjectAttributeData>(),
                    facingDirection = facing,
                    // 0 = never buries. A debug unit must be on screen the moment it is made.
                    digger = 0
                };

                if (!room.SpawnUnitNow(unit, worldPosition))
                {
                    SetUnitSpawnStatus(
                        "The room refused the spawn — it has no unit spawner yet, which means it has not " +
                        "finished initialising.", true);
                    return;
                }

                _unitSpawnedByTab.Add(unit);
                built++;
            }

            SetUnitSpawnStatus(
                $"Spawned {built} × {unitId} at ({anchor.x:0.##}, {anchor.y:0.##}). " +
                $"Room now holds {room.UnitRecordCount} record(s), {room.LiveUnitCount} live object(s).",
                false);
        }

        /// <summary>
        /// Drop every record this tab created. Only this tab's own records — the room's authored enemies
        /// are not this button's to delete.
        /// </summary>
        private void RemoveAllSpawnedByTab(RoomVisualController room)
        {
            if (_unitSpawnedByTab.Count == 0)
            {
                SetUnitSpawnStatus("Nothing spawned by this tab to remove.", false);
                return;
            }

            int removed = 0;
            for (int i = 0; i < _unitSpawnedByTab.Count; i++)
            {
                if (room != null && room.DespawnUnitNow(_unitSpawnedByTab[i])) removed++;
            }

            int asked = _unitSpawnedByTab.Count;
            _unitSpawnedByTab.Clear();

            // Report from what the seam actually did, not from the intent: a record already gone (room
            // rebuilt, unit killed and culled) is a different outcome from a removal that failed.
            SetUnitSpawnStatus(
                $"Removed {removed} of {asked} record(s) this tab created. " +
                $"Room now holds {(room != null ? room.UnitRecordCount : 0)} record(s), " +
                $"{(room != null ? room.LiveUnitCount : 0)} live object(s).",
                removed != asked);
        }

        /// <summary>
        /// The list of records this tab made. No per-row buttons: a button drawn inside the loop that
        /// drew its row mutates the collection being enumerated, which is a real
        /// <c>InvalidOperationException</c> in this codebase. The one remove action sits outside it.
        /// </summary>
        private void DrawSpawnedByTabList()
        {
            GUILayout.Space(8);
            GUILayout.Label(
                $"<b>Spawned by this tab</b> — {_unitSpawnedByTab.Count} record(s). " +
                "<color=#AAAAAA>Positions are room-local pixels, which is what the record stores.</color>",
                GUILayout.ExpandWidth(true));

            if (_unitSpawnedByTab.Count == 0)
            {
                GUILayout.Label("<color=#AAAAAA>None yet.</color>", GUILayout.ExpandWidth(true));
                return;
            }

            const int maxRows = 14;
            int rows = Mathf.Min(maxRows, _unitSpawnedByTab.Count);
            for (int i = 0; i < rows; i++)
            {
                UnitInstance unit = _unitSpawnedByTab[i];
                GUILayout.Label(
                    $"{unit.unitId}  @ ({unit.position.x:0}, {unit.position.y:0})  hp {unit.currentHealth:0.#}/{unit.maxHealth:0.#}",
                    GUILayout.ExpandWidth(true));
            }

            // A truncated list that says nothing reads as "there are 14", which is a wrong answer rather
            // than a missing one.
            if (_unitSpawnedByTab.Count > rows)
            {
                GUILayout.Label(
                    $"<color=#AAAAAA>… {_unitSpawnedByTab.Count - rows} more not listed (first {rows} shown).</color>",
                    GUILayout.ExpandWidth(true));
            }
        }

        private void SetUnitSpawnStatus(string message, bool isError)
        {
            _unitSpawnStatus = message ?? string.Empty;
            _unitSpawnStatusIsError = isError;
        }

        /// <summary>
        /// The definition for an id, from the catalogue <see cref="LoadCatalogs"/> built.
        /// </summary>
        private UnitDefinition ResolveUnitDefinition(string unitId)
        {
            if (_unitDefsById == null || string.IsNullOrEmpty(unitId)) return null;
            return _unitDefsById.TryGetValue(unitId, out UnitDefinition definition) ? definition : null;
        }

        // =========================================================================
        // =========================================================================
        // TAB 11: CAMPAIGN LANDS (the debug travel map)
        // =========================================================================

        /// <summary>
        /// Every land in the catalogue, with its travel gates, and a button that rebuilds the world there.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this exists at all.</b> Two independent things are missing from the loop: the
        /// travel-map page (nothing consumes <c>TravelMapOpenedMessage</c>) and <c>WorldBuilder</c>'s
        /// procedural routing (only 10 of the 33 lands are <c>isProcedural</c>; the rest are authored, so
        /// they can be entered today). Without this tab the only way to reach a land is to already be
        /// standing in the camp in front of a working wall map, which is the thing under test. This is a
        /// test harness, not the feature.</para>
        ///
        /// <para><b>The gates are the shipped rules, not a re-derivation.</b> <see cref="TravelMapModel"/>
        /// is the port of AS3's visibility filter (<c>PipPageInfo.as:143-205</c>) and
        /// <c>Game.checkTravel</c> (<c>Game.as:483-506</c>), and it is fed the live
        /// <see cref="CampaignManager.LandStates"/> and the live trigger table — so the three columns are
        /// what the real page would decide, computed by the code that will draw it. This tab is therefore
        /// the model's first consumer, and a wrong gate shows up here instead of in the shipped page.</para>
        ///
        /// <para><b>Both buttons are production seams.</b> <c>Go</c> is
        /// <see cref="CampaignManager.BeginMission"/> — the same call the map's confirm button makes, so a
        /// base land is entered without a rebuild and a mission land is regenerated. <c>Regen</c> is
        /// <see cref="CampaignManager.TransitionToLand"/> with <c>forceRegenerate</c>, which is what
        /// <c>gotoNextLevel</c> uses; it is the only way to see a procedural land assemble a second
        /// layout. Neither writes world state directly.</para>
        /// </remarks>
        private void DrawLandsTab()
        {
            _landsScroll = GUILayout.BeginScrollView(_landsScroll);

            GUILayout.Label("<b>Campaign Lands</b> — rebuild the world in any land in the catalogue.",
                GUILayout.ExpandWidth(true));
            GUILayout.Label(
                "<color=#AAAAAA><size=11>This is the debug stand-in for the travel map. The camp's wall map " +
                "grants travel and asks for the map to open, but no page consumes that request yet, so " +
                "pressing E on it produces nothing on screen. The gate columns below are computed by " +
                "TravelMapModel — the real rules — and Go is the same call the page's confirm button will " +
                "make.</size></color>",
                GUILayout.ExpandWidth(true));

            GUILayout.Space(6);

            CampaignManager campaign = CampaignManager.Current;
            if (campaign == null)
            {
                GUILayout.Box(
                    "No CampaignManager. It is created by the game lifetime scope, so this tab needs a " +
                    "play-mode session with the gameplay scene loaded (the same requirement as every " +
                    "other tab).",
                    _cardStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndScrollView();
                return;
            }

            DrawCampaignStateLine(campaign);
            GUILayout.Space(6);

            CampaignCatalog catalog = campaign.Catalog;
            if (catalog == null || catalog.AllLands == null || catalog.AllLands.Count == 0)
            {
                GUILayout.Box(
                    "The campaign catalogue is empty or missing (Resources/CampaignCatalog). This is a " +
                    "data problem, not a filter one — run PFE/Data/Import Campaign.",
                    _cardStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndScrollView();
                return;
            }

            DrawLandFilterRow(campaign, catalog);
            GUILayout.Space(4);

            if (!string.IsNullOrEmpty(_landStatus))
            {
                GUILayout.Label(
                    (_landStatusIsError ? "<color=#FF6666>" : "<color=#9AD0FF>") + _landStatus + "</color>",
                    GUILayout.ExpandWidth(true));
            }

            DrawLandRows(campaign, catalog);

            GUILayout.EndScrollView();
        }

        /// <summary>
        /// Where you are, whether travel is granted, and how many times the wall map has fired.
        /// </summary>
        /// <remarks>
        /// <b><c>TravelMapOpenCount</c> is the answer to "is the wall map interactable?"</b> The counter
        /// is the only observable effect the wall map has today, because the page it opens does not
        /// exist. Stand in the camp, press E on the map, and read this line: unchanged means the
        /// interaction never reached the object (presenter/collider/registration), incremented means it
        /// reached it and the missing page is the whole of the symptom.
        /// </remarks>
        private void DrawCampaignStateLine(CampaignManager campaign)
        {
            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label(
                $"<b>In land:</b> {(string.IsNullOrEmpty(campaign.CurrentLandId.CurrentValue) ? "<color=#FFAA33>(none)</color>" : campaign.CurrentLandId.CurrentValue)}" +
                $"   <b>mission:</b> {(string.IsNullOrEmpty(campaign.MissionId) ? "—" : campaign.MissionId)}" +
                $"   <b>travel granted:</b> {(campaign.TravelUnlocked ? "<color=#55FF55>yes</color>" : "<color=#FFAA33>no</color>")}" +
                $"   <b>wall-map opens:</b> <b>{campaign.TravelMapOpenCount}</b>",
                GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        /// <summary>The search box, the test-mode toggle and the refresh button.</summary>
        private void DrawLandFilterRow(CampaignManager campaign, CampaignCatalog catalog)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("<color=#AAAAAA>Find:</color>", GUILayout.Width(38));
            _landSearch = GUILayout.TextField(_landSearch ?? string.Empty, GUILayout.Width(170));

            bool toggled = GUILayout.Toggle(
                _landTestMode,
                " test mode (show lands the page would hide)",
                GUILayout.Width(300));
            if (toggled != _landTestMode)
            {
                _landTestMode = toggled;
                _landModelStale = true;
            }

            if (GUILayout.Button("Refresh gates", GUILayout.Width(120)))
            {
                _landModelStale = true;
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (_landModelStale || _landModel == null)
            {
                // Rebuilt from the live campaign state, which is what makes the columns track
                // visited/access as you play rather than freezing at whatever they were on open.
                _landModel = TravelMapModel.FromCatalog(
                    catalog,
                    campaign.LandStates,
                    campaign.GetTrigger,
                    () => campaign.CurrentLandId.CurrentValue,
                    _landTestMode);
                _landModelStale = false;
            }
        }

        /// <summary>One row per land: identity, gates, and the two travel buttons.</summary>
        private void DrawLandRows(CampaignManager campaign, CampaignCatalog catalog)
        {
            string current = campaign.CurrentLandId.CurrentValue ?? string.Empty;
            int shown = 0;

            for (int i = 0; i < catalog.AllLands.Count; i++)
            {
                LandDefinition land = catalog.AllLands[i];
                if (land == null || string.IsNullOrEmpty(land.landId)) continue;
                if (!MatchesLandFilter(land)) continue;

                shown++;

                TravelLand facts = TravelLand.From(land);
                bool isCurrent = string.Equals(land.landId, current, StringComparison.OrdinalIgnoreCase);

                GUILayout.BeginHorizontal(_cardStyle);

                GUILayout.Label(
                    (isCurrent ? "<color=#55FF55><b>▶ </b></color>" : string.Empty) +
                    $"<b>{land.landId}</b> <color=#AAAAAA>{land.DisplayName}</color>",
                    GUILayout.Width(230));

                GUILayout.Label(
                    $"tip={land.tip}  stage={land.stage}  " +
                    (land.isProcedural ? "<color=#FFAA33>procedural</color>" : "authored") +
                    $"  rooms={land.roomTemplates?.Count ?? 0}  fin={land.fin}",
                    GUILayout.Width(330));

                GUILayout.Label(LandGateText(_landModel, facts), GUILayout.Width(220));

                if (GUILayout.Button(isCurrent ? "Rebuild" : "Go", GUILayout.Width(78)))
                {
                    GoToLand(campaign, land);
                }

                if (GUILayout.Button("Regen", GUILayout.Width(66)))
                {
                    RegenerateLand(campaign, land);
                }

                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            if (shown == 0)
            {
                GUILayout.Label(
                    "<color=#FFAA33>No land matches the filter.</color> An empty list here is the filter, " +
                    "not the catalogue — clear the Find box to see all " +
                    $"{catalog.AllLands.Count} land(s).",
                    GUILayout.ExpandWidth(true));
            }
            else
            {
                GUILayout.Label($"<color=#AAAAAA>{shown} of {catalog.AllLands.Count} land(s).</color>",
                    GUILayout.ExpandWidth(true));
            }
        }

        /// <summary>
        /// The three gates, each labelled, because "not offered" and "offered but not travelable" are
        /// different states with different causes and a single greyed row would hide which one applies.
        /// </summary>
        private static string LandGateText(TravelMapModel model, TravelLand facts)
        {
            if (model == null) return "<color=#FFAA33>gates unknown</color>";

            string visible = model.IsVisible(facts)
                ? "<color=#55FF55>visible</color>"
                : "<color=#888888>hidden</color>";
            string loaded = TravelMapModel.IsLoaded(facts)
                ? "<color=#55FF55>loaded</color>"
                : "<color=#FFAA33>no rooms</color>";
            string travel = model.CanTravel(facts)
                ? "<color=#55FF55>travellable</color>"
                : "<color=#888888>not travellable</color>";

            return $"{visible} / {loaded} / {travel}";
        }

        /// <summary>
        /// Case-insensitive substring match over id, display name and tip. Blank matches everything.
        /// </summary>
        private bool MatchesLandFilter(LandDefinition land)
        {
            if (string.IsNullOrWhiteSpace(_landSearch)) return true;

            string needle = _landSearch.Trim();
            return Contains(land.landId, needle) ||
                   Contains(land.DisplayName, needle) ||
                   Contains(land.tip, needle);
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Enter the land through <see cref="CampaignManager.BeginMission"/> — the shipped path.
        /// </summary>
        private void GoToLand(CampaignManager campaign, LandDefinition land)
        {
            // Deliberately not a direct TransitionToLand: BeginMission is what the map's confirm button
            // calls, including its "a base land is not regenerated" rule, and a debug button that skipped
            // that rule would test a code path nothing ships.
            bool started = campaign.BeginMission(land.landId);
            SetLandStatus(started
                ? $"beginMission('{land.landId}') — the world rebuilds on the next frame."
                : $"beginMission('{land.landId}') did nothing: you are already there, or the id is empty.",
                !started);
        }

        /// <summary>
        /// Force a fresh layout in the land through <c>forceRegenerate</c>, which is
        /// <c>gotoNextLevel</c>'s path.
        /// </summary>
        private void RegenerateLand(CampaignManager campaign, LandDefinition land)
        {
            campaign.TransitionToLand(land.landId, null, forceRegenerate: true);
            SetLandStatus(
                $"TransitionToLand('{land.landId}', forceRegenerate: true) — a fresh layout, same as " +
                "gotoNextLevel. Only a procedural land assembles differently.",
                false);
        }

        private void SetLandStatus(string message, bool isError)
        {
            _landStatus = message;
            _landStatusIsError = isError;
        }

        // =========================================================================
        // TAB 12: LAND STATE — the live readout of the land you are standing in
        // =========================================================================
        //
        // WHY THIS IS A SEPARATE TAB FROM "LANDS" (11). Lands is a travel map: it answers "where can I
        // go". This answers "what is the land I am in, and is it actually built correctly". Those are
        // different questions with different failure modes, and the second one is the one you need when
        // a procedural land comes out as a single room, or its rooms do not connect, or the exit is on
        // the wrong row — because the answer is in the runtime state and the plan, not in the catalogue.
        //
        // It is also the only place the conf RULES are visible at runtime. Every rule lives in
        // LandLayoutPlanner, which is pure, so the tab can re-plan the selected land with a throwaway
        // RNG and show the resulting cell grid WITHOUT touching the live world or the live RNG stream.

        private Vector2 _landStateScroll;

        /// <summary>
        /// Which land the plan preview is for. Independent of the land you are standing in, because the
        /// two questions are different: "why is THIS land wrong" needs the live state, "does conf 3 even
        /// produce the layout I think it does" needs to be askable about any land from anywhere.
        /// </summary>
        private string _landStatePreviewId = string.Empty;

        /// <summary>Stage the plan preview is computed at. AS3's <c>landStage</c> is a descent counter, and
        /// every conf gates something on it (conf 0 clamps the grid, conf 0/1/3/5/6 gate the deep exit),
        /// so a preview at one stage can only ever be half the story.</summary>
        private int _landStatePreviewStage;

        /// <summary>Last action's result, so a button that did nothing says so.</summary>
        private string _landStateStatus = string.Empty;

        /// <summary>
        /// Text the user is typing into each numeric field, keyed by field.
        ///
        /// <para>IMGUI keeps no per-control editing state for a <c>TextField</c> over a computed string,
        /// so the buffer has to live here. Keyed rather than a single field because the runtime stage and
        /// the preview stage are two different numbers on the same screen, and one buffer would make
        /// typing in one scramble the other.</para>
        /// </summary>
        private readonly Dictionary<string, string> _landStateIntBuffers = new Dictionary<string, string>();

        private void DrawLandStateTab()
        {
            _landStateScroll = GUILayout.BeginScrollView(_landStateScroll);

            GUILayout.Label("<b>Land State</b> — the live land, its runtime variables, and whether the " +
                            "world it built is traversable.", GUILayout.ExpandWidth(true));
            GUILayout.Label(
                "<color=#AAAAAA><size=11>Everything in the runtime section is edited in place: the values " +
                "are the ones the next build reads, so changing the descent stage or the visited flag and " +
                "then pressing Rebuild is how you reproduce a specific land without playing to it. The " +
                "plan preview below re-runs LandLayoutPlanner on a throwaway RNG, so looking at a layout " +
                "never disturbs the seeded world you are standing in.</size></color>",
                GUILayout.ExpandWidth(true));

            GUILayout.Space(6);

            CampaignManager campaign = CampaignManager.Current;
            if (campaign == null)
            {
                GUILayout.Box(
                    "No CampaignManager. It is created by the game lifetime scope, so this tab needs a " +
                    "play-mode session with the gameplay scene loaded.",
                    _cardStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndScrollView();
                return;
            }

            CampaignCatalog catalog = campaign.Catalog;
            if (catalog == null || catalog.AllLands == null || catalog.AllLands.Count == 0)
            {
                GUILayout.Box(
                    "The campaign catalogue is empty or missing (Resources/CampaignCatalog). Run " +
                    "PFE/Data/Import Campaign.",
                    _cardStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndScrollView();
                return;
            }

            string currentId = campaign.CurrentLandId.CurrentValue ?? string.Empty;
            LandDefinition current = string.IsNullOrEmpty(currentId) ? null : catalog.GetLand(currentId);

            DrawLandStateIdentity(campaign, current, currentId);
            GUILayout.Space(6);
            DrawLandStateRuntime(campaign, current, currentId);
            GUILayout.Space(6);
            DrawLandStateGeneratorInputs(campaign);
            GUILayout.Space(6);
            DrawLandStateBuiltWorld(campaign, currentId);
            GUILayout.Space(6);
            DrawLandStatePlanPreview(campaign, catalog, current, currentId);
            GUILayout.Space(6);
            DrawLandStateActions(campaign, current, currentId);

            GUILayout.EndScrollView();
        }

        /// <summary>Section A — the current land's authored data, read-only.</summary>
        private void DrawLandStateIdentity(CampaignManager campaign, LandDefinition land, string currentId)
        {
            GUILayout.BeginVertical(_cardStyle);

            GUILayout.Label("<b>A. Land identity</b> <color=#AAAAAA>(LandDefinition — authored data)</color>",
                GUILayout.ExpandWidth(true));

            if (land == null)
            {
                GUILayout.Label(
                    $"<color=#FFAA33>No LandDefinition for the current land id " +
                    $"'{currentId}'{(string.IsNullOrEmpty(currentId) ? " (no land entered yet)" : string.Empty)}." +
                    "</color> The catalogue is what LandIdToCollection and the build route key off, so a " +
                    "land without one is built as authored whatever its real conf is.",
                    GUILayout.ExpandWidth(true));
                GUILayout.EndVertical();
                return;
            }

            GUILayout.Label(
                $"<b>{land.landId}</b>  <color=#AAAAAA>{land.landName}</color>   " +
                $"tip=<b>{land.tip}</b>   " +
                (land.isProcedural
                    ? "<color=#FFAA33><b>procedural</b></color> (buildRandomLand)"
                    : "<color=#9AD0FF>authored</color> (buildSpecifLand)") +
                $"   conf=<b>{land.configId}</b>",
                GUILayout.ExpandWidth(true));

            GUILayout.Label(
                $"grid=<b>{land.gridWidth}x{land.gridHeight}</b>   " +
                $"entry=<b>({land.entryCoordinates.x},{land.entryCoordinates.y})</b>   " +
                $"stage(data)=<b>{land.stage}</b>   dif=<b>{land.baseDifficulty}</b>   " +
                $"autoLevel={(land.autoLevel ? "1" : "0")}   biome=<b>{land.biomeId}</b>",
                GUILayout.ExpandWidth(true));

            GUILayout.Label(
                $"exit prob=<b>{(string.IsNullOrEmpty(land.exitLandId) ? "(none)" : land.exitLandId)}</b>   " +
                $"collection=<b>{land.sourceFileKey}</b>   " +
                $"rooms=<b>{land.roomTemplates?.Count ?? 0}</b>   " +
                $"xp=<b>{land.xpReward}</b>   loadScr=<b>{land.loadScreenIndex}</b>   fin=<b>{land.fin}</b>",
                GUILayout.ExpandWidth(true));

            // The conf is only half the rule set; the grid the conf actually runs on can differ, and the
            // single most common "this land is wrong" report is a grid that was never clamped the way the
            // conf expects. Show the effective grid next to the declared one.
            if (land.isProcedural)
            {
                int effectiveMy = land.gridHeight;
                string note = string.Empty;
                if (land.configId == 0 && _landStatePreviewStageOf(land) <= 0)
                {
                    effectiveMy = 3;
                    note = "  <color=#FFAA33>← clamped to 3 rows while stage &lt;= 0 (Land.as:180-183)</color>";
                }

                GUILayout.Label(
                    $"effective grid at stage {_landStatePreviewStageOf(land)}: <b>{land.gridWidth}x{effectiveMy}</b>" +
                    note,
                    GUILayout.ExpandWidth(true));
            }

            GUILayout.EndVertical();
        }

        /// <summary>The runtime landStage for a land, without creating a state entry for a land you never visited.</summary>
        private static int _landStatePreviewStageOf(LandDefinition land)
        {
            CampaignManager campaign = CampaignManager.Current;
            if (campaign == null || land == null) return 0;
            return campaign.LandStates.TryGet(land.landId, out LandRuntimeState state) ? state.landStage : 0;
        }

        /// <summary>Section B — the runtime variables, all editable in place.</summary>
        private void DrawLandStateRuntime(CampaignManager campaign, LandDefinition land, string currentId)
        {
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label("<b>B. Runtime state</b> <color=#AAAAAA>(LandRuntimeState — what the next build " +
                            "reads; edits apply immediately)</color>", GUILayout.ExpandWidth(true));

            if (string.IsNullOrEmpty(currentId))
            {
                GUILayout.Label("<color=#FFAA33>No land entered yet — nothing to show.</color> " +
                                "Use the Lands tab's Go button first.", GUILayout.ExpandWidth(true));
                GUILayout.EndVertical();
                return;
            }

            LandRuntimeState state = campaign.LandStates.Get(currentId);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"landStage (<color=#AAAAAA>AS3 st</color>):", GUILayout.Width(170));
            int stage = DrawLandStateIntField("runtime.stage." + currentId, state.landStage, 70f);
            if (stage != state.landStage)
            {
                state.landStage = stage;
                SetLandStateStatus($"landStage = {stage}. Rebuild to apply.", false);
            }

            bool upStage = GUILayout.Toggle(state.upStage, " upStage (<color=#AAAAAA>blocks the next upland</color>)",
                GUILayout.Width(330));
            if (upStage != state.upStage) state.upStage = upStage;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool visited = GUILayout.Toggle(state.visited, " visited", GUILayout.Width(110));
            if (visited != state.visited) state.visited = visited;

            bool access = GUILayout.Toggle(state.access, " access", GUILayout.Width(110));
            if (access != state.access) state.access = access;

            bool passed = GUILayout.Toggle(state.passed, " passed", GUILayout.Width(110));
            if (passed != state.passed) state.passed = passed;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("lastCpCode:", GUILayout.Width(170));
            state.lastCpCode = GUILayout.TextField(state.lastCpCode ?? string.Empty, GUILayout.Width(200));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // The derived difficulty, shown because it is NOT the runtime stage: AS3 takes the max of the
            // incoming stage and the land's own `dif` (Land.as:120-124), so raising landStage past `dif`
            // changes nothing until it exceeds it. A tester who edits the stage and sees no change in
            // enemy strength is looking at this, not at a bug.
            if (land != null)
            {
                int dif = land.isProcedural
                    ? Mathf.Max(state.landStage, land.baseDifficulty)
                    : (land.autoLevel ? state.landStage : land.baseDifficulty);

                GUILayout.Label(
                    $"<color=#AAAAAA>derived:</color> landDifLevel=<b>{dif}</b> " +
                    $"<color=#AAAAAA>(AS3 max(stage, dif) for procedural; dif, or stage when autoLevel, for " +
                    $"authored)</color>   lootLimit=<b>{state.landStage + 3}</b>   " +
                    $"gameStage=<b>{land.stage}</b>",
                    GUILayout.ExpandWidth(true));
            }

            GUILayout.EndVertical();
        }

        /// <summary>
        /// Section C — the campaign triggers the generator actually reads.
        ///
        /// <para>Only two families matter to land generation and both are invisible everywhere else:
        /// <c>mbase_visited</c> decides whether conf 4's entry cell is the authored <c>beg0</c> or a random
        /// room (<c>Land.as:276</c>), and <c>prob_&lt;id&gt;</c> makes <c>newRandomProb</c> skip a prob it
        /// would otherwise place (<c>Land.as:759</c>). A land whose boss door is missing is usually a
        /// prob trigger left over from an earlier run.</para>
        /// </summary>
        private void DrawLandStateGeneratorInputs(CampaignManager campaign)
        {
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label("<b>C. Generator inputs</b> <color=#AAAAAA>(campaign triggers the builder " +
                            "branches on)</color>", GUILayout.ExpandWidth(true));

            int mbase = campaign.GetTrigger("mbase_visited");
            GUILayout.BeginHorizontal();
            GUILayout.Label("mbase_visited:", GUILayout.Width(170));
            GUILayout.Label(
                mbase > 0
                    ? $"<b>{mbase}</b> <color=#55FF55>set</color> <color=#AAAAAA>→ conf 4 (random_mbase) " +
                      "starts on a random room, not the authored beg0</color>"
                    : $"<b>{mbase}</b> <color=#888888>unset</color> <color=#AAAAAA>→ conf 4 places its " +
                      "authored beg0 entry room</color>",
                GUILayout.ExpandWidth(true));

            if (GUILayout.Button(mbase > 0 ? "Clear" : "Set", GUILayout.Width(70)))
            {
                campaign.SetTrigger("mbase_visited", mbase > 0 ? 0 : 1);
                SetLandStateStatus($"mbase_visited = {(mbase > 0 ? 0 : 1)}. Rebuild random_mbase to apply.", false);
            }
            GUILayout.EndHorizontal();

            // prob_* triggers. The catalogue has no index of them, so they are found by asking for the
            // ids the lands' prob tables would name — a miss is reported, not silently treated as unset.
            GUILayout.Label("<color=#AAAAAA>prob_* triggers (a set one makes the builder skip that " +
                            "boss door):</color>", GUILayout.ExpandWidth(true));

            CampaignCatalog catalog = campaign.Catalog;
            int shown = 0;
            if (catalog != null && catalog.AllLands != null)
            {
                for (int i = 0; i < catalog.AllLands.Count; i++)
                {
                    LandDefinition l = catalog.AllLands[i];
                    if (l == null || !l.isProcedural || string.IsNullOrEmpty(l.exitLandId)) continue;

                    int v = campaign.GetTrigger("prob_" + l.exitLandId);
                    if (v <= 0) continue;

                    shown++;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"prob_{l.exitLandId} = <b>{v}</b> <color=#AAAAAA>({l.landId})</color>",
                        GUILayout.Width(340));
                    if (GUILayout.Button("Clear", GUILayout.Width(70)))
                    {
                        campaign.SetTrigger("prob_" + l.exitLandId, 0);
                        SetLandStateStatus($"prob_{l.exitLandId} cleared.", false);
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
            }

            if (shown == 0)
            {
                GUILayout.Label(
                    "<color=#888888>none set — every land's prob table is fully available, so a boss door " +
                    "will be placed wherever the conf asks for one.</color>",
                    GUILayout.ExpandWidth(true));
            }

            GUILayout.EndVertical();
        }

        /// <summary>
        /// Section D — the world that was actually built, plus the two checks that answer "is it usable":
        /// reachability from the entry, and whether each door's grid direction agrees with the direction
        /// its neighbour actually sits in.
        /// </summary>
        private void DrawLandStateBuiltWorld(CampaignManager campaign, string currentId)
        {
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label("<b>D. Built world</b> <color=#AAAAAA>(the live LandMap)</color>",
                GUILayout.ExpandWidth(true));

            LandMap map = ResolveLandMap();
            if (map == null)
            {
                GUILayout.Label(
                    "<color=#FFAA33>No LandMap.</color> The bridge has not performed a land transition in " +
                    "this session, so no world has been built.",
                    GUILayout.ExpandWidth(true));
                GUILayout.EndVertical();
                return;
            }

            int roomCount = map.GetRoomCount();
            GUILayout.Label(
                $"bounds=<b>[{map.minBounds.x},{map.maxBounds.x}) x [{map.minBounds.y},{map.maxBounds.y})</b>   " +
                $"rooms=<b>{roomCount}</b>   " +
                $"current=<b>{(map.currentRoom != null ? map.currentRoom.id : "(none)")}</b> " +
                $"at <b>{map.currentRoom?.landPosition}</b>",
                GUILayout.ExpandWidth(true));

            if (roomCount == 0)
            {
                GUILayout.Label("<color=#FFAA33>The map is empty — the build produced no rooms.</color>",
                    GUILayout.ExpandWidth(true));
                GUILayout.EndVertical();
                return;
            }

            int activeDoors = 0, exits = 0, checkpoints = 0, unreachable = 0;
            int verticalDoors = 0, verticalMismatch = 0;
            var unreachableRooms = new List<string>();
            var mismatchRooms = new List<string>();

            HashSet<Vector3Int> reachable = ReachableRooms(map);

            foreach (RoomInstance room in map.GetAllRooms())
            {
                if (room == null) continue;

                if (!reachable.Contains(room.landPosition))
                {
                    unreachable++;
                    if (unreachableRooms.Count < 6) unreachableRooms.Add($"{room.landPosition}:{room.id}");
                }

                if (room.doors != null)
                {
                    foreach (DoorInstance door in room.doors)
                    {
                        if (door == null || !door.isActive) continue;
                        activeDoors++;

                        if (door.side != DoorSide.Bottom && door.side != DoorSide.Top) continue;
                        verticalDoors++;

                        RoomInstance target = map.GetRoom(door.targetRoomPosition);
                        if (target == null) continue;

                        // The door's SIDE says where it is carved in the room; the world delta says which
                        // way the room it leads to actually is. A door in the floor must lead DOWN.
                        //
                        // Do NOT compare the grid delta against the world delta here. The world delta is
                        // the grid delta times one room height, so their signs always agree and such a
                        // check can never fire — which is exactly what this readout used to do, and why it
                        // reported "all doors fine" on a vertically mirrored world.
                        float worldDy = RoomTransitionManager.GetRoomOriginUnity(target).y
                                      - RoomTransitionManager.GetRoomOriginUnity(room).y;

                        bool leadsDown = worldDy < 0f;
                        bool shouldLeadDown = door.side == DoorSide.Bottom;

                        if (Mathf.Abs(worldDy) > 0.0001f && leadsDown != shouldLeadDown)
                        {
                            verticalMismatch++;
                            if (mismatchRooms.Count < 4)
                            {
                                mismatchRooms.Add($"{room.landPosition} {door.side}→{target.landPosition}");
                            }
                        }
                    }
                }

                if (room.objects != null)
                {
                    foreach (ObjectInstance obj in room.objects)
                    {
                        if (obj == null) continue;
                        string type = obj.objectType ?? string.Empty;
                        if (type.IndexOf("exit", StringComparison.OrdinalIgnoreCase) >= 0) exits++;
                        if (type.IndexOf("checkpoint", StringComparison.OrdinalIgnoreCase) >= 0) checkpoints++;
                    }
                }
            }

            GUILayout.Label(
                $"active doors=<b>{activeDoors}</b>   exit boxes=<b>{exits}</b>   " +
                $"checkpoints=<b>{checkpoints}</b>",
                GUILayout.ExpandWidth(true));

            // ── Reachability ────────────────────────────────────────────────────
            //
            // This is the check that answers the original report ("no transition between the rooms of a
            // random land"). A room no active door reaches is a room the player can never stand in, and
            // the count alone does not show it: a land can be 20 rooms and 1 connected island.
            if (unreachable == 0)
            {
                GUILayout.Label(
                    $"<color=#55FF55>reachable:</color> all <b>{roomCount}</b> room(s) reachable from the " +
                    "entry over active doors.",
                    GUILayout.ExpandWidth(true));
            }
            else
            {
                GUILayout.Label(
                    $"<color=#FF6666>reachable:</color> <b>{roomCount - unreachable}</b> of <b>{roomCount}</b> " +
                    $"reachable — <b>{unreachable}</b> stranded: {string.Join(", ", unreachableRooms)}" +
                    (unreachable > unreachableRooms.Count ? ", …" : string.Empty),
                    GUILayout.ExpandWidth(true));
            }

            // ── Door direction vs world direction ───────────────────────────────
            //
            // The land grid keeps AS3's numbering — grid y+1 is the row BELOW (Land.gotoLoc case 3
            // steps y+1 and places the player at the target's ceiling; case 4 steps y-1 and places them
            // at its floor) — and AS3's rendering agrees with that, because Flash screen Y grows down.
            // Unity's +Y grows up, so the world Y of a land row is negated once, in
            // WorldCoordinates.LandRowToWorldPixelY. This readout is the live check that the two still
            // agree: a door carved in the floor must lead to a room rendered below.
            if (verticalDoors == 0)
            {
                GUILayout.Label("<color=#888888>no vertical doors in this land.</color>",
                    GUILayout.ExpandWidth(true));
            }
            else if (verticalMismatch == 0)
            {
                GUILayout.Label(
                    $"<color=#55FF55>door direction:</color> all <b>{verticalDoors}</b> vertical door(s) " +
                    "point at a neighbour on the side they are on.",
                    GUILayout.ExpandWidth(true));
            }
            else
            {
                GUILayout.Label(
                    $"<color=#FF6666>door direction:</color> <b>{verticalMismatch}</b> of <b>{verticalDoors}</b> " +
                    $"vertical door(s) point at a neighbour that is on the OPPOSITE side in world space " +
                    $"({string.Join(", ", mismatchRooms)}). The grid step and the room-origin Y sign " +
                    "disagree; see the Land State section notes.",
                    GUILayout.ExpandWidth(true));
            }

            GUILayout.EndVertical();
        }

        /// <summary>
        /// Flood fill from the room the map considers current, following each active door's recorded
        /// <c>targetRoomPosition</c>.
        ///
        /// <para><b>Why the door's own target and not adjacency.</b> Two rooms can be grid neighbours and
        /// not be connected — that is the whole point of a door. Walking the adjacency graph instead would
        /// report a land as fully connected while every door in it is shut, which is exactly the state
        /// this readout exists to catch.</para>
        /// </summary>
        private static HashSet<Vector3Int> ReachableRooms(LandMap map)
        {
            var seen = new HashSet<Vector3Int>();
            RoomInstance start = map.currentRoom;
            if (start == null) return seen;

            var queue = new Queue<RoomInstance>();
            queue.Enqueue(start);
            seen.Add(start.landPosition);

            while (queue.Count > 0)
            {
                RoomInstance room = queue.Dequeue();
                if (room.doors == null) continue;

                foreach (DoorInstance door in room.doors)
                {
                    if (door == null || !door.isActive) continue;

                    RoomInstance next = map.GetRoom(door.targetRoomPosition);
                    if (next == null || !seen.Add(next.landPosition)) continue;
                    queue.Enqueue(next);
                }
            }

            return seen;
        }

        /// <summary>The live LandMap, via the bridge that owns the reference to it.</summary>
        private static LandMap ResolveLandMap()
        {
            var bridge = FindFirstObjectByType<MapBridge>();
            return bridge != null ? bridge.CurrentLandMap : null;
        }

        /// <summary>
        /// Section E — the plan the conf rules produce, for any land, at any stage.
        ///
        /// <para><b>The RNG is a throwaway.</b> <c>LandLayoutPlanner.Plan</c> draws from the stream it is
        /// handed, so passing a fresh <c>PcgRngService</c> means a preview cannot shift the world the
        /// player is standing in. The seed is fixed, so the preview is stable across frames rather than
        /// reshuffling on every repaint — a preview that changed while you looked at it would be worse
        /// than none.</para>
        /// </summary>
        private void DrawLandStatePlanPreview(CampaignManager campaign, CampaignCatalog catalog,
            LandDefinition current, string currentId)
        {
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label("<b>E. Plan preview</b> <color=#AAAAAA>(LandLayoutPlanner, throwaway RNG — " +
                            "does not touch the live world)</color>", GUILayout.ExpandWidth(true));

            if (string.IsNullOrEmpty(_landStatePreviewId))
            {
                _landStatePreviewId = current != null && current.isProcedural ? current.landId : currentId;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("land:", GUILayout.Width(40));

            var options = new List<LandDefinition>();
            var captions = new List<string>();
            for (int i = 0; i < catalog.AllLands.Count; i++)
            {
                LandDefinition l = catalog.AllLands[i];
                if (l == null || string.IsNullOrEmpty(l.landId)) continue;
                options.Add(l);
                captions.Add(l.landId);
            }

            int selected = options.FindIndex(l => string.Equals(l.landId, _landStatePreviewId,
                StringComparison.OrdinalIgnoreCase));
            if (selected < 0) selected = 0;

            int picked = GUILayout.SelectionGrid(selected, captions.ToArray(), 6);
            if (picked != selected && picked >= 0 && picked < options.Count)
            {
                _landStatePreviewId = options[picked].landId;
                selected = picked;
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            LandDefinition land = selected >= 0 && selected < options.Count ? options[selected] : null;
            if (land == null)
            {
                GUILayout.EndVertical();
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("stage:", GUILayout.Width(40));
            _landStatePreviewStage = Mathf.Clamp(
                DrawLandStateIntField("preview.stage", _landStatePreviewStage, 50f), 0, 9);
            GUILayout.Label(
                "<color=#AAAAAA>AS3 landStage — every conf gates something on it (grid clamp, deep exit, " +
                "beg room, roof row).</color>",
                GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            if (!land.isProcedural)
            {
                GUILayout.Label(
                    $"<color=#9AD0FF>{land.landId}</color> is authored (conf is unused): its rooms come from " +
                    "the templates' own fixedPosition, not from a conf branch. Switch to a " +
                    "<color=#FFAA33>procedural</color> land to see a plan.",
                    GUILayout.ExpandWidth(true));
                GUILayout.EndVertical();
                return;
            }

            bool visited = false;
            bool mbaseVisited = campaign.GetTrigger("mbase_visited") > 0;
            if (campaign.LandStates.TryGet(land.landId, out LandRuntimeState previewState))
            {
                visited = previewState.visited;
            }

            LandLayoutPlan plan;
            try
            {
                plan = LandLayoutPlanner.Plan(new LandLayoutRequest
                {
                    LandId = land.landId,
                    Conf = land.configId,
                    GridWidth = land.gridWidth,
                    GridHeight = land.gridHeight,
                    LandStage = _landStatePreviewStage,
                    EntryCell = land.entryCoordinates,
                    Visited = visited,
                    MbaseVisited = mbaseVisited,
                }, new PFE.Core.Rng.PcgRngService());
            }
            catch (Exception ex)
            {
                GUILayout.Label($"<color=#FF6666>Plan threw: {ex.Message}</color>", GUILayout.ExpandWidth(true));
                GUILayout.EndVertical();
                return;
            }

            int exits = 0, checks = 0, probs = 0, tips = 0;
            foreach (LandCellPlan c in plan.Cells)
            {
                if (c.HasExit) exits++;
                if (c.Checkpoint != CheckpointKind.None) checks++;
                if (c.Prob != ProbKind.None) probs++;
                if (c.Fill == CellFill.Tip) tips++;
            }

            Vector3Int entry = WorldBuilder.ResolveProceduralEntry(land.entryCoordinates, plan.GridSize);

            GUILayout.Label(
                $"conf=<b>{plan.Conf}</b>   grid=<b>{plan.GridSize.x}x{plan.GridSize.y}</b>   " +
                $"cells=<b>{plan.CellCount}</b>   entry=<b>({entry.x},{entry.y})</b>" +
                (entry.x != land.entryCoordinates.x || entry.y != land.entryCoordinates.y
                    ? " <color=#FFAA33>(clamped)</color>" : string.Empty) +
                $"   forcedProbs=<b>{plan.ForcedProbs.Count}</b>",
                GUILayout.ExpandWidth(true));

            GUILayout.Label(
                $"tips=<b>{tips}</b>   exits=<b>{exits}</b>   checkpoints=<b>{checks}</b>   probs=<b>{probs}</b>   " +
                $"visited=<b>{visited}</b>   mbaseVisited=<b>{mbaseVisited}</b>   " +
                $"uniqueRoomPerCell=<b>{plan.UniqueRoomPerCell}</b>",
                GUILayout.ExpandWidth(true));

            if (exits == 0 && land.configId != 4)
            {
                GUILayout.Label(
                    "<color=#FF6666>This plan places no exit.</color> At this stage the land would be a " +
                    "dead end. Conf 0 and 1 gate the deep exit on the stage, conf 3 needs an even-parity " +
                    "top-row cell, so try a higher stage.",
                    GUILayout.ExpandWidth(true));
            }

            // The grid itself, one line per AS3 row (y = 0 is the ceiling). Each cell is a compact glyph
            // so a whole land fits on screen at once — which is the only way to see a rule like "exits on
            // the bottom row, checkpoints on the even cells" rather than infer it from counts.
            GUILayout.Label("<color=#AAAAAA>legend: B=beg tip  T=tip  .=random   E=exit  e=deep exit  " +
                            "C=checkpoint  c=beg checkpoint  P=forced prob  p=chance prob  " +
                            "W=water  G=gas  *=mirror  x=no placement</color>",
                GUILayout.ExpandWidth(true));

            var sb = new System.Text.StringBuilder();
            for (int y = 0; y < plan.GridSize.y; y++)
            {
                sb.Append(y == 0 ? "y=0 " : $"y={y} ");
                for (int x = 0; x < plan.GridSize.x; x++)
                {
                    if (!plan.TryGetCell(x, y, out LandCellPlan c))
                    {
                        sb.Append("?? ");
                        continue;
                    }

                    char g = '.';
                    if (c.SuppressPlacement) g = 'x';
                    else if (c.Fill == CellFill.Tip) g = (c.Tip != null && c.Tip.StartsWith("beg")) ? 'B' : 'T';

                    string marks = string.Empty;
                    if (c.Exit == ExitKind.Deep) marks += "e";
                    else if (c.Exit == ExitKind.Shallow) marks += "E";
                    if (c.Checkpoint == CheckpointKind.Begin) marks += "c";
                    else if (c.Checkpoint == CheckpointKind.Normal) marks += "C";
                    if (c.Prob == ProbKind.Forced) marks += "P";
                    else if (c.Prob == ProbKind.Chance) marks += "p";
                    if (c.Water >= 0) marks += "W";
                    if (c.Gas) marks += "G";
                    if (c.Mirror) marks += "*";

                    sb.Append(g).Append(marks.PadRight(3)).Append(' ');
                }
                sb.Append('\n');
            }

            GUILayout.Label(sb.ToString(), GUILayout.ExpandWidth(true));

            if (plan.ForcedProbs.Count > 0)
            {
                var forced = new List<string>();
                foreach (ForcedProbPlan fp in plan.ForcedProbs)
                {
                    forced.Add($"({fp.Position.x},{fp.Position.y}) stage>={fp.MinStage}");
                }
                GUILayout.Label($"<color=#AAAAAA>forced probs placed before the main pass: " +
                                $"{string.Join(", ", forced)}</color>", GUILayout.ExpandWidth(true));
            }

            GUILayout.EndVertical();
        }

        /// <summary>Section F — the buttons that make a change stick.</summary>
        private void DrawLandStateActions(CampaignManager campaign, LandDefinition land, string currentId)
        {
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label("<b>F. Actions</b>", GUILayout.ExpandWidth(true));

            GUILayout.BeginHorizontal();

            if (land != null && GUILayout.Button($"Rebuild '{land.landId}'", GUILayout.Width(220)))
            {
                bool ok = campaign.BeginMission(land.landId);
                SetLandStateStatus(
                    ok
                        ? $"BeginMission('{land.landId}') — the world rebuilds on the next frame with the " +
                          "state above."
                        : $"BeginMission('{land.landId}') did nothing: you are already there, or the id is empty.",
                    !ok);
            }

            if (land != null && GUILayout.Button("Force regenerate", GUILayout.Width(150)))
            {
                campaign.TransitionToLand(land.landId, null, forceRegenerate: true);
                SetLandStateStatus(
                    $"TransitionToLand('{land.landId}', forceRegenerate: true) — a fresh layout.",
                    false);
            }

            if (!string.IsNullOrEmpty(currentId) && GUILayout.Button("Reset runtime state", GUILayout.Width(170)))
            {
                LandRuntimeState state = campaign.LandStates.Get(currentId);
                state.landStage = 0;
                state.upStage = false;
                state.visited = false;
                state.passed = false;
                state.lastCpCode = string.Empty;
                SetLandStateStatus(
                    $"'{currentId}' reset to landStage 0, upStage/visited/passed false, no checkpoint code. " +
                    "access is deliberately kept — it is a travel-map unlock, not land progress.",
                    false);
            }

            if (!string.IsNullOrEmpty(currentId) && GUILayout.Button("Descend one level", GUILayout.Width(160)))
            {
                bool incremented = campaign.LandStates.UpLandLevel(currentId);
                SetLandStateStatus(
                    incremented
                        ? $"'{currentId}' landStage -> {campaign.LandStates.Get(currentId).landStage}."
                        : $"'{currentId}' landStage unchanged: upStage is already set, so the next upland " +
                          "is blocked (Game.as:474-481). Clear upStage first.",
                    !incremented);
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_landStateStatus))
            {
                // The colour tag is baked in by SetLandStateStatus — wrapping it in a second one here
                // would nest two <color> tags, and Unity's rich text does not nest: the outer one wins
                // and an error would render in the ordinary colour.
                GUILayout.Label(_landStateStatus, GUILayout.ExpandWidth(true));
            }

            GUILayout.EndVertical();
        }

        private void SetLandStateStatus(string message, bool isError)
        {
            _landStateStatus = isError
                ? "<color=#FF6666>" + message + "</color>"
                : "<color=#9AD0FF>" + message + "</color>";
        }

        /// <summary>
        /// An editable integer, because <c>GUILayout</c> has no numeric field (<c>IntField</c> is
        /// <c>EditorGUILayout</c>-only, and this overlay runs in a build too).
        /// </summary>
        /// <remarks>
        /// <para><b>Why a per-key text buffer and not <c>int.Parse</c> on the field text.</b> A raw
        /// <c>TextField</c> over <c>value.ToString()</c> cannot be typed into: every keystroke writes the
        /// parsed number back, the field re-renders from it, and a half-typed <c>"12"</c> collapses to
        /// <c>"1"</c> the moment <c>"1"</c> parses. The buffer is what the user is typing; the int is what
        /// the game uses. They are allowed to disagree while the text is mid-edit.</para>
        ///
        /// <para><b>Re-sync rule.</b> When the text is unchanged this frame, the buffer is refreshed from
        /// the live value <em>only if the buffer holds a valid number that disagrees with it</em> — i.e.
        /// some button changed the value. A buffer that is empty or half-typed is left alone, so clearing
        /// the field to retype does not have the old number pushed back into it.</para>
        /// </remarks>
        private int DrawLandStateIntField(string key, int value, float width)
        {
            if (!_landStateIntBuffers.TryGetValue(key, out string buffer) || buffer == null)
            {
                buffer = value.ToString();
                _landStateIntBuffers[key] = buffer;
            }

            string edited = GUILayout.TextField(buffer, GUILayout.Width(width));

            if (edited != buffer)
            {
                _landStateIntBuffers[key] = edited;
                return int.TryParse(edited, out int parsed) ? parsed : value;
            }

            if (int.TryParse(buffer, out int asInt) && asInt != value)
            {
                _landStateIntBuffers[key] = value.ToString();
            }

            return value;
        }

        // =========================================================================
        // TAB 13: LAND MAP — the minimap of the built land
        // =========================================================================
        //
        // The port of the original's Pip-Boy map page. AS3 paints a per-tile BitmapData
        // (`Land.drawMap`, Land.as:1517-1538, calling `Location.drawMap`, Location.as:2711-2817) and
        // shows it on the Pip-Boy's info page (`PipPageInfo.as:94-103`, sized/positioned at :816-833).
        // This tab is deliberately NOT that page: it is the same image in the F2 debug panel, so the
        // land can be looked at now rather than after the interface exists. When the interface lands,
        // the drawing half moves and this tab is deleted.
        //
        // The image is one texel per tile, composed by LandMinimapComposer from the LIVE LandMap (not a
        // re-plan) and uploaded to a Texture2D. It is REBUILT only when the map structurally changes or
        // on demand, never every frame: a 7x7 land is ~59k tiles, and SetPixels32 over that per frame
        // is pure waste for an image that only changes when you cross a door.
        //
        // The collision-outline debug tools are NOT used here, and should not be. `col on tiles` draws
        // world-space wireframes for the live view (ColliderDebugOverlay); a minimap is a top-down
        // picture, and the two answer different questions. What this tab draws is tile COLOURS, which is
        // what the oracle's minimap is — see docs/LandGameplayLoop/07_MINIMAP.md.

        private Vector2 _landMapScroll;
        private int _landMapZoom = 2;

        /// <summary>Draw every room, visited or not — the panel-local port of the oracle's global
        /// <c>World.w.drawAllMap</c>. A local flag rather than <c>LandMap.DrawAllMap</c> so a debug
        /// toggle cannot silently change what the game itself renders.</summary>
        private bool _landMapRevealAll;

        /// <summary>The uploaded image. Null until a build succeeds; recreated when the land's size
        /// changes and destroyed with the overlay.</summary>
        private Texture2D _landMapTexture;

        /// <summary>What <see cref="_landMapTexture"/> was built from. Rebuilt when this differs, which
        /// is the whole cache key — see <see cref="LandMapSignature"/>.</summary>
        private string _landMapSignature = string.Empty;

        private LandMinimapStats _landMapStats;

        /// <summary>Last action's result, so a button that did nothing says so.</summary>
        private string _landMapStatus = string.Empty;

        private void DrawLandMapTab(PlayerController player)
        {
            _landMapScroll = GUILayout.BeginScrollView(_landMapScroll);

            GUILayout.Label("<b>Land Map</b> — the minimap of the land the player is standing in, " +
                            "drawn from the live LandMap at one pixel per tile.",
                GUILayout.ExpandWidth(true));
            GUILayout.Label(
                "<color=#AAAAAA><size=11>This is the original's Pip-Boy map image " +
                "(Land.drawMap + Location.drawMap) shown in the debug panel until the interface exists. " +
                "Visibility is per <b>room</b>: the oracle also fades tiles by their own visi, which the " +
                "port does not carry.</size></color>",
                GUILayout.ExpandWidth(true));

            GUILayout.Space(6);

            LandMap map = ResolveLandMap();
            if (map == null)
            {
                GUILayout.Box(
                    "No LandMap. The bridge has not performed a land transition in this session, so no " +
                    "world has been built. Use the Lands tab's Go button, or enter a room in play mode.",
                    _cardStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndScrollView();
                return;
            }

            MinimapPlayerMarker marker = ResolvePlayerMarker(player);
            string signature = LandMapSignature(map, _landMapRevealAll, marker);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh", GUILayout.Width(90)))
            {
                _landMapSignature = string.Empty; // forces the rebuild below
                _landMapStatus = "Rebuilt from the live map.";
            }

            GUILayout.Label("zoom:", GUILayout.Width(40));
            _landMapZoom = Mathf.Clamp(
                Mathf.RoundToInt(GUILayout.HorizontalSlider(_landMapZoom, 1f, 6f, GUILayout.Width(120))), 1, 6);
            GUILayout.Label($"<b>{_landMapZoom}x</b>", GUILayout.Width(34));

            bool revealAll = GUILayout.Toggle(_landMapRevealAll, " reveal unvisited rooms", GUILayout.Width(200));
            if (revealAll != _landMapRevealAll)
            {
                _landMapRevealAll = revealAll;
                _landMapStatus = revealAll
                    ? "Revealing every room (the oracle's World.w.drawAllMap)."
                    : "Showing only visited rooms.";
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // The cache. A signature change means the room set, a room's visited flag, the current room
            // or the player's tile moved — i.e. the image would differ. Anything else (a destroyed tile,
            // a door opening) is not in the signature and is picked up by Refresh; see LandMapSignature.
            if (_landMapTexture == null || signature != _landMapSignature)
            {
                RebuildLandMapTexture(map, marker, signature);
            }

            if (_landMapTexture == null)
            {
                GUILayout.Box(
                    "The land has no positioned rooms to draw — the build produced nothing.",
                    _cardStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndScrollView();
                return;
            }

            DrawLandMapStats();
            GUILayout.Space(4);
            DrawLandMapLegend();
            GUILayout.Space(4);
            DrawLandMapImage();

            if (!string.IsNullOrEmpty(_landMapStatus))
            {
                GUILayout.Space(4);
                GUILayout.Label(_landMapStatus, GUILayout.ExpandWidth(true));
            }

            GUILayout.EndScrollView();
        }

        // =========================================================================
        // SAVE / LOAD TAB
        // =========================================================================

        private Vector2 _saveLoadScroll;

        /// <summary>
        /// Mirror of <c>SaveManager</c>'s autosave settings. The manager exposes
        /// <c>SetAutoSaveEnabled</c>/<c>SetAutoSaveInterval</c> but <b>no getters</b> — both fields are
        /// <c>[SerializeField] private</c> — so the tab cannot read the live values back. These are seeded
        /// from the manager's own defaults and kept in step by the setters below, and the tab says so
        /// rather than presenting a mirrored value as the manager's own.
        /// </summary>
        private bool _saveLoadAutoEnabled = true;
        private float _saveLoadAutoInterval = 300f;

        private string _saveLoadStatus = string.Empty;

        /// <summary>
        /// The F2 panel's save/load surface. <c>DevConsoleSaveCommands</c> already exposes the same three
        /// operations to the Lua console; this tab exists because a console verb reports into a log the
        /// developer has to go and read, while a save/load round-trip is something you want to <i>do</i>
        /// and watch — and because <c>CampaignManager.TeleportToCheckpoint</c> had no caller at all.
        /// </summary>
        private void DrawSaveLoadTab()
        {
            _saveLoadScroll = GUILayout.BeginScrollView(_saveLoadScroll);

            GUILayout.Label("<b>Save / Load</b> — the quick-save slot, the checkpoint record, and the " +
                            "autosave timer.", GUILayout.ExpandWidth(true));
            GUILayout.Label(
                "<color=#AAAAAA><size=11>A checkpoint writes the <b>autosave</b> slot, not the quick one " +
                "(SaveManager.RequestCheckpointSave — AS3 World.saveGame(-1)). Loading restores the " +
                "campaign block and then resumes at the recorded checkpoint; the land-entry rule that " +
                "picks the room is CheckpointRules.ResumeRoomOnEntry, which is exercised offline.</size></color>",
                GUILayout.ExpandWidth(true));

            GUILayout.Space(6);

            SaveManager save = SaveManager.Instance;
            if (save == null)
            {
                GUILayout.Box(
                    "No SaveManager in the scene. It is created by the game lifetime scope, so this tab " +
                    "needs a play-mode session with the gameplay scene loaded.",
                    _cardStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndScrollView();
                return;
            }

            DrawSaveLoadSlotRow("Quick save", SaveManager.QuickSaveSlotId);
            GUILayout.Space(2);
            DrawSaveLoadSlotRow("Autosave", SaveManager.AutoSaveSlotId);

            GUILayout.Space(6);
            GUILayout.Label($"current slot: <b>{save.GetCurrentSaveId() ?? "-"}</b>", GUILayout.ExpandWidth(true));
            GUILayout.Label($"save dir: <size=11>{WorldSerializer.GetSaveDirectory()}</size>",
                GUILayout.ExpandWidth(true));

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Quick Save", GUILayout.Width(110)))
            {
                _saveLoadStatus = save.QuickSave(ResolveLandMap())
                    ? "Quick save written."
                    : "Quick save FAILED — see the Unity console (a null LandMap is the usual cause).";
            }

            if (GUILayout.Button("Quick Load", GUILayout.Width(110)))
            {
                // HasQuickSave is checked first so "no save yet" reports as itself: the two have
                // completely different fixes, and QuickLoad's own refusal says only "no quick save found".
                _saveLoadStatus = !save.HasQuickSave()
                    ? "No quick save on disk — press Quick Save first."
                    : save.QuickLoad(ResolveLandMap())
                        ? "Quick load applied."
                        : "Quick load FAILED — see the Unity console.";
            }

            if (GUILayout.Button("Checkpoint Save", GUILayout.Width(140)))
            {
                _saveLoadStatus = save.RequestCheckpointSave()
                    ? "Checkpoint save written to the autosave slot."
                    : "Checkpoint save FAILED — see the Unity console.";
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_saveLoadStatus))
            {
                GUILayout.Space(4);
                GUILayout.Label(_saveLoadStatus, GUILayout.ExpandWidth(true));
            }

            GUILayout.Space(8);
            DrawSaveLoadCheckpointBlock();

            GUILayout.Space(8);
            DrawSaveLoadAutoSaveBlock(save);

            GUILayout.Space(8);
            DrawSaveLoadAllSaves(save);

            GUILayout.EndScrollView();
        }

        private static void DrawSaveLoadSlotRow(string label, string slotId)
        {
            bool present = WorldSerializer.SaveExists(slotId);
            string path = WorldSerializer.GetSaveFilePath(slotId);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{label}</b>", GUILayout.Width(90));
            GUILayout.Label(
                present ? "<color=#7CFC00>present</color>" : "<color=#FF8080>absent</color>",
                GUILayout.Width(72));

            if (present)
            {
                var info = new FileInfo(path);
                GUILayout.Label(
                    $"<size=11>{FormatSaveBytes(info.Length)}  written " +
                    $"{info.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}Z</size>",
                    GUILayout.ExpandWidth(true));
            }
            else
            {
                GUILayout.Label($"<size=11>{path}</size>", GUILayout.ExpandWidth(true));
            }

            GUILayout.EndHorizontal();
        }

        private void DrawSaveLoadCheckpointBlock()
        {
            GUILayout.Label("<b>Checkpoint</b>", GUILayout.ExpandWidth(true));

            CampaignManager campaign = CampaignManager.Current;
            if (campaign == null)
            {
                GUILayout.Box("No CampaignManager — the checkpoint record lives on it.",
                    _cardStyle, GUILayout.ExpandWidth(true));
                return;
            }

            CampaignManager.CheckpointRecord? recorded = campaign.CurrentCheckpoint;
            if (recorded == null)
            {
                GUILayout.Label(
                    "none recorded — activate a checkpoint in play, or load a save that carries one.",
                    GUILayout.ExpandWidth(true));
                return;
            }

            CampaignManager.CheckpointRecord checkpoint = recorded.Value;
            GUILayout.Label(
                $"land <b>{checkpoint.landId}</b>   room <b>({checkpoint.roomX}, {checkpoint.roomY}, " +
                $"{checkpoint.roomZ})</b>   code " +
                $"<b>{(string.IsNullOrEmpty(checkpoint.code) ? "-" : checkpoint.code)}</b>",
                GUILayout.ExpandWidth(true));

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Go to checkpoint", GUILayout.Width(150)))
            {
                // AS3 Consol.as:447 `World.w.land.gotoCheckPoint()` — jump to the checkpoint, in the
                // checkpoint's own room. The room half is the land-entry resume: TransitionToLand rebuilds
                // the land, and because a checkpoint can only have been activated by standing in its land,
                // that land is already `visited`, so firstVisit is false and ResumeRoomOnEntry picks the
                // checkpoint's room. Without that wiring this button would land the player on the entry
                // cell and look like it had merely reloaded the land.
                campaign.TransitionToLand(checkpoint.landId);
                _saveLoadStatus = $"Transitioning to '{checkpoint.landId}' — the entry resumes in the " +
                                  "checkpoint's room.";
            }

            if (GUILayout.Button("Return (hub)", GUILayout.Width(120)))
            {
                _saveLoadStatus = campaign.TeleportToCheckpoint(main: false)
                    ? "Checkpoint teleport started (AS3 CheckPoint.teleport, hub branch)."
                    : "Checkpoint teleport is a no-op here (a main checkpoint whose mission is the hub).";
            }

            if (GUILayout.Button("Return (main)", GUILayout.Width(120)))
            {
                _saveLoadStatus = campaign.TeleportToCheckpoint(main: true)
                    ? "Checkpoint teleport started (main branch -> the mission land)."
                    : "Checkpoint teleport is a no-op here (a main checkpoint whose mission is the hub).";
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawSaveLoadAutoSaveBlock(SaveManager save)
        {
            GUILayout.Label("<b>Autosave</b>", GUILayout.ExpandWidth(true));

            GUILayout.BeginHorizontal();

            bool enabled = GUILayout.Toggle(_saveLoadAutoEnabled, " enabled", GUILayout.Width(110));
            if (enabled != _saveLoadAutoEnabled)
            {
                _saveLoadAutoEnabled = enabled;
                save.SetAutoSaveEnabled(enabled);
                _saveLoadStatus = enabled ? "Autosave enabled." : "Autosave disabled.";
            }

            GUILayout.Label("interval:", GUILayout.Width(58));
            _saveLoadAutoInterval = GUILayout.HorizontalSlider(
                _saveLoadAutoInterval, 30f, 900f, GUILayout.Width(180));
            GUILayout.Label($"<b>{_saveLoadAutoInterval:0} s</b>", GUILayout.Width(64));

            if (GUILayout.Button("Apply interval", GUILayout.Width(110)))
            {
                save.SetAutoSaveInterval(_saveLoadAutoInterval);
                _saveLoadStatus = $"Autosave interval set to {_saveLoadAutoInterval:0} s.";
            }

            if (GUILayout.Button("Trigger now", GUILayout.Width(100)))
            {
                _saveLoadStatus = save.TriggerAutoSave()
                    ? "Autosave written."
                    : "Autosave refused (disabled, or the world is not ready).";
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(
                "<color=#AAAAAA><size=11>A mirror, not a readback — SaveManager exposes the setters but " +
                "not the fields, so these controls show what this tab last applied.</size></color>",
                GUILayout.ExpandWidth(true));
        }

        private void DrawSaveLoadAllSaves(SaveManager save)
        {
            List<SaveMetadata> all = save.GetAllSaves();
            GUILayout.Label($"<b>All saves</b> ({(all != null ? all.Count : 0)})", GUILayout.ExpandWidth(true));

            if (all == null || all.Count == 0)
            {
                GUILayout.Label("none on disk.", GUILayout.ExpandWidth(true));
                return;
            }

            foreach (SaveMetadata meta in all)
            {
                if (meta == null) continue;

                GUILayout.BeginHorizontal();

                // Not GetDisplayName() directly: it does `saveId.Substring(0, 8)`, which throws on a
                // shorter id — an exception inside a debug panel that is meant to be readable when
                // something else has already gone wrong.
                string name = string.IsNullOrEmpty(meta.saveId)
                    ? "(no id)"
                    : meta.saveId.Length >= 8 ? meta.GetDisplayName() : meta.saveId;

                GUILayout.Label(
                    $"{name}  <size=11>rooms={meta.roomCount}  {FormatSaveBytes(meta.fileSize)}</size>",
                    GUILayout.ExpandWidth(true));

                if (GUILayout.Button("Load", GUILayout.Width(70)))
                {
                    _saveLoadStatus = save.LoadGame(meta.saveId, ResolveLandMap())
                        ? $"Loaded '{meta.saveId}'."
                        : $"Load of '{meta.saveId}' FAILED — see the Unity console.";
                }

                if (GUILayout.Button("Delete", GUILayout.Width(70)))
                {
                    _saveLoadStatus = save.DeleteSave(meta.saveId)
                        ? $"Deleted '{meta.saveId}'."
                        : $"Delete of '{meta.saveId}' FAILED.";
                }

                GUILayout.EndHorizontal();
            }
        }

        private static string FormatSaveBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024f:0.#} KiB";
            return $"{bytes / (1024f * 1024f):0.#} MiB";
        }

        /// <summary>
        /// A cheap structural fingerprint of everything the image depends on, so the texture is rebuilt
        /// on a real change and reused otherwise.
        ///
        /// <para><b>What it deliberately excludes.</b> Per-tile state — a wall destroyed, a door opened —
        /// is not in here, because reading all 59k tiles every frame to detect it would cost more than
        /// the rebuild it saves. The Refresh button is the escape hatch, and it is the honest trade for
        /// a debug panel: the alternative is a per-frame full scan to catch a change you can also just
        /// ask for.</para>
        /// </summary>
        private static string LandMapSignature(LandMap map, bool revealAll, in MinimapPlayerMarker marker)
        {
            int count = 0, visited = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            foreach (RoomInstance room in map.GetAllRooms())
            {
                if (room == null)
                {
                    continue;
                }

                count++;
                if (room.isVisited) visited++;

                Vector3Int p = room.landPosition;
                if (p.x < minX) minX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.x > maxX) maxX = p.x;
                if (p.y > maxY) maxY = p.y;
            }

            Vector3Int current = map.currentRoom != null
                ? map.currentRoom.landPosition
                : new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

            Vector2Int playerTile = marker.Present
                ? WorldCoordinates.PixelToTile(marker.RoomLocalPixels)
                : new Vector2Int(int.MinValue, int.MinValue);

            return string.Join("|",
                count, visited, minX, minY, maxX, maxY,
                current.x, current.y, current.z,
                revealAll ? 1 : 0,
                marker.Present ? 1 : 0, marker.Cell.x, marker.Cell.y, playerTile.x, playerTile.y);
        }

        /// <summary>The player's cell and room-local tile, or <see cref="MinimapPlayerMarker.None"/>.
        /// Read from <c>TilePhysicsController.PixelPosition</c> — the same source the telekinesis cursor
        /// uses (PlayerTelekinesisController:1464) — so the marker and the rest of the game cannot
        /// disagree about where the player's feet are.</summary>
        private static MinimapPlayerMarker ResolvePlayerMarker(PlayerController player)
        {
            if (player == null)
            {
                return MinimapPlayerMarker.None;
            }

            var physics = player.GetComponent<TilePhysicsController>();
            if (physics == null)
            {
                return MinimapPlayerMarker.None;
            }

            Vector2 worldPixels = physics.PixelPosition;
            return new MinimapPlayerMarker(
                true,
                WorldCoordinates.WorldToLand(worldPixels),
                WorldCoordinates.WorldToLocal(worldPixels));
        }

        private void RebuildLandMapTexture(LandMap map, in MinimapPlayerMarker marker, string signature)
        {
            if (!LandMinimapComposer.TryBuild(map, _landMapRevealAll, marker,
                    out LandMinimapBuffer buffer, out _landMapStats))
            {
                ReleaseLandMapTexture();
                _landMapSignature = signature;
                return;
            }

            if (_landMapTexture == null ||
                _landMapTexture.width != buffer.Width ||
                _landMapTexture.height != buffer.Height)
            {
                ReleaseLandMapTexture();
                _landMapTexture = new Texture2D(buffer.Width, buffer.Height, TextureFormat.RGBA32, false)
                {
                    // Point filtering so a tile stays a crisp square at every zoom; the whole point of a
                    // per-tile image is that a single tile is legible.
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "PFE.LandMinimap"
                };
            }

            // The buffer is bottom-up, which is what SetPixels32 expects; see LandMinimapBuffer.
            _landMapTexture.SetPixels32(buffer.Pixels);
            _landMapTexture.Apply(false, false);
            _landMapSignature = signature;
        }

        private void ReleaseLandMapTexture()
        {
            if (_landMapTexture != null)
            {
                Destroy(_landMapTexture);
                _landMapTexture = null;
            }
        }

        private void DrawLandMapStats()
        {
            LandMinimapStats s = _landMapStats;
            string hidden = s.RoomsHidden > 0
                ? $"<color=#FFAA33>hidden=<b>{s.RoomsHidden}</b></color>"
                : "hidden=<b>0</b>";

            GUILayout.Label(
                $"rooms=<b>{s.RoomsTotal}</b>   drawn=<b>{s.RoomsDrawn}</b>   {hidden}   " +
                $"image=<b>{_landMapTexture.width}x{_landMapTexture.height}</b>px   " +
                $"cells=[{s.MinCell.x},{s.MaxCell.x}] x [{s.MinCell.y},{s.MaxCell.y}]   " +
                $"revealAll=<b>{(s.RevealAll ? 1 : 0)}</b>",
                GUILayout.ExpandWidth(true));
        }

        /// <summary>The colours the image can contain, as inline swatches. Every one is a
        /// <see cref="MinimapPalette"/> field, so the legend cannot drift from what is drawn.
        ///
        /// <para><b>Hand-wrapped into short rows on purpose.</b> As one label the legend rendered ~165
        /// characters; the window is 920 px wide and the label style does not wrap, so IMGUI would have
        /// silently CLIPPED the tail — and the tail is <c>current room</c> and <c>player</c>, the two
        /// markers a reader most needs the key for. Nothing goes red for this: not the compiler, not a
        /// test, not a log. The budget is ~135 rendered characters (≈880 px of content at fontSize 12,
        /// ≈6.5 px/char); these rows are ~55 each. Rich-text tags cost no width, so count what the row
        /// DRAWS, not its source.</para></summary>
        private static void DrawLandMapLegend()
        {
            GUILayout.Label(
                "<color=#AAAAAA>legend: </color>" +
                Swatch(MinimapPalette.Solid, "wall") + "  " +
                Swatch(MinimapPalette.SolidDamaged, "wall&lt;100hp") + "  " +
                Swatch(MinimapPalette.SolidIndestructible, "indestructible") + "  " +
                Swatch(MinimapPalette.SolidDoor, "door tile"),
                GUILayout.ExpandWidth(true));

            GUILayout.Label(
                "        " +
                Swatch(MinimapPalette.Air, "open") + "  " +
                Swatch(MinimapPalette.Water, "water") + "  " +
                Swatch(MinimapPalette.ShelfOrSlope, "shelf/slope") + "  " +
                Swatch(MinimapPalette.Stair, "ladder"),
                GUILayout.ExpandWidth(true));

            GUILayout.Label(
                "        " +
                Swatch(MinimapPalette.MarkerDoor, "door") + "  " +
                Swatch(MinimapPalette.MarkerExit, "exit") + "  " +
                Swatch(MinimapPalette.MarkerProb, "prob") + "  " +
                Swatch(MinimapPalette.MarkerCheckpoint, "checkpoint") + "  " +
                Swatch(MinimapPalette.MarkerInteractable, "loot") + "  " +
                Swatch(MinimapPalette.MarkerNpc, "unit"),
                GUILayout.ExpandWidth(true));

            GUILayout.Label(
                "        " +
                Swatch(MinimapPalette.CurrentRoomOutline, "current room") + "  " +
                Swatch(MinimapPalette.Player, "player"),
                GUILayout.ExpandWidth(true));
        }

        private static string Swatch(Color32 color, string label)
        {
            return $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>■</color>{label}";
        }

        private void DrawLandMapImage()
        {
            float width = _landMapTexture.width * _landMapZoom;
            float height = _landMapTexture.height * _landMapZoom;

            // Cap the drawn width so a big land cannot push the layout off the window. The vertical
            // scroll view clips horizontally, so an uncapped width would simply be unreachable.
            float maxWidth = Mathf.Max(240f, _windowRect.width - 80f);
            if (width > maxWidth)
            {
                float scale = maxWidth / width;
                width *= scale;
                height *= scale;
            }

            Rect rect = GUILayoutUtility.GetRect(width, height);
            GUI.DrawTexture(rect, _landMapTexture, ScaleMode.StretchToFill, false);
        }

        // =========================================================================
        // TAB 0: PIP LIVE STATS
        // =========================================================================

        private void DrawPipStatsTab(PlayerController player)
        {
            var charStats = player.CharacterStats;
            var unitStats = player.Stats;
            if (charStats == null)
            {
                GUILayout.Label("CharacterStats component missing on Player.");
                return;
            }

            _pipStatsScroll = GUILayout.BeginScrollView(_pipStatsScroll);

            // ── Section 1: Overview & Vitals Bar ─────────────────────────────────
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label("<b>📊 LittlePip Live Vitals & Level Progress</b>", _headerStyle);
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>Level:</b> <color=#55FF55>{charStats.Level}</color> (XP: {charStats.Xp})", GUILayout.Width(180));
            GUILayout.Label($"<b>Skill Points:</b> <color=#FFFF55>{charStats.SkillPoints}</color>", GUILayout.Width(140));
            GUILayout.Label($"<b>Perk Points:</b> <color=#FFAA33>{charStats.PerkPoints}</color> (+{charStats.PerkPointsExtra} bonus)", GUILayout.Width(180));
            GUILayout.FlexibleSpace();
            GUILayout.Label($"<b>Max Capacities:</b> Wpn: {charStats.maxmW} | Med: {charStats.maxmM} | Ammo: L:{charStats.maxm1:0} E:{charStats.maxm2:0} H:{charStats.maxm3:0}");
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // Health & Mana Bars
            float curHp = unitStats != null ? unitStats.CurrentHp.Value : 0f;
            float maxHp = unitStats != null ? unitStats.MaxHp.Value : charStats.MaxHp;
            float curMana = charStats.manaHp;
            float maxMana = charStats.MaxMana;

            GUILayout.BeginHorizontal();
            // HP Bar
            GUILayout.BeginVertical(GUILayout.Width(420));
            GUILayout.Label($"<b>❤️ Health:</b> {curHp:0.0} / {maxHp:0.0} ({(maxHp > 0 ? (curHp / maxHp * 100f) : 0):0}%)");
            DrawProgressBar(curHp / Mathf.Max(1f, maxHp), new Color(0.85f, 0.2f, 0.2f), new Color(0.25f, 0.1f, 0.1f));
            GUILayout.EndVertical();

            GUILayout.Space(20);

            // Mana Bar
            GUILayout.BeginVertical(GUILayout.Width(420));
            GUILayout.Label($"<b>🔮 Mana:</b> {curMana:0.0} / {maxMana:0.0} ({(maxMana > 0 ? (curMana / maxMana * 100f) : 0):0}%)");
            DrawProgressBar(curMana / Mathf.Max(1f, maxMana), new Color(0.2f, 0.55f, 0.95f), new Color(0.1f, 0.15f, 0.25f));
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            GUILayout.Space(8);

            // ── Section 2: 5-Zone Internal Organ Health & Trauma ─────────────────
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>🫀 5-Zone Internal Organ Health & Trauma Monitoring</b>", _headerStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("💖 Heal All Organs & Clear Trauma", GUILayout.Width(240), GUILayout.Height(22)))
            {
                charStats.HealAll();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#AAAAAA><size=11>LittlePip's body is divided into 5 vital zones. Organ damage triggers trauma stages (0: Healthy, 1: Minor, 2: Moderate, 3: Severe, 4: Crippled).</size></color>");
            GUILayout.Space(6);

            GUILayout.BeginHorizontal();
            DrawOrganColumn("🧠 Head", charStats.headHp, charStats.InMaxHP, charStats.headSt, charStats.headMin,
                delta => { charStats.headHp = Mathf.Clamp(charStats.headHp + delta, 1f, charStats.InMaxHP); charStats.RecalculateStats(); },
                () => { charStats.headHp = charStats.InMaxHP; charStats.headSt = 0; charStats.RecalculateStats(); });

            DrawOrganColumn("🫀 Torso", charStats.torsHp, charStats.InMaxHP, charStats.torsSt, charStats.torsMin,
                delta => { charStats.torsHp = Mathf.Clamp(charStats.torsHp + delta, 1f, charStats.InMaxHP); charStats.RecalculateStats(); },
                () => { charStats.torsHp = charStats.InMaxHP; charStats.torsSt = 0; charStats.RecalculateStats(); });

            DrawOrganColumn("🦵 Legs", charStats.legsHp, charStats.InMaxHP, charStats.legsSt, charStats.legsMin,
                delta => { charStats.legsHp = Mathf.Clamp(charStats.legsHp + delta, 1f, charStats.InMaxHP); charStats.RecalculateStats(); },
                () => { charStats.legsHp = charStats.InMaxHP; charStats.legsSt = 0; charStats.RecalculateStats(); });

            DrawOrganColumn("🩸 Blood", charStats.bloodHp, charStats.InMaxHP, charStats.bloodSt, charStats.bloodMin,
                delta => { charStats.bloodHp = Mathf.Clamp(charStats.bloodHp + delta, 1f, charStats.InMaxHP); charStats.RecalculateStats(); },
                () => { charStats.bloodHp = charStats.InMaxHP; charStats.bloodSt = 0; charStats.RecalculateStats(); });

            DrawOrganColumn("🔮 Mana/Mind", charStats.manaHp, charStats.MaxMana, charStats.manaSt, charStats.manaMin,
                delta => { charStats.manaHp = Mathf.Clamp(charStats.manaHp + delta, 0f, charStats.MaxMana); charStats.RecalculateStats(); },
                () => { charStats.manaHp = charStats.MaxMana; charStats.manaSt = 0; charStats.RecalculateStats(); });
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color=#CCCCCC>Organ Max HP: <b>{charStats.InMaxHP:0}</b>  |  Damage Scale: <b>x{charStats.organMult:0.00}</b>  |  Potion Scale: <b>x{charStats.organMultPot:0.00}</b>  |  Radiation Sensitivity: <b>{charStats.radChild:0.00}</b></color>");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("⚠️ Test: Cripple Head (St 3)", GUILayout.Width(170), GUILayout.Height(20)))
            {
                charStats.headHp = charStats.InMaxHP * 0.2f;
                charStats.headSt = 3;
                charStats.RecalculateStats();
            }
            if (GUILayout.Button("⚠️ Test: Cripple Legs (St 3)", GUILayout.Width(170), GUILayout.Height(20)))
            {
                charStats.legsHp = charStats.InMaxHP * 0.2f;
                charStats.legsSt = 3;
                charStats.RecalculateStats();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(8);

            // ── Section 3: Offensive Multipliers & Weapon Ratings ────────────────
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label("<b>⚔️ Combat Offense Multipliers & Weapon Tuning</b>", _headerStyle);
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();

            // Col 1: Damage Multipliers
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(280));
            GUILayout.Label("<b>💥 Damage Multipliers</b>", _subHeaderStyle);
            DrawStatPair("All Damage (allDamMult):", $"<color=#FFFF55><b>x{charStats.allDamMult:0.00}</b></color>");
            DrawStatPair("Guns Damage:", $"x{charStats.gunsDamMult:0.00}");
            DrawStatPair("Melee Damage:", $"x{charStats.meleeDamMult:0.00}");
            DrawStatPair("Melee Attack Speed:", $"x{charStats.meleeSpdMult:0.00}");
            DrawStatPair("Spells / Magic Damage:", $"x{charStats.spellsDamMult:0.00}");
            DrawStatPair("Punch / Unarmed:", $"x{charStats.punchDamMult:0.00}");
            GUILayout.EndVertical();

            GUILayout.Space(10);

            // Col 2: Criticals & Weapon Handling
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(280));
            GUILayout.Label("<b>🎯 Criticals & Handling</b>", _subHeaderStyle);
            DrawStatPair("Base Crit Chance:", $"<color=#55FF55><b>{(charStats.critCh * 100f):0.1f}%</b></color>");
            DrawStatPair("Crit Damage Multiplier:", $"<color=#FFAA33><b>x{charStats.critDamMult:0.00}</b></color>");
            DrawStatPair("Stealth Crit Bonus:", $"+{(charStats.critInvis * 100f):0.1f}%");
            DrawStatPair("Weapon Precision:", $"x{charStats.allPrecMult:0.00}");
            DrawStatPair("Reload Speed:", $"x{charStats.reloadMult:0.00}");
            DrawStatPair("Recoil Handling:", $"x{charStats.recoilMult:0.00}");
            DrawStatPair("Kick / Destruct Force:", $"{charStats.kickDestroy:0}");
            GUILayout.EndVertical();

            GUILayout.Space(10);

            // Col 3: Mob Slayer Multipliers
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(280));
            GUILayout.Label("<b>💀 Enemy Slaying Bonuses</b>", _subHeaderStyle);
            DrawStatPair("vs Ponies / Raiders:", FormatMobBonus(charStats.damPony));
            DrawStatPair("vs Ghouls / Zombies:", FormatMobBonus(charStats.damZombie));
            DrawStatPair("vs Robots / Turrets:", FormatMobBonus(charStats.damRobot));
            DrawStatPair("vs Insects / Bugs:", FormatMobBonus(charStats.damInsect));
            DrawStatPair("vs Monsters / Mutants:", FormatMobBonus(charStats.damMonster));
            DrawStatPair("vs Alicorns / Bosses:", FormatMobBonus(charStats.damAlicorn));
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(8);

            // ── Section 4: Defense, Armor & 17 Damage Resistances ────────────────
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label("<b>🛡️ Defense, Armor & Vulnerability Multipliers</b>", _headerStyle);
            GUILayout.Space(4);

            // Defense summary
            GUILayout.BeginHorizontal(_cardActiveStyle);
            string armorName = (unitStats != null && !string.IsNullOrEmpty(unitStats.ArmourId.Value)) ? unitStats.ArmourId.Value : "None";
            float armInteg = unitStats != null ? unitStats.ArmourIntegrity.Value * 100f : 0f;
            float armEff = unitStats != null ? unitStats.armorEffectiveness : 1f;

            DrawStatPair("Global Vulnerability:", $"<color=#FFAA33><b>x{charStats.allVulnerMult:0.00}</b></color>");
            GUILayout.Space(10);
            DrawStatPair("Natural Skin Resist:", $"<color=#55FF55><b>+{charStats.skin:0.00}</b></color>");
            GUILayout.Space(10);
            DrawStatPair("Equipped Armor:", $"<b>{armorName}</b> ({armInteg:0}% / Eff: x{armEff:0.00})");
            GUILayout.Space(10);
            DrawStatPair("Evasion (Dex/Dodge):", $"Div: {unitStats?.dexterity ?? 1f:0.00} | Dodge: {((unitStats?.dodge ?? 0f) * 100f):0.1f}%");
            GUILayout.EndHorizontal();

            // The live projection itself — the numbers the damage resolver actually reads
            // (AS3 `owner.armor` / `owner.marmor` / `owner.armor_qual`, written by `Armor.setArmor()`).
            // This row is why the panel looked inert: before the armour importer ran, every plate
            // resolved to a null definition, so these were 0/0/0 and nothing here moved.
            if (unitStats != null && unitStats.armour.IsEquipped)
            {
                var a = unitStats.armour;
                GUILayout.BeginHorizontal(_cardStyle);
                GUILayout.Label(
                    $"<b>Armor Ratings:</b> Phys <color=#55AAFF><b>{a.EffectivePhysicalRating:0.#}</b></color> | " +
                    $"Magic <color=#55AAFF><b>{a.EffectiveEnergyRating:0.#}</b></color> | " +
                    $"Reliability <b>{a.EffectiveReliability:0.00}</b> | " +
                    $"Condition <b>x{a.ConditionFactor:0.00}</b>",
                    GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.Label("<b>17 Damage Resistances (UnitStats.Vulnerabilities)</b> — <color=#AAAAAA><size=11>x1.00 = Normal, &lt;x1.00 = Resistant, &gt;x1.00 = Vulnerable, x0.00 = Immune</size></color>");

            var vul = unitStats != null ? unitStats.Vulnerabilities : VulnerabilityData.Neutral;

            GUILayout.BeginHorizontal();

            // Physical (3)
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(210));
            GUILayout.Label("<b>🥊 Physical (3)</b>", _subHeaderStyle);
            DrawResistCell("Bullet", vul.bullet);
            DrawResistCell("Blade / Edged", vul.blade);
            DrawResistCell("Melee (Phis)", vul.phis);
            GUILayout.EndVertical();

            GUILayout.Space(7);

            // Elemental (7)
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(210));
            GUILayout.Label("<b>🔥 Elemental (7)</b>", _subHeaderStyle);
            DrawResistCell("Fire / Burn", vul.fire);
            DrawResistCell("Explosive", vul.expl);
            DrawResistCell("Laser", vul.laser);
            DrawResistCell("Plasma", vul.plasma);
            DrawResistCell("Spark / Shock", vul.spark);
            DrawResistCell("Acid", vul.acid);
            DrawResistCell("Cryo / Freeze", vul.cryo);
            GUILayout.EndVertical();

            GUILayout.Space(7);

            // Biological (4)
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(210));
            GUILayout.Label("<b>☣️ Biological (4)</b>", _subHeaderStyle);
            DrawResistCell("Venom", vul.venom);
            DrawResistCell("Poison", vul.poison);
            DrawResistCell("Bleed", vul.bleed);
            DrawResistCell("Fang / Bite", vul.fang);
            GUILayout.EndVertical();

            GUILayout.Space(7);

            // Special (3)
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(210));
            GUILayout.Label("<b>⚡ Special (3)</b>", _subHeaderStyle);
            DrawResistCell("EMP (Pulse)", vul.emp);
            DrawResistCell("Pink Cloud", vul.pink);
            DrawResistCell("Necrotic", vul.necro);
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(8);

            // ── Section 5: Locomotion, Physics & Telekinesis ─────────────────────
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label("<b>🏃 Locomotion, Physics & Psionics (Telekinesis)</b>", _headerStyle);
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();

            // Col 1: Movement & Jump
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(280));
            GUILayout.Label("<b>👟 Movement & Acrobatics</b>", _subHeaderStyle);
            DrawStatPair("Move Speed Mult:", $"x{charStats.allSpeedMult:0.00}");
            DrawStatPair("Run Speed Mult:", $"x{charStats.runSpeedMult:0.00}");
            DrawStatPair("Jump Velocity:", $"{charStats.jumpdy:0.1} m/s");
            DrawStatPair("Double Jump:", charStats.isDJ > 0 ? "<color=#55FF55>Unlocked</color>" : "<color=#888888>Locked</color>");
            DrawStatPair("Double Jump Vel:", $"{charStats.djumpdy:0.1} m/s");
            DrawStatPair("Wing Flight:", charStats.ableFly > 0 ? "<color=#55FF55>Yes</color>" : "<color=#888888>No</color>");
            DrawStatPair("Braking / Friction:", $"{charStats.tormoz:0.00}");
            DrawStatPair("Knockback Stability:", $"{charStats.knocked:0.00}");
            GUILayout.EndVertical();

            GUILayout.Space(10);

            // Col 2: Levitation & Spells
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(280));
            GUILayout.Label("<b>✨ Levitation & Spells</b>", _subHeaderStyle);
            DrawStatPair("Levitation:", charStats.levitOn > 0 ? "<color=#55FF55>Unlocked</color>" : "<color=#888888>Locked</color>");
            DrawStatPair("Levit Drain:", $"{charStats.levitDMana:0.1} mana/s");
            DrawStatPair("Levit Ascent Drain:", $"{charStats.levitDManaUp:0.1} mana/s");
            DrawStatPair("Teleportation:", charStats.portPoss > 0 ? "<color=#55FF55>Unlocked</color>" : "<color=#888888>Locked</color>");
            DrawStatPair("Spells Allowed:", charStats.spellsPoss > 0 ? "<color=#55FF55>Yes</color>" : "<color=#888888>No</color>");
            DrawStatPair("Mana Regen:", $"{(charStats.recMana * 100f):0.1f}%/s");
            DrawStatPair("Mana Cost Mult:", $"x{charStats.allDManaMult:0.00}");
            DrawStatPair("Warlock Mana Cost:", $"x{charStats.warlockDManaMult:0.00}");
            GUILayout.EndVertical();

            GUILayout.Space(10);

            // Col 3: Telekinesis
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(280));
            GUILayout.Label("<b>🌀 Telekinesis Ratings</b>", _subHeaderStyle);
            DrawStatPair("Max TK Mass:", $"<b>{charStats.maxTeleMassa:0.00} kg</b>");
            DrawStatPair("Max TK Range:", $"{Mathf.Sqrt(charStats.teleDist):0} px");
            DrawStatPair("TK Force Multiplier:", $"x{charStats.teleMult:0.00}");
            DrawStatPair("Throw Force:", $"{charStats.throwForce:0.00}");
            DrawStatPair("Throw Mana Drain:", $"{charStats.throwDmana:0} ({(charStats.throwDmanaMult * 100f):0}%)");
            DrawStatPair("Throw Magic Cost:", $"{charStats.throwDmagic:0}");
            DrawStatPair("Telemaster Rank:", $"Rank {charStats.telemaster}");
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(8);

            // ── Section 6: Exploration, Survival & Companions ────────────────────
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label("<b>🔍 Utility, Survival & Companions</b>", _headerStyle);
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();

            // Col 1: Utility Skills
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(425));
            GUILayout.Label("<b>🛠️ World & Interaction Skills</b>", _subHeaderStyle);
            DrawStatPair("Lockpicking:", $"Rank {charStats.lockPick} (Attemptable: {(charStats.possLockPick > 0 ? "<color=#55FF55>Yes</color>" : "<color=#888888>No</color>")})");
            DrawStatPair("Terminal Hacking:", $"Rank {charStats.hacker} (Master: {(charStats.hackerMaster > 0 ? "<color=#55FF55>Yes</color>" : "<color=#888888>No</color>")})");
            DrawStatPair("Repair Skill:", $"Rank {charStats.repair} (Efficiency: x{charStats.repairMult:0.00})");
            DrawStatPair("Trap Disarm / Remine:", charStats.remine > 0 ? "<color=#55FF55>Unlocked</color>" : "<color=#888888>Locked</color>");
            DrawStatPair("Barter:", $"Lvl {charStats.barterLvl} (Max Deal: {charStats.limitBuys:0} caps, Bonus: x{charStats.capsMult:0.00})");
            DrawStatPair("Stealth:", $"Mult: x{charStats.stealthMult:0.00} | Sneak: {charStats.sneak:0.0} | Noise: {charStats.noiseRun:0.0}");
            DrawStatPair("Medicine:", $"Heal: x{charStats.healMult:0.00} | Flat: +{charStats.bonusHeal:0} | Reanimation HP: {charStats.reanimHp:0}");
            GUILayout.EndVertical();

            GUILayout.Space(10);

            // Col 2: Companions
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(425));
            GUILayout.Label("<b>🤖 Companion Unit Stats</b>", _subHeaderStyle);
            GUILayout.Label("<b>Sprite Bot (Pet):</b>");
            DrawStatPair("  HP / Damage:", $"{charStats.petHP:0} HP  |  x{charStats.petDam:0.00} Dam");
            DrawStatPair("  Defense / Vuln:", $"+{charStats.petRes:0} Res, +{charStats.petSkin:0} Skin  |  x{charStats.petVulner:0.00} Vuln");
            GUILayout.Space(4);
            GUILayout.Label("<b>Calamity Owl Companion:</b>");
            DrawStatPair("  HP / Damage:", $"{charStats.owlHP:0} HP  |  x{charStats.owlDam:0.00} Dam");
            DrawStatPair("  Defense / Vuln:", $"+{charStats.owlSkin:0} Skin  |  x{charStats.owlVulner:0.00} Vuln");
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(8);

            // ── Section 7: Interactive Factor Inspector ──────────────────────────
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label("<b>🔬 RPG Factor Inspector (Contributing Perks & Modifiers)</b>", _headerStyle);
            GUILayout.Label("<color=#AAAAAA><size=11>Select a stat below to inspect every dynamic perk, skill, and system modifier recorded by CharacterStats.TrackFactor().</size></color>");
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            foreach (var statKey in InspectableStats)
            {
                bool isSelected = string.Equals(_inspectedStatFactor, statKey, StringComparison.OrdinalIgnoreCase);
                GUIStyle style = isSelected ? _tabActiveStyle : _tabInactiveStyle;
                if (GUILayout.Button(statKey, style, GUILayout.Height(22)))
                {
                    _inspectedStatFactor = statKey;
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            var factors = charStats.GetFactorsForStat(_inspectedStatFactor);
            if (factors != null && factors.Count > 0)
            {
                GUILayout.BeginVertical(_cardActiveStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label("<b>Source ID</b>", GUILayout.Width(200));
                GUILayout.Label("<b>Type</b>", GUILayout.Width(100));
                GUILayout.Label("<b>Delta / Factor</b>", GUILayout.Width(140));
                GUILayout.Label("<b>Accumulated Result</b>", GUILayout.Width(180));
                GUILayout.EndHorizontal();

                foreach (var factor in factors)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"<color=#55FFFF>{factor.sourceId}</color>", GUILayout.Width(200));
                    GUILayout.Label($"<color=#AAAAAA>{factor.sourceType}</color>", GUILayout.Width(100));
                    string deltaStr = factor.value >= 0 ? $"+{factor.value:0.00}" : $"{factor.value:0.00}";
                    GUILayout.Label($"<color=#FFFF55>{deltaStr}</color>", GUILayout.Width(140));
                    GUILayout.Label($"<b>{factor.result:0.00}</b>", GUILayout.Width(180));
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
            }
            else
            {
                GUILayout.Label($"<color=#888888>No dynamic factors currently modifying '{_inspectedStatFactor}'. Using default/base value.</color>");
            }

            GUILayout.EndVertical();

            GUILayout.Space(12);
            GUILayout.EndScrollView();
        }

        // =========================================================================
        // TAB 1: SKILLS
        // =========================================================================

        private void DrawSkillsTab(PlayerController player)
        {
            var charStats = player.CharacterStats;
            if (charStats == null)
            {
                GUILayout.Label("CharacterStats component missing on Player.");
                return;
            }

            // Top Summary & Cheats
            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label($"<b>Level:</b> {charStats.Level}", GUILayout.Width(80));
            if (GUILayout.Button("Level -1", GUILayout.Width(70))) charStats.SetLevel(Mathf.Max(1, charStats.Level - 1));
            if (GUILayout.Button("Level +1", GUILayout.Width(70))) charStats.SetLevel(charStats.Level + 1);
            if (GUILayout.Button("Level +5", GUILayout.Width(70))) charStats.SetLevel(charStats.Level + 5);

            GUILayout.Space(15);
            GUILayout.Label($"<b>Skill Points:</b> {charStats.SkillPoints}", GUILayout.Width(110));
            if (GUILayout.Button("+5 SP", GUILayout.Width(55))) charStats.GrantSkillPoints(5);
            if (GUILayout.Button("+20 SP", GUILayout.Width(60))) charStats.GrantSkillPoints(20);
            if (GUILayout.Button("Clear SP", GUILayout.Width(65))) charStats.SetSkillPoints(0);

            GUILayout.FlexibleSpace();
            GUILayout.Label("<b>Batch:</b>", GUILayout.Width(45));
            if (GUILayout.Button("All 0", GUILayout.Width(50))) SetAllSkills(charStats, 0);
            if (GUILayout.Button("All 10", GUILayout.Width(55))) SetAllSkills(charStats, 10);
            if (GUILayout.Button("All 20", GUILayout.Width(55))) SetAllSkills(charStats, 20);
            if (GUILayout.Button("All 100", GUILayout.Width(60))) SetAllSkills(charStats, 100);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            _skillsScroll = GUILayout.BeginScrollView(_skillsScroll);

            // Core 13 Skills (Cap 20)
            GUILayout.Label("<b>Core Skills (Cap 20)</b>", _subHeaderStyle);
            string[] coreSkills = new string[]
            {
                "smallguns", "melee", "energy", "explosives", "magic", "tele",
                "repair", "medic", "lockpick", "science", "sneak", "barter", "survival"
            };

            foreach (string skId in coreSkills)
            {
                DrawSkillRow(charStats, skId, 20);
            }

            GUILayout.Space(10);

            // Post-Game Skills (Cap 100)
            GUILayout.Label("<b>Post-Game Skills (Cap 100)</b>", _subHeaderStyle);
            string[] specialSkills = new string[] { "attack", "defense", "knowl" };
            foreach (string skId in specialSkills)
            {
                DrawSkillRow(charStats, skId, 100);
            }

            GUILayout.EndScrollView();
        }

        private void DrawSkillRow(CharacterStats charStats, string skillId, int maxLevel)
        {
            int curLevel = charStats.GetSkillLevel(skillId);
            int tier = charStats.GetSkillTier(skillId);

            GUILayout.BeginHorizontal(_cardStyle);

            string displayName = FormatSkillName(skillId);
            GUILayout.Label($"<b>{displayName}</b> <color=#888888>({skillId})</color>", GUILayout.Width(180));
            GUILayout.Label($"Tier {tier}", _badgeStyle, GUILayout.Width(55));

            int newLevel = Mathf.RoundToInt(GUILayout.HorizontalSlider(curLevel, 0, maxLevel, GUILayout.Width(220)));

            GUILayout.Label($"{curLevel}/{maxLevel}", GUILayout.Width(55));

            if (GUILayout.Button("-5", GUILayout.Width(30))) newLevel = Mathf.Max(0, curLevel - 5);
            if (GUILayout.Button("-1", GUILayout.Width(30))) newLevel = Mathf.Max(0, curLevel - 1);
            if (GUILayout.Button("+1", GUILayout.Width(30))) newLevel = Mathf.Min(maxLevel, curLevel + 1);
            if (GUILayout.Button("+5", GUILayout.Width(30))) newLevel = Mathf.Min(maxLevel, curLevel + 5);
            if (GUILayout.Button("Max", GUILayout.Width(42))) newLevel = maxLevel;
            if (GUILayout.Button("0", GUILayout.Width(25))) newLevel = 0;

            if (newLevel != curLevel)
            {
                charStats.SetSkillLevel(skillId, newLevel);
            }

            GUILayout.EndHorizontal();
        }

        private void SetAllSkills(CharacterStats charStats, int val)
        {
            foreach (string skId in charStats.GetAllSkillIds())
            {
                int max = CharacterStats.IsSkillPost(skId) ? 100 : 20;
                charStats.SetSkillLevel(skId, Mathf.Clamp(val, 0, max));
            }
        }

        private string FormatSkillName(string id)
        {
            switch (id.ToLowerInvariant())
            {
                case "tele": return "Telekinesis";
                case "smallguns": return "Small Guns";
                case "energy": return "Energy Weapons";
                case "explosives": return "Explosives";
                case "melee": return "Melee & Unarmed";
                case "magic": return "Magic / Horn";
                case "repair": return "Repair & Craft";
                case "medic": return "Medicine";
                case "lockpick": return "Lockpicking";
                case "science": return "Science & Terminals";
                case "sneak": return "Stealth & Sneak";
                case "barter": return "Barter & Trading";
                case "survival": return "Survival & Skin";
                case "attack": return "Total Attack Power";
                case "defense": return "Total Defense Resist";
                case "knowl": return "Knowledge & Perks";
                default: return id;
            }
        }

        // =========================================================================
        // TAB 1: PERKS
        // =========================================================================

        private void DrawPerksTab(PlayerController player)
        {
            var charStats = player.CharacterStats;
            if (charStats == null) return;

            // Search & Options Bar
            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label("Search:", GUILayout.Width(50));
            _perkSearch = GUILayout.TextField(_perkSearch, GUILayout.Width(180));
            if (!string.IsNullOrEmpty(_perkSearch) && GUILayout.Button("✕", GUILayout.Width(24)))
            {
                _perkSearch = string.Empty;
            }

            GUILayout.Space(10);
            GUILayout.Label("Filter:", GUILayout.Width(40));
            string[] filters = { "All", "Unlocked Only", "Locked Only" };
            _perkFilterMode = GUILayout.Toolbar(_perkFilterMode, filters, GUILayout.Width(220), GUILayout.Height(22));

            _ignorePerkPrereqs = GUILayout.Toggle(_ignorePerkPrereqs, "Ignore Prereqs (Cheat)", GUILayout.Width(160));

            GUILayout.FlexibleSpace();
            GUILayout.Label($"<b>Perk Points:</b> {charStats.PerkPoints}", GUILayout.Width(105));
            if (GUILayout.Button("+5 PP", GUILayout.Width(55))) charStats.GrantPerkPoints(5);
            GUILayout.EndHorizontal();

            // Batch actions
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("🌟 Unlock Max Rank for All Perks", GUILayout.Height(24)))
            {
                UnlockAllPerks(charStats);
            }
            if (GUILayout.Button("🚫 Clear All Unlocked Perks", GUILayout.Height(24)))
            {
                charStats.ClearAllPerks();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            _perksScroll = GUILayout.BeginScrollView(_perksScroll);

            if (_allPerks == null || _allPerks.Length == 0)
            {
                LoadCatalogs();
            }

            if (_allPerks != null && _allPerks.Length > 0)
            {
                int drawnCount = 0;
                foreach (var perk in _allPerks)
                {
                    if (perk == null) continue;

                    string id = perk.PerkId;
                    string name = !string.IsNullOrEmpty(perk.DisplayName) ? perk.DisplayName : id;
                    string desc = perk.Description ?? string.Empty;

                    int rank = charStats.GetPerkRank(id);
                    int maxRanks = Mathf.Max(1, perk.MaxRank);

                    // Filter mode
                    if (_perkFilterMode == 1 && rank <= 0) continue;
                    if (_perkFilterMode == 2 && rank > 0) continue;

                    // Text search
                    if (!string.IsNullOrEmpty(_perkSearch))
                    {
                        bool match = id.IndexOf(_perkSearch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf(_perkSearch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     desc.IndexOf(_perkSearch, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!match) continue;
                    }

                    drawnCount++;
                    DrawPerkCard(charStats, perk, rank, maxRanks);
                }

                if (drawnCount == 0)
                {
                    GUILayout.Label("No perks matched the current search filter.");
                }
            }
            else
            {
                GUILayout.Label("No PerkDefinitions found in SkillDefinitionDatabase asset.");
            }

            GUILayout.EndScrollView();
        }

        private void DrawPerkCard(CharacterStats charStats, PerkDefinition perk, int curRank, int maxRanks)
        {
            GUIStyle card = curRank > 0 ? _cardActiveStyle : _cardStyle;
            GUILayout.BeginVertical(card);

            GUILayout.BeginHorizontal();
            string statusColor = curRank > 0 ? "#55FF55" : "#AAAAAA";
            string title = $"<color={statusColor}><b>{perk.DisplayName}</b></color> <color=#888888>({perk.PerkId})</color>";
            GUILayout.Label(title, GUILayout.ExpandWidth(true));

            GUILayout.Label($"Rank: <b>{curRank}/{maxRanks}</b>", _badgeStyle, GUILayout.Width(90));

            if (GUILayout.Button("-", GUILayout.Width(26)))
            {
                charStats.SetPerkRank(perk.PerkId, Mathf.Max(0, curRank - 1));
            }
            if (GUILayout.Button("+", GUILayout.Width(26)))
            {
                if (_ignorePerkPrereqs || perk.CanUnlock(charStats, curRank))
                {
                    charStats.SetPerkRank(perk.PerkId, Mathf.Min(maxRanks, curRank + 1));
                }
            }
            if (GUILayout.Button("Max", GUILayout.Width(40)))
            {
                charStats.SetPerkRank(perk.PerkId, maxRanks);
            }
            if (GUILayout.Button("Clear", GUILayout.Width(45)))
            {
                charStats.RemovePerk(perk.PerkId);
            }
            GUILayout.EndHorizontal();

            // Description
            if (!string.IsNullOrEmpty(perk.Description))
            {
                GUILayout.Label($"<color=#CCCCCC><size=11>{perk.Description}</size></color>");
            }

            GUILayout.EndVertical();
            GUILayout.Space(2);
        }

        private void UnlockAllPerks(CharacterStats charStats)
        {
            if (_allPerks == null) return;
            foreach (var p in _allPerks)
            {
                if (p != null && !string.IsNullOrEmpty(p.PerkId))
                {
                    charStats.SetPerkRank(p.PerkId, Mathf.Max(1, p.MaxRank));
                }
            }
        }

        // =========================================================================
        // TAB 2: WEAPONS
        // =========================================================================

        private void DrawWeaponsTab(PlayerController player)
        {
            var loadout = player.GetComponent<PlayerWeaponLoadout>();
            if (loadout == null)
            {
                GUILayout.Label("PlayerWeaponLoadout component missing on Player.");
                return;
            }

            // Current Weapon Header
            var curController = loadout.Current;
            var curDef = curController?.State?.Def;
            string curId = curDef?.weaponId ?? "Unarmed";

            GUILayout.BeginHorizontal(_cardActiveStyle);
            GUILayout.Label($"<b>Equipped Weapon:</b> {curId} <color=#AAAAAA>({curDef?.weaponType.ToString() ?? "None"})</color>", GUILayout.Width(350));
            if (curController?.State != null)
            {
                GUILayout.Label($"Ammo: <b>{curController.State.CurrentAmmo}/{curDef.magazineSize}</b>", GUILayout.Width(120));
                GUILayout.Label($"Durability: <b>{curController.State.CurrentDurability}</b>", GUILayout.Width(130));
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refill Ammo", GUILayout.Width(95)))
            {
                if (curController?.State != null)
                {
                    curController.State.CurrentAmmo = curDef.magazineSize;
                    curController.State.SyncReactive();
                }
            }
            if (GUILayout.Button("Repair 100%", GUILayout.Width(95)))
            {
                if (curController?.State != null)
                {
                    curController.State.CurrentDurability = 1000;
                    curController.State.SyncReactive();
                }
            }
            if (GUILayout.Button("Unequip", GUILayout.Width(75)))
            {
                // Equip unarmed or clear
                var unarmed = _allWeapons?.FirstOrDefault(w => w.weaponType == WeaponType.Melee && w.meleeType == MeleeType.Horizontal);
                if (unarmed != null) loadout.Equip(unarmed);
            }
            GUILayout.EndHorizontal();

            // ── Ammo type row ─────────────────────────────────────────────────
            DrawAmmoTypeRow(curController, curDef);

            GUILayout.Space(6);

            // ── Filter row 1: search + match count ────────────────────────────
            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label("Search:", GUILayout.Width(50));
            _weaponSearch = GUILayout.TextField(_weaponSearch, GUILayout.Width(200));
            if (!string.IsNullOrEmpty(_weaponSearch) && GUILayout.Button("✕", GUILayout.Width(24)))
            {
                _weaponSearch = string.Empty;
            }

            GUILayout.FlexibleSpace();

            // "N / M" so a filter is visibly doing something. The old single-row layout pushed the 8th
            // category button past the 920px window edge, so the last category was silently unreachable.
            if (_allWeapons != null && _allWeapons.Length > 0)
            {
                int shown = 0;
                foreach (var w in _allWeapons)
                {
                    if (MatchesWeaponCategory(w, _weaponCategoryFilter) && MatchesWeaponSearch(w, _weaponSearch))
                    {
                        shown++;
                    }
                }
                GUILayout.Label($"<color=#AAAAAA>{shown} / {_allWeapons.Length}</color>", GUILayout.Width(80));
            }
            GUILayout.EndHorizontal();

            // ── Filter row 2: AS3's six skill categories (+ All) ──────────────
            // On its own row, so the seven buttons never compete with the search field for width and
            // none can be clipped off the window. Order is AS3's (w1,w2,w4,w5,w6,w3).
            GUILayout.BeginHorizontal(_cardStyle);
            _weaponCategoryFilter = GUILayout.Toolbar(
                _weaponCategoryFilter, WeaponCategoryLabels, GUILayout.Height(22));
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            _weaponsScroll = GUILayout.BeginScrollView(_weaponsScroll);

            if (_allWeapons == null || _allWeapons.Length == 0)
            {
                LoadCatalogs();
            }

            if (_allWeapons != null && _allWeapons.Length > 0)
            {
                int count = 0;
                foreach (var weapon in _allWeapons)
                {
                    if (weapon == null) continue;

                    string wid = weapon.weaponId ?? weapon.name;

                    // Category filter (AS3 skill grouping) + free-text search. Both go through the same
                    // helpers the "N / M" count above uses, so the number and the list cannot disagree.
                    if (!MatchesWeaponCategory(weapon, _weaponCategoryFilter)) continue;
                    if (!MatchesWeaponSearch(weapon, _weaponSearch)) continue;

                    count++;
                    DrawWeaponRow(loadout, weapon, wid == curId);
                }

                if (count == 0)
                {
                    GUILayout.Label("No weapons match the current filter.");
                }
            }
            else
            {
                GUILayout.Label("No WeaponDefinition assets found in Resources/Weapons.");
            }

            GUILayout.EndScrollView();
        }

        /// <summary>
        /// The ammo-type dropdown for the equipped weapon.
        ///
        /// <para><b>What "swap ammo" means here.</b> Ammo ids form families — <c>p32</c> is the regular
        /// round and <c>p32_1</c>/<c>p32_2</c> are its variants — grouped by
        /// <see cref="AmmoFamilyResolver"/>. The dropdown lists only the equipped weapon's own family
        /// (usually 1-4 entries, not all 75 rows), preselects the weapon's current type, and on change
        /// writes a per-instance override onto the weapon state.</para>
        ///
        /// <para><b>Why an override rather than editing the definition.</b> <see cref="WeaponDefinition"/>
        /// is one shared asset per weapon: writing to it would change that weapon for every wielder and,
        /// in the editor, survive leaving play mode. The override lives on
        /// <c>WeaponRuntimeState.AmmoTypeOverride</c> and is read through
        /// <c>ResolvedAmmoType</c> by every reload/recycle decision.</para>
        ///
        /// <para><b>No dropdown for weapons that take no ammo.</b> 134 of the 213 weapon assets have a
        /// blank <c>ammoType</c> (melee, magic, unarmed). Those get a greyed label instead — an empty
        /// dropdown would read as a bug, and they never reload anyway (<c>magazineSize</c> 0).</para>
        /// </summary>
        private void DrawAmmoTypeRow(IWeaponController curController, IWeaponStats curDef)
        {
            if (curController?.State == null || curDef == null)
                return;

            GUILayout.BeginHorizontal(_cardStyle);

            GUILayout.Label("<b>Ammo Type</b>", GUILayout.Width(90));

            // The weapon's ORIGINAL type decides whether it takes ammo at all. Reading the resolved type
            // here would let a swap on a melee weapon invent an ammo type for something that has none.
            if (string.IsNullOrEmpty(curDef.ammoType))
            {
                Color prev = GUI.color;
                GUI.color = new Color(0.6f, 0.6f, 0.6f, 1f);
                GUILayout.Label("— (this weapon takes no ammo)", GUILayout.Width(240));
                GUI.color = prev;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                return;
            }

            WeaponRuntimeState state = curController.State;
            string resolved = state.ResolvedAmmoType;

            // Rebuild only when the weapon's resolved type changed — an IMGUI frame must not re-sort a
            // list or re-format labels (those allocate, and this is the debug overlay's hot path).
            if (_ammoDropdownIds == null
                || !string.Equals(_ammoDropdownForAmmoType, resolved, StringComparison.Ordinal))
            {
                var ids = AmmoFamilyResolver.GetSelectableTypes(
                    resolved, ResolveAmmo, () => (IEnumerable<string>)_allAmmoIds);

                _ammoDropdownIds = ids.ToArray();
                _ammoDropdownLabels = new string[_ammoDropdownIds.Length];
                for (int i = 0; i < _ammoDropdownIds.Length; i++)
                    _ammoDropdownLabels[i] = AmmoFamilyResolver.Describe(_ammoDropdownIds[i], ResolveAmmo);

                _ammoDropdownForAmmoType = resolved;
                _ammoDropdownFamily = AmmoFamilyResolver.FamilyOf(resolved, ResolveAmmo);

                _ammoDropdownIndex = Array.IndexOf(_ammoDropdownIds, resolved);
                if (_ammoDropdownIndex < 0) _ammoDropdownIndex = 0;
            }

            int picked = GUILayout.SelectionGrid(
                _ammoDropdownIndex, _ammoDropdownLabels, 1, _badgeStyle, GUILayout.Width(360));

            if (picked != _ammoDropdownIndex && picked >= 0 && picked < _ammoDropdownIds.Length)
            {
                _ammoDropdownIndex = picked;

                // SetAmmoTypeOverride nulls the field when the pick equals the definition's own type, so
                // choosing "regular" genuinely clears the override instead of storing a copy of it.
                state.SetAmmoTypeOverride(_ammoDropdownIds[picked]);

                // The magazine may hold rounds of the previous type. Refilling here would hand the player
                // rounds they did not load; leaving it is what the AS3 model does too (the mag keeps what
                // is in it until a reload). So: nothing to do beyond making the state truthful.
                _ammoDropdownForAmmoType = null; // force the label refresh on the next frame
            }

            GUILayout.Space(8);

            string badge = state.HasAmmoTypeOverride
                ? $"<color=#FFD24A>override → {resolved}</color>"
                : $"<color=#7FE07F>regular ({resolved})</color>";
            GUILayout.Label(badge, GUILayout.Width(190));

            GUILayout.FlexibleSpace();

            // Give ammo into the live inventory (the player's own PlayerInventory — see EnsureInventory).
            if (GUILayout.Button("Give 30", GUILayout.Width(70)))
                GiveAmmo(state.ResolvedAmmoType, 30);
            if (GUILayout.Button("Give 300", GUILayout.Width(80)))
                GiveAmmo(state.ResolvedAmmoType, 300);

            GUILayout.EndHorizontal();

            // Second line: the inventory readout and the "back to regular" affordance. Kept separate so the
            // first row stays readable at the window's default width.
            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label("<color=#AAAAAA>Inventory:</color>", GUILayout.Width(90));

            if (_debugInventory == null)
            {
                GUILayout.Label("<color=#AAAAAA>no inventory resolved yet — press \"Give 30\"</color>");
            }
            else
            {
                int held = _debugInventory.GetAmmoCount(state.ResolvedAmmoType);
                GUILayout.Label($"holds <b>{held}</b> × {state.ResolvedAmmoType}", GUILayout.Width(220));
            }

            GUILayout.FlexibleSpace();

            // Say up front whether this ammo type can be stocked at all. 47 of the 75 ammo ids have no
            // ItemDefinition row (the item importer keeps only the component rows), so AddItem has nothing
            // to key on for them — without this the "Give" button would look broken rather than blocked.
            if (ResolveAmmoItem(state.ResolvedAmmoType) == null)
            {
                GUILayout.Label("<color=#FF9A6A>no item row — cannot be stocked</color>", GUILayout.Width(230));
            }

            if (state.HasAmmoTypeOverride && GUILayout.Button("Reset to regular", GUILayout.Width(130)))
            {
                state.SetAmmoTypeOverride(null);
                _ammoDropdownForAmmoType = null;
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Add <paramref name="amount"/> rounds of <paramref name="ammoId"/> to the player's inventory,
        /// adopting the live one when the game has one and creating a stand-in when it does not.
        ///
        /// <para><b>Since 2026-10-05 the running game does own an inventory</b> —
        /// <c>PlayerController</c> creates a <see cref="PlayerInventory"/>, which builds the
        /// <see cref="GameInventory"/> and points <c>PlayerWeaponLoadout.AmmoSource</c> at itself (see
        /// <see cref="PlayerInventory"/> and <see cref="EnsureInventory"/>). So in a normal play session
        /// this method stocks the same bag a real pickup feeds, and reloads draw from it instead of
        /// filling for free. The earlier "nothing constructs one — only tests do" note described the
        /// state before that wiring and is kept only as history.</para>
        ///
        /// <para><b>The mutation goes through the command seam when a live inventory exists</b>, i.e.
        /// <see cref="PlayerInventory.Submit"/> with an <see cref="InventoryCommand.AddItem"/> — the
        /// identical path a real pickup will take, so pressing "Give" exercises the seam rather than
        /// poking the bag behind it. The direct <c>AddItem</c> below is reached only for a bag this
        /// overlay created itself (no player in the scene) or a live inventory that never got a sink
        /// (no content registry injected), both of which are the pre-wiring situation.</para>
        ///
        /// <para><b>A rejection from the seam is reported, not papered over.</b> When the seam rejects,
        /// this returns false and logs the sink's reason; it deliberately does <i>not</i> retry the
        /// direct <c>AddItem</c> as a fallback. Falling back would make the button look like it worked
        /// while the authority refused — the exact "success that is not evidence" shape this project
        /// keeps paying for — and under host-authoritative co-op a client whose request the host rejects
        /// must not quietly apply it locally. The one realistic way to see this rejection in a play
        /// session is pressing "Give" before <c>GameDatabase.Initialize()</c> has populated the content
        /// registry; the warning names that id so the cause is readable rather than mysterious.</para>
        ///
        /// <para><b>Ammo is stored under the AmmoDefinition id.</b> <c>GameInventory</c> keys items by id
        /// and its <c>GetAmmoCount</c>/<c>ConsumeAmmo</c> look up exactly the id the weapon resolves, so
        /// the two ends agree by construction — this is the same key
        /// <c>RangedWeaponController.CompleteReload</c> passes.</para>
        /// </summary>
        private void GiveAmmo(string ammoId, int amount)
        {
            if (string.IsNullOrEmpty(ammoId)) return;

            if (!GiveAmmoToInventory(ammoId, amount))
                Debug.LogWarning($"[PlayerDebugEditorOverlay] Could not give '{ammoId}' — see the warning above.");
        }

        /// <summary>
        /// The public seam behind <see cref="GiveAmmo"/>, so the console verb reaches the same inventory
        /// as the button instead of growing a second one. Returns false (having logged the reason) when
        /// there is nothing to give.
        /// </summary>
        public bool GiveAmmoToInventory(string ammoId, int amount)
        {
            if (string.IsNullOrEmpty(ammoId) || amount <= 0) return false;

            EnsureInventory();

            if (_debugInventory == null)
            {
                Debug.LogWarning("[PlayerDebugEditorOverlay] Could not create an inventory to give ammo to.");
                return false;
            }

            ItemDefinition item = ResolveAmmoItem(ammoId);
            if (item == null)
            {
                Debug.LogWarning(
                    $"[PlayerDebugEditorOverlay] No ItemDefinition row for ammo '{ammoId}', so there is " +
                    "nothing to add to the inventory. (AddItem keys on the item row under Resources/Items, " +
                    "not the AmmoDefinition.)");
                return false;
            }

            // Preferred path: the live player inventory's command seam. Same route a real pickup takes,
            // so this button proves the seam works rather than proving a debug button can poke a bag.
            // The sink re-resolves the id from the content registry; `ammoId` is the row's own itemId
            // (that is how ResolveAmmoItem keyed it), so both ends name the same row.
            if (_liveInventory != null && _liveInventory.Commands != null)
            {
                InventoryCommandResult result = _liveInventory.Submit(InventoryCommand.AddItem(ammoId, amount));
                if (!result.Applied)
                {
                    Debug.LogWarning($"[PlayerDebugEditorOverlay] The inventory command seam rejected " +
                                     $"'{ammoId}' ×{amount}: {result.Reason}");
                    return false;
                }

                Debug.Log($"[PlayerDebugEditorOverlay] Gave {amount} × '{ammoId}' through the inventory " +
                          $"command seam (now holding {_debugInventory.GetAmmoCount(ammoId)}).");
                return true;
            }

            // Fallback: a bag this overlay created itself (no player in the scene), or a live inventory
            // that never received a sink because no content registry was injected. Both are the
            // pre-wiring situation, and the direct call is the only option left.
            bool added = _debugInventory.AddItem(item, amount);

            Debug.Log($"[PlayerDebugEditorOverlay] Gave {amount} × '{ammoId}' directly " +
                      $"(now holding {_debugInventory.GetAmmoCount(ammoId)}).");

            return added;
        }

        /// <summary>
        /// Resolve the inventory this overlay reads and stocks, adopting the live one when the game has
        /// it and creating a stand-in only when it does not.
        ///
        /// <para><b>Order: adopt the player's <see cref="PlayerInventory"/>, then any other supplied
        /// source, then create.</b> Since 2026-10-05 <c>PlayerController</c> creates a
        /// <see cref="PlayerInventory"/> for the player, so the first arm is the normal path and the
        /// create arm is the bare-scene fallback. See the inline note for why the first arm must come
        /// before the <c>is GameInventory</c> one.</para>
        ///
        /// <para><b>In the create fallback, assigning <c>loadout.AmmoSource</c> is the whole point</b> —
        /// its setter rebuilds the controller factory so subsequently-equipped weapons pick the source
        /// up. A weapon equipped <i>before</i> that call keeps the factory it was built with, so the
        /// change takes effect from the next equip (or the next overlay action). The overlay does not
        /// force a re-equip, because silently swapping the player's weapon to make a debug button take
        /// effect is worse than saying so.</para>
        ///
        /// <para><b>Only 28 of the 75 ammo ids actually have such a row.</b> Measured 2026-10-03: the
        /// intersection of the 75 <c>AmmoDefinition</c> ids with the 451 <c>ItemDefinition</c> ids is
        /// <b>28</b>, and those 28 are exactly the <c>compw</c>/<c>stuff</c> component rows the item
        /// importer keeps. Every <c>tip='a'</c> proper ammunition id (<c>batt</c>, <c>p10</c>, <c>p32</c>,
        /// <c>p556</c>, … — 47 of them) has no item row at all. So "Give" works for component-fed weapons
        /// and reports a named warning for the rest; see the follow-up task covering the importer.</para>
        /// </summary>
        private void EnsureInventory()
        {
            if (_debugInventory != null) return;

            var player = FindFirstObjectByType<PlayerController>();
            var loadout = player != null ? player.GetComponent<PlayerWeaponLoadout>() : null;

            // ADOPT BEFORE CREATING. The first version of this method constructed the inventory first
            // and only then asked the loadout whether one already existed — so the `is GameInventory
            // existing` arm could never be reached (the new object had already been constructed, and
            // `loadout.AmmoSource == null` was tested against a null source that had been null all
            // along). The adopt arm is the one that must run when a source exists; the create arm is
            // the fallback. Ordering them the other way silently shadowed the live source.
            //
            // FIRST ARM IS THE PLAYER'S OWN PlayerInventory. Since 2026-10-05 that component is the
            // real runtime owner of the bag (PlayerController creates it; see PlayerInventory). It must
            // be tried before the `is GameInventory` arm below, because `loadout.AmmoSource` is now the
            // PlayerInventory wrapper rather than a bare GameInventory — that cast misses it, so without
            // this arm the method would build a SECOND, competing bag, wire the loadout to that, and
            // leave the overlay reading an empty inventory while the player's own stayed untouched.
            // That is the same silent shadowing the ordering note above warns about, one level up.
            if (player != null)
            {
                var live = player.GetComponent<PlayerInventory>();
                if (live != null && live.Inventory != null)
                {
                    _liveInventory = live;
                    _debugInventory = live.Inventory;
                    return;
                }
            }

            if (loadout?.AmmoSource is GameInventory existing)
            {
                // A source supplied without a PlayerInventory — a test harness, or the pre-wiring
                // situation. Adopt it rather than shadowing it, or "Give" would add to a bag nothing
                // reads — the button would look like it worked while the weapon stayed empty.
                _debugInventory = existing;
                return;
            }

            _debugInventory = new GameInventory();

            if (loadout == null)
            {
                // No player/loadout in the scene (bare test scene). The inventory still exists, so
                // "Give" is honest about what it added, but nothing consumes it — say so rather than
                // letting the player wonder why reloading still fills for free.
                Debug.LogWarning("[PlayerDebugEditorOverlay] Created a debug inventory, but there is no " +
                                 "PlayerWeaponLoadout in the scene to wire it to, so reloads will keep " +
                                 "filling for free.");
                return;
            }

            loadout.AmmoSource = _debugInventory;
            Debug.Log("[PlayerDebugEditorOverlay] Created a debug inventory and wired it to the " +
                      "player's loadout. Reloads will now consume ammo instead of filling for free.");
        }

        /// <summary>
        /// Whether <paramref name="ammoId"/> has an <see cref="ItemDefinition"/> row, i.e. whether the
        /// debug inventory can stock it at all. Exposed for the console verb so both front ends give the
        /// same answer — measured 2026-10-03, the answer is "no" for 47 of the 75 ids.
        /// </summary>
        public bool AmmoRowExists(string ammoId) => ResolveAmmoItem(ammoId) != null;

        /// <summary>
        /// Whether <paramref name="w"/> belongs to category <paramref name="catIndex"/>.
        ///
        /// <para><b>The category is the AS3 <c>skill</c> id, not the weapon type.</b> AS3's Pip-Boy
        /// weapons tab sets its six buttons to <c>["","w1","w2","w4","w5","w6","w3"]</c>
        /// (<c>PipPageInv.as:33</c>) — literally <c>"w" + weapon.skill</c> — and
        /// <c>PipPageInv.as:100</c> folds tele (<c>skill</c> 7) into the magic button. Skill id → name is
        /// <c>Pers.getWeapLevel</c> (<c>Pers.as:1073-1103</c>).</para>
        ///
        /// <para><b>Why not the old heuristic.</b> The previous version guessed from
        /// <c>weaponId.Contains(...)</c> ("10" for pistols, …). That put <c>smg10</c> in Pistols, missed
        /// the real pistols <c>p9mm</c>/<c>r375</c>/<c>revo</c>, and swept 48 weapons into "Heavy".
        /// <see cref="WeaponDefinition.skillLevel"/> already holds the AS3 <c>skill</c> id
        /// (<c>WeaponDataImporter.cs:250</c>), so the split is exact.</para>
        ///
        /// <para>Weapons with no <c>skill</c> (the 62 enemy/internal assets) match only "All" — in AS3
        /// their <c>"w" + undefined</c> equals no category button either.</para>
        /// </summary>
        private bool MatchesWeaponCategory(WeaponDefinition w, int catIndex)
        {
            if (catIndex <= 0) return true;                        // 0 = All
            if (w == null) return false;
            if (catIndex >= WeaponCategorySkill.Length) return true;

            return As3SkillOf(w) == WeaponCategorySkill[catIndex];
        }

        /// <summary>
        /// AS3's <c>weapon.skill</c> id, with tele folded into magic — <c>PipPageInv.as:100</c> maps
        /// <c>w7</c> to <c>w6</c>, so telekinetic weapons show under Magic rather than nowhere.
        /// </summary>
        private static int As3SkillOf(WeaponDefinition w)
        {
            int skill = w != null ? w.skillLevel : 0;
            return skill == 7 ? 6 : skill;
        }

        /// <summary>
        /// The AS3 category label for a weapon (tele folded into Magic); empty for the skill-0
        /// enemy/internal weapons, which have no player-facing category.
        /// </summary>
        private static string CategoryLabelFor(WeaponDefinition w)
        {
            int skill = As3SkillOf(w);
            for (int i = 1; i < WeaponCategorySkill.Length; i++)
            {
                if (WeaponCategorySkill[i] == skill) return WeaponCategoryLabels[i];
            }
            return string.Empty;
        }

        /// <summary>
        /// Whether <paramref name="w"/> matches the free-text search. Shared by the row loop and the
        /// "N / M" count so the two cannot disagree; it also matches the AS3 category label, so typing
        /// "explosives" finds the grenades and mines.
        /// </summary>
        private bool MatchesWeaponSearch(WeaponDefinition w, string query)
        {
            if (w == null) return false;
            if (string.IsNullOrEmpty(query)) return true;

            string wid = w.weaponId ?? w.name;
            return wid.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || w.weaponType.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || CategoryLabelFor(w).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DrawWeaponRow(PlayerWeaponLoadout loadout, WeaponDefinition weapon, bool isEquipped)
        {
            GUIStyle card = isEquipped ? _cardActiveStyle : _cardStyle;
            GUILayout.BeginHorizontal(card);

            string title = isEquipped ? $"<color=#55FF55><b>▶ {weapon.weaponId}</b></color>" : $"<b>{weapon.weaponId}</b>";
            GUILayout.Label(title, GUILayout.Width(170));
            GUILayout.Label(weapon.weaponType.ToString(), _badgeStyle, GUILayout.Width(75));
            // AS3 skill category (the axis the filter buttons use) — "—" for the skill-0 enemy/internal
            // weapons, which belong to no player category. Shown next to the class so the two are not
            // confused: weaponType is AS3 `tip` (class), this is AS3 `skill` (Pip-Boy category).
            string cat = CategoryLabelFor(weapon);
            GUILayout.Label(string.IsNullOrEmpty(cat) ? "—" : cat, _badgeStyle, GUILayout.Width(85));
            GUILayout.Label($"Dmg: <b>{weapon.baseDamage}</b>", GUILayout.Width(70));
            GUILayout.Label($"Mag: <b>{weapon.magazineSize}</b>", GUILayout.Width(65));
            GUILayout.Label($"DType: <b>{weapon.damageType}</b>", GUILayout.Width(110));

            GUILayout.FlexibleSpace();

            if (isEquipped)
            {
                GUI.color = Color.green;
                GUILayout.Box("EQUIPPED", GUILayout.Width(90), GUILayout.Height(22));
                GUI.color = Color.white;
            }
            else
            {
                if (GUILayout.Button("Equip", GUILayout.Width(90), GUILayout.Height(22)))
                {
                    loadout.Equip(weapon);
                }
            }

            GUILayout.EndHorizontal();
        }

        // =========================================================================
        // TAB 3: ARMOR
        // =========================================================================

        private void DrawArmorTab(PlayerController player)
        {
            var unitStats = player.Stats;
            if (unitStats == null) return;

            string curArmorId = unitStats.ArmourId?.Value ?? string.Empty;
            bool hasArmor = !string.IsNullOrEmpty(curArmorId);

            // Current Armor Header
            GUILayout.BeginHorizontal(_cardActiveStyle);
            GUILayout.Label($"<b>Equipped Armor:</b> {(hasArmor ? curArmorId : "None (Stripped)")}", GUILayout.Width(350));
            if (hasArmor)
            {
                GUILayout.Label($"Integrity: <b>{unitStats.ArmourIntegrity.Value * 100f:0.#}%</b>", GUILayout.Width(140));
            }
            GUILayout.FlexibleSpace();
            if (hasArmor && GUILayout.Button("Repair 100%", GUILayout.Width(100)))
            {
                // Re-equip full integrity
                EquipArmorById(player, curArmorId);
            }
            if (hasArmor && GUILayout.Button("Strip / Unequip", GUILayout.Width(110)))
            {
                unitStats.UnequipArmour();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // Search & Category Toolbar
            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label("Search:", GUILayout.Width(50));
            _armorSearch = GUILayout.TextField(_armorSearch, GUILayout.Width(180));
            if (!string.IsNullOrEmpty(_armorSearch) && GUILayout.Button("✕", GUILayout.Width(24)))
            {
                _armorSearch = string.Empty;
            }

            GUILayout.Space(10);
            string[] modes = { "All 20 Visual Sets (Recommended)", "All Inventory Armor Items" };
            _armorFilterMode = GUILayout.Toolbar(_armorFilterMode, modes, GUILayout.Height(22));
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            _armorScroll = GUILayout.BeginScrollView(_armorScroll);

            if (_armorFilterMode == 0)
            {
                // 20 Visual sets from PlayerAnimationDefinition
                GUILayout.Label("<b>Authored Paper-Doll Armor Sets (Full Character Sprite Overrides)</b>", _subHeaderStyle);

                foreach (string armorId in VisualArmorIds)
                {
                    if (!string.IsNullOrEmpty(_armorSearch) &&
                        armorId.IndexOf(_armorSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    // Pass the resolved definition so the row shows the real tip/HP badge instead of
                    // a bare "Body Set" — the id alone cannot tell a 20000-HP plate from an amulet.
                    DrawArmorRow(player, armorId, armorId == curArmorId, ResolveArmorDefinition(armorId));
                }
            }
            else
            {
                // All ItemDefinitions that represent armor
                if (_allArmorItems == null || _allArmorItems.Length == 0) LoadCatalogs();

                if (_allArmorItems != null && _allArmorItems.Length > 0)
                {
                    foreach (var item in _allArmorItems)
                    {
                        if (item == null) continue;
                        string id = item.itemId ?? item.name;

                        if (!string.IsNullOrEmpty(_armorSearch) &&
                            id.IndexOf(_armorSearch, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        DrawArmorRow(player, id, id == curArmorId, item);
                    }
                }
            }

            GUILayout.EndScrollView();
        }

        private void DrawArmorRow(PlayerController player, string armorId, bool isEquipped, ItemDefinition def = null)
        {
            GUIStyle card = isEquipped ? _cardActiveStyle : _cardStyle;
            GUILayout.BeginHorizontal(card);

            string title = isEquipped ? $"<color=#55FF55><b>▶ {armorId}</b></color>" : $"<b>{armorId}</b>";
            GUILayout.Label(title, GUILayout.Width(180));

            string tipText = def != null ? (def.armorTip == 3 ? "Amulet" : "Body Armor") : "Body Set";
            GUILayout.Label(tipText, _badgeStyle, GUILayout.Width(80));

            if (def != null && def.armorHP > 0)
            {
                GUILayout.Label($"HP: <b>{def.armorHP}</b>", GUILayout.Width(70));
            }
            if (def != null && def.armorHideMane)
            {
                GUILayout.Label("Hides Mane", _badgeStyle, GUILayout.Width(80));
            }

            GUILayout.FlexibleSpace();

            if (isEquipped)
            {
                GUI.color = Color.green;
                GUILayout.Box("EQUIPPED", GUILayout.Width(95), GUILayout.Height(22));
                GUI.color = Color.white;
            }
            else
            {
                if (GUILayout.Button("Equip Armor", GUILayout.Width(95), GUILayout.Height(22)))
                {
                    EquipArmorById(player, armorId, def);
                }
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Resolve an armour definition by id. <b>Armour first</b>: the importer writes the real
        /// <c>&lt;armor&gt;</c> definitions (ratings, resists, level table) to <c>Resources/Armor/</c>,
        /// and looking only in <c>Items/</c> returns null for most plates — <c>assault</c> has no item
        /// row at all — so the caller would build an empty fallback with no resists. That is exactly why
        /// the "Defense, Armor &amp; Vulnerability Multipliers" panel stayed Neutral after equipping.
        /// <c>Items/</c> remains a secondary source so a hand-authored armour item still resolves.
        /// </summary>
        private static ItemDefinition ResolveArmorDefinition(string armorId)
        {
            if (string.IsNullOrEmpty(armorId)) return null;

            var def = Resources.Load<ItemDefinition>($"Armor/{armorId}");
            if (def == null)
            {
                def = Resources.Load<ItemDefinition>($"Items/{armorId}");
            }
            return def;
        }

        private void EquipArmorById(PlayerController player, string armorId, ItemDefinition itemDef = null)
        {
            if (player?.Stats == null) return;

            if (itemDef == null)
            {
                itemDef = ResolveArmorDefinition(armorId);
            }

            if (itemDef == null)
            {
                // Last-resort wrapper for a visual set with no definition at all. It carries no ratings
                // and no resists, so the stats panel will still read empty — that is a data gap, not a
                // path to rely on. Every one of the 20 visual ids has an imported definition today.
                Debug.LogWarning($"[PlayerDebugEditor] No armour definition for '{armorId}' in " +
                                 "Resources/Armor or Resources/Items — equipping an empty shell.");
                itemDef = ScriptableObject.CreateInstance<ItemDefinition>();
                itemDef.itemId = armorId;
                itemDef.armorHP = 150;
                itemDef.armorTip = 1;
            }

            var armorInstance = new GameArmorInstance(itemDef);
            player.Stats.EquipArmour(armorInstance);
            Debug.Log($"[PlayerDebugEditor] Equipped armor '{armorId}' — paper-doll sprite updated.");
        }

        // =========================================================================
        // TAB 4: VITALS & PRESETS
        // =========================================================================

        private void DrawVitalsPresetsTab(PlayerController player)
        {
            var charStats = player.CharacterStats;
            var unitStats = player.Stats;
            if (unitStats == null || charStats == null) return;

            _presetsScroll = GUILayout.BeginScrollView(_presetsScroll);

            // Vitals Sliders
            GUILayout.Label("<b>Vitals & Health Controls</b>", _subHeaderStyle);
            GUILayout.BeginVertical(_cardStyle);

            // HP
            float curHp = unitStats.CurrentHp.Value;
            float maxHp = unitStats.MaxHp.Value;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"HP: <b>{curHp:0}/{maxHp:0}</b>", GUILayout.Width(110));
            float newHp = GUILayout.HorizontalSlider(curHp, 0, maxHp, GUILayout.Width(250));
            if (Math.Abs(newHp - curHp) > 0.5f)
            {
                unitStats.CurrentHp.Value = newHp;
            }
            if (GUILayout.Button("Full HP", GUILayout.Width(75))) unitStats.CurrentHp.Value = maxHp;
            if (GUILayout.Button("Damage 25", GUILayout.Width(85))) unitStats.Damage(25);
            GUILayout.EndHorizontal();

            // Mana
            float curMana = charStats.manaHp;
            float maxMana = charStats.MaxMana;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Mana: <b>{curMana:0}/{maxMana:0}</b>", GUILayout.Width(110));
            float newMana = GUILayout.HorizontalSlider(curMana, 0, maxMana, GUILayout.Width(250));
            if (Math.Abs(newMana - curMana) > 0.5f)
            {
                charStats.manaHp = newMana;
            }
            if (GUILayout.Button("Full Mana", GUILayout.Width(75))) charStats.manaHp = maxMana;
            if (GUILayout.Button("Drain 50", GUILayout.Width(85))) charStats.manaHp = Mathf.Max(0, curMana - 50);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            // Quick actions
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("💚 Full Restore (HP + Mana)", GUILayout.Height(25)))
            {
                unitStats.CurrentHp.Value = maxHp;
                charStats.manaHp = maxMana;
            }
            if (GUILayout.Button("💀 Kill Player", GUILayout.Height(25)))
            {
                unitStats.Damage(maxHp + 999);
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            GUILayout.Space(12);

            // Class Archetype Build Presets
            GUILayout.Label("<b>Archetype Class Build Presets (One-Click Setup)</b>", _subHeaderStyle);

            DrawPresetRow(
                "🎯 Gunslinger / Sniper",
                "Level 15 | Small Guns 20, Sneak 20, Survival 10 | Scoped Hunting Rifle / 10mm | Assault Armor",
                () => ApplyGunslingerPreset(player));

            DrawPresetRow(
                "🛡️ Power Armor Heavy Tank",
                "Level 25 | Defense 50, Heavy/Small Guns 20, Survival 20 | Power Armor | Minigun / Missile",
                () => ApplyTankPreset(player));

            DrawPresetRow(
                "🔮 Psionic / Telekinetic Sorcerer",
                "Level 20 | Telekinesis 20, Magic 20, Knowledge 40, Spirit 40 | Magus Robe | Magic Horn",
                () => ApplyMagePreset(player));

            DrawPresetRow(
                "⚔️ Wasteland Melee Brawler",
                "Level 15 | Melee 20, Attack 30, Survival 20 | Raider Metal Armor | Sledgehammer / Club",
                () => ApplyBrawlerPreset(player));

            DrawPresetRow(
                "🔄 Clean Slate (Level 1 Vanilla LittlePip)",
                "Level 1 | All Skills 0 | Stripped Armor | Unarmed | Zero Perks",
                () => ApplyResetPreset(player));

            GUILayout.EndScrollView();
        }

        private void DrawPresetRow(string title, string details, Action applyAction)
        {
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{title}</b>", GUILayout.Width(300));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("⚡ Apply Preset", GUILayout.Width(130), GUILayout.Height(24)))
            {
                applyAction?.Invoke();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label($"<color=#AAAAAA><size=11>{details}</size></color>");
            GUILayout.EndVertical();
            GUILayout.Space(3);
        }

        private void ApplyGunslingerPreset(PlayerController player)
        {
            var charStats = player.CharacterStats;
            var loadout = player.GetComponent<PlayerWeaponLoadout>();

            charStats.SetLevel(15);
            charStats.SetSkillLevel("smallguns", 20);
            charStats.SetSkillLevel("sneak", 20);
            charStats.SetSkillLevel("survival", 10);
            charStats.SetSkillLevel("repair", 15);
            charStats.SetSkillLevel("lockpick", 15);

            EquipArmorById(player, "assault");

            var rifle = _allWeapons?.FirstOrDefault(w => w.weaponId.Contains("rifle") || w.weaponId.Contains("10"));
            if (rifle != null && loadout != null) loadout.Equip(rifle);

            player.Stats.Heal(999);
            Debug.Log("[PlayerDebugEditor] Applied Gunslinger preset.");
        }

        private void ApplyTankPreset(PlayerController player)
        {
            var charStats = player.CharacterStats;
            var loadout = player.GetComponent<PlayerWeaponLoadout>();

            charStats.SetLevel(25);
            charStats.SetSkillLevel("defense", 50);
            charStats.SetSkillLevel("survival", 20);
            charStats.SetSkillLevel("smallguns", 20);
            charStats.SetSkillLevel("repair", 20);
            charStats.SetPerkRank("life", 40);   // `life` is a perk (AllData.as:5802), not a skill

            EquipArmorById(player, "power");

            var heavy = _allWeapons?.FirstOrDefault(w => w.weaponType == WeaponType.BigGun || w.weaponId.Contains("mini") || w.weaponId.Contains("shot"));
            if (heavy != null && loadout != null) loadout.Equip(heavy);

            player.Stats.Heal(999);
            Debug.Log("[PlayerDebugEditor] Applied Heavy Tank preset.");
        }

        private void ApplyMagePreset(PlayerController player)
        {
            var charStats = player.CharacterStats;
            var loadout = player.GetComponent<PlayerWeaponLoadout>();

            charStats.SetLevel(20);
            charStats.SetSkillLevel("tele", 20);
            charStats.SetSkillLevel("magic", 20);
            charStats.SetSkillLevel("knowl", 40);
            charStats.SetPerkRank("spirit", 40);   // `spirit` is a perk (AllData.as:5806), not a skill

            EquipArmorById(player, "magus");

            var magicWp = _allWeapons?.FirstOrDefault(w => w.weaponType == WeaponType.Magic || w.damageType == DamageType.Laser);
            if (magicWp != null && loadout != null) loadout.Equip(magicWp);

            player.Stats.Heal(999);
            charStats.manaHp = charStats.MaxMana;
            Debug.Log("[PlayerDebugEditor] Applied Psionic Magus preset.");
        }

        private void ApplyBrawlerPreset(PlayerController player)
        {
            var charStats = player.CharacterStats;
            var loadout = player.GetComponent<PlayerWeaponLoadout>();

            charStats.SetLevel(15);
            charStats.SetSkillLevel("melee", 20);
            charStats.SetSkillLevel("attack", 30);
            charStats.SetSkillLevel("survival", 20);

            EquipArmorById(player, "metal");

            var meleeWp = _allWeapons?.FirstOrDefault(w => w.weaponType == WeaponType.Melee);
            if (meleeWp != null && loadout != null) loadout.Equip(meleeWp);

            player.Stats.Heal(999);
            Debug.Log("[PlayerDebugEditor] Applied Melee Brawler preset.");
        }

        private void ApplyResetPreset(PlayerController player)
        {
            var charStats = player.CharacterStats;
            var loadout = player.GetComponent<PlayerWeaponLoadout>();

            charStats.SetLevel(1);
            SetAllSkills(charStats, 0);
            charStats.ClearAllPerks();
            player.Stats.UnequipArmour();

            var unarmed = _allWeapons?.FirstOrDefault(w => w.weaponType == WeaponType.Melee);
            if (unarmed != null && loadout != null) loadout.Equip(unarmed);

            player.Stats.Heal(999);
            Debug.Log("[PlayerDebugEditor] Applied Clean Slate reset.");
        }

        // =========================================================================
        // TAB 7: SPELLS (the caster's catalogue — grant and select without a weapon)
        // =========================================================================

        /// <summary>
        /// The spell catalogue, one card per spell, with a Grant/Select button.
        ///
        /// <para><b>Why this tab exists when the Weapons tab can already select a spell.</b> Equipping
        /// one of the nine weapons whose <c>weapon@spell='1'</c> does route through
        /// <c>PlayerWeaponLoadout</c> and selects the matching spell — but it makes two questions into
        /// one. This tab asks only "can the player cast?", which is the question you want answered when
        /// the cast does nothing. The same split the <c>spell add</c> console verb makes.</para>
        ///
        /// <para><b>The description is derived, not authored.</b> The oracle carries no per-spell prose:
        /// every row sets <c>mess='spell'</c> (<c>AllData.as:4008-4016</c>), which is a category tag
        /// that nothing resolves to a string, and the imported item rows carry the importer's
        /// placeholder name. So each line is built from the spell's own attributes by
        /// <see cref="SpellFacts"/> — every word traces to a field, none of it is invented. See that
        /// class for the per-field oracle citations.</para>
        ///
        /// <para><b>It drives the live caster.</b> Grant calls <c>PlayerSpellCaster.LearnSpell</c> and
        /// Select calls <c>SelectSpell</c> — the same two methods the inventory's <c>useItem</c> dispatch
        /// and the console verb call. So a spell granted here is castable in game immediately, which is
        /// what makes this a test of the cast path rather than a list that formats strings.</para>
        /// </summary>
        private void DrawSpellsTab(PlayerController player)
        {
            PlayerSpellCaster caster = player.GetComponent<PlayerSpellCaster>();
            if (caster == null)
            {
                GUILayout.Box(
                    "PlayerSpellCaster missing on the Player.\n\n" +
                    "PlayerController.Awake adds it (GetComponent ?? AddComponent). If it is absent, the " +
                    "component was removed or Awake threw before that line — run `spell` in the console " +
                    "for the same diagnosis.",
                    _cardStyle, GUILayout.ExpandHeight(true));
                return;
            }

            Spell selected = caster.Selected;

            // ── Header: what C will cast, and how much there is to choose from ──────────────────────
            GUILayout.BeginHorizontal(_cardActiveStyle);
            GUILayout.Label($"<b>Selected (C casts):</b> {(selected != null ? selected.Id : "(none)")}",
                GUILayout.Width(300));
            GUILayout.Label($"Known: <b>{caster.SpellCount}</b>", GUILayout.Width(100));
            GUILayout.Label($"Catalogue: <b>{SpellCatalog.All().Length}</b>", GUILayout.Width(150));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear selection", GUILayout.Width(130), GUILayout.Height(22)))
            {
                // changeSpell is a toggle, so re-selecting the current spell is how the oracle clears
                // the selection (Invent.as:849-852 calls changeSpell("") for the same reason).
                if (selected != null) caster.SelectSpell(selected.Id);
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(
                "<color=#AAAAAA>Press <b>C</b> in game to cast the selection (AS3 <b>keyDef</b>). " +
                "A <b>prod</b> spell repeats while held; the rest fire once per press. " +
                "<b>T</b> is the separate magic-weapon key and is not this path.</color>");

            GUILayout.Space(6);

            _spellsScroll = GUILayout.BeginScrollView(_spellsScroll);

            ItemDefinition[] all = SpellCatalog.All();
            if (all.Length == 0)
            {
                GUILayout.Label("No item row under Resources/Items has a populated spellData, so nothing " +
                                "can be granted. Run `PFE/Data/Simple Import All Data`.");
                GUILayout.EndScrollView();
                return;
            }

            foreach (ItemDefinition item in all)
            {
                if (item == null) continue;

                bool known = caster.Book != null && caster.Book.TryGet(item.itemId, out Spell _);
                bool isSelected = known && selected != null &&
                                  string.Equals(selected.Id, item.itemId, StringComparison.OrdinalIgnoreCase);

                DrawSpellRow(caster, item, known, isSelected);
            }

            GUILayout.EndScrollView();
        }

        /// <summary>
        /// One spell card: the id (or the importer's name when it has a real one) on the first line with
        /// the action button, and the derived description beneath it.
        /// </summary>
        private void DrawSpellRow(PlayerSpellCaster caster, ItemDefinition item, bool known, bool isSelected)
        {
            GUIStyle card = isSelected ? _cardActiveStyle : _cardStyle;
            GUILayout.BeginVertical(card);

            // ── Line 1: name + state + button ──────────────────────────────────────────────────────
            GUILayout.BeginHorizontal();

            string name = SpellCatalog.DisplayName(item);
            string label = name == item.itemId ? item.itemId : $"{item.itemId}  <color=#AAAAAA>({name})</color>";
            string title = isSelected ? $"<color=#55FF55><b>▶ {label}</b></color>" : $"<b>{label}</b>";
            GUILayout.Label(title, GUILayout.Width(280));

            GUILayout.Label(SpellFacts.Flags(in item.spellData), _badgeStyle, GUILayout.Width(200));

            GUILayout.FlexibleSpace();

            if (isSelected)
            {
                GUI.color = Color.green;
                GUILayout.Box("CASTING", GUILayout.Width(90), GUILayout.Height(22));
                GUI.color = Color.white;
            }
            else if (known)
            {
                if (GUILayout.Button("Select", GUILayout.Width(90), GUILayout.Height(22)))
                    caster.SelectSpell(item.itemId);
            }
            else
            {
                if (GUILayout.Button("Grant", GUILayout.Width(90), GUILayout.Height(22)))
                    caster.LearnSpell(item.itemId);
            }

            GUILayout.EndHorizontal();

            // ── Line 2: the derived description ────────────────────────────────────────────────────
            string snd = SpellFacts.SoundId(in item.spellData);
            GUILayout.Label(
                $"   <color=#BBBBBB>{SpellFacts.Summarize(in item.spellData)}" +
                $"{(snd != null ? $"   ·   cast sound: {snd}" : string.Empty)}</color>");

            GUILayout.EndVertical();
        }

        // =========================================================================
        // TAB 6: EFFECTS (status effects on the player OR any unit in the scene)
        // =========================================================================

        /// <summary>
        /// Apply / remove / inspect live status effects on a chosen target — the player, or another
        /// <c>UnitController</c> in the scene.
        ///
        /// <para><b>Why a separate tab, and why on a selectable target.</b> AS3 declares
        /// <c>effects</c> on the base <c>Unit</c> (<c>Unit.as:494</c>), so the player and every NPC own
        /// one; and an effect reaches a victim through <c>Unit.damage()</c>'s on-hit block
        /// (<c>:3763-3834</c>), which is why "burn the enemy" and "heal/cure myself" are the same
        /// mechanism pointed at two different units. A tab that could only address the player would
        /// exercise half of it.</para>
        ///
        /// <para><b>It drives the real runtime, not a debug model.</b> Every button calls
        /// <see cref="ActiveEffectSet.AddEffect"/>/<c>RemoveEffect</c> on the live set, so what it
        /// proves is that the effect system reacts on a running unit — count-down, param replay,
        /// payload — rather than that a debug row formats a string. The readbacks below are the live
        /// values the pass just wrote (<c>maxhp</c>, <c>skin</c>, <c>dexter</c>, the resistance table),
        /// which is how "the stat changed" is observed instead of assumed.</para>
        /// </summary>
        private void DrawEffectsTab(PlayerController player)
        {
            _effectsScroll = GUILayout.BeginScrollView(_effectsScroll);

            UnitController target = ResolveEffectTarget(player, out string targetLabel);

            // ── Target selector ──────────────────────────────────────────────
            GUILayout.Label("<b>Target</b>", _subHeaderStyle);
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Applying to: <b>{targetLabel}</b>", GUILayout.Width(300));

            // Count other units so an empty scene reads as "no other units" rather than a broken
            // selector. RefreshAll below runs once per frame and is cheap (FindObjectsByType).
            _effectTargets = FindObjectsByType<UnitController>(FindObjectsSortMode.None);
            int otherCount = 0;
            if (_effectTargets != null)
            {
                foreach (UnitController u in _effectTargets)
                {
                    if (u != null && u != player) otherCount++;
                }
            }

            if (GUILayout.Button($"◀ Player", GUILayout.Width(90)))
            {
                _effectTargetIndex = 0;
            }
            if (GUILayout.Button($"Next unit ({otherCount}) ▶", GUILayout.Width(140)))
            {
                _effectTargetIndex = (_effectTargetIndex + 1) % Mathf.Max(1, otherCount + 1);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (target != null)
            {
                UnitStats ts = target.UnitStats;
                bool hasResolver = ts != null && ts.HasEffectResolver;
                ActiveEffectSet set = target.Effects;
                GUILayout.Label(
                    $"Effect set: <b>{set?.Count ?? 0}</b> live  |  resolver: " +
                    (hasResolver
                        ? "<color=#55FF55>wired</color>"
                        : "<color=#FFAA33><b>MISSING</b> — ids cannot resolve, `AddEffect` will refuse every one</color>"),
                    GUILayout.ExpandWidth(true));
                if (!hasResolver)
                {
                    GUILayout.Label(
                        "<color=#AAAAAA><size=11>A spawned unit gets its resolver from the room spawner " +
                        "(MapBridge → RoomVisualController → RoomUnitSpawner), so this usually means no " +
                        "room was spawned through that chain — a bare AddComponent test unit, or a scene " +
                        "built before the handover ran.</size></color>");
                }
            }
            else
            {
                GUILayout.Label("<color=#FF6666>No UnitController resolved for this target.</color>");
            }
            GUILayout.EndVertical();

            GUILayout.Space(8);

            if (target == null || target.Effects == null)
            {
                GUILayout.EndScrollView();
                return;
            }

            // ── Armour row (does armour grant an effect?) ────────────────────
            //
            // The oracle answers "yes, exactly one, and only for one of the 35 armour rows":
            // UnitPlayer.as:3884 `armorEffect = addEffect(this.currentArmor.abil)`, and Armor.as:56's
            // `abil` is filled only for `astealth` → `stealth_armor`. It is a mana-spending TOGGLE, and
            // `currentArmor` is player-only. The port has no `abil` on ItemDefinition (the ability
            // subsystem is not ported — see ArmourDataParser's ignored-attribute table), so the row
            // reports the armour's *combat* projection honestly and names the unported link rather than
            // faking a button that would apply nothing.
            GUILayout.Label("<b>Armour effect</b>", _subHeaderStyle);
            GUILayout.BeginVertical(_cardStyle);
            var armUnitStats = target.UnitStats;
            ArmourState armour = armUnitStats?.armour ?? ArmourState.None;
            if (armour.IsEquipped)
            {
                GUILayout.Label(
                    $"Armour: <b>{armUnitStats.ArmourId.Value}</b>  |  integrity {armour.integrity:0}/{armour.maxIntegrity:0} " +
                    $"({armour.IntegrityPercent * 100f:0}%)  |  model {armour.model}");
                GUILayout.Label(
                    $"Ratings: phys <b>{armour.EffectivePhysicalRating:0.#}</b> ({armour.physicalRating:0.#} × cond {armour.ConditionFactor:0.##})  |  " +
                    $"energy <b>{armour.EffectiveEnergyRating:0.#}</b>  |  reliability <b>{armour.EffectiveReliability:0.##}</b>");
            }
            else
            {
                GUILayout.Label("<color=#AAAAAA>No armour equipped (armour is player-only in AS3 — an NPC has a pool, not a plate).</color>");
            }

            GUILayout.BeginHorizontal();
            GUI.enabled = TargetHasEffect(target, "stealth_armor");
            if (GUILayout.Button("Apply armour ability (stealth_armor)", GUILayout.Width(250), GUILayout.Height(22)))
            {
                target.Effects.AddEffect("stealth_armor");
            }
            GUI.enabled = true;
            if (GUILayout.Button("Remove it", GUILayout.Width(110), GUILayout.Height(22)))
            {
                target.Effects.RemoveEffect("stealth_armor");
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(
                "<color=#AAAAAA><size=11>AS3 UnitPlayer.as:3884 applies <b>one</b> armour effect, from the " +
                "armour's <i>abil</i> attribute (only <i>astealth</i> → <i>stealth_armor</i> of 35 rows). " +
                "The port has no <i>abil</i> on the item yet, so this button stands in for that link; the " +
                "equip-time wiring is still owed.</size></color>");
            GUILayout.EndVertical();

            GUILayout.Space(8);

            // ── Live effect list ─────────────────────────────────────────────
            GUILayout.Label($"<b>Live effects on {targetLabel}</b>", _subHeaderStyle);
            GUILayout.BeginVertical(_cardStyle);
            ActiveEffectSet liveSet = target.Effects;
            if (liveSet.Count == 0)
            {
                GUILayout.Label("<color=#AAAAAA>None. Apply one below.</color>");
            }
            else
            {
                for (int i = 0; i < liveSet.Effects.Count; i++)
                {
                    ActiveEffect eff = liveSet.Effects[i];
                    if (eff == null) continue;

                    GUILayout.BeginHorizontal();
                    string state = eff.IsBeingUnset ? "<color=#FF6666>unsetting</color>" : "<color=#55FF55>live</color>";
                    string dur = eff.Forever ? "forever" : $"{eff.TicksRemaining / 30f:0.0}s ({eff.TicksRemaining} ticks)";
                    GUILayout.Label(
                        $"<b>{eff.Id}</b>  [{eff.Tip}]  {state}  t={dur}  lvl={eff.Level}  val={eff.Value:0.##}" +
                        (eff.HasTransitioned ? $"  <color=#FFAA33>(was {eff.OriginalId})</color>" : ""),
                        GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("✕", GUILayout.Width(28), GUILayout.Height(20)))
                    {
                        liveSet.RemoveEffect(eff.Id);
                    }
                    GUILayout.EndHorizontal();
                }
            }
            if (GUILayout.Button("Clear all effects", GUILayout.Width(150), GUILayout.Height(22)))
            {
                liveSet.Clear();
            }
            GUILayout.EndVertical();

            GUILayout.Space(8);

            // ── Live stat readback (how you SEE a param effect land) ─────────
            GUILayout.Label("<b>Live stat readback (effects write through these)</b>", _subHeaderStyle);
            GUILayout.BeginVertical(_cardStyle);
            UnitStats stats = target.UnitStats;
            if (stats != null)
            {
                GUILayout.BeginHorizontal();
                DrawStatPair("Max HP", $"{stats.MaxHp.Value:0.##}");
                DrawStatPair("Skin resistance", $"{stats.skinResistance:0.###}");
                DrawStatPair("Dexterity", $"{stats.dexterity:0.##}");
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                DrawStatPair("Armour effectiveness", $"{stats.armorEffectiveness:0.##}");
                DrawStatPair("Is non-living", stats.isNonLiving ? "yes" : "no");
                DrawStatPair("Alive", stats.IsAlive ? "yes" : "no");
                GUILayout.EndHorizontal();

                int unmapped = 0;
                if (liveSet.UnmappedParamNames != null)
                {
                    foreach (var kv in liveSet.UnmappedParamNames) unmapped += kv.Value;
                }
                GUILayout.Label(
                    $"<size=11>Unmapped &lt;sk&gt; writes this session: <b>{unmapped}</b>" +
                    (unmapped > 0 ? " — see the console (`eff dump`) for the names" : "") +
                    "</size>");

                // The reset half of the pass, reported as a property for the same reason the resolver
                // is: its absence is invisible in every value (an un-reset replay just compounds, which
                // reads as "a strong buff"), so a readback is the only way to see it. NPC mode
                // legitimately has none — AS3's Unit.setEffParams resets only the vulnerability channel
                // and the three Cont counters, which are the only things an NPC-reachable effect writes.
                bool isPlayerTarget = target is PlayerController;
                bool hasReset = stats.HasEffectResetSink;
                GUILayout.Label(
                    "<size=11>Reset sink (player derived block): " +
                    (hasReset
                        ? "<color=#55FF55>installed</color> — a replay rebuilds from baseline first"
                        : (isPlayerTarget
                            ? "<color=#FFAA33><b>absent</b> — a replay will compound; the player bootstrap did not wire it</color>"
                            : "<color=#AAAAAA>none — expected for an NPC: AS3's setEffParams resets only vulnerability</color>")) +
                    "</size>");
            }
            GUILayout.EndVertical();

            GUILayout.Space(8);

            // ── Apply an effect ──────────────────────────────────────────────
            GUILayout.Label("<b>Apply an effect</b>", _subHeaderStyle);

            GUILayout.BeginVertical(_cardStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter:", GUILayout.Width(50));
            _effectSearch = GUILayout.TextField(_effectSearch ?? string.Empty, GUILayout.Width(220));
            if (GUILayout.Button("Clear", GUILayout.Width(60)))
            {
                _effectSearch = string.Empty;
            }
            GUILayout.Label($"Duration (s, 0 = definition):", GUILayout.Width(200));
            _effectDurationSeconds = ParseFloatOrZero(GUILayout.TextField(_effectDurationSeconds <= 0f ? "0" : _effectDurationSeconds.ToString("0.#"), GUILayout.Width(60)));
            GUILayout.Label("Value (0 = definition):", GUILayout.Width(150));
            _effectValue = ParseFloatOrZero(GUILayout.TextField(_effectValue <= 0f ? "0" : _effectValue.ToString("0.##"), GUILayout.Width(60)));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(4);

            // Quick one-click rows for the effects the oracle's tests and on-hit producers name.
            // Driven from QuickEffectRows so the draw loop and VerifyQuickRowsResolve cannot drift.
            for (int r = 0; r < QuickEffectRows.Length; r++)
            {
                (string id, string label, string note) = QuickEffectRows[r];
                DrawQuickEffectRow(target, id, label, note);
            }

            GUILayout.Space(8);
            GUILayout.Label("<size=11><b>All definitions</b> (Resources/Effects — this is what the runtime resolver reads, " +
                            "so a row missing here that exists on disk is a registry gap):</size>");
            _effectListScroll = GUILayout.BeginScrollView(_effectListScroll, GUILayout.Height(220));

            if (_allEffects == null || _allEffects.Length == 0)
            {
                LoadCatalogs();
            }

            int shown = 0;
            if (_allEffects != null)
            {
                string filter = (_effectSearch ?? string.Empty).Trim();
                foreach (EffectDefinition def in _allEffects)
                {
                    if (def == null) continue;
                    if (filter.Length > 0 && def.effectId.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    shown++;

                    GUILayout.BeginHorizontal();
                    string tipColor = def.type == EffectType.Purgeable ? "#55CCFF"
                        : def.type == EffectType.Food ? "#FFFF55"
                        : def.type == EffectType.Neutral ? "#AAAAAA" : "#FFAA33";
                    GUILayout.Label(
                        $"<b>{def.effectId}</b>  <color={tipColor}>[{def.type}]</color>  " +
                        $"t={(def.forever ? "∞" : (def.durationTicks / 30f).ToString("0.#") + "s")}  " +
                        $"val={def.value:0.##}  sk={(def.effects?.Length ?? 0)}",
                        GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Apply", GUILayout.Width(60), GUILayout.Height(20)))
                    {
                        int ticks = _effectDurationSeconds > 0f ? Mathf.RoundToInt(_effectDurationSeconds * 30f) : 0;
                        target.Effects.AddEffect(def.effectId, _effectValue, ticks);
                    }
                    GUILayout.EndHorizontal();
                }
            }
            if (shown == 0)
            {
                GUILayout.Label("<color=#AAAAAA>No definitions match the filter.</color>");
            }
            GUILayout.EndScrollView();

            GUILayout.EndScrollView();
        }

        /// <summary>
        /// One labelled quick-apply row. The tooltip names the oracle line the effect exercises, so a
        /// tester knows what a click is meant to prove rather than just what it does.
        /// </summary>
        private void DrawQuickEffectRow(UnitController target, string id, string label, string oracleNote)
        {
            // A row whose id has no definition would call AddEffect, be refused, and change nothing —
            // indistinguishable from a working row on a unit nothing has hit. Say so on the row and
            // disable the buttons, so a bad id is visible instead of silent. This is the guard the
            // `stunned` row needed; see QuickEffectRows.
            bool resolvable = HasDefinitionFor(id);

            GUILayout.BeginVertical(_cardStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{label}</b>", GUILayout.Width(280));
            GUILayout.Label($"<color=#AAAAAA><size=11>{oracleNote}</size></color>", GUILayout.ExpandWidth(true));
            GUI.enabled = resolvable;
            if (GUILayout.Button("Apply", GUILayout.Width(70), GUILayout.Height(22)))
            {
                int ticks = _effectDurationSeconds > 0f ? Mathf.RoundToInt(_effectDurationSeconds * 30f) : 0;
                target.Effects.AddEffect(id, _effectValue, ticks);
            }
            if (GUILayout.Button("Remove", GUILayout.Width(70), GUILayout.Height(22)))
            {
                target.Effects.RemoveEffect(id);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (!resolvable)
            {
                GUILayout.Label(
                    $"<color=#FF6666><size=11><b>NO DEFINITION</b> for id '{id}' — nothing is under " +
                    "Resources/Effects with that id, so AddEffect refuses it and this row can never do " +
                    "anything. Either the id is wrong or the data has not been imported " +
                    "(PFE/Data/Import Effects from AllData.as).</size></color>");
            }

            GUILayout.EndVertical();
            GUILayout.Space(2);
        }

        /// <summary>
        /// Whether the tab's own catalogue carries a definition for this id. Reads
        /// <see cref="_allEffects"/>, which is the same <c>Resources/Effects</c> the boot registry
        /// loads — so "not here" means the runtime resolver cannot see it either.
        /// </summary>
        private bool HasDefinitionFor(string id)
        {
            if (string.IsNullOrEmpty(id) || _allEffects == null)
            {
                return false;
            }

            for (int i = 0; i < _allEffects.Length; i++)
            {
                EffectDefinition def = _allEffects[i];
                if (def != null && string.Equals(def.effectId, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Name every quick-row id that has no definition, once, at catalogue load.
        ///
        /// <para><b>Why this exists.</b> The <c>stunned</c> row shipped pointing at an id that was in no
        /// <c>&lt;eff&gt;</c> row, no asset and no oracle file, and nothing went red: the button simply
        /// did nothing. A debug tool whose own buttons can be silently inert is worse than no tool, so
        /// the mismatch is now announced where it is cheap to see — at load, by name — rather than
        /// discovered by clicking.</para>
        /// </summary>
        private void VerifyQuickRowsResolve()
        {
            if (_quickRowsVerified)
            {
                return;
            }
            _quickRowsVerified = true;

            if (_allEffects == null || _allEffects.Length == 0)
            {
                return;
            }

            for (int i = 0; i < QuickEffectRows.Length; i++)
            {
                string id = QuickEffectRows[i].Id;
                if (!HasDefinitionFor(id))
                {
                    Debug.LogError(
                        $"[PlayerDebugEditorOverlay] Effects tab quick row '{id}' has no definition under " +
                        "Resources/Effects, so its Apply button can never do anything. Fix the id or run " +
                        "PFE/Data/Import Effects from AllData.as.");
                }
            }
        }

        private static bool TargetHasEffect(UnitController target, string id)
            => target != null && target.Effects != null && target.Effects.Has(id);

        /// <summary>
        /// Resolve the selected target. Index <c>0</c> is always the player; <c>1..n</c> walk the other
        /// units in the scene in discovery order. Falls back to the player when the index no longer
        /// points at a live unit (it died, or the room was torn down), so the tab never goes blank on a
        /// stale selection.
        /// </summary>
        private UnitController ResolveEffectTarget(PlayerController player, out string label)
        {
            if (_effectTargetIndex <= 0)
            {
                label = "the player (LittlePip)";
                return player;
            }

            int seen = 0;
            if (_effectTargets != null)
            {
                foreach (UnitController u in _effectTargets)
                {
                    if (u == null || u == player) continue;
                    seen++;
                    if (seen == _effectTargetIndex)
                    {
                        label = $"{u.name} ({u.GetType().Name})";
                        return u;
                    }
                }
            }

            // Stale index — the unit it named is gone.
            _effectTargetIndex = 0;
            label = "the player (LittlePip)";
            return player;
        }

        /// <summary>
        /// Parse a text field to a float, treating anything unparseable as <c>0</c> — which for both
        /// overrides means "use the definition's own value" (the oracle's
        /// <c>if(this.val == 0) this.val = node.@val</c>, <c>Effect.as:87-90</c>). A debug field must not
        /// be able to wedge the runtime with a parse exception.
        /// </summary>
        private static float ParseFloatOrZero(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0f;
            return float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
        }

        // =========================================================================
        // UI DRAWING HELPERS FOR STATS TAB
        // =========================================================================

        private void DrawProgressBar(float fraction, Color fillColor, Color bgColor)
        {
            fraction = Mathf.Clamp01(fraction);
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(12), GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                var prevColor = GUI.color;
                GUI.color = bgColor;
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                if (fraction > 0f)
                {
                    Rect fillRect = new Rect(r.x, r.y, r.width * fraction, r.height);
                    GUI.color = fillColor;
                    GUI.DrawTexture(fillRect, Texture2D.whiteTexture);
                }
                GUI.color = prevColor;
            }
        }

        private void DrawOrganColumn(string name, float hp, float maxHp, int stage, float minHp, Action<float> onAdjust, Action onCure)
        {
            GUILayout.BeginVertical(_cardActiveStyle, GUILayout.Width(168));
            GUILayout.Label($"<b>{name}</b>", _subHeaderStyle);

            string stageColor;
            string stageDesc;
            switch (stage)
            {
                case 0: stageColor = "#55FF55"; stageDesc = "Healthy (0)"; break;
                case 1: stageColor = "#FFFF55"; stageDesc = "Minor (1)"; break;
                case 2: stageColor = "#FFAA33"; stageDesc = "Moderate (2)"; break;
                case 3: stageColor = "#FF6633"; stageDesc = "Severe (3)"; break;
                default: stageColor = "#FF3333"; stageDesc = "Crippled (4)"; break;
            }
            GUILayout.Label($"Stage: <color={stageColor}><b>{stageDesc}</b></color>");

            float pct = maxHp > 0 ? (hp / maxHp) : 0f;
            Color barColor = pct > 0.6f ? new Color(0.2f, 0.8f, 0.3f) : (pct > 0.3f ? new Color(0.9f, 0.7f, 0.1f) : new Color(0.9f, 0.2f, 0.2f));
            GUILayout.Label($"{hp:0} / {maxHp:0} ({pct * 100f:0}%)");
            DrawProgressBar(pct, barColor, new Color(0.12f, 0.14f, 0.18f));

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("-25", GUILayout.Width(44), GUILayout.Height(20))) onAdjust?.Invoke(-25f);
            if (GUILayout.Button("+25", GUILayout.Width(44), GUILayout.Height(20))) onAdjust?.Invoke(25f);
            if (GUILayout.Button("Cure", GUILayout.Width(48), GUILayout.Height(20))) onCure?.Invoke();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        private void DrawStatPair(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.ExpandWidth(true));
            GUILayout.Label(value, GUILayout.Width(95));
            GUILayout.EndHorizontal();
        }

        private string FormatMobBonus(float bonus)
        {
            if (Mathf.Approximately(bonus, 1f)) return "<color=#CCCCCC>x1.00</color>";
            if (bonus > 1f) return $"<color=#55FF55><b>x{bonus:0.00}</b></color>";
            return $"<color=#FF6666>x{bonus:0.00}</color>";
        }

        private void DrawResistCell(string damageName, float mult)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(damageName, GUILayout.Width(95));
            if (mult <= 0f)
            {
                GUILayout.Label("<color=#FFD700><b>IMMUNE (0x)</b></color>", GUILayout.Width(105));
            }
            else if (mult < 0.999f)
            {
                float resPct = (1f - mult) * 100f;
                GUILayout.Label($"<color=#55FF55><b>{resPct:0}% Res</b> ({mult:0.00}x)</color>", GUILayout.Width(105));
            }
            else if (mult > 1.001f)
            {
                float vulPct = (mult - 1f) * 100f;
                GUILayout.Label($"<color=#FF6666><b>+{vulPct:0}% Vuln</b> ({mult:0.00}x)</color>", GUILayout.Width(105));
            }
            else
            {
                GUILayout.Label("<color=#AAAAAA>Neutral (1.0x)</color>", GUILayout.Width(105));
            }
            GUILayout.EndHorizontal();
        }

        // =========================================================================
        // TAB 8: INVENTORY — AS3 PipPageInv's pages, plus a Drop view
        // =========================================================================
        //
        // Why the sub-tabs are AS3's five pages and not the port's ItemType enum: the pages are what the
        // original's player sees, and InventoryPageRules maps every ItemType onto exactly ONE page, so the
        // list can never print the same row twice. Where that partition departs from AS3 — the import
        // collapsed `e` and `equip` into Equipment, so the oracle's AID/AMMO split is not recoverable —
        // every row prints its own ItemType, so the grouping hides nothing.
        //
        // Every mutation goes through PlayerInventory.Submit. The tab has no direct write to the bag,
        // which is exactly what makes it a test of the seam rather than a bypass of it.

        private static readonly string[] InventorySubTabNames =
            { "⚔ Weapons", "🛡 Armor", "💊 Aid", "📦 Misc", "• Ammo", "✨ Spawn", "⬇ Drop" };

        private void DrawInventoryTab(PlayerController player)
        {
            EnsureInventory();

            // Kept fresh rather than only cached where the bag is adopted: the placement anchors read
            // FacingDirection from it, and a stale or respawned player would place "in front" against a
            // dead transform (which Unity reports as null, so the fallback would silently take over).
            if (player != null) _livePlayer = player;

            PlayerInventory inventory = _liveInventory;
            if (inventory == null || inventory.Inventory == null)
            {
                GUILayout.Box(
                    "No PlayerInventory. PlayerController creates one at boot, so if the scene has a player " +
                    "and this is still empty then Construct never ran — which means no content registry was " +
                    "injected, and every command would be rejected anyway.",
                    _cardStyle, GUILayout.ExpandWidth(true));
                return;
            }

            GUILayout.BeginHorizontal();
            for (int i = 0; i < InventorySubTabNames.Length; i++)
            {
                GUIStyle style = (i == _invPage) ? _tabActiveStyle : _tabInactiveStyle;
                if (GUILayout.Button(InventorySubTabNames[i], style, GUILayout.Height(24)))
                    _invPage = i;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            if (_invPage == InventorySpawnSubTab)
            {
                DrawInventorySpawnView(inventory);
                FlushPendingInventoryAction(player, inventory);
                return;
            }

            if (_invPage == InventoryDropSubTab)
            {
                DrawInventoryDropView(inventory);
                FlushPendingInventoryAction(player, inventory);
                return;
            }

            DrawInventoryAddRow(inventory);
            DrawInventoryStatus();

            var page = (InventoryPage)_invPage;
            _invScroll = GUILayout.BeginScrollView(_invScroll, GUILayout.ExpandHeight(true));

            if (page == InventoryPage.Weapons) DrawInventoryWeaponRows(inventory);
            else if (page == InventoryPage.Armor) DrawInventoryArmorRows(inventory);
            else DrawInventoryItemRows(inventory, page);

            GUILayout.EndScrollView();

            // Outside EndScrollView on purpose — see the method's remarks.
            FlushPendingInventoryAction(player, inventory);
        }

        /// <summary>
        /// The "add by id" row. AS3's counterpart is the debug <c>Invent.addAll*()</c> family; a typed id
        /// is used instead because 500 items is too many to enumerate in a dropdown and the ids are the
        /// same keys the oracle indexes by.
        /// </summary>
        private void DrawInventoryAddRow(PlayerInventory inventory)
        {
            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label("<color=#AAAAAA>Add item by id:</color>", GUILayout.Width(110));

            _invAddId = GUILayout.TextField(_invAddId ?? string.Empty, GUILayout.Width(180));
            GUILayout.Label("<color=#AAAAAA>×</color>", GUILayout.Width(14));
            _invAddQty = GUILayout.TextField(_invAddQty ?? "1", GUILayout.Width(50));

            if (GUILayout.Button("Add", GUILayout.Width(60)))
            {
                if (!TryParsePositive(_invAddQty, out int qty))
                {
                    SetInventoryStatus($"quantity '{_invAddQty}' is not a positive number", true);
                }
                else if (string.IsNullOrEmpty(_invAddId))
                {
                    SetInventoryStatus("type an item id first", true);
                }
                else if (inventory.ResolveItem(_invAddId) == null)
                {
                    // Named rather than passed through: the seam would reject it too, but with the same
                    // wording for every unknown id and no hint that the id is simply misspelled.
                    SetInventoryStatus($"no item row for '{_invAddId}' — the id must match a Resources/Items asset", true);
                }
                else
                {
                    InventoryCommandResult result = inventory.Submit(InventoryCommand.AddItem(_invAddId, qty));
                    SetInventoryStatus(result.Applied
                        ? $"added {qty} × '{_invAddId}'"
                        : $"rejected: {result.Reason}", !result.Applied);
                }
            }

            GUILayout.FlexibleSpace();

            // The per-category mass AS3 keeps (mass[invCat] against maxm1/2/3). Printed here because it is
            // the one number the category work is for, and it is invisible anywhere else.
            var inv = inventory.Inventory;
            GUILayout.Label(
                $"<color=#AAAAAA>mass</color> usable <b>{inv.GetCategoryMass(InventoryCategoryRules.Usable):0.#}</b>  " +
                $"ammo <b>{inv.GetCategoryMass(InventoryCategoryRules.Ammo):0.#}</b>  " +
                $"stuff <b>{inv.GetCategoryMass(InventoryCategoryRules.Stuff):0.#}</b>  " +
                $"<color=#AAAAAA>total</color> <b>{inv.GetTotalMass():0.#}</b>",
                GUILayout.Width(360));

            GUILayout.EndHorizontal();
        }

        private void DrawInventoryStatus()
        {
            if (string.IsNullOrEmpty(_invStatus)) return;
            string colour = _invStatusIsError ? "#FF9A6A" : "#7FE07F";
            GUILayout.Label($"<color={colour}>{_invStatus}</color>");
        }

        private void SetInventoryStatus(string message, bool isError)
        {
            _invStatus = message;
            _invStatusIsError = isError;
        }

        /// <summary>
        /// The item-backed pages. Filtered by <see cref="InventoryPageRules.Contains"/>, which is a
        /// partition, so a row appears on exactly one page.
        /// </summary>
        private void DrawInventoryItemRows(PlayerInventory inventory, InventoryPage page)
        {
            IReadOnlyDictionary<string, GameItemInstance> items = inventory.Items;
            if (items == null || items.Count == 0)
            {
                GUILayout.Label("<color=#AAAAAA>empty — the inventory holds no items</color>");
                return;
            }

            int shown = 0;
            foreach (var kvp in items)
            {
                GameItemInstance inst = kvp.Value;
                if (inst?.Definition == null) continue;
                if (!InventoryPageRules.Contains(page, inst.Definition.type)) continue;

                DrawInventoryItemRow(kvp.Key, inst);
                shown++;
            }

            if (shown == 0)
            {
                GUILayout.Label($"<color=#AAAAAA>nothing on this page (the inventory has {items.Count} " +
                                "item(s), all on other pages)</color>");
            }
        }

        /// <summary>
        /// One row of an item-backed page. Takes no inventory: every button on it only <i>queues</i> a
        /// mutation, which <c>FlushPendingInventoryAction</c> runs after this row's loop has finished.
        /// </summary>
        private void DrawInventoryItemRow(string itemId, GameItemInstance inst)
        {
            ItemDefinition def = inst.Definition;
            int cat = InventoryCategoryRules.ForItem(def);

            GUILayout.BeginHorizontal(_cardStyle);

            GUILayout.Label($"<b>{def.displayName}</b>", GUILayout.Width(180));
            GUILayout.Label($"<color=#AAAAAA>{itemId}</color>", GUILayout.Width(110));
            GUILayout.Label($"<color=#9AD0FF>{def.type}</color>", GUILayout.Width(80));
            GUILayout.Label($"<b>{inst.Quantity}</b>", GUILayout.Width(50));
            GUILayout.Label($"<color=#AAAAAA>{inst.TotalWeight:0.##}u</color>", GUILayout.Width(60));
            GUILayout.Label($"<color=#FFD24A>cat{cat}</color>", GUILayout.Width(50));

            // Queued, never submitted inline: this row is drawn inside a foreach over inventory.Items, and
            // all three of these mutate that dictionary. See the _pendingInventoryAction field block.
            if (GUILayout.Button("−1", GUILayout.Width(34)))
                QueueInventoryAction(PendingInventoryAction.RemoveItem, itemId, 1);

            if (GUILayout.Button("+1", GUILayout.Width(34)))
                QueueInventoryAction(PendingInventoryAction.AddItem, itemId, 1);

            if (GUILayout.Button("Drop 1", GUILayout.Width(62)))
                QueueInventoryAction(PendingInventoryAction.DropItem, itemId, 1);

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawInventoryWeaponRows(PlayerInventory inventory)
        {
            IReadOnlyDictionary<string, GameWeaponInstance> weapons = inventory.Inventory.Weapons;
            if (weapons == null || weapons.Count == 0)
            {
                GUILayout.Label("<color=#AAAAAA>no weapons held. The Weapons tab equips them; " +
                                "GameInventory.AddWeapon is still a stub (no definition resolver).</color>");
                return;
            }

            foreach (var kvp in weapons)
            {
                GameWeaponInstance w = kvp.Value;
                if (w == null) continue;

                GUILayout.BeginHorizontal(_cardStyle);
                GUILayout.Label($"<b>{(w.Definition != null ? w.Definition.displayName : kvp.Key)}</b>", GUILayout.Width(200));
                GUILayout.Label($"<color=#AAAAAA>{kvp.Key}</color>", GUILayout.Width(110));
                GUILayout.Label($"<color=#9AD0FF>{w.Respect}</color>", GUILayout.Width(90));
                GUILayout.Label($"{w.HealthPercent:0}% hp", GUILayout.Width(70));
                GUILayout.Label($"{w.Mass:0.##}u", GUILayout.Width(60));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
        }

        private void DrawInventoryArmorRows(PlayerInventory inventory)
        {
            IReadOnlyDictionary<string, GameArmorInstance> armors = inventory.Inventory.Armors;
            if (armors == null || armors.Count == 0)
            {
                GUILayout.Label("<color=#AAAAAA>no armour held — use the Armor tab's \"add\" row</color>");
                return;
            }

            foreach (var kvp in armors)
            {
                GameArmorInstance a = kvp.Value;
                if (a == null) continue;

                GUILayout.BeginHorizontal(_cardStyle);
                GUILayout.Label($"<b>{(a.Definition != null ? a.Definition.displayName : kvp.Key)}</b>", GUILayout.Width(200));
                GUILayout.Label($"<color=#AAAAAA>{kvp.Key}</color>", GUILayout.Width(110));
                GUILayout.Label($"lvl {a.Level}", GUILayout.Width(60));
                GUILayout.Label($"{a.HealthPercent:0}% hp", GUILayout.Width(70));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
        }

        // =========================================================================
        // Spawn view — create an item out of nothing and put it in the world
        // =========================================================================

        /// <summary>
        /// The Spawn view: pick a category, pick an item from everything the registry knows, set an
        /// amount, and either drop it into the world or put it straight into the bag.
        ///
        /// <para><b>Why this is not just "Drop with a different id".</b> <c>Drop</c> moves something the
        /// player already holds, so its candidates come from the inventory and cannot fail to resolve.
        /// <c>Spawn</c> creates content from the registry, so it has to answer "what exists?" — a
        /// different query — and its candidates <i>can</i> legitimately fail to resolve. Two views keeps
        /// each one's failure modes visible instead of blending them.</para>
        ///
        /// <para><b>Weapons and armour are not offered as categories.</b> Weapons are a separate content
        /// type with a separate dictionary (<c>GameInventory.AddWeapon</c> is still a stub), so a weapon
        /// id resolves to no <see cref="ItemDefinition"/> and could not be taken into the item dict.
        /// Armour rows <i>are</i> item rows, so they do appear — under Misc, which is where
        /// <see cref="InventoryPageRules.PageOf"/> puts <see cref="ItemType.Equipment"/>.</para>
        ///
        /// <para><b>"Spawn in world" honours the shared placement row</b> (<see cref="DrawPlacementRow"/>),
        /// so an item can be put at a chosen distance in front of or behind the player — which is what
        /// makes collection testable without walking onto it. "Add to bag" ignores placement entirely:
        /// it never touches the world.</para>
        /// </summary>
        private void DrawInventorySpawnView(PlayerInventory inventory)
        {
            // ── category ──────────────────────────────────────────────────────
            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label("<color=#AAAAAA>Category:</color>", GUILayout.Width(72));

            for (int p = 0; p < InventoryPageRules.PageCount; p++)
            {
                var candidate = (InventoryPage)p;

                // Only the item-backed pages: offering Weapons/Armor here would produce a picker that
                // always reads "nothing to spawn", which looks like a broken filter.
                if (!InventoryPageRules.IsItemBacked(candidate)) continue;

                GUIStyle style = candidate == _spawnPage ? _tabActiveStyle : _tabInactiveStyle;
                if (GUILayout.Button(InventoryPageRules.Name(candidate), style, GUILayout.Width(72)))
                {
                    _spawnPage = candidate;
                    _pickerCacheValid = false;
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label($"<color=#AAAAAA>{inventory.AvailableItemIds.Count} item rows in the registry</color>");
            GUILayout.EndHorizontal();

            // ── where it lands ────────────────────────────────────────────────
            DrawPlacementRow();

            // ── search + amount + actions ─────────────────────────────────────
            List<string> candidates = GetPickerIds(inventory, _spawnPage, _spawnSearch);

            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label("<color=#AAAAAA>Find:</color>", GUILayout.Width(40));

            string typed = GUILayout.TextField(_spawnSearch ?? string.Empty, GUILayout.Width(150));
            if (typed != _spawnSearch)
            {
                _spawnSearch = typed;
                _pickerCacheValid = false;
            }

            GUILayout.Label("<color=#AAAAAA>×</color>", GUILayout.Width(14));
            _spawnQty = GUILayout.TextField(_spawnQty ?? "1", GUILayout.Width(46));

            GUILayout.Label(
                string.IsNullOrEmpty(_spawnSelectedId)
                    ? "<color=#AAAAAA>selected: <i>none</i></color>"
                    : $"<color=#9AD0FF>selected: <b>{_spawnSelectedId}</b></color>",
                GUILayout.Width(210));

            // The debug stand-in for the oracle's `LootGen` call site, which the port has no counterpart
            // for: without it no pickup could ever be spawned with AutoCollect set, so the walk-over
            // collector would be untestable through the only tool that creates loot. See
            // _spawnAutoCollect for why it defaults ON despite the oracle's conservative default.
            _spawnAutoCollect = GUILayout.Toggle(
                _spawnAutoCollect,
                new GUIContent(" auto-collect",
                    "Mark the spawned pickup auto-collectable: walking over it picks it up. This is the " +
                    "port of AS3 Loot.auto, which only LootGen (enemy drops) sets; player drops and " +
                    "room-placed items are deliberately false."),
                GUILayout.Width(108));

            if (GUILayout.Button("Spawn in world", GUILayout.Width(112)))
                SpawnSelected(inventory, worldOnly: true);

            if (GUILayout.Button("Add to bag", GUILayout.Width(90)))
                SpawnSelected(inventory, worldOnly: false);

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            DrawInventoryStatus();

            GUILayout.Label($"<color=#AAAAAA>showing {candidates.Count} of {inventory.AvailableItemIds.Count} " +
                            $"item rows on {InventoryPageRules.Name(_spawnPage)}</color>");

            _spawnScroll = GUILayout.BeginScrollView(_spawnScroll, GUILayout.ExpandHeight(true));

            if (candidates.Count == 0)
            {
                // Two different causes, named separately — an empty picker is otherwise indistinguishable
                // from an empty registry, and those have completely different fixes.
                GUILayout.Label(
                    $"<color=#AAAAAA>nothing to spawn — no item row on {InventoryPageRules.Name(_spawnPage)} " +
                    $"matches '{_spawnSearch}'. If the registry count above is 0, " +
                    "GameDatabase.Initialize() has not populated it yet.</color>");
            }
            else
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    string id = candidates[i];
                    ItemDefinition def = inventory.ResolveItem(id);

                    GUILayout.BeginHorizontal(_cardStyle);
                    GUILayout.Label($"<b>{(def != null ? def.displayName : id)}</b>", GUILayout.Width(180));
                    GUILayout.Label($"<color=#AAAAAA>{id}</color>", GUILayout.Width(120));
                    GUILayout.Label($"<color=#9AD0FF>{(def != null ? def.type.ToString() : "?")}</color>", GUILayout.Width(90));
                    GUILayout.Label($"<color=#FFD24A>cat{InventoryCategoryRules.ForItem(def)}</color>", GUILayout.Width(50));

                    GUIStyle style = id == _spawnSelectedId ? _tabActiveStyle : _tabInactiveStyle;
                    if (GUILayout.Button("Select", style, GUILayout.Width(64)))
                        _spawnSelectedId = id;

                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.EndScrollView();
        }

        /// <summary>
        /// The picker's candidate list, cached per (page, search). The overlay repaints every frame, so
        /// filtering ~500 ids and resolving each one per repaint would be a per-frame allocation and
        /// dictionary walk for a list that changes only when the user types. <see cref="_pickerCacheValid"/>
        /// is cleared by every input that can change the result.
        /// </summary>
        private List<string> GetPickerIds(PlayerInventory inventory, InventoryPage page, string search)
        {
            string normalised = search ?? string.Empty;

            if (!_pickerCacheValid || _pickerCachePage != page || _pickerCacheSearch != normalised)
            {
                _pickerCache = InventoryItemPicker.Filter(
                    inventory.AvailableItemIds,
                    id => inventory.ResolveItem(id)?.type,
                    id => inventory.ResolveItem(id)?.displayName,
                    page,
                    normalised);

                _pickerCachePage = page;
                _pickerCacheSearch = normalised;
                _pickerCacheValid = true;
            }

            return _pickerCache;
        }

        /// <summary>Spawn the picker's selection, either into the world or into the bag.</summary>
        private void SpawnSelected(PlayerInventory inventory, bool worldOnly)
        {
            if (string.IsNullOrEmpty(_spawnSelectedId))
            {
                SetInventoryStatus("select an item first", true);
                return;
            }

            if (!TryParsePositive(_spawnQty, out int qty))
            {
                SetInventoryStatus($"quantity '{_spawnQty}' is not a positive number", true);
                return;
            }

            if (!worldOnly)
            {
                InventoryCommandResult added = inventory.Submit(InventoryCommand.AddItem(_spawnSelectedId, qty));
                SetInventoryStatus(
                    added.Applied
                        ? $"added {qty} × '{_spawnSelectedId}' to the bag"
                        : $"rejected: {added.Reason}",
                    !added.Applied);
                return;
            }

            SpawnInWorld(inventory, _spawnSelectedId, qty, _spawnAutoCollect);
        }

        /// <summary>
        /// Put <paramref name="quantity"/> of an item into the world with no inventory half — the
        /// difference between this and <see cref="DropFromTab"/>, which also removes from the bag.
        ///
        /// <para>AS3 has no single counterpart. Enemies reach loot through <c>LootGen</c> and the player
        /// through <c>Invent.drop()</c>, and a container carries its own rows; "make one appear here" is
        /// the primitive all three are built from, so it is modelled directly rather than faked by
        /// adding-then-dropping (which would work, but would make an empty bag unable to spawn).</para>
        ///
        /// <para><b>Deliberately not routed through the inventory command seam.</b> The seam exists for
        /// inventory mutations, and this creates a <i>world entity</i> — a different replication problem
        /// (once netcode lands it is a host-authoritative spawn, not a client request). Faking it as
        /// add-then-drop would put it inside the seam at the cost of requiring bag room for something the
        /// bag is not involved in, so the seam stays honest about what it covers.</para>
        ///
        /// <para><paramref name="autoCollect"/> is passed straight to <see cref="WorldItemPickup.Spawn"/>
        /// and is the only way to make a pickup the walk-over collector will accept, because the port has
        /// no <c>LootGen</c> (the oracle's one <c>auto = true</c> call site) yet. The status line says which
        /// it was, so a spawn that "does not get picked up" is not mistaken for a broken collector.</para>
        /// </summary>
        private void SpawnInWorld(PlayerInventory inventory, string itemId, int quantity, bool autoCollect)
        {
            ItemDefinition definition = inventory.ResolveItem(itemId);
            if (definition == null)
            {
                SetInventoryStatus($"no item row for '{itemId}' — nothing to spawn", true);
                return;
            }

            Vector3 where = DropPosition();
            WorldItemPickup pickup = WorldItemPickup.Spawn(definition, quantity, where, autoCollect);

            SetInventoryStatus(
                pickup != null
                    ? $"spawned {quantity} × '{itemId}' at {where.x:0.##},{where.y:0.##}" +
                      (autoCollect ? " (auto-collect)" : " (cursor only)")
                    : $"the world refused to spawn '{itemId}' — see the Console for the reason",
                pickup == null);
        }

        /// <summary>
        /// The world position a drop or spawn lands at. Shared by <see cref="SpawnInWorld"/> and
        /// <see cref="DropFromTab"/> so the two cannot disagree about where "in front of the player" is.
        ///
        /// <para><b>Facing is read here, not stored.</b> <see cref="_placementAnchor"/> names a direction
        /// and this resolves it against the player's live <c>FacingDirection</c> at the moment of the
        /// spawn, so turning around moves "in front" with you. Baking a sign into a text field at button
        /// time would freeze it to whatever the player faced when the tester last typed — the failure
        /// would look like the control doing nothing.</para>
        ///
        /// <para><b>Facing falls back to <c>1</c> (right) with no player.</b> The overlay can be opened
        /// on a scene with no <see cref="PlayerController"/> at all (the harness arm of
        /// <see cref="EnsureInventory"/>), where "front" has no meaning; right is the same default the
        /// unit itself starts on (<c>UnitController._facingDirection = 1</c>).</para>
        /// </summary>
        private Vector3 DropPosition()
        {
            Vector3 origin = _livePlayer != null
                ? _livePlayer.transform.position
                : (_liveInventory != null ? _liveInventory.transform.position : Vector3.zero);

            int facing = _livePlayer != null ? _livePlayer.FacingDirection : 1;
            if (facing == 0) facing = 1;

            if (!TryParseFloat(_placementDistance, out float distance)) distance = 1.5f;

            switch (_placementAnchor)
            {
                case PlacementAnchor.AtFeet:
                    return new Vector3(origin.x, origin.y, 0f);

                case PlacementAnchor.InFront:
                    return new Vector3(origin.x + facing * distance, origin.y, 0f);

                case PlacementAnchor.Behind:
                    return new Vector3(origin.x - facing * distance, origin.y, 0f);

                // Named explicitly (not left to `default`) so that adding a new anchor is a compile-time
                // hole a reviewer sees, rather than a new mode that silently inherits the raw offset.
                // `default` stays for an out-of-range value, which an enum can always hold.
                case PlacementAnchor.Custom:
                default:
                    if (!TryParseFloat(_dropOffsetX, out float offsetX)) offsetX = 0f;
                    if (!TryParseFloat(_dropOffsetY, out float offsetY)) offsetY = -0.5f;
                    return new Vector3(origin.x + offsetX, origin.y + offsetY, 0f);
            }
        }

        /// <summary>
        /// The placement control, rendered identically by the Spawn and Drop views.
        ///
        /// <para><b>One row shows exactly the field that is live.</b> Under <see cref="PlacementAnchor.Custom"/>
        /// it shows the raw X/Y offset fields; under the facing-relative anchors it shows the single
        /// distance field instead. Showing both at once would leave the inactive pair on screen silently
        /// doing nothing — a typed value that changes no behaviour is worse than a hidden one.</para>
        /// </summary>
        private void DrawPlacementRow()
        {
            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label("<color=#AAAAAA>Place:</color>", GUILayout.Width(46));

            DrawAnchorButton("at feet", PlacementAnchor.AtFeet);
            DrawAnchorButton("in front", PlacementAnchor.InFront);
            DrawAnchorButton("behind", PlacementAnchor.Behind);
            DrawAnchorButton("custom x/y", PlacementAnchor.Custom);

            if (_placementAnchor == PlacementAnchor.Custom)
            {
                GUILayout.Label("<color=#AAAAAA>x</color>", GUILayout.Width(12));
                _dropOffsetX = GUILayout.TextField(_dropOffsetX ?? "0", GUILayout.Width(46));
                GUILayout.Label("<color=#AAAAAA>y</color>", GUILayout.Width(12));
                _dropOffsetY = GUILayout.TextField(_dropOffsetY ?? "-0.5", GUILayout.Width(46));
            }
            else
            {
                GUILayout.Label("<color=#AAAAAA>distance</color>", GUILayout.Width(56));
                _placementDistance = GUILayout.TextField(_placementDistance ?? "1.5", GUILayout.Width(46));
            }

            // Which way "front" currently points. Without it the two facing-relative anchors are
            // indistinguishable until the item appears, and a tester checking both sides has no way to
            // confirm the player actually turned.
            string arrow = _livePlayer == null
                ? "<color=#777777>no player — assuming →</color>"
                : (_livePlayer.FacingDirection < 0
                    ? "<color=#9AD0FF>facing ←</color>"
                    : "<color=#9AD0FF>facing →</color>");
            GUILayout.Label(arrow, GUILayout.Width(150));

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        /// <summary>One anchor button, active-styled while it is the selected anchor.</summary>
        private void DrawAnchorButton(string label, PlacementAnchor anchor)
        {
            GUIStyle style = _placementAnchor == anchor ? _tabActiveStyle : _tabInactiveStyle;
            if (GUILayout.Button(label, style, GUILayout.Width(78)))
                _placementAnchor = anchor;
        }

        /// <summary>
        /// The Drop view: move something the player actually holds into the world, and show/collect what
        /// is already lying there.
        ///
        /// <para><b>The picker lists the inventory, not the registry.</b> AS3's <c>Invent.drop()</c> takes
        /// an item the inventory already has, so offering anything else would produce a guaranteed
        /// rejection — the candidate list is <c>inventory.Items</c> and every row carries its held count.
        /// The by-id row is kept as a fallback for dropping something you hold whose row is scrolled out
        /// of view.</para>
        ///
        /// <para><b>Placement exists because the oracle drops at the owner's own position</b>
        /// (<c>owner.X, owner.Y - owner.scY / 2</c>), and standing on the item you just dropped is a poor
        /// way to test picking it up. The control is shared with the Spawn view — see
        /// <see cref="DrawPlacementRow"/> — so the two views cannot place "in front" differently.</para>
        ///
        /// <para><b>Both lists' buttons defer.</b> The held list is drawn from <c>inventory.Items</c> and
        /// "Drop 1" removes from it; the on-the-ground list walks <c>WorldItemPickup.Live</c> by index and
        /// "Take"/"Destroy" remove from it. Mutating either inline broke the first (an
        /// <c>InvalidOperationException</c> from the dictionary enumerator, reported from play) and
        /// silently skipped a row in the second. The view still takes no <c>PlayerController</c> parameter
        /// for that reason — the only thing that needed one was <c>Take</c>, which now runs in the flush.
        /// The placement row reads the cached <see cref="_livePlayer"/> instead, for its facing.</para>
        /// </summary>
        private void DrawInventoryDropView(PlayerInventory inventory)
        {
            IReadOnlyDictionary<string, GameItemInstance> held = inventory.Items;

            // ── where it lands (the same control the Spawn view shows) ────────
            DrawPlacementRow();

            // ── by-id fallback ────────────────────────────────────────────────
            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label("<color=#AAAAAA>by id</color>", GUILayout.Width(40));
            _dropId = GUILayout.TextField(_dropId ?? string.Empty, GUILayout.Width(140));
            GUILayout.Label("<color=#AAAAAA>×</color>", GUILayout.Width(14));
            _dropQty = GUILayout.TextField(_dropQty ?? "1", GUILayout.Width(46));

            if (GUILayout.Button("Drop", GUILayout.Width(56)))
            {
                if (!TryParsePositive(_dropQty, out int qty))
                    SetInventoryStatus($"quantity '{_dropQty}' is not a positive number", true);
                else if (string.IsNullOrEmpty(_dropId))
                    SetInventoryStatus("type an item id first", true);
                else
                    DropFromTab(inventory, _dropId, qty);
            }

            if (GUILayout.Button("use the add row's id", GUILayout.Width(150)))
            {
                _dropId = _invAddId;
                SetInventoryStatus(
                    string.IsNullOrEmpty(_dropId) ? "the add row is empty too" : $"drop id set to '{_dropId}'",
                    false);
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // ── drop from the actual inventory ────────────────────────────────
            GUILayout.Label("<b>Drop from your inventory</b>");

            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label(
                string.IsNullOrEmpty(_dropSelectedId)
                    ? "<color=#AAAAAA>selected: <i>none</i> — click Select on a row</color>"
                    : $"<color=#9AD0FF>selected: <b>{_dropSelectedId}</b></color>",
                GUILayout.Width(250));
            GUILayout.Label("<color=#AAAAAA>×</color>", GUILayout.Width(14));
            _dropSelectQty = GUILayout.TextField(_dropSelectQty ?? "1", GUILayout.Width(46));

            if (GUILayout.Button("Drop selected", GUILayout.Width(110)))
            {
                if (!TryParsePositive(_dropSelectQty, out int qty))
                    SetInventoryStatus($"quantity '{_dropSelectQty}' is not a positive number", true);
                else if (string.IsNullOrEmpty(_dropSelectedId))
                    SetInventoryStatus("select a held item first", true);
                else
                    DropFromTab(inventory, _dropSelectedId, qty);
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (held == null || held.Count == 0)
            {
                GUILayout.Label("<color=#AAAAAA>your inventory holds no items — use the Spawn tab, or the " +
                                "add row on the Aid/Misc/Ammo pages</color>");
            }
            else
            {
                _dropHeldScroll = GUILayout.BeginScrollView(_dropHeldScroll, GUILayout.Height(132));

                foreach (var kvp in held)
                {
                    GameItemInstance inst = kvp.Value;
                    if (inst?.Definition == null) continue;

                    GUILayout.BeginHorizontal(_cardStyle);
                    GUILayout.Label($"<b>{inst.Definition.displayName}</b>", GUILayout.Width(170));
                    GUILayout.Label($"<color=#AAAAAA>{kvp.Key}</color>", GUILayout.Width(110));
                    GUILayout.Label($"<color=#9AD0FF>held <b>{inst.Quantity}</b></color>", GUILayout.Width(90));

                    GUIStyle style = kvp.Key == _dropSelectedId ? _tabActiveStyle : _tabInactiveStyle;
                    if (GUILayout.Button("Select", style, GUILayout.Width(64)))
                    {
                        _dropSelectedId = kvp.Key;

                        // Pre-fill the whole stack only while the field is still the untouched default,
                        // so a tester dropping 1 of 50 is not forced to retype it after every selection.
                        if (_dropSelectQty == "1") _dropSelectQty = inst.Quantity.ToString();
                    }

                    // Queued, not dropped inline: this button is drawn inside a foreach over
                    // inventory.Items, and DropFromTab removes from that very dictionary — the exact
                    // `InvalidOperationException: Collection was modified` this view used to throw.
                    if (GUILayout.Button("Drop 1", GUILayout.Width(62)))
                        QueueInventoryAction(PendingInventoryAction.DropItem, kvp.Key, 1);

                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }

                GUILayout.EndScrollView();
            }

            DrawInventoryStatus();
            GUILayout.Space(6);

            // ── on the ground ─────────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color=#AAAAAA>On the ground: <b>{WorldItemPickup.All.Count}</b> pickup(s)</color>");
            if (GUILayout.Button("Clear all", GUILayout.Width(80)))
            {
                int removed = WorldItemPickup.DespawnAll();
                SetInventoryStatus($"cleared {removed} pickup(s) from the world", false);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            _pickupScroll = GUILayout.BeginScrollView(_pickupScroll, GUILayout.ExpandHeight(true));

            if (WorldItemPickup.All.Count == 0)
            {
                GUILayout.Label("<color=#AAAAAA>nothing on the ground yet — Spawn or Drop something</color>");
            }
            else
            {
                for (int i = 0; i < WorldItemPickup.All.Count; i++)
                {
                    WorldItemPickup pickup = WorldItemPickup.All[i];
                    if (pickup == null) continue;

                    GUILayout.BeginHorizontal(_cardStyle);
                    GUILayout.Label($"<b>{pickup.ItemId}</b>", GUILayout.Width(140));
                    GUILayout.Label($"×{pickup.Quantity}", GUILayout.Width(50));

                    // The walk-over flag, shown because otherwise "I walked over it and nothing happened"
                    // is indistinguishable from a broken collector: a pickup spawned with auto-collect
                    // off is only ever takeable with the cursor, which is the oracle's own behaviour.
                    GUILayout.Label(
                        pickup.AutoCollect
                            ? "<color=#7CE38B>auto</color>"
                            : "<color=#777777>cursor</color>",
                        GUILayout.Width(50));

                    GUILayout.Label($"<color=#AAAAAA>at {pickup.Position.x:0.##},{pickup.Position.y:0.##}</color>", GUILayout.Width(150));

                    // Both queued: this list walks WorldItemPickup.Live by index and Despawn() removes
                    // from it, so doing either inline silently skipped the next row (Take also mutates the
                    // inventory dictionary). Deferring also makes the index loop's re-read Count stable.
                    if (GUILayout.Button("Take", GUILayout.Width(60)))
                        QueuePickupAction(PendingInventoryAction.TakePickup, pickup);

                    if (GUILayout.Button("Destroy", GUILayout.Width(76)))
                        QueuePickupAction(PendingInventoryAction.DestroyPickup, pickup);

                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.EndScrollView();
        }

        /// <summary>Queue an inventory mutation to run after the current draw pass. See the field block
        /// above <see cref="_pendingInventoryAction"/> for why this exists at all.</summary>
        private void QueueInventoryAction(PendingInventoryAction action, string itemId, int quantity)
        {
            _pendingInventoryAction = action;
            _pendingItemId = itemId;
            _pendingQuantity = quantity;
            _pendingPickup = null;
        }

        /// <summary>Queue a world-pickup action to run after the current draw pass.</summary>
        private void QueuePickupAction(PendingInventoryAction action, WorldItemPickup pickup)
        {
            _pendingInventoryAction = action;
            _pendingPickup = pickup;
            _pendingItemId = pickup != null ? pickup.ItemId : string.Empty;
            _pendingQuantity = 0;
        }

        /// <summary>
        /// Run whatever a button queued, now that every loop that drew it has finished.
        ///
        /// <para><b>Must be called after <c>EndScrollView</c>, not inside it</b> — a scroll view is a
        /// layout region, and mutating the data it just laid out from the inside leaves the region's
        /// Begin/End unbalanced against what it measured.</para>
        /// </summary>
        private void FlushPendingInventoryAction(PlayerController player, PlayerInventory inventory)
        {
            PendingInventoryAction action = _pendingInventoryAction;
            if (action == PendingInventoryAction.None) return;

            // Cleared first: the handlers below can set a status message and, for a drop, clear the
            // Drop view's selection — none of which should be able to re-enter this method.
            _pendingInventoryAction = PendingInventoryAction.None;

            switch (action)
            {
                case PendingInventoryAction.AddItem:
                    SubmitInventory(inventory, InventoryCommand.AddItem(_pendingItemId, _pendingQuantity),
                                    $"added {_pendingQuantity} × '{_pendingItemId}'");
                    break;

                case PendingInventoryAction.RemoveItem:
                    SubmitInventory(inventory, InventoryCommand.RemoveItem(_pendingItemId, _pendingQuantity),
                                    $"removed {_pendingQuantity} × '{_pendingItemId}'");
                    break;

                case PendingInventoryAction.DropItem:
                    DropFromTab(inventory, _pendingItemId, _pendingQuantity);
                    break;

                case PendingInventoryAction.TakePickup:
                    if (_pendingPickup != null)
                    {
                        // The same IInteractable the player's action key calls, so this exercises the real
                        // collection path rather than a parallel one.
                        WorldItemPickup taken = _pendingPickup;
                        string takenId = taken.ItemId;
                        taken.Interact(player.gameObject);

                        // Interact() despawns the pickup ONLY when the seam accepted the add, so membership
                        // of the live list is the honest success signal. (Not a null check on the component:
                        // Destroy is deferred to end of frame, so `taken == null` would be false even on a
                        // successful take and the message would claim the opposite of the truth.)
                        bool leftTheWorld = !WorldItemPickup.All.Contains(taken);
                        SetInventoryStatus(
                            leftTheWorld
                                ? $"took '{takenId}' via its interactable"
                                : $"'{takenId}' stayed in the world — the inventory refused it (see the Console)",
                            !leftTheWorld);
                    }
                    break;

                case PendingInventoryAction.DestroyPickup:
                    if (_pendingPickup != null) _pendingPickup.Despawn();
                    break;
            }
        }

        private void SubmitInventory(PlayerInventory inventory, InventoryCommand command, string successMessage)
        {
            InventoryCommandResult result = inventory.Submit(command);
            SetInventoryStatus(result.Applied ? successMessage : $"rejected: {result.Reason}", !result.Applied);
        }

        private void DropFromTab(PlayerInventory inventory, string itemId, int quantity)
        {
            Vector3 where = DropPosition();

            if (!inventory.DropItem(itemId, quantity, where, out string error))
            {
                SetInventoryStatus($"drop refused: {error}", true);
                return;
            }

            SetInventoryStatus($"dropped {quantity} × '{itemId}' at {where.x:0.##},{where.y:0.##}", false);

            // Dropping the last of a stack removes the very row the Drop view's picker is pointing at, so
            // the selection is cleared rather than left naming something no longer held — which would read
            // as "the picker is broken" on the next press.
            if (inventory.Items == null || !inventory.Items.ContainsKey(itemId))
            {
                _dropSelectedId = string.Empty;
            }
        }

        /// <summary>
        /// Parse without throwing. <c>int.TryParse</c> rather than a try/catch so a half-typed field is a
        /// stated rejection instead of an exception from inside the debug tool you opened to debug.
        /// </summary>
        private static bool TryParsePositive(string text, out int value)
        {
            value = 0;
            if (!int.TryParse(text, out int parsed)) return false;
            if (parsed <= 0) return false;
            value = parsed;
            return true;
        }

        private static bool TryParseFloat(string text, out float value)
            => float.TryParse(text, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// The tab strip's wrapping arithmetic, as a pure function.
    /// </summary>
    /// <remarks>
    /// <para><b>Extracted because this rule has now been wrong once and the defect was arithmetic, not
    /// drawing.</b> The strip used to budget a hard-coded 4 px per button for "GUILayout's spacing" while
    /// GUILayout really advances by the style's own <c>margin</c>; with eleven tabs the difference
    /// accumulated across ten gaps and pushed the last tab past the window's right edge. Nothing went red,
    /// because the only way to see it was to look at the running editor.</para>
    ///
    /// <para>Pure and engine-free on purpose — it takes measured widths, not styles — so the wrap can be
    /// asserted offline. <c>PlayerDebugEditorOverlay</c> measures (<c>CalcSize</c> + <c>margin</c>,
    /// <c>GUILayoutUtility.GetRect</c> for the row width) and this decides. Mirrors
    /// <c>UnitPickerLayout</c>, which exists for the same reason: the unit picker's viewport and rows
    /// disagreed because each guessed the row height separately.</para>
    /// </remarks>
    public static class TabStripLayout
    {
        /// <summary>
        /// How much horizontal room one tab consumes in a <c>GUILayout</c> row: the caption's measured
        /// width plus the style's own margin, which is what GUILayout advances by between elements.
        /// </summary>
        public static float TabWidth(float measuredCaptionWidth, float styleMarginHorizontal)
        {
            return measuredCaptionWidth + styleMarginHorizontal;
        }

        /// <summary>
        /// The index each row starts at. Row 0 always starts at 0, so the result has one entry per row and
        /// the rows partition <c>[0, widths.Count)</c>.
        /// </summary>
        /// <remarks>
        /// Greedy, and <b>never splits a tab</b>: a row takes tabs until the next one would pass
        /// <paramref name="available"/>, then the next row begins. Two guards matter:
        /// <list type="bullet">
        /// <item><c>used &gt; 0</c> — a single tab wider than the whole strip gets a row to itself rather
        /// than wrapping before every tab, which would produce an empty row before each one.</item>
        /// <item>a non-positive <paramref name="available"/> — degenerate (a window collapsed to nothing)
        /// and would otherwise put one tab per row; treated as "one row", the honest degradation.</item>
        /// </list>
        /// <para>Widths are assumed to be the already-combined per-tab advances from
        /// <see cref="TabWidth"/>; passing raw caption widths reintroduces exactly the bug this class
        /// exists to prevent.</para>
        /// </remarks>
        public static int[] RowStarts(IReadOnlyList<float> widths, float available)
        {
            if (widths == null || widths.Count == 0)
            {
                return Array.Empty<int>();
            }

            var starts = new List<int> { 0 };

            if (available <= 0f)
            {
                return starts.ToArray();
            }

            float used = 0f;
            for (int i = 0; i < widths.Count; i++)
            {
                float width = widths[i] > 0f ? widths[i] : 0f;

                if (used > 0f && used + width > available)
                {
                    starts.Add(i);
                    used = 0f;
                }

                used += width;
            }

            return starts.ToArray();
        }

        /// <summary>
        /// Whether the strip needs more than one row — i.e. whether it wraps at all. A read of
        /// <see cref="RowStarts"/>, exposed because a test asserting "the new tab fits on one row" is
        /// clearer than counting row starts.
        /// </summary>
        public static bool Wraps(IReadOnlyList<float> widths, float available)
        {
            return RowStarts(widths, available).Length > 1;
        }
    }
}
