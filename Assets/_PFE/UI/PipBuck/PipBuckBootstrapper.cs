using System;
using UnityEngine;
using MessagePipe;
using VContainer;
using PFE.Core;
using PFE.Core.Input;
using PFE.Core.Messages;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Systems.Campaign;
using PFE.Systems.Inventory;
using PFE.Systems.Map;
using PFE.Systems.Trade;
using PFE.UI.MainMenu;
using PFE.UI.PipBuck.Pages;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// Creates the PipBuck's canvas and shell at runtime, and drives its hotkeys.
    ///
    /// <para><b>A second canvas, not a child of the HUD's.</b> The two interfaces scale differently on
    /// purpose: the HUD uses a <c>CanvasScaler</c> against a 1200x800 reference because
    /// <c>GUI.resizeScreen</c> anchors to a virtual screen's edges, while the PipBuck is positioned in
    /// raw stage pixels by <c>PipBuck.resizeScreen</c>. Sharing one canvas would force one of the two
    /// to convert units it has no counterpart for in the Oracle. Two canvases also means the device
    /// draws entirely above the HUD, which is the Oracle's own ordering — the PipBuck is a full-screen
    /// overlay that hides the world.</para>
    ///
    /// <para><b>Why hotkeys are polled here and not published as messages.</b> See
    /// <see cref="InputReader.PipToggle"/>: interface input is not simulation state, and it has to be
    /// readable while the simulation is suspended — which is precisely the state the device puts the
    /// world into.</para>
    /// </summary>
    [LocalOnly]
    public sealed class PipBuckBootstrapper : MonoBehaviour
    {
        private InputReader _input;
        private PipBuckManager _manager;

        private PipBuckView _view;
        private PipBuckCompositionDefinition _composition;

        /// <summary>
        /// The Oracle's localisation document, loaded once from <c>Resources</c>. Every caption the
        /// device shows is a lookup in it (<c>Res.pipText</c>), so it is loaded before the pages are
        /// registered and handed to each one that needs it.
        /// </summary>
        private PipTextTable _text = PipTextTable.Empty;

        /// <summary>
        /// Where the Oracle's localisation document lives in the project. The game keeps it next to
        /// the SWF and names it in <c>lang.xml</c>; the port keeps it in <c>Resources</c> for the same
        /// reason the composition asset is there — nothing exists yet that could hold a serialized
        /// reference to it.
        ///
        /// <para>The file is a verbatim copy of <c>text_en.xml</c> from the shipping build. That is
        /// the document <c>World.as:531</c> loads for the <c>en</c> entry in <c>lang.xml</c>, and the
        /// one the Oracle tree never contained.</para>
        ///
        /// <para><b>The load itself now lives in <c>PfeTextTableProvider</c></b>, because the script
        /// layer needs the same document — <c>Res.messText</c> and <c>GUI.dialText</c> read the
        /// <c>txt</c> table, which is where every campaign message lives. Two loaders would parse the
        /// 800 KB document twice and could disagree about whether it was found.</para>
        /// </summary>
        public const string TextTableResourcePath = PFE.UI.Common.PfeTextTableProvider.TextTableResourcePath;

        /// <summary>
        /// The interface font, resolved once. <see cref="ResolveFont"/> falls back to
        /// <c>Font.CreateDynamicFontFromOSFont</c>, which builds a NEW <c>Font</c> object every call —
        /// so resolving it twice would put the shell and the pages on two different font instances,
        /// with two atlases, for no reason.
        /// </summary>
        private Font _font;

        /// <summary>
        /// The player, cached. See <see cref="ResolveInventory"/> for why this is a lazy scene search
        /// and not an injected reference.
        /// </summary>
        private PlayerController _player;

        /// <summary>Earliest unscaled time at which a failed player lookup may be retried.</summary>
        private float _nextRescanTime;

        /// <summary>
        /// The character sheet's name row, read once from <c>GameBootData</c>.
        /// <see cref="ResolvePlayerName"/> explains the cache.
        /// </summary>
        private string _playerName = string.Empty;
        private bool _nameResolved;

        /// <summary>
        /// The Oracle's <c>globalDif</c> as last read. Kept only so the row has a value before the
        /// provider is first polled; <see cref="ResolveDifficulty"/> re-reads the live setting every
        /// time, because the oracle's <c>globalDif</c> is mutable for the whole run.
        /// </summary>
        private int _difficulty = PlayerPipStatSource.DefaultDifficulty;

        /// <summary>
        /// How long to wait before re-searching the scene for the player. The same interval and the
        /// same reasoning as <c>HudBootstrapper.RescanInterval</c>: a respawn or a late-spawned
        /// player is picked up within half a second, whereas searching every read would be a full
        /// scene walk per frame for as long as the player is absent.
        /// </summary>
        private const float RescanInterval = 0.5f;

        /// <summary>
        /// Page 6 — AS3 <c>PipPageMed</c>, the doctor's contextual page. The id comes from the view,
        /// because the view is the thing that owns the page.
        /// </summary>
        private const int ClinicPageId = PipPageMedView.MedPageId;

        /// <summary>
        /// The container, for the one subscription this bootstrapper owns. Resolved lazily through
        /// <see cref="IObjectResolver.TryResolve"/> rather than injected as a constructor argument for
        /// the reason <c>HudBootstrapper</c> does the same: a missing broker should degrade to "the wall
        /// map draws nothing", which is a report, not a device that never builds at all.
        /// </summary>
        private IObjectResolver _resolver;

        /// <summary>The travel-map subscription, released in <see cref="OnDestroy"/>.</summary>
        private IDisposable _travelMapSubscription;

        /// <summary>The trade page, kept so the vendor subscription can open it without a cast.</summary>
        private PipPageVendView _vendorPage;

        private IDisposable _vendorSubscription;

        /// <summary>The clinic-page subscription, released in <see cref="OnDestroy"/>.</summary>
        private IDisposable _clinicSubscription;

        /// <summary>The doctor's page, kept so the clinic subscription can open it without a cast.</summary>
        private PipPageMedView _medPage;

        [Inject]
        public void Construct(InputReader input, PipBuckManager manager, IObjectResolver resolver)
        {
            _input = input;
            _manager = manager;
            _resolver = resolver;
        }

        private void Start()
        {
            Build();
        }

        private void Build()
        {
            // The localisation document first: every caption below — the six tabs, the inventory's
            // five sub-tabs, its six column headings and its empty-list notice — is a lookup in it.
            _text = LoadTextTable();

            // Resources, because the composition is generated into Data/Resources/UI by the importer
            // and has no serialized reference to hang off — there is no scene object that could hold
            // one before this runs.
            _composition = Resources.Load<PipBuckCompositionDefinition>("UI/PipBuckComposition");
            if (_composition == null)
            {
                Debug.LogError("[PipBuckBootstrapper] No PipBuckComposition at " +
                               "Resources/UI/PipBuckComposition. Run PFE/Art/Import Game Interface " +
                               "step 2 (Generate Composition). The device will not be built.");
                return;
            }

            if (!_composition.Validate(out string report))
            {
                // Logged, not fatal: a composition missing one array should still produce a usable
                // device with that one element absent, and the report names exactly what is missing.
                Debug.LogWarning("[PipBuckBootstrapper] " + report);
            }

            var canvasGo = new GameObject("PipBuck Canvas",
                typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // ConstantPixelSize: canvas local units ARE stage pixels, so PipBuck.resizeScreen's own
            // output is applied without a conversion. See PipBuckView's class summary.
            var scaler = canvasGo.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            // The scene's EventSystem. This canvas gets a GraphicRaycaster, but a raycast is not
            // input: without an EventSystem carrying an input module, no Button or row relay ever
            // hears about a click. SampleScene.unity has no EventSystem object at all, so every
            // uGUI control in the gameplay scene was dead until this ran. See UiEventSystem.
            PFE.UI.Common.UiEventSystem.Ensure();

            _view = canvasGo.AddComponent<PipBuckView>();
            _font = ResolveFont();
            _view.Build(canvas, _composition, _manager, _font);

            // ── The right-hand status monitor (Item I8) ─────────────────────────
            //
            // Mounted on the `pr` clip itself, which is where the Oracle builds its 15 rows
            // (PipBuck.as:131-144) and where they live for the device's whole life — a page change
            // only refills them, it never hides them. The view and its source were both written
            // for this work item and had never been instantiated, so the device's entire
            // right-hand frame drew nothing.
            var monitor = _view.StatusPanel.gameObject.AddComponent<PipStatusMonitorView>();
            monitor.Build(_view.StatusPanel, _composition, BuildStatusSource(), _font,
                _manager.ViewModel);

            RegisterPages();

            // The wall map's page. This is the subscriber `TravelMapOpenedMessage` has never had — see
            // BindTravelMap for what was dead and why it mattered.
            BindTravelMap();
            BindVendorPage();
            BindClinicPage();

            // The tab captions. PipBuckPages.TabLabelFor resolves the Oracle's own string-table key
            // (Res.pipText("main" + n), PipBuck.as:172) against the localisation document, which is
            // the file that document indexes — see PipTextTable for how it was found.
            for (int pageId = 0; pageId <= PipBuckPages.MaxPageId; pageId++)
            {
                _manager.SetTabLabel(pageId, PipBuckPages.TabLabelFor(pageId, _text));
            }

            // Re-render now that the manager actually has pages and labels.
            //
            // This is load-bearing, not a belt-and-braces call. The shell renders from
            // PipBuckView's subscriptions, which fire on Active / ActiveTab / ContextualTab / Page —
            // and all four already hold their final values before the device is ever opened (Page
            // and ActiveTab are both 1, and opening on page 1 leaves them there). So nothing fires
            // when the player presses Tab, and the shell would keep what it drew during Build:
            // empty tab labels, and a notice reading "page 1 is not implemented" from the moment
            // before any page was registered. Both were visible in the play-test that found this.
            _view.RefreshShell();

            Debug.Log("[PipBuckBootstrapper] PipBuck shell built. Tabs: " +
                      PipBuckPages.TabLabelFor(0, _text) + " " +
                      PipBuckPages.TabLabelFor(1, _text) + " " + PipBuckPages.TabLabelFor(2, _text) + " " +
                      PipBuckPages.TabLabelFor(3, _text) + " " + PipBuckPages.TabLabelFor(4, _text) + " " +
                      PipBuckPages.TabLabelFor(5, _text) +
                      ". (Resolved through the Oracle's own keys (" + PipBuckPages.LabelKeyFor(1) +
                      "…). The document holds " + _text.Count + " entries.)");

            Debug.Log("[PipBuckBootstrapper] Sub-tab rows, page 1..9: " + DescribeSubTabs());
        }

        /// <summary>
        /// One line naming every page's visible sub-tabs, resolved through the document.
        ///
        /// <para>Logged rather than left to the screen because the row is the one part of a page that
        /// is knowable before the page is written: if a caption here reads <c>*pip_…</c> the document
        /// did not load, and if a page shows the wrong <i>number</i> of sub-tabs the visibility rule
        /// is wrong — two different failures that look the same on a five-button strip.</para>
        /// </summary>
        private string DescribeSubTabs()
        {
            var sb = new System.Text.StringBuilder();

            // Every stub is in the default context: no NPC, no workbench and the shell on. Pages 4, 6,
            // 7 and 9 hide more once something opens them, which no port path does yet.
            PipSubTabContext context = PipSubTabContext.Default;

            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (int subPage in PipSubTabs.VisibleSubPages(pageId, context))
                {
                    names.Add(PipSubTabs.Label(pageId, subPage, _text));
                }

                sb.Append(pageId).Append('=')
                  .Append(PipBuckPages.IdentifierFor(pageId)).Append('[')
                  .Append(names.Count == 0 ? "none" : string.Join(", ", names))
                  .Append("] ");
            }

            return sb.ToString();
        }

        /// <summary>
        /// The Oracle's localisation document — the table <c>Res.pipText</c> and <c>Res.txt</c>
        /// resolve against (<c>Res.as:55-147</c>), loaded from <c>lang.xml</c>'s <c>en</c> entry by
        /// <c>TextLoader</c> (<c>World.as:531-539</c>).
        ///
        /// <para><b>Why a missing document is a warning and not an error.</b> Every caption has a
        /// documented fallback, so the device is still readable without it — it just reads the
        /// pre-localisation words. Failing the whole build over a caption table would take the
        /// inventory page down with it, which is a strictly worse trade.</para>
        ///
        /// <para><b>Why a parse failure is reported rather than swallowed.</b> A malformed document
        /// that silently became <see cref="PipTextTable.Empty"/> would look exactly like a missing
        /// file, and the two have different fixes.</para>
        /// </summary>
        private static PipTextTable LoadTextTable() => PFE.UI.Common.PfeTextTableProvider.Shared;

        /// <summary>
        /// Registers every page in the Oracle's table, so navigation is complete from day one. Pages
        /// that have not been ported register as <see cref="PipStubPage"/> and say so on screen.
        /// </summary>
        private void RegisterPages()
        {
            // ── Page 1: the character sheet (Item I10) ────────────────────────
            //
            // The source resolves the player lazily, like the inventory's, and for the same reason —
            // see ResolveInventory. The page therefore shows an empty sheet until the player exists,
            // which is honest, rather than binding null once and staying empty forever.
            var statPage = new PipPageStatView(BuildStatSource())
            {
                // The Oracle's captions for the five sub-tabs (stat1..stat5), the 41 param labels, the
                // three list headings (is1..is5), the skill and perk names (the `e` table), the three
                // empty-list notices and the bottom strip's keys.
                TextTable = _text,

                // The Oracle's row actions. Left unset: every one of them needs an RPG command the port
                // does not have, and the page reports what the Oracle would have done instead of
                // silently doing nothing. The perk picker is the exception and is handled by the page
                // itself, because a pick is page state rather than an RPG command — SelectPerk records
                // it and CommitSelectedPerk spends the point (PipPageStat.as:1007-1018).
                RowActivated = null,

                // The Oracle ends the accept branch with `pip.setRPanel(); World.w.saveGame();`
                // (PipPageStat.as:1019-1021). Neither has a seam here yet — the device owns both — so
                // this stays unset rather than pretending. See PerkCommitted.
                PerkCommitted = null,
            };

            statPage.Configure(_composition, _font);

            // The handler belongs to the device, not the page: PipPage.page2Click plays a device sound
            // and, on the doctor and the vendor, opens a different page entirely. See
            // PipBuckManager.SubPageClick.
            //
            // **Claimed on the PAGE, not on its sub-tab bar.** The bar's own `SubPageClicked` is written
            // by PipPageStatView.Build, which forwards to this property — and Build runs at the end of
            // this method, so the previous version of this line, which wrote the bar's slot directly, was
            // overwritten a few statements later. Every sub-tab then raised a null action: the buttons
            // were built, laid out and lit, and did nothing. That is the reported "skills, perks, effects,
            // health is not clickable", and it was silent in the only way that matters — nothing logged,
            // nothing threw.
            statPage.SubPageClicked = subPage => _manager.SubPageClick(subPage);

            _manager.RegisterPage(statPage);

            // ── Page 2: the inventory (Item I9) ───────────────────────────────
            //
            // The source is built from a lazy resolver rather than an injected GameInventory, because
            // the inventory lives on the player and the player is assembled in PlayerController.Awake
            // — see ResolveInventory. The page therefore shows an empty list until the player exists,
            // which is honest, rather than binding null once and staying empty forever.
            // The source needs the localisation document and three live readers — see each property on
            // PlayerPipInventorySource. Every one resolves per read, for the reason the source's own
            // remarks give: the player is assembled in PlayerController.Awake, which Unity does not order
            // against this bootstrapper's Start, so a captured instance would bind null once.
            var inventorySource = new PlayerPipInventorySource(ResolveInventory)
            {
                // Item.as:206-267 — what each row is called. Without this the page prints the
                // definition's placeholder, the literal "Item Name", on all 500 item rows: the play-test's
                // "items have no name". The Oracle has no name field at all and assigns `nazv` from this
                // document, which is why the source needs it rather than a better display name on the asset.
                TextTable = _text,

                // gg.newWeapon.id — the selection frame, PipPageInv.as:391. Non-empty because
                // PlayerWeaponLoadout.Equip writes GameInventory.CurrentWeaponId.
                CurrentWeaponIdProvider = ResolveEquippedWeaponId,

                // gg.currentWeapon.ammoBase — the ammo page's "the loaded round sorts first" nudge, :286.
                LoadedAmmoBaseProvider = ResolveLoadedAmmoBase,

                // World.w.hardInv — gates the mass column (:462-465).
                HardInventoryProvider = ResolveHardInventory,
            };

            var inventoryPage = new PipPageInvView(_manager.ViewModel, inventorySource)
            {
                FooterProvider = BuildInventoryFooter,

                // The Oracle's captions for the sub-tabs (inv1..inv5), the six column headings
                // (ii1..ii8) and the empty-list notice (emptyinv).
                TextTable = _text,

                // The Oracle's left-click action: equip a weapon, wear armour, or use an item.
                // See ActivateInventoryRow.
                RowActivated = ActivateInventoryRow,

                // ── The info pane's three inputs (PipPage.infoItem → infoStr) ──
                //
                // pip.arrWeapon[id] (PipPage.as:423) — the hovered weapon's definition, which carries
                // both the stat block's numbers and weaponVisual, the held art setIco draws.
                WeaponDefinitionProvider = ResolveWeaponDefinition,

                // inv.armors[id] (PipPage.as:600) — the armour INSTANCE, not its definition: the stat
                // block's ratings come from `xml.upd[lvl]` and the level is per-instance. Resolved
                // lazily for the same reason as the two below.
                ArmorInstanceProvider = ResolveArmorInstance,

                // gg.pers (Weapon.setPers:963) — the skill tier and the damage multipliers the stat
                // block reads. Resolved lazily for the same reason the inventory source is.
                PlayerStatsProvider = () => ResolvePlayer()?.CharacterStats,

                // World.w.hardInv — gates the mass2 line (PipPage.as:588-591), same flag as the list's.
                HardInventoryProvider = ResolveHardInventory,
            };

            inventoryPage.Configure(_composition, _font);
            _manager.RegisterPage(inventoryPage);

            // ── Page 3: world info (Item I11) ─────────────────────────────────
            //
            // Map, quests, travel, notes and bestiary. The source resolves the campaign and the player
            // lazily for the same reason the other two do — CampaignManager.Current is assigned in its
            // own Awake order and the player is assembled in PlayerController.Awake — so every provider
            // below re-reads through to the live object rather than capturing one.
            var infoSource = new PlayerPipInfoSource(
                () => CampaignManager.Current,
                () => ResolvePlayer())
            {
                // MapBridge.CurrentLandMap — the same resolution path the F2 debug overlay uses, so the
                // page's minimap and the overlay's cannot disagree about which map is live. Re-searched
                // per read rather than cached: the bridge appears with the land, and a reference captured
                // before it exists would stay null for the whole session.
                LandMapProvider = ResolveLandMap,

                PlayerNameProvider = ResolvePlayerName,

                // World.w.game.triggers — the vendor gate and the travel ladder. Absent reads 0 here;
                // the bestiary's kill count gets its own provider below, whose absent reads negative.
                TriggerProvider = ResolveTriggerAsInt,

                // The bestiary's frag counter. The Oracle's guard `triggers["frag_"+id] >= 0` makes
                // "absent" and "stored 0" different answers, so the default differs from the one above.
                FragCountProvider = name => ResolveTriggerAsInt(name, -1),

                SkillProvider = ResolveSkillLevel,

                // Deliberately left null, each for a reason recorded on the provider itself:
                // RevealAllMapProvider (the console's drawAllMap — no port path can set it), TestModeProvider
                // (World.w.testMode — the shipped game's value is false and nothing in the port turns it
                // on), HelpMessProvider (the options screen owns it and page 5 is still a stub), and
                // SpeedShooterProvider (CharacterStats records speedShtr as unported — a row that
                // guessed would be worse than a row that is absent).
            };

            var infoPage = new PipPageInfoView(infoSource)
            {
                // The Oracle's captions: the five sub-tabs, `emptymap`, `alltask`, `trans`, `frag` and
                // the bestiary's unit names (`Res.txt("u", …)`).
                TextTable = _text,

                // PipPage.page2Click — the device's, for the same reason the character sheet's is. See
                // PipBuckManager.SubPageClick. Claimed on the page before Build, like statPage's.
                SubPageClicked = subPage => _manager.SubPageClick(subPage),

                // `pip.helpText` plus `pip.vis.butHelp.visible` (PipPageInfo.as:89, :203-204) — the
                // tooltip and the "?" button are the device's, so the page publishes and the shell draws.
                HelpChanged = (text, buttonVisible) =>
                {
                    _manager.ViewModel.HelpText.Value = text ?? string.Empty;
                    _manager.ViewModel.HelpVisible.Value = buttonVisible;
                },

                // `game.beginMission(targetLand)` (PipPageInfo.as:762) and `pip.onoff(-1)` (:764).
                TravelConfirmed = BeginMission,
                CloseRequested = () => _manager.Close(),

                // `game.addQuest(id, null, false, false, false)` (:781) for the alltask button.
                QuestsGranted = GrantQuests,

                // `pip.noAct` (:719) — nothing in the port opens the device during a cutscene yet, so
                // the gate reads false; landing one changes this line and nothing else.
                ActionsBlocked = () => false,
            };

            infoPage.Configure(_composition, _font, _text);
            _manager.RegisterPage(infoPage);

            // ── The other seven: sub-tab rows only (see RegisterStub) ─────────
            //
            // Named individually rather than looped so the log states which work item each one
            // belongs to, instead of a count.

            // ── Page 4: trade (docs/TradersAndTrade, T7) ──────────────────────
            //
            // The page needs a source, and the source needs the live bag and stats — both resolved
            // lazily for the same reason the inventory's is (the player is assembled in
            // PlayerController.Awake, which is not ordered against this Start).
            var vendorSource = new PlayerVendorPageSource(ResolveInventory,
                () => ResolvePlayer()?.CharacterStats,
                id => ResolvePlayer()?.GetComponent<PlayerInventory>()?.ResolveItem(id))
            {
                TextTable = _text,
                HardInventoryProvider = ResolveHardInventory,
                InBaseProvider = () => CampaignManager.Current != null && CampaignManager.Current.IsCurrentLandBase,
                TriggerIsOneProvider = name => ResolveTriggerAsInt(name) == 1,

                // `AllData.d.weapon` — the table the item resolver cannot answer for. Without it the
                // weapon vendor's shelf named every row by its raw id and priced it 0.
                WeaponResolver = id => ResolvePlayerInventory()?.ResolveWeapon(id),

                // `pers.priceRepArmor` (Pers.as:105, default 0.5) has no ported field — the gap ledger
                // claimed one, and a grep for the name finds nothing. The Oracle's own default is used
                // rather than inventing a curve; landing the field changes this line only.
                RepairArmorMultiplierProvider = () => 0.5f,
            };

            var vendPage = new PipPageVendView(_manager.ViewModel, vendorSource)
            {
                TextTable = _text,
                SubPageClicked = subPage => _manager.SubPageClick(subPage),

                // `pip.snd(3)` after a transaction (PipPageVend.as:920-924). The sound belongs to the
                // device, so the page raises it and the manager plays it.
                TransactionCommitted = () => _manager.Play(3),
            };

            vendPage.Configure(_composition, _font, _text);
            _manager.RegisterPage(vendPage);
            _vendorPage = vendPage;

            RegisterStub(5, "PipPageOpt — options / save / load");

            // ── Page 6: the doctor (PipPageMed) ───────────────────────────────
            //
            // The page needs a source, and the source needs the live player's three halves — the organ
            // readings on CharacterStats, the HP pool on UnitStats and the purse on GameInventory — all
            // resolved lazily for the same reason the character sheet's are (the player is assembled in
            // PlayerController.Awake, which is not ordered against this Start).
            var medPage = new PipPageMedView(BuildMedSource())
            {
                TextTable = _text,
                SubPageClicked = subPage => _manager.SubPageClick(subPage),

                // `pip.snd(1)` on every handled click (PipPageMed.as:446). The sound belongs to the
                // device, so the page raises it and the manager plays it.
                Clicked = () => _manager.Play(1),
            };

            medPage.Configure(_composition, _font, _text);
            _manager.RegisterPage(medPage);
            _medPage = medPage;

            RegisterStub(7, "PipPageWork — Item I12 (workbench)");
            RegisterStub(8, "PipPageApp — Item I12 (appearance)");
            RegisterStub(9, "PipPageVault — Item I12 (container)");

            // Built after every page is registered, so the device's first open can reveal any of them.
            inventoryPage.Build(_view.PageRoot);
            statPage.Build(_view.PageRoot);
            infoPage.Build(_view.PageRoot);
            vendPage.Build(_view.PageRoot);
            medPage.Build(_view.PageRoot);
        }

        /// <summary>
        /// Subscribes the device to <see cref="VendorOpenedMessage"/> and opens page 4 — the port of
        /// <c>NPC.pip(4)</c> (<c>NPC.as:250-260</c>).
        ///
        /// <para><b>What was dead.</b> The NPC ladder (<c>UnitNPCController.Interact</c>) already
        /// resolved a trader to <c>NpcActivationKind.Trade</c> and then reported it unwired; the page
        /// did not exist at all (it was a stub). This subscription is the edge between them, and the
        /// vendor is resolved from the campaign's registry rather than carried in the message, so a
        /// land rebuild's fresh vendors are what opens.</para>
        /// </summary>
        private void BindVendorPage()
        {
            if (_resolver == null)
            {
                Debug.LogWarning("[PipBuckBootstrapper] No container resolver — VendorOpenedMessage is " +
                                 "wired to nothing, so no trader NPC can open the trade page.");
                return;
            }

            if (!_resolver.TryResolve(out ISubscriber<VendorOpenedMessage> subscriber))
            {
                Debug.LogWarning("[PipBuckBootstrapper] No ISubscriber<VendorOpenedMessage> — a trader " +
                                 "will activate and open nothing. Check that " +
                                 "RegisterMessageBroker<VendorOpenedMessage> is still in the installer.");
                return;
            }

            _vendorSubscription = subscriber.Subscribe(message =>
            {
                CampaignManager campaign = CampaignManager.Current;
                VendorRuntime vendor = campaign?.Vendors.Get(message.VendorId);

                if (vendor == null)
                {
                    Debug.LogWarning($"[PipBuckBootstrapper] VendorOpenedMessage names '{message.VendorId}' " +
                                     "but the campaign has no such vendor. The NPC's `vendor` attribute and " +
                                     "the imported <vendor> blocks disagree — the shelf would be empty, so " +
                                     "the page is not opened.");
                    return;
                }

                if (_vendorPage == null)
                {
                    Debug.LogWarning("[PipBuckBootstrapper] The trade page is not built; cannot open page 4.");
                    return;
                }

                Debug.Log($"[PipBuckBootstrapper] Trade page opened for '{message.VendorId}' " +
                          $"(npc '{message.NpcId}', inter '{message.NpcInter}') — " +
                          $"{vendor.Buys.Count} shelf rows, vendor has {vendor.Money} caps.");

                // NPC.pip(4): set the three device fields, then onoff(4). The page's Open does the
                // vendor.reset() and the multPrice push that PipBuck.onoff/setPage do.
                _vendorPage.Open(vendor, message.NpcId, message.NpcInter);
                _manager.OnOff(PipPageVendView.VendorPageId);
            });
        }

        /// <summary>
        /// Subscribes the device to <see cref="ClinicOpenedMessage"/> and opens page 6 — the port of
        /// <c>NPC.pip(6)</c> (<c>NPC.as:250-260</c>), reached from <c>doc</c>/<c>vdoc</c>
        /// (<c>:217-220</c>) and from <c>adoc</c> past its first repair (<c>:227-230</c>).
        ///
        /// <para><b>What was dead.</b> The NPC ladder (<c>UnitNPCController.Interact</c>) resolved a
        /// doctor to <c>NpcActivationKind.Doctor</c> and then reported it unwired, and page 6 was a stub
        /// that said so. The player pressed the key and got a notice. The page and this edge are the
        /// answer.</para>
        ///
        /// <para><b>The vendor context is written first, and that is the Oracle's order.</b>
        /// <c>NPC.pip</c> sets <c>pip.vendor</c>/<c>npcId</c>/<c>npcInter</c> and <i>then</i> opens the
        /// page. A <c>doc</c> never reaches the vendor branch of <c>activate()</c>, so the Vendor tab
        /// (which lights on page 6, <c>PipBuck.as:222-225</c>) is its only road to its own shelf.</para>
        ///
        /// <para><b>It is an <i>open</i>, not a toggle</b> — same reasoning as the trade page:
        /// <c>page &gt; 0</c> is the open arm (<c>PipBuck.as:251-262</c>), and the interaction that
        /// publishes this cannot fire while the device is already up.</para>
        /// </summary>
        private void BindClinicPage()
        {
            if (_resolver == null)
            {
                Debug.LogWarning("[PipBuckBootstrapper] No container resolver — ClinicOpenedMessage is " +
                                 "wired to nothing, so no doctor NPC can open the clinic page.");
                return;
            }

            if (!_resolver.TryResolve(out ISubscriber<ClinicOpenedMessage> subscriber))
            {
                Debug.LogWarning("[PipBuckBootstrapper] No ISubscriber<ClinicOpenedMessage> — a doctor " +
                                 "will activate and open nothing. Check that " +
                                 "RegisterMessageBroker<ClinicOpenedMessage> is still in the installer.");
                return;
            }

            _clinicSubscription = subscriber.Subscribe(message =>
            {
                // `NPC.pip`'s context half, BEFORE the page opens: a doctor has no other route to its
                // own shelf, because `activate()` never reaches the vendor branch for `doc`/`vdoc`.
                // The Vendor tab lights on page 6 (PipBuck.as:222-225), so this context is what it
                // shows. `SetContext`, not `Open`: opening the clinic must not reset the doctor's
                // stock, which is `onoff(4)`'s job.
                if (_vendorPage != null && !string.IsNullOrEmpty(message.VendorId))
                {
                    VendorRuntime clinicVendor = CampaignManager.Current?.Vendors.Get(message.VendorId);
                    if (clinicVendor != null)
                    {
                        _vendorPage.SetContext(clinicVendor, message.NpcId, message.NpcInter);
                    }
                    else
                    {
                        Debug.LogWarning(
                            $"[PipBuckBootstrapper] ClinicOpenedMessage names vendor " +
                            $"'{message.VendorId}', but the campaign has no such vendor — the doctor's " +
                            "shelf will not be reachable from the Vendor tab. (The clinic itself still " +
                            "opens; `vendor='nope'` on the autodoc is the expected case.)");
                    }
                }

                if (_medPage == null)
                {
                    Debug.LogWarning("[PipBuckBootstrapper] The clinic page is not built; cannot open " +
                                     "page 6. A doctor will activate and show nothing.");
                    return;
                }

                // `npcInter` is what picks the purse (money / gel / good) and whether the Trading tab
                // exists at all (PipPageMed.as:48-57), so it is handed to the page rather than left to
                // the source to guess from the last NPC. The vendor id travels with it so a caps-paying
                // doctor's takings can be credited to the wallet the player later sells to
                // (PipPageMed.as:442-445) — and so that closing the clinic drops it.
                _medPage.Open(message.NpcId, message.NpcInter, message.VendorId);

                Debug.Log($"[PipBuckBootstrapper] Clinic page opened for npc '{message.NpcId}' " +
                          $"(inter '{message.NpcInter}', vendor '{message.VendorId}') — paid in " +
                          $"'{PlayerPipMedSource.CurrencyFor(message.NpcInter)}'. Its trade is reachable " +
                          "from the Vendor tab.");

                _manager.OnOff(ClinicPageId);
            });
        }

        /// <summary>
        /// The doctor's data, wired to the live player.
        ///
        /// <para><b>Every owner is resolved per read</b>, like the character sheet's, and for the same
        /// reason: the player is assembled in <c>PlayerController.Awake</c>.</para>
        ///
        /// <para><b>What is deliberately left unwired, and what that costs.</b>
        /// <c>ActionsBlockedProvider</c> stays null — nothing in the port opens the device during a
        /// cutscene, so the gate reads false; landing one changes this line and nothing else.
        /// <c>ClinicVendorProvider</c> resolves the doctor's own wallet from the campaign's registry,
        /// which is what makes a caps-paying doctor keep its takings
        /// (<c>PipPageMed.as:442-445</c>). <c>NpcInterProvider</c> reads the <i>page's</i> context, not
        /// the message's, because the page is the thing that outlives the message.</para>
        /// </summary>
        private IPipMedSource BuildMedSource()
        {
            return new PlayerPipMedSource(
                () => ResolvePlayer()?.CharacterStats,
                () => ResolvePlayer()?.Stats,
                ResolveInventory)
            {
                NpcInterProvider = () => _medPage?.Context.NpcInter ?? string.Empty,
                GlobalDifficultyProvider = ResolveDifficulty,
                ActionsBlockedProvider = null,

                // `gg.rad` — the clinic's radiation row. Resolved per read for the same reason as
                // everything else here: the player (and its radiation ticker) are built in Awake.
                RadiationProvider = () => ResolvePlayer()?.Radiation,

                // `pip.vendor` — the same registry the trade page reads, so the doctor's takings land in
                // the wallet the player can later sell to. Resolved from the PAGE, not from a cached id,
                // because the page drops its context when the device closes: a clinic the player walked
                // away from must not credit a doctor they are no longer standing in front of.
                ClinicVendorProvider = () =>
                {
                    string vendorId = _medPage?.ClinicVendorId;
                    return string.IsNullOrEmpty(vendorId)
                        ? null
                        : CampaignManager.Current?.Vendors.Get(vendorId);
                },
            };
        }

        /// <summary>
        /// Subscribes the device to <see cref="TravelMapOpenedMessage"/> and opens page 3 on its World
        /// sub-tab — the port of <c>pip.onoff(3, 3)</c> (<c>Interact.as:1636-1641</c>,
        /// <c>NPC.as:210-216</c>).
        ///
        /// <para><b>What was dead.</b> Both travel grants — the camp's wall map (<c>wmap</c>, an object
        /// with <c>allact='map'</c>) and an NPC whose <c>inter</c> is <c>travel</c> — were already wired
        /// end to end on the campaign side: <c>CampaignManager.GrantTravelAndOpenMap</c> unlocks the flag
        /// and publishes this message. Nothing consumed it, so pressing <c>E</c> on the wall map set a
        /// bool and drew no pixel anywhere. The page itself was already built
        /// (<see cref="PipPageInfoView"/>, registered above, with its <c>PipInfoWorldPanel</c> and the 27
        /// travel nodes); the message was the only missing edge.</para>
        ///
        /// <para><b>Why the sub-page is 3 and not 0.</b> <c>page2 = 0</c> means "leave the sub-page where
        /// it was" (<c>PipBuck.as:427-430</c>), which is what pressing <c>M</c> twice relies on. The
        /// Oracle's wall map names the tab explicitly, so a player who had been reading the bestiary must
        /// land on the world map, not back on the bestiary.</para>
        ///
        /// <para><b>Why it opens rather than toggles.</b> <c>page &gt; 0</c> is the "open" arm
        /// (<c>PipBuck.as:251-262</c>); <c>onoff(0)</c> would close the device if it happened to be open,
        /// and the interaction that publishes this is a world interaction, which cannot fire while the
        /// device is up.</para>
        /// </summary>
        private void BindTravelMap()
        {
            if (_resolver == null)
            {
                Debug.LogWarning("[PipBuckBootstrapper] No container resolver — TravelMapOpenedMessage is " +
                                 "wired to nothing, so the camp's wall map and every `travel` NPC will " +
                                 "grant travel and open no page.");
                return;
            }

            if (!_resolver.TryResolve(out ISubscriber<TravelMapOpenedMessage> subscriber))
            {
                Debug.LogWarning("[PipBuckBootstrapper] No ISubscriber<TravelMapOpenedMessage> — the wall " +
                                 "map will grant travel and draw nothing. Check that " +
                                 "RegisterMessageBroker<TravelMapOpenedMessage> is still in the installer.");
                return;
            }

            _travelMapSubscription = subscriber.Subscribe(message =>
            {
                Debug.Log($"[PipBuckBootstrapper] Travel map opened from '{message.CurrentLandId}' — " +
                          "showing PipBuck page 3, World sub-tab (pip.onoff(3,3)).");
                _manager?.OnOff(PipInfoFrame.InfoPageId, PipInfoFrame.WorldSubPage);
            });
        }

        /// <summary>
        /// Registers a not-yet-ported page and gives it its sub-tab row.
        ///
        /// <para><b>Why a stub gets a sub-tab row at all.</b> The row is the page's shape, and the
        /// Oracle derives it from the page's identifier alone (<c>PipPage.as:143-152</c>) — which
        /// pages have which sub-pages is knowable today, is already in the localisation document, and
        /// is the half of each page that a later work item must not be allowed to invent differently.
        /// Drawing it now is also what makes the device's navigation verifiable in a play-test while
        /// the contents are still missing. The shell's notice above it still says so.</para>
        /// </summary>
        private void RegisterStub(int pageId, string why)
        {
            var page = new PipStubPage(pageId, why);
            page.Configure(_composition, _font, _text);

            // The handler belongs to the device, not the page: PipPage.page2Click plays a device
            // sound (PipBuck.as:891) and, on the doctor and the vendor, opens a different page
            // entirely. See PipBuckManager.SubPageClick.
            page.SubTabs.SubPageClicked = subPage => _manager.SubPageClick(subPage);

            _manager.RegisterPage(page);
            page.Build(_view.PageRoot);
        }

        /// <summary>
        /// The player, or null while there is no player. Cached across calls; re-searched at
        /// <see cref="RescanInterval"/> until one appears.
        /// </summary>
        private PlayerController ResolvePlayer()
        {
            if (_player == null)
            {
                // UnityEngine.Time, fully qualified: a bare `Time` inside this namespace would bind to
                // whatever `Time` resolves to first, and the HudBootstrapper already records the trap
                // (PFE.Core contains a PFE.Core.Time namespace).
                if (UnityEngine.Time.unscaledTime < _nextRescanTime) return null;
                _nextRescanTime = UnityEngine.Time.unscaledTime + RescanInterval;

                _player = FindFirstObjectByType<PlayerController>(FindObjectsInactive.Include);
            }

            return _player;
        }

        /// <summary>
        /// The player's inventory, or null while there is no player.
        ///
        /// <para><b>Why a scene search and not an injected reference.</b> <c>GameInventory</c> is not
        /// in the container — it is created by <c>PlayerInventory.Construct</c>, which
        /// <c>PlayerController.Awake</c> triggers (<c>PlayerController.cs:399</c>), and that runs in
        /// an order Unity does not define against this bootstrapper's <c>Start</c>. Injecting it would
        /// mean registering the player's per-player inventory as a container singleton, which the
        /// inventory's own docs reject for co-op reasons (<c>PlayerInventory.cs:15-20</c>: a second
        /// player must get a second inventory). So the resolver walks to it, the same way
        /// <c>HudBootstrapper</c> and <c>DevConsoleInventoryCommands</c> do.</para>
        /// </summary>
        private GameInventory ResolveInventory()
        {
            PlayerInventory owner = ResolvePlayer()?.GetComponent<PlayerInventory>();
            return owner != null ? owner.Inventory : null;
        }

        /// <summary>
        /// The player's <see cref="PlayerInventory"/> itself — the component, not its bag.
        ///
        /// <para><b>Needed because <c>useItem</c> is not a bag operation.</b> Using an item reaches the
        /// character's stats, the spell book and the effect set, all of which live beside the inventory
        /// on the player; <see cref="ResolveInventory"/> hands out the dictionary and would leave a
        /// caller with no way back to those. Resolved per call for the same reason every other provider
        /// in this file is: the player is assembled in <c>PlayerController.Awake</c>, which is not
        /// ordered against this bootstrapper's <c>Start</c>.</para>
        /// </summary>
        private PlayerInventory ResolvePlayerInventory()
            => ResolvePlayer()?.GetComponent<PlayerInventory>();

        /// <summary>
        /// The equipped weapon's id — AS3 <c>gg.newWeapon.id</c> (<c>PipPageInv.as:391</c>, the selection
        /// frame <c>ramka</c>).
        ///
        /// <para>Read from the inventory rather than the loadout because the inventory owns the field
        /// (<c>GameInventory.CurrentWeaponId</c>) and <c>PlayerWeaponLoadout.Equip</c> is its one writer —
        /// so this is the same value the save format persists, and there is no second place to keep in
        /// step. Empty becomes null, which is the source's documented "no frame" input.</para>
        /// </summary>
        private string ResolveEquippedWeaponId()
        {
            string id = ResolveInventory()?.CurrentWeaponId;
            return string.IsNullOrEmpty(id) ? null : id;
        }

        /// <summary>
        /// The equipped weapon's loaded ammo base — AS3 <c>gg.currentWeapon.ammoBase</c>
        /// (<c>PipPageInv.as:286</c>, the ammo page's "the loaded round sorts first" nudge).
        ///
        /// <para><b>The port keeps one id for a loaded round where AS3 splits a base from a per-round
        /// type</b> (<c>GameWeaponInstance.LoadedAmmoType</c>), so that id is what is returned. The
        /// consumer compares it against both an item's own id and its <c>@base</c> — the same two-way test
        /// the Oracle makes (<c>ammoBase == @base || ammoBase == @id</c>) — so a round that is listed under
        /// its base still matches. Returning null (no weapon, or a weapon with no loaded round) disables
        /// the nudge, which is the property's documented behaviour.</para>
        /// </summary>
        private string ResolveLoadedAmmoBase()
        {
            GameInventory inventory = ResolveInventory();
            if (inventory == null) return null;

            string equipped = inventory.CurrentWeaponId;
            if (string.IsNullOrEmpty(equipped)) return null;

            string loaded = inventory.GetWeapon(equipped)?.LoadedAmmoType;
            return string.IsNullOrEmpty(loaded) ? null : loaded;
        }

        /// <summary>
        /// <c>World.w.hardInv</c> — gates the mass column (<c>PipPageInv.as:462-465</c>). False when there
        /// is no inventory yet, which is also the field's own default.
        /// </summary>
        private bool ResolveHardInventory() => ResolveInventory()?.HardInventory ?? false;

        // ── The character sheet's source (Item I10) ────────────────────────────

        /// <summary>
        /// The character sheet's data, wired to the live player.
        ///
        /// <para><b>Every owner is resolved per read.</b> The sheet reads three objects — the player's
        /// <c>CharacterStats</c>, its <c>UnitStats</c> and its <c>GameInventory</c> — and none of them
        /// exists when this runs; see <c>PlayerPipStatSource</c> for why a constructor-captured instance
        /// would show an empty sheet forever.</para>
        ///
        /// <para><b>What is deliberately left unwired, and what that costs.</b> Three providers stay
        /// null, each for a reason recorded on the provider itself:
        /// <c>ReputationProvider</c> (no reputation system — the row reads the Oracle's own starting 0),
        /// <c>GameTimeProvider</c> (the Oracle's clock is accumulated wall time, which the port does not
        /// track; a tick count is a different quantity), and <c>MedicineDisabledProvider</c> (no trigger
        /// table — the medical module is treated as intact, which is also what the status monitor
        /// assumes for the same five organs).</para>
        /// </summary>
        private IPipStatSource BuildStatSource()
        {
            return new PlayerPipStatSource(
                () => ResolvePlayer()?.CharacterStats,
                () => ResolvePlayer()?.Stats,
                ResolveInventory)
            {
                PlayerNameProvider = ResolvePlayerName,
                GlobalDifficultyProvider = ResolveDifficulty,
                ReputationProvider = null,
                GameTimeProvider = null,
                MedicineDisabledProvider = null,

                // `gg.rad` — the Health sub-page's radiation row. Resolved per read, like every other
                // owner on this seam.
                RadiationProvider = () => ResolvePlayer()?.Radiation,
            };
        }

        /// <summary>
        /// The name given at new-game time, from <c>GameBootData</c>.
        ///
        /// <para><b>Read once and cached, because the getter clones.</b>
        /// <c>GameBootData.PeekPendingNewGame()</c> returns a fresh <c>Clone()</c> of the whole settings
        /// object — appearance included — on every call, and the sheet rebuilds on every sub-tab press
        /// and every scroll step. The settings are written in the main menu before the game scene loads
        /// and never change afterwards, so one read is enough.</para>
        ///
        /// <para>The cache latches only once settings actually appear, so a boot order that puts this
        /// bootstrapper first is not fatal — it just leaves the row empty until they do, which is what
        /// the Oracle shows before a name has been given.</para>
        /// </summary>
        private string ResolvePlayerName()
        {
            if (_nameResolved) return _playerName;

            NewGameSettings settings = GameBootData.PeekPendingNewGame();
            if (settings == null) return _playerName;

            _playerName = settings.playerName ?? string.Empty;
            _nameResolved = true;
            return _playerName;
        }

        // ── The information page's source (Item I11) ──────────────────────────

        /// <summary>
        /// The live <see cref="LandMap"/>, through the bridge that owns the reference to it.
        ///
        /// <para><b>The same path the F2 debug overlay uses</b> (<c>PlayerDebugEditorOverlay.ResolveLandMap</c>),
        /// so the page's minimap and the overlay's cannot disagree about which map is live. Re-searched
        /// per read rather than cached: the bridge appears with the land, and a reference captured before
        /// it exists would stay null for the whole session — the same trap the player resolver's
        /// <see cref="RescanInterval"/> exists to avoid, without the player's "a respawn moves it"
        /// justification for a cheaper interval.</para>
        /// </summary>
        private LandMap ResolveLandMap()
        {
            MapBridge bridge = FindFirstObjectByType<MapBridge>();
            return bridge != null ? bridge.CurrentLandMap : null;
        }

        /// <summary>
        /// A trigger coerced to a number, with the travel ladder's absent-answer: 0. The bestiary's kill
        /// count calls the two-argument overload with -1 instead, because the Oracle's
        /// <c>triggers["frag_"+id] &gt;= 0</c> makes "absent" and "stored 0" different answers there.
        /// </summary>
        private int ResolveTriggerAsInt(string name) => ResolveTriggerAsInt(name, 0);

        private int ResolveTriggerAsInt(string name, int absent)
        {
            CampaignManager campaign = CampaignManager.Current;
            return campaign != null ? campaign.GetTrigger(name).AsInt(absent) : absent;
        }

        /// <summary>
        /// <c>Pers.skill[id]</c> — a skill gate's left-hand side. 0 while there is no player, which
        /// refuses every skill-gated vendor task rather than passing it.
        /// </summary>
        private int ResolveSkillLevel(string skillId)
        {
            var stats = ResolvePlayer()?.CharacterStats;
            return stats != null ? stats.GetSkillLevel(skillId) : 0;
        }

        /// <summary>
        /// <c>game.beginMission(targetLand)</c> (<c>PipPageInfo.as:762</c>) — the travel confirm's write
        /// half. True when the mission actually began, which is what lets the page raise
        /// <c>pip.onoff(-1)</c>; a refused travel (a land the mission system will not start) leaves the
        /// device open, which is the Oracle's own outcome for the same refusal.
        /// </summary>
        private bool BeginMission(string landId)
        {
            CampaignManager campaign = CampaignManager.Current;
            if (campaign == null)
            {
                Debug.LogWarning("[PipBuckBootstrapper] Cannot travel to '" + landId +
                                 "': there is no campaign to begin the mission in.");
                return false;
            }

            bool began = campaign.BeginMission(landId);
            if (began)
            {
                Debug.Log("[PipBuckBootstrapper] Began mission '" + landId +
                          "' from the PipBuck travel map (PipPageInfo.as:762).");
            }

            return began;
        }

        /// <summary>
        /// <c>game.addQuest(id, null, false, false, false)</c> (<c>PipPageInfo.as:781</c>) — the alltask
        /// button's grant. The page has already computed WHICH ids (see
        /// <see cref="PipQuestJournal.TasksToGrant"/>); this is the campaign write, so it lives with the
        /// other campaign writes rather than in the view.
        /// </summary>
        private void GrantQuests(System.Collections.Generic.IReadOnlyList<string> questIds)
        {
            CampaignManager campaign = CampaignManager.Current;
            if (campaign == null)
            {
                Debug.LogWarning("[PipBuckBootstrapper] Cannot grant " + questIds.Count +
                                 " vendor task(s): there is no campaign to grant them into.");
                return;
            }

            for (int i = 0; i < questIds.Count; i++)
            {
                string questId = questIds[i];
                if (string.IsNullOrEmpty(questId)) continue;

                campaign.AddQuest(questId);
                Debug.Log("[PipBuckBootstrapper] Granted vendor task '" + questId +
                          "' from the PipBuck quest list (PipPageInfo.as:781).");
            }
        }

        /// <summary>
        /// The run's difficulty as the Oracle's <c>globalDif</c> index.
        ///
        /// <para><b>The two numbering schemes already agree, so there is no translation to get wrong.</b>
        /// <c>NewGameDifficulty</c> runs 0..4 with <c>Normal = 2</c>; the Oracle's <c>globalDif</c> runs
        /// 0..4 with a default of 2 (<c>Game.as:41</c>), and the <c>gui</c> table's <c>dif0</c>..<c>dif4</c>
        /// — the very keys the row resolves against — are indexed by it directly.</para>
        ///
        /// <para><b>Read live from <c>GlobalDifficulty</c> rather than latched from the new-game
        /// settings.</b> The settings are only the value's <i>source at boot</i>; the oracle's
        /// <c>Game.globalDif</c> is mutable for the whole run — <c>changeDif</c> is called by items that
        /// author a <c>chdif</c> attribute (<c>Invent.as:714</c>) and by the console's <c>dif</c> verb
        /// (<c>Consol.as:237</c>). A latch taken here would keep showing the difficulty the run started
        /// at after either of those moved it, and the row is the only place the player can see the
        /// change. This is cheap to read (no clone, unlike <c>PeekPendingNewGame</c>) and the sheet
        /// already rebuilds on every sub-tab press and scroll step, so the row updates without a
        /// change-notification hook.</para>
        ///
        /// <para>The fields <c>_difficulty</c>/<c>_difficultyResolved</c> remain as the fallback the
        /// provider reads before the boot value has ever been resolved — <c>GlobalDifficulty.Value</c>
        /// itself resolves from <c>GameBootData</c>, so the two agree.</para>
        /// </summary>
        private int ResolveDifficulty()
        {
            _difficulty = PFE.Systems.Campaign.GlobalDifficulty.Value;
            return _difficulty;
        }

        // ── The status monitor's source ────────────────────────────────────────

        /// <summary>
        /// The 15 rows behind <see cref="PipStatusMonitorView"/>, wired to the live player.
        ///
        /// <para><b>Every input is a <see cref="Func{TResult}"/>.</b> The monitor refreshes from
        /// <c>LateUpdate</c> while the world is frozen, so it must read through to the player each
        /// time rather than through values captured at boot — see <c>PlayerPipStatusSource</c> for
        /// why a constructor-captured instance would show zeroes forever.</para>
        ///
        /// <para><b>What is deliberately not wired.</b> <c>WeaponConditionProvider</c> stays null, so
        /// the equipped weapon's condition row stays hidden. Its reading lives on the
        /// <c>GameWeaponInstance</c> inside the inventory's weapons dictionary, and the port cannot
        /// put a weapon in that dictionary: weapons are <c>WeaponDefinition</c> assets with no
        /// <c>ItemDefinition</c> row (see <see cref="StartingLoadout"/>). The alternative — reading a
        /// durability counter off the equipped controller — is a different quantity with the same
        /// units, and a row that guesses is worse than a row that is absent, which is the rule
        /// <c>PlayerPipStatusSource</c> is already written to follow.</para>
        /// </summary>
        private IPipStatusSource BuildStatusSource()
        {
            var source = new PlayerPipStatusSource(
                () => ResolvePlayer()?.CharacterStats,
                () => ResolvePlayer()?.Stats,
                ResolveInventory)
            {
                ArmourConditionProvider = ResolveArmourCondition,

                // Caps, read through the live inventory each refresh. AS3 reads `inv.money.kol`
                // (PipBuck.as:411 mirrors it into `pip.money`; :539 draws ritems[9]). The port keeps
                // caps as a count on GameInventory rather than as an item — the divergence is recorded
                // on GameInventory.Money — and PlayerInventory grants the oracle's starting 50
                // (Invent.as:1610). Reading through a Func means the row shows 0 only while there is no
                // player at all, which is PipBuck.as:51's own starting value rather than a guess.
                CapsProvider = ResolveCaps,

                // No trigger table wired yet; the default is "organs shown", which is what a fresh
                // game is.
                MedicineEnabledProvider = null,
            };

            return source;
        }

        /// <summary>
        /// The worn armour's condition for the monitor's row — the equipped armour's current and
        /// maximum integrity, or "no value" when nothing is worn.
        /// </summary>
        private (bool has, float hp, float max) ResolveArmourCondition()
        {
            GameInventory inventory = ResolveInventory();
            if (inventory == null) return (false, 0f, 0f);

            string id = inventory.CurrentArmorId;
            if (string.IsNullOrEmpty(id)) return (false, 0f, 0f);

            foreach (System.Collections.Generic.KeyValuePair<string, GameArmorInstance> pair in inventory.Armors)
            {
                if (pair.Key != id) continue;

                GameArmorInstance armour = pair.Value;
                if (armour == null) return (false, 0f, 0f);

                return (true, armour.CurrentHealth, armour.MaxHealth);
            }

            return (false, 0f, 0f);
        }

        /// <summary>
        /// The player's caps for the monitor's row 9 — AS3 <c>inv.money.kol</c>, which
        /// <c>PipBuck.setPage</c> mirrors into <c>pip.money</c> (<c>PipBuck.as:411</c>) and
        /// <c>setRPanel</c> draws (<c>:539</c>).
        ///
        /// <para>Reads through the live inventory each refresh rather than capturing a balance at boot,
        /// for the same reason every other provider here is a <see cref="Func{TResult}"/>: the player —
        /// and therefore the inventory — does not exist yet when this source is built.</para>
        /// </summary>
        private int ResolveCaps()
        {
            GameInventory inventory = ResolveInventory();
            return inventory != null ? inventory.Money : 0;
        }

        // ── Inventory row activation ─────────────────────────────────────────
        //
        // PipPageInv.itemClick (PipPageInv.as:532-617): a weapon row equips it, an armour row wears
        // it, and everything else uses it. All three branches are ported; the third one had been
        // missing since the page was written, which is why every click on a stim or a spell logged
        // "the port has no inventory command for it".

        /// <summary>
        /// Performs the Oracle's left-click action for a row, or reports that it could not.
        /// Returning <c>false</c> leaves the click to <see cref="PipPageInvView"/>'s own report.
        /// </summary>
        private bool ActivateInventoryRow(PipInventoryRow row)
        {
            if (row.Kind == PipInventoryKind.Weapon) return EquipWeaponRow(row);
            if (row.Kind == PipInventoryKind.Armor) return WearArmourRow(row);

            return UseInventoryRow(row);
        }

        /// <summary>
        /// <c>PipPageInv.as:552-559</c> — the armour branch, <c>World.w.gg.changeArmor(id)</c>.
        /// </summary>
        private bool WearArmourRow(PipInventoryRow row)
        {
            GameInventory inventory = ResolveInventory();
            if (inventory == null)
            {
                Debug.LogWarning("[PipBuckBootstrapper] Cannot wear '" + row.Id + "': there is no " +
                                 "player inventory to read it from.");
                return false;
            }

            if (!inventory.Armors.TryGetValue(row.Id, out GameArmorInstance armour) || armour == null)
            {
                Debug.LogWarning("[PipBuckBootstrapper] Cannot wear '" + row.Id + "': the inventory " +
                                 "holds no armour row for it (PipPageInv.as:552-559).");
                return false;
            }

            PlayerController player = ResolvePlayer();
            if (player?.Stats == null)
            {
                Debug.LogWarning("[PipBuckBootstrapper] Cannot wear '" + row.Id + "': the player has " +
                                 "no UnitStats to equip onto.");
                return false;
            }

            // UnitStats.EquipArmour is the port's equip seam: it stores the backing item, so the
            // condition the monitor reads and the row that was clicked stay the same instance.
            player.Stats.EquipArmour(armour);

            // PipPageInv.as:552-559 — the Oracle's own bookkeeping, which is also what makes the row
            // draw its `ramka` selection frame (PipPageInv.as:391-395) and what the monitor's armour
            // condition row resolves against. Both halves, or the two disagree on what is worn.
            inventory.CurrentArmorId = row.Id;

            // Re-read the page so the selection frame and the equipped tint follow immediately;
            // a sub-tab press is the only other thing that repaints.
            ResolveInventoryPage()?.SetStatus(0);

            Debug.Log("[PipBuckBootstrapper] Wore '" + row.Id + "' (" + row.Name + ") from the " +
                      "PipBuck inventory page (PipPageInv.as:552-559).");

            return true;
        }

        /// <summary>
        /// <c>PipPageInv.as:596</c> — <c>World.w.invent.useItem(id)</c>: the Aid and Misc pages, and
        /// anything on Equipment that is neither a weapon nor armour.
        ///
        /// <para><b>The page is re-read after a successful use.</b> A potion changes the health readout
        /// and the row's stack count; a spell changes the selection frame; a skill book changes the
        /// character sheet. All of those are drawn from the page's own snapshot, so a use that did not
        /// repaint would leave the list showing a row the player just consumed — the same one-tick
        /// staleness the armour branch above already avoids with its <c>SetStatus(0)</c>.</para>
        /// </summary>
        private bool UseInventoryRow(PipInventoryRow row)
        {
            PlayerInventory playerInventory = ResolvePlayerInventory();
            if (playerInventory == null)
            {
                Debug.LogWarning("[PipBuckBootstrapper] Cannot use '" + row.Id + "': there is no " +
                                 "player inventory to use it from.");
                return false;
            }

            // The Oracle's guards that live on the device rather than on the item: `pip.noAct` is
            // checked by the page before this is reached, so only the world state is left to push.
            playerInventory.AtBaseProvider ??= ResolveInBase;

            if (!playerInventory.UseItem(row.Id)) return false;

            // `setStatus(false)` — a full rebuild, because a use can change the list itself.
            ResolveInventoryPage()?.SetStatus(0);
            return true;
        }

        /// <summary>
        /// <c>World.w.loc.base</c> — whether the current land is a base. Read lazily, like every other
        /// provider here, because the land does not exist when the bootstrapper builds.
        /// </summary>
        private bool ResolveInBase()
        {
            CampaignManager campaign = CampaignManager.Current;
            return campaign != null && campaign.IsCurrentLandBase;
        }

        /// <summary>
        /// <c>PipPageInv.itemClick</c>'s weapon branch — <c>PipPageInv.as:545-551</c>:
        /// <c>World.w.gg.changeWeapon(id)</c>, the same call the favourite hotkeys and the number keys
        /// make. The port's counterpart is <c>PlayerWeaponLoadout.Equip</c>, reached with the row's
        /// <c>WeaponDefinition</c>.
        ///
        /// <para><b>This is the other half of "F2 gives, the PipBuck equips".</b> The F2 Weapons tab now
        /// puts a weapon into the bag instead of only putting it in the hand, and this is where the hand
        /// half is reached from — through the row, which is the Oracle's own path, rather than through a
        /// debug button that would leave the bag and the hand disagreeing again.</para>
        ///
        /// <para><b>Why the row's id is enough.</b> It resolves through the same <c>ContentType.Weapon</c>
        /// table the inventory's own sink resolves against, so a row that is listed can always be
        /// equipped: there is one table and both read it, and the two cannot disagree about which weapons
        /// exist.</para>
        /// </summary>
        private bool EquipWeaponRow(PipInventoryRow row)
        {
            PlayerController player = ResolvePlayer();
            if (player == null)
            {
                Debug.LogWarning("[PipBuckBootstrapper] Cannot equip '" + row.Id + "': there is no " +
                                 "player (PipPageInv.as:545-551).");
                return false;
            }

            var loadout = player.GetComponent<PFE.Systems.Weapons.PlayerWeaponLoadout>();
            if (loadout == null)
            {
                Debug.LogWarning("[PipBuckBootstrapper] Cannot equip '" + row.Id + "': the player has " +
                                 "no PlayerWeaponLoadout to equip onto (PipPageInv.as:545-551).");
                return false;
            }

            // Resolved through the inventory's own registry wrapper rather than ContentRegistry directly,
            // so the weapon table has one reader here as well as one writer there.
            WeaponDefinition definition = player.GetComponent<PlayerInventory>()?.ResolveWeapon(row.Id);
            if (definition == null)
            {
                Debug.LogWarning("[PipBuckBootstrapper] Cannot equip '" + row.Id + "': no weapon row " +
                                 "resolves for that id (PipPageInv.as:545-551).");
                return false;
            }

            loadout.Equip(definition);

            // PipPageInv.as:547 — `setStatus(false)`, a repaint that keeps the scroll. 0 is the Oracle's
            // "leave the sub-page where it is" (see PipPageInvView.SetStatus), and the selection frame
            // follows because PlayerWeaponLoadout.Equip writes GameInventory.CurrentWeaponId.
            ResolveInventoryPage()?.SetStatus(0);

            Debug.Log("[PipBuckBootstrapper] Equipped '" + row.Id + "' (" + row.Name + ") from the " +
                      "PipBuck inventory page (PipPageInv.as:545-551).");

            return true;
        }

        /// <summary>
        /// The registered inventory page, for the repaint after an action. Null only if the page was
        /// never registered, which <see cref="Build"/> cannot get past.
        /// </summary>
        private PipPageInvView ResolveInventoryPage() => _manager.PageFor(PipPageInvView.InventoryPageId)
            as PipPageInvView;

        /// <summary>
        /// The info pane's definition lookup — <c>pip.arrWeapon[id]</c> (<c>PipPage.as:423</c>).
        /// Resolved through the same <c>PlayerInventory.ResolveWeapon</c> table the equip path uses,
        /// so the pane and the click cannot disagree about which weapons exist.
        ///
        /// <para><b>Why this is a method and not an inline lambda.</b> The row id may carry a variant
        /// suffix (<c>id^2</c>); the registry is keyed on the base id, so the suffix is stripped here —
        /// one place, documented, rather than inside a lambda where it would be invisible.</para>
        /// </summary>
        private WeaponDefinition ResolveWeaponDefinition(string weaponId)
        {
            if (string.IsNullOrEmpty(weaponId)) return null;

            string id = weaponId;
            int caret = id.IndexOf('^');
            if (caret >= 0) id = id.Substring(0, caret);

            PlayerController player = ResolvePlayer();
            return player != null
                ? player.GetComponent<PlayerInventory>()?.ResolveWeapon(id)
                : null;
        }

        /// <summary>
        /// Resolves an armour id to its live instance — AS3 <c>inv.armors[id]</c>
        /// (<c>PipPage.as:600</c>).
        ///
        /// <para><b>An instance, not a definition</b>, because the armour stat block reads the ratings
        /// at the item's upgrade level (<c>Armor.getXmlParam(xml.upd[lvl])</c>) and only the instance
        /// knows its level. <c>GameInventory.GetArmor</c> is the one reader.</para>
        ///
        /// <para><b>One sub-gap named.</b> The Oracle falls back to <c>pip.arrArmor[id]</c>
        /// (<c>PipPage.as:602-604</c>) — the repair screen's list of every armour in the world,
        /// including ones the player does not carry. The port has no such list, so a miss here returns
        /// null and the pane falls through to the item block. That is reachable only for an armour row
        /// the player does not own, which the inventory page cannot list anyway.</para>
        /// </summary>
        private GameArmorInstance ResolveArmorInstance(string armorId)
        {
            if (string.IsNullOrEmpty(armorId)) return null;
            return ResolveInventory()?.GetArmor(armorId);
        }

        /// <summary>
        /// The inventory page's bottom strip — the Oracle's <c>PipPageInv.showBottext</c>
        /// (<c>PipPageInv.as:321-343</c>).
        /// </summary>
        private PipInventoryFooter BuildInventoryFooter()
        {
            GameInventory inventory = ResolveInventory();
            if (inventory == null) return default;

            return new PipInventoryFooter
            {
                // PipPageInv.as:323 — `Res.pipText("caps") + ": " + yel(pip.money)`.
                //
                // **Money is read, not zeroed.** This used to be a literal 0 with the note that the port
                // did not track caps anywhere; that stopped being true when GameInventory gained Money —
                // PlayerInventory seeds it with the Oracle's starting 50 (Invent.as:1610) and the status
                // monitor's row 9 already reads it through ResolveCaps. The play-test showed the
                // disagreement directly: the right-hand panel read `Caps 50` while this strip read `0`.
                // Read through the same accessor, so the two cannot drift apart again.
                Money = ResolveCaps(),

                HardInventory = inventory.HardInventory,

                // The word before the total. showBottext writes it as part of the same assignment, so
                // omitting it is a different line, not a shorter one.
                CapsLabel = _text.Pip("caps"),

                // inv.retMass(cat) — GameInventory.GetCategoryMass is the port of it, and it already
                // clamps an out-of-range category rather than throwing.
                CategoryMass = inventory.GetCategoryMass,
            };
        }

        /// <summary>
        /// The Oracle's hotkey block (<c>World.as:1321-1376</c>).
        ///
        /// <para>Every one of the seven is gated on <c>!this.sats.active</c> in the Oracle — S.A.T.S.
        /// owns the screen while it is up, so no PipBuck key may fire. The port has no S.A.T.S. yet
        /// (Item I14), so the gate is <see cref="IsSatsActive"/> returning a constant false. It is a
        /// method rather than an inlined <c>false</c> so that landing I14 changes one place.</para>
        ///
        /// <para>Note the sub-pages. <c>keyMed</c> (L) is <c>onoff(1,5)</c> — the character sheet's
        /// self-treatment sub-page, <b>not</b> a medical page; page 6 is the doctor's contextual
        /// modal and has no hotkey. <c>keyQuest</c> (J) is <c>onoff(3,2)</c> and <c>keyMap</c> (M) is
        /// <c>onoff(3,1)</c>.</para>
        /// </summary>
        private void Update()
        {
            if (_input == null || _manager == null) return;

            if (IsSatsActive()) return;

            if (Poll(_input.PipToggle)) _manager.OnOff();            // World.as:1321-1329
            if (Poll(_input.PipInventory)) _manager.OnOff(2);        // World.as:1331-1339
            if (Poll(_input.PipStatus)) _manager.OnOff(1, 1);        // World.as:1341-1349
            if (Poll(_input.PipSkills)) _manager.OnOff(1, 2);        // World.as:1351-1359 is keyMed
            if (Poll(_input.PipMedicine)) _manager.OnOff(1, 5);      // World.as:1353-1359
            if (Poll(_input.PipMap)) _manager.OnOff(3, 1);           // World.as:1361-1369
            if (Poll(_input.PipQuests)) _manager.OnOff(3, 2);        // World.as:1369-1376
        }

        private static bool Poll(UnityEngine.InputSystem.InputAction action)
        {
            return action != null && action.WasPressedThisFrame();
        }

        /// <summary>
        /// <c>World.sats.active</c> — always false until Item I14 lands. See <see cref="Update"/>.
        /// </summary>
        private static bool IsSatsActive()
        {
            return false;
        }

        /// <summary>
        /// The Oracle's interface font, from the composition asset.
        ///
        /// <para>Falls back to the HUD's OS-font path only if the composition's font is missing, and
        /// logs it — a null font renders no text at all, silently, which for a device whose entire
        /// content is text is the worst possible failure.</para>
        /// </summary>
        private Font ResolveFont()
        {
            if (_composition != null && _composition.terminalFont != null)
            {
                return _composition.terminalFont;
            }

            Debug.LogWarning("[PipBuckBootstrapper] The composition has no terminalFont " +
                             "(Lucida Console). Falling back to an OS font — the PipBuck will not " +
                             "render in its authentic typeface.");
            return Font.CreateDynamicFontFromOSFont(
                new[] { "Lucida Console", "Consolas", "Courier New", "DejaVu Sans Mono" }, 16);
        }

        private void OnDestroy()
        {
            // The manager holds the world paused while the device is open. Destroying the bootstrapper
            // with the device open would otherwise leave the simulation frozen with nothing to explain
            // it, so the hold is released explicitly as well as in the manager's own Dispose.
            _manager?.PauseGate?.Release(_manager);

            // The broker outlives this object, so an un-disposed subscription would keep a delegate
            // pointing at a destroyed bootstrapper alive for the rest of the session.
            _travelMapSubscription?.Dispose();
            _travelMapSubscription = null;

            _vendorSubscription?.Dispose();
            _vendorSubscription = null;

            _clinicSubscription?.Dispose();
            _clinicSubscription = null;
        }
    }
}
