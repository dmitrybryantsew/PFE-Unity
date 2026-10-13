using System;
using System.Collections.Generic;

namespace PFE.Systems.Map.Actions
{
    /// <summary>
    /// The capability bag the terminal <c>allact</c> branches need, all optional.
    ///
    /// <para><b>Each member names the AS3 call it stands for</b>, because the whole difficulty of this
    /// vocabulary is that a branch is one line in the oracle and a whole subsystem in the port — and the
    /// line is what has to be honoured. A member that is null is not an error: the branch reports which
    /// call it could not make, by name, and does nothing else.</para>
    /// </summary>
    public sealed class TerminalActionHost
    {
        /// <summary>Sink for "this branch needs something that is not wired".</summary>
        public Action<string> Report;

        /// <summary>AS3 <c>World.w.gui.infoText(key)</c> — the terminal banner.</summary>
        public Action<string> InfoText;

        /// <summary>AS3 <c>World.w.gui.bulb(X, Y)</c> — the flash at the terminal's own position.</summary>
        public Action<RoomInstance, ObjectInstance> Bulb;

        /// <summary>AS3 <c>World.w.pip.onoff(page, subPage)</c>.</summary>
        public Action<int, int> OpenPipPage;

        /// <summary>AS3 <c>World.w.pip.workTip = allact</c> — which bench the pip-buck should open onto.</summary>
        public Action<string> SetWorkTip;

        /// <summary>AS3 <c>this.loc.prob.showHelp()</c> — the prob room's own help page.</summary>
        public Func<bool> ShowProbHelp;

        /// <summary>AS3 <c>this.loc.electroDam</c> — how much electricity is left in the room.</summary>
        public Func<int> ElectroDamage;

        /// <summary>AS3 <c>this.loc.electroCheck()</c> — recompute the room's electric field.</summary>
        public Action ElectroCheck;

        /// <summary>AS3 <c>World.w.gg.bindChain(X, Y - 20)</c> — the chain a terminal anchors.</summary>
        public Action<float, float> BindChain;

        /// <summary>
        /// AS3 <c>this.owner.loc.allAct(this.owner, verb, allId)</c> — the same broadcast a button uses.
        /// <c>hack_lock</c> needs it with an <b>empty</b> tag, which sweeps every object in the room.
        /// </summary>
        public Action<RoomInstance, ObjectInstance, string, string> Broadcast;

        /// <summary>AS3 <c>for each(unit) unit.hack(World.w.pers.security)</c> — <c>hack_robot</c>'s sweep.</summary>
        public Action<RoomInstance> HackUnits;

        /// <summary>AS3 <c>this.setAct("open", 1)</c> — what <c>robocell</c> and <c>alarm</c> end with.</summary>
        public Action<RoomInstance, ObjectInstance> SetOpen;
    }

    /// <summary>
    /// The port of AS3's <c>Interact.allAct</c> <c>else</c> branch — the tagged broadcast
    /// (<c>serv/Interact.as:1645-1647</c> → <c>loc/Location.as:2835-2860</c>).
    ///
    /// <para><b>It declines the eighteen values the oracle handles explicitly, and that is the whole
    /// reason it is a fallback rather than a registration.</b> Registering the broadcast under every id
    /// would make an unwired <c>comein</c> broadcast <c>comein</c> to the room, when AS3 would have run
    /// <c>gg.outLoc(5, X, Y)</c> and never reached the <c>else</c>. The decline list is the oracle's own
    /// chain, transcribed.</para>
    /// </summary>
    public sealed class BroadcastAction : IObjectActionFallback
    {
        /// <summary>
        /// The values AS3's chain tests before its <c>else</c> — transcribed from
        /// <c>Interact.as:1558-1644</c> in source order.
        ///
        /// <para><b>Not the same list as "the values the port registers".</b> This is the oracle's
        /// vocabulary; which of them the port can actually run is <c>TerminalActions</c>' business, and
        /// the two must not be conflated — a branch the port has no host for still <i>exists</i> in the
        /// oracle and therefore still does not broadcast.</para>
        /// </summary>
        private static readonly HashSet<string> OracleBranches = new HashSet<string>(StringComparer.Ordinal)
        {
            "probreturn",
            "hack_robot",
            "hack_lock",
            "prob_help",
            "electro_check",
            "comein",
            "bind",
            "work",
            "lab",
            "stove",
            "app",
            "map",
            "stand",
            "exit",
            "robocell",
            "alarm",
            "vault",
        };

        private readonly ObjectInteractionBroadcast _broadcast;

        public BroadcastAction(ObjectInteractionBroadcast broadcast)
        {
            _broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
        }

        /// <summary>
        /// The reported id. Not an <c>allact</c> any object carries — the broadcast is what happens
        /// when nothing else matches, so it has no value of its own, and a reader who greps for this
        /// string finds this file rather than a definition row.
        /// </summary>
        public string ActionId => "*";

        /// <summary>
        /// Everything except the oracle's explicit branches. Case-sensitive on purpose: the authored
        /// values are lowercase and AS3 compares with <c>==</c>, so accepting <c>Open</c> would invent a
        /// script.
        /// </summary>
        public bool Handles(string actionId)
        {
            return !string.IsNullOrEmpty(actionId) && !OracleBranches.Contains(actionId.Trim());
        }

        /// <summary>Always true: a broadcast is meaningful with or without any other capability.</summary>
        public bool CanExecute(in ObjectActionContext context)
        {
            return context.Room != null;
        }

        /// <summary>
        /// AS3 <c>this.owner.loc.allAct(this.owner, this.allact, this.allid)</c> — the sender is the
        /// object that carried the <c>allact</c>, and the tag is its own <c>allid</c>.
        ///
        /// <para><b>Returns whether anything accepted the verb, and that answer is honest rather than
        /// always-true.</b> A tag that matches nothing — a button in a room whose platforms were never
        /// imported — produces <c>Refused</c>, so the caller does not play an "it worked" animation. AS3
        /// returns nothing at all here, so this is the port adding an answer to a question the oracle
        /// never asked; it is confined to the outcome enum and changes no gameplay.</para>
        /// </summary>
        public bool Execute(in ObjectActionContext context)
        {
            return _broadcast.Run(context.Room, context.Object, context.ActionId, context.AllId) > 0;
        }
    }

    /// <summary>
    /// The port of the explicit <c>allact</c> branches of AS3's <c>Interact.allAct</c>
    /// (<c>serv/Interact.as:1558-1644</c>) that <c>ObjectActionDispatcher.CreateDefault</c> does not
    /// register.
    ///
    /// <para><b>Thirteen values, and what each one can actually do here.</b> Five of the eighteen
    /// branches are wired by <c>CreateDefault</c> (<c>comein</c>, <c>map</c>, <c>exit</c>,
    /// <c>probreturn</c>, and the <c>prob</c> entry). Of the remaining thirteen, the port has a host for
    /// nine — the pip-buck page opens, the info banner prints, a broadcast can be sent, a door can be
    /// opened. The other four need subsystems the port does not have, and each is registered as a
    /// <b>named gap</b> rather than left to the broadcast: <c>electro_check</c> needs the room's electric
    /// field, <c>bind</c> needs the chain gadget, <c>stand</c> needs a <c>Stand</c> object that does not
    /// exist, and <c>prob_help</c> needs the prob room to own a help page.</para>
    ///
    /// <para><b>Why a named gap beats falling through.</b> If these four were left unregistered they
    /// would be answered by <see cref="BroadcastAction"/>, which would send <c>command("electro_check")</c>
    /// to every object tagged with the terminal's <c>allid</c> — and since no definition carries an
    /// <c>allid</c> (<c>GetAllId</c> documents the empty distribution), that tag is empty and the sweep
    /// hits <b>the whole room</b>. Nothing would break, because no verb matches, but the terminal would
    /// look like it did something. A report naming the missing subsystem is the honest answer.</para>
    /// </summary>
    public static class TerminalActions
    {
        /// <summary>
        /// Register every explicit branch the port can run, plus a named gap for each it cannot.
        ///
        /// <para>Call after <c>CreateDefault</c>. Registering an id twice throws
        /// (<c>ObjectActionDispatcher.Register</c>), so the five <c>CreateDefault</c> owns are not
        /// touched here — if that set ever changes, this method will say so loudly rather than
        /// shadowing.</para>
        /// </summary>
        public static void RegisterAll(ObjectActionDispatcher dispatcher, TerminalActionHost host)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            host ??= new TerminalActionHost();

            // ── branches the port can run ────────────────────────────────────────────────

            dispatcher.Register(new TerminalAction("hack_robot", host, TerminalVerb.HackRobot));
            dispatcher.Register(new TerminalAction("hack_lock", host, TerminalVerb.HackLock));
            dispatcher.Register(new TerminalAction("work", host, TerminalVerb.WorkBench));
            dispatcher.Register(new TerminalAction("lab", host, TerminalVerb.WorkBench));
            dispatcher.Register(new TerminalAction("stove", host, TerminalVerb.WorkBench));
            dispatcher.Register(new TerminalAction("app", host, TerminalVerb.PipPage, page: 8));
            dispatcher.Register(new TerminalAction("vault", host, TerminalVerb.PipPage, page: 9));
            dispatcher.Register(new TerminalAction("robocell", host, TerminalVerb.InfoThenOpen));
            dispatcher.Register(new TerminalAction("alarm", host, TerminalVerb.InfoThenOpen));

            // ── branches with no subsystem to talk to ────────────────────────────────────

            RegisterGap(dispatcher, host, "prob_help",
                "loc.prob.showHelp() — the prob room's own help page. IProbRoomHost has no ShowHelp, so " +
                "the termh terminal reports and stops. (AllData.as:5015 termh allact='prob_help'.)");

            RegisterGap(dispatcher, host, "electro_check",
                "loc.electroCheck() and loc.electroDam — the room's electric field. The port has no " +
                "electric-field model, so the elpanel terminal cannot print electroOn/electroOff.");

            RegisterGap(dispatcher, host, "bind",
                "World.w.gg.bindChain(X, Y - 20) — the chain gadget. The port has no chain, so the dsph " +
                "terminal reports and stops.");

            RegisterGap(dispatcher, host, "stand",
                "World.w.stand.onoff(1) — the trade stand. There is no Stand class in the port, so the " +
                "stand object reports and stops.");
        }

        /// <summary>
        /// Register a branch the port cannot run and say so <b>once, at registration</b> rather than when
        /// somebody presses it.
        ///
        /// <para>Reporting eagerly is the difference between "the log tells you what is missing the first
        /// time the room loads" and "you find out by walking up to a terminal and watching nothing
        /// happen". The report channel is the dispatcher's own, so it lands wherever the rest of the
        /// <c>allact</c> diagnostics land.</para>
        /// </summary>
        private static void RegisterGap(
            ObjectActionDispatcher dispatcher,
            TerminalActionHost host,
            string id,
            string reason)
        {
            dispatcher.Register(new MissingHostAction(id));
            host.Report?.Invoke("[Terminal] allact '" + id + "' is a branch of AS3's Interact.allAct that " +
                                "the port cannot run yet: " + reason);
        }

        /// <summary>The thirteen ids <see cref="RegisterAll"/> owns.</summary>
        public static IReadOnlyList<string> RegisteredIds => new[]
        {
            "hack_robot", "hack_lock", "work", "lab", "stove", "app", "vault",
            "robocell", "alarm", "prob_help", "electro_check", "bind", "stand",
        };

        private enum TerminalVerb
        {
            /// <summary><c>hack_robot</c> — <c>Interact.as:1579-1588</c>.</summary>
            HackRobot,

            /// <summary><c>hack_lock</c> — <c>Interact.as:1589-1598</c>.</summary>
            HackLock,

            /// <summary><c>work</c>/<c>lab</c>/<c>stove</c> — <c>Interact.as:1621-1625</c>.</summary>
            WorkBench,

            /// <summary><c>app</c>/<c>vault</c> — <c>Interact.as:1626-1633</c>.</summary>
            PipPage,

            /// <summary><c>robocell</c>/<c>alarm</c> — <c>Interact.as:1640-1650</c>.</summary>
            InfoThenOpen,
        }

        private sealed class TerminalAction : IObjectAction
        {
            private readonly TerminalActionHost _host;
            private readonly TerminalVerb _verb;
            private readonly int _page;

            public TerminalAction(string id, TerminalActionHost host, TerminalVerb verb, int page = 0)
            {
                ActionId = id;
                _host = host;
                _verb = verb;
                _page = page;
            }

            public string ActionId { get; }

            public bool CanExecute(in ObjectActionContext context)
            {
                return true;
            }

            public bool Execute(in ObjectActionContext context)
            {
                switch (_verb)
                {
                    case TerminalVerb.HackRobot:
                        // Interact.as:1579-1588. The banner, the flash, and then *every* unit in the room
                        // is hacked with the player's security skill. Note there is no range test and no
                        // faction filter: a robot terminal rewrites the whole room's robots.
                        Info("term1Act");
                        _host.Bulb?.Invoke(context.Room, context.Object);
                        Require(_host.HackUnits, "hack_robot", "World.w.pers.security + Unit.hack()");
                        _host.HackUnits?.Invoke(context.Room);
                        return true;

                    case TerminalVerb.HackLock:
                        // Interact.as:1589-1598. The banner, the flash, then `command("hack")` on every
                        // object that has an Interact — with no allid, so the sweep is the whole room.
                        // Only objects whose definition authors `hack` react (Interact.as:1699), which is
                        // why this can be a blanket sweep.
                        Info("term2Act");
                        _host.Bulb?.Invoke(context.Room, context.Object);
                        Require(_host.Broadcast, "hack_lock", "loc.allAct(owner, \"hack\", \"\")");
                        _host.Broadcast?.Invoke(context.Room, context.Object, "hack", string.Empty);
                        return true;

                    case TerminalVerb.WorkBench:
                        // Interact.as:1621-1625. `pip.workTip = this.allact` then `pip.onoff(7)` — page 7
                        // is the workbench page, and workTip says which bench it is.
                        _host.SetWorkTip?.Invoke(ActionId);
                        Require(_host.OpenPipPage, ActionId, "World.w.pip.onoff(7)");
                        _host.OpenPipPage?.Invoke(7, 0);
                        return true;

                    case TerminalVerb.PipPage:
                        // Interact.as:1626-1629 (`app` → 8) and :1646-1649 (`vault` → 9).
                        Require(_host.OpenPipPage, ActionId, "World.w.pip.onoff(" + _page + ")");
                        _host.OpenPipPage?.Invoke(_page, 0);
                        return true;

                    case TerminalVerb.InfoThenOpen:
                        // Interact.as:1640-1650. The banner, then `setAct("open", 1)` — which is also what
                        // clears the object's own `allact` (`Interact.as:869-874`), so a robocell or an
                        // alarm fires exactly once and then behaves as an ordinary opened box.
                        Info(ActionId == "robocell" ? "robocellOff" : "alarmOff");
                        Require(_host.SetOpen, ActionId, "setAct(\"open\", 1)");
                        _host.SetOpen?.Invoke(context.Room, context.Object);
                        return true;

                    default:
                        return false;
                }
            }

            /// <summary>
            /// AS3 <c>World.w.gui.infoText(key)</c>. A null <c>InfoText</c> is not reported: the banner
            /// is a presentation channel and a scene without a HUD is a legitimate state, unlike a
            /// terminal that silently fails to open the pip-buck.
            /// </summary>
            private void Info(string key)
            {
                _host.InfoText?.Invoke(key);
            }

            private void Require(Delegate capability, string id, string as3)
            {
                if (capability == null)
                {
                    _host.Report?.Invoke(
                        "[Terminal] allact '" + id + "' wants " + as3 + ", which is not wired in this " +
                        "scene, so it reported and stopped rather than broadcasting the verb to the room.");
                }
            }
        }

        /// <summary>
        /// A branch of the oracle's chain that the port cannot run because the subsystem behind it does
        /// not exist.
        ///
        /// <para><b>It refuses rather than reporting as unhandled.</b> Unhandled means "no script, use
        /// your own behaviour" and the door presenter would then open the terminal like a box. Refused
        /// means "the script ran and declined", which is what a terminal with a broken subsystem should
        /// look like: nothing happens, and the log says why.</para>
        /// </summary>
        private sealed class MissingHostAction : IObjectAction
        {
            public MissingHostAction(string id)
            {
                ActionId = id;
            }

            public string ActionId { get; }

            public bool CanExecute(in ObjectActionContext context)
            {
                return true;
            }

            /// <summary>
            /// Always false: there is nothing to run. The reason it could not run is reported once when
            /// <see cref="RegisterAll"/> registers it, which is where a reader will look for it.
            /// </summary>
            public bool Execute(in ObjectActionContext context)
            {
                return false;
            }
        }
    }
}
