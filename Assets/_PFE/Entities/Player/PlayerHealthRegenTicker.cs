using UnityEngine;
using PFE.Core;
using PFE.Systems.RPG;

namespace PFE.Entities.Player
{
    /// <summary>
    /// Drives <see cref="CharacterStats.TickHealthRegen"/> — the regeneration implant's per-frame
    /// top-up, AS3 <c>UnitPlayer.actions()</c>'s regen block (<c>UnitPlayer.as:963-967</c>) — once per
    /// authoritative tick.
    ///
    /// <para><b>Why this is its own component, like <see cref="PlayerManaTicker"/> and
    /// <see cref="PlayerRadiationTicker"/>.</b> AS3 puts the block in the player's own frame. The port
    /// has no player tick to hang it on: <c>UnitController.SimTick</c> returns immediately for a
    /// motor-driven unit (which the player is), and <c>PlayerLocomotionController</c> runs on Unity's
    /// <c>FixedUpdate</c>, whose rate is a project setting rather than AS3's 30 Hz. So this is an
    /// <see cref="ISimTickable"/> at <see cref="SimTickOrder.PlayerMotor"/>, registered through
    /// <c>AttachSimulation</c>. Registering is <b>not</b> optional: an unregistered tickable silently
    /// never runs, which presents as "the implant I bought does nothing".</para>
    ///
    /// <para><b>Order.</b> Registered <i>after</i> the radiation block, at the same order value — at
    /// equal order <c>SimLoop</c> runs in registration order, and AS3 has the radiation block, then
    /// <c>healhp</c>, then regen, all inside one <c>actions()</c>. Running regen first would top the
    /// player up and then let the same frame's exposure claw it back: a jittery bar instead of a
    /// smooth one.</para>
    ///
    /// <para><b>The arithmetic is not here.</b> It is <c>PFE.Sim.RPG.HealthRegenRules.Step</c>, which is
    /// Unity-free so the offline wall can pin it against the oracle's own expression. This class is the
    /// wiring: one call per tick, and a legacy <c>FixedUpdate</c> fallback that says so once.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerHealthRegenTicker : MonoBehaviour, ISimTickable
    {
        private CharacterStats _characterStats;

        private SimLoop _loop;
        private bool _registered;
        private bool _warnedLegacy;

        /// <summary>The player's own step — see the class remarks for why.</summary>
        public int TickOrder => SimTickOrder.PlayerMotor;

        /// <summary>
        /// Binds the stats. Called once by <see cref="PlayerController"/>. One collaborator is enough
        /// because <see cref="CharacterStats"/> already holds both halves of the oracle's expression:
        /// the implant's <c>regenFew</c>/<c>regenMax</c>, and — through <c>BoundUnitStats</c> and
        /// <c>RadiationProvider</c> — the hp and the <c>rad</c> ceiling.
        /// </summary>
        public void Construct(CharacterStats characterStats)
        {
            _characterStats = characterStats;
        }

        /// <summary>Puts the regen block on the sim clock. Safe to call twice.</summary>
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
            _characterStats?.TickHealthRegen();
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
                    "[PlayerHealthRegenTicker] No SimLoop attached, so the regeneration block runs on " +
                    "Unity's FixedUpdate instead of the sim clock. Health will regenerate at a rate set " +
                    "by the fixed step rather than AS3's 30 Hz.");
            }

            _characterStats?.TickHealthRegen();
        }

        private void OnDestroy()
        {
            if (_loop != null && _registered)
            {
                _loop.Unregister(this);
                _registered = false;
            }
        }
    }
}
