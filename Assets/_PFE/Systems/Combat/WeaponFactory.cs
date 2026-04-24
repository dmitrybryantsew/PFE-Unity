using UnityEngine;
using VContainer;
using VContainer.Unity;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;
using PFE.Systems.Combat;
using PFE.Core.Time;
using PFE.Entities.Units;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Factory for creating weapon instances with proper dependency injection.
    /// This ensures that WeaponLogic receives all its dependencies through DI,
    /// maintaining the purity of the VContainer dependency graph.
    ///
    /// Usage:
    /// Inject IWeaponFactory into your controller and call CreateWeapon().
    /// </summary>
    public interface IWeaponFactory
    {
        /// <summary>
        /// Create a new WeaponLogic instance with all dependencies injected.
        /// </summary>
        WeaponLogic CreateWeaponLogic(WeaponDefinition definition);

        /// <summary>
        /// Create a WeaponView component with proper initialization and injected WeaponLogic.
        /// Note: This requires a parent Transform for the MonoBehaviour component.
        /// </summary>
        WeaponView CreateWeaponView(WeaponDefinition definition, Transform parent, UnitStats ownerStats);
    }

    /// <summary>
    /// Implementation of IWeaponFactory using VContainer's ObjectResolver.
    /// </summary>
    public class WeaponFactory : IWeaponFactory
    {
        private readonly IObjectResolver _resolver;
        private readonly ITimeProvider _timeProvider;
        private readonly ICombatCalculator _combatCalculator;
        private readonly IDurabilitySystem _durabilitySystem;

        public WeaponFactory(
            IObjectResolver resolver,
            ITimeProvider timeProvider,
            ICombatCalculator combatCalculator,
            IDurabilitySystem durabilitySystem)
        {
            _resolver = resolver;
            _timeProvider = timeProvider;
            _combatCalculator = combatCalculator;
            _durabilitySystem = durabilitySystem;
        }

        public WeaponLogic CreateWeaponLogic(WeaponDefinition definition)
        {
            // Create WeaponLogic with all dependencies injected
            return new WeaponLogic(
                definition,
                _timeProvider,
                _combatCalculator,
                _durabilitySystem);
        }

        public WeaponView CreateWeaponView(WeaponDefinition definition, Transform parent, UnitStats ownerStats)
        {
            // Instantiate the prefab
            // Note: This requires a prefab system to be set up
            // For now, we'll create a simple GameObject with WeaponView component
            GameObject weaponObj = new GameObject($"Weapon_{definition.weaponId}");
            weaponObj.transform.SetParent(parent);

            var weaponView = weaponObj.AddComponent<WeaponView>();
            _resolver.Inject(weaponView); // Inject dependencies into the MonoBehaviour

            // Create and inject WeaponLogic
            var weaponLogic = CreateWeaponLogic(definition);
            weaponView.Initialize(weaponLogic, ownerStats);

            return weaponView;
        }
    }
}
