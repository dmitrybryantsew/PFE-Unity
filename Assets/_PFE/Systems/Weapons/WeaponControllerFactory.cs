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
        private readonly IWeaponStatSource _statSource;
        private readonly PFE.Core.Rng.IRngService _rng;

        /// <summary>
        /// Turns a weapon's live ammo id into its ballistics row, so the round's damage/pierce/armour/
        /// knock/fire/det/type terms reach the shot. Null keeps every ammo term at its identity — the
        /// same state a headless test or a training loadout has, and AS3's "неправильный патрон"
        /// branch. See <see cref="IAmmoResolver"/>.
        /// </summary>
        private readonly IAmmoResolver _ammoResolver;

        /// <summary>
        /// Live mana for the magic path, or null for "no tracking". Separate from
        /// <see cref="IWeaponStatSource"/> because mana is per-unit state the shot <i>mutates</i>,
        /// not a multiplier copied onto the weapon at equip time — see <see cref="IManaSource"/>.
        /// </summary>
        private readonly IManaSource _manaSource;

        public WeaponControllerFactory(PfeDebugSettings debugSettings = null, IAmmoSource ammoSource = null,
                                       PFE.Core.Rng.IRngService rng = null, IWeaponStatSource statSource = null,
                                       IAmmoResolver ammoResolver = null, IManaSource manaSource = null)
        {
            _debugSettings = debugSettings;
            _ammoSource    = ammoSource;
            _rng           = rng;
            _statSource    = statSource;
            _ammoResolver  = ammoResolver;
            _manaSource    = manaSource;
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

            // ── Supportive magic is not a held weapon ────────────────────────────────────────────
            //
            // AS3 dispatches on `tip` alone, and the nine spell items carry tip='5', so the raw
            // dispatch would put them in WMagic. AS3 never gets that far: UnitPlayer.changeWeapon
            // intercepts the selection first — `if(_loc3_.spell) { … invent.useItem(id); return; }`
            // (UnitPlayer.as:3627-3635) — so a spell is CAST, never equipped or fired.
            //
            // There is no Unity caster yet, so there is no correct controller to build. Returning
            // null is the honest answer, and the same one this method already gives for a null
            // definition. What must NOT happen is building a MagicWeaponController here: it would
            // fire a real (if harmless) projectile from an item that has no <char> body and no
            // mana cost at all — wrong behaviour that a play-test could easily read as progress.
            if (def.spell)
            {
                Debug.LogWarning(
                    $"[WeaponControllerFactory] '{def.weaponId}' is a supportive spell (AS3 weapon@spell), " +
                    "not a held weapon — no controller created. Spells are cast from the inventory. " +
                    "See docs/OnWeaponsSystemImplementation/14_MagicSystemAudit_2026-10-03.md §3.");
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
                    controller = new MeleeWeaponController(state, _statSource);
                    break;

                case WeaponType.Thrown:         // tip 4 → WThrow
                    // The ranged controller's three dependencies, plus the ammo source: WThrow.shoot()
                    // calls setBullet() (so the round's terms must reach the shot), reads
                    // `owner.weaponSkill` / `owner.mazil` for its launch speed and spread, and
                    // getAmmo() consumes a real inventory ITEM for the player (WThrow.as:270-273).
                    controller = new ThrownWeaponController(state, _statSource, _ammoResolver, _rng, _ammoSource);
                    break;

                case WeaponType.Magic:          // tip 5 → WMagic
                    // Takes the stat source too, for the `checkAvail` skill gate WMagic.attack() runs
                    // (WMagic.as:46-52) — the same gate ranged and melee already apply.
                    controller = new MagicWeaponController(state, _manaSource, _statSource);
                    break;

                case (WeaponType)PaintTip:      // tip 12 → WPaint — no Unity class, and no data uses it
                    Debug.LogWarning(
                        $"[WeaponControllerFactory] Weapon '{def.weaponId}' has tip=12 (AS3 WPaint), which has no " +
                        "Unity counterpart. Falling back to the ranged controller. " +
                        "See 13_WeaponTypeBehaviourAudit_2026-09-27.md §1.6.");
                    controller = new RangedWeaponController(state, _debugSettings, _ammoSource, _rng, _statSource, _ammoResolver);
                    break;

                default:                        // tip 0 (Internal), 2 (Guns), 3 (BigGun), and anything >= 6
                    controller = def.IsUnarmed  // punch > 0 → WPunch; tip alone never selects it
                        ? new UnarmedWeaponController(state, _statSource)
                        : new RangedWeaponController(state, _debugSettings, _ammoSource, _rng, _statSource, _ammoResolver);
                    break;
            }

            Debug.Log($"[WeaponControllerFactory] Created {controller.GetType().Name} for weapon '{def.weaponId}'.");
            return controller;
        }
    }
}
