using System;
using System.Collections.Generic;
using PFE.Entities.Units;

namespace PFE.Systems.Replication
{
    /// <summary>
    /// The view→world stamping pass the snapshot projector drives before it reads
    /// <see cref="PFE.Sim.Units.UnitWorld"/> (thread J, Phase 3): once per tick it stamps every
    /// live unit's view-derived fields — position from the transform, avatar extras from the
    /// installed view source — and the unit's room attribution into the world's slot array, so
    /// the projector itself never touches a <c>MonoBehaviour</c> and the wire read is a dense
    /// array copy.
    ///
    /// <para><b>Why a separate system, and why the projector drives it.</b> The stamping must run
    /// after every system that can move a unit or change its room (motors, scripts, damage
    /// knockback) and immediately before the projection that reads it — the
    /// <see cref="PFE.Core.SimTickOrder.ViewAligned"/> bucket is the only place that holds, and
    /// the order inside the bucket is only guaranteed when the caller owns it. Making the sync a
    /// method the projector calls keeps the contract structural; a second
    /// <c>IAutoRegisteredSimTickable</c> at the same order would rely on registration-order
    /// luck.</para>
    ///
    /// <para><b>Why the registry walk happens here and not in the projector.</b> Position and the
    /// avatar extras are VIEW truth — they live on the transform and the rig's animation
    /// components and can only be read through the controller. That read is isolated in this one
    /// pass; the projector consumes the pure records. The enumeration is
    /// <see cref="UnitViewRegistry.Live"/> with the same <c>HasEntityId</c> filter the projector's
    /// old walk applied, so the projected set is exactly the live, id-carrying units. The world
    /// has no fake-null prune of its own: lifecycle correctness (drive unregister releases the
    /// slot) is the contract, exactly as it is for the drive lanes.</para>
    ///
    /// <para><b>A world the loop does not carry is the fixtures' case</b> — the pass still stamps
    /// each controller's fallback record (harmless, and it keeps
    /// <see cref="UnitController.CaptureSimState"/> identical on both storage paths); the
    /// projector then has no world to read and projects zero units, the same answer a missing
    /// registry gave before Phase 3.</para>
    /// </summary>
    public sealed class UnitRecordSyncSystem
    {
        private readonly UnitViewRegistry _unitViews;

        /// <summary>How many live, id-carrying views the last sync stamped.</summary>
        public int LastSyncedCount { get; private set; }

        /// <summary><paramref name="unitViews"/> is optional so bare fixtures can construct the
        /// pass without a registry; production registers the container singleton and VContainer
        /// fills it in.</summary>
        public UnitRecordSyncSystem(UnitViewRegistry unitViews = null)
        {
            _unitViews = unitViews;
        }

        /// <summary>Stamps every live, id-carrying unit's record and room attribution. Called by
        /// <see cref="SimProjectileSnapshotProjector"/> at the top of its tick.</summary>
        public void SyncAll()
        {
            int synced = 0;

            IReadOnlyList<UnitController> live = _unitViews != null
                ? _unitViews.Live
                : Array.Empty<UnitController>();

            for (int i = 0; i < live.Count; i++)
            {
                UnitController unit = live[i];

                // Same filter the projector's old registry walk applied: a controller is in the
                // list before the spawner stamps its id, and a hash-0 record is malformed on the
                // wire — the slot stays out of the projection until the id lands.
                if (unit == null || !unit.HasEntityId)
                {
                    continue;
                }

                unit.SyncUnitRecordForProjection();
                synced++;
            }

            LastSyncedCount = synced;
        }
    }
}
