using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Guards AS3's <c>fixed</c> — the flag that gates the whole position integration
    /// (<c>Unit.as:1809</c>) — in each of the three places it is authored.
    ///
    /// <para><b>Why three files and not one.</b> <c>fixed</c> reaches the game by three independent
    /// routes, and a guard on any one of them is blind to the other two:</para>
    /// <list type="number">
    /// <item><description><b>The definition</b> — <c>fixed='1'</c> on a <c>&lt;move&gt;</c> node
    /// (<c>Unit.as:1114-1116</c>), authored by 16 unit templates. Imported into
    /// <see cref="UnitDefinition.isFixed"/>.</description></item>
    /// <item><description><b>The placement</b> — <c>fix="1"</c> on a room's <c>&lt;obj&gt;</c> row,
    /// read by <c>UnitTrain.as:34-37</c>. This is <i>per placement</i>, so one
    /// <c>&lt;unit id='training'&gt;</c> row produces both pinned and free dummies.</description></item>
    /// <item><description><b>The gate itself</b> — <c>UnitController.Move()</c> must actually consult
    /// the flag. This is the one that rotted: the dummy's <c>ApplyPlacement</c> carried a comment
    /// saying <c>fix</c> was deliberately unconsumed because "the port has neither a motor nor
    /// knockback for a spawned unit yet". Both had long since become false, and nothing went red.</description></item>
    /// </list>
    ///
    /// <para><b>Two attributes, two different tests, and the difference is load-bearing.</b> The base
    /// class compares a <i>value</i> — <c>node.@fixed &gt; 0</c> (<c>Unit.as:1116</c>) — so
    /// <c>fixed='0'</c> is <i>not</i> pinned. The dummy's subclass tests for <i>presence</i> —
    /// <c>if(param3.@fix.length())</c> (<c>UnitTrain.as:34</c>) — so <c>fix="0"</c> <b>is</b> pinned.
    /// An implementation that "simplified" the two into one rule fails one of the tests below
    /// whichever way it collapsed them.</para>
    ///
    /// <para><b>These definition tests were expected to FAIL until the unit importer was re-run</b>
    /// (<c>PFE/Data/Reimport Units (Overwrite Existing)</c>), because the shipped <c>.asset</c> files
    /// read <c>isFixed: 0</c> and a version of this file that passed before the re-import would have
    /// been asserting the bug. The re-import ran on <b>2026-10-01 21:13</b> and the 16 pinned assets
    /// now match the oracle exactly.</para>
    /// </summary>
    [TestFixture]
    public class UnitFixedTests
    {
        /// <summary>
        /// The units whose <c>&lt;move&gt;</c> node authors <c>fixed='1'</c>, derived from
        /// <c>AllData.as</c> — 16 of them, and the count is load-bearing for
        /// <see cref="TheFixedSetIsNotSilentlyEmpty"/>.
        ///
        /// <para><b><c>turret</c> is deliberately absent, and it used to be listed here.</b> It looks
        /// like the obvious member of the turret family, but <c>AllData.as:1758</c> is
        /// <c>&lt;unit id='turret' cat='2'/&gt;</c> — a <b>self-closing</b> base entry with no children
        /// at all. It authors no <c>&lt;move&gt;</c>, so it authors no <c>fixed</c>; the concrete
        /// turrets inherit from it with <c>cont='turret'</c> and each declares
        /// <c>&lt;move fixed='1'/&gt;</c> of its own (<c>:1761</c>, <c>:1781</c>, <c>:1801</c>,
        /// <c>:1871</c>). Listing the base made this test demand a pin the oracle never gives, so it
        /// failed no matter how many times the importer was re-run — and it is why
        /// <see cref="TheFixedSetIsNotSilentlyEmpty"/> read 16 against an expected 17. Derived by
        /// scanning the oracle for units whose <c>&lt;move&gt;</c> carries <c>fixed='1'</c>: 16 found,
        /// and the 17th entry was the only one unmatched.</para>
        /// </summary>
        private static readonly string[] AuthoredFixedUnits =
        {
            "captive", "ponpon", "ebloat", "eant",
            "turret0", "turret2", "turret4", "turret5",
            "trigcans", "trigridge", "trigplate", "triglaser",
            "damshot", "damgren", "damexpl1", "mwall",
        };

        private static UnitDefinition Load(string id) => Resources.Load<UnitDefinition>("Units/" + id);

        // ── 1. the definition-level flag ─────────────────────────────────────────────────────────

        [Test]
        public void TheUnitAssetSetIsNonEmpty()
        {
            // Positive control. Without it, a renamed Resources folder makes every Load below return
            // null and the file passes vacuously on "no data".
            UnitDefinition[] units = Resources.LoadAll<UnitDefinition>("Units");

            Assert.Greater(units.Length, 100,
                "the Units folder should hold the whole roster; if this is 0 the other tests here " +
                "are asserting nothing");
        }

        [Test]
        public void EveryAuthoredFixedUnit_IsPinnedInItsShippedAsset()
        {
            var missing = new List<string>();
            var unpinned = new List<string>();

            foreach (string id in AuthoredFixedUnits)
            {
                UnitDefinition unit = Load(id);
                if (unit == null)
                {
                    missing.Add(id);
                    continue;
                }

                if (!unit.isFixed)
                {
                    unpinned.Add(id);
                }
            }

            Assert.IsEmpty(missing,
                "these authored units have no shipped asset, so the rest of this test cannot see them");
            Assert.IsEmpty(unpinned,
                "AS3 authors fixed='1' on these <move> nodes; a false here means the importer never " +
                "read the attribute (or the asset is stale) — re-run the unit importer");
        }

        [Test]
        public void ThePlayerIsNotPinned()
        {
            // The complement, and the one that matters most in the other direction: <unit id='littlepip'>
            // authors <move speed='7' accel='3.5'/> and no `fixed`, so the gate must leave the player
            // alone. A rule that pinned anything carrying a <move> node would pass the test above and
            // freeze the player.
            UnitDefinition player = Load("littlepip");
            Assert.IsNotNull(player, "littlepip.asset should exist");

            Assert.IsFalse(player.isFixed,
                "the player authors no `fixed` — a true here means the gate would stop the player moving");
        }

        [Test]
        public void TheFixedSetIsNotSilentlyEmpty()
        {
            // Counts the whole roster rather than the list above, so an importer that wrote `false`
            // everywhere is caught even if the list above were edited to match.
            UnitDefinition[] units = Resources.LoadAll<UnitDefinition>("Units");

            int pinned = 0;
            foreach (UnitDefinition unit in units)
            {
                if (unit != null && unit.isFixed) pinned++;
            }

            Assert.GreaterOrEqual(pinned, AuthoredFixedUnits.Length,
                $"the oracle authors `fixed='1'` on {AuthoredFixedUnits.Length} units; finding fewer " +
                "means the importer is not writing the field at all");
        }

        // ── 2. the dummy's placement-level flag ──────────────────────────────────────────────────

        private static UnitInstance Placement(params (string key, string value)[] attributes)
        {
            var placement = new UnitInstance { unitId = "training" };

            foreach ((string key, string value) in attributes)
            {
                placement.attributes.Add(new MapObjectAttributeData { key = key, value = value });
            }

            return placement;
        }

        /// <summary>
        /// A dummy with the <c>training</c> definition (which authors no <c>fixed</c> of its own), so
        /// anything <see cref="UnitController.IsFixed"/> reports came from the placement.
        /// </summary>
        private static TrainingDummyController MakeDummy(UnitDefinition definition)
        {
            var go = new GameObject("TrainingDummyUnderTest");
            var dummy = go.AddComponent<TrainingDummyController>();
            dummy.Initialize(definition, new UnitStats(500f, 100f));
            return dummy;
        }

        private static UnitDefinition MakeTrainingDefinition(bool isFixed = false)
        {
            var definition = ScriptableObject.CreateInstance<UnitDefinition>();
            definition.id = "training";
            definition.health = 500;
            definition.isFixed = isFixed;
            return definition;
        }

        /// <summary>
        /// <c>UnitTrain.as:34</c> is <c>if(param3.@fix.length())</c> — a <b>presence</b> test, so any
        /// non-empty value pins the dummy, including <c>"0"</c>. That is the opposite of the base
        /// class's <c>node.@fixed &gt; 0</c> at <c>Unit.as:1116</c>, and the <c>"0"</c> case below is
        /// what separates the two rules.
        /// </summary>
        [TestCase("1")]
        [TestCase("0")]
        [TestCase("true")]
        public void ADummyOnAPlacementCarryingFix_IsPinned(string value)
        {
            UnitDefinition definition = MakeTrainingDefinition();
            TrainingDummyController dummy = MakeDummy(definition);

            try
            {
                dummy.ApplyPlacement(Placement(("turn", "-1"), ("fix", value)));

                Assert.IsTrue(dummy.IsFixed,
                    $"fix=\"{value}\" is present, and UnitTrain.as:34 tests presence, not value");
            }
            finally
            {
                Object.DestroyImmediate(dummy.gameObject);
                Object.DestroyImmediate(definition);
            }
        }

        [Test]
        public void ADummyOnAPlacementWithoutFix_IsNotPinned()
        {
            // The complement. Without it, a controller that returned `true` unconditionally would pass
            // the test above, and two of the Camp's five dummies would be frozen for no reason.
            UnitDefinition definition = MakeTrainingDefinition();
            TrainingDummyController dummy = MakeDummy(definition);

            try
            {
                dummy.ApplyPlacement(Placement(("turn", "-1"), ("light", "1")));

                Assert.IsFalse(dummy.IsFixed,
                    "this placement carries no `fix` — the dummy must be free to be pushed");
            }
            finally
            {
                Object.DestroyImmediate(dummy.gameObject);
                Object.DestroyImmediate(definition);
            }
        }

        [Test]
        public void AFixedDefinition_PinsADummyWhosePlacementSaysNothing()
        {
            // The base-class route, through the same controller: `Unit.as:1116` sets `fixed` from the
            // <move> node, which is independent of the placement attribute. The dummy is used here only
            // because it is the one concrete controller the port has — the rule belongs to Unit.
            UnitDefinition definition = MakeTrainingDefinition(isFixed: true);
            TrainingDummyController dummy = MakeDummy(definition);

            try
            {
                dummy.ApplyPlacement(Placement(("turn", "-1")));

                Assert.IsTrue(dummy.IsFixed,
                    "the definition's own `fixed` must pin the unit with no help from the placement");
            }
            finally
            {
                Object.DestroyImmediate(dummy.gameObject);
                Object.DestroyImmediate(definition);
            }
        }

        // ── 3. the real camp data, end to end ────────────────────────────────────────────────────

        [Test]
        public void TheCampTestGround_PlacesFiveDummies_AndExactlyThreeArePinned()
        {
            // The shipped room, not a fixture. RoomsCamp.as:413/419/420 author fix="1" on three of the
            // five <obj id="training"> rows, and this asserts the data the game actually loads still
            // says so — the placement half of the chain, which the definition tests cannot see.
            RoomTemplate camp = Resources.Load<RoomTemplate>("Rooms/rooms_rbl/room_1_0");
            Assert.IsNotNull(camp, "the camp room (rooms_rbl/room_1_0) should be in Resources");

            int total = 0;
            int pinned = 0;

            foreach (ObjectSpawnData spawn in camp.objects)
            {
                if (spawn == null || spawn.id != "training") continue;

                total++;
                if (!string.IsNullOrEmpty(MapObjectDataUtility.GetAttribute(spawn.attributes, "fix")))
                {
                    pinned++;
                }
            }

            Assert.AreEqual(5, total, "the camp's test ground places five dummies");
            Assert.AreEqual(3, pinned,
                "RoomsCamp.as pins three of them (fix=\"1\"); the other two carry only turn/light");
        }
    }
}
