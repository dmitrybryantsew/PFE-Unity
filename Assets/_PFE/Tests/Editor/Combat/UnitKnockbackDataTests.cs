using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Guards the two knockback inputs that live in the shipped unit assets rather than in code:
    /// <c>massafix</c> and <c>knocked</c>.
    ///
    /// <para><b>Why a shipped-asset test and not a parser test.</b> The knockback formula itself is
    /// pinned in <c>KnockbackMathTests</c>, and it passes whatever the assets contain. The bug this file
    /// exists for is the one that class cannot see: the importer reading the wrong attribute, or the
    /// asset never being re-imported, so the game runs with a stale number while every in-memory test
    /// stays green. Reading the real <c>.asset</c> is the only thing that goes red for that.</para>
    ///
    /// <para><b>The three assertions that matter, and what each would catch:</b></para>
    /// <list type="bullet">
    /// <item><description><c>turret1</c> / <c>turret3</c> — AS3 prefers <c>@massafix</c> over
    /// <c>@massa</c> on the same <c>&lt;phis&gt;</c> node (<c>Unit.as:1049-1058</c>). Both author BOTH,
    /// with values 4.5x-12.5x apart, so a port that reads only <c>massa</c> throws them far too easily.
    /// These two are also the only massafix units that are not <c>fixed='1'</c>, i.e. the only ones whose
    /// knockback is visible in AS3.</description></item>
    /// <item><description><c>raider</c> — has no <c>&lt;phis&gt;</c> node at all, so its weight is AS3's
    /// <c>Obj</c> default of 1 (<c>Obj.as:28</c>). This is the control that proves the fallback path is
    /// the one being exercised, rather than the whole file agreeing with itself.</description></item>
    /// <item><description>the sentinel count — most units do NOT author <c>massafix</c>, and they must
    /// read <c>0</c> so that <see cref="UnitDefinition.Massa"/> falls back to <c>mass</c>. A field that
    /// defaulted to anything else would silently rescale every unit in the game.</description></item>
    /// </list>
    ///
    /// <para><b>This file failed for exactly as long as the assets were stale, and that was the point.</b>
    /// Until the unit importer was re-run the two turret assertions read data the assets did not have
    /// yet, so a version of this file that passed earlier would have been asserting the bug. The
    /// re-import ran on 2026-10-01 21:13 (<c>PFE/Data/Reimport Units (Overwrite Existing)</c>) and
    /// rewrote all 148 unit assets, so the numbers below are now live data rather than an
    /// expectation.</para>
    ///
    /// <para>If this file goes red again the cause is almost always one of two things, and the failure
    /// message names which: the importer did not read the attribute (<c>massafix</c> reading <c>0</c>,
    /// or <c>knocked</c> reading the integer <c>1</c> where the oracle says <c>0.3</c>), or the assets
    /// are stale because the re-import never ran.</para>
    /// </summary>
    [TestFixture]
    public class UnitKnockbackDataTests
    {
        private static UnitDefinition Load(string id) => Resources.Load<UnitDefinition>("Units/" + id);

        [Test]
        public void TheUnitAssetSetIsNonEmpty()
        {
            // Positive control for every assertion below. Without it, a renamed Resources folder would
            // make each Load return null and the file would pass vacuously on "no data".
            UnitDefinition[] units = Resources.LoadAll<UnitDefinition>("Units");

            Assert.Greater(units.Length, 100,
                "the Units folder should hold the whole roster; if this is 0 the other tests here " +
                "are asserting nothing");
        }

        [Test]
        public void MassafixIsImported_AndWinsOverMassa()
        {
            // turret1: <phis sX='38' sY='60' massa='110' massafix='500'/>
            UnitDefinition turret1 = Load("turret1");
            Assert.IsNotNull(turret1, "turret1.asset should exist");

            Assert.AreEqual(110f, turret1.mass, 1e-4f,
                "the raw 'massa' attribute is still imported — it is the fallback, not dead data");
            Assert.AreEqual(500f, turret1.massafix, 1e-4f,
                "turret1's 'massafix' — 0 here means the field was not imported; re-run the unit importer");
            Assert.AreEqual(10f, turret1.Massa, 1e-4f,
                "500 / 50 — AS3 uses massafix when present (Unit.as:1049-1058), not massa (110/50 = 2.2)");

            // turret3: <phis sX='40' sY='70' massa='160' massafix='1000'/>
            UnitDefinition turret3 = Load("turret3");
            Assert.IsNotNull(turret3, "turret3.asset should exist");

            Assert.AreEqual(1000f, turret3.massafix, 1e-4f, "turret3's 'massafix'");
            Assert.AreEqual(20f, turret3.Massa, 1e-4f, "1000 / 50, not 160 / 50 = 3.2");
        }

        [Test]
        public void MassafixZeroMeansAbsent_SoMassaApplies()
        {
            // The sentinel. Every asset written before the field existed reads 0, which must mean "use
            // mass" — so this is also the assertion that a re-import cannot rescale the whole roster.
            UnitDefinition[] units = Resources.LoadAll<UnitDefinition>("Units");

            int withMassafix = 0;
            foreach (UnitDefinition u in units)
            {
                if (u == null) continue;

                if (u.massafix > 0f)
                {
                    withMassafix++;
                    Assert.AreEqual(u.massafix / 50f, u.Massa, 1e-4f, $"{u.id}: massafix must win");
                }
                else
                {
                    Assert.AreEqual(u.mass / 50f, u.Massa, 1e-4f, $"{u.id}: must fall back to mass");
                }
            }

            // The oracle authors massafix on exactly 6 <phis> nodes. Allow the imported set to grow, but
            // not to be empty — an all-zero roster would mean the field is not being written at all, and
            // the loop above would then be a very thorough assertion about nothing.
            Assert.GreaterOrEqual(withMassafix, 6,
                "only 6 units in AllData.as author 'massafix'; finding fewer means the importer " +
                "missed some — re-run it and check the <phis> attribute name");
        }

        [Test]
        public void AUnitWithNoPhisNode_KeepsTheOracleDefaultWeight()
        {
            // raider declares <move> but no <phis>, so AS3 never assigns massaMove/massaFix and `massa`
            // stays at Obj's declared default of 1 (Obj.as:28). The port's equivalent is mass = 50 and
            // Massa = 50 / 50 = 1, which is the same quantity expressed in the un-divided unit.
            UnitDefinition raider = Load("raider");
            Assert.IsNotNull(raider, "raider.asset should exist");

            Assert.AreEqual(50f, raider.mass, 1e-4f, "the un-authored default, in raw-attribute units");
            Assert.AreEqual(1f, raider.Massa, 1e-4f, "AS3's Obj default massa of 1");
        }

        [Test]
        public void KnockedIsImported_ForTheTwoMovableTurrets()
        {
            // AS3 reads this from the <move> node (Unit.as:1063 takes node = node0.move[0]; :1082-1085
            // reads node.@knocked), not from <phis>. turret1 and turret3 are the two massafix units that
            // are not fixed='1', so their knocked value is what actually scales a bullet's shove.
            foreach (string id in new[] { "turret1", "turret3" })
            {
                UnitDefinition turret = Load(id);
                Assert.IsNotNull(turret, $"{id}.asset should exist");

                Assert.AreEqual(0.3f, turret.knocked, 1e-4f,
                    $"{id} authors knocked='0.3'; 1.0 here means <move> parsing did not run, or the " +
                    "integer-only pattern dropped the decimal");
            }
        }
    }
}
