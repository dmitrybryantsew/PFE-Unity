using UnityEngine;

namespace PFE.Core.Time
{
    public class UnityTimeProvider : ITimeProvider
    {
        public float CurrentTime => UnityEngine.Time.time;

        public float DeltaTime => UnityEngine.Time.deltaTime;
    }
}
