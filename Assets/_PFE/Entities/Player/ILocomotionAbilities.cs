namespace PFE.Entities.Player
{
    /// <summary>
    /// Contract that the locomotion controller queries for perk/ability-modified movement capabilities.
    /// Backed by inspector toggles for testing, later swapped to CharacterStats queries.
    /// </summary>
    public interface ILocomotionAbilities
    {
        bool CanDoubleJump { get; }
        bool CanLevitate { get; }
        bool CanAirDash { get; }
        bool CanWallJump { get; }
        int MaxJumpCount { get; }
        float JumpForceMultiplier { get; }
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
