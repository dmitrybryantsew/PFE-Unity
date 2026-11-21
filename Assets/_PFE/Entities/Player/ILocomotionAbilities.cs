namespace PFE.Entities.Player
{
    /// <summary>
    /// Contract that the locomotion controller queries for perk/ability-modified movement capabilities.
    /// Backed by inspector toggles for testing, later swapped to CharacterStats queries.
    /// </summary>
    public interface ILocomotionAbilities
    {
        /// <summary>True when mana gates are bypassed — the inspector "infinite mana" test toggle,
        /// or a network body whose client already paid the real cost. Read through the interface
        /// (never a concrete-type pattern match) so a body wearing a different implementation is
        /// not silently treated as a mana-paying player.</summary>
        bool InfiniteMana { get; }
        bool CanDoubleJump { get; }
        bool CanLevitate { get; }
        bool CanAirDash { get; }
        bool CanWallJump { get; }
        int MaxJumpCount { get; }
        float JumpForceMultiplier { get; }
        /// <summary>Force multiplier for air jumps relative to the ground jump. On the interface so
        /// ability implementations that are not the inspector-backed sheet still carry it.</summary>
        float AirJumpForceRatio { get; }
        float MoveSpeedMultiplier { get; }
        float LevitationMaxHeight { get; }
        float LevitationAcceleration { get; }
        float LevitationManaCostPerTick { get; }
        float LevitationManaCostUpward { get; }
        bool CanTeleport { get; }
        float TeleportChargeTimeSeconds { get; }
        float TeleportManaCost { get; }
        float TeleportCooldownSeconds { get; }
    }
}
