using System;
using System.Text;
using UnityEngine;

namespace PFE.Core
{
    /// <summary>
    /// One-line gate for any component that draws a debug overlay.
    ///
    /// <para><b>Why this exists rather than a serialized <see cref="PfeDebugSettings"/> field on each
    /// overlay.</b> A serialized reference has to be wired per scene and per prefab, and an overlay
    /// whose reference is null cannot be switched off — which is how three of these readouts ended up
    /// drawing unconditionally. This resolves the settings asset itself, caches it, and answers false
    /// when there is none, so the default is always "draw nothing".</para>
    ///
    /// <para><b>Reads through, never caches the answer.</b> Only the asset reference is cached; the
    /// channel membership is read live, so a console write takes effect on the very next frame with no
    /// notification plumbing and no chance of a stale copy.</para>
    /// </summary>
    public static class DebugOverlays
    {
        private static PfeDebugSettings _settings;
        private static bool _lookupAttempted;

        /// <summary>The settings asset, or null if the project has none.</summary>
        public static PfeDebugSettings Settings
        {
            get
            {
                if (_settings != null) return _settings;

                // Attempt the load once, then stop: Resources.Load on every frame for a project with
                // no asset would be a silent per-frame cost in a debugging tool.
                if (_lookupAttempted) return null;

                _lookupAttempted = true;
                _settings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
                return _settings;
            }
        }

        /// <summary>Is this overlay switched on? False when there is no settings asset.</summary>
        public static bool IsOn(DebugOverlayChannel channel)
        {
            PfeDebugSettings settings = Settings;
            return settings != null && settings.IsOverlayEnabled(channel);
        }

        /// <summary>The whole mask, or <see cref="DebugOverlayChannel.None"/> with no asset.</summary>
        public static DebugOverlayChannel Enabled
        {
            get
            {
                PfeDebugSettings settings = Settings;
                return settings != null ? settings.EnabledOverlays : DebugOverlayChannel.None;
            }
        }
    }

    /// <summary>
    /// Every debug visualisation in the game, as an independent on/off channel.
    ///
    /// <para><b>Why one flags mask and not a dozen bools.</b> The requirement is "each one can be set
    /// on, or all at once, or several of them", which is a set, not a list of switches. A mask makes
    /// <c>col on doors,triggers</c> and <c>col on all</c> the same operation, keeps the channels in
    /// one place so the console's help cannot drift from what exists, and makes "which overlays are
    /// on" a single value that cannot be half-updated.</para>
    ///
    /// <para><b>Independence is the contract.</b> Turning one channel on must never turn another off.
    /// Tiles and units in particular are independent — the earlier per-feature toggles already were,
    /// and collapsing them into one "collider overlay" switch would have been a regression.</para>
    ///
    /// <para><b>Default is <see cref="None"/>: the game, and nothing else.</b> Several of these were
    /// previously always on in a development build (the room-streaming and object-pool readouts, the
    /// tile-query readout), which is how a debugging aid turns into permanent screen clutter that
    /// nobody can switch off.</para>
    /// </summary>
    [Flags]
    public enum DebugOverlayChannel
    {
        None = 0,

        /// <summary>Tile colliders, drawn as world-space wireframes. Sub-filtered by
        /// <see cref="ColliderDebugTileFilter"/>.</summary>
        Tiles = 1 << 0,

        /// <summary>Unit colliders, drawn as world-space wireframes. Sub-filtered by
        /// <see cref="ColliderDebugUnitFilter"/>.</summary>
        Units = 1 << 1,

        /// <summary>Door interaction triggers and door props.</summary>
        Doors = 1 << 2,

        /// <summary>Area trigger zones — event, passage (exit), teleport and hazard volumes.</summary>
        Triggers = 1 << 3,

        /// <summary>The room boundary edges that start a room transition.</summary>
        Transitions = 1 << 4,

        /// <summary>Map objects: containers, crates, barricades, terminals, fixtures.</summary>
        Objects = 1 << 5,

        /// <summary>The "[P2 Tile Collision Query]" readout — backend and active room.</summary>
        TileQuery = 1 << 6,

        /// <summary>The "Room Streaming Status" readout.</summary>
        RoomData = 1 << 7,

        /// <summary>The "Object Pool Status" readout.</summary>
        PoolData = 1 << 8,

        /// <summary>The SIM CLOCK readout.</summary>
        Clock = 1 << 9,

        /// <summary>The collider legend itself. Separately switchable so the drawn geometry can be
        /// screenshotted without the text plate over it.</summary>
        Legend = 1 << 10,

        /// <summary>
        /// The LowLevelPhysics2D (Box2D v3) chain mirror — the geometry a projectile actually sweeps
        /// against, drawn as world-space polylines.
        ///
        /// <para><b>Why this is its own channel and not part of <see cref="Tiles"/>.</b> They answer
        /// different questions and they can disagree. <see cref="Tiles"/> draws Unity's per-tile
        /// <c>Collider2D</c> grid, which owns its own interpretation of the slope and has ghost
        /// collisions at the seams between adjacent cells. The chain mirror is built from
        /// <c>ITileQueryService</c> instead, and it is what <c>TrySweepTiles</c> consults. When a bullet
        /// stops somewhere the tile boxes say is empty — or passes through somewhere they say is solid —
        /// the disagreement between these two drawings is the entire diagnosis, and until now only one
        /// half of it could be seen at all.</para>
        ///
        /// <para>Drawn from the vertices handed to Box2D, including the one-vertex lead-in and
        /// lead-out that <c>RoomChainGeometry.EmitChain</c> adds because Box2D discards an open
        /// chain's first and final edge. So the drawing shows slightly more than the collidable
        /// surface on purpose: it is what the engine was given.</para>
        /// </summary>
        LowLevelPhysics = 1 << 11,

        /// <summary>
        /// Floating damage numbers — one rising, fading figure per resolved hit, at the impact point.
        ///
        /// <para><b>Why this is a debug channel and not simply a game feature.</b> It is the only way to
        /// tell a bullet that <i>missed</i> from one that <i>never collided</i>: a hit produces a
        /// number (or <c>MISS</c>), and a shot that passes through a unit produces nothing at all.
        /// Those two look identical on screen without it, and they have completely different causes.
        /// Nothing in the simulation reads this channel, so it is free to leave on while diagnosing and
        /// free to leave off in a shipped build.</para>
        /// </summary>
        DamageNumbers = 1 << 12,

        /// <summary>
        /// Per-unit health, armour integrity and natural resistance, drawn above every unit in view.
        ///
        /// <para><b>Reads the target's own state, not the attacker's arithmetic</b> — the same
        /// <c>IDamageable</c> members the damage resolver reads — so a number that does not move when a
        /// bullet visibly lands is evidence about the hit path rather than about the formula. The
        /// training dummies restore to full every tick by design (<c>UnitTrain.control()</c>), so on
        /// them the useful reading is the <i>flash</i> of a drop, not a persistent value.</para>
        /// </summary>
        UnitHealth = 1 << 13,

        /// <summary>
        /// The enemy-AI perception overlay: what each enemy can <b>see</b>, <b>hear</b> and detect at
        /// close range, the line-of-sight ray it is actually testing, its target and last-known
        /// position, and a full readout of the selected unit's blackboard. Sub-filtered by
        /// <see cref="EnemyAIDebugFilter"/>; hotkey <b>F3</b>.
        ///
        /// <para><b>Why this is a channel and not a second F2-style front end.</b> It draws
        /// <i>world</i> geometry — cones, radii and rays anchored to real units — so it belongs to the
        /// mask, alongside tiles and unit colliders, where it can be switched on together with them and
        /// reached by <c>col on ai</c>. The F2 overlay is a pure panel with no world drawing, which is
        /// why nothing there goes through this mask.</para>
        ///
        /// <para><b>The three senses are drawn separately on purpose.</b> "It is chasing the player" is
        /// not evidence of which of vision, hearing or close-proximity fired, and those have completely
        /// different causes and fixes. An enemy that walks past the player, or one that stands beside
        /// the player without reacting, is diagnosable only if the three are separable on screen.</para>
        /// </summary>
        EnemyAI = 1 << 14,

        /// <summary>Everything. <see cref="Legend"/> included — it is a channel like the rest.</summary>
        All = Tiles | Units | Doors | Triggers | Transitions | Objects
            | TileQuery | RoomData | PoolData | Clock | Legend | LowLevelPhysics
            | DamageNumbers | UnitHealth | EnemyAI
    }

    /// <summary>
    /// Parsing, formatting and description for <see cref="DebugOverlayChannel"/>, shared by the
    /// developer console and every consumer so the vocabulary exists in exactly one place.
    ///
    /// <para><b>An unknown token is an error, never a silent skip.</b> The failure mode this avoids
    /// is specific and nasty: <c>col on trigger</c> quietly matching nothing leaves every overlay off,
    /// and "the overlay is on and shows nothing" is indistinguishable from the bug the overlay was
    /// opened to find. The reply names the token that was not understood.</para>
    /// </summary>
    public static class DebugOverlayChannels
    {
        /// <summary>All channel names, in the order they are listed to the user.</summary>
        private static readonly (DebugOverlayChannel Channel, string Name, string Hint)[] Catalogue =
        {
            (DebugOverlayChannel.Tiles,       "tiles",       "tile colliders (red wall / green platform-shelf / yellow stair)"),
            (DebugOverlayChannel.Units,       "units",       "unit colliders (cyan player / orange npc)"),
            (DebugOverlayChannel.Doors,       "doors",       "door interaction triggers and door props"),
            (DebugOverlayChannel.Triggers,    "triggers",    "area trigger zones: event, exit/passage, teleport, hazard"),
            (DebugOverlayChannel.Transitions, "transitions", "room boundary edges that start a room transition"),
            (DebugOverlayChannel.Objects,     "objects",     "map objects: containers, crates, barricades, terminals"),
            (DebugOverlayChannel.TileQuery,   "tilequery",   "the [P2 Tile Collision Query] text readout"),
            (DebugOverlayChannel.RoomData,    "room",        "the Room Streaming Status text readout"),
            (DebugOverlayChannel.PoolData,    "pool",        "the Object Pool Status text readout"),
            (DebugOverlayChannel.Clock,       "clock",       "the SIM CLOCK text readout"),
            (DebugOverlayChannel.Legend,      "legend",      "the collider legend plate"),
            (DebugOverlayChannel.LowLevelPhysics, "physics",   "the LowLevelPhysics2D (Box2D v3) chain mirror a projectile sweeps against"),
            (DebugOverlayChannel.DamageNumbers, "damage",      "floating damage numbers: a figure per landed hit, MISS per evaded one, nothing if the shot never collided"),
            (DebugOverlayChannel.UnitHealth,  "health",      "per-unit hp / armour / skin, drawn above every unit in view"),
            (DebugOverlayChannel.EnemyAI,     "ai",          "enemy perception: vision cone, hearing radius, close bubble, LOS ray, target + the selected unit's state (F3)"),
        };

        /// <summary>
        /// Parse a channel list. Empty means <see cref="DebugOverlayChannel.All"/>, so a bare
        /// <c>col on</c> reads as "everything".
        /// </summary>
        public static bool TryParse(string text, out DebugOverlayChannel channels, out string error)
        {
            channels = DebugOverlayChannel.All;
            error = null;

            if (string.IsNullOrWhiteSpace(text)) return true;

            string trimmed = text.Trim();
            if (IsAllToken(trimmed)) { channels = DebugOverlayChannel.All; return true; }
            if (IsOffToken(trimmed)) { channels = DebugOverlayChannel.None; return true; }

            DebugOverlayChannel accumulated = DebugOverlayChannel.None;

            foreach (string token in SplitTokens(trimmed))
            {
                if (IsAllToken(token)) { accumulated = DebugOverlayChannel.All; continue; }
                if (IsOffToken(token)) continue;

                if (!TryMatch(token, out DebugOverlayChannel channel))
                {
                    error = $"Unknown overlay '{token}'. Known overlays: {NameList()}.";
                    channels = DebugOverlayChannel.None;
                    return false;
                }

                accumulated |= channel;
            }

            channels = accumulated;
            return true;
        }

        /// <summary>
        /// Match one token to a channel, aliases included. Aliases are not politeness — they are what
        /// someone will actually type ("boxes" for the map objects, "exit" for the passage trigger),
        /// and a name that has to be looked up is a name that gets mistyped into a silent no-op.
        /// </summary>
        public static bool TryMatch(string token, out DebugOverlayChannel channel)
        {
            switch (Normalize(token))
            {
                case "tiles": case "tile": case "t":
                    channel = DebugOverlayChannel.Tiles; return true;

                case "units": case "unit": case "u":
                case "npc": case "npcs": case "nps": case "mobs":
                    channel = DebugOverlayChannel.Units; return true;

                case "doors": case "door":
                    channel = DebugOverlayChannel.Doors; return true;

                case "triggers": case "trigger":
                case "areas": case "area":
                case "exit": case "exits":
                case "zones": case "zone":
                    channel = DebugOverlayChannel.Triggers; return true;

                case "transitions": case "transition":
                case "edges": case "edge":
                case "bounds": case "boundary":
                    channel = DebugOverlayChannel.Transitions; return true;

                case "objects": case "object":
                case "boxes": case "box":
                case "crates": case "crate":
                case "props": case "prop":
                case "barricades": case "containers":
                    channel = DebugOverlayChannel.Objects; return true;

                // Note: "help" is deliberately NOT an alias here. The console's shortcut dispatcher
                // matches `help` as a verb before it ever reaches this matcher, so an alias for it
                // could only ever be a name that does not do what it says.
                case "tilequery": case "query": case "colliderhelp":
                    channel = DebugOverlayChannel.TileQuery; return true;

                case "room": case "rooms": case "roomdata": case "streaming":
                    channel = DebugOverlayChannel.RoomData; return true;

                case "pool": case "pooldata": case "pooling":
                    channel = DebugOverlayChannel.PoolData; return true;

                case "clock": case "simclock": case "sim": case "fps":
                    channel = DebugOverlayChannel.Clock; return true;

                case "legend": case "key":
                    channel = DebugOverlayChannel.Legend; return true;

                // The LowLevelPhysics2D chain mirror. "physics" is the name the request used; the rest
                // are what someone who has read the code will actually type. "chain"/"chains" because
                // that is the Box2D shape kind, "box2d" because that is the engine, "llp2d" because
                // that is what every doc in the repo calls the system.
                case "physics": case "phys":
                case "llp2d": case "lowlevelphysics": case "lowlevelphysics2d":
                case "chain": case "chains":
                case "box2d":
                    channel = DebugOverlayChannel.LowLevelPhysics; return true;

                // Floating damage numbers. "dmg" is the abbreviation the request itself used; "miss"
                // and "hit" are what someone types when they are chasing exactly the bug this overlay
                // was added for, and both are free of collisions with the verb list.
                case "damage": case "damagenumbers": case "damagenumber":
                case "dmg": case "numbers": case "number":
                case "hit": case "hits": case "miss": case "misses":
                    channel = DebugOverlayChannel.DamageNumbers; return true;

                // Per-unit health. "hp" is deliberately NOT aliased: `heal/hp` is a console verb, and
                // an alias a verb shadows is a name that can never fire (see the `help` note above).
                case "health": case "hpbar": case "healthbar": case "healthbars":
                case "bars": case "bar":
                    channel = DebugOverlayChannel.UnitHealth; return true;

                // The enemy-AI perception overlay. "ai" is the canonical name (and the console verb);
                // "enemy"/"enemies" are claimed here rather than by the Units channel because that
                // channel is about COLLIDERS while this one is the enemy-specific tool, and "enemy" is
                // what someone reaches for when they mean the latter. "brain" is the type name.
                case "ai": case "enemyai": case "enemy-ai":
                case "enemy": case "enemies":
                case "brain": case "brains":
                    channel = DebugOverlayChannel.EnemyAI; return true;

                default:
                    channel = DebugOverlayChannel.None;
                    return false;
            }
        }

        /// <summary>The canonical name of one channel (the first spelling in the catalogue).</summary>
        public static string NameOf(DebugOverlayChannel channel)
        {
            for (int i = 0; i < Catalogue.Length; i++)
            {
                if (Catalogue[i].Channel == channel) return Catalogue[i].Name;
            }
            return channel.ToString().ToLowerInvariant();
        }

        /// <summary>Render a mask as the comma-separated list <see cref="TryParse"/> accepts back.</summary>
        public static string Format(DebugOverlayChannel channels)
        {
            if (channels == DebugOverlayChannel.None) return "off";
            if (channels == DebugOverlayChannel.All) return "all";

            var sb = new StringBuilder();
            for (int i = 0; i < Catalogue.Length; i++)
            {
                if ((channels & Catalogue[i].Channel) == 0) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(Catalogue[i].Name);
            }

            return sb.Length > 0 ? sb.ToString() : "off";
        }

        /// <summary>Just the names, for an error message.</summary>
        public static string NameList()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Catalogue.Length; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(Catalogue[i].Name);
            }
            return sb.ToString();
        }

        /// <summary>One line per channel: name, on/off, and what it draws.</summary>
        public static string DescribeAll(DebugOverlayChannel enabled)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Catalogue.Length; i++)
            {
                bool on = (enabled & Catalogue[i].Channel) != 0;
                sb.Append(on ? "  [x] " : "  [ ] ")
                  .Append(Catalogue[i].Name.PadRight(12))
                  .Append(Catalogue[i].Hint);
                if (i < Catalogue.Length - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Usage text for the console's <c>help</c> and for a bad argument.</summary>
        public static string Usage()
        {
            return "col on [list]   - turn overlays on (no list = all)\n" +
                   "col off [list]  - turn overlays off (no list = all)\n" +
                   "col             - list every overlay and whether it is on\n" +
                   "list = " + NameList();
        }

        // ── Internals ────────────────────────────────────────────────────────

        /// <summary>
        /// Case and surrounding whitespace are not information here. C# string <c>switch</c> is
        /// case-sensitive, so without this <c>col on Tiles</c> fails with "Unknown overlay 'Tiles'"
        /// and reads as a typo the user cannot see. Tokens are normalised once, here, rather than in
        /// every call site.
        /// </summary>
        private static string Normalize(string token)
        {
            return token?.Trim().ToLowerInvariant();
        }

        private static bool IsAllToken(string token)
        {
            string t = Normalize(token);
            return t == "all" || t == "*" || t == "everything" || t == "on";
        }

        private static bool IsOffToken(string token)
        {
            string t = Normalize(token);
            return t == "off" || t == "none" || t == "0" || t == "false" || t == "hide";
        }

        private static string[] SplitTokens(string text)
        {
            return text.Split(new[] { ',', ';', '+', '|', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
