using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Map;
// Required, and not obviously so: RoomInstance itself lives in PFE.Systems.Map, but the
// ITileQueryService signatures this stub must match (CaptureFullState/ApplyFullState) name
// TileStateSnapshot, which lives here. Removing this line breaks the stub's interface contract.
using PFE.Systems.Map.Serialization;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Particles;
using PFE.Systems.Particles.Adapters;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Particles
{
    /// <summary>
    /// Pins <see cref="RoomParticleEmitter"/> — the one place a Unity world position becomes the AS3
    /// room-local pixel space the particle pipeline works in.
    ///
    /// <para><b>Why this is worth a fixture.</b> The adapter is small, but it is the seam roughly ninety
    /// call sites will use, and its two failure modes are both <i>silent</i>: emitting at an unmirrored Y
    /// puts the particles at the vertically opposite point, and emitting with no room pushes them against
    /// an origin of zero. Neither throws. So the tests below assert the emitted <i>coordinates</i> — and
    /// every "it refused" assertion is paired with a positive control that the same call succeeds once a
    /// room is pushed, because an assertion that X did not happen is unfalsifiable on its own.</para>
    /// </summary>
    [TestFixture]
    public class RoomParticleEmitterTests
    {
        private const int RoomHeight = 25;
        private const float RoomHeightPixels = RoomHeight * WorldConstants.TILE_SIZE;   // 1000

        // ── Fakes ────────────────────────────────────────────────────────────────────────────

        /// <summary>Records what it was asked to emit, so the coordinates can be asserted directly.</summary>
        private sealed class RecordingWorld : IParticleWorld
        {
            public readonly List<(string Id, float X, float Y, ParticleSpec Spec)> Emits =
                new List<(string, float, float, ParticleSpec)>();

            public bool Emit(string id, float x, float y) => Emit(id, x, y, null);

            public bool Emit(string id, float x, float y, ParticleSpec overrides)
            {
                Emits.Add((id, x, y, overrides));
                return true;
            }

            public bool Has(string id) => true;
            public IReadOnlyCollection<string> UnknownIds => Array.Empty<string>();
            public void BeginTick() { }
        }

        /// <summary>
        /// A tile query that answers only what the adapter asks. Every other member throws rather than
        /// returning a default, so a future edit that starts calling one names itself instead of
        /// passing on a meaningless value.
        /// </summary>
        private sealed class StubTileQuery : ITileQueryService
        {
            public RoomInstance RoomInstance;
            public Vector2 Origin;

            TileQueryBackend ITileQueryService.Backend => TileQueryBackend.Unified;
            RoomInstance ITileQueryService.Room => RoomInstance;
            Vector2 ITileQueryService.OriginPixel => Origin;

            bool ITileQueryService.IsSolidAt(Vector2Int tileCoord) => throw new NotSupportedException();
            bool ITileQueryService.CheckCollision(Rect boundsPx, TileQueryOptions options) => throw new NotSupportedException();
            float ITileQueryService.GetGroundHeight(Vector2 positionPx) => throw new NotSupportedException();
            bool ITileQueryService.IsOnGround(Rect boundsPx) => throw new NotSupportedException();
            bool ITileQueryService.IsOnGround(Rect boundsPx, TileQueryOptions options) => throw new NotSupportedException();
            bool ITileQueryService.TryGetSupportSpan(Rect boundsPx, TileQueryOptions options, out float supportLeftWorldPx, out float supportRightWorldPx) => throw new NotSupportedException();
            TileRaycastHit? ITileQueryService.Raycast(Vector2 originPx, Vector2 direction, float maxDistancePx) => throw new NotSupportedException();
            TileQueryFlags ITileQueryService.Classify(Vector2Int tileCoord) => throw new NotSupportedException();
            SurfaceKind ITileQueryService.ClassifySurface(Vector2Int tileCoord) => throw new NotSupportedException();
            TileMoveResult ITileQueryService.ResolveMove(in TileBox box, Vector2 delta, TileQueryFlags mask) => throw new NotSupportedException();
            bool ITileQueryService.ApplyDamage(Vector2 positionPx, int damage, int radiusTiles) => throw new NotSupportedException();
            void ITileQueryService.NotifyTilesMutated(RectInt tileRegion) => throw new NotSupportedException();
            TileStateSnapshot[] ITileQueryService.CaptureFullState() => throw new NotSupportedException();
            void ITileQueryService.ApplyFullState(TileStateSnapshot[] snapshot) => throw new NotSupportedException();
            TileMutation[] ITileQueryService.DrainMutations() => throw new NotSupportedException();
            void ITileQueryService.ApplyMutations(TileMutation[] mutations) => throw new NotSupportedException();
        }

        private static StubTileQuery RoomAt(Vector2 originPixel, int heightTiles = RoomHeight)
        {
            return new StubTileQuery
            {
                RoomInstance = new RoomInstance { height = heightTiles },
                Origin = originPixel,
            };
        }

        // ── The refusal contract ─────────────────────────────────────────────────────────────

        [Test]
        public void Emit_WithNoRoom_RefusesRatherThanEmittingAtAWrongPlace()
        {
            var world = new RecordingWorld();
            var emitter = new RoomParticleEmitter(world);

            Assert.IsFalse(emitter.HasRoom);
            Assert.IsFalse(emitter.Emit("expl", new Vector3(1f, 1f, 0f)));

            // The point of the refusal: nothing reached the world at all. An emit at an unconverted
            // position would be "plausible and wrong", which is worse than no emit.
            Assert.AreEqual(0, world.Emits.Count);
            Assert.AreEqual(1, emitter.RefusedWithoutRoom);

            // Positive control — the same call succeeds once a room is pushed, so the assertion above
            // is about the missing room and not about the call being broken.
            emitter.SetTileQuery(RoomAt(Vector2.zero));
            Assert.IsTrue(emitter.HasRoom);
            Assert.IsTrue(emitter.Emit("expl", new Vector3(1f, 1f, 0f)));
            Assert.AreEqual(1, world.Emits.Count);
            Assert.AreEqual(1, emitter.RefusedWithoutRoom, "a successful emit must not bump the refusal count");
        }

        [Test]
        public void TryToAs3Local_WithNoRoom_ReturnsFalse()
        {
            var emitter = new RoomParticleEmitter(new RecordingWorld());

            Assert.IsFalse(emitter.TryToAs3Local(new Vector3(3f, 4f, 0f), out Vector2 as3Local));

            // Documented as "must not be used" — asserted so the contract is not just a comment.
            Assert.AreEqual(Vector2.zero, as3Local);

            // Positive control.
            emitter.SetTileQuery(RoomAt(Vector2.zero));
            Assert.IsTrue(emitter.TryToAs3Local(new Vector3(3f, 4f, 0f), out _));
        }

        // ── The conversion ───────────────────────────────────────────────────────────────────

        [Test]
        public void Emit_MirrorsYIntoAs3Space()
        {
            var world = new RecordingWorld();
            var emitter = new RoomParticleEmitter(world);
            emitter.SetTileQuery(RoomAt(Vector2.zero));

            // Unity world (5, 10) = 500 px right, 1000 px up from the room's floor corner.
            emitter.Emit("expl", new Vector3(5f, 10f, 0f));

            Assert.AreEqual(1, world.Emits.Count);
            (string id, float x, float y, ParticleSpec _) = world.Emits[0];

            Assert.AreEqual("expl", id);
            Assert.AreEqual(500f, x, 0.001f, "X needs no mirror — both spaces run left-to-right");

            // AS3 Y runs DOWN from the ceiling, so a point at the port's ceiling is AS3 Y 0.
            Assert.AreEqual(0f, y, 0.001f);

            // Control: the un-mirrored value is the vertically opposite end of the room, and it is what
            // this adapter would have produced before the mirror was found. Asserting it is NOT that
            // value is what makes the mirror load-bearing rather than incidental.
            Assert.AreNotEqual(1000f, y, "Y was not mirrored — the part would be at the opposite end");
        }

        [Test]
        public void Emit_SubtractsTheRoomOrigin()
        {
            var world = new RecordingWorld();
            var emitter = new RoomParticleEmitter(world);

            // The room one land step right and one up: origin at world pixel (1920, 1000).
            emitter.SetTileQuery(RoomAt(new Vector2(1920f, 1000f)));

            // Unity world (19.2, 20) = world pixel (1920, 2000) = 1000 px above THIS room's floor.
            emitter.Emit("expl", new Vector3(19.2f, 20f, 0f));

            (_, float x, float y, ParticleSpec _) = world.Emits[0];

            Assert.AreEqual(0f, x, 0.001f);
            Assert.AreEqual(0f, y, 0.001f);

            // Control: without the origin subtraction the same world point reads as (1920, 0) — the
            // classic room-relative bug, invisible in a room at land (0,0) because the origin is zero.
            Assert.AreNotEqual(1920f, x, "the room origin was not subtracted");
        }

        [Test]
        public void Emit_HonoursANonDefaultRoomHeight()
        {
            var world = new RecordingWorld();
            var emitter = new RoomParticleEmitter(world);

            // A 10-tile room is 400 px tall, so its ceiling is at port Y 400, not 1000.
            emitter.SetTileQuery(RoomAt(Vector2.zero, heightTiles: 10));

            emitter.Emit("expl", new Vector3(0f, 4f, 0f));   // 400 px up = the ceiling

            (_, _, float y, ParticleSpec _) = world.Emits[0];

            // A wrong height mirrors by half the difference — the reason the renderer takes the room's
            // own height rather than the 25-tile default.
            Assert.AreEqual(0f, y, 0.001f, "the mirror used the wrong room height");
        }

        [Test]
        public void Emit_PassesTheOverridesThrough()
        {
            var world = new RecordingWorld();
            var emitter = new RoomParticleEmitter(world);
            emitter.SetTileQuery(RoomAt(Vector2.zero));

            var spec = new ParticleSpec { Kol = 30, RX = 100f, RY = 100f };
            emitter.Emit("bubble", new Vector3(0f, 0f, 0f), spec);

            Assert.AreSame(spec, world.Emits[0].Spec, "the caller's spec must be forwarded, not rebuilt");
        }

        // ── The AS3-space entry point ────────────────────────────────────────────────────────

        [Test]
        public void EmitAt_ForwardsVerbatim_AndDoesNotNeedARoom()
        {
            var world = new RecordingWorld();
            var emitter = new RoomParticleEmitter(world);

            // No room pushed: EmitAt is the path for a caller already working in AS3 space, so there is
            // nothing to convert and nothing to refuse.
            Assert.IsFalse(emitter.HasRoom);
            Assert.IsTrue(emitter.EmitAt("expl", new Vector2(123f, 456f)));

            Assert.AreEqual(1, world.Emits.Count);
            Assert.AreEqual(123f, world.Emits[0].X, 0.001f);
            Assert.AreEqual(456f, world.Emits[0].Y, 0.001f);
            Assert.AreEqual(0, emitter.RefusedWithoutRoom, "EmitAt must not count as a refusal");
        }

        // ── The two paths agree ──────────────────────────────────────────────────────────────

        [Test]
        public void EmitAndEmitAt_AgreeOnTheSamePoint()
        {
            var viaWorld = new RecordingWorld();
            var viaAs3 = new RecordingWorld();

            var a = new RoomParticleEmitter(viaWorld);
            var b = new RoomParticleEmitter(viaAs3);
            a.SetTileQuery(RoomAt(Vector2.zero));
            b.SetTileQuery(RoomAt(Vector2.zero));

            var worldPos = new Vector3(7.5f, 3.25f, 0f);
            a.Emit("iskr", worldPos);

            Assert.IsTrue(b.TryToAs3Local(worldPos, out Vector2 as3Local));
            b.EmitAt("iskr", as3Local);

            Assert.AreEqual(viaWorld.Emits[0].X, viaAs3.Emits[0].X, 0.001f);
            Assert.AreEqual(viaWorld.Emits[0].Y, viaAs3.Emits[0].Y, 0.001f);
        }
    }
}
