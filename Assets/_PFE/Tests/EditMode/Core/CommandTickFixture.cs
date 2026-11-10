using NUnit.Framework;
using PFE.Core.Input;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// Phase 2.3 command-tick fixture (F-03, §8 row 2.3): the command the player builds for
    /// tick N must carry <see cref="PlayerCommand.TickIndex"/> == N, not the pre-2.3 stub of 0.
    ///
    /// <para><b>Why a pure fixture, not a live <c>PlayerController</c> test.</b> The player's
    /// command-building path is inside a <c>MonoBehaviour</c> (ECall), so it cannot run in the
    /// offline harness. What <i>can</i> run is the pure command-building helper —
    /// <see cref="PlayerCommand.FromIntent"/> — which is the seam the player uses. This fixture
    /// pins that the tick index flows through the helper unchanged, so a recorded tape names the
    /// tick the command was for. The MonoBehaviour half (that the player passes
    /// <c>SimLoop.TickIndex + 1</c>) is verified by the owner in the editor.</para>
    /// </summary>
    [TestFixture]
    public sealed class CommandTickFixture
    {
        [Test]
        public void FromIntent_WithRealTickIndex_CarriesThatTick()
        {
            // The pre-2.3 stub was tickIndex: 0 for every command. The 2.3 contract is that the
            // command carries the session tick it applies to. Build a command for tick 42 and
            // assert the tick survives.
            var command = PlayerCommand.FromIntent(
                tickIndex: 42,
                slotIndex: 0,
                sequence: 100u,
                moveInputX: 0.5f,
                moveInputY: -0.25f,
                jumpHeld: true,
                runHeld: false,
                jumpPressed: true,
                dashPressed: false,
                dropThroughPressed: false,
                aimAngle: 45f);

            Assert.That(command.TickIndex, Is.EqualTo(42),
                "the command must carry the session tick it was built for, not the pre-2.3 stub of 0");
        }

        [Test]
        public void FromIntent_TickZero_IsNotSpecialCased()
        {
            // Tick 0 is a valid tick (the first tick of a session). The helper must not
            // special-case it — a command for tick 0 carries tick 0, just as a command for
            // tick N carries tick N.
            var command = PlayerCommand.FromIntent(
                tickIndex: 0,
                slotIndex: 0,
                sequence: 0u,
                moveInputX: 0f,
                moveInputY: 0f,
                jumpHeld: false,
                runHeld: false,
                jumpPressed: false,
                dashPressed: false,
                dropThroughPressed: false,
                aimAngle: 0f);

            Assert.That(command.TickIndex, Is.EqualTo(0),
                "tick 0 is a valid session tick and must be preserved as-is");
        }

        [Test]
        public void FromIntent_LargeTickIndex_IsPreserved()
        {
            // A long session reaches high tick indices. The helper must not truncate or
            // wrap — the tick is an int, so any int value is valid.
            const int largeTick = 1_000_000;
            var command = PlayerCommand.FromIntent(
                tickIndex: largeTick,
                slotIndex: 0,
                sequence: 0u,
                moveInputX: 0f,
                moveInputY: 0f,
                jumpHeld: false,
                runHeld: false,
                jumpPressed: false,
                dashPressed: false,
                dropThroughPressed: false,
                aimAngle: 0f);

            Assert.That(command.TickIndex, Is.EqualTo(largeTick),
                "a large tick index must survive the helper unchanged");
        }

        [Test]
        public void FromIntent_SlotIndex_IsPreserved()
        {
            // The slot index flows through the same helper. Single-player is slot 0, but the
            // helper must carry whatever slot it is given — the F-09 seam.
            var command = PlayerCommand.FromIntent(
                tickIndex: 10,
                slotIndex: 3,
                sequence: 0u,
                moveInputX: 0f,
                moveInputY: 0f,
                jumpHeld: false,
                runHeld: false,
                jumpPressed: false,
                dashPressed: false,
                dropThroughPressed: false,
                aimAngle: 0f);

            Assert.That(command.SlotIndex, Is.EqualTo((byte)3),
                "the slot index must flow through the helper unchanged");
        }
    }
}
