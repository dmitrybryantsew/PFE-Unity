using PFE.Data.Definitions;

namespace PFE.Character.Animation
{
    /// <summary>
    /// The playhead rules for an overlay clip — a clip on the character's visual container that runs on
    /// its <b>own</b> timeline, independent of the character's state clip.
    ///
    /// <para><b>Unity-free on purpose.</b> The overlay's renderer cannot be exercised from an offline
    /// host, because assigning a <c>SpriteRenderer</c> reaches a Unity <c>ECall</c> and the JIT refuses
    /// the method that mentions it. So the decision — <i>which</i> frame, and <i>whether</i> to draw at
    /// all — lives here, where a plain NUnit fixture can call it, and
    /// <see cref="CharacterSpriteAssembler"/> is reduced to "call this, then assign the sprite".</para>
    ///
    /// <para><b>The oracle it mirrors.</b> <c>UnitPlayer.as:4916-4925</c>:</para>
    ///
    /// <code>
    /// if(vis.shit &amp;&amp; !vis.shit.visible &amp;&amp; shithp &gt; 0) { vis.shit.visible = true;  vis.shit.gotoAndPlay(1); }
    /// if(vis.shit &amp;&amp;  vis.shit.visible &amp;&amp; shithp &lt;= 0) { vis.shit.visible = false; vis.shit.gotoAndStop(1); }
    /// </code>
    ///
    /// <para>Two things fall out of that, and both are easy to get subtly wrong:</para>
    /// <list type="number">
    /// <item><b>Rise restarts at frame 1, it does not resume.</b> <c>gotoAndPlay(1)</c> is unconditional,
    /// so a shield that went up, dropped and went up again plays the materialise animation again — it
    /// does not reappear already at full brightness.</item>
    /// <item><b>Fall resets the playhead too.</b> <c>gotoAndStop(1)</c> parks the clip on frame 1 while
    /// hidden. If the port only reset on the rise edge it would be equivalent here, but if it reset
    /// neither, the second rise would show the final frame immediately — a shield that pops into
    /// existence with no animation. So the fall edge writes the playhead as well, and this is the
    /// behaviour the tests pin.</item>
    /// </list>
    /// </summary>
    public static class OverlayClip
    {
        /// <summary>
        /// No drawable frame — the clip has no sprites, or the playhead is out of range. Callers treat
        /// this as "disable the renderer", which is what keeps an un-imported overlay from rendering
        /// sprite index 0 of a null array.
        /// </summary>
        public const int NoFrame = -1;

        /// <summary>
        /// The playhead a clip starts on, for both the rise and the fall edge — AS3's
        /// <c>gotoAndPlay(1)</c> / <c>gotoAndStop(1)</c>, which both name frame 1.
        /// </summary>
        /// <returns><c>0</c>, or <see cref="NoFrame"/> when the clip carries no frames.</returns>
        public static int RestartFrame(int frameCount)
        {
            return frameCount > 0 ? 0 : NoFrame;
        }

        /// <summary>
        /// Pull a playhead into <c>[0, frameCount)</c>, so a definition edited in the inspector cannot
        /// index past its own frames.
        /// </summary>
        public static int ClampFrame(int frame, int frameCount)
        {
            if (frameCount <= 0) return NoFrame;
            if (frame < 0) return 0;
            return frame >= frameCount ? frameCount - 1 : frame;
        }

        /// <summary>
        /// The next playhead position. Mirrors the state-clip rule in
        /// <c>CharacterSpriteAssembler.AdvanceFrame</c>, so an overlay and a body clip cannot drift
        /// apart on the same <see cref="AnimationLoopMode"/>.
        /// </summary>
        /// <param name="loopStartFrame">Sub-range start, used by <see cref="AnimationLoopMode.LoopRange"/>.</param>
        /// <param name="loopEndFrame">Sub-range end (exclusive). <c>-1</c> means the whole clip.</param>
        public static int NextFrame(
            int frame,
            int frameCount,
            AnimationLoopMode loopMode,
            int loopStartFrame = 0,
            int loopEndFrame = -1)
        {
            if (frameCount <= 0) return NoFrame;

            int current = ClampFrame(frame, frameCount);
            int next = current + 1;

            switch (loopMode)
            {
                case AnimationLoopMode.Loop:
                    return next >= frameCount ? 0 : next;

                case AnimationLoopMode.LoopRange:
                {
                    int start = ClampFrame(loopStartFrame, frameCount);
                    int end = loopEndFrame < 0 ? frameCount : loopEndFrame;

                    // A degenerate or inverted range would otherwise loop forever on one frame.
                    if (end <= start + 1) return start;

                    return next >= end ? start : next;
                }

                // ClampForever and Manual both stop at the last frame. The shield is ClampForever:
                // visShit's own frame script calls stop() on frame 20 (visShit.as:19-21).
                default:
                    return next >= frameCount ? frameCount - 1 : next;
            }
        }

        /// <summary>
        /// Whether the overlay should be drawn at all: the caller's gate is on <i>and</i> there is
        /// something to draw.
        ///
        /// <para>Both halves matter. Gating on the flag alone draws sprite index 0 of an empty array
        /// for every character that has not had the overlay imported yet, and gating on the frames
        /// alone draws the shield for everyone.</para>
        /// </summary>
        public static bool ShouldDraw(bool gate, int frameCount)
        {
            return gate && frameCount > 0;
        }
    }
}
