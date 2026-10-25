using NUnit.Framework;
using PFE.Entities.Units;
using PFE.Systems.Map.TileQuery;
using PFE.Tests.Editor.Common;

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

        /// <summary>
        /// <c>UnitController.ApplyDefinitionToBody</c> must write <b>three</b> things from the
        /// definition: the collider's size, its feet-anchored offset, and the tile motor's pixel size.
        ///
        /// <para><b>Why a lint.</b> The behavioural form of this needs a <c>GameObject</c> with a
        /// <c>BoxCollider2D</c>, a <c>Rigidbody2D</c> and a <c>TilePhysicsController</c>, which this
        /// project's offline harness cannot construct (<c>SecurityException: ECall methods must be
        /// packaged into a system module</c>) — see <c>UnitMotorReHomeTests</c>'s neighbours.
        /// <c>UnitBodyAnchorTests</c> asserts the real <c>bounds.min.y</c> inside the editor; this is the
        /// half that can go red anywhere.</para>
        ///
        /// <para><b>The defect this pins, exactly.</b> The method wrote <c>size</c> only. Every other
        /// construction path set the offset by hand (<c>RoomUnitSpawner</c>, <c>PlayerRigBuilder</c>), so
        /// the one unit that never did — the prefab player — shipped with its 0.70-tall collider centred
        /// on its feet: 35 px of it inside the floor tile, and the head 35 px outside the box that
        /// projectiles and <c>CollisionBoxesOverlap</c> test against. The motor was never sized at all
        /// outside <c>RoomUnitSpawner</c>, so the player collided as 30×50 while AS3's <c>littlepip</c> is
        /// 50×70. All four writes are checked here because dropping any one of them restores part of the
        /// bug, silently.</para>
        /// </summary>
        [Test]
        public void ApplyDefinitionToBody_WritesSizeOffsetAndMotorSize()
        {
            string source = SourceLint.ReadStripped("Entities/Units/UnitController.cs");
            string body = SourceLint.MethodBody(source, "protected void ApplyDefinitionToBody()");

            Assert.That(body, Does.Contain("boxCollider.offset ="),
                "ApplyDefinitionToBody no longer writes the collider's offset, so the body box is " +
                "centred on the feet again — half of it inside the floor (AS3 Unit.as:1872-1881 puts " +
                "the whole box above the origin: Y1 = Y - scY, Y2 = Y).");

            Assert.That(body, Does.Contain("Height * 0.5f"),
                "The collider offset must be HALF the body height, so that the box's bottom edge lands " +
                "on the feet. A full height, a negative half, or a hardcoded value all put the box " +
                "somewhere other than on the feet.");

            Assert.That(body, Does.Contain("ConfigureCollisionSize("),
                "ApplyDefinitionToBody no longer sizes the tile motor, so any unit built outside " +
                "RoomUnitSpawner (the prefab player above all) collides as the motor's serialized " +
                "30x50 default instead of its definition's box.");

            Assert.That(body, Does.Contain("PixelsFromWorldUnits("),
                "The motor size must go through PixelsFromWorldUnits — the definition is in world " +
                "units and the motor is in pixels, and handing it the raw value is a silent 100x " +
                "error that still compiles.");

            // AS3's crouch height is `sitY` (default `stayY / 2`, Unit.as:1037-1044) and it replaces
            // `scY` while crouching (:2519-2520) — so it is authored data, not a prefab tunable. The
            // motor prefers its serialized `crouchedCollisionHeight` over the definition, which makes
            // it the one body dimension a prefab could still shadow after the offset and size were
            // fixed. Dropping this argument silently restores that shadow.
            Assert.That(body, Does.Contain("sitHeight"),
                "ApplyDefinitionToBody no longer hands the motor the crouch height, so a prefab that " +
                "serialized a stale crouchedCollisionHeight crouches to that instead of the " +
                "definition's sitHeight (AS3 sitY).");
        }
    }
}
