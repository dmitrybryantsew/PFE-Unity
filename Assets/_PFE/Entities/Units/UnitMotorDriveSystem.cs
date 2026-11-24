using System.Collections.Generic;
using PFE.Core;
using PFE.Systems.Physics;

namespace PFE.Entities.Units
{
    /// <summary>
    /// One <see cref="IAutoRegisteredSimTickable"/> that ticks every unit-owned
    /// <see cref="TilePhysicsController"/> in a single loop — the NPC-motor lane of
    /// <see cref="UnitDriveLanes"/>.
    ///
    /// <para><b>Unit-owned motors only.</b> A motor driving the <i>player</i> reports
    /// <see cref="SimTickOrder.PlayerMotor"/> and keeps its direct <c>SimLoop</c> registration — there
    /// is exactly one player, so batching it buys nothing and moving its order into a fixed lane would
    /// be a behaviour change. Only motors marked <see cref="TilePhysicsController.MarkUnitOwned"/> by
    /// the spawner land here, at <see cref="SimTickOrder.UnitMotor"/> as before.</para>
    ///
    /// <para><b>The step body is untouched.</b> The lane calls the motor's public
    /// <c>SimTick</c>, so <c>StepMotor</c> — and with it the contact-invulnerability, effects, corpse,
    /// noise/suspicion/stealth hooks it drives — runs exactly as it did when the loop called it
    /// directly.</para>
    /// </summary>
    public sealed class UnitMotorDriveSystem : IAutoRegisteredSimTickable
    {
        private readonly List<TilePhysicsController> _motors = new List<TilePhysicsController>();

        /// <summary>The NPC-motor bucket — <see cref="SimTickOrder.UnitMotor"/>, after the player motor
        /// and before projectiles, exactly as the per-motor registrations were.</summary>
        public int TickOrder => SimTickOrder.UnitMotor;

        /// <summary>How many unit motors are on the lane.</summary>
        public int Count => _motors.Count;

        /// <summary>Adds a motor. Idempotent, mirroring <see cref="SimLoop.Register"/>.</summary>
        public void Register(TilePhysicsController motor)
        {
            if (motor == null || _motors.Contains(motor))
            {
                return;
            }

            _motors.Add(motor);
        }

        /// <summary>Removes a motor; returns whether it was on the lane.</summary>
        public bool Unregister(TilePhysicsController motor) => _motors.Remove(motor);

        /// <summary>Ticks every registered motor once, in registration (spawn) order.</summary>
        public void SimTick(int tickIndex)
        {
            using (PFE.Core.Profiling.PfeProfiler.Region("units.motorDrive",
                       "units: one batch tick of every unit-owned motor. calls/calls(sim.tick) == 1; motor.tick rows are the children."))
            {
                for (int i = 0; i < _motors.Count; i++)
                {
                    TilePhysicsController motor = _motors[i];
                    if (motor == null)
                    {
                        continue;
                    }

                    motor.SimTick(tickIndex);
                }
            }
        }
    }
}
