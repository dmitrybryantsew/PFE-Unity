using NUnit.Framework;
using PFE.Entities.Enemies.Archetypes;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="AlicornBossWPosRules"/> — the port of AS3 <c>BlitAnim.wPosAlicornBoss</c>
    /// (<c>BlitAnim.as:1834-1990</c>) and the lookup <c>UnitBossAlicorn.setWeaponPos</c> performs on it
    /// (<c>UnitBossAlicorn.as:174-189</c>).
    ///
    /// <para><b>The defect these guard.</b> All four of the boss's weapons were mounted at
    /// <see cref="WeaponHoldRule.Base"/> — <c>Y - scY/2</c> — because the table was not imported. On a
    /// 170 px sprite that is a barrel at the belly; the oracle's anchor is at the horn, 129 px above the
    /// feet and 41 px forward, and it moves with the breathing and the wing beat. The last two fixtures
    /// here pin exactly that difference, using the real numbers rather than the old behaviour.</para>
    ///
    /// <para><b>Every expected value is transcribed from the oracle's array</b>, and the two that matter
    /// most are chosen to be <i>discriminating</i>: a fixture that only checks "row 0 frame 0 is 146,21"
    /// would pass on a table whose other 53 entries were all wrong, so the values below are taken from
    /// different rows, at frames the short row still reaches, and from a frame only the clamp can
    /// produce.</para>
    /// </summary>
    [TestFixture]
    public class AlicornBossWPosRulesTests
    {
        /// <summary><c>bossalicorn</c>'s sheet: <c>sprAlicorn4</c>, 20 columns of 170x170.</summary>
        private const int Columns = 20;

        /// <summary><c>&lt;vis blit='sprAlicorn4' sprX='170' sprY='170' sprDX='105' sprDY='150'/&gt;</c>.</summary>
        private static readonly Vector2Int Registration = new Vector2Int(105, 150);
        private static readonly Vector2Int CellSize     = new Vector2Int(170, 170);

        // ── The table itself ─────────────────────────────────────────────────

        /// <summary>
        /// Three rows, 20 / 14 / 20 entries — <c>stay</c>, <c>fly</c>, <c>die</c>, in sheet-row order
        /// (<c>AllData.as:706-708</c>).
        /// </summary>
        /// <remarks>
        /// <b>The 20 on the last row is not a typo.</b> The oracle's <c>die</c> blit is <c>len='14'</c>
        /// while the table carries 20 entries for that row; the six extra are unreachable. Trimming them
        /// would make the table "tidier" and disagree with the artefact, so the length is pinned instead
        /// and <see cref="DieRow_IsNotTrimmedToItsClipsLength"/> pins what the lookup does with the
        /// overlap.
        /// </remarks>
        [Test]
        public void Rows_AreTheOraclesThreeRowsWithItsOwnLengths()
        {
            Assert.That(AlicornBossWPosRules.RowCount, Is.EqualTo(3),
                "stay / fly / die are rows 0 / 1 / 2.");
            Assert.That(AlicornBossWPosRules.Rows[0].Length, Is.EqualTo(20), "row 0 (stay)");
            Assert.That(AlicornBossWPosRules.Rows[1].Length, Is.EqualTo(14), "row 1 (fly)");
            Assert.That(AlicornBossWPosRules.Rows[2].Length, Is.EqualTo(20), "row 2 (die), 20 authored");
        }

        /// <summary>
        /// The <c>stay</c> row's drift: it starts and ends on <c>(146, 21)</c> and peaks at
        /// <c>(150, 23)</c> in the middle.
        /// </summary>
        /// <remarks>
        /// A loop that starts and ends on the same value is what makes <c>rep='1'</c> seamless, and the
        /// two-frame-wide peak is what a hand-typed table most easily flattens into a constant.
        /// </remarks>
        [Test]
        public void StayRow_StartsAndEndsOnTheSameAnchorAndPeaksInTheMiddle()
        {
            Assert.That(AlicornBossWPosRules.Rows[0][0],  Is.EqualTo(new Vector2Int(146, 21)));
            Assert.That(AlicornBossWPosRules.Rows[0][10], Is.EqualTo(new Vector2Int(150, 23)));
            Assert.That(AlicornBossWPosRules.Rows[0][19], Is.EqualTo(new Vector2Int(146, 21)));
        }

        /// <summary>
        /// The <c>fly</c> row is a flat x with a 2 px vertical wing beat — and it is <b>short</b>: 14
        /// entries in a 20-column sheet.
        /// </summary>
        [Test]
        public void FlyRow_HoldsItsXAndSwingsTwoPixelsVertically()
        {
            for (int frame = 0; frame < AlicornBossWPosRules.Rows[1].Length; frame++)
            {
                Assert.That(AlicornBossWPosRules.Rows[1][frame].x, Is.EqualTo(152),
                    $"row 1 frame {frame}: the fly anchor never moves horizontally");
            }

            Assert.That(AlicornBossWPosRules.Rows[1][0].y,  Is.EqualTo(29), "the beat's low point");
            Assert.That(AlicornBossWPosRules.Rows[1][4].y,  Is.EqualTo(27), "the beat's high point");
            Assert.That(AlicornBossWPosRules.Rows[1][13].y, Is.EqualTo(28), "the last authored frame");
        }

        /// <summary>
        /// The <c>die</c> row begins on the <c>fly</c> row's first anchor — the transition from hovering
        /// to falling is continuous — and then travels a long way, ending at <c>(80, 11)</c>.
        /// </summary>
        /// <remarks>
        /// The first half of this is a <b>continuity</b> claim (the two rows share <c>(152, 29)</c>), and
        /// the second half is the <b>positive control</b> that stops the first half from passing on a
        /// table where every row is a copy of row 0: frame 13 of the die row is <c>(80, 11)</c>, nothing
        /// like row 0's <c>(147, 21)</c>.
        /// </remarks>
        [Test]
        public void DieRow_StartsWhereTheFlyRowDoesAndThenFallsAway()
        {
            Assert.That(AlicornBossWPosRules.Rows[2][0], Is.EqualTo(AlicornBossWPosRules.Rows[1][0]),
                "the first dying frame reuses the flying anchor");

            Assert.That(AlicornBossWPosRules.Rows[2][13], Is.EqualTo(new Vector2Int(80, 11)));
            Assert.That(AlicornBossWPosRules.Rows[2][13], Is.Not.EqualTo(AlicornBossWPosRules.Rows[0][13]),
                "the die row is not a copy of the stay row");
        }

        // ── The lookup ───────────────────────────────────────────────────────

        /// <summary>
        /// The row comes from <c>cellIndex / columns</c> and the frame from <c>cellIndex % columns</c> —
        /// i.e. from the <b>sheet</b>, not from the animation's name.
        /// </summary>
        /// <remarks>
        /// This is the trap <c>BlitAnim</c> sets: <c>anims["die"].id</c> is <c>2</c> because the
        /// constructor assigns <c>this.id = param1.@y</c> (<c>BlitAnim.as:3686</c>) — the sheet row, not
        /// the string key. A port that looked up by animation name would index a table that has no such
        /// key and answer nothing at all.
        /// </remarks>
        [Test]
        public void RowAndFrame_ComeFromTheSheetCellIndex()
        {
            // Row 0, frame 3 — `stay` (146,21 146,21 147,21 148,22 …).
            Assert.That(AlicornBossWPosRules.TryResolveCell(3, Columns, out Vector2Int stay), Is.True);
            Assert.That(stay, Is.EqualTo(new Vector2Int(148, 22)));

            // Row 1, frame 5 — cell 25 is `fly`'s sixth entry.
            Assert.That(AlicornBossWPosRules.TryResolveCell(Columns + 5, Columns, out Vector2Int fly), Is.True);
            Assert.That(fly, Is.EqualTo(new Vector2Int(152, 27)));

            // Row 2, frame 1 — cell 41 is `die`'s second entry, the first frame of the fall.
            Assert.That(AlicornBossWPosRules.TryResolveCell(2 * Columns + 1, Columns, out Vector2Int die), Is.True);
            Assert.That(die, Is.EqualTo(new Vector2Int(143, 21)));
        }

        /// <summary>
        /// A cell whose column lies past the row's own length is clamped to that row's last entry,
        /// because the oracle would read <c>undefined</c> there and throw.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a clamp and not the oracle's <c>catch</c>.</b> The two differ only where the
        /// animation never goes: the <c>fly</c> clip is 14 frames long, so column 19 of row 1 is not a
        /// cell any animation produces. Clamping keeps the weapon on the row's last authored frame —
        /// where the animation itself is holding — while the <c>catch</c> would snap it to the belly.
        /// Both are guesses about an unreachable state; only one of them moves the weapon.</para>
        ///
        /// <para>The assertion is on the <b>last authored</b> value, so a port that read past the row
        /// (returning <c>default</c>, or row 2's entry at the same index) fails.</para>
        /// </remarks>
        [Test]
        public void ACellPastTheRowsOwnLength_ClampsToItsLastEntry()
        {
            Assert.That(AlicornBossWPosRules.TryResolveCell(Columns + 19, Columns, out Vector2Int clamped), Is.True);
            Assert.That(clamped, Is.EqualTo(AlicornBossWPosRules.Rows[1][13]),
                "column 19 of a 14-entry row resolves to the row's last authored anchor");
            Assert.That(clamped, Is.Not.EqualTo(default(Vector2Int)));
        }

        /// <summary>
        /// The two rows are odd for <b>opposite</b> reasons, and the lookup treats them differently:
        /// row 1 is <b>shorter</b> than the sheet (14 of 20 columns) and clamps, while row 2 is
        /// <b>longer</b> than its clip (20 entries for a 14-frame <c>die</c>) and is returned as authored.
        /// </summary>
        /// <remarks>
        /// <para><b>This fixture started out asserting the opposite, and the harness caught it.</b> I
        /// wrote it expecting the clamp to answer entry 13 for column 15 — but the clamp is bounded by
        /// the <i>table row's</i> length (20), not by the animation's. So column 15 of row 2 resolves to
        /// the authored <c>(79, 11)</c>. The entries are dead because
        /// <c>&lt;blit id='die' y='2' len='14'/&gt;</c> (<c>AllData.as:708</c>) never drives the frame
        /// counter past 13, <b>not</b> because the lookup refuses them. That distinction is worth a test:
        /// a future "tidy up the table" edit that trims row 2 to 14 entries would be invisible in play
        /// and would silently change what the lookup answers.</para>
        ///
        /// <para>The clamp's real work is therefore row 1, and
        /// <see cref="ACellPastTheRowsOwnLength_ClampsToItsLastEntry"/> is the fixture for it.</para>
        /// </remarks>
        [Test]
        public void DieRow_IsNotTrimmedToItsClipsLength()
        {
            Assert.That(AlicornBossWPosRules.Rows[2][15], Is.EqualTo(new Vector2Int(79, 11)),
                "the authored entry, which the 14-frame clip never reaches");

            Assert.That(AlicornBossWPosRules.TryResolveCell(2 * Columns + 15, Columns, out Vector2Int resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(new Vector2Int(79, 11)),
                "the lookup answers the authored entry: the row is 20 long, so nothing clamps");
            Assert.That(resolved, Is.Not.EqualTo(AlicornBossWPosRules.Rows[2][13]),
                "and it is not entry 13, which is what a clip-length clamp would have answered");
        }

        /// <summary>
        /// Everything outside the table answers <c>false</c> — the oracle's <c>catch</c> branch, which
        /// sends the weapon to the inherited body hold.
        /// </summary>
        [Test]
        public void CellsOutsideTheTable_AnswerFalse()
        {
            Assert.That(AlicornBossWPosRules.TryResolveCell(-1, Columns, out _), Is.False,
                "CurrentCellIndex is -1 before the first blit");
            Assert.That(AlicornBossWPosRules.TryResolveCell(0, 0, out _), Is.False,
                "a definition with no imported sheet geometry must not divide by zero");
            Assert.That(AlicornBossWPosRules.TryResolveCell(3 * Columns, Columns, out _), Is.False,
                "there is no fourth row");
        }

        // ── What the anchor is worth ─────────────────────────────────────────

        /// <summary>
        /// The anchor in room pixels is <b>41 px forward and 129 px above the feet</b> — the horn, not the
        /// belly — and that is the number the whole port turns on.
        /// </summary>
        /// <remarks>
        /// <para>Composed exactly as <c>AlicornBossHoldPoint.TryResolveHoldPoint</c> composes it:
        /// <see cref="AlicornBossWPosRules.TryResolveCell"/> then
        /// <see cref="GriffinArmMath.ArmOffsetPixels"/>, which is the same two AS3 lines as the alicorn's
        /// override (<c>UnitMerc.as:229-230</c> vs <c>UnitBossAlicorn.as:181-183</c>) including the
        /// <c>visBmp</c> rule and the single y flip.</para>
        ///
        /// <para>Worked out by hand from the oracle's own numbers: <c>sprDX/sprDY</c> is
        /// <c>(105, 150)</c>, so <c>visBmp = (-105, -150)</c>; <c>146 - 105 = 41</c> and
        /// <c>-(21 + -150) = +129</c> in room pixels, i.e. up.</para>
        /// </remarks>
        [Test]
        public void TheAnchorSitsAtTheHorn_NotAtTheBelly()
        {
            Assert.That(AlicornBossWPosRules.TryResolveCell(0, Columns, out Vector2Int wPos), Is.True);

            Vector2 offsetPixels = GriffinArmMath.ArmOffsetPixels(wPos, Registration, CellSize);

            Assert.That(offsetPixels.x, Is.EqualTo(41f).Within(0.01f), "forward of the feet");
            Assert.That(offsetPixels.y, Is.EqualTo(129f).Within(0.01f), "above the feet (room y is up)");
        }

        /// <summary>
        /// The anchor is <b>not</b> what <see cref="WeaponHoldRule.WPosTable"/> falls back to — and that
        /// is why the hold-point source has to exist rather than a rule being enough.
        /// </summary>
        /// <remarks>
        /// <para><c>ResolveHold</c> maps <c>WPosTable</c> to the base hold, which is
        /// <c>Y - scY/2</c> = 50 px above the feet for a 100 px body — <b>79 px below</b> the real anchor
        /// and with no forward offset at all. This fixture is the arithmetic proof that naming the rule
        /// cannot be the fix, so nobody "simplifies" <see cref="AlicornBossHoldPoint"/> away.</para>
        ///
        /// <para>Body height 1.0 is <c>bossalicorn</c>'s <c>&lt;phis sX='100' sY='100'&gt;</c>
        /// (<c>AllData.as:700</c>) — not the 170 px sprite, which is a different number and the reason
        /// the base rule is so far off.</para>
        /// </remarks>
        [Test]
        public void TheRuleFallbackIsNotTheAnchor_WhichIsWhyTheSourceExists()
        {
            Vector2 fallback = WeaponHoldPointMath.ResolveHold(
                WeaponHoldRule.WPosTable, ownerX: 0f, feetY: 0f, bodyHeight: 1f, facingSign: 1f);

            Assert.That(fallback, Is.EqualTo(new Vector2(0f, 0.5f)),
                "WPosTable falls back to the base hold — 50 px up, straight above the feet");

            float anchorY = 129f / WeaponHoldPointMath.FlashPixelsPerUnit;
            float anchorX = 41f / WeaponHoldPointMath.FlashPixelsPerUnit;

            Assert.That(anchorY - fallback.y, Is.EqualTo(0.79f).Within(0.01f),
                "the real anchor is 0.79 units higher than the fallback");
            Assert.That(anchorX, Is.EqualTo(0.41f).Within(0.01f),
                "and 0.41 units forward, which the fallback has no way to express");
        }
    }
}
