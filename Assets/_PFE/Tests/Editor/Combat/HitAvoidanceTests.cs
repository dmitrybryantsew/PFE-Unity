using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins <see cref="HitAvoidance"/> — the port of AS3's four-term <c>udarBullet</c> conjunction
    /// (<c>Unit.as:4067-4131</c>).
    ///
    /// <para><b>What these tests are actually defending.</b> Three properties, none of which is obvious
    /// from the code and each of which fails silently if it regresses:</para>
    /// <list type="number">
    ///   <item><description><b>Inert by default.</b> A context with no <c>miss</c> and no
    ///     <c>precision</c>, against a target at AS3's default dexterity, must hit <i>and consume no
    ///     roll</i>. That is what makes the whole port behaviour-neutral until a producer fills the
    ///     fields — and the roll count is the part that matters, because an extra roll would shift the
    ///     shared combat stream and change crit rolls for every other hit in the same tick.</description></item>
    ///   <item><description><b>The two evasion mechanics stay separate.</b> A projectile is dodged by
    ///     accuracy-vs-dexterity and <i>ignores dodge</i>; a melee swing is dodged by the dodge
    ///     probability and <i>ignores precision and distance</i>. Collapsing them would make armour's
    ///     dodge bonus start dodging bullets.</description></item>
    ///   <item><description><b>The short-circuits are in the oracle's order.</b> In particular
    ///     <c>dexter &lt;= 0</c> ends the whole evasion group <i>before</i> the shot-kind split, so a
    ///     target with no dexterity is hit even by a swing it would otherwise fully dodge.</description></item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class HitAvoidanceTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// An RNG that returns scripted values from a queue and counts how many were consumed. The
        /// count is load-bearing: the oracle short-circuits, so "did this path roll at all" is part of
        /// the behaviour, not an implementation detail.
        /// </summary>
        private sealed class ScriptedRng : IRngService
        {
            private readonly Queue<float> _values;

            public int RollsConsumed { get; private set; }

            public ScriptedRng(params float[] values) => _values = new Queue<float>(values);

            public float NextFloat()
            {
                RollsConsumed++;
                return _values.Count > 0 ? _values.Dequeue() : 0f;
            }

            public IRngService GetStream(RngStream stream, int? salt = null) => this;

            public uint NextUInt() => 0u;
            public int NextInt(int maxExclusive) => 0;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public bool Chance(float probability) => NextFloat() < probability;
            public void Shuffle<T>(IList<T> list) { }
        }

        private static DamageContext Context(
            float missChance = 0f,
            float precision = 0f,
            float antiPrecision = 0f,
            bool isMelee = false)
            => new DamageContext(
                owner:             null,
                weapon:            null,
                baseDamage:        10f,
                explosionDamage:   0f,
                armorMultiplier:   1f,
                piercing:          0f,
                knockback:         0f,
                knockbackDir:      Vector2.right,
                critChance:        0f,
                critMultiplier:    1f,
                damageType:        DamageType.PhysicalBullet,
                destroyTiles:      0f,
                penetrationChance: 0f,
                dopEffect:         null,
                dopDamage:         0f,
                dopChance:         0f,
                missChance:        missChance,
                precision:         precision,
                antiPrecision:     antiPrecision,
                isMelee:           isMelee);

        // ── Accuracy (Bullet.accuracy, Bullet.as:311-322) ────────────────────

        [Test]
        public void Accuracy_UnscopedWeapon_IsOne()
        {
            // AS3 returns 1 before it ever divides, which is why `precision <= 0` is its own
            // always-hit term rather than a case inside this function.
            Assert.AreEqual(1f, HitAvoidance.Accuracy(0f, 0f, 500f), 1e-6f);
        }

        [Test]
        public void Accuracy_FallsOffLinearlyWithDistance()
        {
            // prec='8' imports as 320 px (Weapon.as:822 scales by 40). One tile is 40 px, so the
            // weapon is exactly accurate at 8 tiles and halves at 16.
            Assert.AreEqual(2f,   HitAvoidance.Accuracy(320f, 0f, 160f), 1e-5f, "4 tiles: twice accurate");
            Assert.AreEqual(1f,   HitAvoidance.Accuracy(320f, 0f, 320f), 1e-5f, "8 tiles: exactly accurate");
            Assert.AreEqual(0.5f, HitAvoidance.Accuracy(320f, 0f, 640f), 1e-5f, "16 tiles: half");
        }

        [Test]
        public void Accuracy_InsideAntiPrecision_RampsUpWithDistance_NotDown()
        {
            // The direction is the whole point. `antiprec` is a MINIMUM RANGE: inside it the weapon is
            // worse, not better — 0.25 at zero distance rising to 1.0 at the threshold.
            //
            // This test is the one that fails if someone "fixes" the oracle into a minimum-accuracy
            // clamp: at zero distance the oracle answers 0.25 while a clamp answers Infinity (960/0),
            // and a downward ramp answers 1.0.
            Assert.AreEqual(0.25f,  HitAvoidance.Accuracy(960f, 320f, 0f),        1e-5f, "point blank is worst");
            Assert.AreEqual(0.625f, HitAvoidance.Accuracy(960f, 320f, 160f),      1e-5f, "half-way up the ramp");
            Assert.AreEqual(1f,     HitAvoidance.Accuracy(960f, 320f, 319.9999f), 1e-4f, "just below the threshold");

            // And the ramp does NOT reach 1.0 at the threshold — it is a hard branch flip. At exactly
            // `antiprec` the `<` fails and the ordinary falloff answers 960/320 = 3.0, i.e. AS3 has a
            // discontinuity here (accuracy jumps from ~1.0 to 3.0). Asserted as the oracle behaves
            // rather than smoothed, because a "tidied" version would quietly change these four weapons.
            Assert.AreEqual(3f,     HitAvoidance.Accuracy(960f, 320f, 320f),      1e-5f, "at the threshold: branch flips");
            Assert.AreEqual(1.5f,   HitAvoidance.Accuracy(960f, 320f, 640f),      1e-5f, "past it: plain falloff");
        }

        // ── SkillConfidence (Weapon.checkAvail, Weapon.as:1366-1385) ─────────

        [Test]
        public void SkillConfidence_MatchesTheOracleTable()
        {
            Assert.AreEqual(1f,   HitAvoidance.SkillConfidence(3, 3), 1e-6f, "gap 0");
            Assert.AreEqual(1f,   HitAvoidance.SkillConfidence(3, 5), 1e-6f, "over-skilled");
            Assert.AreEqual(0.8f, HitAvoidance.SkillConfidence(3, 2), 1e-6f, "gap 1");
            Assert.AreEqual(0.6f, HitAvoidance.SkillConfidence(3, 1), 1e-6f, "gap 2");
        }

        [Test]
        public void SkillConfidence_UnknownOwnerSkill_IsNoPenalty()
        {
            // The scaffolded divergence, pinned so it cannot be changed by accident. `weaponLevel` runs
            // 0..12 in AllData, so treating "unknown" as a real skill of 1 would hand a level-4 weapon a
            // gap of 3 — which the oracle answers by REFUSING TO FIRE. The port must not read unknown as
            // a penalty.
            Assert.AreEqual(1f, HitAvoidance.SkillConfidence(4, HitAvoidance.UnknownOwnerSkillLevel), 1e-6f);
            Assert.AreEqual(0f, HitAvoidance.MissChance(4, HitAvoidance.UnknownOwnerSkillLevel), 1e-6f,
                "miss must be 0, or every weapon above the assumed skill level starts missing");
        }

        [Test]
        public void MissChance_IsOneMinusConfidence()
        {
            Assert.AreEqual(0.2f, HitAvoidance.MissChance(3, 2), 1e-6f);
            Assert.AreEqual(0.4f, HitAvoidance.MissChance(3, 1), 1e-6f);
        }

        // ── CanFire (the refuse-to-fire half of checkAvail) ───────────────────

        [Test]
        public void CanFire_RefusesOnlyPastAGapOfTwo()
        {
            // Weapon.as:1377 — `else if(_loc1_ > 2) { infoText("weaponSkillLevel"); return false; }`.
            // The boundary is where the behaviour changes, so assert either side of it rather than
            // only the extreme: a gate that refuses at a gap of 2 would pass an "assert gap 3" test.
            Assert.IsTrue(HitAvoidance.CanFire(5, 5),  "gap 0 — at the required tier");
            Assert.IsTrue(HitAvoidance.CanFire(5, 7),  "gap -2 — over-skilled");
            Assert.IsTrue(HitAvoidance.CanFire(5, 4),  "gap 1 — penalised (0.8), not refused");
            Assert.IsTrue(HitAvoidance.CanFire(5, 3),  "gap 2 — penalised (0.6), not refused");
            Assert.IsFalse(HitAvoidance.CanFire(5, 2), "gap 3 — refused");
            Assert.IsFalse(HitAvoidance.CanFire(12, 0), "gap 12 — the top of the AllData lvl range");
        }

        [Test]
        public void CanFire_UnknownOwnerSkill_IsNeverGated()
        {
            // The pair to SkillConfidence_UnknownOwnerSkill_IsNoPenalty, and the one that keeps the
            // port playable: if "unknown" were read as a real tier of 1, every weapon above level 3
            // would refuse to fire for an owner whose Pers was never wired up.
            Assert.IsTrue(HitAvoidance.CanFire(4, HitAvoidance.UnknownOwnerSkillLevel));
            Assert.IsTrue(HitAvoidance.CanFire(12, HitAvoidance.UnknownOwnerSkillLevel));
        }

        [Test]
        public void CanFire_AgreesWithSkillConfidenceAboutWhereThePenaltyEnds()
        {
            // The two halves of checkAvail must partition the gap range: everything CanFire accepts
            // gets a confidence, everything it rejects is a gap SkillConfidence reports as 1 (its
            // "unreachable" case). If the constants ever diverge, the gap that is refused by one and
            // priced by the other shows up here.
            for (int gap = -2; gap <= 5; gap++)
            {
                int weaponLevel = 6, ownerSkill = 6 - gap;
                if (HitAvoidance.CanFire(weaponLevel, ownerSkill))
                {
                    float conf = HitAvoidance.SkillConfidence(weaponLevel, ownerSkill);
                    Assert.That(conf, Is.EqualTo(gap == 1 ? 0.8f : gap == 2 ? 0.6f : 1f).Within(1e-6f),
                        $"gap {gap} is accepted, so it must be priced");
                }
                else
                {
                    Assert.AreEqual(1f, HitAvoidance.SkillConfidence(weaponLevel, ownerSkill), 1e-6f,
                        $"gap {gap} is refused, so it is never priced");
                }
            }
        }

        // ── CanCastSpells (WMagic.attack, WMagic.as:33-39) ────────────────────

        [Test]
        public void CanCastSpells_OnlyTheExactValueZeroRefuses()
        {
            // `World.w.pers.spellsPoss == 0` is an EQUALITY test in the oracle, not a sign test, and
            // the declaration default is 1 (`Pers.as:423`). So every value but 0 permits the cast —
            // including a hypothetical negative, which a "corrected" `<= 0` would refuse. Kept literal
            // on purpose; this is the assertion that would go red if someone tidied it.
            Assert.IsTrue (HitAvoidance.CanCastSpells(1),  "the Pers.as declaration default");
            Assert.IsTrue (HitAvoidance.CanCastSpells(2),  "a spell-unlock level above the baseline");
            Assert.IsFalse(HitAvoidance.CanCastSpells(0),  "mana organ at trauma stage 4");
            Assert.IsTrue (HitAvoidance.CanCastSpells(-1), "AS3 tests == 0, so a negative does not refuse");
        }

        [Test]
        public void CanCastSpells_IsNotTheSkillGate()
        {
            // Differential control. These two gates sit three lines apart in WMagic.attack() and read
            // different inputs — spellsPoss (the mana organ) and the weapon/skill gap. Neither may
            // subsume the other, and a refactor that routed one through the other would show up here:
            // the pair below is REFUSED by CanFire and PERMITTED by CanCastSpells.
            const int weaponLevel = 12, ownerSkill = 0;   // gap 12 — far past the refuse threshold

            Assert.IsFalse(HitAvoidance.CanFire(weaponLevel, ownerSkill),
                "the skill gate refuses this gap");
            Assert.IsTrue(HitAvoidance.CanCastSpells(1),
                "…but spell permission knows nothing about the skill gap, so it still permits");
        }

        // ── SkillPlusDamage (Weapon.setPers, Weapon.as:984-992) ───────────────

        [Test]
        public void SkillPlusDamage_IsOneUntilTheOwnerBeatsTheRequirement()
        {
            // `if(_loc3_ < 0) skillPlusDam = 1 - _loc3_*0.1; else 1`. Being under-qualified is NOT a
            // damage penalty here — that is SkillConfidence and CanFire. It is simply no bonus, which
            // is the half most likely to be "fixed" into a penalty by mistake.
            Assert.AreEqual(1f, HitAvoidance.SkillPlusDamage(5, 5), 1e-6f, "gap 0 — exactly qualified");
            Assert.AreEqual(1f, HitAvoidance.SkillPlusDamage(5, 4), 1e-6f, "gap 1 — no bonus, no penalty");
            Assert.AreEqual(1f, HitAvoidance.SkillPlusDamage(5, 3), 1e-6f, "gap 2 — still no penalty");
            Assert.AreEqual(1f, HitAvoidance.SkillPlusDamage(5, 2), 1e-6f,
                "gap 3 is REFUSED by CanFire, but if a caller asks anyway the answer is still no bonus.");
        }

        [Test]
        public void SkillPlusDamage_AddsTenPercentPerTierOfOverskill()
        {
            // One tier above the requirement is a gap of -1 -> 1.1, two -> 1.2, and so on. The oracle
            // has no cap on this; the tier table tops out at 5, so the largest real bonus is a tier-5
            // shooter on a level-0 weapon: 1 + 5*0.1 = 1.5.
            Assert.AreEqual(1.1f, HitAvoidance.SkillPlusDamage(4, 5), 1e-6f, "gap -1");
            Assert.AreEqual(1.2f, HitAvoidance.SkillPlusDamage(3, 5), 1e-6f, "gap -2");
            Assert.AreEqual(1.3f, HitAvoidance.SkillPlusDamage(2, 5), 1e-6f, "gap -3");
            Assert.AreEqual(1.5f, HitAvoidance.SkillPlusDamage(0, 5), 1e-6f, "the largest reachable bonus");
        }

        [Test]
        public void SkillPlusDamage_UnknownOwnerSkill_IsNoBonus()
        {
            // Explicit, not incidental: the arithmetic would also land on 1 for the current sentinel
            // (-1 makes the gap positive), but that is a property of the value rather than a rule.
            // This pins the intent so a future sentinel change cannot quietly grant free damage.
            Assert.AreEqual(1f, HitAvoidance.SkillPlusDamage(4, HitAvoidance.UnknownOwnerSkillLevel), 1e-6f);
            Assert.AreEqual(1f, HitAvoidance.SkillPlusDamage(0, HitAvoidance.UnknownOwnerSkillLevel), 1e-6f);
        }

        [Test]
        public void SkillPlusDamage_AndCanFire_ReadTheSameGapWithOppositeSigns()
        {
            // The two halves of the gap rule, pinned together so neither can drift: below the
            // requirement there is no bonus but there may still be a shot; far below it the shot is
            // refused; above it the shot is free and the bonus grows.
            for (int ownerTier = 0; ownerTier <= 7; ownerTier++)
            {
                const int weaponLevel = 4;
                float bonus = HitAvoidance.SkillPlusDamage(weaponLevel, ownerTier);
                bool canFire = HitAvoidance.CanFire(weaponLevel, ownerTier);

                if (ownerTier > weaponLevel)
                    Assert.Greater(bonus, 1f, $"tier {ownerTier} beats level {weaponLevel}: a bonus");
                else
                    Assert.AreEqual(1f, bonus, 1e-6f, $"tier {ownerTier} does not beat level {weaponLevel}");

                if (!canFire)
                    Assert.AreEqual(1f, bonus, 1e-6f, "a refused shot never carries a bonus");
            }
        }

        // ── The conjunction, term by term ────────────────────────────────────

        [Test]
        public void RollsHit_InertContext_AlwaysHits_AndConsumesNoRoll()
        {
            // The behaviour-neutrality guard, and the single most important test here. With no miss
            // penalty and no precision the oracle short-circuits to a hit without rolling, so the
            // shared combat stream is exactly where it would have been before this port landed.
            var rng = new ScriptedRng();

            bool hit = HitAvoidance.RollsHit(Context(), EvasionState.Default, 500f, rng);

            Assert.IsTrue(hit, "a shot with no miss penalty and no precision must always land");
            Assert.AreEqual(0, rng.RollsConsumed,
                "the oracle short-circuits before the dodge group, so no roll may be taken — an extra " +
                "roll here would shift every later crit roll in the same tick");
        }

        [Test]
        public void RollsHit_MissChanceOne_AlwaysMisses_AndRolls()
        {
            var rng = new ScriptedRng(0.99f);

            Assert.IsFalse(HitAvoidance.RollsHit(Context(missChance: 1f), EvasionState.Default, 100f, rng));
            Assert.AreEqual(1, rng.RollsConsumed, "a positive miss term is the one case that does roll");
        }

        [Test]
        public void RollsHit_MissChanceZero_TakesNoRollForTheMissTerm()
        {
            // The complement of the test above: `miss <= 0` short-circuits, so the roll count stays 0
            // on a path that would otherwise consume one.
            var rng = new ScriptedRng(0.99f);

            HitAvoidance.RollsHit(Context(missChance: 0f), EvasionState.Default, 100f, rng);

            Assert.AreEqual(0, rng.RollsConsumed);
        }

        [Test]
        public void RollsHit_HighDexterity_CanEvadeAProjectile()
        {
            // The dexterity mechanic. accuracy = 320/320 = 1.0, so the hit probability is
            // 1.0 / (dexter + 0.05).
            var evasive = new EvasionState(100f, 0f, 0f);   // the `npc` dummy's dexter='100'
            var rng     = new ScriptedRng(0.5f);

            Assert.IsFalse(HitAvoidance.RollsHit(Context(precision: 320f), evasive, 320f, rng),
                "1.0/100.05 = 0.00999 hit chance, so a 0.5 roll must miss");

            var plain = new EvasionState(1f, 0f, 0f);
            var rng2  = new ScriptedRng(0.5f);

            Assert.IsTrue(HitAvoidance.RollsHit(Context(precision: 320f), plain, 320f, rng2),
                "1.0/1.05 = 0.952 hit chance, so a 0.5 roll must land");
        }

        [Test]
        public void RollsHit_PrecisionZero_AlwaysHits_EvenAgainstHighDexterity()
        {
            // Term 3 must stay separate from term 4. Accuracy() returns 1 for an unscoped weapon, so
            // folding the two together would evaluate `rnd < 1/100.05` and make every weapon without a
            // precision stat miss against an evasive target.
            var rng = new ScriptedRng(0.5f);

            Assert.IsTrue(HitAvoidance.RollsHit(
                Context(precision: 0f), new EvasionState(100f, 0f, 0f), 320f, rng));
            Assert.AreEqual(0, rng.RollsConsumed);
        }

        [Test]
        public void RollsHit_DexterityZero_AlwaysHits()
        {
            // `dexter <= 0` is the first term of the evasion group: such a target cannot evade at all.
            var rng = new ScriptedRng(0.999f);

            Assert.IsTrue(HitAvoidance.RollsHit(
                Context(precision: 960f), new EvasionState(0f, 0f, 0f), 640f, rng));
            Assert.AreEqual(0, rng.RollsConsumed);
        }

        [Test]
        public void RollsHit_DexterityPlus_AddsToTheDivisor()
        {
            // dexterPlus is an addition, not a multiplier — a mis-port that multiplied would make
            // sitting/lurking a massive evasion buff instead of the flat +0.25-ish it is.
            var rng = new ScriptedRng(0.05f);

            // accuracy = 100/100 = 1.0; divisor = 1 + 0.95 + 0.05 = 2.0 ⇒ hit chance 0.5 ⇒ a 0.05 roll
            // lands. With the bonus ignored the divisor would be 1.05 and 0.05 would still land, so the
            // assertion below is the one that discriminates: at a 0.6 roll the bonus is the difference.
            Assert.IsTrue(HitAvoidance.RollsHit(
                Context(precision: 100f), new EvasionState(1f, 0.95f, 0f), 100f, rng));

            var rng2 = new ScriptedRng(0.6f);
            Assert.IsFalse(HitAvoidance.RollsHit(
                Context(precision: 100f), new EvasionState(1f, 0.95f, 0f), 100f, rng2),
                "0.6 < 0.5 is false, so the bonus must have pushed the divisor to ~2.0");
        }

        // ── Melee: the dodge probability, and its exclusivity ───────────────

        [Test]
        public void RollsHit_MeleeDodgeZero_AlwaysHits_WithoutRolling()
        {
            // The NPC case, and the reason a club always lands on one: `dodge <= 0` short-circuits.
            var rng = new ScriptedRng(0.99f);

            Assert.IsTrue(HitAvoidance.RollsHit(
                Context(isMelee: true), new EvasionState(1f, 0f, 0f), 0f, rng));
            Assert.AreEqual(0, rng.RollsConsumed);
        }

        [Test]
        public void RollsHit_MeleeDodge_HalfDodgesOnTheRoll()
        {
            // `Math.random() > dodge` — so a HIGH roll hits and a low roll is dodged. Inverting the
            // comparison is the easy mistake, and it makes a dodgy target easier to hit.
            Assert.IsTrue(HitAvoidance.RollsHit(
                Context(isMelee: true), new EvasionState(1f, 0f, 0.5f), 0f, new ScriptedRng(0.6f)),
                "0.6 > 0.5 ⇒ the swing connects");

            Assert.IsFalse(HitAvoidance.RollsHit(
                Context(isMelee: true), new EvasionState(1f, 0f, 0.5f), 0f, new ScriptedRng(0.4f)),
                "0.4 > 0.5 is false ⇒ dodged");
        }

        [Test]
        public void RollsHit_MeleeFullDodge_AlwaysMisses()
        {
            // `dodge < 1` is part of the term, so a dodge of exactly 1 dodges everything — and does it
            // without a roll, because the whole term fails on the first conjunct.
            var rng = new ScriptedRng(0.999f);

            Assert.IsFalse(HitAvoidance.RollsHit(
                Context(isMelee: true), new EvasionState(1f, 0f, 1f), 0f, rng));
            Assert.AreEqual(0, rng.RollsConsumed);
        }

        [Test]
        public void RollsHit_MeleeIgnoresPrecisionAndDistance()
        {
            // A melee swing has no distance term in the oracle, so a weapon with a huge precision
            // stat and a target 40 tiles away still resolves on the dodge probability alone.
            var rng = new ScriptedRng(0.5f);

            Assert.IsTrue(HitAvoidance.RollsHit(
                Context(precision: 960f, isMelee: true), new EvasionState(1f, 0f, 0f), 1600f, rng));
            Assert.AreEqual(0, rng.RollsConsumed, "the melee branch must not compute accuracy");
        }

        [Test]
        public void RollsHit_ProjectileIgnoresDodge()
        {
            // The other half of the exclusivity guard. dodge comes from equipped armour
            // (Pers.as:2016-2017), so if the projectile branch read it a plate's dodge bonus would
            // start dodging bullets.
            //
            // A real precision is required for this to discriminate: with precision 0 the shot
            // short-circuits at term 3 and never reaches the evasion arithmetic at all, so a
            // dodge-reading projectile branch would still pass. With accuracy = 320/320 = 1.0 and a
            // divisor of 1.05 the roll lands, so the only way to fail is to have consulted dodge.
            var rng = new ScriptedRng(0.5f);

            Assert.IsTrue(HitAvoidance.RollsHit(
                Context(precision: 320f), new EvasionState(1f, 0f, 1f), 320f, rng),
                "a full melee dodge must not protect against a bullet");

            // And the same target IS protected from a swing — otherwise the assertion above would also
            // pass on an implementation that ignored dodge entirely.
            Assert.IsFalse(HitAvoidance.RollsHit(
                Context(isMelee: true), new EvasionState(1f, 0f, 1f), 320f, new ScriptedRng(0.999f)),
                "the same dodge must still stop a melee swing");
        }

        [Test]
        public void RollsHit_DexterityZero_OverridesAFullDodge()
        {
            // Ordering guard: `dexter <= 0` is evaluated before the tipBullet split, so a target with
            // no dexterity is hit even by a swing it would otherwise dodge completely. Reading the
            // melee branch first would silently make this a miss.
            var rng = new ScriptedRng(0.999f);

            Assert.IsTrue(HitAvoidance.RollsHit(
                Context(isMelee: true), new EvasionState(0f, 0f, 1f), 0f, rng));
            Assert.AreEqual(0, rng.RollsConsumed);
        }

        // ── The null-RNG contract ────────────────────────────────────────────

        [Test]
        public void RollsHit_NullRng_Hits()
        {
            // Documented, not accidental: with no randomness available the only safe direction is the
            // one that leaves damage working. Asserted so "always hits" cannot be mistaken for
            // "evasion is broken" by a later reader.
            Assert.IsTrue(HitAvoidance.RollsHit(
                Context(missChance: 1f, precision: 960f), new EvasionState(100f, 0f, 1f), 100f, null));
        }
    }
}
