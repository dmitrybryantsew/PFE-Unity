using System;
using UnityEngine;
using UnityEngine.LowLevelPhysics2D;
using PFE.Systems.Map.TileQuery;

namespace PFE.Tests.EditMode.Systems.Physics.LowLevelPhysics2D
{
    /// <summary>
    /// Shared helpers for the LowLevelPhysics2D Stage A spike.
    ///
    /// <para>THROWAWAY. Lives under <c>Tests/</c> and has zero production consumers — see
    /// <c>docs/Roadmap/LLP2D_IMPLEMENTATION_GUIDE.md</c> §5. Nothing in <c>Assets/_PFE/Systems/</c>
    /// or <c>Assets/_PFE/Core/</c> may reference this file.</para>
    ///
    /// <para>All API shapes here were read off the installed editor's shipped XML docs
    /// (<c>UnityEngine.Physics2DModule.xml</c>, 6000.3.10f1), not from memory.</para>
    /// </summary>
    public static class Llp2d
    {
        /// <summary>
        /// 100 pixels = 1 Unity unit. AS3 <c>World.as</c> is authored in pixels; a physics world is
        /// in Unity units, so every pixel value crosses this boundary exactly once — here.
        /// </summary>
        public const float PixelToUnit = 0.01f;

        /// <summary>
        /// AS3 <c>World.as:46</c> (<c>ddy = 1</c>): gravity is 1 pixel per 30 Hz frame².
        /// <c>1 px/frame² × 30² frame²/s² = 900 px/s²</c>.
        /// </summary>
        public const float GravityPxPerSecondSquared =
            TileQueryConstants.Gravity * SimTicksPerSecond * SimTicksPerSecond;

        /// <summary>
        /// The same constant expressed for a physics world: <c>900 px/s² × 0.01 units/px</c>.
        /// Negative because a Unity world has +Y up and AS3 <c>ddy</c> pulls down.
        /// </summary>
        public const float GravityUnitsPerSecondSquared = GravityPxPerSecondSquared * PixelToUnit;

        /// <summary>AS3 <c>World.fps</c>. The canonical tick rate; see <c>SimClock</c>.</summary>
        public const int SimTicksPerSecond = 30;

        /// <summary>One tile is 40 px. AS3 <c>World.as:36-37</c>.</summary>
        public const float TileSizePx = TileQueryConstants.TileSize;

        /// <summary>One tile expressed in Unity units: 40 px × 0.01.</summary>
        public const float TileSizeUnits = TileSizePx * PixelToUnit;

        /// <summary>
        /// Creates a world that only advances when <see cref="PhysicsWorld.Simulate(float)"/> is
        /// called — the only kind allowed in the Sim layer.
        /// </summary>
        /// <param name="workers">
        /// Kept at 1 for the spike: the API documents no world-level cross-platform determinism
        /// guarantee, and <c>&gt; 1</c> is an order hazard. See guide §5 question 5.
        /// </param>
        public static PhysicsWorld CreateScriptWorld(
            float gravityY = -GravityUnitsPerSecondSquared,
            bool continuousAllowed = true,
            int workers = 1)
        {
            PhysicsWorldDefinition def = PhysicsWorldDefinition.defaultDefinition;

            // NOTE: `simulateType` on the DEFINITION, `simulationType` on the WORLD. The spelling
            // differs between the two types in 6000.3.10f1; the implementation guide's sketch
            // says `simulationType` on the definition, which does not compile.
            def.simulateType = PhysicsWorld.SimulationType.Script;
            def.simulationWorkers = workers;
            def.continuousAllowed = continuousAllowed;
            def.gravity = new Vector2(0f, gravityY);

            PhysicsWorld world = PhysicsWorld.Create(def);
            if (!world.isValid)
            {
                throw new InvalidOperationException("PhysicsWorld.Create returned an invalid world.");
            }

            return world;
        }

        /// <summary>
        /// Releases a world. <c>PhysicsConstants.MaxWorlds</c> caps concurrency, so a spike that
        /// leaks worlds eventually fails for a reason that has nothing to do with the question.
        /// </summary>
        public static void DestroyWorld(PhysicsWorld world)
        {
            if (world.isValid)
            {
                world.Destroy(0);
            }
        }

        public static PhysicsBody CreateBody(
            PhysicsWorld world,
            PhysicsBody.BodyType type,
            Vector2 positionUnits,
            float gravityScale = 1f)
        {
            PhysicsBodyDefinition def = PhysicsBodyDefinition.defaultDefinition;
            def.type = type;
            def.position = positionUnits;
            def.gravityScale = gravityScale;
            return world.CreateBody(def);
        }

        /// <summary>
        /// Creates a box shape.
        ///
        /// <para><b><paramref name="sizeUnits"/> is FULL width/height, not half-extents.</b> The
        /// shipped XML docs do not say which, and it is not Box2D's <c>b2MakeBox(hx, hy)</c>
        /// convention. Measured: a body given <c>(0.5, 0.5)</c> came to rest one quarter of a unit
        /// lower than the half-extents reading predicts, in two independent scenarios, which is
        /// exactly the half-height difference.</para>
        /// </summary>
        public static PhysicsShape CreateBox(
            PhysicsBody body,
            Vector2 sizeUnits,
            float density = 1f)
        {
            PhysicsShapeDefinition def = PhysicsShapeDefinition.defaultDefinition;
            def.density = density;
            PolygonGeometry geom = PolygonGeometry.CreateBox(sizeUnits, 0f, false);
            return body.CreateShape(geom, def);
        }

        public static PhysicsShape CreateCircle(PhysicsBody body, float radiusUnits, float density = 1f)
        {
            PhysicsShapeDefinition def = PhysicsShapeDefinition.defaultDefinition;
            def.density = density;
            CircleGeometry geom = CircleGeometry.Create(radiusUnits);
            return body.CreateShape(geom, def);
        }

        /// <summary>
        /// Builds one static chain from a polyline. A chain is a continuous surface with no mass and
        /// no ghost collisions at the seams — the property <c>TileCollider</c> lacks today because it
        /// emits one <c>BoxCollider2D</c> per tile. See guide §1.2 point 2.
        /// </summary>
        public static PhysicsChain CreateChain(PhysicsBody body, Vector2[] pointsUnits, bool isLoop = false)
        {
            if (pointsUnits == null || pointsUnits.Length < 2)
            {
                throw new ArgumentException("A chain needs at least two points.", nameof(pointsUnits));
            }

            PhysicsChainDefinition def = PhysicsChainDefinition.defaultDefinition;
            def.isLoop = isLoop;

            ChainGeometry geom = new ChainGeometry(new ReadOnlySpan<Vector2>(pointsUnits));
            return body.CreateChain(geom, def);
        }

        /// <summary>
        /// Advances a world by exactly one simulation tick. Registered on a <c>SimLoop</c> as an
        /// <c>ISimTickable</c> so the world cannot advance on wall time.
        /// </summary>
        public sealed class WorldStepper : PFE.Core.ISimTickable
        {
            private readonly PhysicsWorld _world;
            private readonly float _stepSeconds;

            public WorldStepper(PhysicsWorld world, float stepSeconds)
            {
                _world = world;
                _stepSeconds = stepSeconds;
            }

            public int TickOrder => PFE.Core.SimTickOrder.Projectiles;

            public void SimTick(int tickIndex)
            {
                _world.Simulate(_stepSeconds);
            }
        }

        /// <summary>
        /// FNV-1a over body transforms, for the determinism probe. Bit-exact on purpose: a value
        /// hash would hide exactly the low-order drift we are looking for.
        /// </summary>
        public static uint HashBodies(PhysicsBody[] bodies)
        {
            const uint offset = 2166136261u;

            uint hash = offset;
            for (int i = 0; i < bodies.Length; i++)
            {
                hash = Mix(hash, bodies[i].position.x);
                hash = Mix(hash, bodies[i].position.y);
                hash = Mix(hash, bodies[i].linearVelocity.x);
                hash = Mix(hash, bodies[i].linearVelocity.y);
            }

            return hash;
        }

        /// <summary>
        /// The Q5 determinism probe, shared so the cross-process golden and the in-process Q5 test
        /// cannot drift apart. Ten boxes with a fixed initial state fall onto a static ground for
        /// <paramref name="ticks"/> ticks; the returned hash covers every body's position and
        /// velocity bit-exactly.
        /// </summary>
        /// <param name="ticks">Ticks to simulate. 1000 is the guide's number (§5 Q5).</param>
        /// <param name="seedOffset">
        /// Added to each body's initial x. A non-zero value is the perturbed control run that proves
        /// the hash is measuring something rather than being constant by accident.
        /// </param>
        /// <param name="stepSeconds">One tick. Must stay <c>1 / SimTicksPerSecond</c>.</param>
        public static uint RunDeterminismProbe(int ticks, float seedOffset, float stepSeconds)
        {
            PhysicsWorld world = CreateScriptWorld();
            try
            {
                PhysicsBody ground = CreateBody(
                    world, PhysicsBody.BodyType.Static, new Vector2(0f, -1f));
                CreateBox(ground, new Vector2(20f, 1f));

                var bodies = new PhysicsBody[10];
                for (int i = 0; i < bodies.Length; i++)
                {
                    // seedOffset is the perturbation the control case uses to prove the hash varies.
                    float x = i * 0.13f + seedOffset;
                    bodies[i] = CreateBody(
                        world, PhysicsBody.BodyType.Dynamic, new Vector2(x, 2f + i * 0.5f));
                    CreateBox(bodies[i], new Vector2(0.4f, 0.4f));
                    bodies[i].linearVelocity = new Vector2(0.5f, 0f);
                }

                for (int i = 0; i < ticks; i++)
                {
                    world.Simulate(stepSeconds);
                }

                return HashBodies(bodies);
            }
            finally
            {
                DestroyWorld(world);
            }
        }

        private const uint FnvPrime = 16777619u;

        private static uint Mix(uint hash, float value)
        {
            uint bits = BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
            hash ^= bits & 0xFF;
            hash *= FnvPrime;
            hash ^= (bits >> 8) & 0xFF;
            hash *= FnvPrime;
            hash ^= (bits >> 16) & 0xFF;
            hash *= FnvPrime;
            hash ^= (bits >> 24) & 0xFF;
            hash *= FnvPrime;
            return hash;
        }
    }
}
