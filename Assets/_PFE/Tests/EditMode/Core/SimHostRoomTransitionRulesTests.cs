using NUnit.Framework;
using PFE.Sim.Replication;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The host-side admission rule for a peer's "I changed rooms" declaration — the room-transition
    /// counterpart of <see cref="SimHostDoorRulesTests"/>. Wave 2B's gate: the declaration names a
    /// room and a world-pixel arrival position, and before this rule the host accepted both without
    /// checking that the position belongs to the room — any bug or out-of-order packet could teleport
    /// the peer's simulated body across the map. Each refusal reason is pinned so the overlay names
    /// the first broken link: an unknown room is a DIFFERENT bug from out-of-bounds coordinates.
    /// </summary>
    [TestFixture]
    public sealed class SimHostRoomTransitionRulesTests
    {
        private const bool Yes = true;
        private const bool No = false;

        // A default-size room at the world origin, as the view sink would supply it: 48x25 tiles of
        // 40 px each. The gate must honour whatever rect it is given, so the tests never assume it.
        private const float MinX = 0f, MinY = 0f;
        private const float MaxX = 1920f, MaxY = 1000f;
        private const float MidX = 960f, MidY = 500f;

        [Test]
        public void InsideBounds_Admits()
        {
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MidX, MidY, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.Admit));
        }

        [Test]
        public void MissingRoom_IsRefusedBeforeAnythingElse()
        {
            // Even coordinates that would sit perfectly inside the rect cannot be admitted when the
            // named room does not exist — the room is the first dependency.
            Assert.That(SimHostRoomTransitionRules.Admit(No, MidX, MidY, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.RefuseUnknownRoom));
        }

        [Test]
        public void LeftOfBounds_IsRefused()
        {
            Assert.That(SimHostRoomTransitionRules.Admit(Yes,
                    MinX - SimHostRoomTransitionRules.BoundsMarginPixels - 1f, MidY,
                    MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds));
        }

        [Test]
        public void RightOfBounds_IsRefused()
        {
            Assert.That(SimHostRoomTransitionRules.Admit(Yes,
                    MaxX + SimHostRoomTransitionRules.BoundsMarginPixels + 1f, MidY,
                    MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds));
        }

        [Test]
        public void BelowBounds_IsRefused()
        {
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MidX,
                    MinY - SimHostRoomTransitionRules.BoundsMarginPixels - 1f,
                    MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds));
        }

        [Test]
        public void AboveBounds_IsRefused()
        {
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MidX,
                    MaxY + SimHostRoomTransitionRules.BoundsMarginPixels + 1f,
                    MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds));
        }

        [Test]
        public void WithinMargin_Admits()
        {
            // Doorway arrival: the peer reports where it stands stepping through a door, which can
            // sit a few pixels outside the strict rect at the seam between two rooms.
            const float insideBand = 64f;
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MinX - insideBand, MidY, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.Admit), "left doorway band");
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MaxX + insideBand, MidY, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.Admit), "right doorway band");
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MidX, MinY - insideBand, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.Admit), "lower doorway band");
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MidX, MaxY + insideBand, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.Admit), "upper doorway band");
        }

        [Test]
        public void ExactlyAtMarginBoundary_Admits()
        {
            // The comparison is inclusive: the boundary point itself is the farthest admissible
            // arrival, one pixel past it is refused. Pinning inclusivity stops a boundary off-by-one
            // in either direction from shipping silently.
            float m = SimHostRoomTransitionRules.BoundsMarginPixels;
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MinX - m, MidY, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.Admit), "left boundary is inclusive");
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MaxX + m, MidY, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.Admit), "right boundary is inclusive");
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MidX, MinY - m, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.Admit), "lower boundary is inclusive");
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MidX, MaxY + m, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.Admit), "upper boundary is inclusive");
        }

        [Test]
        public void OnePixelPastMarginBoundary_IsRefused()
        {
            float m = SimHostRoomTransitionRules.BoundsMarginPixels;
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MinX - m - 1f, MidY, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds), "left");
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MaxX + m + 1f, MidY, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds), "right");
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MidX, MinY - m - 1f, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds), "lower");
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MidX, MaxY + m + 1f, MinX, MinY, MaxX, MaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds), "upper");
        }

        [Test]
        public void NonZeroOriginRoom_UsesTheSuppliedBounds()
        {
            // The view sink derives the rect from the room's live origin and its own width/height —
            // a room one cell right and one row down is at (1920, -1000)-(3840, 0). A point inside
            // the (0,0)-(1920,1000) rect but outside THIS rect must be refused, and a point inside
            // this rect must be admitted. This pins that Admit honours its arguments instead of any
            // hardcoded default room.
            const float roomMinX = 1920f, roomMinY = -1000f;
            const float roomMaxX = 3840f, roomMaxY = 0f;

            Assert.That(SimHostRoomTransitionRules.Admit(Yes, MidX, MidY, roomMinX, roomMinY, roomMaxX, roomMaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds),
                "a position in the origin room is out of bounds for the offset room");
            Assert.That(SimHostRoomTransitionRules.Admit(Yes, roomMinX + MidX, roomMinY + MidY,
                    roomMinX, roomMinY, roomMaxX, roomMaxY),
                Is.EqualTo(SimHostRoomTransitionVerdict.Admit),
                "a position inside the offset room is admitted");
        }

        [Test]
        public void Describe_NamesEveryVerdict()
        {
            foreach (SimHostRoomTransitionVerdict verdict in
                     System.Enum.GetValues(typeof(SimHostRoomTransitionVerdict)))
            {
                string text = SimHostRoomTransitionRules.Describe(verdict);
                Assert.That(text, Is.Not.Null.And.Not.Empty, "verdict " + verdict + " has no description");
                Assert.That(text, Does.Not.Contain("unknown ("), "verdict " + verdict + " fell through the switch");
            }
        }

        // ── Mutation control ───────────────────────────────────────────────────────────────

        // The naive shapes this rule exists to prevent, written out and shown to be caught by the
        // pinned tests above — so those tests are proven able to see the defect rather than merely
        // passing against the real implementation.

        /// <summary>Variant 1: the doorway margin dropped, so only the strict rect is checked.</summary>
        private static SimHostRoomTransitionVerdict MutantNoMargin(
            bool roomFound, float px, float py, float minX, float minY, float maxX, float maxY)
        {
            if (!roomFound) return SimHostRoomTransitionVerdict.RefuseUnknownRoom;
            if (px < minX || px > maxX || py < minY || py > maxY)
                return SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds;
            return SimHostRoomTransitionVerdict.Admit;
        }

        /// <summary>Variant 2: the Y axis never checked — an out-of-bounds height is admitted.</summary>
        private static SimHostRoomTransitionVerdict MutantXAxisOnly(
            bool roomFound, float px, float py, float minX, float minY, float maxX, float maxY)
        {
            const float m = SimHostRoomTransitionRules.BoundsMarginPixels;
            if (!roomFound) return SimHostRoomTransitionVerdict.RefuseUnknownRoom;
            if (px < minX - m || px > maxX + m)
                return SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds;
            return SimHostRoomTransitionVerdict.Admit;
        }

        /// <summary>Variant 3: the room-existence check dropped, so a phantom room is admitted.</summary>
        private static SimHostRoomTransitionVerdict MutantNoRoomCheck(
            bool roomFound, float px, float py, float minX, float minY, float maxX, float maxY)
        {
            const float m = SimHostRoomTransitionRules.BoundsMarginPixels;
            if (px < minX - m || px > maxX + m || py < minY - m || py > maxY + m)
                return SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds;
            return SimHostRoomTransitionVerdict.Admit;
        }

        /// <summary>Variant 4: the boundary comparisons flipped to exclusive, so a doorway arrival
        /// exactly on the margin edge is refused.</summary>
        private static SimHostRoomTransitionVerdict MutantExclusiveBoundary(
            bool roomFound, float px, float py, float minX, float minY, float maxX, float maxY)
        {
            const float m = SimHostRoomTransitionRules.BoundsMarginPixels;
            if (!roomFound) return SimHostRoomTransitionVerdict.RefuseUnknownRoom;
            if (px <= minX - m || px >= maxX + m || py <= minY - m || py >= maxY + m)
                return SimHostRoomTransitionVerdict.RefuseCoordinatesOutOfBounds;
            return SimHostRoomTransitionVerdict.Admit;
        }

        [Test]
        public void MutationControl_EachNaiveVariantIsCaughtByTheTestsAbove()
        {
            float m = SimHostRoomTransitionRules.BoundsMarginPixels;

            // Variant 1 (no margin): WithinMargin_Admits pins Admit at minX - 64, the mutant refuses
            // the same input — the margin band is exactly what it throws away.
            Assert.AreNotEqual(
                SimHostRoomTransitionRules.Admit(Yes, MinX - 64f, MidY, MinX, MinY, MaxX, MaxY),
                MutantNoMargin(Yes, MinX - 64f, MidY, MinX, MinY, MaxX, MaxY),
                "WithinMargin_Admits cannot separate the rule from a variant that drops the doorway " +
                "margin, so it is not a guard.");

            // Variant 2 (X axis only): BelowBounds_IsRefused pins Refuse at minY - margin - 1, the
            // mutant admits the same input.
            Assert.AreNotEqual(
                SimHostRoomTransitionRules.Admit(Yes, MidX, MinY - m - 1f, MinX, MinY, MaxX, MaxY),
                MutantXAxisOnly(Yes, MidX, MinY - m - 1f, MinX, MinY, MaxX, MaxY),
                "BelowBounds_IsRefused cannot separate the rule from a variant that never checks Y, " +
                "so it is not a guard.");

            // Variant 3 (no room check): MissingRoom_IsRefused pins RefuseUnknownRoom with no room,
            // the mutant admits the same input.
            Assert.AreNotEqual(
                SimHostRoomTransitionRules.Admit(No, MidX, MidY, MinX, MinY, MaxX, MaxY),
                MutantNoRoomCheck(No, MidX, MidY, MinX, MinY, MaxX, MaxY),
                "MissingRoom_IsRefusedBeforeAnythingElse cannot separate the rule from a variant " +
                "that skips the room-existence check, so it is not a guard.");

            // Variant 4 (exclusive boundary): ExactlyAtMarginBoundary_Admits pins Admit exactly at
            // minX - margin, the mutant refuses the same input.
            Assert.AreNotEqual(
                SimHostRoomTransitionRules.Admit(Yes, MinX - m, MidY, MinX, MinY, MaxX, MaxY),
                MutantExclusiveBoundary(Yes, MinX - m, MidY, MinX, MinY, MaxX, MaxY),
                "ExactlyAtMarginBoundary_Admits cannot separate the rule from a variant with " +
                "exclusive boundary comparisons, so it is not a guard.");
        }
    }
}
