using PFE.Data.Definitions;
using PFE.Entities.Enemies;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Pets;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Pets
{
    /// <summary>
    /// The companion unit — a port of AS3 <c>UnitPet</c> (<c>fe/unit/UnitPet.as</c>).
    ///
    /// <para><b>One class, three pets.</b> The oracle constructs <c>UnitPet</c> three times
    /// (<c>UnitPlayer.as:446-465</c>) and the differences are flags, not subclasses; the kind is
    /// therefore a field here too, and it is <b>derived from the unit row's own id</b>
    /// (<c>owl</c>/<c>phoenix</c>/<c>moon</c>) so the spawner needs no extra channel. The rules that
    /// differ — flags, speeds, the stat scaling, the ram, the fly point — all live in
    /// <see cref="PetRules"/> where a fixture can execute them.</para>
    ///
    /// <para><b>Why <see cref="EnemyController"/> and not <see cref="UnitController"/>.</b> The pet is
    /// an <i>ally</i>, but everything it needs to be a body already lives on the enemy controller: the
    /// weapon mounts (<c>owllaser</c>, <c>phoenix</c>), the weapon-service handover the spawner
    /// performs, the death sound, and the brain registration that ticks it. Re-deriving those on a
    /// second base class would be a second copy of the weapon stack. What is <i>not</i> inherited is
    /// every enemy behaviour that matters: the faction is forced to <c>F_PLAYER</c>
    /// (<c>UnitPet.as:61</c>), no loot is dropped, no XP is awarded, and the death is the pet's own
    /// (§4 of the doc) rather than the corpse pipeline.</para>
    ///
    /// <para><b>Two lives, one object.</b> AS3 keeps the pet object across a recall — <c>recall()</c>
    /// only hides it (<c>:472-482</c>) — so HP survives being dismissed and re-summoned. The port does
    /// the same: recall hides the GameObject, and only a room change destroys it (the room's unit list
    /// owns the record). <see cref="PetCompanion"/> carries the HP fraction across that gap, which is
    /// also how the owl's <c>owlhpProc</c> save field is modelled (<c>Pers.as:2369-2381</c>).</para>
    /// </summary>
    [RequireComponent(typeof(PetBrain))]
    public class PetController : EnemyController
    {
        /// <summary>
        /// The <c>controllerId</c> a pet's <see cref="UnitInstance"/> carries so the room spawner
        /// builds this class. Deliberately <b>not</b> the unit id: <c>phoenix</c> already maps to the
        /// wild bird's <c>PhoenixController</c>, and a pet must not be built as the caged prop.
        /// </summary>
        public const string ControllerId = "pet";

        public const string ControllerAlias = "pet";

        // ── AS3 sost, as far as the pet uses it ────────────────────────────────────────────────
        /// <summary>Alive and out — <c>sost == 1</c>.</summary>
        public const int SostAlive = 1;

        /// <summary>
        /// Dead or dismissed — <c>sost == 4</c> (<c>UnitPet.as:107</c> sets it in the constructor, so a
        /// freshly built pet is <i>not</i> out until <c>call()</c> runs).
        /// </summary>
        public const int SostGone = 4;

        private PetKind _kind = PetKind.Phoenix;
        private PlayerController _owner;
        private PetCompanion _companion;
        private SpriteRenderer _body;

        private bool _summoned;
        private int _sost = SostGone;
        private int _level = 1;
        private float _damage;

        /// <summary>Which of the three this is — from the definition's id.</summary>
        public PetKind Kind => _kind;

        /// <summary>AS3 <c>active</c> — set by <c>call()</c> and cleared by <c>recall()</c>.</summary>
        public bool IsSummoned => _summoned;

        /// <summary>AS3 <c>sost</c>. <see cref="SostAlive"/> or <see cref="SostGone"/>.</summary>
        public int Sost => _sost;

        /// <summary>The player this pet belongs to, or null before the companion hands it over.</summary>
        public PlayerController Owner => _owner;

        /// <summary>The companion that summons, recalls and revives this pet.</summary>
        public PetCompanion Companion => _companion;

        /// <summary>Live HP. Zero while dead.</summary>
        public float Hp => _unitStats != null ? _unitStats.CurrentHp.Value : 0f;

        /// <summary>AS3 <c>maxhp</c> after <c>setLevel</c>.</summary>
        public float MaxHp => _unitStats != null ? _unitStats.MaxHp.Value : 0f;

        /// <summary>
        /// AS3 <c>dam</c> after <c>setLevel</c> — the pet's own damage number, which is <b>not</b> the
        /// row's <c>&lt;comb damage&gt;</c> (<c>UnitPet.as:206-222</c> overwrites it).
        /// </summary>
        public float PetDamage => _damage;

        /// <summary>The player level the stats were scaled for.</summary>
        public int PetLevel => _level;

        /// <summary>AS3 <c>isFly = true</c> (<c>UnitPet.as:75</c>) — gravity is off for all three.</summary>
        protected override bool IsFlying => true;

        /// <summary>
        /// AS3 <c>UnitPlayer</c> does not drop loot and neither does a pet: <c>UnitPet.die()</c>
        /// (<c>:484-520</c>) emits a particle burst and hides the body. This also keeps the death out of
        /// the loot tables entirely rather than relying on <c>owl</c> naming no table.
        /// </summary>
        protected override bool DropsLootOnDeath => false;

        /// <summary>
        /// The pet's own death, <b>instead of</b> the enemy one — <c>UnitPet.die()</c> (<c>:484-520</c>).
        ///
        /// <para><b>Why <c>base</c> is not called.</b> <c>UnitController.OnDeath</c> awards XP to the
        /// player (<c>Unit.as:4397</c>), drops loot and hands the body to <c>EnterCorpse</c>. Every one
        /// of those is wrong here: a pet dying must not pay the player for its own death, must not leave
        /// a dissolving body, and must stay resurrectable — which the corpse pipeline would make
        /// impossible. The oracle's own <c>die()</c> for a pet does none of it either.</para>
        /// </summary>
        protected override void OnDeath()
        {
            if (_sost >= SostAlive + 1) return;

            // `if(optUncall) { recall(); gg.pet = null; currentPet = ""; return; }` — the moon only.
            if (PetRules.UncallsOnDeath(_kind))
            {
                Recall();
                _companion?.OnPetUncalled(this);
                return;
            }

            // `visible = false; hp = 0; poison = stun = cut = 0; sost = 4; expl();` — the HP is already
            // zero (that is what raised this death), so only the body and the state are left.
            SetBodyVisible(false);
            _sost = SostGone;
            _companion?.OnPetDied(this);
        }

        /// <summary>
        /// A pet that is not out cannot be hit — AS3's <c>attKorp</c> refuses from a body whose
        /// <c>sost &gt; 1</c> (<c>Unit.as:3269</c>), and a hidden pet is not a body at all.
        ///
        /// <para><b>Why this has to be explicit.</b> Without it a stray round would drive the pet's HP
        /// below zero through <c>ApplyCorpseDamage</c>, which destroys the body at overkill — and a
        /// destroyed body cannot be resurrected, so the phoenix would lose its automatic revive to
        /// splash damage it never should have taken.</para>
        /// </summary>
        public override void TakeDamage(float damage)
        {
            if (_sost >= SostAlive + 1) return;
            base.TakeDamage(damage);
        }

        /// <inheritdoc cref="TakeDamage"/>
        public override bool ApplyDamage(in PFE.Systems.Combat.DamageOutcome outcome)
        {
            if (_sost >= SostAlive + 1) return false;
            return base.ApplyDamage(outcome);
        }

        protected override void Awake()
        {
            base.Awake();

            // `this.fraction = F_PLAYER` (`UnitPet.as:61`) — the row says `fraction='1'` (monster), and
            // the oracle overwrites it. Written through the placement layer so the shared asset is not
            // reprogrammed for every other user of the owl row.
            SetRuntimeFaction(FactionType.Player);
        }

        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);

            // The kind is the row's own id — the three rows are `phoenix`, `owl` and `moon`.
            if (definition != null && PetIds.TryParse(definition.id, out PetKind kind))
            {
                _kind = kind;
            }

            SetRuntimeFaction(FactionType.Player);
            EquipPetWeapon();
            RefreshStats();
        }

        /// <summary>
        /// Hand the pet its player. Called by <see cref="PetSpawner"/> immediately after the spawner
        /// returns the controller, i.e. after <see cref="Initialize"/> — which is why the stats are
        /// re-applied here rather than only there.
        /// </summary>
        public void SetOwner(PlayerController owner, PetCompanion companion)
        {
            _owner = owner;
            _companion = companion;
            RefreshStats();
        }

        // ── setLevel (UnitPet.as:199-225) ──────────────────────────────────────────────────────────

        /// <summary>
        /// <c>UnitPet.setLevel(pers.level)</c> — the pet's whole stat block, recomputed from the
        /// player's own pet numbers. Runs on every summon, every resurrect and every player level-up
        /// (<c>Pers.as:1135-1137</c>).
        ///
        /// <para><b>The ratio survives.</b> <c>hp = (hp / maxhp) * maxhp</c> (<c>:202</c>) means a
        /// level-up heals proportionally rather than resetting or capping: a pet at half health stays at
        /// half health with a bigger ceiling. <see cref="PetRules.RescaleCurrentHp"/> is that line.</para>
        /// </summary>
        public void RefreshStats()
        {
            if (_unitStats == null) return;

            PetStatInput input = ReadStatInput();
            _level = _owner != null && _owner.CharacterStats != null ? _owner.CharacterStats.Level : 1;

            PetCombatStats scaled = PetRules.ScaleStats(_kind, _level, input);

            float oldMax = _unitStats.MaxHp.Value;
            float oldHp = _unitStats.CurrentHp.Value;

            _unitStats.MaxHp.Value = scaled.MaxHp;

            // A dead pet stays dead across a re-scale; only a living one keeps its ratio.
            _unitStats.CurrentHp.Value = oldHp > 0f
                ? PetRules.RescaleCurrentHp(oldHp, oldMax, scaled.MaxHp)
                : 0f;

            _unitStats.skinResistance = scaled.Skin;
            AllVulnerabilityMultiplier = scaled.AllVulnerabilityMultiplier;
            _damage = scaled.Damage;

            ApplyConstructorVulnerability();
            ApplyWeaponDamage();
        }

        /// <summary>
        /// The player's pet stats, or the oracle's defaults when there is no player yet.
        ///
        /// <para><see cref="PetStatInput"/> is a snapshot by design — see its remarks — so this is the
        /// one place the live <c>CharacterStats</c> is read.</para>
        /// </summary>
        PetStatInput ReadStatInput()
        {
            PFE.Systems.RPG.CharacterStats stats = _owner != null ? _owner.CharacterStats : null;
            if (stats == null) return PetStatInput.Defaults;

            return new PetStatInput(
                petHp: stats.petHP,
                petDamage: stats.petDam,
                petSkin: stats.petSkin,
                petVulnerability: stats.petVulner,
                owlHp: stats.owlHP,
                owlDamage: stats.owlDam,
                owlSkin: stats.owlSkin,
                owlVulnerability: stats.owlVulner,
                moonHp: stats.moonHP,
                moonDamage: stats.moonDam,
                spellPower: stats.spellPower);
        }

        /// <summary>
        /// The constructor's one vulnerability write — <c>vulner[D_NECRO] = 0.2</c> for the owl and
        /// <c>0.5</c> for the moon (<c>UnitPet.as:96, :102</c>). The phoenix's table is untouched.
        /// </summary>
        void ApplyConstructorVulnerability()
        {
            float necro = PetRules.ConstructorNecroticVulnerability(_kind);
            if (necro < 0f) return;

            VulnerabilityData table = _unitStats.Vulnerabilities;
            table.SetVulnerability(DamageType.Necrotic, necro);
            _unitStats.OverrideVulnerabilityBaseline(table);
        }

        // ── The weapon (UnitPet.as:227-241) ────────────────────────────────────────────────────────

        protected override void OnWeaponServicesReady()
        {
            base.OnWeaponServicesReady();
            EquipPetWeapon();
        }

        /// <summary>
        /// Equip the row's own weapon — <c>owllaser</c> for the owl, <c>phoenix</c> for the bird, none
        /// for the moon. Idempotent, so the two arrival orders (services before <c>Initialize</c> and
        /// after) both end with one mount.
        /// </summary>
        void EquipPetWeapon()
        {
            UnitDefinition definition = Definition;
            if (definition == null || definition.weapons == null || definition.weapons.Length == 0) return;

            for (int i = 0; i < definition.weapons.Length; i++)
            {
                string weaponId = definition.weapons[i].weaponId;
                if (string.IsNullOrEmpty(weaponId)) continue;

                EnemyWeaponMount mount = EquipWeapon(weaponId);
                if (mount != null)
                {
                    mount.HoldRule = WeaponHoldRule.Base;
                    ApplyWeaponDamage();
                }

                // The oracle's `currentWeapon` is singular; a pet row authors at most one.
                return;
            }
        }

        /// <summary>
        /// AS3's <c>currentWeapon.damage = pers.petDam * (1 + level * 0.1)</c> — an <b>assignment</b>
        /// of the pet's damage, not the weapon row's own number.
        ///
        /// <para>Expressed as <c>State.DamageScale</c> (the per-instance factor the ranged controller
        /// multiplies into the finished product) because the weapon definition is a shared asset. The
        /// scale is the ratio between the two numbers, so a row whose <c>dam</c> is zero falls back to
        /// 1 — no weapon in the three pet rows is, and a silent 0 damage would be worse than a
        /// conservative one.</para>
        /// </summary>
        void ApplyWeaponDamage()
        {
            if (_damage <= 0f) return;

            EnemyWeaponMount mount = PrimaryWeaponMount();
            if (mount == null || mount.Controller == null || mount.Definition == null) return;

            float baseDamage = mount.Definition.baseDamage;
            mount.Controller.State.DamageScale = baseDamage > 0.0001f ? _damage / baseDamage : 1f;
        }

        /// <summary>The pet's one weapon mount, or null for the moon.</summary>
        public EnemyWeaponMount PrimaryWeaponMount()
        {
            UnitDefinition definition = Definition;
            if (definition == null || definition.weapons == null) return null;

            for (int i = 0; i < definition.weapons.Length; i++)
            {
                string weaponId = definition.weapons[i].weaponId;
                if (string.IsNullOrEmpty(weaponId)) continue;

                EnemyWeaponMount mount = GetWeaponMount(weaponId);
                if (mount != null) return mount;
            }

            return null;
        }

        /// <summary>True when this pet shoots rather than rams (<c>currentWeapon != null</c>).</summary>
        public bool HasWeapon => PrimaryWeaponMount() != null;

        /// <summary>
        /// Move the barrel to the oracle's own hold point — <c>weaponX = X + 15 * storona,
        /// weaponY = Y - 20</c> for the phoenix, <c>11 / 18</c> for the other two
        /// (<c>UnitPet.as:227-241</c>).
        ///
        /// <para>Written every tick because the point is <b>facing-dependent</b> and the mount's
        /// <c>HoldOffsetPixels</c> is a static offset: a baked value would leave the barrel on the wrong
        /// side of a pet that turned round.</para>
        /// </summary>
        public void UpdateWeaponHoldPoint()
        {
            EnemyWeaponMount mount = PrimaryWeaponMount();
            if (mount == null) return;

            PetPoint hold = PetRules.WeaponHoldPoint(_kind, 0f, 0f, FacingDirection);
            mount.HoldOffsetPixels = new Vector2(hold.X, -hold.Y);
        }

        // ── call / recall (UnitPet.as:446-482) ─────────────────────────────────────────────────────

        /// <summary>
        /// <c>UnitPet.call()</c> (<c>:446-470</c>): show the body, fade it in, reset the AI and re-scale
        /// the stats.
        /// </summary>
        public void Call()
        {
            _summoned = true;

            // `if(hp <= 0 && !optAutores) return;` — a dead owl or moon cannot be called back into
            // existence; only the phoenix has a body left to restore.
            if (Hp <= 0f && !PetRules.AutoResurrects(_kind)) return;

            if (_sost == SostGone && PetRules.AutoResurrects(_kind))
            {
                Resurrect();
            }

            _sost = SostAlive;
            SetBodyVisible(true);
            PetBrain brain = Brain as PetBrain;
            brain?.OnSummoned();
            RefreshStats();
            SnapToPlayer();
        }

        /// <summary><c>UnitPet.recall()</c> (<c>:472-482</c>) — hide, keep the HP.</summary>
        public void Recall()
        {
            _summoned = false;
            _sost = SostGone;
            SetBodyVisible(false);
            (Brain as PetBrain)?.OnRecalled();
        }

        /// <summary>
        /// <c>UnitPet.resurrect()</c> (<c>:522-541</c>) — <c>hp = min(100, maxhp / 2)</c>, back at the
        /// player, visible, state 1.
        /// </summary>
        public void Resurrect()
        {
            if (_unitStats == null) return;

            _unitStats.CurrentHp.Value = PetRules.ResurrectHp(MaxHp);
            _sost = SostAlive;
            _summoned = true;

            // The death latch has to be released or the NEXT death would be ignored: RaiseDeath's guard
            // is `_deathRaised && !IsAlive`, and a resurrected pet is alive again while the flag is
            // still set from the previous death. See UnitController.RaiseDeath.
            _deathRaised = false;

            RefreshStats();
            SnapToPlayer();
            SetBodyVisible(true);
            (Brain as PetBrain)?.OnSummoned();
        }

        /// <summary>Put the body on the player's own feet — the oracle's <c>X = gg.X; Y = gg.Y</c>.</summary>
        public void SnapToPlayer()
        {
            if (_owner == null) return;

            Vector3 target = _owner.transform.position;
            transform.position = new Vector3(target.x, target.y, transform.position.z);
        }

        /// <summary>
        /// Put the live HP at a fraction of the maximum — how a pet rebuilt after a room change comes
        /// back with the health it had, and how a loaded save restores the owl (<c>Pers.owlhpProc</c>).
        ///
        /// <para><b>A fraction of zero kills it; any other fraction does not summon it.</b> That is the
        /// oracle's own split at <c>UnitPlayer.as:456-460</c>: <c>hp = maxhp * owlhpProc</c>, then
        /// <c>if(owlhpProc &lt;= 0) sost = 4</c>. A rebuilt owl at 40 % health is alive-but-dismissed
        /// until something calls it, exactly as a freshly constructed one is (<c>sost = 4</c> in
        /// <c>UnitPet.as:107</c>). Making this method also set <c>sost = 1</c> would re-summon a pet the
        /// player had recalled.</para>
        ///
        /// <para><b>Why this has to exist at all.</b> <see cref="PetSpawner"/> builds every unit at full
        /// health (<c>currentHealth = definition.health</c>), so without this call a room change heals
        /// the pet and a pet saved dead comes back alive.</para>
        /// </summary>
        public void SetHpFraction(float fraction)
        {
            if (_unitStats == null) return;

            float clamped = Mathf.Clamp01(fraction);
            _unitStats.CurrentHp.Value = MaxHp * clamped;

            if (clamped <= 0f)
            {
                _sost = SostGone;
                _summoned = false;
                SetBodyVisible(false);
            }
        }

        // ── heal / repair (UnitPet.as:162-186, :543-573) ───────────────────────────────────────────

        /// <summary>
        /// <c>UnitPet.heal(amount, mode, showNumber)</c>. <paramref name="mode"/> 1 is the radiation
        /// path, and it <b>only heals a living phoenix</b> — the owl and the moon get nothing from the
        /// player's exposure, and a dead bird waits for its cooldown (<c>:164-167</c>).
        /// </summary>
        /// <returns>True when HP actually changed.</returns>
        public bool Heal(float amount, int mode = 0)
        {
            if (_unitStats == null || amount <= 0f) return false;
            if (mode == 1 && !PetRules.RadiationHealAllowed(_kind, _sost)) return false;
            if (!_summoned) return false;

            float before = _unitStats.CurrentHp.Value;
            _unitStats.CurrentHp.Value = PetRules.HealedHp(before, MaxHp, amount);
            return _unitStats.CurrentHp.Value > before;
        }

        /// <summary>
        /// <c>UnitPet.repair(amount)</c> (<c>:543-573</c>) — the workbench/vendor path: heals without a
        /// floating number, and is the <b>only</b> way to bring a dead owl back.
        ///
        /// <para><b>The revive is gated on the owl being the summoned pet</b> — <c>if(sost == 4 &amp;&amp;
        /// gg.currentPet == id)</c>. A recalled owl that gets repaired stays recalled: the money buys
        /// its health back, not its presence. That decision belongs to
        /// <see cref="PetCompanion.RepairOwl"/>, which is the only caller that knows the answer.</para>
        /// </summary>
        public void Repair(float amount, bool reviveIfDead)
        {
            if (_unitStats == null || amount <= 0f) return;

            if (_sost == SostGone)
            {
                if (!reviveIfDead) return;

                _unitStats.CurrentHp.Value = 0f;
                _unitStats.CurrentHp.Value = PetRules.HealedHp(0f, MaxHp, amount);
                _sost = SostAlive;
                _summoned = true;
                _deathRaised = false;
                RefreshStats();
                SetBodyVisible(true);
                (Brain as PetBrain)?.OnSummoned();
                return;
            }

            _unitStats.CurrentHp.Value = PetRules.HealedHp(_unitStats.CurrentHp.Value, MaxHp, amount);
        }

        // ── The body ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Show or hide the sprite. AS3 <c>visible</c>; the GameObject stays for the HP.</summary>
        public void SetBodyVisible(bool visible)
        {
            if (_body == null)
            {
                _body = GetComponentInChildren<SpriteRenderer>(true);
            }

            if (_body != null)
            {
                _body.enabled = visible;
            }
        }

        /// <summary>
        /// The summon fade — <c>oduplenie</c> drives the alpha for 30 frames (<c>:245-256</c>).
        /// </summary>
        public void ApplySummonAlpha(int oduplenie)
        {
            if (_body == null)
            {
                _body = GetComponentInChildren<SpriteRenderer>(true);
            }

            if (_body == null) return;

            Color colour = _body.color;
            colour.a = PetRules.SummonAlpha(oduplenie);
            _body.color = colour;
        }
    }
}
