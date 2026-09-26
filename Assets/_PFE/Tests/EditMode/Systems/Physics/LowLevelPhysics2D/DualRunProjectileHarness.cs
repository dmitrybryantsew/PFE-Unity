using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevelPhysics2D;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Physics.LowLevelPhysics2D
{
    /// <summary>
    /// Stage B1: run a projectile's trajectory against **both** collision backends and capture every
    /// divergence.
    ///
    /// <para><b>The two sides.</b> <i>Classic</i> is <see cref="UnifiedTileQueryService"/> — the
    /// shipped <c>ITileQueryService</c>, untouched, which is what makes it a legitimate "old" side.
    /// <c>LLP2D_IMPLEMENTATION_GUIDE.md</c> §4.3 is explicit that the previous harness
    /// (<c>GridTileQuery</c>) was worthless because its old side delegated to already-reconciled code.
    /// <i>Chain</i> is the static chain mirror built by <see cref="PhysicsWorldService"/> —
    /// <c>Stage B0</c>'s geometry, which until now had no consumer at all.</para>
    ///
    /// <para><b>Why one trajectory, not two simulations.</b> The chain side has no consumer yet, so
    /// there is no second simulation to run. Integrating once and asking both backends the same
    /// question isolates the thing under test — the <i>collision predicate</i> — from the integrator,
    /// which is already pinned by <c>ProjectilePhysicsMathTests</c>. Two simulations would confound
    /// the two.</para>
    ///
    /// <para><b>Why first contact, not every tick.</b> The two sides describe the same tiles
    /// differently: classic tests a full 40×40 tile <i>cell</i>, the chain mirror a zero-thickness
    /// <i>surface</i> on the cell boundary. Deep inside a thick block those must disagree — a cell
    /// has interior, a surface does not. A projectile never gets there, because it stops at first
    /// contact, and at first contact both sides are looking at the same entry face. So first contact
    /// is the quantity that is comparable, and interior disagreement is a known representational
    /// difference rather than a bug.</para>
    ///
    /// <para><b>Coordinate convention.</b> The projectile <c>position</c> is its <b>centre</b> in
    /// room-local pixels. Room-local and world pixels coincide only when the room sits at the origin
    /// with no border; <see cref="RequireOriginAtZero"/> asserts that precondition rather than
    /// silently assuming it.</para>
    /// </summary>
    public static class DualRunProjectileHarness
    {
        /// <summary>No contact was ever reported by this backend.</summary>
        public const int NoContact = -1;

        /// <summary>
        /// One projectile. The trajectory model is deliberately the same on both sides — see the type
        /// remarks. Velocities are AS3 px per 30 Hz frame, because that is what AS3 authors.
        /// </summary>
        public readonly struct Scenario
        {
            public Scenario(
                string name,
                Vector2 startPx,
                Vector2 sizePx,
                Vector2 velocityPxPerFrame,
                float gravityScale,
                float accelPxPerFrame2,
                int flame,
                int ticks)
            {
                Name = name;
                StartPx = startPx;
                SizePx = sizePx;
                VelocityPxPerFrame = velocityPxPerFrame;
                GravityScale = gravityScale;
                AccelPxPerFrame2 = accelPxPerFrame2;
                Flame = flame;
                Ticks = ticks;
            }

            public string Name { get; }
            public Vector2 StartPx { get; }
            public Vector2 SizePx { get; }

            /// <summary>Initial speed in px/frame; converts with <c>VelocityScale</c>, not the accel one.</summary>
            public Vector2 VelocityPxPerFrame { get; }

            /// <summary>AS3 <c>phis.@grav</c> — a multiplier on <c>World.ddy</c>.</summary>
            public float GravityScale { get; }

            /// <summary>AS3 <c>phis.@accel</c>, px/frame². Passed raw; conversion happens in the math.</summary>
            public float AccelPxPerFrame2 { get; }

            /// <summary>AS3 <c>phis.@flame</c>: 0 none, 1 strong lift, 2 weak lift.</summary>
            public int Flame { get; }

            public int Ticks { get; }
        }

        /// <summary>Where one backend says the projectile first touched something.</summary>
        public readonly struct Contact
        {
            public Contact(int tick, Vector2 centrePx)
            {
                Tick = tick;
                CentrePx = centrePx;
            }

            /// <summary>First tick reporting contact, or <see cref="NoContact"/>.</summary>
            public int Tick { get; }

            /// <summary>Projectile centre in px at that tick; meaningless when <see cref="Tick"/> is -1.</summary>
            public Vector2 CentrePx { get; }

            public bool Touched => Tick != NoContact;
        }

        /// <summary>The full result of dual-running one scenario.</summary>
        public readonly struct Result
        {
            public Result(Scenario scenario, Contact classic, Contact chain)
            {
                Scenario = scenario;
                Classic = classic;
                Chain = chain;
            }

            public Scenario Scenario { get; }
            public Contact Classic { get; }
            public Contact Chain { get; }

            /// <summary>
            /// True when the two backends disagree about whether the projectile touched anything, or
            /// about which tick it first did.
            /// </summary>
            public bool IsDivergent => Classic.Tick != Chain.Tick;

            /// <summary>
            /// Tick distance between the two first contacts, or -1 when exactly one side touched —
            /// a "did it touch at all" disagreement, which is a different and more serious class.
            /// </summary>
            public int TickDelta => Classic.Touched && Chain.Touched
                ? Mathf.Abs(Classic.Tick - Chain.Tick)
                : -1;
        }

        /// <summary>
        /// The room must sit at the origin with no border, or room-local pixels are not world pixels
        /// and every query below would be offset. Asserted rather than assumed: a silent offset would
        /// look exactly like a divergence.
        /// </summary>
        public static void RequireOriginAtZero(RoomInstance room)
        {
            if (room.landPosition.x != 0 || room.landPosition.y != 0 || room.borderOffset != 0)
            {
                throw new System.InvalidOperationException(
                    $"This harness queries in room-local pixels and treats them as world pixels, so the " +
                    $"room must be at the origin with no border. Got landPosition={room.landPosition}, " +
                    $"borderOffset={room.borderOffset}.");
            }
        }

        /// <summary>
        /// Runs one scenario against both backends.
        ///
        /// <para>The trajectory is integrated once with <see cref="ProjectilePhysicsMath"/> — the same
        /// pure arithmetic production uses — and the <b>same</b> AABB is offered to each backend on
        /// every tick. Nothing is stopped on contact: a stopped trajectory could not tell you that the
        /// other backend would have touched a tick earlier.</para>
        /// </summary>
        public static Result Run(
            RoomInstance room,
            PhysicsWorldService chainService,
            ITileQueryService classicQuery,
            in Scenario scenario)
        {
            float dt = 1f / PFE.Core.SimClock.CanonicalTicksPerSecond;

            Vector2 direction = Vector2.right;
            Vector2 acceleration = ProjectilePhysicsMath.BulletAcceleration(
                direction, scenario.GravityScale, scenario.AccelPxPerFrame2, scenario.Flame);

            // px/frame -> units/s uses the VELOCITY idiom (x0.3). Using the acceleration idiom here
            // would be a 30x error and is the exact confusion ProjectilePhysicsMath exists to stop.
            Vector2 velocity = scenario.VelocityPxPerFrame * ProjectilePhysicsMath.VelocityScale;
            Vector2 centre = scenario.StartPx * Llp2d.PixelToUnit;

            Contact classic = new Contact(NoContact, Vector2.zero);
            Contact chain = new Contact(NoContact, Vector2.zero);

            PhysicsQuery.QueryFilter filter = new PhysicsQuery.QueryFilter();

            for (int tick = 0; tick < scenario.Ticks; tick++)
            {
                velocity += acceleration * dt;
                centre += velocity * dt;

                Vector2 centrePx = centre / Llp2d.PixelToUnit;

                if (!classic.Touched && ClassicOverlaps(classicQuery, centrePx, scenario.SizePx))
                {
                    classic = new Contact(tick, centrePx);
                }

                if (!chain.Touched && ChainOverlaps(chainService, centre, scenario.SizePx, filter))
                {
                    chain = new Contact(tick, centrePx);
                }

                // Both answered; nothing further can change either answer.
                if (classic.Touched && chain.Touched)
                {
                    break;
                }
            }

            return new Result(scenario, classic, chain);
        }

        /// <summary>
        /// Classic side: a full tile-cell AABB test, in world pixels. <c>TileQueryOptions.Default</c>
        /// deliberately matches the chain builder's own solidity predicate, which counts platforms and
        /// slopes as surfaces — so neither side is given a handicap the other does not have.
        /// </summary>
        private static bool ClassicOverlaps(ITileQueryService query, Vector2 centrePx, Vector2 sizePx)
        {
            var bounds = new Rect(
                centrePx.x - sizePx.x * 0.5f,
                centrePx.y - sizePx.y * 0.5f,
                sizePx.x,
                sizePx.y);

            return query.CheckCollision(bounds, TileQueryOptions.Default);
        }

        /// <summary>
        /// Chain side: the projectile's box, positioned in world space, tested against the chain
        /// mirror.
        ///
        /// <para><c>TestOverlapGeometry</c> is used rather than <c>TestOverlapAABB</c> on purpose. The
        /// AABB variant tests against <i>shape AABBs</i> — the docs say it "may not result in an exact
        /// overlap of the shape itself" — and a chain segment is a zero-thickness line, so its AABB is
        /// either degenerate or far fatter than the surface it represents. That would manufacture
        /// divergences that are artefacts of the query, not of the geometry.</para>
        ///
        /// <para><c>new PhysicsQuery.QueryFilter()</c>, not <c>default</c>: the parameterless ctor is
        /// documented as producing the filter that "hits everything", whereas a default-initialised
        /// filter has empty category masks and would match nothing — a silent zero-divergence pass
        /// that looks like success.</para>
        /// </summary>
        private static bool ChainOverlaps(
            PhysicsWorldService service, Vector2 centreUnits, Vector2 sizePx, PhysicsQuery.QueryFilter filter)
        {
            Vector2 sizeUnits = sizePx * Llp2d.PixelToUnit;

            PolygonGeometry box = PolygonGeometry.CreateBox(
                sizeUnits,
                0f,
                new PhysicsTransform(centreUnits, PhysicsRotate.identity),
                false);

            return service.World.TestOverlapGeometry(box, filter);
        }

        /// <summary>
        /// A per-tick trace of both predicates, for diagnosing a divergence rather than merely
        /// reporting it. Kept separate from <see cref="Run"/> so the fast path stays allocation-free.
        /// </summary>
        public static List<string> Trace(
            RoomInstance room,
            PhysicsWorldService chainService,
            ITileQueryService classicQuery,
            in Scenario scenario)
        {
            float dt = 1f / PFE.Core.SimClock.CanonicalTicksPerSecond;

            Vector2 acceleration = ProjectilePhysicsMath.BulletAcceleration(
                Vector2.right, scenario.GravityScale, scenario.AccelPxPerFrame2, scenario.Flame);
            Vector2 velocity = scenario.VelocityPxPerFrame * ProjectilePhysicsMath.VelocityScale;
            Vector2 centre = scenario.StartPx * Llp2d.PixelToUnit;

            var lines = new List<string>();
            PhysicsQuery.QueryFilter filter = new PhysicsQuery.QueryFilter();

            for (int tick = 0; tick < scenario.Ticks; tick++)
            {
                velocity += acceleration * dt;
                centre += velocity * dt;

                Vector2 centrePx = centre / Llp2d.PixelToUnit;
                bool classic = ClassicOverlaps(classicQuery, centrePx, scenario.SizePx);
                bool chain = ChainOverlaps(chainService, centre, scenario.SizePx, filter);

                lines.Add(
                    $"    tick {tick,4}  centre=({centrePx.x,8:F2},{centrePx.y,8:F2})px  " +
                    $"classic={(classic ? "HIT " : "----")}  chain={(chain ? "HIT " : "----")}");

                if (classic && chain)
                {
                    break;
                }
            }

            return lines;
        }
    }
}
