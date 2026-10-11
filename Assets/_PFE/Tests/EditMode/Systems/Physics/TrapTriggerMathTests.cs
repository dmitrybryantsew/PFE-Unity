using NUnit.Framework;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Pins <see cref="TrapTriggerMath"/> — AS3 <c>loc.Trap</c> (<c>fe/loc/Trap.as</c>), the static
    /// <c>spikes</c>/<c>fspikes</c> objects.
    ///
    /// <para><b>Why this needed pinning.</b> A placed <c>spikes</c> object was completely inert: the
    /// port built the <c>ObjectInstance</c> and nothing ever read it, so the object drew the shared
    /// <c>vismine</c> sprite and did nothing. 540 of the 639 shipped rooms place one.</para>
    ///
    /// <para><b>Half of these tests are about the <i>sign</i> of a Y.</b> The oracle is AS3 (Y down,
    /// <c>dy</c> positive while falling) and this port is Y-up with an up-positive velocity, so the two
    /// are mirrored. A sign error here does not crash and does not look wrong — it silently turns
    /// "spikes catch you falling onto them" into "spikes catch you jumping off them", and the traps
    /// still fire, so nothing goes red. Every arm below therefore has an explicit control asserting that
    /// the <i>opposite</i> motion is spared.</para>
    /// </summary>
    [TestFixture]
    public class TrapTriggerMathTests
    {
        // ── The two spaces ───────────────────────────────────────────────────

        /// <summary>
        /// <c>Trap.as:186</c> compares <c>dy &gt; 8</c>, where AS3's <c>dy</c> is <b>positive while
        /// falling</b>. This port's <c>VelocityPixelsPerFrame.y</c> is negative while falling
        /// (<c>TilePhysicsController.ApplyGravity</c> does <c>dy -= gravity</c> and integrates
        /// <c>targetY = posY + stepMoveY</c>), so the conversion is a negation.
        /// </summary>
        [Test]
        public void ToAs3Dy_NegatesThePortsUpPositiveVelocity()
        {
            // The port says "falling" with a negative y velocity...
            Assert.AreEqual(12f, TrapTriggerMath.ToAs3Dy(-12f),
                "Falling at 12 px/frame in port space is dy = +12 in AS3 space.");
            Assert.AreEqual(-12f, TrapTriggerMath.ToAs3Dy(12f),
                "Rising at 12 px/frame in port space is dy = -12 in AS3 space.");

            // Control: the raw port value would have failed the falling threshold, so a missing
            // negation is not a no-op — it inverts which motion triggers the trap.
            Assert.Less(-12f, TrapTriggerMath.FallingSpeedThreshold,
                "The unconverted port value is below the threshold, i.e. a faller would be spared.");
            Assert.Greater(TrapTriggerMath.ToAs3Dy(-12f), TrapTriggerMath.FallingSpeedThreshold,
                "The converted value is above it, i.e. the faller is caught.");
        }

        /// <summary>
        /// <c>Trap.as:79-94</c> — <c>scX = (@sX &gt; 0) ? @sX : @size * tileX</c>, and the same for Y
        /// with <c>@wid</c>. <c>spikes</c> authors <c>size='1' wid='1'</c> and no <c>@sX</c>/<c>@sY</c>,
        /// so its box is one 40 px tile each way; <c>fspikes</c> authors <c>sY='20'</c> and must use the
        /// explicit 20 rather than <c>wid * 40</c>.
        /// </summary>
        [Test]
        public void Extents_PreferTheExplicitPixelAttributeOverTheTileCount()
        {
            Assert.AreEqual(40f, TrapTriggerMath.ResolveExtentX(sizeAttribute: 1, sXAttribute: 0f),
                "spikes: @size 1 with no @sX is one tile.");
            Assert.AreEqual(80f, TrapTriggerMath.ResolveExtentX(sizeAttribute: 2, sXAttribute: 0f));

            Assert.AreEqual(40f, TrapTriggerMath.ResolveExtentY(widAttribute: 1, sYAttribute: 0f));
            Assert.AreEqual(20f, TrapTriggerMath.ResolveExtentY(widAttribute: 1, sYAttribute: 20f),
                "fspikes authors sY='20', which wins over wid * 40 = 40.");
        }

        // ── The trigger volume ───────────────────────────────────────────────

        /// <summary>
        /// <c>Trap.as:56-65</c>, the non-<c>floor</c> arm — <c>spikes</c>. AS3
        /// <c>Y1 = Y - tileY; Y2 = Y1 + scY</c> with <c>scY = 40</c>, i.e. screen <c>[Y-40, Y]</c>. In this
        /// port's Y-up space that is <c>[anchor, anchor + 40]</c>: the tile the object sits in.
        /// </summary>
        [Test]
        public void Box_NonFloor_IsOneTileEndingAtTheAnchor()
        {
            var box = TrapTriggerMath.ComputeBox(
                anchorCenterX: 100f, anchorBottomY: 200f, extentX: 40f, extentY: 40f, floor: false);

            Assert.AreEqual(80f, box.LeftX, "X1 = X - scX/2.");
            Assert.AreEqual(120f, box.RightX, "X2 = X + scX/2.");
            Assert.AreEqual(200f, box.LowerY, "The anchor is the bottom of the band.");
            Assert.AreEqual(240f, box.UpperY, "The band reaches one tile up from the anchor.");
            Assert.AreEqual(40f, box.Height);
        }

        /// <summary>
        /// <c>Trap.as:57-60</c>, the <c>floor</c> arm — <c>fspikes</c>. AS3 <c>Y1 = Y - scY; Y2 = Y</c>
        /// with <c>scY = 20</c> from <c>@sY</c>. The band is <c>scY</c> tall, <b>not</b> one tile, so a
        /// shared helper that always used <c>tileY</c> would make <c>fspikes</c> twice as deep as the
        /// oracle.
        /// </summary>
        [Test]
        public void Box_Floor_UsesTheAuthoredExtentNotATile()
        {
            var box = TrapTriggerMath.ComputeBox(
                anchorCenterX: 100f, anchorBottomY: 200f, extentX: 40f, extentY: 20f, floor: true);

            Assert.AreEqual(200f, box.LowerY);
            Assert.AreEqual(220f, box.UpperY, "Y - scY = 20 px above the anchor, not a full tile.");
            Assert.AreEqual(20f, box.Height);

            // Control: the two arms really do place the band differently, so choosing the wrong one is
            // observable. It is the *position* that differs, not the height — with `extentY == 20` the
            // non-floor arm is one tile tall and ends 20 px below the floor arm's top, so both heights
            // are 20 and a height comparison would have been a control that controls nothing.
            var nonFloor = TrapTriggerMath.ComputeBox(100f, 200f, 40f, 20f, floor: false);
            Assert.AreEqual(220f, nonFloor.LowerY,
                "Non-floor: upper = anchor + tileY = 240, lower = upper - scY = 220.");
            Assert.AreNotEqual(box.LowerY, nonFloor.LowerY);
        }

        // ── The falling arm (@att 1, `spikes`) ───────────────────────────────

        /// <summary>
        /// <c>Trap.as:186-189</c>: a unit falling faster than 8 px/frame whose feet are inside the box
        /// takes <c>massa * dy / 20 * dam * (1 + locDifLevel * 0.1)</c>.
        /// </summary>
        [Test]
        public void FallingArm_DamagesAFallerInsideTheBox_WithTheOraclesFormula()
        {
            var box = TrapTriggerMath.ComputeBox(100f, 200f, 40f, 40f, floor: false);

            // massa 10, dy 20, damage 50, difficulty 0 -> 10 * 20 / 20 * 50 * 1.0 = 500.
            bool hit = TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerFalling, damageAttribute: 50f,
                unitCenterX: 100f, unitFeetY: 210f, unitHeadY: 250f,
                unitDyAs3: 20f, unitOsnDyAs3: 0f,
                massa: 10f, difficulty: 0f, isFlying: false,
                out float damage);

            Assert.IsTrue(hit);
            Assert.AreEqual(500f, damage, 0.001f);

            // Difficulty scales it by 1 + 0.1 per level, exactly as the oracle's multiplier does.
            TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerFalling, 50f,
                100f, 210f, 250f, 20f, 0f, 10f, difficulty: 4f, isFlying: false, out float scaled);
            Assert.AreEqual(700f, scaled, 0.001f, "1 + 4 * 0.1 = 1.4x.");
        }

        /// <summary>
        /// The threshold and the box are both load-bearing, and the interesting case is the one just
        /// <i>outside</i> each: a slow faller and a faller beside the trap must both be spared.
        /// </summary>
        [Test]
        public void FallingArm_SparesASlowFaller_AndAFallerBesideTheTrap()
        {
            var box = TrapTriggerMath.ComputeBox(100f, 200f, 40f, 40f, floor: false);

            // dy exactly 8 is NOT > 8 (`Trap.as:186` is a strict comparison).
            Assert.IsFalse(TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerFalling, 50f,
                100f, 210f, 250f, unitDyAs3: 8f, 0f, 10f, 0f, false, out float atThreshold));
            Assert.AreEqual(0f, atThreshold);

            // 0.1 px/frame past the left edge of the box.
            Assert.IsFalse(TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerFalling, 50f,
                unitCenterX: 79.9f, 210f, 250f, 20f, 0f, 10f, 0f, false, out _),
                "X1 is 80, so 79.9 is outside.");

            // Feet below the band — falling past the trap, not into it.
            Assert.IsFalse(TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerFalling, 50f,
                100f, unitFeetY: 199f, 250f, 20f, 0f, 10f, 0f, false, out _),
                "The band starts at the anchor, 200.");
        }

        /// <summary>
        /// <b>The mirror control.</b> A unit moving <b>up</b> through the same box must be spared by the
        /// falling arm — that is what stops a sign error from turning every trap into a ceiling trap.
        /// </summary>
        [Test]
        public void FallingArm_SparesARiser_BecauseDyIsNegative()
        {
            var box = TrapTriggerMath.ComputeBox(100f, 200f, 40f, 40f, floor: false);

            Assert.IsFalse(TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerFalling, 50f,
                100f, 210f, 250f, unitDyAs3: -20f, 0f, 10f, 0f, false, out _),
                "Rising fast is dy = -20 in AS3 space, which is not > 8.");
        }

        /// <summary><c>Trap.as:186</c> — <c>!param1.isFly</c>. A flyer passes over spikes.</summary>
        [Test]
        public void BothArms_SpareAFlyer()
        {
            var box = TrapTriggerMath.ComputeBox(100f, 200f, 40f, 40f, floor: false);

            Assert.IsFalse(TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerFalling, 50f,
                100f, 210f, 250f, 20f, 0f, 10f, 0f, isFlying: true, out _));

            var ceiling = TrapTriggerMath.ComputeBox(100f, 200f, 40f, 20f, floor: true);
            Assert.IsFalse(TrapTriggerMath.TryResolveDamage(
                ceiling, TrapTriggerMath.TriggerRising, 15f,
                100f, 150f, 205f, -20f, 0f, 10f, 0f, isFlying: true, out _));
        }

        // ── The rising arm (@att 2, `fspikes`) ───────────────────────────────

        /// <summary>
        /// <c>Trap.as:191-194</c>: the <b>head</b> is tested, not the feet, and the damage has no
        /// <c>dy / 20</c> factor — <c>massa * dam * (1 + locDifLevel * 0.1)</c>. Using the feet, or
        /// keeping the fall factor, would both be plausible-looking and wrong.
        /// </summary>
        [Test]
        public void RisingArm_TestsTheHead_AndHasNoFallFactor()
        {
            // fspikes: floor, sY = 20, so the band is [200, 220].
            var box = TrapTriggerMath.ComputeBox(100f, 200f, 40f, 20f, floor: true);

            // Feet well below the band; only the head is inside. massa 10, damage 15, difficulty 0
            // -> 10 * 15 * 1.0 = 150.
            bool hit = TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerRising, damageAttribute: 15f,
                unitCenterX: 100f, unitFeetY: 150f, unitHeadY: 205f,
                unitDyAs3: -20f, unitOsnDyAs3: 0f,
                massa: 10f, difficulty: 0f, isFlying: false,
                out float damage);

            Assert.IsTrue(hit, "The head at 205 is inside [200, 220].");
            Assert.AreEqual(150f, damage, 0.001f, "No dy/20 factor on this arm.");

            // Control: the FEET at 150 are outside the band, so a version that tested the feet would
            // have reported no hit here.
            Assert.IsFalse(box.ContainsY(150f));
        }

        /// <summary>
        /// The rising arm's own mirror control: a <b>faller</b> must be spared, and so must a riser whose
        /// head never reaches the band.
        /// </summary>
        [Test]
        public void RisingArm_SparesAFaller_AndARiserBelowTheBand()
        {
            var box = TrapTriggerMath.ComputeBox(100f, 200f, 40f, 20f, floor: true);

            Assert.IsFalse(TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerRising, 15f,
                100f, 150f, 205f, unitDyAs3: 20f, 0f, 10f, 0f, false, out _),
                "Falling is dy = +20, which is not < 0.");

            Assert.IsFalse(TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerRising, 15f,
                100f, 150f, unitHeadY: 199f, unitDyAs3: -20f, 0f, 10f, 0f, false, out _),
                "Head at 199 is just under the band's 200.");
        }

        /// <summary>
        /// AS3 <c>Trap.as:191</c> — <c>param1.dy + param1.osndy &lt; 0</c>. The port passes
        /// <c>osndy = 0</c> because it carries no support displacement for tile ground, so the arm
        /// degenerates to <c>dy &lt; 0</c>. This test records the shape of the sum, so that adding the
        /// real <c>osndy</c> later is a change to the caller and not a silent behaviour shift.
        /// </summary>
        [Test]
        public void RisingArm_SumsTheSupportsDisplacement()
        {
            var box = TrapTriggerMath.ComputeBox(100f, 200f, 40f, 20f, floor: true);

            // A support rising at 25 px/frame cancels a 20 px/frame fall: -20 + 25 = +5, not < 0.
            Assert.IsFalse(TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerRising, 15f,
                100f, 150f, 205f, unitDyAs3: -20f, unitOsnDyAs3: 25f, 10f, 0f, false, out _),
                "dy + osndy = +5, so the trap does not fire — the unit is riding its support up.");

            // The same fall with no support displacement does fire.
            Assert.IsTrue(TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerRising, 15f,
                100f, 150f, 205f, unitDyAs3: -20f, unitOsnDyAs3: 0f, 10f, 0f, false, out _));
        }

        /// <summary>
        /// An unrecognised <c>@att</c> fires neither arm, matching the oracle's two <c>if</c>s with no
        /// <c>else</c>. Guessing an arm would invent a trap behaviour the data never asked for.
        /// </summary>
        [Test]
        public void UnknownTriggerAttribute_FiresNeitherArm()
        {
            var box = TrapTriggerMath.ComputeBox(100f, 200f, 40f, 40f, floor: false);

            Assert.IsFalse(TrapTriggerMath.TryResolveDamage(
                box, triggerAttribute: 0, 50f, 100f, 210f, 250f, 20f, 0f, 10f, 0f, false, out _));
            Assert.IsFalse(TrapTriggerMath.TryResolveDamage(
                box, triggerAttribute: 3, 50f, 100f, 210f, 250f, -20f, 0f, 10f, 0f, false, out _));
        }

        /// <summary>
        /// A trap with no <c>@damage</c> cannot hurt anyone: the oracle computes a zero and
        /// <c>damage()</c> does nothing. Reported as "no hit" rather than as a zero-damage hit, so a
        /// caller cannot mistake it for a firing trap.
        /// </summary>
        [Test]
        public void ZeroDamage_ReportsNoHit()
        {
            var box = TrapTriggerMath.ComputeBox(100f, 200f, 40f, 40f, floor: false);

            Assert.IsFalse(TrapTriggerMath.TryResolveDamage(
                box, TrapTriggerMath.TriggerFalling, damageAttribute: 0f,
                100f, 210f, 250f, 20f, 0f, 10f, 0f, false, out float damage));
            Assert.AreEqual(0f, damage);
        }
    }
}
