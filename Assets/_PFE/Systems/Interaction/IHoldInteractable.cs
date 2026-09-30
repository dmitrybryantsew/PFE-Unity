using UnityEngine;

namespace PFE.Systems.Interaction
{
    /// <summary>
    /// An <see cref="IInteractable"/> that must be <b>held</b> for a while before it acts, rather than
    /// firing on the press.
    ///
    /// <para><b>Why a derived interface and not a member on <see cref="IInteractable"/>.</b> Most
    /// interactive things in PFE have no hold at all: only 22 of the 19,461 <c>&lt;obj&gt;</c>
    /// definitions author a <c>time</c>, so a <c>HoldFrames</c> on the base interface would be 0 for
    /// almost every implementor and every implementor would have to answer a question that has no
    /// answer. Worse, adding a member to <see cref="IInteractable"/> would break every existing
    /// implementor and every test double at the point of declaration. A derived interface is additive:
    /// an object opts in by implementing it, and a caller discovers it with
    /// <c>is IHoldInteractable</c>.</para>
    ///
    /// <para><b>The contract a caller relies on.</b> The caller presses, asks
    /// <see cref="HoldFrames"/>, and if it is positive it runs a
    /// <see cref="TimedActionSlot{TPayload}"/> for that many frames, calling
    /// <see cref="IInteractable.Interact"/> only when the hold completes — and <b>not</b> calling it
    /// if the key is released first. So an implementation must treat <c>Interact</c> as
    /// "the hold finished", not "the button went down"; that is already true of AS3, where
    /// <c>Interact.act()</c> is reached only from the timer's completion branch
    /// (<c>UnitPlayer.as:1081-1086</c>).</para>
    ///
    /// <para><b>Zero is a valid answer and it means "no hold".</b> An implementation should return 0
    /// rather than throw when the underlying object authors no duration, so a single presenter can
    /// serve both instant and held objects.</para>
    /// </summary>
    public interface IHoldInteractable : IInteractable
    {
        /// <summary>
        /// Frames the action key must be held before <see cref="IInteractable.Interact"/> is called.
        /// 0 (or less) means the object acts immediately on the press, which is the default for
        /// everything that authors no <c>time</c>.
        /// </summary>
        int HoldFrames { get; }

        /// <summary>
        /// The object's position in world space — the anchor for the "walked away" guard and the
        /// coordinates the effect is run against.
        ///
        /// <para>This is the object's own position, not the point on its collider the cursor hit, and
        /// not the player's. AS3's <c>actAction()</c> measures the range guard against
        /// <c>actionObj.X/Y</c> (<c>UnitPlayer.as:1926</c>) and <c>Land.gotoLoc(5, x, y)</c> takes the
        /// arrival cell from the coordinates it is handed (<c>Land.as:1367-1371</c>) — so for
        /// <c>comein</c> the difference between this and the player's position decides which side of
        /// the Z door the player ends up on.</para>
        /// </summary>
        Vector3 WorldPosition { get; }
    }
}
