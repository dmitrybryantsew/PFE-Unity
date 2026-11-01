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

        /// <summary>
        /// The object half of the authored-script join: <c>&lt;obj id="term" scr="stableStory"/&gt;</c>
        /// names a <c>&lt;scr id&gt;</c> in the campaign data instead of carrying an inline
        /// <c>&lt;scr&gt;</c> block (<c>Box.as:341-343</c>), so the importer leaves
        /// <c>scripts</c> empty and the action list lives only in the catalogue. Nothing read the
        /// attribute, so the raiders stable terminal — whose <c>gotoland random_stable</c> is the only
        /// way into Stable 88 — and the garages sprite-bot wreck (the step that unlocks Manehattan) were
        /// both inert.
        ///
        /// <para>The catalogue is a real <see cref="PFE.Data.Definitions.Campaign.CampaignCatalog"/>
        /// because that is what <see cref="ScriptSystem"/> wraps; the point of the fixture is the call
        /// site in <c>OnObjectInteracted</c>, not the lookup, which
        /// <c>ScriptCatalogHost</c> already covers.</para>
        /// </summary>
        [Test]
        public void OnObjectInteracted_RunsTheAuthoredScriptNamedByScr()
        {
            var authored = ScriptableObject.CreateInstance<PFE.Data.Definitions.Campaign.CampaignScriptDefinition>();
            authored.scriptId = "authoredRefTest";
            authored.actions.Add(new PFE.Data.Definitions.Campaign.CampaignScriptAction
            {
                action = "off",
                target = "trCont",
            });

            var catalog = ScriptableObject.CreateInstance<PFE.Data.Definitions.Campaign.CampaignCatalog>();
            ((List<PFE.Data.Definitions.Campaign.CampaignScriptDefinition>)catalog.AllScripts).Add(authored);
            catalog.Initialize();

            // A report sink, so an unresolvable id fails as a missing assertion rather than a log line.
            var scriptSystem = new ScriptSystem(catalog, null, null, null, _ => { });
            scriptSystem.Initialize();

            var system = new AreaTriggerSystem();
            try
            {
                var room = new RoomInstance();

                var trCont = CreateTrigger("trCont", 640f, 280f, 5, 2);
                trCont.uid = "trCont";
                trCont.isActive = true;
                room.objects.Add(trCont);

                var term = new ObjectInstance
                {
                    objectId = "term",
                    objectType = "box",
                    uid = "",
                    position = new Vector2(920f, 440f),
                    isActive = true
                };
                term.attributes.Add(new MapObjectAttributeData { key = "scr", value = "authoredRefTest" });
                room.objects.Add(term);

                Assert.AreEqual(0, term.scripts.Count,
                    "precondition: `scr=` is an attribute, so the importer produced no script list");

                system.OnObjectInteracted(room, term);

                Assert.IsFalse(trCont.isActive,
                    "the authored script the `scr` attribute names must run on the press");
            }
            finally
            {
                system.Dispose();
                scriptSystem.Dispose();
                Object.DestroyImmediate(catalog);
                Object.DestroyImmediate(authored);
            }
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

        // ── A UNIT's death script — AS3 `scrDie` (Unit.as:1566-1582 → :4392 → :4483) ──────────────

        /// <summary>
        /// The attribute half: <c>scr="stableStory"</c> on a unit names a <c>&lt;scr id&gt;</c> in the
        /// campaign data instead of carrying an inline block (<c>Unit.as:1580-1582</c>).
        ///
        /// <para>Six shipped placements are that shape and one of them is a cutscene that leaves the
        /// room (<c>megabossDie</c>'s <c>goto 1 0</c>), so an unimplemented latch is not a cosmetic
        /// gap — the thunderhead fight simply never ends.</para>
        /// </summary>
        [Test]
        public void OnUnitDestroyed_RunsTheAuthoredScriptNamedByScr()
        {
            PFE.Data.Definitions.Campaign.CampaignCatalog catalog = CatalogueWithOneOff("unitDeathRefTest", "trCont");
            var scriptSystem = new ScriptSystem(catalog, null, null, null, _ => { });
            scriptSystem.Initialize();

            var system = new AreaTriggerSystem();
            try
            {
                var room = new RoomInstance();

                var trCont = CreateTrigger("trCont", 640f, 280f, 5, 2);
                trCont.uid = "trCont";
                trCont.isActive = true;
                room.objects.Add(trCont);

                var boss = new UnitInstance
                {
                    unitId = "megaboss",
                    uid = "megaboss",
                    code = "CMYzTeGVdTOIe20P",
                };
                boss.attributes.Add(new MapObjectAttributeData { key = "scr", value = "unitDeathRefTest" });
                room.units.Add(boss);

                Assert.AreEqual(0, boss.scripts.Count,
                    "precondition: `scr=` is an attribute, so the importer produced no script list");

                system.OnUnitDestroyed(room, boss);

                Assert.IsFalse(trCont.isActive,
                    "the authored script the `scr` attribute names must run when the unit dies");
            }
            finally
            {
                system.Dispose();
                scriptSystem.Dispose();
                DestroyCatalogue(catalog);
            }
        }

        /// <summary>
        /// The inline half, and the rule that makes it <b>different from a box</b>: on a unit a
        /// <c>&lt;scr&gt;</c> with <b>no</b> <c>eve</c> is the death script
        /// (<c>if(_loc5_.eve == "die" || _loc5_.eve == null)</c>, <c>Unit.as:1570</c>), whereas the same
        /// bare block on a <c>Box</c> is the <i>click</i> script (<c>Box.as:341</c>).
        ///
        /// <para>Eleven shipped placements rely on it — <c>mbase/конец</c>'s <c>allact open</c>,
        /// <c>prob/sttech</c>'s <c>unlock</c>, <c>begin/room_3_3</c> — so a port that only matched
        /// <c>eve="die"</c> would leave most of the inline death scripts inert while looking
        /// complete.</para>
        /// </summary>
        [Test]
        public void OnUnitDestroyed_RunsABareInlineScript_AsADeathScript()
        {
            var system = new AreaTriggerSystem();
            var room = new RoomInstance();

            var trPunch = CreateTrigger("trPunch", 600f, 120f, 6, 2);
            trPunch.uid = "trPunch";
            trPunch.isActive = true;
            room.objects.Add(trPunch);

            var platformOperator = new UnitInstance { unitId = "trExpl1", uid = "trExpl1" };
            var bare = new MapObjectScriptData();   // no eventName — AS3's `eve == null`
            bare.actions.Add(new MapObjectScriptActionData { act = "off", targ = "trPunch" });
            platformOperator.scripts.Add(bare);
            room.units.Add(platformOperator);

            Assert.IsTrue(string.IsNullOrEmpty(bare.eventName), "precondition: this block has no `eve`");

            system.OnUnitDestroyed(room, platformOperator);

            Assert.IsFalse(trPunch.isActive,
                "`Unit.as:1570` treats a `<scr>` with no `eve` as the death script on a UNIT — the " +
                "`rooms_workshop/room_0_2` platform operators are exactly this shape.");
        }

        /// <summary>
        /// The precedence, and the reason this is a `return` rather than an extra call: AS3 assigns
        /// <c>scrDie</c> from <c>xml.@scr</c> <i>after</i> the <c>&lt;scr&gt;</c>-child loop
        /// (<c>Unit.as:1566-1575</c> then <c>:1580-1582</c>), so the reference <b>replaces</b> the
        /// inline script and only one of them runs.
        ///
        /// <para><c>mbase/конец.зад</c>'s ranger authors both — an inline <c>&lt;scr allact open 2/&gt;</c>
        /// and <c>scr="rangerStoryOk"</c>. Running both would open the room's gates a second time and
        /// start the cold-death scene, which is a divergence no shipped test would name.</para>
        /// </summary>
        [Test]
        public void OnUnitDestroyed_TheScrReferenceReplacesTheInlineScript()
        {
            PFE.Data.Definitions.Campaign.CampaignCatalog catalog = CatalogueWithOneOff("unitDeathRefTest", "trCont");
            var scriptSystem = new ScriptSystem(catalog, null, null, null, _ => { });
            scriptSystem.Initialize();

            var system = new AreaTriggerSystem();
            try
            {
                var room = new RoomInstance();

                var trCont = CreateTrigger("trCont", 640f, 280f, 5, 2);
                trCont.uid = "trCont";
                trCont.isActive = true;
                room.objects.Add(trCont);

                var trPunch = CreateTrigger("trPunch", 600f, 120f, 6, 2);
                trPunch.uid = "trPunch";
                trPunch.isActive = true;
                room.objects.Add(trPunch);

                var ranger = new UnitInstance { unitId = "ranger", code = "Q7ZEULhjcEQGtJeu" };
                ranger.attributes.Add(new MapObjectAttributeData { key = "scr", value = "unitDeathRefTest" });

                var inline = new MapObjectScriptData();
                inline.actions.Add(new MapObjectScriptActionData { act = "off", targ = "trPunch" });
                ranger.scripts.Add(inline);
                room.units.Add(ranger);

                system.OnUnitDestroyed(room, ranger);

                Assert.IsFalse(trCont.isActive, "the `scr` reference is `scrDie`, so it must run");
                Assert.IsTrue(trPunch.isActive,
                    "`Unit.as:1580-1582` assigns scrDie AFTER the `<scr>` loop — the inline block is " +
                    "replaced, not run alongside. If this is false, one death ran two scripts.");
            }
            finally
            {
                system.Dispose();
                scriptSystem.Dispose();
                DestroyCatalogue(catalog);
            }
        }

        /// <summary>
        /// A <c>RoomTemplate</c>'s unit placements must survive the import with their
        /// <c>&lt;scr&gt;</c> children — the half of the latch that lives in the data.
        ///
        /// <para><b>Why a real room and not a hand-built record.</b> This is the
        /// "dropped one step later" shape the port keeps re-learning: the parser reads
        /// <c>&lt;scr&gt;</c>, the converter projects it onto <c>ObjectSpawnData</c>, and
        /// <c>RoomPopulator.CreateUnit</c> had no parameter to receive it. A unit built by hand in a
        /// test cannot fail that way, so it would pass while every shipped room lost its scripts.</para>
        /// </summary>
        [Test]
        public void ProbPit_UnitPlacements_KeepTheirInlineDeathScripts()
        {
            RoomTemplate pit = Resources.Load<RoomTemplate>("Rooms/rooms_prob/pit");
            Assert.IsNotNull(pit, "The pit room (Rooms/rooms_prob/pit) must exist in Resources.");

            var room = new RoomInstance
            {
                id = "pit_death_script_instance",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0
            };
            room.InitializeTiles();

            RoomPopulator.PopulateRoom(room, pit, room.difficulty);

            UnitInstance pitU1 = room.units.Find(u => u != null && u.uid == "pitU1");
            Assert.IsNotNull(pitU1, "`rooms_prob/pit` places `pitU1` with `uid=\"pitU1\"`.");

            Assert.IsNotNull(pitU1.scripts,
                "the record's script list must exist — an empty one is a data miss, a null one is a " +
                "hand-built record that never went through RoomPopulator");

            Assert.AreEqual(1, pitU1.scripts.Count,
                "`pitU1` authors one `<scr eve=\"die\">` with three `up` actions. An empty list here " +
                "means RoomPopulator dropped the unit's script list on the way to the record.");

            MapObjectScriptData die = pitU1.scripts[0];
            Assert.AreEqual("die", die.eventName);
            Assert.AreEqual(3, die.actions.Count,
                "the oracle lifts pitU2, pitU3 and pitU4 when pitU1 dies — three `up` actions");

            // The control: the same room's plain raiders must NOT have acquired a script list, or the
            // assertion above would pass for a populator that attached scripts to everything.
            UnitInstance plain = room.units.Find(u => u != null && u.scripts != null && u.scripts.Count == 0);
            Assert.IsNotNull(plain, "the pit has ordinary enemies with no `<scr>`; if none has an empty " +
                                    "list, scripts are being attached to placements that never authored one.");
        }

        /// <summary>
        /// A catalogue holding exactly one script: <c>off</c> on <paramref name="target"/>.
        ///
        /// <para>A real <see cref="PFE.Data.Definitions.Campaign.CampaignCatalog"/> rather than a stub,
        /// because that is what <see cref="ScriptSystem"/> wraps; the fixtures are about the call site
        /// in <c>OnUnitDestroyed</c>, not about the lookup.</para>
        /// </summary>
        static PFE.Data.Definitions.Campaign.CampaignCatalog CatalogueWithOneOff(string scriptId, string target)
        {
            var definition = ScriptableObject.CreateInstance<PFE.Data.Definitions.Campaign.CampaignScriptDefinition>();
            definition.scriptId = scriptId;
            definition.actions.Add(new PFE.Data.Definitions.Campaign.CampaignScriptAction
            {
                action = "off",
                target = target,
            });

            var catalog = ScriptableObject.CreateInstance<PFE.Data.Definitions.Campaign.CampaignCatalog>();
            ((List<PFE.Data.Definitions.Campaign.CampaignScriptDefinition>)catalog.AllScripts).Add(definition);
            catalog.Initialize();
            return catalog;
        }

        static void DestroyCatalogue(PFE.Data.Definitions.Campaign.CampaignCatalog catalog)
        {
            if (catalog == null) return;

            // The definition is a plain ScriptableObject the catalogue only references, so destroying
            // the catalogue would leak it and Unity would report it as a leaked object after the run.
            IReadOnlyList<PFE.Data.Definitions.Campaign.CampaignScriptDefinition> scripts = catalog.AllScripts;
            if (scripts != null)
            {
                for (int i = 0; i < scripts.Count; i++)
                {
                    if (scripts[i] != null) Object.DestroyImmediate(scripts[i]);
                }
            }

            Object.DestroyImmediate(catalog);
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
