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
        public const int CurrentFormatVersion = 2;

        private readonly List<ISaveMigrator> _migrators = new List<ISaveMigrator>();

        public SaveMigrationChain()
        {
            RegisterMigrator(new V0ToV1SaveMigrator());
            RegisterMigrator(new V1ToV2SaveMigrator());
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

    /// <summary>
    /// Migrates version 1 saves to version 2 by giving them a difficulty.
    ///
    /// <para>v1 predates <c>WorldSaveData.dif</c>, so there is nothing to recover: AS3's restore arm
    /// answers <b>Normal</b> when the field is absent (<c>Game.as:183-190</c>, the
    /// <c>else globalDif = 2</c> branch), and that is what this writes. Note it is <b>2</b> and not the
    /// field's own <c>NotSaved</c> sentinel: the point of migrating is that every loaded save leaves this
    /// chain with a real, in-range difficulty, so the load path never has to reason about absence
    /// again.</para>
    /// </summary>
    public class V1ToV2SaveMigrator : ISaveMigrator
    {
        public int SourceVersion => 1;
        public int TargetVersion => 2;

        public WorldSaveData Migrate(WorldSaveData data)
        {
            if (data == null) return null;

            if (data.dif < 0)
            {
                data.dif = PFE.Systems.Campaign.GlobalDifficulty.Default;
            }

            data.saveFormatVersion = 2;
            return data;
        }
    }
}
