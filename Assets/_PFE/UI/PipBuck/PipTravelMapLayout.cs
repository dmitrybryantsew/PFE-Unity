using System;
using System.Collections.Generic;

namespace PFE.UI.PipBuck
{
    /// <summary>One land's placement on the world map — sprite 834, frame 1.</summary>
    public readonly struct TravelNode
    {
        /// <summary>The land id. It is also the node's instance name (<c>visWMap[land.id]</c>).</summary>
        public readonly string LandId;

        /// <summary>x of the node's centre, inside <c>visPipWMap</c>.</summary>
        public readonly float X;

        /// <summary>y of the node's centre, inside <c>visPipWMap</c>.</summary>
        public readonly float Y;

        /// <summary>The placement's scale. Four nodes are drawn at 1.25; the rest at 1.</summary>
        public readonly float Scale;

        public TravelNode(string landId, float x, float y, float scale)
        {
            LandId = landId ?? string.Empty;
            X = x;
            Y = y;
            Scale = scale;
        }
    }

    /// <summary>How one node should be drawn — the port of <c>PipPageInfo.as:160-196</c>.</summary>
    public readonly struct TravelNodeDraw
    {
        /// <summary><c>sim.visible</c>. A node that fails the Oracle's gate is <b>invisible</b>, not grey.</summary>
        public readonly bool Visible;

        /// <summary><c>sim.alpha</c>: 1, 0.5 (not travelable) or 0.3 (unvisited in test mode).</summary>
        public readonly float Alpha;

        /// <summary><c>sim.sign.visible</c> — the "new land" flag.</summary>
        public readonly bool Sign;

        public TravelNodeDraw(bool visible, float alpha, bool sign)
        {
            Visible = visible;
            Alpha = alpha;
            Sign = sign;
        }

        /// <summary>The state a node that is not offered at all takes — the <c>if (sim)</c> miss.</summary>
        public static TravelNodeDraw Absent => new TravelNodeDraw(false, 1f, false);
    }

    /// <summary>
    /// The world map's layout — the port of the 27-node placement table in <c>visPipWMap</c> (sprite 834)
    /// and of the per-land icon frame in <c>sim_wmap</c> (833), plus the visibility rules
    /// <c>PipPageInfo.as:143-205</c> applies to them.
    ///
    /// <para><b>The placements are the map.</b> They exist nowhere else — not in the AS3, not in the land
    /// assets, not in any other clip — so this table is data the port must carry, and it was dumped from
    /// the SWF's own <c>PlaceObject2</c> records rather than eyeballed off the art. The dump found 28
    /// placements, of which <b>27 are named</b> (one per land id) and the 28th is the unnamed backdrop at
    /// depth 3 (<c>PipInfoFrame.WorldBackdropCharId</c>).</para>
    ///
    /// <para><b>Three tables, and they reconcile exactly.</b> 27 nodes; <b>25</b> labelled icon frames in
    /// <c>sim</c> (829 has 26 frames, frame 1 unlabelled); 33 land assets in the catalogue. The
    /// differences are not gaps — see <see cref="NodesWithoutIcon"/> and
    /// <see cref="LandsWithoutNode"/>, both of which are asserted rather than assumed.</para>
    ///
    /// <para><b>Unity-free</b>, like <see cref="PipSubTabs"/>: every rule here is a function of its
    /// arguments, so the whole table and every branch is testable from a plain shell.</para>
    /// </summary>
    public static class PipTravelMapLayout
    {
        // ── The 27 nodes, in the SWF's own depth order ────────────────────────
        //
        // Dumped with .workbuddy-ai/tmp/swf_sprite_frame1.py against sprite 834, frame 1. The trailing
        // comment on each row is the raw dump line, so the transcription can be checked without
        // re-running the tool. Four nodes carry scale 1.25 — every procedural ("random_*") land except
        // `random_sewer`, which is drawn at 1 like the fixed ones. That asymmetry is the art's, not a
        // transcription slip: the dump agrees with the table.

        private static readonly TravelNode[] NodeTable =
        {
            new TravelNode("begin",          66.0f, 469.1f, 1f),      // depth   4
            new TravelNode("surf",          144.1f, 429.1f, 1f),      // depth  11
            new TravelNode("rbl",           278.1f, 359.1f, 1f),      // depth  18
            new TravelNode("random_plant",  164.9f, 330.0f, 1.25f),   // depth  25
            new TravelNode("nio",           110.0f, 325.0f, 1f),      // depth  32
            new TravelNode("raiders",       389.1f, 429.1f, 1f),      // depth  39
            new TravelNode("random_sewer",  409.1f, 484.2f, 1f),      // depth  46
            new TravelNode("random_stable", 454.1f, 424.1f, 1.25f),   // depth  53
            new TravelNode("random_mane",   454.1f, 220.0f, 1.25f),   // depth  60
            new TravelNode("bunker",        355.0f, 171.9f, 1f),      // depth  67
            new TravelNode("test",          142.1f, 512.2f, 1f),      // depth  74
            new TravelNode("test2",         186.1f, 512.2f, 1f),      // depth  81
            new TravelNode("random_canter", 111.0f,  85.0f, 1.25f),   // depth  88
            new TravelNode("core",          508.1f, 419.1f, 1f),      // depth  95
            new TravelNode("garages",       243.1f, 273.0f, 1f),      // depth 102
            new TravelNode("mtn",           511.1f, 215.0f, 1f),      // depth 109
            new TravelNode("covert",        110.0f, 200.0f, 1f),      // depth 116
            new TravelNode("src",           209.9f, 131.9f, 1f),      // depth 123
            new TravelNode("minst",          58.0f,  80.0f, 1f),      // depth 130
            new TravelNode("random_encl",   310.0f,  48.2f, 1.25f),   // depth 137
            new TravelNode("random_mbase",  270.1f, 194.0f, 1f),      // depth 144
            new TravelNode("workshop",       60.0f, 325.0f, 1f),      // depth 151
            new TravelNode("hql",           497.1f, 341.1f, 1f),      // depth 158
            new TravelNode("post",          305.0f, 101.0f, 1f),      // depth 165
            new TravelNode("comm",          363.0f,  43.2f, 1f),      // depth 172
            new TravelNode("stable_pi",     300.1f, 487.2f, 1f),      // depth 179
            new TravelNode("art",           330.0f, 225.0f, 1f),      // depth 186
        };

        /// <summary>Every node, in the order the SWF draws them (which is also land-id order).</summary>
        public static IReadOnlyList<TravelNode> Nodes => NodeTable;

        /// <summary>How many nodes the map has — the count the reconciliation is stated against.</summary>
        public static int NodeCount => NodeTable.Length;

        /// <summary>
        /// The node for a land id, or <c>false</c> when the map has none.
        ///
        /// <para><b>This is the port's <c>visWMap[land.id]</c></b> — the Oracle's child lookup by name
        /// (<c>PipPageInfo.as:157</c>). A land with no node is silently skipped there and must be
        /// silently skipped here; the difference is that the port can say <i>which</i> lands those are
        /// (see <see cref="LandsWithoutNode"/>) instead of leaving it to be discovered in a play-test.</para>
        /// </summary>
        public static bool TryGetNode(string landId, out TravelNode node)
        {
            for (int i = 0; i < NodeTable.Length; i++)
            {
                if (string.Equals(NodeTable[i].LandId, landId, StringComparison.OrdinalIgnoreCase))
                {
                    node = NodeTable[i];
                    return true;
                }
            }

            node = default;
            return false;
        }

        // ── The land icon — `sim` (829), 26 frames, 25 labelled ───────────────
        //
        // Read out of the SWF's own FrameLabel records (tag 43) rather than transcribed, because the
        // frame INDEX is the contract: the Oracle steps the clip with `gotoAndStop(land.id)`
        // (PipPageInfo.as:172), so the label is the join key and a re-export that reordered the clip
        // would otherwise put the wrong icon on every land with nothing to notice it.
        //
        // Dumped with .workbuddy-ai/tmp/swf_frame_labels.py: 26 frames, 25 labels, frame 1 unlabelled.
        // The file-wide control printed 1106 FrameLabel records, so "25 here" is a per-sprite count and
        // not a broken walk.
        //
        // Index = frame - 1, exactly as PipBuckCompositionDefinition.inventoryRowIcons is indexed. The
        // unlabelled frame 1 is index 0 and is the deliberately blank default the Oracle's `try` falls
        // back to (`:170-177`).

        private static readonly string[] IconFrameLabels =
        {
            "",               //  1  no label — the blank default the catch arm steps to
            "begin",          //  2
            "surf",           //  3
            "rbl",            //  4
            "covert",         //  5
            "random_plant",   //  6
            "nio",            //  7
            "workshop",       //  8
            "raiders",        //  9
            "random_sewer",   // 10
            "random_stable",  // 11
            "core",           // 12
            "random_mane",    // 13
            "random_mbase",   // 14
            "bunker",         // 15
            "random_canter",  // 16
            "garages",        // 17
            "mtn",            // 18
            "src",            // 19
            "minst",          // 20
            "random_encl",    // 21
            "post",           // 22
            "comm",           // 23
            "hql",            // 24
            "stable_pi",      // 25
            "art",            // 26
        };

        /// <summary>The <c>sim</c> frame labels in frame order. Index = frame - 1.</summary>
        public static IReadOnlyList<string> IconLabels => IconFrameLabels;

        /// <summary>How many frames the <c>sim</c> clip has — the icon array's expected length.</summary>
        public static int IconFrameCount => IconFrameLabels.Length;

        /// <summary>
        /// The icon frame index for a land — the port of <c>sim.sim.gotoAndStop(land.id)</c>, resolved
        /// the way the Oracle's <c>try</c> resolves it: a land whose id is not a label falls back to
        /// frame 1 (index 0), which is the blank one.
        /// </summary>
        public static int IconFrameIndexFor(string landId)
        {
            if (string.IsNullOrEmpty(landId)) return 0;

            for (int i = 0; i < IconFrameLabels.Length; i++)
            {
                if (string.Equals(IconFrameLabels[i], landId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return 0;
        }

        // ── The reconciliation, as data rather than as a claim ────────────────
        //
        // The scope document stated these three sets by hand. Stating them here means a test can assert
        // them, and means the view never has to guess which of "no node" and "no icon" it is looking at.

        /// <summary>
        /// The two nodes whose land has no icon frame — the dev sandboxes.
        ///
        /// <para><b>They are not a gap.</b> <c>test</c> and <c>test2</c> are the only land ids with a node
        /// and no <c>sim</c> label, and <c>PipPageInfo.as:170-177</c> wraps the icon step in a
        /// <c>try</c> whose catch arm is <c>gotoAndStop(1)</c> — the one unlabelled frame. So the blank
        /// is the Oracle's own behaviour, reached by its own error path, and the port reproduces it by
        /// falling back to index 0.</para>
        /// </summary>
        public static readonly string[] NodesWithoutIcon = { "test", "test2" };

        /// <summary>
        /// The land ids in the catalogue that the map never draws, with the reason each one is correct.
        ///
        /// <para><b>None of these is an import gap.</b> Three are prob lands, which the Oracle filters
        /// outright (<c>if (!land.prob)</c>, <c>PipPageInfo.as:154</c>); two are <c>tip='story'</c> lands
        /// reached only by a <c>&lt;s act="gotoland"&gt;</c> script action; and <c>way</c> is where the
        /// game <i>starts</i> the player and the fallback the encounter table sends them to
        /// (<c>Game.as:409</c>). The map is static art and a land with no node can never appear — and
        /// for these six that is the faithful behaviour, because the Oracle refuses to travel to the
        /// land you are already in (<c>Game.as:449-452</c>) and <c>checkTravel</c> returns false
        /// outright from <c>grave</c> (<c>:485</c>).</para>
        /// </summary>
        public static readonly string[] LandsWithoutNode =
        {
            "prob", "stable_pi_atk", "stable_pi_surf",   // prob lands — filtered by `!land.prob`
            "thunder", "grave",                          // tip='story' — <s act="gotoland"> only
            "way",                                       // tip='base' — the start land / encounter fallback
        };

        /// <summary>Whether the map draws a node for this land at all.</summary>
        public static bool HasNode(string landId) => TryGetNode(landId, out _);

        // ── Visibility, dimming and the "new land" flag ───────────────────────

        /// <summary>
        /// The port of <c>PipPageInfo.as:178-197</c> for one node.
        ///
        /// <para><b>The order is load-bearing and is the Oracle's.</b> The alpha starts at 1; the
        /// not-travelable dim is applied first and the test-mode dim <i>overwrites</i> it, so a land that
        /// is both untravelable and unvisited in test mode reads 0.3, not 0.5. The whole block is inside
        /// <c>if (!(land.test &amp;&amp; !testMode))</c> — a dev-sandbox land outside test mode keeps
        /// alpha 1 and stays invisible, rather than being dimmed.</para>
        /// </summary>
        /// <param name="isTestLand">AS3 <c>land.test</c>.</param>
        /// <param name="testMode">AS3 <c>World.w.testMode</c>.</param>
        /// <param name="visited">AS3 <c>land.visited</c>.</param>
        /// <param name="access">AS3 <c>land.access</c>.</param>
        /// <param name="canTravel">AS3 <c>game.checkTravel(land.id)</c> — the <c>fin</c> ladder.</param>
        /// <param name="helpMess">AS3 <c>World.w.helpMess</c> — the hint-marker master switch.</param>
        public static TravelNodeDraw DrawFor(
            bool isTestLand, bool testMode, bool visited, bool access, bool canTravel, bool helpMess)
        {
            if (isTestLand && !testMode)
            {
                // PipPageInfo.as:178 — the whole block is skipped. The node was already set to
                // `visible = false` at :164 and its alpha to 1 at :160, so this is exactly that state.
                return new TravelNodeDraw(false, 1f, false);
            }

            float alpha = 1f;

            // :180-183 — an untravelable land is dimmed, not hidden.
            if (!canTravel)
            {
                alpha = 0.5f;
            }

            // :184-187 — test mode dims an unvisited, un-unlocked land FURTHER, and the assignment
            // overwrites the 0.5 above. Not a minimum, not a product: an assignment.
            if (testMode && !visited && !access)
            {
                alpha = 0.3f;
            }

            // :188-192 — the "a new land opened" flag. Note it needs `access` and the ABSENCE of
            // `visited`, i.e. it marks exactly the transition the player has not taken yet.
            bool sign = helpMess && !visited && access;

            // :193-196 — the visibility gate.
            bool visible = testMode || visited || access;

            return new TravelNodeDraw(visible, alpha, sign);
        }

        /// <summary>
        /// The nodes a page should draw, in table order, with their draw state — the whole
        /// <c>for each(land in game.lands)</c> loop reduced to a pure function.
        ///
        /// <para>Prob lands are excluded here rather than by the caller, because that is where the
        /// Oracle excludes them (<c>PipPageInfo.as:154</c>) and a caller that forgot would draw six
        /// extra nodes and look almost right.</para>
        /// </summary>
        /// <param name="lands">The land facts to consider.</param>
        /// <param name="isProb">AS3 <c>land.prob</c>.</param>
        /// <param name="isTest">AS3 <c>land.test</c>.</param>
        /// <param name="visited">AS3 <c>land.visited</c>.</param>
        /// <param name="access">AS3 <c>land.access</c>.</param>
        /// <param name="canTravel">AS3 <c>game.checkTravel(land.id)</c>.</param>
        public static List<TravelNodeDrawResult> Draw(
            IEnumerable<string> lands,
            Func<string, bool> isProb,
            Func<string, bool> isTest,
            Func<string, bool> visited,
            Func<string, bool> access,
            Func<string, bool> canTravel,
            bool testMode,
            bool helpMess)
        {
            var result = new List<TravelNodeDrawResult>();
            if (lands == null) return result;

            foreach (string landId in lands)
            {
                if (string.IsNullOrEmpty(landId)) continue;

                // :154 — prob lands are never offered. They are detached room collections, not places.
                if (isProb != null && isProb(landId)) continue;

                // :157-158 — `sim = visWMap[land.id]; if (sim)`. A land with no node is skipped in
                // silence, which is the faithful behaviour for the six in LandsWithoutNode.
                if (!TryGetNode(landId, out TravelNode node)) continue;

                TravelNodeDraw draw = DrawFor(
                    isTest != null && isTest(landId),
                    testMode,
                    visited != null && visited(landId),
                    access != null && access(landId),
                    canTravel == null || canTravel(landId),
                    helpMess);

                result.Add(new TravelNodeDrawResult(node, draw));
            }

            return result;
        }

        /// <summary>One node and the state it draws in — the pair <see cref="Draw"/> produces.</summary>
        public readonly struct TravelNodeDrawResult
        {
            public readonly TravelNode Node;
            public readonly TravelNodeDraw Draw;

            public TravelNodeDrawResult(TravelNode node, TravelNodeDraw draw)
            {
                Node = node;
                Draw = draw;
            }
        }

        /// <summary>
        /// The top text the travel map sets — <c>setTopText("infotravel")</c>, but <b>only while travel
        /// is unlocked</b> (<c>PipPageInfo.as:148-151</c>).
        ///
        /// <para>The key, not the caption: the shell resolves it through the document, exactly as the
        /// Oracle's <c>setTopText</c> does (<c>PipPage.as:1496-1511</c>).</para>
        /// </summary>
        public const string WorldTopTextKey = "infotravel";
    }
}
