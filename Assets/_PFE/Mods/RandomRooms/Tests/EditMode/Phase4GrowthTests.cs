using System.Collections.Generic;
using NUnit.Framework;
using PFE.ModAPI;
using PFE.Mods.RandomRooms.Unity;
using UnityEngine;

namespace PFE.Mods.RandomRooms.Tests.EditMode
{
    /// <summary>
    /// Dynamic growth through the new seam: TryExpandLand returns pure expansion blueprints,
    /// never touches existing rooms, and only fires near the grid boundary of the endless dungeon.
    /// </summary>
    [TestFixture]
    public class Phase4GrowthTests
    {
        private static ProceduralLandBuildResult Build5x5(RandomRoomsLandProvider provider, uint seed = 1337u)
        {
            return provider.BuildInitialLand(new ProceduralLandLaunchRequest
            {
                LandId = "random_rooms",
                Biome = "plant",
                Seed = seed
            });
        }

        [Test]
        public void TryExpandLand_RightEdge_AddsColumn()
        {
            var provider = new RandomRoomsLandProvider();
            ProceduralLandBuildResult build = Build5x5(provider);

            // x >= width - 2 triggers growth to the right; y = 0 stays away from the bottom trigger.
            ProceduralLandExpansionResult expansion = provider.TryExpandLand(
                "random_rooms", new Vector3Int(3, 0, 0), build.BoundsMin, build.BoundsMax);

            Assert.That(expansion.Expanded, Is.True);
            Assert.That(expansion.NewBoundsMax, Is.EqualTo(new Vector3Int(6, 5, 1)));
            Assert.That(expansion.NewRooms.Count, Is.EqualTo(5), "One new column of 5 rooms");

            var existing = new HashSet<Vector3Int>();
            foreach (ProceduralRoomBlueprint bp in build.Rooms) existing.Add(bp.Position);
            foreach (ProceduralRoomBlueprint bp in expansion.NewRooms)
            {
                Assert.That(bp.Position.x, Is.EqualTo(5), "New rooms must be the fresh column");
                Assert.That(existing.Contains(bp.Position), Is.False, "Growth must never re-issue an existing cell");
            }
        }

        [Test]
        public void TryExpandLand_Corner_ExpandsBothAxes()
        {
            var provider = new RandomRoomsLandProvider();
            ProceduralLandBuildResult build = Build5x5(provider);

            ProceduralLandExpansionResult expansion = provider.TryExpandLand(
                "random_rooms", new Vector3Int(4, 4, 0), build.BoundsMin, build.BoundsMax);

            Assert.That(expansion.Expanded, Is.True);
            Assert.That(expansion.NewBoundsMax, Is.EqualTo(new Vector3Int(6, 6, 1)));
            Assert.That(expansion.NewRooms.Count, Is.EqualTo(11), "6x6 minus the original 25 rooms");
        }

        [Test]
        public void TryExpandLand_ExpandedGrid_KeepsGrowing()
        {
            var provider = new RandomRoomsLandProvider();
            ProceduralLandBuildResult build = Build5x5(provider);

            ProceduralLandExpansionResult first = provider.TryExpandLand(
                "random_rooms", new Vector3Int(4, 2, 0), build.BoundsMin, build.BoundsMax);
            Assert.That(first.Expanded, Is.True);

            // Standing near the new right edge of the 6x5 grid grows it again.
            ProceduralLandExpansionResult second = provider.TryExpandLand(
                "random_rooms", new Vector3Int(5, 2, 0), first.NewBoundsMin, first.NewBoundsMax);
            Assert.That(second.Expanded, Is.True);
            Assert.That(second.NewBoundsMax, Is.EqualTo(new Vector3Int(7, 5, 1)));
            Assert.That(second.NewRooms.Count, Is.EqualTo(5));
        }

        [Test]
        public void TryExpandLand_NotNearEdge_DoesNotExpand()
        {
            var provider = new RandomRoomsLandProvider();
            ProceduralLandBuildResult build = Build5x5(provider);

            ProceduralLandExpansionResult expansion = provider.TryExpandLand(
                "random_rooms", new Vector3Int(1, 1, 0), build.BoundsMin, build.BoundsMax);

            Assert.That(expansion.Expanded, Is.False);
            Assert.That(expansion.NewRooms.Count, Is.EqualTo(0));
            Assert.That(expansion.NewBoundsMax, Is.EqualTo(build.BoundsMax));
        }

        [Test]
        public void TryExpandLand_Showroom_NeverExpands()
        {
            var provider = new RandomRoomsLandProvider();
            ProceduralLandBuildResult build = provider.BuildInitialLand(new ProceduralLandLaunchRequest
            {
                LandId = "rr_showroom",
                Biome = "plant",
                Seed = 5555u
            });

            ProceduralLandExpansionResult expansion = provider.TryExpandLand(
                "rr_showroom", new Vector3Int(3, 2, 0), build.BoundsMin, build.BoundsMax);

            Assert.That(expansion.Expanded, Is.False, "The showroom is a fixed exhibit");
        }

        [Test]
        public void TryExpandLand_IsDeterministic_PerSeed()
        {
            var firstProvider = new RandomRoomsLandProvider();
            ProceduralLandBuildResult firstBuild = Build5x5(firstProvider, 2024u);
            ProceduralLandExpansionResult first = firstProvider.TryExpandLand(
                "random_rooms", new Vector3Int(4, 4, 0), firstBuild.BoundsMin, firstBuild.BoundsMax);

            var secondProvider = new RandomRoomsLandProvider();
            ProceduralLandBuildResult secondBuild = Build5x5(secondProvider, 2024u);
            ProceduralLandExpansionResult second = secondProvider.TryExpandLand(
                "random_rooms", new Vector3Int(4, 4, 0), secondBuild.BoundsMin, secondBuild.BoundsMax);

            Assert.That(first.NewRooms.Count, Is.EqualTo(second.NewRooms.Count));
            for (int i = 0; i < first.NewRooms.Count; i++)
            {
                Assert.That(second.NewRooms[i].Position, Is.EqualTo(first.NewRooms[i].Position));
                Assert.That(second.NewRooms[i].TileDataString, Is.EqualTo(first.NewRooms[i].TileDataString));
            }
        }
    }
}
