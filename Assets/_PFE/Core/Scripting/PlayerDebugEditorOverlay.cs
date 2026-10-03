using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Inventory;
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
        private int _activeTab = 0; // 0: Pip Stats, 1: Skills, 2: Perks, 3: Weapons, 4: Armor, 5: Vitals & Presets

        private static readonly string[] TabNames = new string[]
        {
            "📊 Pip Stats",
            "⚡ Skills (18)",
            "🌟 Perks",
            "⚔️ Weapons",
            "🛡️ Armor",
            "❤️ Vitals & Presets"
        };

        // Scroll positions for each tab
        private Vector2 _pipStatsScroll;
        private Vector2 _skillsScroll;
        private Vector2 _perksScroll;
        private Vector2 _weaponsScroll;
        private Vector2 _armorScroll;
        private Vector2 _presetsScroll;

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

        // Known 20 visual sets with sprites in PlayerAnimationDefinition
        private static readonly string[] VisualArmorIds = new string[]
        {
            "pip", "tre", "chitin", "kombu", "skin", "metal", "assault", "battle",
            "magus", "antirad", "antihim", "intel", "astealth", "moon", "sapper",
            "power", "polic", "spec", "encl", "ali"
        };

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
                var allItems = Resources.LoadAll<ItemDefinition>("Items");
                if (allItems != null)
                {
                    _allArmorItems = allItems.Where(it =>
                        it != null &&
                        (it.inventoryCategory == InventoryCategory.Apparel ||
                         it.armorTip > 0 ||
                         it.armorHP > 0 ||
                         VisualArmorIds.Contains(it.itemId, StringComparer.OrdinalIgnoreCase))).ToArray();
                }
            }
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

            // Post-Game / Special Skills (Cap 100)
            GUILayout.Label("<b>Post-Game & Special Skills (Cap 100)</b>", _subHeaderStyle);
            string[] specialSkills = new string[] { "attack", "defense", "knowl", "life", "spirit" };
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
                case "life": return "Vitality & HP";
                case "spirit": return "Spirit & Mana";
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

                    DrawArmorRow(player, armorId, armorId == curArmorId);
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

        private void EquipArmorById(PlayerController player, string armorId, ItemDefinition itemDef = null)
        {
            if (player?.Stats == null) return;

            if (itemDef == null)
            {
                itemDef = Resources.Load<ItemDefinition>($"Items/{armorId}");
            }

            if (itemDef == null)
            {
                // Create a runtime item wrapper for visual sets that don't have matching standalone ItemDefinition
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
            charStats.SetSkillLevel("life", 40);

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
            charStats.SetSkillLevel("spirit", 40);

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
