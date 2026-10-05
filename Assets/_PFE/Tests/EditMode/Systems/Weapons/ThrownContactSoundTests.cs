using NUnit.Framework;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="ThrownContactSound"/> — the volume rule for a thrown object's landing sound,
    /// AS3 <c>Snd.ps(this.sndHit, X, Y, 0, Math.abs(d / 10))</c> at <c>PhisBullet.as:246/267/290/321/336</c>.
    ///
    /// <para><b>Why the volume needs a fixture at all.</b> <c>d</c> is a velocity in <b>px/frame</b> — the
    /// oracle's unit — while the port integrates in units/s, and the conversion between them is a factor
    /// of <c>SimClock.FramesPerSecond * PixelToUnit</c>. A caller that passed <c>_velocity.x</c> straight
    /// in would be about 3.3× too loud, and nothing would look wrong: a louder bang still sounds like a
    /// bang. The identity case below (10 px/frame → 1.0) is the one that catches it, because 10 is the
    /// divisor and the only input whose expected output is a round number.</para>
    ///
    /// <para><b>And the unclamped case, which is the oracle.</b> AS3 multiplies <c>param5</c> into the
    /// <c>SoundTransform</c> with no clamp (<c>Snd.as:637-663</c>), so a fast landing is genuinely asked
    /// for more than 1.0. A "sensible" clamp added later would pass every test that only checked the slow
    /// end, so the loud end is pinned too.</para>
    /// </summary>
    [TestFixture]
    public class ThrownContactSoundTests
    {
        // ── Whether anything plays ───────────────────────────────────────────────────────────

        [Test]
        public void ShouldPlay_IsFalseForAnAbsentOrEmptyId()
        {
            // Only thrown weapons carry `<snd fall>`, and the port must not substitute a generic thud —
            // AS3 plays nothing, so nothing is the correct answer.
            Assert.IsFalse(ThrownContactSound.ShouldPlay(null));
            Assert.IsFalse(ThrownContactSound.ShouldPlay(""));
        }

        [Test]
        public void ShouldPlay_IsTrueForTheIdsTheDataActuallyCarries()
        {
            // Positive control for the pair above, using ids read out of AllData.as rather than invented:
            // the three `<snd fall>` values any weapon in the game has.
            Assert.IsTrue(ThrownContactSound.ShouldPlay("fall_grenade"),  "fgren, gasgr");
            Assert.IsTrue(ThrownContactSound.ShouldPlay("bottle_hit"),    "molotov, acidgr");
            Assert.IsTrue(ThrownContactSound.ShouldPlay("fall_metal_small"), "hmine");
        }

        // ── The volume ───────────────────────────────────────────────────────────────────────

        [Test]
        public void TheDivisorIsTenPxPerFrame()
        {
            // Bullet.as:246 and its four siblings all divide by the literal 10.
            Assert.AreEqual(10f, ThrownContactSound.VolumeDivisor);
        }

        [Test]
        public void VolumeScale_IsOneAtTenPxPerFrame()
        {
            // The identity case — the only input whose expected volume is exactly 1.0, and therefore the
            // only one that detects a caller handing over units/s (which would read ~0.3 here).
            Assert.AreEqual(1f, ThrownContactSound.VolumeScale(10f), 1e-5f);
        }

        [Test]
        public void VolumeScale_IsLinearInTheSpeed()
        {
            Assert.AreEqual(0f,   ThrownContactSound.VolumeScale(0f),   1e-5f);
            Assert.AreEqual(0.5f, ThrownContactSound.VolumeScale(5f),   1e-5f);
            Assert.AreEqual(2f,   ThrownContactSound.VolumeScale(20f),  1e-5f);
        }

        [Test]
        public void VolumeScale_UsesTheMagnitudeOfTheAxis()
        {
            // AS3 takes Math.abs(d): a contact from below (dy < 0) is exactly as loud as one from above.
            Assert.AreEqual(ThrownContactSound.VolumeScale(12f),
                            ThrownContactSound.VolumeScale(-12f), 1e-5f);
            Assert.AreEqual(1.2f, ThrownContactSound.VolumeScale(-12f), 1e-5f);
        }

        [Test]
        public void VolumeScale_IsNotClampedToUnity()
        {
            // The loud end, pinned so a later "sanity clamp" cannot pass. A grenade arriving at
            // 20 px/frame — WThrow's own `speed='20'` — is asked for twice its base volume.
            Assert.AreEqual(2f, ThrownContactSound.VolumeScale(20f), 1e-5f);
            Assert.Greater(ThrownContactSound.VolumeScale(30f), 1f);

            // Negative control for the same claim: the clamp a well-meaning fix would add would make
            // this equal to 1, and it does not.
            Assert.AreNotEqual(1f, ThrownContactSound.VolumeScale(30f));
        }
    }
}
