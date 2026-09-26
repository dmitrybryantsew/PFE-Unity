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
    }
}
