using System;
using PFE.Core.Ids;

namespace PFE.Sim.State
{
    /// <summary>
    /// Phase 3.3 (F-06): the arithmetic a host-side snapshot projection performs every tick, kept
    /// pure so the offline wall can execute it.
    ///
    /// <para><b>Why these four functions and not the whole projector.</b> A projector is a
    /// <c>MonoBehaviour</c>-adjacent service: it enumerates live components and reads a scene manager,
    /// neither of which exists outside the editor. What <i>can</i> be wrong about it, and be caught
    /// before a live session, is exactly this arithmetic — the room key that has to agree with
    /// interest management, the wrap-safe sequence increment, the world revision that must not advance
    /// spuriously, and the order-independent room-set signature that decides whether it advances. Those
    /// live here, pinned by <c>SimSnapshotProjectionRulesTests</c>; the projector keeps only the
    /// enumeration, which a source lint pins instead.</para>
    /// </summary>
    public static class SimSnapshotProjectionRules
    {
        /// <summary>
        /// The prefix of a room's replication key. One definition, because two would drift: the key is
        /// how <c>SimSnapshot</c> names a room and how <c>RoomStreamingManager</c>'s presence rules
        /// name it, and a mismatch would silently make every delta address a room the receiver does not
        /// know — a snapshot that deserializes cleanly and reconciles against nothing.
        /// </summary>
        public const string RoomKeyPrefix = "room:";

        /// <summary>
        /// Stable room key. The prefix guarantees a non-zero key even for a room whose id hashes to
        /// zero, which matters because <c>SimRoomDelta.RoomHash</c> uses 0 as "no room".
        /// </summary>
        public static ulong RoomKey(string roomId)
        {
            return EntityId.ComputeFnv1a64(RoomKeyPrefix + (roomId ?? string.Empty));
        }

        /// <summary>Prefix for a loot stack's <c>ItemDefinition</c> id.</summary>
        public const string ItemKeyPrefix = "item:";

        /// <summary>Prefix for a loot stack's <c>WeaponDefinition</c> id.</summary>
        public const string WeaponKeyPrefix = "weapon:";

        /// <summary>
        /// <c>SimDroppedItem.ItemIdHash</c> — the content key of one loot stack.
        ///
        /// <para><b>The prefix is the point.</b> <c>ItemDefinition</c> and <c>WeaponDefinition</c> are
        /// disjoint tables in the oracle (<c>AllData.d.item</c> vs <c>AllData.d.weapon</c>), and their
        /// ids are not namespaced: an item called <c>medkit</c> and a weapon called <c>medkit</c> are
        /// two different things. Hashing the bare id would make them one stack on the receiver, which
        /// is the same conflation <c>IsWeapon</c> exists to prevent — and unlike <c>IsWeapon</c>, the
        /// hash is what the receiver keys on, so it cannot be recovered from the wire.</para>
        ///
        /// <para>The prefix also keeps the key non-zero for an empty id, which matters because the
        /// receiver treats a zero <c>EntityHash</c> as "no identity" and a zero content key would be
        /// equally unreadable.</para>
        /// </summary>
        public static ulong LootItemKey(string itemId, bool isWeapon)
        {
            string prefix = isWeapon ? WeaponKeyPrefix : ItemKeyPrefix;
            return EntityId.ComputeFnv1a64(prefix + (itemId ?? string.Empty));
        }

        /// <summary>
        /// The snapshot sequence's successor. Wrap-safe by construction: <c>uint.MaxValue</c> rolls to
        /// 0, which <see cref="SimStalenessRules.IsNewer"/> reads as newer than <c>uint.MaxValue</c>.
        /// </summary>
        public static uint NextSnapshotSequence(uint current)
        {
            return unchecked(current + 1);
        }

        /// <summary>
        /// The successor of one reliable tile-event sequence within a <c>WorldRevision</c>. Deliberately
        /// a separate rule from <see cref="NextSnapshotSequence"/>: the two counters live on different
        /// channels (reliable events vs unreliable snapshots) and advance at different rates, so sharing
        /// one variable would make a dropped snapshot look like a gap in the reliable stream.
        /// Wrap-safe by the same construction — <c>SimStalenessRules.IsNewer</c> reads a wrapped value
        /// as newer, and a receiver rejects a gap rather than applying partial geometry.
        /// </summary>
        public static uint NextReliableEventSequence(uint current)
        {
            return unchecked(current + 1);
        }

        /// <summary>
        /// The world revision after this projection. It advances <b>only</b> when the set of simulated
        /// rooms changed — a room activation, deactivation or transition. It must not advance per tick:
        /// <c>WorldRevision</c> names the reliable-event lifetime a delta belongs to, so a per-tick bump
        /// would make every snapshot claim a new lifetime and destroy the stale-delta rejection it
        /// exists to provide.
        /// </summary>
        public static long NextWorldRevision(long current, bool roomSetChanged)
        {
            return roomSetChanged ? unchecked(current + 1) : current;
        }

        /// <summary>
        /// Fold one room key into the room-set signature.
        ///
        /// <para><b>XOR, so the signature is order-independent.</b> The projector walks
        /// <c>ActiveRooms</c>, which is a <c>HashSet</c> — its enumeration order is unspecified and may
        /// change between ticks without the set changing. An order-dependent fold (FNV, a rolling
        /// multiply) would then report "the room set changed" on a tick where nothing did, bumping
        /// <c>WorldRevision</c> and invalidating every in-flight delta. XOR is commutative and
        /// associative, so only a genuine membership change moves it.</para>
        /// </summary>
        public static ulong FoldRoomSetSignature(ulong signature, ulong roomKey)
        {
            return signature ^ roomKey;
        }

        /// <summary>The empty signature — no simulated rooms, i.e. before the first projection.</summary>
        public const ulong EmptyRoomSetSignature = 0UL;
    }
}
