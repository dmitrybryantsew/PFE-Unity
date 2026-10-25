using NUnit.Framework;
using PFE.Entities.Player;
using PFE.Sim.Radiation;

namespace PFE.Tests.EditMode.Sim.Radiation
{
    /// <summary>
    /// Pins the player's radiation arithmetic to the oracle — <c>UnitPlayer.actions()</c>'s exposure
    /// block (<c>UnitPlayer.as:898-960</c>), <c>heal(_, 2)</c> (<c>:3173-3178</c>),
    /// <c>raddamage()</c> (<c>:3227-3249</c>) and <c>resurect()</c>'s clamp (<c>:3461-3468</c>).
    ///
    /// <para><b>Why this is worth a fixture.</b> Every number here is a product with a clamp on top,
    /// and the failure modes are all silent: a radiation level that accrues 30× too fast, a hp ceiling
    /// that never applies, a geiger that clicks on every frame, or a perk that caps exposure at the
    /// wrong fraction. None of those throw, and all of them are one constant away from correct.</para>
    ///
    /// <para><b>What is not pinned here.</b> The <c>MonoBehaviour</c> that gathers sources
    /// (<c>PlayerRadiationTicker</c>) and the zombie's aura push: both need a scene. They carry no
    /// arithmetic — every rule is in <see cref="RadiationMath"/> and
    /// <see cref="PlayerRadiationState"/>, which is why this file can run offline.</para>
    /// </summary>
    public class RadiationMathTests
    {
        private const float MaxHp = 200f;

        private static RadiationMath.TickInput Input(
            float drad = 0f, float rad = 0f, float hp = MaxHp, float radX = 1f, float radChild = 0f)
        {
            return new RadiationMath.TickInput
            {
                MaxHp = MaxHp,
                Hp = hp,
                Rad = rad,
                Drad = drad,
                Drad2 = 0f,
                RadX = radX,
                RadChild = radChild,
                AmbientRad = 0f,
                AmbientWaterRad = 1f,
                InWater = false,
                NoRad = false,
                Invulnerable = false,
                GodMode = false,
            };
        }

        // ── The tick ─────────────────────────────────────────────────────────────

        [Test]
        public void Tick_AbsorbsDradOverThirtyFrames()
        {
            // :922 — `rad += drad / 30 * radX`. A source of 3 hp/frame is 3 hp of capacity per second,
            // split into 0.1-hp steps: one frame of drad = 3 adds exactly 0.1.
            RadiationMath.TickResult result = RadiationMath.Tick(Input(drad: 3f));

            Assert.That(result.Rad, Is.EqualTo(0.1f).Within(1e-5f));
            Assert.That(result.Drad, Is.EqualTo(0f), ":945 — the accumulator is consumed.");
        }

        [Test]
        public void Tick_RadXScalesAbsorption()
        {
            // `radX` is the multiplier, not a divisor: 0.5 halves the dose, 2 doubles it.
            Assert.That(RadiationMath.Tick(Input(drad: 3f, radX: 0.5f)).Rad, Is.EqualTo(0.05f).Within(1e-5f));
            Assert.That(RadiationMath.Tick(Input(drad: 3f, radX: 2f)).Rad, Is.EqualTo(0.2f).Within(1e-5f));
        }

        [Test]
        public void Tick_RadXZeroAbsorbsNothing_ButTheGeigerStillClicks()
        {
            // :920-933 — the `radX > 0` guard wraps the absorption AND the hp clamp, and stops there.
            // The geiger block (:935-938) is outside it, so a sealed suit still tells you it is hot.
            RadiationMath.TickResult result = RadiationMath.Tick(Input(drad: 3f, radX: 0f));

            Assert.That(result.Rad, Is.EqualTo(0f));
            Assert.That(result.GeigerChance, Is.GreaterThan(0f));
            Assert.That(result.GeigerEligible, Is.True);
        }

        [Test]
        public void Tick_RadXZeroSkipsTheHpClamp_NotJustTheAbsorption()
        {
            // The half the test above cannot see. `rad += drad / 30 * radX` already adds nothing when
            // radX is 0, so asserting only that `rad` stays put proves nothing about the guard — measured:
            // mutating `radX > 0` to `radX >= 0` left all 20 tests green. What the guard actually buys is
            // that an immune player who is ALREADY irradiated is not clawed down to `maxhp - rad` and does
            // not die, because :927-935 (the clamp, the death and setHp) are all inside it.
            RadiationMath.TickResult result = RadiationMath.Tick(Input(drad: 3f, rad: 100f, hp: MaxHp, radX: 0f));

            Assert.That(result.Rad, Is.EqualTo(100f), "Nothing absorbed.");
            Assert.That(result.Hp, Is.EqualTo(MaxHp), "The clamp is inside the gate, so hp is untouched.");
            Assert.That(result.HpDirty, Is.False, ":935 setHp() is inside the gate too.");
            Assert.That(result.Died, Is.False);
        }

        [Test]
        public void Tick_ClampsHpToMaxMinusRad()
        {
            // :927-931 — the ceiling is `maxhp - rad`, so exposure is health you cannot hold.
            RadiationMath.TickResult result = RadiationMath.Tick(Input(drad: 3f, rad: 50f, hp: 200f));

            // 200 - (50 + 0.1), not 200 - 50: the frame's own dose is absorbed first.
            Assert.That(result.Hp, Is.EqualTo(149.9f).Within(1e-4f));
            Assert.That(result.HpDirty, Is.True);
            Assert.That(result.Died, Is.False);
        }

        [Test]
        public void Tick_RadiationCanKill_AndHpGoesNegative()
        {
            // :927-933 — `if(hp > maxhp - rad) { hp = maxhp - rad; if(hp <= 0) this.die(); }`.
            //
            // The assignment has NO max(0): `hp` is a plain `public var hp:Number` (Unit.as:126), not a
            // clamped property, so an exposure that overshoots maxhp leaves hp genuinely negative and
            // the oracle only reacts to it by dying. Asserting 0 here would encode a rule the oracle
            // does not have — the same trap as a docstring inventing a rule. `HpCeiling` is the clamped
            // one, and it clamps because a *heal target* of -0.05 is nonsense; the tick does not.
            RadiationMath.TickResult result = RadiationMath.Tick(Input(drad: 3f, rad: MaxHp - 0.05f, hp: MaxHp));

            Assert.That(result.Rad, Is.EqualTo(MaxHp + 0.05f).Within(1e-4f), "The frame's own dose lands first.");
            Assert.That(result.Hp, Is.EqualTo(-0.05f).Within(1e-4f), "Unclamped, exactly as AS3 assigns it.");
            Assert.That(result.Hp, Is.LessThan(0f));
            Assert.That(result.Died, Is.True);
        }

        [Test]
        public void HpCeiling_ClampsWhereTheTickDoesNot()
        {
            // The deliberate asymmetry, stated as one assertion: for the same overshoot the tick writes
            // a negative hp while the ceiling that healing must respect is floored at 0. `maxhp - rad`
            // is used as a *limit* at :957 and :963, where a negative limit would invert the comparison,
            // but at :929 it is used as an assignment, where the oracle does not clamp.
            float tickHp = RadiationMath.Tick(Input(drad: 3f, rad: MaxHp - 0.05f, hp: MaxHp)).Hp;
            float ceiling = RadiationMath.HpCeiling(MaxHp, MaxHp + 0.05f);

            Assert.That(tickHp, Is.LessThan(0f), "The tick is a raw assignment.");
            Assert.That(ceiling, Is.EqualTo(0f), "The ceiling is a limit, so it floors.");
            Assert.That(tickHp, Is.LessThan(ceiling));
        }

        [Test]
        public void Tick_RadChildCapsExposureAtThePerkFraction()
        {
            // :923-926 — with the perk at 0.2 exposure stops at 80 % of maxhp, i.e. 160.
            RadiationMath.TickResult result = RadiationMath.Tick(
                Input(drad: 300f, rad: 159.9f, hp: MaxHp, radChild: 0.2f));

            Assert.That(result.Rad, Is.EqualTo(160f).Within(1e-4f));
            Assert.That(result.Hp, Is.EqualTo(40f).Within(1e-4f));
        }

        [Test]
        public void Tick_RadChildAbsentDoesNotCapAtMaxHp()
        {
            // The oracle only clamps when `radChild > 0`; without the perk exposure can exceed maxhp
            // (and the hp clamp then kills). Capping at maxhp unconditionally would be an invention.
            RadiationMath.TickResult result = RadiationMath.Tick(Input(drad: 300f, rad: 250f, hp: 0f));

            Assert.That(result.Rad, Is.GreaterThan(MaxHp));
        }

        [Test]
        public void Tick_HitRadiationBleedsInAndDecays()
        {
            // :913-917 — a hit's `drad2` is added, then reduced by 0.1, for as long as it is positive.
            var input = Input();
            input.Drad2 = 6f;

            RadiationMath.TickResult first = RadiationMath.Tick(input);
            Assert.That(first.Rad, Is.EqualTo(6f / 30f).Within(1e-5f));
            Assert.That(first.Drad2, Is.EqualTo(5.9f).Within(1e-5f));

            input.Drad2 = first.Drad2;
            input.Rad = first.Rad;
            RadiationMath.TickResult second = RadiationMath.Tick(input);
            Assert.That(second.Rad, Is.EqualTo((6f + 5.9f) / 30f).Within(1e-5f));
        }

        [Test]
        public void Tick_NoRadSkipsOneFramesAmbient()
        {
            // :901-904 — the spawn/location grace consumes itself on the first frame.
            var input = Input();
            input.NoRad = true;
            input.AmbientRad = 5f;

            Assert.That(RadiationMath.Tick(input).Rad, Is.EqualTo(0f));
        }

        [Test]
        public void Tick_WaterRadOnlyWhileSubmerged()
        {
            // :906-910 — `if(inWater) drad += loc.wrad`.
            var dry = Input();
            dry.AmbientWaterRad = 5f;
            dry.InWater = false;

            var wet = Input();
            wet.AmbientWaterRad = 5f;
            wet.InWater = true;

            Assert.That(RadiationMath.Tick(dry).Rad, Is.EqualTo(0f));
            Assert.That(RadiationMath.Tick(wet).Rad, Is.EqualTo(5f / 30f).Within(1e-5f));
        }

        [Test]
        public void Tick_IdleParksTheSentinel_AndReportsTheBarDirtyOnce()
        {
            // :946-951 — nothing added, drad == 0: refresh the bar once, then park at -0.0001 so the
            // branch cannot run again. The sentinel must be negative and non-zero.
            RadiationMath.TickResult result = RadiationMath.Tick(Input());

            Assert.That(result.HpDirty, Is.True);
            Assert.That(result.Drad, Is.EqualTo(-0.0001f).Within(1e-9f));

            var again = Input();
            again.Drad = result.Drad;
            RadiationMath.TickResult second = RadiationMath.Tick(again);
            Assert.That(second.HpDirty, Is.False);
            Assert.That(second.Drad, Is.EqualTo(-0.0001f).Within(1e-9f));
        }

        [Test]
        public void Tick_InvulnerableAndGodModeStopAbsorption()
        {
            var invulnerable = Input(drad: 3f);
            invulnerable.Invulnerable = true;
            var god = Input(drad: 3f);
            god.GodMode = true;

            Assert.That(RadiationMath.Tick(invulnerable).Rad, Is.EqualTo(0f));
            Assert.That(RadiationMath.Tick(god).Rad, Is.EqualTo(0f));
        }

        [Test]
        public void Tick_InvulnerableAndGodModeAlsoSkipTheHpClamp()
        {
            // `!invulner && !World.w.godMode` sits in the same :920 guard as `radX > 0`, so the same
            // asymmetry applies: asserting only that no radiation was absorbed cannot distinguish the
            // gate from the multiplication. An invulnerable player holding radiation keeps their health.
            var invulnerable = Input(drad: 3f, rad: 100f, hp: MaxHp);
            invulnerable.Invulnerable = true;
            var god = Input(drad: 3f, rad: 100f, hp: MaxHp);
            god.GodMode = true;

            RadiationMath.TickResult a = RadiationMath.Tick(invulnerable);
            RadiationMath.TickResult b = RadiationMath.Tick(god);

            Assert.That(a.Hp, Is.EqualTo(MaxHp));
            Assert.That(b.Hp, Is.EqualTo(MaxHp));
            Assert.That(a.Died || b.Died, Is.False);
        }

        // ── The source geometry ──────────────────────────────────────────────────

        [Test]
        public void Falloff_IsLinearFromOneAtTheCentreToZeroAtTheRim()
        {
            Assert.That(RadiationMath.Falloff(0f, 250f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(RadiationMath.Falloff(125f, 250f), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(RadiationMath.Falloff(250f, 250f), Is.EqualTo(0f));
            Assert.That(RadiationMath.Falloff(400f, 250f), Is.EqualTo(0f));
        }

        [Test]
        public void ElementalShare_IsACliffNotARamp()
        {
            // :3239-3246 — outside the inner quarter the share is `normalised * 4`, inside it the share
            // is full strength. A linear ramp here would make a gas cloud's inner 75 % far weaker.
            Assert.That(RadiationMath.ElementalShare(0.1f), Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(RadiationMath.ElementalShare(0.2f), Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(RadiationMath.ElementalShare(0.25f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(RadiationMath.ElementalShare(0.9f), Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void InRadius_IsStrictAtTheRim()
        {
            Assert.That(RadiationMath.InRadius(0f, 0f, 250f), Is.True);
            Assert.That(RadiationMath.InRadius(249f, 0f, 250f), Is.True);
            Assert.That(RadiationMath.InRadius(250f, 0f, 250f), Is.False);
            Assert.That(RadiationMath.InRadius(0f, 0f, 0f), Is.False);
        }

        [Test]
        public void VerticalOffset_SamplesTheChestNotTheFeet()
        {
            // `Obj.as:205` — `gg.Y - gg.scY * 0.75 - obj.Y + obj.scY / 2`. A 70-px player whose chest
            // is 52.5 px up, measured against a 50-px object's centre 25 px up, is 77.5 px above it.
            float dy = RadiationMath.VerticalOffset(playerFeetY: 0f, playerHeight: 70f, objectCentreY: 25f);

            Assert.That(dy, Is.EqualTo(-77.5f).Within(1e-4f));
        }

        // ── Cures and clamps ─────────────────────────────────────────────────────

        [Test]
        public void RemoveRad_FloorsAtZeroAndReportsWhatItTook()
        {
            Assert.That(RadiationMath.RadAfterRemoval(10f, 4f), Is.EqualTo(6f).Within(1e-5f));
            Assert.That(RadiationMath.RadAfterRemoval(3f, 80f), Is.EqualTo(0f));
        }

        [Test]
        public void Resurrect_LeavesFortyHpOfHeadroom()
        {
            // :3461-3467 — `if(rad > maxhp - 40) rad = maxhp - 40; if(rad < 0) rad = 0;`
            Assert.That(RadiationMath.RadAfterResurrect(195f, MaxHp), Is.EqualTo(160f).Within(1e-5f));
            Assert.That(RadiationMath.RadAfterResurrect(20f, MaxHp), Is.EqualTo(20f).Within(1e-5f));
            Assert.That(RadiationMath.RadAfterResurrect(-5f, MaxHp), Is.EqualTo(0f));
        }

        [Test]
        public void HpCeiling_IsMaxMinusRadAndNeverNegative()
        {
            Assert.That(RadiationMath.HpCeiling(200f, 50f), Is.EqualTo(150f).Within(1e-5f));
            Assert.That(RadiationMath.HpCeiling(200f, 250f), Is.EqualTo(0f));
        }
    }

    /// <summary>
    /// Pins the state holder's frame bookkeeping — the parts that are not arithmetic but are still
    /// rules: the hit assignment, the 30-frame elemental payout, and the two grace windows.
    /// </summary>
    public class PlayerRadiationStateTests
    {
        private static PlayerRadiationState TickOnce(PlayerRadiationState state, float drad = 0f)
        {
            state.Tick(200f, 200f, 1f, 0f, drad, 1f, false, false, false);
            return state;
        }

        [Test]
        public void AddHitRadiation_AssignsRatherThanAdds()
        {
            // `UnitPlayer.as:3220` — `this.drad2 = param1.radDamage`. Two radiating hits in one frame
            // keep the last, not the sum.
            var state = new PlayerRadiationState();
            state.AddHitRadiation(6f);
            state.AddHitRadiation(3f);

            Assert.That(state.Drad2, Is.EqualTo(3f).Within(1e-5f));
        }

        [Test]
        public void SourcesAreIgnoredDuringTheNoGasWindow()
        {
            // `t_nogas` is set to 90 on a location change (:654) and blocks `raddamage` outright.
            var state = new PlayerRadiationState();
            state.OnLocationChanged();

            state.AddSourceExposure(1f, 10f);
            Assert.That(state.Drad, Is.EqualTo(0f), "A source inside the grace window contributes nothing.");

            for (int i = 0; i < 90; i++) state.TickGrace();
            state.AddSourceExposure(1f, 10f);
            Assert.That(state.Drad, Is.EqualTo(10f).Within(1e-5f), "After 90 frames the source lands again.");
        }

        [Test]
        public void ElementalPayout_IsEveryThirtyFramesAndDividedByThirty()
        {
            // :1529-1551 — `++t_raddam; if(t_raddam > 30) { damage(ddam / 30, type); ddam = 0; }`
            var state = new PlayerRadiationState();

            for (int frame = 0; frame < 30; frame++)
            {
                state.AddSourceElemental(1f, 5f, tip: 1);
                state.TickElemental(out float venom, out _, out _);
                Assert.That(venom, Is.EqualTo(0f), $"No payout before frame 30 (frame {frame}).");
            }

            // The 31st call trips the counter, exactly as `> 30` does.
            state.AddSourceElemental(1f, 5f, tip: 1);
            state.TickElemental(out float paid, out _, out _);
            Assert.That(paid, Is.EqualTo(31 * 5f / 30f).Within(1e-4f));
        }

        [Test]
        public void ElementalPayout_IgnoresTipsOutsideOneToThree()
        {
            // `this["ddam" + param3] == null` drops anything else.
            var state = new PlayerRadiationState();
            state.AddSourceElemental(1f, 10f, tip: 7);

            float venom = 0f, pink = 0f, necrotic = 0f;
            for (int i = 0; i < 31; i++) state.TickElemental(out venom, out pink, out necrotic);

            Assert.That(venom + pink + necrotic, Is.EqualTo(0f));
        }

        [Test]
        public void RemoveRad_ReportsTheAmountActuallyTaken()
        {
            var state = new PlayerRadiationState();
            TickPastGrace(state, ambientRad: 30f);   // +1 rad

            Assert.That(state.Rad, Is.EqualTo(1f).Within(1e-5f), "The setup must actually dose the player.");
            Assert.That(state.RemoveRad(0.25f), Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(state.Rad, Is.EqualTo(0.75f).Within(1e-5f));
            Assert.That(state.RemoveRad(50f), Is.EqualTo(0.75f).Within(1e-5f));
            Assert.That(state.Rad, Is.EqualTo(0f));
        }

        [Test]
        public void Resurrect_ClampsAndReArmsTheGrace()
        {
            var state = new PlayerRadiationState();
            TickPastGrace(state, ambientRad: 6000f);   // +200 rad
            Assert.That(state.Rad, Is.EqualTo(200f).Within(1e-4f), "The setup must actually dose the player.");

            state.OnResurrect(200f);

            Assert.That(state.Rad, Is.EqualTo(160f).Within(1e-4f));
            Assert.That(state.NoGasFrames, Is.EqualTo(250), ":3481 — `t_nogas = 250`.");
            Assert.That(state.Drad2, Is.EqualTo(0f), ":3485 — `drad2 = 0`.");
        }

        [Test]
        public void Clear_ZeroesExposureAndReArmsSpawnGrace()
        {
            var state = new PlayerRadiationState();
            TickPastGrace(state, ambientRad: 300f);
            Assert.That(state.Rad, Is.EqualTo(10f).Within(1e-4f), "The setup must actually dose the player.");

            state.Clear();

            Assert.That(state.Rad, Is.EqualTo(0f));
            Assert.That(state.NoRad, Is.True, "UnitPlayer.as:101 — a fresh player skips their first frame.");
            Assert.That(state.NoGasFrames, Is.EqualTo(90));
        }

        /// <summary>
        /// Runs one frame that is actually billed. A fresh state has <c>noRad = true</c>
        /// (<c>UnitPlayer.as:101</c>), and <c>:901-904</c> spends that flag on the player's first frame
        /// without adding any ambient exposure — so a test that calls <see cref="PlayerRadiationState.Tick"/>
        /// once expecting a dose gets zero and reports it as a bug in the state.
        /// </summary>
        private static RadiationMath.TickResult TickPastGrace(PlayerRadiationState state, float ambientRad)
        {
            state.Tick(200f, 200f, 1f, 0f, 0f, 0f, false, false, false);   // spends the spawn grace
            return state.Tick(200f, 200f, 1f, 0f, ambientRad, 1f, false, false, false);
        }
    }
}
