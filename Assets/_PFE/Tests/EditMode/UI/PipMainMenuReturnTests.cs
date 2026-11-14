using System.IO;
using NUnit.Framework;
using PFE.Core;
using PFE.Tests.Editor.Common;
using PFE.UI.PipBuck;
using PFE.UI.PipBuck.Pages;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins the port's one way out of a session: the PipBuck tab the Oracle captions <b>"Main menu"</b>
    /// (<c>main5</c>, <c>PipBuck.as:172</c>) and the request it raises.
    ///
    /// <para><b>What this guards.</b> Three things that are invisible in a screenshot and each of which
    /// fails differently:</para>
    /// <list type="number">
    /// <item>The device must <b>close before</b> the scene load starts. It holds
    /// <see cref="InterfacePauseGate"/> while open, so a load that ran first would take the world down
    /// frozen — and if the load failed, the player would be in the wasteland with a frozen simulation
    /// and nothing on screen to explain it.</item>
    /// <item>The request must fire <b>once</b>. A page whose button can be pressed twice, or a manager
    /// that raises on a refused call, queues a second load behind the first.</item>
    /// <item>The scene name must be the one that ships. A rename that the build settings do not follow
    /// fails at the exact moment the player has already lost the session.</item>
    /// </list>
    ///
    /// <para><b>Why the manager and not the page.</b> <see cref="PipPageMainMenuView"/> is plumbing: its
    /// <c>_root</c> is a <see cref="RectTransform"/> and <c>Build</c> is the only thing that creates
    /// one, which is an ECall an offline fixture cannot make. The ordering that matters lives in
    /// <see cref="PipBuckManager.RequestMainMenu"/>, which is pure logic and is therefore what this
    /// fixture holds still. What <i>can</i> be asserted about the page without an engine — its id, its
    /// implemented flag and its scene name — is asserted too.</para>
    /// </summary>
    [TestFixture]
    public class PipMainMenuReturnTests
    {
        private PipBuckManager _manager;
        private InterfacePauseGate _pause;
        private int _raised;

        /// <summary>
        /// Whether the device was still open <b>at the moment the request fired</b>. The end state
        /// cannot answer that: a manager that raised the event and then closed the device would leave
        /// the same state behind, and the subscriber would have started a scene load while the world
        /// was still suspended.
        /// </summary>
        private bool _openWhenRaised;

        [SetUp]
        public void SetUp()
        {
            // A null sound service is deliberate and safe: PipBuckManager.Play returns immediately on
            // one, so the fixture does not need an audio engine to close the device.
            _pause = new InterfacePauseGate();
            _manager = new PipBuckManager(new PipBuckViewModel(), null, _pause);
            _raised = 0;
            _openWhenRaised = true;

            _manager.MainMenuRequested += () =>
            {
                _raised++;
                _openWhenRaised = _manager.IsOpen;
            };
        }

        [TearDown]
        public void TearDown()
        {
            _manager?.ViewModel?.Dispose();
        }

        /// <summary>
        /// The shipped path: open the device on page 5, press the button. The device must be shut by
        /// the time the subscriber runs, and the world gate must be free.
        /// </summary>
        [Test]
        public void RequestMainMenu_WhileOpen_ClosesTheDeviceBeforeAsking()
        {
            _manager.OnOff(PipPageMainMenuView.MainMenuPageId);

            Assert.That(_manager.IsOpen, Is.True, "Precondition: the device is open on page 5.");
            Assert.That(_pause.IsWorldPaused, Is.True,
                "Precondition: an open device holds the world suspended.");

            bool accepted = _manager.RequestMainMenu();

            Assert.That(accepted, Is.True, "A request from an open device must be accepted.");
            Assert.That(_openWhenRaised, Is.False,
                "The request fired while the device was still open, so the subscriber began the scene " +
                "load with the world suspended. The close has to come first.");
            Assert.That(_manager.IsOpen, Is.False,
                "The device stayed open while the scene was about to unload.");
            Assert.That(_pause.IsWorldPaused, Is.False,
                "The world gate is still held. A failed load would leave the simulation frozen with the " +
                "device gone — nothing on screen to explain it.");
        }

        /// <summary>The request reaches its one subscriber exactly once.</summary>
        [Test]
        public void RequestMainMenu_RaisesTheRequestExactlyOnce()
        {
            _manager.OnOff(PipPageMainMenuView.MainMenuPageId);

            _manager.RequestMainMenu();

            Assert.That(_raised, Is.EqualTo(1), "The return request must fire once per accepted press.");
        }

        /// <summary>
        /// A second press — the button is disabled in the view, but the manager is the guard — is
        /// refused, because the device it was pressed on is already closed. Without this the load
        /// queue could hold two requests and the second would run against a destroyed scene.
        /// </summary>
        [Test]
        public void RequestMainMenu_AfterTheDeviceClosed_IsRefused()
        {
            _manager.OnOff(PipPageMainMenuView.MainMenuPageId);
            _manager.RequestMainMenu();

            bool accepted = _manager.RequestMainMenu();

            Assert.That(accepted, Is.False, "A closed device cannot ask to leave the session.");
            Assert.That(_raised, Is.EqualTo(1),
                "The refused call still raised the request, so two loads were queued.");
        }

        /// <summary>A request from a device that was never opened is refused, and silent.</summary>
        [Test]
        public void RequestMainMenu_WhenTheDeviceWasNeverOpened_IsRefused()
        {
            bool accepted = _manager.RequestMainMenu();

            Assert.That(accepted, Is.False);
            Assert.That(_raised, Is.EqualTo(0),
                "A request from no device at all ended the session — no player input asked for that.");
        }

        /// <summary>
        /// The key-capture popup owns the keyboard while it is up (<c>Ctr.as:718</c>), and the two
        /// other entry points already refuse in that state — <c>pageClick</c> and <c>pipClose</c>
        /// (<c>PipBuck.as:184-191</c>, <c>:198-205</c>). This one must agree with them, or the button
        /// would be the only control in the device that works while a key is being bound.
        /// </summary>
        [Test]
        public void RequestMainMenu_WhileAKeyIsBeingCaptured_IsRefused()
        {
            _manager.OnOff(PipPageMainMenuView.MainMenuPageId);
            _manager.ViewModel.ReqKey.Value = true;

            bool accepted = _manager.RequestMainMenu();

            Assert.That(accepted, Is.False);
            Assert.That(_raised, Is.EqualTo(0), "The key-capture state did not gate the request.");
            Assert.That(_manager.IsOpen, Is.True,
                "A refused request must not close the device — the player is still binding a key.");
        }

        /// <summary>
        /// The page sits in the Oracle's slot 5, the one tab that is permanently captioned
        /// <c>main5</c>. Asserted against the literals, not against
        /// <see cref="PipBuckPages.LabelKeyFor"/> — a test that compares a symbol to itself cannot see
        /// it change.
        /// </summary>
        [Test]
        public void Page_IsTheFifthTopLevelTab()
        {
            Assert.That(PipPageMainMenuView.MainMenuPageId, Is.EqualTo(5));
            Assert.That(PipBuckPages.LabelKeyFor(5), Is.EqualTo("main5"));
            Assert.That(new PipPageMainMenuView().PageId, Is.EqualTo(5));
        }

        /// <summary>
        /// The page must report itself implemented, or <c>PipBuckView.RefreshNotice</c> draws
        /// "PAGE 5 (OPT) HAS NO CONTENTS YET" over a prompt that works — the exact screen this work
        /// item replaces.
        /// </summary>
        [Test]
        public void Page_ReportsItselfImplemented()
        {
            Assert.That(new PipPageMainMenuView().IsImplemented, Is.True,
                "The shell will cover the return prompt with its 'no contents' notice.");
        }

        /// <summary>
        /// The scene name, pinned twice: against the literal that <c>SceneLoader</c> receives, and
        /// against the scene asset on disk. Either half alone is not a guard — the literal agrees with
        /// itself, and a file check passes for a scene the build settings do not contain.
        ///
        /// <para><b>The path is found through <see cref="SourceLint.ProjectRoot"/>, not
        /// <c>Application.dataPath</c>.</b> The latter is a Unity ECall and throws in the offline
        /// harness this project verifies with, which would make the check unrunnable exactly where it
        /// is needed.</para>
        /// </summary>
        [Test]
        public void SceneName_IsTheShippedMainMenuScene()
        {
            Assert.That(PipPageMainMenuView.MainMenuSceneName, Is.EqualTo("MainMenuScene"));

            string scenePath = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Scenes",
                PipPageMainMenuView.MainMenuSceneName + ".unity");

            Assert.That(File.Exists(scenePath), Is.True,
                $"No scene asset at '{scenePath}'. The 'Main menu' tab would load nothing and leave " +
                "the player in the wasteland with the device closed.");
        }
    }
}
