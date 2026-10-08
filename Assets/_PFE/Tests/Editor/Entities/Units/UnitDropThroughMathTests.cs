using NUnit.Framework;
using PFE.Entities.Units;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins <see cref="UnitDropThroughMath"/> — AS3 <c>Unit.throu</c>'s zombie trigger, the "the target is
    /// below me, so the catwalk is not a floor" band.
    ///
    /// <para><b>Why this fixture exists.</b> The whole mechanic is one comparison against one threshold,
    /// and every way to get it wrong is silent. A <c>&gt;</c> written as <c>&gt;=</c> moves the trigger by
    /// a single pixel and is invisible in play; the sign is worse, because the two coordinate systems run
    /// opposite ways — AS3's Y is down-positive and Unity's is up-positive — so a flipped subtraction turns
    /// "the target is below me" into "the target is above me" and the zombie either drops through the floor
    /// it is standing on or never drops at all. Neither version throws, and neither is visible in a single
    /// play-through, which is why the arithmetic is a pure function with a fixture rather than four lines
    /// inside a <c>MonoBehaviour</c>.</para>
    ///
    /// <para><b>What is deliberately NOT pinned here.</b> The caller's wiring — that a zombie in
    /// <c>Alert</c> or <c>CombatChase</c> actually asks for the drop, that the flag is cleared when it
    /// should be, and that the unit then leaves the platform — is behaviour, not arithmetic, and it needs a
    /// <c>GameObject</c> and a room. That half lives in <c>EnemyBrainTests</c> and is flagged there as
    /// owner-only.</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitDropThroughMathTests
    {
        // ── The thresholds themselves ─────────────────────────────────────────────────────────

        /// <summary>
        /// <c>if(celDY &gt; 80)</c> — <c>UnitZombie.as:859</c>.
        /// </summary>
        [Test]
        public void DropTriggerPixels_IsEighty()
        {
            Assert.That(UnitDropThroughMath.DropTriggerPixels, Is.EqualTo(80f),
                "UnitZombie.as:859 — `if(celDY > 80) { throu = true; }`.");

            Assert.That(UnitDropThroughMath.DropTriggerPixels, Is.Not.EqualTo(70f),
                "70 is UnitZombie.as:692's `porog = 0` band, which is a different test in the same branch. " +
                "They are two numbers, not one, and collapsing them would move the drop trigger 10 px.");

            Assert.That(UnitDropThroughMath.DropTriggerPixels, Is.Not.EqualTo(40f),
                "40 is UnitZombie.as:679's `aiVNapr` band — the JUMP half of the same vertical reaction " +
                "(see docs/OnEnemiesAndAi/20). Sharing a constant would tie the drop to the jump.");

            Assert.That(UnitDropThroughMath.DropTriggerPixels, Is.Not.EqualTo(200f),
                "200 is UnitZombie.optDistAtt (:79), the contact-attack half-width. Unrelated axis.");
        }

        /// <summary>
        /// <c>if(Y &gt; loc.spaceY * Tile.tileY - 80)</c> — <c>UnitZombie.as:934-937</c>.
        /// </summary>
        /// <remarks>
        /// The guard and the trigger are both 80 in the oracle, which is a coincidence of the source and
        /// not a shared rule — hence a separate constant. Pinning them as two fields means a future
        /// correction to one cannot silently move the other.
        /// </remarks>
        [Test]
        public void RoomFloorGuardPixels_IsEightyAndIsItsOwnField()
        {
            Assert.That(UnitDropThroughMath.RoomFloorGuardPixels, Is.EqualTo(80f),
                "UnitZombie.as:934 — `if(Y > loc.spaceY * Tile.tileY - 80) { throu = false; }`.");
        }

        // ── ShouldDropThrough: the boundary and the sign ─────────────────────────────────────

        /// <summary>
        /// The comparison is <b>strict</b>. A target whose centre sits exactly on the 80 px line does not
        /// trigger the drop — <c>&gt;</c>, not <c>&gt;=</c>.
        /// </summary>
        [Test]
        public void ShouldDropThrough_IsStrictlyGreater()
        {
            // unit top 200, target centre 120 → exactly 80 px below.
            Assert.That(UnitDropThroughMath.ShouldDropThrough(200f, 120f), Is.False,
                "Exactly 80 px is NOT past `celDY > 80`. Changing this to `>=` is the mutation this " +
                "assertion exists to catch.");

            Assert.That(UnitDropThroughMath.ShouldDropThrough(200f, 119.9f), Is.True,
                "A hair past 80 px IS past it.");

            Assert.That(UnitDropThroughMath.ShouldDropThrough(200f, 120.1f), Is.False,
                "A hair short of 80 px is not.");
        }

        /// <summary>
        /// The band sweeps monotonically across the threshold, and every case is listed rather than sampled,
        /// so growing the set cannot leave a stale hand-written expectation behind.
        /// </summary>
        [Test]
        public void ShouldDropThrough_SweepsTheBand()
        {
            // unit top fixed at 200; the gap is (200 - targetCentre).
            (float targetCentre, float gap, bool expected)[] cases =
            {
                (0f,   200f, true),
                (60f,  140f, true),
                (80f,  120f, true),
                (110f,  90f, true),
                (119f,  81f, true),
                (120f,  80f, false), // the boundary
                (121f,  79f, false),
                (150f,  50f, false),
                (200f,   0f, false), // level with the unit's top
            };

            foreach ((float targetCentre, float gap, bool expected) in cases)
            {
                Assert.That(
                    UnitDropThroughMath.ShouldDropThrough(200f, targetCentre),
                    Is.EqualTo(expected),
                    $"unit top 200, target centre {targetCentre} (gap {gap} px) must be {expected}.");
            }
        }

        /// <summary>
        /// The sign, stated as its own test because it is the failure the two coordinate systems invite.
        ///
        /// <para>AS3's <c>celDY</c> is down-positive, so "more than 80" means <i>below</i>. Unity's Y is
        /// up-positive, so the same condition is <c>unitTop - targetCentre</c> and the subtraction must be
        /// in that order. Swap it and a zombie with the target <i>above</i> it drops through the floor.</para>
        /// </summary>
        [Test]
        public void ShouldDropThrough_TargetAboveIsNeverADrop()
        {
            Assert.That(UnitDropThroughMath.ShouldDropThrough(100f, 500f), Is.False,
                "Target 400 px ABOVE the unit's top. AS3's `celDY` would be -400; only +80 triggers.");

            Assert.That(UnitDropThroughMath.ShouldDropThrough(100f, 100f), Is.False,
                "Target centre level with the unit's top — gap 0.");

            Assert.That(UnitDropThroughMath.ShouldDropThrough(100f, 19f), Is.True,
                "Target 81 px BELOW the unit's top — the only direction that triggers.");
        }

        /// <summary>
        /// The two arguments are not interchangeable, and this is the assertion that says so.
        /// </summary>
        /// <remarks>
        /// A symmetric implementation — <c>Mathf.Abs(unitTop - targetCentre) &gt; 80</c> — passes every test
        /// above except this one, and is a plausible thing to write while "tidying" the subtraction. It
        /// would make a zombie drop through a catwalk to reach a target standing on a roof.
        /// </remarks>
        [Test]
        public void ShouldDropThrough_IsNotSymmetric()
        {
            Assert.That(UnitDropThroughMath.ShouldDropThrough(500f, 100f), Is.True,
                "Unit top 500, target centre 100 → target is 400 px below → drop.");

            Assert.That(UnitDropThroughMath.ShouldDropThrough(100f, 500f), Is.False,
                "The same two numbers the other way round → target is 400 px above → do NOT drop. " +
                "A Mathf.Abs() version would return true for both.");
        }

        // ── IsNearRoomFloor ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// The guard is also strict: a unit exactly 80 px above the room's floor is still allowed to drop.
        /// </summary>
        [Test]
        public void IsNearRoomFloor_IsStrictlyLess()
        {
            // room floor at 0; the unit's feet are 80 px above it.
            Assert.That(UnitDropThroughMath.IsNearRoomFloor(80f, 0f), Is.False,
                "Exactly 80 px up is NOT `Y > floor - 80`. The oracle's guard is strict, like the trigger.");

            Assert.That(UnitDropThroughMath.IsNearRoomFloor(79.9f, 0f), Is.True,
                "A hair inside 80 px IS inside.");

            Assert.That(UnitDropThroughMath.IsNearRoomFloor(80.1f, 0f), Is.False,
                "A hair outside is outside.");
        }

        /// <summary>
        /// The guard is measured from the room's floor edge, which is a world pixel offset — so it must
        /// track the room origin rather than assume it is zero.
        /// </summary>
        /// <remarks>
        /// This is the same trap <c>RoomChainGeometryTests</c> covers for derived geometry: a rule written
        /// against room-local pixels looks correct in every fixture built on the room at land (0,0) and
        /// displaces every other room.
        /// </remarks>
        [Test]
        public void IsNearRoomFloor_TracksTheRoomOrigin()
        {
            const float roomFloorWorldPx = 4000f; // a room that is not at the origin

            Assert.That(UnitDropThroughMath.IsNearRoomFloor(roomFloorWorldPx + 40f, roomFloorWorldPx), Is.True,
                "40 px above this room's floor → inside the guard.");

            Assert.That(UnitDropThroughMath.IsNearRoomFloor(roomFloorWorldPx + 400f, roomFloorWorldPx), Is.False,
                "400 px above it → free to drop.");

            Assert.That(UnitDropThroughMath.IsNearRoomFloor(40f, 0f), Is.True,
                "And the same question with an origin room gives the same answer — the offset cancels, " +
                "which is what makes the world-pixel form safe.");
        }

        // ── The two rules compose the way the call site needs ─────────────────────────────────

        /// <summary>
        /// The call site's predicate: drop only when the target is far enough below <b>and</b> the unit is
        /// not already near the room's floor.
        /// </summary>
        /// <remarks>
        /// The interaction is the interesting part and neither function shows it alone: a zombie standing
        /// on the lowest catwalk with the player on the floor beneath has the target below it, so the
        /// trigger is true — and the guard is what stops it walking out of the room. Without the guard the
        /// drop trigger would be satisfied all the way to the floor and the zombie would sink through it.
        /// </remarks>
        [Test]
        public void TriggerAndGuard_ComposeToTheCallSitesPredicate()
        {
            const float roomFloorWorldPx = 1000f;

            // Zombie high up on a catwalk, player far below on the floor: drop.
            bool wantsDrop = UnitDropThroughMath.ShouldDropThrough(1200f, 1050f)
                && !UnitDropThroughMath.IsNearRoomFloor(1150f, roomFloorWorldPx);
            Assert.That(wantsDrop, Is.True, "High catwalk, target on the floor 150 px below → drop.");

            // Zombie on the lowest catwalk, only 40 px above the room's floor: the guard refuses.
            wantsDrop = UnitDropThroughMath.ShouldDropThrough(1040f, 900f)
                && !UnitDropThroughMath.IsNearRoomFloor(1010f, roomFloorWorldPx);
            Assert.That(wantsDrop, Is.False,
                "The trigger says yes (target 140 px below) but the unit is 10 px off the room's floor, " +
                "so the guard must win — otherwise it walks itself out of the room.");

            // Target only slightly below: no drop, catwalk stays a floor.
            wantsDrop = UnitDropThroughMath.ShouldDropThrough(1200f, 1150f)
                && !UnitDropThroughMath.IsNearRoomFloor(1150f, roomFloorWorldPx);
            Assert.That(wantsDrop, Is.False,
                "Target 50 px below is inside the 80 px band → stay on the catwalk and keep chasing.");
        }
    }
}
