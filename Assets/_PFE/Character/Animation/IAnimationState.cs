using PFE.Entities.Player;
using UnityEngine;

namespace PFE.Character.Animation
{
    /// <summary>
    /// One state in the character animation FSM.
    /// Implementations are plain C# objects, not MonoBehaviours.
    ///
    /// Priority ordering (higher wins):
    ///   Locomotion states  10–110   (evaluated every tick from PlayerMovementSnapshot)
    ///   Action states    200–250   (activated externally by game events)
    /// </summary>
    public interface IAnimationState
    {
        /// <summary>Higher value = evaluated first = wins when multiple states CanEnter.</summary>
        int Priority { get; }

        /// <summary>
        /// While true the driver does not re-evaluate lower-priority states.
        /// Set only for play-once action states (die, res, punch, kick, lurk).
        /// </summary>
        bool IsLocked { get; }

        /// <summary>Should this state be active given the current snapshot?</summary>
        bool CanEnter(in PlayerMovementSnapshot snapshot);

        /// <summary>
        /// Animation state name to pass to CharacterSpriteAssembler.
        /// Return null to keep the current state name unchanged.
        /// </summary>
        string GetStateName(in PlayerMovementSnapshot snapshot);

        /// <summary>
        /// Return an explicit frame index for this animation tick, or -1 to advance normally.
        /// Called once per animation tick (driven by definition.frameRate).
        /// Used for: jump (velocity-mapped), stay (aim-mapped), ladder (direction-controlled).
        /// </summary>
        int OverrideFrame(in PlayerMovementSnapshot snapshot, int currentFrame, int frameCount);

        /// <summary>Called when transitioning INTO this state. previousState is the assembler's last state name.</summary>
        void OnEnter(string previousState, IAnimationEventSink events);

        /// <summary>Called when transitioning AWAY from this state. nextState is the incoming state name.</summary>
        void OnExit(string nextState, IAnimationEventSink events);

        /// <summary>Called once per animation tick after the frame has been set. Use for footsteps, impacts, etc.</summary>
        void OnFrameChanged(int newFrame, in PlayerMovementSnapshot snapshot, IAnimationEventSink events);
    }

    /// <summary>
    /// Receives animation-driven game events (sounds, particles, screen effects).
    /// Implement this on whatever handles audio / VFX in the scene and assign it to CharacterAnimationDriver.
    /// </summary>
    public interface IAnimationEventSink
    {
        void FireEvent(string eventId, Vector2 worldPosition);
    }

    /// <summary>String constants for events fired through IAnimationEventSink.</summary>
    public static class AnimEvent
    {
        public const string Footstep      = "footstep";
        public const string FootstepHeavy = "footstep_heavy"; // reserved — currently unused
        public const string FootstepCrawl = "footstep_crawl"; // prone crawl (quieter)
        public const string ClimbStep     = "climb_step";
        public const string Jump          = "jump";
        public const string DoubleJump    = "double_jump";
        public const string Land          = "land";
        public const string WaterEnter    = "water_enter";
        public const string WaterExit     = "water_exit";
        public const string MeleeImpact   = "melee_impact";
        public const string Death         = "death";
        public const string Resurrect     = "resurrect";
        public const string Dash          = "dash";
        public const string Teleport      = "teleport";
        public const string Stagger       = "stagger";
    }
}
