using System;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the <b>presentation wiring</b> of S.A.T.S. — the ballistic tracer, the single
    /// <c>radius</c> sprite and their per-frame ordering.
    ///
    /// <para><b>Why this fixture exists: a real defect it was written against.</b>
    /// <c>SatsBootstrapper.SyncTracer()</c> had been fully implemented (100 steps, 9 px sub-steps,
    /// tile collision, a LineRenderer) and was <i>never called from any method</i>. It compiled cleanly,
    /// every arithmetic fixture was green, and the tracer simply did not exist at runtime. At the same
    /// time <c>SatsView.Sync</c> set <c>_reticle.enabled = active</c>, so a melee-sized ring appeared on
    /// every weapon while the blast ring was nowhere. Both are MonoBehaviour composition faults: no
    /// offline harness can see them, and no exception is thrown. A source lint is the only executable
    /// guard this project has for that class of defect.</para>
    ///
    /// <para><b>Every assertion is scoped to a method body, with an absent control.</b> A search that
    /// runs "from the signature onward" is satisfied by the same identifier in a later member — the
    /// guard that cannot fail. <see cref="MethodBody"/> bounds the region at the member indent.</para>
    /// </summary>
    [TestFixture]
    public class SatsPresentationWiringLintTests
    {
        private const string BootstrapperPath = "Systems/Combat/Sats/SatsBootstrapper.cs";
        private const string ViewPath = "UI/Combat/SatsView.cs";

        /// <summary>One member's text, from its signature to the closing brace at member indent.</summary>
        private static string MethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0,
                "Could not find `" + signature + "`. If the member was renamed, repoint this fixture " +
                "rather than leaving it to fail as a missing-signature error.");

            int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "Could not find the end of `" + signature + "`.");

            return source.Substring(start, end - start);
        }

        // ── 1. The tracer is DRIVEN, not merely defined ─────────────────────────

        /// <summary>
        /// <c>LateUpdate</c> must call <c>SyncTracer()</c>. The definition
        /// <c>private SatsRadiusSpec SyncTracer()</c> contains the same identifier, so the assertion
        /// matches the call form with its semicolon.
        /// </summary>
        [Test]
        public void Bootstrapper_CallsTheTracerEveryFrame()
        {
            string body = MethodBody(SourceLint.ReadStripped(BootstrapperPath), "private void LateUpdate()");

            // Positive control on the extraction: an empty or truncated body must fail here too.
            Assert.IsTrue(body.Contains("_view.Sync("),
                "LateUpdate no longer syncs the view — the extraction is not the method it claims to be.");

            Assert.IsTrue(body.Contains("SyncTracer();"),
                "SyncTracer() is defined but never called. The ballistic preview is then dead code: " +
                "the mode opens, the world freezes, and no trajectory is ever drawn. This is the " +
                "exact defect this fixture was written after finding in the tree.");
        }

        // ── 2. The view gets THIS frame's radius, after the trace ───────────────

        /// <summary>
        /// The radius sprite is pinned to the tracer's <b>endpoint</b>
        /// (<c>radius.x = weapon.trasser.X</c>, <c>Sats.as:556-557</c>), so the trace has to run before
        /// the view is synced, and its result has to be the value passed in.
        /// </summary>
        [Test]
        public void Bootstrapper_TracesBeforeSyncingTheView()
        {
            string body = MethodBody(SourceLint.ReadStripped(BootstrapperPath), "private void LateUpdate()");

            int traced = body.IndexOf("SyncTracer();", StringComparison.Ordinal);
            int synced = body.IndexOf("_view.Sync(", StringComparison.Ordinal);

            Assert.IsTrue(traced > 0, "SyncTracer() is not called. See Bootstrapper_CallsTheTracerEveryFrame.");
            Assert.IsTrue(synced > 0, "_view.Sync is not called.");
            Assert.Less(traced, synced,
                "The view is synced before the trace. The blast ring would then use the PREVIOUS " +
                "frame's impact point, lagging behind the cursor by one frame.");

            Assert.IsTrue(body.Contains("_view.Sync(_sats, _sats.ShowsPercentages, radius)"),
                "LateUpdate does not pass the traced radius state to the view. Without it the view " +
                "cannot know whether to show a melee reach ring, a blast ring, or nothing.");
        }

        // ── 3. The impact point is consumed, not discarded ──────────────────────

        /// <summary>
        /// <c>SatsTrajectory.Trace</c> reports where the arc stopped. The Oracle puts the blast ring
        /// there; an <c>out _</c> silently puts it nowhere.
        /// </summary>
        [Test]
        public void Tracer_UsesItsEndpointForTheBlastRing()
        {
            string body = MethodBody(SourceLint.ReadStripped(BootstrapperPath),
                "private SatsRadiusSpec SyncTracer()");

            Assert.IsTrue(body.Contains("out SatsTracePoint endPx"),
                "SyncTracer discards the trace's endpoint. The blast ring has no impact point to " +
                "follow, which is the one place the Oracle does pin it to.");
            Assert.IsFalse(body.Contains("endPx: out _"),
                "The endpoint is explicitly thrown away (Sats.as:556-557 pins the ring to trasser.X/Y).");
            Assert.IsTrue(body.Contains("new SatsRadiusSpec(new Vector2(endPx.X, endPx.Y)"),
                "The radius spec is not built from the traced endpoint. A blast ring at the muzzle or " +
                "the cursor is worse than no ring: it tells the player the wrong area.");
            Assert.IsTrue(body.Contains("def.explRadius > 0f"),
                "The blast ring is not gated on explRadius. A plain rifle would show an explosion " +
                "circle it does not have (Sats.as:551-561).");
        }

        // ── 4. Melee keeps its own reach ring ───────────────────────────────────

        /// <summary>
        /// <c>tip &lt;= 1</c> suppresses the tracer but still draws the ring at
        /// <c>X + meleeS*storona, Y - scY/2</c> scaled <c>meleeR/100</c> (<c>Sats.as:126-135</c>).
        /// Returning <c>Hidden</c> from the melee branch would erase it.
        /// </summary>
        [Test]
        public void Tracer_MeleeStillReturnsTheReachRing()
        {
            string body = MethodBody(SourceLint.ReadStripped(BootstrapperPath),
                "private SatsRadiusSpec SyncTracer()");

            Assert.IsTrue(body.Contains("def.weaponType == WeaponType.Melee"),
                "The melee / noTrass branch is gone, so melee weapons would draw a ballistic arc.");
            Assert.IsTrue(body.Contains("new SatsRadiusSpec(meleePx, meleeR / 100f)"),
                "The melee branch returns no radius spec. The reach ring is the ONLY preview a melee " +
                "weapon gets in the Oracle; losing it leaves the mode with no feedback at all.");
        }

        // ── 5. The view is a projection, not a second policy ────────────────────

        /// <summary>
        /// <c>Sync</c> must not enable the reticle on <c>active</c> alone, and <c>SyncReticle</c> must
        /// honour <c>radius.Visible</c>. The Oracle hides the sprite for any non-explosive ranged
        /// weapon (<c>Sats.as:559-561</c>).
        /// </summary>
        [Test]
        public void View_ShowsTheReticleOnlyWhenTheSpecSaysSo()
        {
            string source = SourceLint.ReadStripped(ViewPath);
            string sync = MethodBody(source,
                "public void Sync(SatsController sats, bool showPercent, SatsRadiusSpec radius)");

            Assert.IsTrue(sync.Contains("SyncLabels("),
                "SatsView.Sync no longer draws labels — the extraction is not the method it claims to be.");
            Assert.IsFalse(sync.Contains("_reticle.enabled = active"),
                "The reticle is enabled on `active` alone. That puts a melee-sized ring on every " +
                "weapon and hides it on none, which is neither of the Oracle's three radius states.");
            Assert.IsTrue(sync.Contains("SyncReticle(radius)"),
                "Sync does not pass the frame's radius spec to the reticle.");

            string reticle = MethodBody(source, "private void SyncReticle(SatsRadiusSpec radius)");
            Assert.IsTrue(reticle.Contains("if (!radius.Visible)"),
                "SyncReticle ignores radius.Visible — the radius sprite can never be hidden.");
            Assert.IsTrue(reticle.Contains("WorldToCanvas(radius.WorldPixels)"),
                "The ring is not positioned from the spec's world-pixel point.");
        }

        // ── 6. Gravity is mirrored, not dropped ─────────────────────────────────

        /// <summary>
        /// <c>if (this.grav) trasser.ddy += World.ddy</c> (<c>Weapon.as:1651-1654</c>),
        /// <c>World.ddy = 1</c> (<c>World.as:46</c>). AS3's Y runs down and the port's runs up, so the
        /// mirror is a <b>negative</b> acceleration. Passing <c>0f</c> makes every gravity weapon's
        /// preview a straight line while the shot arcs.
        /// </summary>
        [Test]
        public void Tracer_MirrorsGravityInsteadOfDroppingIt()
        {
            string body = MethodBody(SourceLint.ReadStripped(BootstrapperPath),
                "private SatsRadiusSpec SyncTracer()");

            Assert.IsTrue(body.Contains("float gravity = def.bulletGravity > 0f ? -1f : 0f;"),
                "Gravity is not derived from bulletGravity, or its sign is not mirrored. AS3's " +
                "World.ddy is +1 in a Y-down world; the port's pixel Y is Unity's Y-up.");
            Assert.IsTrue(body.Contains("gravityPxPerTickSquared: gravity"),
                "SatsTrajectory.Trace is not given the gravity it models. A grenade preview would be " +
                "a straight line while the round drops.");
            Assert.IsFalse(body.Contains("gravityPxPerTickSquared: 0f"),
                "Gravity is hard-coded to zero — the traced arc ignores it entirely.");
        }
    }
}
