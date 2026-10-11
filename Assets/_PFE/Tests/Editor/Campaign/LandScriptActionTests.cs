using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Scripting;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// Asserts the campaign-level half of the script vocabulary in <see cref="AreaTriggerSystem"/>:
    /// <c>upland</c>, <c>openland</c>, <c>refill</c>, <c>trigger</c>, <c>passed</c>, and the
    /// <c>@n</c> branch of <c>gotoland</c> (<c>Script.as:392-472</c>).
    ///
    /// <para>These are the actions <c>03_GAP_LEDGER.md</c> §8 names as the four that matter for the loop,
    /// plus the <c>gotoland</c> divergence D8.</para>
    ///
    /// <para><b><c>exit</c> is deliberately absent from that list</b>, and
    /// <see cref="Exit_IsNotARoomScriptAndStaysUnhandledHere"/> pins that it stays absent. It used to be
    /// listed here — see that test for why listing it was the bug.</para>
    /// </summary>
    [TestFixture]
    public class LandScriptActionTests
    {
        private sealed class FakeHost : ILandScriptHost
        {
            public int UpLandLevelCalls;
            public bool UpLandLevelResult = true;

            public int OpenLandCalls;
            public string LastOpenLandId;
            public bool OpenLandResult = true;

            public int RefillCalls;
            public int PassedCalls;
            public int GotoNextLevelCalls;

            public string LastTriggerName;
            public int LastTriggerValue = int.MinValue;

            public string LastGotoLandId;
            public int LastGotoLandN = int.MinValue;
            public string LastGotoLandCoordinates = "«unset»";
            public int GotoLandCalls;

            public bool UpLandLevel() { UpLandLevelCalls++; return UpLandLevelResult; }

            public bool OpenLand(string landId) { OpenLandCalls++; LastOpenLandId = landId; return OpenLandResult; }

            public void RefillVendors() { RefillCalls++; }

            public void SetTrigger(string triggerName, int value) { LastTriggerName = triggerName; LastTriggerValue = value; }

            public void MarkPassed() { PassedCalls++; }

            public void GotoNextLevel() { GotoNextLevelCalls++; }

            public void GotoLand(string landId, int n, string coordinates)
            {
                GotoLandCalls++;
                LastGotoLandId = landId;
                LastGotoLandN = n;
                LastGotoLandCoordinates = coordinates;
            }
        }

        private static RoomInstance MakeRoom()
        {
            return new RoomInstance { id = "TestRoom", width = WorldConstants.ROOM_WIDTH, height = WorldConstants.ROOM_HEIGHT };
        }

        private static MapObjectScriptActionData Action(string act, string val = null, string n = null,
            string opt1 = null, string opt2 = null)
        {
            return new MapObjectScriptActionData { act = act, val = val, n = n, opt1 = opt1, opt2 = opt2 };
        }

        // =====================================================================
        //  upland — the descent
        // =====================================================================

        [Test]
        public void Upland_ReachesTheHostAndReportsWhetherTheStageMoved()
        {
            var host = new FakeHost { UpLandLevelResult = true };
            var system = new AreaTriggerSystem(null, null, null, null, host);
            bool? reported = null;
            system.OnUpLandLevel += moved => reported = moved;

            system.ExecuteAction(MakeRoom(), Action("upland"));

            Assert.That(host.UpLandLevelCalls, Is.EqualTo(1), "AS3 `upland` → Game.upLandLevel (Script.as:396-399).");
            Assert.That(reported, Is.True, "The increment result must be observable.");
        }

        [Test]
        public void Upland_SecondCallReportsFalse()
        {
            // The upStage guard lives in the registry; the fake mirrors it so the wiring is what is tested.
            var host = new FakeHost { UpLandLevelResult = false };
            var system = new AreaTriggerSystem(null, null, null, null, host);
            var results = new List<bool>();
            system.OnUpLandLevel += moved => results.Add(moved);

            system.ExecuteAction(MakeRoom(), Action("upland"));
            system.ExecuteAction(MakeRoom(), Action("upland"));

            Assert.That(results, Is.EqualTo(new List<bool> { false, false }));
            Assert.That(host.UpLandLevelCalls, Is.EqualTo(2));
        }

        [Test]
        public void Upland_WithoutAHost_DoesNotThrowAndReportsNoMove()
        {
            var system = new AreaTriggerSystem();
            bool? reported = null;
            system.OnUpLandLevel += moved => reported = moved;

            Assert.DoesNotThrow(() => system.ExecuteAction(MakeRoom(), Action("upland")));
            Assert.That(reported, Is.False, "No host means no increment — and no exception.");
        }

        // =====================================================================
        //  openland
        // =====================================================================

        [Test]
        public void OpenLand_ReachesTheHostWithTheLandId()
        {
            var host = new FakeHost();
            var system = new AreaTriggerSystem(null, null, null, null, host);
            string opened = null;
            system.OnOpenLand += id => opened = id;

            system.ExecuteAction(MakeRoom(), Action("openland", val: "nio"));

            Assert.That(host.OpenLandCalls, Is.EqualTo(1));
            Assert.That(host.LastOpenLandId, Is.EqualTo("nio"), "Script.as:458-468 reads @val.");
            Assert.That(opened, Is.EqualTo("nio"));
        }

        // =====================================================================
        //  refill / trigger / passed / exit
        // =====================================================================

        [Test]
        public void Refill_ReachesTheHost()
        {
            var host = new FakeHost();
            var system = new AreaTriggerSystem(null, null, null, null, host);

            system.ExecuteAction(MakeRoom(), Action("refill"));

            Assert.That(host.RefillCalls, Is.EqualTo(1), "Script.as:392-395 → Land.refill().");
        }

        [Test]
        public void Trigger_DefaultsToOneAndHonoursN()
        {
            var host = new FakeHost();
            var system = new AreaTriggerSystem(null, null, null, null, host);

            system.ExecuteAction(MakeRoom(), Action("trigger", val: "dial_calam2"));
            Assert.That(host.LastTriggerName, Is.EqualTo("dial_calam2"));
            Assert.That(host.LastTriggerValue, Is.EqualTo(1), "Script.as:424-434 — no @n means setTrigger(val).");

            system.ExecuteAction(MakeRoom(), Action("trigger", val: "storm", n: "4"));
            Assert.That(host.LastTriggerValue, Is.EqualTo(4), "…and @n overrides it.");
        }

        [Test]
        public void Passed_ReachesTheHost()
        {
            var host = new FakeHost();
            var system = new AreaTriggerSystem(null, null, null, null, host);

            system.ExecuteAction(MakeRoom(), Action("passed"));

            Assert.That(host.PassedCalls, Is.EqualTo(1), "Script.as:469-472 → land.act.passed = true.");
        }

        // =====================================================================
        //  exit — the one that is NOT here, and must not come back
        // =====================================================================

        /// <summary>
        /// A <b>negative control</b>: driving <c>ExecuteAction(act: "exit")</c> must reach nothing.
        ///
        /// <para><b>Why this replaced a positive test.</b> This fixture used to assert the opposite —
        /// that <c>AreaTriggerSystem</c> forwards <c>exit</c> to <c>ILandScriptHost.GotoNextLevel</c> —
        /// against an <c>if (command == "exit")</c> branch in <see cref="AreaTriggerSystem"/>. That
        /// branch was an invention and it was deleted, so the test went red and stayed red. It was wrong
        /// twice: it could never fire in a real room, and it advertised a script name the oracle does not
        /// have — which is how the next reader concludes that <c>exit</c> is a room script and wires the
        /// wrong half of the loop.</para>
        ///
        /// <para><b>The oracle.</b> <c>Script.run</c>'s chain is <c>hpbar, refill, upland, locon, locoff,
        /// quest, showstage, show, stage, trigger, goto, gotoland, openland, passed, actprob</c>
        /// (<c>Script.as:390-474</c>) and <c>exit</c> is not among them. No room authors one either:
        /// <c>grep -rn 'act="exit"' rooms/</c> finds nothing, and the port's imported room assets carry
        /// zero <c>act: exit</c>. The real <c>exit</c> is an <i>object's</i> <c>allact</c>
        /// (<c>&lt;obj id='exit' … allact='exit' n='Выход с уровня'/&gt;</c>, <c>AllData.as:5016</c> →
        /// <c>Interact.as:1646-1649</c> → <c>Game.gotoNextLevel</c>, <c>Game.as:464-472</c>).</para>
        ///
        /// <para><b>Where the positive half lives</b> — the branch that really advances the level, and
        /// the ordering that makes it the <i>second</i> hop of the descent:
        /// <c>ObjectActionDispatcherTests.CreateDefault_RegistersExitAndDispatchesItThroughTheLandScriptHost</c>
        /// (the exit object's <c>allact</c> advances the level exactly once, and does <b>not</b> increment
        /// <c>landStage</c>) and
        /// <c>ObjectActionDispatcherTests.Dispatch_ObjectWithAProb_EntersTheProbRoomInsteadOfRunningItsAllAct</c>
        /// (the bottom-row exit box carries <c>prob='exit_plant'</c> <i>and</i> inherits <c>allact='exit'</c>,
        /// and the <c>prob</c> branch wins — which is what routes the player through the exit room and its
        /// <c>upland</c> instead of rebuilding the land at the same stage for ever).</para>
        ///
        /// <para>This is the only shape that can still go red here: re-add the branch and this fails.</para>
        /// </summary>
        [Test]
        public void Exit_IsNotARoomScriptAndStaysUnhandledHere()
        {
            var host = new FakeHost();
            var system = new AreaTriggerSystem(null, null, null, null, host);

            system.ExecuteAction(MakeRoom(), Action("exit"));

            Assert.That(host.GotoNextLevelCalls, Is.EqualTo(0),
                "Script.as:390-474 has no `exit` branch — the exit box is an object `allact`, not a room " +
                "script, and it is dispatched by ObjectActionDispatcher → ExitAction.");
        }

        // =====================================================================
        //  gotoland and @n (divergence D8)
        // =====================================================================

        [Test]
        public void GotoLand_PlainCarriesNoCoordinates()
        {
            var host = new FakeHost();
            var system = new AreaTriggerSystem(null, null, null, null, host);

            system.ExecuteAction(MakeRoom(), Action("gotoland", val: "surf"));

            Assert.That(host.GotoLandCalls, Is.EqualTo(1));
            Assert.That(host.LastGotoLandId, Is.EqualTo("surf"));
            Assert.That(host.LastGotoLandN, Is.EqualTo(0));
            Assert.That(host.LastGotoLandCoordinates, Is.Null);
        }

        [Test]
        public void GotoLand_N1_CarriesTheOptCoordinatePair()
        {
            var host = new FakeHost();
            var system = new AreaTriggerSystem(null, null, null, null, host);

            system.ExecuteAction(MakeRoom(), Action("gotoland", val: "nio", n: "1", opt1: "3", opt2: "4"));

            Assert.That(host.LastGotoLandN, Is.EqualTo(1));
            Assert.That(host.LastGotoLandCoordinates, Is.EqualTo("3:4"),
                "Script.as:449-452 — @n='1' enters at @opt1:@opt2.");
        }

        [Test]
        public void GotoLand_N2_IsAForcedRegenerate()
        {
            var host = new FakeHost();
            var system = new AreaTriggerSystem(null, null, null, null, host);

            system.ExecuteAction(MakeRoom(), Action("gotoland", val: "nio", n: "2"));

            Assert.That(host.LastGotoLandN, Is.EqualTo(2), "Script.as:445-448 — @n='2' forces a rebuild.");
            Assert.That(host.LastGotoLandCoordinates, Is.Null);
        }

        [Test]
        public void GotoLand_WithoutAHost_StillFiresTheLegacyEvent()
        {
            var system = new AreaTriggerSystem();
            string received = null;
            system.OnGotoLand += land => received = land;

            system.ExecuteAction(MakeRoom(), Action("gotoland", val: "surf"));

            Assert.That(received, Is.EqualTo("surf"),
                "The event path must survive for scenes with no campaign host wired.");
        }
    }
}
