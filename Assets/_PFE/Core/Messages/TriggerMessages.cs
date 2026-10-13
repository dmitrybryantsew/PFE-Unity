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

        /// <summary>
        /// AS3 <c>messText</c>'s <c>param3</c>: <c>mess.y = screenY - 50 - mess.height</c> instead of
        /// <c>mess.y = 50</c> (<c>GUI.as:1682-1689</c>) — the banner drops to the bottom of the screen.
        /// </summary>
        public bool BottomPosition;

        /// <summary>
        /// AS3 <c>messText</c>'s <c>param4</c>: append with <c>&lt;br&gt;</c> rather than replace
        /// (<c>GUI.as:1675-1680</c>). Carried because the script vocabulary sets it from
        /// <c>opt2</c> — <c>mess val opt1 opt2</c> (<c>Script.as:247-250</c>) — and a banner that
        /// silently replaces where the oracle appends is a wrong transcript, not a cosmetic
        /// difference.
        /// </summary>
        public bool Push;

        public TutorialPromptMessage(string messageKey, string text, bool show, bool bottomPosition = false,
                                     bool push = false)
        {
            MessageKey = messageKey;
            Text = text;
            Show = show;
            BottomPosition = bottomPosition;
            Push = push;
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

    /// <summary>
    /// Published when travel is granted and the world map should be shown — AS3 <c>pip.onoff(3,3)</c>,
    /// reached from the camp's wall map (<c>Interact.as:1636-1641</c>) or a <c>travel</c> NPC
    /// (<c>NPC.as:210-216</c>).
    ///
    /// <para>An event rather than a direct call because the map page is a view that does not exist yet
    /// (L11): publishing it now keeps the campaign side complete and lets the page be added without
    /// touching <c>CampaignManager</c> again.</para>
    /// </summary>
    public struct TravelMapOpenedMessage
    {
        /// <summary>The land the player is standing in — the map opens showing that as "here".</summary>
        public string CurrentLandId;

        public TravelMapOpenedMessage(string currentLandId)
        {
            CurrentLandId = currentLandId;
        }
    }

    /// <summary>
    /// The one request that actually rebuilds the world. <c>CampaignManager</c> owns campaign *state* and
    /// publishes this; the *builder* (<c>MapBridge</c>) subscribes and runs the build.
    ///
    /// <para><b>Why a second message.</b> Before this, two unconnected paths existed:
    /// <c>MapBridge.HandleGotoLand</c> really rebuilt, and <c>CampaignManager.TransitionToLand</c> only
    /// logged a success and updated two reactive properties nobody read
    /// (<c>docs/LandGameplayLoop/03_GAP_LEDGER.md</c> §1). One owner has to build; this is the seam that
    /// makes that explicit.</para>
    /// </summary>
    public struct LandBuildRequestMessage
    {
        /// <summary>Destination land id.</summary>
        public string LandId;

        /// <summary>"x:y" entry override, or null to use the land's own entry cell.</summary>
        public string EntryCoordinates;

        /// <summary>AS3 <c>Game.crea</c> — force a fresh layout even if one is cached
        /// (<c>Game.as:464-472</c>, the level advance).</summary>
        public bool ForceRegenerate;

        public LandBuildRequestMessage(string landId, string entryCoordinates = null, bool forceRegenerate = false)
        {
            LandId = landId;
            EntryCoordinates = entryCoordinates;
            ForceRegenerate = forceRegenerate;
        }
    }
}
