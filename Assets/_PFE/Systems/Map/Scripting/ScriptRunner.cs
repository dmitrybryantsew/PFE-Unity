using System;
using System.Collections.Generic;
using System.Globalization;
using PFE.Core;
using PFE.Systems.Campaign;
using UnityEngine;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// The stepwise script interpreter — a port of <c>fe.serv.Script</c> (<c>Script.as</c>).
    ///
    /// <para><b>What was wrong before this existed.</b> The port ran a script's actions in a synchronous
    /// <c>for</c> loop, so <b>every authored <c>@t</c> delay and every <c>wait</c> was silently ignored</b>
    /// (<c>03_GAP_LEDGER.md</c> §2.1). A cutscene that should take twenty seconds and pause for a keypress
    /// ran instantly, and nothing went red — the commands all executed, just all at once. The
    /// <c>begin</c> land's opening (<c>beginBeg</c>) is thirteen actions with delays of 2/4/2/2/1 seconds;
    /// run synchronously it shows the last message and skips the dialogue entirely.</para>
    ///
    /// <para><b>The timing model, stated exactly.</b> AS3 <c>start()</c> fires <b>action 0
    /// immediately</b> and then sets <c>tcom = acts[0].t</c>; <c>step()</c> decrements <c>tcom</c> and,
    /// when it reaches zero, fires the <b>next</b> action and loads its <c>t</c>
    /// (<c>Script.as:123-197</c>). So an action's <c>@t</c> is the pause <b>after</b> it, not before it.
    /// The port's own field comment claims the opposite ("delay … before this action executes"), which is
    /// one of the reasons nothing consumed it.</para>
    ///
    /// <para><b>Countdown semantics.</b> <c>tcom</c> is loaded with the frame count and decremented on
    /// each subsequent step, so a delay of <c>n</c> frames elapses over <c>n + 1</c> ticks — the tick that
    /// loads it plus <c>n</c> decrements. That is the oracle's behaviour, and it is why <c>@t="10"</c> is
    /// eleven ticks rather than ten. Do not "fix" it.</para>
    ///
    /// <para><b>It names what it does not run.</b> <see cref="ScriptVocabulary.ReportedCommands"/> is the
    /// set of oracle commands with no ported consumer; each is reported <b>once per command name</b>, so a
    /// missing branch is distinguishable from a command that ran and changed nothing. A command that is
    /// neither handled nor an oracle command at all is reported too — that is how the port's invented
    /// <c>exit</c> branch stayed invisible for so long.</para>
    /// </summary>
    public sealed class ScriptRunner
    {
        /// <summary>
        /// The campaign-level host, resolved <b>per call</b> rather than held.
        ///
        /// <para><b>Why a factory and not a reference.</b> A room's trigger system is built while the
        /// world is being assembled, and that can happen before <c>CampaignManager</c> exists — the same
        /// ordering that made <c>AreaTriggerSystem.LandHost</c> a per-call read instead of a constructor
        /// argument. Capturing a null reference here would make <c>upland</c>, <c>trigger</c> and
        /// <c>gotoland</c> no-ops for the whole session while every other command worked, which is the
        /// hardest possible version of this bug to notice.</para>
        /// </summary>
        private readonly Func<ILandScriptHost> _campaign;

        private readonly Func<IScriptQuestHost> _quests;
        private readonly IScriptPresentationHost _presentation;
        private readonly IScriptObjectHost _objects;
        private readonly IScriptPlayerHost _player;
        private readonly IScriptInputProbe _input;
        private readonly IScriptCatalogHost _catalog;
        private readonly Action<string> _report;

        /// <summary>Scripts mid-flight, in the order they started. AS3 keeps the same list on the land
        /// (<c>Script.as:58-61</c>) and steps every entry each frame.</summary>
        private readonly List<RunningScript> _running = new List<RunningScript>();

        /// <summary>Command names already reported as unhandled, so the notice is once per name rather
        /// than once per execution.</summary>
        private readonly HashSet<string> _reportedUnhandled = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>How many scripts are still running. Exposed so a test can watch a script finish.</summary>
        public int RunningCount => _running.Count;

        /// <summary>The command names reported as unhandled so far — the same set the log carries, so a
        /// fixture can assert it rather than scrape output.</summary>
        public IEnumerable<string> ReportedUnhandled => _reportedUnhandled;

        public ScriptRunner(
            Func<ILandScriptHost> campaign = null,
            Func<IScriptQuestHost> quests = null,
            IScriptPresentationHost presentation = null,
            IScriptObjectHost objects = null,
            IScriptPlayerHost player = null,
            IScriptInputProbe input = null,
            IScriptCatalogHost catalog = null,
            Action<string> report = null)
        {
            _campaign = campaign;
            _quests = quests;
            _presentation = presentation;
            _objects = objects;
            _player = player;
            _input = input;
            _catalog = catalog;
            _report = report ?? (message => Debug.LogWarning(message));
        }

        /// <summary>One script in flight.</summary>
        private sealed class RunningScript
        {
            public RoomInstance Room;
            public ObjectInstance Owner;
            public List<MapObjectScriptActionData> Actions;

            /// <summary>Index of the action last fired — AS3 <c>ncom</c>.</summary>
            public int Ncom;

            /// <summary>Frames remaining before the next action — AS3 <c>tcom</c>.</summary>
            public int Tcom;

            /// <summary>Whether the script is blocked on the player — AS3 <c>wait</c>.</summary>
            public bool Wait;

            /// <summary>
            /// AS3 <c>dial_n</c> — the dialogue line cursor while waiting.
            ///
            /// <para><b>An <c>int</c>, and <c>-1</c> is meaningful.</b> <c>-1</c> means "the command
            /// being waited on has no line walk" — a <c>dial</c> whose <c>@t</c> is not positive — so
            /// the first keypress just closes the box. <c>0</c> and up means "walking a dialogue from
            /// this row", which is what <c>dialog</c>, <c>inform</c>, <c>landlevel</c> and <c>wait</c>
            /// set (<c>Script.as:260-295</c>, <c>:555-560</c>). Storing it as a string and comparing to
            /// <c>"-1"</c> would work for the first press and then silently never match again.</para>
            /// </summary>
            public int DialN = -1;

            /// <summary>The action being waited on — AS3 <c>actObj</c>.</summary>
            public MapObjectScriptActionData ActObj;
        }

        /// <summary>
        /// AS3 <c>Script.start()</c> (<c>Script.as:123-144</c>).
        ///
        /// <para><b>The synchronous arm is not a shortcut, it is the oracle.</b> A script whose actions
        /// carry no delay and none of the four blocking commands runs entirely inside <c>start()</c> —
        /// <c>if(!this.onTimer) for each(…) this.com(_loc1_)</c>. Making every script stepwise would
        /// change when a room's doors open by a frame, for no gain.</para>
        /// </summary>
        /// <param name="room">The room the script belongs to — AS3 <c>Script.land</c>.</param>
        /// <param name="script">The authored action list.</param>
        /// <param name="owner">The object the script hangs off, for <c>@targ="this"</c>.</param>
        public void Run(RoomInstance room, MapObjectScriptData script, ObjectInstance owner = null)
        {
            if (room == null || script?.actions == null || script.actions.Count == 0) return;

            bool onTimer = RequiresTimer(script.actions);

            if (!onTimer)
            {
                for (int i = 0; i < script.actions.Count; i++)
                {
                    Execute(room, owner, script.actions[i], null);
                }

                return;
            }

            var running = new RunningScript
            {
                Room = room,
                Owner = owner,
                Actions = script.actions,
                Ncom = 0,
            };

            Execute(room, owner, script.actions[0], running);
            running.Tcom = FramesOf(script.actions[0]);
            _running.Add(running);
        }

        /// <summary>
        /// Starts an <b>authored</b> script by id — AS3 <c>Area.scrOver.start()</c> where
        /// <c>scrOver</c> came from <c>Game.getScript(@scr, area)</c> (<c>Area.as:151-158</c>,
        /// <c>:354-368</c>).
        ///
        /// <para><b>Why this is not just <c>Run</c> with a lookup bolted on.</b> A room's area carries
        /// <c>scr="beginBeg"</c> — a <i>reference</i> — while a room's object carries inline
        /// <c>&lt;scr&gt;&lt;s act="…"/&gt;&lt;/scr&gt;</c> blocks. Both reach <c>Script.start()</c> in the
        /// oracle and both must reach one interpreter here; the difference is only where the action list
        /// is read from. Folding the lookup into <c>Run</c> would make the inline path pay for a
        /// catalogue it never uses, and would hide the fact that an unresolvable id is a
        /// <i>data</i> failure rather than a script failure.</para>
        /// </summary>
        /// <returns><c>false</c> when the id could not be resolved, so a caller can distinguish "the
        /// area fired and its script was missing" from "the area never fired".</returns>
        public bool RunAuthored(RoomInstance room, string scriptId, ObjectInstance owner = null)
        {
            if (string.IsNullOrEmpty(scriptId)) return false;

            if (_catalog == null)
            {
                ReportOnce("(catalogue)", $"[ScriptRunner] the area references the authored script " +
                                          $"'{scriptId}', but no script catalogue is wired, so nothing " +
                                          "started.");
                return false;
            }

            if (!_catalog.TryGetScript(scriptId, out MapObjectScriptData script))
            {
                // The catalogue host reports the miss by id; nothing further to add here.
                return false;
            }

            Run(room, script, owner);
            return true;
        }

        /// <summary>
        /// AS3 <c>Script.analiz</c>'s <c>onTimer</c> decision (<c>Script.as:73-96</c>): a positive
        /// <c>@t</c> anywhere, or one of the four blocking commands.
        /// </summary>
        public static bool RequiresTimer(List<MapObjectScriptActionData> actions)
        {
            if (actions == null) return false;

            for (int i = 0; i < actions.Count; i++)
            {
                MapObjectScriptActionData action = actions[i];
                if (action == null) continue;

                if (FramesOf(action) > 0) return true;
                if (ScriptVocabulary.ForcesTimer(action.act)) return true;
            }

            return false;
        }

        /// <summary>
        /// AS3 <c>Math.round(param1.@t * World.fps)</c> (<c>Script.as:91</c>) — converted <b>once</b>, at
        /// read time, exactly as the oracle converts it once in <c>analiz</c> rather than per step.
        /// </summary>
        public static int FramesOf(MapObjectScriptActionData action)
        {
            if (action == null || string.IsNullOrEmpty(action.t)) return 0;

            if (!float.TryParse(action.t, NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds))
            {
                return 0;
            }

            int frames = (int)Math.Round(seconds * ScriptVocabulary.FramesPerSecond, MidpointRounding.AwayFromZero);
            return frames > 0 ? frames : 0;
        }

        /// <summary>
        /// AS3 <c>Script.step()</c> (<c>Script.as:146-197</c>). Called once per sim tick from
        /// <c>SimTickOrder.Scripts</c>, which sits beside <c>Triggers</c> because a script is what a
        /// trigger produces.
        /// </summary>
        public void SimTick()
        {
            // Iterated by index and backwards, because a step can start another script (a `gotoland`
            // that arrives somewhere with an entry script) and because a step can finish this one.
            for (int i = _running.Count - 1; i >= 0; i--)
            {
                if (i >= _running.Count) continue;
                Step(_running[i], i);
            }
        }

        private void Step(RunningScript running, int index)
        {
            if (running.Tcom > 0)
            {
                running.Tcom--;
            }

            if (running.Tcom > 0) return;

            if (running.Wait)
            {
                // AS3 Script.as:154-183, reproduced line for line.
                //
                //   if(keyPressed2)      dial_n = 10000;      // right-click: skip to the end
                //   else if(!keyPressed) return;              // nothing pressed: stay blocked
                //   if(dial_n < 0)       { dialText(); wait = false; }
                //   else { ++dial_n; if(dialText(val, dial_n, opt1>0, true)) { ...blocked...; return; }
                //          dialText(); controlOn(); wait = false; }
                //   keyPressed = keyPressed2 = false;
                //
                // The `dialText` return value is the whole mechanism: it is false only when row
                // `dial_n` does not exist, so the loop walks a monologue one press at a time and only
                // releases when it runs off the end. A port that released after a single press would
                // show `begDial`'s first Littlepip line and drop the other two.
                if (_input == null)
                {
                    // No probe: the script must NOT block for ever. Release the block and let the
                    // missing seam be reported by whoever wired a runner without one.
                    running.Wait = false;
                }
                else
                {
                    if (_input.AdvancePressed)
                    {
                        running.DialN = 10000;
                    }
                    else if (!_input.ActionKeyHeld)
                    {
                        return;
                    }

                    if (running.DialN < 0)
                    {
                        // A `dial` whose @t is not positive: the first press simply closes the box.
                        _presentation?.HideDialogue();
                        running.Wait = false;
                    }
                    else
                    {
                        running.DialN++;
                        bool stillHasLines = _presentation != null && _presentation.ShowDialogue(
                            running.ActObj?.val,
                            running.DialN,
                            ParseInt(running.ActObj?.opt1) > 0,
                            true);

                        if (stillHasLines)
                        {
                            // AS3 `World.w.ctr.active = false` + `return` — the controller stays locked
                            // and the script stays blocked on the next press.
                            _presentation.SetControlEnabled(false);
                            _input.ClearPresses();
                            return;
                        }

                        _presentation?.HideDialogue();
                        _presentation?.SetControlEnabled(true);
                        running.Wait = false;
                    }

                    _input.ClearPresses();
                }
            }

            running.Ncom++;
            if (running.Ncom >= running.Actions.Count)
            {
                // AS3 `this.running = false; World.w.gui.dialText();` (Script.as:187-191). The hide is
                // load-bearing and easy to drop: a `dial` with a positive `@t` does NOT block
                // (`Script.as:251-255` only sets `wait` when `t <= 0`), so `beginBeg`'s three `begDial`
                // lines are shown on a timer by three separate actions and nothing ever walks them off
                // screen. Without this the third line — "I'd better get going" — stays up for the rest
                // of the session, over the world, with the player already in control.
                _presentation?.HideDialogue();
                _running.RemoveAt(index);
                return;
            }

            MapObjectScriptActionData next = running.Actions[running.Ncom];
            Execute(running.Room, running.Owner, next, running);
            running.Tcom = FramesOf(next);
        }

        /// <summary>
        /// AS3 <c>Script.com(action)</c> (<c>Script.as:199-562</c>).
        ///
        /// <para><b>The <c>@targ</c> test comes first and it is not a branch of the command chain.</b>
        /// With a <c>@targ</c> the oracle forwards <c>act</c>+<c>val</c> to that object's own
        /// <c>command()</c> and <b>none</b> of the campaign <c>if</c>-chain runs (<c>:218-236</c>). The
        /// two vocabularies overlap in spelling — <c>off</c> and <c>open</c> are object commands while
        /// <c>openland</c> is a campaign one — so dispatching by name alone would send an object command
        /// into the campaign chain.</para>
        /// </summary>
        private void Execute(RoomInstance room, ObjectInstance owner, MapObjectScriptActionData action,
                             RunningScript running)
        {
            if (action == null) return;

            string command = Normalise(action.act);
            if (command.Length == 0) return;

            // Resolved once per action, not once per script: a `gotoland` action can rebuild the world
            // (and with it the campaign's current land) part-way through a script.
            ILandScriptHost campaign = _campaign?.Invoke();
            IScriptQuestHost quests = _quests?.Invoke();

            if (running != null)
            {
                running.ActObj = action;
                running.Wait = false;
                running.DialN = -1;
            }

            // AS3 clears both key latches at the TOP of every command (`Script.as:215`). It matters:
            // without it, one press satisfies the dialogue walk and the blocking command that follows.
            _input?.ClearPresses();

            if (!string.IsNullOrEmpty(action.targ))
            {
                string target = string.Equals(action.targ, "this", StringComparison.OrdinalIgnoreCase)
                    ? owner?.uid ?? owner?.code
                    : action.targ;

                if (_objects == null)
                {
                    ReportOnce("(object host)", $"a `@targ` command ('{command}' on '{target}') has no " +
                                                "object host wired, so the object was not touched.");
                }
                else if (!_objects.ObjectCommand(room, target, command, action.val))
                {
                    Debug.LogWarning($"[ScriptRunner] `{command}` names target '{target}', but no object " +
                                     "in the room matches that uid or code.");
                }

                return;
            }

            switch (command)
            {
                // ── Staging ────────────────────────────────────────────────────────────────────────
                case "control off": _presentation?.SetControlEnabled(false); break;
                case "control on": _presentation?.SetControlEnabled(true); break;
                case "gui off": _presentation?.SetGuiEnabled(false); break;
                case "gui on": _presentation?.SetGuiEnabled(true); break;

                case "mess":
                    if (_presentation == null) ReportMissing("mess");
                    else _presentation.ShowMessage(action.val, ParseInt(action.opt1) > 0,
                                                   ParseInt(action.opt2) > 0);
                    break;

                case "dial":
                    // Script.as:251-259 — `if(param1.t <= 0) this.wait = true;` then
                    // `dialText(val, n, opt1>0, wait)`. A `dial` with a positive @t is a timed caption
                    // that does not block; one without blocks on a keypress. Note the line index is the
                    // authored `@n`, not a cursor — the walk only starts if the script is blocked.
                    if (running != null && FramesOf(action) <= 0) running.Wait = true;
                    if (_presentation == null) ReportMissing("dial");
                    else
                    {
                        _presentation.ShowDialogue(action.val, ParseInt(action.n),
                                                   ParseInt(action.opt1) > 0,
                                                   running?.Wait ?? false);
                    }

                    break;

                case "dialog":
                    // Script.as:260-272 — a whole dialogue from row 0, always blocking, and only when
                    // `World.w.dialOn` (the player's "show dialogue" setting) is on.
                    if (_presentation == null) ReportMissing("dialog");
                    else if (_presentation.ShowDialogue(action.val, 0, ParseInt(action.opt1) > 0, true))
                    {
                        if (running != null)
                        {
                            running.Wait = true;
                            running.DialN = 0;
                        }

                        _presentation.SetControlEnabled(false);
                    }

                    break;

                case "inform":
                    // Script.as:273-280 — `dialText(<r mod={opt2} push={opt1>0}>{val}</r>, 0, false, true)`.
                    // The text is shown verbatim: this is the one command that does NOT go through the
                    // `txt` table, which is why the presentation seam has a raw form as well as a
                    // lookup form.
                    if (_presentation == null) ReportMissing("inform");
                    else
                    {
                        _presentation.SetControlEnabled(false);
                        _presentation.ShowRawDialogue(action.val, ParseInt(action.opt2),
                                                      ParseInt(action.opt1) > 0, true);
                        if (running != null)
                        {
                            running.Wait = true;
                            running.DialN = 0;
                        }
                    }

                    break;

                case "landlevel":
                    // Script.as:281-295 — only when `dialOn`, and only for a land the game knows.
                    if (_presentation == null) ReportMissing("landlevel");
                    else
                    {
                        _presentation.SetControlEnabled(false);
                        _presentation.ShowLandLevel(action.val);
                        if (running != null)
                        {
                            running.Wait = true;
                            running.DialN = 0;
                        }
                    }

                    break;

                case "black": _presentation?.SetBlack(ParseFloat(action.val)); break;
                case "dblack": _presentation?.SetBlackFade(ParseFloat(action.val)); break;
                case "anim": _presentation?.PlayPlayerAnimation(action.val, ParseInt(action.opt1) > 0); break;
                case "turn": _presentation?.TurnPlayer(ParseFloat(action.val) > 0f); break;
                case "music": _presentation?.PlayMusic(action.val, ParseInt(action.n)); break;

                case "music_rep":
                    // `if(pers.rep >= pers.repGood)` (Script.as:350-353). The port has no reputation
                    // reading here, so the track plays — the alternative is a silent no-op that looks
                    // like a missing branch.
                    _presentation?.PlayMusic(action.val, ParseInt(action.n));
                    break;

                case "show": _presentation?.ClearCameraIntro(); break;
                case "pip": _presentation?.OpenPipPage(action.val, action.n); break;

                // ── Progression ────────────────────────────────────────────────────────────────────
                case "upland":
                    if (campaign == null) ReportMissing("upland");
                    else campaign.UpLandLevel();
                    break;

                case "openland":
                    if (campaign == null) ReportMissing("openland");
                    else campaign.OpenLand(action.val);
                    break;

                case "refill":
                    if (campaign == null) ReportMissing("refill");
                    else campaign.RefillVendors();
                    break;

                case "passed":
                    if (campaign == null) ReportMissing("passed");
                    else campaign.MarkPassed();
                    break;

                case "trigger":
                    if (campaign == null) ReportMissing("trigger");
                    else campaign.SetTrigger(action.val, TriggerValue.FromScriptCommandN(action.n));
                    break;

                case "gotoland":
                    if (campaign == null) ReportMissing("gotoland");
                    else campaign.GotoLand(action.val, NValue(action), GotolandCoordinates(action));
                    break;

                case "goto":
                    if (_objects == null) ReportMissing("goto");
                    else GotoXY(action.val);
                    break;

                case "quest":
                    if (quests == null) ReportMissing("quest");
                    else quests.AddQuest(action.val);
                    break;

                case "showstage":
                    if (quests == null) ReportMissing("showstage");
                    else quests.ShowQuest(action.val, action.n);
                    break;

                case "stage":
                    if (quests == null) ReportMissing("stage");
                    else quests.CloseQuest(action.val, action.n);
                    break;

                case "allact":
                    if (_objects == null) ReportMissing("allact");
                    else _objects.RunAllAct(room, action.val, action.n);
                    break;

                // ── Player ─────────────────────────────────────────────────────────────────────────
                case "take":
                    if (_player == null) ReportMissing("take");
                    else _player.TakeItem(action.val, NValue(action));
                    break;

                case "armor":
                    if (_player == null) ReportMissing("armor");
                    else _player.ChangeArmor(action.val);
                    break;

                case "xp":
                    if (_player == null) ReportMissing("xp");
                    else _player.GrantXp((int)ParseFloat(action.val));
                    break;

                case "perk":
                    if (_player == null) ReportMissing("perk");
                    else _player.AddPerk(action.val);
                    break;

                case "eff":
                    if (_player == null) ReportMissing("eff");
                    else _player.AddEffect(action.val, ParseInt(action.opt1), ParseInt(action.opt2));
                    break;

                case "remeff":
                    if (_player == null) ReportMissing("remeff");
                    else _player.RemoveEffect(action.val);
                    break;

                case "weapch":
                    if (_player == null) ReportMissing("weapch");
                    else _player.ChangeWeapon(action.val);
                    break;

                // ── Blocking ───────────────────────────────────────────────────────────────────────
                case "wait":
                    // `this.wait = true; this.dial_n = 0;` (Script.as:555-560) — block until the action key.
                    if (running != null)
                    {
                        running.Wait = true;
                        running.DialN = 0;
                    }

                    break;

                default:
                    ReportCommand(command);
                    break;
            }
        }

        /// <summary>
        /// AS3 <c>goto val</c> (<c>Script.as:435-442</c>): <c>val</c> is a <c>"x y"</c> pair and the
        /// command is a no-op unless it splits into exactly two parts — the oracle's
        /// <c>if(_loc5_.length == 2)</c>.
        /// </summary>
        private void GotoXY(string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            string[] parts = value.Split(' ');
            if (parts.Length != 2)
            {
                Debug.LogWarning($"[ScriptRunner] `goto '{value}'` is not an 'x y' pair, so nothing moved " +
                                 "(AS3's split requires exactly two parts).");
                return;
            }

            _objects.GotoXY(parts[0], parts[1]);
        }

        /// <summary>AS3's <c>gotoland</c> branches on <c>@n</c> (<c>Script.as:443-456</c>).</summary>
        private static int NValue(MapObjectScriptActionData action) => ParseInt(action?.n);

        private static string GotolandCoordinates(MapObjectScriptActionData action)
        {
            if (NValue(action) != 1) return null;
            return $"{action.opt1 ?? string.Empty}:{action.opt2 ?? string.Empty}";
        }

        private static string Normalise(string command) =>
            string.IsNullOrEmpty(command) ? string.Empty : command.Trim().ToLowerInvariant();

        private static int ParseInt(string raw) => QuestRules.ParseIntOrDefault(raw, 0);

        private static float ParseFloat(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0f;
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value
                : 0f;
        }

        /// <summary>
        /// Report a command the oracle has but the port does not run — once per name.
        ///
        /// <para>The wording differs by whether the command exists in the oracle at all, because those are
        /// different findings: "not ported yet" versus "this command is an invention".</para>
        /// </summary>
        private void ReportCommand(string command)
        {
            if (ScriptVocabulary.IsReported(command))
            {
                ReportOnce(command,
                    $"[ScriptRunner] `{command}` is an oracle command (Script.as:199-562) with no ported " +
                    "consumer, so it was not executed. See ScriptVocabulary.ReportedCommands.");
                return;
            }

            ReportOnce(command,
                $"[ScriptRunner] `{command}` is not an oracle command at all — Script.com " +
                "(Script.as:199-562) has no such branch. Either the authored data carries a typo, or the " +
                "port invented it (as it did with `exit`).");
        }

        private void ReportMissing(string command)
        {
            ReportOnce("(host for " + command + ")",
                $"[ScriptRunner] `{command}` needs a host that is not wired, so it did nothing.");
        }

        private void ReportOnce(string key, string message)
        {
            if (_reportedUnhandled.Add(key)) _report(message);
        }
    }
}
