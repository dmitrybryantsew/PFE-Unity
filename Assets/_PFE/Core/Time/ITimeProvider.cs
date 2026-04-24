namespace PFE.Core.Time
{
    /// <summary>
    /// Abstraction for Unity's Time system to enable unit testing.
    /// Allows time to be controlled/simulated in tests without running the actual Unity engine.
    /// </summary>
    public interface ITimeProvider
    {
        /// <summary>
        /// The time at the beginning of this frame (Read Only).
        /// This is the time in seconds since the start of the game.
        /// </summary>
        float CurrentTime { get; }

        /// <summary>
        /// The time in seconds it took to complete the last frame (Read Only).
        /// Use this to make your game frame rate independent.
        /// </summary>
        float DeltaTime { get; }
    }
}
