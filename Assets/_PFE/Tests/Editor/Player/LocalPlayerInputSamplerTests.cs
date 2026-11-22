using System;
using System.Collections.Generic;
using MessagePipe;
using NUnit.Framework;
using UnityEngine;
using PFE.Core;
using PFE.Core.Input;
using PFE.Core.Messages;
using PFE.Entities.Player;

namespace PFE.Tests.Editor.Player
{
    /// <summary>
    /// Behavioural tests for the thread-F sampler — the class that owns every input subscription and
    /// the per-frame command build. <c>LocalPlayerInputSampler</c> is a plain C# class (no
    /// <c>MonoBehaviour</c>), so unlike the controller it left, these fixtures construct it directly
    /// and exercise the real fold/gate/latch logic; the two-line wiring fact on the component stays
    /// pinned by <c>PlayerCommandConsumptionLintTests</c>.
    /// </summary>
    [TestFixture]
    public sealed class LocalPlayerInputSamplerTests
    {
        private InputReader _input;
        private LocalPlayerInputSampler _sampler;
        private RecordingSink _sink;
        private PfeDebugSettings _debugSettings;

        // MessagePipe subscribers the sampler subscribes to; each records its handler so a test can
        // deliver a message exactly as the real broker would.
        private FakeSubscriber<AttackMessage> _attack;
        private FakeSubscriber<TeleportMessage> _teleport;
        private FakeSubscriber<InteractMessage> _interact;
        private FakeSubscriber<ReloadMessage> _reload;
        private FakeSubscriber<SpellCastMessage> _spellCast;
        private FakeSubscriber<SpellHotkeyMessage> _spellHotkey;
        private FakeSubscriber<GrenadeMessage> _grenade;
        private FakeSubscriber<PunchMessage> _punch;
        private FakeSubscriber<MagicWeaponMessage> _magicWeapon;

        [SetUp]
        public void SetUp()
        {
            _debugSettings = ScriptableObject.CreateInstance<PfeDebugSettings>();
            _input = new InputReader(
                new FakePublisher<JumpMessage>(),
                new FakePublisher<AttackMessage>(),
                new FakePublisher<InteractMessage>(),
                new FakePublisher<DashMessage>(),
                new FakePublisher<TeleportMessage>(),
                new FakePublisher<ReloadMessage>(),
                new FakePublisher<SpellCastMessage>(),
                new FakePublisher<MagicWeaponMessage>(),
                new FakePublisher<GrenadeMessage>(),
                new FakePublisher<PunchMessage>(),
                new FakePublisher<SpellHotkeyMessage>(),
                _debugSettings);

            _attack = new FakeSubscriber<AttackMessage>();
            _teleport = new FakeSubscriber<TeleportMessage>();
            _interact = new FakeSubscriber<InteractMessage>();
            _reload = new FakeSubscriber<ReloadMessage>();
            _spellCast = new FakeSubscriber<SpellCastMessage>();
            _spellHotkey = new FakeSubscriber<SpellHotkeyMessage>();
            _grenade = new FakeSubscriber<GrenadeMessage>();
            _punch = new FakeSubscriber<PunchMessage>();
            _magicWeapon = new FakeSubscriber<MagicWeaponMessage>();

            _sink = new RecordingSink();
            _sampler = new LocalPlayerInputSampler();
            _sampler.Construct(
                _input,
                _attack,
                _teleport,
                _interact,
                _reload,
                _spellCast,
                _spellHotkey,
                _grenade,
                _punch,
                _magicWeapon,
                _debugSettings,
                _sink,
                doubleTapWindowSeconds: 0.25f);
        }

        [TearDown]
        public void TearDown()
        {
            SimInteractCommandLatch.Reset();
            _sampler.Dispose();
            _input.Dispose();
            UnityEngine.Object.DestroyImmediate(_debugSettings);
        }

        // ── Gates and shaping ────────────────────────────────────────────────────

        [Test]
        public void SampleFrame_ZeroesMoveInput_WhenControlInactive()
        {
            _input.MoveInput.Value = new Vector2(1f, 0f);

            var frame = Sample(controlActive: false);

            Assert.That(frame.ProcessedMoveInput, Is.EqualTo(Vector2.zero));
            Assert.That(frame.ArmTakeWindow, Is.False, "a gated-out direction must not arm the loot window");
        }

        [Test]
        public void SampleFrame_ZeroesMoveInput_WhenWorldPaused()
        {
            _input.MoveInput.Value = new Vector2(0f, -1f);

            var frame = Sample(isWorldPaused: true);

            Assert.That(frame.ProcessedMoveInput, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void SampleFrame_PassesMoveInput_WhenActive()
        {
            _input.MoveInput.Value = new Vector2(0.5f, -1f);

            var frame = Sample();

            Assert.That(frame.ProcessedMoveInput.x, Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(frame.ProcessedMoveInput.y, Is.EqualTo(-1f).Within(1e-6f));
            Assert.That(frame.ArmTakeWindow, Is.True, "a held horizontal direction arms the loot window");
        }

        [Test]
        public void SampleFrame_NegatesHorizontalInput_WhenZaput()
        {
            _input.MoveInput.Value = new Vector2(1f, 0f);

            var frame = Sample(zaput: true);

            Assert.That(frame.ProcessedMoveInput.x, Is.EqualTo(-1f).Within(1e-6f));
        }

        [Test]
        public void SampleFrame_DoesNotNegate_WhenControlInactive()
        {
            // AS3 applies zaput at the read site, which sits behind ggControl: a locked-out player
            // has no intent to invert — and the gated vector is zero anyway.
            _input.MoveInput.Value = new Vector2(1f, 0f);

            var frame = Sample(controlActive: false, zaput: true);

            Assert.That(frame.ProcessedMoveInput, Is.EqualTo(Vector2.zero));
        }

        // ── Command build and tape ───────────────────────────────────────────────

        [Test]
        public void SampleFrame_NoMotor_NoCommandIsBuilt()
        {
            SimInteractCommandLatch.Raise(roomHash: 9, objectIndex: 3);

            var frame = Sample(motorDriven: false);

            Assert.That(frame.HasCommand, Is.False);
            Assert.That(_sampler.CommandBuffer.TryGetLatest(out _), Is.False,
                "the pre-split build lived inside the locomotion branch — a rig with no motor " +
                "neither builds nor tapes a command");
        }

        [Test]
        public void SampleFrame_MotorDriven_PushesOneCommandPerFrame()
        {
            _input.MoveInput.Value = new Vector2(1f, 0f);

            var first = Sample(commandTick: 10);
            var second = Sample(commandTick: 11);

            Assert.That(first.HasCommand, Is.True);
            Assert.That(second.HasCommand, Is.True);
            Assert.That(first.Command.TickIndex, Is.EqualTo(10));
            Assert.That(second.Command.TickIndex, Is.EqualTo(11));
            Assert.That(second.Command.Sequence, Is.EqualTo(first.Command.Sequence + 1),
                "the sequence counter advances once per built command");
        }

        [Test]
        public void SampleFrame_CarriesTheSampledIntent()
        {
            _input.MoveInput.Value = new Vector2(1f, 0f);
            _input.IsDashing.Value = true;
            _input.IsAttacking.Value = true;

            var frame = Sample(aimAngle: 42.5f, weaponKey: 17, teleportHeld: true,
                teleportCursorPixels: new Vector2(5f, 6f));

            PlayerCommand command = frame.Command;
            Assert.That(command.MoveXFloat, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(command.RunHeld, Is.True);
            Assert.That(command.FireHeld, Is.True, "the host-side mount fires while the level is set");
            Assert.That(command.AimAngle, Is.EqualTo(42.5f));
            Assert.That(command.WeaponKey, Is.EqualTo(17));
            Assert.That(command.TeleportHeld, Is.True);
            Assert.That(command.CursorWorldX, Is.EqualTo(5f));
            Assert.That(command.CursorWorldY, Is.EqualTo(6f));
        }

        [Test]
        public void SampleFrame_CarriesTheSuppressedContainerEdge()
        {
            SimInteractCommandLatch.Raise(roomHash: 0xABCDEF, objectIndex: 7);

            var frame = Sample();

            Assert.That(frame.Command.InteractPressed, Is.True);
            Assert.That(frame.Command.TargetRoomHash, Is.EqualTo(0xABCDEF));
            Assert.That(frame.Command.TargetObjectIndex, Is.EqualTo(7));

            var next = Sample();
            Assert.That(next.Command.InteractPressed, Is.False,
                "the latch is exactly-once: the following frame must not see the edge again");
        }

        [Test]
        public void SampleFrame_FoldsFramesOfOneTick_IntoOneTapeEntry()
        {
            // Two render frames share one command tick: the tape must hold ONE entry for that tick,
            // not two — the uplink sends the window verbatim.
            _input.MoveInput.Value = new Vector2(1f, 0f);
            Sample(commandTick: 5);
            _input.MoveInput.Value = new Vector2(0.25f, 0f);
            Sample(commandTick: 5);

            var buffer = _sampler.CommandBuffer;
            int entriesForTick = 0;
            foreach (PlayerCommand cmd in buffer.OldestFirst())
            {
                if (cmd.TickIndex == 5)
                {
                    entriesForTick++;
                }
            }

            Assert.That(entriesForTick, Is.EqualTo(1),
                "the per-tick fold replaces the buffered entry in place rather than duplicating it");
        }

        // ── Message → sink forwarding ────────────────────────────────────────────

        [Test]
        public void AttackMessage_ForwardsBothEdges()
        {
            _attack.Publish(new AttackMessage { IsStarted = true });
            _attack.Publish(new AttackMessage { IsStarted = false });

            Assert.That(_sink.AttackEdges, Is.EqualTo(new[] { true, false }));
        }

        [Test]
        public void ReloadMessage_ForwardsThePressEdgeOnly()
        {
            _reload.Publish(new ReloadMessage { IsStarted = false });
            Assert.That(_sink.Reloads, Is.EqualTo(0));

            _reload.Publish(new ReloadMessage { IsStarted = true });
            Assert.That(_sink.Reloads, Is.EqualTo(1));
        }

        [Test]
        public void GrenadeMessage_ForwardsTheHeldEdgeOnly()
        {
            _grenade.Publish(new GrenadeMessage { IsHeld = false });
            Assert.That(_sink.Grenades, Is.EqualTo(0));

            _grenade.Publish(new GrenadeMessage { IsHeld = true });
            Assert.That(_sink.Grenades, Is.EqualTo(1));
        }

        [Test]
        public void PunchMessage_SamplesTheRunKeyAtThePress()
        {
            _input.IsDashing.Value = true;
            _punch.Publish(new PunchMessage { IsHeld = true });

            Assert.That(_sink.Punches, Is.EqualTo(new[] { true }),
                "AS3 reads keyRun at the press (UnitPlayer.as:2379) — the kicked flag is sampled " +
                "where the message lands, not where the punch executes");
        }

        [Test]
        public void TeleportMessage_ForwardsBothEdges()
        {
            _teleport.Publish(new TeleportMessage { IsStarted = true });
            _teleport.Publish(new TeleportMessage { IsStarted = false });

            Assert.That(_sink.TeleportEdges, Is.EqualTo(new[] { true, false }));
        }

        [Test]
        public void InteractMessage_ForwardsBothEdges()
        {
            _interact.Publish(new InteractMessage { IsPressed = true });
            _interact.Publish(new InteractMessage { IsPressed = false });

            Assert.That(_sink.InteractEdges, Is.EqualTo(new[] { true, false }));
        }

        [Test]
        public void SpellAndMagicMessages_ForwardTheirEdges()
        {
            _magicWeapon.Publish(new MagicWeaponMessage { IsHeld = true });
            _spellCast.Publish(new SpellCastMessage { IsHeld = true });
            _spellHotkey.Publish(new SpellHotkeyMessage { Slot = 3, IsHeld = true });

            Assert.That(_sink.MagicWeapons, Is.EqualTo(1));
            Assert.That(_sink.DefendSpellEdges, Is.EqualTo(new[] { true }));
            Assert.That(_sink.SpellHotkeys, Is.EqualTo(new[] { 3 }));
        }

        [Test]
        public void Construct_Twice_ResubscribesWithoutDuplicatingDelivery()
        {
            // The player GameObject is injected twice (RegisterComponent + autoInjectGameObjects);
            // the sampler's guard must dispose the first subscription set so one published message
            // reaches the sink exactly once.
            _sampler.Construct(
                _input,
                _attack,
                _teleport,
                _interact,
                _reload,
                _spellCast,
                _spellHotkey,
                _grenade,
                _punch,
                _magicWeapon,
                _debugSettings,
                _sink,
                doubleTapWindowSeconds: 0.25f);

            _attack.Publish(new AttackMessage { IsStarted = true });

            Assert.That(_sink.AttackEdges, Is.EqualTo(new[] { true }),
                "a second Construct without disposal would deliver every message twice");
        }

        // ── Double-tap policy (static, time-explicit) ────────────────────────────

        [Test]
        public void HorizontalDoubleTap_SecondTapInsideWindow_Triggers()
        {
            float lastLeft = float.NegativeInfinity, lastRight = float.NegativeInfinity;
            bool wasLeft = false, wasRight = false;

            bool first = LocalPlayerInputSampler.DetectHorizontalDoubleTap(
                -1f, currentTime: 10f, windowSeconds: 0.25f,
                ref lastLeft, ref lastRight, ref wasLeft, ref wasRight);
            bool second = LocalPlayerInputSampler.DetectHorizontalDoubleTap(
                0f, currentTime: 10.1f, windowSeconds: 0.25f,
                ref lastLeft, ref lastRight, ref wasLeft, ref wasRight);
            bool third = LocalPlayerInputSampler.DetectHorizontalDoubleTap(
                -1f, currentTime: 10.2f, windowSeconds: 0.25f,
                ref lastLeft, ref lastRight, ref wasLeft, ref wasRight);

            Assert.That(first, Is.False, "the first tap has no prior tap to measure against");
            Assert.That(second, Is.False, "releasing the direction is not a tap");
            Assert.That(third, Is.True, "the second tap inside the window is the dash");
        }

        [Test]
        public void HorizontalDoubleTap_SecondTapOutsideWindow_DoesNotTrigger()
        {
            float lastLeft = float.NegativeInfinity, lastRight = float.NegativeInfinity;
            bool wasLeft = false, wasRight = false;

            LocalPlayerInputSampler.DetectHorizontalDoubleTap(
                1f, currentTime: 10f, windowSeconds: 0.25f,
                ref lastLeft, ref lastRight, ref wasLeft, ref wasRight);
            bool late = LocalPlayerInputSampler.DetectHorizontalDoubleTap(
                1f, currentTime: 11f, windowSeconds: 0.25f,
                ref lastLeft, ref lastRight, ref wasLeft, ref wasRight);

            Assert.That(late, Is.False);
        }

        [Test]
        public void DownDoubleTap_SecondTapInsideWindow_Triggers()
        {
            float lastDown = float.NegativeInfinity;
            bool wasDown = false;

            LocalPlayerInputSampler.DetectDownDoubleTap(
                -1f, currentTime: 20f, windowSeconds: 0.25f, ref lastDown, ref wasDown);
            LocalPlayerInputSampler.DetectDownDoubleTap(
                0f, currentTime: 20.1f, windowSeconds: 0.25f, ref lastDown, ref wasDown);
            bool third = LocalPlayerInputSampler.DetectDownDoubleTap(
                -1f, currentTime: 20.2f, windowSeconds: 0.25f, ref lastDown, ref wasDown);

            Assert.That(third, Is.True, "the oracle's keyDubSit drop-through");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private LocalPlayerInputSampler.SampledFrame Sample(
            bool controlActive = true,
            bool isWorldPaused = false,
            bool zaput = false,
            float aimAngle = 0f,
            ushort weaponKey = 0,
            bool motorDriven = true,
            int commandTick = 1,
            byte commandSlot = 0,
            bool teleportHeld = false,
            Vector2 teleportCursorPixels = default)
        {
            return _sampler.SampleFrame(
                controlActive: controlActive,
                isWorldPaused: isWorldPaused,
                zaput: zaput,
                aimAngle: aimAngle,
                weaponKey: weaponKey,
                motorDriven: motorDriven,
                commandTick: commandTick,
                commandSlot: commandSlot,
                teleportHeld: teleportHeld,
                teleportCursorPixels: teleportCursorPixels);
        }

        private sealed class RecordingSink : LocalPlayerInputSampler.IActionSink
        {
            public readonly List<bool> AttackEdges = new List<bool>();
            public readonly List<bool> TeleportEdges = new List<bool>();
            public readonly List<bool> InteractEdges = new List<bool>();
            public readonly List<bool> DefendSpellEdges = new List<bool>();
            public readonly List<int> SpellHotkeys = new List<int>();
            public readonly List<bool> Punches = new List<bool>();
            public int Reloads;
            public int Grenades;
            public int MagicWeapons;

            public void AttackEdge(bool started) => AttackEdges.Add(started);
            public void ReloadPressed() => Reloads++;
            public void GrenadePressed() => Grenades++;
            public void PunchPressed(bool kicked) => Punches.Add(kicked);
            public void TeleportEdge(bool started) => TeleportEdges.Add(started);
            public void InteractEdge(bool pressed) => InteractEdges.Add(pressed);
            public void MagicWeaponPressed() => MagicWeapons++;
            public void DefendSpellEdge(bool held) => DefendSpellEdges.Add(held);
            public void SpellHotkeyEdge(int slot, bool held) => SpellHotkeys.Add(slot);
        }

        private sealed class FakeSubscriber<T> : ISubscriber<T>
        {
            private IMessageHandler<T> _handler;

            public IDisposable Subscribe(IMessageHandler<T> handler, params MessageHandlerFilter<T>[] filters)
            {
                _handler = handler;
                return new FakeDisposable();
            }

            public void Publish(T message) => _handler?.Handle(message);
        }

        private sealed class FakePublisher<T> : IPublisher<T>
        {
            public void Publish(T message)
            {
            }
        }

        private sealed class FakeDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
