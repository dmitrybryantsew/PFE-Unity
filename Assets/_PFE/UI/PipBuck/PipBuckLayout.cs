using System;
using UnityEngine;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// Where a number in this file came from. Carried per row so a reviewer can tell an attested
    /// Oracle coordinate from a port invention without leaving the file.
    ///
    /// <para><b>Why this is not a comment.</b> The HUD work (Item I4) established the rule: a row that
    /// was invented must never be quoted later as if it were cited. A comment rots; an enum field is
    /// read by <see cref="PipBuckLayout.Describe"/> and shows up in the log, so the claim travels with
    /// the number.</para>
    /// </summary>
    public enum PipProvenance
    {
        /// <summary>Read out of the SWF's own <c>PlaceObject2</c> record for sprite 1155.</summary>
        OracleSwfSymbol,

        /// <summary>Assigned by Oracle ActionScript, cited by <c>file:line</c>.</summary>
        OracleCode,

        /// <summary>The port's own choice. No Oracle coordinate exists. Do not cite as evidence.</summary>
        PortChoice,
    }

    /// <summary>One element's resting position in the device's own coordinate space.</summary>
    public readonly struct PipAnchor
    {
        public PipAnchor(Vector2 position, PipProvenance provenance, string citation)
        {
            Position = position;
            Provenance = provenance;
            Citation = citation;
        }

        /// <summary>Position in the Oracle's 1200x800 device space, +x right, +y <b>down</b>.</summary>
        public Vector2 Position { get; }

        public PipProvenance Provenance { get; }

        /// <summary>The <c>file:line</c> (or the SWF read) this number came from.</summary>
        public string Citation { get; }

        /// <summary>
        /// Flash's y axis points down, Unity's <c>anchoredPosition</c> points up. Every caller that
        /// places a child under the device root goes through this, so the flip exists in exactly one
        /// place — the I4 work already showed what happens when two owners disagree about a sign.
        /// </summary>
        public Vector2 ToUnityAnchoredPosition()
        {
            return new Vector2(Position.x, -Position.y);
        }
    }

    /// <summary>Every element the device shell places itself.</summary>
    public enum PipElement
    {
        /// <summary>The flat <c>visPipBuck</c> render — casing, screen, dial, bezel chrome.</summary>
        Backdrop,

        /// <summary><c>skin</c> — the screen's inner surface.</summary>
        Skin,

        /// <summary><c>fon</c> — the phosphor background behind the pages.</summary>
        Fon,

        /// <summary><c>pipError</c> — the "no power" overlay <c>supply(-1)</c> shows.</summary>
        PipError,

        /// <summary><c>but0</c> — the Close tab. Sits alone at the bottom of the device.</summary>
        CloseTab,

        Tab1,
        Tab2,
        Tab3,
        Tab4,
        Tab5,

        /// <summary><c>butHelp</c> — the "?" button that shows <c>helpText</c>.</summary>
        HelpButton,

        /// <summary><c>butMass</c> — the weight button that shows <c>massText</c>.</summary>
        MassButton,

        /// <summary><c>toptext</c> — the page's title strip.</summary>
        TopText,

        /// <summary><c>pr</c> — the right-hand 15-row status monitor's parent.</summary>
        StatusPanel,

        /// <summary>The page container <c>PipPage.vis</c>.</summary>
        PageRoot,

        /// <summary><c>visPipHelp</c> — the hover help popup.</summary>
        HelpPopup,

        /// <summary><c>visSetKey</c> — the key-capture popup.</summary>
        SetKeyPopup,
    }

    /// <summary>
    /// The PipBuck device's geometry: the port of <c>PipBuck.resizeScreen</c> plus the resting
    /// position of every element the shell places itself.
    ///
    /// <para><b>Where the positions come from.</b> They are read out of the SWF, not measured off the
    /// exported PNG. The FFDec sprite export writes <c>visPipBuck</c> as a single flat 1920x1080 PNG
    /// with no placement data, so the art cannot answer "where is <c>but1</c>" — but the SWF's own
    /// <c>PlaceObject2</c> records can, and they do:
    /// <c>.workbuddy-ai/tmp/swf_sprite_frame1.py</c> dumps sprite 1155's frame-1 display list out of
    /// the XML a previous session produced. All 14 placements carry an instance name
    /// (<c>getChildByName</c> targets) and all 14 matrices decode, so the read is complete rather
    /// than partial.</para>
    ///
    /// <para><b>The backdrop offset is the symbol's bounds minimum, computed rather than
    /// measured.</b> The SWF gives child positions relative to the symbol's registration point, but
    /// the PNG's (0,0) is the symbol's <i>bounds minimum</i>, so <see cref="BackdropOffset"/> is
    /// that minimum. It is derived by unioning the 14 frame-1 placements' child bounds (each shape's
    /// <c>shapeBounds</c>, each bitmap's width/height, translated by its matrix): the union is
    /// <c>(-360, -137) .. (1560, 943)</c>, i.e. exactly 1920x1080 — the PNG's own size, which is the
    /// independent confirmation. <c>skin</c> (1138) is the outer term on both axes: a 1920x1080
    /// shape centred on its placement (600, 403).</para>
    ///
    /// <para><b>Correction, 2026-10-13.</b> The y term used to read 140, "measured" as <c>toptext</c>
    /// (symbol y=5) landing on the title text at PNG y=145. That is off by 3 px because
    /// <c>toptext</c> is a <c>DefineEditText</c> whose rendered glyphs sit below its registration
    /// point — its bounds are y[-0.5..73.5], so the visible cap line is ~3 px in. A second, wholly
    /// independent method agrees with the bounds computation: cross-correlating <c>ButPage</c>'s
    /// standalone export against the composite puts its art at PNG (376, 271), i.e. the symbol origin
    /// at (360.5, 137.0). Both say 137, so the artwork was being drawn 3 px above the port's own
    /// overlays (the tabs, the title, the status panel).</para>
    ///
    /// <para><b>The tabs are a vertical column on the left, not a top bar.</b> The interface
    /// specification's "6 top buttons" and "Top Nav Tabs" are wrong. <c>but1..but5</c> sit at
    /// x=17 with y = 135.5, 213.5, 293.5, 373.5, 453.5 — a left-hand column with an ~80 px pitch —
    /// and <c>but0</c> (Close) is alone at (30, 693.5), near the bottom. The exported bezel agrees:
    /// five labelled buttons stacked at the left and a sixth at the bottom. This was verified against
    /// the art <i>after</i> reading the SWF, and the two agree to the pixel.</para>
    /// </summary>
    public static class PipBuckLayout
    {
        // ── The device's virtual screen ───────────────────────────────────────
        //
        // PipBuck.visX / visY (PipBuck.as:35-37). These are the numbers resizeScreen centres, and
        // they are the canvas reference resolution — NOT the artwork's bounds, which are larger.

        public const float ReferenceWidth = 1200f;
        public const float ReferenceHeight = 800f;

        public static Vector2 ReferenceSize => new Vector2(ReferenceWidth, ReferenceHeight);

        /// <summary>Canvas match mode. Same 0.5 as the HUD: neither axis is privileged.</summary>
        public const float MatchWidthOrHeight = 0.5f;

        // ── The backdrop PNG's origin inside the device space ─────────────────

        /// <summary>
        /// Where the flat <c>visPipBuck</c> PNG's top-left corner sits in device space — which is the
        /// symbol's bounds minimum, because the PNG's (0,0) <i>is</i> the bounds minimum. Negative on
        /// both axes: the artwork is 1920x1080 around a 1200x800 screen, so it hangs outside on all
        /// four sides. See the class summary for the derivation and for the 2026-10-13 correction of
        /// the y term (140 -> 137).
        /// </summary>
        public static readonly Vector2 BackdropOffset = new Vector2(-360f, -137f);

        public static readonly Vector2 BackdropSize = new Vector2(1920f, 1080f);

        // ── Element geometry ──────────────────────────────────────────────────

        /// <summary><c>ButPage</c> symbol 1143, exported 132x69 (frame 1 = normal, 2 = active).</summary>
        public static readonly Vector2 TabButtonSize = new Vector2(132f, 69f);

        /// <summary>The six tab slots, in the order the device draws them.</summary>
        public static readonly PipElement[] TabSlots =
        {
            PipElement.CloseTab,
            PipElement.Tab1,
            PipElement.Tab2,
            PipElement.Tab3,
            PipElement.Tab4,
            PipElement.Tab5,
        };

        /// <summary>
        /// Where a tab button's art lands <b>inside the backdrop PNG</b>, in the PNG's own pixel
        /// space: <c>png = symbol - BackdropOffset</c>, less the canvas inset.
        ///
        /// <para><b>Why this exists.</b> FFDec's sprite export flattens <c>visPipBuck</c>'s display
        /// list, so the six <c>ButPage</c> instances were rasterised into <c>PipBuck_Bezel.png</c> —
        /// including the two design-time <c>DefineEditText</c> payloads ("Инвентарь" / "1") that
        /// ActionScript overwrites at runtime. This rect is where they were, and it is what the
        /// importer's <c>Verify</c> inspects to prove they are gone.</para>
        ///
        /// <para><b>The canvas inset matters.</b> A placement gives the symbol's registration point,
        /// but <see cref="PipBuckTabArt"/>'s canvas is the shape padded by half the stroke width, so
        /// the registration point sits at (1.5, 1.5) of the canvas. Without the inset the rect is
        /// 1.5 px down-right of the art it is supposed to describe — which the pixel check would
        /// report as a one-pixel miss rather than as a wrong number.</para>
        /// </summary>
        public static Rect BackdropFootprint(PipElement tab)
        {
            Vector2 anchor = AnchorFor(tab).Position - BackdropOffset;
            Vector2 canvasOrigin = new Vector2(anchor.x - PipBuckTabArt.CanvasOriginInset,
                                               anchor.y - PipBuckTabArt.CanvasOriginInset);
            return new Rect(canvasOrigin.x, canvasOrigin.y, TabButtonSize.x, TabButtonSize.y);
        }

        /// <summary>
        /// How far inside a footprint the button's own outline is guaranteed not to reach.
        /// <c>ButPage</c>'s border is <c>#00ff99</c> — 1 px on frame 1, 3 px on frame 2 — so a check
        /// asking "is this region free of that colour?" must stay clear of the border. 8 px does, for
        /// both frames.
        /// </summary>
        public const float TabArtInteriorInset = 8f;

        /// <summary>
        /// The interior of a footprint: the region a baked caption would occupy, and the only part of
        /// the button that is neither stroke nor rounded-corner background.
        /// </summary>
        public static Rect BackdropFootprintInterior(PipElement tab)
        {
            Rect r = BackdropFootprint(tab);
            return new Rect(r.x + TabArtInteriorInset, r.y + TabArtInteriorInset,
                            r.width - 2f * TabArtInteriorInset, r.height - 2f * TabArtInteriorInset);
        }

        /// <summary><c>visPipRItem</c> symbol 1129, exported 369x25. One row of the status panel.</summary>
        public static readonly Vector2 MonitorRowSize = new Vector2(369f, 25f);

        // Status panel rows: PipBuck.as:131-144.
        public const int MonitorRowCount = 15;          // kolRItems = 15
        public const float MonitorRowX = 5f;
        public const float MonitorRowY0 = 40f;
        public const float MonitorRowPitch = 30f;

        // Page rows: PipPage.as:109-113.
        public const int PageRowCount = 18;             // maxrows = 18
        public const float PageRowX = 30f;
        public const float PageRowY0 = 100f;
        public const float PageRowPitch = 30f;

        /// <summary>Top-level tab count — <c>kolPages = 5</c> (PipBuck.as:41).</summary>
        public const int TopTabCount = 5;

        /// <summary>
        /// The "?" (<c>butHelp</c>) and weight (<c>butMass</c>) buttons' hit box. A port choice with one
        /// constraint the Oracle itself supplies: the two placements are 59 px apart (x 17 and x 76), so
        /// neither button can be wider than that without overlapping the other — 52 leaves a 7 px gutter.
        /// Both buttons' art is baked into the bezel composite, so these are hit areas over it, the same
        /// arrangement the page tabs use, not art of their own.
        /// </summary>
        public static readonly Vector2 HelpButtonSize = new Vector2(52f, 52f);

        /// <summary>
        /// The resting positions, in the Oracle's 1200x800 device space. Every row cites its source;
        /// see <see cref="PipProvenance"/> for why that is a field and not a comment.
        /// </summary>
        private static readonly PipAnchor[] Table =
        {
            // The SWF read: sprite 1155 frame 1, 14 placements, twips/20.
            new PipAnchor(new Vector2(600f, 403f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 8, name='skin'"),
            new PipAnchor(new Vector2(599.5f, 399.5f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 2, name='fon'"),
            new PipAnchor(new Vector2(620f, 390.8f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 5, name='pipError'"),
            new PipAnchor(new Vector2(30f, 693.5f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 22, name='but0'"),
            new PipAnchor(new Vector2(17f, 135.5f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 10, name='but1'"),
            new PipAnchor(new Vector2(17f, 213.5f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 14, name='but2'"),
            new PipAnchor(new Vector2(17f, 293.5f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 18, name='but3'"),
            new PipAnchor(new Vector2(17f, 373.5f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 26, name='but4'"),
            new PipAnchor(new Vector2(17f, 453.5f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 30, name='but5'"),
            new PipAnchor(new Vector2(17f, 531.9f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 34, name='butHelp'"),
            new PipAnchor(new Vector2(76f, 531.9f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 37, name='butMass'"),
            new PipAnchor(new Vector2(505.4f, 5f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 41, name='toptext'"),
            new PipAnchor(new Vector2(1105.7f, 135.5f), PipProvenance.OracleSwfSymbol,
                "SWF sprite 1155 depth 44, name='pr'"),

            // Assigned by ActionScript rather than by the symbol.
            new PipAnchor(new Vector2(165f, 72f), PipProvenance.OracleCode,
                "PipPage.as:89-90 — this.vis.x = 165; this.vis.y = 72"),
            new PipAnchor(new Vector2(168f, 138f), PipProvenance.OracleCode,
                "PipBuck.as:115-116 — vishelp.x = 168; vishelp.y = 138"),
            new PipAnchor(new Vector2(600f, 400f), PipProvenance.OracleCode,
                "PipBuck.as:120-121 — vissetkey.x = 600; vissetkey.y = 400"),
        };

        /// <summary>
        /// The backdrop's anchor is the one entry that is <b>derived</b> rather than read, so it is
        /// kept out of <see cref="Table"/> — which is the list of numbers a reader may cite. Folding
        /// it in would put a computed value in the same array as thirteen measured ones.
        /// </summary>
        public static PipAnchor BackdropAnchor => new PipAnchor(
            BackdropOffset, PipProvenance.OracleSwfSymbol,
            "measured: bezel PNG green outline x=377 - but1 symbol x=17 => origin x=360; " +
            "toptext symbol y=5 lands at PNG y=145 => origin y=140");

        /// <summary>The resting anchor for an element.</summary>
        public static PipAnchor AnchorFor(PipElement element)
        {
            if (element == PipElement.Backdrop)
            {
                return BackdropAnchor;
            }

            int index = (int)element - 1;   // Backdrop is index 0 and is not in the table.
            if (index < 0 || index >= Table.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(element), element,
                    "No anchor row for this element. Add one to Table rather than defaulting to zero — " +
                    "a silently zeroed element is a layout bug that looks like a missing sprite.");
            }

            return Table[index];
        }

        /// <summary>One line per element: position, provenance, citation. For the boot log.</summary>
        public static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"PipBuck device space {ReferenceWidth}x{ReferenceHeight}");

            PipAnchor backdrop = BackdropAnchor;
            sb.AppendLine($"  {"Backdrop",-14} {backdrop.Position} {backdrop.Provenance}  {backdrop.Citation}");

            foreach (PipElement element in Enum.GetValues(typeof(PipElement)))
            {
                if (element == PipElement.Backdrop) continue;
                PipAnchor a = AnchorFor(element);
                sb.AppendLine($"  {element,-14} {a.Position} {a.Provenance}  {a.Citation}");
            }

            return sb.ToString();
        }

        // ── resizeScreen ──────────────────────────────────────────────────────

        /// <summary>Where the device root goes, and at what scale, for a given stage size.</summary>
        public readonly struct DevicePlacement
        {
            public DevicePlacement(float x, float y, float scale)
            {
                X = x;
                Y = y;
                Scale = scale;
            }

            /// <summary>Stage-space position of the device origin. +y is <b>down</b> (Flash).</summary>
            public float X { get; }
            public float Y { get; }
            public float Scale { get; }

            public Vector2 ToUnityAnchoredPosition() => new Vector2(X, -Y);
        }

        /// <summary>
        /// The port of <c>PipBuck.resizeScreen</c> (<c>PipBuck.as:357-384</c>).
        ///
        /// <para>Three regimes, and the boundaries are load-bearing:</para>
        /// <list type="number">
        /// <item><description><b>Stage at or above 1200x800.</b> Scale stays exactly 1 — the device is
        /// never magnified, only moved. Up to 1320 wide it is pinned to the top-left corner; past
        /// that it is centred, with the Oracle's own <b>-60</b> x nudge.</description></item>
        /// <item><description><b>Stage narrower than 1200 or shorter than 800.</b> The device is
        /// pinned to the top-left and <i>shrunk</i> by the smaller of the two ratios — so it never
        /// overflows the stage, at the cost of not filling it.</description></item>
        /// </list>
        ///
        /// <para><b>The -60 is not a fudge.</b> The artwork is 1920x1080 around a 1200x800 screen, so
        /// centring the 1200x800 box would leave the casing visibly off-centre; the Oracle shifts the
        /// whole device 60 px left to compensate. Ported verbatim, because "it looks centred without
        /// it on my monitor" is not evidence.</para>
        /// </summary>
        public static DevicePlacement ComputeDevicePlacement(float stageWidth, float stageHeight)
        {
            if (stageWidth >= ReferenceWidth && stageHeight >= ReferenceHeight)
            {
                if (stageWidth > 1320f)
                {
                    return new DevicePlacement(
                        (stageWidth - ReferenceWidth) / 2f - 60f,
                        (stageHeight - ReferenceHeight) / 2f,
                        1f);
                }

                return new DevicePlacement(0f, 0f, 1f);
            }

            float scale = Mathf.Min(stageWidth / ReferenceWidth, stageHeight / ReferenceHeight);
            return new DevicePlacement(0f, 0f, scale);
        }

        /// <summary>Row <paramref name="index"/>'s position inside the status panel.</summary>
        public static Vector2 MonitorRowPosition(int index)
        {
            if (index < 0 || index >= MonitorRowCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    $"Status panel has {MonitorRowCount} rows (PipBuck.kolRItems).");
            }

            return new Vector2(MonitorRowX, MonitorRowY0 + index * MonitorRowPitch);
        }

        /// <summary>Row <paramref name="index"/>'s position inside the page root.</summary>
        public static Vector2 PageRowPosition(int index)
        {
            if (index < 0 || index >= PageRowCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    $"A page has {PageRowCount} rows (PipPage.maxrows).");
            }

            return new Vector2(PageRowX, PageRowY0 + index * PageRowPitch);
        }

        /// <summary>
        /// The header row's position — one pitch <b>above</b> row 0.
        ///
        /// <para><b>Why this is a real row and not a label above the list.</b> The Oracle builds
        /// <c>maxrows + 1</c> row clips: the loop runs <c>-1</c> to <c>maxrows</c>
        /// (<c>PipPage.as:108-142</c>), and the <c>-1</c> iteration becomes <c>statHead</c> — the same
        /// clip class, at <c>y = 100 + (-1) * 30 = 70</c>, with its <c>back</c> hidden
        /// (<c>PipPage.as:123-127</c>). So the column captions line up with the columns by
        /// construction, because they are the same clip. It is a separate accessor rather than
        /// <c>PageRowPosition(-1)</c> so the range check on that method keeps its meaning.</para>
        /// </summary>
        public static Vector2 PageHeaderPosition()
        {
            return new Vector2(PageRowX, PageRowY0 - PageRowPitch);
        }
    }
}
