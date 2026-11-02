using NUnit.Framework;
using PFE.Systems.Physics;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Pins AS3's <b>Up mantle</b> — <c>Unit.as:2108-2118</c> / <c>:2185-2195</c> — the arm that lets
    /// a standing player hold Up against a one-tile-high ledge and be lifted onto it.
    ///
    /// <para><b>What the port was missing.</b> Not the crouch. <c>UpdateColliderProfile</c> already
    /// shrank the box on demand and already refused to grow it back under a low ceiling, which is the
    /// oracle's <c>unsit()</c> → <c>collisionAll()</c> → <c>sit(true)</c> round-trip
    /// (<c>Unit.as:2532-2538</c>) exactly. What was missing was the <i>lift</i>: <c>porog</c> is 20 px
    /// while walking (<c>UnitPlayer.as:2550</c>) and a tile is 40, so a full-tile ledge stopped the
    /// player dead and no code path in the motor read the Up key at all.</para>
    ///
    /// <para><b>Why the row arithmetic is the risky half and gets its own tests.</b> AS3's grid index
    /// grows <b>downward</b>; the port's grows up. The oracle's "one row below the head" is therefore
    /// <c>headRow − 1</c> here, and writing the oracle's <c>+ 1</c> produces a probe of the cell
    /// <i>above the head</i> — always open, never a ledge. That failure does not throw, does not
    /// change any other test, and looks exactly like "the feature was never implemented", which is
    /// how it survived being read as missing once already.</para>
    /// </summary>
    [TestFixture]
    public class CrouchSqueezeUpMathTests
    {
        /// <summary><c>WorldConstants.TILE_SIZE</c>.</summary>
        private const float TileSize = 40f;

        /// <summary>littlepip's standing height — AS3 <c>stayY = 70</c>.</summary>
        private const float StandingHeight = 70f;

        /// <summary>littlepip's crouched height — AS3 <c>sitY = stayY / 2 = 35</c>; the asset's <c>sitHeight</c> is 0.3 → 30 px.</summary>
        private const float CrouchedHeight = 30f;

        // ── The mirror ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The one assertion that catches the sign. AS3 reads <c>floor(Y1 / tileY) + 1</c>, which in
        /// this port's upward-growing grid is one row <b>down</b>.
        /// </summary>
        [Test]
        public void TorsoRow_IsOneRowDown_NotOneRowUp()
        {
            Assert.AreEqual(4, CrouchSqueezeUpMath.TorsoRow(5),
                "AS3's headRow + 1 is one row lower on screen; the port's grid runs the other way, so " +
                "the torso row is headRow - 1. A `+ 1` here probes the cell above the head, which is " +
                "open by definition, and the mantle silently never fires.");
        }

        /// <summary>
        /// A player standing on the room's floor: feet 40 (the top of row 0), head 110 → row 2, torso
        /// row 1. The ledge that row 1 would be has its top at 80 — exactly one tile above the feet,
        /// which is the geometry <c>porog = 20</c> cannot climb.
        /// </summary>
        [Test]
        public void HeadRow_PlayerOnTheFloor_ReadsTheRowTheHeadIsActuallyIn()
        {
            int headRow = CrouchSqueezeUpMath.HeadRow(40f, StandingHeight, 0f, TileSize);
            int torsoRow = CrouchSqueezeUpMath.TorsoRow(headRow);

            Assert.AreEqual(2, headRow, "feet 40 + height 70 = head 110, and 110/40 floors to 2");
            Assert.AreEqual(1, torsoRow, "the chest is in row 1, which spans [40, 80)");

            float ledgeTop = (torsoRow + 1) * TileSize;
            Assert.AreEqual(80f, ledgeTop, "the torso cell's top surface is one tile above the feet");
            Assert.IsTrue(CrouchSqueezeUpMath.LiftsUpward(40f, ledgeTop));
        }

        /// <summary>
        /// The room origin is subtracted, not ignored. Every room but the one at land (0,0) has a
        /// non-zero origin, so a version that forgot it would mantle correctly only in the first room.
        /// </summary>
        [Test]
        public void HeadRow_SubtractsTheRoomOrigin()
        {
            Assert.AreEqual(2, CrouchSqueezeUpMath.HeadRow(440f, StandingHeight, 400f, TileSize),
                "feet 440 with origin 400 is feet 40 room-local, head 110, row 2");

            Assert.AreNotEqual(
                CrouchSqueezeUpMath.HeadRow(440f, StandingHeight, 400f, TileSize),
                CrouchSqueezeUpMath.HeadRow(440f, StandingHeight, 0f, TileSize),
                "dropping the origin must change the answer, or the test proves nothing");
        }

        /// <summary>
        /// A head exactly on a cell boundary stays in the upper cell, as AS3's bare <c>floor</c> does.
        /// A nudging epsilon here would move it down a row and shift the whole probe by a cell.
        /// </summary>
        [Test]
        public void HeadRow_OnACellBoundary_StaysInTheCellTheHeadTouches()
        {
            Assert.AreEqual(1, CrouchSqueezeUpMath.HeadRow(10f, 30f, 0f, TileSize),
                "10 + 30 = 40 exactly; floor(40/40) is 1 on both sides of the mirror");
        }

        [Test]
        public void HeadRow_RefusesADegenerateTileSize()
        {
            Assert.AreEqual(int.MinValue, CrouchSqueezeUpMath.HeadRow(0f, 70f, 0f, 0f),
                "a zero tile size must not divide — the caller then declines the mantle");
        }

        // ── The gates ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void ShouldSqueezeUp_FiresOnTheOracleGeometry()
        {
            Assert.IsTrue(CrouchSqueezeUpMath.ShouldSqueezeUp(
                upHeld: true, crouching: false, grounded: true,
                headRowOpen: true, torsoRowSolid: true, torsoRowUnraised: true));
        }

        [Test]
        public void ShouldSqueezeUp_RefusesWithoutTheKey()
        {
            Assert.IsFalse(CrouchSqueezeUpMath.ShouldSqueezeUp(
                upHeld: false, crouching: false, grounded: true,
                headRowOpen: true, torsoRowSolid: true, torsoRowUnraised: true),
                "the mantle is the Up key's arm (`this.isUp`, Unit.as:2108)");
        }

        [Test]
        public void ShouldSqueezeUp_RefusesACrouchedUnit()
        {
            Assert.IsFalse(CrouchSqueezeUpMath.ShouldSqueezeUp(
                upHeld: true, crouching: true, grounded: true,
                headRowOpen: true, torsoRowSolid: true, torsoRowUnraised: true),
                "AS3 requires `!isSit` — letting the mantle stand a crouched player up first would " +
                "teleport them through a ceiling they were crawling under");
        }

        [Test]
        public void ShouldSqueezeUp_RefusesAnAirborneUnit()
        {
            Assert.IsFalse(CrouchSqueezeUpMath.ShouldSqueezeUp(
                upHeld: true, crouching: false, grounded: false,
                headRowOpen: true, torsoRowSolid: true, torsoRowUnraised: true),
                "AS3 requires `stay`");
        }

        [Test]
        public void ShouldSqueezeUp_RefusesWhenTheHeadCellIsSolid()
        {
            Assert.IsFalse(CrouchSqueezeUpMath.ShouldSqueezeUp(
                upHeld: true, crouching: false, grounded: true,
                headRowOpen: false, torsoRowSolid: true, torsoRowUnraised: true),
                "`_loc2_.phis == 0 || == 3` — mantling into a wall would put the head inside it");
        }

        [Test]
        public void ShouldSqueezeUp_RefusesWhenTheTorsoCellIsOpen()
        {
            Assert.IsFalse(CrouchSqueezeUpMath.ShouldSqueezeUp(
                upHeld: true, crouching: false, grounded: true,
                headRowOpen: true, torsoRowSolid: false, torsoRowUnraised: true),
                "`_loc3_.phis != 0 && != 3` — there is no floor to stand on; this is the flat-ground " +
                "case, where the torso row is air because the player is standing in it");
        }

        [Test]
        public void ShouldSqueezeUp_RefusesARaisedForm()
        {
            Assert.IsFalse(CrouchSqueezeUpMath.ShouldSqueezeUp(
                upHeld: true, crouching: false, grounded: true,
                headRowOpen: true, torsoRowSolid: true, torsoRowUnraised: false),
                "`_loc3_.zForm == 0` — a quarter-height form's phY1 is 10 px down inside the cell, so " +
                "snapping to it would put the feet inside the cell above");
        }

        // ── The direction guard ──────────────────────────────────────────────────────────────────

        [Test]
        public void LiftsUpward_AcceptsOneTile_AndRefusesDownwards()
        {
            Assert.IsTrue(CrouchSqueezeUpMath.LiftsUpward(40f, 80f), "one tile up is the mechanic");
            Assert.IsFalse(CrouchSqueezeUpMath.LiftsUpward(80f, 40f),
                "a downward 'lift' is what a sign error looks like, and it would drop the unit " +
                "through the floor it was standing on");
            Assert.IsFalse(CrouchSqueezeUpMath.LiftsUpward(80f, 80f), "no movement is not a mantle");
        }

        // ── Satisfiability: the destination is a real space ──────────────────────────────────────

        /// <summary>
        /// The lift is only meaningful if the crouched body actually fits the cell it is lifted into,
        /// and the standing body does not — otherwise the mechanic would either do nothing (the
        /// crouch is unnecessary) or wedge the player (the crouch is not enough). This is the
        /// arithmetic the port's <c>UpdateColliderProfile</c> then acts on: it holds the box at
        /// <c>sitHeight</c> precisely because <c>HasSolidOverlapAt(standing height)</c> is true.
        /// </summary>
        [Test]
        public void TheCrouchedBodyFitsTheCellTheMantleAimsAt_AndTheStandingBodyDoesNot()
        {
            const float ledgeTop = 80f;

            Assert.Less(CrouchedHeight, TileSize,
                "sitHeight 30 px must be under the 40 px cell, or a crouched player could not occupy " +
                "one and the whole crouch-through-a-gap mechanic is unreachable");

            float crouchedHead = ledgeTop + CrouchedHeight;
            Assert.LessOrEqual(crouchedHead, ledgeTop + TileSize,
                "the crouched body must stay inside the single cell above the ledge");

            float standingHead = ledgeTop + StandingHeight;
            Assert.Greater(standingHead, ledgeTop + TileSize,
                "and the standing body must NOT fit — that is what forces the crouch, and it is why " +
                "AS3's sit(true) at Unit.as:2115 is not redundant");
        }

        // ── Source lints ─────────────────────────────────────────────────────────────────────────
        //
        // The rule class can be perfect and the wiring wrong; a MonoBehaviour cannot be exercised in
        // the offline harness, so the wiring is pinned by reading it.

        private const string MotorPath = "Systems/Physics/TilePhysicsController.cs";

        private const string MotorInterfacePath = "Systems/Physics/IMovementMotor.cs";

        private const string LocomotionControllerPath = "Entities/Player/PlayerLocomotionController.cs";

        [Test]
        public void Lint_MotorProbesTheTorsoRow_AndNeverTheRowAboveTheHead()
        {
            string source = ReadStripped(MotorPath);
            string body = SourceLint.MethodBody(source, "private bool TrySqueezeUp(");

            Assert.That(body, Does.Contain("CrouchSqueezeUpMath.TorsoRow"),
                "the probe row must come from the rule, where the mirror is pinned");
            Assert.That(body, Does.Contain("CrouchSqueezeUpMath.HeadRow"),
                "the head row must come from the rule too — an inline floor() loses the origin term");
            Assert.That(body, Does.Not.Contain("headRow + 1"),
                "AS3's `+ 1` is one row DOWN in a Y-down grid; in this port's Y-up grid that is the " +
                "cell above the head, and the mantle would never fire");
            Assert.That(body, Does.Contain("standingCollisionHeight"),
                "the probe height must be the STANDING one — AS3's scY is stayY here because `!isSit` " +
                "is one of the arm's gates. Reading the current height would shrink the probe the " +
                "moment the unit crouched, and the arm would then measure a different row");
        }

        [Test]
        public void Lint_MotorRefusesAMantleWithNoHorizontalIntent()
        {
            string source = ReadStripped(MotorPath);
            string body = SourceLint.MethodBody(source, "private bool TrySqueezeUp(");

            Assert.That(body, Does.Contain("Mathf.Abs(moveX) < 0.001f"),
                "AS3's arm sits inside `dx + osndx < 0` / `> 0`, so it needs horizontal motion. " +
                "Without this the arm also runs for a unit already embedded in a wall, which is a " +
                "free teleport out of a stuck state the oracle does not have");
        }

        [Test]
        public void Lint_MotorRunsTheMantle_AndOnlyAfterTheCheapStepUp()
        {
            string source = ReadStripped(MotorPath);
            string body = SourceLint.MethodBody(source, "private void MoveSingleStep(");

            Assert.That(body, Does.Contain("TryStepUp(newX, hw, hhHorizontalCheck)"),
                "the porog step-up must still run");
            Assert.That(body, Does.Contain("TrySqueezeUp(newX, hw)"),
                "the mantle must be reachable from the step that is blocked, or it is dead code");
            Assert.That(body, Does.Contain("&& !TrySqueezeUp"),
                "the mantle must gate the wall push-back, not run beside it — otherwise the unit is " +
                "lifted onto the ledge and shoved back off it in the same sub-step");
        }

        [Test]
        public void Lint_MotorChecksTheDestinationBeforeMovingThere()
        {
            string source = ReadStripped(MotorPath);
            string body = SourceLint.MethodBody(source, "private bool TrySqueezeUp(");

            Assert.That(body, Does.Contain("CheckTileCollisionAt(attemptedX, targetFeetY"),
                "the port probes the column AHEAD, not the body's own edge, so the destination box " +
                "must be checked: a refusal has to mean 'no mantle', never 'teleported into a wall'");
            Assert.That(body, Does.Contain("CrouchSqueezeUpMath.LiftsUpward"),
                "a downward snap must be refused before the position is written");
            Assert.That(body, Does.Contain("isCrouching"),
                "the oracle's `!isSit` gate must reach the rule");
        }

        [Test]
        public void Lint_LocomotionPublishesTheUpLevel()
        {
            string source = ReadStripped(LocomotionControllerPath);

            Assert.That(source, Does.Contain("SetUpHeld(CrouchStateMath.UpHeld"),
                "the mantle needs `isUp`, and `isUp` is `ctr.keyBeUp` (UnitPlayer.as:2944). Without " +
                "this publish the motor's upHeld is permanently false and the whole arm is dead");

            string motor = ReadStripped(MotorPath);
            Assert.That(motor, Does.Contain("public void SetUpHeld("),
                "the interface member needs an implementation on the one motor");
        }

        [Test]
        public void Lint_TheMotorInterfaceCarriesUpSeparatelyFromTheLadder()
        {
            string source = ReadStripped(MotorInterfacePath);

            Assert.That(source, Does.Contain("void SetUpHeld(bool upHeld)"),
                "Up is the ladder's climb key AND the mantle's key AND the crouch's release key; " +
                "folding it into SetLadderInput would retune one mechanic by editing another");
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────

        private static string ReadStripped(string relativePath)
        {
            // `SourceLint.ReadStripped` strips comments AND strings and asserts its own positive
            // control (`///` gone) on every call — a lint searching raw text would pass on a file
            // whose call had been deleted, which is the failure these lints exist to avoid.
            return SourceLint.ReadStripped(relativePath);
        }
    }
}
