using UnityEngine;
using PFE.Core;
using PFE.Systems.Interaction;

namespace PFE.Entities.Player
{
    /// <summary>
    /// Runs the player's hold-to-act gesture: press, hold for the object's authored duration, then
    /// act — or let go and abandon it.
    ///
    /// <para><b>What it replaces.</b> AS3 keeps this state on the player itself: <c>actionObj</c>
    /// (<c>UnitPlayer.as:67</c>), <c>t_action</c> (<c>:69</c>), <c>mt_action</c> (<c>:71</c>), the
    /// decrement and fire at <c>:1071-1086</c>, the start at <c>:1922-2001</c> and the abandon on key
    /// release at <c>:2131-2135</c>. That is one concern spread over 500 lines of a 5,000-line class.
    /// Here it is the whole job of one component, and the countdown itself lives in
    /// <see cref="ActionTimer"/> / <see cref="TimedActionSlot{TPayload}"/>, where it can be tested
    /// without a scene.</para>
    ///
    /// <para><b>Why it ticks on <see cref="SimLoop"/> and not on <c>FixedUpdate</c>.</b> The duration
    /// is an AS3 frame count, and one AS3 frame is one sim tick. The legacy <c>FixedUpdate</c> path
    /// runs at Unity's fixed rate — 50 Hz by default — so counting there would finish a
    /// <c>time='10'</c> hold in 0.2 s instead of 0.33 s: the same 1.67x clock error the tile motor is
    /// documented to have (<c>GoldenTraceHarness</c>). <c>FixedUpdate</c> is kept only as a fallback
    /// for the case where nothing attached a sim loop, so a hold can never silently never
    /// complete.</para>
    ///
    /// <para><b>Order: <see cref="SimTickOrder.Triggers"/>.</b> After movement has settled, so the
    /// range guard sees this tick's final position, and so a completed hold that opens a Z door runs
    /// at the same order as the other door work.</para>
    /// </summary>
    public sealed class PlayerActionInteractor : MonoBehaviour, ISimTickable
    {
        /// <summary>
        /// Everything needed to run the effect once a hold completes, or to unwind it when one is
        /// abandoned: what is being acted on, who is acting, and where the effect happens.
        ///
        /// <para><see cref="WorldPosition"/> is the <b>object's</b> position, not the player's. That
        /// distinction is load-bearing for <c>comein</c>: AS3's <c>Land.gotoLoc(5, x, y)</c> takes the
        /// arrival cell from the coordinates it is handed (<c>Land.as:1367-1371</c>), so passing the
        /// player's position would put the player somewhere else entirely.</para>
        /// </summary>
        public readonly struct HoldTarget
        {
            public HoldTarget(IHoldInteractable target, GameObject user, Vector3 worldPosition)
            {
                Target = target;
                User = user;
                WorldPosition = worldPosition;
            }

            /// <summary>The object being held.</summary>
            public IHoldInteractable Target { get; }

            /// <summary>The unit doing the holding — the player.</summary>
            public GameObject User { get; }

            /// <summary>The object's world position, i.e. where the action happens.</summary>
            public Vector3 WorldPosition { get; }

            public override string ToString()
            {
                return "HoldTarget(" + (Target != null ? Target.GetType().Name : "null") + " @ " +
                       WorldPosition + ")";
            }
        }

        /// <summary>
        /// How far the player may drift from the object before a hold in flight is abandoned, in world
        /// units. AS3 makes the same check in <c>actAction()</c> and nulls <c>actionObj</c> once the
        /// squared distance exceeds <c>World.w.actionDist</c> (<c>UnitPlayer.as:1924-1930</c>).
        ///
        /// <para>2.5 rather than AS3's 2.0 m: the port already admits an interaction at 2.5
        /// (<c>DoorPropPresenter.CanInteract</c>, <c>PlayerController.TryCursorInteract</c>), and a
        /// guard tighter than the admission would cancel holds the player never left.</para>
        /// </summary>
        [SerializeField]
        [Tooltip("World units the player may drift from the object before the hold is abandoned. " +
                 "Matches the port's 2.5 m interaction range (AS3 actionDist is 2.0 m).")]
        private float _holdRange = 2.5f;

        private readonly TimedActionSlot<HoldTarget> _slot;

        private IActionProgressView _view;
        private SimClock _clock;
        private SimLoop _loop;
        private bool _attached;
        private bool _warnedAboutFallbackDriver;
        private bool _warnedAboutMissingHold;

        public PlayerActionInteractor()
        {
            // The view is reached through a forwarder rather than captured, so a HUD bar can be bound
            // after this component exists — or swapped later — without rebuilding the slot.
            _slot = new TimedActionSlot<HoldTarget>(new ViewForwarder(this));
        }

        /// <summary>
        /// Where hold progress is drawn. Null (the default) draws nothing. Settable at any time,
        /// including while a hold is in flight.
        /// </summary>
        public IActionProgressView View
        {
            get => _view;
            set => _view = value;
        }

        /// <summary>True while the player is holding an action key on a timed object.</summary>
        public bool IsHolding => _slot.IsActive;

        /// <summary>Fraction of the hold completed, 0&#8594;1. 0 when nothing is held.</summary>
        public float HoldProgress => _slot.Progress;

        /// <summary>Frames left on the hold in flight.</summary>
        public int RemainingHoldFrames => _slot.RemainingFrames;

        /// <summary>See <see cref="ISimTickable.TickOrder"/>.</summary>
        public int TickOrder => SimTickOrder.Triggers;

        /// <summary>
        /// Hands this component to the fixed-step simulation. Mirrors
        /// <c>TilePhysicsController.AttachSimulation</c> deliberately: same null guard, same
        /// "not attached means the legacy driver" contract.
        /// </summary>
        public void AttachSimulation(SimClock clock, SimLoop loop)
        {
            if (clock == null || loop == null)
            {
                Debug.LogWarning(
                    "[PFE] PlayerActionInteractor.AttachSimulation called with a null clock or loop; " +
                    "holds will run on FixedUpdate and be about 1.67x fast.", this);
                return;
            }

            _clock = clock;
            _loop = loop;
            _attached = true;
            _loop.Register(this);
        }

        /// <summary>
        /// Starts a hold on <paramref name="target"/>, if it wants one.
        ///
        /// <para>Returns false when the object authors no duration — the caller must then take its
        /// immediate path, because AS3 fires a zero-time action on the spot
        /// (<c>UnitPlayer.as:1985-1988</c>) rather than arming a timer. Also returns false when a hold
        /// is already in flight, which is the single-<c>actionObj</c> rule.</para>
        /// </summary>
        public bool TryBeginHold(IHoldInteractable target, GameObject user, Vector3 worldPosition)
        {
            if (target == null)
            {
                return false;
            }

            TimedActionBeginResult result = _slot.TryBegin(
                target.HoldFrames,
                new HoldTarget(target, user, worldPosition));

            if (result == TimedActionBeginResult.Begun)
            {
                return true;
            }

            if (result == TimedActionBeginResult.NotATimedAction && !_warnedAboutMissingHold)
            {
                // The intended caller checks HoldFrames > 0 before calling, so reaching here means the
                // caller's hold/immediate branch disagrees with the object. Named once rather than
                // swallowed: silently doing nothing is how a "the door stopped working" report starts.
                _warnedAboutMissingHold = true;
                Debug.LogWarning(
                    "[PFE] TryBeginHold called for '" + target.GetType().Name + "', which authors no " +
                    "hold duration. The caller should have taken the immediate Interact path. This is " +
                    "reported once per interactor.", this);
            }

            return false;
        }

        /// <summary>
        /// Abandons the hold in flight. This is the port of the released-key path
        /// (<c>UnitPlayer.as:2131-2135</c>): progress is discarded, nothing fires.
        ///
        /// <para>Safe to call unconditionally on every key-up, including when no hold was running.</para>
        /// </summary>
        public void ReleaseHold()
        {
            _slot.Cancel();
        }

        /// <summary>One authoritative simulation step. See <see cref="ISimTickable"/>.</summary>
        public void SimTick(int tickIndex)
        {
            AdvanceHold();
        }

        /// <summary>
        /// Fallback driver for the case where no sim loop was attached — e.g. the component was added
        /// to a scene without <c>MapBridge</c>. Unity's fixed step is not AS3's 30 Hz, so a hold is
        /// proportionally fast; that is warned about rather than left silent, because the alternative
        /// failure mode is a door that never opens at all.
        /// </summary>
        private void FixedUpdate()
        {
            if (_attached)
            {
                return;
            }

            if (!_warnedAboutFallbackDriver && _slot.IsActive)
            {
                _warnedAboutFallbackDriver = true;
                float actualRate = Time.fixedDeltaTime > 0f ? 1f / Time.fixedDeltaTime : 0f;
                Debug.LogWarning(
                    "[PFE] PlayerActionInteractor has no SimLoop attached, so hold timers run on " +
                    "FixedUpdate at " + actualRate.ToString("0.#") + " Hz instead of the simulation's " +
                    (_clock != null ? _clock.TicksPerSecond.ToString() : "configured") +
                    " Hz. Hold durations will not match AS3. Attach via AttachSimulation.", this);
            }

            AdvanceHold();
        }

        private void AdvanceHold()
        {
            if (!_slot.IsActive)
            {
                return;
            }

            // Re-validate the target before spending a tick on it. AS3 drops the action when the object
            // goes away (actionObj.active == 0, UnitPlayer.as:1073-1076) and when the player walks out
            // of range (:1924-1930); both are checked here.
            if (!CanContinueHold(_slot.ActivePayload))
            {
                _slot.Cancel();
                return;
            }

            TimedActionTick<HoldTarget> tick = _slot.Advance();
            if (tick.Result == TimedActionTickResult.Completed)
            {
                // The payload comes from the tick, not from the slot: that is what guarantees the
                // effect runs against the target that was held, exactly once.
                HoldTarget completed = tick.Payload;
                completed.Target.Interact(completed.User);
            }
        }

        private bool CanContinueHold(HoldTarget target)
        {
            if (target.Target == null || target.User == null)
            {
                return false;
            }

            // A destroyed MonoBehaviour is "fake null" through its interface reference, so calling into
            // it would throw MissingReferenceException rather than return false. The UnityEngine.Object
            // test must therefore come before any call on the interface.
            if (target.Target is UnityEngine.Object unityObject && unityObject == null)
            {
                return false;
            }

            float driftSq = ((Vector2)target.User.transform.position -
                             (Vector2)target.WorldPosition).sqrMagnitude;
            if (driftSq > _holdRange * _holdRange)
            {
                return false;
            }

            return target.Target.CanInteract(target.User);
        }

        private void OnEnable()
        {
            // Re-register across a disable/enable cycle, exactly as TilePhysicsController does.
            // SimLoop.Register is idempotent.
            if (_attached && _loop != null)
            {
                _loop.Register(this);
            }
        }

        private void OnDisable()
        {
            // A disabled owner must not leave a half-counted hold behind, nor a bar on screen.
            _slot.Reset();
        }

        private void OnDestroy()
        {
            if (_attached && _loop != null)
            {
                _loop.Unregister(this);
            }
        }

        /// <summary>Forwards progress to whatever view is currently bound. See the constructor.</summary>
        private sealed class ViewForwarder : IActionProgressView
        {
            private readonly PlayerActionInteractor _owner;

            public ViewForwarder(PlayerActionInteractor owner)
            {
                _owner = owner;
            }

            public void Show(float progress)
            {
                (_owner._view ?? NullActionProgressView.Instance).Show(progress);
            }

            public void Hide()
            {
                (_owner._view ?? NullActionProgressView.Instance).Hide();
            }
        }
    }
}
