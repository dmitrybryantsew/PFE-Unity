using System;

namespace PFE.Core.Ids
{
    /// <summary>
    /// Deterministic, stable runtime entity identifier.
    /// Wraps a 64-bit FNV-1a hash for fast O(1) equality and lookups on hot paths,
    /// while preserving the structured debug string for serialization and inspection.
    ///
    /// Schemes:
    ///   - Room spawns: roomId:spawnType:spawnIndex (e.g. "r_03_07:enemy_grunt:002")
    ///   - Players: player:slotIndex (e.g. "player:0")
    ///   - Runtime ephemerals: runtime:tickIndex:counter (e.g. "runtime:1420:001")
    /// </summary>
    [Serializable]
    public readonly struct EntityId : IEquatable<EntityId>, IComparable<EntityId>
    {
        public readonly ulong Hash;
        public readonly string DebugString;

        public bool IsValid => Hash != 0;

        public static readonly EntityId Empty = new EntityId(0, string.Empty);

        public EntityId(ulong hash, string debugString)
        {
            Hash = hash;
            DebugString = debugString ?? string.Empty;
        }

        /// <summary>Creates an ID for a static or procedural room spawn.</summary>
        public static EntityId CreateForRoomSpawn(string roomId, string spawnType, int spawnIndex)
        {
            string idString = $"{roomId}:{spawnType}:{spawnIndex:D3}";
            return new EntityId(ComputeFnv1a64(idString), idString);
        }

        /// <summary>Creates a cross-room player entity ID.</summary>
        public static EntityId CreateForPlayer(int slotIndex = 0)
        {
            string idString = $"player:{slotIndex}";
            return new EntityId(ComputeFnv1a64(idString), idString);
        }

        /// <summary>Creates an ephemeral runtime entity ID tied to simulation tick.</summary>
        public static EntityId CreateRuntime(long tickIndex, int counter)
        {
            string idString = $"runtime:{tickIndex}:{counter:D3}";
            return new EntityId(ComputeFnv1a64(idString), idString);
        }

        /// <summary>Parses or reconstructs an EntityId from a serialized string.</summary>
        public static EntityId FromString(string text)
        {
            if (string.IsNullOrEmpty(text)) return Empty;
            return new EntityId(ComputeFnv1a64(text), text);
        }

        /// <summary>
        /// 64-bit FNV-1a hash algorithm. Fast, deterministic, non-cryptographic.
        /// </summary>
        public static ulong ComputeFnv1a64(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < text.Length; i++)
            {
                hash ^= (byte)text[i];
                hash = unchecked(hash * 1099511628211UL);
            }
            return hash != 0 ? hash : 1;
        }

        public bool Equals(EntityId other) => Hash == other.Hash;

        public override bool Equals(object obj) => obj is EntityId other && Equals(other);

        public override int GetHashCode() => Hash.GetHashCode();

        public int CompareTo(EntityId other) => Hash.CompareTo(other.Hash);

        public override string ToString() => string.IsNullOrEmpty(DebugString) ? Hash.ToString("X16") : DebugString;

        public static bool operator ==(EntityId left, EntityId right) => left.Hash == right.Hash;

        public static bool operator !=(EntityId left, EntityId right) => left.Hash != right.Hash;
    }
}
