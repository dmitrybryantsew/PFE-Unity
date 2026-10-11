using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PFE.Systems.Map.DataMigration;
using PFE.Systems.Map.Generation;
using PFE.Data.Definitions;

namespace PFE.Systems.Map.DataMigration
{
    /// <summary>
    /// Converts AS3 room data to Unity RoomTemplate ScriptableObjects.
    /// Handles tile encoding, object placement, and asset creation.
    /// 
    /// IMPORTANT: Tile data is stored as dot-separated multi-character codes.
    /// Each tile can be "C", "_E", "CА", "_Б-", etc. The dots are the delimiter.
    /// This is decoded at runtime by TileDecoder using the TileFormDatabase.
    /// </summary>
    public class AS3ToUnityConverter
    {
        private AS3ObjectMapping objectMapping;
        private readonly AS3LandDefaultsDatabase landDefaults;
        private readonly MapObjectCatalog objectCatalog;

        public AS3ToUnityConverter(AS3ObjectMapping mapping, AS3LandDefaultsDatabase landDefaults = null, MapObjectCatalog objectCatalog = null)
        {
            this.objectMapping = mapping;
            this.landDefaults = landDefaults;
            this.objectCatalog = objectCatalog;
            if (objectMapping != null)
            {
                objectMapping.InitializeCache();
            }

            if (this.objectCatalog != null)
            {
                this.objectCatalog.RebuildIndex();
            }
        }

        /// <summary>
        /// Convert AS3 room to RoomTemplate.
        /// </summary>
        public RoomTemplate ConvertRoom(AS3RoomData as3Room)
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();

            // Basic info
            template.id = as3Room.name;
            template.sourceCollectionId = as3Room.sourceCollectionId;
            template.name = as3Room.name;
            template.type = GetRoomType(as3Room);
            // z is the room's level in the land grid, not its backdrop. AS3 places authored rooms
            // with `this.locs[rx][ry][rz] = newLoc(room, rx, ry, rz)` (Land.as:726-727). Hardcoding
            // z to 0 put room_0_0_1 (z=1) at the same coordinate as room_0_0 (z=0), and whichever
            // won the arbitrary load-order overwrite was the room the player spawned into.
            template.fixedPosition = new Vector3Int(as3Room.x, as3Room.y, as3Room.z);
            template.backgroundRoomId = ResolveBackgroundRoomId(as3Room);
            template.backgroundDecorations = ParseBackgroundDecorations(as3Room);

            // Store raw dot-separated tile rows (NOT single-char flattened)
            template.tileDataString = ConvertTileRows(as3Room);

            // Parse objects
            template.objects = ParseObjects(as3Room);

            // Parse spawn points
            template.spawnPoints = FindSpawnPoints(as3Room);

            // Parse door configuration from original XML door data
            template.doorQuality = ParseDoorConfiguration(as3Room);

            // Environment from options
            template.environment = ParseEnvironment(as3Room);

            // Difficulty
            if (int.TryParse(as3Room.GetOption("level", "0"), out int parsedLevel))
            {
                template.difficultyLevel = Mathf.Clamp(parsedLevel, 0, 20);
            }
            else
            {
                template.difficultyLevel = Mathf.Clamp(
                    Mathf.FloorToInt(Mathf.Sqrt(as3Room.x * as3Room.x + as3Room.y * as3Room.y) / 2f), 0, 20);
            }

            // Generation. AS3 computes both of these PER ROOM (Room.as:24-83) and the port used to
            // hard-code them, which moved the random-fill pool in two directions at once.
            //
            //   rnd  (Room.as:26, 60-79) — true unless the room's `tip` is one of Room.nornd, or the
            //        options carry `nornd`. Those rooms are backgrounds, roofs and passages: AS3 reaches
            //        them only through `newTipLoc` for the cells the conf names by tip, never as random
            //        fill. Hard-coding `true` put 127 of them into the random pool across the ten
            //        procedural lands, crowding out real gameplay rooms.
            //
            //   kol  (Room.as:24, 60-83) — the room's draw quota for one land build, and ALSO the
            //        weight: `newRandomLoc` pushes a candidate `kol*kol` times (Land.as:912-920), so
            //        getting it wrong changes the odds quadratically, not linearly. 2 by default; 1 for
            //        `tip == "uniq"` or an options `uniq`; 4 for an options `test` (which also forces
            //        `lvl = 0`). Hard-coding 2 made each of the 32 `uniq` rooms four times as likely as
            //        AS3 makes it.
            //
            // Both are read through `GetOption(key, null)` so "absent" is distinguishable from
            // "present but empty": AS3 guards every one of these on `.length()`, so `tip=""` leaves the
            // field undefined rather than empty, and neither is a member of nornd. An empty options
            // dictionary is equivalent to AS3's `if(this.xml.options.length())` block never running —
            // for these two fields the defaults are the same either way, so no separate presence flag is
            // needed.
            string rawTip = as3Room.GetOption("tip", null);

            template.allowRandom = !RoomNorndTips.Contains(rawTip) &&
                                   !as3Room.options.ContainsKey("nornd");

            if (as3Room.options.ContainsKey("test"))
            {
                template.maxInstances = 4;
                template.difficultyLevel = 0;   // Room.as:82-83 sets lvl as well as kol
            }
            else if (rawTip == "uniq" || as3Room.options.ContainsKey("uniq"))
            {
                template.maxInstances = 1;
            }
            else
            {
                template.maxInstances = 2;
            }

            return template;
        }

        /// <summary>
        /// AS3 <c>Room.nornd</c> (<c>Room.as:6</c>) — the tips that are never random fill.
        ///
        /// <para>Transcribed verbatim, because the set is the rule: <c>beg0</c> (the entry cell's own
        /// room), <c>back</c>/<c>roof</c>/<c>pass</c>/<c>passroof</c>/<c>roofpass</c> (the structural
        /// shells) and <c>vert</c>/<c>surf</c> (the vertical and surface connectors). A room whose tip is
        /// one of these is placed only where the conf names that tip — <c>Land.as:226-352</c> is a chain
        /// of <c>newTipLoc("beg…")</c>/<c>newTipLoc("passroof")</c> calls for exactly those cells.</para>
        ///
        /// <para>Compared with <see cref="StringComparer.Ordinal"/> and <b>without trimming</b>: AS3 does
        /// <c>this.tip == _loc2_</c> against the raw attribute value, so a tip of <c>"back "</c> would not
        /// match there either. Trimming here would accept rooms the oracle keeps out.</para>
        /// </summary>
        private static readonly HashSet<string> RoomNorndTips = new HashSet<string>(StringComparer.Ordinal)
        {
            "beg0", "back", "roof", "pass", "passroof", "roofpass", "vert", "surf"
        };

        /// <summary>
        /// Convert multiple rooms at once.
        /// </summary>
        public List<RoomTemplate> ConvertRooms(List<AS3RoomData> as3Rooms)
        {
            List<RoomTemplate> templates = new List<RoomTemplate>();

            foreach (var as3Room in as3Rooms)
            {
                try
                {
                    templates.Add(ConvertRoom(as3Room));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error converting room {as3Room.name}: {ex.Message}");
                }
            }

            return templates;
        }

        // =====================================================
        //  TILE DATA — preserve dot-separated format verbatim
        // =====================================================

        /// <summary>
        /// Store tile rows preserving the original dot-separated multi-character format.
        /// Each row is stored as-is. Rows are joined by newlines.
        /// At runtime, TileDecoder.ParseRoom() splits by dots and decodes each token.
        /// </summary>
        private string ConvertTileRows(AS3RoomData as3Room)
        {
            List<string> rows = new List<string>(WorldConstants.ROOM_HEIGHT);

            for (int y = 0; y < WorldConstants.ROOM_HEIGHT; y++)
            {
                string sourceRow = y < as3Room.tileLayers.Count
                    ? as3Room.tileLayers[y]?.TrimEnd('\r', '\n') ?? ""
                    : "";

                if (string.IsNullOrEmpty(sourceRow))
                {
                    // Empty row: generate 48 air tiles
                    rows.Add(string.Join(".", Enumerable.Repeat("_", WorldConstants.ROOM_WIDTH)));
                    continue;
                }

                // Validate tile count by splitting on dots
                // Use RemoveEmptyEntries to handle trailing dots (e.g., "C._E._E.")
                string[] tokens = sourceRow.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length != WorldConstants.ROOM_WIDTH)
                {
                    Debug.LogWarning(
                        $"[AS3ToUnityConverter] Row {y} has {tokens.Length} tiles, expected {WorldConstants.ROOM_WIDTH}. " +
                        $"Adjusting.");

                    if (tokens.Length < WorldConstants.ROOM_WIDTH)
                    {
                        // Pad with air tiles
                        var padded = new string[WorldConstants.ROOM_WIDTH];
                        Array.Copy(tokens, padded, tokens.Length);
                        for (int i = tokens.Length; i < WorldConstants.ROOM_WIDTH; i++)
                            padded[i] = "_";
                        sourceRow = string.Join(".", padded);
                    }
                    else
                    {
                        // Truncate extra tiles
                        sourceRow = string.Join(".", tokens.Take(WorldConstants.ROOM_WIDTH));
                    }
                }

                rows.Add(sourceRow);
            }

            return string.Join("\n", rows);
        }

        // =====================================================
        //  ROOM TYPE
        // =====================================================

        private string GetRoomType(AS3RoomData room)
        {
            string explicitType = room.GetOption("tip", "");
            if (!string.IsNullOrEmpty(explicitType))
                return explicitType;

            string name = room.name ?? "";
            if (name.Contains("beg", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("start", StringComparison.OrdinalIgnoreCase))
                return "beg0";
            if (name.Contains("roof", StringComparison.OrdinalIgnoreCase)) return "roof";
            if (name.Contains("vert", StringComparison.OrdinalIgnoreCase)) return "vert";
            if (name.Contains("surf", StringComparison.OrdinalIgnoreCase)) return "surf";
            if (name.Contains("end", StringComparison.OrdinalIgnoreCase)) return "end";

            return "pass";
        }

        // =====================================================
        //  OBJECTS
        // =====================================================

        private List<ObjectSpawnData> ParseObjects(AS3RoomData as3Room)
        {
            List<ObjectSpawnData> placements = new List<ObjectSpawnData>();

            foreach (var as3Obj in as3Room.objects)
            {
                MapObjectDefinition definition = ResolveDefinition(as3Obj.id);
                ObjectSpawnData placement = new ObjectSpawnData
                {
                    id = as3Obj.id,
                    definitionId = as3Obj.id,
                    definition = definition,
                    type = GetObjectTypeFromId(as3Obj.id, as3Obj.attributes, definition),
                    tileCoord = new Vector2Int(as3Obj.x, as3Obj.y),
                    code = as3Obj.code,
                    uid = as3Obj.GetAttribute("uid", string.Empty),
                    attributes = MapObjectDataUtility.BuildAttributes(
                        as3Obj.attributes,
                        "x", "y", "id", "code", "uid"),
                    items = ConvertItems(as3Obj.items),
                    scripts = ConvertScripts(as3Obj.scripts)
                };

                placement.ApplyResolvedDefinition(definition);
                placement.RefreshLegacyParameters();
                placements.Add(placement);
            }

            return placements;
        }

        private MapObjectDefinition ResolveDefinition(string objectId)
        {
            if (objectCatalog == null || string.IsNullOrWhiteSpace(objectId))
            {
                return null;
            }

            return objectCatalog.GetDefinition(objectId);
        }

        private string GetObjectTypeFromId(
            string objectId,
            IReadOnlyDictionary<string, string> attributes,
            MapObjectDefinition definition)
        {
            if (string.IsNullOrEmpty(objectId)) return "obj";
            if (objectId.Equals("player", StringComparison.OrdinalIgnoreCase)) return "player";

            if (TryGetMappedObjectType(objectId, out string mappedType))
            {
                return mappedType;
            }

            if (definition != null)
            {
                return definition.GetResolvedPlacementType();
            }

            if (objectId == "tarakan" || objectId == "enemy") return "unit";
            MapObjectDefinitionClassifier.ResolveFamily(objectId, attributes, out string placementType, out _);
            return placementType;
        }

        private bool TryGetMappedObjectType(string objectId, out string mappedType)
        {
            mappedType = string.Empty;
            if (objectMapping == null || string.IsNullOrWhiteSpace(objectId))
            {
                return false;
            }

            if (!objectMapping.IsKnown(objectId))
            {
                return false;
            }

            AS3ObjectMapping.ObjectMapping mapping = objectMapping.GetMapping(objectId);
            if (mapping == null)
            {
                return false;
            }

            mappedType = mapping.category switch
            {
                ObjectCategory.Player => "player",
                ObjectCategory.Enemy => "unit",
                ObjectCategory.NPC => "unit",
                ObjectCategory.Container => "box",
                ObjectCategory.Door => "door",
                ObjectCategory.Trigger => "area",
                _ => string.Empty
            };

            return !string.IsNullOrEmpty(mappedType);
        }

        private static List<MapObjectItemData> ConvertItems(List<AS3Item> sourceItems)
        {
            List<MapObjectItemData> items = new List<MapObjectItemData>();
            if (sourceItems == null)
            {
                return items;
            }

            for (int i = 0; i < sourceItems.Count; i++)
            {
                AS3Item item = sourceItems[i];
                if (item == null)
                {
                    continue;
                }

                items.Add(new MapObjectItemData
                {
                    id = item.id,
                    attributes = MapObjectDataUtility.BuildAttributes(item.attributes)
                });
            }

            return items;
        }

        private static List<MapObjectScriptData> ConvertScripts(List<AS3Script> sourceScripts)
        {
            List<MapObjectScriptData> scripts = new List<MapObjectScriptData>();
            if (sourceScripts == null)
            {
                return scripts;
            }

            for (int i = 0; i < sourceScripts.Count; i++)
            {
                AS3Script sourceScript = sourceScripts[i];
                if (sourceScript == null)
                {
                    continue;
                }

                MapObjectScriptData script = new MapObjectScriptData
                {
                    eventName = sourceScript.eventName
                };

                if (sourceScript.actions != null)
                {
                    for (int actionIndex = 0; actionIndex < sourceScript.actions.Count; actionIndex++)
                    {
                        AS3ScriptAction sourceAction = sourceScript.actions[actionIndex];
                        if (sourceAction == null)
                        {
                            continue;
                        }

                        script.actions.Add(new MapObjectScriptActionData
                        {
                            act = sourceAction.act,
                            targ = sourceAction.targ,
                            val = sourceAction.val,
                            n = sourceAction.n,
                            opt1 = sourceAction.opt1,
                            opt2 = sourceAction.opt2,
                            t = sourceAction.t
                        });
                    }
                }

                scripts.Add(script);
            }

            return scripts;
        }

        // =====================================================
        //  SPAWN POINTS
        // =====================================================

        private List<SpawnPointData> FindSpawnPoints(AS3RoomData as3Room)
        {
            List<SpawnPointData> spawnPoints = new List<SpawnPointData>();

            AS3Object playerObj = as3Room.objects.FirstOrDefault(o => o.id == "player");
            if (playerObj != null)
            {
                spawnPoints.Add(new SpawnPointData
                {
                    tileCoord = new Vector2Int(playerObj.x, playerObj.y),
                    type = SpawnType.Player
                });
            }
            else
            {
                spawnPoints.Add(new SpawnPointData
                {
                    tileCoord = new Vector2Int(2, 15),
                    type = SpawnType.Player
                });
            }

            return spawnPoints;
        }

        private List<BackgroundDecorationData> ParseBackgroundDecorations(AS3RoomData as3Room)
        {
            List<BackgroundDecorationData> decorations = new List<BackgroundDecorationData>();

            foreach (var background in as3Room.backgrounds)
            {
                decorations.Add(new BackgroundDecorationData
                {
                    id = background.id,
                    tileCoord = new Vector2Int(background.x, background.y)
                });
            }

            return decorations;
        }

        private string ResolveBackgroundRoomId(AS3RoomData as3Room)
        {
            if (as3Room == null)
            {
                return string.Empty;
            }

            // Background-room linkage is separate from <back> decoration placement.
            // Only use an explicit option if the source data provides one.
            return as3Room.GetOption("back", "").Trim();
        }

        // =====================================================
        //  DOORS — the room's own <doors> element
        // =====================================================

        /// <summary>
        /// Read the room's door qualities from the AS3 <c>&lt;doors&gt;</c> element.
        ///
        /// <para>AS3 does exactly this in the <c>Location</c> constructor — it never derives doors from
        /// the tile grid:</para>
        /// <code>
        /// Location.as:605-608   s = nroom.doors[0]; this.doors = s.split(".");
        /// Location.as:633-641   else { while(i &lt; 22) this.doors[i] = 2; }   // no &lt;doors&gt; -&gt; all-2
        /// </code>
        ///
        /// <para><b>This replaces a tile-code heuristic that was an invention.</b> The oracle's own
        /// attribute was never read, yet 413 of its 639 rooms carry it (exactly 1:1 with
        /// <c>&lt;room&gt;</c>, e.g. <c>RoomsPlant.as:119</c>); the four room files without it take the
        /// all-2 default above. The heuristic also wrote slots <c>6-11 / 12-17 / 18-23</c> while every
        /// consumer reads <c>6-10 / 11-16 / 17-21</c> (<see cref="DoorMatchMath.SlotRange"/>,
        /// <c>RoomGenerator.GetDoorSide</c>, <c>DoorCarver.CarveDoor</c>), so 163 of 959 of its entries
        /// landed on the wrong wall — 86 of them never carved at all. And because it wrote
        /// <c>Narrow</c> (2) unconditionally, the oracle's wide doors (quality 3/4, its <i>modal</i>
        /// value) could never occur. See <c>docs/LandGameplayLoop/06_DOOR_CARVE_AUDIT.md</c>.</para>
        ///
        /// <para>The string is captured during the XML parse by <c>AS3RoomParser</c> under the
        /// <c>_doors_raw</c> option and decoded by <see cref="DoorMatchMath.Split"/> — the same 22-slot,
        /// oracle-ordered decode the runtime uses. The slots therefore come out already in the oracle's
        /// own order and need no remapping.</para>
        /// </summary>
        private int[] ParseDoorConfiguration(AS3RoomData as3Room)
        {
            as3Room.options.TryGetValue("_doors_raw", out string raw);

            // 22 meaningful slots: 0-5 right, 6-10 bottom, 11-16 left, 17-21 top. A missing or empty
            // string yields all-DefaultQuality (2), which is AS3's documented fallback.
            int[] slots = DoorMatchMath.Split(raw);

            // RoomTemplate.doorQuality is allocated with slack to 24; only 0..21 are read.
            int[] doorQuality = new int[WorldConstants.DOORS_PER_ROOM];
            for (int i = 0; i < slots.Length && i < doorQuality.Length; i++)
            {
                doorQuality[i] = slots[i];
            }

            return doorQuality;
        }

        // =====================================================
        //  ENVIRONMENT
        // =====================================================

        private RoomEnvironmentData ParseEnvironment(AS3RoomData as3Room)
        {
            var env = new RoomEnvironmentData();

            IReadOnlyDictionary<string, string> inheritedOptions = GetInheritedLandOptions(as3Room);

            env.musicTrack = ResolveOption(as3Room, inheritedOptions, "music", "");
            env.colorScheme = ResolveOption(as3Room, inheritedOptions, "color", "");
            env.backgroundColorScheme = ResolveOption(as3Room, inheritedOptions, "colorfon", "");
            env.backgroundWall = ResolveOption(as3Room, inheritedOptions, "backwall", "");
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "backform", "0"), out int backform))
                env.backgroundForm = backform;
            env.transparentBackground = ResolveFlag(as3Room, inheritedOptions, "transpfon");
            env.hasSky = ResolveFlag(as3Room, inheritedOptions, "sky");
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "ramka", "1"), out int borderType))
                env.borderType = borderType;
            env.noBlackReveal = ResolveFlag(as3Room, inheritedOptions, "noblack");
            if (float.TryParse(ResolveOption(as3Room, inheritedOptions, "vis", "1"), out float vis))
                env.visibilityMultiplier = vis;
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "lon", "0"), out int lon))
                env.lightsOn = lon;
            env.returnsToDarkness = ResolveFlag(as3Room, inheritedOptions, "retdark");
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "wlevel", "100"), out int wl))
                env.waterLevel = wl;
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "wtip", "0"), out int wt))
                env.waterType = wt;
            if (float.TryParse(ResolveOption(as3Room, inheritedOptions, "wopac", "0"), out float wo))
                env.waterOpacity = wo;
            if (float.TryParse(ResolveOption(as3Room, inheritedOptions, "wdam", "0"), out float wd))
                env.waterDamage = wd;
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "wtipdam", "7"), out int wtd))
                env.waterDamageType = wtd;
            if (float.TryParse(ResolveOption(as3Room, inheritedOptions, "rad", "0"), out float rad))
                env.radiation = rad;
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "dark", "0"), out int dark))
                env.darkness = dark;

            return env;
        }

        private IReadOnlyDictionary<string, string> GetInheritedLandOptions(AS3RoomData as3Room)
        {
            if (as3Room == null || landDefaults == null || string.IsNullOrWhiteSpace(as3Room.sourceCollectionId))
            {
                return null;
            }

            landDefaults.TryGetOptions(as3Room.sourceCollectionId, out IReadOnlyDictionary<string, string> options);
            return options;
        }

        /// <summary>
        /// A room option, else the land's, else <paramref name="fallback"/>.
        ///
        /// <para><b>A present-but-empty attribute is ABSENT, not an override.</b> AS3 gates every one of
        /// these on the attribute's <i>length</i>, not on its presence
        /// (<c>Location.as:390</c> <c>if(nroom.options.@backwall.length())</c>, and the same shape at
        /// <c>:394, :402, :406-436</c>). <c>@backwall.length()</c> is <b>0</b> for <c>backwall=""</c>, so an
        /// explicitly empty attribute leaves the land's value standing — and <c>Location.as:376-387</c>
        /// seeds twelve fields from <c>this.land.act</c> before the room gets a say.</para>
        ///
        /// <para><b>This used to return the empty string and stop.</b> <c>TryGetValue</c> succeeds on a key
        /// the parser stored with an empty value, so <c>&lt;options tip="beg0" backwall="" music="…"/&gt;</c>
        /// (<c>RoomsPlant.as:1223</c>) wiped the land's wall instead of deferring to it — 43 of 639 imported
        /// rooms ended up with <c>backgroundWall: ""</c>, and <c>RoomBackdropRenderer.CreateRoomBackdrop</c>
        /// then returned without drawing anything at all, leaving the camera's clear colour visible.</para>
        /// </summary>
        private static string ResolveOption(AS3RoomData room, IReadOnlyDictionary<string, string> inheritedOptions, string key, string fallback)
        {
            // `.length()` — a non-empty room value is the only thing that overrides the land.
            if (room != null &&
                room.options.TryGetValue(key, out string roomValue) &&
                !string.IsNullOrEmpty(roomValue))
            {
                return roomValue;
            }

            if (inheritedOptions != null &&
                inheritedOptions.TryGetValue(key, out string inheritedValue) &&
                !string.IsNullOrEmpty(inheritedValue))
            {
                return inheritedValue;
            }

            return fallback;
        }

        /// <summary>
        /// A boolean room option — true only when the attribute is <b>present and non-empty</b>.
        ///
        /// <para>AS3 tests <c>.length()</c> for these too (<c>transpfon</c> <c>Location.as:398</c>,
        /// <c>noblack</c> <c>:442</c>, <c>retdark</c> <c>:478</c>, <c>sky</c> <c>:518</c>), so
        /// <c>transpfon=""</c> means <i>off</i>, not <i>on</i>. <see cref="ContainsKey(string)"/> could not
        /// tell those apart.</para>
        ///
        /// <para>⚠ The <paramref name="inheritedOptions"/> fallback here is <b>wider than AS3</b>, and that
        /// divergence is pre-existing and deliberately untouched: <c>Location.as:376-387</c> inherits exactly
        /// twelve <i>valued</i> fields from the land and no flags, whereas this method would inherit a land
        /// flag if one existed. It is a no-op against the current data — no land in <c>GameData.as</c>
        /// declares <c>transpfon</c>, <c>sky</c>, <c>noblack</c> or <c>retdark</c> — so narrowing it here
        /// would be a behaviour change in a pass whose subject is a missing backdrop. Tracked, not fixed.</para>
        /// </summary>
        private static bool ResolveFlag(AS3RoomData room, IReadOnlyDictionary<string, string> inheritedOptions, string key)
        {
            if (room != null &&
                room.options.TryGetValue(key, out string roomValue) &&
                !string.IsNullOrEmpty(roomValue))
            {
                return true;
            }

            return inheritedOptions != null &&
                inheritedOptions.TryGetValue(key, out string inheritedValue) &&
                !string.IsNullOrEmpty(inheritedValue);
        }

        // =====================================================
        //  VALIDATION (post-conversion sanity check)
        // =====================================================

        /// <summary>
        /// Validate a converted RoomTemplate. Returns error message or null if valid.
        /// </summary>
        public static string ValidateTemplate(RoomTemplate template)
        {
            if (template == null) return "Template is null";
            if (string.IsNullOrEmpty(template.id)) return "Empty ID";
            if (string.IsNullOrEmpty(template.tileDataString)) return "Empty tile data";

            string[] rows = template.tileDataString.Split('\n');
            if (rows.Length != WorldConstants.ROOM_HEIGHT)
                return $"Expected {WorldConstants.ROOM_HEIGHT} rows, got {rows.Length}";

            for (int y = 0; y < rows.Length; y++)
            {
                string[] tokens = rows[y].Split('.');
                if (tokens.Length != WorldConstants.ROOM_WIDTH)
                    return $"Row {y}: expected {WorldConstants.ROOM_WIDTH} tiles, got {tokens.Length}";
            }

            if (template.doorQuality == null || template.doorQuality.Length != 24)
                return $"Door quality array: expected 24 entries, got {template.doorQuality?.Length ?? 0}";

            return null; // Valid
        }

        /// <summary>
        /// Save template to asset database.
        /// </summary>
        public void SaveTemplate(RoomTemplate template, string path)
        {
#if UNITY_EDITOR
            string assetPath = $"{path}/{template.id}.asset";
            UnityEditor.AssetDatabase.CreateAsset(template, assetPath);
            UnityEditor.AssetDatabase.SaveAssets();
#endif
        }
    }
}
