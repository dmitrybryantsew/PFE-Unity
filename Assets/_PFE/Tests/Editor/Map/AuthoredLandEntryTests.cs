using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Tests for <see cref="MapBridge.ResolveDefaultEntryCell"/> — which room a land is entered at when
    /// the caller names none and no checkpoint applies.
    ///
    /// <para><b>The bug this pins.</b> The port answered that question with a shape heuristic
    /// (<c>ResolveEntrancePosition</c>): first <c>beg0</c> template, else the first template carrying a
    /// type-0 spawn point, else the first template with <c>x &gt;= 0</c>. The heuristic has no notion of
    /// the Z axis, and <c>rooms_nio</c>'s first spawned template is <c>bossraider</c> at (-1,0,<b>1</b>) —
    /// the upper layer of the same cell as the entrance corridor <c>room_-1_0</c> (-1,0,0). So every
    /// entry to <c>nio</c>, from the plant exit and from the land map alike, spawned the player inside
    /// the boss arena. The oracle does not guess: <c>Land.enterLand</c>'s else-branch is
    /// <c>locX = act.begLocX; locY = act.begLocY; locZ = 0</c> (<c>Land.as:1178-1181</c>), and
    /// <c>begLocX/begLocY</c> is the land's own <c>locx/locy</c> (<c>LandAct.as:157</c>).</para>
    ///
    /// <para><b>Why the fixture is shaped this way.</b> The oracle's rule is "the land wins, the
    /// templates are not consulted at all". The test therefore builds a collection whose <i>first</i>
    /// spawned template is the boss arena and whose entry cell is a different room — the exact shape that
    /// made the heuristic wrong — and asserts the answer is the land's cell, not the spawn-point room.
    /// A second test keeps the heuristic alive for an unresolved land, which is the only case it is for.
    /// </para>
    /// </summary>
    [TestFixture]
    public class AuthoredLandEntryTests
    {
        private static RoomTemplate Room(string id, Vector3Int position, bool playerSpawn = false)
        {
            var template = ScriptableObject.CreateInstance<RoomTemplate>();
            template.id = id;
            template.type = "pass";
            template.fixedPosition = position;

            if (playerSpawn)
            {
                template.spawnPoints = new List<SpawnPointData>
                {
                    new SpawnPointData
                    {
                        tileCoord = new Vector2Int(2, 15),
                        type = SpawnType.Player
                    }
                };
            }

            return template;
        }

        private static LandDefinition Land(string id, int x, int y)
        {
            var land = ScriptableObject.CreateInstance<LandDefinition>();
            land.landId = id;
            land.isProcedural = false;
            land.entryCoordinates = new Vector2Int(x, y);
            return land;
        }

        [Test]
        public void AnAuthoredLandEntersAtItsOwnLocxLocyNotAtTheFirstSpawnPoint()
        {
            // `nio` verbatim: locx=-1 locy=0 (GameData.as:171), and the collection lists the boss arena
            // (z=1, has a player spawn) before the entrance corridor (z=0). The old answer was
            // (-1,0,1) — the boss room. The oracle's answer is (-1,0,0) — room_-1_0.
            var templates = new List<RoomTemplate>
            {
                Room("bossraider", new Vector3Int(-1, 0, 1), playerSpawn: true),
                Room("room_-1_0", new Vector3Int(-1, 0, 0), playerSpawn: true)
            };

            Vector3Int cell = MapBridge.ResolveDefaultEntryCell(
                Land("nio", -1, 0), templates, "rooms_nio");

            Assert.AreEqual(new Vector3Int(-1, 0, 0), cell,
                "An authored land must enter at locx/locy with z=0. Landing on the spawn-point template " +
                "is how every nio entry ended up in the boss arena.");
        }

        [Test]
        public void TheEntryCellIsAlwaysTheGroundLayer()
        {
            // z is not an input anywhere in the oracle's rule: enterLand writes `locZ = 0` explicitly.
            // A land whose locx/locy coincide with a Z=1 room must still enter the ground layer, or the
            // fix above would just move the bug one layer down.
            var templates = new List<RoomTemplate>
            {
                Room("bossraider", new Vector3Int(2, 3, 1), playerSpawn: true)
            };

            Vector3Int cell = MapBridge.ResolveDefaultEntryCell(
                Land("somewhere", 2, 3), templates, "rooms_somewhere");

            Assert.AreEqual(new Vector3Int(2, 3, 0), cell);
        }

        [Test]
        public void AnUnresolvedLandStillUsesTheTemplateHeuristic()
        {
            // The control: with no LandDefinition there is no locx/locy to read, so the shape heuristic
            // is all that is left. It must keep working — this is the path the authored-fallback warning
            // in PerformLandTransition describes, and deleting the heuristic outright would strand it.
            var templates = new List<RoomTemplate>
            {
                Room("room_0_0", new Vector3Int(0, 0, 0)),
                Room("room_4_2", new Vector3Int(4, 2, 0), playerSpawn: true)
            };

            Vector3Int cell = MapBridge.ResolveDefaultEntryCell(null, templates, "rooms_unknown");

            Assert.AreEqual(new Vector3Int(4, 2, 0), cell,
                "With no land definition the first template carrying a player spawn is still the best " +
                "available answer.");
        }
    }
}
