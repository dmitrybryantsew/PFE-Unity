using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using PFE.Data.Definitions;
using PFE.Systems.Campaign;
using PFE.UI.Common;

namespace PFE.UI.PipBuck.Pages
{
    /// <summary>
    /// The world-info page — <b>page 3</b> of the device, the port of <c>fe.inter.PipPageInfo</c>.
    ///
    /// <para><b>Its five sub-tabs are the Oracle's <c>page2</c> 1..5</b> — Map, Quests, World, Notes,
    /// Enemies — and unlike page 1 there is no sixth: <c>PipPageInfo</c> never leaves its own sub-page
    /// range. All five are visible on every entry (<c>PipSubTabs.IsVisible</c>), and none of them ever
    /// carries a sign marker (<c>PipSubTabs.SignFor</c>), so the sub-tab row is the plain five.</para>
    ///
    /// <para><b>Why this is a plain class and not a <c>MonoBehaviour</c>.</b> The page's state — which
    /// sub-page, the scroll offset, which row was last hovered, which land is picked — is worth testing,
    /// and a test that needs a <c>RectTransform</c> cannot run in this project. So the page is built with
    /// <c>new</c>, owns the GameObjects it creates, and takes its per-tick hook through
    /// <see cref="Step"/>. The only components it adds are two small relays that forward Unity's
    /// event-system callbacks back into it, because those callbacks can only arrive at a component.</para>
    ///
    /// <para><b>Where the rules live.</b> Almost none of them are here. The journal's sort, the
    /// <c>?????</c>/counter/optional combinations, the note list's two-pass order and its 60-character
    /// truncation, the bestiary's category roll-up and its three kill gates, the travel map's node table
    /// and its visibility rule, and the minimap's pan/zoom arithmetic are all in Unity-free types
    /// (<see cref="PipQuestJournal"/>, <see cref="PipNoteJournal"/>, <see cref="PipBestiary"/>,
    /// <see cref="PipTravelMapLayout"/>, <see cref="PipMinimapState"/>) that a plain shell can execute.
    /// This class is the part that reads the world, lays the results out and routes input.</para>
    ///
    /// <para><b>What the port cannot do yet, named rather than hidden.</b></para>
    /// <list type="bullet">
    /// <item><description><b>The row art is a stand-in.</b> <c>visPipQuestItem</c> (999) is not in the
    /// composition, so a row is a transparent hit rect with three text/image columns at the Oracle's own
    /// offsets — the same gap <c>PipPageInvView</c> and <c>PipPageStatView</c> record for their rows.
    /// The offsets, the pitch and the row count are all attested.</description></item>
    /// <item><description><b>The <c>mq</c> marker is a tint.</b> <c>mainQuest</c> (997) has four frames
    /// — frame 1 for a main quest, frames 2/3/4 for note importance 1/2/3 (<see cref="PipInfoFrame"/>)
    /// — and none of them is imported, so the port draws a coloured square per frame.</description></item>
    /// <item><description><b>The bestiary icon is empty.</b> <c>Unit.initIco</c> builds it from a unit's
    /// own world sprite; the port has the sheet but not the lookup, so the slot exists and stays hidden
    /// until <see cref="UnitIconProvider"/> is supplied. See that property.</description></item>
    /// <item><description><b>There is no scrollbar.</b> The Oracle shows <c>vis.scBar</c> when a list is
    /// longer than 18 rows; the port scrolls with the wheel and shows no thumb, exactly as
    /// <c>PipPageStatView</c> already does.</description></item>
    /// <item><description><b>The body pane does not scroll.</b> <c>vis.scText</c> appears when the body
    /// overflows (<c>PipPage.as:1240-1248</c>); the port's body is a fixed rect with truncation, and the
    /// pane is sized so that a full bestiary stat block fits.</description></item>
    /// </list>
    /// </summary>
    public sealed class PipPageInfoView : IPipPage
    {
        /// <summary>The Oracle's page index for world info — <c>pages[3]</c> (<c>PipBuck.as:111</c>).</summary>
        public const int InfoPageId = PipInfoFrame.InfoPageId;

        // ── Row geometry ──────────────────────────────────────────────────────
        //
        // The row ORIGIN and pitch are the Oracle's own (PipPage.as:112-113, through
        // PipBuckLayout.PageRowPosition), and so is every column x below — they are the values
        // setStatItem assigns per sub-page (PipPageInfo.as:312, :329, :338). What is the port's is the
        // WIDTH of each cell: the Oracle's fields live inside the un-imported row clip, so their own
        // widths cannot be read.

        /// <summary>The name cell's width on the quest and note lists.</summary>
        private const float NameWidthText = 250f;

        /// <summary>The name cell's width on the bestiary, where the column starts at x = 5.</summary>
        private const float NameWidthEnemy = 270f;

        /// <summary>The count cell's width — the bestiary's kill count and the note's full title.</summary>
        private const float CountWidth = 110f;

        /// <summary>The <c>mq</c> marker's box. The Oracle scales clip 997 by 0.626; the port's tint
        /// square is sized to sit between the row's left edge and the name column.</summary>
        private const float MarkerSize = 14f;

        /// <summary>How many rows the list shows at once — <c>PipPage.maxrows = 18</c>.</summary>
        private const int VisibleRows = PipInfoFrame.RowCount;

        /// <summary>The body pane's height. Sized so a full bestiary stat block fits without a
        /// scrollbar; see the class remarks for why there is none.</summary>
        private const float BodyHeight = 500f;

        /// <summary>The title field's height.</summary>
        private const float TitleHeight = 30f;

        private const int RowFontSize = 14;
        private const int TitleFontSize = 16;
        private const int BodyFontSize = 14;
        private const int HeaderFontSize = 15;

        /// <summary>The marker's tint per <c>mq</c> frame. Frame 1 is a main quest; 2/3/4 are note
        /// importance 1/2/3. A port choice, because the frames are not imported.</summary>
        private static readonly Color[] MarkerTints =
        {
            new Color(1f, 1f, 0.2f, 1f),      // frame 1 — main quest
            new Color(0f, 1f, 0.6f, 1f),      // frame 2 — importance 1
            new Color(1f, 0.6f, 0f, 1f),      // frame 3 — importance 2
            new Color(1f, 0.2f, 0.2f, 1f),    // frame 4 — importance 3
        };

        // ── Wiring ────────────────────────────────────────────────────────────

        private readonly IPipInfoSource _source;
        private readonly PipSubTabBar _subTabs = new PipSubTabBar();
        private readonly PipInfoMapPanel _mapPanel = new PipInfoMapPanel();
        private readonly PipInfoWorldPanel _worldPanel = new PipInfoWorldPanel();

        private PipBuckCompositionDefinition _composition;
        private Font _font;

        /// <summary>
        /// The Oracle's localisation document — the table every caption on this page is a lookup in.
        /// Null is legal and means "no document": every caption then degrades to the Oracle's own
        /// missing-key marker (<c>*pip_alltask</c>), which says exactly what is wrong rather than
        /// inventing a word.
        /// </summary>
        public PipTextTable TextTable { get; set; } = PipTextTable.Empty;

        /// <summary>
        /// Raised when a sub-tab is pressed, so the <i>device</i> can run <c>PipPage.page2Click</c>.
        /// See <see cref="PipStubPage.SubTabs"/> for why the handler is the device's.
        /// </summary>
        public Action<int> SubPageClicked { get; set; }

        /// <summary>
        /// Raised when the page's help state changes — the Oracle's <c>pip.helpText</c> plus
        /// <c>pip.vis.butHelp.visible</c>. The arguments are (text, buttonVisible).
        ///
        /// <para><b>Why this is an event and not a direct write.</b> The "?" button and the tooltip are
        /// the <i>device's</i> — <c>PipBuck.as:114-127</c> builds them once, on <c>PipBuck.vis</c>, and
        /// page 3 merely publishes the one help string it owns. Routing it through an event keeps the
        /// page from depending on the shell's view, which is the same split every other provider on this
        /// class makes.</para>
        /// </summary>
        public Action<string, bool> HelpChanged { get; set; }

        /// <summary>
        /// Starts the travel the player confirmed — the Oracle's
        /// <c>game.beginMission(this.targetLand)</c> (<c>PipPageInfo.as:762</c>). Returns true when the
        /// mission actually began; the page then raises <see cref="CloseRequested"/>, which is the
        /// Oracle's <c>pip.onoff(-1)</c>.
        ///
        /// <para>Left unset the page reports what the Oracle would have done instead of silently doing
        /// nothing — the same arrangement <c>PipPageStatView.RowActivated</c> uses.</para>
        /// </summary>
        public Func<string, bool> TravelConfirmed { get; set; }

        /// <summary>
        /// The quest ids the <c>alltask</c> button grants — the Oracle's
        /// <c>game.addQuest(id, null, false, false, false)</c> (<c>PipPageInfo.as:781</c>). The page
        /// computes <i>which</i> ids (see <see cref="PipQuestJournal.TasksToGrant"/>); the host owns
        /// the grant, because that is a campaign write and not a view concern.
        /// </summary>
        public Action<IReadOnlyList<string>> QuestsGranted { get; set; }

        /// <summary>Closes the device — the Oracle's <c>pip.onoff(-1)</c> after a mission begins.</summary>
        public Action CloseRequested { get; set; }

        /// <summary>
        /// The bestiary's icon for a unit, or null. Left unset the icon slot stays hidden.
        ///
        /// <para><b>Why it is a provider rather than something the page resolves.</b> The Oracle's
        /// <c>Unit.initIco(id)</c> reads <c>AllData</c>'s <c>&lt;vis&gt;</c> node and either a
        /// <c>vclass</c> DisplayObject or a sliced sheet cell. The port has the sheet — it is on
        /// <c>UnitDefinition.spriteSheet</c>/<c>iconCell</c> — but resolving a <i>unit id</i> to a
        /// <c>UnitDefinition</c> needs a database the page does not hold and must not reach for. So the
        /// slot is built, positioned and left disabled; supplying this closes the gap without the page
        /// learning about the unit catalogue.</para>
        /// </summary>
        public Func<string, Sprite> UnitIconProvider { get; set; }

        /// <summary>
        /// AS3 <c>pip.noAct</c> — actions are refused while the device is open during a cutscene or a
        /// rat race (<c>PipBuck.as:309-313</c>). Null reads false.
        /// </summary>
        public Func<bool> ActionsBlocked { get; set; }

        // ── Built objects ─────────────────────────────────────────────────────

        private RectTransform _root;
        private RectTransform _headerRect;
        private Text _headerName;
        private Text _headerCount;

        private readonly RectTransform[] _rowRects = new RectTransform[VisibleRows];
        private readonly Text[] _nameCells = new Text[VisibleRows];
        private readonly Text[] _countCells = new Text[VisibleRows];
        private readonly Image[] _markers = new Image[VisibleRows];

        /// <summary>The row marker's frames (<c>mq</c>, 997) — null until the rows are built, and null
        /// when the composition has none, which is what switches <see cref="SetMarker"/> between the art
        /// and the tint fallback.</summary>
        private Sprite[] _markerFrames;

        private Text _titleText;
        private Text _bodyText;
        private Text _emptyText;
        private Text _bottomText;
        private RectTransform _titleRect;
        private RectTransform _bodyRect;
        private RectTransform _iconRect;
        private Image _iconImage;

        private RectTransform _confirmRect;
        private Image _confirmBackground;
        private Text _confirmLabel;

        // ── State ─────────────────────────────────────────────────────────────

        private int _subPage = PipInfoFrame.MapSubPage;
        private int _scroll;
        private PipInfoSnapshot _snapshot;

        private readonly List<PipQuestRow> _questRows = new List<PipQuestRow>();
        private readonly List<PipNoteRow> _noteRows = new List<PipNoteRow>();
        private readonly List<PipBestiaryRow> _enemyRows = new List<PipBestiaryRow>();

        /// <summary>The row the pointer is over, as a display index into the active list, or -1.</summary>
        private int _hoveredRow = -1;

        /// <summary>The Oracle's <c>targetLand</c> — the travel node the player has picked.</summary>
        private string _targetLand = "";

        /// <summary>The land the hover pane is describing — the Oracle keeps the last hovered one on
        /// screen, because it attaches no <c>MOUSE_OUT</c> handler to a node.</summary>
        private string _hoveredLand = "";

        public PipPageInfoView(IPipInfoSource source)
        {
            _source = source;
        }

        // ── IPipPage ──────────────────────────────────────────────────────────

        public int PageId => InfoPageId;

        /// <summary>True: this page has content. The shell's "not implemented" notice stays hidden.</summary>
        public bool IsImplemented => true;

        /// <summary>The sub-page currently listed — the Oracle's <c>page2</c>, 1..5.</summary>
        public int SubPage => _subPage;

        /// <summary>The scroll offset, in rows.</summary>
        public int ScrollOffset => _scroll;

        /// <summary>The land the player has picked on the travel map — the Oracle's <c>targetLand</c>.</summary>
        public string TargetLand => _targetLand;

        /// <summary>The sub-tab row, for the device to hang its click handler off.</summary>
        public PipSubTabBar SubTabs => _subTabs;

        /// <summary>The minimap panel, so the device can reach its scale for a debug readout.</summary>
        public PipInfoMapPanel MapPanel => _mapPanel;

        /// <summary>The travel map panel.</summary>
        public PipInfoWorldPanel WorldPanel => _worldPanel;

        /// <summary>
        /// Supplies the composition (for the sub-tab and map button sprites), the font and the
        /// localisation document. Called by <c>PipBuckBootstrapper</c> before <see cref="Build"/>.
        /// </summary>
        public void Configure(PipBuckCompositionDefinition composition, Font font, PipTextTable text)
        {
            _composition = composition;
            _font = font;
            TextTable = text ?? PipTextTable.Empty;
        }

        /// <summary>
        /// The port of <c>PipPage.setStatus</c>'s sub-page half.
        ///
        /// <para><b>The argument's meaning is the Oracle's.</b> <c>PipBuck.setPage</c> assigns
        /// <c>page2</c> only when its own argument is <c>&gt; 0</c> (<c>PipBuck.as:427-430</c>), so
        /// <b>0 means "leave the sub-page and the scroll exactly where they were"</b> — which is what
        /// pressing <c>M</c> a second time relies on. A non-zero value resets the scroll, because the new
        /// sub-page has a different list (<c>PipPage.as:926-931</c>).</para>
        /// </summary>
        public void SetStatus(int subPage)
        {
            if (subPage > 0)
            {
                _subPage = PipSubTabs.Coerce(PageId, subPage, PipSubTabContext.Default);
                _scroll = 0;
            }

            Rebuild();
        }

        /// <summary>
        /// One 30 Hz interface tick.
        ///
        /// <para><b>The Oracle's page does not override <c>step</c></b> (<c>PipPageInfo</c> has no such
        /// method and <c>PipPage.step</c> is empty), so there is nothing to advance: the page's content
        /// is static between rebuilds, and the two things that animate in the Oracle — the map's drag and
        /// the travel <c>sign</c> clip — are driven by the pointer and by a flag respectively.
        /// Deliberately silent; <c>Step</c> runs 30 times a second.</para>
        /// </summary>
        public void Step()
        {
        }

        /// <summary>Shows or hides the page's root. The Oracle's <c>vis.visible</c>.</summary>
        public void SetVisible(bool visible)
        {
            if (_root == null) return;
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        /// <summary>
        /// Creates the page's GameObjects under <paramref name="pageRoot"/>.
        ///
        /// <para><b>The page root is already at the Oracle's <c>vis</c> origin.</b> <c>PipBuckView</c>
        /// places it at <c>PipElement.PageRoot</c>, which is <c>PipPage.as:89-90</c>'s
        /// <c>vis.x = 165; vis.y = 72</c>. Every coordinate here is relative to that, exactly as the
        /// Oracle's are relative to <c>vis</c>.</para>
        /// </summary>
        public void Build(RectTransform pageRoot)
        {
            if (pageRoot == null)
            {
                Debug.LogError("[PipPageInfoView] Build called with no page root. Page 3 will not exist; " +
                               "the device will show an empty screen for M and J.");
                return;
            }

            // Idempotent: a canvas resize re-runs the shell's layout, and a second Build would stack a
            // second copy of every row on top of the first — which reads as doubled text, not an error.
            if (_root != null) return;

            _root = CreateRect("PipInfo Page", pageRoot);

            // One scroll relay on the page root, not one per row: Unity's event system walks UP from
            // the raycast hit, so a relay on the parent serves every row and the gaps between them. The
            // map panel puts its own relay on its window, which takes the wheel over the map first.
            _root.gameObject.AddComponent<ScrollRelay>().Page = this;

            _subTabs.Build(_root, _font, _composition != null ? _composition.subCategoryButton : null);

            // The one writer of the bar's handler; it forwards to the page's own event, read at *invoke*
            // time so a handler set after Build still takes effect. See PipPageStatView.Build for the
            // play-test that this line exists to prevent.
            _subTabs.SubPageClicked = subPage => SubPageClicked?.Invoke(subPage);

            BuildMapPanel();
            BuildWorldPanel();
            BuildHeader();
            BuildRows();
            BuildTextFields();
            BuildIcon();
            BuildConfirmButton();

            Rebuild();

            // PipPage.as:92 — `this.vis.visible = false;` in the Oracle's page constructor.
            SetVisible(false);
        }

        private void BuildMapPanel()
        {
            _mapPanel.Build(_root, _font, _composition);
        }

        private void BuildWorldPanel()
        {
            _worldPanel.Build(_root, _composition);
            _worldPanel.NodeClicked = OnNodeClicked;
            _worldPanel.NodeHovered = OnNodeHovered;
        }

        private void BuildHeader()
        {
            // `statHead` is the i = -1 iteration of the row loop — a row like any other, one pitch above
            // row 0, with its `back` hidden (PipPage.as:123-127). Only the bestiary turns it on.
            _headerRect = CreateRowRect("statHead", PipBuckLayout.PageHeaderPosition());

            // The header is a caption, not a row: it must not swallow the hover meant for the row under
            // it. The Oracle's own header hides its `back` clip for the same reason (PipPage.as:123-127).
            var headerHit = _headerRect.GetComponent<Image>();
            if (headerHit != null) headerHit.raycastTarget = false;

            _headerName = CreateCell(_headerRect, "nazv", PipInfoFrame.EnemyNameX, NameWidthEnemy,
                HeaderFontSize);
            _headerCount = CreateCell(_headerRect, "kol", PipInfoFrame.RowCountX, CountWidth,
                HeaderFontSize);
        }

        private void BuildRows()
        {
            for (int row = 0; row < VisibleRows; row++)
            {
                RectTransform rect = CreateRowRect("Row" + row, PipBuckLayout.PageRowPosition(row));
                _rowRects[row] = rect;

                var relay = rect.gameObject.AddComponent<RowRelay>();
                relay.Page = this;
                relay.RowIndex = row;

                // The row's own Image stays fully transparent — it is the raycast target the hover and
                // the wheel reach the page through, and the art (when there is any) is the `back` child
                // below it, not this fill.
                rect.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0f);

                BuildRowBack(rect);

                _markers[row] = CreateMarker(rect);

                // The name cell's x is per sub-page (PipPageInfo.as:312/329/338) and is written by
                // RefreshRows, because the same row clips are reused when the sub-page changes.
                _nameCells[row] = CreateCell(rect, "nazv", PipInfoFrame.RowNameX, NameWidthText,
                    RowFontSize);

                _countCells[row] = CreateCell(rect, "kol", PipInfoFrame.RowCountX, CountWidth,
                    RowFontSize);
            }
        }

        /// <summary>
        /// The row's `back` (498) — the panel the inventory page already imports as
        /// <see cref="PipBuckCompositionDefinition.inventoryRowBack"/>, because sprite 999 composes the
        /// same symbol (<c>PipInfoFrame</c> rows 209-218). The placement is the Oracle's own: the SWF
        /// records the child at (188.3, 12.0) with scale (0.671, 1.0), and both row symbols are
        /// registered at their CENTRE, so the rect keeps the art's natural 560x25 and lets the scale do
        /// the work — 560 x 0.671 spans row-local x 0.4..376.2, i.e. the panel starts at the row's left
        /// edge, which is the check that the numbers describe the same object.
        ///
        /// <para>Without the composition the row stays transparent (still a raycast target, so hover
        /// and scroll keep working); <c>ramka</c> (897) is deliberately not drawn —
        /// <c>PipPage.as:119-121</c> hides it in the row constructor and <c>PipPageInfo.as:306</c>
        /// re-hides it per item, so nothing on this page ever shows it.</para>
        /// </summary>
        private void BuildRowBack(RectTransform row)
        {
            Sprite back = _composition != null ? _composition.inventoryRowBack : null;
            if (back == null) return;

            var go = new GameObject("back", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(row, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);                       // the symbol registers at centre
            rect.sizeDelta = new Vector2(560f, 25f);                    // the art's own size
            rect.localScale = new Vector3(PipInfoFrame.RowBackScaleX, 1f, 1f);
            rect.anchoredPosition = new Vector2(PipInfoFrame.RowBackX, -PipInfoFrame.RowCentreY);

            Image image = go.GetComponent<Image>();
            image.sprite = back;
            image.color = Color.white;
            image.raycastTarget = false;
        }

        private Image CreateMarker(RectTransform row)
        {
            var go = new GameObject("mq", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(row, false);

            Image image = go.GetComponent<Image>();
            image.raycastTarget = false;
            image.enabled = false;

            _markerFrames = _composition != null ? _composition.infoMarkerFrames : null;
            bool hasArt = _markerFrames != null && _markerFrames.Length > 0;

            if (hasArt)
            {
                // The Oracle's own placement: (16.9, 12.0) at scale 0.626, the symbol registering at
                // its centre like every other row child — 31 x 0.626 ≈ 19.4 px, hanging 3.5 px out of
                // the 26-tall row band at the bottom, which is the art's own choice.
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(31f, 31f);
                rect.localScale = new Vector3(PipInfoFrame.RowMarkerScale, PipInfoFrame.RowMarkerScale, 1f);
                rect.anchoredPosition = new Vector2(PipInfoFrame.RowMarkerX, -PipInfoFrame.RowCentreY);
            }
            else
            {
                // The no-art fallback: a tinted square sized to sit between the row's left edge and the
                // name column.
                AnchorTopLeft(rect);
                rect.sizeDelta = new Vector2(MarkerSize, MarkerSize);
                rect.anchoredPosition = new Vector2(PipInfoFrame.RowMarkerX, -PipInfoFrame.RowMarkerY);
                image.color = MarkerTints[0];
            }

            return image;
        }

        private void BuildTextFields()
        {
            _titleRect = CreateTextRect("nazv", PipInfoFrame.PrologueTextX, PipInfoFrame.TitleY,
                PipInfoFrame.PrologueTitleWidth, TitleHeight, TitleFontSize, PfeUiColors.PageYellow,
                out _titleText);
            _titleText.supportRichText = true;

            _bodyRect = CreateTextRect("info", PipInfoFrame.PrologueTextX, PipInfoFrame.BodyY,
                PipInfoFrame.PrologueBodyWidth, BodyHeight, BodyFontSize, PfeUiColors.PageR0,
                out _bodyText);
            _bodyText.supportRichText = true;
            _bodyText.alignment = TextAnchor.UpperLeft;

            // `emptytext` (1003) — sub-tab 1's `emptymap` notice. Authored at (19.5, 86.2).
            CreateTextRect("emptytext", PipInfoFrame.EmptyTextX, PipInfoFrame.EmptyTextY,
                PipInfoFrame.MapPanelWidth, 60f, TitleFontSize, PfeUiColors.PageOrange, out _emptyText);
            _emptyText.alignment = TextAnchor.UpperLeft;

            // `bottext` (1002). Built because the page frame has it and the base constructor builds it
            // for every page, but `setSubPages` hides it on entry and no sub-page re-shows it — so it is
            // hidden here and stays hidden. A later work item that needs a footer finds it rather than
            // building a second one.
            CreateTextRect("bottext", PipInfoFrame.BottomTextX, PipInfoFrame.BottomTextY,
                PipInfoFrame.PrologueBodyWidth, 30f, RowFontSize, PfeUiColors.PageR0, out _bottomText);
            _bottomText.gameObject.SetActive(false);
        }

        private void BuildIcon()
        {
            // `ico` (240) — the bestiary's icon holder. It has no placements of its own, so its size is
            // the port's own (PipInfoFrame.BestiaryIconSize).
            var go = new GameObject("ico", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _iconRect = (RectTransform)go.transform;
            _iconRect.SetParent(_root, false);
            AnchorTopLeft(_iconRect);
            _iconRect.sizeDelta = new Vector2(PipInfoFrame.BestiaryIconSize, PipInfoFrame.BestiaryIconSize);
            Place(_iconRect, PipInfoFrame.IconX, PipInfoFrame.IconY);

            _iconImage = go.GetComponent<Image>();
            _iconImage.raycastTarget = false;
            _iconImage.enabled = false;
        }

        private void BuildConfirmButton()
        {
            // `butOk` (508) — the `alltask` / `trans` button. `butDef` is not built: page 3 never shows
            // it (the Oracle's `setSubPages` hides it and no branch re-shows it), so a second button
            // would be dead weight with a name that suggests otherwise.
            var go = new GameObject("butOk", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(Button));
            _confirmRect = (RectTransform)go.transform;
            _confirmRect.SetParent(_root, false);
            AnchorTopLeft(_confirmRect);
            _confirmRect.sizeDelta = new Vector2(PipInfoFrame.ButtonWidth, PipInfoFrame.ButtonHeight);
            Place(_confirmRect, PipInfoFrame.ConfirmButtonX, PipInfoFrame.ButtonY);

            _confirmBackground = go.GetComponent<Image>();

            // The button's own art (symbol 508, 187x28 — the rect's own size). The Oracle keeps a `text`
            // child on top of it (`vis.butOk.text.text = …`, PipPageInfo.as:198/:203), so the caption is
            // drawn over the art rather than instead of it; without the art the sub-category sprite is
            // the stand-in it always was.
            Sprite buttonArt = _composition != null ? _composition.infoConfirmButton : null;
            _confirmBackground.sprite = buttonArt != null
                ? buttonArt
                : _composition != null ? _composition.subCategoryButton : null;
            _confirmBackground.color = _composition != null
                ? Color.white
                : new Color(0f, 0f, 0f, 0.01f);

            var labelGo = new GameObject("text", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Text));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(_confirmRect, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            _confirmLabel = labelGo.GetComponent<Text>();
            _confirmLabel.font = _font;
            _confirmLabel.fontSize = RowFontSize;
            _confirmLabel.color = PfeUiColors.PageR0;
            _confirmLabel.alignment = TextAnchor.MiddleCenter;
            _confirmLabel.raycastTarget = false;

            var button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(OnConfirm);

            go.SetActive(false);
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        /// <summary>
        /// Re-reads the world and repaints everything — the port of <c>setStatus</c> plus
        /// <c>setSubPages</c> plus <c>setStatItems</c>.
        /// </summary>
        public void Rebuild()
        {
            if (_root == null) return;

            _snapshot = _source?.Read() ?? new PipInfoSnapshot();

            BuildLists();
            ClampScroll();

            // PipPage.as:922-923 — `setStatus` clears both text columns before `setSubPages` runs, so the
            // detail pane starts empty on every entry and is filled by the first hover. That is the
            // Oracle's behaviour: there is no "selected row" notion on this page.
            _hoveredRow = -1;
            _hoveredLand = "";
            _targetLand = "";

            RefreshSubTabs();
            RefreshHeader();
            RefreshRows();
            RefreshTextColumns();
            RefreshIcon();
            RefreshPanels();
            RefreshConfirmButton();
            RefreshHelp();
        }

        private void BuildLists()
        {
            _questRows.Clear();
            _noteRows.Clear();
            _enemyRows.Clear();

            switch (_subPage)
            {
                case PipInfoFrame.QuestSubPage:
                    _questRows.AddRange(PipQuestJournal.BuildList(_snapshot.Quests, TextTable));
                    break;

                case PipInfoFrame.NoteSubPage:
                    _noteRows.AddRange(
                        PipNoteJournal.BuildList(_snapshot.Notes, TextTable, _snapshot.PlayerName));
                    break;

                case PipInfoFrame.EnemySubPage:
                    _enemyRows.AddRange(
                        PipBestiary.BuildList(_snapshot.AllDataRoot, TextTable, _snapshot.FragCountOf));
                    break;
            }
        }

        /// <summary>How many entries the active list holds. Only one list is ever non-empty.</summary>
        private int EntryCount => _questRows.Count + _noteRows.Count + _enemyRows.Count;

        private void ClampScroll()
        {
            int maxScroll = Mathf.Max(0, EntryCount - VisibleRows);
            if (_scroll > maxScroll) _scroll = maxScroll;
            if (_scroll < 0) _scroll = 0;
        }

        private void RefreshSubTabs()
        {
            // Page 3 signs nothing and has no context-dependent sub-tab, so the signals are the empty
            // set — PipSubTabs.SignFor returns 0 for every slot on this page, which is what the Oracle's
            // `setSigns` override leaves behind.
            _subTabs.Refresh(PageId, _subPage, TextTable, PipSubTabContext.Default, PipSubTabSignals.None);
        }

        private void RefreshHeader()
        {
            // PipPageInfo.as:250-254 — the header exists only for the bestiary.
            bool visible = _subPage == PipInfoFrame.EnemySubPage;

            if (_headerRect != null && _headerRect.gameObject.activeSelf != visible)
            {
                _headerRect.gameObject.SetActive(visible);
            }

            if (!visible) return;

            // `statHead.nazv.text = ""; statHead.mq.visible = false; statHead.kol.text = frag`.
            if (_headerName != null) _headerName.text = string.Empty;
            if (_headerCount != null) _headerCount.text = Pip("frag");
        }

        private void RefreshRows()
        {
            float nameX = PipInfoFrame.NameXFor(_subPage);
            float nameWidth = _subPage == PipInfoFrame.EnemySubPage ? NameWidthEnemy : NameWidthText;

            for (int row = 0; row < VisibleRows; row++)
            {
                int index = row + _scroll;
                bool visible = index < EntryCount;

                if (_rowRects[row] != null && _rowRects[row].gameObject.activeSelf != visible)
                {
                    _rowRects[row].gameObject.SetActive(visible);
                }

                if (!visible) continue;

                // Every row is reset before the sub-page's own rules run, exactly as setStatItem does
                // (PipPageInfo.as:301-309): the marker hidden, the name at full alpha, the count blank.
                if (_markers[row] != null)
                {
                    _markers[row].enabled = false;
                    _markers[row].gameObject.SetActive(false);
                }

                if (_countCells[row] != null)
                {
                    _countCells[row].text = string.Empty;
                    _countCells[row].gameObject.SetActive(false);
                }

                if (_nameCells[row] != null)
                {
                    _nameCells[row].text = string.Empty;
                    _nameCells[row].color = PfeUiColors.PageR0;
                    ((RectTransform)_nameCells[row].transform).sizeDelta =
                        new Vector2(nameWidth, PipBuckLayout.PageRowPitch);
                    ((RectTransform)_nameCells[row].transform).anchoredPosition =
                        new Vector2(nameX, 0f);
                }

                switch (_subPage)
                {
                    case PipInfoFrame.QuestSubPage:
                        RefreshQuestRow(row, _questRows[index]);
                        break;
                    case PipInfoFrame.NoteSubPage:
                        RefreshNoteRow(row, _noteRows[index]);
                        break;
                    case PipInfoFrame.EnemySubPage:
                        RefreshEnemyRow(row, _enemyRows[index]);
                        break;
                }
            }
        }

        private void RefreshQuestRow(int row, PipQuestRow entry)
        {
            Text name = _nameCells[row];
            if (name == null) return;

            name.text = entry.Display;

            // `param1.nazv.alpha = param1.mq.alpha = 0.4` for a finished quest (:317).
            name.color = WithAlpha(PfeUiColors.PageR0, entry.Dim ? PipQuestJournal.DoneAlpha : 1f);

            if (!entry.IsMain) return;

            SetMarker(row, entry.MarkerFrame, entry.Dim ? PipQuestJournal.DoneAlpha : 1f);
        }

        private void RefreshNoteRow(int row, PipNoteRow entry)
        {
            Text name = _nameCells[row];
            if (name != null)
            {
                name.text = entry.Caption;
                name.color = PfeUiColors.PageR0;
            }

            // `param1.kol.text = param2.nazv` — the FULL title, so a truncated caption still has its
            // whole text on screen (:331).
            if (_countCells[row] != null)
            {
                _countCells[row].text = entry.Title;
                _countCells[row].color = PfeUiColors.PageR0;
                _countCells[row].gameObject.SetActive(true);
            }

            SetMarker(row, entry.MarkerFrame, 1f);
        }

        private void RefreshEnemyRow(int row, PipBestiaryRow entry)
        {
            Text name = _nameCells[row];
            if (name != null)
            {
                // `PipBestiary.Caption` is the Oracle's own `<b>`/indent expression, so the two spaces
                // runs and the bold flag cannot drift from the model.
                name.text = PipBestiary.Caption(entry);
                name.color = PfeUiColors.PageR0;
            }

            if (_countCells[row] == null) return;

            // `if(param2.kol > 0) param1.kol.text = param2.kol; param1.kol.visible = true;` (:352-356) —
            // the field is shown even when empty, so a faction with no counted members still has its
            // column.
            _countCells[row].text = entry.ShowCount
                ? entry.Kills.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
            _countCells[row].color = PfeUiColors.PageR0;
            _countCells[row].gameObject.SetActive(true);
        }

        private void SetMarker(int row, int frame, float alpha)
        {
            Image marker = _markers[row];
            if (marker == null) return;

            int index = Mathf.Clamp(frame - 1, 0, MarkerTints.Length - 1);

            if (_markerFrames != null && _markerFrames.Length > 0)
            {
                // The real clip: the frame IS the state (1 main quest, 2/3/4 note importance), so the
                // sprite replaces the tint and only the alpha is the port's.
                marker.sprite = _markerFrames[Mathf.Clamp(index, 0, _markerFrames.Length - 1)];
                marker.color = new Color(1f, 1f, 1f, alpha);
            }
            else
            {
                marker.color = WithAlpha(MarkerTints[index], alpha);
            }

            marker.gameObject.SetActive(true);
            marker.enabled = true;
        }

        private void RefreshTextColumns()
        {
            // The two columns' x/width are per sub-page (PipPageInfo.as:84-87, :145-147) and the body's
            // y is per sub-page too — see RefreshDetail.
            float x = PipInfoFrame.TextXFor(_subPage);

            if (_titleRect != null)
            {
                _titleRect.sizeDelta = new Vector2(PipInfoFrame.TitleWidthFor(_subPage), TitleHeight);
                Place(_titleRect, x, PipInfoFrame.TitleY);
            }

            if (_bodyRect != null)
            {
                _bodyRect.sizeDelta = new Vector2(PipInfoFrame.BodyWidthFor(_subPage), BodyHeight);
                Place(_bodyRect, x, BodyYFor(_subPage));
            }

            if (_titleText != null) _titleText.text = string.Empty;
            if (_bodyText != null) _bodyText.text = string.Empty;

            // `emptytext` is sub-tab 1's alone: `vis.emptytext.text = noMap ? emptymap : ""`.
            if (_emptyText != null)
            {
                bool showEmpty = _subPage == PipInfoFrame.MapSubPage &&
                                 (_snapshot.Map == null || _snapshot.Map.NoMap);

                _emptyText.text = showEmpty ? Pip("emptymap") : string.Empty;
            }
        }

        /// <summary>
        /// Where the body starts on a sub-page — the Oracle's three assignments
        /// (<c>PipPageInfo.as:368</c>, <c>:427</c>, <c>:446</c>).
        /// </summary>
        private static float BodyYFor(int subPage)
        {
            switch (subPage)
            {
                // `vis.info.y = vis.ico.y` — the quest detail sits level with the icon slot.
                case PipInfoFrame.QuestSubPage:
                    return PipInfoFrame.IconY;

                // `vis.info.y = vis.nazv.y` — the note body replaces the title's own line.
                case PipInfoFrame.NoteSubPage:
                    return PipInfoFrame.TitleY;

                // `vis.info.y = vis.ico.y + vis.ico.height + 20` — under the icon.
                case PipInfoFrame.EnemySubPage:
                    return PipInfoFrame.BestiaryBodyY;

                default:
                    return PipInfoFrame.BodyY;
            }
        }

        private void RefreshIcon()
        {
            if (_iconRect == null) return;

            // `vis.ico.visible = true` only on the bestiary (:252).
            bool visible = _subPage == PipInfoFrame.EnemySubPage;
            if (_iconRect.gameObject.activeSelf != visible) _iconRect.gameObject.SetActive(visible);
            if (!visible) return;

            // `vis.ico.x = 685 - vis.ico.width / 2` (:448) — the icon is centred on x = 685.
            Place(_iconRect, 685f - PipInfoFrame.BestiaryIconSize * 0.5f, PipInfoFrame.IconY);

            Sprite icon = string.IsNullOrEmpty(_hoveredUnitId) ? null : UnitIconProvider?.Invoke(_hoveredUnitId);

            _iconImage.enabled = icon != null;
            _iconImage.sprite = icon;
        }

        private void RefreshPanels()
        {
            if (_subPage == PipInfoFrame.MapSubPage)
            {
                _mapPanel.Refresh(_snapshot.Map);
            }
            else
            {
                _mapPanel.SetVisible(false);
            }

            if (_subPage == PipInfoFrame.WorldSubPage)
            {
                _worldPanel.Refresh(BuildWorldDraws(), _targetLand, PfeUiColors.PageR0);
                _worldPanel.SetVisible(true);
            }
            else
            {
                _worldPanel.SetVisible(false);
            }
        }

        /// <summary>
        /// The nodes the travel map should draw — the whole <c>for each(land in game.lands)</c> loop,
        /// reduced to the model's pure function.
        ///
        /// <para><b>Prob lands are excluded by the model, not here.</b> That is where the Oracle excludes
        /// them (<c>PipPageInfo.as:154</c>), and a caller that forgot would draw six extra nodes and look
        /// almost right.</para>
        /// </summary>
        private List<PipTravelMapLayout.TravelNodeDrawResult> BuildWorldDraws()
        {
            var ids = new List<string>(_snapshot.Lands.Count);
            for (int i = 0; i < _snapshot.Lands.Count; i++)
            {
                PipLandFacts land = _snapshot.Lands[i];
                if (land != null && !string.IsNullOrEmpty(land.LandId)) ids.Add(land.LandId);
            }

            return PipTravelMapLayout.Draw(
                ids,
                landId => Land(landId)?.IsProb ?? false,
                landId => Land(landId)?.IsTest ?? false,
                landId => Land(landId)?.Visited ?? false,
                landId => Land(landId)?.Access ?? false,
                landId => Land(landId)?.CanTravel ?? false,
                _snapshot.TestMode,
                _snapshot.HelpMess);
        }

        private PipLandFacts Land(string landId) => _snapshot?.FindLand(landId);

        private void RefreshConfirmButton()
        {
            if (_confirmRect == null) return;

            string label = string.Empty;
            bool visible = false;

            if (_subPage == PipInfoFrame.QuestSubPage)
            {
                visible = HasAllTask();
                label = Pip("alltask");
            }
            else if (_subPage == PipInfoFrame.WorldSubPage)
            {
                // The Oracle writes `trans` into the field unconditionally (:200) and shows it only
                // once a travelable node has been clicked (:727).
                label = Pip("trans");
                visible = !string.IsNullOrEmpty(_targetLand);
            }

            _confirmLabel.text = label;

            // The button is tinted down when it is on screen but its label could not be resolved — a
            // visible `*pip_alltask` on a full-brightness button reads as a translation bug rather than
            // as a missing document, and the two have different fixes.
            if (_confirmBackground != null)
            {
                bool resolved = label.Length > 0 && label[0] != '*';
                _confirmBackground.color = _composition != null
                    ? (resolved ? Color.white : new Color(1f, 0.6f, 0.6f, 1f))
                    : new Color(0f, 0f, 0f, 0.01f);
            }

            if (_confirmRect.gameObject.activeSelf != visible)
            {
                _confirmRect.gameObject.SetActive(visible);
            }
        }

        private void RefreshHelp()
        {
            // `pip.vis.butHelp.visible = false` at the top of setSubPages (:89), and only sub-page 3
            // re-shows it with its own string (:203-204). So on four of the five sub-tabs the device's
            // "?" button is off screen, and on the fifth it holds `helpWorld`.
            bool visible = _subPage == PipInfoFrame.WorldSubPage;
            HelpChanged?.Invoke(visible ? HelpWorld() : string.Empty, visible);
        }

        /// <summary>
        /// <c>Res.txt("p","helpWorld",0,true)</c> — the travel map's help string.
        ///
        /// <para><b>The fourth argument is the <c>@1..@5</c> keybind pass, and it is passed because the
        /// Oracle passes it.</b> <c>helpWorld</c> carries no <c>s1..s5</c> attributes, so the pass is a
        /// no-op for it — but passing it means the port does not have to be right about which strings
        /// have them.</para>
        ///
        /// <para><b>The string is rendered verbatim, brackets and all.</b> <c>helpWorld</c> writes
        /// <c>[Base camps]</c> and the Oracle shows the brackets literally: the <c>yel</c> rewrite lives
        /// in the note body path (<c>PipPageInfo.as:429-430</c>), not in the help path. Running the
        /// project's <c>[</c>-rewriting formatter here would colour them.</para>
        /// </summary>
        private string HelpWorld()
        {
            string raw = TextTable != null ? TextTable.Text("p", "helpWorld", 0) : string.Empty;

            var keys = new string[5];
            for (int i = 1; i <= 5; i++)
            {
                keys[i - 1] = TextTable?.EntryAttribute("p", "helpWorld", "s" + i);
            }

            return PipTextTable.ApplyKeys(raw, keys, id => TextTable != null ? TextTable.Key(id) : id);
        }

        // ── The detail pane ───────────────────────────────────────────────────

        private string _hoveredQuestId = "";
        private string _hoveredNoteId = "";
        private string _hoveredUnitId = "";

        /// <summary>The port of a row's <c>MouseEvent.MOUSE_OVER</c> handler, <c>statInfo</c>.</summary>
        internal void OnRowHover(int rowIndex)
        {
            int index = rowIndex + _scroll;
            if (index < 0 || index >= EntryCount) return;

            if (_hoveredRow == index) return;
            _hoveredRow = index;

            switch (_subPage)
            {
                case PipInfoFrame.QuestSubPage:
                    _hoveredQuestId = _questRows[index].QuestId;
                    break;
                case PipInfoFrame.NoteSubPage:
                    _hoveredNoteId = _noteRows[index].NoteId;
                    break;
                case PipInfoFrame.EnemySubPage:
                    _hoveredUnitId = _enemyRows[index].UnitId;
                    break;
            }

            RefreshDetail();
        }

        /// <summary>
        /// Repaints the two text columns from the hovered row — the body of <c>statInfo</c>.
        ///
        /// <para><b>Hover, not click.</b> The Oracle wires <c>statInfo</c> to <c>MOUSE_OVER</c> and gives
        /// a row on sub-tabs 2, 4 and 5 no click handler at all (<c>itemClick</c> branches on
        /// <c>page2 == 3</c> and returns otherwise), so clicking a quest does nothing and the detail
        /// follows the pointer. The port keeps that, because the alternative — a selection model — is a
        /// different page.</para>
        /// </summary>
        private void RefreshDetail()
        {
            if (_titleText == null || _bodyText == null) return;

            switch (_subPage)
            {
                case PipInfoFrame.QuestSubPage:
                    ShowQuestDetail();
                    break;
                case PipInfoFrame.WorldSubPage:
                    ShowLandDetail();
                    break;
                case PipInfoFrame.NoteSubPage:
                    ShowNoteDetail();
                    break;
                case PipInfoFrame.EnemySubPage:
                    ShowEnemyDetail();
                    break;
                default:
                    _titleText.text = string.Empty;
                    _bodyText.text = string.Empty;
                    break;
            }
        }

        private void ShowQuestDetail()
        {
            QuestFacts facts = QuestFactsFor(_hoveredQuestId);
            if (facts == null)
            {
                _titleText.text = string.Empty;
                _bodyText.text = string.Empty;
                return;
            }

            _titleText.text = PipQuestJournal.TitleFor(facts, TextTable);
            _bodyText.text = FormatBody(
                PipQuestJournal.BuildDetail(facts, StateFor(_hoveredQuestId), TextTable));
        }

        private void ShowNoteDetail()
        {
            if (string.IsNullOrEmpty(_hoveredNoteId))
            {
                _titleText.text = string.Empty;
                _bodyText.text = string.Empty;
                return;
            }

            // `vis.info.y = vis.nazv.y` and no `nazv` assignment — the note's title is on its row, and
            // the body replaces the title line (:424-426).
            _titleText.text = string.Empty;
            _bodyText.text = FormatBody(
                PipNoteJournal.BuildBody(_hoveredNoteId, TextTable, _snapshot.PlayerName));
        }

        private void ShowEnemyDetail()
        {
            if (string.IsNullOrEmpty(_hoveredUnitId))
            {
                _titleText.text = string.Empty;
                _bodyText.text = string.Empty;
                return;
            }

            // `vis.nazv.text = param1.currentTarget.nazv.text` — the row's own caption, bold tags and
            // indent included (:444).
            _titleText.text = PipBestiary.Caption(FindEnemyRow(_hoveredUnitId));
            _bodyText.text = FormatBody(PipBestiary.BuildDetail(
                _snapshot.AllDataRoot, TextTable, _hoveredUnitId, KillsFor(_hoveredUnitId)));
        }

        /// <summary>
        /// The travel hover pane — the port of <c>PipPageInfo.as:369-423</c>.
        ///
        /// <para><b>Six appends, each gated on a different fact, and the order is load-bearing.</b> The
        /// land's own <c>info</c> text comes first; then exactly <b>one</b> of the four status lines
        /// (unvisited / passed / base / random), because the Oracle's chain is <c>if … else if …</c>;
        /// then the prob-room counter, the recommended level, the under-levelled warning, and finally the
        /// impaired-shooting warning. Swapping any two changes the pane's paragraph order.</para>
        /// </summary>
        private void ShowLandDetail()
        {
            PipLandFacts land = Land(_hoveredLand);
            if (land == null)
            {
                _titleText.text = string.Empty;
                _bodyText.text = string.Empty;
                return;
            }

            _titleText.text = TextTable.Text("m", land.LandId, 0);

            var body = new System.Text.StringBuilder();
            body.Append(TextTable.Text("m", land.LandId, 1));

            // The four-way chain. Note the Oracle's `else if` order: unvisited wins over passed, and
            // `tip == "base"` wins over `tip == "rnd"` — a land cannot be both, but the order is what the
            // code says and is reproduced.
            if (!land.Visited)
            {
                body.Append("\n\n").Append(PipPageMarkup.InlineBlue(Pip("ls1")));
            }
            else if (land.Passed)
            {
                body.Append("\n\n").Append(PipPageMarkup.InlineOrange(Pip("ls2")));
            }
            else if (land.Tip == "base")
            {
                body.Append("\n\n").Append(PipPageMarkup.InlineOrange(Pip("ls4")));
            }
            else if (land.Tip == "rnd")
            {
                body.Append("\n\n").Append(PipPageMarkup.InlineYellow(
                    Pip("ls3") + ": " + (land.LandStage + 1)));
            }

            if (land.Tip == "rnd" && land.AllProbCount > 0)
            {
                body.Append("\n").Append(PipPageMarkup.InlineYellow(
                    Pip("kolProb") + ": " + land.ClosedProbCount + "/" + land.AllProbCount));
            }

            if (land.Difficulty > 0)
            {
                // `Math.round(_loc2_.dif)` — AS3's round, which is floor(x + 0.5).
                body.Append("\n\n").Append(Pip("recLevel")).Append(' ')
                    .Append(As3Round(land.Difficulty));
            }

            if (land.Difficulty > _snapshot.PlayerLevel)
            {
                body.Append("\n\n").Append(Pip("wrLevel"));
            }

            // `speedShtr` — three exclusive warnings at 3, 2 and 1, then a hint at any value above zero.
            // The warnings are `red()`-wrapped; the hint is not.
            if (_snapshot.SpeedShooter >= 3)
            {
                body.Append("\n\n").Append(PipPageMarkup.Red(Pip("speedshtr3")));
            }
            else if (_snapshot.SpeedShooter == 2)
            {
                body.Append("\n\n").Append(PipPageMarkup.Red(Pip("speedshtr2")));
            }
            else if (_snapshot.SpeedShooter == 1)
            {
                body.Append("\n\n").Append(PipPageMarkup.Red(Pip("speedshtr1")));
            }

            if (_snapshot.SpeedShooter >= 1)
            {
                body.Append("\n").Append(Pip("speedshtr0"));
            }

            _bodyText.text = FormatBody(body.ToString());
        }

        /// <summary>
        /// Resolves the Flash spans a body carries to Unity colour tags — and nothing else.
        ///
        /// <para><b>Why not the full formatter.</b> These strings are assigned to <c>htmlText</c> in the
        /// Oracle and rendered by the page style sheet, so the spans must be resolved; but the <c>[</c>
        /// rewrite belongs to the callers that do it (the note body does, the quest body does not), so
        /// running the full <c>GUI.as</c> pipeline here would colour brackets the Oracle leaves plain —
        /// see <c>PfeTextFormatter.FormatSpans</c>.</para>
        /// </summary>
        private static string FormatBody(string flashText)
        {
            return PfeTextFormatter.FormatSpans(flashText, FlashStyleSheet.Page, null);
        }

        private QuestFacts QuestFactsFor(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return null;
            for (int i = 0; i < _snapshot.Quests.Count; i++)
            {
                PipQuestEntry entry = _snapshot.Quests[i];
                if (entry?.Facts != null &&
                    string.Equals(entry.Facts.QuestId, questId, StringComparison.Ordinal))
                {
                    return entry.Facts;
                }
            }

            return null;
        }

        private QuestRuntimeState StateFor(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return null;
            for (int i = 0; i < _snapshot.Quests.Count; i++)
            {
                PipQuestEntry entry = _snapshot.Quests[i];
                if (entry?.Facts != null &&
                    string.Equals(entry.Facts.QuestId, questId, StringComparison.Ordinal))
                {
                    return entry.State;
                }
            }

            return null;
        }

        private PipBestiaryRow FindEnemyRow(string unitId)
        {
            for (int i = 0; i < _enemyRows.Count; i++)
            {
                if (string.Equals(_enemyRows[i].UnitId, unitId, StringComparison.Ordinal))
                {
                    return _enemyRows[i];
                }
            }

            return null;
        }

        /// <summary>
        /// The kill count the bestiary's body reads — the row's <c>kol</c>, and for a faction the
        /// <b>rolled-up</b> total, which is what the Oracle passes when the player clicks a faction row:
        /// <c>infoUnit(id, param1.kol.text)</c> reads the row's own count field.
        /// </summary>
        private int KillsFor(string unitId)
        {
            PipBestiaryRow row = FindEnemyRow(unitId);
            return row != null ? row.Kills : -1;
        }

        // ── The vendor gate ───────────────────────────────────────────────────

        private bool HasAllTask()
        {
            return PipQuestJournal.HasAllTask(
                _snapshot.InBaseLand,
                _snapshot.PlayerLevel,
                _snapshot.VendorTasks,
                _snapshot.QuestState,
                LandFactsFor,
                _snapshot.Trigger,
                _snapshot.Skill);
        }

        private List<string> TasksToGrant()
        {
            return PipQuestJournal.TasksToGrant(
                _snapshot.VendorTasks,
                _snapshot.PlayerLevel,
                _snapshot.QuestState,
                LandFactsFor,
                _snapshot.Trigger,
                _snapshot.Skill);
        }

        /// <summary>
        /// A land as <c>checkQuest</c> sees it. <b>Null for an unknown land, which refuses</b> — the
        /// Oracle's one arm that fires on a missing input (<c>PipPage.as:1519-1522</c>).
        /// </summary>
        private PipTaskLandFacts LandFactsFor(string landId)
        {
            PipLandFacts land = Land(landId);
            if (land == null) return null;

            return new PipTaskLandFacts
            {
                Access = land.Access,
                Visited = land.Visited,
                Difficulty = land.Difficulty,
            };
        }

        // ── Interaction ───────────────────────────────────────────────────────

        /// <summary>The port of the travel map's click handler, <c>itemClick</c> (<c>:716-747</c>).</summary>
        internal void OnNodeClicked(string landId)
        {
            if (string.IsNullOrEmpty(landId)) return;

            // `if(page2 == 3 && (pip.travel || World.w.testMode))` — the whole handler is behind the
            // gate, so a click on a node while the sub-tab is elsewhere does nothing.
            if (_subPage != PipInfoFrame.WorldSubPage) return;
            if (!_snapshot.TravelUnlocked && !_snapshot.TestMode) return;

            if (ActionsBlocked?.Invoke() == true)
            {
                Report("the Oracle would refuse with `noAct` (PipPageInfo.as:719-723)");
                return;
            }

            PipLandFacts land = Land(landId);

            if (land == null || !land.CanTravel)
            {
                // `vis.butOk.visible = false; World.w.gui.infoText("noTravel")` (:741-744).
                _targetLand = "";
                RefreshConfirmButton();
                _worldPanel.Refresh(BuildWorldDraws(), _targetLand, PfeUiColors.PageR0);
                Report($"'{landId}' is not travelable, so the Oracle would show `noTravel` " +
                       "(PipPageInfo.as:741-744).");
                return;
            }

            _targetLand = landId;

            // `setStatItems()` then `butOk.visible = true` then `zad.gotoAndStop(2)` on the new node —
            // note this is NOT `setStatus`, so the hover pane and the scroll survive the pick.
            RefreshConfirmButton();
            _worldPanel.Refresh(BuildWorldDraws(), _targetLand, PfeUiColors.PageR0);
        }

        /// <summary>The port of a travel node's <c>MOUSE_OVER</c> handler (<c>:369-423</c>).</summary>
        internal void OnNodeHovered(string landId)
        {
            if (_subPage != PipInfoFrame.WorldSubPage) return;
            if (string.Equals(_hoveredLand, landId, StringComparison.Ordinal)) return;

            _hoveredLand = landId;
            ShowLandDetail();
        }

        /// <summary>The port of <c>butOk</c>'s handler, <c>transOk</c> (<c>:749-786</c>).</summary>
        private void OnConfirm()
        {
            if (ActionsBlocked?.Invoke() == true)
            {
                Report("the Oracle would refuse with `noAct` (PipPageInfo.as:751-755)");
                return;
            }

            if (_subPage == PipInfoFrame.WorldSubPage)
            {
                ConfirmTravel();
                return;
            }

            if (_subPage == PipInfoFrame.QuestSubPage)
            {
                ConfirmAllTask();
            }
        }

        private void ConfirmTravel()
        {
            if (!_snapshot.TravelUnlocked && !_snapshot.TestMode) return;

            PipLandFacts land = Land(_targetLand);

            // `if(game.lands[targetLand] && game.lands[targetLand].loaded)` (:760-763) — the confirm is
            // refused outright for a land whose rooms have not been read, and `beginMission` is what
            // actually moves the player.
            if (land == null || !land.Loaded)
            {
                Report($"travel to '{_targetLand}' was refused: the land is " +
                       (land == null ? "unknown" : "not loaded") + " (PipPageInfo.as:760-763).");
                return;
            }

            if (TravelConfirmed == null)
            {
                Report($"the Oracle would call beginMission('{_targetLand}') and close the device " +
                       "(PipPageInfo.as:762-764), but no TravelConfirmed handler is set");
                return;
            }

            if (!TravelConfirmed(_targetLand)) return;

            // `pip.onoff(-1)` — the device closes.
            CloseRequested?.Invoke();
        }

        private void ConfirmAllTask()
        {
            List<string> grants = TasksToGrant();

            if (grants.Count == 0)
            {
                // The Oracle's loop simply finds nothing and calls `setStatus()`; the button was visible
                // because HasAllTask saw a manual row that TasksToGrant deliberately skips.
                Rebuild();
                return;
            }

            if (QuestsGranted == null)
            {
                Report($"the Oracle would grant {grants.Count} vendor task(s) via addQuest " +
                       "(PipPageInfo.as:775-784), but no QuestsGranted handler is set");
                Rebuild();
                return;
            }

            QuestsGranted(grants);

            // `setStatus()` — the journal is rebuilt so the granted quests appear.
            Rebuild();
        }

        /// <summary>Scrolls the list. Positive moves the window down the list.</summary>
        public void ScrollRows(int delta)
        {
            if (delta == 0) return;

            int next = Mathf.Clamp(_scroll + delta, 0, Mathf.Max(0, EntryCount - VisibleRows));
            if (next == _scroll) return;

            _scroll = next;

            // `statScroll` calls `setStatItems(position)`, which re-windows the rows only — the header,
            // the panels and the detail pane are untouched. Rebuilding all of it would clear the hover,
            // which the Oracle does not do.
            RefreshRows();
        }

        private void Report(string what)
        {
            Debug.Log($"[PipPageInfoView] {what}.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private RectTransform CreateRowRect(string name, Vector2 flash)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(PipInfoFrame.RowWidth, PipBuckLayout.PageRowPitch);
            Place(rect, flash.x, flash.y);
            return rect;
        }

        private Text CreateCell(RectTransform parent, string name, float x, float width, int fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(width, PipBuckLayout.PageRowPitch);
            rect.anchoredPosition = new Vector2(x, 0f);

            Text text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.color = PfeUiColors.PageR0;
            text.alignment = TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            text.text = string.Empty;
            return text;
        }

        private RectTransform CreateTextRect(string name, float x, float y, float width, float height,
            int fontSize, Color colour, out Text text)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(width, height);
            Place(rect, x, y);

            text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.color = colour;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            text.text = string.Empty;
            return rect;
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            return rect;
        }

        /// <summary>
        /// Top-left anchoring, because every coordinate in this file is a Flash coordinate: the Oracle
        /// measures from the page clip's top-left corner with +y pointing <b>down</b>.
        /// </summary>
        private static void AnchorTopLeft(RectTransform rect)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
        }

        /// <summary>The single y-negation for this file — see <c>PipAnchor.ToUnityAnchoredPosition</c>.</summary>
        private static void Place(RectTransform rect, float flashX, float flashY)
        {
            rect.anchoredPosition = new Vector2(flashX, -flashY);
        }

        private static Color WithAlpha(Color colour, float alpha) =>
            new Color(colour.r, colour.g, colour.b, alpha);

        /// <summary>AS3 <c>Math.round</c> — <c>floor(x + 0.5)</c>, so halfway rounds toward +Infinity
        /// rather than to even. A land's <c>dif</c> is an integer already, so this is a formality today;
        /// it is kept as the real function because the Oracle writes <c>Math.round</c> and a later
        /// change to a fractional difficulty would otherwise round to even and differ by one.</summary>
        private static int As3Round(float value) => (int)Math.Floor(value + 0.5);

        private string Pip(string id) => TextTable != null ? TextTable.Pip(id) : "*pip_" + id;

        // ── Event relays ──────────────────────────────────────────────────────
        //
        // Unity delivers pointer and scroll callbacks to a COMPONENT, and this page is a plain class, so
        // these two thin components exist to hand the event straight back. They hold no state and decide
        // nothing — every rule stays in the page, which is the half that can be tested.

        private sealed class RowRelay : MonoBehaviour, IPointerEnterHandler
        {
            public PipPageInfoView Page;
            public int RowIndex;

            public void OnPointerEnter(PointerEventData eventData) => Page?.OnRowHover(RowIndex);
        }

        private sealed class ScrollRelay : MonoBehaviour, IScrollHandler
        {
            public PipPageInfoView Page;

            public void OnScroll(PointerEventData eventData)
            {
                if (Page == null) return;

                // On sub-tab 1 the wheel zooms the map and never scrolls a list: the Oracle's
                // `scroll` override branches on `page2 == 1` and its `scBar` is invisible there because
                // the list is empty (`PipPageInfo.as:836-850`, `PipPage.as:1666-1668`). The map panel has
                // its own relay over the window, so this arm only fires for a wheel outside it.
                if (Page._subPage == PipInfoFrame.MapSubPage) return;

                // Unity: wheel up is a positive y. Flash: wheel up is a positive delta, and the Oracle
                // *decrements* the scroll position for it (PipPage.as:1670-1677). So a positive Unity y
                // is -1 row here.
                Page.ScrollRows(eventData.scrollDelta.y > 0f ? -1 : 1);
            }
        }
    }
}
