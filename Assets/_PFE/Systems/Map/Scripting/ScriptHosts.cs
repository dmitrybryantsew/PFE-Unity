namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// The authored-script lookup — AS3 <c>World.w.game.getScript(id, area)</c>
    /// (<c>Game.as:722-750</c>), which resolves the <c>&lt;scr id="…"&gt;</c> collection of
    /// <c>GameData.d</c>.
    ///
    /// <para><b>This is the seam the <c>begin</c> land was missing.</b> A room's area object carries
    /// <c>scr="beginBeg"</c> and <c>scrout="…"</c> (<c>Area.as:151-158</c>), and those are
    /// <i>references</i> to an authored script, not inline action lists. The port imported the
    /// attribute faithfully — <c>rooms_begin/room_0_0.asset</c> carries
    /// <c>attributes: - key: scr / value: beginBeg</c> — and then never read it, so the opening
    /// cutscene of the entire campaign could not start. Nothing was red: the data was present, the
    /// actions were present, and no code joined them.</para>
    /// </summary>
    public interface IScriptCatalogHost
    {
        /// <summary>
        /// Resolves an authored script by id.
        ///
        /// <para>The result is a <see cref="MapObjectScriptData"/> rather than a
        /// <c>CampaignScriptDefinition</c> because the runner must treat an authored script and an
        /// inline <c>&lt;scr&gt;</c> block identically — they differ only in where the action list comes
        /// from, and giving the interpreter two action shapes would be two things to keep in step.</para>
        /// </summary>
        /// <returns><c>false</c> when no script carries that id, so the caller can report the id rather
        /// than let it read as "the script ran and did nothing".</returns>
        bool TryGetScript(string scriptId, out MapObjectScriptData script);
    }

    /// <summary>
    /// The quest half of the script vocabulary — AS3 <c>World.w.game.addQuest</c> /
    /// <c>showQuest</c> / <c>closeQuest</c> (<c>Game.as:527-632</c>), reached from the
    /// <c>quest</c>, <c>showstage</c> and <c>stage</c> commands (<c>Script.as:408-423</c>).
    ///
    /// <para>Narrow on purpose: the runner needs three operations, and handing it the whole
    /// <c>ICampaignManager</c> would make the runner's dependency read as "the script can do anything the
    /// campaign can". Implemented by <c>CampaignManager</c>.</para>
    /// </summary>
    public interface IScriptQuestHost
    {
        /// <summary>AS3 <c>addQuest(val)</c> — the <c>quest</c> command.</summary>
        void AddQuest(string questId);

        /// <summary>AS3 <c>showQuest(val, n)</c> — the <c>showstage</c> command. Un-hides sub <c>n</c>.</summary>
        void ShowQuest(string questId, string n);

        /// <summary>
        /// AS3 <c>closeQuest(val, n)</c> — the <c>stage</c> command. <paramref name="n"/> absent, empty or
        /// negative completes the <b>whole</b> quest.
        /// </summary>
        void CloseQuest(string questId, string n);
    }

    /// <summary>
    /// The staging half of the vocabulary — everything a cutscene needs to take over the screen.
    ///
    /// <para><b>All of it is optional at the call site.</b> Each method is invoked only when the runner
    /// has a host; with none, the command is reported once by name rather than silently skipped, because
    /// "the fade never happened" and "the fade happened" must not look alike in a log.</para>
    ///
    /// <para><b>The dialogue surface is deliberately the oracle's one function, not four.</b> AS3 routes
    /// <c>dial</c>, <c>dialog</c>, <c>inform</c> and <c>landlevel</c> through the single
    /// <c>GUI.dialText(id, n, down, wait)</c> (<c>GUI.as:1741-1905</c>), which returns <c>true</c> when it
    /// rendered a line and <c>false</c> when the id or the line index does not exist. That return value
    /// is <b>load-bearing</b>: <c>Script.step</c> uses it to decide whether the script is still blocked
    /// (<c>Script.as:170-183</c>), which is what walks a multi-line Littlepip monologue one press at a
    /// time. A port with a <c>void</c> show-dialogue would have to guess, and the guess that looks
    /// reasonable — "one press, one line, then move on" — skips the rest of every multi-line
    /// dialogue.</para>
    /// </summary>
    public interface IScriptPresentationHost
    {
        /// <summary>
        /// AS3 <c>mess val</c> (<c>Script.as:247-250</c>) → <c>gui.messText(val, "", opt1&gt;0,
        /// opt2&gt;0)</c> — the Littlepip message banner. <paramref name="down"/> is <c>@opt1 &gt; 0</c>
        /// (draw below the player), <paramref name="push"/> is <c>@opt2 &gt; 0</c> (append rather than
        /// replace).
        /// </summary>
        void ShowMessage(string messKey, bool down, bool push);

        /// <summary>
        /// AS3 <c>gui.dialText(id, n, down, wait)</c> for a <b>table</b> id: looks up
        /// <c>&lt;txt id="id"&gt;&lt;n&gt;&lt;r p="…"&gt;</c> and renders row <paramref name="lineIndex"/>.
        ///
        /// <para>The rows carry a speaker (<c>p="lp1"…"lp4"</c> = Littlepip, <c>"pip"</c> = the PipBuck,
        /// <c>"mtn"</c> = a MAS terminal), which is what makes <c>begDial</c>'s three lines read as
        /// Littlepip talking rather than as anonymous captions.</para>
        /// </summary>
        /// <returns><c>false</c> when the id is unknown or has no row at that index — AS3's
        /// <c>return false</c> at <c>GUI.as:1766-1781</c>.</returns>
        bool ShowDialogue(string dialogueId, int lineIndex, bool down, bool wait);

        /// <summary>
        /// AS3 <c>gui.dialText(&lt;r mod=… push=…&gt;text&lt;/r&gt;, 0, false, true)</c> — the
        /// <b>inline</b> form, used by <c>inform</c> (<c>Script.as:273-280</c>) and <c>landlevel</c>
        /// (<c>:281-295</c>). No table lookup: the text is already in hand.
        /// </summary>
        /// <param name="mod">AS3 <c>@mod</c>: 0 = the dialogue window, ≥1 = the information panel,
        /// 2 = the panel with its OK button.</param>
        void ShowRawDialogue(string text, int mod, bool push, bool wait);

        /// <summary>AS3 <c>gui.dialText()</c> with no id (<c>Script.as:210-213</c>) — hides the
        /// dialogue window.</summary>
        void HideDialogue();

        /// <summary>AS3 <c>control off</c> / <c>control on</c> (<c>Script.as:239-246</c>) — the cutscene
        /// input lock. Off means the player cannot move.</summary>
        void SetControlEnabled(bool enabled);

        /// <summary>AS3 <c>gui off</c> / <c>gui on</c> (<c>Script.as:384-391</c>) — the HUD bar.</summary>
        void SetGuiEnabled(bool enabled);

        /// <summary>AS3 <c>black val</c> (<c>Script.as:371-379</c>) — set the blackout overlay's alpha,
        /// and clear the camera's fade. <paramref name="alpha"/> is the authored <c>@val</c>.</summary>
        void SetBlack(float alpha);

        /// <summary>AS3 <c>dblack val</c> (<c>Script.as:380-383</c>) — the camera's fade rate. A negative
        /// value fades in, a positive one fades out.</summary>
        void SetBlackFade(float rate);

        /// <summary>AS3 <c>anim val</c> (<c>Script.as:355-358</c>) — play a named animation on the player.
        /// <paramref name="flag"/> is the authored <c>@opt1 &gt; 0</c>.</summary>
        void PlayPlayerAnimation(string animation, bool flag);

        /// <summary>AS3 <c>turn val</c> (<c>Script.as:359-370</c>) — face the player. A positive
        /// <c>@val</c> faces right.</summary>
        void TurnPlayer(bool faceRight);

        /// <summary>AS3 <c>music val</c> (<c>Script.as:337-347</c>) — start a track.</summary>
        void PlayMusic(string trackId, int loops);

        /// <summary>AS3 <c>show</c> (<c>Script.as:416-419</c>) — clears <c>cam.showOn</c>, the camera's
        /// opening pan.</summary>
        void ClearCameraIntro();

        /// <summary>AS3 <c>pip val, n</c> (<c>Script.as:528-531</c>) → <c>pip.onoff(val, n)</c>. The
        /// PipBuck page selector; page <c>-1</c> closes it.</summary>
        void OpenPipPage(string page, string subPage);

        /// <summary>
        /// AS3 <c>landlevel val</c>'s composed body (<c>Script.as:281-295</c>): the land's name, its
        /// recommended level, the player's level, and — when the land outranks the player — the "you are
        /// under-levelled" line.
        /// </summary>
        void ShowLandLevel(string landId);
    }

    /// <summary>
    /// The object half of the vocabulary: the <c>@targ</c> path (<c>Script.as:218-236</c>), the
    /// <c>allact</c> bridge (<c>:296-299</c>) and <c>goto</c> (<c>:435-442</c>).
    /// </summary>
    public interface IScriptObjectHost
    {
        /// <summary>
        /// AS3 <c>_loc2_.command(act, val)</c> — forward a command to one object, found by uid or code.
        ///
        /// <para><b>This is a different dispatch from the campaign chain and must not be merged with
        /// it.</b> <c>sign</c>, <c>off</c>, <c>on</c>, <c>onoff</c>, <c>open</c>, <c>close</c>,
        /// <c>unlock</c> and <c>lock</c> all live on the object (<c>Obj.command</c> /
        /// <c>Box.command</c>). <c>unlock</c> is what <c>beginMtn</c> uses on <c>begDoor</c>.</para>
        ///
        /// <para><b>The room is a parameter because the lookup is room-scoped and the host is not.</b>
        /// The runner already holds the room a script belongs to (<c>RunningScript.Room</c>), and a room
        /// streams in and out independently of the host that resolves objects for it. A host that
        /// remembered "the current room" would answer against a stale one the moment two rooms are alive
        /// — which is the normal case while a land is being streamed.</para>
        /// </summary>
        /// <returns><c>false</c> when no object matched, so the caller can report a bad uid rather than
        /// let it read as a command that ran.</returns>
        bool ObjectCommand(RoomInstance room, string uidOrCode, string command, string value);

        /// <summary>
        /// The same dispatch for an object the caller <b>already holds</b> — the <c>@targ="this"</c>
        /// half (<c>Script.as:222</c>).
        ///
        /// <para><b>Why a separate overload and not a string round trip.</b> The oracle writes
        /// <c>if(param1.targ == "this") _loc2_ = this.owner;</c> and calls <c>command()</c> on that
        /// reference. It never converts the owner to an id and looks it up. The port did, and the
        /// indirection is not merely wasteful — it is <i>wrong</i> for every object whose <c>uid</c> is
        /// empty, which is the common case here: a room object carries a generated <c>code</c> and a
        /// blank <c>uid</c>. A <c>uid ?? code</c> chain falls through only on <c>null</c>, never on
        /// <c>""</c>, so <c>"this"</c> resolved to the empty string, the lookup found nothing, and 57
        /// authored <c>act="off" targ="this"</c> actions across 49 rooms were dropped with a
        /// "no object matches that uid or code" warning.</para>
        ///
        /// <para><b>There is deliberately no room-membership test.</b> The oracle commands whatever the
        /// owner is; it does not first check that the owner is in <c>land.uidObjs</c>. A script's owner
        /// is by construction the object the script hangs off, so a membership check could only ever
        /// reject a legitimate owner.</para>
        /// </summary>
        /// <returns><c>false</c> when <paramref name="target"/> is null or the command is unknown.</returns>
        bool ObjectCommand(RoomInstance room, ObjectInstance target, string command, string value);

        /// <summary>AS3 <c>allact val</c> (<c>Script.as:296-299</c>) → <c>loc.allAct(null, val, n)</c> —
        /// run an interaction by id, which is the bridge between the two vocabularies. The oracle passes
        /// a <b>null</b> object here, so an action that needs the object it came from is not reachable
        /// this way.</summary>
        void RunAllAct(RoomInstance room, string allactId, string n);

        /// <summary>AS3 <c>goto val</c> (<c>Script.as:435-442</c>) — move the player to a room cell
        /// (<c>val</c> is <c>"x y"</c>).</summary>
        void GotoXY(string x, string y);
    }

    /// <summary>
    /// The <b>unit</b> half of the <c>@targ</c> vocabulary — the commands that land on a live
    /// <c>Unit</c> rather than on a map object.
    ///
    /// <para><b>Why this is a second interface and not more methods on
    /// <see cref="IScriptObjectHost"/>.</b> AS3 has one table (<c>land.uidObjs</c>) holding objects
    /// <i>and</i> units, but the port does not: objects are records on <c>room.objects</c> resolved by
    /// <c>AreaTriggerSystem</c>, and units are records on <c>room.units</c> whose live controllers are
    /// owned by <c>RoomUnitSpawner</c>. Those are different owners in different layers, so the seam
    /// splits where the ownership does. The <i>caller</i> still tries both in one place, which is what
    /// keeps the oracle's single-lookup behaviour observable.</para>
    ///
    /// <para><b>The verbs are the unit's own.</b> <c>show</c>, <c>fly</c>, <c>sign</c>, <c>ai</c>,
    /// <c>rep</c>, <c>hide</c>, <c>rem</c>, <c>turn</c>, <c>tell</c> and <c>activate</c> are
    /// <c>UnitNPC.command</c> / <c>UnitNPC.analiz</c> (<c>UnitNPC.as:321-442</c>) and
    /// <c>Unit.command</c> (<c>Unit.as:4784-4811</c>). They overlap in spelling with the object verbs
    /// (<c>sign</c> is on both) and mean different things, which is why the object host is asked first
    /// and a unit is only reached when it declines — the same precedence the oracle's single table gets
    /// for free from the fact that one id names one object.</para>
    /// </summary>
    public interface IUnitCommandHost
    {
        /// <summary>
        /// Forward a command to one <i>live</i> unit, found by the uid or code the room authored.
        ///
        /// <para><b>The lookup is over <c>room.units</c> and the write is on the spawned controller.</b>
        /// A unit record with no live GameObject (one that died this frame, or a room whose spawner has
        /// not run) is a miss, not an error: the caller reports an unmatched uid only when <i>both</i>
        /// hosts decline.</para>
        /// </summary>
        /// <returns><c>false</c> when no unit matched, so the caller can report a bad uid rather than
        /// let it read as a command that ran.</returns>
        bool UnitCommand(RoomInstance room, string uidOrCode, string command, string value);
    }

    /// <summary>
    /// The player half of the vocabulary — the commands that change the character rather than the world.
    /// </summary>
    public interface IScriptPlayerHost
    {
        /// <summary>AS3 <c>take val, n</c> (<c>Script.as:300-316</c>). A negative <c>@n</c> withdraws.</summary>
        void TakeItem(string itemId, int count);

        /// <summary>AS3 <c>armor val</c> (<c>Script.as:317-320</c>) — equip an armour set.</summary>
        void ChangeArmor(string armorId);

        /// <summary>AS3 <c>xp val</c> (<c>Script.as:321-324</c>).</summary>
        void GrantXp(int amount);

        /// <summary>AS3 <c>perk val</c> (<c>Script.as:325-328</c>).</summary>
        void AddPerk(string perkId);

        /// <summary>AS3 <c>eff val, opt1, opt2</c> (<c>Script.as:329-332</c>).</summary>
        void AddEffect(string effectId, int opt1, int opt2);

        /// <summary>AS3 <c>remeff val</c> (<c>Script.as:333-336</c>).</summary>
        void RemoveEffect(string effectId);

        /// <summary>AS3 <c>weapch val</c> (<c>Script.as:506-509</c>) — switch weapon.</summary>
        void ChangeWeapon(string weaponId);
    }

    /// <summary>
    /// The <c>wait</c> seam — AS3 <c>Script.step</c> reads <c>World.w.ctr.keyPressed</c> /
    /// <c>keyPressed2</c> (<c>Script.as:154-183</c>) and refuses to advance until one of them is set.
    ///
    /// <para><b>Why a probe and not <c>InputReader</c>.</b> <see cref="PFE.Core.ISimTickable"/> forbids a
    /// tick from reading input directly — input is gathered once on the frame boundary into a snapshot and
    /// handed to the tick. This interface is that snapshot's script-relevant half: a latch the frame
    /// layer sets and the runner consumes. Reading <c>InputReader</c> from inside a tick would work today
    /// and break the determinism contract the moment a second peer exists.</para>
    /// </summary>
    public interface IScriptInputProbe
    {
        /// <summary>Whether the action key is down. AS3 <c>ctr.keyPressed</c> — held, not edge.</summary>
        bool ActionKeyHeld { get; }

        /// <summary>
        /// Whether the "skip the rest of this dialogue" input was pressed since the last clear. AS3
        /// <c>ctr.keyPressed2</c>, which <c>Ctr.onRightMouseDown1</c> sets (<c>Ctr.as:582-588</c>) —
        /// right mouse button, not the action key. Reading <c>dial_n = 10000</c> is the oracle's way of
        /// saying "run past the last line" (<c>Script.as:156-159</c>).
        /// </summary>
        bool AdvancePressed { get; }

        /// <summary>
        /// Clear both latches — AS3 sets <c>keyPressed = keyPressed2 = false</c> after consuming them
        /// (<c>Script.as:183</c>) <b>and at the start of every command</b> (<c>:215</c>). The second
        /// clear is the one that is easy to miss: without it, one keypress satisfies the dialogue walk
        /// <i>and</i> whatever blocking command follows it.
        /// </summary>
        void ClearPresses();
    }
}
