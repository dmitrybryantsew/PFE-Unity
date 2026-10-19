using System;
using System.Collections.Generic;
using System.Globalization;
using PFE.Data.Definitions;
using PFE.Data.Definitions.Campaign;
using PFE.Entities.Enemies;
using PFE.Entities.Player;
using PFE.Systems.Campaign;
using PFE.Systems.Interaction;
using PFE.Systems.Inventory;
using PFE.Systems.Map;
using PFE.Systems.Map.Scripting;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.RPG;
using PFE.Systems.Trade;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// The port of <c>fe/unit/UnitNPC.as</c> — the class behind every authored NPC placement in the
    /// game, and the one the surf land's <c>surfCalam</c> needs.
    ///
    /// <para><b>Why nothing worked before this class existed.</b> <c>&lt;obj id='npc' cl='UnitNPC'/&gt;</c>
    /// (<c>AllData.as:4926</c>) is the definition row every NPC placement resolves to, and
    /// <c>RoomUnitSpawner</c> had no entry for it — so a <c>type: unit, id: npc</c> placement fell
    /// through to the bare <c>UnitController</c>, which has no art swap, no persona, no script verbs and
    /// no interaction. That is the whole of the reported "there is no Calamity character": he was
    /// spawned, as a collider, with no sprite, forever invisible, and every one of <c>surfStory</c>'s
    /// five <c>@targ="surfCalam"</c> actions was dropped.</para>
    ///
    /// <para><b>Two documents, one unit.</b> An NPC's identity is split in the oracle. The unit row says
    /// the unit is class <c>UnitNPC</c>; the placement says <i>which</i> persona it is
    /// (<c>npc="calam"</c>); and the persona row in <c>GameData.as:669</c> carries the art
    /// (<c>vis='Calam'</c>), the interaction (<c>inter='travel'</c>), the weapon (<c>weap='sniper'</c>)
    /// and the dialogue table. <c>UnitNPC</c>'s constructor joins them at <c>:79-91</c> and everything
    /// below reads off the result. The port's equivalent join is <see cref="INpcDefinitionProvider"/>,
    /// handed in by the spawner because <c>AddComponent</c> never goes through VContainer.</para>
    ///
    /// <para><b>Why it extends <c>EnemyController</c> and not <c>UnitController</c>.</b> An NPC is not an
    /// enemy, but it needs three things only <c>EnemyController</c> has: the weapon stack (Calamity
    /// carries <c>sniper</c> and <c>ai="agro"</c> is what makes him use it), the brain tick, and the
    /// per-instance faction override. What keeps him <i>friendly</i> is not the class — it is his
    /// data: <c>&lt;unit id='npc' fraction='100'&gt;</c> is AS3's <c>F_PLAYER</c> (<c>Unit.as:78</c>), so
    /// the already-landed faction gate in <c>EnemyBrain.IsValidTarget</c> refuses the player as a target
    /// and <c>ai="agro"</c> means "shoot the raiders", which is exactly what the oracle's
    /// <c>surfStory</c> beat is for.</para>
    ///
    /// <para><b>What is deliberately not ported, each named rather than silently dropped.</b></para>
    /// <list type="bullet">
    /// <item><description><c>replic()</c> — the idle voice lines (<c>:477</c>) and the
    /// <c>replicVse</c> verb (<c>:365-369</c>). The port has no per-unit voice-line table; the
    /// <c>replicVse</c> branch is a real branch and is recognised here, but it plays nothing. That is a
    /// missing subsystem, not a missing call.</description></item>
    /// <item><description><c>zzzGen</c> particles (<c>:481-491</c>) — <c>NPC.refresh</c> sets
    /// <c>zzzGen = rep == 2</c> for the <c>patient</c> persona, and the oracle emits a <c>zzz</c>
    /// particle every 120 frames. The port's particle emitter is available but the persona's
    /// <c>zzzGen</c> state has no field yet. <b>The branch's animation half is a no-op for the patient
    /// and is therefore not ported either:</b> it is <c>vis.osn.gotoAndStop(1)</c>, and the patient's
    /// clip has the single label <c>stay</c> — so the fidget's <c>gotoAndPlay("move1")</c> throws and
    /// the playhead is already on frame 1 when the reset would run.</description></item>
    /// <item><description><c>mater</c> (<c>:431-441</c>) — AS3's "material" flag, which decides whether
    /// the unit is drawn with its palette swap. The port has no such switch.</description></item>
    /// <item><description>The <c>ico</c> objective icon (<c>:351-364</c>, <c>:578</c>) — the <c>sign</c>
    /// verb is ported as a <i>rule</i> (<see cref="ObjectiveSignVisible"/>) because no renderer consumes
    /// it yet, and inventing a sprite for it would be guessing at a HUD element the port does not
    /// have.</description></item>
    /// <item><description>The <c>vis</c> frame animation (<c>:243-281</c>) — <b>ported</b>, by
    /// <see cref="NpcVisualAnimator"/> against the persona's own <c>osn</c> clip. The clip's frames and
    /// its label table come from <c>UnitSpriteImporter</c>'s NPC pass; see
    /// <see cref="PFE.Data.Definitions.Campaign.NpcDefinition.visualFrames"/> for why the frames live in
    /// a child MovieClip rather than in the <c>visual&lt;Vis&gt;</c> symbol the rest of the port
    /// imports.</description></item>
    /// <item><description><c>UnitNPC.findCel</c>'s <c>invis</c> test (<c>:449</c>). <c>invis</c> is
    /// declared (<c>Unit.as:468</c>) and read (<c>:2720</c>, <c>:2890</c>, <c>:2985</c>, and here) but
    /// <b>never written anywhere in the oracle</b> — so <c>surfCalam</c>'s authored
    /// <c>invis="1"</c> is dead data and porting it would invent an untargetability the original does
    /// not have. Recorded because it is the kind of attribute a reader expects to matter.</description></item>
    /// </list>
    /// </summary>
    [RequireComponent(typeof(UnitNpcBrain))]
    public class UnitNPCController : EnemyController, IInteractable, IInteractableName
    {
        /// <summary>AS3's controller class name — <c>AllData.as:4926</c> <c>&lt;obj … cl='UnitNPC'/&gt;</c>.</summary>
        public const string ControllerId = "UnitNPC";

        /// <summary>
        /// AS3 <c>UnitNPC.icoFrame</c>'s fallback for a persona with no <c>ico</c> attribute
        /// (<c>:153</c>), and the doctor's (<c>:149</c>).
        /// </summary>
        public const int DefaultIconFrame = 2;

        // ── AS3 UnitNPC's own fields (UnitNPC.as:11-59), same names where they carry behaviour ─────

        /// <summary>AS3 <c>que</c> — the verb queue <see cref="Command"/> falls through to.</summary>
        readonly List<QueuedNpcVerb> _que = new List<QueuedNpcVerb>();

        /// <summary>The weapon ids the persona authored, in equip order. AS3 <c>weap</c>/<c>weap2</c>.</summary>
        readonly List<string> _equippedWeaponIds = new List<string>();

        /// <summary>AS3 <c>targNPC</c> — the persona. <c>null</c> when the placement named one the catalogue does not have.</summary>
        NpcDefinition _persona;

        /// <summary>AS3 <c>npcId</c> — the persona id, or the unit's own id when the placement named none.</summary>
        string _npcId = string.Empty;

        /// <summary>AS3 <c>targNPC.rep</c> — the persona's progression counter.</summary>
        int _rep;

        /// <summary>AS3 <c>aiTip</c> — the one AI switch an NPC has.</summary>
        string _aiTip = string.Empty;

        /// <summary>AS3 <c>animFly</c> — whether this NPC ever flies at all, from the placement's <c>fly</c>.</summary>
        bool _animFly;

        /// <summary>AS3 <c>isFly</c> — whether it is flying <i>now</i>. Read by the physics gates.</summary>
        bool _isFly;

        /// <summary>AS3 <c>t_ref</c> — the re-interaction lockout, in frames (<c>:299-303</c>).</summary>
        int _tRef;

        /// <summary>AS3 <c>wait</c> — frames before the next queued verb may run (<c>:493-500</c>).</summary>
        int _wait;

        /// <summary>AS3 <c>dey</c> — <c>"fly"</c> while a flight is in progress (<c>:408</c>, <c>:533</c>).</summary>
        string _dey = string.Empty;

        /// <summary>AS3 <c>cx</c> — the flight target's X, in the port's room-local pixels.</summary>
        float _cx = -1f;

        /// <summary>AS3 <c>cy</c> — the flight target's Y, in the port's room-local pixels.</summary>
        float _cy = -1f;

        /// <summary>AS3 <c>t_float</c> — the hover bob's phase (<c>:523</c>).</summary>
        float _tFloat;

        /// <summary>AS3 <c>vis.alpha</c> — the reveal fade's progress (<c>:506-509</c>).</summary>
        float _revealAlpha = 1f;

        INpcDefinitionProvider _npcProvider;
        SpriteRenderer _npcRenderer;

        /// <summary>
        /// The persona's own clip player — AS3's <c>vis.osn</c> MovieClip, and the <c>animate()</c>
        /// that drives it. <c>null</c> until <see cref="ApplyPersonaArt"/> has both a persona and a
        /// renderer to bind.
        /// </summary>
        NpcVisualAnimator _npcAnimator;

        /// <summary>
        /// The NPC's overhead bubble — AS3 <c>this.ico</c> (<c>UnitNPC.as:157-159</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>What the oracle builds.</b> Three lines in the constructor:
        /// <c>this.ico = new visNPCIco(); this.ico.y = -140; vis.addChild(this.ico);</c> — a 31-frame
        /// clip parented to the unit's own visual, so it mirrors with <c>storona</c> and is hidden by
        /// the same <c>vis.visible</c> the body uses. Its frame comes from <c>NPC.setIco</c>
        /// (<c>NPC.as:474-490</c>): <c>icoFrame</c> — the persona's <c>ico</c> attribute — by default,
        /// or the label <c>"dial" + n</c> when <c>setStatus(n)</c> has dialogue to announce.</para>
        ///
        /// <para><b>Nothing consumed any of it, which is the reported "NPCs have no bubble".</b>
        /// <c>PipBuckCompositionDefinition.npcPortraits</c> held all 31 frames, <c>NpcDefinition.iconIndex</c>
        /// held the persona's frame and <c>ObjectiveSignVisible</c> held the sign rule, and there was no
        /// renderer, no frame selection and no caller — three pieces with zero consumers.</para>
        ///
        /// <para><b>Frame 1 is blank, and that is the whole "no bubble" state.</b> Measured on the
        /// export: <c>DefineSprite_4844</c> frame 1 has 0 opaque pixels of 118x142. The oracle never
        /// hides the icon on a talkable NPC — it leaves it visible on a transparent frame — so the port
        /// treats "frame 1" as "nothing to draw" rather than carrying a second visibility flag.</para>
        ///
        /// <para><b>The two label frames are read from the SWF, not guessed.</b> <c>DefineSprite_4844</c>
        /// carries exactly two <c>FrameLabel</c> tags: frame <b>30</b> = <c>dial1</c> (bubble with text
        /// lines) and frame <b>31</b> = <c>dial2</c> (bubble with three exclamation marks). Read out of
        /// the tag stream of <c>pfe/scripts/_assets/assets.swf</c> — 31 frames, no <c>DoAction</c>, so the
        /// clip loops and a static frame is the correct reading of <c>gotoAndStop</c>.</para>
        ///
        /// <para><b>Placement.</b> The export canvas is 118x142 px and the art occupies
        /// <c>y 0..44, x 43..76</c> — at the <i>top</i> of the canvas, horizontally centred.
        /// <c>ico.y = -140</c> therefore puts the canvas's top edge 140 px above the unit's feet, so the
        /// art spans <b>96..140 px above the feet</b>. The imported sprite has a centre pivot at 100 px
        /// per unit, so the canvas centre belongs at <c>140 - 142/2 = 69 px</c> above the feet — that is
        /// <see cref="NpcBubbleHeightPixels"/>, and it is why the number is not the oracle's 140.</para>
        /// </remarks>
        SpriteRenderer _npcBubble;

        /// <summary>
        /// Frames until <see cref="RefreshBubble"/> may re-evaluate the dialogue gate.
        /// </summary>
        /// <remarks>
        /// <b>Polled, not event-driven, and the cadence is the honest part.</b> AS3 recomputes the icon
        /// on <c>NPC.refresh()</c> and on the scripted <c>check</c> verb; the port has neither a
        /// per-persona refresh broadcast nor a trigger-change event reachable from here, so a script
        /// that sets a trigger and immediately expects a bubble would otherwise wait for the next room
        /// build. One evaluation a second is cheap — <c>NpcDialogueGate.TryFindGreeting</c> walks one
        /// persona's own dialogue list — and it is a poll the oracle does not have, which is why it is
        /// named here rather than buried in the tick.
        /// </remarks>
        int _npcBubbleRefreshTicks;

        RoomInstance _room;

        /// <summary>
        /// The room's object half of AS3's single <c>land.uidObjs</c> table, so a dialogue script's
        /// <c>@targ</c> can name an object. See <see cref="SetScriptHosts"/>.
        /// </summary>
        IScriptObjectHost _scriptObjects;

        /// <summary>The room's unit half of the same table.</summary>
        IUnitCommandHost _scriptUnits;

        /// <summary>Whether the persona's weapon has been equipped. Idempotent, so this only gates the warning.</summary>
        bool _weaponsEquipped;

        bool _reportedMissingPersona;
        bool _reportedNoCatalog;
        bool _reportedNoBubbleFrames;

        /// <summary>
        /// The player's stats and inventory, resolved on first use by <see cref="ResolvePlayerStats"/> /
        /// <see cref="ResolvePlayerInventory"/>. Cached because the bubble poll evaluates the dialogue
        /// gate every second; never cached as <c>null</c>, because the player may be assembled after the
        /// first NPC tick.
        /// </summary>
        CharacterStats _playerStats;
        GameInventory _playerInventory;

        readonly HashSet<string> _reportedUnwiredVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _reportedUnwiredActivations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Public surface, for the spawner, the brain and a fixture ───────────────────────────────

        /// <summary>AS3 <c>npcId</c>. Empty until <see cref="SetPlacement"/> has run.</summary>
        public string NpcId => _npcId;

        /// <summary>AS3 <c>targNPC.rep</c> — 0 before the <c>rep</c> verb or a landing sets it.</summary>
        public int Rep => _rep;

        /// <summary>AS3 <c>aiTip</c> — <c>"fly"</c> or <c>"agro"</c> in the shipped data.</summary>
        public string AiTip => _aiTip;

        /// <summary>
        /// AS3 <c>ico.sign.visible</c> — the objective icon's resolved visibility, from the <c>sign</c>
        /// verb (<c>:351-364</c>) and the landing arm (<c>:603-606</c>).
        ///
        /// <para>Exposed as the <i>rule</i> rather than drawn: the port has no objective-icon renderer,
        /// and the two values the oracle reads to decide it — <c>World.w.helpMess</c> (the player's help
        /// setting) and <c>aiTip == "fly"</c> — are both available here. A caller that later draws the
        /// icon reads this and does not re-derive the rule.</para>
        /// </summary>
        public bool ObjectiveSignVisible { get; private set; }

        /// <summary>AS3 <c>animFly</c> — from the placement's <c>fly</c> attribute.</summary>
        public bool CanFly => _animFly;

        /// <summary>
        /// AS3 <c>isFly</c> — airborne <i>now</i>, cleared by <see cref="Land"/>.
        /// </summary>
        /// <remarks>
        /// <b>Public because the F3 panel has to show it.</b> <c>IsFlying</c> is the protected
        /// physics-facing override; this is the same field read for reporting, the pattern
        /// <c>UnitController.IsFlyingUnit</c> already uses for the motor. The landing block is gated on
        /// <c>_isFly &amp;&amp; IsGrounded</c>, so "he stopped shooting" and "he is still flying" are
        /// otherwise indistinguishable from the outside — and the difference decides whether
        /// <c>aiTip</c> was cleared by a landing or never set at all.
        /// </remarks>
        public bool IsFlyingNow => _isFly;

        /// <summary>AS3 <c>isFly</c> — read by <c>Trap.attKorp</c> and by both gravity paths.</summary>
        protected override bool IsFlying => _isFly;

        /// <summary>
        /// AS3 <c>que</c>'s depth — how many authored verbs are still waiting on <c>wait</c>.
        /// </summary>
        /// <remarks>
        /// <b>The one number that explains "he has a target and never fires".</b> <c>agro</c> does not
        /// come from the placement — <c>surfCalam</c> is placed with <c>fly="1"</c> and no <c>ai</c> —
        /// so it arrives as a queued <c>ai agro</c> from <c>surfStory</c>, and a queued verb runs only
        /// once <see cref="QueueWaitFrames"/> has reached 0. While <c>fly</c> is in flight that clock is
        /// 1000 frames, so for the whole flight the agro verb is sitting here doing nothing. A panel
        /// showing <c>aiTip='fly'</c> reads as "he is flying", which is true and is not the problem; a
        /// panel showing <c>queue=1 wait=812</c> names it.
        /// </remarks>
        public int PendingQueuedVerbCount => _que.Count;

        /// <summary>AS3 <c>wait</c> — the frames the queue is still blocked on. See <see cref="PendingQueuedVerbCount"/>.</summary>
        public int QueueWaitFrames => _wait;

        /// <summary>How many weapon ids the persona authored — AS3 <c>weap</c> plus <c>weap2</c>.</summary>
        public int EquippedWeaponCount => _equippedWeaponIds.Count;

        /// <summary>
        /// The persona's primary weapon id (<c>&lt;npc weap='sniper'/&gt;</c>), or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <c>_equippedWeaponIds</c> is the authority for both the equip and the trigger, so a
        /// diagnostic that reports an id from anywhere else — the persona, the definition — could
        /// disagree with the list that actually gets looped over. This reads the list.
        /// </remarks>
        public string PrimaryWeaponId =>
            _equippedWeaponIds.Count > 0 ? _equippedWeaponIds[0] : null;

        /// <summary>The mount for <see cref="PrimaryWeaponId"/>, or <c>null</c> when it was never built.</summary>
        public EnemyWeaponMount PrimaryWeaponMount =>
            _equippedWeaponIds.Count > 0 ? GetWeaponMount(_equippedWeaponIds[0]) : null;

        /// <summary>
        /// How many mounts the last <see cref="SetWeaponTrigger"/> call actually reached.
        /// </summary>
        /// <remarks>
        /// The trigger loop is a <c>foreach</c> over ids with a <c>null</c>-skipping body, so an empty id
        /// list and a list whose mounts failed to build are the same silent no-op. Counting what was
        /// reached separates them from "the trigger was never pushed at all", which is a third thing
        /// again and lives on the brain's side.
        /// </remarks>
        public int LastTriggerMountCount { get; private set; }

        /// <summary>The persona this NPC resolved, or <c>null</c>. Read by the interaction ladder.</summary>
        public NpcDefinition Persona => _persona;

        /// <summary>AS3 <c>inter.active</c> — a hidden NPC cannot be walked up to (<c>Unit.as:1845</c>).</summary>
        public string DisplayName => _persona != null && !string.IsNullOrEmpty(_persona.aliasName)
            ? _persona.aliasName
            : _npcId;

        // ── The seams the spawner uses ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Hand the NPC catalogue in, so <see cref="SetPlacement"/> can resolve the persona.
        /// </summary>
        /// <remarks>
        /// Same handover shape and the same reason as <c>SetDamageSystem</c>: a spawned unit is built
        /// with <c>AddComponent</c>, which VContainer never observes, so an <c>[Inject]</c> field could
        /// not reach it. <c>null</c> falls back to the <c>Resources</c>-backed shared provider, so a
        /// spawner built by a test gets a provider that simply misses every id rather than a
        /// <c>NullReferenceException</c>.
        /// </remarks>
        public void SetNpcProvider(INpcDefinitionProvider provider)
        {
            _npcProvider = provider;
        }

        /// <summary>
        /// Hand the room in, so the <c>fly</c> verb can convert an AS3 tile into this port's room-local
        /// pixel space.
        /// </summary>
        /// <remarks>
        /// <b>Why the room and not just the tile pitch.</b> AS3's <c>Y</c> grows downward and this
        /// port's grows upward, and the mirror is anchored on the room: <c>RoomPopulator</c> places a
        /// unit at <c>(roomHeight - borderOffset - tileY - 1) * 40 + 1</c> for an AS3 anchor of
        /// <c>(tileY + 1) * 40 - 1</c>. A conversion that did not know the room's height could not
        /// reproduce that, and Calamity would fly to the vertically mirrored tile.
        /// </remarks>
        public void SetRoom(RoomInstance room)
        {
            _room = room;
        }

        /// <summary>
        /// Hand in the room's two halves of AS3's <c>land.uidObjs</c> table, so a dialogue script's
        /// <c>@targ</c> can resolve — an object through the first, a unit through the second.
        /// </summary>
        /// <remarks>
        /// <para><b>The oracle builds a dialogue's script with the LAND as its context.</b>
        /// <c>NPC.check(true)</c> (<c>NPC.as:322</c>) is
        /// <c>_loc4_ = new Script(_loc2_.scr[0], World.w.land, this.owner, true);</c> — the second
        /// argument is the land, and it is what <c>Script.com</c> uses to resolve
        /// <c>@targ</c> (<c>Script.as:218-236</c>: <c>this.land.uidObjs[param1.targ]</c>).</para>
        ///
        /// <para><b>What was missing, and what it silently dropped.</b>
        /// <see cref="RunGreeting"/> built its runner with <c>objects: null, units: null</c>, so every
        /// dialogue action that names a target was dropped by the "no object in the room matches that
        /// uid or code" path — the same failure the unit half of the table was added to fix, one layer
        /// further out. The shipped data makes it visible: the camp's bar vendor authors
        /// <c>unlock targ="doorRBL1"</c> (<c>vendor_bar_hi</c>), <c>check targ="rblCalam"</c>
        /// (<c>vendor_bar_stable</c> and <c>vendor_bar_mane</c>) and <c>sign targ="worldMap"</c>
        /// (<c>vendor_bar_task</c>, the row that also runs <c>openland random_plant</c>) — three
        /// different verbs, all target-bearing, all dropped.</para>
        ///
        /// <para><b>Both arguments are tolerated as null</b>, and null is exactly the old behaviour: a
        /// target-bearing action is reported once and skipped, rather than throwing out of an
        /// interaction.</para>
        /// </remarks>
        public void SetScriptHosts(IScriptObjectHost objects, IUnitCommandHost units)
        {
            _scriptObjects = objects;
            _scriptUnits = units;
        }

        /// <summary>
        /// Read the placement the way <c>UnitNPC</c>'s constructor does (<c>:61-211</c>), and apply the
        /// persona's own starting state.
        /// </summary>
        /// <remarks>
        /// <para><b>Call before <c>Initialize</c>.</b> The oracle reads <c>xml.@npc</c> in its
        /// constructor and creates the weapon from the persona on the next line (<c>:175-185</c>), so
        /// the persona has to be in hand before the port equips anything. The spawner therefore calls
        /// this immediately after <c>AddComponent</c>, and <see cref="Initialize"/> equips from it.</para>
        ///
        /// <para><b>It is a separate seam from <c>ApplyPlacement</c> on purpose.</b>
        /// <c>ApplyPlacement</c> runs last in the spawn sequence and carries the base <c>Unit</c>'s own
        /// fields (facing, faction); the persona join has to happen earlier, and collapsing the two
        /// would make the equip order depend on an unrelated call.</para>
        /// </remarks>
        public void SetPlacement(UnitInstance placement)
        {
            // UnitNPC.as:70-77 — an empty `cid` becomes the literal id "npc"; the persona id then comes
            // from `xml.@npc` at :89, falling back to that id at :94-96.
            string personaId = placement != null ? placement.GetAttribute("npc") : null;
            _npcId = string.IsNullOrEmpty(personaId) ? "npc" : personaId;

            INpcDefinitionProvider provider = _npcProvider ?? ResourcesNpcDefinitionProvider.Shared;
            if (!provider.TryGetNpc(_npcId, out _persona))
            {
                _persona = null;
                if (!_reportedMissingPersona)
                {
                    _reportedMissingPersona = true;
                    Debug.LogWarning(
                        $"[UnitNPCController] '{name}' was placed with npc='{_npcId}' but no " +
                        "NpcDefinition carries that id, so it has no art, no interaction and no " +
                        "persona state. Run PFE/Data/Import Campaign if the catalogue is missing.");
                }
            }

            if (_persona != null)
            {
                ApplyPersonaArt(_persona);

                // UnitNPC.as:133-140 — `weap` / `weap2` off the persona row.
                if (!string.IsNullOrEmpty(_persona.defaultWeapon))
                {
                    _equippedWeaponIds.Add(_persona.defaultWeapon);
                }

                if (!string.IsNullOrEmpty(_persona.secondaryWeapon))
                {
                    _equippedWeaponIds.Add(_persona.secondaryWeapon);
                }
            }

            if (placement == null)
            {
                return;
            }

            // UnitNPC.as:192-208 — the three placement attributes the class itself reads, in the
            // oracle's order. Note what is NOT here: `invis`, which the oracle declares, reads and never
            // writes. See the class summary.
            if (!string.IsNullOrEmpty(placement.GetAttribute("fly")))
            {
                _animFly = true;
                _aiTip = "fly";
                _isFly = true;
            }

            if (!string.IsNullOrEmpty(placement.GetAttribute("hide")))
            {
                Hide();
            }

            string placementAi = placement.GetAttribute("ai");
            if (!string.IsNullOrEmpty(placementAi))
            {
                _aiTip = placementAi;
            }

            // `animFly`/`isFly` are read directly off the unit in AS3, but the clip player is a
            // separate component here — and `ApplyPersonaArt` ran before the `fly` attribute was read,
            // so it bound the player with both still false.
            SyncAnimatorFlight();

            ApplyPersonaInit();
        }

        /// <summary>
        /// <c>NPC.init()</c> (<c>NPC.as:110-131</c>) plus the <c>addVisual</c> half of
        /// <c>UnitNPC.as:213-228</c> — the persona's starting hidden state, which is what makes Calamity
        /// invisible until the script's <c>show</c>.
        /// </summary>
        /// <remarks>
        /// <b>The <c>rep == 0</c> half is the whole reason Calamity is not standing in the surf room
        /// from the start.</b> <c>NPC.as:112-117</c> is
        /// <c>if(id == "calam") { if(rep == 0 || trig("rbl_visited") &gt; 0) hidden = true; }</c>, and a
        /// freshly spawned NPC has <c>rep == 0</c> — so he begins hidden, <c>surfStory</c>'s
        /// <c>show</c> reveals him, and once the player has been to the camp he stays hidden for the
        /// rest of the game.
        /// </remarks>
        void ApplyPersonaInit()
        {
            if (_persona == null)
            {
                return;
            }

            CampaignManager campaign = CampaignManager.Current;
            bool rblVisited = campaign != null && TriggerTruthy(campaign.GetTrigger("rbl_visited"));

            bool hidden = UnitNpcRules.InitialHidden(_npcId, _rep, rblVisited);

            string aiTip = UnitNpcRules.InitialAiTip(_npcId, _rep);
            if (aiTip != null)
            {
                _aiTip = aiTip;
            }

            SetVisible(!hidden);

            if (aiTip == "agro")
            {
                SetAgro(true);
            }
        }

        /// <summary>
        /// Point the unit's renderer at the persona's own art, and anchor it.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the sprite cannot come from the unit definition.</b> Every NPC placement shares
        /// one <c>&lt;unit id='npc'&gt;</c> row, so <c>UnitDefinition.sprite</c> could only ever hold one
        /// picture for all 108 personas. The art belongs to the persona —
        /// <c>UnitNPC.as:101-108</c> resolves <c>"visual" + npcXML.@vis</c> per instance — which is what
        /// <c>NpcDefinition.visualSprite</c> carries, filled by <c>UnitSpriteImporter</c>'s NPC pass.</para>
        ///
        /// <para><b>The anchor is re-applied, not assumed.</b> The spawner already ran
        /// <see cref="UnitSpriteAnchor"/> over the <i>unit</i> sprite; this is a different picture with
        /// the same pivot problem, so it goes through the same helper rather than a bare
        /// <c>renderer.sprite =</c>.</para>
        ///
        /// <para><b>A persona with a clip is handed to <see cref="NpcVisualAnimator"/> instead.</b>
        /// <c>visualSprite</c> is only ever one frame of that clip, so drawing it directly would leave
        /// <c>vis.osn.gotoAndStop("fly")</c> with nothing to move — which is exactly what Calamity
        /// looked like. The animator draws the resting frame itself, at the persona's own scale, so this
        /// path and the animated path cannot disagree about either the picture or the pivot.</para>
        /// </remarks>
        void ApplyPersonaArt(NpcDefinition persona)
        {
            if (_npcRenderer == null || persona == null)
            {
                return;
            }

            if (persona.VisualFrameCount > 0)
            {
                if (_npcAnimator == null)
                {
                    _npcAnimator = _npcRenderer.GetComponent<NpcVisualAnimator>();
                    if (_npcAnimator == null)
                    {
                        _npcAnimator = _npcRenderer.gameObject.AddComponent<NpcVisualAnimator>();
                    }
                }

                _npcAnimator.Initialize(persona, _npcRenderer);
                SyncAnimatorFlight();
                _npcRenderer.enabled = IsVisible;
                return;
            }

            if (persona.visualSprite == null)
            {
                return;
            }

            UnitSpriteAnchor.ApplyTo(_npcRenderer, persona.visualSprite, new Vector2Int(-1, -1));
            _npcRenderer.enabled = IsVisible;
        }

        /// <summary>
        /// Push <c>animFly</c> / <c>isFly</c> to the clip player — AS3 reads both directly out of the
        /// unit, but they live on the controller here and the player is a separate component.
        /// </summary>
        /// <remarks>
        /// Called from every site that writes either flag rather than from a polling tick: <c>isFly</c>
        /// changes on exactly three events (the placement's <c>fly</c>, leaving the ground, and the
        /// landing), and a per-frame copy would be a second, silently-diverging source of truth for
        /// when the clip switches poses.
        /// </remarks>
        void SyncAnimatorFlight()
        {
            if (_npcAnimator == null)
            {
                return;
            }

            _npcAnimator.SetAnimFly(_animFly);
            _npcAnimator.SetFly(_isFly);
        }

        /// <summary>
        /// The visual renderer, handed over by the spawner.
        /// </summary>
        /// <remarks>
        /// Overridden so the persona's art can be re-applied when the renderer arrives <i>after</i> the
        /// persona — the spawner's order is provider, placement, renderer, but a test or a later caller
        /// may do it the other way and would otherwise get a unit with the persona's state and the
        /// wrong picture.
        /// </remarks>
        public override void SetVisualRenderer(SpriteRenderer visualRenderer)
        {
            base.SetVisualRenderer(visualRenderer);
            _npcRenderer = visualRenderer;
            ApplyPersonaArt(_persona);
        }

        // ── Initialization: the persona's weapon ───────────────────────────────────────────────────

        /// <summary>
        /// Equip the persona's weapon once the definition and stats exist.
        /// </summary>
        /// <remarks>
        /// <b>Why here and not only in <c>OnWeaponServicesReady</c>.</b> The spawner hands the weapon
        /// services over <i>before</i> <c>Initialize</c>, so <c>OnWeaponServicesReady</c> fires with
        /// <c>Stats == null</c> — and <c>EnemyController.EquipWeapon</c> captures
        /// <c>Faction</c> into the mount at that moment, which would resolve to <c>Neutral</c> and give
        /// Calamity's bullets the wrong side. <c>Initialize</c> runs after the definition has landed, so
        /// equipping here gets the row's real <c>fraction='100'</c>. Both paths call the same idempotent
        /// helper, so whichever arrives last wins without stacking a second mount.
        /// </remarks>
        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);
            EquipPersonaWeapons();
        }

        /// <summary>Late handover: the container's weapon services arrived after <c>Initialize</c>.</summary>
        protected override void OnWeaponServicesReady()
        {
            EquipPersonaWeapons();
        }

        void EquipPersonaWeapons()
        {
            if (_weaponsEquipped || _stats == null || _equippedWeaponIds.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _equippedWeaponIds.Count; i++)
            {
                EnemyWeaponMount mount = EquipWeapon(_equippedWeaponIds[i]);

                // AS3 `UnitNPC.control()` calls `currentWeapon.attack()` on EVERY frame while a live
                // `celUnit` is in sight (:570-577), and `t_auto` — the semi-auto click debounce — is
                // refreshed by every one of those calls (`Weapon.as:1297-1302`) before `Weapon.step()`
                // gets to decay it (:1226, and `step()` runs after `control()` at `Unit.as:1806-1852`).
                // A semi-auto weapon therefore fires ONCE and is locked out for the rest of the
                // engagement — `rapid=24 > 6` makes the sniper semi-auto, so this is exactly Calamity's
                // one shot. `attack(true)` is the oracle's own answer for a trigger with no click edge
                // (`UnitPlayer.as:1161`, the `sats` turret); an NPC's is the same situation.
                // See WeaponRuntimeState.AutomaticTrigger — a recorded divergence.
                mount?.MarkAutomaticTrigger();
            }

            _weaponsEquipped = true;
        }

        /// <summary>
        /// AS3 <c>celX</c>/<c>celY</c> — the world point every held weapon on this NPC aims at,
        /// maintained once per frame by <see cref="TickCel"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>The oracle has these two fields and the port did not, and that was the aiming
        /// bug.</b> <c>UnitNPC.control()</c> rewrites them every frame — the <c>if(celUnit == null)</c>
        /// block at <c>:528-532</c> and <c>setCel(celUnit)</c> at <c>:561</c> — and
        /// <c>Weapon.actions()</c> aims along <c>atan2(owner.celY - Y, |owner.celX - X| * owner.storona)</c>
        /// (<c>Weapon.as:1074-1086</c>), where <c>Y</c> is the weapon's own hold point, i.e. the
        /// <b>hands</b>.</para>
        ///
        /// <para>The port published an aim target only on a firing tick, and took it from
        /// <c>target.transform.position</c> — the target's <b>feet</b>. Because the angle is measured
        /// from the hands, aiming at the feet tilts the barrel down by the target's whole body height
        /// over the horizontal gap: at Calamity's 573 px it is a few degrees, at close range it is the
        /// difference between the torso and the floor in front of it. <c>Unit.setCel</c> aims at the box
        /// <i>centre</i> (<c>celY = param1.Y - param1.scY / 2</c>), which
        /// <see cref="AimPointOn"/> now produces.</para>
        ///
        /// <para><b>And the idle point is not the feet either.</b> With no target the oracle aims at
        /// <c>(X + storona * 100, Y - 10)</c> — ten pixels above its own feet — so the barrel rests at a
        /// shallow downward angle instead of hanging at whatever the last shot left it at. The port's
        /// fallback was the unit's own facing direction at feet height, which is a different line.</para>
        /// </remarks>
        public Vector2 CelPointWorld { get; private set; }

        /// <summary>
        /// AS3 <c>Unit.setCel</c> (<c>Unit.as:4703-4735</c>) as <c>UnitNPC.control()</c> calls it: the
        /// target's box centre while there is one, the idle point along the facing otherwise. Pushed to
        /// every mounted weapon, which is the oracle's one <c>celX</c> read by both weapons.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="UnitNpcBrain"/> on <b>every</b> frame, agro or not, because an idle
        /// NPC's weapon still follows <c>celX</c> — see <see cref="CelPointWorld"/>.
        /// </remarks>
        /// <param name="target">
        /// AS3 <c>celUnit</c>, or <c>null</c>. A dead target is treated as no target: the oracle's arm
        /// tests <c>celUnit.sost == 1 &amp;&amp; celUnit.hp &gt; 0</c> before it calls <c>setCel</c>, and a
        /// corpse is not something a weapon should track.
        /// </param>
        public void TickCel(UnitController target)
        {
            CelPointWorld = target != null && target.IsAlive ? AimPointOn(target) : IdleAimPoint();
            SetWeaponAimPoint(CelPointWorld);
        }

        /// <summary>
        /// AS3 <c>setCel(param1)</c> — <c>(target.X + target.scX/4 * target.storona, target.Y - target.scY/2)</c>.
        /// </summary>
        /// <remarks>
        /// <b>Both offsets are above the target's feet, which is the whole point.</b> AS3's <c>Y</c> is
        /// down-positive, so <c>Y - scY/2</c> is half a body <i>up</i>; in the port that is
        /// <c>+ height * 0.5</c>. The horizontal nudge uses the <b>target's</b> facing, not this unit's —
        /// a target walking away is aimed at its back-side edge and one walking toward us at its
        /// front-side edge, which is what the oracle does.
        /// </remarks>
        Vector2 AimPointOn(UnitController target)
        {
            UnitDefinition stats = target.Stats;
            float width = stats != null && stats.Width > 0f ? stats.Width : 0.5f;
            float height = stats != null && stats.Height > 0f ? stats.Height : 0.7f;

            Vector3 position = target.transform.position;
            return new Vector2(
                position.x + target.FacingDirection * width * UnitNpcRules.TargetAimWidthFraction,
                position.y + height * UnitNpcRules.TargetAimHeightFraction);
        }

        /// <summary>
        /// AS3 <c>celX = X + storona * 100; celY = Y - 10</c> (<c>UnitNPC.as:530-531</c>) — where the
        /// weapon rests with nothing to shoot.
        /// </summary>
        Vector2 IdleAimPoint()
        {
            const float pixelsPerUnit = 100f;
            return new Vector2(
                transform.position.x + FacingDirection * (UnitNpcRules.IdleAimForwardPixels / pixelsPerUnit),
                FeetWorldY + (UnitNpcRules.IdleAimAboveFeetPixels / pixelsPerUnit));
        }

        /// <summary>
        /// Hold or release every weapon the persona authored. Called by <see cref="UnitNpcBrain"/>.
        /// </summary>
        /// <remarks>
        /// <para>AS3 <c>UnitNPC.control()</c>'s agro arm calls <c>currentWeapon.attack()</c> and
        /// <c>dopWeapon.attack()</c> (<c>:570-577</c>) — both weapons, every frame, while a live target
        /// is in sight. <c>EnemyWeaponMount</c> latches its own <c>t_attack</c>, so pushing the trigger
        /// every frame is what the oracle does and the pacing stays the weapon's job.</para>
        ///
        /// <para><b>The aim point is deliberately NOT set here.</b> It used to be, from
        /// <c>target.transform.position</c>, which is the target's feet — see
        /// <see cref="CelPointWorld"/>. Aim belongs to the per-frame <see cref="TickCel"/>, which runs
        /// before this on the same tick; a second writer here would overwrite the correct point on
        /// exactly the ticks that matter, the firing ones.</para>
        /// </remarks>
        public void SetWeaponTrigger(bool firing)
        {
            int reached = 0;

            for (int i = 0; i < _equippedWeaponIds.Count; i++)
            {
                EnemyWeaponMount mount = GetWeaponMount(_equippedWeaponIds[i]);
                if (mount == null)
                {
                    continue;
                }

                mount.SetFiring(firing);
                reached++;
            }

            // Reported, not acted on — see LastTriggerMountCount. Written on the release push too,
            // because "the release reached nothing" and "the press reached nothing" are the same defect
            // and a counter that only tracked presses would hide half of it.
            LastTriggerMountCount = reached;
        }

        // ── AS3 UnitNPC.command / analiz / control ─────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>UnitNPC.command(param1, param2)</c> (<c>:321-381</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The base call comes first, exactly as the oracle's <c>super.command</c> does</b>
        /// (<c>:324</c>) — so <c>activate</c> and <c>fraction</c> are handled by <c>Unit</c> and the
        /// NPC's ladder only sees what is left.</para>
        ///
        /// <para><b>Only six verbs act immediately; everything else is queued.</b> That is the oracle's
        /// <c>else</c> (<c>:374-380</c>) and it is load-bearing: <c>fly</c> and <c>ai</c> reach the NPC
        /// only through the queue, which is what paces them one per <c>wait</c> expiry. A port that
        /// "helpfully" handled <c>fly</c> inline would start the flight on the same frame as the script
        /// action and drop the <c>wait</c> gate entirely.</para>
        ///
        /// <para><b>Comparison is case-insensitive here, and that is a deliberate divergence.</b> AS3
        /// compares with <c>==</c>, which is case-sensitive; the port normalises, so an authored
        /// <c>Show</c> would act here and queue there. Strictly more permissive, and the shipped data
        /// is uniformly lower case.</para>
        /// </remarks>
        public override bool Command(string verb, string value)
        {
            bool handledByBase = base.Command(verb, value);

            if (string.IsNullOrEmpty(verb))
            {
                return handledByBase;
            }

            switch (UnitNpcRules.Classify(verb))
            {
                case UnitNpcVerbKind.Hide:
                    Hide();
                    return true;

                case UnitNpcVerbKind.Show:
                    Show();
                    return true;

                case UnitNpcVerbKind.OpenEyes:
                    // UnitNPC.as:341-350 — `try { vis.osn.gotoAndStop(2); } catch(err:*) {}`. Frame 2 is
                    // the second frame of the persona's own clip, and the try/catch is why a persona
                    // with a one-frame clip is silent about it rather than an error.
                    _npcAnimator?.GotoAndStopFrame(UnitNpcRules.OpenEyesFrame);
                    return true;

                case UnitNpcVerbKind.Sign:
                    Sign();
                    return true;

                case UnitNpcVerbKind.ReplicVse:
                    // UnitNPC.as:365-369 — `t_replic = 0; replic("vse")`. No voice-line table; see the
                    // class summary. `t_replic` has no port counterpart to reset either.
                    return true;

                case UnitNpcVerbKind.Rep:
                    // UnitNPC.as:370-373 — `this.targNPC.rep = int(param2)`. AS3's `int("junk")` is 0,
                    // which is also the port's `TryParse` failure value, so the two agree.
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rep))
                    {
                        _rep = rep;
                    }
                    else
                    {
                        _rep = 0;
                    }

                    return true;

                case UnitNpcVerbKind.Queue:
                    _que.Add(new QueuedNpcVerb(verb, value));
                    return true;

                default:
                    return handledByBase;
            }
        }

        /// <summary>
        /// AS3 <c>UnitNPC.control()</c> (<c>:462-609</c>) — the NPC's whole per-frame life.
        /// </summary>
        /// <remarks>
        /// <para><b>Why <c>TickControl</c> and not <c>StepUnit</c>.</b> A unit with a
        /// <c>TilePhysicsController</c> — which is every spawned unit under the live
        /// <c>PfeDebugSettings.UnitMotor</c> — is owned by <c>TilePhysicsController.StepMotor</c> and
        /// never reaches <c>StepUnit</c>. <c>TickControl</c> is the seam both drivers call, and it sits
        /// between gravity and the integration on the motor-less path and above both on the motor path,
        /// which is where the oracle puts <c>control()</c>.</para>
        ///
        /// <para><b>The velocity writes go through <c>As3VelocityPixelsPerFrame</c>, not through
        /// <c>transform</c>.</b> That property resolves to whichever component actually owns the
        /// velocity — the motor's <c>dx</c>/<c>dy</c> on the live path, this class's <c>_velocity</c>
        /// otherwise — so the same three additions land in the right place on both drivers. AS3's
        /// <c>dx += …</c> is literally this.</para>
        /// </remarks>
        public override void TickControl()
        {
            base.TickControl();

            // UnitNPC.as:466-469 — the interaction lockout.
            if (_tRef > 0)
            {
                _tRef--;
            }

            // UnitNPC.as:493-505 — the queue's clock. Note the order: `wait` counts down, `wait == 1`
            // clears the flight one frame before the queue may drain, and the drain is gated on
            // `wait <= 0`. Reproducing it any other way changes when a `fly` is followed by the next
            // verb.
            if (_wait > 0)
            {
                _wait--;
            }

            if (_wait == 1)
            {
                _dey = string.Empty;
            }

            if (_que.Count > 0 && _wait <= 0)
            {
                QueuedNpcVerb next = _que[0];
                _que.RemoveAt(0);
                Analiz(next);
            }

            // UnitNPC.as:506-513 — the reveal fade. AS3 lerps `vis.alpha`; the port has no MovieClip
            // alpha, so the equivalent is the SpriteRenderer's colour alpha.
            if (_revealAlpha < 1f)
            {
                _revealAlpha = Mathf.Min(1f, _revealAlpha + UnitNpcRules.RevealAlphaPerFrame);
                ApplyRevealAlpha();
            }

            // UnitNPC.as:514-520 — an NPC told to fly becomes airborne as soon as it leaves the ground.
            if (_aiTip == "fly" && !IsGrounded)
            {
                _isFly = true;
                SyncAnimatorFlight();
            }

            // UnitNPC.as:521-527 — the hover bob. AS3 also adds `floatX`, which it declares at :45 and
            // never writes; adding a zero is what the oracle does, so the port does not invent one.
            if (_isFly)
            {
                _tFloat += UnitNpcRules.FlyFloatPhaseStep;
                Vector2 bobbed = As3VelocityPixelsPerFrame;
                bobbed.y += UnitNpcRules.FloatOffset(_tFloat);
                As3VelocityPixelsPerFrame = bobbed;
            }

            // UnitNPC.as:533-556 — the flight itself.
            if (_dey == "fly")
            {
                TickFlight();
            }
        }

        /// <summary>
        /// The <c>dey == "fly"</c> arm of <c>UnitNPC.control()</c> (<c>:533-556</c>), with the oracle's
        /// squared-distance tests preserved verbatim.
        /// </summary>
        void TickFlight()
        {
            Vector2 current = RoomLocalPixels;
            float dvigX = _cx - current.x;
            float dvigY = _cy - current.y;

            if (UnitNpcRules.ShouldBrake(dvigX, dvigY))
            {
                // AS3 `dx *= 0.85; dy *= 0.85;` then the arrival test on the same frame's distances.
                Vector2 braked = As3VelocityPixelsPerFrame * UnitNpcRules.FlyBrakePerFrame;
                As3VelocityPixelsPerFrame = braked;

                if (UnitNpcRules.HasArrived(dvigX, dvigY))
                {
                    _dey = string.Empty;
                    As3VelocityPixelsPerFrame = Vector2.zero;
                    _wait = 0;
                }

                return;
            }

            float acceleration = _stats != null ? _stats.Acceleration : 0f;
            Vector2 thrust = UnitNpcRules.SteeringAcceleration(dvigX, dvigY, acceleration);

            Vector2 steered = As3VelocityPixelsPerFrame;
            steered.x += thrust.x;
            steered.y += thrust.y;
            As3VelocityPixelsPerFrame = steered;
        }

        /// <summary>
        /// AS3 <c>UnitNPC.analiz(param1)</c> (<c>:383-442</c>) — the one queued verb this frame.
        /// </summary>
        /// <remarks>
        /// <b>An unrecognised verb is consumed and dropped, and that is the oracle.</b> AS3's ladder has
        /// no <c>else</c>: a verb outside its seven cases leaves through the end of the function having
        /// done nothing. The port logs once per distinct verb so a mis-spelling is visible without
        /// pretending the original reported it.
        /// </remarks>
        void Analiz(QueuedNpcVerb verb)
        {
            string command = verb.Command != null ? verb.Command.Trim().ToLowerInvariant() : string.Empty;

            switch (command)
            {
                case "check":
                    // UnitNPC.as:391-397 — `targNPC.check()` with the default (non-running) argument,
                    // i.e. "does a dialogue match?" without running it.
                    break;

                case "ai":
                    // UnitNPC.as:398-401 — `aiTip = param1.val`.
                    SetAiTip(verb.Value);
                    break;

                case "fly":
                    // UnitNPC.as:402-410.
                    BeginFlight(verb.Value);
                    break;

                case "rem":
                    // UnitNPC.as:411-414 — `this.hide()`.
                    Hide();
                    break;

                case "turn":
                    // UnitNPC.as:415-430.
                    ApplyTurn(verb.Value);
                    break;

                case "tell":
                    // UnitNPC.as:386-390 — `t_replic = 0; replic(param1.val)`. No voice-line table; the
                    // branch is recognised so it is not reported as unknown.
                    break;

                case "mater":
                    // UnitNPC.as:431-441 — AS3's palette-swap flag, which the port does not model.
                    break;

                default:
                    if (_reportedUnwiredVerbs.Add(command))
                    {
                        Debug.LogWarning(
                            $"[UnitNPCController] '{name}' consumed an unknown queued verb " +
                            $"'{command}' ({verb}). AS3's analiz has no else branch either, so it did " +
                            $"nothing in the oracle too — but an authored verb that never acts is " +
                            $"usually a spelling difference rather than intent. " +
                            $"Known: {string.Join(", ", UnitNpcRules.AnalizableVerbs)}.");
                    }

                    break;
            }
        }

        /// <summary>
        /// The tail of AS3 <c>UnitNPC.control()</c>'s agro arm (<c>:594-607</c>) — the descent and the
        /// landing. Called once per frame by <see cref="UnitNpcBrain"/> while <c>aiTip == "agro"</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this lives on the controller and not the brain.</b> Both halves write the
        /// unit's velocity, and <c>As3VelocityPixelsPerFrame</c> is <c>protected</c> on
        /// <c>UnitController</c> — the brain is a separate <c>MonoBehaviour</c> and can only read the
        /// public view. The brain owns the <i>decision</i> (it is the only thing that knows whether a
        /// target is in sight) and passes the one bit this needs.</para>
        ///
        /// <para><b>What was missing, and why it is the reported symptom.</b> AS3's agro arm is three
        /// branches: fire at a live <c>celUnit</c>, look for one, or — the <c>else</c> — sink
        /// (<c>if(isFly &amp;&amp; dy &lt; 5) dy += 1;</c>). Then, unconditionally, a floor-contact test:
        /// <c>if(turnY &lt; 0) { isFly = false; turnY = 0; aiTip = ""; targNPC.landing(); inter.active =
        /// true; }</c>. The port had neither half, so a Calamity who had killed every raider stayed
        /// airborne with <c>agro</c> set for ever — he never came down, never ran
        /// <c>NPC.landing()</c>, and therefore never set <c>rep = 1</c>, which is the flag that decides
        /// whether he is hidden the next time the room is built.</para>
        ///
        /// <para><b><c>turnY &lt; 0</c> is floor contact, and the port already has that signal.</b>
        /// <c>turnY</c> is written in exactly four places in the whole oracle, and every one is a
        /// collision response: <c>-1</c> when the unit is pushed past the room's bottom limit
        /// (<c>Unit.as:2262</c>, <c>:2279</c>, <c>:2376</c>, <c>:2397</c>), <c>+1</c> at the top. So
        /// <c>turnY &lt; 0</c> means "I just touched the floor", which is what
        /// <see cref="UnitController.IsGrounded"/> answers here — the same event, read from the side
        /// that actually models it. The alternative the port had chosen was to leave the landing
        /// unported because <c>turnY</c> has no field, which turned a missing <i>alias</i> into a
        /// missing <i>behaviour</i>.</para>
        ///
        /// <para><b>The descent is the one line here that must NOT be transcribed verbatim.</b> AS3
        /// writes <c>if(isFly &amp;&amp; dy &lt; 5) dy += 1;</c> in a space where <b>+Y is down</b>. The port's
        /// velocity is Unity-signed — <c>UnitFallPhysics.FallSpeed</c> and
        /// <c>TilePhysicsController</c>'s own gravity both make Y <i>negative</i> to fall — so a
        /// literal <c>+=</c> here made the sink a <i>climb</i>: the unit accelerated upward until the
        /// flight brake settled it at about 3.3 px/frame, reached the room's ceiling, and never touched
        /// the floor. <c>IsGrounded</c> therefore never fired, <see cref="Land"/> never ran, and
        /// <c>rep</c> was never written — the reported "he should come down after the enemies are
        /// dead". Both the guard and the increment are mirrored: AS3's <c>dy &lt; 5</c> becomes
        /// <c>dy &gt; -5</c>, and <c>dy += 1</c> becomes <c>dy -= 1</c>.</para>
        /// </remarks>
        /// <param name="hasTarget">
        /// AS3 <c>celUnit != null</c> — whether the brain currently has a live target. Only the
        /// <c>else</c> (no target) arm sinks.
        /// </param>
        public void TickAgroArm(bool hasTarget)
        {
            // :594-599 — the `else` arm. A flyer with nothing to shoot drifts down.
            if (_isFly && !IsGrounded && !hasTarget)
            {
                Vector2 sinking = As3VelocityPixelsPerFrame;

                // AS3 `dy < 5` in a down-positive space → `dy > -5` here. The clamp exists so a unit
                // already falling faster than 5 px/frame is not accelerated further by its own idle.
                if (sinking.y > -UnitNpcRules.AgroDescentClamp)
                {
                    sinking.y -= UnitNpcRules.AgroDescentPerFrame;
                    As3VelocityPixelsPerFrame = sinking;
                }
            }

            // :596-607 — `if(turnY < 0)`. Touching the floor while flying ends the fight.
            //
            // ── `turnY < 0` is the ROOM'S BOTTOM, not "standing on something" ─────────────────────
            //
            // For a flyer the oracle's vertical branch never consults tiles at all: `if(levit ||
            // plav || isFly) { Y += ...; if(Y > loc.limY) { Y = limY - 1; dy = 0; turnY = -1; } }`
            // (`Unit.as:2255-2265`). The only floor a flying unit can hit is `loc.limY` — the bottom
            // line of the room. Every other `turnY = -1` writer is in the *non*-flyer arm.
            //
            // The port substituted `IsGrounded`, which answers a different question: "am I resting on
            // a tile or a shelf prop". In `room_12_1` that is true on the ledge at row 16 — six tiles
            // above the floor — so the agro arm's first tick after `ai agro` landed him in mid-air,
            // which clears `aiTip` and ends the fight before it starts. That is the reported shape:
            // the `stay` pose (which only `Land()` can select) on a unit standing on nothing, and no
            // shots. A position test cannot be fooled by whatever support happens to be under him.
            if (_isFly && IsAtRoomBottom())
            {
                Land();
            }
        }

        /// <summary>
        /// Whether the feet have reached the room's bottom line — AS3 <c>Y &gt; loc.limY</c> for a flyer
        /// (<c>Unit.as:2257</c>), which is the only thing that can set <c>turnY = -1</c> while flying.
        /// </summary>
        /// <remarks>
        /// <para><b>One tile of tolerance, deliberately.</b> AS3 clamps the feet to <c>limY - 1</c>, i.e.
        /// room-local <c>y = 1</c> in this port's mirrored space (<see cref="As3PixelsToRoomLocal"/>
        /// maps <c>limY</c> to 0). A port unit that descends under gravity is stopped by the floor
        /// <i>tile</i> instead of passing through it, so it comes to rest at the top of the bottom
        /// walkable row — up to one tile higher than the oracle's clamp. Anything above that is not
        /// the room bottom and must not end the fight.</para>
        ///
        /// <para><b>A missing room falls back to <see cref="UnitController.IsGrounded"/>.</b> That is
        /// the old, weaker signal, and it is the honest choice: a unit with no room has no
        /// <c>limY</c> to compare against, and refusing to land at all would strand it airborne for
        /// ever. The live path always has a room — the spawner holds it.</para>
        /// </remarks>
        bool IsAtRoomBottom()
        {
            if (_room == null)
            {
                return IsGrounded;
            }

            return RoomLocalPixels.y <= WorldConstants.TILE_SIZE;
        }

        /// <summary>
        /// AS3 <c>UnitNPC.control()</c>'s landing block (<c>:596-607</c>) —
        /// <c>isFly = false; turnY = 0; aiTip = ""; targNPC.landing(); inter.active = true;</c>.
        /// </summary>
        /// <remarks>
        /// <para><b><c>targNPC.landing()</c> is the whole point for Calamity</b>:
        /// <c>NPC.landing()</c> (<c>NPC.as:197-203</c>) is <c>if(id == "calam") rep = 1;</c> — and
        /// <c>rep</c> is what <c>NPC.init()</c> reads to decide whether he is hidden
        /// (<c>NPC.as:110-127</c>). A Calamity who fights and lands is a Calamity who has "already done
        /// the surf story"; one who never lands is one the game will re-run the whole cutscene
        /// for.</para>
        ///
        /// <para><b><c>inter.active = true</c> has no port counterpart, deliberately.</b> The port's
        /// interaction gate is <c>CanInteract</c> = <see cref="IsVisible"/> &amp;&amp; <c>IsAlive</c>, and
        /// AS3's <c>inter.active</c> is re-asserted here only because the <c>agro</c> arm can leave a
        /// unit mid-fight with the button removed. There is no second flag to write — a second flag
        /// would be a second thing that can disagree with the sprite.</para>
        ///
        /// <para><b>The <c>ico.sign</c> re-assert is skipped, and named rather than faked.</b> AS3 ends
        /// with <c>if(this.ico.sign) ico.sign.visible = World.w.helpMess</c>. The port has no
        /// objective-icon renderer and no reachable help-message setting from here (both already
        /// documented on <see cref="ObjectiveSignVisible"/>), so writing a value nothing reads would
        /// create the decoy the project's own rules warn about.</para>
        /// </remarks>
        public void Land()
        {
            _isFly = false;
            _aiTip = string.Empty;
            _npcBubbleRefreshTicks = 0;

            // ── The landing ends the flight ────────────────────────────────────────────────────────
            //
            // AS3 never needs this line, and that is why the port does. In the oracle the landing is
            // only reachable from the agro arm, which is only reachable once `aiTip == "agro"`, which
            // is only reachable once the queue drained, which needs `wait <= 0` — and `wait` is 1000
            // (`UnitNPC.as:406`) until the flight's own arrival test sets it to 0. So by the time AS3
            // can land, `dey` has already ended.
            //
            // The port can land while `dey == "fly"` is still live (any support above the floor used to
            // do it; see TickAgroArm). `TickFlight` runs on `dey`, not on `isFly`, so it kept steering
            // the *landed* unit toward a target sixteen tiles up — `_isFly` false, so the `stay` pose,
            // gravity fighting the steering, and the unit parked in the air above the room. Clearing
            // the flight state here makes that state unreachable rather than unlikely.
            //
            // `_wait = 0` is not a divergence: in every state the oracle can reach, it is already 0 by
            // the time `landing()` runs. Here it only shortens the 1000-frame timeout the abandoned
            // flight would otherwise leave in front of the rest of the queue.
            _dey = string.Empty;
            _wait = 0;
            As3VelocityPixelsPerFrame = Vector2.zero;

            // AS3 :598 writes `isFly` and `animate()`'s fly branch reads it on the very next display
            // frame — so the pose changes from `fly` to `stay` as part of the landing, not as something
            // a later tick has to notice.
            SyncAnimatorFlight();

            // `aiTip = ""` clears the AI on the brain's side too, which is what releases the trigger:
            // EnemyWeaponMount latches `_firing` until told otherwise, so clearing only the string
            // would leave him shooting at nothing while he walks away.
            SetAgro(false);

            int? landingRep = UnitNpcRules.LandingRep(_npcId);
            if (landingRep.HasValue)
            {
                _rep = landingRep.Value;
            }

            // Where he came down, once. A landing ends the fight, so a landing in the wrong place is
            // indistinguishable from "he never acquired a target" from the player's chair — and the
            // two have completely different causes. One line names the height, which is the whole
            // question: the room bottom is ~0 px and the surf perch is ~640.
            bool hadTarget = TryGetComponent(out UnitNpcBrain brain) && brain.CurrentTarget != null;
            Debug.Log(
                $"[UnitNPCController] '{name}' landed at room-local y={RoomLocalPixels.y:0}px " +
                $"(room bottom is 0; a flyer must not land above it), rep={_rep}, " +
                $"target was {(hadTarget ? "set" : "null")}. " +
                "A landing clears aiTip and ends the agro arm.",
                this);
        }

        /// <summary>
        /// AS3 <c>aiTip = value</c>, plus the brain hand-off the port needs.
        /// </summary>
        void SetAiTip(string value)
        {
            _aiTip = value ?? string.Empty;

            // The bubble's visibility is a function of `aiTip` (`agro` hides it), so a change here has
            // to invalidate the poll rather than wait out the refresh interval.
            _npcBubbleRefreshTicks = 0;

            SetAgro(string.Equals(_aiTip, "agro", StringComparison.OrdinalIgnoreCase));
        }

        void SetAgro(bool agro)
        {
            if (TryGetComponent(out UnitNpcBrain brain))
            {
                brain.SetAgro(agro);
            }
        }

        /// <summary>
        /// AS3 <c>analiz</c>'s <c>fly</c> arm (<c>:402-410</c>).
        /// </summary>
        /// <remarks>
        /// <b>The target is an AS3 pixel, and the port's pixel space is mirrored on Y.</b>
        /// <c>UnitNpcRules.ParseFlyTarget</c> produces the oracle's own <c>(tileX + 1) * 40</c> /
        /// <c>tileY * 40</c>; the conversion into this port's room-local pixels is the same mirror
        /// <c>RoomPopulator</c> applies to a placement, and it is done here rather than inside the rule
        /// because it needs the room. See <see cref="SetRoom"/>.
        /// </remarks>
        void BeginFlight(string value)
        {
            Vector2 as3Pixels = UnitNpcRules.ParseFlyTarget(
                value, WorldConstants.TILE_SIZE, WorldConstants.TILE_SIZE);

            Vector2 roomLocal = As3PixelsToRoomLocal(as3Pixels);
            _cx = roomLocal.x;
            _cy = roomLocal.y;
            _dey = "fly";
            _wait = UnitNpcRules.FlyWaitFrames;
        }

        /// <summary>
        /// AS3 <c>analiz</c>'s <c>turn</c> arm (<c>:415-430</c>) — <c>storona</c> and <c>setVisPos()</c>.
        /// </summary>
        void ApplyTurn(string value)
        {
            int facing = UnitNpcRules.TurnFacing(value);

            // AS3: `if(val == "0") storona = -storona;` — flip whatever it currently is.
            SetFacing(facing == 0 ? -FacingDirection : facing);
        }

        // ── hide / show / sign ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>UnitNPC.hide()</c> (<c>:283-295</c>): clear the interaction, drop the visual and hide
        /// the held weapon.
        /// </summary>
        /// <remarks>
        /// <b>The interaction half is not decoration.</b> <c>inter.active = false</c> is what makes a
        /// hidden NPC unreachable, and it is enforced at the source in AS3: <c>Unit.onCursor</c>
        /// (<c>Unit.as:1845</c>) is gated on <c>isVis</c>, so an invisible unit is not even a cursor
        /// target. The port's equivalent is <see cref="CanInteract"/>, which reads the same flag.
        /// </remarks>
        public void Hide()
        {
            SetVisible(false);
            ObjectiveSignVisible = false;
            _npcBubbleRefreshTicks = 0;
        }

        /// <summary>
        /// AS3 <c>UnitNPC.command("show")</c>'s arm (<c>:329-340</c>): reveal, restart the fade from
        /// zero, and clear the persona's hidden flag.
        /// </summary>
        public void Show()
        {
            SetVisible(true);
            _revealAlpha = 0f;
            ApplyRevealAlpha();
            _npcBubbleRefreshTicks = 0;

            // AS3 :339 — `this.targNPC.hidden = false`. The port keeps the persona's hidden state in
            // the unit's visibility (see ApplyPersonaInit), so there is deliberately no second copy
            // here to clear: a second flag would be a second thing that can disagree with the sprite.
        }

        /// <summary>
        /// AS3 <c>UnitNPC.command("sign")</c>'s arm (<c>:351-364</c>) — resolve the objective icon's
        /// visibility.
        /// </summary>
        /// <remarks>
        /// The oracle's two inputs are <c>aiTip == "fly"</c> (an NPC on his way somewhere does not
        /// advertise himself) and <c>World.w.helpMess</c>, the player's help-message setting. The port
        /// has no settings object reachable from here, so the second term is taken as <c>true</c> — the
        /// oracle's default — and the gap is named on
        /// <see cref="ObjectiveSignVisible"/> rather than papered over.
        ///
        /// <para><b>The <c>ico</c> node's existence is not tracked separately.</b> AS3 creates the icon
        /// object on the first <c>sign</c> and then toggles it; the port has one boolean, whose default
        /// <c>false</c> is already the value an unissued <c>sign</c> produces (no icon). A second
        /// "has it ever been issued" flag would be written and read by nothing — the decoy the project
        /// rule warns about — so it is deliberately absent.</para>
        /// </remarks>
        void Sign()
        {
            ObjectiveSignVisible = !string.Equals(_aiTip, "fly", StringComparison.OrdinalIgnoreCase);
        }

        // ── The overhead bubble — AS3's `ico` (UnitNPC.as:157-159, :578-588, NPC.as:474-490) ──────

        /// <summary>Frames in <c>visNPCIco</c> (<c>DefineSprite_4844</c>). See <see cref="_npcBubble"/>.</summary>
        const int NpcBubbleFrameCount = 31;

        /// <summary>The transparent frame — AS3's <c>icoFrame</c> default, i.e. "no icon".</summary>
        const int NpcBubbleBlankFrame = 1;

        /// <summary>SWF frame label <c>dial1</c> — a bubble with text lines: ordinary new dialogue.</summary>
        const int NpcBubbleDial1Frame = 30;

        /// <summary>SWF frame label <c>dial2</c> — a bubble with three exclamation marks: important dialogue.</summary>
        const int NpcBubbleDial2Frame = 31;

        /// <summary>
        /// Where the icon's <b>canvas centre</b> sits above the unit's feet, in pixels — see
        /// <see cref="_npcBubble"/> for the derivation from <c>ico.y = -140</c> and the 118x142 canvas.
        /// </summary>
        const float NpcBubbleHeightPixels = 69f;

        /// <summary>How often <see cref="RefreshBubble"/> re-evaluates the dialogue gate. See <see cref="_npcBubbleRefreshTicks"/>.</summary>
        const int NpcBubbleRefreshIntervalTicks = 30;

        /// <summary>
        /// The bubble frames, loaded once and shared — <c>PipBuckCompositionDefinition.npcPortraits</c>
        /// (sprite 4844, 31 frames).
        /// </summary>
        /// <remarks>
        /// <c>Resources.Load</c> rather than an injected reference, matching every other consumer of this
        /// definition (<c>PipBuckBootstrapper</c>, <c>HudBootstrapper</c>): the asset is a
        /// <c>ScriptableObject</c> under <c>Resources/</c> and the unit is built by <c>AddComponent</c>,
        /// which VContainer never sees, so there is no injection point to use. Unity caches the load, and
        /// the <c>Lazy</c> keeps a per-NPC field from being a second reference to the same object.
        /// </remarks>
        static readonly Lazy<Sprite[]> NpcBubbleFrames = new Lazy<Sprite[]>(() =>
        {
            PipBuckCompositionDefinition composition =
                Resources.Load<PipBuckCompositionDefinition>("UI/PipBuckComposition");
            return composition != null ? composition.npcPortraits : Array.Empty<Sprite>();
        });

        /// <summary>
        /// AS3 <c>this.ico</c>'s per-frame upkeep. Called once per tick by <see cref="UnitNpcBrain"/>,
        /// whether or not the NPC is <c>agro</c>.
        /// </summary>
        public void TickBubble()
        {
            if (_npcBubble == null)
            {
                EnsureBubble();
            }

            if (_npcBubble == null)
            {
                return;
            }

            if (_npcBubbleRefreshTicks > 0)
            {
                _npcBubbleRefreshTicks--;
                return;
            }

            _npcBubbleRefreshTicks = NpcBubbleRefreshIntervalTicks;
            RefreshBubble();
        }

        /// <summary>
        /// Build the bubble's GameObject, parented to the unit so it mirrors and hides with the body —
        /// AS3's <c>vis.addChild(this.ico)</c>.
        /// </summary>
        /// <remarks>
        /// <b>Parented to <c>transform</c>, not to the visual renderer's GameObject, and that is the
        /// faithful choice.</b> The unit mirrors by writing <c>transform.localScale.x = storona</c>
        /// (<c>UnitController.ApplyFacingToTransform</c>), which is the port's <c>vis.scaleX</c> — so a
        /// child of the unit inherits exactly the flip the oracle's <c>ico</c> inherits. The icon's own
        /// local X is 0, so the mirror moves nothing but the art.
        /// </remarks>
        void EnsureBubble()
        {
            if (_persona == null)
            {
                return;
            }

            Sprite[] frames = NpcBubbleFrames.Value;
            if (frames == null || frames.Length < NpcBubbleFrameCount)
            {
                if (!_reportedNoBubbleFrames)
                {
                    _reportedNoBubbleFrames = true;
                    Debug.LogWarning(
                        $"[UnitNPCController] '{name}' cannot draw its overhead icon: " +
                        $"PipBuckComposition.npcPortraits is missing or short " +
                        $"(expected {NpcBubbleFrameCount}, got {(frames != null ? frames.Length : 0)}). " +
                        "Re-run PFE/Art/Import Interface Assets.");
                }

                return;
            }

            var bubbleGo = new GameObject("NpcIcon");
            bubbleGo.transform.SetParent(transform, false);
            bubbleGo.transform.localPosition = new Vector3(0f, NpcBubbleHeightPixels / 100f, 0f);

            _npcBubble = bubbleGo.AddComponent<SpriteRenderer>();
            _npcBubble.enabled = false;

            // The icon fades in with the body: AS3's reveal lerps `vis.alpha`, and `ico` is a child of
            // `vis`, so it is inside that fade rather than alongside it.
            Color colour = _npcBubble.color;
            colour.a = _revealAlpha;
            _npcBubble.color = colour;
        }

        /// <summary>
        /// Choose the bubble's frame and visibility — <c>NPC.setIco</c> plus the two
        /// <c>ico.visible</c> writes in the agro arm (<c>UnitNPC.as:578-588</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The three states, in the oracle's order.</b> <c>setStatus(n &gt; 0)</c> → label
        /// <c>dial</c>+n, which is the "I have something to say" bubble; <c>setStatus(0)</c> →
        /// <c>icoFrame</c>, the persona's resting icon (<c>&lt;npc ico='10'/&gt;</c> for Calamity);
        /// and frame 1, which is blank, for a persona that authors no <c>ico</c> at all. Separately, the
        /// agro arm hides the icon outright while the NPC is fighting.</para>
        ///
        /// <para><b><c>setStatus</c>'s <c>imp</c> flag picks between the two bubbles</b>
        /// (<c>NPC.as:387-394</c>: <c>if(_loc2_.@imp.length()) setStatus(2); else setStatus(1);</c>),
        /// which is <c>NpcDialogueEntry.isImportant</c>.</para>
        /// </remarks>
        void RefreshBubble()
        {
            if (_npcBubble == null)
            {
                return;
            }

            if (_npcRenderer != null)
            {
                // Just in front of the unit's own body, on the same sorting layer. The spawner orders
                // units by depth (`-floor(y / tile)`), so borrowing the body's order plus one keeps the
                // bubble attached to its own NPC instead of sinking behind a nearer one.
                _npcBubble.sortingLayerID = _npcRenderer.sortingLayerID;
                _npcBubble.sortingOrder = _npcRenderer.sortingOrder + 1;
            }

            int frame = ResolveBubbleFrame();
            bool draw = IsVisible
                        && frame > NpcBubbleBlankFrame
                        // UnitNPC.as:578 and :583 — `this.ico.visible = false` in both arms that have a
                        // target. A fighting NPC does not advertise a conversation.
                        && !string.Equals(_aiTip, "agro", StringComparison.OrdinalIgnoreCase);

            if (draw)
            {
                _npcBubble.sprite = NpcBubbleFrames.Value[frame - 1];
            }

            _npcBubble.enabled = draw;
        }

        /// <summary>
        /// The <c>visNPCIco</c> frame this NPC should be showing, 1-based.
        /// </summary>
        int ResolveBubbleFrame()
        {
            if (_persona == null)
            {
                return NpcBubbleBlankFrame;
            }

            // `icoFrame` — `<npc ico='10'/>`. Absent means 0 here, and the oracle's own default is 1.
            int resting = _persona.iconIndex > NpcBubbleBlankFrame && _persona.iconIndex <= NpcBubbleFrameCount
                ? _persona.iconIndex
                : NpcBubbleBlankFrame;

            if (!TryFindPendingDialogue(out bool important))
            {
                return resting;
            }

            return important ? NpcBubbleDial2Frame : NpcBubbleDial1Frame;
        }

        /// <summary>
        /// Whether the persona has a dialogue that matches right now — the port's <c>NPC.check()</c>.
        /// </summary>
        /// <remarks>
        /// The same gate the interaction ladder uses (<see cref="BuildActivationInput"/>), so the bubble
        /// and the "talk to me" prompt can never disagree about whether there is something to hear.
        /// </remarks>
        bool TryFindPendingDialogue(out bool important)
        {
            important = false;

            if (_persona == null)
            {
                return false;
            }

            NpcDialogueFacts facts = BuildDialogueFacts();
            if (!NpcDialogueGate.TryFindGreeting(_persona, facts, out string dialogueId))
            {
                return false;
            }

            NpcDialogueEntry entry = FindDialogueEntry(dialogueId);
            important = entry != null && entry.isImportant;
            return true;
        }

        void ApplyRevealAlpha()
        {
            if (_npcRenderer == null)
            {
                return;
            }

            Color colour = _npcRenderer.color;
            colour.a = _revealAlpha;
            _npcRenderer.color = colour;

            // AS3's `ico` is a child of `vis`, so the reveal fade covers the bubble as well as the body.
            // Leaving it out would pop the icon in at full opacity over a body that is still fading.
            if (_npcBubble != null)
            {
                Color bubble = _npcBubble.color;
                bubble.a = _revealAlpha;
                _npcBubble.color = bubble;
            }
        }

        // ── World interaction ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>NPC.setStatus(0)</c>'s verb (<c>NPC.as:415-464</c>), resolved through
        /// <see cref="NpcActivation.InteractionVerb"/>.
        /// </summary>
        /// <remarks>
        /// The persona's <c>ua2</c> wins over everything (<c>:415-418</c>), which is why Calamity —
        /// <c>ua1='dial' ua2='dial'</c> — shows <c>dial</c> even though his <c>inter</c> is
        /// <c>travel</c>. The <c>travel</c> interaction is what the <i>branch ladder</i> does, not what
        /// the prompt says.
        /// </remarks>
        public string ActionText
        {
            get
            {
                if (_persona == null)
                {
                    return "dial";
                }

                NpcActivationInput input = BuildActivationInput(out _, out _);
                return NpcActivation.InteractionVerb(input, _persona.userAction2);
            }
        }

        /// <summary>
        /// Whether the player may interact. AS3's gate is <c>Unit.onCursor</c>'s <c>isVis</c>
        /// (<c>Unit.as:1845</c>) plus <c>inter.active</c>; a hidden NPC is not reachable at all.
        /// </summary>
        public bool CanInteract(GameObject user) => IsVisible && IsAlive;

        /// <summary>
        /// AS3 <c>UnitNPC.npcFun()</c> (<c>:297-319</c>) — the interaction handler — through
        /// <c>NPC.activate()</c>'s ladder (<c>NPC.as:205-248</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The <c>travel</c> branch is the reported "move the player to the base camp".</b>
        /// <c>NPC.as:211-216</c> with <c>npcInter == "travel"</c> runs <c>grantTravel + openMap</c>,
        /// which the port already has as <c>CampaignManager.ActivateNpc</c> — it was written and tested
        /// with the note that "the NPC system itself is unported — nothing walks up to an NPC and calls
        /// this yet". This is that caller.</para>
        ///
        /// <para><b>A matched dialogue pre-empts it, and for Calamity it does.</b> His persona carries
        /// <c>dialCalam2</c> with no gate at all, whose script is <c>passed</c> + <c>openland rbl</c> —
        /// the actions that unlock the camp on the travel map. So the first conversation runs the
        /// dialogue and unlocks the land; the second reaches the <c>travel</c> branch and grants the map.
        /// Running only the travel branch would leave the camp locked and the map empty, which is the
        /// "nothing happens when I talk to him" shape.</para>
        ///
        /// <para><b>The turn-to-face and the lockout come first</b> (<c>:299-314</c>): a re-entrant
        /// activation within 16 frames is dropped, and a non-<c>noturn</c> NPC faces the player before it
        /// speaks.</para>
        /// </remarks>
        public void Interact(GameObject user)
        {
            if (!CanInteract(user))
            {
                return;
            }

            // UnitNPC.as:299-303 — `if(this.zanyato || this.t_ref > 0) return; this.t_ref = 16;`.
            if (_tRef > 0)
            {
                return;
            }

            _tRef = UnitNpcRules.ReferenceCooldownFrames;

            // UnitNPC.as:304-314 — face the player, unless the persona is marked `noturn`.
            if (_persona == null || !_persona.noTurn)
            {
                if (user != null)
                {
                    SetFacing(user.transform.position.x >= transform.position.x ? 1 : -1);
                }
            }

            if (_persona == null)
            {
                ReportUnwiredActivation("(no persona)");
                return;
            }

            NpcActivationInput input = BuildActivationInput(out NpcDialogueEntry entry, out string dialogueId);
            NpcActivationKind kind = NpcActivation.Resolve(input);

            if (kind == NpcActivationKind.Dialogue && entry != null)
            {
                RunGreeting(entry, dialogueId);
                return;
            }

            if (kind == NpcActivationKind.Travel)
            {
                CampaignManager campaign = CampaignManager.Current;
                if (campaign == null)
                {
                    Debug.LogWarning(
                        "[UnitNPCController] no CampaignManager.Current, so this NPC's travel grant " +
                        "cannot open the world map.");
                    return;
                }

                campaign.ActivateNpc(_persona.interactionType);
                return;
            }

            // NPC.as:236-239 — `else if(this.vendor) { this.pip(4); }`. `pip` sets the three device
            // fields and opens page 4; the campaign publishes and the device's subscriber opens.
            if (kind == NpcActivationKind.Trade)
            {
                CampaignManager campaign = CampaignManager.Current;
                if (campaign == null)
                {
                    Debug.LogWarning(
                        "[UnitNPCController] no CampaignManager.Current, so this trader cannot open " +
                        "the trade page.");
                    return;
                }

                campaign.RequestTrade(_persona.npcId, _persona.vendorId, _persona.interactionType);
                return;
            }

            // NPC.as:221-231 — `adoc` with `rep <= 1` runs `this.repair()`; with `rep > 1` it opens the
            // clinic page instead. The repair half is the ladder below; the clinic half is page 6.
            if (kind == NpcActivationKind.AutoDocRepair)
            {
                RunAutoDocRepair();
                return;
            }

            // NPC.as:232-235 — `this.patient()`.
            if (kind == NpcActivationKind.Patient)
            {
                RunPatient();
                return;
            }

            // NPC.as:217-220 — `else if(npcInter == "doc" || npcInter == "vdoc") this.pip(6);`, and
            // NPC.as:227-230 — `adoc` with `rep > 1` does the same. `pip` writes the three device
            // fields and then `onoff(6)`; the campaign publishes and the device's subscriber opens.
            //
            // Page 6 is still a stub. Opening it is nevertheless the right answer and reporting "no
            // handler for kind 'Doctor'" is not: the Oracle opens the clinic, so the port opens the
            // clinic, and the stub says in one line that its contents are not ported yet. The old
            // behaviour consumed the interaction and showed nothing at all, which is the one outcome
            // the player cannot distinguish from a bug in the doctor's `inter` attribute.
            if (kind == NpcActivationKind.Doctor || kind == NpcActivationKind.AutoDocClinic)
            {
                CampaignManager campaign = CampaignManager.Current;
                if (campaign == null)
                {
                    Debug.LogWarning(
                        "[UnitNPCController] no CampaignManager.Current, so this doctor cannot open " +
                        "the clinic page.");
                    return;
                }

                // `pip(6)` writes all three device fields before it opens the page, so the doctor's own
                // shelf is reachable from the Vendor tab while the clinic is up. Dropping the vendor id
                // is what made a doctor's trade unreachable (see ClinicOpenedMessage).
                campaign.RequestClinic(_persona.npcId, _persona.interactionType, _persona.vendorId);
                return;
            }

            ReportUnwiredActivation(kind.ToString());
        }

        /// <summary>
        /// The inputs <c>NPC.activate</c> branches on (<c>NPC.as:205-248</c>), assembled from the
        /// persona and the campaign.
        /// </summary>
        NpcActivationInput BuildActivationInput(out NpcDialogueEntry entry, out string dialogueId)
        {
            entry = null;
            dialogueId = null;

            bool dialogueMatched = false;

            if (_persona != null)
            {
                NpcDialogueFacts facts = BuildDialogueFacts();
                dialogueMatched = NpcDialogueGate.TryFindGreeting(_persona, facts, out dialogueId);

                if (dialogueMatched)
                {
                    entry = FindDialogueEntry(dialogueId);
                }
            }

            return new NpcActivationInput(
                npcInter: _persona != null ? _persona.interactionType : string.Empty,
                hasVendor: _persona != null && !string.IsNullOrEmpty(_persona.vendorId),
                dialogueMatched: dialogueMatched,
                rep: _rep,
                hasLegacyDialogue: _persona != null && !string.IsNullOrEmpty(_persona.legacyDialogueId),
                // UnitNPC.as:98 — `this.targNPC.owner = this;` for every NPC, unconditionally.
                hasOwner: true);
        }

        /// <summary>
        /// The dialogue conditions, resolved against the campaign.
        /// </summary>
        /// <remarks>
        /// <para><b>Every function is supplied, including the ones that can only answer "no".</b> That
        /// is deliberate: <c>NpcDialogueGate.TryFindGreeting</c> skips a gate whose function is
        /// <c>null</c>, so a missing function is a gate that silently stops gating — the "tolerant null
        /// dependency degrades to silence" shape. A function that returns a real, defensible value keeps
        /// the gate honest even when the underlying system is thin.</para>
        ///
        /// <para><b>The two thin ones, named.</b> <c>BarterSkill</c> is 0 because the port has no
        /// skill table for the player yet; 0 disables the gate rather than passing it, which is the same
        /// answer <c>entry.requiredBarter &gt; 0</c> gives for the 95 of 97 dialogue rows that author no
        /// <c>barter</c> at all. <c>QuestState</c> reads <c>CampaignManager.TryGetQuestState</c>, which
        /// is real.</para>
        /// </remarks>
        NpcDialogueFacts BuildDialogueFacts()
        {
            CampaignManager campaign = CampaignManager.Current;

            if (campaign == null && !_reportedNoCatalog)
            {
                _reportedNoCatalog = true;
                Debug.LogWarning(
                    "[UnitNPCController] no CampaignManager.Current, so every dialogue gate on this " +
                    "NPC is evaluated against an empty campaign: no trigger is set, no land is " +
                    "accessible and no quest is in progress.");
            }

            // AS3 reads the LIVE player here — `World.w.pers.level` (:273-277) and
            // `World.w.gg.currentArmor.id` (:306-310). Both used to be hardcoded answers, which is
            // worse than a missing gate: a constant `false` does not "skip" the test, it makes every
            // row that carries the attribute unmatchable for ever, so the row is dead data and the
            // dialogue list silently falls through to the next entry.
            //
            //   `level: 0`  → 10 authored rows (`vendor_armor_new1` lvl=5, `vendor_weap_new1`
            //                 lvl=10, `vendor_armor_paint` lvl=20, …) could never be reached.
            //   `ownsArmor: false` → 3 rows (`dialEnd_vendor_armor` armor='ali',
            //                 `vendor_expl_sapper` armor='sapper', `vendor_sr_power_armor`
            //                 armor='power') could never be reached.
            //
            // The player is resolved lazily and cached: `PlayerController` is a scene object and this
            // controller may evaluate the gate before it exists (the bubble poll runs from the first
            // tick), so a null is retried rather than remembered as "there is no player".
            CharacterStats stats = ResolvePlayerStats();
            GameInventory inventory = ResolvePlayerInventory();

            return new NpcDialogueFacts(
                isDialoguePlayed: id => campaign != null && TriggerTruthy(campaign.GetTrigger("dial_" + id)),
                isTriggerOne: name => campaign != null && TriggerEquals(campaign.GetTrigger(name), 1d),
                landAccessible: landId => campaign != null && LandIsAccessible(campaign, landId),
                questState: questId => campaign != null && campaign.TryGetQuestState(questId, out int state, out _, out _)
                    ? state
                    : 0,
                // `gg.currentArmor == null || gg.currentArmor.id != @armor` — the EQUIPPED set, not
                // "owns it". An empty `CurrentArmorId` therefore fails every `@armor` row, which is
                // the oracle's answer for a player in plain clothes.
                ownsArmor: armorId => inventory != null &&
                                      !string.IsNullOrEmpty(inventory.CurrentArmorId) &&
                                      string.Equals(inventory.CurrentArmorId, armorId, StringComparison.Ordinal),
                // No row in the shipped data carries `@weap` on a `<dial>` (measured: 0 of 97), and
                // `NpcDialogueGate` does not consult the attribute at all — so this answers the same
                // question the same way as `@armor` rather than returning a constant, and the day the
                // gate starts reading it the answer is already right.
                ownsWeapon: weaponId => inventory != null &&
                                        !string.IsNullOrEmpty(inventory.CurrentWeaponId) &&
                                        string.Equals(inventory.CurrentWeaponId, weaponId, StringComparison.Ordinal),
                level: stats != null ? stats.Level : 0,
                // AS3 compares the barter skill's TIER, not its points: `getSkLevel(pers.skills["barter"])`
                // (<c>NPC.as:278</c>) is the 0-5 ladder at <c>Pers.as:995-1016</c> (>=2→1, >=5→2, >=9→3,
                // >=14→4, >=20→5). `CharacterStats.GetSkillTier` is that same ladder
                // (<c>ComputeSkillTier</c>), so this is the faithful reading; `GetSkillLevel` would hand
                // back the raw 0-20+ points and make every `barter='1'` row pass at 2 points instead of
                // at tier 1 — the two coincide only below tier 1.
                barterSkill: stats != null ? stats.GetSkillTier("barter") : 0,
                isTriggerValue: (name, value) =>
                    campaign != null &&
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double wanted) &&
                    TriggerEquals(campaign.GetTrigger(name), wanted),
                // `gg.currentPet` — there is no pet system in the port, so "the player has this pet"
                // is honestly false. One row authors it (`dialVelvet3 pet='phoenix'`) and it is a
                // side conversation, not a gate on any quest.
                hasPet: _ => false);
        }

        /// <summary>
        /// The player's stats, cached across calls but never cached as <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <b>Why a scene search at all.</b> This controller is spawned by <c>RoomUnitSpawner</c>, which
        /// has no player reference and no container — the same reason <c>HudBootstrapper</c> and
        /// <c>DevConsoleCommands</c> resolve the player the same way. The lookup runs once per NPC
        /// instance; the per-second bubble poll then reads a field.
        /// </remarks>
        CharacterStats ResolvePlayerStats()
        {
            if (_playerStats != null) return _playerStats;

            PlayerController player = FindFirstObjectByType<PlayerController>(FindObjectsInactive.Include);
            _playerStats = player != null ? player.CharacterStats : null;
            return _playerStats;
        }

        /// <summary>The player's inventory — see <see cref="ResolvePlayerStats"/> for the lookup rule.</summary>
        GameInventory ResolvePlayerInventory()
        {
            if (_playerInventory != null) return _playerInventory;

            PlayerController player = FindFirstObjectByType<PlayerController>(FindObjectsInactive.Include);
            PlayerInventory component = player != null ? player.GetComponent<PlayerInventory>() : null;
            _playerInventory = component != null ? component.Inventory : null;
            return _playerInventory;
        }

        static bool LandIsAccessible(CampaignManager campaign, string landId)
        {
            if (string.IsNullOrEmpty(landId))
            {
                return true;
            }

            return campaign.LandStates.TryGet(landId, out LandRuntimeState state) && state.access;
        }

        /// <summary>
        /// AS3's <c>trig(name)</c> in boolean position — a set, non-zero value. AS3 coerces, so the
        /// string <c>"1"</c> and the int <c>1</c> both read true.
        /// </summary>
        static bool TriggerTruthy(TriggerValue value)
            => value.IsSet && (!value.TryAsNumber(out double number) || number != 0d);

        static bool TriggerEquals(TriggerValue value, double expected)
            => value.IsSet && value.TryAsNumber(out double number) && number == expected;

        NpcDialogueEntry FindDialogueEntry(string dialogueId)
        {
            if (_persona?.dialogues == null || string.IsNullOrEmpty(dialogueId))
            {
                return null;
            }

            for (int i = 0; i < _persona.dialogues.Count; i++)
            {
                NpcDialogueEntry entry = _persona.dialogues[i];
                if (entry != null && string.Equals(entry.dialogueId, dialogueId, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>
        /// Run a matched dialogue — AS3 <c>NPC.check(true)</c>'s body (<c>NPC.as:317-362</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The action list is built the way the oracle builds it.</b> AS3 prepends a
        /// <c>dialog</c> action carrying the id (only when <c>World.w.dialOn</c>), runs the row's own
        /// <c>&lt;scr&gt;</c>, and appends <c>trigger dial_&lt;id&gt; 1</c> plus <c>checkall</c>. The
        /// appended trigger is what makes the <i>second</i> conversation skip this row — drop it and the
        /// player is stuck repeating the same greeting for ever, with the travel branch never
        /// reached.</para>
        ///
        /// <para><b>The <c>dialog</c> action is prepended unconditionally.</b> AS3 gates it on the
        /// player's "show dialogue" setting, which the port does not have; and
        /// <c>ScriptRunner</c>'s <c>dialog</c> branch already treats "no text for this id" as a no-op
        /// rather than a block, so a missing text row degrades to "the script still runs".</para>
        ///
        /// <para><b>The script is run through a real <c>ScriptRunner</c>, not a bespoke dispatcher.</b>
        /// <c>openland</c> is a campaign command with an existing, tested implementation; a second
        /// interpreter here would be a second place for the vocabulary to drift.</para>
        /// </remarks>
        void RunGreeting(NpcDialogueEntry entry, string dialogueId)
        {
            ScriptSystem system = ScriptSystem.Current;
            if (system == null)
            {
                Debug.LogWarning(
                    $"[UnitNPCController] '{name}' matched dialogue '{dialogueId}' but there is no " +
                    "ScriptSystem.Current to run it, so the greeting — and its script — did nothing.");
                return;
            }

            var actions = new List<MapObjectScriptActionData>
            {
                new MapObjectScriptActionData
                {
                    act = "dialog",
                    val = dialogueId,
                    // AS3 pushes `n = -1` here (`NPC.as:327-336`), not 0. The value reaches
                    // `Script.as`'s `dialog` arm, which reads the row index `n` starts from — a
                    // dialogue always starts at row 0, and -1 is how the oracle says "no row cursor".
                    n = "-1",
                    t = "0",
                    opt1 = "0",
                    opt2 = "0",
                    targ = string.Empty,
                },
            };

            MapObjectScriptData body = ScriptCatalogHost.Convert(entry.actions);
            if (body?.actions != null)
            {
                actions.AddRange(body.actions);
            }

            actions.Add(new MapObjectScriptActionData
            {
                act = "trigger",
                val = "dial_" + dialogueId,
                t = "0",
                n = "1",
                opt1 = "0",
                opt2 = "0",
                targ = string.Empty,
            });

            // AS3 pushes this third (`NPC.as:340-347`): `{act:"checkall", val:0, n:1}` — every unit in
            // the room re-runs `check`, which is how a dialogue can hand the beat to another NPC.
            // `ScriptRunner` has no `checkall` branch yet (it is on `ScriptVocabulary`'s known-unwired
            // list), so this is fidelity rather than a behaviour change: it is appended so the action
            // list is the oracle's list, and lands the day the branch does.
            actions.Add(new MapObjectScriptActionData
            {
                act = "checkall",
                val = "0",
                t = "0",
                n = "1",
                opt1 = "0",
                opt2 = "0",
                targ = string.Empty,
            });

            var script = new MapObjectScriptData { eventName = string.Empty, actions = actions };

            // `new Script(node, World.w.land, owner, true)` — the land context is what makes `@targ`
            // resolvable. See SetScriptHosts for the three shipped actions that were being dropped.
            ScriptRunner runner = system.CreateRunner(
                objects: _scriptObjects, campaign: null, units: _scriptUnits);
            system.Register(runner);

            // The owner is deliberately null: an NPC is not an ObjectInstance, and the actions a
            // dialogue row actually authors against `@targ="this"` are the object-command verbs, which
            // `ScriptRunner` routes through the object host above rather than through the owner.
            runner.Run(_room, script, owner: null);
        }

        void ReportUnwiredActivation(string key)
        {
            if (!_reportedUnwiredActivations.Add(key))
            {
                return;
            }

            Debug.LogWarning(
                $"[UnitNPCController] '{name}' (persona '{_npcId}') resolved an activation of kind " +
                $"'{key}', which the port has no handler for. The world interaction was consumed and " +
                "did nothing; see NpcActivationKind for which branches exist.");
        }

        // ── The autodoc and the patient ────────────────────────────────────────────────────────────
        //
        // `NPC.repair` (:492-557) and `NPC.patient` (:563-621). Both were RESOLVED by
        // `NpcActivation.Resolve` and then had no handler, so the interaction was consumed and
        // reported "no handler for kind 'AutoDocRepair'" — the doctor looked inert.
        //
        // Both ladders are driven by the persona's own `<quest>` / `<rep>` children, which
        // `NpcDefinition` did not carry at all until this pass; the fields exist now and
        // `CampaignDataParser` fills them.

        /// <summary>
        /// AS3 <c>NPC.repair()</c> (<c>NPC.as:492-557</c>) — the <c>inter='adoc'</c> branch taken while
        /// <c>rep &lt;= 1</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The first interaction does not repair anything, by design.</b> <c>:494-500</c>
        /// grants the persona's quest and <i>returns</i>, so the autodoc's first use is a conversation
        /// and only the second can be a repair. That is the branch the port was missing entirely.</para>
        ///
        /// <para><b>The parts list is walked twice, and it must be.</b> <c>:514-529</c> only tests each
        /// row and reports the shortfall; <c>:530-541</c> withdraws. A single fused loop would consume
        /// glue and tape before discovering the chip is missing, leaving the player short with nothing
        /// repaired.</para>
        ///
        /// <para><b>One oracle quirk is reproduced rather than fixed.</b> The test is
        /// <c>items[id] &amp;&amp; items[id].kol &lt; kol</c> — so a part the player has <i>never held</i>
        /// has no inventory row at all, the condition is false, and its requirement is <b>skipped</b>.
        /// A part that was held and spent to zero keeps its row (<c>Invent.minusItem</c> sets
        /// <c>kol = 0</c> and does not delete it, <c>Invent.as:1393-1396</c>) and therefore does fail.
        /// The port's <c>GameInventory</c> keeps rows the same way, so the same distinction is
        /// observable here; it is written out rather than "corrected" because the fix belongs to the
        /// Oracle.</para>
        /// </remarks>
        void RunAutoDocRepair()
        {
            CampaignManager campaign = CampaignManager.Current;
            CharacterStats stats = ResolvePlayerStats();
            GameInventory inventory = ResolvePlayerInventory();

            if (stats == null || inventory == null)
            {
                ReportOnce("(autodoc player)", $"[UnitNPCController] '{name}' is an autodoc, but the " +
                    "player's stats/inventory could not be resolved, so the repair did nothing.");
                return;
            }

            // :494-500 — grant the quest, then stop.
            if (!string.IsNullOrEmpty(_persona.questId) && campaign != null &&
                !campaign.TryGetQuestRuntime(_persona.questId, out _))
            {
                campaign.AddQuest(_persona.questId);
                return;
            }

            // :501-504 — `pers.skills[needskill] == null`. A skill the player was never given is a
            // different answer from "level 0", and the port's table keeps the two apart.
            if (!string.IsNullOrEmpty(_persona.requiredSkill) &&
                !stats.SkillLevels.ContainsKey(_persona.requiredSkill))
            {
                return;
            }

            int tier = stats.GetSkillTier(_persona.requiredSkill);
            bool canRepair = false;

            if (tier < 2)
            {
                ShowNpcDialogue("rblAutoDocR1");
            }
            else if (tier >= 5)
            {
                ShowNpcDialogue("rblAutoDocR5");
                canRepair = true;
            }
            else if (_rep == 1)
            {
                canRepair = true;

                for (int i = 0; i < _persona.repairRequirements.Count; i++)
                {
                    NpcRepairRequirement requirement = _persona.repairRequirements[i];
                    if (requirement == null || string.IsNullOrEmpty(requirement.itemId)) continue;

                    GameItemInstance row = inventory.GetItem(requirement.itemId);
                    if (row == null) continue; // the never-held quirk above

                    if (row.Quantity < requirement.count)
                    {
                        ShowNpcMessage("required",
                            $"{requirement.itemId} x{requirement.count - row.Quantity}");
                        canRepair = false;
                    }
                }

                if (canRepair)
                {
                    for (int i = 0; i < _persona.repairRequirements.Count; i++)
                    {
                        NpcRepairRequirement requirement = _persona.repairRequirements[i];
                        if (requirement == null || string.IsNullOrEmpty(requirement.itemId)) continue;
                        if (inventory.GetItem(requirement.itemId) == null) continue;

                        inventory.RemoveItem(requirement.itemId, requirement.count);
                        ShowNpcMessage("withdraw", $"{requirement.itemId} x{requirement.count}");
                    }

                    ShowNpcDialogue("rblAutoDocR4");
                }
                else
                {
                    ShowNpcDialogue("rblAutoDocR3");
                }
            }
            else
            {
                ShowNpcDialogue("rblAutoDocR2");
                _rep = 1;
            }

            if (!canRepair) return;

            // :548-551 — `rep = 2; inter.t_action = 0; setStatus();`. `t_action` is the interaction
            // cooldown the port calls `_tRef`; zeroing it lets the next press land immediately.
            _rep = 2;
            _tRef = 0;
            RefreshBubble();

            // :552-555 — `closeQuest(quest.@id, quest.@cid)`, i.e. the SUB form. `cid` is "1" in the
            // shipped data; an empty one would mean "close everything", which is why it is passed
            // through rather than defaulted.
            if (!string.IsNullOrEmpty(_persona.questId))
            {
                campaign?.CloseQuest(_persona.questId, _persona.questSubId);
            }
        }

        /// <summary>
        /// AS3 <c>NPC.patient()</c> (<c>NPC.as:563-621</c>) — the <c>inter='patient'</c> branch.
        /// </summary>
        /// <remarks>
        /// <para><b>The captive's dialogue does not pre-empt this.</b> <c>activate()</c> tests
        /// <c>check(true) &amp;&amp; npcInter != "patient"</c>, so a matched dialogue still falls through
        /// to here — <c>NpcActivation.Resolve</c> already reproduces that exemption.</para>
        ///
        /// <para><b><c>rep</c> is the whole state machine.</b> 0 → tell the player what the patient
        /// needs and set 1; 1 → consume the vaccine and set 2; 2 → the "open your eyes" ending, gated
        /// on the <c>patient_tr2</c> trigger having been written as the literal <c>"wait"</c>. Note
        /// that literal: it is a string, not <c>1</c>, and <c>trig("patient_tr2") == "1"</c> is false
        /// for it — the last branch is unreachable until something else writes the trigger.</para>
        /// </remarks>
        void RunPatient()
        {
            CampaignManager campaign = CampaignManager.Current;
            CharacterStats stats = ResolvePlayerStats();
            GameInventory inventory = ResolvePlayerInventory();

            if (stats == null || inventory == null)
            {
                ReportOnce("(patient player)", $"[UnitNPCController] '{name}' is a patient, but the " +
                    "player's stats/inventory could not be resolved, so nothing happened.");
                return;
            }

            // :565-569 — the skill gate comes first, before any branch.
            if (!string.IsNullOrEmpty(_persona.requiredSkill) &&
                !stats.SkillLevels.ContainsKey(_persona.requiredSkill))
            {
                return;
            }

            int tier = stats.GetSkillTier(_persona.requiredSkill);

            if (_rep == 2)
            {
                // :575-586 — `trig("patient_tr2") == "1"` opens the owner's eyes; anything else (the
                // literal "wait" included) re-shows the line.
                bool opened = campaign != null && TriggerEquals(campaign.GetTrigger("patient_tr2"), 1d);
                if (opened)
                {
                    // `this.owner.command("openEyes")` — the unit-command vocabulary has no `openEyes`
                    // verb in the port, and the patient's owner is a room unit this controller does not
                    // command. Named rather than silently dropped: the quest half below still runs.
                    ReportOnce("openEyes", $"[UnitNPCController] '{name}': the patient's " +
                        "`openEyes` verb has no consumer in the port, so the visual opening is not " +
                        "played. The quest progression below is unaffected.");
                }
                else
                {
                    ShowNpcDialogue("dialPatient7");
                }
            }
            else if (_rep == 1)
            {
                // :587-601 — the vaccine. `items[needitem] && kol > 0`, so a missing row is a refusal
                // here (unlike the autodoc's parts list, which skips it).
                string itemId = _persona.requiredItemId;
                if (!string.IsNullOrEmpty(itemId) && inventory.HasItem(itemId, 1))
                {
                    inventory.RemoveItem(itemId, 1);
                    _rep = 2;
                    ShowNpcDialogue("dialPatient5");

                    // `triggers["patient_tr2"] = "wait"` — the STRING, not 1. `TriggerValue.FromString`
                    // keeps it a string, so `trig(name) == 1` is false for it, exactly as in the oracle.
                    campaign?.SetTrigger("patient_tr2", TriggerValue.FromString("wait"));
                    campaign?.CloseQuest("patientHeal", "3");
                    campaign?.ShowQuest("patientHeal", "4");
                }
                else
                {
                    ShowNpcDialogue("dialPatient8");
                }
            }
            else if (tier < 4)
            {
                ShowNpcDialogue("dialPatient2");
            }
            else
            {
                // :614-621 — the medic identifies the illness and starts the quest's stages 2 and 3.
                ShowNpcDialogue("dialPatient3");
                _rep = 1;
                campaign?.SetTrigger("patient_tr1", TriggerValue.FromInt(1));
                campaign?.CloseQuest("patientHeal", "1");
                campaign?.ShowQuest("patientHeal", "2");
                campaign?.ShowQuest("patientHeal", "3");
            }

            // `this.refresh()` (:620) — re-evaluate the icon/verb after the state change.
            RefreshBubble();
        }

        /// <summary>
        /// Show one authored dialogue line through the room's real <see cref="ScriptRunner"/>.
        /// </summary>
        /// <remarks>
        /// <b>Why a runner and not a direct call to the presentation.</b> The autodoc's lines are
        /// ordinary <c>dial</c> text and must behave like any other dialogue — blocking on a press,
        /// releasing control afterwards. Reusing the interpreter is the same argument
        /// <see cref="RunGreeting"/> makes: a second path would be a second place for the blocking
        /// rules to drift.
        /// </remarks>
        void ShowNpcDialogue(string dialogueId)
        {
            if (string.IsNullOrEmpty(dialogueId)) return;
            RunOneNpcAction("dialog", dialogueId);
        }

        /// <summary>
        /// Show one HUD message — AS3 <c>World.w.gui.infoText(key, …)</c>.
        /// </summary>
        /// <remarks>
        /// <b>The arguments are logged, not rendered.</b> <c>infoText</c> takes parameters
        /// (<c>required, itemName, shortfall</c>; <c>withdraw, itemName, count</c>) and the port's
        /// <c>mess</c> command carries only a text key. The numbers therefore go to the console so a
        /// play-tester can see what the autodoc asked for; wiring them into the HUD needs the
        /// parameterised <c>infoText</c> seam, which does not exist yet.
        /// </remarks>
        void ShowNpcMessage(string key, string detail)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!string.IsNullOrEmpty(detail))
            {
                Debug.Log($"[UnitNPCController] infoText '{key}': {detail}");
            }

            RunOneNpcAction("mess", key);
        }

        void RunOneNpcAction(string act, string val)
        {
            ScriptSystem system = ScriptSystem.Current;
            if (system == null)
            {
                Debug.LogWarning(
                    $"[UnitNPCController] '{name}' tried to run `{act} {val}` but there is no " +
                    "ScriptSystem.Current, so the line was not shown.");
                return;
            }

            var script = new MapObjectScriptData
            {
                eventName = string.Empty,
                actions = new List<MapObjectScriptActionData>
                {
                    new MapObjectScriptActionData
                    {
                        act = act,
                        val = val,
                        t = "0",
                        n = "-1",
                        opt1 = "0",
                        opt2 = "0",
                        targ = string.Empty,
                    },
                },
            };

            ScriptRunner runner = system.CreateRunner(
                objects: _scriptObjects, campaign: null, units: _scriptUnits);
            system.Register(runner);
            runner.Run(_room, script, owner: null);
        }

        void ReportOnce(string key, string message)
        {
            if (_reportedUnwiredVerbs.Add(key)) Debug.LogWarning(message);
        }

        // ── Coordinates ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// This unit's position in the port's room-local pixels — the space <c>UnitInstance.position</c>
        /// and <c>TilePhysicsController.posX/posY</c> both use.
        /// </summary>
        Vector2 RoomLocalPixels => new Vector2(
            transform.localPosition.x * TileQueryConstants.UnitToPixel,
            transform.localPosition.y * TileQueryConstants.UnitToPixel);

        /// <summary>
        /// Convert an AS3 room-local pixel into this port's room-local pixel space.
        /// </summary>
        /// <remarks>
        /// <para><b>X is a shift, Y is a mirror.</b> <c>WorldCoordinates.RoomOriginPixelX</c> documents
        /// it: "X is not flipped — only the land's Y axis is". A room's local pixel space is inset by
        /// <c>borderOffset</c> tiles on both axes, and Y is mirrored about the room's height. The exact
        /// form is the inverse of <c>RoomPopulator.As3UnitAnchorPixels</c>, which maps an AS3 anchor of
        /// <c>(tileY + 1) * 40 - 1</c> to <c>(roomHeight - borderOffset - tileY - 1) * 40 + 1</c> — both
        /// are <c>C - as3Y</c> for the same <c>C</c>, which is what makes this the same mirror rather
        /// than a similar one.</para>
        ///
        /// <para><b>A missing room falls back to no mirror at all</b>, which is wrong but honest: a unit
        /// with no room cannot be flown anywhere meaningful, and silently mirroring about a guessed
        /// height would put it in the wrong place with no signal. The room is always present on the live
        /// path — the spawner holds it.</para>
        /// </remarks>
        Vector2 As3PixelsToRoomLocal(Vector2 as3Pixels)
        {
            if (_room == null)
            {
                return as3Pixels;
            }

            int borderOffset = Mathf.Max(0, _room.borderOffset);
            float tile = WorldConstants.TILE_SIZE;
            float roomPixelHeight = (_room.height - borderOffset) * tile;

            return new Vector2(
                as3Pixels.x + borderOffset * tile,
                roomPixelHeight - as3Pixels.y);
        }
    }
}
