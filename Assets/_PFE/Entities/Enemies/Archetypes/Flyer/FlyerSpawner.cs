using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Spawn a unit at runtime from inside the simulation — AS3 <c>Location.createUnit</c>, the call a
    /// bloat nest makes every 100 ticks (<c>UnitBloatEmitter.as:73</c>) and a dying bloat makes on its
    /// split (<c>UnitBloat.as:171-179</c>).
    ///
    /// <para><b>Why this is a class and not four lines in the brain.</b> A unit record needs a unique
    /// <c>entityId</c>, a definition-derived health, a controller id left empty so the spawner falls
    /// through to the definition's own, and a <see cref="RoomVisualController"/> to build it. The alarm's
    /// reinforcement path (<c>SecurityRuntime.SpawnReinforcement</c>) does the same four things; the two
    /// cannot share the alarm's version because that one also spends a spawn budget and picks an
    /// <c>enspawn</c> marker, neither of which a nest has.</para>
    ///
    /// <para><b>Position is world space, and that is deliberate.</b> The oracle spawns a child at its
    /// parent's own <c>X</c>/<c>Y</c>; the caller's <c>transform.position</c> is that point in the port.
    /// <c>SpawnUnitNow</c> converts it to room pixels itself, so passing a pixel position here would be
    /// converted a second time.</para>
    /// </summary>
    public static class FlyerSpawner
    {
        static bool _reportedNoController;

        /// <summary>
        /// Build <paramref name="unitId"/> at <paramref name="worldPosition"/> and hand back its
        /// controller, or <c>null</c> when the room, the definition or the spawner is missing.
        /// </summary>
        /// <remarks>
        /// <b>Null is a legal answer, not an exception.</b> A room with no live visual controller (a
        /// headless fixture, a room mid-teardown) cannot spawn anything; the oracle would have had a
        /// <c>loc</c> because it is the location. The nest reports it once rather than per emit, the same
        /// way <c>ObjectShineEmitter</c> does for a missing particle emitter.
        /// </remarks>
        public static UnitController Spawn(RoomInstance room, string unitId, Vector3 worldPosition)
        {
            if (room == null || string.IsNullOrEmpty(unitId))
            {
                return null;
            }

            if (!ResourcesUnitDefinitionProvider.Shared.TryGetUnit(unitId, out UnitDefinition definition)
                || definition == null)
            {
                Debug.LogWarning(
                    $"[Flyer] '{unitId}' has no UnitDefinition, so the spawn was dropped. AS3's " +
                    "createUnit always has one — the id came from AllData.");
                return null;
            }

            RoomVisualController controller = RoomVisualController.FindForRoom(room);
            if (controller == null)
            {
                if (!_reportedNoController)
                {
                    _reportedNoController = true;
                    Debug.LogWarning(
                        $"[Flyer] No live RoomVisualController owns room '{room.id}', so '{unitId}' could " +
                        "not be spawned. AS3 cannot reach this state — `loc` is the location.");
                }

                return null;
            }

            var instance = new UnitInstance
            {
                // Phase 3.2 (F-05): host-assigned, tick-stamped id from the one run allocator.
                // The old static `_serial++` survived room rebuilds and replays, so the same
                // replayed tick sequence produced a different id on the second run.
                entityId = PFE.Core.RuntimeEntityIdContext.AllocateRuntimeId().ToString(),
                unitId = unitId,
                unitType = unitId,
                isDead = false,
                maxHealth = definition.health,
                currentHealth = definition.health,
                // Empty, so RoomUnitSpawner falls through to the definition's controllerId — the same
                // precedence AS3's createUnit gets when it passes no `cl`.
                controllerId = string.Empty,
                attributes = new List<MapObjectAttributeData>(),
                facingDirection = 1,
                // 0 = never buries: a nest's child must be on screen the moment it is made.
                digger = 0,
            };

            if (!controller.SpawnUnitNow(instance, worldPosition))
            {
                return null;
            }

            return controller.TryGetSpawnedUnit(instance, out GameObject spawned) && spawned != null
                ? spawned.GetComponent<UnitController>()
                : null;
        }
    }
}
