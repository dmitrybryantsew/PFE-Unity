using PFE.Data.Definitions;
using PFE.Entities.Units;
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
            if (_brain != null)
            {
                _brain.SetState(EnemyAIState.Idle);
            }
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
    }
}
