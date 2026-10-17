using PFE.Data.Definitions.Campaign;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Plays an NPC persona's own animation clip — the port of <c>UnitNPC.animate()</c>
    /// (<c>fe/unit/UnitNPC.as:243-281</c>) plus the two one-shot playhead writes in
    /// <c>command("openEyes")</c> (<c>:341-350</c>) and the constructor (<c>:164-174</c>).
    ///
    /// <para><b>Why a persona needs its own player.</b> <see cref="UnitAnimator"/> is driven by
    /// <c>UnitDefinition.animations</c> and blits <i>sheet cells</i>. An NPC has no sheet: every NPC in
    /// the game shares the one <c>&lt;unit id='npc'&gt;</c> row, and the art belongs to the persona's
    /// <c>visual&lt;Vis&gt;</c> DisplayObject, whose frames are separate PNGs addressed by
    /// <b>label</b> and by <b>timeline frame number</b>. The two addressing modes are the whole
    /// contract: <c>gotoAndStop("stay")</c>, <c>gotoAndPlay("move1")</c>, <c>gotoAndStop(2)</c>.</para>
    ///
    /// <para><b>What the oracle actually animates, and what it does not.</b> There are only three
    /// moving parts, and two of them are one-liners:</para>
    /// <list type="number">
    /// <item><description><b>The fidget.</b> <c>t_anim</c> counts down from <b>100</b> (declared
    /// <c>:41</c>, not zero), then every <c>150 + random*200</c> frames the clip jumps to
    /// <c>move1</c> or <c>move2</c> and plays on. A persona with neither label — <c>visualCalam</c> is
    /// one, its labels being <c>stay,trot,run,polz,jump,laz,plav,punch,kick,die,derg,fly</c> — has the
    /// <c>gotoAndPlay</c> <i>throw</i> into a <c>catch</c> that does nothing. That is why the call is
    /// wrapped, and why "no <c>move1</c>" must mean "do not move" rather than "go to frame 1".</description></item>
    /// <item><description><b>The fly / stay switch</b>, gated on <c>animFly</c> — the placement's
    /// <c>fly</c> attribute. Only while flying does the clip show <c>fly</c>; landing shows
    /// <c>stay</c>.</description></item>
    /// <item><description><b>The one-shot writes</b> — the constructor's <c>stay</c> (falling back to
    /// frame 1 when the label is absent) and <c>openEyes</c>'s frame 2.</description></item>
    /// </list>
    ///
    /// <para><b>Why Calamity was the visible symptom.</b> <c>rooms_surf</c> places him as
    /// <c>&lt;obj id="npc" npc="calam" uid="surfCalam" fly="1"/&gt;</c>, so <c>animFly</c> is true and
    /// <c>isFly</c> starts true — the first <c>animate()</c> frame puts his clip on <c>fly</c>, and the
    /// landing at the end of <c>surfStory</c> puts it back on <c>stay</c>. With the clip not imported
    /// and no player, he stood in the one pose his single exported PNG held for the entire
    /// cutscene.</para>
    ///
    /// <para><b>Cadence.</b> The oracle's <c>animate()</c> runs on <c>ENTER_FRAME</c>, the SWF's
    /// display frame, which is 30 Hz (<c>World.as:44</c>). This runs in <c>FixedUpdate</c> behind a
    /// <c>1/30</c> accumulator, mirroring <see cref="UnitAnimator"/> exactly — animation is view-only
    /// and must not consume sim ticks. See that class for why the alternative was rejected.</para>
    ///
    /// <para><b>This component does not write <c>renderer.enabled</c>.</b> Visibility belongs to
    /// <c>UnitController.ApplyVisualVisibility</c> / <c>UnitNPCController.ApplyPersonaArt</c>, and a
    /// second writer would produce the "hiding works for some NPCs" shape that
    /// <see cref="UnitAnimator"/> documents at length.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NpcVisualAnimator : MonoBehaviour
    {
        /// <summary>
        /// The game's frame rate — AS3 <c>World.as:44</c>, <c>public static const fps:* = 30;</c>.
        /// Taken from <see cref="UnitAnimator.FrameRate"/> rather than re-declared, so the two
        /// animation paths cannot drift on the one number they both need.
        /// </summary>
        public const float FrameRate = UnitAnimator.FrameRate;

        /// <summary>
        /// AS3 <c>internal var t_anim:int = 100;</c> (<c>UnitNPC.as:41</c>) — the first fidget is 100
        /// frames away, not immediate.
        /// </summary>
        public const int InitialFidgetDelay = 100;

        /// <summary>The registration point personas are anchored with — "not declared", so the
        /// anchor is derived from the sprite. Matches <c>UnitNPCController.ApplyPersonaArt</c>.</summary>
        static readonly Vector2Int PersonaRegistrationPoint = new Vector2Int(-1, -1);

        NpcDefinition _persona;
        SpriteRenderer _renderer;

        /// <summary>AS3 <c>t_anim</c> — frames until the next fidget.</summary>
        int _tAnim = InitialFidgetDelay;

        /// <summary>
        /// AS3 <c>animState</c> — the state the clip is in. Written <b>only</b> by the fly/stay switch,
        /// never by the fidget, which is the oracle's own asymmetry and is load-bearing: a vendor whose
        /// fidget is playing keeps <c>animState == "stay"</c> throughout.
        /// </summary>
        string _animState = string.Empty;

        /// <summary>AS3 <c>animFly</c> — whether this persona has a <c>fly</c> pose at all.</summary>
        bool _animFly;

        /// <summary>AS3 <c>isFly</c> — whether it is airborne <i>now</i>.</summary>
        bool _isFly;

        /// <summary>0-based index into <c>persona.visualFrames</c>.</summary>
        int _frame;

        /// <summary>Whether the playhead advances — AS3's <c>gotoAndPlay</c> vs <c>gotoAndStop</c>.</summary>
        bool _playing;

        /// <summary>The frame currently on the renderer, so an unchanged frame is not reassigned.</summary>
        int _drawnFrame = -1;

        readonly float _frameDuration = 1f / FrameRate;
        float _timer;

        /// <summary>The AS3 label the playhead is sitting on, or empty when the frame has none.</summary>
        public string CurrentLabel
        {
            get
            {
                if (_persona == null || _persona.visualFrames == null ||
                    _frame < 0 || _frame >= _persona.visualFrames.Count)
                {
                    return string.Empty;
                }

                return _persona.visualFrames[_frame].label ?? string.Empty;
            }
        }

        /// <summary>The 1-based SWF frame the playhead is on, or <c>0</c> when unbound.</summary>
        public int CurrentFrameNumber
        {
            get
            {
                if (_persona == null || _persona.visualFrames == null ||
                    _frame < 0 || _frame >= _persona.visualFrames.Count)
                {
                    return 0;
                }

                return _persona.visualFrames[_frame].frameNumber;
            }
        }

        /// <summary>AS3 <c>animState</c>. Empty until the fly/stay switch has run.</summary>
        public string AnimState => _animState;

        /// <summary>AS3 <c>t_anim</c>, exposed for readouts and fixtures.</summary>
        public int FidgetCountdown => _tAnim;

        /// <summary>True when the playhead is advancing — AS3's <c>gotoAndPlay</c> arm.</summary>
        public bool IsPlaying => _playing;

        /// <summary>True when there is a clip with at least two frames and a renderer to draw it on.
        /// A one-frame persona is not animating, and saying so keeps a caller from waiting for
        /// movement that cannot happen.</summary>
        public bool HasAnimation =>
            _renderer != null && _persona != null && _persona.VisualFrameCount > 1;

        /// <summary>
        /// Bind this player to a persona and a renderer, and perform the constructor's resting-frame
        /// write — AS3 <c>UnitNPC.as:164-174</c>.
        ///
        /// <code>
        /// try { vis.osn.gotoAndStop("stay"); } catch(err:*) { vis.osn.gotoAndStop(1); }
        /// </code>
        ///
        /// <para><b>The <c>catch</c> is a real branch, not defensive noise.</b> A persona with no
        /// <c>stay</c> label still comes to rest — on timeline frame 1 —
        /// <see cref="NpcDefinition.IndexOfRestingFrame"/> owns that fallback. Binding a persona with
        /// no frames at all leaves the renderer alone, so the sprite
        /// <c>UnitNPCController.ApplyPersonaArt</c> already drew stays on screen.</para>
        /// </summary>
        /// <returns>True when a clip was bound.</returns>
        public bool Initialize(NpcDefinition persona, SpriteRenderer renderer)
        {
            _persona = persona;
            _renderer = renderer;

            // A re-bound player is a freshly spawned NPC. Leaving the previous persona's countdown
            // and playhead in place would carry one unit's fidget schedule onto another's.
            _tAnim = InitialFidgetDelay;
            _animState = string.Empty;
            _animFly = false;
            _isFly = false;
            _playing = false;
            _frame = 0;
            _drawnFrame = -1;
            _timer = 0f;

            if (_persona == null || _persona.VisualFrameCount == 0)
            {
                return false;
            }

            _frame = Mathf.Max(0, _persona.IndexOfRestingFrame());

            ApplyScale();
            Draw();
            return true;
        }

        /// <summary>AS3 <c>animFly</c>, from the placement's <c>fly</c> attribute
        /// (<c>UnitNPC.as:194-199</c>).</summary>
        public void SetAnimFly(bool animFly) => _animFly = animFly;

        /// <summary>AS3 <c>isFly</c> — true while airborne, cleared by the landing block
        /// (<c>UnitNPC.as:598</c>).</summary>
        public void SetFly(bool isFly) => _isFly = isFly;

        /// <summary>
        /// AS3 <c>UnitNPC.animate()</c> (<c>:243-281</c>) — one display frame of state selection.
        /// </summary>
        /// <remarks>
        /// <para><b>Both random rolls are consumed even when the label is missing, and that is
        /// faithful.</b> The oracle computes <c>_loc1_</c> before the <c>try</c>, so a persona without
        /// <c>move1</c>/<c>move2</c> still advances the random stream before its <c>gotoAndPlay</c>
        /// throws. Skipping the roll would desync every later roll on that unit.</para>
        ///
        /// <para><b><c>animState</c> is only written when the <c>gotoAndStop</c> succeeded.</b> In the
        /// oracle the assignment sits <i>after</i> the call and <i>inside</i> the same <c>try</c>, so a
        /// persona with no <c>fly</c> label re-attempts the switch every frame instead of latching a
        /// state it never reached. That is preserved here by testing the return value.</para>
        /// </remarks>
        public void Animate()
        {
            if (_persona == null || _persona.VisualFrameCount == 0)
            {
                return;
            }

            if (_tAnim > 0)
            {
                _tAnim--;
            }
            else
            {
                _tAnim = UnitNpcRules.NextFidgetDelay(Random.value);

                // The roll happens before the goto in the oracle, so it happens here too — the
                // `gotoAndPlay` result is deliberately ignored (see the class summary).
                string label = UnitNpcRules.FidgetLabel(Random.value);
                GotoAndPlay(label);
            }

            if (!_animFly)
            {
                return;
            }

            if (_isFly && !string.Equals(_animState, "fly", System.StringComparison.Ordinal))
            {
                if (GotoAndStop("fly"))
                {
                    _animState = "fly";
                }
            }

            if (!_isFly && !string.Equals(_animState, "stay", System.StringComparison.Ordinal))
            {
                if (GotoAndStop("stay"))
                {
                    _animState = "stay";
                }
            }
        }

        /// <summary>
        /// <c>vis.osn.gotoAndStop(label)</c> — jump to a labelled frame and hold it.
        /// </summary>
        /// <returns>
        /// False when the clip has no such label. That is the oracle's <c>throw</c>, and every caller
        /// has to decide what it means: the fidget ignores it, the fly/stay switch treats it as "the
        /// state was not reached".
        /// </returns>
        public bool GotoAndStop(string label)
        {
            int index = _persona != null ? _persona.IndexOfVisualLabel(label) : -1;
            if (index < 0)
            {
                return false;
            }

            SetPlayhead(index, playing: false);
            return true;
        }

        /// <summary>
        /// <c>vis.osn.gotoAndPlay(label)</c> — jump to a labelled frame and run on from there,
        /// wrapping at the end of the timeline.
        ///
        /// <para><b>It runs on through the following labels, and that is the oracle's behaviour.</b>
        /// <c>gotoAndPlay</c> is not "play this named segment": Flash plays to the last frame and loops
        /// back to frame 1, and this export contains <b>no <c>DoAction</c> tag anywhere</b> — so no
        /// symbol carries a <c>stop()</c> and every clip loops. A vendor's <c>move1</c> therefore plays
        /// on through <c>move2</c> and <c>stay</c> and back, which is what the original does.</para>
        /// </summary>
        public bool GotoAndPlay(string label)
        {
            int index = _persona != null ? _persona.IndexOfVisualLabel(label) : -1;
            if (index < 0)
            {
                return false;
            }

            SetPlayhead(index, playing: true);
            return true;
        }

        /// <summary>
        /// <c>vis.osn.gotoAndStop(frameNumber)</c> — the numeric form, used by <c>openEyes</c>
        /// (<c>UnitNPC.as:345</c>, frame 2) and by the constructor's fallback (<c>:172</c>, frame 1).
        /// </summary>
        /// <returns>False when the clip has no frame with that number.</returns>
        public bool GotoAndStopFrame(int frameNumber)
        {
            int index = _persona != null ? _persona.IndexOfVisualFrameNumber(frameNumber) : -1;
            if (index < 0)
            {
                return false;
            }

            SetPlayhead(index, playing: false);
            return true;
        }

        /// <summary>
        /// Advance the playhead by one frame while it is running, and draw the result.
        ///
        /// <para>Public and separate from <see cref="FixedUpdate"/> for the same reason
        /// <see cref="UnitAnimator.AdvanceFrame"/> is: <c>FixedUpdate</c> does not run in EditMode, and
        /// asserting on a 30 Hz accumulator would make every test a timing test.</para>
        /// </summary>
        public void AdvanceFrame()
        {
            if (_persona == null || _persona.VisualFrameCount == 0)
            {
                return;
            }

            if (_playing)
            {
                _frame = (_frame + 1) % _persona.VisualFrameCount;
            }

            Draw();
        }

        void FixedUpdate()
        {
            if (_persona == null || _persona.VisualFrameCount == 0 || _frameDuration <= 0f)
            {
                return;
            }

            _timer += Time.fixedDeltaTime;
            while (_timer >= _frameDuration)
            {
                _timer -= _frameDuration;

                // `animate()` first, then the playhead — the oracle's `animate()` is the ENTER_FRAME
                // handler and Flash advances the playhead as part of the same display frame. The
                // order only matters on a frame that both selects a state and steps, where selecting
                // first is what makes `gotoAndPlay` start on the frame it named.
                Animate();
                AdvanceFrame();
            }
        }

        void SetPlayhead(int index, bool playing)
        {
            if (_persona == null || index < 0 || index >= _persona.VisualFrameCount)
            {
                return;
            }

            _frame = index;
            _playing = playing;
            _drawnFrame = -1;

            // Drawn immediately, not on the next tick: a `gotoAndStop` is visible on the frame it is
            // issued, and the fly/stay switch is issued from `Animate()` outside the accumulator loop.
            Draw();
        }

        void Draw()
        {
            if (_renderer == null || _persona == null || _persona.visualFrames == null ||
                _frame < 0 || _frame >= _persona.visualFrames.Count)
            {
                return;
            }

            if (_frame == _drawnFrame)
            {
                return;
            }

            Sprite sprite = _persona.visualFrames[_frame].sprite;
            if (sprite == null)
            {
                return;
            }

            _drawnFrame = _frame;

            // The anchor is derived from the sprite actually being drawn, and from its size AFTER the
            // persona's own scale — because `localScale` scales the sprite about the transform's
            // origin, so the offset that puts its bottom edge 10 px below the feet is measured in
            // scaled pixels, not source ones. A centre-pivoted, unscaled sprite yields exactly what
            // UnitSpriteAnchor.ApplyTo produces, so nothing moves for the 114 clip-less personas.
            _renderer.sprite = sprite;
            _renderer.transform.localPosition = WorldCoordinates.PixelToUnity(
                ScaledPixelOffset(sprite, _persona.VisualScale, PersonaRegistrationPoint));
        }

        /// <summary>
        /// <see cref="UnitSpriteAnchor.PixelOffset(Vector2, Vector2, Vector2Int)"/> with the sprite's
        /// pivot and size expressed in post-scale pixels.
        /// </summary>
        static Vector2 ScaledPixelOffset(Sprite sprite, Vector2 scale, Vector2Int registrationPoint)
        {
            if (sprite == null)
            {
                return Vector2.zero;
            }

            float sx = Mathf.Abs(scale.x);
            float sy = Mathf.Abs(scale.y);

            var pivot = new Vector2(sprite.pivot.x * sx, sprite.pivot.y * sy);
            var size = new Vector2(sprite.rect.width * sx, sprite.rect.height * sy);

            return UnitSpriteAnchor.PixelOffset(pivot, size, registrationPoint);
        }

        /// <summary>
        /// Put the persona's own scale on the visual child's transform.
        ///
        /// <para><b>Why the child and not the unit.</b> The unit's <c>localScale.x</c> is the
        /// <i>facing</i> (<c>UnitController.ApplyFacingToTransform</c> writes <c>±1</c> there), so the
        /// two compose multiplicatively the way the SWF's own placement matrix composes with AS3's
        /// <c>storona</c> flip. Writing it on the unit instead would fight the facing update every
        /// frame.</para>
        ///
        /// <para><b>The negative-X case is real data, not a curiosity.</b> The doctor's and every
        /// vendor's <c>osn</c> placement is <c>(-0.316, 0.316)</c> — mirrored as well as shrunk — so a
        /// port that dropped the sign would draw five shopkeepers back to front.</para>
        /// </summary>
        void ApplyScale()
        {
            if (_renderer == null || _persona == null)
            {
                return;
            }

            Vector2 scale = _persona.VisualScale;
            _renderer.transform.localScale = new Vector3(scale.x, scale.y, 1f);
        }
    }
}
