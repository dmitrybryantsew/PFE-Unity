using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 2.4 source lint: the <b>reviewed mutator set</b> is the contract for mechanism (c).
    ///
    /// <para><b>Why a lint and not only the runtime fixture.</b> The guard lines live on
    /// <c>MonoBehaviour</c>s; instantiating one offline throws before the first assertion (the ECall
    /// wall documented in <c>ClientPredictionSeamTests</c>). A source lint executes everywhere, so it
    /// is the only guard that can prove a reviewed mutator still carries its guard. It also pins the
    /// release-build wiring, which no runtime test can observe from inside a debug build.</para>
    ///
    /// <para><b>Scope honesty.</b> This pins an explicitly reviewed list, exactly as
    /// <c>AuthorityMarkerLintTests</c> names the three Sim-facing controllers and
    /// <c>ClientPredictionSeamTests</c> names the tick-path RNG allow-list. It does <b>not</b>
    /// auto-detect a brand-new public mutator: adding one is a reviewed change that must add an entry
    /// here (and a guard line) in the same commit. Private writes from non-tick paths inside the
    /// declaring class are likewise out of scope. Readiness §8 row 2.4 says so plainly.</para>
    ///
    /// <para><b>Mutation control.</b> The predicate is run over a body with its guard line removed and
    /// must go false; a green result that cannot go red is worth nothing.</para>
    /// </summary>
    [TestFixture]
    public sealed class HostWriteGuardCoverageLintTests
    {
        private const string UnitController = "Entities/Units/UnitController.cs";
        private const string PlayerController = "Entities/Player/PlayerController.cs";
        private const string TilePhysicsController = "Systems/Physics/TilePhysicsController.cs";
        private const string GuardFile = "Core/SimAuthorityContext.cs";
        private const string LoopFile = "Core/SimLoop.cs";
        private const string ScopeFile = "Core/Installers/GameLifetimeScope.cs";

        /// <summary>
        /// Every public/protected method reviewed in Phase 2.4 as an <b>originating</b> write to
        /// <c>[Authoritative]</c> state, plus the two restore surfaces (mechanism a). Format is
        /// <c>relative path::method signature</c>. Restore surfaces are listed separately below.
        /// </summary>
        private static readonly string[] OriginatingMutators =
        {
            UnitController + "::protected virtual void ApplyHeroTier()",
            UnitController + "::public virtual void SetVisible(bool visible)",
            UnitController + "::public virtual void SetFacing(int direction)",
            UnitController + "::public virtual void ApplyPlacement(UnitInstance placement)",
            UnitController + "::public virtual bool Command(string verb, string value)",
            UnitController + "::public void AddForce(Vector2 force)",
            UnitController + "::public void SetVelocityX(float velocityX)",
            UnitController + "::public void SetVelocityY(float velocityY)",
            UnitController + "::public void SetLevitated(bool levitated)",
            UnitController + "::public virtual void SetDropThroughPlatforms(bool shouldDropThrough)",
            UnitController + "::public void SetGrounded(bool grounded)",
            UnitController + "::public void SetRuntimeFaction(FactionType faction)",
            UnitController + "::public virtual void TakeDamage(float damage)",
            UnitController + "::public virtual bool ApplyDamage(in DamageOutcome outcome)",
            UnitController + "::public void SetSubmergedOverride(bool submerged)",
            UnitController + "::public virtual void ApplyKnockback(Vector2 impulse)",
            UnitController + "::public void ReviveFromCorpse()",
            PlayerController + "::public override void TakeDamage(float damage)",
            PlayerController + "::public override bool ApplyDamage(in DamageOutcome outcome)",
            PlayerController + "::public override bool TryReplic(PFE.Sim.Units.ReplicKind kind)",
            PlayerController + "::protected override void OnDeath()",
            PlayerController + "::public void KillFromRadiation()",
            PlayerController + "::public void ApplyRadiationElementalDamage(float damage, PFE.Data.Definitions.DamageType type)",
        };

        /// <summary>
        /// The snapshot-boundary case. These must use <c>CheckRestore()</c>, not the originating
        /// <c>Check(Authoritative)</c>: a client applying host truth is legal, a client applying it
        /// mid-prediction-tick is not.
        /// </summary>
        private static readonly string[] RestoreSurfaces =
        {
            UnitController + "::public void ApplyUnitState(in SimUnitState state)",
            TilePhysicsController + "::public void ApplyMotorState(in SimMotorState state)",
        };

        [Test]
        public void EveryReviewedMutator_CarriesAHostWriteGuard()
        {
            var violations = new List<string>();

            foreach (string entry in AllEntries())
            {
                string path, signature;
                Split(entry, out path, out signature);

                string body = SourceLint.MethodBody(SourceLint.ReadStripped(path), signature);
                if (!HasGuard(body))
                {
                    violations.Add(path + " :: " + signature);
                }
            }

            CollectionAssert.IsEmpty(violations,
                "A Phase 2.4 reviewed mutator lost its HostWriteGuard line. Guarded methods:\n" +
                string.Join("\n", violations));
        }

        [Test]
        public void RestoreSurfaces_UseCheckRestore_NotTheOriginatingCheck()
        {
            foreach (string entry in RestoreSurfaces)
            {
                string path, signature;
                Split(entry, out path, out signature);

                string body = SourceLint.MethodBody(SourceLint.ReadStripped(path), signature);
                Assert.That(body, Does.Contain("HostWriteGuard.CheckRestore()"),
                    signature + " is a mirroring path; it must use the restore guard.");
                Assert.That(body, Does.Not.Contain("HostWriteGuard.Check(SimAuthority.Authoritative)"),
                    signature + " must not be classified as an originating write.");
            }
        }

        [Test]
        public void GuardCallCount_MatchesTheReviewedSetExactly()
        {
            // A new guard line added without review, or one silently deleted, must fail here rather
            // than drift the contract. Restore lines are included in the per-file totals.
            AssertCount(UnitController, 18);
            AssertCount(PlayerController, 6);
            AssertCount(TilePhysicsController, 1);
        }

        [Test]
        public void MutationControl_RemovingAGuardLine_IsDetected()
        {
            string path, signature;
            Split(OriginatingMutators[0], out path, out signature);
            string body = SourceLint.MethodBody(SourceLint.ReadStripped(path), signature);

            Assert.IsTrue(HasGuard(body), "Precondition: the real body must carry a guard.");

            string mutated = body
                .Replace("HostWriteGuard.Check(SimAuthority.Authoritative);", "")
                .Replace("HostWriteGuard.CheckRestore();", "");

            Assert.IsFalse(HasGuard(mutated),
                "Mutation control failed: deleting a guard line was not detected. The green " +
                "coverage result above would be vacuous.");
        }

        [Test]
        public void ReleaseBuild_ChecksAreConditional_AndHaveAnObservableSink()
        {
            string guard = ReadKeepStrings(GuardFile);

            int conditionals = CountOf(guard, "[System.Diagnostics.Conditional(\"UNITY_ASSERTIONS\")]");
            Assert.That(conditionals, Is.EqualTo(2),
                "Both Check and CheckRestore must be [Conditional(\"UNITY_ASSERTIONS\")] so the call " +
                "and its arguments are removed at every call site in a release player.");

            Assert.That(guard, Does.Contain("SetFailureSink"),
                "The guard needs a swappable sink or the offline mutation control cannot observe it.");
            Assert.That(guard, Does.Contain("EnterSnapshotApplication"),
                "The three-state context must expose the snapshot-mirroring scope.");

            // The predicate can go false — otherwise the conditional assertion proves nothing.
            Assert.IsFalse(guard.Replace("[System.Diagnostics.Conditional(\"UNITY_ASSERTIONS\")]", "")
                .Contains("[System.Diagnostics.Conditional(\"UNITY_ASSERTIONS\")]"));
        }

        [Test]
        public void SimLoop_DispatchScopeIsCompiledOutOfRelease()
        {
            string loop = SourceLint.ReadStripped(LoopFile);

            Assert.That(loop, Does.Contain("#if UNITY_ASSERTIONS"),
                "StepOnce must not allocate an authority scope in a release player.");
            Assert.That(loop, Does.Contain("using (SimAuthorityContext.EnterSimulation(Role))"),
                "StepOnce must scope the whole dispatch in assertion builds.");
            Assert.That(loop, Does.Contain("public SimLoopRole Role { get; set; } = SimLoopRole.Host;"),
                "Role must be pushed (not injected) and default to Host so existing fixtures stay silent.");
        }

        [Test]
        public void Bootstrap_PushesTheHostPeerRole()
        {
            string scope = SourceLint.ReadStripped(ScopeFile);

            Assert.That(scope, Does.Contain("SimAuthorityContext.SetPeerRole(SimPeerRole.Host)"),
                "The ambient context must come from a peer role pushed at bootstrap, not from a " +
                "globally-permissive Host default that would hide client writes between ticks.");
        }

        [Test]
        public void AssertionDefine_IsPresentInBothGeneratedProjects()
        {
            // [Conditional] elides the call at the CALLER's compile time. If Unity regenerates a
            // csproj without this define, the offline harness would run a guard with no call sites
            // and every mutation control would be vacuously green. Pin the define on both sides.
            string root = SourceLint.ProjectRoot();
            foreach (string project in new[] { "PFE.View.csproj", "PFE.Tests.csproj" })
            {
                string full = Path.Combine(root, project);
                Assert.IsTrue(File.Exists(full), "Generated project missing: " + full);
                string text = File.ReadAllText(full);

                int at = text.IndexOf("<DefineConstants>", StringComparison.Ordinal);
                int end = at >= 0
                    ? text.IndexOf("</DefineConstants>", at, StringComparison.Ordinal)
                    : -1;
                Assert.That(at, Is.GreaterThanOrEqualTo(0), project + " has no DefineConstants block.");
                Assert.That(end, Is.GreaterThan(at), project + " has an unterminated DefineConstants block.");

                string defines = text.Substring(at, end - at);
                Assert.That(defines, Does.Contain("UNITY_ASSERTIONS"),
                    project + " no longer defines UNITY_ASSERTIONS; the guard would be compiled out " +
                    "of the offline harness too.");

                // Absent control: the predicate can go false.
                Assert.IsFalse(defines.Replace("UNITY_ASSERTIONS", "").Contains("UNITY_ASSERTIONS"),
                    "Mutation control failed: the define predicate cannot go false.");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static IEnumerable<string> AllEntries()
        {
            foreach (string entry in OriginatingMutators) yield return entry;
            foreach (string entry in RestoreSurfaces) yield return entry;
        }

        private static void Split(string entry, out string path, out string signature)
        {
            int at = entry.IndexOf("::", StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThan(0), "Malformed lint entry: " + entry);
            path = entry.Substring(0, at);
            signature = entry.Substring(at + 2);
        }

        private static bool HasGuard(string body)
        {
            return body.Contains("HostWriteGuard.Check(SimAuthority.Authoritative)")
                || body.Contains("HostWriteGuard.CheckRestore()");
        }

        private static void AssertCount(string path, int expected)
        {
            string stripped = SourceLint.ReadStripped(path);
            Assert.That(CountOf(stripped, "HostWriteGuard."), Is.EqualTo(expected),
                "The reviewed guard set for " + path + " changed. Add the new mutator to this lint, " +
                "or restore the deleted guard line, in the same change.");
        }

        private static int CountOf(string text, string needle)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += needle.Length;
            }
            return n;
        }

        /// <summary>
        /// Comments removed, string literals kept — the inverse of <c>ReadStripped</c>, because the
        /// conditional attribute's symbol <em>is</em> a string and is the thing under test.
        /// </summary>
        private static string ReadKeepStrings(string relativePath)
        {
            string fullPath = Path.Combine(
                SourceLint.ProjectRoot(), "Assets", "_PFE",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(fullPath), "Lint target missing: " + fullPath);
            return SourceLint.StripCommentsKeepStrings(File.ReadAllText(fullPath));
        }
    }
}
