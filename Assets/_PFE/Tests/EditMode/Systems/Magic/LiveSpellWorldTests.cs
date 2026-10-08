using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Audio;
using PFE.Systems.Map;
using PFE.Systems.Map.Serialization;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Magic;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Magic
{
    /// <summary>
    /// An <see cref="ITileQueryService"/> that answers only <see cref="ITileQueryService.Raycast"/> —
    /// the single member <see cref="LiveSpellWorld"/> uses.
    ///
    /// <para><b>Every other member throws rather than returning a default.</b> A default would let a
    /// future edit that starts calling one pass silently on a meaningless value; a throw names the
    /// member. The base class exists so the two fakes below differ only in what they report.</para>
    /// </summary>
    internal abstract class StubTileQuery : ITileQueryService
    {
        TileQueryBackend ITileQueryService.Backend => TileQueryBackend.Unified;
        RoomInstance ITileQueryService.Room => null;
        Vector2 ITileQueryService.OriginPixel => Vector2.zero;

        bool ITileQueryService.IsSolidAt(Vector2Int tileCoord) => throw new NotSupportedException();
        bool ITileQueryService.CheckCollision(Rect boundsPx, TileQueryOptions options) => throw new NotSupportedException();
        float ITileQueryService.GetGroundHeight(Vector2 positionPx) => throw new NotSupportedException();
        bool ITileQueryService.IsOnGround(Rect boundsPx) => throw new NotSupportedException();
        bool ITileQueryService.IsOnGround(Rect boundsPx, TileQueryOptions options) => throw new NotSupportedException();
        bool ITileQueryService.TryGetSupportSpan(Rect boundsPx, TileQueryOptions options, out float supportLeftWorldPx, out float supportRightWorldPx) => throw new NotSupportedException();

        public abstract TileRaycastHit? Raycast(Vector2 originPx, Vector2 direction, float maxDistancePx);

        TileQueryFlags ITileQueryService.Classify(Vector2Int tileCoord) => throw new NotSupportedException();
        SurfaceKind ITileQueryService.ClassifySurface(Vector2Int tileCoord) => throw new NotSupportedException();
        TileMoveResult ITileQueryService.ResolveMove(in TileBox box, Vector2 delta, TileQueryFlags mask) => throw new NotSupportedException();
        // No `= 1` on radiusTiles: an explicit interface implementation cannot be called with optional
        // arguments, so a default here would be a lie the compiler warns about (CS1066).
        bool ITileQueryService.ApplyDamage(Vector2 positionPx, int damage, int radiusTiles) => throw new NotSupportedException();
        void ITileQueryService.NotifyTilesMutated(RectInt tileRegion) => throw new NotSupportedException();
        TileStateSnapshot[] ITileQueryService.CaptureFullState() => throw new NotSupportedException();
        void ITileQueryService.ApplyFullState(TileStateSnapshot[] snapshot) => throw new NotSupportedException();
        TileMutation[] ITileQueryService.DrainMutations() => throw new NotSupportedException();
        void ITileQueryService.ApplyMutations(TileMutation[] mutations) => throw new NotSupportedException();
    }

    /// <summary>
    /// Reports whatever the test sets, and records the arguments it was handed.
    ///
    /// <para><b>Why a recorder rather than a mock framework.</b> The interesting behaviour is the
    /// <i>arguments</i> — the unit conversion — and recording them makes that assertable directly.</para>
    /// </summary>
    internal sealed class RecordingTileQuery : StubTileQuery
    {
        /// <summary>What the next ray reports. Null means "nothing hit".</summary>
        public TileData HitTile;

        public int RaycastCalls;
        public Vector2 LastOriginPx;
        public Vector2 LastDirection;
        public float LastMaxDistancePx;

        public override TileRaycastHit? Raycast(Vector2 originPx, Vector2 direction, float maxDistancePx)
        {
            RaycastCalls++;
            LastOriginPx = originPx;
            LastDirection = direction;
            LastMaxDistancePx = maxDistancePx;

            return HitTile != null ? new TileRaycastHit(HitTile, originPx) : (TileRaycastHit?)null;
        }
    }

    /// <summary>
    /// Reports a hit whose <c>Tile</c> is null — the "hit from a non-tile source" the interface
    /// documents. Distinct from <see cref="RecordingTileQuery"/> because it is the one case a
    /// <c>hit.HasValue</c>-only test gets wrong.
    /// </summary>
    internal sealed class NoTileHitQuery : StubTileQuery
    {
        public override TileRaycastHit? Raycast(Vector2 originPx, Vector2 direction, float maxDistancePx)
            => new TileRaycastHit(null, originPx);
    }

    /// <summary>An <see cref="ISoundService"/> that records the calls it receives.</summary>
    internal sealed class RecordingSoundService : ISoundService
    {
        public readonly List<(string id, Vector2 pos)> Played = new List<(string, Vector2)>();

        void ISoundService.Play(string id, Vector2 worldPos, float volumeScale)
            => Played.Add((id, worldPos));

        void ISoundService.PlayLoop(string id, object key, float volume) => throw new NotSupportedException();
        void ISoundService.PlayLoopFromTime(string id, object key, float startTimeSec, float volume) => throw new NotSupportedException();
        float ISoundService.GetLoopTime(object key) => throw new NotSupportedException();
        void ISoundService.StopLoop(object key) => throw new NotSupportedException();
        void ISoundService.StopAll() => throw new NotSupportedException();
        float ISoundService.SfxVolume { get; set; }
    }

    /// <summary>
    /// The production <see cref="ISpellWorld"/>, offline.
    ///
    /// <para><b>What is worth proving here, and what is not.</b> The alicorn flag is a constant
    /// <c>false</c> and the three presentation members are no-ops — nothing about them can fail, so
    /// there is no test for them. The two members that <i>can</i> be wrong are
    /// <see cref="ISpellWorld.IsBlocked"/>'s polarity (the oracle's double negative) and its unit
    /// conversion (the cast path works in Unity units, the tile primitive in world pixels). Both are
    /// asserted with a positive <b>and</b> an absent control, because a method that always returned
    /// <c>false</c> would pass the polarity test alone.</para>
    /// </summary>
    [TestFixture]
    public class LiveSpellWorldTests
    {
        private RecordingTileQuery _query;
        private RecordingSoundService _sound;
        private ISpellWorld _world;

        [SetUp]
        public void SetUp()
        {
            _query = new RecordingTileQuery();
            _sound = new RecordingSoundService();
            _world = new LiveSpellWorld(() => _query, _sound);
        }

        // ── the polarity ─────────────────────────────────────────────────────────────────────────

        [Test]
        public void IsBlocked_IsTrue_WhenTheRayHitsATile()
        {
            _query.HitTile = new TileData();

            Assert.IsTrue(_world.IsBlocked(1f, 2f, 3f, 4f),
                "AS3 asks loc.isLine -- 'is the way clear' -- and the port's primitive answers 'what did " +
                "I hit'. A hit is therefore BLOCKED, and the negation lives in LiveSpellHost.");
        }

        [Test]
        public void IsBlocked_IsFalse_WhenTheRayHitsNothing()
        {
            _query.HitTile = null;

            Assert.IsFalse(_world.IsBlocked(1f, 2f, 3f, 4f),
                "The absent control for the test above: without this, a method that always returned " +
                "true would pass the hit case.");
        }

        [Test]
        public void IsBlocked_IsFalse_WhenTheHitCarriesNoTile()
        {
            // The interface documents a hit whose source is not a tile. That is not a wall, so it must
            // not block -- and this is the one case a `hit.HasValue`-only test gets wrong.
            ISpellWorld world = new LiveSpellWorld(() => new NoTileHitQuery(), _sound);

            Assert.IsFalse(world.IsBlocked(0f, 0f, 10f, 0f));
        }

        // ── the unit conversion ──────────────────────────────────────────────────────────────────

        [Test]
        public void IsBlocked_ConvertsUnityUnitsToWorldPixels()
        {
            // 100 px = 1 unit (TileQueryConstants.UnitToPixel). A caster at 1.5 units sends its ray from
            // 150 px, and a target at 4 units is 400 px away. Both endpoints, because converting only
            // the origin would leave the ray's LENGTH wrong and the direction right -- a hit that is
            // missed at range and looks like a LOS failure.
            _world.IsBlocked(1.5f, -2f, 4f, 2f);

            Assert.AreEqual(1, _query.RaycastCalls, "exactly one ray per line test");
            Assert.AreEqual(150f, _query.LastOriginPx.x, 1e-3f);
            Assert.AreEqual(-200f, _query.LastOriginPx.y, 1e-3f);

            float expectedLength = new Vector2(4f - 1.5f, 2f - (-2f)).magnitude * 100f;
            Assert.AreEqual(expectedLength, _query.LastMaxDistancePx, 1e-3f);

            Assert.AreEqual(1f, _query.LastDirection.magnitude, 1e-4f,
                "the direction handed to the query must be normalised -- the interface says so, and a " +
                "non-unit vector with a separate maxDistance is read as a short ray");
        }

        [Test]
        public void IsBlocked_DoesNotRaycast_WhenTheSegmentIsDegenerate()
        {
            // Origin == target. There is nothing to travel through, so the answer is "not blocked" and no
            // ray is sent. The guard matters: `delta / distance` with distance 0 hands the query a NaN
            // direction, and a NaN ray reports "no hit" everywhere -- a silently permissive LOS that no
            // test on the hit case would catch.
            Assert.IsFalse(_world.IsBlocked(3f, 3f, 3f, 3f));
            Assert.AreEqual(0, _query.RaycastCalls, "a zero-length segment must not reach the query");
        }

        // ── the missing room ─────────────────────────────────────────────────────────────────────

        [Test]
        public void IsBlocked_IsFalse_WhenThereIsNoRoom()
        {
            ISpellWorld world = new LiveSpellWorld(() => null, _sound);

            Assert.IsFalse(world.IsBlocked(0f, 0f, 100f, 0f),
                "no room means no tiles, so nothing can block. Refusing the cast instead would be a " +
                "refusal the player cannot see or fix -- the gate order in SpellCastRules decides what " +
                "a failed line means, not this method.");
        }

        // ── sound ────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void PlaySound_ForwardsTheIdAndPosition()
        {
            _world.PlaySound("nomagic", 5f, 7f);

            Assert.AreEqual(1, _sound.Played.Count);
            Assert.AreEqual("nomagic", _sound.Played[0].id);
            Assert.AreEqual(new Vector2(5f, 7f), _sound.Played[0].pos);
        }

        [Test]
        public void PlaySound_IsSilent_ForAnEmptyId()
        {
            _world.PlaySound("", 1f, 1f);
            _world.PlaySound(null, 1f, 1f);

            Assert.AreEqual(0, _sound.Played.Count,
                "an empty sound id is the spell rows' 'no snd attribute' case, not a request to play");
        }

        [Test]
        public void PlaySound_IsSilent_WithoutASoundService()
        {
            ISpellWorld world = new LiveSpellWorld(() => _query, null);

            // Must not throw: a rig with no audio is a legitimate configuration, and a cast that
            // NullReferences here would be a crash blamed on the spell system.
            Assert.DoesNotThrow(() => world.PlaySound("nomagic", 0f, 0f));
        }

        // ── the alicorn flag ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Alicorn_IsFalse_BecauseThePortHasNoSuchGlobal()
        {
            Assert.IsFalse(_world.Alicorn,
                "The port has no playable alicorn. This is false rather than wired to " +
                "UnitDefinition.isAlicorn (a per-unit-definition flag, a different thing) so the two " +
                "alicorn branches stay visibly unimplemented. When a playable alicorn lands, this is " +
                "the one assertion to change.");
        }
    }
}
