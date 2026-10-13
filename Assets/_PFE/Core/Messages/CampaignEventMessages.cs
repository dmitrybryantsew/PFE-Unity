using PFE.Systems.Campaign;

namespace PFE.Core.Messages
{
    /// <summary>
    /// A story trigger changed value — AS3 <c>World.w.game.setTrigger(name, value)</c>
    /// (<c>Game.as:676-679</c>).
    ///
    /// <para><b>Why a message and not another <c>ReactiveProperty</c>.</b> The project's split is
    /// explicit (<c>PROJECT_MAP.md</c> §2): <b>R3 for values the HUD displays, MessagePipe for events
    /// gameplay reacts to</b>. A trigger *change* is something a cutscene, a quest hook or a spawn reacts
    /// to; a subscriber to a property would have to carry the previous value itself to learn that
    /// anything happened at all — which is exactly the bug <see cref="LandEnteredMessage"/> exists to
    /// avoid.</para>
    ///
    /// <para><b>Both values travel.</b> The oracle has no "was" notion — it reads the table — but a
    /// hook that fires on "the trigger became set" cannot be written without it, and a subscriber that
    /// diffs the table itself would be reading state the publisher already had in hand.</para>
    /// </summary>
    public struct TriggerChangedMessage
    {
        /// <summary>The trigger's name — AS3 <c>triggers[name]</c>.</summary>
        public string Name;

        /// <summary>What the table held before. <see cref="TriggerValue.Unset"/> when the key was absent,
        /// which is a different answer from a stored zero.</summary>
        public TriggerValue OldValue;

        /// <summary>What the table holds now.</summary>
        public TriggerValue NewValue;

        public TriggerChangedMessage(string name, TriggerValue oldValue, TriggerValue newValue)
        {
            Name = name;
            OldValue = oldValue;
            NewValue = newValue;
        }
    }

    /// <summary>
    /// A quest's state changed — AS3 <c>Quest.state</c> (<c>Quest.as:71</c>), whose three values are
    /// <c>0</c> not started, <c>1</c> in progress, <c>2</c> done.
    ///
    /// <para><b>This is the event the port had no equivalent of.</b> <c>CompleteQuest</c> used to update
    /// three collections and tell nobody, so "a quest advanced" was unobservable from outside
    /// <c>CampaignManager</c> — which is why nothing could bind a hook to it.</para>
    /// </summary>
    public struct QuestStateChangedMessage
    {
        /// <summary>The parent quest id — the <c>val</c> of an authored <c>&lt;s act="quest"/&gt;</c>.</summary>
        public string QuestId;

        /// <summary>State before: 0, 1 or 2.</summary>
        public int OldState;

        /// <summary>State after: 0, 1 or 2.</summary>
        public int NewState;

        /// <summary>
        /// The sub-objective that moved, as the oracle's own sub id (a <b>string</b> — <c>Quest.closeSub</c>
        /// looks it up in <c>subsId</c>, <c>Quest.as:579</c>), or null when the parent itself moved.
        /// </summary>
        public string Stage;

        public QuestStateChangedMessage(string questId, int oldState, int newState, string stage = null)
        {
            QuestId = questId;
            OldState = oldState;
            NewState = newState;
            Stage = stage;
        }
    }

    /// <summary>
    /// The campaign entered a land — published by <c>CampaignManager.TransitionToLand</c> after the id is
    /// set, and by <c>AdoptBuiltLand</c> when a debug-built world is attributed.
    ///
    /// <para><b>Why this exists when <c>CurrentLandId</c> is already reactive.</b> A property answers
    /// "what is it"; a subscriber that wants "we just arrived" must diff it against a remembered value,
    /// and every subscriber would have to do that separately. The oracle's equivalent is
    /// <c>Game.enterToCurLand</c> (<c>Game.as:396-400</c>), which runs once per entry — this is that
    /// moment, named.</para>
    /// </summary>
    public struct LandEnteredMessage
    {
        /// <summary>The land now being entered.</summary>
        public string LandId;

        /// <summary>The land left behind, or null on the first entry of a session.</summary>
        public string PreviousLandId;

        /// <summary>The entering land's <c>landStage</c> — the depth the descent has reached.</summary>
        public int Stage;

        public LandEnteredMessage(string landId, string previousLandId, int stage)
        {
            LandId = landId;
            PreviousLandId = previousLandId;
            Stage = stage;
        }
    }

    /// <summary>
    /// A checkpoint activated — AS3 <c>CheckPoint.activate()</c> (<c>CheckPoint.as:184-248</c>), the one
    /// place the oracle saves the game (<c>:247</c>).
    /// </summary>
    public struct CheckpointActivatedMessage
    {
        /// <summary>The land the checkpoint is in.</summary>
        public string LandId;

        /// <summary>The room cell, as the port's land grid — not source pixels.</summary>
        public int RoomX;
        public int RoomY;
        public int RoomZ;

        /// <summary>
        /// AS3 <c>currentCPCode</c>, or an empty string. It is the checkpoint's identity when present:
        /// <c>Location.as:2065-2067</c> re-finds a checkpoint by code after a rebuild, and the exit room's
        /// checkpoint has none (<c>RoomsProb.as:4724</c>) and falls back to the room cell.
        /// </summary>
        public string Code;

        public CheckpointActivatedMessage(string landId, int roomX, int roomY, int roomZ, string code)
        {
            LandId = landId;
            RoomX = roomX;
            RoomY = roomY;
            RoomZ = roomZ;
            Code = code;
        }
    }
}
