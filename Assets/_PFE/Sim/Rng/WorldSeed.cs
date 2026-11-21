using System;

namespace PFE.Core.Rng
{
    /// <summary>
    /// The root of every derived stream in one world.
    ///
    /// <para><b>Why this is a service and not the constant it replaces.</b> The seed used to be
    /// <c>0x853c49e6748fea9b</c>, compiled into <c>GameLifetimeScope</c>. Two processes therefore
    /// agreed by accident of building the same binary — not by contract — and a host and a client
    /// could silently disagree the moment either was built differently. A world seed is world state:
    /// it belongs in the save (where <c>WorldSaveData.worldSeed</c> already declares it) and it must
    /// be published by the host when a peer joins.</para>
    ///
    /// <para><b>What it is not.</b> It is not a switch for runtime determinism. Enemy AI, damage and
    /// drops stay host-authoritative and replicated; only generation-time content is derived from
    /// this value.</para>
    /// </summary>
    public interface IWorldSeed
    {
        /// <summary>The 64-bit root every generation stream descends from.</summary>
        ulong Value { get; }
    }

    /// <summary>Immutable <see cref="IWorldSeed"/>.</summary>
    public sealed class WorldSeed : IWorldSeed
    {
        /// <summary>
        /// The seed a world gets when nothing supplied one. Matches the value
        /// <c>PcgRngService</c> used to hard-code, so an unsupplied world behaves as before.
        /// </summary>
        public const ulong DefaultValue = 0x853c49e6748fea9bUL;

        public WorldSeed(ulong value)
        {
            Value = value;
        }

        public ulong Value { get; }

        /// <summary>A world seeded from the historical default. Explicit, not implicit.</summary>
        public static WorldSeed Default => new WorldSeed(DefaultValue);

        public override string ToString()
        {
            return "0x" + Value.ToString("X16");
        }
    }

    /// <summary>
    /// The derivation keys every generation path agrees on.
    ///
    /// <para><b>Why a named helper and not an ad-hoc hash at each call site.</b> The key that decides
    /// a room's <i>content</i> and the key that names the room <i>on the wire</i> must be the same
    /// number, or a peer can agree about which room it is looking at while disagreeing about what is
    /// inside it. <see cref="PFE.Sim.State.SimSnapshotProjectionRules.RoomKey"/> is the wire key, so
    /// this class forwards to it rather than duplicating the hash.</para>
    /// </summary>
    public static class RngKeys
    {
        /// <summary>Sub-stream index for the land's layout and room set.</summary>
        public const ulong LandSubStream = 0UL;

        /// <summary>Sub-stream index for a room's tiles and authored placement.</summary>
        public const ulong TilesSubStream = 1UL;

        /// <summary>Sub-stream index for a room's enemy families, variants and counts.</summary>
        public const ulong EnemiesSubStream = 2UL;

        /// <summary>Sub-stream index for a room's container and floor loot.</summary>
        public const ulong LootSubStream = 3UL;

        /// <summary>
        /// Sub-stream index for <c>RoomPopulator.PopulateRoom</c> — the authored object table, the
        /// enemy spawn points and the enemy family rolls for one room. One stream per room is enough
        /// because the pass itself is a fixed sequence of draws; what must not vary is where the
        /// sequence starts.
        /// </summary>
        public const ulong PopulateSubStream = 4UL;

        /// <summary>Prefix for a land's identity, so a land id cannot collide with a room id.</summary>
        public const string LandKeyPrefix = "land:";

        /// <summary>
        /// A land's identity as a generation key: <c>(landId, landStage)</c>.
        ///
        /// <para><b>Why the stage is part of the key.</b> A land is rebuilt on every descent and its
        /// layout depends on the stage it is entered at (<c>Land.as:180-183</c> shortens the grid on a
        /// conf-0 first run). Two builds of the same land at different stages are different maps, so
        /// they must not share a stream.</para>
        ///
        /// <para><b>What is deliberately not in the key.</b> <c>visited</c>/<c>mbaseVisited</c> also
        /// change the layout, but they are campaign state that both peers read from the same save. If
        /// they disagree the two maps differ, and that is a campaign divergence the world contract
        /// reports — not something a different RNG stream could paper over.</para>
        ///
        /// <para><b>Ordinal and platform-independent.</b> <c>string.GetHashCode</c> is randomised per
        /// process on .NET Core, so using it here would give each peer a different land stream while
        /// looking correct in a single-process test.</para>
        /// </summary>
        public static ulong LandKey(string landId, int landStage)
        {
            ulong idHash = PFE.Core.Ids.EntityId.ComputeFnv1a64(
                LandKeyPrefix + (landId ?? string.Empty));
            return PcgRngService.Hash64(idHash, (ulong)(uint)landStage);
        }
    }

    /// <summary>
    /// The world → land → room stream chain, as one place instead of an ad-hoc hash per call site.
    ///
    /// <para><b>The chain.</b> <c>world seed → land(landId, landStage) → room(roomKey)</c>. Every
    /// arrow is <c>PcgRngService.Derive</c>, which hashes the parent's <i>seed</i> and never its live
    /// cursor, so a land's contents do not depend on which land was built before it and a room's
    /// contents do not depend on which room was populated before it.</para>
    ///
    /// <para><b>Why a land stream and not one global stream.</b> Within a land the build order is
    /// fixed — the plan fills cells in ascending <c>(x, y)</c> and <c>BuildProceduralLand</c> walks
    /// that plan — so a cursor is safe <i>inside</i> a land. What was not safe is the stream crossing
    /// a land boundary, which is what a process-wide cursor did.</para>
    /// </summary>
    public static class WorldRng
    {
        /// <summary>The root every derived stream in this world descends from.</summary>
        public static IRngService Root(IWorldSeed seed)
        {
            return new PcgRngService((seed ?? WorldSeed.Default).Value);
        }

        /// <summary>
        /// The stream one land's build draws from: layout, doors, prob selection, checkpoint
        /// placement, template choice and room difficulty.
        /// </summary>
        public static IRngService ForLand(IWorldSeed seed, string landId, int landStage)
        {
            return Root(seed).Derive(RngStream.Spawn, RngKeys.LandKey(landId, landStage),
                RngKeys.LandSubStream);
        }

        /// <summary>
        /// A room-scoped child of a land stream. <paramref name="roomKey"/> must be
        /// <c>SimSnapshotProjectionRules.RoomKey</c>, so the key that decides a room's content and the
        /// key that names it on the wire stay the same number.
        /// </summary>
        public static IRngService ForRoom(IRngService landStream, ulong roomKey, ulong subStream)
        {
            if (landStream == null) throw new ArgumentNullException(nameof(landStream));
            return landStream.Derive(RngStream.Spawn, roomKey, subStream);
        }

        /// <summary>
        /// One room <i>object</i>'s stream — a container's contents, in practice.
        ///
        /// <para><b>Why the object needs its own key.</b> Keying by the room alone gives every chest in
        /// a room the same inventory, which is more obviously wrong than the cursor it replaces. Keying
        /// by the container <i>type</i> is worse still: it makes every chest in the world identical.
        /// <paramref name="objectIndex"/> is <c>ObjectInstance.spawnIndex</c> — assigned once, in
        /// population order, so both peers agree on which chest is which.</para>
        ///
        /// <para><b>What this does not do.</b> It fixes what a container <i>holds</i>. Whether it is
        /// already empty is runtime state and still has to be replicated; a derived inventory does not
        /// stop two peers from both looting the same chest.</para>
        /// </summary>
        public static IRngService ForObject(IRngService landStream, ulong roomKey, int objectIndex,
            ulong subStream)
        {
            if (landStream == null) throw new ArgumentNullException(nameof(landStream));

            ulong objectKey = PcgRngService.Hash64(roomKey, (ulong)(uint)objectIndex);
            return landStream.Derive(RngStream.Spawn, objectKey, subStream);
        }
    }
}
