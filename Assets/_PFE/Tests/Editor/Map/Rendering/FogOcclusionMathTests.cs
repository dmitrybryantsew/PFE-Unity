using System;
using NUnit.Framework;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Tests.Editor.Map.Rendering
{
    /// <summary>
    /// Pins the fog-of-war rules to AS3.
    ///
    /// <para><b>Occlusion.</b> AS3 reads <c>Tile.opac</c> in exactly one place,
    /// <c>Location.lighting():3214</c>. On the decode path it is binary: <c>if(this.phis &gt; 0)
    /// this.opac = 1;</c> (<c>Tile.as:198-201</c>), and <c>setZForm(n &gt; 0)</c> clears it to 0
    /// (<c>Tile.as:293-296</c>). Water is the room's <c>wopac</c> option applied as a <c>max</c>
    /// (<c>Location.lighting():3217-3220</c>). A door object is the third writer, and the only one that
    /// writes a fraction (<c>Box.as:667, 685</c>).</para>
    ///
    /// <para><b>Reveal area.</b> <c>Location.lighting():3143-3148</c> and <c>Grafon.setLight():689-699</c>
    /// both start their walk at index 1, so tile column 0 and tile row 0 are never lit.</para>
    ///
    /// <para>The port previously returned 0.6 for a broad "looks solid" predicate. That only blocked a
    /// one-tile wall because the shadow ray then stepped 20 px (two samples per 40 px wall); once the
    /// step went back to AS3's 40 px, 0.6 would have let light straight through walls. These tests
    /// exist so the constant and the predicate can never drift apart again.</para>
    ///
    /// <para><b>The predicate was then wrong a second time.</b> It read <c>physicsType != Air</c>,
    /// which is wider than <c>phis &gt; 0</c>: a ladder decodes to <c>Stair</c> and a catwalk to
    /// <c>Platform</c>, but both forms carry <c>phis == 0</c>, so the oracle leaves them transparent.
    /// Keying on <c>!= Air</c> made 24 621 tiles across the 13 shipped rooms cast a total shadow.
    /// The old test here was named <c>NonAirPhysicsTypes_AllBlockLight</c> and asserted <c>1f</c> for
    /// both — a test named for a claim, asserting the bug. It is now
    /// <see cref="SurfacePhysicsTypes_DoNotBlockLight"/>.</para>
    /// </summary>
    [TestFixture]
    public class FogOcclusionMathTests
    {
        const float Tolerance = 1e-4f;

        /// <summary>"No door covers this tile" — spelled once so a test cannot pass a stray 0.</summary>
        const float NoDoor = TileData.NoDoorOcclusion;

        static float Occlusion(TilePhysicsType physicsType, int heightLevel = 0, bool hasWater = false, float waterOpacity = 0f)
        {
            return FogOcclusionMath.ResolveTileOcclusionOpacity(
                physicsType, NoDoor, heightLevel, hasWater, waterOpacity);
        }

        [Test]
        public void SolidTile_FullyBlocksLight()
        {
            Assert.AreEqual(1f, Occlusion(TilePhysicsType.Wall),
                Tolerance,
                "AS3 `if(this.phis > 0) this.opac = 1` (Tile.as:198-201) — one solid tile on a " +
                "40 px step must fully block a 40 px wall.");
        }

        [Test]
        public void AirTile_DoesNotBlockLight()
        {
            Assert.AreEqual(0f, Occlusion(TilePhysicsType.Air), Tolerance);
        }

        [Test]
        public void RaisedTile_DoesNotBlockLight_EvenWhenSolid()
        {
            Assert.AreEqual(0f, Occlusion(TilePhysicsType.Wall, heightLevel: 2),
                Tolerance,
                "AS3 `setZForm(n > 0) -> this.opac = 0` (Tile.as:293-296) *clears* the value set from " +
                "phis; a partial-height tile has air above it, so light passes over it.");
        }

        [Test]
        public void WaterWithoutRoomOption_DoesNotBlockLight()
        {
            Assert.AreEqual(0f, Occlusion(TilePhysicsType.Air, hasWater: true, waterOpacity: 0f),
                Tolerance,
                "`opacWater` defaults to 0 (LandAct.as:84) and the room only sets it from `wopac` " +
                "(LandAct.as:225-228).");
        }

        [Test]
        public void WaterWithRoomOption_BlocksLight_UsingTheRawOption()
        {
            Assert.AreEqual(0.7f, Occlusion(TilePhysicsType.Air, hasWater: true, waterOpacity: 0.7f),
                Tolerance,
                "AS3 uses `opacWater` directly (Location.lighting():3219), it does not halve it.");
        }

        [Test]
        public void WaterOnASolidTile_TakesTheMaximum_NotTheSum()
        {
            Assert.AreEqual(1f, Occlusion(TilePhysicsType.Wall, hasWater: true, waterOpacity: 0.7f),
                Tolerance,
                "Location.lighting():3217-3220 replaces `_loc18_` only when `opacWater > _loc18_`.");
        }

        [Test]
        public void WaterOpacityAboveOne_IsClamped()
        {
            Assert.AreEqual(1f, Occlusion(TilePhysicsType.Air, hasWater: true, waterOpacity: 3f),
                Tolerance,
                "A `wopac` above 1 would otherwise let a single tile over-block a ray.");
        }

        [Test]
        public void RaisedTileStillBlocksWhenTheRoomFillsItWithWater()
        {
            Assert.AreEqual(0.5f, Occlusion(TilePhysicsType.Wall, heightLevel: 3, hasWater: true, waterOpacity: 0.5f),
                Tolerance,
                "The water term is applied after the zForm clear, so a raised flooded tile still " +
                "attenuates by `wopac`.");
        }

        /// <summary>
        /// A ladder and a catwalk are NOT opaque. Both decode to a non-<c>Air</c> physics type, so this
        /// is the test that distinguishes "the port's physics enum" from "AS3's <c>phis</c>".
        /// </summary>
        [Test]
        public void SurfacePhysicsTypes_DoNotBlockLight()
        {
            Assert.AreEqual(0f, Occlusion(TilePhysicsType.Platform),
                Tolerance,
                "A shelf/catwalk form is `phis == 0` (Tile.as:186-189 — `shelf` is an independent " +
                "flag that never assigns `phis`), so Tile.inForm():198 never fires and `opac` stays 0.");

            Assert.AreEqual(0f, Occlusion(TilePhysicsType.Stair),
                Tolerance,
                "A stair/ladder form is `phis == 0` (Tile.as:194-197), so it is transparent to light.");
        }

        /// <summary>
        /// The exhaustive form, so a future fifth physics type cannot silently inherit opacity.
        ///
        /// <para>The <c>Wall</c> assertion is a positive control in the same test on purpose: a rule
        /// that returned <c>0f</c> for everything would fail here instead of passing.</para>
        /// </summary>
        [Test]
        public void OnlyWallPhysicsType_BlocksLight()
        {
            Assert.AreEqual(1f, Occlusion(TilePhysicsType.Wall),
                Tolerance,
                "Positive control: `phis > 0` is the only condition that sets `opac = 1`.");

            Assert.AreEqual(0f, Occlusion(TilePhysicsType.Air), Tolerance);
            Assert.AreEqual(0f, Occlusion(TilePhysicsType.Platform), Tolerance, "phis == 0.");
            Assert.AreEqual(0f, Occlusion(TilePhysicsType.Stair), Tolerance, "phis == 0.");
        }

        // ---------------------------------------------------------------------------------------
        // The door term — AS3 Box.door_opac.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The sentinel must mean "absent", not "zero". A rule that treated
        /// <c>TileData.NoDoorOcclusion</c> as a value would make every wall transparent, which is the
        /// exact failure the door term is most likely to introduce.
        /// </summary>
        [Test]
        public void NoDoorSentinel_LeavesTheWallOpaque()
        {
            Assert.AreEqual(1f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(
                    TilePhysicsType.Wall, TileData.NoDoorOcclusion, 0, false, 0f),
                Tolerance,
                "Positive control for the door term: -1 means 'no door', so a plain wall still blocks.");

            Assert.Less(TileData.NoDoorOcclusion, 0f,
                "The sentinel must stay negative; 0 is a meaningful door value (an open door).");
        }

        [Test]
        public void ClosedWoodenDoor_BlocksLightByItsOwnOpac_NotCompletely()
        {
            Assert.AreEqual(0.8f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(
                    TilePhysicsType.Wall, 0.8f, 0, false, 0f),
                Tolerance,
                "`door1`/`door1a`/`door1b`/`hatch1` author `opac='0.8'` (AllData.as:4864-4873) and " +
                "Box.as:685 assigns it, so a closed wooden door still lets 0.2 of the ray through.");
        }

        [Test]
        public void ClosedGrate_IsAlmostTransparent()
        {
            Assert.AreEqual(0.1f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(
                    TilePhysicsType.Wall, 0.1f, 0, false, 0f),
                Tolerance,
                "`grate`/`hgrate`/`alib1`/`alib2` author `opac='0.1'` (AllData.as:4859-4860, 5038-5039) " +
                "— you can see through a closed grate in AS3, and the port could not.");
        }

        [Test]
        public void OpenDoor_IsTransparent()
        {
            Assert.AreEqual(0f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(
                    TilePhysicsType.Air, 0f, 0, false, 0f),
                Tolerance,
                "Box.setDoor(true) writes `phis = 0` and `opac = 0` (Box.as:684-685).");
        }

        /// <summary>
        /// A door value is only honoured while the tile is solid — so a stale one left behind by a
        /// destroyed or carved tile cannot resurrect an occlusion nothing owns.
        /// </summary>
        [Test]
        public void DoorOcclusionOnANonSolidTile_IsIgnored()
        {
            Assert.AreEqual(0f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(
                    TilePhysicsType.Air, 0.8f, 0, false, 0f),
                Tolerance,
                "AS3's `opac` is one field, so a door value surviving on an air tile can only be stale; " +
                "reading it would make an opened doorway dark again.");

            Assert.AreEqual(0f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(
                    TilePhysicsType.Stair, 0.8f, 0, false, 0f),
                Tolerance,
                "Same for the surface types — a catwalk under a door is still `phis == 0`.");
        }

        /// <summary>
        /// <c>setZForm</c> runs in the decode pass and <c>initDoor</c> after it, so a door's write is the
        /// later one and beats the raised-tile clear.
        /// </summary>
        [Test]
        public void RaisedDoorTile_StillUsesTheDoorValue()
        {
            Assert.AreEqual(0.3f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(
                    TilePhysicsType.Wall, 0.3f, 2, false, 0f),
                Tolerance,
                "`door4`/`enclpole` author `opac='0.3'` (AllData.as:4872, 4876).");
        }

        [Test]
        public void DoorOcclusion_StillTakesTheWaterMaximum()
        {
            Assert.AreEqual(0.5f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(
                    TilePhysicsType.Wall, 0.3f, 0, true, 0.5f),
                Tolerance,
                "Location.lighting():3217-3220 maxes water over whatever `opac` holds, including a " +
                "door's value — it is not a second assignment.");

            Assert.AreEqual(0.3f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(
                    TilePhysicsType.Wall, 0.3f, 0, true, 0.2f),
                Tolerance,
                "And a weaker `wopac` must not lower a door's value either.");
        }

        [Test]
        public void DoorOcclusionAboveOne_IsClamped()
        {
            Assert.AreEqual(1f,
                FogOcclusionMath.ResolveTileOcclusionOpacity(
                    TilePhysicsType.Wall, 3f, 0, false, 0f),
                Tolerance,
                "A stray `opac` above 1 would otherwise let one door over-block a whole ray.");
        }

        // ---------------------------------------------------------------------------------------
        // The reveal area — the ring AS3's light pass never writes.
        // ---------------------------------------------------------------------------------------

        [Test]
        public void RevealArea_ExcludesTheLeftColumnAndTheTopRow()
        {
            const int width = 48;
            const int height = 25;

            Assert.IsFalse(FogOcclusionMath.IsInsideRevealArea(0, 0, width, height),
                "AS3 starts both walks at 1 (Location.lighting():3143-3148).");
            Assert.IsFalse(FogOcclusionMath.IsInsideRevealArea(0, 10, width, height),
                "Column 0 — `_loc7_ = 1` skips it on every row.");
            Assert.IsFalse(FogOcclusionMath.IsInsideRevealArea(10, height - 1, width, height),
                "Row 0 in AS3 is the TOP row (`nroom.a[0]`), and ParseRoom flips it to y = height - 1.");
        }

        /// <summary>
        /// The positive control: AS3's bound is <c>while(i &lt; spaceX)</c>, not
        /// <c>&lt;= spaceX - 1</c>, so the right column and the far row <b>are</b> lit. A predicate that
        /// skipped a full ring would pass the test above and fail this one.
        /// </summary>
        [Test]
        public void RevealArea_IncludesTheRightColumnAndTheFarRow()
        {
            const int width = 48;
            const int height = 25;

            Assert.IsTrue(FogOcclusionMath.IsInsideRevealArea(width - 1, 10, width, height),
                "`_loc7_` runs to spaceX - 1 inclusive.");
            Assert.IsTrue(FogOcclusionMath.IsInsideRevealArea(10, 0, width, height),
                "`_loc8_` runs to spaceY - 1 inclusive, which ParseRoom maps to y = 0.");
            Assert.IsTrue(FogOcclusionMath.IsInsideRevealArea(width - 1, 0, width, height),
                "The corner opposite the skipped one is lit.");
        }

        /// <summary>
        /// Counts the cells rather than sampling them, so the band can only be one tile wide and can
        /// only be on two edges.
        /// </summary>
        [Test]
        public void RevealArea_IsTheGridMinusOneColumnAndOneRow()
        {
            const int width = 48;
            const int height = 25;

            int inside = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (FogOcclusionMath.IsInsideRevealArea(x, y, width, height))
                    {
                        inside++;
                    }
                }
            }

            Assert.AreEqual((width - 1) * (height - 1), inside,
                "47 x 24 = 1128 of the 1200 tiles are lit; the 72 that are not are column 0 " +
                "(25 tiles), the top row (48), and they share the corner.");
        }

        [Test]
        public void RevealArea_IsEmptyForADegenerateGrid()
        {
            Assert.IsFalse(FogOcclusionMath.IsInsideRevealArea(0, 0, 0, 0),
                "A zero-sized grid must light nothing rather than index out of range.");
            Assert.IsFalse(FogOcclusionMath.IsInsideRevealArea(0, 0, 1, 1),
                "A 1x1 grid is all ring.");
        }

        // ── The refresh gate — the port of AS3 `Location.step():3398` ────────────────────────────
        //
        // The oracle is `if(camera moved > 0.5px || … || isRelight || isRebuild) lighting()`. Each of
        // the port's three inputs stands in for one term, and each must be able to force the pass
        // ALONE: an `&&` here would silently drop the door-open and wall-destroyed relights, which is
        // the exact bug this gate was fixed for. The three single-input tests are therefore each
        // other's controls, and the all-false case is the one that proves the gate is not always-on.

        [Test]
        public void FullLightPass_NoInput_DoesNotRun()
        {
            Assert.IsFalse(FogOcclusionMath.ShouldRunFullLightPass(
                    maskDirty: false, playerSampleChanged: false, relightRequested: false),
                "With nothing stale, a follow-up frame must not pay for a full re-raycast.");
        }

        [Test]
        public void FullLightPass_DirtyMask_Runs()
        {
            // Room teardown, the fog toggle, and RevealAll set the flag directly.
            Assert.IsTrue(FogOcclusionMath.ShouldRunFullLightPass(true, false, false));
        }

        [Test]
        public void FullLightPass_PlayerMoved_Runs()
        {
            // The port's stand-in for AS3's camera-moved terms — a changed player tile sample.
            Assert.IsTrue(FogOcclusionMath.ShouldRunFullLightPass(false, true, false));
        }

        [Test]
        public void FullLightPass_RelightRequested_Runs()
        {
            // The door-open / wall-destroyed term (`isRelight || isRebuild`). This is the input that
            // was missing: without it, a door opened while the player stood still never refreshed the
            // mask, and the doorway stayed dark until the player stepped.
            Assert.IsTrue(FogOcclusionMath.ShouldRunFullLightPass(false, false, true));
        }

        // ── The shadow ray — `Location.lighting():3196, 3212-3213` ───────────────────────────────
        //
        // The walk advances 40 px along the dominant axis per step and runs `_loc16_ = 1 … <= _loc15_`,
        // so the sample count is `floor(d / 40)` and the samples sit on the tile lattice. The port used
        // to short-circuit with "within 40 px ⇒ nothing in the way", which is a different rule and lit
        // the solid tile directly below a light. These pin the count; the four `RayTransmission_` cases
        // below pin the behaviour that count produces.

        const float Tile = 40f;

        /// <summary>Tiles at the given room-local pixel, so a test can place an occluder exactly.</summary>
        static Func<Vector2, float> SolidAt(params Vector2Int[] tiles)
        {
            return p =>
            {
                int tx = Mathf.FloorToInt(p.x / Tile);
                int ty = Mathf.FloorToInt(p.y / Tile);
                for (int i = 0; i < tiles.Length; i++)
                {
                    if (tiles[i].x == tx && tiles[i].y == ty)
                    {
                        return 1f;
                    }
                }

                return 0f;
            };
        }

        [Test]
        public void OcclusionRay_AtExactlyOneTile_ChargesTheTargetTile()
        {
            // THE regression. The tile directly below a light is exactly 40 px away, and AS3's walk
            // reaches it (k = 1). The old port short-circuit returned "transparent" for anything
            // within 40 px, so a solid floor one tile below a light was charged nothing and lit —
            // "I can see the tile below me, the original leaves it dark".
            Assert.AreEqual(1, FogOcclusionMath.OcclusionRaySampleCount(Tile, Tile));
        }

        [Test]
        public void OcclusionRay_WithinOneTile_ChargesNothing()
        {
            // The other half of the same rule: a light must not occlude on the tile it stands in.
            Assert.AreEqual(0, FogOcclusionMath.OcclusionRaySampleCount(Tile * 0.5f, Tile));
        }

        [Test]
        public void OcclusionRay_TwoTiles_ChargesTwice()
        {
            Assert.AreEqual(2, FogOcclusionMath.OcclusionRaySampleCount(Tile * 2f, Tile));
        }

        [Test]
        public void OcclusionRay_BetweenOneAndTwoTiles_ChargesOnce()
        {
            // `floor`, not `ceil`: AS3's last sample sits on the lattice, so a target 60 px away is
            // charged one tile and NOT the target itself. Changing this to `ceil` would charge the
            // target early and darken a band of tiles the oracle lights.
            Assert.AreEqual(1, FogOcclusionMath.OcclusionRaySampleCount(Tile * 1.5f, Tile));
        }

        [Test]
        public void OcclusionRay_ZeroDistance_ChargesNothing()
        {
            Assert.AreEqual(0, FogOcclusionMath.OcclusionRaySampleCount(0f, Tile));
        }

        [Test]
        public void RayTransmission_SolidTileDirectlyBelowTheLight_IsBlocked()
        {
            // End-to-end through the shipped ray: a light at the centre of tile (2,2) aimed at the
            // centre of tile (2,1) — the tile directly below it in port space. The ray must charge
            // that tile and come back blocked.
            Vector2 light = new Vector2(100f, 100f);
            Vector2 below = new Vector2(100f, 60f);

            float transmission = FogOcclusionMath.ResolveRayTransmission(
                light, below, Tile, SolidAt(new Vector2Int(2, 1)));

            Assert.AreEqual(0f, transmission, Tolerance,
                "A solid tile one tile below the light must block it. Returning 1 here is the " +
                "reported bug: the tile below the player was lit in the port and dark in the original.");
        }

        [Test]
        public void RayTransmission_ClearPath_IsFullyTransmitted()
        {
            // Control for the test above: same geometry, nothing in the way. Without this, a ray that
            // returned 0 for every input would look like a pass.
            float transmission = FogOcclusionMath.ResolveRayTransmission(
                new Vector2(100f, 100f), new Vector2(100f, 60f), Tile, SolidAt());

            Assert.AreEqual(1f, transmission, Tolerance);
        }

        [Test]
        public void RayTransmission_SolidTileDirectlyBesideTheLight_IsBlocked()
        {
            // Direction control: the fix must not be vertical-only. Same one-tile distance, along X.
            float transmission = FogOcclusionMath.ResolveRayTransmission(
                new Vector2(100f, 100f), new Vector2(140f, 100f), Tile, SolidAt(new Vector2Int(3, 2)));

            Assert.AreEqual(0f, transmission, Tolerance);
        }

        [Test]
        public void RayTransmission_TheLightsOwnTile_IsNeverSelfOccluding()
        {
            // The case the old short-circuit was accidentally covering: a target inside the light's own
            // tile is 20 px away, so the count is 0 and the tile's own solidity must not block. A fix
            // that simply charged every tile would make a light in a wall blind itself.
            float transmission = FogOcclusionMath.ResolveRayTransmission(
                new Vector2(100f, 100f), new Vector2(100f, 120f), Tile, SolidAt(new Vector2Int(2, 2)));

            Assert.AreEqual(1f, transmission, Tolerance);
        }

        [Test]
        public void RayTransmission_FractionalTile_SubtractsRatherThanBlocks()
        {
            // AS3 accumulates subtractively (`_loc6_ -= _loc18_`, Location.as:3223). A closed grate's
            // 0.1 opac dims a ray to 0.9; it does not stop it.
            float transmission = FogOcclusionMath.ResolveRayTransmission(
                new Vector2(100f, 100f), new Vector2(100f, 60f), Tile, _ => 0.1f);

            Assert.AreEqual(0.9f, transmission, Tolerance);
        }

        // ── The player's light source — `Location.lighting():3137-3140` ───────────────────────────
        //
        // The port shipped the player's light at `transform.position`, which is the unit's FEET
        // (`PlayerRigBuilder`: the collider is offset `+height/2` above the origin, "because the
        // origin is the unit's feet"). AS3 puts it 12 px in front of the unit and 75.3 % of the way
        // up its sprite. These tests pin the offset, and the two below them pin why it matters.

        [Test]
        public void PlayerLight_IsNotAtTheUnitsFeet()
        {
            // The control for the whole group: if the vertical term were 0 the rule would be a no-op
            // and every other test here would pass while the port stayed wrong.
            Vector2 offset = FogOcclusionMath.ResolvePlayerLightPixelOffset(facingDirection: 1, spriteHeightPixels: 70f);

            Assert.Greater(offset.y, 0f, "AS3 `gg.Y1 + gg.stayY*0.247` is above the feet, never at them.");
        }

        [Test]
        public void PlayerLight_SitsAt753PercentOfTheSpriteHeight()
        {
            // `Y1 + stayY*0.247` with `Y1 = Y - scY` (Unit.as:1875) and `stayY = scY` (Unit.as:1027)
            // is `Y - scY*0.753`. 70 px is this project's default player sprite height
            // (UnitDefinition.height = 0.70).
            Vector2 offset = FogOcclusionMath.ResolvePlayerLightPixelOffset(1, 70f);

            Assert.AreEqual(70f * 0.753f, offset.y, Tolerance);
            Assert.AreEqual(52.71f, offset.y, 0.01f,
                "The default player's light is ~53 px above its feet — chest height, not the ground.");
        }

        [Test]
        public void PlayerLight_FacingRight_SitsTwelvePixelsForward()
        {
            Assert.AreEqual(12f, FogOcclusionMath.ResolvePlayerLightPixelOffset(1, 70f).x, Tolerance);
        }

        [Test]
        public void PlayerLight_FacingLeft_SitsTwelvePixelsBack()
        {
            Assert.AreEqual(-12f, FogOcclusionMath.ResolvePlayerLightPixelOffset(-1, 70f).x, Tolerance);
        }

        [Test]
        public void PlayerLight_ZeroFacing_FoldsToRight()
        {
            // AS3's `storona` is only ever ±1 (Unit.as:602-612), so 0 is not a state it can be in.
            // Folding it to +1 keeps the forward term rather than silently dropping it to 0.
            Assert.AreEqual(12f, FogOcclusionMath.ResolvePlayerLightPixelOffset(0, 70f).x, Tolerance);
        }

        [Test]
        public void PlayerLight_ZeroHeightSprite_HasNoVerticalOffset()
        {
            Assert.AreEqual(0f, FogOcclusionMath.ResolvePlayerLightPixelOffset(1, 0f).y, Tolerance);
        }

        [Test]
        public void RayTransmission_LightOnTheFeet_TipsTheFirstSampleIntoTheTileBelow()
        {
            // **Why the light position and the ray rule are a matched pair.**
            //
            // A grounded unit's feet rest exactly on a tile boundary — `ResolveVerticalDown` returns
            // `tileTop`. Put the light there and the ray to the tile below takes exactly ONE sample,
            // at `feet - 40`: the floor tile's bottom edge. A hair of downward error moves that
            // sample into the tile below, the floor is never charged, and the tile below lights up.
            // `RoomVisualController` reads the light off `transform.position`, which has been through
            // the `×0.01`/`×100` pixel↔unit round trip — so the hair is really there, and it grows
            // with the room's world position.
            const float Feet = 40f;                     // standing on the top edge of tile row 0
            Vector2 target = new Vector2(20f, -20f);    // centre of tile (0, -1) — the tile below
            Func<Vector2, float> floorIsSolid = SolidAt(new Vector2Int(0, 0));

            float exact = FogOcclusionMath.ResolveRayTransmission(
                new Vector2(20f, Feet), target, Tile, floorIsSolid);
            Assert.AreEqual(0f, exact, Tolerance,
                "Exactly on the boundary the sample lands in the floor tile and blocks.");

            float hairLow = FogOcclusionMath.ResolveRayTransmission(
                new Vector2(20f, Feet - 1e-4f), target, Tile, floorIsSolid);
            Assert.AreEqual(1f, hairLow, Tolerance,
                "A ten-thousandth of a pixel lower and the same ray misses the floor entirely.");
        }

        [Test]
        public void RayTransmission_As3LightPosition_IsImmuneToThatError()
        {
            // Same geometry, light where AS3 puts it. The first sample now lands ~13 px inside the
            // floor tile, so both the exact and the hair-low case block. This is the assertion that
            // makes the lattice ray safe to ship: the ray rule alone is knife-edged, the ray rule
            // plus the oracle's light position is not.
            const float Feet = 40f;
            Vector2 offset = FogOcclusionMath.ResolvePlayerLightPixelOffset(1, 70f);
            Vector2 target = new Vector2(20f, -20f);
            Func<Vector2, float> floorIsSolid = SolidAt(new Vector2Int(0, 0));

            float exact = FogOcclusionMath.ResolveRayTransmission(
                new Vector2(20f, Feet) + offset, target, Tile, floorIsSolid);
            float hairLow = FogOcclusionMath.ResolveRayTransmission(
                new Vector2(20f, Feet - 1e-4f) + offset, target, Tile, floorIsSolid);

            Assert.AreEqual(0f, exact, Tolerance);
            Assert.AreEqual(0f, hairLow, Tolerance,
                "At AS3's light height no epsilon can push the first sample out of the floor tile.");
        }

        // ── The light pass's sample point — `Location.lighting():3152-3153` ───────────────────────
        //
        // AS3 measures the light's distance to the tile's TOP-LEFT CORNER, `(i * tileX, j * tileY)`,
        // and aims the occlusion ray at that same point. The port sampled the tile CENTRE. Because
        // the ray's target moved with it, that was not a half-tile drawing offset — it changed which
        // tiles are lit. See ResolveLightSamplePixel for the measurement.

        [Test]
        public void LightSample_SitsOnTheTilesTopLeftCorner()
        {
            Vector2 sample = FogOcclusionMath.ResolveLightSamplePixel(3, 5, Tile);

            Assert.AreEqual(120f, sample.x, Tolerance, "3 * 40 — the tile's left edge");
            Assert.AreEqual(240f, sample.y, Tolerance,
                "(5 + 1) * 40 — the tile's TOP edge. Port Y is up, AS3's is down, so AS3's `j * tileY` " +
                "is the port tile's top, i.e. `(y + 1) * 40`. Using `y * 40` would drop it a whole tile.");
        }

        [Test]
        public void LightSample_IsNotTheTileCentre()
        {
            // The control for this group: if the sample were still the centre every test here would
            // pass while the port kept the bug.
            Vector2 centre = new Vector2(3 * Tile + Tile * 0.5f, 5 * Tile + Tile * 0.5f);

            Assert.AreNotEqual(centre, FogOcclusionMath.ResolveLightSamplePixel(3, 5, Tile));
        }

        [Test]
        public void LightSample_AdjacentTilesAreExactlyOneTileApart()
        {
            Vector2 here = FogOcclusionMath.ResolveLightSamplePixel(3, 5, Tile);
            Vector2 right = FogOcclusionMath.ResolveLightSamplePixel(4, 5, Tile);
            Vector2 up = FogOcclusionMath.ResolveLightSamplePixel(3, 6, Tile);

            Assert.AreEqual(Tile, right.x - here.x, Tolerance);
            Assert.AreEqual(0f, right.y - here.y, Tolerance);
            Assert.AreEqual(0f, up.x - here.x, Tolerance);
            Assert.AreEqual(Tile, up.y - here.y, Tolerance);
        }

        // ── The ray's tile lookup — `Location.getAbsTile():2333-2340` ─────────────────────────────

        [Test]
        public void LightSample_LiesInsideTheTileItBelongsTo()
        {
            // AS3's invariant: `getAbsTile(i * tileX, j * tileY)` is `space[i][j]` — the target point
            // belongs to the tile it was computed for. This is what ties the two new rules together,
            // and it is the assertion the old plain-`floor` lookup fails: `floor(40 * (y + 1) / 40)`
            // is `y + 1`, the tile ABOVE.
            const int RoomHeightTiles = 25;

            for (int x = 1; x < 6; x++)
            {
                for (int y = 1; y < 6; y++)
                {
                    Vector2 sample = FogOcclusionMath.ResolveLightSamplePixel(x, y, Tile);
                    Vector2Int coord = FogOcclusionMath.ResolveRayLookupCoord(
                        sample.x, sample.y, RoomHeightTiles, Tile);

                    Assert.AreEqual(x, coord.x, $"column of the sample for tile ({x}, {y})");
                    Assert.AreEqual(y, coord.y, $"row of the sample for tile ({x}, {y})");
                }
            }
        }

        [Test]
        public void RayLookup_ExactBoundary_NamesTheTileBelow_NotAbove()
        {
            // AS3 floors in a Y-DOWN space. The port's Y is up, so the row is the mirror of that
            // floor. At `localY = 40` — the top edge of port row 0 — AS3's answer is row 0 (the tile
            // whose top edge it is); a plain floor says row 1.
            Vector2Int coord = FogOcclusionMath.ResolveRayLookupCoord(0f, 40f, 25, Tile);

            Assert.AreEqual(0, coord.y,
                "the boundary belongs to the tile BELOW it in port terms, not the one above");
            Assert.AreEqual(0, coord.x);
        }

        [Test]
        public void RayLookup_InteriorPixel_AgreesWithThePlainFloor()
        {
            // The two conventions differ only ON a boundary — so this pins that the fix is a
            // boundary correction and not a wholesale re-indexing.
            Vector2Int interior = FogOcclusionMath.ResolveRayLookupCoord(41f, 41f, 25, Tile);

            Assert.AreEqual(Mathf.FloorToInt(41f / Tile), interior.x);
            Assert.AreEqual(Mathf.FloorToInt(41f / Tile), interior.y);
        }
    }
}
