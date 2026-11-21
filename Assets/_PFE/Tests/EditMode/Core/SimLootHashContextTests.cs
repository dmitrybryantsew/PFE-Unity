using System;
using NUnit.Framework;
using PFE.Sim.State;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The reverse content catalog a replicated drop needs: <c>SimDroppedItem.ItemIdHash</c> is a
    /// one-way hash, so the client rebuilds hash → id from its OWN registry and both sides must agree
    /// because they ship the same content.
    /// </summary>
    [TestFixture]
    public sealed class SimLootHashContextTests
    {
        [TearDown]
        public void TearDown()
        {
            SimLootHashContext.Reset();
        }

        private static void SetDemoSources()
        {
            SimLootHashContext.SetSources(
                itemIds: () => new[] { "stimpak", "ammo_9mm", "money" },
                weaponIds: () => new[] { "r32", "mont" });
        }

        [Test]
        public void ItemId_RoundTripsThroughTheHash()
        {
            SetDemoSources();

            string id;
            ulong hash = SimSnapshotProjectionRules.LootItemKey("stimpak", isWeapon: false);
            Assert.That(SimLootHashContext.TryGetId(hash, isWeapon: false, out id), Is.True);
            Assert.That(id, Is.EqualTo("stimpak"));
        }

        [Test]
        public void WeaponId_RoundTripsThroughTheHash()
        {
            SetDemoSources();

            string id;
            ulong hash = SimSnapshotProjectionRules.LootItemKey("r32", isWeapon: true);
            Assert.That(SimLootHashContext.TryGetId(hash, isWeapon: true, out id), Is.True);
            Assert.That(id, Is.EqualTo("r32"));
        }

        [Test]
        public void TheWeaponPrefixSeparatesATableCollision()
        {
            // 32 ids exist in BOTH the item and the weapon tables in the shipped data; the isWeapon
            // half of the key is what keeps the two rows from collapsing into one hash entry.
            SetDemoSources();

            string id;
            Assert.That(SimLootHashContext.TryGetId(
                SimSnapshotProjectionRules.LootItemKey("stimpak", isWeapon: false),
                isWeapon: false, out id), Is.True);
            Assert.That(SimLootHashContext.TryGetId(
                SimSnapshotProjectionRules.LootItemKey("stimpak", isWeapon: true),
                isWeapon: true, out id), Is.False,
                "stimpak is not a weapon in the demo set; the prefixed lookup must not find it");
        }

        [Test]
        public void AnUnknownHash_AnswersFalse()
        {
            SetDemoSources();

            string id;
            Assert.That(SimLootHashContext.TryGetId(0xDEADBEEF00000001UL, false, out id), Is.False);
            Assert.That(id, Is.Null);
        }

        [Test]
        public void Count_ReflectsBothTables()
        {
            SetDemoSources();

            Assert.That(SimLootHashContext.Count, Is.EqualTo(5));
        }

        [Test]
        public void NoSources_BuildsAnEmptyMapThatAnswersFalse()
        {
            // A process that never published sources (a headless fixture) gets "cannot resolve",
            // never a throw — the applier counts these instead of crashing mid-Apply.
            string id;
            Assert.That(SimLootHashContext.TryGetId(123UL, false, out id), Is.False);
            Assert.That(SimLootHashContext.Count, Is.EqualTo(0));
        }

        [Test]
        public void ReplacingSources_RebuildsTheMap()
        {
            SetDemoSources();
            Assert.That(SimLootHashContext.Count, Is.EqualTo(5));

            SimLootHashContext.SetSources(() => new[] { "only_item" }, () => new string[0]);
            Assert.That(SimLootHashContext.Count, Is.EqualTo(1));

            string id;
            Assert.That(SimLootHashContext.TryGetId(
                SimSnapshotProjectionRules.LootItemKey("stimpak", false), false, out id), Is.False);
            Assert.That(SimLootHashContext.TryGetId(
                SimSnapshotProjectionRules.LootItemKey("only_item", false), false, out id), Is.True);
        }

        [Test]
        public void DuplicateIds_AreStoredOnce()
        {
            SimLootHashContext.SetSources(
                () => new[] { "double", "double" },
                () => new[] { "double" });

            Assert.That(SimLootHashContext.TryGetId(
                SimSnapshotProjectionRules.LootItemKey("double", false), false, out _), Is.True);
            Assert.That(SimLootHashContext.TryGetId(
                SimSnapshotProjectionRules.LootItemKey("double", true), true, out _), Is.True);
        }
    }
}
