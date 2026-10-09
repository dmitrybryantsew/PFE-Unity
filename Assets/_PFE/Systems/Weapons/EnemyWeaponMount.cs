using System.Collections.Generic;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Systems.Weapons.Controllers;
using UnityEngine;
using VContainer;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// A held weapon on an <b>enemy</b> — the NPC half of the weapon stack.
    ///
    /// <para><b>The gap this closes.</b> Every piece of the weapon pipeline already existed and was
    /// tested — <see cref="WeaponControllerFactory"/> builds the right controller from a
    /// <see cref="WeaponDefinition"/>, <see cref="IWeaponController"/> produces <c>ShotPlan</c>s, and
    /// <see cref="ProjectileSpawner"/> turns a plan into a projectile with its visual, flare and
    /// sound. <b>Exactly one caller existed: <c>PlayerWeaponLoadout</c>.</b> No enemy referenced the
    /// factory, the controller or the spawner, so an NPC's <c>alilight</c> bolt had no projectile, no
    /// muzzle flare and no report — the shot was resolved as a direct damage number by the brain and
    /// nothing was drawn or played. This class is the missing consumer.</para>
    ///
    /// <para><b>Why it is a component rather than more code in a brain.</b> The controller is
    /// disposable, owns timers, and must be fed the owner's hold point every tick — the same shape
    /// <c>PlayerWeaponLoadout</c> has. Duplicating that inside <c>AlicornBrain</c> would put a second
    /// copy of the firing order in the tree, and the order is load-bearing (muzzle point before Tick,
    /// flush after Tick, sound before spawn). One consumer, shared by every armed archetype.</para>
    ///
    /// <para><b>No presenter, so no muzzle child.</b> The player's round leaves the barrel tip that
    /// <c>WeaponPresenter</c> drew; an NPC has no held-weapon sprite rig, so
    /// <see cref="RangedWeaponController.MuzzleWorldPoint"/> is the hold point itself. That is the
    /// same fallback the controller documents for "the vis has not been drawn yet".</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EnemyWeaponMount : MonoBehaviour
    {
        /// <summary>
        /// The unit this weapon belongs to.
        ///
        /// <para><b>Resolved through <c>GetComponentInParent</c>, and that is not defensive
        /// coding.</b> A mount lives on its own child GameObject under the unit — one child per weapon,
        /// because a unit may carry several (<c>alilight</c> plus the tr3 <c>alipsy</c>) and
        /// <see cref="DisallowMultipleComponent"/> rules out stacking them on one GameObject. The unit
        /// itself is the parent, so the lookup has to walk up.</para>
        /// </summary>
        private UnitController _owner;

        private WeaponDefinition    _def;
        private IWeaponController   _controller;
        private ProjectileSpawner   _spawner;
        private ISoundService       _sound;
        private PfeDebugSettings    _debug;
        private PFE.Core.Rng.IRngService _rng;

        private Vector2 _aimTarget;
        private bool    _firing;
        private bool    _initialised;

        private readonly List<ShotPlan> _plans = new List<ShotPlan>(4);

        /// <summary>True when a controller was built and the weapon can actually fire.</summary>
        public bool IsArmed => _controller != null;

        /// <summary>The equipped definition, or null when unarmed.</summary>
        public WeaponDefinition Definition => _def;

        /// <summary>AS3 <c>currentWeapon</c> — the live controller, exposed for tests and the overlay.</summary>
        public IWeaponController Controller => _controller;

        /// <summary>
        /// Build the weapon. Safe to call with any of the services null: a null
        /// <paramref name="factory"/> (a bare test spawn, or a scene without the registration) leaves
        /// the mount unarmed and says so once, rather than throwing inside a spawn loop.
        /// </summary>
        public void Initialize(
            WeaponDefinition def,
            FactionType ownerFaction,
            IProjectileFactory factory,
            IObjectResolver resolver = null,
            ISoundService sound = null,
            PfeDebugSettings debug = null,
            PFE.Core.Rng.IRngService rng = null)
        {
            _owner = GetComponentInParent<UnitController>();
            _def   = def;
            _sound = sound;
            _debug = debug;
            _rng   = rng;

            if (def == null)
            {
                return;
            }

            if (factory == null)
            {
                Debug.LogWarning(
                    $"[EnemyWeaponMount] '{def.weaponId}' not equipped on '{name}': no IProjectileFactory. " +
                    "The enemy will fight without its weapon (no projectile, no flare, no report).");
                return;
            }

            // Ammo and mana are deliberately null: an NPC has no inventory and no mana pool in this
            // port. That is the documented "training / no tracking" state rather than a silent
            // downgrade — every ammo multiplier stays at its identity, which is AS3's own answer for a
            // unit whose `invent` has no matching round.
            var factoryBuilder = new WeaponControllerFactory(
                debugSettings: debug,
                ammoSource: null,
                rng: rng,
                statSource: null,
                ammoResolver: null,
                manaSource: null);

            _controller = factoryBuilder.Create(def, ownerFaction);
            if (_controller == null)
            {
                // WeaponControllerFactory returns null for a supportive spell (AS3 `weapon@spell`),
                // which is cast rather than held. Not a failure — say why and stay unarmed.
                Debug.LogWarning(
                    $"[EnemyWeaponMount] '{def.weaponId}' produced no controller (see the factory log above); " +
                    $"'{name}' stays unarmed.");
                return;
            }

            _spawner = gameObject.GetComponent<ProjectileSpawner>();
            if (_spawner == null)
            {
                _spawner = gameObject.AddComponent<ProjectileSpawner>();
            }

            _spawner.Initialize(factory, resolver, debug);
            _spawner.SetWeapon(def);

            // AS3 `Unit.as:3084-3099` — the noise a shot makes is announced by the shooter, and the
            // spawner reads it off `Owner` (`GetComponentInParent<UnitController>`). Without this the
            // bolt is silent to every other AI. The owner is the unit, not this child object.
            _spawner.Owner = _owner != null ? _owner.transform : transform;

            _initialised = true;
        }

        /// <summary>Aim the weapon at a world-space point (AS3 <c>celX</c>/<c>celY</c>).</summary>
        public void SetAimTarget(Vector2 worldTarget) => _aimTarget = worldTarget;

        /// <summary>
        /// Hold or release the trigger. Mirrors <c>PlayerWeaponLoadout.BeginAttack/EndAttack</c>, which
        /// is what the controllers latch as <c>_attackHeld</c>.
        /// </summary>
        public void SetFiring(bool firing) => _firing = firing;

        /// <summary>
        /// The weapon's world hold point — AS3 <c>Unit.setWeaponPos</c>, through the same
        /// <see cref="WeaponHoldPointMath"/> the player uses.
        ///
        /// <para>Reusing the shared rule rather than writing a second one is the point: the hold point
        /// is a function of the body box, the facing and the aim, and a private copy here would be the
        /// second place to get the axis flip wrong.</para>
        /// </summary>
        public Vector2 ResolveHoldPoint()
        {
            if (_owner == null) _owner = GetComponentInParent<UnitController>();
            if (_owner == null) return transform.position;

            UnitDefinition stats = _owner.Stats;
            var inputs = new WeaponHoldPointMath.Inputs
            {
                OwnerX     = _owner.transform.position.x,
                OwnerFeetY = _owner.FeetWorldY,
                BodyWidth  = stats != null && stats.Width  > 0f ? stats.Width  : 0.5f,
                BodyHeight = stats != null && stats.Height > 0f ? stats.Height : 0.7f,
                FacingSign = _owner.FacingDirection,
                AimX       = _aimTarget.x,
                Tip        = _def != null ? (int)_def.weaponType : 0,
            };

            return WeaponHoldPointMath.Resolve(inputs);
        }

        /// <summary>
        /// Advance the weapon one simulation tick and emit whatever it fired.
        ///
        /// <para><b>The call order is the contract</b> and it is copied from
        /// <c>PlayerWeaponLoadout.FixedUpdate</c>: muzzle point before <c>Tick</c> (because <c>Tick</c>
        /// is what consumes it), flush after <c>Tick</c>, sound before the spawn.</para>
        /// </summary>
        public void Tick(float dt)
        {
            if (!_initialised || _controller == null) return;

            Vector2 hold = ResolveHoldPoint();

            if (_controller is RangedWeaponController ranged)
            {
                ranged.MuzzleWorldPoint = hold;
            }

            if (_firing) _controller.BeginAttack();
            else         _controller.EndAttack();

            _controller.Tick(dt, hold, hold, _aimTarget);

            _plans.Clear();
            _plans.AddRange(_controller.FlushShotPlans());
            if (_plans.Count == 0) return;

            PlayShootSound(hold);
            _spawner?.SpawnFromPlans(_plans);
        }

        /// <summary>
        /// AS3 <c>Weapon.as:1619-1621</c> — <c>if(sndShoot != "" &amp;&amp; kol_shoot % sndShoot_n == 0)
        /// Snd.ps(sndShoot, X, Y)</c>. The <c>kol_shoot % n</c> throttle is not modelled (it only
        /// matters for a minigun's per-round report); one report per plan batch is what the player path
        /// already does.
        /// </summary>
        private void PlayShootSound(Vector2 hold)
        {
            if (_sound == null) return;

            for (int i = 0; i < _plans.Count; i++)
            {
                if (!_plans[i].Cues.PlayShootSound) continue;

                string id = _def != null ? _def.soundShoot : null;
                if (!string.IsNullOrEmpty(id))
                {
                    _sound.Play(id, hold);
                }
                return;
            }
        }

        private void OnDestroy()
        {
            _controller?.Dispose();
            _controller = null;
            _initialised = false;
        }
    }
}
