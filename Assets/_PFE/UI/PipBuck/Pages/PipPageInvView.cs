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
    /// The inventory page — <b>page 2</b> of the device, the port of <c>fe.inter.PipPageInv</c>.
    ///
    /// <para><b>Its five sub-category tabs are the Oracle's <c>page2</c>.</b> Weapons, Armor, Aid,
    /// Misc and Ammo are the <i>inner</i> tabs of the inventory page — the ones behind the device's
    /// own five page tabs, which is the "PipBuck on Tab with its own tabs" this work item exists to
    /// port. Sub-page <b>1</b> is Weapons, not the character sheet: see
    /// <see cref="PipInventoryList"/> for the mapping and why an off-by-one here would look like a
    /// data problem rather than a layout one.</para>
    ///
    /// <para><b>Why this is a plain class and not a <c>MonoBehaviour</c>.</b> The page's state
    /// (which sub-page, which scroll offset, which rows) is worth testing, and a test that needs a
    /// <c>RectTransform</c> cannot run in this project — the editor is owner-only. So the class is
    /// constructed with <c>new</c>, owns the GameObjects it creates, and receives its per-tick hook
    /// through <see cref="Step"/> rather than through <c>Update</c>. The only components it adds are
    /// two small relays that forward Unity's event-system callbacks back into it, because those
    /// callbacks can only arrive at a component.</para>
    ///
    /// <para><b>The row art is missing, and the rows say so.</b> The Oracle's row clip is
    /// <c>visPipInvItem</c> (set at <c>PipPageInv.as:30</c>), and it was <b>not</b> imported into the
    /// composition asset — <c>PipBuckCompositionDefinition</c> carries the bezel, the tabs, the
    /// monitor row and the sub-category button, and no page row. So the rows are drawn as text at the
    /// Oracle's own coordinates with no background sprite, rather than with a stand-in that would
    /// make the page look finished when its art is not there. The column <i>offsets</i> inside a row
    /// are the port's for the same reason: they lived inside the missing symbol.</para>
    /// </summary>
    public sealed class PipPageInvView : IPipPage
    {
        /// <summary>The Oracle's page index for the inventory — <c>pages[2]</c> (<c>PipBuck.as:111</c>).</summary>
        public const int InventoryPageId = 2;

        /// <summary>The Oracle's six row columns: fav, nazv, hp, ammo, mass, ammotip.</summary>
        private const int ColumnCount = 6;

        /// <summary>
        /// Whether the header row draws readable captions or the Oracle's raw string keys. See
        /// <see cref="PipInventoryList.HeaderFor"/> — the readable set is the port's prose, the key
        /// set is the Oracle's own un-translated table, and neither is a lie as long as the choice is
        /// visible.
        /// </summary>
        public static bool ShowHeaderLabels = true;

        // ── Column geometry ───────────────────────────────────────────────────
        //
        // PORT CHOICE. The Oracle positions these inside the row clip `visPipInvItem`, which was not
        // imported, so there is no attested offset to read. What IS attested is the row's own origin
        // (PipPage.as:112-113) and the row pitch, and those are used verbatim. The widths are sized
        // to the longest string each column can hold: a name, a condition percentage, a round count.

        private static readonly float[] ColumnX = { 0f, 55f, 330f, 420f, 510f, 610f };
        private static readonly float[] ColumnWidth = { 50f, 270f, 85f, 85f, 95f, 270f };

        /// <summary>Wide enough for the six columns; the device's page area is about 935 px.</summary>
        private const float RowWidth = 900f;

        // ── Sub-category tab geometry ─────────────────────────────────────────
        //
        // PORT CHOICE, same reason: the Oracle's sub-tabs are `but1..but5` inside `visPipInv`, which
        // was not imported. They are placed on one row above the header, which is where the Oracle's
        // own sub-tab strip sits relative to its list.
        //
        // The numbers now live in PipSubTabBar, which is the row every other page draws too. This
        // page keeps its own copies of the *names* so the layout code below reads as it always has,
        // but they are aliases — a second literal here would let the inventory's row drift away from
        // the nine other pages' rows, and the drift would look like a per-page styling choice.

        private const float SubTabY = PipSubTabBar.TabY;
        private const float SubTabX0 = PipSubTabBar.TabX0;
        private const float SubTabPitch = PipSubTabBar.TabPitch;
        private static readonly Vector2 SubTabSize = PipSubTabBar.TabSize;

        private const float EmptyTextX = 330f;
        private const float EmptyTextY = 300f;
        private const float BottomTextY = 660f;

        // ── Wiring ────────────────────────────────────────────────────────────

        private readonly PipBuckViewModel _viewModel;
        private readonly IPipInventorySource _source;

        private PipBuckCompositionDefinition _composition;
        private Font _font;

        /// <summary>The bottom strip's inputs, or null for a page with no inventory behind it.</summary>
        public Func<PipInventoryFooter> FooterProvider { get; set; }

        /// <summary>
        /// The Oracle's localisation document — the table <c>Res.pipText</c> resolves against
        /// (<c>Res.as:144-147</c>). Supplies the sub-tab captions (<c>inv1..inv5</c>,
        /// <c>PipPage.as:148</c>), the six column headings (<c>ii1..ii8</c>,
        /// <c>PipPageInv.as:68-73</c> / <c>:180-185</c> / <c>:218-226</c>) and the empty-list notice
        /// (<c>emptyinv</c>, <c>:310</c>).
        ///
        /// <para>Null is legal and means "no document": every caption then falls back to the port's
        /// own prose. Set by <c>PipBuckBootstrapper</c> from
        /// <c>Resources/Localisation/text_en.xml</c>; a page built without it still draws, it just
        /// draws the pre-localisation words.</para>
        /// </summary>
        public PipTextTable TextTable { get; set; }

        /// <summary>
        /// What a row activation does. The Oracle's <c>itemClick</c> equips a weapon
        /// (<c>PipPageInv.as:545-551</c>), equips armour (<c>:552-559</c>) or uses an item
        /// (<c>:596</c>), and its <c>itemRightClick</c> queues a drop (<c>:636-661</c>).
        ///
        /// <para><b>The port has no path for any of those yet.</b> <c>IInventoryCommandSink</c> carries
        /// <c>AddItem</c>, <c>RemoveItem</c>, <c>AddArmor</c>, <c>ConsumeAmmo</c> and <c>DropItem</c>
        /// (<c>InventoryCommand.cs:16-48</c>) — no "equip", no "use". Rather than call into
        /// <c>GameInventory</c> directly and bypass the command seam the whole system exists to
        /// establish, the page hands the row to this callback. Returning <c>true</c> means "handled";
        /// returning <c>false</c> — or leaving the handler unset — falls through to the report below,
        /// so a click always says what the Oracle would have done rather than doing nothing.</para>
        /// </summary>
        public Func<PipInventoryRow, bool> RowActivated { get; set; }

        // ── Built objects ─────────────────────────────────────────────────────

        private RectTransform _root;
        private readonly RectTransform[] _rowRects = new RectTransform[PipBuckLayout.PageRowCount];
        private readonly Image[] _rowBackgrounds = new Image[PipBuckLayout.PageRowCount];

        /// <summary>Flat: cell <c>row * ColumnCount + column</c>. See <see cref="Cell"/>.</summary>
        private readonly Text[] _cells = new Text[PipBuckLayout.PageRowCount * ColumnCount];

        private Text[] _headerCells;
        private readonly Text[] _subTabLabels = new Text[PipInventoryList.SubPageCount];
        private readonly Image[] _subTabBackgrounds = new Image[PipInventoryList.SubPageCount];
        private Text _emptyText;
        private Text _bottomText;

        // ── State ─────────────────────────────────────────────────────────────

        private int _subPage = PipInventoryList.WeaponsSubPage;
        private int _scroll;

        /// <summary>
        /// Ids whose "new" marker has been dismissed by hovering, so <see cref="Step"/> does not
        /// re-draw it. The Oracle writes <c>nov = 0</c> back into the inventory instead
        /// (<c>PipPageInv.as:731</c>); the port cannot, because <c>nov</c> is not a field of any
        /// command kind. Held here rather than dropped so the visible behaviour matches.
        /// </summary>
        private readonly HashSet<string> _dismissedNewMarkers = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The Oracle's <c>over_t</c> (<c>PipPageInv.as:23</c>) — a hover dwell countdown.</summary>
        private int _overTicks;
        private string _hoveredId;

        /// <summary>
        /// How long a hover must last before the marker clears. The Oracle sets <c>over_t = 30</c> on
        /// hover (<c>PipPageInv.as:527</c>) and clears at 1 (<c>:723</c>), at 30 Hz — so 29 ticks,
        /// just under a second.
        /// </summary>
        private const int HoverDwellTicks = 30;

        public PipPageInvView(PipBuckViewModel viewModel, IPipInventorySource source)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _source = source;
        }

        // ── IPipPage ──────────────────────────────────────────────────────────

        public int PageId => InventoryPageId;

        /// <summary>True: this page has content. The shell's "not implemented" notice stays hidden.</summary>
        public bool IsImplemented => true;

        /// <summary>The sub-page currently listed — the Oracle's <c>page2</c>, 1..5.</summary>
        public int SubPage => _subPage;

        /// <summary>The scroll offset, in rows.</summary>
        public int ScrollOffset => _scroll;

        /// <summary>The font the page draws with, once <see cref="Configure"/> has run.</summary>
        public Font Font => _font;

        /// <summary>
        /// Supplies the composition (for the sub-tab sprite) and the font. Called by
        /// <c>PipBuckBootstrapper</c> before <see cref="Build"/>.
        ///
        /// <para><b>Why this is not a constructor argument.</b> <see cref="IPipPage"/>'s seam is
        /// <c>Build(RectTransform)</c> and it is shared by every page, including the stubs that need
        /// neither. Widening the seam to carry a font would make every page's signature claim a
        /// dependency most of them do not have. The two calls are ordered by the one caller.</para>
        /// </summary>
        public void Configure(PipBuckCompositionDefinition composition, Font font)
        {
            _composition = composition;
            _font = font;
        }

        /// <summary>
        /// The port of <c>PipPage.setStatus</c> (<c>PipPage.as:917-956</c>).
        ///
        /// <para><b>The argument's meaning is the Oracle's, and it is not "the sub-page".</b>
        /// <c>setStatus(param1)</c> treats <paramref name="subPage"/> as a truthiness flag: a non-zero
        /// value resets the scroll to the top (<c>PipPage.as:927-930</c>). <c>PipBuck.setPage</c>
        /// passes its own <c>param2</c> straight through, and only when that is <c>&gt; 0</c> does it
        /// also assign <c>page2</c> (<c>PipBuck.as:427-430</c>). So <b>0 means "leave both the
        /// sub-page and the scroll exactly where they were"</b> — which is what a plain <c>onoff(2)</c>
        /// relies on when the player re-opens the inventory.</para>
        /// </summary>
        public void SetStatus(int subPage)
        {
            if (subPage > 0)
            {
                _subPage = Mathf.Clamp(subPage, 1, PipInventoryList.SubPageCount);
                _scroll = 0;
            }

            Rebuild();
        }

        /// <summary>Shows or hides the page's root. The Oracle's <c>vis.visible</c>.</summary>
        public void SetVisible(bool visible)
        {
            if (_root == null) return;
            if (_root.gameObject.activeSelf != visible)
            {
                _root.gameObject.SetActive(visible);
            }
        }

        /// <summary>
        /// One 30 Hz interface tick — the port of <c>PipPageInv.step</c>
        /// (<c>PipPageInv.as:717-737</c>).
        ///
        /// <para>The Oracle's whole body is the hover countdown: hovering a row arms <c>over_t = 30</c>
        /// (<c>:527</c>), each tick decrements it (<c>:719-722</c>), and at 1 it clears the row's
        /// "new" marker and writes <c>nov = 0</c> back into the inventory (<c>:723-736</c>). The
        /// marker half is ported; the write-back is not, and cannot be — <c>nov</c> is not a field of
        /// any <c>InventoryCommandKind</c>, and reaching into <c>GameInventory</c> from a view would
        /// bypass the seam that exists to keep mutations authoritative.</para>
        /// </summary>
        public void Step()
        {
            if (_overTicks > 0)
            {
                _overTicks--;
            }

            if (_overTicks != 1 || _hoveredId == null) return;

            if (_dismissedNewMarkers.Add(_hoveredId))
            {
                Rebuild();
            }
        }

        /// <summary>
        /// Creates the page's GameObjects under <paramref name="pageRoot"/>.
        ///
        /// <para><b>The page root is already at the Oracle's <c>vis</c> origin.</b>
        /// <c>PipBuckView</c> places it at <c>PipElement.PageRoot</c>, which is
        /// <c>PipPage.as:89-90</c>'s <c>vis.x = 165; vis.y = 72</c>. So every coordinate in this file
        /// is relative to that, exactly as the Oracle's are relative to <c>vis</c>.</para>
        /// </summary>
        public void Build(RectTransform pageRoot)
        {
            if (pageRoot == null)
            {
                Debug.LogError("[PipPageInvView] Build called with no page root. The inventory page " +
                               "will not exist; the device will show an empty screen for page 2.");
                return;
            }

            // Idempotent: the shell may be rebuilt (a canvas resize re-runs ApplyResize, and a future
            // rebuild path may re-Build pages). A second Build would otherwise stack a second copy of
            // every row on top of the first, which looks like doubled text rather than an error.
            if (_root != null) return;

            _root = CreateRect("PipInv Page", pageRoot);

            // The scroll relay goes on the page root, not on each row. Unity's event system walks UP
            // the hierarchy from the raycast hit until it finds a handler, so one relay on the parent
            // serves every row — and also the gaps between them, which is where a player's wheel
            // actually lands when the list is short.
            _root.gameObject.AddComponent<ScrollRelay>().Page = this;

            BuildSubTabs();
            BuildHeader();
            BuildRows();
            BuildEmptyText();
            BuildBottomText();

            Rebuild();
        }

        // ── Construction ──────────────────────────────────────────────────────

        private void BuildSubTabs()
        {
            for (int i = 0; i < PipInventoryList.SubPageCount; i++)
            {
                int subPage = i + 1;

                var go = new GameObject("SubTab" + subPage, typeof(RectTransform), typeof(CanvasRenderer),
                    typeof(Image), typeof(Button));
                var rect = (RectTransform)go.transform;
                rect.SetParent(_root, false);
                AnchorTopLeft(rect);
                rect.sizeDelta = SubTabSize;
                Place(rect, SubTabX0 + i * SubTabPitch, SubTabY);

                Image background = go.GetComponent<Image>();
                background.sprite = _composition != null ? _composition.subCategoryButton : null;
                background.color = Color.white;
                _subTabBackgrounds[i] = background;

                var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                var labelRect = (RectTransform)labelGo.transform;
                labelRect.SetParent(rect, false);
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;

                Text label = labelGo.GetComponent<Text>();
                label.font = _font;
                label.fontSize = 15;
                label.color = PfeUiColors.PageR0;
                label.alignment = TextAnchor.MiddleCenter;
                label.raycastTarget = false;
                // Left empty here and written by RefreshSubTabs, which runs on every Rebuild. Setting
                // it only at build time would leave the caption frozen if TextTable arrived after
                // Build — the "drew it once and nothing ever corrected it" failure this device has
                // already been bitten by once (see PipBuckView.RefreshShell).
                label.text = string.Empty;
                _subTabLabels[i] = label;

                // Capture `subPage`, not `i`: a closure over the loop variable would make every tab
                // open the last one. (Same trap PipBuckView.BuildTabs documents.)
                var button = go.GetComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() => OnSubTabClicked(subPage));
            }
        }

        private void BuildHeader()
        {
            Vector2 flash = PipBuckLayout.PageHeaderPosition();
            _headerCells = new Text[ColumnCount];

            for (int column = 0; column < ColumnCount; column++)
            {
                _headerCells[column] = CreateCell(_root, "Header" + column, flash, column, 15,
                    PfeUiColors.PageYellow);
            }
        }

        private void BuildRows()
        {
            for (int row = 0; row < PipBuckLayout.PageRowCount; row++)
            {
                Vector2 flash = PipBuckLayout.PageRowPosition(row);

                var go = new GameObject("Row" + row, typeof(RectTransform), typeof(CanvasRenderer),
                    typeof(Image));
                var rect = (RectTransform)go.transform;
                rect.SetParent(_root, false);
                AnchorTopLeft(rect);
                rect.sizeDelta = new Vector2(RowWidth, PipBuckLayout.PageRowPitch);
                Place(rect, flash.x, flash.y);

                // The row's own hit area. A fully transparent Image still receives raycasts in Unity
                // UI (the alpha test only engages above alphaHitTestMinimumThreshold), so this is what
                // makes the row clickable and hoverable without drawing anything — the Oracle's row
                // background lives in the symbol that was not imported.
                Image background = go.GetComponent<Image>();
                background.color = new Color(1f, 1f, 1f, 0f);
                _rowBackgrounds[row] = background;

                var relay = go.AddComponent<RowRelay>();
                relay.Page = this;
                relay.RowIndex = row;

                for (int column = 0; column < ColumnCount; column++)
                {
                    _cells[row * ColumnCount + column] = CreateCell(rect, "Col" + column, flash, column,
                        14, PfeUiColors.PageR0);
                }

                _rowRects[row] = rect;
            }
        }

        private Text CreateCell(RectTransform parent, string name, Vector2 rowOrigin, int column,
            int fontSize, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(ColumnWidth[column], PipBuckLayout.PageRowPitch);
            rect.anchoredPosition = new Vector2(ColumnX[column], 0f);

            Text text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.color = colour;
            text.alignment = TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            text.text = string.Empty;
            return text;
        }

        private void BuildEmptyText()
        {
            var go = new GameObject("Empty", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(400f, 40f);
            Place(rect, EmptyTextX, EmptyTextY);

            _emptyText = go.GetComponent<Text>();
            _emptyText.font = _font;
            _emptyText.fontSize = 18;
            _emptyText.color = PfeUiColors.PageOrange;
            _emptyText.alignment = TextAnchor.MiddleLeft;
            _emptyText.raycastTarget = false;
            _emptyText.text = string.Empty;
        }

        private void BuildBottomText()
        {
            var go = new GameObject("bottext", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(RowWidth, 30f);
            Place(rect, PipBuckLayout.PageRowX, BottomTextY);

            _bottomText = go.GetComponent<Text>();
            _bottomText.font = _font;
            _bottomText.fontSize = 15;
            _bottomText.color = PfeUiColors.PageR0;
            _bottomText.alignment = TextAnchor.MiddleLeft;
            _bottomText.supportRichText = true;   // the caps total arrives as a <color> tag
            _bottomText.raycastTarget = false;
            _bottomText.text = string.Empty;
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        /// <summary>
        /// Re-reads the source and repaints everything — the port of <c>setSubPages</c> plus the row
        /// loop in <c>setStatItems</c>.
        /// </summary>
        public void Rebuild()
        {
            if (_root == null) return;

            bool showHidden = _viewModel != null && _viewModel.ShowHidden.Value;

            IReadOnlyList<PipInventoryEntry> entries = _source?.Read();
            List<PipInventoryRow> rows = PipInventoryList.RowsFor(entries, _subPage, showHidden);

            // The Oracle clamps implicitly by the scrollbar's own min/max (PipPage.as:944-949); the
            // port clamps here because a rebuild can shorten the list under a scrolled page — drop an
            // item while scrolled to the bottom and an unclamped offset would show blank rows.
            int maxScroll = PipInventoryList.MaxScroll(rows.Count, PipBuckLayout.PageRowCount);
            if (_scroll > maxScroll) _scroll = maxScroll;
            if (_scroll < 0) _scroll = 0;

            RefreshSubTabs();
            RefreshHeader(rows.Count > 0);
            RefreshRows(rows);
            RefreshEmptyText(rows.Count == 0);
            RefreshBottomText();
        }

        private void RefreshSubTabs()
        {
            for (int i = 0; i < PipInventoryList.SubPageCount; i++)
            {
                if (_subTabLabels[i] == null) continue;

                int subPage = i + 1;
                bool active = subPage == _subPage;

                // PipPage.as:148 — `_loc3_.text.text = Res.pipText(this.pp + _loc4_)`, i.e.
                // `inv1`..`inv5`. Re-resolved every rebuild, so a TextTable supplied after Build still
                // lands on screen.
                _subTabLabels[i].text = PipInventoryList.SubPageName(subPage, TextTable);

                // The Oracle lights the current sub-tab by `fon.gotoAndStop(2)` on its background
                // (PipPage.as:1600-1609). With one frame imported, the lit state is a tint — and the
                // tint is what the tab bar does too, so the two read the same.
                if (_subTabBackgrounds[i] != null)
                {
                    _subTabBackgrounds[i].color = active
                        ? PfeUiColors.PageYellow
                        : Color.white;
                }

                _subTabLabels[i].color = active ? PfeUiColors.PageDark : PfeUiColors.PageR0;
            }
        }

        private void RefreshHeader(bool listHasRows)
        {
            if (_headerCells == null) return;

            // PipPageInv.as:308-317 — with nothing to list the Oracle hides the whole header
            // (`statHead.visible = false`) and shows the empty notice instead. A header over an empty
            // list reads as a broken list.
            for (int column = 0; column < ColumnCount; column++)
            {
                if (_headerCells[column] == null) continue;
                _headerCells[column].text = listHasRows ? HeaderCaption(column) : string.Empty;
            }
        }

        /// <summary>
        /// The header's caption for a column — the Oracle's <c>Res.pipText("ii…")</c>, or the port's
        /// readable word. See <see cref="ShowHeaderLabels"/>.
        /// </summary>
        private string HeaderCaption(int column)
        {
            if (ShowHeaderLabels)
            {
                return PipInventoryList.HeaderFor(_subPage, TextTable).ToArray()[column];
            }

            // The Oracle's own keys, drawn as-is: `ii1`..`ii8`. This is the raw-key mode, kept because
            // it is the only caption that cannot be wrong — it is the key itself.
            return PipInventoryList.HeaderKeysFor(_subPage)[column];
        }

        private void RefreshRows(List<PipInventoryRow> rows)
        {
            for (int row = 0; row < PipBuckLayout.PageRowCount; row++)
            {
                int entryIndex = PipInventoryList.EntryIndexForRow(
                    row, rows.Count, _scroll, PipBuckLayout.PageRowCount);

                bool visible = entryIndex >= 0;

                // PipPage.as:990-1002 — a row past the end of the list is hidden, not left showing the
                // previous page's contents.
                if (_rowRects[row] != null && _rowRects[row].gameObject.activeSelf != visible)
                {
                    _rowRects[row].gameObject.SetActive(visible);
                }

                if (!visible) continue;

                PipInventoryRow entry = rows[entryIndex];

                SetCell(row, 0, entry.Fav);
                SetCell(row, 1, entry.Name);
                SetCell(row, 2, entry.Value);
                SetCell(row, 3, entry.Extra);
                SetCell(row, 4, entry.Mass);
                SetCell(row, 5, entry.ExtraTip);

                // PipPageInv.as:415-418 — an unavailable weapon's name is dimmed rather than the row
                // hidden: the player still needs to see that they own it.
                if (Cell(row, 1) != null)
                {
                    Cell(row, 1).color = entry.Available ? PfeUiColors.PageR0 : PfeUiColors.PageDark;
                }

                // The Oracle's `ramka` selection frame. With no row sprite, the frame is a tint on the
                // row's own (transparent) background.
                if (_rowBackgrounds[row] != null)
                {
                    _rowBackgrounds[row].color = entry.Equipped
                        ? new Color(PfeUiColors.PageGreen.r, PfeUiColors.PageGreen.g, PfeUiColors.PageGreen.b, 0.22f)
                        : new Color(1f, 1f, 1f, 0f);
                }
            }
        }

        private void RefreshEmptyText(bool isEmpty)
        {
            if (_emptyText == null) return;

            // PipPageInv.as:310 — `vis.emptytext.text = Res.pipText("emptyinv")`. The table gives
            // "No items in this category". With no document loaded the caption degrades to the
            // Oracle's own missing-key marker (`*pip_emptyinv`), which says exactly what is wrong
            // rather than pretending the list is empty for a reason it cannot name.
            _emptyText.text = isEmpty ? EmptyText() : string.Empty;
        }

        /// <summary>The empty-list notice — <c>Res.pipText("emptyinv")</c> (<c>PipPageInv.as:310</c>).</summary>
        private string EmptyText()
        {
            if (TextTable != null && TextTable.TryGet("p", "emptyinv", 0, out string localised))
            {
                return localised;
            }

            // No document: the port's own word, as it was before the table existed.
            return "EMPTY";
        }

        private void RefreshBottomText()
        {
            if (_bottomText == null) return;

            PipInventoryFooter footer = FooterProvider != null
                ? FooterProvider()
                : default;

            _bottomText.text = PipInventoryList.BottomText(footer, _subPage);
        }

        private Text Cell(int row, int column) => _cells[row * ColumnCount + column];

        private void SetCell(int row, int column, string value)
        {
            Text cell = Cell(row, column);
            if (cell != null) cell.text = value ?? string.Empty;
        }

        // ── Interaction ───────────────────────────────────────────────────────

        /// <summary>The port of <c>PipPage.page2Click</c> (<c>PipPage.as:883-892</c>).</summary>
        public void OnSubTabClicked(int subPage)
        {
            if (subPage < 1 || subPage > PipInventoryList.SubPageCount) return;
            if (subPage == _subPage) return;

            _subPage = subPage;

            // PipPageInv.setSubPages always resets the scroll on a sub-page change (it calls
            // setStatus with a truthy argument), and so does this: keeping the offset would land the
            // player mid-list on a page with different contents.
            _scroll = 0;

            Rebuild();
        }

        /// <summary>The port of the row's <c>MouseEvent.MOUSE_OVER</c> handler, <c>statInfo</c>.</summary>
        internal void OnRowHover(int rowIndex)
        {
            PipInventoryRow? row = RowAt(rowIndex);
            if (row == null) return;

            string id = row.Value.Id;
            if (id == _hoveredId) return;

            _hoveredId = id;

            // PipPageInv.as:523-528 — the dwell only re-arms for a *different* id, which is why the
            // early return above is load-bearing: without it, a pointer resting on a row would reset
            // the countdown every frame and the marker would never clear.
            _overTicks = HoverDwellTicks;
        }

        /// <summary>The port of the row's click handler, <c>itemClick</c> (<c>PipPageInv.as:532-617</c>).</summary>
        internal void OnRowClicked(int rowIndex)
        {
            // PipPageInv.as:534-538 — every action is refused while `pip.noAct` is set (a rat-race, a
            // cutscene). The Oracle answers with `gui.infoText("noAct")`; the port has no info strip
            // on this page, so the refusal is logged.
            if (_viewModel != null && _viewModel.NoAct.Value)
            {
                Debug.Log("[PipPageInvView] Item actions are refused: the Oracle's `noAct` is set " +
                          "(PipPageInv.as:534-538).");
                return;
            }

            PipInventoryRow? row = RowAt(rowIndex);
            if (row == null) return;

            // The handler claims the click when it acted. A handler that returns false — or no handler
            // at all — lands on the report below, which is the same message either way, so the
            // Oracle's verb table stays in exactly one place.
            if (RowActivated != null && RowActivated(row.Value))
            {
                return;
            }

            // No handler: say what the Oracle would have done, rather than nothing at all. See
            // RowActivated for why the port cannot do it yet.
            string verb;
            switch (row.Value.Kind)
            {
                case PipInventoryKind.Weapon: verb = "equip weapon (PipPageInv.as:545-551)"; break;
                case PipInventoryKind.Armor: verb = "equip armour (PipPageInv.as:552-559)"; break;
                default: verb = "use item (PipPageInv.as:596)"; break;
            }

            Debug.Log($"[PipPageInvView] '{row.Value.Id}' — the Oracle would {verb}, but the port has " +
                      "no inventory command for it. Set RowActivated to supply one.");
        }

        /// <summary>
        /// Scrolls the list. Positive moves the window down the list.
        ///
        /// <para>The Oracle's own mapping is inverted from Unity's: <c>onMouseWheel1</c> increments
        /// the scroll position when the wheel delta is <b>negative</b> (<c>PipPage.as:1670-1677</c>),
        /// and Flash reports a wheel-down as negative while Unity reports it as negative
        /// <c>scrollDelta.y</c> too — so the sign survives, but the meaning of the parameter here is
        /// stated in rows rather than in deltas to keep the two apart.</para>
        /// </summary>
        public void ScrollRows(int delta)
        {
            if (delta == 0) return;

            int maxScroll = PipInventoryList.MaxScroll(CurrentRowCount(), PipBuckLayout.PageRowCount);
            int next = Mathf.Clamp(_scroll + delta, 0, maxScroll);
            if (next == _scroll) return;

            _scroll = next;
            Rebuild();
        }

        private int CurrentRowCount()
        {
            bool showHidden = _viewModel != null && _viewModel.ShowHidden.Value;
            return PipInventoryList.RowsFor(_source?.Read(), _subPage, showHidden).Count;
        }

        /// <summary>The row shown at a row-clip index, or null when that clip is empty.</summary>
        private PipInventoryRow? RowAt(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= PipBuckLayout.PageRowCount) return null;
            if (_rowRects[rowIndex] == null || !_rowRects[rowIndex].gameObject.activeSelf) return null;

            bool showHidden = _viewModel != null && _viewModel.ShowHidden.Value;
            List<PipInventoryRow> rows = PipInventoryList.RowsFor(_source?.Read(), _subPage, showHidden);

            int entryIndex = PipInventoryList.EntryIndexForRow(
                rowIndex, rows.Count, _scroll, PipBuckLayout.PageRowCount);
            if (entryIndex < 0) return null;

            return rows[entryIndex];
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

        /// <summary>
        /// Top-left anchoring, because every coordinate in this file is a Flash coordinate: the
        /// Oracle measures from the page clip's top-left corner with +y pointing <b>down</b>.
        /// </summary>
        private static void AnchorTopLeft(RectTransform rect)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
        }

        /// <summary>
        /// Places a child at a Flash coordinate. The single y-negation for this file — see
        /// <c>PipAnchor.ToUnityAnchoredPosition</c>, which is the same flip the shell uses, and the
        /// I4 note about what happens when two owners disagree about a sign.
        /// </summary>
        private static void Place(RectTransform rect, float flashX, float flashY)
        {
            rect.anchoredPosition = new Vector2(flashX, -flashY);
        }

        // ── Event relays ──────────────────────────────────────────────────────
        //
        // Unity delivers pointer and scroll callbacks to a COMPONENT, and this page is a plain class,
        // so these two thin components exist to hand the event straight back. They hold no state and
        // decide nothing — every rule stays in the page, which is the half that can be tested.

        private sealed class RowRelay : MonoBehaviour, IPointerEnterHandler, IPointerClickHandler
        {
            public PipPageInvView Page;
            public int RowIndex;

            public void OnPointerEnter(PointerEventData eventData) => Page?.OnRowHover(RowIndex);

            public void OnPointerClick(PointerEventData eventData)
            {
                // The Oracle routes a ctrl-click to the right-click handler (PipPageInv.as:539-543).
                // The port has no right-click action to route to yet, so both go to the same hook —
                // and OnRowClicked's log names the Oracle's own right-click behaviour.
                Page?.OnRowClicked(RowIndex);
            }
        }

        private sealed class ScrollRelay : MonoBehaviour, IScrollHandler
        {
            public PipPageInvView Page;

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
