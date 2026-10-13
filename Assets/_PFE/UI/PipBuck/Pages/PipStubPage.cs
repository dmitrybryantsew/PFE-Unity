using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.UI.PipBuck.Pages
{
    /// <summary>
    /// A page that has not been ported yet. Registered so the device's navigation is complete and the
    /// tab bar is testable before the pages exist.
    ///
    /// <para><b>It reports <see cref="IsImplemented"/> false rather than pretending to work.</b>
    /// <see cref="PipBuckView"/> reads that flag and shows a named notice. The alternative — an empty
    /// page that silently draws nothing — is the failure this project keeps re-learning: a blank
    /// screen and a page that failed to load look identical, and the difference is the whole
    /// diagnosis.</para>
    ///
    /// <para><b>It still draws its sub-tab row, and that is not a contradiction.</b> The row is the
    /// page's <i>shape</i> — which sub-pages exist and what they are called — and the Oracle derives
    /// all of it from the page's own identifier (<c>PipPage.as:143-152</c>) before a single row of
    /// content is built. So the row is knowable today and is worth showing today: it is what makes
    /// the device's navigation verifiable in a play-test while the contents are still stubs, and it
    /// is the half of the page that a later work item must not be allowed to invent differently. The
    /// notice above it still says the contents are missing.</para>
    /// </summary>
    public sealed class PipStubPage : IPipPage
    {
        private readonly string _why;
        private readonly PipSubTabBar _subTabs = new PipSubTabBar();

        private PipBuckCompositionDefinition _composition;
        private Font _font;
        private PipTextTable _text = PipTextTable.Empty;

        private int _subPage = 1;

        public PipStubPage(int pageId, string why)
        {
            PageId = pageId;
            _why = why;
        }

        public int PageId { get; }

        public bool IsImplemented => false;

        /// <summary>Why the page is not here yet — surfaced in the log so the gap is traceable.</summary>
        public string Reason => _why;

        /// <summary>The sub-page the page is on — the Oracle's <c>page2</c>.</summary>
        public int SubPage => _subPage;

        /// <summary>
        /// The sub-tab row, for the shell to hang the click handler off. Exposed rather than wired
        /// internally because the handler is the <i>device's</i> — the Oracle's <c>page2Click</c>
        /// plays a device sound (<c>pip.snd(2)</c>) and, on two pages, opens a different page
        /// entirely. See <see cref="PipBuckManager.SubPageClick"/>.
        /// </summary>
        public PipSubTabBar SubTabs => _subTabs;

        /// <summary>
        /// What opened the page — the variables the Oracle's sub-tab visibility rules branch on.
        /// <see cref="PipSubTabContext.Default"/> for every page in the shipped port, because the four
        /// contextual pages are reached from world interactions that do not exist yet.
        /// </summary>
        public PipSubTabContext Context { get; set; } = PipSubTabContext.Default;

        /// <summary>
        /// The values behind the sub-tab sign markers. <see cref="PipSubTabSignals.None"/> for every
        /// page in the shipped port: the only two pages that sign a sub-tab are the character sheet
        /// (which has no port yet) and the vendor (which has no context yet).
        /// </summary>
        public PipSubTabSignals Signals { get; set; } = PipSubTabSignals.None;

        /// <summary>
        /// Supplies the composition (for the sub-tab button sprite), the font and the localisation
        /// document. Called by <c>PipBuckBootstrapper</c> before <see cref="Build"/>.
        ///
        /// <para><b>Why not constructor arguments.</b> Same reason as <c>PipPageInvView.Configure</c>:
        /// <see cref="IPipPage"/>'s seam is <c>Build(RectTransform)</c> and it is shared by every
        /// page, so widening it would make every page's signature claim three dependencies the
        /// content-less stubs would not otherwise have.</para>
        /// </summary>
        public void Configure(PipBuckCompositionDefinition composition, Font font, PipTextTable text)
        {
            _composition = composition;
            _font = font;
            _text = text ?? PipTextTable.Empty;
        }

        /// <summary>
        /// The port of <c>PipPage.setStatus</c>'s sub-page half.
        ///
        /// <para><b>The argument's meaning is the Oracle's.</b> <c>PipBuck.setPage</c> assigns
        /// <c>page2</c> only when its own argument is <c>&gt; 0</c> (<c>PipBuck.as:427-430</c>), so
        /// <b>0 means "leave the sub-page exactly where it was"</b> — which is what a plain
        /// <c>onoff(1)</c> relies on when the player re-opens the character sheet. A non-zero value
        /// is then passed through <see cref="PipSubTabs.Coerce"/>, because three pages overwrite
        /// <c>page2</c> outright while their <c>setSubPages</c> runs.</para>
        /// </summary>
        public void SetStatus(int subPage)
        {
            if (subPage > 0)
            {
                _subPage = PipSubTabs.Coerce(PageId, subPage, Context);
            }

            Refresh();
        }

        public void Step()
        {
            // Nothing to advance: a page with no contents has no animation, no hover countdown and
            // no scroll. Deliberately silent — `Step` runs 30 times a second.
        }

        /// <summary>
        /// Shows or hides the sub-tab row. The Oracle's <c>vis.visible</c>, which its constructor
        /// sets false (<c>PipPage.as:92</c>) so that every page starts hidden and only
        /// <c>setPage</c> reveals one.
        /// </summary>
        public void SetVisible(bool visible)
        {
            _subTabs.SetVisible(visible);
        }

        /// <summary>Creates the sub-tab row under <paramref name="pageRoot"/>.</summary>
        public void Build(RectTransform pageRoot)
        {
            if (pageRoot == null)
            {
                Debug.LogError($"[PipStubPage] Build for page {PageId} called with no page root. The " +
                               "page's sub-tab row will not exist and the page will look like it has " +
                               "no sub-pages at all.");
                return;
            }

            if (_font == null)
            {
                // Not fatal — the row still draws, with blank captions. Worth naming, because a row
                // of five empty buttons reads as "the localisation failed" rather than "Configure
                // was not called", and the two have different fixes.
                Debug.LogWarning($"[PipStubPage] Page {PageId} was built before Configure, so the " +
                                 "sub-tab captions will be blank.");
            }

            _subTabs.Build(pageRoot, _font, _composition != null ? _composition.subCategoryButton : null);

            // PipPage.as:92 — `this.vis.visible = false;` in the constructor. Every page starts
            // hidden; PipBuckManager.SetPage reveals exactly one.
            _subTabs.SetVisible(false);

            Refresh();
        }

        private void Refresh()
        {
            _subTabs.Refresh(PageId, _subPage, _text, Context, Signals);
        }
    }
}
