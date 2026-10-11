using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Messages;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Scripting;
using UnityEngine;

namespace PFE.Tests.Editor.Map
{
    [TestFixture]
    public class AreaTriggerSystemTests
    {
        private GameObject _staticParentObj;
        private GameObject _physicalParentObj;
        private Transform _staticParent;
        private Transform _physicalParent;

        [SetUp]
        public void SetUp()
        {
            _staticParentObj = new GameObject("StaticParent");
            _physicalParentObj = new GameObject("PhysicalParent");
            _staticParent = _staticParentObj.transform;
            _physicalParent = _physicalParentObj.transform;
        }

        [TearDown]
        public void TearDown()
        {
            if (_staticParentObj != null) Object.DestroyImmediate(_staticParentObj);
            if (_physicalParentObj != null) Object.DestroyImmediate(_physicalParentObj);
        }

        [Test]
        public void EvaluateOverlap_PlayerInsideTrigger_ReturnsTrue()
        {
            var trigger = CreateTrigger("trCont", 640f, 280f, 5, 2);
            Rect playerInside = new Rect(660f, 290f, 30f, 50f);

            bool overlaps = AreaTriggerSystem.EvaluateOverlap(trigger, playerInside);

            Assert.IsTrue(overlaps);
        }

        [Test]
        public void EvaluateOverlap_PlayerOutsideTrigger_ReturnsFalse()
        {
            var trigger = CreateTrigger("trCont", 640f, 280f, 5, 2);
            Rect playerOutside = new Rect(100f, 100f, 30f, 50f);

            bool overlaps = AreaTriggerSystem.EvaluateOverlap(trigger, playerOutside);

            Assert.IsFalse(overlaps);
        }

        [Test]
        public void EvaluateOverlap_InactiveTrigger_ReturnsFalse()
        {
            var trigger = CreateTrigger("trCont", 640f, 280f, 5, 2);
            trigger.isActive = false;
            Rect playerInside = new Rect(660f, 290f, 30f, 50f);

            bool overlaps = AreaTriggerSystem.EvaluateOverlap(trigger, playerInside);

            Assert.IsFalse(overlaps);
        }

        /// <summary>
        /// Only the player may fire an area trigger — a spawned NPC must not.
        ///
        /// <para><b>This pins the removed clause.</b> <c>IsPlayerCollider</c> used to fall through to
        /// <c>other.GetComponentInParent&lt;UnitController&gt;() != null</c>, and <c>RoomUnitSpawner</c>
        /// puts a <c>UnitController</c> subclass on <b>every</b> spawned unit
        /// (<c>RoomUnitSpawner.cs:769</c>), so a wandering enemy stepping on a floor trigger fired
        /// <c>OnPlayerEnter</c> and ran the trigger's script action — including
        /// <c>gotoland</c>/<c>exit</c>, a room/land change driven by an NPC. The
        /// <c>PlayerController</c> clause (<c>PlayerController : UnitController</c>) is the precise
        /// form and already covers the player.</para>
        ///
        /// <para>The negative control carries a real <c>UnitController</c> on purpose: it is the exact
        /// thing the old clause matched, so this test would fail if the clause came back.</para>
        /// </summary>
        [Test]
        public void AreaTriggerPresenter_OnlyThePlayerIsAnActor()
        {
            var playerGo = new GameObject("Player");
            playerGo.transform.SetParent(_staticParent);
            playerGo.tag = "Player";
            var playerCollider = playerGo.AddComponent<BoxCollider2D>();

            Assert.IsTrue(AreaTriggerPresenter.IsPlayerCollider(playerCollider),
                "The player is the one actor that may fire an area trigger.");

            var unitGo = new GameObject("Unit");
            unitGo.transform.SetParent(_staticParent);
            unitGo.AddComponent<BoxCollider2D>();
            unitGo.AddComponent<PFE.Entities.Units.UnitController>();
            var unitCollider = unitGo.GetComponent<BoxCollider2D>();

            Assert.IsFalse(AreaTriggerPresenter.IsPlayerCollider(unitCollider),
                "An NPC standing on a floor area trigger must NOT fire it: the trigger's script can " +
                "change the room or the land, which is the player's business alone.");

            Assert.IsFalse(AreaTriggerPresenter.IsPlayerCollider(null),
                "A null collider is never an actor.");
        }

        [Test]
        public void OnPlayerEnter_WithTutorialMessage_DispatchesPrompt()
        {
            var system = new AreaTriggerSystem();
            var room = new RoomInstance();
            var trigger = CreateTrigger("trDownJump", 640f, 280f, 2, 2);
            trigger.attributes.Add(new MapObjectAttributeData { key = "mess", value = "trDownJump" });
            trigger.attributes.Add(new MapObjectAttributeData { key = "down", value = "1" });

            TutorialPromptMessage? receivedPrompt = null;
            system.OnPromptChanged += msg => receivedPrompt = msg;

            system.OnPlayerEnter(room, trigger);

            Assert.IsNotNull(receivedPrompt);
            Assert.AreEqual("trDownJump", receivedPrompt.Value.MessageKey);
            Assert.IsTrue(receivedPrompt.Value.Show);
            Assert.IsTrue(receivedPrompt.Value.BottomPosition);
            Assert.IsTrue(receivedPrompt.Value.Text.Contains("jump down"));
        }

        [Test]
        public void OnPlayerExit_ClearsPrompt()
        {
            var system = new AreaTriggerSystem();
            var room = new RoomInstance();
            var trigger = CreateTrigger("trDownJump", 640f, 280f, 2, 2);
            trigger.attributes.Add(new MapObjectAttributeData { key = "mess", value = "trDownJump" });

            TutorialPromptMessage? receivedPrompt = null;
            system.OnPromptChanged += msg => receivedPrompt = msg;

            system.OnPlayerExit(room, trigger);

            Assert.IsNotNull(receivedPrompt);
            Assert.AreEqual("trDownJump", receivedPrompt.Value.MessageKey);
            Assert.IsFalse(receivedPrompt.Value.Show);
            Assert.IsEmpty(receivedPrompt.Value.Text);
        }

        [Test]
        public void OnPlayerEnter_ExecutesSignAction_DispatchesObjectiveMarker()
        {
            var system = new AreaTriggerSystem();
            var room = new RoomInstance();

            var wallcab = new ObjectInstance
            {
                objectId = "wallcab",
                uid = "trSign1",
                position = new Vector2(920f, 440f),
                isActive = true
            };
            room.objects.Add(wallcab);

            var trigger = CreateTrigger("trCont", 640f, 280f, 5, 2);
            var script = new MapObjectScriptData();
            script.actions.Add(new MapObjectScriptActionData { act = "sign", targ = "trSign1", val = "1" });
            trigger.scripts.Add(script);
            room.objects.Add(trigger);

            ObjectiveMarkerMessage? receivedMarker = null;
            system.OnMarkerChanged += msg => receivedMarker = msg;

            system.OnPlayerEnter(room, trigger);

            Assert.IsNotNull(receivedMarker);
            Assert.AreEqual("trSign1", receivedMarker.Value.TargetUid);
            Assert.IsTrue(receivedMarker.Value.Show);
            Assert.AreEqual(new Vector2(920f, 440f), receivedMarker.Value.TargetPosition);
        }

        [Test]
        public void OnPlayerEnter_ExecutesOffAction_DeactivatesTargetObject()
        {
            var system = new AreaTriggerSystem();
            var room = new RoomInstance();

            var targetTrigger = CreateTrigger("trCont", 640f, 280f, 5, 2);
            targetTrigger.uid = "trCont";
            targetTrigger.isActive = true;
            room.objects.Add(targetTrigger);

            var trigger = CreateTrigger("other", 100f, 100f, 2, 2);
            var script = new MapObjectScriptData();
            script.actions.Add(new MapObjectScriptActionData { act = "off", targ = "trCont" });
            trigger.scripts.Add(script);
            room.objects.Add(trigger);

            system.OnPlayerEnter(room, trigger);

            Assert.IsFalse(targetTrigger.isActive);
        }

        [Test]
        public void OnObjectInteracted_ExecutesScript_DeactivatesTarget()
        {
            var system = new AreaTriggerSystem();
            var room = new RoomInstance();

            var trCont = CreateTrigger("trCont", 640f, 280f, 5, 2);
            trCont.uid = "trCont";
            trCont.isActive = true;
            room.objects.Add(trCont);

            var wallcab = new ObjectInstance
            {
                objectId = "wallcab",
                uid = "trSign1",
                position = new Vector2(920f, 440f),
                isActive = true
            };
            var script = new MapObjectScriptData();
            script.actions.Add(new MapObjectScriptActionData { act = "off", targ = "trCont" });
            wallcab.scripts.Add(script);
            room.objects.Add(wallcab);

            system.OnObjectInteracted(room, wallcab);

            Assert.IsFalse(trCont.isActive);
        }

        [Test]
        public void OnObjectDestroyed_ExecutesDieScript_DeactivatesTarget()
        {
            var system = new AreaTriggerSystem();
            var room = new RoomInstance();

            var trPunch = CreateTrigger("trPunch", 600f, 120f, 6, 2);
            trPunch.uid = "trPunch";
            trPunch.isActive = true;
            room.objects.Add(trPunch);

            var septum = new ObjectInstance
            {
                objectId = "septum",
                uid = "trSign2",
                position = new Vector2(480f, 120f),
                isActive = true
            };
            var dieScript = new MapObjectScriptData
            {
                eventName = "die"
            };
            dieScript.actions.Add(new MapObjectScriptActionData { act = "off", targ = "trPunch" });
            septum.scripts.Add(dieScript);
            room.objects.Add(septum);

            system.OnObjectDestroyed(room, septum);

            Assert.IsFalse(trPunch.isActive);
        }

        [Test]
        public void RoomObjectVisualManager_CreatesAreaTriggerPresenter_ForAreaObjects()
        {
            RoomInstance room = new RoomInstance();
            MapObjectVisualDefinition visual = ScriptableObject.CreateInstance<MapObjectVisualDefinition>();
            visual.visualId = "visArea";
            visual.pixelSize = new Vector2Int(100, 100);
            visual.pivot = Vector2.zero;
            visual.frames = new Sprite[] { Sprite.Create(new Texture2D(100, 100), new Rect(0, 0, 100, 100), Vector2.zero) };

            ObjectInstance areaObj = new ObjectInstance
            {
                objectId = "area",
                objectType = "area",
                definitionId = "area",
                position = new Vector2(640f, 360f),
                parameters = "w=\"5\" h=\"2\" mess=\"trCont\"",
                runtimeState = new MapObjectRuntimeStateData()
            };
            areaObj.EnsureStructuredData();
            room.objects.Add(areaObj);

            var triggerSystem = new AreaTriggerSystem();
            var manager = new RoomObjectVisualManager(room, _staticParent, _physicalParent, triggerSystem);
            manager.RefreshAll();

            Transform presenter = _staticParent.GetChild(0);
            AreaTriggerPresenter triggerPresenter = presenter.GetComponent<AreaTriggerPresenter>();

            Assert.IsNotNull(triggerPresenter);
            Assert.IsNotNull(triggerPresenter.Collider);
            Assert.IsTrue(triggerPresenter.Collider.isTrigger);
            Assert.AreEqual(Vector2.one, triggerPresenter.Collider.size);
            Assert.AreEqual(new Vector2(0.5f, 0.5f), triggerPresenter.Collider.offset);
            Assert.AreSame(triggerSystem, triggerPresenter.TriggerSystem);

            Object.DestroyImmediate(visual.frames[0].texture);
            Object.DestroyImmediate(visual.frames[0]);
            Object.DestroyImmediate(visual);
        }

        private static ObjectInstance CreateTrigger(string id, float x, float y, int w, int h)
        {
            var obj = new ObjectInstance
            {
                objectId = "area",
                objectType = "area",
                definitionId = "area",
                position = new Vector2(x, y),
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };
            obj.attributes.Add(new MapObjectAttributeData { key = "w", value = w.ToString() });
            obj.attributes.Add(new MapObjectAttributeData { key = "h", value = h.ToString() });
            obj.EnsureStructuredData();
            return obj;
        }
    }
}
