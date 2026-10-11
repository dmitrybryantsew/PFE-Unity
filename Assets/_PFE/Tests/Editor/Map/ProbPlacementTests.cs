using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// The two placements a prob door subsystem needs: the <c>doorprob</c>/<c>doorboss</c> that stands in a
    /// normal room, and the <c>doorout</c> that stands in the prob room.
    ///
    /// <para><b>Why this is not in <c>RoomPopulatorTests</c>.</b> That fixture is editor-only: every case
    /// there builds a <c>ScriptableObject</c> (<c>RoomTemplate</c>, <c>MapObjectDefinition</c>) and fails
    /// with <c>ECall methods must be packaged into a system module</c> in a plain shell. Both placements
    /// here need only a <c>RoomInstance</c>, which is a plain class, so they run anywhere. Putting them
    /// beside the ones that cannot run would have hidden them.</para>
    /// </summary>
    [TestFixture]
    public class ProbPlacementTests
    {
        private static RoomInstance MakeRoom(params Vector2Int[] spawnTiles)
        {
            var room = new RoomInstance
            {
                id = "prob_placement_fixture",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0,
            };
            room.InitializeTiles();

            foreach (Vector2Int tile in spawnTiles)
            {
                room.spawnPoints.Add(new SpawnPoint { tileCoord = tile, type = SpawnType.Player });
            }

            return room;
        }

        // =====================================================================
        //  createDoorProb  (Location.as:2124-2135)
        // =====================================================================

        /// <summary>
        /// <b>A room with no spawn point gets no prob door.</b> The oracle returns <c>false</c>
        /// (<c>Location.as:2134</c>) and <c>newRandomProb</c> then places nothing and does not build the
        /// prob room either (<c>Land.as:857-860</c>). Inventing a position would put a door in a room the
        /// oracle would have left alone — and, worse, would make the prob room reachable from nowhere.
        /// </summary>
        [Test]
        public void PlaceProbDoor_WithNoSpawnPoint_PlacesNothing()
        {
            RoomInstance room = MakeRoom();

            ObjectInstance placed = RoomPopulator.PlaceProbDoor(room, "doorboss", "bossraider1");

            Assert.IsNull(placed, "no spawn point means the oracle returns false");
            Assert.AreEqual(0, room.objects.Count, "and nothing at all is added to the room");
        }

        /// <summary>
        /// The placed door carries exactly the attributes <c>createDoorProb</c> writes:
        /// <c>prob</c>, <c>nazv</c>, <c>time='20'</c>, <c>inter='8'</c>.
        ///
        /// <para><b><c>time='20'</c> overrides the definition's <c>time='30'</c></b>
        /// (<c>AllData.as:5018-5019</c>). Asserting the instance value is the point: a reader that trusted
        /// the definition would get 30 and the door would behave differently.</para>
        /// </summary>
        [Test]
        public void PlaceProbDoor_CarriesTheOraclesInstanceAttributes()
        {
            RoomInstance room = MakeRoom(new Vector2Int(10, 10));

            ObjectInstance placed = RoomPopulator.PlaceProbDoor(room, "doorboss", "bossraider1");

            Assert.IsNotNull(placed);
            Assert.AreEqual("doorboss", placed.objectId);
            Assert.AreEqual("doorboss", placed.definitionId);
            Assert.AreEqual("box", placed.objectType);

            Assert.AreEqual("bossraider1", placed.GetAttribute("prob"),
                "`prob` is what Interact.allAct reads first (Interact.as:1558) to enter the prob room.");
            Assert.AreEqual("20", placed.GetAttribute("time"),
                "the instance time is 20, not the definition's 30.");
            Assert.AreEqual("8", placed.GetAttribute("inter"));
        }

        /// <summary>
        /// <c>nazv</c> is <c>Res.txt("m", pid)</c> in AS3. The port has no localisation table, so the raw
        /// prob id is stored instead — and a caller may override it.
        /// </summary>
        [Test]
        public void PlaceProbDoor_DisplayNameDefaultsToTheProbId_AndCanBeOverridden()
        {
            RoomInstance room = MakeRoom(new Vector2Int(3, 4));

            ObjectInstance defaulted = RoomPopulator.PlaceProbDoor(room, "doorprob", "buttons1");
            Assert.AreEqual("buttons1", defaulted.GetAttribute("nazv"),
                "no display name supplied, so the prob id stands in for Res.txt(\"m\", pid)");

            ObjectInstance named = RoomPopulator.PlaceProbDoor(room, "doorprob", "buttons1", "Trial Door");
            Assert.AreEqual("Trial Door", named.GetAttribute("nazv"));
        }

        /// <summary>
        /// The door stands where AS3's <c>createObj</c> puts a box: the <b>bottom-centre</b> of the spawn
        /// point's tile, not its top-left corner.
        ///
        /// <para><c>Location.as:2005</c> — <c>((nx + 0.5 * size) * Tile.tileX, (ny + 1) * Tile.tileY - 1)</c>,
        /// in Flash's top-down pixels. This port stores room-local pixels bottom-up, so the row is mirrored
        /// and the trailing <c>-1</c> becomes <c>+1</c>: exactly what
        /// <c>RoomPopulator.ResolveLegacyBottomAnchorPixels</c> computes for an imported object. A synthetic
        /// placement has to land on that same anchor, which is the point of the assertion.</para>
        ///
        /// <para>Tile (7, 9) of a 25-row room with <c>size='2'</c> therefore anchors at
        /// <c>(7 + 1) * 40 = 320</c> across and <c>(25 - 9 - 1) * 40 + 1 = 601</c> up. The old expectation,
        /// <c>TileToPixel(7, 9) = (280, 360)</c>, is the tile's top-left corner in Flash's top-down pixels —
        /// half a tile to the left and six tiles too high.</para>
        /// </summary>
        [Test]
        public void PlaceProbDoor_StandsOnTheSpawnPointsBottomCentreAnchor()
        {
            var tile = new Vector2Int(7, 9);
            RoomInstance room = MakeRoom(tile);

            ObjectInstance placed = RoomPopulator.PlaceProbDoor(room, "doorprob", "buttons1");

            Assert.AreEqual(new Vector2(320f, 601f), placed.position,
                "AS3 createObj anchors a box at the bottom-centre of its tile");
            Assert.AreNotEqual(WorldCoordinates.TileToPixel(tile), placed.position,
                "control: the tile's top-left corner is the wrong anchor");
        }

        /// <summary>
        /// The checkpoint uses the same anchor as the prob door — <c>createObj</c>'s <c>tip == "checkpoint"</c>
        /// branch repeats the box formula verbatim (<c>Location.as:2040</c>), and <c>createCheck</c> reaches
        /// it through the same <c>spawnPoints</c> entry (<c>:2091-2094</c>).
        /// </summary>
        [Test]
        public void PlaceCheckpointMarker_StandsOnTheSpawnPointsBottomCentreAnchor()
        {
            var tile = new Vector2Int(4, 4);
            RoomInstance room = MakeRoom(tile);

            ObjectInstance placed = RoomPopulator.PlaceCheckpointMarker(room, isBegin: true);

            Assert.AreEqual(new Vector2(200f, 801f), placed.position,
                "(4 + 1) * 40 = 200 across, (25 - 4 - 1) * 40 + 1 = 801 up");
        }

        /// <summary>
        /// The prob room's return door, same anchor again — <c>Land.buildProb</c> passes
        /// <c>loc.spawnPoints[0]</c> through <c>createObj(…, "box", …)</c> (<c>Land.as:797-800</c>).
        /// </summary>
        [Test]
        public void PlaceReturnDoor_StandsOnTheSpawnPointsBottomCentreAnchor()
        {
            var tile = new Vector2Int(1, 2);
            RoomInstance room = MakeRoom(tile);

            ObjectInstance placed = RoomPopulator.PlaceReturnDoor(room);

            Assert.AreEqual(new Vector2(80f, 881f), placed.position,
                "(1 + 1) * 40 = 80 across, (25 - 2 - 1) * 40 + 1 = 881 up");
        }

        /// <summary>
        /// Every synthetic placement carries the definition's footprint, because the anchor's X term is
        /// half that width and the static placements have no <c>MapObjectCatalog</c> to read it from.
        ///
        /// <para><b>Both keys are asserted, and that is the point.</b> The three readers —
        /// <c>ResolvePlacementSizeTiles</c>, <c>RoomInstance.GetApproximatePixelSize</c> and
        /// <c>DoorPropPresenter.GetCoveredTileRange</c> — each return as soon as <i>either</i> attribute
        /// parses, so writing <c>size</c> alone would leave the height at its 1-tile default and describe a
        /// 2x1 object where AS3 has 2x3 (<c>AllData.as:5007</c>).</para>
        /// </summary>
        [Test]
        public void SyntheticPlacements_CarryTheDefinitionsFootprint()
        {
            RoomInstance room = MakeRoom(new Vector2Int(3, 3));

            ObjectInstance checkpoint = RoomPopulator.PlaceCheckpointMarker(room, isBegin: true);
            ObjectInstance probDoor = RoomPopulator.PlaceProbDoor(room, "doorprob", "buttons1");
            ObjectInstance exit = RoomPopulator.PlaceExit(room, "exit_plant");

            foreach (ObjectInstance placed in new[] { checkpoint, probDoor, exit })
            {
                Assert.AreEqual("2", placed.GetAttribute("size"),
                    $"{placed.objectId} is size='2' in AllData.as");
                Assert.AreEqual("3", placed.GetAttribute("wid"),
                    $"{placed.objectId} is wid='3' in AllData.as");
            }
        }

        /// <summary>
        /// Placing a prob door appends; it must not clear the room's other objects. A room that already has
        /// an exit and a checkpoint keeps them.
        /// </summary>
        [Test]
        public void PlaceProbDoor_AppendsToExistingObjects()
        {
            RoomInstance room = MakeRoom(new Vector2Int(5, 5));

            RoomPopulator.PlaceExit(room, "exit_plant");
            RoomPopulator.PlaceCheckpointMarker(room, isBegin: true);
            int before = room.objects.Count;
            Assert.AreEqual(2, before, "positive control: two objects placed first");

            RoomPopulator.PlaceProbDoor(room, "doorboss", "bossraider1");

            Assert.AreEqual(before + 1, room.objects.Count, "the door is added, not swapped in");
            Assert.AreEqual("doorboss", room.objects[room.objects.Count - 1].objectId);
            Assert.AreEqual("exit", room.objects[0].objectId, "the earlier exit survives");
        }

        // =====================================================================
        //  createCheck  (Location.as:2088-2111)
        // =====================================================================

        /// <summary>
        /// <b>The begin checkpoint is the plain, unlocked <c>checkpoint</c>.</b> AS3 appends a random
        /// <c>1..5</c> to the id only when the checkpoint is <i>not</i> the begin one, the land is random
        /// and a 50% roll passes (<c>Location.as:2095-2098</c>:
        /// <c>if(!param1 &amp;&amp; this.land.rnd &amp;&amp; Math.random() &lt; 0.5)</c>). The suffixed ids are
        /// the locked/mined variants — <c>AllData.as:5008</c> gives <c>checkpoint1</c> <c>lock='1.4'</c>
        /// ("КТ с замком") and <c>checkpoint4</c> <c>mine='1'</c> — so placing the begin checkpoint as
        /// <c>checkpoint1</c> put the locked variant at the one checkpoint the oracle guarantees is
        /// unlocked, and it drew with a lock on it.
        /// </summary>
        [Test]
        public void PlaceCheckpointMarker_BeginMarkerIsThePlainCheckpoint()
        {
            RoomInstance room = MakeRoom(new Vector2Int(4, 4));

            ObjectInstance begin = RoomPopulator.PlaceCheckpointMarker(room, isBegin: true);

            Assert.IsNotNull(begin);
            Assert.AreEqual("checkpoint", begin.objectId, "the begin checkpoint is the unlocked variant");
        }

        /// <summary>
        /// Control for the case above: the id is not simply hard-wired to whatever the last change wanted.
        /// A non-begin checkpoint placed on an <b>authored</b> land is also plain, because the roll needs
        /// <c>land.rnd</c> and no caller supplies it here — see
        /// <see cref="PlaceCheckpointMarker_RollsTheLockedVariantWhenTheLandIsRandom"/> for the case where
        /// it does.
        /// </summary>
        [Test]
        public void PlaceCheckpointMarker_NonBeginMarkerOnAnAuthoredLandIsPlain()
        {
            RoomInstance room = MakeRoom(new Vector2Int(4, 4));

            ObjectInstance nonBegin = RoomPopulator.PlaceCheckpointMarker(room, isBegin: false);

            Assert.AreEqual("checkpoint", nonBegin.objectId,
                "an authored land (land.rnd false) never rolls; it must not place a locked one by accident");
        }

        // ---------------------------------------------------------------------
        //  The locked-variant roll  (Location.as:2094-2098)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Draws <c>NextInt</c> from a fixed queue so a test can state the two rolls exactly.
        ///
        /// <para><b>Queued rather than fixed, because the roll is two draws.</b> A double that always
        /// returned the same number could not distinguish "the coin passed" from "the variant was 3" — and
        /// the oracle's second draw is conditional on the first, so a test has to be able to say both.</para>
        /// </summary>
        private sealed class QueuedRng : PFE.Core.Rng.IRngService
        {
            private readonly Queue<int> _draws;

            public QueuedRng(params int[] draws) => _draws = new Queue<int>(draws);

            public int Remaining => _draws.Count;

            public int NextInt(int maxExclusive) => _draws.Count > 0 ? _draws.Dequeue() : 0;

            public bool Chance(float probability) => NextInt(2) == 0;
            public float NextFloat() => 0f;
            public uint NextUInt() => 0u;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public void Shuffle<T>(IList<T> list) { }
            public PFE.Core.Rng.IRngService GetStream(PFE.Core.Rng.RngStream stream, int? salt = null) => this;
        }

        /// <summary>
        /// <c>if(!param1 &amp;&amp; this.land.rnd &amp;&amp; Math.random() &lt; 0.5)</c> — the roll runs only on a
        /// random land, and the begin checkpoint short-circuits it.
        /// </summary>
        [Test]
        public void PlaceCheckpointMarker_RollsTheLockedVariantWhenTheLandIsRandom()
        {
            RoomInstance room = MakeRoom(new Vector2Int(4, 4));

            // First draw is the coin (0 of 2 -> passed), second is the variant (2 of 5 -> 3).
            var rng = new QueuedRng(0, 2);
            ObjectInstance placed = RoomPopulator.PlaceCheckpointMarker(
                room, isBegin: false, landIsRandom: true, rng: rng);

            Assert.AreEqual("checkpoint3", placed.objectId,
                "a non-begin checkpoint on a random land whose 50% roll passed takes a 1..5 suffix");
            Assert.AreEqual("checkpoint3", placed.definitionId,
                "the definition id must follow the object id, or the lock attribute is read off the wrong row");
        }

        /// <summary>
        /// The begin checkpoint is <b>never</b> a variant, even on a random land with the coin passed —
        /// and, because AS3 short-circuits before either draw, <b>no random value is consumed</b>.
        ///
        /// <para><b>Why the draw count is asserted and not just the id.</b> The stream is shared with the
        /// rest of the world build, so a version that drew first and discarded the result would place the
        /// same id and silently shift every later placement in the build.</para>
        /// </summary>
        [Test]
        public void PlaceCheckpointMarker_BeginMarkerSkipsTheRollAndConsumesNoDraw()
        {
            RoomInstance room = MakeRoom(new Vector2Int(4, 4));

            var rng = new QueuedRng(0, 2);
            ObjectInstance begin = RoomPopulator.PlaceCheckpointMarker(
                room, isBegin: true, landIsRandom: true, rng: rng);

            Assert.AreEqual("checkpoint", begin.objectId, "param1 short-circuits the roll entirely");
            Assert.AreEqual(2, rng.Remaining, "the oracle draws nothing at all for the begin checkpoint");
        }

        /// <summary>
        /// The second draw is <b>conditional on the first</b>: a failed coin consumes one value, not two.
        /// AS3 reaches <c>Math.floor(Math.random() * 5 + 1)</c> only inside the <c>if</c>.
        /// </summary>
        [Test]
        public void PlaceCheckpointMarker_FailedRollConsumesOnlyTheCoin()
        {
            RoomInstance room = MakeRoom(new Vector2Int(4, 4));

            var rng = new QueuedRng(1, 2); // coin fails (1 of 2 is not 0)
            ObjectInstance placed = RoomPopulator.PlaceCheckpointMarker(
                room, isBegin: false, landIsRandom: true, rng: rng);

            Assert.AreEqual("checkpoint", placed.objectId, "a failed 50% roll leaves the plain id");
            Assert.AreEqual(1, rng.Remaining, "the variant draw is inside the `if`, so it never happens");
        }

        /// <summary>
        /// No stream, no roll — the parameter is optional so every existing caller keeps the previous
        /// behaviour, and a null stream must not throw out of a world build.
        /// </summary>
        [Test]
        public void PlaceCheckpointMarker_WithoutAStreamPlacesThePlainCheckpoint()
        {
            RoomInstance room = MakeRoom(new Vector2Int(4, 4));

            ObjectInstance placed = RoomPopulator.PlaceCheckpointMarker(
                room, isBegin: false, landIsRandom: true, rng: null);

            Assert.AreEqual("checkpoint", placed.objectId);
        }

        /// <summary>
        /// <b><c>land.rnd</c> gates the roll on its own, not merely by being the default.</b> A stream is
        /// present and would happily answer, so an authored land must still refuse <i>and</i> leave the
        /// stream untouched.
        ///
        /// <para>Without this case the <c>landIsRandom</c> term is untestable: every other case either
        /// passes no stream (so the <c>rng == null</c> term alone explains the plain id) or passes a random
        /// land (so the term is never exercised). Deleting <c>!landIsRandom</c> from the guard would leave
        /// the whole fixture green.</para>
        /// </summary>
        [Test]
        public void PlaceCheckpointMarker_AuthoredLandRefusesTheRollEvenWithAStream()
        {
            RoomInstance room = MakeRoom(new Vector2Int(4, 4));

            var rng = new QueuedRng(0, 2);
            ObjectInstance placed = RoomPopulator.PlaceCheckpointMarker(
                room, isBegin: false, landIsRandom: false, rng: rng);

            Assert.AreEqual("checkpoint", placed.objectId, "an authored land never rolls a variant");
            Assert.AreEqual(2, rng.Remaining, "and it consumes no random value either");
        }

        /// <summary>
        /// The begin checkpoint carries the port-only <c>beg</c> attribute, which is how the presenter —
        /// a different object in a different layer — learns what AS3 keeps in <c>createCheck</c>'s
        /// <c>param1</c>.
        ///
        /// <para><b>This replaced a derivation that had gone stale.</b> The presenter used to read
        /// <c>objectId == "checkpoint1"</c>, written when <c>checkpoint1</c> was the begin checkpoint.
        /// Correcting that made the test unsatisfiable, so <c>isBegin</c> became permanently false with
        /// nothing failing. Asserting the carrier is what keeps the two ends tied together.</para>
        /// </summary>
        [Test]
        public void PlaceCheckpointMarker_BeginMarkerCarriesTheBeginAttribute()
        {
            RoomInstance room = MakeRoom(new Vector2Int(4, 4));

            ObjectInstance begin = RoomPopulator.PlaceCheckpointMarker(room, isBegin: true);
            ObjectInstance nonBegin = RoomPopulator.PlaceCheckpointMarker(room, isBegin: false);

            Assert.AreEqual("1", begin.GetAttribute(RoomPopulator.BeginCheckpointAttribute),
                "the begin checkpoint is marked, so the presenter can tell");
            Assert.AreEqual(string.Empty, nonBegin.GetAttribute(RoomPopulator.BeginCheckpointAttribute),
                "and a non-begin checkpoint is not");
        }

        /// <summary>
        /// Null inputs are refused rather than throwing. A missing room is a caller bug, but a door that
        /// reports "nothing placed" is recoverable where an exception out of a world build is not.
        /// </summary>
        [Test]
        public void PlaceProbDoor_NullInputs_AreRefused()
        {
            RoomInstance room = MakeRoom(new Vector2Int(1, 1));

            Assert.IsNull(RoomPopulator.PlaceProbDoor(null, "doorprob", "buttons1"));
            Assert.IsNull(RoomPopulator.PlaceProbDoor(room, null, "buttons1"));
            Assert.IsNull(RoomPopulator.PlaceProbDoor(room, "", "buttons1"));
            Assert.AreEqual(0, room.objects.Count);
        }

        // =====================================================================
        //  doorout  (Land.as:797-800)
        // =====================================================================

        /// <summary>
        /// The prob room's return door is a <c>doorout</c> carrying <c>uid='begin'</c> and an <b>empty</b>
        /// <c>prob</c>.
        ///
        /// <para><b>Why the empty attribute matters.</b> <c>Interact.as:386-389</c> only assigns its
        /// <c>prob</c> field when <c>@prob.length()</c> is non-zero, so an empty <c>prob</c> does not mean
        /// "enter the room named empty" — the entry branch is skipped and the object's own
        /// <c>allact='probreturn'</c> returns the player instead. A reader that tested "has a prob
        /// attribute" rather than "has a non-empty one" would make the return door re-enter the room the
        /// player is already standing in: an infinite loop with no error.</para>
        /// </summary>
        [Test]
        public void PlaceReturnDoor_CarriesAnEmptyProbAndTheBeginUid()
        {
            RoomInstance room = MakeRoom(new Vector2Int(4, 4));

            ObjectInstance placed = RoomPopulator.PlaceReturnDoor(room);

            Assert.IsNotNull(placed);
            Assert.AreEqual("doorout", placed.objectId);
            Assert.AreEqual("doorout", placed.definitionId);
            Assert.AreEqual("box", placed.objectType);
            Assert.AreEqual(RoomPopulator.ProbReturnBeginUid, placed.uid);
            Assert.AreEqual("begin", placed.uid,
                "Probation.doorsOnOff keys on uid == 'begin' to keep this door visible while the room " +
                "seals its other exits (Probation.as:299-308).");
            Assert.AreEqual("", placed.GetAttribute("prob"), "the prob attribute is present but empty");
        }

        /// <summary>
        /// A prob room with no spawn point is built without a return door rather than with one at an
        /// invented position — AS3 guards the whole call on <c>if(loc.spawnPoints.length)</c>.
        /// </summary>
        [Test]
        public void PlaceReturnDoor_WithNoSpawnPoint_PlacesNothing()
        {
            RoomInstance room = MakeRoom();

            Assert.IsNull(RoomPopulator.PlaceReturnDoor(room));
            Assert.AreEqual(0, room.objects.Count);
        }

        [Test]
        public void PlaceReturnDoor_NullRoom_IsRefused()
        {
            Assert.IsNull(RoomPopulator.PlaceReturnDoor(null));
        }
    }
}
