using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.LowLevelPhysics2D;
using PFE.Core;
using PFE.Entities.Weapons;
using PFE.Systems.Map;
using PFE.Systems.Map.Streaming;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins the px↔unit boundary between <see cref="Projectile"/> and the Stage C sweep seam.
    ///
    /// <para><b>Why this fixture exists.</b> The seam's own tests
    /// (<c>Llp2dStageCSweepTests</c>) call <c>IPhysicsWorldService.TrySweepTiles</c> <i>directly</i>
    /// and therefore cannot see what the caller hands it — by construction they agree with the seam
    /// about units. The first play-test with <c>ProjectilesUseLowLevelPhysics</c> on found the gap:
    /// <see cref="Projectile.SimTick"/> passed its position in <b>Unity units</b> to a seam that takes
    /// <b>world pixels</b>, so every tile query ran at 1/100 scale near the world origin, found no
    /// geometry, and reported "nothing in the way". Projectiles flew through every wall. No test
    /// failed, because every test agreed with the seam.</para>
    ///
    /// <para>So this fixture asserts the one thing nothing else can: <i>the consumer converts</i>.
    /// It substitutes a recording stub for the seam, so it fails on the arguments rather than on a
    /// physics outcome — which means it is exact, fast, and needs no world, no prefab and no pool.</para>
    ///
    /// <para><b>It also corrects a claim.</b> <c>ProjectilePhysicsMathTests</c> says exercising the
    /// call site "would require PlayMode". It does not: <c>SimTick</c> is public, reads no
    /// <c>Time</c>/<c>Transform</c>/<c>Input</c>, and gates only on <c>_isInitialized</c>, so
    /// EditMode plus reflection over the injected fields is enough.</para>
    /// </summary>
    [TestFixture]
    public class ProjectileSweepUnitBoundaryTests
    {
        /// <summary>
        /// Values chosen so units and pixels differ by a recognisable factor: the sim position is
        /// (4.5, 4.0) units = (450, 400) px, and one canonical 30 Hz tick of a 300 units/s velocity
        /// is 10 units = 1000 px. A units/pixels mix-up cannot land on these by accident.
        /// </summary>
        private static readonly Vector2 StartUnits   = new Vector2(4.5f, 4f);
        private static readonly Vector2 Velocity     = new Vector2(300f, 0f);   // units/s
        private static readonly Vector2 StartPx      = new Vector2(450f, 400f);
        private static readonly Vector2 DeltaPx      = new Vector2(1000f, 0f);
        private static readonly Vector2 ExpectedEndUnits = new Vector2(14.5f, 4f);

        private GameObject  _go;
        private Projectile  _projectile;
        private RecordingPhysicsWorld _world;

        [SetUp]
        public void SetUp()
        {
            _go         = new GameObject("ProjectileSweepUnitBoundary");
            _projectile = _go.AddComponent<Projectile>();
            _world      = new RecordingPhysicsWorld();

            // Awake/OnEnable do not run for AddComponent in EditMode, so nothing has initialised
            // this instance. Build the minimum state SimTick reads, through the same fields the
            // container would inject.
            SetField("_simClock",      new SimClock());     // canonical 30 Hz => SimDt = 1/30
            SetField("_physicsWorld",  _world);
            SetField("_isInitialized", true);
            SetField("_lifetimeTimer", 30f);
            SetField("_simPosition",   StartUnits);
            SetField("_velocity",      Velocity);
            SetField("_ddx",           0f);
            SetField("_ddy",           0f);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        /// <summary>
        /// The regression that this fixture was written for: the seam must be handed world pixels.
        /// If this fails with the start position reading (4.5, 4.0) rather than (450, 400), the
        /// consumer is passing units and every tile query will miss.
        /// </summary>
        [Test]
        public void SimTick_HandsTheSweepSeamWorldPixels_NotSimUnits()
        {
            _projectile.SimTick(0);

            Assert.AreEqual(1, _world.SweepCalls,
                "The flip must query the seam exactly once per tick.");

            AssertVector(_world.LastFromPx, StartPx,
                "TrySweepTiles takes world PIXELS. Handing it the sim's units puts every query at " +
                "1/100 scale near the world origin, where no room has geometry, so every projectile " +
                "passes through every wall — the play-test bug this test exists to prevent.");

            AssertVector(_world.LastDeltaPx, DeltaPx,
                "The step delta is also in world pixels: 300 units/s x (1/30) s = 10 units = 1000 px.");
        }

        /// <summary>
        /// The other half of the boundary, and the half a wrong fix is most likely to break: the
        /// size and facing the seam receives are the prefab-shaped pixel size and a unit vector.
        /// </summary>
        [Test]
        public void SimTick_PassesThePixelSizedShapeAndItsFacing()
        {
            _projectile.SimTick(0);

            AssertVector(_world.LastSizePx, new Vector2(93f, 6f),
                "The shape size is already in pixels (the prefab collider is 0.93 x 0.06 units). " +
                "Scaling it by UnitToPixel a second time would sweep a 9300 px needle.");

            AssertVector(_world.LastFacing, Vector2.right,
                "Facing is the normalised travel direction, so the needle's leading tip leads.");
        }

        /// <summary>
        /// The conversion must be applied on the way out and on the way back, and nowhere else. The
        /// sim integrates in units because <c>_velocity</c> is units/s and the legacy path hands it
        /// straight to <c>Rigidbody2D.linearVelocity</c> — so an uncontacted step advances in units.
        /// </summary>
        [Test]
        public void SimTick_AdvancesTheSimInUnits_NotPixels()
        {
            _projectile.SimTick(0);

            AssertVector(SimPosition, ExpectedEndUnits,
                "The sim's own position stays in Unity units. Advancing it by the pixel delta would " +
                "move the projectile 100x too far.");
        }

        /// <summary>
        /// A contact comes back in world pixels and must be converted before the sim adopts it —
        /// otherwise the projectile would jump 100x away at the moment it hits a wall.
        /// </summary>
        [Test]
        public void SimTick_MapsTheContactBackToUnits_BeforeAdoptingIt()
        {
            // FlipActive defers the pool release to LateUpdate instead of disabling the GameObject
            // inside the tick, which is what lets this test call SimTick directly and safely.
            SetField("_registeredOnSimLoop", true);
            _world.ContactPointPx = new Vector2(450f, 40f);
            _world.ContactNormal  = Vector2.up;
            _world.ReportsContact = true;

            _projectile.SimTick(0);

            AssertVector(SimPosition, new Vector2(4.5f, 0.4f),
                "The contact point is world pixels and the sim's position is units, so the seam's " +
                "answer is scaled by PixelToUnit before it is adopted.");
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────

        private Vector2 SimPosition => (Vector2)GetField("_simPosition");

        private object GetField(string name)
        {
            FieldInfo field = typeof(Projectile).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(field,
                $"Projectile has no private instance field '{name}'. This fixture reflects over " +
                "runtime state, so a rename here is a test change, not a production change.");

            return field.GetValue(_projectile);
        }

        private void SetField(string name, object value)
        {
            FieldInfo field = typeof(Projectile).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(field,
                $"Projectile has no private instance field '{name}'. This fixture reflects over " +
                "runtime state, so a rename here is a test change, not a production change.");

            field.SetValue(_projectile, value);
        }

        private static void AssertVector(Vector2 actual, Vector2 expected, string why)
        {
            // Component-wise rather than Assert.AreEqual(Vector2, Vector2): Unity's Vector2 equality
            // is an epsilon compare, and a failure message naming the axis is worth more than one
            // naming the pair.
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(1e-3f),
                why + $" (x: got {actual.x}, want {expected.x})");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(1e-3f),
                why + $" (y: got {actual.y}, want {expected.y})");
        }

        /// <summary>
        /// A stand-in for <see cref="IPhysicsWorldService"/> that records what the sweep was asked
        /// and answers with a scripted result.
        ///
        /// <para>Recording the <i>arguments</i> is the whole point: the seam's unit convention is a
        /// contract between two classes, and only the caller's half of it is invisible to the seam's
        /// own tests. Answering "no contact" by default keeps the assertion on the argument boundary
        /// rather than on the impact path.</para>
        /// </summary>
        private sealed class RecordingPhysicsWorld : IPhysicsWorldService
        {
            public Vector2 LastFromPx  { get; private set; }
            public Vector2 LastDeltaPx { get; private set; }
            public Vector2 LastSizePx  { get; private set; }
            public Vector2 LastFacing  { get; private set; }
            public int     SweepCalls  { get; private set; }

            /// <summary>Scripted answer. Left false so the default path exercises no impact logic.</summary>
            public bool    ReportsContact { get; set; }
            public Vector2 ContactPointPx { get; set; }
            public Vector2 ContactNormal  { get; set; }

            // The seam is the only member under test; the rest exist to satisfy the interface.
            public PhysicsWorld World       => default;
            public bool         IsWorldValid => true;
            public int          TickOrder    => SimTickOrder.Projectiles;

            public void SimTick(int tickIndex) { }

            public void SubscribeToRoomEvents(RoomStreamingManager streamingManager) { }
            public void BuildRoomGeometry(RoomInstance room) { }
            public void DestroyRoomGeometry(RoomInstance room) { }
            public void RebuildRegion(RoomInstance room, RectInt tileRegion) { }

            public bool TrySweepTiles(Vector2 fromPx, Vector2 deltaPx, Vector2 sizePx, Vector2 facing,
                                      out Vector2 contactPointPx, out Vector2 contactNormal)
            {
                LastFromPx  = fromPx;
                LastDeltaPx = deltaPx;
                LastSizePx  = sizePx;
                LastFacing  = facing;
                SweepCalls++;

                contactPointPx = ContactPointPx;
                contactNormal  = ContactNormal;
                return ReportsContact;
            }

            /// <summary>
            /// Not exercised here: this fixture is about the sweep's unit boundary. Returns false —
            /// "outside every mirrored room" — which is what a caller would see with no room active,
            /// and is the answer that makes a consumer stay on its legacy path rather than act on a
            /// null seam.
            /// </summary>
            public bool TryGetRoomTileQueryAt(Vector2 worldPx, out RoomTileQuery query)
            {
                query = default;
                return false;
            }
        }
    }
}
