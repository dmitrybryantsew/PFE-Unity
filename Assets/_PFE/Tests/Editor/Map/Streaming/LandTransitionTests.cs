using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using PFE.Core.Messages;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Scripting;

namespace PFE.Tests.Editor.Map.Streaming
{
    [TestFixture]
    public class LandTransitionTests
    {
        private GameObject _holderGo;

        [SetUp]
        public void SetUp()
        {
            _holderGo = new GameObject("LandTransitionTests_Holder");
        }

        [TearDown]
        public void TearDown()
        {
            if (_holderGo != null)
            {
                Object.DestroyImmediate(_holderGo);
            }
        }

        [Test]
        public void AreaTriggerSystem_ExecuteAction_GotoLand_TriggersOnGotoLandEvent()
        {
            var system = new AreaTriggerSystem();
            string receivedLand = null;
            system.OnGotoLand += land => receivedLand = land;

            var room = new RoomInstance
            {
                id = "TestRoom",
                width = 48,
                height = 27
            };

            var action = new MapObjectScriptActionData
            {
                act = "gotoland",
                val = "surf",
                targ = "" // gotoland has no target object
            };

            system.ExecuteAction(room, action);

            Assert.That(receivedLand, Is.EqualTo("surf"), "gotoland action must fire OnGotoLand event with destination land");
        }

        [Test]
        public void AreaTriggerPresenter_ResolveAreaType_GotoLandScript_ResolvesToTeleport()
        {
            var trigger = new ObjectInstance
            {
                objectId = "area",
                objectType = "area",
                position = new Vector2(4600f, 1900f),
                scripts = new List<MapObjectScriptData>
                {
                    new MapObjectScriptData
                    {
                        eventName = "",
                        actions = new List<MapObjectScriptActionData>
                        {
                            new MapObjectScriptActionData { act = "gotoland", val = "surf" },
                            new MapObjectScriptActionData { act = "passed" }
                        }
                    }
                }
            };

            AreaTriggerType resolved = AreaTriggerPresenter.ResolveAreaType(trigger);

            Assert.That(resolved, Is.EqualTo(AreaTriggerType.Teleport), "Triggers with gotoland action must resolve to Teleport (Emerald Green)");
        }

        [Test]
        public void AreaTriggerPresenter_ColorsByType_TeleportIsGreen()
        {
            Color teleFill = AreaTriggerPresenter.GetFillColor(AreaTriggerType.Teleport);
            Color teleWire = AreaTriggerPresenter.GetWireColor(AreaTriggerType.Teleport);

            // Emerald Green: Green channel should be significantly higher than red
            Assert.That(teleFill.g, Is.GreaterThan(teleFill.r));
            Assert.That(teleWire.g, Is.GreaterThan(teleWire.r));
        }

        [Test]
        public void RoomVisualController_OnGotoLand_ForwardsFromAreaTriggerSystem()
        {
            var go = new GameObject("TestRVC_GotoLand");
            go.transform.SetParent(_holderGo.transform);
            var rvc = go.AddComponent<RoomVisualController>();

            string landDispatched = null;
            rvc.OnGotoLand += land => landDispatched = land;

            var room = new RoomInstance
            {
                id = "TestRoom",
                width = 48,
                height = 27
            };
            room.InitializeTiles();

            // Initialize wires up the trigger system before it touches the render assets, so the
            // system survives the early-out below. The null database is deliberate: this test only
            // cares about the OnGotoLand forwarding chain, not about rendering the room.
            LogAssert.Expect(LogType.Error, "[RoomVisualController] Cannot initialize with null asset database!");
            rvc.Initialize(room, null);

            Assert.That(rvc.AreaTriggerSystem, Is.Not.Null);

            var action = new MapObjectScriptActionData
            {
                act = "gotoland",
                val = "surf"
            };

            rvc.AreaTriggerSystem.ExecuteAction(room, action);

            Assert.That(landDispatched, Is.EqualTo("surf"), "RoomVisualController must forward OnGotoLand from its AreaTriggerSystem");
        }

        // ==========================================================
        //  LAND ID -> ROOM COLLECTION
        //
        //  A gotoland action carries a land *id* ("surf", "random_mbase"), but rooms are stored under the
        //  land's *file* ("rooms_surf", "rooms_mbase") — that is the key Rooms.as:2794-2822 registers each
        //  land under and the key AS3LandDefaultsDatabase merges inherited options by. The id cannot be
        //  turned into the file mechanically: random_mbase is rooms_mbase, and stable_pi_surf is rooms_pis.
        //  The table is transcribed from the <land> entries in GameData.as, so a typo in it would be
        //  silent — hence these tests.
        // ==========================================================

        private static string ResolveCollectionName(string landId)
        {
            var method = typeof(MapBridge).GetMethod(
                "ResolveCollectionName",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            Assert.That(method, Is.Not.Null, "MapBridge.ResolveCollectionName should exist as a private static method.");
            return (string)method.Invoke(null, new object[] { landId });
        }

        [TestCase("rbl", "rooms_rbl", TestName = "ResolveCollectionName_Camp_IsTheRblLand")]
        [TestCase("surf", "rooms_surf", TestName = "ResolveCollectionName_Surf")]
        [TestCase("covert", "rooms_covert", TestName = "ResolveCollectionName_Covert")]
        [TestCase("src", "rooms_src", TestName = "ResolveCollectionName_Src")]
        [TestCase("random_mbase", "rooms_mbase", TestName = "ResolveCollectionName_RandomMbaseIsNotRoomsRandomMbase")]
        [TestCase("bunker", "rooms_mbase", TestName = "ResolveCollectionName_BunkerAliasesMbase")]
        [TestCase("stable_pi_surf", "rooms_pis", TestName = "ResolveCollectionName_StablePiSurfIsRoomsPis")]
        [TestCase("stable_pi", "rooms_pi", TestName = "ResolveCollectionName_StablePi")]
        [TestCase("thunder", "rooms_thunder", TestName = "ResolveCollectionName_Thunder")]
        public void ResolveCollectionName_KnownLandIds_MapToTheirOracleCollectionFile(string landId, string expected)
        {
            Assert.That(ResolveCollectionName(landId), Is.EqualTo(expected),
                $"<land id='{landId}'> in GameData.as points at the collection '{expected}'.");
        }

        [Test]
        public void ResolveCollectionName_UnknownLandId_DoesNotInventACollection()
        {
            // The fallback used to capitalise the id, so an unrecognised land produced "Nio", "Covert" or
            // "Src" — collections that never existed — and the transition failed with a bare "no templates
            // found" that named the invented string. "camp" is the trap: the camp is land 'rbl'.
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Unknown land id"));

            Assert.That(ResolveCollectionName("camp"), Is.EqualTo(string.Empty),
                "There is no land called 'camp' in the oracle; guessing a collection for it hides the mistake.");
        }

        [Test]
        public void ResolveCollectionName_CollectionIdPassedThrough_IsAccepted()
        {
            // A value that has already been resolved arrives in collection form, because that is what
            // RoomTemplate.sourceCollectionId stores.
            Assert.That(ResolveCollectionName("rooms_nio"), Is.EqualTo("rooms_nio"));
        }
    }
}
