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

        /// <summary>The object a prob room's return door is placed as (AS3 <c>doorout</c>).</summary>
        public const string ProbReturnObjectId = "doorout";

        /// <summary>
        /// The <c>uid</c> of the return door a prob room is entered by. <c>Probation.doorsOnOff</c>
        /// keeps exactly this door visible while sealing the room's other exits
        /// (<c>Probation.as:299-308</c>).
        /// </summary>
        public const string ProbReturnBeginUid = "begin";

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

            // Phase 3: Place XP bonuses — but NOT in a prob room.
            //
            // AS3's setObjects() ends at setRandomUnits() (Location.as:1031-1033); createXpBonuses is a
            // step of the *conf loop*, not of setObjects — `_loc10_ = true` is set per cell at
            // Land.as:509 and `createXpBonuses(5)` is called at :672-674 (only confs 4 and 7 clear the
            // flag, and only for the land's begin cell). A prob room never passes through that loop:
            // buildProb creates it (Land.as:789) and buildProbs then calls setObjects/preStep/prepare on
            // it (:760-766). So a prob room gets no XP bonuses — and giving it some would hand the player
            // five free orbs every time they walk through a room that is re-entered on every descent.
            //
            // `probId` is the discriminator, the same one ProbTransition.AdmitsGridStep uses: it is
            // non-empty for exactly the rooms AS3 keeps in `probs`.
            if (string.IsNullOrEmpty(room.probId))
            {
                PlaceXpBonuses(room, 5, r, spawnCounters);
            }
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
                        rng: rng,
                        definitionCid: spawnData.definition != null
                            ? spawnData.definition.GetAttribute("cid", null)
                            : null);
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
        /// <param name="definitionCid">
        /// The <b>definition row's</b> <c>@cid</c> (not the placement's), or null when absent. AS3 reads
        /// it off <c>AllData.d.obj.(@id == id)[0]</c> inside <c>Unit.create</c>
        /// (<c>Unit.as:853-862</c>) and the subclass maps it onto its own id — this is how
        /// <c>trplate</c> spawns <c>trigplate</c> and <c>turret</c> spawns <c>turret0</c>.
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
            PFE.Core.Rng.IRngService rng = null,
            string definitionCid = null)
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
            //
            // The definition's `@cid` is the cid *seed* (Unit.as:853-862): a room places `trplate`, the
            // definition row says `cid='trigplate'`, and the spawned id is `trigplate`. It is read off
            // the DEFINITION row, not the placement — 0 of the shipped placements carry `@cid`, while 24
            // definitions do. Without it the alias families (turret/trplate/expl1/…) compose from a null
            // seed and resolve to nothing.
            string resolvedId = UnitVariantResolver.ResolveSpawnId(
                unitId,
                difficulty,
                rng,
                UnitVariantResolver.NoContext,
                MapObjectDataUtility.GetAttribute(attributes, "tr", null),
                definitionCid);

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
        /// AS3 <c>Location.createExit(param)</c> (<c>Location.as:2113-2121</c>): an <c>exit</c> box dropped on
        /// a spawn point, carrying <c>prob = exitProb + param</c>.
        ///
        /// <para>The <c>prob</c> attribute is load-bearing: <c>Interact.allAct</c> tests <c>prob</c> first
        /// (<c>Interact.as:1558</c>), so the bottom-row exit <em>enters the exit room</em> rather than
        /// advancing the level. The level advance happens on the exit room's own <c>exit</c> object, which
        /// carries no <c>prob</c>.</para>
        /// </summary>
        /// <param name="exitProb">AS3 <c>LandAct.exitProb</c> — the land's <c>exit</c> attribute prefix.</param>
        /// <param name="suffix">AS3 <c>param</c>: empty for the shallow exit, <c>"1"</c> for the hand-off.</param>
        public static ObjectInstance PlaceExit(RoomInstance room, string exitProb, string suffix = "")
        {
            if (room == null) return null;

            var spawn = new ObjectSpawnData
            {
                id = "exit",
                type = "exit",
                definitionId = "exit",
                attributes = SyntheticPlacementAttributes(new List<MapObjectAttributeData>
                {
                    new MapObjectAttributeData { key = "prob", value = (exitProb ?? string.Empty) + (suffix ?? string.Empty) },
                    new MapObjectAttributeData { key = "inter", value = "8" },
                }),
            };

            Vector2 pos = ChooseObjectPixel(room, spawn);
            CreateObject(room, spawn, pos.x, pos.y);
            return room.objects.Count > 0 ? room.objects[room.objects.Count - 1] : null;
        }

        /// <summary>
        /// Port-only carrier for AS3 <c>createCheck</c>'s <c>param1</c> — whether this is the
        /// <b>begin</b> checkpoint.
        ///
        /// <para><b>Why the port needs a carrier and AS3 does not.</b> AS3 uses <c>param1</c> inside the
        /// one function that has it — to skip the locked-variant roll and to call
        /// <c>cp.activate(true)</c> — and then forgets it. The port places the checkpoint in
        /// <see cref="PlaceCheckpointMarker"/> but decides what an activation <i>does</i> in
        /// <c>DoorPropPresenter</c>, a different object in a different layer, so the fact has to be
        /// written down. An attribute is the port's idiom for a placement-level fact (see the
        /// <c>prob</c>/<c>uid</c> attributes on the synthetic doors) and it survives a rebuild for the
        /// same reason.</para>
        ///
        /// <para><b>This replaced a derivation that had gone stale.</b> The presenter used to read
        /// begin-ness back off the object id as <c>objectId == "checkpoint1"</c> — written when
        /// <c>checkpoint1</c> was the begin checkpoint. That was corrected (the begin checkpoint is the
        /// plain <c>checkpoint</c>), which silently made the test unsatisfiable, so <c>isBegin</c> became
        /// permanently <c>false</c>. Nothing failed; the flag just stopped being the flag.</para>
        /// </summary>
        public const string BeginCheckpointAttribute = "beg";

        /// <summary>
        /// AS3 <c>Location.createCheck(isBeg)</c> (<c>Location.as:2088-2111</c>): a checkpoint object plus a
        /// player spawn point. This is the <em>placement</em> half only — making the checkpoint activatable
        /// (write <c>currentCP</c>, save) is a separate item.
        ///
        /// <para><b>The id is the plain, unlocked <c>checkpoint</c> unless the oracle rolls a variant.</b>
        /// AS3 appends a random <c>1..5</c> only when the checkpoint is <i>not</i> the begin one, the land
        /// is random and a 50% roll passes (<c>Location.as:2095-2098</c>):
        /// <c>if(!param1 &amp;&amp; this.land.rnd &amp;&amp; Math.random() &lt; 0.5)</c>. Those suffixed ids are
        /// the locked/mined variants — <c>AllData.as:5008-5012</c> gives <c>checkpoint1</c> <c>lock='1.4'</c>
        /// ("КТ с замком", a checkpoint with a lock), <c>checkpoint4</c> <c>mine='1'</c>, and so on — while
        /// plain <c>checkpoint</c> carries neither. This method previously used <c>checkpoint1</c> for the
        /// <b>begin</b> checkpoint, which is exactly backwards: it put the locked variant at the one
        /// checkpoint the oracle guarantees is unlocked, so the checkpoint the player starts at drew with a
        /// lock on it.</para>
        ///
        /// <para><b>The roll is opt-in, and the call site does not opt in yet.</b> It runs only when the
        /// caller supplies both <paramref name="landIsRandom"/> and <paramref name="rng"/>. The oracle
        /// rolls; the port deliberately does not, because a locked checkpoint's only way open is
        /// lockpicking, and the port's lockpicking model
        /// (<c>PFE.Systems.RPG.LockAttemptSystem</c>, a faithful port of <c>Interact.unlock</c>) currently
        /// has <b>no interaction surface wired to it</b>. Placing locked checkpoints before that exists
        /// would turn a working save point into a permanently dead one. The roll is implemented and tested
        /// here so enabling it is a one-line change at the call site once lockpicking is reachable — not a
        /// rewrite.</para>
        /// </summary>
        /// <param name="isBegin">AS3 <c>param1</c> — the begin checkpoint, which never rolls a variant.</param>
        /// <param name="landIsRandom">AS3 <c>this.land.rnd</c> — only a procedural land rolls.</param>
        /// <param name="rng">The stream to draw from. AS3 uses the global <c>Math.random()</c>; the port
        /// threads a seeded stream so a build is reproducible.</param>
        public static ObjectInstance PlaceCheckpointMarker(
            RoomInstance room,
            bool isBegin,
            bool landIsRandom = false,
            PFE.Core.Rng.IRngService rng = null)
        {
            if (room == null) return null;

            string id = ResolveCheckpointObjectId(isBegin, landIsRandom, rng);

            var attributes = SyntheticPlacementAttributes();
            if (isBegin)
            {
                attributes.Add(new MapObjectAttributeData
                {
                    key = BeginCheckpointAttribute,
                    value = "1",
                });
            }

            var spawn = new ObjectSpawnData
            {
                id = id,
                type = "checkpoint",
                definitionId = id,
                attributes = attributes,
            };

            Vector2 pos = ChooseObjectPixel(room, spawn);
            CreateCheckpoint(room, spawn, pos.x, pos.y);
            return room.objects.Count > 0 ? room.objects[room.objects.Count - 1] : null;
        }

        /// <summary>
        /// AS3 <c>createCheck</c>'s id roll, drawn in the oracle's order.
        ///
        /// <para><b>The second draw is conditional on the first, and that is why this is a statement
        /// sequence rather than one expression.</b> AS3 evaluates <c>Math.random() &lt; 0.5</c> as the
        /// <c>if</c> condition and only reaches <c>Math.floor(Math.random() * 5 + 1)</c> when it passed.
        /// Folding both draws into a single call — <c>rng.NextInt(5)</c> as an argument alongside the
        /// coin — would consume one extra value from the shared spawn stream on every failed roll, and
        /// every later placement in the same build would shift.</para>
        /// </summary>
        public static string ResolveCheckpointObjectId(
            bool isBegin, bool landIsRandom, PFE.Core.Rng.IRngService rng)
        {
            if (isBegin || !landIsRandom || rng == null)
            {
                return PFE.Sim.Campaign.CheckpointRules.PlainCheckpointId;
            }

            bool variantRollPassed = rng.NextInt(2) == 0;
            int variantRoll = variantRollPassed
                ? rng.NextInt(PFE.Sim.Campaign.CheckpointRules.LockedVariantCount) + 1
                : 0;

            return PFE.Sim.Campaign.CheckpointRules.SelectObjectId(
                isBegin, landIsRandom, variantRollPassed, variantRoll);
        }

        /// <summary>
        /// AS3 <c>Location.createDoorProb(did, pid)</c> (<c>Location.as:2124-2135</c>): the trial/battle
        /// door that stands in a normal room and opens into a detached prob room.
        ///
        /// <para><b>It refuses when the room has no spawn point</b>, and that is not defensive
        /// programming — the oracle returns <c>false</c> and <c>newRandomProb</c> then places nothing at
        /// all (<c>Land.as:857-860</c>). Returning <c>null</c> here reproduces that: the caller must not
        /// invent a position for a door the oracle would have skipped.</para>
        ///
        /// <para><b>The instance attributes override the definition's.</b> <c>AllData.as:5018-5019</c>
        /// gives <c>doorprob</c>/<c>doorboss</c> <c>time='30'</c>, but <c>createDoorProb</c> writes
        /// <c>time='20'</c> on every placement, so the placed door is the 20-second one. <c>inter='8'</c>
        /// agrees with the definition and is written for the same reason — the placement is the record
        /// that the oracle's own attributes were these.</para>
        ///
        /// <para><b><c>nazv</c> is <c>Res.txt("m", pid)</c></b> in AS3, and the port has no localisation
        /// table, so — following the precedent set for effect definitions — the raw id is stored in its
        /// place rather than an invented display string.</para>
        ///
        /// <para><b>Known divergence.</b> AS3 picks a <i>random</i> spawn point
        /// (<c>Location.as:2130</c>); this picks the first, which is what <see cref="PlaceExit"/> does
        /// too. Kept consistent deliberately so the two door placements cannot disagree.</para>
        /// </summary>
        /// <param name="doorObjectId">AS3 <c>did</c> — <c>doorprob</c> or <c>doorboss</c>.</param>
        /// <param name="probId">AS3 <c>pid</c> — the prob room this door opens into.</param>
        /// <param name="displayName">
        /// AS3 <c>nazv</c>. Defaults to <paramref name="probId"/>, the port's substitute for
        /// <c>Res.txt("m", pid)</c>.
        /// </param>
        /// <returns>The placed object, or <c>null</c> when nothing was placed.</returns>
        public static ObjectInstance PlaceProbDoor(
            RoomInstance room,
            string doorObjectId,
            string probId,
            string displayName = null)
        {
            if (room == null) return null;
            if (string.IsNullOrEmpty(doorObjectId)) return null;

            // AS3 guards on `this.spawnPoints.length > 0` and returns false otherwise.
            if (room.spawnPoints == null || room.spawnPoints.Count == 0) return null;

            var spawn = new ObjectSpawnData
            {
                id = doorObjectId,
                type = "box",
                definitionId = doorObjectId,
                attributes = SyntheticPlacementAttributes(new List<MapObjectAttributeData>
                {
                    // Read first by Interact.allAct (Interact.as:1558) — this is what makes the door
                    // enter a prob room rather than fall through to the object's own behaviour.
                    new MapObjectAttributeData { key = "prob", value = probId ?? string.Empty },
                    new MapObjectAttributeData { key = "nazv", value = displayName ?? probId ?? string.Empty },
                    new MapObjectAttributeData { key = "time", value = "20" },
                    new MapObjectAttributeData { key = "inter", value = "8" },
                }),
            };

            Vector2 pos = ChooseObjectPixel(room, spawn);
            CreateObject(room, spawn, pos.x, pos.y);
            return room.objects.Count > 0 ? room.objects[room.objects.Count - 1] : null;
        }

        /// <summary>
        /// The <c>doorout</c> box a prob room carries so the player can leave
        /// (AS3 <c>Land.buildProb</c>, <c>Land.as:797-800</c>).
        ///
        /// <para><b>Two attributes, and the empty one is load-bearing by being empty.</b> AS3 writes
        /// <c>&lt;obj prob='' uid='begin'/&gt;</c>. The <c>prob</c> attribute is present but empty, and
        /// <c>Interact</c> only assigns its <c>prob</c> field when <c>@prob.length()</c> is non-zero
        /// (<c>Interact.as:386-389</c>) — so an empty <c>prob</c> does <b>not</b> mean "enter the prob
        /// room named empty". It means the entry branch is skipped and the object's own
        /// <c>allact='probreturn'</c> (<c>AllData.as:5017</c>) runs instead, which returns the player to
        /// the land they came from. Writing the empty attribute is what documents that.</para>
        ///
        /// <para><c>uid='begin'</c> is what <c>Probation.doorsOnOff</c> keys on to keep the return door
        /// visible while every other <c>doorout</c> in the room is sealed (<c>Probation.as:299-308</c>).</para>
        /// </summary>
        /// <returns>The placed object, or <c>null</c> when the room has no spawn point.</returns>
        public static ObjectInstance PlaceReturnDoor(RoomInstance room)
        {
            if (room == null) return null;

            // AS3 guards the whole call on `if(loc.spawnPoints.length)`.
            if (room.spawnPoints == null || room.spawnPoints.Count == 0) return null;

            var spawn = new ObjectSpawnData
            {
                id = ProbReturnObjectId,
                type = "box",
                definitionId = ProbReturnObjectId,
                uid = ProbReturnBeginUid,
                attributes = SyntheticPlacementAttributes(new List<MapObjectAttributeData>
                {
                    new MapObjectAttributeData { key = "prob", value = string.Empty },
                }),
            };

            // AS3 buildProb passes `loc.spawnPoints[0]` — the FIRST spawn point, not a random one
            // (Land.as:797-800) — and then goes through createObj, so it gets the box anchor.
            Vector2 pos = ChooseObjectPixel(room, spawn);
            CreateObject(room, spawn, pos.x, pos.y);
            return room.objects.Count > 0 ? room.objects[room.objects.Count - 1] : null;
        }

        /// <summary>
        /// The pixel anchor a synthetic placement lands on: the room's first spawn point, resolved exactly
        /// the way an imported object's position is.
        ///
        /// <para><b>Why it is not simply the spawn point's tile.</b> AS3 does not place an object at its
        /// tile's corner. <c>createObj</c> anchors a box at <i>bottom-centre</i> —
        /// <c>((nx + 0.5 * size) * Tile.tileX, (ny + 1) * Tile.tileY - 1)</c>
        /// (<c>Location.as:2005</c>) — and a checkpoint at the very same formula (<c>:2040</c>). This used
        /// to return <c>TileToPixel(spawnPoint.tileCoord)</c>: the tile's <i>top-left</i> corner, in Flash's
        /// top-down pixels. So a synthetic object was drawn half a tile to the left and mirrored vertically
        /// about the room — a checkpoint on AS3 row 15 of a 25-row room appeared six tiles too high.
        /// Imported objects have always gone through <see cref="ResolveLegacyBottomAnchorPixels"/>; this
        /// makes the synthetic ones agree with them.</para>
        ///
        /// <para><b>The footprint attributes are part of the anchor.</b>
        /// <see cref="ResolveLegacyBottomAnchorPixels"/> offsets X by half the object's width and reads
        /// that width from the definition, which a synthetic placement does not have — so
        /// <paramref name="spawnData"/> must already carry
        /// <see cref="SyntheticPlacementAttributes"/> or the anchor lands half a tile short of the
        /// oracle's.</para>
        ///
        /// <para><b>Known divergence.</b> AS3 picks a <i>random</i> spawn point (<c>Location.as:2094</c>,
        /// <c>:2118</c>, <c>:2129</c>); this takes the first, which keeps the placements from disagreeing
        /// with each other. <c>buildProb</c>'s <c>doorout</c> does use the first
        /// (<c>Land.as:797-800</c>), so this matches the oracle there. Recorded rather than silently
        /// guessed.</para>
        /// </summary>
        private static Vector2 ChooseObjectPixel(RoomInstance room, ObjectSpawnData spawnData)
        {
            if (room.spawnPoints != null && room.spawnPoints.Count > 0)
            {
                spawnData.tileCoord = room.spawnPoints[0].tileCoord;
                return ResolveLegacyBottomAnchorPixels(room, spawnData);
            }

            return RoomSetup.FindPlayerSpawnPixels(room);
        }

        /// <summary>
        /// The attributes every synthetic placement carries, taken from the object's AS3 definition row.
        ///
        /// <para><b>Why an attribute rather than a definition.</b> AS3 reads both from
        /// <c>AllData.d.obj.(@id == id)</c> — the definition. <c>ResolvePlacementSizeTiles</c>,
        /// <c>RoomInstance.GetApproximatePixelSize</c> and <c>DoorPropPresenter.GetCoveredTileRange</c>
        /// each take the definition first and these attributes second, and none of the synthetic placements
        /// here has a definition to offer: <c>MapObjectCatalog</c> is a <c>ScriptableObject</c> and these
        /// helpers are static. Writing the attributes is the port's own stand-in, not a new mechanism.</para>
        ///
        /// <para><b>Both keys, never just one.</b> Each of those readers returns as soon as <i>either</i>
        /// attribute parses, so writing <c>size</c> alone would leave the height at its 1-tile default and
        /// describe a 2x1 object where AS3 has 2x3.</para>
        ///
        /// <para><b>The values.</b> <c>checkpoint</c> is <c>size='2' wid='3'</c>
        /// (<c>AllData.as:5007</c>), and <c>exit</c>, <c>doorout</c>, <c>doorprob</c> and <c>doorboss</c>
        /// are all identical (<c>:5016-5019</c>) — which is why one pair serves every placement here.</para>
        /// </summary>
        private static List<MapObjectAttributeData> SyntheticPlacementAttributes(
            List<MapObjectAttributeData> extra = null)
        {
            var attributes = new List<MapObjectAttributeData>
            {
                new MapObjectAttributeData { key = "size", value = "2" },
                new MapObjectAttributeData { key = "wid", value = "3" },
            };

            if (extra != null && extra.Count > 0)
            {
                attributes.AddRange(extra);
            }

            return attributes;
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
