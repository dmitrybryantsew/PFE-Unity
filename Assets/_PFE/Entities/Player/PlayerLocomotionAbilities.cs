using PFE.Data.Definitions;
using PFE.Entities.Units;
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

        private void Awake()
        {
            var unitController = GetComponent<UnitController>();
            _definition = unitController != null ? unitController.Stats : null;

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
            if (canDoubleJump && maxJumpCount < 2) maxJumpCount = 2;
        }

        // --- ILocomotionAbilities ---

        public bool InfiniteMana => infiniteMana;

        public bool CanDoubleJump => canDoubleJump;
        public bool CanLevitate => canLevitate;
        public bool CanAirDash => canAirDash;
        public bool CanWallJump => canWallJump;
        public int MaxJumpCount => canDoubleJump ? Mathf.Max(maxJumpCount, 2) : 1;
        public float JumpForceMultiplier => jumpForceMultiplier;
        public float AirJumpForceRatio => airJumpForceRatio;
        public float MoveSpeedMultiplier => moveSpeedMultiplier;
        public float LevitationMaxHeight => levitationMaxHeight;
        public float LevitationAcceleration => levitationAcceleration;
        public float LevitationManaCostPerTick => levitationManaCostPerTick;
        public float LevitationManaCostUpward => levitationManaCostUpward;
        public bool CanTeleport => canTeleport;
        public float TeleportChargeTimeSeconds => teleportChargeTimeSeconds;
        public float TeleportManaCost => teleportManaCost;
        public float TeleportCooldownSeconds => teleportCooldownSeconds;
    }
}
