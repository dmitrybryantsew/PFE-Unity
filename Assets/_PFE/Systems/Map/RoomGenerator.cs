using System;
using System.Collections.Generic;
using UnityEngine;
using VContainer;
namespace PFE.Systems.Map
{
    /// <summary>
    /// Room generator - procedural room generation.
    /// From AS3: Land.buildRandomLand(), Land.newRandomLoc()
    /// </summary>
    public class RoomGenerator
    {
        private List<RoomTemplate> allTemplates;
        private Dictionary<string, int> templateUsageCount = new Dictionary<string, int>();
        private bool prototypeMode;
        private bool excludeSpecialTypesInRandomSelection;
        private readonly TileFormDatabase _formDatabase;
        // Special-purpose room types that should not appear in generic random fill.
        // AS3 oracle: `Room.nornd` (Room.as:11) = beg0, back, roof, pass, passroof, roofpass, vert, surf.
        // The port used to carry beg0, beg1, roof, vert, surf, back, end, end1 — it was missing
        // pass/passroof/roofpass (which conf 3 and conf 11 request *by tip*, Land.as:239-269, 374-386)
        // and it excluded beg1/end/end1, which the oracle does NOT exclude.
        // See docs/LandGameplayLoop/03_GAP_LEDGER.md §10 (D4).
        private static readonly HashSet<string> SpecialRoomTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "beg0", "back", "roof", "pass", "passroof", "roofpass", "vert", "surf"
        };

        private readonly PFE.Core.Rng.IRngService _rng;

        [Inject]  // Add this constructor
        public RoomGenerator(TileFormDatabase formDb = null, PFE.Core.Rng.IRngService rng = null)
        {
            _formDatabase = formDb;
            _rng = rng != null ? rng.GetStream(PFE.Core.Rng.RngStream.Spawn) : new PFE.Core.Rng.PcgRngService().GetStream(PFE.Core.Rng.RngStream.Spawn);
        }
        /// <summary>
        /// Initialize with all templates.
        /// </summary>
        public void Initialize(List<RoomTemplate> templates)
        {
            allTemplates = templates;
            // Initialize usage counts
            templateUsageCount.Clear();
            foreach (var template in templates)
            {
                string templateKey = template.GetContentId();
                if (!string.IsNullOrEmpty(templateKey))
                {
                    templateUsageCount[templateKey] = 0;
                }
            }
        }

        /// <summary>
        /// Configure generation behavior for migration/prototype runs.
        /// </summary>
        public void ConfigureGenerationMode(bool usePrototypeMode, bool excludeSpecialTypesInRandom = false)
        {
            prototypeMode = usePrototypeMode;
            excludeSpecialTypesInRandomSelection = excludeSpecialTypesInRandom;
        }

        public bool IsPrototypeMode => prototypeMode;

        /// <summary>
        /// The land this generator is currently building for. Set by <c>WorldBuilder.BuildLand</c> before
        /// it starts generating; <see cref="LocationBuildContext.None"/> when no land is known.
        ///
        /// <para><b>Why a property and not just a parameter.</b> <c>GenerateRoom</c> is called from four
        /// places — the procedural pass, the authored pass, the prob-room builder and the editor preview —
        /// and only the first two have a land in hand. A property lets the land-scoped callers configure
        /// the generator once, the way <c>BuildLand</c> already re-points its template pool
        /// (<c>roomGenerator.Initialize(templates)</c>), instead of threading a land through every call
        /// site including the ones that have none.</para>
        /// </summary>
        public LocationBuildContext BuildContext { get; private set; } = LocationBuildContext.None;

        /// <summary>
        /// Point the generator at a land. AS3's equivalent is the <c>Land</c> instance that
        /// <c>newLoc</c> is a method of.
        /// </summary>
        public void ConfigureLocation(in LocationBuildContext context) => BuildContext = context;

        /// <summary>
        /// Generate room from template.
        /// </summary>
        /// <param name="locationContext">
        /// The land to build this room for. <c>null</c> falls back to <see cref="BuildContext"/>, which is
        /// what the per-cell procedural pass relies on — it varies only the depth offset, and passes that
        /// explicitly.
        /// </param>
        public RoomInstance GenerateRoom(
            RoomTemplate template,
            Vector3Int position,
            LocationBuildContext? locationContext = null)
        {
            if (template == null)
            {
                Debug.LogError("Attempted to generate room from null template");
                return null;
            }

            LocationBuildContext context = locationContext ?? BuildContext;

            RoomInstance room = new RoomInstance
            {
                id = $"{template.GetContentId()}_{position.x}_{position.y}_{position.z}",
                templateId = template.GetContentId(),
                landPosition = position,
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                roomType = template.type
            };

            // Initialize and parse tiles
            room.InitializeTiles();
            TileData[,] templateTiles = template.ParseTiles(_formDatabase);
            for (int x = 0; x < room.width; x++)
            {
                for (int y = 0; y < room.height; y++)
                {
                    if (templateTiles[x, y] != null)
                    {
                        room.tiles[x, y] = templateTiles[x, y];
                    }
                }
            }

            // Copy spawn points
            foreach (var spawnData in template.spawnPoints)
            {
                room.spawnPoints.Add(new SpawnPoint
                {
                    tileCoord = spawnData.tileCoord,
                    type = spawnData.type,
                    unitId = spawnData.unitId,
                    facingDirection = spawnData.facingDirection
                });
            }

            foreach (var backgroundDecoration in template.backgroundDecorations)
            {
                room.backgroundDecorations.Add(new BackgroundDecorationInstance
                {
                    decorationId = backgroundDecoration.id,
                    tileCoord = backgroundDecoration.tileCoord
                });
            }

            // Copy door configuration
            room.doors = CreateDoors(template, room, position);

            // Set difficulty — the port's Land.newLoc + Land.setLocDif.
            //
            // AS3 does NOT derive a room's difficulty from the room's own `lvl`. `lvl` is only a
            // *selection* gate (`Land.newRandomLoc` skips a room whose `lvl > maxlevel`, Land.as:918);
            // the difficulty comes from the LAND: `locDifLevel = landDifLevel + depthOffset`
            // (Land.as:982) and `enemyLevel = locDifLevel` (:987). Filling these from
            // `template.difficultyLevel` was wrong twice over — wrong source, and it left `enemyCounts`
            // all zero because nothing called `setKolEn`.
            room.biome = context.Biome;
            room.landConfig = context.LandConfig;
            room.noHolesPlace = context.NoHolesPlace;

            room.difficulty = new RoomDifficulty();

            // AS3's order matters here. `Location`'s constructor runs buildLoc() — which reads
            // `<options kolspawn>` into `kolEnSpawn` (Location.as:502-505) — and only afterwards does
            // newLoc() call setLocDif() (Land.as:975), which reads `if(param1.kolEnSpawn == 0)` to decide
            // whether to seed it. Applying the option after the planner would make the guard see 0 and
            // overwrite an authored value.
            //
            // An unauthored `kolspawn` is 0 in AS3 (`Location.as:225`), so the port's `-1` sentinel is
            // clamped to 0 here rather than skipped — absent and an authored `0` behave identically in the
            // oracle, and only the value matters to the guard.
            room.difficulty.spawnEnemyCount = Mathf.Max(0, template.environment.kolEnSpawn);

            LocationDifficultyPlanner.Apply(
                room.difficulty,
                new LocationDifficultyInput(
                    biome: context.Biome,
                    landDifLevel: context.LandDifLevel,
                    difficultyOffset: context.DifficultyOffset,
                    tipEnemy: template.environment.tipEnemy,
                    landConfig: context.LandConfig),
                _rng);

            // Set environment
            room.environment = new RoomEnvironment
            {
                musicTrack = template.environment.musicTrack,
                colorScheme = template.environment.colorScheme,
                backgroundColorScheme = template.environment.backgroundColorScheme,
                backgroundWall = template.environment.backgroundWall,
                backgroundForm = template.environment.backgroundForm,
                transparentBackground = template.environment.transparentBackground,
                hasSky = template.environment.hasSky,
                borderType = template.environment.borderType,
                noBlackReveal = template.environment.noBlackReveal,
                visibilityMultiplier = template.environment.visibilityMultiplier,
                lightsOn = template.environment.lightsOn,
                returnsToDarkness = template.environment.returnsToDarkness,
                waterLevel = template.environment.waterLevel,
                waterType = template.environment.waterType,
                waterOpacity = template.environment.waterOpacity,
                waterDamage = template.environment.waterDamage,
                waterDamageType = template.environment.waterDamageType,
                radiation = template.environment.radiation,
                radiationDamage = template.environment.radiationDamage,
                darkness = template.environment.darkness
            };

            return room;
        }

        /// <summary>
        /// Build the room's door slots from the template's <c>doorQuality</c> table.
        ///
        /// <para><b>These are <i>candidates</i>, not doors, and they start inactive.</b> AS3's
        /// <c>doors[i]</c> is only a candidate mask — a slot qualifies when
        /// <c>min(a[i], b[i+11]) &gt;= 2</c> (<c>Land.as:426</c>, <c>:443</c>) and the doors actually
        /// carved are a <i>draw</i> from that list: three times with replacement on the right wall,
        /// once on the lower side (<c>Land.as:471-488</c>).</para>
        ///
        /// <para>Setting <c>isActive = true</c> here (as this used to) collapsed that distinction: the
        /// mask became the final answer, so every candidate was carved and every candidate got a
        /// walk-through trigger. The filter that should have narrowed it —
        /// <see cref="PFE.Systems.Map.Generation.DoorMatchMath"/> via
        /// <c>WorldBuilder.CarveNeighbourPair</c> — could only ever set <c>isActive = true</c> as well,
        /// so it added nothing. Measured before the fix: a mean of <b>17.4 of 24</b> slots carved per
        /// room in <c>rooms_plant</c>, against roughly 4-8 in the oracle, and four rooms that the oracle
        /// authors with <i>zero</i> doors carved 2-6 of them.</para>
        ///
        /// <para>The reset that used to hide this (<c>WorldBuilder.BuildDoorConnections</c> clearing
        /// <c>isActive</c>) only runs on the authored path, which <c>BuildProceduralLand</c> never
        /// calls — so the over-carve was live in every procedural land.</para>
        ///
        /// <para>See <c>docs/LandGameplayLoop/06_DOOR_CARVE_AUDIT.md</c>.</para>
        /// </summary>
        private List<DoorInstance> CreateDoors(RoomTemplate template, RoomInstance room, Vector3Int position)
        {
            List<DoorInstance> doors = new List<DoorInstance>();

            for (int i = 0; i < template.doorQuality.Length && i < WorldConstants.DOORS_PER_ROOM; i++)
            {
                if (template.doorQuality[i] >= (int)DoorQuality.Narrow)
                {
                    DoorInstance door = new DoorInstance
                    {
                        doorIndex = i,
                        side = GetDoorSide(i),
                        quality = (DoorQuality)template.doorQuality[i],

                        // Inactive on purpose: a candidate becomes a door only when
                        // WorldBuilder.CarveNeighbourPair activates it (AS3's pass_r/pass_d draw).
                        isActive = false,
                        tilePosition = GetDoorTilePosition(i)
                    };

                    doors.Add(door);
                }
            }

            return doors;
        }

        /// <summary>
        /// Get door side from index.
        /// </summary>
        private DoorSide GetDoorSide(int doorIndex)
        {
            if (doorIndex >= 0 && doorIndex <= 5) return DoorSide.Right;
            if (doorIndex >= 6 && doorIndex <= 10) return DoorSide.Bottom;
            if (doorIndex >= 11 && doorIndex <= 16) return DoorSide.Left;
            return DoorSide.Top;
        }

        /// <summary>
        /// Get door tile position from index.
        /// </summary>
        private Vector2Int GetDoorTilePosition(int doorIndex)
        {
            int width = WorldConstants.ROOM_WIDTH;   // 48
            int height = WorldConstants.ROOM_HEIGHT; // 25 — AS3 World.cellsY

            if (doorIndex >= 0 && doorIndex <= 5)
            {
                // RIGHT side: AS3 formula = index * 4 + 3
                int y = height - 1 - (doorIndex * 4 + 3);
                return new Vector2Int(width - 1, y);
            }
            else if (doorIndex >= 6 && doorIndex <= 10)
            {
                // BOTTOM side: AS3 formula = (index - 6) * 9 + 4
                int x = (doorIndex - 6) * 9 + 4;
                return new Vector2Int(x, 0);
            }
            else if (doorIndex >= 11 && doorIndex <= 16)
            {
                // LEFT side: AS3 formula = (index - 11) * 4 + 3
                int y = height - 1 - ((doorIndex - 11) * 4 + 3);
                return new Vector2Int(0, y);
            }
            else if (doorIndex >= 17 && doorIndex <= 21)
            {
                // TOP side: AS3 formula = (index - 17) * 9 + 4
                int x = (doorIndex - 17) * 9 + 4;
                return new Vector2Int(x, height - 1);
            }

            return Vector2Int.zero;
        }

        /// <summary>
        /// Select random room template (weighted).
        /// From AS3: Land.newRandomLoc() - weighted selection with kol * kol
        /// </summary>
        public RoomTemplate SelectRandomRoom(int maxDifficulty, string requiredType = null, List<RoomTemplate> exclude = null)
        {
            return SelectRandomRoomInternal(maxDifficulty, requiredType, exclude, allowExcludeFallbackRetry: true);
        }

        private RoomTemplate SelectRandomRoomInternal(int maxDifficulty, string requiredType, List<RoomTemplate> exclude, bool allowExcludeFallbackRetry)
        {
            if (allTemplates == null || allTemplates.Count == 0)
            {
                Debug.LogWarning($"No valid room templates found (maxDifficulty: {maxDifficulty}, type: {requiredType})");
                return null;
            }

            List<RoomTemplate> candidates = new List<RoomTemplate>();
            List<int> weights = new List<int>();

            foreach (var template in allTemplates)
            {
                // Skip templates without ID
                if (string.IsNullOrEmpty(template.id))
                    continue;

                if (template.specificMapOnly)
                    continue;

                // Check exclusion list
                if (exclude != null && exclude.Contains(template)) continue;

                // Type and random rules
                if (!string.IsNullOrEmpty(requiredType))
                {
                    if (!string.Equals(template.type, requiredType, StringComparison.Ordinal))
                        continue;
                }
                else
                {
                    if (!template.allowRandom)
                        continue;

                    if (excludeSpecialTypesInRandomSelection && IsSpecialType(template.type))
                        continue;
                }

                // Check constraints
                if (template.difficultyLevel > maxDifficulty) continue;

                // Check usage count unless prototype mode ignores instance limits
                string templateKey = template.GetContentId();
                int currentUsage = templateUsageCount.ContainsKey(templateKey) ? templateUsageCount[templateKey] : 0;
                if (!prototypeMode && currentUsage >= template.maxInstances) continue;

                // Calculate weight (quadratic - kol * kol in AS3)
                int remaining = prototypeMode ? Mathf.Max(1, template.maxInstances) : (template.maxInstances - currentUsage);
                int weight = DrawWeight(remaining, template.difficultyLevel, maxDifficulty, prototypeMode);

                candidates.Add(template);
                weights.Add(weight);
            }

            if (candidates.Count == 0)
            {
                // Prototype mode fallback: if exclusion eliminated all candidates, retry without exclusion once.
                if (prototypeMode && allowExcludeFallbackRetry && exclude != null && exclude.Count > 0)
                {
                    Debug.LogWarning(
                        $"No valid room templates found with exclusion list (maxDifficulty: {maxDifficulty}, type: {requiredType}). " +
                        "Prototype mode retrying without exclusion.");
                    return SelectRandomRoomInternal(maxDifficulty, requiredType, null, allowExcludeFallbackRetry: false);
                }

                // AS3's exhaustion fallback (Land.as:926-935). When no room survives `kol > 0` — every
                // eligible room has been drawn its maximum number of times — AS3 does NOT leave the cell
                // empty: it re-pools on `rnd` alone, ignoring `kol`, `lvl` and the neighbour exclusions,
                // and draws from that.
                //
                // Without this the grid would develop holes once `maxInstances` is honoured, because a
                // null template makes WorldBuilder skip the cell (WorldBuilder.cs:808-813) and a land can
                // then be missing the room its own exit lives in.
                RoomTemplate exhausted = SelectFromExhaustedPool(maxDifficulty, requiredType);
                if (exhausted != null)
                {
                    CountUsage(exhausted);
                    return exhausted;
                }

                Debug.LogWarning($"No valid room templates found (maxDifficulty: {maxDifficulty}, type: {requiredType})");
                return null;
            }

            // Weighted random selection
            int totalWeight = 0;
            foreach (int w in weights) totalWeight += w;

            int random = _rng.Range(0, totalWeight);
            int cumulative = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                cumulative += weights[i];
                if (random < cumulative)
                {
                    CountUsage(candidates[i]);
                    return candidates[i];
                }
            }

            // Fallback (shouldn't reach here)
            return candidates[candidates.Count - 1];
        }

        /// <summary>
        /// AS3's draw weight for one room: <c>kol * kol</c> (<c>Land.as:909</c>), with the oracle's one
        /// exception (<c>Land.as:910-913</c>).
        ///
        /// <para>That exception: <c>if(_loc9_ == 4 &amp;&amp; _loc8_.lvl == 0 &amp;&amp; param1 &gt; 1)
        /// _loc9_ = 2;</c> — a stage-0 room with exactly two draws left keeps weight 4 on the shallow
        /// levels but drops to 2 once the land is deeper than 1, so the easy pool stops crowding out the
        /// rooms that only unlock below. <c>_loc9_ == 4</c> is <c>kol == 2</c>, i.e. "two draws left".</para>
        /// </summary>
        public static int DrawWeight(int remaining, int difficultyLevel, int stage, bool prototypeMode)
        {
            int weight = remaining * remaining;
            if (prototypeMode) return weight;

            if (weight == 4 && difficultyLevel == 0 && stage > 1)
            {
                return 2;
            }

            return weight;
        }

        /// <summary>
        /// AS3's exhaustion pool (<c>Land.as:926-935</c>): re-pool on <c>rnd</c> alone, so a land whose
        /// eligible rooms have all spent their <c>kol</c> still gets a room instead of a hole.
        ///
        /// <para><b>The gate is <c>rnd</c> and nothing else.</b> The oracle's <c>else</c> branch tests
        /// <c>if(_loc8_.rnd)</c> — not <c>lvl</c>, not <c>kol</c>, and the neighbour exclusions are gone
        /// with the first pool. Reproducing that exactly means a deeper room can appear here; that is the
        /// oracle's behaviour, not a bug to smooth over.</para>
        ///
        /// <para><c>null</c> when no room at all is <c>rnd</c>, which is the one case where AS3 would
        /// index an empty array and throw. Returning <c>null</c> lets the caller log and skip the cell
        /// instead.</para>
        /// </summary>
        private RoomTemplate SelectFromExhaustedPool(int maxDifficulty, string requiredType)
        {
            if (allTemplates == null) return null;

            List<RoomTemplate> pool = new List<RoomTemplate>();
            foreach (var template in allTemplates)
            {
                if (template == null || string.IsNullOrEmpty(template.id)) continue;
                if (template.specificMapOnly) continue;

                // A `tip`-driven draw keeps its tip: AS3's newRandomLoc(param5) only ever re-pools on
                // `rnd`, and the caller asked for a shape, so widening it here would answer with a room
                // the cell cannot use.
                if (!string.IsNullOrEmpty(requiredType))
                {
                    if (!string.Equals(template.type, requiredType, StringComparison.Ordinal)) continue;
                }
                else if (!template.allowRandom)
                {
                    continue;
                }

                pool.Add(template);
            }

            if (pool.Count == 0) return null;
            return pool[_rng.Range(0, pool.Count)];
        }

        /// <summary>
        /// Spend one of a template's <c>kol</c> (<c>Land.as:937</c>: <c>--_loc8_.kol</c>).
        /// </summary>
        private void CountUsage(RoomTemplate template)
        {
            string key = template.GetContentId();
            if (string.IsNullOrEmpty(key)) return;
            if (!templateUsageCount.ContainsKey(key)) templateUsageCount[key] = 0;
            templateUsageCount[key]++;
        }

        /// <summary>
        /// Select room by type.
        /// From AS3: Land.newTipLoc()
        /// </summary>
        public RoomTemplate SelectRoomByType(string roomType, List<RoomTemplate> exclude = null)
        {
            if (allTemplates == null || allTemplates.Count == 0)
            {
                Debug.LogWarning($"No valid rooms of type '{roomType}' found (after exclusion and usage check)");
                return null;
            }

            List<RoomTemplate> candidates = new List<RoomTemplate>();

            foreach (var template in allTemplates)
            {
                if (template.specificMapOnly)
                    continue;

                if (template.type == roomType)
                {
                    // Check exclusion list
                    if (exclude != null && exclude.Contains(template))
                        continue;

                    // Check usage count
                    string templateKey = template.GetContentId();
                    int currentUsage = templateUsageCount.ContainsKey(templateKey) ? templateUsageCount[templateKey] : 0;
                    if (!prototypeMode && currentUsage >= template.maxInstances)
                        continue;

                    candidates.Add(template);
                }
            }

            if (candidates.Count == 0)
            {
                Debug.LogWarning($"No valid rooms of type '{roomType}' found (after exclusion and usage check)");
                return null;
            }

            // Select random candidate
            RoomTemplate selected = candidates[_rng.Range(0, candidates.Count)];

            // Increment usage count
            string selectedKey = selected.GetContentId();
            if (!string.IsNullOrEmpty(selectedKey))
            {
                if (!templateUsageCount.ContainsKey(selectedKey))
                    templateUsageCount[selectedKey] = 0;
                templateUsageCount[selectedKey]++;
            }

            return selected;
        }

        private static bool IsSpecialType(string type)
        {
            if (string.IsNullOrEmpty(type))
            {
                return false;
            }

            return SpecialRoomTypes.Contains(type);
        }

        /// <summary>
        /// Reset usage counts.
        /// </summary>
        public void ResetUsageCounts()
        {
            // Collect keys first to avoid modifying during iteration
            var keys = new List<string>(templateUsageCount.Keys);
            foreach (var key in keys)
            {
                templateUsageCount[key] = 0;
            }
        }

        /// <summary>
        /// Get current usage count for a template.
        /// </summary>
        public int GetUsageCount(string templateId)
        {
            if (templateUsageCount.ContainsKey(templateId))
            {
                return templateUsageCount[templateId];
            }
            return 0;
        }
    }
}
