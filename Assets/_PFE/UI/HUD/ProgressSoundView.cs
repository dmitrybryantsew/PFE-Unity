using System;
using MessagePipe;
using PFE.Core.Messages;
using PFE.UI.Common;
using UnityEngine;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Plays the three progression chimes the port was silently missing — level up, quest granted,
    /// quest completed.
    ///
    /// <para><b>The sounds existed; the calls did not.</b> <c>levelup</c>, <c>quest</c> and
    /// <c>quest_ok</c> are all declared in <c>Snd.as:300,305-306</c> and all three were already
    /// imported into <c>SoundCatalog.asset</c> with a clip attached, so nothing was missing from the
    /// asset pipeline. What was missing was a caller: the Oracle plays them from
    /// <c>Pers.upLevel</c> (<c>Pers.as:1140</c>), <c>Game.addQuest</c>/<c>showQuest</c>
    /// (<c>Game.as:543,572,603</c>) and <c>Quest.close</c> (<c>Quest.as:693</c>), and the port's
    /// equivalents — <c>CharacterStats.UpLevel</c> and <c>CampaignManager</c>'s quest verbs — published
    /// their events into nothing. <c>LevelUpMessage</c> in particular had <b>one publisher and zero
    /// subscribers</b>.</para>
    ///
    /// <para><b>Why a relay rather than a call at each publisher.</b> The Oracle calls
    /// <c>Snd.ps</c> from inside game logic because its sound service is a global singleton. The
    /// port's is an injected service, and the two publishers live on opposite sides of a boundary
    /// the project keeps: <c>CharacterStats</c> and <c>CampaignManager</c> are rules, this is
    /// presentation. Subscribing to the events they already publish keeps the dependency pointing
    /// one way and means a future headless harness never has to stub audio to run a level-up.</para>
    ///
    /// <para><b>Played at the listener, not at the player.</b> These are non-diegetic signals —
    /// they come out of the interface, not out of the world — which is exactly the case
    /// <see cref="UiSoundService"/> already documents, so it is used rather than
    /// <c>ISoundService.Play</c> directly. Playing at the player's world position would pan the
    /// chime as they walk.</para>
    /// </summary>
    public class ProgressSoundView : MonoBehaviour
    {
        private UiSoundService _sound;
        private IDisposable _levelUpSubscription;
        private IDisposable _questSubscription;

        /// <summary>Level-ups heard since <see cref="Bind"/>. For tests and for the build log.</summary>
        public int LevelUpsHeard { get; private set; }

        /// <summary>Quests granted since <see cref="Bind"/>. For tests.</summary>
        public int QuestsGivenHeard { get; private set; }

        /// <summary>Whole quests completed since <see cref="Bind"/>. For tests.</summary>
        public int QuestsCompletedHeard { get; private set; }

        /// <summary>True when both streams are attached — i.e. a level-up will be audible.</summary>
        public bool IsBound => _levelUpSubscription != null && _questSubscription != null;

        /// <summary>
        /// Attaches the two streams. Split from construction because the subscribers come from the
        /// container and the component comes from the bootstrapper, the same split
        /// <see cref="ScriptToastView.Bind"/> uses.
        ///
        /// <para>Each stream is bound independently: a container missing one broker should still
        /// leave the other audible, and a single combined null-check would silence both.</para>
        /// </summary>
        public void Bind(UiSoundService sound, ISubscriber<LevelUpMessage> levelUps,
            ISubscriber<QuestStateChangedMessage> quests)
        {
            Unbind();

            _sound = sound;
            _levelUpSubscription = levelUps?.Subscribe(OnLevelUp);
            _questSubscription = quests?.Subscribe(OnQuestStateChanged);
        }

        /// <summary>Drops both subscriptions. Safe to call twice; <see cref="Bind"/> calls it first.</summary>
        public void Unbind()
        {
            _levelUpSubscription?.Dispose();
            _questSubscription?.Dispose();
            _levelUpSubscription = null;
            _questSubscription = null;
        }

        private void OnDestroy() => Unbind();

        /// <summary><c>Pers.upLevel</c>'s last statement (<c>Pers.as:1140</c>).</summary>
        private void OnLevelUp(LevelUpMessage message)
        {
            LevelUpsHeard++;
            _sound?.PlayLevelUp();
        }

        private void OnQuestStateChanged(QuestStateChangedMessage message)
        {
            ProgressSound sound = ProgressSoundRules.ForQuestStateChange(
                message.OldState, message.NewState, message.Stage);

            switch (sound)
            {
                case ProgressSound.QuestGiven:
                    QuestsGivenHeard++;
                    _sound?.PlayQuestGiven();
                    break;

                case ProgressSound.QuestComplete:
                    QuestsCompletedHeard++;
                    _sound?.PlayQuestComplete();
                    break;
            }
        }
    }
}
