using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Contract for all weapon family controllers: ranged, magic, melee, thrown, unarmed.
    ///
    /// Each implementation directly mirrors one of the AS3 weapon classes:
    ///   RangedWeaponController  → Weapon.as   (tip 0/2/3)
    ///   MagicWeaponController   → WMagic.as   (tip 5)
    ///   MeleeWeaponController   → WClub.as    (tip 1)
    ///   ThrownWeaponController  → WThrow.as   (tip 4)
    ///   UnarmedWeaponController → WPunch.as   (punch > 0)
    ///
    /// Callers (PlayerWeaponLoadout) call Tick() once per FixedUpdate, then
    /// FlushShotPlans() to collect everything that happened that frame.
    /// </summary>
    public interface IWeaponController : IDisposable
    {
        /// <summary>Mutable runtime state for this weapon instance. Never null after construction.</summary>
        WeaponRuntimeState State { get; }

        /// <summary>
        /// Advance all frame timers and evaluate firing / reload / recharge logic.
        ///
        /// Called once per FixedUpdate (30 fps cadence matching AS3).
        ///
        /// Parameters:
        ///   dt         — fixed delta time in seconds (Time.fixedDeltaTime)
        ///   holdPoint  — current world-space weapon hold position (WeaponMounts.WeaponHoldPoint)
        ///   hornPoint  — current world-space horn/magic position (WeaponMounts.MagicHoldPoint)
        ///   aimTarget  — world-space cursor / AI target position
        ///
        /// After Tick(), call FlushShotPlans() to get all ShotPlans produced this frame.
        /// </summary>
        void Tick(float dt, Vector2 holdPoint, Vector2 hornPoint, Vector2 aimTarget);

        /// <summary>
        /// Signal that the attack button/trigger has been pressed this frame.
        /// Mirrors the start of Weapon.attack() being called.
        /// </summary>
        void BeginAttack();

        /// <summary>Signal that the attack button/trigger has been released.</summary>
        void EndAttack();

        /// <summary>
        /// Request a manual reload.
        /// The controller will start the reload sequence if the magazine is not already full
        /// and no reload is already in progress.
        /// </summary>
        void StartReload();

        /// <summary>
        /// Return all ShotPlans accumulated since the last call and clear the internal list.
        ///
        /// Typical usage:
        ///   controller.Tick(dt, holdPoint, hornPoint, aimTarget);
        ///   foreach (var plan in controller.FlushShotPlans()) { ... }
        /// </summary>
        IReadOnlyList<ShotPlan> FlushShotPlans();
    }
}
