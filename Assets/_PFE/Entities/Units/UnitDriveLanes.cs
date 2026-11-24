using PFE.Entities.Enemies;
using PFE.Systems.Physics;

namespace PFE.Entities.Units
{
    /// <summary>
    /// The three per-population batch drive systems — brains, unit motors, motor-less unit steps —
    /// as one object, pushed onto <see cref="PFE.Core.SimLoop"/> the same way <see cref="PFE.Core.SimLoop.WorldGate"/>
    /// and <see cref="PFE.Core.SimLoop.EntityIds"/> are pushed (thread J, Phase 2 of the
    /// UnitController → UnitWorld migration).
    ///
    /// <para><b>Why lanes exist at all.</b> Every <see cref="UnitController"/>, <see cref="EnemyBrain"/>
    /// and unit-owned <see cref="TilePhysicsController"/> used to register itself with
    /// <c>SimLoop</c> individually, so the loop's registry churned on every spawn, despawn and room
    /// stream — invalidating the dispatch order and rebuilding the
    /// <c>TickableCostTable</c> rows several times per firefight. The lanes invert that: one
    /// <see cref="PFE.Core.ISimTickable"/> per population per order bucket ticks the whole population
    /// in a single tight loop, and unit lifecycle stops touching the loop's registry entirely.</para>
    ///
    /// <para><b>The fallback contract (load-bearing).</b> The entities that register are built with
    /// <c>AddComponent</c> and reach the loop only through <c>AttachSimulation(clock, loop)</c>, so
    /// the lanes travel with the loop: an entity checks <c>loop.UnitDrives</c> and registers on the
    /// lane when it is present, else directly on the loop — which is exactly what every fixture and
    /// offline harness (a bare <c>new SimLoop</c>, no container) keeps doing. Production pushes the
    /// lanes at container build time; nothing constructed without a container changes behaviour.</para>
    ///
    /// <para><b>Ordering is the contract.</b> The three lanes occupy the same buckets the per-entity
    /// tickables did — brains at <see cref="PFE.Core.SimTickOrder.UnitMotor"/> − 1, unit motors at
    /// <see cref="PFE.Core.SimTickOrder.UnitMotor"/>, motor-less unit steps at
    /// <see cref="PFE.Core.SimTickOrder.UnitsAndAi"/> — so the batch refactor changes dispatch
    /// mechanics and nothing else: same order values, same per-entity step bodies, same intra-tick
    /// composition.</para>
    /// </summary>
    public sealed class UnitDriveLanes
    {
        public UnitDriveLanes(
            UnitBrainDriveSystem brains,
            UnitMotorDriveSystem motors,
            UnitStepDriveSystem steps)
        {
            Brains = brains;
            Motors = motors;
            Steps = steps;
        }

        /// <summary>Every live <see cref="EnemyBrain"/>, ticked at <c>UnitMotor − 1</c> (decide, then motors apply).</summary>
        public UnitBrainDriveSystem Brains { get; }

        /// <summary>Every unit-owned <see cref="TilePhysicsController"/>, ticked at <see cref="PFE.Core.SimTickOrder.UnitMotor"/>.</summary>
        public UnitMotorDriveSystem Motors { get; }

        /// <summary>Every sim-driven <see cref="UnitController"/> (motor-less), ticked at <see cref="PFE.Core.SimTickOrder.UnitsAndAi"/>.</summary>
        public UnitStepDriveSystem Steps { get; }
    }
}
