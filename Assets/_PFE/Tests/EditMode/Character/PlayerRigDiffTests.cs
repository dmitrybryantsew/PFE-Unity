using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NUnit.Framework;
using PFE.Entities.Player.Rig;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// The player-rig parity rule — the "these two rigs disagree, and here is where" decision behind
    /// the code-built player.
    ///
    /// <para><b>Why these are offline.</b> The same split <see cref="OverlayClipTests"/> documents:
    /// the rule lives in a plain class (<see cref="PlayerRigSnapshot"/>) that mentions no
    /// <c>UnityEngine</c> type, so its whole decision table runs in an offline host. The half that
    /// cannot run offline — reading a live <c>GameObject</c> into a snapshot — is kept as thin as
    /// possible and is deliberately not tested here, because a fixture whose arrange cannot run
    /// proves nothing.</para>
    /// </summary>
    [TestFixture]
    public class PlayerRigDiffTests
    {
        static PlayerRigSnapshot Snap(params (string Key, string Value)[] entries)
        {
            var s = new PlayerRigSnapshot();
            foreach (var e in entries) s.Set(e.Key, e.Value);
            return s;
        }

        // ── Diff ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Diff_OnIdenticalSnapshots_IsEmpty()
        {
            var a = Snap(("tilePhysics.maxSpeedX", "8"), ("abilities.canLevitate", "true"));
            var b = Snap(("tilePhysics.maxSpeedX", "8"), ("abilities.canLevitate", "true"));

            Assert.AreEqual(0, PlayerRigSnapshot.Diff(a, b).Count);
        }

        [Test]
        public void Diff_ReportsOnlyTheKeyThatChanged()
        {
            var baseline = Snap(("tilePhysics.platformThreshold", "8"), ("abilities.moveSpeedMultiplier", "1"));
            var candidate = Snap(("tilePhysics.platformThreshold", "10"), ("abilities.moveSpeedMultiplier", "1"));

            List<RigDivergence> diffs = PlayerRigSnapshot.Diff(baseline, candidate);

            Assert.AreEqual(1, diffs.Count, "only the changed key is a divergence");
            Assert.AreEqual("tilePhysics.platformThreshold", diffs[0].Key);
            Assert.AreEqual(RigDivergenceKind.ValueDiffers, diffs[0].Kind);
            Assert.AreEqual("tilePhysics.platformThreshold: 8 -> 10", diffs[0].ToString());
        }

        [Test]
        public void Diff_ReportsAbsenceInBothDirections_BecauseAnExtraKeyIsAlsoADivergence()
        {
            var baseline = Snap(("a", "1"));
            var candidate = Snap(("b", "2"));

            List<RigDivergence> diffs = PlayerRigSnapshot.Diff(baseline, candidate);

            Assert.AreEqual(2, diffs.Count, "one key only on each side");
            Assert.IsTrue(diffs.Exists(d => d.Key == "a" && d.Kind == RigDivergenceKind.MissingInCandidate),
                "a key the rig never set is a finding, not a silence");
            Assert.IsTrue(diffs.Exists(d => d.Key == "b" && d.Kind == RigDivergenceKind.MissingInBaseline),
                "a key the prefab never had is a finding too");
        }

        [Test]
        public void Diff_IsDeterministic_BecauseTheReportIsAssertedLineByLine()
        {
            var baseline = Snap(("z", "1"), ("m", "1"), ("a", "1"));
            var candidate = Snap(("z", "2"), ("m", "2"), ("a", "2"));

            List<RigDivergence> first = PlayerRigSnapshot.Diff(baseline, candidate);
            List<RigDivergence> second = PlayerRigSnapshot.Diff(baseline, candidate);

            Assert.AreEqual(new[] { "a", "m", "z" }, new[] { first[0].Key, first[1].Key, first[2].Key },
                "ordinal key order, so a report can be diffed against a golden file");
            for (int i = 0; i < first.Count; i++)
            {
                Assert.AreEqual(first[i].ToString(), second[i].ToString());
            }
        }

        [Test]
        public void Diff_ThrowsOnANullSide_RatherThanSilentlyReportingNoDivergence()
        {
            var a = Snap(("k", "1"));
            Assert.Throws<System.ArgumentNullException>(() => PlayerRigSnapshot.Diff(a, null));
            Assert.Throws<System.ArgumentNullException>(() => PlayerRigSnapshot.Diff(null, a));
        }

        // ── Float formatting ────────────────────────────────────────────────────────────────

        [Test]
        public void FormatFloat_IsCultureInvariant_SoTheDiffDoesNotDependOnTheMachine()
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                // A comma-decimal culture. A culture-sensitive format would emit "0,14" here and the
                // same rig would compare equal on one machine and different on another.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                Assert.AreEqual("0.14", PlayerRigSnapshot.FormatFloat(0.14f));
                Assert.AreEqual("8", PlayerRigSnapshot.FormatFloat(8f));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Test]
        public void FormatFloat_RoundTrips_SoExactStringEqualityIsAValidEqualityTest()
        {
            foreach (float value in new[] { 0f, 1f, 0.14f, 25f, 0.8f, -1.6f, 0.0001f, 12345.678f })
            {
                string text = PlayerRigSnapshot.FormatFloat(value);
                float back = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                Assert.AreEqual(value, back, "FormatFloat must round-trip, or the diff lies: " + value);
            }
        }

        [Test]
        public void Set_Overloads_NormaliseToTheSameTextTheDiffCompares()
        {
            var s = new PlayerRigSnapshot();
            s.Set("b", true);
            s.Set("f", 0.14f);
            s.Set("i", 3);
            s.Set("n", (string)null);

            Assert.AreEqual("true", s.Values["b"]);
            Assert.AreEqual("0.14", s.Values["f"]);
            Assert.AreEqual("3", s.Values["i"]);
            Assert.AreEqual("<null>", s.Values["n"], "a null must not read as an empty string");
        }
    }
}
