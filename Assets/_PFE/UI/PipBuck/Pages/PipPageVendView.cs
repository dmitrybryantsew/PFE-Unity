using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using PFE.Data.Definitions;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Trade;
using PFE.UI.Common;

namespace PFE.UI.PipBuck.Pages
{
    /// <summary>
    /// The trade page — <b>page 4</b> of the device, the port of <c>fe.inter.PipPageVend</c>.
    ///
    /// <para><b>It is a PipBuck page, not a trade screen.</b> <c>pages[4]</c> is opened by
    /// <c>pip.onoff(4)</c> from an NPC (<c>NPC.as:236-239, 250-260</c>); the device supplies the tab,
    /// the sub-tab row, the sound and the caps readout. See <c>docs/TradersAndTrade/00_INDEX.md</c>
    /// non-negotiable #1.</para>
    ///
    /// <para><b>The four sub-pages are the Oracle's <c>page2</c> 1..4</b> — Buy, Sell, Repair, Quests
    /// (<c>PipPageVend.as:119-434</c>). Which of them exist is <see cref="PipSubTabs"/>' answer, not
    /// this page's: <c>but5</c> is hidden for every vendor (<c>:40</c>), Quests needs an NPC
    /// (<c>:86-93</c>), and a plain vending machine (<c>npcInter == "v"</c>) has no Repair
    /// (<c>:102-109</c>).</para>
    ///
    /// <para><b>The frame is the inventory's.</b> The Oracle does not set <c>pageClass</c>, so it
    /// defaults to <c>visPipInv</c> (1113) — the same 18-row list, the same scrollbar at (600, 100) and
    /// the same info pane at (626..912) the inventory page draws. Those numbers are read out of the SWF
    /// and are shared with <see cref="PipPageInvView"/>; the row <i>clip</i> is not: the Oracle's is
    /// <c>visPipBuyItem</c> (symbol 963), <b>which this project has not imported</b>. So the rows here
    /// are drawn as text at the Oracle's row pitch with a plain tint for the selection — see
    /// <see cref="RowBackTint"/>. This is the precedent <c>PipPageInvView</c> set before its art
    /// landed, and the alternative (drawing the inventory's <c>back</c>/<c>ramka</c> art, which belongs
    /// to a different symbol) would be a stand-in that looks finished.</para>
    /// </summary>
    public sealed class PipPageVendView : IPipPage, IPipContextPage
    {
        /// <summary>The Oracle's page index for the vendor — <c>pages[4]</c> (<c>PipBuck.as:111</c>).</summary>
        public const int VendorPageId = 4;

        private const int ColumnCount = 4;

        // ── Row geometry ──────────────────────────────────────────────────────
        //
        // PORT CHOICE, and named as one: the vendor row clip (visPipBuyItem, 963) has not been
        // imported, so its DefineEditText placements cannot be read. These four columns are chosen to
        // line up with the inventory's row panel (560 wide from x=30) and with the four quantities the
        // Oracle writes per sub-page (PipPageVend.as:516-568): name, condition/have, count, price.
        // When 963 lands, replace these with its display list — nothing else in this file changes.

        private static readonly float[] ColumnX = { 6f, 246f, 392f, 470f };
        private static readonly float[] ColumnWidth = { 236f, 142f, 74f, 84f };
        private static readonly bool[] ColumnRight = { false, true, true, true };

        /// <summary>Height of a text field inside a row — the inventory's own field height.</summary>
        private const float RowTextHeight = 21.9f;
        private const float RowTextY = 3f;
        private const int RowFontSize = 15;

        /// <summary>The row's clickable footprint — the list panel's, 560 wide.</summary>
        private static readonly Vector2 RowHitSize = new Vector2(560f, PipBuckLayout.PageRowPitch);

        // ── Page-level geometry (sprite 1113, visPipInv) ──────────────────────
        //
        // ATTESTED — the same dump PipPageInvView documents: scBar at (600, 100) 17x536, nazv at
        // (631, 87) right-aligned, info at (626, 162.1), bottext at (19.5, 653.1), and butOk (508)
        // at (679.7, 651.1) 187x28 (PipInfoFrame.ConfirmButtonX/Y/Width/Height).

        private const float ListScrollbarX = 600f;
        private const float ListScrollbarY = 100f;
        private const float ListScrollbarWidth = 17f;
        private const float ListScrollbarHeight = 536f;
        private const float ThumbMinHeight = 54f;

        private const float InfoNameX = 631f;
        private const float InfoNameY = 87f;
        private const float InfoNameWidth = 283f;
        private const float InfoNameHeight = 61f;
        private const int InfoNameFontSize = 20;

        private const float InfoTextX = 626f;
        private const float InfoTextY = 162.1f;
        private const float InfoTextWidth = 286f;
        private const float InfoTextBottom = 640f;
        private const int InfoTextFontSize = 14;

        private const float EmptyTextX = 19.5f;
        private const float EmptyTextY = 86.2f;
        private const float EmptyTextWidth = 569.45f;

        private const float BottomTextX = 19.5f;
        private const float BottomTextY = 653.1f;
        private const float BottomTextWidth = 500f;

        /// <summary>The selection tint — a port device, not the Oracle's <c>ramka</c> frame.</summary>
        private static readonly Color RowBackTint = new Color(1f, 1f, 1f, 0.10f);

        // ── Wiring ────────────────────────────────────────────────────────────

        private readonly PipBuckViewModel _viewModel;
        private readonly IVendorPageSource _source;

        private PipBuckCompositionDefinition _composition;
        private Font _font;

        /// <summary>The Oracle's localisation document — <c>Res.pipText</c>.</summary>
        public PipTextTable TextTable { get; set; } = PipTextTable.Empty;

        /// <summary>What opened the page — <c>pip.npcId</c> / <c>pip.npcInter</c>.</summary>
        public PipSubTabContext Context { get; private set; } = PipSubTabContext.Default;

        /// <summary>The sub-tab row, for the device to hang its click handler off.</summary>
        public PipSubTabBar SubTabs => _subTabs;

        /// <summary>The page's own <c>page2Click</c> — forwarded to the device.</summary>
        public Action<int> SubPageClicked { get; set; }

        /// <summary>Raised after a transaction lands, so the device can play <c>pip.snd(3)</c>.</summary>
        public Action TransactionCommitted { get; set; }

        private readonly PipSubTabBar _subTabs = new PipSubTabBar();

        private RectTransform _root;
        private int _subPage = (int)VendorSubPage.Buy;
        private int _scroll;

        private VendorRuntime _vendor;
        private string _npcId = string.Empty;
        private string _npcInter = string.Empty;

        /// <summary>The sell-side ledger. AS3 keeps it on the <c>Item</c> rows; the port's bag has no
        /// <c>bou</c> field, so the selection lives here and is cleared on every open.</summary>
        private readonly Dictionary<string, int> _sellBou = new Dictionary<string, int>(StringComparer.Ordinal);

        // ── Row state ─────────────────────────────────────────────────────────

        private readonly List<VendorBuyRow> _buyRows = new List<VendorBuyRow>();
        private readonly List<VendorSellRow> _sellRows = new List<VendorSellRow>();
        private readonly List<VendorRepairRow> _repairRows = new List<VendorRepairRow>();
        private readonly List<VendorQuestRow> _questRows = new List<VendorQuestRow>();

        private readonly RectTransform[] _rowRects = new RectTransform[PipBuckLayout.PageRowCount];
        private readonly Image[] _rowBacks = new Image[PipBuckLayout.PageRowCount];
        private readonly Text[,] _cells = new Text[PipBuckLayout.PageRowCount, ColumnCount];
        private readonly Text[] _headerCells = new Text[ColumnCount];

        private RectTransform _listScrollbar;
        private RectTransform _listThumb;
        private Text _infoName;
        private Text _infoText;
        private Text _emptyText;
        private Text _bottomText;
        private Text _messageText;

        private RectTransform _confirmRect;
        private Image _confirmBackground;
        private Text _confirmLabel;

        /// <summary>The last refusal, shown in the footer — <c>infoText("noMoney")</c> and friends
        /// (<c>PipPageVend.as:705-714, 962-966</c>).</summary>
        private string _message;

        private int _hoveredRow = -1;

        public PipPageVendView(PipBuckViewModel viewModel, IVendorPageSource source)
        {
            _viewModel = viewModel;
            _source = source;
        }

        // ── IPipPage ──────────────────────────────────────────────────────────

        public int PageId => VendorPageId;

        public bool IsImplemented => true;

        /// <summary>The sub-page currently listed — the Oracle's <c>page2</c>, 1..4.</summary>
        public int SubPage => _subPage;

        /// <summary>The scroll offset, in rows.</summary>
        public int ScrollOffset => _scroll;

        /// <summary>The vendor whose shelf is on screen, or null.</summary>
        public VendorRuntime Vendor => _vendor;

        public void Configure(PipBuckCompositionDefinition composition, Font font, PipTextTable text)
        {
            _composition = composition;
            _font = font;
            TextTable = text ?? PipTextTable.Empty;
        }

        /// <summary>
        /// The hand-off — <c>NPC.pip(4)</c> (<c>NPC.as:250-260</c>) plus the two things
        /// <c>PipBuck.onoff(4)</c> does to the vendor: <c>vendor.reset()</c> (<c>PipBuck.as:287-290</c>)
        /// and <c>vendor.multPrice = pers.barterMult</c> (<c>PipBuck.as:414</c>).
        ///
        /// <para><b>The order is the Oracle's and it is load-bearing.</b> <c>reset()</c> clears the
        /// buy ledger; <c>setPage</c> then overwrites <c>multPrice</c>. <c>setRndBuys</c> may already
        /// have raised <c>multPrice</c> to 0.8..1.3, and the row price takes the <b>max</b> of that and
        /// the player's value (<c>PipPageVend.as:154-158</c>) — so overwriting here does not lose the
        /// markup, it just stops the player's number from being the only one.</para>
        /// </summary>
        public void Open(VendorRuntime vendor, string npcId, string npcInter)
        {
            _vendor = vendor;
            _npcId = npcId ?? string.Empty;
            _npcInter = npcInter ?? string.Empty;
            Context = new PipSubTabContext(_npcId, _npcInter, null, false);

            _subPage = (int)VendorSubPage.Buy;
            _scroll = 0;
            _message = null;
            _hoveredRow = -1;
            _sellBou.Clear();

            if (_vendor != null)
            {
                _vendor.Reset();
                if (_source != null) _vendor.MultPrice = _source.BarterMultiplier;
            }

            Rebuild();
        }

        /// <summary>
        /// Write the device's three vendor-context fields <b>without</b> opening this page — the
        /// context half of <c>NPC.pip</c> (<c>NPC.as:250-260</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is not <see cref="Open"/>.</b> <c>pip</c> sets
        /// <c>pip.vendor</c>/<c>npcId</c>/<c>npcInter</c> and then opens whichever page the caller asked
        /// for; the vendor's stock reset belongs to <c>PipBuck.onoff(4)</c>/<c>setPage</c>, not to the
        /// field assignment. <see cref="Open"/> does the reset, so calling it from the clinic path would
        /// refill a doctor's shelves merely because the clinic was opened.</para>
        ///
        /// <para><b>Why it matters at all.</b> A <c>doc</c> never reaches page 4 through
        /// <c>activate()</c> — the doctor branch pre-empts the vendor branch — so the Vendor tab
        /// (which lights on page 6, <c>PipBuck.as:222-225</c>) is the only road to a doctor's trade. It
        /// shows this page, and this page shows whatever context it was last given.</para>
        /// </remarks>
        public void SetContext(VendorRuntime vendor, string npcId, string npcInter)
        {
            _vendor = vendor;
            _npcId = npcId ?? string.Empty;
            _npcInter = npcInter ?? string.Empty;
            Context = new PipSubTabContext(_npcId, _npcInter, null, false);

            // The shelf list is rebuilt from the new context, but the scroll position and the
            // sell-side selection are left alone: they belong to the page's own session, and this is a
            // context write from another page.
            if (_vendor != null && _source != null)
            {
                _vendor.MultPrice = _source.BarterMultiplier;
            }

            Rebuild();
        }

        /// <summary><c>PipBuck.onoff(-1)</c> clears the three context fields (<c>PipBuck.as:323-324</c>).</summary>
        public void Close()
        {
            _vendor = null;
            _npcId = string.Empty;
            _npcInter = string.Empty;
            Context = PipSubTabContext.Default;
            _sellBou.Clear();
            _message = null;
        }

        public void SetStatus(int subPage)
        {
            if (subPage > 0)
            {
                _subPage = PipSubTabs.Coerce(PageId, subPage, Context);
                _scroll = 0;
                _message = null;
            }

            Rebuild();
        }

        public void Step()
        {
            // The Oracle's PipPageVend does not override `step`; its content is static between
            // rebuilds. Deliberately silent — this runs 30 times a second.
        }

        /// <summary>Show or hide the page's root — pure visibility, and nothing else.</summary>
        /// <remarks>
        /// <para><b>This used to call <see cref="Close"/>, and that was the reported bug.</b>
        /// <c>PipBuck.setPage</c> hides every page before showing the target
        /// (<c>PipBuck.as:417-423</c>), so opening page 4 through <c>NPC.pip(4)</c> hid page 4
        /// <i>first</i> — wiping the vendor, the npcId and the npcInter that <see cref="Open"/> had
        /// just written. The page rebuilt with no context: "No trader.", an empty shelf and no Quests
        /// tab. Context now survives visibility changes and is dropped by
        /// <see cref="OnDeviceClosed"/>, which is what the Oracle's
        /// <c>pip.vendor = null; pip.npcId = null</c> (<c>PipBuck.as:323-324</c>) actually means.</para>
        /// </remarks>
        public void SetVisible(bool visible)
        {
            if (_root == null) return;

            if (_root.gameObject.activeSelf != visible)
            {
                _root.gameObject.SetActive(visible);
            }
        }

        /// <summary>
        /// The device closed — drop the trade context. The port of <c>pip.vendor = null;
        /// pip.npcId = null</c> (<c>PipBuck.as:323-324</c>), which runs in the closing arm of
        /// <c>onoff</c> and so must not fire on an ordinary page switch.
        /// </summary>
        /// <remarks>
        /// <para>Called by <c>PipBuckManager.OnClosed</c> for every registered page that is an
        /// <see cref="IPipContextPage"/>, so this runs whether or not page 4 was the page the player
        /// was looking at — a stale trader context from a camp visit that was interrupted by a page
        /// switch is cleared too.</para>
        /// </remarks>
        public void OnDeviceClosed()
        {
            Close();
            Rebuild();
        }

        public void Build(RectTransform pageRoot)
        {
            if (pageRoot == null)
            {
                Debug.LogError("[PipPageVendView] Build called with no page root. The trade page will " +
                               "not exist; page 4 will show an empty screen.");
                return;
            }

            if (_root != null) return;

            _root = CreateRect("PipVend Page", pageRoot);
            _root.gameObject.AddComponent<ScrollRelay>().Page = this;

            _subTabs.Build(_root, _font, _composition != null ? _composition.subCategoryButton : null);
            _subTabs.SubPageClicked = subPage => SubPageClicked?.Invoke(subPage);

            BuildHeader();
            BuildRows();
            BuildListScrollbar();
            BuildInfoPane();
            BuildEmptyText();
            BuildBottomText();
            BuildMessageText();
            BuildConfirmButton();

            Rebuild();
            SetVisible(false);
        }

        // ── Construction ──────────────────────────────────────────────────────

        private void BuildHeader()
        {
            RectTransform header = CreateRowRect("HeaderRow", PipBuckLayout.PageHeaderPosition());

            for (int column = 0; column < ColumnCount; column++)
            {
                _headerCells[column] = CreateCell(header, "Header" + column, column, PfeUiColors.PageYellow);
            }
        }

        private void BuildRows()
        {
            for (int row = 0; row < PipBuckLayout.PageRowCount; row++)
            {
                RectTransform rect = CreateRowRect("Row" + row, PipBuckLayout.PageRowPosition(row));
                _rowRects[row] = rect;

                // The selection tint. A port device (see the class summary): a fully transparent image
                // would be invisible, so the tint only shows for a row with a pending quantity.
                var backGo = new GameObject("sel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                var backRect = (RectTransform)backGo.transform;
                backRect.SetParent(rect, false);
                AnchorTopLeft(backRect);
                backRect.sizeDelta = new Vector2(560f, PipBuckLayout.PageRowPitch);
                backRect.anchoredPosition = Vector2.zero;
                _rowBacks[row] = backGo.GetComponent<Image>();
                _rowBacks[row].color = RowBackTint;
                _rowBacks[row].raycastTarget = false;

                var relay = rect.gameObject.AddComponent<RowRelay>();
                relay.Page = this;
                relay.RowIndex = row;

                for (int column = 0; column < ColumnCount; column++)
                {
                    _cells[row, column] = CreateCell(rect, "Col" + column, column, PfeUiColors.PageR0);
                }
            }
        }

        private RectTransform CreateRowRect(string name, Vector2 flash)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = RowHitSize;
            Place(rect, flash.x, flash.y);

            Image hit = go.GetComponent<Image>();
            hit.color = new Color(1f, 1f, 1f, 0f);
            hit.raycastTarget = true;

            return rect;
        }

        private Text CreateCell(RectTransform parent, string name, int column, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(ColumnWidth[column], RowTextHeight);
            rect.anchoredPosition = new Vector2(ColumnX[column], -RowTextY);

            Text text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = RowFontSize;
            text.color = colour;
            text.alignment = ColumnRight[column] ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            text.horizontalOverflow = column == 0 ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            text.text = string.Empty;
            return text;
        }

        private void BuildListScrollbar()
        {
            var go = new GameObject("scBar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _listScrollbar = (RectTransform)go.transform;
            _listScrollbar.SetParent(_root, false);
            AnchorTopLeft(_listScrollbar);
            _listScrollbar.sizeDelta = new Vector2(ListScrollbarWidth, ListScrollbarHeight);
            Place(_listScrollbar, ListScrollbarX, ListScrollbarY);

            Image track = go.GetComponent<Image>();
            track.sprite = _composition != null ? _composition.scrollBarTrack : null;
            track.type = Image.Type.Sliced;
            track.color = Color.white;
            track.raycastTarget = false;

            var thumbGo = new GameObject("thumb", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _listThumb = (RectTransform)thumbGo.transform;
            _listThumb.SetParent(_listScrollbar, false);
            _listThumb.anchorMin = _listThumb.anchorMax = new Vector2(0f, 1f);
            _listThumb.pivot = new Vector2(0.5f, 1f);
            _listThumb.sizeDelta = new Vector2(ListScrollbarWidth, ThumbMinHeight);
            _listThumb.anchoredPosition = Vector2.zero;

            Image thumb = thumbGo.GetComponent<Image>();
            thumb.sprite = _composition != null ? _composition.scrollBarThumb : null;
            thumb.type = Image.Type.Sliced;
            thumb.color = Color.white;
            thumb.raycastTarget = false;
        }

        private void BuildInfoPane()
        {
            _infoName = CreatePaneText("nazv", InfoNameX, InfoNameY, InfoNameWidth, InfoNameHeight,
                InfoNameFontSize, TextAnchor.UpperRight);

            _infoText = CreatePaneText("info", InfoTextX, InfoTextY, InfoTextWidth,
                InfoTextBottom - InfoTextY, InfoTextFontSize, TextAnchor.UpperLeft);
            _infoText.supportRichText = true;
        }

        private Text CreatePaneText(string name, float x, float y, float width, float height,
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
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            text.text = string.Empty;
            return text;
        }

        private void BuildEmptyText()
        {
            _emptyText = CreatePaneText("emptytext", EmptyTextX, EmptyTextY, EmptyTextWidth, 71f,
                14, TextAnchor.UpperLeft);
        }

        private void BuildBottomText()
        {
            _bottomText = CreatePaneText("bottext", BottomTextX, BottomTextY, BottomTextWidth, 23f,
                18, TextAnchor.UpperLeft);
            _bottomText.supportRichText = true;
        }

        private void BuildMessageText()
        {
            _messageText = CreatePaneText("infoText", BottomTextX, BottomTextY - 24f, 560f, 22f,
                16, TextAnchor.UpperLeft);
            _messageText.color = PfeUiColors.PageYellow;
        }

        private void BuildConfirmButton()
        {
            var go = new GameObject("butOk", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(Button));
            _confirmRect = (RectTransform)go.transform;
            _confirmRect.SetParent(_root, false);
            AnchorTopLeft(_confirmRect);
            _confirmRect.sizeDelta = new Vector2(PipInfoFrame.ButtonWidth, PipInfoFrame.ButtonHeight);
            Place(_confirmRect, PipInfoFrame.ConfirmButtonX, PipInfoFrame.ButtonY);

            _confirmBackground = go.GetComponent<Image>();
            Sprite art = _composition != null ? _composition.infoConfirmButton : null;
            _confirmBackground.sprite = art != null
                ? art
                : _composition != null ? _composition.subCategoryButton : null;
            _confirmBackground.color = art != null ? Color.white : new Color(0f, 0f, 0f, 0.01f);

            var labelGo = new GameObject("text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
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
            button.onClick.AddListener(OnAccept);

            go.SetActive(false);
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        public void Rebuild()
        {
            if (_root == null) return;

            RefreshRows();
            ClampScroll();

            RefreshSubTabs();
            RefreshHeader();
            RefreshRowCells();
            RefreshScrollbar();
            RefreshEmptyText();
            RefreshFooter();
            RefreshConfirmButton();
            RefreshInfoPane();
        }

        /// <summary>Rebuilds the typed row lists for the active sub-page.</summary>
        private void RefreshRows()
        {
            _buyRows.Clear();
            _sellRows.Clear();
            _repairRows.Clear();
            _questRows.Clear();

            if (_vendor == null || _source == null) return;

            switch ((VendorSubPage)_subPage)
            {
                case VendorSubPage.Buy: BuildBuyRows(); break;
                case VendorSubPage.Sell: BuildSellRows(); break;
                case VendorSubPage.Repair: BuildRepairRows(); break;
                case VendorSubPage.Quests: BuildQuestRows(); break;
            }
        }

        private void BuildBuyRows()
        {
            VendorPageContext ctx = _source.Context;

            for (int i = 0; i < _vendor.Buys.Count; i++)
            {
                VendorStock stock = _vendor.Buys[i];
                if (stock == null) continue;

                VendorItemFacts item = _source.Item(stock.Id);

                // The eleven guards, in the Oracle's order (PipPageVend.as:129-210). A row that fails
                // is absent from the list — this is a build-time filter, not a click-time refusal.
                if (!VendorTradeRules.AdmitBuyRow(stock, item, ctx, _source, out _)) continue;

                int price = VendorTradeRules.BuyUnitPrice(stock, item, _vendor.MultPrice,
                    out float multiplier);

                // :184-187 — `sost` becomes the player's own stack count for a consumable they hold.
                float have = stock.Sost;
                int owned = _source.PlayerStock(stock.Id);
                if (owned > 0 && stock.Tip != "weapon" && stock.Tip != "armor") have = owned;

                _buyRows.Add(new VendorBuyRow
                {
                    Rid = stock.Key,
                    Id = stock.Id,
                    Variant = stock.Variant,
                    Tip = stock.Tip,
                    Name = item.Exists ? item.Name : stock.Id,
                    Kol = stock.Kol,
                    Bou = stock.Bou,
                    Price = price,
                    Multiplier = multiplier,
                    Have = have,
                    SortName = CategoryName(stock.Tip),
                    Barter = stock.Barter,
                    Level = stock.Lvl
                });
            }

            VendorTradeRules.SortBuyRows(_buyRows);
        }

        private void BuildSellRows()
        {
            List<VendorSellRow> rows = _source.SellRows();
            for (int i = 0; i < rows.Count; i++)
            {
                VendorSellRow row = rows[i];
                row.Bou = _sellBou.TryGetValue(row.Rid, out int bou) ? bou : 0;
                _sellRows.Add(row);
            }
        }

        private void BuildRepairRows()
        {
            List<VendorRepairRow> rows = _source.RepairRows(_vendor.MultPrice, RepairArmorMultiplier());
            _repairRows.AddRange(rows);
        }

        private void BuildQuestRows()
        {
            if (_vendor.Xml?.tasks == null || string.IsNullOrEmpty(_npcId)) return;

            for (int i = 0; i < _vendor.Xml.tasks.Count; i++)
            {
                VendorTaskEntry task = _vendor.Xml.tasks[i];
                if (task == null || string.IsNullOrEmpty(task.taskId)) continue;

                int state = _source.QuestState(task.taskId);

                // PipPageVend.as:382-396 — a task gated on a skill the player lacks is not listed.
                if (!string.IsNullOrEmpty(task.skill) && task.skillLevel > 0 &&
                    _source.SkillLevel(task.skill) < task.skillLevel) continue;

                _questRows.Add(new VendorQuestRow
                {
                    Id = task.taskId,
                    Name = _source.QuestName(task.taskId) ?? task.taskId,
                    State = state,
                    CanActivate = state == 1 || state == 3 || state == 4
                });
            }
        }

        /// <summary>
        /// The repair page's armour multiplier — AS3 <c>pers.priceRepArmor</c> (<c>Pers.as:105</c>,
        /// default 0.5). The port has the field on <c>CharacterStats</c>; a page built without stats
        /// reads the Oracle's default.
        /// </summary>
        private float RepairArmorMultiplier()
        {
            return _source is PlayerVendorPageSource player && player.RepairArmorMultiplierProvider != null
                ? player.RepairArmorMultiplierProvider()
                : 0.5f;
        }

        private int SkillLevel(string skillId) => _source != null ? _source.SkillLevel(skillId) : 0;

        private string CategoryName(string tip)
        {
            if (string.IsNullOrEmpty(tip)) return string.Empty;
            string text = TextTable != null ? TextTable.Pip(tip) : null;
            return string.IsNullOrEmpty(text) || text.StartsWith("*", StringComparison.Ordinal) ? tip : text;
        }

        private void ClampScroll()
        {
            int maxScroll = Mathf.Max(0, RowCount() - PipBuckLayout.PageRowCount);
            if (_scroll > maxScroll) _scroll = maxScroll;
            if (_scroll < 0) _scroll = 0;
        }

        private int RowCount()
        {
            switch ((VendorSubPage)_subPage)
            {
                case VendorSubPage.Buy: return _buyRows.Count;
                case VendorSubPage.Sell: return _sellRows.Count;
                case VendorSubPage.Repair: return _repairRows.Count;
                case VendorSubPage.Quests: return _questRows.Count;
                default: return 0;
            }
        }

        private void RefreshSubTabs()
        {
            _subTabs.Refresh(PageId, _subPage, TextTable, Context,
                new PipSubTabSignals(0, 0, false, 0f, HasReportableQuest()));
        }

        /// <summary>
        /// <c>setSigns</c> (<c>PipPageVend.as:439-470</c>) — the "!" on the Quests sub-tab. It lights
        /// for a task that is new or claimable. The port can distinguish "new" (4) from "in progress"
        /// (1) but not "reportable" (3) yet — see <see cref="PlayerVendorPageSource.QuestState"/> — so
        /// an in-progress task does not light it.
        /// </summary>
        private bool HasReportableQuest()
        {
            for (int i = 0; i < _questRows.Count; i++)
            {
                if (_questRows[i].State == 3 || _questRows[i].State == 4) return true;
            }
            return false;
        }

        private void RefreshHeader()
        {
            string[] captions = HeaderCaptions();

            for (int column = 0; column < ColumnCount; column++)
            {
                if (_headerCells[column] == null) continue;
                _headerCells[column].text = RowCount() > 0 ? captions[column] : string.Empty;
            }
        }

        private string[] HeaderCaptions()
        {
            switch ((VendorSubPage)_subPage)
            {
                case VendorSubPage.Buy:
                    return new[] { Key("goods", "goods"), Key("condhave", "condition / you have"),
                                   Key("kol", "available"), Key("price", "price") };

                case VendorSubPage.Sell:
                    return new[] { Key("goods", "goods"), Key("youhave", "you have"), string.Empty,
                                   Key("price", "price") };

                case VendorSubPage.Repair:
                    return new[] { Key("goods", "goods"), Key("cond", "condition"), string.Empty,
                                   Key("repprice", "repair price") };

                default:
                    return new[] { Key("quests", "quests"), Key("state", "state"), string.Empty, string.Empty };
            }
        }

        private string Key(string id, string fallback)
        {
            string text = TextTable != null ? TextTable.Pip(id) : null;
            return string.IsNullOrEmpty(text) || text.StartsWith("*", StringComparison.Ordinal) ? fallback : text;
        }

        private void RefreshRowCells()
        {
            for (int row = 0; row < PipBuckLayout.PageRowCount; row++)
            {
                int index = _scroll + row;
                bool visible = index < RowCount();

                if (_rowRects[row] != null && _rowRects[row].gameObject.activeSelf != visible)
                {
                    _rowRects[row].gameObject.SetActive(visible);
                }

                if (!visible) continue;

                string[] values = RowValues(index);
                for (int column = 0; column < ColumnCount; column++)
                {
                    if (_cells[row, column] != null) _cells[row, column].text = values[column] ?? string.Empty;
                }

                if (_rowBacks[row] != null)
                {
                    bool selected = RowBou(index) > 0;
                    _rowBacks[row].color = selected ? RowBackTint : new Color(1f, 1f, 1f, 0f);
                }
            }
        }

        private string[] RowValues(int index)
        {
            switch ((VendorSubPage)_subPage)
            {
                case VendorSubPage.Buy:
                {
                    VendorBuyRow row = _buyRows[index];
                    return new[]
                    {
                        row.Name,
                        row.Have > 0f ? row.Have.ToString("0") : "-",
                        row.Kol.ToString(),
                        row.Price.ToString()
                    };
                }

                case VendorSubPage.Sell:
                {
                    VendorSellRow row = _sellRows[index];
                    return new[] { row.Name, row.Kol.ToString(), string.Empty, row.Price.ToString() };
                }

                case VendorSubPage.Repair:
                {
                    VendorRepairRow row = _repairRows[index];
                    float percent = row.MaxHp > 0f ? row.Hp / row.MaxHp * 100f : 0f;
                    return new[] { row.Name, percent.ToString("0") + "%", string.Empty, row.Price.ToString() };
                }

                default:
                {
                    VendorQuestRow row = _questRows[index];
                    return new[] { row.Name, QuestStateText(row.State), string.Empty, string.Empty };
                }
            }
        }

        private string QuestStateText(int state)
        {
            switch (state)
            {
                case 1: return Key("questactive", "in progress");
                case 2: return Key("questdone", "done");
                case 3: return Key("questready", "ready");
                case 4: return Key("questnew", "new");
                default: return string.Empty;
            }
        }

        private int RowBou(int index)
        {
            switch ((VendorSubPage)_subPage)
            {
                case VendorSubPage.Buy: return index < _buyRows.Count ? _buyRows[index].Bou : 0;
                case VendorSubPage.Sell: return index < _sellRows.Count ? _sellRows[index].Bou : 0;
                default: return 0;
            }
        }

        private void RefreshScrollbar()
        {
            if (_listScrollbar == null || _listThumb == null) return;

            int count = RowCount();
            bool needed = count > PipBuckLayout.PageRowCount;

            if (_listScrollbar.gameObject.activeSelf != needed)
            {
                _listScrollbar.gameObject.SetActive(needed);
            }

            if (!needed) return;

            int maxScroll = Mathf.Max(1, count - PipBuckLayout.PageRowCount);
            float fraction = (float)_scroll / maxScroll;

            float visible = Mathf.Min(1f, (float)PipBuckLayout.PageRowCount / count);
            float thumbHeight = Mathf.Max(ThumbMinHeight, ListScrollbarHeight * visible);
            float travel = ListScrollbarHeight - thumbHeight;

            _listThumb.sizeDelta = new Vector2(ListScrollbarWidth, thumbHeight);
            _listThumb.anchoredPosition = new Vector2(0f, -travel * fraction);
        }

        private void RefreshEmptyText()
        {
            if (_emptyText == null) return;

            if (RowCount() > 0 || _vendor == null)
            {
                _emptyText.text = _vendor == null ? Key("novendor", "No trader.") : string.Empty;
                return;
            }

            switch ((VendorSubPage)_subPage)
            {
                case VendorSubPage.Buy: _emptyText.text = Key("emptybuy", "Nothing for sale."); break;
                case VendorSubPage.Sell: _emptyText.text = Key("emptysell", "Nothing to sell."); break;
                case VendorSubPage.Repair: _emptyText.text = Key("emptyrep", "Nothing to repair."); break;
                default: _emptyText.text = Key("emptytask", "No quests."); break;
            }
        }

        private void RefreshFooter()
        {
            if (_bottomText == null) return;

            int caps = _source != null ? _source.Context.Caps : 0;
            int committed = _vendor != null ? VendorTradeRules.BuyTotal(_vendor.KolBou) : 0;
            int left = Mathf.Max(0, caps - committed);

            var sb = new StringBuilder();
            sb.Append(Key("caps", "Caps")).Append(": ").Append(caps);
            if (committed > 0) sb.Append(" (").Append(left).Append(' ').Append(Key("left", "left")).Append(')');

            if (_vendor != null && _subPage == (int)VendorSubPage.Sell)
            {
                sb.Append("  ·  ").Append(Key("sell", "Sell")).Append(": ")
                  .Append(VendorTradeRules.SellTotal(_vendor.KolSell));
            }

            _bottomText.text = sb.ToString();

            if (_messageText != null)
            {
                _messageText.text = _message ?? string.Empty;
            }
        }

        private void RefreshConfirmButton()
        {
            if (_confirmRect == null) return;

            // PipPageVend.as:372 — the repair sub-page hides `butOk`: repair is per-click. The Quests
            // page has no transaction either (`transOk`'s dispatch at :906-925 has no page2 == 4 arm),
            // so the button would be dead there — hidden rather than left to do nothing.
            bool visible = _vendor != null && _subPage != (int)VendorSubPage.Repair &&
                           _subPage != (int)VendorSubPage.Quests;

            if (_confirmRect.gameObject.activeSelf != visible)
            {
                _confirmRect.gameObject.SetActive(visible);
            }

            if (_confirmLabel != null) _confirmLabel.text = Key("transaction", "Accept the deal");
        }

        private void RefreshInfoPane()
        {
            if (_infoName == null || _infoText == null) return;

            if (_hoveredRow < 0 || _hoveredRow >= RowCount())
            {
                _infoName.text = string.Empty;
                _infoText.text = string.Empty;
                return;
            }

            var sb = new StringBuilder();

            switch ((VendorSubPage)_subPage)
            {
                case VendorSubPage.Buy:
                {
                    VendorBuyRow row = _buyRows[_hoveredRow];
                    _infoName.text = row.Name;
                    sb.Append(Key("price", "price")).Append(": ").Append(row.Price).Append('\n');
                    sb.Append(Key("kol", "available")).Append(": ").Append(row.Kol).Append('\n');
                    if (row.Barter > 0) sb.Append(Key("barter", "barter")).Append(": ").Append(row.Barter).Append('\n');
                    if (row.Level > 0) sb.Append(Key("lvl", "level")).Append(": ").Append(row.Level).Append('\n');
                    break;
                }

                case VendorSubPage.Sell:
                {
                    VendorSellRow row = _sellRows[_hoveredRow];
                    _infoName.text = row.Name;
                    sb.Append(Key("price", "price")).Append(": ").Append(row.Price).Append('\n');
                    sb.Append(Key("youhave", "you have")).Append(": ").Append(row.Kol).Append('\n');
                    break;
                }

                case VendorSubPage.Repair:
                {
                    VendorRepairRow row = _repairRows[_hoveredRow];
                    _infoName.text = row.Name;
                    sb.Append(Key("cond", "condition")).Append(": ")
                      .Append(row.Hp.ToString("0")).Append(" / ").Append(row.MaxHp.ToString("0")).Append('\n');
                    sb.Append(Key("repprice", "repair price")).Append(": ").Append(row.Price).Append('\n');
                    break;
                }

                default:
                {
                    VendorQuestRow row = _questRows[_hoveredRow];
                    _infoName.text = row.Name;
                    sb.Append(QuestStateText(row.State)).Append('\n');
                    break;
                }
            }

            _infoText.text = sb.ToString();
        }

        // ── Interaction ───────────────────────────────────────────────────────

        /// <summary>The port of <c>PipPageVend.itemClick</c> (<c>:777-810</c>).</summary>
        private void OnRowClicked(int rowIndex, bool rightClick, bool shift, bool ctrl)
        {
            int index = _scroll + rowIndex;
            if (index >= RowCount()) return;

            switch ((VendorSubPage)_subPage)
            {
                case VendorSubPage.Buy: ClickBuy(index, rightClick, shift, ctrl); break;
                case VendorSubPage.Sell: ClickSell(index, rightClick, shift, ctrl); break;
                case VendorSubPage.Repair: ClickRepair(index); break;
                case VendorSubPage.Quests: ClickQuest(index); break;
            }

            Rebuild();
        }

        private void ClickBuy(int index, bool rightClick, bool shift, bool ctrl)
        {
            if (index >= _buyRows.Count) return;

            VendorBuyRow row = _buyRows[index];
            VendorStock stock = FindStock(row.Rid);
            if (stock == null) return;

            if (rightClick)
            {
                // itemRightClick (:886-904) — remove one, or ten with shift.
                UnselectBuy(stock, row, shift ? 10 : 1);
                return;
            }

            if (ctrl)
            {
                // ctrl-click unselects the whole row (:795-810).
                UnselectBuy(stock, row, row.Bou);
                return;
            }

            int amount = shift ? row.Kol - row.Bou : 1;
            SelectBuy(stock, row, amount);
        }

        /// <summary>
        /// <c>selBuy</c> (<c>PipPageVend.as:670-725</c>) — clamp to the remaining stock, then clamp to
        /// what the caps still allow, then commit. A refusal is <b>not</b> silent: the Oracle shows
        /// <c>infoText("noMoney", shortfall)</c>.
        /// </summary>
        private void SelectBuy(VendorStock stock, VendorBuyRow row, int amount)
        {
            if (amount <= 0) return;

            int remaining = stock.Kol - stock.Bou;
            if (remaining <= 0) return;

            if (amount > remaining) amount = remaining;

            int caps = _source.Context.Caps;
            int committed = VendorTradeRules.BuyTotal(_vendor.KolBou);
            int affordable = VendorTradeRules.ClampToAffordable(amount, row.Price, caps - committed);

            if (affordable <= 0)
            {
                int shortfall = row.Price - Mathf.Max(0, caps - committed);
                _message = Key("noMoney", "Not enough caps") + " (" + Mathf.Max(0, shortfall) + ")";
                return;
            }

            if (affordable < amount)
            {
                _message = Key("noMoney", "Not enough caps") + " (" +
                           ((amount - affordable) * row.Price) + ")";
            }
            else
            {
                _message = null;
            }

            stock.Bou += affordable;
            _vendor.KolBou += row.Price * affordable;
        }

        private void UnselectBuy(VendorStock stock, VendorBuyRow row, int amount)
        {
            if (amount <= 0) return;

            int n = Mathf.Min(amount, stock.Bou);
            if (n <= 0) return;

            stock.Bou -= n;
            _vendor.KolBou -= row.Price * n;
            if (_vendor.KolBou < 0f) _vendor.KolBou = 0f;
            _message = null;
        }

        private void ClickSell(int index, bool rightClick, bool shift, bool ctrl)
        {
            if (index >= _sellRows.Count) return;

            VendorSellRow row = _sellRows[index];
            int current = _sellBou.TryGetValue(row.Rid, out int bou) ? bou : 0;

            if (rightClick || ctrl)
            {
                int remove = ctrl ? current : (shift ? 10 : 1);
                int n = Mathf.Min(remove, current);
                if (n <= 0) return;

                _sellBou[row.Rid] = current - n;
                _vendor.KolSell -= row.Price * n;
            }
            else
            {
                int add = shift ? row.Kol - current : 1;
                if (add <= 0) return;
                if (current + add > row.Kol) add = row.Kol - current;

                _sellBou[row.Rid] = current + add;
                _vendor.KolSell += row.Price * add;
            }

            if (_vendor.KolSell < 0f) _vendor.KolSell = 0f;
            _message = null;
        }

        /// <summary>
        /// The repair click (<c>PipPageVend.as:811-860</c>) — per click, no ledger and no Accept.
        /// The caps are clamped to what the player has, and the caps are converted back into HP by the
        /// row's own price (<c>:845-856</c>).
        /// </summary>
        private void ClickRepair(int index)
        {
            if (index >= _repairRows.Count) return;

            VendorRepairRow row = _repairRows[index];
            if (row.Price <= 0) return;

            int caps = _source.Context.Caps;
            int cost = Mathf.Min(row.Price, caps);
            if (cost <= 0)
            {
                _message = Key("noMoney", "Not enough caps");
                return;
            }

            float restore = row.MaxHp * ((float)cost / row.Price);
            if (!_source.RepairItem(row.Rid, restore))
            {
                _message = Key("norepair", "Cannot repair that");
                return;
            }

            if (!_source.SpendCaps(cost))
            {
                _message = Key("noMoney", "Not enough caps");
                return;
            }

            _vendor.Money += cost;
            _message = null;
            TransactionCommitted?.Invoke();
        }

        private void ClickQuest(int index)
        {
            if (index >= _questRows.Count) return;

            VendorQuestRow row = _questRows[index];
            if (!row.CanActivate) return;

            _source.ActivateQuest(row.Id, row.State);
            _message = null;
            TransactionCommitted?.Invoke();
        }

        private VendorStock FindStock(string rid)
        {
            if (_vendor == null) return null;
            return _vendor.Buys2.TryGetValue(rid, out VendorStock stock) ? stock : null;
        }

        private void OnHover(int rowIndex)
        {
            int index = _scroll + rowIndex;
            _hoveredRow = index < RowCount() ? index : -1;
            RefreshInfoPane();
        }

        public void ScrollRows(int delta)
        {
            if (delta == 0) return;

            int maxScroll = Mathf.Max(0, RowCount() - PipBuckLayout.PageRowCount);
            int next = Mathf.Clamp(_scroll + delta, 0, maxScroll);
            if (next == _scroll) return;

            _scroll = next;
            Rebuild();
        }

        // ── The transaction ───────────────────────────────────────────────────

        /// <summary>
        /// <c>transOk</c> (<c>PipPageVend.as:906-925</c>) — dispatch on the sub-page, then redraw and
        /// sound. The sound is the device's (<c>pip.snd(3)</c>), so it is raised as
        /// <see cref="TransactionCommitted"/>.
        /// </summary>
        private void OnAccept()
        {
            if (_vendor == null) return;

            if (_subPage == (int)VendorSubPage.Buy) Buy();
            else if (_subPage == (int)VendorSubPage.Sell) Sell();

            Rebuild();
        }

        /// <summary>
        /// <c>trade()</c> (<c>PipPageVend.as:927-955</c>): the caps are debited by
        /// <c>ceil(kolBou)</c>, the vendor credited by the same, and every selected row's <c>bou</c>
        /// lands in the bag.
        /// </summary>
        private void Buy()
        {
            int total = VendorTradeRules.BuyTotal(_vendor.KolBou);
            if (total <= 0) return;

            if (_vendor.KolBou > _source.Context.Caps)
            {
                _message = Key("noMoney", "Not enough caps");
                return;
            }

            if (!_source.SpendCaps(total))
            {
                _message = Key("noMoney", "Not enough caps");
                return;
            }

            for (int i = 0; i < _vendor.Buys.Count; i++)
            {
                VendorStock stock = _vendor.Buys[i];
                if (stock == null || stock.Bou <= 0) continue;

                if (!_source.AddPurchased(stock.Id, stock.Bou))
                {
                    Debug.LogWarning($"[PipPageVendView] Paid for '{stock.Key}' x{stock.Bou} but the " +
                                     "row could not be added to the bag. The caps were already debited " +
                                     "(the Oracle debits first too) — this is a data gap: no item, " +
                                     "weapon or armour definition answers that id.");
                }

                stock.Kol -= stock.Bou;
                stock.Bou = 0;
            }

            _vendor.Money += total;
            _vendor.KolBou = 0f;
            _message = null;

            TransactionCommitted?.Invoke();
        }

        /// <summary>
        /// <c>sell()</c> (<c>PipPageVend.as:957-995</c>): refused when the vendor cannot pay and the
        /// player is not in a base; the caps credit is <c>floor(kolSell)</c> while the vendor pays
        /// <c>ceil(kolSell)</c> — the fraction is the vendor's.
        /// </summary>
        private void Sell()
        {
            if (_vendor.KolSell <= 0f) return;

            VendorPageContext ctx = _source.Context;
            if (!VendorTradeRules.CanPayForSale(ctx, _vendor.Money, _vendor.KolSell))
            {
                _message = Key("noSell", "The trader cannot pay that much");
                return;
            }

            int credit = VendorTradeRules.SellTotal(_vendor.KolSell);
            int debit = (int)Math.Ceiling(_vendor.KolSell);

            for (int i = 0; i < _sellRows.Count; i++)
            {
                VendorSellRow row = _sellRows[i];
                if (row.Bou <= 0) continue;

                if (!_source.RemoveSold(row.Id, row.Bou))
                {
                    Debug.LogWarning($"[PipPageVendView] Sold '{row.Rid}' x{row.Bou} but the bag refused " +
                                     "the removal; the row is left in the list.");
                    continue;
                }

                AddToVendorShelf(row, row.Bou);
            }

            _source.ReceiveCaps(credit);
            _vendor.Money -= debit;
            if (_vendor.Money < 0) _vendor.Money = 0;

            _vendor.KolSell = 0f;
            _sellBou.Clear();
            _message = null;

            TransactionCommitted?.Invoke();
        }

        /// <summary>
        /// <c>vend.buys2[key].kol += bou</c>, creating the row when the vendor did not stock it
        /// (<c>PipPageVend.as:975-985</c>).
        /// </summary>
        private void AddToVendorShelf(VendorSellRow row, int count)
        {
            if (_vendor.Buys2.TryGetValue(row.Rid, out VendorStock existing))
            {
                existing.Kol += count;
                return;
            }

            CampaignDataParser.SplitVariant(row.Rid, out string baseId, out int variant);

            var stock = new VendorStock
            {
                Id = baseId,
                Variant = variant,
                Tip = row.Tip,
                Kol = count,
                Sost = 1f
            };

            _vendor.Buys.Add(stock);
            _vendor.Buys2[stock.Key] = stock;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

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

        private static void AnchorTopLeft(RectTransform rect)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
        }

        private static void Place(RectTransform rect, float flashX, float flashY)
        {
            rect.anchoredPosition = new Vector2(flashX, -flashY);
        }

        // ── Event relays ──────────────────────────────────────────────────────

        private sealed class RowRelay : MonoBehaviour, IPointerEnterHandler, IPointerClickHandler
        {
            public PipPageVendView Page;
            public int RowIndex;

            public void OnPointerEnter(PointerEventData eventData) => Page?.OnHover(RowIndex);

            public void OnPointerClick(PointerEventData eventData)
            {
                bool right = eventData != null && eventData.button == PointerEventData.InputButton.Right;
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

                Page?.OnRowClicked(RowIndex, right, shift, ctrl);
            }
        }

        private sealed class ScrollRelay : MonoBehaviour, IScrollHandler
        {
            public PipPageVendView Page;

            public void OnScroll(PointerEventData eventData)
            {
                Page?.ScrollRows(eventData.scrollDelta.y > 0f ? -1 : 1);
            }
        }
    }
}
