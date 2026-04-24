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
        const float DefaultDeltaTime = 1f / 60f;
        const float GravityPixelsPerSecond = 1800f;
        const float GroundDrag = 10f;
        const float AirDrag = 2f;
        const float TelekinesisFollowSpeed = 14f;
        const float MinimumImpactSpeed = 220f;
        const float ThrowGraceDuration = 0.18f;
        const float StepDistancePixels = 8f;

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

        public void Update(RoomInstance room, float deltaTime = DefaultDeltaTime)
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
            velocity.y += GravityPixelsPerSecond * deltaTime;

            float drag = state.isGrounded ? GroundDrag : AirDrag;
            float dragFactor = Mathf.Clamp01(1f - drag * deltaTime);
            velocity.x *= dragFactor;

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
            int steps = Mathf.Max(1, Mathf.CeilToInt(maxDistance / StepDistancePixels));
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
                    if (delta.y > 0f)
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
                Mathf.Clamp(candidatePosition.y, sizePixels.y, roomHeightPixels));
        }
    }
}
