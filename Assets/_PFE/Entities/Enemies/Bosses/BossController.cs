using UnityEngine;

namespace PFE.Entities.Enemies.Bosses
{
    [RequireComponent(typeof(BossBrain))]
    public class BossController : EnemyController
    {
        public const string ControllerId = "UnitBoss";
        public const string ControllerAlias = "boss";

        public BossBrain BossBrain => _brain as BossBrain;

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<BossBrain>();
            }
        }

        protected override void OnDeath()
        {
            if (BossBrain != null && BossBrain.TryReviveSecondLife())
            {
                if (_unitStats != null)
                {
                    _unitStats.CurrentHp.Value = Mathf.Max(1f, _unitStats.MaxHp.Value - 0.1f);
                }
                _brain.SetState(EnemyAIState.CombatChase);
                return;
            }

            base.OnDeath();
        }
    }

    [RequireComponent(typeof(BossRaiderBrain))]
    public class BossRaiderController : BossController
    {
        public new const string ControllerId = "UnitBossRaider";
        public new const string ControllerAlias = "bossraider";

        public int ReinforcementsSpawned { get; private set; }

        public virtual void SpawnReinforcementWave(int quarter)
        {
            ReinforcementsSpawned++;
        }
    }

    [RequireComponent(typeof(BossEnclBrain))]
    public class BossEnclController : BossController
    {
        public new const string ControllerId = "UnitBossEncl";
        public new const string ControllerAlias = "bossencl";
    }

    [RequireComponent(typeof(BossNecrBrain))]
    public class BossNecrController : BossController
    {
        public new const string ControllerId = "UnitBossNecr";
        public new const string ControllerAlias = "bossnecr";
    }

    [RequireComponent(typeof(BossAlicornBrain))]
    public class BossAlicornController : BossController
    {
        public new const string ControllerId = "UnitBossAlicorn";
        public new const string ControllerAlias = "bossalicorn";

        /// <summary>
        /// <b>Deliberately NOT a spell-shield dome.</b> <c>UnitBossAlicorn</c> looks like the other
        /// shield bosses — it declares <c>shitMaxHp = 400</c> (<c>:32</c>), <c>t_shit = 300</c>
        /// (<c>:105</c>), <c>shitArmor = 15</c> (<c>:119</c>) and creates the <c>visShit3</c> dome
        /// (<c>:123-127</c>) — but <b>nothing in that class ever assigns <c>shithp</c></b>, and
        /// <c>t_shit</c> is only ever decremented (<c>:374-376</c>), never read. Every one of those four
        /// fields is vestigial: <c>Unit.damage()</c> reaches <c>shitArmor</c> only inside
        /// <c>if(shithp &gt; 0)</c>, which never holds, so the boss absorbs nothing.
        ///
        /// <para><b>Its dome is a one-shot, not a pool.</b> <c>die():709-712</c> is the only place the
        /// clip is touched after construction: <c>if(isShit) { isShit = false;
        /// visshit.gotoAndPlay(2); hp = maxhp - 0.1; ... }</c> — the first lethal hit shatters the dome
        /// and revives the boss. <c>setNull():203-207</c> restores <c>isShit = true</c> on a respawn.
        /// That state is already modelled (<see cref="BossAlicornBrain.IsShit"/>); the <i>visual</i> is
        /// not, and it needs a play-once path, not the pool poll <c>UnitShieldOverlay</c> implements.
        /// Returning <c>true</c> here would attach a dome that can never draw.</para>
        /// </summary>
        public override bool UsesBossShieldOverlay => false;

        /// <summary>See <see cref="UsesBossShieldOverlay"/> — the boss alicorn has no shield pool.</summary>
        public override bool HasSpellShieldOverlay => false;
    }

    [RequireComponent(typeof(BossDronBrain))]
    public class BossDronController : BossController
    {
        public new const string ControllerId = "UnitBossDron";
        public new const string ControllerAlias = "bossdron";
    }

    [RequireComponent(typeof(BossUltraBrain))]
    public class BossUltraController : BossController
    {
        public new const string ControllerId = "UnitBossUltra";
        public new const string ControllerAlias = "bossultra";

        public override void TakeDamage(float damage)
        {
            if (BossBrain is BossUltraBrain ultra && ultra.ShieldHp > 0f)
            {
                float absorbed = Mathf.Min(ultra.ShieldHp, damage);
                ultra.ShieldHp -= absorbed;
                damage -= absorbed;
                if (damage <= 0f) return;
            }
            base.TakeDamage(damage);
        }

        public override bool ApplyDamage(in PFE.Systems.Combat.DamageOutcome outcome)
        {
            if (BossBrain is BossUltraBrain ultra && ultra.ShieldHp > 0f)
            {
                float absorbed = Mathf.Min(ultra.ShieldHp, outcome.HpDamage);
                ultra.ShieldHp -= absorbed;
                if (ultra.ShieldHp > 0f) return false;
            }
            return base.ApplyDamage(outcome);
        }
    }

    [RequireComponent(typeof(ThunderHeadBrain))]
    public class ThunderHeadController : BossController
    {
        public new const string ControllerId = "UnitThunderHead";
        public new const string ControllerAlias = "thunderhead";
    }

    public class ThunderTurretController : EnemyController
    {
        public const string ControllerId = "UnitThunderTurret";
        public const string ControllerAlias = "ttur";
    }
}
