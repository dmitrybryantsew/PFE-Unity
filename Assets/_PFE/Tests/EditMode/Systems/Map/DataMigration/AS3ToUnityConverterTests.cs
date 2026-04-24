using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.DataMigration;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Map.DataMigration
{
    [TestFixture]
    public class AS3ToUnityConverterTests
    {
        [Test]
        public void ConvertRoom_PreservesStructuredObjectData()
        {
            AS3RoomData room = new AS3RoomData
            {
                name = "room_test",
                x = 1,
                y = 2
            };

            for (int i = 0; i < WorldConstants.ROOM_HEIGHT; i++)
            {
                room.tileLayers.Add(string.Join(".", System.Linq.Enumerable.Repeat("_", WorldConstants.ROOM_WIDTH)));
            }

            AS3Object chest = new AS3Object
            {
                id = "chest",
                code = "code_1",
                x = 5,
                y = 6
            };
            chest.attributes["uid"] = "uid_1";
            chest.attributes["light"] = "1";
            chest.attributes["lock"] = "2.5";
            chest.items.Add(new AS3Item { id = "stimpak" });
            chest.scripts.Add(new AS3Script
            {
                eventName = "open",
                actions =
                {
                    new AS3ScriptAction
                    {
                        act = "off",
                        targ = "door_a",
                        val = "1"
                    }
                }
            });

            room.objects.Add(chest);

            AS3ToUnityConverter converter = new AS3ToUnityConverter(null);

            RoomTemplate template = converter.ConvertRoom(room);

            Assert.AreEqual(1, template.objects.Count);
            ObjectSpawnData spawn = template.objects[0];
            Assert.AreEqual("box", spawn.type);
            Assert.AreEqual("code_1", spawn.code);
            Assert.AreEqual("uid_1", spawn.uid);
            Assert.AreEqual("1", spawn.GetAttribute("light"));
            Assert.AreEqual("2.5", spawn.GetAttribute("lock"));
            Assert.AreEqual(1, spawn.items.Count);
            Assert.AreEqual("stimpak", spawn.items[0].id);
            Assert.AreEqual(1, spawn.scripts.Count);
            Assert.AreEqual("open", spawn.scripts[0].eventName);
            Assert.AreEqual("off", spawn.scripts[0].actions[0].act);
        }

        [Test]
        public void ConvertRoom_ClassifiesKnownDoorIdsAsDoors()
        {
            AS3RoomData room = new AS3RoomData
            {
                name = "room_door",
                x = 0,
                y = 0
            };

            for (int i = 0; i < WorldConstants.ROOM_HEIGHT; i++)
            {
                room.tileLayers.Add(string.Join(".", System.Linq.Enumerable.Repeat("_", WorldConstants.ROOM_WIDTH)));
            }

            room.objects.Add(new AS3Object
            {
                id = "hatch2",
                x = 1,
                y = 1
            });

            AS3ToUnityConverter converter = new AS3ToUnityConverter(null);
            RoomTemplate template = converter.ConvertRoom(room);

            Assert.AreEqual(1, template.objects.Count);
            Assert.AreEqual("door", template.objects[0].type);
        }

        [Test]
        public void ConvertRoom_UsesCatalogDefinitionAndPreservesDefinitionIdentity()
        {
            AS3RoomData room = new AS3RoomData
            {
                name = "room_catalog",
                x = 0,
                y = 0
            };

            for (int i = 0; i < WorldConstants.ROOM_HEIGHT; i++)
            {
                room.tileLayers.Add(string.Join(".", System.Linq.Enumerable.Repeat("_", WorldConstants.ROOM_WIDTH)));
            }

            room.objects.Add(new AS3Object
            {
                id = "custom_console",
                x = 2,
                y = 3
            });

            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "custom_console";
            definition.family = MapObjectFamily.Device;
            definition.defaultPlacementType = "box";

            MapObjectCatalog catalog = ScriptableObject.CreateInstance<MapObjectCatalog>();
            catalog.SetDefinitions(new[] { definition });

            AS3ToUnityConverter converter = new AS3ToUnityConverter(null, null, catalog);
            RoomTemplate template = converter.ConvertRoom(room);

            Assert.AreEqual(1, template.objects.Count);
            Assert.AreEqual("custom_console", template.objects[0].definitionId);
            Assert.AreSame(definition, template.objects[0].definition);
            Assert.AreEqual("box", template.objects[0].type);
        }
    }
}
