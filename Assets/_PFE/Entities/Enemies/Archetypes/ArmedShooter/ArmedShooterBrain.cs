using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Sim.Units;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// ArmedShooter enemy brain — a high-fidelity port of AS3 <c>fe/unit/UnitRaider.as</c> and its subclasses
    /// (UnitSlaver, UnitZebra, UnitRanger, UnitEncl, UnitMerc, UnitNecros).
    /// </summary>
    public class ArmedShooterBrain : EnemyBrain
    {
        // ── Constants & Configuration from Oracle ─────────────────────────────

        /// <summary>AS3 UnitRaider.optDistAtt default window (200 px).</summary>
        protected const float OptDistAttackPixels = 200f;

        /// <summary>Default delay before acting on first sight (UnitRaider.tupizna = 40).</summary>
        protected int _tupizna = 40;

        /// <summary>
        /// Awareness ladder threshold (AS3 <c>maxSpok</c>). Set by
        /// <see cref="ApplyDefinitionTuning"/>: <c>Unit.as:362</c> declares 30 and only
        /// <c>UnitEncl</c>/<c>UnitMerc</c> raise it to 50 (<c>UnitEncl.as:25</c>, <c>UnitMerc.as:27</c>).
        /// Not serialized — the value is derived, never authored.
        /// </summary>
        protected int _maxSpok = 30;
        protected int _aiSpok;

        /// <summary>
        /// AS3 <c>UnitRaider.attackerType</c> (<c>UnitRaider.as:83</c>, declared <c>0</c>) — how this unit
        /// attacks:
        /// <list type="bullet">
        /// <item><description><b>0</b> — contact: the body hits, no weapon swing (<c>attKorp</c>).</description></item>
        /// <item><description><b>1</b> — the held <c>tip == 1</c> weapon is swung (<c>currentWeapon.attack()</c>).</description></item>
        /// <item><description><b>2</b> — ranged: the held weapon is fired.</description></item>
        /// <item><description><b>3</b> — thrown: the held weapon is thrown.</description></item>
        /// </list>
        ///
        /// <para><b>Read, not computed.</b> The value is derived once, by
        /// <see cref="ArmedShooterController"/> from the weapon it rolled
        /// (<c>UnitRaider.as:257-272</c>), and copied here in
        /// <see cref="ApplyDefinitionTuning"/>. The brain must not re-derive it: the previous
        /// id-and-substring inference was a second, divergent copy of the oracle's ladder, and it is
        /// precisely what made a <c>raider1</c> that rolled a gun fight as a contact attacker.</para>
        /// </summary>
        [SerializeField]
        protected int _attackerType;

        /// <summary>Tactical flags from AS3.</summary>
        [SerializeField] protected bool _isWalker;
        [SerializeField] protected bool _isSniper;
        /// <summary>
        /// Mirrors AS3 <c>UnitRaider.dash</c> (<c>UnitRaider.as:57</c>) — the gate on the states-7/8
        /// dash-and-charge arm (<c>:764</c>).
        ///
        /// <para><b>It is live, for exactly one family, and this comment used to say the opposite.</b>
        /// <c>UnitRaider</c> declares <c>public var dash:Boolean = false</c> and never writes it, and no
        /// <c>&lt;un dash=…&gt;</c> exists in <c>AllData.as</c> — so a grep of the <i>base</i> class says
        /// the arm is dead, which is what the previous note concluded. But <c>UnitZebra.as:25</c> writes
        /// <c>dash = true;</c> in its constructor, and <c>UnitZebra</c> is the only subclass that does.
        /// The arm was therefore dead for the one class it exists for: the zebra — the charger, the
        /// family whose whole silhouette is a body that runs you down — never reached states 7 or 8.
        /// <see cref="ApplyDefinitionTuning"/> now sets it from the family id, beside <c>tupizna</c> and
        /// <c>durak</c>.</para>
        /// </summary>
        [SerializeField] protected bool _canDash;
        /// <summary>
        /// AS3 <c>UnitRaider.durak</c> (<c>UnitRaider.as:55</c>, declared <c>true</c>) — the oracle's
        /// "foolish" flag, and it cuts both ways: a <c>durak</c> unit takes the run speed in states 2/3
        /// (<c>:1014</c>) but re-asserts its heading only 50 % of the time instead of 90 % (<c>:1185</c>).
        /// <c>UnitZebra.as:24</c>, <c>UnitEncl.as:23</c> and <c>UnitRanger.as:22</c> clear it;
        /// <c>UnitMerc</c> does not. <see cref="ApplyDefinitionTuning"/> derives it from the family id.
        /// </summary>
        [SerializeField] protected bool _durak = true;
        /// <summary>
        /// AS3 <c>UnitRaider.flyer</c> (<c>UnitRaider.as:29</c>) — "this unit can fly", a <b>class</b>
        /// fact for the two subclasses that set it in their constructors.
        ///
        /// <para><b>Where the value comes from, and why not from the row.</b> <c>UnitMerc.as:22</c> and
        /// <c>UnitEncl.as:18</c> both write <c>flyer = true;</c> before <c>super()</c>. The row-side
        /// writer (<c>UnitRaider.as:314-318</c>, <c>&lt;un flyer='1'&gt;</c>) exists in the oracle but
        /// <b>no shipped row authors it</b> — <c>grep "flyer="</c> over <c>AllData</c> returns 0 — so in
        /// the shipped game the flag is true for exactly the merc and encl families and false for every
        /// other raider variant. <see cref="ApplyDefinitionTuning"/> therefore derives it from the family
        /// id, the same way it already derives <c>maxSpok</c>.</para>
        ///
        /// <para><b>It was declared and never assigned</b> until this pass, which is why the animation
        /// branch below it could not be reached and no griffin ever flew. A flag with no writer is
        /// indistinguishable from a feature that was never ported — which is what it was.</para>
        /// </summary>
        [SerializeField] protected bool _isFlyer;

        /// <summary>
        /// AS3 <c>Unit.isFly</c> (<c>Unit.as:284</c>) for this brain — the runtime flight state, as
        /// opposed to <see cref="_isFlyer"/>'s capability.
        ///
        /// <para><b>Two writers, both from <c>UnitRaider.control()</c>:</b>
        /// <c>:938</c> (<c>if(flyer &amp;&amp; celDY &lt; -80) isFly = true</c> — the target is well
        /// above) and <c>:955-971</c> (six airborne ticks), and one clearer, <c>:1313-1331</c> (twenty
        /// ticks of floor contact). The port's <c>TargetDeltaY</c> is the negation of the oracle's
        /// <c>celDY</c> because the port's Y grows upward, so the oracle's <c>&lt; -80</c> is
        /// <c>&gt; 80</c> here — the same flip <c>UpdateDropThrough</c> already relies on.</para>
        /// </summary>
        protected bool _isFly;

        /// <summary>AS3 <c>t_fall</c> (<c>UnitRaider.as:955-966</c>) — consecutive airborne ticks.</summary>
        protected int _tFall;

        /// <summary>AS3 <c>t_landing</c> (<c>UnitRaider.as:1313-1327</c>) — consecutive grounded ticks.</summary>
        protected int _tLanding;

        /// <summary>AS3 <c>t_float</c> (<c>UnitRaider.as:974-980</c>) — the hover bob's phase.</summary>
        protected float _tFloat;

        /// <summary>
        /// AS3 <c>stalkDist</c> — the radius inside which a flying unit stops approaching and hovers
        /// (<c>UnitRaider.as:1291-1299</c>, <c>rasst2 &lt; stalkDist * stalkDist</c>).
        ///
        /// <para><b>A constant standing in for an unimported attribute, not a tuned value.</b> The
        /// attribute is <c>&lt;un stalk&gt;</c>; <c>UnitDataImporter.ParseUnitExtras</c> documents it as
        /// one of the eighteen it deliberately does not write, and <c>UnitDefinition.stalkDistance</c>
        /// exists but is read by nothing. Shipped values for the flyers are 500 (<c>merc1</c>,
        /// <c>merc2</c>) and 1000 (<c>merc3</c>), so 500 is the family's floor rather than a guess.</para>
        /// </summary>
        protected float _stalkDistance = 500f;

        /// <summary>
        /// <c>UnitRaider.as:974-980</c> — <c>t_float += 0.0512</c> per frame, then
        /// <c>floatX = sin(t_float)</c>, <c>floatY = cos(t_float) * 2</c>. The constant is the oracle's,
        /// not a tuning knob.
        /// </summary>
        protected const float FloatPhaseStep = 0.0512f;

        /// <summary>How long a flying unit must stay in floor contact before it lands (<c>:1313-1327</c>).</summary>
        protected const int LandingTicks = 20;

        /// <summary>
        /// <c>UnitRaider.as:1293-1294</c> — <c>dx *= 0.85; dy *= 0.85;</c>, the hover's per-frame damping.
        /// The oracle <b>bleeds</b> a hovering flyer's velocity; it does not stop it. Stopping it is
        /// what made a griffin look pinned in place instead of coasting to a halt inside its stand-off
        /// radius.
        /// </summary>
        protected const float HoverDamping = 0.85f;

        /// <summary>Airborne ticks before <c>isFly</c> arms (<c>UnitRaider.as:955-966</c>, <c>t_fall &gt; 6</c>).</summary>
        protected const int FlyAfterFallTicks = 6;

        /// <summary>The target's height above the unit that arms flight outright (<c>:938</c>, 80 px).</summary>
        protected const float FlyOnTargetAbovePixels = 80f;

        /// <summary>
        /// Whether this unit is airborne <i>under its own power</i> right now — the brain's answer to AS3
        /// <c>isFly</c>.
        ///
        /// <para><b>Read by <see cref="ArmedShooterController.IsFlying"/>, which is what turns gravity
        /// off.</b> That link is the whole reason the flag has to be public: <c>UnitFlyPhysics</c> and
        /// <c>TilePhysicsController</c>'s flyer branch were ported and correct, and unreachable, because
        /// no enemy controller ever answered <c>true</c>.</para>
        /// </summary>
        public bool IsFlying => _isFly;

        /// <summary>Whether this unit is a flyer at all (AS3 <c>flyer</c>), for diagnostics.</summary>
        public bool IsFlyer => _isFlyer;

        [SerializeField] protected float _aiDist = 400f;

        /// <summary>
        /// Burst cadence in ticks (AS3 <c>aiAttackOch</c>, <c>UnitRaider.as:1525-1536</c>).
        ///
        /// <para><b>Now imported, and the old constant was wrong in both directions.</b> The value is
        /// <c>&lt;un och='…'&gt;</c> (<c>UnitRaider.as:319-321</c>), and when the row is silent AS3's
        /// declared default is <b>0</b> — which is not "no cadence" but the 10 %-per-frame branch
        /// (<c>:1517</c>) <i>and</i> the trigger for the constructor's magazine rule:
        /// <c>if(currentWeapon &amp;&amp; currentWeapon.holder &gt; 10 &amp;&amp; aiAttackOch == 0)
        /// aiAttackOch = round(holder / 2)</c> (<c>:217-219</c>). The port carried a flat <c>30</c>, so
        /// every shooter without an authored <c>och</c> fired on a duty cycle the oracle never runs, and
        /// <c>merc2</c>'s authored <c>och='3'</c> and <c>merc3</c>'s <c>45</c> were ignored entirely.</para>
        /// </summary>
        protected int _aiAttackOch;
        protected int _aiAttackT;

        /// <summary>Jump cooldown (AS3 aiJump, 30..79 ticks).</summary>
        protected int _jumpCooldownTicks;

        /// <summary>State timer (AS3 aiTCh).</summary>
        protected int _aiTCh;

        /// <summary>
        /// AS3 <c>aiNapr</c> (<c>Unit.as:348</c>, aliased to <c>storona</c> wherever either is written) —
        /// the direction this unit <b>wants to move</b>, as a <i>latched</i> value.
        ///
        /// <para><b>The latch is the whole mechanism, not an implementation detail.</b> The oracle
        /// re-asserts it from <c>celDX</c> only every fifteen ticks and only when the target is more than
        /// 100 px away (<c>:1181-1196</c>), and every turn — at a wall, at a lip — writes it. Between
        /// those writes it <i>persists</i>, which is what lets a raider that turned at a wall walk away
        /// from it for a few seconds instead of turning straight back. The port derived the direction
        /// from <c>TargetDeltaX</c> on every tick, so a turn lasted exactly one frame: that is the
        /// "stuck in geometry" report.</para>
        /// </summary>
        protected int _aiNapr;

        /// <summary>
        /// AS3 <c>aiTTurn</c> (<c>Unit.as:352</c>, declared <c>10</c>) — how many more blocked ticks the
        /// unit will try to <i>jump</i> before it commits to going around. Only the wall reaction reads
        /// it; every refusal costs <see cref="ArmedShooterStateMath.WallTurnPenalty"/>.
        /// </summary>
        protected int _aiTTurn = ArmedShooterStateMath.WallTurnInitialTicks;

        /// <summary>
        /// Family internal state (0..8 in UnitRaider.as:624-1453):
        /// 0: Idle, 1: Patrol, 2: Alert, 3: CombatChase, 4: Hold/strafe,
        /// 5: Spotted deciding, 6: Grenade/mine approach, 7: Dash wind-up, 8: Charge
        /// </summary>
        protected int _familyState;

        /// <summary>
        /// The port's <c>Unit.overLook</c> — <b>latched</b> from
        /// <see cref="ArmedShooterStateMath.OverLookForFamilyState"/> because the oracle's states 4, 5
        /// and 7 do not touch it, and pushed to <see cref="EnemySensors.Omnidirectional"/> at the top of
        /// every tick.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a latched field and not a property.</b> The oracle's <c>overLook</c> is a plain
        /// <c>Boolean</c> on the unit that three different state bodies write and nothing recomputes, so
        /// its value in state 4 depends on whether the unit arrived from state 3 or from state 1. A
        /// property that mapped the state to a value would have to answer for 4/5/7, and every answer it
        /// could give is a rule the oracle does not have.</para>
        ///
        /// <para><b>Seeded <c>false</c>, which is what the data says for every raider.</b> AS3 seeds it
        /// from the unit row (<c>Unit.as:1345</c>, <c>node.@overlook &gt; 0</c>), and only the bosses and
        /// <c>turret2</c> author <c>overlook='1'</c> — <c>raider</c>, <c>slaver</c>, <c>merc</c>,
        /// <c>ranger</c> and <c>encl</c> are all <c>0</c>. The seed is therefore inert for this
        /// archetype and only matters for a subclass; the state bodies override it within one tick
        /// anyway.</para>
        /// </remarks>
        protected bool _overLook;

        protected float _walkSpeed = 2f;
        protected float _runSpeed = 4f;

        public int AttackerType { get => _attackerType; set => _attackerType = value; }
        public int FamilyState => _familyState;

        /// <summary>
        /// This unit as an <see cref="ArmedShooterController"/>, which is where the rolled weapon and its
        /// mount live. <c>_controller</c> is declared on <see cref="EnemyBrain"/> as the base
        /// <c>UnitController</c>, so the weapon half needs the cast; null means the brain is attached to
        /// something that is not an ArmedShooter.
        /// </summary>
        protected ArmedShooterController ShooterController => _controller as ArmedShooterController;

        /// <summary>
        /// Whether the oracle called <c>currentWeapon.attack()</c> on this tick — AS3's "the trigger is
        /// held right now".
        ///
        /// <para><b>Why a per-tick latch rather than a persistent flag.</b> AS3 calls
        /// <c>currentWeapon.attack()</c> from <c>UnitRaider.attack()</c>, which the state machine runs
        /// every frame (<c>UnitRaider.as:1483-1553</c>). The weapon's own <c>t_attack</c> is what paces
        /// the rounds; the caller only decides whether the trigger is down <i>this</i> frame. So the latch
        /// is cleared at the top of every tick and re-armed by <see cref="ExecuteAttackAction"/>, which is
        /// exactly one frame of "held".</para>
        /// </summary>
        protected bool _weaponTriggerHeld;

        /// <summary>
        /// AS3 <c>t_gren</c> (<c>UnitMerc.as:17</c>, <c>UnitEncl.as:13</c>, <c>UnitRanger.as:14</c>) —
        /// the countdown before the family's secondary weapon is thrown. <c>-1</c> means "not seeded
        /// yet"; the oracle seeds it in the constructor with <c>round(random * span + 50)</c>, which the
        /// first tick does here because the span is the family's and the brain only learns it from the
        /// controller.
        /// </summary>
        /// <remarks>
        /// An <c>int</c>, not a <c>float</c>: the oracle declares <c>int</c> and decrements with
        /// <c>--</c>, and the countdown is a tick count, not a duration. The re-arm range is a
        /// <i>different</i> pair of bounds from the initial one on the ranger — see
        /// <c>ArmedShooterController.SecondaryRearmMinTicks</c>.
        /// </remarks>
        protected int _secondaryTimer = -1;

        /// <summary>
        /// The secondary weapon that fires this tick, or <c>-1</c>.
        ///
        /// <para>A latch rather than a direct call for the same reason as
        /// <see cref="_weaponTriggerHeld"/>: <c>EnemyWeaponMount</c> holds its trigger until told
        /// otherwise, and <see cref="ReleaseSecondaryTrigger"/> is what tells it.</para>
        /// </summary>
        protected int _secondaryFiring = -1;

        protected override bool StopsToAttack => false;

        /// <summary>
        /// <c>UnitRaider.as:1378-1387</c> — a raider walks doors open. The gate there is
        /// <c>pumpObj.lock &lt;= 0 &amp;&amp; pumpObj.active &amp;&amp; pumpObj.action == 1 &amp;&amp;
        /// pumpObj.lockTip != 4</c>; the port applies the first two in
        /// <see cref="EnemyBrain.TryPushDoorOpen"/> and has no <c>lockTip</c> concept — a door it cannot
        /// open is simply a locked one, which is the same outcome.
        /// </summary>
        protected override bool CanOpenDoors => true;

        /// <summary>
        /// AS3 <c>stroll == false</c> — this unit was authored to hold its post rather than wander.
        /// </summary>
        /// <remarks>
        /// <para><b>Three placements spell it, and one unit row does.</b> The placement's
        /// <c>ai="stay"</c>, <c>ai="quiet"</c> and <c>ai="sniper"</c> all clear <c>stroll</c>
        /// (<c>UnitRaider.as:227-241</c>), and the unit row's own <c>&lt;un stay='1'/&gt;</c> does too
        /// (<c>:351-354</c> — <c>if(node0.un.@stay.length()) this.stroll = false;</c>).
        /// <see cref="UnitController.PlacementStaysPut"/> answers for <b>both</b> halves: the placement
        /// attribute through <c>UnitPlacementAi</c>, and the row through
        /// <see cref="PFE.Data.Definitions.UnitDefinition.willStayInPlace"/>, which
        /// <c>UnitDataImporter.ParseUnitExtras</c> now imports. The row half is gated on the raider family
        /// because <c>UnitRaider</c>'s constructor is the only reader of <c>node0.un.@stay</c> in the whole
        /// oracle, and exactly one unit authors it (<c>zebra3</c>).</para>
        ///
        /// <para><b>What it suppresses, and what it must not.</b> Only the idle <i>wander</i> step below.
        /// A pinned raider still turns to face, still hears, still sees, still raises the alarm and still
        /// shoots — the oracle sets <c>aiState = 0</c> (stand) and leaves the whole perception chain above
        /// it untouched. Suppressing more than the wander would make <c>ai="stay"</c> mean "deaf and
        /// blind", which is not what the data says.</para>
        /// </remarks>
        protected bool StaysPut => _controller != null && _controller.PlacementStaysPut;

        /// <summary>
        /// AS3 <c>moving == false</c> — this unit was authored <c>ai="sniper"</c>, so it holds its ground
        /// instead of closing to contact.
        /// </summary>
        /// <remarks>
        /// <para><b>The other half of the same attribute.</b> <c>UnitRaider.as:236-241</c> sets
        /// <c>stroll</c>, <c>moving</c> and <c>weaponSkill</c> together for <c>sniper</c>; the port reads
        /// the first through <see cref="StaysPut"/> and this one through
        /// <see cref="UnitController.PlacementHoldsPosition"/>. Six shipped placements author it — two
        /// slavers, two rangers, one zebra, one raider.</para>
        ///
        /// <para><b>What it does.</b> <c>UnitRaider.as:768-771</c> — <c>if(!this.moving) aiState = 4;</c>
        /// — forces the hold/strafe state, which is the same <c>_familyState = 4</c> the stand-off test
        /// below selects. <c>moving</c> is otherwise never false, so this is the only writer. The three
        /// other readers (<c>:943</c>, <c>:1099</c>, <c>:1338</c>) all gate movement on it, and all three
        /// are already satisfied by <c>_familyState == 4</c> producing <c>speed = 0</c>.</para>
        /// </remarks>
        protected bool HoldsPosition => _controller != null && _controller.PlacementHoldsPosition;

        protected override void Awake()
        {
            base.Awake();
            ApplyDefinitionTuning();
        }

        public override void Initialize(ITileQueryService tileQuery, SimLoop simLoop)
        {
            base.Initialize(tileQuery, simLoop);
            ApplyDefinitionTuning();
        }

        public override void OnDefinitionAssigned()
        {
            base.OnDefinitionAssigned();
            ApplyDefinitionTuning();
        }

        protected virtual void ApplyDefinitionTuning()
        {
            if (_controller == null || _controller.Definition == null)
            {
                return;
            }

            UnitDefinition def = _controller.Definition;

            // ── The row flags, BEFORE anything reads them ────────────────────────────────────────────
            //
            // `_walkSpeed` is derived from `_isWalker` and `_isWalker` is derived from the row, so the
            // assignment has to come first. It did not, and the two passes this method makes then
            // disagreed: the first (during `Initialize`) computed a walker's walk speed from
            // `moveSpeed`, and the second (`OnWeaponEquipped`, once the weapon is mounted) recomputed it
            // as 2.5. A zebra therefore walked at 4 until it was armed and at 2.5 afterwards, and which
            // one a reader saw depended on when they looked.
            if (def.isWalker) _isWalker = true;
            if (def.isSniper) _isSniper = true;

            string defId = def.id != null ? def.id.ToLowerInvariant() : "";

            // `parentId` is the family id (`encl1` → `encl`); the unit's own id is the fallback for a
            // family template, which has no parent. Every per-class constant below is keyed on it.
            string familyId = !string.IsNullOrEmpty(def.parentId) ? def.parentId : defId;

            // The class table first, because `runSpeed` below depends on `dash` — the zebra is the only
            // family that overrides the raider's `× 2` with `× 3`, and it does so in the same
            // constructor that sets `dash`. See ClassConstantsFor for the transcription.
            ArmedShooterStateMath.ClassConstants constants =
                ArmedShooterStateMath.ClassConstantsFor(familyId);

            // ── `maxSpeed`, `walkSpeed`, `runSpeed` (UnitRaider.as:152-160, UnitZebra.as:31) ─────────
            //
            //     walkSpeed = maxSpeed;  if(this.walker) walkSpeed = 2.5;
            //     runSpeed  = maxSpeed * 2;          // UnitRaider.as:160
            //     runSpeed  = maxSpeed * 3;          // UnitZebra.as:31 — after super() returns
            //
            // **`maxSpeed` is the ROW's speed, not `walkSpeed`, and the two differ for a walker.**
            // The port derived the run speed from `walkSpeed` and multiplied by `runMultiplier` (2), so
            // the zebra — which is `isWalker: 1`, `moveSpeed: 4` — ran at 2.5 × 2 = **5** px/frame where
            // the oracle gives 4 × 3 = **12**. That is not a tuning nudge: the zebra is the one
            // ArmedShooter built to close a gap, the player simply walks away from it, and a boss that
            // cannot arrive reads in play as a boss that is not moving.
            float maxSpeed = def.moveSpeed > 0f ? def.moveSpeed : 2f;
            _walkSpeed = ArmedShooterStateMath.WalkSpeedFor(maxSpeed, _isWalker);
            _runSpeed = ArmedShooterStateMath.RunSpeedFor(maxSpeed, def.runMultiplier, constants.Dash);

            _patrolSpeed = _walkSpeed;
            _chaseSpeed = _runSpeed;
            _alertSpeed = _runSpeed * 0.6f;

            // AS3 `aiDist` (`UnitRaider.as:323-326`, declared 1000). This used to read
            // `detectionDistance` — AS3's *observation* power, which the importer leaves at 400 for every
            // unit — so the ranged arm committed at 400 px where the oracle commits at 1000, and the 8
            // rows that author `<un dist>` (500/600/700) were ignored.
            _aiDist = def.aiDistance > 0 ? def.aiDistance : 1000f;

            // AS3 `stalkDist` (`:327-330`, declared 500), the flyer's hover radius.
            _stalkDistance = def.stalkDistance > 0 ? def.stalkDistance : 500f;

            // ── The per-class constants, read from the constructors ─────────────────────────────────
            //
            // `tupizna`, `durak`, `dash`, `flyer`, `visionMult` and `maxSpok` are all declared with a
            // default on `UnitRaider`/`Unit` and overwritten by a subclass constructor that runs after
            // `super(...)`. They are CLASS facts — nothing in `AllData.as` authors them — so the only
            // way to know one is to read the subclass, and none of them was ported: every ArmedShooter
            // in the game used the raider defaults. The table lives in
            // `ArmedShooterStateMath.ClassConstantsFor` so the offline harness can pin it against the
            // oracle's literals; this is the wiring.
            //
            // What the zebra was getting wrong, concretely:
            //   tupizna 40 → 10   it stood for up to 59 ticks after spotting the player, not 29
            //   durak   true → false  it took the run speed in states 2/3 and re-asserted its heading
            //                        50 % of the time instead of 90 % — faster AND worse at steering
            //   dash    false → true  states 7 and 8 never happened, i.e. no wind-up, no charge
            //   visionMult 1 → 1.3    it saw 23 % shorter than the oracle's zebra
            _tupizna = constants.Tupizna;
            _durak = constants.Durak;
            _canDash = constants.Dash;
            _isFlyer = constants.Flyer;
            _maxSpok = constants.MaxSpok;

            if (_sensors != null)
            {
                _sensors.VisionMultiplier = constants.VisionMultiplier;
            }

            // `flyer` used to be derived here from the family id, and it now comes from the constants
            // table above with the rest of the class facts. `UnitMerc.as:22` and `UnitEncl.as:18` set it
            // in their constructors; nothing in `AllData` authors `<un flyer>` (grep: 0), so the family
            // id is still the only input — it just has one reader now instead of two.

            // ── The attack branch, read off the weapon the controller rolled ──────────────────
            //
            // AS3 derives `attackerType` in the CONSTRUCTOR from the weapon it rolled and the unit's own
            // `krep` (`UnitRaider.as:257-272`), and every attack decision below branches on it. Both
            // halves live on the controller: the roll needs the weapon table (to skip an id
            // `Weapon.create` would reject) and the mount (to equip), neither of which a brain has.
            //
            // The block this replaces classified the unit from its ID — `raider1..4` melee,
            // `raider5..8` ranged, `raider9` thrown — and, for every other variant, from substrings of
            // the FIRST candidate's id (`contains "club"` → melee, `contains "grenade"` → thrown). Both
            // are inventions. `raider1`'s list is six melee candidates and `raider5`'s is five firearms,
            // but `raider2` mixes `bat`/`spear` with `mach`, `slaver1` mixes `pipe`/`cknife` with
            // `hunt`/`lshot`, and `zebra`/`encl`/`merc` are mixed too — the oracle's answer depends on
            // which candidate the difficulty-gated roll accepted, which is a per-spawn draw.
            //
            // 0 when there is no controller at all: the brain is then attached to something that is not
            // an ArmedShooter and has no weapon, which is AS3's own `!currentWeapon → 0` answer.
            _attackerType = ShooterController != null ? ShooterController.AttackerType : 0;

            // ── `aiAttackOch`, the authored value then the magazine rule ──────────────────────────
            //
            // `UnitRaider.getXmlParam()` writes the row's `<un och>` (`:319-321`), then the CONSTRUCTOR
            // — which runs after the weapon roll — overwrites a still-zero value from the weapon's
            // magazine (`:217-219`). Both halves matter: `och == 0` is the 10 %-per-frame branch, and
            // deriving it from `holder` is what makes a 12-round rifle fire in bursts while a 6-round
            // revolver does not.
            //
            // The derivation needs the MOUNT, which does not exist yet when this runs during
            // `Initialize` (the controller rolls, then calls `base.Initialize`, then equips). That is why
            // `OnWeaponEquipped` re-enters this method once the weapon is on the unit — see
            // `ArmedShooterController.EquipRolledWeapon`.
            int och = def.aiAttackOch;
            if (och == 0)
            {
                WeaponDefinition primary = ShooterController != null && ShooterController.WeaponMount != null
                    ? ShooterController.WeaponMount.Definition
                    : null;

                if (primary != null && primary.magazineSize > 10)
                {
                    och = Mathf.RoundToInt(primary.magazineSize / 2f);
                }
            }

            _aiAttackOch = och;

            // `if(this.attackerType <= 0) this.stalkDist = 0;` (`UnitRaider.as:275-278`) — a contact
            // attacker never hovers, whatever its family authors.
            if (_attackerType <= 0) _stalkDistance = 0f;
        }

        /// <summary>
        /// Re-derive the tuning once the rolled weapon is mounted.
        ///
        /// <para><b>Why a hook and not a poll.</b> `_aiAttackOch`'s magazine rule
        /// (<c>UnitRaider.as:217-219</c>) is a <i>constructor</i> fact — it is computed once, from the
        /// weapon the unit ended up holding, and never recomputed. The port's equivalent moment is the
        /// end of `ArmedShooterController.EquipRolledWeapon`, which is the first point at which the
        /// mount exists. Re-entering the whole tuning is idempotent (every field it writes is derived),
        /// so no separate partial-update path can drift from the full one.</para>
        /// </summary>
        public void OnWeaponEquipped() => ApplyDefinitionTuning();

        public override void SimTick(int tickIndex)
        {
            // Release last tick's trigger before the state machine runs. AS3's `attack()` is called once
            // per frame and decides from scratch whether `currentWeapon.attack()` happens, so a frame in
            // which no branch fires is a frame with the trigger up.
            //
            // This has to PUSH the release, not merely clear a local flag. `EnemyWeaponMount` latches
            // `_firing` until told otherwise, and `base.SimTick` ends with `TickWeaponMounts` — so a unit
            // that stops attacking (target lost, state drops to Alert/Patrol) would otherwise leave the
            // trigger held forever and keep shooting while it walks away. `ExecuteAttackAction` re-arms
            // this within the same tick, before the mount is advanced.
            //
            // It stays OUTSIDE the freeze guard below on purpose: a frozen shooter must stop firing, and
            // this is the line that says so.
            _weaponTriggerHeld = false;
            SetWeaponTrigger(false);
            ReleaseSecondaryTrigger();

            // ── AS3's freeze gate, and where it sits relative to these decrements ──────────────────
            //
            // `UnitRaider.control()` checks `World.w.enemyAct <= 0` at :680 and returns at :684. The
            // `aiLaz`/`aiJump`/`aiTCh` decrements are the lines AFTER it (:686-697). So a frozen
            // shooter's cadence timers stop advancing, and un-freezing resumes the tick phase where it
            // left off rather than restarting it.
            //
            // `base.SimTick` has the same gate and returns early, but a callee returning does not stop
            // this method — so the guard has to be repeated here, or the timers would keep counting
            // down under a freeze and the phase would be wrong on resume.
            if (!EnemyActivity.IsFrozen)
            {
                _jumpCooldownTicks = UnitJumpMath.Tick(_jumpCooldownTicks);
                if (_aiAttackT > 0) _aiAttackT--;
                if (_aiTCh > 0) _aiTCh--;

                // AS3's own site is `if(enemyAct > 1 && aiTCh % 10 == 1) --aiSpok;` — the
                // `enemyAct > 1` half is `EnemyActivity.RunsCadence`, which at every value the console
                // can write (3 or 0) is the same test as the `IsFrozen` guard this block is already
                // inside. Written once rather than twice; see `EnemyActivity.CadenceFloor`.
                if (_aiSpok > 0 && tickIndex % 10 == 0) _aiSpok--;
            }

            // ── overLook → the sensors' cone switch, BEFORE the sensors run ────────────────────────
            //
            // `findCel()` is called at `UnitRaider.as:794`, which is ~380 lines ABOVE the state bodies
            // that write `overLook` (:1074, :1078, :1178) — so in the oracle a frame's sighting uses the
            // PREVIOUS frame's flag. Latching from `_familyState` here, before `base.SimTick` runs
            // `Evaluate`, reproduces that ordering exactly rather than approximately.
            //
            // Without this the flag stayed false for every raider in every state, so the facing cone
            // applied during a chase: the player steps behind the shooter and `TargetUnit` goes null on
            // that tick, leaving `ChaseLastKnownPosition` to walk to a stale point. The oracle's chasing
            // raider tracks through 360° (`look(target, true)` drops the behind-gate at `:4598`).
            bool? overLook = ArmedShooterStateMath.OverLookForFamilyState(_familyState);
            if (overLook.HasValue)
            {
                _overLook = overLook.Value;
            }

            _sensors.Omnidirectional = _overLook;

            base.SimTick(tickIndex);

            // ── AS3 `findCel()`'s re-arm: `if(celUnit) { this.replic("attack"); aiSpok = maxSpok + 10; }` ──
            //
            // `_aiSpok` was declared, read by `TickIdle` and by the state-4 gate below, and **written by
            // nothing but `Alarma`** — so it started at 0 and only ever drained. Two consequences, both
            // reported as behaviour: `TickIdle`'s `_aiSpok >= _maxSpok` promotion was unreachable, and
            // the ranged raider's hold state (`aiSpok >= maxSpok + 8`) could never be entered at all.
            //
            // In the oracle the value is re-armed on **every visible frame** — `findCel()` runs inside the
            // `aiTCh % 10 == 1` block, and its `if(celUnit)` arm assigns `maxSpok + 10` — so while the
            // player is in sight `aiSpok` sits pinned at its maximum and the hold gate is permanently
            // open. When sight breaks it drains at one per ten ticks, which is what closes the gate.
            //
            // `TargetUnit != null` is the port's `celUnit`: `EnemySensors.Evaluate` nulls it on the first
            // obscured tick and leaves it set only on a sighting commit. Gated on the freeze for the same
            // reason the oracle's site is inside `enemyAct > 1`.
            if (!EnemyActivity.IsFrozen && _blackboard.TargetUnit != null)
            {
                _aiSpok = _maxSpok + 10;

                // `findCel()`'s committed-target arm (`UnitRaider.as:821-824`):
                //     else { this.replic("attack"); aiSpok = maxSpok + 10; … }
                // i.e. a unit that is ALREADY engaged shouts again while it can still see the player.
                // `aiState > 1` is `_familyState > 1`, and the latch is last tick's value because
                // `findCel` runs before the state bodies in the oracle too. The cooldown inside
                // `TryReplic` is what keeps this from being a line per frame.
                if (_familyState > 1)
                {
                    _controller.TryReplic(ReplicKind.Attack);
                }
            }

            // The secondary's countdown is NOT ticked here. `base.SimTick` ends in `TickWeapons`, which
            // advances every mount — so a trigger set after it would be released at the top of the next
            // tick before any mount ever saw it. `TickWeapons` below is where it runs.

            // AS3 `UnitRaider.control()` runs the flyer block (`:938`, `:955-980`) before the state
            // machine's own movement, so this tick's steering reads a flight state decided this tick.
            // `IsFlying` reaches the motor's gravity pass on the following step — which is the oracle's
            // own ordering, since `forces()` runs before `control()`.
            UpdateFlight();

            // `currentWeapon.rot` is rewritten from `celX`/`celY` on every frame of `control()`
            // (`:663-667`), including the frames the trigger is up. Publishing it here — after the
            // sensors and the state machine have run, so the goal is this tick's — is what makes the
            // gun track a moving player instead of snapping to the facing between shots.
            UpdateAim();

            if (_isFly)
            {
                // A flying unit has no platform to drop through and no jump to make. The release is
                // asserted rather than merely skipped: `SetDropThroughPlatforms` is a level, so not
                // calling it would leave the last ground-side answer latched.
                SetDropThroughPlatforms(false);
            }
            else
            {
                UpdateDropThrough();
                UpdateJump(tickIndex);
            }
        }

        /// <summary>
        /// Hold or release the rolled weapon's trigger, and keep its aim on the target.
        /// </summary>
        /// <remarks>
        /// <para><b>Aim is in Unity world units and must not be scaled.</b> The value feeds
        /// <c>WeaponHoldPointMath.Inputs.AimX</c> and <c>RangedWeaponController</c>'s rotation, both of
        /// which speak world units — the alicorn's <c>UpdateWeaponFire</c> documents the 45°-off bug a
        /// <c>* 100f</c> here produces. The <c>TargetDeltaX/Y</c> family really is in pixels; those are
        /// for the AI's range tests and never for the weapon.</para>
        ///
        /// <para><b>Re-read every firing tick</b>, not latched at the decision, so a shot follows a
        /// moving player — which is what the oracle does, since <c>attack()</c> resolves its direction
        /// from the unit's current target each frame.</para>
        /// </remarks>
        protected virtual void SetWeaponTrigger(bool firing)
        {
            ArmedShooterController ctrl = ShooterController;
            if (ctrl == null) return;

            EnemyWeaponMount mount = ctrl.WeaponMount;
            if (mount == null) return;

            if (firing && _blackboard.TargetUnit != null)
            {
                // `setCel`'s point, not the target's origin. AS3's `celY` is `param1.Y - param1.scY/2`
                // (`Unit.as:4708`), i.e. mid-body, plus a quarter-width bias along the target's facing
                // (`:4707`); a unit's transform sits on its feet, so aiming at `transform.position`
                // points the barrel half a body too low — visible as a gun that fires into the floor
                // when the shooter is a griffin hovering at head height.
                mount.SetAimTarget(EnemySensors.GetUnitAimWorld(_blackboard.TargetUnit));
            }

            mount.SetFiring(firing);
        }

        /// <summary>
        /// Point the held weapon at the unit's goal on <b>every</b> tick — AS3 <c>UnitRaider.control()</c>
        /// <c>:663-667</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The oracle writes <c>currentWeapon.rot</c> every frame, not only on a firing
        /// frame.</b> <c>if(currentWeapon &amp;&amp; currentWeapon.drot &gt; 0) currentWeapon.rot =
        /// Math.atan2(celY - currentWeapon.Y, Math.abs(celX - currentWeapon.X) * storona)</c>, from the
        /// same <c>celX</c>/<c>celY</c> the movement uses. The port published an aim only from
        /// <see cref="SetWeaponTrigger"/>, i.e. on the ticks the trigger was held — so between shots the
        /// gun snapped back to <c>EnemyWeaponMount</c>'s facing-only fallback and never followed the
        /// player, which is the reported "its weapon does not turn".</para>
        ///
        /// <para><b>Cheap and idempotent.</b> <c>SetAimTarget</c> is two field writes; the mount reads
        /// the value once per tick and the presenter once per frame.</para>
        /// </remarks>
        protected virtual void UpdateAim()
        {
            if (_controller == null || !_controller.IsAlive) return;

            ArmedShooterController ctrl = ShooterController;
            if (ctrl == null) return;

            EnemyWeaponMount mount = ctrl.WeaponMount;
            if (mount == null) return;

            mount.SetAimTarget(ResolveAimWorldPoint());
        }

        /// <summary>
        /// The world point the held weapon should point at — the port's <c>celX</c>/<c>celY</c>, in
        /// <b>world units</b> (the unit the mount, the presenter and the ranged controller all speak).
        /// </summary>
        /// <remarks>
        /// <para><b>Three cases, all the oracle's.</b> A live target gets <c>setCel(unit)</c>'s point —
        /// half a body up and a quarter body along the target's facing (<c>Unit.as:4707-4708</c>) — and
        /// that is <b>not</b> the same as the centre <see cref="EnemySensors.GetUnitCenterPixels"/>
        /// returns: the lead is part of the aim. A remembered point (a sound, or the last sighting)
        /// gets that point as-is, because the oracle's <c>setCel(null, x, y)</c> stores raw numbers.
        /// With no goal at all the weapon points at the ahead-point <c>control()</c> maintains for an
        /// idle unit: <c>celY = Y - scY; celX = X + scX * storona * 2;</c> (<c>:682-683</c>), i.e. two
        /// body widths forward at head height — <see cref="EnemyAimMath.AheadCel"/>.</para>
        /// </remarks>
        protected Vector2 ResolveAimWorldPoint()
        {
            if (_blackboard.TargetUnit != null)
            {
                return EnemySensors.GetUnitAimWorld(_blackboard.TargetUnit);
            }

            if (TryResolveCelPixels(out Vector2 goalPx, out _))
            {
                return goalPx * TileQueryConstants.PixelToUnit;
            }

            return EnemyAimMath.AheadCel(
                (Vector2)_controller.transform.position * 100f,
                _controller.SpriteSizePixels,
                _controller.FacingDirection) * TileQueryConstants.PixelToUnit;
        }

        /// <summary>
        /// Decide this tick's secondary trigger <b>before</b> the mounts are advanced, then let the base
        /// advance them.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this override exists.</b> <see cref="UpdateSecondaryWeapon"/> used to run after
        /// <c>base.SimTick</c>, which ends in <c>TickWeapons</c>. The sequence was therefore: release the
        /// secondary at the top of tick N, advance every mount with the trigger up, set the trigger at
        /// the tail of tick N — and then release it again at the top of tick N+1 <i>before</i> any mount
        /// could consume it. The trigger was never true while a mount ticked, so <c>mercgr</c>,
        /// <c>robomlau</c> and <c>robogas</c> never fired at all. Nothing threw and nothing logged.</para>
        ///
        /// <para><b>Why here and not before <c>base.SimTick</c>.</b> <see cref="UpdateSecondaryWeapon"/>
        /// reads <c>_familyState</c>, which the state machine inside <c>base.SimTick</c> decides. Sitting
        /// on the <c>TickWeapons</c> seam keeps that read <i>this</i> tick's — the same tick the mount
        /// consumes — which is the oracle's ordering: <c>attack()</c> runs from the state machine's combat
        /// arms and the weapon's own step follows in the same frame.</para>
        /// </remarks>
        protected override void TickWeapons()
        {
            UpdateSecondaryWeapon();
            base.TickWeapons();
        }

        /// <summary>
        /// AS3's secondary weapon countdown — <c>UnitMerc.attack()</c>'s <c>t_gren</c> tail
        /// (<c>UnitMerc.as:88-95</c>, identical in <c>UnitEncl.as:103-114</c>) and
        /// <c>UnitRanger.attack()</c>'s (<c>UnitRanger.as:69-81</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Three differences between the two families, all in the oracle's text:</b></para>
        /// <list type="bullet">
        /// <item><description><b>Merc / encl</b> — one weapon (<c>mercgr</c>), re-armed to
        /// <c>round(random*150+50)</c>, and it only throws when there is a target <i>and</i> a 50 % roll
        /// passes: <c>if(Boolean(celUnit) &amp;&amp; isrnd()) this.thWeapon.attack();</c>.</description></item>
        /// <item><description><b>Ranger</b> — two weapons, re-armed to <c>round(random*90+50)</c>, and it
        /// always throws one: <c>robomlau</c> with a target, <c>robogas</c> without.</description></item>
        /// <item><description><b>The timer re-arms whether or not the throw happened.</b> The re-arm is
        /// outside the <c>if</c> in both classes, so a mercenary that failed its roll waits a full period
        /// rather than retrying next frame. Moving it inside the guard would make the rocket fire on the
        /// first frame a target appeared, every time.</description></item>
        /// </list>
        ///
        /// <para><b>Gated on the combat states.</b> AS3 sets <c>aiAttack = 1</c> for states 3, 4, 6 and 8
        /// and calls <c>attack()</c> only then (<c>:1442-1452</c>); <c>attack()</c> is not called while
        /// patrolling, so a griffin does not lob rockets at nothing.</para>
        /// </remarks>
        protected virtual void UpdateSecondaryWeapon()
        {
            ArmedShooterController ctrl = ShooterController;
            if (ctrl == null) return;
            if (ctrl.SecondaryWeaponIds.Count == 0) return;

            // `UnitRaider.control()` returns at `:684` when `World.w.enemyAct <= 0`, and `attack()` is
            // called from the tail of `control()` — so a frozen shooter's `t_gren` does not advance and
            // cannot throw. This runs on the `TickWeapons` seam, which is outside `SimTick`'s own freeze
            // block (the base returns from `SimTick` only), so the guard has to be repeated here.
            if (EnemyActivity.IsFrozen) return;

            if (_familyState != 3 && _familyState != 4 && _familyState != 6 && _familyState != 8)
            {
                return;
            }

            if (_secondaryTimer < 0)
            {
                _secondaryTimer = UnityEngine.Random.Range(
                    ctrl.SecondaryPeriodMinTicks, ctrl.SecondaryPeriodMaxTicks + 1);
            }

            if (--_secondaryTimer > 0) return;

            bool hasTarget = _blackboard.TargetUnit != null;

            bool mayFire = !ctrl.SecondaryFiresOnlyWithTarget
                || (hasTarget && UnityEngine.Random.value < 0.5f);

            if (mayFire)
            {
                // Ranger: `if(celUnit) dopWeapon1.attack(); else dopWeapon2.attack();`
                int index = hasTarget || ctrl.SecondaryWeaponIds.Count < 2 ? 0 : 1;

                EnemyWeaponMount mount = ctrl.GetSecondaryMount(index);
                if (mount != null)
                {
                    // The same `celX`/`celY` the primary reads — AS3 maintains ONE aim point per unit
                    // and both `currentWeapon` and `thWeapon` read it (`WThrow.shoot()` uses
                    // `owner.celX`/`celY`, `Unit.as:4707-4708`). Aiming the rocket at
                    // `transform.position` pointed it at the target's feet, i.e. half a body low, and
                    // left the ranger's no-target `robogas` throw with a stale point instead of the
                    // oracle's ahead-of-the-body idle aim.
                    mount.SetAimTarget(ResolveAimWorldPoint());
                    mount.SetFiring(true);
                    _secondaryFiring = index;
                }
            }

            // Outside the `mayFire` guard, exactly as in the oracle — and on the RE-ARM range, which
            // is not the initial one for the ranger (`UnitRanger.as:80` = `random*90+50`).
            _secondaryTimer = UnityEngine.Random.Range(
                ctrl.SecondaryRearmMinTicks, ctrl.SecondaryRearmMaxTicks + 1);
        }

        /// <summary>
        /// Release last tick's secondary trigger. Called at the top of every tick, beside
        /// <see cref="SetWeaponTrigger"/>'s release.
        /// </summary>
        protected virtual void ReleaseSecondaryTrigger()
        {
            if (_secondaryFiring < 0) return;

            ShooterController?.GetSecondaryMount(_secondaryFiring)?.SetFiring(false);
            _secondaryFiring = -1;
        }

        protected virtual void UpdateDropThrough()
        {
            if (_controller == null) return;
            if (_familyState == 2 || _familyState == 3 || _familyState == 4)
            {
                bool targetBelow = _blackboard.HasLastKnownTargetPosition && _blackboard.TargetDeltaY > 80f;
                bool nearFloor = _tileQuery != null && UnitDropThroughMath.IsNearRoomFloor(transform.position.y * 100f, _tileQuery.OriginPixel.y);
                SetDropThroughPlatforms(targetBelow && !nearFloor);
            }
            else
            {
                SetDropThroughPlatforms(false);
            }
        }

        /// <summary>
        /// AS3 <c>UnitRaider.control()</c>'s jump request (<c>:1179-1182</c>) — <c>aiVNapr &lt; 0 &amp;&amp;
        /// this.aiJump &lt;= 0 &amp;&amp; aiTCh % 2 == 1 &amp;&amp; this.checkJump()</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>This method never fired, and the proof is arithmetic.</b> The old body had two gates
        /// that cannot both hold:</para>
        /// <list type="bullet">
        /// <item><description><c>_blackboard.TargetDeltaY &lt; -40f</c> — and <c>TargetDeltaY</c> is
        /// <i>target centre minus this unit's eye</i> (<c>EnemySensors.cs:713</c>), where the eye is the
        /// body's centre (<c>GetEyePositionPixels</c>). For two 70 px bodies that reduces to
        /// <i>feet − feet</i>, so the test means <b>"the target is 40 px BELOW me"</b>.</description></item>
        /// <item><description><c>UnitJumpMath.ShouldJump(feet + 30, LastKnownTargetPosition.y)</c> — and
        /// the stored position is the target's <b>feet</b> (<c>EnemySensors.cs:710</c>), so this means
        /// <b>"the target's feet are 70 px ABOVE mine"</b>.</description></item>
        /// </list>
        /// <para>A body that must be simultaneously 40 px below and 70 px above is dead code, which is why
        /// no raider has ever jumped in this port — and why a 40 px lip, which the motor's 10 px step-up
        /// cannot take, left them pushing at the geometry.</para>
        ///
        /// <para><b>The oracle's four terms, restored one for one:</b></para>
        /// <list type="number">
        /// <item><b>the state</b> — <c>aiState == 2 || 3 || 6 || 8</c>. The port has no state 6 (its
        /// grenade arm is not a movement state), so 2, 3 and 8.</item>
        /// <item><b>the cooldown</b> — <c>aiJump &lt;= 0</c>, armed by every <c>jump()</c> call.</item>
        /// <item><b>the parity gate</b> — <c>aiTCh % 2 == 1</c>. Read from <c>_aiTCh</c> rather than from
        /// the tick index, because this brain <i>has</i> the oracle's counter: it is decremented every
        /// tick exactly as <c>:692-695</c> does and re-armed every 100..199 ticks by the chase re-roll, so
        /// its parity is the oracle's.</item>
        /// <item><b>the target overhead</b> — <c>aiVNapr &lt; 0</c>, i.e. <c>celDY &lt; -40</c>, i.e.
        /// <see cref="UnitJumpMath.ShouldJump"/> on the two quantities the oracle compares: the
        /// <b>target's centre</b> against this unit's <b>top</b>. Both are recovered from the blackboard's
        /// own centre-to-centre delta, which is what <c>ZombieBrain.UpdateJump</c> already does — no
        /// second position definition, and no dependence on the feet-stored last-known position.</item>
        /// <item><b>grounded + headroom</b> — <c>jump()</c>'s <c>if(stay)</c> and
        /// <c>checkJump()</c>'s four probes, the same <see cref="EnemyBrain.HasJumpHeadroom"/> the zombie
        /// uses.</item>
        /// </list>
        /// </remarks>
        protected virtual void UpdateJump(int tickIndex)
        {
            if (_controller == null || !_controller.IsGrounded) return;
            if (_jumpCooldownTicks > 0) return;

            if (_familyState != 2 && _familyState != 3 && _familyState != 8) return;

            // AS3 `aiTCh % 2 == 1` — every other tick.
            if (_aiTCh % 2 != 1) return;

            // `aiVNapr < 0`: the target's centre is more than 40 px above this unit's top.
            if (!IsTargetAbove()) return;

            if (_controller.Stats == null || _controller.Stats.JumpForce <= 0f) return;
            if (!HasJumpHeadroom()) return;

            JumpScaled(1f);
        }

        /// <summary>
        /// <c>aiVNapr &lt; 0</c> (<c>:930-932</c>) — is the target above this unit's top by more than
        /// 40 px?
        ///
        /// <para><b>Why the centre and not the feet.</b> AS3 compares <c>celY</c> (the target's centre,
        /// <c>setCel</c>: <c>param1.Y - param1.scY/2</c>) against <c>Y1 = Y - scY</c> (this unit's top).
        /// The blackboard carries the same difference in centre-to-centre form, so both ends are
        /// recovered from it rather than from a stored feet position — the two agree by construction with
        /// <c>ZombieBrain.UpdateJump</c>, which reads the identical pair.</para>
        /// </summary>
        protected bool IsTargetAbove()
        {
            if (!_blackboard.HasLastKnownTargetPosition) return false;

            Vector2 eyePx = GetEyePositionPixels();
            float halfHeightPx = UnitHeightPixels * 0.5f;
            float unitTopPx = eyePx.y + halfHeightPx;
            float targetCentrePx = eyePx.y + _blackboard.TargetDeltaY;

            return UnitJumpMath.ShouldJump(unitTopPx, targetCentrePx);
        }

        /// <summary><c>aiVNapr == 1</c> — the target's centre is more than 40 px <b>below</b> this unit's
        /// top. The mirror of <see cref="IsTargetAbove"/>, on the same measurement.</summary>
        protected bool IsTargetBelow()
        {
            if (!_blackboard.HasLastKnownTargetPosition) return false;

            Vector2 eyePx = GetEyePositionPixels();
            float unitTopPx = eyePx.y + UnitHeightPixels * 0.5f;
            float targetCentrePx = eyePx.y + _blackboard.TargetDeltaY;

            return ArmedShooterStateMath.IsTargetBelow(
                unitTopPx, targetCentrePx, UnitJumpMath.TargetAboveJumpPixels);
        }

        /// <summary><c>celDY &gt; 100</c> — the target is far enough below that a jump would carry this
        /// unit away from it. Read by the wall reaction (<c>:1392</c>).</summary>
        protected bool IsTargetWellBelow()
        {
            if (!_blackboard.HasLastKnownTargetPosition) return false;

            Vector2 eyePx = GetEyePositionPixels();
            float unitTopPx = eyePx.y + UnitHeightPixels * 0.5f;
            float targetCentrePx = eyePx.y + _blackboard.TargetDeltaY;

            return ArmedShooterStateMath.IsTargetBelow(
                unitTopPx, targetCentrePx, ArmedShooterStateMath.WallJumpTargetBelowPixels);
        }

        /// <summary>This unit's body height in pixels, for the two <c>celDY</c> comparisons.</summary>
        protected float UnitHeightPixels => (_controller != null && _controller.Stats != null)
            ? _controller.Stats.Height * 100f
            : 70f;

        /// <summary>
        /// AS3 <c>UnitRaider.jump(param1)</c> (<c>:574-601</c>) — an impulse of <c>jumpdy * magnitude</c>
        /// plus the forward shove, and the cooldown the call itself arms.
        /// </summary>
        /// <remarks>
        /// <para><b>The cooldown belongs to <c>jump()</c>, not to its callers.</b>
        /// <c>aiJump = Math.floor(30 + Math.random() * 50)</c> is the first line of the oracle's function,
        /// before the <c>if(stay)</c> that applies the impulse — so <i>every</i> jump site arms it, and a
        /// ledge hop therefore suppresses the next overhead jump. Arming it at one call site (as the port
        /// did) would leave the other three sites free to re-jump every tick.</para>
        ///
        /// <para><b>The shove is real and it is what makes a raider look lively.</b>
        /// <c>if(aiNapr == -1) dx *= 0.8; else dx += storona * accel * 2;</c> — a jump carries the unit
        /// two acceleration units in its facing direction. <c>AddForce</c> is the port's <c>dx +=</c>
        /// (both are px/frame against the motor's <c>dx</c>), and the <c>* 0.8</c> damping on the
        /// leftward case is <b>not</b> modelled: the seam offers a desired speed, not a velocity, so there
        /// is nothing to scale. Recorded rather than faked, because the asymmetry is the oracle's own
        /// (it is a `if`/`else`, not a mirror) and reproducing half of it would be worse than reproducing
        /// none.</para>
        /// </remarks>
        protected virtual void JumpScaled(float magnitude)
        {
            // `this.aiJump = Math.floor(30 + Math.random() * 50);` — the oracle's FIRST line, before the
            // `if(stay)` that applies the impulse. So a jump attempted in mid-air still costs the
            // cooldown, and arming it after the grounded test would let a falling raider re-arm every
            // tick and land with a full cooldown instead of a spent one.
            //
            // An int draw: the float overload is inclusive of its upper bound and can produce 80, one
            // tick longer than the oracle can ever give.
            _jumpCooldownTicks = UnitJumpMath.CooldownTicks(
                UnityEngine.Random.Range(0, UnitJumpMath.CooldownRollRange));

            float jumpForce = (_controller != null && _controller.Stats != null)
                ? _controller.Stats.JumpForce
                : 0f;

            // `Unit.as:1890`'s `jumpdy <= 0` refusal — `bigrobot` carries 0, so "cannot jump" is a real
            // data property and not a missing value.
            if (jumpForce <= 0f) return;

            // `jump()`'s `if(stay)`. Every caller checks grounded too, but this is the gate the oracle
            // puts inside the function and it is the only one that cannot be forgotten by a new caller.
            if (_controller == null || !_controller.IsGrounded) return;

            JumpVertical(jumpForce * magnitude);

            if (_motor != null)
            {
                float accel = _controller.Stats != null ? _controller.Stats.Acceleration : 0f;
                if (accel > 0f)
                {
                    int facing = _controller.FacingDirection >= 0 ? 1 : -1;
                    _motor.AddForce(new Vector2(facing * accel * 2f, 0f));
                }
            }
        }

        /// <summary>
        /// The flyer half of <c>UnitRaider.control()</c> — <c>:938</c> (arm on a target well above),
        /// <c>:955-971</c> (arm after six airborne ticks), <c>:974-980</c> (the bob) and
        /// <c>:1288-1334</c> (steer, hover, land).
        ///
        /// <para><b>What was missing, and why it looked ported.</b> Every piece downstream of this method
        /// already existed: <c>UnitFlyPhysics.ApplyBrake</c> is a literal transcription of the
        /// <c>isFly</c> arm of <c>forces()</c>, <c>TilePhysicsController.ApplyGravity</c> calls it
        /// instead of applying gravity, and the merc family's <c>fly</c> clip (row 1, 14 frames,
        /// looping) is imported into all five assets. What was absent was the <i>state</i>: nothing ever
        /// set <c>flyer</c>, nothing ever set <c>isFly</c>, and <c>EnemyController</c> inherited
        /// <c>IsFlying =&gt; false</c> from <c>UnitController</c>, so the motor's flyer branch could not
        /// be taken by any enemy in the game. A griffin therefore walked, jumped and fell like a raider
        /// while holding a griffin's sprite.</para>
        ///
        /// <para><b>The steering is the oracle's shape, not its arithmetic.</b> AS3 accumulates into
        /// <c>dx</c>/<c>dy</c> and lets <c>forces()</c> damp the result; the port's brain seam is a
        /// <i>desired speed</i> (<see cref="MoveHorizontal"/>) and a <i>desired velocity</i>
        /// (<see cref="SetVerticalSpeed"/>), because the motor owns the velocity's storage. So the
        /// current velocity is read back, the thrust vector is computed the same way — normalised,
        /// capped at <c>accel</c>, scaled down by <c>dist²/10000</c> inside 100 px — and the two are
        /// added, exactly as <c>dx += spd.x</c> does. The 0.85 hover damping is applied to the same
        /// read-back velocity rather than replaced by a hard stop.</para>
        ///
        /// <para><b>Both halves of the bob are applied, and the goal is recomputed every tick.</b> The
        /// previous version dropped <c>floatX</c> (a horizontal bob "fighting the motor's
        /// <c>MoveTowards</c>") and steered on <c>EnemyBlackboard.TargetDeltaX/Y</c>. Those deltas are
        /// written <b>only on a sighting commit</b> (<c>EnemySensors.cs:699-700</c>), so they are frozen
        /// the moment line of sight breaks and are <b>zero</b> for a unit that was only ever alerted by
        /// sound — which is a griffin that thrusts at nothing and just bobs up and down in place. The
        /// oracle rewrites <c>celX</c>/<c>celY</c> every frame through <c>setCel</c>; see
        /// <see cref="TryResolveCelPixels"/>.</para>
        /// </summary>
        protected virtual void UpdateFlight()
        {
            if (!_isFlyer || _controller == null || !_controller.IsAlive)
            {
                _isFly = false;
                return;
            }

            // ── Acquisition ─────────────────────────────────────────────────────────────────────────
            // `if(this.flyer && celDY < -80) isFly = true;` (:938). The port's Y grows upward, so the
            // oracle's negative delta is a positive one here.
            if (_blackboard.HasLastKnownTargetPosition
                && _blackboard.TargetDeltaY > FlyOnTargetAbovePixels)
            {
                _isFly = true;
            }

            // `if(flyer) { if(stay) t_fall = 0; else { ++t_fall; if(t_fall > 6) isFly = true; } }`
            // (:955-971). `stay` is the oracle's grounded flag.
            //
            // ── The landing rule needs a PUSH into the floor, not mere contact ───────────────────
            // `:1313-1331` counts frames in which `turnY < 0`, and `turnY` is written by `run()` when a
            // DOWNWARD move is blocked by the floor. A flyer that merely rests on the floor with
            // `dy == 0` never sets it and therefore never lands. The port's `IsGrounded` is a proximity
            // probe ("is there a tile 2 px under the feet"), which a resting flyer satisfies every
            // tick — so the old test dismounted a griffin the instant it touched down, and a
            // dismounted mercenary is a `sniper` with `moving == false`, i.e. one that stands still for
            // the rest of the fight. A strictly downward velocity is the port's closest equivalent of
            // `turnY < 0`: the unit is driving itself into the floor.
            if (_controller.IsGrounded)
            {
                _tFall = 0;

                bool pressingDown = _controller.VelocityPixelsPerFrame.y < 0f;
                if (_isFly && pressingDown)
                {
                    if (++_tLanding > LandingTicks)
                    {
                        _isFly = false;
                        _tLanding = 0;
                    }
                }
                else
                {
                    // Consecutive contact only: a griffin that touches down, lifts off the floor on
                    // the next bob tick and touches down again has not been pressing into it for
                    // twenty frames.
                    _tLanding = 0;
                }
            }
            else
            {
                _tLanding = 0;
                if (++_tFall > FlyAfterFallTicks) _isFly = true;
            }

            if (!_isFly) return;

            // ── The bob, `:974-980` ─────────────────────────────────────────────────────────────────
            _tFloat += FloatPhaseStep;
            float floatX = Mathf.Sin(_tFloat);
            float floatY = Mathf.Cos(_tFloat) * 2f;

            // ── The goal, AS3 `celX`/`celY` ─────────────────────────────────────────────────────────
            bool hasGoal = TryResolveCelPixels(out Vector2 goalPx, out bool goalIsLiveTarget);

            Vector2 selfCenterPx = EnemySensors.GetUnitCenterPixels(_controller);
            Vector2 delta = hasGoal ? goalPx - selfCenterPx : Vector2.zero;

            // `aiNapr`/`tstor` follow `celDX` in `control()`, and the whole body sprite — the weapon's
            // parent — mirrors on `storona`. Without this a griffin that lost its target never turned,
            // so its gun stayed on the side it was born facing.
            if (hasGoal && Mathf.Abs(delta.x) > 5f)
            {
                ApplyFacing(delta.x > 0f ? 1 : -1);
            }

            // The oracle accumulates into `dx`/`dy` and lets `forces()` damp the result; the port's
            // seam is a desired velocity, so the current one is read back and the thrust added to it.
            // That is what keeps the 0.7 speed cap and the `brake` deadzone in `ApplyFlightBrake`
            // meaningful: they are what stop an accumulating thrust from growing for ever.
            Vector2 velocity = _controller.VelocityPixelsPerFrame;

            // `if(Boolean(celUnit) && rasst2 < this.stalkDist * this.stalkDist)` (:1291-1299) — close
            // enough to a target it can actually SEE: hold station. `celUnit` is a live committed
            // target, not "has ever seen one" — a griffin hunting a last-known position must keep
            // flying to it, which is the whole reason the two cases are separate in the oracle.
            if (goalIsLiveTarget && delta.sqrMagnitude < _stalkDistance * _stalkDistance)
            {
                // `dx *= 0.85; dx += floatX; dy += floatY;`
                //
                // The horizontal half coasts to rest and then sways on the spot. It is written this
                // way rather than as one `dx * 0.85 + floatX` because the measured velocity already
                // contains last tick's `floatX`, and feeding the bob back through a 0.85 filter
                // integrates it: `v = 0.85 v + 1` settles at 6.7 px/frame, a griffin sliding sideways
                // across the room. The oracle is safe from that only because `brake = 1` absorbs a
                // bob of 0.5 (`UnitFlyPhysics` documents the pairing for `UnitNPC`); `UnitRaider`'s
                // bob is 2, four times the value the deadzone was sized for.
                float hoverX = velocity.x * HoverDamping;
                if (Mathf.Abs(hoverX) < 1f)
                {
                    hoverX = floatX;
                }

                MoveHorizontal(hoverX);
                SetVerticalSpeed(floatY);
                return;
            }

            // `else { spd.x = celX - X; spd.y = celY - (Y - scY/2); norma(spd, min(accel,
            // accel * dist² / 10000)); dx += spd.x + floatX; dy += spd.y + floatY; }` (:1300-1325).
            float accel = _controller.Definition != null && _controller.Definition.acceleration > 0f
                ? _controller.Definition.acceleration
                : 2f;

            float distSq = delta.sqrMagnitude;
            float thrust = Mathf.Min(accel, accel * distSq / 10000f);

            Vector2 step;
            if (distSq > 0.0001f)
            {
                step = delta * (thrust / Mathf.Sqrt(distSq));
            }
            else
            {
                step = Vector2.zero;
            }

            // Horizontal accumulates (momentum toward the goal, capped by the flyer brake); vertical
            // is commanded outright. The vertical bob is deliberately NOT fed back into the command —
            // same integration argument as the hover above, and on this axis it would be a slow climb
            // to the ceiling rather than a sway.
            MoveHorizontal(velocity.x + step.x);
            SetVerticalSpeed(step.y + floatY);
        }

        /// <summary>
        /// The flight goal in <b>world pixels</b> — AS3 <c>celX</c>/<c>celY</c>, which
        /// <c>setCel</c> rewrites every frame (<c>Unit.as:4703-4733</c>: the target's centre while it is
        /// committed, the last committed point otherwise).
        /// </summary>
        /// <remarks>
        /// <para><b>Why the blackboard's cached deltas cannot be used for this.</b>
        /// <c>TargetDeltaX/Y</c> and <c>TargetDistance</c> are written on exactly one branch of
        /// <c>EnemySensors.Evaluate</c> — the sighting commit (<c>EnemySensors.cs:695-700</c>). They
        /// are frozen the moment line of sight breaks, and a sound-only commit never writes them at
        /// all, so they stay at their <c>Reset</c> values of <c>0</c>. A flyer steering on them either
        /// thrusts along a stale vector or thrusts at nothing: it hovers, bobs, and never closes.</para>
        ///
        /// <para><b>One recorded approximation.</b> <c>LastKnownTargetPosition</c> is stored at the
        /// target's <i>feet</i> (<c>EnemySensors.cs:697</c>) while the oracle's <c>celY</c> is its
        /// centre, so a flyer heading for a remembered position aims half a body low. It is not
        /// corrected here because the same field drives the walkers' jump height
        /// (<c>UnitJumpMath.ShouldJump</c>) and the F3 overlay, where the feet are the right point.</para>
        /// </remarks>
        /// <param name="goalPx">The goal, in world pixels.</param>
        /// <param name="isLiveTarget">
        /// <c>true</c> when the goal is the currently committed target — the oracle's
        /// <c>celUnit != null</c>, which is the gate on the hover branch.
        /// </param>
        protected bool TryResolveCelPixels(out Vector2 goalPx, out bool isLiveTarget)
        {
            isLiveTarget = false;

            if (_blackboard.TargetUnit != null)
            {
                goalPx = EnemySensors.GetUnitCenterPixels(_blackboard.TargetUnit);
                isLiveTarget = true;
                return true;
            }

            // A sound-only commit is `setCel(null, X + rand*200 - 100, Y + rand*200 - 100)`
            // (`Unit.as:4683-4686`) — a GOAL, with `celUnit` deliberately left null. Without this arm a
            // griffin that was only ever alerted by a gunshot has no goal at all and hovers where it
            // stands; the walkers get this from `EnemyBrain.TickAlert`, which a flyer never reaches
            // because its own movement ladder runs after the state machine.
            if (_blackboard.HasHeardNoise)
            {
                goalPx = _blackboard.LastHeardNoisePosition;
                return true;
            }

            if (_blackboard.HasLastKnownTargetPosition)
            {
                goalPx = _blackboard.LastKnownTargetPosition;
                return true;
            }

            goalPx = Vector2.zero;
            return false;
        }

        public override void Alarma(float sourceX = -1f, float sourceY = -1f)
        {
            base.Alarma(sourceX, sourceY);
            if (_familyState <= 1)
            {
                _aiSpok = _maxSpok + 10;
                _familyState = 3;
                SetState(EnemyAIState.CombatChase);
            }
        }

        protected override void TickIdle(int tickIndex)
        {
            _familyState = 0;

            // `UnitRaider.as:1060` / `:1079` — `if(!this.quiet && isrnd(0.001)) this.replic("neutral");`
            // The `quiet` test belongs here and not inside `TryReplic`: it mutes the idle roll only, so
            // a pinned sniper placement still shouts when it engages and still gets a death rattle.
            if (!_controller.PlacementIsQuiet && UnityEngine.Random.value < 0.001f)
            {
                _controller.TryReplic(ReplicKind.Neutral);
            }

            if (_aiSpok >= _maxSpok && _blackboard.TargetUnit != null)
            {
                _familyState = 3;
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);

                // AS3 `UnitRaider.control()` :725-742 — the wander decision is gated on `stroll`:
                //     if(this.stroll) aiState = Math.floor(Math.random() * 2);
                //     else            aiState = 0;
                // so a pinned raider rolls nothing and stands. The dice are still consumed when it is
                // pinned, deliberately: skipping the `Random.value` call would shift the shared stream and
                // change every other unit's rolls for the rest of the room.
                if (!StaysPut && UnityEngine.Random.value < 0.5f)
                {
                    SetState(EnemyAIState.Patrol);
                    return;
                }
            }

            if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
            {
                // Spotted deciding state — `aiTCh = floor(rand*20 + tupizna)` for a ranged attacker,
                // `… + tupizna/4` for a melee one (`UnitRaider.as:810-821`).
                _familyState = 5;
                _aiTCh = ArmedShooterStateMath.SpottedDelayTicks(
                    _tupizna, _attackerType, UnityEngine.Random.Range(0, ArmedShooterStateMath.SpottedRollRange));
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_blackboard.HasHeardNoise)
            {
                // `findCel()`'s heard-only arm (`UnitRaider.as:826-829`): `replic("ear")` with the
                // budget left one short, so hearing something can never promote into the chase. The
                // port has that invariant in `EnemyAwarenessMath.SoundOnlyAwarenessTicks`; the bark is
                // placed here, at the one-shot transition, rather than per tick — a noise event is a
                // single event, and `TryReplic`'s cooldown would otherwise turn it into a monologue.
                _controller.TryReplic(ReplicKind.Ear);
                SetState(EnemyAIState.Alert);
                return;
            }

            StopMovement();
        }

        protected override void TickPatrol(int tickIndex)
        {
            _familyState = 1;

            // `UnitRaider.as:1079` — the same 0.1 % idle roll in the walk state. Two call sites, one
            // condition: the oracle duplicates the line in both state bodies rather than sharing it.
            if (!_controller.PlacementIsQuiet && UnityEngine.Random.value < 0.001f)
            {
                _controller.TryReplic(ReplicKind.Neutral);
            }

            if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
            {
                _familyState = 5;
                _aiTCh = ArmedShooterStateMath.SpottedDelayTicks(
                    _tupizna, _attackerType, UnityEngine.Random.Range(0, ArmedShooterStateMath.SpottedRollRange));
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_blackboard.HasHeardNoise)
            {
                _controller.TryReplic(ReplicKind.Ear);
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);
                SetState(EnemyAIState.Idle);
                return;
            }

            HandleLedgeAhead(tickIndex);
            float speed = _isWalker ? 2.5f : _walkSpeed;
            MoveHorizontal((_controller != null && _controller.FacingDirection < 0 ? -1f : 1f) * speed);
        }

        protected override void TickAlert(int tickIndex)
        {
            // Drop the movement commit. AS3 seeds `aiNapr` from `storona` in the constructor and never
            // clears it, so it survives a whole patrol — but the port has no constructor to seed from and
            // a stale commit from a chase that ended forty seconds ago would send a freshly alarmed
            // raider the wrong way until its first `aiTCh % 15` re-assert. Clearing it here makes the
            // chase's own `if (_aiNapr == 0)` arm point the unit at the target on the tick it commits,
            // which is the same observable answer the oracle reaches on its next re-assert.
            _aiNapr = 0;

            if (_familyState == 5)
            {
                StopMovement();
                if (_aiTCh <= 0)
                {
                    // `if(celUnit)` in `UnitRaider.as:698-715` — a COMMITTED target, not merely a
                    // non-null one. See the identical note in `RobotBrain.TickAlert`: a stale
                    // `TargetUnit` plus a drained budget promoted into a chase that bailed on its
                    // first tick. The `else` arm is the oracle's `else { aiState = 1; }`.
                    if (_blackboard.TargetUnit != null && !ChaseBudgetExhausted)
                    {
                        // `UnitRaider.as:698-715` — the spotted timer expiring with the target still in
                        // sight: `if(celUnit) { this.replic("attack"); aiSpok = maxSpok + 10; … }`. This
                        // is the engagement bark; the ongoing re-shout lives at `findCel`'s re-arm.
                        _controller.TryReplic(ReplicKind.Attack);

                        _familyState = 3;
                        SetState(EnemyAIState.CombatChase);
                    }
                    else
                    {
                        _familyState = 1;
                        SetState(EnemyAIState.Patrol);
                    }
                }
                return;
            }

            _familyState = 2;
            base.TickAlert(tickIndex);
        }

        /// <summary>
        /// AS3 <c>UnitRaider.control()</c>'s combat arm — the state bodies at <c>:1174-1416</c> (states
        /// 2/3/6/8), <c>:1418-1428</c> (state 4) and <c>:1430-1434</c> (state 7), plus the <b>re-roll</b>
        /// that chooses between them at <c>:746-780</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>What was wrong with the previous body, in one sentence: it re-decided the state from
        /// the current distance on every tick, and the oracle only re-decides when <c>aiTCh</c> drains.</b>
        /// The consequences were exactly the three reported symptoms:</para>
        /// <list type="bullet">
        /// <item><description><b>Parked in state 4.</b> <c>_attackerType == 2 &amp;&amp; dist &lt;= aiDist
        /// &amp;&amp; dist &gt;= 80</c> is true for the whole engagement of a ranged raider, so it stood
        /// still with <c>speed = 0</c> from the moment it entered its own <c>aiDist</c> (1000 px by
        /// default) until the player closed to 80 px. The oracle's state 4 is a <i>cycle</i>: 70 % of
        /// re-rolls choose it, and its body returns to state 3 the moment the target is inside 200 px —
        /// usually with a backwards hop.</description></item>
        /// <item><description><b>No backwards hop.</b> The whole <c>dx = runSpeed * (celDX &gt; 0 ? -1 : 1)
        /// * 2; jump(0.4);</c> tail (<c>:1421-1427</c>) was absent, so the one thing that makes a raider
        /// look agitated in the original — a flinch out of its own stand-off — never happened.</description></item>
        /// <item><description><b>No wall or ledge reaction.</b> The chase never read
        /// <c>WallLeft</c>/<c>WallRight</c> and never ran the lip hop, so a blocked raider pushed at the
        /// geometry until its awareness budget drained.</description></item>
        /// </list>
        ///
        /// <para><b>Direction is a latch, not a per-tick derivation — and that is the fix for "stuck in
        /// geometry".</b> AS3's <c>aiNapr</c> is re-asserted from <c>celDX</c> only every fifteen ticks
        /// and only when the target is more than 100 px away (<c>:1181-1196</c>); between those it
        /// persists, which is what lets a turn at a wall actually <i>take</i>. The port re-derived the
        /// direction from <c>TargetDeltaX</c> every tick, so any turn was undone on the next frame and the
        /// unit walked straight back into the wall.</para>
        /// </remarks>
        protected override void TickCombatChase(int tickIndex)
        {
            // The hunt ends on the awareness budget, not on the sighting. `EnemySensors` nulls
            // `TargetUnit` on the first obscured tick, so the old `TargetUnit == null → Alert` exit
            // made every archetype forget the player the instant line of sight broke. See
            // `EnemyBrain.ChaseBudgetExhausted`.
            if (ChaseBudgetExhausted)
            {
                // `UnitRaider.as:723-728` — `if(aiSpok == 0) { if(this.aiVKurse && aiState > 1)
                // this.replic("vse"); … }`. The budget running out is the port's `aiSpok == 0`, and a
                // chase that reaches it is by definition `aiState > 1`, so this is the "she got away"
                // line. The oracle's `aiVKurse` guard ("this one personally wronged me") is not modelled
                // — see the open-work note; without it every raider says it, which is the oracle's
                // behaviour for a raider that actually saw the player.
                _controller.TryReplic(ReplicKind.Vse);

                SetState(EnemyAIState.Alert);
                return;
            }

            if (_blackboard.TargetUnit == null)
            {
                // Still hunting a target it cannot see: keep walking to the last place it was seen,
                // at chase speed, until the budget drains. AS3 `UnitRaider.control()`'s state-3 arm
                // steers on `celX`/`celY` exactly like this.
                ChaseLastKnownPosition(tickIndex);
                return;
            }

            float dx = _blackboard.TargetDeltaX;
            float distSq = _blackboard.TargetDistanceSq;

            // ── AS3 `aiState == 7`: `walk = 0; aiNapr = tstor = celX > X ? 1 : -1;` (:1430-1434) ────
            //
            // The oracle's 7 → 8 transition sits in the `aiTCh` chain *before* the general re-roll
            // (`:737-741`), so it is an `else if` of it and must be tested first. Reachable only for a
            // family whose constructor sets `dash` — the zebra alone (`UnitZebra.as:25`); see `_canDash`.
            if (_familyState == ArmedShooterStateMath.DashWindUp)
            {
                StopMovement();
                ApplyFacing(dx >= 0f ? 1 : -1);

                if (_aiTCh <= 0)
                {
                    // `aiState = 8; aiTCh = 35;` (`:716-720`). The wind-up itself was given 20 ticks by
                    // `NextStateTimerTicks`; this is the charge that follows it.
                    _familyState = ArmedShooterStateMath.Charge;
                    _aiTCh = ArmedShooterStateMath.ChargeTicks;
                }

                return;
            }

            // ── The re-roll (`:746-780`) ────────────────────────────────────────────────────────────
            //
            // States 2 and 3 share one body in the oracle (`aiState == 2 || 3 || 6 || 8`), so a unit
            // promoted out of Alert with `_familyState == 2` is already running the chase; only the
            // speed differs, and `_durak` gives both the run speed. Normalise before the re-roll so the
            // state-4 arm cannot be reached with a stale 2.
            if (_familyState < ArmedShooterStateMath.Chase)
            {
                _familyState = ArmedShooterStateMath.Chase;
            }

            if (_aiTCh <= 0)
            {
                ReRollFamilyState(distSq);
            }

            // ── `aiState == 4` (:1418-1428) ─────────────────────────────────────────────────────────
            if (_familyState == ArmedShooterStateMath.Hold)
            {
                TickHoldState(dx, distSq);

                // `aiAttack = 1` covers states 3, 4, 6 and 8 (`:1442-1452`), so the hold still fires.
                if (IsTargetInAttackRange())
                {
                    ExecuteAttackAction();
                }

                return;
            }

            // ── `aiState == 2 || 3 || 6 || 8` (:1174-1416) ──────────────────────────────────────────

            // `if(aiTCh % 15 == 1 && aiState != 6) { if(isrnd(durak ? 0.5 : 0.9)) { if(celDX > 100)
            // aiNapr = tstor = 1; if(celDX < -100) aiNapr = tstor = -1; } }` (:1181-1196).
            //
            // Note the dead zone: a target inside 100 px does not re-assert the direction at all, which
            // is why a raider circling the player does not jitter. The roll is consumed unconditionally
            // so the stream does not shift.
            if (_aiTCh % 15 == 1)
            {
                float directionRoll = UnityEngine.Random.value;
                if (directionRoll < (_durak ? 0.5f : 0.9f))
                {
                    if (dx > 100f) _aiNapr = 1;
                    else if (dx < -100f) _aiNapr = -1;
                }
            }

            if (_aiNapr == 0)
            {
                // `aiNapr = storona` in the constructor (`:256`). A brain that enters the chase without
                // ever having patrolled has no direction yet; point it at the target once.
                _aiNapr = dx >= 0f ? 1 : -1;
            }

            // Facing. `if(aiState == 3 && weaponKrep == 1 && attackerType == 2) tstor = celDX > 0 ? 1 : -1`
            // (`:1037-1040`) — a ranged raider faces its target even while stepping away from it; every
            // other class aliases `tstor` to `aiNapr`, so it faces where it walks.
            if (_attackerType == 2)
            {
                if (dx > 5f) ApplyFacing(1);
                else if (dx < -5f) ApplyFacing(-1);
            }
            else
            {
                ApplyFacing(_aiNapr);
            }

            float speed = _familyState == ArmedShooterStateMath.Charge ? _runSpeed * 2.5f
                        : _durak ? _runSpeed : _walkSpeed;

            // `if(storona * aiNapr < 0) maxSpeed = walkSpeed;` (`:1041-1044`) — back-pedalling is a
            // walk. Only reachable for the ranged arm above, since everyone else faces `aiNapr`.
            if (_attackerType == 2 && _controller != null && _controller.FacingDirection * _aiNapr < 0)
            {
                speed = _walkSpeed;
            }

            int moveDir = _aiNapr;

            if (speed > 0f)
            {
                // A flyer takes neither the ledge hop nor the wall reaction: there is no ledge to fall
                // off, and a wall is something it flies over.
                if (!_isFly)
                {
                    moveDir = HandleChaseObstacles(moveDir);
                }

                MoveHorizontal(moveDir * speed);
            }
            else
            {
                StopMovement();
            }

            // Firing / attack
            if (IsTargetInAttackRange())
            {
                ExecuteAttackAction();
            }
        }

        /// <summary>
        /// AS3 <c>UnitRaider.control()</c>'s <c>aiTCh</c>-expiry block (<c>:746-780</c>) — the only place
        /// the chase's state is <i>chosen</i>.
        /// </summary>
        /// <remarks>
        /// <para><b>Three writers, in the oracle's order, and the last one wins:</b></para>
        /// <list type="number">
        /// <item><description><c>if(attackerType == 2 &amp;&amp; aiSpok &gt;= maxSpok + 8 &amp;&amp; dist²
        /// &lt; aiDist²) aiState = isrnd(0.3) ? 3 : 4; else aiState = 3;</c> (<c>:756-763</c>) — the
        /// ranged stand-off, and the reason a raider holds 70 % of its cycles.</description></item>
        /// <item><description><c>if(this.dash &amp;&amp; isrnd(0.3) &amp;&amp; celUnit) aiState = 7;</c>
        /// (<c>:764-767</c>) — dead in the shipped data.</description></item>
        /// <item><description><c>if(!this.moving) aiState = 4;</c> (<c>:768-771</c>) — the pinned sniper
        /// override. It sits <i>after</i> the others so it beats them, and it does not suppress their
        /// rolls.</description></item>
        /// </list>
        ///
        /// <para><b>The cycle is re-armed here and nowhere else.</b> <c>aiTCh = floor(rand*100) + 100</c>
        /// (<c>:772</c>) for the general case, which is what makes the 3↔4 choice a rhythm of 3.3–6.6 s
        /// rather than a per-frame coin flip. `_aiTCh` is decremented in <see cref="SimTick"/> beside the
        /// oracle's own <c>--aiTCh</c>.</para>
        /// </remarks>
        protected virtual void ReRollFamilyState(float distSq)
        {
            bool mayHold = ArmedShooterStateMath.MayHold(
                _attackerType == 2, _aiSpok, _maxSpok, distSq, _aiDist * _aiDist);

            _familyState = ArmedShooterStateMath.SelectChaseState(mayHold, UnityEngine.Random.value);

            // `this.dash && isrnd(0.3)` — short-circuited in the oracle, so the draw is only taken when
            // the flag is set. Only the zebra sets it (`UnitZebra.as:25`); see `_canDash`.
            if (_canDash && UnityEngine.Random.value < 0.3f)
            {
                _familyState = ArmedShooterStateMath.DashWindUp;
            }

            if (HoldsPosition)
            {
                _familyState = ArmedShooterStateMath.Hold;
            }

            _aiTCh = ArmedShooterStateMath.NextStateTimerTicks(
                _familyState, _attackerType,
                UnityEngine.Random.Range(0, ArmedShooterStateMath.StateTimerRollRange));
        }

        /// <summary>
        /// AS3's <c>aiState == 4</c> body (<c>:1418-1428</c>) — stand, face the target, and flinch
        /// backwards out of the target's reach.
        /// </summary>
        /// <remarks>
        /// <code>
        /// walk = 0;
        /// aiNapr = this.tstor = celX > X ? 1 : -1;
        /// if(this.attackerType == 2 &amp;&amp; celDX * celDX + celDY * celDY &lt; 200 * 200) {
        ///    if(isrnd(0.6)) {
        ///       dx = runSpeed * (celDX &gt; 0 ? -1 : 1) * 2;
        ///       this.jump(0.4);
        ///    }
        ///    aiState = 3;
        /// }
        /// </code>
        ///
        /// <para><b>The <c>aiState = 3</c> is outside the roll, and that is load-bearing.</b> The hold
        /// ends the moment a ranged attacker's target is inside 200 px whether or not the flinch fired —
        /// a port that gated the exit on the roll would leave a raider standing in front of a player it
        /// rolled badly against, for the rest of the cycle.</para>
        ///
        /// <para><b>The reversal is a velocity assignment, not a target speed.</b> The oracle writes
        /// <c>dx = -2 * runSpeed</c> outright, so the unit leaves at twice run speed <i>immediately</i>
        /// and spends the next frames being pulled back by state 3's <c>dx += accel</c>. A target speed
        /// held for one tick would only decelerate the unit and the hop would have no retreat in it at
        /// all — see <see cref="SetHorizontalVelocity"/>, which is how the assignment is expressed
        /// through a seam that only offers a target speed.</para>
        /// </remarks>
        protected virtual void TickHoldState(float dx, float distSq)
        {
            // `walk = 0;`
            StopMovement();

            // `aiNapr = this.tstor = celX > X ? 1 : -1;`
            _aiNapr = dx >= 0f ? 1 : -1;
            ApplyFacing(_aiNapr);

            if (!ArmedShooterStateMath.ShouldEndHold(_attackerType == 2, distSq)) return;

            if (ArmedShooterStateMath.ShouldBackHop(UnityEngine.Random.value))
            {
                // `dx = runSpeed * (celDX > 0 ? -1 : 1) * 2` — an ASSIGNMENT, not an acceleration, and
                // that difference is the whole flinch. See `SetHorizontalVelocity`.
                SetHorizontalVelocity(
                    (dx > 0f ? -1f : 1f) * _runSpeed * ArmedShooterStateMath.BackHopSpeedMultiplier);

                JumpScaled(ArmedShooterStateMath.BackHopMagnitude);
            }

            _familyState = ArmedShooterStateMath.Chase;
        }

        /// <summary>
        /// AS3's <c>dx = …</c> — <b>assign</b> the horizontal velocity — expressed through a seam that
        /// only offers an acceleration-limited target speed.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the plain seam is not enough.</b> <see cref="EnemyBrain.MoveHorizontal"/> sets the
        /// motor's <i>desired</i> speed and the motor walks <c>dx</c> toward it at its own acceleration,
        /// so a target of −2·runSpeed held for a single tick moves <c>dx</c> by one acceleration step and
        /// never reverses it. The oracle's state-4 flinch <i>assigns</i> <c>dx</c>, so the unit leaves at
        /// −2·runSpeed instantly and then spends several frames being pulled back by <c>dx += accel</c> —
        /// which is what reads as a hop backwards rather than as a stutter. Doing it with a target speed
        /// produces a vertical jump with no visible retreat, i.e. the animation without the behaviour.</para>
        ///
        /// <para><b>How it is expressed.</b> <c>AddForce</c> is the seam's <c>dx +=</c>, so adding
        /// <c>v − dx</c> <i>is</i> <c>dx = v</c> — exact, and it leaves the subsequent decay to the
        /// motor's own <c>MoveTowards</c> on the next ticks, which is the same shape as the oracle's
        /// per-frame <c>dx += accel</c>. The desired speed is set too, so a motor-less brain (the
        /// <c>UnitController</c> path) still gets the intent.</para>
        /// </remarks>
        private void SetHorizontalVelocity(float speedPixelsPerFrame)
        {
            if (_motor == null)
            {
                MoveHorizontal(speedPixelsPerFrame);
                return;
            }

            _motor.AddForce(new Vector2(speedPixelsPerFrame - _motor.State.Velocity.x, 0f));
            _motor.SetDesiredHorizontalSpeed(speedPixelsPerFrame);
        }

        /// <summary>
        /// The chase's two ground reactions — the ledge half-hop (<c>:1372-1375</c>) and the
        /// <c>turnX</c> wall turn-or-jump (<c>:1379-1414</c>).
        /// </summary>
        /// <returns>
        /// The direction to move this tick. Usually <paramref name="moveDir"/>; flipped when the wall
        /// reaction decides to turn away.
        /// </returns>
        /// <remarks>
        /// <para><b>Where <c>turnX</c> comes from, since the port has no physics latch.</b> AS3's
        /// collision pass sets <c>turnX = -sign(dx)</c> when a horizontal move is blocked
        /// (<c>Unit.as:2070</c>, <c>:2080</c>) and the state body consumes it on the next frame. The
        /// motor's <c>WallLeft</c>/<c>WallRight</c> are that same fact, kept per step
        /// (<c>TilePhysicsController.MoveSingleStep</c>) — so the brain reads the flag the physics pass
        /// just set instead of a latch, which is the same information one frame earlier.</para>
        ///
        /// <para><b>The two branches of the wall reaction are not symmetric.</b>
        /// <c>if(celDX * aiNapr &lt; 0)</c> — the wall is <i>behind</i> the target, so turning away from
        /// it also turns away from the target — is taken immediately and refreshes the patience timer.
        /// The other branch, where the wall is <i>between</i> the unit and its target, tries to jump:
        /// 10 % of frames, a ceiling, a target far below, or no headroom each refuse, and a refusal
        /// costs ten ticks of the timer. Two refusals force the turn. So a blocked raider hops about
        /// once and then commits to going around, rather than bouncing in place.</para>
        ///
        /// <para><b>Both are gated on the unit being grounded, and neither runs for a flyer</b> — the
        /// caller has already excluded <c>_isFly</c>, and AS3's <c>stay</c> tests sit inside the
        /// non-flying branch of the movement ladder.</para>
        /// </remarks>
        protected virtual int HandleChaseObstacles(int moveDir)
        {
            if (_controller == null || _tileQuery == null || _tileQuery.Room == null) return moveDir;
            if (!_controller.IsGrounded) return moveDir;

            // ── `if(stay && isrnd(0.5) && aiVNapr <= 0 && (shX1 > 0.5 && aiNapr < 0 || shX2 > 0.5 &&
            // aiNapr > 0)) _loc2_ = 0.5;` — the chase's only ledge reaction.
            bool atEdge = UnitOverhangMath.IsAtEdgeToward(
                moveDir,
                _controller.OverhangLeft,
                _controller.OverhangRight,
                UnitOverhangMath.ChaseEdgeThreshold);

            if (ArmedShooterStateMath.ShouldHopAtLip(
                    grounded: true,
                    roll: UnityEngine.Random.value,
                    targetBelow: IsTargetBelow(),
                    atEdgeTowardDirection: atEdge))
            {
                JumpScaled(ArmedShooterStateMath.ChaseLipHopMagnitude);
            }

            if (_motor == null) return moveDir;

            // `turnX` — the direction that turns away from the wall.
            bool wallAhead = moveDir < 0 ? _motor.State.WallLeft : _motor.State.WallRight;
            if (!wallAhead) return moveDir;

            int turnX = moveDir < 0 ? 1 : -1;

            // `if(celDX * aiNapr < 0) { aiNapr = this.tstor = turnX; aiTTurn = floor(rand*20) + 5; }`
            if (_blackboard.TargetDeltaX * moveDir < 0f)
            {
                _aiNapr = turnX;
                ApplyFacing(turnX);
                _aiTTurn = ArmedShooterStateMath.WallTurnTicks(
                    UnityEngine.Random.Range(0, ArmedShooterStateMath.WallTurnRollRange));
                return turnX;
            }

            // `--aiTTurn; if(refused) aiTTurn -= 10; else _loc2_ = 1;`
            _aiTTurn--;

            if (ArmedShooterStateMath.ShouldJumpAtWall(
                    hitCeiling: _motor.State.HitCeiling,
                    targetWellBelow: IsTargetWellBelow(),
                    hasHeadroom: HasJumpHeadroom(),
                    roll: UnityEngine.Random.value))
            {
                JumpScaled(1f);
            }
            else
            {
                _aiTTurn -= ArmedShooterStateMath.WallTurnPenalty;
            }

            // `if(aiTTurn < 0 && stay) { aiNapr = this.tstor = turnX; aiTTurn = …; }`
            if (ArmedShooterStateMath.ShouldTurnAtWall(_aiTTurn, _controller.IsGrounded))
            {
                _aiTTurn = ArmedShooterStateMath.WallTurnTicks(
                    UnityEngine.Random.Range(0, ArmedShooterStateMath.WallTurnRollRange));
                _aiNapr = turnX;
                ApplyFacing(turnX);
                return turnX;
            }

            return moveDir;
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;
            float dist = _blackboard.TargetDistance;

            if (_attackerType == 1)
            {
                // AS3 `UnitRaider.attack()`'s melee-weapon gate (`:1496-1499`):
                // `Math.abs(celDX) < 120 && Math.abs(celDY) < currentWeapon.rapid * 8`.
                //
                // The vertical term is the WEAPON's own swing duration times 8 px, not a constant —
                // which is the whole reason a knife (rapid 12 → 96 px) reaches higher and lower than a
                // slow club (rapid 24 → 192 px). The port used a flat 60 px for both. 60 is kept as the
                // fallback for a unit whose mount could not be built (a bare test spawn), so the range
                // test never depends on weapon services existing.
                float rapid = ShooterController != null && ShooterController.WeaponMount != null
                              && ShooterController.WeaponMount.Definition != null
                    ? ShooterController.WeaponMount.Definition.rapid
                    : 0f;
                float yLimit = rapid > 0f ? rapid * 8f : 60f;

                return Mathf.Abs(_blackboard.TargetDeltaX) <= 120f
                    && Mathf.Abs(_blackboard.TargetDeltaY) <= yLimit;
            }

            if (_attackerType == 0 || _attackerType == 3)
            {
                return dist <= OptDistAttackPixels && Mathf.Abs(_blackboard.TargetDeltaY) <= 80f;
            }

            // Ranged. AS3 engages on `aiSpok >= maxSpok + 8 && dist² < aiDist²` (`:756`) — the awareness
            // budget, not a sighting — plus the `isLaz == 0` groundedness gate on the firing branch. The
            // port's `HasLineOfSight` here is stricter than the oracle's and is recorded rather than
            // changed: the ranged branch already requires a target to aim at, and loosening the range
            // test without the `aiAttackOch` work below would fire at nothing.
            return dist <= _aiDist && _blackboard.HasLineOfSight;
        }

        /// <summary>
        /// AS3 <c>UnitRaider.attack()</c> (<c>UnitRaider.as:1483-1553</c>), branch for branch.
        /// </summary>
        /// <remarks>
        /// <para><b>Every branch here calls <c>currentWeapon.attack()</c>, and the port's equivalent is
        /// <see cref="SetWeaponTrigger"/></b> — holding the mount's trigger for this tick and aiming it.
        /// The weapon controller's own <c>t_attack</c> paces the individual rounds, exactly as in the
        /// oracle, so nothing here decides when a bullet leaves.</para>
        ///
        /// <para><b>What this replaced.</b> The old body called <c>TryContactAttack</c> for attackerType 1
        /// (so a spear-armed <c>raider2</c> body-slammed instead of swinging) and, for 2 and 3, fabricated
        /// the damage outright with <c>PendingDamage.Direct</c> — no projectile, no flare, no report, and a
        /// damage number invented from <c>UnitDefinition.damage</c>. The <c>Direct</c> calls survive only
        /// as the fallback for a unit with no mount.</para>
        /// </remarks>
        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive) return;
            if (_blackboard.TargetUnit == null) return;

            // AS3 tests `shok <= 0` inside each branch; hoisting it is equivalent for every branch that
            // exists here, because all four require a live target anyway.
            if (StaggerMath.BlocksAttack(_blackboard.ShockTimerTicks))
            {
                return;
            }

            // `if((attackerType == 0 || aiState == 8) && celUnit && shok <= 0)` — the contact arm, which
            // also owns the state-8 charge: a charging unit damages with its body whatever it is holding.
            if (_attackerType == 0 || _familyState == 8)
            {
                // `attKorp(celUnit, Math.abs(dx) > 8 ? 1 : 0.5)` — a body moving faster than 8 px/frame
                // hits for full scale, a standing one for half. `dx` is the unit's own per-frame x
                // velocity, which is what the motor's State.Velocity carries.
                float speed = Mathf.Abs(_motor != null ? _motor.State.Velocity.x : 0f);
                bool connected = _controller.TryContactAttack(
                    _blackboard.TargetUnit, speed > 8f ? 1f : 0.5f);

                // `if(attKorp(...)) { if(aiState == 8) aiTCh = 5; }` (`:1486-1493`) — a landed charge hit
                // cuts the remaining charge short. Without it the zebra keeps ploughing through the
                // player for the whole 35 ticks however many times it connects, instead of bouncing off
                // and re-deciding, which is the difference between a charge and a wall.
                if (connected && _familyState == ArmedShooterStateMath.Charge)
                {
                    _aiTCh = ArmedShooterStateMath.ChargeHitTicks;
                }

                return;
            }

            switch (_attackerType)
            {
                case 1: AttackWithMeleeWeapon(); break;
                case 2: AttackWithRangedWeapon(); break;
                case 3: AttackWithThrownWeapon(); break;
            }
        }

        /// <summary>
        /// AS3's <c>attackerType == 1</c> arm (<c>:1496-1506</c>) — swing the held <c>tip == 1</c> weapon,
        /// then take the independent point-blank body hit.
        /// </summary>
        /// <remarks>
        /// <code>
        /// if(Math.abs(celDX) &lt; 120 &amp;&amp; Math.abs(celDY) &lt; currentWeapon.rapid * 8 &amp;&amp; shok &lt;= 0 &amp;&amp; isrnd(0.3))
        ///    currentWeapon.attack();
        /// if(isrnd(0.1))
        ///    attKorp(celUnit, 0.5);
        /// </code>
        ///
        /// <para><b>The 0.3 is a per-frame trigger probability, not a fire rate.</b> The oracle calls
        /// <c>attack()</c> every frame and lets a 30 % roll decide whether the trigger is down; the
        /// weapon's own <c>rapid_act</c> is what paces the swings. So this must be a per-tick roll — a
        /// one-shot decision would swing once per approach.</para>
        /// </remarks>
        protected virtual void AttackWithMeleeWeapon()
        {
            // A unit whose mount could not be built (a bare test spawn, or a missing WeaponDefinition)
            // keeps the old body hit rather than becoming harmless. Gated on range so it is not a
            // per-tick damage aura.
            if (ShooterController == null || ShooterController.WeaponMount == null)
            {
                if (IsTargetInAttackRange())
                {
                    _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
                }
                return;
            }

            if (IsTargetInAttackRange() && UnityEngine.Random.value < 0.3f)
            {
                _weaponTriggerHeld = true;
                SetWeaponTrigger(true);
            }

            // The body hit rides alongside the swing and is not gated on the swing's roll or on range.
            if (UnityEngine.Random.value < 0.1f)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 0.5f);
            }
        }

        /// <summary>
        /// AS3's <c>attackerType == 2</c> arm (<c>:1507-1541</c>) — fire the held weapon on an
        /// <c>aiAttackOch</c> duty cycle.
        /// </summary>
        /// <remarks>
        /// <code>
        /// if(!this.sniper) mazil = aiState == 4 ? 5 : 16;
        /// if(this.aiAttackOch == 0 &amp;&amp; shok &lt;= 0 &amp;&amp; (celUnit != null &amp;&amp; isrnd(0.1) || celUnit == null &amp;&amp; isrnd(0.03)))
        ///    currentWeapon.attack();
        /// if(this.aiAttackOch &gt; 0 &amp;&amp; (!this.sniper || celUnit)) {
        ///    if(this.aiAttackT &lt;= 0) this.aiAttackT = Math.round((Math.random() * 0.4 + 0.8) * this.aiAttackOch);
        ///    if(this.aiAttackT &gt; this.aiAttackOch * 0.25) currentWeapon.attack();
        ///    --this.aiAttackT;
        /// }
        /// if(dist² &lt; 100² &amp;&amp; isrnd(0.1)) attKorp(celUnit, 0.5);
        /// </code>
        ///
        /// <para><b>The <c>aiAttackT &gt; och * 0.25</c> test is a duty cycle, and it is what the port
        /// was missing.</b> <c>aiAttackT</c> is re-armed to 0.8–1.2 × <c>aiAttackOch</c> and then counts
        /// down, so the trigger is held for the top ~75 % of each window and released for the bottom
        /// quarter. The port fired a single fabricated hit at the window's start instead, which is both
        /// the wrong shape and the wrong delivery.</para>
        ///
        /// <para><b><c>mazil</c> is not modelled — and the guard makes a sniper the one case the port
        /// already gets right.</b> The oracle raises the shooter's inaccuracy to 16 px (5 while holding
        /// position, 25 while levitating), but only <c>if(!this.sniper)</c>: <c>Unit.mazil</c> is declared
        /// <c>0</c> (<c>Unit.as:318</c>) and a sniper never overwrites it. That value reaches the weapon
        /// through <c>Pers.mazilAdd</c>, and an NPC has no stat source in this port — so the spread is
        /// the weapon's own for everyone, which is <b>correct for the 17 units whose row authors
        /// <c>&lt;un sniper='1'/&gt;</c> and under-jittered for every other shooter</b>. Recorded in
        /// <c>ThrownWeaponController.Mazil</c> and in the worklog; it needs an NPC stat source, not a
        /// hard-coded literal here.</para>
        ///
        /// <para><b>The other <c>sniper</c> reader is satisfied structurally, not skipped.</b>
        /// <c>if(this.aiAttackOch &gt; 0 &amp;&amp; (!this.sniper || celUnit))</c> (<c>:1525</c>) means a
        /// sniper only holds its burst cadence with a target in view. Every caller of this method has
        /// already required <c>_blackboard.TargetUnit != null</c> (see <c>TickCombatChase</c>'s guard), so
        /// the <c>celUnit</c> term is true by construction and the test reduces to the plain
        /// <c>_aiAttackOch &gt; 0</c> already written below.</para>
        ///
        /// <para><b>What changed for <c>sniper</c> is upstream, not here.</b>
        /// <c>UnitDataImporter.ParseUnitExtras</c> now writes
        /// <see cref="PFE.Data.Definitions.UnitDefinition.isSniper"/> from
        /// <c>&lt;un sniper='1'/&gt;</c>, so <c>_isSniper</c> above stops being permanently
        /// <c>false</c>. It is still <b>not read</b>: the oracle's only other consumer is the
        /// <c>mazil</c> line this paragraph is about, and <c>mazil</c> needs an NPC stat source. The flag
        /// is therefore carried, correct and unconsumed, with its consumer named — rather than left
        /// looking like a flag nothing ever set.</para>
        /// </remarks>
        protected virtual void AttackWithRangedWeapon()
        {
            if (ShooterController == null || ShooterController.WeaponMount == null)
            {
                // No mount to fire through. Keep the old direct report, on the window cadence, so a unit
                // whose weapon could not be equipped is not completely harmless — and so this is visibly
                // the fallback rather than a second damage path that could double-hit.
                if (_aiAttackT <= 0)
                {
                    _aiAttackT = Mathf.RoundToInt(
                        (UnityEngine.Random.value * 0.4f + 0.8f) * _aiAttackOch);
                    FireRangedAttack();
                }
                return;
            }

            if (_aiAttackOch > 0)
            {
                if (_aiAttackT <= 0)
                {
                    // AS3's formula, verbatim. The port used to substitute a flat 40 for a sniper; the
                    // oracle has no such special case — its only sniper test is `(!this.sniper || celUnit)`,
                    // which is already satisfied here because a target is required to get this far.
                    _aiAttackT = Mathf.RoundToInt(
                        (UnityEngine.Random.value * 0.4f + 0.8f) * _aiAttackOch);
                }

                if (_aiAttackT > _aiAttackOch * 0.25f)
                {
                    _weaponTriggerHeld = true;
                    SetWeaponTrigger(true);
                }
            }
            else if (UnityEngine.Random.value < 0.1f)
            {
                // `aiAttackOch == 0` — the constant-free branch, a 10 % per-frame trigger.
                _weaponTriggerHeld = true;
                SetWeaponTrigger(true);
            }

            if (_blackboard.TargetDistance <= 100f && UnityEngine.Random.value < 0.1f)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 0.5f);
            }
        }

        /// <summary>
        /// AS3's <c>attackerType == 3</c> arm (<c>:1542-1552</c>) — throw, then take the point-blank body
        /// hit.
        /// </summary>
        /// <remarks>
        /// <code>
        /// if(Boolean(celUnit) &amp;&amp; isrnd(0.02)) {
        ///    currentWeapon.attack();
        ///    if(currentWeapon is WThrow &amp;&amp; (currentWeapon as WThrow).kolAmmo &lt;= 0) this.attackerType = 0;
        /// }
        /// if(dist² &lt; 100² &amp;&amp; isrnd(0.1)) attKorp(celUnit, Math.abs(dx) &gt; 8 ? 1 : 0.5);
        /// </code>
        ///
        /// <para><b>The reclassification is ported, and it is why <c>RemainingThrows</c> exists.</b> An NPC
        /// thrower carries four rounds (<c>WThrow.kolAmmo</c>); once they are gone AS3 demotes it to a
        /// contact attacker, so it charges instead of standing at range holding nothing. Note the oracle
        /// checks the counter immediately after <c>attack()</c>, in the same frame — <c>getAmmo()</c> has
        /// already decremented it by then, so a throw that spends the last round flips the branch on the
        /// spot.</para>
        /// </remarks>
        protected virtual void AttackWithThrownWeapon()
        {
            float speed = Mathf.Abs(_motor != null ? _motor.State.Velocity.x : 0f);

            if (ShooterController == null || ShooterController.WeaponMount == null)
            {
                // No mount: the old direct report, on the port's own 60–100 tick cadence (there is no
                // oracle cadence to copy for a throw that has no weapon object to throw).
                if (_aiAttackT <= 0)
                {
                    _aiAttackT = UnityEngine.Random.Range(60, 100);
                    FireThrownAttack();
                }
                return;
            }

            if (UnityEngine.Random.value < 0.02f)
            {
                _weaponTriggerHeld = true;
                SetWeaponTrigger(true);

                if (ShooterController.RemainingThrows == 0)
                {
                    _attackerType = 0;
                }
            }

            if (_blackboard.TargetDistance <= 100f && UnityEngine.Random.value < 0.1f)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, speed > 8f ? 1f : 0.5f);
            }
        }

        /// <summary>
        /// Fallback ranged report for a unit whose weapon mount could not be built — a bare test spawn, or
        /// an id with no <see cref="WeaponDefinition"/>.
        /// </summary>
        /// <remarks>
        /// <b>This is no longer the attack.</b> The real path is the rolled weapon's mount, fired through
        /// <see cref="SetWeaponTrigger"/> — a projectile with its own visual, flare and report. This
        /// fabricated <c>PendingDamage.Direct</c> used to BE the ranged attack for every ArmedShooter,
        /// which is why "shooting enemies do not have a weapon" was literally true: nothing was drawn or
        /// played, only a damage number applied. Kept only so a unit whose weapon failed to equip is not
        /// harmless, and gated on the weapon window's cadence so it cannot become a per-tick damage aura.
        /// </remarks>
        protected virtual void FireRangedAttack()
        {
            if (_blackboard.TargetUnit == null || _controller == null) return;

            // No noise here. A real shot is announced by ProjectileSpawner.AnnounceShot from the
            // weapon's own `<snd noise>` (AS3 `Weapon.as:1581`); this path exists only for a unit
            // whose mount failed to build, and it spawns no projectile, so there is no weapon to
            // take a radius from. `MakeNoise(250, true)` was invented.
            // See docs/EnemyImplementationGuides/AUDIT_noise_sources_2026-10-19.md.

            // Directly report simulated ranged combat or contact impact
            // `MeleeDamage`, not `Definition.damage`: a starred enemy's `dam` is multiplied per instance
            // (Unit.as:1650-1669) and the definition is shared by every unit of this id.
            float baseDamage = _controller.MeleeDamage;
            DamageType dType = _controller.Definition != null ? _controller.Definition.damageType : DamageType.PhysicalBullet;

            Vector2 origin = (Vector2)transform.position * 100f;
            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(baseDamage, dType),
                _blackboard.TargetUnit,
                targetPos);

            if (_controller.DamageSystem != null)
            {
                _controller.DamageSystem.Report(pending);
            }
            else
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
        }

        /// <summary>
        /// Fallback thrown report for a unit whose weapon mount could not be built. See
        /// <see cref="FireRangedAttack"/> — same status, same reason.
        /// </summary>
        protected virtual void FireThrownAttack()
        {
            if (_blackboard.TargetUnit == null || _controller == null) return;
            // No noise — same reason as FireRangedAttack: this is the no-mount fallback, and the
            // thrown weapon's own `<snd noise>` is what the oracle charges when it actually lands.

            float baseDamage = _controller.Definition != null ? _controller.MeleeDamage * 1.5f : 15f;
            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(baseDamage, DamageType.Explosive),
                _blackboard.TargetUnit,
                targetPos);

            if (_controller.DamageSystem != null)
            {
                _controller.DamageSystem.Report(pending);
            }
            else
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
        }

        /// <summary>
        /// Animation state resolution matching AS3 UnitRaider.animate() branch order (UnitRaider.as:399-484).
        /// </summary>
        /// <summary>
        /// A port of <c>UnitRaider.animate()</c> (<c>UnitRaider.as:400-484</c>) — the family brain for
        /// <c>raider</c>, <c>slaver</c>, <c>zebra</c>, <c>ranger</c>, <c>encl</c>, <c>merc</c> and
        /// <c>necros</c>.
        ///
        /// <para><b>No <c>trup</c> gate on the death arm</b> — <c>grep -c trup UnitRaider.as</c> is 0, so
        /// unlike <c>UnitAnt</c> and <c>UnitMonstrik</c> this arm is always reachable, and every raider
        /// sheet does author <c>die</c>, <c>death</c> and <c>fall</c>.</para>
        /// </summary>
        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            if (!_controller.IsAlive)
            {
                // UnitRaider.as:402-422 — `if(stay) { if(animState != "fall") { if(animState == "death")
                // "fall"; else "die"; } } else "death";`.
                //
                // The `animState != "fall"` guard is load-bearing and was missing: a body that is already
                // playing `fall` is left alone. Without it a grounded corpse flipped `fall` back to `die`,
                // restarting the death animation every frame instead of settling.
                if (!_controller.IsGrounded) return "death";
                if (ShowingAnimState("fall")) return "fall";
                return ShowingAnimState("death") ? "fall" : "die";
            }

            float currentSpeed = Mathf.Abs(_motor != null ? _motor.State.Velocity.x : 0f);

            // `else if(stay)`: dx == 0 or aiState == 7 -> stay; attacking (attackerType 0) or aiState 8
            // -> run; a walker patrolling -> walk; otherwise -> trot.
            if (_controller.IsGrounded)
            {
                // A grounded flyer plays `walk`, never `trot`/`run`: `UnitMerc.animate()`'s `stay`
                // branch is `if(dx == 0) "stay"; else "walk";` with no third option. This is not
                // cosmetic — `merc1..5` author `trot` and `run` with `length: 0`, so the raider mapping
                // below would show an empty clip, i.e. a griffin with no sprite at all while it walks
                // to its hover distance.
                if (_isFlyer)
                {
                    return currentSpeed < 0.1f ? "stay" : "walk";
                }

                if (currentSpeed < 0.1f || _familyState == 7)
                {
                    return "stay";
                }

                if ((_attackerType == 0 && _currentState == EnemyAIState.CombatChase) || _familyState == 8)
                {
                    return "run";
                }

                if (_isWalker && (_familyState <= 1 || _familyState == 4))
                {
                    return "walk";
                }

                return "trot";
            }

            // `else if(this.flyer) { if(isFly) "derg"; else "stay"; }` is `UnitRaider.animate()`'s arm
            // (`:445-455`), and it is NOT the arm the reported units take: `UnitMerc.animate()`
            // (<c>:196-217</c>) and `UnitEncl.animate()` (<c>:157-170</c>) both override it with
            // `else if(isFly || aiPlav || levit) "fly";` — the clip the merc/encl sheets actually
            // author (`<blit id='fly' y='1' len='14' rep='1'/>`). Returning `derg` here, as this arm
            // used to, would have been the raider's answer for a griffin.
            if (_isFlyer && _isFly)
            {
                return "fly";
            }

            // AS3's remaining arms — `levit` -> `derg`, `isLaz && mostLaz` -> `laz`, `aiPlav` -> `plav` —
            // are not ported: this brain has no levitate, wall-grip or swim state to read, so they have
            // no trigger to hang off. `jump` is the oracle's own final `else` and is what a raider in
            // free fall actually plays.
            return "jump";
        }
    }
}
