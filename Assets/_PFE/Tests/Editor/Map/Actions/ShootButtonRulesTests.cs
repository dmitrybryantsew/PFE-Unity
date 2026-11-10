using NUnit.Framework;
using PFE.Systems.Map.Actions;

namespace PFE.Tests.Editor.Map.Actions
{
    /// <summary>
    /// Tests for the oracle-literal rules behind <c>knop2</c> — the button that is pressed by shooting
    /// it — <see cref="ShootButtonRules"/>.
    ///
    /// <para><b>Why these are fixtures and not comments on the presenter.</b> Every decision here is a
    /// comparison the oracle makes on one authored attribute, and three of the four failure modes are
    /// invisible in a play-test:</para>
    /// <list type="bullet">
    /// <item>reading <c>knop</c> from the placement only — 41 of 41 shipped <c>knop2</c> placements
    /// author none, so every button would take the wrong <c>dam</c> arm and the wrong behaviour would
    /// look like "the button does nothing";</item>
    /// <item>treating <c>actdam="0"</c> as "no proxy" — the oracle tests the attribute's length, so the
    /// value is irrelevant, and a value test is an invention that happens to agree with today's one
    /// shipped row;</item>
    /// <item>letting a <c>tipdam</c> mismatch pass the round through — in AS3 the round still stops on
    /// the button; it just does not press it.</item>
    /// </list>
    ///
    /// <para><b>The shipped data, for orientation.</b> <c>knop2</c> is
    /// <c>actdam='1' inter='4' knop='1'</c> (<c>AllData.as</c>); 41 placements in 12 rooms. Six of them
    /// — all six in <c>buttons4</c> — override <c>inter="0"</c>, one per damage type
    /// (<c>tipdam</c> 3, 5, 6, 9, 10, 11). Seven <c>buttons3</c> placements filter to
    /// <c>tipdam="4"</c> (explosive) without overriding <c>inter</c>, and 27 author neither an
    /// <c>allact</c> nor a <c>&lt;scr&gt;</c> — the group that needs the admission clause. The two
    /// scripted ones are the tutorial (<c>rooms_begin/room_2_1</c>) and <c>workshop/room_1_0</c>.</para>
    /// </summary>
    [TestFixture]
    public sealed class ShootButtonRulesTests
    {
        // ── actdam: presence, not value (Box.as:410) ───────────────────────────────────────────────

        [Test]
        public void ADefinitionAuthorsingActDamBindsAProxy()
        {
            Assert.IsTrue(ShootButtonRules.HasBulletDamageProxy("1"),
                "knop2 authors actdam='1'; without a proxy the button has no hit surface at all.");
        }

        [Test]
        public void ActDamIsTestedForPresenceNotForTruth()
        {
            // Box.as:410 is `if(node.@actdam.length())`. `actdam="0"` is a one-node XMLList whose text
            // is "0", which is truthy — so the proxy is bound. A value test would answer false here and
            // would be an invention the shipped data cannot catch, because knop2 is the only row.
            Assert.IsTrue(ShootButtonRules.HasBulletDamageProxy("0"));
            Assert.IsTrue(ShootButtonRules.HasBulletDamageProxy("false"));
        }

        [Test]
        public void AnAbsentOrEmptyActDamBindsNothing()
        {
            // An empty attribute has length() == 0, so the oracle binds nothing — and the import writes
            // "" for a row that authors none.
            Assert.IsFalse(ShootButtonRules.HasBulletDamageProxy(string.Empty));
            Assert.IsFalse(ShootButtonRules.HasBulletDamageProxy(null));
        }

        // ── tipdam: the placement's filter (Box.as:412-418 → VirtualUnit.as:24-27) ─────────────────

        [Test]
        public void AnAbsentTipDamMeansAcceptEveryDamageType()
        {
            // `bindUnit()`'s default parameter is "-1" (Box.as:1227), so a placement that authors no
            // tipdam accepts everything. The tutorial button is this shape.
            Assert.AreEqual(ShootButtonRules.NoTipDamageFilter, ShootButtonRules.ResolveTipDamage(null));
            Assert.AreEqual(ShootButtonRules.NoTipDamageFilter, ShootButtonRules.ResolveTipDamage(string.Empty));
        }

        [Test]
        public void AnAuthoredTipDamIsTheIntegerThePlacementWrote()
        {
            // The values the shipped data actually authors: 4 on seven buttons3 placements, and
            // 3/5/6/9/10/11 on the six buttons4 ones (one per damage type). DamageType's numbering is
            // AS3's Unit.D_* numbering, so no translation is involved.
            Assert.AreEqual(4, ShootButtonRules.ResolveTipDamage("4"));
            Assert.AreEqual(5, ShootButtonRules.ResolveTipDamage("5"));
            Assert.AreEqual(11, ShootButtonRules.ResolveTipDamage("11"));
            Assert.AreEqual(0, ShootButtonRules.ResolveTipDamage("0"));
        }

        [Test]
        public void AnUnparseableTipDamBecomesZeroRatherThanUnfiltered()
        {
            // AS3's int("abc") is 0, which is D_BUL. Returning -1 instead would make such a button
            // accept everything — strictly more permissive than the oracle, and silent.
            Assert.AreEqual(0, ShootButtonRules.ResolveTipDamage("abc"));
            Assert.AreEqual(0, ShootButtonRules.ResolveTipDamage("5x"));
        }

        [Test]
        public void NoFilterAcceptsEveryTypeAndAFilterAcceptsOnlyItsOwn()
        {
            int any = ShootButtonRules.NoTipDamageFilter;

            Assert.IsTrue(ShootButtonRules.AcceptsDamageType(any, (int)DamageTypeProbe.Bullet));
            Assert.IsTrue(ShootButtonRules.AcceptsDamageType(any, (int)DamageTypeProbe.Explosive));
            Assert.IsTrue(ShootButtonRules.AcceptsDamageType(any, (int)DamageTypeProbe.Cryo));

            Assert.IsTrue(ShootButtonRules.AcceptsDamageType(5, 5));
            Assert.IsFalse(ShootButtonRules.AcceptsDamageType(5, 3));
            Assert.IsTrue(ShootButtonRules.AcceptsDamageType(0, 0));
            Assert.IsFalse(ShootButtonRules.AcceptsDamageType(0, 1));
        }

        /// <summary>
        /// The three AS3 <c>Unit.D_*</c> values used above, named locally so this fixture does not depend
        /// on the Unity-side enum's assembly. <c>DataEnums.cs:83</c> mirrors <c>Unit.as:64-77</c> exactly
        /// (<c>D_BUL=0</c>, <c>D_EXPL=4</c>, <c>D_LASER=5</c>, <c>D_CRIO=11</c>), which is what makes the
        /// authored integer comparable directly.
        /// </summary>
        private enum DamageTypeProbe
        {
            Bullet = 0,
            Explosive = 4,
            Laser = 5,
            Cryo = 11,
        }

        // ── knop: definition first, placement overriding (Interact.as:304-306, :403-405) ──────────

        [Test]
        public void KnobFallsBackToTheDefinition()
        {
            // The case that mattered: knop2 authors knop='1' on the definition and NO shipped placement
            // repeats it. Reading the placement only made every bullet-button a non-knob.
            Assert.IsTrue(ShootButtonRules.IsKnob(string.Empty, "1"));
        }

        [Test]
        public void APlacementKnobOverridesAnAbsentDefinition()
        {
            // knop1/knop3 author it on the definition too, but Interact.as:403-405 lets the placement
            // win when it authors one — the same shape as `inter`.
            Assert.IsTrue(ShootButtonRules.IsKnob("1", string.Empty));
            Assert.IsTrue(ShootButtonRules.IsKnob("1", "0"));
        }

        [Test]
        public void KnobIsPresenceNotAPositiveNumber()
        {
            // `if(this.knop)` on an untyped field assigned an XMLList: a one-node list whose text is "0"
            // is truthy. No shipped placement authors knop="0", so this pins the reading rather than a
            // current data shape.
            Assert.IsTrue(ShootButtonRules.IsKnob("0", string.Empty));
        }

        [Test]
        public void NeitherNodeAuthoringKnobIsNotAKnob()
        {
            Assert.IsFalse(ShootButtonRules.IsKnob(string.Empty, string.Empty));
            Assert.IsFalse(ShootButtonRules.IsKnob(null, null));
        }

        // ── inter: placement overriding definition (Interact.as:292, :397-400) ────────────────────

        [Test]
        public void InteractionModePrefersThePlacement()
        {
            // buttons4's two knop2 placements author inter="0" over a definition that says inter='4'.
            // This override IS the buttons4 mechanic: those two take the one-shot arm instead of the
            // toggle.
            Assert.AreEqual(0, ShootButtonRules.ResolveInteractionMode("0", "4"));
            Assert.AreEqual(2, ShootButtonRules.ResolveInteractionMode("2", "4"));
        }

        [Test]
        public void InteractionModeFallsBackToTheDefinition()
        {
            Assert.AreEqual(4, ShootButtonRules.ResolveInteractionMode(string.Empty, "4"));
            Assert.AreEqual(4, ShootButtonRules.ResolveInteractionMode(null, "4"));
        }

        [Test]
        public void AnUnparseablePlacementFallsBackRatherThanBecomingZero()
        {
            // AS3 assigns the attribute and parses at use sites; a non-numeric placement value therefore
            // leaves the definition's number in place rather than silently disabling the object.
            Assert.AreEqual(4, ShootButtonRules.ResolveInteractionMode("x", "4"));
        }

        [Test]
        public void NoAuthoredModeIsZero()
        {
            Assert.AreEqual(0, ShootButtonRules.ResolveInteractionMode(string.Empty, string.Empty));
        }

        // ── the three `dam` arms (Interact.as:1744-1758) ──────────────────────────────────────────

        [Test]
        public void AKnobWithActionZeroPressesOnce()
        {
            Assert.IsTrue(ShootButtonRules.PressesOnce(true, 0));
            Assert.IsFalse(ShootButtonRules.PressesOnce(true, 4));
            Assert.IsFalse(ShootButtonRules.PressesOnce(false, 0));
        }

        [Test]
        public void TheOwnStateTogglesOnlyWithAStateAndAPositiveAction()
        {
            // `(door > 0 || knop > 0) && action > 0` (Interact.as:1294).
            Assert.IsTrue(ShootButtonRules.TogglesOwnState(knob: true, hasDoor: false, interactionMode: 4));
            Assert.IsTrue(ShootButtonRules.TogglesOwnState(knob: false, hasDoor: true, interactionMode: 1));
            Assert.IsFalse(ShootButtonRules.TogglesOwnState(knob: true, hasDoor: false, interactionMode: 0));
            Assert.IsFalse(ShootButtonRules.TogglesOwnState(knob: false, hasDoor: false, interactionMode: 4));
        }

        [Test]
        public void AnExplosiveKnobDetonatesBeforeAnyPressArm()
        {
            Assert.AreEqual(
                DamageVerbOutcome.Explode,
                ShootButtonRules.ResolveDamageVerb(explosive: true, knob: true, interactionMode: 0));
        }

        [Test]
        public void AOneShotKnobOpensRatherThanToggling()
        {
            Assert.AreEqual(
                DamageVerbOutcome.OpenOnce,
                ShootButtonRules.ResolveDamageVerb(explosive: false, knob: true, interactionMode: 0));
        }

        [Test]
        public void EverythingElseRunsActOsn()
        {
            // knop2's shipped shape — knop, inter=4 — and a plain door both fall through to actOsn.
            Assert.AreEqual(
                DamageVerbOutcome.Activate,
                ShootButtonRules.ResolveDamageVerb(explosive: false, knob: true, interactionMode: 4));
            Assert.AreEqual(
                DamageVerbOutcome.Activate,
                ShootButtonRules.ResolveDamageVerb(explosive: false, knob: false, interactionMode: 1));
        }

        // ── allAct: either source runs it (Interact.as:1315) ─────────────────────────────────────

        [Test]
        public void EitherAnAllActOrAProbRunsTheBroadcast()
        {
            // `if(Boolean(this.allact) || this.prob != null) this.allAct();` — the prob test is first
            // inside allAct itself, which is why an object with a prob and an allact takes the prob
            // branch. This predicate only decides whether the call happens.
            Assert.IsTrue(ShootButtonRules.ShouldRunAllAct("open", string.Empty));
            Assert.IsTrue(ShootButtonRules.ShouldRunAllAct(string.Empty, "p1"));
            Assert.IsFalse(ShootButtonRules.ShouldRunAllAct(string.Empty, string.Empty));
            Assert.IsFalse(ShootButtonRules.ShouldRunAllAct(null, null));
        }
    }
}
