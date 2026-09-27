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
    /// <para><b>What is not tested here.</b> That the <i>data</i> carries the override: these are unit
    /// tests over the predicate, not a content contract. Every one of the 204 assets still holds the
    /// default <c>autoMode = 0</c> until <c>WeaponDataImporter</c> is re-run, so a content contract
    /// would be red today for a reason that has nothing to do with this rule. Add one after the
    /// re-bake, asserting <c>Resources.Load&lt;WeaponDefinition&gt;("Weapons/shotgun").IsAuto</c> is
    /// true and that a rapid-3 weapon with no override is still auto.</para>
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
            Object.DestroyImmediate(_def);
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
    }
}
