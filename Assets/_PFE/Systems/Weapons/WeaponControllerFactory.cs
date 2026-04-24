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
    /// Pure C# — no MonoBehaviour. Instantiated and owned by PlayerWeaponLoadout.
    /// Enemies will use this factory too (future EnemyWeaponLoadout).
    ///
    /// AS3 mapping:
    ///   tip == 1       → WClub   → MeleeWeaponController
    ///   tip == 4       → WThrow  → ThrownWeaponController
    ///   tip == 5       → WMagic  → MagicWeaponController
    ///   Internal (0)   → WPunch  → UnarmedWeaponController  (punch/kick, no held vis)
    ///   everything else → Weapon → RangedWeaponController   (Guns=2, BigGun=3)
    /// </summary>
    public sealed class WeaponControllerFactory
    {
        private readonly PfeDebugSettings _debugSettings;
        private readonly IAmmoSource      _ammoSource;

        public WeaponControllerFactory(PfeDebugSettings debugSettings = null, IAmmoSource ammoSource = null)
        {
            _debugSettings = debugSettings;
            _ammoSource    = ammoSource;
        }

        /// <summary>
        /// Create a fully initialised controller for the given weapon definition.
        ///
        /// The returned controller owns its WeaponRuntimeState. Dispose() the
        /// controller when the weapon is unequipped to release reactive subscriptions.
        /// </summary>
        public IWeaponController Create(WeaponDefinition def)
        {
            if (def == null)
            {
                Debug.LogError("[WeaponControllerFactory] WeaponDefinition is null — cannot create controller.");
                return null;
            }

            var state = new WeaponRuntimeState(def);

            // Mirror Weapon.create() dispatch in AS3.
            // WeaponType.Internal (0) covers punch/unarmed (WPunch in AS3 — punch > 0 on node).
            // All ranged families (Guns=2, BigGun=3, Internal non-punch) → RangedWeaponController.
            // Punch detection could refine Internal further in Stage 5 using def.meleeType or a flag.
            IWeaponController controller = def.weaponType switch
            {
                WeaponType.Melee   => new MeleeWeaponController(state),
                WeaponType.Thrown  => new ThrownWeaponController(state),
                WeaponType.Magic   => new MagicWeaponController(state),
                WeaponType.Internal => new UnarmedWeaponController(state),
                _                  => new RangedWeaponController(state, _debugSettings, _ammoSource),
            };

            Debug.Log($"[WeaponControllerFactory] Created {controller.GetType().Name} for weapon '{def.weaponId}'.");
            return controller;
        }
    }
}
