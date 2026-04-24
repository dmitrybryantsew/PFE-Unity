using UnityEngine;

namespace PFE.Core.Messages
{
    #region Input Messages

    /// <summary>
    /// Published when jump input state changes.
    /// </summary>
    public struct JumpMessage
    {
        public bool IsStarted;
    }

    /// <summary>
    /// Published when attack input state changes.
    /// </summary>
    public struct AttackMessage
    {
        public bool IsStarted;
    }

    /// <summary>
    /// Published when interact button is pressed.
    /// </summary>
    public struct InteractMessage
    {
        public bool IsPressed;
    }

    /// <summary>
    /// Published when dash input is triggered.
    /// </summary>
    public struct DashMessage
    {
        public bool IsStarted;
    }

    /// <summary>
    /// Published when teleport key state changes (Q key hold/release).
    /// AS3: charge-based — hold to charge, release to execute.
    /// </summary>
    public struct TeleportMessage
    {
        public bool IsStarted;
    }

    #endregion

    #region Combat Messages

    /// <summary>
    /// Published when a weapon is fired.
    /// </summary>
    public struct WeaponFiredMessage
    {
        public string WeaponId;
        public Vector3 Position;
        public Vector3 Direction;
    }

    /// <summary>
    /// Published when a unit takes damage.
    /// </summary>
    public struct DamageTakenMessage
    {
        public float Damage;
        public Vector3 HitPoint;
        public bool IsCritical;
        public int TargetInstanceId;
    }

    /// <summary>
    /// Published when damage is dealt to a target.
    /// Similar to DamageTakenMessage but from attacker's perspective.
    /// </summary>
    public struct DamageDealtMessage
    {
        public float damage;
        public Vector3 position;
        public bool isCritical;
        public bool isMiss;
    }

    /// <summary>
    /// Published when a unit is healed.
    /// </summary>
    public struct HealMessage
    {
        public float amount;
        public Vector3 position;
    }

    /// <summary>
    /// Published when a weapon starts reloading.
    /// </summary>
    public struct WeaponReloadStartedMessage
    {
        public string WeaponId;
        public float ReloadDuration;
    }

    /// <summary>
    /// Published when a weapon finishes reloading.
    /// </summary>
    public struct WeaponReloadCompletedMessage
    {
        public string WeaponId;
    }

    /// <summary>
    /// Published when weapon durability changes.
    /// </summary>
    public struct WeaponDurabilityChangedMessage
    {
        public string WeaponId;
        public int CurrentDurability;
        public int MaxDurability;
        public float BreakingStatus;
    }

    #endregion

    #region RPG Messages

    /// <summary>
    /// Published when a character levels up.
    /// </summary>
    public struct LevelUpMessage
    {
        public int NewLevel;
        public int SkillPointsGained;
    }

    /// <summary>
    /// Published when a skill level changes.
    /// </summary>
    public struct SkillLevelChangedMessage
    {
        public string SkillId;
        public int NewLevel;
    }

    /// <summary>
    /// Published when character stats change.
    /// </summary>
    public struct StatsChangedMessage
    {
        public int CurrentHp;
        public int MaxHp;
        public int CurrentMana;
        public int MaxMana;
    }

    #endregion
}
