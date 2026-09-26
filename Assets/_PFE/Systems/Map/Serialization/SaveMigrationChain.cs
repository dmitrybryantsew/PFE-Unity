using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Systems.Inventory;
using PFE.Systems.RPG;

namespace PFE.Systems.Map.Serialization
{
    /// <summary>
    /// Sequential migration chain for WorldSaveData.
    /// Handles migrating legacy unversioned (v0) and older saves to CurrentFormatVersion.
    /// Rejects saves with format versions newer than supported.
    /// </summary>
    public class SaveMigrationChain
    {
        public const int CurrentFormatVersion = 1;

        private readonly List<ISaveMigrator> _migrators = new List<ISaveMigrator>();

        public SaveMigrationChain()
        {
            RegisterMigrator(new V0ToV1SaveMigrator());
        }

        public void RegisterMigrator(ISaveMigrator migrator)
        {
            if (migrator != null)
                _migrators.Add(migrator);
        }

        public WorldSaveData Migrate(WorldSaveData data)
        {
            if (data == null) return null;

            if (data.saveFormatVersion > CurrentFormatVersion)
            {
                throw new InvalidOperationException(
                    $"Save format version {data.saveFormatVersion} is newer than supported version {CurrentFormatVersion}. Please update the game.");
            }

            while (data.saveFormatVersion < CurrentFormatVersion)
            {
                int currentVer = data.saveFormatVersion;
                var migrator = _migrators.Find(m => m.SourceVersion == currentVer);
                if (migrator == null)
                {
                    throw new InvalidOperationException(
                        $"No save migrator found from version {currentVer} to version {currentVer + 1}.");
                }

                data = migrator.Migrate(data);
                if (data.saveFormatVersion != migrator.TargetVersion)
                {
                    data.saveFormatVersion = migrator.TargetVersion;
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Migrates unversioned (v0) saves to version 1.
    /// Ensures PlayerStateSnapshot has initialized RPG stats, inventory, and equipment fields.
    /// </summary>
    public class V0ToV1SaveMigrator : ISaveMigrator
    {
        public int SourceVersion => 0;
        public int TargetVersion => 1;

        public WorldSaveData Migrate(WorldSaveData data)
        {
            if (data == null) return null;

            if (data.player == null)
            {
                data.player = PlayerStateSnapshot.CreateFromPlayer();
            }

            if (data.player.rpgStats == null)
            {
                data.player.rpgStats = new RPGSaveData
                {
                    level = Mathf.Max(1, data.player.level),
                    xp = Mathf.RoundToInt(data.player.experience),
                    maxHp = data.player.maxHealth > 0 ? data.player.maxHealth : 100f,
                    maxMana = 100f,
                    organMaxHp = 100f
                };
            }

            if (data.player.inventory == null)
            {
                data.player.inventory = new GameInventorySaveData();
            }

            data.saveFormatVersion = 1;
            return data;
        }
    }
}
