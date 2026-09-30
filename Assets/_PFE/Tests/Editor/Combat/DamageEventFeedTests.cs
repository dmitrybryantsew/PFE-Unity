using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins <see cref="DamageEventFeed"/> — the bounded window the floating-damage overlay reads.
    ///
    /// <para>Three things here can go wrong silently. A feed that grows without bound is a leak that
    /// only shows up after a long fight. A feed that expires on absolute time wipes the screen when the
    /// game is paused. And a feed that renders a fully-absorbed hit the same as a miss destroys the one
    /// distinction the overlay exists to make — "the shot connected and did nothing" versus "the shot
    /// was evaded" have different causes, and a shared rendering hides which happened.</para>
    /// </summary>
    [TestFixture]
    public class DamageEventFeedTests
    {
        private const float Lifetime = DamageEventFeed.DefaultLifetime;

        private DamageEventFeed _feed;
        private readonly List<DamageNumber> _scratch = new List<DamageNumber>();

        [SetUp]
        public void SetUp()
        {
            // A fresh instance, never DamageEventFeed.Default: that one is a process-wide singleton the
            // overlay reads, so a test writing to it would leak into every later test in the domain.
            _feed = new DamageEventFeed();
        }

        // ── Recording ────────────────────────────────────────────────────────

        [Test]
        public void Report_AddsOneEntry()
        {
            _feed.Report(Vector3.one, 12f, isCritical: false, isMiss: false);

            Assert.AreEqual(1, _feed.Count);
        }

        [Test]
        public void AdvanceAndSnapshot_ReturnsWhatWasReported()
        {
            _feed.Report(new Vector3(3f, 4f, 0f), 12f, isCritical: false, isMiss: false);

            int live = _feed.AdvanceAndSnapshot(0.05f, _scratch);

            Assert.AreEqual(1, live);
            Assert.AreEqual(new Vector3(3f, 4f, 0f), _scratch[0].Position);
            Assert.AreEqual(12f, _scratch[0].Amount, 1e-4f);
        }

        [Test]
        public void Report_AtCapacity_DropsTheOldest_NotTheNewest()
        {
            // Under sustained fire the recent hits are the ones being diagnosed, so the buffer must
            // evict from the far end. Asserted on identity rather than on the count, because a
            // count-only test passes just as happily against a feed that drops the newest.
            var feed = new DamageEventFeed(capacity: 3);

            feed.Report(Vector3.zero, 1f, false, false);
            feed.Report(Vector3.zero, 2f, false, false);
            feed.Report(Vector3.zero, 3f, false, false);
            feed.Report(Vector3.zero, 4f, false, false);

            feed.AdvanceAndSnapshot(0f, _scratch);

            Assert.AreEqual(3, _scratch.Count);
            CollectionAssert.AreEqual(new[] { 2f, 3f, 4f }, Amounts(_scratch),
                "the oldest entry must be the one evicted");
        }

        [Test]
        public void Count_StaysBounded_OverManyHits()
        {
            for (int i = 0; i < DamageEventFeed.DefaultCapacity * 5; i++)
            {
                _feed.Report(Vector3.zero, 1f, false, false);
            }

            Assert.LessOrEqual(_feed.Count, DamageEventFeed.DefaultCapacity,
                "the buffer is bounded — an unbounded one is a leak that only shows up after a long fight");
        }

        // ── Ageing and expiry ────────────────────────────────────────────────

        [Test]
        public void Advance_ExpiresAtTheLifetime_NotBefore()
        {
            _feed.Report(Vector3.zero, 5f, false, false);

            // Just short of the lifetime: still there.
            Assert.AreEqual(1, _feed.AdvanceAndSnapshot(Lifetime * 0.99f, _scratch));

            // Past it: gone.
            Assert.AreEqual(0, _feed.AdvanceAndSnapshot(Lifetime, _scratch));
            Assert.AreEqual(0, _feed.Count, "an expired entry must be released, not just hidden");
        }

        [Test]
        public void Advance_AccumulatesAcrossCalls_NotFromAnAbsoluteClock()
        {
            // The point of ageing by delta rather than by storing a birth time: a paused or
            // backgrounded game must not expire the whole buffer the moment it resumes. Ten small
            // steps totalling under the lifetime must keep the entry alive.
            _feed.Report(Vector3.zero, 5f, false, false);

            for (int i = 0; i < 10; i++)
            {
                _feed.AdvanceAndSnapshot(Lifetime * 0.05f, _scratch);
            }

            Assert.AreEqual(1, _scratch.Count, "half a lifetime of small steps must not expire it");
            Assert.AreEqual(Lifetime * 0.5f, _scratch[0].Age, 1e-4f);
        }

        [Test]
        public void Clear_EmptiesTheFeed()
        {
            _feed.Report(Vector3.zero, 5f, false, false);
            _feed.Clear();

            Assert.AreEqual(0, _feed.Count);
            Assert.AreEqual(0, _feed.AdvanceAndSnapshot(0f, _scratch));
        }

        // ── The visual curve ─────────────────────────────────────────────────

        [Test]
        public void Alpha_HoldsFullForTheFirstHalf_ThenFadesToZero()
        {
            // A figure that starts fading on frame one is unreadable at exactly the moment it matters.
            Assert.AreEqual(1f, DamageEventFeed.Alpha(0f, Lifetime), 1e-4f, "born fully opaque");
            Assert.AreEqual(1f, DamageEventFeed.Alpha(Lifetime * 0.5f, Lifetime), 1e-4f, "still opaque at half");
            Assert.AreEqual(0.5f, DamageEventFeed.Alpha(Lifetime * 0.75f, Lifetime), 1e-4f, "half faded at 3/4");
            Assert.AreEqual(0f, DamageEventFeed.Alpha(Lifetime, Lifetime), 1e-4f, "gone at the end");
        }

        [Test]
        public void RiseOffset_ClimbsLinearlyFromZeroToTheFullDistance()
        {
            Assert.AreEqual(0f, DamageEventFeed.RiseOffset(0f, Lifetime), 1e-4f);
            Assert.AreEqual(DamageEventFeed.RiseDistance * 0.5f,
                            DamageEventFeed.RiseOffset(Lifetime * 0.5f, Lifetime), 1e-4f);
            Assert.AreEqual(DamageEventFeed.RiseDistance,
                            DamageEventFeed.RiseOffset(Lifetime, Lifetime), 1e-4f);
        }

        [Test]
        public void Alpha_AndRise_AreClamped_NotExtrapolated()
        {
            // Both are called with an age that can momentarily exceed the lifetime (the snapshot is
            // taken before the expiry sweep in some frames), and an unclamped curve would push the
            // figure off-screen or give it a negative alpha.
            Assert.AreEqual(0f, DamageEventFeed.Alpha(Lifetime * 3f, Lifetime), 1e-4f);
            Assert.AreEqual(DamageEventFeed.RiseDistance,
                            DamageEventFeed.RiseOffset(Lifetime * 3f, Lifetime), 1e-4f);
        }

        // ── The text, which is the actual finding ────────────────────────────

        [Test]
        public void TextFor_Miss_ReadsMiss_NotZero()
        {
            Assert.AreEqual("MISS", DamageEventFeed.TextFor(new DamageNumber(Vector3.zero, 0f, false, true, 0f)));
        }

        [Test]
        public void TextFor_AbsorbedHit_ReadsZero_NotMiss()
        {
            // The distinction the overlay exists for. A hit that landed and did no damage is the
            // signature of armour (or skin) absorbing everything, and rendering it as MISS would send
            // the reader to the evasion code instead of the reduction code.
            var absorbed = new DamageNumber(Vector3.zero, 0f, isCritical: false, isMiss: false, 0f);

            Assert.AreEqual("0", DamageEventFeed.TextFor(absorbed));
            Assert.AreNotEqual(DamageEventFeed.TextFor(absorbed),
                               DamageEventFeed.TextFor(new DamageNumber(Vector3.zero, 0f, false, true, 0f)));
        }

        [Test]
        public void TextFor_RoundsUp_SoAGrazeDoesNotReadAsZero()
        {
            // 0.4 damage rendered as "0" is indistinguishable from a fully-absorbed hit, which is a
            // different bug.
            Assert.AreEqual("1", DamageEventFeed.TextFor(new DamageNumber(Vector3.zero, 0.4f, false, false, 0f)));
            Assert.AreEqual("12", DamageEventFeed.TextFor(new DamageNumber(Vector3.zero, 11.2f, false, false, 0f)));
        }

        [Test]
        public void TextFor_Crit_IsMarked()
        {
            Assert.AreEqual("12!", DamageEventFeed.TextFor(new DamageNumber(Vector3.zero, 11.2f, true, false, 0f)));
        }

        // ── The shared instance ──────────────────────────────────────────────

        [Test]
        public void Default_IsASingleton_TheOverlayCanRead()
        {
            Assert.IsNotNull(DamageEventFeed.Default);
            Assert.AreSame(DamageEventFeed.Default, DamageEventFeed.Default,
                "the overlay has no wiring, so the default must be stable across reads");
        }

        private static List<float> Amounts(List<DamageNumber> numbers)
        {
            var result = new List<float>(numbers.Count);
            for (int i = 0; i < numbers.Count; i++) result.Add(numbers[i].Amount);
            return result;
        }
    }
}
