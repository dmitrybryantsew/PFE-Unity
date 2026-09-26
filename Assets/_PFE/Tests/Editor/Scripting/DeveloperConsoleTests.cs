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
