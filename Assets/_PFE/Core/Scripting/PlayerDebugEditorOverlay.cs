using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PFE.Core;
using PFE.Data;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Effects;
using PFE.Systems.Inventory;
using PFE.Systems.Magic;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
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
        private int _activeTab = 0; // 0: Pip Stats, 1: Skills, 2: Perks, 3: Weapons, 4: Armor, 5: Vitals & Presets, 6: Effects, 7: Spells

        private static readonly string[] TabNames = new string[]
        {
            "📊 Pip Stats",
            "⚡ Skills (18)",
            "🌟 Perks",
            "⚔️ Weapons",
            "🛡️ Armor",
            "❤️ Vitals & Presets",
            "☣️ Effects",
            "🔮 Spells"
        };

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
        private int _weaponCategoryFilter = 0; // 0: All, 1: Handgun, 2: SMG/Rifle, 3: Shotgun, 4: Heavy, 5: Melee, 6: Magic/Energy, 7: Thrown

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
        /// Live inventory created by this overlay, if any. The player has no inventory wired at runtime
        /// (<c>PlayerWeaponLoadout.AmmoSource</c> is only ever assigned in tests), so "Give ammo" has
        /// nothing to add to until one is made — see <see cref="EnsureInventory"/>.
        /// </summary>
        private GameInventory _debugInventory;

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
            GUILayout.BeginHorizontal();
            for (int i = 0; i < TabNames.Length; i++)
            {
                GUIStyle style = (i == _activeTab) ? _tabActiveStyle : _tabInactiveStyle;
                if (GUILayout.Button(TabNames[i], style, GUILayout.Height(28)))
                {
                    _activeTab = i;
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            // ── Active Tab View ──────────────────────────────────────────────────
            if (player == null)
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
            }

            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 24));
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

            // Filter & Search Toolbar
            GUILayout.BeginHorizontal(_cardStyle);
            GUILayout.Label("Search:", GUILayout.Width(50));
            _weaponSearch = GUILayout.TextField(_weaponSearch, GUILayout.Width(160));
            if (!string.IsNullOrEmpty(_weaponSearch) && GUILayout.Button("✕", GUILayout.Width(24)))
            {
                _weaponSearch = string.Empty;
            }

            GUILayout.Space(10);
            string[] cats = { "All", "Pistols", "Rifles/SMG", "Shotgun", "Heavy", "Melee", "Energy/Magic", "Thrown" };
            _weaponCategoryFilter = GUILayout.Toolbar(_weaponCategoryFilter, cats, GUILayout.Height(22));
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

                    // Category filter
                    if (!MatchesWeaponCategory(weapon, _weaponCategoryFilter)) continue;

                    // Text search
                    if (!string.IsNullOrEmpty(_weaponSearch))
                    {
                        if (wid.IndexOf(_weaponSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                            weapon.weaponType.ToString().IndexOf(_weaponSearch, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                    }

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
        private void DrawAmmoTypeRow(IWeaponController curController, WeaponDefinition curDef)
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

            // Give ammo into the live inventory (created on first use — see EnsureInventory).
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
                GUILayout.Label("<color=#AAAAAA>no inventory wired — press \"Give 30\" to create one</color>");
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
        /// Add <paramref name="amount"/> rounds of <paramref name="ammoId"/> to the debug inventory,
        /// creating and wiring that inventory on first use.
        ///
        /// <para><b>Why the inventory has to be created here.</b> Nothing in the running game constructs a
        /// <c>GameInventory</c> or assigns <c>PlayerWeaponLoadout.AmmoSource</c> — only tests do — so every
        /// reload currently takes the <c>_ammoSource == null</c> branch in
        /// <c>RangedWeaponController.CompleteReload</c> and fills the magazine unconditionally
        /// (infinite-ammo / training behaviour). Without a source, "give ammo" would have nowhere to put
        /// it and no visible effect at all.</para>
        ///
        /// <para><b>The side effect is real and intended:</b> once this runs, reloads draw from the
        /// inventory and stop being infinite — a weapon with an empty inventory reloads to 0. That is the
        /// honest behaviour, and it is why the button says "create one" rather than pretending ammo is
        /// already being tracked.</para>
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

            // Quantity is applied by AddItem's own stacking rules; when a stack already exists it merges.
            bool added = _debugInventory.AddItem(item, amount);

            Debug.Log($"[PlayerDebugEditorOverlay] Gave {amount} × '{ammoId}' " +
                      $"(now holding {_debugInventory.GetAmmoCount(ammoId)}).");

            return added;
        }

        /// <summary>
        /// Create the debug inventory and point the loadout at it, if that has not happened yet.
        ///
        /// <para><b>Assigning <c>loadout.AmmoSource</c> is the whole point</b> — its setter rebuilds the
        /// controller factory so subsequently-equipped weapons pick the source up. A weapon equipped
        /// <i>before</i> this call keeps the factory it was built with, so the change takes effect from the
        /// next equip (or the next overlay action). The overlay does not force a re-equip, because silently
        /// swapping the player's weapon to make a debug button take effect is worse than saying so.</para>
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
            if (loadout?.AmmoSource is GameInventory existing)
            {
                // Something already supplied a source (a test harness, or the future inventory
                // system). Adopt it rather than shadowing it, or "Give" would add to a bag nothing
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

        private bool MatchesWeaponCategory(WeaponDefinition w, int catIndex)
        {
            switch (catIndex)
            {
                case 0: return true;
                case 1: return w.weaponType == WeaponType.Guns && (w.weaponId.Contains("pistol") || w.weaponId.Contains("10") || w.weaponId.Contains("revolver") || w.weaponId.Contains("32") || w.weaponId.Contains("magnum"));
                case 2: return w.weaponType == WeaponType.Guns && (w.weaponId.Contains("rifle") || w.weaponId.Contains("smg") || w.weaponId.Contains("carbine") || w.weaponId.Contains("assault") || w.weaponId.Contains("sniper"));
                case 3: return w.weaponType == WeaponType.Guns && (w.weaponId.Contains("shot") || w.weaponId.Contains("drob"));
                case 4: return w.weaponType == WeaponType.BigGun || w.weaponId.Contains("mini") || w.weaponId.Contains("flamer") || w.weaponId.Contains("rocket");
                case 5: return w.weaponType == WeaponType.Melee;
                case 6: return w.weaponType == WeaponType.Magic || w.damageType == DamageType.Laser || w.damageType == DamageType.Plasma;
                case 7: return w.weaponType == WeaponType.Thrown;
                default: return true;
            }
        }

        private void DrawWeaponRow(PlayerWeaponLoadout loadout, WeaponDefinition weapon, bool isEquipped)
        {
            GUIStyle card = isEquipped ? _cardActiveStyle : _cardStyle;
            GUILayout.BeginHorizontal(card);

            string title = isEquipped ? $"<color=#55FF55><b>▶ {weapon.weaponId}</b></color>" : $"<b>{weapon.weaponId}</b>";
            GUILayout.Label(title, GUILayout.Width(170));
            GUILayout.Label(weapon.weaponType.ToString(), _badgeStyle, GUILayout.Width(75));
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
    }
}
