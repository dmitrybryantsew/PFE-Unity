using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.Generation;
using PFE.Tests.Editor.Core;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// <b>A prob room is a real room, not a shell.</b>
    ///
    /// <para>AS3 builds a prob room in two passes. <c>buildProb</c> constructs the <c>Location</c>, tags
    /// it <c>landProb</c>/<c>noMap</c> and drops the <c>doorout</c> (<c>Land.as:771-807</c>). Then
    /// <c>buildProbs</c> walks every location it collected — prob rooms included, because
    /// <c>buildProb</c> pushes them into <c>listLocs</c> (<c>:802</c>) — and calls
    /// <c>setObjects()</c> / <c>preStep()</c> / <c>prob.prepare()</c> on each (<c>:760-766</c>).</para>
    ///
    /// <para><see cref="ProbRoomBuilder.Build"/> ported the first pass only, so every prob room came out
    /// with tiles and a return door and <i>nothing else</i>. Measured in game: walking the plant's
    /// bottom-row exit into <c>rooms_prob/exit_plant</c> produced a room with no <c>work</c>, no
    /// <c>checkpoint</c>, no <c>exit</c> box and neither trigger area — so the room's own level advance
    /// could not be reached and the land could never descend. That is the symptom the owner reported as
    /// *"no door to descend"*.</para>
    ///
    /// <para><b>Why these run offline.</b> <c>RoomTemplate</c> is a <c>ScriptableObject</c>, whose
    /// constructor is an ECall, so a plain <c>CreateInstance</c> dies in a shell — but this fixture only
    /// ever reads fields it wrote itself, which is exactly what <see cref="OfflineScriptableObject"/>
    /// exists for. That stub skips field initializers, so every list <c>GenerateRoom</c> walks is written
    /// by hand in <see cref="MakeExitPlantTemplate"/>.</para>
    /// </summary>
    [TestFixture]
    public class ProbRoomBuilderTests
    {
        private const string ExitPlant = "exit_plant";

        /// <summary>The XP collectible <c>createXpBonuses</c> creates (<c>RoomPopulator</c>, id <c>xp</c>).</summary>
        private const string XpBonusObjectId = "xp";

        private static ObjectSpawnData Obj(string id, string type, int x, int y)
        {
            return new ObjectSpawnData { id = id, type = type, tileCoord = new Vector2Int(x, y) };
        }

        /// <summary>
        /// <c>rooms_prob/exit_plant</c> (<c>RoomsProb.as:4724-4779</c>): a wide 48x25 corridor whose five
        /// placed objects are the two trigger areas, the checkpoint, the exit box and the workbench.
        /// </summary>
        private static RoomTemplate MakeExitPlantTemplate()
        {
            RoomTemplate template = OfflineScriptableObject.Create<RoomTemplate>();

            template.id = ExitPlant;
            template.sourceCollectionId = "rooms_prob";
            template.type = "exit";
            template.tileDataString = string.Empty; // all air, so no tile can refuse a placement
            template.difficultyLevel = 0;
            template.doorQuality = new int[24];

            template.objects = new List<ObjectSpawnData>
            {
                Obj("area", "area", 24, 23),          // refill / upland / off  — the level advance
                Obj("area", "area", 22, 23),          // trig_plantNextLevel -> plantStory1
                Obj("checkpoint", "checkpoint", 29, 15),
                Obj("exit", "box", 38, 15),           // allact='exit' -> gotoNextLevel
                Obj("work", "box", 14, 15),
            };

            // Without one, PlaceReturnDoor has nowhere to stand the doorout (Location.as:797-800 guards
            // on `loc.spawnPoints.length`).
            template.spawnPoints = new List<SpawnPointData>
            {
                new SpawnPointData { tileCoord = new Vector2Int(8, 15), type = SpawnType.Player }
            };

            template.backgroundDecorations = new List<BackgroundDecorationData>();
            template.environment = new RoomEnvironmentData();
            return template;
        }

        /// <summary>
        /// <b>The regression.</b> Every object the room declares must exist once it is built. Without this
        /// the exit box at x=38 — the only thing in the room that advances the land — is absent, and the
        /// room is a dead end.
        /// </summary>
        [Test]
        public void Build_PopulatesTheRoomsOwnObjects()
        {
            RoomTemplate template = MakeExitPlantTemplate();

            RoomInstance room = ProbRoomBuilder.Build(new RoomGenerator(), template, ExitPlant);

            Assert.IsNotNull(room, "positive control: the room was built at all");
            Assert.AreEqual(ExitPlant, room.probId, "and it carries the prob id it was built for");

            foreach (string id in new[] { "work", "checkpoint", "exit", "area" })
            {
                Assert.IsNotNull(
                    room.objects.Find(o => o != null && o.objectId == id),
                    $"'{id}' is declared in the room's XML (RoomsProb.as:4724-4779) but was not spawned. " +
                    "A prob room built without setObjects is a shell: the exit box is how the land " +
                    "descends, and the x=24 area is how landStage is raised.");
            }
        }

        /// <summary>
        /// The <c>doorout</c> is created inside <c>buildProb</c> (<c>Land.as:797-800</c>), i.e. <i>before</i>
        /// <c>setObjects</c>, so it must still be present — and must not be the only object present, which
        /// is what the bug produced.
        /// </summary>
        [Test]
        public void Build_KeepsTheReturnDoorAlongsideTheRoomsObjects()
        {
            RoomTemplate template = MakeExitPlantTemplate();

            RoomInstance room = ProbRoomBuilder.Build(new RoomGenerator(), template, ExitPlant);

            Assert.IsNotNull(
                room.objects.Find(o => o != null && o.objectId == RoomPopulator.ProbReturnObjectId),
                "the doorout is what puts the player back, and setObjects must not displace it");

            Assert.AreEqual(
                template.objects.Count + 1, room.objects.Count,
                "the return door plus every declared object — no more (the room places no units and no " +
                "XP bonuses) and no fewer");
        }

        /// <summary>
        /// <b>A prob room gets no XP bonuses.</b> <c>createXpBonuses</c> is a step of the conf loop, not of
        /// <c>setObjects</c>: <c>_loc10_ = true</c> is set per cell at <c>Land.as:509</c> and
        /// <c>createXpBonuses(5)</c> is called at <c>:672-674</c>. A prob room never enters that loop — it
        /// is built by <c>buildProbs</c> after it — so the orbs would be a port invention, and five free
        /// orbs in a room that is re-entered on every descent is a farm.
        /// </summary>
        [Test]
        public void Build_PlacesNoXpBonuses()
        {
            RoomTemplate template = MakeExitPlantTemplate();

            RoomInstance room = ProbRoomBuilder.Build(new RoomGenerator(), template, ExitPlant);

            Assert.IsNull(
                room.objects.Find(o => o != null && o.objectId == XpBonusObjectId),
                "a prob room is not in the conf loop, so createXpBonuses never runs for it");
        }

        /// <summary>
        /// <b>The control that keeps <see cref="Build_PlacesNoXpBonuses"/> honest.</b> The identical
        /// arrange, with <c>probId</c> left empty, must produce the orbs the guard suppresses — otherwise
        /// that test could be passing because this arrange never places an orb at all.
        /// </summary>
        [Test]
        public void PopulateRoom_OnAnOrdinaryRoom_DoesPlaceXpBonuses()
        {
            RoomTemplate template = MakeExitPlantTemplate();

            RoomInstance room = new RoomGenerator().GenerateRoom(template, Vector3Int.zero);
            Assert.IsTrue(
                string.IsNullOrEmpty(room.probId),
                "positive control: GenerateRoom leaves probId empty, so this is an ordinary room");

            RoomPopulator.PopulateRoom(room, template, room.difficulty);

            Assert.IsNotNull(
                room.objects.Find(o => o != null && o.objectId == XpBonusObjectId),
                "an ordinary room does get XP bonuses, so the guard is a real branch and not a no-op");
        }
    }
}
