using System;
using System.Collections.Generic;
using NUnit.Framework;
using R3;
using UnityEngine;
using PFE.UI.HUD;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// The half of the "max health lags behind" defect that runs without the editor.
    ///
    /// <para><b>Why a separate fixture.</b> Its sibling <c>HealthReadoutTests</c> is the real thing —
    /// it drives <c>CharacterStats.UpLevel</c> and a live <c>HUDViewModel</c> — and it can only run
    /// inside Unity, because it needs a <c>LevelCurve</c> asset and two <c>MonoBehaviour</c>s. Every one
    /// of its five tests therefore fails in the shell with
    /// <c>SecurityException: ECall methods must be packaged into a system module</c>. A guard that only
    /// executes when someone opens Unity is a guard that runs almost never, so the part of the rule that
    /// is plain R3 was moved to <see cref="HealthReadout"/> and is pinned here instead.</para>
    ///
    /// <para><b>The division is deliberate and honest.</b> This fixture proves the readout reacts to a
    /// ceiling change and formats like <c>GUI.setHp</c>. It does <i>not</i> prove that
    /// <c>RecalculateStats</c> raises the ceiling before the health — that is the sibling's job, in the
    /// editor. Two causes, two fixtures, and neither one silently stands in for the other.</para>
    /// </summary>
    [TestFixture]
    public class HealthReadoutFormatTests
    {
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        /// <summary>
        /// Set by <see cref="StartR3WithoutTheEngine"/>; <c>null</c> means it ran and R3 came up.
        /// </summary>
        /// <remarks>
        /// The initial value is a deliberate trap: if the runner never calls <c>[OneTimeSetUp]</c> — and
        /// the project's own offline runner ignored the attribute until 2026-10-27 — the guard reports
        /// "never ran" instead of letting four tests each fail with a bare "type initializer" line that
        /// says nothing about why.
        /// </remarks>
        private static string _r3StartupFailure = "OneTimeSetUp never ran";

        [OneTimeSetUp]
        public void StartR3WithoutTheEngine()
        {
            _r3StartupFailure = null;

            // R3's global system resolves its frame and time providers when the type is first touched,
            // and inside Unity it resolves them to the engine's. Outside it, the static constructor
            // throws — so record the whole chain here, once, instead of letting four tests each report
            // the same bare "type initializer" line.
            try
            {
                ObservableSystem.DefaultFrameProvider = new FakeFrameProvider();
            }
            catch (Exception e)
            {
                _r3StartupFailure = Flatten(e);
            }
        }

        private static string Flatten(Exception e)
        {
            var text = new System.Text.StringBuilder();
            for (Exception cur = e; cur != null; cur = cur.InnerException)
                text.Append(cur.GetType().Name).Append(": ").Append(cur.Message).Append(" <- ");
            return text.ToString();
        }

        private static void RequireR3()
        {
            if (_r3StartupFailure != null)
                Assert.Fail("R3's global system did not start without the engine: " + _r3StartupFailure);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _disposables.Count; i++)
                _disposables[i].Dispose();
            _disposables.Clear();
        }

        // ── The format rule ──────────────────────────────────────────────────

        [Test]
        [Description("A full pool reads as one number twice, not as a mismatch")]
        public void Format_FullPool_ReadsTheSameNumberTwice()
        {
            Assert.AreEqual("100 / 100", HealthReadout.Format(100f, 100f));
        }

        [Test]
        [Description("GUI.as:944 rounds the pair up — 84.3 reads 85, not 84")]
        public void Format_CeilsLikeTheOracle()
        {
            Assert.AreEqual("85 / 100", HealthReadout.Format(84.3f, 100f));

            // The absent control: `Mathf.Round` is the obvious implementation and it disagrees.
            Assert.AreEqual("84 / 100", $"{Mathf.RoundToInt(84.3f)} / {Mathf.RoundToInt(100f)}",
                "if this line ever matches the assertion above, the rounding rule stopped being tested");
        }

        [Test]
        [Description("A player on a fraction of a hit point reads 1, not 0 — the reason for ceil")]
        public void Format_AFractionOfAPoint_ReadsOne()
        {
            Assert.AreEqual("1 / 100", HealthReadout.Format(0.4f, 100f),
                "a bar at zero with the player still alive is the reading that looks like a bug");
        }

        [Test]
        [Description("Exactly zero stays zero — ceil must not invent health")]
        public void Format_AtZero_StaysZero()
        {
            Assert.AreEqual("0 / 100", HealthReadout.Format(0f, 100f));
        }

        // ── The binding: it must fire on EITHER number ───────────────────────

        [Test]
        [Description("The ceiling moving alone republishes — this is the reported defect")]
        public void Bind_ACeilingChangeWithNoHealthChange_Republishes()
        {
            RequireR3();
            var hp = new ReactiveProperty<float>(100f);
            var maxHp = new ReactiveProperty<float>(100f);

            string latest = null;
            var text = HealthReadout.Bind(hp, maxHp);
            _disposables.Add(text);
            _disposables.Add(text.Subscribe(value => latest = value));

            Assert.AreEqual("100 / 100", latest, "primed on subscribe with the live pair");

            // The exact shape of the bug: the ceiling moves, the health does not.
            maxHp.Value = 115f;

            Assert.AreEqual("100 / 115", latest,
                "a binding over the health alone produces no event here — that was the 'max health lags " +
                "behind' report, and the bar was right the whole time");
        }

        [Test]
        [Description("The health moving alone still republishes")]
        public void Bind_AHealthChangeAlone_Republishes()
        {
            RequireR3();
            var hp = new ReactiveProperty<float>(100f);
            var maxHp = new ReactiveProperty<float>(100f);

            string latest = null;
            var text = HealthReadout.Bind(hp, maxHp);
            _disposables.Add(text);
            _disposables.Add(text.Subscribe(value => latest = value));

            hp.Value = 50f;

            Assert.AreEqual("50 / 100", latest);
        }

        [Test]
        [Description("A level-up at full health lands on the new ceiling with no gap")]
        public void Bind_ALevelUpAtFullHealth_HasNoGap()
        {
            RequireR3();
            var hp = new ReactiveProperty<float>(100f);
            var maxHp = new ReactiveProperty<float>(100f);

            string latest = null;
            var text = HealthReadout.Bind(hp, maxHp);
            _disposables.Add(text);
            _disposables.Add(text.Subscribe(value => latest = value));

            // What RecalculateStats does, in order: ceiling first, health second (Pers.as:2165 then :2245).
            maxHp.Value = 115f;
            hp.Value = 115f;

            Assert.AreEqual("115 / 115", latest);
        }

        [Test]
        [Description("Disposing the readout stops it, so a rebind cannot leave a live subscription behind")]
        public void Bind_AfterDispose_StopsRepublishing()
        {
            RequireR3();
            var hp = new ReactiveProperty<float>(100f);
            var maxHp = new ReactiveProperty<float>(100f);

            string latest = null;
            var text = HealthReadout.Bind(hp, maxHp);
            text.Subscribe(value => latest = value);

            text.Dispose();
            maxHp.Value = 115f;

            Assert.AreEqual("100 / 100", latest,
                "the owner disposes this on Rebind(); a leaked one would keep the whole player graph alive");
        }
    }
}
