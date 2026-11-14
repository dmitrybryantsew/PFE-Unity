using System;
using System.IO;
using System.Text;
using PFE.Sim.State;

namespace PFE.Net
{
    /// <summary>
    /// Which of the two possible divergences the client detected.
    ///
    /// <para><b>World and campaign are deliberately separate.</b> A room-set difference means the two
    /// processes generated different maps: every <c>SimRoomDelta.RoomHash</c> and every tile journal
    /// entry addresses a room the client may not have, so applying host state is actively wrong. A
    /// campaign difference (a land, mission, checkpoint or difficulty the host reached but the client
    /// has not) is expected while live campaign events are still owed (P5): it must be <i>reported</i>,
    /// not treated as a foreign world. Collapsing the two into one "mismatch" would either block a
    /// healthy session the moment the host progresses, or silently apply state to a different map.</para>
    /// </summary>
    public enum PfeWorldVerdict
    {
        /// <summary>No contract has arrived yet. Applying host state is still allowed: the pre-P2
        /// behaviour must not regress into a session that refuses every snapshot at startup.</summary>
        Unknown = 0,
        Match = 1,
        WorldMismatch = 2,
        CampaignDivergence = 3,
        NoLocalWorld = 4,
        NoHostWorld = 5,
    }

    /// <summary>
    /// The host's world and campaign identity at one instant. Plain data: no Unity, no FishNet, no
    /// globals, so the codec and every comparison rule are executable in the offline wall.
    ///
    /// <para><b>Why the whole room-key set and not just a hash.</b> The client does not merely need to
    /// know "the worlds differ" — it needs to name the first room the host has and it does not, because
    /// that is the difference between a bad seed and a room that streamed out late. A single digest can
    /// collide and can never name a key. The exact set also means the client can later reconcile its
    /// room registry from the host's list without a second handshake.</para>
    ///
    /// <para><b>Why <see cref="HasCheckpoint"/> is a separate field.</b> The save format has the same
    /// distinction: an empty checkpoint code is a real checkpoint (the exit room's), while "no
    /// checkpoint" is a different state. Encoding absence as <c>""</c> would make those two worlds
    /// compare equal and hide a real campaign divergence.</para>
    /// </summary>
    public sealed class PfeWorldContract
    {
        public int Version;
        public ulong[] RoomKeys;
        public string LandId;
        public string MissionId;
        public bool HasCheckpoint;
        public string CheckpointCode;
        public int Difficulty;
    }

    /// <summary>
    /// Fixed-layout, self-delimiting codec. A world contract is the FIRST authoritative message a peer
    /// receives, so it is also the first place a truncated or hostile packet can arrive: every length
    /// is bounds-checked before a read, and a bad frame is refused with a named reason instead of an
    /// exception unwinding FishNet's receive loop.
    /// </summary>
    public static class PfeWorldContractCodec
    {
        /// <summary>"PFWC", so a snapshot or an envelope accidentally routed here is refused by magic.</summary>
        public const uint Magic = 0x50465743u;
        public const int CurrentVersion = 1;
        public const int MaxRooms = 4096;
        public const int MaxStringBytes = 256;

        public static bool TrySerialize(PfeWorldContract value, out byte[] bytes, out string reason)
        {
            bytes = null;
            reason = "none";
            if (value == null || value.RoomKeys == null)
            {
                reason = "no world contract to send";
                return false;
            }
            if (value.RoomKeys.Length == 0)
            {
                reason = "world is not built yet (zero rooms)";
                return false;
            }
            if (value.RoomKeys.Length > MaxRooms)
            {
                reason = "world has too many rooms (" + value.RoomKeys.Length + ")";
                return false;
            }

            byte[] land;
            byte[] mission;
            byte[] checkpoint;
            if (!TryEncodeString(value.LandId, "land id", out land, out reason)) return false;
            if (!TryEncodeString(value.MissionId, "mission id", out mission, out reason)) return false;
            if (!TryEncodeString(value.CheckpointCode, "checkpoint code", out checkpoint, out reason)) return false;

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(CurrentVersion);
                writer.Write(value.RoomKeys.Length);
                for (int i = 0; i < value.RoomKeys.Length; i++) writer.Write(value.RoomKeys[i]);
                WriteString(writer, land);
                WriteString(writer, mission);
                writer.Write((byte)(value.HasCheckpoint ? 1 : 0));
                WriteString(writer, checkpoint);
                writer.Write(value.Difficulty);
                writer.Flush();
                bytes = stream.ToArray();
                return true;
            }
        }

        public static bool TryDeserialize(byte[] bytes, out PfeWorldContract value, out string reason)
        {
            value = null;
            if (bytes == null || bytes.Length == 0)
            {
                reason = "empty world contract frame";
                return false;
            }

            try
            {
                using (var stream = new MemoryStream(bytes, writable: false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (!HasBytes(reader, 4)) return Refuse(out reason, "truncated world contract (magic)");
                    if (reader.ReadUInt32() != Magic) return Refuse(out reason, "bad world contract magic");

                    if (!HasBytes(reader, 4)) return Refuse(out reason, "truncated world contract (version)");
                    int version = reader.ReadInt32();
                    if (version != CurrentVersion)
                        return Refuse(out reason, "unsupported world contract version " + version);

                    if (!HasBytes(reader, 4)) return Refuse(out reason, "truncated world contract (room count)");
                    int count = reader.ReadInt32();
                    if (count <= 0 || count > MaxRooms)
                        return Refuse(out reason, "world contract room count " + count + " out of range");

                    if (!HasBytes(reader, (long)count * 8L))
                        return Refuse(out reason, "truncated world contract (room keys)");
                    var keys = new ulong[count];
                    for (int i = 0; i < count; i++) keys[i] = reader.ReadUInt64();

                    string land;
                    string mission;
                    string checkpoint;
                    if (!TryReadString(reader, "land id", out land, out reason)) return false;
                    if (!TryReadString(reader, "mission id", out mission, out reason)) return false;

                    if (!HasBytes(reader, 1)) return Refuse(out reason, "truncated world contract (checkpoint flag)");
                    byte flag = reader.ReadByte();
                    if (flag > 1) return Refuse(out reason, "bad world contract checkpoint flag " + flag);

                    if (!TryReadString(reader, "checkpoint code", out checkpoint, out reason)) return false;

                    if (!HasBytes(reader, 4)) return Refuse(out reason, "truncated world contract (difficulty)");
                    int difficulty = reader.ReadInt32();

                    // A well-formed prefix followed by garbage is still a malformed frame. Refusing it
                    // keeps a future field append from silently reading the old layout.
                    if (reader.BaseStream.Position != reader.BaseStream.Length)
                        return Refuse(out reason, "trailing bytes in world contract");

                    value = new PfeWorldContract
                    {
                        Version = version,
                        RoomKeys = keys,
                        LandId = land,
                        MissionId = mission,
                        HasCheckpoint = flag == 1,
                        CheckpointCode = checkpoint,
                        Difficulty = difficulty,
                    };
                    reason = "none";
                    return true;
                }
            }
            catch (Exception ex)
            {
                return Refuse(out reason, "malformed world contract: " + ex.GetType().Name);
            }
        }

        private static bool Refuse(out string reason, string text)
        {
            reason = text;
            return false;
        }

        private static bool HasBytes(BinaryReader reader, long count)
        {
            return reader.BaseStream.Position + count <= reader.BaseStream.Length;
        }

        private static bool TryEncodeString(string raw, string label, out byte[] data, out string reason)
        {
            data = Encoding.UTF8.GetBytes(raw ?? string.Empty);
            if (data.Length > MaxStringBytes)
            {
                reason = "world contract " + label + " is too long (" + data.Length + " bytes)";
                return false;
            }
            reason = "none";
            return true;
        }

        private static void WriteString(BinaryWriter writer, byte[] data)
        {
            writer.Write(data.Length);
            if (data.Length != 0) writer.Write(data);
        }

        private static bool TryReadString(BinaryReader reader, string label, out string value, out string reason)
        {
            value = string.Empty;
            if (!HasBytes(reader, 4))
                return Refuse(out reason, "truncated world contract (" + label + " length)");

            int length = reader.ReadInt32();
            if (length < 0 || length > MaxStringBytes)
                return Refuse(out reason, "world contract " + label + " length " + length + " out of range");
            if (!HasBytes(reader, length))
                return Refuse(out reason, "truncated world contract (" + label + " bytes)");

            byte[] data = reader.ReadBytes(length);
            if (data.Length != length)
                return Refuse(out reason, "truncated world contract (" + label + " bytes)");

            value = Encoding.UTF8.GetString(data);
            reason = "none";
            return true;
        }
    }

    /// <summary>
    /// Comparisons and the application gate. Pure: the session only captures live state and reads the
    /// verdict, so every decision below can be pinned by an offline fixture and mutated individually.
    /// </summary>
    public static class PfeWorldContractRules
    {
        /// <summary>How often the client re-captures its still-unbuilt world while a host contract is
        /// already waiting. A one-shot comparison at packet arrival would be permanently wrong when the
        /// world build finishes a frame later.</summary>
        public const int LocalWorldRetryTicks = 30;

        public static bool IsWorldReady(PfeWorldContract contract)
        {
            return contract != null && contract.RoomKeys != null && contract.RoomKeys.Length > 0;
        }

        /// <summary>Sorted and de-duplicated. The map's <c>rooms.Values</c> enumeration order is not
        /// guaranteed, and one duplicated key would cancel out of the XOR signature entirely.</summary>
        public static ulong[] NormalizeRoomKeys(ulong[] keys)
        {
            if (keys == null || keys.Length == 0) return Array.Empty<ulong>();
            var copy = new ulong[keys.Length];
            Array.Copy(keys, copy, keys.Length);
            Array.Sort(copy);

            int unique = 0;
            for (int i = 0; i < copy.Length; i++)
            {
                if (unique != 0 && copy[unique - 1] == copy[i]) continue;
                copy[unique++] = copy[i];
            }
            if (unique == copy.Length) return copy;

            var trimmed = new ulong[unique];
            Array.Copy(copy, trimmed, unique);
            return trimmed;
        }

        /// <summary>The project's canonical order-independent room-set fold, reused rather than a second
        /// digest. Only a membership change moves it; a re-ordered enumeration does not.</summary>
        public static ulong RoomSetSignature(ulong[] normalizedKeys)
        {
            ulong signature = SimSnapshotProjectionRules.EmptyRoomSetSignature;
            if (normalizedKeys == null) return signature;
            for (int i = 0; i < normalizedKeys.Length; i++)
                signature = SimSnapshotProjectionRules.FoldRoomSetSignature(signature, normalizedKeys[i]);
            return signature;
        }

        /// <summary>Exact two-pointer comparison of two normalized sets. A digest alone can collide;
        /// this is the decision the gate actually uses, and it names the offending keys.</summary>
        public static bool TryFindFirstDifference(ulong[] hostKeys, ulong[] localKeys,
            out ulong hostOnly, out ulong localOnly)
        {
            hostOnly = 0UL;
            localOnly = 0UL;
            ulong[] a = NormalizeRoomKeys(hostKeys);
            ulong[] b = NormalizeRoomKeys(localKeys);

            int i = 0;
            int j = 0;
            while (i < a.Length || j < b.Length)
            {
                if (i >= a.Length) { localOnly = b[j]; return true; }
                if (j >= b.Length) { hostOnly = a[i]; return true; }
                if (a[i] == b[j]) { i++; j++; continue; }
                // Both cursors sit on a key the other set does not have. Name BOTH: reporting only the
                // smaller one prints "local-only 0x0", which tells the operator nothing about the room
                // the client actually built instead.
                hostOnly = a[i];
                localOnly = b[j];
                return true;
            }
            return false;
        }

        public static PfeWorldVerdict Compare(PfeWorldContract host, PfeWorldContract local,
            out string reason)
        {
            reason = "none";
            if (host == null || !IsWorldReady(host))
            {
                reason = "no host world contract yet";
                return PfeWorldVerdict.NoHostWorld;
            }
            if (!IsWorldReady(local))
            {
                reason = "local world is not built yet";
                return PfeWorldVerdict.NoLocalWorld;
            }
            if (host.Version != PfeWorldContractCodec.CurrentVersion)
            {
                reason = "host world contract version " + host.Version + " is not supported";
                return PfeWorldVerdict.WorldMismatch;
            }

            ulong hostOnly;
            ulong localOnly;
            if (TryFindFirstDifference(host.RoomKeys, local.RoomKeys, out hostOnly, out localOnly))
            {
                reason = "room set differs (host-only 0x" + hostOnly.ToString("X16")
                    + ", local-only 0x" + localOnly.ToString("X16") + ")";
                return PfeWorldVerdict.WorldMismatch;
            }

            // Campaign scalars are compared in a fixed order so a named reason is deterministic.
            if (!string.Equals(host.LandId ?? "", local.LandId ?? "", StringComparison.Ordinal))
            {
                reason = "campaign land differs: host='" + (host.LandId ?? "") + "' local='"
                    + (local.LandId ?? "") + "'";
                return PfeWorldVerdict.CampaignDivergence;
            }
            if (!string.Equals(host.MissionId ?? "", local.MissionId ?? "", StringComparison.Ordinal))
            {
                reason = "campaign mission differs: host='" + (host.MissionId ?? "") + "' local='"
                    + (local.MissionId ?? "") + "'";
                return PfeWorldVerdict.CampaignDivergence;
            }
            if (host.HasCheckpoint != local.HasCheckpoint)
            {
                reason = "checkpoint presence differs: host=" + host.HasCheckpoint
                    + " local=" + local.HasCheckpoint;
                return PfeWorldVerdict.CampaignDivergence;
            }
            if (!string.Equals(host.CheckpointCode ?? "", local.CheckpointCode ?? "", StringComparison.Ordinal))
            {
                reason = "checkpoint code differs: host='" + (host.CheckpointCode ?? "") + "' local='"
                    + (local.CheckpointCode ?? "") + "'";
                return PfeWorldVerdict.CampaignDivergence;
            }
            if (host.Difficulty != local.Difficulty)
            {
                reason = "difficulty differs: host=" + host.Difficulty + " local=" + local.Difficulty;
                return PfeWorldVerdict.CampaignDivergence;
            }

            return PfeWorldVerdict.Match;
        }

        /// <summary>
        /// Gate BEFORE the tile/loot/unit applier. Only a definite foreign map is blocked; every other
        /// verdict (including "no contract yet" and a campaign divergence) still applies host state,
        /// because blocking those would regress the working session into a permanently frozen client.
        /// </summary>
        public static bool CanApplyHostState(PfeWorldVerdict verdict)
        {
            return verdict != PfeWorldVerdict.WorldMismatch;
        }

        /// <summary>Whether a client with a pending host contract should re-capture its own world.</summary>
        public static bool ShouldRetryLocalCapture(PfeWorldVerdict verdict, int tickCount)
        {
            return verdict == PfeWorldVerdict.NoLocalWorld
                && tickCount > 0 && tickCount % LocalWorldRetryTicks == 0;
        }

        /// <summary>Host only, and only once its own world exists. A contract with zero rooms would make
        /// every client compare against "no host world" forever.</summary>
        public static bool ShouldPublish(bool isHost, bool alreadyPublished, PfeWorldContract contract)
        {
            return isHost && !alreadyPublished && IsWorldReady(contract);
        }

        public static string Describe(PfeWorldVerdict verdict)
        {
            switch (verdict)
            {
                case PfeWorldVerdict.Match: return "match";
                case PfeWorldVerdict.WorldMismatch: return "WORLD MISMATCH";
                case PfeWorldVerdict.CampaignDivergence: return "campaign diverged";
                case PfeWorldVerdict.NoLocalWorld: return "local world pending";
                case PfeWorldVerdict.NoHostWorld: return "host contract pending";
                default: return "unknown";
            }
        }
    }
}
