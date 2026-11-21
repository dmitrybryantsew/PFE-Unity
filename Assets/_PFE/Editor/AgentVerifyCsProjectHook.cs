#if UNITY_EDITOR
using System.IO;
using UnityEditor;

namespace PFE.Editor
{
    /// <summary>
    /// Re-adds the agentverify fixtures to the generated csproj on every regeneration.
    ///
    /// <para><b>Why this exists.</b> The offline fixtures under <c>.workbuddy-ai/tools/agentverify/</c>
    /// live outside <c>Assets/</c>, so Unity's csproj generator never lists them — and the generated
    /// header warns that a hand edit is overwritten on the next regeneration. The wiring lint
    /// (<c>PfeNetSessionWiringLintTests.InterpolationRulesArePureAndCompiledIntoBothBuilds</c>) pins
    /// the interpolation fixture's <c>&lt;Compile Include&gt;</c> in <c>Assembly-CSharp.csproj</c>
    /// precisely because an unlisted file is a 0-error build that compiled nothing; without this hook
    /// that lint would go red the first time the editor regenerates the file after a hand add.</para>
    ///
    /// <para><b>Only Assembly-CSharp.csproj is touched.</b> The offline wall project is hand-maintained
    /// and already carries the fixture; every other generated project neither needs nor can reference
    /// the agentverify sources.</para>
    /// </summary>
    internal sealed class AgentVerifyCsProjectHook : AssetPostprocessor
    {
        // Pinned by PfeNetSessionWiringLintTests against Assembly-CSharp.csproj (and the
        // hand-maintained AgentVerifyWall.csproj). Backslash form, exactly as the generator writes.
        private const string InterpolationFixtureItem =
            "<Compile Include=\".workbuddy-ai\\tools\\agentverify\\pfenet\\PfeRemoteInterpolationRulesTests.cs\" />";

        private const string ProjectClosingTag = "</Project>";

        // AssetPostprocessor callback: Unity invokes this static method for every generated csproj.
        // It must live on an AssetPostprocessor-derived class — a plain static class is not
        // discovered, which is exactly how this hook silently never ran.
        public static string OnGeneratedCSProject(string path, string content)
        {
            if (Path.GetFileName(path) != "Assembly-CSharp.csproj") return content;
            if (content.Contains(InterpolationFixtureItem)) return content;

            int insertAt = content.LastIndexOf(ProjectClosingTag, System.StringComparison.Ordinal);
            if (insertAt < 0) return content;

            const string itemGroup =
                "  <ItemGroup>\r\n    " + InterpolationFixtureItem + "\r\n  </ItemGroup>\r\n";
            return content.Insert(insertAt, itemGroup);
        }
    }
}
#endif
