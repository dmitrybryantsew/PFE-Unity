using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Core.Messages;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Effects;
using PFE.Systems.RPG.Data;
using PFE.Systems.Weapons;

namespace PFE.Systems.RPG
{
    /// <summary>
    /// Core RPG character stats system.
    /// Based on AS3 Pers.as, UnitPlayer.as, and AllData.as.
    /// 
    /// Bridges the RPG progression system with combat UnitStats:
    /// - 16 skills (13 regular capped at 20 + 3 post-game capped at 100)
    /// - 84 perks across 15 categories with prerequisites
    /// - Level-based progression (XP curve, skill points, perk points)
    /// - 5-zone internal organ health (Head, Torso, Legs, Blood, Mana) and trauma stages
    /// - Direct reactive bridge to UnitStats
    /// - Universal AS3 <sk> modifier evaluation via StatModifierApplier
    /// </summary>
    public class CharacterStats : MonoBehaviour, Data.ICharacterStats, IWeaponStatSource
    {
        [Header("Base Stats")]
        [SerializeField] private int level = 1;
        [SerializeField] private int xp = 0;
        [SerializeField] private int skillPoints = 0;
        [SerializeField] private int perkPoints = 0;
        [SerializeField] private int perkPointsExtra = 0;

        [Header("Skill Data")]
        [SerializeField] private LevelCurve levelCurve;

        [Header("Definitions")]
        [SerializeField] private SkillDefinitionDatabase skillDatabase;

        // All 18 skill IDs (16 authored in AllData.as + 2 special rewards)
        private static readonly string[] AllSkillIds = new string[]
        {
            "tele", "melee", "smallguns", "energy", "explosives", "magic",
            "repair", "medic", "lockpick", "science", "sneak", "barter", "survival",
            "attack", "defense", "knowl", "life", "spirit"
        };

        public static readonly int[] PostSkTab = new int[] { 5, 11, 18, 26, 35, 45, 56, 68, 82, 100 };

        // Skill levels: skillId -> points
        private readonly Dictionary<string, int> skillLevels = new Dictionary<string, int>();

        // Perk ranks: perkId -> rank
        private readonly Dictionary<string, int> perkRanks = new Dictionary<string, int>();

        // Weapon skills multipliers: weaponCategory -> multiplier (default 1.0)
        private readonly Dictionary<string, float> weaponSkills = new Dictionary<string, float>();

        // Damage resistances / vulnerabilities: damageTypeId -> multiplier (default 1.0)
        private readonly Dictionary<int, float> vulnerabilities = new Dictionary<int, float>();

        // UnitStats Bridge
        private UnitStats _unitStats;
        public UnitStats BoundUnitStats => _unitStats;

        [Header("Derived Vitals")]
        [SerializeField] private float maxHp = 100f;
        [SerializeField] private float inMaxHP = 200f;
        [SerializeField] private float inMaxMana = 400f;
        [SerializeField] private float organMaxHp = 200f;

        /// <summary>
        /// AS3 <c>Unit.maxmana</c> (<c>Unit.as:140</c>, default 1000) — the ceiling of the
        /// <b>magic budget</b>. Serialized fallback used only while no
        /// <see cref="UnitStats"/> is bound; otherwise the live value is
        /// <see cref="UnitStats.MaxMana"/>. See <see cref="MagicMana"/>.
        /// </summary>
        [SerializeField] private float inMaxMagic = 1000f;

        /// <summary>Fallback for <see cref="MagicMana"/> while no <see cref="UnitStats"/> is bound.</summary>
        [SerializeField] private float _magicMana = 1000f;

        // Body parts health (AS3: headHP, torsHP, legsHP, bloodHP, manaHP)
        [HideInInspector] public float headHp = 200f;
        [HideInInspector] public float torsHp = 200f;
        [HideInInspector] public float legsHp = 200f;
        [HideInInspector] public float bloodHp = 200f;
        [SerializeField] private float _manaHp = 400f;

        /// <summary>
        /// AS3 <c>Pers.manaHP</c> (<c>Pers.as:141</c>) — the <b>mana organ</b>, the fifth wound
        /// track alongside <see cref="headHp"/>/<see cref="torsHp"/>/<see cref="legsHp"/>/
        /// <see cref="bloodHp"/>. It is <i>not</i> a spendable pool: it has trauma stages
        /// (<see cref="manaSt"/>) and effectively no regeneration, so it recovers only through
        /// <see cref="HealOrgan"/>.
        ///
        /// <para><b>This used to be a live property over <c>UnitStats.Mana.Value</c>.</b> That
        /// aliasing was wrong: AS3 keeps <c>Pers.manaHP</c> (400) and <c>Unit.mana</c> (1000) as
        /// two separate values with different semantics, and a single pool cannot be both a
        /// regenerating budget and a non-regenerating wound track. See <see cref="MagicMana"/>
        /// for the other half. The setter is deliberately unclamped so that
        /// <see cref="ApplyManaDamage"/> can run AS3's exact floor-then-clamp sequence
        /// (<c>Pers.as:1822-1829</c>).</para>
        /// </summary>
        public float manaHp
        {
            get => _manaHp;
            set => _manaHp = value;
        }

        /// <summary>AS3 <c>Pers.inMaxMana</c> — the organ's ceiling (400), and the denominator of <see cref="manaSt"/>.</summary>
        public float MaxMana => inMaxMana;
        public float maxMana
        {
            get => inMaxMana;
            set => inMaxMana = value;
        }

        // === The magic budget: AS3 Unit.mana / Unit.maxmana ======================
        // Distinct from the organ above. This is the pool the HUD shows, the pool magic
        // spends, the pool teleport pays from, and the only one that regenerates.

        /// <summary>
        /// AS3 <c>Unit.mana</c> (<c>Unit.as:138</c>, default 1000) — the <b>magic budget</b>.
        /// Regenerates every tick via <see cref="TickMana"/>; pays for spellcasting
        /// (<c>WMagic.as:114</c>), teleport (<c>UnitPlayer.as:1712</c>) and the upkeep half of
        /// telekinesis/levitation (<c>UnitPlayer.as:1352</c>).
        /// </summary>
        public float MagicMana
        {
            get => _unitStats != null ? _unitStats.Mana.Value : _magicMana;
            set
            {
                _magicMana = value;
                if (_unitStats != null)
                {
                    _unitStats.Mana.Value = value;
                }
            }
        }

        /// <summary>AS3 <c>Unit.maxmana</c> — the budget's ceiling (1000 by default).</summary>
        public float MaxMagicMana
        {
            get => _unitStats != null ? _unitStats.MaxMana.Value : inMaxMagic;
            set
            {
                inMaxMagic = value;
                if (_unitStats != null)
                {
                    _unitStats.MaxMana.Value = value;
                }
            }
        }

        /// <summary>
        /// The budget as a 0..1 fraction. AS3's HUD shows exactly this: <c>GUI.setMana</c>
        /// (<c>GUI.as:993</c>) prints <c>round(gg.mana / 10) + "%"</c>, and its full-pool
        /// branch tests <c>mana &lt; 995</c> — both only make sense against a ceiling of 1000.
        /// </summary>
        public float MagicManaPercent => MaxMagicMana > 0f ? Mathf.Clamp01(MagicMana / MaxMagicMana) : 0f;

        /// <summary>
        /// AS3 <c>Unit.dmana</c> — the per-frame mana delta, carried across frames because
        /// <c>UnitPlayer.as:1346-1350</c> reads the <i>previous</i> frame's value to build the
        /// idle regen. See <see cref="TickMana"/>.
        ///
        /// <para><b>Initialised to 1, not 0.</b> <c>Unit.as:142</c> declares <c>dmana = 1</c> and
        /// nothing resets it at construction — <c>defaultParams()</c> does not touch it, so a fresh
        /// character's first idle tick computes <c>1 + recMana</c>. Because the carry compounds and
        /// is only cleared inside the upkeep branch, that head start survives forever, so matching it
        /// is free fidelity. It is also never reset by a stat recompute in AS3 — see
        /// <see cref="ResetToDefaults"/>, which does reset it, being the port's "fresh character".</para>
        /// </summary>
        private float _manaDelta = 1f;

        // Trauma Stages (0 = Healthy, 1 = Minor, 2 = Moderate, 3 = Severe, 4 = Crippled/Fatal)
        [HideInInspector] public int headSt = 0;
        [HideInInspector] public int torsSt = 0;
        [HideInInspector] public int legsSt = 0;
        [HideInInspector] public int bloodSt = 0;
        [HideInInspector] public int manaSt = 0;

        // Organ Minimums & Multipliers
        [HideInInspector] public float headMin = -1f;
        [HideInInspector] public float torsMin = -1f;
        [HideInInspector] public float legsMin = -1f;
        [HideInInspector] public float bloodMin = -1f;
        [HideInInspector] public float manaMin = 0f;
        [HideInInspector] public float organMult = 1.0f;
        [HideInInspector] public float organMultPot = 1.0f;
        [HideInInspector] public float radChild = 0f;

        /// <summary>
        /// AS3 <c>Pers.dieDamage</c> (<c>Pers.as:83</c>) — the fraction of max organ HP a
        /// forced-fatal hit deals. AS3 scales it by difficulty (0.15 / 0.25 / 0 on the easier
        /// settings, <c>:766-826</c>); 0.25 is the normal-difficulty default.
        /// </summary>
        [HideInInspector] public float dieDamage = 0.25f;

        // Combat Stats (AS3 Pers.as / Unit.as)
        [HideInInspector] public float allDamMult = 1.0f;
        [HideInInspector] public float allVulnerMult = 1.0f;
        [HideInInspector] public float critCh = 0.05f;
        [HideInInspector] public float critDamMult = 2.0f;

        /// <summary>
        /// AS3 <c>Unit.critInvis</c> (<c>Unit.as:322</c>, 0) — the <b>stealth-crit</b> probability,
        /// granted by the sneak skill (<c>AllData.as:5280</c>, <c>vd='0.05'</c> per level).
        ///
        /// <para>Read in <c>Unit.damage():3659-3666</c> as a <i>second</i>, independent crit roll that
        /// runs after the ordinary one, and only when the shooter is not the target's current
        /// look-at (<c>this.celUnit != param3.owner</c>) and the target is not a machine
        /// (<c>!this.doop</c>). It doubles the damage again (<c>param1 *= 2</c>) and contributes
        /// <c>+2</c> to the crit flag, so it stacks on top of a normal crit.</para>
        ///
        /// <para><b>Declared on <c>Unit</c>, not on <c>gg</c>.</b> No <c>defaultParams()</c> line
        /// resets it (<c>Pers.as:842-960</c> has none), so like <c>jammedMult</c> and <c>recyc</c> it
        /// keeps whatever the last <c>&lt;sk&gt;</c> left even after the perk is gone. This port
        /// reproduces that: <see cref="ResetToDefaults"/> does reset it to 0, which diverges from the
        /// oracle's non-reset, and is called out in that method's own note rather than silently
        /// "fixed" here.</para>
        /// </summary>
        [HideInInspector] public float critInvis = 0f;

        /// <summary>
        /// AS3 <c>Pers.desintegr</c> (<c>Pers.as:199</c>, 0) — the <b>disintegration</b> probability,
        /// granted by the level-15 <c>desintegr</c> perk (<c>AllData.as:5497-5500</c>,
        /// <c>v0='0' v1='0.05'</c>).
        ///
        /// <para>It is not a crit: it is an <b>overkill</b> proc. <c>Unit.damage():3671-3677</c> fires
        /// it only for <c>D_LASER</c>/<c>D_PLASMA</c>, only against a target whose current HP is at
        /// most ten times the damage about to be dealt (<c>this.hp &lt;= param1 * 10</c>), and then
        /// multiplies the damage by <b>12</b>. That threshold is what makes it a finisher rather than
        /// a damage buff — it can only ever turn an already-surviving-by-a-hair target into ash.</para>
        ///
        /// <para><b>Declared on <c>Pers</c> as a bare field, not on <c>gg</c></b> — which is why
        /// <c>setSkillParam</c> writes it through its generic <c>hasOwnProperty</c> branch rather than
        /// a dedicated one, and why nothing in <c>defaultParams()</c> resets it. AS3 copies it onto the
        /// weapon at <c>Weapon.setPers</c> (<c>Weapon.as:967-970</c>) and from there onto the bullet
        /// (<c>:1525-1527</c>, gated on the perk actually being held — <c>if(param2.desintegr &gt; 0)</c>
        /// only copies <i>upward</i>, so a weapon never loses a value it already had).</para>
        /// </summary>
        [HideInInspector] public float desintegr = 0f;
        [HideInInspector] public float dexter = 0.0f;
        [HideInInspector] public float dodgePlus = 0f;
        [HideInInspector] public float skin = 0.0f;
        [HideInInspector] public float meleeDamMult = 1.0f;
        [HideInInspector] public float meleeSpdMult = 1.0f;
        [HideInInspector] public float gunsDamMult = 1.0f;
        [HideInInspector] public float spellsDamMult = 1.0f;
        [HideInInspector] public float punchDamMult = 1.0f;

        // Mobility & Physics Stats
        [HideInInspector] public float allSpeedMult = 1.0f;
        [HideInInspector] public float allPrecMult = 1.0f;
        [HideInInspector] public float runSpeedMult = 2.0f;
        [HideInInspector] public float stamRun = 1.0f;
        [HideInInspector] public float stamRes = 2.0f;
        [HideInInspector] public float maxOd = 75f;

        /// <summary>
        /// AS3 <c>Pers.runPenalty</c> (<c>Pers.as:189</c>) — the accuracy hit for firing while
        /// running. Read at <c>UnitPlayer.as:1185-1187</c> as <c>precMult *= 1 - runPenalty</c>,
        /// applied only when the value is positive <b>and</b> the player is moving faster than
        /// 10 px/tick on either axis.
        ///
        /// <para><b>0.5 is the live default, and it comes from the declaration — not from a
        /// reset.</b> <c>defaultParams()</c> has <i>no</i> line for this field, so the declaration
        /// value survives every recompute. The only other value it can take is <b>0.25</b>, written
        /// by the <c>rungun</c> perk (<c>AllData.as:5516</c>, <c>v0='0.5' v1='0.25'</c>). Contrast
        /// <see cref="recMana"/>, where the declaration is the *unreachable* one; here it is the
        /// live one. See <c>TOPIC_lessons</c> on declaration-defaults.</para>
        /// </summary>
        [HideInInspector] public float runPenalty = 0.5f;

        /// <summary>
        /// AS3 <c>Pers.jumpPenalty</c> (<c>Pers.as:191</c>) — the accuracy hit applied whenever the
        /// player is <b>not</b> on the ground (<c>UnitPlayer.as:1189-1191</c>, gated on
        /// <c>!stay</c>). Note the sense: this is a penalty for *being airborne*, and it fires on
        /// every jump, not only while rising.
        ///
        /// <para>Default <b>0.3</b> from the declaration (no <c>defaultParams()</c> line); the
        /// <c>rungun</c> perk lowers it to <b>0.15</b> (<c>AllData.as:5517</c>).</para>
        /// </summary>
        [HideInInspector] public float jumpPenalty = 0.3f;

        /// <summary>
        /// AS3 <c>Pers.backPenalty</c> (<c>Pers.as:193</c>) — the accuracy hit for firing behind you:
        /// applied when the equipped weapon's facing differs from the body's
        /// (<c>UnitPlayer.as:1197-1199</c>, <c>currentWeapon.storona != storona</c>).
        ///
        /// <para>Default <b>0.4</b> from the declaration; the <c>composure</c> perk lowers it to
        /// <b>0.2</b> (<c>AllData.as:5525</c>).</para>
        /// </summary>
        [HideInInspector] public float backPenalty = 0.4f;

        /// <summary>
        /// AS3 <c>Pers.stayBonus</c> (<c>Pers.as:195</c>) — the accuracy <i>bonus</i> for standing
        /// still: applied as <c>precMult *= 1 + stayBonus</c> when the player is grounded and
        /// nearly stationary (<c>UnitPlayer.as:1193-1195</c>, <c>stay &amp;&amp; |dx| &lt; 1</c>).
        ///
        /// <para>Default <b>0.3</b> from the declaration; the <c>composure</c> perk <i>raises</i> it
        /// to <b>0.45</b> (<c>AllData.as:5526</c>) — the two perks move this and
        /// <see cref="backPenalty"/> in opposite directions, which is why neither may be folded into
        /// the other.</para>
        /// </summary>
        [HideInInspector] public float stayBonus = 0.3f;

        /// <summary>
        /// AS3 <c>Pers.mazilAdd</c> (<c>Pers.as:371</c>) — the "miss radius" bonus, in the same
        /// arbitrary units as <c>Sats</c>' spread model. <c>UnitPlayer.as:1182</c> copies it into
        /// <c>UnitPlayer.mazil</c> every tick; the only consumers are the weapon spread rolls
        /// (<c>Weapon.as:1460</c> for guns, <c>WThrow.as:139</c> for thrown), and <c>Sats.as:343</c>
        /// for the VATS preview.
        ///
        /// <para><b>This is a spread term, not a precision multiplier.</b> It is read here so the
        /// value is live and can be handed to whatever owns spread; the port's launch path does not
        /// yet roll a muzzle angle (see <c>Weapon.as:1460</c>) so nothing consumes it today. Do not
        /// fold it into <c>precMult</c> — that would be a different game.</para>
        ///
        /// <para>Default <b>0</b> (declaration and <c>defaultParams()</c> agree). Written by effects:
        /// <c>+5</c>/<c>+15</c> tiers (<c>AllData.as:5865</c>) and <c>+25</c>
        /// (<c>AllData.as:5965</c>).</para>
        /// </summary>
        [HideInInspector] public float mazilAdd = 0.0f;

        /// <summary>
        /// The locomotion inputs <see cref="ComputePrecisionMultiplier"/> needs, published by the
        /// movement layer each tick.
        ///
        /// <para><b>Why this is state on <c>CharacterStats</c> rather than a parameter on the shot.</b>
        /// AS3 reads these off the <i>player</i> (<c>UnitPlayer.control()</c>), not off the weapon, and
        /// the four terms are evaluated once per control tick — before the weapon is ever asked to
        /// fire. The port keeps the same ownership: the locomotion layer writes this struct, the RPG
        /// model composes the multiplier, and the weapon layer reads only the composed result. That
        /// way no weapon controller needs a reference to the movement system, which is the same
        /// separation <c>IWeaponStatSource</c> exists to preserve.</para>
        ///
        /// <para><b>Defaults describe a grounded, motionless player</b> — the state AS3's own
        /// <c>stay = true, dx = 0, dy = 0</c> starts in. That means an un-fed model still produces the
        /// oracle's standing-still multiplier (<c>allPrecMult * (1 + stayBonus)</c>) rather than a
        /// silently different one, and the <c>stayBonus</c> stays visible in the debug readout.</para>
        /// </summary>
        public readonly struct PrecisionState
        {
            /// <summary>AS3 <c>stay</c> — grounded, i.e. not mid-jump.</summary>
            public readonly bool IsGrounded;
            /// <summary>Horizontal speed in tiles/tick (AS3 <c>dx</c>) — the oracle's own unit.</summary>
            public readonly float Dx;
            /// <summary>Vertical speed in tiles/tick (AS3 <c>dy</c>).</summary>
            public readonly float Dy;
            /// <summary>AS3 <c>currentWeapon.storona != storona</c> — weapon faces away from the body.</summary>
            public readonly bool WeaponFacingDiffers;

            public PrecisionState(bool isGrounded, float dx, float dy, bool weaponFacingDiffers)
            {
                IsGrounded         = isGrounded;
                Dx                 = dx;
                Dy                 = dy;
                WeaponFacingDiffers = weaponFacingDiffers;
            }

            /// <summary>A grounded, motionless player facing the same way as the weapon.</summary>
            public static PrecisionState Standing => new PrecisionState(true, 0f, 0f, false);
        }

        private PrecisionState _precisionState = PrecisionState.Standing;

        /// <summary>
        /// The latest locomotion state, written by the movement layer. See <see cref="PrecisionState"/>.
        /// </summary>
        public PrecisionState CurrentPrecisionState
        {
            get => _precisionState;
            set => _precisionState = value;
        }

        /// <summary>
        /// The composed precision multiplier for the current locomotion state — AS3's
        /// <c>owner.precMult</c> at fire time. This is what a shot should carry into
        /// <c>DamageContext.PrecisionMultiplier</c>.
        /// </summary>
        public float PrecisionMultiplier => ComputePrecisionMultiplier(
            _precisionState.IsGrounded, _precisionState.Dx, _precisionState.Dy,
            _precisionState.WeaponFacingDiffers);

        /// <summary>
        /// AS3 <c>Pers.recMana</c> — the magic budget's per-tick regeneration, added to the
        /// carried <c>dmana</c> (see <see cref="TickMana"/>).
        ///
        /// <para><b>0.025, and the declaration lies.</b> <c>Pers.as:231</c> declares
        /// <c>recMana = 0.012</c>, but <c>defaultParams()</c> (<c>:855</c>) immediately overwrites it
        /// with <c>0.025</c>, and <c>setParameters()</c> (<c>:2164</c>) calls <c>defaultParams()</c> on
        /// every stat recompute. No <c>&lt;sk id='recMana'&gt;</c> exists anywhere in AllData, so
        /// <b>0.025 is the value the shipped game always uses</b> and 0.012 is unreachable.
        /// (<c>recManaMin</c>, by contrast, <i>is</i> written by three skills — 0.75/2/5.)</para>
        ///
        /// <para>This port was briefly set to 0.012 during the 2026-10-02 pool split, by reading the
        /// declaration and stopping there. That is the mirror of this project's usual failure: not a
        /// value that went stale, but a value that was never live. When porting a field, grep for what
        /// <i>writes</i> it as well as what reads it.</para>
        /// </summary>
        [HideInInspector] public float recMana = 0.025f;
        /// <summary>AS3 <c>Pers.recManaMin</c> (<c>Pers.as:229</c>, 0) — the regen floor.</summary>
        [HideInInspector] public float recManaMin = 0f;
        /// <summary>AS3 <c>Pers.shtrManaRes</c> (<c>Pers.as:367</c>, 1) — scales both the regen and its floor.</summary>
        [HideInInspector] public float shtrManaRes = 1f;
        /// <summary>
        /// AS3 <c>Pers.manaHPRes</c> (<c>Pers.as:151</c>, 0.1) — the per-frame top-up of the mana
        /// ORGAN, applied only while <c>manaHP &lt; manaMin</c> (<c>UnitPlayer.as:1357-1359</c>).
        /// With the default <c>manaMin</c> of 0 this never fires, which is why the organ
        /// effectively does not regenerate.
        /// </summary>
        [HideInInspector] public float manaHPRes = 0.1f;
        /// <summary>AS3 <c>Pers.portMana</c> (<c>Pers.as:437</c>, 25) — teleport's cost, scaled by <see cref="allDManaMult"/>.</summary>
        [HideInInspector] public float portMana = 25f;
        /// <summary>AS3 <c>Pers.teleMana</c> (<c>Pers.as:87</c>, 1) — scales how much the upkeep wounds the ORGAN.</summary>
        [HideInInspector] public float teleMana = 1f;
        /// <summary>AS3 <c>Pers.teleManaMult</c> (<c>Pers.as:215</c>, 0.04) — the organ-wound multiplier for upkeep.</summary>
        [HideInInspector] public float teleManaMult = 0.04f;
        /// <summary>AS3 <c>Pers.alicornRunMana</c> (<c>Pers.as:469</c>, 5) — the alicorn flight-run drain on the budget.</summary>
        [HideInInspector] public float alicornRunMana = 5f;

        [HideInInspector] public int isDJ = 0;
        [HideInInspector] public int levitOn = 0;
        [HideInInspector] public float levitDMana = 5.0f;
        [HideInInspector] public float levitDManaUp = 20.0f;
        [HideInInspector] public int portPoss = 0;
        [HideInInspector] public int spellsPoss = 1;

        // Skill & Interaction Stats
        [HideInInspector] public int lockPick = 0;
        [HideInInspector] public int possLockPick = 0;
        [HideInInspector] public int lockPickTime = 30;
        /// <summary>
        /// AS3 <c>Pers.unlockMaster</c> (<c>Pers.as:281</c>, 0) — the effective lockpick level at
        /// which a physical lock opens outright. Read by <c>Pers.getLockMaster(1)</c>
        /// (<c>Pers.as:2356</c>); compared against a container's <c>lockLevel</c> in
        /// <c>GUI.as:1379</c> and <c>UnitPlayer.as:1975</c>. Granted by perks (<c>ref='add'</c>,
        /// <c>AllData.as:6034</c>). Unlike <c>lockPick</c>, it ignores the raw skill.
        /// </summary>
        [HideInInspector] public int unlockMaster = 0;
        /// <summary>
        /// AS3 <c>Pers.lockAtt</c> (<c>Pers.as:287</c>, <b>1</b>) — a multiplier on the progress
        /// dealt per lockpicking attempt (<c>Interact.as:1083</c>). <b>Drawback-shaped:</b> higher
        /// deals more lock damage (i.e. fails faster). Granted by the lockpick skill
        /// (<c>AllData.as:5588</c> is <c>pinBreak</c>; see that field for the sibling shape).
        /// </summary>
        [HideInInspector] public float lockAtt = 1f;
        /// <summary>
        /// AS3 <c>Pers.pinBreak</c> (<c>Pers.as:289</c>, <b>1</b>) — probability that a failed
        /// attempt snaps the bobby pin instead of merely failing (<c>Interact.as:1073</c>:
        /// <c>pinBreak &gt;= 1 || random() &lt; pinBreak</c> — so <b>1 means always break</b>).
        /// Default 1 is therefore "always breaks"; the lockpick skill lowers it
        /// (<c>AllData.as:5588</c>, <c>v0='1' v1='0.5'</c>).
        /// </summary>
        [HideInInspector] public float pinBreak = 1f;
        [HideInInspector] public int hacker = 0;
        [HideInInspector] public int hackerMaster = 0;
        /// <summary>
        /// AS3 <c>Pers.hackAtt</c> (<c>Pers.as:299</c>, <b>3</b>) — the number of attempts a
        /// terminal grants before it locks out (<c>Interact.as:1114</c> seeds the container's own
        /// <c>lockAtt</c> from this). Granted by the hacker skill (<c>AllData.as:5608</c>,
        /// <c>v0='3' v1='5'</c>).
        /// </summary>
        [HideInInspector] public int hackAtt = 3;
        [HideInInspector] public int repair = 0;
        [HideInInspector] public float repairMult = 1.0f;
        [HideInInspector] public int remine = 0;
        [HideInInspector] public int signal = 0;
        [HideInInspector] public int barterLvl = 0;
        /// <summary>
        /// AS3 <c>Pers.limitBuys</c> (<c>Pers.as:317</c>, <b>1</b>) — multiplies a vendor's
        /// per-item restock cap (<c>Vendor.as:304</c>: <c>lim = ceil(buy.@n * limitBuys)</c>).
        /// The declaration default is 1 and <c>defaultParams()</c> does <b>not</b> reset it, so 1
        /// is the live value; the port previously declared 0, which made every cap
        /// <c>ceil(n * 0) = 0</c> and stopped all restocking.
        /// </summary>
        [HideInInspector] public float limitBuys = 1f;
        /// <summary>
        /// The shared destination for AS3 <c>Pers.capsMult</c> (<c>Pers.as:283</c>, 1, a loot
        /// multiplier — <c>LootGen.as:247</c>) <b>and</b> <c>Pers.barterMult</c>
        /// (<c>Pers.as:313</c>, 1, the vendor price multiplier — <c>PipBuck.as:414</c>). Both
        /// statIds land in this one field (see <c>ApplyNamedStat</c>).
        /// </summary>
        [HideInInspector] public float capsMult = 1.0f;
        [HideInInspector] public float healMult = 0.4f;
        [HideInInspector] public float bonusHeal = 0f;
        [HideInInspector] public float reanimHp = 0f;
        [HideInInspector] public float regenFew = 0f;
        [HideInInspector] public float regenMax = 0f;
        [HideInInspector] public float stealthMult = 1.0f;
        [HideInInspector] public float noiseRun = 0f;
        [HideInInspector] public float sneak = 0f;
        [HideInInspector] public float sneakLurk = 0f;
        [HideInInspector] public int eco = 0;
        [HideInInspector] public float kickDestroy = 30f;
        [HideInInspector] public float tormoz = 1.0f;
        [HideInInspector] public float knocked = 1.0f;
        [HideInInspector] public float reloadMult = 1.0f;
        [HideInInspector] public float recoilMult = 1.0f;

        /// <summary>
        /// AS3 <c>Pers.jammedMult</c> (<c>Pers.as:251</c>, 1) — scales the jam and misfire
        /// probabilities (<c>Weapon.as:1429</c>). It is a <b>drawback</b> multiplier: higher means
        /// more jams. Read by <see cref="PFE.Systems.Weapons.IWeaponStatSource"/>.
        /// </summary>
        [HideInInspector] public float jammedMult = 1.0f;

        /// <summary>
        /// AS3 <c>Pers.recyc</c> (<c>Pers.as:201</c>, <b>0</b>) — the chance that firing does not
        /// consume a round, and only for <c>batt</c>/<c>energ</c>/<c>crystal</c> ammo
        /// (<c>Weapon.as:1586</c>). Defaults to 0, i.e. never recycle.
        /// </summary>
        [HideInInspector] public float recyc = 0f;

        /// <summary>
        /// AS3 <c>Pers.meleeRun</c> (<c>Pers.as:175</c>, <b>10</b>) — the melee levitation-run
        /// distance, quadrupled while in the sky (<c>WClub.as:249-252</c>). The melee skill raises it
        /// by 3 per tier (<c>AllData.as:5227</c>: <c>v0='10' vd='3'</c>).
        ///
        /// <para><b>Not reset by <c>defaultParams()</c>.</b> Like <c>jammedMult</c> and
        /// <c>recyc</c>, <c>Pers.as:820-960</c> contains no assignment to it, so a removed melee
        /// skill leaves a stale value behind — that asymmetry is the oracle, and
        /// <see cref="ResetToDefaults"/> reproduces it.</para>
        /// </summary>
        [HideInInspector] public float meleeRun = 10f;
        [HideInInspector] public float jumpdy = 12f;
        [HideInInspector] public float djumpdy = 6f;
        [HideInInspector] public int infravis = 0;
        [HideInInspector] public float lastCh = 0f;
        [HideInInspector] public int freel = 0;
        [HideInInspector] public int dropTre = 0;
        /// <summary>
        /// AS3 <c>Pers.upChance</c> (<c>Pers.as:291</c>, 0) — when &gt; 0 the lockpicking roll uses
        /// the better <c>chanceUnlock2</c> table instead of <c>chanceUnlock</c>
        /// (<c>Interact.as:1256</c>). Granted by a skill (<c>AllData.as:4193</c>). Reset to 0 by
        /// <c>defaultParams()</c> (<c>Pers.as:941</c>).
        /// </summary>
        [HideInInspector] public float upChance = 0f;
        [HideInInspector] public int ableFly = 0;
        [HideInInspector] public float potShad = 0f;

        // Monster/Target Damage Bonuses
        [HideInInspector] public float pipEmpVulner = 3.0f;
        [HideInInspector] public float damPony = 1.0f;
        [HideInInspector] public float damZombie = 1.0f;
        [HideInInspector] public float damRobot = 1.0f;
        [HideInInspector] public float damInsect = 1.0f;
        [HideInInspector] public float damMonster = 1.0f;
        [HideInInspector] public float damAlicorn = 1.0f;

        // Companion Stats
        [HideInInspector] public float petHP = 50f;
        [HideInInspector] public float petDam = 2.0f;
        [HideInInspector] public float petRes = 30f;
        [HideInInspector] public float petSkin = 0f;
        [HideInInspector] public float petVulner = 1.0f;
        [HideInInspector] public float owlHP = 200f;
        [HideInInspector] public float owlDam = 3.0f;
        [HideInInspector] public float owlSkin = 5.0f;
        [HideInInspector] public float owlVulner = 1.0f;

        // Ammo/Inventory limits
        [HideInInspector] public float maxmW = 7f;
        [HideInInspector] public float maxmM = 3f;
        [HideInInspector] public float maxm1 = 40f;
        [HideInInspector] public float maxm2 = 1000f;
        [HideInInspector] public float maxm3 = 500f;

        // Telekinesis Stats (AS3: Pers.as & AllData.as)
        [HideInInspector] public float maxTeleMassa = 0.6f;
        [HideInInspector] public float teleDist = 360000f;
        [HideInInspector] public float telePorog = 0.1f;
        [HideInInspector] public float teleMult = 2.2f;
        [HideInInspector] public float throwForce = 0.0f;
        [HideInInspector] public float throwDmagic = 200f;
        [HideInInspector] public float throwDmanaMult = 0.05f;
        [HideInInspector] public float throwDmana = 250f;
        [HideInInspector] public float allDManaMult = 1.0f;
        [HideInInspector] public int telemaster = 0;
        [HideInInspector] public float warlockDManaMult = 1.0f;

        // Factor tracking for UI
        [System.Serializable]
        public class StatFactor
        {
            public string sourceId;   // e.g., "oak", "medic"
            public string sourceType; // "skill", "perk", "weap", "stat", "min"
            public float value;
            public float result;      // Final stat value after this factor
        }
        private readonly Dictionary<string, List<StatFactor>> statFactors = new Dictionary<string, List<StatFactor>>(StringComparer.OrdinalIgnoreCase);

        // Events
        public event Action<int> onLevelUp;
        public event Action<string, int> onSkillChanged;
        public event Action<string, int> onPerkAdded;
        public event Action<int, int> onTraumaChanged; // bodyPart (1..5), stage (0..4)
        public event Action<int> onXpAdded;
        public event Action onStatsRecalculated;

        /// <summary>
        /// Raised when an organ reaches zero — AS3's <c>Pers.die()</c> call sites inside
        /// <c>damage()</c>, <c>bloodDamage()</c> and <c>manaDamage()</c>.
        ///
        /// <para>Organ trauma used to be unable to kill: the code clamped the organ to 1 and
        /// returned, so the only death path was <c>UnitStats.CurrentHp</c> reaching 0. AS3 has
        /// both, and the head is the fastest way to die in the oracle (<c>Pers.as:1693-1697</c>).</para>
        /// </summary>
        public event Action onDeath;

        // MessagePipe Publishers (optional / injected)
#pragma warning disable CS0649
        [VContainer.Inject] private MessagePipe.IPublisher<PFE.Core.Messages.XpGainedMessage> _xpPublisher;
        [VContainer.Inject] private MessagePipe.IPublisher<PFE.Core.Messages.LevelUpMessage> _levelUpPublisher;
        [VContainer.Inject] private MessagePipe.IPublisher<PFE.Core.Messages.SkillLevelChangedMessage> _skillPublisher;
        [VContainer.Inject] private MessagePipe.IPublisher<PFE.Core.Messages.PerkAddedMessage> _perkPublisher;
        [VContainer.Inject] private MessagePipe.IPublisher<PFE.Core.Messages.TraumaChangedMessage> _traumaPublisher;
#pragma warning restore CS0649

        private void NotifyTraumaChanged(int bodyPart, int stage)
        {
            onTraumaChanged?.Invoke(bodyPart, stage);
            _traumaPublisher?.Publish(new PFE.Core.Messages.TraumaChangedMessage
            {
                BodyPart = bodyPart,
                Stage = stage
            });
        }

        // Public properties
        public int Level => level;
        public int Xp => xp;
        public int SkillPoints => skillPoints;
        public int PerkPoints => perkPoints;
        public int PerkPointsExtra => perkPointsExtra;
        public float MaxHp => maxHp;
        public float InMaxHP => inMaxHP;
        public float OrganMaxHp => organMaxHp;
        public float AllDamMult => allDamMult;
        public float AllVulnerMult => allVulnerMult;
        public float MaxTeleMassa { get => maxTeleMassa; set => maxTeleMassa = value; }
        public float TeleDist { get => teleDist; set => teleDist = value; }
        public float TelePorog { get => telePorog; set => telePorog = value; }
        public float TeleMult { get => teleMult; set => teleMult = value; }
        public float ThrowForce { get => throwForce; set => throwForce = value; }
        public float ThrowDmagic { get => throwDmagic; set => throwDmagic = value; }
        public float ThrowDmanaMult { get => throwDmanaMult; set => throwDmanaMult = value; }
        public float AllDManaMult { get => allDManaMult; set => allDManaMult = value; }
        public int Telemaster { get => telemaster; set => telemaster = value; }

        // ── Debug-facing views ────────────────────────────────────────────────
        // The console's `rpg` verb has to answer "is the modifier engine actually running, and from
        // what data?" — which means seeing the *resolved assets* and the live skill/perk tables, not
        // just the derived numbers. Without these the only observable is a final stat value, and a
        // wrong value cannot be told apart from an engine that never ran.

        /// <summary>
        /// The modifier database resolved by <see cref="EnsureResources"/>. <b>Null means
        /// <see cref="RecalculateStats"/> is on the fallback path</b> (<see cref="ApplySkillEffectsFallback"/>),
        /// i.e. the 258 imported AllData.as modifiers are not in use at all.
        /// </summary>
        public SkillDefinitionDatabase SkillDatabase => skillDatabase;

        /// <summary>XP curve resolved by <see cref="EnsureResources"/>; null falls back to defaults.</summary>
        public LevelCurve Curve => levelCurve;

        /// <summary>Live skill table (skillId → points). Read-only view for debug tooling.</summary>
        public IReadOnlyDictionary<string, int> SkillLevels => skillLevels;

        /// <summary>Live perk table (perkId → rank). Read-only view for debug tooling.</summary>
        public IReadOnlyDictionary<string, int> PerkRanks => perkRanks;

        private void Awake()
        {
            EnsureResources();
        }

        // ── Deterministic damage RNG ──────────────────────────────────────────
        //
        // ApplyOrganDamage's 20/40/40 head/torso/legs split and ApplyBloodDamage's bleed roll are the
        // only two places in the RPG path that draw a random number. Both used UnityEngine.Random,
        // which is a process-global, unseeded generator — so the same hit resolved differently on
        // every run, and two peers could never agree on an outcome. Every other random draw in the
        // simulation already goes through IRngService (registered in GameLifetimeScope, seeded from
        // PfeDebugSettings.RngSeedOverride, and already bound into Lua's math.random).
        //
        // Shared static, not per-instance, and that is deliberate: one ordered stream means the
        // sequence is a function of the order damage events occur, which is what makes a replay
        // reproducible. A per-instance stream seeded identically would hand every unit the SAME
        // sequence — correlated rolls, which is worse than either option.
        //
        // The lazy fallback mirrors RoomPopulator.GetSpawnRng: PcgRngService's default constructor
        // takes a fixed seed, so an un-wired scene (a test, a bare scene) is still deterministic
        // rather than merely un-random. GameLifetimeScope pushes the real seeded instance over it.
        private static IRngService s_combatRng;

        /// <summary>
        /// The shared deterministic combat RNG. Set from the container at boot
        /// (<c>GameLifetimeScope</c>); falls back to a fresh <see cref="PcgRngService"/> combat
        /// stream so a scene with no container is still reproducible.
        /// </summary>
        public static IRngService CombatRng
        {
            get => s_combatRng ??= new PcgRngService().GetStream(RngStream.Combat);
            set => s_combatRng = value;
        }

        private void EnsureResources()
        {
            if (levelCurve == null)
            {
                levelCurve = Resources.Load<LevelCurve>("LevelCurve");
            }
            if (skillDatabase == null)
            {
                skillDatabase = Resources.Load<SkillDefinitionDatabase>("SkillDefinitionDatabase");
            }
        }

        /// <summary>
        /// Bind a UnitStats instance to this RPG character block.
        /// Synchronizes HP, Mana, and derived combat properties.
        /// </summary>
        public void BindUnitStats(UnitStats stats)
        {
            _unitStats = stats;
            if (_unitStats != null)
            {
                _unitStats.MaxHp.Value = maxHp;
                _unitStats.CurrentHp.Value = maxHp;
                // The budget pool is seeded from its OWN ceiling, not from the organ. The two
                // used to be the same number because manaHp aliased UnitStats.Mana; AS3 keeps
                // Unit.mana (1000) and Pers.manaHP (400) separate, so seeding MaxMana from
                // inMaxMana would silently re-merge them.
                _unitStats.MaxMana.Value = inMaxMagic;
                _unitStats.Mana.Value = _magicMana;
            }

            // AS3's Pers constructor fills `skills` with every authored id at 0, so
            // setParameters() always evaluates each skill's level-0 modifier. Without this the
            // skills loop in RecalculateStats iterates an empty dictionary and the entire
            // modifier engine is inert — which is what happened in production, because
            // Initialize() (the only other seeder) had no non-test caller.
            SeedSkillIds();

            // The effect host. AS3's player stack is Pers + Unit: the effect set lives on the Unit
            // (`Unit.effects`) but the param replay goes through `Pers.setParameters()`
            // (`Pers.as:2152-2205`), which is this class. So bind here, at the same moment the two
            // halves are joined, and route the <sk> writes into RecalculateStats rather than into the
            // unit's own fields.
            AttachEffectHost();
        }

        /// <summary>
        /// Wire the bound unit's effect system to this class — the port of the <c>Pers</c> half of
        /// AS3's effect stack.
        ///
        /// <para><b>Three separate seams, and all three are needed.</b> <see cref="UnitStats.EffectResetSink"/>
        /// is the reset half of <c>Pers.setParameters</c> (the port's <c>RecalculateStats</c>) — without
        /// it the <c>&lt;sk&gt;</c> replay below compounds on every pass;
        /// <see cref="UnitStats.EffectParamSink"/> carries the <c>&lt;sk&gt;</c> writes, which for the
        /// player belong on this block and not on the unit; <see cref="UnitStats.EffectPayloadSink"/>
        /// carries the payload extension that only exists for the player (the organ heals in
        /// <c>Effect.as:401-403</c>). Without the first, a player effect would write the unit's fields
        /// and be invisible to every RPG stat; without the second, a hydra effect would heal only the HP
        /// bar and never the organs.</para>
        ///
        /// <para>Also switches the unit to <see cref="PersMode.Player"/>, which is the oracle's own
        /// split — <c>Pers.setParameters</c> replays with <c>eff.lvl</c> and <b>skips</b> effects that
        /// are being removed, while <c>Unit.setEffParams</c> replays with a hardcoded 1/0 index and
        /// includes them. Getting the mode wrong would either lose the NPC reset or apply a value the
        /// player path never applies.</para>
        /// </summary>
        private void AttachEffectHost()
        {
            if (_unitStats == null)
            {
                return;
            }

            // The reset half. RecalculateStats is the port's Pers.setParameters: reset -> level ->
            // skills -> perks -> trauma -> sync. It deliberately does NOT replay effects; that is the
            // pass in UnitStats.RunEffectParamPass, which calls this first and then replays. Wiring it
            // to ResetToDefaults alone would be wrong — AS3's defaultParams() is step 2 of eight, not
            // the whole thing.
            _unitStats.EffectResetSink = RecalculateStats;
            _unitStats.EffectParamSink = ApplyEffectParam;
            _unitStats.EffectPayloadSink = ApplyEffectPayload;
        }

        /// <summary>
        /// The <c>Pers.setSkillParam</c> half of an effect's <c>&lt;sk&gt;</c> write
        /// (<c>Pers.as:1468-1573</c>), routed through the same universal sink the skills and perks use.
        ///
        /// <para><b>Why <see cref="ApplyNamedStat"/> and not a switch of its own.</b> AS3's
        /// <c>Pers.setSkillParam</c> is the <i>same function</i> for a skill, a perk, an item and an
        /// effect — only the XML node differs. Duplicating its switch here would be a second
        /// implementation that could drift, which is precisely the "one formula, two copies" shape this
        /// project keeps finding. So the effect path goes through the one sink.</para>
        ///
        /// <para>The <c>index</c> is the oracle's per-level vector index, already resolved by
        /// <see cref="ActiveEffectSet.EnumerateParams"/>; it is passed to
        /// <see cref="EffectParam.ValueForLevel(int)"/> and never recomputed.</para>
        /// </summary>
        private void ApplyEffectParam(EffectParam param, int index, string effectId)
        {
            float value = param.ValueForLevel(index);

            // AS3's `tip == "res"` writes `vulner[id] -= v` on the UNIT's table (Pers.as:1509-1517 and
            // Unit.as:3443 both do this), NOT through the tip='res' dictionary the skills use — the
            // dictionary is the *baseline* builder, and an effect's deduction is transient and must be
            // undone by the replay. So the effect's resistance write goes straight onto the unit's live
            // table, which is what the reset-then-replay pass rebuilds.
            if (param.IsResistance)
            {
                if (int.TryParse(param.id, out int resIndex) &&
                    System.Enum.IsDefined(typeof(DamageType), resIndex))
                {
                    VulnerabilityData live = _unitStats.Vulnerabilities;
                    var dt = (DamageType)resIndex;
                    live.SetVulnerability(dt, live.GetVulnerability(dt) - value);
                    _unitStats.OverrideVulnerabilityBaseline(live);
                    TrackFactor($"eff_res[{resIndex}]", effectId ?? "effect", "effect", value,
                        live.GetVulnerability(dt));
                }
                else
                {
                    _unitStats.Effects.RecordUnmappedName("res:" + param.id);
                }
                return;
            }

            // Everything else is the universal named-stat write, tagged so a factor search can separate
            // an effect's contribution from a skill's.
            ApplyNamedStat(param.id, "", RefTypeFor(param.op), value,
                effectId ?? "effect", "effect");
        }

        /// <summary>
        /// The payload extension the player alone needs — AS3 <c>Effect.as:398-408</c>.
        ///
        /// <para>Hydra heals the HP bar through the unit path (already done by
        /// <c>EffectPayloads.Run</c>) and then, for the player, three more pools: the two organ heals
        /// <c>pers.heal(val, 4)</c>/<c>(val, 5)</c> and the blood. The index vocabulary is
        /// <c>Pers.heal</c>'s own (1=hp, 2..5 = the organ tracks).</para>
        /// </summary>
        private void ApplyEffectPayload(ActiveEffect effect)
        {
            if (effect == null)
            {
                return;
            }

            if (effect.Id == "hydra")
            {
                HealOrgan(effect.Value, organType: 4);
                HealOrgan(effect.Value, organType: 5);
            }
        }

        /// <summary>
        /// AS3's <c>&lt;sk ref&gt;</c> to this class's <c>refType</c> vocabulary — <c>add</c>,
        /// <c>mult</c>, or a plain assign. The default is the dangerous one and is preserved:
        /// <see cref="EffectParamRef.Assign"/> means <i>replace</i>, not "no operation".
        /// </summary>
        private static string RefTypeFor(EffectParamRef op)
        {
            switch (op)
            {
                case EffectParamRef.Add: return "add";
                case EffectParamRef.Mult: return "mult";
                default: return "set";
            }
        }

        /// <summary>
        /// Ensure every authored skill id has an entry, defaulting to 0. Idempotent, and it never
        /// lowers an existing value, so a save loaded later is not clobbered.
        ///
        /// <para>This is what makes the port match AS3's <c>Pers.skills</c>, which is populated at
        /// construction rather than on first use.</para>
        /// </summary>
        public void SeedSkillIds()
        {
            foreach (string skillId in AllSkillIds)
            {
                if (!skillLevels.ContainsKey(skillId))
                    skillLevels[skillId] = 0;
            }
        }

        /// <summary>
        /// Initialize character stats with a level curve.
        /// </summary>
        public void Initialize(LevelCurve curve)
        {
            levelCurve = curve;
            EnsureResources();

            SeedSkillIds();

            maxHp = levelCurve != null ? levelCurve.BaseHp : 100f;
            inMaxHP = levelCurve != null ? levelCurve.BaseOrganHp : 200f;
            inMaxMana = 400f;
            organMaxHp = inMaxHP;

            headHp = inMaxHP;
            torsHp = inMaxHP;
            legsHp = inMaxHP;
            bloodHp = inMaxHP;
            _manaHp = inMaxMana;

            // The budget pool starts full (AS3 Pers.as:540 does this for manaHP, and
            // Unit.as:138-140 initialises Unit.mana to maxmana).
            // dmana starts at 1, its AS3 declaration value (Unit.as:142) -- see _manaDelta.
            _manaDelta = 1f;
            MagicMana = inMaxMagic;

            RecalculateStats();
        }

        /// <summary>
        /// Add XP and check for level up (AS3 Pers.as:961 expa).
        /// Spawns floating +Xxp text if world position is provided.
        /// </summary>
        public void AddXp(int amount, float worldX = float.NegativeInfinity, float worldY = float.NegativeInfinity)
        {
            if (amount <= 0) return;

            xp += amount;
            onXpAdded?.Invoke(amount);

            Vector3 spawnPos = transform.position;
            if (!float.IsNegativeInfinity(worldX) && !float.IsNegativeInfinity(worldY))
            {
                spawnPos = new Vector3(worldX, worldY, transform.position.z);
            }

            // Spawn floating +Nxp text via DamageEventFeed (AS3 Pers.as:980 numbEmit("+Nxp"))
            DamageEventFeed.Default.ReportText(spawnPos + Vector3.up * 0.4f, $"+{amount}xp", new Color(1f, 0.95f, 0.4f));

            _xpPublisher?.Publish(new PFE.Core.Messages.XpGainedMessage
            {
                Amount = amount,
                TotalXp = xp,
                Position = spawnPos
            });

            EnsureResources();
            int xpForNextLevel = levelCurve != null ? levelCurve.GetXpForLevel(level) : 5000 * level * (level + 1) / 2;
            if (xp >= xpForNextLevel && level < 100)
            {
                UpLevel();
            }
        }

        /// <summary>
        /// Handle level up logic (AS3 Pers.as:1123 upLevel).
        /// </summary>
        public void UpLevel()
        {
            level++;
            EnsureResources();

            int skillPointsGained = levelCurve != null ? levelCurve.SkillPointsPerLevel : 5;
            skillPoints += skillPointsGained;
            perkPoints++;

            onLevelUp?.Invoke(level);
            _levelUpPublisher?.Publish(new PFE.Core.Messages.LevelUpMessage
            {
                NewLevel = level,
                SkillPointsGained = skillPointsGained,
                PerkPointsGained = 1
            });
            RecalculateStats();
        }

        /// <summary>
        /// Add points to a skill (AS3 Pers.as:1191 addSkill).
        /// </summary>
        public bool AddSkill(string skillId, int points, bool spendPoints = false)
        {
            if (points <= 0) return false;

            int currentLevel = GetSkillLevel(skillId);
            int maxLevel = IsSkillPost(skillId) ? 100 : 20;

            int pointsCanAdd = Mathf.Min(points, maxLevel - currentLevel);
            if (pointsCanAdd <= 0) return false;

            if (spendPoints)
            {
                if (skillPoints < pointsCanAdd) return false;
                skillPoints -= pointsCanAdd;
            }

            skillLevels[skillId] = currentLevel + pointsCanAdd;
            onSkillChanged?.Invoke(skillId, skillLevels[skillId]);
            _skillPublisher?.Publish(new PFE.Core.Messages.SkillLevelChangedMessage
            {
                SkillId = skillId,
                NewLevel = skillLevels[skillId]
            });

            // Check for knowl perk point unlock
            if (skillId == "knowl")
            {
                int tier = GetPostSkillTier(skillLevels[skillId]);
                if (tier > perkPointsExtra)
                {
                    int bonusPerks = tier - perkPointsExtra;
                    perkPoints += bonusPerks;
                    perkPointsExtra = tier;
                }
            }

            RecalculateStats();
            return true;
        }

        /// <summary>
        /// Add skill point (spending from pool).
        /// </summary>
        public bool AddSkillPoint(string skillId, int points = 1)
        {
            return AddSkill(skillId, points, spendPoints: true);
        }

        public int GetSkillLevel(string skillId)
        {
            return skillLevels.TryGetValue(skillId, out int lvl) ? lvl : 0;
        }

        public int GetSkillTier(string skillId)
        {
            int lvl = GetSkillLevel(skillId);
            return IsSkillPost(skillId) ? GetPostSkillTier(lvl) : CalculateSkillTier(lvl);
        }

        public static bool IsSkillPost(string skillId)
        {
            return skillId == "attack" || skillId == "defense" || skillId == "knowl";
        }

        public int CalculateSkillTier(int skillLevel)
        {
            return ComputeSkillTier(skillLevel);
        }

        public static int ComputeSkillTier(int skillLevel)
        {
            if (skillLevel >= 20) return 5;
            if (skillLevel >= 14) return 4;
            if (skillLevel >= 9) return 3;
            if (skillLevel >= 5) return 2;
            if (skillLevel >= 2) return 1;
            return 0;
        }

        public static int GetPostSkillTier(int points)
        {
            if (points < PostSkTab[0]) return 0;
            int tier = 0;
            for (int i = 0; i < PostSkTab.Length; i++)
            {
                if (points >= PostSkTab[i])
                    tier = i + 1;
            }
            return tier;
        }

        /// <summary>
        /// Add a perk by ID (AS3 Pers.as:1300 addPerk).
        /// </summary>
        public bool AddPerk(string perkId)
        {
            if (perkPoints <= 0) return false;

            EnsureResources();
            Data.PerkDefinition perkDef = skillDatabase != null ? skillDatabase.GetPerk(perkId) : null;
            if (perkDef != null)
            {
                int currentRank = GetPerkRank(perkId);
                if (!perkDef.CanUnlock(this, currentRank))
                    return false;
            }

            int oldRank = GetPerkRank(perkId);
            perkRanks[perkId] = oldRank + 1;
            perkPoints--;

            onPerkAdded?.Invoke(perkId, perkRanks[perkId]);
            _perkPublisher?.Publish(new PFE.Core.Messages.PerkAddedMessage
            {
                PerkId = perkId,
                Rank = perkRanks[perkId]
            });
            RecalculateStats();
            return true;
        }

        public bool AddPerk(Data.PerkDefinition perk)
        {
            if (perk == null) return false;
            return AddPerk(perk.PerkId);
        }

        public int GetPerkRank(string perkId)
        {
            return perkRanks.TryGetValue(perkId, out int rank) ? rank : 0;
        }

        public IReadOnlyList<string> GetAllSkillIds() => AllSkillIds;

        public string[] GetAllPerkIds()
        {
            var set = new HashSet<string>(perkRanks.Keys);
            if (skillDatabase != null)
            {
                foreach (var perk in skillDatabase.GetAllPerks())
                {
                    if (perk != null && !string.IsNullOrEmpty(perk.PerkId))
                        set.Add(perk.PerkId);
                }
            }
            var result = new string[set.Count];
            set.CopyTo(result);
            return result;
        }

        public int GetSkillTierForWeapon(int skillCode)
        {
            switch (skillCode)
            {
                case 1: return GetSkillTier("melee");
                case 2: return GetSkillTier("smallguns");
                case 3: return GetSkillTier("repair");
                case 4: return GetSkillTier("energy");
                case 5: return GetSkillTier("explosives");
                case 6: return GetSkillTier("magic");
                case 7: return GetSkillTier("tele");
                default: return 5;
            }
        }

        public float GetWeaponSkillMultiplier(string weaponType)
        {
            return weaponSkills.TryGetValue(weaponType, out float mult) ? mult : 1.0f;
        }

        public void SetWeaponSkillMultiplier(string weaponType, float val)
        {
            weaponSkills[weaponType] = val;
        }

        public int GetWeaponSkillTier(string weaponType)
        {
            return GetSkillTier(weaponType);
        }

        public void SetSkillLevel(string skillId, int level)
        {
            int maxLevel = IsSkillPost(skillId) ? 100 : 20;
            skillLevels[skillId] = Mathf.Clamp(level, 0, maxLevel);
            onSkillChanged?.Invoke(skillId, skillLevels[skillId]);
            _skillPublisher?.Publish(new PFE.Core.Messages.SkillLevelChangedMessage
            {
                SkillId = skillId,
                NewLevel = skillLevels[skillId]
            });
            if (skillId == "knowl")
            {
                int tier = GetPostSkillTier(skillLevels[skillId]);
                if (tier > perkPointsExtra)
                {
                    int bonusPerks = tier - perkPointsExtra;
                    perkPoints += bonusPerks;
                    perkPointsExtra = tier;
                }
            }
            RecalculateStats();
        }

        public void SetPerkRank(string perkId, int rank)
        {
            if (string.IsNullOrEmpty(perkId)) return;
            if (rank <= 0)
            {
                perkRanks.Remove(perkId);
            }
            else
            {
                perkRanks[perkId] = rank;
                onPerkAdded?.Invoke(perkId, rank);
                _perkPublisher?.Publish(new PFE.Core.Messages.PerkAddedMessage
                {
                    PerkId = perkId,
                    Rank = rank
                });
            }
            RecalculateStats();
        }

        public void RemovePerk(string perkId)
        {
            SetPerkRank(perkId, 0);
        }

        public void ClearAllPerks()
        {
            perkRanks.Clear();
            RecalculateStats();
        }

        public void SetLevel(int newLevel)
        {
            level = Mathf.Clamp(newLevel, 1, 100);
            RecalculateStats();
            onLevelUp?.Invoke(level);
        }

        public void SetSkillPoints(int points) => skillPoints = Mathf.Max(0, points);
        public void SetPerkPoints(int points) => perkPoints = Mathf.Max(0, points);

        public void GrantSkillPoints(int amount) => skillPoints += amount;
        public void GrantPerkPoints(int amount) => perkPoints += amount;

        /// <summary>
        /// Reset all derived stats to default values (AS3 Pers.as:842 defaultParams).
        ///
        /// <para><b>Only what the oracle resets.</b> AS3's <c>setParameters()</c> (<c>Pers.as:2154</c>)
        /// calls <c>defaultParams()</c> and then re-applies every skill and perk, so every field the
        /// oracle's <c>defaultParams()</c> touches is safe to re-zero here — it is re-earned by the
        /// <c>&lt;sk&gt;</c>/<c>&lt;p&gt;</c> passes that follow. A field it does <i>not</i> touch is a
        /// <b>declaration default that must survive every recompute</b> (that is exactly what
        /// <c>defaultParams()</c> means for a field it omits). Resetting one of those silently deletes
        /// the value on the first recompute — the failure <c>VendorInventoryTests</c> and
        /// <c>PlayerTelekinesisControllerTests</c> pin.
        /// </para>
        ///
        /// <para>Audited 2026-10-03 with <c>resetdiff.py</c>, which extracts both bodies and diffs
        /// them: the port was writing <b>38</b> fields the oracle never assigns, including
        /// <c>barterLvl</c>/<c>limitBuys</c>/<c>capsMult</c> (<c>Pers.as:283,315,317</c>),
        /// <c>unlockMaster</c>/<c>lockAtt</c>/<c>pinBreak</c>/<c>hackAtt</c> (<c>:281,287,289,299</c>)
        /// and <c>repairMult</c>/<c>remine</c>/<c>signal</c>. The oracle's own text is explicit about
        /// the opposite for several of them: <c>lockAtt</c>/<c>pinBreak</c>/<c>hackAtt</c> are
        /// "raised by <i>spent XP</i>" (<c>:286-299</c>), which all three reset sites destroyed.</para>
        /// </summary>
        public void ResetToDefaults()
        {
            EnsureResources();

            maxHp = levelCurve != null ? levelCurve.BaseHp : 100f;
            inMaxHP = levelCurve != null ? levelCurve.BaseOrganHp : 200f;
            inMaxMana = 400f;
            organMaxHp = inMaxHP;

            allDamMult = 1.0f;
            allVulnerMult = 1.0f;
            critCh = 0.05f;
            critDamMult = 2.0f;
            // critInvis / isDJ / portPoss / shtrManaRes are reset here even though the oracle's
            // defaultParams() does not name them, and that is deliberate: each is a *modifier target*
            // (a `case` in ApplyNamedStat, driven by a <sk>/<p> modifier or the isDJ perk at
            // ApplyPerkEffectsFallback). Resetting them IS the base-before-re-earn pattern this method
            // exists for — the <sk>/<p> passes that follow put the earned value back. `resetdiff.py`
            // lists them as "not in the oracle" but they are the benign class: a field with a writer
            // downstream, unlike lockAtt/pinBreak/barterLvl/etc., which had none.
            critInvis = 0f;
            dexter = 0.0f;
            dodgePlus = 0f;
            skin = 0.0f;
            meleeDamMult = 1.0f;
            meleeSpdMult = 1.0f;
            gunsDamMult = 1.0f;
            spellsDamMult = 1.0f;
            punchDamMult = 1.0f;
            // AS3 defaultParams() resets reloadMult and recoilMult to 1 (Pers.as:913-914) on every
            // recompute. They were missing here, so a removed perk left a stale multiplier behind.
            reloadMult = 1.0f;
            recoilMult = 1.0f;
            // jammedMult, recyc, meleeRun, drotMult and explRadMult are deliberately NOT reset here,
            // because defaultParams() does not reset them either — the grep over Pers.as:820-960
            // finds no assignment to any of the five. They therefore keep whatever the last <sk>
            // left, even after the perk that granted it is gone. That looks like an oversight in
            // AS3, but it is the oracle: reproducing it is what keeps a perk-removal recompute
            // identical. (defaultParams() line 853 does set kickDestroy = 30, so that one IS reset;
            // see below.)
            //
            // desintegr joins that "not reset" group for the same reason and by the same grep: it is
            // a bare Pers field (Pers.as:199), not a gg.* param, and defaultParams() never assigns
            // it. So a build that takes the perk once keeps its chance for the session.
            //
            // critInvis above IS reset, and that is a deliberate, recorded divergence: AS3 has no
            // defaultParams() line for it either (it is declared on Unit, Unit.as:322). Resetting it
            // is the safer direction — it cannot leave a stealth-crit chance behind after the sneak
            // perk is dropped — but it is not the oracle, so do not "fix" desintegr to match it.
            allSpeedMult = 1.0f;
            allPrecMult = 1.0f;
            runSpeedMult = 2.0f;
            stamRun = 1.0f;
            stamRes = 2.0f;
            maxOd = 75f;
            // AS3 defaultParams() DOES assign mazilAdd = 0 (Pers.as:928) — so it is reset here.
            mazilAdd = 0f;
            // runPenalty / jumpPenalty / backPenalty / stayBonus are DELIBERATELY NOT reset:
            // AS3's defaultParams() has no line for any of them, so their declaration values
            // (0.5 / 0.3 / 0.4 / 0.3) survive every stat recompute, and only the rungun/composure
            // perks ever move them. Resetting them here would silently delete the live defaults on
            // the first recompute. They join the same "not in defaultParams()" group as
            // desintegr/jammedMult/recyc/meleeRun/drotMult/explRadMult — see the note above.
            recMana = 0.025f;
            recManaMin = 0f;
            shtrManaRes = 1f;
            // AS3 defaultParams() DOES assign kickDestroy = 30 (Pers.as:853) — unlike the five
            // multipliers above. It was missing here, so a removed kick-destroy perk left an
            // inflated value behind and a bare kick kept smashing tiles.
            kickDestroy = 30f;
            manaMin = 0f;
            isDJ = 0;
            levitOn = 0;
            levitDMana = 5.0f;
            levitDManaUp = 20.0f;
            portPoss = 0;
            spellsPoss = 1;
            lockPick = 0;
            possLockPick = 0;
            // ── NOT reset here: the oracle's defaultParams() assigns none of these ──────────
            //
            // Every name below was assigned here and is now deliberately left alone. AS3's
            // defaultParams() (Pers.as:842-957) contains no assignment to it, so for each of these the
            // *declaration* value is the default and must survive every recompute — re-zeroing it
            // deletes whatever raised it, which is precisely what several tests pin.
            //
            //   manaHPRes / portMana / teleManaMult / teleDist / telePorog / teleMult
            //   throwForce / throwDmagic / throwDmanaMult / throwDmana / inMaxMagic / sneakLurk
            //   unlockMaster / lockAtt / pinBreak                  (AS3: "raised by spent XP")
            //   hackerMaster / hackAtt / repair
            //   repairMult / remine / signal
            //   barterLvl / limitBuys / capsMult
            //   healMult / bonusHeal / reanimHp / regenFew / regenMax / eco
            //   stealthMult / noiseRun / sneak / maxTeleMassa
            //
            // Three deserve a specific note:
            //
            //  * lockAtt / pinBreak / hackAtt — AS3's own comment (Pers.as:286-299) says these are
            //    "raised by XP you spend". This block destroyed all three on the next recompute, so
            //    every lock attempt read the declaration default instead of what was bought. That is
            //    the failure LockAttemptSystemTests reports, where the whole verb answers "Missed".
            //
            //  * shtrManaRes — the oracle DOES reset it, but in `invMassParam()` (:2084), not in
            //    defaultParams(). The port has no invMassParam at all — maxSpeed/accelMult/speedShtr/
            //    jumpMult/noStairs do not exist in it — so the constant it folded in here happened to
            //    produce the right value. It is left out anyway, because a reset belonging to a
            //    different oracle function is exactly the kind of near-miss that reads as correct.
            //    Recorded divergence: `invMassParam()` is unported, so its mass-driven speedShtr and
            //    shtrManaRes penalties never apply.
            //
            //  * maxTeleMassa — was reset to 0.6 here, but AS3 declares 1 and derives the smaller
            //    value in the telekinesis/perk path. Left at whatever that path set.
            //
            // Verified with .workbuddy-ai/tools/agentverify/wall/resetdiff.py, which diffs this body
            // against Pers.defaultParams(): it reported 38 such fields before this change.
            allDManaMult = 1.0f;
            telemaster = 0;
            warlockDManaMult = 1.0f;
            // AS3 Pers.as:941 (defaultParams) also resets freel/dropTre/modAnalis/modTarget/modMetal/
            // upChance/ableFly/socks/potShad here. Only upChance is carried over for now because it
            // has a live consumer (LockAttemptSystem); the rest are still on the divergence list and
            // adding resets with no reader would be inert (see TOPIC_lessons #29).
            upChance = 0f;

            weaponSkills.Clear();
            vulnerabilities.Clear();
            statFactors.Clear();

            // restoreFractionOfHealth is deliberately NOT done here — see RecalculateStats, which
            // restores the fractions AFTER the skill/perk passes, exactly where AS3 does it
            // (setParameters:2186-2190). Doing it here would be undone by those passes.
        }

        /// <summary>
        /// Restore the health pools to the fractions captured before the recompute. AS3
        /// <c>setParameters():2186-2190</c> — <c>manaHP = procMana * inMaxMana</c> and the same for
        /// head/torso/legs/blood. Kept separate from <see cref="ResetToDefaults"/> because the oracle
        /// runs it <i>after</i> the <c>&lt;sk&gt;</c>/<c>&lt;p&gt;</c> passes: those can change
        /// <c>inMaxMana</c>, and the point of the fraction is to survive that change.
        /// </summary>
        private void RestoreHealthFractions(float procHp, float procHead, float procTors,
            float procLegs, float procBlood, float procMana)
        {
            headHp = procHead * inMaxHP;
            torsHp = procTors * inMaxHP;
            legsHp = procLegs * inMaxHP;
            bloodHp = procBlood * inMaxHP;
            manaHp = procMana * inMaxMana;
        }

        /// <summary>
        /// Recalculate all derived stats from skills, perks, and level (AS3 Pers.as:2152 setParameters).
        /// </summary>
        public void RecalculateStats()
        {
            EnsureResources();

            // 1. Calculate proportional healths to preserve fractions across level/stat shifts
            float procHp = (_unitStats != null && _unitStats.MaxHp.Value > 0) ? _unitStats.CurrentHp.Value / _unitStats.MaxHp.Value : 1f;
            float procHead = inMaxHP > 0 ? headHp / inMaxHP : 1f;
            float procTors = inMaxHP > 0 ? torsHp / inMaxHP : 1f;
            float procLegs = inMaxHP > 0 ? legsHp / inMaxHP : 1f;
            float procBlood = inMaxHP > 0 ? bloodHp / inMaxHP : 1f;
            float procMana = inMaxMana > 0 ? manaHp / inMaxMana : 1f;

            // 2. Reset defaults
            ResetToDefaults();

            // 3. Level scaling (AS3 Pers.as:2165-2166)
            int hpPerLevel = levelCurve != null ? levelCurve.HpPerLevel : 15;
            int organHpPerLevel = levelCurve != null ? levelCurve.OrganHpPerLevel : 40;
            maxHp += (level - 1) * hpPerLevel;
            inMaxHP += (level - 1) * organHpPerLevel;
            organMaxHp = inMaxHP;

            // 4. Apply all skills (AS3 Pers.as:2167-2180)
            foreach (var kvp in skillLevels)
            {
                string skillId = kvp.Key;
                int points = kvp.Value;
                int tier = IsSkillPost(skillId) ? GetPostSkillTier(points) : CalculateSkillTier(points);

                // attack / defense / survival / sneak used to be special-cased here, and each
                // replacement diverged from the oracle:
                //   attack    added points*0.05 — AS3's <sk> is v0+vd against the post TIER (0..10),
                //             so points (0..100) made it up to 10x too strong;
                //   defense   compounded *0.97 per point — AS3 multiplies ONCE by (1-0.03*tier);
                //   survival  used points for skin (AS3 uses the tier) and compounded
                //             allVulnerMult (AS3 is linear, 1-0.01*points);
                //   sneak     invented critCh and dexter, which AS3's sneak skill never touches.
                // The imported AllData.as modifiers are now the only source, which is what makes
                // "universal <sk> engine" true rather than aspirational.
                var skillDef = skillDatabase != null ? skillDatabase.GetSkill(skillId) : null;
                if (skillDef != null && skillDef.modifiers != null && skillDef.modifiers.Length > 0)
                {
                    foreach (var mod in skillDef.modifiers)
                    {
                        StatModifierApplier.Apply(this, mod, tier, points, skillId);
                    }
                }
                else
                {
                    ApplySkillEffectsFallback(skillId, points, tier);
                }
            }

            // 5. Apply all perks (AS3 Pers.as:2181-2185)
            foreach (var kvp in perkRanks)
            {
                string perkId = kvp.Key;
                int rank = kvp.Value;
                if (rank <= 0) continue;

                var perkDef = skillDatabase != null ? skillDatabase.GetPerk(perkId) : null;
                if (perkDef != null && perkDef.modifiers != null && perkDef.modifiers.Length > 0)
                {
                    foreach (var mod in perkDef.modifiers)
                    {
                        StatModifierApplier.Apply(this, mod, rank, rank, perkId, "perk");
                    }
                }
                else
                {
                    ApplyPerkEffectsFallback(perkId, rank);
                }
            }

            // 6. Restore organ and vital healths (AS3 Pers.as:2186-2190)
            RestoreHealthFractions(procHp, procHead, procTors, procLegs, procBlood, procMana);

            // 7. Apply limb trauma effects (AS3 Pers.as:2193 traumaParameters)
            ApplyTraumaModifiers();

            // 8. Push to UnitStats bridge
            if (_unitStats != null)
            {
                _unitStats.CurrentHp.Value = Mathf.Clamp(maxHp * procHp, 0, maxHp);
                // The magic budget is deliberately NOT written here. AS3's setParameters
                // (Pers.as:2152+) preserves the ORGAN's fraction across a max change and never
                // touches Unit.mana. This line used to copy the organ value into the budget,
                // which is one of the ways the two pools stayed merged.
                SyncUnitStats();
            }

            onStatsRecalculated?.Invoke();
        }

        /// <summary>
        /// Synchronizes current CharacterStats directly to bound UnitStats.
        /// </summary>
        public void SyncUnitStats()
        {
            if (_unitStats == null) return;
            _unitStats.MaxHp.Value = maxHp;
            // inMaxMagic, not inMaxMana: this is Unit.maxmana (the 1000 budget), and
            // inMaxMana is Pers.inMaxMana (the 400 organ). Writing the organ's ceiling here
            // would cap the budget at 400 and re-merge the two pools.
            _unitStats.MaxMana.Value = inMaxMagic;
            _unitStats.critChanceBonus = critCh;
            _unitStats.critDamageBonus = critDamMult - 2.0f;
            // The two attacker-side crit channels that reach the hit resolver through DamageContext.
            // AS3 copies both out of Pers onto the weapon/bullet (Weapon.as:1697 for critInvis,
            // :967-970 and :1525-1527 for desintegr); the port carries them on UnitStats and stamps
            // them onto the context at fire time, which is the same mechanism-only divergence the
            // IWeaponStatSource multipliers use.
            //
            // critDamMult is stored as a bonus (+0.0 for 2x) on UnitStats, but critInvis and desintegr
            // are raw probabilities and pass through unscaled.
            _unitStats.critInvisChance = critInvis;
            _unitStats.desintegrChance = desintegr;
            _unitStats.damageMultiplier = allDamMult;
            _unitStats.skinResistance = skin;
            _unitStats.dexterity = dexter;
            _unitStats.dodge = dodgePlus;

            // AS3 folds the skill/perk `tip='res'` deductions into gg.vulner, which is the table
            // Unit.damage() multiplies every hit by (Unit.as:3529). UnitStats owns that table and
            // rebuilds it from a baseline plus armour resists, so the baseline is the correct seam.
            //
            // The previous behaviour wrote a local dictionary that nothing ever read, so all 30
            // authored resistance deductions (oak, reinforced, wild, stonewall, antifire, babah,
            // diel, envir, durable, …) were inert.
            _unitStats.SetVulnerabilityBaseline(BuildVulnerabilityBaseline());
        }

        // ── IWeaponStatSource ────────────────────────────────────────────────────────────────
        // AS3 copies most of these out of Pers onto the weapon instance in Weapon.setParams
        // (Weapon.as:973-977, then the melee override WClub.as:204-205) and the weapon then reads
        // its own copy. The port instead lets the weapon hold a reference to the live stat source,
        // so a mid-fight perk change is picked up without rebuilding the weapon — a deliberate,
        // documented divergence in *mechanism* only; the values and the sites that consume them are
        // identical.
        //
        // Implemented explicitly so the multipliers do not widen CharacterStats' public surface
        // (the fields themselves are already public, with the AS3 lowercase names).
        float IWeaponStatSource.ReloadMult   => reloadMult;
        float IWeaponStatSource.RecoilMult   => recoilMult;
        float IWeaponStatSource.JammedMult   => jammedMult;
        float IWeaponStatSource.Recyc        => recyc;
        float IWeaponStatSource.MeleeDamMult => meleeDamMult;
        float IWeaponStatSource.MeleeSpdMult => meleeSpdMult;
        float IWeaponStatSource.PunchDamMult => punchDamMult;
        float IWeaponStatSource.KickDestroy  => kickDestroy;
        float IWeaponStatSource.MeleeRun     => meleeRun;

        // The two attacker-side hit procs. Unlike the multipliers above, AS3 does not copy these
        // into setParams — it stamps them on the bullet at fire time (Weapon.as:1697 for critInvis,
        // :1525-1527 for desintegr). They are on this interface anyway so the controllers have one
        // place to read every Pers value a shot needs; see IWeaponStatSource.
        float IWeaponStatSource.CritInvis    => critInvis;
        float IWeaponStatSource.Desintegr    => desintegr;

        // The precision channel. These five are read by the *player* rather than copied onto the
        // weapon: AS3's UnitPlayer.control() rebuilds precMult from them every tick and stamps the
        // composed value on the bullet (UnitPlayer.as:1181-1200 -> Weapon.as:1634). See
        // IWeaponStatSource for the full chain, including why mazilAdd is NOT in precMult.
        float IWeaponStatSource.AllPrecMult  => allPrecMult;
        float IWeaponStatSource.RunPenalty   => runPenalty;
        float IWeaponStatSource.JumpPenalty  => jumpPenalty;
        float IWeaponStatSource.BackPenalty  => backPenalty;
        float IWeaponStatSource.StayBonus    => stayBonus;
        float IWeaponStatSource.MazilAdd     => mazilAdd;

        // The composed value: the five raw fields above through the four locomotion terms of
        // UnitPlayer.as:1181-1200. `PrecisionMultiplier` is the same property the player's own
        // composition publishes, so the weapon side and the debug view can never disagree.
        float IWeaponStatSource.PrecisionMultiplier => PrecisionMultiplier;

        /// <summary>
        /// AS3 <c>UnitPlayer.control()</c>'s precision block, verbatim
        /// (<c>UnitPlayer.as:1181-1200</c>) — the <b>situational</b> half of <c>precMult</c>.
        ///
        /// <para>This is the value AS3 stamps on the bullet through
        /// <c>Weapon.resultPrec(owner.precMult, …)</c> (<c>Weapon.as:1634</c>, <c>:1531</c>). It is
        /// recomputed <b>every control tick</b>, not on equip, so a shot fired mid-jump or while
        /// backpedalling carries a different multiplier from one fired standing still — which is the
        /// whole point of the four terms.</para>
        ///
        /// <para><b>The gate.</b> AS3 wraps all four in <c>if(sats.que.length == 0 &amp;&amp; !lurked)</c>
        /// — no VATS queue and not lurking. The port has no VATS and no lurked state, so both terms
        /// are permanently falsy and the gate is <b>always open</b>. That is the oracle's own
        /// behaviour for a player who is neither aiming in VATS nor lurking, so it is written as a
        /// comment rather than as a field that would always be true (see the triage note in
        /// <c>docs/RPG_Step4_Open_Questions_2026-10-02.md</c>).</para>
        /// </summary>
        /// <param name="isGrounded">
        /// AS3 <c>stay</c> — the player's grounded state, <b>not</b> "the player is standing still".
        /// The oracle's <c>stay</c> is set from the jump/ground check, and it is the same flag that
        /// gates the <c>jumpPenalty</c> (<c>!stay</c>) and the <c>stayBonus</c> (<c>stay</c>).
        /// </param>
        /// <param name="dx">
        /// The player's horizontal speed in <b>tiles per tick</b>, which is the unit AS3's
        /// <c>dx</c> is in — so the oracle's literal thresholds (<c>&gt; 10</c>, <c>&lt; 1</c>) are
        /// compared against it unconverted. A caller passing pixels would silently disable every
        /// term, so the name is in the parameter rather than in the body.
        /// </param>
        /// <param name="dy">The player's vertical speed, same unit, for the running test only.</param>
        /// <param name="weaponFacingDiffers">
        /// AS3 <c>currentWeapon.storona != storona</c> — the equipped weapon points the other way
        /// from the body. The caller owns that comparison because it needs both the weapon and the
        /// unit's facing; passing a bool keeps this method from reaching into the weapon layer.
        /// </param>
        /// <returns>
        /// The composed multiplier: <c>allPrecMult</c> times each term that applies. <b>Always
        /// positive</b> — every factor is <c>1 ± a penalty &lt; 1</c>, so it cannot flip the sign and
        /// turn a shot's precision negative.
        /// </returns>
        public float ComputePrecisionMultiplier(bool isGrounded, float dx, float dy,
                                                bool weaponFacingDiffers)
        {
            return ComposePrecisionMultiplier(
                allPrecMult, runPenalty, jumpPenalty, stayBonus, backPenalty,
                isGrounded, dx, dy, weaponFacingDiffers);
        }

        /// <summary>
        /// The <b>pure</b> form of the AS3 precision block (<c>UnitPlayer.as:1181-1200</c>).
        ///
        /// <para><b>Why static.</b> The rule is arithmetic on nine numbers and has no dependency on
        /// Unity, so keeping it on the <c>MonoBehaviour</c> instance would make it unrunnable in the
        /// offline fixture harness (<c>CharacterStats</c> is a <c>MonoBehaviour</c>, and instantiating
        /// one there throws <c>ECall</c>). A static function is directly testable, which is the only
        /// reason the four terms can be pinned to the oracle at all.</para>
        /// </summary>
        /// <param name="allPrecMult">The owner's standing multiplier (the seed value).</param>
        /// <param name="runPenalty">AS3 <c>Pers.runPenalty</c>, applied only when <b>positive</b>.</param>
        /// <param name="jumpPenalty">AS3 <c>Pers.jumpPenalty</c>, applied when airborne.</param>
        /// <param name="stayBonus">AS3 <c>Pers.stayBonus</c>, added when grounded and near-still.</param>
        /// <param name="backPenalty">AS3 <c>Pers.backPenalty</c>, applied when the weapon faces back.</param>
        /// <param name="isGrounded">AS3 <c>stay</c> — grounded, not "motionless".</param>
        /// <param name="dx">Horizontal speed in <b>px per AS3 frame</b>, the oracle's own unit.</param>
        /// <param name="dy">Vertical speed, same unit.</param>
        /// <param name="weaponFacingDiffers">AS3 <c>currentWeapon.storona != storona</c>.</param>
        public static float ComposePrecisionMultiplier(
            float allPrecMult, float runPenalty, float jumpPenalty, float stayBonus, float backPenalty,
            bool isGrounded, float dx, float dy, bool weaponFacingDiffers)
        {
            float precMult = allPrecMult;

            // AS3: `if(this.sats.que.length == 0 && !this.lurked)` — always true in the port.
            // See the instance method's remarks; the gate is reproduced as a comment, not dead state.

            // `runPenalty > 0` is a real guard, not a formality: the perk sets it to 0.25, but a
            // future `<sk ref='mult'>` could zero it, and AS3 would then skip the term entirely
            // rather than multiplying by 1.
            if (runPenalty > 0f && (dx > 10f || dx < -10f || dy > 10f || dy < -10f))
            {
                precMult *= 1f - runPenalty;
            }

            // Airborne. Note this is `!stay`, so it fires on *every* jump, not only while rising.
            if (!isGrounded)
            {
                precMult *= 1f - jumpPenalty;
            }

            // Grounded AND nearly stationary. Mutually exclusive with the jump term by construction
            // (`stay` vs `!stay`), which is why the oracle can write them as two separate ifs.
            if (isGrounded && dx < 1f && dx > -1f)
            {
                precMult *= 1f + stayBonus;
            }

            // Firing behind yourself.
            if (weaponFacingDiffers)
            {
                precMult *= 1f - backPenalty;
            }

            return precMult;
        }

        /// <summary>
        /// The AS3 <c>gg.vulner</c> table: <see cref="VulnerabilityData.Neutral"/> (all 1, emp 0 —
        /// AS3's own <c>defaultParams()</c> baseline) with every <c>tip='res'</c> deduction
        /// subtracted.
        ///
        /// <para><see cref="DamageType"/>'s numeric values <i>are</i> AS3's <c>D_*</c> indices, so the
        /// dictionary key casts straight across; the guard drops anything that is not a real damage
        /// type rather than writing a slot by accident.</para>
        /// </summary>
        private VulnerabilityData BuildVulnerabilityBaseline()
        {
            VulnerabilityData table = VulnerabilityData.Neutral;
            foreach (var kvp in vulnerabilities)
            {
                if (!Enum.IsDefined(typeof(DamageType), kvp.Key)) continue;
                table.SetVulnerability((DamageType)kvp.Key, kvp.Value);
            }
            return table;
        }

        /// <summary>
        /// Universal AS3 setSkillParam (Pers.as:1468-1573).
        /// Applies modifiers from skills, perks, items, traumas, and status effects.
        /// </summary>
        /// <param name="sourceType">
        /// What KIND of source this modifier came from — the <see cref="StatFactor.sourceType"/>
        /// vocabulary: <c>"skill"</c>, <c>"perk"</c>, <c>"weap"</c>, <c>"stat"</c>, <c>"min"</c>.
        /// Defaults to <c>"stat"</c> for callers that are not one of those.
        ///
        /// <para><b>Not `refType`.</b> These are different questions: `sourceType` answers "who applied
        /// this" and `refType` answers "how" (add vs mult). Every call site here used to pass
        /// <c>refType</c> straight into the <c>sourceType</c> slot, so the documented vocabulary was
        /// dead and a factor search for sourceType == "skill" could never match.</para>
        /// </param>
        public void ApplyNamedStat(string statId, string tip, string refType, float val,
                                   string sourceId = null, string sourceType = "stat")
        {
            if (string.IsNullOrEmpty(statId)) return;

            if (tip == "weap")
            {
                weaponSkills[statId] = val;
                TrackFactor(statId, sourceId ?? statId, "weap", val, val);
                return;
            }

            if (tip == "res")
            {
                if (int.TryParse(statId, out int resIndex))
                {
                    float cur = vulnerabilities.TryGetValue(resIndex, out float v) ? v : 1.0f;
                    float updated = cur - val;
                    vulnerabilities[resIndex] = updated;
                    TrackFactor($"res[{resIndex}]", sourceId ?? "res", "min", val, updated);
                }
                else
                {
                    TrackFactor($"res_{statId}", sourceId ?? "res", "min", val, val);
                }
                return;
            }

            // AS3 Pers.as:1522-1525 — `tip="m"` means "add to the Pers member", and it ignores the
            // `ref` attribute entirely (`this[_loc4_.@id] += _loc5_`). 84 entries in AllData.as use
            // it (maxmW/maxm1..3/maxmM), and none of them carries a `ref`, so without this they fell
            // through to the switch as a *set* — or, for the ids with no case, were dropped.
            if (tip == "m")
            {
                refType = "add";
            }

            switch (statId)
            {
                case "maxhp":
                case "maxHp":
                    maxHp = ApplyFloatOp(maxHp, refType, val);
                    TrackFactor("maxHp", sourceId ?? "stat", sourceType, val, maxHp);
                    break;
                case "inMaxMana":
                case "maxMana":
                    inMaxMana = ApplyFloatOp(inMaxMana, refType, val);
                    TrackFactor("inMaxMana", sourceId ?? "stat", sourceType, val, inMaxMana);
                    break;
                case "allDamMult":
                    allDamMult = ApplyFloatOp(allDamMult, refType, val);
                    TrackFactor("allDamMult", sourceId ?? "stat", sourceType, val, allDamMult);
                    break;
                case "allVulnerMult":
                    allVulnerMult = ApplyFloatOp(allVulnerMult, refType, val);
                    TrackFactor("allVulnerMult", sourceId ?? "stat", sourceType, val, allVulnerMult);
                    break;
                case "critCh":
                    critCh = ApplyFloatOp(critCh, refType, val);
                    TrackFactor("critCh", sourceId ?? "stat", sourceType, val, critCh);
                    break;
                case "critDamMult":
                    critDamMult = ApplyFloatOp(critDamMult, refType, val);
                    TrackFactor("critDamMult", sourceId ?? "stat", sourceType, val, critDamMult);
                    break;
                case "dexter":
                    dexter = ApplyFloatOp(dexter, refType, val);
                    TrackFactor("dexter", sourceId ?? "stat", sourceType, val, dexter);
                    break;
                case "dodgePlus":
                    dodgePlus = ApplyFloatOp(dodgePlus, refType, val);
                    TrackFactor("dodgePlus", sourceId ?? "stat", sourceType, val, dodgePlus);
                    break;
                case "skin":
                    skin = ApplyFloatOp(skin, refType, val);
                    TrackFactor("skin", sourceId ?? "stat", sourceType, val, skin);
                    break;
                case "meleeDamMult":
                    meleeDamMult = ApplyFloatOp(meleeDamMult, refType, val);
                    TrackFactor("meleeDamMult", sourceId ?? "stat", sourceType, val, meleeDamMult);
                    break;
                case "meleeSpdMult":
                    meleeSpdMult = ApplyFloatOp(meleeSpdMult, refType, val);
                    TrackFactor("meleeSpdMult", sourceId ?? "stat", sourceType, val, meleeSpdMult);
                    break;
                case "meleeRun":
                    // Reached from the melee skill (AllData.as:5227, v0=10 vd=3) — a flat stat, not
                    // a multiplier, and the only writer of Pers.meleeRun in the whole oracle.
                    meleeRun = ApplyFloatOp(meleeRun, refType, val);
                    TrackFactor("meleeRun", sourceId ?? "stat", sourceType, val, meleeRun);
                    break;
                case "gunsDamMult":
                    gunsDamMult = ApplyFloatOp(gunsDamMult, refType, val);
                    TrackFactor("gunsDamMult", sourceId ?? "stat", sourceType, val, gunsDamMult);
                    break;
                case "spellsDamMult":
                    spellsDamMult = ApplyFloatOp(spellsDamMult, refType, val);
                    TrackFactor("spellsDamMult", sourceId ?? "stat", sourceType, val, spellsDamMult);
                    break;
                case "punchDamMult":
                    punchDamMult = ApplyFloatOp(punchDamMult, refType, val);
                    TrackFactor("punchDamMult", sourceId ?? "stat", sourceType, val, punchDamMult);
                    break;
                case "allSpeedMult":
                    allSpeedMult = ApplyFloatOp(allSpeedMult, refType, val);
                    TrackFactor("allSpeedMult", sourceId ?? "stat", sourceType, val, allSpeedMult);
                    break;
                case "allPrecMult":
                    allPrecMult = ApplyFloatOp(allPrecMult, refType, val);
                    TrackFactor("allPrecMult", sourceId ?? "stat", sourceType, val, allPrecMult);
                    break;
                case "runSpeedMult":
                    runSpeedMult = ApplyFloatOp(runSpeedMult, refType, val);
                    TrackFactor("runSpeedMult", sourceId ?? "stat", sourceType, val, runSpeedMult);
                    break;
                // ── The precision-penalty quartet (UnitPlayer.as:1185-1199) ──────────────────────
                // All four arrive with NO `ref` (`v0='0.5' v1='0.25'` etc.), so ApplyFloatOp assigns
                // the tier value absolutely — which is correct and is how AS3 reads them: the perk's
                // tier *is* the penalty, not a delta. Do not "fix" these to a mult op.
                case "runPenalty":
                    runPenalty = ApplyFloatOp(runPenalty, refType, val);
                    TrackFactor("runPenalty", sourceId ?? "stat", sourceType, val, runPenalty);
                    break;
                case "jumpPenalty":
                    jumpPenalty = ApplyFloatOp(jumpPenalty, refType, val);
                    TrackFactor("jumpPenalty", sourceId ?? "stat", sourceType, val, jumpPenalty);
                    break;
                case "backPenalty":
                    backPenalty = ApplyFloatOp(backPenalty, refType, val);
                    TrackFactor("backPenalty", sourceId ?? "stat", sourceType, val, backPenalty);
                    break;
                case "stayBonus":
                    stayBonus = ApplyFloatOp(stayBonus, refType, val);
                    TrackFactor("stayBonus", sourceId ?? "stat", sourceType, val, stayBonus);
                    break;
                case "mazilAdd":
                    mazilAdd = ApplyFloatOp(mazilAdd, refType, val);
                    TrackFactor("mazilAdd", sourceId ?? "stat", sourceType, val, mazilAdd);
                    break;
                case "isDJ":
                    isDJ = Mathf.RoundToInt(ApplyFloatOp(isDJ, refType, val));
                    TrackFactor("isDJ", sourceId ?? "stat", sourceType, val, isDJ);
                    break;
                case "levitOn":
                    levitOn = Mathf.RoundToInt(ApplyFloatOp(levitOn, refType, val));
                    TrackFactor("levitOn", sourceId ?? "stat", sourceType, val, levitOn);
                    break;
                case "levitDMana":
                    levitDMana = ApplyFloatOp(levitDMana, refType, val);
                    TrackFactor("levitDMana", sourceId ?? "stat", sourceType, val, levitDMana);
                    break;
                case "levitDManaUp":
                    levitDManaUp = ApplyFloatOp(levitDManaUp, refType, val);
                    TrackFactor("levitDManaUp", sourceId ?? "stat", sourceType, val, levitDManaUp);
                    break;
                case "telemaster":
                    telemaster = Mathf.RoundToInt(ApplyFloatOp(telemaster, refType, val));
                    TrackFactor("telemaster", sourceId ?? "stat", sourceType, val, telemaster);
                    break;
                case "teleDist":
                    teleDist = ApplyFloatOp(teleDist, refType, val);
                    TrackFactor("teleDist", sourceId ?? "stat", sourceType, val, teleDist);
                    break;
                case "telePorog":
                    telePorog = ApplyFloatOp(telePorog, refType, val);
                    TrackFactor("telePorog", sourceId ?? "stat", sourceType, val, telePorog);
                    break;
                case "teleMult":
                    teleMult = ApplyFloatOp(teleMult, refType, val);
                    TrackFactor("teleMult", sourceId ?? "stat", sourceType, val, teleMult);
                    break;
                case "maxTeleMassa":
                    maxTeleMassa = ApplyFloatOp(maxTeleMassa, refType, val);
                    TrackFactor("maxTeleMassa", sourceId ?? "stat", sourceType, val, maxTeleMassa);
                    break;
                case "throwForce":
                    throwForce = ApplyFloatOp(throwForce, refType, val);
                    TrackFactor("throwForce", sourceId ?? "stat", sourceType, val, throwForce);
                    break;
                case "throwDmagic":
                    throwDmagic = ApplyFloatOp(throwDmagic, refType, val);
                    TrackFactor("throwDmagic", sourceId ?? "stat", sourceType, val, throwDmagic);
                    break;
                case "throwDmanaMult":
                    throwDmanaMult = ApplyFloatOp(throwDmanaMult, refType, val);
                    TrackFactor("throwDmanaMult", sourceId ?? "stat", sourceType, val, throwDmanaMult);
                    break;
                case "throwDmana":
                    throwDmana = ApplyFloatOp(throwDmana, refType, val);
                    TrackFactor("throwDmana", sourceId ?? "stat", sourceType, val, throwDmana);
                    break;
                case "allDManaMult":
                    allDManaMult = ApplyFloatOp(allDManaMult, refType, val);
                    TrackFactor("allDManaMult", sourceId ?? "stat", sourceType, val, allDManaMult);
                    break;
                case "warlockDManaMult":
                    warlockDManaMult = ApplyFloatOp(warlockDManaMult, refType, val);
                    TrackFactor("warlockDManaMult", sourceId ?? "stat", sourceType, val, warlockDManaMult);
                    break;
                case "portPoss":
                    portPoss = Mathf.RoundToInt(ApplyFloatOp(portPoss, refType, val));
                    TrackFactor("portPoss", sourceId ?? "stat", sourceType, val, portPoss);
                    break;
                case "spellsPoss":
                    spellsPoss = Mathf.RoundToInt(ApplyFloatOp(spellsPoss, refType, val));
                    TrackFactor("spellsPoss", sourceId ?? "stat", sourceType, val, spellsPoss);
                    break;
                case "lockPick":
                    lockPick = Mathf.RoundToInt(ApplyFloatOp(lockPick, refType, val));
                    TrackFactor("lockPick", sourceId ?? "stat", sourceType, val, lockPick);
                    break;
                case "possLockPick":
                    possLockPick = Mathf.RoundToInt(ApplyFloatOp(possLockPick, refType, val));
                    TrackFactor("possLockPick", sourceId ?? "stat", sourceType, val, possLockPick);
                    break;
                case "hacker":
                    hacker = Mathf.RoundToInt(ApplyFloatOp(hacker, refType, val));
                    TrackFactor("hacker", sourceId ?? "stat", sourceType, val, hacker);
                    break;
                case "hackerMaster":
                    hackerMaster = Mathf.RoundToInt(ApplyFloatOp(hackerMaster, refType, val));
                    TrackFactor("hackerMaster", sourceId ?? "stat", sourceType, val, hackerMaster);
                    break;
                case "hackAtt":
                    hackAtt = Mathf.RoundToInt(ApplyFloatOp(hackAtt, refType, val));
                    TrackFactor("hackAtt", sourceId ?? "stat", sourceType, val, hackAtt);
                    break;
                case "unlockMaster":
                    unlockMaster = Mathf.RoundToInt(ApplyFloatOp(unlockMaster, refType, val));
                    TrackFactor("unlockMaster", sourceId ?? "stat", sourceType, val, unlockMaster);
                    break;
                case "lockAtt":
                    lockAtt = ApplyFloatOp(lockAtt, refType, val);
                    TrackFactor("lockAtt", sourceId ?? "stat", sourceType, val, lockAtt);
                    break;
                case "pinBreak":
                    pinBreak = ApplyFloatOp(pinBreak, refType, val);
                    TrackFactor("pinBreak", sourceId ?? "stat", sourceType, val, pinBreak);
                    break;
                case "lockPickTime":
                    lockPickTime = Mathf.RoundToInt(ApplyFloatOp(lockPickTime, refType, val));
                    TrackFactor("lockPickTime", sourceId ?? "stat", sourceType, val, lockPickTime);
                    break;
                case "repair":
                    repair = Mathf.RoundToInt(ApplyFloatOp(repair, refType, val));
                    TrackFactor("repair", sourceId ?? "stat", sourceType, val, repair);
                    break;
                case "repairMult":
                    repairMult = ApplyFloatOp(repairMult, refType, val);
                    TrackFactor("repairMult", sourceId ?? "stat", sourceType, val, repairMult);
                    break;
                case "remine":
                    remine = Mathf.RoundToInt(ApplyFloatOp(remine, refType, val));
                    TrackFactor("remine", sourceId ?? "stat", sourceType, val, remine);
                    break;
                case "signal":
                    signal = Mathf.RoundToInt(ApplyFloatOp(signal, refType, val));
                    TrackFactor("signal", sourceId ?? "stat", sourceType, val, signal);
                    break;
                case "barterLvl":
                    barterLvl = Mathf.RoundToInt(ApplyFloatOp(barterLvl, refType, val));
                    TrackFactor("barterLvl", sourceId ?? "stat", sourceType, val, barterLvl);
                    break;
                case "limitBuys":
                    limitBuys = ApplyFloatOp(limitBuys, refType, val);
                    TrackFactor("limitBuys", sourceId ?? "stat", sourceType, val, limitBuys);
                    break;
                case "capsMult":
                case "barterMult":
                    capsMult = ApplyFloatOp(capsMult, refType, val);
                    TrackFactor("capsMult", sourceId ?? "stat", sourceType, val, capsMult);
                    break;
                case "healMult":
                    healMult = ApplyFloatOp(healMult, refType, val);
                    TrackFactor("healMult", sourceId ?? "stat", sourceType, val, healMult);
                    break;
                case "bonusHeal":
                    bonusHeal = ApplyFloatOp(bonusHeal, refType, val);
                    TrackFactor("bonusHeal", sourceId ?? "stat", sourceType, val, bonusHeal);
                    break;
                case "reanimHp":
                    reanimHp = ApplyFloatOp(reanimHp, refType, val);
                    TrackFactor("reanimHp", sourceId ?? "stat", sourceType, val, reanimHp);
                    break;
                case "regenFew":
                    regenFew = ApplyFloatOp(regenFew, refType, val);
                    TrackFactor("regenFew", sourceId ?? "stat", sourceType, val, regenFew);
                    break;
                case "regenMax":
                    regenMax = ApplyFloatOp(regenMax, refType, val);
                    TrackFactor("regenMax", sourceId ?? "stat", sourceType, val, regenMax);
                    break;
                case "stealthMult":
                    stealthMult = ApplyFloatOp(stealthMult, refType, val);
                    TrackFactor("stealthMult", sourceId ?? "stat", sourceType, val, stealthMult);
                    break;
                case "noiseRun":
                    noiseRun = ApplyFloatOp(noiseRun, refType, val);
                    TrackFactor("noiseRun", sourceId ?? "stat", sourceType, val, noiseRun);
                    break;
                case "sneak":
                    sneak = ApplyFloatOp(sneak, refType, val);
                    TrackFactor("sneak", sourceId ?? "stat", sourceType, val, sneak);
                    break;
                case "critInvis":
                    critInvis = ApplyFloatOp(critInvis, refType, val);
                    TrackFactor("critInvis", sourceId ?? "stat", sourceType, val, critInvis);
                    break;
                case "desintegr":
                    desintegr = ApplyFloatOp(desintegr, refType, val);
                    TrackFactor("desintegr", sourceId ?? "stat", sourceType, val, desintegr);
                    break;
                case "stamRun":
                    stamRun = ApplyFloatOp(stamRun, refType, val);
                    TrackFactor("stamRun", sourceId ?? "stat", sourceType, val, stamRun);
                    break;
                case "stamRes":
                    stamRes = ApplyFloatOp(stamRes, refType, val);
                    TrackFactor("stamRes", sourceId ?? "stat", sourceType, val, stamRes);
                    break;
                case "maxOd":
                    maxOd = ApplyFloatOp(maxOd, refType, val);
                    TrackFactor("maxOd", sourceId ?? "stat", sourceType, val, maxOd);
                    break;
                case "recMana":
                    recMana = ApplyFloatOp(recMana, refType, val);
                    TrackFactor("recMana", sourceId ?? "stat", sourceType, val, recMana);
                    break;
                case "recManaMin":
                    recManaMin = ApplyFloatOp(recManaMin, refType, val);
                    TrackFactor("recManaMin", sourceId ?? "stat", sourceType, val, recManaMin);
                    break;
                case "manaMin":
                    manaMin = ApplyFloatOp(manaMin, refType, val);
                    TrackFactor("manaMin", sourceId ?? "stat", sourceType, val, manaMin);
                    break;
                case "shtrManaRes":
                    shtrManaRes = ApplyFloatOp(shtrManaRes, refType, val);
                    TrackFactor("shtrManaRes", sourceId ?? "stat", sourceType, val, shtrManaRes);
                    break;
                case "manaHPRes":
                    manaHPRes = ApplyFloatOp(manaHPRes, refType, val);
                    TrackFactor("manaHPRes", sourceId ?? "stat", sourceType, val, manaHPRes);
                    break;
                case "portMana":
                    portMana = ApplyFloatOp(portMana, refType, val);
                    TrackFactor("portMana", sourceId ?? "stat", sourceType, val, portMana);
                    break;
                case "teleMana":
                    teleMana = ApplyFloatOp(teleMana, refType, val);
                    TrackFactor("teleMana", sourceId ?? "stat", sourceType, val, teleMana);
                    break;
                case "teleManaMult":
                    teleManaMult = ApplyFloatOp(teleManaMult, refType, val);
                    TrackFactor("teleManaMult", sourceId ?? "stat", sourceType, val, teleManaMult);
                    break;
                case "alicornRunMana":
                    alicornRunMana = ApplyFloatOp(alicornRunMana, refType, val);
                    TrackFactor("alicornRunMana", sourceId ?? "stat", sourceType, val, alicornRunMana);
                    break;
                case "inMaxMagic":
                case "maxmana":
                    // AS3 Unit.maxmana. Reachable by name from a <sk> the same way inMaxMana is.
                    MaxMagicMana = ApplyFloatOp(MaxMagicMana, refType, val);
                    TrackFactor("inMaxMagic", sourceId ?? "stat", sourceType, val, MaxMagicMana);
                    break;
                case "eco":
                    eco = Mathf.RoundToInt(ApplyFloatOp(eco, refType, val));
                    TrackFactor("eco", sourceId ?? "stat", sourceType, val, eco);
                    break;
                case "sneakLurk":
                    sneakLurk = ApplyFloatOp(sneakLurk, refType, val);
                    TrackFactor("sneakLurk", sourceId ?? "stat", sourceType, val, sneakLurk);
                    break;
                // ── Destinations that already had a field but no case, so the modifier was dropped.
                //    Every one of these is referenced by authored content (skills and perks). ──
                case "kickDestroy":
                    kickDestroy = ApplyFloatOp(kickDestroy, refType, val);
                    TrackFactor("kickDestroy", sourceId ?? "stat", sourceType, val, kickDestroy);
                    break;
                case "radChild":
                    radChild = ApplyFloatOp(radChild, refType, val);
                    TrackFactor("radChild", sourceId ?? "stat", sourceType, val, radChild);
                    break;
                case "organMultPot":
                    organMultPot = ApplyFloatOp(organMultPot, refType, val);
                    TrackFactor("organMultPot", sourceId ?? "stat", sourceType, val, organMultPot);
                    break;
                case "reloadMult":
                    reloadMult = ApplyFloatOp(reloadMult, refType, val);
                    TrackFactor("reloadMult", sourceId ?? "stat", sourceType, val, reloadMult);
                    break;
                case "recoilMult":
                    recoilMult = ApplyFloatOp(recoilMult, refType, val);
                    TrackFactor("recoilMult", sourceId ?? "stat", sourceType, val, recoilMult);
                    break;
                case "jammedMult":
                    jammedMult = ApplyFloatOp(jammedMult, refType, val);
                    TrackFactor("jammedMult", sourceId ?? "stat", sourceType, val, jammedMult);
                    break;
                case "recyc":
                    recyc = ApplyFloatOp(recyc, refType, val);
                    TrackFactor("recyc", sourceId ?? "stat", sourceType, val, recyc);
                    break;
                case "jumpdy":
                    jumpdy = ApplyFloatOp(jumpdy, refType, val);
                    TrackFactor("jumpdy", sourceId ?? "stat", sourceType, val, jumpdy);
                    break;
                case "djumpdy":
                    djumpdy = ApplyFloatOp(djumpdy, refType, val);
                    TrackFactor("djumpdy", sourceId ?? "stat", sourceType, val, djumpdy);
                    break;
                case "freel":
                    freel = Mathf.RoundToInt(ApplyFloatOp(freel, refType, val));
                    TrackFactor("freel", sourceId ?? "stat", sourceType, val, freel);
                    break;
                case "upChance":
                    upChance = ApplyFloatOp(upChance, refType, val);
                    TrackFactor("upChance", sourceId ?? "stat", sourceType, val, upChance);
                    break;
                case "damPony":
                case "damZombie":
                case "damRobot":
                case "damInsect":
                case "damMonster":
                case "damAlicorn":
                    ApplyCreatureDamageMult(statId, refType, val, sourceId, sourceType);
                    break;
                case "petDam":
                    petDam = ApplyFloatOp(petDam, refType, val);
                    TrackFactor("petDam", sourceId ?? "stat", sourceType, val, petDam);
                    break;
                case "petSkin":
                    petSkin = ApplyFloatOp(petSkin, refType, val);
                    TrackFactor("petSkin", sourceId ?? "stat", sourceType, val, petSkin);
                    break;
                case "petVulner":
                    petVulner = ApplyFloatOp(petVulner, refType, val);
                    TrackFactor("petVulner", sourceId ?? "stat", sourceType, val, petVulner);
                    break;
                case "owlDam":
                    owlDam = ApplyFloatOp(owlDam, refType, val);
                    TrackFactor("owlDam", sourceId ?? "stat", sourceType, val, owlDam);
                    break;
                case "owlSkin":
                    owlSkin = ApplyFloatOp(owlSkin, refType, val);
                    TrackFactor("owlSkin", sourceId ?? "stat", sourceType, val, owlSkin);
                    break;
                case "owlVulner":
                    owlVulner = ApplyFloatOp(owlVulner, refType, val);
                    TrackFactor("owlVulner", sourceId ?? "stat", sourceType, val, owlVulner);
                    break;
                // Magazine capacities. AS3 reaches these through the `tip="m"` branch above, which
                // forces an additive application.
                case "maxmW":
                    maxmW = ApplyFloatOp(maxmW, refType, val);
                    TrackFactor("maxmW", sourceId ?? "stat", sourceType, val, maxmW);
                    break;
                case "maxm1":
                    maxm1 = ApplyFloatOp(maxm1, refType, val);
                    TrackFactor("maxm1", sourceId ?? "stat", sourceType, val, maxm1);
                    break;
                case "maxm2":
                    maxm2 = ApplyFloatOp(maxm2, refType, val);
                    TrackFactor("maxm2", sourceId ?? "stat", sourceType, val, maxm2);
                    break;
                case "maxm3":
                    maxm3 = ApplyFloatOp(maxm3, refType, val);
                    TrackFactor("maxm3", sourceId ?? "stat", sourceType, val, maxm3);
                    break;
                case "maxmM":
                    maxmM = ApplyFloatOp(maxmM, refType, val);
                    TrackFactor("maxmM", sourceId ?? "stat", sourceType, val, maxmM);
                    break;
                default:
                    // A stat the content authors reference but this class has no destination for.
                    // AS3 would have applied it (`this[id] = _loc5_` on the Pers object); here it is
                    // recorded for the UI only. Logged once per id so the gap is visible rather than
                    // silently swallowed — see the RPG evaluation's "67 of 155 statIds" finding.
                    WarnUnmappedStat(statId, sourceId);
                    TrackFactor(statId, sourceId ?? "stat", sourceType, val, val);
                    break;
            }
        }

        // One line per unmapped statId, not one per application: a skill or perk is re-evaluated on
        // every RecalculateStats, which happens on every stage change and every level-up.
        private readonly HashSet<string> _warnedUnmappedStats = new HashSet<string>();

        private void WarnUnmappedStat(string statId, string sourceId)
        {
            if (!_warnedUnmappedStats.Add(statId)) return;
            Debug.LogWarning(
                $"[CharacterStats] '{statId}' (from '{sourceId ?? "?"}') has no destination in " +
                "ApplyNamedStat, so this modifier has no effect. Add a case for it, or drop it " +
                "from AllData.as. Further occurrences of this id are not logged.");
        }

        /// <summary>
        /// The six per-creature-family damage multipliers (AS3 <c>damPony</c>, <c>damZombie</c>,
        /// <c>damRobot</c>, <c>damInsect</c>, <c>damMonster</c>, <c>damAlicorn</c>). Grouped because
        /// they are one behaviour — a damage multiplier keyed by the target's family — and a
        /// per-family case block would be six copies of the same three lines.
        /// </summary>
        private void ApplyCreatureDamageMult(string statId, string refType, float val, string sourceId,
                                            string sourceType = "stat")
        {
            switch (statId)
            {
                case "damPony": damPony = ApplyFloatOp(damPony, refType, val); break;
                case "damZombie": damZombie = ApplyFloatOp(damZombie, refType, val); break;
                case "damRobot": damRobot = ApplyFloatOp(damRobot, refType, val); break;
                case "damInsect": damInsect = ApplyFloatOp(damInsect, refType, val); break;
                case "damMonster": damMonster = ApplyFloatOp(damMonster, refType, val); break;
                case "damAlicorn": damAlicorn = ApplyFloatOp(damAlicorn, refType, val); break;
                default: return;
            }
            TrackFactor(statId, sourceId ?? "stat", sourceType, val, GetCreatureDamageMult(statId));
        }

        private float GetCreatureDamageMult(string statId)
        {
            switch (statId)
            {
                case "damPony": return damPony;
                case "damZombie": return damZombie;
                case "damRobot": return damRobot;
                case "damInsect": return damInsect;
                case "damMonster": return damMonster;
                case "damAlicorn": return damAlicorn;
                default: return 1f;
            }
        }

        private static float ApplyFloatOp(float current, string refType, float val)
        {
            if (refType == "add") return current + val;
            if (refType == "mult") return current * val;
            return val;
        }

        /// <summary>
        /// Applies limb trauma effects from AllData.as trauma perks (AS3 Pers.as:1978 traumaParameters).
        /// </summary>
        private void ApplyTraumaModifiers()
        {
            if (skillDatabase == null) return;

            if (headSt > 0)
            {
                var p = skillDatabase.GetPerk("trauma_head");
                if (p != null && p.modifiers != null)
                {
                    int rank = Mathf.Min(headSt, 3);
                    foreach (var mod in p.modifiers)
                        StatModifierApplier.Apply(this, mod, rank, rank, "trauma_head");
                }
            }

            if (torsSt > 0)
            {
                var p = skillDatabase.GetPerk("trauma_tors");
                if (p != null && p.modifiers != null)
                {
                    int rank = Mathf.Min(torsSt, 3);
                    foreach (var mod in p.modifiers)
                        StatModifierApplier.Apply(this, mod, rank, rank, "trauma_tors");
                }
            }

            if (legsSt > 0)
            {
                var p = skillDatabase.GetPerk("trauma_legs");
                if (p != null && p.modifiers != null)
                {
                    int rank = Mathf.Min(legsSt, 3);
                    foreach (var mod in p.modifiers)
                        StatModifierApplier.Apply(this, mod, rank, rank, "trauma_legs");
                }
            }

            if (bloodSt > 0)
            {
                var p = skillDatabase.GetPerk("trauma_blood");
                if (p != null && p.modifiers != null)
                {
                    int rank = Mathf.Min(bloodSt, 3);
                    foreach (var mod in p.modifiers)
                        StatModifierApplier.Apply(this, mod, rank, rank, "trauma_blood");
                }
            }

            if (manaSt > 0)
            {
                var p = skillDatabase.GetPerk("trauma_mana");
                if (p != null && p.modifiers != null)
                {
                    foreach (var mod in p.modifiers)
                        StatModifierApplier.Apply(this, mod, manaSt, manaSt, "trauma_mana");
                }
            }

            if (manaSt >= 4)
            {
                levitOn = 0;
                isDJ = 0;
                spellsPoss = 0;
            }
        }

        /// <summary>
        /// Apply organ damage according to AS3 distribution (Pers.as:1659 damage):
        /// 20% Head (x2 damage)
        /// 40% Torso (or poison/venom)
        /// 40% Legs
        /// </summary>
        public void ApplyOrganDamage(float damage, int damageType = 0, bool forceFatal = false)
        {
            // AS3 Pers.as:1662-1665 — a forced-fatal hit discards the incoming number and uses a
            // fraction of max organ HP instead.
            if (forceFatal) damage = dieDamage * inMaxHP;

            if (damage <= 0f) return;

            // AS3:1666-1669 — internal and bleeding damage never reach the organ roll at all.
            if (damageType == (int)DamageType.Internal || damageType == (int)DamageType.Bleed) return;

            // AS3:1670-1673 — necrotic is cut to a tenth before the multipliers.
            if (damageType == (int)DamageType.Necrotic) damage *= 0.1f;

            damage *= organMult;
            damage *= organMultPot;

            float roll = CombatRng.NextFloat();
            if (roll < 0.2f)
            {
                // Head hit. AS3:1683-1686 — the x2 is skipped on a forced-fatal hit.
                if (!forceFatal) damage *= 2f;
                int oldStage = 4 - Mathf.CeilToInt(headHp / inMaxHP * 4f);
                headHp -= damage;
                if (headHp < headMin) headHp = headMin;
                if (headHp <= 0f)
                {
                    headHp = 1f;
                    Die();
                }

                headSt = 4 - Mathf.CeilToInt(headHp / inMaxHP * 4f);
                if (headSt != oldStage)
                {
                    NotifyTraumaChanged(1, headSt);
                    RecalculateStats();
                }
            }
            else if (roll < 0.6f
                     || damageType == (int)DamageType.Poison
                     || damageType == (int)DamageType.Venom)
            {
                // AS3:1708 — the torso branch is taken by a roll under 0.6 OR by poison/venom.
                // The port used to test the literal `10`, which is D_ACID in its own DamageType
                // enum; D_POISON is 12 and D_VENOM is 7, so neither ever reached here.
                int oldStage = 4 - Mathf.CeilToInt(torsHp / inMaxHP * 4f);
                torsHp -= damage;
                if (torsHp < torsMin) torsHp = torsMin;
                if (torsHp <= 0f)
                {
                    torsHp = 1f;
                    Die();
                }

                torsSt = 4 - Mathf.CeilToInt(torsHp / inMaxHP * 4f);
                if (torsSt != oldStage)
                {
                    NotifyTraumaChanged(2, torsSt);
                    RecalculateStats();
                }
            }
            else
            {
                // Legs hit
                int oldStage = 4 - Mathf.CeilToInt(legsHp / inMaxHP * 4f);
                legsHp -= damage;
                if (legsHp < legsMin) legsHp = legsMin;
                if (legsHp <= 0f)
                {
                    legsHp = 1f;
                    Die();
                }

                legsSt = 4 - Mathf.CeilToInt(legsHp / inMaxHP * 4f);
                if (legsSt != oldStage)
                {
                    NotifyTraumaChanged(3, legsSt);
                    RecalculateStats();
                }
            }
        }

        /// <summary>
        /// AS3 <c>Pers.die()</c> (<c>Pers.as:1756-1769</c>). Raised by every organ that reaches zero.
        /// The caller (<see cref="PFE.Entities.Player.PlayerController"/>) owns what dying means.
        /// </summary>
        private void Die()
        {
            Debug.Log("[CharacterStats] An organ reached zero — dying (AS3 Pers.die()).");
            onDeath?.Invoke();
        }

        /// <summary>
        /// Apply blood damage (AS3 Pers.as:1771 bloodDamage).
        ///
        /// <para><b>Gated on damage type.</b> AS3 wraps the entire body in
        /// <c>if(param2 == D_BLEED || D_BLADE || D_BUL || D_FANG)</c>, so any other type does
        /// nothing; the port applied every call. The amount is also <i>random</i> in AS3
        /// (<c>Math.random() * param1</c>), and bullets and fangs are halved.</para>
        /// </summary>
        public void ApplyBloodDamage(float damage, int damageType = 0)
        {
            if (damage <= 0f) return;

            bool bleeds = damageType == (int)DamageType.Bleed
                          || damageType == (int)DamageType.Blade
                          || damageType == (int)DamageType.PhysicalBullet
                          || damageType == (int)DamageType.Fang;
            if (!bleeds) return;

            damage *= 3f * organMult;
            damage *= CombatRng.NextFloat();
            if (damageType == (int)DamageType.PhysicalBullet || damageType == (int)DamageType.Fang)
            {
                damage *= 0.5f;
            }

            int oldStage = 4 - Mathf.CeilToInt(bloodHp / inMaxHP * 4f);
            bloodHp -= damage;
            if (bloodHp < bloodMin) bloodHp = bloodMin;
            if (bloodHp <= 0f)
            {
                bloodHp = 1f;
                Die();
            }

            bloodSt = 4 - Mathf.CeilToInt(bloodHp / inMaxHP * 4f);
            if (bloodSt != oldStage)
            {
                NotifyTraumaChanged(4, bloodSt);
                RecalculateStats();
            }
        }

        /// <summary>
        /// Apply mana damage (AS3 Pers.as:1810 manaDamage).
        /// </summary>
        public void ApplyManaDamage(float damage)
        {
            if (damage <= 0f) return;

            int oldStage = 4 - Mathf.CeilToInt(manaHp / inMaxMana * 4f);
            manaHp -= damage;

            // AS3:1822-1829 — the difficulty's floor applies first, then the zero clamp. The port
            // used a bare Mathf.Max(0, ...), which dropped the floor (AS3 sets manaMin to 56 under
            // the `nomed` trigger and 0 otherwise).
            if (manaMin > 0f && manaHp < 1f) manaHp = 1f;
            if (manaHp < 0f) manaHp = 0f;

            manaSt = 4 - Mathf.CeilToInt(manaHp / inMaxMana * 4f);
            if (manaSt != oldStage)
            {
                NotifyTraumaChanged(5, manaSt);
                RecalculateStats();
            }
        }

        /// <summary>
        /// Everything the per-tick mana block needs to know about the rest of the player.
        /// A struct rather than parameters so the driver cannot silently omit a flag — the
        /// named fields force each one to be considered.
        /// </summary>
        public struct ManaTickState
        {
            /// <summary>AS3 <c>teleObj</c> — a prop is currently held by telekinesis.</summary>
            public bool TelekinesisActive;

            /// <summary>AS3 <c>teleSqrtMassa</c> — the square root of the held prop's mass.</summary>
            public float TelekinesisSqrtMass;

            /// <summary>AS3 <c>levit == 1</c>.</summary>
            public bool Levitating;

            /// <summary>
            /// AS3 <c>levitup</c> — ascending while levitating.
            ///
            /// <para><b>AS3 multiplies this branch by <c>grav</c></b> (<c>UnitPlayer.as:1313</c>,
            /// <c>dmana -= levitDManaUp * grav + levitDMana</c>), and <c>grav</c> is <i>not</i> a
            /// constant 1 — it is the room's gravity, assigned from <c>loc.grav</c> at <c>:785</c> and
            /// zeroed at <c>:789</c> when the room has none. This port has no room-gravity plumbing on
            /// this path yet, so it assumes <c>grav == 1</c>. That is a <b>known, deliberate
            /// simplification</b>, correct for normal-gravity rooms and wrong for a zero-g room, where
            /// AS3 charges only <c>levitDMana</c> (5) to ascend instead of 25. If rooms gain gravity,
            /// add the multiplier here rather than to the caller.</para>
            /// </summary>
            public bool LevitatingUp;
            /// <summary>AS3's alicorn branch: flying, running, and turning (<c>UnitPlayer.as:1333</c>).</summary>
            public bool AlicornRunning;
        }

        /// <summary>
        /// AS3's affordability test for a budget spend, including the full-pool bypass.
        ///
        /// <para><c>UnitPlayer.as:1633</c> reads
        /// <c>mana &lt; pers.portMana * pers.allDManaMult &amp;&amp; mana &lt; maxmana * 0.99</c> — so a
        /// pool at or above 99% of its ceiling may act <i>regardless of cost</i>. The same shape
        /// appears at <c>:1662</c>, <c>WMagic.as:67</c> and <c>Weapon.as:1845</c>. It looks like a
        /// bug and is almost certainly one, but it is the oracle, so it is preserved rather than
        /// corrected — and it is only reachable because the ceiling is now the correct 1000.</para>
        /// </summary>
        public bool CanAffordMagicMana(float cost)
        {
            return CanAffordMagic(MagicMana, MaxMagicMana, cost);
        }

        /// <summary>
        /// The rule behind <see cref="CanAffordMagicMana"/>, as a static so a caller holding only a
        /// <see cref="UnitStats"/> — which carries the budget but not the organs — can apply the
        /// <i>same</i> rule instead of a second copy of it.
        ///
        /// <para>There is exactly one caller of this shape outside this class:
        /// <c>PlayerLocomotionController.HasManaForTeleport</c>'s fallback, reached when the abilities
        /// component is not a <c>PlayerLocomotionAbilities</c> or has no organs attached. A bare
        /// <c>mana &gt;= cost</c> there would silently drop the bypass, which is precisely the kind of
        /// two-copies-drift the full-pool rule invites.</para>
        /// </summary>
        public static bool CanAffordMagic(float currentMana, float maxMana, float cost)
        {
            return currentMana >= cost || currentMana >= maxMana * 0.99f;
        }

        /// <summary>
        /// One AS3 frame of the mana block — <c>UnitPlayer.as:1302-1361</c>, verbatim.
        ///
        /// <para><b>One call = one tick = one AS3 frame.</b> Per <see cref="PFE.Core.ISimTickable"/>'s
        /// rule, frame counters stay frames: there is deliberately no <c>deltaTime</c> here, and
        /// converting AS3's per-frame constants into per-second ones is the bug this replaces.</para>
        ///
        /// <para><b>Both pools move, with different scalars.</b> The upkeep branch spends the
        /// BUDGET (<c>mana += dmana</c>, scaled by <c>allDManaMult</c>) <i>and</i> wounds the ORGAN
        /// (<c>manaDamage(-dmana * teleMana * teleManaMult)</c>, and a third of that when only
        /// holding a prop). That coupling is the reason the two pools exist at all; a single pool
        /// can only do one of the two.</para>
        ///
        /// <para><b>The <c>dmana</c> carry-over is an oracle quirk, preserved on purpose.</b>
        /// AS3 declares <c>dmana</c> on <c>Unit</c>, resets it only inside the upkeep branch
        /// (<c>:1304</c>), and then the idle branch reads the <i>previous</i> frame's value:
        /// <c>if (dmana &lt; recManaMin*shtrManaRes) dmana = ...; dmana += recMana*shtrManaRes;</c>.
        /// On consecutive idle frames that compounds, so the budget refills quadratically rather
        /// than linearly — which is what makes 0.012/frame a usable regen rate instead of a
        /// 46-minute wait. Reproducing it is a deliberate choice; "fixing" it to a flat
        /// <c>recMana</c> per tick would be a divergence, not a cleanup.</para>
        /// </summary>
        public void TickMana(in ManaTickState state)
        {
            float dmana = _manaDelta;

            if (state.TelekinesisActive || state.Levitating)
            {
                dmana = 0f;

                if (state.TelekinesisActive)
                {
                    dmana -= state.TelekinesisSqrtMass * teleMult;
                }

                if (state.Levitating)
                {
                    // AS3: `dmana -= pers.levitDManaUp * grav + pers.levitDMana` while ascending.
                    dmana -= state.LevitatingUp
                        ? levitDManaUp + levitDMana
                        : levitDMana;
                }

                dmana *= allDManaMult;

                if (teleMana > 0f)
                {
                    // The organ wound. AS3:1325 uses the full drain while levitating;
                    // AS3:1329 uses a third of it when only a prop is held.
                    float wound = state.Levitating
                        ? -dmana * teleMana * teleManaMult
                        : -dmana / 3f * teleMana * teleManaMult;

                    ApplyManaDamage(wound);
                }
            }
            else if (state.AlicornRunning)
            {
                dmana = -alicornRunMana;
            }
            else
            {
                if (dmana < recManaMin * shtrManaRes)
                {
                    dmana = recManaMin * shtrManaRes;
                }
                dmana += recMana * shtrManaRes;
            }

            _manaDelta = dmana;

            MagicMana = Mathf.Min(MagicMana + dmana, MaxMagicMana);

            // AS3:1357-1359 — the organ only tops up while it is BELOW manaMin. With the default
            // manaMin of 0 this never fires, which is exactly why the organ has no regeneration.
            if (manaHp < manaMin)
            {
                manaHp += manaHPRes;
            }
        }

        /// <summary>
        /// Heal specific organ (AS3 Pers.as:1841 heal).
        /// organType: 1=head, 2=torso, 3=legs, 4=all physical, 5=blood, 6=mana.
        /// </summary>
        public void HealOrgan(float amount, int organType = 4)
        {
            if (amount <= 0f) return;

            if (organType == 1 || organType == 4)
            {
                headHp = Mathf.Min(inMaxHP, headHp + amount);
                headSt = 4 - Mathf.CeilToInt(headHp / inMaxHP * 4f);
            }
            if (organType == 2 || organType == 4)
            {
                torsHp = Mathf.Min(inMaxHP, torsHp + amount);
                torsSt = 4 - Mathf.CeilToInt(torsHp / inMaxHP * 4f);
            }
            if (organType == 3 || organType == 4)
            {
                legsHp = Mathf.Min(inMaxHP, legsHp + amount);
                legsSt = 4 - Mathf.CeilToInt(legsHp / inMaxHP * 4f);
            }
            if (organType == 5)
            {
                bloodHp = Mathf.Min(inMaxHP, bloodHp + amount);
                bloodSt = 4 - Mathf.CeilToInt(bloodHp / inMaxHP * 4f);
            }
            if (organType == 6)
            {
                manaHp = Mathf.Min(inMaxMana, manaHp + amount);
                manaSt = 4 - Mathf.CeilToInt(manaHp / inMaxMana * 4f);
            }

            RecalculateStats();
        }

        /// <summary>
        /// Fully restore all organs (AS3 Pers.as:1945 healAll).
        /// </summary>
        public void HealAll()
        {
            headHp = inMaxHP;
            torsHp = inMaxHP;
            legsHp = inMaxHP;
            bloodHp = inMaxHP;
            manaHp = inMaxMana;

            headSt = 0;
            torsSt = 0;
            legsSt = 0;
            bloodSt = 0;
            manaSt = 0;

            if (_unitStats != null)
            {
                _unitStats.CurrentHp.Value = maxHp;
                // AS3 healAll (Pers.as:1945) restores the ORGAN tracks only; it does not refill
                // Unit.mana. A full heal therefore leaves the magic budget where it was.
            }

            RecalculateStats();
        }

        /// <summary>
        /// Returns effective skill check value (AS3 Pers.as:2314 getLockTip).
        /// 1=lockpick, 2=hacker, 3=remine, 4=repair weapon, 5=repair armor, 6=signal.
        /// </summary>
        public int GetLockTip(int lockType)
        {
            switch (lockType)
            {
                case 1:
                    return possLockPick > 0 ? lockPick : -100;
                case 2:
                    return hacker;
                case 3:
                    return remine;
                case 4:
                case 5:
                    return repair;
                case 6:
                    return signal;
                default:
                    return 0;
            }
        }

        private void TrackFactor(string statId, string sourceId, string sourceType, float value, float result)
        {
            if (!statFactors.ContainsKey(statId))
                statFactors[statId] = new List<StatFactor>();

            statFactors[statId].Add(new StatFactor
            {
                sourceId = sourceId,
                sourceType = sourceType,
                value = value,
                result = result
            });
        }

        public List<StatFactor> GetFactorsForStat(string statId)
        {
            return statFactors.TryGetValue(statId, out var list) ? list : new List<StatFactor>();
        }

        /// <summary>
        /// Total factor rows recorded by the most recent <see cref="RecalculateStats"/>. The table is
        /// cleared by <see cref="ResetToDefaults"/> at the top of every recalculation, so this is
        /// per-recalculation and not cumulative.
        ///
        /// <para><b>Zero means the engine ran and found nothing</b> — the skills loop iterated an empty
        /// (or unmapped) table and every modifier was dropped. That is the single number that separates
        /// "the engine is live" from "the engine is inert", which was defect 1: the fixes were correct
        /// and had no effect because nothing seeded the table.</para>
        /// </summary>
        public int FactorCount
        {
            get
            {
                int n = 0;
                foreach (var list in statFactors.Values) n += list.Count;
                return n;
            }
        }

        /// <summary>Stat ids that recorded at least one factor in the most recent recalculation.</summary>
        public IEnumerable<string> TrackedStatIds => statFactors.Keys;

        #region Backwards Compatibility Fallbacks

        private void ApplySkillEffectsFallback(string skillId, int level, int tier)
        {
            if (level <= 0) return;

            switch (skillId)
            {
                case "melee":
                    meleeDamMult += tier * 0.1f;
                    break;
                case "smallguns":
                    gunsDamMult += tier * 0.1f;
                    break;
                case "sneak":
                    critCh += level * 0.05f;
                    dexter += level * 0.15f;
                    break;
                case "survival":
                    skin += level;
                    for (int i = 0; i < level; i++) allVulnerMult *= 0.99f;
                    break;
                case "attack":
                    allDamMult += level * 0.05f;
                    break;
                case "defense":
                    for (int i = 0; i < level; i++) allVulnerMult *= 0.97f;
                    break;
            }
        }

        private void ApplyPerkEffectsFallback(string perkId, int rank)
        {
            switch (perkId)
            {
                case "isDJ":
                    isDJ = 1;
                    break;
                case "levitOn":
                    levitOn = 1;
                    break;
                case "telemaster":
                    telemaster = 1;
                    break;
            }
        }

        #endregion

        #region Serialization

        public CharacterSaveData GetSaveData()
        {
            return new CharacterSaveData
            {
                level = level,
                xp = xp,
                skillPoints = skillPoints,
                perkPoints = perkPoints,
                perkPointsExtra = perkPointsExtra,
                skillLevels = new Dictionary<string, int>(skillLevels),
                perkRanks = new Dictionary<string, int>(perkRanks),
                headHp = inMaxHP > 0 ? headHp / inMaxHP : 1f,
                torsHp = inMaxHP > 0 ? torsHp / inMaxHP : 1f,
                legsHp = inMaxHP > 0 ? legsHp / inMaxHP : 1f,
                bloodHp = inMaxHP > 0 ? bloodHp / inMaxHP : 1f,
                manaHp = inMaxMana > 0 ? manaHp / inMaxMana : 1f
            };
        }

        public void LoadSaveData(CharacterSaveData data)
        {
            if (data == null) return;

            level = data.level;
            xp = data.xp;
            skillPoints = data.skillPoints;
            perkPoints = data.perkPoints;
            perkPointsExtra = data.perkPointsExtra;

            skillLevels.Clear();
            if (data.skillLevels != null)
            {
                foreach (var kvp in data.skillLevels)
                    skillLevels[kvp.Key] = kvp.Value;
            }

            perkRanks.Clear();
            if (data.perkRanks != null)
            {
                foreach (var kvp in data.perkRanks)
                    perkRanks[kvp.Key] = kvp.Value;
            }

            RecalculateStats();

            headHp = data.headHp * inMaxHP;
            torsHp = data.torsHp * inMaxHP;
            legsHp = data.legsHp * inMaxHP;
            bloodHp = data.bloodHp * inMaxHP;
            manaHp = data.manaHp * inMaxMana;
        }

        #endregion
    }

    [System.Serializable]
    public class CharacterSaveData
    {
        public int level;
        public int xp;
        public int skillPoints;
        public int perkPoints;
        public int perkPointsExtra;

        public Dictionary<string, int> skillLevels = new Dictionary<string, int>();
        public Dictionary<string, int> perkRanks = new Dictionary<string, int>();

        public float headHp;
        public float torsHp;
        public float legsHp;
        public float bloodHp;
        public float manaHp;
    }
}
