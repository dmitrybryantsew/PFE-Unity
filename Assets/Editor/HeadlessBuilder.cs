using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using System.IO;
using System.Linq;

public class HeadlessBuilder
{
    /// <summary>
    /// Release player build. Carries NO Development flag, so every custom ProfilerMarker is
    /// compiled away ([Conditional]) and any profiling probe built this way reports silent zeros.
    /// Never use this output for profiling - use <see cref="BuildDevelopment"/>.
    /// </summary>
    public static void Build()
    {
        BuildInto("Builds/PFE_Demo.exe", BuildOptions.StrictMode, exitWhenDone: true);
    }

    /// <summary>
    /// Development player build, written to a separate folder so it cannot clobber the release
    /// output. Keeps ProfilerMarkers, debug symbols and the FrameTimingManager (the latter is
    /// always active in a Development Player, with no Player Setting needed).
    /// </summary>
    public static void BuildDevelopment()
    {
        BuildInto("Builds/PFE_Dev/PFE_Unity.exe", BuildOptions.Development, exitWhenDone: true);
    }

    /// <summary>
    /// Same build as <see cref="BuildDevelopment"/>, reachable from the Editor menu.
    ///
    /// WHY THIS EXISTS: the -batchmode route cannot run while the Editor has this project open.
    /// Unity takes a lock on the project, so a second (batchmode) instance exits immediately with
    /// code 1 *before* the engine even initialises - the log stops right after
    /// "Successfully changed project path to" and never reaches "Initialize engine version".
    /// That is not a build failure and it produces no compile errors to read; it just silently
    /// does nothing. Verified twice in this repo: `headless_build.log` (6000.3.2f1, 2026-01-30)
    /// and `Logs/build-dev.log` (6000.3.10f1, 2026-09-28) fail identically.
    ///
    /// So: if Unity is already open, use this menu item instead of the command line.
    /// The CLI path remains for CI, where nothing holds the lock.
    ///
    /// AND WITH THE LOCK GONE THE CLI STILL DOES NOT WORK ON THIS MACHINE (measured 2026-09-28).
    /// Two separate failures, in order. First UPM: the Editor reaches "Initialize engine version"
    /// and "Input System module state changed to: Initialized" (so it is emphatically NOT the lock
    /// failure above), then dies with
    ///     [Package Manager] Server process stopped with exit code `101`
    ///     [Package Manager] Could not connect to IPC stream "Upm-&lt;pid&gt;" after 30.0 seconds.
    ///     Exiting without the bug reporter. Application will terminate with return code 1
    /// `-noUpm` (documented in EditorCommandLineArguments) gets past that. But then it deadlocks:
    ///     Application.AssetDatabase Initial Refresh Start
    /// and stops forever, in all six configurations tried - -batchmode and interactive, with and
    /// without -noUpm, with -nographics, with and without the agent sandbox, and with the full
    /// Windows environment exported (the agent shell lacks NUMBER_OF_PROCESSORS, windir, SystemRoot,
    /// SystemDrive and COMPUTERNAME; supplying them changes nothing). Unity is idle at ~0.05 s CPU
    /// per 10 s, the disk is idle, all threads are in Wait, and NO child process is created - no
    /// AssetImportWorker, no build tool. A working Editor session does spawn AssetImportWorker1/2
    /// (see Logs/unity_procs.txt), which is exactly why this builds from the Editor and not from a
    /// CLI launch. Until that is understood, produce players from the Editor, not the command line.
    /// </summary>
    [MenuItem("Tools/PFE/Build Development Player")]
    private static void BuildDevelopmentFromMenu()
    {
        BuildInto("Builds/PFE_Dev/PFE_Unity.exe", BuildOptions.Development, exitWhenDone: false);
    }

    /// <summary>
    /// <paramref name="exitWhenDone"/> is true for command-line invocation and false for the menu
    /// item. It matters: <see cref="EditorApplication.Exit"/> terminates the whole Editor process,
    /// which is correct for -batchmode and catastrophic when a human clicked a menu item.
    /// </summary>
    private static void BuildInto(string buildPath, BuildOptions options, bool exitWhenDone)
    {
        // 1. Ensure output directory exists
        string buildDir = Path.GetDirectoryName(buildPath);
        if (!Directory.Exists(buildDir)) Directory.CreateDirectory(buildDir);

        // 2. Find or Create a Scene to build
        string[] scenes = GetBuildScenes();
        
        if (scenes.Length == 0)
        {
            Debug.LogError("[HeadlessBuilder] No scenes found to build!");
            if (exitWhenDone) EditorApplication.Exit(1);
            return;
        }

        Debug.Log($"[HeadlessBuilder] Building with scenes: {string.Join(", ", scenes)}");
        Debug.Log($"[HeadlessBuilder] Output: {buildPath}  Options: {options}");

        // 3. Configure Build Options
        BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions();
        buildPlayerOptions.scenes = scenes;
        buildPlayerOptions.locationPathName = buildPath;
        buildPlayerOptions.target = BuildTarget.StandaloneWindows64;
        buildPlayerOptions.options = options;

        // 4. Run Build
        UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
        
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.Log($"### BUILD SUCCEEDED ### {buildPath} ({report.summary.totalSize} bytes)");
            if (exitWhenDone) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("### BUILD FAILED ###");
            foreach (var step in report.steps)
            {
                foreach (var msg in step.messages)
                {
                    if (msg.type == LogType.Error || msg.type == LogType.Exception)
                    {
                        Debug.LogError($"[BuildError] {msg.content}");
                    }
                }
            }
            if (exitWhenDone) EditorApplication.Exit(1);
        }
    }

    private static string[] GetBuildScenes()
    {
        // Priority 1: Use scenes already defined in Build Settings
        string[] buildSettingsScenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();
            
        if (buildSettingsScenes.Length > 0) return buildSettingsScenes;

        // Priority 2: Find "Main.unity" or "SampleScene.unity" in file system
        string[] searchPatterns = new[] { "Main.unity", "SampleScene.unity", "*.unity" };
        foreach (var pattern in searchPatterns)
        {
            string[] found = Directory.GetFiles("Assets", pattern, SearchOption.AllDirectories);
            if (found.Length > 0)
            {
                // Return the first valid scene found
                return new[] { found[0].Replace("\\", "/") };
            }
        }

        // Priority 3: Create a dummy scene if absolutely nothing exists
        Debug.LogWarning("[HeadlessBuilder] No scenes found. Creating 'Bootstrap.unity'...");
        string scenePath = "Assets/Scenes/Bootstrap.unity";
        Directory.CreateDirectory("Assets/Scenes");
        
        var newScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        EditorSceneManager.SaveScene(newScene, scenePath);
        
        return new[] { scenePath };
    }
}