using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Systems.Map.Rendering;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.EditMode.Systems.Map.Rendering
{
    /// <summary>
    /// Pins the <b>contract</b> between <see cref="RoomBackdropRenderer"/> and the two backdrop shaders:
    /// every uniform the C# sets must be declared by the shader it is set on.
    ///
    /// <para><b>The bug this exists to make findable.</b> <c>Material.SetFloat("_Darkness", …)</c> on a
    /// shader that does not declare <c>_Darkness</c> does not throw, does not warn and does not log. The
    /// material silently keeps the shader's declared default, and the room draws with the wrong darkness —
    /// or with no shadow at all. There is no compile error, no exception and nothing in the console, so the
    /// only signals are a picture that looks slightly off and this fixture. The same class of typo in a
    /// <i>uniform the CPU path also uses</i> is exactly what produced the 1.8×-too-bright build in doc 09
    /// §16.9, one step further along.</para>
    ///
    /// <para><b>Why a lint and not a behavioural test.</b> A material, a shader and a GPU cannot be stood up
    /// in this project's offline harness, so the behavioural test cannot run at all — and an unexecuted guard
    /// is the thing these lints exist to avoid (<c>SourceLint</c>). Reading both files is executable
    /// everywhere and fails on the exact edit that breaks the picture.</para>
    /// </summary>
    public sealed class BackdropShaderContractTests
    {
        private const string RendererPath = "Systems/Map/Rendering/RoomBackdropRenderer.cs";
        private const string WallShaderPath = "Data/Resources/RoomBackdrop/PFE_BackdropMultiply.shader";
        private const string DecorationShaderPath = "Data/Resources/RoomBackdrop/PFE_BackdropDecorationMultiply.shader";

        /// <summary>Matches <c>SetFloat("_Name", …)</c> and its siblings, capturing the uniform name.</summary>
        private static readonly Regex UniformSetCall = new Regex(
            "Set(?:Texture|Vector|Float|Color|Int)\\(\\s*\"(_[A-Za-z0-9_]+)\"",
            RegexOptions.Compiled);

        /// <summary>Matches a shader property declaration, capturing the name before its display string.</summary>
        private static readonly Regex PropertyDeclaration = new Regex(
            "(_[A-Za-z0-9_]+)\\s*\\(",
            RegexOptions.Compiled);

        // ---------------------------------------------------------------- wall

        [Test]
        public void EveryUniformTheWallMaterialSetsIsDeclaredByTheWallShader()
        {
            HashSet<string> set = UniformsSetBy("private Material GetBackdropMaterial(");
            HashSet<string> declared = ShaderProperties(WallShaderPath);

            // Positive controls on both scans. Without them a broken regex yields two empty sets, every
            // "all of these are in that" assertion passes, and the lint reports green on a file it never
            // read — the shape `SourceLint` was extracted to prevent.
            CollectionAssert.Contains(set, "_Darkness",
                "The uniform scan found no `_Darkness` in GetBackdropMaterial, so it is not reading the " +
                "method it thinks it is reading.");
            CollectionAssert.Contains(declared, "_ShadowTex",
                "The property scan found no `_ShadowTex` in the wall shader, so it is not reading the " +
                "Properties block it thinks it is reading.");

            AssertEveryUniformIsDeclared(set, declared, "PFE_BackdropMultiply.shader", "GetBackdropMaterial");
        }

        // ---------------------------------------------------------------- decorations

        [Test]
        public void EveryUniformTheDecorationMaterialSetsIsDeclaredByTheDecorationShader()
        {
            HashSet<string> set = UniformsSetBy("private Material GetDecorationMaterial(");
            HashSet<string> declared = ShaderProperties(DecorationShaderPath);

            CollectionAssert.Contains(set, "_Darkness",
                "The uniform scan found no `_Darkness` in GetDecorationMaterial — the decorations' material " +
                "is where the room's darkness reaches the shader, and a scan that misses it is not scanning.");
            CollectionAssert.Contains(declared, "_ShadowTex",
                "The property scan found no `_ShadowTex` in the decoration shader.");

            AssertEveryUniformIsDeclared(set, declared, "PFE_BackdropDecorationMultiply.shader", "GetDecorationMaterial");
        }

        /// <summary>
        /// The two shaders must be read from two different files. A path typo that points both fixtures at
        /// the wall shader would keep every assertion above green while the decoration shader — the new
        /// file, the one nobody has seen a picture of yet — went unread.
        /// </summary>
        [Test]
        public void TheTwoShadersAreDifferentFiles()
        {
            HashSet<string> wall = ShaderProperties(WallShaderPath);
            HashSet<string> decorations = ShaderProperties(DecorationShaderPath);

            CollectionAssert.Contains(wall, "_TexScale",
                "The wall's tiling fill uniforms (`_TexScale`/`_TexOffset`/`_Flip`) describe a world-space " +
                "bitmap fill. If they are gone the wall stopped being a fill, and the decoration shader's " +
                "sprite sampling is not the only thing that changed.");
            CollectionAssert.DoesNotContain(decorations, "_TexScale",
                "The decoration shader declares the wall's tiling fill uniform — the two paths are being " +
                "read from the same file, so the contract above is only checked once.");

            CollectionAssert.Contains(decorations, "_MainTex",
                "A decoration is a picture: it must sample its own sprite texture.");
        }

        // ---------------------------------------------------------------- the two traps that cost builds

        /// <summary>
        /// Both shaders must raise the darkness to <see cref="BackdropCompositeMath.Gamma"/> before
        /// multiplying. This is the bug that shipped once: the CPU multiplies into the texture's stored
        /// bytes, the fragment multiplies after the sRGB decode, and the difference is
        /// <c>darken^(1/2.2 - 1)</c> — 1.8× too bright at the shipped darkness (doc 09 §16.9).
        ///
        /// <para>The expected text is built from <see cref="BackdropCompositeMath.Gamma"/> rather than
        /// hardcoded, so changing the reference without changing the shaders fails here instead of on a
        /// screen.</para>
        /// </summary>
        [Test]
        public void BothShadersRaiseTheDarkenToTheGammaTheReferenceUses()
        {
            string gamma = BackdropCompositeMath.Gamma.ToString(CultureInfo.InvariantCulture);
            Assert.AreEqual("2.2", gamma,
                "The shaders hardcode `pow(darken, 2.2)`. If the reference's Gamma moves, both shaders must " +
                "move with it — this assertion is the place that says so out loud.");

            string expected = "pow(darken, " + gamma + ")";

            Assert.That(SourceLint.ReadStripped(WallShaderPath), Does.Contain(expected),
                "The wall shader no longer raises the darken to the gamma. Multiplying by `darken` in " +
                "linear space is a different operation from multiplying by it in byte space: the room " +
                "comes out 1.8x too bright and every shadow is weakened by the same factor.");

            Assert.That(SourceLint.ReadStripped(DecorationShaderPath), Does.Contain(expected),
                "The decoration shader composites the same darkness as the wall. It needs the same " +
                "exponent, or the graffiti is lit differently from the wall it is painted on.");
        }

        /// <summary>
        /// The wall's fill is derived from world position; it must not sample the sprite. The quad the GPU
        /// path draws is a 1×1 white sprite, so sampling <c>_MainTex</c> there would paint the wall white
        /// rather than tile the backdrop — and it would look like "the backdrop disappeared", not like a
        /// shader bug.
        /// </summary>
        [Test]
        public void TheWallShaderDerivesItsFillFromWorldPositionRatherThanSamplingTheSprite()
        {
            string wall = SourceLint.ReadStripped(WallShaderPath);
            string decorations = SourceLint.ReadStripped(DecorationShaderPath);

            Assert.That(wall, Does.Not.Contain("SAMPLE_TEXTURE2D(_MainTex"),
                "The wall shader samples `_MainTex`. Its quad is a 1x1 white sprite scaled to the fill " +
                "rect, so that texel is white everywhere — the wall would stop tiling the backdrop.");

            Assert.That(decorations, Does.Contain("SAMPLE_TEXTURE2D(_MainTex"),
                "The decoration shader does not sample `_MainTex`, so it draws no decoration at all.");
        }

        /// <summary>
        /// The CPU composite must stay, and stay reachable. It is the rollback (<c>BackdropMode.Cpu</c>) and
        /// the only honest A/B reference for the shader — deleting it would leave the shader with nothing
        /// to be compared against, which is the failure doc 09 §9.5 names for `TileCompositor`.
        /// </summary>
        [Test]
        public void TheCpuDecorationCompositeIsStillReachable()
        {
            string renderer = SourceLint.ReadStripped(RendererPath);
            string body = SourceLint.MethodBody(renderer, "private void CreateBackgroundSprite(");

            Assert.That(body, Does.Contain("CreateDecorationCompositeSprite"),
                "CreateBackgroundSprite no longer composites on the CPU. That method is the rollback and " +
                "the A/B reference for the decoration shader; if it is gone, `BackdropMode.Cpu` silently " +
                "draws nothing and the two paths can no longer be compared.");
        }

        // ---------------------------------------------------------------- helpers

        private static void AssertEveryUniformIsDeclared(
            HashSet<string> set,
            HashSet<string> declared,
            string shaderFile,
            string method)
        {
            Assert.That(set, Is.Not.Empty, method + " sets no uniforms — the scan is not reading it.");

            var missing = new List<string>();
            foreach (string uniform in set)
            {
                if (!declared.Contains(uniform))
                {
                    missing.Add(uniform);
                }
            }

            Assert.That(missing, Is.Empty,
                method + " sets " + string.Join(", ", missing) + ", which " + shaderFile + " does not " +
                "declare in its Properties block. `Material.Set*` on an undeclared property is a silent " +
                "no-op: the material keeps the shader's default and the room draws with the wrong " +
                "darkness or no shadow, with nothing in the console.");
        }

        /// <summary>The uniform names <paramref name="methodSignature"/> passes to <c>Material.Set*</c>.</summary>
        private static HashSet<string> UniformsSetBy(string methodSignature)
        {
            string renderer = SourceLint.StripCommentsKeepStrings(ReadUnderPfe(RendererPath));
            string body = SourceLint.MethodBody(renderer, methodSignature);

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in UniformSetCall.Matches(body))
            {
                names.Add(match.Groups[1].Value);
            }

            return names;
        }

        /// <summary>The names declared in a shader's <c>Properties</c> block, comments and strings stripped.</summary>
        private static HashSet<string> ShaderProperties(string relativePath)
        {
            string source = SourceLint.ReadStripped(relativePath);

            int properties = source.IndexOf("Properties", StringComparison.Ordinal);
            Assert.IsTrue(properties >= 0, "No `Properties` block in " + relativePath + ".");

            int open = source.IndexOf('{', properties);
            Assert.IsTrue(open >= 0, "No `{` after `Properties` in " + relativePath + ".");

            int depth = 0;
            int close = -1;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) { close = i; break; }
            }

            Assert.IsTrue(close > open, "Unbalanced braces in the Properties block of " + relativePath + ".");

            string block = source.Substring(open, close - open);

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in PropertyDeclaration.Matches(block))
            {
                names.Add(match.Groups[1].Value);
            }

            return names;
        }

        private static string ReadUnderPfe(string relativePath)
        {
            string fullPath = Path.Combine(
                SourceLint.ProjectRoot(),
                "Assets",
                "_PFE",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(fullPath), "Lint target missing: " + fullPath);
            return File.ReadAllText(fullPath);
        }
    }
}
