using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Tests.Editor.UnitAnimation
{
    /// <summary>
    /// Tests for the animation stepping rule — <see cref="AnimationFrame.Step"/>,
    /// <see cref="AnimationFrame.CellFor"/>, <see cref="AnimationFrame.Restart"/> — and for
    /// <see cref="UnitAnimator"/>'s indexing on top of it.
    ///
    /// <para>These pin the parts of <c>BlitAnim.step()</c> that fail <i>plausibly</i>. Three of them
    /// produce a moving sprite that is simply wrong rather than an error: wrapping a replay to
    /// <c>firstFrame</c> instead of <c>returnFrame</c>, rounding the fractional cursor instead of
    /// truncating it, and letting a row past the sheet's end wrap onto the next row's art. None of those
    /// would appear in a log.</para>
    ///
    /// <para>The numbers come from the real data. <c>gutsy1</c> is the case worth knowing: its
    /// <c>stay</c> is <c>&lt;blit id='stay' len='40' rep='0'/&gt;</c>, which — because <c>rep</c> is
    /// <i>presence</i>-tested — is a 40-cell loop on row 0, over a <c>sprGutsy1</c> sheet of 40 columns by
    /// 2 rows. <c>walk</c> is where the fractional steps live: <c>df='0.5'</c> with
    /// <c>ff='1' rf='2'</c>.</para>
    /// </summary>
    public class UnitAnimationStepTests
    {
        /// <summary>A frame cursor plus its stopped flag, since <c>Step</c> drives both by reference.</summary>
        struct Cursor
        {
            public float Frame;
            public bool Stopped;

            public int Cell(AnimationFrame state) => state.CellFor(Frame);
        }

        static Cursor At(AnimationFrame state)
        {
            var cursor = new Cursor();
            state.Restart(ref cursor.Frame, ref cursor.Stopped);
            return cursor;
        }

        // ── Step: the bound is firstFrame + length - 1 ───────────────────────

        [Test]
        public void Step_AdvancesByTheFrameStep_WhileBelowTheLastUsableCell()
        {
            var state = new AnimationFrame { row = 0, length = 3, firstFrame = 0, frameStep = 1f };
            Cursor cursor = At(state);

            Assert.That(cursor.Frame, Is.EqualTo(0f), "restart() puts the cursor on firstFrame");

            state.Step(ref cursor.Frame, ref cursor.Stopped);
            Assert.That(cursor.Frame, Is.EqualTo(1f));
            Assert.That(cursor.Stopped, Is.False, "cell 1 of 3 is not the last usable cell");

            state.Step(ref cursor.Frame, ref cursor.Stopped);
            Assert.That(cursor.Frame, Is.EqualTo(2f));
            Assert.That(cursor.Stopped, Is.False);
        }

        [Test]
        public void Step_AtTheLastUsableCell_WithoutReplay_Stops()
        {
            // len='3' means cells 0,1,2 are usable, so 2 is the last one — the bound is
            // firstf + maxf - 1 = 2, not firstf + maxf = 3.
            var state = new AnimationFrame { row = 0, length = 3, firstFrame = 0, frameStep = 1f };
            Cursor cursor = At(state);

            state.Step(ref cursor.Frame, ref cursor.Stopped);
            state.Step(ref cursor.Frame, ref cursor.Stopped);
            state.Step(ref cursor.Frame, ref cursor.Stopped);

            Assert.That(cursor.Frame, Is.EqualTo(2f), "the cursor must not leave the usable range");
            Assert.That(cursor.Stopped, Is.True);
        }

        [Test]
        public void Step_OnceStopped_KeepsHoldingTheLastCell()
        {
            // The oracle still calls step() every tick; on a stopped state the bound test is false and
            // replay is false, so it re-sets st = true and the cursor does not move. A unit whose death
            // animation has finished must hold its last frame, not rewind or drift.
            var state = new AnimationFrame { row = 0, length = 2, firstFrame = 0, frameStep = 1f };
            Cursor cursor = At(state);

            for (int i = 0; i < 6; i++)
            {
                state.Step(ref cursor.Frame, ref cursor.Stopped);
            }

            Assert.That(cursor.Frame, Is.EqualTo(1f));
            Assert.That(cursor.Stopped, Is.True);
        }

        [Test]
        public void Step_WithReplay_WrapsToTheReturnFrame_NotTheFirstFrame()
        {
            // `walk` really is `<blit id='walk' len='9' ff='1' rf='2' df='0.5' rep='1'/>`. ff=1 means the
            // usable cells are 1..9 and rf=2 means the loop restarts at cell 2 — cell 1 is the wind-up,
            // shown once. Wrapping to firstFrame instead would replay the wind-up every cycle.
            var state = new AnimationFrame
            {
                row = 0, length = 9, firstFrame = 1, returnFrame = 2, frameStep = 1f, replay = true
            };
            Cursor cursor = At(state);

            Assert.That(cursor.Frame, Is.EqualTo(1f));

            for (int i = 0; i < 8; i++)
            {
                state.Step(ref cursor.Frame, ref cursor.Stopped);
            }

            Assert.That(cursor.Frame, Is.EqualTo(9f), "cell 9 is the last usable cell (ff + len - 1)");
            Assert.That(cursor.Stopped, Is.False, "a replaying state never stops");

            state.Step(ref cursor.Frame, ref cursor.Stopped);

            Assert.That(cursor.Frame, Is.EqualTo(2f), "the loop restarts at rf, not at ff");
            Assert.That(cursor.Stopped, Is.False);
        }

        [Test]
        public void Step_ReplayWithNoReturnFrame_WrapsToCellZero()
        {
            // retf's declared default is 0 (BlitAnim.as:3678), so `rep` with no `rf` loops to the row's
            // first cell. 86 of the 176 rows carry `rep`, and only 28 carry `rf`.
            var state = new AnimationFrame { row = 0, length = 2, firstFrame = 0, frameStep = 1f, replay = true };
            Cursor cursor = At(state);

            state.Step(ref cursor.Frame, ref cursor.Stopped);
            state.Step(ref cursor.Frame, ref cursor.Stopped);

            Assert.That(cursor.Frame, Is.EqualTo(0f));
        }

        [Test]
        public void Step_IsStatic_NeverAdvances()
        {
            // `stab='1'` means the state is posed from outside by setStab(progress), which is how the
            // eight `jump` rows pick a frame from the jump's own arc rather than from a timer. step()
            // returns immediately, so the cursor stays where setStab left it.
            var state = new AnimationFrame
            {
                row = 3, length = 16, firstFrame = 0, frameStep = 1f, isStatic = true
            };
            Cursor cursor = At(state);

            for (int i = 0; i < 5; i++)
            {
                state.Step(ref cursor.Frame, ref cursor.Stopped);
            }

            Assert.That(cursor.Frame, Is.EqualTo(0f));
            Assert.That(cursor.Stopped, Is.False, "a static state is not 'finished' — it is externally driven");
        }

        [Test]
        public void Step_ZeroFrameStep_ReadsAsTheOracleDefaultOfOne()
        {
            // AS3 gets df = 1 from a field initializer, which a serializable struct cannot have. A
            // hand-built frame therefore arrives with frameStep 0, and a zero step would leave the bound
            // test permanently true — the state would stall on its first cell with no error anywhere.
            var state = new AnimationFrame { row = 0, length = 3, firstFrame = 0, frameStep = 0f };

            Assert.That(state.EffectiveFrameStep, Is.EqualTo(1f));

            Cursor cursor = At(state);
            state.Step(ref cursor.Frame, ref cursor.Stopped);

            Assert.That(cursor.Frame, Is.EqualTo(1f), "a zero step must not freeze the animation");
        }

        [Test]
        public void Step_FractionalFrameStep_AccumulatesWithoutLosingTheFraction()
        {
            // df='0.5' on six `walk` rows. The cursor must accumulate 0.5 at a time; an int cursor would
            // add 0.5 to itself and stay on 0 forever.
            var state = new AnimationFrame
            {
                row = 0, length = 5, firstFrame = 0, frameStep = 0.5f, replay = true
            };
            Cursor cursor = At(state);

            state.Step(ref cursor.Frame, ref cursor.Stopped);
            Assert.That(cursor.Frame, Is.EqualTo(0.5f));

            state.Step(ref cursor.Frame, ref cursor.Stopped);
            Assert.That(cursor.Frame, Is.EqualTo(1f));
        }

        // ── CellFor: truncation, not rounding ────────────────────────────────

        [Test]
        public void CellFor_TruncatesRatherThanRounds()
        {
            // AS3 hands the float cursor to `blit(param1:int, param2:int)`, so it is truncated toward
            // zero. Rounding would show cell 2 while the oracle is still showing cell 1.
            var state = new AnimationFrame { length = 10, frameStep = 0.4f };

            Assert.That(state.CellFor(0.4f), Is.EqualTo(0));
            Assert.That(state.CellFor(1.4f), Is.EqualTo(1));
            Assert.That(state.CellFor(1.9f), Is.EqualTo(1), "1.9 truncates to 1, it does not round to 2");
            Assert.That(state.CellFor(2f), Is.EqualTo(2));
        }

        [Test]
        public void CellFor_HalfStep_RepeatsEveryCellExceptTheLast()
        {
            var state = new AnimationFrame
            {
                row = 0, length = 4, firstFrame = 0, frameStep = 0.5f, replay = true
            };
            Cursor cursor = At(state);

            var visited = new System.Collections.Generic.List<int>();
            for (int i = 0; i < 8; i++)
            {
                visited.Add(cursor.Cell(state));
                state.Step(ref cursor.Frame, ref cursor.Stopped);
            }

            // 0, 0, 1, 1, 2, 2, 3, 0 — and the 0 is the point.
            //
            // A half-step does NOT hold every cell for two ticks: it holds every cell but the LAST.
            // The oracle's bound is `f < firstf + maxf - 1` (`BlitAnim.as:3721`), which is a test
            // against the last USABLE index, not against a count — so the cursor is allowed to *reach*
            // 3.0 and is then wrapped on the very next step, having displayed cell 3 for one tick.
            // The cycle is 7 ticks (0,0,1,1,2,2,3), not 8, and the eighth read is already the wrap.
            //
            // This assertion previously read `{ 0, 0, 1, 1, 2, 2, 3, 3 }` with the comment "each cell
            // shown twice", which is a plausible reading of "half step" and is not what the oracle
            // does. It was wrong, not the implementation — re-derived from `BlitAnim.as:3715-3733`,
            // where `df=0.5, firstf=0, maxf=4, retf=0` walks f through 0, 0.5, 1, 1.5, 2, 2.5, 3.0 and
            // then resets. `Gutsy1Stay_VisitsAllFortyCellsThenLoopsForever` below is the same rule at
            // df=1, where no cell repeats and the difference is invisible.
            Assert.That(visited, Is.EqualTo(new[] { 0, 0, 1, 1, 2, 2, 3, 0 }));
        }

        // ── Restart ──────────────────────────────────────────────────────────

        [Test]
        public void Restart_ReturnsToTheFirstFrameAndClearsStopped()
        {
            var state = new AnimationFrame { row = 2, length = 3, firstFrame = 0, frameStep = 1f };
            Cursor cursor = At(state);

            state.Step(ref cursor.Frame, ref cursor.Stopped);
            state.Step(ref cursor.Frame, ref cursor.Stopped);
            state.Step(ref cursor.Frame, ref cursor.Stopped);
            Assert.That(cursor.Stopped, Is.True);

            state.Restart(ref cursor.Frame, ref cursor.Stopped);

            Assert.That(cursor.Frame, Is.EqualTo(0f));
            Assert.That(cursor.Stopped, Is.False);
        }

        // ── The real gutsy1 `stay`, end to end ───────────────────────────────

        [Test]
        public void Gutsy1Stay_VisitsAllFortyCellsThenLoopsForever()
        {
            // <blit id='stay' len='40' rep='0'/> on sprGutsy1, whose sheet the importer measured as
            // 40 columns by 2 rows. `rep='0'` is TRUE — BlitAnim.as:3703 presence-tests the attribute —
            // so this is a loop, and it must never stop.
            var state = new AnimationFrame
            {
                row = 0, length = 40, firstFrame = 0, returnFrame = 0, frameStep = 1f, replay = true
            };
            Cursor cursor = At(state);

            var distinct = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < 40; i++)
            {
                distinct.Add(cursor.Cell(state));
                state.Step(ref cursor.Frame, ref cursor.Stopped);
            }

            Assert.That(distinct.Count, Is.EqualTo(40), "every one of the 40 cells must be shown");
            Assert.That(cursor.Frame, Is.EqualTo(0f), "40 steps of 1 from 0 wraps back to 0");
            Assert.That(cursor.Stopped, Is.False, "a rep state must never stop");
        }

        [Test]
        public void Gutsy1Stay_CellsStayInsideItsSheet()
        {
            // The sheet is 40 wide, so cell indices 0..39 are row 0 and 40..79 are row 1. A `stay` that
            // walked one cell too far would silently draw row 1's first frame.
            var state = new AnimationFrame
            {
                row = 0, length = 40, firstFrame = 0, frameStep = 1f, replay = true
            };
            Cursor cursor = At(state);

            for (int i = 0; i < 40; i++)
            {
                int index = UnitSheetLayout.CellIndex(state.row, cursor.Cell(state), columns: 40);
                Assert.That(index, Is.InRange(0, 39), "row 0 of a 40-wide sheet is cells 0..39");
                state.Step(ref cursor.Frame, ref cursor.Stopped);
            }
        }

        // ── UnitAnimator: indexing and the out-of-range guard ────────────────

        static UnitDefinition MakeDefinition(int columns, int rows, int stayLength, bool replay)
        {
            var texture = new Texture2D(columns * 4, rows * 4);
            var sheet = new Sprite[columns * rows];
            for (int i = 0; i < sheet.Length; i++)
            {
                sheet[i] = Sprite.Create(texture, new Rect(i % columns * 4, i / columns * 4, 4, 4),
                    new Vector2(0.5f, 0.5f));
            }

            var definition = ScriptableObject.CreateInstance<UnitDefinition>();
            definition.id = "animator-fixture";
            definition.spriteSheet = sheet;
            definition.spriteSheetColumns = columns;
            definition.spriteSheetRows = rows;
            definition.animations = new AnimationSet
            {
                stay = new AnimationFrame
                {
                    row = 0, length = stayLength, firstFrame = 0, frameStep = 1f, replay = replay
                }
            };
            return definition;
        }

        static UnitAnimator MakeAnimator(UnitDefinition definition, out GameObject go)
        {
            go = new GameObject("animator-fixture");
            SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
            var animator = go.AddComponent<UnitAnimator>();
            animator.Initialize(definition, renderer);
            return animator;
        }

        [Test]
        public void UnitAnimator_WalksTheRowAndWrapsAtTheEnd()
        {
            UnitDefinition definition = MakeDefinition(columns: 4, rows: 2, stayLength: 4, replay: true);
            UnitAnimator animator = MakeAnimator(definition, out GameObject go);

            try
            {
                Assert.That(animator.HasAnimation, Is.True);

                var visited = new System.Collections.Generic.List<int>();
                for (int i = 0; i < 5; i++)
                {
                    visited.Add(animator.CurrentCellIndex);
                    animator.AdvanceFrame();
                }

                Assert.That(visited, Is.EqualTo(new[] { 0, 1, 2, 3, 0 }));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void UnitAnimator_RowPastTheSheet_IsRefusedRatherThanWrapped()
        {
            // CellIndex is plain row-major arithmetic, so row 2 of a 2-row sheet does not fail — it
            // computes index 8 and draws row 1's first cell. A wrong-but-plausible frame is worse than
            // no frame, so the animator returns -1 and leaves the resting sprite alone.
            UnitDefinition definition = MakeDefinition(columns: 4, rows: 2, stayLength: 4, replay: true);
            definition.animations.stay.row = 2;

            UnitAnimator animator = MakeAnimator(definition, out GameObject go);

            try
            {
                Assert.That(animator.CurrentCellIndex, Is.EqualTo(-1));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void UnitAnimator_ReselectingTheSameState_DoesNotRewindIt()
        {
            // The oracle restarts only on `animState != animState2`. An AI that asserts "walk" every tick
            // must not be pinned to cell 0 by its own reassertion.
            UnitDefinition definition = MakeDefinition(columns: 4, rows: 2, stayLength: 4, replay: true);
            UnitAnimator animator = MakeAnimator(definition, out GameObject go);

            try
            {
                animator.AdvanceFrame();
                animator.AdvanceFrame();
                Assert.That(animator.CurrentCellIndex, Is.EqualTo(2));

                animator.SetState("stay");

                Assert.That(animator.CurrentCellIndex, Is.EqualTo(2), "a re-assertion must not rewind");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void UnitAnimator_UnknownState_ReportsNoAnimation()
        {
            UnitDefinition definition = MakeDefinition(columns: 4, rows: 2, stayLength: 4, replay: true);
            UnitAnimator animator = MakeAnimator(definition, out GameObject go);

            try
            {
                // `derg`, `super` and `attack` exist in AllData.as with no field in AnimationSet, so an
                // unmapped id must read as "nothing to play" rather than throwing.
                Assert.That(animator.SetState("derg"), Is.False);
                Assert.That(animator.HasAnimation, Is.False);
                Assert.That(animator.CurrentCellIndex, Is.EqualTo(-1));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void UnitAnimator_SingleCellStay_LeavesTheRestingSpriteAlone()
        {
            // 22 of the 26 `stay` rows declare no `len` at all, so length is 1. Those units have no
            // animation to play and the sprite RoomUnitSpawner.ApplySprite drew must stay untouched.
            UnitDefinition definition = MakeDefinition(columns: 4, rows: 2, stayLength: 1, replay: false);
            UnitAnimator animator = MakeAnimator(definition, out GameObject go);

            try
            {
                Assert.That(animator.HasAnimation, Is.True, "one cell is still a state with frames");

                animator.AdvanceFrame();
                animator.AdvanceFrame();

                Assert.That(animator.CurrentCellIndex, Is.EqualTo(0), "a 1-cell state never leaves cell 0");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
