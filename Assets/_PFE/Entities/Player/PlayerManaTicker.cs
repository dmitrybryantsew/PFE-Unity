using UnityEngine;
using PFE.Core;
using PFE.Systems.RPG;

namespace PFE.Entities.Player
{
    /// <summary>
    /// Drives <see cref="CharacterStats.TickMana"/> — AS3's mana block, <c>UnitPlayer.as:1302-1361</c> —
    /// once per authoritative tick.
    ///
    /// <para><b>Why this is its own component.</b> AS3 puts the block in <c>UnitPlayer.step()</c>, the
    /// player's own frame. The port has no player tick to hang it on:</para>
    /// <list type="bullet">
    ///   <item><description><see cref="PFE.Entities.Units.UnitController.SimTick"/> is not virtual
    ///     <i>and</i> returns immediately when <c>_hasTilePhysics</c> is set — which it is for the
    ///     player, who is motor-driven. So the player never runs that method at all.</description></item>
    ///   <item><description><see cref="PFE.Entities.Units.UnitController.StepUnit"/> is the documented
    ///     hook for "extra work in this unit's step", but it is only reached from the two drivers that
    ///     a motor-driven unit skips.</description></item>
    ///   <item><description><see cref="PlayerLocomotionController"/> is the obvious alternative, but it
    ///     is <c>sealed</c> and runs on Unity's <c>FixedUpdate</c>, whose rate is not the sim's
    ///     30 Hz — so hanging mana regen there would make the rate depend on a project setting.</description></item>
    /// </list>
    ///
    /// <para>So it is a separate <see cref="ISimTickable"/> at
    /// <see cref="SimTickOrder.PlayerMotor"/> — the player's own step — registered through the same
    /// <c>AttachSimulation(clock, loop)</c> idiom as <c>PlayerActionInteractor</c>. Registering is
    /// <b>not</b> optional: an unregistered tickable silently never runs, which presents as "mana
    /// does not regenerate" rather than as an error.</para>
    ///
    /// <para><b>Legacy fallback.</b> With no <see cref="SimLoop"/> attached the sim owns nothing, so
    /// the block runs from <c>FixedUpdate</c> and says so once. The rate is then Unity's fixed step
    /// rather than AS3's 30 Hz, which makes regen and upkeep faster or slower than the oracle in
    /// proportion — the same trade <c>PlayerActionInteractor</c> documents for its hold timers.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerManaTicker : MonoBehaviour, ISimTickable
    {
        private CharacterStats _stats;
        private PlayerLocomotionController _locomotion;
        private PlayerTelekinesisController _telekinesis;

        private SimLoop _loop;
        private bool _registered;
        private bool _warnedLegacy;

        /// <summary>
        /// The player's own step. Mana is part of the player's frame in AS3, so it belongs at
        /// <see cref="SimTickOrder.PlayerMotor"/> and not with the generic units at
        /// <c>UnitsAndAi</c> — a pool the motor spends must be decremented before the motor reads
        /// it, not after.
        /// </summary>
        public int TickOrder => SimTickOrder.PlayerMotor;

        /// <summary>Binds the three collaborators. Called once by <see cref="PlayerController"/>.</summary>
        public void Construct(CharacterStats stats, PlayerLocomotionController locomotion,
                              PlayerTelekinesisController telekinesis)
        {
            _stats = stats;
            _locomotion = locomotion;
            _telekinesis = telekinesis;
        }

        /// <summary>Puts the mana block on the sim clock. Safe to call twice.</summary>
        public void Attach(SimClock clock, SimLoop loop)
        {
            if (loop == null)
            {
                return;
            }

            _loop = loop;
            if (!_registered)
            {
                _loop.Register(this);
                _registered = true;
            }
        }

        public void SimTick(int tickIndex)
        {
            Step();
        }

        private void FixedUpdate()
        {
            if (_registered)
            {
                return;
            }

            if (!_warnedLegacy)
            {
                _warnedLegacy = true;
                Debug.LogWarning(
                    "[PlayerManaTicker] No SimLoop attached, so the mana block runs on Unity's " +
                    "FixedUpdate instead of the sim clock. Regen and upkeep rates will differ from " +
                    "AS3's 30 Hz by the ratio of the fixed step to 1/30 s.");
            }

            Step();
        }

        private void OnDestroy()
        {
            // Without this SimLoop keeps a reference to a dead MonoBehaviour and ticks it forever,
            // which presents as "the sim gets slower the longer the session runs".
            if (_loop != null && _registered)
            {
                _loop.Unregister(this);
                _registered = false;
            }
        }

        /// <summary>
        /// Gathers this tick's state and hands it to the model. All the arithmetic lives in
        /// <see cref="CharacterStats.TickMana"/>; this method only answers "what is the player doing".
        /// </summary>
        private void Step()
        {
            if (_stats == null)
            {
                return;
            }

            var state = new CharacterStats.ManaTickState();

            if (_telekinesis != null && _telekinesis.IsHoldingObject && _telekinesis.HeldObject != null)
            {
                state.TelekinesisActive = true;

                // AS3's teleSqrtMassa is sqrt(massa) above telePorog and 0 at or below it
                // (UnitPlayer.as:1807-1813), so a prop light enough to be under the threshold is
                // free to hold. TelekinesisMath.HoldManaDrain applies the same rule; it is
                // re-derived here rather than called because that helper folds in teleMult and
                // this struct wants the raw term.
                float massa = _telekinesis.HeldObject.GetAs3Massa();
                state.TelekinesisSqrtMass =
                    massa > _stats.TelePorog ? Mathf.Sqrt(Mathf.Max(0f, massa)) : 0f;
            }

            if (_locomotion != null && _locomotion.IsLevitating)
            {
                state.Levitating = true;
                state.LevitatingUp = _locomotion.IsLevitatingUpward;
            }

            // AS3's third branch (UnitPlayer.as:1333) needs World.w.alicorn && isFly && keyRun &&
            // a turn key held. The port has no playable alicorn, which is also why alicornRunMana
            // had no destination before now. Left explicitly false so the branch is visibly
            // unimplemented rather than accidentally reachable.
            state.AlicornRunning = false;

            _stats.TickMana(state);
        }
    }
}
