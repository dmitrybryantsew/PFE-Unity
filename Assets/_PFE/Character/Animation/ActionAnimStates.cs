using PFE.Entities.Player;
using UnityEngine;

namespace PFE.Character.Animation
{
    // ─────────────────────────────────────────────────────────────────────────
    //  AnimationAction enum — used by CharacterAnimationDriver.TriggerAction()
    // ─────────────────────────────────────────────────────────────────────────

    public enum AnimationAction
    {
        Die,        // Normal death
        DieAlt,     // Alternate death context (dieali)
        Resurrect,  // Respawn / revival (res)
        Punch,      // Melee punch
        Kick,       // Melee kick (while running)
        Stagger,    // Ground hit-stagger (derg)
        Lurk,       // Enter cover — call with variant 1/2/3
        Unlurk,     // Exit cover — plays unlurk if available, else returns to locomotion
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Base helpers shared by the simple action states
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Base for single-clip locked action states (play once, hold last frame, unlock).</summary>
    public abstract class SimpleActionState : IAnimationState
    {
        bool _isActive;

        public abstract int    Priority  { get; }
        public          bool   IsLocked  => _isActive;
        public          bool   CanEnter(in PlayerMovementSnapshot s) => _isActive;
        public abstract string GetStateName(in PlayerMovementSnapshot s);
        public          int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc) => -1;

        public virtual void OnEnter(string prev, IAnimationEventSink e) { }

        public virtual void OnExit(string next, IAnimationEventSink e)
        {
            // Called when the driver detects the clip has finished (or is interrupted).
            _isActive = false;
        }

        public virtual void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e) { }

        protected void Activate() => _isActive = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Die / DieAlt   Priority 250 — highest; nothing can interrupt death
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class DieAnimState : SimpleActionState
    {
        bool _useAlt;

        public override int    Priority => 250;
        public override string GetStateName(in PlayerMovementSnapshot s) => _useAlt ? "dieali" : "die";

        public void Activate(bool useAlt = false)
        {
            _useAlt = useAlt;
            base.Activate();
        }

        public override void OnEnter(string prev, IAnimationEventSink e)
            => e?.FireEvent(AnimEvent.Death, Vector2.zero);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Resurrect (res)   Priority 240
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class ResAnimState : SimpleActionState
    {
        public override int    Priority => 240;
        public override string GetStateName(in PlayerMovementSnapshot s) => "res";

        public new void Activate() => base.Activate();

        public override void OnEnter(string prev, IAnimationEventSink e)
            => e?.FireEvent(AnimEvent.Resurrect, Vector2.zero);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Punch   Priority 230
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class PunchAnimState : SimpleActionState
    {
        // Approximate frame index where the impact lands (tune per animation data).
        const int ImpactFrame = 8;

        public override int    Priority => 230;
        public override string GetStateName(in PlayerMovementSnapshot s) => "punch";

        public new void Activate() => base.Activate();

        public override void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e)
        {
            if (frame == ImpactFrame)
                e?.FireEvent(AnimEvent.MeleeImpact, s.Position);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Kick   Priority 220   (melee while running)
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class KickAnimState : SimpleActionState
    {
        const int ImpactFrame = 6;

        public override int    Priority => 220;
        public override string GetStateName(in PlayerMovementSnapshot s) => "kick";

        public new void Activate() => base.Activate();

        public override void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e)
        {
            if (frame == ImpactFrame)
                e?.FireEvent(AnimEvent.MeleeImpact, s.Position);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Stagger / Derg (ground hit-reaction)   Priority 210
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class DergAnimState : SimpleActionState
    {
        public override int    Priority => 210;
        public override string GetStateName(in PlayerMovementSnapshot s) => "derg";

        public new void Activate() => base.Activate();

        public override void OnEnter(string prev, IAnimationEventSink e)
            => e?.FireEvent(AnimEvent.Stagger, Vector2.zero);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Lurk + Unlurk sequence   Priority 200
    //
    //  State machine within the state:
    //    Lurking   → IsLocked=true, plays lurk1/2/3 (loops indefinitely)
    //    Unlurking → IsLocked=true, plays "unlurk" once, then deactivates
    //
    //  When "unlurk" is not in the definition, the driver falls back to "stay"
    //  via its missing-state check, which effectively returns to locomotion immediately.
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class LurkAnimState : IAnimationState
    {
        bool _isActive;
        bool _isUnlurking;
        int  _variant; // 1, 2, or 3

        public int  Priority => 200;
        public bool IsLocked => _isActive;

        public bool CanEnter(in PlayerMovementSnapshot s) => _isActive;

        public string GetStateName(in PlayerMovementSnapshot s)
            => _isUnlurking ? "unlurk" : $"lurk{_variant}";

        public int OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc)
            => _isUnlurking ? -1 : 0; // lurk holds frame 0; unlurk auto-advances to detect end

        /// <param name="variant">Cover variant: 1 = low, 2 = medium, 3 = tall. 0 = random.</param>
        public void Activate(int variant = 0)
        {
            _isActive    = true;
            _isUnlurking = false;
            _variant     = variant > 0 ? Mathf.Clamp(variant, 1, 3) : Random.Range(1, 4);
        }

        /// <summary>
        /// Begin the exit sequence. The driver must be force-re-evaluated after calling this
        /// (CharacterAnimationDriver.TriggerAction(AnimationAction.Unlurk) does this automatically).
        /// </summary>
        public void Unlurk()
        {
            if (_isActive && !_isUnlurking)
                _isUnlurking = true;
        }

        public void OnEnter(string prev, IAnimationEventSink e) { }

        public void OnExit(string next, IAnimationEventSink e)
        {
            // Transitioning from lurk → unlurk: stay active, don't reset.
            // Transitioning from unlurk → anything: fully deactivate.
            if (next != "unlurk")
                _isActive = _isUnlurking = false;
        }

        public void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e) { }
    }
}
