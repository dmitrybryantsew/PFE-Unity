using NUnit.Framework;
using PFE.Systems.Pets;

namespace PFE.Tests.EditMode.Sim
{
    /// <summary>
    /// Fixtures for the companion rules — every assertion cites the oracle line it pins.
    ///
    /// <para>The three defects this file exists to catch are the ones that read as obvious and are
    /// wrong: <c>runSpeed = maxSpeed * 2</c> overwriting the XML's <c>run</c>, <c>level = pers.level
    /// - 1</c> in <c>setLevel</c>, and the <c>+ 1</c> inside the "can my weapon hurt it" comparison.
    /// Each has a test that fails on the plausible misreading, not just on a missing feature.</para>
    /// </summary>
    [TestFixture]
    public class PetRulesTests
    {
        // ── setLevel (UnitPet.as:199-225) ──────────────────────────────────────────────────────

        [Test]
        public void ScaleStats_Phoenix_AtLevelOne_UsesRawPetStats()
        {
            PetCombatStats stats = PetRules.ScaleStats(PetKind.Phoenix, 1, PetStatInput.Defaults);

            // `level = param1 - 1` (:201), then `petHP * (1 + level * 0.12)` (:205).
            Assert.That(stats.MaxHp, Is.EqualTo(50f).Within(0.001f), "Pers.as:439 — petHP = 50, level 0");
            Assert.That(stats.Damage, Is.EqualTo(2f).Within(0.001f), "Pers.as:441 — petDam = 2, level 0");
            Assert.That(stats.Skin, Is.EqualTo(0f).Within(0.001f), "Pers.as:443 — petSkin = 0");
            Assert.That(stats.AllVulnerabilityMultiplier, Is.EqualTo(1f).Within(0.001f), "Pers.as:445 — petVulner = 1");
        }

        [Test]
        public void ScaleStats_Phoenix_AtLevelTen_UsesLevelMinusOne_NotLevel()
        {
            PetCombatStats stats = PetRules.ScaleStats(PetKind.Phoenix, 10, PetStatInput.Defaults);

            // level = 9: 50 * (1 + 9 * 0.12) = 104, 2 * (1 + 9 * 0.10) = 3.8.
            // The misreading `level = param1` gives 50 * 2.2 = 110 and 4.0 — this is the guard.
            Assert.That(stats.MaxHp, Is.EqualTo(104f).Within(0.001f), "UnitPet.as:201,205");
            Assert.That(stats.Damage, Is.EqualTo(3.8f).Within(0.001f), "UnitPet.as:206");
        }

        [Test]
        public void ScaleStats_Owl_UsesOwlStats_AndIgnoresSpellPower()
        {
            var stats = new PetStatInput(
                petHp: 50f, petDamage: 2f, petSkin: 0f, petVulnerability: 1f,
                owlHp: 200f, owlDamage: 3f, owlSkin: 5f, owlVulnerability: 1f,
                moonHp: 70f, moonDamage: 15f, spellPower: 3f);

            PetCombatStats owl = PetRules.ScaleStats(PetKind.Owl, 1, stats);

            Assert.That(owl.MaxHp, Is.EqualTo(200f).Within(0.001f), "Pers.as:449 — owlHP = 200");
            Assert.That(owl.Damage, Is.EqualTo(3f).Within(0.001f), "Pers.as:451 — owlDam = 3");
            Assert.That(owl.Skin, Is.EqualTo(5f).Within(0.001f), "Pers.as:453 — owlSkin = 5");
            Assert.That(owl.AllVulnerabilityMultiplier, Is.EqualTo(1f).Within(0.001f),
                "UnitPet.as:211-218 — the owl branch has no spellPower term");
        }

        [Test]
        public void ScaleStats_Moon_MultipliesBySpellPower()
        {
            var stats = new PetStatInput(
                petHp: 50f, petDamage: 2f, petSkin: 0f, petVulnerability: 1f,
                owlHp: 200f, owlDamage: 3f, owlSkin: 5f, owlVulnerability: 1f,
                moonHp: 70f, moonDamage: 15f, spellPower: 2f);

            PetCombatStats moon = PetRules.ScaleStats(PetKind.Moon, 1, stats);

            // UnitPet.as:221-222 — the only branch with `* this.gg.spellPower`.
            Assert.That(moon.MaxHp, Is.EqualTo(140f).Within(0.001f), "70 * 1 * 2");
            Assert.That(moon.Damage, Is.EqualTo(30f).Within(0.001f), "15 * 1 * 2");
        }

        [Test]
        public void RunSpeed_IsDoubleWalkSpeed_NotTheXmlRunAttribute()
        {
            // AllData.as:1322 authors run='15'; UnitPet.as:74 overwrites it with maxSpeed * 2.
            Assert.That(PetRules.RunSpeed(5f), Is.EqualTo(10f).Within(0.001f),
                "UnitPet.as:74 — `runSpeed = maxSpeed * 2`");
            Assert.That(PetRules.RunSpeed(7f), Is.EqualTo(14f).Within(0.001f),
                "AllData.as:1339 — the moon's speed='7'");
        }

        [Test]
        public void RescaleCurrentHp_PreservesTheRatio_AcrossALevelUp()
        {
            // UnitPet.as:202, :224 — `var _loc2_ = hp / maxhp; … hp = _loc2_ * maxhp;`
            Assert.That(PetRules.RescaleCurrentHp(25f, 50f, 104f), Is.EqualTo(52f).Within(0.001f));
        }

        [Test]
        public void ResurrectHp_IsHalfMaxHp_CappedAtOneHundred()
        {
            // UnitPet.as:529 — `hp = Math.min(100, maxhp / 2)`
            Assert.That(PetRules.ResurrectHp(200f), Is.EqualTo(100f).Within(0.001f), "half is 100, cap does not bite");
            Assert.That(PetRules.ResurrectHp(400f), Is.EqualTo(100f).Within(0.001f), "half is 200, cap bites");
            Assert.That(PetRules.ResurrectHp(50f), Is.EqualTo(25f).Within(0.001f), "below the cap, half");
        }

        // ── the flag table (UnitPet.as:87-108) ────────────────────────────────────────────────

        [Test]
        public void AutoResurrects_IsPhoenixOnly()
        {
            Assert.That(PetRules.AutoResurrects(PetKind.Phoenix), Is.True, "UnitPet.as:89");
            Assert.That(PetRules.AutoResurrects(PetKind.Owl), Is.False, "UnitPet.as:94");
            Assert.That(PetRules.AutoResurrects(PetKind.Moon), Is.False, "UnitPet.as:101");
        }

        [Test]
        public void UncallsOnDeath_IsMoonOnly()
        {
            Assert.That(PetRules.UncallsOnDeath(PetKind.Moon), Is.True, "UnitPet.as:103");
            Assert.That(PetRules.UncallsOnDeath(PetKind.Phoenix), Is.False, "default optUncall = false (:48)");
            Assert.That(PetRules.UncallsOnDeath(PetKind.Owl), Is.False);
        }

        [Test]
        public void CanPerch_IsFalseForTheMoon()
        {
            Assert.That(PetRules.CanPerch(PetKind.Moon), Is.False, "UnitPet.as:102 — optSit = false");
            Assert.That(PetRules.CanPerch(PetKind.Phoenix), Is.True, "default optSit = true (:44)");
        }

        [Test]
        public void TurnsToTarget_IsFalseForTheMoon()
        {
            Assert.That(PetRules.TurnsToTarget(PetKind.Moon), Is.False, "UnitPet.as:105 — optTurn = false");
            Assert.That(PetRules.TurnsToTarget(PetKind.Owl), Is.True);
        }

        [Test]
        public void WeaponRange_IsTwoHundredForPhoenix_FourHundredForTheRest()
        {
            Assert.That(PetRules.WeaponRange(PetKind.Phoenix), Is.EqualTo(200f), "UnitPet.as:90");
            Assert.That(PetRules.WeaponRange(PetKind.Owl), Is.EqualTo(400f), "UnitPet.as:95");
            Assert.That(PetRules.WeaponRange(PetKind.Moon), Is.EqualTo(400f), "UnitPet.as:104");
        }

        [Test]
        public void DeathCooldownTicks_IsPetResTimesThirty()
        {
            // UnitPet.as:517 — `noPet = World.w.pers.petRes * 30`; Pers.as:861 makes petRes 30.
            Assert.That(PetRules.DeathCooldownTicks(PetRules.EffectivePetRes), Is.EqualTo(900),
                "30 s at 30 fps");
            Assert.That(PetRules.DeathCooldownTicks(20f), Is.EqualTo(600), "the easy-difficulty column, if it ever lands");
        }

        // ── callPet (UnitPlayer.as:3899-3952) ─────────────────────────────────────────────────

        static PetCallRequest Call(
            PetKind kind, bool forced = false, int noPet = 0, int lockTicks = 0, bool canAttack = true,
            bool alicorn = false, PetKind? current = null, float owlHp = 200f, bool petsAllowed = true)
            => new PetCallRequest(kind, forced, noPet, lockTicks, canAttack, alicorn, current, owlHp, petsAllowed);

        [Test]
        public void DecideCall_SummonsWhenNothingBlocks()
        {
            PetCallDecision d = PetRules.DecideCall(Call(PetKind.Phoenix));

            Assert.That(d.Action, Is.EqualTo(PetCallAction.Summon), "UnitPlayer.as:3936-3944");
            Assert.That(d.Refused, Is.False);
        }

        [Test]
        public void DecideCall_CooldownBlocks_UnlessForced()
        {
            Assert.That(PetRules.DecideCall(Call(PetKind.Phoenix, noPet: 1)).Refusal,
                Is.EqualTo(PetCallRefusal.Cooldown), "UnitPlayer.as:3901 — `noPet > 0 && !param2`");

            Assert.That(PetRules.DecideCall(Call(PetKind.Phoenix, forced: true, noPet: 1)).Action,
                Is.EqualTo(PetCallAction.Summon), "UnitPlayer.as:3901 — the spell's force bypasses it");
        }

        [Test]
        public void DecideCall_TheForceTermGuardsOnlyTheLock()
        {
            // AS3 `noPet2 > 0 && !param2 || !atkPoss || alicorn`: && binds tighter than ||, so a
            // forced call skips the LOCK but still cannot summon while atkPoss is false.
            Assert.That(PetRules.DecideCall(Call(PetKind.Owl, forced: true, lockTicks: 10, canAttack: false)).Refusal,
                Is.EqualTo(PetCallRefusal.Locked), "UnitPlayer.as:3906 — `!this.atkPoss` is outside the guard");
        }

        [Test]
        public void DecideCall_RefusesWhileAlicorn()
        {
            Assert.That(PetRules.DecideCall(Call(PetKind.Phoenix, alicorn: true)).Refusal,
                Is.EqualTo(PetCallRefusal.Locked), "UnitPlayer.as:3906 — `|| World.w.alicorn`");
        }

        [Test]
        public void DecideCall_RefusesADeadOwlOnlyWhileItIsOut()
        {
            Assert.That(PetRules.DecideCall(Call(PetKind.Owl, current: PetKind.Owl, owlHp: 0f)).Refusal,
                Is.EqualTo(PetCallRefusal.OwlDead), "UnitPlayer.as:3911-3914");

            Assert.That(PetRules.DecideCall(Call(PetKind.Owl, current: null, owlHp: 0f)).Action,
                Is.EqualTo(PetCallAction.Summon), "a recalled dead owl can be re-summoned — it revives on repair");
        }

        [Test]
        public void DecideCall_AskingForTheSamePetTogglesItOff()
        {
            PetCallDecision d = PetRules.DecideCall(Call(PetKind.Phoenix, current: PetKind.Phoenix));

            Assert.That(d.Action, Is.EqualTo(PetCallAction.Recall), "UnitPlayer.as:3925-3927");
        }

        [Test]
        public void DecideCall_TheToggleIsDecidedBeforeTheRoomsPetBan()
        {
            // `if(currentPet == param1) … else if(!loc.petOn) …` — a no-pet room can still dismiss.
            PetCallDecision d = PetRules.DecideCall(
                Call(PetKind.Phoenix, current: PetKind.Phoenix, petsAllowed: false));

            Assert.That(d.Action, Is.EqualTo(PetCallAction.Recall), "UnitPlayer.as:3925-3928 — the order matters");
        }

        [Test]
        public void DecideCall_DoesNothingInARoomThatForbidsPets()
        {
            PetCallDecision d = PetRules.DecideCall(Call(PetKind.Phoenix, petsAllowed: false));

            Assert.That(d.Action, Is.EqualTo(PetCallAction.None), "UnitPlayer.as:3928-3931 — `noPetCall`");
            Assert.That(d.Refused, Is.False, "the oracle prints a message, it is not one of the three refusals");
        }

        [Test]
        public void DecideCall_ForcedBypassesEveryRefusal()
        {
            PetCallDecision d = PetRules.DecideCall(
                Call(PetKind.Moon, forced: true, noPet: 5, lockTicks: 5, current: PetKind.Moon, owlHp: 0f));

            Assert.That(d.Action, Is.EqualTo(PetCallAction.Recall),
                "Spell.as:428-434 calls with force, and the same-pet toggle still applies");
        }

        // ── healing (UnitPet.as:162-186) ──────────────────────────────────────────────────────

        [Test]
        public void RadiationHeal_OnlyTouchesALivingPhoenix()
        {
            Assert.That(PetRules.RadiationHealAllowed(PetKind.Phoenix, 1), Is.True, "UnitPet.as:164");
            Assert.That(PetRules.RadiationHealAllowed(PetKind.Phoenix, 4), Is.False, "sost >= 3 — a dead phoenix waits");
            Assert.That(PetRules.RadiationHealAllowed(PetKind.Owl, 1), Is.False, "UnitPet.as:164 — `id != \"phoenix\"`");
            Assert.That(PetRules.RadiationHealAllowed(PetKind.Moon, 1), Is.False);
        }

        [Test]
        public void HealedHp_ClampsAtMax()
        {
            Assert.That(PetRules.HealedHp(40f, 50f, 30f), Is.EqualTo(50f).Within(0.001f), "UnitPet.as:168-172");
            Assert.That(PetRules.HealedHp(40f, 50f, 5f), Is.EqualTo(45f).Within(0.001f));
            Assert.That(PetRules.HealedHp(40f, 50f, 0f), Is.EqualTo(40f).Within(0.001f), "a zero heal is not a heal");
        }

        // ── targeting (UnitPet.as:375-411) ────────────────────────────────────────────────────

        [Test]
        public void WeaponCanHurt_KeepsThePlusOneTerm()
        {
            // `damage * vulner < armour + skin + 1 - pier` (:397)
            Assert.That(PetRules.WeaponCanHurt(2f, 1f, 0f, 0f, 0f), Is.True,
                "2 < 0 + 0 + 1 → false, so the target IS attackable");
            Assert.That(PetRules.WeaponCanHurt(1f, 1f, 1f, 0f, 0f), Is.False,
                "1 < 1 + 0 + 1 - 0 → true, so it is skipped");
            Assert.That(PetRules.WeaponCanHurt(1f, 1f, 1f, 0f, 1f), Is.True,
                "piercing 1 turns the same comparison around");
        }

        [Test]
        public void TurretIsTargetable_OnlyOnceItHasNoticedSomething()
        {
            Assert.That(PetRules.TurretIsTargetable(true, 1), Is.False, "UnitPet.as:389 — `aiState <= 1`");
            Assert.That(PetRules.TurretIsTargetable(true, 2), Is.True);
            Assert.That(PetRules.TurretIsTargetable(false, 0), Is.True, "a non-turret is never filtered here");
        }

        [Test]
        public void RamSelfDamage_IsTwoPercentOfThePetsDamage()
        {
            // UnitPet.as:797 — `damage(dam * 0.02, Unit.D_INSIDE)`
            Assert.That(PetRules.RamSelfDamage(200f), Is.EqualTo(4f).Within(0.001f));
        }

        // ── getFlyPoint (UnitPet.as:303-368) ─────────────────────────────────────────────────

        [Test]
        public void FollowPoint_SitsBehindThePlayer()
        {
            PetPoint p = PetRules.FollowPoint(500f, 400f, facing: 1, limitX: 1000f, limitY: 1000f);

            // `flyX = gg.X + gg.storona * (-120)`, `flyY = gg.Y - 80` (:306-310)
            Assert.That(p.X, Is.EqualTo(380f).Within(0.001f));
            Assert.That(p.Y, Is.EqualTo(320f).Within(0.001f));
        }

        [Test]
        public void FollowPoint_ClampsInTheOraclesOrder_SoANarrowRoomGoesNegative()
        {
            // x=380 → `if(x < 60) x = 60` → 60 → `if(x > limX-60) x = limX-60` → 40.
            // Doing the two clamps in the other order leaves 60; the oracle's order is the guard.
            PetPoint p = PetRules.FollowPoint(500f, 400f, facing: 1, limitX: 100f, limitY: 1000f);

            Assert.That(p.X, Is.EqualTo(40f).Within(0.001f), "UnitPet.as:311-318");
        }

        [Test]
        public void SnapToTileGrid_AddsTwentyAndThirtyNine()
        {
            // UnitPet.as:344-345
            PetPoint p = PetRules.SnapToTileGrid(100f, 100f);
            Assert.That(p.X, Is.EqualTo(100f).Within(0.001f), "floor(2.5)*40+20");
            Assert.That(p.Y, Is.EqualTo(119f).Within(0.001f), "floor(2.5)*40+39");
        }

        [Test]
        public void ProbeGridFallback_WalksTheFourCellsInOrder()
        {
            // The oracle accumulates BOTH offsets before the third probe, so the four cells are
            // (100,100), (140,140), (140,180), (180,180) — not a column at the original x (:350-365).
            PetPoint? hit = PetRules.ProbeGridFallback(100f, 100f, facing: 1,
                (x, y) => !(x == 140f && y == 180f));

            Assert.That(hit.HasValue, Is.True);
            Assert.That(hit.Value.X, Is.EqualTo(140f).Within(0.001f), "UnitPet.as:356-360 — the third probe");
            Assert.That(hit.Value.Y, Is.EqualTo(180f).Within(0.001f));
        }

        [Test]
        public void ProbeGridFallback_ReturnsTheFirstCellWhenItIsAlreadyFree()
        {
            PetPoint? hit = PetRules.ProbeGridFallback(100f, 100f, facing: 1, (x, y) => false);

            Assert.That(hit.HasValue, Is.True, "UnitPet.as:346-349 — the snapped point itself");
            Assert.That(hit.Value.X, Is.EqualTo(100f).Within(0.001f));
        }

        [Test]
        public void ProbeGridFallback_ReturnsNull_WhenAllFourAreSolid()
        {
            Assert.That(PetRules.ProbeGridFallback(0f, 0f, 1, (x, y) => true).HasValue, Is.False,
                "UnitPet.as:366-367 — the caller then falls back to the player's own feet");
        }

        [Test]
        public void IsPerchCandidate_AcceptsTheMinusFortyToEightyBand()
        {
            // `flyY - Y1 < 80 && flyY - Y1 > -40` (:331)
            Assert.That(PetRules.IsPerchCandidate(false, true, false, 0f, 100f, topY: 300f, flyX: 50f, flyY: 350f),
                Is.True, "dy = 50");
            Assert.That(PetRules.IsPerchCandidate(false, true, false, 0f, 100f, topY: 300f, flyX: 50f, flyY: 390f),
                Is.False, "dy = 90, outside the band");
            Assert.That(PetRules.IsPerchCandidate(false, true, false, 0f, 100f, topY: 300f, flyX: 150f, flyY: 350f),
                Is.False, "the prop does not span the fly point");
            Assert.That(PetRules.IsPerchCandidate(true, true, false, 0f, 100f, topY: 300f, flyX: 50f, flyY: 350f),
                Is.False, "a wall is not a seat");
        }

        // ── the timers (UnitPet.as:601-658) ──────────────────────────────────────────────────

        [Test]
        public void NextRetargetTicks_MovingIsTenToTwentyNine_StillIsThirtyToOneTwentyNine()
        {
            Assert.That(PetRules.NextRetargetTicks(5f, 0f), Is.EqualTo(10), "UnitPet.as:652 — rand*20+10");
            Assert.That(PetRules.NextRetargetTicks(-5f, 0.999f), Is.EqualTo(29), "|dx| > 2, upper end");
            Assert.That(PetRules.NextRetargetTicks(0f, 0f), Is.EqualTo(30), "UnitPet.as:656 — rand*100+30");
            Assert.That(PetRules.NextRetargetTicks(0f, 0.999f), Is.EqualTo(129), "upper end");
            Assert.That(PetRules.NextRetargetTicks(2f, 0f), Is.EqualTo(30), "`> 2`, so exactly 2 is the still branch");
        }

        [Test]
        public void ShouldAcquireTarget_RequiresTheModuloAndNoSpawnGrace()
        {
            Assert.That(PetRules.ShouldAcquireTarget(1, 31, 0), Is.True, "UnitPet.as:745");
            Assert.That(PetRules.ShouldAcquireTarget(2, 31, 0), Is.False, "already attacking");
            Assert.That(PetRules.ShouldAcquireTarget(1, 32, 0), Is.False, "31 % 30 == 1");
            Assert.That(PetRules.ShouldAcquireTarget(1, 31, 1), Is.False, "oduplenie > 0");
        }

        [Test]
        public void ShouldRetarget_OnlyWhileAttackingOrRecoiling()
        {
            Assert.That(PetRules.ShouldRetarget(2, 16), Is.True, "UnitPet.as:752 — 16 % 15 == 1");
            Assert.That(PetRules.ShouldRetarget(5, 31), Is.True);
            Assert.That(PetRules.ShouldRetarget(1, 16), Is.False);
        }

        // ── steering (UnitPet.as:659-740) ────────────────────────────────────────────────────

        [Test]
        public void ApproachSpeed_CapsAtTwiceTheAcceleration()
        {
            // `norma(spd, Math.min(accel * 2, accel * _loc1_ / 200))` (:684)
            Assert.That(PetRules.ApproachSpeed(1.5f, 100f), Is.EqualTo(0.75f).Within(0.001f), "1.5*100/200");
            Assert.That(PetRules.ApproachSpeed(1.5f, 1000f), Is.EqualTo(3f).Within(0.001f), "capped at accel*2");
        }

        [Test]
        public void CloseIn_BackOff_And_Coast_UseTheOraclesRings()
        {
            Assert.That(PetRules.ShouldCloseIn(181f, 200f, true), Is.True, "181 > 200*0.9");
            Assert.That(PetRules.ShouldCloseIn(180f, 200f, true), Is.False,
                "exactly 0.9 * range is NOT close-in — the oracle's test is `>`, not `>=` (:680)");
            Assert.That(PetRules.ShouldCloseIn(170f, 200f, true), Is.False, "inside 0.9 * range");
            Assert.That(PetRules.ShouldCloseIn(170f, 200f, false), Is.True, "no material → still closes");
            Assert.That(PetRules.ShouldBackOff(99f), Is.True, "rasstOut = 100");
            Assert.That(PetRules.ShouldBackOff(101f), Is.False);
            Assert.That(PetRules.ShouldCoast(99f, true), Is.True, "UnitPet.as:725");
            Assert.That(PetRules.ShouldCoast(99f, false), Is.False, "coasting needs material");
        }

        [Test]
        public void SummonAlpha_FadesInOverThirtyTicks()
        {
            // UnitPet.as:245-256
            Assert.That(PetRules.SummonAlpha(60), Is.EqualTo(0f).Within(0.001f), "> 30 → invisible");
            Assert.That(PetRules.SummonAlpha(30), Is.EqualTo(0f).Within(0.001f), "exactly 30 → 1 - 1 = 0");
            Assert.That(PetRules.SummonAlpha(15), Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(PetRules.SummonAlpha(0), Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void WeaponHoldPoint_PhoenixIsFifteenByTwenty()
        {
            // UnitPet.as:229-238
            PetPoint phoenix = PetRules.WeaponHoldPoint(PetKind.Phoenix, 100f, 100f, facing: 1);
            Assert.That(phoenix.X, Is.EqualTo(115f).Within(0.001f));
            Assert.That(phoenix.Y, Is.EqualTo(80f).Within(0.001f));

            PetPoint owl = PetRules.WeaponHoldPoint(PetKind.Owl, 100f, 100f, facing: -1);
            Assert.That(owl.X, Is.EqualTo(89f).Within(0.001f));
            Assert.That(owl.Y, Is.EqualTo(82f).Within(0.001f));
        }

        // ── taming (UnitPhoenix.as:103-143) ──────────────────────────────────────────────────

        [Test]
        public void TameFeeds_NeedACookie_AndFiveCompletes()
        {
            Assert.That(PetRules.TameFeedsAfterFeed(3, hasCookie: false), Is.EqualTo(3),
                "UnitPhoenix.as:110-137 — no cookie, no progress");
            Assert.That(PetRules.TameFeedsAfterFeed(3, hasCookie: true), Is.EqualTo(4), "UnitPhoenix.as:112-121");

            Assert.That(PetRules.TameCompletes(4, alreadyTamed: false), Is.False, "four is not enough");
            Assert.That(PetRules.TameCompletes(5, alreadyTamed: false), Is.True, "UnitPhoenix.as:122");
            Assert.That(PetRules.TameCompletes(5, alreadyTamed: true), Is.False,
                "UnitPhoenix.as:122 — `&& !triggers[\"pet_phoenix\"]`");
        }

        [Test]
        public void TryParse_RecognisesExactlyTheThreeIds()
        {
            Assert.That(PetIds.TryParse("phoenix", out PetKind phoenix) && phoenix == PetKind.Phoenix, Is.True);
            Assert.That(PetIds.TryParse("owl", out PetKind owl) && owl == PetKind.Owl, Is.True);
            Assert.That(PetIds.TryParse("moon", out PetKind moon) && moon == PetKind.Moon, Is.True);
            Assert.That(PetIds.TryParse("Phoenix", out _), Is.False, "ordinal — the oracle's ids are lowercase");
            Assert.That(PetIds.TryParse("bloat", out _), Is.False);
        }
    }
}
