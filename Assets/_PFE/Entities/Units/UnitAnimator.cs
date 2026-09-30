using System;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Plays a unit's sheet animation — the port of the oracle's per-frame draw loop,
    /// <c>UnitAlicorn.as:315-340</c>:
    ///
    /// <code>
    /// if(animState != animState2) { anims[animState].restart(); animState2 = animState; }
    /// if(!anims[animState].st)    { blit(anims[animState].id, anims[animState].f); }
    /// anims[animState].step();
    /// </code>
    ///
    /// <para><b>Why this exists.</b> <c>UnitDefinition.spriteSheet</c>, <c>.animations</c>,
    /// <c>.spriteSheetColumns</c> and <c>.spriteSheetRows</c> were written by the importers and read by
    /// <b>nobody</b> — every reference in the repo was an importer <i>writing</i> them. So a spawned unit
    /// stood frozen on its resting frame however much animation data its asset carried. This is the
    /// missing reader.</para>
    ///
    /// <para><b>The data was ready before the code was.</b> 75 of the 148 unit assets carry animation
    /// data; four have a genuinely multi-frame <c>stay</c> — <c>gutsy</c> and <c>gutsy1</c> (40 cells,
    /// looping), <c>necros</c> (24), <c>bossalicorn</c> (20) — and the sheets behind them are complete
    /// (<c>gutsy1</c> is <c>sprGutsy1</c>, 40 columns x 2 rows, all 80 cells imported). The other 22
    /// <c>stay</c> rows in <c>AllData.as</c> are single-cell, so for those the resting frame <i>is</i> the
    /// animation and this component changes nothing visible.</para>
    ///
    /// <para><b>Cadence, and the one place this is deliberately not the oracle's shape.</b> The oracle's
    /// loop runs on <c>ENTER_FRAME</c>, the SWF's display frame. This runs in <c>FixedUpdate</c> behind a
    /// <c>1/30</c> accumulator, mirroring <see cref="PFE.Character.Animation.CharacterAnimationDriver"/>
    /// — the port's existing animation cadence, recorded in
    /// <c>docs/CharacterAnimation_PortGapAudit.md</c> row 44 as "cadence matches, but the driver is
    /// Unity's 50 Hz step, outside the P1 Sim/View split". Animation is view-only and must not consume
    /// sim ticks, so the alternative — driving it from <c>SimLoop</c> at <c>SimTickOrder.ViewAligned</c>
    /// — would be architecturally tidier but would need <c>SimClock</c>/<c>SimLoop</c> plumbed through
    /// <c>MapBridge</c> into <c>RoomVisualController</c>, and would stop animating entirely whenever
    /// <c>PfeDebugSettings.SimTickEnabled</c> is off. That trade is the caller's to make; this class
    /// needs nothing but a renderer.</para>
    ///
    /// <para><b>What it does not do yet.</b> Nothing drives the state machine: with no AI ported,
    /// <c>animState</c> stays <c>stay</c> forever, exactly as AS3's constructor leaves it
    /// (<c>Unit.as:2860</c>). Walking, attacking and dying become visible the moment something calls
    /// <see cref="SetState"/>; the stepping and the cell indexing are already the oracle's.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UnitAnimator : MonoBehaviour
    {
        /// <summary>
        /// The game's frame rate — AS3 <c>World.as:44</c>, <c>public static const fps:* = 30;</c>. It is
        /// not in the unit data: <c>AllData.as</c> carries no frame rate at all, and the oracle's own
        /// <c>BlitAnim</c> is driven by the stage's rate rather than by anything it owns.
        /// </summary>
        public const float FrameRate = 30f;

        UnitDefinition _definition;
        SpriteRenderer _renderer;

        string _stateName;
        AnimationFrame _state;
        float _frame;
        bool _stopped;

        /// <summary>
        /// The cell currently on the renderer, so an unchanged cell is not reassigned every tick. Reset
        /// whenever the state changes, because the same index in a new state is a different cell.
        /// </summary>
        int _drawnCell = -1;

        readonly float _frameDuration = 1f / FrameRate;
        float _timer;

        /// <summary>The AS3 animation id currently selected, e.g. <c>stay</c>. Empty before <see cref="Initialize"/>.</summary>
        public string StateName => _stateName;

        /// <summary>True when there is a state with frames and a renderer to draw them on.</summary>
        public bool HasAnimation =>
            _renderer != null && _definition != null && _state.HasFrames;

        /// <summary>
        /// The index into <see cref="UnitDefinition.spriteSheet"/> that should be on screen, or
        /// <c>-1</c> when the state has no frames or its cell lies outside the sheet.
        /// </summary>
        public int CurrentCellIndex => CellIndexFor(_state, _frame);

        /// <summary>
        /// Bind this animator to a unit and start it in <paramref name="initialState"/> — AS3's
        /// <c>animState = "stay"</c> at the end of the <c>Unit</c> constructor (<c>Unit.as:2860</c>).
        ///
        /// <para><b>Call it after the renderer already shows the resting frame.</b>
        /// <c>RoomUnitSpawner.ApplySprite</c> draws <see cref="UnitDefinition.sprite"/> first and this
        /// takes over from the next tick, so a unit with no animation keeps its static sprite and one
        /// with animation does not flicker through an empty first frame.</para>
        /// </summary>
        /// <returns>True when <paramref name="initialState"/> actually has frames to play.</returns>
        public bool Initialize(UnitDefinition definition, SpriteRenderer renderer, string initialState = "stay")
        {
            _definition = definition;
            _renderer = renderer;
            _stateName = null;
            _state = default;
            _frame = 0f;
            _stopped = false;
            _drawnCell = -1;
            _timer = 0f;

            return SetState(initialState);
        }

        /// <summary>
        /// Select the AS3 animation state by id (<c>stay</c>, <c>walk</c>, <c>trot</c>, <c>run</c>,
        /// <c>jump</c>, <c>die</c>, <c>death</c>, <c>fall</c>, <c>sit</c>, <c>fly</c>, <c>dig</c>,
        /// <c>plav</c>, <c>polz</c>, <c>laz</c>, <c>pre</c> — see <see cref="AnimationSet.As3Ids"/>).
        ///
        /// <para><b>Re-selecting the current state is a no-op, on purpose.</b> The oracle restarts only
        /// when the state <i>changes</i> — <c>if(animState != animState2)</c> — so a caller that asserts
        /// its state every tick, which is what an AI will do, must not rewind the animation each time.
        /// Returning the current state's <c>HasFrames</c> rather than <c>true</c> keeps the return value
        /// meaning "this state is playable" in both branches.</para>
        /// </summary>
        /// <returns>True when the named state exists and has frames.</returns>
        public bool SetState(string as3Id)
        {
            if (string.IsNullOrEmpty(as3Id))
            {
                return false;
            }

            if (string.Equals(as3Id, _stateName, StringComparison.Ordinal))
            {
                return _state.HasFrames;
            }

            // Get() is the read-only accessor: an id with no field in the set yields `default`, whose
            // HasFrames is false. An earlier accessor used TrySet as a predicate and blanked the state it
            // read — see the note on AnimationSet.Get.
            AnimationFrame state = _definition != null && _definition.animations != null
                ? _definition.animations.Get(as3Id)
                : default;

            _stateName = as3Id;
            _state = state;
            _drawnCell = -1;
            _timer = 0f;
            state.Restart(ref _frame, ref _stopped);

            return state.HasFrames;
        }

        /// <summary>
        /// Advance exactly one animation frame and draw it — the oracle's whole loop body.
        ///
        /// <para>Public and separate from <see cref="FixedUpdate"/> so a test can drive it
        /// deterministically: <c>FixedUpdate</c> does not run in EditMode, and asserting on a 30 Hz
        /// accumulator would make every animation test a timing test.</para>
        ///
        /// <para>The draw happens <b>before</b> the step, and only while the state is not stopped — AS3
        /// <c>if(!anims[animState].st) { blit(...); } anims[animState].step();</c>. So the final cell of a
        /// non-replaying row is drawn once and then held, rather than being skipped.</para>
        /// </summary>
        public void AdvanceFrame()
        {
            if (!_state.HasFrames)
            {
                return;
            }

            if (!_stopped)
            {
                Draw();
            }

            _state.Step(ref _frame, ref _stopped);
        }

        void FixedUpdate()
        {
            if (!_state.HasFrames || _frameDuration <= 0f)
            {
                return;
            }

            _timer += Time.fixedDeltaTime;
            while (_timer >= _frameDuration)
            {
                _timer -= _frameDuration;
                AdvanceFrame();
            }
        }

        void Draw()
        {
            int index = CellIndexFor(_state, _frame);
            if (index < 0 || index == _drawnCell)
            {
                return;
            }

            Sprite[] sheet = _definition.spriteSheet;
            if (sheet == null || index >= sheet.Length)
            {
                return;
            }

            _drawnCell = index;
            Sprite cell = sheet[index];

            // Anchored, not just assigned: cells sliced from the sheet carry the oracle's pivot while the
            // per-frame resting frame does not, so a unit that starts animating would otherwise jump by
            // the difference. UnitSpriteAnchor derives the offset from the sprite actually drawn, so a
            // correctly-pivoted cell yields zero and a centre-pivoted one yields the correction.
            UnitSpriteAnchor.ApplyTo(_renderer, cell, _definition.registrationPoint);
            _renderer.enabled = cell != null;
        }

        /// <summary>
        /// Row-major index into the sliced sheet for a state and cursor, or <c>-1</c> when it cannot be
        /// addressed.
        ///
        /// <para><b>The bounds check is not defensive padding — it prevents a plausible wrong answer.</b>
        /// <see cref="UnitSheetLayout.CellIndex"/> is plain <c>row * columns + column</c>, so a row one
        /// past the last does not fail: it wraps onto the following row and draws a cell belonging to a
        /// different animation. The importer already reports those states as <c>OutOfRange</c> (11 of
        /// them), so this refuses to draw them instead of drawing something that looks like art.</para>
        ///
        /// <para><c>spriteSheetColumns</c>/<c>Rows</c> are <c>0</c> until the sprite import has run, which
        /// is the correct degradation: nothing is drawn and the resting sprite from
        /// <c>RoomUnitSpawner.ApplySprite</c> stays on screen.</para>
        /// </summary>
        int CellIndexFor(AnimationFrame state, float frame)
        {
            if (_definition == null || !state.HasFrames)
            {
                return -1;
            }

            int columns = _definition.spriteSheetColumns;
            int rows = _definition.spriteSheetRows;
            if (columns <= 0 || rows <= 0)
            {
                return -1;
            }

            int column = state.CellFor(frame);
            if (state.row < 0 || state.row >= rows || column < 0 || column >= columns)
            {
                return -1;
            }

            return UnitSheetLayout.CellIndex(state.row, column, columns);
        }
    }
}
