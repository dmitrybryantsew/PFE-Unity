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

        // ---- Defaults (used by editor Reset buttons) ----

        public static MoveBindings DefaultMove => new MoveBindings();

        public static ButtonBinding DefaultJump     => new ButtonBinding("<Keyboard>/space",      "<Keyboard>/ctrl",      "<Gamepad>/buttonSouth");
        public static ButtonBinding DefaultAttack   => new ButtonBinding("<Mouse>/leftButton",    "",                     "<Gamepad>/buttonRightShoulder");
        public static ButtonBinding DefaultInteract => new ButtonBinding("<Keyboard>/e",          "",                     "<Gamepad>/buttonWest");
        public static ButtonBinding DefaultDash     => new ButtonBinding("<Keyboard>/leftShift",  "",                     "<Gamepad>/buttonEast");
        public static ButtonBinding DefaultTeleport => new ButtonBinding("<Keyboard>/q",          "",                     "<Gamepad>/leftShoulder");
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
