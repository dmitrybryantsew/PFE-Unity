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
    /// <para><b>What drives it.</b> <see cref="EnemyBrain.UpdateAnimation"/> selects the state
    /// (<see cref="SetState"/>) and poses a <c>stab</c> row (<see cref="SetStab"/>); the stepping and the
    /// cell indexing here are the oracle's. A unit whose brain never calls <see cref="SetState"/> stays on
    /// <c>stay</c> forever, exactly as AS3's constructor leaves it (<c>Unit.as:2860</c>).</para>
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
        /// Whether the sprite is allowed on screen — AS3 <c>vis.visible</c>, which
        /// <c>UnitZombie.animate()</c> clears while a unit is buried in the floor
        /// (<c>UnitZombie.as:265-268</c>) and restores when it digs out or dies.
        ///
        /// <para><b>Why the flag lives here and not on the renderer.</b> <see cref="Draw"/> is the only
        /// thing that writes <c>renderer.enabled</c>, and it runs every animation frame. A caller that
        /// simply set <c>renderer.enabled = false</c> would be undone by the very next <see cref="Draw"/>
        /// — and only for units whose current state has frames, which is the shape of bug that looks
        /// like "hiding works for some enemies". The hidden unit keeps stepping its animation, exactly as
        /// the oracle does: <c>animate()</c>'s <c>aiState == 5</c> branch sets <c>vis.visible = false</c>
        /// and then falls through to the same <c>blit</c>/<c>step</c> tail as every other branch.</para>
        /// </summary>
        bool _visible = true;

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

            // A re-bound animator is a freshly spawned unit, so it starts visible. Leaving the previous
            // unit's hidden state in place would spawn a unit the caller cannot see and would not
            // connect to the ambush it was hidden for.
            _visible = true;

            return SetState(initialState);
        }

        /// <summary>
        /// Show or hide the unit's sprite — AS3 <c>vis.visible</c>.
        ///
        /// <para>Not "disable the renderer": see <see cref="_visible"/> for why the flag has to be read
        /// by <see cref="Draw"/> rather than written onto the renderer. A null renderer is legal and is
        /// remembered, so a caller can set visibility before <see cref="Initialize"/> binds one.</para>
        /// </summary>
        public void SetVisible(bool visible)
        {
            _visible = visible;

            if (_renderer != null)
            {
                _renderer.enabled = visible;
            }
        }

        /// <summary>
        /// Select the AS3 animation state by id (<c>stay</c>, <c>walk</c>, <c>trot</c>, <c>run</c>,
        /// <c>jump</c>, <c>die</c>, <c>death</c>, <c>fall</c>, <c>sit</c>, <c>fly</c>, <c>dig</c>,
        /// <c>plav</c>, <c>polz</c>, <c>laz</c>, <c>pre</c>, <c>attack</c>, <c>derg</c>, <c>super</c> — see
        /// <see cref="AnimationSet.As3Ids"/>).
        ///
        /// <para><b>"Exists" means the unit's own sheet authors it, and the two ways it can fail are
        /// different.</b> An id with no field in <see cref="AnimationSet"/> is a port gap; an id that has
        /// a field but no row on <i>this</i> definition is a data/behaviour mismatch. Both return
        /// <c>false</c> and both leave <see cref="HasAnimation"/> false, which is the honest answer —
        /// <c>Draw</c> then leaves the sprite alone rather than showing a blank cell. The second case is
        /// the one that shipped a bug: the alicorn brain returned <c>pre</c> for a sheet with no
        /// <c>pre</c> row, so the alicorn vanished for up to 40 ticks after every spell.</para>
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
        /// Pose a <c>stab</c> row from outside — AS3 <c>BlitAnim.setStab(progress)</c>
        /// (<c>BlitAnim.as:3741-3751</c>): <c>this.f = this.maxf * clamp(progress, 0, 0.999)</c>.
        ///
        /// <para><b>Why a state needs this at all.</b> A row declared <c>stab='1'</c> is
        /// <see cref="AnimationFrame.isStatic"/>, and <see cref="AnimationFrame.Step"/> returns
        /// immediately for it — so it never advances by itself and, without a caller here, it can only
        /// ever show its first cell. That is not hypothetical: the zombie's <c>jump</c> row is
        /// <c>stab='1'</c> (<c>AllData.as:80</c>, <c>&lt;blit id='jump' y='3' len='16' stab='1'/&gt;</c>)
        /// and the oracle drives it every airborne frame from
        /// <c>UnitZombie.as:308</c>, so a port that plays it as an ordinary self-advancing row shows a
        /// <b>frozen launch pose</b> for the whole arc — which reads as "the jump animation is wrong"
        /// rather than as "the row is not being driven".</para>
        ///
        /// <para><b>Call it before <see cref="SetState"/>, not after.</b> That is the oracle's order and
        /// it is load-bearing on exactly one frame: <c>setStab</c> is called at <c>:308</c> and the
        /// restart at <c>:318</c> (<c>if(animState != animState2) { anims[animState].restart(); }</c>),
        /// and <c>restart()</c> sets <c>f = firstf</c> — so on the frame the state <i>changes</i> into
        /// <c>jump</c> the pose is discarded and cell 0 is drawn. Posing after <see cref="SetState"/>
        /// would draw the velocity-derived cell one frame early and the launch pose would never be seen.
        /// </para>
        ///
        /// <para><b>Consequence, stated because it looks like a bug:</b> on that one change frame
        /// <c>_state</c> is still the <i>previous</i> row, so <see cref="AnimationFrame.FrameAtProgress"/>
        /// is asked for a cell of the row the unit is leaving. The result is harmless <i>only</i> because
        /// <see cref="SetState"/> discards it — which is the behaviour above, not an accident to be
        /// repaired. Do not move this call after <see cref="SetState"/> to "get the right row": that
        /// makes the pose survive the change frame and diverges from the oracle.
        /// </para>
        ///
        /// <para><b>The cursor is written unconditionally, like AS3's.</b> <c>setStab</c> does not test
        /// <c>stab</c>, so neither does this — the caller owns "only pose a row that is <c>stab</c>",
        /// which mirrors the oracle having exactly one call site. The last-drawn cell is invalidated
        /// because the cell <i>is</i> the cursor here: without that, <see cref="Draw"/>'s
        /// unchanged-cell guard would skip the redraw and the row would hold its previous pose.</para>
        /// </summary>
        /// <param name="progress">Normalised pose, <c>0</c> at the start of the row and <c>1</c> at its
        /// end. Clamped by <see cref="AnimationFrame.FrameAtProgress"/> below <c>1</c>, so the last cell
        /// stays reachable.</param>
        public void SetStab(float progress)
        {
            if (!_state.HasFrames)
            {
                return;
            }

            _frame = _state.FrameAtProgress(progress);
            _drawnCell = -1;
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
            _renderer.enabled = _visible && cell != null;
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
