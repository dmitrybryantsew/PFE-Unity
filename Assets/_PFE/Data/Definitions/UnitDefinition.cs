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

        [Tooltip("AI controller ID")]
        public string controllerId;

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

        [Tooltip("Acceleration rate")]
        [Range(0.1f, 10f)]
        public float acceleration = 2f;

        [Tooltip("Braking/friction")]
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
        public float RunSpeed => moveSpeed * runMultiplier;
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

        [Tooltip("Observation range (detection)")]
        [Range(0, 20)]
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

        [Tooltip("Water ability")]
        [Range(0f, 2f)]
        public float waterAbility = 1f;

        [Tooltip("Hearing range")]
        [Range(0f, 20f)]
        public float hearingRange = 5f;

        [Tooltip("Damage type")]
        public DamageType damageType = DamageType.PhysicalMelee;

        [Tooltip("Is stable (cannot be knocked down)")]
        public bool isStable = false;

        #endregion

        #region Parameters (param tag)

        [Header("Parameters")]
        [Tooltip("Blood type: 0=None, 1=Red, 2=Green, 3=Pink (param.@blood). NONE is AS3's field " +
                 "default (Unit.as:416) and is also the bleed-immunity flag (:1417-1420) — the 100 of " +
                 "134 <param> nodes that do not author `blood` are bleed-immune, not red.")]
        public BloodType bloodType = BloodType.None;

        [Tooltip("Leaves corpse on death")]
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
        [Tooltip("Noise level generated")]
        [Range(0, 2000)]
        public int noiseLevel = 300;

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

        [Tooltip("Stalk distance")]
        [Range(0, 2000)]
        public int stalkDistance = 0;

        [Tooltip("Action points (SATS)")]
        [Range(0, 100)]
        public int actionPoints = 0;

        [Tooltip("Special state")]
        [Range(0, 10)]
        public int specialState = 0;

        [Tooltip("Attract range")]
        [Range(0, 1000)]
        public int attractRange = 0;

        [Tooltip("Is walker (patrols)")]
        public bool isWalker = false;

        [Tooltip("Is sniper")]
        public bool isSniper = false;

        [Tooltip("Has grenades")]
        public bool hasGrenades = false;

        [Tooltip("Uses enclave weapons")]
        public bool usesEnclaveWeapons = false;

        [Tooltip("Will stay in place")]
        public bool willStayInPlace = false;

        [Tooltip("Can resurrect")]
        public bool canResurrect = false;

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
