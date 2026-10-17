namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The frame facts of <b>page 3</b> — the port of <c>fe.inter.PipPageInfo</c>'s constructor and of
    /// <c>setSubPages</c>'s shared prologue (<c>PipPageInfo.as:38-63</c>, <c>:78-90</c>, <c>:145-147</c>).
    ///
    /// <para><b>Why these numbers are a type and not constants in the view.</b> Three of them are
    /// <i>overwritten</i> by ActionScript, so the symbol's own authored values are dead and a reader
    /// who dumps the SWF will find the wrong numbers. Keeping both, with the reason, is the only way
    /// the next reader does not "fix" the port back to the authored values.</para>
    ///
    /// <para><b>Unity-free on purpose</b>, like <see cref="PipSubTabs"/> and <see cref="PipInfoFrame"/>'s
    /// siblings: every number here is a fact about the Oracle, so the tests that pin them run against
    /// the shipped assembly from a plain shell.</para>
    /// </summary>
    public static class PipInfoFrame
    {
        /// <summary>The Oracle's page index — <c>pages[3]</c> (<c>PipBuck.as:111</c>).</summary>
        public const int InfoPageId = 3;

        // ── The five sub-pages, by the Oracle's own numbers ───────────────────

        /// <summary>Local minimap — <c>PipPageInfo.as:92-105</c>.</summary>
        public const int MapSubPage = 1;

        /// <summary>The quest journal — <c>PipPageInfo.as:106-142</c>.</summary>
        public const int QuestSubPage = 2;

        /// <summary>The travel map — <c>PipPageInfo.as:143-205</c>.</summary>
        public const int WorldSubPage = 3;

        /// <summary>The note list — <c>PipPageInfo.as:206-242</c>.</summary>
        public const int NoteSubPage = 4;

        /// <summary>The bestiary — <c>PipPageInfo.as:243-292</c>.</summary>
        public const int EnemySubPage = 5;

        /// <summary>How many rows a page builds — <c>PipPage.maxrows = 18</c> (<c>PipPage.as:57</c>).</summary>
        public const int RowCount = PipBuckLayout.PageRowCount;

        // ── The prologue's text columns ───────────────────────────────────────
        //
        // setSubPages opens by hiding everything and resetting the two text columns (:84-90). Those
        // values are the Oracle's; the SYMBOL's own authored x for the same two clips is dead, and the
        // SWF dump says so:
        //
        //   visPipInfo (1012) frame 1:  nazv 1001 @ (632.0, 87.0)   info 1000 @ (626.0, 162.1)
        //
        // Both are overwritten on every sub-page before anything is drawn, so the authored pair is
        // never observable. Use these, and take only y/width/font from the symbol.

        /// <summary><c>vis.nazv.x = vis.info.x = 458</c> — <c>PipPageInfo.as:84-85</c>.</summary>
        public const float PrologueTextX = 458f;

        /// <summary><c>vis.nazv.width = 413</c> — <c>PipPageInfo.as:86</c>.</summary>
        public const float PrologueTitleWidth = 413f;

        /// <summary><c>vis.info.width = 458</c> — <c>PipPageInfo.as:87</c>.</summary>
        public const float PrologueBodyWidth = 458f;

        /// <summary>
        /// The travel map's narrower columns — <c>PipPageInfo.as:145-147</c>.
        ///
        /// <para>The world map occupies the left of the page, so sub-page 3 pushes the text columns
        /// right and narrows them. It is the only sub-page that does this, and it is why the two
        /// constants cannot be shared.</para>
        /// </summary>
        public const float WorldTextX = 584f;

        /// <inheritdoc cref="WorldTextX"/>
        public const float WorldTitleWidth = 287f;

        /// <inheritdoc cref="WorldTextX"/>
        public const float WorldBodyWidth = 332f;

        /// <summary>The text column's x for a sub-page — the Oracle's own branch, in one place.</summary>
        public static float TextXFor(int subPage) => subPage == WorldSubPage ? WorldTextX : PrologueTextX;

        /// <summary>The title column's width for a sub-page.</summary>
        public static float TitleWidthFor(int subPage) =>
            subPage == WorldSubPage ? WorldTitleWidth : PrologueTitleWidth;

        /// <summary>The body column's width for a sub-page.</summary>
        public static float BodyWidthFor(int subPage) =>
            subPage == WorldSubPage ? WorldBodyWidth : PrologueBodyWidth;

        // ── The two page clips' own offsets inside `vis` ──────────────────────
        //
        // PipPageInfo.as:48-51 — `visMap` is the minimap window, `visWMap` the travel map. Both are
        // children of `vis` (which the port's page root already sits at), so these are page-root-local
        // exactly like every other coordinate on this page.

        /// <summary><c>visMap.x = 12</c> — <c>PipPageInfo.as:48</c>.</summary>
        public const float MapPanelX = 12f;

        /// <summary><c>visMap.y = 75</c> — <c>PipPageInfo.as:49</c>.</summary>
        public const float MapPanelY = 75f;

        /// <summary><c>visWMap.x = 17</c> — <c>PipPageInfo.as:50</c>.</summary>
        public const float WorldPanelX = 17f;

        /// <summary><c>visWMap.y = 80</c> — <c>PipPageInfo.as:51</c>.</summary>
        public const float WorldPanelY = 80f;

        /// <summary>
        /// The minimap panel's size — sprite 990 exports 843x676.
        ///
        /// <para>It is carried rather than derived because it is the <i>panel's</i> extent, not the
        /// window's: the three zoom buttons sit at x 747, well outside the 714-wide window box, and the
        /// panel has to be big enough to hold them. 747 + a button's width still lands inside 843, which
        /// is the check that the panel size and the button placements describe the same object.</para>
        /// </summary>
        public const float MapPanelWidth = 843f;

        /// <inheritdoc cref="MapPanelWidth"/>
        public const float MapPanelHeight = 676f;

        // ── The minimap window — sprite 990 ───────────────────────────────────
        //
        // Dumped from sprite 990 frame 1 (six placements, all named):
        //
        //   depth  1  981  fon        2.326,4.287  ( 369.1, 300.0)
        //   depth  3  982  vmap       1.000,1.000  (  15.0,  15.0)
        //   depth  5  981  maska      2.326,4.287  ( 369.1, 300.0)
        //   depth  7  985  butZoomP   1.000,1.000  ( 747.1,  27.1)
        //   depth 10  987  butZoomM   1.000,1.000  ( 747.1,  63.1)
        //   depth 13  989  butCenter  1.000,1.000  ( 747.1,  99.1)
        //
        // `fon` and `maska` are the SAME clip (981) at the SAME placement, so the window is that clip's
        // box: `maska` is the mask that clips `vmap` to it, and AS3 reads `fon.width` / `fon.height`
        // for the zoom and centre anchors (`:801`, `:812-813`).
        //
        // 981 exports at 307x136, and the placement scales it by 2.326 / 4.287 — so the window is
        // 714.1 x 583.0, and its translate (369.1, 300.0) is the box's CENTRE. That is checkable two
        // ways and both agree: 307 * 2.326 = 714.1, and 369.1 - 714.1/2 = 12.05, which keeps the box
        // inside sprite 990's own 843x676 export. Reading the translate as the box's top-left instead
        // would put it at x 369..1083, i.e. 240 px outside the clip it lives in.

        /// <summary><c>visMap.fon.width</c> — 307 (the clip's export) x 2.326 (its placement scale).</summary>
        public const float MapWindowWidth = 714.1f;

        /// <summary><c>visMap.fon.height</c> — 136 x 4.287.</summary>
        public const float MapWindowHeight = 583f;

        /// <summary>The window box's centre x — the <c>fon</c> / <c>maska</c> placement translate.</summary>
        public const float MapWindowCentreX = 369.1f;

        /// <summary>The window box's centre y.</summary>
        public const float MapWindowCentreY = 300f;

        /// <summary>The window box's left edge inside <c>visPipMap</c>.</summary>
        public const float MapWindowLeft = MapWindowCentreX - MapWindowWidth * 0.5f;

        /// <summary>The window box's top edge inside <c>visPipMap</c>.</summary>
        public const float MapWindowTop = MapWindowCentreY - MapWindowHeight * 0.5f;

        /// <summary>
        /// The zoom buttons' anchor — <c>setMapSize(fon.width / 2, fon.height / 2)</c>
        /// (<c>PipPageInfo.as:801</c>, <c>:807</c>): the window's own centre.
        /// </summary>
        public const float MapZoomAnchorX = MapWindowCentreX;

        /// <inheritdoc cref="MapZoomAnchorX"/>
        public const float MapZoomAnchorY = MapWindowCentreY;

        /// <summary>
        /// The anchor <c>setMapSize</c> uses when nothing else supplies one — its own defaults,
        /// <c>350</c> and <c>285</c> (<c>PipPageInfo.as:816</c>). Used by the drag-release path
        /// (<c>:795</c>), which is the only caller that passes no anchor.
        /// </summary>
        public const float MapDefaultAnchorX = 350f;

        /// <inheritdoc cref="MapDefaultAnchorX"/>
        public const float MapDefaultAnchorY = 285f;

        /// <summary><c>vmap.x = 15</c> — the pan layer's resting offset inside the window.</summary>
        public const float MapPanX0 = 15f;

        /// <summary><c>vmap.y = 15</c>.</summary>
        public const float MapPanY0 = 15f;

        /// <summary><c>butZoomP</c> / <c>butZoomM</c> / <c>butCenter</c> share this x.</summary>
        public const float MapButtonX = 747.1f;

        /// <summary><c>butZoomP.y</c>.</summary>
        public const float MapZoomInY = 27.1f;

        /// <summary><c>butZoomM.y</c>.</summary>
        public const float MapZoomOutY = 63.1f;

        /// <summary><c>butCenter.y</c>.</summary>
        public const float MapCentreY = 99.1f;

        /// <summary>The zoom level the page opens at — <c>mapScale = 2</c>, <c>ms = 2</c> (<c>:28-30</c>).</summary>
        public const float MapInitialScale = 2f;

        /// <summary>The lowest zoom — <c>setMapSize</c> clamps to 1 (<c>PipPageInfo.as:822-825</c>).</summary>
        public const float MapMinScale = 1f;

        /// <summary>The highest zoom — <c>setMapSize</c> clamps to 6 (<c>PipPageInfo.as:818-821</c>).</summary>
        public const float MapMaxScale = 6f;

        // ── The row template — sprite 999 (`visPipQuestItem`) ─────────────────
        //
        // Sub-tabs 2, 3 and 4 all use this clip as their row (`itemClass = visPipQuestItem`,
        // PipPageInfo.as:40) — the base constructor builds maxrows + 1 of them at x = 30, pitch 30
        // (PipPage.as:108-142). Its six placements:
        //
        //   depth 1  498  back   0.671,1.000  ( 188.3, 12.0)
        //   depth 3  991  nazv   1.000,1.000  (  75.5,  3.0)
        //   depth 4  992  id     1.000,1.000  ( -48.5,  3.0)
        //   depth 5  997  mq     0.626,0.626  (  16.9, 12.0)
        //   depth 7  998  kol    1.000,1.000  ( 284.9,  3.0)
        //   depth 8  897  ramka  0.669,1.000  ( 188.2, 12.0)
        //
        // `back` (498) and `ramka` (897) are ALREADY IMPORTED — they are the inventory row's panel and
        // selection frame (see PipBuckCompositionDefinition.inventoryRowBack / inventoryRowFrame). So
        // this page needs no new row art at all, which is why the row is the cheapest part of it.

        /// <summary><c>back</c> (498) placement x inside the row.</summary>
        public const float RowBackX = 188.3f;

        /// <summary><c>back</c>'s scaleX — the 560-px panel is drawn at 0.671, i.e. ~376 px.</summary>
        public const float RowBackScaleX = 0.671f;

        /// <summary><c>ramka</c> (897) placement x.</summary>
        public const float RowFrameX = 188.2f;

        /// <summary><c>ramka</c>'s scaleX.</summary>
        public const float RowFrameScaleX = 0.669f;

        /// <summary><c>nazv</c> (991) authored x. The Oracle overwrites it per sub-page — see below.</summary>
        public const float RowNameX = 75.5f;

        /// <summary><c>id</c> (992) x — the hidden identifier field, the Oracle's click payload.</summary>
        public const float RowIdX = -48.5f;

        /// <summary><c>mq</c> (997) x — the marker column.</summary>
        public const float RowMarkerX = 16.9f;

        /// <summary><c>mq</c> y.</summary>
        public const float RowMarkerY = 12.0f;

        /// <summary><c>mq</c>'s scale — 0.626 on both axes.</summary>
        public const float RowMarkerScale = 0.626f;

        /// <summary><c>kol</c> (998) x — the right-hand count column.</summary>
        public const float RowCountX = 284.9f;

        /// <summary>The y both text columns and the id share.</summary>
        public const float RowCellY = 3.0f;

        /// <summary>
        /// The y the row's PANEL children share — the dump puts <c>back</c> at (188.3, <b>12.0</b>,
        /// <c>mq</c> at (16.9, <b>12.0</b>) and <c>ramka</c> at (188.2, <b>12.0</b>): the row's vertical
        /// centre line, with the panel art hanging below it. NOT <see cref="RowCellY"/> — that 3.0 is
        /// the text cells' own baseline, and placing the back at it lifts the whole panel 9 px.
        /// </summary>
        public const float RowCentreY = 12f;

        /// <summary>The row clip's own size — sprite 999 exports 426x26.</summary>
        public const float RowWidth = 426f;

        /// <inheritdoc cref="RowWidth"/>
        public const float RowHeight = 26f;

        // The name column's x is moved by setStatItem, per sub-page (PipPageInfo.as:312, :329, :338).
        // The authored 75.5 is never visible on this page.

        /// <summary><c>param1.nazv.x = 32</c> on the quest list — <c>PipPageInfo.as:312</c>.</summary>
        public const float QuestNameX = 32f;

        /// <summary><c>param1.nazv.x = 32</c> on the note list — <c>PipPageInfo.as:329</c>.</summary>
        public const float NoteNameX = 32f;

        /// <summary><c>param1.nazv.x = 5</c> on the bestiary — <c>PipPageInfo.as:338</c>.</summary>
        public const float EnemyNameX = 5f;

        /// <summary>The name column's x for a sub-page — the Oracle's three assignments.</summary>
        public static float NameXFor(int subPage)
        {
            switch (subPage)
            {
                case QuestSubPage: return QuestNameX;
                case NoteSubPage: return NoteNameX;
                case EnemySubPage: return EnemyNameX;
                default: return RowNameX;
            }
        }

        // ── The detail-pane icon — sprite 240 (`ico`) ─────────────────────────
        //
        // visPipInfo (1012) frame 1 places `ico` (240) at (458.0, 160.1). It has no placements of its
        // own (declared frameCount 1, nothing inside), so the bestiary icon is whatever the page adds.

        /// <summary><c>ico</c> x inside the page.</summary>
        public const float IconX = 458f;

        /// <summary><c>ico</c> y inside the page.</summary>
        public const float IconY = 160.1f;

        /// <summary>
        /// The two text columns' y — the symbol's own placements for <c>nazv</c> (1001) and <c>info</c>
        /// (1000). Only the x is overwritten by <c>setSubPages</c>; the y is authored and stays.
        /// </summary>
        public const float TitleY = 87f;

        /// <inheritdoc cref="TitleY"/>
        public const float BodyY = 162.1f;

        /// <summary>
        /// The bestiary icon's box in the port.
        ///
        /// <para><b>A port choice, and the one thing on this page that has no Oracle size.</b> The
        /// Oracle's icon is <c>Unit.initIco</c>'s own display object, added into <c>vis.ico</c> (240) —
        /// which has <i>no placements at all</i>, so the symbol contributes no size. <c>statInfo</c>
        /// then reads <c>vis.ico.height</c> back to place the body under it
        /// (<c>PipPageInfo.as:446</c>), which is why the port needs a number here: without one, the body
        /// would be placed at the same y as the title.</para>
        /// </summary>
        public const float BestiaryIconSize = 64f;

        /// <summary>
        /// Where the bestiary's body starts — <c>vis.info.y = vis.ico.y + vis.ico.height + 20</c>
        /// (<c>PipPageInfo.as:446</c>), with the port's own icon size standing in for
        /// <c>vis.ico.height</c>.
        /// </summary>
        public const float BestiaryBodyY = IconY + BestiaryIconSize + IconBodyGap;

        /// <summary>
        /// Where the bestiary body starts — <c>vis.info.y = vis.ico.y + vis.ico.height + 20</c>
        /// (<c>PipPageInfo.as:446</c>). The height is the icon's, which the port knows once it has
        /// placed the sprite; this is the gap the Oracle adds below it.
        /// </summary>
        public const float IconBodyGap = 20f;

        // ── The travel map — sprite 834 (`visPipWMap`) ────────────────────────

        /// <summary>
        /// The map background shape's own charId inside sprite 834. The dump shows one <b>unnamed</b>
        /// placement at depth 3, charId 800, at (0, 0) — that is the wasteland backdrop the 27 nodes
        /// sit on. It is the only placement in the clip that is not a land node.
        /// </summary>
        public const int WorldBackdropCharId = 800;

        /// <summary>
        /// The travel map panel's size — sprite 834 exports 571x597. The 27 nodes span x 58..511 and
        /// y 43..512, and each node's own clip (833) is 118x118 centred on its placement, so the
        /// outermost node reaches x 18..551 and y -16..571: both inside the export, which is the check
        /// that the placement table and the panel size describe the same object.
        /// </summary>
        public const float WorldPanelWidth = 571f;

        /// <inheritdoc cref="WorldPanelWidth"/>
        public const float WorldPanelHeight = 597f;

        /// <summary>
        /// The travel node clip's own size — <c>sim_wmap</c> (833) exports 118x118, and its placements
        /// are node CENTRES (they are spread over the whole 571x597 panel).
        /// </summary>
        public const float WorldNodeSize = 118f;

        /// <summary>The land icon inside a node — <c>sim</c> (829) exports 40x40.</summary>
        public const float WorldIconSize = 40f;

        /// <summary>The selection frame inside a node — <c>zad</c> (803) exports 42x42, 2 frames.</summary>
        public const float WorldSelectionSize = 42f;

        /// <summary>
        /// Where a travel node's <c>sign</c> marker sits, relative to the node — the "new land" flag the
        /// Oracle plays when <c>helpMess</c> is on and the land is reachable but unvisited
        /// (<c>PipPageInfo.as:188-192</c>).
        ///
        /// <para><b>Not a number from the SWF.</b> <c>sign</c> is a child of <c>sim_wmap</c> (833) whose
        /// placement the port has not dumped — the node's own children are <c>zad</c> 803,
        /// <c>sim</c> 829 and <c>sign</c> 832, and only <c>zad</c> and <c>sim</c> are needed to draw the
        /// node. The port draws the sign as a tinted outline of the node instead of the Oracle's animated
        /// clip, so there is no offset to read. Recorded as a port choice, not cited as evidence.</para>
        /// </summary>
        public const float WorldSignInset = 6f;

        // ── Text that is not a row ────────────────────────────────────────────

        /// <summary><c>bottext</c> (1002) authored y inside the page — the page's own footer strip.</summary>
        public const float BottomTextY = 653.1f;

        /// <summary><c>emptytext</c> (1003) authored position inside the page.</summary>
        public const float EmptyTextX = 19.5f;

        /// <inheritdoc cref="EmptyTextX"/>
        public const float EmptyTextY = 86.2f;

        /// <summary><c>bottext</c>'s authored x — the same 19.5 as <c>emptytext</c>.</summary>
        public const float BottomTextX = 19.5f;

        /// <summary>The confirmation button's x — <c>butOk</c> (508) in sprite 1012.</summary>
        public const float ConfirmButtonX = 679.7f;

        /// <summary>The cancel button's x — <c>butDef</c> (508).</summary>
        public const float CancelButtonX = 448.7f;

        /// <summary>Both buttons' y.</summary>
        public const float ButtonY = 651.1f;

        /// <summary>Both buttons' size — symbol 508 exports 187x28.</summary>
        public const float ButtonWidth = 187f;

        /// <inheritdoc cref="ButtonWidth"/>
        public const float ButtonHeight = 28f;

        // ── The `mq` marker's frames — sprite 997 (`mainQuest`) ───────────────
        //
        // ONE symbol carries two meanings, and the mapping is forced rather than guessed:
        //
        //   sprite 997 has exactly 4 frames (declared frameCount), no DoAction and no frame labels;
        //   PipPageInfo.as:314 steps it to frame 1 for a MAIN QUEST;
        //   PipPageInfo.as:334 steps it to `param2.ico + 1` for a NOTE, where ico = int(@imp) > 0;
        //   the document's @imp histogram is exactly {1: 96, 2: 12, 3: 3} — no 0, no 4.
        //
        // So frame 1 is the quest marker and frames 2/3/4 are note importance 1/2/3: 1 + 3 = 4, an exact
        // fit with no spare frame and no gap. There are no labels to read because none exist — the two
        // call sites ARE the naming.

        /// <summary>The main-quest marker frame — <c>mq.gotoAndStop(1)</c> (<c>PipPageInfo.as:314</c>).</summary>
        public const int MarkerMainQuestFrame = 1;

        /// <summary>
        /// The frame a note's importance icon steps to — <c>mq.gotoAndStop(param2.ico + 1)</c>
        /// (<c>PipPageInfo.as:334</c>), where <c>ico = int(@imp)</c> and <c>@imp</c> is 1..3.
        /// </summary>
        public static int MarkerNoteFrame(int importance) => importance + 1;

        /// <summary>How many frames <c>mainQuest</c> (997) has — the arithmetic above, as a number a
        /// fixture can assert against the mapping.</summary>
        public const int MarkerFrameCount = 4;

        /// <summary>The highest note importance the document uses, i.e. the last frame
        /// (<c>@imp</c> histogram tops out at 3).</summary>
        public const int MaxNoteImportance = 3;

        // ── The note row's title truncation ───────────────────────────────────
        //
        // PipPageInfo.as:330 — `param1.nazv.htmlText = param2.nazv.substr(param2.nazv.charAt(0) == " "
        // ? 3 : 0, 60)`. Two quirks in one line: the cap is 60 characters, and a title whose FIRST
        // character is a space is trimmed by 3 rather than by 1 (the Oracle's note titles are written
        // with a leading indent, and `n.t` carries it).

        /// <summary>The row caption's character cap — <c>substr(…, 60)</c> (<c>PipPageInfo.as:330</c>).</summary>
        public const int NoteTitleMaxChars = 60;

        /// <summary>How far a space-led title is trimmed — <c>charAt(0) == " " ? 3 : 0</c>.</summary>
        public const int NoteTitleSpaceTrim = 3;

        // ── The header row ────────────────────────────────────────────────────
        //
        // The bestiary's header (`statHead`, PipPageInfo.as:250-254) is the `i = -1` iteration of the
        // same row loop the list uses, so it sits one pitch above row 0. The position lives in
        // PipBuckLayout.PageHeaderPosition() and is NOT mirrored here: that function returns a
        // UnityEngine.Vector2, and copying it would drag UnityEngine into a type whose whole purpose is
        // to be readable from a plain shell. This page is only special in that it turns the header
        // *on* — every other page leaves it hidden.
    }
}
