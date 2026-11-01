using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins <see cref="DesintegrRules"/> against <c>fe/serv/Desintegr.as</c> and the three call sites in
    /// <c>Unit.as</c> that feed it.
    ///
    /// <para><b>Why this class is executable and the effect is not.</b> Everything the dissolve decides —
    /// the tip table, the colour ramp, the erosion curve, the glow ramp, the particle gate, the burst
    /// count — is a number, and those numbers live in a type that references no engine object. The
    /// drawing needs a <c>SpriteRenderer</c>, a material and a room, which outside the editor throws
    /// <c>ECall</c> before an assertion is reached. So the arithmetic is proven here and the wiring is
    /// pinned by a source lint in <c>CorpseDamageWiringLintTests</c>.</para>
    ///
    /// <para><b>Every assertion names the oracle line it comes from.</b> These values were transcribed
    /// once, and a transcription is exactly the kind of thing that is wrong in a way that still passes a
    /// structural review — the boss work in this project found invented numbers that had survived one.
    /// A fixture that quotes the line is a fixture a reader can check.</para>
    /// </summary>
    [TestFixture]
    public class DesintegrRulesTests
    {
        // ── The tip table: `die()`'s branch that decides what the body becomes ──────────────────

        [Test]
        public void TipForOverkill_IsFourPlusBlood()
        {
            // Unit.as:4347-4353 — `initBurn(4 + this.blood)`.
            Assert.AreEqual(DesintegrTip.BloodRed,   DesintegrRules.TipForOverkill(BloodType.Red));
            Assert.AreEqual(DesintegrTip.BloodGreen, DesintegrRules.TipForOverkill(BloodType.Green));
            Assert.AreEqual(DesintegrTip.BloodPink,  DesintegrRules.TipForOverkill(BloodType.Pink));

            // The values themselves, not only the round trip: `4 + 1` is tip 5, and a table shifted by
            // one would still map three distinct blood types onto three distinct tips.
            Assert.AreEqual(5, (int)DesintegrRules.TipForOverkill(BloodType.Red));
            Assert.AreEqual(6, (int)DesintegrRules.TipForOverkill(BloodType.Green));
            Assert.AreEqual(7, (int)DesintegrRules.TipForOverkill(BloodType.Pink));
        }

        [Test]
        public void TipForOverkill_ZeroBloodIsNone_NotTipFour()
        {
            // The trap. `4 + 0` is 4, and 4 is the CRYO preset — so a naive `4 + blood` would freeze a
            // bloodless robot's corpse instead of removing it. AS3 never reaches that arithmetic with
            // blood == 0 because `die()`'s arm is guarded by `this.blood > 0` (:4347), so the rule has to
            // answer "no dissolve" itself.
            Assert.AreEqual(DesintegrTip.None, DesintegrRules.TipForOverkill(BloodType.None));
            Assert.IsFalse(DesintegrRules.IsDissolve(DesintegrRules.TipForOverkill(BloodType.None)));
        }

        // ── The damage-type table: `Unit.damage()`'s `_loc10_` (Unit.as:3713-3741) ──────────────
        //
        // A tip from here outranks both `trup` arms of `die()`, so these are the cases where a body
        // burns instead of becoming a corpse — or, for `trup='0'`, instead of bursting.

        [Test]
        public void TipForDamageType_OnlyFiveCodesProduceABurn()
        {
            // The oracle writes five `if`s and leaves `_loc10_` at 0 otherwise (:3670 resets it). Every
            // other code in the enum is an ordinary death, so this list is a *complete* partition: a
            // reader can see that fire, laser, plasma, acid, astro, friendly fire and cryo are the only
            // codes that ever reach `initBurn` through this arm.
            DamageType[] quiet =
            {
                DamageType.PhysicalBullet, DamageType.Blade,      DamageType.PhysicalMelee,
                DamageType.Explosive,      DamageType.Venom,      DamageType.EMP,
                DamageType.Spark,          DamageType.Poison,     DamageType.Bleed,
                DamageType.Fang,           DamageType.Balefire,   DamageType.Necrotic,
                DamageType.Psionic,        DamageType.Pink,       DamageType.Internal,
            };

            foreach (DamageType type in quiet)
            {
                // Deliberately absurd overkill and a bodyless unit: neither may manufacture a tip out of
                // a code the oracle's table does not name.
                Assert.AreEqual(DesintegrTip.None,
                    DesintegrRules.TipForDamageType(type, 1e6f, 10f, false, 0f),
                    type + " has no row in Unit.as:3725-3741");
            }
        }

        [Test]
        public void TipForDamageType_FireBurnsOnlyPastThreeTimesMaxHp()
        {
            // :3725 — `param2 == D_FIRE && (hp <= -maxhp*3 || !trup)`.
            const float maxHp = 40f;

            Assert.AreEqual(DesintegrTip.Burn,
                DesintegrRules.TipForDamageType(DamageType.Fire, 120f, maxHp, true, 0.99f),
                "exactly 3x the pool is `hp == -maxhp*3`, which is `<=` and therefore burns");

            Assert.AreEqual(DesintegrTip.None,
                DesintegrRules.TipForDamageType(DamageType.Fire, 119.9f, maxHp, true, 0.99f),
                "just short of 3x leaves a corpse — the roll is irrelevant for fire");

            // `!trup` — the other half of the same condition. A rat always burns.
            Assert.AreEqual(DesintegrTip.Burn,
                DesintegrRules.TipForDamageType(DamageType.Fire, 0f, maxHp, false, 0.99f));
        }

        [Test]
        public void TipForDamageType_LaserBurnsOnACoinFlip()
        {
            // :3729 — `param2 == D_LASER && (hp <= -maxhp*3 || !trup || this.isrnd())`.
            // `isrnd()` is `Math.random() < 0.5` (Unit.as:4855-4858), so HALF of all laser kills burn
            // however little overkill they carried. This is the single most surprising row in the table.
            const float maxHp = 40f;

            Assert.AreEqual(DesintegrTip.Burn,
                DesintegrRules.TipForDamageType(DamageType.Laser, 0f, maxHp, true, 0.49f),
                "0.49 < 0.5 — the flip wins with no overkill at all");

            Assert.AreEqual(DesintegrTip.None,
                DesintegrRules.TipForDamageType(DamageType.Laser, 0f, maxHp, true, 0.5f),
                "0.5 is NOT < 0.5, so the flip loses; an off-by-one here would make it 51%");

            Assert.AreEqual(DesintegrTip.Burn,
                DesintegrRules.TipForDamageType(DamageType.Laser, 120f, maxHp, true, 0.99f),
                "overkill still burns without the flip");
        }

        [Test]
        public void TipForDamageType_PlasmaAndAcidShareThePlasmaPreset()
        {
            // :3733 — `param2 == D_PLASMA || param2 == D_ACID`. Unconditional: no overkill test, no
            // `trup` test, no roll.
            Assert.AreEqual(DesintegrTip.Plasma,
                DesintegrRules.TipForDamageType(DamageType.Plasma, 0f, 40f, true, 0.99f));
            Assert.AreEqual(DesintegrTip.Plasma,
                DesintegrRules.TipForDamageType(DamageType.Acid, 0f, 40f, true, 0.99f));

            // Acid has no preset of its own and must not be given one.
            Assert.AreEqual(DesintegrRules.ParticleId(DesintegrTip.Plasma),
                DesintegrRules.ParticleId(
                    DesintegrRules.TipForDamageType(DamageType.Acid, 0f, 40f, true, 0.99f)));
        }

        [Test]
        public void TipForDamageType_AstralAndFriendlyFireShareTheAstroPreset()
        {
            // :3737 — `param2 == D_ASTRO || param2 == D_FRIEND`. Note the asymmetry in the enum's
            // values: astral is 18 and friendly fire is 101, so a range test would be wrong here.
            Assert.AreEqual(DesintegrTip.Astro,
                DesintegrRules.TipForDamageType(DamageType.Astral, 0f, 40f, true, 0.99f));
            Assert.AreEqual(DesintegrTip.Astro,
                DesintegrRules.TipForDamageType(DamageType.FriendlyFire, 0f, 40f, true, 0.99f));
        }

        [Test]
        public void TipForDamageType_CryoIsUnconditional()
        {
            // :3741 — `param2 == D_CRIO`, alone on its line with no condition at all. A cryo kill freezes
            // the body even if the unit leaves no corpse, which is why this arm also outranks `trup`.
            Assert.AreEqual(DesintegrTip.Cryo,
                DesintegrRules.TipForDamageType(DamageType.Cryo, 0f, 40f, true, 0.99f));
            Assert.AreEqual(DesintegrTip.Cryo,
                DesintegrRules.TipForDamageType(DamageType.Cryo, 0f, 40f, false, 0.99f));
        }

        [Test]
        public void TipForDamageType_ZeroMaxHpDoesNotDivideOrExplode()
        {
            // A stat-less unit (MaxHp 0) must not turn every fire hit into a burn by way of
            // `overkill >= 0`, and must not produce NaN. `0 * 3` is 0, so `0 >= 0` is true — which is
            // exactly AS3's `hp <= -maxhp*3` with maxhp 0, i.e. true at death. Pinned so the reading is
            // deliberate rather than accidental.
            Assert.AreEqual(DesintegrTip.Burn,
                DesintegrRules.TipForDamageType(DamageType.Fire, 0f, 0f, true, 0.99f));
            Assert.AreEqual(DesintegrTip.None,
                DesintegrRules.TipForDamageType(DamageType.PhysicalBullet, 0f, 0f, true, 0.99f));
        }

        [Test]
        public void TipForDamageType_ReturnsOnlyPresetsTheEffectCanRender()
        {
            // Every non-None answer must be a tip the rest of the rules can drive — a particle row, a
            // glow colour and a tint. This is what keeps the new arm from reaching a preset that
            // `ParticleId` does not know.
            foreach (DamageType type in new[]
                     {
                         DamageType.Fire, DamageType.Laser, DamageType.Plasma,
                         DamageType.Acid, DamageType.Astral, DamageType.FriendlyFire, DamageType.Cryo,
                     })
            {
                DesintegrTip tip = DesintegrRules.TipForDamageType(type, 200f, 10f, false, 0.1f);
                Assert.IsTrue(DesintegrRules.IsDissolve(tip), type + " must yield a renderable preset");
                Assert.IsNotNull(DesintegrRules.ParticleId(tip));
            }
        }

        [Test]
        public void IsDissolve_CoversOneToSeven_AndNothingElse()
        {
            for (int tip = 1; tip <= 7; tip++)
            {
                Assert.IsTrue(DesintegrRules.IsDissolve((DesintegrTip)tip),
                    "tip " + tip + " is one of the seven presets in Desintegr.as:62-107");
            }

            Assert.IsFalse(DesintegrRules.IsDissolve(DesintegrTip.None));
            Assert.IsFalse(DesintegrRules.IsDissolve((DesintegrTip)8),
                "the table stops at 7; an out-of-range value must not be treated as a preset");
        }

        // ── The particle rows ──────────────────────────────────────────────────────────────────

        [Test]
        public void ParticleId_MatchesTheConstructorBlock()
        {
            Assert.AreEqual("burn",   DesintegrRules.ParticleId(DesintegrTip.Burn));    // :65
            Assert.AreEqual("plakap", DesintegrRules.ParticleId(DesintegrTip.Plasma));  // :72
            Assert.AreEqual("krupa",  DesintegrRules.ParticleId(DesintegrTip.Cryo));    // :86
            Assert.AreEqual("blood",  DesintegrRules.ParticleId(DesintegrTip.BloodRed));   // :93
            Assert.AreEqual("gblood", DesintegrRules.ParticleId(DesintegrTip.BloodGreen)); // :99
            Assert.AreEqual("pblood", DesintegrRules.ParticleId(DesintegrTip.BloodPink));  // :105

            // Tip 3 shares `burn` with tip 1 — only the glow colour and the RGB ramp differ (:76-82).
            // A port that invented an `astro` row would be inventing art, so this is asserted rather
            // than left to the reader.
            Assert.AreEqual(DesintegrRules.ParticleId(DesintegrTip.Burn),
                            DesintegrRules.ParticleId(DesintegrTip.Astro));

            Assert.IsNull(DesintegrRules.ParticleId(DesintegrTip.None));
        }

        [Test]
        public void BloodParticleIds_AgreeWithTheBloodSprayRows()
        {
            // The three ids are spelled out twice — once here, once in `BloodSprayRules` — because that
            // type lives in PFE.View, which references PFE.Sim and not the other way round. This fixture
            // sees both assemblies, so it is the only place the duplication can be pinned.
            Assert.AreEqual(PFE.Systems.Particles.BloodSprayRules.RedSprayId,
                            DesintegrRules.ParticleId(DesintegrTip.BloodRed));
            Assert.AreEqual(PFE.Systems.Particles.BloodSprayRules.GreenSprayId,
                            DesintegrRules.ParticleId(DesintegrTip.BloodGreen));
            Assert.AreEqual(PFE.Systems.Particles.BloodSprayRules.PinkSprayId,
                            DesintegrRules.ParticleId(DesintegrTip.BloodPink));
        }

        [Test]
        public void SoundId_IsSilentForTheBloodTips()
        {
            Assert.AreEqual("desintegr_f", DesintegrRules.SoundId(DesintegrTip.Burn));
            Assert.AreEqual("liquid_f",    DesintegrRules.SoundId(DesintegrTip.Plasma));
            Assert.AreEqual("desintegr_f", DesintegrRules.SoundId(DesintegrTip.Astro));
            Assert.AreEqual("freezing_f",  DesintegrRules.SoundId(DesintegrTip.Cryo));

            // The three blood constructor blocks (:90-107) contain no `Snd.ps` call at all — a body that
            // was already shredded does not hiss. Filling these in with `desintegr_f` would add a sound
            // the game does not have on the most common dissolve in it.
            Assert.IsNull(DesintegrRules.SoundId(DesintegrTip.BloodRed));
            Assert.IsNull(DesintegrRules.SoundId(DesintegrTip.BloodGreen));
            Assert.IsNull(DesintegrRules.SoundId(DesintegrTip.BloodPink));
        }

        [Test]
        public void GlowColorRgb_IsTheOraclesLiteral()
        {
            // Desintegr.as:66, :73, :80, :87, :94, :100, :106 — the decimals as written, with the hex
            // beside them so a reader can see the colour without converting.
            Assert.AreEqual(16755200, DesintegrRules.GlowColorRgb(DesintegrTip.Burn));       // 0xFFAA00
            Assert.AreEqual(65280,    DesintegrRules.GlowColorRgb(DesintegrTip.Plasma));     // 0x00FF00
            Assert.AreEqual(4474111,  DesintegrRules.GlowColorRgb(DesintegrTip.Astro));      // 0x4444FF
            Assert.AreEqual(255,      DesintegrRules.GlowColorRgb(DesintegrTip.Cryo));       // 0x0000FF
            Assert.AreEqual(16711680, DesintegrRules.GlowColorRgb(DesintegrTip.BloodRed));   // 0xFF0000
            Assert.AreEqual(6736947,  DesintegrRules.GlowColorRgb(DesintegrTip.BloodGreen)); // 0x66CC33
            Assert.AreEqual(16738047, DesintegrRules.GlowColorRgb(DesintegrTip.BloodPink));  // 0xFF66FF
        }

        // ── The colour transform ───────────────────────────────────────────────────────────────

        [Test]
        public void TintFor_BloodTipsDrainTwoChannels()
        {
            // (1, 0.85, 0.85) — :92. Red survives, green and blue are cut.
            DesintegrTint red = DesintegrRules.TintFor(DesintegrTip.BloodRed);
            Assert.That(red.MulR, Is.EqualTo(1f).Within(1e-6));
            Assert.That(red.MulG, Is.EqualTo(0.85f).Within(1e-6));
            Assert.That(red.MulB, Is.EqualTo(0.85f).Within(1e-6));
            Assert.That(red.AddR + red.AddG + red.AddB, Is.EqualTo(0f).Within(1e-6),
                "the blood tips pass zero offsets; a non-zero one would brighten instead of draining");

            // (0.9, 1, 0.85) — :98.
            DesintegrTint green = DesintegrRules.TintFor(DesintegrTip.BloodGreen);
            Assert.That(green.MulR, Is.EqualTo(0.9f).Within(1e-6));
            Assert.That(green.MulG, Is.EqualTo(1f).Within(1e-6));
            Assert.That(green.MulB, Is.EqualTo(0.85f).Within(1e-6));

            // (1, 0.85, 0.88) — :104.
            DesintegrTint pink = DesintegrRules.TintFor(DesintegrTip.BloodPink);
            Assert.That(pink.MulG, Is.EqualTo(0.85f).Within(1e-6));
            Assert.That(pink.MulB, Is.EqualTo(0.88f).Within(1e-6));
        }

        [Test]
        public void TintFor_FirePushesWithOffsetsInPortColourSpace()
        {
            // `new ColorTransform(1,1,1,1, 255/10, 100/10, 0, 0)` — :64. Flash's offsets are 0..255
            // additions; the port's colour space is 0..1, so a per-frame red offset of `255/10` is
            // `0.1`, NOT `25.5`. Getting this wrong tints the body a hundred times too hard.
            DesintegrTint burn = DesintegrRules.TintFor(DesintegrTip.Burn);
            Assert.That(burn.AddR, Is.EqualTo(0.1f).Within(1e-5), "255/10 in 0..255 space is 0.1 in 0..1");
            Assert.That(burn.AddG, Is.EqualTo(100f / 10f / 255f).Within(1e-5));
            Assert.That(burn.AddB, Is.EqualTo(0f).Within(1e-6), "blue is untouched by the fire ramp");
            Assert.That(burn.MulR, Is.EqualTo(1f).Within(1e-6), "the elemental tips multiply by identity");

            // (1,1,1,1, 0, 255/10, 100/10, 0) — :71.
            DesintegrTint plasma = DesintegrRules.TintFor(DesintegrTip.Plasma);
            Assert.That(plasma.AddR, Is.EqualTo(0f).Within(1e-6));
            Assert.That(plasma.AddG, Is.EqualTo(0.1f).Within(1e-5));
            Assert.That(plasma.AddB, Is.EqualTo(100f / 10f / 255f).Within(1e-5));

            // (1,1,1,1, 100/10, 100/10, 255/10, 0) — :85.
            DesintegrTint cryo = DesintegrRules.TintFor(DesintegrTip.Cryo);
            Assert.That(cryo.AddR, Is.EqualTo(100f / 10f / 255f).Within(1e-5));
            Assert.That(cryo.AddB, Is.EqualTo(0.1f).Within(1e-5));
        }

        [Test]
        public void TintAt_CompoundsBecauseTheOracleReTransformsItsOwnBitmap()
        {
            // `burnBmp.colorTransform(burnBmp.rect, burnCt)` (:116) transforms the bitmap it has already
            // transformed, once per frame. Ten applications of 0.85 leave 0.85^10 = 0.1969, not 0.85 —
            // which is the difference between "the body goes deep red" and "the body goes slightly warm".
            DesintegrTint afterTen = DesintegrRules.TintAt(DesintegrTip.BloodRed, 10);
            Assert.That(afterTen.MulG, Is.EqualTo(0.196874f).Within(1e-5), "0.85^10");
            Assert.That(afterTen.MulB, Is.EqualTo(0.196874f).Within(1e-5));

            // The offsets accumulate linearly instead: ten frames of +0.1 is +1.0, i.e. full white.
            DesintegrTint fireTen = DesintegrRules.TintAt(DesintegrTip.Burn, 10);
            Assert.That(fireTen.AddR, Is.EqualTo(1f).Within(1e-5), "255/10 * 10 frames = 255 = 1.0");

            // Frame 0 is the identity — the state before `step()` has run at all.
            DesintegrTint zero = DesintegrRules.TintAt(DesintegrTip.Burn, 0);
            Assert.That(zero.MulR, Is.EqualTo(1f).Within(1e-6));
            Assert.That(zero.AddR, Is.EqualTo(0f).Within(1e-6));
        }

        [Test]
        public void TintAt_StopsRampingAfterTenFrames()
        {
            // `if(burnN > 0 && burnN <= burnTime1)` — :114. Past the tenth frame the oracle stops
            // transforming, so the erosion phase holds the colour it reached.
            DesintegrTint atTen = DesintegrRules.TintAt(DesintegrTip.Burn, 10);
            DesintegrTint atEleven = DesintegrRules.TintAt(DesintegrTip.Burn, 11);
            DesintegrTint atForty = DesintegrRules.TintAt(DesintegrTip.Burn, 40);

            Assert.That(atEleven.AddR, Is.EqualTo(atTen.AddR).Within(1e-6));
            Assert.That(atForty.AddR, Is.EqualTo(atTen.AddR).Within(1e-6));

            // The control: frame 9 is NOT equal to frame 10, or the clamp would be indistinguishable
            // from "the ramp never happened".
            Assert.That(DesintegrRules.TintAt(DesintegrTip.Burn, 9).AddR,
                        Is.Not.EqualTo(atTen.AddR).Within(1e-6));
        }

        // ── The erosion, the glow and the end ──────────────────────────────────────────────────

        [Test]
        public void ErodeFraction_IsZeroThroughTheTintPhase()
        {
            Assert.That(DesintegrRules.ErodeFraction(0), Is.EqualTo(0f));
            Assert.That(DesintegrRules.ErodeFraction(1), Is.EqualTo(0f));
            Assert.That(DesintegrRules.ErodeFraction(10), Is.EqualTo(0f),
                "the erosion branch does not run until burnN > burnTime1 (:119)");
        }

        [Test]
        public void ErodeFraction_ClimbsOneThirtiethPerFrameToExactlyOne()
        {
            Assert.That(DesintegrRules.ErodeFraction(11), Is.EqualTo(1f / 30f).Within(1e-6));
            Assert.That(DesintegrRules.ErodeFraction(20), Is.EqualTo(10f / 30f).Within(1e-6));
            Assert.That(DesintegrRules.ErodeFraction(40), Is.EqualTo(1f).Within(1e-6),
                "`burnKolPix * (40 - 10) / 30` is the whole bitmap");

            // Clamped, not extrapolated: frame 41 must not ask for 103% of the body.
            Assert.That(DesintegrRules.ErodeFraction(41), Is.EqualTo(1f).Within(1e-6));
        }

        [Test]
        public void GlowAlpha_RampsOverTenFramesThenHoldsAtFull()
        {
            // `new GlowFilter(colour, burnN / burnTime1, 3, 3, 2, 3)` — :117.
            Assert.That(DesintegrRules.GlowAlpha(0), Is.EqualTo(0f));
            Assert.That(DesintegrRules.GlowAlpha(1), Is.EqualTo(0.1f).Within(1e-6));
            Assert.That(DesintegrRules.GlowAlpha(10), Is.EqualTo(1f).Within(1e-6));

            // It does NOT fall back to zero when the erosion starts: the oracle only assigns the filter
            // in the first branch, and an assigned filter keeps its last value. A halo that vanished at
            // frame 11 would be the plausible-looking wrong reading.
            Assert.That(DesintegrRules.GlowAlpha(11), Is.EqualTo(1f).Within(1e-6));
            Assert.That(DesintegrRules.GlowAlpha(40), Is.EqualTo(1f).Within(1e-6));
        }

        [Test]
        public void IsComplete_IsTrueAtTheLastErosionFrame()
        {
            Assert.IsFalse(DesintegrRules.IsComplete(39));
            Assert.IsTrue(DesintegrRules.IsComplete(40));
            Assert.IsTrue(DesintegrRules.IsComplete(41));
        }

        [Test]
        public void EmitsParticles_IsTheErosionPhaseOnly()
        {
            Assert.IsFalse(DesintegrRules.EmitsParticles(10), "the tint phase sprays nothing");
            Assert.IsTrue(DesintegrRules.EmitsParticles(11));
            Assert.IsTrue(DesintegrRules.EmitsParticles(40), "the last erosion frame still emits");
            Assert.IsFalse(DesintegrRules.EmitsParticles(41), "and the frame after it does not");
        }

        [Test]
        public void TotalFrames_AgreesWithTheCorpseCountdown()
        {
            // Two constants for one duration. If they ever disagree, a body either freezes fully eroded
            // for N frames or is removed while it still has pixels — and nothing else would notice.
            Assert.AreEqual(CorpseDamageMath.DissolveFrames, DesintegrRules.TotalFrames);
            Assert.AreEqual(DesintegrRules.TintFrames + DesintegrRules.ErodeFrames,
                            DesintegrRules.TotalFrames);
            Assert.AreEqual(10, DesintegrRules.TintFrames, "burnTime1, Desintegr.as:38");
            Assert.AreEqual(30, DesintegrRules.ErodeFrames, "burnTime2, Desintegr.as:39");
        }

        // ── The per-frame particle gate ───────────────────────────────────────────────────────

        [Test]
        public void ShouldEmit_HeavyBodiesAlwaysPass()
        {
            // `if(this.owner.massa >= 0.25 || Math.random() < this.owner.massa * 4)` — :122.
            Assert.IsTrue(DesintegrRules.ShouldEmit(0.25f, 1f), "the boundary itself passes");
            Assert.IsTrue(DesintegrRules.ShouldEmit(0.8f, 0.99f));
            Assert.IsTrue(DesintegrRules.ShouldEmit(1.5f, 0.99f));
        }

        [Test]
        public void ShouldEmit_LightBodiesRoll()
        {
            // massa 0.1 asks for 40% of frames.
            Assert.IsTrue(DesintegrRules.ShouldEmit(0.1f, 0.39f));
            Assert.IsFalse(DesintegrRules.ShouldEmit(0.1f, 0.4f));
            Assert.IsFalse(DesintegrRules.ShouldEmit(0.1f, 0.9f));

            // A zero-mass body never emits through the second term — `0 * 4` is not `> 0`.
            Assert.IsFalse(DesintegrRules.ShouldEmit(0f, 0f));
        }

        // ── `expl()` — the burst a non-dissolving body throws ─────────────────────────────────

        [Test]
        public void ExplosionCount_IsMassTimesFifty()
        {
            // `{kol: massa * 50, rx: scX/2, ry: scY/2}` — Unit.as:4443.
            Assert.AreEqual(25, DesintegrRules.ExplosionCount(0.5f));
            Assert.AreEqual(5, DesintegrRules.ExplosionCount(0.1f));
            Assert.AreEqual(0, DesintegrRules.ExplosionCount(0f));
        }

        [Test]
        public void ExplosionCount_TruncatesRatherThanRounds()
        {
            // `Emitter.cast` loops an integer counter (`Emitter.as:171-179`), so 24.5 spawns 24. Rounding
            // would add a particle to every fractional body in the game — small, systematic, invisible.
            Assert.AreEqual(24, DesintegrRules.ExplosionCount(0.49f));
            Assert.AreEqual(24, DesintegrRules.ExplosionCount(0.4999f));
        }

        [Test]
        public void ExplosionCount_IsCappedAtTheCastLimit()
        {
            // `Emitter.as:176-179` caps one cast at 50, so a boss asking for 75 gets 50.
            Assert.AreEqual(50, DesintegrRules.ExplosionCount(1f));
            Assert.AreEqual(50, DesintegrRules.ExplosionCount(1.5f));
            Assert.AreEqual(DesintegrRules.MaxCastParticles, DesintegrRules.ExplosionCount(9f));
        }

        // ── The oracle's duplicated-key bug, reproduced on purpose ────────────────────────────

        [Test]
        public void ParticleJitter_UsesScYForXAndNothingForY()
        {
            // Desintegr.as:124-127 passes `{ "rx": scX * 0.75, "rx": scY * 0.5 }` — the SAME KEY twice,
            // so `ry` is never supplied and `rx` ends up as `scY * 0.5`. Reproducing the "intended"
            // reading (rx = scX * 0.75, ry = scY * 0.5) changes the shape of every dissolve in the game
            // from a vertical crumble to a wide spray.
            Assert.That(DesintegrRules.ParticleJitterX(55f, 70f), Is.EqualTo(35f).Within(1e-6),
                "scY * 0.5, not scX * 0.75");
            Assert.That(DesintegrRules.ParticleJitterX(55f, 70f), Is.Not.EqualTo(55f * 0.75f).Within(1e-6),
                "if this ever equals scX * 0.75, someone has 'fixed' the oracle's typo");

            Assert.That(DesintegrRules.ParticleJitterY(55f, 70f), Is.EqualTo(0f).Within(1e-6),
                "`ry` is never supplied — a non-zero value would give the spray a width the game lacks");
        }
    }
}
