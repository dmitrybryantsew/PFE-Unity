using UnityEngine;
using PFE.ModAPI;
using PFE.Systems.Combat;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace PFE.Data.Definitions
{
    /// <summary>
    /// ScriptableObject definition for weapon data.
    /// Replaces XML-based weapon definitions from AllData.as in ActionScript.
    /// Create instances via Assets > Create > PFE > Weapon Definition
    /// </summary>
    [CreateAssetMenu(fileName = "NewWeaponDef", menuName = "PFE/Weapon Definition")]
    public class WeaponDefinition : ScriptableObject, IGameContent, IWeaponStats
    {
        [Header("Identification")]
        public string weaponId;

        /// <summary>
        /// The name the Oracle shows for this weapon — its <c>Res.txt("w", id)</c> entry, the
        /// <c>&lt;weapon id="…"&gt;&lt;n&gt;</c> row in <c>text_en.xml</c>.
        ///
        /// <para><b>Empty is the safe default, and empty is what every shipped asset has.</b> This field
        /// is new, so all existing <c>WeaponDefinition</c> assets load it as <c>""</c> from the engine's
        /// zero-fill rather than from any initializer here — which is why every reader falls back to
        /// <see cref="weaponId"/>. A sentinel meaning "unset" must never be a value that reads as a real
        /// name, or a stale asset shows the wrong weapon instead of a usable one.</para>
        ///
        /// <para><b>Nothing needs this filled in to show a correct name.</b> The names already ship in
        /// <c>Resources/Localisation/text_en.xml</c>, and
        /// <c>PFE.UI.Common.PfeTextTableProvider.WeaponNameOrId</c> resolves them at runtime. This field
        /// is the frozen/offline alternative — what an importer would write — and it wins when set.</para>
        /// </summary>
        [Tooltip("Display name (the Oracle's <weapon><n> row). Empty = fall back to the weapon id.")]
        public string displayName = "";

        /// <summary>
        /// AS3 <c>@mess</c> — the once-per-save tutorial popup key, read by <c>Invent.take</c> for
        /// weapons exactly as it is for items (<c>Invent.as:1293-1300</c>).
        ///
        /// <para><b>Four of the 213 shipped weapon rows carry it</b> — <c>mont</c>, <c>r32</c>,
        /// <c>rech</c>, <c>telebul</c> (<c>AllData.as:2192</c>, <c>:2455</c>, <c>:2559</c>,
        /// <c>:3533</c>). All four have their text, and <b>not</b> where a reader would first look:
        /// the <c>&lt;weapon&gt;</c> table in <c>text_en.xml</c> ships <c>&lt;n&gt;</c> and
        /// <c>&lt;info&gt;</c> and <b>no <c>&lt;mess&gt;</c> child at all</b>, so the bodies live in
        /// the <b><c>item</c></b> table keyed by the weapon id —
        /// <c>&lt;item id="r32"&gt;&lt;mess s1="keyReload"&gt;…</c> (<c>text_en.xml:2800-2801</c>).
        /// That is the row <c>Res.txt("i", l.mess)</c> reads, so the popup path is the same one an item
        /// takes.</para>
        ///
        /// <para><b>Empty is the safe default and empty is what every shipped asset has.</b> The
        /// runtime therefore reads the shipped row through
        /// <c>PlayerInventory.WeaponRowMess</c> until the import fills this in, so the popup works
        /// without a re-import — the same arrangement that keeps <c>possLockPick</c> alive. The
        /// import is what makes the value survive offline.</para>
        /// </summary>
        [Tooltip("Tutorial popup key (AS3 @mess). Empty = fall back to the shipped AllData weapon row.")]
        public string mess = "";

        // Legacy property for compatibility
        public string ID => weaponId;

        // IGameContent
        string IGameContent.ContentId => weaponId;
        ContentType IGameContent.ContentType => ContentType.Weapon;

        [Tooltip("Weapon type determines firing behavior")]
        public WeaponType weaponType;

        /// <summary>
        /// AS3's `punch` attribute — a class selector *separate* from <see cref="weaponType"/>.
        ///
        /// This is a trap worth stating plainly: `tip` is NOT the class selector on its own.
        /// Weapon.as:345-394 dispatches tip==1 → WClub, tip==12 → WPaint, tip==4 → WThrow,
        /// tip==5 → WMagic, **then punch &gt; 0 → WPunch**, and everything else falls through to
        /// the base `Weapon` class — which is a *ranged* weapon, not "unarmed".
        ///
        /// Only 3 weapons in AllData.as carry punch='1' (scorppunch / scorp2punch / scorp3punch,
        /// all created by UnitMonstrik.as). Treating tip==0 as unarmed mis-routes the other 57
        /// tip==0 weapons (every turret, drone laser, zombie spitter, alimray, robominigun, …)
        /// to the punch controller, which spawns no projectile and draws no sprite.
        ///
        /// See docs/OnWeaponsSystemImplementation/13_WeaponTypeBehaviourAudit_2026-09-27.md §1.
        /// </summary>
        [Tooltip("AS3 `punch` attribute — 1 selects WPunch. Separate from tip; tip==0 alone means RANGED.")]
        public int punch;

        /// <summary>
        /// True only for the punch/kick family (AS3 WPunch / WKick).
        ///
        /// Single source of truth for "unarmed": both the controller factory and the sprite
        /// presenter read this, so the two cannot drift apart the way
        /// `weaponType == WeaponType.Internal` did (which hid the held sprite for 57 ranged weapons).
        /// </summary>
        public bool IsUnarmed => punch > 0;

        // Legacy property for compatibility
        public WeaponType Type => weaponType;

        /// <summary>
        /// The weight one of these adds to the carrier — AS3 <c>Item.mass</c> for a weapon row
        /// (<c>fe/serv/Item.as:278-288</c>):
        ///
        /// <code>
        /// if(this.tip == L_WEAPON &amp;&amp; this.xml) {
        ///    if(this.xml.@tip != 4) this.mass = 1;                        // :280-282
        ///    if(this.xml.phis.length() &amp;&amp; this.xml.phis.@m.length())
        ///       this.mass = this.xml.phis.@m;                             // :284-286
        /// }
        /// </code>
        ///
        /// <para><b>So the Oracle's value is 1 for every weapon except a thrown one, which stays 0.</b>
        /// <see cref="WeaponType.Thrown"/> is <c>xml.@tip == 4</c> — the two enums are the same numbering,
        /// so the rule translates directly. This is a derived property rather than a stored field
        /// precisely because the default is a <i>function of the type</i>: a stored
        /// <c>public int mass = 1;</c> would be silently wrong for all 4-ish thrown rows, and Unity's
        /// zero-fill for a field absent from an existing asset would make it wrong for all of them.</para>
        ///
        /// <para><b>The one part of the Oracle's rule this does not reproduce: the <c>&lt;phis m&gt;</c>
        /// override.</b> 74 weapon rows in <c>AllData.txt</c> carry an explicit <c>m</c> on their
        /// <c>&lt;phis&gt;</c> child and the importer does not read it, so those 74 report the default
        /// instead of their authored weight. Recorded rather than papered over: inventing a field nobody
        /// fills would look like the value was real. r32 — the weapon in the play-test log — has
        /// <c>massa='2'</c> but no <c>m</c>, so 1 is its correct value and this rule is exact for it.</para>
        /// </summary>
        public float Mass => weaponType == WeaponType.Thrown ? 0f : 1f;

        public int skillLevel;              // Required skill level
        public int weaponLevel;              // Weapon level

        /// <summary>
        /// AS3 <c>Weapon.perslvl</c> (<c>Weapon.as:116</c>, default 0) — the <b>character level</b>
        /// a weapon additionally requires, read off the weapon root (<c>Weapon.as:430</c>
        /// <c>this.perslvl = node.@perslvl</c>).
        ///
        /// <para><b>A different gate from <see cref="weaponLevel"/>.</b> <c>weaponLevel</c> is
        /// compared against the owner's <i>skill tier</i> (<c>gap = lvl - getWeapLevel(skill)</c>);
        /// <c>perslvl</c> is compared against the owner's <i>character level</i>
        /// (<c>Weapon.as:1382</c>, <c>:1866</c>). The info pane shows both as separate lines
        /// (<c>PipPage.as:478-486</c>), so one field cannot stand in for the other.</para>
        ///
        /// <para>23 rows carry it, every one a <c>tip='5'</c> spell weapon, and all but one write a
        /// non-zero value (0 for <c>telebul</c> … 28 for <c>a_magic</c>). AS3 never writes it to a
        /// non-magic weapon, so a 0 here means "no character-level requirement", which is also the
        /// attribute-absent default — the two are indistinguishable in the oracle too, because the
        /// gate is written as <c>if(w.perslvl > 0)</c>.</para>
        /// </summary>
        public int perslvl;                  // Character-level requirement (AS3 @perslvl)

        [Header("Combat Stats")]
        public float baseDamage = 10f;       // Base damage from XML

        // Legacy property for compatibility
        public float Damage => baseDamage;

        [Tooltip("Fire cooldown in frames (rapid in AS3, 30 FPS = 1 second)")]
        public float rapid = 10f;

        /// <summary>
        /// AS3's <c>char@auto</c> override. <b>0 = attribute absent</b>, 1 = <c>auto='0'</c>
        /// (force single-shot), 2 = <c>auto='1'</c> (force continuous).
        ///
        /// <para>AS3 tests <i>presence</i> first and only then the value (<c>Weapon.as:856-859</c>),
        /// so "absent" and "'0'" are different answers and a plain <c>bool</c> cannot carry both.
        /// Imported from the <b>tier-1</b> <c>&lt;char&gt;</c> node, alongside <c>rapid</c> — both live
        /// on <c>&lt;char&gt;</c>, not on the <c>&lt;weapon&gt;</c> root.</para>
        ///
        /// <para><b>Why "absent" is the zero value rather than -1.</b> Every one of the 204 existing
        /// assets predates this field, so its value on load comes from the fallback for a field the
        /// YAML does not mention — not from the importer. Encoding "absent" as 0 makes that fallback
        /// correct whether the engine applies the field initializer or zero-fills. The cost of getting
        /// it wrong is severe and silent in one direction only: zero-fill with -1-as-absent would make
        /// every weapon read as <i>explicitly single-shot</i>, so the SMG, minigun and every other
        /// fast weapon would stop firing while the attack key is held.</para>
        /// </summary>
        [Tooltip("AS3 char@auto: 0 = absent (use rapid<=6), 1 = forced single-shot, 2 = forced auto")]
        public int autoMode;

        /// <summary>
        /// True when the weapon fires continuously while the attack key is held.
        ///
        /// Mirrors <c>Weapon.as:856-859</c> exactly:
        /// <code>
        /// this.auto = this.rapid &lt;= 6;
        /// if(param1.@auto.length()) this.auto = param1.@auto != "0";
        /// </code>
        ///
        /// <para><b>Single source of truth</b>, like <see cref="IsUnarmed"/>: all three weapon
        /// controllers read this instead of repeating <c>rapid &lt;= 6</c> inline. Three copies of
        /// the heuristic is how the <c>@auto</c> half went missing from all of them at once —
        /// 14 weapons (shotgun, bfg, mont, knife, lasp, …) fired single-shot in Unity where AS3
        /// fires them continuously.</para>
        ///
        /// <para>Not modelled here: AS3 also forces <c>auto = true</c> for every non-player unit
        /// (<c>Weapon.as:339-342</c>). Nothing equips an enemy yet, so that rule has no call site —
        /// add it when an enemy weapon loadout lands.</para>
        /// </summary>
        public bool IsAuto => autoMode == 1 ? false : autoMode == 2 ? true : rapid <= 6f;

        // Legacy property for compatibility
        public int FireRateFrames => (int)rapid;

        [Tooltip("Accuracy stat (higher = better, scales with distance)")]
        public float precision = 0f;

        // Legacy property for compatibility
        public float Accuracy => precision;

        /// <summary>
        /// Minimum-range distance in pixels — AS3 <c>@antiprec * 40</c> (<c>Weapon.as:826</c>), the
        /// partner of <see cref="precision"/> and scaled identically so the two are comparable against
        /// a round's travel distance. Inside it, accuracy ramps from 0.25 to 1.0, i.e. the weapon is
        /// <b>worse</b> point-blank. <c>0</c> = no minimum range.
        ///
        /// <para>Four weapons in <c>AllData.as</c> carry <c>antiprec='8'</c> (320 px, 8 tiles) —
        /// long-range pieces with a real dead zone. Ported as data rather than dropped because
        /// "accuracy clamped to a minimum" would invert the mechanic. Consumed by
        /// <see cref="PFE.Systems.Combat.HitAvoidance.Accuracy"/>.</para>
        /// </summary>
        public float antiPrecision = 0f;

        [Tooltip("Spread angle in degrees (lower = better)")]
        public float deviation = 0f;

        public float armorPenetration = 0f;  // pier in AS3
        public float knockback = 0f;         // otbros in AS3

        [Header("Critical Hit")]
        [Range(0f, 1f)]
        public float critChance = 0.1f;      // critCh in AS3
        public float critMultiplier = 2f;    // critM in AS3

        [Header("Projectile")]
        public int projectilesPerShot = 1;   // kol in AS3
        public float projectileSpeed = 100f; // speed in AS3

        [Header("Burst Fire")]
        public int burstCount = 0;           // dkol in AS3

    [Header("Explosion")]
    public float explRadius = 0f;        // Explosion radius
    public float explosionDamage = 0f;   // damageExpl in AS3

    /// <summary>
    /// Which explosion shape this weapon's blast uses — AS3 <c>char.@expltip</c>
    /// (<c>Weapon.as:846</c>), copied to the bullet at <c>:1676</c>. 1 = <c>explBlast</c>, 2 =
    /// <c>explGas</c>, 3 = both, sequenced.
    /// </summary>
    /// <remarks>
    /// <para><b>Read by <c>Bullet.explRun()</c>'s multiplexer</b> (<c>weapon/Bullet.as:711-717</c>):
    /// <c>explTip == 1 || (explTip == 3 &amp;&amp; expl_t == 0)</c> → <c>explBlast()</c>;
    /// <c>explTip == 2 || (explTip == 3 &amp;&amp; expl_t &gt; 0)</c> → <c>explGas()</c>. So 3 means "one
    /// blast, then a sustained gas cloud".</para>
    /// <para><b>1 is AS3's declaration default and also the value the attribute is absent with</b> —
    /// <c>Weapon.as:844</c> guards the read on <c>@expltip.length()</c>, so an absent attribute leaves
    /// <c>explTip = 1</c>. Only <c>gasgr</c>, <c>acidgr</c>, <c>zombivenom</c>, <c>zombiacid</c>,
    /// <c>zombipink</c> and <c>robogas</c> carry the attribute at all.</para>
    /// </remarks>
    public int explTip = 1;              // expltip in AS3

    /// <summary>
    /// How many explosion <b>pulses</b> the blast fires — AS3 <c>char.@explkol</c>
    /// (<c>Weapon.as:850</c>). 0 and 1 both mean a single pulse; anything higher is a sustained
    /// damage area.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the "damage area".</b> <c>Bullet.explosion()</c> (<c>:683-691</c>) runs one
    /// pulse immediately and then sets <c>expl_t = (explKol - 1) * explPeriod</c>
    /// (<c>explPeriod = 10</c> frames, a class constant — <c>:121</c>); <c>run()</c> fires another
    /// pulse every time the countdown passes <c>expl_t % 10 == 1</c> (<c>:240-250</c>). So a weapon with
    /// <c>explkol='10'</c> detonates <b>ten</b> times over <c>9 * 10 = 90</c> frames — which is exactly
    /// what <c>fgren</c>, <c>molotov</c>, <c>gasgr</c> and <c>acidgr</c> do, and why they are a damage
    /// <i>area</i> rather than a single burst.</para>
    /// <para><b>Eight weapons carry a value above 1:</b> <c>fgren</c> 10, <c>molotov</c> 10,
    /// <c>gasgr</c> 12, <c>acidgr</c> 12, <c>zombivenom</c> 12, <c>zombiacid</c> 12, <c>zombipink</c>
    /// 12, <c>robogas</c> 12. <c>impgr</c> and <c>impmine</c> carry <c>1</c>, which is one pulse.</para>
    /// </remarks>
    public int explKol = 0;              // explkol in AS3
    // Still NOT IMPORTED, and why — the rest of the <char> explosion block:
    //   char.@explperiod — does not exist in the data; explPeriod is the class constant 10
    //                      (Bullet.as:121). Nothing to import.
    //   char.@massafix   — read by Unit.setLevel for the HP formula, not by the explosion path.
    //   char.@combo / @pow / @critdam — melee-combo and crit channels with no port consumer yet.
    //   char.@rashod / @recharg (on <ammo>) — ammo consumption; the port fires one round per shot.
    //   <sats> @noperc — AS3's "ignore the perk's per-shot S.A.T.S. cost" (Weapon.as:451); no port
    //          consumer yet. @cons / @no / @que ARE read now — by WeaponXmlExtrasReader, because the
    //          info pane's AP line and the S.A.T.S. cost both need them.
    //   <com>  @chance / @price / @rep / @stage / @uniq / @worth — IMPORTED NOW (see the <com> block
    //          below): all six, because the unique-variant feature needs @uniq and @price and the rest
    //          come free once the node is read. The second <com> node, which carries a variant's own
    //          price, is held on `variant.ComAttrs`.
    //   <dop>  @shoot / @vision — the on-hit "make the target shoot/see" effects, unimplemented.
    //   <phis> @drot / @drot2 / @distexpl / @grav2 / @long / @m / @minlong / @volna / @recoil /
    //          @massa — AS3's rotation/gravity/range variants; the port's ballistic model reads
    //          speed/grav/accel/flame/navod only.
    //   <vis>  @flare / @grav / @icomult / @lasm / @loot / @vweap — presentation metadata the port
    //          derives elsewhere (icomult is folded into the sprite, flare is a muzzle effect).
    //   <snd>  @dem / @t11 / @t21 — see the note on soundFall/soundSens below.
        //   root   @crack / @mess / @nostand / @sX / @sY — stand-offsets and sprite data; @sX/@sY are
        //          read by the mine placement path only (see MineObject). @perk is read by
        //          WeaponXmlExtras (the info pane's Perk line), and @perslvl is now a field above.
        // ⚠ @mess USED to be "deliberately dropped" here and no longer is: 2026-10-18 added the
        // reader for it. `Invent.take`'s tutorial popup keys off `l.mess` for WEAPONS too
        // (Invent.as:1293) — the `if(l.tip == Item.L_WEAPON)` branch is INSIDE `take` and calls
        // `addWeapon` at :1101, so it is not a separate entry point and the popup tail runs for both.
        // Measured 2026-10-18: four `<weapon>` rows carry it — `mont`, `r32`, `rech`, `telebul`
        // (AllData.as:2192, :2455, :2559, :3533) — and all four have their text in text_en.xml.
        // NOT where a reader would first look: the `<weapon>` table in that document ships `<n>` and
        // `<info>` only, **no `<mess>` child at all**, so the text lives in the **`item`** table keyed
        // by the weapon id: `<item id="r32"><mess s1="keyReload">Firearms are weapons that use
        // various types of cartridges for firing. Press @1 to reload</mess></item>`
        // (text_en.xml:2800-2801). That is the same shape `Res.txt("i", l.mess)` reads for an item, so
        // the popup path is identical and nothing downstream is weapon-specific.
        // ⚠ The runtime does NOT need a re-import to read the key: `PlayerInventory.WeaponRowMess`
        // reads the shipped row through `AllDataElements.TryGet(allData, "weapon", id, …)`, which is
        // depth-aware, public and offline-testable. The lookup is deliberately TABLE-EXPLICIT rather
        // than item-first, because measured **32 ids exist in both tables** (`b bomb`, `cryogr`,
        // `dinamit`, `fgren` …) and the Oracle reads `AllData.d.weapon` in the weapon branch.
        // Everything in that list was checked against the oracle, not against the port: each one is either
        // absent from the data, or read by an AS3 subsystem the port does not have. The list is here so the
        // next audit does not have to re-derive it — see docs/AUDIT_throwable_and_explosive_2026-10-03.md.

        [Header("Ammunition")]
        public int magazineSize = 0;         // holder in AS3
        public float reloadTime = 0f;        // reload in AS3 (frames)
        public string ammoType;

        // Legacy property for compatibility
        public string AmmoTypeID => ammoType;

        [Header("Durability")]
        public int maxDurability = 100;      // maxhp in AS3

        [Header("Fire Timing — AS3 parity")]
        [Tooltip("Charge-up frames before first shot fires (prep in AS3). 0 = no charge. Minigun, railgun, etc.")]
        public int prepFrames;
        [Tooltip("Ammo consumed per shot (rashod in AS3). Most weapons = 1. Railgun = 2, etc.")]
        public int ammoPerShot = 1;
        [Tooltip("Self-regenerating ammo cadence in frames (recharg in AS3). 0 = no recharge. Used by 'recharg' ammo weapons.")]
        public int rechargeFrames;

        [Header("Recoil")]
        [Tooltip("Duration of push-back in frames after shot (recoil in AS3). Applied as positional offset along aim axis.")]
        public int recoilFrames;
        [Tooltip("Angular lift added to rotation per shot (recoilUp in AS3). Decays each frame. In degrees.")]
        public float recoilLift;

        [Header("Magic — dual cost")]
        [Tooltip("Mana BUDGET spent per shot — AS3 `ammo@magic` -> `Weapon.dmagic` -> `owner.mana -= dmagic`. " +
                 "WMagic (tip==5) only. fireball=500, eclipse=800, mray=25.")]
        public float magicPoolCost;
        [Tooltip("Mana ORGAN wounded per shot — AS3 `ammo@mana` -> `Weapon.dmana` -> `pers.manaDamage(dmana)`. " +
                 "WMagic (tip==5) only. Not the same pool as magicPoolCost; the organ does not regenerate.")]
        public float manaHealthCost;

    [Header("Thrown Settings")]
    [Tooltip("Throw sub-type (throwtip in AS3). 0=arc grenade, 1=mine placement, 2=sticky throw.")]
    public int throwTip;
    [Tooltip("Fuse countdown in frames before detonation (detTime in AS3). Default 75 (~2.5s at 30fps).")]
    public int fuseFrames = 75;
    [Tooltip("Radio detonation flag (char.@radio in AS3). Second press detonates placed mines.")]
    public bool radio;

    /// <summary>
    /// Placed-mine proximity box half-width, in AS3 pixels — <c>Mine.sens</c>, read from the weapon's
    /// own <c>&lt;char sens&gt;</c> (<c>Mine.as:166-169</c>, declaration default 100).
    ///
    /// <para>The trigger box is <b>not</b> a circle and <b>not</b> symmetric: AS3 tests
    /// <c>|dx| &lt; sens</c> horizontally but <c>dy ∈ (-sens, +0.4·sens)</c> vertically
    /// (<c>Mine.as:317</c>), and AS3's Y runs downward, so a mine senses <c>sens</c> px <i>above</i>
    /// itself and <c>0.4·sens</c> px below. See <c>MineObject.CheckProximity</c>.</para>
    ///
    /// <para><b>Zero means "never triggers on proximity"</b> — <c>Mine.control()</c> guards the whole
    /// scan with <c>this.sens &gt; 0</c> (<c>Mine.as:309</c>). <c>x37</c> is exactly that mine: it is
    /// armed, visible and inert until its radio detonator fires.</para>
    /// </summary>
    [Tooltip("Placed-mine proximity half-width in AS3 pixels (char.@sens). 0 = never proximity-triggers " +
             "(x37: radio-only). Mines only.")]
    public float sens = 100f;

        [Header("Melee Settings")]
        public MeleeType meleeType;          // mtip in AS3
        [Tooltip("Reach of the weapon in Flash pixels (dlina in AS3, phis@long). Club/sword swing radius or spear reach.")]
        public float meleeDlina = 50f;
        [Tooltip("Minimum reach for thrust/slash weapons in Flash pixels (mindlina in AS3, phis@minlong). Defaults to meleeDlina when the row omits minlong; only the Overhead sweep (mtip=2) and the Thrust lunge read it.")]
        public float meleeMinDlina = 50f;

        /// <summary>
        /// <c>&lt;phis massa&gt;</c> — the weapon's own mass attribute, read <b>only</b> by the melee
        /// spring's per-frame travel limit: <c>max(meleeRun, 1/massa)</c> px/frame, where AS3's
        /// <c>massa</c> is this value divided by 50 (<c>Weapon.as:715-721</c>), so the limit works out
        /// to <c>50 / massa</c>. A bat (<c>massa='1'</c>) gets 50 px/frame and is effectively
        /// unclamped; a hammer (<c>massa='15'</c>) gets 3.3, so <c>Pers.meleeRun</c> (10 at tier 0)
        /// wins instead.
        ///
        /// <para><b>0 means "the row authored no massa"</b> — a real state in the oracle, where
        /// <c>1/0 = Infinity</c> makes the step unclamped. Every one of the 30 melee rows in
        /// <c>AllData.as</c> does author it (values 1..15), so 0 only appears on an asset that has not
        /// been reimported since this field was added; <see cref="MeleeSpringMath.StepClampPx"/>
        /// reproduces the oracle's own behaviour for it rather than inventing a default.</para>
        /// </summary>
        [Tooltip("phis@massa — the weapon's own mass (1..15 on every melee row). Read ONLY by the " +
                 "melee spring's step limit, max(meleeRun, 50/massa) px per frame. 0 = row authored " +
                 "none, which in AS3 leaves the step unclamped. Melee only.")]
        public float meleeMassa;
        [Tooltip("Enables combo counter — every 4th hit deals 2× damage (combinat in AS3, char@combo OR !auto&&!powerfull).")]
        public bool meleeCombo;
        [Tooltip("Weapon can be charged by holding attack (powerfull in AS3, char@pow). The charge itself is not ported — see TOPIC_weapons_dispatch.")]
        public bool meleePowerAttack;

        [Header("Projectile — Type")]
        [Tooltip("Which prefab archetype to spawn. Set by importer from vbul/spring/flame/phisbul/navod.")]
        public ProjectileArchetype projectileArchetype;
        [Tooltip("Damage type carried by this projectile (tipdam in AS3 char node).")]
        public DamageType damageType;

        [Header("Projectile — Physics")]
        [Tooltip("Gravity multiplier on the bullet (phis.@grav). 0 = no gravity. Explosive/flame weapons use 1.")]
        public float bulletGravity;
        [Tooltip("Bullet thrust along the aim direction, in AS3 pixels/frame² (phis.@accel). " +
                 "Rockets use this. Stored RAW — Projectile applies the px/frame² -> units/s² " +
                 "conversion (x9), not the x0.3 velocity one.")]
        public float bulletAccel;
        [Tooltip("Flame type (phis.@flame). 0=none, 1=strong upward arc, 2=weak. Sets the upward " +
                 "lift acceleration (Weapon.as:1545/1551) and the bullet's lifetime. Lift ADDS to " +
                 "gravity, as in AS3; it does not replace it.")]
        public int bulletFlame;
        [Tooltip("Homing strength (phis.@navod). >0 spawns a SmartBullet that tracks nearest enemy.")]
        public float bulletNavod;
        [Tooltip("Uses physics bullet (vis.@phisbul). If true: Dynamic Rigidbody2D, real gravity, bounces/sticks.")]
        public bool isPhysBullet;

        /// <summary>
        /// Detonate on the first tile contact instead of bouncing — AS3 <c>PhisBullet.bumc</c>,
        /// set from <c>&lt;phis bumc&gt;</c> (<c>WThrow.as:60-63</c> → <c>:184</c>).
        ///
        /// <para><b>Not the same field as <see cref="isPhysBullet"/>, and reading one for the other was
        /// a real bug.</b> <c>phisbul</c> is a <c>&lt;vis&gt;</c> attribute that selects the
        /// <c>ProjectileArchetype</c> (see <c>DeriveArchetype</c>); <c>bumc</c> is a
        /// <c>&lt;phis&gt;</c> attribute that decides contact detonation. The port read
        /// <c>vis@phisbul</c> and used it as <c>bumc</c>, and the two sets are <b>disjoint</b>: the ten
        /// weapons carrying <c>phisbul</c> are all ranged projectiles, while the only two carrying
        /// <c>bumc</c> are the throwables <c>acidgr</c> and <c>molotov</c> — so neither ever detonated
        /// on contact.</para>
        ///
        /// <para>Consumed by <c>ThrownObjectPhysics.Step</c>'s <c>detonateOnContact</c> parameter and
        /// by <c>ThrownObject</c>'s trigger path.</para>
        /// </summary>
        [Tooltip("Detonate on first tile contact instead of bouncing (phis.@bumc). Throwables only — " +
                 "acidgr and molotov. NOT the same as isPhysBullet.")]
        public bool bumc;

        [Header("Projectile — Visual")]
        [Tooltip("Bullet visual class name from AllData.as (vis.@vbul). Empty = default ballistic round.")]
        public string vbul;
        [Tooltip("Per-weapon explosion-visual override (vis.@visexpl, Weapon.as:623-625). Empty = use the " +
                 "damage-type table. 'sparkle' is a MAGIC value (the standard blast plus sparkleexpl); any " +
                 "other value is emitted as a particle id.")]
        public string visExpl;
        [Tooltip("Imported projectile art definition matched from vbul or default ballistic rules.")]
        public ProjectileVisualDefinition projectileVisual;
        [Tooltip("Spring/visual stretch mode (vis.@spring). 1=velocity scale, 2=laser stretch, 3=multi-frame spread.")]
        public int springMode = 1;
        [Tooltip("Play animation on bullet vis each shot (vis.@bulanim). Used for sparks and flames.")]
        public bool bulletAnimated;
        [Tooltip("Eject shell casing particle on fire (vis.@shell).")]
        public bool hasShell;
        [Tooltip("Demask/light radius emitted when firing (vis.@shine). 500 = default.")]
        public int shineRadius = 500;

        [Header("Projectile — Impact")]
        [Tooltip("Decal type left on surfaces (vis.@tipdec + weapon.@tipdec).")]
        public DecalType decalType;
        [Tooltip("Tile/structure destruction amount per hit (char.@destroy).")]
        public float destroyTiles;
        [Tooltip("Flat armour-piercing POINTS (char.@pier), subtracted from the target's armour " +
                 "reduction. Not a probability — see penetration.")]
        public float piercing;

        [Tooltip("Penetration budget (dop.@probiv, plus the ammo's probiv when ammo is wired). " +
                 "A round with this > 0 does NOT stop on the unit it hits: it spends damage and " +
                 "carries on. 0 = an ordinary round, which stops.")]
        public float penetration;

        [Header("On-hit effect (dop node)")]
        [Tooltip("AS3 <dop effect> — the on-hit status this weapon applies (Weapon.as:691-694). " +
                 "Empty means no effect. Mapped to an effect id by the damage path, not directly.")]
        public string dopEffect;

        [Tooltip("AS3 <dop damage> — the payload amount for the on-hit effect (Weapon.as:695-698). " +
                 "Field default 0, so an absent attribute applies an effect with value 0.")]
        public float dopDamage;

        [Tooltip("AS3 <dop ch> — application chance (Weapon.as:699-702). DEFAULT 1 = always; an " +
                 "absent attribute is not 0. Values >= 1 are treated as CERTAIN, not rolled " +
                 "(Unit.as:3771: `dopCh >= 1 || Math.random() < dopCh`).")]
        public float dopChance = 1f;

        [Header("Magic Weapon")]
        [Tooltip("Requires Alicorn Amulet equipped to use (weapon.@alicorn).")]
        public bool alicornOnly;

        /// <summary>
        /// AS3 <c>weapon@spell</c> — true for the nine <b>supportive</b> magic items
        /// (<c>sp_slow</c>, <c>sp_mwall</c>, <c>sp_blast</c>, <c>sp_cryst</c>, <c>sp_kdash</c>,
        /// <c>sp_mshit</c>, <c>sp_moon</c>, <c>sp_gwall</c>, <c>sp_invulner</c>).
        ///
        /// <para><b>This is the flag that separates two unrelated things that both carry
        /// <c>tip == 5</c>.</b> <c>weaponType == WeaponType.Magic</c> covers both, so on its own it
        /// cannot tell them apart:</para>
        ///
        /// <list type="bullet">
        /// <item><description><b>Assault magic</b> (<c>spell == false</c>) — <c>WMagic</c> projectiles:
        /// fireball, eclipse, mray, dray, ice, lightning, dragon, udar, blades… These are held and
        /// fired, and pay mana per shot (<see cref="magicPoolCost"/> /
        /// <see cref="manaHealthCost"/>).</description></item>
        /// <item><description><b>Supportive magic</b> (<c>spell == true</c>) — a <c>Spell</c>, not a
        /// weapon at all. It has no <c>&lt;char&gt;</c> body, is never equipped, and is <i>cast from
        /// the inventory</i>: <c>UnitPlayer.as:3627-3635</c> intercepts the selection and calls
        /// <c>invent.useItem(id)</c> instead of switching to it.</description></item>
        /// </list>
        ///
        /// <para><b>Why it matters before the caster exists.</b> <see cref="WeaponControllerFactory"/>
        /// dispatches on <c>tip</c> alone, so without this flag the nine spell items route to
        /// <c>MagicWeaponController</c> and fire a real (if harmless) projectile — the wrong
        /// behaviour, and one a play-test could read as progress. Both the factory and
        /// <c>PlayerWeaponLoadout.Equip</c> now refuse them loudly.</para>
        ///
        /// <para>See docs/OnWeaponsSystemImplementation/14_MagicSystemAudit_2026-10-03.md §1 and §3.</para>
        /// </summary>
        [Tooltip("AS3 weapon@spell — a supportive spell, cast from the inventory, NOT a fired weapon. " +
                 "The nine sp_* items. Never equipped.")]
        public bool spell;

        [Header("Visuals (held sprite)")]
        [Tooltip("Imported weapon visual definition. Wired by WeaponGraphicsImportWindow.")]
        public WeaponVisualDefinition weaponVisual;

        [Tooltip("Override sprite symbol name (vis.@vweap). Empty = use 'vis' + weaponId. " +
                 "Set by importer when AllData.as has an explicit vweap attribute.")]
        public string visualOverrideId;

        [Tooltip("Muzzle flare effect id (vis.@flare). e.g. 'spark', 'plasma', 'laser'. Empty = none.")]
        public string muzzleFlareId;

        // Legacy string field kept for tools that wrote to it previously.
        [HideInInspector]
        public string weaponSprite;

        // Legacy properties for compatibility
        public string WeaponSprite => weaponSprite;

        [Header("Audio")]
        [Tooltip("Sound ID played when the weapon fires (e.g. 'rifle_s')")]
        public string soundShoot;
        [Tooltip("Sound ID played when reloading starts (e.g. 'rifle_r')")]
        public string soundReload;
        [Tooltip("Sound ID played on bullet impact (e.g. 'hit_metal'). Leave empty to use material-based hit sounds.")]
        public string soundHit;
        [Tooltip("Sound ID played during weapon prep / ready animation (e.g. charge-up)")]
        public string soundPrep;
        [Tooltip("Loop restart point within the prep sound file in milliseconds (snd.@t1). " +
                 "While firing, playback jumps back here when it approaches t2. " +
                 "Example: minigun_s t1=1030 = start of the firing loop section.")]
        public int soundPrepT1;
        [Tooltip("Spin-down start point within the prep sound file in milliseconds (snd.@t2). " +
                 "On trigger release, playback jumps here to play the wind-down tail. " +
                 "Example: minigun_s t2=3060 = end of firing loop / start of spin-down.")]
        public int soundPrepT2;
        [Tooltip("How far the shot sound travels in the world — used for AI noise awareness (snd.@noise). " +
                 "0 = this weapon is silent (the oracle's default: AS3 Weapon.noise = 0).")]
        public float noiseRadius;

        [Tooltip("Sound ID played when a THROWN object strikes a tile (snd.@fall). AS3 assigns it to the " +
                 "PhisBullet's sndHit (WThrow.as:196) and plays it on every tile bounce with volume " +
                 "|d|/10 (PhisBullet.as:244-337). Empty = the object lands silently. Note this is a " +
                 "thrown-weapon field: a gun's equivalent is soundHit.")]
        public string soundFall;

        [Tooltip("Sound ID played while a placed MINE counts down (snd.@sens) — the beep. AS3 " +
                 "Mine.control() plays it every 5 ticks while aiState == 2 (Mine.as:340-343). " +
                 "Mines only; empty for every other weapon.")]
        public string soundSens;

        // ── Deliberately absent from the <snd> block, and why ─────────────────────────────────────
        //
        // AS3 reads ELEVEN <snd> names, split across three parsers that never see each other's fields:
        //   Weapon.getSndParam (Weapon.as:641-680)  shoot shoot_n reload hit prep t1 t2 noise
        //   WThrow             (WThrow.as:64-66)    fall
        //   Mine               (Mine.as:112-119)    dem sens
        // Reading only the first parser is how a throwable's landing sound and a mine's beeps were
        // dropped without anything going red. The other two are absent for a *measured* reason:
        //
        //   shoot_n  — read at Weapon.as:1619 (`kol_shoot % sndShoot_n == 0`) and defaulted to 1 at
        //              :306. It occurs **zero** times in AllData.as, so no weapon can exercise it and
        //              the port's existing "play on every shot" is already correct for 100% of the
        //              data. A field here would be imported-but-unread.
        //   dem      — Mine.remine() (Mine.as:268-271) is reached from the re-mine skill check
        //              (`inter.successRemine`, :209). The port has the skill (`LockType.Mine` in
        //              SkillCheckSystem) but **no caller** — the mine-defuse interaction is unwired —
        //              so a field here could not change any behaviour yet. Add it with its caller.

        // Legacy properties for compatibility
        public string SoundShoot => soundShoot;
        public string SoundReload => soundReload;

        // ── The <com> block — trader / quality metadata ───────────────────────────────────────────────
        //
        // AS3 reads six attributes off the weapon's FIRST <com> child (Weapon.as:454-472). Until now the
        // port parsed none of them, which the "Still NOT IMPORTED" list below used to record. The block
        // is imported now because the unique-variant feature needs two of these and the rest are free
        // once the node is being read at all.

        /// <summary>
        /// AS3 <c>com@uniq</c> — <b>the chance to find this weapon's unique ("II") variant</b>, as the
        /// data file itself documents it: <i>"uniq — шанс найти уникальный вариант (если он есть)"</i>
        /// (<c>AllData.as:2089</c>). Read at <c>Weapon.as:464-467</c> into <c>Weapon.uniq</c>, and
        /// consumed by the loot generator (<c>LootGen.as:53</c>) to build the <c>arr["uniq"]</c> pool.
        ///
        /// <para><b>-1 is the absent value, and it is NOT the same as 0.</b> AS3 declares
        /// <c>public var uniq:Number = -1;</c> (<c>Weapon.as:100</c>) and assigns it only
        /// <c>if(node.com[0].@uniq.length())</c>, so a weapon with no <c>&lt;com&gt;</c> — or one whose
        /// <c>&lt;com&gt;</c> omits <c>uniq</c> — keeps <b>-1</b>. The distinction is load-bearing:
        /// <c>Weapon.updVariant</c> refuses outright when <c>this.uniq &lt; 0</c>
        /// (<c>Weapon.as:895-900</c>), so <b>-1 means "this weapon has no unique variant and cannot be
        /// upgraded"</b>, while <c>uniq='0'</c> means "it has one, it just never drops as random loot".
        /// Collapsing the two would let an upgrade land on a weapon that has no tier 1 to upgrade to.</para>
        ///
        /// <para>Measured on the shipped <c>AllData.as</c>: 120 weapons carry a <c>&lt;com&gt;</c> and
        /// <b>26</b> have <c>uniq &gt; 0</c> — <c>spear mach zknife zsword sledge p10mm p127mm lasp
        /// pp127mm assr carbine sparkl nova oldshot saf9 prism oldr winchester brushgun lasr pulser rail
        /// gatl gatp flamer glau</c>. The other 45 of the 71 variants are shop, quest, room or enemy
        /// loadouts only.</para>
        ///
        /// <para><b>Duplicated on purpose, and worth knowing.</b> The loot side already carries this
        /// number as <c>LootItemRow.UniqChance</c> (a <c>float?</c> whose <c>null</c> is the same "absent"
        /// the <c>-1</c> encodes here). Both are read from the same oracle attribute and both are
        /// correct; they answer different questions — the loot catalog asks "is there a pool entry", this
        /// asks "can this weapon be upgraded". Unifying them is a later refactor, not a bug to fix
        /// quietly here.</para>
        /// </summary>
        [Tooltip("AS3 com@uniq — drop weight of the unique variant. -1 = absent = this weapon has NO " +
                 "unique variant (AS3 refuses to upgrade it). 0 = has one, never random loot.")]
        public float uniq = -1f;

        /// <summary>
        /// AS3 <c>com@price</c> — the base trader price, before <c>sost</c>/condition multipliers
        /// (<c>Item.getPrice</c>, <c>Item.as:350-374</c>). <c>0</c> when the weapon has no <c>&lt;com&gt;</c>.
        ///
        /// <para>The variant's price rule hangs off this: <c>getPrice</c> uses <c>com[variant].@price</c>
        /// when that node exists and otherwise multiplies by <b>3</b>. No weapon in <c>AllData.as</c>
        /// carries a second <c>&lt;com&gt;</c> <i>without</i> a price, so in practice a unique costs 3× the
        /// base — but the node <b>does</b> repeat on 9 weapons, which is why the override is stored rather
        /// than assumed.</para>
        /// </summary>
        [Tooltip("AS3 com@price — base trader price. 0 = no <com> row.")]
        public float comPrice;

        /// <summary>AS3 <c>com@worth</c> — quality tier, the <c>worth</c> a loot entry carries
        /// (<c>LootGen.as:53</c>). <c>0</c> when absent.</summary>
        [Tooltip("AS3 com@worth — quality tier. 0 = absent.")]
        public float comWorth;

        /// <summary>AS3 <c>com@chance</c> — the chance this weapon appears in the random <c>weapon</c>
        /// loot pool at all (<c>LootGen.as:53</c>). Distinct from <see cref="uniq"/>, which weights the
        /// <i>variant</i> pool. <c>0</c> when absent.</summary>
        [Tooltip("AS3 com@chance — chance to appear in the weapon loot pool. 0 = absent.")]
        public float comChance;

        /// <summary>AS3 <c>com@stage</c> — the story stage gate on the loot entry
        /// (<c>LootGen.as:53</c>). <c>0</c> when absent.</summary>
        [Tooltip("AS3 com@stage — story-stage gate. 0 = absent.")]
        public int comStage;

        /// <summary>AS3 <c>com@rep</c> — repair efficiency with tools, i.e. how much of the weapon's
        /// durability a repair kit restores (<c>Weapon.as:456-459</c>). AS3 declares the field default as
        /// <c>rep_eff = 1</c> (<c>Weapon.as:122</c>), but assigns it only on presence, so an absent
        /// attribute keeps <b>1</b>; this field therefore defaults to 1 as well rather than 0.</summary>
        [Tooltip("AS3 com@rep — repair efficiency. Absent keeps AS3's default of 1.")]
        public float comRep = 1f;

        /// <summary>
        /// The weapon's unique ("II") variant — AS3's <c>Weapon.variant</c>, held as the index-1 override
        /// of the seven repeated child nodes.
        ///
        /// <para><b>Presence, not a bool.</b> <see cref="WeaponVariantData.Variant"/> is <c>0</c> when the
        /// block has no second <c>&lt;char&gt;</c> — the answer for 142 of the 213 weapons, and the state
        /// every pre-existing asset loads as. Do not read this as "has a variant"; read
        /// <see cref="WeaponVariantData.IsPresent"/>.</para>
        ///
        /// <para><b>Consuming this needs the merge, not a field copy.</b> The override is a
        /// <i>partial</i> delta: <c>oldshot</c> tier 1 raises damage 6→7 and hp 150→250 and says nothing
        /// about <c>kol</c> or <c>knock</c>, which stay 5 and 3. The port must therefore fold it over the
        /// base node's run — <c>WeaponVariantXml.MergeRuns</c> — and read the result, rather than reading
        /// the override's fields directly. Reading it directly turns every unmentioned stat into 0.</para>
        /// </summary>
        [Tooltip("The unique (\"II\") variant override. Variant == 0 means this weapon has no variant.")]
        public WeaponVariantData variant;

        #region IWeaponStats
        string IWeaponStats.weaponId => weaponId;
        WeaponType IWeaponStats.weaponType => weaponType;
        int IWeaponStats.punch => punch;
        bool IWeaponStats.IsUnarmed => IsUnarmed;
        int IWeaponStats.skillLevel => skillLevel;
        int IWeaponStats.weaponLevel => weaponLevel;
        float IWeaponStats.baseDamage => baseDamage;
        float IWeaponStats.rapid => rapid;
        int IWeaponStats.autoMode => autoMode;
        bool IWeaponStats.IsAuto => IsAuto;
        float IWeaponStats.precision => precision;
        float IWeaponStats.antiPrecision => antiPrecision;
        float IWeaponStats.deviation => deviation;
        float IWeaponStats.armorPenetration => armorPenetration;
        float IWeaponStats.knockback => knockback;
        float IWeaponStats.critChance => critChance;
        float IWeaponStats.critMultiplier => critMultiplier;
        int IWeaponStats.projectilesPerShot => projectilesPerShot;
        float IWeaponStats.projectileSpeed => projectileSpeed;
        int IWeaponStats.burstCount => burstCount;
        float IWeaponStats.explRadius => explRadius;
        float IWeaponStats.explosionDamage => explosionDamage;
        int IWeaponStats.explTip => explTip;
        int IWeaponStats.explKol => explKol;
        int IWeaponStats.magazineSize => magazineSize;
        float IWeaponStats.reloadTime => reloadTime;
        string IWeaponStats.ammoType => ammoType;
        int IWeaponStats.maxDurability => maxDurability;
        int IWeaponStats.prepFrames => prepFrames;
        int IWeaponStats.ammoPerShot => ammoPerShot;
        int IWeaponStats.rechargeFrames => rechargeFrames;
        int IWeaponStats.recoilFrames => recoilFrames;
        float IWeaponStats.recoilLift => recoilLift;
        float IWeaponStats.magicPoolCost => magicPoolCost;
        float IWeaponStats.manaHealthCost => manaHealthCost;
        int IWeaponStats.throwTip => throwTip;
        int IWeaponStats.fuseFrames => fuseFrames;
        bool IWeaponStats.radio => radio;
        float IWeaponStats.sens => sens;
        MeleeType IWeaponStats.meleeType => meleeType;
        float IWeaponStats.meleeDlina => meleeDlina;
        float IWeaponStats.meleeMinDlina => meleeMinDlina;
        float IWeaponStats.meleeMassa => meleeMassa;
        bool IWeaponStats.meleeCombo => meleeCombo;
        bool IWeaponStats.meleePowerAttack => meleePowerAttack;
        ProjectileArchetype IWeaponStats.projectileArchetype => projectileArchetype;
        DamageType IWeaponStats.damageType => damageType;
        float IWeaponStats.bulletGravity => bulletGravity;
        float IWeaponStats.bulletAccel => bulletAccel;
        int IWeaponStats.bulletFlame => bulletFlame;
        float IWeaponStats.bulletNavod => bulletNavod;
        bool IWeaponStats.isPhysBullet => isPhysBullet;
        bool IWeaponStats.bumc => bumc;
        string IWeaponStats.soundShoot => soundShoot;
        string IWeaponStats.soundReload => soundReload;
        string IWeaponStats.soundHit => soundHit;
        string IWeaponStats.soundPrep => soundPrep;
        int IWeaponStats.soundPrepT1 => soundPrepT1;
        int IWeaponStats.soundPrepT2 => soundPrepT2;
        float IWeaponStats.noiseRadius => noiseRadius;
        string IWeaponStats.soundFall => soundFall;
        string IWeaponStats.soundSens => soundSens;
        DecalType IWeaponStats.decalType => decalType;
        float IWeaponStats.destroyTiles => destroyTiles;
        float IWeaponStats.piercing => piercing;
        float IWeaponStats.penetration => penetration;
        string IWeaponStats.dopEffect => dopEffect;
        float IWeaponStats.dopDamage => dopDamage;
        float IWeaponStats.dopChance => dopChance;
        bool IWeaponStats.alicornOnly => alicornOnly;
        bool IWeaponStats.spell => spell;
        IWeaponVisualStats IWeaponStats.weaponVisual => weaponVisual;
        string IWeaponStats.muzzleFlareId => muzzleFlareId;
        int IWeaponStats.shineRadius => shineRadius;
        bool IWeaponStats.hasShell => hasShell;
        #endregion
    }
}
