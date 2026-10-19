using UnityEngine;

namespace PFE.Systems.Security
{
    /// <summary>
    /// A room's alarm — AS3 <c>Location.t_alarm</c> / <c>t_alarmsp</c> plus the <c>t_budilo</c> pulse
    /// that <c>Interact</c> owns (<c>Location.as:235-237</c>, <c>:2912-2920</c>, <c>:3429-3440</c>;
    /// <c>Interact.as:638-647</c>).
    ///
    /// <para><b>The alarm is three timers, not one flag.</b>
    /// <list type="bullet">
    /// <item><see cref="TicksLeft"/> — <c>t_alarm</c>. While it is positive the player
    /// <b>cannot leave the land</b>: <c>World.possiblyOut()</c> answers <c>2</c>
    /// (<c>World.as:1455-1458</c>), and every exit refuses with <c>noOutLoc</c>
    /// (<c>Interact.as:1560-1563</c>).</item>
    /// <item><see cref="SpawnTicksLeft"/> — <c>t_alarmsp</c>. Counts to zero once, then a
    /// reinforcement spawns and the timer re-arms to a short random interval, repeating until
    /// <see cref="SpawnBudget"/> is spent (<c>Location.as:2977-2998</c>).</item>
    /// <item><see cref="PulseTicksLeft"/> — <c>t_budilo</c>. Every 30 ticks it re-alerts every unit in
    /// earshot at the alarm's own position, which is what makes an alarm a <i>hunt</i> rather than a
    /// one-off (<c>Interact.as:638-647</c>).</item>
    /// </list></para>
    ///
    /// <para><b>Duration.</b> <c>signal(param1 = 300)</c> — 300 ticks at 30 Hz is <b>10 seconds</b>,
    /// which is long enough to cross a room and short enough that a player can wait it out.</para>
    /// </summary>
    public sealed class RoomAlarm
    {
        /// <summary>AS3 <c>Location.signal</c>'s default duration (<c>:2912</c>).</summary>
        public const int DefaultSignalTicks = 300;

        /// <summary>AS3 <c>Interact.alarm()</c>'s <c>t_budilo = 240</c> (<c>:1446</c>).</summary>
        public const int BudiloTicks = 240;

        /// <summary>How often the <c>budilo</c> pulse fires while it lasts (<c>Interact.as:640</c>).</summary>
        public const int BudiloPeriod = 30;

        /// <summary>Radius the pulse alerts at — <c>loc.budilo(X, Y, 1500)</c> (<c>:642</c>).</summary>
        public const float BudiloRadius = 1500f;

        /// <summary>AS3 <c>t_alarm</c> — ticks left in the lockout.</summary>
        public int TicksLeft { get; private set; }

        /// <summary>AS3 <c>t_alarmsp</c> — ticks until the next reinforcement.</summary>
        public int SpawnTicksLeft { get; private set; }

        /// <summary>AS3 <c>t_budilo</c> — ticks left in the alerting pulse.</summary>
        public int PulseTicksLeft { get; private set; }

        /// <summary>AS3 <c>kolEnSpawn</c> — how many more reinforcements this room may produce.</summary>
        public int SpawnBudget { get; set; }

        /// <summary>Where the alarm was raised, in room-local pixels — the pulse origin.</summary>
        public Vector2 Origin { get; private set; }

        /// <summary>How many times this alarm has been raised, for the F2 readout.</summary>
        public int RaiseCount { get; private set; }

        /// <summary>How many reinforcements this alarm has actually produced.</summary>
        public int SpawnedCount { get; private set; }

        /// <summary>True while the land exit is refused.</summary>
        public bool IsActive => TicksLeft > 0;

        /// <summary>
        /// Raise the alarm — AS3 <c>Location.signal(param1 = 300)</c> (<c>:2912-2920</c>).
        ///
        /// <para><b>Re-raising extends, it does not stack.</b> AS3 assigns both timers, so a second
        /// signal resets the countdown to a fresh 300 rather than adding to it. A second signal also
        /// re-rolls the first spawn delay.</para>
        /// </summary>
        public void Signal(Vector2 origin, int ticks = DefaultSignalTicks, System.Func<float> roll = null)
        {
            TicksLeft = ticks;

            // `t_alarmsp = floor(param1 * random() * 0.25 + 0.25)` — so the first reinforcement lands
            // somewhere in the first quarter of the alarm, never later.
            float r = roll != null ? roll() : Random.value;
            SpawnTicksLeft = Mathf.FloorToInt(ticks * r * 0.25f + 0.25f);

            PulseTicksLeft = BudiloTicks;
            Origin = origin;
            RaiseCount++;
        }

        /// <summary>
        /// <c>Interact.alarm()</c>'s pulse half (<c>:1446-1449</c>) — <b>without</b> re-rolling the
        /// spawn timer, because that call does not touch <c>t_alarmsp</c>.
        /// </summary>
        public void StartPulse()
        {
            PulseTicksLeft = BudiloTicks;
        }

        /// <summary>Clear everything — the alarm panel was shut off.</summary>
        public void Clear()
        {
            TicksLeft = 0;
            SpawnTicksLeft = 0;
            PulseTicksLeft = 0;
        }

        /// <summary>What one tick produced, so the caller does not have to re-read three counters.</summary>
        public struct TickResult
        {
            /// <summary>The <c>budilo</c> pulse should fire this tick.</summary>
            public bool Pulse;

            /// <summary>A reinforcement should be spawned this tick.</summary>
            public bool Spawn;

            /// <summary>The alarm ran out on this tick.</summary>
            public bool Expired;
        }

        /// <summary>
        /// One simulation tick — AS3 <c>Location.step</c>'s alarm tail (<c>:3429-3440</c>) merged with
        /// <c>Interact.step</c>'s pulse (<c>:638-647</c>).
        ///
        /// <para><b>Order is the oracle's:</b> <c>t_alarm</c> decrements first, then <c>t_alarmsp</c>,
        /// and the pulse is checked before its own decrement so the first pulse lands on the tick the
        /// alarm is raised +1.</para>
        /// </summary>
        public TickResult Tick(System.Func<float> roll = null)
        {
            var result = new TickResult();
            if (TicksLeft <= 0 && PulseTicksLeft <= 0 && SpawnTicksLeft <= 0)
            {
                return result;
            }

            bool wasActive = TicksLeft > 0;

            if (TicksLeft > 0)
            {
                TicksLeft--;
                if (TicksLeft == 0) result.Expired = wasActive;
            }

            if (SpawnTicksLeft > 0)
            {
                SpawnTicksLeft--;
                if (SpawnTicksLeft == 0 && SpawnBudget > 0)
                {
                    result.Spawn = true;
                    SpawnedCount++;
                    SpawnBudget--;

                    // `if(!param1) t_alarmsp = floor(random() * 30);` — the stream continues on a short
                    // cadence until the budget is gone.
                    float r = roll != null ? roll() : Random.value;
                    SpawnTicksLeft = Mathf.FloorToInt(r * 30f);
                    if (SpawnTicksLeft <= 0) SpawnTicksLeft = 1;
                }
            }

            if (PulseTicksLeft > 0)
            {
                if (PulseTicksLeft % BudiloPeriod == 0)
                {
                    result.Pulse = true;
                }

                PulseTicksLeft--;
            }

            return result;
        }
    }
}
