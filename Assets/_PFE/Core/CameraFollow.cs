using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Core
{
    /// <summary>
    /// Simple 2D camera follow for the player, plus the room-static mode of the AS3 original.
    /// Smoothly follows the target with optional lookahead and bounds, or — when
    /// <see cref="StaticRoomCamera"/> is on — parks on the centre of the room and fits the whole
    /// room into the viewport.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the mode flag is static.</b> The camera component is serialized into
    /// <c>SampleScene.unity</c> with its <c>target</c> wired to the Player prefab instance, and
    /// <c>MapBridge.SetupCameraFollow</c> also configures it at runtime. The F2 tab and the two
    /// room-transition snaps are all separate callers that need the SAME answer. A serialized field
    /// would give each of them a different component to ask (and no answer at all when the component
    /// is missing), which is exactly the failure mode the Inspector-wired camera invites. One static
    /// value behind every front end, read live, with no cached copy.</para>
    ///
    /// <para><b>Oracle.</b> <c>fe/inter/Camera.as:setLoc()</c> sets <c>moved=false</c> and centres the
    /// room container when the room fits the stage. The shipped default <c>zoom100=false</c> selects
    /// <c>isZoom=2</c> (<c>scaleV = min(1280/1920, 800/1000)</c>), so a standard 48x25 room fits and
    /// <c>calc()</c> clamps to <c>-maxvx/2, -maxvy/2</c> — a fixed centred view.
    /// <c>fe/inter/PipPageOpt.as:594-615</c> toggles that live. This is that option.</para>
    /// </remarks>
    public class CameraFollow : MonoBehaviour
    {
        /// <summary>
        /// True = the room-static camera of the AS3 default; false = the port's player-follow camera.
        /// Runtime-only for now (the toggle lives in the F2 debug panel); not persisted to an options
        /// file. Read live every <c>LateUpdate</c>, so flipping it takes effect on the next frame.
        /// </summary>
        public static bool StaticRoomCamera { get; set; }

        /// <summary>Standard room size in Unity units: 48 x 25 cells of 40 px at 100 px/unit.</summary>
        public static Vector2 RoomSizeUnity => WorldConstants.ROOM_SIZE_PIXELS / 100f;

        /// <summary>
        /// The centre of the room cell containing <paramref name="playerWorldUnity"/>, in Unity units.
        /// Deliberately mirrors <c>RoomTransitionManager</c>'s "no target door -> centre of room"
        /// arithmetic, including the land-Y flip: a room at row r spans [-r*H, (1-r)*H).
        /// </summary>
        /// <remarks>
        /// Derived from the player's position rather than from a room-change event, so door
        /// transitions, edge walks, restored rooms and save loads all re-centre with no wiring.
        /// Standard-sized rooms only; AS3's oversized <c>zoom &gt; 1</c> rooms are not modelled by
        /// the port's room grid yet.
        /// </remarks>
        public static Vector3 GetStaticRoomCenter(Vector3 playerWorldUnity)
        {
            Vector2 pixel = WorldCoordinates.UnityToPixel(playerWorldUnity);
            Vector3Int land = WorldCoordinates.WorldToLand(pixel);
            Vector2 centerPixel = new Vector2(
                (land.x + 0.5f) * WorldConstants.ROOM_SIZE_PIXELS.x,
                WorldCoordinates.LandRowToWorldPixelY(land.y) + 0.5f * WorldConstants.ROOM_SIZE_PIXELS.y);
            return WorldCoordinates.PixelToUnity(centerPixel);
        }

        /// <summary>
        /// Orthographic size that fits the whole room at the current aspect ratio — the port's
        /// equivalent of AS3 <c>scaleV = min(screenX/maxsx, screenY/maxsy)</c>. At 16:9 this is
        /// 5.4 rather than the scene's 5.0, which is the difference between seeing all 19.2 units
        /// of room width and clipping ~140 px off the sides.
        /// </summary>
        public static float GetStaticOrthoSize(float aspect)
        {
            Vector2 room = RoomSizeUnity;
            return Mathf.Max(room.y * 0.5f, room.x * 0.5f / Mathf.Max(0.1f, aspect));
        }

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

        private Camera _camera;

        /// <summary>
        /// The orthographic size the scene shipped with, captured the first time static mode runs so
        /// switching back restores the real value instead of a hardcoded 5. Negative = not captured.
        /// </summary>
        private float _followOrthographicSize = -1f;

        private void LateUpdate()
        {
            if (_camera == null)
                _camera = GetComponent<Camera>();
            Camera cam = _camera;

            // The static branch comes BEFORE the target null-guard: a room-static camera needs no
            // player to know where to sit, and the F2 tab must work in a scene with no player.
            if (StaticRoomCamera)
            {
                Vector3 anchor = target != null ? target.position : transform.position;
                Vector3 center = GetStaticRoomCenter(anchor);
                Vector3 parked = center + new Vector3(0f, 0f, offset.z);
                if (snapToPixelGrid)
                    parked = SnapToPixelGrid(parked);
                transform.position = parked;

                if (cam != null)
                {
                    if (_followOrthographicSize < 0f)
                        _followOrthographicSize = cam.orthographicSize;
                    float fit = GetStaticOrthoSize(cam.aspect);
                    if (!Mathf.Approximately(cam.orthographicSize, fit))
                        cam.orthographicSize = fit;
                }
                return;
            }

            // Leaving static mode: restore the framing the scene was authored with, exactly once.
            if (_followOrthographicSize >= 0f)
            {
                if (cam != null)
                    cam.orthographicSize = _followOrthographicSize;
                _followOrthographicSize = -1f;
            }

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
