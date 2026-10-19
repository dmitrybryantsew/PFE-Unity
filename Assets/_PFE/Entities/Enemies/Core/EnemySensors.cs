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
        /// <summary>
        /// Which half of the plane this unit's vision cone covers.
        ///
        /// <para><b>AS3's cone is not one shape.</b> <c>Unit.putLoc</c>-style turret tables pick an aim
        /// angle per mount kind, and the cone around it is <c>vAngle ± vKonus/2</c>
        /// (<c>Unit.as:4602-4617</c>). A wall turret authors <c>vKonus = Math.PI / 2</c>
        /// (<c>UnitTurret.as:204</c>) and then sweeps <c>aRot</c> every <c>period</c>
        /// (<c>UnitTurret.as:252-253</c>, <c>:606-615</c>), so:</para>
        /// <list type="bullet">
        /// <item><description><c>vis='right'</c> sweeps <c>[45,-45]</c> — the whole right half-plane across the sweep.</description></item>
        /// <item><description><c>vis='left'</c> sweeps <c>[135,-135]</c> — the whole left half-plane.</description></item>
        /// <item><description><c>vis='up'</c> sweeps <c>[-45,-135]</c> — the whole upper half-plane.</description></item>
        /// <item><description><c>vis='down'</c> sweeps <c>[45,135]</c> — the whole lower half-plane.</description></item>
        /// </list>
        /// <para>The port has no continuous sweep for a stationary trap, so it models the
        /// <b>sweep's aggregate</b> rather than one frame's quadrant: slightly wider than any single
        /// AS3 frame, identical in direction. A right-facing turret therefore still acquires a target
        /// the oracle's current frame would miss — the sweep gets there within <c>period</c> ticks.</para>
        ///
        /// <para><b>Only turrets set this.</b> <c>@vis</c> is read off the placement by
        /// <c>UnitTurret</c> alone (<c>UnitTurret.as:136-139</c>) and only honoured for mount tips 2
        /// and 5. The default keeps every other enemy — and every horizontal turret placement — on
        /// the existing <see cref="EnemyBlackboard.FacingDirection"/> hemisphere.</para>
        /// </summary>
        public VisionCone Cone = VisionCone.Horizontal;

        /// <summary>
        /// The shape of a unit's vision half-plane. See <see cref="Cone"/>.
        /// </summary>
        public enum VisionCone
        {
            /// <summary>A half-plane bounded by a vertical line through the eye — the facing side.</summary>
            Horizontal = 0,

            /// <summary>The upper half-plane: every point at or above the eye's height.</summary>
            Up = 1,

            /// <summary>The lower half-plane: every point at or below the eye's height.</summary>
            Down = 2
        }

        /// <summary>
        /// Maximum sight range in world pixels at a <b>neutral</b> target (40px = 1 tile; 480px = 12
        /// tiles).
        ///
        /// <para><b>It is no longer the range an actual target is tested at.</b> AS3's <c>look()</c>
        /// computes the range <i>per target</i> from that target's own stealth
        /// (<c>unit/Unit.as:4587</c>), so this field is now the observer's range against a target at
        /// <see cref="StealthMath.ReferenceVisibility"/> with no cloak and no reveal. See
        /// <see cref="EffectiveSightRangePixels"/>. It is kept as the drawn radius for the neutral case
        /// and as the turret-aggregate model's base; the F3 overlay draws the <i>effective</i> range.</para>
        /// </summary>
        public float VisionRangePixels = 480f;

        /// <summary>
        /// The <b>observer's</b> own vision scale — AS3 <c>Unit.vision</c>, the
        /// <c>(param3 ? param3 : this.vision)</c> fallback at the end of <c>look()</c>'s range product
        /// (<c>unit/Unit.as:4587</c>).
        ///
        /// <para><b>Separate from <see cref="VisionRangePixels"/>, and separate from
        /// <see cref="ObservationPower"/>.</b> The three are easy to conflate and none of them is the
        /// others: <c>vision</c> scales the <i>sight range</i>, <c>observ</c> scales the <i>rate</i> at
        /// which suspicion accumulates, and <c>VisionRangePixels</c> is this port's absolute
        /// calibration. The oracle's declared default is 1, and no unit in the shipped data authors a
        /// different one, so this stays 1 unless the data says otherwise.</para>
        /// </summary>
        public float VisionPower = 1f;

        /// <summary>
        /// The location's <c>visMult</c> — AS3 <c>Location.visMult</c>, the room/land brightness
        /// multiplier in <c>look()</c>'s range product (<c>unit/Unit.as:4587</c>).
        ///
        /// <para><b>This field used to be a hook with nothing on the other end, and that cost a
        /// play-test.</b> It was declared, multiplied into <see cref="EffectiveSightRangePixels"/>, and
        /// <b>assigned nowhere</b> — so every observer saw at the neutral <c>1</c> while the oracle
        /// multiplied by the room's own value. The surf rooms author <c>vis='10'</c>, so their sight
        /// range was <b>ten times</b> shorter than the original's. The symptom was read as "Calamity
        /// can't see the raiders", which was literally true: 480 px against a 573 px gap, where the
        /// oracle's range is 4800 px.</para>
        ///
        /// <para>Now fed from <c>RoomEnvironment.visibilityMultiplier</c> by
        /// <c>EnemyController.SetLocationVisibilityMultiplier</c>, which <c>RoomUnitSpawner</c> calls
        /// with the room's value. The default stays the oracle's neutral 1, so a unit spawned with no
        /// room (a test, the training rig) is unchanged.</para>
        /// </summary>
        public float LocationVisiMult = StealthMath.DefaultVisiMult;

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
        /// <para><b>Now wired, and only from difficulty.</b> <c>EnemyController.SetLocationEarMultiplier</c>
        /// pushes the room's value in at spawn time, sourced from <c>RoomDifficulty.earMult</c>, which
        /// <c>LocationDifficultyPlanner</c> halves when the run's global difficulty is below 2 — exactly the
        /// oracle's single writer. It is still true that no XML attribute can author it, so a per-room value
        /// other than 1 or 0.5 is not expressible and must not be invented; the field is placed here rather
        /// than on a location object because the port has no location-acoustics object, so this is the
        /// listener's stand-in for the <c>earMult</c> of the location it is standing in.</para>
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
        /// Whether the facing cone is skipped — AS3 <c>look(param1, param2)</c>'s <c>param2</c>
        /// (<c>unit/Unit.as:4560</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The cone is opt-out in the oracle, and the NPC is the caller that opts out.</b>
        /// <c>UnitNPC.findCel</c> is
        /// <c>for each(_loc2_ in loc.units) if(look(_loc2_,true)) { setCel(_loc2_); return true; }</c>
        /// (<c>fe/unit/UnitNPC.as:444-460</c>) — the second argument is a literal <c>true</c>, and the
        /// only two places <c>look()</c> reads it are the behind-the-back rejection
        /// (<c>:4598</c> <c>if(this.vKonus == 0 &amp;&amp; !param2 &amp;&amp; _loc5_ * storona &lt; 0 …)</c>)
        /// and the vertical-angle penalty (<c>:4589</c>). So an NPC's acquisition is <b>360°</b>: a
        /// Calamity facing away from a raider still finds him, which is exactly what the surf cutscene
        /// needs when the script drops him on a perch and tells him to fight.</para>
        ///
        /// <para>The base <c>Unit.findCel</c> passes <c>this.overLook || param1</c> instead
        /// (<c>:4671</c>, <c>:4693</c>), so an ordinary raider keeps the cone unless the data says
        /// otherwise — which is why this is a per-observer flag and not a change to the default.</para>
        /// </remarks>
        public bool Omnidirectional;

        /// <summary>
        /// Whether a sighting commits to a target without first filling that target's suspicion meter —
        /// the other half of <c>UnitNPC.findCel</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Two different <c>findCel</c>s, two different commit rules.</b> The base
        /// <c>Unit.findCel</c> commits only when <c>obs &gt;= maxObs</c>
        /// (<c>unit/Unit.as:4675</c>) — for the player it accumulates first, and for a non-player
        /// candidate it commits on <c>look() &gt; 0.5</c> (<c>:4693</c>). The NPC override has no meter
        /// at all: <c>look()</c> returning anything non-zero calls <c>setCel</c> immediately
        /// (<c>UnitNPC.as:451-455</c>).</para>
        ///
        /// <para>The port applied the base rule to everyone, so an NPC's first sighting only
        /// <i>accumulated</i> — <c>TargetUnit</c> stayed null until the meter reached
        /// <see cref="NoiseMath.DefaultMaxObservation"/>, and a meter that never quite filled left him
        /// with no target at all while the raiders, who use the base path, had one. The F3 overlay shows
        /// that asymmetry directly: every raider carries <c>T</c>, the NPC carries none.</para>
        /// </remarks>
        public bool CommitsWithoutObservation;

        /// <summary>
        /// Why the last <see cref="Evaluate"/> rejected its nearest candidate, or
        /// <see cref="RejectionReason.Committed"/> when it acquired one.
        /// </summary>
        /// <remarks>
        /// <b>A diagnosis field, not a behaviour one.</b> Perception has six gates — no candidates,
        /// range, cone, line of sight, the suspicion meter, and the faction filter upstream — and they
        /// all present identically in play as "he does not see them". Without this, every one of them
        /// costs a play-test to tell apart; with it, the first frame that fails names itself.
        /// </remarks>
        public RejectionReason LastRejection { get; private set; }

        /// <summary>Distance in world pixels to the candidate <see cref="LastRejection"/> refers to.</summary>
        public float LastRejectionDistancePixels { get; private set; }

        /// <summary>Which gate refused the nearest candidate. See <see cref="LastRejection"/>.</summary>
        public enum RejectionReason
        {
            /// <summary>Nothing to look at — the candidate pool was empty.</summary>
            NoCandidates = 0,

            /// <summary>A target was acquired; no gate refused it.</summary>
            Committed,

            /// <summary>Beyond <see cref="EffectiveSightRangePixels"/> for that target.</summary>
            Range,

            /// <summary>Behind the observer, with <see cref="Omnidirectional"/> off.</summary>
            Cone,

            /// <summary>In range and in the cone, but the tile ray was blocked.</summary>
            LineOfSight,

            /// <summary>Seen, but the target's suspicion meter has not filled and the observer has no waiver.</summary>
            ObservationMeter,
        }

        /// <summary>
        /// Checks optical line of sight using the unified tile query service.
        ///
        /// <para><b>The range cull is a parameter, because the range is per target.</b> AS3's
        /// <c>look()</c> derives the range from the target's own stealth (<c>unit/Unit.as:4587</c>), so
        /// a hard cull at <see cref="VisionRangePixels"/> would clip a revealed target the oracle still
        /// sees — <c>demask</c> is added outside the multiply, so a firing, cloaked player is visible
        /// <i>beyond</i> the neutral range. <paramref name="maxRangePixels"/> is the caller's
        /// <see cref="EffectiveSightRangePixels"/>; a non-positive value falls back to
        /// <see cref="VisionRangePixels"/>, which is what a caller with no target wants.</para>
        /// </summary>
        /// <param name="maxRangePixels">The cull distance for <i>this</i> target. <c>&lt;= 0</c> means <see cref="VisionRangePixels"/>.</param>
        public bool CheckLineOfSight(
            Vector2 eyePosPx, Vector2 targetCenterPx, ITileQueryService tileQuery, float maxRangePixels = -1f,
            Vector2 rayOriginPx = default)
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

            float range = maxRangePixels > 0f ? maxRangePixels : VisionRangePixels;
            if (dist > range)
            {
                return false;
            }

            Vector2 dir = diff / dist;

            // ── AS3's ray is not drawn from the eye, and it does not start at the body ────────────────
            //
            // `look()` measures its distance from `eyeY = Y - 30` (`Unit.as:4581-4582`) but then walks
            // its occlusion samples from a DIFFERENT point: `_loc14_ = Y - scY * 0.75` and
            // `_loc13_ = X + scX * 0.25 * storona` (`:4623-4624`) — three quarters of the body height
            // above the feet, a quarter width along the facing. And the walk begins at sample
            // `_loc10_ = this.mater ? 1 : 4` (`:4620`), so the first ~4·(maxdelta) of the line is never
            // tested at all.
            //
            // Both halves exist for the same reason and it is the reported bug: a unit standing on a
            // ledge is surrounded by the tile it stands on, and a ray that starts at its feet and is
            // tested from pixel zero is blocked by that ledge before it has left the unit's own tile.
            // Calamity is scripted to `fly 40:7` and fight from the perch he lands on, so a ray from
            // his feet hits the perch and he reports "no vision" while the raiders — standing on open
            // ground — see him perfectly.
            Vector2 origin = rayOriginPx != default ? rayOriginPx : eyePosPx;
            float skipPx = SightRaySkipPixels(diff);

            if (skipPx >= dist)
            {
                // The whole line lies inside the skipped prefix, i.e. the target is closer than the
                // oracle would ever test. `look()` returns non-zero for it (`:4636`), so this does too.
                return true;
            }

            Vector2 start = origin + dir * skipPx;
            float length = dist - skipPx;

            TileRaycastHit? hit = tileQuery.Raycast(start, dir, length);

            if (hit.HasValue)
            {
                // Occluded if hit point is between the ray's start and the target
                float hitDist = Vector2.Distance(start, hit.Value.Point);
                if (hitDist < length - 5f)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// AS3 <c>World.maxdelta</c> (<c>fe/World.as:52</c>) — the sample spacing <c>look()</c> derives
        /// its step count from.
        /// </summary>
        public const float As3MaxDeltaPixels = 9f;

        /// <summary>
        /// The index <c>look()</c> starts its occlusion walk at for a non-<c>mater</c> unit
        /// (<c>Unit.as:4620</c>).
        /// </summary>
        public const int As3FirstSightSample = 4;

        /// <summary>
        /// How far along the sight line AS3's first tested sample sits, in pixels —
        /// <c>4 · (max(|dx|,|dy|) / (floor(max(|dx|,|dy|) / 9) + 1))</c>.
        /// </summary>
        /// <remarks>
        /// Reproducing the skip as a distance rather than as a sample loop keeps the existing
        /// <see cref="ITileQueryService.Raycast"/> seam — the oracle's walk and this one differ only in
        /// where they begin and how finely they step, and only the beginning is load-bearing for
        /// occlusion.
        /// </remarks>
        public static float SightRaySkipPixels(Vector2 diff)
        {
            float maxDelta = Mathf.Max(Mathf.Abs(diff.x), Mathf.Abs(diff.y));
            if (maxDelta <= 0f)
            {
                return 0f;
            }

            int samples = Mathf.FloorToInt(maxDelta / As3MaxDeltaPixels) + 1;
            return As3FirstSightSample * (maxDelta / samples);
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

            // A vertical cone is the same half-plane turned 90 degrees, rim included: AS3's
            // ±45 frame around an aim of straight up accepts everything at or above the eye, and
            // the sweep over the author's aRot pair covers the two adjacent quadrants, i.e. the
            // whole half. See Cone for the tables.
            if (Cone != VisionCone.Horizontal)
            {
                float dy = targetCenterPx.y - eyePosPx.y;
                return Cone == VisionCone.Up ? dy >= 0f : dy <= 0f;
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
        /// The range at which <b>this</b> observer can see <paramref name="target"/> right now — AS3
        /// <c>Unit.look()</c>'s range product, <c>(visibility · stealthMult · loc.visMult + demask) ·
        /// vision</c> (<c>unit/Unit.as:4587</c>).
        ///
        /// <para><b>This is the keystone of the stealth system.</b> Before it, the port culled every
        /// candidate at a flat <see cref="VisionRangePixels"/> — a constant with no target-side term for
        /// crouching, cover or a cloak to act on, which is why none of them existed. The arithmetic
        /// lives in <see cref="StealthMath.EffectiveSightRangePixels"/> so it can be asserted without a
        /// scene; this method is the wiring that reads a live target's state.</para>
        ///
        /// <para><b>A null target, or one with the neutral state, yields
        /// <see cref="VisionRangePixels"/> exactly.</b> That is deliberate: it is what makes this change
        /// inert until something authors stealth, and it is the check that catches "the product silently
        /// changed every enemy's range".</para>
        /// </summary>
        public float EffectiveSightRangePixels(UnitController target)
        {
            if (target == null)
            {
                return VisionRangePixels;
            }

            StealthMath.StealthState stealth = target.Stealth;

            return StealthMath.EffectiveSightRangePixels(
                VisionRangePixels,
                stealth.Visibility,
                stealth.StealthMult,
                LocationVisiMult,
                stealth.Demask,
                VisionPower);
        }

        /// <summary>
        /// The range this unit would have against a target standing at
        /// <see cref="StealthMath.ReferenceVisibility"/> with no cloak and no reveal — i.e.
        /// <c>VisionRangePixels × loc.visMult × vision</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>This exists because <see cref="VisionRangePixels"/> is not the range anything is
        /// tested at, and reading it as one costs a play-test.</b> The F3 overlay drew
        /// <c>VisionRangePixels</c> and printed <c>vision=480</c> for every unit in the game, so a room
        /// whose <c>vis='10'</c> gives its occupants 4800 px looked exactly like a room whose
        /// occupants are blind at 480 — the <i>fix</i> looked like it had not landed, because the
        /// instrument never changed.</para>
        ///
        /// <para>It is the neutral-target case of <see cref="EffectiveSightRangePixels"/>, and it is
        /// deliberately <b>not</b> what a null target yields there (that returns the bare base, which
        /// is the "inert until something authors stealth" contract). Use this for <i>reporting</i>,
        /// <see cref="EffectiveSightRangePixels"/> for <i>deciding</i>: with a real candidate the
        /// per-target terms can shorten it, so a drawn circle must use the candidate's own number when
        /// there is one.</para>
        /// </remarks>
        public float NeutralSightRangePixels =>
            StealthMath.EffectiveSightRangePixels(
                VisionRangePixels,
                (int)StealthMath.ReferenceVisibility,
                1f,
                LocationVisiMult,
                0f,
                VisionPower);

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
            int tickIndex,
            Vector2 rayOriginPx = default)
        {
            if (blackboard == null)
            {
                return;
            }

            // Oracle AS3 gates raycast checks on `aiTCh % 10 == 1`
            bool shouldRaycast = (tickIndex % RaycastThrottleInterval == 1);

            UnitController seenCandidate = null;
            float seenIntensity = 0f;
            UnitController heardCandidate = null;
            float heardIntensity = 0f;
            Vector2 heardCenterPx = Vector2.zero;

            float closestDistSq = float.MaxValue;
            Vector2 bestTargetCenterPx = Vector2.zero;

            // The nearest refusal, kept so `LastRejection` can name the gate that actually stopped the
            // observer rather than the last candidate the loop happened to visit.
            RejectionReason nearestReason = RejectionReason.NoCandidates;
            float nearestDistanceSq = float.MaxValue;

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
                    //
                    // The range is the TARGET's, not a constant: AS3's `look()` computes
                    // `(visibility · stealthMult · loc.visMult + demask) · vision` per target
                    // (unit/Unit.as:4587). A still or cloaked target is therefore seen at a shorter
                    // range, and one that just fired at a longer range — `demask` is added outside the
                    // multiply, so a muzzle flash survives a cloak. A target with the neutral state
                    // yields exactly VisionRangePixels, which is why this changed no enemy's range until
                    // something authored stealth.
                    float effectiveRange = EffectiveSightRangePixels(candidate);
                    if (distSq > effectiveRange * effectiveRange)
                    {
                        NoteRefusal(RejectionReason.Range, distSq, ref nearestReason, ref nearestDistanceSq);
                        continue;
                    }

                    // AS3 gates this on `!param2` (`Unit.as:4598`); `UnitNPC.findCel` passes `true`.
                    if (!Omnidirectional && !CheckVisionAngle(eyePosPx, facing, targetCenterPx))
                    {
                        NoteRefusal(RejectionReason.Cone, distSq, ref nearestReason, ref nearestDistanceSq);
                        continue;
                    }

                    bool los = blackboard.HasLineOfSight;
                    if (shouldRaycast)
                    {
                        los = CheckLineOfSight(
                            eyePosPx, targetCenterPx, tileQuery, effectiveRange, rayOriginPx);
                    }

                    if (los && distSq < closestDistSq)
                    {
                        closestDistSq = distSq;
                        seenCandidate = candidate;
                        bestTargetCenterPx = targetCenterPx;

                        // The point-blank band is the TARGET's `detecting` (unit/Unit.as:4632), not the
                        // observer's CloseProximityPixels — which is why a lurking or cloaked target
                        // (detecting == 0) loses it entirely, while the observer's own bubble survives
                        // for the behind-gate at :4598. The two are the same number only by default.
                        seenIntensity = NoiseMath.VisionIntensity(
                            distSq, candidate.Detecting, effectiveRange);
                    }
                    else if (!los)
                    {
                        NoteRefusal(RejectionReason.LineOfSight, distSq, ref nearestReason, ref nearestDistanceSq);
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
                // The target's OWN state scales the intensity before it reaches the meter — AS3 calls
                // `observation(_loc4_, this.observ)` ON the target, so `sneak`, `demask`,
                // `pers.visiMult`, `stealthMult`, `lurked` and `isSit` are all the target's
                // (unit/UnitPlayer.as:3106-3136). This is the one place the listener half and the target
                // half meet, and it is why a lurker at the edge of a sight range contributes nothing
                // while the same lurker at point blank still contributes 15.
                //
                // `sneak` is the oracle's own dead field: `UnitPlayer.sneak` is read here and assigned
                // nowhere in the whole oracle, so it is 0 by construction and not merely by omission.
                StealthMath.StealthState stealth = seenCandidate.Stealth;
                seenCandidate.AddSuspicion(NoiseMath.ObservationInput(
                    seenIntensity,
                    ObservationPower,
                    sneak: 0f,
                    demask: stealth.Demask,
                    visiMult: stealth.VisiMult,
                    stealthMult: stealth.StealthMult,
                    lurked: stealth.Lurked,
                    isSit: stealth.IsSit,
                    sneakLurk: stealth.SneakLurk));
            }

            if (heardCandidate != null && heardIntensity > 0f)
            {
                heardCandidate.AddSuspicion(heardIntensity);
            }

            // ── Commit. ─────────────────────────────────────────────────────────────────────────
            //
            // Sight wins when both fire, matching the oracle's `if / else if` — and a sighting hands
            // over the exact position, because a sighting IS a position.
            //
            // `CommitsWithoutObservation` is the second half of `UnitNPC.findCel`'s divergence: the base
            // `Unit.findCel` waits for `obs >= maxObs` (`Unit.as:4675`), the NPC override does not wait
            // at all (`UnitNPC.as:451-455`). Without this an NPC's first sighting only accumulates, and
            // `TargetUnit` stays null until a meter that the NPC's own target may never fill.
            if (seenCandidate != null &&
                (CommitsWithoutObservation || seenCandidate.IsObserved))
            {
                LastRejection = RejectionReason.Committed;
                LastRejectionDistancePixels = Mathf.Sqrt(closestDistSq);

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
                // Nothing was visible this tick. `nearestReason` stays `NoCandidates` when the pool was
                // empty — the two are indistinguishable from the outside, and "no candidates" is the
                // one that means the unit's faction or liveness filter upstream refused everything.
                LastRejection = nearestReason;
                LastRejectionDistancePixels = nearestDistanceSq < float.MaxValue
                    ? Mathf.Sqrt(nearestDistanceSq)
                    : 0f;

                blackboard.TargetUnit = null;
                blackboard.HasLineOfSight = false;
                blackboard.TimeSinceTargetSpottedTicks++;
            }
            else
            {
                // Seen, but the meter has not filled yet — the unit is looking straight at the player and
                // has not yet decided. AS3 reaches this state every time a target appears at the far edge
                // of the sight range, and it is where `observ` earns its keep.
                //
                // With no waiver, this is also where an NPC that HAS found its target sits for the
                // several ticks the meter needs — which is why it gets its own reason rather than
                // being lumped in with "never saw anything".
                LastRejection = RejectionReason.ObservationMeter;
                LastRejectionDistancePixels = Mathf.Sqrt(closestDistSq);

                blackboard.HasLineOfSight = true;
                blackboard.TimeSinceTargetSpottedTicks = 0;
            }
        }

        /// <summary>
        /// Record the nearest refusal, so <see cref="LastRejection"/> describes the gate that mattered
        /// rather than the last candidate the loop happened to reach.
        /// </summary>
        static void NoteRefusal(
            RejectionReason reason, float distSq,
            ref RejectionReason nearestReason, ref float nearestDistanceSq)
        {
            if (distSq >= nearestDistanceSq)
            {
                return;
            }

            nearestReason = reason;
            nearestDistanceSq = distSq;
        }

        /// <summary>
        /// Raises alert level upon taking damage or hearing an alarm call (AS3 <c>alarma()</c>).
        ///
        /// <para>Note this is <c>alarma</c>, not <c>budilo</c>: it alerts <i>this</i> unit at a position.
        /// The propagation half — telling the neighbours — is <c>EnemyBrain.Budilo</c>, which is gated on
        /// each receiver's own <c>ear</c> and on whether it is a robot.</para>
        /// </summary>
        public void RaiseAlarm(
            EnemyBlackboard blackboard,
            Vector2 alarmSourcePx,
            int durationTicks,
            bool caughtUnawares)
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
            //
            // `caughtUnawares` is the gate the oracle puts INSIDE `alarma()`, and omitting it was a
            // real bug rather than a simplification. `UnitAIRobot.alarma()` is
            // `if(sost == 1 && aiState <= 1) { … shok = …; }` (`UnitAIRobot.as:134-141`), so a robot
            // that is already searching or chasing is not startled again. With the gate missing, any
            // neighbour's shout re-armed `shok` on a robot that was already in the chase, and the fire
            // gate `aiAttackOch == 0 && shok <= 0` (`UnitAIRobot.as:585`) then held it silent
            // indefinitely. The caller decides, because the oracle's condition is per-archetype —
            // `UnitZombie` uses `aiState <= 1 || aiState == 5` (`:334`), `UnitAIRobot` uses
            // `aiState <= 1`.
            if (caughtUnawares)
            {
                blackboard.ShockTimerTicks =
                    StaggerMath.AlarmTicks(Random.Range(0, StaggerMath.AlarmRollRange));
            }
        }
    }
}
