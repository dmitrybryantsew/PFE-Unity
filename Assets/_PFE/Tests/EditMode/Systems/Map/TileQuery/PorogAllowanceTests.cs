using NUnit.Framework;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;

namespace PFE.Tests.EditMode.Systems.Map.TileQuery
{
    /// <summary>
    /// The porog (step-up allowance) family. Worth pinning because **every one of these values already
    /// existed and was correct** — the defect was that the *player* was given the *unit's* value, so a
    /// test asserting "the constants are unchanged" would have passed throughout it. The tests that
    /// matter are the ones asserting the two sets are <i>different</i> — see
    /// <see cref="PlayerGrounded_IsNotTheUnitsAllowance"/>.
    ///
    /// <para><b>What `porog` actually means</b>, because it is easy to invert: it is not "how far a
    /// mover may be lifted", it is <b>how far above the surface a shelf still counts</b>. AS3 seats a
    /// mover while <c>Y2 - phY1 &lt;= porog</c> (<c>Unit.as:2131</c>, <c>:2208</c>) and stops colliding
    /// with the shelf beyond it (<c>Y2 - porog &gt; phY1</c>, <c>Unit.as:2578</c>). A larger allowance
    /// therefore keeps a shelf solid — and seats a mover — from further above. These tests are about
    /// <i>fidelity</i>; they are not evidence about the direction of the "standing on air" gap.
    /// See <c>docs/Research/SHELF_CATWALK_DIAGNOSIS_2026-09-30.md</c> §8.</para>
    /// </summary>
    [TestFixture]
    public sealed class PorogAllowanceTests
    {
        // ── The generic unit's values: AS3 Unit.as:278-280 ───────────────────

        [Test]
        public void UnitGrounded_IsTen()
        {
            Assert.AreEqual(10f, TileQueryConstants.PorogGrounded,
                "AS3 Unit.as:278 — `public var porog:Number = 10`.");
        }

        [Test]
        public void UnitAirborne_IsFour()
        {
            Assert.AreEqual(4f, TileQueryConstants.PorogAirborne,
                "AS3 Unit.as:279 — `public var porog_jump:Number = 4`.");
        }

        // ── The player's values: AS3 UnitPlayer.as ───────────────────────────

        [Test]
        public void PlayerGrounded_IsZero()
        {
            // UnitPlayer.as:2530-2531 resets BOTH of the player's allowances to 0 every frame, before
            // any input is considered. The non-zero cases are opt-in from there.
            Assert.AreEqual(0f, TileQueryConstants.PlayerPorogGrounded,
                "AS3 UnitPlayer.as:2530 — `porog = 0`.");
        }

        [Test]
        public void PlayerJump_IsTen()
        {
            // UnitPlayer.as:2532-2535 — only `if((isRun || ctr.keyBeUp) && jumpNumb == 0)`.
            Assert.AreEqual(10f, TileQueryConstants.PlayerPorogJump,
                "AS3 UnitPlayer.as:2533 — `porog_jump = 10` while running or holding Up.");
        }

        [Test]
        public void PlayerWalk_IsTwenty()
        {
            // UnitPlayer.as:2550 and :2569 — set inside both walk branches, alongside `isTake = 40`.
            Assert.AreEqual(20f, TileQueryConstants.PlayerPorogWalk,
                "AS3 UnitPlayer.as:2550 — `porog = 20` while a walk key is held.");
        }

        // ── The relationships: these are what actually guard the defect ──────

        [Test]
        public void PlayerGrounded_IsNotTheUnitsAllowance()
        {
            // The whole defect was the player being handed PorogGrounded (10) instead of
            // PlayerPorogGrounded (0). If someone "simplifies" the two by aliasing them, this goes red —
            // and a constants-only test would not.
            //
            // Fidelity only. Do NOT read this as the cause of the "standing on air" symptom: porog is
            // how far ABOVE the surface a shelf still counts (Unit.as:2131 places, :2578 blocks), so
            // the port's larger 10 is the *more* seating value. See
            // docs/Research/SHELF_CATWALK_DIAGNOSIS_2026-09-30.md §8.
            Assert.AreNotEqual(TileQueryConstants.PorogGrounded, TileQueryConstants.PlayerPorogGrounded,
                "A grounded player's allowance is 0, NOT the generic unit's 10 (AS3 UnitPlayer.as:2530 " +
                "vs Unit.as:278). This is a fidelity requirement, not the explanation of the " +
                "floating-player symptom.");
        }

        [Test]
        public void StandingPlayer_IsStricterThanAnyUnit()
        {
            // The player is the strictest mover while standing, and that is deliberate in AS3.
            // Note the direction of the effect, because it is easy to get backwards: a larger porog
            // keeps a shelf solid, and seats a mover, from further ABOVE the surface (Unit.as:2131,
            // :2578). So "the port is too permissive for the player" is a fidelity statement about
            // which constant is used — it is NOT a claim about which way the gap moves.
            Assert.Less(TileQueryConstants.PlayerPorogGrounded, TileQueryConstants.PorogGrounded,
                "The standing player's allowance is the strictest of the two (0 vs 10).");
        }

        [Test]
        public void WalkingPlayer_IsLooserThanAUnit()
        {
            // The asymmetry is deliberate and worth pinning, because the naive "fix" for D6 is to set
            // the player to a flat 0 everywhere — which would stop the player climbing stairs. AS3
            // gives the WALKING player 20, twice the unit's 10.
            Assert.Greater(TileQueryConstants.PlayerPorogWalk, TileQueryConstants.PorogGrounded,
                "A walking player steps up 20 px, deliberately more than a unit's 10 (UnitPlayer.as:2550).");

            Assert.Greater(TileQueryConstants.PlayerPorogWalk, TileQueryConstants.PlayerPorogGrounded,
                "Walking must be the permissive case for the player, standing the strict one.");
        }

        [Test]
        public void PlayerAirborne_IsNotTheUnitsAirborneAllowance()
        {
            // The port's airborne branch has the same shape of hazard: the player must not silently
            // inherit the unit's 4 px.
            Assert.AreNotEqual(TileQueryConstants.PorogAirborne, TileQueryConstants.PlayerPorogJump,
                "A player's airborne allowance is 0 unless running/Up (10) — never the unit's 4.");
        }

        [Test]
        public void EveryAllowance_IsNonNegativeAndAtMostHalfATile()
        {
            // Sanity bound. A porog above half a tile would let a mover climb a step taller than the
            // tile it stands on, which AS3 never does; a negative one would be meaningless.
            float halfTile = WorldConstants.TILE_SIZE * 0.5f;

            float[] all =
            {
                TileQueryConstants.PorogGrounded,
                TileQueryConstants.PorogAirborne,
                TileQueryConstants.PlayerPorogGrounded,
                TileQueryConstants.PlayerPorogJump,
                TileQueryConstants.PlayerPorogWalk,
            };

            foreach (float porog in all)
            {
                Assert.GreaterOrEqual(porog, 0f, "A step-up allowance cannot be negative.");
                Assert.LessOrEqual(porog, halfTile,
                    $"{porog} px exceeds half a {WorldConstants.TILE_SIZE} px tile.");
            }
        }
    }
}
