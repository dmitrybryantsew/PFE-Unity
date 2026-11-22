using System;
using R3;
using MessagePipe;
using UnityEngine;
using UnityEngine.InputSystem;
using PFE.Core;
using PFE.Core.Input;
using PFE.Core.Messages;

namespace PFE.Entities.Player
{
    /// <summary>
    /// The local player's <b>input → intent</b> half, extracted from <c>PlayerController</c> (thread F
    /// of the wave-2 split): the one class that subscribes to <see cref="InputReader"/>'s message
    /// stream and the legacy key polls, folds what they produced this frame into one
    /// tick-stamped <see cref="PlayerCommand"/>, and pushes it onto the replay tape.
    ///
    /// <para><b>Why a separate class at all.</b> The 2026-11-06 "Space no longer always jumps" defect
    /// was a wiring shape inside a <c>MonoBehaviour</c> that no offline fixture can instantiate. The
    /// tape's fold policy escaped into <see cref="PlayerCommandTape"/>; this class completes the
    /// extraction by moving every input subscription and every frame-sampling state out of the
    /// controller, so the only things left on the component are lifecycle and the two wiring lines a
    /// source lint pins (sample → execute, never a tape read-back).</para>
    ///
    /// <para><b>Two channels out, deliberately.</b> (1) The per-frame command stream — move axes,
    /// jump/run/dash/drop edges, aim, fire level, teleport level+cursor, and the suppressed-container
    /// interact edge — is what a tick consumer (local locomotion today; a host or predicting client
    /// tomorrow) executes. (2) The discrete message edges with AS3 immediate-action semantics
    /// (attack/reload/grenade/punch/teleport/interact/spell keys) forward straight to the
    /// <see cref="IActionSink"/> — AS3 acts on the press frame, not on a tick boundary, so these do
    /// not wait for the command build. The spell caster keeps its own <c>BindInput</c> subscription:
    /// casting is a component concern that wave 3 will route through the command stream.</para>
    ///
    /// <para><b>Authority.</b> Everything here is local input-sampling state
    /// (<c>[ClientPredicted]</c> — the peer's own intent, replayed for prediction) or wiring
    /// (<c>[LocalOnly]</c>). No field is host truth: the host learns this peer's intent exclusively
    /// from the commands those fields produce.</para>
    /// </summary>
    public sealed class LocalPlayerInputSampler : IDisposable
    {
        /// <summary>
        /// The consumer of the discrete action edges the message stream carries. Implemented by
        /// <c>PlayerCommandExecutor</c> for the local rig; the shape exists so the sampler needs no
        /// knowledge of what acting means.
        /// </summary>
        public interface IActionSink
        {
            /// <summary>Attack button edge. <c>true</c> on the press, <c>false</c> on the release.</summary>
            void AttackEdge(bool started);
            /// <summary>R — the reload key press (edge only; a release has no consumer).</summary>
            void ReloadPressed();
            /// <summary>G — the grenade key press (held edge of the message).</summary>
            void GrenadePressed();
            /// <summary>F — the punch key press. <paramref name="kicked"/> is AS3's <c>keyRun</c> at the press.</summary>
            void PunchPressed(bool kicked);
            /// <summary>Q edge — telekinesis grab/drop and the teleport charge latch.</summary>
            void TeleportEdge(bool started);
            /// <summary>E / right-click edge — <c>true</c> press, <c>false</c> release (release abandons a hold).</summary>
            void InteractEdge(bool pressed);
            /// <summary>T — the magic-weapon key press.</summary>
            void MagicWeaponPressed();
            /// <summary>C — the supportive-spell key edge (the spell caster consumes the message itself).</summary>
            void DefendSpellEdge(bool held);
            /// <summary>A favourite-spell hotkey edge (the spell caster consumes the message itself).</summary>
            void SpellHotkeyEdge(int slot, bool held);
        }

        /// <summary>
        /// One render frame's sampled intent. <see cref="HasCommand"/> is <c>false</c> when the rig has
        /// no <c>PlayerLocomotionController</c>: the legacy/fallback motor paths consume
        /// <see cref="ProcessedMoveInput"/> directly and no command is built or taped (the pre-split
        /// behaviour — the command build lived inside the locomotion branch).
        /// </summary>
        public struct SampledFrame
        {
            public SampledFrame(
                bool hasCommand,
                in PlayerCommand command,
                Vector2 processedMoveInput,
                bool armTakeWindow)
            {
                HasCommand = hasCommand;
                Command = command;
                ProcessedMoveInput = processedMoveInput;
                ArmTakeWindow = armTakeWindow;
            }

            /// <summary>Whether <see cref="Command"/> was built and pushed this frame.</summary>
            public bool HasCommand { get; }

            /// <summary>The command this frame built for its tick. Valid only when <see cref="HasCommand"/>.</summary>
            public PlayerCommand Command { get; }

            /// <summary>The move vector after the cutscene/pause gates and the <c>zaput</c> inversion.</summary>
            public Vector2 ProcessedMoveInput { get; }

            /// <summary>AS3's <c>isTake</c> arm: a horizontal direction held, or a raw jump edge this frame.</summary>
            public bool ArmTakeWindow { get; }
        }

        [LocalOnly(Note = "Wiring reference; injected input service, never replicated")]
        private InputReader _input;

        [LocalOnly(Note = "Wiring reference; injected debug settings asset, never replicated")]
        private PfeDebugSettings _debugSettings;

        [LocalOnly(Note = "Wiring reference; the action consumer the message edges forward to, never replicated")]
        private IActionSink _sink;

        [LocalOnly(Note = "Wiring reference; subscription bag, never replicated")]
        private CompositeDisposable _disposables;

        [LocalOnly(Note = "Config tuning; dash/drop double-tap window mirrored from the serialized field, never replicated")]
        private float _doubleTapWindowSeconds;

        // F-03 / Phase 2.1: the local player's intent as a tick-stamped command stream. The tape
        // holds the redundant last-N window a client would send (and replay for prediction); today it
        // is the single-player command tape. Single-player slot is always 0.
        private const int CommandBufferCapacity = 64;

        [ClientPredicted(Note = "One-command-per-tick replay tape; predicted (F-03)")]
        private readonly PlayerCommandTape _commandTape = new PlayerCommandTape(CommandBufferCapacity);

        [ClientPredicted(Note = "Command tape sequence counter; predicted (F-03)")]
        private uint _commandSequence;

        [ClientPredicted(Note = "Double-tap dash detection; local input-sampling state, predicted (F-03)")]
        private float _lastLeftTapTime = float.NegativeInfinity;

        [ClientPredicted(Note = "Double-tap dash detection; local input-sampling state, predicted (F-03)")]
        private float _lastRightTapTime = float.NegativeInfinity;

        [ClientPredicted(Note = "Double-tap drop-through detection; local input-sampling state, predicted (F-03)")]
        private float _lastDownTapTime = float.NegativeInfinity;

        [ClientPredicted(Note = "Dash edge tracking; local input-sampling state, predicted (F-03)")]
        private bool _wasLeftHeld;

        [ClientPredicted(Note = "Dash edge tracking; local input-sampling state, predicted (F-03)")]
        private bool _wasRightHeld;

        [ClientPredicted(Note = "Drop-through edge tracking; local input-sampling state, predicted (F-03)")]
        private bool _wasDownHeld;

        /// <summary>The local player's recent command window (redundant last-N). Read-only to callers.</summary>
        public PlayerCommandBuffer CommandBuffer => _commandTape.Buffer;

        /// <summary>
        /// Subscribe to the nine gameplay messages. Called from <c>PlayerController.Construct</c>, which
        /// receives the subscribers from the container; the controller itself no longer subscribes to
        /// any of them.
        /// </summary>
        /// <param name="doubleTapWindowSeconds">Mirrored from the player's serialized tuning field; the
        /// sampler owns no scene-serialized state of its own.</param>
        public void Construct(
            InputReader input,
            ISubscriber<AttackMessage> attackSubscriber,
            ISubscriber<TeleportMessage> teleportSubscriber,
            ISubscriber<InteractMessage> interactSubscriber,
            ISubscriber<ReloadMessage> reloadSubscriber,
            ISubscriber<SpellCastMessage> spellCastSubscriber,
            ISubscriber<SpellHotkeyMessage> spellHotkeySubscriber,
            ISubscriber<GrenadeMessage> grenadeSubscriber,
            ISubscriber<PunchMessage> punchSubscriber,
            ISubscriber<MagicWeaponMessage> magicWeaponSubscriber,
            PfeDebugSettings debugSettings,
            IActionSink sink,
            float doubleTapWindowSeconds)
        {
            _input = input;
            _debugSettings = debugSettings;
            _sink = sink;
            _doubleTapWindowSeconds = doubleTapWindowSeconds;

            // ── Construct really is called twice, and this guard is why that is harmless ─────────────
            //
            // The player GameObject is injected by two independent paths (RegisterComponent in
            // GameLifetimeScope AND the scene scope's autoInjectGameObjects), so this method runs twice
            // and every Subscribe below used to be registered twice: one TeleportMessage arrived as
            // two. Disposing the previous set makes the duplicate harmless whatever its source; the
            // warning keeps the underlying scene misconfiguration visible instead of hiding it behind
            // the guard. (Same history as the old controller-local guard — it moved here with the
            // subscriptions.)
            if (_disposables != null)
            {
                Debug.LogWarning(
                    "[LocalPlayerInputSampler] Construct() called more than once — the player component " +
                    "is injected twice (RegisterComponent AND autoInjectGameObjects both name the player " +
                    "GameObject). Re-subscribing; the previous MessagePipe subscriptions are disposed.");
                _disposables.Dispose();
            }

            _disposables = new CompositeDisposable();

            attackSubscriber.Subscribe(message =>
            {
                if (_debugSettings != null && _debugSettings.LogWeaponLifecycle)
                    Debug.Log($"[LocalPlayerInputSampler] AttackMessage received — IsStarted={message.IsStarted}.");
                if (message.IsStarted)
                    _sink?.AttackEdge(true);
                else
                    _sink?.AttackEdge(false);
            }).AddTo(_disposables);

            // R — the reload key. AS3's `keyReload` (inter/Ctr.as:24) drives two things from one
            // press: the magazine reload, and — for a radio throwable — `currentWeapon.detonator()`
            // (UnitPlayer.as:2358). Both live behind `IWeaponController.StartReload()`, which the
            // controller decides between using its own definition (`WeaponDefinition.radio`), so there
            // is nothing to branch on here.
            //
            // The press edge only. See ReloadMessage for why a release edge has no consumer.
            reloadSubscriber.Subscribe(message =>
            {
                if (!message.IsStarted) return;
                if (_debugSettings != null && _debugSettings.LogWeaponLifecycle)
                    Debug.Log("[LocalPlayerInputSampler] ReloadMessage received — forwarding to the loadout.");
                _sink?.ReloadPressed();
            }).AddTo(_disposables);

            // G — AS3 `keyGrenad` (UnitPlayer.as:2188-2204). Fires the player's `throwWeapon`, a slot
            // independent of the weapon in hand, which is why this is not routed through Attack.
            //
            // Press edge only, even though the message carries both edges. AS3 reads the held boolean
            // every frame, but the handler itself clears it after a successful throw, so one press is
            // one throw.
            grenadeSubscriber.Subscribe(message =>
            {
                if (!message.IsHeld) return;
                _sink?.GrenadePressed();
            }).AddTo(_disposables);

            // F — AS3 `keyPunch` (inter/Ctr.as:23), handled at UnitPlayer.as:2377-2385.
            //
            // A second weapon slot, not a mode of the one in hand: AS3 builds `punchWeapon` beside
            // `currentWeapon` and runs its `actions()` every frame, so hoofing something with a rifle
            // equipped is normal. The run key is read HERE, at the press, because that is where the
            // oracle reads it (`(punchWeapon as WKick).kick = this.ctr.keyRun`, :2379) — the sampler
            // owns the input read, so the kicked flag is sampled off InputReader at the message.
            punchSubscriber.Subscribe(message =>
            {
                if (!message.IsHeld) return;

                // Shift is AS3's `keyRun`. The port binds it as `Dash` (PfeInputSettings.dash) and the
                // locomotion block already reads it as `runHeld`, so this is the same signal, not a
                // second opinion about it.
                bool kicked = _input != null && _input.IsDashing.Value;
                _sink?.PunchPressed(kicked);
            }).AddTo(_disposables);

            teleportSubscriber.Subscribe(message =>
            {
                // Q does two things at once in AS3 and they are NOT mutually exclusive. While the key
                // is held, actTele() runs ONCE on the press edge (latched by `teleReady`) and the
                // teleport charge `t_port` increments on EVERY frame; on release, actPort() fires if
                // the charge reached portTime (UnitPlayer.as:2141-2176). The edge forwarding preserves
                // that: the executor drives telekinesis and the locomotion's teleport latch from the
                // same two edges.
                //
                // Delivery count, so "the input published two edges" and "the broker delivered two"
                // are distinguishable from each other in the flight log. The [INPUT] half lives in
                // InputReader; this is the receive half.
                TelekinesisRecorder.Write($"[DELIVER] TeleportMessage IsStarted={message.IsStarted}");

                _sink?.TeleportEdge(message.IsStarted);
            }).AddTo(_disposables);

            interactSubscriber.Subscribe(message =>
            {
                // Both edges carry: acting is a hold (AS3 keeps keyAction as a held boolean) and the
                // release abandons it. The direct E/right-click poll in PollLegacyInteractEdges backs
                // this stream and the executor dedups the two by frame.
                _sink?.InteractEdge(message.IsPressed);
            }).AddTo(_disposables);

            // ── The remaining §3.11 cancel triggers that arrive as messages ──────────────────────────
            //
            // Each of these is `if(sats.que.length > 0) sats.clearAll();` at the top of an otherwise
            // unrelated handler in UnitPlayer.as. They are subscribed here rather than folded into the
            // spell caster because the caster is a *component* that can be absent from a hand-built
            // rig, and a cancel trigger that disappears with an optional component is worse than one
            // that is slightly out of place.
            //
            // Note the cast subscriber is shared with `_spellCaster.BindInput`: MessagePipe delivers to
            // every handler on a subscriber, so both the cast and this cancel see the same message.
            magicWeaponSubscriber.Subscribe(message =>
            {
                if (!message.IsHeld) return;
                _sink?.MagicWeaponPressed();
            }).AddTo(_disposables);

            spellCastSubscriber.Subscribe(message =>
            {
                if (!message.IsHeld) return;
                _sink?.DefendSpellEdge(true);
            }).AddTo(_disposables);

            spellHotkeySubscriber.Subscribe(message =>
            {
                if (!message.IsHeld) return;
                _sink?.SpellHotkeyEdge(message.Slot, true);
            }).AddTo(_disposables);
        }

        /// <summary>
        /// Fold one render frame's input into the tick-stamped command stream — the moved body of the
        /// old <c>PlayerController.HandleMovementInput</c>, with the authority/effect bridges (forced
        /// aim, forced attack, S.A.T.S. observation) still owned by the controller and handed in as
        /// parameters.
        /// </summary>
        /// <param name="motorDriven">The rig has a <c>PlayerLocomotionController</c>. When false no
        /// command is built or taped — the pre-split build lived inside the locomotion branch and the
        /// legacy/fallback motors consume <see cref="SampledFrame.ProcessedMoveInput"/> instead.</param>
        /// <param name="teleportHeld">Read back from the locomotion (POST-telekinesis-steal key state:
        /// a grab that stole the key this frame already called <c>SetTeleportHeld(false)</c>).</param>
        /// <param name="teleportCursorPixels">The last cursor the camera forwarded, world pixels.</param>
        public SampledFrame SampleFrame(
            bool controlActive,
            bool isWorldPaused,
            bool zaput,
            float aimAngle,
            ushort weaponKey,
            bool motorDriven,
            int commandTick,
            byte commandSlot,
            bool teleportHeld,
            Vector2 teleportCursorPixels)
        {
            if (_input == null)
            {
                return default;
            }

            // AS3 `ggControl` gates the whole control block at `World.as:1379`. Substituting a zero
            // vector rather than early-returning keeps the motor told "no input" so it decelerates —
            // the interface pause gate joins ControlActive for the same reason (`World.as:1257` freezes
            // the room while the PipBuck or S.A.T.S. is open). Zero rather than return, so the motor
            // still decelerates instead of freezing mid-stride.
            Vector2 input = (controlActive && !isWorldPaused) ? _input.CurrentMoveInput : Vector2.zero;

            // AS3 `zaput` (UnitPlayer.as:93, read at :2085-2086) swaps the two movement keys at the
            // moment they are read. Negating the horizontal axis is the same fact for an analogue
            // stick, applied HERE, to the raw intent, so everything downstream sees one consistent
            // direction. It does NOT touch the aim: `zaput` inverts movement, not the camera.
            if (controlActive && zaput)
            {
                input.x = -input.x;
            }

            // Arm the loot-take window — AS3's `this.isTake = 40`, set while a horizontal direction is
            // HELD (UnitPlayer.as:2551, :2570) or on a jump press (:2685). It is assigned, not
            // accumulated, so holding a direction pins it at 40; armed here, on the frame boundary,
            // because a tick must not read Input — the tick only ages the value this sets.
            bool armTakeWindow =
                Mathf.Abs(input.x) > 0.1f ||
                _input.Jump != null && _input.Jump.WasPressedThisFrame();

            if (!motorDriven)
            {
                // The command build lived inside the locomotion branch before the split; a rig with
                // no locomotion keeps that shape — the legacy/fallback motors consume the processed
                // move input and nothing is taped.
                return new SampledFrame(
                    hasCommand: false,
                    command: default,
                    processedMoveInput: input,
                    armTakeWindow: armTakeWindow);
            }

            // Every edge and level below is gated on the same lock as the move vector: a cutscene that
            // let the player jump or dash would also break the animation the script is playing.
            bool jumpHeld = controlActive && _input.IsJumpHeld;
            bool runHeld = controlActive && _input.IsDashing.Value;
            bool jumpPressed = controlActive && _input.Jump != null && _input.Jump.WasPressedThisFrame();
            bool dashPressed = runHeld && DetectHorizontalDoubleTap(
                input.x, Time.unscaledTime, _doubleTapWindowSeconds,
                ref _lastLeftTapTime, ref _lastRightTapTime, ref _wasLeftHeld, ref _wasRightHeld);
            bool dropThroughPressed = DetectDownDoubleTap(
                input.y, Time.unscaledTime, _doubleTapWindowSeconds,
                ref _lastDownTapTime, ref _wasDownHeld);

            // F-03 / Phase 2.3: TickIndex is the session tick the command applies to — the next tick
            // the SimLoop steps (TickIndex is the last COMPLETED tick). With no SimLoop attached (the
            // legacy FixedUpdate path) it stays 0: there is no tick clock, so the tape degenerates to
            // one rolling entry and nothing replays it. The slot comes from the IPlayerSlotContext
            // seam (F-09); single-player resolves to slot 0, which the caller passes in.
            //
            // A suppressed container interact (a joined client pressing E on a chest — the local open
            // is replaced by the command edge) becomes this frame's InteractPressed. The latch is
            // consumed HERE so the edge rides the same command everything else does and the per-tick
            // tape fold keeps exactly-once delivery. A raised latch no build ever consumes is dropped,
            // which is the same answer a lost press gets.
            ulong interactRoomHash;
            int interactObjectIndex;
            bool interactPressed = SimInteractCommandLatch.TryConsume(
                out interactRoomHash, out interactObjectIndex);

            var command = PlayerCommand.FromIntent(
                tickIndex: commandTick,
                slotIndex: commandSlot,
                sequence: _commandSequence++,
                moveInputX: input.x,
                moveInputY: input.y,
                jumpHeld: jumpHeld,
                runHeld: runHeld,
                jumpPressed: jumpPressed,
                dashPressed: dashPressed,
                dropThroughPressed: dropThroughPressed,
                aimAngle: aimAngle,
                // Teleport is hold-to-charge, release-to-execute — a level plus the cursor the charge
                // will fire at. The level is the locomotion's POST-steal readback (parameter), so a
                // grab that stole the key this frame already suppresses the charge.
                teleportHeld: teleportHeld,
                cursorWorldX: teleportCursorPixels.x,
                cursorWorldY: teleportCursorPixels.y,
                // What is in hand rides every command idempotently as the interned weapon key: the
                // host cannot derive the peer's equipped weapon (it lives in the peer's inventory), so
                // the peer states it and the host converts the key back to the content hash at the
                // body boundary, stamping SimUnitState.WeaponIdHash for the clients exactly as when
                // the hash itself rode the command.
                weaponKey: weaponKey,
                // The interact edge, when one fired this frame: zero hash means "no target", which
                // the host-side ingress refuses — an edge never defaults to chest 0.
                interactPressed: interactPressed,
                targetRoomHash: interactRoomHash,
                targetObjectIndex: interactObjectIndex,
                // The attack level rides the command like every other level: the host-side weapon
                // mount on the peer's simulated body fires while the flag is set. What the peer holds
                // is intent the host cannot derive — the local rig's trigger state lives in the
                // InputReader, not in any Sim record.
                fireHeld: _input.IsAttackHeld);

            // The tape always contains this frame's intent for the tick — the per-tick fold replaces
            // the buffered entry in place rather than duplicating it (PlayerCommandTape).
            _commandTape.Push(command);

            return new SampledFrame(
                hasCommand: true,
                command: command,
                processedMoveInput: input,
                armTakeWindow: armTakeWindow);
        }

        /// <summary>
        /// The direct interact poll (E key or right mouse button) that backs the Interact message
        /// stream. Both edges are read because acting is a hold: the press begins it, the release
        /// abandons it. The press is gated on the cutscene lock — `control off` must also stop the
        /// player opening a door mid-script — while the release is deliberately NOT gated, so a lock
        /// that lands while the key is held still delivers the abandon and the interactor cannot stay
        /// stuck in its "acting" state. The executor dedups this stream against the message stream by
        /// frame, so the redundancy cannot double-fire.
        /// </summary>
        public void PollLegacyInteractEdges(bool controlActive)
        {
            bool ePressed = UnityEngine.Input.GetKeyDown(KeyCode.E) ||
                (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame);
            bool rightClick = UnityEngine.Input.GetMouseButtonDown(1) ||
                (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame);

            if (controlActive && (ePressed || rightClick))
            {
                _sink?.InteractEdge(true);
            }

            bool eReleased = UnityEngine.Input.GetKeyUp(KeyCode.E) ||
                (Keyboard.current != null && Keyboard.current.eKey.wasReleasedThisFrame);
            bool rightReleased = UnityEngine.Input.GetMouseButtonUp(1) ||
                (Mouse.current != null && Mouse.current.rightButton.wasReleasedThisFrame);

            if (eReleased || rightReleased)
            {
                _sink?.InteractEdge(false);
            }
        }

        /// <summary>
        /// Horizontal double-tap detection — AS3's `keyDub` dash (UnitPlayer.as:2642-2660). Kept a
        /// public static with explicit state parameters (like <see cref="PlayerCommandTape.Fold"/>) so
        /// fixtures exercise the exact rule without constructing the sampler or touching
        /// <see cref="Time"/>.
        /// </summary>
        public static bool DetectHorizontalDoubleTap(
            float horizontalInput,
            float currentTime,
            float windowSeconds,
            ref float lastLeftTapTime,
            ref float lastRightTapTime,
            ref bool wasLeftHeld,
            ref bool wasRightHeld)
        {
            const float threshold = 0.5f;
            bool dashTriggered = false;

            bool leftHeld = horizontalInput <= -threshold;
            bool rightHeld = horizontalInput >= threshold;

            if (leftHeld && !wasLeftHeld)
            {
                dashTriggered = currentTime - lastLeftTapTime <= windowSeconds;
                lastLeftTapTime = currentTime;
            }

            if (rightHeld && !wasRightHeld)
            {
                dashTriggered = currentTime - lastRightTapTime <= windowSeconds;
                lastRightTapTime = currentTime;
            }

            wasLeftHeld = leftHeld;
            wasRightHeld = rightHeld;
            return dashTriggered;
        }

        /// <summary>Down double-tap detection — AS3's `keyDubSit` drop-through (UnitPlayer.as:2917-2925).
        /// Static with explicit state for the same reason as <see cref="DetectHorizontalDoubleTap"/>.</summary>
        public static bool DetectDownDoubleTap(
            float verticalInput,
            float currentTime,
            float windowSeconds,
            ref float lastDownTapTime,
            ref bool wasDownHeld)
        {
            const float threshold = 0.5f;
            bool downHeld = verticalInput <= -threshold;
            bool dropTriggered = false;

            if (downHeld && !wasDownHeld)
            {
                dropTriggered = currentTime - lastDownTapTime <= windowSeconds;
                lastDownTapTime = currentTime;
            }

            wasDownHeld = downHeld;
            return dropTriggered;
        }

        /// <summary>Drop the MessagePipe subscriptions. Called from the player's <c>OnDestroy</c>.</summary>
        public void Dispose()
        {
            _disposables?.Dispose();
            _disposables = null;
        }
    }
}
