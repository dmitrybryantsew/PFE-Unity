using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.LowLevelPhysics2D;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Tests.EditMode.Systems.Map.TileCollision;

namespace PFE.Tests.EditMode.Systems.Physics.LowLevelPhysics2D
{
    /// <summary>
    /// Stage B1: dual-run projectiles against the classic tile query and the chain mirror, and
    /// capture the divergences.
    ///
    /// <para><b>The evidence this exists to produce.</b> <c>LLP2D_IMPLEMENTATION_GUIDE.md</c> §6:
    /// "Dual-run projectiles against both backends. The divergence log must be enabled and actually
    /// captured — <c>tileQueryLogDivergence</c> shipped defaulting to <c>0</c> and no one ever turned
    /// it on; do not repeat that." So this is not a smoke test: it reports what it found, and the
    /// report is the deliverable.</para>
    ///
    /// <para><b>Why the classic side is legitimate.</b> §4.3 warns that the previous harness compared
    /// two things that both delegated to already-reconciled code, so it "can no longer reproduce
    /// pre-P2 behaviour". Here the old side is <see cref="UnifiedTileQueryService"/> — shipped,
    /// untouched, and the authority production actually runs.</para>
    /// </summary>
    [TestFixture]
    public sealed class Llp2dStageB1DualRunTests
    {
        // 16x5. Row 0 is the TOP row (SyntheticRoomBuilder maps row r to y = height-1-r), so:
        //   row 0 -> y=4  ceiling, px y in [160,200]
        //   row 2 -> y=2  a four-tile shelf at columns 5..8, px x in [200,360], y in [80,120]
        //   row 4 -> y=0  floor, px y in [0,40]
        // Free space is therefore x in [40,600], y in [40,160], minus the shelf box.
        private static readonly string[] RoomWithShelf =
        {
            "################",
            "#..............#",
            "#....####......#",
            "#..............#",
            "################"
        };

        /// <summary>An 8x8 px projectile, the size class the AS3 bullets use.</summary>
        private static readonly Vector2 ProjectileSize = new Vector2(8f, 8f);

        // ── Precondition ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The chain world must be queryable <b>without ever being stepped</b>. Stage A built chains
        /// and only ever read <c>counters</c>; nothing has ever issued a query against the mirror, so
        /// "does the broad-phase know about these shapes before the first <c>Simulate</c>?" is
        /// genuinely unverified. If it does not, every scenario below would report
        /// "chain never touched" and look like a geometry bug rather than a query-ordering one — so
        /// it gets its own test, with its own message.
        /// </summary>
        [Test]
        public void ChainMirror_AnswersQueries_BeforeTheFirstStep()
        {
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(RoomWithShelf);
            DualRunProjectileHarness.RequireOriginAtZero(room);

            using var service = new PhysicsWorldService();
            service.BuildRoomGeometry(room);

            Assert.Greater(service.World.counters.shapeCount, 0,
                "Precondition: the room produced chain shapes at all.");

            var filter = new PhysicsQuery.QueryFilter();

            // Straddling the floor's top surface (y = 40 px = 0.4 units): the chain mirror represents
            // that surface as a zero-thickness line, so a box centred on it must overlap.
            Assert.IsTrue(QueryChain(service, new Vector2(340f, 40f), ProjectileSize, filter),
                "A box straddling the floor surface did not overlap any chain. Either the broad-phase " +
                "needs a step before queries, or the chain mirror is not where the tiles are.");

            // Empty air in the middle of the room. Guards the opposite failure: a filter or a
            // transform bug that makes every query report a hit.
            Assert.IsFalse(QueryChain(service, new Vector2(300f, 140f), ProjectileSize, filter),
                "A box in open air reported a chain overlap, so the query is not discriminating.");
        }

        /// <summary>
        /// Documents the representational difference rather than leaving it to be discovered as a
        /// "divergence": a box strictly <i>inside</i> a solid tile cell overlaps a classic tile but
        /// no chain, because a cell has interior and a surface does not. This is why the harness
        /// compares <b>first contact</b> and not per-tick overlap.
        /// </summary>
        [Test]
        public void InsideASolidCell_TheTwoBackendsMustDisagree()
        {
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(RoomWithShelf);

            using var service = new PhysicsWorldService();
            service.BuildRoomGeometry(room);

            var classic = new UnifiedTileQueryService(room);
            var filter = new PhysicsQuery.QueryFilter();

            // Deep inside the floor row (y in [0,40] px), clear of both its surfaces.
            var insideFloorPx = new Vector2(340f, 20f);

            var bounds = new Rect(
                insideFloorPx.x - ProjectileSize.x * 0.5f,
                insideFloorPx.y - ProjectileSize.y * 0.5f,
                ProjectileSize.x,
                ProjectileSize.y);

            Assert.IsTrue(classic.CheckCollision(bounds, TileQueryOptions.Default),
                "A box inside a solid tile must collide classically.");

            Assert.IsFalse(QueryChain(service, insideFloorPx, ProjectileSize, filter),
                "A box inside a solid tile must NOT overlap a chain — the chain describes the cell's " +
                "surface, not its interior. If this ever becomes true the two representations have " +
                "converged and the first-contact comparison below needs revisiting.");
        }

        // ── The dual-run itself ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Every scenario that <b>should</b> hit something: both backends must agree that it did, and
        /// must agree on the tick to within one. One tick is the honest tolerance — the classic side
        /// tests a closed cell while the chain side tests a boundary line, so a projectile whose edge
        /// lands exactly on a surface can be classified a tick apart. Anything larger is a real
        /// disagreement about where geometry is.
        /// </summary>
        [TestCaseSource(nameof(ContactScenarios))]
        public void ContactScenario_BothBackendsAgreeOnFirstContact(
            string name, Vector2 startPx, Vector2 velocityPxPerFrame, float gravityScale, int ticks)
        {
            DualRunProjectileHarness.Result result = RunScenario(
                name, startPx, velocityPxPerFrame, gravityScale, ticks);

            Assert.IsTrue(result.Classic.Touched,
                $"[{name}] the CLASSIC backend never reported contact, so the scenario is mis-aimed " +
                "and proves nothing. Trace:\n" + Describe(name, startPx, velocityPxPerFrame, gravityScale, ticks));

            Assert.IsTrue(result.Chain.Touched,
                $"[{name}] the CHAIN backend never reported contact where the classic backend did " +
                $"(classic tick {result.Classic.Tick}, chain never). Trace:\n" +
                Describe(name, startPx, velocityPxPerFrame, gravityScale, ticks));

            Assert.LessOrEqual(result.TickDelta, 1,
                $"[{name}] the backends disagree on first contact by {result.TickDelta} ticks " +
                $"(classic {result.Classic.Tick} at {result.Classic.CentrePx}, " +
                $"chain {result.Chain.Tick} at {result.Chain.CentrePx}). More than one tick apart is a " +
                "disagreement about where geometry is, not a boundary-rounding artefact. Trace:\n" +
                Describe(name, startPx, velocityPxPerFrame, gravityScale, ticks));
        }

        /// <summary>
        /// The control, and the one scenario a false-positive bug would fail: a projectile that
        /// travels through open air must be reported as touching nothing by <b>both</b> backends. It
        /// is what makes the agreements above meaningful.
        /// </summary>
        [Test]
        public void FreeSpaceScenario_NeitherBackendReportsContact()
        {
            DualRunProjectileHarness.Result result = RunScenario(
                "free_space", new Vector2(60f, 140f), new Vector2(2f, 0f), gravityScale: 0f, ticks: 10);

            Assert.IsFalse(result.Classic.Touched,
                $"[free_space] the classic backend reported contact in open air at tick " +
                $"{result.Classic.Tick} ({result.Classic.CentrePx}).");

            Assert.IsFalse(result.Chain.Touched,
                $"[free_space] the chain backend reported contact in open air at tick " +
                $"{result.Chain.Tick} ({result.Chain.CentrePx}). A hit-everything filter or a " +
                "mis-transformed query box would look exactly like this.");

            Assert.IsFalse(result.IsDivergent, "[free_space] the backends disagreed about touching nothing.");
        }

        /// <summary>
        /// Runs the whole set and prints the divergence report. This test asserts nothing beyond the
        /// harness having run — it exists so the captured evidence is in the log, which §6 says is the
        /// part that was skipped last time.
        /// </summary>
        [Test]
        public void AllScenarios_ReportTheCapturedDivergences()
        {
            var sb = new StringBuilder();
            sb.Append("[LLP2D B1] dual-run projectiles: classic UnifiedTileQueryService vs chain mirror\n");
            sb.Append("  room ").Append(RoomWithShelf[0].Length).Append('x').Append(RoomWithShelf.Length)
              .Append(" tiles; projectile ").Append(ProjectileSize.x).Append('x').Append(ProjectileSize.y)
              .Append(" px\n");

            int scenarios = 0;
            int divergent = 0;

            foreach (ScenarioSpec spec in AllSpecs())
            {
                DualRunProjectileHarness.Result r = RunScenario(
                    spec.Name, spec.StartPx, spec.Velocity, spec.GravityScale, spec.Ticks);

                scenarios++;
                if (r.IsDivergent)
                {
                    divergent++;
                }

                sb.Append("  ").Append(spec.Name.PadRight(22))
                  .Append(" classic=").Append(Format(r.Classic))
                  .Append("  chain=").Append(Format(r.Chain))
                  .Append(r.IsDivergent ? "   <-- DIVERGENT" : string.Empty)
                  .Append('\n');
            }

            sb.Append("  ").Append(scenarios).Append(" scenarios, ").Append(divergent)
              .Append(" divergent. Agreement is judged on FIRST CONTACT only; interior overlap is " +
                      "expected to differ (a cell has interior, a surface does not).");

            Debug.Log(sb.ToString());

            Assert.Greater(scenarios, 0, "No scenarios ran, so nothing was compared.");
        }

        // ── Scenario plumbing ────────────────────────────────────────────────────────────────

        private readonly struct ScenarioSpec
        {
            public ScenarioSpec(string name, Vector2 startPx, Vector2 velocity, float gravityScale, int ticks)
            {
                Name = name;
                StartPx = startPx;
                Velocity = velocity;
                GravityScale = gravityScale;
                Ticks = ticks;
            }

            public string Name { get; }
            public Vector2 StartPx { get; }
            public Vector2 Velocity { get; }
            public float GravityScale { get; }
            public int Ticks { get; }
        }

        /// <summary>
        /// Scenarios that must contact. Speeds are px per 30 Hz frame — AS3's unit.
        ///
        /// <para><b>Why constant velocity, and why so slow.</b> The chain mirror is a
        /// <i>zero-thickness</i> surface, so the projectile overlaps it only while its centre is
        /// within one projectile width of it — an 8 px window here. A step larger than that can jump
        /// the window entirely and the chain backend would report "never touched". That is
        /// <b>tunnelling</b>, a different question (Stage A's Q6, answered by CCD), and it would be
        /// misread as a geometry divergence. 4 px/frame puts at least two samples inside every window.
        /// Gravity is left out for the same reason: it would make the speed at contact an
        /// uncontrolled quantity, and a fast contact is exactly the failure mode above. The classic
        /// side has no such window — a cell test stays true once entered — so slowing down costs the
        /// comparison nothing.</para>
        /// </summary>
        private static IEnumerable<ScenarioSpec> ContactSpecs()
        {
            // Falls 100 -> 40 px onto the floor, clear of the shelf in x (450 px is tile 11; the
            // shelf occupies tiles 5..8).
            yield return new ScenarioSpec("fall_onto_floor", new Vector2(450f, 100f), new Vector2(0f, -4f), 0f, 30);

            // Falls 150 -> 120 px onto the shelf top, directly above tiles 5..8.
            yield return new ScenarioSpec("fall_onto_shelf", new Vector2(280f, 150f), new Vector2(0f, -4f), 0f, 30);

            // Rises 50 -> 80 px into the shelf's underside.
            yield return new ScenarioSpec("rise_into_shelf_underside", new Vector2(280f, 50f), new Vector2(0f, 4f), 0f, 20);

            // Rises 60 -> 160 px into the ceiling's underside.
            yield return new ScenarioSpec("rise_into_ceiling", new Vector2(450f, 60f), new Vector2(0f, 4f), 0f, 40);

            // Travels 500 -> 600 px into the right wall's inner face.
            yield return new ScenarioSpec("travel_into_right_wall", new Vector2(500f, 140f), new Vector2(4f, 0f), 0f, 40);
        }

        private static IEnumerable<ScenarioSpec> AllSpecs()
        {
            foreach (ScenarioSpec spec in ContactSpecs())
            {
                yield return spec;
            }

            // The control.
            yield return new ScenarioSpec("free_space", new Vector2(60f, 140f), new Vector2(2f, 0f), 0f, 10);
        }

        private static IEnumerable<TestCaseData> ContactScenarios()
        {
            foreach (ScenarioSpec spec in ContactSpecs())
            {
                yield return new TestCaseData(
                        spec.Name, spec.StartPx, spec.Velocity, spec.GravityScale, spec.Ticks)
                    .SetName($"Contact_{spec.Name}");
            }
        }

        private static DualRunProjectileHarness.Result RunScenario(
            string name, Vector2 startPx, Vector2 velocity, float gravityScale, int ticks)
        {
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(RoomWithShelf);
            DualRunProjectileHarness.RequireOriginAtZero(room);

            using var service = new PhysicsWorldService();
            service.BuildRoomGeometry(room);

            var classic = new UnifiedTileQueryService(room);

            var scenario = new DualRunProjectileHarness.Scenario(
                name, startPx, ProjectileSize, velocity,
                gravityScale, accelPxPerFrame2: 0f, flame: 0, ticks: ticks);

            return DualRunProjectileHarness.Run(room, service, classic, scenario);
        }

        private static string Describe(
            string name, Vector2 startPx, Vector2 velocity, float gravityScale, int ticks)
        {
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(RoomWithShelf);

            using var service = new PhysicsWorldService();
            service.BuildRoomGeometry(room);

            var classic = new UnifiedTileQueryService(room);

            var scenario = new DualRunProjectileHarness.Scenario(
                name, startPx, ProjectileSize, velocity,
                gravityScale, accelPxPerFrame2: 0f, flame: 0, ticks: ticks);

            var sb = new StringBuilder();
            foreach (string line in DualRunProjectileHarness.Trace(room, service, classic, scenario))
            {
                sb.Append(line).Append('\n');
            }

            return sb.ToString();
        }

        private static string Format(DualRunProjectileHarness.Contact contact)
        {
            return contact.Touched
                ? $"tick {contact.Tick,3} ({contact.CentrePx.x:F0},{contact.CentrePx.y:F0})px"
                : "never touched     ";
        }

        /// <summary>Same box the harness offers the chain backend, exposed for the precondition tests.</summary>
        private static bool QueryChain(
            PhysicsWorldService service, Vector2 centrePx, Vector2 sizePx, PhysicsQuery.QueryFilter filter)
        {
            Vector2 sizeUnits = sizePx * Llp2d.PixelToUnit;
            PolygonGeometry box = PolygonGeometry.CreateBox(
                sizeUnits, 0f,
                new PhysicsTransform(centrePx * Llp2d.PixelToUnit, PhysicsRotate.identity),
                false);

            return service.World.TestOverlapGeometry(box, filter);
        }
    }
}
