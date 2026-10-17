using UnityEngine;

namespace PFE.Systems.Interaction
{
    /// <summary>
    /// A view that can be told <b>where in the world</b> to draw itself, in addition to how far along
    /// it is.
    ///
    /// <para><b>Why the position is not part of <see cref="IActionProgressView"/>.</b> That interface
    /// is the timer's seam and is deliberately about a <i>number</i>: <c>Show(progress)</c> /
    /// <c>Hide()</c>, nothing else. Adding a position to it would force every implementation — including
    /// <see cref="NullActionProgressView"/> and every test double — to answer a question most of them
    /// have no answer for, and a HUD bar that is genuinely screen-anchored would have to accept an
    /// argument it discards.</para>
    ///
    /// <para><b>What it is for.</b> The Oracle's hold bar <c>pr_bar</c> is not a screen bar: it is
    /// re-projected onto the object being acted on every frame —
    /// <c>cam.setKoord(this.pr_bar, this.gg.actionObj.owner.X, this.gg.actionObj.owner.Y)</c>
    /// (<c>GUI.as:1302</c>) — and then rides with the interaction prompt
    /// (<c>celobj.y = pr_bar.y = …</c>, <c>GUI.as:1463</c>). A view implementing this interface is one
    /// that can honour that; the caller pushes the anchor when a hold begins, exactly as it pushes
    /// progress.</para>
    ///
    /// <para><b>Push, not pull.</b> Like <see cref="IActionProgressView"/>, an implementation is a
    /// passive sink: it must not read the target back, and it may be called at most once per hold.
    /// A view that does not implement this interface simply draws where it always did — the caller
    /// discovers the capability with <c>is IWorldAnchorSink</c>.</para>
    /// </summary>
    public interface IWorldAnchorSink
    {
        /// <summary>
        /// The world position the bar should be drawn over — the <i>target's</i> position, not the
        /// player's. AS3 passes <c>actionObj.owner.X/Y</c>, i.e. the object being acted on.
        /// </summary>
        void SetWorldAnchor(Vector3 worldPosition);
    }
}
