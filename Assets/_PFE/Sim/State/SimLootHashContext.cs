using System;
using System.Collections.Generic;

namespace PFE.Sim.State
{
    /// <summary>
    /// The reverse lookup a replicated drop needs: <see cref="SimDroppedItem.ItemIdHash"/> is a one-way
    /// hash of the content id, so a client that wants to spawn the drop's view has to map the hash back
    /// to an id before it can resolve a definition.
    ///
    /// <para><b>Why a catalog build, not a wire change.</b> Both peers run the same content build, so the
    /// hash → id map is computable on each side from its own registry — enumerating every item id with
    /// <see cref="SimSnapshotProjectionRules.LootItemKey(string, bool)"/> and every weapon id the same
    /// way. Sending the id string on the wire would be a protocol change for a question both sides can
    /// already answer; the slice keeps the wire byte-for-byte as it is. The <c>isWeapon</c> half of the
    /// key matters: 32 ids exist in BOTH the item and the weapon tables, so a bare id hash would
    /// collide.</para>
    ///
    /// <para><b>Lazy sources, exactly like <c>ContentInternContext</c>.</b> The content registry is empty
    /// at container build time; the map materializes on first use, which is always a spawn path that
    /// cannot run before the content exists. Main-thread only, like every ambient carrier in this
    /// project.</para>
    ///
    /// <para><b>Both id forms are keys.</b> The registry stores qualified ids ("pfe.base.medkit") with
    /// bare aliases ("medkit"), and the hash on the wire is computed from whichever form the producing
    /// path happened to carry — loot tables and authored <c>&lt;item&gt;</c> children name the bare
    /// oracle id; a definition-driven path can carry the qualified one. The published sources yield
    /// every id in both forms (see <c>GameLifetimeScope.IdsInBothForms</c>), so each form hashes to
    /// its own key and both resolve. Keying on only one form makes every drop from the other form
    /// unresolvable — which reads as the drop counter growing every snapshot with zero spawns.</para>
    /// </summary>
    public static class SimLootHashContext
    {
        private static Func<IEnumerable<string>> _itemIds;
        private static Func<IEnumerable<string>> _weaponIds;

        private static Dictionary<ulong, string> _byHash;

        /// <summary>Publish the id sources. Called once from the DI build callback; a second call
        /// replaces them and drops the built map, which is the reload case, not a leak.</summary>
        public static void SetSources(Func<IEnumerable<string>> itemIds, Func<IEnumerable<string>> weaponIds)
        {
            _itemIds = itemIds;
            _weaponIds = weaponIds;
            _byHash = null;
        }

        /// <summary>The content id for a wire hash, or false when this build does not carry it — a
        /// counted refusal in the applier, never a guessed definition.</summary>
        public static bool TryGetId(ulong itemIdHash, bool isWeapon, out string id)
        {
            EnsureBuilt();
            return _byHash.TryGetValue(itemIdHash, out id);
        }

        /// <summary>How many hash → id entries the built map holds. The offline fixture's readback that
        /// the map was actually built from the sources.</summary>
        public static int Count
        {
            get
            {
                EnsureBuilt();
                return _byHash.Count;
            }
        }

        /// <summary>Drop the sources and the map. Test hook and the return-to-title case.</summary>
        public static void Reset()
        {
            _itemIds = null;
            _weaponIds = null;
            _byHash = null;
        }

        private static void EnsureBuilt()
        {
            if (_byHash != null) return;

            var byHash = new Dictionary<ulong, string>(1024);
            AddAll(byHash, _itemIds, isWeapon: false);
            AddAll(byHash, _weaponIds, isWeapon: true);
            _byHash = byHash;
        }

        private static void AddAll(Dictionary<ulong, string> byHash, Func<IEnumerable<string>> source,
                                   bool isWeapon)
        {
            if (source == null) return;

            IEnumerable<string> ids = source();
            if (ids == null) return;

            foreach (string id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                byHash[SimSnapshotProjectionRules.LootItemKey(id, isWeapon)] = id;
            }
        }
    }
}
