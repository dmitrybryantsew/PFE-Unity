using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core;
using PFE.Sim.Content;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// The ambient carrier for the intern tables — specifically the property that made it lazy: the
    /// content registry is empty at container build, so a table built there would be empty forever and
    /// every projected <c>DefinitionId</c> would be 0 for the whole session.
    ///
    /// <para><b>Why laziness is asserted rather than assumed.</b> This is the kind of ordering bug that
    /// leaves no trace: nothing throws, the tables are non-null, the lookups all answer
    /// <c>Uninterned</c>, and the only symptom is a replicated weapon id that is always 0 — which looks
    /// exactly like "the content table was never built" and like "this weapon is a mod nobody loaded".
    /// The counter that makes it visible is
    /// <c>SimProjectileSnapshotProjector.UninternedDefinitionCount</c>; the fixture below makes the
    /// mechanism itself impossible to break silently.</para>
    /// </summary>
    [TestFixture]
    public class ContentInternContextTests
    {
        [SetUp]
        public void SetUp()
        {
            ContentInternContext.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            // Process-wide static: a leaked source would make the next fixture's laziness assertion
            // read as a pass for the wrong reason.
            ContentInternContext.Reset();
        }

        /// <summary>
        /// Nothing is built until something asks. The source is a closure over the content registry, so
        /// counting its invocations is the only way to observe "the table was not built yet".
        /// </summary>
        [Test]
        public void SetSources_DoesNotBuildUntilFirstAccess()
        {
            int calls = 0;

            ContentInternContext.SetSources(
                () => { calls++; return new[] { "zombie", "skeleton" }; },
                () => { calls++; return new[] { "pistol" }; });

            Assert.AreEqual(0, calls, "Pushing the sources must not build anything.");

            ContentInternTable units = ContentInternContext.Units;

            Assert.AreEqual(1, calls, "Exactly the unit table should have been built.");
            Assert.AreEqual(2, units.Count);

            // The weapon table is still unbuilt — the two are independent on purpose.
            Assert.AreEqual(1, calls);
            Assert.AreEqual(1, ContentInternContext.Weapons.Count);
            Assert.AreEqual(2, calls);
        }

        /// <summary>Once built, the table is cached — this getter sits in a per-tick projection path.</summary>
        [Test]
        public void Access_IsCachedAfterTheFirstBuild()
        {
            int calls = 0;
            ContentInternContext.SetSources(
                () => { calls++; return new[] { "zombie" }; },
                () => { calls++; return new[] { "pistol" }; });

            for (int i = 0; i < 100; i++)
            {
                ContentInternTable unused = ContentInternContext.Units;
            }

            Assert.AreEqual(1, calls, "A hundred accesses must build once, not a hundred times.");
        }

        /// <summary>
        /// A content reload must be able to rebuild. Without this, a mod hot-load would leave the table
        /// describing the previous content set and the new definitions would all project as 0.
        /// </summary>
        [Test]
        public void Invalidate_RebuildsFromTheSource()
        {
            string[] weapons = { "pistol" };

            ContentInternContext.SetSources(
                () => new[] { "zombie" },
                () => weapons);

            Assert.AreEqual(ContentInternTable.Uninterned, ContentInternContext.Weapons.GetKeyOrDefault("plasma"));

            weapons = new[] { "pistol", "plasma" };
            ContentInternContext.Invalidate();

            Assert.AreNotEqual(ContentInternTable.Uninterned, ContentInternContext.Weapons.GetKeyOrDefault("plasma"),
                "After a reload the new definition must be internable.");
        }

        /// <summary>
        /// The fixture/tooling path: ready-made tables, no source, no content registry. It must also
        /// cancel a previously pushed source, or a test that configures explicit tables would silently
        /// get the content registry's instead.
        /// </summary>
        [Test]
        public void Configure_OverridesAnyPushedSource()
        {
            int calls = 0;
            ContentInternContext.SetSources(
                () => { calls++; return new[] { "from-source" }; },
                () => { calls++; return new[] { "from-source" }; });

            ContentInternContext.Configure(
                ContentInternTable.Build(new[] { "explicit-unit" }),
                ContentInternTable.Build(new[] { "explicit-weapon" }));

            Assert.AreEqual(0, calls, "The source must not be consulted after Configure.");
            Assert.AreNotEqual(ContentInternTable.Uninterned, ContentInternContext.Units.GetKeyOrDefault("explicit-unit"));
            Assert.AreEqual(ContentInternTable.Uninterned, ContentInternContext.Units.GetKeyOrDefault("from-source"));
        }

        /// <summary>
        /// Defaults are usable, not null: a containerless build must answer rather than throw inside a
        /// spawn path. This is the state every offline fixture and every editor tool runs in.
        /// </summary>
        [Test]
        public void Defaults_AnswerUninternedRatherThanThrowing()
        {
            Assert.IsFalse(ContentInternContext.IsConfigured);
            Assert.AreEqual(0, ContentInternContext.Units.Count);
            Assert.AreEqual(0, ContentInternContext.Weapons.Count);
            Assert.AreEqual(ContentInternTable.Uninterned, ContentInternContext.Weapons.GetKeyOrDefault("pistol"));

            // Null sources degrade to empty rather than throwing at the first spawn.
            ContentInternContext.SetSources(null, null);
            Assert.AreEqual(0, ContentInternContext.Weapons.Count);
        }

        /// <summary>
        /// A source that yields nothing is not a crash. The visible symptom is
        /// <c>UninternedDefinitionCount &gt; 0</c>, which is why that counter exists.
        /// </summary>
        [Test]
        public void AnEmptySource_BuildsAnEmptyTable()
        {
            ContentInternContext.SetSources(() => Array.Empty<string>(), () => Array.Empty<string>());

            Assert.AreEqual(0, ContentInternContext.Units.Count);
            Assert.IsFalse(ContentInternContext.IsConfigured);
        }
    }
}
