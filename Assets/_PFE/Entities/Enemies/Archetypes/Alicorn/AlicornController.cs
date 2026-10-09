using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Controller for Alicorn / Magic enemy units (AS3 <c>fe/unit/UnitAlicorn.as</c>).
    ///
    /// <para><b>What this class owns, and what it deliberately does not.</b> It owns the shield's
    /// <i>tier constants</i> (<c>shitMaxHp</c>, <c>shitArmor</c>) and its <i>cast timer</i>
    /// (<c>t_shit</c>) — the two things that are per-unit and per-tier. The shield's <i>state</i> lives
    /// on <see cref="UnitStats.ShitHp"/>/<see cref="UnitStats.ShitArmor"/>, and the <i>rule</i> that
    /// consumes it lives in <c>PFE.Systems.Combat.SpellShield</c> plus the damage resolver. That split
    /// is what lets the player's <c>sp_mshit</c> shield, the alicorn's, and the three other shielded
    /// bosses share one implementation.</para>
    ///
    /// <para><b>The ladder itself is in <see cref="AlicornShieldRules"/>, not here.</b> The arithmetic
    /// (<c>t_shit</c> decrement gate, the cast condition, <c>allVulnerMult</c>) is Unity-free, so it can
    /// be pinned by an offline NUnit fixture. This class is a <c>MonoBehaviour</c> and cannot be
    /// constructed outside the editor, so a test that drove it directly would be a guard that never
    /// runs. Everything below reads the stats, asks the rules, and writes the stats back.</para>
    ///
    /// <para><b>An earlier revision kept a private <c>_shieldHp</c> here and absorbed hits in an
    /// overridden <c>TakeDamage</c>/<c>ApplyDamage</c>.</b> That was a second, divergent shield: it
    /// subtracted <c>max(1, damage - shitArmor)</c> instead of the oracle's
    /// <c>shithp -= damage</c> plus <c>reduction += shitArmor</c>, it never set <c>allVulnerMult</c>,
    /// and — because nothing outside this class could see the field — it was invisible to the damage
    /// pipeline, to the HP bar and to every renderer. Both overrides are gone; the resolver does the
    /// arithmetic now and this class only paces the cast.</para>
    /// </summary>
    [RequireComponent(typeof(AlicornBrain))]
    public class AlicornController : EnemyController
    {
        public const string ControllerId = "UnitAlicorn";
        public const string ControllerAlias = "alicorn";

        /// <summary>
        /// AS3 <c>UnitAlicorn.shitMaxHp</c> (<c>:33</c>, default <c>300</c>; <c>:173</c> raises it to
        /// <c>500</c> for <c>tr3</c>) — the pool <see cref="CastShield"/> fills.
        /// </summary>
        /// <remarks>
        /// <c>UnitAlicorn.setLevel()</c> also scales it (<c>:249</c>,
        /// <c>shitMaxHp *= 1 + level * 0.1</c>). <b>That term is not modelled</b>, because the port has
        /// no level for NPCs at all — the same gap <c>UnitController.SkinResistance</c> records for
        /// <c>Unit.setLevel()</c>'s <c>skin *= 1 + level * 0.05</c>. A levelled alicorn would therefore
        /// shield itself for less than the oracle's.
        /// </remarks>
        [SerializeField] private float _shieldMaxHp = 300f;

        /// <summary>
        /// AS3 <c>UnitAlicorn.osob</c> — the third term of the shield timer's decrement condition
        /// (<c>:571</c>: <c>if(aiSpok &gt; 0 || this.tr == 3 &amp;&amp; this.osob || this.t_shit &gt; 150)</c>).
        /// It lets a <c>tr3</c> unit keep counting <c>t_shit</c> down past 150 while calm, so its shield
        /// comes back without it having to notice anything.
        /// </summary>
        /// <remarks>
        /// <para><b>Exposed as a serialized field because the oracle rolls it at construction</b>
        /// (<c>:155/:167/:179</c>: <c>Math.random() &lt; 0.7 / 0.5 / 0.4</c>, forced <c>true</c> on
        /// <c>globalDif == 4</c>), and the port deliberately does not draw from its seeded RNG at spawn
        /// — a spawn that consumed a number would desynchronise every later roll in the room. AS3's
        /// <c>Math.random()</c> is Flash's unseeded global anyway, so there is no stream to reproduce.
        /// Leaving it <c>false</c> gives the 60 %-of-the-time <c>tr3</c> behaviour: the shield still
        /// returns, it just waits for the unit to be aware first.</para>
        /// </remarks>
        [SerializeField] private bool _osob;

        /// <summary>
        /// AS3 <c>UnitAlicorn.t_shit</c> (<c>:111</c>, <c>90</c>; <c>:178</c> sets <c>45</c> on
        /// <c>tr3</c>) — the countdown to the next <see cref="CastShield"/>. Unbounded below, as AS3's
        /// is: <see cref="CastShield"/> rewrites it to <c>1000</c>, and it only stops falling when the
        /// unit is neither aware nor <c>osob</c> and has already passed 150.
        /// </summary>
        private int _tShit = 90;

        /// <summary>
        /// The oracle's <c>tr</c> — <c>UnitAlicorn.as:17</c> — read from the definition id
        /// (<c>alicorn3</c> → 3). <b>0</b> for the family template <c>alicorn</c>, which the oracle
        /// never spawns directly.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the id and not the health.</b> The previous tier test was
        /// <c>definition.health &gt; 300f</c>, and it was wrong in both directions: from
        /// <c>AllData.as:668-683</c> the tiers are <c>alicorn1</c> hp 350, <c>alicorn2</c> hp 250,
        /// <c>alicorn3</c> hp 250 — so the test gave <b>tr1</b> the tr3 shield (500/armour 50) and gave
        /// <b>tr3</b>, the tier that actually carries the second weapon and the big shield, the
        /// ordinary one. Health is not the tier and never was; the id is.</para>
        /// </remarks>
        public int Tier { get; private set; }

        /// <summary>
        /// The tier the shield <b>dome</b> reads — AS3 <c>UnitAlicorn.as:191</c>,
        /// <c>tr == 3 ? visShit2 : visShit</c>. Exposed so <c>UnitShieldOverlay</c> can pick the clip
        /// without knowing what an alicorn is.
        /// </summary>
        public override int ShieldOverlayTier => Tier;

        /// <summary>
        /// Every alicorn tier can raise a shield — <see cref="CastShield"/> is unconditional in the
        /// oracle (<c>UnitAlicorn.as:1087-1093</c>) and <c>t_shit</c> exists on all four rows.
        /// </summary>
        public override bool HasSpellShieldOverlay => true;

        /// <summary>
        /// The shield pool — AS3 <c>Unit.shithp</c>. Answered from <see cref="UnitController.ShieldHp"/>
        /// (<c>UnitStats.ShitHp</c>) rather than from a private field, so there is exactly one pool and
        /// the damage resolver, the HP bar and the renderer all read the same one.
        /// </summary>
        public float MaxShieldHp => _shieldMaxHp;

        /// <summary>
        /// Ticks left on <c>t_shit</c>. Negative is a real state (AS3 never floors it) and means "ready
        /// to cast as soon as the pool empties".
        /// </summary>
        public int ShieldCastTimerTicks => _tShit;

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<AlicornBrain>();
            }
        }

        /// <summary>
        /// Equip the alicorn's weapons, once the definition and the weapon services are both present.
        /// Called from <see cref="Initialize"/> (the normal order — services arrive first) and from
        /// <see cref="OnWeaponServicesReady"/> (a late handover, after <c>Initialize</c> already ran).
        /// </summary>
        /// <remarks>
        /// <para><b>Both guards are load-bearing.</b> <c>RoomUnitSpawner</c> hands the weapon services
        /// over <i>before</i> <c>Initialize</c>, and <c>Faction</c> is derived from <c>Stats</c>
        /// (<c>UnitController.Faction</c>), so equipping on the early hook alone would build a mount
        /// with <c>FactionType.Neutral</c> — a weapon that does not know whose side it is on. Gating on
        /// <c>Stats != null</c> makes the early call a no-op and lets <c>Initialize</c> do the work;
        /// gating on <see cref="EnemyController.HasWeaponServices"/> makes the late call the one that
        /// works. Neither order double-equips, because <c>EquipWeapon</c> is idempotent per id.</para>
        /// </remarks>
        private void EquipAlicornWeapons()
        {
            if (Stats == null || !HasWeaponServices) return;

            // AS3 UnitAlicorn.as:146 — unconditional, on every tier.
            EquipWeapon("alilight");

            // AS3 UnitAlicorn.as:174 — the psy weapon exists only on tr3.
            if (Tier == 3) EquipWeapon("alipsy");
        }

        protected override void OnWeaponServicesReady() => EquipAlicornWeapons();

        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);

            Tier = ParseTier(definition);

            // AS3 UnitAlicorn.as:152/172/173 — shitArmor 25, and 50 with a 500 pool on tr3.
            // AS3 :111/:178 — the cast timer starts at 90, or 45 on tr3.
            // All three come from the rules class, so the numbers the offline fixture pins are the
            // numbers this method actually writes — a duplicated literal here would drift silently.
            _shieldMaxHp = AlicornShieldRules.MaxShieldHp(Tier);
            float shieldArmor = AlicornShieldRules.ShieldArmor(Tier);
            _tShit = AlicornShieldRules.InitialCastTimer(Tier);

            if (stats != null)
            {
                stats.ShitArmor = shieldArmor;

                // AS3 `Unit.as:134` declares `shithp = 0`, and the `UnitAlicorn` constructor never
                // assigns it — the first shield arrives through the `t_shit` ladder in `control()`
                // (:571-578). So an alicorn spawns UNARMOURED and shields itself about 1.5 s after it
                // becomes aware of a target (91 ticks at 60 Hz, from `t_shit = 90`). The port used to
                // hand it a full shield at spawn, which is a different fight: the first hit the player
                // landed was absorbed by a shield that the oracle would not have had up yet.
                stats.ShitHp = 0f;
            }

            EquipAlicornWeapons();
        }

        /// <summary>
        /// Read the oracle's <c>tr</c> from the unit id's trailing digits (<c>alicorn3</c> → 3).
        /// A template id with no digit (<c>alicorn</c>) is tier 0.
        /// </summary>
        private static int ParseTier(UnitDefinition definition)
        {
            string id = definition != null ? definition.id : null;
            if (string.IsNullOrEmpty(id)) return 0;

            int start = id.Length;
            while (start > 0 && char.IsDigit(id[start - 1])) start--;
            if (start == id.Length) return 0;

            return int.TryParse(id.Substring(start), out int tier) ? tier : 0;
        }

        /// <summary>
        /// AS3 <c>UnitAlicorn.control():571-578</c> — the shield's cast timer, and the recast.
        /// </summary>
        /// <remarks>
        /// <para>The oracle, verbatim:</para>
        /// <code>
        /// if(aiSpok &gt; 0 || this.tr == 3 &amp;&amp; this.osob || this.t_shit &gt; 150) --this.t_shit;
        /// if(shithp &lt;= 0 &amp;&amp; this.t_shit &lt;= 0) this.castShit();
        /// </code>
        ///
        /// <para><b>The <c>t_shit &gt; 150</c> term is what makes the timer a countdown rather than a
        /// gate.</b> <c>castShit()</c> writes <c>1000</c>, so the first 850 ticks after a cast run down
        /// regardless of what the unit is doing; below 150 the timer only keeps falling while the unit
        /// is aware (or, on <c>tr3</c>, <c>osob</c>). A shielded alicorn therefore cannot recast sooner
        /// than 850 ticks (~14 s at 60 Hz) after its previous cast, and an alicorn that is never aware
        /// of anything keeps its initial <c>t_shit = 90</c> and never raises a shield at all — which is
        /// the oracle's behaviour, not an omission.</para>
        ///
        /// <para><b><c>allVulnerMult</c> is written here because <c>control()</c> is the only place the
        /// oracle writes it</b> (<c>:587-594</c>): <c>0.6</c> while the pool is positive,
        /// <c>0.4</c> on <c>tr3</c>, and <c>1</c> otherwise. Recomputed every tick rather than latched
        /// on the cast, so it falls back to <c>1</c> on the same frame the pool empties — which is the
        /// frame the shield is supposed to stop helping.</para>
        /// </remarks>
        /// <param name="alerted">
        /// AS3 <c>aiSpok &gt; 0</c> — the unit is currently aware of something
        /// (<c>EnemyBlackboard.AlertTimerTicks</c>). Note this is <i>not</i> "has a target":
        /// <c>aiSpok</c> survives losing sight, which is exactly the window in which a unit that is
        /// searching still re-raises its shield.
        /// </param>
        public void UpdateShieldTick(bool alerted)
        {
            _tShit = AlicornShieldRules.Tick(_tShit, alerted, Tier, _osob);

            if (AlicornShieldRules.ShouldCast(ShieldHp, _tShit)) CastShield();

            AllVulnerabilityMultiplier = AlicornShieldRules.AllVulnerabilityMultiplier(ShieldHp, Tier);
        }

        /// <summary>
        /// AS3 <c>UnitAlicorn.castShit()</c> (<c>:1087-1093</c>) —
        /// <c>curA = 100; shithp = shitMaxHp; t_shit = 1000; visDetails();</c>.
        /// </summary>
        /// <remarks>
        /// <b><c>curA</c> and <c>visDetails()</c> have no counterpart here.</b> <c>curA</c> is the
        /// alicorn's "current action" byte, which also feeds <c>dexter = 2 - curA / 100</c> (<c>:557</c>)
        /// and the animation branches; no unit in the port models it. <c>visDetails()</c> repaints the
        /// unit's HP bar and its shield segment (<c>:1319-1330</c>) — the port's equivalent is the
        /// overlay renderer, which polls the pool itself.
        /// </remarks>
        public void CastShield()
        {
            // `UnitStats`, not `Stats` — the latter is the legacy `UnitController` alias for the
            // *definition* (`UnitDefinition`), which has no pool on it. The shield lives on the stats
            // object, the same one `Initialize` wrote `ShitArmor` into.
            if (UnitStats != null) UnitStats.ShitHp = _shieldMaxHp;
            _tShit = AlicornShieldRules.CastTimerAfterCast;
        }
    }
}
