using System;
using PFE.Character;
using PFE.Character.Animation;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Magic;
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
        [SerializeField] internal CharacterAnimationDefinition _definition;
        [SerializeField] internal CharacterStyleData           _styleData;

        [Header("Armour binding")]
        [Tooltip("Auto-resolved from this GameObject or a parent when left empty.")]
        [SerializeField] PlayerController _player;

        CharacterSpriteAssembler _assembler;
        CharacterVisualContext   _context = CharacterVisualContext.Default;

        UnitStats _stats;
        CompositeDisposable _disposables;

        /// <summary>Stops <c>Start</c> rebinding a stats source that was already bound explicitly.</summary>
        bool _bound;

        /// <summary>
        /// The last shield visibility this view pushed, so <see cref="Update"/> can tell a rise from a
        /// fall. Held here rather than read back off the context because the context is a struct copied
        /// into the assembler — reading it back would report the assembler's state, not this view's last
        /// decision.
        /// </summary>
        bool _shieldVisible;

        /// <summary>
        /// Levitation source for the horn aura. Resolved lazily and allowed to be null: a player rig
        /// without a locomotion controller still draws, it just never reaches the aura's 100 state.
        /// </summary>
        PlayerLocomotionController _locomotion;
        bool _locomotionResolved;

        /// <summary>
        /// The aura's current charge, 0..100 — AS3 <c>aMC</c> (<c>UnitPlayer.as:287</c>, which
        /// initialises it to 50 so the glow fades <i>in</i> at spawn rather than snapping). Ramped
        /// here rather than in the renderer so the renderer stays a thing that draws a number.
        /// </summary>
        float _hornAuraLevel = HornAura.LevelWeaponDrawn;

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

        /// <summary>
        /// Drives the shield graphic off <c>shithp</c> — AS3 <c>UnitPlayer.as:4916-4924</c>.
        ///
        /// <para><b>Polled, not subscribed, and that is the oracle's shape rather than laziness.</b>
        /// <c>shithp</c> is a plain field on <see cref="UnitStats"/>, not a <c>ReactiveProperty</c>:
        /// three of its four producers are <i>not</i> the spell system (four bosses set it directly), so
        /// there is no single write to hook. AS3 reads it every frame in the player's vis update, and so
        /// does this — a view may poll; the sim may not.</para>
        ///
        /// <para>Only the two edges are pushed into the context. Pushing the level every frame would
        /// rebuild the sprite composition 60 times a second for a value that changes on a cast and on a
        /// break.</para>
        /// </summary>
        void Update()
        {
            if (_stats == null) return;

            float shitHp = _stats.ShitHp;

            if (SpellShield.ShouldShow(shitHp, _shieldVisible))
            {
                SetShieldVisible(true);
            }
            else if (SpellShield.ShouldHide(shitHp, _shieldVisible))
            {
                SetShieldVisible(false);
            }

            UpdateHornAura();
        }

        /// <summary>
        /// Walks the horn's glow toward its target — AS3 <c>aMC</c> chasing <c>aMagic</c>
        /// (<c>UnitPlayer.as:1478-1486</c>), written out at <c>:4851-4852</c> as
        /// <c>alpha = aMC / 100</c>. See <see cref="HornAura"/> for the whole rule.
        ///
        /// <para><b>Which half of the rule is wired.</b> The 100 state — levitating, or holding a
        /// telekinesis target of mass ≥ 1 — is driven from
        /// <see cref="PlayerLocomotionController.IsLevitating"/>, which is the state the port actually
        /// models. The 50 state is "a weapon whose <c>vis</c> is visible is drawn"
        /// (<c>:1142-1150</c>), and the port has no equivalent signal: weapons here are data, with no
        /// in-hand model whose <c>visible</c> flag could be read, so there is nothing honest to test.
        /// The consequence is bounded and visible rather than silent: the glow reaches full brightness
        /// but never sits at half, and it never comes on merely because a gun is out.</para>
        ///
        /// <para><b>Polled, like the shield.</b> <c>IsLevitating</c> is a plain bool on a controller
        /// that owns its own update loop, and the ramp has to run every frame anyway to be a ramp — so
        /// there is no edge to subscribe to.</para>
        /// </summary>
        void UpdateHornAura()
        {
            if (_assembler == null)
            {
                return;
            }

            if (!_locomotionResolved)
            {
                _locomotionResolved = true;
                _locomotion = GetComponentInParent<PlayerLocomotionController>();
            }

            bool levitating = _locomotion != null && _locomotion.IsLevitating;
            float target = levitating ? HornAura.LevelLevitate : HornAura.LevelIdle;

            float next = HornAura.StepLevel(_hornAuraLevel, target);
            if (Mathf.Approximately(next, _hornAuraLevel))
            {
                return;
            }

            _hornAuraLevel = next;
            _assembler.HornAuraLevel = next;
        }

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

        /// <summary>
        /// Force the horn aura's charge, 0..100 — see <see cref="HornAura"/>.
        ///
        /// <para>Exists because <see cref="UpdateHornAura"/> can only reach two of the original's
        /// three states: levitation (100) and nothing (0). The third, "a weapon whose <c>vis</c> is
        /// visible is drawn" (50), needs a signal the port does not have yet, so until it does this is
        /// the seam a caller can drive instead of the rule being invented here.</para>
        /// </summary>
        public void SetHornAuraLevel(float level)
        {
            _hornAuraLevel = Mathf.Clamp(level, HornAura.LevelIdle, HornAura.LevelMax);
            if (_assembler != null)
            {
                _assembler.HornAuraLevel = _hornAuraLevel;
            }
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

        /// <summary>
        /// Show or hide the spell shield — AS3's <c>vis.shit.visible</c> plus its
        /// <c>gotoAndPlay(1)</c>/<c>gotoAndStop(1)</c>.
        ///
        /// <para>Called from <see cref="Update"/> on the two edges
        /// <see cref="SpellShield.ShouldShow"/>/<see cref="SpellShield.ShouldHide"/> decide, so the
        /// rule stays in the tested class and this method stays a setter. Pushing the context is the
        /// whole of its job: <c>vis.shit</c> is an <b>overlay clip</b>, not a body part, and
        /// <see cref="CharacterSpriteAssembler"/> owns that clip's playhead — it restarts on the rise
        /// edge and resets on the fall edge, which is exactly <c>gotoAndPlay(1)</c>/<c>gotoAndStop(1)</c>.
        /// So nothing here advances frames, and nothing here needs to.</para>
        /// </summary>
        public void SetShieldVisible(bool visible)
        {
            _shieldVisible = visible;
            _context.showShield = visible;
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
