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
    /// The character sheet — <b>page 1</b> of the device, the port of <c>fe.inter.PipPageStat</c>.
    ///
    /// <para><b>Its five sub-tabs are the Oracle's <c>page2</c> 1..5.</b> Main / Skills / Perks /
    /// Effects / Health are the page's own tabs — the row behind the device's own five page tabs. A
    /// sixth sub-page, the level-up perk picker, has no button: the Oracle reaches it from Perks
    /// (<c>PipPageStat.as:1007-1012</c>) and returns from it through the Cancel button. It is
    /// reachable through <see cref="OpenPerkPicker"/> and left through <see cref="ClosePerkPicker"/>.</para>
    ///
    /// <para><b>Why this is a plain class and not a <c>MonoBehaviour</c>.</b> The page's state — which
    /// sub-page, the scroll offset, the hovered row, which rows a snapshot produces — is worth testing,
    /// and a test that needs a <c>RectTransform</c> cannot run in this project. So the page is built
    /// with <c>new</c>, owns the GameObjects it creates, and takes its per-tick hook through
    /// <see cref="Step"/>. The only components it adds are two small relays that forward Unity's
    /// event-system callbacks back into it, because those callbacks can only arrive at a component.</para>
    ///
    /// <para><b>The row art is missing, and the rows say so by their shape.</b> The Oracle's row clip is
    /// <c>visPipStatItem</c> (<c>PipPageStat.as:33</c>) and it was not imported into the composition —
    /// the same gap <c>PipPageInvView</c> records for <c>visPipInvItem</c>. So a row is two text
    /// columns at coordinates the port chose, and the column offsets are tagged
    /// <c>PORT CHOICE</c> below. What <i>is</i> attested and used verbatim is the row origin and pitch
    /// (<c>PipPage.as:112-113</c>, through <see cref="PipBuckLayout.PageRowPosition"/>), the row count,
    /// and every string on the page.</para>
    ///
    /// <para><b>Two visual elements are stand-ins, and each says which.</b> The Oracle's skill rows
    /// carry a twenty-pip bar (<c>progress.p1</c>..<c>p20</c>, <c>PipPageStat.as:527-544</c>) and its
    /// health rows a proportional <c>hpbar</c> (<c>:555-559</c>); neither symbol was imported. The port
    /// draws both as one continuous fill bar, which carries the same fraction. See
    /// <see cref="BarWidth"/>.</para>
    /// </summary>
    public sealed class PipPageStatView : IPipPage
    {
        /// <summary>The Oracle's page index for the character sheet — <c>pages[1]</c> (<c>PipBuck.as:111</c>).</summary>
        public const int StatPageId = 1;

        // ── Column geometry ───────────────────────────────────────────────────
        //
        // PORT CHOICE. The Oracle positions these inside the row clip `visPipStatItem`, which was not
        // imported, so there is no attested offset to read. What IS attested is the *value column's*
        // per-sub-page x, which the Oracle assigns on the clip itself (PipPageStat.as:499, :509, :522,
        // :545) — those three numbers ARE the Oracle's and are used as given. The name column's x and
        // both widths are the port's, sized to the longest string each holds.

        /// <summary>The name column's x, inside the row.</summary>
        private const float NameX = 0f;

        /// <summary>The name column's width — the longest param label, with room for its "-  " prefix.</summary>
        private const float NameWidth = 330f;

        /// <summary>The value column's x when the sub-page does not move it — <c>PipPageStat.as:499</c>.</summary>
        private const float ValueXDefault = 335f;

        /// <summary>The value column's x on the Effects sub-page — <c>PipPageStat.as:509</c>.</summary>
        private const float ValueXEffects = 500f;

        /// <summary>The value column's x on a <b>post-game</b> skill row — <c>PipPageStat.as:522</c>.</summary>
        private const float ValueXPostSkill = 215f;

        /// <summary>The value column's x on a regular skill row — <c>PipPageStat.as:545</c>.</summary>
        private const float ValueXSkill = 525f;

        private const float ValueWidth = 260f;

        /// <summary>
        /// The bar's width, under the value column.
        ///
        /// <para><b>A stand-in for two different un-imported symbols.</b> A skill row's bar is the
        /// Oracle's twenty-pip <c>progress</c> clip and a health row's is <c>hpbar</c>; the port draws
        /// both as one continuous fill of the same fraction. The difference a player would notice is
        /// that the pips are discrete; the fraction, which is what the pips encode, is identical.</para>
        /// </summary>
        private const float BarWidth = 150f;
        private const float BarHeight = 5f;
        private const float BarY = 24f;

        private const float EmptyTextX = 330f;
        private const float EmptyTextY = 300f;
        private const float BottomTextY = 660f;

        /// <summary>Wide enough for the two columns; the device's page area is about 935 px.</summary>
        private const float RowWidth = 900f;

        // ── Wiring ────────────────────────────────────────────────────────────

        private readonly IPipStatSource _source;
        private readonly PipSubTabBar _subTabs = new PipSubTabBar();

        private PipBuckCompositionDefinition _composition;
        private Font _font;

        /// <summary>
        /// The Oracle's localisation document — the table <c>Res.pipText</c> resolves against
        /// (<c>Res.as:144-147</c>). Every caption on this page is a lookup in it: the sub-tab names
        /// (<c>stat1</c>..<c>stat5</c>), the param labels (<c>alldamage</c>, <c>reload</c>, …), the
        /// header keys (<c>is1</c>..<c>is5</c>), the skill and perk names (the <c>e</c> table) and the
        /// three empty-list notices.
        ///
        /// <para>Null is legal and means "no document": every caption then degrades to the Oracle's own
        /// missing-key marker (<c>*p_name</c>), which says exactly what is wrong rather than inventing a
        /// word. Set by <c>PipBuckBootstrapper</c> from <c>Resources/Localisation/text_en.xml</c>.</para>
        /// </summary>
        public PipTextTable TextTable { get; set; }

        /// <summary>
        /// Raised when a sub-tab is pressed, so the <i>device</i> can run <c>PipPage.page2Click</c> —
        /// assign <c>page2</c>, call the page's <c>setStatus</c> and play the click sound. The handler
        /// is the device's because on two pages a sub-tab opens a different page entirely; see
        /// <see cref="PipBuckManager.SubPageClick"/>.
        /// </summary>
        public Action<int> SubPageClicked { get; set; }

        /// <summary>
        /// What a row activation does. The Oracle's <c>itemClick</c> spends a skill point
        /// (<c>PipPageStat.as:872-885</c>), uses medicine (<c>:895-957</c>) or selects a perk
        /// (<c>:886-894</c>).
        ///
        /// <para><b>The port has no command for any of those yet</b> — the same gap the inventory page
        /// records. Rather than reach into the RPG system from a view, the page hands the row to this
        /// callback; returning <c>false</c>, or leaving it unset, falls through to a report of what the
        /// Oracle would have done.</para>
        /// </summary>
        public Func<PipStatRow, bool> RowActivated { get; set; }

        /// <summary>
        /// Raised with the perk id after the picker successfully spends a point on it — the port's
        /// counterpart to the Oracle's <c>pip.setRPanel(); World.w.saveGame();</c> at the end of the
        /// accept branch (<c>PipPageStat.as:1019-1021</c>). A device hangs its panel refresh and its
        /// save off this.
        /// </summary>
        public Action<string> PerkCommitted { get; set; }

        /// <summary>Raised when a row is hovered, so the Health strip can name that row's medicine.</summary>
        public Action<PipStatRow> RowHovered { get; set; }

        // ── Built objects ─────────────────────────────────────────────────────

        private RectTransform _root;

        /// <summary>
        /// The list scrollbar — the Oracle's <c>scBar</c>. Shared chrome with the inventory page via
        /// <see cref="PipRowChrome.Scrollbar"/>: the character sheet had a working mouse wheel and no
        /// visible bar, so a player could scroll a list with no indication that more existed.
        /// </summary>
        private readonly PipRowChrome.Scrollbar _scrollbar = new PipRowChrome.Scrollbar();

        private readonly RectTransform[] _rowRects = new RectTransform[PipStatList.VisibleRows];
        private readonly Text[] _nameCells = new Text[PipStatList.VisibleRows];
        private readonly Text[] _valueCells = new Text[PipStatList.VisibleRows];
        private readonly Image[] _rowBars = new Image[PipStatList.VisibleRows];

        private Text _headerName;
        private Text _headerValue;
        private Text _emptyText;
        private Text _bottomText;

        // ── State ─────────────────────────────────────────────────────────────

        private int _subPage = PipStatList.MainSubPage;
        private int _scroll;
        private string _hoveredId = string.Empty;

        /// <summary>
        /// The perk the player has picked on the level-up page — the Oracle's <c>this.selectedPerk</c>
        /// (<c>PipPageStat.as:890</c>). Empty means nothing is picked, which is the state the page opens
        /// in and the state its bottom strip reads as "choose a perk".
        /// </summary>
        private string _selectedPerk = string.Empty;

        /// <summary>The rows currently drawn. Kept so hover and click can map a row clip back to a row.</summary>
        private readonly List<PipStatRow> _rows = new List<PipStatRow>();

        /// <summary>The snapshot the current rows were built from — what the bottom strip reads.</summary>
        private PipStatSnapshot _snapshot;

        public PipPageStatView(IPipStatSource source)
        {
            _source = source;
        }

        // ── IPipPage ──────────────────────────────────────────────────────────

        public int PageId => StatPageId;

        /// <summary>True: this page has content. The shell's "not implemented" notice stays hidden.</summary>
        public bool IsImplemented => true;

        /// <summary>The sub-page currently listed — the Oracle's <c>page2</c>, 1..6.</summary>
        public int SubPage => _subPage;

        /// <summary>The perk picked on the level-up page, or empty. See <see cref="_selectedPerk"/>.</summary>
        public string SelectedPerk => _selectedPerk;

        /// <summary>The scroll offset, in rows.</summary>
        public int ScrollOffset => _scroll;

        /// <summary>The sub-tab row, for the device to hang its click handler off. See <see cref="SubPageClicked"/>.</summary>
        public PipSubTabBar SubTabs => _subTabs;

        /// <summary>The font the page draws with, once <see cref="Configure"/> has run.</summary>
        public Font Font => _font;

        /// <summary>
        /// Supplies the composition (for the sub-tab button sprite) and the font. Called by
        /// <c>PipBuckBootstrapper</c> before <see cref="Build"/>. Same arrangement — and the same
        /// reason — as <c>PipPageInvView.Configure</c>.
        /// </summary>
        public void Configure(PipBuckCompositionDefinition composition, Font font)
        {
            _composition = composition;
            _font = font;
        }

        /// <summary>
        /// The port of <c>PipPage.setStatus</c>'s sub-page half.
        ///
        /// <para><b>The argument's meaning is the Oracle's.</b> <c>PipBuck.setPage</c> assigns
        /// <c>page2</c> only when its own argument is <c>&gt; 0</c> (<c>PipBuck.as:427-430</c>), so
        /// <b>0 means "leave the sub-page and the scroll exactly where they were"</b> — which is what a
        /// plain <c>onoff(1)</c> relies on when the player re-opens the character sheet. A non-zero
        /// value also resets the scroll, because the new sub-page has different contents
        /// (<c>PipPage.as:927-930</c>).</para>
        /// </summary>
        public void SetStatus(int subPage)
        {
            if (subPage > 0)
            {
                _subPage = Mathf.Clamp(subPage, PipStatList.MainSubPage, PipStatList.PerkPickSubPage);
                _scroll = 0;
                _hoveredId = string.Empty;

                // A pick made on the level-up page means nothing on any other sub-page, and the Oracle
                // clears it on the way in (transOk, :1007-1012) — so it is cleared on any move rather
                // than left to leak into a later visit.
                _selectedPerk = string.Empty;
            }

            Rebuild();
        }

        /// <summary>Shows or hides the page's root. The Oracle's <c>vis.visible</c>.</summary>
        public void SetVisible(bool visible)
        {
            if (_root == null) return;
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        /// <summary>
        /// One 30 Hz interface tick — the port of <c>PipPageStat.step</c>.
        ///
        /// <para><b>The Oracle's page does not override <c>step</c>.</b> <c>PipPage.step</c> advances the
        /// list's own hover/scroll animation, and this page's content is static between rebuilds, so
        /// there is nothing to advance. Deliberately silent — <c>Step</c> runs 30 times a second, and a
        /// log line here would be 1800 lines a minute.</para>
        /// </summary>
        public void Step()
        {
        }

        /// <summary>
        /// Creates the page's GameObjects under <paramref name="pageRoot"/>.
        ///
        /// <para><b>The page root is already at the Oracle's <c>vis</c> origin.</b>
        /// <c>PipBuckView</c> places it at <c>PipElement.PageRoot</c>, which is <c>PipPage.as:89-90</c>'s
        /// <c>vis.x = 165; vis.y = 72</c>. Every coordinate here is relative to that, exactly as the
        /// Oracle's are relative to <c>vis</c>.</para>
        /// </summary>
        public void Build(RectTransform pageRoot)
        {
            if (pageRoot == null)
            {
                Debug.LogError("[PipPageStatView] Build called with no page root. The character sheet " +
                               "will not exist; the device will show an empty screen for page 1.");
                return;
            }

            // Idempotent: a canvas resize re-runs the shell's layout, and a second Build would stack a
            // second copy of every row on top of the first — which reads as doubled text, not an error.
            if (_root != null) return;

            _root = CreateRect("PipStat Page", pageRoot);

            // One scroll relay on the page root, not one per row: Unity's event system walks UP from
            // the raycast hit, so a relay on the parent serves every row and the gaps between them.
            _root.gameObject.AddComponent<ScrollRelay>().Page = this;

            _subTabs.Build(_root, _font, _composition != null ? _composition.subCategoryButton : null);

            // **The one writer of the bar's handler, and it forwards to the page's own event.**
            //
            // The device claims the click by setting this page's `SubPageClicked`, not the bar's — see
            // the property, and PipBuckBootstrapper for why. The lambda reads the property at *invoke*
            // time rather than capturing its value here, so a handler set after Build still takes
            // effect. That matters: Build runs last in PipBuckBootstrapper.RegisterPages.
            //
            // Before this, the bootstrapper wrote the bar's slot directly and Build then overwrote it
            // with a forward to a property nobody ever set — so every sub-tab button was built, laid out,
            // lit, and wired to a null action. A play-test reported exactly that: "skills, perks,
            // effects, health is not clickable".
            _subTabs.SubPageClicked = subPage => SubPageClicked?.Invoke(subPage);

            BuildHeader();
            BuildRows();
            _scrollbar.Build(_root, _composition);
            BuildEmptyText();
            BuildBottomText();

            Rebuild();

            // PipPage.as:92 — `this.vis.visible = false;` in the Oracle's page constructor. Every page
            // starts hidden and only PipBuckManager.SetPage reveals one. Nothing in the boot order
            // currently opens the device without going through SetPage, so this is a guard rather than
            // a fix — but a page that built itself visible would draw over the shell's backdrop from
            // the moment the scene loaded, and that reads as "the device is stuck open".
            SetVisible(false);
        }

        // ── Construction ──────────────────────────────────────────────────────

        private void BuildHeader()
        {
            // The header is a ROW, one pitch above row 0 — PipBuckLayout.PageHeaderPosition. It is built
            // with the same helper BuildRows uses, and that is the point: parented straight to the page
            // root it landed on the root's top-left corner, because CreateCell writes y = 0 relative to
            // its parent, and the sub-tab strip sits at PipSubTabBar.TabY = 10 below that same corner. The
            // two rows were drawn on top of each other, which a play-test reported as "key, name, in one
            // level with tabs".
            RectTransform header = CreateRowRect("HeaderRow", PipBuckLayout.PageHeaderPosition(), false);

            _headerName = CreateCell(header, "HeaderName", NameX, NameWidth, 15,
                PfeUiColors.PageYellow);
            _headerValue = CreateCell(header, "HeaderValue", ValueXDefault, ValueWidth, 15,
                PfeUiColors.PageYellow);
        }

        /// <summary>
        /// One of the page's row rects — a list row <b>and</b> the header row, which is a row like any
        /// other. Shared by <see cref="BuildRows"/> and <see cref="BuildHeader"/> so the two cannot drift
        /// apart again: the version of this code that lived only inside <see cref="BuildRows"/> is what
        /// let the header be built somewhere else entirely.
        /// </summary>
        /// <param name="withHitArea">True for a list row, which needs a transparent raycast target so it
        /// can be clicked and hovered; false for the header, which is a caption and must not swallow the
        /// click meant for the row under it.</param>
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
            for (int row = 0; row < PipStatList.VisibleRows; row++)
            {
                RectTransform rect = CreateRowRect("Row" + row, PipBuckLayout.PageRowPosition(row), true);
                _rowRects[row] = rect;

                // The Oracle's row `back`, behind everything else in the row. The character sheet's own
                // clip is `visPipStatItem`, which was never imported, so the imported inventory row art
                // (498) stands in — the same substitution the Info page already makes for `visPipQuestItem`
                // (999). Without it the rows were transparent, which is why Status looked like a wall of
                // floating text next to the inventory's banded list.
                //
                // The width is the art's own 560, not this page's 900-px row rect: the Oracle's band ends
                // just before the scrollbar, which sits at x 600 in page-root coordinates, so a
                // full-width band would run under the bar. 560 is also exactly the span the inventory
                // page's identical art occupies.
                PipRowChrome.AddRowBack(rect, _composition != null ? _composition.inventoryRowBack : null,
                    PipRowChrome.RowBackWidth);

                // A fully transparent Image still receives raycasts in Unity UI, so this is what makes
                // the row hoverable and clickable without drawing anything of its own.
                Image background = rect.GetComponent<Image>();
                background.color = new Color(1f, 1f, 1f, 0f);

                var relay = rect.gameObject.AddComponent<RowRelay>();
                relay.Page = this;
                relay.RowIndex = row;

                _nameCells[row] = CreateCell(rect, "Name", NameX, NameWidth, 14, PfeUiColors.PageR0);
                _valueCells[row] = CreateCell(rect, "Value", ValueXDefault, ValueWidth, 14,
                    PfeUiColors.PageR0);

                _rowBars[row] = CreateBar(rect, row);
            }
        }

        private Image CreateBar(RectTransform row, int index)
        {
            var go = new GameObject("Bar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(row, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(BarWidth, BarHeight);
            rect.anchoredPosition = new Vector2(ValueXDefault, -BarY);

            Image image = go.GetComponent<Image>();
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillOrigin = (int)Image.OriginHorizontal.Left;
            image.color = PfeUiColors.PageGreen;
            image.raycastTarget = false;
            image.enabled = false;
            return image;
        }

        private Text CreateCell(RectTransform parent, string name, float x, float width,
            int fontSize, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(width, PipBuckLayout.PageRowPitch);

            // y is 0 relative to `parent`, and `parent` is always a positioned row rect (see
            // CreateRowRect). There used to be a `rowOrigin` parameter here that was passed a Flash
            // position and never read — which is how the header ended up on the page root's corner with
            // the sub-tabs. A parameter that is never read cannot be kept honest, so it is gone rather
            // than documented.
            rect.anchoredPosition = new Vector2(x, 0f);

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
            rect.sizeDelta = new Vector2(600f, 80f);
            Place(rect, EmptyTextX, EmptyTextY);

            _emptyText = go.GetComponent<Text>();
            _emptyText.font = _font;
            _emptyText.fontSize = 18;
            _emptyText.color = PfeUiColors.PageOrange;
            _emptyText.alignment = TextAnchor.UpperLeft;
            _emptyText.horizontalOverflow = HorizontalWrapMode.Wrap;
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
            _bottomText.supportRichText = true;   // the counts arrive as <color> tags
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

            _snapshot = _source?.Read() ?? new PipStatSnapshot();

            _rows.Clear();
            _rows.AddRange(PipStatList.RowsFor(_snapshot, _subPage, TextTable));

            // The Oracle clamps implicitly through the scrollbar's own min/max (PipPage.as:944-949);
            // the port clamps here because a rebuild can shorten the list under a scrolled page — lose
            // a perk and an unclamped offset would show blank rows.
            int maxScroll = PipStatList.MaxScroll(_rows.Count);
            if (_scroll > maxScroll) _scroll = maxScroll;
            if (_scroll < 0) _scroll = 0;

            string empty = PipStatList.EmptyTextFor(_subPage, _snapshot, TextTable);
            bool hasList = empty == null && _rows.Count > 0;

            RefreshSubTabs();
            RefreshHeader(hasList);
            RefreshRows(hasList);
            _scrollbar.Refresh(_rows.Count, PipStatList.VisibleRows, _scroll, maxScroll);
            RefreshEmptyText(empty);
            RefreshBottomText();
        }

        private void RefreshSubTabs()
        {
            // The sign markers are the Oracle's setSigns override (PipPageStat.as:453-471): a marker on
            // Skills when a point is unspent, on Perks when a perk point is, and a two- or three-level
            // one on Health as the player's limbs degrade. The values come from the same snapshot the
            // rows do, so the row and its marker can never disagree.
            var signals = new PipSubTabSignals(
                _snapshot.SkillPoints,
                _snapshot.PerkPoints,
                HasVitals(_snapshot),
                LowestVitalFraction(_snapshot),
                false);

            _subTabs.Refresh(PageId, _subPage, TextTable, PipSubTabContext.Default, signals);
        }

        private static bool HasVitals(PipStatSnapshot s) =>
            s?.Vitals != null && s.Vitals.InMaxHp > 0f;

        private static float LowestVitalFraction(PipStatSnapshot s)
        {
            PipStatVitals v = s?.Vitals;
            if (v == null || v.InMaxHp <= 0f) return 1f;

            float lowest = v.HeadHp;
            if (v.TorsoHp < lowest) lowest = v.TorsoHp;
            if (v.LegsHp < lowest) lowest = v.LegsHp;
            if (v.BloodHp < lowest) lowest = v.BloodHp;
            return lowest / v.InMaxHp;
        }

        private void RefreshHeader(bool hasList)
        {
            (string name, string value) = PipStatList.HeaderFor(_subPage, TextTable);

            // PipPageStat.as:60-63 — the header row is hidden when the page has nothing to list.
            bool visible = hasList && (name.Length > 0 || value.Length > 0);
            if (_headerName != null)
            {
                _headerName.text = visible ? name : string.Empty;
                _headerName.gameObject.SetActive(visible);
            }

            if (_headerValue != null)
            {
                _headerValue.text = visible ? value : string.Empty;
                _headerValue.gameObject.SetActive(visible);
            }
        }

        private void RefreshRows(bool hasList)
        {
            for (int row = 0; row < PipStatList.VisibleRows; row++)
            {
                int entryIndex = hasList
                    ? PipStatList.EntryIndexForRow(row, _rows.Count, _scroll)
                    : -1;

                bool visible = entryIndex >= 0;
                if (_rowRects[row] != null && _rowRects[row].gameObject.activeSelf != visible)
                {
                    _rowRects[row].gameObject.SetActive(visible);
                }

                if (!visible) continue;

                PipStatRow entry = _rows[entryIndex];

                if (_nameCells[row] != null)
                {
                    _nameCells[row].text = entry.Name;
                    _nameCells[row].color = new Color(PfeUiColors.PageR0.r, PfeUiColors.PageR0.g,
                        PfeUiColors.PageR0.b, entry.Alpha);
                }

                if (_valueCells[row] != null)
                {
                    _valueCells[row].text = entry.Value;
                    _valueCells[row].color = new Color(PfeUiColors.PageR0.r, PfeUiColors.PageR0.g,
                        PfeUiColors.PageR0.b, entry.Alpha);

                    // PipPageStat.as:499/509/522/545 — the Oracle moves the value column per sub-page,
                    // and on the Skills page per *row* (a post-game skill's value carries a longer
                    // string, so it starts further left). The numbers are the Oracle's own.
                    ((RectTransform)_valueCells[row].transform).anchoredPosition =
                        new Vector2(ValueColumnX(entry), 0f);
                }

                RefreshBar(row, entry);
            }
        }

        /// <summary>Where a row's value column sits — the Oracle's per-sub-page (and per-row) x.</summary>
        private float ValueColumnX(PipStatRow entry)
        {
            if (_subPage == PipStatList.EffectsSubPage) return ValueXEffects;

            if (_subPage == PipStatList.SkillsSubPage)
            {
                // `param2.post > 0` moves the value to 215, a regular skill's to 525.
                return IsPostSkillRow(entry.Id) ? ValueXPostSkill : ValueXSkill;
            }

            return ValueXDefault;
        }

        /// <summary>
        /// Whether a Skills-page row belongs to a post-game skill. The row id <i>is</i> the skill id
        /// (<c>PipPageStat.as:312</c>), and <c>Pers.skillIsPost</c> is a fixed three-name list — so the
        /// page can answer this without the row carrying a flag.
        /// </summary>
        private static bool IsPostSkillRow(string id)
        {
            return id == "attack" || id == "defense" || id == "knowl";
        }

        private void RefreshBar(int row, PipStatRow entry)
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
            ((RectTransform)bar.transform).anchoredPosition = new Vector2(ValueColumnX(entry), -BarY);
        }

        private void RefreshEmptyText(string empty)
        {
            if (_emptyText == null) return;
            _emptyText.text = empty ?? string.Empty;
        }

        private void RefreshBottomText()
        {
            if (_bottomText == null) return;

            // The Health strip names the medicine of the row the pointer is over, so the hovered id
            // has to reach the builder — it is the Oracle's `infoItemId`, and it is a field of the
            // snapshot rather than a parameter for the same reason the rest of the state is. The
            // picker's selection travels the same way: both are page state the source cannot know.
            _snapshot.HoveredRowId = _hoveredId;
            _snapshot.SelectedPerk = _selectedPerk;
            _bottomText.text = PipStatList.BottomText(_snapshot, _subPage, TextTable);
        }

        // ── Interaction ───────────────────────────────────────────────────────

        /// <summary>
        /// Enters the level-up perk picker — the Oracle's <c>transOk</c> on the Perks sub-page
        /// (<c>PipPageStat.as:1007-1012</c>), which sets <c>page2 = 6</c>, plays sound 2 and clears the
        /// selection. <b>Not a sub-tab click</b>: there is no button for sub-page 6, and it must not go
        /// through <c>SubPageClick</c>, which would play the wrong sound and re-run the sub-tab rules.
        /// </summary>
        public void OpenPerkPicker()
        {
            _subPage = PipStatList.PerkPickSubPage;
            _scroll = 0;
            _hoveredId = string.Empty;
            _selectedPerk = string.Empty;
            Rebuild();
        }

        /// <summary>
        /// Leaves the perk picker — the Oracle's <c>gotoDef</c> (<c>PipPageStat.as:1027-1035</c>):
        /// back to Perks, sound 2.
        /// </summary>
        public void ClosePerkPicker()
        {
            if (_subPage != PipStatList.PerkPickSubPage) return;
            _subPage = PipStatList.PerksSubPage;
            _scroll = 0;
            _hoveredId = string.Empty;
            _selectedPerk = string.Empty;
            Rebuild();
        }

        /// <summary>The port of a row's <c>MouseEvent.MOUSE_OVER</c> handler, <c>statInfo</c>.</summary>
        internal void OnRowHover(int rowIndex)
        {
            PipStatRow? row = RowAt(rowIndex);
            if (row == null) return;

            string id = row.Value.Id;
            if (id == _hoveredId) return;

            _hoveredId = id;
            RowHovered?.Invoke(row.Value);
            RefreshBottomText();
        }

        /// <summary>The port of the row's click handler, <c>itemClick</c> (<c>PipPageStat.as:863-959</c>).</summary>
        internal void OnRowClicked(int rowIndex)
        {
            PipStatRow? row = RowAt(rowIndex);
            if (row == null) return;

            // The picker's click is page state, so the page handles it before consulting any command
            // hook. PipPageStat.as:886-894 sets `selectedPerk` and reveals the accept button; nothing
            // else on the page can be done without a command the port does not have.
            if (_subPage == PipStatList.PerkPickSubPage && SelectPerk(row.Value)) return;

            if (RowActivated != null && RowActivated(row.Value)) return;

            // No handler: say what the Oracle would have done, rather than nothing at all. See
            // RowActivated for why the port cannot do it yet.
            string verb;
            switch (_subPage)
            {
                case PipStatList.SkillsSubPage:
                    verb = "spend a skill point on '" + row.Value.Id + "' (PipPageStat.as:872-885)";
                    break;
                case PipStatList.PerkPickSubPage:
                    verb = "select the perk '" + row.Value.Id + "' (PipPageStat.as:886-894)";
                    break;
                case PipStatList.HealthSubPage:
                    verb = "use medicine for '" + row.Value.Id + "' (PipPageStat.as:895-957)";
                    break;
                default:
                    verb = "open the info pane for '" + row.Value.Id + "' (PipPageStat.as:562-742)";
                    break;
            }

            Debug.Log($"[PipPageStatView] Row '{row.Value.Id}' — the Oracle would {verb}, but the port " +
                      "has no RPG command for it. Set RowActivated to supply one.");
        }

        /// <summary>
        /// The picker's row click — <c>PipPageStat.as:886-894</c>: <c>if(alpha &gt;= 1)</c> then
        /// <c>selectedPerk = id</c> and the accept button appears.
        ///
        /// <para><b>The alpha guard <i>is</i> the eligibility rule, and it has already run.</b>
        /// <c>setStatItem</c> dims a candidate the player cannot take yet to <c>alpha = 0.4</c>
        /// (<c>:550-553</c>) and this handler refuses anything below 1 — so
        /// <see cref="PipStatRow.Alpha"/> decides, and re-deriving eligibility here would be a second
        /// copy of a rule the model already applied.</para>
        ///
        /// <para><b>Where the commit happens.</b> The Oracle applies the pick through its accept button
        /// (<c>butOk</c> → <c>transOk</c>, <c>:1007-1012</c>), which the port has not built because the
        /// frame art for it is not imported. <see cref="CommitSelectedPerk"/> implements that branch and
        /// is exposed for the device to claim; until then, a second click on the row already picked
        /// commits it, so a pick can be made and applied rather than only made.</para>
        ///
        /// <para>Always returns true: the click has been handled, whether or not it changed anything,
        /// so the caller must not fall through to the "no command" report.</para>
        /// </summary>
        private bool SelectPerk(PipStatRow row)
        {
            // :887 — a dimmed row cannot be picked. Silent, exactly as the Oracle is.
            if (row.Alpha < 1f) return true;

            // A second click on the row already picked is the port's accept. The Oracle's accept is the
            // frame's `butOk` — see CommitSelectedPerk — and without some way to reach it the pick would
            // reach the bottom strip and stop there.
            if (_selectedPerk == row.Id)
            {
                CommitSelectedPerk();
                return true;
            }

            _selectedPerk = row.Id;
            RefreshBottomText();

            Debug.Log($"[PipPageStatView] Perk '{row.Id}' picked (PipPageStat.as:886-894). Click it again " +
                      "to spend the point — the Oracle's accept is the frame's `butOk`, which the port " +
                      "has not built (see CommitSelectedPerk).");

            return true;
        }

        /// <summary>
        /// Commits the picker's selection — the body of AS3 <c>PipPageStat.as:1007-1012</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The Oracle's rule, term for term.</b> <c>if(this.selectedPerk != "" &amp;&amp;
        /// this.pers.perkPoint &gt; 0) this.pers.addPerk(this.selectedPerk, true);</c> — and then
        /// <c>page2 = 3</c>, which runs <b>unconditionally</b>, so a refused pick still leaves the
        /// picker.</para>
        ///
        /// <para><b>The <c>true</c> is load-bearing, and it is why this goes through the source rather
        /// than calling <c>AddPerk</c> with one argument.</b> The one-argument form is the Oracle's
        /// default and is <b>free</b> — it is what a script's <c>perk</c> action uses
        /// (<c>Script.as:327</c>). The picker's form charges. A commit that used the free form would
        /// hand the player every perk in the game.</para>
        ///
        /// <para><b>Why it is a method and not a button handler.</b> The Oracle's accept is the frame's
        /// <c>butOk</c>, which the port has not built. Exposing the commit on its own means the device
        /// can claim it the same way it claims <see cref="SubPageClicked"/>, and the debug overlay can
        /// call it directly today.</para>
        /// </remarks>
        public bool CommitSelectedPerk()
        {
            string picked = _selectedPerk;

            if (string.IsNullOrEmpty(picked))
            {
                Debug.Log("[PipPageStatView] Commit with no perk selected — nothing to spend " +
                          "(PipPageStat.as:1016 requires `selectedPerk != \"\"`).");
                return false;
            }

            bool committed = _source != null && _source.TryCommitPerk(picked);

            // `page2 = 3` runs either way (:1018) — a refused pick still leaves the picker.
            _selectedPerk = string.Empty;
            _subPage = PipStatList.PerksSubPage;
            _scroll = 0;
            _hoveredId = string.Empty;
            Rebuild();

            if (committed)
            {
                PerkCommitted?.Invoke(picked);
                Debug.Log($"[PipPageStatView] Spent a perk point on '{picked}' " +
                          "(PipPageStat.as:1016-1018).");
            }
            else
            {
                Debug.LogWarning($"[PipPageStatView] '{picked}' was picked but not applied — no perk " +
                                 "point to spend, or it is already at its rank ceiling. The Oracle " +
                                 "closes the picker here regardless (:1018).");
            }

            return committed;
        }

        /// <summary>Scrolls the list. Positive moves the window down the list.</summary>
        public void ScrollRows(int delta)
        {
            if (delta == 0) return;

            int next = Mathf.Clamp(_scroll + delta, 0, PipStatList.MaxScroll(_rows.Count));
            if (next == _scroll) return;

            _scroll = next;
            Rebuild();
        }

        /// <summary>The row shown at a row-clip index, or null when that clip is empty.</summary>
        private PipStatRow? RowAt(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= PipStatList.VisibleRows) return null;
            if (_rowRects[rowIndex] == null || !_rowRects[rowIndex].gameObject.activeSelf) return null;

            int entryIndex = PipStatList.EntryIndexForRow(rowIndex, _rows.Count, _scroll);
            if (entryIndex < 0 || entryIndex >= _rows.Count) return null;

            return _rows[entryIndex];
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
        /// <c>PipAnchor.ToUnityAnchoredPosition</c>, the same flip the shell and the inventory page use.
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
            public PipPageStatView Page;
            public int RowIndex;

            public void OnPointerEnter(PointerEventData eventData) => Page?.OnRowHover(RowIndex);

            public void OnPointerClick(PointerEventData eventData) => Page?.OnRowClicked(RowIndex);
        }

        private sealed class ScrollRelay : MonoBehaviour, IScrollHandler
        {
            public PipPageStatView Page;

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
