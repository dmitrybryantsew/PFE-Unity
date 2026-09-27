using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Tests.Editor.Map.Rendering
{
    /// <summary>
    /// Pins the fog-of-war emitter rule to AS3's.
    ///
    /// AS3's only *baked* emitters are room objects carrying the <c>light</c> attribute:
    /// <c>Box.as:377-379</c> parses <c>xml.@light</c>, <c>Location.lightAll():3096-3107</c> iterates
    /// <c>this.objs</c>, and <c>Land.ativateLoc():1273</c> calls it once per room activation. Each
    /// emitter contributes three sources — the ±10 px triplet at <c>Location.as:3103-3105</c>.
    ///
    /// Backdrop art is **not** an emitter. <c>BackObj.light</c> (<c>BackObj.as:25</c>) is a
    /// <c>MovieClip</c> — the <c>_l</c> "lit" graphic drawn into <c>colorBmp</c> at
    /// <c>Grafon.as:607</c> — and <c>&lt;back&gt;</c> nodes live in <c>Location.backobjs</c>
    /// (<c>Location.as:704</c>), which <c>lightAll()</c> never visits.
    ///
    /// Regression this guards: the port additionally matched any id containing
    /// "light"/"lamp"/"torch" and applied that test to <c>room.backgroundDecorations</c> as well.
    /// In <c>Base/room_2_0</c> that turned six <c>&lt;back id="light4|light5"&gt;</c> decorations into
    /// 18 of 21 light sources and made each visibility refresh cost ~20 ms (43 FPS in play).
    /// See docs/Perf_FogOfWar_RoomLag_Investigation.md.
    ///
    /// Every "must not emit" case below is paired with a control emitter in the same room, so the
    /// expected count can only come out right if the bake actually ran *and* the negative case
    /// contributed nothing. An unpaired expectation of 0 would also pass if the bake never ran at
    /// all — which is exactly how the first version of this fixture produced six false failures.
    /// </summary>
    [TestFixture]
    public class RoomBackdropLightSourceTests
    {
        /// <summary><c>Location.as:3103-3105</c> emits at X-10, X, X+10 for every light object.</summary>
        const int Triplet = 3;

        [Test]
        public void RebuildLightSources_BackdropDecorationNamedLight_DoesNotEmit()
        {
            RoomInstance room = CreateRoom();
            room.backgroundDecorations.Add(CreateDecoration("light4", 3, 3));
            room.backgroundDecorations.Add(CreateDecoration("light5", 9, 4));
            room.objects.Add(CreateLightObject("control", new Vector2(200f, 200f)));

            Assert.AreEqual(Triplet, Bake(room).StaticLightSourceCount,
                "A <back id=\"lightN\"> decoration is backdrop art, not a light: BackObj.light is the " +
                "'_l' lit MovieClip (BackObj.as:25) drawn into colorBmp at Grafon.as:607, and <back> " +
                "nodes go to Location.backobjs (Location.as:704), which lightAll() never iterates. " +
                "Before the fix these two decorations alone added six sources on top of the control.");
        }

        [Test]
        public void RebuildLightSources_ObjectIdContainingLightWithoutAttribute_DoesNotEmit()
        {
            RoomInstance room = CreateRoom();
            room.objects.Add(CreateObject("lightpost", new Vector2(200f, 200f)));
            room.objects.Add(CreateObject("lamp", new Vector2(240f, 200f)));
            room.objects.Add(CreateObject("torch", new Vector2(280f, 200f)));
            room.objects.Add(CreateLightObject("control", new Vector2(320f, 200f)));

            Assert.AreEqual(Triplet, Bake(room).StaticLightSourceCount,
                "An id substring is not an AS3 signal — only the `light` attribute is (Box.as:377-379). " +
                "Do not reintroduce the name heuristic.");
        }

        [Test]
        public void RebuildLightSources_ObjectWithLightAttribute_EmitsOneTriplet()
        {
            RoomInstance room = CreateRoom();
            room.objects.Add(CreateLightObject("brazier", new Vector2(200f, 200f)));

            Assert.AreEqual(Triplet, Bake(room).StaticLightSourceCount);
        }

        [Test]
        public void RebuildLightSources_TwoLightObjects_EmitTwoTriplets()
        {
            RoomInstance room = CreateRoom();
            room.objects.Add(CreateLightObject("brazier", new Vector2(200f, 200f)));
            room.objects.Add(CreateLightObject("candle", new Vector2(400f, 200f)));

            Assert.AreEqual(Triplet * 2, Bake(room).StaticLightSourceCount,
                "lightAll() calls lighting() three times per light object (Location.as:3103-3105), so " +
                "the source count is 3 per emitter, not 1.");
        }

        [TestCase("1")]
        [TestCase("true")]
        [TestCase("TRUE")]
        [TestCase("yes")]
        [TestCase("on")]
        public void RebuildLightSources_TruthyLightAttributeValues_Emit(string attributeValue)
        {
            RoomInstance room = CreateRoom();
            room.objects.Add(CreateLightObject("brazier", new Vector2(200f, 200f), attributeValue));

            Assert.AreEqual(Triplet, Bake(room).StaticLightSourceCount);
        }

        [TestCase("0")]
        [TestCase("false")]
        [TestCase("no")]
        [TestCase("off")]
        [TestCase("")]
        [TestCase("   ")]
        public void RebuildLightSources_FalsyLightAttributeValues_DoNotEmit(string attributeValue)
        {
            RoomInstance room = CreateRoom();
            room.objects.Add(CreateLightObject("brazier", new Vector2(200f, 200f), attributeValue));
            room.objects.Add(CreateLightObject("control", new Vector2(400f, 200f)));

            Assert.AreEqual(Triplet, Bake(room).StaticLightSourceCount,
                $"`light=\"{attributeValue}\"` is falsy, so only the control emitter contributes.");
        }

        [Test]
        public void RebuildLightSources_InactiveLightObject_DoesNotEmit()
        {
            RoomInstance room = CreateRoom();
            ObjectInstance inactive = CreateLightObject("brazier", new Vector2(200f, 200f));
            inactive.isActive = false;
            room.objects.Add(inactive);
            room.objects.Add(CreateLightObject("control", new Vector2(400f, 200f)));

            Assert.AreEqual(Triplet, Bake(room).StaticLightSourceCount);
        }

        [Test]
        public void RebuildLightSources_LightOutsideRoomBounds_IsDropped()
        {
            RoomInstance room = CreateRoom();
            // The -20 px offset puts the triplet at y = -20, i.e. outside the room's 0..1000 px span.
            room.objects.Add(CreateLightObject("brazier", new Vector2(200f, 0f)));
            room.objects.Add(CreateLightObject("control", new Vector2(400f, 200f)));

            Assert.AreEqual(Triplet, Bake(room).StaticLightSourceCount,
                "AddLightSource drops positions outside the room rect; the AS3 pass would likewise " +
                "have no tiles to write there.");
        }

        [Test]
        public void RebuildLightSources_IsIdempotent()
        {
            RoomInstance room = CreateRoom();
            room.objects.Add(CreateLightObject("brazier", new Vector2(200f, 200f)));

            RoomBackdropRenderer renderer = Bake(room);
            int first = renderer.StaticLightSourceCount;
            renderer.RebuildLightSources();

            Assert.AreEqual(Triplet, first);
            Assert.AreEqual(first, renderer.StaticLightSourceCount,
                "lightAll() rebuilds from scratch (Location.as:3096); a second call must not accumulate.");
        }

        /// <summary>
        /// Mirrors the reported room: six <c>&lt;back id="light4|light5"&gt;</c> decorations, five
        /// objects with no <c>light</c> attribute, and one object that does carry it. Before the fix
        /// this produced 21 sources (3 player + 18 phantom); AS3 produces 3.
        /// </summary>
        [Test]
        public void RebuildLightSources_Room20Shape_MatchesAs3EmitterCount()
        {
            RoomInstance room = CreateRoom();

            room.backgroundDecorations.Add(CreateDecoration("light4", 3, 3));
            room.backgroundDecorations.Add(CreateDecoration("light5", 4, 3));
            room.backgroundDecorations.Add(CreateDecoration("light4", 8, 5));
            room.backgroundDecorations.Add(CreateDecoration("light5", 9, 5));
            room.backgroundDecorations.Add(CreateDecoration("light4", 14, 8));
            room.backgroundDecorations.Add(CreateDecoration("light5", 15, 8));

            room.objects.Add(CreateObject("locker", new Vector2(200f, 200f)));
            room.objects.Add(CreateObject("woodbox", new Vector2(240f, 200f)));
            room.objects.Add(CreateObject("table", new Vector2(280f, 200f)));
            room.objects.Add(CreateObject("chair", new Vector2(320f, 200f)));
            room.objects.Add(CreateObject("barrel", new Vector2(360f, 200f)));
            room.objects.Add(CreateLightObject("lamp", new Vector2(400f, 200f)));

            Assert.AreEqual(Triplet, Bake(room).StaticLightSourceCount,
                "AS3 bakes one triplet for the single `light=\"1\"` object and nothing for the six " +
                "light4/light5 backdrop decorations or the other five ids.");
        }

        /// <summary>
        /// Builds the renderer and runs the bake step. <c>CreateVisuals()</c> would normally do this
        /// (<c>Land.ativateLoc()</c> -&gt; <c>lightAll()</c>), but it bails out early without a real
        /// transform hierarchy, so the EditMode tests drive <see cref="RoomBackdropRenderer.RebuildLightSources"/>
        /// directly — that is what the method is public for.
        /// </summary>
        RoomBackdropRenderer Bake(RoomInstance room)
        {
            RoomBackdropRenderer renderer = CreateRenderer(room);
            renderer.RebuildLightSources();
            return renderer;
        }

        RoomBackdropRenderer CreateRenderer(RoomInstance room)
        {
            return new RoomBackdropRenderer(
                room,
                tileTextureLookup: null,
                backgroundLookup: null,
                settingsLookup: null,
                roomKey: "test",
                backgroundParent: null,
                visibilityMaskParent: null,
                backdropTextureScale: Vector2.one,
                backdropTextureOffset: Vector2.zero,
                flipBackdropTextureX: false,
                flipBackdropTextureY: false,
                globalBackdropTint: RoomBackdropSettingsLookup.TintSettings.Default,
                backdropSharpenStrength: 1f,
                globalDecorationTint: RoomBackdropSettingsLookup.TintSettings.Default);
        }

        static RoomInstance CreateRoom()
        {
            RoomInstance room = new RoomInstance
            {
                id = "test_room",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                landPosition = new Vector3Int(0, 0, 0)
            };

            room.InitializeTiles();
            return room;
        }

        static BackgroundDecorationInstance CreateDecoration(string decorationId, int tileX, int tileY)
        {
            return new BackgroundDecorationInstance
            {
                decorationId = decorationId,
                tileCoord = new Vector2Int(tileX, tileY)
            };
        }

        /// <summary>An object with the AS3 <c>light</c> attribute set to a truthy value.</summary>
        static ObjectInstance CreateLightObject(string objectId, Vector2 position, string attributeValue = "1")
        {
            return CreateObject(objectId, position, attributeValue);
        }

        static ObjectInstance CreateObject(string objectId, Vector2 position, string light = null)
        {
            ObjectInstance obj = new ObjectInstance
            {
                objectId = objectId,
                objectType = "box",
                definitionId = objectId,
                position = position,
                isActive = true
            };

            if (light != null)
            {
                obj.attributes.Add(new MapObjectAttributeData { key = "light", value = light });
            }

            return obj;
        }
    }
}
