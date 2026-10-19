using NUnit.Framework;
using PFE.Systems.Security;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Security
{
    /// <summary>
    /// Pins <see cref="RoomAlarm"/> — AS3's <c>Location.t_alarm</c> / <c>t_alarmsp</c>
    /// (<c>Location.as:2912-2920</c>, <c>:3429-3440</c>) plus the <c>t_budilo</c> pulse
    /// <c>Interact</c> owns (<c>Interact.as:638-647</c>).
    ///
    /// <para><b>Why this needs a fixture rather than a playtest.</b> The alarm is <b>three timers</b>,
    /// not a flag, and each one fails differently: <c>t_alarm</c> is the land-exit lockout
    /// (<c>World.as:1455-1458</c>), <c>t_alarmsp</c> is the reinforcement cadence, <c>t_budilo</c> is
    /// the re-alerting pulse. A port that collapsed them into one boolean would still "work" — an alarm
    /// would raise, units would react, and the only symptom would be that walking out of the land is
    /// allowed ten seconds early, or that reinforcements never stop. Both are invisible in the first
    /// ten seconds of a playtest and obvious here.</para>
    ///
    /// <para><b>The specific traps.</b> Re-raising <i>extends</i> rather than stacks
    /// (<c>signal()</c> assigns both timers). The pulse fires on a modulus, so it must fire exactly
    /// <c>BudiloTicks / BudiloPeriod</c> times and not once more at zero. And the reinforcement timer
    /// re-arms to a <i>short random</i> interval after each spawn (<c>enemySpawn</c>'s
    /// <c>t_alarmsp = floor(random() * 30)</c>), which is what makes an alarm a sustained hunt instead
    /// of a single visitor.</para>
    /// </summary>
    public class RoomAlarmTests
    {
        /// <summary>Replays fixed draws; a dry queue repeats the last value.</summary>
        private sealed class Rolls
        {
            private readonly System.Collections.Generic.Queue<float> _values;
            private float _last;

            public Rolls(params float[] values)
            {
                _values = new System.Collections.Generic.Queue<float>(values);
                if (values.Length > 0) _last = values[values.Length - 1];
            }

            public float Next() => _values.Count > 0 ? (_last = _values.Dequeue()) : _last;
        }

        // ── Raising ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>signal(300)</c> sets <c>t_alarm</c> to the duration and <c>t_alarmsp</c> to
        /// <c>floor(300 * r * 0.25 + 0.25)</c> — so the first reinforcement lands inside the first
        /// quarter of the alarm, never after it, and never at exactly zero for any real draw.
        /// </summary>
        [Test]
        public void Signal_SetsTheLockoutAndTheFirstSpawnDelay_InsideTheFirstQuarter()
        {
            var alarm = new RoomAlarm();
            alarm.Signal(new Vector2(120f, 340f), RoomAlarm.DefaultSignalTicks, new Rolls(0f).Next);

            Assert.That(alarm.TicksLeft, Is.EqualTo(300), "signal()'s default is 300 ticks = 10 s at 30 Hz.");
            Assert.That(alarm.SpawnTicksLeft, Is.EqualTo(0), "floor(300 * 0 * 0.25 + 0.25) = 0.");
            Assert.That(alarm.PulseTicksLeft, Is.EqualTo(RoomAlarm.BudiloTicks));
            Assert.That(alarm.Origin, Is.EqualTo(new Vector2(120f, 340f)));
            Assert.That(alarm.RaiseCount, Is.EqualTo(1));
            Assert.That(alarm.IsActive, Is.True);

            var high = new RoomAlarm();
            high.Signal(Vector2.zero, 300, new Rolls(1f).Next);
            Assert.That(high.SpawnTicksLeft, Is.EqualTo(75),
                "floor(300 * 1 * 0.25 + 0.25) = 75 — the whole point is that it is bounded by a quarter.");
        }

        /// <summary>
        /// A second signal <b>extends</b> the alarm rather than adding to it: <c>signal()</c> assigns
        /// both timers. A port that accumulated would let a player who keeps tripping tripwires build an
        /// alarm that never ends.
        /// </summary>
        [Test]
        public void ReRaising_ExtendsTheAlarm_ItDoesNotStack()
        {
            var alarm = new RoomAlarm();
            alarm.Signal(Vector2.zero, 300, new Rolls(0.5f).Next);

            for (int i = 0; i < 100; i++) alarm.Tick(new Rolls(0.5f).Next);
            Assert.That(alarm.TicksLeft, Is.EqualTo(200), "premise: 100 of 300 ticks spent.");

            alarm.Signal(Vector2.zero, 300, new Rolls(0.5f).Next);
            Assert.That(alarm.TicksLeft, Is.EqualTo(300), "a re-raise is a fresh 300, not 500.");
            Assert.That(alarm.RaiseCount, Is.EqualTo(2));
        }

        /// <summary>
        /// <c>Interact.alarm()</c> sets <c>t_budilo = 240</c> without touching <c>t_alarmsp</c>
        /// (<c>:1446-1449</c>) — the pulse can be restarted without moving the reinforcement clock.
        /// </summary>
        [Test]
        public void StartPulse_DoesNotDisturbTheSpawnTimer()
        {
            var alarm = new RoomAlarm();
            alarm.Signal(Vector2.zero, 300, new Rolls(1f).Next);
            int spawnDelay = alarm.SpawnTicksLeft;

            alarm.StartPulse();

            Assert.That(alarm.PulseTicksLeft, Is.EqualTo(RoomAlarm.BudiloTicks));
            Assert.That(alarm.SpawnTicksLeft, Is.EqualTo(spawnDelay));
        }

        // ── Ticking ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The pulse fires on a modulus, before its own decrement, so a full 240 ticks produce exactly
        /// eight pulses (240/30) and <b>not</b> a ninth at zero.
        /// </summary>
        [Test]
        public void ThePulse_FiresEightTimesOverItsLifetime()
        {
            var alarm = new RoomAlarm();
            alarm.Signal(Vector2.zero, 300, new Rolls(1f).Next);

            int pulses = 0;
            for (int i = 0; i < RoomAlarm.BudiloTicks; i++)
            {
                if (alarm.Tick(new Rolls(1f).Next).Pulse) pulses++;
            }

            Assert.That(pulses, Is.EqualTo(RoomAlarm.BudiloTicks / RoomAlarm.BudiloPeriod),
                "240 ticks at one pulse per 30 = 8. A ninth would mean the modulus is checked after the "
                + "decrement, i.e. the pulse fires on the tick AFTER it should.");
            Assert.That(alarm.PulseTicksLeft, Is.EqualTo(0));
        }

        /// <summary>
        /// The lockout expires <b>once</b>, on the tick it reaches zero — not on every later tick. A
        /// repeated <c>Expired</c> would re-run the "the alarm is over" reaction every frame.
        /// </summary>
        [Test]
        public void TheLockout_ExpiresExactlyOnce()
        {
            var alarm = new RoomAlarm();
            alarm.Signal(Vector2.zero, 10, new Rolls(1f).Next);

            int expiries = 0;
            for (int i = 0; i < 40; i++)
            {
                if (alarm.Tick(new Rolls(1f).Next).Expired) expiries++;
            }

            Assert.That(expiries, Is.EqualTo(1));
            Assert.That(alarm.TicksLeft, Is.EqualTo(0));
            Assert.That(alarm.IsActive, Is.False, "and the land exit is allowed again.");
        }

        /// <summary>
        /// A reinforcement needs <b>budget</b>: <c>enemySpawn</c> opens with
        /// <c>if(this.kolEnSpawn &lt;= 0 …) return;</c> (<c>:2979</c>). Without the guard, an alarm in a
        /// room with no garrison left would spawn forever.
        /// </summary>
        [Test]
        public void WithNoBudget_NoReinforcementIsEverSpent()
        {
            var alarm = new RoomAlarm { SpawnBudget = 0 };
            alarm.Signal(Vector2.zero, 300, new Rolls(1f).Next);

            int spawns = 0;
            for (int i = 0; i < 300; i++)
            {
                if (alarm.Tick(new Rolls(1f).Next).Spawn) spawns++;
            }

            Assert.That(spawns, Is.EqualTo(0));
            Assert.That(alarm.SpawnedCount, Is.EqualTo(0));
        }

        /// <summary>
        /// With a budget, each spawn spends one and re-arms to a short random interval
        /// (<c>t_alarmsp = floor(random() * 30)</c>, <c>:2983-2987</c>) — so a budget of two produces two
        /// reinforcements and then stops, even though the alarm is still running.
        /// </summary>
        [Test]
        public void ABudgetOfTwo_ProducesTwoReinforcements_ThenStops()
        {
            var alarm = new RoomAlarm { SpawnBudget = 2 };
            alarm.Signal(Vector2.zero, 300, new Rolls(1f).Next);   // first delay = 75

            int spawns = 0;
            for (int i = 0; i < 300; i++)
            {
                if (alarm.Tick(new Rolls(1f).Next).Spawn) spawns++;
            }

            Assert.That(spawns, Is.EqualTo(2));
            Assert.That(alarm.SpawnedCount, Is.EqualTo(2));
            Assert.That(alarm.SpawnBudget, Is.EqualTo(0));
            Assert.That(alarm.IsActive, Is.True, "the lockout is independent of the garrison.");
        }

        /// <summary>
        /// A cleared alarm stops all three timers — the panel's <c>allAct == "alarm"</c> branch
        /// (<c>Interact.as:1655-1659</c>) is the only way to end one early.
        /// </summary>
        [Test]
        public void Clear_StopsAllThreeTimers()
        {
            var alarm = new RoomAlarm { SpawnBudget = 5 };
            alarm.Signal(Vector2.zero, 300, new Rolls(1f).Next);

            alarm.Clear();

            Assert.That(alarm.TicksLeft, Is.EqualTo(0));
            Assert.That(alarm.SpawnTicksLeft, Is.EqualTo(0));
            Assert.That(alarm.PulseTicksLeft, Is.EqualTo(0));
            Assert.That(alarm.IsActive, Is.False);

            RoomAlarm.TickResult tick = alarm.Tick(new Rolls(1f).Next);
            Assert.That(tick.Pulse || tick.Spawn || tick.Expired, Is.False,
                "a cleared alarm is inert, not merely quiet.");
        }

        /// <summary>
        /// An untouched alarm does nothing at all — the idle guard at the top of <c>Tick</c>. Without it
        /// a fresh alarm would report <c>Pulse</c> on its first tick because
        /// <c>0 % 30 == 0</c>, i.e. every room would pulse once the moment the tab created it.
        /// </summary>
        [Test]
        public void AFreshAlarm_IsInert()
        {
            var alarm = new RoomAlarm();

            RoomAlarm.TickResult tick = alarm.Tick(new Rolls(0f).Next);

            Assert.That(tick.Pulse, Is.False, "0 % 30 == 0 must not count as a pulse.");
            Assert.That(tick.Spawn, Is.False);
            Assert.That(tick.Expired, Is.False);
            Assert.That(alarm.IsActive, Is.False);
        }
    }
}
