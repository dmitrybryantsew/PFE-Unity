// Frame-driven dump trigger for PfeProfiler. Spawned by PfeProfiler.HookAfterSceneLoad when
// profiling is enabled, so boot can be measured without any scene setup.
//
// Two dumps: one at frame 5 (boot usually done) and one at frame 60 (in case init overruns).
// Class name matches the file name — Unity requires that for MonoBehaviours.

using UnityEngine;

namespace PFE.Core.Profiling
{
    public sealed class PfeProfilerAutoDump : MonoBehaviour
    {
        const int FirstFrame = 1;
        const int EarlyDumpFrame = 5;
        const int LateDumpFrame = 60;

        int _frames;

        void Start() => PfeProfiler.Mark("FirstBehaviour.Start");

        void Update()
        {
            _frames++;

            if (_frames == FirstFrame)
            {
                // Backstop for the boot.afterSceneLoadGap span opened by HookAfterSceneLoad.
                // GameManager.Start normally closes it. If Start never ran, or ran before the hook,
                // close it here — an open span left on the frame stack would swallow every later
                // region as a child of itself and quietly ruin the whole report.
                PfeProfiler.CloseSpan();
                PfeProfiler.Mark("FirstUpdate");
            }
            else if (_frames == EarlyDumpFrame) PfeProfiler.Dump("early");
            else if (_frames == LateDumpFrame) PfeProfiler.Dump("late");
        }
    }
}
