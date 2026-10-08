using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Entities.Enemies
{
    /// <summary>
    /// Pure situational data container for an enemy unit.
    /// Updated by <see cref="EnemySensors"/> and evaluated by <see cref="EnemyBrain"/>.
    /// Contains zero behavior logic or physics simulation.
    /// </summary>
    public sealed class EnemyBlackboard
    {
        /// <summary>
        /// Current primary combat target (null when idle or investigating).
        /// </summary>
        public UnitController TargetUnit;

        /// <summary>
        /// Last confirmed or estimated position of target, in world pixels.
        ///
        /// <para>Only meaningful when <see cref="HasLastKnownTargetPosition"/> is set. It is a
        /// <c>Vector2</c>, so it always <i>has</i> a value — and <c>(0, 0)</c> is the corner of the map,
        /// not "nowhere". A consumer that reads it without the flag walks to the world origin, which is
        /// exactly what <c>TickAlert</c> used to do the moment the heard-noise latch stopped being
        /// re-armed every tick.</para>
        /// </summary>
        public Vector2 LastKnownTargetPosition;

        /// <summary>
        /// Whether <see cref="LastKnownTargetPosition"/> has ever been written — i.e. whether this unit
        /// has actually <i>seen</i> the target at least once. AS3 gets this for free: <c>celUnit</c> is
        /// null until <c>setCel(unit)</c> runs, and the position is read only in the branches that have
        /// already tested it. The port keeps the two apart explicitly because its position field cannot
        /// be null.
        /// </summary>
        public bool HasLastKnownTargetPosition;

        /// <summary>
        /// Distance squared to target in world pixels.
        /// </summary>
        public float TargetDistanceSq;

        /// <summary>
        /// Euclidean distance to target in world pixels.
        /// </summary>
        public float TargetDistance;

        /// <summary>
        /// Delta X to target (Target.X - Unit.X) in world pixels.
        /// </summary>
        public float TargetDeltaX;

        /// <summary>
        /// Delta Y to target (Target.Y - Unit.Y) in world pixels.
        /// </summary>
        public float TargetDeltaY;

        /// <summary>
        /// Whether a clear raycast exists between enemy eye and target.
        /// </summary>
        public bool HasLineOfSight;

        /// <summary>
        /// How many simulation ticks have elapsed since the target was last visible.
        ///
        /// <para><b>This is a readout, not the chase timeout — it used to be both, and that was the
        /// bug.</b> <c>TickCombatChase</c> dropped out of the chase on <c>TargetUnit == null</c> and
        /// then tested <c>TimeSinceTargetSpottedTicks &gt; 90</c>; because <c>EnemySensors.Evaluate</c>
        /// nulls <c>TargetUnit</c> on the first obscured tick, the first test always fired and the
        /// 90-tick grace period was unreachable. The retention budget is
        /// <see cref="AlertTimerTicks"/> (<c>aiSpok</c>) — see <c>EnemyAwarenessMath</c>. This field is
        /// still maintained and still shown by the F3 overlay, but nothing branches on it.</para>
        /// </summary>
        public int TimeSinceTargetSpottedTicks;

        /// <summary>
        /// Whether this unit has <b>committed</b> to investigating a sound — AS3's <c>celUnit == null</c>
        /// plus a <c>celX</c>/<c>celY</c> pair set by <c>setCel(null, x, y)</c>.
        ///
        /// <para><b>This is a latch, not a sensor reading.</b> It was previously set every tick the target
        /// was inside a fixed radius and cleared only on physically arriving within 20 px, so a unit that
        /// could not reach the position stayed alert forever. It is now set only when the commit gate
        /// passes (the target's suspicion meter reached <c>maxObs</c>, or the sound was loud enough to
        /// react to at once) and it is cleared on arrival <i>or</i> on dropping back out of the alert
        /// states. The live "am I hearing something right now" reading is
        /// <see cref="HeardNoiseIntensity"/>.</para>
        /// </summary>
        public bool HasHeardNoise;

        /// <summary>
        /// Position of the loudest recent sound, in world pixels. For a sound-only commit this is
        /// <b>not</b> the target's position — AS3 randomises it by ±100 px
        /// (<c>actionscript_project_context.txt:126483</c>), because a sound gives a direction and a
        /// distance, not a point. See <see cref="PFE.Entities.Units.NoiseMath.SoundOnlyCommitSpreadPixels"/>.
        /// </summary>
        public Vector2 LastHeardNoisePosition;

        /// <summary>
        /// The intensity of what this unit can hear <b>this tick</b>, 0 when it can hear nothing — the
        /// graded output of AS3's <c>Unit.listen()</c>.
        ///
        /// <para>Transient by design, and the counterpart of the latched
        /// <see cref="HasHeardNoise"/>. Keeping both is what lets "I heard something" and "I have decided
        /// to go and look" be different facts, which they are in the oracle: the first is recomputed
        /// every frame, the second survives the sound stopping.</para>
        /// </summary>
        public float HeardNoiseIntensity;

        /// <summary>
        /// This tick's vision intensity — AS3 <c>look()</c>'s 20 or 4. Feeds the same suspicion meter
        /// <see cref="HeardNoiseIntensity"/> does, and is kept separately so the overlay can show which
        /// sense is currently doing the work.
        /// </summary>
        public float SeenIntensity;

        /// <summary>
        /// The unit's <b>awareness budget</b> — AS3 <c>UnitZombie.aiSpok</c> — in simulation ticks.
        /// Armed by a sighting (<c>EnemySensors.Evaluate</c>), by an alarm (<c>RaiseAlarm</c>) and by an
        /// ambusher rising (<c>ZombieBrain.Rise</c>); drained one tick at a time in
        /// <c>EnemyBrain.SimTick</c>.
        ///
        /// <para><b>This, not <see cref="TargetUnit"/>, is what keeps a hunt alive.</b> The oracle clears
        /// <c>celUnit</c> the moment the target is not seen but keeps the goal in <c>celX</c>/<c>celY</c>,
        /// so a unit that has lost sight of its target still runs at it until this budget drains. Above
        /// <c>EnemyAwarenessMath.ChaseThresholdTicks</c> the unit is chasing (full run speed); between 1
        /// and that threshold it is searching at alert speed; at 0 it returns to patrol. See
        /// <c>EnemyAwarenessMath</c> for the oracle's numbers and the tick/decrement translation.</para>
        ///
        /// <para><b>Previously documented as an "alert countdown" that nothing armed on sight</b> — which
        /// is why the chase died on the first obscured tick. The name is kept because it is what the F3
        /// overlay prints and what the hearing workstream already references; read it as
        /// <c>aiSpok</c>.</para>
        /// </summary>
        public int AlertTimerTicks;

        /// <summary>
        /// Time remaining in current sub-behavior state in simulation ticks (AS3 'aiTCh').
        /// </summary>
        public int StateTimerTicks;

        /// <summary>
        /// Shock / reaction delay timer in simulation ticks (AS3 'shok').
        /// </summary>
        public int ShockTimerTicks;

        /// <summary>
        /// Cooldown between attacks in simulation ticks.
        /// </summary>
        public int AttackCooldownTicks;

        /// <summary>
        /// Current facing direction (-1 for Left, +1 for Right).
        /// </summary>
        public int FacingDirection = 1;

        /// <summary>
        /// Whether the unit's feet are currently resting on ground or platform.
        /// </summary>
        public bool IsGrounded = true;

        /// <summary>
        /// Clears target information when dropping out of combat.
        /// </summary>
        public void ClearTarget()
        {
            TargetUnit = null;
            HasLineOfSight = false;
            TargetDistanceSq = float.MaxValue;
            TargetDistance = float.MaxValue;
            TargetDeltaX = 0f;
            TargetDeltaY = 0f;
        }
    }
}
