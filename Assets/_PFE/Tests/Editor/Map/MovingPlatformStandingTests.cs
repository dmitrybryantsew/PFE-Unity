using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// A button-operated platform is a floor, and a rider is carried by it.
    ///
    /// <para><b>What was broken, in three separate places.</b> The report was "button operated
    /// platforms do not allow stand on them". Measured against the oracle and the port:</para>
    ///
    /// <list type="number">
    /// <item><description><b>No support surface.</b> <c>platform2</c>/<c>platform3</c> resolve to
    /// <c>MapObjectPhysicalCapability.DoorOccupancy</c> and are therefore never tracked by
    /// <c>RoomObjectPhysicsLayer</c>; AS3's <c>Unit.checkShelf</c> walks <em>every</em> box in the room
    /// (<c>Unit.as:2716-2719</c>), and both platforms author <c>shelf="1"</c>.</description></item>
    /// <item><description><b>No displacement.</b> The rider's carry is the support's <c>cdx</c>/<c>cdy</c>
    /// (<c>Unit.as:2014-2026</c>), which AS3 records in <c>Box.run</c> (<c>Box.as:643-644</c>). The port's
    /// only writer is <c>StepDynamicObject</c>, which skips non-dynamic props — so the platform slid out
    /// from under a rider standing perfectly still.</description></item>
    /// <item><description><b>No carry shift in the landing test.</b> AS3 passes <c>osndy</c> as the second
    /// argument of <c>checkShelf</c> (<c>Unit.as:2340</c>), which moves the upper end of the window. The
    /// port passed nothing, so a rising platform's top sat above the window and the platform overtook its
    /// rider.</description></item>
    /// </list>
    ///
    /// <para>Every test below has a control that fails if the term it names is removed, so a green run
    /// cannot be produced by the port having no platforms in it.</para>
    /// </summary>
    [TestFixture]
    public class MovingPlatformStandingTests
    {
        private const float Tolerance = 1e-3f;

        /// <summary>AS3's <c>TileQueryConstants.PorogGrounded</c>, inlined so this fixture needs no extra using.</summary>
        private const float Porog = 10f;

        /// <summary>
        /// The authored row of <c>platform2</c>: <c>family=Platform</c>, <c>size=3</c>, <c>wid=1</c>,
        /// <c>wall="1"</c> and — the term that makes it standable — <c>shelf="1"</c>
        /// (<c>Assets/_PFE/Data/Resources/MapObjects/Definitions/platform2.asset</c>).
        /// </summary>
        private static MapObjectDefinition CreatePlatformDefinition(bool authorShelf)
        {
            MapObjectDefinition definition = ScriptableObject.CreateInstance<MapObjectDefinition>();
            definition.objectId = "platform2";
            definition.legacyTip = "box";
            definition.family = MapObjectFamily.Platform;
            definition.size = 3;
            definition.width = 1;
            definition.physicalCapability = MapObjectPhysicalCapability.DoorOccupancy;

            definition.legacyAttributes.Add(new MapObjectAttributeData { key = "tip", value = "box" });
            definition.legacyAttributes.Add(new MapObjectAttributeData { key = "wall", value = "1" });
            definition.legacyAttributes.Add(new MapObjectAttributeData { key = "size", value = "3" });
            definition.legacyAttributes.Add(new MapObjectAttributeData { key = "wid", value = "1" });

            if (authorShelf)
            {
                definition.legacyAttributes.Add(new MapObjectAttributeData { key = "shelf", value = "1" });
            }

            return definition;
        }

        /// <summary>
        /// A room with one platform at <paramref name="position"/> (its bottom-left, room-local pixels).
        /// 120 × 40 px of footprint, so a platform at (120, 120) has its top at y = 160 and spans
        /// x ∈ [60, 180].
        /// </summary>
        private static RoomInstance CreateRoom(
            out ObjectInstance platform,
            bool authorShelf = true,
            bool withMoveNode = false,
            Vector2? position = null)
        {
            RoomInstance room = new RoomInstance
            {
                id = "test_room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                landPosition = new Vector3Int(0, 0, 0)
            };
            room.InitializeTiles();

            var attributes = new List<MapObjectAttributeData>
            {
                new MapObjectAttributeData { key = "scx", value = "120" },
                new MapObjectAttributeData { key = "scy", value = "40" }
            };

            if (withMoveNode)
            {
                // RoomsSerial2.as:4103 — `<move dy="-19" tstay="30" tmove="200"/>`, i.e. a lift that
                // rises 19 tiles. Kept at one tile / one frame here so a step is a whole leg.
                attributes.Add(new MapObjectAttributeData { key = "move_present", value = "1" });
                attributes.Add(new MapObjectAttributeData { key = "move_dy", value = "-1" });
                attributes.Add(new MapObjectAttributeData { key = "move_tstay", value = "0" });
                attributes.Add(new MapObjectAttributeData { key = "move_tmove", value = "1" });
            }

            platform = new ObjectInstance
            {
                objectId = "platform2",
                objectType = "box",
                definition = CreatePlatformDefinition(authorShelf),
                definitionId = "platform2",
                position = position ?? new Vector2(120f, 120f),
                isActive = true,
                attributes = attributes,
                runtimeState = new MapObjectRuntimeStateData()
            };

            platform.InitializeDynamicRuntimeState();
            room.AddObject(platform);
            room.Activate();
            return room;
        }

        /// <summary>The feet rect: 20 px wide, its bottom edge at <paramref name="feetY"/>.</summary>
        private static Rect FeetRect(float feetY)
        {
            return new Rect(120f, feetY, 20f, 40f);
        }

        // ── 1. the platform is a surface at all ───────────────────────────────────────────────

        [Test]
        public void Platform_IsRegisteredAsASurface_AndIsFoundUnderTheFeet()
        {
            RoomInstance room = CreateRoom(out ObjectInstance platform);

            Assert.AreEqual(1, room.ObjectPhysicsLayer.StaticShelfCount,
                "The platform must be registered as a non-dynamic shelf — before this list existed it " +
                "was not registered anywhere, because DoorOccupancy is not a dynamic capability.");

            bool found = room.ObjectPhysicsLayer.TryFindGroundPropUnder(
                FeetRect(160f), Porog, out ObjectInstance support, out float surfaceY);

            Assert.IsTrue(found, "A unit standing on the platform must find it as ground.");
            Assert.AreSame(platform, support);
            Assert.AreEqual(160f, surfaceY, Tolerance,
                "The support's top edge is position.y + scy = 120 + 40.");
        }

        [Test]
        public void Platform_WithoutAnAuthoredShelf_IsNotASurface()
        {
            // The control for the test above: same object, same capability, same size — only the
            // authored `shelf` is gone, and `wall="1"` then clears it (Box.as:269-274). If this test
            // ever passes the query, the registration is not reading `shelf` at all and the first test
            // is green for the wrong reason.
            RoomInstance room = CreateRoom(out ObjectInstance platform, authorShelf: false);

            Assert.AreEqual(0, room.ObjectPhysicsLayer.StaticShelfCount);

            Assert.IsFalse(room.ObjectPhysicsLayer.TryFindGroundPropUnder(
                    FeetRect(160f), Porog, out ObjectInstance support, out _),
                "A wall prop with no authored shelf is not a floor.");

            Assert.IsNull(support);
            Assert.IsFalse(platform.IsShelf(),
                "and the rule itself says so — this is the term the registration reads.");
        }

        // ── 2. a platform that rose this tick is still under the feet ─────────────────────────

        [Test]
        public void PlatformThatRoseThisTick_IsFoundOnlyWithItsDisplacement()
        {
            // The platform has risen 3.8 px since the unit last resolved its ground (a 19-tile rise
            // over 200 frames is exactly 3.8 px/frame), so its top is now ABOVE the feet.
            RoomInstance room = CreateRoom(out _, position: new Vector2(120f, 123.8f));
            RoomObjectPhysicsLayer layer = room.ObjectPhysicsLayer;

            Assert.IsFalse(layer.TryFindGroundPropUnder(
                    FeetRect(160f), Porog, out _, out _),
                "The un-shifted band stops at the feet, so a surface above them is invisible — this " +
                "is the reported bug in its purest form: the lift overtakes the rider.");

            Assert.IsTrue(layer.TryFindGroundPropUnder(
                    FeetRect(160f), Porog, out ObjectInstance support, out float surfaceY, carryPixels: 3.8f),
                "AS3's param2 shifts both ends of the band by the support's own displacement " +
                "(Unit.as:2720, osndy from :2014-2026).");

            Assert.AreEqual(163.8f, surfaceY, Tolerance);
            Assert.IsNotNull(support);
        }

        [Test]
        public void PlatformThatSankThisTick_IsFoundWithANegativeDisplacement()
        {
            // The mirror case: the platform is 3.8 px BELOW where it was, so the feet are in the air
            // and the un-shifted band already reaches it. Asserted so the carry term cannot be written
            // as "add the magnitude" and pass only the rising case.
            RoomInstance room = CreateRoom(out _, position: new Vector2(120f, 116.2f));
            RoomObjectPhysicsLayer layer = room.ObjectPhysicsLayer;

            Assert.IsTrue(layer.TryFindGroundPropUnder(
                    FeetRect(160f), Porog, out _, out float surfaceY, carryPixels: -3.8f),
                "A descending platform's top is within the band with or without the shift.");

            Assert.AreEqual(156.2f, surfaceY, Tolerance);
        }

        // ── 3. the motor's crossing test, which is the player's path ──────────────────────────

        [Test]
        public void RisingPlatform_IsCrossedWithTheCarryShift_ButNotWithoutIt()
        {
            // The player's motor applies the carry to its own move, so `feetAfter` already contains it:
            // feet start at 160, gravity pulls 0.5 down, the carry pushes 3.8 up, net +3.3.
            RoomInstance room = CreateRoom(out _, position: new Vector2(120f, 123.8f));
            RoomObjectPhysicsLayer layer = room.ObjectPhysicsLayer;

            Assert.IsFalse(layer.TryFindPropCrossedThisStep(
                    FeetRect(160f), 163.3f, out _, out _),
                "Without the shift the window is [feetAfter, feetBefore] = [163.3, 160], and the top " +
                "at 163.8 is above it — the platform passes through the rider.");

            Assert.IsTrue(layer.TryFindPropCrossedThisStep(
                    FeetRect(160f), 163.3f, out ObjectInstance support, out float surfaceY, carryPixels: 3.8f),
                "With AS3's param2 the window becomes [163.3, 163.8] and the top is the landing.");

            Assert.AreEqual(163.8f, surfaceY, Tolerance);
            Assert.IsNotNull(support);
        }

        // ── 4. the platform records what a rider follows ──────────────────────────────────────

        [Test]
        public void PlatformStep_RecordsTheDisplacementARiderFollows()
        {
            RoomInstance room = CreateRoom(out ObjectInstance platform, withMoveNode: true);
            Assert.AreEqual(1, room.MovingPlatforms.Count, "the placement authors a move node");

            Assert.IsTrue(room.MovingPlatforms.TryCommand(platform, "move"));

            room.MovingPlatforms.Step();
            Assert.AreEqual(0f, platform.runtimeState.dynamicState.cdy, Tolerance,
                "The first frame of the cycle is p = 0 — parked at the start, so no displacement.");

            room.MovingPlatforms.Step();

            Assert.AreEqual(40f, platform.runtimeState.dynamicState.cdy, Tolerance,
                "AS3 records `cdy = Y - stY` (Box.as:643-644) and the rider reads it next frame. " +
                "Without this write the carry is always zero and the platform slides out from under " +
                "a stationary rider.");

            Assert.AreEqual(160f, platform.position.y, Tolerance,
                "and it is the same number as the position change, not a second opinion about it.");
        }

        [Test]
        public void VerticalPlatform_RisesWhenAs3AuthorsANegativeDy()
        {
            // `dy="-19"` is authored in AS3's Y-DOWN space (Interact.as:448-451), so a negative value is
            // a RISE. The port's room-local Y runs up, so the endpoint has to be `BegY - dy * tileY`.
            // This used to add, which sent every vertical platform the wrong way.
            RoomInstance room = CreateRoom(out ObjectInstance platform, withMoveNode: true);
            room.MovingPlatforms.TryCommand(platform, "move");
            room.MovingPlatforms.Step();
            room.MovingPlatforms.Step();

            Assert.AreEqual(160f, platform.position.y, Tolerance,
                "120 + 40: one tile UP, not 80 (one tile down).");
        }
    }
}
