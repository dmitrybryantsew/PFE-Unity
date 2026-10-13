using NUnit.Framework;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Tests for the durability of an authored door / barricade box —
    /// <see cref="DoorDestructionMath"/>, the port of AS3 <c>Box.hp</c> / <c>Box.thre</c> as a weapon
    /// applies them through <c>Tile.udar()</c>.
    ///
    /// <para><b>Why this is a fixture and not just a comment on the presenter.</b> "Doors cannot be
    /// destroyed by weapons" had no test anywhere, and the numbers that decide it are shipped data:
    /// <c>door1</c> is <c>hp='60' thre='10'</c>, <c>door1b</c> is <c>hp='400' thre='50'</c>, and
    /// <c>platform1</c> is <c>hp='5000' thre='800'</c> (<c>AllData.as:4858-4876</c>). A rule that
    /// ignored <c>thre</c>, or that treated <c>thre == damage</c> as "ignored", would still look
    /// plausible in a play-test — a pistol would simply "not quite" break the steel door. Pinning the
    /// arithmetic is what makes the difference visible without the editor.</para>
    /// </summary>
    [TestFixture]
    public class DoorDestructionMathTests
    {
        // ── The shipped rows, as the importer writes them ────────────────────────────────────────
        //
        // MapObjectDefinitionImporter maps hp -> hitPoints and thre/porog/tr -> threshold, defaulting
        // each to 0 (MapObjectDefinitionImporter.cs:155-156).

        private const float Door1Hp = 60f;      // AllData.as:4864  door1      hp='60'   thre='10'
        private const float Door1Thre = 10f;
        private const float Door1BHp = 400f;    // AllData.as:4866  door1b     hp='400'  thre='50'
        private const float Door1BThre = 50f;
        private const float Platform1Hp = 5000f; // AllData.as:4861 platform1 hp='5000' thre='800'
        private const float Platform1Thre = 800f;
        private const float SeptumHp = 30f;      // AllData.as:4858  septum     hp='30'   no thre
        private const float Window1Hp = 10f;     // AllData.as:4862  window1    hp='10'   no thre

        // ── ResolveHitPoints / ResolveThreshold ──────────────────────────────────────────────────

        [Test]
        public void AuthoredHitPointsAreKept()
        {
            Assert.AreEqual(Door1Hp, DoorDestructionMath.ResolveHitPoints(Door1Hp, false));
            Assert.AreEqual(SeptumHp, DoorDestructionMath.ResolveHitPoints(SeptumHp, false));
        }

        [Test]
        public void AnAbsentHitPointsFallsBackToBoxsOwnDefault()
        {
            // The importer writes 0 for a definition row that authors no `hp`, so 0 is what "absent"
            // looks like on disk. AS3's Box initialises hp to 100 (Box.as:56) and only overwrites it
            // under a `.length()` guard, so 100 is the oracle's answer for that row.
            Assert.AreEqual(DoorDestructionMath.DefaultHitPoints,
                DoorDestructionMath.ResolveHitPoints(0f, false));
        }

        [Test]
        public void IndestructibleOverridesBothHitPointsAndThreshold()
        {
            // Box.as:285-289 — a placed `indestruct` assigns hp = 10000 AND thre = 10000, whatever the
            // definition authored.
            Assert.AreEqual(DoorDestructionMath.IndestructibleValue,
                DoorDestructionMath.ResolveHitPoints(Door1Hp, true));
            Assert.AreEqual(DoorDestructionMath.IndestructibleValue,
                DoorDestructionMath.ResolveThreshold(Door1Thre, true));
        }

        [Test]
        public void AnAuthoredThresholdIsKeptAndAbsenceIsZero()
        {
            Assert.AreEqual(Door1BThre, DoorDestructionMath.ResolveThreshold(Door1BThre, false));
            Assert.AreEqual(DoorDestructionMath.DefaultThreshold,
                DoorDestructionMath.ResolveThreshold(0f, false));
        }

        [Test]
        public void ANegativeAuthoredThresholdIsClampedToZero()
        {
            // A negative `thre` can only be a typo, and the safe reading is 0 ("no floor"), not a
            // threshold that *heals* the door — the same direction Box.thre's own default points.
            Assert.AreEqual(0f, DoorDestructionMath.ResolveThreshold(-5f, false));
        }

        // ── IgnoresDamage — AS3's `thre > param1` ───────────────────────────────────────────────

        [Test]
        public void AHitBelowTheThresholdIsIgnoredEntirely()
        {
            // AS3 has no chip damage in this path: `if (indestruct || thre > param1) return false`.
            Assert.IsTrue(DoorDestructionMath.IgnoresDamage(Door1BThre, 49f));
            Assert.IsTrue(DoorDestructionMath.IgnoresDamage(Platform1Thre, 799f));
        }

        [Test]
        public void AHitEqualToTheThresholdIsNotIgnored()
        {
            // The guard is `>`, not `>=`. This is the corner the shipped door2 sits on (thre='100'):
            // a weapon carrying exactly 100 must bite.
            Assert.IsFalse(DoorDestructionMath.IgnoresDamage(Door1BThre, Door1BThre));
            Assert.IsFalse(DoorDestructionMath.IgnoresDamage(100f, 100f));
        }

        [Test]
        public void AZeroThresholdNeverIgnoresAnything()
        {
            // septum (hp='30', no thre) and window1 (hp='10', no thre) must be breakable by any
            // weapon that carries any destroy value at all — including one that carries 0, which
            // then subtracts nothing.
            Assert.IsFalse(DoorDestructionMath.IgnoresDamage(0f, 0f));
            Assert.IsFalse(DoorDestructionMath.IgnoresDamage(0f, 1f));
        }

        // ── ApplyDamage / IsDestroyed ───────────────────────────────────────────────────────────

        [Test]
        public void DamageIsSubtractedInFull()
        {
            Assert.AreEqual(40f, DoorDestructionMath.ApplyDamage(Door1Hp, 20f));
        }

        [Test]
        public void ZeroOrNegativeHitPointsIsDestroyed()
        {
            Assert.IsTrue(DoorDestructionMath.IsDestroyed(0f));
            Assert.IsTrue(DoorDestructionMath.IsDestroyed(-1f));
            Assert.IsFalse(DoorDestructionMath.IsDestroyed(0.5f));
        }

        // ── The whole rule, run over a sequence of hits ──────────────────────────────────────────

        /// <summary>
        /// Fires <paramref name="shots"/> rounds of <paramref name="destroy"/> at a door and returns
        /// how many it took to wreck it, or <c>-1</c> if it survived them all. Mirrors the presenter's
        /// <c>DamageDoor</c> ordering: ignore-check, subtract, then the <c>hp &lt;= 0</c> test.
        /// </summary>
        private static int ShotsToDestroy(float authoredHp, float authoredThre, float destroy, int shots)
        {
            float hp = DoorDestructionMath.ResolveHitPoints(authoredHp, false);
            float thre = DoorDestructionMath.ResolveThreshold(authoredThre, false);

            for (int i = 1; i <= shots; i++)
            {
                if (DoorDestructionMath.IgnoresDamage(thre, destroy)) return -1;

                hp = DoorDestructionMath.ApplyDamage(hp, destroy);
                if (DoorDestructionMath.IsDestroyed(hp)) return i;
            }

            return -1;
        }

        [Test]
        public void AWoodenDoorFallsToThreePistolRounds()
        {
            // door1: 60 hp, thre 10. Three rounds of 20 -> 60 -> 40 -> 20 -> 0.
            Assert.AreEqual(3, ShotsToDestroy(Door1Hp, Door1Thre, 20f, 10));
        }

        [Test]
        public void AWeakWeaponCannotWearDownAReinforcedDoor()
        {
            // door1b: 400 hp, thre 50. Rounds of 40 are ignored outright, however many are fired —
            // the reported "shooting the door does nothing" is *this* door and *that* weapon, and the
            // oracle's answer really is "nothing happens".
            Assert.AreEqual(-1, ShotsToDestroy(Door1BHp, Door1BThre, 40f, 500));
        }

        [Test]
        public void AStrongEnoughWeaponStillBreaksTheReinforcedDoor()
        {
            // Same door, rounds of 50 (exactly at thre) -> 400/50 = 8 hits.
            Assert.AreEqual(8, ShotsToDestroy(Door1BHp, Door1BThre, 50f, 500));
        }

        [Test]
        public void TheHardestBarrierNeedsTheHeaviestWeapon()
        {
            // platform1: 5000 hp, thre 800. 900-damage rounds: ceil(5000/900) = 6.
            Assert.AreEqual(6, ShotsToDestroy(Platform1Hp, Platform1Thre, 900f, 100));

            // And 800 is the floor: anything under it does literally nothing, forever.
            Assert.AreEqual(-1, ShotsToDestroy(Platform1Hp, Platform1Thre, 799f, 5000));
        }

        [Test]
        public void AThresholdlessBarrierFallsToASingleRifleRound()
        {
            // septum (30 hp, no thre) and window1 (10 hp, no thre): one round that carries at least
            // their hp takes them out, because the floor is 0.
            Assert.AreEqual(1, ShotsToDestroy(SeptumHp, 0f, 30f, 5));
            Assert.AreEqual(1, ShotsToDestroy(Window1Hp, 0f, 30f, 5));
        }
    }
}
