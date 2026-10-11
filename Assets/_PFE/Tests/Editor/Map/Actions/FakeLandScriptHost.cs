using PFE.Systems.Map.Scripting;

namespace PFE.Tests.Editor.Map.Actions
{
    /// <summary>
    /// Test double for <see cref="ILandScriptHost"/>, used here for the one verb the dispatcher drives:
    /// <c>GotoNextLevel</c>.
    ///
    /// <para><b>It counts the verb rather than recording a boolean</b> so a test can tell "the action ran
    /// once" from "the action ran and the caller then did something else that also advanced the level".
    /// The descent loop's whole failure mode is a double advance — the exit box and the exit room's own
    /// exit box are two different objects, and a wiring mistake that fires both would rebuild the land
    /// twice per exit while every individual assertion still looked green.</para>
    ///
    /// <para>The other six verbs are no-ops with counters: they exist only because the interface has
    /// them, and a test that finds one of them called has found a bug.</para>
    /// </summary>
    internal sealed class FakeLandScriptHost : ILandScriptHost
    {
        public int GotoNextLevelCalls;
        public int UpLandLevelCalls;
        public int OpenLandCalls;
        public int RefillCalls;
        public int SetTriggerCalls;
        public int MarkPassedCalls;
        public int GotoLandCalls;

        /// <summary>What <see cref="UpLandLevel"/> reports. AS3's <c>upLandLevel</c> is once per entry.</summary>
        public bool UpLandLevelResult = true;

        public bool UpLandLevel()
        {
            UpLandLevelCalls++;
            return UpLandLevelResult;
        }

        public bool OpenLand(string landId)
        {
            OpenLandCalls++;
            return true;
        }

        public void RefillVendors()
        {
            RefillCalls++;
        }

        public void SetTrigger(string triggerName, int value)
        {
            SetTriggerCalls++;
        }

        public void MarkPassed()
        {
            MarkPassedCalls++;
        }

        public void GotoNextLevel()
        {
            GotoNextLevelCalls++;
        }

        public void GotoLand(string landId, int n, string coordinates)
        {
            GotoLandCalls++;
        }
    }
}
