using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// Phase D: the executable contract for keyed RNG derivation.
    ///
    /// <para><b>The defect these tests exist for.</b> <see cref="IRngService.GetStream"/> seeds a child
    /// from the parent's <i>live</i> state, so the child depends on how many draws every earlier caller
    /// made. Two machines that populate rooms in a different order therefore get different enemies in
    /// the same room, which is why the room sets never matched and why <c>PfeWorldContract</c> had to
    /// compare two independently generated worlds. <see cref="IKeyedRng.Derive"/> hashes the seed and
    /// the keys and never reads the cursor.</para>
    ///
    /// <para><b>Why there is no literal oracle.</b> AS3 is unseeded by design (see
    /// <c>LandLayoutPlanner</c>), so no generated value can be pinned to the original. The oracle is an
    /// <i>invariant</i>: the same seed and key must agree regardless of history. That property alone
    /// can be satisfied by returning a constant, so every positive test is paired with a negative one
    /// that requires different seeds or keys to <i>differ</i>. Delete the negative and the positive
    /// stops meaning anything.</para>
    /// </summary>
    public sealed class RngDerivationTests
    {
        private const ulong SeedA = 0x853c49e6748fea9bUL;
        private const ulong SeedB = 0x853c49e6748fea9cUL;

        private const ulong RoomOne = 0x1111111111111111UL;
        private const ulong RoomTwo = 0x2222222222222222UL;

        private static uint[] Draw(IRngService rng, int count)
        {
            var values = new uint[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = rng.NextUInt();
            }
            return values;
        }

        private static void AssertSame(uint[] expected, uint[] actual, string because)
        {
            Assert.AreEqual(expected.Length, actual.Length, because + " (length)");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], actual[i], because + " (index " + i + ")");
            }
        }

        /// <summary>
        /// The property the whole phase exists for: what a room generates must not depend on what was
        /// drawn before it.
        ///
        /// <para><b>This test fails against the pre-Phase-D implementation.</b> <c>GetStream</c> seeded
        /// the child from <c>_state</c>, so the second instance — which has advanced its cursor 128
        /// times — would hand out a different child. That is the regression this pins.</para>
        /// </summary>
        [Test]
        public void Derive_IsIndependentOfHowManyDrawsTheParentHasMade()
        {
            var fresh = new PcgRngService(SeedA);
            IRngService freshRoom = fresh.Derive(RngStream.Spawn, RoomOne);
            uint[] freshValues = Draw(freshRoom, 16);

            var used = new PcgRngService(SeedA);
            Draw(used, 128);
            IRngService usedRoom = used.Derive(RngStream.Spawn, RoomOne);
            uint[] usedValues = Draw(usedRoom, 16);

            AssertSame(freshValues, usedValues,
                "a room's stream must not depend on the parent's cursor position");
        }

        /// <summary>
        /// The negative control for the test above. Without it, an implementation that returned a
        /// constant would pass every positive assertion in this file.
        /// </summary>
        [Test]
        public void Derive_DiffersWhenTheSeedDiffers()
        {
            IRngService fromA = new PcgRngService(SeedA).Derive(RngStream.Spawn, RoomOne);
            IRngService fromB = new PcgRngService(SeedB).Derive(RngStream.Spawn, RoomOne);

            uint[] a = Draw(fromA, 16);
            uint[] b = Draw(fromB, 16);

            CollectionAssert.AreNotEqual(a, b,
                "two different world seeds must not produce the same room contents");
        }

        /// <summary>
        /// The other negative control: the key has to matter, or every room in a world would be
        /// populated identically and the room set would agree for the wrong reason.
        /// </summary>
        [Test]
        public void Derive_DiffersWhenTheKeyDiffers()
        {
            var root = new PcgRngService(SeedA);

            uint[] one = Draw(root.Derive(RngStream.Spawn, RoomOne), 16);
            uint[] two = Draw(root.Derive(RngStream.Spawn, RoomTwo), 16);

            CollectionAssert.AreNotEqual(one, two,
                "two different rooms must not receive the same population stream");
        }

        /// <summary>
        /// Chained derivation — <c>world → land → room</c> — must be stable too, because the room key is
        /// derived from the land stream in the intended architecture.
        /// </summary>
        [Test]
        public void Derive_ChainsWithoutDependingOnOrder()
        {
            var fresh = new PcgRngService(SeedA);
            uint[] freshRoom = Draw(fresh
                .Derive(RngStream.Spawn, RoomOne)
                .Derive(RngStream.Spawn, RoomTwo), 16);

            var used = new PcgRngService(SeedA);
            Draw(used, 64);
            IRngService land = used.Derive(RngStream.Spawn, RoomOne);
            Draw(land, 64);
            uint[] usedRoom = Draw(land.Derive(RngStream.Spawn, RoomTwo), 16);

            AssertSame(freshRoom, usedRoom, "a chained derivation must be a pure function of the chain");
        }

        /// <summary>
        /// Deriving is not drawing: asking twice for the same child must not change the answer, or a
        /// caller that resolves its stream lazily would silently get a different room.
        /// </summary>
        [Test]
        public void Derive_DoesNotAdvanceTheParent()
        {
            var root = new PcgRngService(SeedA);

            uint[] first = Draw(root.Derive(RngStream.Loot, RoomOne), 8);
            uint[] second = Draw(root.Derive(RngStream.Loot, RoomOne), 8);

            AssertSame(first, second, "Derive must not consume the parent's cursor");
        }

        /// <summary>
        /// The domain separates streams that share a key, so a room's loot cannot be correlated with its
        /// enemies.
        /// </summary>
        [Test]
        public void Derive_SeparatesDomainsForTheSameKey()
        {
            var root = new PcgRngService(SeedA);

            uint[] spawn = Draw(root.Derive(RngStream.Spawn, RoomOne), 8);
            uint[] loot = Draw(root.Derive(RngStream.Loot, RoomOne), 8);

            CollectionAssert.AreNotEqual(spawn, loot, "one room's spawn and loot streams must differ");
        }

        /// <summary>
        /// The interface decision, pinned: <see cref="IRngService"/> was left unchanged so the dozen
        /// existing doubles keep compiling. A double therefore does <i>not</i> support keyed derivation,
        /// the extension must not throw, and callers that need the guarantee must be able to detect it
        /// rather than assume it.
        /// </summary>
        [Test]
        public void Derive_FallsBackForAnImplementationThatIsNotKeyed()
        {
            var double_ = new NonKeyedRng();

            Assert.IsFalse(((IRngService)double_).SupportsDerivation(),
                "a plain IRngService must report that it cannot guarantee keyed derivation");

            Assert.DoesNotThrow(() => ((IRngService)double_).Derive(RngStream.Spawn, RoomOne),
                "the extension must degrade rather than throw for an existing double");

            Assert.IsTrue(((IRngService)new PcgRngService(SeedA)).SupportsDerivation(),
                "the production generator must support keyed derivation");
        }

        [Test]
        public void WorldSeed_DefaultIsExplicitAndStable()
        {
            Assert.AreEqual(WorldSeed.DefaultValue, WorldSeed.Default.Value,
                "an unsupplied world must reproduce the historical seed");
            Assert.AreEqual(WorldSeed.DefaultValue, new WorldSeed(WorldSeed.DefaultValue).Value,
                "a supplied seed must be reported unchanged");
        }

        // ── The land chain: world seed → land(landId, landStage) → room ───────────────────────
        //
        // These are the fixtures for the property the phase actually needed. The three above prove
        // Derive is a pure function; these prove the *chain* is, which is what decides whether two
        // peers build the same map.

        [Test]
        public void WorldRng_ForLandIsAPureFunctionOfSeedLandAndStage()
        {
            var world = new WorldSeed(SeedA);

            uint[] first = Draw(WorldRng.ForLand(world, "surf", 2), 16);
            uint[] second = Draw(WorldRng.ForLand(world, "surf", 2), 16);

            AssertSame(first, second, "asking for the same land twice must give the same map");
        }

        /// <summary>
        /// The defect a process-wide cursor caused: the same land produced a different map depending
        /// on which land had been built before it. Building a different land in between must not move
        /// a single draw of this one.
        /// </summary>
        [Test]
        public void WorldRng_ALandDoesNotDependOnWhichLandWasBuiltFirst()
        {
            var world = new WorldSeed(SeedA);

            Draw(WorldRng.ForLand(world, "surf", 2), 256);
            uint[] surfFirst = Draw(WorldRng.ForLand(world, "surf", 2), 16);

            Draw(WorldRng.ForLand(world, "wasteland", 3), 512);
            uint[] surfAfter = Draw(WorldRng.ForLand(world, "surf", 2), 16);

            AssertSame(surfFirst, surfAfter,
                "a land must not inherit the history of the lands built before it");
        }

        [Test]
        public void WorldRng_LandStageIsPartOfTheLandKey()
        {
            var world = new WorldSeed(SeedA);

            CollectionAssert.AreNotEqual(
                Draw(WorldRng.ForLand(world, "surf", 2), 16),
                Draw(WorldRng.ForLand(world, "surf", 3), 16),
                "the same land entered at another stage is a different map (Land.as:180-183)");
        }

        [Test]
        public void WorldRng_LandIdIsPartOfTheLandKey()
        {
            var world = new WorldSeed(SeedA);

            CollectionAssert.AreNotEqual(
                Draw(WorldRng.ForLand(world, "surf", 2), 16),
                Draw(WorldRng.ForLand(world, "wasteland", 2), 16),
                "two lands at the same stage must not share a layout stream");
        }

        [Test]
        public void WorldRng_WorldSeedIsPartOfTheLandStream()
        {
            CollectionAssert.AreNotEqual(
                Draw(WorldRng.ForLand(new WorldSeed(SeedA), "surf", 2), 16),
                Draw(WorldRng.ForLand(new WorldSeed(SeedB), "surf", 2), 16),
                "a different world seed must produce a different map");
        }

        /// <summary>
        /// The room path, which is the one that reaches enemies. The land stream HAS been drawn from
        /// by the time a room is populated, so this is where a cursor-based derivation would leak
        /// population order into room contents.
        /// </summary>
        [Test]
        public void WorldRng_RoomStreamIgnoresHowManyRoomsWerePopulatedBeforeIt()
        {
            IRngService land = WorldRng.ForLand(new WorldSeed(SeedA), "surf", 2);

            uint[] first = Draw(WorldRng.ForRoom(land, RoomOne, RngKeys.EnemiesSubStream), 16);
            Draw(land, 4096);
            uint[] after = Draw(WorldRng.ForRoom(land, RoomOne, RngKeys.EnemiesSubStream), 16);

            AssertSame(first, after, "a room's enemies must not depend on population order");
        }

        /// <summary>
        /// Container contents, which is where this phase started: a chest must hold the same thing on
        /// every machine, and must not depend on how many chests were opened before it.
        /// </summary>
        [Test]
        public void WorldRng_ContainerContentsIgnoreOpenOrder()
        {
            IRngService land = WorldRng.ForLand(new WorldSeed(SeedA), "surf", 2);

            uint[] first = Draw(WorldRng.ForObject(land, RoomOne, 3, RngKeys.LootSubStream), 12);
            Draw(land, 2048);
            uint[] after = Draw(WorldRng.ForObject(land, RoomOne, 3, RngKeys.LootSubStream), 12);

            AssertSame(first, after, "a chest's contents must not depend on open order");
        }

        /// <summary>
        /// The failure that keying by container <i>type</i> would have produced: every chest in a room
        /// holding the same inventory. The object index is what prevents it, so it has to be pinned.
        /// </summary>
        [Test]
        public void WorldRng_TwoContainersInOneRoomHoldDifferentContents()
        {
            IRngService land = WorldRng.ForLand(new WorldSeed(SeedA), "surf", 2);

            CollectionAssert.AreNotEqual(
                Draw(WorldRng.ForObject(land, RoomOne, 3, RngKeys.LootSubStream), 12),
                Draw(WorldRng.ForObject(land, RoomOne, 4, RngKeys.LootSubStream), 12),
                "two chests in one room must not share one inventory");
        }

        [Test]
        public void WorldRng_TheSameObjectIndexInAnotherRoomIsADifferentContainer()
        {
            IRngService land = WorldRng.ForLand(new WorldSeed(SeedA), "surf", 2);

            CollectionAssert.AreNotEqual(
                Draw(WorldRng.ForObject(land, RoomOne, 3, RngKeys.LootSubStream), 12),
                Draw(WorldRng.ForObject(land, RoomTwo, 3, RngKeys.LootSubStream), 12),
                "object index 3 is a different container in a different room");
        }

        /// <summary>
        /// The land key decides what every existing save regenerates into, so it is pinned rather than
        /// merely consistent. A change here is a save migration. It is also the only guard against
        /// <c>string.GetHashCode</c>, which is randomised per process on .NET Core and would hand each
        /// peer a different land while looking perfectly correct in a single-process test.
        /// </summary>
        [Test]
        public void RngKeys_LandKeyIsPinned()
        {
            Assert.AreEqual(0x1CD811CB772C3011UL, RngKeys.LandKey("surf", 2));
            Assert.AreEqual(0x6848F40D2387327AUL, RngKeys.LandKey("wasteland", 3));
            Assert.AreEqual(0x5CEB8390AE425E5FUL, RngKeys.LandKey(null, 0));
        }

        /// <summary>A stand-in for the existing test doubles: it satisfies <see cref="IRngService"/> and
        /// nothing else.</summary>
        private sealed class NonKeyedRng : IRngService
        {
            public uint NextUInt() => 0u;
            public float NextFloat() => 0f;
            public int NextInt(int maxExclusive) => 0;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public bool Chance(float probability) => false;
            public void Shuffle<T>(IList<T> list) { }
            public IRngService GetStream(RngStream stream, int? salt = null) => this;
        }
    }
}
