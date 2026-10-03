using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using PFE.Data;
using PFE.Data.Definitions;
using PFE.ModAPI;
using PFE.Systems.Weapons;

namespace PFE.Tests.Editor.Content
{
    /// <summary>
    /// Pins <see cref="GameDatabase.GetItem"/> — the lookup <c>GameInventory.AddArmor</c> needs — and
    /// the decision behind it: <b>armour registers under <see cref="ContentType.Item"/>, the same type
    /// as every other item.</b>
    ///
    /// <para><b>Why armour is a separate Resources folder but not a separate ContentType.</b> The 35
    /// <c>&lt;armor&gt;</c> elements are a different element type from AllData's 500 <c>&lt;item&gt;</c>
    /// ones, and <c>DataImportVerificationTests</c> pins <c>Resources/Items</c> at exactly 451 assets —
    /// so the importer writes to <c>Resources/Armor</c> and <c>BuiltInContentSource</c> loads it
    /// separately. But <see cref="ItemDefinition"/> declares <c>ContentType.Item</c> for itself, so both
    /// folders land in one registry namespace. That is safe here (no id appears in both sets) and
    /// matches AS3, where armour and items share the item list.</para>
    ///
    /// <para><b>Registration goes through <see cref="ContentRegistry.Register"/>, not the legacy
    /// <c>Register*</c> methods.</b> Those write only to <c>GameDatabase</c>'s private dictionaries —
    /// <c>RegisterRoomTemplate</c> never touches the registry — which is why <c>GetUnit</c>/<c>GetWeapon</c>
    /// carry a legacy fallback and <c>GetItem</c> does not. The registry path is what
    /// <c>BuiltInContentSource</c> uses in production, so it is the path worth testing.</para>
    /// </summary>
    [TestFixture]
    public class GameDatabaseItemLookupTests
    {
        private readonly List<Object> _created = new List<Object>();
        private GameDatabase _database;

        [SetUp]
        public void SetUp()
        {
            _database = new GameDatabase();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _created)
                if (created != null) Object.DestroyImmediate(created);

            _created.Clear();
        }

        private ItemDefinition Definition(string id)
        {
            var definition = ScriptableObject.CreateInstance<ItemDefinition>();
            definition.itemId = id;
            _created.Add(definition);
            return definition;
        }

        /// <summary>Registers exactly the way <c>BuiltInContentSource</c> does.</summary>
        private void Register(ItemDefinition definition)
            => _database.Registry.Register(ModManifest.CreateBaseGame(), definition);

        [Test]
        public void GetItem_FindsABareIdThroughTheBaseGameAlias()
        {
            ItemDefinition item = Definition("pot1");
            Register(item);

            Assert.AreSame(item, _database.GetItem("pot1"),
                "A bare id must resolve: the registry stores it as pfe.base.pot1 and keeps the bare name " +
                "as an alias, which is what every legacy caller passes.");
        }

        [Test]
        public void GetItem_FindsTheNamespacedId()
        {
            ItemDefinition item = Definition("pot1");
            Register(item);

            Assert.AreSame(item, _database.GetItem("pfe.base.pot1"));
        }

        [Test]
        public void GetItem_FindsAnItemAndAnArmourUnderTheSameContentType()
        {
            ItemDefinition item = Definition("pot1");

            ItemDefinition armour = Definition("kombu");
            armour.armourLevels = new[] { new EquipmentData { armor = 4, reliability = 0.6f } };
            armour.equipment = armour.armourLevels[0];

            Register(item);
            Register(armour);

            Assert.AreSame(item, _database.GetItem("pot1"), "A plain item.");
            Assert.AreSame(armour, _database.GetItem("kombu"), "And an armour, through the same lookup.");
            Assert.AreEqual(4, _database.GetItem("kombu").armourLevels[0].armor,
                "The per-level table survives the round trip through the registry.");
        }

        [Test]
        public void GetItem_ReturnsNullForAnUnknownId()
        {
            Register(Definition("pot1"));

            Assert.IsNull(_database.GetItem("kombu"));
        }

        [Test]
        public void GetItem_ReturnsNullForNullOrEmpty()
        {
            Assert.IsNull(_database.GetItem(null));
            Assert.IsNull(_database.GetItem(string.Empty));
        }

        [Test]
        public void GetAllItemIDs_ListsTheBareIds()
        {
            Register(Definition("pot1"));
            Register(Definition("kombu"));

            CollectionAssert.AreEquivalent(new[] { "pot1", "kombu" }, _database.GetAllItemIDs());
        }

        [Test]
        public void AnIdUsedTwice_KeepsTheFirstAndWarns()
        {
            // The hazard of one shared ContentType. Today there is no collision — 35 armour ids against
            // 500 item ids, disjoint — but that is a property of the *data*, not of the code, so the
            // failure mode is pinned here: first registration wins and the second only warns.
            ItemDefinition first = Definition("clash");
            ItemDefinition second = Definition("clash");

            Register(first);

            LogAssert.Expect(LogType.Warning,
                "[ContentRegistry] Item/pfe.base.clash: duplicate from pfe.base, kept pfe.base");

            Register(second);

            Assert.AreSame(first, _database.GetItem("clash"),
                "Additive policy keeps the first; an armour that shadowed an item id would lose silently.");
        }
    }

    /// <summary>
    /// Pins the <b>wiring invariant</b> behind <see cref="ContentRegistryAmmoResolver"/>: the registry
    /// the consumers are handed must be the one <see cref="GameDatabase"/> actually initialized.
    ///
    /// <para><b>Why this fixture exists.</b> <see cref="GameDatabase"/> builds its own
    /// <c>ContentRegistry</c> in its constructor and <c>GameManager</c> populates that one through
    /// <c>gameDatabase.Initialize()</c>. <c>GameLifetimeScope</c> separately registered a
    /// <c>ContentRegistry</c> singleton for injection — a <i>second</i>, never-initialized table — and
    /// that is what <c>PlayerWeaponLoadout</c> was handed. So the ammo resolver answered <c>null</c> for
    /// every id for the whole session: the round's damage multiplier, armour piercing, armour
    /// multiplier, knockback, precision, penetration budget and damage-type override were all silently
    /// dropped, and selecting the armour-piercing <c>p5_1</c> in the F2 dropdown changed nothing — a
    /// 20-armour training dummy kept taking 0 from the 7-damage minigun.</para>
    ///
    /// <para><b>What made it invisible.</b> A null ammo row is AS3's <i>"неправильный патрон"</i> branch
    /// — every ammo term stays at its identity — so the failure is numerically identical to "this
    /// round has no special properties". The F2 dropdown could not show it either, because it loads
    /// <c>AmmoDefinition</c> assets from <c>Resources/Ammo</c> itself and printed the round's real
    /// <c>pierce +40</c>. Three layers each looked correct; only the injection seam was wrong.</para>
    ///
    /// <para>The first two tests are the positive and the regression; the third is the absent control
    /// that keeps the new error specific to "empty registry" rather than firing for any miss.</para>
    /// </summary>
    [TestFixture]
    public class AmmoResolverRegistryWiringTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _created)
                if (created != null) Object.DestroyImmediate(created);

            _created.Clear();
        }

        private AmmoDefinition Round(string id, int armourPiercing)
        {
            var round = ScriptableObject.CreateInstance<AmmoDefinition>();
            round.ammoId = id;
            round.baseId = "p5";
            round.armorPiercingBonus = armourPiercing;
            _created.Add(round);
            return round;
        }

        /// <summary>
        /// Whether <see cref="LogAssert"/> can be used at all.
        ///
        /// <para>Offline — outside the Unity Test Runner — <c>LogAssert</c> has no log scope and throws
        /// <c>InvalidOperationException: No log scope is available</c>. That is an <b>unreachable
        /// arrange step, not a red test</b>, so the two log-asserting tests below return early rather
        /// than reporting a failure the offline wall would have to be taught to classify. The
        /// behavioural assertion still runs under the editor runner, which is where it matters.</para>
        /// </summary>
        private static bool LogAssertIsAvailable()
        {
            try
            {
                LogAssert.NoUnexpectedReceived();
                return true;
            }
            catch (System.InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// The correct wiring: the resolver is built from the database's own registry, which is the
        /// table <c>Initialize()</c> fills. The round's ballistics must come through intact.
        /// </summary>
        [Test]
        public void Resolver_BuiltFromTheDatabasesRegistry_FindsTheRoundAndItsBallistics()
        {
            var database = new GameDatabase();
            AmmoDefinition ap = Round("p5_1", armourPiercing: 40);
            database.Registry.Register(ModManifest.CreateBaseGame(), ap);

            var resolver = new ContentRegistryAmmoResolver(database.Registry);
            AmmoDefinition resolved = resolver.Resolve("p5_1");

            Assert.AreSame(ap, resolved, "A bare id must resolve through the pfe.base alias.");
            Assert.AreEqual(40, resolved.armorPiercingBonus,
                "The piercing the shot folds into DamageContext.Piercing must survive the lookup.");
        }

        /// <summary>
        /// The regression pin — this is the state production was in. A fresh registry is exactly what
        /// <c>builder.Register&lt;ContentRegistry&gt;(Lifetime.Singleton)</c> handed out, and it
        /// resolves nothing. It must also now say so, once, instead of failing silently.
        /// </summary>
        [Test]
        public void Resolver_BuiltFromAnUninitializedRegistry_ResolvesNothingAndReportsTheWiringFault()
        {
            if (!LogAssertIsAvailable()) return;

            var resolver = new ContentRegistryAmmoResolver(new ContentRegistry());

            LogAssert.Expect(LogType.Error, new Regex("ContentRegistryAmmoResolver.*no Ammo rows"));

            Assert.IsNull(resolver.Resolve("p5_1"),
                "An uninitialized registry cannot know any round — this is the silent 0-damage state.");
        }

        /// <summary>
        /// Absent control. The empty-registry error must be specific: a resolver with no registry at all
        /// is the legitimate headless/test case and must stay quiet, or every fixture would log errors
        /// and the real one would be lost in the noise.
        /// </summary>
        [Test]
        public void Resolver_WithNoRegistry_ResolvesNothingAndStaysSilent()
        {
            if (!LogAssertIsAvailable()) return;

            var resolver = new ContentRegistryAmmoResolver(null);

            Assert.IsNull(resolver.Resolve("p5_1"));

            LogAssert.NoUnexpectedReceived();
        }
    }
}
