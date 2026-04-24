using UnityEngine;

namespace PFE.Core
{
    /// <summary>
    /// Simple 2D camera follow for the player.
    /// Smoothly follows the target with optional lookahead and bounds.
    /// </summary>
    public class CameraFollow : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("The target to follow (usually the player)")]
        public Transform target;

        [Header("Follow Settings")]
        [Tooltip("How quickly the camera catches up to target movement")]
        [Range(0.1f, 10f)]
        public float smoothSpeed = 5f;

        [Tooltip("Offset from the target position")]
        public Vector3 offset = new Vector3(0, 0, -10);

        [Header("Pixel Snap")]
        [Tooltip("Snaps the camera to the world pixel grid to reduce sprite/background blur from subpixel movement.")]
        public bool snapToPixelGrid = true;

        [Tooltip("World pixels per Unity unit. Matches the map renderer's 100 px = 1 unit convention.")]
        [Min(1f)]
        public float pixelsPerUnit = 100f;

        [Header("Bounds (Optional)")]
        [Tooltip("Should the camera stay within bounds?")]
        public bool useBounds = false;

        [Tooltip("Minimum bounds for camera position")]
        public Vector2 minBounds;

        [Tooltip("Maximum bounds for camera position")]
        public Vector2 maxBounds;

        private void LateUpdate()
        {
            if (target == null)
                return;

            // Calculate desired position
            Vector3 desiredPosition = target.position + offset;

            // Apply bounds if enabled
            if (useBounds)
            {
                desiredPosition.x = Mathf.Clamp(desiredPosition.x, minBounds.x, maxBounds.x);
                desiredPosition.y = Mathf.Clamp(desiredPosition.y, minBounds.y, maxBounds.y);
            }

            // Smoothly interpolate to desired position
            Vector3 smoothedPosition = Vector3.Lerp(
                transform.position,
                desiredPosition,
                smoothSpeed * UnityEngine.Time.deltaTime
            );

            if (snapToPixelGrid)
            {
                smoothedPosition = SnapToPixelGrid(smoothedPosition);
            }

            transform.position = smoothedPosition;
        }

        /// <summary>
        /// Set the target to follow at runtime.
        /// </summary>
        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
        }

        /// <summary>
        /// Set camera bounds based on room dimensions.
        /// </summary>
        public void SetBounds(Vector2 min, Vector2 max)
        {
            minBounds = min;
            maxBounds = max;
            useBounds = true;
        }

        /// <summary>
        /// Clear bounds and allow free camera movement.
        /// </summary>
        public void ClearBounds()
        {
            useBounds = false;
        }

        private Vector3 SnapToPixelGrid(Vector3 position)
        {
            float unitsPerPixel = 1f / Mathf.Max(1f, pixelsPerUnit);
            position.x = Mathf.Round(position.x / unitsPerPixel) * unitsPerPixel;
            position.y = Mathf.Round(position.y / unitsPerPixel) * unitsPerPixel;
            return position;
        }
    }
}
