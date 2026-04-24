namespace PFE.Entities.Player
{
    /// <summary>
    /// High-level locomotion modes that animation and gameplay can react to.
    /// </summary>
    public enum PlayerLocomotionState
    {
        Grounded = 0,
        Crouched = 1,
        JumpRise = 2,
        Fall = 3,
        Dash = 4,
        Ladder = 5,
        Swim = 6,
        Levitate = 7,
        Knockback = 8,
        Disabled = 9,
        Teleporting = 10
    }
}
