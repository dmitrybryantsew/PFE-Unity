using UnityEngine;

namespace PFE.Core.Messages
{
    /// <summary>
    /// Published when a tutorial prompt or contextual message should be shown or hidden.
    /// Used by Area triggers (e.g. trSit, trDownJump, trCont, trTele, trPunch).
    /// </summary>
    public struct TutorialPromptMessage
    {
        public string MessageKey;
        public string Text;
        public bool Show;
        public bool BottomPosition;

        public TutorialPromptMessage(string messageKey, string text, bool show, bool bottomPosition = false)
        {
            MessageKey = messageKey;
            Text = text;
            Show = show;
            BottomPosition = bottomPosition;
        }
    }

    /// <summary>
    /// Published when an objective indicator / arrow should point toward or stop pointing toward a map object.
    /// Corresponds to AS3 script action act="sign".
    /// </summary>
    public struct ObjectiveMarkerMessage
    {
        public string TargetUid;
        public Vector2 TargetPosition;
        public bool Show;

        public ObjectiveMarkerMessage(string targetUid, Vector2 targetPosition, bool show)
        {
            TargetUid = targetUid;
            TargetPosition = targetPosition;
            Show = show;
        }
    }

    /// <summary>
    /// Published when an area trigger or script commands the game to transition to another land (e.g. "surf", "base", "stable").
    /// Corresponds to AS3 script action act="gotoland".
    /// </summary>
    public struct LandTransitionMessage
    {
        public string TargetLand;
        public string TargetCoordinates;

        public LandTransitionMessage(string targetLand, string targetCoordinates = null)
        {
            TargetLand = targetLand;
            TargetCoordinates = targetCoordinates;
        }
    }
}
