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
    /// behaviour (<c>Interact.as:889</c>) — plus the <c>prob</c> test that precedes it.
    ///
    /// <para><b>The <c>prob</c> test is not a branch of the switch, and the port keeps it where the oracle
    /// has it.</b> <c>Interact.allAct</c> is <c>if(this.prob != null) … else if(this.allact == "…") …</c>
    /// (<c>Interact.as:1558-1650</c>), so an object carrying a non-empty <c>prob</c> never runs an
    /// <c>allact</c> at all. Modelling it as another registered action would have made the two branches
    /// siblings, and the exit loop depends on them being ordered — see <see cref="Dispatch"/>.</para>
    ///
    /// <para><b>It names what it does not handle.</b> AS3's switch has around twenty branches
    /// (<c>hack_robot</c>, <c>hack_lock</c>, <c>bind</c>, <c>map</c>, <c>stand</c>, <c>exit</c>,
    /// <c>vault</c>, <c>electro_check</c>, <c>prob_help</c>, <c>robocell</c>, <c>alarm</c>,
    /// <c>work</c>/<c>lab</c>/<c>stove</c>, <c>app</c>, <c>comein</c>, …). <c>comein</c>, <c>map</c>,
    /// <c>exit</c> and <c>probreturn</c> are registered by <see cref="CreateDefault"/>; the rest are not
    /// ported, so an unregistered id is reported <b>once per id</b> and the caller falls back to its own
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

        /// <summary>
        /// The prob-room capability, or null when none was supplied. Kept as a field rather than as a
        /// registered action because the <c>prob</c> branch is <b>not</b> an <c>allact</c> value — it is
        /// tested before the switch, off the placement's own attribute. See <see cref="Dispatch"/>.
        /// </summary>
        private readonly IProbRoomHost _probHost;

        /// <summary>
        /// The pseudo-id the <c>prob</c> branch reports itself under when it cannot run. It is not an
        /// <c>allact</c> any object carries, which is the point: a reader who greps for it finds this
        /// file and not a definition row, so it cannot be mistaken for a script name.
        /// </summary>
        public const string ProbEntryReportId = "prob";

        /// <param name="reportUnhandled">
        /// Sink for the once-per-id "no handler" notice. Defaults to <c>Debug.LogWarning</c>; a test
        /// supplies a collector so the notice is asserted rather than merely printed.
        /// </param>
        public ObjectActionDispatcher(Action<string> reportUnhandled = null, IProbRoomHost probHost = null)
        {
            _report = reportUnhandled ?? (message => Debug.LogWarning(message));
            _probHost = probHost;
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
        /// <param name="travelMapHost">
        /// Supplies <c>allact='map'</c> — the camp's wall map. <b>Null is tolerated</b>, for the same
        /// reason <paramref name="transition"/> is: the map object must degrade to "reported as
        /// unhandled", not to a thrown exception out of an interaction. A host that is present but whose
        /// travel is locked is a different state and is handled inside <see cref="MapAction"/>.
        /// </param>
        /// <param name="exitHost">
        /// Supplies <c>allact='exit'</c> — the campaign-level "end this level" step
        /// (<c>Interact.as:1646-1649</c> → <c>Game.gotoNextLevel</c>, <c>Game.as:464-472</c>). This is
        /// normally the same <c>CampaignManager</c> the map host is. <b>Null is tolerated and reported</b>:
        /// leaving <c>exit</c> unregistered keeps the exit box on its old behaviour (it opens like a door)
        /// rather than throwing out of an interaction — but it is a wiring bug, so it is named once.
        /// </param>
        /// <param name="probHost">
        /// Supplies the <c>prob</c> branch and <c>allact='probreturn'</c> — entering and leaving a detached
        /// prob room (<c>Interact.as:1558-1578</c> → <c>Land.gotoProb</c>, <c>Land.as:1391-1438</c>).
        /// <b>Null is tolerated and reported</b>, and it degrades in two visible ways: <c>probreturn</c> is
        /// left unregistered, and every object carrying a <c>prob</c> attribute reports
        /// <see cref="ProbEntryReportId"/> as unhandled once. Without that, a prob door would silently open
        /// like an ordinary box — which is exactly the state the exit loop was stuck in.
        /// </param>
        public static ObjectActionDispatcher CreateDefault(
            IRoomLayerTransition transition,
            Action<string> reportUnhandled = null,
            PFE.Systems.Campaign.ITravelMapHost travelMapHost = null,
            PFE.Systems.Map.Scripting.ILandScriptHost exitHost = null,
            IProbRoomHost probHost = null)
        {
            var dispatcher = new ObjectActionDispatcher(reportUnhandled, probHost);

            if (transition == null)
            {
                // Leave `comein` unregistered so the caller stays on its own fallback. Registering it
                // with a null transition is impossible — ComeInAction refuses to be constructed that
                // way on purpose, because a `comein` that cannot toggle is a wiring bug, not a state.
                dispatcher._report(
                    "[ObjectAction] No IRoomLayerTransition is available, so `comein` is not registered: " +
                    "a Z door will fall back to opening instead of moving the player to the other layer.");
            }
            else
            {
                dispatcher.Register(new ComeInAction(transition));
            }

            if (travelMapHost == null)
            {
                dispatcher._report(
                    "[ObjectAction] No ITravelMapHost is available, so `map` is not registered: the camp's " +
                    "wall map (wmap) will report as unhandled instead of granting travel.");
            }
            else
            {
                dispatcher.Register(new MapAction(travelMapHost));
            }

            if (exitHost == null)
            {
                dispatcher._report(
                    "[ObjectAction] No ILandScriptHost is available, so `exit` is not registered: the exit " +
                    "box (AllData.as:5016) will fall back to opening instead of ending the level, and the " +
                    "descent loop will never advance.");
            }
            else
            {
                dispatcher.Register(new ExitAction(exitHost));
            }

            if (probHost == null)
            {
                dispatcher._report(
                    "[ObjectAction] No IProbRoomHost is available, so `probreturn` is not registered and no " +
                    "object's `prob` attribute can be honoured: doorprob / doorboss / the bottom-row exit box " +
                    "(Location.as:2119) will report as unhandled and fall back to opening. A prob room needs " +
                    "a transition manager that owns the room registry — see RoomTransitionManager.SetProbContext.");
            }
            else
            {
                dispatcher.Register(new ProbReturnAction(probHost));
            }

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
        /// Run the handler for the context object — AS3 <c>Interact.allAct()</c> (<c>Interact.as:1554-1660</c>)
        /// in its own order.
        ///
        /// <para><b>The <c>prob</c> test comes first, and that ordering is the mechanism, not a detail.</b>
        /// The oracle opens with</para>
        /// <code>
        /// if(this.prob != null)            { … this.loc.land.gotoProb(this.prob, this.owner.X, this.owner.Y); }
        /// else if(this.allact == "probreturn") { … gotoProb("", …) }
        /// else if(this.allact == "hack_robot") { … }
        /// …
        /// else if(this.allact == "exit")   { World.w.game.gotoNextLevel(); }
        /// </code>
        /// <para>so an object carrying a non-empty <c>prob</c> takes the prob path and <b>none</b> of the
        /// <c>allact</c> chain runs — including <c>exit</c>. The bottom-row exit box carries both
        /// (<c>prob</c> from its placement, <c>allact='exit'</c> from its definition row), so testing
        /// <c>allact</c> first would send the player straight to <c>gotoNextLevel</c>, skipping the
        /// detached exit room and the <c>upland</c> in its area script — the land would regenerate at the
        /// <i>same</i> stage for ever. Nothing would go red: the level would visibly rebuild, which looks
        /// exactly like progress.</para>
        ///
        /// <para><b>NotApplicable is impossible once a <c>prob</c> is present.</b> The switch's own "no
        /// script" answer is about <c>allact</c>; a prob carrier has a script by definition, so an
        /// unwired prob host must answer <see cref="ObjectActionOutcome.Unhandled"/> (the caller falls
        /// back) rather than <see cref="ObjectActionOutcome.NotApplicable"/>.</para>
        /// </summary>
        public ObjectActionOutcome Dispatch(in ObjectActionContext context)
        {
            string probId = Normalize(context.ProbId);

            if (probId.Length > 0)
            {
                if (_probHost == null)
                {
                    ReportOnce(ProbEntryReportId, context.ObjectId);
                    return ObjectActionOutcome.Unhandled;
                }

                if (!_probHost.CanEnterProb)
                {
                    // AS3 returns false from ativateLoc and moves nobody (Land.as:1427-1436). Refused, so
                    // the caller does not fall through to opening the door: a prob door that cannot open
                    // its room must not become an ordinary box.
                    return ObjectActionOutcome.Refused;
                }

                return _probHost.TryEnterProb(probId, context.WorldPosition)
                    ? ObjectActionOutcome.Handled
                    : ObjectActionOutcome.Refused;
            }

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
