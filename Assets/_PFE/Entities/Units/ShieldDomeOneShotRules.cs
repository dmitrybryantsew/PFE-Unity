using PFE.Character.Animation;
using PFE.Data.Definitions;

namespace PFE.Entities.Units
{
    /// <summary>
    /// The playhead of the <b>boss alicorn's</b> shield dome — AS3 <c>UnitBossAlicorn</c>'s
    /// <c>visShit3</c>, which is a one-shot break rather than a shield pool.
    ///
    /// <para><b>Why this is not in <see cref="UnitShieldOverlayRules"/>.</b> That class is in
    /// <c>PFE.Data</c>, which references <c>PFE.Sim</c> and <c>PFE.ModAPI</c> and <b>not</b>
    /// <c>PFE.View</c> — so it cannot see <see cref="OverlayClip"/>, the playhead arithmetic it would
    /// have to reuse. Rather than duplicate the frame rules (and then keep two copies of
    /// <c>NoFrame</c> agreeing), the dome's <i>selection</i> stays in Data and its <i>driver</i> lives
    /// here, next to its only consumer, <see cref="UnitShieldOverlay"/>. The assembly boundary is the
    /// reason; it is not an accident of file layout.</para>
    ///
    /// <para><b>The oracle, which is two lines and no pool.</b> <c>UnitBossAlicorn</c> declares
    /// <c>shitMaxHp = 400</c>, <c>t_shit = 300</c> and <c>shitArmor = 15</c> and then <b>never assigns
    /// <c>shithp</c></b>, so the family alicorn's <c>if(shithp &gt; 0)</c> poll never fires for it.
    /// The clip is driven explicitly instead:</para>
    ///
    /// <code>
    /// // constructor :123-127
    /// this.visshit = new visShit3(); vis.addChild(this.visshit); this.visshit.gotoAndStop(1);
    ///
    /// // die() :709-712
    /// if(this.isShit) { this.isShit = false; this.visshit.gotoAndPlay(2); hp = maxhp - 0.1; ... }
    /// </code>
    ///
    /// <para>Three consequences, each of which the pool dome gets <i>wrong</i> if reused unchanged:</para>
    /// <list type="number">
    /// <item><description><b>The dome is drawn from spawn.</b> <c>gotoAndStop(1)</c> runs in the
    ///     constructor, so there is no rise edge at all — the Super Alicorn arrives with her dome on.
    ///     The pool driver's "hidden until <c>shithp &gt; 0</c>" would leave her bare for the whole
    ///     fight, because her pool never fills.</description></item>
    /// <item><description><b>The break starts on frame 2, not frame 1.</b> <c>gotoAndPlay(2)</c>
    ///     skips the intact pose, so the shatter begins on the hit that caused it. Starting at frame 1
    ///     replays "nothing has happened yet" for a fifth of a second.</description></item>
    /// <item><description><b>The break ends.</b> <c>visShit</c> is <c>ClampForever</c> — the dome is
    ///     meant to stay materialised — while the break must play out and stop. There is no
    ///     <c>visible = false</c> in the oracle; it simply leaves the clip on its final, faded frame.
    ///     See <see cref="NextBreakFrame"/> for why the port hides instead of holding.</description></item>
    /// </list>
    ///
    /// <para><b>What the port does <i>not</i> do, deliberately.</b> Nothing re-shows the dome after a
    /// break. AS3 touches <c>visshit</c> in exactly two places (the constructor and <c>die()</c>), and
    /// <c>setNull():203-207</c> restores <c>isShit = true</c> without ever touching the clip — so a
    /// respawned boss is armoured again with no dome on screen, which is the oracle's own behaviour.
    /// The port has no boss respawn path at all, so there is nothing to restore today; if one is added,
    /// this paragraph is where the decision is recorded.</para>
    /// </summary>
    public static class ShieldDomeOneShotRules
    {
        /// <summary>
        /// Whether a dome kind is driven by the one-shot break rather than by a shield pool.
        /// </summary>
        /// <remarks>
        /// Exactly the <see cref="UnitShieldOverlayKind.Boss"/> clip. <c>UnitBossAlicorn</c> is the only
        /// class in the oracle that instantiates <c>visShit3</c> and the only one that calls
        /// <c>gotoAndPlay(2)</c> on its own dome; the tier-3 alicorn's <c>visShit2</c> is a pool dome
        /// like the ordinary one. Keyed on the kind, therefore, and not on "the big dome".
        /// </remarks>
        public static bool IsOneShot(UnitShieldOverlayKind kind)
        {
            return kind == UnitShieldOverlayKind.Boss;
        }

        /// <summary>
        /// The frame an intact one-shot dome is parked on — AS3 <c>gotoAndStop(1)</c> in the constructor.
        /// </summary>
        /// <returns><c>0</c>, or <see cref="OverlayClip.NoFrame"/> when the clip carries no frames.</returns>
        public static int IntactFrame(int frameCount)
        {
            return OverlayClip.RestartFrame(frameCount);
        }

        /// <summary>
        /// The frame the break starts on — AS3 <c>gotoAndPlay(2)</c>, i.e. <b>index 1</b>.
        /// </summary>
        /// <remarks>
        /// A single-frame clip degrades to its only frame rather than to
        /// <see cref="OverlayClip.NoFrame"/>, so a placeholder import still shatters something instead
        /// of silently drawing nothing at the phase transition.
        /// </remarks>
        public static int BreakStartFrame(int frameCount)
        {
            if (frameCount <= 0) return OverlayClip.NoFrame;
            return frameCount > 1 ? 1 : 0;
        }

        /// <summary>
        /// The next playhead of a break, or <see cref="OverlayClip.NoFrame"/> when the break has played
        /// out and the dome should disappear.
        /// </summary>
        /// <remarks>
        /// <b>No clamp at the end, unlike the pool dome.</b> Every frame from
        /// <see cref="BreakStartFrame"/> to the last is drawn exactly once, and the step after the last
        /// returns <see cref="OverlayClip.NoFrame"/>. AS3 instead leaves <c>visshit</c> on its final
        /// frame forever; that frame is the fully faded-out pose, so "hold the last frame" and "hide"
        /// are the same picture — and hiding is one draw cheaper and cannot leave a stale sprite on
        /// screen if the clip is ever re-imported with a visible last frame.
        /// </remarks>
        public static int NextBreakFrame(int frame, int frameCount)
        {
            if (frameCount <= 0) return OverlayClip.NoFrame;

            int next = OverlayClip.ClampFrame(frame, frameCount) + 1;
            return next >= frameCount ? OverlayClip.NoFrame : next;
        }
    }
}
