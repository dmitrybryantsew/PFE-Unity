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
    /// <para><b>Why a seed and a digest instead of the whole room-key set (protocol v2).</b> The set
    /// existed because each machine generated its own world from a stream it seeded independently, so
    /// the only way to know the two maps agreed was to ship every key — up to 4096 of them, which
    /// fragments across several MTU-sized chunks on the first message a peer receives. Generation is
    /// now derived from <see cref="WorldSeed"/> through
    /// <c>world → land(landId, landStage) → room(roomKey)</c>, so the room set is a <i>function</i> of
    /// values the contract already carries. Shipping the keys was shipping the answer to a sum both
    /// sides can compute.</para>
    ///
    /// <para><b>Why the digest is still here.</b> Determinism is an invariant of this build, not of the
    /// wire. Two peers on different builds — different room templates, a changed generation rule —
    /// generate different maps from the same seed. Comparing only the seed would call that a match and
    /// then apply host state to a room the client does not have, which is the exact failure the
    /// contract exists to prevent. The seed names the <i>cause</i>; the digest catches the
    /// <i>effect</i>.</para>
    ///
    /// <para><b>What the shrink costs.</b> A mismatch can no longer name the first differing room key.
    /// It names the seed and the room count instead, which is strictly better for the common case
    /// (peers disagreeing about the seed) and worse for the rare one (same seed, different content).
    /// The rare case is a build mismatch, and the fix for that is rebuilding, not reconciling.</para>
    ///
    /// <para><b>Why <see cref="RoomCount"/> is separate from the digest.</b> The fold of an empty set
    /// equals the fold of a set whose members cancel, and a zero-room capture must read as "the world
    /// is not built yet" rather than "an empty world". The count is the readiness signal;
    /// <see cref="HasCheckpoint"/> has the same shape and the same reason.</para>
    /// </summary>
    public sealed class PfeWorldContract
    {
        public int Version;

        /// <summary>The 64-bit generation root both peers must share. See <c>IWorldSeed</c>.</summary>
        public ulong WorldSeed;

        /// <summary>How many rooms the sender's world has. Zero means "not built yet".</summary>
        public int RoomCount;

        /// <summary>
        /// Order-independent fold of the sorted, de-duplicated room-key set —
        /// <c>PfeWorldContractRules.RoomSetSignature</c>, the same fold the snapshot path uses.
        /// </summary>
        public ulong RoomSetHash;

        public string LandId;
        public string MissionId;
        public bool HasCheckpoint;
        public string CheckpointCode;
        public int Difficulty;

        /// <summary>
        /// The land layout inputs the world build actually read: the stage and the two visited gates
        /// (<c>visited</c> retires the procedural <c>beg*</c> entry room; <c>mbase_visited</c> gates
        /// conf 4's). <see cref="PFE.Core.Rng.RngKeys.LandKey"/> deliberately excludes them, so two
        /// peers that read these differently generate different maps from the same seed — they are
        /// world inputs, compared as world identity, not campaign decoration. The capture writes the
        /// (0, false, false) sentinel for authored lands, whose layouts ignore all three, so two
        /// identical authored maps can never mismatch on a flag that did not shape them.
        /// </summary>
        public int LandStage;

        /// <summary><see cref="LandStage"/> — the per-land <c>visited</c> gate.</summary>
        public bool LandVisited;

        /// <summary><see cref="LandStage"/> — the campaign-wide <c>mbase_visited</c> trigger.</summary>
        public bool LandMbaseVisited;
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

        /// <summary>
        /// v2 replaced the room-key array with <c>(WorldSeed, RoomCount, RoomSetHash)</c>. The bump is
        /// deliberate: a v1 peer reading v2 would take the seed's high word as a room count and decode
        /// nonsense, so the version check must reject rather than guess.
        ///
        /// <para>v3 appends the land layout inputs <c>(LandStage, LandVisited, LandMbaseVisited)</c>
        /// after Difficulty. They are world inputs — two peers that read them differently generate
        /// different maps from the same seed — so they ride the contract and are compared before the
        /// digest. Appending keeps every v2 offset valid; a v2 frame is still refused by the version
        /// check rather than misread.</para>
        /// </summary>
        public const int CurrentVersion = 3;

        /// <summary>A sanity bound on <c>RoomCount</c>, not an array capacity any more.</summary>
        public const int MaxRooms = 4096;

        public const int MaxStringBytes = 256;

        public static bool TrySerialize(PfeWorldContract value, out byte[] bytes, out string reason)
        {
            bytes = null;
            reason = "none";
            if (value == null)
            {
                reason = "no world contract to send";
                return false;
            }
            if (value.RoomCount <= 0)
            {
                reason = "world is not built yet (zero rooms)";
                return false;
            }
            if (value.RoomCount > MaxRooms)
            {
                reason = "world has too many rooms (" + value.RoomCount + ")";
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
                writer.Write(value.WorldSeed);
                writer.Write(value.RoomCount);
                writer.Write(value.RoomSetHash);
                WriteString(writer, land);
                WriteString(writer, mission);
                writer.Write((byte)(value.HasCheckpoint ? 1 : 0));
                WriteString(writer, checkpoint);
                writer.Write(value.Difficulty);
                writer.Write(value.LandStage);
                writer.Write((byte)(value.LandVisited ? 1 : 0));
                writer.Write((byte)(value.LandMbaseVisited ? 1 : 0));
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

                    if (!HasBytes(reader, 8)) return Refuse(out reason, "truncated world contract (world seed)");
                    ulong worldSeed = reader.ReadUInt64();

                    if (!HasBytes(reader, 4)) return Refuse(out reason, "truncated world contract (room count)");
                    int count = reader.ReadInt32();
                    if (count <= 0 || count > MaxRooms)
                        return Refuse(out reason, "world contract room count " + count + " out of range");

                    if (!HasBytes(reader, 8)) return Refuse(out reason, "truncated world contract (room set hash)");
                    ulong roomSetHash = reader.ReadUInt64();

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

                    if (!HasBytes(reader, 4)) return Refuse(out reason, "truncated world contract (land stage)");
                    int landStage = reader.ReadInt32();

                    if (!HasBytes(reader, 1)) return Refuse(out reason, "truncated world contract (land visited flag)");
                    byte visitedFlag = reader.ReadByte();
                    if (visitedFlag > 1)
                        return Refuse(out reason, "bad world contract land visited flag " + visitedFlag);

                    if (!HasBytes(reader, 1)) return Refuse(out reason, "truncated world contract (mbase visited flag)");
                    byte mbaseFlag = reader.ReadByte();
                    if (mbaseFlag > 1)
                        return Refuse(out reason, "bad world contract mbase visited flag " + mbaseFlag);

                    // A well-formed prefix followed by garbage is still a malformed frame. Refusing it
                    // keeps a future field append from silently reading the old layout.
                    if (reader.BaseStream.Position != reader.BaseStream.Length)
                        return Refuse(out reason, "trailing bytes in world contract");

                    value = new PfeWorldContract
                    {
                        Version = version,
                        WorldSeed = worldSeed,
                        RoomCount = count,
                        RoomSetHash = roomSetHash,
                        LandId = land,
                        MissionId = mission,
                        HasCheckpoint = flag == 1,
                        CheckpointCode = checkpoint,
                        Difficulty = difficulty,
                        LandStage = landStage,
                        LandVisited = visitedFlag == 1,
                        LandMbaseVisited = mbaseFlag == 1,
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
            return contract != null && contract.RoomCount > 0;
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

        /// <summary>
        /// Exact two-pointer comparison of two normalized sets, naming the first key each side has and
        /// the other does not.
        ///
        /// <para><b>Why this survives the shrink.</b> The wire carries a digest now, so the gate cannot
        /// use this. It is kept as the <i>oracle for the digest</i>: the offline fixture asserts
        /// "equal signature ⇔ no difference" over generated sets, which is the only way to know the
        /// fold is not silently weaker than the comparison it replaced. Without that, a hash collision
        /// would read as a match forever and nothing would ever say so.</para>
        /// </summary>
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

            // ── World identity, in causal order ─────────────────────────────────────────────────
            //
            // Seed first, because it is the cause: two peers with different seeds generated different
            // maps, and saying so is more useful than saying the digests differ. The digest is still
            // checked, because the same seed on two different builds produces different maps — that is
            // a real bug class (a client on an old build) that determinism alone cannot catch.
            if (host.WorldSeed != local.WorldSeed)
            {
                reason = "world seed differs (host 0x" + host.WorldSeed.ToString("X16")
                    + ", local 0x" + local.WorldSeed.ToString("X16") + ")";
                return PfeWorldVerdict.WorldMismatch;
            }

            // Land layout inputs, in the order the build reads them. Stage and the two visited gates
            // are read BEFORE the world is built (LandLayoutPlanner gates beg* on !visited), and
            // RngKeys.LandKey deliberately excludes them — so two peers that disagree about them
            // generate different maps from the same seed. Naming the flag is actionable in a way
            // "room set hash differs" is not. The capture writes (0,false,false) for authored lands,
            // whose layouts ignore all three, so this can only fire for a procedural build where the
            // gates actually shaped the map.
            if (host.LandStage != local.LandStage)
            {
                reason = "land stage differs (host " + host.LandStage + ", local " + local.LandStage
                    + ") — same seed, so the two builds generate different lands";
                return PfeWorldVerdict.WorldMismatch;
            }
            if (host.LandVisited != local.LandVisited)
            {
                reason = "land visited flag differs (host " + (host.LandVisited ? 1 : 0)
                    + ", local " + (local.LandVisited ? 1 : 0)
                    + ") — the beg* entry gate makes these different maps";
                return PfeWorldVerdict.WorldMismatch;
            }
            if (host.LandMbaseVisited != local.LandMbaseVisited)
            {
                reason = "mbase visited flag differs (host " + (host.LandMbaseVisited ? 1 : 0)
                    + ", local " + (local.LandMbaseVisited ? 1 : 0)
                    + ") — conf 4's entry gate makes these different maps";
                return PfeWorldVerdict.WorldMismatch;
            }

            if (host.RoomCount != local.RoomCount)
            {
                reason = "room count differs (host " + host.RoomCount + ", local " + local.RoomCount
                    + ") — same seed, so the two builds generate different lands";
                return PfeWorldVerdict.WorldMismatch;
            }

            if (host.RoomSetHash != local.RoomSetHash)
            {
                reason = "room set hash differs (host 0x" + host.RoomSetHash.ToString("X16")
                    + ", local 0x" + local.RoomSetHash.ToString("X16")
                    + ") — same seed and count, so the room identities differ";
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
