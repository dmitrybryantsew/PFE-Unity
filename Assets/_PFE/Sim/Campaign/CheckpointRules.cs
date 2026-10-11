using System;
using System.Globalization;

namespace PFE.Sim.Campaign
{
    /// <summary>
    /// Everything <c>fe.loc.CheckPoint</c> reads to decide what an activation does.
    ///
    /// <para>Primitives only, on purpose: the offline wall can build a <c>CheckPoint</c> is not
    /// reachable (it is an AS3 class, and the port's checkpoint is a plain object plus a MonoBehaviour
    /// presenter), so the <i>decision</i> is separated from the <i>object</i> and this struct is what the
    /// decision takes.</para>
    /// </summary>
    public struct CheckpointFacts
    {
        /// <summary>AS3 <c>inter.lock &gt; 0 || inter.mine &gt; 0</c> (<c>CheckPoint.as:186-189</c>).</summary>
        public bool locked;

        /// <summary>AS3 <c>active</c>: 0 = fresh, 1 = deactivated (reopenable), 2 = activated.</summary>
        public int active;

        /// <summary>AS3 <c>activate(param1)</c> — true for the <c>beg</c> checkpoint the entry cell
        /// carries (<c>Location.createCheck</c>). A begin checkpoint skips the heal/XP restore.</summary>
        public bool isBegin;

        /// <summary>AS3 <c>xml.@main</c> — a main checkpoint is never deactivated and never
        /// area-activated (<c>CheckPoint.as:129-139</c>).</summary>
        public bool main;

        /// <summary>AS3 <c>xml.@tele</c> — offers the "return" interaction once
        /// <c>game.mReturn</c> is on (<c>CheckPoint.as:97-100</c>).</summary>
        public bool teleOn;

        /// <summary>AS3 <c>used</c> — a hardcore one-shot teleport is spent (<c>CheckPoint.as:266-271</c>).</summary>
        public bool used;

        /// <summary>AS3 <c>World.w.game.mReturn</c> — the return-to-checkpoint feature is armed.</summary>
        public bool mReturn;

        /// <summary>AS3 <c>code</c> — the checkpoint's identity, used to re-find it on re-entry
        /// (<c>Location.as:2065-2067</c>). Empty means "no code".</summary>
        public string code;
    }

    /// <summary>
    /// Which of AS3's two entry points is being run.
    ///
    /// <para><c>CheckPoint</c> exposes <c>activate()</c> (<c>:184</c>) and <c>areaActivate()</c>
    /// (<c>:269</c>), and the second is not a synonym for the first: <c>areaActivate</c> guards on
    /// <c>active == 0</c> before calling <c>activate</c>. So a checkpoint the player walks into behaves
    /// differently from one they press E on — the walk-in is a no-op on an already-activated checkpoint
    /// and on one that has reopened, while the press still works.</para>
    /// </summary>
    public enum CheckpointEntry
    {
        /// <summary>AS3 <c>activate()</c> — the interaction button.</summary>
        Activate,

        /// <summary>AS3 <c>areaActivate()</c> — the walk-into area (<c>:269-275</c>).</summary>
        Area,
    }

    /// <summary>What <see cref="CheckpointRules.Activate"/> decided. A refused activation is a
    /// <i>different answer</i> from a completed one, so it is carried rather than signalled by a bool.</summary>
    public struct CheckpointActivation
    {
        /// <summary>AS3 returned before doing anything (<c>CheckPoint.as:186-190</c>).</summary>
        public bool refused;

        /// <summary>The <c>active</c> value after the call: 2 when it ran, unchanged when refused.</summary>
        public int newActive;

        /// <summary>AS3 <c>if(code)</c> — write <c>prevCPCode</c> and <c>land.act.lastCpCode</c>.</summary>
        public bool writesCode;

        /// <summary>AS3 <c>mReturn &amp;&amp; teleOn &amp;&amp; !used</c> — offer the return interaction.</summary>
        public bool offersTeleport;

        /// <summary>AS3 <c>active == 0 &amp;&amp; !param1</c> — heal the mana pool and grant the XP bonus.</summary>
        public bool grantsRestoreBonus;
    }

    /// <summary>
    /// The pure half of <c>fe.loc.CheckPoint</c> (<c>CheckPoint.as:184-291</c>) — the rules, with no
    /// engine, no <c>ObjectInstance</c> and no scene.
    ///
    /// <para><b>Why this exists separately.</b> The checkpoint is the only save trigger in the game:
    /// <c>CheckPoint.activate</c> ends in <c>World.w.saveGame()</c> (<c>CheckPoint.as:247</c>). A port that
    /// gets the rules wrong therefore saves at the wrong moment, and nothing about a save looks wrong
    /// until a reload. Keeping the rules in <c>PFE.Sim</c> is what makes them testable offline; the
    /// presenter that calls them is a MonoBehaviour and cannot be.</para>
    ///
    /// <para><b>The <c>main</c> checkpoint is not a normal one.</b> Its constructor forces
    /// <c>active = 2</c>, clears its <c>Area</c> and arms the teleport before any player touches it
    /// (<c>CheckPoint.as:129-139</c>) — so it can never be <i>activated</i>, only teleported from. That is
    /// why <see cref="Deactivates"/> refuses <c>main</c> outright.</para>
    /// </summary>
    public static class CheckpointRules
    {
        /// <summary>AS3 <c>active == 0</c> — untouched.</summary>
        public const int Fresh = 0;

        /// <summary>AS3 <c>active == 1</c> — a non-current checkpoint that has reopened.</summary>
        public const int Reopenable = 1;

        /// <summary>AS3 <c>active == 2</c> — activated, or a <c>main</c> checkpoint.</summary>
        public const int Activated = 2;

        /// <summary>
        /// AS3's constructor normalisation (<c>CheckPoint.as:113-118</c> reading <c>loadObj.act</c>, then
        /// <c>:129-135</c> for <c>main</c>): a <c>main</c> checkpoint is forced to <c>active = 2</c>, has
        /// its <c>Area</c> cleared and makes itself the land's current checkpoint <i>before any player
        /// touches it</i>.
        ///
        /// <para><b>Why this is a separate step and not a <c>main</c> term inside
        /// <see cref="IsRefused"/>.</b> The oracle's <c>activate()</c> guard is
        /// <c>inter.lock &gt; 0 || inter.mine &gt; 0 || active == 2</c> (<c>CheckPoint.as:186-190</c>) and
        /// its <c>areaActivate()</c> guard is <c>active == 0</c> (<c>:274-279</c>) — <b>neither mentions
        /// <c>main</c></b>. A main checkpoint is refused, and never area-activated, purely because its
        /// constructor already left it at <c>active == 2</c>. Spelling <c>main</c> into those two guards
        /// would make the port agree with the oracle's <i>outcomes</i> while disagreeing with its
        /// <i>mechanism</i>: it would still let a main checkpoint sit at <c>active == 1</c>, a state the
        /// oracle's construction makes unreachable.</para>
        ///
        /// <para><b>This is not cosmetic.</b> Without it <c>CheckpointFacts.main</c> is a dead field — the
        /// presenter reads <c>@main</c> off the XML and the adapter fills it in, but nothing consumed it, so
        /// a main checkpoint ran the full <c>activate()</c>: it healed, granted the XP bonus and
        /// <b>saved</b>, none of which the oracle does (its button runs <c>teleport</c>, <c>:136</c>).</para>
        /// </summary>
        public static CheckpointFacts Construct(in CheckpointFacts facts)
        {
            CheckpointFacts built = facts;
            if (built.main) built.active = Activated;
            return built;
        }

        /// <summary>
        /// AS3 <c>activate</c>'s opening guard (<c>CheckPoint.as:186-190</c>): a locked checkpoint, or one
        /// already at <c>active == 2</c>, does nothing at all. A <c>main</c> checkpoint is caught here
        /// because <see cref="Construct"/> has already put it at <c>active == 2</c> — the oracle has no
        /// separate <c>main</c> test in this guard either.
        /// </summary>
        public static bool IsRefused(in CheckpointFacts facts)
        {
            return facts.locked || facts.active == Activated;
        }

        /// <summary>
        /// AS3 <c>CheckPoint.activate(param1 = false)</c> (<c>CheckPoint.as:184-248</c>).
        /// </summary>
        /// <remarks>
        /// Callers must pass facts that have been through <see cref="Construct"/> — that is where a
        /// <c>main</c> checkpoint is put at <c>active == 2</c> and so becomes unactivatable. Passing raw
        /// facts is what made a main checkpoint heal, bonus and save before this rule existed.
        /// </remarks>
        public static CheckpointActivation Activate(in CheckpointFacts facts)
        {
            if (IsRefused(in facts))
            {
                return new CheckpointActivation
                {
                    refused = true,
                    newActive = facts.active,
                };
            }

            return new CheckpointActivation
            {
                refused = false,
                newActive = Activated,
                // AS3 `if(code)` — an empty code is not written, so lastCpCode is left alone rather than
                // cleared. Writing "" would erase the land's record of its last checkpoint.
                writesCode = !string.IsNullOrEmpty(facts.code),
                // AS3 `World.w.game.mReturn && this.teleOn && !this.used`.
                offersTeleport = facts.mReturn && facts.teleOn && !facts.used,
                // AS3 `if(this.active == 0 && param1 == false)`.
                grantsRestoreBonus = facts.active == Fresh && !facts.isBegin,
            };
        }

        /// <summary>
        /// AS3 <c>areaActivate()</c> (<c>CheckPoint.as:274-279</c>): the touch-area path, which only ever
        /// fires on a <c>fresh</c> checkpoint. The oracle's guard is <c>if(this.active == 0)</c> with no
        /// <c>main</c> test, because a <c>main</c> checkpoint never reaches here — its constructor set
        /// <c>this.area = null</c> (<c>CheckPoint.as:132</c>) and forced <c>active = 2</c> (<c>:133</c>), so
        /// it is false for it <i>by construction</i> — see <see cref="Construct"/>, which is what makes that
        /// claim true in the port rather than merely asserted.
        /// </summary>
        public static bool AreaActivates(in CheckpointFacts facts)
        {
            return facts.active == Fresh;
        }

        /// <summary>
        /// AS3 <c>step()</c>'s tail (<c>CheckPoint.as:320-322</c>): a checkpoint that is no longer the
        /// player's current one reopens — unless it is a <c>main</c> checkpoint, which
        /// <c>deactivate()</c> refuses to touch (<c>CheckPoint.as:282-285</c>).
        /// </summary>
        /// <param name="isCurrentCheckpoint">AS3 <c>World.w.pers.currentCP == this</c>.</param>
        public static bool Deactivates(int active, bool main, bool isCurrentCheckpoint)
        {
            return active == Activated && !main && !isCurrentCheckpoint;
        }

        /// <summary>
        /// AS3 <c>CheckPoint.teleport()</c> (<c>CheckPoint.as:250-267</c>): where the "return" interaction
        /// sends the player.
        ///
        /// <para><b>An empty string is a real answer, not a failure.</b> AS3's <c>main</c> branch is
        /// <c>else if(missionId != "rbl")</c> — when the mission id <i>is</i> the hub, teleporting would
        /// send the player back into the camp they are already in, so it does nothing. The port compares
        /// against <paramref name="baseId"/> instead of the literal, which is the same value
        /// (<c>Game.as:11</c> <c>baseId = "rbl"</c>) read from the catalogue rather than spelled here.</para>
        /// </summary>
        /// <returns>The land id to travel to, or <c>""</c> when the teleport is a no-op.</returns>
        public static string TeleportTarget(bool main, string missionId, string baseId)
        {
            if (!main)
            {
                return baseId ?? string.Empty;
            }

            if (string.IsNullOrEmpty(missionId)) return string.Empty;
            return string.Equals(missionId, baseId, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : missionId;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Locked / mined variants
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The unlocked checkpoint id — AS3 <c>createCheck</c>'s starting <c>_loc3_</c>
        /// (<c>Location.as:2094</c>). <b>Ten characters</b>, which is what makes the 11th the variant
        /// digit; see <see cref="LockVariantNumber"/>.
        /// </summary>
        public const string PlainCheckpointId = "checkpoint";

        /// <summary>
        /// How many locked/mined variants exist — <c>checkpoint1</c>..<c>checkpoint5</c>
        /// (<c>AllData.as:5008-5012</c>), which is also the frame count AS3's <c>vis.lock</c> clip is
        /// indexed by.
        /// </summary>
        public const int LockedVariantCount = 5;

        /// <summary>
        /// AS3 <c>Location.createCheck</c>'s id roll (<c>Location.as:2094-2098</c>):
        /// <code>
        /// _loc3_ = "checkpoint";
        /// if(!param1 &amp;&amp; this.land.rnd &amp;&amp; Math.random() &lt; 0.5)
        ///     _loc3_ += Math.floor(Math.random() * 5 + 1);
        /// </code>
        ///
        /// <para><b>The begin checkpoint is never a variant.</b> <c>param1</c> short-circuits the whole
        /// roll, so the one checkpoint the player starts the game on is always the plain, unlocked
        /// <c>checkpoint</c>. A caller that got this backwards would draw a padlock on the starting
        /// checkpoint and lock the player out of their own first save point.</para>
        ///
        /// <para>The two random draws are the caller's: <paramref name="variantRollPassed"/> is
        /// <c>Math.random() &lt; 0.5</c> and <paramref name="variantRoll"/> is
        /// <c>Math.floor(Math.random() * 5 + 1)</c>. They are passed in rather than drawn here so this
        /// stays a pure function and so the caller can draw them <i>in the oracle's order</i> — AS3 only
        /// draws the second one when the first passed, and a shared RNG stream makes that visible.</para>
        /// </summary>
        public static string SelectObjectId(
            bool isBegin, bool landIsRandom, bool variantRollPassed, int variantRoll)
        {
            if (isBegin || !landIsRandom || !variantRollPassed)
            {
                return PlainCheckpointId;
            }

            int variant = variantRoll;
            if (variant < 1) variant = 1;
            if (variant > LockedVariantCount) variant = LockedVariantCount;

            return PlainCheckpointId + variant.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Which locked variant an id names — AS3 <c>int(this.id.charAt(10))</c>
        /// (<c>CheckPoint.as:64-68</c>). <b>0</b> when the id is the plain checkpoint or carries no
        /// usable digit.
        ///
        /// <para><b>0 is not a failure here, it is the answer for the common case.</b> <c>charAt(10)</c>
        /// on the ten-character <c>"checkpoint"</c> returns <c>""</c>, which is falsy, so AS3 takes the
        /// <c>else</c> branch and hides the padlock. Returning 0 for "no variant" keeps the two cases —
        /// "no padlock" and "padlock frame N" — from collapsing into one.</para>
        ///
        /// <para><b>Why a digit outside 1..5 reads as no variant.</b> AS3 sets
        /// <c>this.locked = true</c> only <i>after</i> <c>vis.lock.gotoAndStop(n)</c> returns, and
        /// <c>gotoAndStop</c> on a frame the clip does not have throws — caught by the <c>try/catch</c>
        /// wrapped around the whole block (<c>:62-76</c>). The clip has <see cref="LockedVariantCount"/>
        /// frames, so a sixth frame number leaves <c>locked</c> at its <c>false</c> initialiser. Reading
        /// a bad digit as "locked" would be the one direction that cannot be undone at runtime.</para>
        /// </summary>
        public static int LockVariantNumber(string objectId)
        {
            if (string.IsNullOrEmpty(objectId) || objectId.Length <= PlainCheckpointId.Length)
            {
                return 0;
            }

            char digit = objectId[PlainCheckpointId.Length];
            if (digit < '1' || digit > '9') return 0;

            int variant = digit - '0';
            return variant <= LockedVariantCount ? variant : 0;
        }

        /// <summary>
        /// Whether a checkpoint draws with a padlock — AS3 <c>CheckPoint</c>'s constructor sets
        /// <c>this.locked = true</c> and <c>vis.lock.gotoAndStop(n)</c> for exactly these ids
        /// (<c>CheckPoint.as:62-76</c>).
        /// </summary>
        public static bool IsLockedVariant(string objectId) => LockVariantNumber(objectId) != 0;

        /// <summary>
        /// AS3 <c>CheckPoint.activate</c>'s opening guard, first term: <c>inter.lock &gt; 0 || inter.mine
        /// &gt; 0</c> (<c>CheckPoint.as:186-189</c>).
        ///
        /// <para><b>Both values come off the definition row, not the placed node.</b> <c>Interact</c>'s
        /// constructor reads <c>param2.@lock</c>/<c>@mine</c> (<c>Interact.as:218-252</c>), and
        /// <c>param2</c> is the AllData row the object's id resolves to — <c>CheckPoint.as:45</c> passes
        /// exactly that <c>node</c>. The placed checkpoint is built with no XML of its own
        /// (<c>Location.createCheck</c> calls <c>createObj(id,"checkpoint",x,y)</c> with no fourth
        /// argument), so there is no placement-level override to consult.</para>
        ///
        /// <para><b>What the five variants actually carry</b> (<c>AllData.as:5008-5012</c>):
        /// <c>checkpoint1</c>/<c>2</c> <c>lock='1.4'</c>, <c>checkpoint3</c> <c>lock='2'</c>,
        /// <c>checkpoint4</c>/<c>5</c> <c>mine='1'</c> — so this answers <c>true</c> for all five and
        /// <c>false</c> for the plain <c>checkpoint</c>, which authors neither.</para>
        ///
        /// <para><b>It is not the same question as <see cref="IsLockedVariant"/>.</b> That one is the
        /// <i>graphic</i> flag, derived from the id; this one is the <i>gate</i>, derived from the
        /// definition. AS3 keeps them in separate places (<c>this.locked</c> vs <c>inter.lock</c>) and
        /// they are cleared by different things — <c>step()</c> clears the graphic once the lock is
        /// picked (<c>:312-316</c>), while the gate only opens when <c>inter.lock</c> itself reaches
        /// 0. Collapsing them would make the padlock vanish without opening the gate, or vice
        /// versa.</para>
        /// </summary>
        /// <param name="lockAttribute">The definition's <c>lock</c> attribute, or empty.</param>
        /// <param name="mineAttribute">The definition's <c>mine</c> attribute, or empty.</param>
        public static bool IsLocked(string lockAttribute, string mineAttribute)
        {
            return IsPositiveNumber(lockAttribute) || IsPositiveNumber(mineAttribute);
        }

        private static bool IsPositiveNumber(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;

            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                   && value > 0f;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Walk-into area
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The checkpoint's own interaction box, in world units, relative to its bottom-centre anchor.
        ///
        /// <para><b>One box serves both the walk-into area and the cursor test.</b> The constructor sizes
        /// the <c>Area</c> from <c>X1/X2/Y1/Y2</c> (<c>CheckPoint.as:51-54</c>, <c>:90-92</c>) and
        /// <c>step()</c> then reuses those same four numbers for <c>onCursor</c>
        /// (<c>:295</c>) — so "the player walked in" and "the cursor is over it" are the same region by
        /// construction, not by coincidence.</para>
        /// </summary>
        public struct CheckpointAreaBox
        {
            /// <summary>Half the box width — AS3 <c>scX / 2</c>, where <c>scX = size * tileX</c>.</summary>
            public float halfWidth;

            /// <summary>The box height — AS3 <c>scY</c>, where <c>scY = wid * tileY</c>.</summary>
            public float height;

            /// <summary>
            /// How far the box centre sits above the anchor. AS3's <c>Y1 = Y - scY</c>, <c>Y2 = Y</c>
            /// makes the box <b>bottom-anchored</b>, so the centre is half the height up — the same
            /// convention <c>ResolveLegacyBottomAnchorPixels</c> already places the sprite on.
            /// </summary>
            public float centreYOffset;
        }

        /// <summary>
        /// AS3's checkpoint box from the definition's footprint (<c>CheckPoint.as:48-53</c>):
        /// <c>scX = node.@size * World.tileX</c>, <c>scY = node.@wid * World.tileY</c>.
        ///
        /// <para><b><c>size</c> is the width and <c>wid</c> is the height</b> — the names are the
        /// oracle's and they read backwards, which is exactly why the checkpoint's box is 2 tiles wide
        /// by 3 tall (<c>AllData.as:5007</c>) rather than the other way round.</para>
        /// </summary>
        /// <param name="sizeTiles">AS3 <c>@size</c> — width in tiles.</param>
        /// <param name="widTiles">AS3 <c>@wid</c> — height in tiles.</param>
        /// <param name="tileSizeWorld">One tile in world units (<c>WorldConstants.TILE_SIZE / 100</c>).</param>
        public static CheckpointAreaBox ResolveAreaBox(int sizeTiles, int widTiles, float tileSizeWorld)
        {
            float width = Math.Max(0, sizeTiles) * tileSizeWorld;
            float height = Math.Max(0, widTiles) * tileSizeWorld;

            return new CheckpointAreaBox
            {
                halfWidth = width * 0.5f,
                height = height,
                centreYOffset = height * 0.5f,
            };
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Land-entry resume
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A land cell — AS3's <c>locX/locY/locZ</c> triple, which names <b>one room</b> of a land.
        ///
        /// <para>Plain <c>int</c>s rather than <c>Vector3Int</c> so this stays in <c>PFE.Sim</c>: the
        /// offline wall cannot resolve <c>UnityEngine</c>, and a rule that cannot be executed offline
        /// cannot be tested offline. The caller converts at the boundary.</para>
        /// </summary>
        public struct CheckpointRoom
        {
            public int x;
            public int y;
            public int z;
        }

        /// <summary>
        /// Which of <c>enterLand</c>'s three branches supplies the starting room — AS3's
        /// <c>if/else if/else</c> order (<c>Land.as:1146</c>, <c>:1171</c>, <c>:1178</c>) lifted out so the
        /// <b>precedence itself</b> is testable.
        ///
        /// <para><b>Why the order is not left to the call site.</b> Written as a <c>??</c> chain the
        /// precedence is invisible: swapping two terms still compiles, still reads plausibly, and changes
        /// behaviour only for the case where a caller named a room <i>and</i> the land has a checkpoint —
        /// which is the re-visit through a door script, i.e. not rare. The oracle is unambiguous
        /// (named wins), so it is spelled here and asserted.</para>
        /// </summary>
        public enum EntryRoomSource
        {
            /// <summary>AS3 <c>param2 != null</c> — the caller named a room (<c>"x:y"</c>).</summary>
            Named,

            /// <summary>AS3 <c>this.currentCP &amp;&amp; !param1</c> — a re-visit resumes at the checkpoint.</summary>
            Checkpoint,

            /// <summary>AS3's <c>else</c> — the land's begin cell.</summary>
            Entrance,
        }

        /// <summary>
        /// AS3 <c>Land.enterLand</c>'s branch selection (<c>Land.as:1143-1186</c>). See
        /// <see cref="EntryRoomSource"/> for why this is a rule and not an inline <c>??</c> chain.
        /// </summary>
        /// <param name="hasNamedRoom">AS3 <c>param2 != null</c> — a coordinate string was supplied.</param>
        /// <param name="hasCheckpointRoom">Whether <see cref="ResumeRoomOnEntry"/> returned a room.</param>
        public static EntryRoomSource ResolveEntryRoomSource(bool hasNamedRoom, bool hasCheckpointRoom)
        {
            if (hasNamedRoom) return EntryRoomSource.Named;
            if (hasCheckpointRoom) return EntryRoomSource.Checkpoint;
            return EntryRoomSource.Entrance;
        }

        /// <summary>
        /// AS3 <c>Game.as:347-350</c> — the <c>_loc1_</c> that <c>enterLand</c> receives as its
        /// <c>param1</c>:
        /// <code>
        /// var _loc1_:* = false;
        /// if(!this.curLand.rnd &amp;&amp; !this.curLand.visited) { _loc1_ = true; }
        /// </code>
        ///
        /// <para><b>This is the whole of "on re-visit".</b> The flag is true exactly once per authored
        /// land — the first time it is entered — and false on every later entry. A procedural land
        /// (<c>rnd</c>) is <i>never</i> a first visit, because its layout is rebuilt each time and there
        /// is no authored entry cell to protect.</para>
        ///
        /// <para><b>Order matters and is easy to invert.</b> <c>Game.as</c> computes this <i>before</i>
        /// calling <c>enterLand</c> and only sets <c>visited = true</c> afterwards (<c>:386-389</c>). A
        /// port that marked the land visited on the way in would make <c>firstVisit</c> permanently
        /// false and silently resume every land at its checkpoint, including the first entry.</para>
        /// </summary>
        public static bool IsFirstVisit(bool landIsRandom, bool visited)
        {
            return !landIsRandom && !visited;
        }

        /// <summary>
        /// AS3 <c>Land.enterLand</c>'s middle branch (<c>Land.as:1171-1176</c>) — where the player is put
        /// when they re-enter a land they have a checkpoint in:
        /// <code>
        /// else if(Boolean(this.currentCP) &amp;&amp; !param1)
        /// {
        ///    World.w.pers.currentCP = this.currentCP;
        ///    this.gotoCheckPoint();
        ///    this.currentCP.activate();
        /// }
        /// </code>
        ///
        /// <para><b>The room, not the position.</b> <c>gotoCheckPoint</c> (<c>:1466-1472</c>) sets
        /// <c>locX/locY/locZ</c> from the checkpoint's <i>own</i> land cell and only then
        /// <c>gg.setLocPos(_loc1_.X, _loc1_.Y)</c>. This function answers the first half; the second is
        /// already free in the port, because <c>RoomPopulator.CreateCheckpoint</c> registers a
        /// <c>SpawnType.Player</c> spawn point at the checkpoint itself and
        /// <c>RoomSetup.FindPlayerSpawnPixels</c> returns it as Priority 1. So choosing the right room
        /// <i>is</i> choosing the right position.</para>
        ///
        /// <para><b><c>this.currentCP</c> is the <i>land's</i> checkpoint, not <c>pers.currentCP</c>.</b>
        /// The oracle keeps both (<c>CheckPoint.activate</c> writes <c>pers.currentCP</c> at
        /// <c>:196</c> while <c>land.currentCP</c> is what this branch tests). The port stores one record
        /// and tags it with its land id, so "this land has a current checkpoint" is the land-id match —
        /// <paramref name="checkpointLandId"/> against <paramref name="targetLand"/> — and a checkpoint
        /// left in <i>another</i> land correctly does not hijack this entry.</para>
        ///
        /// <para><b>The branch sits between the other two, and its order is load-bearing.</b> AS3 tests
        /// explicit coordinates first (<c>:1146</c>), this second, the begin cell third (<c>:1178</c>).
        /// So a caller that <i>did</i> name a room still gets that room even on a re-visit — only a
        /// nameless entry falls through to the checkpoint.</para>
        /// </summary>
        /// <returns>The room to start in, or <c>null</c> to fall through to the land's entry cell.</returns>
        public static CheckpointRoom? ResumeRoomOnEntry(
            bool hasCheckpoint, string checkpointLandId, string targetLand, bool firstVisit,
            int roomX, int roomY, int roomZ)
        {
            if (!hasCheckpoint || firstVisit) return null;

            if (string.IsNullOrEmpty(checkpointLandId) || string.IsNullOrEmpty(targetLand)) return null;

            if (!string.Equals(checkpointLandId, targetLand, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new CheckpointRoom { x = roomX, y = roomY, z = roomZ };
        }
    }
}
