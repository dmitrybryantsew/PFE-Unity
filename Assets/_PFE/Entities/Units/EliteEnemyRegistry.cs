using System;
using System.Collections.Generic;

namespace PFE.Entities.Units
{
    /// <summary>
    /// The live set of starred enemies, so the HUD can hang a star and a name over each one.
    ///
    /// <para><b>Why a registry and not a scene scan.</b> The nameplate is a HUD element that must be
    /// built when a unit is <i>promoted</i> — which happens inside <c>UnitController.ApplyPlacement</c>,
    /// long after the HUD exists and in a layer that must not know what a HUD is. A per-frame
    /// <c>FindObjectsByType</c> would be the alternative and would cost a scene walk every frame to find
    /// a handful of units. The same publish/subscribe shape as <c>ItemPickupFeedback.Published</c>, and
    /// the same reason: the producer is deep in the entity layer and the consumer is the interface.</para>
    ///
    /// <para><b>Only promoted units are listed.</b> <c>Unit.as:2886-2889</c> hides
    /// <c>hpBar.goldstar</c> for every unit whose <c>hero</c> is <c>&lt;= 0</c>, so an ordinary raider
    /// has no star and needs no label. Registration happens from
    /// <c>UnitController.ApplyHeroTier</c>, which is where the promotion is decided — not from
    /// <see cref="UnitController.HeroTier"/>, which also reports a <c>hero="1"</c> placement the oracle
    /// refuses to promote.</para>
    ///
    /// <para><b>Static state, and why it is safe.</b> The list holds <c>UnitController</c> references
    /// only for units that are alive and promoted. Both ends unregister — <c>OnDeath</c> and
    /// <c>OnDestroy</c> — and <see cref="Live"/> prunes anything Unity has destroyed in the meantime, so
    /// a missed unregister degrades to one stale entry rather than a <c>MissingReferenceException</c> per
    /// frame. <see cref="Reset"/> exists for a test that needs a clean session.</para>
    /// </summary>
    public static class EliteEnemyRegistry
    {
        private static readonly List<UnitController> _live = new List<UnitController>();

        /// <summary>Raised once per promoted unit, after its stats and name are final.</summary>
        public static event Action<UnitController> Registered;

        /// <summary>Raised when a promoted unit dies or is destroyed.</summary>
        public static event Action<UnitController> Unregistered;

        /// <summary>The promoted units that still exist. Never null; may be empty.</summary>
        public static IReadOnlyList<UnitController> Live
        {
            get
            {
                Prune();
                return _live;
            }
        }

        /// <summary>Add a promoted unit. Idempotent.</summary>
        public static void Register(UnitController unit)
        {
            if (unit == null || _live.Contains(unit)) return;

            _live.Add(unit);
            Registered?.Invoke(unit);
        }

        /// <summary>Remove a unit. Safe for one that was never promoted.</summary>
        public static void Unregister(UnitController unit)
        {
            if (unit == null) return;
            if (!_live.Remove(unit)) return;

            Unregistered?.Invoke(unit);
        }

        /// <summary>Drop every entry and every subscriber — for a test, or a new session.</summary>
        public static void Reset()
        {
            _live.Clear();
            Registered = null;
            Unregistered = null;
        }

        /// <summary>
        /// Drop entries Unity has destroyed without reaching <see cref="Unregister"/> — the fake-null
        /// case the rest of the entity layer already guards against.
        /// </summary>
        private static void Prune()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i] == null)
                {
                    _live.RemoveAt(i);
                }
            }
        }
    }
}
