using System;
using UnityEngine;
using System.Collections.Generic;
using System.Globalization;
using PFE.Core.Ids;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using EntityId = PFE.Core.Ids.EntityId;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Populates rooms with objects, enemies, and interactive elements.
    /// Port of AS3 Location.setObjects(), createUnit(), createObj(), setRandomUnits().
    ///
    /// This bridges the gap between your RoomTemplate data (which has ObjectSpawnData
    /// and SpawnPointData) and the runtime RoomInstance (which has empty lists).
    ///
    /// Call flow (mirrors AS3):
    ///   1. RoomGenerator.GenerateRoom() - creates tiles, copies spawn data
    ///   2. DoorCarver.CarveAllDoors() - opens door passages
    ///   3. DoorCarver.ApplyBorder() - applies ramka border walls on existing edge cells
    ///   4. RoomPopulator.PopulateRoom() - spawns all entities <-- THIS CLASS
    /// </summary>
    public class RoomPopulator
    {
        private static PFE.Core.Rng.IRngService s_spawnRng;
        private static PFE.Core.Rng.IRngService GetSpawnRng(PFE.Core.Rng.IRngService rng) =>
            rng?.GetStream(PFE.Core.Rng.RngStream.Spawn) ?? (s_spawnRng ??= new PFE.Core.Rng.PcgRngService().GetStream(PFE.Core.Rng.RngStream.Spawn));

        /// <summary>
        /// Populate a room with all entities from its template data.
        /// Mirrors AS3 Location.setObjects().
        /// </summary>
        /// <param name="unitDefinitions">
        /// Where unit stat blocks come from. Defaults to the <c>Resources/Units</c> lookup; injectable so
        /// tests do not depend on which unit assets happen to be on disk.
        /// </param>
        public static void PopulateRoom(
            RoomInstance room,
            RoomTemplate template,
            RoomDifficulty difficulty,
            PFE.Core.Rng.IRngService rng = null,
            IUnitDefinitionProvider unitDefinitions = null)
        {
            if (room == null || template == null) return;

            var r = GetSpawnRng(rng);
            var spawnCounters = new Dictionary<string, int>();

            // Phase 1: Process template objects (from XML obj elements) sorted deterministically
            var sortedObjects = new List<ObjectSpawnData>(template.objects);
            sortedObjects.Sort((a, b) =>
            {
                int coordA = a.tileCoord.y * room.width + a.tileCoord.x;
                int coordB = b.tileCoord.y * room.width + b.tileCoord.x;
                int cmp = coordA.CompareTo(coordB);
                if (cmp != 0) return cmp;
                return string.CompareOrdinal(a.id ?? "", b.id ?? "");
            });

            foreach (var objData in sortedObjects)
            {
                string spawnType = !string.IsNullOrEmpty(objData.id) ? objData.id : (objData.type ?? "obj");
                if (!spawnCounters.TryGetValue(spawnType, out int count)) count = 0;
                spawnCounters[spawnType] = count + 1;
                var entityId = EntityId.CreateForRoomSpawn(room.id, spawnType, count);

                ProcessObjectSpawn(room, objData, difficulty, entityId, unitDefinitions, r);
            }

            // Phase 2: Place random enemies at spawn points
            PlaceRandomEnemies(room, template, difficulty, r, spawnCounters, unitDefinitions);

            // Phase 3: Place XP bonuses
            // AS3: createXpBonuses() places collectible XP orbs
            PlaceXpBonuses(room, 5, r, spawnCounters);
        }

        /// <summary>
        /// Process a single object spawn from template data.
        /// Mirrors AS3 Location.setObjects() inner loop + createObj() + createUnit().
        /// </summary>
        private static void ProcessObjectSpawn(
            RoomInstance room,
            ObjectSpawnData spawnData,
            RoomDifficulty difficulty,
            EntityId entityId = default,
            IUnitDefinitionProvider unitDefinitions = null,
            PFE.Core.Rng.IRngService rng = null)
        {
            if (spawnData == null) return;

            spawnData.EnsureStructuredData();

            Vector2Int placementTileCoord = ResolveLegacyPlacementTileCoord(room, spawnData.tileCoord);
            int nx = placementTileCoord.x;
            int ny = placementTileCoord.y;

            // Check if placement is valid (AS3: noHolesPlace check)
            var tile = room.GetTileAtCoord(new Vector2Int(nx, ny));
            if (tile != null && !tile.canPlaceObjects)
                return;

            // Imported room objects currently preserve Flash's top-down tile coordinates.
            // Convert them into the room's bottom-up pixel space here so existing imports stay valid.
            Vector2 bottomAnchorPixels = ResolveLegacyBottomAnchorPixels(room, spawnData);
            float pixelX = bottomAnchorPixels.x;
            float pixelY = bottomAnchorPixels.y;
            //float liftedObjectPixelY = pixelY + WorldConstants.TILE_SIZE; //prob need to make adjustable in map editor or offset

            switch (ResolvePlacementType(spawnData))
            {
                case "unit":
                    // The two sources here are NOT interchangeable, and AS3 reads them from different
                    // places:
                    //   * `turn` comes off the PLACED <obj> node — Unit.as:596-608 compares `param3.@turn`,
                    //     where param3 is the node handed to Unit.create() by Location.createUnit().
                    //   * `cl` comes off the DEFINITION row — Unit.as:702 reassigns its local `node` to
                    //     `AllData.d.obj.(@id == id)[0]` and :708 reads `node.@cl` from that. The placed
                    //     node never carries `cl` (0 of the 564 room assets in Resources/Rooms do).
                    // Passing the definition's `cl` is therefore correct, not a shortcut.
                    CreateUnit(
                        room,
                        unitId: spawnData.id,
                        x: pixelX,
                        y: pixelY,
                        entityId: entityId,
                        unitDefinitions: unitDefinitions,
                        controllerId: spawnData.definition != null
                            ? spawnData.definition.GetAttribute("cl", string.Empty)
                            : string.Empty,
                        attributes: spawnData.attributes,
                        turn: spawnData.GetAttribute("turn", null),
                        difficulty: ResolveLocationDifficulty(difficulty),
                        rng: rng);
                    break;

                case "box":
                case "door":
                    CreateObject(room, spawnData, pixelX, pixelY, entityId);
                    break;

                case "checkpoint":
                    CreateCheckpoint(room, spawnData, pixelX, pixelY, entityId);
                    break;

                case "area":
                    CreateArea(room, spawnData, placementTileCoord, entityId);
                    break;

                case "bonus":
                    CreateBonus(room, spawnData, pixelX, pixelY, entityId);
                    break;

                case "trap":
                    CreateTrap(room, spawnData, pixelX, pixelY, entityId);
                    break;

                default:
                    // Generic object
                    CreateObject(room, spawnData, pixelX, pixelY, entityId);
                    break;
            }
        }

        /// <summary>
        /// The placement bucket that decides how this spawn is instantiated.
        ///
        /// <see cref="ObjectSpawnData.type"/> is a string cached at import from the definition's
        /// <c>defaultPlacementType</c>, and it can be stale — Camp/room_1_0 literally serializes
        /// <c>type: obj</c> for its five training dummies, because the classifier had no
        /// <c>tip='unit'</c> branch when the room was imported (see MapObjectFamily.Unit). Trusting
        /// the cached string made every authored enemy and NPC spawn as a static prop.
        ///
        /// The definition is therefore consulted first, but only when it carries a <i>specific</i>
        /// bucket: a generic "obj" from the definition must not downgrade a specific stored bucket,
        /// which is what keeps <c>tarakan</c> a unit — its definition is still the stale generic
        /// value while DefaultAS3ObjectMapping supplies "unit" for it. Spawns with no definition
        /// (and the synthetic ones built in PlaceXpBonuses) keep using the stored string.
        /// </summary>
        private static string ResolvePlacementType(ObjectSpawnData spawnData)
        {
            string fromDefinition = spawnData?.definition?.GetResolvedPlacementType();

            if (!string.IsNullOrWhiteSpace(fromDefinition) &&
                !string.Equals(fromDefinition, MapObjectDefinition.GenericPlacementType, StringComparison.Ordinal))
            {
                return fromDefinition;
            }

            return string.IsNullOrWhiteSpace(spawnData?.type)
                ? MapObjectDefinition.GenericPlacementType
                : spawnData.type;
        }

        /// <summary>
        /// Create a unit (enemy, NPC, etc.) in the room.
        /// Simplified port of AS3 Location.createUnit().
        /// </summary>
        /// <param name="controllerId">
        /// The AS3 controller class from the placed <c>&lt;obj cl="…"&gt;</c> attribute. Empty for units
        /// with no authored placement (the random-enemy pass), which get the base controller.
        /// </param>
        /// <param name="attributes">
        /// The placement's own attributes (<c>turn</c>/<c>fix</c>/<c>tr</c>…). The controller reads them
        /// in its constructor, so they have to survive this call.
        /// </param>
        /// <param name="turn">
        /// The placement's raw <c>turn</c> attribute, or null when absent. <b>Raw, not resolved:</b> the
        /// facing coin flip and the variant roll draw from the same spawn stream, and the oracle takes
        /// them in that order (<c>Location.as:1185</c> <c>randomCid()</c> first, then
        /// <c>Unit.as:609-613</c>). A pre-resolved direction would force the caller to draw before this
        /// method runs and reverse the two.
        /// </param>
        /// <param name="difficulty">
        /// AS3 <c>Location.locDifLevel</c>, consumed by <see cref="UnitVariantResolver.RandomCid"/>.
        /// </param>
        private static void CreateUnit(
            RoomInstance room,
            string unitId,
            float x,
            float y,
            EntityId entityId = default,
            IUnitDefinitionProvider unitDefinitions = null,
            string controllerId = null,
            List<MapObjectAttributeData> attributes = null,
            string turn = null,
            float difficulty = 0f,
            PFE.Core.Rng.IRngService rng = null)
        {
            // The oracle does NOT spawn the id the room authored — it spawns a VARIANT of it.
            // Location.createUnit() (Location.as:1169/1185) calls randomCid() and hands the result to
            // Unit.create(), whose subclass joins the two (`UnitZombie.as:104 id = "zombie" + tr`).
            // Passing the authored id through unchanged is what made a placed `zombie` resolve to
            // Resources/Units/zombie.asset — the family template, which carries no <vis> art, no
            // <comb hp> and no <move speed> at all. See UnitVariantResolver for the full argument.
            //
            // `tr` wins over the rolled cid, matching UnitZombie.as:83-101's precedence (saved object,
            // then placement node, then cid). The placement node is the only layer the port has.
            // `NoContext`, not `default` — a defaulted struct has `LandY == 0`, which is a real land
            // index and would make `randomCid("ranger")` pick its `landY == 0` arm. See
            // UnitVariantContext's remarks.
            string resolvedId = UnitVariantResolver.ResolveSpawnId(
                unitId,
                difficulty,
                rng,
                UnitVariantResolver.NoContext,
                MapObjectDataUtility.GetAttribute(attributes, "tr", null));

            float health = ResolveUnitHealth(resolvedId, unitDefinitions);

            // Hoisted out of the initializer below because the digger roll has to come AFTER it.
            //
            // AS3's draw order at a spawn is: randomCid() (Location.as:1185) -> the base Unit
            // constructor's facing coin flip (Unit.as:609-613) -> the UnitZombie constructor's digger
            // roll (UnitZombie.as:120-127). All three share one stream, so rolling the digger before the
            // facing flip would swap two draws and shift every later placement in the room — a silent,
            // whole-map change that no test would name.
            int facing = UnitController.ResolveFacing(turn, 1, rng);
            int digger = ResolveDiggerTier(resolvedId, attributes, difficulty, rng);

            var unit = new UnitInstance
            {
                entityId = entityId.IsValid ? entityId.ToString() : string.Empty,
                unitId = resolvedId,
                unitType = resolvedId,
                position = new Vector2(x, y),
                isDead = false,
                maxHealth = health,
                currentHealth = health,
                controllerId = controllerId ?? string.Empty,
                attributes = MapObjectDataUtility.CloneAttributes(attributes),
                facingDirection = facing,
                digger = digger
            };

            // AS3 UnitZombie.setPos (UnitZombie.as:192-213) — the bury, resolved here because this is
            // the port's `setPos`: the tiles are final (RoomSetup applies the border and carves the
            // doors before it populates) and the room is not active yet, which is the state AS3's own
            // `!loc.active` guard describes.
            //
            //   solid floor    -> `this.zak = true; this.zakop();`   => bury, waiting in ambush
            //   no solid floor -> `this.zak = false;` and, for tier 2 only, `exterminate()`
            //
            // `exterminate()` is `loc.remObj(this)` + `disabled = true` (Unit.as:4411-4421), so a tier-2
            // unit over a catwalk or a drop is removed before it is ever drawn — it never appears. Tier 1
            // is left standing as an ordinary ghoul, which is what `zak = false` means: it keeps its
            // `digger` value (a scripted `command()` can still dig it out) but never buries on its own.
            bool groundSolid = room.HasWallUnderFeet(new Vector2(x, y));

            if (digger != 0 && !groundSolid && digger == 2)
            {
                return;
            }

            unit.ambushArmed = digger != 0 && groundSolid;

            room.units.Add(unit);
        }

        /// <summary>
        /// The zombie family's ambush tier — AS3 <c>UnitZombie.digger</c>
        /// (<c>UnitZombie.as:120-127</c>).
        ///
        /// <code>
        /// if(param3 &amp;&amp; param3.@dig.length()) { this.digger = param3.@dig; }
        /// else { this.digger = isrnd(Math.min(param2 / 20 + 0.25,0.75)) ? 1 : 0; }
        /// </code>
        ///
        /// <para><b>Placement first, and the value is not clamped.</b> <c>param3</c> is the placed
        /// <c>&lt;obj&gt;</c> node — the same node whose <c>turn</c> the base constructor reads — so the
        /// authored <c>dig=</c> wins over the roll. AS3 stores it verbatim and only ever tests it for
        /// truthiness and against 1/2/3, so an out-of-range value falls through to the "senses nothing"
        /// branch rather than being coerced; clamping here would silently turn an authored
        /// <c>dig='5'</c> into a permanently inert tier 3.</para>
        ///
        /// <para><b><c>param2</c> is the difficulty, not the unit's health.</b> <c>Unit.create</c> is
        /// <c>(id, dif, xml, loadObj, ncid)</c> and <c>Location.as:1175/1187</c> passes
        /// <c>this.locDifLevel</c>; the base <c>Unit</c> constructor ignores the slot entirely (its hp
        /// comes from <c>&lt;hp&gt;</c> in <c>getXmlParam</c>). So the chance is <b>25 % at difficulty
        /// 0</b>, rising to the 75 % cap at difficulty 10 — which is why a level-0 room still hides a
        /// quarter of its zombies in the floor.</para>
        ///
        /// <para><b>Nothing in the shipped data authors <c>dig=</c></b>, so today every zombie takes the
        /// roll. That is recorded rather than assumed: the attribute is honoured because the oracle
        /// honours it, and a future room that places one will get an authored ambusher.</para>
        ///
        /// <para><b>Only the zombie family rolls, and the roll is therefore inside the family gate.</b>
        /// The draw belongs to <c>UnitZombie</c>'s constructor and to nothing else — a raider or a
        /// training dummy never asks the stream for it. Rolling unconditionally would consume an extra
        /// draw per non-zombie placement, which shifts every later placement in the room: no error, no
        /// warning, and a whole map that quietly differs from the one AS3 would have generated.</para>
        /// </summary>
        private static int ResolveDiggerTier(
            string unitId,
            List<MapObjectAttributeData> attributes,
            float difficulty,
            PFE.Core.Rng.IRngService rng)
        {
            if (!RollsAmbushTier(unitId))
            {
                return 0;
            }

            string authored = MapObjectDataUtility.GetAttribute(attributes, "dig", null);

            if (!string.IsNullOrWhiteSpace(authored))
            {
                if (int.TryParse(authored, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tier))
                {
                    return tier;
                }

                // AS3 would store the raw string and then compare it against integers, so it would be
                // truthy (burying) with no matching tier branch. Naming the value is the only way that
                // becomes diagnosable instead of looking like a roll.
                Debug.LogWarning(
                    $"[RoomPopulator] Placement attribute dig='{authored}' is not an integer; ignoring " +
                    $"it and rolling instead. AS3 would treat the raw string as a truthy tier with no " +
                    $"matching branch.");
            }

            float chance = Mathf.Min(difficulty / 20f + 0.25f, 0.75f);
            return rng != null && rng.Chance(chance) ? 1 : 0;
        }

        /// <summary>
        /// Whether the zombie family owns this id, i.e. whether AS3's <c>UnitZombie</c> constructor is
        /// the one that ran for it.
        ///
        /// <para><b>Why an id test rather than a flag.</b> The oracle makes the id and the tier in the
        /// same constructor — <c>id = "zombie" + this.tr;</c> at <c>UnitZombie.as:104</c>, the roll at
        /// <c>:120-127</c> — so "the id starts with <c>zombie</c>" and "a digger was rolled" are the same
        /// statement, and the port can read the first instead of inventing a second source of truth. A
        /// definition flag would have to be kept in sync with the controller table in
        /// <c>RoomUnitSpawner</c>, which is a second place to get it wrong.</para>
        ///
        /// <para>The family is <c>zombie</c> (the template) plus <c>zombie0</c>..<c>zombie9</c> — 11
        /// assets, all in <c>Resources/Units</c>; no other unit id in the project begins with the
        /// word.</para>
        /// </summary>
        private static bool RollsAmbushTier(string unitId)
        {
            return !string.IsNullOrEmpty(unitId) &&
                   unitId.StartsWith("zombie", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The port's analogue of AS3 <c>Location.locDifLevel</c> — the number
        /// <see cref="UnitVariantResolver.RandomCid"/> scales its thresholds against.
        ///
        /// <para><c>Land.setLocDif()</c> (<c>Land.as:977-995</c>) computes
        /// <c>locDifLevel = landDifLevel + &lt;land modifier&gt;</c> and then assigns
        /// <c>enemyLevel = locDifLevel</c>, so <c>enemyLevel</c> is the field that carries it. (The
        /// oracle afterwards adds a <c>globalDif</c> term to <c>enemyLevel</c> <i>only</i>, so the two
        /// part company when <c>globalDif &gt; 2</c>; the port has no <c>globalDif</c>, so they coincide
        /// here — recorded rather than papered over.)</para>
        /// </summary>
        private static float ResolveLocationDifficulty(RoomDifficulty difficulty)
        {
            return difficulty != null ? difficulty.enemyLevel : 0f;
        }

        /// <summary>
        /// A unit's maximum health, read from its <see cref="UnitDefinition"/>.
        ///
        /// <para><b>This replaces a guess table.</b> <c>CalculateUnitHealth</c> listed nine ids and fell
        /// through to <c>_ => 50f</c>; <c>"training"</c> was not among them, so the dummy reported 50
        /// where <c>Resources/Units/training.asset</c> says <b>500</b> — and a missing id was
        /// indistinguishable from a unit that really has 50 hp. The definition is the source of truth;
        /// when it cannot be resolved the id is named in the warning instead of being silently
        /// approximated.</para>
        ///
        /// <para><b>No level scaling here, deliberately.</b> The guess table multiplied by
        /// <c>1 + level * 0.15</c> as an approximation. AS3 scales a unit by level in
        /// <c>setLevel()</c>, which is a per-controller override — <c>UnitTrain.as:78-80</c> makes it a
        /// no-op precisely because a training dummy must not scale. That belongs to the controller, not
        /// to the spawn record, so this returns the definition's base value and the controller decides.</para>
        /// </summary>
        private static float ResolveUnitHealth(string unitId, IUnitDefinitionProvider unitDefinitions)
        {
            IUnitDefinitionProvider provider = unitDefinitions ?? ResourcesUnitDefinitionProvider.Shared;

            if (provider.TryGetUnit(unitId, out UnitDefinition definition) && definition != null)
            {
                return definition.health;
            }

            Debug.LogWarning(
                $"[RoomPopulator] No UnitDefinition for '{unitId}' — using UnitDefinition.DefaultHealth " +
                $"({UnitDefinition.DefaultHealth}). Run 'PFE/Data/Import Units' if this unit should carry " +
                $"its own stats.");

            return UnitDefinition.DefaultHealth;
        }

        /// <summary>
        /// Create a box/door/interactive object.
        /// Simplified port of AS3 Location.createObj() for "box" and "door" types.
        /// </summary>
        private static void CreateObject(RoomInstance room, ObjectSpawnData spawnData, float x, float y, EntityId entityId = default)
        {
            spawnData?.EnsureStructuredData();

            var obj = new ObjectInstance
            {
                entityId = entityId.IsValid ? entityId.ToString() : string.Empty,
                objectId = spawnData?.id ?? string.Empty,
                objectType = spawnData?.type ?? "obj",
                definitionId = spawnData?.GetResolvedDefinitionId() ?? string.Empty,
                definition = spawnData?.definition,
                code = spawnData?.code ?? string.Empty,
                uid = spawnData?.uid ?? string.Empty,
                attributes = MapObjectDataUtility.CloneAttributes(spawnData?.attributes),
                items = MapObjectDataUtility.CloneItems(spawnData?.items),
                scripts = MapObjectDataUtility.CloneScripts(spawnData?.scripts),
                parameters = spawnData?.parameters ?? string.Empty,
                position = new Vector2(x, y),
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };

            obj.runtimeState.InitializeFromAttributes(obj.attributes);
            obj.InitializeDynamicRuntimeState();
            obj.RefreshLegacyParameters();
            room.AddObject(obj);
        }

        /// <summary>
        /// Create a checkpoint/save point.
        /// From AS3: Location.createCheck() + CheckPoint class.
        /// </summary>
        private static void CreateCheckpoint(RoomInstance room, ObjectSpawnData spawnData, float x, float y, EntityId entityId = default)
        {
            spawnData?.EnsureStructuredData();

            var obj = new ObjectInstance
            {
                entityId = entityId.IsValid ? entityId.ToString() : string.Empty,
                objectId = spawnData?.id ?? string.Empty,
                objectType = "checkpoint",
                definitionId = spawnData?.GetResolvedDefinitionId() ?? string.Empty,
                definition = spawnData?.definition,
                code = spawnData?.code ?? string.Empty,
                uid = spawnData?.uid ?? string.Empty,
                attributes = MapObjectDataUtility.CloneAttributes(spawnData?.attributes),
                items = MapObjectDataUtility.CloneItems(spawnData?.items),
                scripts = MapObjectDataUtility.CloneScripts(spawnData?.scripts),
                parameters = spawnData?.parameters ?? string.Empty,
                position = new Vector2(x, y),
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };

            obj.runtimeState.InitializeFromAttributes(obj.attributes);
            obj.InitializeDynamicRuntimeState();
            obj.RefreshLegacyParameters();
            room.AddObject(obj);

            // Add as spawn point for player
            room.spawnPoints.Add(new SpawnPoint
            {
                tileCoord = WorldCoordinates.PixelToTile(new Vector2(x, y)),
                type = SpawnType.Player
            });
        }

        /// <summary>
        /// Create area trigger.
        /// </summary>
        private static void CreateArea(RoomInstance room, ObjectSpawnData data, EntityId entityId = default)
        {
            CreateArea(room, data, ResolveLegacyPlacementTileCoord(room, data.tileCoord), entityId);
        }

        private static void CreateArea(RoomInstance room, ObjectSpawnData data, Vector2Int placementTileCoord, EntityId entityId = default)
        {
            if (data == null)
            {
                return;
            }

            data.EnsureStructuredData();

            var obj = new ObjectInstance
            {
                entityId = entityId.IsValid ? entityId.ToString() : string.Empty,
                objectId = data.id,
                objectType = "area",
                definitionId = data.GetResolvedDefinitionId(),
                definition = data.definition,
                code = data.code ?? string.Empty,
                uid = data.uid ?? string.Empty,
                attributes = MapObjectDataUtility.CloneAttributes(data.attributes),
                items = MapObjectDataUtility.CloneItems(data.items),
                scripts = MapObjectDataUtility.CloneScripts(data.scripts),
                parameters = data.parameters ?? string.Empty,
                position = WorldCoordinates.TileToPixel(placementTileCoord),
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };

            obj.runtimeState.InitializeFromAttributes(obj.attributes);
            obj.InitializeDynamicRuntimeState();
            obj.RefreshLegacyParameters();
            room.AddObject(obj);
        }

        /// <summary>
        /// Create bonus pickup.
        /// </summary>
        private static void CreateBonus(RoomInstance room, ObjectSpawnData spawnData, float x, float y, EntityId entityId = default)
        {
            spawnData?.EnsureStructuredData();

            var obj = new ObjectInstance
            {
                entityId = entityId.IsValid ? entityId.ToString() : string.Empty,
                objectId = spawnData?.id ?? string.Empty,
                objectType = "bonus",
                definitionId = spawnData?.GetResolvedDefinitionId() ?? string.Empty,
                definition = spawnData?.definition,
                code = spawnData?.code ?? string.Empty,
                uid = spawnData?.uid ?? string.Empty,
                attributes = MapObjectDataUtility.CloneAttributes(spawnData?.attributes),
                items = MapObjectDataUtility.CloneItems(spawnData?.items),
                scripts = MapObjectDataUtility.CloneScripts(spawnData?.scripts),
                parameters = spawnData?.parameters ?? string.Empty,
                position = new Vector2(x, y),
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };

            obj.runtimeState.InitializeFromAttributes(obj.attributes);
            obj.InitializeDynamicRuntimeState();
            obj.RefreshLegacyParameters();
            room.AddObject(obj);
        }

        /// <summary>
        /// Create trap object.
        /// </summary>
        private static void CreateTrap(RoomInstance room, ObjectSpawnData spawnData, float x, float y, EntityId entityId = default)
        {
            spawnData?.EnsureStructuredData();

            var obj = new ObjectInstance
            {
                entityId = entityId.IsValid ? entityId.ToString() : string.Empty,
                objectId = spawnData?.id ?? string.Empty,
                objectType = "trap",
                definitionId = spawnData?.GetResolvedDefinitionId() ?? string.Empty,
                definition = spawnData?.definition,
                code = spawnData?.code ?? string.Empty,
                uid = spawnData?.uid ?? string.Empty,
                attributes = MapObjectDataUtility.CloneAttributes(spawnData?.attributes),
                items = MapObjectDataUtility.CloneItems(spawnData?.items),
                scripts = MapObjectDataUtility.CloneScripts(spawnData?.scripts),
                parameters = spawnData?.parameters ?? string.Empty,
                position = new Vector2(x, y),
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };

            obj.runtimeState.InitializeFromAttributes(obj.attributes);
            obj.InitializeDynamicRuntimeState();
            obj.RefreshLegacyParameters();
            room.AddObject(obj);
        }

        /// <summary>
        /// Place random enemies from spawn point data.
        /// Mirrors AS3 Location.setRandomUnits().
        /// </summary>
        private static void PlaceRandomEnemies(
            RoomInstance room,
            RoomTemplate template,
            RoomDifficulty difficulty,
            PFE.Core.Rng.IRngService rng,
            Dictionary<string, int> spawnCounters = null,
            IUnitDefinitionProvider unitDefinitions = null)
        {
            // Use spawn points marked as enemy spawns
            var enemySpawns = new List<SpawnPoint>();
            foreach (var sp in room.spawnPoints)
            {
                if (sp.type == SpawnType.Enemy || sp.type == SpawnType.Boss)
                {
                    enemySpawns.Add(sp);
                }
            }

            if (enemySpawns.Count == 0) return;

            // Sort deterministically before shuffling
            enemySpawns.Sort((a, b) =>
            {
                int coordA = a.tileCoord.y * room.width + a.tileCoord.x;
                int coordB = b.tileCoord.y * room.width + b.tileCoord.x;
                return coordA.CompareTo(coordB);
            });

            // Determine enemy count based on difficulty
            int totalDesired = difficulty.GetTotalEnemyCount();
            if (totalDesired <= 0) totalDesired = 3; // Fallback default
            int enemyCount = Mathf.Min(totalDesired, enemySpawns.Count);

            // Shuffle spawn points deterministically
            rng.Shuffle(enemySpawns);

            for (int i = 0; i < enemyCount && i < enemySpawns.Count; i++)
            {
                var spawn = enemySpawns[i];
                string unitId = !string.IsNullOrEmpty(spawn.unitId) ? spawn.unitId : "raider";

                float px = (spawn.tileCoord.x + 0.5f) * WorldConstants.TILE_SIZE;
                float py = (spawn.tileCoord.y + 1f) * WorldConstants.TILE_SIZE - 1;

                EntityId entityId = default;
                if (spawnCounters != null)
                {
                    if (!spawnCounters.TryGetValue(unitId, out int count)) count = 0;
                    spawnCounters[unitId] = count + 1;
                    entityId = EntityId.CreateForRoomSpawn(room.id, unitId, count);
                }

                // No authored `cl` and no per-placement flags on this path: the base controller.
                //
                // AS3 reaches this same conclusion differently. setRandomUnits() passes the spawn point's
                // own <obj> node to createUnit() (Location.as:1081 `this.ups[...].xml`), so the Unit
                // constructor's `if(param3)` is TRUE and, when that node carries no `turn`, it coin-flips
                // (Unit.as:609-613). Passing `turn: null` here reproduces that.
                //
                // It must NOT read SpawnPoint.facingDirection: that field is a constant 1 in this port —
                // no importer writes it and AS3ToUnityConverter.FindSpawnPoints only ever emits the
                // player spawn — so using it would silently pin every random enemy to facing right.
                CreateUnit(
                    room,
                    unitId: unitId,
                    x: px,
                    y: py,
                    entityId: entityId,
                    unitDefinitions: unitDefinitions,
                    turn: null,
                    difficulty: ResolveLocationDifficulty(difficulty),
                    rng: rng);
            }
        }

        /// <summary>
        /// Place XP bonus collectibles.
        /// Mirrors AS3 Location.createXpBonuses().
        /// Tries to place bonuses in empty air tiles across room quadrants.
        /// </summary>
        private static void PlaceXpBonuses(RoomInstance room, int maxBonuses, PFE.Core.Rng.IRngService rng, Dictionary<string, int> spawnCounters = null)
        {
            int placed = 0;
            int quadrant = 4; // Start top-left, cycle through quadrants

            for (int attempt = 0; attempt < 100 && placed < maxBonuses; attempt++)
            {
                // Calculate quadrant bounds (AS3 cycles through 4 quadrants)
                int minX = 2, maxX = room.width - 2;
                int minY = 2, maxY = room.height - 2;

                switch (quadrant)
                {
                    case 4: maxX = room.width / 2; maxY = room.height / 2; break;
                    case 3: minX = room.width / 2; maxY = room.height / 2; break;
                    case 2: maxX = room.width / 2; minY = room.height / 2; break;
                    case 1: minX = room.width / 2; minY = room.height / 2; break;
                }

                int tx = rng.Range(minX, maxX);
                int ty = rng.Range(minY, maxY);

                var tile = room.GetTileAtCoord(new Vector2Int(tx, ty));
                if (tile != null && tile.physicsType == TilePhysicsType.Air)
                {
                    // Check adjacent tile is also air (AS3 checks left/right neighbor)
                    var leftTile = room.GetTileAtCoord(new Vector2Int(tx - 1, ty));
                    var rightTile = room.GetTileAtCoord(new Vector2Int(tx + 1, ty));
                    if ((leftTile != null && leftTile.physicsType == TilePhysicsType.Air) ||
                        (rightTile != null && rightTile.physicsType == TilePhysicsType.Air))
                    {
                        EntityId entityId = default;
                        if (spawnCounters != null)
                        {
                            string bonusType = "bonus_xp";
                            if (!spawnCounters.TryGetValue(bonusType, out int count)) count = 0;
                            spawnCounters[bonusType] = count + 1;
                            entityId = EntityId.CreateForRoomSpawn(room.id, bonusType, count);
                        }

                        CreateBonus(room, new ObjectSpawnData
                        {
                            id = "xp",
                            type = "bonus",
                            tileCoord = new Vector2Int(tx, ty)
                        },
                            (tx + 0.5f) * WorldConstants.TILE_SIZE,
                            (ty + 0.5f) * WorldConstants.TILE_SIZE,
                            entityId);
                        placed++;
                        if (quadrant > 0) quadrant--;
                    }
                }
            }
        }

        private static Vector2Int ResolveLegacyPlacementTileCoord(RoomInstance room, Vector2Int legacyTileCoord)
        {
            int borderOffset = room != null ? Mathf.Max(0, room.borderOffset) : 0;
            int roomHeight = room != null ? room.height : WorldConstants.ROOM_HEIGHT;

            return new Vector2Int(
                legacyTileCoord.x + borderOffset,
                roomHeight - borderOffset - 1 - legacyTileCoord.y);
        }

        private static Vector2 ResolveLegacyBottomAnchorPixels(RoomInstance room, ObjectSpawnData spawnData)
        {
            int borderOffset = room != null ? Mathf.Max(0, room.borderOffset) : 0;
            int roomHeight = room != null ? room.height : WorldConstants.ROOM_HEIGHT;
            float sizeTiles = Mathf.Max(1f, ResolvePlacementSizeTiles(spawnData));

            return new Vector2(
                (spawnData.tileCoord.x + borderOffset + 0.5f * sizeTiles) * WorldConstants.TILE_SIZE,
                (roomHeight - borderOffset - spawnData.tileCoord.y - 1) * WorldConstants.TILE_SIZE + 1f);
        }

        private static int ResolvePlacementSizeTiles(ObjectSpawnData spawnData)
        {
            if (spawnData?.definition != null)
            {
                return Mathf.Max(1, spawnData.definition.size);
            }

            string rawSize = spawnData?.GetAttribute("size", string.Empty);
            if (int.TryParse(rawSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSize))
            {
                return Mathf.Max(1, parsedSize);
            }

            return 1;
        }
    }
}
