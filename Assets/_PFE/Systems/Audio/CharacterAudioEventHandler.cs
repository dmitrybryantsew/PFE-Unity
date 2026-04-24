using PFE.Character.Animation;
using UnityEngine;
using VContainer;

namespace PFE.Systems.Audio
{
    /// <summary>
    /// Bridges IAnimationEventSink to ISoundService for the player character.
    ///
    /// Attach to the same GameObject as CharacterAnimationDriver and assign to its
    /// eventSink field (Event Sink Object in the Inspector).
    ///
    /// Footstep behaviour matches AS3 UnitPon.as sndStep():
    ///   - Variant cycling: 1 → 4 → 2 → 3 per step (never repeats adjacent sounds)
    ///   - 50% chance of "a" suffix per step (footstep1a, footstep2a …)
    ///   - lazstep has no "a" variant
    ///   - Volume: footsteps at 25% of SFX volume (stepVol=0.5 × footstepVol=0.2 / globalVol=0.4)
    ///   - Ladder steps at 50% (double footstepVol in original)
    ///   - Crawl steps at 15% (quieter than walking)
    /// </summary>
    public class CharacterAudioEventHandler : MonoBehaviour, IAnimationEventSink
    {
        [Inject] private ISoundService _sounds;

        // VContainer injection entry point
        [Inject]
        public void Construct(ISoundService soundService)
        {
            _sounds = soundService;
        }

        // ---------------------------------------------------------------
        // Footstep variant cycling — matches AS3 sndStep() pattern
        // ---------------------------------------------------------------

        // Variants play in this order per stride: 1 → 4 → 2 → 3
        private static readonly int[] VariantOrder = { 1, 4, 2, 3 };
        private int _stepCycle;

        /// <summary>
        /// Returns the next footstep ID with variant cycling and 50% "a" suffix.
        /// materialBase: "footstep" or "metalstep"
        /// </summary>
        private string NextFootstepId(string materialBase)
        {
            int variant = VariantOrder[_stepCycle & 3];
            _stepCycle++;
            string suffix = Random.value < 0.5f ? "a" : "";
            return $"{materialBase}{variant}{suffix}";
        }

        /// <summary>
        /// Returns the next lazstep ID. No "a" suffix (not in original sound library).
        /// </summary>
        private string NextClimbStepId()
        {
            int variant = VariantOrder[_stepCycle & 3];
            _stepCycle++;
            return $"lazstep{variant}";
        }

        // ---------------------------------------------------------------
        // IAnimationEventSink
        // ---------------------------------------------------------------

        public void FireEvent(string eventId, Vector2 worldPosition)
        {
            switch (eventId)
            {
                // Ground footsteps — variant cycling + "a" suffix + quiet volume
                case AnimEvent.Footstep:
                case AnimEvent.FootstepHeavy:
                    _sounds?.Play(NextFootstepId("footstep"), worldPosition, 0.25f);
                    return;

                // Crawl footsteps — same pool, even quieter
                case AnimEvent.FootstepCrawl:
                    _sounds?.Play(NextFootstepId("footstep"), worldPosition, 0.15f);
                    return;

                // Ladder climb — lazstep pool, slightly louder than ground steps
                case AnimEvent.ClimbStep:
                    _sounds?.Play(NextClimbStepId(), worldPosition, 0.5f);
                    return;
            }

            // All other events: simple one-to-one mapping, normal volume
            string soundId = MapEventToSound(eventId);
            if (soundId != null)
                _sounds?.Play(soundId, worldPosition);
        }

        // ---------------------------------------------------------------
        // Event → Sound ID mapping (non-footstep events)
        // ---------------------------------------------------------------

        private static string MapEventToSound(string eventId) => eventId switch
        {
            // Locomotion
            AnimEvent.Jump          => "move",          // jump whoosh
            AnimEvent.DoubleJump    => "move",
            AnimEvent.Land          => "fall_body",     // landing thud

            // Water
            AnimEvent.WaterEnter    => "fall_item_water",
            AnimEvent.WaterExit     => "fall_item_water",

            // Combat
            AnimEvent.MeleeImpact   => "blade",
            AnimEvent.Death         => "rm",            // random death grunt rm1..rm6
            AnimEvent.Stagger       => "blade2",

            // Abilities
            AnimEvent.Dash          => "dash",
            AnimEvent.Teleport      => "teleport",

            AnimEvent.Resurrect     => null,

            _ => null
        };
    }
}
