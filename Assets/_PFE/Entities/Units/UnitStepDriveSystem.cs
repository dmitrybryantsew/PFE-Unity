using System.Collections.Generic;
using PFE.Core;

namespace PFE.Entities.Units
{
    /// <summary>
    /// One <see cref="IAutoRegisteredSimTickable"/> that steps every sim-driven, motor-less
    /// <see cref="UnitController"/> in a single loop — the unit-step lane of <see cref="UnitDriveLanes"/>.
    ///
    /// <para><b>Which units land here.</b> Every controller that ran <c>AttachSimulation</c> registers
    /// with the lane — the same set that registered with <c>SimLoop</c> before, motored ones included.
    /// A motored unit's <see cref="UnitController.SimTick"/> returns immediately because its motor owns
    /// its step (see that method's remarks), so keeping it on the lane preserves the old registry's
    /// exact membership at zero behavioural risk; skipping it here would be a pure optimisation for a
    /// later, separately-tested change.</para>
    ///
    /// <para><b>Order is the promise.</b> The lane sits at <see cref="SimTickOrder.UnitsAndAi"/>, the
    /// bucket the controllers' own registrations occupied, so the batch loop runs after the room's
    /// prop heartbeat (<see cref="SimTickOrder.RoomState"/>) and the unit motors, and before
    /// projectiles — the per-unit step composition inside <see cref="UnitController.SimTick"/> is
    /// unchanged.</para>
    /// </summary>
    public sealed class UnitStepDriveSystem : IAutoRegisteredSimTickable
    {
        private readonly List<UnitController> _units = new List<UnitController>();

        /// <summary>The unit-step bucket — <see cref="SimTickOrder.UnitsAndAi"/>, exactly as the
        /// controllers' own registrations were.</summary>
        public int TickOrder => SimTickOrder.UnitsAndAi;

        /// <summary>How many controllers are on the lane.</summary>
        public int Count => _units.Count;

        /// <summary>Adds a controller. Idempotent, mirroring <see cref="SimLoop.Register"/>.</summary>
        public void Register(UnitController unit)
        {
            if (unit == null || _units.Contains(unit))
            {
                return;
            }

            _units.Add(unit);
        }

        /// <summary>Removes a controller; returns whether it was on the lane.</summary>
        public bool Unregister(UnitController unit) => _units.Remove(unit);

        /// <summary>Steps every registered controller once, in registration (spawn) order.</summary>
        public void SimTick(int tickIndex)
        {
            using (PFE.Core.Profiling.PfeProfiler.Region("units.stepDrive",
                       "units: one batch tick of every sim-driven unit controller. calls/calls(sim.tick) == 1; unit.simTick rows are the children."))
            {
                for (int i = 0; i < _units.Count; i++)
                {
                    UnitController unit = _units[i];
                    if (unit == null)
                    {
                        continue;
                    }

                    unit.SimTick(tickIndex);
                }
            }
        }
    }
}
