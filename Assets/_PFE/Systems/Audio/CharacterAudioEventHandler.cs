using PFE.Character.Animation;
using PFE.Systems.Map;
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

        public void FireEvent(string eventId, Vector2 worldPosition, float volumeScale = 1f)
        {
            // ── Pixels in, units out ─────────────────────────────────────────────────────────────
            //
            // Every producer of these events passes `PlayerMovementSnapshot.Position`, which is the
            // motor's `posX`/`posY` — world PIXELS (100 per unit), not `transform.position`. Handing
            // that straight to `ISoundService.Play` put a pixel number into `SoundService.ComputePan`,
            // which divides by the camera's half-width in UNITS: a player at pixel x=2000 against a
            // camera at unit x=20 came out as (2000-20)/10 = 198, clamped to +1. So every footstep,
            // splash, jump and landing played hard-panned to one ear instead of centred, and flipped
            // side as the player crossed the world origin. Converted once here rather than at the
            // nine fire sites, because this is the only consumer of the event.
            Vector2 worldUnits = WorldCoordinates.PixelToUnity(worldPosition);

            switch (eventId)
            {
                // Ground footsteps — variant cycling + "a" suffix + quiet volume
                case AnimEvent.Footstep:
                case AnimEvent.FootstepHeavy:
                    _sounds?.Play(NextFootstepId("footstep"), worldUnits, 0.25f);
                    return;

                // Crawl footsteps — same pool, even quieter
                case AnimEvent.FootstepCrawl:
                    _sounds?.Play(NextFootstepId("footstep"), worldUnits, 0.15f);
                    return;

                // Ladder climb — lazstep pool, slightly louder than ground steps
                case AnimEvent.ClimbStep:
                    _sounds?.Play(NextClimbStepId(), worldUnits, 0.5f);
                    return;
            }

            // All other events: simple one-to-one mapping, at the event's own loudness. The water
            // splash is the one event that sets it — `|dy| / 10` (`Unit.as:2675`), computed by
            // SwimAnimState, so a slip into water is quiet and a plunge is not.
            string soundId = MapEventToSound(eventId);
            if (soundId != null)
                _sounds?.Play(soundId, worldUnits, volumeScale);
        }

        // ---------------------------------------------------------------
        // Event → Sound ID mapping (non-footstep events)
        // ---------------------------------------------------------------

        private static string MapEventToSound(string eventId) => eventId switch
        {
            // Locomotion
            //
            // Jump / DoubleJump fall through to the null default on purpose, and that is the oracle's
            // answer rather than an omission. They used to map to "move", which is the moving-platform
            // hum: Interact.as:1869 plays it from `sound("move")` and :1931/:1939 start it at offset 0
            // and 5500 ms, so the sample is over five seconds long — a mechanical loop, not a whoosh.
            // Hearing it on the levitation (double) jump is what the platform complaint was.
            // `UnitPlayer`'s complete set of scripted samples is dash / electro / al_armor / bale_e
            // (UnitPlayer.as:1883, :3364, :3986, :4198); there is no jump or levitation cue among them,
            // and the sound library has no id for one either. A whoosh here would be invented.
            //
            // They are deleted from the switch rather than mapped to null so the lint can see it: a
            // stripped source still shows `AnimEvent.Jump`, and its absence is the invariant.
            AnimEvent.Land          => "fall_body",     // landing thud

            // Water
            AnimEvent.WaterEnter    => "fall_item_water",
            AnimEvent.WaterExit     => "fall_item_water",

            // Combat. No melee-impact arm: the swing's own sample comes from the weapon controller
            // (`m_big`/`m_med`, WKick.as:81/:105) and the landing sample from MeleeHitVolume.
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
