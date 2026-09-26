using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Lightweight runtime layer for dynamic map props.
    /// Keeps simple room-local motion and collision off the main object list so
    /// inert props do not need per-object physics behaviors.
    /// </summary>
    public sealed class RoomObjectPhysicsLayer
    {
        /// <summary>
        /// The legacy per-frame step. Deliberately <b>not</b> the canonical sim step: the per-frame
        /// driver calls <see cref="Update"/> once per rendered frame and has always used 1/60, so this
        /// value is kept for that path. The sim-driven path passes <c>SimClock.SimDt</c> (1/30)
        /// explicitly — see <c>PfeDebugSettings.SimTickRoom</c>. Public so the room heartbeat and this
        /// default cannot drift apart.
        /// </summary>
        public const float LegacyPerFrameDeltaTime = 1f / 60f;

        // ── Physics tuning surface ───────────────────────────────────────────────────────────────
        // Public so tests can pin them. This is deliberate: the projectile census found that the
        // reason four divergent gravity/acceleration literals coexisted for years was that none of
        // them was assertable. Every value below carries its AS3 citation for the same reason.

        /// <summary>
        /// Fall acceleration. AS3 applies <c>dy += World.ddy</c> once per 30 Hz frame
        /// (<c>Box.as:895</c>) and <c>World.ddy</c> is <c>1</c> px/frame² (<c>World.as:46</c>), so the
        /// correct value is <c>1 × 30² = 900</c> px/s². The old 1800 was 2× too strong — the same
        /// doubling as the divergent <c>ddy</c> literals reconciled in the projectile census.
        /// </summary>
        public const float GravityPixelsPerSecond = 900f;

        /// <summary>
        /// Terminal fall speed. AS3 gates the integrator with <c>if(!isPlav &amp;&amp; dy &lt;
        /// World.maxdy)</c> (<c>Box.as:893</c>) where <c>World.maxdy = 20</c> px/frame
        /// (<c>World.as:48</c>) → <c>20 × 30 = 600</c> px/s. The port had no clamp at all, so a long
        /// fall could exceed AS3's terminal velocity.
        /// </summary>
        public const float MaxFallSpeedPixelsPerSecond = 600f;

        /// <summary>
        /// Ground friction. AS3 damps horizontal speed on landing: <c>dx *= 0.92</c>, and snaps to a
        /// full stop when <c>|dx| &lt; 5</c> px/frame (<c>Box.as:1090-1096</c>). Applied here as a
        /// <i>rate</i> — <c>Pow(0.92, deltaTime × 30)</c> — so one 1/30 sim tick is exactly one AS3
        /// frame and the legacy 1/60 per-frame path decays at the same speed per unit time.
        /// </summary>
        public const float GroundFriction = 0.92f;
        public const float GroundStopPixelsPerFrame = 5f;

        /// <summary>Stop threshold in the port's px/s units (<c>5 × 30 = 150</c>).</summary>
        public const float GroundStopPixelsPerSecond =
            GroundStopPixelsPerFrame * PFE.Core.SimClock.CanonicalTicksPerSecond;

        /// <summary>
        /// Telekinesis follow rate. <b>Not</b> an AS3 constant: AS3 models telekinesis as the
        /// <c>levit</c> flag plus a 0.8 per-frame damping in <c>forces()</c> (<c>Box.as:907-910</c>),
        /// not as position tracking, so there is no AS3 number to copy. Left as-is and flagged rather
        /// than silently "corrected" to a value with no citation.
        /// </summary>
        const float TelekinesisFollowSpeed = 14f;

        const float MinimumImpactSpeed = 220f;
        const float ThrowGraceDuration = 0.18f;

        /// <summary>
        /// Maximum movement per collision check. AS3 substeps with <c>World.maxdelta = 9</c> px
        /// (<c>World.as:52</c>, used at <c>Box.as:610-620</c>); the port used 8. The substep count is
        /// AS3's <c>floor(max / maxdelta) + 1</c>, not the port's old <c>ceil(max / 8)</c>. The two
        /// agree at small distances but diverge in bands — at 17 px the old form took 3 substeps where
        /// AS3 takes 2, so the old code subdivided more than AS3 did in exactly the range where the
        /// limit was being exceeded. Verified numerically before changing it (17 → 2, not 3).
        /// </summary>
        public const float SubstepDistancePixels = 9f;

        /// <summary>
        /// AS3's substep count: <c>floor(maxDistance / maxdelta) + 1</c> (<c>Box.as:616</c>). Pure, so
        /// it can be asserted directly without a room. AS3 wraps it in a two-branch guard — when
        /// <c>|dx| &lt; maxdelta &amp;&amp; |dy| &lt; maxdelta</c> it takes a single un-subdivided step
        /// (<c>Box.as:610-613</c>), otherwise it uses this count (<c>Box.as:614-623</c>). The formula
        /// already returns 1 for exactly that first range, so the two branches collapse into one here
        /// and the port needs no guard of its own.
        /// </summary>
        public static int SubstepCount(float maxDistance)
        {
            return Mathf.FloorToInt(Mathf.Max(0f, maxDistance) / SubstepDistancePixels) + 1;
        }

        /// <summary>
        /// Ground friction for one step: below the stop threshold the box is halted outright,
        /// otherwise damped by <see cref="GroundFriction"/> per AS3 frame. Pure, so the rate and the
        /// threshold can be asserted without a room. AS3 <c>Box.as:1090-1096</c>.
        /// </summary>
        public static float ApplyGroundFriction(float velocityX, float deltaTime)
        {
            if (Mathf.Abs(velocityX) < GroundStopPixelsPerSecond)
            {
                return 0f;
            }

            return velocityX * Mathf.Pow(
                GroundFriction, deltaTime * PFE.Core.SimClock.CanonicalTicksPerSecond);
        }

        /// <summary>
        /// Fall speed for one step: gravity accelerates until the AS3 terminal velocity, then stops
        /// accelerating. Pure, and the clamp is inside the gate exactly as AS3 writes it
        /// (<c>Box.as:893</c> — <c>if(!isPlav &amp;&amp; dy &lt; World.maxdy)</c>).
        /// </summary>
        public static float IntegrateFallSpeed(float velocityY, float deltaTime)
        {
            if (velocityY <= -MaxFallSpeedPixelsPerSecond)
            {
                return velocityY;
            }

            return Mathf.Max(
                -MaxFallSpeedPixelsPerSecond, velocityY - GravityPixelsPerSecond * deltaTime);
        }

        readonly List<ObjectInstance> _dynamicObjects = new List<ObjectInstance>();
        readonly HashSet<ObjectInstance> _dynamicObjectLookup = new HashSet<ObjectInstance>();
        int _lastKnownRoomObjectCount = -1;

        public IReadOnlyList<ObjectInstance> DynamicObjects => _dynamicObjects;
        public int DynamicObjectCount => _dynamicObjects.Count;

        public void EnsureSynchronized(List<ObjectInstance> roomObjects)
        {
            SyncWithRoom(roomObjects);
        }

        public void Rebuild(IReadOnlyList<ObjectInstance> objects)
        {
            _dynamicObjects.Clear();
            _dynamicObjectLookup.Clear();

            if (objects == null)
            {
                _lastKnownRoomObjectCount = 0;
                return;
            }

            for (int i = 0; i < objects.Count; i++)
            {
                Register(objects[i]);
            }

            _lastKnownRoomObjectCount = objects.Count;
        }

        public bool Register(ObjectInstance obj)
        {
            if (obj == null)
            {
                return false;
            }

            obj.InitializeDynamicRuntimeState();
            if (!obj.ShouldTrackInPhysicsLayer())
            {
                return false;
            }

            if (_dynamicObjectLookup.Add(obj))
            {
                _dynamicObjects.Add(obj);
                return true;
            }

            return false;
        }

        public bool Unregister(ObjectInstance obj)
        {
            if (obj == null || !_dynamicObjectLookup.Remove(obj))
            {
                return false;
            }

            _dynamicObjects.Remove(obj);
            return true;
        }

        public bool TryApplyImpulse(ObjectInstance obj, Vector2 deltaVelocity, bool treatAsThrow = false)
        {
            if (obj == null)
            {
                return false;
            }

            Register(obj);
            if (!_dynamicObjectLookup.Contains(obj))
            {
                return false;
            }

            MapObjectDynamicStateData state = obj.runtimeState.dynamicState;
            state.isHeldByTelekinesis = false;
            state.hasTelekineticTarget = false;
            state.velocity += deltaVelocity;
            state.isGrounded = false;
            state.isThrown = treatAsThrow && obj.CanBeThrown();
            state.throwGraceTime = state.isThrown ? ThrowGraceDuration : 0f;
            return true;
        }

        public bool TryFindNearestTelekineticObject(Vector2 origin, float maxDistancePixels, out ObjectInstance obj)
        {
            obj = null;
            float clampedMaxDistance = Mathf.Max(0f, maxDistancePixels);
            float bestDistanceSquared = clampedMaxDistance * clampedMaxDistance;

            for (int i = 0; i < _dynamicObjects.Count; i++)
            {
                ObjectInstance candidate = _dynamicObjects[i];
                if (candidate == null ||
                    !candidate.SupportsTelekinesis() ||
                    candidate.IsDestroyed() ||
                    !candidate.isActive)
                {
                    continue;
                }

                Vector2 candidatePoint = candidate.GetApproximateBounds().center;
                float distanceSquared = (candidatePoint - origin).sqrMagnitude;
                if (distanceSquared > bestDistanceSquared)
                {
                    continue;
                }

                bestDistanceSquared = distanceSquared;
                obj = candidate;
            }

            return obj != null;
        }

        public bool TrySetTelekineticHold(ObjectInstance obj, Vector2 targetPosition)
        {
            if (obj == null || !obj.SupportsTelekinesis())
            {
                return false;
            }

            Register(obj);
            if (!_dynamicObjectLookup.Contains(obj))
            {
                return false;
            }

            MapObjectDynamicStateData state = obj.runtimeState.dynamicState;
            state.isHeldByTelekinesis = true;
            state.hasTelekineticTarget = true;
            state.telekineticTarget = targetPosition;
            state.isThrown = false;
            state.throwGraceTime = 0f;
            state.velocity = Vector2.zero;
            state.isGrounded = false;
            return true;
        }

        public bool TryReleaseTelekineticHold(ObjectInstance obj, Vector2 releaseVelocity, bool treatAsThrow = true)
        {
            if (obj == null || obj.runtimeState == null || obj.runtimeState.dynamicState == null)
            {
                return false;
            }

            Register(obj);
            if (!_dynamicObjectLookup.Contains(obj))
            {
                return false;
            }

            MapObjectDynamicStateData state = obj.runtimeState.dynamicState;
            state.isHeldByTelekinesis = false;
            state.hasTelekineticTarget = false;
            state.velocity = releaseVelocity;
            state.isThrown = treatAsThrow && obj.CanBeThrown();
            state.throwGraceTime = state.isThrown ? ThrowGraceDuration : 0f;
            state.isGrounded = false;
            return true;
        }

        public void Update(RoomInstance room, float deltaTime = LegacyPerFrameDeltaTime)
        {
            if (room == null)
            {
                return;
            }

            SyncWithRoom(room.objects);

            if (_dynamicObjects.Count == 0)
            {
                return;
            }

            float dt = Mathf.Max(0.0001f, deltaTime);
            for (int i = 0; i < _dynamicObjects.Count; i++)
            {
                ObjectInstance obj = _dynamicObjects[i];
                if (obj == null)
                {
                    continue;
                }

                StepDynamicObject(room, obj, dt);
            }
        }

        void SyncWithRoom(List<ObjectInstance> roomObjects)
        {
            if (roomObjects == null)
            {
                _dynamicObjects.Clear();
                _dynamicObjectLookup.Clear();
                _lastKnownRoomObjectCount = 0;
                return;
            }

            if (_lastKnownRoomObjectCount != roomObjects.Count)
            {
                Rebuild(roomObjects);
                return;
            }

            for (int i = _dynamicObjects.Count - 1; i >= 0; i--)
            {
                ObjectInstance tracked = _dynamicObjects[i];
                if (tracked == null ||
                    !roomObjects.Contains(tracked) ||
                    !tracked.ShouldTrackInPhysicsLayer())
                {
                    _dynamicObjectLookup.Remove(tracked);
                    _dynamicObjects.RemoveAt(i);
                }
            }
        }

        void StepDynamicObject(RoomInstance room, ObjectInstance obj, float deltaTime)
        {
            obj.InitializeDynamicRuntimeState();

            MapObjectDynamicStateData state = obj.runtimeState.dynamicState;
            if (!state.isDynamic || !obj.ShouldSimulateDynamicPhysics())
            {
                return;
            }

            if (state.throwGraceTime > 0f)
            {
                state.throwGraceTime = Mathf.Max(0f, state.throwGraceTime - deltaTime);
            }

            if (state.isHeldByTelekinesis && obj.SupportsTelekinesis())
            {
                StepHeldObject(room, obj, state, deltaTime);
                return;
            }

            Vector2 velocity = state.velocity;

            velocity.y = IntegrateFallSpeed(velocity.y, deltaTime);

            // There is no air damping in AS3 at all — the old AirDrag = 2 was an invented force and is
            // gone. Ground friction applies only while in contact (Box.as:1090-1096).
            if (state.isGrounded)
            {
                velocity.x = ApplyGroundFriction(velocity.x, deltaTime);
            }

            state.velocity = velocity;
            MoveWithCollision(room, obj, state, velocity * deltaTime);
        }

        void StepHeldObject(RoomInstance room, ObjectInstance obj, MapObjectDynamicStateData state, float deltaTime)
        {
            Vector2 targetPosition = state.hasTelekineticTarget ? state.telekineticTarget : obj.position;
            Vector2 toTarget = targetPosition - obj.position;
            Vector2 desiredVelocity = toTarget * TelekinesisFollowSpeed;
            state.velocity = desiredVelocity;
            state.isGrounded = false;
            state.isThrown = false;

            MoveWithCollision(room, obj, state, desiredVelocity * deltaTime);
        }

        void MoveWithCollision(RoomInstance room, ObjectInstance obj, MapObjectDynamicStateData state, Vector2 totalDelta)
        {
            float maxDistance = Mathf.Max(Mathf.Abs(totalDelta.x), Mathf.Abs(totalDelta.y));
            int steps = SubstepCount(maxDistance);
            Vector2 stepDelta = totalDelta / steps;

            for (int i = 0; i < steps; i++)
            {
                if (!Mathf.Approximately(stepDelta.x, 0f))
                {
                    TryMoveAxis(room, obj, state, new Vector2(stepDelta.x, 0f), false);
                }

                if (!Mathf.Approximately(stepDelta.y, 0f))
                {
                    TryMoveAxis(room, obj, state, new Vector2(0f, stepDelta.y), true);
                }
            }
        }

        void TryMoveAxis(RoomInstance room, ObjectInstance obj, MapObjectDynamicStateData state, Vector2 delta, bool verticalAxis)
        {
            if (verticalAxis)
            {
                state.isGrounded = false;
            }

            Vector2 candidatePosition = obj.position + delta;
            if (HasCollision(room, obj, candidatePosition))
            {
                float impactSpeed = verticalAxis ? Mathf.Abs(state.velocity.y) : Mathf.Abs(state.velocity.x);
                if (impactSpeed >= MinimumImpactSpeed && obj.IsDynamicPhysicalProp())
                {
                    state.lastImpactSpeed = impactSpeed;
                }

                if (verticalAxis)
                {
                    if (delta.y < 0f)
                    {
                        state.isGrounded = true;
                    }

                    state.velocity = new Vector2(state.velocity.x, 0f);
                }
                else
                {
                    state.velocity = new Vector2(0f, state.velocity.y);
                }

                state.isThrown = false;
                return;
            }

            obj.position = ClampToRoomBounds(room, obj, candidatePosition);
        }

        bool HasCollision(RoomInstance room, ObjectInstance obj, Vector2 candidatePosition)
        {
            Rect bounds = obj.GetApproximateBounds(candidatePosition);
            float roomWidthPixels = room.width * WorldConstants.TILE_SIZE;
            float roomHeightPixels = room.height * WorldConstants.TILE_SIZE;

            if (bounds.xMin < 0f || bounds.yMin < 0f || bounds.xMax > roomWidthPixels || bounds.yMax > roomHeightPixels)
            {
                return true;
            }

            return room.CheckCollision(bounds.position, bounds.size);
        }

        Vector2 ClampToRoomBounds(RoomInstance room, ObjectInstance obj, Vector2 candidatePosition)
        {
            Vector2 sizePixels = obj.GetApproximatePixelSize();
            float roomWidthPixels = room.width * WorldConstants.TILE_SIZE;
            float roomHeightPixels = room.height * WorldConstants.TILE_SIZE;

            return new Vector2(
                Mathf.Clamp(candidatePosition.x, sizePixels.x * 0.5f, roomWidthPixels - sizePixels.x * 0.5f),
                Mathf.Clamp(candidatePosition.y, 0f, roomHeightPixels - sizePixels.y));
        }
    }
}
