using NUnit.Framework;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// D0 of <c>docs/DeviceInteractionAndLoot/04_IMPLEMENTATION_PLAN.md</c>: the single rule that decides
    /// whether a prop is a container and which table it names.
    ///
    /// <para><b>Why the order is load-bearing.</b> <see cref="ContainerRule.ResolveCont"/> must be
    /// placement-first, then definition — AS3 copies the definition's <c>cont</c> first
    /// (<c>Interact.as:210-213</c>) and then lets the placed node override it (<c>:332-335</c>). Get it
    /// backwards and every room override is silently ignored, which is exactly the shape of the bug this
    /// fixture guards: a room writes <c>cont="chest"</c> on a <c>case</c> and the definition's
    /// <c>cont="case"</c> would win instead.</para>
    ///
    /// <para>Pure — no <c>ScriptableObject</c>, no scene — so it runs in a plain shell as well as in the
    /// editor.</para>
    /// </summary>
    [TestFixture]
    public class ContainerRuleTests
    {
        // =====================================================================
        //  ResolveCont — the precedence
        // =====================================================================

        /// <summary>
        /// <b>The placement wins over the definition.</b> <c>AllData.as</c> gives <c>case</c> the default
        /// <c>cont='case'</c>; four room placements override it with <c>cont="chest"</c>. Reading the
        /// definition last would hand every one of them the wrong table.
        /// </summary>
        [Test]
        public void ResolveCont_PlacementWins_OverDefinition()
        {
            string resolved = ContainerRule.ResolveCont(placementCont: "chest", definitionCont: "case");

            Assert.AreEqual("chest", resolved,
                "the placed node is applied after the definition row (Interact.as:210-213 then :332-335)");
        }

        /// <summary>
        /// <b>A present-but-empty placement falls through to the definition.</b> Both AS3 reads are
        /// guarded by <c>.length()</c> (<c>:210</c>, <c>:332</c>), so an authored <c>cont=""</c> is a
        /// no-op. 27 room placements write exactly that — <c>medbox</c> among them, whose definition
        /// carries <c>cont='med'</c>.
        /// </summary>
        [Test]
        public void ResolveCont_EmptyPlacement_FallsThroughToDefinition()
        {
            string resolved = ContainerRule.ResolveCont(placementCont: string.Empty, definitionCont: "med");

            Assert.AreEqual("med", resolved,
                "cont=\"\" is length 0 and does NOT override the definition's cont='med'");
        }

        /// <summary>
        /// <b>A null placement falls through too</b> — the "attribute absent" case, which must behave the
        /// same as the empty one. A definition with no <c>cont</c> and no placement override resolves to
        /// empty, not to a sentinel.
        /// </summary>
        [Test]
        public void ResolveCont_AbsentBoth_ResolvesEmpty()
        {
            Assert.AreEqual(string.Empty, ContainerRule.ResolveCont(null, null));
            Assert.AreEqual(string.Empty, ContainerRule.ResolveCont(string.Empty, string.Empty));
        }

        /// <summary>
        /// <b><c>"*"</c> is returned verbatim and is not treated as empty.</b> Seven room placements author
        /// it. It is a real value the caller must be able to see and report (Q1) — mapping it to
        /// <c>""</c> here would hide the very key the port is supposed to surface.
        /// </summary>
        [Test]
        public void ResolveCont_Wildcard_IsReturnedVerbatim()
        {
            string resolved = ContainerRule.ResolveCont(placementCont: "*", definitionCont: "case");

            Assert.AreEqual("*", resolved, "the wildcard is a value, not an absence");
            Assert.AreNotEqual(string.Empty, resolved);
        }

        // =====================================================================
        //  The classification predicates
        // =====================================================================

        /// <summary>
        /// <b>A definition with no <c>cont</c> is not a container.</b> This is the exact shape of the
        /// <c>mcrate1</c>…<c>mcrate5</c> misclassification: those rows carry no <c>cont</c> and no
        /// <c>inter</c> (<c>AllData.as:4879</c>), so they are pushable crates.
        /// </summary>
        [Test]
        public void HasLootTable_NoCont_IsFalse()
        {
            Assert.IsFalse(ContainerRule.HasLootTable(ContainerRule.ResolveCont(null, null)));
            Assert.IsFalse(ContainerRule.HasLootTable(string.Empty));
        }

        /// <summary>
        /// <b>A real key is a table.</b> Positive control for the test above: without it, "everything is
        /// false" would pass trivially.
        /// </summary>
        [Test]
        public void HasLootTable_RealKey_IsTrue()
        {
            Assert.IsTrue(ContainerRule.HasLootTable("chest"));
            Assert.IsTrue(ContainerRule.HasLootTable("med"));
            Assert.IsTrue(ContainerRule.HasLootTable("raider"));
        }

        /// <summary>
        /// <b>The looted sentinel is not a table.</b> <c>setAct("loot", n)</c> writes
        /// <c>cont = "empty"</c> (<c>Interact.as:840</c>) and <c>loot()</c> returns early on it
        /// (<c>:1965-1968</c>), so a looted prop must never be rolled again.
        /// </summary>
        [Test]
        public void HasLootTable_LootedSentinel_IsFalse()
        {
            Assert.IsFalse(ContainerRule.HasLootTable(ContainerRule.EmptyCont));
            Assert.IsTrue(ContainerRule.IsLooted(ContainerRule.EmptyCont));
        }

        /// <summary>
        /// <b>The wildcard is not a table but <i>is</i> still a container.</b> AS3 lets the player open a
        /// <c>*</c> prop and answers "itsEmpty" (<c>Interact.as:2020-2023</c>) — so it must stay
        /// interactable, while <see cref="ContainerRule.HasLootTable"/> must say "no table" so nothing
        /// pretends to roll. The two predicates differ on exactly this one value.
        /// </summary>
        [Test]
        public void Wildcard_IsAContainerButNotATable()
        {
            Assert.IsTrue(ContainerRule.IsContainer(ContainerRule.WildcardCont),
                "AS3 opens it and reports 'itsEmpty' — it is a container");
            Assert.IsFalse(ContainerRule.HasLootTable(ContainerRule.WildcardCont),
                "but no lootCont branch matches it, so it has no table (Q1)");
            Assert.IsTrue(ContainerRule.IsWildcard(ContainerRule.WildcardCont));
        }

        /// <summary>
        /// <b>The empty string is not a container at all</b> — the prop would never have got an
        /// <c>Interact</c> in AS3 either, because the value that admitted it is absent.
        /// </summary>
        [Test]
        public void EmptyString_IsNotAContainer()
        {
            Assert.IsFalse(ContainerRule.IsContainer(string.Empty));
            Assert.IsFalse(ContainerRule.IsContainer(null));
        }
    }
}
