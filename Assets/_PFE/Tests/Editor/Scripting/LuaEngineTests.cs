using System;
using MoonSharp.Interpreter;
using NUnit.Framework;
using PFE.Core.Ids;
using PFE.Core.Rng;
using PFE.Core.Scripting;
using PFE.Systems.Map;
using PFE.Systems.Map.Scripting;
using UnityEngine;
using EntityId = PFE.Core.Ids.EntityId;

namespace PFE.Tests.Scripting
{
    [TestFixture]
    public sealed class LuaEngineTests
    {
        private IRngService _rngService;
        private MoonSharpScriptEngine _engine;

        [SetUp]
        public void SetUp()
        {
            _rngService = new PcgRngService(0x12345678UL);
            _engine = new MoonSharpScriptEngine(_rngService);
        }

        [TearDown]
        public void TearDown()
        {
            _engine?.Dispose();
        }

        [Test]
        public void ExecuteString_BasicMath_ReturnsCorrectResult()
        {
            DynValue result = _engine.ExecuteString("return 15 + 27");
            Assert.That(result.Type, Is.EqualTo(DataType.Number));
            Assert.That(result.Number, Is.EqualTo(42));
        }

        [Test]
        public void Sandboxing_BansOSAndIO()
        {
            // Preset_SoftSandbox must not expose dangerous OS/IO tables
            Script sandboxed = _engine.CreateScript(LuaSandboxPolicy.Default);

            DynValue osVal = sandboxed.Globals.Get("os");
            Assert.That(osVal.IsNil(), Is.True, "os library must not be accessible in soft sandbox.");

            DynValue ioVal = sandboxed.Globals.Get("io");
            Assert.That(ioVal.IsNil(), Is.True, "io library must not be accessible in soft sandbox.");
        }

        [Test]
        public void DeterministicRng_MatchesPcgRngService()
        {
            // Verify that calling math.random in Lua draws directly from PFE's IRngService
            ulong seed = 0xABCDEF0123456789UL;
            var expectedRng = new PcgRngService(seed);
            var actualRng = new PcgRngService(seed);

            using var engine = new MoonSharpScriptEngine(actualRng);
            Script script = engine.CreateScript(LuaSandboxPolicy.Default);

            for (int i = 0; i < 10; i++)
            {
                float expected = expectedRng.NextFloat();
                DynValue luaVal = script.DoString("return math.random()");
                Assert.That((float)luaVal.Number, Is.EqualTo(expected).Within(0.00001f));
            }
        }

        [Test]
        public void DeterministicRng_RangeMatchesExpected()
        {
            ulong seed = 0x9876543210FEDCBAUL;
            var expectedRng = new PcgRngService(seed);
            var actualRng = new PcgRngService(seed);

            using var engine = new MoonSharpScriptEngine(actualRng);
            Script script = engine.CreateScript(LuaSandboxPolicy.Default);

            for (int i = 0; i < 10; i++)
            {
                int expected = expectedRng.Range(10, 51);
                DynValue luaVal = script.DoString("return math.random(10, 50)");
                Assert.That((int)luaVal.Number, Is.EqualTo(expected));
            }
        }

        [Test]
        public void EntityId_RoundTrip_PreservesValue()
        {
            EntityId id = EntityId.CreateForRoomSpawn("r_01_02", "door", 3);
            _engine.SetGlobal("testId", id);

            DynValue result = _engine.ExecuteString("return testId");
            Assert.That(result.Type, Is.EqualTo(DataType.UserData));

            EntityId unwrapped = result.ToObject<EntityId>();
            Assert.That(unwrapped.Hash, Is.EqualTo(id.Hash));
            Assert.That(unwrapped.DebugString, Is.EqualTo(id.DebugString));
        }

        [Test]
        public void InstructionLimit_AbortsInfiniteLoop()
        {
            var policy = new LuaSandboxPolicy
            {
                InstructionLimit = 1000
            };

            var script = _engine.CreateScript(policy);

            Assert.Throws<ScriptRuntimeException>(() =>
            {
                script.DoString("while true do local a = 1 end");
            });
        }

        [Test]
        public void LuaTriggerBridge_OpensDoorCorrectly()
        {
            var room = new RoomInstance
            {
                id = "room_test",
                tiles = new TileData[10, 10]
            };

            for (int x = 0; x < 10; x++)
            {
                for (int y = 0; y < 10; y++)
                {
                    room.tiles[x, y] = new TileData { physicsType = TilePhysicsType.Air };
                }
            }

            var doorObj = new ObjectInstance
            {
                uid = "door_alpha",
                position = new Vector2(100f, 100f),
                runtimeState = new MapObjectRuntimeStateData { isOpen = false }
            };
            room.objects.Add(doorObj);

            var bridge = new LuaTriggerBridge(_engine);
            bridge.ExecuteTriggerScript(room, doorObj, "room.open_door('door_alpha')");

            Assert.That(doorObj.runtimeState.isOpen, Is.True, "Door should be marked open by Lua script.");
        }
    }
}
