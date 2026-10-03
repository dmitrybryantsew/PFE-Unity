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
    /// Published on <b>both</b> edges of the interact button (E): <c>true</c> when it goes down,
    /// <c>false</c> when it comes back up.
    ///
    /// <para>The release edge is not optional. Interacting is a hold in AS3 — <c>keyAction</c> is a
    /// held boolean and <c>UnitPlayer.as:2131-2135</c> abandons the action the moment it goes false.
    /// A consumer that only reads the press cannot tell "still holding" from "let go early".</para>
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
        public int PerkPointsGained;
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

    /// <summary>
    /// Published when XP is gained. Matches AS3 numbEmit("+Nxp").
    /// </summary>
    public struct XpGainedMessage
    {
        public int Amount;
        public int TotalXp;
        public Vector3 Position;
    }

    /// <summary>
    /// Published when a perk rank is gained.
    /// </summary>
    public struct PerkAddedMessage
    {
        public string PerkId;
        public int Rank;
    }

    /// <summary>
    /// Published when limb trauma stage changes (1=head, 2=torso, 3=legs, 4=blood, 5=mana).
    /// </summary>
    public struct TraumaChangedMessage
    {
        public int BodyPart;
        public int Stage;
    }

    #endregion
}
