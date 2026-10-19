namespace PFE.UI.HUD
{
    /// <summary>The three progression chimes the Oracle plays, as signals rather than clip names.</summary>
    public enum ProgressSound
    {
        /// <summary>Nothing to play — the transition is silent in the Oracle.</summary>
        None = 0,

        /// <summary><c>levelup</c> — <c>Pers.upLevel</c> (<c>Pers.as:1140</c>).</summary>
        LevelUp = 1,

        /// <summary><c>quest</c> — <c>Game.addQuest</c> / <c>showQuest</c> (<c>Game.as:543,572,603</c>).</summary>
        QuestGiven = 2,

        /// <summary><c>quest_ok</c> — <c>Quest.close</c>, parent only (<c>Quest.as:690-693</c>).</summary>
        QuestComplete = 3,
    }

    /// <summary>
    /// Which progression chime a quest-state transition earns.
    ///
    /// <para><b>Why this is not decided inside the view.</b> The decision is a reading of two
    /// different Oracle call sites that disagree about sub-objectives, and it is the kind of thing
    /// that is wrong in a way nobody notices until a chime plays on the wrong event. Isolating it
    /// means the three transitions can be pinned by a test that never builds a GameObject, and the
    /// view is left with a <c>switch</c>.</para>
    ///
    /// <para><b>The asymmetry is the Oracle's.</b> <c>Game.addQuest</c> chimes on
    /// <c>quest</c> for every grant, but <c>Quest.close</c> chimes on <c>quest_ok</c> only when
    /// <c>!this.sub</c> (<c>Quest.as:690</c>) — a sub-objective closing plays <b>nothing</b>, it
    /// only prints <c>doneStage</c>. So a state-2 transition is not automatically a completion:
    /// it is one only when no <c>Stage</c> was named.</para>
    /// </summary>
    public static class ProgressSoundRules
    {
        /// <summary><c>QuestState.NotStarted</c> (<c>QuestRules.cs:15</c>).</summary>
        public const int NotStarted = 0;

        /// <summary><c>QuestState.Active</c> (<c>QuestRules.cs:18</c>).</summary>
        public const int Active = 1;

        /// <summary><c>QuestState.Done</c> (<c>QuestRules.cs:21</c>).</summary>
        public const int Done = 2;

        /// <summary>
        /// The chime for a <c>QuestStateChangedMessage</c>.
        ///
        /// <para><b>Checked in this order on purpose.</b> A transition can be reported with
        /// <c>OldState == NewState</c> (a close that re-publishes the state it was already in), and
        /// a parent completing publishes <c>1 → 2</c> with a null stage — so testing "granted"
        /// first would leave the completion to a later branch that a sub close also reaches. The
        /// completion test is therefore first and is guarded by the stage, which is the only field
        /// that separates the two.</para>
        /// </summary>
        /// <param name="oldState">State before the change — 0, 1 or 2.</param>
        /// <param name="newState">State after the change — 0, 1 or 2.</param>
        /// <param name="stage">The sub-objective that moved, or null when the parent moved.</param>
        public static ProgressSound ForQuestStateChange(int oldState, int newState, string stage)
        {
            // `if(!this.sub) { … Snd.ps("quest_ok"); }` — a named stage means a sub moved, and the
            // Oracle's else-branch prints `doneStage` and plays nothing.
            if (newState == Done && string.IsNullOrEmpty(stage)) return ProgressSound.QuestComplete;

            // `Game.addQuest`'s two chime sites (:543 for a quest parked at state 0, :572 for a fresh
            // grant) are both 0 → 1. A quest that is already active and merely re-granted is not a
            // chime: the Oracle returns before the `Snd.ps` in that case (:537-552).
            if (oldState == NotStarted && newState == Active) return ProgressSound.QuestGiven;

            return ProgressSound.None;
        }
    }
}
