using System;
using UnityEngine;

namespace PFE.Core.Input
{
    [CreateAssetMenu(fileName = "PfeInputSettings", menuName = "PFE/Input Settings")]
    public sealed class PfeInputSettings : ScriptableObject
    {
        [Header("Movement")]
        public MoveBindings move = new MoveBindings();

        [Header("Actions")]
        public ButtonBinding jump     = new ButtonBinding("<Keyboard>/space",      "<Keyboard>/ctrl",       "<Gamepad>/buttonSouth");
        public ButtonBinding attack   = new ButtonBinding("<Mouse>/leftButton",    "",                      "<Gamepad>/buttonRightShoulder");
        public ButtonBinding interact = new ButtonBinding("<Keyboard>/e",          "",                      "<Gamepad>/buttonWest");
        public ButtonBinding dash     = new ButtonBinding("<Keyboard>/leftShift",  "",                      "<Gamepad>/buttonEast");
        public ButtonBinding teleport = new ButtonBinding("<Keyboard>/q",          "",                      "<Gamepad>/leftShoulder");

        // R, because that is AS3's own default: <key id='keyReload' def={Keyboard.R}/> (inter/Ctr.as:24).
        // Drives two things: the magazine reload, and — for a radio throwable — the detonator
        // (UnitPlayer.as:2358). See ReloadMessage.
        public ButtonBinding reload     = new ButtonBinding("<Keyboard>/r",          "",                      "<Gamepad>/buttonNorth");

        // ---- Spells (AS3 Ctr.as:24-25, :62-65) ----

        // C — AS3's own default for `keyDef`, the SUPPORTIVE-spell cast button. Casts the spell selected
        // in the inventory; see SpellCastMessage for the held/prod semantics.
        public ButtonBinding defend      = new ButtonBinding("<Keyboard>/c",          "",                      "<Gamepad>/rightTrigger");

        // T — AS3's own default for `keyMagic`, the assault MAGIC-WEAPON button. Deliberately a
        // different key from `defend`: the oracle keeps the nine supportive spells and the magic weapons
        // on separate buttons (Ctr.as:24 vs :25), and the port must not merge them.
        public ButtonBinding magicWeapon = new ButtonBinding("<Keyboard>/t",          "",                      "<Gamepad>/leftTrigger");

        // Z and X are AS3's only BOUND spell hotkeys; slots 3 and 4 exist and are unbound
        // (Ctr.as:62-66, World.kolQS = 4). Kept unbound here rather than dropped, so the slot count
        // matches the oracle and a future default cannot silently invent a key.
        public ButtonBinding spell1 = new ButtonBinding("<Keyboard>/z", "", "<Gamepad>/dpadLeft");
        public ButtonBinding spell2 = new ButtonBinding("<Keyboard>/x", "", "<Gamepad>/dpadRight");
        public ButtonBinding spell3 = new ButtonBinding("",             "", "");
        public ButtonBinding spell4 = new ButtonBinding("",             "", "");

        [Header("Save / Load")]
        // F11, not the conventional F9. F9 is already taken by a DIFFERENT input system:
        // DoorPropPresenter toggles the door-collider debug overlay on a raw Input.GetKeyDown(KeyCode.F9)
        // (its siblings use F8 and F10). The two systems do not know about each other, so binding
        // QuickLoad to F9 made one key press do both. QuickLoad yields rather than the overlay, because
        // the overlays are pre-existing and documented ("Hotkey: F9" in PfeDebugSettings) and because
        // an unintended world reload mid-play-test is the more disruptive of the two failures.
        // F5/F11 rather than F5/F6: keeping load off the key adjacent to save is the same reason the
        // convention was F5/F9 in the first place.
        public ButtonBinding quickSave = new ButtonBinding("<Keyboard>/f5",  "", "<Gamepad>/select");
        public ButtonBinding quickLoad = new ButtonBinding("<Keyboard>/f11", "", "<Gamepad>/start");

        // ---- Defaults (used by editor Reset buttons) ----

        public static MoveBindings DefaultMove => new MoveBindings();

        public static ButtonBinding DefaultJump     => new ButtonBinding("<Keyboard>/space",      "<Keyboard>/ctrl",      "<Gamepad>/buttonSouth");
        public static ButtonBinding DefaultAttack   => new ButtonBinding("<Mouse>/leftButton",    "",                     "<Gamepad>/buttonRightShoulder");
        public static ButtonBinding DefaultInteract => new ButtonBinding("<Keyboard>/e",          "",                     "<Gamepad>/buttonWest");
        public static ButtonBinding DefaultDash     => new ButtonBinding("<Keyboard>/leftShift",  "",                     "<Gamepad>/buttonEast");
        public static ButtonBinding DefaultTeleport => new ButtonBinding("<Keyboard>/q",          "",                     "<Gamepad>/leftShoulder");
        public static ButtonBinding DefaultReload   => new ButtonBinding("<Keyboard>/r",          "",                     "<Gamepad>/buttonNorth");
        public static ButtonBinding DefaultDefend   => new ButtonBinding("<Keyboard>/c",          "",                     "<Gamepad>/rightTrigger");
        public static ButtonBinding DefaultMagicWeapon => new ButtonBinding("<Keyboard>/t",       "",                     "<Gamepad>/leftTrigger");
        public static ButtonBinding DefaultSpell1   => new ButtonBinding("<Keyboard>/z",          "",                     "<Gamepad>/dpadLeft");
        public static ButtonBinding DefaultSpell2   => new ButtonBinding("<Keyboard>/x",          "",                     "<Gamepad>/dpadRight");
        public static ButtonBinding DefaultSpell3   => new ButtonBinding("",                      "",                     "");
        public static ButtonBinding DefaultSpell4   => new ButtonBinding("",                      "",                     "");
        public static ButtonBinding DefaultQuickSave => new ButtonBinding("<Keyboard>/f5",        "",                     "<Gamepad>/select");
        public static ButtonBinding DefaultQuickLoad => new ButtonBinding("<Keyboard>/f11",       "",                     "<Gamepad>/start");
    }

    [Serializable]
    public class MoveBindings
    {
        [Header("WASD")]
        public string kbUp    = "<Keyboard>/w";
        public string kbDown  = "<Keyboard>/s";
        public string kbLeft  = "<Keyboard>/a";
        public string kbRight = "<Keyboard>/d";

        [Header("Arrow Keys (Alt)")]
        public string altUp    = "<Keyboard>/upArrow";
        public string altDown  = "<Keyboard>/downArrow";
        public string altLeft  = "<Keyboard>/leftArrow";
        public string altRight = "<Keyboard>/rightArrow";
    }

    [Serializable]
    public class ButtonBinding
    {
        [Tooltip("Primary keyboard / mouse binding path.")]
        public string keyboard;

        [Tooltip("Optional secondary keyboard binding (leave empty to skip).")]
        public string altKeyboard;

        [Tooltip("Gamepad binding path.")]
        public string gamepad;

        public ButtonBinding() { }

        public ButtonBinding(string keyboard, string altKeyboard, string gamepad)
        {
            this.keyboard    = keyboard;
            this.altKeyboard = altKeyboard;
            this.gamepad     = gamepad;
        }
    }
}
