using System.Collections.Generic;
using UnityEngine;
using PFE.Systems.Physics;
using PFE.Systems.Telekinesis;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Lightweight runtime layer for dynamic map props.
    /// Keeps simple room-local motion and collision off the main object list so
    /// inert props do not need per-object physics behaviors.
    /// </summary>
    public sealed class RoomObjectPhysicsLayer
    {
        /// <summary>
        /// The legacy per-frame step. Deliberately <b>not</b> the canonical sim step: the per-frame
        /// driver calls <see cref="Update"/> once per rendered frame and has always used 1/60, so this
        /// value is kept for that path. The sim-driven path passes <c>SimClock.SimDt</c> (1/30)
        /// explicitly — see <c>PfeDebugSettings.SimTickRoom</c>. Public so the room heartbeat and this
        /// default cannot drift apart.
        /// </summary>
        public const float LegacyPerFrameDeltaTime = 1f / 60f;

        // ── Physics tuning surface ───────────────────────────────────────────────────────────────
        // Public so tests can pin them. This is deliberate: the projectile census found that the
        // reason four divergent gravity/acceleration literals coexisted for years was that none of
        // them was assertable. Every value below carries its AS3 citation for the same reason.

        /// <summary>
        /// Fall acceleration. AS3 applies <c>dy += World.ddy</c> once per 30 Hz frame
        /// (<c>Box.as:895</c>) and <c>World.ddy</c> is <c>1</c> px/frame² (<c>World.as:46</c>), so the
        /// correct value is <c>1 × 30² = 900</c> px/s². The old 1800 was 2× too strong — the same
        /// doubling as the divergent <c>ddy</c> literals reconciled in the projectile census.
        /// </summary>
        public const float GravityPixelsPerSecond = 900f;

        /// <summary>
        /// Terminal fall speed. AS3 gates the integrator with <c>if(!isPlav &amp;&amp; dy &lt;
        /// World.maxdy)</c> (<c>Box.as:893</c>) where <c>World.maxdy = 20</c> px/frame
        /// (<c>World.as:48</c>) → <c>20 × 30 = 600</c> px/s. The port had no clamp at all, so a long
        /// fall could exceed AS3's terminal velocity.
        /// </summary>
        public const float MaxFallSpeedPixelsPerSecond = 600f;

        /// <summary>
        /// Ground friction. AS3 damps horizontal speed on landing: <c>dx *= 0.92</c>, and snaps to a
        /// full stop when <c>|dx| &lt; 5</c> px/frame (<c>Box.as:1090-1096</c>). Applied here as a
        /// <i>rate</i> — <c>Pow(0.92, deltaTime × 30)</c> — so one 1/30 sim tick is exactly one AS3
        /// frame and the legacy 1/60 per-frame path decays at the same speed per unit time.
        /// </summary>
        public const float GroundFriction = 0.92f;
        public const float GroundStopPixelsPerFrame = 5f;

        /// <summary>Stop threshold in the port's px/s units (<c>5 × 30 = 150</c>).</summary>
        public const float GroundStopPixelsPerSecond =
            GroundStopPixelsPerFrame * PFE.Core.SimClock.CanonicalTicksPerSecond;

        // ── TelekinesisFollowSpeed: REMOVED ──────────────────────────────────────────────────────
        //
        // <b>Removed.</b> This used to be <c>const float TelekinesisFollowSpeed = 14f</c> — an invented
        // proportional follow rate (<c>v = toTarget × 14</c>) with no AS3 counterpart. It is gone rather
        // than retuned, because AS3's controller is a different <i>shape</i>, not a different number:
        //
        //   1. the player's hold tick nudges the held object's own dx/dy toward the cursor PER AXIS —
        //      a ±15 px deadzone, `+= teleAccel` until teleSpeed is reached (UnitPlayer.as:1247-1262;
        //      teleSpeed = 8, teleAccel = 1 at :57/:59). That IS position tracking, as accel to a cap.
        //   2. Box.forces() then damps it — dx *= 0.8; dy *= 0.8 while levit is set (Box.as:906-910).
        //
        // There is no proportional term anywhere in it. StepHeldObject now runs that model; the
        // arithmetic lives in PFE.Systems.Telekinesis.TelekinesisMath.
        // ────────────────────────────────────────────────────────────────────────────────────────

        const float MinimumImpactSpeed = 220f;
        const float ThrowGraceDuration = 0.18f;

        /// <summary>
        /// Maximum movement per collision check. AS3 substeps with <c>World.maxdelta = 9</c> px
        /// (<c>World.as:52</c>, used at <c>Box.as:610-620</c>); the port used 8. The substep count is
        /// AS3's <c>floor(max / maxdelta) + 1</c>, not the port's old <c>ceil(max / 8)</c>. The two
        /// agree at small distances but diverge in bands — at 17 px the old form took 3 substeps where
        /// AS3 takes 2, so the old code subdivided more than AS3 did in exactly the range where the
        /// limit was being exceeded. Verified numerically before changing it (17 → 2, not 3).
        /// </summary>
        public const float SubstepDistancePixels = 9f;

        /// <summary>
        /// AS3's substep count: <c>floor(maxDistance / maxdelta) + 1</c> (<c>Box.as:616</c>). Pure, so
        /// it can be asserted directly without a room. AS3 wraps it in a two-branch guard — when
        /// <c>|dx| &lt; maxdelta &amp;&amp; |dy| &lt; maxdelta</c> it takes a single un-subdivided step
        /// (<c>Box.as:610-613</c>), otherwise it uses this count (<c>Box.as:614-623</c>). The formula
        /// already returns 1 for exactly that first range, so the two branches collapse into one here
        /// and the port needs no guard of its own.
        /// </summary>
        public static int SubstepCount(float maxDistance)
        {
            return Mathf.FloorToInt(Mathf.Max(0f, maxDistance) / SubstepDistancePixels) + 1;
        }

        /// <summary>
        /// Ground friction for one step: below the stop threshold the box is halted outright,
        /// otherwise damped by <see cref="GroundFriction"/> per AS3 frame. Pure, so the rate and the
        /// threshold can be asserted without a room. AS3 <c>Box.as:1090-1096</c>.
        /// </summary>
        public static float ApplyGroundFriction(float velocityX, float deltaTime)
        {
            if (Mathf.Abs(velocityX) < GroundStopPixelsPerSecond)
            {
                return 0f;
            }

            return velocityX * Mathf.Pow(
                GroundFriction, deltaTime * PFE.Core.SimClock.CanonicalTicksPerSecond);
        }

        /// <summary>
        /// Fall speed for one step: gravity accelerates until the AS3 terminal velocity, then stops
        /// accelerating. Pure, and the clamp is inside the gate exactly as AS3 writes it
        /// (<c>Box.as:893</c> — <c>if(!isPlav &amp;&amp; dy &lt; World.maxdy)</c>).
        /// </summary>
        public static float IntegrateFallSpeed(float velocityY, float deltaTime)
        {
            if (velocityY <= -MaxFallSpeedPixelsPerSecond)
            {
                return velocityY;
            }

            return Mathf.Max(
                -MaxFallSpeedPixelsPerSecond, velocityY - GravityPixelsPerSecond * deltaTime);
        }

        readonly List<ObjectInstance> _dynamicObjects = new List<ObjectInstance>();
        readonly HashSet<ObjectInstance> _dynamicObjectLookup = new HashSet<ObjectInstance>();
        int _lastKnownRoomObjectCount = -1;

        // ── Per-tick query candidate lists ───────────────────────────────────────────────────────
        //
        // <b>What these fix.</b> Both unit-side prop queries used to walk `_dynamicObjects` from
        // scratch, once per unit per step, and re-decide per candidate everything that does not depend
        // on the unit asking. With 24 units and ~90 props that is 24 x 90 candidate tests per step,
        // 50 steps a second -- and the measured camp capture showed `phys.prop.impactQuery` alone
        // costing 21.4 s of self time over a 21.8 s window, 0.457 ms per call, because the sweep was
        // asked on every motor-less unit every step whether or not anything was falling.
        //
        // The unit-independent half of each filter is now decided ONCE per prop-layer tick, into a
        // short list, so a query walks only props that could possibly qualify. This is the shape the
        // oracle has: `Box.as:630` gates `attDrop()` once, on the box, before any unit is examined --
        // the port had that same gate, but inside the per-candidate loop, so it paid for the whole scan
        // in order to discover that nothing was moving.
        //
        // <b>What is deliberately NOT hoisted.</b> Anything that can change between the rebuild and a
        // later query within the same tick stays in the per-call loop: `isActive`, `IsDestroyed()`,
        // `isHeldByTelekinesis` and the AABB overlap are all re-tested per call. Hoisting those would
        // let a prop grabbed mid-tick still damage what it is waved through, which is exactly the
        // `!levit` half of `Box.as:630`. The lists are a filter, not a snapshot of the answer.
        //
        // <b>Why rebuilt in Update() rather than lazily.</b> Rebuilding at the end of the prop layer's
        // own tick keeps the lists in step with the props by construction: if `Update` is not running,
        // the props are not moving either, so a list that is one tick old is still the right list. A
        // lazily-stamped rebuild keyed on `_tickCount` would instead go stale *forever* on a room whose
        // prop layer never ticks -- the failure would be "crates stop hurting units", with nothing red.
        readonly List<ObjectInstance> _shelfCandidates = new List<ObjectInstance>();
        readonly List<ImpactCandidate> _impactCandidates = new List<ImpactCandidate>();

        // Resolved per-prop pixel size for this tick. `GetApproximatePixelSize` is the string-parsing
        // half of `GetApproximateBounds` (scx/scy -> GetAttribute -> float.TryParse with a culture), and
        // both queries were paying it once per candidate per call. The size cannot change while a prop
        // falls, so it is resolved once per prop per tick and the rect is then built from the prop's
        // live position -- see ObjectInstance.GetApproximateBoundsWithSize.
        readonly Dictionary<ObjectInstance, Vector2> _pixelSizeByProp =
            new Dictionary<ObjectInstance, Vector2>();

        /// <summary>
        /// Set whenever the candidate lists could be out of date: the tracked set changed, the props
        /// moved, or the layer was cleared. Starts true so an untouched layer is still correct.
        ///
        /// <para><b>Why a dirty flag and not a rebuild inside <see cref="Update(RoomInstance, float)"/>.</b>
        /// These queries are public API and the fixtures drive them directly — construct a layer,
        /// <c>Register</c> a prop, set its velocity, ask — without ever calling <c>Update</c>. A list
        /// rebuilt only in <c>Update</c> would be empty there, so every one of those tests would fail
        /// while the game looked fine. Building on demand removes that whole class of divergence, and
        /// it also removes the stale-forever case: a room whose prop layer stops ticking still answers
        /// from a list that any mutation marks dirty.</para>
        /// </summary>
        bool _queryCandidatesDirty = true;

        /// <summary>Invalidate the per-tick candidate lists; they are rebuilt on the next query.</summary>
        void MarkQueryCandidatesDirty()
        {
            _queryCandidatesDirty = true;
        }

        /// <summary>Rebuild the candidate lists if anything has changed since they were last built.</summary>
        void EnsureQueryCandidates()
        {
            if (_queryCandidatesDirty)
            {
                RebuildQueryCandidates();
            }
        }

        /// <summary>
        /// One prop that survived the <b>static</b> half of the filters this tick, plus the state object
        /// the per-call loop needs to re-test the parts that can change.
        ///
        /// <para><b>Why the velocity gates are NOT baked in here.</b> Baking them in would freeze a
        /// decision that a telekinesis throw can invalidate between the rebuild and a query — a crate
        /// thrown mid-tick would wait a full prop tick to become eligible. So this list is only a
        /// <i>superset</i>: it holds the props that moved this tick, and the exact oracle gates
        /// (<c>Box.as:632</c> / <c>:918</c>) are re-evaluated per call from live velocity, exactly as
        /// they were before this change. A prop at rest cannot pass either gate, so nothing that the
        /// old loop accepted is missing here.</para>
        /// </summary>
        struct ImpactCandidate
        {
            public ObjectInstance Obj;
            public MapObjectDynamicStateData State;

            public ImpactCandidate(ObjectInstance obj, MapObjectDynamicStateData state)
            {
                Obj = obj;
                State = state;
            }
        }

        /// <summary>
        /// The resolved pixel size for <paramref name="obj"/> this tick, computed at most once.
        /// </summary>
        Vector2 GetCachedPixelSize(ObjectInstance obj)
        {
            if (_pixelSizeByProp.TryGetValue(obj, out Vector2 size))
            {
                return size;
            }

            size = obj.GetApproximatePixelSize();
            _pixelSizeByProp[obj] = size;
            return size;
        }

        // ── Heartbeat instrumentation ────────────────────────────────────────────────────────────
        // How many times the room has actually stepped this layer, and with what step.
        //
        // This exists because "the grab succeeded but the prop never moved" has exactly two shapes --
        // the hold was refused, or nothing is ticking the room -- and from the outside they are
        // identical: silence. A probe that samples TickCount twice, a second apart, tells them apart
        // with one number.
        //
        // Counting here rather than in RoomInstance is deliberate: RoomInstance.Update already
        // early-returns when the room is inactive, so a frozen counter means "the room never reached
        // the physics layer", which is the question actually being asked.
        int _tickCount;
        float _lastTickDeltaTime;
        int _lastTickDynamicObjectCount;

        /// <summary>
        /// Times <see cref="Update(RoomInstance, float)"/> has actually run. Frozen across a second of
        /// real time means the room is not ticking this layer — see <c>tele probe</c>'s <c>[1b]</c>.
        /// </summary>
        public int TickCount => _tickCount;

        /// <summary>
        /// How many props this layer is tracking. This is the list size every prop query used to walk
        /// per call, so it is the number that decides whether the query cost is a shape problem or a
        /// constant-factor one — read it beside <see cref="ShelfCandidateCount"/> and
        /// <see cref="ImpactCandidateCount"/> to see how much of it the per-tick pre-filter removes.
        /// </summary>
        public int TrackedDynamicObjectCount => _dynamicObjects.Count;

        /// <summary>Props that could be a surface this tick (the unit-independent half of Unit.checkShelf).</summary>
        public int ShelfCandidateCount => _shelfCandidates.Count;

        /// <summary>Props that moved this tick — the list the impact sweep now walks.</summary>
        public int ImpactCandidateCount => _impactCandidates.Count;

        /// <summary>The step passed to the most recent <see cref="Update(RoomInstance, float)"/>.</summary>
        public float LastTickDeltaTime => _lastTickDeltaTime;

        /// <summary>Dynamic-object count after the most recent tick, so a mid-run rebuild is visible.</summary>
        public int LastTickDynamicObjectCount => _lastTickDynamicObjectCount;

        public IReadOnlyList<ObjectInstance> DynamicObjects => _dynamicObjects;
        public int DynamicObjectCount => _dynamicObjects.Count;

        public void EnsureSynchronized(List<ObjectInstance> roomObjects)
        {
            SyncWithRoom(roomObjects);
        }

        public void Rebuild(IReadOnlyList<ObjectInstance> objects)
        {
            _dynamicObjects.Clear();
            _dynamicObjectLookup.Clear();
            MarkQueryCandidatesDirty();

            if (objects == null)
            {
                _lastKnownRoomObjectCount = 0;
                return;
            }

            for (int i = 0; i < objects.Count; i++)
            {
                Register(objects[i]);
            }

            _lastKnownRoomObjectCount = objects.Count;
        }

        public bool Register(ObjectInstance obj)
        {
            if (obj == null)
            {
                return false;
            }

            obj.InitializeDynamicRuntimeState();
            if (!obj.ShouldTrackInPhysicsLayer())
            {
                return false;
            }

            if (_dynamicObjectLookup.Add(obj))
            {
                _dynamicObjects.Add(obj);
                MarkQueryCandidatesDirty();
                return true;
            }

            return false;
        }

        public bool Unregister(ObjectInstance obj)
        {
            if (obj == null || !_dynamicObjectLookup.Remove(obj))
            {
                return false;
            }

            _dynamicObjects.Remove(obj);
            MarkQueryCandidatesDirty();
            return true;
        }

        public bool TryApplyImpulse(ObjectInstance obj, Vector2 deltaVelocity, bool treatAsThrow = false)
        {
            if (obj == null)
            {
                return false;
            }

            Register(obj);
            if (!_dynamicObjectLookup.Contains(obj))
            {
                return false;
            }

            MapObjectDynamicStateData state = obj.runtimeState.dynamicState;
            state.isHeldByTelekinesis = false;
            state.hasTelekineticTarget = false;
            state.velocity += deltaVelocity;
            state.isGrounded = false;
            state.isThrown = treatAsThrow && obj.CanBeThrown();
            state.throwGraceTime = state.isThrown ? ThrowGraceDuration : 0f;
            return true;
        }

        public bool TryFindNearestTelekineticObject(Vector2 origin, float maxDistancePixels, out ObjectInstance obj)
        {
            obj = null;
            float clampedMaxDistance = Mathf.Max(0f, maxDistancePixels);
            float bestDistanceSquared = clampedMaxDistance * clampedMaxDistance;

            for (int i = 0; i < _dynamicObjects.Count; i++)
            {
                ObjectInstance candidate = _dynamicObjects[i];
                if (candidate == null ||
                    !candidate.SupportsTelekinesis() ||
                    candidate.IsDestroyed() ||
                    !candidate.isActive)
                {
                    continue;
                }

                Vector2 candidatePoint = candidate.GetApproximateBounds().center;
                float distanceSquared = (candidatePoint - origin).sqrMagnitude;
                if (distanceSquared > bestDistanceSquared)
                {
                    continue;
                }

                bestDistanceSquared = distanceSquared;
                obj = candidate;
            }

            return obj != null;
        }

        /// <summary>
        /// Finds the best telekinesis grab candidate near the cursor, matching AS3's
        /// <c>UnitPlayer.as:1769-1783</c> candidate search.
        ///
        /// <para>Guards on <see cref="ObjectInstance.IsLiftable"/> (authored capability AND runtime
        /// <c>levitPoss</c>) and the AS3 mass limit (<see cref="ObjectInstance.GetAs3Massa"/> &lt;=
        /// <paramref name="maxMassa"/>).</para>
        ///
        /// <para><b>No <c>stay</c> test, matching the oracle.</b> <c>UnitPlayer.actTele</c> filters on
        /// <c>levitPoss</c> and <c>massa</c> alone (<c>UnitPlayer.as:1769-1783</c>); <c>stay</c> appears
        /// only in the HUD hint at <c>GUI.as:1326</c>, and AS3 <i>clears</i> it when it grabs
        /// (<c>:1831</c>). An earlier version of this method required <c>IsAtRest()</c>, which merged
        /// the hint into the action and refused grabs AS3 allows — most visibly a prop caught
        /// mid-flight. Removed 2026-10-02 after the owner confirmed AS3 can grab a falling object.</para>
        ///
        /// <para>The already-held test is the port's own, and it takes over what <c>stay</c> was
        /// accidentally doing: a prop that is already held must not become a candidate again.</para>
        /// </summary>
        public bool TryFindTelekineticCandidate(Vector2 cursorPixels, float maxCursorDistancePixels, float maxMassa, out ObjectInstance obj)
        {
            obj = null;
            float clampedMaxDistance = Mathf.Max(0f, maxCursorDistancePixels);
            float bestDistanceSquared = clampedMaxDistance * clampedMaxDistance;

            // The grab candidate scan. Heaviest per candidate in this file — IsLiftable() and
            // GetAs3Massa() each re-parse attributes — but it is a PRESS-edge cost, not a per-tick
            // one, so a large number here means something is calling it per frame, not that the
            // filter is slow.
            using (PFE.Core.Profiling.PfeProfiler.Region("tele.candidate",
                "telekinesis: grab candidate scan. Press-edge only — a per-frame count here is the bug."))
            {
            for (int i = 0; i < _dynamicObjects.Count; i++)
            {
                ObjectInstance candidate = _dynamicObjects[i];
                if (candidate == null ||
                    candidate.IsDestroyed() ||
                    !candidate.isActive ||
                    !candidate.IsLiftable() ||
                    candidate.IsHeldByTelekinesis() ||
                    candidate.GetAs3Massa() > maxMassa)
                {
                    continue;
                }

                Vector2 candidatePoint = candidate.GetApproximateBounds().center;
                float distanceSquared = (candidatePoint - cursorPixels).sqrMagnitude;
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    obj = candidate;
                }
            }

            return obj != null;
            }
        }

        public bool TrySetTelekineticHold(ObjectInstance obj, Vector2 targetPosition)
        {
            if (obj == null || !obj.SupportsTelekinesis())
            {
                return false;
            }

            Register(obj);
            if (!_dynamicObjectLookup.Contains(obj))
            {
                return false;
            }

            MapObjectDynamicStateData state = obj.runtimeState.dynamicState;
            state.isHeldByTelekinesis = true;
            state.hasTelekineticTarget = true;
            state.telekineticTarget = targetPosition;
            state.isThrown = false;
            state.throwGraceTime = 0f;
            state.velocity = Vector2.zero;
            state.isGrounded = false;

            // AS3 clears `stay` at the end of actTele (UnitPlayer.as:1831) -- it is a CONSEQUENCE of
            // being picked up, not a precondition for it. Kept because it is real oracle state (a
            // future HUD hint reads it, GUI.as:1326) and because the object is no longer at rest.
            state.stay = false;
            return true;
        }

        public bool TryReleaseTelekineticHold(ObjectInstance obj, Vector2 releaseVelocity, bool treatAsThrow = true)
        {
            if (obj == null || obj.runtimeState == null || obj.runtimeState.dynamicState == null)
            {
                return false;
            }

            Register(obj);
            if (!_dynamicObjectLookup.Contains(obj))
            {
                return false;
            }

            MapObjectDynamicStateData state = obj.runtimeState.dynamicState;
            state.isHeldByTelekinesis = false;
            state.hasTelekineticTarget = false;
            state.velocity = releaseVelocity;
            state.isThrown = treatAsThrow && obj.CanBeThrown();
            state.throwGraceTime = state.isThrown ? ThrowGraceDuration : 0f;
            state.isGrounded = false;
            return true;
        }

        public void Update(RoomInstance room, float deltaTime = LegacyPerFrameDeltaTime)
        {
            if (room == null)
            {
                // Clear rather than leave the last tick's candidates behind. Without this a room that
                // goes away would keep answering queries from a list of props that are no longer
                // tracked — "crates still hurt units in a room you have left", which is the stale-
                // candidate failure the field block above warns about.
                ClearQueryCandidates();
                return;
            }

            _tickCount++;
            _lastTickDeltaTime = deltaTime;

            // `phys.objects.sync` is O(tracked x room.objects) — SyncWithRoom tests
            // `roomObjects.Contains(tracked)` per tracked prop, and `room.objects` is the room's
            // whole list. It is cheap per candidate but it is the one part of this layer whose cost
            // grows with the SQUARE-ish product of the two lists, so it is measured separately from
            // the stepping loop below.
            using (PFE.Core.Profiling.PfeProfiler.Region("phys.objects.sync",
                "physics: reconcile the tracked dynamic-prop list against room.objects (Contains per tracked prop)."))
            {
                SyncWithRoom(room.objects);
            }

            _lastTickDynamicObjectCount = _dynamicObjects.Count;

            if (_dynamicObjects.Count == 0)
            {
                ClearQueryCandidates();
                return;
            }

            float dt = Mathf.Max(0.0001f, deltaTime);
            using (PFE.Core.Profiling.PfeProfiler.Region("phys.objects.step",
                "physics: integrate every dynamic prop this tick. calls == ticks; total/calls == ms per tick for ALL props."))
            {
                for (int i = 0; i < _dynamicObjects.Count; i++)
                {
                    ObjectInstance obj = _dynamicObjects[i];
                    if (obj == null)
                    {
                        continue;
                    }

                    StepDynamicObject(room, obj, dt);
                }
            }

            // The props just moved, so the candidate lists are stale. Marked rather than rebuilt here:
            // these queries are public API and the fixtures drive them without ticking the layer, so the
            // build has to happen on demand. See _queryCandidatesDirty.
            MarkQueryCandidatesDirty();
        }

        void ClearQueryCandidates()
        {
            _shelfCandidates.Clear();
            _impactCandidates.Clear();
            _pixelSizeByProp.Clear();

            // Clearing without marking dirty would let the next query read an empty list and answer
            // "nothing here" — the silent-empty-report failure. Marking it forces a rebuild from
            // whatever `_dynamicObjects` holds at that moment.
            MarkQueryCandidatesDirty();
        }

        /// <summary>
        /// Recompute the two per-tick candidate lists: the props that could be a surface
        /// (<c>Unit.checkShelf</c>) and the props that could deal an impact (<c>Box.attDrop</c>).
        ///
        /// <para>Everything decided here is <b>unit-independent and tick-stable</b> — the prop's
        /// own flags, whether it is a shelf (a definition/attribute question, so it cannot change while
        /// a prop falls), and the two velocity gates. The per-call loops re-test the flags that a
        /// telekinesis grab or a destruction can flip mid-tick; see the field block.</para>
        /// </summary>
        void RebuildQueryCandidates()
        {
            using (PFE.Core.Profiling.PfeProfiler.Region("phys.prop.candidates",
                "physics: rebuild the per-tick shelf + impact candidate lists (the unit-independent half of both filters)."))
            {
                ClearQueryCandidates();

                for (int i = 0; i < _dynamicObjects.Count; i++)
                {
                    ObjectInstance candidate = _dynamicObjects[i];
                    if (candidate == null || !candidate.isActive || candidate.IsDestroyed())
                    {
                        continue;
                    }

                    MapObjectDynamicStateData candidateState = candidate.runtimeState?.dynamicState;
                    if (candidateState == null || candidateState.isHeldByTelekinesis)
                    {
                        continue;
                    }

                    // Unit.as:2719 — `_loc4_.shelf && !_loc4_.levit`. NOTE: no `stay`, unlike
                    // Box.checkShelf; a unit may land on a crate that is still settling and push it down.
                    if (candidate.IsShelf())
                    {
                        _shelfCandidates.Add(candidate);
                    }

                    // The ONE pre-filter the impact sweep gets, and it is deliberately weaker than the
                    // oracle's gates: keep only props that are actually moving.
                    //
                    // `IsMovingFastEnoughToSweep` reads `state.velocity`, so a prop at rest cannot pass
                    // `Box.as:630` (`|d| > 5`) or either gate below it — which means nothing the old
                    // per-unit loop accepted is excluded by this. It also means a prop carried by a
                    // moving support (`FollowSupport` moves it by displacement, leaving velocity zero)
                    // is correctly absent: the old loop rejected it too.
                    //
                    // In a room where nothing is falling — most rooms, most of the time — this collapses
                    // the per-unit scan to an empty list, which is the whole point.
                    Vector2 resting = candidateState.velocity;
                    if (resting.x == 0f && resting.y == 0f)
                    {
                        continue;
                    }

                    _impactCandidates.Add(new ImpactCandidate(candidate, candidateState));
                }

                _queryCandidatesDirty = false;
            }
        }

        void SyncWithRoom(List<ObjectInstance> roomObjects)
        {
            if (roomObjects == null)
            {
                _dynamicObjects.Clear();
                _dynamicObjectLookup.Clear();
                _lastKnownRoomObjectCount = 0;
                MarkQueryCandidatesDirty();
                return;
            }

            if (_lastKnownRoomObjectCount != roomObjects.Count)
            {
                Rebuild(roomObjects);
                return;
            }

            bool removedAny = false;
            for (int i = _dynamicObjects.Count - 1; i >= 0; i--)
            {
                ObjectInstance tracked = _dynamicObjects[i];
                if (tracked == null ||
                    !roomObjects.Contains(tracked) ||
                    !tracked.ShouldTrackInPhysicsLayer())
                {
                    _dynamicObjectLookup.Remove(tracked);
                    _dynamicObjects.RemoveAt(i);
                    removedAny = true;
                }
            }

            if (removedAny)
            {
                MarkQueryCandidatesDirty();
            }
        }

        void StepDynamicObject(RoomInstance room, ObjectInstance obj, float deltaTime)
        {
            obj.InitializeDynamicRuntimeState();

            MapObjectDynamicStateData state = obj.runtimeState.dynamicState;
            if (!state.isDynamic || !obj.ShouldSimulateDynamicPhysics())
            {
                return;
            }

            if (state.throwGraceTime > 0f)
            {
                state.throwGraceTime = Mathf.Max(0f, state.throwGraceTime - deltaTime);
            }

            if (state.isHeldByTelekinesis && obj.SupportsTelekinesis())
            {
                StepHeldObject(room, obj, state, deltaTime);
                return;
            }

            // AS3 `Box.as:571-595`, which runs at the top of the box's own step, BEFORE the movement
            // below — so a rider is carried by the support's displacement from the previous tick, and
            // is then free to fall or be blocked in its own right.
            FollowSupport(room, obj, state);

            Vector2 velocity = state.velocity;

            velocity.y = IntegrateFallSpeed(velocity.y, deltaTime);

            // There is no air damping in AS3 at all — the old AirDrag = 2 was an invented force and is
            // gone. Ground friction applies only while in contact (Box.as:1090-1096).
            if (state.isGrounded)
            {
                velocity.x = ApplyGroundFriction(velocity.x, deltaTime);
            }

            state.velocity = velocity;

            // AS3 records the tick's displacement after the move (`Box.as:643-644`,
            // `cdx = X - stX`), and that value is what a rider reads on ITS next step. Captured around
            // the move rather than derived from velocity, because what a rider must follow is where the
            // support actually ENDED UP: a support that was refused a move has a non-zero velocity and
            // zero displacement, and carrying a rider by the velocity would slide it off a crate that
            // never moved.
            Vector2 positionBeforeMove = obj.position;
            MoveWithCollision(room, obj, state, velocity * deltaTime);
            state.cdx = obj.position.x - positionBeforeMove.x;
            state.cdy = obj.position.y - positionBeforeMove.y;

            // AS3 Obj.stay -- "at rest". Box.as sets it true on landing (:1104) and clears it while
            // moving (:965/:1037/:1119); UnitPlayer.as:1831 clears it again on grab. The threshold is
            // the layer's own ground-stop speed, already used for friction, so "at rest" means the
            // same thing in both places.
            //
            // NOTE: AS3's grab action does NOT test this -- only the HUD hint (GUI.as:1326) does. See
            // MapObjectDynamicStateData.stay for the citations. It is maintained here because it is
            // real AS3 state that a future HUD hint reads, and because prop stacking reads it:
            // Box.checkShelf requires a support to be `stay` (Box.as:1198).
            state.stay = state.isGrounded &&
                         state.velocity.sqrMagnitude <= GroundStopPixelsPerSecond * GroundStopPixelsPerSecond;
        }

        void StepHeldObject(RoomInstance room, ObjectInstance obj, MapObjectDynamicStateData state, float deltaTime)
        {
            // Only ever runs for a prop actually being held, so its cost is "the telekinesis hold",
            // per tick. If the camp is slow while HOLDING but fine while not, this is the region.
            using (PFE.Core.Profiling.PfeProfiler.Region("phys.objects.held",
                "telekinesis: one held prop's tick (accelerate to cursor + damp + collide)."))
            {
            Vector2 targetPosition = state.hasTelekineticTarget ? state.telekineticTarget : obj.position;

            // AS3's controller, in the port's px/s units. The oracle's teleSpeed/teleAccel are px per
            // FRAME at 30 fps, so both need converting before they can meet a velocity that is
            // integrated as position += velocity * deltaTime. TelekinesisMath owns the conversion.
            float speedCap = TelekinesisMath.PerFrameVelocityToPerSecond(TelekinesisMath.PlayerTeleSpeed);
            float accelStep = TelekinesisMath.PerFrameAccelToPerSecondSquared(TelekinesisMath.PlayerTeleAccel) * deltaTime;

            // Compare the object's AABB CENTRE to the cursor on BOTH axes, matching AS3.
            //
            // AS3's hold tick (`UnitPlayer.as:1247-1262`) writes `teleObj.dx`/`teleObj.dy` from four
            // per-axis tests, and the vertical pair reads
            //   `if(teleObj.Y - teleObj.scY / 2 < celY - 15) teleObj.dy += teleAccel;`
            // — i.e. it compares the object's vertical MIDDLE to the cursor. Its `X` is already the
            // horizontal centre (`X1 = X - scX/2`, `X2 = X + scX/2`).
            //
            // `ObjectInstance.position.y` is the object's BOTTOM, not its centre:
            // `GetApproximateBounds` builds `Rect(x - w/2, y, w, h)` and `Rect.y` is the bottom edge
            // in a y-up world. Feeding `position.y` in raw therefore biases the vertical target by
            // half the object's height — 40 px for `mcrate1` (position 560, centre 600), which is
            // nearly three times the 15 px deadzone it is being compared against. The prop would
            // settle visibly low and refuse to rise the last stretch to the cursor.
            //
            // This is the same bottom/centre confusion the candidate filter already had fixed for it
            // (`ReferencePoint` in `PlayerTelekinesisController`). The filter and the step must agree
            // on the reference point or the object settles somewhere neither of them asked for.
            Vector2 objCenter = obj.GetApproximateBounds().center;

            Vector2 velocity = state.velocity;
            velocity.x = TelekinesisMath.HoldAccelStep(objCenter.x, targetPosition.x, velocity.x,
                                                       speedCap, accelStep, TelekinesisMath.Deadzone);
            velocity.y = TelekinesisMath.HoldAccelStep(objCenter.y, targetPosition.y, velocity.y,
                                                       speedCap, accelStep, TelekinesisMath.Deadzone);

            // Box.forces() runs after the player's hold tick in AS3, so the damping is applied to the
            // just-nudged velocity. Exponent form because 0.8 is per FRAME, and this may be called at
            // any rate -- the naive product would damp twice as often at 60 Hz.
            velocity *= TelekinesisMath.DampingOverInterval(TelekinesisMath.LevitDamping, deltaTime);

            state.velocity = velocity;
            state.isGrounded = false;
            state.isThrown = false;

            // A held object is not at rest. This is bookkeeping, not a gate: "already held" is asked
            // directly via IsHeldByTelekinesis, because the grab gate deliberately ignores `stay`.
            state.stay = false;

            // AS3 `Box.as:571-575` -- while levitating, the box clears `stay`, `fixPlav` and its
            // support link. A held prop is lifted OFF whatever it was resting on, so it must stop
            // following that prop's displacement; otherwise a crate you pick up out of a stack would
            // keep being carried by the crate it just left. It also cannot be a support itself while
            // held, because Box.checkShelf requires `stay` (Box.as:1198).
            state.osnova = null;

            MoveWithCollision(room, obj, state, velocity * deltaTime);
            }
        }

        void MoveWithCollision(RoomInstance room, ObjectInstance obj, MapObjectDynamicStateData state, Vector2 totalDelta)
        {
            float maxDistance = Mathf.Max(Mathf.Abs(totalDelta.x), Mathf.Abs(totalDelta.y));
            int steps = SubstepCount(maxDistance);
            Vector2 stepDelta = totalDelta / steps;

            for (int i = 0; i < steps; i++)
            {
                if (!Mathf.Approximately(stepDelta.x, 0f))
                {
                    TryMoveAxis(room, obj, state, new Vector2(stepDelta.x, 0f), false);
                }

                if (!Mathf.Approximately(stepDelta.y, 0f))
                {
                    TryMoveAxis(room, obj, state, new Vector2(0f, stepDelta.y), true);
                }
            }
        }

        void TryMoveAxis(RoomInstance room, ObjectInstance obj, MapObjectDynamicStateData state, Vector2 delta, bool verticalAxis)
        {
            if (verticalAxis)
            {
                state.isGrounded = false;
            }

            Vector2 candidatePosition = obj.position + delta;
            if (HasCollision(room, obj, candidatePosition))
            {
                // Record the refusal for the held prop so `tele probe` can name the blocking tile.
                // Scoped to the held object on purpose: a prop resting on the ground refuses a
                // downward move every single tick, so recording unconditionally would write four
                // fields per prop per tick for a diagnostic that only ever reads the held one.
                if (state.isHeldByTelekinesis)
                {
                    state.hasRejectedMove = true;
                    state.rejectedMoveVertical = verticalAxis;
                    state.rejectedMoveCandidate = candidatePosition;
                    state.rejectedMoveTick = _tickCount;
                }

                float impactSpeed = verticalAxis ? Mathf.Abs(state.velocity.y) : Mathf.Abs(state.velocity.x);
                if (impactSpeed >= MinimumImpactSpeed && obj.IsDynamicPhysicalProp())
                {
                    state.lastImpactSpeed = impactSpeed;
                }

                if (verticalAxis)
                {
                    if (delta.y < 0f)
                    {
                        state.isGrounded = true;
                    }

                    state.velocity = new Vector2(state.velocity.x, 0f);
                }
                else
                {
                    state.velocity = new Vector2(0f, state.velocity.y);
                }

                state.isThrown = false;
                return;
            }

            // ── AS3's prop-shelf fallback (`Box.as:1049`) ─────────────────────────────────────────
            //
            // Reached only when the tile sweep above found nothing, and guarded further on
            // `!levit && !isThrow` exactly as the oracle guards it: a prop being carried or thrown
            // passes OVER another prop rather than landing on it. Its own collision against walls is
            // unaffected — that is the refusal branch above, which runs first.
            //
            // This is the whole of "I can't build a tower". Without it a falling crate has nothing to
            // land on, because `HasCollision` can only see tiles, so every crate falls to the floor and
            // a stack is impossible.
            if (verticalAxis && delta.y < 0f && !state.isHeldByTelekinesis && !state.isThrown &&
                TryFindSupportProp(obj, delta.y, out ObjectInstance support, out float supportTop))
            {
                state.osnova = support;
                state.isGrounded = true;
                state.isThrown = false;
                state.velocity = new Vector2(state.velocity.x, 0f);

                // AS3 `Box.as:1069-1071`: `Y = _loc4_; Y1 = Y - scY; Y2 = Y;` — the prop's BOTTOM is
                // placed on the support's TOP edge. `ObjectInstance.position.y` is the bottom in the
                // port as well (`GetApproximateBounds` builds `Rect(x - w/2, y, w, h)`), so this is a
                // direct copy rather than a centre/edge conversion.
                obj.position = new Vector2(obj.position.x, supportTop);
                return;
            }

            obj.position = ClampToRoomBounds(room, obj, candidatePosition);
        }

        bool HasCollision(RoomInstance room, ObjectInstance obj, Vector2 candidatePosition)
        {
            Rect bounds = obj.GetApproximateBounds(candidatePosition);
            float roomWidthPixels = room.width * WorldConstants.TILE_SIZE;
            float roomHeightPixels = room.height * WorldConstants.TILE_SIZE;

            if (bounds.xMin < 0f || bounds.yMin < 0f || bounds.xMax > roomWidthPixels || bounds.yMax > roomHeightPixels)
            {
                return true;
            }

            return room.CheckCollision(bounds.position, bounds.size);
        }

        /// <summary>
        /// AS3 <c>Unit.checkShelf</c> (<c>Unit.as:2713-2741</c>) — the prop a unit is standing on, and
        /// the height of its top edge. <b>State form:</b> "is there a prop surface within
        /// <paramref name="porogPixels"/> below my feet".
        ///
        /// <para><b>Why this is a different function from <see cref="TryFindSupportProp"/>, and why they
        /// must not be merged.</b> Both answer "is there a prop under me", and they disagree on three of
        /// their four terms — each disagreement is the oracle's:</para>
        ///
        /// <list type="table">
        /// <item><term>horizontal</term><description>a unit tests full AABB overlap
        /// (<c>!(X2 &lt; box.X1 || X1 &gt; box.X2)</c>, <c>:2719</c>); a prop tests whether its
        /// <b>centre</b> is over the support (<c>Box.as:1198</c>). So a unit stands on a crate it is
        /// only half on, while a crate in the same position falls past it.</description></item>
        /// <item><term><c>stay</c></term><description>a prop's support must be at rest
        /// (<c>Box.as:1198</c>); a unit's need not be (<c>Unit.as:2719</c> has no <c>stay</c> term) —
        /// instead a unit that lands on a moving prop <i>pushes it down</i>, which is what the caller
        /// does with the returned support.</description></item>
        /// <item><term><c>levit</c></term><description>both refuse a prop held by telekinesis, because
        /// a crate floating in mid-air is not a platform for either.</description></item>
        /// </list>
        ///
        /// <para><b>Why a band and not the oracle's crossing test.</b> AS3 calls this inside its motion
        /// resolution with the step already known (<c>checkShelf(dy / param1)</c>, <c>:2340</c>), so it
        /// can ask "does this step cross the top". The port resolves a unit's groundedness <i>before</i>
        /// the step (<c>UnitController.ResolveGroundState</c> runs ahead of <c>ApplyGravity</c>), so the
        /// equivalent question is the one the port already asks of tiles: "is there a surface within one
        /// step-up allowance of my feet". <paramref name="porogPixels"/> is that allowance, and the
        /// caller passes <c>TileQueryConstants.PorogGrounded</c> — the same 10 px the tile query
        /// uses.</para>
        ///
        /// <para>The consequence is recorded rather than hidden: a unit can be reported grounded while
        /// up to <c>porog</c> px above the crate's top, where AS3 would only report it on the step that
        /// crossed. The caller snaps the unit down onto the returned surface, so the visible result is a
        /// landing of at most one porog rather than a unit hovering above a crate it is standing
        /// on.</para>
        ///
        /// <para><b>When the step IS known, use <see cref="TryFindPropCrossedThisStep"/>.</b> The band is
        /// a substitute for the crossing test, not an improvement on it, and it is only correct because
        /// the caller has no step to give. A caller that does have one — a motor, which is resolving the
        /// move — must use the crossing form: with a band, a unit whose feet are 8 px above a crate and
        /// which then moves 1 px would be snapped <i>up</i> onto it, so every prop within
        /// <c>porog</c> would behave like a magnet.</para>
        /// </summary>
        /// <param name="feetBoundsRoomLocalPixels">The unit's collider AABB in <b>room-local</b> pixels.
        /// Its <c>yMin</c> is the feet — the same convention as the tile probe, so there is no
        /// seat/centre/edge conversion to get wrong here.</param>
        /// <param name="porogPixels">How far above the surface still counts as standing on it.</param>
        /// <param name="support">The prop found, or null.</param>
        /// <param name="surfaceY">The support's top edge, room-local pixels.</param>
        public bool TryFindGroundPropUnder(
            Rect feetBoundsRoomLocalPixels,
            float porogPixels,
            out ObjectInstance support,
            out float surfaceY)
        {
            // Region is here for the CALL COUNT as much as the time: this is asked once per unit per
            // step by ResolveGroundState, so `calls / calls(sim.tick)` is the unit count and
            // `calls` per second says how often the state query runs. The work itself is charged to
            // phys.prop.surfaceUnder.
            using (PFE.Core.Profiling.PfeProfiler.Region("phys.prop.groundQuery",
                "physics: state query — 'what prop am I standing on'. Once per unit per step."))
            {
            return TryFindPropSurfaceUnder(
                feetBoundsRoomLocalPixels,
                feetBoundsRoomLocalPixels.yMin - porogPixels,
                out support,
                out surfaceY);
            }
        }

        /// <summary>
        /// AS3 <c>Unit.checkShelf</c> (<c>Unit.as:2713-2741</c>) — the prop this step's motion landed on.
        /// <b>Event form:</b> "does this step cross a prop's top edge".
        ///
        /// <para><b>This is the oracle's own test, translated.</b> The comparison is
        /// <c>Y2 + param2 &lt;= box.Y1 &amp;&amp; Y2 + param1 + param2 &gt; box.Y1</c> (<c>:2720</c>) — in
        /// the port's y-up space, "the surface was at or below where my feet started, and is at or above
        /// where they ended". See
        /// <see cref="PFE.Entities.Units.UnitCheckShelfMath.IsLandingOnSurface"/> for the translation and
        /// for the one deliberate deviation (<c>&gt;=</c> where the oracle has <c>&gt;</c>).</para>
        ///
        /// <para><b>Ordering and substepping.</b> A fast fall is a sequence of substeps, and only the
        /// substep that actually crosses lands: passing the whole step as one span would land on a crate
        /// the unit flew past, and passing only the destination would let it tunnel through. So a motor
        /// calls this once per substep, with that substep's before and after.</para>
        ///
        /// <para>The candidate filter is the unit-side one, identical to
        /// <see cref="TryFindGroundPropUnder"/> — full AABB overlap, <c>shelf</c>, not <c>levit</c>, and
        /// deliberately <b>no</b> <c>stay</c> (a unit may land on a crate that is still settling, and
        /// push it down: <c>Unit.as:2734-2737</c>). Only the vertical bound differs between the two
        /// methods, which is why they share one implementation.</para>
        /// </summary>
        /// <param name="feetBoundsBeforeRoomLocalPixels">The unit's collider AABB <b>before</b> the step,
        /// room-local pixels; <c>yMin</c> is the feet.</param>
        /// <param name="feetAfterRoomLocalY">The feet <b>after</b> the step, room-local pixels. Equal to
        /// the before value for a substep with no vertical motion, in which case a surface exactly at the
        /// feet still counts — see <c>UnitCheckShelfMath.IsLandingOnSurface</c> for why.</param>
        /// <param name="support">The prop landed on, or null.</param>
        /// <param name="surfaceY">The support's top edge, room-local pixels.</param>
        public bool TryFindPropCrossedThisStep(
            Rect feetBoundsBeforeRoomLocalPixels,
            float feetAfterRoomLocalY,
            out ObjectInstance support,
            out float surfaceY)
        {
            // The one to watch: a motor calls this ONCE PER SUBSTEP, so its call count is
            // (units x substeps per step x ticks per second) — the only query here whose count is
            // multiplied by the substep count rather than by the unit count. If this region's calls
            // dwarf phys.prop.groundQuery's, the substep loop is the amplifier.
            using (PFE.Core.Profiling.PfeProfiler.Region("phys.prop.crossedQuery",
                "physics: event query — 'what prop did this substep cross'. Once per SUBSTEP, so its count is units x substeps."))
            {
            return TryFindPropSurfaceUnder(
                feetBoundsBeforeRoomLocalPixels,
                feetAfterRoomLocalY,
                out support,
                out surfaceY);
            }
        }

        /// <summary>
        /// The one loop behind <see cref="TryFindGroundPropUnder"/> and
        /// <see cref="TryFindPropCrossedThisStep"/>: a prop whose top edge is at or below
        /// <c>feetBounds.yMin</c> and at or above <paramref name="lowestAcceptedSurfaceRoomLocalY"/>,
        /// horizontally overlapping the feet.
        ///
        /// <para>The two callers differ only in that bound, and the difference is the whole point: a
        /// <i>state</i> query sets it one step-up allowance below the feet ("what am I standing on"), an
        /// <i>event</i> query sets it at the post-step feet ("what did this step cross"). Folding them
        /// into one function with the bound as a parameter keeps the filter — the part carrying the three
        /// oracle citations — in exactly one place.</para>
        /// </summary>
        bool TryFindPropSurfaceUnder(
            Rect feetBoundsRoomLocalPixels,
            float lowestAcceptedSurfaceRoomLocalY,
            out ObjectInstance support,
            out float surfaceY)
        {
            // The shared scan behind both shelf queries: one pass over EVERY tracked dynamic prop,
            // per call. Its self time is the per-candidate cost (IsShelf + GetApproximateBounds, the
            // latter re-parsing `scx`/`scy` per call), and total/calls is what one query costs. With
            // its call count from the two wrappers above, this is the region that answers
            // "is this O(units x objects), and how much per candidate".
            using (PFE.Core.Profiling.PfeProfiler.Region("phys.prop.surfaceUnder",
                "physics: the one scan behind groundQuery + crossedQuery — walks only props that can be a shelf; see phys.prop.candidates."))
            {
            support = null;
            surfaceY = 0f;

            // Demand-driven, so the fixtures — which Register a prop and query without ever ticking the
            // layer — see the same list the game does. See _queryCandidatesDirty.
            EnsureQueryCandidates();

            if (_shelfCandidates.Count == 0)
            {
                return false;
            }

            float feetY = feetBoundsRoomLocalPixels.yMin;
            float myLeft = feetBoundsRoomLocalPixels.xMin;
            float myRight = feetBoundsRoomLocalPixels.xMax;

            // `_shelfCandidates` already applied the unit-independent half of this filter once this
            // tick — null/active/destroyed, `shelf` (Unit.as:2719) and `!levit`. Re-tested here is what
            // a telekinesis grab or a destruction can flip between the rebuild and now, plus the three
            // comparisons that depend on the unit asking. See the candidate-list field block.
            //
            // The list preserves `_dynamicObjects` order, so the FIRST match is the same prop this loop
            // returned before — the result is identical, not merely equivalent.
            for (int i = 0; i < _shelfCandidates.Count; i++)
            {
                ObjectInstance candidate = _shelfCandidates[i];
                if (candidate == null || !candidate.isActive || candidate.IsDestroyed())
                {
                    continue;
                }

                MapObjectDynamicStateData candidateState = candidate.runtimeState?.dynamicState;
                if (candidateState == null || candidateState.isHeldByTelekinesis)
                {
                    continue;
                }

                // The size was resolved once for this prop this tick; only the position is read fresh.
                Rect candidateBounds = candidate.GetApproximateBoundsWithSize(GetCachedPixelSize(candidate));
                float candidateTop = candidateBounds.yMax;

                // The surface has to be at or below the feet (it is a floor, not a lid)...
                if (candidateTop > feetY)
                {
                    continue;
                }

                // ...and no lower than the caller's bound — the step-up allowance for a state query, or
                // where this step's motion ended for an event query.
                if (candidateTop < lowestAcceptedSurfaceRoomLocalY)
                {
                    continue;
                }

                // Full overlap, not centre-in-span — the unit-side rule.
                if (myRight <= candidateBounds.xMin || myLeft >= candidateBounds.xMax)
                {
                    continue;
                }

                support = candidate;
                surfaceY = candidateTop;
                return true;
            }

            return false;
            }
        }

        /// <summary>
        /// AS3 <c>Box.checkShelf</c> (<c>Box.as:1191-1203</c>) — the prop this one may land on, and the
        /// height of its top edge.
        ///
        /// <para><b>The oracle, and the one comparison that is easy to get wrong:</b></para>
        /// <code>
        /// if(!box.invis &amp;&amp; box.stay &amp;&amp; box.shelf
        ///    &amp;&amp; !(X &lt; box.X1 || X &gt; box.X2)          // MY CENTRE inside the support's span
        ///    &amp;&amp; Y2 &lt;= box.Y1 &amp;&amp; Y2 + dy &gt; box.Y1)  // my bottom crosses its top this step
        /// { this.osnova = box; return box.Y1; }
        /// </code>
        ///
        /// <para><b>Centre-in-span, not overlap.</b> This test is <i>not</i> the one
        /// <c>Unit.checkShelf</c> uses: a unit tests full AABB overlap
        /// (<c>!(X2 &lt; box.X1 || X1 &gt; box.X2)</c>, <c>Unit.as:2719</c>), a prop tests whether its
        /// <b>centre</b> is over the support. So a crate hanging half off the edge of another crate
        /// does <i>not</i> land on it — it falls — while a unit in the same position does. Sharing one
        /// overlap helper between the two would silently change which of them is true. See
        /// <see cref="TryFindGroundPropUnder"/> for the unit-side counterpart and the full list of
        /// differences.</para>
        ///
        /// <para><b>Three gates, all from the oracle.</b> <c>stay</c> means the support is at rest, so
        /// a crate still settling is not yet a floor. <c>shelf</c> is
        /// <see cref="ObjectInstance.IsShelf"/> — true by default, see that method. And the support must
        /// not be <c>levit</c>, i.e. not held by telekinesis: a crate floating in mid-air is not a
        /// platform.</para>
        ///
        /// <para><c>invis</c> is the port's <c>!isActive || IsDestroyed()</c> — AS3's <c>invis</c> is set
        /// when a prop falls out of the room's bottom (<c>Box.as:1059</c>), which is the port's
        /// destroyed/inactive state.</para>
        /// </summary>
        /// <param name="obj">The prop looking for something to land on.</param>
        /// <param name="deltaY">This substep's vertical motion, in pixels. <b>Negative is downward</b> in
        /// the port's y-up space, which is the direction that can land.</param>
        /// <param name="support">The prop found, or null.</param>
        /// <param name="surfaceY">The support's top edge in room-local pixels, which is where the falling
        /// prop's bottom belongs.</param>
        public bool TryFindSupportProp(ObjectInstance obj, float deltaY, out ObjectInstance support, out float surfaceY)
        {
            // Prop-side shelf search (Box.checkShelf) — a full pass over tracked props per falling
            // prop per substep. Separate from phys.prop.surfaceUnder because this one is asked BY the
            // prop layer's own stepping loop, so its cost lands inside phys.objects.step.
            using (PFE.Core.Profiling.PfeProfiler.Region("phys.prop.supportQuery",
                "physics: prop-side shelf search (Box.checkShelf) — per falling prop, per substep."))
            {
            support = null;
            surfaceY = 0f;

            if (obj == null || _dynamicObjects.Count == 0)
            {
                return false;
            }

            Rect myBounds = obj.GetApproximateBounds();
            float myCentreX = myBounds.center.x;
            float myBottom = myBounds.yMin;

            for (int i = 0; i < _dynamicObjects.Count; i++)
            {
                ObjectInstance candidate = _dynamicObjects[i];
                if (candidate == null || ReferenceEquals(candidate, obj))
                {
                    continue;
                }

                if (!candidate.isActive || candidate.IsDestroyed())
                {
                    continue;
                }

                if (!candidate.IsShelf())
                {
                    continue;
                }

                MapObjectDynamicStateData candidateState = candidate.runtimeState?.dynamicState;
                if (candidateState == null || !candidateState.stay || candidateState.isHeldByTelekinesis)
                {
                    continue;
                }

                Rect candidateBounds = candidate.GetApproximateBounds();

                if (myCentreX < candidateBounds.xMin || myCentreX > candidateBounds.xMax)
                {
                    continue;
                }

                // ── The crossing test, TRANSLATED out of AS3's y-down space ─────────────────────
                //
                // AS3 is y-DOWN: `Y1 = Y - scY` is a prop's top (smaller y) and `Y2 = Y` its bottom
                // (larger y). The oracle's two comparisons are therefore
                //     Y2 <= box.Y1          -- my bottom is at or above the support's top
                //     Y2 + dy > box.Y1      -- and this step's fall takes me below it
                // with `dy` positive because falling increases y.
                //
                // In the port's y-UP space both the sign of "above" and the sign of `deltaY` flip, so
                // the same two comparisons are `>=` and `<` — NOT the literal forms above. Copying them
                // across untranslated inverts the whole test and the search then never finds anything:
                // which is exactly how this was written the first time, and what
                // `FallingProp_FindsAShelfPropBelowIt` caught.
                if (myBottom < candidateBounds.yMax)
                {
                    continue;
                }

                // This substep does not reach its top, so it is not a landing. (`!(Y2 + dy > box.Y1)`)
                if (myBottom + deltaY >= candidateBounds.yMax)
                {
                    continue;
                }

                support = candidate;
                surfaceY = candidateBounds.yMax;
                return true;
            }

            return false;
            }
        }

        /// <summary>
        /// AS3 <c>Box.attDrop</c> (<c>Box.as:914-940</c>) — the <b>hardest</b> prop that overlaps this
        /// rectangle and is moving fast enough to hurt whatever it touches.
        ///
        /// <para><b>This is the unit-driven inversion of the oracle's loop.</b> AS3 asks the question
        /// the other way round: the <i>box</i> iterates <c>loc.units</c> (<c>:922</c>) and hits each
        /// one it overlaps. The port asks it from the unit, because the unit is the side that already
        /// owns everything the hit needs — its own mass, <c>knocked</c>, <c>fixed</c>, its velocity
        /// and its damage entry point — whereas the prop side would need a
        /// <c>UnitInstance</c> → <c>UnitController</c> link that does not exist (<c>RoomInstance.units</c>
        /// holds data records with no back-pointer).</para>
        ///
        /// <para><b>Why "hardest" is not a behaviour change.</b> AS3 would let every overlapping prop
        /// run <c>udarBox</c> — but <c>udarBox</c> sets <c>neujaz = neujazMax</c> on the unit
        /// (<c>:4222</c>) and its own entry gate refuses a unit that is already <c>neujaz &gt; 0</c>
        /// (<c>:4213</c>), and <c>attDrop</c>'s per-target gate repeats the refusal (<c>:926</c>). So
        /// the <i>second</i> prop in the same window is refused no matter which prop got there first.
        /// Returning one is therefore equivalent in effect, and unlike AS3 it does not depend on the
        /// room's object order.</para>
        ///
        /// <para><b>The gates, in the oracle's order.</b> <c>:632</c>'s ±5 px/frame pre-filter is the
        /// caller's (<c>IsMovingFastEnoughToSweep</c>) — it gates whether the sweep runs at all, before
        /// any target is considered — and this method applies it per candidate as well, so a caller
        /// that skips it cannot get a different answer. Then <c>:918</c>'s <c>vel2 &lt; 50</c>, then
        /// <c>:926</c>'s overlap test.</para>
        ///
        /// <para><b>What is deliberately not modelled.</b> AS3's <c>:924</c> also skips a unit whose
        /// faction is the one currently levitating the prop (<c>fracLevit</c>), so a crate lifted by
        /// your own side cannot hurt you. <c>fracLevit</c> is not ported (0 hits) and a unit standing
        /// under a crate its own side is holding up is a narrow case; recorded rather than
        /// approximated. <c>:926</c>'s <c>sost == 4</c> (dead) and <c>loc != loc</c> (other room) are
        /// the caller's, since it knows which unit it is asking about.</para>
        /// </summary>
        /// <param name="boundsRoomLocalPixels">The unit's collider AABB in <b>room-local</b> pixels —
        /// the same space as <see cref="ObjectInstance.GetApproximateBounds"/>, so no conversion.</param>
        /// <param name="prop">The hardest qualifying prop, or null.</param>
        /// <param name="velocitySquaredPixelsPerFrame">That prop's AS3 <c>vel2</c>, in px per frame
        /// squared. Returned rather than recomputed by the caller so the value the gate accepted is
        /// the value the damage formula uses.</param>
        public bool TryFindImpactingPropFor(
            Rect boundsRoomLocalPixels,
            out ObjectInstance prop,
            out float velocitySquaredPixelsPerFrame)
        {
            prop = null;
            velocitySquaredPixelsPerFrame = 0f;

            // The prop-impact sweep added with "boxes damage units" (Box.attDrop). Asked once per
            // unit per step by UnitController.SweepPropImpacts, and — unlike the shelf queries — it
            // is skipped entirely while the unit is contact-invulnerable, so its call count is the
            // honest measure of how often the camp's crates are actually being swept for.
            using (PFE.Core.Profiling.PfeProfiler.Region("phys.prop.impactQuery",
                "physics: prop-impact sweep (Box.attDrop) — once per unit per step. Walks only props that MOVED this tick; see phys.prop.candidates."))
            {

            // The sweep gate guarantees vel2 >= 50, so -1 can never be the winning score; it is just a
            // "nothing has qualified yet" sentinel that no real vel2 can tie.
            float hardestVelocitySquared = -1f;

            // Demand-driven, so the fixtures — which Register a prop, set its velocity and query without
            // ever ticking the layer — see the same list the game does. See _queryCandidatesDirty.
            EnsureQueryCandidates();

            // `_impactCandidates` holds only the props that MOVED this tick (see the field block), so a
            // room with nothing falling makes this loop empty — which is what turns a per-unit scan of
            // every prop into a per-unit scan of nothing.
            //
            // Everything the old loop did is still done: the flags are re-tested because a telekinesis
            // grab or a destruction can flip them between the rebuild and now, and the two velocity
            // gates are evaluated from LIVE velocity, so a crate thrown in between is judged on what it
            // is actually doing rather than on a frozen number. The list preserves `_dynamicObjects`
            // order, so ties on `hardestVelocitySquared` break exactly as they did before.
            for (int i = 0; i < _impactCandidates.Count; i++)
            {
                ImpactCandidate entry = _impactCandidates[i];
                ObjectInstance candidate = entry.Obj;
                MapObjectDynamicStateData candidateState = entry.State;

                if (candidate == null || candidateState == null ||
                    !candidate.isActive || candidate.IsDestroyed() ||
                    candidateState.isHeldByTelekinesis)
                {
                    continue;
                }

                Vector2 perFrame = PropImpactMath.ToPixelsPerFrame(candidateState.velocity);

                // `Box.as:632`, then `Box.as:918`. Both, in that order — they are not interchangeable;
                // see PropImpactMath.ImpactSweepMinimumSpeedPixelsPerFrame for the one point where
                // they disagree.
                if (!PropImpactMath.IsMovingFastEnoughToSweep(perFrame))
                {
                    continue;
                }

                float vel2 = PropImpactMath.VelocitySquaredFromPerFrame(perFrame);
                if (!PropImpactMath.IsImpactHardEnough(vel2))
                {
                    continue;
                }

                // Cheapest rejection first: a prop that cannot beat the current best does not need its
                // bounds computed.
                if (vel2 <= hardestVelocitySquared)
                {
                    continue;
                }

                // The size was resolved once for this prop this tick; only the position is read fresh.
                Rect candidateBounds = candidate.GetApproximateBoundsWithSize(GetCachedPixelSize(candidate));

                // AS3 `Box.as:926` — `X1 > X2 || X2 < X1 || Y1 > Y2 || Y2 < Y1`, an AABB separation
                // test. Any overlap at all counts; a unit half under a crate is hit.
                if (candidateBounds.xMax < boundsRoomLocalPixels.xMin ||
                    candidateBounds.xMin > boundsRoomLocalPixels.xMax ||
                    candidateBounds.yMax < boundsRoomLocalPixels.yMin ||
                    candidateBounds.yMin > boundsRoomLocalPixels.yMax)
                {
                    continue;
                }

                prop = candidate;
                hardestVelocitySquared = vel2;
                velocitySquaredPixelsPerFrame = vel2;
            }

            return prop != null;
            }
        }

        /// <summary>
        /// AS3 <c>Box.as:571-595</c> — carry a prop that is resting on another prop, and detach it when
        /// it should no longer be carried.
        ///
        /// <para><b>Why a rider follows displacement and not velocity.</b> The support's
        /// <c>cdx</c>/<c>cdy</c> is what it <i>actually</i> moved last tick. A support that was refused a
        /// move has a non-zero velocity and zero displacement, so a rider carried by velocity would
        /// slide off a crate that never moved. Following the displacement also means the rider keeps up
        /// exactly — no lag, no accumulated drift — which is what makes a tower behave like one object
        /// while it is being pushed.</para>
        ///
        /// <para><b>Four detach paths, all the oracle's.</b> The support stopped being <c>stay</c>; the
        /// support became <c>levit</c> (picked up by telekinesis); the carry move is blocked by a tile;
        /// and the rider itself became <c>levit</c>. Three of the four also clear the rider's
        /// <c>stay</c>, because a rider that has just lost its floor is no longer at rest — it is about
        /// to fall, and AS3 says so explicitly (<c>Box.as:577-588</c>).</para>
        ///
        /// <para><b>Ordering, recorded not hidden.</b> AS3 iterates the room's object list, so a rider
        /// stepped <i>after</i> its support in the same tick follows this tick's displacement, while a
        /// rider stepped <i>before</i> it follows the previous tick's. That is a property of the oracle
        /// too (its <c>loc.objs</c> loop has the same order dependence), and it is a one-frame effect,
        /// so it is not worth diverging from AS3 to fix.</para>
        ///
        /// <para><c>wall == 0</c> is not tested here, though AS3 tests it (<c>Box.as:575</c>). It is
        /// provably redundant for a tracked prop:
        /// <c>MapObjectDefinitionClassifier.ResolvePhysicalCapability</c> returns
        /// <see cref="MapObjectPhysicalCapability.Static"/> for any <c>wall &gt; 0</c> prop
        /// (<c>:552-555</c>) <i>before</i> it can return a dynamic capability, and only a dynamic prop is
        /// ever registered in this layer. So every object this method can be called for has
        /// <c>wall == 0</c>.</para>
        /// </summary>
        void FollowSupport(RoomInstance room, ObjectInstance obj, MapObjectDynamicStateData state)
        {
            if (state.isHeldByTelekinesis)
            {
                state.stay = false;
                state.osnova = null;
                return;
            }

            if (state.osnova == null || !state.stay)
            {
                return;
            }

            MapObjectDynamicStateData supportState = state.osnova.runtimeState?.dynamicState;

            if (supportState == null || !supportState.stay || supportState.isHeldByTelekinesis)
            {
                state.stay = false;
                state.osnova = null;
                return;
            }

            if (supportState.cdx == 0f && supportState.cdy == 0f)
            {
                return;
            }

            Vector2 carried = obj.position + new Vector2(supportState.cdx, supportState.cdy);

            if (HasCollision(room, obj, carried))
            {
                state.stay = false;
                state.osnova = null;
                return;
            }

            obj.position = carried;
        }

        Vector2 ClampToRoomBounds(RoomInstance room, ObjectInstance obj, Vector2 candidatePosition)
        {
            Vector2 sizePixels = obj.GetApproximatePixelSize();
            float roomWidthPixels = room.width * WorldConstants.TILE_SIZE;
            float roomHeightPixels = room.height * WorldConstants.TILE_SIZE;

            return new Vector2(
                Mathf.Clamp(candidatePosition.x, sizePixels.x * 0.5f, roomWidthPixels - sizePixels.x * 0.5f),
                Mathf.Clamp(candidatePosition.y, 0f, roomHeightPixels - sizePixels.y));
        }
    }
}
