using NUnit.Framework;
using PFE.Entities.Units;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// D5 — <c>earMult</c>, the one difficulty scalar that reaches a unit's senses.
    ///
    /// <para><b>Where it comes from.</b> <c>Land.setLocDif</c> writes it onto the room
    /// (<c>Land.as:988-991</c>: <c>if (globalDif &lt; 2) earMult *= 0.5</c>), the room's planner hands it
    /// to the listener, and the listener multiplies it into the hearing product
    /// (<c>Unit.as:4538</c>) and the alarm radius (<c>Location.as:2867</c>). The default is
    /// <b>1</b> (<c>Location.as:259</c>) — Normal and harder are the identity, easy halves.</para>
    ///
    /// <para><b>Only the hearing path, and that is the oracle's split.</b> The oracle's
    /// <c>Location.budilo</c> folds <c>earMult</c> into the alarm radius <i>squared</i>, while
    /// <c>Unit.listen()</c> folds it into the hearing radius <i>linearly</i>. The port has no
    /// <c>budilo</c>; its alarm path is <c>Unit.budilo</c>, which carries no <c>earMult</c> term at all.
    /// So the scalar reaches <see cref="NoiseMath.HearingRadius"/> and
    /// <see cref="NoiseMath.AlarmReaches"/>'s <c>receiverEarMultiplier</c> argument, and nothing else.
    /// Adding it at a second site would apply the same difficulty modifier twice.</para>
    ///
    /// <para><b>It is not authored data.</b> <c>grep '@earMult'</c> over the whole oracle dump returns
    /// nothing: no XML attribute moves it, so nothing may expose it as per-unit authoring.</para>
    /// </summary>
    [TestFixture]
    public class DifficultyHearingTests
    {
        /// <summary>The scalar's resting value — <c>Location.earMult:Number = 1</c> (Location.as:259).</summary>
        [Test]
        public void DefaultEarMultiplier_IsOne()
        {
            Assert.That(NoiseMath.DefaultEarMultiplier, Is.EqualTo(1f).Within(1e-5));
        }

        /// <summary>
        /// <c>noise × ear × earMult</c> — the product is linear in <c>earMult</c>, so halving the scalar
        /// halves the radius exactly (the oracle squares it at the <i>alarm</i> site, which is a
        /// different call).
        /// </summary>
        [Test]
        public void HearingRadius_WithEarMultHalf_IsExactlyHalf()
        {
            float full = NoiseMath.HearingRadius(noise: 200, ear: 1.5f, earMultiplier: 1f);
            float half = NoiseMath.HearingRadius(noise: 200, ear: 1.5f, earMultiplier: 0.5f);

            Assert.That(full, Is.EqualTo(300f).Within(1e-3), "200 * 1.5 * 1");
            Assert.That(half, Is.EqualTo(150f).Within(1e-3), "200 * 1.5 * 0.5");
            Assert.That(half * 2f, Is.EqualTo(full).Within(1e-3),
                "an easy run halves the audible radius (Unit.as:4538)");
        }

        /// <summary>
        /// The half-radius is observable, not just arithmetic: a target that is audible at Normal is
        /// inaudible at Easy from twice the distance.
        /// </summary>
        [Test]
        public void HearingIntensity_AtEasy_IsZeroBeyondHalfTheRadius()
        {
            const int noise = 200;
            const float ear = 1f;

            float radius = NoiseMath.HearingRadius(noise, ear, 1f);               // 200 px at Normal
            float distanceSquared = (radius * 0.75f) * (radius * 0.75f);          // 150 px away

            Assert.That(NoiseMath.HearingIntensity(noise, ear, 1f, distanceSquared), Is.GreaterThan(0f),
                "control: at Normal the target IS audible at 150 px");
            Assert.That(NoiseMath.HearingIntensity(noise, ear, 0.5f, distanceSquared), Is.EqualTo(0f),
                "at Easy the radius is 100 px, so the same target at 150 px is inaudible");
        }

        /// <summary>
        /// The alarm path's receiver term is the same scalar, applied once
        /// (<c>alarmRadius × ear × earMult</c>, Location.as:2867). Halving it quarters the reachable
        /// <i>area</i> — which is why the oracle squares the radius rather than scaling the distance.
        /// </summary>
        [Test]
        public void AlarmReaches_WithEarMultHalf_ReachesAQuarterOfTheArea()
        {
            const float alarmRadius = 100f;
            const float receiverEar = 1f;

            // 90 px away: inside a 100 px alarm, outside a 50 px one.
            float distanceSquared = 90f * 90f;

            Assert.That(NoiseMath.AlarmReaches(distanceSquared, alarmRadius, receiverEar, 1f, false), Is.True,
                "control: at Normal the alarm reaches 90 px");
            Assert.That(NoiseMath.AlarmReaches(distanceSquared, alarmRadius, receiverEar, 0.5f, false), Is.False,
                "at Easy the alarm radius is halved to 50 px");

            // 40 px away: inside even the halved radius.
            Assert.That(NoiseMath.AlarmReaches(40f * 40f, alarmRadius, receiverEar, 0.5f, false), Is.True,
                "control: a nearby receiver still hears it — the halving is not a mute");
        }

        /// <summary>
        /// A robot ignores the scalar, exactly as the oracle does (<c>Location.as:36411, :36423</c> gate on
        /// the receiver being mechanical). A port that applied <c>earMult</c> to every receiver would make
        /// eyebots and turrets easier to sneak past on Easy — a difficulty modifier on a unit type the
        /// oracle excludes from it.
        /// </summary>
        [Test]
        public void AlarmReaches_ARobotIgnoresTheEarMultiplier()
        {
            const float alarmRadius = 100f;
            float distanceSquared = 90f * 90f;

            Assert.That(NoiseMath.AlarmReaches(distanceSquared, alarmRadius, receiverEar: 1f,
                                               receiverEarMultiplier: 0.5f, receiverIsRobot: true), Is.True,
                "the robot branch compares against the bare alarm radius");
            Assert.That(NoiseMath.AlarmReaches(distanceSquared, alarmRadius, receiverEar: 0.1f,
                                               receiverEarMultiplier: 0.5f, receiverIsRobot: true), Is.True,
                "and ignores the receiver's ear too");
        }

        /// <summary>
        /// The three-way early-out: <c>noise &lt;= 0 || ear &lt;= 0 || earMult &lt;= 0</c> means a muted
        /// room is silent, not "quiet". Asserted because a floor here would resurrect the bug this file's
        /// subject was written to fix.
        /// </summary>
        [Test]
        public void HearingRadius_AnyFactorAtZero_IsZero()
        {
            Assert.That(NoiseMath.HearingRadius(200, 1f, 0f), Is.EqualTo(0f), "a muted room is silent");
            Assert.That(NoiseMath.HearingRadius(0, 1f, 0.5f), Is.EqualTo(0f), "a silent target");
            Assert.That(NoiseMath.HearingRadius(200, 0f, 0.5f), Is.EqualTo(0f), "a deaf listener");
        }
    }
}
