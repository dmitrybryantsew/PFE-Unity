using System.Collections.Generic;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Every live <see cref="UnitController"/> in the session — the list the snapshot projector
    /// enumerates to project units, the brains sweep for targets, and S.A.T.S. scans for candidates.
    ///
    /// <para><b>Why this exists at all.</b> The identity registry is keyed on the <i>record</i>
    /// (<c>UnitInstance</c>), by row 3.2's decision, because <c>RefreshAll</c> destroys and rebuilds the
    /// view on every room entry. That decision is right, and it means the registry cannot answer "which
    /// controller is this record's view" — <c>RoomUnitSpawner._spawned</c> can, but it is private and
    /// there is one spawner per room. So the projection needs a flat list of live views, which is what
    /// this is. It is the mirror of <see cref="EliteEnemyRegistry"/>'s shape, for a different audience:
    /// that one holds promoted units for the nameplate, this one holds every unit for replication.</para>
    ///
    /// <para><b>An instance, not a static — thread E.</b> The class used to be a process-wide static that
    /// every controller reached from <c>OnEnable</c>; that made tests share one mutable list (a spawn in
    /// fixture A was visible to fixture B's brains) and needed a manual <c>Reset()</c> in teardown. The
    /// container owns ONE instance (<c>GameLifetimeScope</c>, singleton) and hands it down the spawn
    /// chain: <c>RoomUnitSpawner</c> attaches it to every unit it builds, the player attaches it from
    /// its injection, and consumers read it through their unit or their constructor. A fixture builds
    /// <c>new UnitViewRegistry()</c> and owns it outright — no reset, no leakage.</para>
    ///
    /// <para><b>Populated from the lifecycle, not from the spawner.</b> <c>UnitController.OnEnable</c>
    /// registers and <c>OnDisable</c> unregisters against the attached instance, so a pooled or
    /// re-enabled unit is tracked correctly without a second hand-over point — the same reason
    /// <c>OnEnable</c> already re-registers with <c>SimLoop</c>. A controller is in this list <i>before</i>
    /// it has an entity id (Unity runs <c>OnEnable</c> inside <c>AddComponent</c>, before the spawner
    /// stamps it), so every consumer must filter on <see cref="UnitController.HasEntityId"/> rather than
    /// assume. Because the instance arrives just AFTER <c>AddComponent</c> returns, <c>OnEnable</c> skips
    /// registration when no instance is attached yet and <c>UnitController.AttachViewRegistries</c>
    /// registers retroactively for an already-active unit.</para>
    ///
    /// <para><b>Unity-side only.</b> <see cref="Live"/> prunes Unity fake-nulls with the engine's
    /// <c>==</c> overload, which is an ECall outside the editor. No offline fixture may touch this type;
    /// the projector's arithmetic is pinned separately in <c>SimSnapshotProjectionRules</c> and its
    /// wiring by <c>SimReplicationWiringLintTests</c>.</para>
    /// </summary>
    public sealed class UnitViewRegistry
    {
        private readonly List<UnitController> _live = new List<UnitController>();

        /// <summary>How many live views are tracked. No pruning; for diagnostics and lints.</summary>
        public int Count => _live.Count;

        /// <summary>The live views. Prunes destroyed entries first; never null, may be empty.</summary>
        public IReadOnlyList<UnitController> Live
        {
            get
            {
                Prune();
                return _live;
            }
        }

        /// <summary>Track a view. Idempotent.</summary>
        public void Register(UnitController unit)
        {
            if (unit == null || _live.Contains(unit)) return;
            _live.Add(unit);
        }

        /// <summary>Stop tracking a view. Safe for one that was never registered.</summary>
        public void Unregister(UnitController unit)
        {
            if (unit == null) return;
            _live.Remove(unit);
        }

        /// <summary>Drop every entry — for a new session or a scene unload.</summary>
        public void Reset()
        {
            _live.Clear();
        }

        /// <summary>
        /// Drop entries Unity destroyed without reaching <see cref="Unregister"/>. A destroyed
        /// <c>MonoBehaviour</c> compares equal to null through the engine's overload, which is why this
        /// cannot be a plain reference check.
        /// </summary>
        private void Prune()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i] == null) _live.RemoveAt(i);
            }
        }
    }
}
