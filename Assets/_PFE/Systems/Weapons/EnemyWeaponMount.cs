using System.Collections.Generic;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Entities.Weapons;
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
    /// <para><b>It now owns the held-weapon sprite too.</b> A <c>vis</c> child carrying a
    /// <see cref="SpriteRenderer"/> and a <see cref="WeaponPresenter"/> is built beside the controller,
    /// so an NPC's melee weapon is <i>drawn</i> rather than only swung. The presenter is the same one
    /// the player path uses, deliberately: for a <c>krep == 0</c> melee weapon the port's ranged flip
    /// rule and the oracle's melee rule are the <b>same world transform</b>
    /// (<c>R(rot+180)·S(−1,1) = R(rot)·S(1,−1)</c>, see <see cref="WeaponPresenter"/>), so no second
    /// rule is needed and the two paths cannot drift.</para>
    ///
    /// <para><b>Still approximate:</b> the oracle positions a raider's weapon from the per-animation-frame
    /// <c>wPos</c> bone table (<c>UnitRaider.setWeaponPos</c>, <c>BlitAnim.wPosRaider1/2</c>), which is
    /// <b>not ported</b> — the weapon therefore sits at the body hold point rather than the animated
    /// hand. That is a position offset, not a missing sprite, and is tracked as a follow-up.</para>
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

        /// <summary>
        /// The swept hit volume for a <c>tip == 1</c> melee weapon, or null for every other type.
        ///
        /// <para><b>Why a mount needs one at all.</b> <see cref="MeleeWeaponController"/> does not spawn
        /// anything — it produces a <see cref="ShotKind.MeleeSweep"/> plan and pushes the swept segment
        /// into an <see cref="IMeleeHitVolume"/> it was handed, and <c>ProjectileSpawner</c> explicitly
        /// ignores that plan kind. So a melee weapon with no volume attached swings, plays its sound and
        /// damages <i>nothing</i>, silently: the controller's <c>HitVolume?</c> calls are all null
        /// conditional. On the player path <c>PlayerWeaponLoadout</c> supplies the volume from a scene
        /// child; an enemy built with <c>AddComponent</c> has no scene child to point at, which is the
        /// same missing-consumer shape <see cref="EnemyWeaponMount"/> itself was created to close.</para>
        /// </summary>
        private MeleeHitVolume _meleeVolume;

        /// <summary>
        /// The held-weapon sprite driver — the NPC counterpart of the component
        /// <c>PlayerWeaponLoadout</c> owns on the player.
        ///
        /// <para><b>Why the mount needs one.</b> Everything else on the enemy weapon path existed
        /// (controller, hit volume, projectile spawner, report) but nothing ever drew the weapon: an
        /// NPC's melee weapon swung at empty air and a gun fired from an invisible barrel. This is the
        /// missing renderer.</para>
        /// </summary>
        private WeaponPresenter _presenter;

        private Vector2 _aimTarget;
        private bool    _hasAimTarget;
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
        /// Throws left on this mount's NPC counter, or <c>-1</c> when the weapon is not thrown (or is
        /// player-owned).
        ///
        /// <para>Surfaced because AS3 acts on it: <c>UnitRaider.attack()</c> flips
        /// <c>attackerType</c> to 0 the moment a <c>WThrow</c>'s <c>kolAmmo</c> empties
        /// (<c>UnitRaider.as:1546-1549</c>), i.e. a raider that has thrown its four grenades stops being a
        /// thrower and charges instead of standing at range holding an empty hand.</para>
        /// </summary>
        public int RemainingThrows =>
            _controller is ThrownWeaponController thrown ? thrown.RemainingThrows : -1;

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
            //
            // `npcOwner: true` is NOT decoration: it selects AS3's non-player half of
            // `WThrow.getAmmo()` (the unit's own four-round `kolAmmo` counter). Without it a thrown
            // weapon fell into the port's *training* branch and an enemy grenade thrower never ran out.
            var factoryBuilder = new WeaponControllerFactory(
                debugSettings: debug,
                ammoSource: null,
                rng: rng,
                statSource: null,
                ammoResolver: null,
                manaSource: null,
                npcOwner: true);

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

            // The melee half. Built here rather than in the archetype for the same reason the ranged
            // half is: one consumer, shared by every armed archetype, and the controller's own
            // `HitVolume` contract is what the player path already satisfies.
            if (_controller is MeleeWeaponController melee)
            {
                BuildMeleeHitVolume(melee, resolver);
            }

            // The drawing half. Built last, once the controller exists, because the presenter reads
            // its state every frame and a state that does not yet exist would be a null deref on the
            // first LateUpdate.
            BuildHeldWeaponVisual(def);

            _initialised = true;
        }

        /// <summary>
        /// Give the mount the held-weapon sprite the player path gets from <c>PlayerWeaponLoadout</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The shape is the player rig's, copied deliberately.</b> A <c>vis</c> child holds the
        /// <see cref="SpriteRenderer"/> and the <see cref="WeaponPresenter"/>; the presenter writes the
        /// flip and the aim rotation on the transform of the renderer it finds, and that node has to be
        /// the sprite's own pivot, not the mount — see <see cref="WeaponPresenter"/>'s class doc for the
        /// pivot jump writing them on the wrong node causes.</para>
        ///
        /// <para><b>Order inside this method is load-bearing.</b> <c>AddComponent&lt;WeaponPresenter&gt;</c>
        /// runs <c>Awake</c> synchronously on a live GameObject, and that <c>Awake</c> resolves the
        /// renderer with <c>GetComponentInChildren</c> and never retries. The renderer must therefore
        /// exist before the presenter is added.</para>
        ///
        /// <para><b>No <c>muzzle</c> child.</b> <c>WeaponPresenter</c> publishes the barrel tip from the
        /// visual definition's <c>muzzleLocalOffset</c>, not from a named child, so the player rig's
        /// empty <c>muzzle</c> node is not needed here.</para>
        /// </remarks>
        private void BuildHeldWeaponVisual(WeaponDefinition def)
        {
            if (_controller == null || def == null) return;

            var visGo = new GameObject("vis");
            visGo.transform.SetParent(transform, false);
            visGo.AddComponent<SpriteRenderer>();

            _presenter = visGo.AddComponent<WeaponPresenter>();
            _presenter.SetState(_controller.State, def, IsFixedMeleeMount());
            _presenter.SetAimTarget(ResolveIdleAimTarget());
        }

        /// <summary>
        /// AS3 <c>owner.weaponKrep &gt; 0</c> — read off the unit, not the weapon.
        ///
        /// <para><c>Weapon.as:499-501</c> copies <c>owner.weaponKrep</c> onto the weapon at construction,
        /// and <c>Unit.as:332</c> declares it <b>1</b>, overwriting it only when <c>&lt;comb krep&gt;</c>
        /// is present. The port's <c>UnitDefinition.isStable</c> is that attribute (<c>krep == 1</c>).
        /// One recorded divergence: the importer writes <c>isStable</c> only when the attribute exists,
        /// so "absent" reads <c>false</c> where the oracle would answer 1 — which matters for a unit
        /// that carries a melee weapon <i>and</i> omits <c>krep</c>. Every raider/slaver that holds a
        /// <c>tip &lt;= 1</c> weapon carries it explicitly, so this family is exact.</para>
        /// </summary>
        private bool IsFixedMeleeMount()
        {
            if (_owner == null) _owner = GetComponentInParent<UnitController>();
            UnitDefinition stats = _owner != null ? _owner.Stats : null;
            return stats != null && stats.isStable;
        }

        /// <summary>
        /// Give a melee controller the swept trigger it needs, on a child of this mount.
        /// </summary>
        /// <remarks>
        /// <para><b>The shape is copied from <c>PlayerWeaponLoadout</c>, deliberately.</b> A child
        /// GameObject (the collider has to be somewhere the controller can move independently of the
        /// unit's body), a <see cref="MeleeHitVolume"/> on it — whose <c>[RequireComponent]</c> supplies
        /// the <c>CapsuleCollider2D</c> that <c>BindMove</c> resizes — an explicit
        /// <c>resolver.Inject</c> because a runtime <c>AddComponent</c> is never seen by VContainer, and
        /// <c>SetActive(false)</c> so the volume is cold until the first strike window opens.</para>
        ///
        /// <para><b>No <c>_spawner</c> involvement.</b> <see cref="ProjectileSpawner"/> ignores
        /// <see cref="ShotKind.MeleeSweep"/> outright, so the volume is the whole delivery path and the
        /// plan's only job is to carry the <c>DamageContext</c> — which <see cref="Tick"/> forwards,
        /// exactly as <c>PlayerWeaponLoadout.FixedUpdate</c> does.</para>
        ///
        /// <para><b>A missing resolver is reported, not swallowed.</b> <see cref="MeleeHitVolume"/>
        /// resolves its <c>DamageSystem</c> by <c>[Inject]</c>; without it every hit falls back to the
        /// volume's flat <c>TakeDamage(1f)</c> branch, which looks like a working weapon that does
        /// negligible damage. That is worth one warning per unit.</para>
        /// </remarks>
        private void BuildMeleeHitVolume(MeleeWeaponController melee, IObjectResolver resolver)
        {
            var volumeGo = new GameObject("HitVolume");
            volumeGo.transform.SetParent(transform, false);

            _meleeVolume = volumeGo.AddComponent<MeleeHitVolume>();

            if (resolver != null)
            {
                resolver.Inject(_meleeVolume);
            }
            else
            {
                Debug.LogWarning(
                    $"[EnemyWeaponMount] '{(_def != null ? _def.weaponId : "?")}' on '{name}' has a hit " +
                    "volume but no IObjectResolver, so its DamageSystem cannot be injected and every hit " +
                    "will fall back to the volume's flat 1-damage branch.");
            }

            melee.HitVolume = _meleeVolume;
            _meleeVolume.SetActive(false);
        }

        /// <summary>Aim the weapon at a world-space point (AS3 <c>celX</c>/<c>celY</c>).</summary>
        public void SetAimTarget(Vector2 worldTarget)
        {
            _aimTarget    = worldTarget;
            _hasAimTarget = true;
        }

        /// <summary>
        /// The world point the weapon should point at while the unit is <b>not</b> firing.
        ///
        /// <para><b>Why the idle case needs an explicit answer.</b> The brain only publishes an aim
        /// target on a tick it is firing (<c>ArmedShooterBrain.SetWeaponTrigger</c>), so between shots
        /// the mount would otherwise have nothing to face and the weapon would hang pointing at the
        /// last target — or at the world origin before the first shot. AS3 has no such gap: an NPC's
        /// <c>celX</c> is maintained by the AI every frame. Pointing along the unit's own facing is the
        /// faithful idle answer and keeps the weapon on the side the character is looking.</para>
        /// </summary>
        private Vector2 ResolveIdleAimTarget()
        {
            if (_owner == null) _owner = GetComponentInParent<UnitController>();
            if (_owner == null) return (Vector2)transform.position + Vector2.right;

            // The presenter only reads the aim target's X against the weapon's own X, so anchoring this
            // on the unit's feet (rather than the hold point) is enough — and it avoids reading the
            // weapon's own state before it has ever been stepped.
            return new Vector2(_owner.transform.position.x, _owner.FeetWorldY)
                 + new Vector2(_owner.FacingDirection, 0f);
        }

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

            // The presenter draws from the same one flip decision the controller aims with. While the
            // unit is not firing the brain publishes no aim target, so fall back to the unit's facing —
            // otherwise the held weapon would keep pointing at wherever the last shot went.
            _presenter?.SetAimTarget(_firing && _hasAimTarget ? _aimTarget : ResolveIdleAimTarget());

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

            // The melee plan's only job is to carry the swing's DamageContext to the volume — the
            // spawner ignores `MeleeSweep` outright. Same loop, and the same "take the last plan"
            // tie-break, as `PlayerWeaponLoadout.FixedUpdate`; it runs before the spawner call so a
            // swing armed and swept in the same tick hits with this tick's damage.
            if (_meleeVolume != null)
            {
                for (int i = _plans.Count - 1; i >= 0; i--)
                {
                    if (_plans[i].Kind == ShotKind.MeleeSweep)
                    {
                        _meleeVolume.SetDamageContext(_plans[i].Damage);
                        break;
                    }
                }
            }

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
