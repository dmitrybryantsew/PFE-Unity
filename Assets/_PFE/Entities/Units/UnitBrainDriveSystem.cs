using System.Collections.Generic;
using PFE.Core;
using PFE.Entities.Enemies;

namespace PFE.Entities.Units
{
    /// <summary>
    /// One <see cref="IAutoRegisteredSimTickable"/> that ticks every live <see cref="EnemyBrain"/> in
    /// a single loop — the brain lane of <see cref="UnitDriveLanes"/>.
    ///
    /// <para><b>Replaces per-brain <c>SimLoop</c> registration.</b> Brains register here (via
    /// <c>loop.UnitDrives</c>, see <see cref="UnitDriveLanes"/>) instead of on the loop itself, so
    /// spawning or destroying an enemy stops invalidating the loop's dispatch order and cost rows.
    /// The lane sits at the same bucket the brains occupied —
    /// <see cref="SimTickOrder.UnitMotor"/> − 1 — because a brain's decision must be applied by the
    /// unit's motor in the same tick.</para>
    ///
    /// <para><b>Iteration discipline.</b> Forward index walk; a brain that unregisters mid-tick (its
    /// <c>OnDestroy</c> runs at a frame boundary in practice, the guard is defence) is skipped by the
    /// Unity fake-null check rather than throwing <c>MissingReferenceException</c>. The per-brain
    /// <see cref="EnemyBrain.SimTick"/> body is called unchanged, so the freeze gate, the dead-corpse
    /// animation branch and every state tick keep their own profiler regions and their exact
    /// behaviour.</para>
    /// </summary>
    public sealed class UnitBrainDriveSystem : IAutoRegisteredSimTickable
    {
        private readonly List<EnemyBrain> _brains = new List<EnemyBrain>();

        /// <summary>The brain bucket — one below <see cref="SimTickOrder.UnitMotor"/>, matching
        /// <see cref="EnemyBrain"/>'s own former order: decide, then the motors apply.</summary>
        public int TickOrder => SimTickOrder.UnitMotor - 1;

        /// <summary>How many brains are on the lane. The population readback for the overlay/tests.</summary>
        public int Count => _brains.Count;

        /// <summary>Adds a brain. Idempotent, mirroring <see cref="SimLoop.Register"/>.</summary>
        public void Register(EnemyBrain brain)
        {
            if (brain == null || _brains.Contains(brain))
            {
                return;
            }

            _brains.Add(brain);
        }

        /// <summary>Removes a brain; returns whether it was on the lane.</summary>
        public bool Unregister(EnemyBrain brain) => _brains.Remove(brain);

        /// <summary>Ticks every registered brain once, in registration (spawn) order.</summary>
        public void SimTick(int tickIndex)
        {
            using (PFE.Core.Profiling.PfeProfiler.Region("units.brainDrive",
                       "units: one batch tick of every live enemy brain. calls/calls(sim.tick) == 1; the per-brain bodies are the children."))
            {
                for (int i = 0; i < _brains.Count; i++)
                {
                    EnemyBrain brain = _brains[i];
                    if (brain == null)
                    {
                        continue;
                    }

                    brain.SimTick(tickIndex);
                }
            }
        }
    }
}
