using UnityEngine;
using PFE.ModAPI;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using Profiler = PFE.Core.Profiling.PfeProfiler;
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
            // Measured 2026-09-25: game.db.init was 3493 ms of boot — the single largest item,
            // larger than all tile rendering. It is these eleven Resources.LoadAll calls. Split
            // per type so we can see which one actually costs. Note the room templates get loaded
            // AGAIN by GameManager.LoadRoomTemplates (11.9 ms) — that second load is nearly free
            // because Unity already has the objects in memory, which is what makes the asymmetry.
            RegisterType<UnitDefinition>(registry, "game.db.load.units", "Units", "");
            RegisterType<WeaponDefinition>(registry, "game.db.load.weapons", "Weapons", "");
            RegisterType<RoomTemplate>(registry, "game.db.load.rooms", "Rooms", "");
            RegisterType<CharacterAnimationDefinition>(registry, "game.db.load.characters", "Characters", "");
            RegisterType<ItemDefinition>(registry, "game.db.load.items", "Items", "");
            RegisterType<AmmoDefinition>(registry, "game.db.load.ammo", "Ammo", "");
            RegisterType<MapObjectDefinition>(registry, "game.db.load.mapObjects", "MapObjects/Definitions", "");
            RegisterType<PerkDefinition>(registry, "game.db.load.perks", "Perks", "");
            RegisterType<EffectDefinition>(registry, "game.db.load.effects", "Effects", "");
            RegisterType<RPGSkill>(registry, "game.db.load.rpgSkills", "Skills", "");
            RegisterType<RPGPerk>(registry, "game.db.load.rpgPerks", "RPG/Perks", "");
        }

        /// <summary>
        /// Load all ScriptableObjects of type T from Resources and register them.
        /// Tries the primary path first, then fallback path if no results.
        /// </summary>
        /// <param name="label">
        /// Profiler region id. Every call site passes a string literal, so the set of ids stays
        /// bounded and static — see the RULES note in PfeProfiler about forwarded ids.
        /// </param>
        void RegisterType<T>(IContentRegistry registry, string label, string primaryPath, string fallbackPath)
            where T : ScriptableObject, IGameContent
        {
            using (Profiler.Region(label, "boot: Resources.LoadAll<T> for one content type + registration of every asset"))
            {
                T[] assets;
                // Split the load from the registration loop. If loadAll is ~900ms and register is
                // ~5ms then the cost is Unity's Resources loader and no amount of tuning our
                // registry will help — the answer would be to stop loading this type at boot.
                using (Profiler.Region("game.db.loadAll", "boot: Resources.LoadAll<T> — Unity's loader, disk + deserialize"))
                {
                    assets = Resources.LoadAll<T>(primaryPath);
                    if (assets.Length == 0 && !string.IsNullOrEmpty(fallbackPath))
                    {
                        assets = Resources.LoadAll<T>(fallbackPath);
                    }
                }

                // Count is a runtime value, so it goes in `detail` on a fixed id — never in the id.
                Profiler.Mark("game.db.load.count", $"{label} count={assets.Length}");

                using (Profiler.Region("game.db.register", "boot: registry.Register per asset — pure dictionary/string work"))
                {
                    foreach (var asset in assets)
                    {
                        registry.Register(_manifest, asset);
                    }
                }
            }
        }
    }
}
