using System;
using System.Collections.Generic;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// The room-script command vocabulary, as a list — and which of it the port actually executes.
    ///
    /// <para><b>Why this type exists rather than a comment.</b> A command that is silently unhandled is
    /// indistinguishable from a command that ran and did nothing: a trigger fires, the player sees no
    /// change, and there is nothing to grep. This makes the unhandled set a <i>value</i>, so a test can
    /// assert it equals the documented set (<c>EveryOracleCommand_IsHandledOrReported</c>) and a new
    /// command cannot be added to the oracle without the port's omission becoming visible.</para>
    ///
    /// <para><b>The vocabulary is 49 commands, not 18.</b> <c>docs/Campaign/03_QUESTS_SCRIPTS_AND_EVENT_ENGINE.md</c>
    /// listed 18 and mixed in names from <c>Interact.as</c>'s <b>different</b> <c>allact</c> vocabulary.
    /// The authoritative list is <c>Script.com</c>'s flat if-chain (<c>Script.as:199-562</c>). The two
    /// vocabularies share no mechanism: an <c>allact</c> runs when the player presses the action key on an
    /// object, a script command runs from a room's <c>&lt;s&gt;</c> list. They must never share a
    /// switch.</para>
    /// </summary>
    public static class ScriptVocabulary
    {
        /// <summary>
        /// Every <c>@act</c> value <c>Script.com</c> branches on, in the oracle's own order
        /// (<c>Script.as:239-560</c>). The <c>@targ</c> path (<c>:218-236</c>) is not a command and is not
        /// in this list — it forwards to the target object's own <c>command()</c>.
        /// </summary>
        public static readonly string[] OracleCommands =
        {
            "control off", "control on",
            "mess", "dial", "dialog", "inform", "landlevel",
            "allact",
            "take", "armor", "xp", "perk", "eff", "remeff",
            "music", "music_rep",
            "anim", "turn",
            "black", "dblack",
            "gui off", "gui on",
            "refill", "upland",
            "locon", "locoff",
            "quest", "showstage", "show", "stage",
            "trigger", "goto", "gotoland", "openland", "passed",
            "actprob", "alarm", "trus", "checkall", "robots",
            "weapch", "alicorn", "wave", "pip", "speceffect", "scene",
            "endgame", "gameover",
            "wait",
        };

        /// <summary>
        /// The commands <see cref="ScriptRunner"/> executes. Everything the <c>begin</c> land's opening
        /// needs is here, plus the cheap campaign and object commands.
        /// </summary>
        public static readonly string[] HandledCommands =
        {
            // The cutscene staging the opening scripts are made of (beginBeg/beginMtn/beginUp).
            "control off", "control on",
            "mess", "dial", "dialog", "inform", "landlevel",
            "black", "dblack", "anim", "turn", "show",
            "gui off", "gui on",
            "pip", "music", "music_rep",
            "wait",

            // Progression — the reason a cutscene exists at all.
            "quest", "showstage", "stage", "trigger",
            "upland", "openland", "refill", "passed",
            "gotoland", "goto",

            // Objects and the player.
            "allact", "take", "armor", "xp", "perk", "eff", "remeff", "weapch",

            // Security. `alarm` is `World.w.loc.signal()` (Script.as:480-483) — the tripwire areas in
            // RoomsProb/RoomsSerial and a raider's own alarm script both land here.
            "alarm",
        };

        /// <summary>
        /// The commands that are <b>reported by name and not executed</b> — each needs a subsystem the
        /// port does not have. Reported rather than dropped, because "the door did not open" and "the
        /// door opened" must not look the same in a log.
        /// </summary>
        public static readonly string[] ReportedCommands =
        {
            // `locon` / `locoff` toggle the land's location objects (Location.allon/alloff). The port
            // has no land-wide object toggle — the objects exist, the aggregate switch does not.
            "locon", "locoff",

            // Prob-room machinery: entering a prob room and its wave trigger. The prob host exists
            // (IProbRoomHost) but is not reachable from a room script yet.
            // `alarm` is NOT here any more — it raises the room alarm through SecurityRuntime
            // (Script.as:480-483), which is what the tripwire areas in RoomsProb/RoomsSerial need.
            "actprob", "wave",

            // `trus` writes a trust value on the location; nothing in the port reads one.
            "trus",

            // `checkall` re-runs every unit's `check`; the port's units have no `check` command.
            "checkall",

            // `robots` activates the location's robocell.
            "robots",

            // `alicorn` toggles the Alicorn ability, which is a port-side RPG feature with no script hook.
            "alicorn",

            // Presentation with no ported consumer: `speceffect` (screen shader), `scene` (the cutscene
            // overlay), `endgame`/`gameover` (the ending sequence).
            "speceffect", "scene", "endgame", "gameover",
        };

        /// <summary>
        /// The object-command vocabulary reachable through <c>@targ</c> — AS3
        /// <c>Script.com</c>'s <c>_loc2_.command(param1.act, param1.val)</c> (<c>Script.as:234</c>),
        /// dispatched to <c>Obj.command</c> / <c>Box.command</c>.
        ///
        /// <para><b>Separate from the two lists above because it is a different dispatch.</b> A command
        /// with a <c>@targ</c> never reaches the campaign chain — it goes to one object. <c>unlock</c> is
        /// the one the <c>begin</c> land needs (<c>beginMtn</c> unlocks <c>begDoor</c>).</para>
        /// </summary>
        public static readonly string[] ObjectCommands =
        {
            "sign", "off", "on", "onoff", "open", "close", "unlock", "lock",
        };

        /// <summary>Whether <see cref="ScriptRunner"/> executes <paramref name="command"/>.</summary>
        public static bool IsHandled(string command)
        {
            if (string.IsNullOrEmpty(command)) return false;
            for (int i = 0; i < HandledCommands.Length; i++)
            {
                if (string.Equals(HandledCommands[i], command, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>Whether <paramref name="command"/> is a known oracle command that the port reports
        /// rather than runs.</summary>
        public static bool IsReported(string command)
        {
            if (string.IsNullOrEmpty(command)) return false;
            for (int i = 0; i < ReportedCommands.Length; i++)
            {
                if (string.Equals(ReportedCommands[i], command, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>Whether the oracle has this command at all. A <c>false</c> here means the command is
        /// an invention — which is how the port acquired a phantom <c>exit</c> branch that could never
        /// fire.</summary>
        public static bool IsOracleCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return false;
            for (int i = 0; i < OracleCommands.Length; i++)
            {
                if (string.Equals(OracleCommands[i], command, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// Commands that make a script run <b>stepwise</b> rather than all at once — AS3
        /// <c>Script.analiz</c> (<c>Script.as:73-96</c>) sets <c>onTimer</c> for these four, and for any
        /// action carrying a positive <c>@t</c>.
        ///
        /// <para><b>Why the four.</b> Each of them blocks on the player: <c>dial</c> and <c>dialog</c> set
        /// <c>wait</c> when their <c>t</c> is not positive, and <c>inform</c>/<c>landlevel</c> do the same
        /// unconditionally (<c>Script.as:273-295</c>). Running them in a synchronous loop would show the
        /// last one and skip the rest.</para>
        /// </summary>
        public static bool ForcesTimer(string command)
        {
            if (string.IsNullOrEmpty(command)) return false;
            return string.Equals(command, "dial", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "dialog", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "inform", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "landlevel", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// AS3 <c>World.fps</c> — the frame rate <c>@t</c> is expressed in (<c>Script.as:91</c>:
        /// <c>Math.round(t * World.fps)</c>). The sim runs at the same rate, so one frame is one tick.
        /// </summary>
        public const int FramesPerSecond = 30;
    }
}
