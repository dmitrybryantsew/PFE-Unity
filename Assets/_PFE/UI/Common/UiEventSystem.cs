using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace PFE.UI.Common
{
    /// <summary>
    /// Creates the gameplay scene's uGUI <see cref="EventSystem"/> when the scene has none.
    ///
    /// <para><b>Why the gameplay scene needs a standing guarantee at all.</b>
    /// <c>Assets/_PFE/Scenes/SampleScene.unity</c> contains <b>no EventSystem object</b> — the string
    /// does not occur in the file. <c>MainMenuScene.unity</c> has one, carrying an
    /// <see cref="InputSystemUIInputModule"/>, so the convention exists and the main menu behaves.
    /// The gameplay scene simply never got one, and every uGUI object it creates at runtime is
    /// therefore inert.</para>
    ///
    /// <para><b>A <c>GraphicRaycaster</c> without an EventSystem does nothing.</b> A raycast hit has to
    /// be turned into a pointer event by an input module before any <c>Button</c> or
    /// <c>IPointerClickHandler</c> can see it. <c>PipBuckBootstrapper</c> puts a
    /// <c>GraphicRaycaster</c> on the device's canvas, so the raycasts happen — and then go nowhere.
    /// The screen looks exactly like a working device that ignores the mouse.</para>
    ///
    /// <para><b>The module is chosen by the project setting, not by taste.</b>
    /// <c>ProjectSettings.asset</c> sets <c>activeInputHandler: 2</c> — the Input System package
    /// <b>only</b> — and <c>com.unity.inputsystem</c> 1.18.0 is in <c>Packages/manifest.json</c>. A
    /// <c>StandaloneInputModule</c> on this project throws
    /// <c>InvalidOperationException</c> on its first read, so it would turn "the buttons are dead"
    /// into "the buttons are dead and the console is full". The module the main-menu scene already
    /// uses is the right one here too.</para>
    ///
    /// <para><b>Not authored into the scene.</b> The gameplay scene's objects are created at runtime
    /// by <c>GameLifetimeScope</c> — the HUD, the developer console, the save hotkeys and the PipBuck
    /// itself are all built from code against data that only exists at runtime. Adding an EventSystem
    /// to the scene file would be the one static object in a scene that is otherwise assembled, and
    /// scene edits are the owner's call. One <c>Ensure()</c> gives the same result without touching it.</para>
    /// </summary>
    public static class UiEventSystem
    {
        /// <summary>
        /// Creates an <see cref="EventSystem"/> if and only if the active scene has none.
        /// Safe to call from every bootstrapper; only the first call creates anything.
        /// </summary>
        /// <returns><c>true</c> when this call created the EventSystem; <c>false</c> when one was already there.</returns>
        public static bool Ensure()
        {
            // `current` is assigned by the EventSystem in OnEnable, so a live one answers it. The
            // FindFirstObject call covers the case that matters: a scene object that exists but is
            // disabled — `current` would be null, and adding a second EventSystem would make Unity
            // log "There can be only one active Event System" without fixing anything.
            if (EventSystem.current != null) return false;
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>(FindObjectsInactive.Include) != null)
            {
                return false;
            }

            var go = new GameObject("EventSystem",
                typeof(EventSystem),
                typeof(InputSystemUIInputModule));

            Debug.Log("[UiEventSystem] Created the gameplay scene's EventSystem. It was not in " +
                      "SampleScene.unity, so every uGUI button in this scene — the PipBuck's tabs, " +
                      "its sub-tabs, its close button and its inventory rows — was inert until now.");

            return true;
        }
    }
}
