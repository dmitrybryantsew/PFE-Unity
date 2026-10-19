using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Controller for Zombie / Ghoul enemy units (AS3 <c>fe/unit/UnitZombie.as</c>).
    ///
    /// <para><b>It deliberately declares no <c>Faction</c>.</b> The port's first version overrode it to
    /// <see cref="FactionType.Zombie"/>, which reads as the natural choice — there is a
    /// <c>F_ZOMBIE</c> constant, and this is the zombie controller. Both halves of that are wrong.
    /// AS3's <c>F_ZOMBIE</c> is <b>3</b> and is <i>declared and never assigned anywhere in the
    /// codebase</i>; the zombie family row is <c>&lt;unit id='zombie' fraction='1'&gt;</c>
    /// (<c>AllData.as:768</c>) — <b>1</b>, <c>F_MONSTER</c> — and <c>UnitZombie.resurrect()</c> writes
    /// <c>fraction = Unit.F_MONSTER</c> (<c>:506</c>) even for a zombie that has already been killed
    /// once. So the data and the oracle agree on Monster, and the override made the port disagree with
    /// both.</para>
    ///
    /// <para>It also happened to be harmless, which is exactly why it is worth deleting rather than
    /// leaving: <c>UnitController.Faction</c> already answers
    /// <c>_stats.fraction</c>, and <c>zombie0..zombie9</c> all inherit <c>fraction: 1</c> from the
    /// family, so the override returned a value nothing else produced and nothing yet reads. The first
    /// consumer that trusts <c>Faction</c> — friendly fire, a targeting filter, a faction-keyed
    /// dialogue — would have found every zombie in a faction of its own.</para>
    /// </summary>
    [RequireComponent(typeof(ZombieBrain))]
    public sealed class ZombieController : EnemyController
    {
        public const string ControllerId = "UnitZombie";
        public const string ControllerAlias = "zombie";

        [SerializeField] private int _resurrectTimer = 300;
        [SerializeField] private bool _canResurrect;

        public bool CanResurrect => _canResurrect;
        public int ResurrectTimer => _resurrectTimer;

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<ZombieBrain>();
            }
        }

        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);
            if (definition != null && definition.canResurrect)
            {
                _canResurrect = true;
                _resurrectTimer = 300;
            }
        }

        public void SetCanResurrect(bool canRes, int timer = 300)
        {
            _canResurrect = canRes;
            _resurrectTimer = timer;
        }

        public void UpdateResurrectTick()
        {
            if (!_canResurrect || IsAlive) return;

            // AS3's timer only runs while the body still exists — `if(sost == 3 && isRes && t_res < 20)`
            // (`UnitZombie.as:219`). A body the player shot apart is `sost == 4`, and a destroyed body
            // cannot get up. Without this the corpse would dissolve and then stand back up out of
            // nothing, which is the one outcome the state machine exists to prevent.
            if (IsCorpseRemoved) return;

            _resurrectTimer--;
            if (_resurrectTimer <= 1)
            {
                Resurrect();
            }
        }

        public void Resurrect()
        {
            _resurrectTimer = 300;
            if (_unitStats != null)
            {
                _unitStats.CurrentHp.Value = _unitStats.MaxHp.Value;
            }

            // AS3 sets `sost = 1` here. The port's corpse state owns the collider, the rigidbody and
            // the renderer, so writing HP alone would leave an invisible, intangible unit that every
            // damage source skips — see ReviveFromCorpse.
            ReviveFromCorpse();

            if (_brain != null)
            {
                _brain.SetState(EnemyAIState.Idle);
            }
        }

        /// <summary>
        /// <c>UnitZombie.setWeaponPos</c> (<c>UnitZombie.as:175-179</c>) — the spit weapon is held
        /// <b>30 px ahead of the body centre</b> and <b>0.8 of the body height above the feet</b>, on the
        /// side the unit is facing.
        ///
        /// <para><b>Why this has to be answered here and not left at the base rule.</b> The base
        /// <c>Unit.setWeaponPos</c> is the inherited default and the zombie is one of the 21 classes that
        /// override it. Without the override the venom lands from a grip point a full body width to the
        /// side of the mouth, which is visible on every spit and looks like a muzzle-offset bug rather
        /// than the missing per-class constant it is. <c>WeaponHoldRule</c> carries the count.</para>
        /// </summary>
        protected override WeaponHoldRule ResolveHoldRule() => WeaponHoldRule.Zombie;

        /// <summary>
        /// AS3 <c>currentWeapon.attack()</c> for the spit skills — equip the weapon this skill fires,
        /// aim it at <paramref name="aimWorldPoint"/> and fire one shot.
        ///
        /// <para><b>One shot, not a held trigger.</b> The oracle's <c>attack()</c> is a single call, so
        /// this goes through <see cref="EnemyWeaponMount.FireOnce"/>, which presses and releases inside
        /// one mount tick. See that method for what holding the trigger would actually cost — for a
        /// semi-auto weapon it is a lockout and an inflated <c>pow</c>, not a burst.</para>
        ///
        /// <para><b>Not <c>MarkAutomaticTrigger</c>.</b> That clears the semi-auto <c>t_auto</c>
        /// debounce so a held trigger can re-arm, which is what an NPC pushing every frame needs. A
        /// zombie presses once and releases, which is the ordinary semi-auto cycle — the same reason
        /// raiders are deliberately left without it.</para>
        /// </summary>
        /// <param name="weaponId">The weapon id, from <c>ZombieSuperMath.WeaponId</c>. Null is a
        /// no-op: a skill that fires nothing is not an error.</param>
        public void FireSuperWeapon(string weaponId, Vector2 aimWorldPoint)
        {
            if (string.IsNullOrEmpty(weaponId))
            {
                return;
            }

            EnemyWeaponMount mount = EquipWeapon(weaponId);
            if (mount == null)
            {
                // EquipWeapon already logged why (no WeaponDefinition for the id).
                return;
            }

            mount.SetAimTarget(aimWorldPoint);
            mount.FireOnce();
        }
    }

    [RequireComponent(typeof(ZombieBrain))]
    public sealed class GhoulController : EnemyController
    {
        public const string ControllerId = "UnitGhoul";
        public const string ControllerAlias = "ghoul";

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<ZombieBrain>();
            }
        }
    }

    [RequireComponent(typeof(ZombieBrain))]
    public sealed class DeadController : EnemyController
    {
        public const string ControllerId = "UnitDead";
        public const string ControllerAlias = "dead";

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<ZombieBrain>();
            }
        }
    }

    /// <summary>
    /// Controller for <c>spectre</c> — AS3 <c>UnitSpectre</c>, which <b>extends <c>Unit</c>, not
    /// <c>UnitZombie</c></b>.
    ///
    /// <para><b>It deliberately requires no brain.</b> The spawner used to route <c>spectre</c> to
    /// <see cref="ZombieController"/>, whose <c>[RequireComponent(typeof(ZombieBrain))]</c> handed every
    /// spectre the entire zombie behaviour set — digger tiers, jump, drop-through, ledge handling and the
    /// <c>t_res</c> resurrection — none of which <c>UnitSpectre</c> has.
    /// <c>04_ROSTER_AND_FAMILY_MAP.md</c> states the rule outright: it "must never be routed to
    /// <c>ZombieBrain</c>".</para>
    ///
    /// <para>A brainless <see cref="EnemyController"/> is the honest shape while the spectre's own
    /// behaviour is unported. Its oracle node is <c>&lt;unit id='spectre' fraction='0'&gt;</c> with
    /// <c>hp='350' damage='1000' tipdam='16' ear='0' observ='200'</c> — it is <c>fraction 0</c>
    /// (neutral), <c>invulner</c>, <c>isFly</c>, placed as a room object, acting only under
    /// <c>World.w.enemyAct</c> and applying <c>horror</c>/<c>curse</c>. None of that is modelled here, so
    /// this controller does exactly one thing: it stops the spectre being a zombie. It stands, it is
    /// damageable and it dies.</para>
    ///
    /// <para>Its node also carries <b>no <c>&lt;vis&gt;</c> at all</b> — the oracle draws it with the
    /// vector class <c>visualSpectre</c> — so its imported sheet is empty and it draws nothing until the
    /// vector pipeline covers units. That is a separate, already-tracked gap and not a reason to leave it
    /// mis-routed.</para>
    /// </summary>
    public sealed class SpectreController : EnemyController
    {
        public const string ControllerId = "UnitSpectre";
        public const string ControllerAlias = "spectre";

        /// <summary>
        /// <c>UnitSpectre.as:33</c> — <c>collisionTip = 0</c>. A ghost passes through rather than
        /// body-checking, so its contact hit does no momentum exchange. See
        /// <see cref="PFE.Entities.Units.UnitController.CollisionTip"/>.
        /// </summary>
        public override bool CollisionTip => false;
    }
}
