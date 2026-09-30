using System;
using PFE.Character;
using PFE.Character.Animation;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using R3;
using UnityEngine;

namespace PFE.Entities.Player
{
    /// <summary>
    /// Bootstraps the CharacterSpriteAssembler on the player GameObject at game-scene start.
    ///
    /// Reads CharacterAppearance from GameBootData (set by the main-menu new-game flow)
    /// and calls assembler.Setup() so the player looks correct from frame 1.
    ///
    /// Also provides runtime API for equipping armor and granting wings (potion effect).
    ///
    /// <para><b>Armour is driven from <c>UnitStats</c>, not called by hand.</b> <see cref="Start"/>
    /// resolves the <c>PlayerController</c> and binds <see cref="UnitStats.ArmourId"/>, so equipping,
    /// unequipping, swapping and breaking armour all update the sprite with no call site to remember.
    /// Before this existed, <see cref="SetArmor"/> had exactly one caller — an Editor preview tool —
    /// so armour could be equipped in the data layer while the character never changed.</para>
    ///
    /// Execution order -150: runs in Awake before CharacterAnimationDriver.Start() (-50).
    ///
    /// Setup (add to the player GameObject):
    ///   1. Assign _definition  → PlayerAnimationDefinition asset
    ///   2. Assign _styleData   → PlayerStyleData asset
    ///   3. CharacterSpriteAssembler is found automatically via RequireComponent.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    [RequireComponent(typeof(CharacterSpriteAssembler))]
    [DisallowMultipleComponent]
    public sealed class PlayerCharacterVisual : MonoBehaviour
    {
        [Header("Animation assets")]
        [SerializeField] CharacterAnimationDefinition _definition;
        [SerializeField] CharacterStyleData           _styleData;

        [Header("Armour binding")]
        [Tooltip("Auto-resolved from this GameObject or a parent when left empty.")]
        [SerializeField] PlayerController _player;

        CharacterSpriteAssembler _assembler;
        CharacterVisualContext   _context = CharacterVisualContext.Default;

        UnitStats _stats;
        CompositeDisposable _disposables;

        /// <summary>Stops <c>Start</c> rebinding a stats source that was already bound explicitly.</summary>
        bool _bound;

        // ── Unity lifecycle ──────────────────────────────────────────────────

        void Awake()
        {
            _assembler = GetComponent<CharacterSpriteAssembler>();

            if (_definition == null || _styleData == null)
            {
                Debug.LogError(
                    "[PlayerCharacterVisual] _definition or _styleData not assigned. " +
                    "Drag the PlayerAnimationDefinition and PlayerStyleData assets in the Inspector.",
                    this);
                return;
            }

            // Read appearance from the new-game boot data (set in main menu).
            // PeekPendingNewGame does NOT consume the settings — the game-scene bootstrapper
            // (or save system) is responsible for consuming them.
            CharacterAppearance appearance =
                GameBootData.PeekPendingNewGame()?.appearance
                ?? CharacterAppearance.CreateDefault();

            _assembler.Setup(_definition, _styleData, appearance, _context);
        }

        // Bound in Start, not Awake, deliberately: this component runs at -150, but PlayerController
        // has no execution order and creates its UnitStats in its own Awake (order 0). Binding from
        // Awake here would read a null Stats on every scene load.
        void Start()
        {
            if (_bound)
            {
                return;
            }

            if (_player == null)
            {
                _player = GetComponentInParent<PlayerController>();
            }

            if (_player == null || _player.Stats == null)
            {
                Debug.LogWarning(
                    "[PlayerCharacterVisual] No PlayerController/UnitStats found, so equipped armour " +
                    "will not show on the character. Equipping and damage still work; only the sprite " +
                    "is affected.", this);
                return;
            }

            BindToStats(_player.Stats);
        }

        void OnDestroy() => _disposables?.Dispose();

        // ── Public runtime API ───────────────────────────────────────────────

        /// <summary>
        /// Drive the armour sprite from a stats source — the port of AS3
        /// <c>UnitPlayer.changeArmor()</c>'s visual half, which writes <c>Appear.ggArmorId</c> and
        /// <c>Appear.hideMane</c> and then calls <c>refreshVis()</c> (<c>UnitPlayer.as:3809-3819</c>).
        ///
        /// <para>Subscribes to <see cref="UnitStats.ArmourId"/> rather than
        /// <see cref="UnitStats.HasArmour"/> because a swap between two armours leaves a bool
        /// unchanged and would raise no notification. <c>ReactiveProperty</c> replays on subscribe,
        /// so armour equipped before the bind is picked up without a separate initial read.</para>
        /// </summary>
        public void BindToStats(UnitStats stats)
        {
            if (stats == null) throw new ArgumentNullException(nameof(stats));

            // Replace, don't accumulate — the HUD views' rule, and the same leak applies here.
            _disposables?.Dispose();
            _disposables = new CompositeDisposable();

            _stats = stats;
            _bound = true;

            stats.ArmourId.Subscribe(_ => ApplyArmour()).AddTo(_disposables);
        }

        /// <summary>
        /// Push the currently-equipped armour onto the sprite. Reads <c>ArmourItem</c> rather than
        /// taking it as a parameter because it is the source of truth for
        /// <see cref="IArmourItem.HideMane"/>, and it is already consistent with
        /// <see cref="UnitStats.ArmourId"/> by the time the notification arrives — both are written
        /// in <c>UnitStats.SetArmour</c>.
        /// </summary>
        void ApplyArmour()
        {
            string id = _stats != null ? _stats.ArmourId.CurrentValue : string.Empty;
            IArmourItem item = _stats != null ? _stats.ArmourItem : null;

            SetArmor(id);

            // A broken plate arrives here as empty id + null item, because UnitStats.ApplyDamage
            // unequips on break — which is how the sprite comes off, the job AS3 gives
            // changeArmor("off") inside Armor.damage() (Armor.as:331-334).
            SetHideMane(item != null && item.HideMane);
        }

        /// <summary>Replace the player's visual appearance (e.g. after loading a save).</summary>
        public void ApplyAppearance(CharacterAppearance appearance)
        {
            if (appearance == null) return;
            _assembler.Appearance = appearance.Clone();
        }

        /// <summary>Equip or unequip an armor set by its ID.</summary>
        public void SetArmor(string armorId)
        {
            _context.armorId = armorId ?? string.Empty;
            PushContext();
        }

        /// <summary>
        /// Grant or revoke the wing-flight ability.
        /// Wings are acquired in-game via a potion — not part of base appearance.
        /// </summary>
        public void SetWingsVisible(bool visible)
        {
            _context.showWings = visible;
            PushContext();
        }

        /// <summary>Hide or show the mane (e.g. certain helmets hide it).</summary>
        public void SetHideMane(bool hide)
        {
            _context.hideMane = hide;
            PushContext();
        }

        /// <summary>Make the character fully transparent (e.g. stealth effect).</summary>
        public void SetTransparent(bool transparent)
        {
            _context.transparent = transparent;
            PushContext();
        }

        /// <summary>
        /// Record the change on the context and hand it to the assembler. The context is written even
        /// when the assembler is missing, and the null guard matters: <see cref="Awake"/> returns
        /// early — after logging an error — when the animation assets are unassigned, and these
        /// setters are reachable from <see cref="BindToStats"/> before anyone notices.
        /// </summary>
        void PushContext()
        {
            if (_assembler != null)
            {
                _assembler.VisualContext = _context;
            }
        }
    }
}
