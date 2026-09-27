using PFE.Data.Definitions;
using PFE.Core;
using PFE.Systems.Weapons.Controllers;
using PFE.Systems.Inventory;
using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Creates the correct IWeaponController for a WeaponDefinition.
    ///
    /// Mirrors Weapon.create() in AS3 — the same switch on tip / punch that
    /// decides which class (Weapon / WClub / WThrow / WMagic / WPunch) to instantiate.
    ///
    /// AS3 (Weapon.as:345-394), evaluated in this exact order — tip is tested BEFORE punch:
    ///   tip == 1        → WClub   → MeleeWeaponController
    ///   tip == 12       → WPaint  → no Unity counterpart (see below)
    ///   tip == 4        → WThrow  → ThrownWeaponController
    ///   tip == 5        → WMagic  → MagicWeaponController
    ///   punch > 0       → WPunch  → UnarmedWeaponController
    ///   everything else → Weapon  → RangedWeaponController
    ///
    /// The last two lines are the whole point of this class. **tip alone never selects WPunch** —
    /// `punch` is a separate attribute — and the base `Weapon` that everything else falls through
    /// to is a *ranged* weapon. So tip == 0 (WeaponType.Internal) means RANGED, not "unarmed";
    /// reading it as unarmed drove 57 of the 60 tip==0 weapons (every turret, drone laser, zombie
    /// spitter, alimray, robominigun, ttweap1..6) into the punch controller, which spawns no
    /// projectile and draws no sprite.
    /// See docs/OnWeaponsSystemImplementation/13_WeaponTypeBehaviourAudit_2026-09-27.md §1.
    ///
    /// tip == 12 (WPaint) never occurs in AllData.as — count is 0. AS3 only ever builds WPaint
    /// directly (UnitPlayer.as:432), and the port has no WPaint class, so it is reported loudly
    /// rather than silently mis-routed.
    ///
    /// Pure C# — no MonoBehaviour. Instantiated and owned by PlayerWeaponLoadout.
    /// Enemies will use this factory too (future EnemyWeaponLoadout).
    /// </summary>
    public sealed class WeaponControllerFactory
    {
        /// <summary>AS3 tip value that selects WPaint — not represented in <see cref="WeaponType"/>.</summary>
        private const int PaintTip = 12;

        private readonly PfeDebugSettings _debugSettings;
        private readonly IAmmoSource      _ammoSource;
        private readonly PFE.Core.Rng.IRngService _rng;

        public WeaponControllerFactory(PfeDebugSettings debugSettings = null, IAmmoSource ammoSource = null, PFE.Core.Rng.IRngService rng = null)
        {
            _debugSettings = debugSettings;
            _ammoSource    = ammoSource;
            _rng           = rng;
        }

        /// <summary>
        /// Create a fully initialised controller for the given weapon definition.
        ///
        /// The returned controller owns its WeaponRuntimeState. Dispose() the
        /// controller when the weapon is unequipped to release reactive subscriptions.
        /// </summary>
        /// <param name="ownerFaction">
        /// Faction of the unit equipping the weapon — the attacker side of
        /// <see cref="FactionRule"/>. Stamped onto the state here because this factory sees only a
        /// definition, while the caller that equips the weapon knows whose it is. Defaults to
        /// <see cref="PFE.Data.Definitions.FactionType.Neutral"/>, so an unowned weapon hits everyone
        /// rather than silently becoming friendly to one side.
        /// </param>
        public IWeaponController Create(
            WeaponDefinition def, PFE.Data.Definitions.FactionType ownerFaction = PFE.Data.Definitions.FactionType.Neutral)
        {
            if (def == null)
            {
                Debug.LogError("[WeaponControllerFactory] WeaponDefinition is null — cannot create controller.");
                return null;
            }

            var state = new WeaponRuntimeState(def);
            state.OwnerFaction = ownerFaction;

            // Mirror Weapon.create() dispatch in AS3 (Weapon.as:345-394), same precedence:
            // tip first, then punch, then the ranged base class.
            IWeaponController controller;
            switch (def.weaponType)
            {
                case WeaponType.Melee:          // tip 1 → WClub
                    controller = new MeleeWeaponController(state);
                    break;

                case WeaponType.Thrown:         // tip 4 → WThrow
                    controller = new ThrownWeaponController(state);
                    break;

                case WeaponType.Magic:          // tip 5 → WMagic
                    controller = new MagicWeaponController(state);
                    break;

                case (WeaponType)PaintTip:      // tip 12 → WPaint — no Unity class, and no data uses it
                    Debug.LogWarning(
                        $"[WeaponControllerFactory] Weapon '{def.weaponId}' has tip=12 (AS3 WPaint), which has no " +
                        "Unity counterpart. Falling back to the ranged controller. " +
                        "See 13_WeaponTypeBehaviourAudit_2026-09-27.md §1.6.");
                    controller = new RangedWeaponController(state, _debugSettings, _ammoSource, _rng);
                    break;

                default:                        // tip 0 (Internal), 2 (Guns), 3 (BigGun), and anything >= 6
                    controller = def.IsUnarmed  // punch > 0 → WPunch; tip alone never selects it
                        ? new UnarmedWeaponController(state)
                        : new RangedWeaponController(state, _debugSettings, _ammoSource, _rng);
                    break;
            }

            Debug.Log($"[WeaponControllerFactory] Created {controller.GetType().Name} for weapon '{def.weaponId}'.");
            return controller;
        }
    }
}
