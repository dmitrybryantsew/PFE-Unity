using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using PFE.Data.Definitions;
using PFE.Systems.Crafting;
using PFE.Systems.Inventory;
using PFE.Systems.Items;
using PFE.UI.Common;

namespace PFE.UI.PipBuck.Pages
{
    /// <summary>
    /// The workbench — <b>page 7</b> of the device, the port of <c>fe.inter.PipPageWork</c>.
    ///
    /// <para><b>It is one page with three verbs, and which of them exist is the station's answer.</b>
    /// A workbench (<c>workTip == "work"</c>) builds, upgrades and repairs; a stove or a lab only
    /// builds; the portable <c>mworkbench</c> only repairs (<c>PipPageWork.as:48-58</c>). The rule is
    /// already ported and tested in <see cref="PipSubTabs"/> — this page must not re-decide it, only
    /// obey <see cref="PipSubTabs.Coerce"/>.</para>
    ///
    /// <para><b>The frame is the inventory's.</b> Page 7 sets <c>itemClass = visPipInvItem</c>
    /// (<c>:23</c>) and does not set <c>pageClass</c>, so it draws the same 18-row list at the same
    /// pitch as page 2. The row <i>clip</i> (<c>visPipInvItem</c>) has not been imported as a
    /// separate symbol; the rows here are drawn as text at the Oracle's pitch, exactly as
    /// <see cref="PipPageVendView"/> does and for the same reason.</para>
    ///
    /// <para><b>Three columns, not five.</b> The Oracle writes only <c>statHead.nazv</c>,
    /// <c>.hp</c> and <c>.ammotip</c> on this page (<c>:71-73</c>, <c>:215-218</c>); <c>fav</c>,
    /// <c>ammo</c> and <c>ramka</c> are explicitly blanked (<c>:70-74</c>, <c>:286-296</c>). So the
    /// page has a name column, a condition/count column and a percentage column, and nothing else —
    /// adding the inventory's mass column would be inventing a field the Oracle leaves empty.</para>
    /// </summary>
    public sealed class PipPageWorkView : IPipPage, IPipContextPage
    {
        /// <summary>The Oracle's page index for the workbench — <c>pages[7]</c> (<c>PipBuck.as:111</c>).</summary>
        public const int WorkPageId = 7;

        private const int ColumnCount = 3;

        // ── Row geometry ──────────────────────────────────────────────────────
        //
        // PORT CHOICE, named as one: the row clip is the inventory's (visPipInvItem), whose
        // DefineEditText placements this page does not own. These three columns line up with the
        // 560-wide row panel and with the three fields the Oracle writes.

        private static readonly float[] ColumnX = { 6f, 386f, 470f };
        private static readonly float[] ColumnWidth = { 372f, 80f, 84f };
        private static readonly bool[] ColumnRight = { false, true, true };

        private const float RowTextHeight = 21.9f;
        private const float RowTextY = 3f;
        private const int RowFontSize = 15;

        private static readonly Vector2 RowHitSize = new Vector2(560f, PipBuckLayout.PageRowPitch);

        // ── Page geometry (shared with the inventory and the trade page) ──────

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

        private static readonly Color RowBackTint = new Color(1f, 1f, 1f, 0.10f);

        /// <summary>The 50 % alpha the Oracle gives a row it will refuse for lack of skill
        /// (<c>PipPageWork.as:299-302</c>).</summary>
        private const float SkillLockedAlpha = 0.5f;

        // ── Wiring ────────────────────────────────────────────────────────────

        private readonly WorkbenchService _service;

        private PipBuckCompositionDefinition _composition;
        private Font _font;

        /// <summary>The Oracle's localisation document — <c>Res.pipText</c>.</summary>
        public PipTextTable TextTable { get; set; } = PipTextTable.Empty;

        /// <summary>What opened the page — <c>pip.workTip</c>, already normalised.</summary>
        public PipSubTabContext Context { get; private set; } = PipSubTabContext.Default;

        /// <summary>The sub-tab row, for the device to hang its click handler off.</summary>
        public PipSubTabBar SubTabs => _subTabs;

        /// <summary>The page's own <c>page2Click</c> — forwarded to the device.</summary>
        public Action<int> SubPageClicked { get; set; }

        /// <summary>Raised after a click that changed something, so the device can play <c>pip.snd(1)</c>
        /// (<c>PipPageWork.as:614</c>).</summary>
        public Action Committed { get; set; }

        private readonly PipSubTabBar _subTabs = new PipSubTabBar();

        private RectTransform _root;
        private int _subPage = 1;
        private int _scroll;
        private int _hoveredRow = -1;
        private string _message;

        private readonly List<CraftRowView> _craftRows = new List<CraftRowView>();
        private readonly List<UpgradeCandidate> _upgradeRows = new List<UpgradeCandidate>();
        private readonly List<RepairCandidate> _repairRows = new List<RepairCandidate>();

        private readonly RectTransform[] _rowRects = new RectTransform[PipBuckLayout.PageRowCount];
        private readonly Image[] _rowBacks = new Image[PipBuckLayout.PageRowCount];
        private readonly Text[,] _cells = new Text[PipBuckLayout.PageRowCount, ColumnCount];
        private readonly Text[] _headerCells = new Text[ColumnCount];

        private RectTransform _listScrollbar;
        private RectTransform _listThumb;
        private Text _emptyText;
        private Text _bottomText;
        private Text _messageText;
        private Text _infoName;
        private Text _infoText;

        public PipPageWorkView(WorkbenchService service)
        {
            _service = service;
        }

        public int PageId => WorkPageId;

        public bool IsImplemented => true;

        /// <summary>The sub-page the page is on — the Oracle's <c>page2</c>.</summary>
        public int SubPage => _subPage;

        /// <summary>Supplies the composition (for the sub-tab sprite), the font and the document.
        /// Same seam, and the same reason, as <see cref="PipStubPage.Configure"/>.</summary>
        public void Configure(PipBuckCompositionDefinition composition, Font font, PipTextTable text)
        {
            _composition = composition;
            _font = font;
            TextTable = text ?? PipTextTable.Empty;
        }

        // ── Context ───────────────────────────────────────────────────────────

        /// <summary>
        /// Open the page on a station — <c>Interact.allAct</c>'s <c>pip.workTip = allact;
        /// pip.onoff(7)</c> (<c>Interact.as:1621-1625</c>) and <c>Invent.useItem</c>'s portable-bench
        /// branch (<c>Invent.as:561-571</c>).
        ///
        /// <para><b>The normalisation is the Oracle's, and it must happen here.</b> <c>mworklab</c> and
        /// <c>mworkexpl</c> are rewritten to <c>lab</c>/<c>expl</c> at the top of <c>setSubPages</c>
        /// (<c>PipPageWork.as:40-47</c>) <i>before</i> the sub-tab row is configured — so by the time
        /// anything asks "which sub-pages exist", the alias is gone. Writing the raw alias into the
        /// context would leave <see cref="PipSubTabs"/> matching neither branch and showing all three
        /// sub-tabs on a portable lab.</para>
        /// </summary>
        public void Open(string workTip)
        {
            string station = CraftingRecipeTable.NormaliseStation(workTip);
            Context = new PipSubTabContext(string.Empty, string.Empty, station, false);

            _subPage = PipSubTabs.Coerce(WorkPageId, 1, Context);
            _scroll = 0;
            _hoveredRow = -1;
            _message = null;

            Rebuild();
        }

        /// <summary>Drop the station — <c>PipBuck.as:323-324</c>'s context clear, on close only.</summary>
        public void Close()
        {
            Context = PipSubTabContext.Default;
            _message = null;
            _hoveredRow = -1;
        }

        public void OnDeviceClosed()
        {
            Close();
            Rebuild();
        }

        // ── IPipPage ──────────────────────────────────────────────────────────

        public void SetStatus(int subPage)
        {
            if (subPage > 0)
            {
                _subPage = PipSubTabs.Coerce(WorkPageId, subPage, Context);
                _scroll = 0;
                _message = null;
            }

            Rebuild();
        }

        public void Step()
        {
            // The Oracle's PipPageWork does not override `step`; its contents are static between
            // rebuilds. Deliberately silent — this runs 30 times a second.
        }

        public void SetVisible(bool visible)
        {
            if (_root == null) return;
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        public void Build(RectTransform pageRoot)
        {
            if (pageRoot == null)
            {
                Debug.LogError("[PipPageWorkView] Build called with no page root. The workbench page " +
                               "will not exist; page 7 will show an empty screen.");
                return;
            }

            if (_root != null) return;

            _root = CreateRect("PipWork Page", pageRoot);
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
            Place(rect, ColumnX[column], RowTextY);

            var text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = RowFontSize;
            text.color = colour;
            text.alignment = ColumnRight[column] ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private void BuildListScrollbar()
        {
            _listScrollbar = CreateRect("ListScrollbar", _root);
            Place(_listScrollbar, ListScrollbarX, ListScrollbarY);
            _listScrollbar.sizeDelta = new Vector2(ListScrollbarWidth, ListScrollbarHeight);

            var track = _listScrollbar.gameObject.AddComponent<Image>();
            track.color = new Color(1f, 1f, 1f, 0.08f);
            track.raycastTarget = false;

            var thumbGo = new GameObject("Thumb", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _listThumb = (RectTransform)thumbGo.transform;
            _listThumb.SetParent(_listScrollbar, false);
            AnchorTopLeft(_listThumb);
            _listThumb.sizeDelta = new Vector2(ListScrollbarWidth, ThumbMinHeight);
            _listThumb.anchoredPosition = Vector2.zero;

            var thumb = thumbGo.GetComponent<Image>();
            thumb.color = new Color(1f, 1f, 1f, 0.35f);
            thumb.raycastTarget = false;

            _listScrollbar.gameObject.SetActive(false);
        }

        private void BuildInfoPane()
        {
            var nameGo = new GameObject("InfoName", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var nameRect = (RectTransform)nameGo.transform;
            nameRect.SetParent(_root, false);
            AnchorTopLeft(nameRect);
            nameRect.sizeDelta = new Vector2(InfoNameWidth, InfoNameHeight);
            Place(nameRect, InfoNameX, InfoNameY);

            _infoName = nameGo.GetComponent<Text>();
            _infoName.font = _font;
            _infoName.fontSize = InfoNameFontSize;
            _infoName.color = PfeUiColors.PageYellow;
            _infoName.alignment = TextAnchor.UpperRight;
            _infoName.horizontalOverflow = HorizontalWrapMode.Wrap;
            _infoName.verticalOverflow = VerticalWrapMode.Overflow;
            _infoName.raycastTarget = false;

            var textGo = new GameObject("InfoText", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(_root, false);
            AnchorTopLeft(textRect);
            textRect.sizeDelta = new Vector2(InfoTextWidth, InfoTextBottom - InfoTextY);
            Place(textRect, InfoTextX, InfoTextY);

            _infoText = textGo.GetComponent<Text>();
            _infoText.font = _font;
            _infoText.fontSize = InfoTextFontSize;
            _infoText.color = PfeUiColors.PageR0;
            _infoText.alignment = TextAnchor.UpperLeft;
            _infoText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _infoText.verticalOverflow = VerticalWrapMode.Overflow;
            _infoText.raycastTarget = false;
        }

        private void BuildEmptyText()
        {
            var go = new GameObject("EmptyText", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(EmptyTextWidth, 40f);
            Place(rect, EmptyTextX, EmptyTextY);

            _emptyText = go.GetComponent<Text>();
            _emptyText.font = _font;
            _emptyText.fontSize = RowFontSize;
            _emptyText.color = PfeUiColors.PageR0;
            _emptyText.alignment = TextAnchor.UpperLeft;
            _emptyText.raycastTarget = false;
        }

        private void BuildBottomText()
        {
            var go = new GameObject("BottomText", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(BottomTextWidth, 22f);
            Place(rect, BottomTextX, BottomTextY);

            _bottomText = go.GetComponent<Text>();
            _bottomText.font = _font;
            _bottomText.fontSize = RowFontSize;
            _bottomText.color = PfeUiColors.PageR0;
            _bottomText.alignment = TextAnchor.UpperLeft;
            _bottomText.raycastTarget = false;
        }

        private void BuildMessageText()
        {
            var go = new GameObject("MessageText", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(500f, 22f);
            Place(rect, BottomTextX, BottomTextY - 26f);

            _messageText = go.GetComponent<Text>();
            _messageText.font = _font;
            _messageText.fontSize = RowFontSize;
            _messageText.color = PfeUiColors.PageYellow;
            _messageText.alignment = TextAnchor.UpperLeft;
            _messageText.raycastTarget = false;
        }

        // ── Refresh ───────────────────────────────────────────────────────────

        public void Rebuild()
        {
            if (_root == null) return;

            RefreshRows();
            ClampScroll();

            _subTabs.Refresh(PageId, _subPage, TextTable, Context, PipSubTabSignals.None);

            RefreshHeader();
            RefreshRowCells();
            RefreshScrollbar();
            RefreshEmptyText();
            RefreshFooter();
            RefreshInfoPane();
        }

        private void RefreshRows()
        {
            _craftRows.Clear();
            _upgradeRows.Clear();
            _repairRows.Clear();

            if (_service == null) return;

            switch (_subPage)
            {
                case 1: _craftRows.AddRange(_service.BuildCraftRows(Context.WorkTip)); break;
                case 2: _upgradeRows.AddRange(_service.BuildUpgradeRows()); break;
                case 3: _repairRows.AddRange(_service.BuildRepairRows()); break;
            }
        }

        private int RowCount()
        {
            switch (_subPage)
            {
                case 1: return _craftRows.Count;
                case 2: return _upgradeRows.Count;
                case 3: return _repairRows.Count;
                default: return 0;
            }
        }

        private void ClampScroll()
        {
            int maxScroll = Mathf.Max(0, RowCount() - PipBuckLayout.PageRowCount);
            if (_scroll > maxScroll) _scroll = maxScroll;
            if (_scroll < 0) _scroll = 0;
        }

        private void RefreshHeader()
        {
            string[] captions = HeaderCaptions();
            bool show = RowCount() > 0;

            for (int column = 0; column < ColumnCount; column++)
            {
                if (_headerCells[column] == null) continue;
                _headerCells[column].text = show ? captions[column] : string.Empty;
            }
        }

        /// <summary>
        /// The three field captions per sub-page, from the Oracle's own writes:
        /// <c>nazv</c>/<c>hp</c> on create (<c>:71-72</c>), nothing on upgrade (<c>:161-165</c> blanks
        /// every field), and <c>ii2</c>/<c>ii3</c>/<c>repairto</c> on repair (<c>:215-218</c>).
        /// </summary>
        private string[] HeaderCaptions()
        {
            switch (_subPage)
            {
                case 1: return new[] { Key("work1", "Create"), Key("iv6", "you have"), string.Empty };
                case 3: return new[] { Key("ii2", "name"), Key("ii3", "condition"), Key("repairto", "repair by") };
                default: return new[] { string.Empty, string.Empty, string.Empty };
            }
        }

        private void RefreshRowCells()
        {
            for (int row = 0; row < PipBuckLayout.PageRowCount; row++)
            {
                int index = _scroll + row;
                bool visible = index < RowCount();

                if (_rowRects[row] != null && _rowRects[row].gameObject.activeSelf != visible)
                    _rowRects[row].gameObject.SetActive(visible);

                if (!visible) continue;

                string[] values = RowValues(index);
                for (int column = 0; column < ColumnCount; column++)
                {
                    Text cell = _cells[row, column];
                    if (cell == null) continue;
                    cell.text = values[column];
                }

                // :299-302 — a row the player's skill cannot reach is drawn at half alpha.
                float alpha = _subPage == 1 && _craftRows[index].Ok > 1 ? SkillLockedAlpha : 1f;
                for (int column = 0; column < ColumnCount; column++)
                {
                    Text cell = _cells[row, column];
                    if (cell == null) continue;
                    Color colour = cell.color;
                    colour.a = alpha;
                    cell.color = colour;
                }

                if (_rowBacks[row] != null) _rowBacks[row].enabled = _hoveredRow == index;
            }
        }

        private string[] RowValues(int index)
        {
            switch (_subPage)
            {
                case 1:
                {
                    CraftRowView row = _craftRows[index];
                    string name = NameOf(row.Kind, row.Id);
                    return new[] { name, row.Have > 0 ? row.Have.ToString() : string.Empty, string.Empty };
                }

                case 2:
                {
                    UpgradeCandidate row = _upgradeRows[index];
                    string name = NameOf(row.Kind, row.Id);
                    string detail = row.Kind == CraftRowKind.Armor
                        ? row.Level + " → " + (row.Level + 1)
                        : Key("upgrade2", "II");
                    return new[] { name, detail, string.Empty };
                }

                default:
                {
                    RepairCandidate row = _repairRows[index];
                    string name = NameOf(row.Kind, row.Id);
                    float condition = row.MaxHp > 0f ? row.Hp / row.MaxHp * 100f : 0f;
                    float perComponent = RepairRules.DisplayPercent(row.FractionOfMax,
                        _service != null ? _service.RepairMultiplier : 1f);
                    return new[]
                    {
                        name,
                        Math.Round(condition * 10.0) / 10.0 + "%",
                        perComponent.ToString("0.#") + "%"
                    };
                }
            }
        }

        private void RefreshScrollbar()
        {
            if (_listScrollbar == null || _listThumb == null) return;

            int count = RowCount();
            bool needed = count > PipBuckLayout.PageRowCount;

            if (_listScrollbar.gameObject.activeSelf != needed) _listScrollbar.gameObject.SetActive(needed);
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

            if (RowCount() > 0)
            {
                _emptyText.text = string.Empty;
                return;
            }

            switch (_subPage)
            {
                case 1: _emptyText.text = Key("emptycreate", "No schematics or recipes available"); break;
                case 2: _emptyText.text = Key("emptyupgrade", "Nothing to upgrade"); break;
                default: _emptyText.text = Key("emptyrep", "Nothing to repair"); break;
            }
        }

        private void RefreshFooter()
        {
            if (_messageText != null) _messageText.text = _message ?? string.Empty;
            if (_bottomText == null) return;

            // The Oracle writes the caps line and then blanks it (:59, :66); the only thing that
            // reaches this field on this page is `showBottext` — the hovered repair row's component
            // stock (:355-369).
            _bottomText.text = string.Empty;

            if (_subPage != 3 || _hoveredRow < 0 || _hoveredRow >= _repairRows.Count) return;

            RepairCandidate row = _repairRows[_hoveredRow];
            int have = _service != null ? _service.CountOf(row.ComponentId) : 0;
            string name = PipItemNames.Item(TextTable, row.ComponentId, string.Empty, string.Empty, 0,
                string.Empty, row.ComponentId);
            _bottomText.text = name + ": " + have;
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

            switch (_subPage)
            {
                case 1:
                {
                    CraftRowView row = _craftRows[_hoveredRow];
                    _infoName.text = NameOf(row.Kind, row.Id);

                    CraftingRecipe? recipe = CraftingRecipeTable.FindByOutput(_service?.Recipes, row.Id);
                    if (recipe.HasValue)
                    {
                        for (int i = 0; i < recipe.Value.Materials.Length; i++)
                        {
                            CraftMaterialRow material = recipe.Value.Materials[i];
                            int have = _service != null ? _service.Available(material.Id) : 0;
                            string name = PipItemNames.Item(TextTable, material.Id, string.Empty, string.Empty,
                                0, string.Empty, material.Id);
                            sb.Append(name).Append(": ").Append(have).Append('/').Append(material.Count)
                              .Append('\n');
                        }

                        if (recipe.Value.HasSkillGate)
                        {
                            sb.Append(Key("needSkill", "Required skill")).Append(' ')
                              .Append(SkillName(recipe.Value.Skill)).Append('-')
                              .Append(recipe.Value.SkillLevel).Append('\n');
                        }
                    }

                    break;
                }

                case 2:
                {
                    UpgradeCandidate row = _upgradeRows[_hoveredRow];
                    _infoName.text = NameOf(row.Kind, row.Id);
                    if (row.Kind == CraftRowKind.Armor)
                    {
                        string name = PipItemNames.Item(TextTable, row.ComponentId, string.Empty, string.Empty,
                            0, string.Empty, row.ComponentId);
                        sb.Append(name).Append(": ").Append(row.Cost).Append('\n');
                    }
                    else
                    {
                        CraftingRecipe? recipe = CraftingRecipeTable.FindByOutput(_service?.Recipes, row.Id);
                        if (recipe.HasValue)
                        {
                            for (int i = 0; i < recipe.Value.Materials.Length; i++)
                            {
                                CraftMaterialRow material = recipe.Value.Materials[i];
                                int have = _service != null ? _service.Available(material.Id) : 0;
                                string name = PipItemNames.Item(TextTable, material.Id, string.Empty,
                                    string.Empty, 0, string.Empty, material.Id);
                                sb.Append(name).Append(": ").Append(have).Append('/').Append(material.Count)
                                  .Append('\n');
                            }
                        }
                    }

                    break;
                }

                default:
                {
                    RepairCandidate row = _repairRows[_hoveredRow];
                    _infoName.text = NameOf(row.Kind, row.Id);
                    sb.Append(Key("ii3", "condition")).Append(": ")
                      .Append(Math.Round(row.Hp / Mathf.Max(1f, row.MaxHp) * 1000.0) / 10.0).Append("%\n");
                    sb.Append(Key("inforepair", "[click] - repair")).Append('\n');

                    string name = PipItemNames.Item(TextTable, row.ComponentId, string.Empty, string.Empty,
                        0, string.Empty, row.ComponentId);
                    int have = _service != null ? _service.CountOf(row.ComponentId) : 0;
                    sb.Append(name).Append(": ").Append(have).Append('\n');
                    break;
                }
            }

            _infoText.text = sb.ToString();
        }

        // ── Interaction ───────────────────────────────────────────────────────

        private void OnHover(int rowIndex)
        {
            int index = _scroll + rowIndex;
            _hoveredRow = index < RowCount() ? index : -1;
            RefreshRowCells();
            RefreshFooter();
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

        /// <summary>
        /// <c>itemClick</c> (<c>PipPageWork.as:399-617</c>) — one click, one action, then a redraw.
        ///
        /// <para><b>A refusal does not rebuild and does not sound.</b> The Oracle's refusals
        /// <c>return</c> before <c>pip.snd(1)</c> (<c>:437-440</c>, <c>:544-547</c>), and a rebuild
        /// would also drop the row the player just clicked out from under the cursor. A success
        /// rebuilds and raises <see cref="Committed"/>.</para>
        /// </summary>
        private void OnRowClicked(int rowIndex, bool right, bool shift, bool ctrl)
        {
            if (right) return;

            int index = _scroll + rowIndex;
            if (index >= RowCount() || _service == null) return;

            WorkbenchResult result;

            switch (_subPage)
            {
                case 1:
                    result = _service.Craft(_craftRows[index].Id);
                    break;
                case 2:
                {
                    UpgradeCandidate row = _upgradeRows[index];
                    result = row.Kind == CraftRowKind.Armor
                        ? _service.UpgradeArmor(row.Id)
                        : _service.UpgradeWeapon(row.Id);
                    break;
                }
                default:
                {
                    RepairCandidate row = _repairRows[index];
                    result = row.Kind == CraftRowKind.Armor
                        ? _service.RepairArmor(row.Id)
                        : _service.RepairWeapon(row.Id);
                    break;
                }
            }

            _message = MessageFor(result, DisplayRowName(index));
            if (!result.Succeeded) return;

            Rebuild();
            Committed?.Invoke();
        }

        private string DisplayRowName(int index)
        {
            switch (_subPage)
            {
                case 1: return index < _craftRows.Count ? NameOf(_craftRows[index].Kind, _craftRows[index].Id) : string.Empty;
                case 2: return index < _upgradeRows.Count ? NameOf(_upgradeRows[index].Kind, _upgradeRows[index].Id) : string.Empty;
                default: return index < _repairRows.Count ? NameOf(_repairRows[index].Kind, _repairRows[index].Id) : string.Empty;
            }
        }

        /// <summary>
        /// Renders a result's <c>infoText</c> key. The two argument placeholders the oracle uses are
        /// <c>@1</c>/<c>@2</c> (<c>created</c> = "Weapon @1 created", <c>created2</c> = "Item @1 created.
        /// You have total - @2"), so the substitution is positional.
        ///
        /// <para><b>@1 falls back to the clicked row's name.</b> The oracle passes <c>cnazv</c> — the
        /// name of the row that was clicked — and the service deliberately does not know display names,
        /// so the page supplies it.</para>
        /// </summary>
        private string MessageFor(in WorkbenchResult result, string rowName)
        {
            if (string.IsNullOrEmpty(result.MessageKey)) return string.Empty;

            string text = Key(result.MessageKey, result.MessageKey);
            string arg1 = result.Arg1.Length > 0 ? DisplayName(result.Arg1) : rowName;
            if (!string.IsNullOrEmpty(arg1)) text = text.Replace("@1", arg1);
            if (result.Arg2.Length > 0) text = text.Replace("@2", result.Arg2);
            return text;
        }

        // ── Naming ────────────────────────────────────────────────────────────

        /// <summary>
        /// A row's name, from the table the oracle reads for that shape: <c>Res.txt("w", id)</c> for a
        /// weapon, <c>Res.txt("a", id)</c> for an armour, <c>Res.txt("i", id)</c> for anything else
        /// (<c>PipPageWork.as:97</c>, <c>:115</c>, <c>:134</c>).
        ///
        /// <para><b>The item branch does not use the schematic override.</b> A row on this page is the
        /// recipe's <i>product</i>, and the oracle asks the plain item table — the <c>scheme1</c>/<c>recipe</c>
        /// wording belongs to the scheme's own row in the inventory (<c>Item.as:257-267</c>), not to the
        /// product listed here.</para>
        /// </summary>
        private string NameOf(CraftRowKind kind, string id)
        {
            switch (kind)
            {
                case CraftRowKind.Weapon: return PipItemNames.Weapon(TextTable, id, 0, id);
                case CraftRowKind.Armor: return PipItemNames.Armor(TextTable, id, id);
                default: return PipItemNames.Item(TextTable, id, string.Empty, string.Empty, 0,
                    string.Empty, id);
            }
        }

        /// <summary>
        /// A message argument, rendered through the four tables the oracle's <c>Res.txt</c> can answer
        /// from — weapon, armour, item, then the skill table <c>e</c> (which is what
        /// <c>needSkill</c>'s <c>@1</c> is, <c>PipPageWork.as:376</c>). First hit wins; otherwise the id
        /// is shown as-is.
        /// </summary>
        private string DisplayName(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return string.Empty;

            string text = TextTable != null ? TextTable.Text("w", arg) : null;
            if (!string.IsNullOrEmpty(text)) return text;

            text = TextTable != null ? TextTable.Text("a", arg) : null;
            if (!string.IsNullOrEmpty(text)) return text;

            text = TextTable != null ? TextTable.Text("i", arg) : null;
            if (!string.IsNullOrEmpty(text)) return text;

            text = TextTable != null ? TextTable.Text("e", arg) : null;
            if (!string.IsNullOrEmpty(text)) return text;

            return arg;
        }

        private string SkillName(string skillId) => DisplayName(skillId);

        private string Key(string id, string fallback)
        {
            string text = TextTable != null ? TextTable.Pip(id) : null;
            return string.IsNullOrEmpty(text) || text.StartsWith("*", StringComparison.Ordinal) ? fallback : text;
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
            public PipPageWorkView Page;
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
            public PipPageWorkView Page;

            public void OnScroll(PointerEventData eventData)
            {
                Page?.ScrollRows(eventData.scrollDelta.y > 0f ? -1 : 1);
            }
        }
    }
}
