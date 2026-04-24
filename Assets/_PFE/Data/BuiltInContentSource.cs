using UnityEngine;
using PFE.ModAPI;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using RPGPerk = PFE.Systems.RPG.Data.PerkDefinition;
using RPGSkill = PFE.Systems.RPG.Data.SkillDefinition;

namespace PFE.Data
{
    /// <summary>
    /// Content source for the base game ("pfe.base").
    /// Wraps Resources.LoadAll calls — the base game is the first mod.
    /// As content migrates to Addressables, this source will adapt internally
    /// without changing the IContentSource contract.
    /// </summary>
    public class BuiltInContentSource : IContentSource
    {
        readonly ModManifest _manifest;

        public ModManifest Manifest => _manifest;

        public BuiltInContentSource()
        {
            _manifest = ModManifest.CreateBaseGame();
        }

        public void RegisterContent(IContentRegistry registry)
        {
            RegisterType<UnitDefinition>(registry, "Units", "");
            RegisterType<WeaponDefinition>(registry, "Weapons", "");
            RegisterType<RoomTemplate>(registry, "Rooms", "");
            RegisterType<CharacterAnimationDefinition>(registry, "Characters", "");
            RegisterType<ItemDefinition>(registry, "Items", "");
            RegisterType<AmmoDefinition>(registry, "Ammo", "");
            RegisterType<MapObjectDefinition>(registry, "MapObjects/Definitions", "");
            RegisterType<PerkDefinition>(registry, "Perks", "");
            RegisterType<EffectDefinition>(registry, "Effects", "");
            RegisterType<RPGSkill>(registry, "Skills", "");
            RegisterType<RPGPerk>(registry, "RPG/Perks", "");
        }

        /// <summary>
        /// Load all ScriptableObjects of type T from Resources and register them.
        /// Tries the primary path first, then fallback path if no results.
        /// </summary>
        void RegisterType<T>(IContentRegistry registry, string primaryPath, string fallbackPath)
            where T : ScriptableObject, IGameContent
        {
            var assets = Resources.LoadAll<T>(primaryPath);
            if (assets.Length == 0 && !string.IsNullOrEmpty(fallbackPath))
            {
                assets = Resources.LoadAll<T>(fallbackPath);
            }

            foreach (var asset in assets)
            {
                registry.Register(_manifest, asset);
            }
        }
    }
}
