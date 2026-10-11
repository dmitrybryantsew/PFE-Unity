using System;
using PFE.Entities.Player;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The checkpoint's walk-into area — AS3 <c>CheckPoint</c>'s own <c>Area</c> with
    /// <c>over = areaActivate</c> (<c>CheckPoint.as:90-92</c>).
    ///
    /// <para><b>Enter, not stay.</b> AS3's <c>Area.step()</c> fires <c>over</c> on the rising edge only:
    /// <c>if(this.active &amp;&amp; !this.preactive &amp;&amp; Boolean(this.over)) this.over()</c>
    /// (<c>Area.as:342-345</c>, with <c>preactive = active</c> latched at <c>:373</c>). That is
    /// <c>OnTriggerEnter2D</c>, and deliberately not <c>OnTriggerStay2D</c> — the difference is invisible
    /// for a fresh checkpoint, which is the only state that activates, but it is the difference between
    /// one call and sixty a second.</para>
    ///
    /// <para><b>Only the player counts.</b> A checkpoint's area keeps the default <c>tip = "gg"</c>
    /// (<c>Area.as:13</c>), and <c>step()</c>'s <c>gg</c> branch tests <c>loc.gg</c> alone
    /// (<c>:308-316</c>) — the player, not any unit. An enemy or a loose crate standing on a checkpoint
    /// must not activate it, so the filter is player-only rather than "any unit".</para>
    ///
    /// <para><b>It owns no state.</b> Whether the walk-in is allowed to do anything is
    /// <c>CheckpointRules.AreaActivates</c>, applied by the campaign; this component only reports that
    /// the player arrived. That keeps the rule offline-testable and stops a second copy of the
    /// <c>active == 0</c> guard from drifting out of step with the first.</para>
    /// </summary>
    [RequireComponent(typeof(BoxCollider2D))]
    public sealed class CheckpointAreaTrigger : MonoBehaviour
    {
        private Action _onPlayerEnter;

        /// <summary>
        /// Wires the arrival callback. Called once, by the presenter that creates this object; the
        /// callback re-reads everything it needs at call time, so nothing here caches checkpoint state.
        /// </summary>
        public void Initialize(Action onPlayerEnter)
        {
            _onPlayerEnter = onPlayerEnter;
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!IsPlayer(other)) return;
            _onPlayerEnter?.Invoke();
        }

        /// <summary>
        /// Whether a collider belongs to the player.
        ///
        /// <para>Tag first because that is what the rest of the codebase does
        /// (<c>DoorPropPresenter.EjectOverlappingUnits</c>, <c>AreaTriggerPresenter.IsPlayerCollider</c>),
        /// then the component as a fallback for a player object whose tag was never set. Deliberately
        /// <b>not</b> <c>UnitController</c>: that would match enemies, which AS3's <c>gg</c> branch
        /// excludes.</para>
        /// </summary>
        private static bool IsPlayer(Collider2D other)
        {
            if (other == null) return false;
            if (other.CompareTag("Player")) return true;

            return other.GetComponentInParent<PlayerController>() != null;
        }
    }
}
