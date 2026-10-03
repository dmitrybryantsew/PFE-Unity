using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.RPG;
using UnityEngine;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace PFE.Entities.Player
{
    /// <summary>
    /// Stub implementation of ILocomotionAbilities backed by SerializeField toggles.
    /// Test abilities in the inspector without needing perk UI.
    /// Later: swap backing store to CharacterStats queries (Step 6).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerLocomotionAbilities : MonoBehaviour, ILocomotionAbilities
    {
#if ODIN_INSPECTOR
        [FoldoutGroup("Jump Abilities")]
#else
        [Header("Jump Abilities")]
#endif
        [SerializeField] private bool canDoubleJump;

#if ODIN_INSPECTOR
        [FoldoutGroup("Jump Abilities")]
#endif
        [SerializeField]
        [Tooltip("1 = normal jump only, 2 = double jump")]
        [Range(1, 3)]
        private int maxJumpCount = 1;

#if ODIN_INSPECTOR
        [FoldoutGroup("Jump Abilities")]
#endif
        [SerializeField]
        [Tooltip("Multiplier applied to base jump force (from perks)")]
        [Range(0.5f, 2f)]
        private float jumpForceMultiplier = 1f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Jump Abilities")]
#endif
        [SerializeField]
        [Tooltip("Force multiplier for air jumps relative to ground jump (e.g. 0.8 = 80%)")]
        [Range(0.5f, 1f)]
        private float airJumpForceRatio = 0.8f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Testing")]
#endif
        [Header("Testing")]
        [SerializeField]
        [Tooltip("Infinite mana for testing levitation without mana drain")]
        private bool infiniteMana;

#if ODIN_INSPECTOR
        [FoldoutGroup("Levitation")]
#endif
        [Header("Levitation")]
        [SerializeField] private bool canLevitate;

#if ODIN_INSPECTOR
        [FoldoutGroup("Levitation")]
#endif
        [SerializeField]
        [Tooltip("Max height above ground for levitation (0 = use UnitDefinition default)")]
        private float levitationMaxHeight;

#if ODIN_INSPECTOR
        [FoldoutGroup("Levitation")]
#endif
        [SerializeField]
        [Tooltip("Levitation vertical acceleration (0 = use UnitDefinition default)")]
        private float levitationAcceleration;

#if ODIN_INSPECTOR
        [FoldoutGroup("Levitation")]
#endif
        [SerializeField]
        [Tooltip("Mana cost per physics tick while levitating")]
        [Range(0f, 10f)]
        private float levitationManaCostPerTick = 0.5f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Levitation")]
#endif
        [SerializeField]
        [Tooltip("Extra mana cost per tick when ascending")]
        [Range(0f, 10f)]
        private float levitationManaCostUpward = 0.3f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Movement Abilities")]
#endif
        [Header("Movement Abilities")]
        [SerializeField] private bool canAirDash;

#if ODIN_INSPECTOR
        [FoldoutGroup("Movement Abilities")]
#endif
        [SerializeField] private bool canWallJump;

#if ODIN_INSPECTOR
        [FoldoutGroup("Movement Abilities")]
#endif
        [SerializeField]
        [Tooltip("Multiplier applied to base move speed (from perks)")]
        [Range(0.5f, 3f)]
        private float moveSpeedMultiplier = 1f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Teleport")]
#endif
        [Header("Teleport")]
        [SerializeField] private bool canTeleport;

#if ODIN_INSPECTOR
        [FoldoutGroup("Teleport")]
#endif
        [SerializeField]
        [Tooltip("Hold duration before teleport executes (AS3: portTime=25 frames ≈ 0.42s)")]
        [Range(0f, 2f)]
        private float teleportChargeTimeSeconds = 0.42f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Teleport")]
#endif
        [SerializeField]
        [Tooltip("Mana cost per teleport (AS3: portMana=25)")]
        [Range(0f, 100f)]
        private float teleportManaCost = 25f;

#if ODIN_INSPECTOR
        [FoldoutGroup("Teleport")]
#endif
        [SerializeField]
        [Tooltip("Cooldown between teleports in seconds (AS3: portDown=300 frames ≈ 5s)")]
        [Range(0f, 10f)]
        private float teleportCooldownSeconds = 5f;

        private UnitDefinition _definition;
        private CharacterStats _characterStats;

        private void Awake()
        {
            var unitController = GetComponent<UnitController>();
            _definition = unitController != null ? unitController.Stats : null;
            _characterStats = GetComponent<CharacterStats>() ?? GetComponentInParent<CharacterStats>();

            // Pull defaults from UnitDefinition if not overridden
            if (_definition != null)
            {
                if (levitationMaxHeight <= 0f) levitationMaxHeight = _definition.levitationMaxHeight;
                if (levitationAcceleration <= 0f) levitationAcceleration = _definition.levitationAcceleration;
            }
            else
            {
                if (levitationMaxHeight <= 0f) levitationMaxHeight = 60f;
                if (levitationAcceleration <= 0f) levitationAcceleration = 1.6f;
            }

            // Sync maxJumpCount with canDoubleJump toggle
            if (CanDoubleJump && maxJumpCount < 2) maxJumpCount = 2;
        }

        /// <summary>
        /// The organ/budget stats for this player, resolved lazily and cached.
        ///
        /// <para><b>Public because <see cref="PlayerLocomotionController"/> needs it, not because
        /// anything outside the player does.</b> The controller owns the teleport's affordability test
        /// and routes it through <see cref="CharacterStats.CanAffordMagicMana"/> so the full-pool
        /// bypass lives in exactly one place; that call needs this reference. It was <c>private</c>
        /// until the mana split, when the call moved here — the move is what made the accessor's
        /// visibility load-bearing, and the compiler is the only thing that noticed.</para>
        ///
        /// <para>Exposing it rather than having the controller resolve its own keeps one resolution
        /// path: this accessor already falls back to <c>GetComponentInParent</c>, and a second copy of
        /// that walk would be a second place to get it wrong.</para>
        /// </summary>
        public CharacterStats CharacterStats => _characterStats != null ? _characterStats : (_characterStats = GetComponent<CharacterStats>() ?? GetComponentInParent<CharacterStats>());

        // --- ILocomotionAbilities ---

        public bool InfiniteMana => infiniteMana;

        public bool CanDoubleJump => canDoubleJump || (CharacterStats != null && CharacterStats.isDJ > 0);
        public bool CanLevitate => canLevitate || (CharacterStats != null && CharacterStats.levitOn > 0);
        public bool CanAirDash => canAirDash;
        public bool CanWallJump => canWallJump;
        public int MaxJumpCount => CanDoubleJump ? Mathf.Max(maxJumpCount, 2) : 1;
        public float JumpForceMultiplier => jumpForceMultiplier;
        public float AirJumpForceRatio => airJumpForceRatio;
        public float MoveSpeedMultiplier => moveSpeedMultiplier * (CharacterStats != null ? CharacterStats.allSpeedMult : 1f);
        public float LevitationMaxHeight => levitationMaxHeight;
        public float LevitationAcceleration => levitationAcceleration;
        /// <summary>
        /// The per-tick levitation upkeep, INCLUDING <c>allDManaMult</c>.
        ///
        /// <para>AS3 applies that multiplier to the whole upkeep term at <c>UnitPlayer.as:1320</c>
        /// (<c>dmana *= pers.allDManaMult</c>) before either pool is charged, so a perk that scales
        /// mana costs must scale levitation too. The port omitted it, which made every such perk
        /// inert for levitation.</para>
        /// </summary>
        public float LevitationManaCostPerTick => AllDManaMult * (
            (CharacterStats != null && CharacterStats.levitDMana > 0f)
                ? CharacterStats.levitDMana
                : levitationManaCostPerTick);

        /// <summary>The ascending surcharge, also scaled by <c>allDManaMult</c> (<c>UnitPlayer.as:1313</c>).</summary>
        public float LevitationManaCostUpward => AllDManaMult * (
            (CharacterStats != null && CharacterStats.levitDManaUp > 0f)
                ? CharacterStats.levitDManaUp
                : levitationManaCostUpward);

        private float AllDManaMult => CharacterStats != null ? CharacterStats.allDManaMult : 1f;
        public bool CanTeleport => canTeleport || (CharacterStats != null && CharacterStats.portPoss > 0);
        public float TeleportChargeTimeSeconds => teleportChargeTimeSeconds;
        /// <summary>
        /// Teleport's cost — AS3 <c>Pers.portMana</c> (25) scaled by <c>allDManaMult</c>
        /// (<c>UnitPlayer.as:1633</c>, <c>mana &lt; pers.portMana * pers.allDManaMult</c>).
        /// Read from CharacterStats so a <c>&lt;sk&gt;</c> on <c>portMana</c> reaches it; the
        /// serialized field is the fallback for a player with no CharacterStats.
        /// </summary>
        public float TeleportManaCost => AllDManaMult * (
            CharacterStats != null ? CharacterStats.portMana : teleportManaCost);
        public float TeleportCooldownSeconds => teleportCooldownSeconds;
    }
}
