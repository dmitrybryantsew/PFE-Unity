using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using PFE.Systems.Telekinesis;

namespace PFE.Tests.Editor.Player
{
    /// <summary>
    /// EditMode tests for <see cref="PlayerTelekinesisController"/>.
    ///
    /// <para>Pins the AS3 telekinesis rules (<c>UnitPlayer.as</c>, <c>Box.as</c>, <c>Pers.as</c>):
    /// <list type="bullet">
    /// <item>Grab refusal on insufficient mana (<c>&lt; 200</c>), excessive mass, unliftable props, distance, or blocked LOS.</item>
    /// <item>Grab acceptance of a prop that is mid-flight (<c>stay</c> is not a gate in AS3).</item>
    /// <item>Grab success and hold state propagation.</item>
    /// <item>Input gesture handling: Q edge grabs settled prop, Q edge while holding drops, Q release is unconsumed.</item>
    /// <item>Hold tick: cursor tracking and mass-scaled mana drain.</item>
    /// <item>Automatic drop on distance (<c>&gt; teleDist * 1.2</c>), mana exhaustion, or lost liftability.</item>
    /// <item>Throw action: unskilled drops, skilled flings with upward bias and mana cost.</item>
    /// </list>
    /// </para>
    /// </summary>
    [TestFixture]
    public class PlayerTelekinesisControllerTests
    {
        private const float Tolerance = 1e-3f;
        private readonly List<GameObject> _spawnedObjects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawnedObjects.Count; i++)
            {
                if (_spawnedObjects[i] != null)
                {
                    Object.DestroyImmediate(_spawnedObjects[i]);
                }
            }
            _spawnedObjects.Clear();
        }

        private CharacterStats CreateTestStats(GameObject go, float initialMana = 1000f)
        {
            var stats = go.AddComponent<CharacterStats>();
            var levelCurve = ScriptableObject.CreateInstance<LevelCurve>();
            levelCurve.baseHp = 100;
            levelCurve.hpPerLevel = 15;
            levelCurve.organHpPerLevel = 40;
            levelCurve.baseOrganHp = 200;
            levelCurve.skillPointsPerLevel = 5;
            stats.Initialize(levelCurve);
            stats.manaHp = initialMana;
            // Seed BOTH pools to the same value.
            //
            // Telekinesis reads the magic-mana BUDGET (CharacterStats.MagicMana == AS3 Unit.mana),
            // which is what every gate in this fixture's sut compares against: the `mana < 200` grab
            // gate (UnitPlayer.as:1754), the `mana <= 0` drop gate (:1274) and the throw spend
            // (:1856 `if(_loc2_ <= mana)`). manaHp is the WOUND organ -- a different, non-regenerating
            // pool that happens to share the name. Seeding only manaHp left the budget at its own
            // default, so a fixture that set mana to 199 still had budget 1000 and the gate never
            // tripped. Both are seeded so the wound-track tests in this fixture are unaffected.
            stats.MagicMana = initialMana;
            return stats;
        }

        private PlayerTelekinesisController CreatePlayer(
            Vector2 playerPositionPixels,
            float mana = 1000f,
            ITileQueryService tileQueryService = null)
        {
            var go = new GameObject("TestPlayer");
            _spawnedObjects.Add(go);

            var stats = CreateTestStats(go, mana);
            var controller = go.AddComponent<PlayerTelekinesisController>();
            controller.Stats = stats;
            controller.SetPlayerPositionPixels(playerPositionPixels);

            if (tileQueryService != null)
            {
                controller.SetTileQueryService(tileQueryService);
            }

            return controller;
        }

        private RoomInstance CreateRoomWithProp(
            out ObjectInstance prop,
            Vector2 propPosition,
            float massa = 25f,
            bool isLiftable = true,
            bool isAtRest = true,
            MapObjectPhysicalCapability capability = MapObjectPhysicalCapability.DynamicTelekinetic)
        {
            var room = new RoomInstance
            {
                id = "test_room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                landPosition = new Vector3Int(0, 0, 0)
            };
            room.InitializeTiles();

            var def = ScriptableObject.CreateInstance<MapObjectDefinition>();
            def.objectId = "woodbox";
            def.size = 1;
            def.width = 1;
            def.physicalCapability = capability;
            def.mass = massa;

            prop = new ObjectInstance
            {
                objectId = "woodbox",
                objectType = "box",
                definition = def,
                definitionId = "woodbox",
                position = propPosition,
                runtimeState = new MapObjectRuntimeStateData()
            };
            prop.EnsureStructuredData();
            prop.runtimeState.dynamicState.levitPoss = isLiftable;
            prop.runtimeState.dynamicState.stay = isAtRest;

            room.AddObject(prop);
            return room;
        }

        // ── Grab Gate Tests: UnitPlayer.as:1754, 1790 ──────────────────────────────────────────

        [Test]
        public void TryGrab_WhenManaBelow200_RefusesGrab()
        {
            // UnitPlayer.as:1754 — `if(mana < 200) return;`
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 199f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            bool grabbed = player.TryGrab();

            Assert.IsFalse(grabbed, "Grab must be refused when mana is strictly below 200.");
            Assert.IsFalse(player.IsHoldingObject);
            Assert.IsNull(player.HeldObject);
        }

        [Test]
        public void TryGrab_WhenMassExceedsMaxTeleMassa_RefusesGrab()
        {
            // Base maxTeleMassa is 0.6f. def.massa = 50f gives AS3 massa = 1.0f > 0.6f.
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f), massa: 50f);
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            bool grabbed = player.TryGrab();

            Assert.IsFalse(grabbed, "Grab must be refused when object mass exceeds player's maxTeleMassa.");
            Assert.IsFalse(player.IsHoldingObject);
        }

        [Test]
        public void TryGrab_WhenPropNotAtRest_StillGrabs()
        {
            // AS3 CAN grab a falling prop. actTele's candidate loop (UnitPlayer.as:1769-1783) filters
            // on levitPoss and massa only — it never reads `stay`; only the HUD hint at GUI.as:1326
            // does, and AS3 CLEARS `stay` on grab (:1831), so it is a consequence of picking a prop up
            // rather than a precondition for it. An earlier version of the port required IsAtRest()
            // here and refused exactly this grab. Removed 2026-10-02.
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f), isAtRest: false);
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            bool grabbed = player.TryGrab();

            Assert.IsTrue(grabbed, "A prop mid-flight must be grabbable — AS3's actTele does not test stay.");
            Assert.AreSame(prop, player.HeldObject);
            Assert.IsFalse(prop.runtimeState.dynamicState.stay,
                "Grabbing must clear stay, as AS3 does at UnitPlayer.as:1831.");
        }

        [Test]
        public void TryGrab_WhenPropAlreadyHeld_RefusesGrab()
        {
            // The port's own guard, replacing what `stay` was accidentally doing. Once the `stay`
            // precondition went away, a held prop would otherwise become a candidate again and could
            // be re-grabbed mid-flight.
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            prop.runtimeState.dynamicState.isHeldByTelekinesis = true;
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            bool grabbed = player.TryGrab();

            Assert.IsFalse(grabbed, "A prop already held by telekinesis must not be a grab candidate.");
            Assert.IsFalse(player.IsHoldingObject);
        }

        [Test]
        public void TryGrab_WhenPropNotLiftable_RefusesGrab()
        {
            // Obj.as:32 / UnitPlayer.as:1790 — levitPoss must be true.
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f), isLiftable: false);
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            bool grabbed = player.TryGrab();

            Assert.IsFalse(grabbed, "Grab must be refused when object is not liftable (levitPoss == false).");
            Assert.IsFalse(player.IsHoldingObject);
        }

        [Test]
        public void TryGrab_WhenDistanceExceedsTeleDist_RefusesGrab()
        {
            // Default teleDist is 360000 px² (600 px). Position prop 700 px away.
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(700f, 100f));
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(700f, 115f));

            bool grabbed = player.TryGrab();

            Assert.IsFalse(grabbed, "Grab must be refused when distance exceeds teleDist.");
            Assert.IsFalse(player.IsHoldingObject);
        }

        [Test]
        public void TryGrab_WhenLineOfSightBlocked_RefusesGrabUnlessTelemaster()
        {
            // UnitPlayer.as:1792 — line of sight is checked when telemaster == 0.
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            var queryService = new UnifiedTileQueryService(room);
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f, tileQueryService: queryService);
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            // Without obstacle, grab succeeds
            bool grabbedClear = player.TryGrab();
            Assert.IsTrue(grabbedClear, "Unobstructed candidate should be grabbed.");
            player.Drop();
            prop.runtimeState.dynamicState.stay = true;

            // Insert a solid wall column between player (x=0) and prop (x=100) at tile column x=1 (x: 40..80 px)
            for (int y = 0; y < room.height; y++)
            {
                room.tiles[1, y] = new TileData { physicsType = TilePhysicsType.Wall };
            }

            Assert.AreEqual(0, player.Stats.Telemaster);
            bool grabbedBlocked = player.TryGrab();
            Assert.IsFalse(grabbedBlocked, "Blocked LOS should prevent telekinesis grab when telemaster == 0.");

            // Unlock telemaster perk (AS3: telemaster = 1 bypasses LOS)
            player.Stats.Telemaster = 1;
            bool grabbedPenetrating = player.TryGrab();
            Assert.IsTrue(grabbedPenetrating, "Telemaster perk allows telekinesis grab through walls/tiles.");
            Assert.IsTrue(player.IsHoldingObject);
        }

        [Test]
        public void TryGrab_WhenAllConditionsMet_SucceedsAndHoldsObject()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            bool grabbed = player.TryGrab();

            Assert.IsTrue(grabbed, "Valid prop under cursor must be grabbed.");
            Assert.IsTrue(player.IsHoldingObject);
            Assert.AreSame(prop, player.HeldObject);
            Assert.IsTrue(prop.runtimeState.dynamicState.isHeldByTelekinesis);
        }

        // ── Room Source: gameplay gets the room from the LandMap, never from SetRoom ──────────
        //
        // These pin the wiring gap that made grabbing impossible in play-test while every other
        // test here stayed green. Every fixture above calls SetRoom; no gameplay code does. The
        // component is created by PlayerController.Awake via AddComponent, so VContainer never
        // injects it (ExistingComponentProvider injects only the component it was handed, which is
        // the sibling PlayerController), and MapBridge is what hands it the LandMap instead.
        //
        // Nothing went red, because a controller with no room is a legal state for a fixture — it
        // just returns false from every grab. That is the failure mode these four tests exist to
        // make loud.

        [Test]
        public void CurrentRoom_WhenOnlyLandMapIsSet_ResolvesFromLandMap()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out _, propPosition: new Vector2(100f, 100f));
            var landMap = new LandMap();
            landMap.SetCurrentRoom(room);

            player.SetLandMap(landMap);

            Assert.AreSame(room, player.CurrentRoom,
                "With no SetRoom call the controller must resolve its room from the LandMap — that " +
                "is the only room source gameplay has.");
        }

        [Test]
        public void CurrentRoom_AfterRoomTransition_FollowsTheLandMap()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var firstRoom = CreateRoomWithProp(out _, propPosition: new Vector2(100f, 100f));
            var secondRoom = CreateRoomWithProp(out _, propPosition: new Vector2(100f, 100f));
            var landMap = new LandMap();
            landMap.SetCurrentRoom(firstRoom);
            player.SetLandMap(landMap);

            landMap.SetCurrentRoom(secondRoom);

            Assert.AreSame(secondRoom, player.CurrentRoom,
                "A door transition goes through RoomTransitionManager -> landMap.SetCurrentRoom and " +
                "never re-enters MapBridge.SpawnPlayer, so a controller that held a room instead of " +
                "the land map would keep grabbing in the room the player left.");
        }

        [Test]
        public void TryGrab_WhenRoomComesOnlyFromLandMap_Succeeds()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            var landMap = new LandMap();
            landMap.SetCurrentRoom(room);
            player.SetLandMap(landMap);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            bool grabbed = player.TryGrab();

            Assert.IsTrue(grabbed, "A grab must work with the room sourced from the LandMap alone.");
            Assert.AreSame(prop, player.HeldObject);
        }

        [Test]
        public void TryGrab_AfterRoomTransition_GrabsInTheNewRoom()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var firstRoom = CreateRoomWithProp(out _, propPosition: new Vector2(100f, 100f));
            var secondRoom = CreateRoomWithProp(out ObjectInstance propInSecondRoom,
                propPosition: new Vector2(100f, 100f));
            var landMap = new LandMap();
            landMap.SetCurrentRoom(firstRoom);
            player.SetLandMap(landMap);

            landMap.SetCurrentRoom(secondRoom);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            bool grabbed = player.TryGrab();

            Assert.IsTrue(grabbed, "After a room transition the grab must resolve against the new room.");
            Assert.AreSame(propInSecondRoom, player.HeldObject);
        }

        [Test]
        public void ResolveTileQuery_WithNoOverride_UsesTheCurrentRoom()
        {
            // The LOS ray is only meaningful against the room the player is in. A query handed over
            // once wraps one room, so it must be derived rather than held. An all-air room is the
            // cheapest way to show the derived query exists and answers "nothing in the way".
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            var landMap = new LandMap();
            landMap.SetCurrentRoom(room);
            player.SetLandMap(landMap);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            bool grabbed = player.TryGrab();

            Assert.IsTrue(grabbed,
                "With telemaster == 0 the line-of-sight test runs; on an all-air room it must find " +
                "nothing and let the grab through. A derived query that reported a phantom hit here " +
                "would make every grab fail while looking like a physics problem.");
        }

        // ── Input Gesture Handling: Q Key Press & Release ─────────────────────────────────────

        [Test]
        public void OnTeleportKeyPressed_PressEdge_GrabsObjectAndConsumesEvent()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            bool consumed = player.OnTeleportKeyPressed(isStarted: true);

            Assert.IsTrue(consumed, "Q press edge grabbing a prop must consume the event to suppress teleport.");
            Assert.IsTrue(player.IsHoldingObject);
        }

        [Test]
        public void OnTeleportKeyPressed_PressEdge_WhenHolding_DropsObjectAndConsumesEvent()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            player.TryGrab();
            Assert.IsTrue(player.IsHoldingObject);

            bool consumed = player.OnTeleportKeyPressed(isStarted: true);

            Assert.IsTrue(consumed, "Q press edge while holding must drop the prop and consume the event.");
            Assert.IsFalse(player.IsHoldingObject);
            Assert.IsFalse(prop.runtimeState.dynamicState.isHeldByTelekinesis);
        }

        [Test]
        public void OnTeleportKeyPressed_ReleaseEdge_ReturnsFalse()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);

            bool consumed = player.OnTeleportKeyPressed(isStarted: false);

            Assert.IsFalse(consumed, "Q release edge should not be consumed by telekinesis.");
        }

        // ── Throw Action: UnitPlayer.as:1847-1879 ──────────────────────────────────────────────

        [Test]
        public void TryThrow_WhenUnskilled_DropsObjectInPlace()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            player.TryGrab();
            Assert.AreEqual(0f, player.Stats.ThrowForce, "Unskilled player has throwForce = 0.");

            bool threw = player.TryThrow();

            Assert.IsTrue(threw);
            Assert.IsFalse(player.IsHoldingObject, "Held object must be released.");
            Assert.IsFalse(prop.runtimeState.dynamicState.isHeldByTelekinesis);
            Assert.IsFalse(prop.runtimeState.dynamicState.isThrown, "Unskilled drop does not count as throw.");
            Assert.AreEqual(Vector2.zero, prop.runtimeState.dynamicState.velocity);
            Assert.AreEqual(500f, player.Stats.manaHp, Tolerance, "Unskilled drop costs 0 mana.");
        }

        [Test]
        public void TryThrow_WhenSkilled_ImpartsDirectionalVelocityAndDrainsMana()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            // Set skilled throw stats (rank 1 telethrow: force 25, dmagic 200)
            player.Stats.ThrowForce = 25f;
            player.Stats.ThrowDmagic = 200f;

            player.TryGrab();
            Assert.IsTrue(player.IsHoldingObject);

            bool threw = player.TryThrow();

            Assert.IsTrue(threw);
            Assert.IsFalse(player.IsHoldingObject, "Object must no longer be held after throw.");
            Assert.IsFalse(prop.runtimeState.dynamicState.isHeldByTelekinesis);
            Assert.IsTrue(prop.runtimeState.dynamicState.isThrown, "Throw must set isThrown flag.");

            // Velocity must have positive X (thrown rightward away from player at X=0 to prop at X=100)
            // and upward bias in Y (due to -10 px AS3 bias mapped to Unity +Y).
            Assert.Greater(prop.runtimeState.dynamicState.velocity.x, 0f, "Should be thrown rightward away from player.");
            Assert.Greater(prop.runtimeState.dynamicState.velocity.y, 0f, "Should have upward vertical throw impulse.");

            // Mana cost: massa = 25 / 50 = 0.5f. cost = 0.5 * 200 * 1.0 = 100 mana.
            // Asserted on the BUDGET pool (MagicMana == AS3 `mana`), which is the field the throw
            // spends (`UnitPlayer.as:1859 mana -= _loc2_`) and the one TestStats now seeds.
            Assert.AreEqual(400f, player.Stats.MagicMana, Tolerance, "Mana must be deducted by throw cost.");
        }

        // ── Hold Loop & Auto-Drop: UnitPlayer.as:1274, 1307 ────────────────────────────────────

        [Test]
        public void UpdateHold_UpdatesTelekineticTargetAndDrainsMana()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            player.TryGrab();
            Assert.IsTrue(player.IsHoldingObject);

            // Move cursor to new coordinate
            Vector2 newCursor = new Vector2(150f, 180f);
            player.SetCursorWorldPixels(newCursor);

            // Update hold over 1 second (30 AS3 frames)
            player.UpdateHold(1.0f);

            Assert.AreEqual(newCursor, prop.runtimeState.dynamicState.telekineticTarget,
                "Telekinetic target on held object must update to cursor world pixels.");

            // NOTE: UpdateHold deliberately does NOT drain mana, and asserting that it does would
            // re-state the double-charge bug. AS3 has no drain in its hold block; the cost lives in
            // the single per-tick mana block (UnitPlayer.as:1302-1361) as
            // `dmana -= teleSqrtMassa * pers.teleMult`, which PlayerManaTicker.TickMana reproduces.
            // The hold COST is therefore pinned in HoldManaDrain_*DrainsTheBudget below, against
            // that tick, not against this frame-clock call.
            Assert.AreEqual(500f, player.Stats.MagicMana, Tolerance,
                "UpdateHold must not charge mana -- the drain belongs to the sim tick, not this call.");
        }

        [Test]
        public void HoldManaDrain_OneSimTick_DrainsTheBudgetBySqrtMassTimesTeleMult()
        {
            // The hold COST, pinned against the tick that actually charges it -- the replacement for
            // the old "UpdateHold drains" assertion, which could only pass if the drain were charged
            // twice (once per frame here, once per tick in PlayerManaTicker).
            //
            // UnitPlayer.as:1307 `dmana -= this.teleSqrtMassa * this.pers.teleMult`, then (:1331)
            // `dmana *= allDManaMult`. TelekinesisMath.TickManaState does that part; here we drive
            // CharacterStats.TickMana directly, as the ticker does, so no clock is needed.
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));
            player.TryGrab();
            Assert.IsTrue(player.IsHoldingObject);

            float massa = prop.GetAs3Massa();
            Assert.Greater(massa, player.Stats.TelePorog,
                "Fixture precondition: the prop must be heavy enough to cost mana to hold.");

            float before = player.Stats.MagicMana;

            var tick = new CharacterStats.ManaTickState
            {
                TelekinesisActive = true,
                TelekinesisSqrtMass = Mathf.Sqrt(massa),
            };
            player.Stats.TickMana(tick);

            float expectedDrain = Mathf.Sqrt(massa) * player.Stats.TeleMult * player.Stats.AllDManaMult;
            Assert.AreEqual(before - expectedDrain, player.Stats.MagicMana, Tolerance,
                "One tick holding a prop must drain sqrt(massa) * teleMult * allDManaMult from the budget.");
        }

        [Test]
        public void UpdateHold_WhenObjectPastDropDistance_DropsObject()
        {
            // Default teleDist is 360000. Drop threshold is teleDist * 1.2 = 432000 px² (sqrt ≈ 657 px).
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            player.TryGrab();
            Assert.IsTrue(player.IsHoldingObject);

            // Move prop far away beyond 1.2x range (700 px away, 700² = 490000 > 432000)
            prop.position = new Vector2(700f, 100f);

            player.UpdateHold(0.1f);

            Assert.IsFalse(player.IsHoldingObject, "Prop past 1.2x teleDist must be dropped.");
            Assert.IsFalse(prop.runtimeState.dynamicState.isHeldByTelekinesis);
        }

        [Test]
        public void UpdateHold_WhenManaZero_DropsObject()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            player.TryGrab();
            Assert.IsTrue(player.IsHoldingObject);

            // Exhaust the BUDGET pool -- the drop gate is `mana <= 0` against Unit.mana
            // (UnitPlayer.as:1274), which is MagicMana here, NOT the manaHp wound organ.
            player.Stats.MagicMana = 0f;

            player.UpdateHold(0.1f);

            Assert.IsFalse(player.IsHoldingObject, "Prop must be dropped when mana is exhausted.");
            Assert.IsFalse(prop.runtimeState.dynamicState.isHeldByTelekinesis);
        }

        [Test]
        public void UpdateHold_WhenObjectLosesLevitPoss_DropsObject()
        {
            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 500f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            player.SetCursorWorldPixels(new Vector2(100f, 115f));

            player.TryGrab();
            Assert.IsTrue(player.IsHoldingObject);

            // Clear levitPoss at runtime (e.g. from script or object state transition)
            prop.runtimeState.dynamicState.levitPoss = false;

            player.UpdateHold(0.1f);

            Assert.IsFalse(player.IsHoldingObject, "Prop must be dropped if levitPoss becomes false.");
            Assert.IsFalse(prop.runtimeState.dynamicState.isHeldByTelekinesis);
        }

        // ── The mover, not the writer: StepHeldObject through the room heartbeat ────────────────

        /// <summary>
        /// A held prop must actually MOVE when the room is stepped.
        ///
        /// <para><b>Why this test exists, and why nothing else here covers it.</b> Every other test in
        /// this fixture drives <see cref="PlayerTelekinesisController.UpdateHold"/>, which only
        /// <i>writes</i> <c>dynamicState.telekineticTarget</c> and drains mana — it moves nothing.
        /// Moving the prop is <c>RoomObjectPhysicsLayer.StepHeldObject</c>, reached only through
        /// <c>RoomInstance.Update</c>, and <b>no test in this fixture ever called it</b>. So the suite
        /// was green across the whole grab / hold / drop surface while being structurally unable to
        /// notice that a held box never moves: it asserted the writer and never the mover. That is
        /// exactly the play-test symptom — "the probe says the grab would succeed, I press Q, and
        /// nothing happens". <c>UpdateHold_UpdatesTelekineticTargetAndDrainsMana</c> asserts the target
        /// follows the cursor, which is the half that was already working.</para>
        ///
        /// <para>This closes the gap: grab, aim, then step the room the way the heartbeat does and
        /// assert the position changed in the right direction. A failure here is a physics bug; a pass
        /// here means the play-test failure is upstream — the room never ticking
        /// (<c>RoomInstance.isActive</c>) or the heartbeat not being driven.</para>
        /// </summary>
        [Test]
        public void HeldProp_MovesTowardTheCursor_WhenTheRoomIsStepped()
        {
            const float As3Frame = 1f / 30f;

            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 1000f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);

            // RoomInstance.isActive defaults to FALSE and RoomInstance.Update early-returns on it, so
            // without this the loop below would step nothing and the test would "fail" for a reason
            // that has nothing to do with the physics it is here to check.
            room.isActive = true;

            player.SetCursorWorldPixels(new Vector2(100f, 115f));
            Assert.IsTrue(player.TryGrab(), "precondition: the grab must succeed");
            Assert.IsTrue(prop.IsHeldByTelekinesis(), "precondition: the hold flag must be set");

            // Aim well above the prop so the expected direction is unambiguous, then step.
            player.SetCursorWorldPixels(new Vector2(100f, 200f));

            Vector2 start = prop.position;
            for (int i = 0; i < 30; i++)
            {
                player.UpdateHold(As3Frame);
                room.Update(As3Frame);
            }

            Assert.Greater(prop.position.y, start.y,
                "A held prop must rise toward the cursor once the room is stepped. If this fails, " +
                "StepHeldObject/MoveWithCollision is refusing the move; if it passes, a play-test " +
                "failure is the heartbeat or room activation, not the physics.");
            Assert.AreEqual(100f, prop.position.x, 1f,
                "The cursor did not move horizontally, so the prop must not drift sideways.");
        }

        /// <summary>
        /// The hold step must compare the object's AABB <b>centre</b> to the cursor, not its bottom.
        ///
        /// <para>AS3's hold tick writes <c>teleObj.dy</c> from <c>teleObj.Y - teleObj.scY / 2</c>
        /// against <c>celY</c> (<c>UnitPlayer.as:1255/1259</c>) — the object's vertical <i>middle</i>;
        /// its <c>X</c> is already the horizontal centre. <c>ObjectInstance.position.y</c> is the
        /// <b>bottom</b> (<c>GetApproximateBounds</c> builds <c>Rect(x - w/2, y, w, h)</c>), so
        /// feeding it in raw biases the vertical target by half the object's height — 40 px for the
        /// room's crates, against a 15 px deadzone.</para>
        ///
        /// <para><b>Why the cursor is put level with the centre.</b> The discriminator is whether a
        /// nudge fires at all. With the cursor exactly level with the centre the difference is 0 —
        /// inside the deadzone, so nothing is nudged. Reading the bottom instead puts it 20 px below
        /// the cursor, which is outside the 15 px deadzone, so the object is nudged <b>up</b>. The
        /// two reference points cannot be pushed to opposite <i>signs</i> here (half a 40 px prop is
        /// 20 px, so the two windows overlap), but "no nudge" versus "nudge up" separates them just
        /// as cleanly. Part (b) is the positive control: without it, (a) would also pass if the step
        /// simply never moved anything.</para>
        /// </summary>
        [Test]
        public void HeldProp_ComparesItsCentreToTheCursor_NotItsBottom()
        {
            const float As3Frame = 1f / 30f;

            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 1000f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            room.isActive = true;

            player.SetCursorWorldPixels(new Vector2(100f, 115f));
            Assert.IsTrue(player.TryGrab(), "precondition: the grab must succeed");

            Vector2 center = prop.GetApproximateBounds().center;
            Assert.Greater(center.y - prop.position.y, TelekinesisMath.Deadzone,
                "precondition: half the prop's height must exceed the deadzone, or the two reference " +
                "points fall inside the same no-nudge band and this test cannot tell them apart.");

            // (a) Cursor level with the CENTRE -> difference 0 -> inside the deadzone -> no nudge.
            player.SetCursorWorldPixels(new Vector2(100f, center.y));
            player.UpdateHold(As3Frame);
            room.Update(As3Frame);

            Assert.LessOrEqual(prop.runtimeState.dynamicState.velocity.y, Tolerance,
                "With the cursor level with the prop's CENTRE nothing may be nudged — AS3 compares " +
                "the centre (UnitPlayer.as:1255), so the difference is 0 and the deadzone swallows " +
                "it. A positive velocity here means the step is comparing the prop's BOTTOM, which " +
                "reads as 20 px below the cursor and lies outside the 15 px deadzone.");

            // (b) Positive control: more than a deadzone above the centre must nudge UP.
            Vector2 centerAfterA = prop.GetApproximateBounds().center;
            player.SetCursorWorldPixels(
                new Vector2(100f, centerAfterA.y + TelekinesisMath.Deadzone + 5f));
            player.UpdateHold(As3Frame);
            room.Update(As3Frame);

            Assert.Greater(prop.runtimeState.dynamicState.velocity.y, 0f,
                "Positive control: a cursor more than a deadzone above the centre must nudge the " +
                "prop upward, otherwise (a) proves nothing.");
        }

        /// <summary>
        /// A cleared <c>hasTelekineticTarget</c> must not silently freeze the hold.
        ///
        /// <para><c>StepHeldObject</c> reads
        /// <c>targetPosition = state.hasTelekineticTarget ? state.telekineticTarget : obj.position</c>,
        /// so with the flag false the held object chases <i>its own position</i>: the difference is
        /// always inside the deadzone, velocity stays exactly zero, and the prop hangs motionless with
        /// no error, no refusal and nothing in the trace. Both <c>TryApplyImpulse</c> and
        /// <c>TryReleaseTelekineticHold</c> clear the flag, so a knockback or any impact landing on a
        /// held prop does it in normal play.</para>
        ///
        /// <para>AS3 has no flag to go stale — its player tick writes the object's <c>dx</c>/<c>dy</c>
        /// directly every frame (<c>UnitPlayer.as:1247-1262</c>) — so the hold tick re-asserts it for as
        /// long as the prop is held.</para>
        /// </summary>
        [Test]
        public void HeldProp_WithTargetFlagCleared_StillMoves()
        {
            const float As3Frame = 1f / 30f;

            var player = CreatePlayer(playerPositionPixels: new Vector2(0f, 100f), mana: 1000f);
            var room = CreateRoomWithProp(out ObjectInstance prop, propPosition: new Vector2(100f, 100f));
            player.SetRoom(room);
            room.isActive = true;

            player.SetCursorWorldPixels(new Vector2(100f, 115f));
            Assert.IsTrue(player.TryGrab(), "precondition: the grab must succeed");

            // What an impulse or a release elsewhere would leave behind.
            prop.runtimeState.dynamicState.hasTelekineticTarget = false;

            player.SetCursorWorldPixels(new Vector2(100f, 200f));

            Vector2 start = prop.position;
            for (int i = 0; i < 30; i++)
            {
                player.UpdateHold(As3Frame);
                room.Update(As3Frame);
            }

            Assert.Greater(prop.position.y, start.y,
                "The hold tick must re-assert hasTelekineticTarget while the prop is held. Without it " +
                "StepHeldObject aims the prop at its own position, velocity stays 0, and the prop hangs " +
                "motionless -- a silent freeze with nothing in the trace.");
        }
    }
}
