using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Actions
{
    /// <summary>
    /// What <see cref="ObjectActionDispatcher.Dispatch"/> decided. The caller needs to tell these apart:
    /// <see cref="Refused"/> must <b>not</b> fall through to the object's own behaviour, because AS3
    /// refuses a toggle by returning null and leaving the player where they are.
    /// </summary>
    public enum ObjectActionOutcome
    {
        /// <summary>The object carries no <c>allact</c>, so it has no script. The caller's own behaviour applies.</summary>
        NotApplicable,

        /// <summary>The object has an <c>allact</c>, but no handler is registered for it. Reported once.</summary>
        Unhandled,

        /// <summary>A handler is registered and declined to run (or ran and changed nothing).</summary>
        Refused,

        /// <summary>A handler ran.</summary>
        Handled,
    }

    /// <summary>
    /// The port of AS3's <c>Interact.act()</c> switch — the single place an <c>allact</c> value becomes
    /// behaviour (<c>Interact.as:889</c>).
    ///
    /// <para><b>It names what it does not handle.</b> AS3's switch has around twenty branches
    /// (<c>hack_robot</c>, <c>hack_lock</c>, <c>bind</c>, <c>map</c>, <c>stand</c>, <c>exit</c>,
    /// <c>vault</c>, <c>electro_check</c>, <c>prob_help</c>, <c>robocell</c>, <c>alarm</c>,
    /// <c>work</c>/<c>lab</c>/<c>stove</c>, <c>app</c>, <c>comein</c>, …). Porting them all is not this
    /// change, so an unregistered id is reported <b>once per id</b> and the caller falls back to its own
    /// behaviour. Without that, a missing branch is indistinguishable from a door that simply does not
    /// respond — the same failure shape as a parser that silently drops what it cannot match.</para>
    ///
    /// <para>The fallback is deliberate rather than incidental: several objects that carry an
    /// unported <c>allact</c> (the <c>term*</c> terminals, for instance) currently open and close
    /// through the door presenter, and inventing behaviour for them here would change gameplay in a
    /// pass whose subject is the z axis.</para>
    /// </summary>
    public sealed class ObjectActionDispatcher
    {
        private readonly Dictionary<string, IObjectAction> _actions =
            new Dictionary<string, IObjectAction>(StringComparer.Ordinal);

        private readonly HashSet<string> _reportedUnhandled =
            new HashSet<string>(StringComparer.Ordinal);

        private readonly Action<string> _report;

        /// <param name="reportUnhandled">
        /// Sink for the once-per-id "no handler" notice. Defaults to <c>Debug.LogWarning</c>; a test
        /// supplies a collector so the notice is asserted rather than merely printed.
        /// </param>
        public ObjectActionDispatcher(Action<string> reportUnhandled = null)
        {
            _report = reportUnhandled ?? (message => Debug.LogWarning(message));
        }

        /// <summary>
        /// A dispatcher with the ported <c>allact</c> scripts registered.
        /// </summary>
        /// <param name="transition">
        /// The layer-transition capability <c>comein</c> needs. <b>Null is tolerated</b>: a scene with
        /// no <c>RoomTransitionManager</c> would otherwise throw out of
        /// <c>DoorPropPresenter.Interact</c>, which is a far worse failure than a door that opens
        /// instead of teleporting. The caller is told once, by name.
        /// </param>
        /// <param name="reportUnhandled">See the constructor.</param>
        public static ObjectActionDispatcher CreateDefault(
            IRoomLayerTransition transition,
            Action<string> reportUnhandled = null)
        {
            var dispatcher = new ObjectActionDispatcher(reportUnhandled);

            if (transition == null)
            {
                // Leave `comein` unregistered so the caller stays on its own fallback. Registering it
                // with a null transition is impossible — ComeInAction refuses to be constructed that
                // way on purpose, because a `comein` that cannot toggle is a wiring bug, not a state.
                dispatcher._report(
                    "[ObjectAction] No IRoomLayerTransition is available, so `comein` is not registered: " +
                    "a Z door will fall back to opening instead of moving the player to the other layer.");
                return dispatcher;
            }

            dispatcher.Register(new ComeInAction(transition));
            return dispatcher;
        }

        /// <summary>The ids this dispatcher can handle.</summary>
        public IEnumerable<string> RegisteredActionIds => _actions.Keys;

        /// <summary>The ids reported as unhandled so far. Exposed so a test can pin "once".</summary>
        public IEnumerable<string> ReportedUnhandled => _reportedUnhandled;

        /// <summary>
        /// Register a handler. Registering two handlers for one id is a programming error that would
        /// otherwise shadow silently, so it throws and names the id.
        /// </summary>
        public void Register(IObjectAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            string id = Normalize(action.ActionId);
            if (id.Length == 0)
            {
                throw new ArgumentException(
                    $"{action.GetType().Name} has an empty ActionId; an action must name the `allact` " +
                    "value it handles.", nameof(action));
            }

            if (_actions.ContainsKey(id))
            {
                throw new InvalidOperationException(
                    $"Two handlers are registered for allact '{id}' " +
                    $"({_actions[id].GetType().Name} and {action.GetType().Name}). One would silently win.");
            }

            _actions[id] = action;
        }

        /// <summary>Whether <paramref name="actionId"/> has a handler.</summary>
        public bool Handles(string actionId)
        {
            string id = Normalize(actionId);
            return id.Length > 0 && _actions.ContainsKey(id);
        }

        /// <summary>
        /// Run the handler for the context object's <c>allact</c>.
        /// </summary>
        public ObjectActionOutcome Dispatch(in ObjectActionContext context)
        {
            string id = Normalize(context.ActionId);

            if (id.Length == 0)
            {
                return ObjectActionOutcome.NotApplicable;
            }

            if (!_actions.TryGetValue(id, out IObjectAction action))
            {
                ReportOnce(id, context.ObjectId);
                return ObjectActionOutcome.Unhandled;
            }

            if (!action.CanExecute(in context))
            {
                return ObjectActionOutcome.Refused;
            }

            return action.Execute(in context)
                ? ObjectActionOutcome.Handled
                : ObjectActionOutcome.Refused;
        }

        /// <summary>
        /// Trim only. The comparison stays <see cref="StringComparer.Ordinal"/> because AS3 compares
        /// <c>allact</c> with <c>==</c> against lowercase literals (<c>Interact.as:1619</c>), and the
        /// imported data is lowercase — so accepting a differently-cased id would be an invention.
        /// </summary>
        private static string Normalize(string actionId)
        {
            return string.IsNullOrEmpty(actionId) ? string.Empty : actionId.Trim();
        }

        private void ReportOnce(string id, string objectId)
        {
            if (!_reportedUnhandled.Add(id))
            {
                return;
            }

            _report(
                $"[ObjectAction] allact '{id}' has no handler" +
                (string.IsNullOrEmpty(objectId) ? string.Empty : $" (first seen on '{objectId}')") +
                ". Registered scripts: " + string.Join(", ", _actions.Keys) +
                ". Falling back to the object's own behaviour — this branch of AS3's " +
                "Interact.act() switch is either not ported yet or not wired.");
        }
    }
}
