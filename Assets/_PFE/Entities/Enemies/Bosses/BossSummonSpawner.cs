using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Entities.Enemies.Bosses
{
    /// <summary>
    /// Build a named unit at one of a room's authored <c>enspawn</c> markers — the runtime half of
    /// <c>Location.waveSpawn</c> (<c>Location.as:3000-3030</c>), which is how Mchavi fills her arena.
    ///
    /// <para><b>Why this exists at all.</b> <c>UnitBossNecr.spawn()</c> (<c>:629-683</c>) is the single
    /// biggest thing the fight was missing: she teleported, cursed and blasted, but the arena stayed
    /// empty, because the port had no way to create a unit from inside the simulation. The two existing
    /// runtime spawners did not fit — <c>FlyerSpawner</c> builds at the <i>parent's own</i> position and
    /// carries no placement attributes, and <c>PetSpawner</c> forces <c>controllerId = "pet"</c>. What a
    /// summon needs and neither has is <b>a chosen marker</b>, <b>a health multiplier</b>, and
    /// <b>the caster's faction</b>.</para>
    ///
    /// <para><b>Three things the oracle does here that are easy to miss.</b> <c>waveSpawn</c> picks
    /// <c>enspawn[param2]</c> — a <i>different</i> marker per summon, falling back to a random one —
    /// sets <c>fraction = 1</c> and then <c>spawn()</c> immediately overwrites it with the boss's own, and
    /// finally calls <c>alarma()</c> on the newcomer. Without the alarm the three bodies stand at their
    /// markers doing nothing, which reads exactly like "the summons do not work".</para>
    ///
    /// <para><b>Position is a marker, not a pixel.</b> <see cref="SpawnPoint.GetEnemyUnityPosition"/>
    /// applies <c>addEnSpawn</c>'s own anchor (<c>:914-930</c>, <c>size</c> floored at 1) — <i>not</i> the
    /// player's <c>size='2'</c>. Using the player anchor here would put every summon half a tile right
    /// of where the oracle puts it.</para>
    /// </summary>
    public static class BossSummonSpawner
    {
        static bool _reportedNoController;
        static bool _reportedNoPoints;
        static bool _reportedNoDefinition;

        /// <summary>
        /// The room's enemy markers <b>in authored order</b> — AS3's <c>Location.enspawn</c>.
        ///
        /// <para><b>The order is the contract.</b> <c>waveSpawn</c> indexes this list directly
        /// (<c>enspawn[0]</c>, <c>enspawn[1]</c>, <c>enspawn[2]</c>), so the i-th summon lands on the
        /// i-th authored marker. <c>RoomPopulator.RecordEnemySpawnPoints</c> appends in the template's
        /// object order, which is the XML's document order — the same order AS3's object loop pushes
        /// into <c>enspawn</c>.</para>
        /// </summary>
        public static List<SpawnPoint> WavePoints(RoomInstance room)
        {
            var points = new List<SpawnPoint>();
            if (room?.spawnPoints == null) return points;

            for (int i = 0; i < room.spawnPoints.Count; i++)
            {
                SpawnPoint point = room.spawnPoints[i];
                if (point != null && point.type == SpawnType.Enemy)
                {
                    points.Add(point);
                }
            }

            return points;
        }

        /// <summary>
        /// Build <paramref name="unitId"/> at the <paramref name="waveIndex"/>-th enemy marker and hand
        /// back its controller, or <c>null</c> when the room, the marker, the definition or the visual
        /// controller is missing.
        /// </summary>
        /// <param name="hpMult">
        /// AS3 <c>hpmult</c> from the summon's own XML node, applied to the unit row's
        /// <c>&lt;comb hp&gt;</c> (<c>Unit.as:1128</c>). <c>0</c> or less means "the row's own health",
        /// which is the value every other runtime spawn uses.
        /// </param>
        /// <param name="faction">
        /// The caster's faction — <c>_loc4_.fraction = fraction</c> (<c>UnitBossNecr.as:665</c>).
        /// <c>null</c> leaves the unit on its definition's, which is what
        /// <c>waveSpawn</c>'s own <c>fraction = 1</c> would have been overwritten by anyway.
        /// </param>
        /// <param name="attackScale">
        /// <c>summonAtkMult</c>. <c>1</c> is a no-op; see <see cref="BossNecrRules.SummonAttackScaleFor"/>
        /// for the three real values.
        /// </param>
        /// <remarks>
        /// <b>Null is a legal answer, not an exception.</b> A headless fixture has no live visual
        /// controller and a room may author no <c>enspawn</c> rows at all; the oracle returns
        /// <c>null</c> from <c>waveSpawn</c> in the second case too (<c>:3006-3009</c>). Each cause is
        /// reported once rather than per summon, because a summon beat fires every few seconds.
        /// </remarks>
        public static UnitController Summon(
            RoomInstance room,
            string unitId,
            float hpMult,
            int waveIndex,
            FactionType? faction,
            float attackScale)
        {
            if (room == null || string.IsNullOrEmpty(unitId)) return null;

            List<SpawnPoint> points = WavePoints(room);
            if (points.Count == 0)
            {
                if (!_reportedNoPoints)
                {
                    _reportedNoPoints = true;
                    Debug.LogWarning(
                        $"[BossNecr] Room '{room.id}' authors no `enspawn` markers, so '{unitId}' was " +
                        "not summoned. AS3's waveSpawn returns null there too (Location.as:3006-3009) " +
                        "— a boss that summons needs markers to summon at.");
                }

                return null;
            }

            int index = BossNecrRules.WavePointIndex(waveIndex, points.Count, Random.value);
            if (index < 0) return null;
            SpawnPoint point = points[index];

            if (!ResourcesUnitDefinitionProvider.Shared.TryGetUnit(unitId, out UnitDefinition definition)
                || definition == null)
            {
                if (!_reportedNoDefinition)
                {
                    _reportedNoDefinition = true;
                    Debug.LogWarning(
                        $"[BossNecr] '{unitId}' has no UnitDefinition, so the summon was dropped. AS3's " +
                        "createUnit always has one — the id comes from AllData.");
                }

                return null;
            }

            RoomVisualController controller = RoomVisualController.FindForRoom(room);
            if (controller == null)
            {
                if (!_reportedNoController)
                {
                    _reportedNoController = true;
                    Debug.LogWarning(
                        $"[BossNecr] No live RoomVisualController owns room '{room.id}', so '{unitId}' " +
                        "could not be summoned. AS3 cannot reach this state — `loc` is the location.");
                }

                return null;
            }

            // `this.hp = this.maxhp = node.@hp * this.hpmult` (Unit.as:1128). A missing or nonsense
            // multiplier means the row's own health; a zero would make the summon dead on arrival.
            float health = hpMult > 0f ? definition.health * hpMult : definition.health;

            var instance = new UnitInstance
            {
                // Phase 3.2 (F-05): host-assigned runtime id; see FlyerSpawner for why the
                // per-spawner static serial had to go.
                entityId = PFE.Core.RuntimeEntityIdContext.AllocateRuntimeId().ToString(),
                unitId = unitId,
                unitType = unitId,
                isDead = false,
                maxHealth = health,
                currentHealth = health,
                // Empty, so RoomUnitSpawner falls through to the definition's controllerId — the same
                // precedence AS3's createUnit gets when it passes no `cl`.
                controllerId = string.Empty,
                attributes = new List<MapObjectAttributeData>(),
                fraction = faction,
                // The marker's own facing, as the alarm's reinforcement path reads it. `enspawn` rows
                // author no `turn`, so this is 1 today; reading it keeps the two runtime spawns from
                // diverging if the importer ever starts carrying one.
                facingDirection = point.facingDirection != 0f
                    ? Mathf.RoundToInt(point.facingDirection)
                    : 1,
                // 0 = never buries. AS3 passes `emergence(30)` to createUnit (Location.as:3015), which
                // is a 30-frame rise-from-the-ground animation; the port has no `emergence` seam, and a
                // summon that spawns *buried* would be a summon the player cannot find. Being visible
                // immediately is the closer approximation of the two.
                digger = 0,
            };

            if (!controller.SpawnUnitNow(instance, point.GetEnemyUnityPosition(room)))
            {
                Debug.LogWarning(
                    $"[BossNecr] Summon '{unitId}' could not be spawned at marker {index} of " +
                    $"'{room.id}' (SpawnUnitNow refused — no spawner or no physical parent).");
                return null;
            }

            if (!controller.TryGetSpawnedUnit(instance, out GameObject spawned) || spawned == null)
            {
                return null;
            }

            var unit = spawned.GetComponent<UnitController>();
            if (unit == null) return null;

            if (unit is EnemyController enemy)
            {
                // `_loc4_.dam *= this.summonAtkMult` and the weapon's damage/damageExpl with it
                // (:669-674). Applied after the spawn because the weapon mount does not exist until
                // Initialize has run.
                enemy.ScaleSummonDamage(attackScale);

                // `_loc5_.alarma()` (Location.as:3026) — the newcomer is told to look for the player at
                // its own position. Without it the three bodies stand at their markers.
                if (enemy.Brain != null)
                {
                    enemy.Brain.Alarma();
                }
            }

            return unit;
        }
    }
}
