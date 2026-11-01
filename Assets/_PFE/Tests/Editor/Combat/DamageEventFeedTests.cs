using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins <see cref="DamageEventFeed"/> — the bounded window the floating-damage overlay reads, and
    /// the port's model of AS3's <c>numbEmit</c> hit figures.
    ///
    /// <para>Four things here can go wrong silently. A feed that grows without bound is a leak that only
    /// shows up after a long fight. A feed that expires on absolute time wipes the screen when the game
    /// is paused. A feed that renders a fully-absorbed hit the same as a miss destroys the one
    /// distinction the overlay exists to make. And a feed that does not <b>add up</b> prints a column of
    /// small numbers where the oracle prints one climbing total — which is the difference between the
    /// port and the game it is a port of.</para>
    ///
    /// <para>Every number asserted below is the oracle's, cited at the assertion. The previous version of
    /// this fixture pinned a 0.9-second lifetime, a linear 0.85-unit rise and a ceiling-rounding rule —
    /// all three invented, all three now replaced by what <c>Unit.damage()</c> actually does.</para>
    /// </summary>
    [TestFixture]
    public class DamageEventFeedTests
    {
        private const float Lifetime = DamageEventFeed.DefaultLifetime;

        /// <summary>Target key for the accumulating cases. Any non-zero int works; this one is a dummy.</summary>
        private const int Target = 4242;

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
            Assert.AreEqual(12f, _scratch[0].Amount, 1e-4f);

            // The position is jittered on purpose — AS3 passes `rx: 40` on every damage cast
            // (Unit.as:3934), so the figure lands within ±20 px (0.2 units) of the impact.
            Assert.AreEqual(4f, _scratch[0].Position.y, 1e-4f, "jitter is horizontal only");
            Assert.LessOrEqual(Mathf.Abs(_scratch[0].Position.x - 3f), 0.2f + 1e-4f);
        }

        [Test]
        [Description("The spread is AS3's rx: 40 — ±20 px, horizontal only, and reproducible from a seed")]
        public void Jitter_IsTheOracleSpread_AndIsReproducibleFromASeed()
        {
            // Capacity well above the sample size, so nothing is evicted mid-measurement.
            var seeded = new DamageEventFeed(capacity: 256, seed: 20261027);
            var twin = new DamageEventFeed(capacity: 256, seed: 20261027);

            // The spread's own numbers, written down before anything is measured. `rx: 40` is the
            // emitter argument; ±20 px is what it produces. These two lines are what make the bound
            // below a test rather than a restatement: with the half-width used as the multiplier the
            // figure lands within ±10 px, which satisfies a ±20 px upper bound perfectly well.
            Assert.AreEqual(40f, DamageEventFeed.JitterWidthPixels, 1e-4f, "AS3 rx: 40");
            Assert.AreEqual(20f, DamageEventFeed.JitterHalfWidthPixels, 1e-4f, "half of rx");

            const int draws = 200;
            for (int i = 0; i < draws; i++)
            {
                // targetKey 0: never accumulates, so every draw is its own figure.
                seeded.Report(Vector3.zero, 1f, isCritical: false, isMiss: false);
                twin.Report(Vector3.zero, 1f, isCritical: false, isMiss: false);
            }

            var seededFigures = new List<DamageNumber>();
            seeded.AdvanceAndSnapshot(0f, seededFigures);
            var twinFigures = new List<DamageNumber>();
            twin.AdvanceAndSnapshot(0f, twinFigures);

            Assert.AreEqual(draws, seededFigures.Count, "targetKey 0 means one figure per draw");

            float min = float.MaxValue;
            float max = float.MinValue;

            for (int i = 0; i < seededFigures.Count; i++)
            {
                Vector3 p = seededFigures[i].Position;

                Assert.AreEqual(0f, p.y, 1e-6f, "rx is the only axis every numbEmit cast passes");
                Assert.LessOrEqual(Mathf.Abs(p.x), 0.2f + 1e-4f,
                    "(0.5 +- 0.5) * 40 px = +-20 px = +-0.2 units");
                Assert.AreEqual(twinFigures[i].Position.x, p.x, 1e-6f,
                    "a seeded feed replays a burst identically — that is why the RNG is instance state");

                min = Mathf.Min(min, p.x);
                max = Mathf.Max(max, p.x);
            }

            // The spread has to actually spread. A jitter that always returned the midpoint would
            // satisfy the bound above and be a silent regression in the very thing the bound is for.
            Assert.Less(min, -0.15f, "200 draws should reach most of the way to -0.2");
            Assert.Greater(max, 0.15f, "and most of the way to +0.2");
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

        // ── Accumulation — the oracle's default mode ─────────────────────────

        [Test]
        public void Accumulate_AddsSuccessiveHitsIntoOneFigure()
        {
            // AS3 `Unit.as:3940-3958`: `hitSumm += param1`, and while `hitPart` is alive the SAME part
            // is re-texted with the new total. One figure, climbing.
            _feed.Report(Vector3.zero, 10f, false, false, Target);
            _feed.Report(Vector3.zero, 5f, false, false, Target);
            _feed.Report(Vector3.zero, 2f, false, false, Target);

            Assert.AreEqual(1, _feed.Count, "three hits on one target are one figure, not three");

            _feed.AdvanceAndSnapshot(0f, _scratch);

            Assert.AreEqual(17f, _scratch[0].Amount, 1e-4f);
            Assert.AreEqual("17", DamageEventFeed.TextFor(_scratch[0]));
        }

        [Test]
        public void Accumulate_KeepsThePositionItWasBornAt()
        {
            // The oracle updates `text` and `liv` on an existing part and never touches its X/Y
            // (`Unit.as:3952-3957`), so the total keeps drifting from the first hit's impact point
            // instead of teleporting to each new one.
            _feed.Report(new Vector3(1f, 0f, 0f), 10f, false, false, Target);
            _feed.AdvanceAndSnapshot(0.1f, _scratch);
            float x0 = _scratch[0].Position.x;

            _feed.Report(new Vector3(9f, 0f, 0f), 10f, false, false, Target);
            _feed.AdvanceAndSnapshot(0f, _scratch);

            // The count is the premise, not a restatement: with accumulation off the second hit is a
            // second figure, and `_scratch[0]` then trivially keeps the first hit's position — which is
            // how this test stayed green under a mutation that disabled accumulation (2026-10-27).
            Assert.AreEqual(1, _scratch.Count, "the second hit must have joined the first figure");
            Assert.AreEqual(20f, _scratch[0].Amount, 1e-4f, "which now holds both hits");
            Assert.AreEqual(x0, _scratch[0].Position.x, 1e-4f,
                "and it kept drifting from the first impact, it did not jump to the new one");
        }

        [Test]
        public void Accumulate_RefreshesTheLifetimeOnEveryHit()
        {
            // `hitPart.liv = 60` on each hit (`Unit.as:3957`) — so a held trigger keeps ONE figure alive
            // for as long as the trigger is held, even though a single figure only lives two seconds.
            _feed.Report(Vector3.zero, 1f, false, false, Target);

            // Twenty hits, 0.2 s apart: 4 s of sustained fire, twice one figure's whole life. Every hit
            // is inside the ten-frame window, so every one of them refreshes the same figure.
            for (int i = 0; i < 20; i++)
            {
                _feed.AdvanceAndSnapshot(0.2f, _scratch);
                _feed.Report(Vector3.zero, 1f, false, false, Target);
            }

            _feed.AdvanceAndSnapshot(0f, _scratch);

            Assert.AreEqual(1, _scratch.Count, "sustained fire is one climbing total, not a column");
            Assert.AreEqual(21f, _scratch[0].Amount, 1e-4f);
        }

        [Test]
        public void Accumulate_AGapLongerThanTheWindow_StartsASecondFigure()
        {
            // The window is a CONTRAST, so both sides of it are asserted here. The oracle's
            // `t_hitPart` is only ten frames, so a shot a second and a half later is a new number even
            // though the previous figure is still on screen — but asserting only that far side passes
            // just as well with accumulation switched off entirely, which a mutation control proved on
            // 2026-10-27. The near side is therefore no longer left to a sibling test.
            _feed.Report(Vector3.zero, 10f, false, false, Target);
            _feed.AdvanceAndSnapshot(0.1f, _scratch);
            _feed.Report(Vector3.zero, 4f, false, false, Target);
            _feed.AdvanceAndSnapshot(0f, _scratch);

            Assert.AreEqual(1, _scratch.Count, "0.1 s is inside the ten-frame window: one figure");
            Assert.AreEqual(14f, _scratch[0].Amount, 1e-4f, "and the second hit was added to it");

            // The far side: 1.8 s more (1.9 s of the figure's 2 s life) is well outside the window.
            _feed.AdvanceAndSnapshot(1.8f, _scratch);

            _feed.Report(Vector3.zero, 4f, false, false, Target);
            _feed.AdvanceAndSnapshot(0f, _scratch);

            Assert.AreEqual(2, _scratch.Count, "the first figure is still on screen beside the new one");
            Assert.AreEqual(14f, _scratch[0].Amount, 1e-4f, "and its total is frozen");
            Assert.AreEqual(4f, _scratch[1].Amount, 1e-4f, "the new figure starts from zero");
        }

        [Test]
        public void Accumulate_ForgetsTheSumAfterTenQuietFrames_ButTheFigureLingers()
        {
            // The control first: two hits inside the window are ONE figure holding the sum. Without it
            // this test passes with accumulation switched off — measured by mutation control on
            // 2026-10-27, which is the only reason the control is here rather than in a sibling.
            _feed.Report(Vector3.zero, 10f, false, false, Target);
            _feed.AdvanceAndSnapshot(0.1f, _scratch);
            _feed.Report(Vector3.zero, 5f, false, false, Target);
            _feed.AdvanceAndSnapshot(0f, _scratch);

            Assert.AreEqual(1, _scratch.Count, "inside the window the hits are one figure");
            Assert.AreEqual(15f, _scratch[0].Amount, 1e-4f, "and they add up");

            // The subtle half, and the one that looks like a bug until you read the oracle:
            //   `t_hitPart` counts down 10 frames and then clears `hitSumm`/`hitPart` (`:3222-3233`),
            //   while the PART itself lives 60 frames. So the total is forgotten long before the figure
            //   disappears, and the next burst starts a second figure over the first.

            // Past the window (1/3 s), well short of the lifetime (2 s).
            _feed.AdvanceAndSnapshot(DamageEventFeed.AccumulateWindow + 0.05f, _scratch);

            Assert.AreEqual(1, _scratch.Count, "the figure is still on screen");
            Assert.AreEqual(0, _feed.AccumulatingCount, "but it is no longer adding up");

            _feed.Report(Vector3.zero, 7f, false, false, Target);
            _feed.AdvanceAndSnapshot(0f, _scratch);

            Assert.AreEqual(2, _scratch.Count, "a new burst starts a new figure");
            Assert.AreEqual(7f, _scratch[1].Amount, 1e-4f, "and it starts from zero, not from 10");
        }

        [Test]
        public void Accumulate_CritEnlargesTheFigureAndKeepsItEnlarged()
        {
            // `hitPart.vis.scaleX = hitPart.vis.scaleY = 1.6 / cam.scaleV` (`Unit.as:3954`) — set, never
            // cleared, and never applied to a non-crit. So a crit anywhere in the burst makes the whole
            // total big.
            _feed.Report(Vector3.zero, 10f, isCritical: false, isMiss: false, Target);
            _feed.AdvanceAndSnapshot(0f, _scratch);
            Assert.AreEqual(1f, _scratch[0].Scale, 1e-4f);

            _feed.Report(Vector3.zero, 10f, isCritical: true, isMiss: false, Target);
            _feed.AdvanceAndSnapshot(0f, _scratch);
            Assert.AreEqual(DamageEventFeed.CriticalScale, _scratch[0].Scale, 1e-4f);

            _feed.Report(Vector3.zero, 10f, isCritical: false, isMiss: false, Target);
            _feed.AdvanceAndSnapshot(0f, _scratch);
            Assert.AreEqual(DamageEventFeed.CriticalScale, _scratch[0].Scale, 1e-4f,
                "a later ordinary hit must not shrink the figure back");
        }

        [Test]
        public void PerHit_PrintsOneFigurePerHit()
        {
            // AS3 `showHit == 1` — the other mode the PipBuck options page can select.
            _feed.Style = HitNumberStyle.PerHit;

            _feed.Report(Vector3.zero, 10f, false, false, Target);
            _feed.Report(Vector3.zero, 5f, false, false, Target);

            Assert.AreEqual(2, _feed.Count);
        }

        [Test]
        public void Off_RecordsNothing()
        {
            // AS3 `showHit == 0`. The ambient/damage-over-time path is the one exception there
            // (`Unit.as:3928` prints when `param4`), and that path does not reach this feed yet.
            _feed.Style = HitNumberStyle.Off;

            _feed.Report(Vector3.zero, 10f, false, false, Target);
            _feed.ReportText(Vector3.zero, "+5xp", frame: HitNumberFrame.Xp);

            Assert.AreEqual(0, _feed.Count);
        }

        [Test]
        public void Miss_IsSuppressedWhileABurstIsAccumulating()
        {
            // `showHit == 2 && t_hitPart == 0` is the gate on the miss figure (`Unit.as:4108`), so a
            // dodge in the middle of a burst prints nothing — the running total is the story.
            _feed.Report(Vector3.zero, 10f, false, false, Target);
            _feed.Report(Vector3.zero, 0f, isCritical: false, isMiss: true, Target);

            Assert.AreEqual(1, _feed.Count, "no MISS figure while the total is live");
        }

        [Test]
        public void Miss_PrintsWhenNothingIsAccumulating()
        {
            _feed.Report(Vector3.zero, 0f, isCritical: false, isMiss: true, Target, HitNumberFrame.Miss);

            Assert.AreEqual(1, _feed.Count);
            _feed.AdvanceAndSnapshot(0f, _scratch);
            Assert.AreEqual("MISS", DamageEventFeed.TextFor(_scratch[0]));
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
        public void Lifetime_IsSixtyFrames()
        {
            // The `numb` emitter's MinLiv, from the oracle's own particle table.
            Assert.AreEqual(2f, Lifetime, 1e-4f, "60 frames at 30 Hz");
        }

        [Test]
        public void AccumulateWindow_IsTenFrames()
        {
            // AS3 `t_hitPart = 10`, counted once per unit step.
            Assert.AreEqual(1f / 3f, DamageEventFeed.AccumulateWindow, 1e-4f);
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
            _feed.Report(Vector3.zero, 5f, false, false, Target);
            _feed.Clear();

            Assert.AreEqual(0, _feed.Count);
            Assert.AreEqual(0, _feed.AccumulatingCount, "the windows are state too");
            Assert.AreEqual(0, _feed.AdvanceAndSnapshot(0f, _scratch));
        }

        // ── The visual curve ─────────────────────────────────────────────────

        [Test]
        public void Alpha_IsAlwaysOne_TheOracleDoesNotFade()
        {
            // The `numb` emitter has `Alph: 0` and `PreAlph: 0`: the figure is opaque for all sixty
            // frames and then simply is not there. A fade would be a port invention.
            Assert.AreEqual(1f, DamageEventFeed.Alpha(0f, Lifetime), 1e-4f);
            Assert.AreEqual(1f, DamageEventFeed.Alpha(Lifetime * 0.5f, Lifetime), 1e-4f);
            Assert.AreEqual(1f, DamageEventFeed.Alpha(Lifetime, Lifetime), 1e-4f);
        }

        [Test]
        public void RiseOffset_IsTheBrakingSeries_NotALinearRamp()
        {
            // AS3 integrates `Y += dy; dy *= 0.98` (Part.as:161-169) from dy = 1 px/frame, so the
            // distance after n frames is (1 - 0.98^n) / 0.02 px — 35.1 px over the full 60 frames,
            // with most of it spent in the first half-second.
            Assert.AreEqual(0f, DamageEventFeed.RiseOffsetPixels(0f), 1e-4f);
            Assert.AreEqual(35.12f, DamageEventFeed.RiseOffsetPixels(DamageEventFeed.LifetimeFrames), 0.05f);

            // Concave, not straight: at the halfway frame it has already covered well over half the
            // distance. A linear curve would give 17.5 here.
            float halfway = DamageEventFeed.RiseOffsetPixels(DamageEventFeed.LifetimeFrames * 0.5f);
            Assert.Greater(halfway, 20f, "the brake makes the first half of the life cover most of the rise");
            Assert.Less(halfway, 35.12f);
        }

        [Test]
        public void RiseOffset_IsClamped_NotExtrapolated()
        {
            // Called with an age that can momentarily exceed the lifetime (the snapshot is taken
            // before the expiry sweep in some frames); an unclamped series would keep climbing.
            float atLife = DamageEventFeed.RiseOffsetPixels(DamageEventFeed.LifetimeFrames);
            Assert.AreEqual(atLife,
                            DamageEventFeed.RiseOffsetPixels(DamageEventFeed.LifetimeFrames * 3f), 1e-4f);
        }

        [Test]
        public void RiseOffsetWorld_IsThePixelRiseInUnityUnits()
        {
            // 35.1 px at 100 px per unit.
            Assert.AreEqual(0.3512f,
                            DamageEventFeed.RiseOffsetWorld(Lifetime), 1e-3f);
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
        public void TextFor_Rounds_AsTheOracleDoes()
        {
            // AS3 `Math.round(param1)` (Unit.as:3932, :3956). The port used to ceil; the oracle rounds,
            // and 0.5 is already the floor at which it prints anything at all (MinPrintedDamage).
            Assert.AreEqual("12", DamageEventFeed.TextFor(new DamageNumber(Vector3.zero, 11.6f, false, false, 0f)));
            Assert.AreEqual("11", DamageEventFeed.TextFor(new DamageNumber(Vector3.zero, 11.2f, false, false, 0f)));
            Assert.AreEqual("0", DamageEventFeed.TextFor(new DamageNumber(Vector3.zero, 0.4f, false, false, 0f)));
        }

        [Test]
        public void TextFor_CustomText_WinsOverTheNumber()
        {
            // "+120xp", "+40", a status word — all of these are casts with their own text.
            var xp = new DamageNumber(Vector3.zero, 0f, false, false, 0f,
                                      customText: "+120xp", customColor: null,
                                      frame: HitNumberFrame.Xp);

            Assert.AreEqual("+120xp", DamageEventFeed.TextFor(xp));
        }

        // ── The palette ──────────────────────────────────────────────────────

        [Test]
        public void ColorFor_MatchesTheOracleFrames()
        {
            // Sampled from Assets/_PFE/Art/Particles/visualNumb/f000..f010.png — frame n is file
            // f(n-1) — so these are the game's colours, not a palette invented beside it.
            AssertColor(255, 146, 13, HitNumberFrame.Damage);       // f000, orange
            AssertColor(255, 102, 102, HitNumberFrame.PlayerDamage); // f001, red
            AssertColor(200, 108, 255, HitNumberFrame.Ambient);      // f002, purple
            AssertColor(102, 255, 0, HitNumberFrame.Heal);           // f003, green
            AssertColor(153, 153, 153, HitNumberFrame.Miss);         // f009, grey
            AssertColor(251, 176, 247, HitNumberFrame.Pink);         // f010, pink
        }

        [Test]
        public void ColorFor_UnknownFrame_FallsBackToDamageOrange()
        {
            AssertColor(255, 146, 13, frame: 0);
            AssertColor(255, 146, 13, frame: 99);
        }

        // ── The shared instance ──────────────────────────────────────────────

        [Test]
        public void Default_IsASingleton_TheOverlayCanRead()
        {
            Assert.IsNotNull(DamageEventFeed.Default);
            Assert.AreSame(DamageEventFeed.Default, DamageEventFeed.Default,
                "the overlay has no wiring, so the default must be stable across reads");
        }

        [Test]
        public void Default_Style_IsAccumulate()
        {
            // World.as:214 — `showHit:int = 2`. If this ever drifts to PerHit, the game stops looking
            // like the oracle on every burst, and nothing else would go red.
            Assert.AreEqual(HitNumberStyle.Accumulate, new DamageEventFeed().Style);
        }

        private static void AssertColor(int r, int g, int b, int frame)
        {
            Color actual = DamageEventFeed.ColorFor(frame);
            var expected = new Color32((byte)r, (byte)g, (byte)b, 255);

            Assert.AreEqual(expected.r / 255f, actual.r, 1e-3f, $"frame {frame} red");
            Assert.AreEqual(expected.g / 255f, actual.g, 1e-3f, $"frame {frame} green");
            Assert.AreEqual(expected.b / 255f, actual.b, 1e-3f, $"frame {frame} blue");
        }

        private static List<float> Amounts(List<DamageNumber> numbers)
        {
            var result = new List<float>(numbers.Count);
            for (int i = 0; i < numbers.Count; i++) result.Add(numbers[i].Amount);
            return result;
        }
    }
}
