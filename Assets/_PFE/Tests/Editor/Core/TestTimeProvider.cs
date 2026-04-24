namespace PFE.Tests.Editor.Core
{
    using PFE.Core.Time;

    /// <summary>
    /// Test implementation of ITimeProvider for unit testing.
    /// Allows manual control of time for deterministic tests.
    /// </summary>
    public class TestTimeProvider : ITimeProvider
    {
        public float CurrentTime { get; set; }
        public float DeltaTime { get; set; }

        /// <summary>
        /// Initialize with default values (simulating ~60 FPS).
        /// </summary>
        public TestTimeProvider()
        {
            CurrentTime = 0f;
            DeltaTime = 0.016f; // ~60 FPS
        }

        /// <summary>
        /// Initialize with specific time values.
        /// </summary>
        public TestTimeProvider(float currentTime, float deltaTime)
        {
            CurrentTime = currentTime;
            DeltaTime = deltaTime;
        }

        /// <summary>
        /// Advance time by a specific amount.
        /// Useful for simulating time passage in tests.
        /// </summary>
        public void AdvanceTime(float seconds)
        {
            CurrentTime += seconds;
        }

        /// <summary>
        /// Simulate a specific number of frames at a given FPS.
        /// </summary>
        public void AdvanceFrames(int frames, float fps = 30f)
        {
            float deltaTime = 1f / fps;
            for (int i = 0; i < frames; i++)
            {
                CurrentTime += deltaTime;
            }
        }

        /// <summary>
        /// Reset time to zero.
        /// </summary>
        public void Reset()
        {
            CurrentTime = 0f;
            DeltaTime = 0.016f;
        }
    }
}
