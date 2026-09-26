using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data;
using PFE.Data.Definitions;
using PFE.Systems.Inventory;
using PFE.Systems.Map;
using PFE.Systems.Map.Serialization;
using PFE.Systems.RPG;
using PFE.Systems.Weapons;

namespace PFE.Tests.Editor.Map.Serialization
{
    [TestFixture]
    public class P3SaveLoadTests
    {
        [Test]
        public void SaveLoad_RoundTrip_PreservesAllState()
        {
            var original = new WorldSaveData
            {
                saveId = "test_roundtrip",
                timestamp = 1700000000L,
                saveVersion = "1.0",
                saveFormatVersion = 1,
                minX = 0, minY = 0, minZ = 0,
                maxX = 5, maxY = 5, maxZ = 0,
                currentRoomX = 2, currentRoomY = 3, currentRoomZ = 0,
                player = new PlayerStateSnapshot
                {
                    posX = 120.5f,
                    posY = 240.0f,
                    roomX = 2,
                    roomY = 3,
                    roomZ = 0,
                    health = 85f,
                    maxHealth = 120f,
                    level = 5,
                    experience = 1450f,
                    equippedWeaponId = "pistol_10mm",
                    equippedArmorId = "vault_suit",
                    rpgStats = new RPGSaveData
                    {
                        level = 5,
                        xp = 1450,
                        skillPoints = 3,
                        perkPoints = 1,
                        maxHp = 120f,
                        maxMana = 80f,
                        organMaxHp = 100f,
                        headHp = 95f,
                        torsHp = 110f,
                        legsHp = 105f,
                        bloodHp = 100f,
                        manaHp = 80f,
                        skills = new List<RPGSaveData.SkillSaveData>
                        {
                            new RPGSaveData.SkillSaveData("small_guns", 45),
                            new RPGSaveData.SkillSaveData("lockpick", 30)
                        },
                        perks = new List<RPGSaveData.PerkSaveData>
                        {
                            new RPGSaveData.PerkSaveData("quick_reload", 1)
                        }
                    },
                    inventory = new GameInventorySaveData
                    {
                        currentWeaponId = "pistol_10mm",
                        currentArmorId = "vault_suit",
                        categoryMass = new float[] { 0f, 2.5f, 5.0f, 10.0f },
                        weapons = new List<GameWeaponSaveData>
                        {
                            new GameWeaponSaveData
                            {
                                weaponId = "pistol_10mm",
                                currentHealth = 90f,
                                currentAmmo = 8,
                                loadedAmmoType = "ammo_10mm",
                                respect = 0,
                                variant = 1
                            }
                        },
                        armors = new List<GameArmorSaveData>
                        {
                            new GameArmorSaveData
                            {
                                armorId = "vault_suit",
                                currentHealth = 100f,
                                level = 0
                            }
                        },
                        items = new List<GameItemSaveData>
                        {
                            new GameItemSaveData
                            {
                                itemId = "ammo_10mm",
                                quantity = 50,
                                condition = 1f,
                                variant = 0,
                                healthMultiplier = 1f,
                                newStatus = 0,
                                acquisitionTimestamp = 1700000000000L,
                                vaultQuantity = 0
                            }
                        },
                        favoriteSlots = new List<GameInventorySaveData.FavoriteSlotSaveEntry>
                        {
                            new GameInventorySaveData.FavoriteSlotSaveEntry { itemId = "pistol_10mm", slot = 0 }
                        }
                    },
                    weaponRuntime = new WeaponRuntimeSaveData
                    {
                        weaponId = "pistol_10mm",
                        currentAmmo = 8,
                        currentDurability = 90,
                        jammed = false,
                        kolShoot = 12,
                        tAttack = 0,
                        tReload = 30, // Mid-reload tick count
                        tPrep = 0,
                        tRet = 2,
                        tRech = 0,
                        tRel = 0,
                        tShoot = 0,
                        tAuto = 0,
                        pow = 0,
                        rotUp = 1.5f
                    }
                }
            };

            string json = WorldSerializer.SerializeToJson(original);
            Assert.IsNotNull(json);

            var loaded = WorldDeserializer.DeserializeFromJson(json);
            Assert.IsNotNull(loaded);

            Assert.AreEqual(original.saveId, loaded.saveId);
            Assert.AreEqual(1, loaded.saveFormatVersion);
            Assert.AreEqual(original.player.posX, loaded.player.posX);
            Assert.AreEqual(original.player.posY, loaded.player.posY);
            Assert.AreEqual(original.player.equippedWeaponId, loaded.player.equippedWeaponId);
            Assert.AreEqual(original.player.equippedArmorId, loaded.player.equippedArmorId);

            // RPG stats verification
            Assert.IsNotNull(loaded.player.rpgStats);
            Assert.AreEqual(5, loaded.player.rpgStats.level);
            Assert.AreEqual(1450, loaded.player.rpgStats.xp);
            Assert.AreEqual(2, loaded.player.rpgStats.skills.Count);
            Assert.AreEqual("small_guns", loaded.player.rpgStats.skills[0].skillId);
            Assert.AreEqual(45, loaded.player.rpgStats.skills[0].level);

            // Inventory verification
            Assert.IsNotNull(loaded.player.inventory);
            Assert.AreEqual(1, loaded.player.inventory.weapons.Count);
            Assert.AreEqual("pistol_10mm", loaded.player.inventory.weapons[0].weaponId);
            Assert.AreEqual(8, loaded.player.inventory.weapons[0].currentAmmo);
            Assert.AreEqual(1, loaded.player.inventory.armors.Count);
            Assert.AreEqual(1, loaded.player.inventory.items.Count);
            Assert.AreEqual(50, loaded.player.inventory.items[0].quantity);

            // Weapon runtime tick timers
            Assert.IsNotNull(loaded.player.weaponRuntime);
            Assert.AreEqual(30, loaded.player.weaponRuntime.tReload);
            Assert.AreEqual(2, loaded.player.weaponRuntime.tRet);
            Assert.AreEqual(8, loaded.player.weaponRuntime.currentAmmo);
            Assert.AreEqual(1.5f, loaded.player.weaponRuntime.rotUp);
        }

        [Test]
        public void WeaponReloadExploit_PreservesReloadTicksAcrossSave()
        {
            // Scenario: Player begins reload (45 ticks remaining at 30 Hz).
            // Saves mid-reload.
            var runtimeData = new WeaponRuntimeSaveData
            {
                weaponId = "shotgun",
                currentAmmo = 0,
                currentDurability = 100,
                tReload = 45, // 45 frames left
                jammed = false
            };

            var save = new WorldSaveData
            {
                saveId = "reload_exploit_test",
                timestamp = 1000,
                saveFormatVersion = 1,
                minX = 0, minY = 0, minZ = 0, maxX = 1, maxY = 1, maxZ = 0,
                player = new PlayerStateSnapshot
                {
                    weaponRuntime = runtimeData
                }
            };

            string json = WorldSerializer.SerializeToJson(save);
            var loaded = WorldDeserializer.DeserializeFromJson(json);

            Assert.IsNotNull(loaded.player.weaponRuntime);
            Assert.AreEqual(45, loaded.player.weaponRuntime.tReload, "Reload ticks MUST NOT be cleared on save/load");
            Assert.AreEqual(0, loaded.player.weaponRuntime.currentAmmo, "Ammo MUST NOT be prematurely refilled");
        }

        [Test]
        public void DestroyedTiles_PersistAndRestore()
        {
            var room = new RoomInstance
            {
                id = "r_01_01",
                templateId = "test_room",
                width = 10,
                height = 10
            };
            room.InitializeTiles();

            // Place a solid wall tile at (3, 4)
            room.tiles[3, 4] = new TileData
            {
                gridPosition = new Vector2Int(3, 4),
                physicsType = TilePhysicsType.Wall,
                indestructible = false,
                hitPoints = 100
            };

            // Destroy it
            room.tiles[3, 4].Destroy();
            Assert.IsTrue(room.tiles[3, 4].IsDestroyed());
            Assert.AreEqual(TilePhysicsType.Air, room.tiles[3, 4].physicsType);

            // Snapshot
            var snapshot = RoomStateSnapshot.CreateFromRoom(room);
            Assert.IsNotNull(snapshot.destroyedTiles);
            Assert.IsTrue(Array.Exists(snapshot.destroyedTiles, c => c == new Vector2Int(3, 4)));

            // Fresh room with same template
            var freshRoom = new RoomInstance
            {
                id = "r_01_01",
                templateId = "test_room",
                width = 10,
                height = 10
            };
            freshRoom.InitializeTiles();
            freshRoom.tiles[3, 4] = new TileData
            {
                gridPosition = new Vector2Int(3, 4),
                physicsType = TilePhysicsType.Wall,
                indestructible = false,
                hitPoints = 100
            };

            // Restore snapshot
            snapshot.RestoreToRoom(freshRoom);

            Assert.AreEqual(TilePhysicsType.Air, freshRoom.tiles[3, 4].physicsType);
            Assert.IsTrue(freshRoom.tiles[3, 4].IsDestroyed());
        }

        [Test]
        public void TolerantModLoading_MissingModLogsWarning_DoesNotCrash()
        {
            var meta = new ModSaveMetadata
            {
                gameVersion = "1.0",
                contentSchemaVersion = 1,
                activeMods = new List<ModSaveMetadata.ModSaveEntry>
                {
                    new ModSaveMetadata.ModSaveEntry
                    {
                        modId = "heavy_weapons_pack",
                        version = "1.2.0",
                        isCosmeticOnly = false,
                        contentHash = "hash123"
                    }
                }
            };

            var emptyRegistry = new ContentRegistry();
            var warnings = meta.ValidateCompatibility(emptyRegistry);

            Assert.AreEqual(1, warnings.Count);
            StringAssert.Contains("Missing mod: heavy_weapons_pack", warnings[0]);
        }

        [Test]
        public void SaveMigration_V0ToV1()
        {
            var legacyV0Save = new WorldSaveData
            {
                saveId = "legacy_save",
                timestamp = 500,
                saveFormatVersion = 0, // Unversioned legacy
                minX = 0, minY = 0, minZ = 0, maxX = 1, maxY = 1, maxZ = 0,
                player = new PlayerStateSnapshot
                {
                    level = 3,
                    experience = 450f,
                    health = 80f,
                    maxHealth = 100f
                }
            };

            var chain = new SaveMigrationChain();
            var migrated = chain.Migrate(legacyV0Save);

            Assert.AreEqual(1, migrated.saveFormatVersion);
            Assert.IsNotNull(migrated.player.rpgStats);
            Assert.AreEqual(3, migrated.player.rpgStats.level);
            Assert.AreEqual(450, migrated.player.rpgStats.xp);
            Assert.IsNotNull(migrated.player.inventory);
        }

        [Test]
        public void SaveMigration_RejectsFutureVersions()
        {
            var futureSave = new WorldSaveData
            {
                saveId = "future_save",
                timestamp = 9999999,
                saveFormatVersion = 999 // Future version
            };

            var chain = new SaveMigrationChain();
            Assert.Throws<InvalidOperationException>(() => chain.Migrate(futureSave));
        }
    }
}
