using System.Collections.Generic;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Sim.State;
using UnityEngine;

namespace PFE.Net
{
    /// <summary>
    /// The client-side display spawner for host-replicated transients — slice 3's consumer. The
    /// projector already emits every live round, thrown object and mine as a
    /// <see cref="SimProjectileState"/> set in <c>SimRoomDelta.Projectiles</c>; this class reconciles
    /// that set against the views it owns, keyed by <see cref="SimProjectileState.EntityHash"/>.
    ///
    /// <para><b>Full replace per accepted snapshot, like the looted-container set.</b> The projector
    /// emits the whole live set every tick, so presence in the snapshot IS liveness: a record not
    /// seen this snapshot is gone on the host (impacted, detonated, outlived), and its view must be
    /// destroyed — a round that lingers after the host pooled it is a ghost that no error names. The
    /// sweep is mark-and-sweep: mark on every applied record, destroy every unmarked view.</para>
    ///
    /// <para><b>The room gate is the body's gate.</b> Records arrive inside a room delta and the
    /// positions are pixels the host measured in ITS presentation room; a record from another room's
    /// delta would draw at plausible-looking but wrong coordinates in the room this client follows —
    /// the exact "third play-test" failure <see cref="PfeRemoteBodyRules.CanPlaceBodyInRoom"/> exists
    /// for on the unit side. Skipped records are counted, never guessed.</para>
    ///
    /// <para><b>A record with no resolvable weapon still gets a view.</b> The view tracks position,
    /// facing and lifetime with its renderer disabled (the honest empty state); dropping the record
    /// instead would desync the reconcile, because the host keeps sending it. The count is the
    /// readback for a content-set divergence — the intern table naming something this build lacks.</para>
    /// </summary>
    public sealed class PfeRemoteProjectileDisplay
    {
        private readonly Dictionary<ulong, PfeRemoteProjectileView> _live =
            new Dictionary<ulong, PfeRemoteProjectileView>(64);
        private readonly HashSet<ulong> _seen = new HashSet<ulong>(64);
        private readonly List<ulong> _sweep = new List<ulong>(64);

        /// <summary>How many views the reconciler created.</summary>
        public int Spawned { get; private set; }

        /// <summary>How many motion samples updated an existing view.</summary>
        public int Updated { get; private set; }

        /// <summary>How many samples were refused as not newer — out-of-order unreliable delivery.</summary>
        public int RejectedStale { get; private set; }

        /// <summary>How many views the sweep destroyed because the host dropped the record.</summary>
        public int Destroyed { get; private set; }

        /// <summary>How many records were skipped because their delta named another room.</summary>
        public int SkippedForeignRoom { get; private set; }

        /// <summary>How many records failed the receiver-side gate (no hash / unknown kind / NaN).</summary>
        public int SkippedUndisplayable { get; private set; }

        /// <summary>How many views were built with no resolvable weapon — art-less, tracking only.</summary>
        public int UnresolvedDefinitions { get; private set; }

        /// <summary>How many views are alive right now.</summary>
        public int LiveCount => _live.Count;

        /// <summary>Reconcile one accepted snapshot against the live view set.</summary>
        public void Apply(in SimSnapshot snapshot, ulong localRoomHash)
        {
            _seen.Clear();

            SimRoomDelta[] rooms = snapshot.Rooms;
            if (rooms != null)
            {
                for (int r = 0; r < rooms.Length; r++)
                {
                    SimProjectileState[] records = rooms[r].Projectiles;
                    if (records == null || records.Length == 0) continue;

                    if (!PfeRemoteBodyRules.CanPlaceBodyInRoom(rooms[r].RoomHash, localRoomHash))
                    {
                        SkippedForeignRoom += records.Length;
                        continue;
                    }

                    for (int p = 0; p < records.Length; p++)
                    {
                        ApplyOne(in records[p]);
                    }
                }
            }

            Sweep();
        }

        /// <summary>Destroy every view. A client whose host vanished keeps no floating rounds, and a
        /// session teardown leaves nothing behind.</summary>
        public void Clear()
        {
            foreach (KeyValuePair<ulong, PfeRemoteProjectileView> entry in _live)
            {
                if (entry.Value != null) entry.Value.DestroyView();
            }
            _live.Clear();
            _seen.Clear();
        }

        private void ApplyOne(in SimProjectileState state)
        {
            if (!PfeRemoteProjectileRules.IsDisplayableRecord(in state))
            {
                SkippedUndisplayable++;
                return;
            }

            _seen.Add(state.EntityHash);

            PfeRemoteProjectileView view;
            if (_live.TryGetValue(state.EntityHash, out view))
            {
                if (view.ApplyState(in state)) Updated++;
                else RejectedStale++;
                return;
            }

            WeaponDefinition weapon = ResolveDefinition(state.DefinitionId);
            if (weapon == null) UnresolvedDefinitions++;

            // Creation carries the first sample: a view that exists but never had a state applied
            // would draw at the origin for a frame, which is exactly the kind of wrong a screenshot
            // catches and a counter never would.
            view = PfeRemoteProjectileView.Create(state.EntityHash, state.Kind, weapon, in state);
            _live.Add(state.EntityHash, view);
            Spawned++;
        }

        private void Sweep()
        {
            if (_live.Count == 0) return;

            _sweep.Clear();
            foreach (KeyValuePair<ulong, PfeRemoteProjectileView> entry in _live)
            {
                if (!_seen.Contains(entry.Key)) _sweep.Add(entry.Key);
            }

            for (int i = 0; i < _sweep.Count; i++)
            {
                PfeRemoteProjectileView view;
                if (!_live.TryGetValue(_sweep[i], out view)) continue;
                _live.Remove(_sweep[i]);
                if (view != null) view.DestroyView();
                Destroyed++;
            }
        }

        /// <summary>The interned id back to a definition: content id through the ambient table, asset
        /// through the same Resources scan the remote weapon view uses — one source of truth for
        /// "which weapons exist" on this machine.</summary>
        private static WeaponDefinition ResolveDefinition(ushort definitionId)
        {
            string weaponId = ContentInternContext.Weapons.GetContentId(definitionId);
            if (string.IsNullOrEmpty(weaponId)) return null;
            return PfeRemoteWeaponCatalog.ResolveById(weaponId);
        }
    }
}
