using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;

namespace PFE.Tests.Editor.Entities.Enemies
{
    /// <summary>
    /// The chain that makes a satchel charge work — <c>rooms_begin/room_1_4</c> places three
    /// (<c>expl1</c> → <c>damexpl1</c>) and a <c>detonator</c> whose inline script is
    /// <c>&lt;scr targ="trExpl1" act="dam"/&gt;</c>.
    ///
    /// <para><b>Five links, and the report was four of them broken at once.</b></para>
    /// <list type="number">
    /// <item><description>the <c>&lt;un tip&gt;</c> attribute decides shooter / thrower / charge, and
    /// nothing imported it — every damager was the same weaponless unit;</description></item>
    /// <item><description><c>command("dam")</c> is the only thing that wakes one, and
    /// <c>UnitController.Command</c> had no such verb;</description></item>
    /// <item><description><c>activate()</c>'s tip-3 arm is an explosion whose three magnitudes are
    /// literals in the oracle, not data;</description></item>
    /// <item><description>the visual is <c>vis</c> + id, the one visual rule in the game that is not
    /// carried by data, so the importer resolved none of the ten units that use it;</description></item>
    /// <item><description>and a damager must start dormant — the port armed it at spawn, so every charge
    /// and every hidden shooter fired at the player from the moment the room loaded.</description></item>
    /// </list>
    ///
    /// <para>Links 1, 2 and 5 are invisible to any play-test that does not already know what a charge
    /// looks like, which is exactly why they are pinned here rather than described in a comment.</para>
    /// </summary>
    [TestFixture]
    public sealed class DamagerTrapRulesTests
    {
        // ── 1. The tip attribute ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>UnitDamager.as:18</c> — <c>internal var tipDamager:int = 1</c>. A row that authors no
        /// <c>&lt;un tip&gt;</c> is a SHOOTER, because <c>setWeapon()</c> builds a weapon for
        /// <c>tipDamager == 1</c> (<c>:150-158</c>). Zero would fall through every arm in
        /// <c>setWeapon</c> and leave a unit with no weapon and no blast.
        /// </summary>
        [Test]
        public void AnAbsentTipLeavesTheOracleDefaultOfAShooter()
        {
            Assert.AreEqual(1, DamagerTrapRules.DefaultTip);
            Assert.AreEqual(DamagerTrapRules.TipShooter, DamagerTrapRules.DefaultTip);
        }

        /// <summary>
        /// The three authored rows — <c>damshot</c> 1, <c>damgren</c> 2, <c>damexpl1</c> 3
        /// (<c>AllData.as</c>). They are three different machines and the value is the dispatch.
        /// </summary>
        [Test]
        public void TheThreeAuthoredTipsAreDistinctAndOnlyThreeIsABomb()
        {
            Assert.IsTrue(DamagerTrapRules.IsBomb(3));
            Assert.IsFalse(DamagerTrapRules.IsBomb(1));
            Assert.IsFalse(DamagerTrapRules.IsBomb(2));

            // Control: the tip values are the AS3 ones, not renumbered.
            Assert.AreEqual(1, DamagerTrapRules.TipShooter);
            Assert.AreEqual(2, DamagerTrapRules.TipThrower);
            Assert.AreEqual(3, DamagerTrapRules.TipBomb);
        }

        [Test]
        public void ParseDamagerTipReadsTheAuthorsRowAndLeavesAnAbsentOneAlone()
        {
            // Verbatim from AllData.as — the three rows that author it.
            Assert.AreEqual(3, UnitExtrasParser.ParseDamagerTip("<unit id='damexpl1'>\n<un tip='3' skill='explosives'/>\n</unit>"));
            Assert.AreEqual(2, UnitExtrasParser.ParseDamagerTip("<unit id='damgren'>\n<un tip='2' skill='explosives'/>\n</unit>"));
            Assert.AreEqual(1, UnitExtrasParser.ParseDamagerTip("<unit id='damshot'>\n<un tip='1' skill='firearms'/>\n</unit>"));

            // Absent → null, so the importer leaves the field at its declaration default.
            Assert.IsNull(UnitExtrasParser.ParseDamagerTip("<unit id='rat'>\n<un walker='1'/>\n</unit>"));

            // Anchor control: `chootch` contains `tip`? No — but `attch`/`chootch` must not be read as
            // `tip`, and an anchored regex is the only thing that keeps that true.
            Assert.IsNull(UnitExtrasParser.ParseDamagerTip("<unit id='merc3'>\n<un chootch='2'/>\n</unit>"));
        }

        // ── 2. The activation gate ────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>UnitDamager.control()</c> attacks only inside <c>if(this.status == 1)</c> (<c>:390</c>), and
        /// <c>activate()</c> is the only writer of 1. A damager that no script has woken does NOTHING —
        /// which is why the port arming it at spawn was a live defect and not a cosmetic one.
        /// </summary>
        [Test]
        public void ADormantDamagerDoesNotAttackAndAWokenOneDoes()
        {
            Assert.IsFalse(DamagerTrapRules.Attacks(0));
            Assert.IsTrue(DamagerTrapRules.Attacks(1));

            Assert.IsTrue(DamagerTrapRules.CanActivate(0));
            Assert.IsFalse(DamagerTrapRules.CanActivate(1), "a woken damager must not re-enter activate()");
        }

        // ── 3. The blast ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>UnitDamager.as:152-154</c> — <c>setWeapon()</c>'s <c>tipDamager == 3</c> arm. These are
        /// literals in the oracle; a "tidy" refactor into a definition field would silently stop
        /// matching the game, so the numbers are asserted rather than trusted.
        /// </summary>
        [Test]
        public void TheChargeCarriesTheOraclesOwnThreeMagnitudes()
        {
            Assert.AreEqual(250f, DamagerTrapRules.BombDamage);
            Assert.AreEqual(1000f, DamagerTrapRules.BombDestroyTiles);
            Assert.AreEqual(200f, DamagerTrapRules.BombRadiusPixels);
        }

        /// <summary>
        /// <c>UnitDamager.as:349</c> — <c>new Bullet(this, X, Y - 20, null, false)</c>, the arm taken
        /// because a tip-3 damager never builds a <c>currentWeapon</c>.
        ///
        /// <para><b>AS3's Y runs down; the port's runs up.</b> Twenty pixels is twenty pixels, but the
        /// sign is a mirror, not a copy — the offset is applied as <c>+Y</c> in world space. A test that
        /// only checked "20" would pass with the blast under the floor.</para>
        /// </summary>
        [Test]
        public void TheBlastIsAnchoredTwentyPixelsAboveTheChargeNotBelowIt()
        {
            Assert.AreEqual(20f, DamagerTrapRules.BombCentreOffsetPixels);
            Assert.Greater(DamagerTrapRules.BombCentreOffsetPixels, 0f,
                           "AS3 Y-20 is UP; in a y-up port that is a positive offset");
        }

        /// <summary>
        /// <b>The trap this factory exists to avoid.</b> <c>DamageContext.Contact</c> leaves
        /// <c>ExplosionDamage</c> at 0, and the explosion path reads that field rather than
        /// <c>BaseDamage</c> — so a blast built from <c>Contact</c> deals nothing and reports no error.
        /// </summary>
        [Test]
        public void ABlastContextCarriesItsMagnitudeWhereTheExplosionPathReadsIt()
        {
            var blast = DamageContext.Blast(250f, DamageType.Explosive);

            Assert.AreEqual(250f, blast.ExplosionDamage);
            Assert.AreEqual(250f, blast.BaseDamage,
                            "the two are written from one argument so they cannot disagree");
            Assert.AreEqual(DamageType.Explosive, blast.DamageType);

            // Control: the contact factory is the one that would have produced a silent zero.
            var contact = DamageContext.Contact(250f, DamageType.Explosive);
            Assert.AreEqual(0f, contact.ExplosionDamage);
            Assert.AreEqual(250f, contact.BaseDamage);
        }

        // ── 4. The visual rule ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>UnitTrap.as:22</c>, <c>UnitTrigger.as:56</c>, <c>UnitDamager.as:54</c> —
        /// <c>Res.getVis("vis" + id, vismtrap)</c>. The importer knew only <c>vclass</c> and <c>blit</c>,
        /// so <c>damexpl1</c> imported with no sprite at all: three invisible charges in
        /// <c>room_1_4</c>.
        /// </summary>
        [Test]
        public void TheDerivedVisualNameIsVisPlusTheId()
        {
            Assert.AreEqual("visdamexpl1", TrapVisualNaming.CandidateFor("damexpl1"));
            Assert.AreEqual("vismtrap", TrapVisualNaming.CandidateFor("mtrap"));
            Assert.IsNull(TrapVisualNaming.CandidateFor(null));
            Assert.IsNull(TrapVisualNaming.CandidateFor(string.Empty));
        }

        /// <summary>
        /// The <c>vismtrap</c> fallback belongs to the three classes that declare it. <c>encl</c> is the
        /// one other id with a <c>vis&lt;id&gt;</c> symbol and must not borrow a bear trap's picture.
        /// </summary>
        [Test]
        public void OnlyTheTrapFamilyMayFallBackToTheSharedTrapSymbol()
        {
            Assert.IsTrue(TrapVisualNaming.UsesSharedTrapFallback("damexpl1"));
            Assert.IsTrue(TrapVisualNaming.UsesSharedTrapFallback("damgren"));
            Assert.IsTrue(TrapVisualNaming.UsesSharedTrapFallback("trigcans"));
            Assert.IsTrue(TrapVisualNaming.UsesSharedTrapFallback("mtrap"));

            Assert.IsFalse(TrapVisualNaming.UsesSharedTrapFallback("encl"));
            Assert.IsFalse(TrapVisualNaming.UsesSharedTrapFallback("raider1"));
        }
    }
}
