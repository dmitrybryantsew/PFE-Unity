using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using PFE.Data.Definitions;
using PFE.UI.Common;

namespace PFE.UI.PipBuck.Pages
{
    /// <summary>
    /// The doctor — <b>page 6</b> of the device, the port of <c>fe.inter.PipPageMed</c>.
    ///
    /// <para><b>Its frame is the inventory's, and that is attested rather than assumed.</b>
    /// <c>PipPageMed</c> never assigns <c>pageClass</c>, so the base constructor's
    /// <c>if (this.pageClass == null) this.pageClass = visPipInv;</c> (<c>PipPage.as:84-88</c>) applies
    /// and the page is built on <b>sprite 1113</b> — the same clip as the inventory and the vendor. That
    /// is why <c>scBar</c> is at (600, 100), <c>nazv</c> at (631, 87), <c>info</c> at (626, 162.1) and
    /// <c>bottext</c> at (19.5, 653.1) here: those are the inventory clip's own placements, and
    /// <see cref="PipRowChrome"/> already carries the first two.</para>
    ///
    /// <para><b>The row art is not imported.</b> The Oracle's row clip is <c>visPipMedItem</c>
    /// (<c>PipPageMed.as:39</c>), a symbol this project does not have — the same gap
    /// <c>PipPageInvView</c> and <c>PipPageVendView</c> record. So a row is three text columns at
    /// coordinates the port chose, tagged <c>PORT CHOICE</c> below, over the imported inventory row band
    /// so it still reads as a list. What <i>is</i> attested and used verbatim is the row origin and pitch
    /// (<c>PipPage.as:112-113</c> through <see cref="PipBuckLayout.PageRowPosition"/>), the row count,
    /// the price column's right edge, and every string.</para>
    ///
    /// <para><b>Two sub-tabs, and the second one is another page.</b> <c>PipPageMed.as:41</c> hides
    /// but3, but4 and but5; <c>:48-57</c> hides but2 as well when the doctor is paid in gel or in goods.
    /// Clicking but2 sets <c>page2 = 1</c> and calls <c>pip.onoff(4)</c> (<c>:247-252</c>) — the trade
    /// page. That rule lives in <see cref="PipSubTabs.NavigatesToPage"/> and is executed by
    /// <see cref="PipBuckManager.SubPageClick"/>; the page only reports the click.</para>
    /// </summary>
    public sealed class PipPageMedView : IPipPage, IPipContextPage
    {
        /// <summary>The Oracle's page index for the doctor — <c>pages[6]</c> (<c>PipBuck.as:111</c>).</summary>
        public const int MedPageId = 6;

        // ── Column geometry ───────────────────────────────────────────────────
        //
        // PORT CHOICE. The Oracle positions these inside the row clip `visPipMedItem`, which was not
        // imported, so there is no attested offset to read. The three columns are sized to the three
        // things the Oracle writes per row — a label, a reading, a price — inside the 560-px row panel
        // that the imported band art covers and that the vendor page's four columns already live in.
        // The name column is wide because "Body condition:" and the indented organ labels are long; the
        // price column is right-aligned against the panel's edge, as the vendor page's is.

        /// <summary>Label column x, inside the row.</summary>
        private const float NameX = 6f;

        /// <summary>Label column width — "Body condition:" plus room for the three-space organ indent.</summary>
        private const float NameWidth = 300f;

        /// <summary>Reading column x — the Oracle's <c>numb</c>, e.g. <c>143/200</c>.</summary>
        private const float ValueX = 312f;

        private const float ValueWidth = 140f;

        /// <summary>Price column x — the Oracle's <c>price</c>, the caps a click costs.</summary>
        private const float PriceX = 458f;

        private const float PriceWidth = 96f;

        /// <summary>The row panel's width — the imported band art's own (<c>visPipInvItem.back</c>).</summary>
        private const float RowWidth = PipRowChrome.RowBackWidth;

        private const float BarWidth = 150f;
        private const float BarHeight = 5f;
        private const float BarY = 24f;

        private const int RowFontSize = 15;
        private const int HeaderFontSize = 15;

        // The Oracle's `vis` placements for the two text areas this page uses, read out of the SWF for
        // sprite 1113 and shared with PipPageVendView: `nazv` (631, 87) right-aligned over the info
        // pane, `info` (626, 162.1) below it, `bottext` (19.5, 653.1) along the bottom.

        private const float InfoNameX = 631f;
        private const float InfoNameY = 87f;
        private const float InfoNameWidth = 283f;
        private const int InfoNameFontSize = 20;

        private const float InfoTextX = 626f;
        private const float InfoTextY = 162.1f;
        private const float InfoTextWidth = 286f;
        private const float InfoTextBottom = 640f;
        private const int InfoTextFontSize = 14;

        private const float BottomTextX = 19.5f;
        private const float BottomTextY = 653.1f;
        private const float BottomTextWidth = 500f;

        // ── Wiring ────────────────────────────────────────────────────────────

        private readonly IPipMedSource _source;
        private readonly PipSubTabBar _subTabs = new PipSubTabBar();

        private PipBuckCompositionDefinition _composition;
        private Font _font;

        /// <summary>The Oracle's localisation document — every caption on this page is a lookup in it.</summary>
        public PipTextTable TextTable { get; set; } = PipTextTable.Empty;

        /// <summary>
        /// What opened the page — <c>pip.npcId</c> / <c>pip.npcInter</c>. The sub-tab row reads
        /// <see cref="PipSubTabContext.NpcInter"/> to decide whether the Trading tab exists at all
        /// (<c>PipPageMed.as:48-57</c>), and the source reads it to decide which purse to draw.
        /// </summary>
        public PipSubTabContext Context { get; private set; } = PipSubTabContext.Default;

        /// <summary>The sub-tab row, for the device to hang its click handler off.</summary>
        public PipSubTabBar SubTabs => _subTabs;

        /// <summary>The page's own <c>page2Click</c> — forwarded to the device, which owns the redirect.</summary>
        public Action<int> SubPageClicked { get; set; }

        /// <summary>
        /// Raised after a click that was accepted, so the device can play <c>pip.snd(1)</c>
        /// (<c>PipPageMed.as:446</c>). The Oracle plays it even for a click that healed nothing — the
        /// section header's click is a sound and a repaint and no more — so this fires for every
        /// handled click, not only for a successful treatment.
        /// </summary>
        public Action Clicked { get; set; }

        /// <summary>The player's level is below this for the charity rule (<c>PipPageMed.as:343</c>).</summary>
        public const int CharityLevelCeiling = 6;

        // ── Built objects ─────────────────────────────────────────────────────

        private RectTransform _root;

        /// <summary>The list scrollbar — the Oracle's <c>scBar</c>. Shared chrome with every other list page.</summary>
        private readonly PipRowChrome.Scrollbar _scrollbar = new PipRowChrome.Scrollbar();

        private readonly RectTransform[] _rowRects = new RectTransform[PipBuckLayout.PageRowCount];
        private readonly Text[] _nameCells = new Text[PipBuckLayout.PageRowCount];
        private readonly Text[] _valueCells = new Text[PipBuckLayout.PageRowCount];
        private readonly Text[] _priceCells = new Text[PipBuckLayout.PageRowCount];
        private readonly Image[] _rowBars = new Image[PipBuckLayout.PageRowCount];

        private Text _headerPrice;
        private Text _infoName;
        private Text _infoText;
        private Text _bottomText;

        // ── State ─────────────────────────────────────────────────────────────

        private int _scroll;
        private string _hoveredId = string.Empty;

        /// <summary>The rows currently drawn — kept so a click and a hover can map a row clip back to one.</summary>
        private readonly List<PipMedRow> _rows = new List<PipMedRow>();

        /// <summary>The snapshot the current rows were built from. The footer's balance comes from it.</summary>
        private PipMedSnapshot _snapshot = new PipMedSnapshot();

        public PipPageMedView(IPipMedSource source)
        {
            _source = source;
        }

        // ── IPipPage ──────────────────────────────────────────────────────────

        public int PageId => MedPageId;

        /// <summary>True: this page has content. The shell's "not implemented" notice stays hidden.</summary>
        public bool IsImplemented => true;

        /// <summary>
        /// The Oracle's <c>page2</c>. Always 1 on this page — its only other value is the trade page,
        /// which is a different page id. See <see cref="PipMedList"/>'s class summary.
        /// </summary>
        public int SubPage => 1;

        /// <summary>The scroll offset, in rows. Always 0 in practice — the list is shorter than the window.</summary>
        public int ScrollOffset => _scroll;

        /// <summary>The rows currently drawn, for a test or a debug overlay.</summary>
        public IReadOnlyList<PipMedRow> Rows => _rows;

        /// <summary>The last snapshot read, for a test or a debug overlay.</summary>
        public PipMedSnapshot Snapshot => _snapshot;

        public void Configure(PipBuckCompositionDefinition composition, Font font, PipTextTable text)
        {
            _composition = composition;
            _font = font;
            TextTable = text ?? PipTextTable.Empty;
        }

        /// <summary>
        /// The vendor the doctor trades through, or empty — <c>pip.vendor</c>'s identity. Kept on the
        /// page because it is part of the clinic's context and must be dropped with it
        /// (<see cref="Close"/>), exactly as the trade page drops its own.
        /// </summary>
        public string ClinicVendorId { get; private set; } = string.Empty;

        /// <summary>
        /// The hand-off — <c>NPC.pip(6)</c> (<c>NPC.as:250-260</c>): the device's context fields are set
        /// and then page 6 is opened. There is no vendor reset on this path, unlike page 4: a doctor's
        /// shelf is not what the clinic shows, and resetting it would restock it for free.
        /// </summary>
        public void Open(string npcId, string npcInter, string vendorId = null)
        {
            Context = new PipSubTabContext(npcId, npcInter, null, false);
            ClinicVendorId = vendorId ?? string.Empty;
            _scroll = 0;
            _hoveredId = string.Empty;
            ClearInfoPane();

            Rebuild();
        }

        /// <summary><c>PipBuck.onoff(-1)</c> clears the context (<c>PipBuck.as:323-324</c>).</summary>
        public void Close()
        {
            Context = PipSubTabContext.Default;
            ClinicVendorId = string.Empty;
            _hoveredId = string.Empty;
            ClearInfoPane();
        }

        /// <summary>
        /// The device closed — drop the clinic context. Called by <c>PipBuckManager.OnClosed</c> for
        /// every registered <see cref="IPipContextPage"/>, so a clinic the player walked away from does
        /// not colour a later visit.
        /// </summary>
        public void OnDeviceClosed()
        {
            Close();
            Rebuild();
        }

        /// <summary>
        /// The port of <c>PipPage.setStatus</c>'s sub-page half. A non-zero value is passed through
        /// <see cref="PipSubTabs.Coerce"/> and then ignored, because this page has one sub-page; the
        /// scroll and the hover are reset either way, which is the Oracle's behaviour on every repaint.
        /// </summary>
        public void SetStatus(int subPage)
        {
            _scroll = 0;
            _hoveredId = string.Empty;
            Rebuild();
        }

        /// <summary>
        /// Shows or hides the page's root — pure visibility, and nothing else.
        ///
        /// <para><b>Not <see cref="Close"/>.</b> <c>PipBuck.setPage</c> hides every page before showing
        /// the target (<c>PipBuck.as:417-423</c>), so a <c>SetVisible</c> that dropped the context would
        /// wipe the <c>npcInter</c> one statement after <see cref="Open"/> wrote it — which is exactly
        /// the bug the vendor page shipped with ("no traders, no quest tab").</para>
        /// </summary>
        public void SetVisible(bool visible)
        {
            if (_root == null) return;
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        /// <summary>
        /// One 30 Hz interface tick. <c>PipPageMed</c> does not override <c>step</c>, and this page's
        /// content is static between rebuilds, so there is nothing to advance. Deliberately silent —
        /// <c>Step</c> runs 30 times a second.
        /// </summary>
        public void Step()
        {
        }

        public void Build(RectTransform pageRoot)
        {
            if (pageRoot == null)
            {
                Debug.LogError("[PipPageMedView] Build called with no page root. The doctor's page will " +
                               "not exist; page 6 will show an empty screen.");
                return;
            }

            // Idempotent: a canvas resize re-runs the shell's layout, and a second Build would stack a
            // second copy of every row on top of the first — which reads as doubled text, not an error.
            if (_root != null) return;

            _root = CreateRect("PipMed Page", pageRoot);

            // One scroll relay on the page root, not one per row: Unity's event system walks UP from the
            // raycast hit, so a relay on the parent serves every row.
            _root.gameObject.AddComponent<ScrollRelay>().Page = this;

            _subTabs.Build(_root, _font, _composition != null ? _composition.subCategoryButton : null);

            // The forwarder, so a handler set after Build still takes effect. Build runs last in
            // PipBuckBootstrapper.RegisterPages, which is the trap the character sheet already hit.
            _subTabs.SubPageClicked = subPage => SubPageClicked?.Invoke(subPage);

            BuildHeader();
            BuildRows();
            _scrollbar.Build(_root, _composition);
            BuildInfoPane();
            BuildBottomText();

            Rebuild();
            SetVisible(false);

            // Said once, at build time, because this page can be three rows shorter than the Oracle's and
            // a player comparing against a screenshot deserves to know why rather than to wonder whether
            // radiation is broken. The condition is the source's, not this file's: the rows appear the
            // day a source reports exposure readings.
            Debug.Log("[PipPageMedView] Built. The radiation, open-wound and poison rows are listed only " +
                      "when the source reports exposure readings (PipMedSnapshot.HasExposureReadings); the " +
                      "port keeps none of the three (the Oracle heals them through " +
                      "`UnitPlayer.heal(_, 2|3|4)`, UnitPlayer.as:3171-3201). The health pool and the five " +
                      "organs are complete.");
        }

        // ── Construction ──────────────────────────────────────────────────────

        /// <summary>
        /// The header row — <c>PipPageMed.as:71-74</c>. A row like any other, one pitch above row 0,
        /// with its <c>hpbar</c> hidden, its <c>nazv</c> and <c>numb</c> cleared and only its
        /// <c>price</c> column captioned "Healing cost".
        /// </summary>
        private void BuildHeader()
        {
            RectTransform header = CreateRowRect("HeaderRow", PipBuckLayout.PageHeaderPosition(), false);
            _headerPrice = CreateCell(header, "HeaderPrice", PriceX, PriceWidth, HeaderFontSize,
                PfeUiColors.PageYellow, TextAnchor.MiddleRight);
        }

        /// <summary>
        /// One row rect — a list row <b>and</b> the header row. Shared so the two cannot drift apart:
        /// the version of this code that lived only inside the row loop is what put the character
        /// sheet's header on the page root's corner, level with the sub-tabs.
        /// </summary>
        private RectTransform CreateRowRect(string name, Vector2 flash, bool withHitArea)
        {
            Type[] parts = withHitArea
                ? new[] { typeof(RectTransform), typeof(CanvasRenderer), typeof(Image) }
                : new[] { typeof(RectTransform) };

            var go = new GameObject(name, parts);
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(RowWidth, PipBuckLayout.PageRowPitch);
            Place(rect, flash.x, flash.y);
            return rect;
        }

        private void BuildRows()
        {
            for (int row = 0; row < PipBuckLayout.PageRowCount; row++)
            {
                RectTransform rect = CreateRowRect("Row" + row, PipBuckLayout.PageRowPosition(row), true);
                _rowRects[row] = rect;

                // The Oracle's row `back`, behind everything else in the row. The doctor's own clip is
                // `visPipMedItem`, never imported, so the imported inventory band stands in — the same
                // substitution the character sheet and the info page already make. Without it the rows
                // would be transparent text floating next to the inventory's banded list.
                PipRowChrome.AddRowBack(rect, _composition != null ? _composition.inventoryRowBack : null,
                    PipRowChrome.RowBackWidth);

                // A fully transparent Image still receives raycasts, so this is what makes the row
                // hoverable and clickable without drawing anything of its own.
                Image background = rect.GetComponent<Image>();
                background.color = new Color(1f, 1f, 1f, 0f);

                var relay = rect.gameObject.AddComponent<RowRelay>();
                relay.Page = this;
                relay.RowIndex = row;

                _nameCells[row] = CreateCell(rect, "Name", NameX, NameWidth, RowFontSize,
                    PfeUiColors.PageR0, TextAnchor.MiddleLeft);
                _valueCells[row] = CreateCell(rect, "Value", ValueX, ValueWidth, RowFontSize,
                    PfeUiColors.PageR0, TextAnchor.MiddleRight);
                _priceCells[row] = CreateCell(rect, "Price", PriceX, PriceWidth, RowFontSize,
                    PfeUiColors.PageR0, TextAnchor.MiddleRight);

                _rowBars[row] = CreateBar(rect);
            }
        }

        private Image CreateBar(RectTransform row)
        {
            var go = new GameObject("Bar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(row, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(BarWidth, BarHeight);
            rect.anchoredPosition = new Vector2(ValueX, -BarY);

            Image image = go.GetComponent<Image>();
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillOrigin = (int)Image.OriginHorizontal.Left;
            image.color = PfeUiColors.PageGreen;
            image.raycastTarget = false;
            image.enabled = false;
            return image;
        }

        private Text CreateCell(RectTransform parent, string name, float x, float width, int fontSize,
            Color colour, TextAnchor alignment)
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
            text.color = colour;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            text.text = string.Empty;
            return text;
        }

        /// <summary>
        /// The hover pane — <c>statInfo</c> (<c>PipPageMed.as:226-239</c>): the row's own name in
        /// <c>vis.nazv</c> and the <c>p</c> table's <c>&lt;info&gt;</c> paragraph in <c>vis.info</c>.
        ///
        /// <para><b>It is a real pane and not decoration.</b> The id it looks up is the row's key, and
        /// the keys are the document's own <c>statHead1</c>, <c>statTors2</c>, <c>organism</c>… so the
        /// explanation a player reads is the one that belongs to <i>that wound stage</i> — a head at
        /// stage 2 gets a different paragraph from one at stage 0.</para>
        /// </summary>
        private void BuildInfoPane()
        {
            _infoName = CreateLabel("nazv", InfoNameX, InfoNameY, InfoNameWidth, 61f, InfoNameFontSize,
                TextAnchor.UpperRight);

            _infoText = CreateLabel("info", InfoTextX, InfoTextY, InfoTextWidth,
                InfoTextBottom - InfoTextY, InfoTextFontSize, TextAnchor.UpperLeft);
            _infoText.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        /// <summary>
        /// A standalone text field placed in the Oracle's page-clip coordinates — the shape
        /// <see cref="CreateCell"/> cannot express, because a cell is a child of a positioned row.
        /// </summary>
        private Text CreateLabel(string name, float x, float y, float width, float height,
            int fontSize, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(width, height);
            Place(rect, x, y);

            Text text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.color = PfeUiColors.PageR0;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            text.text = string.Empty;
            return text;
        }

        private void BuildBottomText()
        {
            var go = new GameObject("bottext", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(BottomTextWidth, 30f);
            Place(rect, BottomTextX, BottomTextY);

            _bottomText = go.GetComponent<Text>();
            _bottomText.font = _font;
            _bottomText.fontSize = 15;
            _bottomText.color = PfeUiColors.PageR0;
            _bottomText.alignment = TextAnchor.MiddleLeft;
            _bottomText.supportRichText = true;   // the balance arrives as a <color> tag
            _bottomText.raycastTarget = false;
            _bottomText.text = string.Empty;
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        /// <summary>
        /// Re-reads the source and repaints everything — the port of <c>setSubPages</c> plus the row loop
        /// in <c>setStatItems</c>.
        /// </summary>
        public void Rebuild()
        {
            if (_root == null) return;

            _snapshot = _source?.Read() ?? new PipMedSnapshot();

            _rows.Clear();
            _rows.AddRange(PipMedList.RowsFor(_snapshot, TextTable));

            // The Oracle clamps implicitly through the scrollbar's own min/max (PipPage.as:944-949);
            // the port clamps here because a rebuild can shorten the list under a scrolled page.
            int maxScroll = Mathf.Max(0, _rows.Count - PipBuckLayout.PageRowCount);
            if (_scroll > maxScroll) _scroll = maxScroll;
            if (_scroll < 0) _scroll = 0;

            RefreshSubTabs();
            RefreshHeader();
            RefreshRows();
            _scrollbar.Refresh(_rows.Count, PipBuckLayout.PageRowCount, _scroll, maxScroll);
            RefreshBottomText();
        }

        private void RefreshSubTabs()
        {
            // PipPageMed has no setSigns override, so all five markers are 0 (PipPage.as:962-965).
            _subTabs.Refresh(PageId, 1, TextTable, Context, PipSubTabSignals.None);
        }

        private void RefreshHeader()
        {
            if (_headerPrice == null) return;
            _headerPrice.text = PipMedList.HeaderPrice(TextTable);
        }

        private void RefreshRows()
        {
            for (int row = 0; row < PipBuckLayout.PageRowCount; row++)
            {
                int entryIndex = row + _scroll;
                bool visible = entryIndex >= 0 && entryIndex < _rows.Count;

                if (_rowRects[row] != null && _rowRects[row].gameObject.activeSelf != visible)
                {
                    _rowRects[row].gameObject.SetActive(visible);
                }

                if (!visible) continue;

                PipMedRow entry = _rows[entryIndex];

                if (_nameCells[row] != null) _nameCells[row].text = entry.Name;
                if (_valueCells[row] != null) _valueCells[row].text = entry.Value;

                if (_priceCells[row] != null)
                {
                    // PipPageMed.as:211-218 — `Math.round(param2.price)`, or an empty field when the row
                    // carries no price at all. The section header is the only such row.
                    _priceCells[row].text = entry.HasPrice
                        ? entry.DisplayPrice.ToString()
                        : string.Empty;
                }

                RefreshBar(row, entry);
            }
        }

        private void RefreshBar(int row, PipMedRow entry)
        {
            Image bar = _rowBars[row];
            if (bar == null) return;

            if (!entry.HasBar)
            {
                bar.enabled = false;
                return;
            }

            bar.enabled = true;
            bar.fillAmount = entry.Bar;
        }

        private void RefreshBottomText()
        {
            if (_bottomText == null) return;
            _bottomText.text = PipMedList.BottomText(_snapshot, TextTable);
        }

        // ── Interaction ───────────────────────────────────────────────────────

        /// <summary>The port of the row's <c>MOUSE_OVER</c> handler, <c>statInfo</c> (<c>PipPageMed.as:226-239</c>).</summary>
        internal void OnRowHover(int rowIndex)
        {
            PipMedRow? row = RowAt(rowIndex);
            if (row == null) return;

            string id = row.Value.Id;
            if (id == _hoveredId) return;
            _hoveredId = id;

            // :229-234 — an empty id clears the pane; anything else names the row and explains it.
            if (string.IsNullOrEmpty(id))
            {
                ClearInfoPane();
                return;
            }

            if (_infoName != null) _infoName.text = Localise("p", id);
            if (_infoText != null) _infoText.text = InfoFor(id);
        }

        private void ClearInfoPane()
        {
            if (_infoName != null) _infoName.text = string.Empty;
            if (_infoText != null) _infoText.text = string.Empty;
        }

        /// <summary>
        /// The port of the row's click handler, <c>itemClick</c> (<c>PipPageMed.as:278-450</c>).
        ///
        /// <para><b>The snapshot is re-read here rather than reused from the last rebuild.</b> That is
        /// the Oracle: <c>itemClick</c> recomputes the cost from the live organ reading before charging,
        /// so a page left open while something healed underneath it charges for the state at the moment
        /// of the click and not for the state at the moment of the paint.</para>
        /// </summary>
        internal void OnRowClicked(int rowIndex)
        {
            PipMedRow? row = RowAt(rowIndex);
            if (row == null) return;

            PipMedSnapshot snapshot = _source?.Read() ?? new PipMedSnapshot();

            // :282-286 — `if(pip.noAct) { World.w.gui.infoText("noAct"); return; }`. No sound, no
            // repaint, and no charge: the gate is before everything.
            if (snapshot.ActionsBlocked)
            {
                Debug.Log("[PipPageMedView] A doctor's row was clicked while the interface is not " +
                          "accepting actions (PipPageMed.as:282-286, `pip.noAct`). Nothing was healed " +
                          "and nothing was charged.");
                return;
            }

            PipMedPlan plan = PipMedList.PlanFor(snapshot, row.Value.Id);

            if (!plan.Acts)
            {
                // The section header, and anything else the Oracle's ladder does not match. It still
                // plays the click and repaints (:446-449); it heals nothing.
                _snapshot = snapshot;
                Rebuild();
                Clicked?.Invoke();
                return;
            }

            bool applied = _source != null && _source.TryApply(plan);

            // :446-449 — `pip.snd(1); setStatus(); this.showBottext(); pip.setRPanel();` in that order,
            // unconditionally. The repaint is what makes the row's new reading and the new balance
            // appear, and it happens whether or not the treatment landed.
            Rebuild();

            if (applied)
            {
                Debug.Log($"[PipPageMedView] '{row.Value.Id}' treated for {PipBuckReadoutFormat.As3Round(plan.Cost)} " +
                          $"(PipPageMed.as:289-441): +{Mathf.Round(plan.Heal)} to organ {plan.OrganType}" +
                          (plan.IsCharity ? " — free, the charity branch (:343)." : "."));
            }

            Clicked?.Invoke();
        }

        /// <summary>Scrolls the list. Positive moves the window down the list.</summary>
        public void ScrollRows(int delta)
        {
            if (delta == 0) return;

            int maxScroll = Mathf.Max(0, _rows.Count - PipBuckLayout.PageRowCount);
            int next = Mathf.Clamp(_scroll + delta, 0, maxScroll);
            if (next == _scroll) return;

            _scroll = next;
            Rebuild();
        }

        /// <summary>The row shown at a row-clip index, or null when that clip is empty.</summary>
        private PipMedRow? RowAt(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= PipBuckLayout.PageRowCount) return null;
            if (_rowRects[rowIndex] == null || !_rowRects[rowIndex].gameObject.activeSelf) return null;

            int entryIndex = rowIndex + _scroll;
            if (entryIndex < 0 || entryIndex >= _rows.Count) return null;

            return _rows[entryIndex];
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// <c>Res.txt("p", id, 1)</c> — the row's explanation paragraph. Empty on a miss, which is what
        /// the Oracle's <c>try/catch</c> produces too (<c>Res.as:68-86</c>): a row with no entry in the
        /// document shows no pane rather than a marker.
        /// </summary>
        private string InfoFor(string id)
        {
            return TextTable != null && TextTable.TryGetInTable("pip", id, 1, out string info)
                ? info
                : string.Empty;
        }

        /// <summary>
        /// A caption, or the Oracle's own missing-key marker (<c>Res.as:120-130</c>) — the same
        /// arrangement <c>PipPageStatView</c> uses, so a document that failed to load says so in the
        /// place the word should be rather than leaving a blank.
        /// </summary>
        private string Localise(string alias, string id)
        {
            if (TextTable != null && TextTable.TryGet(alias, id, 0, out string value)) return value;
            return "*" + alias + "_" + id;
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

        /// <summary>
        /// Places a child at a Flash coordinate. The single y-negation for this file — see
        /// <c>PipAnchor.ToUnityAnchoredPosition</c>, the same flip the shell and every other page use.
        /// </summary>
        private static void Place(RectTransform rect, float flashX, float flashY)
        {
            rect.anchoredPosition = new Vector2(flashX, -flashY);
        }

        // ── Event relays ──────────────────────────────────────────────────────
        //
        // Unity delivers pointer and scroll callbacks to a COMPONENT and this page is a plain class, so
        // these two thin components hand the event straight back. They hold no state and decide nothing.

        private sealed class RowRelay : MonoBehaviour, IPointerEnterHandler, IPointerClickHandler
        {
            public PipPageMedView Page;
            public int RowIndex;

            public void OnPointerEnter(PointerEventData eventData) => Page?.OnRowHover(RowIndex);

            public void OnPointerClick(PointerEventData eventData) => Page?.OnRowClicked(RowIndex);
        }

        private sealed class ScrollRelay : MonoBehaviour, IScrollHandler
        {
            public PipPageMedView Page;

            public void OnScroll(PointerEventData eventData)
            {
                // Unity: wheel up is a positive y. Flash: wheel up is a positive delta, and the Oracle
                // decrements the scroll position for it (PipPage.as:1674-1677). So a positive Unity y
                // is -1 row here.
                Page?.ScrollRows(eventData.scrollDelta.y > 0f ? -1 : 1);
            }
        }
    }
}
