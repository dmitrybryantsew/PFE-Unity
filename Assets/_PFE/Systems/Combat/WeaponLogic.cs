using UnityEngine;
using R3;
using Cysharp.Threading.Tasks;
using System;
using System.Threading;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Core.Time;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Controls weapon behavior including firing, accuracy, durability.
    /// Handles rapid fire, burst fire, reload, and degradation.
    /// Based on docs/task1_core_mechanics/02_combat_logic.md
    /// Renamed from WeaponController to WeaponLogic to avoid naming conflict.
    /// </summary>
    public class WeaponLogic
    {
        private readonly WeaponDefinition weaponDef;
        private readonly ITimeProvider _timeProvider;
        private readonly ICombatCalculator _combatCalculator;
        private readonly IDurabilitySystem _durabilitySystem;

        private int currentDurability;
        private int currentAmmo;
        private float lastFireTime = -999f;  // Initialize to past to allow first shot
        private int burstShotsFired;

        // Reactive properties for UI binding
        public ReactiveProperty<int> CurrentAmmo { get; private set; }
        public ReactiveProperty<int> CurrentDurability { get; private set; }
        public ReactiveProperty<bool> IsReloading { get; private set; }
        public ReactiveProperty<float> ReloadProgress { get; private set; }

        public WeaponDefinition WeaponDef => weaponDef;
        public bool IsEmpty => weaponDef.magazineSize > 0 && currentAmmo <= 0;
        public bool IsBroken => currentDurability <= 0;

        public WeaponLogic(WeaponDefinition definition, ITimeProvider timeProvider, ICombatCalculator combatCalculator, IDurabilitySystem durabilitySystem)
        {
            if (definition == null)
                throw new System.ArgumentNullException(nameof(definition), "Weapon definition cannot be null");

            weaponDef = definition;
            _timeProvider = timeProvider;
            _combatCalculator = combatCalculator;
            _durabilitySystem = durabilitySystem;
            currentDurability = definition.maxDurability;
            currentAmmo = definition.magazineSize;

            CurrentAmmo = new ReactiveProperty<int>(currentAmmo);
            CurrentDurability = new ReactiveProperty<int>(currentDurability);
            IsReloading = new ReactiveProperty<bool>(false);
            ReloadProgress = new ReactiveProperty<float>(0f);
        }

        /// <summary>
        /// Attempt to fire the weapon.
        /// Returns true if weapon fired successfully.
        /// </summary>
        public bool Fire(UnitStats ownerStats)
        {
            // Check if can fire
            if (IsReloading.Value)
                return false;

            if (weaponDef.magazineSize > 0 && currentAmmo <= 0)
                return false;

            if (IsBroken)
                return false;

            // Check fire rate cooldown
            float cooldown = _combatCalculator.FramesToSeconds(weaponDef.rapid);
            if (_timeProvider.CurrentTime - lastFireTime < cooldown)
                return false;

            // Check for jam/misfire
            float breaking = _combatCalculator.CalculateBreaking(
                weaponDef.maxDurability,
                currentDurability);

            if (CheckJam(breaking, weaponDef.magazineSize))
            {
                // Weapon jammed
                return false;
            }

            if (CheckMisfire(breaking))
            {
                // Misfire - consumes ammo but deals no damage
                ConsumeAmmo(1);
                ConsumeDurability(1);
                lastFireTime = _timeProvider.CurrentTime;
                return false;
            }

            // Fire weapon
            int shotsToFire = weaponDef.burstCount > 0 ? weaponDef.burstCount : 1;

            for (int i = 0; i < shotsToFire; i++)
            {
                if (currentAmmo > 0)
                {
                    ConsumeAmmo(1);
                    ConsumeDurability(1);
                    burstShotsFired++;
                }
            }

            lastFireTime = _timeProvider.CurrentTime;
            return true;
        }

        /// <summary>
        /// Calculate accuracy deviation for this shot.
        /// Returns deviation angle in degrees.
        /// </summary>
        public float CalculateDeviation(UnitStats ownerStats, float weaponSkill = 1f)
        {
            float breaking = _combatCalculator.CalculateBreaking(
                weaponDef.maxDurability,
                currentDurability);

            return _combatCalculator.CalculateEffectiveDeviation(
                weaponDef.deviation,
                breaking,
                1f, // skillConf
                weaponSkill,
                0f); // mazil
        }

        /// <summary>
        /// Check if weapon jams (fails to fire).
        /// Jam chance increases as durability decreases.
        /// </summary>
        private bool CheckJam(float breaking, int magazineSize)
        {
            float jamChance = _combatCalculator.CalculateJamChance(breaking, magazineSize);
            return UnityEngine.Random.value < jamChance;
        }

        /// <summary>
        /// Check if weapon misfires (consumes ammo, no damage).
        /// Misfire chance increases as durability decreases.
        /// </summary>
        private bool CheckMisfire(float breaking)
        {
            float misfireChance = _combatCalculator.CalculateMisfireChance(breaking);
            return UnityEngine.Random.value < misfireChance;
        }

        /// <summary>
        /// Consume ammunition from magazine.
        /// </summary>
        private void ConsumeAmmo(int amount)
        {
            if (weaponDef.magazineSize <= 0)
                return;

            currentAmmo = Mathf.Max(0, currentAmmo - amount);
            CurrentAmmo.Value = currentAmmo;
        }

        /// <summary>
        /// Consume weapon durability.
        /// Each shot costs 1 + ammoHP durability.
        /// </summary>
        private void ConsumeDurability(int amount = 1)
        {
            currentDurability = Mathf.Max(0, currentDurability - amount);
            CurrentDurability.Value = currentDurability;
        }

        /// <summary>
        /// Start reload sequence using UniTask for async delay.
        /// </summary>
        public async UniTaskVoid StartReloadAsync(CancellationToken cancellationToken = default)
        {
            if (weaponDef.magazineSize <= 0)
                return;

            if (IsReloading.Value)
                return;

            if (currentAmmo >= weaponDef.magazineSize)
                return;

            IsReloading.Value = true;

            // Reload time in seconds
            float reloadTime = _combatCalculator.FramesToSeconds(weaponDef.reloadTime);

            try
            {
                // Update reload progress
                float startTime = _timeProvider.CurrentTime;
                while (_timeProvider.CurrentTime - startTime < reloadTime)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        IsReloading.Value = false;
                        ReloadProgress.Value = 0f;
                        return;
                    }

                    float elapsed = _timeProvider.CurrentTime - startTime;
                    ReloadProgress.Value = elapsed / reloadTime;

                    // Wait for next frame
                    await UniTask.Yield(cancellationToken);
                }

                // Reload complete
                CompleteReload();
            }
            catch (OperationCanceledException)
            {
                // Reload was cancelled
                IsReloading.Value = false;
                ReloadProgress.Value = 0f;
            }
        }

        /// <summary>
        /// Complete reload and refill magazine.
        /// </summary>
        public void CompleteReload()
        {
            currentAmmo = weaponDef.magazineSize;
            CurrentAmmo.Value = currentAmmo;
            IsReloading.Value = false;
            ReloadProgress.Value = 0f;
        }

        /// <summary>
        /// Repair weapon durability.
        /// </summary>
        public void Repair(int amount)
        {
            currentDurability = Mathf.Min(weaponDef.maxDurability, currentDurability + amount);
            CurrentDurability.Value = currentDurability;
        }

        /// <summary>
        /// Set durability directly (for testing).
        /// </summary>
        public void SetDurability(int amount)
        {
            currentDurability = Mathf.Clamp(amount, 0, weaponDef.maxDurability);
            CurrentDurability.Value = currentDurability;
        }

        /// <summary>
        /// Set ammo directly (for testing).
        /// </summary>
        public void SetAmmo(int amount)
        {
            currentAmmo = Mathf.Clamp(amount, 0, weaponDef.magazineSize);
            CurrentAmmo.Value = currentAmmo;
        }

        /// <summary>
        /// Reset weapon to full condition.
        /// </summary>
        public void Reset()
        {
            currentDurability = weaponDef.maxDurability;
            currentAmmo = weaponDef.magazineSize;
            CurrentDurability.Value = currentDurability;
            CurrentAmmo.Value = currentAmmo;
            IsReloading.Value = false;
            burstShotsFired = 0;
        }

        /// <summary>
        /// Get fire rate in shots per second.
        /// </summary>
        public float GetFireRate()
        {
            return _combatCalculator.CalculateFireRate(weaponDef.rapid);
        }
    }
}
