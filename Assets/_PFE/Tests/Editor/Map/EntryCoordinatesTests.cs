using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Tests for the <c>"x:y"</c> an entry override carries.
    ///
    /// <para><b>Why this is its own fixture.</b> The parse has one live caller in the whole data set —
    /// <c>&lt;s act="gotoland" val="raiders" n="1" opt1="2" opt2="2"/&gt;</c> (<c>RoomsProb.as:2562</c>) —
    /// and its failure mode is silent: an unparseable string falls back to the land's own entry cell, so
    /// the player simply arrives somewhere other than the script asked for. The only way to tell "the
    /// parser understood it" from "the parser gave up" is a positive control, and that needs the parser
    /// reachable without a <c>MonoBehaviour</c>.</para>
    ///
    /// <para><b>Oracle:</b> <c>Script.as:449-452</c> builds <c>opt1 + ":" + opt2</c>;
    /// <c>Land.as:1148-1167</c> splits on <c>":"</c> into <c>locX</c>/<c>locY</c>, treating a missing
    /// second part as <c>0</c>.</para>
    /// </summary>
    [TestFixture]
    public class EntryCoordinatesTests
    {
        /// <summary>Asserts the string parses, and returns the value — so a failed parse cannot be mistaken for (0,0,0).</summary>
        private static Vector3Int Parsed(string coordinates)
        {
            Vector3Int? parsed = EntryCoordinates.Parse(coordinates);
            Assert.That(parsed.HasValue, Is.True, $"'{coordinates ?? "<null>"}' should parse.");
            return parsed.Value;
        }

        private static void AssertRejected(string coordinates)
        {
            Assert.That(EntryCoordinates.TryParse(coordinates, out _), Is.False,
                $"'{coordinates ?? "<null>"}' must not parse.");
            Assert.That(EntryCoordinates.Parse(coordinates).HasValue, Is.False,
                "Parse must agree with TryParse — a caller using either must get the same answer.");
        }

        [Test]
        public void ParsesTheOneLiveExample()
        {
            // RoomsProb.as:2562 — the only `gotoland` in the game with n="1". If this fails, the sewer's
            // exit puts the player at raiders' default entry instead of (2, 2).
            Assert.AreEqual(new Vector3Int(2, 2, 0), Parsed("2:2"));
        }

        [Test]
        public void ParsesTwoDigitParts()
        {
            Assert.AreEqual(new Vector3Int(13, 7, 0), Parsed("13:7"));
        }

        [Test]
        public void AOnePartCoordinateIsXWithZeroY()
        {
            // Land.as:1159-1166: `if(length >= 2) locY = [1]; else locY = 0;` — AS3 accepts a bare x.
            Assert.AreEqual(new Vector3Int(5, 0, 0), Parsed("5"));
        }

        [Test]
        public void PartsPastTheSecondAreIgnored()
        {
            // Land.as only reads [0] and [1]; a third part is not an error in AS3, and inventing one
            // here would reject data the oracle accepts.
            Assert.AreEqual(new Vector3Int(1, 2, 0), Parsed("1:2:3"));
        }

        [Test]
        public void NegativeCoordinatesSurvive()
        {
            Assert.AreEqual(new Vector3Int(-1, 4, 0), Parsed("-1:4"));
        }

        [Test]
        public void NoOverrideIsNullRatherThanZeroZero()
        {
            // The distinction the caller depends on: null means "use the land's own entry cell".
            // Returning (0,0,0) would instead *move* the player to the grid origin.
            Assert.That(EntryCoordinates.Parse(null).HasValue, Is.False);
            Assert.That(EntryCoordinates.Parse(string.Empty).HasValue, Is.False);
            Assert.That(EntryCoordinates.Parse("   ").HasValue, Is.False);
        }

        [Test]
        public void ANonNumericPartIsRejectedRatherThanGuessed()
        {
            // AS3 would carry "abc" through as a string; the port cannot, and must not silently
            // substitute a number. Falling back to the land's own entry cell is the safe direction.
            AssertRejected("abc:2");
            AssertRejected("2:abc");
            AssertRejected("abc");

            // Positive control: the same shape with real numbers parses, so the assertions above are
            // about the digits and not about the split.
            Assert.AreEqual(new Vector3Int(2, 2, 0), Parsed("2:2"));
        }

        [Test]
        public void AStrayColonDoesNotProduceAPartialCoordinate()
        {
            // ":2" — parts[0] is empty. A parse that only validated parts[1] would return (0, 2, 0),
            // which is a real room and would look like it worked.
            AssertRejected(":2");
            AssertRejected("2:");
        }

        [Test]
        public void TryParse_LeavesTheOutAtDefaultOnFailure()
        {
            Assert.That(EntryCoordinates.TryParse("nonsense", out Vector3Int position), Is.False);
            Assert.AreEqual(default(Vector3Int), position,
                "A failing TryParse must not hand back a half-filled value the caller might use anyway.");
        }
    }
}
