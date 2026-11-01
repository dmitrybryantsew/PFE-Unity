using System;
using System.Collections.Generic;
using NUnit.Framework;
using R3;
using UnityEngine;
using PFE.Entities.Units;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using PFE.UI.HUD;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins what the HUD's health readout is allowed to see — from both ends.
    ///
    /// <para><b>The defect these exist for.</b> Level up at full health and the pool goes
    /// <c>100/100</c> → <c>115/115</c>: the ceiling changed and the health changed with it. The bar
    /// moved (it is a <c>CombineLatest</c> over both numbers) but the "current / max" text beside it
    /// kept printing <c>100</c> as the maximum, until the player took a hit. That is the report
    /// "max health lags behind", and it had two independent causes:</para>
    /// <list type="number">
    ///   <item><description><b>Write order.</b> <c>CharacterStats.RecalculateStats</c> wrote
    ///     <c>CurrentHp</c> before <c>MaxHp</c>, so every listener of the health read the ceiling at
    ///     the one instant it was still stale. AS3 does the opposite — <c>Pers.as:2165</c> sets
    ///     <c>gg.maxhp</c>, <c>:2245</c> then sets <c>gg.hp</c>.</description></item>
    ///   <item><description><b>Read shape.</b> <c>HealthBarView</c> subscribed to <c>CurrentHealth</c>
    ///     alone and called a getter that read the ceiling inside the handler, so a ceiling change with
    ///     no second health write produced no event at all. It now binds
    ///     <see cref="HUDViewModel.HealthText"/>, which combines both.</description></item>
    /// </list>
    ///
    /// <para>Either fix alone makes the common case look right, which is why both are pinned
    /// separately: reverting one would leave the other passing.</para>
    /// </summary>
    [TestFixture]
    public class HealthReadoutTests
    {
        private GameObject _go;
        private GameObject _hudGo;
        private CharacterStats _charStats;
        private UnitStats _unitStats;
        private LevelCurve _curve;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        [SetUp]
        public void SetUp()
        {
            _curve = ScriptableObject.CreateInstance<LevelCurve>();
            _curve.baseHp = 100;
            _curve.hpPerLevel = 15;
            _curve.baseOrganHp = 200;
            _curve.organHpPerLevel = 40;
            _curve.xpDelta = 5000;
            _curve.skillPointsPerLevel = 5;

            _go = new GameObject("HealthReadoutTestPlayer");
            _charStats = _go.AddComponent<CharacterStats>();
            _charStats.Initialize(_curve);

            _unitStats = new UnitStats(100f, 400f);
            _charStats.BindUnitStats(_unitStats);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _subscriptions.Count; i++)
                _subscriptions[i].Dispose();
            _subscriptions.Clear();

            if (_hudGo != null) UnityEngine.Object.DestroyImmediate(_hudGo);
            if (_go != null) UnityEngine.Object.DestroyImmediate(_go);
            if (_curve != null) UnityEngine.Object.DestroyImmediate(_curve);
        }

        // ── Write side: the order the bridge is pushed in ────────────────────

        [Test]
        [Description("A level-up raises the max BEFORE the health, so a listener never reads a stale ceiling")]
        public void RecalculateStats_RaisesTheCeilingBeforeTheHealth()
        {
            var order = new List<string>();

            // ReactiveProperty emits its current value on subscribe, so the first call is the priming
            // one; only later calls are the writes under test.
            bool maxPrimed = false;
            bool hpPrimed = false;

            _subscriptions.Add(_unitStats.MaxHp.Subscribe(_ =>
            {
                if (maxPrimed) order.Add("max");
                maxPrimed = true;
            }));

            _subscriptions.Add(_unitStats.CurrentHp.Subscribe(_ =>
            {
                if (hpPrimed) order.Add("hp");
                hpPrimed = true;
            }));

            // Level 2 at full health: both numbers move, 100 -> 115.
            _charStats.UpLevel();

            Assert.AreEqual(115f, _unitStats.MaxHp.Value, 1e-3f, "baseHp 100 + 1 level x 15");
            Assert.AreEqual(115f, _unitStats.CurrentHp.Value, 1e-3f, "the fraction is preserved, and it was full");

            CollectionAssert.AreEqual(new[] { "max", "hp" }, order,
                "AS3 sets gg.maxhp (Pers.as:2165) before gg.hp (:2245); the reverse order is what made " +
                "the HUD print a maximum that had not been written yet");
        }

        [Test]
        [Description("A level-up at full health leaves no gap between the two numbers")]
        public void UpLevel_AtFullHealth_KeepsHealthEqualToTheNewCeiling()
        {
            _charStats.UpLevel();

            Assert.AreEqual(_unitStats.MaxHp.Value, _unitStats.CurrentHp.Value, 1e-3f,
                "a full-health player must not read 115/100 after levelling");
        }

        [Test]
        [Description("A level-up while wounded scales the pool, it does not refill it")]
        public void UpLevel_WhileWounded_PreservesTheFraction()
        {
            _unitStats.CurrentHp.Value = 50f;   // half of 100

            _charStats.UpLevel();

            Assert.AreEqual(115f, _unitStats.MaxHp.Value, 1e-3f);
            Assert.AreEqual(57.5f, _unitStats.CurrentHp.Value, 1e-3f, "half of the new ceiling");
        }

        // ── Read side: the text follows a max-only change ────────────────────

        [Test]
        [Description("HealthText republishes when only the maximum changes")]
        public void HealthText_FollowsAMaxChangeWithNoHealthChange()
        {
            var viewModel = BuildViewModel();

            string latest = null;
            _subscriptions.Add(viewModel.HealthText.Subscribe(text => latest = text));

            Assert.AreEqual("100 / 100", latest, "primed with the live pair");

            // The exact shape of the old bug: the ceiling moves and the health does not.
            _unitStats.MaxHp.Value = 115f;

            Assert.AreEqual("100 / 115", latest,
                "the readout must not keep the old ceiling — that is the 'max health lags behind' report");
        }

        [Test]
        [Description("HealthText rounds up, as GUI.setHp does")]
        public void HealthText_CeilsLikeTheOracle()
        {
            var viewModel = BuildViewModel();

            string latest = null;
            _subscriptions.Add(viewModel.HealthText.Subscribe(text => latest = text));

            // AS3 `GUI.as:944`: `Math.ceil(hp) + "/" + Math.ceil(maxhp)` — a player on 0.4 hp reads
            // "1", not "0". A bar at zero with the player still alive is the reading that looks like a bug.
            _unitStats.CurrentHp.Value = 84.3f;

            Assert.AreEqual("85 / 100", latest);
        }

        private HUDViewModel BuildViewModel()
        {
            _hudGo = new GameObject("HealthReadoutTestHud");
            var viewModel = _hudGo.AddComponent<HUDViewModel>();

            // No loadout and no organ source: the weapon and mana-organ bindings stay unbound, which is
            // legitimate (the bootstrapper rebinds when they appear) and keeps this fixture to the
            // health readout.
            viewModel.Initialize(loadout: null, stats: _unitStats, organs: null);
            return viewModel;
        }
    }
}
