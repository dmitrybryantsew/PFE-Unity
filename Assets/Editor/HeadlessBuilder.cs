using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using System.IO;
using System.Linq;

public class HeadlessBuilder
{
    public static void Build()
    {
        // 1. Ensure output directory exists
        string buildPath = "Builds/PFE_Demo.exe";
        string buildDir = Path.GetDirectoryName(buildPath);
        if (!Directory.Exists(buildDir)) Directory.CreateDirectory(buildDir);

        // 2. Find or Create a Scene to build
        string[] scenes = GetBuildScenes();
        
        if (scenes.Length == 0)
        {
            Debug.LogError("[HeadlessBuilder] No scenes found to build!");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log($"[HeadlessBuilder] Building with scenes: {string.Join(", ", scenes)}");

        // 3. Configure Build Options
        BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions();
        buildPlayerOptions.scenes = scenes;
        buildPlayerOptions.locationPathName = buildPath;
        buildPlayerOptions.target = BuildTarget.StandaloneWindows64;
        buildPlayerOptions.options = BuildOptions.StrictMode;

        // 4. Run Build
        UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
        
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.Log("### BUILD SUCCEEDED ###");
            EditorApplication.Exit(0);
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
            EditorApplication.Exit(1);
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