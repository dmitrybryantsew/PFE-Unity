using System;
using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the three <b>runtime identity / read-back / text-order</b> seams of S.A.T.S. that the
    /// arithmetic fixtures and the earlier wiring lints could not see.
    ///
    /// <para><b>Why these need a fixture at all.</b> Each of the three defects compiled cleanly, was
    /// invisible to <c>SatsRulesTests</c> (the arithmetic was right), to the execution lints (the
    /// calls were all present) and to the presentation lints (the view was synced every frame). What
    /// was wrong was <i>which object</i> held the state, <i>what a bool meant</i>, and <i>the order
    /// of two string replacements</i>. All three live in <c>MonoBehaviour</c>s or in the container, so
    /// an offline behavioural test needs a scene that the harness cannot build. A source lint is the
    /// only guard that can actually execute here.</para>
    ///
    /// <para><b>Every assertion has an absent control from the same extracted region</b>, so a lint
    /// cannot pass because the extractor returned an empty string. The comment stripper is used for
    /// the identifier assertions and the keep-strings stripper for the text-order assertions — the
    /// opposite choice in either case would make the guard vacuous: comments name the very calls
    /// these tests check for, and the order is only visible in the string literals.</para>
    /// </summary>
    [TestFixture]
    public class SatsRuntimeWiringLintTests
    {
        private const string BootstrapperPath = "Systems/Combat/Sats/SatsBootstrapper.cs";
        private const string TargetingPath = "Systems/Combat/Sats/SatsTargeting.cs";
        private const string ScopePath = "Core/Installers/GameLifetimeScope.cs";

        /// <summary>
        /// Source with comments removed but string literals preserved — for lints whose subject IS the
        /// literals. <see cref="SourceLint.ReadStripped"/> would erase <c>"@4"</c> and <c>"@@"</c>
        /// along with the comments, leaving nothing to order.
        /// </summary>
        private static string KeptSource(string relativePath)
        {
            string full = Path.Combine(
                SourceLint.ProjectRoot(),
                "Assets",
                "_PFE",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(full), "Lint target missing: " + full);

            string kept = SourceLint.StripCommentsKeepStrings(File.ReadAllText(full));

            Assert.That(kept, Does.Not.Contain("///"),
                "The comment stripper did not strip — an assertion about the hint text would then " +
                "be satisfiable by the explanatory comment that describes the correct order.");

            return kept;
        }

        // ── 1. One controller, owned by the container ───────────────────────────

        /// <summary>
        /// The bootstrapper must USE the container's singleton, never build a second one.
        ///
        /// <para><b>The defect this prevents.</b> A private <c>new SatsController(_pause)</c> in
        /// <c>Start()</c> holds the input, the pause gate and the queued cells. Every consumer
        /// (<c>PlayerWeaponLoadout</c>, <c>PlayerController</c>, <c>PipBuckBootstrapper</c>) is
        /// injected with the container's <i>other</i> instance, which never sees a cell. Accepting
        /// releases the pause correctly, <c>DriveSatsQueue()</c> asks the idle singleton for a front
        /// cell, gets <c>false</c>, and the plan never fires. That is the "accepting does not fire"
        /// report: two real controllers, zero shared state.</para>
        /// </summary>
        [Test]
        public void Bootstrapper_UsesTheContainerController()
        {
            string source = SourceLint.ReadStripped(BootstrapperPath);

            // Absent control: if the extractor returned nothing, every Contains/!Contains below
            // would still pass. This line is in Start() in any working version.
            Assert.IsTrue(source.Contains("_loop?.Register(_sats)"),
                "Start() no longer registers the controller on SimLoop. Without a tick the mode " +
                "receives no input at all; this is also the control that the source was read.");

            Assert.IsFalse(source.Contains("new SatsController"),
                "SatsBootstrapper constructs its own SatsController. The queue then lives on an " +
                "object PlayerWeaponLoadout cannot see, so accepting the plan fires nothing. " +
                "Inject the container's singleton into Construct() instead.");

            // Both halves of the fix: an injected parameter AND its assignment to the field the
            // rest of the bootstrapper drives. A lint that pinned only one would survive a signature
            // that takes the controller and then ignores it.
            string construct = SourceLint.MethodBody(source, "public void Construct(SimLoop loop, SatsController sats)");
            Assert.IsTrue(construct.Contains("_sats = sats;"),
                "Construct() takes the container controller but does not put it in _sats.");

            // The other half of the identity claim: there must actually BE a container singleton.
            // Without this registration VContainer fails at build time, or a future reader "fixes"
            // the crash by restoring a local construction.
            string scope = SourceLint.ReadStripped(ScopePath);
            Assert.IsTrue(
                scope.Contains("builder.Register<PFE.Systems.Combat.Sats.SatsController>(Lifetime.Singleton);"),
                "GameLifetimeScope no longer registers SatsController as a singleton. The four " +
                "consumers would not share a queue even if the bootstrapper injected one.");
        }

        // ── 2. A failed fog read-back is "fully visible" ────────────────────────

        /// <summary>
        /// <c>SatsTargeting.Refresh</c> must feed the read-back flag and value to
        /// <c>SatsRules.FogAdmitsTarget</c>, not use the flag itself as a skip guard.
        ///
        /// <para><b>The defect this prevents.</b>
        /// <c>RoomBackdropRenderer.TryGetFogVisibility</c> returns <c>false</c> with
        /// <c>visibility = 1</c> when there is no mask — fog off, mask not built, collision-only
        /// room. Treating that <c>false</c> as "skip this unit" empties the candidate list in every
        /// fogless room: no names, no percentages, no brackets, no hover stats. The mode still opens
        /// and still queues free-aim cells, so it looks like a rendering fault rather than a filter
        /// that rejected everyone.</para>
        /// </summary>
        [Test]
        public void Targeting_AdmitsTargetsWhenThereIsNoFogMask()
        {
            string source = SourceLint.ReadStripped(TargetingPath);
            string body = SourceLint.MethodBody(source,
                "public void Refresh(UnitController player, bool isMelee, float meleeReachPx, ISatsWorldQuery world)");

            // Absent control. HasLineOfSight is in the same branch in every working version.
            Assert.IsTrue(body.Contains("HasLineOfSight"),
                "Refresh() no longer runs the LOS test — or the extracted body is empty, in which " +
                "case every assertion below would pass vacuously.");

            int read = body.IndexOf("TryGetFogVisibility", StringComparison.Ordinal);
            int rule = body.IndexOf("SatsRules.FogAdmitsTarget", StringComparison.Ordinal);

            Assert.IsTrue(read >= 0, "Refresh() no longer samples the fog mask at all.");
            Assert.IsTrue(rule >= 0,
                "Refresh() bypasses SatsRules.FogAdmitsTarget. The no-mask case then depends on a " +
                "bool whose false means 'fully visible', and the arithmetic fixture cannot help.");
            Assert.Less(read, rule,
                "The mask must be sampled BEFORE the admission rule decides; the rule takes the " +
                "read-back flag as an input.");

            Assert.IsFalse(body.Contains("if (!world.TryGetFogVisibility("),
                "The read-back bool is used as a skip guard again. false means 'no fog mask, fully " +
                "visible', not 'dark' (RoomBackdropRenderer.cs:753-756).");
        }

        /// <summary>
        /// The seam must default to the fully-lit value when there is no room visual, so the fix
        /// above is not undone one layer down.
        /// </summary>
        [Test]
        public void WorldQuery_DefaultsToFullyLitWithoutARoom()
        {
            string source = SourceLint.ReadStripped(BootstrapperPath);
            string body = SourceLint.MethodBody(source,
                "public bool TryGetFogVisibility(Vector3 worldPosition, out float visibility)");

            Assert.IsTrue(body.Contains("_owner._roomVisual"),
                "The extracted WorldQuery body is empty or no longer consults the room visual.");
            Assert.IsTrue(body.Contains("visibility = 1f;"),
                "With no room visual there is nothing drawn over the unit; the seam must report " +
                "visibility 1, not 0. A 0 here hides every target in a scene with no room.");
            Assert.IsFalse(body.Contains("visibility = 0f;"),
                "The seam still defaults to pitch black, which is the inverted reading.");
        }

        // ── 3. Key placeholders before newline conversion ──────────────────────

        /// <summary>
        /// <c>SetTopText</c> must substitute <c>@1</c>…<c>@4</c> <b>before</b> turning the remaining
        /// <c>@</c> characters into newlines — <c>Res.addKeys()</c> runs before
        /// <c>GUI.setTopText()</c>'s <c>/@/g</c> replacement.
        ///
        /// <para><b>The defect this prevents.</b> In the authored string
        /// <c>@1 - select@@2 - cancel@@3 or @4 - accept</c>, each <c>@@</c> is one newline marker
        /// plus the <c>@</c> of the next placeholder. Replacing <c>"@@"</c> first destroys the
        /// <c>@2</c> and <c>@3</c> tokens, so the hint prints bare <c>2</c> and <c>3</c> — and
        /// tells the player to press <c>3</c> to accept, which is not a bound key. The actual accept
        /// is <c>@3</c> (E / Interact) or <c>@4</c> (V / middle mouse).</para>
        /// </summary>
        [Test]
        public void Hint_SubstitutesKeysBeforeItConvertsNewlines()
        {
            string kept = KeptSource(BootstrapperPath);
            string body = SourceLint.MethodBody(kept, "public void SetTopText(string key)");

            Assert.IsTrue(body.Contains("PfeTextTableProvider"),
                "The extracted SetTopText body is empty — every ordering assertion below would " +
                "then pass without checking anything.");

            int key4 = body.IndexOf("\"@4\"", StringComparison.Ordinal);
            int newline = body.IndexOf("Replace(\"@\", \"\\n\")", StringComparison.Ordinal);

            Assert.IsTrue(key4 >= 0, "SetTopText no longer substitutes the @4 (S.A.T.S. toggle) key.");
            Assert.IsTrue(newline >= 0,
                "SetTopText no longer converts the remaining '@' characters to newlines.");
            Assert.Less(key4, newline,
                "The @1..@4 substitutions must run BEFORE the newline conversion. Res.addKeys() " +
                "runs first in the Oracle (Res.as:2637-2648); GUI.setTopText() then replaces /@/g " +
                "(GUI.as:1123-1138). Reversing them mangles @2 and @3.");

            Assert.IsFalse(body.Contains("Replace(\"@@\""),
                "SetTopText still replaces \"@@\" as a unit. The first '@' is a line break and the " +
                "second belongs to the following placeholder; they are not an escape pair.");
        }

        /// <summary>
        /// The key the hint prints as "accept" must be the key <c>PollInput</c> actually accepts on.
        /// A hint that names a different action is worse than no hint: it sends the player to a key
        /// that does something else.
        /// </summary>
        [Test]
        public void Hint_PrintsTheSameAcceptKeyTheModePolls()
        {
            string kept = KeptSource(BootstrapperPath);
            string top = SourceLint.MethodBody(kept, "public void SetTopText(string key)");

            int at3 = top.IndexOf("\"@3\"", StringComparison.Ordinal);
            Assert.IsTrue(at3 >= 0, "The hint no longer has an @3 placeholder.");

            // The @3 substitution, up to the end of that chained Replace call.
            int after = top.IndexOf(".Replace(\"@4\"", at3, StringComparison.Ordinal);
            Assert.IsTrue(after > at3, "The @1..@4 substitution chain is no longer in order.");
            Assert.IsTrue(top.Substring(at3, after - at3).Contains("input.Interact"),
                "@3 must resolve to the Interact binding. In text_en.xml s3=\"keyAction\", which " +
                "is the port's Interact action — the same one PollInput accepts on.");

            string stripped = SourceLint.ReadStripped(BootstrapperPath);
            string poll = SourceLint.MethodBody(stripped, "private void PollInput()");
            Assert.IsTrue(poll.Contains("_sats.LatchAccept()"),
                "PollInput no longer latches an accept at all.");
            Assert.IsTrue(poll.Contains("if (Pressed(input.Interact)) _sats.LatchAccept();"),
                "The accept input is no longer Interact. The hint must be changed too, or it will " +
                "name a key that does not accept.");
        }
    }
}
