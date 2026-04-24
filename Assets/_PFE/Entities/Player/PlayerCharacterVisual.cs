using PFE.Character;
using PFE.Character.Animation;
using PFE.Core;
using PFE.Data.Definitions;
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

        CharacterSpriteAssembler _assembler;
        CharacterVisualContext   _context = CharacterVisualContext.Default;

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

        // ── Public runtime API ───────────────────────────────────────────────

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
            _assembler.VisualContext = _context;
        }

        /// <summary>
        /// Grant or revoke the wing-flight ability.
        /// Wings are acquired in-game via a potion — not part of base appearance.
        /// </summary>
        public void SetWingsVisible(bool visible)
        {
            _context.showWings = visible;
            _assembler.VisualContext = _context;
        }

        /// <summary>Hide or show the mane (e.g. certain helmets hide it).</summary>
        public void SetHideMane(bool hide)
        {
            _context.hideMane = hide;
            _assembler.VisualContext = _context;
        }

        /// <summary>Make the character fully transparent (e.g. stealth effect).</summary>
        public void SetTransparent(bool transparent)
        {
            _context.transparent = transparent;
            _assembler.VisualContext = _context;
        }
    }
}
