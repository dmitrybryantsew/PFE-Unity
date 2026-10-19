using System;
using UnityEngine;
using System.Collections.Generic;
using System.Globalization;
using PFE.Core.Ids;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
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
        /// <param name="objectDefinitions">
        /// Where map-object definition rows come from. Defaults to the <c>Resources/MapObjects/Definitions</c>
        /// lookup. Used for exactly one thing on this path: the random-enemy pass has no authored
        /// definition row for the family <c>Location.randomUnit()</c> picks, so the row's <c>@cid</c>
        /// alias seed has to be looked up by family id — see <see cref="CreateUnit"/>'s
        /// <c>definitionCid</c>.
        /// </param>
        public static void PopulateRoom(
            RoomInstance room,
            RoomTemplate template,
            RoomDifficulty difficulty,
            PFE.Core.Rng.IRngService rng = null,
            IUnitDefinitionProvider unitDefinitions = null,
            IObjectDefinitionProvider objectDefinitions = null)
        {
            if (room == null || template == null) return;

            var r = GetSpawnRng(rng);
            var spawnCounters = new Dictionary<string, int>();

            // Phase 0: split the authored object table exactly the way AS3 Location.buildLoc() does.
            //
            // This step is the whole reason random rooms were empty. Location.as:656-701 does NOT put
            // every `<obj>` child into one list: it looks the row up in AllData and branches on the
            // row's `tip`, sending `spawnpoint` rows to `spawnPoints`, `enspawn` rows to `addEnSpawn`,
            // `up` rows into `ups[tipn]`, and only the remainder into `objsT` (which `setObjects` then
            // turns into units and props).
            //
            // The port skipped the branch and imported every one of those rows as a plain object, so the
            // 2 722 `up` markers and 625 `player` markers in the shipped rooms were being instantiated
            // as generic props AND the enemy points they represent were never collected — `ups[]` was
            // empty, so `setRandomUnits` had nothing to place on even once the counts existed.
            ObjectTablePartition table = PartitionObjectTable(template);

            // Record the authored capacity BEFORE Phase 2 spends it. `setRandomUnits` splices points out
            // of `ups[i]` as it places them, so after the pass the array is empty and the authored figure
            // is unrecoverable — see RoomInstance.upPointCounts. Captured here, the readout can tell "the
            // generator asked for 17 and the room authored 0 points" (by design) apart from "the
            // generator asked for 17 and the room authored 22 points but placed none" (a real fault),
            // which is the question the F2 Room tab exists to answer.
            RecordUpPointCounts(room, table.UpGroups);

            // Phase 1: Process template objects (from XML obj elements) sorted deterministically
            var sortedObjects = new List<ObjectSpawnData>(table.Spawnable);
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

            // Phase 2: Place random enemies at the `up` points, per group. Mirrors setRandomUnits().
            PlaceRandomEnemies(room, table, difficulty, ResolveEnemyContext(room), r, spawnCounters,
                unitDefinitions, objectDefinitions ?? ResourcesObjectDefinitionProvider.Shared);

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
        /// AS3 <c>Location.as:1017</c>, in full: <b>would the oracle skip this authored placement?</b>
        ///
        /// <code>
        /// if(!(this.noHolesPlace &amp;&amp; _loc1_.rem &gt; 0 &amp;&amp; !this.space[_loc1_.x][_loc1_.y].place))
        /// </code>
        ///
        /// <para><b>All three terms are required, and each one is a divergence the port used to have.</b>
        /// <paramref name="noHolesPlace"/> is the room-level gate (<c>Land.newLoc</c> passes
        /// <c>this.rnd</c>); <paramref name="rem"/> is the definition's own flag, and only 24 of the 202
        /// definitions carry it — so an authored <i>unit</i> or <i>trap</i> is placed even when it stands
        /// in a doorway; and the tile flag is the door-slot marking
        /// (<see cref="DoorCarver.MarkNoObj"/>). Testing the tile flag alone deleted 1515 authored
        /// placements across 335 of the 550 shipped rooms.</para>
        ///
        /// <para><b>A missing tile means "place".</b> AS3 indexes <c>space[x][y]</c> unguarded and would
        /// fault on an out-of-room coordinate, so there is no oracle behaviour to copy; the port's
        /// existing reading — a placement parked outside the grid is not a doorway collision — is kept,
        /// because inventing "skip" here would silently drop a placement on a data error.</para>
        /// </summary>
        /// <param name="noHolesPlace">The room's <c>noHolesPlace</c>.</param>
        /// <param name="rem">The <b>definition</b> row's <c>rem</c> (not the placement's) — see
        /// <see cref="PFE.Data.Definitions.MapObjectDefinition.GetRem"/>.</param>
        /// <param name="tile">The tile the placement resolves to, or null when it is off the grid.</param>
        public static bool SkipsAuthoredPlacement(bool noHolesPlace, int rem, TileData tile)
        {
            return noHolesPlace && rem > 0 && tile != null && !tile.canPlaceObjects;
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

            // AS3 Location.as:1017 — the doorway test, which is a THREE-term condition:
            //
            //   if(!(this.noHolesPlace && _loc1_.rem > 0 && !this.space[_loc1_.x][_loc1_.y].place))
            //
            // The port tested `!canPlaceObjects` alone, which is neither of the first two terms. Both
            // omissions were load-bearing and they compounded with the marking bug on the other side of
            // this seam (see DoorCarver.MarkNoObj): a unit or a trap carries no `rem`, so AS3 places it
            // whatever the tile says, while the port dropped it.
            TileData tile = room.GetTileAtCoord(new Vector2Int(nx, ny));
            if (SkipsAuthoredPlacement(
                    room.noHolesPlace,
                    spawnData.definition != null ? spawnData.definition.GetRem() : 0,
                    tile))
            {
                return;
            }

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
                            : null,
                        // AS3's createUnit() runs randomUnit() on EVERY requested id, authored or not
                        // (Location.as:1160), so the context is handed over here too. For an ordinary
                        // authored id it returns "" without drawing, which is why this cannot change the
                        // existing placements.
                        //
                        // `as3PlacementTile` is deliberately left null: the authored path keeps its own
                        // anchor (ResolveLegacyBottomAnchorPixels, which uses the DEFINITION's `size`).
                        // AS3 would instead use the created unit's sprite width — see the findings doc
                        // for that half-tile divergence; it is pre-existing and out of scope here.
                        enemyContext: ResolveEnemyContext(room),
                        water: ResolveAs3Water(room, ResolveEnemyContext(room), placementTileCoord),
                        // The two `uidObjs` keys. They live on the placement record, not in the attribute
                        // list — see UnitInstance.uid.
                        uid: spawnData.uid,
                        code: spawnData.code,
                        // The unit's own <item> children — the boss's `key_sewer` lives here. Without this
                        // parameter the whole authored chain (parser -> converter -> RoomObjectData ->
                        // this spawn descriptor) terminated at a method with no field to receive it.
                        items: spawnData.items);
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
            string definitionCid = null,
            LocationEnemyContext? enemyContext = null,
            bool water = false,
            Vector2Int? as3PlacementTile = null,
            IObjectDefinitionProvider objectDefinitions = null,
            string uid = null,
            string code = null,
            List<MapObjectItemData> items = null)
        {
            // ── Step 1 of the oracle's createUnit: which FAMILY is this really?
            //
            // Location.as:1160 `var _loc13_:String = this.randomUnit(param1, _loc12_);`. `param1` is the
            // requested id, which for the random-enemy pass is one of the five *point groups*
            // (enl1/enl2/enf1/enc1/lov) and for an authored placement is a unit id. randomUnit has a
            // case for the five groups and returns "" for everything else — with NO draw, which is why
            // calling it on the authored path is free.
            //
            // This step was missing entirely, and it is why the group ids could never work: without it
            // `enl2` went straight to randomCid(), which has no case for it, so the port would have
            // spawned a unit literally called `enl2`.
            LocationEnemyContext context = enemyContext ?? LocationEnemyContext.NoContext;
            string family = LocationEnemySelector.RandomUnit(unitId, context, water, rng);

            // Location.as:1161-1188. Two arms, and the second is a genuine fallback rather than an
            // error path: it fires when randomUnit declines (`""`), and it creates the REQUESTED id.
            // The oracle's guard is `if(_loc13_ == "" && !this.homeStable || _loc9_ == null)`; the port
            // has no `homeStable` (the `home` option) and `_loc9_` can never be null here, so the guard
            // reduces to `family == ""`.
            //
            // The oracle does NOT spawn the id the room authored — it spawns a VARIANT of it.
            // Location.createUnit() (Location.as:1169/1185) calls randomCid() and hands the result to
            // Unit.create(), whose subclass joins the two (`UnitZombie.as:104 id = "zombie" + tr`).
            // Passing the authored id through unchanged is what made a placed `zombie` resolve to
            // Resources/Units/zombie.asset — the family template, which carries no <vis> art, no
            // <comb hp> and no <move speed> at all. See UnitVariantResolver for the full argument.
            //
            // `tr` wins over the rolled cid, matching UnitZombie.as:83-101's precedence (saved object,
            // then placement node, then cid). The placement node is the only layer the port has.
            // `NoContext` is no longer passed here: `context` carries biome/tipEnemy/landConfig/landY,
            // and `default(UnitVariantContext)` would have `LandY == 0` — a real land index — which made
            // `randomCid("ranger")` pick its `landY == 0` arm instead of rolling.
            //
            // The definition's `@cid` is the cid *seed* (Unit.as:853-862): a room places `trplate`, the
            // definition row says `cid='trigplate'`, and the spawned id is `trigplate`. It is read off
            // the DEFINITION row, not the placement — 0 of the shipped placements carry `@cid`, while 24
            // definitions do. Without it the alias families (turret/trplate/expl1/…) compose from a null
            // seed and resolve to nothing.
            string requestedId = string.IsNullOrEmpty(family) ? unitId : family;
            string placementTr = MapObjectDataUtility.GetAttribute(attributes, "tr", null);

            // The definition row's `@cid` is read off the row for the id being created — which, on this
            // path, is the FAMILY randomUnit() just chose, not the group marker the room authored. The
            // authored path already supplies it (ProcessObjectSpawn reads `spawnData.definition`); here
            // there is no authored row, so resolve the family's row by id.
            //
            // Without this the alias families composed from a null seed: `trridge`/`trplate`/`trcans`/
            // `trlaser` fell back to an id with no `Resources/Units/*.asset` at all (the rows say
            // cid='trigridge'/'trigplate'/'trigcans'/'triglaser', and those assets DO exist), and
            // `landturret` resolved to the floor turret `turret0` instead of the land turret `turret1` —
            // a wrong variant that draws, so it reads as "working". `trridge` and friends come from the
            // `lov` group, which 285 of the shipped rooms author, so this was a live hole, not a corner.
            //
            // The lookup draws nothing, so it cannot disturb the shared spawn stream; it is placed here
            // only because it must precede ResolveSpawnId, which consumes the seed.
            string effectiveCid = definitionCid;
            if (string.IsNullOrEmpty(effectiveCid) &&
                objectDefinitions != null &&
                objectDefinitions.TryGetObject(requestedId, out MapObjectDefinition familyDefinition))
            {
                effectiveCid = familyDefinition.GetAttribute("cid", null);
            }

            string resolvedId = UnitVariantResolver.ResolveSpawnId(
                requestedId,
                difficulty,
                rng,
                context.ToVariantContext(),
                placementTr,
                effectiveCid);

            float health = ResolveUnitHealth(resolvedId, unitDefinitions);

            // Location.as:1211-1212 — the oracle creates the unit first and then reads its sprite width
            // to centre it: `_loc15_ = floor((_loc9_.scX - 1) / 40) + 1` and
            // `putLoc((param2 + 0.5*_loc15_)*Tile.tileX, (param3 + 1)*Tile.tileY - 1)`.
            //
            // This runs AFTER ResolveFacing/ResolveDiggerTier below in draw order terms? No — it draws
            // nothing, so its position in the sequence is free; it is placed here only because it needs
            // `resolvedId`. The two draws above and below it keep the oracle's order.
            if (as3PlacementTile.HasValue)
            {
                Vector2 anchored = As3UnitAnchorPixels(
                    room, as3PlacementTile.Value, ResolveAs3AnchorWidthTiles(resolvedId, unitDefinitions));
                x = anchored.x;
                y = anchored.y;
            }

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
                // The oracle's `uidObjs` entry for a unit. AS3 registers the unit under BOTH the authored
                // uid and its generated code (`Location.createUnit` -> `Unit.create` stores both on the
                // unit, and `uidObjs` is keyed by whichever the script names), so both travel. A blank
                // uid is the common case — only ~1 placement in 20 authors one — and an empty string is
                // the honest value for "the room named nothing".
                uid = uid ?? string.Empty,
                code = code ?? string.Empty,
                position = new Vector2(x, y),
                isDead = false,
                maxHealth = health,
                currentHealth = health,
                controllerId = controllerId ?? string.Empty,
                attributes = MapObjectDataUtility.CloneAttributes(attributes),
                // The unit's own <item> children — AS3 Unit.create builds `inter` from the placed node and
                // Unit.dropLoot opens with `if(inter) inter.loot()` (Unit.as:4450-4455). Cloned for the same
                // reason `attributes` is: the caller's spawn descriptor outlives this unit.
                items = MapObjectDataUtility.CloneItems(items),
                fraction = ResolvePlacementFaction(attributes),
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
        /// The faction this placement authored — AS3 <c>UnitTurret.as:140-142</c>
        /// <c>if(param3 &amp;&amp; param3.@fraction.length()) fraction = param3.@fraction;</c>.
        ///
        /// <para><b>Returns null when the row is silent, and that is the point.</b> The three
        /// consumers of a faction are the damage rules, the AI target filter and the turret's own
        /// <c>warn = 0</c> gate, and each of them distinguishes "the placement overrode this" from
        /// "the unit definition decides". Collapsing the silent case to <c>Neutral</c> would make
        /// <c>fraction='0'</c> — which <c>Unit.as:454</c> does define — indistinguishable from an
        /// absent attribute, and the camp turrets sit exactly on that boundary: their placement says
        /// <c>fraction="100"</c> while their unit row (<c>AllData.as:1758</c>) says nothing at all.</para>
        ///
        /// <para><b>Non-numeric is treated as absent, not as zero.</b> AS3's <c>@fraction.length()</c>
        /// only tests that the attribute is present, and then assigns a String into an <c>int</c>
        /// field — so a garbage value coerces to <c>NaN</c>, and <c>fraction == F_PLAYER</c> is then
        /// false for every comparison. The port has no <c>NaN</c> in an enum, and inventing
        /// <c>Neutral</c> for it would hand a garbage value the meaning of a deliberate one, so it
        /// falls back to the definition instead. The oracle has no such placement in its data.</para>
        /// </summary>
        public static FactionType? ResolvePlacementFaction(List<MapObjectAttributeData> attributes)
        {
            string raw = MapObjectDataUtility.GetAttribute(attributes, "fraction", null);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? (FactionType)value
                : null;
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

        /// <summary>The placement attribute the port writes for AS3's <c>CheckPoint.teleOn</c>.</summary>
        public const string TeleCheckpointAttribute = "tele";

        /// <summary>
        /// AS3 <c>Location.createObj</c>'s checkpoint branch, <b>second statement</b>
        /// (<c>Location.as:2040-2046</c>) — the difficulty override that turns a checkpoint's return
        /// teleport on:
        /// <code>
        /// if(World.w.game.globalDif &lt;= 1 || this.land.rnd &amp;&amp; World.w.game.globalDif == 2
        ///    &amp;&amp; Math.random() &lt; 0.33) { (obj as CheckPoint).teleOn = true; }
        /// </code>
        /// <para><b>Why this is its own method and not part of <see cref="PlaceCheckpointMarker"/>.</b>
        /// The oracle applies it <i>after</i> the <c>CheckPoint</c> constructor, as a separate statement —
        /// it is an override of the object, not one of its constructor arguments. Keeping it separate also
        /// leaves <see cref="PlaceCheckpointMarker"/>'s existing contract untouched, so the id roll's own
        /// fixtures keep describing the id roll.</para>
        ///
        /// <para><b>The random draw and which stream it comes from.</b> The oracle spends a global
        /// <c>Math.random()</c> here — a stream separate from the one <c>setLocDif</c> draws. The port
        /// draws from an <b>ephemeral salted sub-stream</b>
        /// (<c>IRngService.GetStream(stream, salt)</c>), which seeds a fresh generator from the parent's
        /// state and the salt <i>without advancing the parent</i>. That is the choice the plan asks to be
        /// made explicit: drawing from the shared spawn stream instead would consume a value and shift
        /// every later placement in the land build, which the oracle's separate global stream does not do.</para>
        ///
        /// <para><b>Honest limit, recorded not hidden.</b> <c>teleOn</c> only becomes observable through
        /// <c>CheckpointRules.Resolve</c>'s <c>offersTeleport = mReturn &amp;&amp; teleOn &amp;&amp; !used</c>.
        /// <c>mReturn</c> is now derived from the <c>noreturn</c> trigger the way AS3 derives it
        /// (<c>Game.as:390-397</c>, ported in <c>CampaignManager.ActivateCheckpoint</c>) — it was left
        /// permanently false before, which inverted the oracle's <c>true</c> initialiser
        /// (<c>Game.as:49</c>) and made the offer impossible. What is still missing is the <i>surface</i>:
        /// no checkpoint object is an <c>IInteractable</c>, so the offered return has nowhere to be
        /// pressed — the same deferred shape as the locked-variant roll above.</para>
        /// </summary>
        /// <param name="checkpoint">The placement to write onto. <c>null</c> is tolerated.</param>
        /// <param name="landRnd">AS3 <c>this.land.rnd</c> — only a procedural land rolls.</param>
        /// <param name="rng">The land build stream. <c>null</c> disables the 33 % arm only.</param>
        /// <returns>The oracle's <c>teleOn</c>.</returns>
        public static bool ApplyCheckpointTeleOn(
            ObjectInstance checkpoint,
            bool landRnd,
            PFE.Core.Rng.IRngService rng = null)
        {
            if (checkpoint == null) return false;

            int globalDif = GlobalDifficulty.Value;

            // The draw sits at the END of an `&&` chain, so it is reached only when the difficulty is
            // Normal and the land is procedural. Testing that BEFORE drawing is what keeps an authored
            // land, or any difficulty other than 2, from spending a value the oracle does not.
            //
            // `1f` is the "no draw happened" sentinel: `1f < 0.33f` is false, so a caller with no stream
            // gets the deterministic answer rather than a coin flip that was never tossed.
            float roll = 1f;
            if (rng != null && DifficultyRules.CheckpointRollsForTeleOn(globalDif, landRnd))
            {
                roll = rng.GetStream(PFE.Core.Rng.RngStream.Spawn, salt: TeleOnSalt(checkpoint)).NextFloat();
            }

            bool teleOn = DifficultyRules.CheckpointTeleOn(globalDif, landRnd, roll);
            if (teleOn)
            {
                checkpoint.attributes ??= new List<MapObjectAttributeData>();
                checkpoint.attributes.Add(new MapObjectAttributeData
                {
                    key = TeleCheckpointAttribute,
                    value = "1",
                });
            }

            return teleOn;
        }

        /// <summary>
        /// The salt for <see cref="ApplyCheckpointTeleOn"/>'s sub-stream: the checkpoint's own pixel
        /// position, so two checkpoints in one land do not share a draw and the same land re-built gets the
        /// same answer. AS3 has no equivalent because its global stream is one sequence for the whole run.
        /// </summary>
        private static int TeleOnSalt(ObjectInstance checkpoint)
        {
            int x = (int)checkpoint.position.x;
            int y = (int)checkpoint.position.y;
            return unchecked(x * 397 ^ y * 31);
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

            // Add as spawn point for player.
            //
            // `SpawnPoint.tileCoord` is an AS3 row everywhere on the field — that is the importers'
            // convention and what SpawnPoint.GetRoomLocalPixel mirrors. This used to store
            // `PixelToTile` of the anchor just computed, i.e. a PORT row, putting a second convention
            // on the same field: a landmine that only stayed quiet because this point is appended
            // after the room's imported one and is therefore never the first SpawnType.Player entry.
            //
            // `spawnData.tileCoord` is the checkpoint's own AS3 tile — the very value the anchor above
            // was derived from, in both call paths (ProcessObjectSpawn hands over the imported object's
            // data; PlaceCheckpointMarker's ChooseObjectPixel has already overwritten it with the
            // room's first spawn point). So this is the exact inverse of the anchor, not an approximation.
            Vector2Int markerTile = spawnData != null
                ? spawnData.tileCoord
                : As3TileOfAnchorPixel(room, new Vector2(x, y));

            room.spawnPoints.Add(new SpawnPoint
            {
                tileCoord = markerTile,
                type = SpawnType.Player
            });
        }

        /// <summary>
        /// The AS3 tile a port-space anchor pixel was placed from — the inverse of
        /// <see cref="WorldCoordinates.As3BottomCentreAnchorPixels"/> for the anchor size a
        /// checkpoint carries.
        /// </summary>
        /// <remarks>
        /// Only reachable when <c>CreateCheckpoint</c> is handed no spawn data, which neither call site
        /// does. It exists so that path still writes the field's one convention rather than a port row.
        /// </remarks>
        private static Vector2Int As3TileOfAnchorPixel(RoomInstance room, Vector2 anchorPixel)
        {
            Vector2Int portTile = WorldCoordinates.PixelToTile(anchorPixel);
            int roomHeight = room != null ? room.height : WorldConstants.ROOM_HEIGHT;

            return new Vector2Int(
                portTile.x - (int)SpawnPoint.AnchorSizeTiles + 1,
                WorldCoordinates.UnityRowToAs3Row(portTile.y, roomHeight));
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
        /// Place the room's random enemies: one draw per enemy point, per group.
        /// Port of AS3 <c>Location.setRandomUnits()</c> (<c>Location.as:1053-1109</c>).
        ///
        /// <para><b>What this replaced, and why the old shape could not work.</b> The previous version
        /// looked for <see cref="SpawnType.Enemy"/> entries on <c>room.spawnPoints</c>. Nothing has ever
        /// produced one: <c>AS3ToUnityConverter.FindSpawnPoints</c> emits exactly one point, the
        /// <see cref="SpawnType.Player"/> one, and no importer writes <c>SpawnType.Enemy</c> anywhere.
        /// So the method returned at its first guard in every room, always. The count it would have used
        /// was zero as well, because <c>RoomDifficulty.SetEnemyCount</c> had no production caller.</para>
        ///
        /// <para><b>The oracle's shape is per group, not per total.</b> <c>kolEn[i]</c> is a count for
        /// <i>group i</i>, and the points for group <c>i</c> live in <c>ups[i]</c>; there is no
        /// "total enemies" quantity anywhere in <c>setRandomUnits</c>. Each draw picks an index into the
        /// surviving points of that group, creates one unit, and then retires that point.</para>
        ///
        /// <para><b>The retirement rule is unusual and is transcribed as written.</b>
        /// <c>if(ups[i].length &lt;= 1) { ups[i] = []; break; } ups[i].splice(_loc3_,1);</c>
        /// (<c>Location.as:1082-1087</c>) — the length test happens <i>after</i> the create and clears
        /// the whole list rather than removing the used point. So the last point is consumed without
        /// being spliced, and a group whose <c>kolEn[i]</c> exceeds its point count simply stops early.
        /// "Fixing" this to a plain remove would let one extra enemy per group spawn.</para>
        /// </summary>
        private static void PlaceRandomEnemies(
            RoomInstance room,
            ObjectTablePartition table,
            RoomDifficulty difficulty,
            in LocationEnemyContext context,
            PFE.Core.Rng.IRngService rng,
            Dictionary<string, int> spawnCounters = null,
            IUnitDefinitionProvider unitDefinitions = null,
            IObjectDefinitionProvider objectDefinitions = null)
        {
            if (room == null || difficulty == null || table.UpGroups == null || rng == null) return;

            // Location.as:1058-1093. Index 0 is skipped: `tipEn[0]` is the empty string and `ups[0]`
            // is where AS3 parks markers whose `tipn` it could not parse, and `setRandomUnits` never
            // reads it.
            for (int group = 1; group < LocationEnemySelector.GroupCount; group++)
            {
                int wanted = difficulty.GetEnemyCount(group);
                if (wanted <= 0) continue;

                List<ObjectSpawnData> points = table.UpGroups[group];
                if (points == null || points.Count == 0) continue;

                // Location.as:1062-1074 — `if(this.noHolesPlace)` drops the points that landed on a
                // tile that cannot take an object. This mutates `ups[i]` itself, so a dropped point also
                // stops counting toward the `length <= 1` test below.
                if (room.noHolesPlace)
                {
                    for (int i = points.Count - 1; i >= 0; i--)
                    {
                        TileData tile = room.GetTileAtCoord(points[i].tileCoord);
                        if (tile != null && !tile.canPlaceObjects)
                        {
                            points.RemoveAt(i);
                        }
                    }
                }

                if (points.Count == 0) continue;

                for (int placed = 0; placed < wanted; placed++)
                {
                    int pick = rng.Range(0, points.Count);
                    ObjectSpawnData point = points[pick];

                    CreateGroupUnit(room, LocationEnemySelector.GroupIds[group], point,
                        context, difficulty, rng, spawnCounters, unitDefinitions, objectDefinitions);

                    if (points.Count <= 1)
                    {
                        points.Clear();
                        break;
                    }

                    points.RemoveAt(pick);
                }
            }

            // Location.as:1094-1108 — hidden enemies are drawn from the same `ups[2]` pool, after the
            // main pass, and share its retirement rule. `ups[2]` may already be empty by now.
            if (difficulty.hiddenEnemyCount > 0)
            {
                List<ObjectSpawnData> hiddenPoints = table.UpGroups[2];
                if (hiddenPoints == null || hiddenPoints.Count == 0) return;

                for (int placed = 0; placed < difficulty.hiddenEnemyCount; placed++)
                {
                    int pick = rng.Range(0, hiddenPoints.Count);
                    ObjectSpawnData point = hiddenPoints[pick];

                    CreateHidden(room, point, context, difficulty, rng, spawnCounters, unitDefinitions,
                        objectDefinitions);

                    if (hiddenPoints.Count <= 1)
                    {
                        break;
                    }

                    hiddenPoints.RemoveAt(pick);
                }
            }
        }

        /// <summary>
        /// Create one unit at an authored enemy point. The oracle's <c>setRandomUnits</c> hands
        /// <c>createUnit</c> the point's tile coordinates and its own <c>&lt;obj&gt;</c> node
        /// (<c>Location.as:1081</c> <c>this.ups[_loc1_][_loc3_].xml</c>), which is what makes the
        /// placement's <c>turn</c>/<c>tr</c>/<c>dig</c> attributes reach the unit's constructor.
        /// </summary>
        private static void CreateGroupUnit(
            RoomInstance room,
            string groupId,
            ObjectSpawnData point,
            in LocationEnemyContext context,
            RoomDifficulty difficulty,
            PFE.Core.Rng.IRngService rng,
            Dictionary<string, int> spawnCounters,
            IUnitDefinitionProvider unitDefinitions,
            IObjectDefinitionProvider objectDefinitions)
        {
            string unitId = groupId;

            EntityId entityId = default;
            if (spawnCounters != null)
            {
                if (!spawnCounters.TryGetValue(unitId, out int count)) count = 0;
                spawnCounters[unitId] = count + 1;
                entityId = EntityId.CreateForRoomSpawn(room.id, unitId, count);
            }

            // No authored `cl` on this path — the group markers carry none — so the base controller,
            // and RoomUnitSpawner resolves the real one from the spawned unit's definition.
            //
            // `turn` is passed RAW (null when the marker has none), because the facing coin flip and the
            // variant roll draw from the same stream in that order (Location.as:1185 randomCid, then
            // Unit.as:609-613 facing).
            CreateUnit(
                room,
                unitId: unitId,
                x: 0f,
                y: 0f,
                entityId: entityId,
                unitDefinitions: unitDefinitions,
                attributes: point.attributes,
                turn: point.GetAttribute("turn", null),
                difficulty: ResolveLocationDifficulty(difficulty),
                rng: rng,
                enemyContext: context,
                water: ResolveAs3Water(room, context, point.tileCoord),
                as3PlacementTile: point.tileCoord,
                // AS3 has no authored definition row for a group marker either — `createUnit` is handed
                // the GROUP id and looks the row up by the *resolved* family. Handing the provider over
                // is what lets CreateUnit fill the alias seed (`trridge` -> `trigridge`,
                // `landturret` -> `land`). Note this is deliberately NOT `point.definition`: the marker's
                // own row is `enl2`, which carries no `cid`.
                objectDefinitions: objectDefinitions);
        }

        /// <summary>
        /// Port of AS3 <c>Location.createHidden(param1, param2)</c> (<c>Location.as:1333-1355</c>): the
        /// buried/silent occupants of a room, drawn from the <c>enl2</c> point pool after the visible
        /// enemies.
        ///
        /// <para><b>What it creates depends on the room's enemy tier, and two of the four arms create a
        /// prop rather than a unit.</b> <c>tipEnemy == 2</c> drops a <c>robocell</c> box and
        /// <c>tipEnemy == 1 || 3</c> an <c>alarm</c> box; only tiers 0 and 6 create units
        /// (<c>zombie</c> with an authored <c>dig='2'</c>, and the <c>lov</c> group respectively). Biomes
        /// 10 and 11 return without doing anything at all.</para>
        ///
        /// <para><b>The <c>dig='2'</c> is load-bearing.</b> It is the only authored <c>dig</c> in the
        /// game: the hidden zombies are always tier 2 (buried with a sharp ear), never rolled, and the
        /// attribute is supplied by the oracle's own inline node
        /// (<c>&lt;unit dig='2'/&gt;</c>) rather than by room data.</para>
        /// </summary>
        private static void CreateHidden(
            RoomInstance room,
            ObjectSpawnData point,
            in LocationEnemyContext context,
            RoomDifficulty difficulty,
            PFE.Core.Rng.IRngService rng,
            Dictionary<string, int> spawnCounters,
            IUnitDefinitionProvider unitDefinitions,
            IObjectDefinitionProvider objectDefinitions)
        {
            // Location.as:1335-1338
            if (context.Biome == 10 || context.Biome == 11) return;

            int tipEnemy = difficulty.tipEnemy;
            Vector2Int tile = point.tileCoord;

            if (tipEnemy == 0)
            {
                // The oracle's own node, not the marker's: `createUnit("zombie", x, y, false, <unit dig='2'/>)`.
                var dig = new List<MapObjectAttributeData>
                {
                    new MapObjectAttributeData { key = "dig", value = "2" }
                };

                string unitId = "zombie";
                EntityId entityId = default;
                if (spawnCounters != null)
                {
                    if (!spawnCounters.TryGetValue(unitId, out int count)) count = 0;
                    spawnCounters[unitId] = count + 1;
                    entityId = EntityId.CreateForRoomSpawn(room.id, unitId, count);
                }

                CreateUnit(
                    room,
                    unitId: unitId,
                    x: 0f,
                    y: 0f,
                    entityId: entityId,
                    unitDefinitions: unitDefinitions,
                    attributes: dig,
                    turn: null,
                    difficulty: ResolveLocationDifficulty(difficulty),
                    rng: rng,
                    enemyContext: context,
                    water: ResolveAs3Water(room, context, tile),
                    as3PlacementTile: tile,
                    objectDefinitions: objectDefinitions);
                return;
            }

            if (tipEnemy == 2 || tipEnemy == 1 || tipEnemy == 3)
            {
                // `createObj("robocell", "box", …)` / `createObj("alarm", "box", …)`. The oracle builds
                // these from a bare id with no placed node, so there is no ObjectSpawnData to reuse;
                // CreateHiddenObject builds the minimal record the same way PlaceXpBonuses does.
                string objectId = tipEnemy == 2 ? "robocell" : "alarm";
                CreateHiddenObject(room, objectId, tile);
                return;
            }

            if (tipEnemy == 6)
            {
                // `createUnit("lov", …)` — note this is a GROUP id, so it goes through randomUnit like
                // any other, and on this arm the marker's own attributes are NOT passed.
                string unitId = "lov";
                EntityId entityId = default;
                if (spawnCounters != null)
                {
                    if (!spawnCounters.TryGetValue(unitId, out int count)) count = 0;
                    spawnCounters[unitId] = count + 1;
                    entityId = EntityId.CreateForRoomSpawn(room.id, unitId, count);
                }

                CreateUnit(
                    room,
                    unitId: unitId,
                    x: 0f,
                    y: 0f,
                    entityId: entityId,
                    unitDefinitions: unitDefinitions,
                    turn: null,
                    difficulty: ResolveLocationDifficulty(difficulty),
                    rng: rng,
                    enemyContext: context,
                    water: ResolveAs3Water(room, context, tile),
                    as3PlacementTile: tile,
                    objectDefinitions: objectDefinitions);
            }
        }

        /// <summary>
        /// The <c>createObj</c> half of <c>createHidden</c>: a box built from an id and a tile, with no
        /// authored placement node behind it. Placed through the same pixel anchor a unit would use, so
        /// the cell it lands in matches AS3's <c>createObj(id, tip, x, y)</c>.
        /// </summary>
        private static void CreateHiddenObject(RoomInstance room, string objectId, Vector2Int tile)
        {
            Vector2 pixels = As3UnitAnchorPixels(room, tile, 1f);

            var obj = new ObjectInstance
            {
                entityId = string.Empty,
                objectId = objectId,
                objectType = "box",
                definitionId = objectId,
                definition = null,
                code = string.Empty,
                uid = string.Empty,
                attributes = new List<MapObjectAttributeData>(),
                items = new List<MapObjectItemData>(),
                scripts = new List<MapObjectScriptData>(),
                parameters = string.Empty,
                position = pixels,
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };

            obj.InitializeDynamicRuntimeState();
            room.AddObject(obj);
        }

        /// <summary>
        /// The room's AS3 <c>Location</c> context, assembled from the fields <c>RoomGenerator</c> wrote
        /// at build time. <c>landX</c>/<c>landY</c> are the room's grid coordinates, which is what AS3
        /// stores in <c>newLoc</c> (<c>Land.as:950-951</c>) and what <c>randomUnit</c>'s parity tests
        /// read.
        /// </summary>
        private static LocationEnemyContext ResolveEnemyContext(RoomInstance room)
        {
            if (room == null) return LocationEnemyContext.NoContext;

            RoomDifficulty difficulty = room.difficulty;

            return new LocationEnemyContext(
                biome: room.biome,
                tipEnemy: difficulty != null ? difficulty.tipEnemy : -1,
                // AS3 randomCid/randomUnit read `locDifLevel`, and `setLocDif` writes it to BOTH
                // `baseDifficulty` and `enemyLevel` (Land.as:983/987). `baseDifficulty` is the one that
                // stays equal to `locDifLevel` once the unported `globalDif` term lands, so it is the
                // right source.
                difficulty: difficulty != null ? difficulty.baseDifficulty : 0f,
                landX: room.landPosition.x,
                landY: room.landPosition.y,
                landConfig: room.landConfig);
        }

        /// <summary>
        /// AS3 <c>createUnit</c>'s water test: <c>(biom == 1 || biom == 5) &amp;&amp; param4 == false &amp;&amp;
        /// getTile(x, y).water &gt; 0</c> (<c>Location.as:1156-1159</c>). <c>param4</c> is the
        /// "position is already pixels" flag and is <c>false</c> on both the authored and the
        /// random-enemy paths, so it drops out.
        ///
        /// <para>It matters: in biomes 1 (sewer) and 5 (canter) an <c>enl1</c>/<c>enc1</c>/<c>enf1</c>
        /// point standing in water resolves to <c>fish</c> instead of the dry-land family.</para>
        /// </summary>
        private static bool ResolveAs3Water(RoomInstance room, in LocationEnemyContext context, Vector2Int as3Tile)
        {
            if (room == null) return false;
            if (context.Biome != 1 && context.Biome != 5) return false;

            TileData tile = room.GetTileAtCoord(ResolveLegacyPlacementTileCoord(room, as3Tile));
            return tile != null && tile.hasWater;
        }

        /// <summary>
        /// AS3 <c>createUnit</c>'s pixel anchor for an authored tile:
        /// <c>((x + 0.5 * w) * Tile.tileX, (y + 1) * Tile.tileY - 1)</c>
        /// (<c>Location.as:1211-1212</c>), mirrored into the port's up-growing Y.
        ///
        /// <para><c>w</c> is <see cref="ResolveAs3AnchorWidthTiles"/>. The Y half is the same mirror
        /// <c>ResolveLegacyBottomAnchorPixels</c> applies, which is why the two agree on Y and differ
        /// only on X — the authored path uses the definition's <c>size</c> for X, AS3 uses the created
        /// unit's sprite width.</para>
        /// </summary>
        private static Vector2 As3UnitAnchorPixels(RoomInstance room, Vector2Int as3Tile, float unitWidthTiles)
        {
            int borderOffset = room != null ? Mathf.Max(0, room.borderOffset) : 0;
            int roomHeight = room != null ? room.height : WorldConstants.ROOM_HEIGHT;

            return new Vector2(
                (as3Tile.x + borderOffset + 0.5f * unitWidthTiles) * WorldConstants.TILE_SIZE,
                (roomHeight - borderOffset - as3Tile.y - 1) * WorldConstants.TILE_SIZE + 1f);
        }

        /// <summary>
        /// AS3 <c>_loc15_ = floor((_loc9_.scX - 1) / 40) + 1</c> (<c>Location.as:1211</c>) — the created
        /// unit's own width in tiles, which is what the oracle centres the sprite on.
        ///
        /// <para><b>Not the marker's <c>size</c>.</b> The five enemy markers are
        /// <c>enl1 size=1</c>, <c>enl2 size=2</c>, <c>enf1 size=1</c>, <c>enc1 size=1</c>,
        /// <c>lov size=1</c> (<c>AllData.as:4931-4935</c>), but AS3 never reads that here: it reads the
        /// sprite it just created. Using the marker's size would push every <c>enl2</c> enemy half a
        /// tile to the right of where the oracle puts it.</para>
        ///
        /// <para>Measured: every unit in <c>Resources/Units</c> is 0.2–1.0 tiles wide except four
        /// (<c>destr1</c> 1.6, <c>dront</c> 1.2, <c>sentinel</c> 1.1, <c>thunderhead</c> 60), and none of
        /// those four is reachable from <c>randomUnit</c> — so in practice this returns 1 for every
        /// random enemy, and the port's X anchor matches the old hard-coded
        /// <c>(tile.x + 0.5) * TILE_SIZE</c>. It is computed rather than assumed so a future wide enemy
        /// is placed correctly instead of silently half a tile off.</para>
        /// </summary>
        private static float ResolveAs3AnchorWidthTiles(string unitId, IUnitDefinitionProvider unitDefinitions)
        {
            IUnitDefinitionProvider provider = unitDefinitions ?? ResourcesUnitDefinitionProvider.Shared;

            if (provider.TryGetUnit(unitId, out UnitDefinition definition) && definition != null)
            {
                float widthPixels = definition.Width * WorldConstants.TILE_SIZE;
                return Mathf.Floor((widthPixels - 1f) / WorldConstants.TILE_SIZE) + 1f;
            }

            return 1f;
        }

        /// <summary>
        /// AS3 <c>Location.buildLoc()</c>'s dispatch of a room's <c>&lt;obj&gt;</c> children
        /// (<c>Location.as:656-701</c>).
        /// </summary>
        private struct ObjectTablePartition
        {
            /// <summary>The rows that fall through to <c>objsT</c> — what <c>setObjects</c> instantiates.</summary>
            public List<ObjectSpawnData> Spawnable;

            /// <summary>
            /// AS3 <c>ups[]</c>, indexed by <c>tipn</c>. Index 0 is never read by
            /// <c>setRandomUnits</c> (its loop starts at 1) — AS3 parks unparseable markers there, and
            /// so does this port, which is why an unrecognised marker id is dropped rather than placed.
            /// </summary>
            public List<ObjectSpawnData>[] UpGroups;

            /// <summary>How many <c>enspawn</c> rows the room authored. See the remarks on
            /// <see cref="PartitionObjectTable"/> — the consumer is unported.</summary>
            public int EnSpawnRows;

            /// <summary>How many <c>spawnpoint</c> rows were skipped. The player point is already on
            /// <c>room.spawnPoints</c> via <c>RoomGenerator</c>; AS3 never instantiates the marker.</summary>
            public int SpawnPointRows;
        }

        /// <summary>
        /// Split the room's authored object table the way AS3 <c>Location.buildLoc()</c> does
        /// (<c>Location.as:656-701</c>).
        ///
        /// <code>
        /// xmll = AllData.d.obj.(@id == obj.@id)[0];
        /// if(xmll.@tip == "spawnpoint") spawnPoints.push({x, y});
        /// else if(xmll.@tip == "enspawn") addEnSpawn(nx, ny, xmll);
        /// else if(xmll.@tip == "up")     ups[int(xmll.@tipn)].push({x, y, xml: obj});
        /// else                           objsT.push({id, tip, rem, x, y, xml: obj});
        /// </code>
        ///
        /// <para><b>The tip comes off the AllData row, not off the placed node</b>
        /// (<c>Location.as:658</c> looks the row up by id). The port's <c>MapObjectDefinition</c> is that
        /// row, and <c>legacyTip</c> is the <c>tip</c> attribute copied verbatim — see
        /// <c>MapObjectDefinition.GetResolvedPlacementType</c>, which reads the same field for the same
        /// reason.</para>
        ///
        /// <para><b><c>enspawn</c> rows are collected but not consumed.</b> The oracle sends them to
        /// <c>addEnSpawn</c>, which fills <c>Location.enspawn</c> and <c>kolEnSpawn</c>; that list is read
        /// by <c>Location.enemySpawn()</c> and <c>waveSpawn()</c> (<c>Location.as:2977-3030</c>), the
        /// alarm/wave spawner. Neither is ported, so there is no consumer. They are still removed from the
        /// spawnable set, because AS3 never instantiates them — leaving them in is what produced 89 junk
        /// props per import.</para>
        /// </summary>
        private static ObjectTablePartition PartitionObjectTable(RoomTemplate template)
        {
            var partition = new ObjectTablePartition
            {
                Spawnable = new List<ObjectSpawnData>(),
                UpGroups = new List<ObjectSpawnData>[LocationEnemySelector.GroupCount]
            };

            for (int i = 0; i < partition.UpGroups.Length; i++)
            {
                partition.UpGroups[i] = new List<ObjectSpawnData>();
            }

            if (template?.objects == null) return partition;

            foreach (ObjectSpawnData objData in template.objects)
            {
                if (objData == null) continue;
                objData.EnsureStructuredData();

                switch (DefinitionTip(objData))
                {
                    case "spawnpoint":
                        partition.SpawnPointRows++;
                        continue;

                    case "enspawn":
                        partition.EnSpawnRows++;
                        continue;

                    case "up":
                        int group = LocationEnemySelector.GroupIndexForId(objData.id);
                        if (group == 0)
                        {
                            Debug.LogWarning(
                                $"[RoomPopulator] Room '{template.id}': object '{objData.id}' has " +
                                $"definition tip='up' but is not one of the five enemy point groups " +
                                $"(enl1, enl2, enf1, enc1, lov), so its tipn is unknown. AS3 would push " +
                                $"it onto ups[0], which setRandomUnits never reads — dropped.");
                            continue;
                        }

                        partition.UpGroups[group].Add(objData);
                        continue;
                }

                partition.Spawnable.Add(objData);
            }

            return partition;
        }

        /// <summary>
        /// Copy the partition's per-group point counts onto the room, mirroring the fact that AS3's
        /// <c>ups</c> is a member of <c>Location</c> rather than a local of <c>buildLoc</c>
        /// (<c>Location.as:333-336</c> allocates it once, and <c>:684</c> pushes into it from the object
        /// loop).
        ///
        /// <para><b>It takes the groups array rather than the partition struct, and it is public, so the
        /// offline wall can call it.</b> Every other test of this seam builds a <c>ScriptableObject</c>
        /// template, which the wall cannot construct (<c>SecurityException: ECall</c>) — so the property
        /// that has to hold here would otherwise be unpinned outside the editor. The array is all this
        /// needs, and a hand-built one is enough to reproduce the drain below.</para>
        ///
        /// <para><b>The counts are a snapshot, taken before the pass spends the pool.</b>
        /// <c>setRandomUnits</c> splices points out of <c>ups[i]</c> as it places them and clears the list
        /// outright once one remains (<c>Location.as:1082-1087</c>), so afterwards the authored figure
        /// exists nowhere but <c>RoomInstance.upPointCounts</c>. That is why the call site sits
        /// immediately after <c>PartitionObjectTable</c> and <b>must stay above Phase 2</b>: moving it
        /// below would make every room in the game report that it authored no enemy points.</para>
        ///
        /// <para>The array is re-allocated when the room's own is the wrong shape, because
        /// <c>[NonSerialized]</c> fields arrive as <c>null</c> on a room that was deserialised, and a
        /// zero-length array would silently report every group as having no points — the exact reading
        /// this field exists to make trustworthy.</para>
        /// </summary>
        public static void RecordUpPointCounts(RoomInstance room, List<ObjectSpawnData>[] upGroups)
        {
            if (room == null || upGroups == null) return;

            if (room.upPointCounts == null || room.upPointCounts.Length != upGroups.Length)
            {
                room.upPointCounts = new int[upGroups.Length];
            }

            for (int i = 0; i < upGroups.Length; i++)
            {
                List<ObjectSpawnData> points = upGroups[i];
                room.upPointCounts[i] = points != null ? points.Count : 0;
            }
        }

        /// <summary>
        /// AS3 <c>xmll.@tip</c> — the classification read off the AllData definition row
        /// (<c>Location.as:658</c>), lower-cased so the three comparisons above match the oracle's exact
        /// string literals regardless of import casing. An object with no resolvable definition yields
        /// <c>""</c>, which is what AS3 does when the id is not in AllData at all: <c>xmll</c> is null,
        /// every <c>@tip</c> test is false, and the row lands in <c>objsT</c>.
        /// </summary>
        private static string DefinitionTip(ObjectSpawnData spawnData)
        {
            string tip = spawnData?.definition?.legacyTip;
            return string.IsNullOrWhiteSpace(tip) ? string.Empty : tip.Trim().ToLowerInvariant();
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

            // The arithmetic lives in WorldCoordinates so the player-spawn reader can share it rather
            // than re-derive the mirror — which is how the two came to disagree.
            return WorldCoordinates.As3BottomCentreAnchorPixels(
                spawnData.tileCoord, sizeTiles, roomHeight, borderOffset);
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
