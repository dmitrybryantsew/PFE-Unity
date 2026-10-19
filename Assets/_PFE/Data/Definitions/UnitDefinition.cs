using UnityEngine;
using PFE.ModAPI;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

// The enums and structs are in the same namespace
// Just need to ensure the file is included in the build

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Complete ScriptableObject definition for unit data.
    /// Replaces XML-based unit definitions from AllData.as in ActionScript.
    ///
    /// XML Structure Reference:
    /// <unit id='raider1' fraction='2' cat='3' parent='raider' xp='100'>
    ///     <phis sX='55' sY='70' massa='55'/>
    ///     <move speed='4' jump='18' accel='3'/>
    ///     <comb hp='50' damage='11' krep='1'/>
    ///     <vis blit='sprRaider1' sprX='120' sex='w'/>
    ///     <n>Display Name</n>
    ///     <w id='cknife' ch='0.2' dif='6'/>
    /// </unit>
    ///
    /// Create instances via Assets > Create > PFE > Unit Definition
    /// </summary>
    [CreateAssetMenu(fileName = "NewUnit", menuName = "PFE Data/Units/Unit Definition")]
    public class UnitDefinition : VersionedData, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Unique ID for this unit (e.g., 'raider1', 'littlepip')")]
        public string id;

        public override string DataId => id;

        // Legacy property for compatibility
        public string ID => id;

        // IGameContent
        string IGameContent.ContentId => id;
        ContentType IGameContent.ContentType => ContentType.Unit;

        [Tooltip("Parent template ID (e.g., 'raider' for 'raider1')")]
        public string parentId;

        [Tooltip("AI controller ID — AS3 `cl` on the <obj> row, NOT the `cont` attribute. See the " +
                 "lookalike warning on `lootTableKey`.")]
        public string controllerId;

        /// <summary>
        /// AS3 <c>inter.cont</c> — the <b>loot table</b> this unit drops from when it dies, from the
        /// <c>cont=</c> attribute of its own <c>&lt;unit&gt;</c> row. Empty means "not authored".
        ///
        /// <para><b>Resolve it, never read it raw.</b> AS3 assigns <c>inter.cont = mid</c> — the unit's
        /// <i>id</i> — and only then lets <c>@cont</c> override it (<c>Unit.as:1005-1009</c>). So a
        /// <c>raider5</c> (which authors <c>cont='raider'</c>) drops from the <c>raider</c> table while a
        /// <c>rat</c> (which authors nothing) drops from the table keyed <c>"rat"</c>. 40 of the 564 unit
        /// rows author <c>cont</c>; the other 524 fall back to their own id, which is why
        /// <c>LootGen.lootDrop</c> has branches keyed on plain ids (<c>rat</c>, <c>slime</c>,
        /// <c>ant1</c>, <c>bloodwing</c>, <c>necros</c>, <c>ebloat</c>, …).
        /// <see cref="PFE.Systems.Loot.UnitDeathDrop.ResolveLootTableKey"/> owns that rule.</para>
        ///
        /// <para><b>The lookalike this field exists to undo.</b> <c>UnitDataImporter</c> used to write
        /// <c>cont</c> into <see cref="controllerId"/> — and still does, deliberately, because removing
        /// that write changes which controller a unit spawns with (see the importer). <c>cont</c> is a
        /// loot-table key; the controller selector is <c>cl=</c> on <c>&lt;obj&gt;</c> rows. They are
        /// different attributes on different elements and are not interchangeable — the same warning
        /// <c>RoomInstance.controllerId</c> already carries.</para>
        /// </summary>
        [Tooltip("AS3 `cont` — the loot table this unit drops from on death. Empty = use the unit's own " +
                 "id (Unit.as:1005-1009). Not the AI controller id.")]
        public string lootTableKey;

        [Tooltip("Faction (AS3 `fraction`). AS3 values: 0=Neutral (the default for a unit with no " +
                 "attribute), 1=Monster, 2=Raider, 3=Zombie, 4=Robot, 100=Player. The player is not " +
                 "in the data — PlayerController overrides this to Player at runtime.")]
        public FactionType fraction = FactionType.Neutral;

        [Tooltip("Category: 1=Template, 2=Faction, 3=Spawnable")]
        public UnitCategory category = UnitCategory.Template;

        [Tooltip("XP reward for killing")]
        public int xpReward;

        [Header("Display")]
        [Tooltip("Display name (from <n> tag in XML)")]
        public string displayName;

        public override string DisplayName => displayName ?? id;

        #region Physics (phis tag)

        [Header("Physics")]
        [Tooltip("Width from 'sX' in AS3 (55px = 0.55 units)")]
        [Range(0.1f, 2f)]
        public float width = 0.55f;

        [Tooltip("Height from 'sY' in AS3 (70px = 0.70 units)")]
        [Range(0.1f, 3f)]
        public float height = 0.70f;

        [Tooltip("Sitting height")]
        [Range(0f, 2f)]
        public float sitHeight = 0.5f;

        [Tooltip("Mass from 'massa' in AS3 — the RAW attribute, used only when 'massafix' is absent. " +
                 "See Massa for the value AS3 uses.")]
        [Range(1f, 500f)]
        public float mass = 50f;

        /// <summary>
        /// Mass from <c>massafix</c> in AS3 — the RAW attribute, and the one AS3 actually uses when a
        /// unit authors it. <c>0</c> means "this unit does not author <c>massafix</c>".
        ///
        /// <para><b>Zero is the safe sentinel, deliberately.</b> Every <c>.asset</c> written before this
        /// field existed deserialises it as <c>0</c>, which reads as "absent" and falls back to
        /// <see cref="mass"/> — i.e. exactly the behaviour those assets had. The alternative sentinel
        /// (<c>-1</c> for "absent") would have made every un-reimported asset look like it authored a
        /// negative mass. Only 6 units in the whole oracle author <c>massafix</c> at all.</para>
        /// </summary>
        [Tooltip("Mass from 'massafix' in AS3 — the RAW attribute, and the one AS3 actually uses. " +
                 "0 means the unit does not author it, so 'mass' applies. See Massa.")]
        [Range(0f, 10000f)]
        public float massafix = 0f;

        /// <summary>
        /// AS3's <c>Unit.massa</c> — the weight divisor in the knockback formula.
        ///
        /// <para><b>AS3 divides the attribute by 50 and the port does not, so these are not the same
        /// number.</b> <c>Unit.as:1047</c> reads <c>massaMove = node.@massa / 50</c> and <c>:1051</c>
        /// reads <c>massaFix = node.@massafix / 50</c>; the port's importer stores the attribute
        /// verbatim. AS3's own field default is <c>1</c> (<c>Obj.as:28</c> — <c>massa</c> is declared on
        /// <c>Obj</c>, not <c>Unit</c>), which is why <see cref="mass"/> defaults to 50 — the same
        /// quantity, unconverted. Feeding the raw value straight into the knockback divisor would make
        /// every unit 50x harder to throw, which is exactly the silent scale error this property exists
        /// to prevent.</para>
        ///
        /// <para><b><c>massafix</c> wins over <c>massa</c>, and the port used to ignore it.</b>
        /// <c>Unit.as:1049-1056</c> reads both off the same <c>&lt;phis&gt;</c> node
        /// (<c>node = node0.phis[0]</c>, <c>:1018</c>) and sets <c>massaFix</c> from <c>@massafix</c> when
        /// present, from <c>@massa</c> otherwise; <c>:1058</c> then does <c>massa = this.massaFix</c>.
        /// Five units author both and would have been thrown 4.5x-12.5x too far — <c>turret0</c>
        /// <c>massa='40' massafix='250'</c> is 5.0 against the port's 0.8, <c>turret2</c>'s
        /// <c>40/500</c> is 12.5x. All six are turrets, i.e. exactly what the player shoots at.</para>
        ///
        /// <para>AS3 keeps two more: <c>massaFix</c> at rest and <c>massaMove</c> while moving
        /// (<c>:1058</c> / <c>:3137</c>, the levitation branch). Only the resting one is modelled here;
        /// the moving variant is not imported, so a levitating unit keeps its resting weight.</para>
        /// </summary>
        public float Massa => (massafix > 0f ? massafix : mass) / 50f;

        // Legacy properties for compatibility
        public float Width => width;
        public float Height => height;

        #endregion

        #region Movement (move tag)

        [Header("Movement")]
        [Tooltip("Walking speed")]
        [Range(0f, 20f)]
        public float moveSpeed = 4f;

        [Tooltip("Running speed multiplier")]
        [Range(1f, 5f)]
        public float runMultiplier = 2f;

        /// <summary>
        /// AS3's <c>run</c> — the unit's <b>absolute</b> maximum speed while running, in pixels per
        /// 30 Hz frame (<c>Unit.as:1072</c> <c>this.runSpeed = node.@run</c>). <c>0</c> = not authored.
        ///
        /// <para><b>Why this is not <see cref="runMultiplier"/>.</b> They are different quantities and
        /// the data proves it: <c>zombie0</c> is <c>&lt;move speed='1.5' run='10' …/&gt;</c>, so its run
        /// speed is <b>10 px/frame</b> — 6.7× its walk speed, which the <c>[Range(1f, 5f)]</c> multiplier
        /// cannot express. The multiplier is the <i>player's</i> sprint knob: AS3 gives
        /// <c>littlepip</c> no <c>run</c> attribute at all, so the port invented a multiplier for the
        /// hold-to-sprint input and it is the right model there. An enemy brain reading
        /// <c>RunSpeed</c> wants the oracle's absolute number.</para>
        ///
        /// <para><b>Ordering matters: absolute wins.</b> A row that authors <c>run</c> means exactly that
        /// speed, so <see cref="RunSpeed"/> prefers this field whenever it is positive and only falls
        /// back to the multiplier when it is not.</para>
        /// </summary>
        [Tooltip("AS3 'run' — absolute run speed in px/frame. 0 = not authored; RunSpeed falls back " +
                 "to moveSpeed * runMultiplier.")]
        [Range(0f, 30f)]
        public float runSpeed = 0f;

        [Tooltip("Acceleration rate")]
        [Range(0.1f, 10f)]
        public float acceleration = 2f;

        /// <summary>
        /// AS3 <c>brake</c> (<c>Unit.as:230</c>, default <c>1</c>), read from <c>&lt;move brake='…'&gt;</c>
        /// at <c>:1091-1094</c>.
        ///
        /// <para><b>Its only consumer is the flying deadzone.</b> <c>UnitController.ApplyFlightBrake</c>
        /// and <c>TilePhysicsController</c>'s flyer branch are the two readers, and both run only while
        /// a unit is airborne under <c>isFly</c>. Ground friction does not read this field — see
        /// <c>UnitFallPhysics</c>, which carries the AS3 brake constant itself. So a value here changes
        /// how a flyer settles and nothing about how a walker walks.</para>
        ///
        /// <para>The <c>0.5</c> initialiser is a prefab-only fallback: <c>UnitDataImporter</c> writes
        /// the field for every imported unit (AS3's <c>1</c> when the row is silent, the authored value
        /// otherwise), so it does not survive an import. Left as-is rather than changed to <c>1</c>,
        /// because a hand-made asset that never goes through the importer is the only thing it governs
        /// and silently moving it would be a change with no oracle citation.</para>
        /// </summary>
        [Tooltip("AS3 'brake' — the flying deadzone, in px/frame. AS3's default is 1.")]
        [Range(0.1f, 2f)]
        public float braking = 0.5f;

        [Tooltip("Jump force")]
        [Range(0f, 30f)]
        public float jumpForce = 15f;

        [Tooltip("Knockback susceptibility (AS3 'knocked') — 0 = immovable, 1 = ordinary, >1 = light")]
        [Range(0f, 3f)]
        public float knocked = 1f;

        // Legacy properties for compatibility
        public float WalkSpeed => moveSpeed;
        public float RunSpeed => runSpeed > 0f ? runSpeed : moveSpeed * runMultiplier;
        public float Acceleration => acceleration;
        public float JumpForce => jumpForce;

        [Tooltip("Max levitation height")]
        [Range(0f, 200f)]
        public float levitationMaxHeight = 60f;

        [Tooltip("Levitation acceleration")]
        [Range(0f, 10f)]
        public float levitationAcceleration = 1.6f;

        [Tooltip("Can swim")]
        public bool canSwim = true;

        [Tooltip("Can levitate")]
        public bool canLevitate = false;

        [Tooltip("Can be knocked down")]
        public bool canBeKnockedDown = true;

        /// <summary>
        /// AS3's <c>Unit.fixed</c>, authored as <c>fixed='1'</c> on the <c>&lt;move&gt;</c> node
        /// (<c>Unit.as:1114-1116</c>, <c>this.fixed = node.@fixed &gt; 0</c>).
        ///
        /// <para><b>It gates the entire position integration, not just the walk input.</b>
        /// <c>Unit.as:1809</c> wraps the call to <c>run()</c> — the function that does
        /// <c>X += dx</c> — in <c>if(!this.fixed)</c>, while <c>forces()</c> and <c>control()</c> run
        /// unconditionally above it. So a fixed unit still accumulates <c>dx</c>/<c>dy</c> and still
        /// turns to face, but never moves: it is immune to knockback <i>displacement</i> (a bullet
        /// still adds to <c>dx</c>, the value just never lands) and takes no collision response,
        /// because <c>run()</c> also owns wall resolution. A second gate at <c>:4223</c> makes it an
        /// immovable wall to physics boxes instead — the box rebounds at half speed.</para>
        ///
        /// <para><b>Also runtime-mutable, and this field only carries the authored half.</b> AS3
        /// flips <c>fixed</c> from behaviour code in eleven unit subclasses — <c>UnitTurret.as:462</c>
        /// and <c>UnitZombie.as:455</c> clear it, <c>UnitTransmitter</c>/<c>UnitTrain</c>/
        /// <c>UnitTrigger</c>/<c>UnitThunderTurret</c>/<c>UnitMsp</c>/<c>UnitSlime</c> and others set
        /// it, and <c>Unit.as:3130</c> clears it on a fixed unit that has been levitating for more
        /// than 75 ticks (<c>otryv()</c>, <c>:2830</c>). None of those subclasses exist in the port
        /// yet, so today the flag is whatever the data says. <see cref="PFE.Entities.Units.UnitController.IsFixed"/>
        /// is <c>virtual</c> so that a ported subclass can override it rather than needing this field
        /// to become mutable.</para>
        ///
        /// <para><b>16 units author it</b> — <c>captive</c>, <c>ponpon</c>, <c>ebloat</c>, <c>eant</c>,
        /// <c>turret0</c>, <c>turret2</c>, <c>turret4</c>, <c>turret5</c>, <c>trigcans</c>,
        /// <c>trigridge</c>, <c>trigplate</c>, <c>triglaser</c>, <c>damshot</c>, <c>damgren</c>,
        /// <c>damexpl1</c>, <c>mwall</c> — and the oracle only ever writes <c>fixed='1'</c>, so this is
        /// also the count of <c>&lt;move&gt;</c> nodes that carry it. <c>turret</c> is <b>not</b> one of
        /// them, though it looks like it should be: <c>AllData.as:1758</c> is
        /// <c>&lt;unit id='turret' cat='2'/&gt;</c>, a self-closing base with no children, so it authors
        /// no <c>&lt;move&gt;</c> at all. The concrete turrets inherit from it via <c>cont='turret'</c>
        /// and each declares its own <c>&lt;move fixed='1'/&gt;</c>.</para>
        /// </summary>
        [Tooltip("AS3's Unit.fixed — pinned in place. Gates the whole position integration (Unit.as:1809).")]
        public bool isFixed = false;

        [Tooltip("Wall damage per frame")]
        [Range(0, 200)]
        public int wallDamage = 0;

        [Tooltip("Step threshold (for detecting drops)")]
        [Range(0, 50)]
        public int stepThreshold = 0;

        #endregion

        #region Combat (comb tag)

        [Header("Combat")]
        [Tooltip("Maximum health points")]
        [Range(1, 5000)]
        public int health = DefaultHealth;

        /// <summary>
        /// The health a unit carries when no definition supplies one.
        ///
        /// <para>Named rather than written as a literal in two places, because it now has a second
        /// reader: <c>RoomPopulator</c> falls back to it when a unit id has no
        /// <c>UnitDefinition</c>, and it must stay equal to the field initializer above or the
        /// fallback silently disagrees with the data model.</para>
        /// </summary>
        public const int DefaultHealth = 50;

        [Tooltip("Armor (physical damage reduction)")]
        [Range(0, 100)]
        public int armor = 0;

        [Tooltip("Magic armor (energy damage reduction)")]
        [Range(0, 100)]
        public int magicArmor = 0;

        [Tooltip("Armor health (armor durability)")]
        [Range(0, 1000)]
        public int armorHealth = 0;

        [Tooltip("Base damage")]
        [Range(0, 200)]
        public int damage = 10;

        /// <summary>
        /// AS3 <c>Unit.observ</c> — the enemy's <b>observation power</b>, authored on the
        /// <c>&lt;comb&gt;</c> element as <c>observ='…'</c>.
        ///
        /// <para><b>This is not a detection range.</b> The name and the old tooltip both suggested one.
        /// In the oracle it is the second argument to the player's suspicion meter —
        /// <c>observation(intensity, this.observ)</c> (<c>actionscript_project_context.txt:126474</c>) —
        /// where it <i>multiplies</i> the intensity by up to <c>1 + observ * 0.2</c> (see
        /// <see cref="PFE.Entities.Units.NoiseMath"/>). A high <c>observ</c> makes a unit notice the
        /// player faster from any given glimpse; it does not change how far it can see. It does
        /// <b>not</b> scale the hearing path. <c>zombie9</c> authors <c>observ='6'</c>; 37 <c>&lt;comb&gt;</c>
        /// rows carry one at all.</para>
        ///
        /// <para><b>This field spent its whole life at 0, because the importer's pattern was wrong.</b>
        /// It read <c>obs='(\d+)'</c>, and <c>obs='</c> appears <b>zero</b> times in the 8 MB oracle — it is
        /// not a substring of <c>observ='5'</c>. So all 37 authored values were dropped and every unit kept
        /// the default, which is a <i>legal</i> value, so nothing ever looked broken: the symptom was only
        /// "enemies notice a little slower than the data says". The pattern is now <c>\bobserv='(\d+)'</c>.
        /// AS3's own default is <c>0</c> (<c>:122282</c>), which is why the field default matches.</para>
        /// </summary>
        [Tooltip("AS3 'observ' — observation power, not a range. Scales how fast this unit builds " +
                 "suspicion of the player.")]
        [Range(0, 200)]
        public int observationRange = 0;

        [Tooltip("Radiation damage on hit")]
        [Range(0, 50)]
        public int radiationDamage = 0;

        [Tooltip("Skin thickness (damage reduction)")]
        [Range(0f, 50f)]
        public float skinThickness = 0f;

        [Tooltip("Dexterity")]
        [Range(0.1f, 5f)]
        public float dexterity = 1f;

        [Tooltip("Skill multiplier")]
        [Range(0.1f, 3f)]
        public float skill = 1f;

        /// <summary>
        /// AS3 <c>Unit.armor_qual</c> — the <b>probability</b> that this unit's armour rating applies to
        /// a given hit, authored on the <c>&lt;comb&gt;</c> element as <c>aqual='…'</c>
        /// (<c>Unit.as:1154-1156</c>).
        ///
        /// <para><b>Not a magnitude.</b> It is consumed as <c>isrnd(armor_qual)</c>, and <c>isrnd(n)</c>
        /// is literally <c>Math.random() &lt; n</c> (<c>Unit.as:4855</c>) — so <c>0.75</c> means "the
        /// plate's flat rating applies to three hits in four", not "75 % damage reduction". The rating
        /// it gates is <see cref="armor"/> (physical) or <see cref="magicArmor"/> (energy), picked by the
        /// damage type.</para>
        ///
        /// <para><b>Zero means never, and zero is AS3's default.</b> A unit with <c>armor</c> but no
        /// <c>aqual</c> wears its pool down without ever reducing a hit. That is the oracle's behaviour,
        /// not a bug to "fix" with a default of 1.</para>
        ///
        /// <para><b>This field used to be called <c>waterAbility</c> and was filled from the same
        /// attribute.</b> The name came from a reading of <c>aqual</c> as "aquatic ability"; the oracle
        /// has no such stat — <c>@aqual</c> is written to exactly one place, <c>armor_qual</c>
        /// (<c>:1154-1156</c>), and the field had <b>zero readers</b> in the port, so the mis-mapping was
        /// invisible. Renaming it (rather than adding a second field) keeps one field per attribute; the
        /// values already in the 148 unit assets sit under the old key and are discarded on load, so the
        /// next **overwrite**-import is what makes them live. Until then every asset answers the class
        /// default, which is "no armour reduction" — the pre-existing behaviour, not a regression.</para>
        /// </summary>
        [Tooltip("AS3 'aqual' — probability (0..1) that this unit's flat armour rating applies to a hit. " +
                 "0 = never. The pool still wears down either way.")]
        [Range(0f, 1f)]
        public float armourQuality = 0f;

        [Tooltip("AS3 'ear' — the unit's hearing multiplier. 0 = stone deaf (every small robot in the " +
                 "roster authors it), 1 = ordinary, 2.5 = the boss at :2761.")]
        [Range(0f, 5f)]
        public float ear = 1f;

        [Tooltip("Damage type")]
        public DamageType damageType = DamageType.PhysicalMelee;

        [Tooltip("Is stable (cannot be knocked down)")]
        public bool isStable = false;

        /// <summary>
        /// AS3 <c>Unit.weaponKrep</c> — the raw <c>&lt;comb krep&gt;</c> value. Declared <b>1</b>
        /// (<c>Unit.as:332</c>) and overwritten only when the attribute is present (<c>Unit.as:1168</c>),
        /// so <b>absent means 1, not 0</b>. The player is the one unit that forces it to 0
        /// (<c>UnitPlayer.as:368</c>).
        ///
        /// <para><b>Why this is not <see cref="isStable"/>.</b> <c>isStable</c> is the importer's
        /// <c>krep == 1</c> projection and it collapses two different rows into <c>false</c>: the 14
        /// that author <c>krep='0'</c> and the 91 that author nothing. For the rules that ask
        /// "is the weapon rigidly mounted" those two answers are opposites — 0 lerps, absent snaps —
        /// so the projection cannot carry the rule. Measured over <c>AllData.as</c>: 139
        /// <c>&lt;comb&gt;</c> nodes, 34 <c>krep='1'</c>, 14 <c>krep='0'</c>, 91 absent.</para>
        ///
        /// <para><b>Consumers, and why only one so far.</b> The snap-vs-lerp rule reads this
        /// (<c>RangedWeaponController.SnapToHoldPoint</c>, fed by <c>EnemyWeaponMount</c>), because its
        /// only interim error before a re-import is "a rigid mount snaps instead of trailing" — which is
        /// strictly closer to the oracle. <see cref="isStable"/> still feeds
        /// <c>ArmedShooterController.WeaponKrep</c> and <c>EnemyWeaponMount.IsFixedMeleeMount</c> on
        /// purpose: for those, defaulting the 14 <c>krep='0'</c> rows to 1 would flip a melee-armed
        /// raider from a swinging weapon to a rigid one. <b>Switch them to this field only after
        /// <c>PFE/Data/Reimport Units (Overwrite Existing)</c> has run</b>, which is what writes 0 onto
        /// those 14 assets.</para>
        /// </summary>
        [Tooltip("AS3 'comb krep' — 1 = the weapon is rigidly mounted (snaps to the hold point, and " +
                 "a melee weapon does not swing). DEFAULT 1: the oracle's field default, applied when " +
                 "the row authors no krep. Only krep='0' opts out (14 rows + the player).")]
        [Range(0, 2)]
        public int weaponKrep = 1;

        #endregion

        #region Parameters (param tag)

        [Header("Parameters")]
        [Tooltip("Blood type: 0=None, 1=Red, 2=Green, 3=Pink (param.@blood). NONE is AS3's field " +
                 "default (Unit.as:416) and is also the bleed-immunity flag (:1417-1420) — the 100 of " +
                 "134 <param> nodes that do not author `blood` are bleed-immune, not red.")]
        public BloodType bloodType = BloodType.None;

        [Tooltip("AS3 `trup` — 'corpse'. True means the unit leaves a body and plays its die/death " +
                 "animation. False means it is destroyed outright: no corpse, no death frame, no death " +
                 "rattle. Field default is AS3's `true` (Unit.as:422); <param trup='0'> sets false. " +
                 "Only the 3 units that author it (rat, tarakan, ant) are false.")]
        public bool leavesCorpse = true;

        [Tooltip("Is invulnerable")]
        public bool isInvulnerable = false;

        [Tooltip("Can activate traps")]
        public bool canActivateTraps = true;

        [Tooltip("Uses special stats")]
        public bool usesSpecialStats = false;

        [Tooltip("Is NPC (not hostile)")]
        public bool isNpc = false;

        [Tooltip("Can overlook (see through obstacles)")]
        public bool canOverlook = false;

        [Tooltip("Is pony")]
        public bool isPony = false;

        [Tooltip("Is zombie")]
        public bool isZombie = false;

        [Tooltip("Is insect")]
        public bool isInsect = false;

        [Tooltip("Is mechanical (robot)")]
        public bool isMechanical = false;

        [Tooltip("Is alicorn")]
        public bool isAlicorn = false;

        [Tooltip("Hero type (for damage bonuses)")]
        public string heroType;

        [Tooltip("Has hero bonus")]
        public bool hasHeroBonus = false;

        #endregion

        #region Vulnerabilities

        [Header("Vulnerabilities")]
        [Tooltip("AS3 Unit.vulner. Neutral is all-1 *except emp*, which AS3 forces to 0 (Unit.as:590).")]
        public VulnerabilityData vulnerabilities = VulnerabilityData.Neutral;

        #endregion

        #region Vision/AI

        [Header("Vision/AI")]
        /// <summary>
        /// AS3 <c>Unit.noiseRun:int = 200</c> — how loud this unit is when it moves at full speed,
        /// authored on the <c>&lt;vis&gt;</c> element as <c>noise='…'</c>.
        ///
        /// <para><b>Renamed from <c>noiseLevel</c>, and the rename is the point.</b> The old name read as
        /// "a tuning knob" and the importer accordingly <i>hardcoded</i> it to 300 for every unit in the
        /// game instead of reading the attribute — so the value was invented, uniform, and read by no
        /// code. The oracle's name says what it is: the noise you make while running. The default is the
        /// oracle's 200 (<c>actionscript_project_context.txt:122272</c>), not the port's 300. Existing
        /// <c>.asset</c> files still carry a stale <c>noiseLevel:</c> key; Unity ignores an unknown key
        /// and the importer rewrites the value, so a reimport is all that is needed.</para>
        ///
        /// <para><b>The magnitude, for sanity-checking a row:</b> <c>&lt;vis noise='600'/&gt;</c> is what
        /// every zombie and raider authors, so they are audible at 600 px when running; slimes author
        /// <c>noise='0'</c> and are silent whatever they do. The player authors nothing and keeps 200.
        /// See <see cref="PFE.Entities.Units.NoiseMath.MovementNoise"/> for how this is spent — at the
        /// oracle's thresholds a unit is only ever at full <c>noiseRun</c> above 12 px/frame.</para>
        /// </summary>
        [Tooltip("AS3 'noise' (Unit.noiseRun) — the radius in px this unit is audible at while running " +
                 "at full speed. 0 = silent. Default 200.")]
        [Range(0, 2000)]
        public int noiseRun = 200;

        [Tooltip("Visual damage level")]
        [Range(0, 10)]
        public int visualDamageLevel = 1;

        [Tooltip("Splash damage")]
        [Range(0, 50)]
        public int splashDamage = 0;

        [Tooltip("Trip damage")]
        [Range(0, 50)]
        public int tripDamage = 0;

        [Tooltip("Dialogue set ID")]
        public string dialogueSet;

        [Tooltip("Teleport color")]
        public Color teleportColor = Color.white;

        [Tooltip("Sprite")]
        public Sprite sprite;

        /// <summary>
        /// The AS3 sheet id from <c>&lt;vis blit='sprRaider5' …/&gt;</c> — the name of the sprite sheet
        /// this unit is drawn from, e.g. <c>sprRaider5</c>, <c>sprAnt1</c>, <c>sprZombie3</c>.
        ///
        /// <para>Kept as a string because it is the <b>join key</b> to the imported sheet assets, and
        /// because the units that are drawn as a DisplayObject rather than a sheet (<c>slime</c>,
        /// <c>turret</c>, <c>training</c>) have none — an empty value here is meaningful, not an error.
        /// A sheet unit whose family declared the blits gets its sheet from the variant node, so this is
        /// the variant's value: <c>raider5</c> is <c>sprRaider5</c> while the family <c>raider</c> has
        /// none at all.</para>
        /// </summary>
        [Tooltip("AS3 sprite sheet id (vis@blit), e.g. sprRaider5 — empty for DisplayObject units")]
        public string spriteSheetId;

        /// <summary>
        /// The AS3 <c>vclass</c> DisplayObject name from <c>&lt;vis vclass='visualSlime'/&gt;</c>, for the
        /// 24 room-placed units that have no sprite sheet. Empty for sheet units. Note that AS3 gives
        /// <c>blit</c> precedence over <c>vclass</c> when a node somehow carries both
        /// (<c>Unit.as:886-890</c>).
        /// </summary>
        [Tooltip("AS3 DisplayObject class name (vis@vclass), e.g. visualSlime — empty for sheet units")]
        public string visualClassName;

        /// <summary>
        /// The sliced sheet, <b>row-major over the whole grid</b>: index = <c>row * spriteSheetColumns + column</c>.
        ///
        /// <para>Row-major over the <i>whole</i> sheet rather than a per-state list on purpose. AS3 keeps
        /// the state's row (<c>BlitAnim.id</c>, from <c>@y</c>) and its frame (<c>BlitAnim.f</c>) as two
        /// independent numbers and indexes the sheet with both — <c>Unit.as:2866</c>,
        /// <c>blitRect.x = col * blitX; blitRect.y = row * blitY;</c> — so a flat grid array is the shape
        /// the runtime actually needs. Two states can share a row (<c>raider</c>'s <c>death</c> and
        /// <c>fall</c> are both <c>y='5'</c>), which a per-state list could not express without
        /// duplicating sprites.</para>
        /// </summary>
        [Tooltip("Sliced sheet, row-major: index = row * spriteSheetColumns + column")]
        public Sprite[] spriteSheet;

        /// <summary>
        /// Cell size in pixels, from <c>&lt;vis sprX= sprY=/&gt;</c>. <c>sprY</c> defaults to <c>sprX</c>
        /// when absent — <c>Unit.as:939</c>, <c>sprY = @sprY &gt; 0 ? int(@sprY) : sprX</c> — but the two
        /// are genuinely separate, because the data uses non-square cells: <c>ant1</c> is 78x32,
        /// <c>tarakan</c> 60x30, <c>molerat</c> 85x58, <c>hellhound1</c> 200x170. Collapsing them to
        /// <c>(sprX, sprX)</c> gives every one of those the wrong cell height.
        /// </summary>
        [Tooltip("Cell size in pixels (vis@sprX, vis@sprY); sprY defaults to sprX")]
        public Vector2Int spriteDimensions = new Vector2Int(120, 120);

        /// <summary>
        /// Columns in the imported sheet, <c>textureWidth / sprX</c>. 0 until the sprite import has run.
        /// The divisor is the unit's <b>own</b> <c>sprX</c>, never a global constant: the column count
        /// varies (24, 26, 25, 15, 11, 8, 14 …) and only the unit's own cell size divides its sheet exactly.
        /// </summary>
        [Tooltip("Columns in the imported sheet (width / sprX); 0 before the sprite import has run")]
        public int spriteSheetColumns;

        /// <summary>Rows in the imported sheet, <c>textureHeight / sprY</c>. 0 until the sprite import has run.</summary>
        [Tooltip("Rows in the imported sheet (height / sprY); 0 before the sprite import has run")]
        public int spriteSheetRows;

        /// <summary>
        /// The AS3 registration point, from <c>&lt;vis sprDX= sprDY=/&gt;</c> — the pixel <i>inside the
        /// cell</i> that lands on the unit's origin. <b>Not a draw size</b>, which is what the previous
        /// field name claimed: <c>Unit.as:2848-2858</c> reads <c>visBmp.x = -blitDX</c>, i.e. it offsets
        /// the cell so that this point sits at (0,0).
        ///
        /// <para>A negative component means "not declared", and AS3 then falls back to centring
        /// horizontally (<c>-blitX / 2</c>) and anchoring 10px above the cell's bottom
        /// (<c>-blitY + 10</c>). That fallback is the "feet on the ground" default, so it is reproduced
        /// rather than replaced with a plain centre pivot.</para>
        /// </summary>
        [Tooltip("AS3 registration point (vis@sprDX/sprDY) in pixels; negative = not declared")]
        public Vector2Int registrationPoint = new Vector2Int(-1, -1);

        /// <summary>
        /// The icon cell within the same sheet, from <c>&lt;vis icoX= icoY=/&gt;</c>
        /// (<c>Unit.as:940-941</c>, which defaults both to 0 when absent or non-positive). Combined with
        /// <see cref="spriteSheetId"/> and <see cref="spriteDimensions"/> this is enough to lift the
        /// unit's portrait straight out of the sheet it is already drawn from.
        /// </summary>
        [Tooltip("Icon cell within the sheet (vis@icoX/icoY); (-1,-1) = not declared")]
        public Vector2Int iconCell = new Vector2Int(-1, -1);

        /// <summary>
        /// The unit's <b>separate aiming limb</b>, or null for the ~every unit that has none — AS3
        /// <c>UnitMerc.arm</c>, <c>Res.getVis("visualGrifArm" + tr, …)</c>
        /// (<c>fe/unit/UnitMerc.as:29</c>).
        ///
        /// <para><b>Why it is a sprite on the definition and not a second sheet.</b> The limb is a
        /// one-frame symbol of its own, chosen by the <i>same</i> variant number as the body sheet —
        /// <c>sprGriffonN</c> pairs with <c>visualGrifArmN</c> — so the only thing the runtime needs is
        /// "which sprite", and the number it needs to look up the limb's geometry (the port's
        /// <c>GriffinArmMath.Variants</c>) is the same one. Keeping it off the sheet means the body's
        /// cell grid is untouched by a limb that is not part of any of its states.</para>
        ///
        /// <para>Written by <c>UnitSpriteImporter</c>'s griffin-arm pass, which also bakes the limb's
        /// measured registration point into the sprite's pivot. Null means the pass has not run, and the
        /// griffin then holds its weapon at the body hold point rather than not shooting at all.</para>
        /// </summary>
        [Tooltip("AS3 UnitMerc.arm — the separate aiming limb (visualGrifArmN); null for every other unit")]
        public Sprite armSprite;

        [Tooltip("Gender")]
        public Gender gender = Gender.Other;

        #endregion

        #region Sounds

        [Header("Sounds")]
        [Tooltip("Music track ID played when this unit is in combat")]
        public string musicTrack;

        [Tooltip("Sound ID played on death (supports group IDs like 'rm', 'rw')")]
        public string deathSoundId;

        [Tooltip("Sound ID played when the unit falls / lands")]
        public string fallingSoundId;

        [Tooltip("Sound ID looped while the unit is running (e.g. 'drone' for robots)")]
        public string soundRun;

        #endregion

        #region Special

        [Header("Special")]
        [Tooltip("Detection distance")]
        [Range(0, 2000)]
        public int detectionDistance = 400;

        [Tooltip("Grenade count")]
        [Range(0, 20)]
        public int grenadeCount = 0;

        /// <summary>
        /// AS3 <c>&lt;un stalk='…'&gt;</c> → <c>UnitRaider.stalkDist</c> (<c>UnitRaider.as:327-330</c>),
        /// the radius inside which a flying unit stops approaching and hovers
        /// (<c>:1291-1299</c>).
        ///
        /// <para><b><c>500</c> is AS3's declared default</b> (<c>UnitRaider.as:91</c>), not a tuned
        /// number, and the previous <c>0</c> initialiser was a value no oracle class ever holds: a
        /// <c>stalkDist</c> of 0 means "never hover", which the oracle only reaches by overwriting it for
        /// a contact attacker (<c>:275-278</c>). That overwrite is a rule the brain applies, not
        /// something the asset should encode.</para>
        ///
        /// <para>6 rows author the attribute — see
        /// <see cref="UnitExtrasParser.ParseStalkDistance"/>.</para>
        /// </summary>
        [Tooltip("Stalk distance (AS3 stalkDist) — a flyer's hover radius")]
        [Range(0, 2000)]
        public int stalkDistance = 500;

        /// <summary>
        /// AS3 <c>&lt;un och='…'&gt;</c> → <c>UnitRaider.aiAttackOch</c> (<c>UnitRaider.as:319-321</c>).
        ///
        /// <para><b>Raider-only, and that is measured, not assumed.</b> <c>@och</c> has exactly one
        /// reader in the whole oracle — <c>UnitRaider.as:319-321</c> — so the attribute reaches
        /// <c>UnitMerc</c>, <c>UnitEncl</c> and <c>UnitRanger</c> through inheritance and reaches
        /// <b>nothing</b> in the robot family. <c>UnitAIRobot</c> declares its own
        /// <c>aiAttackOch = 100</c> (<c>UnitAIRobot.as:26</c>) and never reads it from XML, so a robot
        /// with an authored <c>och</c> keeps 100. That is why <c>RobotBrain._burstCooldownTicks</c>
        /// stays a constant: there is no data to import for it.</para>
        ///
        /// <para><b><c>0</c> means "not authored", and each family reads that differently — which is
        /// why the asset stores the raw presence rather than a resolved number.</b> The raider's declared
        /// default is also <c>0</c>, and a <c>0</c> there means the 10 %-per-frame branch — unless the
        /// rolled weapon's magazine is over 10, in which case <c>UnitRaider</c>'s constructor derives
        /// <c>round(holder / 2)</c> (<c>:217-219</c>). The robot's declared default is <c>100</c> and it
        /// has no holder rule at all. Resolving here would bake one family's default into the other's
        /// data.</para>
        /// </summary>
        [Tooltip("Burst cadence in ticks (AS3 aiAttackOch); 0 = not authored")]
        [Range(0, 300)]
        public int aiAttackOch = 0;

        /// <summary>
        /// AS3 <c>&lt;un dist='…'&gt;</c> → <c>UnitRaider.aiDist</c> (<c>:323-326</c>), the range at
        /// which the ranged arm commits (<c>:756</c>).
        ///
        /// <para><b>This is not <see cref="detectionDistance"/>.</b> That field is AS3's
        /// <c>observ</c>/observation power path and was being used as an <c>aiDist</c> stand-in — 400
        /// for every unit in the game, where the oracle's default is 1000 and 8 rows author 500/600/700.
        /// Keeping them separate is what lets the commit range be the authored number while observation
        /// stays observation.</para>
        /// </summary>
        [Tooltip("Commit range in pixels (AS3 aiDist); 1000 is AS3's default")]
        [Range(0, 3000)]
        public int aiDistance = 1000;

        [Tooltip("Action points (SATS)")]
        [Range(0, 100)]
        public int actionPoints = 0;

        [Tooltip("Special state")]
        [Range(0, 10)]
        public int specialState = 0;

        [Tooltip("Attract range")]
        [Range(0, 1000)]
        public int attractRange = 0;

        /// <summary>
        /// AS3 <c>UnitRaider.walker</c>, set from the <b>presence</b> of <c>&lt;un walker='1'/&gt;</c>
        /// (<c>UnitRaider.as:331-334</c>; the value is ignored, exactly as <c>res</c>). 23 unit rows
        /// author it.
        ///
        /// <para><b>Its two consumers are ported and its writer was not.</b>
        /// <c>ArmedShooterBrain.ApplyDefinitionTuning</c> reads this into <c>_isWalker</c>, which pins the
        /// walk speed to a literal 2.5 px/frame (<c>UnitRaider.as:151-155</c>) and selects the
        /// <c>walk</c> rather than <c>trot</c> animation while patrolling or holding
        /// (<c>:434</c>). Until <c>UnitDataImporter.ParseUnitExtras</c> read the attribute, every one of
        /// those 23 units ran at its own <c>moveSpeed</c> and played <c>trot</c>.</para>
        /// </summary>
        [Tooltip("Is walker — AS3 UnitRaider.walker (presence of <un walker=.../>). Pins walk speed " +
                 "to 2.5 px/frame and selects the walk animation.")]
        public bool isWalker = false;

        /// <summary>
        /// AS3 <c>UnitRaider.sniper</c>, set from the <b>presence</b> of <c>&lt;un sniper='1'/&gt;</c>
        /// (<c>UnitRaider.as:335-338</c>). 17 unit rows author it.
        ///
        /// <para><b>Not the placement's <c>ai="sniper"</c>.</b> They are two switches that share a word:
        /// this one is the unit row's own and only suppresses the ranged arm's aim jitter
        /// (<c>UnitRaider.as:1509</c> — <c>if(!this.sniper) mazil = aiState == 4 ? 5 : 16;</c>); the
        /// placement one is the room's (<c>:236-241</c>) and also clears <c>stroll</c> and <c>moving</c>
        /// and raises <c>weaponSkill</c> to 2. <c>zebra3</c> authors this one.</para>
        /// </summary>
        [Tooltip("Is sniper — AS3 UnitRaider.sniper (presence of <un sniper=.../>). Suppresses the " +
                 "ranged arm's aim jitter. NOT the placement's ai=\"sniper\".")]
        public bool isSniper = false;

        [Tooltip("Has grenades")]
        public bool hasGrenades = false;

        [Tooltip("Uses enclave weapons")]
        public bool usesEnclaveWeapons = false;

        /// <summary>
        /// AS3 <c>UnitRaider.stroll</c>'s <b>unit-row</b> writer — the presence of
        /// <c>&lt;un stay='1'/&gt;</c> (<c>UnitRaider.as:351-354</c>:
        /// <c>if(node0.un.@stay.length()) this.stroll = false;</c>).
        ///
        /// <para><b>Exactly one unit row authors it — <c>zebra3</c></b> (<c>AllData.as:361</c>, the
        /// "Зебра-ассасин" row, which also carries <c>walker='1' sniper='1'</c>). The other four writers
        /// of <c>stroll</c> are the <i>placement's</i> <c>ai</c> spellings (<c>:227-241</c>), which the
        /// port reads through <c>UnitController.PlacementStaysPut</c>; this field is the row's own half,
        /// and <c>UnitController.PlacementStaysPut</c> ORs it in for the raider family only — the family
        /// whose constructor owns the read.</para>
        ///
        /// <para><b>This is the unit row's <c>stay</c>, not the room's.</b> <c>RoomsSerial2.as:3061</c>
        /// authors <c>stay="1"</c> on an <c>&lt;obj&gt;</c> placement, and nothing in AS3 reads
        /// <c>param3.@stay</c> — the only read anywhere in the oracle is <c>node0.un.@stay</c>. That
        /// placement attribute is dead data in the original, and the port must not "fix" it by reading it.
        /// </para>
        ///
        /// <para><b>Presence, not value.</b> The single author writes <c>'1'</c>, and AS3 never compares
        /// it, so the value is ignored — the same call as <see cref="canResurrect"/> and
        /// <see cref="isWalker"/>.</para>
        /// </summary>
        [Tooltip("Will stay in place — AS3 UnitRaider.stroll=false from the unit row's own " +
                 "<un stay=.../> (presence). Only zebra3 authors it; distinct from the placement's " +
                 "ai=\"stay\", which is read separately.")]
        public bool willStayInPlace = false;

        /// <summary>
        /// AS3 <c>UnitZombie.isRes</c>, set from the <b>presence</b> of <c>&lt;un res='…'/&gt;</c>
        /// (<c>UnitZombie.as:161-164</c> — the value is ignored, so <c>res='1'</c> and <c>res='2'</c>
        /// mean the same thing). Only <c>zombie7/8/9</c> author it.
        ///
        /// <para><b>No reader yet.</b> The 300-tick resurrection countdown
        /// (<c>UnitZombie.as:495-524</c>) is not ported, so nothing consumes this flag today and the
        /// dead-zombie branch in <c>ZombieBrain.ResolveAnimState</c> collapses AS3's <c>sost == 3</c>
        /// into <c>sost == 2</c>. The importer writes it so the data survives a re-import; a unit that
        /// actually resurrects is feature work, not import repair.</para>
        ///
        /// <para><b><c>res</c> is an overloaded attribute.</b> <c>UnitTrigger</c> reads the same name
        /// as a resource requirement (<c>UnitTrigger.as:117-119</c>), so this field is only correct if
        /// the import applies it to the zombie family alone — see
        /// <c>UnitDataImporter.ParseUnitExtras</c>.</para>
        /// </summary>
        [Tooltip("Can resurrect (AS3 UnitZombie.isRes; presence of <un res=.../>)")]
        public bool canResurrect = false;

        /// <summary>
        /// AS3 <c>UnitZombie.superSilaTip</c> — which super-attack this variant owns, from
        /// <c>&lt;un ss='N'/&gt;</c> (<c>UnitZombie.as:151-156</c>, resolved into the effect ladder by
        /// <c>setSuper()</c>, <c>:954-1037</c>). <b>0 means "no super"</b>, which is AS3's own field
        /// initialiser and the state of every unit that does not author the attribute.
        ///
        /// <para><b>Nine rows carry it, and they are exactly <c>zombie1..zombie9</c></b>
        /// (<c>AllData.as:793, 803, 813, 828, 838, 849, 865, 881, 896</c>). <c>zombie0</c> — "Дикий гуль",
        /// the plain ghoul — authors none, which is why the family template and the weakest variant are
        /// the two units with no special at all.</para>
        ///
        /// <para><b>The values are a dispatch table, not a strength.</b> 1 pounce, 2 telekinesis,
        /// 3 venom spit, 4 acid spit, 5 radiation aura, 6 quake, 7 necro bolt + telekinesis, 8 pink spit
        /// + death blast, 9 is rewritten to 1 with a stronger jump by <c>setSuper()</c>
        /// (<c>:962-969</c>). Reading it as "power level" would make every variant the same unit with a
        /// different multiplier.</para>
        /// </summary>
        [Tooltip("AS3 UnitZombie.superSilaTip from <un ss=N/> — 0 = no super, 1..9 = the effect " +
                 "ladder in ZombieBrain. Only zombie1..9 author it.")]
        public int superSkill = 0;

        [Tooltip("Glows")]
        public bool glows = false;

        [Tooltip("Has light bulb")]
        public bool hasLightBulb = false;

        [Tooltip("Can carry items")]
        public bool canCarryItems = false;

        [Tooltip("Attach distance")]
        [Range(0f, 5f)]
        public float attachDistance = 1f;

        [Tooltip("Drop item ID")]
        public string dropItem;

        #endregion

        #region Weapons

        [Header("Weapons")]
        [Tooltip("Weapons this unit can carry")]
        public WeaponChance[] weapons;

        #endregion

        #region Animations

        [Header("Animations")]
        public AnimationSet animations;

        #endregion

        protected override bool OnValidateData()
        {
            // Validate parent unit exists
            if (!string.IsNullOrEmpty(parentId))
            {
                // Would check database in real implementation
            }

            // Validate weapon references
            if (weapons != null)
            {
                foreach (var w in weapons)
                {
                    if (string.IsNullOrEmpty(w.weaponId))
                    {
                        Debug.LogWarning($"{id}: Weapon has null ID");
                        return false;
                    }
                }
            }

            return true;
        }

        public override string[] GetReferencedDataIds()
        {
            var ids = new System.Collections.Generic.List<string>();

            if (!string.IsNullOrEmpty(parentId))
                ids.Add(parentId);

            if (!string.IsNullOrEmpty(controllerId))
                ids.Add(controllerId);

            if (weapons != null)
            {
                foreach (var w in weapons)
                {
                    if (!string.IsNullOrEmpty(w.weaponId))
                        ids.Add(w.weaponId);
                }
            }

            return ids.ToArray();
        }
    }
}
