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

        /// <summary>
        /// A unit part that owns the hold point instead of the body — a griffin's arm, and the seam the
        /// remaining <c>wPos</c> bone-table units will use. Null for every archetype whose hold point
        /// <see cref="WeaponHoldRule"/> can already express.
        ///
        /// <para><b>Resolved lazily, and that is not defensive.</b> The part is built by the archetype
        /// controller while this mount is built by <see cref="EquipRolledWeapon"/>; which of the two runs
        /// first depends on the archetype's <c>Initialize</c> order, so resolving once in
        /// <see cref="Initialize"/> would bind or miss depending on the class. Looking it up on first
        /// use makes the order irrelevant, and a null result is re-tried on the next tick rather than
        /// cached forever.</para>
        /// </summary>
        private IWeaponHoldPointSource _holdSource;

        /// <summary>True once <see cref="_holdSource"/> has been searched for, so a hit is not re-searched.</summary>
        private bool _holdSourceResolved;

        /// <summary>
        /// Which <c>setWeaponPos</c> override the owner runs — set by the controller that equips this
        /// weapon, because the controller is the only thing that knows which AS3 class it stands in for
        /// (<c>RobotController</c> maps <c>RobotKind</c> plus <c>RobotSecondaryKind</c>).
        ///
        /// <para>Default <see cref="WeaponHoldRule.Base"/> is the honest answer for an archetype that
        /// has not been mapped yet: it is the inherited <c>Unit.setWeaponPos</c>, not the player's rule
        /// and not a guess at a constant.</para>
        /// </summary>
        public WeaponHoldRule HoldRule { get; set; } = WeaponHoldRule.Base;

        private Vector2 _aimTarget;
        private bool    _hasAimTarget;
        private bool    _firing;

        /// <summary>
        /// A one-tick pulse set by <see cref="FireOnce"/> and consumed by the next <see cref="Tick"/>.
        ///
        /// <para><b>Deliberately a separate field from <see cref="_firing"/>.</b> That one is owned by
        /// <see cref="SetFiring"/>. A one-shot that wrote it would be able to cancel a held trigger — or
        /// be cancelled by one — depending only on which arrived first, and the loser would be silent:
        /// either a burst weapon that fires once, or a one-shot that never fires at all. Keeping the two
        /// apart means neither can corrupt the other, and each can be reasoned about on its own.</para>
        /// </summary>
        private bool    _fireOnce;
        private bool    _initialised;

        private readonly List<ShotPlan> _plans = new List<ShotPlan>(4);

        /// <summary>True when a controller was built and the weapon can actually fire.</summary>
        public bool IsArmed => _controller != null;

        /// <summary>
        /// The trigger as of the last <see cref="SetFiring"/> call — AS3's <c>attack()</c> having been
        /// called this frame.
        /// </summary>
        /// <remarks>
        /// Exposed for one reason: "he has a target, he is agro, and nothing comes out" is a chain of
        /// six links (brain → trigger → mount → controller → plan → spawner) and every one of them is
        /// invisible from the player's chair. A reader that can see the trigger was held narrows it to
        /// the controller or the spawner without a second play-test. Nothing in the project branches on
        /// this.
        /// </remarks>
        public bool IsFiring => _firing || _fireOnce;

        /// <summary>
        /// How many shot batches this mount has produced since it was built — the proof that a trigger
        /// held for a second actually reached <see cref="ProjectileSpawner"/>.
        /// </summary>
        /// <remarks>
        /// A counter rather than a flag on purpose: one is "it fired once and stopped" (an empty
        /// magazine, a jam) and a climbing one is "it is firing and you cannot see it" (a spawn point
        /// inside geometry, a missing visual). Those two have nothing in common and both read as
        /// "does not shoot" from outside.
        /// </remarks>
        public int ShotsFired { get; private set; }

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

            // ── AS3 `Weapon.krep` — set once, at construction, exactly where the oracle sets it ────
            //
            // `Weapon.as:499-501` copies `owner.weaponKrep` onto the weapon while the weapon is being
            // built (`if(owner && owner.weaponKrep > 0) krep = owner.weaponKrep;`), and `krep` is never
            // re-read afterwards. Doing the same here — rather than per tick — keeps a mid-life
            // definition swap from changing a rule the oracle fixes for the weapon's lifetime.
            //
            // What it selects: `Weapon.actions()` (Weapon.as:1076-1086) snaps `X/Y` to the hold point
            // when `krep > 0` and eases at 1/5 only when it is 0. Every robot in the roster is
            // `krep == 1`, so this is the arm the robot family has been missing — see
            // `docs/EnemyImplementationGuides/AUDIT_robot_weapon_attachment_2026-10-19.md` §R1.
            if (_controller is RangedWeaponController rigid)
            {
                rigid.SnapToHoldPoint = IsRigidlyMounted();
                rigid.OwnerFacingSign  = _owner != null ? _owner.FacingDirection : 1;
            }
            else if (_controller is ThrownWeaponController rigidThrow)
            {
                // `WThrow` inherits the same `actions()` rule — measured: it overrides neither
                // `actions()` nor `step()`, so a rigid-mounted thrower snaps exactly like a gun.
                rigidThrow.SnapToHoldPoint = IsRigidlyMounted();
                rigidThrow.OwnerFacingSign  = _owner != null ? _owner.FacingDirection : 1;
            }

            _spawner = gameObject.GetComponent<ProjectileSpawner>();
            if (_spawner == null)
            {
                _spawner = gameObject.AddComponent<ProjectileSpawner>();
            }

            // AS3 `UnitMerc.as:33` / `UnitEncl.as:30` — the throw count belongs to the UNIT, not to the
            // weapon row: `(this.thWeapon as WThrow).kolAmmo = grenader;`. Read from the owner's
            // definition here because this is the only place that has both the unit and its controller.
            // A unit whose row authors no `<un grenader>` leaves the constructor's default in place,
            // which matches the oracle — such a unit is never given the weapon at all
            // (`UnitMerc.as:30`'s `if(grenader > 0)`).
            if (_controller is ThrownWeaponController thrownController && _owner != null
                && _owner.Definition != null)
            {
                thrownController.SetThrowCount(_owner.Definition.grenadeCount);
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
        /// AS3 <c>Weapon.krep &gt; 0</c> — read from <see cref="UnitDefinition.weaponKrep"/>, which is the
        /// oracle's own field: <b>1</b> by default, 0 only when the row authors <c>krep='0'</c>
        /// (<c>Unit.as:332</c>, <c>:1168</c>).
        ///
        /// <para><b>Not <see cref="UnitDefinition.isStable"/>.</b> That projection is <c>krep == 1</c>, so
        /// it reads <c>false</c> both for an authored <c>krep='0'</c> and for the 91 rows that author
        /// nothing — and for this rule those two are opposite answers (0 eases, absent snaps). Using it
        /// would leave every drone, the spritebot and the mega dron easing, i.e. the four worst cases of
        /// the lag this closes. See <see cref="UnitDefinition.weaponKrep"/>'s remarks for why
        /// <c>IsFixedMeleeMount</c> above deliberately still reads <c>isStable</c>.</para>
        /// </summary>
        private bool IsRigidlyMounted()
        {
            if (_owner == null) _owner = GetComponentInParent<UnitController>();
            UnitDefinition stats = _owner != null ? _owner.Stats : null;
            return stats != null && stats.weaponKrep > 0;
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
        /// Mark this weapon's trigger as having no click edge — AS3's <c>attack(true)</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Called by <c>UnitNPCController</c> and by nothing else.</b> An NPC's trigger is
        /// pushed on every frame (`UnitNPC.as:570-577`), and a semi-auto weapon's <c>t_auto</c>
        /// debounce is refreshed by every push — so without this the NPC fires once per engagement and
        /// then holds a trigger that can never re-arm. See
        /// <see cref="WeaponRuntimeState.AutomaticTrigger"/> for the frame-by-frame trace.</para>
        ///
        /// <para><b>Not set for raiders, deliberately.</b> Their <c>attack()</c> call is gated on
        /// <c>isrnd(0.1)</c> (<c>UnitRaider.as:1521</c>), which leaves <c>t_auto</c> the gap it needs to
        /// expire; giving them automatic fire would replace an authored cadence with the weapon's.</para>
        /// </remarks>
        public void MarkAutomaticTrigger()
        {
            if (_controller != null)
            {
                _controller.State.AutomaticTrigger = true;
            }
        }

        /// <summary>
        /// The world point the weapon should point at while the unit is <b>not</b> firing.
        ///
        /// <para><b>Why the idle case needs an explicit answer.</b> A brain that only publishes an aim
        /// target on a tick it is firing (<c>ArmedShooterBrain.SetWeaponTrigger</c>) would otherwise have
        /// nothing to face between shots, and the weapon would hang pointing at the last target — or at
        /// the world origin before the first shot. Pointing along the unit's own facing is a faithful
        /// idle answer and keeps the weapon on the side the character is looking.</para>
        ///
        /// <para><b>This is a last resort, not the oracle's rule.</b> AS3 has no such gap: every unit's
        /// <c>control()</c> rewrites <c>celX</c>/<c>celY</c> on every frame — a raider aims a body height
        /// above its feet (<c>UnitRaider.as:682</c>, <c>celY = Y - scY</c>), an NPC ten pixels above them
        /// (<c>UnitNPC.as:531</c>) — and the weapon reads that. A unit whose controller publishes a
        /// per-frame point (see <c>UnitNPCController.TickCel</c>) therefore never reaches this method,
        /// because <see cref="SetAimTarget"/> has already been called for this tick. The answer below is
        /// what remains for a brain that does not maintain one, and the per-archetype value it would need
        /// is that archetype's <c>celY</c>, not a generic constant.</para>
        /// </summary>
        private Vector2 ResolveIdleAimTarget()
        {
            if (_owner == null) _owner = GetComponentInParent<UnitController>();
            if (_owner == null) return (Vector2)transform.position + Vector2.right;

            // Facing only — the horizontal sign is what the presenter and the controller both read, and
            // anchoring on the feet keeps this independent of the weapon's own state before it has ever
            // been stepped.
            return new Vector2(_owner.transform.position.x, _owner.FeetWorldY)
                 + new Vector2(_owner.FacingDirection, 0f);
        }

        /// <summary>
        /// Hold or release the trigger. Mirrors <c>PlayerWeaponLoadout.BeginAttack/EndAttack</c>, which
        /// is what the controllers latch as <c>_attackHeld</c>.
        /// </summary>
        public void SetFiring(bool firing) => _firing = firing;

        /// <summary>
        /// Show or hide the held-weapon sprite — AS3 <c>currentWeapon.vis.visible = false</c>
        /// (<c>UnitRaider.as:510-513</c>, <c>UnitEqd.as:29</c>, <c>UnitProtect.as:34</c>,
        /// <c>UnitSentinel.as:38</c>).
        ///
        /// <para><b>Why the oracle hides it, and why the port had to.</b> The mount is a <i>child
        /// GameObject</i> of the unit, so it does not inherit the body's death animation: a corpse drawn
        /// on the "death"/"fall" row would keep a rifle floating where the hand used to be. The oracle
        /// hides the sprite in every <c>dropLoot</c> override that owns a weapon, and the two it does not
        /// hide it for are the alicorn and the dron — which never hold one in the port either.</para>
        ///
        /// <para><b>The visual only.</b> The controller, its state and its aim stay alive; a hidden
        /// weapon that is somehow asked to fire still fires. Nothing in the oracle ties visibility to
        /// firing either, and the brain stops deciding to attack on death
        /// (<c>EnemyAIState.Dead</c>).</para>
        /// </summary>
        public void SetHeldVisualVisible(bool visible)
        {
            if (_presenter == null) return;

            // The presenter lives on the "vis" child, which is also the node that carries the
            // SpriteRenderer and the pivot — deactivating it hides the sprite without touching the
            // mount's own transform, so a later re-show lands exactly where it was.
            _presenter.gameObject.SetActive(visible);
        }

        /// <summary>
        /// Fire this weapon exactly once — AS3's <c>currentWeapon.attack()</c>, which is a <b>call</b>,
        /// not a held trigger.
        ///
        /// <para><b>Why the mount owns this and not the caller.</b> The trigger is a level that
        /// <see cref="Tick"/> reads on every tick, so a brain that pressed it and forgot to release would
        /// be modelling a mouse button held down — which no enemy in the oracle ever does. Arming a pulse
        /// here instead means the press and its release both happen inside one <see cref="Tick"/>: no
        /// cross-tick state, and no assumption about who ticks this mount, or when, or how often.</para>
        ///
        /// <para><b>What holding it actually costs, measured rather than assumed.</b> For a
        /// <b>semi-auto</b> weapon this does <i>not</i> produce a burst. <c>auto</c> is
        /// <c>rapid &lt;= 6</c> (<c>Weapon.as:856</c>) and every spit weapon is <c>rapid</c> 60–90, so the
        /// second and later presses take the single-shot branch — <c>t_auto = 3; ++pow; return;</c>
        /// (<c>:1297-1302</c>) — which pins the debounce and inflates <c>pow</c> once per tick. The damage
        /// from holding is therefore a <b>lockout plus a wrong charge counter</b>, not a machine gun: the
        /// weapon fires once and then cannot fire again for as long as the trigger stays down.</para>
        /// </summary>
        public void FireOnce() => _fireOnce = true;

        /// <summary>
        /// The weapon's world hold point — the owner's own <c>setWeaponPos</c>, selected by
        /// <see cref="HoldRule"/>.
        ///
        /// <para><b>It used to call <see cref="WeaponHoldPointMath.Resolve"/>, and that was the
        /// "weapon is kinda misplaced" report.</b> <c>Resolve</c> models
        /// <c>UnitPlayer.setWeaponPos</c> — the override that reaches the gun out past the snout when
        /// the cursor is beyond the body, holds it at <c>scY * 0.7</c>, lifts it 40 px when the player
        /// stands with the weapon up, and probes for walls. The mount never set <c>WeaponKrep</c>, so
        /// <c>Resolve</c> took the <c>WeaponKrep == 0</c> branch by default and an enemy silently got
        /// the player's rule: an NPC aiming at anything further than its own body width had its gun
        /// shoved a full body width to the side and held 0.2 of a body too high.</para>
        ///
        /// <para><b>Then it over-corrected to the base rule, which was also wrong.</b> The base
        /// <c>Unit.setWeaponPos</c> is only the inherited default; 21 oracle classes override it, and
        /// every robot in this family is one of them — <c>protect</c> and <c>robobrain</c> hold at a flat
        /// <c>Y - 40</c>, <c>sentinel</c> at <c>Y - 90</c>, <c>spritebot</c> at <c>Y - 1</c>. The
        /// <see cref="HoldRule"/> dispatch is the fix; <see cref="WeaponHoldRule"/> carries the count and
        /// the class list.</para>
        ///
        /// <para>The aim point is still tracked — it drives the <i>rotation</i>, which is
        /// <c>Weapon.actions</c>'s <c>rot2</c> and has nothing to do with where the gun is held.</para>
        /// </summary>
        /// <summary>
        /// The owner's body for <see cref="MeleeWeaponController.Owner"/> — AS3 reads <c>owner.X</c>,
        /// <c>owner.Y - scY/2</c> and <c>owner.weaponKrep</c> directly in <c>WClub.actions()</c>.
        ///
        /// <para><b><c>weaponKrep</c> is the branch selector, and it is read from
        /// <see cref="UnitDefinition.weaponKrep"/>, not from <c>isStable</c>.</b> The two answer
        /// different questions for an absent attribute: <c>isStable</c> is the <c>krep == 1</c>
        /// projection, so the 91 rows that author nothing read <c>false</c>, while AS3's field
        /// initialiser is <b>1</b> (<c>Unit.as:332</c>) and those rows take the <i>rigid</i> branch.
        /// The hold-point rule above makes the same distinction.</para>
        ///
        /// <para><c>ClampStep</c> is false: AS3 applies the per-frame travel clamp only to
        /// <c>owner.player</c> (<c>WClub.as:328</c>).</para>
        /// </summary>
        private MeleeWeaponController.OwnerFrame ResolveMeleeOwnerFrame()
        {
            if (_owner == null) _owner = GetComponentInParent<UnitController>();
            if (_owner == null)
            {
                // No unit in scope: report "rigid", which snaps to the hold point and is exactly what
                // this mount's melee weapons did before the spring existed.
                return new MeleeWeaponController.OwnerFrame(
                    transform.position.x, transform.position.y, 1f, rigidMount: true, clampStep: false);
            }

            UnitDefinition stats = _owner.Stats;
            float height = stats != null && stats.Height > 0f ? stats.Height : 0.7f;

            return new MeleeWeaponController.OwnerFrame(
                bodyX:          _owner.transform.position.x,
                // AS3 `owner.Y - owner.scY/2`: the unit origin is the feet, so this is the body's middle.
                bodyCentreY:    _owner.FeetWorldY - height * 0.5f,
                bodyFacingSign: _owner.FacingDirection,
                rigidMount:     stats == null || stats.weaponKrep > 0,
                clampStep:      false);
        }

        public Vector2 ResolveHoldPoint()
        {
            if (_owner == null) _owner = GetComponentInParent<UnitController>();
            if (_owner == null) return transform.position;

            // A bone-driven hold point wins over every body rule: AS3's `UnitMerc.setWeaponPos` does
            // not compute a position at all, it reads `arm.emit` through the arm's own transform
            // (`UnitMerc.as:104-108`). The body rules below are the inherited `Unit.setWeaponPos` and
            // are only reachable for a unit that has no such override.
            if (ResolveHoldSource() != null && _holdSource.TryResolveHoldPoint(out Vector2 bonePoint))
            {
                return bonePoint;
            }

            UnitDefinition stats = _owner.Stats;
            float height = stats != null && stats.Height > 0f ? stats.Height : 0.7f;

            return WeaponHoldPointMath.ResolveHold(
                HoldRule,
                _owner.transform.position.x,
                _owner.FeetWorldY,
                height,
                // `storona` — read by the Zombie arm's `weaponX = X + storona * 30` and ignored by
                // every other rule. The owner is the source, not the mount, for the same reason the
                // hold RULE is: the controller is what knows which class it is standing in for.
                _owner.FacingDirection);
        }

        /// <summary>
        /// Find (once) and bind the part that owns this weapon's hold point, if the unit has one.
        ///
        /// <para>Searched from the <b>unit</b>, not from this mount: the part is a sibling of the mount
        /// under the unit root, because it belongs to the body (a griffin's arm is drawn with the body
        /// and mirrors with it), while the mount owns only the weapon. A search from here would never
        /// find a sibling.</para>
        /// </summary>
        private IWeaponHoldPointSource ResolveHoldSource()
        {
            if (_holdSourceResolved)
            {
                return _holdSource;
            }

            if (_owner == null) _owner = GetComponentInParent<UnitController>();
            if (_owner == null)
            {
                return null;
            }

            _holdSource = _owner.GetComponentInChildren<IWeaponHoldPointSource>(true);
            _holdSourceResolved = _holdSource != null;

            if (_holdSource != null && _controller != null)
            {
                _holdSource.BindWeapon(_controller.State);
            }

            return _holdSource;
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

            // ── The aim point, before the hold point ─────────────────────────────────────────────
            //
            // One aim point for both consumers. The presenter draws from the same flip decision the
            // controller aims with, so resolving them separately is how the drawn barrel and the fired
            // round come to disagree.
            //
            // It is resolved FIRST because a bone-driven hold point is posed by it: a griffin's arm
            // turns with the weapon (`UnitMerc.as:89`), so the hand is not where the previous aim put it
            // until the aim is known. Doing this after `ResolveHoldPoint` — which is where it used to
            // sit, harmlessly, while every hold point was a body constant — would leave the weapon one
            // frame behind the arm on every turn.
            //
            // `_hasAimTarget` alone — NOT `_firing && _hasAimTarget`. The old form threw away a
            // per-frame aim point on every non-firing tick, which silently reverted an NPC to the
            // facing-only fallback between shots and made the oracle's `celX` maintenance invisible in
            // the picture. A brain that never calls SetAimTarget still gets the fallback, so the raider
            // path is unchanged.
            Vector2 aim = _hasAimTarget ? _aimTarget : ResolveIdleAimTarget();
            ResolveHoldSource()?.SetAimWorldPoint(aim);
            _presenter?.SetAimTarget(aim);

            Vector2 hold = ResolveHoldPoint();

            if (_controller is RangedWeaponController ranged)
            {
                // ── The muzzle, from the DRAWN weapon ────────────────────────────────────────────
                //
                // AS3 `Weapon.shoot()` calls `getBulXY()` (Weapon.as:1461), which reads the `vis.emit`
                // marker through `vis.localToGlobal` — the barrel tip *after* the flip and the aim
                // rotation — and falls back to `X/Y` only when that lookup fails (Weapon.as:1395-1420).
                //
                // The presenter already publishes exactly that point
                // (`WeaponPresenter.MuzzleWorldPosition`), and the player path reads it
                // (`PlayerWeaponLoadout.cs:599`). This mount handed the controller `hold` instead — the
                // weapon's ORIGIN — so every enemy round left the grip: measured 0.875 u short on
                // `dront`'s `ttweap1`, 0.15 u on `robolaser`, 0.17 u on `robospark`/`roboplasma`.
                // Null until the first `LateUpdate`, and `hold` is then the oracle's own fallback.
                ranged.MuzzleWorldPoint = _presenter != null && _presenter.MuzzleWorldPosition.HasValue
                    ? _presenter.MuzzleWorldPosition.Value
                    : hold;

                // `owner.storona` for the rigid arm's aim formula (`Weapon.as:1080`). It changes with
                // the brain's facing, so it is refreshed per tick rather than fixed at construction.
                ranged.OwnerFacingSign = _owner != null ? _owner.FacingDirection : 1;
            }
            else if (_controller is ThrownWeaponController thrownAim)
            {
                thrownAim.OwnerFacingSign = _owner != null ? _owner.FacingDirection : 1;
            }
            else if (_controller is MeleeWeaponController melee)
            {
                melee.Owner = ResolveMeleeOwnerFrame();
            }

            // A held trigger and a one-shot pulse are the same press for this tick and different
            // afterwards: `_firing` is whatever `SetFiring` last said, while `_fireOnce` is spent here.
            // Neither writes the other's field, so a `FireOnce` can never cancel a held trigger and a
            // held trigger can never swallow a one-shot.
            if (_firing || _fireOnce) _controller.BeginAttack();
            else                      _controller.EndAttack();

            // Consumed by the tick that read it, so the NEXT tick takes `EndAttack` — the two-tick
            // press/release a real click makes. Not cleared inside `FireOnce`, because the read is what
            // makes the press count; and not cleared on the early returns above, so a pulse armed while
            // the mount cannot tick is deferred to the next tick rather than silently dropped.
            _fireOnce = false;

            _controller.Tick(dt, hold, hold, aim);

            _plans.Clear();
            _plans.AddRange(_controller.FlushShotPlans());
            if (_plans.Count == 0) return;

            // Counted here rather than at the spawner call: a plan batch IS the shot, and a spawner
            // that drops it is a different failure with its own report. See ShotsFired.
            ShotsFired++;

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
