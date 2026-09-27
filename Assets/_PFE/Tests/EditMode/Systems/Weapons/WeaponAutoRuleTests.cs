using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="WeaponDefinition.IsAuto"/> — AS3's <c>auto</c> flag, which decides whether a
    /// weapon keeps firing while the attack key is held or fires once per press.
    ///
    /// <para><b>Why this fixture exists.</b> <c>Weapon.as:856-859</c> is a two-step rule, and the port
    /// implemented only the first step:</para>
    /// <code>
    /// this.auto = this.rapid &lt;= 6;
    /// if(param1.@auto.length()) this.auto = param1.@auto != "0";
    /// </code>
    /// <para>All three controllers repeated <c>rapid &lt;= 6</c> inline and none read
    /// <c>char@auto</c>, so 14 weapons whose tier-1 <c>&lt;char&gt;</c> carries <c>auto='1'</c> with
    /// <c>rapid &gt; 6</c> — shotgun (12), bfg (15), mont (15), knife (8), lasp (7) and the rest —
    /// fired single-shot with a tap debounce where AS3 fires them continuously. The weapon definition
    /// is the only place the two halves can be kept together, so the rule lives there and the
    /// controllers read it.</para>
    ///
    /// <para>The last three tests are the <b>content contract</b> — they read the real imported assets,
    /// so they fail if the importer ever stops reading <c>char@auto</c>. They were deliberately withheld
    /// until <c>WeaponDataImporter</c> had been re-run: before that every asset held the default
    /// <c>autoMode = 0</c>, so they would have been red for a reason unrelated to this rule.</para>
    /// </summary>
    [TestFixture]
    public class WeaponAutoRuleTests
    {
        // autoMode encoding, mirroring the field: 0 = absent, 1 = forced single-shot, 2 = forced auto.
        private const int Absent      = 0;
        private const int ForcedOff   = 1;
        private const int ForcedOn    = 2;

        private WeaponDefinition _def;

        [SetUp]
        public void SetUp()
        {
            _def = ScriptableObject.CreateInstance<WeaponDefinition>();
        }

        [TearDown]
        public void TearDown()
        {
            // Qualified because this file also needs `using System;` (StringComparer.Ordinal at the
            // content contract), which brings System.Object into scope and makes a bare `Object`
            // ambiguous between it and UnityEngine.Object (CS0104).
            UnityEngine.Object.DestroyImmediate(_def);
        }

        // ── The heuristic half (Weapon.as:856) ────────────────────────────────

        [Test]
        public void OverrideAbsent_UsesTheRapidHeuristic()
        {
            _def.autoMode = Absent;

            _def.rapid = 12f;
            Assert.IsFalse(_def.IsAuto, "rapid 12 with no @auto override must be single-shot.");

            _def.rapid = 5f;
            Assert.IsTrue(_def.IsAuto, "rapid 5 with no @auto override must be continuous.");
        }

        [Test]
        public void OverrideAbsent_BoundaryAtSixIsInclusive()
        {
            // AS3 is `rapid <= 6`, not `< 6`. An off-by-one here is silent: it moves exactly one
            // weapon class between single-shot and continuous, and only autoaxe and railway sit on
            // the boundary — both carrying auto='1', so the override would mask the bug in content.
            _def.autoMode = Absent;
            _def.rapid = 6f;

            Assert.IsTrue(_def.IsAuto, "AS3 uses `rapid <= 6`, so rapid 6 must be continuous.");
        }

        [Test]
        public void DefaultAutoMode_IsAbsent_SoAFreshAssetKeepsTheHeuristic()
        {
            // The value every existing asset gets for a field its YAML does not mention. If this ever
            // stops being 0, 204 assets silently change meaning — see the field's own comment.
            Assert.AreEqual(Absent, _def.autoMode,
                "A field absent from old YAML must fall back to 'no override', not to 'forced single-shot'.");

            _def.rapid = 3f;
            Assert.IsTrue(_def.IsAuto, "…and a fresh definition must still fire continuously at rapid 3.");
        }

        // ── The override half (Weapon.as:857-859) ─────────────────────────────

        [Test]
        public void ForcedOn_WinsOverTheHeuristic_EvenWhenRapidIsSlow()
        {
            // The live case: 14 weapons. shotgun's tier-1 <char> is rapid='12' auto='1'.
            _def.autoMode = ForcedOn;
            _def.rapid = 12f;

            Assert.IsTrue(_def.IsAuto,
                "shotgun/bfg/mont/knife/… carry char auto='1' with rapid > 6 — the override must win.");
        }

        [Test]
        public void ForcedOff_WinsOverTheHeuristic_EvenWhenRapidIsFast()
        {
            // No asset in AllData.as carries auto='0', so this half of the AS3 rule is unexercised by
            // content. It is pinned here precisely because nothing else would notice it breaking.
            _def.autoMode = ForcedOff;
            _def.rapid = 3f;

            Assert.IsFalse(_def.IsAuto,
                "char auto='0' must force single-shot even at rapid 3, which the heuristic would call auto.");
        }

        [Test]
        public void ForcedOff_BeatsTheHeuristic_OnTheBoundary()
        {
            _def.autoMode = ForcedOff;
            _def.rapid = 6f;

            Assert.IsFalse(_def.IsAuto,
                "The override is applied after the heuristic, so it must win at the boundary too.");
        }

        [Test]
        public void TheThreeStates_AreDistinguishable_AtTheSameRapid()
        {
            // The point of a tri-state: at one rapid, absent and forced-on must differ, or the
            // override does nothing. A plain bool would collapse "absent" into one of the other two.
            _def.rapid = 12f;

            _def.autoMode = Absent;
            bool absent = _def.IsAuto;

            _def.autoMode = ForcedOff;
            bool forcedOff = _def.IsAuto;

            _def.autoMode = ForcedOn;
            bool forcedOn = _def.IsAuto;

            Assert.IsFalse(absent,    "absent at rapid 12 → heuristic → single-shot");
            Assert.IsFalse(forcedOff, "explicit '0' at rapid 12 → single-shot");
            Assert.IsTrue(forcedOn,   "explicit '1' at rapid 12 → continuous");
            Assert.AreNotEqual(absent, forcedOn, "absent and '1' must differ, or the override does nothing.");
        }

        // ── Content contract ──────────────────────────────────────────────────
        //
        // These read the imported assets, so they are the half that proves the *data* carries the
        // override — the unit tests above only prove the predicate is right.

        private static List<WeaponDefinition> LoadAllWeapons() =>
            Resources.LoadAll<WeaponDefinition>("Weapons").ToList();

        [Test]
        public void ImportedContent_TheSixteenCharAutoWeaponsCarryTheOverride()
        {
            var forcedAuto = LoadAllWeapons()
                .Where(d => d.autoMode == ForcedOn)
                .Select(d => d.weaponId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.AreEqual(
                new[]
                {
                    "a_expl", "aglau", "autoaxe", "bfg", "cdagger", "cknife", "edagger", "knife",
                    "lasp", "mont", "pshot", "railway", "rech", "saf9", "shotgun", "zknife",
                },
                forcedAuto,
                "AllData.as carries char auto='1' on 23 <char> variants across these 16 weapons " +
                "(7 of them have two variants; the importer reads tier-1 only). An empty list means the " +
                "importer is not reading char@auto at all; a different list means it is reading the " +
                "wrong <char>. Re-run PFE/Data/Import Weapons from AllData.as.");
        }

        [Test]
        public void ImportedContent_NoWeaponForcesSingleShot()
        {
            var forcedOff = LoadAllWeapons()
                .Where(d => d.autoMode == ForcedOff)
                .Select(d => d.weaponId)
                .ToList();

            CollectionAssert.IsEmpty(forcedOff,
                "Every one of the 23 char@auto overrides in AllData.as is auto='1' — none is auto='0'. " +
                "So autoMode == 1 anywhere means the importer is mis-parsing the attribute value. " +
                "Re-run PFE/Data/Import Weapons from AllData.as.");
        }

        [Test]
        public void ImportedContent_ShotgunIsAuto_WhereTheHeuristicAloneSaysNo()
        {
            var shotgun = Resources.Load<WeaponDefinition>("Weapons/shotgun");
            Assert.IsNotNull(shotgun, "shotgun.asset not found under Resources/Weapons.");

            // Guard the premise. If rapid ever drops to <= 6 the assertion below would pass on the
            // heuristic alone and this test would silently stop testing the override.
            Assert.Greater(shotgun.rapid, 6f,
                "shotgun.rapid must stay > 6, or this test can pass without the override.");

            Assert.IsTrue(shotgun.IsAuto,
                "AS3 fires the shotgun continuously — <char rapid='12' auto='1'>. Without the override " +
                "the rapid<=6 heuristic makes it single-shot with a tap debounce.");
        }
    }
}
