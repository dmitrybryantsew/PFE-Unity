namespace PFE.Data.Definitions
{
    /// <summary>
    /// All enumerations for PFE data system.
    /// Matching the ActionScript constants from the original game.
    /// </summary>

    /// <summary>
    /// Faction (<c>fraction</c> in AS3) — the team id that decides who may damage whom.
    ///
    /// <para><b>The numbers are AS3's, deliberately.</b> <c>Unit.as:78-86</c> declares
    /// <c>F_PLAYER = 100</c>, <c>F_MONSTER = 1</c>, <c>F_RAIDER = 2</c>, <c>F_ZOMBIE = 3</c> and
    /// <c>F_ROBOT = 4</c>, and <c>Unit.as:454</c> defaults <c>fraction = 0</c>. The port previously
    /// renumbered these as Neutral/Player/Enemy/Unknown/Special, which put <c>Player</c> at <b>1</b> —
    /// the value AS3 uses for a <i>monster</i>. Since the importer casts the raw attribute
    /// (<c>(FactionType)fraction</c>), that made all 29 <c>fraction='1'</c> monsters decode as the
    /// player's faction. Keeping AS3's numbers makes the import a plain cast and lets a faction be
    /// compared against the oracle by eye.</para>
    ///
    /// <para>Note the player is <i>not</i> in the data: <c>littlepip</c> carries no <c>fraction</c>
    /// attribute at all, and <c>UnitPlayer.as:385</c> assigns <c>fraction = F_PLAYER</c> in code. The
    /// port mirrors that with an override on <c>PlayerController</c> rather than by writing to the
    /// shared <c>UnitDefinition</c> asset.</para>
    /// </summary>
    public enum FactionType
    {
        /// <summary>AS3 <c>Unit.as:454</c> default — a unit with no <c>fraction</c> attribute.</summary>
        Neutral = 0,

        /// <summary>AS3 <c>F_MONSTER</c> (<c>Unit.as:80</c>).</summary>
        Monster = 1,

        /// <summary>AS3 <c>F_RAIDER</c> (<c>Unit.as:82</c>).</summary>
        Raider = 2,

        /// <summary>AS3 <c>F_ZOMBIE</c> (<c>Unit.as:84</c>).</summary>
        Zombie = 3,

        /// <summary>AS3 <c>F_ROBOT</c> (<c>Unit.as:86</c>).</summary>
        Robot = 4,

        /// <summary>AS3 <c>F_PLAYER</c> (<c>Unit.as:78</c>) — the player and their allies.</summary>
        Player = 100
    }

    /// <summary>
    /// Unit category from AS3.
    /// 1 = Template, 2 = Faction, 3 = Spawnable
    /// </summary>
    public enum UnitCategory
    {
        Template = 1,
        Faction = 2,
        Spawnable = 3
    }

    /// <summary>
    /// Blood type from AS3.
    /// 0 = None, 1 = Red, 2 = Green, 3 = Pink
    /// </summary>
    public enum BloodType
    {
        None = 0,
        Red = 1,
        Green = 2,
        Pink = 3
    }

    /// <summary>
    /// Gender from AS3.
    /// </summary>
    public enum Gender
    {
        Male,
        Female,
        Other
    }

    /// <summary>
    /// Damage type enumeration matching the original game.
    /// Based on Unit.as D_BUL through D_PINK constants.
    /// </summary>
    public enum DamageType
    {
        PhysicalBullet = 0,   // D_BUL
        Blade = 1,            // D_BLADE
        PhysicalMelee = 2,    // D_PHIS
        Fire = 3,             // D_FIRE
        Explosive = 4,        // D_EXPL
        Laser = 5,            // D_LASER
        Plasma = 6,           // D_PLASMA
        Venom = 7,            // D_VENOM
        EMP = 8,              // D_EMP
        Spark = 9,            // D_SPARK
        Acid = 10,            // D_ACID
        Cryo = 11,            // D_CRIO
        Poison = 12,          // D_POISON
        Bleed = 13,           // D_BLEED
        Fang = 14,            // D_FANG
        Balefire = 15,        // D_BALE
        Necrotic = 16,        // D_NECRO
        Psionic = 17,         // D_PSY
        Astral = 18,          // D_ASTRO
        Pink = 19,            // D_PINK
        Internal = 100,       // D_INSIDE
        FriendlyFire = 101    // D_FRIEND
    }

    /// <summary>
    /// Weapon type enumeration from AS3 — these values are AS3's `tip` attribute, verbatim.
    /// tip='0' = the base ranged Weapon  (⚠ NOT "unarmed" — see Internal below)
    /// tip='1' = Melee
    /// tip='2' = Guns (Small guns, pistols, rifles)
    /// tip='3' = Big guns (Heavy weapons)
    /// tip='4' = Thrown
    /// tip='5' = Magic
    ///
    /// `tip` is not the class selector on its own: AS3's Weapon.create() (Weapon.as:345-394) tests
    /// tip first, then the separate `punch` attribute, and only then falls through to the base
    /// (ranged) Weapon. Use <see cref="WeaponDefinition.IsUnarmed"/> for the punch/kick family.
    /// </summary>
    public enum WeaponType
    {
        /// <summary>
        /// AS3 tip='0' — the base `Weapon` class, which is a RANGED weapon.
        ///
        /// The name is a historical trap: reading it as "unarmed" is what mis-routed 57 of the 60
        /// tip==0 weapons (turrets, drone lasers, zombie spitters, alimray, robominigun) into the
        /// punch controller. Unarmed is `punch > 0`, i.e. <see cref="WeaponDefinition.IsUnarmed"/>.
        /// </summary>
        Internal = 0,
        Melee = 1,
        Guns = 2,
        BigGun = 3,
        Thrown = 4,
        Magic = 5
    }

    /// <summary>
    /// Weapon category for weapon type '2' (Guns).
    /// 0 = Unarmed, 1 = Melee, 2 = Pistol, 3 = SMG, 4 = Shotgun, 5 = Rifle, 6 = Heavy, 7 = Sniper, 8 = Explosive, 9 = Magic
    /// </summary>
    public enum WeaponCategory
    {
        Unarmed = 0,
        Melee = 1,
        Pistol = 2,
        SMG = 3,
        Shotgun = 4,
        Rifle = 5,
        Heavy = 6,
        Sniper = 7,
        Explosive = 8,
        Magic = 9
    }

    /// <summary>
    /// Melee weapon subtype from AS3.
    /// </summary>
    public enum MeleeSubType
    {
        Unarmed = 0,
        Sword = 1,
        Axe = 2,
        Sledge = 3,
        Spear = 4,
        Knife = 5,
        Club = 6,
        Fist = 7
    }

    /// <summary>
    /// Thrown weapon subtype from AS3.
    /// </summary>
    public enum ThrownSubType
    {
        Grenade = 0,
        Mine = 1,
        ThrownWeapon = 2
    }

    /// <summary>
    /// Melee weapon type from AS3.
    /// mtip='0' - Horizontal swing (sword, bat)
    /// mtip='1' - Thrust (spear)
    /// mtip='2' - Overhead smash (hammer)
    /// </summary>
    public enum MeleeType
    {
        Horizontal = 0,
        Thrust = 1,
        Overhead = 2
    }

    /// <summary>
    /// Item type enumeration from AS3.
    /// tip='a' = Ammo
    /// tip='m' = Medical
    /// tip='b' = Book
    /// tip='c' = Component
    /// tip='e' = Equipment
    /// tip='s' = Sphera (Artifact)
    /// tip='i' = Implant
    /// </summary>
    public enum ItemType
    {
        Ammo,
        Medical,
        Book,
        Component,
        Equipment,
        Chems,  // Potions
        Sphera,  // Artifacts
        Implant,
        Key,
        Quest,
        Valuable,
        Misc,

        /// <summary>
        /// AS3 <c>tip='spell'</c> — the nine cast-from-inventory spells (<c>sp_slow</c>, <c>sp_mwall</c>,
        /// <c>sp_blast</c>, <c>sp_cryst</c>, <c>sp_kdash</c>, <c>sp_mshit</c>, <c>sp_moon</c>,
        /// <c>sp_gwall</c>, <c>sp_invulner</c>). Carries <see cref="SpellData"/>.
        ///
        /// <para><b>APPENDED — this member may never be inserted above.</b> <c>ItemType</c> is
        /// serialised by <b>value</b> into every <c>ItemDefinition</c> asset (<c>type: 11</c> is
        /// <c>Misc</c>), so inserting a member anywhere earlier renumbers every value above it and
        /// silently reinterprets all 500 item assets — no error, no warning, and the assets look
        /// fine in the inspector. New members go at the end, always.</para>
        ///
        /// <para>Before this member existed, <c>FixDataImport.GetItemTypeFromSource</c> sent
        /// <c>tip='spell'</c> to its <c>default:</c> bucket alongside <c>note</c>, <c>weap</c>,
        /// <c>stuff</c>, <c>trap</c> and twenty others — so all nine spells imported as
        /// <c>Misc</c>.</para>
        /// </summary>
        Spell,
    }

    /// <summary>
    /// Item subcategory for component type.
    /// </summary>
    public enum ComponentCategory
    {
        General,
        Mechanical,
        Electronic,
        Plant,
        Energy,
        Alchemy
    }

    /// <summary>
    /// Inventory category for UI organization
    /// </summary>
    public enum InventoryCategory
    {
        General = 0,
        Weapons = 1,
        Apparel = 2,
        Aid = 3,
        Misc = 4,
        Ammo = 5,
        Books = 6,
        Keys = 7
    }

    /// <summary>
    /// How the item is used
    /// </summary>
    public enum UsageType
    {
        None = 0,
        SingleUse = 1,
        Consumable = 2,
        Equipment = 3
    }

    /// <summary>
    /// Ammo modifier variants
    /// </summary>
    public enum AmmoModifier
    {
        None = 0,
        ArmorPiercing = 1,
        Expansive = 2,
        Pulse = 3,
        Incendiary = 4,
        Plasma = 5,
        Overcharge = 6,
        Napalm = 7,
        Cryo = 8,
        Magic = 9
    }

    /// <summary>
    /// Effect category — AS3 <c>Effect.tip</c>, read from <c>&lt;eff tip&gt;</c>.
    ///
    /// <para><b>The names are the meaning AS3 actually gives each value; the previous set
    /// (<c>Good</c>/<c>Bad</c>/<c>Special</c>/<c>Neutral</c>) was an invention with no reader, and its
    /// comments contradicted its numbers.</b> Every value below is backed by a call site or by the
    /// data census (79 definitions in <c>AllData.as</c>):</para>
    /// <list type="bullet">
    /// <item><see cref="Neutral"/> (<c>0</c>) — 1 row, <c>curse</c>. A trigger effect: it writes a
    /// game flag rather than a stat, so it has no display category.</item>
    /// <item><see cref="Timed"/> (<c>2</c>) — 40 rows (<c>burning</c>, <c>freezing</c>,
    /// <c>chemburn</c>, <c>drunk</c>, …). The ordinary timed effect.</item>
    /// <item><see cref="Food"/> (<c>3</c>) — 27 rows, all <c>f_*</c>. <b>One at a time</b>: this is the
    /// only <c>tip</c> with a merge special-case (<c>Unit.as:3366-3371</c>, a wholesale replace), and
    /// the only one whose removal message is worded differently (<c>Effect.as:304</c>
    /// <c>"endFoodEffect"</c>).</item>
    /// <item><see cref="Purgeable"/> (<c>4</c>) — 11 rows (<c>alicorn</c>, <c>disorient</c>,
    /// <c>horror</c>, <c>stupor</c>, <c>weak</c>, …). A purifying potion removes every effect of this
    /// type (<c>Invent.as:447-457</c>, <c>if(eff.tip == 4) eff.unsetEff(false,true,false)</c> — note
    /// the arguments: <b>no aftereffect, no announce, no param pass</b>, followed by one
    /// <c>setParameters()</c> for the batch).</item>
    /// <item><c>1</c> — <b>declared nowhere in the data</b>. Kept out of the enum rather than named and
    /// never used, so a hand-authored <c>tip='1'</c> cannot silently map to a plausible-sounding
    /// category this project invented.</item>
    /// </list>
    /// </summary>
    public enum EffectType
    {
        /// <summary>AS3 <c>tip=0</c> — a trigger/flag effect (only <c>curse</c>).</summary>
        Neutral = 0,

        /// <summary>AS3 <c>tip=2</c> — the ordinary timed effect (40 rows).</summary>
        Timed = 2,

        /// <summary>AS3 <c>tip=3</c> — the food channel; only one may be active (27 rows).</summary>
        Food = 3,

        /// <summary>AS3 <c>tip=4</c> — removed in bulk by a purifying potion (11 rows).</summary>
        Purgeable = 4
    }

    /// <summary>
    /// Whether a unit is driven by the player's <c>Pers</c>/<c>CharacterStats</c> stack or by the
    /// NPC's <c>Unit</c>/<c>UnitStats</c> stack.
    ///
    /// <para><b>Not a cosmetic distinction — the two stacks disagree about how effects are applied.</b>
    /// The oracle replays an effect's <c>&lt;sk&gt;</c> writes with a different level index and a
    /// different treatment of removed effects depending on which class owns the effect set
    /// (<c>Unit.as:3495</c> vs <c>Pers.as:2202</c>), so a param pass must know which it is
    /// running. See the design doc §3 for the measured difference; putting it in the type system is
    /// how that difference stops being a comment nobody reads.</para>
    /// </summary>
    public enum PersMode
    {
        /// <summary><c>Unit.setEffParams</c> — index <c>1</c> active / <c>0</c> being unset.</summary>
        Npc = 0,

        /// <summary><c>Pers.setParameters</c> — index <c>eff.lvl</c>; removed effects skipped.</summary>
        Player = 1,
    }

    /// <summary>
    /// Perk type from AS3.
    /// tip='1' = Player selectable
    /// tip='0' = Automatic/effect only
    /// </summary>
    public enum PerkType
    {
        Automatic = 0,
        Selectable = 1
    }

    /// <summary>
    /// Decal type from AS3 — tipdec values in weapon XML vis node.
    /// Values match the original tipdec integers directly.
    /// </summary>
    public enum DecalType
    {
        None        = 0,
        Metal       = 1,   // metal bullet hole
        Stone       = 2,   // stone/rock impact
        Rail        = 3,   // railgun / piercing
        Blade       = 4,   // melee / blade slash
        Wood        = 5,   // wood splinter
        // 6-8 unused in source
        Explosive   = 9,   // explosion scorch
        // 10 unused
        FireEnergy  = 11,  // fire / energy burn
        Laser       = 12,  // laser scorch
        EnergyBeam  = 13,  // energy beam mark
        // 14 unused
        Plasma      = 15,  // plasma burn
        // 16-18 unused
        Sparkle     = 19,  // special sparkle / pink
    }

    /// <summary>
    /// Projectile visual and physics archetype.
    /// Determines which prefab to spawn and how the bullet behaves.
    /// Derived at import time from vbul, spring, flame, phisbul and navod XML fields.
    /// </summary>
    public enum ProjectileArchetype
    {
        /// <summary>Standard ballistic round — spring=1, no gravity override, default physics.</summary>
        Ballistic  = 0,
        /// <summary>Laser beam — spring=2, speed≥2000, stretched from origin.</summary>
        Laser      = 1,
        /// <summary>Plasma orb — vbul contains "plasma", additive glow.</summary>
        Plasma     = 2,
        /// <summary>Flame / fire — flame>0, short lifetime, gravity arc.</summary>
        Flame      = 3,
        /// <summary>Physics projectile (grenade, rocket) — phisbul=1, Dynamic Rigidbody, AoE.</summary>
        Explosive  = 4,
        /// <summary>Spark / electrical — vbul="spark"|"sparkl"|"lightning", animated.</summary>
        Spark      = 5,
        /// <summary>Spit / acid / venom — vbul contains "plevok"|"venom"|"kapl".</summary>
        Spit       = 6,
        /// <summary>Homing missile — navod>0, SmartBullet tracking.</summary>
        Homing     = 7,
        /// <summary>Innate unicorn magic spell — tip==5 (WMagic), spawns from horn, costs mana.</summary>
        Magic      = 8,
    }

    /// <summary>
    /// Location type from AS3.
    /// </summary>
    public enum LocationType
    {
        Interior,
        Exterior,
        Dungeon,
        Town,
        Special,
        Boss,
        Tutorial
    }

    /// <summary>
    /// Value tier from AS3.
    /// Determines item drop probability.
    /// </summary>
    public enum ValueTier
    {
        Trash = 0,
        Common = 1,
        Uncommon = 2,
        Rare = 3,
        Epic = 4,
        Legendary = 5
    }

    /// <summary>
    /// Skill requirement types from AS3.
    /// </summary>
    public enum RequirementType
    {
        Level,
        Skill,
        Guns,  // Small guns OR Energy weapons
        Perk
    }

    /// <summary>
    /// Stat modifier types from AS3.
    /// ref='add' = Add to base
    /// ref='mult' = Multiply with base
    /// (no ref) = Set value
    /// </summary>
    public enum ModifierType
    {
        Add,
        Multiply,
        Set,
        WeaponSkill  // Special case for weapon skills
    }

    /// <summary>
    /// Stat modifier target from AS3.
    /// </summary>
    public enum ModifierTarget
    {
        Player,
        Pers,  // Character
        Unit   // Enemy/NPC
    }
}
