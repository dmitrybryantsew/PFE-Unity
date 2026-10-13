using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Weapons;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="MeleeTileSweepMath"/> — AS3's <b>bullet-versus-tile</b> sub-step loop
    /// (<c>Bullet.run()</c>, <c>fe/weapon/Bullet.as:411-493</c>), which is what makes a melee swing
    /// break the wall it is swung into.
    ///
    /// <para><b>Why this is a separate fixture from <c>UnitSweepMathTests</c>.</b> The two loops share
    /// the sub-step arithmetic and nothing else: this one asks "is a tile here?" by <i>position</i>
    /// (AS3's <c>loc.getAbsTile(X,Y)</c>) rather than by testing a candidate list, and it is the melee
    /// path's only route to tile damage — a trigger capsule cannot see an <c>IDestructibleTile</c>,
    /// because tiles are not <c>IDamageable</c>.</para>
    ///
    /// <para><b>Every expected position is derived from the sub-step grid, not guessed.</b>
    /// <c>StepsFor(40, 0)</c> is <c>floor(40/9) + 1 = 5</c>, so a 40 px move is five steps of 8 px and
    /// the positions visited are 8, 16, 24, 32, 40. The tests below assert the visited set directly, so
    /// a change to the step formula fails here instead of silently moving every impact by up to
    /// 9 px.</para>
    /// </summary>
    [TestFixture]
    public class MeleeTileSweepMathTests
    {
        /// <summary>
        /// A recording tile lookup: answers "is this point inside one of the given boxes?" and keeps
        /// every position it was asked about, so the sub-step grid can be asserted directly instead of
        /// being inferred from which tile happened to be reported.
        /// </summary>
        private sealed class ProbeLog
        {
            private readonly List<float[]> _tiles;

            public readonly List<float> AskedXs = new List<float>();
            public readonly List<float> AskedYs = new List<float>();

            public ProbeLog(params float[][] tiles)
            {
                _tiles = new List<float[]>(tiles);
            }

            public int Count => AskedXs.Count;

            public bool IsTileAt(float x, float y)
            {
                AskedXs.Add(x);
                AskedYs.Add(y);

                foreach (float[] t in _tiles)
                {
                    if (x >= t[0] && x <= t[1] && y >= t[2] && y <= t[3]) return true;
                }

                return false;
            }
        }

        /// <summary>A tile's physical bounds, as AS3's <c>phX1..phX2</c> / <c>phY1..phY2</c>.</summary>
        private static float[] Tile(float minX, float maxX, float minY, float maxY)
            => new[] { minX, maxX, minY, maxY };

        // ── The grid ─────────────────────────────────────────────────────────

        [Test]
        public void TheVisitedPositionsAreTheSubStepGrid()
        {
            var probe = new ProbeLog();

            MeleeTileSweepMath.TryFirstTileHit(0f, 0f, 40f, 0f, probe.IsTileAt, out _, out _);

            // 5 sub-steps of 8 px, the FIRST of which is one step out (advance-then-test).
            Assert.That(probe.AskedXs, Is.EqualTo(new List<float> { 8f, 16f, 24f, 32f, 40f }),
                "40 px at maxdelta 9 is floor(40/9)+1 = 5 steps of 8 px");
        }

        [Test]
        public void TheOriginIsNotTested()
        {
            // AS3's run() opens with `X += dx / param1` (Bullet.as:417-418) and only then reaches the
            // tile lookup (:432). The origin is the previous frame's final sub-step, so re-testing it
            // would report a hit one sub-step early — up to 9 px of positional error on every impact.
            var probe = new ProbeLog(Tile(0f, 0f, -1f, 1f));   // a tile exactly at the origin

            bool hit = MeleeTileSweepMath.TryFirstTileHit(0f, 0f, 40f, 0f, probe.IsTileAt, out _, out _);

            Assert.IsFalse(hit, "a tile only at the origin must not be reported — the origin is not a probe site");
            Assert.That(probe.AskedXs, Has.No.Member(0f));
        }

        [Test]
        public void ZeroDeltaStillProbesOnce()
        {
            // StepsFor always returns at least 1: AS3 calls run() once for a stationary bullet, so the
            // current position is tested every frame rather than only on the frame it was reached.
            var probe = new ProbeLog(Tile(4f, 6f, -1f, 1f));

            bool hit = MeleeTileSweepMath.TryFirstTileHit(5f, 0f, 0f, 0f, probe.IsTileAt, out float hx, out float hy);

            Assert.IsTrue(hit);
            Assert.That(probe.Count, Is.EqualTo(1));
            Assert.That(hx, Is.EqualTo(5f));
            Assert.That(hy, Is.EqualTo(0f));
        }

        // ── First hit wins, and only the first ───────────────────────────────

        [Test]
        public void TheFirstTileAlongTheSweepWins()
        {
            // Two tiles, both sitting ON the sub-step grid {8,16,24,32,40}: one covers 32, the other
            // covers 40. AS3 latches `tilehit` on the first one (Bullet.as:491), so the caller applies
            // damage to exactly one tile per swing — and the loop must stop rather than walk on to 40.
            var probe = new ProbeLog(
                Tile(39f, 41f, -1f, 1f),   // reached only at the last sub-step, 40
                Tile(31f, 33f, -1f, 1f));  // reached at 32 — earlier, so this one must win

            bool hit = MeleeTileSweepMath.TryFirstTileHit(0f, 0f, 40f, 0f, probe.IsTileAt, out float hx, out _);

            Assert.IsTrue(hit);
            Assert.That(hx, Is.EqualTo(32f),
                "the 31..33 tile is reached at sub-step 32; the 39..41 tile only at 40");
            Assert.That(probe.Count, Is.EqualTo(4),
                "the loop returns on the hit — it must not probe the remaining sub-step at 40");
        }

        [Test]
        public void ASingleSampleWouldMissATileTheSubStepFinds()
        {
            // The load-bearing case, and the reason the loop exists: a 3 px tile whose only grid point
            // is 24, three sub-steps short of the end. An end-of-segment overlap test samples only 40
            // and walks straight past it. This is the melee half of the same arithmetic that fixes
            // "bullets sometimes go through enemies".
            var probe = new ProbeLog(Tile(23f, 25f, -1f, 1f));

            bool hit = MeleeTileSweepMath.TryFirstTileHit(0f, 0f, 40f, 0f, probe.IsTileAt, out float hx, out _);

            Assert.IsTrue(hit, "the 23..25 tile contains sub-step 24 and must be found");
            Assert.That(hx, Is.EqualTo(24f));
        }

        [Test]
        public void ATileBeyondTheEndOfTheSweepIsNotHit()
        {
            var probe = new ProbeLog(Tile(44f, 48f, -1f, 1f));

            bool hit = MeleeTileSweepMath.TryFirstTileHit(0f, 0f, 40f, 0f, probe.IsTileAt, out _, out _);

            Assert.IsFalse(hit, "44 is past the last probe site (40)");
        }

        // ── Inclusivity and edges ────────────────────────────────────────────

        [Test]
        public void TileBoundsAreInclusiveOnBothEdges()
        {
            // AS3's test is `X >= phX1 && X <= phX2 && Y >= phY1 && Y <= phY2` (Bullet.as:476) —
            // inclusive on all four, like the unit test at :515.
            var probe = new ProbeLog(Tile(16f, 16f, 0f, 0f));   // degenerate: exactly the sub-step point

            bool hit = MeleeTileSweepMath.TryFirstTileHit(0f, 0f, 40f, 0f, probe.IsTileAt, out float hx, out float hy);

            Assert.IsTrue(hit, "a tile whose bounds are exactly the sub-step position is a hit");
            Assert.That(hx, Is.EqualTo(16f));
            Assert.That(hy, Is.EqualTo(0f));
        }

        [Test]
        public void ADiagonalSweepStepsByTheLargerComponent()
        {
            // StepsFor keys off max(|dx|,|dy|), so a 30/40 move is 5 steps of 6/8 px — not 8 steps of
            // 3.75/5. Deriving the grid from the wrong component is the mistake that produced five bad
            // expectations in the sibling fixture.
            var probe = new ProbeLog();

            MeleeTileSweepMath.TryFirstTileHit(0f, 0f, 30f, 40f, probe.IsTileAt, out _, out _);

            Assert.That(probe.Count, Is.EqualTo(5));
            Assert.That(probe.AskedXs[4], Is.EqualTo(30f).Within(1e-4f));
            Assert.That(probe.AskedYs[4], Is.EqualTo(40f).Within(1e-4f));
            Assert.That(probe.AskedYs[0], Is.EqualTo(8f).Within(1e-4f));
        }

        [Test]
        public void ACoarserMaxDeltaChangesTheGrid()
        {
            // The parameter exists so the constant is not baked in; a caller passing 20 would get
            // floor(40/20)+1 = 3 steps of 13.33 px. Pinned because a silently-ignored parameter is
            // indistinguishable from a correct one on the default path.
            var probe = new ProbeLog();

            MeleeTileSweepMath.TryFirstTileHit(0f, 0f, 40f, 0f, probe.IsTileAt, out _, out _, maxDeltaPx: 20f);

            Assert.That(probe.Count, Is.EqualTo(3));
        }

        // ── Degenerate inputs ────────────────────────────────────────────────

        [Test]
        public void ANullPredicateIsNotAHit()
        {
            bool hit = MeleeTileSweepMath.TryFirstTileHit(0f, 0f, 40f, 0f, null, out float hx, out float hy);

            Assert.IsFalse(hit);
            Assert.That(hx, Is.EqualTo(0f), "the out-params report the origin, not NaN");
            Assert.That(hy, Is.EqualTo(0f));
        }

        [Test]
        public void NoTileAnywhereIsNotAHit()
        {
            var probe = new ProbeLog();

            bool hit = MeleeTileSweepMath.TryFirstTileHit(0f, 0f, 40f, 0f, probe.IsTileAt, out float hx, out float hy);

            Assert.IsFalse(hit);
            Assert.That(hx, Is.EqualTo(0f));
            Assert.That(hy, Is.EqualTo(0f));
            Assert.That(probe.Count, Is.EqualTo(5), "every sub-step is still asked");
        }
    }
}
