using System;
using System.Collections.Generic;

namespace PFE.Sim.State
{
    /// <summary>
    /// The reconciliation rule for one room's floor-loot set on the wire — <see cref="SimRoomDelta.DroppedItems"/>
    /// is a <b>full replace</b>, exactly like <see cref="SimRoomDelta.LootedObjects"/>: a drop the host
    /// still reports exists, a drop the host no longer reports is gone (taken or despawned on the host).
    ///
    /// <para><b>Why full replace and not a despawn list.</b> The projector emits
    /// <c>RemovedEntityHashes</c> empty; presence in <c>DroppedItems</c> IS the spawn signal and absence
    /// IS the despawn signal. That is the same contract the opened-container set uses, and it repairs a
    /// lost snapshot for free: the next snapshot carries the whole set again. A receiver that merged
    /// instead of replacing would keep re-adding a drop the local player already watched vanish.</para>
    ///
    /// <para><b>Why the wire set is validated before it is applied.</b> A drop's identity is its
    /// <see cref="SimDroppedItem.EntityHash"/>; a set carrying hash 0 or two entries with the same hash
    /// cannot be applied without inventing an identity, so the whole room set is refused and counted
    /// rather than half-applied — the same refusal shape the looted-object applier uses.</para>
    /// </summary>
    public static class SimDroppedItemSetRules
    {
        /// <summary>The reconcile decision for one room: which wire entries to spawn, which local
        /// records to remove, and which entries exist on both sides (the caller diffs their fields).</summary>
        public readonly struct Plan
        {
            /// <summary>Indices into the wire array: drops the local room does not have yet.</summary>
            public readonly int[] AddedWireIndices;

            /// <summary>Indices into the local record list: records the wire set no longer reports.</summary>
            public readonly int[] RemovedLocalIndices;

            /// <summary>Indices into the wire array whose hash also exists locally — the caller updates
            /// the existing record/view from these.</summary>
            public readonly int[] KeptWireIndices;

            public Plan(int[] addedWireIndices, int[] removedLocalIndices, int[] keptWireIndices)
            {
                AddedWireIndices = addedWireIndices;
                RemovedLocalIndices = removedLocalIndices;
                KeptWireIndices = keptWireIndices;
            }
        }

        /// <summary>
        /// A wire set is applicable only when every entry has a non-zero identity and no identity
        /// repeats. Both defects make the set unaddressable: hash 0 is the receiver's "no identity"
        /// sentinel (<see cref="PFE.Sim.Ids.DroppedItemIdentityRules.Unassigned"/>) and a duplicate makes
        /// "which drop changed" unanswerable.
        /// </summary>
        public static bool TryValidateWireSet(SimDroppedItem[] wire, out string reason)
        {
            if (wire == null)
            {
                reason = "no drop set";
                return false;
            }

            for (int i = 0; i < wire.Length; i++)
            {
                if (wire[i].EntityHash == 0UL)
                {
                    reason = "drop set entry " + i + " carries no entity hash";
                    return false;
                }

                for (int j = i + 1; j < wire.Length; j++)
                {
                    if (wire[i].EntityHash == wire[j].EntityHash)
                    {
                        reason = "drop set carries duplicate entity hash 0x" +
                                 wire[i].EntityHash.ToString("X16");
                        return false;
                    }
                }
            }

            reason = "none";
            return true;
        }

        /// <summary>
        /// Compute the full-replace plan for one room. <paramref name="localHashes"/> are the entity
        /// hashes of the room's current drop records, in list order. Pure: the caller owns every Unity
        /// touch (record creation, view spawn, despawn), so the decision itself runs in the offline wall.
        /// </summary>
        public static Plan ComputePlan(SimDroppedItem[] wire, IReadOnlyList<ulong> localHashes)
        {
            var added = new List<int>(wire?.Length ?? 0);
            var kept = new List<int>(wire?.Length ?? 0);
            var removed = new List<int>(localHashes?.Count ?? 0);

            int localCount = localHashes?.Count ?? 0;
            var matchedLocal = localCount > 0 ? new bool[localCount] : Array.Empty<bool>();

            if (wire != null)
            {
                for (int w = 0; w < wire.Length; w++)
                {
                    int localIndex = IndexOf(localHashes, wire[w].EntityHash);
                    if (localIndex < 0)
                    {
                        added.Add(w);
                    }
                    else
                    {
                        kept.Add(w);
                        matchedLocal[localIndex] = true;
                    }
                }
            }

            for (int l = 0; l < localCount; l++)
            {
                if (!matchedLocal[l]) removed.Add(l);
            }

            return new Plan(added.ToArray(), removed.ToArray(), kept.ToArray());
        }

        private static int IndexOf(IReadOnlyList<ulong> hashes, ulong hash)
        {
            if (hashes == null) return -1;
            for (int i = 0; i < hashes.Count; i++)
            {
                if (hashes[i] == hash) return i;
            }
            return -1;
        }
    }
}
