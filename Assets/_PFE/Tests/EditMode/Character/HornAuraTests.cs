using NUnit.Framework;
using PFE.Character;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// The horn aura's charge rule — AS3 <c>aMC</c> chasing <c>aMagic</c>
    /// (<c>UnitPlayer.as:1478-1486</c>) and the alpha it becomes (<c>:4851-4852</c>).
    ///
    /// <para><b>Why these are literals.</b> 5 per tick, 0 / 50 / 100, and a ceiling of 100 are read
    /// off the AS3, not recomputed from <see cref="HornAura"/>'s own constants — a test that
    /// re-derived the expectation from the same constant it is testing would pass for any constant.</para>
    ///
    /// <para><b>The overshoot row is the interesting one.</b> The original's integers can land 1..4
    /// past the target (<c>aMC</c> 50 → <c>aMagic</c> 0 stops at 0 after ten steps, but a target of 50
    /// reached from 3 would step 3→8→…→48→53 without a clamp). <see cref="HornAura.StepLevel"/>
    /// deliberately stops on the target instead; the row below pins that decision so it cannot drift
    /// back by accident.</para>
    /// </summary>
    [TestFixture]
    public class HornAuraTests
    {
        const float Tolerance = 1e-4f;

        [Test]
        public void IdleStaysIdle()
        {
            Assert.AreEqual(HornAura.LevelIdle, HornAura.StepLevel(HornAura.LevelIdle, HornAura.LevelIdle),
                Tolerance, "no target, no movement");
        }

        [Test]
        public void RisesByFivePerTick()
        {
            float level = HornAura.LevelIdle;
            for (int tick = 1; tick <= 10; tick++)
            {
                level = HornAura.StepLevel(level, HornAura.LevelWeaponDrawn);
                Assert.AreEqual(tick * 5f, level, Tolerance, $"tick {tick} of 0 -> 50");
            }

            Assert.AreEqual(50f, level, Tolerance, "ten ticks of 5 is exactly the weapon-drawn target");
        }

        [Test]
        public void FallsByFivePerTick()
        {
            float level = HornAura.LevelLevitate;
            for (int tick = 1; tick <= 20; tick++)
            {
                level = HornAura.StepLevel(level, HornAura.LevelIdle);
            }

            Assert.AreEqual(0f, level, Tolerance, "twenty ticks of 5 is exactly the full range");
        }

        [Test]
        public void StopsOnTheTargetRatherThanOvershootingIt()
        {
            // 48 + 5 = 53 in the original's unclamped integers; the port stops at 50.
            Assert.AreEqual(50f, HornAura.StepLevel(48f, 50f), Tolerance);
            // And a step that would cross the target from above.
            Assert.AreEqual(50f, HornAura.StepLevel(52f, 50f), Tolerance);
        }

        [Test]
        public void ClampsAnOutOfRangeTarget()
        {
            Assert.AreEqual(0f, HornAura.StepLevel(0f, -40f), Tolerance, "a negative target is idle");
            Assert.AreEqual(5f, HornAura.StepLevel(0f, 900f), Tolerance,
                "a target past the ceiling still ramps toward the ceiling, one step at a time");
        }

        [Test]
        public void NormalizedIsAlphaOutOfOneHundred()
        {
            Assert.AreEqual(0.0f, HornAura.Normalized(0f), Tolerance);
            Assert.AreEqual(0.5f, HornAura.Normalized(50f), Tolerance);
            Assert.AreEqual(1.0f, HornAura.Normalized(100f), Tolerance);
            Assert.AreEqual(1.0f, HornAura.Normalized(250f), Tolerance, "clamped, not extrapolated");
            Assert.AreEqual(0.0f, HornAura.Normalized(-250f), Tolerance);
        }

        [Test]
        public void ThePartNameMatchesTheImportedDefinition()
        {
            // If the importer ever renames symbol 239, the renderers would silently stop finding it:
            // `part.partName == HornAura.PartName` would simply never be true and the glow would go
            // back to being invisible with no error anywhere. This is the only guard on that string.
            Assert.AreEqual("magic", HornAura.PartName);
        }
    }
}
