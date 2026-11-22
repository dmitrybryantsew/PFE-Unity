using UnityEngine;
using PFE.Core;
using PFE.Core.Input;
using PFE.Systems.Combat.Sats;
using PFE.Systems.Interaction;
using PFE.Systems.Physics;
using PFE.Systems.Weapons;

namespace PFE.Entities.Player
{
    /// <summary>
    /// The local player's <b>intent → subsystems</b> half, extracted from <c>PlayerController</c>
    /// (thread F of the wave-2 split): the one class that drives
    /// <see cref="PlayerLocomotionController"/>, <see cref="TilePhysicsController"/> (legacy path),
    /// <see cref="PlayerTelekinesisController"/> and <see cref="PlayerWeaponLoadout"/> from player
    /// intent — the per-frame <see cref="PlayerCommand"/> plus the discrete action edges the sampler
    /// forwards.
    ///
    /// <para><b>Why one executor.</b> A command must mean the same thing whoever applies it. Today only
    /// the local rig applies commands (<c>Update</c> executes the fresh frame the sampler just built);
    /// the host-side body for a remote peer (<c>PfeRemoteBody</c>) re-implements a subset. Driving
    /// both from this class — with the rig-specific bits behind <see cref="IPlayerContext"/> — is the
    /// wave-2 step that makes "works in singleplayer, desyncs in multiplayer" structurally harder:
    /// local and remote players then run through the same execution code.</para>
    ///
    /// <para><b>What stays behind.</b> The discrete edges keep AS3's immediate-action semantics (act on
    /// the press frame, not a tick boundary) — moving them into the command struct is a protocol
    /// change (wave 3), so the sampler forwards them here directly. The spell caster likewise keeps
    /// its own input subscription; only its S.A.T.S. cancel triggers moved here.</para>
    ///
    /// <para><b>Authority.</b> This class writes no <c>[Authoritative]</c> state: the gates it reads
    /// (cutscene lock, <c>attackForever</c>, S.A.T.S. state) come through <see cref="IPlayerContext"/>,
    /// and the motor mirrors it reports (<c>isGrounded</c>/<c>isRunning</c>) are the same writes the
    /// pre-split controller made from the same place in the frame.</para>
    /// </summary>
    public sealed class PlayerCommandExecutor : LocalPlayerInputSampler.IActionSink
    {
        /// <summary>
        /// The rig- and player-specific services the executor needs that are not components on the
        /// rig: authority gates, S.A.T.S. state, interact targeting, and the motor-state mirrors that
        /// land on <c>UnitController</c> fields. Implemented by <c>PlayerController</c> for the local
        /// rig; a host-side body implements it for a remote peer.
        /// </summary>
        public interface IPlayerContext
        {
            /// <summary>The player GameObject — the actor every interaction names.</summary>
            GameObject Self { get; }

            /// <summary>AS3 <c>attackForever</c> — the `vote` effect's lock on throwing and striking.</summary>
            float AttackForever { get; }

            /// <summary>Whether a S.A.T.S. plan is queued (the manual-fire swallow decision).</summary>
            bool SatsHasQueuedShots { get; }

            /// <summary>Whether the S.A.T.S. mode is open (planning clicks are not trigger pulls).</summary>
            bool SatsIsActive { get; }

            /// <summary>Drop any queued S.A.T.S. plan — the §3.11 ledger's player-side triggers.</summary>
            void InvalidateSats(SatsInvalidationReason reason);

            /// <summary>The interactable under the cursor within reach, if any (no nearest-target fallback).</summary>
            IInteractable FindCursorTarget();

            /// <summary>Begins a hold or acts at once; true when the target consumed the press.</summary>
            bool BeginOrPerformInteract(IInteractable target);

            /// <summary>The locomotion path placed the player: mirror its grounded/running state.</summary>
            void MirrorLocomotionState(bool isGrounded, bool isRunning);

            /// <summary>The legacy tile-physics path placed the player: mirror its grounded state.</summary>
            void MirrorLegacyGrounded(bool isGrounded);

            /// <summary>No motor at all: the original Rigidbody2D fallback for hand-built test rigs.</summary>
            void ApplyFallbackMotor(Vector2 processedMoveInput);
        }

        [LocalOnly(Note = "Wiring reference; the authority/SATS/targeting seam, never replicated")]
        private readonly IPlayerContext _context;

        [LocalOnly(Note = "Wiring reference; debug settings asset, never replicated")]
        private PfeDebugSettings _debugSettings;

        [LocalOnly(Note = "Wiring reference; primary motor, bound in Awake, never replicated")]
        private PlayerLocomotionController _locomotion;

        [LocalOnly(Note = "Wiring reference; telekinesis component, bound in Awake, never replicated")]
        private PlayerTelekinesisController _telekinesis;

        [LocalOnly(Note = "Wiring reference; weapon loadout, bound in Awake, never replicated")]
        private PlayerWeaponLoadout _loadout;

        [LocalOnly(Note = "Wiring reference; legacy fallback motor, bound in Awake, never replicated")]
        private TilePhysicsController _tilePhysics;

        [LocalOnly(Note = "Wiring reference; hold-to-act gesture runner, bound in Awake, never replicated")]
        private PlayerActionInteractor _actionInteractor;

        /// <summary>
        /// Frame of the last accepted interact press. The press arrives twice — once on the
        /// MessagePipe subscription and once from the direct poll in
        /// <see cref="LocalPlayerInputSampler.PollLegacyInteractEdges"/> — and this is what stops one
        /// key press acting twice, which for a door would open and close it in a single frame.
        /// </summary>
        [ClientPredicted(Note = "Input edge dedup; local acting-side state, predicted (F-03)")]
        private int _lastInteractPressFrame = -1;

        public PlayerCommandExecutor(IPlayerContext context)
        {
            _context = context;
        }

        /// <summary>Bind the debug settings. Called from <c>Construct</c> (either injection order).</summary>
        public void BindDebugSettings(PfeDebugSettings debugSettings)
        {
            _debugSettings = debugSettings;
        }

        /// <summary>
        /// Bind the rig's components. Called from <c>PlayerController.Awake</c> once every component
        /// the player assembles exists; safe to call again (idempotent re-bind).
        /// </summary>
        public void BindComponents(
            PlayerLocomotionController locomotion,
            PlayerTelekinesisController telekinesis,
            PlayerWeaponLoadout loadout,
            TilePhysicsController tilePhysics,
            PlayerActionInteractor actionInteractor)
        {
            _locomotion = locomotion;
            _telekinesis = telekinesis;
            _loadout = loadout;
            _tilePhysics = tilePhysics;
            _actionInteractor = actionInteractor;
        }

        /// <summary>
        /// Execute one render frame's sampled intent — the moved body of the old
        /// <c>PlayerController.HandleMovementInput</c>'s dispatch half. The command goes straight to
        /// the motor it was built for (never a tape read-back; the local consumer and the tape are fed
        /// from the same fresh frame, which <c>PlayerCommandConsumptionLintTests</c> pins).
        /// </summary>
        /// <param name="cursorWorldPixels">The cursor in world pixels, or null when no camera has been
        /// resolved yet — forwarding a placeholder would pin the telekinesis cursor to the room
        /// origin and make every grab miss.</param>
        public void Execute(in LocalPlayerInputSampler.SampledFrame frame, Vector2? cursorWorldPixels)
        {
            if (frame.HasCommand && _locomotion != null)
            {
                if (cursorWorldPixels.HasValue)
                {
                    // AS3: World.w.celX/celY — the point the teleport charge fires at and the
                    // telekinesis cursor. Forwarded only when a real cursor exists (see parameter doc).
                    _locomotion.SetCursorWorldPixels(cursorWorldPixels.Value);
                    _telekinesis?.SetCursorWorldPixels(cursorWorldPixels.Value);
                }

                PlayerCommand command = frame.Command;
                _locomotion.SetIntent(in command);
                _context.MirrorLocomotionState(
                    _locomotion.CurrentSnapshot.IsGrounded,
                    _locomotion.CurrentSnapshot.IsRunning);
                return;
            }

            if (_tilePhysics != null)
            {
                // Route input through tile physics controller (the legacy path predating locomotion).
                bool jump = UnityEngine.Input.GetKey(KeyCode.Space) || UnityEngine.Input.GetKey(KeyCode.W);
                bool down = UnityEngine.Input.GetKey(KeyCode.S);
                _tilePhysics.SetInput(frame.ProcessedMoveInput.x, jump, down);

                // Sync grounded state from tile physics
                _context.MirrorLegacyGrounded(_tilePhysics.IsGrounded);
                return;
            }

            // Fallback: original Rigidbody2D movement (a hand-built rig with neither motor).
            _context.ApplyFallbackMotor(frame.ProcessedMoveInput);
        }

        // ── Discrete action edges (AS3 immediate-action semantics: act on the press frame) ──────────

        /// <summary>Attack button edge — the moved <c>HandleAttackStart</c>/<c>HandleAttackEnd</c>.</summary>
        public void AttackEdge(bool started)
        {
            if (started)
            {
                HandleAttackStart();
            }
            else
            {
                HandleAttackEnd();
            }
        }

        /// <summary>Start attacking when attack button is pressed.</summary>
        private void HandleAttackStart()
        {
            // AS3 `:2334-2340` — a manual attack while a plan is standing cancels the plan **and is
            // itself swallowed** (`keyAttack = false`). So the click that ends a plan does not also
            // spend a round; the player who meant to cancel gets the cancel, not a shot they did not
            // aim. `_latchAttackEdge` in SatsController.Step2 covers the same trigger from the
            // mode's side, but that runs on a tick — by which time this handler has already fired.
            //
            // `!IsActive` is load-bearing: while the mode is open an attack press *queues a cell*
            // (Sats.as:181-201 consumes it first), so this must not treat a queued shot as a manual
            // one and wipe the plan the player is still building.
            if (_context.SatsHasQueuedShots && !_context.SatsIsActive)
            {
                _context.InvalidateSats(SatsInvalidationReason.ManualFire);
                return;
            }

            // Sats.step() consumes keyAttack while the mode is open (Sats.as:185-188): LMB queues a
            // cell and is NOT a manual trigger pull. The bootstrapper reads the same InputAction and
            // latches the cell, so this handler must not also latch `BeginAttack()` — otherwise an
            // ordinary planning click leaves _attackHeld down, and accepting the plan inherits a
            // held trigger that outlives the queue on an automatic weapon.
            if (_context.SatsIsActive) return;

            // If the mouse is aimed directly at an interactable within reach, interact instead of
            // attacking. Cursor only — this must not fall back to the nearest target, or a click meant
            // as a shot would be swallowed by whatever happens to be standing next to the player.
            //
            // Routed through the same hold/immediate decision as the interact key, so a Z door needs
            // its hold here too rather than opening on the click. There is deliberately no matching
            // release: a hold begun by a click simply runs to completion, or is abandoned by walking
            // away (the range guard). Letting go of the attack button cancelling an E-initiated hold
            // would be a surprising coupling.
            if (_context.BeginOrPerformInteract(_context.FindCursorTarget()))
            {
                return;
            }

            _loadout?.BeginAttack();
        }

        /// <summary>Stop attacking when attack button is released.</summary>
        private void HandleAttackEnd()
        {
            _loadout?.EndAttack();
        }

        /// <summary>R — the reload key press: magazine reload, or a radio throwable's detonator.</summary>
        public void ReloadPressed()
        {
            _loadout?.StartReload();
        }

        /// <summary>G — AS3 `keyGrenad`: throws the player's `throwWeapon`, a slot of its own.</summary>
        public void GrenadePressed()
        {
            // AS3 `:2186` — the grenade arm is gated on `this.attackForever <= 0`, so the `vote`
            // effect locks throwing out entirely while it runs. The gate is a *player control* rule,
            // not a weapon rule: the same `ThrowGrenade()` is what a script or a test calls.
            if (_context.AttackForever > 0)
            {
                if (_debugSettings != null && _debugSettings.LogWeaponLifecycle)
                {
                    Debug.Log("[PlayerCommandExecutor] GrenadePressed — refused: attackForever > 0 " +
                              "(AS3 UnitPlayer.as:2186).");
                }
                return;
            }

            // AS3 `:2188-2191` — `if(sats.que.length > 0) sats.clearAll();` sits *inside* the gated
            // block and *before* the throw, so a refused throw (the gate above) leaves the plan
            // standing while an accepted one drops it. Placing this above the gate instead would
            // cancel a plan during a `vote`, when the player did nothing they could have wanted.
            _context.InvalidateSats(SatsInvalidationReason.Grenade);

            bool threw = _loadout != null && _loadout.ThrowGrenade();
            if (_debugSettings != null && _debugSettings.LogWeaponLifecycle)
            {
                Debug.Log(threw
                    ? $"[PlayerCommandExecutor] GrenadePressed — throwing '{_loadout.ThrowWeapon?.State.Def.weaponId}'."
                    : "[PlayerCommandExecutor] GrenadePressed — no throwable in the grenade slot " +
                      "(AS3's `else { keyGrenad = false; }` arm).");
            }
        }

        /// <summary>F — AS3 `keyPunch`: the bare-hoof strike, a second weapon slot beside the one in hand.</summary>
        /// <param name="kicked">AS3's `keyRun` sampled at the press — the back kick versus the front strike.</param>
        public void PunchPressed(bool kicked)
        {
            // AS3 `:2377` gates the whole block on `attackForever <= 0` — the `vote` effect locks
            // striking out, the same gate the grenade arm honours (:2186).
            if (_context.AttackForever > 0)
            {
                if (_debugSettings != null && _debugSettings.LogWeaponLifecycle)
                    Debug.Log("[PlayerCommandExecutor] PunchPressed — refused: attackForever > 0 (UnitPlayer.as:2377).");
                return;
            }

            bool struck = _loadout != null && _loadout.Punch(kicked);

            if (_debugSettings != null && _debugSettings.LogWeaponLifecycle)
            {
                Debug.Log(struck
                    ? $"[PlayerCommandExecutor] PunchPressed — {(kicked ? "back kick" : "front strike")} (runHeld={kicked})."
                    : "[PlayerCommandExecutor] PunchPressed — no punch weapon built; nothing struck.");
            }
        }

        /// <summary>Q edge: telekinesis grab/drop and the locomotion's teleport charge latch.</summary>
        public void TeleportEdge(bool started)
        {
            TelekinesisTrace.Log(
                $"PlayerCommandExecutor got TeleportEdge started={started}  " +
                $"telekinesis={(_telekinesis != null ? "present" : "NULL")}  " +
                $"locomotion={(_locomotion != null ? "present" : "NULL")}");

            // AS3 `:2143-2147` — `clearAll` sits inside `if(!teleReady)`, i.e. on the press edge only,
            // not on every held frame. `teleReady` is the latch that makes one hold one teleport;
            // the message carries the same two edges, so `started` is the equivalent observation.
            if (started)
            {
                _context.InvalidateSats(SatsInvalidationReason.Teleport);
            }

            if (_telekinesis != null)
            {
                _telekinesis.OnTeleportKeyPressed(started);
            }

            if (_locomotion != null)
            {
                _locomotion.SetTeleportHeld(started);
            }
        }

        /// <summary>E / right-click edge — the moved <c>HandleInteract</c>/<c>EndInteract</c>.</summary>
        public void InteractEdge(bool pressed)
        {
            if (pressed)
            {
                HandleInteract();
            }
            else
            {
                EndInteract();
            }
        }

        /// <summary>
        /// The action key went down.
        /// </summary>
        private void HandleInteract()
        {
            // Guarded per frame. Both the MessagePipe subscription and the direct poll deliver the
            // same press, and an instant action must not run twice for one key press — which for a
            // door means open-then-close in a single frame.
            if (Time.frameCount == _lastInteractPressFrame) return;
            _lastInteractPressFrame = Time.frameCount;

            // AS3 UnitPlayer.as:2125-2129: if holding an object, Action key throws it.
            if (_telekinesis != null && _telekinesis.IsHoldingObject)
            {
                if (_telekinesis.TryThrow())
                {
                    return;
                }
            }

            // AS3 `:2117-2124` — `clearAll` sits inside the `if(!this.teleObj)` arm, *after* the
            // throw-what-you-hold branch above. So an Action press that throws a held prop leaves the
            // plan alone, while one that actually acts on the world drops it. The port's `teleObj` is
            // `_telekinesis.IsHoldingObject`, which is why this cannot move above that block.
            _context.InvalidateSats(SatsInvalidationReason.Interact);

            // A hold in flight owns the action: AS3 will not start a second while actionObj is set —
            // actAction() only re-checks the incumbent (:1924-1930).
            if (_actionInteractor != null && _actionInteractor.IsHolding) return;

            _context.BeginOrPerformInteract(_context.FindCursorTarget());
        }

        /// <summary>
        /// The action key came up: abandon any hold in flight. Nothing fires.
        ///
        /// <para>This is AS3's release path — a false <c>keyAction</c> nulls <c>actionObj</c> at
        /// <c>UnitPlayer.as:2131-2135</c> — and it is why letting go of E half-way through a Z door
        /// does nothing at all rather than finishing.</para>
        /// </summary>
        private void EndInteract()
        {
            _actionInteractor?.ReleaseHold();
        }

        /// <summary>T — AS3 `keyMagic`: the assault magic weapon is a separate slot; a press cancels the plan.</summary>
        public void MagicWeaponPressed()
        {
            _context.InvalidateSats(SatsInvalidationReason.MagicWeapon);
        }

        /// <summary>C — the defend spell (:2228-2231). The spell caster consumes the message itself.</summary>
        public void DefendSpellEdge(bool held)
        {
            if (!held) return;
            _context.InvalidateSats(SatsInvalidationReason.DefendSpell);
        }

        /// <summary>Any favourite-spell hotkey (:2259-2262). The spell caster consumes the message itself.</summary>
        public void SpellHotkeyEdge(int slot, bool held)
        {
            if (!held) return;
            _context.InvalidateSats(SatsInvalidationReason.SpellHotkey);
        }
    }
}
