using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The robot <i>brain</i> host — <b>not</b> the <c>UnitAIRobot</c> family.
    ///
    /// <para><b>Why <c>AiFamily</c> is deliberately NOT overridden here.</b> This class is the port's
    /// home for ten AS3 classes, and only five of them are <c>extends UnitAIRobot</c>:
    /// <c>UnitRobobrain</c>, <c>UnitSentinel</c>, <c>UnitProtect</c>, <c>UnitEqd</c> and
    /// <c>UnitGutsy</c> — those five read the placement <c>ai</c> as <c>quiet</c> and nothing else
    /// (<c>UnitAIRobot.as:49-52</c>). The other five — <c>UnitDron</c>, <c>UnitVortex</c>,
    /// <c>UnitRoller</c>, <c>UnitMsp</c>, <c>UnitSpriteBot</c> — are <c>extends Unit</c> and read no
    /// <c>aiTip</c> at all. So the override belongs on the five, and the base stays
    /// <see cref="UnitAiFamily.Generic"/>. Putting it here would hand <c>dron</c> and <c>roller</c> a
    /// spelling they do not have.</para>
    /// </summary>
    [RequireComponent(typeof(RobotBrain))]
    public class RobotController : EnemyController
    {
        public const string ControllerId = "UnitAIRobot";
        public const string ControllerAlias = "robot";

        public new RobotBrain Brain => _brain as RobotBrain;

        /// <summary>
        /// Reusable candidate buffer, so a roll allocates nothing. Same shape as the ArmedShooter's.
        /// </summary>
        private readonly List<WeaponOption> _weaponOptions = new List<WeaponOption>(4);

        /// <summary>Whether the roll has run, so a second <c>Initialize</c> cannot re-roll the weapon.</summary>
        private bool _weaponRolled;

        /// <summary>
        /// The weapon id this robot rolled, or <c>null</c> for a robot whose row has no usable
        /// <c>&lt;w&gt;</c> candidate (<c>spritebot</c>, <c>vortex</c>, <c>roller</c>, <c>roller2</c>,
        /// <c>msp</c> — all five attack by contact or by their own rule).
        /// </summary>
        public string EquippedWeaponId { get; private set; }

        /// <summary>
        /// The live mount for the rolled weapon, or <c>null</c> when nothing was rolled or the mount
        /// could not be built. <see cref="RobotBrain"/> fires through this.
        /// </summary>
        public EnemyWeaponMount WeaponMount =>
            EquippedWeaponId != null ? GetWeaponMount(EquippedWeaponId) : null;

        /// <summary>
        /// AS3 <c>UnitAIRobot.getWeapon</c> (<c>UnitAIRobot.as:70-98</c>), which the five
        /// <c>extends UnitAIRobot</c> subclasses call from their constructors:
        /// <c>Robobrain.as:19</c>, <c>Protect.as:25</c>, <c>Gutsy.as:27</c>, <c>Eqd.as:19</c>,
        /// <c>Sentinel.as:26</c>.
        ///
        /// <para><b>Why this exists at all.</b> The robot family fabricated its damage
        /// (<c>RobotBrain.ExecuteAttackAction</c> built a <c>PendingDamage.Direct</c> from
        /// <c>UnitDefinition.damage</c>) and never held a weapon object, so a robobrain's
        /// <c>robolaser</c> — 7 damage, 30 rapid, a laser bolt with its own visual and report — was
        /// replaced by an instant contact hit at the row's generic <c>damage</c> value. The data was
        /// imported and unused: <c>robobrain.asset</c> carries <c>robolaser ch=0.5</c> and
        /// <c>robospark</c> exactly as the row authors them.</para>
        ///
        /// <para><b>The three arms are the oracle's, in the oracle's order.</b>
        /// <c>param4.weap</c> (a runtime override object) has no port equivalent, so the chain is
        /// <c>param3.@weap</c> (the placement) → <c>getXmlWeapon(param2)</c> (the row's roll, gated by
        /// the location's difficulty). <c>weap=""</c> is "explicitly unarmed" and must not fall
        /// through to the roll — see <see cref="UnitPlacementWeapon"/>.</para>
        /// </summary>
        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            // Before the roll: `UnitDron` picks its weapon FROM `tr` (`UnitDron.as:84-91`), so the
            // variant has to be known before a weapon is chosen.
            VariantTr = ResolveVariantTr(definition);

            if (!_weaponRolled)
            {
                _weaponRolled = true;
                RollWeapon(definition);
            }

            base.Initialize(definition, stats);

            EquipRolledWeapon();
        }

        /// <summary>
        /// AS3 <c>Unit.tr</c> — the per-spawn variant index — recovered from the id the spawner baked.
        /// </summary>
        /// <remarks>
        /// <para><b>Read back off the id rather than rolled here.</b> AS3 resolves <c>tr</c> first and
        /// then builds the id from it (<c>UnitDron.as:47-56</c>: <c>id = "dron" + tr</c>, or
        /// <c>"dront"</c> when <c>tr == 100</c>; <c>UnitGutsy.as:17-21</c>:
        /// <c>id = "gutsy"; if(tr &gt; 0) id += tr</c>). This port's
        /// <see cref="PFE.Systems.Map.UnitVariantResolver"/> performs that same selection at spawn time
        /// and composes the identical id, so by the time this controller exists the answer is already in
        /// <c>definition.id</c> — the same argument <c>FlyerController</c> makes for <c>_variant</c>.
        /// Rolling again here would consume a second random draw and could disagree with the id the
        /// spawner already committed to.</para>
        ///
        /// <para><b><c>dront</c> maps back to 100, and that is the only id that needs it.</b> The mega
        /// dron's row is a separate <c>&lt;unit&gt;</c> with no <c>&lt;w&gt;</c> of its own
        /// (<c>AllData.txt:1975</c>) precisely because <c>UnitDron</c> builds its weapon by name when
        /// <c>tr == 100</c>. Without the reverse mapping a <c>dront</c> would read <c>tr == 0</c> and end
        /// up unarmed.</para>
        /// </remarks>
        protected int VariantTr { get; private set; }

        private static int ResolveVariantTr(UnitDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.id)) return 0;

            if (string.Equals(definition.id, "dront", System.StringComparison.Ordinal)) return 100;

            // `TierOf` returns -1 for an id with no trailing digits — "no variant", which the oracle's
            // own `else` arm turns into 0 for every robot but the drone.
            int tr = PFE.Core.Scripting.UnitSpawnCatalog.TierOf(definition.id);
            return tr < 0 ? 0 : tr;
        }

        /// <summary>
        /// The family's <c>setWeaponPos</c> overrides, keyed by the two things this port already knows
        /// about a robot at equip time.
        /// </summary>
        /// <remarks>
        /// <para><b>Every member of this family overrides the base rule, and each with a different
        /// constant.</b> Measured off the oracle, in Flash pixels above the feet:
        /// <c>UnitRobobrain</c> and <c>UnitProtect</c> 40 (<c>Robobrain.as:51</c>,
        /// <c>Protect.as:124</c>), <c>UnitSentinel</c> 90 (<c>:97</c>), <c>UnitSpriteBot</c> 1
        /// (<c>:66</c>), <c>UnitDron</c> <c>scY/2</c> plus 40 <i>down</i> on the <c>tr == 100</c> mega
        /// drone (<c>:173-181</c>), and <c>UnitGutsy</c> a per-frame bone table (<c>:104-111</c>).
        /// <c>UnitVortex</c>, <c>UnitRoller</c> and <c>UnitMsp</c> override nothing and keep the
        /// inherited <c>Y - scY*0.5</c>.</para>
        ///
        /// <para><b>Why <c>Kind</c> plus <c>SecondaryKind</c> is enough.</b> The four
        /// <c>extends UnitAIRobot</c> robots are all <see cref="RobotKind.Standard"/>, and the two that
        /// differ from the 40-px pair are exactly the two that carry a secondary weapon — gutsy the
        /// flamethrower, sentinel the rocket — which is already a field on the brain. So no id string is
        /// parsed and no new data is imported. The 40-px pair is split by id only to keep the two class
        /// names distinct in the ledger; they are byte-identical rules.</para>
        /// </remarks>
        protected override WeaponHoldRule ResolveHoldRule()
        {
            RobotKind kind = Brain != null ? Brain.Kind : RobotKind.Standard;

            switch (kind)
            {
                case RobotKind.Drone:
                    // `dront` is the only robot whose hold point depends on `tr`.
                    return VariantTr >= 100 ? WeaponHoldRule.DronHeavy : WeaponHoldRule.Dron;

                case RobotKind.SpriteBot:
                    return WeaponHoldRule.SpriteBot;

                case RobotKind.Vortex:
                case RobotKind.Roller:
                case RobotKind.Msp:
                    return WeaponHoldRule.Base;

                default:
                    RobotSecondaryKind secondary =
                        Brain != null ? Brain.SecondaryKind : RobotSecondaryKind.None;

                    if (secondary == RobotSecondaryKind.Rocket) return WeaponHoldRule.Sentinel;

                    // Gutsy's bone table is not imported, so this is the named gap rather than a
                    // fabricated constant — ResolveHold falls back to the base rule for it.
                    if (secondary == RobotSecondaryKind.Flamer) return WeaponHoldRule.WPosTable;

                    return IsProtectId(Definition) ? WeaponHoldRule.Protect : WeaponHoldRule.Robobrain;
            }
        }

        /// <summary>
        /// <c>protect</c> and its <c>protect1</c> variant, which share <c>UnitProtect</c>'s hold point.
        /// </summary>
        private static bool IsProtectId(UnitDefinition definition)
        {
            string id = definition != null ? definition.id : null;
            return !string.IsNullOrEmpty(id)
                && id.StartsWith("protect", System.StringComparison.Ordinal);
        }

        /// <summary>A late weapon-services handover reaches a robot that already ran <c>Initialize</c>.</summary>
        protected override void OnWeaponServicesReady() => EquipRolledWeapon();

        /// <summary>
        /// Re-roll once the placement is known, when the placement's <c>weap</c> decides the answer.
        ///
        /// <para><b>The same arrival-order problem the raider has.</b> AS3 reads <c>param3.@weap</c>
        /// inside the constructor; <c>RoomUnitSpawner</c> calls <c>Initialize</c> (which rolls) before
        /// <c>ApplyPlacement</c>. Only a placement that actually authors <c>weap</c> re-rolls, so no
        /// un-authored robot consumes a second random draw.</para>
        /// </summary>
        public override void ApplyPlacement(UnitInstance placement)
        {
            base.ApplyPlacement(placement);

            if (!_weaponRolled) return;
            if (PlacementWeaponMode == PlacementWeaponMode.Roll) return;

            ClearWeaponMounts();
            RollWeapon(Definition);
            EquipRolledWeapon();
        }

        private void RollWeapon(UnitDefinition definition)
        {
            EquippedWeaponId = null;

            PlacementWeaponMode mode = PlacementWeaponMode;

            switch (mode)
            {
                case PlacementWeaponMode.Fixed:
                    // `Weapon.create(this, param3.weap)` strips a trailing `^n` before the id lookup.
                    EquippedWeaponId = UnitPlacementWeapon.BaseWeaponId(PlacementWeapText);
                    break;

                case PlacementWeaponMode.Unarmed:
                    EquippedWeaponId = null;
                    break;
            }

            if (EquippedWeaponId == null && mode != PlacementWeaponMode.Unarmed)
            {
                WeaponChance[] candidates = definition != null ? definition.weapons : null;
                if (candidates != null && candidates.Length > 0)
                {
                    _weaponOptions.Clear();
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        _weaponOptions.Add(new WeaponOption(
                            candidates[i].weaponId,
                            candidates[i].chance,
                            candidates[i].difficulty,
                            // AS3 `f` — the SECONDARY weapon (gutsy's `robofire`, sentinel's
                            // `robomlau`). The roll steps over it; the subclass grants it by name.
                            candidates[i].isFixedWeapon));
                    }

                    int difficulty = (int)LocationDifficulty;

                    int index = UnitWeaponSelector.SelectIndex(
                        _weaponOptions,
                        difficulty,
                        roll01: () => UnityEngine.Random.value,
                        isAvailable: id => TryGetWeaponDefinition(id, out _));

                    if (index != UnitWeaponSelector.NoWeapon)
                    {
                        EquippedWeaponId = _weaponOptions[index].WeaponId;
                    }
                }
            }

            // `UnitSpriteBot.as:31` — the class builds its weapon rather than rolling one. Reached only
            // when the row offered nothing usable, so a row that does author candidates still wins.
            if (EquippedWeaponId == null && mode != PlacementWeaponMode.Unarmed)
            {
                EquippedWeaponId = HardCodedPrimaryWeaponId;
            }
        }

        private void EquipRolledWeapon()
        {
            if (Stats == null || !HasWeaponServices) return;

            if (!string.IsNullOrEmpty(EquippedWeaponId))
            {
                EquipWeapon(EquippedWeaponId);
            }

            if (!string.IsNullOrEmpty(SecondaryWeaponId))
            {
                EquipWeapon(SecondaryWeaponId);
            }

            // The per-variant weapon tweaks AS3 applies in the subclass constructor, which all need the
            // mount to exist first.
            ApplyVariantTuning();
        }

        /// <summary>
        /// Per-<c>tr</c> weapon adjustments, applied once the mounts exist — today only the gutsy's
        /// doubled flamethrower (<c>UnitGutsy.as:29-33</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Why a hook and not a line in each subclass's <c>Awake</c>.</b> These adjustments
        /// mutate a <i>mount</i>, and the mounts are built in <c>EquipRolledWeapon</c> — which runs from
        /// <c>Initialize</c> and again from <c>OnWeaponServicesReady</c> and <c>ApplyPlacement</c>, so a
        /// one-shot <c>Awake</c> would miss every rebuild after the first and double-apply on the
        /// rebuilds it did catch. Running here means the tuning is re-applied to whichever mount exists,
        /// and the writer below is an idempotent assignment rather than a multiplication.</para>
        ///
        /// <para><b>An assignment, not a multiply.</b> <c>WeaponRuntimeState.DamageScale</c> starts at 1
        /// and this hook runs again on every mount rebuild (<c>OnWeaponServicesReady</c>,
        /// <c>ApplyPlacement</c>), so <c>*= 2</c> here would compound to 4, then 8. AS3's own
        /// <c>*= 2</c> is safe only because it runs once, in a constructor, on a freshly created
        /// weapon.</para>
        /// </remarks>
        private void ApplyVariantTuning()
        {
            if (UnitArchetypeRules.RobotSecondaryKindFor(DefinitionId) != RobotSecondaryKind.Flamer) return;

            EnemyWeaponMount mount = SecondaryMount;
            if (mount == null || mount.Controller == null) return;

            // Only the tr == 1 variant doubles: Location.randomCid returns "1" when the room's
            // tipEnemy == 7 and "0" otherwise, and the id suffix is appended only for a non-zero tr —
            // so `gutsy` is the plain flamethrower and `gutsy1` is the hot one.
            mount.Controller.State.DamageScale = VariantTr == 1 ? 2f : 1f;
        }

        /// <summary>
        /// The robot's second weapon — AS3's <c>dopWeapon</c>, created by name in the constructor
        /// rather than rolled, and therefore present on every instance of that class.
        ///
        /// <para><b>Two robots have one, and the row proves it:</b> <c>gutsy</c>'s <c>robofire</c> and
        /// <c>sentinel</c>'s <c>robomlau</c> are the <c>f='1'</c> rows the roll steps over, and
        /// <c>UnitGutsy.as:28</c> / <c>UnitSentinel.as:27</c> both create them unconditionally — no
        /// ammo gate, unlike the mercenary's <c>mercgr</c>. Resolved from the unit id, the same fact
        /// the deleted <c>GutsyController</c>/<c>SentinelController</c> used to answer by class.</para>
        /// </summary>
        private string SecondaryWeaponId
        {
            get
            {
                switch (UnitArchetypeRules.RobotSecondaryKindFor(DefinitionId))
                {
                    case RobotSecondaryKind.Flamer: return "robofire";
                    case RobotSecondaryKind.Rocket: return "robomlau";
                    default: return null;
                }
            }
        }

        /// <summary>The mount for <see cref="SecondaryWeaponId"/>, or null.</summary>
        public EnemyWeaponMount SecondaryMount =>
            SecondaryWeaponId != null ? GetWeaponMount(SecondaryWeaponId) : null;

        /// <summary>
        /// A weapon this class builds in its constructor instead of rolling one — AS3
        /// <c>UnitSpriteBot.as:31</c>: <c>currentWeapon = new Weapon(this,"robolaser");</c>, and the
        /// mega dron's by-name <c>ttweapN</c> (<c>UnitDron.as:84-91</c>).
        ///
        /// <para>Used only when the row's own <c>&lt;w&gt;</c> roll produced nothing, which is exactly
        /// spritebot's and <c>dront</c>'s case: spritebot authors no <c>&lt;w&gt;</c> rows at all, and
        /// the mega dron's row carries none because its weapon is built when <c>tr == 100</c>.</para>
        ///
        /// <para><b>The dront arm is a roll, so it must stay a single draw.</b> It is drawn here, in
        /// the property <see cref="RollWeapon"/> reads once per roll — a caller that read it twice
        /// would get two different answers and a silent double draw.</para>
        /// </summary>
        private string HardCodedPrimaryWeaponId
        {
            get
            {
                string id = DefinitionId;
                if (id != null && id.StartsWith("spritebot", System.StringComparison.Ordinal))
                {
                    return "robolaser";
                }
                return VariantTr >= 100 ? "ttweap" + UnityEngine.Random.Range(1, 7) : null;
            }
        }

        /// <summary>
        /// AS3 <c>Unit.isFly</c> for the three robots that set it in their constructors and never clear
        /// it — <c>UnitDron.as:67</c>, <c>UnitSpriteBot.as:27</c>, <c>UnitVortex.as:24</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Same defect class as the griffins, and the same shape of fix.</b> Those three
        /// classes each write <c>isFly = true</c> unconditionally in their constructor, so in the oracle
        /// they take the <c>isFly</c> arm of <c>forces()</c> for their whole life: no
        /// <c>dy += World.ddy</c>, no ground brake. The port inherited
        /// <c>UnitController.IsFlying =&gt; false</c> and never overrode it, so a drone, a spritebot and a
        /// vortex were all pulled down by the same gravity every walking unit obeys. <c>UnitDron</c> at
        /// least still asked for its <c>fly</c> clip (<see cref="RobotBrain.ResolveAnimState"/>), which is
        /// why a sinking drone at least <i>looked</i> airborne.</para>
        ///
        /// <para><b>Why these three and not a data flag.</b> Unlike the mercenary's
        /// <c>flyer</c>, which <c>UnitRaider.control()</c> converts into a runtime <c>isFly</c> that comes
        /// and goes with the terrain (<c>UnitRaider.as:940-980</c>), these three are permanent: nothing in
        /// their <c>control()</c> ever writes <c>isFly</c> again. So the capability and the state are the
        /// same fact, and the kind alone decides it — no runtime field, no per-tick update.</para>
        ///
        /// <para><b>The damping is the flyer arm's, not the class's own.</b> <c>UnitSpriteBot.forces()</c>
        /// and <c>UnitVortex.forces()</c> override the <c>isFly</c> arm with <c>dx *= 0.95; dy *= 0.95;</c>
        /// instead of <c>Unit.as</c>'s cap-and-deadzone, and that difference is <b>not</b> ported here:
        /// <c>IsFlying</c> routes them through
        /// <see cref="PFE.Systems.Physics.UnitFlyPhysics.ApplyBrake"/> like every other
        /// flyer. Recorded rather than silently approximated — see the audit's divergence list.</para>
        /// </remarks>
        protected override bool IsFlying =>
            Brain != null
            && (Brain.Kind == RobotKind.Drone
                || Brain.Kind == RobotKind.SpriteBot
                || Brain.Kind == RobotKind.Vortex);

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<RobotBrain>();
            }
        }


        /// <summary>
        /// The placement-<c>ai</c> vocabulary this robot reads — AS3 <c>UnitAIRobot.as:49-52</c> reads
        /// <c>quiet</c> and nothing else, but only on the five <c>extends UnitAIRobot</c> classes
        /// (<c>robobrain</c>, <c>protect</c>, <c>gutsy</c>, <c>sentinel</c>, <c>eqd</c>). The flying and
        /// rolling robots extend <c>Unit</c> and read no <c>aiTip</c> at all, so they keep
        /// <see cref="UnitAiFamily.Generic"/>. Resolved from the unit id — the same fact the deleted
        /// per-class controllers used to answer by inheritance.
        /// </summary>
        protected override UnitAiFamily AiFamily =>
            UnitArchetypeRules.UsesRobotAiFamily(DefinitionId) ? UnitAiFamily.Robot : UnitAiFamily.Generic;

        /// <summary>The unit id this robot was initialized with, or null before <c>Initialize</c>.</summary>
        private string DefinitionId => Definition != null ? Definition.id : null;
    }
}
