using UnityEngine;
using VContainer;
using PFE.Core;
using PFE.Core.Input;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Systems.Inventory;
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
        /// How long to wait before re-searching the scene for the player. The same interval and the
        /// same reasoning as <c>HudBootstrapper.RescanInterval</c>: a respawn or a late-spawned
        /// player is picked up within half a second, whereas searching every read would be a full
        /// scene walk per frame for as long as the player is absent.
        /// </summary>
        private const float RescanInterval = 0.5f;

        [Inject]
        public void Construct(InputReader input, PipBuckManager manager)
        {
            _input = input;
            _manager = manager;
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
            // ── Page 2: the inventory (Item I9) ───────────────────────────────
            //
            // The source is built from a lazy resolver rather than an injected GameInventory, because
            // the inventory lives on the player and the player is assembled in PlayerController.Awake
            // — see ResolveInventory. The page therefore shows an empty list until the player exists,
            // which is honest, rather than binding null once and staying empty forever.
            var inventoryPage = new PipPageInvView(
                _manager.ViewModel,
                new PlayerPipInventorySource(ResolveInventory))
            {
                FooterProvider = BuildInventoryFooter,

                // The Oracle's captions for the sub-tabs (inv1..inv5), the six column headings
                // (ii1..ii8) and the empty-list notice (emptyinv).
                TextTable = _text,

                // The Oracle's left-click action, for the one kind of row the port can act on.
                // See ActivateInventoryRow.
                RowActivated = ActivateInventoryRow,
            };

            inventoryPage.Configure(_composition, _font);
            _manager.RegisterPage(inventoryPage);

            // ── The other nine: sub-tab rows only (see RegisterStub) ──────────
            //
            // Named individually rather than looped so the log states which work item each one
            // belongs to, instead of a count.
            RegisterStub(1, "PipPageStat — Item I10 (character sheet, skills, perks, effects, health)");
            RegisterStub(3, "PipPageInfo — Item I11 (map, quests, world, notes, enemies)");
            RegisterStub(4, "PipPageVend — Item I12 (merchant)");
            RegisterStub(5, "PipPageOpt — options / save / load");
            RegisterStub(6, "PipPageMed — Item I12 (doctor)");
            RegisterStub(7, "PipPageWork — Item I12 (workbench)");
            RegisterStub(8, "PipPageApp — Item I12 (appearance)");
            RegisterStub(9, "PipPageVault — Item I12 (container)");

            // Built after every page is registered, so the device's first open can reveal any of them.
            inventoryPage.Build(_view.PageRoot);
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

                // Left unset on purpose: the port does not track money, and the Oracle's caps row
                // reads its own starting value of 0 (PipBuck.as:51) rather than a made-up number.
                // See GameInventory.cs:71-73 for what the category means.
                CapsProvider = null,

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

        // ── Inventory row activation ─────────────────────────────────────────
        //
        // PipPageInv.itemClick (PipPageInv.as:532-617): a weapon row equips it, an armour row wears
        // it, and everything else uses it. Only the armour branch has a port it can actually reach,
        // so the other two return false and let the page report what the Oracle would have done.

        /// <summary>
        /// Performs the Oracle's left-click action for a row, or reports that it could not.
        /// Returning <c>false</c> leaves the click to <see cref="PipPageInvView"/>'s own report.
        /// </summary>
        private bool ActivateInventoryRow(PipInventoryRow row)
        {
            if (row.Kind != PipInventoryKind.Armor) return false;

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
        /// The registered inventory page, for the repaint after an action. Null only if the page was
        /// never registered, which <see cref="Build"/> cannot get past.
        /// </summary>
        private PipPageInvView ResolveInventoryPage() => _manager.PageFor(PipPageInvView.InventoryPageId)
            as PipPageInvView;

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
                // Money is 0 because the port does not track it anywhere: GameInventory's category
                // table documents index 0 as "Not tracked (money, special)"
                // (GameInventory.cs:71-73), and PlayerPipStatusSource records the same gap for the
                // status monitor's caps row. Zero is also the Oracle's own starting value
                // (PipBuck.as:51), so this is not a fabricated reading.
                Money = 0,

                HardInventory = inventory.HardInventory,

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
        }
    }
}
