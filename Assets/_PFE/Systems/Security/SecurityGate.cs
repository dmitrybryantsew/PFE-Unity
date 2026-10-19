using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Systems.Security
{
    /// <summary>
    /// The presenter-facing half of the security subsystem: what the prompt says, how long the hold is,
    /// and whether the press was consumed.
    ///
    /// <para><b>Why a static gate rather than a component.</b> The two presenters that need it
    /// (<c>DoorPropPresenter</c>, <c>ContainerPropPresenter</c>) already own their object and their
    /// room, and the state they need lives on the object. A component would have to be added, wired and
    /// destroyed alongside each of them for no gain — and the one thing it would own (the tick) is
    /// already owned once, by <see cref="SecurityRuntime"/>.</para>
    /// </summary>
    public static class SecurityGate
    {
        /// <summary>Whether this object currently has a lock to work — AS3 <c>lock &gt; 0 &amp;&amp; lock &lt; 100</c>.</summary>
        public static bool IsLocked(ObjectInstance obj, RoomInstance room)
        {
            SecurityProfile profile = SecurityRuntime.ProfileFor(obj, room);
            return profile != null && (profile.IsLocked || profile.Mine > 0);
        }

        /// <summary>
        /// The prompt for a locked object — AS3 <c>Interact.update()</c>'s <c>actionText</c> table
        /// (<c>:715-776</c>).
        ///
        /// <para><b>A seized lock has no prompt.</b> AS3 sets <c>actionText = ""</c> for
        /// <c>lockTip == 0</c> and leaves the entry guards to refuse the press
        /// (<c>UnitPlayer.as:1964-1973</c>); a prompt that says "Pick lock" on a lock that can never
        /// open is worse than no prompt.</para>
        /// </summary>
        public static string Prompt(ObjectInstance obj, RoomInstance room, string fallback)
        {
            SecurityProfile profile = SecurityRuntime.ProfileFor(obj, room);
            if (profile == null) return fallback;

            if (profile.Mine > 0)
            {
                return profile.MineTip == 6 ? "Shut off" : "Defuse";
            }

            if (!profile.IsLocked) return fallback;

            switch (profile.LockTip)
            {
                case SecurityLockTip.Terminal:
                    return "Hack";
                case SecurityLockTip.Pick:
                    return "Pick lock";
                case SecurityLockTip.RepairWeapon:
                case SecurityLockTip.RepairPart:
                    return "Fix";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Extra hold frames a locked object adds on top of its authored <c>time</c> — AS3
        /// <c>t_action = inter.t_action + pers.getLockPickTime(lock, tip)</c>
        /// (<c>UnitPlayer.as:1976</c>).
        ///
        /// <para><b>This is what makes a lock a decision rather than a click.</b> A pick is 30 ticks
        /// (one second) and 18 when the lock is below the actor's rank (<c>Pers.as:2381-2389</c>), so a
        /// player standing in a corridor is exposed for a full second per attempt.</para>
        /// </summary>
        public static int ExtraHoldFrames(ObjectInstance obj, RoomInstance room)
        {
            SecurityProfile profile = SecurityRuntime.ProfileFor(obj, room);
            if (profile == null) return 0;

            if (profile.Mine > 0)
            {
                SecurityActor mineActor = SecurityRuntime.ReadActor(SecurityRuntime.PlayerObject);
                return mineActor.LockPickTime > 0 ? mineActor.LockPickTime : 30;
            }

            if (!profile.IsLocked) return 0;

            SecurityActor actor = SecurityRuntime.ReadActor(SecurityRuntime.PlayerObject);
            int lockPickTime = actor.LockPickTime > 0 ? actor.LockPickTime : 30;
            int skill = SecurityRules.SkillFor(profile.LockTip, actor);
            return profile.Lock < skill ? Mathf.RoundToInt(lockPickTime * 0.6f) : lockPickTime;
        }

        /// <summary>
        /// Run one press against the object's lock. See <see cref="SecurityRuntime.TryWork"/>.
        /// </summary>
        public static SecurityGateOutcome TryWork(GameObject user, ObjectInstance obj, RoomInstance room)
        {
            SecurityGateOutcome outcome = SecurityRuntime.TryWork(user, obj, room, out SecurityAttemptResult result);

            // The port has no gui.infoText strip; a Debug line is the only immediate feedback besides
            // the F2 log. One line per press is cheap and is the difference between "the lock is hard"
            // and "the press never arrived".
            if (outcome != SecurityGateOutcome.NotLocked)
            {
                Debug.Log($"[Security] {obj?.objectId}: {result.MessageKey} — {SecurityRuntime.Describe(result)}" +
                          (result.Delta != 0 ? $" (delta {result.Delta}, chance {result.SuccessChance:P0})" : string.Empty));
            }

            return outcome;
        }
    }

    /// <summary>
    /// A presenter whose GameObject stands for a map object that can carry a lock — implemented by
    /// <c>DoorPropPresenter</c> and <c>ContainerPropPresenter</c>.
    ///
    /// <para><b>Why an interface rather than two casts in the consumer.</b> The only consumer is the F2
    /// Security tab, which has to answer "what lock am I looking at" from
    /// <c>PlayerController.CurrentInteractTarget</c> — typed <c>IInteractable</c>, which says nothing
    /// about locks. Casting to the two presenter types would work today and would silently stop
    /// covering the third presenter that gains a lock tomorrow; the members already exist on both
    /// presenters under these exact names, so implementing this costs one word per class and no new
    /// state.</para>
    /// </summary>
    public interface ISecurityObject
    {
        /// <summary>The map object this presenter draws.</summary>
        ObjectInstance ObjectInstance { get; }

        /// <summary>The room the object belongs to — the scope its profile and alarm live in.</summary>
        RoomInstance Room { get; }
    }
}
