using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using PFE.Data.Definitions;
using PFE.Systems.Inventory;
using PFE.Systems.Items;
using PFE.Systems.RPG;
using PFE.Systems.Weapons;
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
    /// <para><b>The row art is now the Oracle's own symbols, at the Oracle's own offsets.</b>
    /// <c>visPipInvItem</c> (symbol 975) frame 1 carries twelve placements — <c>back</c> (498,
    /// 560x25, centre-registered at row-local (278.6, 12.0)), <c>ramka</c> (897, 559x26, at
    /// (277.9, 12.0)), <c>trol</c> (876, the per-item icon, at (45.0, 12.0)) and nine
    /// <c>DefineEditText</c> fields — and this page now draws all three art children and puts the six
    /// visible text columns at the field placements read out of the SWF (twips / 20). Until the
    /// 2026-10-14 pass the row sprite, the selection frame, the per-row icon and the scrollbar were
    /// all missing, which is what the play-test reported as "no rows graphics, no idea if
    /// scrollable".</para>
    ///
    /// <para><b>The right-hand pane is the Oracle's info pane.</b> <c>visPipInv</c> (1113) carries
    /// <c>nazv</c> (1014, right-aligned, at (631, 87)), <c>ico</c> (240, at (630, 160.1)) and
    /// <c>info</c> (1013, at (626, ico-bottom + 10)). Hovering a row is the Oracle's
    /// <c>statInfo</c> (<c>PipPageInv.as:496-530</c>) → <c>infoItem</c> → <c>infoStr</c>, and the
    /// weapon branch of <c>infoStr</c> (<c>PipPage.as:421-597</c>) is what fills the stat block.</para>
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

        // ── Row geometry ──────────────────────────────────────────────────────
        //
        // ATTESTED, corrected 2026-10-14. The old offsets were a PORT CHOICE made while visPipInvItem
        // (symbol 975) was believed unimported; the SWF's frame-1 display list answers the question
        // directly (dumped with .workbuddy-ai/tmp/swf_sprite_frame1.py on swfxml_pfe.xml):
        //
        //   depth  charId  name     scale         translate (px)
        //       1     498  back     1.000,1.000   ( 278.6,  12.0)   <- centre-registered
        //       3     966  nazv     1.000,1.000   (  58.5,   3.0)
        //       4     967  fav      1.000,1.000   ( -30.5,   3.0)
        //       5     968  id       1.000,1.000   ( 633.8,   3.0)   <- hidden data carrier
        //       6     969  hp       1.000,1.000   ( 211.5,   3.0)
        //       7     970  ammo     1.000,1.000   ( 340.5,   3.0)
        //       8     971  ammotip  1.000,1.000   ( 408.4,   3.0)
        //       9     972  cat      1.000,1.000   ( 591.8,   3.0)   <- hidden data carrier
        //      10     973  rid      1.000,1.000   ( 565.5,   3.0)   <- hidden data carrier
        //      11     876  trol     1.000,1.000   (  45.0,  12.0)   <- centre-registered
        //      13     974  mass     1.000,1.000   ( 347.4,   2.9)
        //      14     897  ramka    1.000,1.000   ( 277.9,  12.0)   <- centre-registered
        //
        // The three centre-registered children are placed with a centre pivot at the placement itself;
        // the nine text fields are DefineEditTexts, which register at their TOP-LEFT, so they take the
        // placement as their top-left corner. Field widths are the DefineEditText bounds' Xmax
        // (twips/20); every field's bounds begin at Xmin = -2, the 2 px gutter Flash pads a text box
        // with, so the usable width IS Xmax. Alignment comes from each field's own `align` attribute:
        // fav / hp / ammo are right-aligned, everything else left.
        //
        // `mass` (347.4..402.9) and `ammo` (340.5..403.95) genuinely overlap. They are mutually
        // exclusive in practice — ammo carries the round count on the weapon page and the price on
        // valuables, mass carries the stack weight only in hard mode — but a valuable in hard mode
        // would draw both, and that is the Oracle's own output, not a porting error.

        /// <summary>Row-local top-left of each column, in the Oracle's pixel space.</summary>
        private static readonly float[] ColumnX = { -30.5f, 58.5f, 211.5f, 340.5f, 347.4f, 408.4f };

        /// <summary>Usable width of each column — the DefineEditText bounds' Xmax, twips/20.</summary>
        private static readonly float[] ColumnWidth = { 65f, 283.5f, 124.5f, 63.45f, 55.5f, 151.5f };

        /// <summary>Which columns are right-aligned — the DefineEditText `align` attribute.</summary>
        private static readonly bool[] ColumnRight = { true, false, true, true, false, false };

        /// <summary>
        /// The row's clickable footprint. The Oracle's row clip's own bounds run from <c>fav</c>'s
        /// left edge (-30.5) to <c>id</c>'s right (666.8), but the <i>visible</i> panel is
        /// <c>back</c>: 560 wide from -1.4. The hit area is the panel's, so a click in the blank
        /// right half of the page is not silently claimed by an invisible row.
        /// </summary>
        private static readonly Vector2 RowHitSize = new Vector2(560f, PipBuckLayout.PageRowPitch);

        /// <summary>Vertical offset of the text fields inside a row — every field's placement y.</summary>
        private const float RowTextY = 3f;

        /// <summary>Height of the text fields — the DefineEditText bounds' Y span, twips/20.</summary>
        private const float RowTextHeight = 21.9f;

        /// <summary>Row text size. The fields' <c>fontHeight</c> is 320 twips = 16 px.</summary>
        private const int RowFontSize = 16;

        /// <summary><c>back</c>, symbol 498 — 560x25, centre-registered at (278.6, 12.0).</summary>
        private static readonly Vector2 RowBackSize = new Vector2(560f, 25f);
        private static readonly Vector2 RowBackCentre = new Vector2(278.6f, 12f);

        /// <summary><c>ramka</c>, symbol 897 — 559x26, centre-registered at (277.9, 12.0).</summary>
        private static readonly Vector2 RowFrameSize = new Vector2(559f, 26f);
        private static readonly Vector2 RowFrameCentre = new Vector2(277.9f, 12f);

        /// <summary>
        /// <c>trol</c>, symbol 876 — the per-item icon, centre-registered at (45.0, 12.0). Its frame
        /// shapes' bounds are roughly 15x17 around the origin (shape 848: X -7.45..7.25, Y -8.5..8.6),
        /// which is what makes the registration the centre; the generated PNGs are 20x19 canvases
        /// around that.
        /// </summary>
        private static readonly Vector2 RowIconCentre = new Vector2(45f, 12f);
        private static readonly Vector2 RowIconSize = new Vector2(20f, 19f);

        // ── Page-level geometry (sprite 1113, visPipInv) ──────────────────────
        //
        // ATTESTED, same dump. The page-level placements the Oracle's own display list records:
        //
        //   scBar (492)      (600.0, 100.0)  scaleY 5.360  -> 17 x 536
        //   nazv (1014)      (631.0,  87.0)  w 283, h 61, align RIGHT, fontHeight 20
        //   ico  (240)       (630.0, 160.1)  — an empty holder clip; shape 241's bounds are
        //                                    125.3 x 97.2, but the weapon art placed inside it
        //                                    overflows the box (Flash does not clip children)
        //   info (1013)      (626.0, y)      w 286, fontHeight 14; y is ico.y + ico.height + 10
        //                                    when an icon is shown, else its own 162.1
        //   emptytext (1016) ( 19.5,  86.2)  w 569.45, fontHeight 14
        //   bottext (1015)   ( 19.5, 653.1)  w 872.5, fontHeight 18

        // The scrollbar's five numbers now live in <see cref="PipRowChrome"/>, because every list
        // sub-tab on the device needs the same bar and three copies of "600, 100, 17, 536, 54" is
        // three chances to disagree. Aliased rather than deleted so every call site below still reads
        // as the Oracle's own name for the piece.
        private const float ListScrollbarX = PipRowChrome.ListScrollbarX;
        private const float ListScrollbarY = PipRowChrome.ListScrollbarY;
        private const float ListScrollbarWidth = PipRowChrome.ListScrollbarWidth;
        private const float ListScrollbarHeight = PipRowChrome.ListScrollbarHeight;   // 100 authored x scaleY 5.360

        /// <summary>
        /// The thumb's minimum height, in the stretched track's pixels. Flash's UIScrollBar clamps the
        /// thumb to the skin's own arrow-to-arrow span; the thumb skin is 54 px tall, and squeezing a
        /// 9-sliced sprite below its caps would fold the caps over themselves.
        /// </summary>
        private const float ThumbMinHeight = PipRowChrome.ThumbMinHeight;

        private const float InfoNameX = 631f;
        private const float InfoNameY = 87f;
        private const float InfoNameWidth = 283f;
        private const float InfoNameHeight = 61f;
        private const int InfoNameFontSize = 20;

        private const float InfoIcoX = 630f;
        private const float InfoIcoY = 160.1f;

        /// <summary>
        /// The widest the weapon image is drawn. The Oracle lets <c>infIco</c> overflow the ico clip
        /// at its own natural size scaled by <c>vis.@icomult</c>; the port clamps to the page's free
        /// width instead, because a 1:1 sprite wider than the pane would run off the device.
        /// </summary>
        private const float InfoIcoMaxWidth = 280f;

        private const float InfoTextX = 626f;
        private const float InfoTextDefaultY = 162.1f;
        private const float InfoTextWidth = 286f;
        private const float InfoTextBottom = 680f;
        private const int InfoTextFontSize = 14;

        private const float EmptyTextX = 19.5f;
        private const float EmptyTextY = 86.2f;
        private const float EmptyTextWidth = 569.45f;

        private const float BottomTextX = 19.5f;
        private const float BottomTextY = 653.1f;
        private const float BottomTextWidth = 872.5f;

        // ── Sub-category tab geometry ─────────────────────────────────────────
        //
        // ATTESTED — see PipSubTabBar, which owns the numbers (SWF sprite 1113 depths 135..155,
        // but1..but5 at y 26.4, x 0/140/280/420/560, symbol 1011 at 138x32). This page aliases them
        // rather than keeping a second copy, so the inventory's row and every other page's row cannot
        // drift apart.

        private const float SubTabY = PipSubTabBar.TabY;
        private const float SubTabX0 = PipSubTabBar.TabX0;
        private const float SubTabPitch = PipSubTabBar.TabPitch;
        private static readonly Vector2 SubTabSize = PipSubTabBar.TabSize;

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
        /// <c>PipPageInv.as:68-73</c> / <c>:180-185</c> / <c>:218-226</c>), the empty-list notice
        /// (<c>emptyinv</c>, <c>:310</c>) and every line of the info pane's stat block
        /// (<c>weapontip</c>, <c>lvl</c>, <c>damage</c>, …, <c>PipPage.as:440-596</c>).
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
        /// <para><b>All three left-click branches have a host now</b>, and the page still does not call
        /// into <c>GameInventory</c> itself: equipping reaches <c>PlayerWeaponLoadout</c>, wearing
        /// reaches <c>UnitStats.EquipArmour</c>, and using reaches <c>PlayerInventory.UseItem</c> — none
        /// of which are inventory mutations the command seam owns, and all of which need the player
        /// rather than the bag. The page hands the row over and reports the answer. Returning
        /// <c>true</c> means "acted"; returning <c>false</c> — or leaving the handler unset — falls
        /// through to the report below, which is the refusal path.</para>
        ///
        /// <para><b>The right-click (drop) branch is still unported</b>, and that is why the relay
        /// routes a ctrl-click to the same hook rather than pretending to have a second verb.</para>
        /// </summary>
        public Func<PipInventoryRow, bool> RowActivated { get; set; }

        /// <summary>
        /// Resolves a weapon id to its definition, for the info pane's image and stat block — the
        /// Oracle's <c>pip.arrWeapon[id]</c> (<c>PipPage.as:423</c>). The bootstrapper wires this to
        /// <c>PlayerInventory.ResolveWeapon</c>. Null, or a miss, leaves the pane name-only.
        /// </summary>
        public Func<string, WeaponDefinition> WeaponDefinitionProvider { get; set; }

        /// <summary>
        /// Resolves an armour id to its live instance — the Oracle's <c>inv.armors[id]</c>
        /// (<c>PipPage.as:600-604</c>). The bootstrapper wires this to <c>GameInventory.GetArmor</c>.
        ///
        /// <para><b>Why an instance and not a definition.</b> The armour stat block reads
        /// <c>a.armor</c>/<c>a.marmor</c>/<c>a.resist[…]</c>/<c>a.radVul</c>, and every one of those
        /// comes out of <c>getXmlParam(this.xml.upd[this.lvl])</c> (<c>Armor.as:186-193</c>) — i.e.
        /// <b>they change with the item's upgrade level</b>. A definition alone cannot answer them: it
        /// carries the whole level table, and the level is per-instance. This is the same join
        /// <c>GameArmorInstance.ToArmourState</c> already performs for the combat path.</para>
        ///
        /// <para>Null, or a miss, means "no such armour" — which the Oracle handles by re-typing the
        /// lookup as <c>L_ITEM</c> (<c>PipPage.as:411-414</c>), so the pane falls through to the item
        /// block rather than going blank.</para>
        /// </summary>
        public Func<string, GameArmorInstance> ArmorInstanceProvider { get; set; }

        /// <summary>
        /// The player's <see cref="CharacterStats"/>, for the info pane's "your level" and the damage
        /// multipliers <c>Weapon.setPers</c> copies off <c>Pers</c> (<c>Weapon.as:963-1032</c>).
        /// Resolved lazily for the same reason the inventory source is — the player is assembled in
        /// <c>PlayerController.Awake</c>. Null degrades the pane to data-only values.
        /// </summary>
        public Func<CharacterStats> PlayerStatsProvider { get; set; }

        /// <summary>
        /// <c>World.w.hardInv</c> — gates the info pane's <c>mass2</c> line
        /// (<c>PipPage.as:588-591</c>) exactly as it gates the list's mass column.
        /// </summary>
        public Func<bool> HardInventoryProvider { get; set; }

        // ── Built objects ─────────────────────────────────────────────────────

        private RectTransform _root;
        private readonly RectTransform[] _rowRects = new RectTransform[PipBuckLayout.PageRowCount];
        private readonly CanvasGroup[] _rowGroups = new CanvasGroup[PipBuckLayout.PageRowCount];
        private readonly Image[] _rowBacks = new Image[PipBuckLayout.PageRowCount];
        private readonly Image[] _rowFrames = new Image[PipBuckLayout.PageRowCount];
        private readonly Image[] _rowIcons = new Image[PipBuckLayout.PageRowCount];

        /// <summary>Flat: cell <c>row * ColumnCount + column</c>. See <see cref="ColumnCount"/>.</summary>
        private readonly Text[] _cells = new Text[PipBuckLayout.PageRowCount * ColumnCount];

        private Text[] _headerCells;
        private readonly Text[] _subTabLabels = new Text[PipInventoryList.SubPageCount];
        private readonly Image[] _subTabBackgrounds = new Image[PipInventoryList.SubPageCount];
        private Text _emptyText;
        private Text _bottomText;

        // The list scrollbar — scBar, shown only when the list overflows (PipPage.as:942-953).
        private RectTransform _listScrollbar;
        private RectTransform _listThumb;

        // The info pane — nazv, ico, info. Built once; filled per hover.
        private Text _infoName;
        private Image _infoIco;
        private Text _infoText;

        // ── State ─────────────────────────────────────────────────────────────

        private int _subPage = PipInventoryList.WeaponsSubPage;
        private int _scroll;

        /// <summary>
        /// The id whose info the pane is showing — the Oracle's <c>overId</c>/<c>assId</c> role.
        /// Cleared on a page or sub-page change (<c>setStatus</c> blanks <c>vis.info</c> and
        /// <c>vis.nazv</c>, <c>PipPage.as:922-923</c>), kept across a scroll step, because the
        /// Oracle's scroll path only re-windows the rows and never touches the pane.
        /// </summary>
        private string _infoId;

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

        /// <summary><c>World.fps</c> (<c>World.as:44</c>) — the constant the APS/DPS lines divide by.</summary>
        private const float OracleFps = 30f;

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
        /// Supplies the composition (the row sprites, the row icons, the scrollbar skins and the
        /// sub-tab sprite) and the font. Called by <c>PipBuckBootstrapper</c> before <see cref="Build"/>.
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
        ///
        /// <para><c>setStatus</c> also blanks the info pane (<c>:922-923</c>), on every call — which
        /// is why re-opening the device closes the pane as well as the list's scroll position.</para>
        /// </summary>
        public void SetStatus(int subPage)
        {
            if (subPage > 0)
            {
                _subPage = Mathf.Clamp(subPage, 1, PipInventoryList.SubPageCount);
                _scroll = 0;
            }

            // PipPage.as:922-923 — the pane is cleared on every setStatus, before setSubPages runs.
            _infoId = null;

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
            BuildListScrollbar();
            BuildInfoPane();
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
            _headerCells = new Text[ColumnCount];

            // The header is a ROW, one pitch above row 0 — PipBuckLayout.PageHeaderPosition, which cites
            // PipPage.as:108-142 (`maxrows + 1` clips, the -1 iteration becoming `statHead`). It is built
            // with the same helper BuildRows uses, and that is the point. PipPage.as:123-127 hides the
            // header's `back`, so no art children are created here — captions only.
            RectTransform header = CreateRowRect("HeaderRow", PipBuckLayout.PageHeaderPosition());

            for (int column = 0; column < ColumnCount; column++)
            {
                _headerCells[column] = CreateCell(header, "Header" + column, column,
                    RowFontSize, PfeUiColors.PageYellow);
            }
        }

        /// <summary>
        /// One of the page's row rects — a list row <b>and</b> the header row, which is a row like any
        /// other. Shared by <see cref="BuildRows"/> and <see cref="BuildHeader"/> so the two cannot drift
        /// apart again: the version of this code that lived only inside <see cref="BuildRows"/> is what
        /// let the header be built somewhere else entirely.
        /// </summary>
        private RectTransform CreateRowRect(string name, Vector2 flash)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = RowHitSize;
            Place(rect, flash.x, flash.y);

            // The row's own hit area. A fully transparent Image still receives raycasts in Unity
            // UI (the alpha test only engages above alphaHitTestMinimumThreshold), so this is what
            // makes the row clickable and hoverable; the visible panel is the `back` child below.
            Image hit = go.GetComponent<Image>();
            hit.color = new Color(1f, 1f, 1f, 0f);
            hit.raycastTarget = true;

            return rect;
        }

        private void BuildRows()
        {
            for (int row = 0; row < PipBuckLayout.PageRowCount; row++)
            {
                RectTransform rect = CreateRowRect("Row" + row, PipBuckLayout.PageRowPosition(row));
                _rowRects[row] = rect;
                _rowGroups[row] = rect.gameObject.AddComponent<CanvasGroup>();

                // The Oracle's row children, in its own depth order: back (d1) under everything, the
                // text fields (d3..d13), trol (d11) and ramka (d14) on top.
                _rowBacks[row] = CreateRowArt(rect, "back", _composition != null ? _composition.inventoryRowBack : null,
                    RowBackSize, RowBackCentre);
                _rowBacks[row].raycastTarget = false;

                var relay = rect.gameObject.AddComponent<RowRelay>();
                relay.Page = this;
                relay.RowIndex = row;

                for (int column = 0; column < ColumnCount; column++)
                {
                    _cells[row * ColumnCount + column] = CreateCell(rect, "Col" + column, column,
                        RowFontSize, PfeUiColors.PageR0);
                }

                _rowIcons[row] = CreateRowArt(rect, "trol", null, RowIconSize, RowIconCentre);
                _rowIcons[row].raycastTarget = false;

                _rowFrames[row] = CreateRowArt(rect, "ramka", _composition != null ? _composition.inventoryRowFrame : null,
                    RowFrameSize, RowFrameCentre);
                _rowFrames[row].raycastTarget = false;
                _rowFrames[row].enabled = false;   // lit per row by RefreshRows for the equipped entry
            }
        }

        /// <summary>
        /// One centre-registered art child of a row. The three row symbols (498, 876, 897) are all
        /// registered at their centre, so the child takes the SWF's placement as its
        /// <c>anchoredPosition</c> directly — with the single y-negation this file owns
        /// (<see cref="PlaceCentre"/>).
        /// </summary>
        private static Image CreateRowArt(RectTransform parent, string name, Sprite sprite,
            Vector2 size, Vector2 centre)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            PlaceCentre(rect, centre.x, centre.y);

            Image image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.color = Color.white;
            return image;
        }

        private Text CreateCell(RectTransform parent, string name, int column,
            int fontSize, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            AnchorTopLeft(rect);
            rect.sizeDelta = new Vector2(ColumnWidth[column], RowTextHeight);

            // y is RowTextY relative to `parent`, and `parent` is always a positioned row rect (see
            // CreateRowRect). There used to be a `rowOrigin` parameter here that was passed a Flash
            // position and never read — which is how the header ended up on the page root's corner with
            // the sub-tabs. A parameter that is never read cannot be kept honest, so it is gone rather
            // than documented.
            rect.anchoredPosition = new Vector2(ColumnX[column], -RowTextY);

            Text text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.color = colour;

            // The DefineEditText `align` attribute: fav / hp / ammo are right-aligned fields.
            text.alignment = ColumnRight[column] ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;

            // A text field in Flash does not render past its own bounds, and the name box (283.5)
            // reaches under the hp field (which ends at 336). Overflow there would draw a long name
            // across the condition column; wrapping is the Flash behaviour for a fixed-width field.
            text.horizontalOverflow = column == 1 ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            text.text = string.Empty;
            return text;
        }

        /// <summary>
        /// The list scrollbar — <c>scBar</c>, a <c>fl.controls.UIScrollBar</c> (symbol 492) the Oracle
        /// places at (600, 100) with scaleY 5.360, i.e. 17x536, just right of the 560-wide row panel.
        ///
        /// <para><b>The show/hide and value rules are the Oracle's</b> (<c>PipPage.as:942-953</c>):
        /// visible only when <c>arr.length &gt; maxrows</c>, <c>minScrollPosition</c> 0,
        /// <c>maxScrollPosition</c> = <c>arr.length - maxrows</c>, <c>scrollPosition</c> = the current
        /// offset. <see cref="RefreshScrollbar"/> applies them; this method only builds the chrome.</para>
        ///
        /// <para><b>The thumb's size and travel are a port choice.</b> The Flash component's internal
        /// thumb arithmetic lives in the player's compiled <c>fl.controls</c> classes, not in the SWF's
        /// tag stream, so there is no number to read. The port uses the obvious fraction — thumb =
        /// visible/total of the track, clamped to the skin's own 54 px, travel = the remainder — which
        /// is what the component visibly does. The <b>position rule that matters</b>, the one the
        /// player reads, is the Oracle's: the thumb sits at scroll/maxScroll.</para>
        /// </summary>
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
            _listThumb.pivot = new Vector2(0.5f, 1f);   // hangs DOWN from its top edge, like Flash's y
            _listThumb.sizeDelta = new Vector2(ListScrollbarWidth, ThumbMinHeight);
            _listThumb.anchoredPosition = Vector2.zero;

            Image thumb = thumbGo.GetComponent<Image>();
            thumb.sprite = _composition != null ? _composition.scrollBarThumb : null;
            thumb.type = Image.Type.Sliced;
            thumb.color = Color.white;
            thumb.raycastTarget = false;
        }

        /// <summary>
        /// The info pane — <c>nazv</c> (the item name), <c>ico</c> (its image) and <c>info</c> (the
        /// stat block), the three children of <c>visPipInv</c> the Oracle fills from
        /// <c>infoItem</c>/<c>infoStr</c>. Built once, filled per hover.
        /// </summary>
        private void BuildInfoPane()
        {
            _infoName = CreatePaneText("nazv", InfoNameX, InfoNameY, InfoNameWidth, InfoNameHeight,
                InfoNameFontSize, TextAnchor.UpperRight);
            // The Oracle's own nazv field is right-aligned: its text hugs the pane's right edge, which
            // is where scText (the pane's scrollbar) sits.

            var icoGo = new GameObject("ico", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _infoIco = icoGo.GetComponent<Image>();
            _infoIco.rectTransform.SetParent(_root, false);
            // Pivot TOP-CENTRE: the Oracle top-aligns infIco inside the ico clip and centres it
            // horizontally (`infIco.x = -rect.left + 140 - width/2; infIco.y = -rect.top`,
            // PipPage.as:1063-1064). The rect is resized to the sprite by RefreshInfoImage.
            _infoIco.rectTransform.anchorMin = _infoIco.rectTransform.anchorMax = new Vector2(0f, 1f);
            _infoIco.rectTransform.pivot = new Vector2(0.5f, 1f);
            _infoIco.rectTransform.sizeDelta = new Vector2(100f, 100f);
            _infoIco.rectTransform.anchoredPosition = new Vector2(InfoIcoX + 140f, -InfoIcoY);
            _infoIco.preserveAspect = true;
            _infoIco.raycastTarget = false;
            _infoIco.enabled = false;

            _infoText = CreatePaneText("info", InfoTextX, InfoTextDefaultY, InfoTextWidth,
                InfoTextBottom - InfoTextDefaultY, InfoTextFontSize, TextAnchor.UpperLeft);
            _infoText.supportRichText = true;   // the stat block arrives with <color> spans
        }

        /// <summary>A pane text field with its attested top-left and width.</summary>
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
            _bottomText.supportRichText = true;   // the caps total arrives as a <color> tag
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        /// <summary>
        /// Re-reads the source and repaints everything — the port of <c>setSubPages</c> plus the row
        /// loop in <c>setStatItems</c>, plus the scrollbar rule and the info pane's re-render.
        /// </summary>
        public void Rebuild()
        {
            if (_root == null) return;

            bool showHidden = _viewModel != null && _viewModel.ShowHidden.Value;

            IReadOnlyList<PipInventoryEntry> entries = _source != null ? _source.Read() : null;
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
            RefreshScrollbar(rows.Count, maxScroll);
            RefreshEmptyText(rows.Count == 0);
            RefreshBottomText();
            RefreshInfoPane(rows);
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

                // PipPageInv.as:411-414 — `item.alpha = 0.4` for a stored weapon (respect == 1). The
                // dimming covers the whole clip, panel and all, which is what a CanvasGroup gives.
                if (_rowGroups[row] != null)
                {
                    _rowGroups[row].alpha = entry.Hidden ? 0.4f : 1f;
                }

                // PipPageInv.as:383 — `item.trol.gotoAndStop(obj.trol)`, with the catch falling back
                // to frame 1 (the empty art) when the label does not exist — e.g. "armor2", which the
                // clip has no frame for. InventoryRowIconIndexFor returns 0 on a miss, which IS
                // frame 1, so the fallback is the same branch.
                if (_rowIcons[row] != null)
                {
                    int iconIndex = PipBuckCompositionDefinition.InventoryRowIconIndexFor(entry.Category);
                    Sprite[] icons = _composition != null ? _composition.inventoryRowIcons : null;
                    _rowIcons[row].sprite = icons != null && iconIndex < icons.Length
                        ? icons[iconIndex]
                        : null;
                }

                // PipPageInv.as:391 / :430-444 — `ramka.visible` marks the equipped weapon or armour.
                if (_rowFrames[row] != null)
                {
                    _rowFrames[row].enabled = entry.Equipped;
                }
            }
        }

        /// <summary>
        /// The Oracle's scrollbar rule (<c>PipPage.as:942-953</c>), applied to the chrome
        /// <see cref="BuildListScrollbar"/> built.
        /// </summary>
        private void RefreshScrollbar(int rowCount, int maxScroll)
        {
            if (_listScrollbar == null) return;

            bool visible = rowCount > PipBuckLayout.PageRowCount;
            if (_listScrollbar.gameObject.activeSelf != visible)
            {
                _listScrollbar.gameObject.SetActive(visible);
            }

            if (!visible || _listThumb == null) return;

            // Thumb size: the visible fraction of the list, at least the skin's own height.
            float fraction = Mathf.Clamp01((float)PipBuckLayout.PageRowCount / rowCount);
            float thumbHeight = Mathf.Max(ThumbMinHeight, ListScrollbarHeight * fraction);
            _listThumb.sizeDelta = new Vector2(ListScrollbarWidth, thumbHeight);

            // Thumb position: scroll / maxScroll of the remaining travel. maxScroll is > 0 here,
            // because a list of 19+ rows is the only way the bar is visible.
            float travel = ListScrollbarHeight - thumbHeight;
            float top = maxScroll > 0 ? _scroll / (float)maxScroll * travel : 0f;
            _listThumb.anchoredPosition = new Vector2(0f, -top);
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

        // ── The info pane ─────────────────────────────────────────────────────

        /// <summary>
        /// Re-renders the pane for <see cref="_infoId"/>, or clears it when that row has gone.
        ///
        /// <para><b>Why the pane survives a scroll.</b> The Oracle's scroll path re-windows the rows
        /// and never touches the pane; only <c>setStatus</c> blanks it (<c>PipPage.as:922-923</c>).
        /// The one port addition is the "row has gone" clear: a pane describing a weapon the player no
        /// longer holds would be a lie the Oracle cannot produce, because its <c>infoItem</c> runs on
        /// the hover event and its data cannot vanish underneath a mouse.</para>
        /// </summary>
        private void RefreshInfoPane(List<PipInventoryRow> rows)
        {
            if (_infoText == null) return;

            if (string.IsNullOrEmpty(_infoId))
            {
                ClearInfoPane();
                return;
            }

            PipInventoryRow? shown = null;
            for (int i = 0; i < rows.Count; i++)
            {
                if (string.Equals(rows[i].Id, _infoId, StringComparison.Ordinal))
                {
                    shown = rows[i];
                    break;
                }
            }

            if (shown == null)
            {
                ClearInfoPane();
                return;
            }

            ShowInfo(shown.Value);
        }

        /// <summary>PipPage.as:922-923 — <c>vis.info.text = ""; vis.nazv.text = ""</c>.</summary>
        private void ClearInfoPane()
        {
            if (_infoName != null) _infoName.text = string.Empty;
            if (_infoText != null) _infoText.text = string.Empty;
            if (_infoIco != null) _infoIco.enabled = false;
        }

        /// <summary>
        /// The port of <c>PipPageInv.statInfo</c>'s pane half (<c>PipPageInv.as:496-530</c> →
        /// <c>PipPage.infoItem</c>): the hovered row's name, image and stat block.
        ///
        /// <para><b>The branch is chosen by the PAGE, not by the row.</b> <c>statInfo</c> passes a
        /// literal <c>tip</c> per <c>page2</c> value — <c>1</c> → <c>L_WEAPON</c>, <c>2</c> →
        /// <c>L_ARMOR</c>, <c>3</c>/<c>4</c> → <c>L_ITEM</c>, <c>5</c> → <c>L_AMMO</c>
        /// (<c>PipPageInv.as:499-520</c>) — so <c>_subPage</c> is the selector, and the same id on two
        /// pages can legitimately produce two different blocks.</para>
        ///
        /// <para><b>One named gap, at the icon.</b> <c>infoItem</c> also draws art: for armour the
        /// <b>paper-doll</b> (<c>setIco(2, …)</c> → <c>pip.setArmor(id)</c> plus the character preview
        /// clip, <c>PipPage.as:1073-1081</c>), and for an item the <b>frame named by its id</b> in the
        /// <c>vis.item</c> timeline (<c>setIco(3, …)</c> → <c>gotoAndStop(id)</c>,
        /// <c>PipPage.as:1082-1090</c>). Neither rig is ported, so this pane draws <b>text only</b> for
        /// those two branches and leaves <c>_infoIco</c> off — which is also the shape the Oracle takes
        /// when the clip is missing. <c>ItemDefinition.icon</c> exists but is written by no importer and
        /// read by no consumer, so it is not a substitute for the timeline frame.</para>
        /// </summary>
        private void ShowInfo(PipInventoryRow row)
        {
            if (_infoName == null || _infoText == null) return;

            _infoName.text = row.Name;

            CharacterStats stats = PlayerStatsProvider != null ? PlayerStatsProvider() : null;

            if (_subPage == PipInventoryList.AmmoSubPage)
            {
                ShowInfoWithoutIcon(BuildAmmoInfoText(row));
                return;
            }

            if (_subPage == PipInventoryList.WeaponsSubPage)
            {
                WeaponDefinition definition = WeaponDefinitionProvider != null
                    ? WeaponDefinitionProvider(row.Id)
                    : null;

                if (definition == null)
                {
                    // PipPage.as:423-427 — `w = pip.arrWeapon[id]; if (w == null) return ""`.
                    ShowInfoWithoutIcon(string.Empty);
                    return;
                }

                RefreshInfoImage(definition);
                _infoText.text = BuildWeaponInfoText(row, definition, stats);
                return;
            }

            if (_subPage == PipInventoryList.ArmorSubPage)
            {
                GameArmorInstance armour = ArmorInstanceProvider != null
                    ? ArmorInstanceProvider(row.Id)
                    : null;

                if (armour == null)
                {
                    // PipPage.as:411-414 — `tip == L_ARMOR && inv.armors[id] == null &&
                    // pip.arrArmor[id] == null` re-types the lookup as L_ITEM before infoStr sees it.
                    ShowInfoWithoutIcon(BuildItemInfoText(row, stats));
                    return;
                }

                ShowInfoWithoutIcon(BuildArmorInfoText(row, armour));
                return;
            }

            // Aid (3) and Misc (4): L_ITEM.
            ShowInfoWithoutIcon(BuildItemInfoText(row, stats));
        }

        /// <summary>
        /// <c>PipPage.as:1013-1019</c> — <c>setIco()</c> with no arguments: drop the icon and put
        /// <c>info</c> back at its own <c>y</c>, then set the block.
        /// </summary>
        private void ShowInfoWithoutIcon(string text)
        {
            _infoIco.enabled = false;
            MoveInfoText(InfoTextDefaultY);
            _infoText.text = text;
        }

        /// <summary>
        /// The ammo branch of <c>PipPage.infoStr</c> (<c>PipPage.as:699-747</c>).
        ///
        /// <para><b>Reads the raw <c>&lt;item&gt;</c> row, not an imported asset.</b> The Oracle reads
        /// <c>inv.items[id].xml</c> — the AllData element itself — and the port's <c>ItemDefinition</c>
        /// does not carry <c>@damage</c>/<c>@pier</c>/<c>@armor</c>/<c>@prec</c>/<c>@det</c>/<c>@sell</c>
        /// at all (its <c>ammoVariant</c> is a different, older schema). So this follows the same
        /// pattern the weapon branch already uses for <c>WeaponXmlExtras</c>: read the row from
        /// <c>Resources/Loot/AllData</c> at runtime.</para>
        /// </summary>
        private string BuildAmmoInfoText(PipInventoryRow row)
        {
            string attrs = ItemRowAttrs(row.Id);
            var s = new StringBuilder();

            // PipPage.as:701-717 — the body. `AllData.d.weapon.(@id == id).length()` first: an id that
            // is also a weapon shows the WEAPON description. Otherwise a row with @base shows the base
            // item's description plus the ammo-mod blurb; otherwise the item's own.
            if (AllDataText != null && WeaponXmlBlocks.TryGetBlock(AllDataText, row.Id, out _))
            {
                s.Append(ResTxt("w", row.Id, 1));
            }
            else
            {
                string baseId = Attr(attrs, "base");
                if (!string.IsNullOrEmpty(baseId))
                {
                    s.Append(ResTxt("i", baseId, 1));

                    // PipPage.as:708-712 — `if(ammo.@mod > 0)` is a NUMBER comparison and the key is
                    // `"ammomod_" + ammo.@mod`, i.e. the attribute's own text. Reading it with AttrI was
                    // both an `@attr > 0` test (which AttrI's own doc forbids — a fractional `mod` would
                    // truncate to 0 and drop the line) and a re-rendering of the text, where the Oracle
                    // keys "ammomod_1.0" for `mod='1.0'` and AttrI would key "ammomod_1". Output is
                    // identical on all 26 shipped `mod=` values, which are integral 1..9; the change is
                    // to stop relying on that.
                    if (AttrF(attrs, "mod", 0f) > 0f)
                    {
                        s.Append("\n\n").Append(ResTxt("p", "ammomod_" + Attr(attrs, "mod"), 1));
                    }
                }
                else
                {
                    s.Append(ResTxt("i", row.Id, 1));
                }
            }

            s.Append('\n');

            // PipPage.as:719-734 — the modifier lines. AS3 tests @x.length(), i.e. PRESENCE, so
            // damage='0' still prints "x0"; AttrPresent is that test, AttrBool (== '1') is not.
            if (AttrPresent(attrs, "damage"))
                s.Append('\n').Append(Pip("damage")).Append(": x").Append(Yel(Attr(attrs, "damage")));
            if (AttrPresent(attrs, "pier"))
                s.Append('\n').Append(Pip("pier")).Append(": ").Append(Yel(Attr(attrs, "pier")));
            if (AttrPresent(attrs, "armor"))
                s.Append('\n').Append(Pip("tarmor")).Append(": x").Append(Yel(Attr(attrs, "armor")));
            if (AttrPresent(attrs, "prec"))
                s.Append('\n').Append(Pip("prec")).Append(": x").Append(Yel(Attr(attrs, "prec")));
            if (AttrF(attrs, "det", 0f) > 0f)
                s.Append('\n').Append(Pip("det"));

            // PipPage.as:739-742 — hard-mode mass. The parenthesised vault token is
            // `inv.items[id].invCat`, which is DERIVED from this same row rather than imported — see
            // InvCat. This line used to omit the token as a named gap; it is now the Oracle's line.
            if (HardInventoryProvider != null && HardInventoryProvider() && AttrF(attrs, "m", 0f) > 0f)
            {
                s.Append("\n\n").Append(Pip("mass")).Append(": ").Append(MassSpan(Attr(attrs, "m")))
                    .Append(" (").Append(Pip("vault" + InvCat(attrs, Attr(attrs, "tip")))).Append(')');
            }

            if (AttrF(attrs, "sell", 0f) > 0f)
            {
                s.Append('\n').Append(Pip("sell")).Append(": ").Append(Yel(Attr(attrs, "sell")));
            }

            return s.ToString();
        }

        // ── Armour branch (PipPage.as:598-698) ───────────────────────────────────────────────────────

        /// <summary>
        /// The armour branch of <c>PipPage.infoStr</c> (<c>PipPage.as:598-698</c>).
        ///
        /// <para><b>Every value comes off the item's upgrade level.</b> The Oracle reads
        /// <c>a.armor</c>, <c>a.marmor</c>, <c>a.resist[…]</c>, <c>a.radVul</c>, <c>a.dexter</c> and
        /// <c>a.sneak</c> — all of which <c>Armor.getXmlParam(this.xml.upd[this.lvl])</c> wrote
        /// (<c>Armor.as:186-193</c>). <c>EquipmentAtLevel(armour.Level)</c> is that same read, and it
        /// is why this builder takes an <b>instance</b> and not a definition: the definition holds the
        /// whole level table, the level is per-instance.</para>
        ///
        /// <para><b>Three fields come off the item instead, and that is not a mistake.</b>
        /// <c>meleeMult</c>/<c>gunsMult</c>/<c>magicMult</c> are read in the <c>Armor</c> constructor
        /// (<c>Armor.as:155-168</c>) from the <c>&lt;armor&gt;</c> <i>root</i> — <b>before</b>
        /// <c>getXmlParam</c> is ever called — so they do not vary by level. They live on
        /// <see cref="ItemDefinition"/> for exactly that reason, one copy each, and
        /// <c>ArmourDataParser</c> reads them off the root. <c>crit</c> is in the same group.</para>
        ///
        /// <para><b>Rounding.</b> The Oracle uses <c>Math.round</c>, which is <c>floor(x + 0.5)</c> —
        /// halfway rounds toward <b>+Infinity</b>, not to even. See <see cref="As3Round"/>. The
        /// <c>armor</c>/<c>marmor</c> lines round an already-integral value: every <c>&lt;upd armor&gt;</c>
        /// and <c>&lt;upd marmor&gt;</c> in the shipped data is a whole number (measured 2026-10-14), so
        /// the port's <c>int</c> fields are exact there and the round is the identity.</para>
        /// </summary>
        private string BuildArmorInfoText(PipInventoryRow row, GameArmorInstance armour)
        {
            ItemDefinition def = armour.Definition;
            EquipmentData equipment = def != null ? def.EquipmentAtLevel(armour.Level) : default;

            var s = new StringBuilder();

            // PipPage.as:605-608 — `armor_qual`, which the port stores as `reliability` (see its doc).
            if (equipment.reliability > 0f)
                s.Append(Pip("aqual")).Append(": ").Append(Yel(As3Round(equipment.reliability * 100f) + "%"));

            // PipPage.as:609-612. `armor` is the physical rating, `marmor` the magic one.
            if (equipment.armor > 0)
                s.Append('\n').Append(Pip("armor")).Append(": ")
                    .Append(Yel(equipment.armor.ToString(CultureInfo.InvariantCulture)));
            if (equipment.magicArmor > 0)
                s.Append('\n').Append(Pip("marmor")).Append(": ")
                    .Append(Yel(equipment.magicArmor.ToString(CultureInfo.InvariantCulture)));

            // PipPage.as:613-624 — `!= 0`, not `> 0`: metal plate declares `dexter='-0.3'` and a
            // stealth suit `sneak='-0.2'`, and both are bonuses that must print with their sign.
            if (equipment.dexterity != 0f)
                s.Append('\n').Append(Pip("dexter")).Append(": ")
                    .Append(Yel(As3Round(equipment.dexterity * 100f) + "%"));
            if (equipment.sneak != 0f)
                s.Append('\n').Append(Pip("sneak")).Append(": ")
                    .Append(Yel(As3Round(equipment.sneak * 100f) + "%"));

            // PipPage.as:625-640 — the damage multipliers, gated on `!= 1` (neutral), and printed as
            // the DELTA from 1 with an explicit "+". Note the Oracle prepends the "+" outside `yel`,
            // so the sign is unstyled: "+<yel>16%</yel>".
            float meleeMult = def != null ? def.armorMeleeMult : 1f;
            if (meleeMult != 1f)
                s.Append('\n').Append(Pip("meleedamage")).Append(": +")
                    .Append(Yel(As3Round((meleeMult - 1f) * 100f) + "%"));

            float gunsMult = def != null ? def.armorGunsMult : 1f;
            if (gunsMult != 1f)
                s.Append('\n').Append(Pip("gunsdamage")).Append(": +")
                    .Append(Yel(As3Round((gunsMult - 1f) * 100f) + "%"));

            float magicMult = def != null ? def.armorMagicMult : 1f;
            if (magicMult != 1f)
                s.Append('\n').Append(Pip("spelldamage")).Append(": +")
                    .Append(Yel(As3Round((magicMult - 1f) * 100f) + "%"));

            float crit = def != null ? def.armorCrit : 0f;
            if (crit != 0f)
                s.Append('\n').Append(Pip("critch")).Append(": +")
                    .Append(Yel(As3Round(crit * 100f) + "%"));

            // PipPage.as:641-644 — radiation. The gate is `radVul < 1`, and the port's neutral IS 1
            // (Armor.as:36), so an armour that never declared `radx` prints no line. The value shown is
            // `radx` back: `1 - radVul`. Measured 2026-10-14: 6 of the 55 `<upd>` elements carry `radx`.
            if (equipment.radVul < 1f)
                s.Append('\n').Append(Pip("radx")).Append(": ")
                    .Append(Yel(As3Round((1f - equipment.radVul) * 100f) + "%"));

            // PipPage.as:645-692 — the thirteen resistances, in the Oracle's own order and with its own
            // table keys. The order is NOT alphabetical and NOT the enum's: the Oracle prints
            // BUL, EXPL, PHIS, BLADE, FANG, FIRE, LASER, PLASMA, SPARK, CRIO, VENOM, ACID, NECRO, and
            // the `crio` key is spelled without the `y` the port's field uses.
            AppendResist(s, equipment.resists.bullet, "bullet");
            AppendResist(s, equipment.resists.explosive, "expl");
            AppendResist(s, equipment.resists.physical, "phis");
            AppendResist(s, equipment.resists.blade, "blade");
            AppendResist(s, equipment.resists.fang, "fang");
            AppendResist(s, equipment.resists.fire, "fire");
            AppendResist(s, equipment.resists.laser, "laser");
            AppendResist(s, equipment.resists.plasma, "plasma");
            AppendResist(s, equipment.resists.spark, "spark");
            AppendResist(s, equipment.resists.cryo, "crio");
            AppendResist(s, equipment.resists.venom, "venom");
            AppendResist(s, equipment.resists.acid, "acid");
            AppendResist(s, equipment.resists.necrotic, "necro");

            // PipPage.as:693 — `s += "\n\n" + Res.txt("a", id, 1)`. razd 1: the armour's prose. No
            // trailing newline here (the item branch is the one that ends on one).
            s.Append("\n\n").Append(ResTxt("a", row.Id, 1));

            return s.ToString();
        }

        /// <summary>
        /// One resistance line — <c>PipPage.as:645-692</c>, repeated thirteen times in the Oracle with
        /// only the index and the table key changing. Gated on <c>!= 0</c>, because a negative resist is
        /// a real vulnerability: <c>metal</c> carries <c>spark='-0.3'</c>.
        /// </summary>
        private void AppendResist(StringBuilder s, float value, string pipKey)
        {
            if (value == 0f) return;

            s.Append('\n').Append(Pip(pipKey)).Append(": ")
                .Append(Yel(As3Round(value * 100f) + "%"));
        }

        // ── Item branch (PipPage.as:748-867) ─────────────────────────────────────────────────────────

        /// <summary>
        /// The item branch of <c>PipPage.infoStr</c> (<c>PipPage.as:748-867</c>) — the <c>else</c> arm,
        /// reached by the Aid (3) and Misc (4) pages, and by an armour id with no instance.
        ///
        /// <para><b>Reads the raw <c>&lt;item&gt;</c> row, not an imported asset.</b> The Oracle reads
        /// <c>inv.items[id].xml</c> — the AllData element itself — and the port's
        /// <see cref="ItemDefinition"/> carries none of <c>@hhp</c>/<c>@hmana</c>/<c>@effect</c>/
        /// <c>@perk</c>/<c>@pet_info</c>. So, exactly like the ammo branch, this reads the row from
        /// <c>Resources/Loot/AllData</c> through <see cref="ItemRowAttrs"/>.</para>
        ///
        /// <para><b>The Oracle's <c>tip</c> is re-read from the row.</b> <c>infoStr</c>'s parameter is
        /// <c>L_ITEM</c>, and the first thing the item arm does is <c>tip = pot.@tip</c>
        /// (<c>PipPage.as:752</c>) — so the sub-blocks below are selected by the item's <b>own</b>
        /// <c>@tip</c> (med, food, pot, him, book, spell, paint, instr, impl, art), not by the page.</para>
        ///
        /// <para><b>Named gaps, at the line that would carry them.</b> <c>@pet_info</c> names a
        /// <c>gg.pets</c> entry and the port has no pet model at all (<c>PipPage.as:835-848</c>), so the
        /// block is skipped — which is what the Oracle itself does when the lookup misses. Nothing else
        /// is dropped.</para>
        /// </summary>
        private string BuildItemInfoText(PipInventoryRow row, CharacterStats stats)
        {
            string attrs = ItemRowAttrs(row.Id);
            string tip = Attr(attrs, "tip");

            // PipPage.as:750-752 — `s = Res.txt("i",id,1) + "\n"; pot = inv.items[id].xml; tip = pot.@tip`.
            var s = new StringBuilder();
            s.Append(ResTxt("i", row.Id, 1)).Append('\n');

            // PipPage.as:753-756 — instruction / implant / art replace the WHOLE body with effStr.
            if (tip == "instr" || tip == "impl" || tip == "art")
            {
                s.Clear();
                s.Append(EffStr("item", row.Id, stats)).Append('\n');
            }

            // PipPage.as:757-806 — the healing block. `healMult` (0.4) scales hhp/hhplong/hrad; the
            // rest are unscaled except hmana, which has its own `healManaMult` (1). See CharacterStats
            // for why those are two fields and not one.
            if (tip == "med" || tip == "food" || tip == "pot" || tip == "him")
            {
                float healMult = stats != null ? stats.healMult : 0.4f;          // Pers.as:321
                float healManaMult = stats != null ? stats.healManaMult : 1f;    // Pers.as:321

                bool hasHhp = AttrPresent(attrs, "hhp");
                bool hasHhpLong = AttrPresent(attrs, "hhplong");

                if (hasHhp || hasHhpLong)
                {
                    // **The Oracle's own NaN, reproduced.** The gate admits `hhplong` alone, and then
                    // reads `@hhp` unconditionally: an absent attribute is an empty XMLList, which
                    // coerces to NaN, so `Math.round(NaN * healMult)` is NaN. Exactly one item in
                    // AllData hits this — `sars` (hhplong, no hhp) — and the Oracle prints
                    // "healhp: NaN" for it. Zeroed instead it would read "healhp: 0", i.e. "this item
                    // heals nothing", which is a different and wrong statement. Faithful, not improved.
                    string hp = hasHhp ? As3Round(AttrF(attrs, "hhp", 0f) * healMult) : "NaN";
                    s.Append('\n').Append(Pip("healhp")).Append(": ").Append(Yel(hp));
                }
                if (hasHhpLong)
                    s.Append('+').Append(Yel(As3Round(AttrF(attrs, "hhplong", 0f) * healMult)));

                if (AttrPresent(attrs, "hrad"))
                    s.Append('\n').Append(Pip("healrad")).Append(": ")
                        .Append(Yel(As3Round(AttrF(attrs, "hrad", 0f) * healMult)));
                if (AttrPresent(attrs, "hcut"))
                    s.Append('\n').Append(Pip("healcut")).Append(": ")
                        .Append(Yel(As3Round(AttrF(attrs, "hcut", 0f))));
                if (AttrPresent(attrs, "hpoison"))
                    s.Append('\n').Append(Pip("healpoison")).Append(": ")
                        .Append(Yel(As3Round(AttrF(attrs, "hpoison", 0f))));
                if (AttrPresent(attrs, "horgan"))
                    s.Append('\n').Append(Pip("healorgan")).Append(": ")
                        .Append(Yel(As3Round(AttrF(attrs, "horgan", 0f))));
                if (AttrPresent(attrs, "horgans"))
                    s.Append('\n').Append(Pip("healorgans")).Append(": ")
                        .Append(Yel(As3Round(AttrF(attrs, "horgans", 0f))));
                if (AttrPresent(attrs, "hblood"))
                    s.Append('\n').Append(Pip("healblood")).Append(": ")
                        .Append(Yel(As3Round(AttrF(attrs, "hblood", 0f))));
                if (AttrPresent(attrs, "hmana"))
                    s.Append('\n').Append(Pip("healmana")).Append(": ")
                        .Append(Yel(As3Round(AttrF(attrs, "hmana", 0f) * healManaMult)));
                if (AttrPresent(attrs, "alc"))
                    s.Append('\n').Append(Pip("alcohol")).Append(": ")
                        .Append(Yel(As3Round(AttrF(attrs, "alc", 0f))));
                if (AttrPresent(attrs, "rad"))
                    s.Append('\n').Append(Pip("rad")).Append(": ")
                        .Append(Yel(As3Round(AttrF(attrs, "rad", 0f))));

                // PipPage.as:798-801 — `@effect` names an `<eff>` row, and the line's own label is the
                // `refeff` key. The body is effStr's, so it arrives already coloured.
                if (AttrPresent(attrs, "effect"))
                    s.Append('\n').Append(Pip("refeff")).Append(": ")
                        .Append(EffStr("eff", Attr(attrs, "effect"), stats));

                // PipPage.as:802-805 — the perk line. `Res.txt("e", @perk)` is razd 0, i.e. the perk's
                // NAME; the rank is `pers.perks[perk]`, printed as "0" when absent or non-positive.
                if (AttrPresent(attrs, "perk"))
                {
                    string perk = Attr(attrs, "perk");
                    int rank = stats != null ? stats.GetPerkRank(perk) : 0;
                    s.Append('\n').Append(Pink(ResTxt("e", perk))).Append(": ")
                        .Append(Pip("level")).Append(' ').Append(rank > 0 ? rank.ToString() : "0");
                }

                // PipPage.as:806-809 — OUTSIDE the `@perk` test, exactly as in the Oracle: an item with
                // `@maxperk` but no `@perk` still appends the bare "/N".
                if (AttrPresent(attrs, "maxperk"))
                    s.Append('/').Append(Attr(attrs, "maxperk"));
            }

            // PipPage.as:810-815 — a book the player already has the skill for. `pers.skills[id] != null`
            // is "a skill with this id exists", which the port answers with the level table's keys.
            if (tip == "book" && stats != null && stats.SkillLevels.ContainsKey(row.Id))
                s.Append('\n').Append(Pip("skillup")).Append(": ").Append(Pink(ResTxt("e", row.Id)));

            // PipPage.as:816-821 — spells. `yel(pot.@mana)` and `yel(pot.@culd + guiText("sec"))` print
            // the RAW attributes (no rounding on the first, string-concatenated units on the second),
            // which is why the port appends the attribute text rather than a parsed number.
            if (tip == "spell")
            {
                float manaMult = stats != null ? stats.allDManaMult : 1f;
                float spellDown = stats != null ? stats.spellDown : 1f;
                float mana = AttrF(attrs, "mana", 0f);
                float culd = AttrF(attrs, "culd", 0f);
                string sec = ResTxt("g", "sec");

                s.Append('\n').Append(Pip("dmana2")).Append(": ").Append(Yel(Attr(attrs, "mana")))
                    .Append(" (").Append(Yel(As3Round(mana * manaMult))).Append(')');

                s.Append('\n').Append(Pip("culd")).Append(": ").Append(Yel(Attr(attrs, "culd") + sec))
                    .Append(" (").Append(Yel(As3Round(culd * spellDown) + sec)).Append(')');

                // PipPage.as:820 — `@tele > 0` picks the teleport description over the generic one.
                bool tele = AttrF(attrs, "tele", 0f) > 0f;
                s.Append('\n').Append(Pip("is1")).Append(": ")
                    .Append(Pink(ResTxt("e", tele ? "tele" : "magic")));
            }

            // PipPage.as:822-831 — the one hardcoded id in the branch: `rep`, the repair kit. Its
            // "effect" is `@hp * repairMult` rather than any heal attribute, and it prints only when
            // positive.
            if (row.Id == "rep")
            {
                float repairMult = stats != null ? stats.repairMult : 1f;
                float hhp = AttrPresent(attrs, "hp") ? AttrF(attrs, "hp", 0f) * repairMult : 0f;
                if (hhp > 0f)
                    s.Append('\n').Append(Pip("effect")).Append(": ").Append(Yel(As3Round(hhp)));
            }

            // PipPage.as:835-848 — `@pet_info` → `gg.pets[@pet_info]`. NAMED GAP: the port has no pet
            // model, so the lookup can never hit and the block prints nothing — which is also the
            // Oracle's behaviour on a miss. Three items carry the attribute (`sp_moon`, `whistle`,
            // `owl`); their hp/skin/allresist/damage lines are absent rather than invented.

            // PipPage.as:849-852 — a paint can's whole body is its `paint` table entry.
            if (tip == "paint")
                s = new StringBuilder(ResTxt("p", "paint", 1));

            // PipPage.as:853-856 — hard-mode mass, with the vault token. `inv.items[id].invCat` is a
            // DERIVED field, not an XML attribute: Item.as:268-294 computes it from `@invcat`, `@us`
            // and the item's own tip, in that precedence. See InvCat.
            if (HardInventoryProvider != null && HardInventoryProvider() && AttrF(attrs, "m", 0f) > 0f)
            {
                s.Append("\n\n").Append(Pip("mass")).Append(": ").Append(MassSpan(Attr(attrs, "m")))
                    .Append(" (").Append(Pip("vault" + InvCat(attrs, tip))).Append(')');
            }

            // PipPage.as:857-860.
            if (AttrF(attrs, "sell", 0f) > 0f)
                s.Append('\n').Append(Pip("sell")).Append(": ").Append(Yel(Attr(attrs, "sell")));

            return s.ToString();
        }

        /// <summary>
        /// The item's <c>invCat</c> — <b>computed, not read</b>. AS3's <c>Item.invCat</c> is a field
        /// (<c>Item.as:83</c>, default <b>3</b>) that its constructor overwrites from three sources in
        /// a fixed order (<c>Item.as:268-294</c>):
        /// <list type="number">
        /// <item><description>ammo and explosives → <b>2</b> (<c>Item.as:272-276</c>);</description></item>
        /// <item><description>anything usable (<c>@us &gt; 0</c>) that is not food, "eda" or a book →
        /// <b>1</b> (<c>Item.as:278-281</c>);</description></item>
        /// <item><description>an explicit <c>@invcat</c> overrides both (<c>Item.as:289-293</c>).</description></item>
        /// </list>
        ///
        /// <para><b>Why this is derivable here at all.</b> Every input is an attribute of the very
        /// <c>&lt;item&gt;</c> row the pane already holds, so no importer change is needed — the
        /// previous revision of this pane recorded "invCat is not imported" as a gap and dropped the
        /// token; it is computed instead. The token is real: <c>vault1</c>, <c>vault2</c> and
        /// <c>vault3</c> all exist in the text table, and the shipped data's only <c>@invcat</c> value
        /// is <c>3</c> (on 10 rows).</para>
        ///
        /// <para>The precedence is the Oracle's and is order-sensitive: an ammo item with
        /// <c>@us</c> would be 2, then 1, then whatever <c>@invcat</c> says.</para>
        /// </summary>
        private static int InvCat(string attrs, string tip)
        {
            int invCat = 3;                                                          // Item.as:83

            if (tip == "a" || tip == "e") invCat = 2;                                // Item.as:272-276
            if (AttrF(attrs, "us", 0f) > 0f && tip != "food" && tip != "eda" && tip != "book")
                invCat = 1;                                                          // Item.as:278-281
            if (AttrPresent(attrs, "invcat")) invCat = AttrI(attrs, "invcat", invCat); // Item.as:289-293

            return invCat;
        }

        /// <summary>
        /// The port of <c>PipPage.setIco(1, id)</c>'s image half (<c>PipPage.as:1020-1070</c>): the
        /// weapon's held art, top-aligned in the ico clip and horizontally centred, with <c>info</c>
        /// pushed below it by 10 px.
        /// </summary>
        private void RefreshInfoImage(WeaponDefinition definition)
        {
            Sprite sprite = definition.weaponVisual != null ? definition.weaponVisual.IdleSprite : null;

            if (sprite == null)
            {
                // PipPage.as:1043-1046 — `if (vWeapon == null)` falls through with no icon added, and
                // info keeps the y it had. The pane degrades to text-only, which is the same shape.
                _infoIco.enabled = false;
                MoveInfoText(InfoTextDefaultY);
                return;
            }

            // The Oracle draws infIco at its natural size times vis.@icomult; the port clamps the
            // width so a 1:1 sprite cannot run off the pane (see InfoIcoMaxWidth).
            float scale = Mathf.Min(1f, InfoIcoMaxWidth / Mathf.Max(1f, sprite.rect.width));
            float width = sprite.rect.width * scale;
            float height = sprite.rect.height * scale;

            _infoIco.enabled = true;
            _infoIco.sprite = sprite;
            _infoIco.rectTransform.sizeDelta = new Vector2(width, height);

            // PipPage.as:1066 — `vis.info.y = vis.ico.y + vis.ico.height + 10`.
            MoveInfoText(InfoIcoY + height + 10f);
        }

        private void MoveInfoText(float flashY)
        {
            if (_infoText == null) return;

            // PipPage.as:1238 — `vis.info.height = 680 - this.vis.info.y`: the block always runs to
            // the page's bottom edge, whatever moved its top.
            _infoText.rectTransform.anchoredPosition = new Vector2(InfoTextX, -flashY);
            _infoText.rectTransform.sizeDelta = new Vector2(InfoTextWidth, InfoTextBottom - flashY);
        }

        /// <summary>
        /// The weapon branch of <c>PipPage.infoStr</c> (<c>PipPage.as:421-597</c>), line for line
        /// against the port's data model. Every line whose Oracle input the port does not carry is
        /// named at its call site rather than silently dropped.
        /// </summary>
        private string BuildWeaponInfoText(PipInventoryRow row, WeaponDefinition def, CharacterStats stats)
        {
            // Weapon.as:963-992 (setPers) — the values Pers copies onto the weapon, which the stat
            // block reads back. `stats == null` is a unit with no Pers: every multiplier is its
            // AS3 declaration default.
            int skill = Mathf.Clamp(def.skillLevel, 1, 7);          // w.skill (== AllData @skill)
            int lvl = def.weaponLevel;                              // w.lvl   (== AllData @lvl)
            int tip = (int)def.weaponType;                          // w.tip: 1 melee .. 5 magic

            // **The weapon-skill members are explicit interface implementations** (CharacterStats
            // declares `float IWeaponStatSource.ReloadMult => …`), so they are read through the
            // interface, not off the class. The local holds the reference so the three reads below
            // share one conversion and one null test.
            IWeaponStatSource pers = stats;
            int tier = pers != null ? pers.OwnerWeaponSkillLevel(skill) : 0;
            float skillMult = pers != null ? pers.WeaponSkillMultiplier(skill) : 1f;

            // PipPage.as:429-439 — the skill gate. tier == 100 is Pers.getWeapLevel's fail-open
            // sentinel for a skill code it does not recognise (Pers.as:1073-1104); the Oracle then
            // computes razn = lvl - 100 < 0 and skillPlusDam would explode, because the sentinel is
            // only ever reached for a code no weapon carries. The port clamps it: fail-open, without
            // the arithmetic the sentinel was never meant to feed.
            bool failOpen = tier >= 100;
            int razn = failOpen ? 0 : lvl - tier;
            float skillConf = razn == 1 ? 0.75f : razn >= 2 ? 0.5f : 1f;
            float skillPlusDam = razn < 0 ? 1f - razn * 0.1f : 1f;

            float damMult = stats != null ? stats.allDamMult : 1f;
            if (skill == 2 || skill == 3 || skill == 4)             // Weapon.as:980-983
            {
                damMult *= stats != null ? stats.gunsDamMult : 1f;
            }

            // PipPage.as:551 reads `w.precMult`, which setPers builds from allPrecMult (Weapon.as:976)
            // AND the weapon's perk family (:1004). Hoisted out of the precision line so the perk
            // pass below can fold into it.
            float precMult = stats != null ? stats.allPrecMult : 1f;

            // The AllData root extras, read once: the perk family (`perk`), the S.A.T.S. fields
            // (`sats cons` / `que` / `no`) and the authored `<phis m>`. Read here rather than down at
            // the AP line because the perk pass below needs the family first.
            WeaponXmlExtras extras = ReadExtras(def.weaponId);

            // ── Weapon.setPers's perk pass (Weapon.as:999-1041) ───────────────────────────────
            //
            // `opt` is written in exactly ONE place — `opt[node.@perk] = true` (Weapon.as:427) — and
            // the loop tests `this.opt[perk]` for each of the ten families, so at most ONE family can
            // apply: the weapon's own root `perk`. Each value below is that one family's Pers stat,
            // which the RPG layer now stores (CharacterStats.WeaponPerkStat). Before that store
            // existed every one of these hit ApplyNamedStat's `default:` and was dropped with a
            // warning, so a perk-fed weapon silently showed damAdd/pierAdd/critchAdd/consMult as
            // their setPers defaults (0/0/0/1) no matter which perks the player held.
            float damAdd = 0f, pierAdd = 0f, critchAdd = 0f, consMult = 1f;
            if (stats != null && CharacterStats.IsWeaponPerkId(extras.Perk))
            {
                damAdd    = stats.WeaponPerkStat(extras.Perk, "Det",    0f);   // Weapon.as:1022
                pierAdd   = stats.WeaponPerkStat(extras.Perk, "Pier",   0f);   // Weapon.as:1026
                critchAdd = stats.WeaponPerkStat(extras.Perk, "Critch", 0f);   // Weapon.as:1030
                consMult  = stats.WeaponPerkStat(extras.Perk, "Cons",   1f);   // Weapon.as:1010
                damMult  *= stats.WeaponPerkStat(extras.Perk, "Dam",    1f);   // Weapon.as:1008
                precMult *= stats.WeaponPerkStat(extras.Perk, "Prec",   1f);   // Weapon.as:1004
            }

            var s = new StringBuilder();

            // PipPage.as:440
            s.Append(Pip("weapontip")).Append(": ").Append(Yel(Pip("weapontip" + skill)));

            // PipPage.as:441-477 — the skill gate lines. `w.lvlNoUse` is not a data field: AS3 sets
            // it in exactly one place, WThrow.as:50, as `if(this.throwTip > 0) lvlNoUse = true`, and
            // nothing else ever writes it. So it is derived here from the same attribute rather than
            // stored — the port already carries it as WeaponDefinition.throwTip.
            if (lvl > 0)
            {
                s.Append('\n').Append(Pip("lvl")).Append(": ").Append(Yel(lvl.ToString()));
                s.Append('\n').Append(Pip("islvl")).Append(": ").Append(Yel(tier.ToString()));

                if (razn > 0) s.Append(RedOpen);

                bool noUse = (def.throwTip > 0 && razn > 0) || razn > 2;
                if (noUse)
                {
                    s.Append(" (").Append(Pip("weapnouse")).Append(')');
                }
                else if (razn > 0)
                {
                    s.Append(razn == 2 ? " (-40% " : " (-20% ");
                    s.Append(tip == 1 ? Pip("rapid") : tip == 4 ? Pip("distance") : Pip("precision"));
                    s.Append(')');
                }

                if (razn > 0) s.Append(SpanClose);
            }

            // PipPage.as:478-486 — the character-level gate, distinct from the skill gate above: it
            // compares against the owner's LEVEL, not the weapon-skill tier. WeaponDefinition.perslvl
            // now carries @perslvl (23 rows, all magic), so this is read rather than skipped.
            if (def.perslvl > 0)
            {
                int persLevel = stats != null ? stats.Level : 0;
                s.Append('\n').Append(Pip("perslvl")).Append(": ").Append(Yel(def.perslvl.ToString()));
                s.Append('\n').Append(Pip("isperslvl")).Append(": ").Append(Yel(persLevel.ToString()));
                if (persLevel < def.perslvl)
                {
                    s.Append(RedOpen).Append(" (").Append(Pip("weapnouse")).Append(')').Append(SpanClose);
                }
            }

            // PipPage.as:487-520 — damage, then explosion damage, then the multi-projectile counts.
            float damage = def.baseDamage;                          // w.damage
            float damageExpl = def.explosionDamage;                 // w.damageExpl
            float resultDam = 0f;
            float resultExpl = 0f;

            s.Append('\n').Append(Pip("damage")).Append(": ");
            if (damage > 0)
            {
                s.Append(Yel(As3Round1(damage)));
                resultDam = ResultDamage(damage, damAdd, damMult, skillMult, skillPlusDam);
                if (resultDam != damage) s.Append(" (").Append(Yel(As3Round1(resultDam))).Append(')');
            }

            if (damage > 0 && damageExpl > 0) s.Append(" + ");

            if (damageExpl > 0)
            {
                // PipPage.as:505 — the explosion's shown base carries damMult where the plain damage
                // line does not. Reproduced as-is; it is the Oracle's own asymmetry.
                s.Append(Yel(As3Round1(damageExpl * damMult)));
                resultExpl = ResultDamage(damageExpl, damAdd, damMult, skillMult, skillPlusDam);
                if (resultExpl != damageExpl)
                {
                    s.Append(" (").Append(Yel(As3Round1(resultExpl))).Append(')');
                }

                s.Append(' ').Append(Pip("expldam"));
            }

            // w.kol / w.explKol — projectilesPerShot and explKol on the definition.
            if (def.projectilesPerShot > 1) s.Append(" [x").Append(def.projectilesPerShot).Append(']');
            if (def.explKol > 1) s.Append(" [x").Append(def.explKol).Append(']');

            // PipPage.as:521-530 — attack rate and DPS, for every weapon but a thrown one.
            float wrapid = ResultRapid(def, tip, skillConf, stats);
            if (tip != 4 && wrapid > 0f)
            {
                s.Append('\n').Append(Pip("aps")).Append(": ")
                    .Append(Yel((OracleFps / wrapid).ToString("F1", CultureInfo.InvariantCulture)));

                float dps = (resultDam + resultExpl) * def.projectilesPerShot * OracleFps / wrapid;
                s.Append('\n').Append(Pip("dps")).Append(": ")
                    .Append(Yel(dps.ToString("F1", CultureInfo.InvariantCulture)));

                // PipPage.as:526-529 — the sustained rate, charged for reloads:
                // `reload * reloadMult / holder * rashod`.
                if (def.magazineSize > 0)
                {
                    float reloadMult = pers != null ? pers.ReloadMult : 1f;
                    float sustained = (resultDam + resultExpl) * def.projectilesPerShot * OracleFps /
                        (wrapid + def.reloadTime * reloadMult / def.magazineSize * def.ammoPerShot);
                    s.Append(" (").Append(Yel(sustained.ToString("F1", CultureInfo.InvariantCulture)))
                        .Append(')');
                }
            }

            // PipPage.as:531 — `w.critCh + w.critchAdd + gg.critCh`. critchAdd is the weapon's perk
            // family's `Critch` stat (rifle → rifleCritch), computed in the perk pass above.
            float critChance = def.critChance + critchAdd + (stats != null ? stats.critCh : 0f);
            s.Append('\n').Append(Pip("critch")).Append(": ")
                .Append(Yel(Mathf.RoundToInt(critChance * 100f) + "%"));

            // PipPage.as:532 — tipDamage. The port's DamageType enum values 1..9 are the Oracle's
            // tipdam1..9 indices (Blade 1 .. Spark 9); PhysicalBullet (0) has no tipdam0 row in the
            // table, and the miss marker that follows is the Oracle's own output for it.
            s.Append('\n').Append(Pip("tipdam")).Append(": ")
                .Append(Blue(Pip("tipdam" + (int)def.damageType)));

            // PipPage.as:533-544 — the loaded-round lines, firearms only.
            if (tip < 4 && def.magazineSize > 0)
            {
                s.Append('\n').Append(Pip("inv5")).Append(": ").Append(Yel(ItemName(def.ammoType)));
                s.Append('\n').Append(Pip("holder")).Append(": ").Append(Yel(def.magazineSize.ToString()));

                if (def.ammoPerShot > 1)
                {
                    s.Append(" (").Append(Yel(def.ammoPerShot.ToString())).Append(' ')
                        .Append(Pip("rashod")).Append(')');
                }
            }

            // PipPage.as:545-548 — a spell's mana cost.
            if (tip == 5)
            {
                s.Append('\n').Append(Pip("dmana")).Append(": ")
                    .Append(Yel(Mathf.RoundToInt(def.magicPoolCost).ToString()));
            }

            // PipPage.as:549-552 — precision, in the Oracle's own units (data value * 40 is a pixel
            // distance; /40 reads it back). precMult was folded with the perk family above.
            if (def.precision > 0)
            {
                s.Append('\n').Append(Pip("prec")).Append(": ")
                    .Append(Yel(Mathf.RoundToInt(def.precision * precMult / 40f).ToString()));
            }

            // PipPage.as:553-556 — armour penetration, `w.pier + w.pierAdd`. pierAdd is the perk
            // family's `Pier` stat (shot → shotPier, acute → acutePier), summed in the perk pass.
            if (def.armorPenetration + pierAdd > 0)
            {
                s.Append('\n').Append(Pip("pier")).Append(": ")
                    .Append(Yel(Mathf.RoundToInt(def.armorPenetration + pierAdd).ToString()));
            }

            // PipPage.as:557-574 — the S.A.T.S. action-point cost, gated on `!w.noSats`
            // (Weapon.as:447, `<sats no>` present). `satsCons` is AllData's <sats cons> (default 10);
            // `consMult` is the perk family's `Cons` stat (pistol → pistolCons) from the perk pass;
            // `gg.pers.satsMult` is the mathlogic perk's multiplier; `skillConf` is the gate penalty.
            // All four are now real values, where the line previously read consMult=1 and satsMult=1
            // as named gaps.
            if (!extras.NoSats)
            {
                float satsMult = stats != null ? stats.satsMult : 1f;
                s.Append('\n').Append(Pip("ap")).Append(": ");
                s.Append(razn > 0 ? RedOpen : YelOpen);
                s.Append(Mathf.RoundToInt(extras.SatsCons * consMult / Mathf.Max(0.01f, skillConf) * satsMult)
                    .ToString());
                s.Append(SpanClose);

                // PipPage.as:570-573 — the queued-shots multiplier, shown only above 1.
                if (extras.SatsQue > 1)
                {
                    s.Append(" (x").Append(Yel(extras.SatsQue.ToString())).Append(')');
                }
            }

            // PipPage.as:579-582 — the weapon's bound perk, from the AllData row's root attribute.
            if (!string.IsNullOrEmpty(extras.Perk) && TextTable != null &&
                TextTable.TryGet("e", extras.Perk, 0, out string perkName))
            {
                s.Append('\n').Append(Pip("refperk")).Append(": ").Append(Pink(perkName));
            }

            // PipPage.as:588-591 — the weapon-slot mass, hard mode only.
            if (HardInventoryProvider != null && HardInventoryProvider() && tip < 4)
            {
                float mass = extras.HasMass ? extras.Mass : def.Mass;
                s.Append('\n').Append(Pip("mass2")).Append(": ")
                    .Append(MassSpan(mass.ToString(CultureInfo.InvariantCulture)));
            }

            // PipPage.as:583-587, :596 — the description, from the weapon table's second child
            // (`Res.txt("w", id, 1)`), falling back to the base id when the row id is a variant.
            s.Append("\n\n").Append(WeaponDescription(row.Id, def));

            return s.ToString();
        }

        /// <summary>The AllData source text, read once per device. Null when the asset is absent.</summary>
        private static string _allData;
        private static bool _allDataRead;

        /// <summary>
        /// <c>Resources/Loot/AllData</c> — the port's mirror of <c>AllData.as</c>'s XML literal —
        /// loaded once and shared by every runtime row reader on this page.
        /// </summary>
        private static string AllDataText
        {
            get
            {
                if (!_allDataRead)
                {
                    _allDataRead = true;
                    TextAsset asset = Resources.Load<TextAsset>("Loot/AllData");
                    _allData = asset != null ? asset.text : null;
                }
                return _allData;
            }
        }

        // `<item>` rows by id, built once from AllDataText. Separate from WeaponXmlBlocks' own cache:
        // that one keys `<weapon>` blocks, this one `<item>` rows, and the ammo branch needs the latter.
        private static Dictionary<string, ItemXmlRow> _itemRows;

        /// <summary>The raw attribute run of an <c>&lt;item&gt;</c> row, or empty when the id is absent.</summary>
        private static string ItemRowAttrs(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return string.Empty;
            string all = AllDataText;
            if (all == null) return string.Empty;

            if (_itemRows == null)
            {
                _itemRows = new Dictionary<string, ItemXmlRow>(StringComparer.Ordinal);
                foreach (ItemXmlRow r in ItemXmlRows.Parse(all))
                    if (!string.IsNullOrEmpty(r.Id) && !_itemRows.ContainsKey(r.Id)) _itemRows[r.Id] = r;
            }

            return _itemRows.TryGetValue(itemId, out ItemXmlRow row) ? row.Attrs : string.Empty;
        }

        /// <summary>
        /// <c>Res.txt(alias, id, razd)</c> — a general table read with an explicit child index, for the
        /// calls that are not the <c>&lt;p&gt;</c> table (item/weapon/armour descriptions, <c>ammomod_N</c>).
        /// Mirrors <see cref="Pip"/>, which is the razd-0 <c>&lt;p&gt;</c> case.
        ///
        /// <para><b>The default is 0, and 0 means the <c>n</c> child — the name, not the description.</b>
        /// <c>Res.clob[0] = "n"</c> (<c>Res.as:29</c>), so a call that omits <c>razd</c> reads the name.
        /// The <c>effStr</c> requirement lines rely on exactly that (<c>Res.txt("e", req.@id)</c> yields
        /// "Survival", not its prose). Getting this default wrong would print a paragraph where the
        /// Oracle prints one word.</para>
        ///
        /// <para><b>Why <see cref="PipTextTable.Text"/> and not <see cref="PipTextTable.TryGet"/>.</b>
        /// At <c>razd == 0</c> the Oracle returns a visible <c>*eff_id</c> marker for a missing key
        /// (<c>Res.as:93-97</c>) and at any other <c>razd</c> it returns <c>""</c>. <c>Text</c>
        /// reproduces both branches, so a miss stays visible; <c>TryGet</c> would silently erase it.
        /// For the razd≥1 call sites above this is a no-op — the two agree there.</para>
        /// </summary>
        /// <param name="alias">A <c>Res.clob</c> key — <c>"i"</c>, <c>"w"</c>, <c>"e"</c>, <c>"p"</c>…</param>
        /// <param name="id">The entry id.</param>
        /// <param name="razd">The child index; 0 = <c>n</c> (name), 1 = <c>info</c>, 2 = <c>mess</c>.</param>
        private string ResTxt(string alias, string id, int razd = 0)
        {
            if (TextTable == null) return string.Empty;
            return TextTable.Text(alias, id, razd);
        }

        // ── effStr (PipPage.as:231-391) ───────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>PipPage.effStr(tip, id, dlvl)</c> — the body formatter for an <b>item</b>, an <b>effect</b>,
        /// a <b>perk</b> or a <b>skill</b>.
        ///
        /// <para><b>What it does, in the Oracle's order.</b> Take the element's own description
        /// (<c>Res.txt("i", id, 1)</c> for an item, <c>Res.txt("e", id, 1)</c> for the rest); strip a
        /// trailing <c>_ad</c> from the id for the <i>lookup only</i>; resolve the element out of
        /// <c>AllData.d[tip]</c>; compute a level; substitute the level's <c>&lt;textvar&gt;</c> into the
        /// <c>#1</c>…<c>#5</c> placeholders; append the <c>&lt;eff&gt;</c> stat lines; append the hard-mode
        /// <c>&lt;sk tip='m'&gt;</c> mass lines; append the <c>&lt;req&gt;</c> requirement block.</para>
        ///
        /// <para><b>The level is the part that is easy to get wrong.</b> It starts at <b>1</b>, not 0, and
        /// the <c>him</c> branches only ever <i>raise</i> it — <c>him='2'</c> sets 2 or 3 from the
        /// addiction table, <c>him='1'</c> copies <see cref="CharacterStats.himLevel"/>. <c>perk</c> reads
        /// a rank (absent → 0), <c>skill</c> reads a <b>tier</b>, not raw points. Then
        /// <paramref name="dlvl"/> is added. A level of 1 means "use <c>textvar[0]</c>" — the
        /// <c>lvl &gt; 1</c> guard is what makes that fall through.</para>
        ///
        /// <para><b>The one place the Oracle's markup cannot be copied.</b> It builds an HTML string and
        /// uses <c>&lt;br&gt;</c> for line breaks. Legacy uGUI <c>Text</c> rich text has no
        /// <c>&lt;br&gt;</c>, so every one of them becomes <c>'\n'</c> here — the same substitution the
        /// weapon branch already makes for the Oracle's own <c>"\n"</c>. The colour spans are unchanged
        /// (see the class's markup note).</para>
        /// </summary>
        /// <param name="tip">AS3 <c>tip</c>: <c>item</c>, <c>eff</c>, <c>perk</c> or <c>skill</c>.</param>
        /// <param name="id">The element id. A trailing <c>_ad</c> is stripped for the lookup.</param>
        /// <param name="pers">The player stats, or null.</param>
        /// <param name="dlvl">AS3 <c>dlvl</c>, added to the computed level. 0 for both call sites.</param>
        private string EffStr(string tip, string id, CharacterStats pers, int dlvl = 0)
        {
            string s = tip == "item" ? ResTxt("i", id, 1) : ResTxt("e", id, 1);

            // PipPage.as:256-259 — the `_ad` suffix marks an addiction's own variant id, and only the
            // LOOKUP is stripped: `s` above already used the full id.
            if (id != null && id.Length > 3 && id.EndsWith("_ad", StringComparison.Ordinal))
                id = id.Substring(0, id.Length - 3);

            string all = AllDataText;
            if (all == null || string.IsNullOrEmpty(id) ||
                !AllDataElements.TryGet(all, tip, id, out AllDataElement dp))
            {
                // PipPage.as:260-269 — no such element (or an empty list): the bare description is the
                // whole answer, and that is NOT an error path.
                return s;
            }

            // PipPage.as:271-301 — the level.
            int lvl = 1;
            if (tip == "perk")
            {
                lvl = pers != null ? pers.GetPerkRank(id) : 0;
            }
            else if (tip == "skill")
            {
                lvl = pers != null ? pers.GetSkillTier(id) : 0;
            }
            else if (Attr(dp.Attrs, "him") == "2")
            {
                // AS3 compares an absent slot as `undefined`, for which both comparisons are false — so
                // the port's 0-for-absent Addiction() gives the identical outcome (see its own note).
                float ad = pers != null ? pers.Addiction(id) : 0f;
                if (ad >= (pers != null ? pers.ad2 : 250)) lvl = 2;
                if (ad >= (pers != null ? pers.ad3 : 500)) lvl = 3;
            }
            else if (Attr(dp.Attrs, "him") == "1")
            {
                lvl = pers != null ? pers.himLevel : 1;
            }

            lvl += dlvl;

            // PipPage.as:302-309 — the level's text variant, with `textvar[0]` as the fallback.
            List<AllDataElement> textvars = AllDataElements.Children(dp.Body, "textvar");
            if (lvl > 1 && textvars.Count >= lvl)
                s = AddVar(s, textvars[lvl - 1].Attrs);
            else if (textvars.Count > 0)
                s = AddVar(s, textvars[0].Attrs);

            // PipPage.as:310-317 — the effect lines. Each reads `n<lvl>`, so a level above the authored
            // count yields an empty value rather than an error; that is the Oracle's behaviour too.
            List<AllDataElement> effs = AllDataElements.Children(dp.Body, "eff");
            if (effs.Count > 0 && lvl > 0)
            {
                s += "\n";
                foreach (AllDataElement eff in effs)
                {
                    string key = !string.IsNullOrEmpty(eff.Id) ? eff.Id : "refeff";
                    s += "\n" + Pip(key) + ": " + Yel(Attr(eff.Attrs, "n" + lvl));
                }
            }

            // PipPage.as:318-337 — the hard-mode mass lines, `sk tip='m'` only.
            if (HardInventoryProvider != null && HardInventoryProvider())
            {
                foreach (AllDataElement sk in AllDataElements.Children(dp.Body, "sk"))
                {
                    if (Attr(sk.Attrs, "tip") != "m") continue;

                    string add = MassSpan("+1");
                    if (AttrF(sk.Attrs, "vd", 0f) > 0f)
                        add = MassSpan("+" + Attr(sk.Attrs, "vd")) + " " + Pip("perlevel");
                    if (AttrF(sk.Attrs, "v1", 0f) > 0f)
                        add = MassSpan("+" + Attr(sk.Attrs, "v1"));

                    s += "\n" + Pip("add_" + sk.Id) + " " + add;
                }
            }

            // PipPage.as:338-389 — the requirements. Note `lvl--` FIRST: the block reports the level
            // *below* the one whose text is being shown.
            List<AllDataElement> reqs = AllDataElements.Children(dp.Body, "req");
            if (reqs.Count > 0)
            {
                s += "\n\n" + Pip("requir");
                lvl--;

                foreach (AllDataElement req in reqs)
                {
                    int reqLevel = AttrPresent(req.Attrs, "lvl") ? AttrI(req.Attrs, "lvl", 1) : 1;
                    if (lvl > 0 && AttrPresent(req.Attrs, "dlvl"))
                        reqLevel += lvl * AttrI(req.Attrs, "dlvl", 0);

                    string line = "\n";
                    bool ok = true;
                    string reqId = req.Id;

                    if (reqId == "level")
                    {
                        line += Pip("level");
                        if ((pers != null ? pers.Level : 0) < reqLevel) ok = false;
                    }
                    else if (reqId == "guns")
                    {
                        // One requirement covering two skills, satisfied by EITHER.
                        line += ResTxt("e", "smallguns") + " " + Pip("or") + " " + ResTxt("e", "energy");
                        int small = pers != null ? pers.GetSkillTier("smallguns") : 0;
                        int energy = pers != null ? pers.GetSkillTier("energy") : 0;
                        if (small < reqLevel && energy < reqLevel) ok = false;
                    }
                    else
                    {
                        line += ResTxt("e", reqId);
                        if ((pers != null ? pers.GetSkillTier(reqId) : 0) < reqLevel) ok = false;
                    }

                    line += ": " + reqLevel;
                    s += ok ? Yel(line) : Red(line);
                }
            }

            return s;
        }

        /// <summary>
        /// <c>PipPage.addVar</c> (<c>PipPage.as:217-229</c>) — substitute a <c>&lt;textvar&gt;</c> node's
        /// <c>s1</c>…<c>s5</c> attributes into the <c>#1</c>…<c>#5</c> placeholders the description
        /// carries, each wrapped in the yellow class.
        ///
        /// <para>String replace, exactly as the Oracle does it, so a description containing a literal
        /// <c>#1</c> that is not a placeholder is substituted too. Faithful, not improved.</para>
        /// </summary>
        private static string AddVar(string text, string textvarAttrs)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(textvarAttrs)) return text;

            for (int i = 1; i <= 5; i++)
            {
                string value = Attr(textvarAttrs, "s" + i);
                if (string.IsNullOrEmpty(value)) continue;
                text = text.Replace("#" + i, Yel(value));
            }

            return text;
        }

        // Attribute readers, unqualified to match how the weapon branch reads its extras. The
        // boundary-anchored implementations live in WeaponXmlAttrs; `damage` here is the ammo row's
        // own attribute and must not be confused with a weapon's `tipdam` (the boundary forbids it).
        private static string Attr(string src, string name) => WeaponXmlAttrs.Attr(src, name);
        private static int AttrI(string src, string name, int fallback) => WeaponXmlAttrs.AttrI(src, name, fallback);
        private static float AttrF(string src, string name, float fallback) => WeaponXmlAttrs.AttrF(src, name, fallback);
        private static bool AttrPresent(string src, string name) => WeaponXmlAttrs.AttrPresent(src, name);

        /// <summary>
        /// The weapon's AllData root attributes — where <c>&lt;sats cons&gt;</c>, <c>&lt;phis m&gt;</c>
        /// and the root <c>perk</c> live, none of which WeaponDefinition carries. Read from
        /// <c>Resources/Loot/AllData</c>, the port's mirror of <c>AllData.as</c>'s XML literal.
        /// </summary>
        private static WeaponXmlExtras ReadExtras(string weaponId)
        {
            string all = AllDataText;
            if (all != null && WeaponXmlBlocks.TryGetBlock(all, weaponId, out WeaponXmlBlock block))
            {
                return WeaponXmlExtrasReader.Read(block);
            }

            return WeaponXmlExtras.Empty;
        }

        /// <summary>
        /// <c>Res.txt("w", id, 1)</c> (<c>PipPage.as:583-587</c>). A variant id carries
        /// <c>^n</c>; the Oracle falls back to the weapon's own base id, which is what stripping the
        /// suffix reproduces.
        /// </summary>
        private string WeaponDescription(string rowId, WeaponDefinition def)
        {
            if (TextTable == null) return string.Empty;

            string id = rowId;
            int caret = id != null ? id.IndexOf('^') : -1;
            if (caret >= 0) id = id.Substring(0, caret);

            if (TextTable.TryGet("w", id, 1, out string text) && text.Length > 0) return text;
            if (def != null && !string.IsNullOrEmpty(def.weaponId) &&
                TextTable.TryGet("w", def.weaponId, 1, out string baseText))
            {
                return baseText;
            }

            return string.Empty;
        }

        /// <summary><c>Res.txt("i", ammo)</c> (<c>PipPage.as:535</c>) — the loaded round's name.</summary>
        private string ItemName(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return string.Empty;
            if (TextTable != null && TextTable.TryGet("i", itemId, 0, out string name)) return name;
            return itemId;
        }

        /// <summary>
        /// <c>Weapon.resultDamage</c> (<c>Weapon.as:1627-1630</c>) —
        /// <c>(damage + damAdd) * damMult * skill * skillPlusDam * (1 - breaking*0.3)</c>.
        ///
        /// <para><c>damAdd</c> is now carried (the weapon perk family's <c>Det</c> stat, Weapon.as:1022,
        /// computed in the perk pass). The <c>breaking</c> term is still 0: it is the weapon
        /// <i>instance's</i> wear (<c>Weapon.as:1358-1362</c>, non-zero only below half durability), and
        /// the inventory row does not carry a condition channel — the row's own value column is what
        /// shows it. That is a different gap from the perk-fed fields: an instance-state input, not a
        /// missing data field.</para>
        /// </summary>
        private static float ResultDamage(float damage, float damAdd, float damMult, float skillMult,
                                          float skillPlusDam)
        {
            return (damage + damAdd) * damMult * skillMult * skillPlusDam;
        }

        /// <summary>
        /// <c>Weapon.resultRapid</c> (<c>Weapon.as:1637-1640</c>) — the base class returns its argument
        /// unchanged. The melee override (<c>WClub.as:658</c>) divides by the skill gate and the
        /// melee speed multiplier, which is the one branch the port reproduces;
        /// <c>rapidMultCont</c> is a status multiplier the info pane does not read.
        /// </summary>
        private static float ResultRapid(WeaponDefinition def, int tip, float skillConf, CharacterStats stats)
        {
            float rapid = def.rapid;
            if (tip == 1 && rapid > 0f)
            {
                float meleeSpd = stats != null ? stats.meleeSpdMult : 1f;
                rapid = rapid / Mathf.Max(0.01f, skillConf) / Mathf.Max(0.01f, meleeSpd);
            }

            return rapid;
        }

        /// <summary>AS3 <c>Math.round(x * 10) / 10</c> — the damage lines' one-decimal form.</summary>
        private static string As3Round1(float value)
        {
            return (Mathf.Round(value * 10f) / 10f).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// AS3 <c>Math.round</c> — <b>halfway rounds toward +Infinity</b>, i.e. <c>floor(x + 0.5)</c>.
        ///
        /// <para><b>This is not <c>Mathf.RoundToInt</c>, and the difference is observable.</b> Unity's
        /// <c>Mathf.RoundToInt</c> is banker's rounding — it sends an exact <c>.5</c> to the
        /// <i>even</i> neighbour — so <c>Math.round(0.5)</c> is 1 in AS3 and <c>Mathf.RoundToInt(0.5f)</c>
        /// is 0. AS3's own rule is <c>floor(x + 0.5)</c>, which also gets the negatives right:
        /// <c>Math.round(-0.5)</c> is 0 and <c>floor(-0.5 + 0.5)</c> is 0.</para>
        ///
        /// <para>The armour and item branches use this. <b>The weapon branch does not</b> — it predates
        /// this helper and calls <c>Mathf.RoundToInt</c> at its own lines; that is a recorded, separate
        /// divergence, not something to "unify" without re-measuring the weapon data. For the armour
        /// branch the two happen to agree on everything shipped: every value it multiplies by 100 is a
        /// two-decimal number, and none of them lands on an exact half (measured 2026-10-14, all 13
        /// resistances plus qual/dexter/sneak/radx/crit/melee/guns/magic).</para>
        ///
        /// <para>Returns a <b>string</b> rather than an <c>int</c>, matching <see cref="As3Round1"/>:
        /// every call site feeds a rich-text span, and an invariant-culture format keeps a comma-decimal
        /// locale from writing "25,0%" into the pane.</para>
        /// </summary>
        private static string As3Round(float value) =>
            ((int)Math.Floor(value + 0.5f)).ToString(CultureInfo.InvariantCulture);

        // ── Rich-text spans ───────────────────────────────────────────────────
        //
        // The Oracle's infoStr builds its text as HTML with the page style sheet's classes
        // (PipPage.setStyle, PipPage.as:163). Legacy uGUI Text has no style sheets, so the four
        // classes it uses become <color> spans over FlashTextStyle's hex constants — the same values
        // PfeUiColors derives its Color structs from, so a bar tint and a stat line cannot disagree.

        private const string SpanClose = "</color>";
        private const string Quote = "\"";
        private const string RedOpen = "<color=" + Quote + FlashTextStyle.PageRed + Quote + ">";
        private const string YelOpen = "<color=" + Quote + FlashTextStyle.PageYel + Quote + ">";

        private static string Yel(string text) => YelOpen + text + SpanClose;
        private static string Red(string text) => RedOpen + text + SpanClose;
        private static string Blue(string text) =>
            "<color=" + Quote + FlashTextStyle.PageBlu + Quote + ">" + text + SpanClose;
        private static string Pink(string text) =>
            "<color=" + Quote + FlashTextStyle.PagePink + Quote + ">" + text + SpanClose;
        private static string MassSpan(string text) =>
            "<color=" + Quote + FlashTextStyle.PageMass + Quote + ">" + text + SpanClose;

        /// <summary><c>Res.pipText(key)</c> — the page's own table, with a visible miss marker.</summary>
        private string Pip(string key)
        {
            if (TextTable != null) return TextTable.Pip(key);
            return "*" + key;
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

            // PipPage.as:922-923 — a sub-page change goes through setStatus, which blanks the pane.
            _infoId = null;

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
            _infoId = id;

            // The pane is filled now, not on the next tick — the Oracle's statInfo runs inside the
            // mouse event, and a one-tick lag would read as a flicker on every hover.
            ShowInfo(row.Value);

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

            // No handler acted: say what the Oracle would have done, and name the verb. Every branch
            // of `itemClick` now has a host, so this is the refusal path — an armour id with no
            // instance, a stim that would heal nothing, a book already read — and not the old
            // "nothing is wired" path it used to be. The refusal itself has already logged its own
            // reason at the point that refused, so this line is the verb, not a second diagnosis.
            string verb;
            switch (row.Value.Kind)
            {
                case PipInventoryKind.Weapon: verb = "equip weapon (PipPageInv.as:545-551)"; break;
                case PipInventoryKind.Armor: verb = "equip armour (PipPageInv.as:552-559)"; break;
                default: verb = "use item (PipPageInv.as:596)"; break;
            }

            Debug.Log($"[PipPageInvView] '{row.Value.Id}' — the Oracle would {verb}, and the host " +
                      "declined: nothing was consumed. See the host's own log line for the reason.");
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
            return PipInventoryList.RowsFor(_source != null ? _source.Read() : null, _subPage, showHidden).Count;
        }

        /// <summary>The row shown at a row-clip index, or null when that clip is empty.</summary>
        private PipInventoryRow? RowAt(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= PipBuckLayout.PageRowCount) return null;
            if (_rowRects[rowIndex] == null || !_rowRects[rowIndex].gameObject.activeSelf) return null;

            bool showHidden = _viewModel != null && _viewModel.ShowHidden.Value;
            List<PipInventoryRow> rows = PipInventoryList.RowsFor(
                _source != null ? _source.Read() : null, _subPage, showHidden);

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
        /// Places a top-registered child at a Flash coordinate. The single y-negation for this file —
        /// see <c>PipAnchor.ToUnityAnchoredPosition</c>, which is the same flip the shell uses, and
        /// the I4 note about what happens when two owners disagree about a sign.
        /// </summary>
        private static void Place(RectTransform rect, float flashX, float flashY)
        {
            rect.anchoredPosition = new Vector2(flashX, -flashY);
        }

        /// <summary>
        /// The same flip for a <b>centre-registered</b> child — the three row art symbols (498, 876,
        /// 897) all register at their centre, so their placements are centre coordinates, not
        /// top-left ones. Kept separate from <see cref="Place"/> so the two registrations cannot be
        /// confused by a caller picking the wrong helper.
        /// </summary>
        private static void PlaceCentre(RectTransform rect, float flashX, float flashY)
        {
            rect.anchoredPosition = new Vector2(flashX, -flashY);
        }

        private Text Cell(int row, int column) => _cells[row * ColumnCount + column];

        private void SetCell(int row, int column, string value)
        {
            Text cell = Cell(row, column);
            if (cell != null) cell.text = value ?? string.Empty;
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
