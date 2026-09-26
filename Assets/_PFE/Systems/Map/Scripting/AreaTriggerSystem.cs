using System;
using System.Collections.Generic;
using System.Globalization;
using MessagePipe;
using PFE.Core.Messages;
using UnityEngine;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// Evaluates area trigger overlaps, dispatches tutorial messages, and executes map object scripts.
    /// Bridges ActionScript 3 Area.as and Script.as functionality into Unity.
    /// </summary>
    public sealed class AreaTriggerSystem
    {
        private readonly IPublisher<TutorialPromptMessage> _promptPublisher;
        private readonly IPublisher<ObjectiveMarkerMessage> _markerPublisher;

        private readonly Dictionary<string, string> _localizedTextOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public event Action<TutorialPromptMessage> OnPromptChanged;
        public event Action<ObjectiveMarkerMessage> OnMarkerChanged;
        public event Action<ObjectInstance> OnObjectStateChanged;

        public AreaTriggerSystem() : this(null, null)
        {
        }

        public AreaTriggerSystem(
            IPublisher<TutorialPromptMessage> promptPublisher,
            IPublisher<ObjectiveMarkerMessage> markerPublisher)
        {
            _promptPublisher = promptPublisher;
            _markerPublisher = markerPublisher;
        }

        public void SetTextOverride(string key, string text)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            _localizedTextOverrides[key] = text;
        }

        /// <summary>
        /// Get the 2D bounding rectangle of an area trigger in room-local pixel space.
        /// </summary>
        public static Rect GetTriggerPixelRect(ObjectInstance trigger)
        {
            if (trigger == null)
            {
                return Rect.zero;
            }

            float w = TryParseFloatAttribute(trigger, "w", 2f);
            float h = TryParseFloatAttribute(trigger, "h", 2f);
            if (w <= 0f) w = 2f;
            if (h <= 0f) h = 2f;

            float widthPixels = w * WorldConstants.TILE_SIZE;
            float heightPixels = h * WorldConstants.TILE_SIZE;

            return new Rect(trigger.position.x, trigger.position.y, widthPixels, heightPixels);
        }

        /// <summary>
        /// Checks if a player bounding rectangle overlaps the trigger area.
        /// </summary>
        public static bool EvaluateOverlap(ObjectInstance trigger, Rect playerBoundsPixels)
        {
            if (trigger == null || !trigger.isActive)
            {
                return false;
            }

            Rect triggerRect = GetTriggerPixelRect(trigger);
            return triggerRect.Overlaps(playerBoundsPixels);
        }

        /// <summary>
        /// Called when the player enters an area trigger.
        /// Dispatches prompt messages and executes entry scripts.
        /// </summary>
        public void OnPlayerEnter(RoomInstance room, ObjectInstance trigger)
        {
            if (trigger == null || !trigger.isActive)
            {
                return;
            }

            string messKey = trigger.GetAttribute("mess", string.Empty);
            if (!string.IsNullOrEmpty(messKey))
            {
                string text = ResolveTutorialText(messKey);
                bool isDown = string.Equals(trigger.GetAttribute("down", "0"), "1", StringComparison.Ordinal);
                var promptMsg = new TutorialPromptMessage(messKey, text, true, isDown);
                _promptPublisher?.Publish(promptMsg);
                OnPromptChanged?.Invoke(promptMsg);
            }

            if (trigger.scripts != null)
            {
                for (int i = 0; i < trigger.scripts.Count; i++)
                {
                    MapObjectScriptData script = trigger.scripts[i];
                    if (script == null) continue;

                    if (string.IsNullOrEmpty(script.eventName) || string.Equals(script.eventName, "enter", StringComparison.OrdinalIgnoreCase))
                    {
                        ExecuteScript(room, script);
                    }
                }
            }
        }

        /// <summary>
        /// Called when the player exits an area trigger.
        /// Clears active prompt message and executes exit scripts.
        /// </summary>
        public void OnPlayerExit(RoomInstance room, ObjectInstance trigger)
        {
            if (trigger == null)
            {
                return;
            }

            string messKey = trigger.GetAttribute("mess", string.Empty);
            if (!string.IsNullOrEmpty(messKey))
            {
                var promptMsg = new TutorialPromptMessage(messKey, string.Empty, false);
                _promptPublisher?.Publish(promptMsg);
                OnPromptChanged?.Invoke(promptMsg);
            }

            if (trigger.scripts != null)
            {
                for (int i = 0; i < trigger.scripts.Count; i++)
                {
                    MapObjectScriptData script = trigger.scripts[i];
                    if (script == null) continue;

                    if (string.Equals(script.eventName, "exit", StringComparison.OrdinalIgnoreCase))
                    {
                        ExecuteScript(room, script);
                    }
                }
            }
        }

        /// <summary>
        /// Called when an object is interacted with (e.g. looting a box or clicking a wallcab).
        /// </summary>
        public void OnObjectInteracted(RoomInstance room, ObjectInstance obj)
        {
            if (obj == null || obj.scripts == null) return;

            for (int i = 0; i < obj.scripts.Count; i++)
            {
                MapObjectScriptData script = obj.scripts[i];
                if (script == null) continue;

                if (string.IsNullOrEmpty(script.eventName) ||
                    string.Equals(script.eventName, "interact", StringComparison.OrdinalIgnoreCase))
                {
                    ExecuteScript(room, script);
                }
            }
        }

        /// <summary>
        /// Called when an object is destroyed or broken (e.g. wooden partition destroyed).
        /// </summary>
        public void OnObjectDestroyed(RoomInstance room, ObjectInstance obj)
        {
            if (obj == null || obj.scripts == null) return;

            for (int i = 0; i < obj.scripts.Count; i++)
            {
                MapObjectScriptData script = obj.scripts[i];
                if (script == null) continue;

                if (string.Equals(script.eventName, "die", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(script.eventName, "destroy", StringComparison.OrdinalIgnoreCase))
                {
                    ExecuteScript(room, script);
                }
            }
        }

        public void ExecuteScript(RoomInstance room, MapObjectScriptData script)
        {
            if (room == null || script == null || script.actions == null) return;

            for (int i = 0; i < script.actions.Count; i++)
            {
                ExecuteAction(room, script.actions[i]);
            }
        }

        public void ExecuteAction(RoomInstance room, MapObjectScriptActionData action)
        {
            if (room == null || action == null || string.IsNullOrEmpty(action.targ)) return;

            string targetUid = action.targ;
            string command = action.act?.ToLowerInvariant() ?? string.Empty;

            List<ObjectInstance> targets = FindObjectsByUidOrCode(room, targetUid);

            switch (command)
            {
                case "sign":
                    bool showMarker = action.val != "0" && !string.Equals(action.val, "false", StringComparison.OrdinalIgnoreCase);
                    Vector2 markerPosition = targets.Count > 0 ? targets[0].position : Vector2.zero;
                    var markerMsg = new ObjectiveMarkerMessage(targetUid, markerPosition, showMarker);
                    _markerPublisher?.Publish(markerMsg);
                    OnMarkerChanged?.Invoke(markerMsg);
                    break;

                case "off":
                    for (int i = 0; i < targets.Count; i++)
                    {
                        targets[i].isActive = false;
                        OnObjectStateChanged?.Invoke(targets[i]);
                    }
                    break;

                case "on":
                    for (int i = 0; i < targets.Count; i++)
                    {
                        targets[i].isActive = true;
                        OnObjectStateChanged?.Invoke(targets[i]);
                    }
                    break;

                case "onoff":
                    for (int i = 0; i < targets.Count; i++)
                    {
                        targets[i].isActive = !targets[i].isActive;
                        OnObjectStateChanged?.Invoke(targets[i]);
                    }
                    break;

                case "open":
                    for (int i = 0; i < targets.Count; i++)
                    {
                        targets[i].runtimeState.isOpen = true;
                        UpdateDoorTileCollision(room, targets[i], true);
                        OnObjectStateChanged?.Invoke(targets[i]);
                    }
                    break;

                case "close":
                    for (int i = 0; i < targets.Count; i++)
                    {
                        targets[i].runtimeState.isOpen = false;
                        UpdateDoorTileCollision(room, targets[i], false);
                        OnObjectStateChanged?.Invoke(targets[i]);
                    }
                    break;
            }
        }

        /// <summary>
        /// Stamps or clears solid wall physics on tiles occupied by a door object.
        /// Port of AS3 Box.as:setDoor().
        /// </summary>
        public static void UpdateDoorTileCollision(RoomInstance room, ObjectInstance obj, bool isOpen)
        {
            if (room?.tiles == null || obj == null) return;

            // Default door size in pixels: 40x80 (2x4 tiles)
            float widthPx = 40f;
            float heightPx = 80f;
            if (obj.definition != null)
            {
                if (obj.definition.width > 0) widthPx = obj.definition.width * WorldConstants.TILE_SIZE;
                if (obj.definition.size > 0) heightPx = obj.definition.size * WorldConstants.TILE_SIZE;
            }

            float leftPx = obj.position.x - widthPx * 0.5f;
            float rightPx = obj.position.x + widthPx * 0.5f - 1f;
            float bottomPx = obj.position.y;
            float topPx = obj.position.y + heightPx - 1f;

            if (!isOpen)
            {
                EjectPlayerIfOverlappingDoor(room, obj, leftPx, rightPx, bottomPx, topPx);
            }

            int txMin = Mathf.FloorToInt(leftPx / WorldConstants.TILE_SIZE);
            int txMax = Mathf.FloorToInt(rightPx / WorldConstants.TILE_SIZE);
            int tyMin = Mathf.FloorToInt(bottomPx / WorldConstants.TILE_SIZE);
            int tyMax = Mathf.FloorToInt(topPx / WorldConstants.TILE_SIZE);

            TilePhysicsType targetType = isOpen ? TilePhysicsType.Air : TilePhysicsType.Wall;

            for (int x = txMin; x <= txMax; x++)
            {
                for (int y = tyMin; y <= tyMax; y++)
                {
                    TileData tile = room.GetTileAtCoord(new Vector2Int(x, y));
                    if (tile != null)
                    {
                        tile.physicsType = targetType;
                    }
                }
            }
        }

        private static void EjectPlayerIfOverlappingDoor(
            RoomInstance room,
            ObjectInstance obj,
            float leftPx,
            float rightPx,
            float bottomPx,
            float topPx)
        {
            var player = GameObject.FindWithTag("Player");
            if (player == null)
            {
                var locomotion = UnityEngine.Object.FindFirstObjectByType<PFE.Entities.Player.PlayerLocomotionController>();
                if (locomotion != null) player = locomotion.gameObject;
            }
            if (player == null) return;

            Vector3 playerWorldPos = player.transform.position;
            float roomOriginPxX = room.landPosition.x * WorldConstants.ROOM_SIZE_PIXELS.x;
            float roomOriginPxY = room.landPosition.y * WorldConstants.ROOM_SIZE_PIXELS.y;

            float playerPxX = (playerWorldPos.x * 100f) - roomOriginPxX;
            float playerPxY = (playerWorldPos.y * 100f) - roomOriginPxY;

            float halfW = 15f; // 15px (0.15m)
            var col = player.GetComponent<Collider2D>();
            if (col != null && !col.isTrigger)
            {
                halfW = Mathf.Max(12f, col.bounds.extents.x * 100f);
            }

            bool overlapX = (playerPxX + halfW) > leftPx + 1f && (playerPxX - halfW) < rightPx - 1f;
            bool overlapY = playerPxY + 50f > bottomPx && playerPxY < topPx;

            if (overlapX && overlapY)
            {
                float doorCenterPx = (leftPx + rightPx) * 0.5f;
                const float marginPx = 8f;
                float targetPxX = playerPxX < doorCenterPx
                    ? (leftPx - halfW - marginPx)
                    : (rightPx + halfW + marginPx);

                float targetWorldX = (targetPxX + roomOriginPxX) * 0.01f;
                Vector3 newPos = new Vector3(targetWorldX, playerWorldPos.y, playerWorldPos.z);

                var motor = player.GetComponent<PFE.Systems.Physics.IMovementMotor>();
                if (motor != null)
                {
                    motor.SetUnityPosition(newPos);
                }
                else
                {
                    player.transform.position = newPos;
                }
            }
        }

        private static List<ObjectInstance> FindObjectsByUidOrCode(RoomInstance room, string uidOrCode)
        {
            var results = new List<ObjectInstance>();
            if (room?.objects == null || string.IsNullOrEmpty(uidOrCode)) return results;

            for (int i = 0; i < room.objects.Count; i++)
            {
                ObjectInstance obj = room.objects[i];
                if (obj == null) continue;

                if (string.Equals(obj.uid, uidOrCode, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(obj.code, uidOrCode, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(obj.objectId, uidOrCode, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(obj);
                }
            }

            return results;
        }

        public string ResolveTutorialText(string messKey)
        {
            if (string.IsNullOrWhiteSpace(messKey))
            {
                return string.Empty;
            }

            if (_localizedTextOverrides.TryGetValue(messKey, out string customText))
            {
                return customText;
            }

            // Standard fallback texts matching the original Flash baseline
            return messKey switch
            {
                "trDownJump" => "Press [S]+[Spacebar] to jump down",
                "trSit" => "Press [S] to sit / crouch",
                "trCont" => "Check the container",
                "trTele" => "Use telekinesis or jump to the upper ledge",
                "trPunch" => "Break the barricade with melee attack",
                _ => messKey
            };
        }

        private static float TryParseFloatAttribute(ObjectInstance obj, string key, float defaultValue)
        {
            if (obj == null) return defaultValue;
            string raw = obj.GetAttribute(key, string.Empty);
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : defaultValue;
        }
    }
}
