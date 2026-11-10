using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Pets;
using UnityEngine;

namespace PFE.Entities.Pets
{
    /// <summary>
    /// Build a pet into a live room — the runtime half of <c>Location.createUnit</c> for the three
    /// companion rows.
    ///
    /// <para><b>Why the pet does not go through the room's own spawn list.</b> A pet is not a placement:
    /// it has no <c>&lt;obj&gt;</c> node, no authored position and no difficulty roll. It is created by
    /// the player's own <c>callPet</c>, at the player, and destroyed when the player leaves the room.
    /// The spawner path (<see cref="RoomVisualController.SpawnUnitNow"/>) is used because it is the one
    /// place that builds a collider, a sprite, a motor, a tile query, the weapon services and the
    /// damage sink in the order <c>UnitController</c> needs them — re-deriving that here would be a
    /// second, thinner copy that would silently skip whichever seam was added last.</para>
    ///
    /// <para><b><c>controllerId</c> is <c>"pet"</c>, not the unit id.</b> <c>phoenix</c> already maps to
    /// the wild bird's <c>PhoenixController</c> in the spawner's controller table
    /// (<c>AllData.as:1321</c> is the same row the caged prop uses), so asking for the row id would
    /// build the enemy — a parked, immobile bird that cannot be summoned. The pet is a different class
    /// reading the same row, exactly as <c>UnitPet</c> does.</para>
    /// </summary>
    public static class PetSpawner
    {
        static bool _reportedNoController;

        /// <summary>
        /// Build <paramref name="kind"/> at <paramref name="worldPosition"/> and hand back its
        /// controller, or <c>null</c> when the room, the definition or the visual controller is missing.
        /// </summary>
        public static PetController Spawn(RoomInstance room, PetKind kind, Vector3 worldPosition)
        {
            if (room == null) return null;

            string unitId = PetIds.ToId(kind);

            if (!ResourcesUnitDefinitionProvider.Shared.TryGetUnit(unitId, out UnitDefinition definition)
                || definition == null)
            {
                Debug.LogWarning(
                    $"[PetSpawner] '{unitId}' has no UnitDefinition, so the pet could not be built. " +
                    "AS3's createUnit always has one — the id comes from AllData.");
                return null;
            }

            RoomVisualController controller = RoomVisualController.FindForRoom(room);
            if (controller == null)
            {
                if (!_reportedNoController)
                {
                    _reportedNoController = true;
                    Debug.LogWarning(
                        $"[PetSpawner] No live RoomVisualController owns room '{room.id}', so '{unitId}' " +
                        "could not be summoned. AS3 cannot reach this state — `loc` is the location.");
                }

                return null;
            }

            var instance = new UnitInstance
            {
                // Phase 3.2 (F-05): host-assigned runtime id; see FlyerSpawner for why the
                // per-spawner static serial had to go.
                entityId = PFE.Core.RuntimeEntityIdContext.AllocateRuntimeId().ToString(),
                unitId = unitId,
                unitType = unitId,
                isDead = false,
                maxHealth = definition.health,
                currentHealth = definition.health,

                // The one field that decides the class — see the class remarks.
                controllerId = PetController.ControllerId,

                attributes = new List<MapObjectAttributeData>(),
                facingDirection = 1,
                digger = 0,
            };

            if (!controller.SpawnUnitNow(instance, worldPosition))
            {
                return null;
            }

            return controller.TryGetSpawnedUnit(instance, out GameObject spawned) && spawned != null
                ? spawned.GetComponent<PetController>()
                : null;
        }
    }
}
