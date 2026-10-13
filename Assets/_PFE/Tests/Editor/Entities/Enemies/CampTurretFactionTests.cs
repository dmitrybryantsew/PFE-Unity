using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Entities.Enemies;
using PFE.Entities.Enemies.Archetypes;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Tests.Editor.Entities.Enemies
{
    /// <summary>
    /// The two rules behind "the camp is safe", asserted as pure functions — no <c>SetUp</c>, no
    /// <c>GameObject</c>, no asset read, so the whole fixture executes offline.
    ///
    /// <para><b>Why this is a separate fixture from <see cref="CampTurretFactionTests"/>.</b> A
    /// <c>[SetUp]</c> that allocates a <c>GameObject</c> is an <c>ECall</c>, and an ECall kills every
    /// test in its fixture — so a fixture that also wants to spawn things cannot host a pure assertion.
    /// The integration half stays in the other fixture and is verified in the editor; this half runs on
    /// every change.</para>
    /// </summary>
    [TestFixture]
    public class TurretFactionRuleTests
    {
        private static List<MapObjectAttributeData> Attributes(params string[] keyValuePairs)
        {
            var list = new List<MapObjectAttributeData>();
            for (int i = 0; i + 1 < keyValuePairs.Length; i += 2)
            {
                list.Add(new MapObjectAttributeData { key = keyValuePairs[i], value = keyValuePairs[i + 1] });
            }

            return list;
        }

        // ── the placement override (UnitTurret.as:140-142) ───────────────────────────────────────

        /// <summary>
        /// The camp's turrets are the player's only because their <c>&lt;obj&gt;</c> rows say
        /// <c>fraction="100"</c> — the <c>turret</c> unit row carries no <c>fraction</c> at all
        /// (<c>AllData.as:1758</c> <c>&lt;unit id='turret' cat='2'/&gt;</c>).
        /// </summary>
        [Test]
        public void ResolvePlacementFaction_ReadsThePlacedFraction()
        {
            FactionType? faction = RoomPopulator.ResolvePlacementFaction(
                Attributes("code", "JHpuvdxIgCqkmD7I", "fraction", "100"));

            Assert.IsTrue(faction.HasValue, "fraction=\"100\" is authored, so the placement must answer.");
            Assert.AreEqual(FactionType.Player, faction.Value);
        }

        /// <summary>
        /// The complement, and the reason the field is nullable: <b>93 of the 97 <c>turret</c>
        /// placements in the game author no <c>fraction</c> at all</b> and are hostile. A silent row
        /// must stay silent so the unit definition is consulted — collapsing it to <c>Neutral</c> would
        /// make "the row said 0" indistinguishable from "the row said nothing", and would quietly make
        /// every authored turret in the game faction 0.
        /// </summary>
        [Test]
        public void ResolvePlacementFaction_SilentRowIsNull_NotNeutral()
        {
            Assert.IsNull(RoomPopulator.ResolvePlacementFaction(Attributes("code", "abc")),
                "No fraction attribute means no override — the definition decides.");
            Assert.IsNull(RoomPopulator.ResolvePlacementFaction(new List<MapObjectAttributeData>()),
                "An empty attribute list is the same silence.");
            Assert.IsNull(RoomPopulator.ResolvePlacementFaction(null),
                "A null list must not throw; RoomPopulator calls this on every placement.");
        }

        /// <summary>
        /// <c>fraction="0"</c> is a real AS3 value (<c>Unit.as:454</c>), so it must come back as
        /// <c>Neutral</c> rather than being mistaken for an absent attribute. This is the assertion
        /// that makes the nullable field load-bearing rather than decorative.
        /// </summary>
        [Test]
        public void ResolvePlacementFaction_ExplicitZero_IsNeutralNotAbsent()
        {
            FactionType? faction = RoomPopulator.ResolvePlacementFaction(Attributes("fraction", "0"));

            Assert.IsTrue(faction.HasValue, "fraction=\"0\" IS authored — it is not the same as silence.");
            Assert.AreEqual(FactionType.Neutral, faction.Value);
        }

        /// <summary>
        /// AS3 assigns the attribute's String straight into an <c>int</c> field, so garbage coerces to
        /// <c>NaN</c> and every <c>==</c> against it is false. The port has no NaN in an enum, and
        /// inventing <c>Neutral</c> would give a typo the meaning of a deliberate instruction — so
        /// garbage falls back to the definition. No shipped placement is in this state; the test exists
        /// so a future one fails loudly instead of silently becoming faction 0.
        /// </summary>
        [Test]
        public void ResolvePlacementFaction_NonNumericIsTreatedAsAbsent()
        {
            Assert.IsNull(RoomPopulator.ResolvePlacementFaction(Attributes("fraction", "raider")));
            Assert.IsNull(RoomPopulator.ResolvePlacementFaction(Attributes("fraction", "")));
            Assert.IsNull(RoomPopulator.ResolvePlacementFaction(Attributes("fraction", "  ")));
        }

        /// <summary>
        /// The key match is case-insensitive (<c>MapObjectDataUtility.GetAttribute</c>), and the
        /// importer has always written it lower-case. Asserted because a case-sensitive match here
        /// would fail silently for every placement — the same shape as the <c>characterId</c> /
        /// <c>characterID</c> trap.
        /// </summary>
        [Test]
        public void ResolvePlacementFaction_KeyMatchIsCaseInsensitive()
        {
            FactionType? faction = RoomPopulator.ResolvePlacementFaction(Attributes("Fraction", "100"));

            Assert.IsTrue(faction.HasValue);
            Assert.AreEqual(FactionType.Player, faction.Value);
        }

        // ── the targeting rule (Unit.as:4643-4655, UnitTurret.as:549) ────────────────────────────

        /// <summary>
        /// A unit never targets its own faction. Asserted in <b>both</b> directions, so a predicate
        /// that always returned false — which would "fix" the camp by disarming every enemy in the
        /// game — cannot pass.
        /// </summary>
        [Test]
        public void FactionPermitsTargeting_RefusesOwnFaction_AndAdmitsAnother()
        {
            Assert.IsFalse(EnemyBrain.FactionPermitsTargeting(FactionType.Player, FactionType.Player),
                "A player-faction turret must not accept the player as a target — this is the camp bug.");
            Assert.IsFalse(EnemyBrain.FactionPermitsTargeting(FactionType.Raider, FactionType.Raider),
                "UnitTurret.as:549 skips `_loc3_.fraction == fraction` for every faction, not just F_PLAYER.");
            Assert.IsFalse(EnemyBrain.FactionPermitsTargeting(FactionType.Neutral, FactionType.Neutral));

            Assert.IsTrue(EnemyBrain.FactionPermitsTargeting(FactionType.Player, FactionType.Raider),
                "An ordinary raider must still be able to target the player, or the gate breaks every " +
                "enemy in the game.");
            Assert.IsTrue(EnemyBrain.FactionPermitsTargeting(FactionType.Monster, FactionType.Raider));
            Assert.IsTrue(EnemyBrain.FactionPermitsTargeting(FactionType.Raider, FactionType.Player));
            Assert.IsTrue(EnemyBrain.FactionPermitsTargeting(FactionType.Robot, FactionType.Player));
        }

        /// <summary>
        /// The two rules composed: a camp turret's faction (Player, from its placement) against the
        /// player's faction must be refused, while the same turret's faction against a raider must be
        /// admitted — i.e. it is a guard, not a disarm.
        /// </summary>
        [Test]
        public void CampTurret_RefusesThePlayer_ButWouldAdmitARaider()
        {
            FactionType turretFaction = RoomPopulator
                .ResolvePlacementFaction(Attributes("fraction", "100")).Value;

            Assert.IsFalse(EnemyBrain.FactionPermitsTargeting(FactionType.Player, turretFaction));
            Assert.IsTrue(EnemyBrain.FactionPermitsTargeting(FactionType.Raider, turretFaction));
        }
    }

    /// <summary>
    /// The camp is the player's own base, and its defences are the player's — so they must not shoot
    /// the player.
    ///
    /// <para><b>The three breaks this pins shut.</b> The turrets shot the player inside their own base
    /// because three independent things were each wrong at once:
    /// <list type="number">
    /// <item><c>UnitInstance</c> had no faction field, so <c>RoomPopulator</c> dropped the placement's
    /// <c>fraction="100"</c> on the floor;</item>
    /// <item><c>UnitController.Faction</c> read only the shared <c>UnitDefinition</c>, which for
    /// <c>turret</c> is <c>fraction: 2</c> (Raider) and, in AS3, is not set at all
    /// (<c>AllData.as:1758</c> <c>&lt;unit id='turret' cat='2'/&gt;</c>) — the placement is the
    /// <i>only</i> source;</item>
    /// <item><c>EnemyBrain.RefreshTargetCandidates</c> added every live <c>PlayerController</c>
    /// unconditionally, so faction had no effect on targeting anywhere in the game.</item>
    /// </list></para>
    ///
    /// <para><b>⚠ These tests need the asset database and a live GameObject, so they cannot run in the
    /// offline wall</b> — they fail at <c>[SetUp]</c> with <c>SecurityException: ECall methods must be
    /// packaged into a system module</c>, which is an environment limit and not a defect. The two rules
    /// they exercise are proven offline in <see cref="TurretFactionRuleTests"/>; these tests prove the
    /// <i>wiring</i> — that the room data reaches the field and the field reaches the controller — and
    /// are owner-verified in the editor's EditMode runner.</para>
    ///
    /// <para><b>Every test here has a complement.</b> A gate that simply refused every candidate would
    /// pass a "the friendly turret does not target the player" assertion while breaking every enemy in
    /// the game, so a hostile turret is spawned from real shipped data as the control.</para>
    /// </summary>
    [TestFixture]
    public class CampTurretFactionTests
    {
        private GameObject _root;
        private Transform _parent;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("CampTurretFactionTests_Root");
            var parentGo = new GameObject("SpawnedUnits");
            parentGo.transform.SetParent(_root.transform, false);
            _parent = parentGo.transform;
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
            }
        }

        private static RoomInstance MakeRoom(string id)
        {
            var room = new RoomInstance
            {
                id = id,
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                borderOffset = 0
            };
            room.InitializeTiles();
            return room;
        }

        private static List<UnitInstance> TurretPlacements(RoomInstance room)
        {
            return room.units.FindAll(u => u != null && u.controllerId == TurretController.ControllerId);
        }

        /// <summary>
        /// The camp room authors nine player-faction turrets — four <c>turret</c>, one
        /// <c>landturret</c>, four <c>armturret</c> (<c>RoomsCamp.as:86-90, 127-130, 794</c>, all
        /// <c>fraction="100"</c>). Each must survive the import as a placement override.
        /// </summary>
        [Test]
        public void CampRoom_EveryTurretPlacement_CarriesThePlayersFaction()
        {
            RoomTemplate camp = Resources.Load<RoomTemplate>("Rooms/rooms_rbl/room_0_0");
            Assert.IsNotNull(camp, "The camp room (Rooms/rooms_rbl/room_0_0) must exist in Resources.");

            RoomInstance room = MakeRoom("camp_faction_instance");
            RoomPopulator.PopulateRoom(room, camp, room.difficulty);

            List<UnitInstance> turrets = TurretPlacements(room);
            Assert.AreEqual(9, turrets.Count,
                "RoomsCamp.as authors 4 `turret`, 1 `landturret` and 4 `armturret` in the camp room. " +
                "Got: " + Describe(turrets));

            for (int i = 0; i < turrets.Count; i++)
            {
                Assert.AreEqual(FactionType.Player, turrets[i].fraction,
                    $"'{turrets[i].unitId}' is placed with fraction=\"100\" (F_PLAYER). A null here " +
                    "means RoomPopulator did not read the placement's fraction attribute at all.");
            }
        }

        /// <summary>
        /// The <c>turret</c> unit definition says <c>fraction: 2</c> (Raider), so the nine assertions
        /// above are only meaningful if at least one of them <i>overrode</i> a different value. This is
        /// the control that makes the previous test non-vacuous.
        /// </summary>
        [Test]
        public void CampRoom_PlacementFaction_ActuallyOverridesTheDefinition()
        {
            var definition = Resources.Load<UnitDefinition>("Units/turret");
            Assert.IsNotNull(definition, "Units/turret must exist in Resources.");
            Assert.AreNotEqual(FactionType.Player, definition.fraction,
                "This test exists because the `turret` definition is NOT the player's. If that ever " +
                "changes, the placement override is untested and this test must be re-pointed at " +
                "another id.");

            RoomTemplate camp = Resources.Load<RoomTemplate>("Rooms/rooms_rbl/room_0_0");
            RoomInstance room = MakeRoom("camp_override_instance");
            RoomPopulator.PopulateRoom(room, camp, room.difficulty);

            List<UnitInstance> turrets = TurretPlacements(room);
            Assert.Greater(turrets.Count, 0);

            bool overrode = false;
            for (int i = 0; i < turrets.Count; i++)
            {
                if (turrets[i].fraction == FactionType.Player) overrode = true;
            }

            Assert.IsTrue(overrode,
                "At least one camp turret must read Player from its placement while its definition " +
                "says " + definition.fraction + " — otherwise nothing here exercises the override.");
        }

        [Test]
        public void CampRoom_SpawnedTurret_IsThePlayersFaction()
        {
            RoomTemplate camp = Resources.Load<RoomTemplate>("Rooms/rooms_rbl/room_0_0");
            Assert.IsNotNull(camp, "The camp room must exist in Resources.");

            RoomInstance room = MakeRoom("camp_spawn_instance");
            RoomPopulator.PopulateRoom(room, camp, room.difficulty);

            var spawner = new RoomUnitSpawner(room, _parent);
            spawner.RefreshAll();

            TurretController[] spawned = _parent.GetComponentsInChildren<TurretController>(true);
            Assert.Greater(spawned.Length, 0,
                "RoomUnitSpawner must instantiate the camp's turrets — nothing else turns room.units " +
                "into live GameObjects.");

            for (int i = 0; i < spawned.Length; i++)
            {
                Assert.AreEqual(FactionType.Player, spawned[i].Faction,
                    $"'{spawned[i].name}' spawned in the camp but reports faction " +
                    $"{spawned[i].Faction}. UnitController.Faction must prefer the placement " +
                    "(UnitTurret.as:140-142) over the shared UnitDefinition asset.");
            }
        }

        /// <summary>
        /// The complement, from real shipped data: 93 of the 97 <c>turret</c> placements in the game
        /// carry <b>no</b> <c>fraction</c> at all, and those are hostile. If the placement override
        /// leaked a default, this would go green where it must go red.
        /// </summary>
        [Test]
        public void HostileTurret_WithASilentPlacement_IsNotThePlayersFaction()
        {
            RoomTemplate hostile = Resources.Load<RoomTemplate>("Rooms/rooms_art/room_5_1");
            Assert.IsNotNull(hostile, "Rooms/rooms_art/room_5_1 must exist in Resources.");

            RoomInstance room = MakeRoom("hostile_turret_instance");
            RoomPopulator.PopulateRoom(room, hostile, room.difficulty);

            List<UnitInstance> turrets = TurretPlacements(room);
            Assert.Greater(turrets.Count, 0, "room_5_1 authors a turret with no fraction.");

            for (int i = 0; i < turrets.Count; i++)
            {
                Assert.IsNull(turrets[i].fraction,
                    $"'{turrets[i].unitId}' authors no fraction, so the placement must stay silent " +
                    "rather than inventing a value — the definition is a real answer here.");
            }

            var spawner = new RoomUnitSpawner(room, _parent);
            spawner.RefreshAll();

            TurretController[] spawned = _parent.GetComponentsInChildren<TurretController>(true);
            Assert.Greater(spawned.Length, 0);

            for (int i = 0; i < spawned.Length; i++)
            {
                Assert.AreNotEqual(FactionType.Player, spawned[i].Faction,
                    $"'{spawned[i].name}' has a silent placement, so it must fall back to its " +
                    "definition — which is not the player's. A Player here means the override is " +
                    "writing a default instead of only writing what the row authored.");
            }
        }

        private static string Describe(List<UnitInstance> units)
        {
            if (units.Count == 0) return "<none>";
            var parts = new List<string>(units.Count);
            for (int i = 0; i < units.Count; i++)
            {
                parts.Add($"{units[i].unitId}(fraction={(units[i].fraction.HasValue ? units[i].fraction.Value.ToString() : "<none>")})");
            }

            return string.Join(", ", parts);
        }
    }
}
