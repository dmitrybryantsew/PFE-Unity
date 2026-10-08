using NUnit.Framework;
using PFE.Entities.Units;
using PFE.Systems.Map.TileQuery;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins <see cref="UnitCollisionBoxMath"/> — the one conversion between the two conventions a
    /// unit's body size is stored in.
    ///
    /// <para><b>Why this fixture exists.</b> <c>RoomUnitSpawner</c> fed
    /// <c>UnitDefinition.Width</c>/<c>Height</c> (world units: <c>0.55</c>/<c>0.70</c>) straight into
    /// <c>TilePhysicsController.ConfigureCollisionSize</c>, whose parameters are pixels and whose values
    /// reach <c>TileCollisionMath.CheckTileCollisionAt</c> — which divides by
    /// <c>WorldConstants.TILE_SIZE</c> to get tile coordinates. So every motored unit collided against a
    /// box 100x too small while its <c>BoxCollider2D</c>, and therefore the box the collider debug
    /// overlay draws, was correct. Nothing threw; the unit just ignored walls.</para>
    ///
    /// <para><b>Why it is a pure fixture and not a spawner test.</b> The natural test —
    /// <c>UnitMotorReHomeTests.Spawner_WithUnitMotorOn_GivesTheUnitAMotorSizedFromItsDefinition</c> —
    /// needs a <c>GameObject</c>, and this project's offline harness cannot construct one
    /// (<c>SecurityException: ECall methods must be packaged into a system module</c>), so that fixture
    /// only runs inside the Unity editor. The arithmetic lives here instead, where it runs anywhere.
    /// The editor-only fixture still asserts the call site is wired to this helper.</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitCollisionBoxMathTests
    {
        private const float Tolerance = 0.0001f;

        /// <summary>
        /// <c>zombie0</c>'s real authored size: <c>&lt;phis sX='55' sY='70'&gt;</c>, stored by
        /// <c>XMLConverter</c> as <c>sX / 100f</c>.
        /// </summary>
        [Test]
        public void Zombie0_ConvertsToItsAuthoredPixelSize()
        {
            Assert.That(UnitCollisionBoxMath.PixelsFromWorldUnits(0.55f), Is.EqualTo(55f).Within(Tolerance),
                "AS3 sX='55' is stored as 0.55 world units and must reach the motor as 55 px.");

            Assert.That(UnitCollisionBoxMath.PixelsFromWorldUnits(0.70f), Is.EqualTo(70f).Within(Tolerance),
                "AS3 sY='70' is stored as 0.70 world units and must reach the motor as 70 px.");
        }

        /// <summary>
        /// The negative control, stated as an assertion rather than left implicit: the converted value
        /// must NOT equal the raw one. Without this, a fixture that only asserted "0.55 in, 55 out"
        /// would still pass if the helper were changed to a no-op that returned its input and the test
        /// values were quietly changed to match — which is exactly how the original bug survived a test
        /// named "…SizedFromItsDefinition".
        /// </summary>
        [Test]
        public void TheConvertedValueIsNotTheRawValue()
        {
            Assert.That(UnitCollisionBoxMath.PixelsFromWorldUnits(0.55f), Is.Not.EqualTo(0.55f),
                "0.55 world units must not be handed to a pixel parameter unchanged — that is the " +
                "100x-too-small box this helper exists to prevent.");
        }

        /// <summary>
        /// The conversion is the inverse of the canonical constant, so it cannot drift from the rest of
        /// the port by someone re-deriving 100.
        /// </summary>
        [Test]
        public void ConversionRoundTripsThroughTheCanonicalConstant()
        {
            Assert.That(TileQueryConstants.PixelToUnit, Is.EqualTo(0.01f).Within(Tolerance),
                "TileQueryConstants.PixelToUnit is the port's one 0.01, and this helper divides by it " +
                "rather than by a literal 100.");

            foreach (float worldUnits in new[] { 0.5f, 0.55f, 0.7f, 1f, 2f })
            {
                float pixels = UnitCollisionBoxMath.PixelsFromWorldUnits(worldUnits);
                Assert.That(pixels * TileQueryConstants.PixelToUnit, Is.EqualTo(worldUnits).Within(Tolerance),
                    $"{worldUnits} world units -> {pixels} px must multiply back to {worldUnits}.");
            }
        }

        /// <summary>
        /// A definition that authors no size keeps the motor's serialized default.
        /// <c>ConfigureCollisionSize</c> ignores non-positive values, so this helper has to return the
        /// <c>0</c> sentinel rather than a negative box.
        /// </summary>
        [Test]
        public void NonPositiveStaysTheSentinelRatherThanGoingNegative()
        {
            Assert.That(UnitCollisionBoxMath.PixelsFromWorldUnits(0f), Is.EqualTo(0f),
                "0 must stay 0 so ConfigureCollisionSize keeps the serialized default.");
            Assert.That(UnitCollisionBoxMath.PixelsFromWorldUnits(-0.55f), Is.EqualTo(0f),
                "A negative world size must not become a negative collision box.");
        }
    }
}
