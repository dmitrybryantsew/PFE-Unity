using System;
using MoonSharp.Interpreter;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Core.Scripting;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Tests.Scripting
{
    [TestFixture]
    public sealed class DeveloperConsoleTests
    {
        private IRngService _rngService;
        private MoonSharpScriptEngine _engine;
        private DeveloperConsoleService _console;
        private LandMap _landMap;
        private bool _fogDisabled;
        private bool _fogRevealed;

        [SetUp]
        public void SetUp()
        {
            _rngService = new PcgRngService(0x12345678UL);
            _engine = new MoonSharpScriptEngine(_rngService);

            _landMap = new LandMap();
            _landMap.Initialize(new Vector3Int(-10, -10, 0), new Vector3Int(10, 10, 1));

            // Add 3 sample rooms
            for (int i = 0; i < 3; i++)
            {
                var room = new RoomInstance
                {
                    id = $"room_{i}",
                    landPosition = new Vector3Int(i, 0, 0),
                    isVisited = false
                };
                _landMap.AddRoom(room, room.landPosition);
            }

            _fogDisabled = false;
            _fogRevealed = false;

            _console = new DeveloperConsoleService(_engine, _landMap);
            _console.SetDependencies(
                _engine,
                _landMap,
                setFogDisabledAction: disabled => _fogDisabled = disabled,
                revealFogAction: () => _fogRevealed = true,
                isFogDisabledFunc: () => _fogDisabled);
        }

        [TearDown]
        public void TearDown()
        {
            _engine?.Dispose();
        }

        [Test]
        public void ExecuteInput_MapCommand_RevealsAllLandMapRooms()
        {
            // Verify initially unvisited
            foreach (var r in _landMap.GetAllRooms())
            {
                Assert.That(r.isVisited, Is.False);
            }
            Assert.That(_landMap.DrawAllMap, Is.False);

            string result = _console.ExecuteInput("map");

            Assert.That(result, Does.Contain("Revealed 3 rooms"));
            Assert.That(_landMap.DrawAllMap, Is.True);
            foreach (var r in _landMap.GetAllRooms())
            {
                Assert.That(r.isVisited, Is.True);
            }
            Assert.That(_fogDisabled, Is.True);
            Assert.That(_fogRevealed, Is.True);
        }

        [Test]
        public void ExecuteInput_FogToggle_TogglesFogState()
        {
            Assert.That(_fogDisabled, Is.False);

            // Toggle 1: should disable fog (reveal)
            string r1 = _console.ExecuteInput("fog");
            Assert.That(_fogDisabled, Is.True);
            Assert.That(r1, Does.Contain("DISABLED (revealed)"));

            // Toggle 2: should re-enable fog
            string r2 = _console.ExecuteInput("fog");
            Assert.That(_fogDisabled, Is.False);
            Assert.That(r2, Does.Contain("ENABLED"));
        }

        [Test]
        public void ExecuteInput_FogExplicitCommands_SetState()
        {
            _console.ExecuteInput("fog off");
            Assert.That(_fogDisabled, Is.True);

            _console.ExecuteInput("fog on");
            Assert.That(_fogDisabled, Is.False);
        }

        [Test]
        public void ExecuteInput_HelpCommand_ReturnsHelpMenu()
        {
            string help = _console.ExecuteInput("help");
            Assert.That(help, Does.Contain("map"));
            Assert.That(help, Does.Contain("fog"));
            Assert.That(help, Does.Contain("clear"));
        }

        [Test]
        public void ExecuteInput_HelpCommand_ListsSaveVerbs()
        {
            string help = _console.ExecuteInput("help");
            Assert.That(help, Does.Contain("save"));
            Assert.That(help, Does.Contain("load"));
        }

        [Test]
        public void ExecuteInput_SaveVerb_IsDispatchedToSaveCommandsNotLua()
        {
            // A bare (unwired) command object: every dependency is null, so Save() returns its
            // "GameManager not available" line. That the reply is a [save] message at all is the
            // assertion - an unregistered verb would fall through to Lua and come back as "nil".
            _console.SetCommandObjects(null, null, new DevConsoleSaveCommands());

            string save = _console.ExecuteInput("save");
            Assert.That(save, Does.StartWith("[save]"));

            string status = _console.ExecuteInput("saves");
            Assert.That(status, Does.StartWith("[save]"));

            // 'load' on an unwired object must report the missing GameManager, not a load failure.
            string load = _console.ExecuteInput("load");
            Assert.That(load, Does.StartWith("[save]"));
        }

        [Test]
        public void ExecuteInput_SaveVerb_WithoutCommandObjects_FallsThroughToLua()
        {
            // The console can be opened long before the container is wired, so an unregistered
            // save verb must degrade to Lua evaluation rather than throwing. The assertion is
            // deliberately about the *absence* of a save-command reply: whether the Lua fallback
            // yields nil or a Lua error is not this test's business.
            Assert.That(_console.ExecuteInput("save"), Does.Not.StartWith("[save]"));
        }

        [Test]
        public void PlayerArmourCommands_WithoutWiring_DegradeToMessagesNotExceptions()
        {
            // The controller's own doc comment promises this: a console opened while the container is
            // only half-built answers "that command is unavailable" rather than throwing from the tool
            // you opened to debug the problem. Armour is the case that matters most right now, because
            // nothing in production wires any of it yet — so this is the state a tester will actually hit.
            //
            // A bare command object: every dependency is null, and there is no PlayerController in the
            // scene, so the provider's FindFirstObjectByType fallback returns null too.
            _console.SetCommandObjects(new DevConsolePlayerCommands(), null, null);

            Assert.That(_console.ExecuteInput("return player:ArmourIds()"),
                Does.Contain("No GameDatabase"), "The lookup must report the missing database, not throw.");

            Assert.That(_console.ExecuteInput("return player:EquipArmour('metal')"),
                Does.Contain("No player in scene"));

            Assert.That(_console.ExecuteInput("return player:UnequipArmour()"),
                Does.Contain("No player in scene"));

            Assert.That(_console.ExecuteInput("return player:WearArmour(50)"),
                Does.Contain("No player in scene"));

            Assert.That(_console.ExecuteInput("return player:Armour()"),
                Does.Contain("No player in scene"));
        }

        [Test]
        public void ExecuteInput_LuaCode_EvaluatesCorrectly()
        {
            string result = _console.ExecuteInput("return 33 * 3");
            Assert.That(result, Is.EqualTo("99"));
        }

        [Test]
        public void LuaStandardLibrary_PfeRevealMapAndToggleFog_Works()
        {
            // Call pfe.reveal_map() from Lua
            DynValue result = _engine.ExecuteString("return pfe.reveal_map()");
            Assert.That(result.String, Does.Contain("Revealed 3 rooms"));
            Assert.That(_landMap.DrawAllMap, Is.True);
            Assert.That(_fogDisabled, Is.True);

            // Call pfe.toggle_fog() from Lua - since fog was disabled (revealed) by reveal_map, toggling re-enables it
            DynValue fogResult = _engine.ExecuteString("return pfe.toggle_fog()");
            Assert.That(fogResult.String, Does.Contain("Fog of war"));
            Assert.That(_fogDisabled, Is.False);

            // Toggling again disables fog (reveals)
            _engine.ExecuteString("return pfe.toggle_fog()");
            Assert.That(_fogDisabled, Is.True);
        }
    }
}
