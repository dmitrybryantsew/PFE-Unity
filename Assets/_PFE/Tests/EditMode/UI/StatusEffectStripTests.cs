using System.Collections.Generic;
using NUnit.Framework;
using PFE.UI.HUD;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins the status-effect strip — <see cref="StatusEffectStripRules"/> (the port of
    /// <c>GUI.setEffects</c>, <c>GUI.as:1140-1242</c>) and <see cref="StatusEffectIcons"/> (the id →
    /// frame table read out of sprite 4453's <c>FrameLabel</c> tags).
    ///
    /// <para><b>Why the icon table needs a fixture at all.</b> It is 55 hand-copied strings whose only
    /// source is a binary tag list, so a typo is invisible: an unknown id does not throw, it draws the
    /// blank first frame (<c>GUI.as:1223-1226</c>), so a misspelt <c>potion_rat</c> would show an empty
    /// slot with a countdown under it and nothing anywhere would be red. The tests below check the
    /// ends of the table, the offsets that are easy to be off by one, and that no two ids collide.</para>
    ///
    /// <para><b>And why the ordering needs one.</b> The oracle's cascade is nested and sets its
    /// one-shot flags on the way <i>in</i>, so "bleeding, poison, shield, then the list" is the
    /// behaviour and the nesting is an implementation detail. A port that re-derived the nesting gets
    /// the priority wrong in exactly the case a player sees most: a bleed plus a buff.</para>
    /// </summary>
    [TestFixture]
    public class StatusEffectStripTests
    {
        private static StatusEffectSlotDisplay[] NewSlots()
        {
            return new StatusEffectSlotDisplay[StatusEffectStripRules.SlotCount];
        }

        private static List<StatusEffectEntry> Effects(params StatusEffectEntry[] entries)
        {
            return new List<StatusEffectEntry>(entries);
        }

        // ── StatusEffectIcons: the frame table ────────────────────────────────

        [Test]
        public void Icons_CutIsFrameTwo_SoSpriteIndexOne()
        {
            // The first label in sprite 4453 is 'cut' on frame 2, and frame 1 is the blank fallback.
            Assert.AreEqual(1, StatusEffectIcons.IndexOf("cut"));
        }

        [Test]
        public void Icons_LastLabelIsPotionRatOnTheLastFrame()
        {
            // 'potion_rat' is the 55th label and frame 56 — the end of the sequence.
            Assert.AreEqual(StatusEffectIcons.IconCount - 1, StatusEffectIcons.IndexOf("potion_rat"));
            Assert.AreEqual(55, StatusEffectIcons.IndexOf("potion_rat"));
        }

        [Test]
        public void Icons_TheStripSpecialsAndFoodAreWhereTheOracleLooksForThem()
        {
            // These four are not effects at all: three hard-coded priority slots (GUI.as:1171-1197) and
            // the shared tip==3 icon (:1214-1217). Their frames are 2..5 and 46.
            Assert.AreEqual(2, StatusEffectIcons.IndexOf("poison"));
            Assert.AreEqual(4, StatusEffectIcons.IndexOf("shit"));
            Assert.AreEqual(5, StatusEffectIcons.IndexOf("burning"));
            Assert.AreEqual(45, StatusEffectIcons.IndexOf("food"));
        }

        [Test]
        public void Icons_EveryFrameInTheSequenceIsClaimedExactlyOnce()
        {
            // A duplicate would make one of the two ids unreachable, and the sprite sequence has no
            // repeated art to hint at it.
            var seen = new HashSet<int>();
            int claimed = 0;

            for (int index = 1; index < StatusEffectIcons.IconCount; index++)
            {
                string id = StatusEffectIcons.IdAt(index);
                Assert.IsNotNull(id, "sprite index " + index + " has no label");
                Assert.IsTrue(seen.Add(index), "sprite index " + index + " claimed twice");
                Assert.AreEqual(index, StatusEffectIcons.IndexOf(id),
                    "round-trip failed for '" + id + "'");
                claimed++;
            }

            Assert.AreEqual(StatusEffectIcons.IconCount - 1, claimed);
            Assert.IsNull(StatusEffectIcons.IdAt(0), "frame 1 is the blank fallback and has no label");
        }

        [Test]
        public void Icons_UnknownIdDrawsTheBlankFrameRatherThanHidingTheSlot()
        {
            // GUI.as:1223-1226: the gotoAndStop throws and the catch writes frame 1. A hidden slot would
            // silently drop a running effect from the strip; an empty icon with a timer does not.
            Assert.AreEqual(StatusEffectIcons.Blank, StatusEffectIcons.IndexOf("not_an_effect"));
            Assert.AreEqual(StatusEffectIcons.Blank, StatusEffectIcons.IndexOf(null));
            Assert.AreEqual(StatusEffectIcons.Blank, StatusEffectIcons.IndexOf(string.Empty));
            Assert.IsFalse(StatusEffectIcons.IsKnown("not_an_effect"));
            Assert.IsTrue(StatusEffectIcons.IsKnown("drunk"));
        }

        // ── StatusEffectStripRules: the cascade ───────────────────────────────

        [Test]
        public void Strip_EmptyStateHidesEverySlot()
        {
            var slots = NewSlots();

            StatusEffectStripRules.Build(0f, 0f, 0f, Effects(), slots);

            foreach (StatusEffectSlotDisplay slot in slots)
            {
                Assert.IsTrue(slot.IsHidden);
            }
        }

        [Test]
        public void Strip_PriorityIsCutThenPoisonThenShieldThenTheList()
        {
            var slots = NewSlots();

            StatusEffectStripRules.Build(
                2.5f, 3.25f, 40f,
                Effects(new StatusEffectEntry("burning", 90)),
                slots);

            Assert.AreEqual(StatusEffectIcons.IndexOf("cut"), slots[0].IconIndex);
            Assert.AreEqual("2.5", slots[0].Text);

            Assert.AreEqual(StatusEffectIcons.IndexOf("poison"), slots[1].IconIndex);
            Assert.AreEqual("3.3", slots[1].Text);

            Assert.AreEqual(StatusEffectIcons.IndexOf("shit"), slots[2].IconIndex);
            Assert.AreEqual("40", slots[2].Text);

            Assert.AreEqual(StatusEffectIcons.IndexOf("burning"), slots[3].IconIndex);
            Assert.AreEqual("3", slots[3].Text);   // 90 ticks / 30

            Assert.IsTrue(slots[4].IsHidden);
        }

        [Test]
        public void Strip_ASpecialThatIsAbsentDoesNotLeaveAGap()
        {
            // The oracle's t1 flag is set even when cut is 0, so poison takes slot 0 rather than slot 1.
            var slots = NewSlots();

            StatusEffectStripRules.Build(0f, 1.5f, 0f, Effects(), slots);

            Assert.AreEqual(StatusEffectIcons.IndexOf("poison"), slots[0].IconIndex);
            Assert.AreEqual("1.5", slots[0].Text);
        }

        [Test]
        public void Strip_EachSpecialAppearsAtMostOnce()
        {
            var slots = NewSlots();

            // cut is considered on the first slot and never again, whatever the value.
            StatusEffectStripRules.Build(
                9f, 0f, 0f,
                Effects(
                    new StatusEffectEntry("rage", 60),
                    new StatusEffectEntry("mint", 60)),
                slots);

            Assert.AreEqual(StatusEffectIcons.IndexOf("cut"), slots[0].IconIndex);
            Assert.AreEqual(StatusEffectIcons.IndexOf("rage"), slots[1].IconIndex);
            Assert.AreEqual(StatusEffectIcons.IndexOf("mint"), slots[2].IconIndex);

            for (int i = 3; i < StatusEffectStripRules.SlotCount; i++)
            {
                Assert.IsTrue(slots[i].IsHidden, "slot " + i + " should be empty");
            }
        }

        [Test]
        public void Strip_AnEffectBeingUnsetBlocksTheTailInsteadOfBeingSkipped()
        {
            // GUI.as:1201-1228 — `n` only advances on a draw, so a vse entry at the head leaves every
            // remaining slot empty for that frame. This is the one place the port must NOT be helpful.
            var slots = NewSlots();

            StatusEffectStripRules.Build(
                0f, 0f, 0f,
                Effects(
                    new StatusEffectEntry("rage", 60, beingUnset: true),
                    new StatusEffectEntry("mint", 60)),
                slots);

            for (int i = 0; i < StatusEffectStripRules.SlotCount; i++)
            {
                Assert.IsTrue(slots[i].IsHidden, "slot " + i + " must stay empty behind a vse effect");
            }
        }

        [Test]
        public void Strip_ForeverPrintsTheInfinityMark()
        {
            var slots = NewSlots();

            StatusEffectStripRules.Build(
                0f, 0f, 0f,
                Effects(new StatusEffectEntry("bloodinv", 30, forever: true)),
                slots);

            Assert.AreEqual(StatusEffectIcons.IndexOf("bloodinv"), slots[0].IconIndex);
            Assert.AreEqual(StatusEffectStripRules.ForeverText, slots[0].Text);
        }

        [Test]
        public void Strip_FoodChannelUsesTheSharedFoodIcon()
        {
            // tip == 3 draws 'food' whatever the id is (GUI.as:1214-1217).
            var slots = NewSlots();

            StatusEffectStripRules.Build(
                0f, 0f, 0f,
                Effects(new StatusEffectEntry("hydra", 300, isFood: true)),
                slots);

            Assert.AreEqual(StatusEffectIcons.IndexOf("food"), slots[0].IconIndex);
            Assert.AreEqual("10", slots[0].Text);
        }

        [Test]
        public void Strip_CountdownIsFlooredSeconds()
        {
            var slots = NewSlots();

            StatusEffectStripRules.Build(
                0f, 0f, 0f,
                Effects(
                    new StatusEffectEntry("rage", 29),
                    new StatusEffectEntry("mint", 30),
                    new StatusEffectEntry("dash", 31),
                    new StatusEffectEntry("buck", 599)),
                slots);

            Assert.AreEqual("0", slots[0].Text);
            Assert.AreEqual("1", slots[1].Text);
            Assert.AreEqual("1", slots[2].Text);
            Assert.AreEqual("19", slots[3].Text);
        }

        [Test]
        public void Strip_StopsAtTwelveSlotsAndIgnoresTheRest()
        {
            var many = new List<StatusEffectEntry>();
            for (int i = 0; i < 20; i++)
            {
                many.Add(new StatusEffectEntry("rage", 60));
            }

            var slots = NewSlots();
            StatusEffectStripRules.Build(1f, 1f, 1f, many, slots);

            // Three specials plus nine effects fill the strip exactly.
            Assert.IsFalse(slots[StatusEffectStripRules.SlotCount - 1].IsHidden);
            Assert.AreEqual(StatusEffectStripRules.SlotCount, slots.Length);
        }

        [Test]
        public void Strip_MagnitudeRoundsHalfAwayFromZeroLikeFlash()
        {
            // Math.round(2.25 * 10) / 10 is 2.3 in AS3; .NET's default banker's rounding gives 2.2.
            var slots = NewSlots();

            StatusEffectStripRules.Build(2.25f, 0f, 0f, Effects(), slots);
            Assert.AreEqual("2.3", slots[0].Text);
        }

        // ── ManaReadout: the budget text ──────────────────────────────────────

        [Test]
        public void ManaText_ThreeBranches()
        {
            Assert.AreEqual("OUT", ManaReadout.Format(0f, "Magic", "OUT"));
            Assert.AreEqual("OUT", ManaReadout.Format(9.9f, "Magic", "OUT"));
            Assert.AreEqual("Magic 1%", ManaReadout.Format(10f, "Magic", "OUT"));
            Assert.AreEqual("Magic 34%", ManaReadout.Format(340f, "Magic", "OUT"));
            Assert.AreEqual("Magic 99%", ManaReadout.Format(994f, "Magic", "OUT"));

            // 995..1000 is silent — a full pool prints nothing at all (GUI.as:995-998).
            Assert.AreEqual(string.Empty, ManaReadout.Format(995f, "Magic", "OUT"));
            Assert.AreEqual(string.Empty, ManaReadout.Format(ManaReadout.MaxBudget, "Magic", "OUT"));
        }

        // ── ExperienceReadout: the bar ────────────────────────────────────────

        [Test]
        public void XpBar_IsProgressBetweenTheTwoCumulativeThresholds()
        {
            // Level 1: xpProgress(0) = 0, xpProgress(1) = 5000 (xpDelta).
            Assert.AreEqual(0f, ExperienceReadout.Fraction(0, 0, 5000), 1e-4f);
            Assert.AreEqual(0.5f, ExperienceReadout.Fraction(2500, 0, 5000), 1e-4f);
            Assert.AreEqual(1f, ExperienceReadout.Fraction(5000, 0, 5000), 1e-4f);

            // Level 2: prev = 5000, next = 15000 — the total is cumulative, not per level.
            Assert.AreEqual(0f, ExperienceReadout.Fraction(5000, 5000, 15000), 1e-4f);
            Assert.AreEqual(0.5f, ExperienceReadout.Fraction(10000, 5000, 15000), 1e-4f);
        }

        [Test]
        public void XpBar_ClampsAndSurvivesADegenerateRange()
        {
            Assert.AreEqual(0f, ExperienceReadout.Fraction(-500, 5000, 15000));
            Assert.AreEqual(1f, ExperienceReadout.Fraction(99999, 5000, 15000));
            Assert.AreEqual(0f, ExperienceReadout.Fraction(100, 5000, 5000));
        }
    }
}
