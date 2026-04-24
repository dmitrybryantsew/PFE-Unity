using PFE.Data.Definitions;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Interface for critical hit calculations including backstab and absolute pierce.
    /// </summary>
    public interface ICriticalHitSystem
    {
        float CalculateCriticalChance(WeaponDefinition weaponDef, UnitStats ownerStats, float additionalChance = 0f);
        float CalculateCriticalMultiplier(WeaponDefinition weaponDef, UnitStats ownerStats, float additionalMultiplier = 0f);
        bool RollCriticalHit(float critChance);
        bool IsBackstab(Vector3 attackerPosition, Vector3 targetPosition, Vector3 targetForward, float backstabAngle = 90f);
        float CalculateBackstabMultiplier(bool isBackstab);
        bool RollAbsolutePierce(float absolutePierceChance);
        float ApplyCriticalAndBackstab(float baseDamage, float critMultiplier, bool isCrit, bool isBackstab);
    }
}
