using System;
using MoonSharp.Interpreter;
using PFE.Core;
using PFE.Core.Scripting;
using UnityEngine;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// Host-authoritative bridge between AreaTriggerSystem and ILuaEngine.
    /// Allows map objects, doors, and triggers to execute sandboxed Lua routines on the host.
    /// </summary>
    [Authoritative]
    public sealed class LuaTriggerBridge
    {
        private readonly ILuaEngine _luaEngine;

        public LuaTriggerBridge(ILuaEngine luaEngine)
        {
            _luaEngine = luaEngine;
        }

        /// <summary>
        /// Executes a Lua script within the context of an active room and trigger object.
        /// </summary>
        public DynValue ExecuteTriggerScript(RoomInstance room, ObjectInstance trigger, string luaCode)
        {
            if (_luaEngine == null || string.IsNullOrWhiteSpace(luaCode) || room == null)
                return DynValue.Nil;

            var script = _luaEngine.CreateScript(LuaSandboxPolicy.Default);

            // Bind room helper API table
            var roomTable = new Table(script);
            roomTable["id"] = room.id;
            roomTable["open_door"] = (Action<string>)(doorUid =>
            {
                var obj = FindObject(room, doorUid);
                if (obj != null)
                {
                    obj.runtimeState.isOpen = true;
                    AreaTriggerSystem.UpdateDoorTileCollision(room, obj, true);
                }
            });

            roomTable["close_door"] = (Action<string>)(doorUid =>
            {
                var obj = FindObject(room, doorUid);
                if (obj != null)
                {
                    obj.runtimeState.isOpen = false;
                    AreaTriggerSystem.UpdateDoorTileCollision(room, obj, false);
                }
            });

            roomTable["set_active"] = (Action<string, bool>)((objUid, active) =>
            {
                var obj = FindObject(room, objUid);
                if (obj != null)
                {
                    obj.isActive = active;
                }
            });

            script.Globals["room"] = roomTable;

            if (trigger != null)
            {
                var triggerTable = new Table(script);
                triggerTable["uid"] = trigger.uid ?? string.Empty;
                triggerTable["pos_x"] = trigger.position.x;
                triggerTable["pos_y"] = trigger.position.y;
                script.Globals["trigger"] = triggerTable;
            }

            try
            {
                return script.DoString(luaCode);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LuaTriggerBridge] Error executing trigger Lua script: {ex.Message}");
                return DynValue.Nil;
            }
        }

        private static ObjectInstance FindObject(RoomInstance room, string uid)
        {
            if (room?.objects == null || string.IsNullOrEmpty(uid)) return null;
            for (int i = 0; i < room.objects.Count; i++)
            {
                if (string.Equals(room.objects[i].uid, uid, StringComparison.OrdinalIgnoreCase))
                    return room.objects[i];
            }
            return null;
        }
    }
}
