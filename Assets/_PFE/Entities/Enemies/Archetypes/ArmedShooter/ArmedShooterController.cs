using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Loot;
using PFE.Systems.Map;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The ArmedShooter family's controller (AS3 <c>fe/unit/UnitRaider.as</c> and its subclasses
    /// <c>UnitSlaver</c>, <c>UnitZebra</c>, <c>UnitRanger</c>, <c>UnitEncl</c>, <c>UnitMerc</c>,
    /// <c>UnitNecros</c>).
    ///
    /// <para><b>What this class owns: the weapon the unit is actually holding.</b> AS3 rolls it in the
    /// constructor — <c>currentWeapon = getXmlWeapon(param2)</c> (<c>UnitRaider.as:179</c>), where
    /// <c>param2</c> is the location's <c>locDifLevel</c> — and then derives <c>attackerType</c> from
    /// what it rolled (<c>:257-272</c>). The brain reads the result and picks its attack branch; this
    /// class performs the roll and holds the answer, which is the same split the alicorn uses (controller
    /// equips, brain fires).</para>
    ///
    /// <para><b>The divergence this replaces.</b> The brain used to classify itself from the unit id
    /// (<c>raider1..4</c> melee, <c>raider5..8</c> ranged, <c>raider9</c> thrown) and from substrings of
    /// the first candidate weapon id (<c>contains "club"</c> → melee, <c>contains "grenade"</c> →
    /// thrown). Both are inventions: the oracle's answer is a function of the weapon it <i>rolled</i> —
    /// a per-spawn random draw over the whole candidate list, gated by difficulty — and of the unit's own
    /// <c>krep</c>. Under the old code a <c>raider1</c> that rolled <c>mach</c> (tip 0, a gun) still
    /// fought as a contact attacker, and no enemy ever held a weapon object at all.</para>
    ///
    /// <para><b>What this class does not own: the secondary weapon.</b> Rows carrying AS3's <c>f</c>
    /// attribute (<c>mercgr</c> on <c>merc1..5</c>, <c>robomlau</c>/<c>robogas</c> on <c>ranger1..3</c>,
    /// <c>mercgr</c> on <c>encl</c>) are <b>excluded from the roll</b> by
    /// <see cref="UnitWeaponSelector.SelectIndex"/> — which is the faithful half, and the half whose
    /// absence silently armed every mercenary with a rocket launcher. Actually <i>firing</i> them is a
    /// separate mechanism in the oracle: the family constructor builds the weapon
    /// (<c>UnitMerc.as:32</c>, <c>UnitEncl.as:29</c>, <c>UnitRanger.as:28-29</c>) and
    /// <c>attack()</c> fires it on its own <c>t_gren</c> countdown, entirely outside
    /// <c>attackerType</c>. That is the next step for this family and is <b>not</b> wired here — these
    /// units currently roll and fire their primary weapon only. Note also that
    /// <c>merc1</c>/<c>merc2</c>/<c>merc4</c> author no <c>&lt;un grenader&gt;</c>, so their <c>mercgr</c>
    /// row is inert even in the oracle (<c>UnitMerc.as:30</c> guards on <c>grenader &gt; 0</c>).</para>
    /// </summary>
    [RequireComponent(typeof(ArmedShooterBrain))]
    public class ArmedShooterController : EnemyController
    {
        public const string ControllerId = "UnitRaider";
        public const string ControllerAlias = "raider";

        /// <summary>
        /// The placement-<c>ai</c> reader this family uses — AS3 <c>UnitRaider.as:227-241</c>, inherited
        /// by <c>UnitEncl</c>, <c>UnitMerc</c>, <c>UnitRanger</c>, <c>UnitZebra</c> and
        /// <c>UnitSlaver</c> because they all extend <c>UnitRaider</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Set here and not on <c>UnitController</c>, because the mapping is a class fact.</b>
        /// This family reads <c>stay</c>, <c>quiet</c> and <c>sniper</c>; a robot reads only
        /// <c>quiet</c>, an alicorn reads <c>stay</c>/<c>quiet</c>/<c>pole</c>, and a hellhound reads
        /// none of them. See <see cref="UnitPlacementAi"/> for the table and
        /// <c>UnitController.PlacementStaysPut</c> for what the answer drives.</para>
        ///
        /// <para><b>This is also the only family whose unit row can clear <c>stroll</c>.</b>
        /// <c>UnitRaider.as:351-354</c> reads <c>node0.un.@stay</c> and no other class reads that
        /// attribute, which is why the row half of <c>PlacementStaysPut</c> is gated on this family.</para>
        /// </remarks>
        /// <summary>
        /// The family's secondary weapons — AS3's <c>thWeapon</c>/<c>dopWeapon</c>, built by the
        /// subclass constructor and fired on their own countdown, outside <c>attackerType</c> entirely.
        ///
        /// <para><b>Why the controller owns the list and not the brain.</b> Which weapon a family
        /// carries is a class fact (<c>UnitMerc.as:30-35</c>, <c>UnitEncl.as:29-34</c>,
        /// <c>UnitRanger.as:28-29</c>), and it is gated on data the controller already reads — the
        /// mercenary's <c>&lt;un grenader&gt;</c> count. The brain only owns the countdown.</para>
        /// </summary>
        private readonly List<string> _secondaryWeaponIds = new List<string>(2);

        /// <summary>The ids in <see cref="_secondaryWeaponIds"/>, for the brain's countdown.</summary>
        public IReadOnlyList<string> SecondaryWeaponIds => _secondaryWeaponIds;

        /// <summary>
        /// Shortest <i>initial</i> <c>t_gren</c>, in ticks — the value the field is declared with
        /// (<c>UnitMerc.as:17</c>: <c>round(random*150+50)</c>; <c>UnitRanger.as:14</c>:
        /// <c>round(random*120+50)</c>).
        /// </summary>
        public int SecondaryPeriodMinTicks { get; private set; }

        /// <summary>Longest <i>initial</i> <c>t_gren</c>, in ticks.</summary>
        public int SecondaryPeriodMaxTicks { get; private set; }

        /// <summary>
        /// Shortest <c>t_gren</c> <i>re-arm</i>, in ticks (<c>UnitMerc.as:153</c>:
        /// <c>random*150+50</c>; <c>UnitRanger.as:80</c>: <c>random*90+50</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Separate from the initial period because the ranger's differ.</b> The mercenary
        /// arms and re-arms on the same 50–200 range, but the ranger is declared with
        /// <c>random*120+50</c> (50–170) and re-armed with <c>random*90+50</c> (50–140) — it starts
        /// patient and then becomes markedly quicker. Collapsing the two into one pair would make
        /// every ranger after its first rocket wait up to 30 ticks longer than the original.</para>
        /// </remarks>
        public int SecondaryRearmMinTicks { get; private set; }

        /// <summary>Longest <c>t_gren</c> re-arm, in ticks.</summary>
        public int SecondaryRearmMaxTicks { get; private set; }

        /// <summary>
        /// <c>true</c> for merc/encl, whose secondary only fires on a 50 % roll <i>and</i> only with a
        /// target in view (<c>UnitMerc.as:88-95</c>); <c>false</c> for the ranger, which always throws
        /// one of its two (<c>UnitRanger.as:69-81</c>).
        /// </summary>
        public bool SecondaryFiresOnlyWithTarget { get; private set; }

        /// <summary>
        /// The mount for a secondary id, or null. Thin wrapper so the brain never has to know whether
        /// the unit is a merc, an enclave soldier or a ranger.
        /// </summary>
        public EnemyWeaponMount GetSecondaryMount(int index)
            => index >= 0 && index < _secondaryWeaponIds.Count
                ? GetWeaponMount(_secondaryWeaponIds[index])
                : null;

        protected override UnitAiFamily AiFamily => UnitAiFamily.Raider;

        /// <summary>
        /// This unit as an <see cref="ArmedShooterBrain"/> — the holder of the runtime <c>isFly</c>
        /// flag, which the base <c>EnemyBrain</c> type does not have.
        /// </summary>
        protected ArmedShooterBrain ShooterBrain => _brain as ArmedShooterBrain;

        /// <summary>
        /// AS3 <c>Unit.isFly</c>, handed to the physics layer.
        ///
        /// <para><b>This override is the difference between a griffin and a raider.</b>
        /// <c>UnitController.IsFlying</c> is what <c>TilePhysicsController.ApplyGravity</c> and
        /// <c>ApplyFriction</c> test to choose the <c>isFly</c> arm of <c>forces()</c> instead of
        /// gravity; both of those were ported and correct, and both were unreachable for an enemy
        /// because the base answer is a constant <c>false</c>. The brain owns the state (it is what
        /// reads the target's height and the fall timer), so the controller only forwards it.</para>
        /// </summary>
        protected override bool IsFlying => ShooterBrain != null && ShooterBrain.IsFlying;

        /// <summary>
        /// Reusable candidate buffer, so a roll allocates nothing. Filled from
        /// <see cref="UnitDefinition.weapons"/> and handed to <see cref="UnitWeaponSelector"/>.
        /// </summary>
        private readonly List<WeaponOption> _weaponOptions = new List<WeaponOption>(8);

        /// <summary>Whether the roll has run, so a second <c>Initialize</c> cannot re-roll the weapon.</summary>
        private bool _weaponRolled;

        /// <summary>
        /// AS3 <c>UnitRaider.attackerType</c> (<c>:83</c>, assigned at <c>:257-272</c>) — how this unit
        /// attacks: 0 contact, 1 melee-weapon swing, 2 ranged, 3 thrown. The brain branches on it; see
        /// <see cref="UnitWeaponSelector.AttackerType"/> for the ladder.
        /// </summary>
        public int AttackerType { get; private set; }

        /// <summary>
        /// The weapon id <see cref="UnitWeaponSelector.SelectIndex"/> accepted, or <c>null</c> when no
        /// candidate was eligible (AS3 <c>getXmlWeapon</c> returning <c>null</c>, which the oracle treats
        /// as "no weapon" and answers with <c>attackerType = 0</c>).
        /// </summary>
        public string EquippedWeaponId { get; private set; }

        /// <summary>
        /// AS3 <c>weaponKrep</c> — the unit's <c>&lt;comb krep&gt;</c>, whose declared default is
        /// <b>1</b> (<c>Unit.as:332</c>) and which is only overwritten when the attribute is present
        /// (<c>Unit.as:1166-1169</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Read from <c>UnitDefinition.isStable</c>, which is the importer's name for exactly
        /// this value</b> (<c>krep == 1</c>). One divergence is recorded rather than hidden: the importer
        /// writes <c>isStable</c> only when <c>&lt;comb krep&gt;</c> exists, so "attribute absent" and
        /// "<c>krep='0'</c>" both read <c>false</c> where the oracle would answer 1 for the first. Of the
        /// 139 <c>&lt;comb&gt;</c> nodes in <c>AllData.as</c>, 45 carry <c>krep</c> and 94 do not — and
        /// <b>every ArmedShooter unit that holds a <c>tip &lt;= 1</c> weapon carries it explicitly</b>
        /// (the only unit without one, <c>necros</c>, has an empty weapon list and therefore lands on
        /// <c>attackerType</c> 0 either way). So this family is exact today. The affected units are 17
        /// Robot / Flyer / Boss rows — <c>dron1..3</c>, <c>owl</c>, the bloat/slime family,
        /// <c>phoenix</c> and four bosses — and they are the follow-up: a dedicated <c>weaponKrep</c>
        /// field plus a re-import, which belongs with those families' passes, not this one.</para>
        /// </remarks>
        public bool WeaponKrep { get; private set; }

        /// <summary>
        /// The live mount for the rolled weapon, or <c>null</c> when nothing was rolled or the mount could
        /// not be built (no definition, or a bare test spawn with no weapon services). The brain fires
        /// through this.
        /// </summary>
        public EnemyWeaponMount WeaponMount =>
            EquippedWeaponId != null ? GetWeaponMount(EquippedWeaponId) : null;

        /// <summary>
        /// Throws left on the rolled weapon's NPC counter, or <c>-1</c> when it is not a thrown weapon or
        /// no mount was built. AS3 reclassifies a thrower whose counter has run out as a contact attacker
        /// (<c>UnitRaider.as:1546-1549</c>); the brain reads this to do the same.
        /// </summary>
        public int RemainingThrows => WeaponMount != null ? WeaponMount.RemainingThrows : -1;

        /// <summary>
        /// The tier of the weapon this unit is holding — AS3 <c>currentWeapon.variant</c>, i.e. the
        /// number that gets appended as <c>^N</c> when the gun is dropped or serialised
        /// (<c>UnitRaider.as:184-187</c>, <c>:516-520</c>).
        ///
        /// <para><b>Two things set it, and both are oracle reads.</b></para>
        /// <list type="number">
        ///   <item><description>the placement's <c>weap='lmg^1'</c>, through
        ///     <see cref="UnitPlacementWeapon.VariantOf"/> — the same suffix
        ///     <see cref="UnitPlacementWeapon.BaseWeaponId"/> strips for the lookup;</description></item>
        ///   <item><description>the hero upgrade, <c>UnitRaider.setHero</c>'s
        ///     <c>updVariant(1)</c> (<c>:362-372</c>), rolled by
        ///     <see cref="EnemyWeaponDropRules.ShouldUpgradeToHeroVariant"/>.</description></item>
        /// </list>
        ///
        /// <para><b>The tier is recorded but not yet applied to the fired stats.</b> AS3's
        /// <c>updVariant</c> re-runs <c>getXmlParam()</c>, so a <c>carbine^1</c> hits harder and holds
        /// more. The port's weapon controllers read the base <see cref="WeaponDefinition"/>, and the
        /// merge that would produce the tier-1 numbers
        /// (<see cref="PFE.Systems.Weapons.WeaponVariantXml.MergeRuns"/>) has no runtime applier — the
        /// same open gap that stops a player's picked-up unique from firing better. It is one gap, and
        /// this field is the identity half of it: the drop, the pickup name and the trader price are
        /// correct, the damage is not.</para>
        /// </summary>
        public int HeldWeaponVariant { get; private set; }

        /// <summary>
        /// AS3 <c>isDropArm</c> — whether this unit's corpse drops the weapon it was holding
        /// (<c>UnitRaider.as:47, 206-209, 514-522</c>). See
        /// <see cref="EnemyWeaponDropRules.IsDropArm"/> for the three things that clear
        /// it; this property only supplies the inputs.
        /// </summary>
        public bool DropsWieldedWeapon => EnemyWeaponDropRules.IsDropArm(
            AiFamily == UnitAiFamily.Raider, FamilyId, _placementNoDrop);

        /// <summary>
        /// The unit row's family key — <c>parentId</c> when it has one (<c>raider1</c> → <c>raider</c>),
        /// otherwise the row's own id. The same expression <see cref="EquipFamilySecondaries"/> uses, and
        /// the only thing that distinguishes <c>encl</c>/<c>ranger</c> from the rest of the family: both
        /// extend <c>UnitRaider</c> and therefore share this controller, but both clear
        /// <c>isDropArm</c> in their constructors.
        /// </summary>
        private string FamilyId
        {
            get
            {
                UnitDefinition def = Definition;
                if (def == null) return null;
                return !string.IsNullOrEmpty(def.parentId) ? def.parentId : def.id;
            }
        }

        /// <summary>
        /// Whether the placement carried a <c>nodrop</c> attribute — <b>presence</b>, not value
        /// (<c>UnitRaider.as:206-209</c>). Two shipped slaver placements author <c>nodrop="1"</c>.
        /// </summary>
        private bool _placementNoDrop;

        /// <summary>
        /// Whether the hero-tier weapon roll has already run. It must run <b>once</b>, after the final
        /// weapon is known: <c>ApplyPlacement</c> can re-roll a placement-fixed weapon, and a second roll
        /// would consume a second random draw and could upgrade a weapon the first one did not.
        /// </summary>
        private bool _heroVariantApplied;

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<ArmedShooterBrain>();
            }
        }

        /// <summary>
        /// Roll the weapon, then let the base run its normal sequence.
        /// </summary>
        /// <remarks>
        /// <para><b>The roll has to precede <c>base.Initialize</c>, and that is not stylistic.</b>
        /// <c>EnemyController.Initialize</c> calls <c>_brain.OnDefinitionAssigned()</c>, which is where
        /// <c>ArmedShooterBrain.ApplyDefinitionTuning</c> reads <see cref="AttackerType"/>. Rolling
        /// afterwards would leave the brain classified from the property's default for the whole life of
        /// the unit — the exact "value written but never read at the right time" shape this port keeps
        /// finding.</para>
        ///
        /// <para>The roll itself only needs the definition and <see cref="EnemyController.LocationDifficulty"/>,
        /// both of which are already set. Equipping needs <c>Stats</c> and the weapon services, so it runs
        /// after the base call — and again from <c>OnWeaponServicesReady</c> if the services arrive later.</para>
        /// </remarks>
        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            if (!_weaponRolled)
            {
                _weaponRolled = true;
                RollWeapon(definition);
            }

            base.Initialize(definition, stats);

            EquipRolledWeapon();
        }

        /// <summary>
        /// A late weapon-services handover reaches an archetype that already ran <c>Initialize</c>, so the
        /// rolled weapon is equipped here. Idempotent: <c>EquipWeapon</c> is per-id, and a unit whose roll
        /// produced nothing returns immediately.
        /// </summary>
        protected override void OnWeaponServicesReady() => EquipRolledWeapon();

        /// <summary>
        /// AS3 <c>UnitRaider</c>'s constructor weapon choice (<c>UnitRaider.as:162-181</c>), then the
        /// <c>attackerType</c> ladder (<c>UnitRaider.as:257-272</c>).
        ///
        /// <para><b>Three arms, and the roll is only the last of them.</b> The oracle's chain is
        /// <c>param4.weap</c> (a runtime override object, no port equivalent) → the placement's
        /// <c>param3.@weap</c> → <c>getXmlWeapon(param2)</c>, the unit row's <c>&lt;w&gt;</c> roll. The
        /// middle arm is what this method now reads; see <see cref="UnitPlacementWeapon"/> for why
        /// <c>weap=""</c> is "explicitly unarmed" rather than "roll anyway".</para>
        ///
        /// <para><b>Why this can run twice.</b> <c>RoomUnitSpawner</c> calls <c>Initialize</c> (which
        /// rolls) before <c>ApplyPlacement</c> (which supplies the placement), so the first roll cannot
        /// see the attribute. The <c>ApplyPlacement</c> override re-rolls when the attribute turns out
        /// to matter, and <see cref="EnemyController.ClearWeaponMounts"/> drops the first weapon. Both
        /// happen before the first <c>SimTick</c>.</para>
        /// </summary>
        private void RollWeapon(UnitDefinition definition)
        {
            WeaponDefinition rolled = null;
            EquippedWeaponId = null;

            // A re-roll must not inherit the previous weapon's tier. `ApplyPlacement` calls this again
            // when the placement names a weapon, and a tier left standing from the first roll would be
            // appended to an id it never belonged to.
            HeldWeaponVariant = 0;

            // ── Arm 2: the placement's `weap` (UnitRaider.as:170-176) ────────────────────────────────
            //
            // Gated on the raider family because that is the only family AS3 lets read it — `UnitNecros`
            // extends `Unit`, not `UnitRaider`, and this controller is shared with it for port
            // convenience.
            //
            // The gate is applied to the MODE, not wrapped around the switch below. Folding it into the
            // switch would leave a necros placement authoring `weap=""` looking like `Unarmed` to the
            // roll guard further down, so it would be disarmed by an attribute its AS3 class never
            // reads. No shipped necros placement authors `weap` today — the nineteen that do are
            // raider/ranger/merc/slaver/zebra — which is precisely why the bug would have been invisible
            // until someone authored one.
            PlacementWeaponMode mode = AiFamily == UnitAiFamily.Raider
                ? PlacementWeaponMode
                : PlacementWeaponMode.Roll;

            switch (mode)
            {
                case PlacementWeaponMode.Fixed:
                    // `Weapon.create(this, param3.@weap)` — which strips a trailing `^n` BEFORE the
                    // id lookup (Weapon.as:352-356). Without the strip, the two shipped placements
                    // that author `lmg^1` and `zebr^1` would resolve to nothing and fight unarmed.
                    EquippedWeaponId = UnitPlacementWeapon.BaseWeaponId(PlacementWeapText);
                    // …and the suffix it stripped is the weapon's TIER, which AS3 keeps
                    // (`currentWeapon.variant`) and re-appends when the corpse is looted
                    // (UnitRaider.as:516-520). Dropping it here is what made those two placements
                    // drop a plain gun.
                    HeldWeaponVariant = UnitPlacementWeapon.VariantOf(PlacementWeapText);
                    TryGetWeaponDefinition(EquippedWeaponId, out rolled);
                    break;

                case PlacementWeaponMode.Unarmed:
                    // `weap=""` — the inner `!= ""` fails, `currentWeapon` stays null, and the roll
                    // below is never reached.
                    EquippedWeaponId = null;
                    rolled = null;
                    break;
            }

            // ── Arm 3: the unit row's roll (Unit.as:1450-1475) ───────────────────────────────────────
            //
            // Reached only when the placement authored no `weap` at all. Note this is the oracle's
            // `else`, so a `Fixed`/`Unarmed` answer above must NOT fall through — which is why the
            // switch sets `EquippedWeaponId` and the guard below tests it rather than re-testing the
            // mode.
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
                            // AS3 `f` — the row is this unit's SECONDARY weapon (mercgr for merc/encl,
                            // robomlau/robogas for ranger, …). The oracle's roll steps over it
                            // (`Unit.as:1459`) and the family constructor grants it by name instead, so it
                            // must not be offered here. See WeaponRowParser for the full account.
                            candidates[i].isFixedWeapon));
                    }

                    // `getXmlWeapon(param1:int)` truncates the caller's `locDifLevel` (a Number) — the
                    // parameter is typed int at the declaration, so AS3 does the same.
                    int difficulty = (int)LocationDifficulty;

                    int index = UnitWeaponSelector.SelectIndex(
                        _weaponOptions,
                        difficulty,
                        // AS3's `isrnd` is `Math.random() < p` — Flash's *unseeded* global, not the room's
                        // seeded spawn stream. UnityEngine.Random is the port's equivalent global, and using
                        // it here deliberately leaves the seeded room stream untouched: a spawn that consumed
                        // a number from it would desynchronise every later roll in the room, which is the
                        // same reason AlicornController does not roll `osob` at spawn.
                        roll01: () => UnityEngine.Random.value,
                        // `Weapon.create` returns null for an id that is not in the weapon table, and the
                        // oracle then CONTINUES to the next candidate rather than giving up (Unit.as:1470).
                        isAvailable: id => TryGetWeaponDefinition(id, out _));

                    if (index != UnitWeaponSelector.NoWeapon)
                    {
                        EquippedWeaponId = _weaponOptions[index].WeaponId;
                        TryGetWeaponDefinition(EquippedWeaponId, out rolled);
                    }
                }
            }

            // AS3 Unit.as:332 — `weaponKrep` defaults to 1; see WeaponKrep's remarks for how the port
            // recovers it.
            WeaponKrep = definition != null && definition.isStable;

            AttackerType = UnitWeaponSelector.AttackerType(
                rolled != null ? (int)rolled.weaponType : 0,
                WeaponKrep,
                rolled != null);
        }

        /// <summary>
        /// Build the mount for the rolled weapon, once both the definition (<c>Stats</c>) and the weapon
        /// services are present. Both guards are load-bearing, exactly as they are for the alicorn:
        /// <c>RoomUnitSpawner</c> hands the services over <i>before</i> <c>Initialize</c> and
        /// <c>Faction</c> is derived from <c>Stats</c>, so equipping on the early hook alone would build a
        /// mount that does not know whose side it is on.
        /// </summary>
        private void EquipRolledWeapon()
        {
            if (Stats == null || !HasWeaponServices) return;

            if (!string.IsNullOrEmpty(EquippedWeaponId))
            {
                EquipWeapon(EquippedWeaponId);
            }

            EquipFamilySecondaries();

            // The magazine rule in `UnitRaider`'s constructor (`:217-219`) runs after the weapon roll and
            // reads the weapon's `holder`, so the brain can only finish its tuning once the mount exists.
            ShooterBrain?.OnWeaponEquipped();
        }

        /// <summary>
        /// Build the family's secondary weapon(s) — the <c>f='1'</c> rows the roll deliberately steps
        /// over (<see cref="UnitWeaponSelector.SelectIndex"/>), which each subclass grants by name.
        /// </summary>
        /// <remarks>
        /// <para><b>The ammo gate is the oracle's, and it is not cosmetic.</b>
        /// <c>UnitMerc.as:30</c> and <c>UnitEncl.as:29</c> wrap the whole creation in
        /// <c>if(grenader &gt; 0)</c>, so <c>merc1</c>, <c>merc2</c> and <c>merc4</c> — which author no
        /// <c>grenader</c> — carry <b>no</b> <c>mercgr</c> in the original game, even though their rows
        /// list the candidate. Equipping it unconditionally would hand three of the five griffins a
        /// 50-damage rocket the oracle never gives them.</para>
        ///
        /// <para><b>The ranger's two are unconditional.</b> <c>UnitRanger.as:28-29</c> has no gate at
        /// all: <c>robomlau</c> when it has a target, <c>robogas</c> when it does not.</para>
        ///
        /// <para><b>Idempotent by construction.</b> <c>EquipWeapon</c> is per-id, and the list is
        /// rebuilt from scratch on every call — <c>ApplyPlacement</c> destroys the mounts and re-enters
        /// this path, so appending without clearing would grow the list on every spawn.</para>
        /// </remarks>
        private void EquipFamilySecondaries()
        {
            _secondaryWeaponIds.Clear();

            UnitDefinition def = Definition;
            if (def == null) return;

            string familyId = !string.IsNullOrEmpty(def.parentId) ? def.parentId : def.id;

            switch (familyId)
            {
                case "merc":
                case "encl":
                    if (def.grenadeCount <= 0) return;

                    _secondaryWeaponIds.Add("mercgr");
                    SecondaryPeriodMinTicks = 50;
                    SecondaryPeriodMaxTicks = 200;
                    SecondaryRearmMinTicks = 50;
                    SecondaryRearmMaxTicks = 200;
                    SecondaryFiresOnlyWithTarget = true;
                    break;

                case "ranger":
                    _secondaryWeaponIds.Add("robomlau");
                    _secondaryWeaponIds.Add("robogas");
                    SecondaryPeriodMinTicks = 50;
                    SecondaryPeriodMaxTicks = 170;
                    SecondaryRearmMinTicks = 50;
                    SecondaryRearmMaxTicks = 140;
                    SecondaryFiresOnlyWithTarget = false;
                    break;

                default:
                    return;
            }

            for (int i = 0; i < _secondaryWeaponIds.Count; i++)
            {
                EquipWeapon(_secondaryWeaponIds[i]);
            }
        }

        /// <summary>
        /// Re-roll the weapon now that the placement is known, when the placement's <c>weap</c>
        /// attribute decides the outcome.
        ///
        /// <para><b>Why an override at all.</b> AS3 reads <c>param3.@weap</c> inside the unit's
        /// constructor, so the placement is known before any weapon is chosen
        /// (<c>UnitRaider.as:162-181</c>). The port's spawner calls <c>Initialize</c> — which rolls — at
        /// <c>RoomUnitSpawner.cs:1149</c> and <c>ApplyPlacement</c> at <c>:1233</c>, so the first roll
        /// cannot see the attribute. Re-rolling here is the same fix shape as
        /// <c>UnitNPCController.SyncAnimatorFlight</c>: the fact arrives late, so the dependent state is
        /// recomputed at the arrival seam rather than polled every frame.</para>
        ///
        /// <para><b>Only when the placement actually decides.</b> A unit whose placement authors no
        /// <c>weap</c> keeps the weapon its first roll produced — re-rolling would consume a second
        /// <c>Math.random()</c> for no reason and change the weapon of every un-authored raider in the
        /// game. A unit whose placement says <c>weap=""</c> or names a weapon is re-rolled, and the first
        /// weapon's mount is destroyed so it is not also held.</para>
        /// </summary>
        public override void ApplyPlacement(UnitInstance placement)
        {
            base.ApplyPlacement(placement);

            // The base returns early on a null placement, so it never dereferences one — but this
            // override reads `placement.attributes` below and would. A bare spawn (a test, a
            // `loc.createUnit` with no node) is exactly that case.
            if (placement == null) return;

            // The same family gate as `RollWeapon`, and it has to be here too: a necros placement
            // authoring `weap` must not trigger a re-roll, because AS3's necros never reads it.
            if (AiFamily != UnitAiFamily.Raider) return;

            // `UnitRaider.as:206-209` — `if(param3 && param3.@nodrop.length()) isDropArm = false;`.
            // Presence, not value: `nodrop="0"` disables the drop just as `nodrop="1"` does.
            _placementNoDrop = MapObjectDataUtility.HasAttribute(placement.attributes, NoDropAttributeName);

            if (!_weaponRolled) return;

            PlacementWeaponMode mode = PlacementWeaponMode;
            if (mode == PlacementWeaponMode.Roll)
            {
                // No re-roll, but the hero tier is only known now (base.ApplyPlacement read it), so the
                // weapon upgrade still has to be evaluated here.
                ApplyHeroWeaponVariant();
                return;
            }

            // The definition the first roll used is gone by now; re-read it from the same source
            // `Initialize` was given. `Stats` is not it — this needs the unit row's <w> candidates,
            // which `RollWeapon` only consults on the `Roll` arm, so a null here costs nothing.
            ClearWeaponMounts();
            RollWeapon(Definition);
            EquipRolledWeapon();

            // After the re-roll, because a `weap`-fixed weapon is the one that ends up held.
            ApplyHeroWeaponVariant();
        }

        /// <summary>AS3's placement attribute — <c>UnitRaider.as:206</c>.</summary>
        public const string NoDropAttributeName = "nodrop";

        /// <summary>
        /// AS3 <c>UnitRaider.setHero</c>'s weapon half (<c>UnitRaider.as:362-372</c>) — a hero raider's
        /// weapon can be its "II" variant, and that variant is what the corpse drops.
        ///
        /// <para><b>Why it runs from <c>ApplyPlacement</c> and not from the constructor.</b> The oracle
        /// calls <c>setHero</c> from <c>Location.createUnit</c> <i>after</i> the unit is built
        /// (<c>Location.as:1204</c>), so the weapon already exists and the placement's <c>hero</c>
        /// attribute is already known. In the port the tier arrives on the placement, which
        /// <c>RoomUnitSpawner</c> hands over in <c>ApplyPlacement</c> — after <c>Initialize</c> rolled.
        /// Running here is the same order by a different route.</para>
        ///
        /// <para><b>Two gates, and the second one is not obvious.</b></para>
        /// <list type="number">
        ///   <item><description><c>hero == 1</c> — tiers 2, 3 and 4 are <c>Unit.setHero</c>'s
        ///     armoured/damage tiers (<c>Unit.as:1651-1665</c>) and never touch the
        ///     weapon.</description></item>
        ///   <item><description><b><c>mHero</c></b> — <c>Unit.setHero</c> opens with
        ///     <c>if(!this.mHero) return;</c> (<c>Unit.as:1628</c>), and <c>mHero</c> is set only by the
        ///     unit <b>row</b>'s <c>&lt;param hero='…'&gt;</c> (<c>Unit.as:1371-1375</c>). Note the value
        ///     is a hero <i>name</i> — <c>hero='zebra'</c> — not a tier; the numeric tier is
        ///     <c>hero</c>, which <c>setHero</c> assigns from its argument.</description></item>
        /// </list>
        ///
        /// <para><b>Why the gate here is "is this a raider family" and not "is this row an mHero row".</b>
        /// The row flag is not imported — <c>UnitDefinition.heroType</c> is declared
        /// (<c>UnitDefinition.cs:482</c>) and written by nothing, so the port cannot ask. The
        /// substitution is <b>measured, not assumed</b>: only <b>6 of the 38</b> raider-family rows are
        /// <c>mHero</c> rows (<c>raider</c>, <c>slaver</c>, <c>zebra</c>, <c>merc</c>, <c>ranger</c>,
        /// <c>encl</c> — the six base rows), but <b>every one of the 41 shipped hero placements</b> uses a
        /// base row, and the 6 that do not (<c>necros</c> ×2, <c>ebloat</c> ×1, <c>ant</c> ×3) are not
        /// raider-family, so this override never sees them. A mod that placed <c>hero=</c> on a numbered
        /// raider row (<c>raider5</c>) would diverge; importing <c>heroType</c> is the fix when someone
        /// needs it.</para>
        ///
        /// <para><b>The draw is <c>UnityEngine.Random</c>, deliberately.</b> AS3's <c>isrnd</c> is
        /// Flash's unseeded global, and the weapon roll itself uses the same source for the same reason
        /// (see <c>RollWeapon</c>'s note): consuming the seeded room stream here would desynchronise
        /// every later roll in the room.</para>
        /// </summary>
        private void ApplyHeroWeaponVariant()
        {
            if (_heroVariantApplied) return;
            _heroVariantApplied = true;

            if (HeroTier != EnemyWeaponDropRules.HeroTierThatUpgradesTheWeapon) return;
            if (string.IsNullOrEmpty(EquippedWeaponId)) return;

            if (!TryGetWeaponDefinition(EquippedWeaponId, out WeaponDefinition weapon) || weapon == null)
            {
                return;
            }

            if (!EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(
                    weapon.uniq, UnityEngine.Random.value))
            {
                return;
            }

            HeldWeaponVariant = EnemyWeaponDropRules.HeroVariant;
        }

        /// <summary>
        /// AS3 <c>UnitRaider.dropLoot</c>'s second half (<c>UnitRaider.as:507-523</c>) — hide the held
        /// gun's sprite, then (if <c>isDropArm</c>) drop it as a loot item.
        ///
        /// <para><b>Order and guards are the oracle's.</b> <c>super.dropLoot()</c> has already run — the
        /// base class calls this hook after its table/essence/gem rolls — and the visual is hidden
        /// <b>whether or not</b> the weapon is dropped, because the oracle's hide sits outside the
        /// <c>if(isDropArm)</c> block. Only a unit with no weapon at all skips both
        /// (<c>if(currentWeapon)</c>).</para>
        ///
        /// <para><b><c>if(currentWeapon)</c> is "did it roll a weapon", not "is there a mount".</b> The
        /// gate is <see cref="EquippedWeaponId"/> — the id the roll accepted — and the mount is consulted
        /// only to hide the sprite, through a null-conditional call. Gating the drop on the mount would
        /// lose the gun for every unit whose weapon services arrived late or whose definition failed to
        /// resolve, and those are exactly the cases where a silent loss is hardest to see.</para>
        ///
        /// <para><b><c>kol = 0</c> is not a typo.</b> That is the oracle's fifth argument
        /// (<c>:521</c>) and it is what makes the dropped gun 5–20 % condition rather than pristine —
        /// see <see cref="EnemyWeaponDropRules.DroppedWeaponKol"/>. <c>imp</c> is left at the roller's
        /// default because AS3's <c>LootGen.lootId</c> takes no such argument: the drop is an ordinary
        /// item, not one of the container-child rolls that <c>RollPlacedItems</c> marks important.</para>
        ///
        /// <para><b>Two other halves of the same oracle method are NOT ported, and are named here so
        /// they cannot be mistaken for "done".</b> <c>UnitRaider.dropLoot</c> continues past the drop
        /// with:</para>
        /// <list type="number">
        ///   <item><description><c>if(attackerType == 3)</c> — a <b>thrower fires three more
        ///     <c>currentWeapon.attack()</c> shots</b> at random offsets around its own corpse
        ///     (<c>:526-533</c>). A dying grenadier lobs a last handful; the port's corpses are inert.
        ///     <c>attackerType</c> 3 is a weapon whose <c>tip</c> is 4, so the units this affects are
        ///     measured: <c>raider9</c> (both candidates), <c>raider8</c> (one of three) and
        ///     <c>merc1</c>–<c>merc5</c> (one each).</description></item>
        ///   <item><description><c>if(dropWeapon)</c> — a second, invisible weapon built from the unit
        ///     row's <c>&lt;un drop='…'&gt;</c> attribute (<c>:210-215</c>, <c>:349</c>) and fired
        ///     <b>once</b> at the corpse (<c>:534-538</c>). <b>Exactly one unit authors it:
        ///     <c>zebra2</c></b> — "Зебра-диверсант" — with <c>drop='bomb'</c>, i.e. the saboteur is
        ///     supposed to set off a bomb when it dies. Blocked on data, not on code:
        ///     <c>UnitExtrasParser</c> reads <c>&lt;un&gt;</c> for <c>walker</c>, <c>sniper</c>,
        ///     <c>stay</c> and <c>glow</c> and not for <c>drop</c>, so the id never reaches a
        ///     <see cref="PFE.Data.Definitions.UnitDefinition"/> and the unit assets would need a
        ///     re-import to carry it.</description></item>
        /// </list>
        /// </summary>
        protected override void DropWieldedWeapon(LootContext ctx, float centreY)
        {
            // `if(currentWeapon)` — a unit that rolled nothing (or authored `weap=""`) has no gun to
            // hide and nothing to drop.
            if (string.IsNullOrEmpty(EquippedWeaponId)) return;

            WeaponMount?.SetHeldVisualVisible(false);        // `currentWeapon.vis.visible = false`

            if (!DropsWieldedWeapon) return;                 // `if(isDropArm)`

            LootRoller.RollPlacedItem(
                EnemyWeaponDropRules.DropId(EquippedWeaponId, HeldWeaponVariant),
                ctx,
                kol: EnemyWeaponDropRules.DroppedWeaponKol,
                nx: transform.position.x,
                ny: centreY);
        }
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class RaiderController : ArmedShooterController
    {
        public new const string ControllerId = "UnitRaider";
        public new const string ControllerAlias = "raider";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class SlaverController : ArmedShooterController
    {
        public new const string ControllerId = "UnitSlaver";
        public new const string ControllerAlias = "slaver";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class ZebraController : ArmedShooterController
    {
        public new const string ControllerId = "UnitZebra";
        public new const string ControllerAlias = "zebra";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class RangerController : ArmedShooterController
    {
        public new const string ControllerId = "UnitRanger";
        public new const string ControllerAlias = "ranger";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class EnclController : ArmedShooterController
    {
        public new const string ControllerId = "UnitEncl";
        public new const string ControllerAlias = "encl";
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class MercController : ArmedShooterController
    {
        public new const string ControllerId = "UnitMerc";
        public new const string ControllerAlias = "merc";

        /// <summary>
        /// AS3 <c>UnitMerc.arm</c> — the griffin's separate aiming limb, or null when the arm art has
        /// not been imported (<c>UnitDefinition.armSprite</c> is written by
        /// <c>UnitSpriteImporter</c>'s griffin-arm pass).
        ///
        /// <para>It is built here rather than inside the weapon mount because the limb belongs to the
        /// <i>body</i>: AS3 adds it to the same layer as <c>vis</c> and mirrors it with the unit's own
        /// <c>storona</c>, not with the weapon's aim. The mount then asks it where the weapon goes
        /// through <c>IWeaponHoldPointSource</c>.</para>
        /// </summary>
        private GriffinArmRig _arm;

        /// <summary>
        /// Build the arm <b>after</b> <c>base.Initialize</c>: the rig reads
        /// <see cref="PFE.Entities.Units.UnitController.Stats"/> for the sheet's cell size and
        /// registration point, and those only exist once the base has assigned the definition. The
        /// weapon mount resolves its hold-point source lazily, so building the limb a step after the
        /// weapon is built costs nothing and needs no ordering contract between the two.
        /// </summary>
        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);

            if (definition == null || definition.armSprite == null)
            {
                return;
            }

            int variant = GriffinArmMath.VariantIndexForSpriteSheet(definition.spriteSheetId);
            _arm = GriffinArmRig.Attach(this, definition.armSprite, variant);
        }

        /// <summary>
        /// AS3 <c>UnitMerc.as:164-166</c> — <c>if(sost == 2 || sost == 3) arm.visible = false</c>. The
        /// death animation is drawn without the limb, so leaving the arm on leaves a gun-holding hand
        /// floating over a corpse.
        /// </summary>
        protected override void OnDeath()
        {
            if (_arm != null)
            {
                _arm.SetVisible(false);
            }

            base.OnDeath();
        }
    }

    [RequireComponent(typeof(ArmedShooterBrain))]
    public class NecrosController : ArmedShooterController
    {
        public new const string ControllerId = "UnitNecros";
        public new const string ControllerAlias = "necros";

        /// <summary>
        /// <c>UnitNecros</c> reads <c>stay</c> and nothing else — it extends <c>Unit</c>, <b>not</b>
        /// <c>UnitRaider</c> (<c>UnitNecros.as:102</c>: <c>if(aiTip != "stay" &amp;&amp; isrnd())</c>), and
        /// has no <c>stroll</c> or <c>quiet</c> field behind it.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the override is mandatory rather than cosmetic.</b> This controller derives from
        /// <see cref="ArmedShooterController"/> because it shares the weapon-rolling controller — a port
        /// convenience, not an oracle fact. Without this override it would inherit
        /// <see cref="UnitAiFamily.Raider"/> and answer <c>quiet</c>/<c>sniper</c> as well, so a
        /// hypothetical <c>ai="quiet"</c> necros would stand still where the oracle lets it wander. The
        /// shipped data authors <c>stay</c> on seven necros placements and <c>quiet</c> on none, so the
        /// divergence is latent rather than visible — which is exactly the kind that survives a
        /// play-test.</para>
        /// </remarks>
        protected override UnitAiFamily AiFamily => UnitAiFamily.Necros;

        /// <summary>
        /// <c>UnitNecros.as:29</c> — <c>collisionTip = 0</c>. A necrosprite's contact hit damages
        /// without exchanging momentum, so it neither shoves its target nor recoils off it. See
        /// <see cref="PFE.Entities.Units.UnitController.CollisionTip"/>.
        /// </summary>
        public override bool CollisionTip => false;
    }
}
