using NUnit.Framework;
using PFE.Core;
using PFE.Core.Input;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// Phase 2.3 acceptance (F-03, §8 row 2.3, §11 item 3): the same <see cref="PlayerCommand"/>
    /// tape fed to two independent <see cref="SimLoop"/> instances must produce identical per-tick
    /// outputs — the host-replay determinism <c>F6</c> asks for, now reachable because 2.2 gave the
    /// slot meaning and 2.3 gave the command a real tick.
    ///
    /// <para><b>Why this is offline-runnable.</b> The loop path is pure C#: a bare
    /// <c>SimLoop</c> with a scripted <see cref="ISimTickable"/> that consumes commands from a
    /// <see cref="PlayerCommandBuffer"/> via <see cref="PlayerCommandBuffer.TryGetForTick"/> —
    /// the same call a host replaying a tape makes. No <c>MonoBehaviour</c>, no
    /// <c>Application.dataPath</c>, no ECall. This is the <c>PrototypeRuntime</c>-equivalent
    /// acceptance from row 2.1, now exercising the tick-aligned consumption seam.</para>
    ///
    /// <para><b>What is pinned.</b> (1) <see cref="SimLoop"/> has zero wall-time dependency in
    /// the command path — the scripted tickable reads only the tick index and the buffer, never
    /// <c>Time.deltaTime</c> or <c>Input.*</c>; (2) two loops fed the same tape produce identical
    /// state hashes per tick; (3) the buffer's <see cref="PlayerCommandBuffer.TryGetForTick"/>
    /// is the consumption mechanism, not <c>TryGetLatest</c> — proving the tick-aligned seam.</para>
    ///
    /// <para><b>Scope — corrected 2026-11-07.</b> This pins the <b>HOST replay consumer</b>, not
    /// the local single-player intent path. Phase 2.3 made <c>PlayerController</c> read its own
    /// freshly produced command back through <c>TryGetForTick</c> in the name of "exercising the
    /// same seam". Because that lookup is oldest-first and several render frames share one 30 Hz
    /// tick, a jump press on the second frame of a tick window read back a stale command and Space
    /// stopped reliably jumping. Local intent now calls <c>SetIntent(command)</c> on the command it
    /// just built; <c>TryGetForTick</c> belongs to a consumer replaying a tape that already has
    /// exactly one command per tick (see <c>PlayerCommandTape</c> and
    /// <c>PlayerCommandConsumptionLintTests</c>).</para>
    /// </summary>
    [TestFixture]
    public sealed class TwoSimLoopDeterminismTests
    {
        /// <summary>
        /// A scripted tickable that consumes the command for its tick from a
        /// <see cref="PlayerCommandBuffer"/> and accumulates a simple deterministic state.
        /// This is the offline stand-in for the real motor: it proves the command tape drives
        /// the loop identically on two independent instances.
        /// </summary>
        private sealed class CommandConsumer : ISimTickable
        {
            private readonly PlayerCommandBuffer _buffer;

            /// <summary>Accumulated state — the deterministic output of the tape.</summary>
            public float AccumulatedX;
            public float AccumulatedY;
            public int CommandsConsumed;
            public int CommandsMissed;

            public CommandConsumer(PlayerCommandBuffer buffer)
            {
                _buffer = buffer;
            }

            public int TickOrder => SimTickOrder.Input;

            public void SimTick(int tickIndex)
            {
                if (_buffer.TryGetForTick(tickIndex, out PlayerCommand command))
                {
                    AccumulatedX += command.MoveXFloat;
                    AccumulatedY += command.MoveYFloat;
                    CommandsConsumed++;
                }
                else
                {
                    CommandsMissed++;
                }
            }

            /// <summary>A deterministic hash of the accumulated state, for comparison.</summary>
            public ulong StateHash()
            {
                unchecked
                {
                    ulong hash = 14695981039346656037UL;
                    hash ^= (ulong)CommandsConsumed;
                    hash *= 1099511628211UL;
                    hash ^= (ulong)CommandsMissed;
                    hash *= 1099511628211UL;
                    hash ^= (ulong)AccumulatedX.GetHashCode();
                    hash *= 1099511628211UL;
                    hash ^= (ulong)AccumulatedY.GetHashCode();
                    hash *= 1099511628211UL;
                    return hash;
                }
            }
        }

        /// <summary>
        /// Builds a deterministic command tape: N commands with tick indices 0..N-1,
        /// varying intent so the accumulated state is sensitive to every command.
        /// </summary>
        private static PlayerCommand[] BuildTape(int length)
        {
            var tape = new PlayerCommand[length];
            for (int i = 0; i < length; i++)
            {
                tape[i] = PlayerCommand.FromIntent(
                    tickIndex: i,
                    slotIndex: 0,
                    sequence: (uint)i,
                    moveInputX: (float)System.Math.Sin(i * 0.1),
                    moveInputY: (float)System.Math.Cos(i * 0.1),
                    jumpHeld: (i % 3) == 0,
                    runHeld: (i % 5) == 0,
                    jumpPressed: (i % 7) == 0,
                    dashPressed: (i % 11) == 0,
                    dropThroughPressed: (i % 13) == 0,
                    aimAngle: i * 1.5f);
            }
            return tape;
        }

        [Test]
        public void SameTape_TwoSimLoops_ProduceIdenticalState()
        {
            const int tapeLength = 300;
            PlayerCommand[] tape = BuildTape(tapeLength);

            // Two independent loops, each with its own buffer and consumer. The buffer must be
            // large enough to hold the full tape — a 64-capacity ring would evict the commands
            // for early ticks before the loop reaches them.
            var bufferA = new PlayerCommandBuffer(tapeLength);
            var bufferB = new PlayerCommandBuffer(tapeLength);
            foreach (PlayerCommand command in tape)
            {
                bufferA.Enqueue(command);
                bufferB.Enqueue(command);
            }

            var loopA = new SimLoop(new SimClock(SimClock.CanonicalTicksPerSecond), null);
            var loopB = new SimLoop(new SimClock(SimClock.CanonicalTicksPerSecond), null);
            var consumerA = new CommandConsumer(bufferA);
            var consumerB = new CommandConsumer(bufferB);
            loopA.Register(consumerA);
            loopB.Register(consumerB);

            // Step both loops for the full tape.
            for (int i = 0; i < tapeLength; i++)
            {
                loopA.StepOnce();
                loopB.StepOnce();
            }

            Assert.That(consumerA.CommandsConsumed, Is.EqualTo(tapeLength),
                "every command on the tape must be consumed by tick-aligned lookup");
            Assert.That(consumerB.CommandsConsumed, Is.EqualTo(tapeLength));
            Assert.That(consumerA.CommandsMissed, Is.EqualTo(0),
                "no tick should miss its command — the tape covers every tick");
            Assert.That(consumerB.CommandsMissed, Is.EqualTo(0));

            Assert.That(consumerA.AccumulatedX, Is.EqualTo(consumerB.AccumulatedX).Within(0.0001f),
                "two loops fed the same tape must accumulate identically");
            Assert.That(consumerA.AccumulatedY, Is.EqualTo(consumerB.AccumulatedY).Within(0.0001f));
            Assert.That(consumerA.StateHash(), Is.EqualTo(consumerB.StateHash()),
                "the deterministic state hash must match across two independent SimLoop instances");
        }

        [Test]
        public void SameTape_TwoSimLoops_PerTickStatesMatch()
        {
            const int tapeLength = 100;
            PlayerCommand[] tape = BuildTape(tapeLength);

            var bufferA = new PlayerCommandBuffer(tapeLength);
            var bufferB = new PlayerCommandBuffer(tapeLength);
            foreach (PlayerCommand command in tape)
            {
                bufferA.Enqueue(command);
                bufferB.Enqueue(command);
            }

            var loopA = new SimLoop(new SimClock(SimClock.CanonicalTicksPerSecond), null);
            var loopB = new SimLoop(new SimClock(SimClock.CanonicalTicksPerSecond), null);
            var consumerA = new CommandConsumer(bufferA);
            var consumerB = new CommandConsumer(bufferB);
            loopA.Register(consumerA);
            loopB.Register(consumerB);

            // Step both loops and compare per-tick state.
            for (int tick = 0; tick < tapeLength; tick++)
            {
                loopA.StepOnce();
                loopB.StepOnce();

                Assert.That(consumerA.AccumulatedX, Is.EqualTo(consumerB.AccumulatedX).Within(0.0001f),
                    $"divergence at tick {tick}: AccumulatedX differs");
                Assert.That(consumerA.AccumulatedY, Is.EqualTo(consumerB.AccumulatedY).Within(0.0001f),
                    $"divergence at tick {tick}: AccumulatedY differs");
            }
        }

        [Test]
        public void TryGetForTick_IsTheConsumptionMechanism()
        {
            // Pin the HOST replay consumer's seam: it must use TryGetForTick, not TryGetLatest.
            // A consumer using TryGetLatest would consume the newest command regardless of tick,
            // which is the pre-2.3 stub behaviour. This scripted tickable is a stand-in for a host
            // replaying a finished tape — it is NOT the local single-player input path (which must
            // never read its own command back; see PlayerCommandConsumptionLintTests).
            const int tapeLength = 50;
            PlayerCommand[] tape = BuildTape(tapeLength);

            var buffer = new PlayerCommandBuffer(tapeLength);
            foreach (PlayerCommand command in tape)
            {
                buffer.Enqueue(command);
            }

            var loop = new SimLoop(new SimClock(SimClock.CanonicalTicksPerSecond), null);
            var consumer = new CommandConsumer(buffer);
            loop.Register(consumer);

            // Step only half the tape.
            for (int i = 0; i < tapeLength / 2; i++)
            {
                loop.StepOnce();
            }

            Assert.That(consumer.CommandsConsumed, Is.EqualTo(tapeLength / 2),
                "only the stepped ticks should have consumed their commands");
            Assert.That(consumer.CommandsMissed, Is.EqualTo(0),
                "the first half of the tape is fully covered");

            // The second half of the tape is still in the buffer, unconsumed.
            Assert.That(buffer.TryGetForTick(tapeLength - 1, out _), Is.True,
                "the unconsumed tail of the tape must still be buffered");
        }

        [Test]
        public void DifferentTapes_ProduceDifferentStates()
        {
            // Absent control: two loops fed *different* tapes must diverge. If this passes,
            // the determinism test above is meaningful — a tape that always produces the same
            // output regardless of content would make the identical-state assertion vacuous.
            const int tapeLength = 50;
            PlayerCommand[] tapeA = BuildTape(tapeLength);
            PlayerCommand[] tapeB = BuildTape(tapeLength);
            // Mutate one command on tape B.
            tapeB[10] = PlayerCommand.FromIntent(10, 0, 10u, 1f, 1f,
                true, true, true, true, true, 999f);

            var bufferA = new PlayerCommandBuffer(tapeLength);
            var bufferB = new PlayerCommandBuffer(tapeLength);
            foreach (PlayerCommand command in tapeA) bufferA.Enqueue(command);
            foreach (PlayerCommand command in tapeB) bufferB.Enqueue(command);

            var loopA = new SimLoop(new SimClock(SimClock.CanonicalTicksPerSecond), null);
            var loopB = new SimLoop(new SimClock(SimClock.CanonicalTicksPerSecond), null);
            var consumerA = new CommandConsumer(bufferA);
            var consumerB = new CommandConsumer(bufferB);
            loopA.Register(consumerA);
            loopB.Register(consumerB);

            for (int i = 0; i < tapeLength; i++)
            {
                loopA.StepOnce();
                loopB.StepOnce();
            }

            Assert.That(consumerA.StateHash(), Is.Not.EqualTo(consumerB.StateHash()),
                "different tapes must produce different states — the determinism test is not vacuous");
        }
    }
}
