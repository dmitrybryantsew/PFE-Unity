using System.Collections.Generic;
using PFE.Entities.Units;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Entities.Enemies
{
    /// <summary>
    /// Sensory perception engine for enemy units.
    /// Implements ActionScript 3 sensory math (<c>Unit.listen()</c>, <c>Unit.look()</c>,
    /// <c>Unit.findCel()</c>) and evaluates optical line of sight through
    /// <see cref="ITileQueryService"/>.
    ///
    /// <para><b>The acoustic half of this class was wrong in a way that looked like AI tuning.</b> It
    /// asked "is the target inside a fixed 320 px circle" and set a sticky alert flag on a yes. The
    /// oracle's audible distance is the product <c>target.noise × listener.ear × earMult</c>, where
    /// <c>noise</c> is 0 unless the target just did something loud — so a stationary player is
    /// inaudible at any distance, and even a sprint is quieter than the port's constant. The rules now
    /// live in <see cref="NoiseMath"/>, where they can be asserted without a scene; this class is the
    /// wiring. See <c>docs/OnEnemiesAndAi/23_Hearing_Noise_And_Ear_2026-10-07.md</c> for the
    /// diagnosis.</para>
    /// </summary>
    public sealed class EnemySensors
    {
        /// <summary>Maximum sight range in world pixels (40px = 1 tile; 480px = 12 tiles).</summary>
        public float VisionRangePixels = 480f;

        /// <summary>Close proximity radius in pixels where target is detected even behind unit (AS3 'detecting').</summary>
        public float CloseProximityPixels = 40f;

        /// <summary>
        /// The listener's hearing multiplier — AS3 <c>Unit.ear</c>.
        ///
        /// <para><b>This was called <c>HearingMultiplier</c>, and the name hid what it was.</b> It is the
        /// <i>listener's</i> factor in <c>listen()</c>'s product, one of the two numbers that decide
        /// whether a sound is audible at all — and nothing ever set it from the unit, so every enemy in
        /// the game heard with the same ears. It is now fed from
        /// <see cref="UnitController.Ear"/>, which reads the definition's <c>ear</c>: the XML attribute
        /// every small robot in the roster authors as <c>0</c>, meaning stone deaf. A zombie ambush
        /// overrides it while buried; see <c>ZombieBrain.Bury</c>.</para>
        /// </summary>
        public float Ear = NoiseMath.DefaultEar;

        /// <summary>
        /// The location's <c>earMult</c> — AS3 <c>Location.earMult</c>, halved on easy difficulty.
        ///
        /// <para><b>This is a difficulty scalar with no data source.</b> In the whole oracle
        /// <c>earMult</c> appears four times: declared <c>= 1</c> (<c>:33803</c>), halved once by
        /// <c>setLocDif</c> when <c>globalDif &lt; 2</c> (<c>:32526</c>), and read twice
        /// (<c>:126338</c>, <c>:36411</c>). There is <b>no XML attribute for it</b>, and its only writer
        /// runs once per <c>Location</c> — so it is 1 or 0.5 and nothing else. Nothing in the port's data
        /// can author it, and treating this as a per-room tuning knob would be inventing a feature.</para>
        ///
        /// <para>The port has no runtime <c>globalDif</c>, so this sits at the oracle's declared default of
        /// 1 and nothing halves it. It is placed here rather than on a location object because the port has
        /// no location-acoustics object: this field is the listener's stand-in for the <c>earMult</c> of the
        /// location it is standing in. Wiring the difficulty halving is recorded as an open item rather than
        /// faked.</para>
        /// </summary>
        public float EarMultiplier = NoiseMath.DefaultEarMultiplier;

        /// <summary>
        /// The <b>observer's</b> observation power — AS3 <c>Unit.observ</c>.
        ///
        /// <para><b>The observer's, not the target's</b>, which is the part that is easy to get backwards:
        /// the oracle's call is <c>(player).observation(lookIntensity, this.observ)</c>
        /// (<c>actionscript_project_context.txt:126474</c>) — <c>this</c> is the enemy doing the looking.
        /// It scales only the <i>vision</i> path; hearing is never scaled by it. Fed from
        /// <see cref="UnitController.ObservationPower"/>.</para>
        /// </summary>
        public float ObservationPower;

        /// <summary>
        /// Sensory raycast throttling interval in ticks (mirrors AS3 'aiTCh % 10 == 1' = ~3 Hz).
        /// </summary>
        public int RaycastThrottleInterval = 10;

        /// <summary>
        /// Checks optical line of sight using the unified tile query service.
        /// </summary>
        public bool CheckLineOfSight(Vector2 eyePosPx, Vector2 targetCenterPx, ITileQueryService tileQuery)
        {
            if (tileQuery == null)
            {
                // Headless test without map: unobstructed line of sight
                return true;
            }

            Vector2 diff = targetCenterPx - eyePosPx;
            float dist = diff.magnitude;

            if (dist <= 0.001f)
            {
                return true;
            }

            if (dist > VisionRangePixels)
            {
                return false;
            }

            Vector2 dir = diff / dist;
            TileRaycastHit? hit = tileQuery.Raycast(eyePosPx, dir, dist);

            if (hit.HasValue)
            {
                // Occluded if hit point is between eye and target
                float hitDist = Vector2.Distance(eyePosPx, hit.Value.Point);
                if (hitDist < dist - 5f)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks whether target lies within the unit's forward vision hemisphere or close detection bubble.
        /// </summary>
        public bool CheckVisionAngle(Vector2 eyePosPx, int facing, Vector2 targetCenterPx)
        {
            Vector2 diff = targetCenterPx - eyePosPx;
            float distSq = diff.sqrMagnitude;

            // Close proximity: detected regardless of facing (AS3 'detecting' radius)
            if (distSq <= CloseProximityPixels * CloseProximityPixels)
            {
                return true;
            }

            // Forward facing check: target must be on the side the unit is facing
            float dx = diff.x;
            if (facing > 0 && dx < 0f)
            {
                return false; // Facing right, but target is behind to the left
            }

            if (facing < 0 && dx > 0f)
            {
                return false; // Facing left, but target is behind to the right
            }

            return true;
        }

        /// <summary>
        /// The radius this unit can currently hear a source of <paramref name="targetNoise"/> at —
        /// AS3 <c>Unit.listen()</c>'s <c>param1.noise * this.ear * loc.earMult</c>
        /// (<c>actionscript_project_context.txt:126338</c>).
        ///
        /// <para>Exposed as its own method because the F3 overlay draws it. The sensors need the
        /// <i>intensity</i>; deriving one from the other at both call sites is how a drawn circle and a
        /// tested radius drift apart.</para>
        /// </summary>
        /// <param name="targetNoise">
        /// The <b>target's</b> current noise — <see cref="UnitController.Noise"/>. <c>0</c> whenever the
        /// target is not doing anything loud, and that is the common case.
        /// </param>
        public float HearingRadiusPixels(int targetNoise)
        {
            return NoiseMath.HearingRadius(targetNoise, Ear, EarMultiplier);
        }

        /// <summary>
        /// How audible a source of <paramref name="targetNoise"/> is to this unit — AS3
        /// <c>Unit.listen()</c>'s graded return, <c>(1 − d²/r²) × 4</c>
        /// (<c>actionscript_project_context.txt:126332-126353</c>).
        ///
        /// <para><b>This replaced a bool, and that is the whole fix.</b> The old <c>CheckHearing</c>
        /// answered "is the target inside 320 px", which was wrong three ways at once: the radius was a
        /// constant rather than a product, it did not depend on the target doing anything, and it had no
        /// near/far distinction. A target with <c>noise = 0</c> — one standing still — is now inaudible
        /// at <i>any</i> distance including point blank, because the oracle returns before it computes a
        /// distance at all.</para>
        /// </summary>
        /// <param name="listenerPosPx">This unit's eye, in world pixels.</param>
        /// <param name="targetCenterPx">The target's <b>centre</b>, in world pixels — AS3 compares centre
        /// to centre (<c>Y − scY/2</c> on both sides), which is what
        /// <see cref="GetUnitCenterPixels"/> produces.</param>
        /// <param name="targetNoise">The target's current noise.</param>
        /// <returns><c>0</c> when inaudible; otherwise up to <see cref="NoiseMath.MaxHearingIntensity"/>.</returns>
        public float HearIntensity(Vector2 listenerPosPx, Vector2 targetCenterPx, int targetNoise)
        {
            float distSq = (targetCenterPx - listenerPosPx).sqrMagnitude;
            return NoiseMath.HearingIntensity(targetNoise, Ear, EarMultiplier, distSq);
        }

        public static Vector2 GetUnitCenterPixels(UnitController unit)
        {
            if (unit == null) return Vector2.zero;
            float heightPx = (unit.Stats != null) ? unit.Stats.Height * 100f : 50f;
            return (Vector2)unit.transform.position * 100f + new Vector2(0f, heightPx * 0.5f);
        }

        /// <summary>
        /// Evaluates candidates (e.g. players) and updates the blackboard situational context.
        /// Raycasting is throttled to every <see cref="RaycastThrottleInterval"/> ticks.
        ///
        /// <para><b>What changed, and why it is the reported bug.</b> Hearing used to be a bool, tested
        /// only for a target <i>outside</i> the vision cone and <i>inside</i> the vision range, and any
        /// <c>true</c> set <c>HasHeardNoise</c> — which the brain promotes on the very next tick. So a
        /// player standing still inside a fixed 320 px circle produced a permanent alert pinned to the
        /// exact pixel he was standing on. Three oracle rules are now honoured, and each one alone would
        /// have been enough to break that:</para>
        /// <list type="number">
        /// <item><description><b>Hearing is graded and unconditional.</b> <c>listen()</c> runs for every
        /// candidate every tick with no range pre-cull — the radius <i>is</i> the range, and a 600-noise
        /// rifle shot is audible further than this unit can see.</description></item>
        /// <item><description><b>Sight and sound feed the same meter.</b> Both call <c>observation()</c>
        /// on the target (<c>:126469-126474</c>), and nothing is committed until
        /// <c>obs &gt;= maxObs</c>. A single tick of perception is no longer enough — the unit has to
        /// <i>accumulate</i> suspicion.</description></item>
        /// <item><description><b>A loud sound commits at once; a faint one only accumulates.</b>
        /// <c>if(_loc3_ &gt; 1)</c> (<c>:126487</c>) — within <c>0.866 · r</c>. And a sound-only commit
        /// sends the unit to the target's position <b>±100 px</b>, not to the pixel, which is what makes
        /// a quiet player survivable.</description></item>
        /// </list>
        /// </summary>
        public void Evaluate(
            EnemyBlackboard blackboard,
            Vector2 eyePosPx,
            int facing,
            ITileQueryService tileQuery,
            IReadOnlyList<UnitController> candidates,
            int tickIndex)
        {
            if (blackboard == null)
            {
                return;
            }

            bool shouldRaycast = (tickIndex % RaycastThrottleInterval == 0);

            UnitController seenCandidate = null;
            float seenIntensity = 0f;
            UnitController heardCandidate = null;
            float heardIntensity = 0f;
            Vector2 heardCenterPx = Vector2.zero;

            float closestDistSq = float.MaxValue;
            Vector2 bestTargetCenterPx = Vector2.zero;

            if (candidates != null && candidates.Count > 0)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    UnitController candidate = candidates[i];
                    if (candidate == null || !candidate.IsAlive)
                    {
                        continue;
                    }

                    Vector2 targetCenterPx = GetUnitCenterPixels(candidate);
                    Vector2 diff = targetCenterPx - eyePosPx;
                    float distSq = diff.sqrMagnitude;

                    // ── Hearing, BEFORE the range pre-cull. ──────────────────────────────────────
                    //
                    // AS3's `listen()` is not gated on the vision range. The old loop started with
                    // `distSq > VisionRange² -> continue`, which made a 600 px gunshot inaudible past
                    // 480 px — the one sound the whole system exists for.
                    float hear = HearIntensity(eyePosPx, targetCenterPx, candidate.Noise);
                    if (hear > heardIntensity)
                    {
                        heardIntensity = hear;
                        heardCandidate = candidate;
                        heardCenterPx = targetCenterPx;
                    }

                    // ── Vision. ──────────────────────────────────────────────────────────────────
                    if (distSq > VisionRangePixels * VisionRangePixels)
                    {
                        continue;
                    }

                    if (!CheckVisionAngle(eyePosPx, facing, targetCenterPx))
                    {
                        continue;
                    }

                    bool los = blackboard.HasLineOfSight;
                    if (shouldRaycast)
                    {
                        los = CheckLineOfSight(eyePosPx, targetCenterPx, tileQuery);
                    }

                    if (los && distSq < closestDistSq)
                    {
                        closestDistSq = distSq;
                        seenCandidate = candidate;
                        bestTargetCenterPx = targetCenterPx;
                        seenIntensity = NoiseMath.VisionIntensity(
                            distSq, CloseProximityPixels, VisionRangePixels);
                    }
                }
            }

            blackboard.HeardNoiseIntensity = heardIntensity;
            blackboard.SeenIntensity = seenIntensity;

            // ── Feed the target's suspicion meter. ───────────────────────────────────────────────
            //
            // AS3 `findCel()`: `observation(_loc3_)` for sound with no second argument, and
            // `observation(_loc4_, this.observ)` for sight. The observer's own `observ` scales only the
            // vision path — that asymmetry is the oracle's, and it is why a sharp-eyed unit notices
            // faster without being able to hear further.
            if (seenCandidate != null && seenIntensity > 0f)
            {
                seenCandidate.AddSuspicion(NoiseMath.ObservationInput(seenIntensity, ObservationPower));
            }

            if (heardCandidate != null && heardIntensity > 0f)
            {
                heardCandidate.AddSuspicion(heardIntensity);
            }

            // ── Commit. ─────────────────────────────────────────────────────────────────────────
            //
            // Sight wins when both fire, matching the oracle's `if / else if` — and a sighting hands
            // over the exact position, because a sighting IS a position.
            if (seenCandidate != null && seenCandidate.IsObserved)
            {
                blackboard.TargetUnit = seenCandidate;
                blackboard.HasLineOfSight = true;
                blackboard.TargetDistanceSq = closestDistSq;
                blackboard.TargetDistance = Mathf.Sqrt(closestDistSq);
                blackboard.LastKnownTargetPosition = (Vector2)seenCandidate.transform.position * 100f;
                blackboard.HasLastKnownTargetPosition = true;
                blackboard.TargetDeltaX = bestTargetCenterPx.x - eyePosPx.x;
                blackboard.TargetDeltaY = bestTargetCenterPx.y - eyePosPx.y;
                blackboard.TimeSinceTargetSpottedTicks = 0;

                // AS3 `aiSpok = maxSpok + 10` (`UnitZombie.as:657`), inside the `if(findCel())` block and
                // only on the branch where `celUnit` was actually set — i.e. on a SIGHTING, which is
                // exactly this commit. Without it the unit has no budget to spend once the target steps
                // out of view, and the chase dies on the first obscured tick. See EnemyAwarenessMath.
                //
                // An assignment, not a `Max`: the oracle assigns here, and the value is the largest of
                // the three arming sites, so an assignment can only ever lengthen a shorter alarm.
                blackboard.AlertTimerTicks = EnemyAwarenessMath.FullAwarenessTicks;
                return;
            }

            if (heardCandidate != null &&
                (heardCandidate.IsObserved || NoiseMath.CommitsImmediately(heardIntensity)))
            {
                blackboard.HasHeardNoise = true;

                // A sound-only commit is deliberately imprecise: ±100 px, two independent draws. This is
                // what makes a quiet player survivable — the unit walks to somewhere near you and then
                // has to search, rather than to the pixel you are standing on.
                Vector2 jitter = NoiseMath.RandomisedOffset(
                    NoiseMath.SoundOnlyCommitSpreadPixels,
                    UnityEngine.Random.value,
                    UnityEngine.Random.value);

                blackboard.LastHeardNoisePosition = heardCenterPx + jitter;

                // AS3 `aiSpok = maxSpok - 1` (`UnitZombie.as:661`) — the `else` of `if(celUnit)` inside
                // `if(findCel())`, which is exactly this branch: the noise meter filled, so the unit
                // commits, but it has NOT seen the target, so `celUnit` stays null and the goal is a
                // jittered position. This is an ASSIGNMENT, not a raise, and the value is deliberately
                // one short of `maxSpok` (`EnemyAwarenessMath.SoundOnlyAwarenessTicks` = 290) so that a
                // sound on its own can never promote the unit into the chase — it buys the alert state
                // only, and `IsChasing` is false at 290 against a threshold of 300.
                //
                // It is wired here because leaving it out is not neutral: the sound path then had no
                // budget at all and fell back on the 90-tick `StateTimerTicks` — about 3 s of searching
                // where the oracle gives about 9.7 s — and `SoundOnlyAwarenessTicks` was a constant with
                // a test and no caller.
                blackboard.AlertTimerTicks = EnemyAwarenessMath.SoundOnlyAwarenessTicks;
            }

            if (seenCandidate == null)
            {
                blackboard.TargetUnit = null;
                blackboard.HasLineOfSight = false;
                blackboard.TimeSinceTargetSpottedTicks++;
            }
            else
            {
                // Seen, but the meter has not filled yet — the unit is looking straight at the player and
                // has not yet decided. AS3 reaches this state every time a target appears at the far edge
                // of the sight range, and it is where `observ` earns its keep.
                blackboard.HasLineOfSight = true;
                blackboard.TimeSinceTargetSpottedTicks = 0;
            }
        }

        /// <summary>
        /// Raises alert level upon taking damage or hearing an alarm call (AS3 <c>alarma()</c>).
        ///
        /// <para>Note this is <c>alarma</c>, not <c>budilo</c>: it alerts <i>this</i> unit at a position.
        /// The propagation half — telling the neighbours — is <c>EnemyBrain.Budilo</c>, which is gated on
        /// each receiver's own <c>ear</c> and on whether it is a robot.</para>
        /// </summary>
        public void RaiseAlarm(EnemyBlackboard blackboard, Vector2 alarmSourcePx, int durationTicks = 60)
        {
            if (blackboard == null) return;
            blackboard.AlertTimerTicks = Mathf.Max(blackboard.AlertTimerTicks, durationTicks);
            blackboard.LastHeardNoisePosition = alarmSourcePx;
            blackboard.HasHeardNoise = true;
            // AS3 `UnitZombie.alarma()` / `UnitRaider.alarma()` — `shok = Math.floor(Math.random() * 15
            // + 5)` (UnitZombie.as:346, UnitRaider.as:493), i.e. 5..19. This was 3..7, which matches
            // neither end of the oracle's range.
            //
            // It is an ASSIGNMENT, not a raise — `StaggerMath.AlarmTicks` documents why that matters.
            // Note also that the oracle gates this assignment on the unit being caught unawares
            // (`UnitZombie.as:334-347`); this call site does not, because `RaiseAlarm` is also the
            // port's "heard a noise" path and the state test belongs to the caller. See the open task
            // recorded in `StaggerMath`'s remarks.
            blackboard.ShockTimerTicks =
                StaggerMath.AlarmTicks(Random.Range(0, StaggerMath.AlarmRollRange));
        }
    }
}
