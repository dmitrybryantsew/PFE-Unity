using NUnit.Framework;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// AS3 <c>Location.as:1017</c> — the doorway test an authored placement has to pass, and each of its
    /// three terms on its own.
    ///
    /// <para><b>Why this is a fixture of its own.</b> The guard is the consumer of the door-slot marking
    /// (<see cref="DoorCarver"/>), and its three terms are three separate divergences the port used to
    /// have. The expensive one was the missing <c>rem</c> term: a unit or a trap carries no <c>rem</c>,
    /// so AS3 places it even when it stands in a doorway, while the port dropped it. Combined with the
    /// old whole-border-ring marking that removed 1515 authored placements across 335 of the 550 shipped
    /// rooms — <c>rooms_plant/яма</c> lost all 19 of its units, all of its traps and most of its loot,
    /// which is how the owner found it.</para>
    ///
    /// <para><b>Why it is not an integration test.</b> Driving the real path needs a
    /// <c>MapObjectDefinition</c> ScriptableObject, and the offline harness cannot construct one
    /// (<c>ScriptableObject.CreateInstance</c> is an ECall, so every test in <c>RoomPopulatorTests</c>
    /// fails there). The predicate is therefore tested directly, and the marking it reads is tested
    /// through <c>CarveDoor</c> in <c>DoorCarverTests</c> — between them the whole condition is covered
    /// without an asset load.</para>
    /// </summary>
    [TestFixture]
    public class DoorwayPlacementGuardTests
    {
        [Test]
        public void AllThreeTermsHold_SkipsThePlacement()
        {
            Assert.IsTrue(RoomPopulator.SkipsAuthoredPlacement(
                noHolesPlace: true, rem: 1, tile: MarkedTile()));
        }

        [Test]
        public void WithoutRem_ThePlacementIsKeptEvenInADoorway()
        {
            // The unit / trap / spike case. `rem` is on only 24 of the 202 definitions — the containers
            // and the five enemy-point markers — and `Location.as:1017` tests `rem > 0` before it ever
            // looks at the tile. Paired with the test above so neither can pass vacuously: the same
            // marked tile DOES skip when `rem` is 1.
            Assert.IsFalse(RoomPopulator.SkipsAuthoredPlacement(
                noHolesPlace: true, rem: 0, tile: MarkedTile()));
        }

        [Test]
        public void ANegativeRem_IsKept()
        {
            // The oracle's test is `> 0`, not `!= 0`.
            Assert.IsFalse(RoomPopulator.SkipsAuthoredPlacement(
                noHolesPlace: true, rem: -1, tile: MarkedTile()));
        }

        [Test]
        public void InARoomThatAllowsHoles_ThePlacementIsKept()
        {
            Assert.IsFalse(RoomPopulator.SkipsAuthoredPlacement(
                noHolesPlace: false, rem: 1, tile: MarkedTile()));
        }

        [Test]
        public void OnATileThatWasNeverMarked_ThePlacementIsKept()
        {
            Assert.IsFalse(RoomPopulator.SkipsAuthoredPlacement(
                noHolesPlace: true, rem: 1, tile: new TileData { canPlaceObjects = true }));
        }

        [Test]
        public void OffTheGrid_ThePlacementIsKept()
        {
            // AS3 indexes `space[x][y]` unguarded and would fault, so there is no oracle behaviour to
            // copy; the port's pre-existing reading — a placement parked outside the grid is not a
            // doorway collision — is kept, because "skip" here would silently drop a placement on what
            // is really a data error.
            Assert.IsFalse(RoomPopulator.SkipsAuthoredPlacement(
                noHolesPlace: true, rem: 1, tile: null));
        }

        private static TileData MarkedTile()
        {
            return new TileData { canPlaceObjects = false };
        }
    }
}
