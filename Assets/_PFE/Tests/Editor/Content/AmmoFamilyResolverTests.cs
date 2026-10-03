using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Tests.Editor.Content
{
    /// <summary>
    /// Pins <see cref="AmmoFamilyResolver"/> — the family grouping the F2 ammo dropdown is built on.
    ///
    /// <para><b>The shape being pinned.</b> Ammo ids are <c>&lt;family&gt;</c> for the regular round and
    /// <c>&lt;family&gt;_&lt;n&gt;</c> for variants, with <see cref="AmmoDefinition.baseId"/> naming the family
    /// on every member. Verified against the shipped assets: <c>p32</c>/<c>p32_1</c>/<c>p32_2</c> all
    /// carry <c>baseId: p32</c>. These tests use hand-built definitions rather than the real assets so a
    /// change to the imported data cannot silently redefine the contract — the data has its own coverage.</para>
    /// </summary>
    [TestFixture]
    public class AmmoFamilyResolverTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _created.Clear();
        }

        private AmmoDefinition Make(string id, string baseId)
        {
            var def = ScriptableObject.CreateInstance<AmmoDefinition>();
            def.ammoId = id;
            def.baseId = baseId;
            _created.Add(def);
            return def;
        }

        private AmmoFamilyResolver.AmmoLookup LookupOf(params AmmoDefinition[] defs)
        {
            var byId = new Dictionary<string, AmmoDefinition>();
            foreach (var d in defs)
            {
                if (d != null) byId[d.ammoId] = d;
            }
            return id => byId.TryGetValue(id, out var found) ? found : null;
        }

        private AmmoFamilyResolver.AmmoIdEnumerator EnumerateOf(params AmmoDefinition[] defs)
        {
            return () =>
            {
                var ids = new List<string>();
                foreach (var d in defs)
                {
                    if (d != null) ids.Add(d.ammoId);
                }
                return ids;
            };
        }

        // ── StripVariantSuffix ────────────────────────────────────────────────

        [Test]
        public void StripVariantSuffix_RemovesTrailingNumericGroup()
        {
            Assert.AreEqual("p32", AmmoFamilyResolver.StripVariantSuffix("p32_1"));
            Assert.AreEqual("p32", AmmoFamilyResolver.StripVariantSuffix("p32_12"));
            Assert.AreEqual("p50mg", AmmoFamilyResolver.StripVariantSuffix("p50mg_2"));
        }

        [Test]
        public void StripVariantSuffix_LeavesNonVariantIdsAlone()
        {
            // A base id has no suffix, so it maps to itself — this is what makes IsRegular work.
            Assert.AreEqual("p32", AmmoFamilyResolver.StripVariantSuffix("p32"));
            // A non-numeric suffix is part of the name, not a variant index.
            Assert.AreEqual("p32_x", AmmoFamilyResolver.StripVariantSuffix("p32_x"));
            Assert.AreEqual("gren40", AmmoFamilyResolver.StripVariantSuffix("gren40"));
            // A trailing underscore with nothing after it is not a variant separator.
            Assert.AreEqual("p32_", AmmoFamilyResolver.StripVariantSuffix("p32_"));
        }

        // ── IsRegular ─────────────────────────────────────────────────────────

        [Test]
        public void IsRegular_TrueOnlyForTheFamilyRowItself()
        {
            Assert.IsTrue(AmmoFamilyResolver.IsRegular("p32"));
            Assert.IsTrue(AmmoFamilyResolver.IsRegular("batt"));
            Assert.IsFalse(AmmoFamilyResolver.IsRegular("p32_1"));
            Assert.IsFalse(AmmoFamilyResolver.IsRegular("batt_6"));
            Assert.IsFalse(AmmoFamilyResolver.IsRegular(string.Empty));
        }

        // ── FamilyOf ──────────────────────────────────────────────────────────

        [Test]
        public void FamilyOf_PrefersTheDefinitionsBaseId()
        {
            var variant = Make("p32_1", "p32");
            string family = AmmoFamilyResolver.FamilyOf("p32_1", LookupOf(variant));
            Assert.AreEqual("p32", family);
        }

        [Test]
        public void FamilyOf_FallsBackToTheNamingConventionWhenBaseIdIsBlank()
        {
            // 31 of the 75 shipped rows leave baseId empty. Without the textual fallback, such a row
            // would form a family of one and the dropdown would offer no variants at all.
            var variant = Make("p10_1", "");
            string family = AmmoFamilyResolver.FamilyOf("p10_1", LookupOf(variant));
            Assert.AreEqual("p10", family);
        }

        [Test]
        public void FamilyOf_UnknownIdStillUsesTheNamingConvention()
        {
            // No asset at all: the id must still group, so a weapon naming a mistyped variant does not
            // produce an empty list.
            Assert.AreEqual("p32", AmmoFamilyResolver.FamilyOf("p32_9", id => null));
        }

        // ── GetSelectableTypes ────────────────────────────────────────────────

        [Test]
        public void GetSelectableTypes_RegularFirstThenVariantsSorted()
        {
            var bas = Make("p32", "p32");
            var v2 = Make("p32_2", "p32");
            var v1 = Make("p32_1", "p32");

            var list = AmmoFamilyResolver.GetSelectableTypes(
                "p32", LookupOf(bas, v1, v2), EnumerateOf(v2, bas, v1));

            CollectionAssert.AreEqual(new[] { "p32", "p32_1", "p32_2" }, list);
        }

        [Test]
        public void GetSelectableTypes_ExcludesOtherFamilies()
        {
            var p32 = Make("p32", "p32");
            var p32v = Make("p32_1", "p32");
            var other = Make("p12", "p12");
            var otherV = Make("p12_3", "p12");

            var list = AmmoFamilyResolver.GetSelectableTypes(
                "p32", LookupOf(p32, p32v, other, otherV), EnumerateOf(p32, p32v, other, otherV));

            CollectionAssert.DoesNotContain(list, "p12");
            CollectionAssert.DoesNotContain(list, "p12_3");
            CollectionAssert.AreEqual(new[] { "p32", "p32_1" }, list);
        }

        [Test]
        public void GetSelectableTypes_FromAVariantStillListsTheWholeFamily()
        {
            // The currently-selected type may itself be a variant (after a previous swap). The list must
            // stay the full family, not shrink to that variant, or the dropdown could never go back.
            var bas = Make("p32", "p32");
            var v1 = Make("p32_1", "p32");
            var v2 = Make("p32_2", "p32");

            var list = AmmoFamilyResolver.GetSelectableTypes(
                "p32_2", LookupOf(bas, v1, v2), EnumerateOf(bas, v1, v2));

            CollectionAssert.AreEqual(new[] { "p32", "p32_1", "p32_2" }, list);
        }

        [Test]
        public void GetSelectableTypes_EmptyWhenWeaponHasNoAmmoType()
        {
            // 134 of 213 weapons are melee/magic/unarmed with a blank ammoType; the caller uses the empty
            // list to decide to show "— (no ammo)" instead of a dropdown.
            var list = AmmoFamilyResolver.GetSelectableTypes(string.Empty, id => null, () => new string[0]);
            Assert.IsEmpty(list);
        }

        [Test]
        public void GetSelectableTypes_SingleEntryWhenNothingIsKnown()
        {
            // A weapon naming an ammo id with no asset and no enumerable siblings. One entry, not zero —
            // an empty dropdown is indistinguishable from "this weapon takes no ammo".
            var list = AmmoFamilyResolver.GetSelectableTypes("ghost", id => null, () => new string[0]);
            CollectionAssert.AreEqual(new[] { "ghost" }, list);
        }

        [Test]
        public void GetSelectableTypes_WorksWithoutAnEnumerator()
        {
            var bas = Make("p32", "p32");
            var list = AmmoFamilyResolver.GetSelectableTypes("p32", LookupOf(bas), null);
            CollectionAssert.AreEqual(new[] { "p32" }, list);
        }

        // ── Describe ──────────────────────────────────────────────────────────

        [Test]
        public void Describe_NamesTheMissingAssetInsteadOfInventingNumbers()
        {
            // A fabricated "×1.0" on a missing asset would read as a real value.
            string text = AmmoFamilyResolver.Describe("ghost", id => null);
            StringAssert.Contains("ghost", text);
            StringAssert.Contains("no AmmoDefinition", text);
            StringAssert.DoesNotContain("×1", text);
        }

        [Test]
        public void Describe_MarksTheRegularRound()
        {
            var bas = Make("p32", "p32");
            var v1 = Make("p32_1", "p32");
            var lookup = LookupOf(bas, v1);

            StringAssert.Contains("regular", AmmoFamilyResolver.Describe("p32", lookup));
            StringAssert.DoesNotContain("regular", AmmoFamilyResolver.Describe("p32_1", lookup));
        }
    }
}
