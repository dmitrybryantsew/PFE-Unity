using UnityEngine;
using PFE.Systems.Map.TileQuery;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// The tile world a thrown object's step needs, reduced to the three questions AS3's
    /// <c>PhisBullet.run()</c> actually asks.
    ///
    /// <para><b>Why an interface and not <see cref="ITileQueryService"/> directly.</b> The step is
    /// pure arithmetic over a point and a tile grid, and the whole value of extracting it is that it
    /// can be driven from a test without a <see cref="PFE.Systems.Map.RoomInstance"/>, a
    /// <c>MonoBehaviour</c> or a running engine — the same reason
    /// <see cref="ProjectilePhysicsMath"/> and <c>TilePhysicsStepMath</c> exist. The production
    /// adapter is <c>ThrownObject.ThrownTileProbe</c>, which forwards to
    /// <see cref="ITileQueryService"/>; the test adapter is a 2D <c>bool[,]</c>.</para>
    ///
    /// <para><b>Coordinates are WORLD PIXELS</b>, the space <see cref="ITileQueryService"/> uses, so
    /// the adapter does the room-origin arithmetic once instead of the step doing it per query.</para>
    /// </summary>
    public interface IThrownTileProbe
    {
        /// <summary>
        /// The room's world-pixel bounds. AS3 <c>loc.limX</c>/<c>limY</c>
        /// (<c>Location.as:267-268</c>, <c>spaceX * Tile.tileX</c>): a thrown object that leaves them
        /// is removed (<c>PhisBullet.as:234-238</c> and <c>:309-313</c>).
        /// </summary>
        Rect RoomBoundsPx { get; }

        /// <summary>
        /// AS3 <c>loc.getAbsTile(X, Y)</c> followed by the <c>phis == 1</c> test
        /// (<c>PhisBullet.as:242/263/286/315</c>). False outside the room, which is exactly what
        /// AS3's <c>otstoy</c> default tile (<c>phis = 0</c>) means.
        /// </summary>
        bool IsSolidAt(Vector2 worldPx);

        /// <summary>
        /// The world-pixel rect of the tile cell containing <paramref name="worldPx"/> — the port's
        /// stand-in for AS3's <c>phX1/phX2/phY1/phY2</c>. Those are <c>X*40 … (X+1)*40</c>
        /// (<c>Tile.as:109-112</c>), i.e. exactly the cell, except for a <c>zForm</c> tile which
        /// raises <c>phY1</c> (<c>Tile.as:292</c>). <c>zForm</c> is not modelled in the port's
        /// <c>TileData</c>, so the cell is exact here — recorded rather than approximated.
        /// </summary>
        Rect CellBoundsAt(Vector2 worldPx);
    }

    /// <summary>
    /// One thrown object's AS3-native simulation state.
    ///
    /// <para><b>Units are AS3's, not the port's.</b> <see cref="PositionPx"/> is world pixels and
    /// <see cref="VelocityPxPerFrame"/> is <b>pixels per 30 Hz frame</b>, so every AS3 comparison
    /// (<c>dy &gt; 2</c>, <c>|dx| &lt; 9</c>) and every AS3 assignment ports literally instead of
    /// through a conversion factor that could be the wrong one of the two this project uses. The
    /// entity converts at exactly two boundaries: <c>Initialize</c> (units/s in) and <c>LateUpdate</c>
    /// (units out to the Transform).</para>
    ///
    /// <para><b>Y is Unity's Y-up</b>, unlike AS3's Y-down. That is the only sign difference, and it
    /// is confined to the vertical branches, each of which names the AS3 line it came from. Gravity
    /// therefore <i>subtracts</i> here where AS3 adds.</para>
    /// </summary>
    public struct ThrownObjectState
    {
        /// <summary>Centre of the object, world pixels.</summary>
        public Vector2 PositionPx;

        /// <summary>Velocity in px/frame, Y-up — so falling is negative.</summary>
        public Vector2 VelocityPxPerFrame;

        /// <summary>AS3 <c>stay</c>: resting on a floor, so the horizontal brake applies.</summary>
        public bool Stay;

        /// <summary>
        /// Set when <c>bumc</c> contact detonation fired. The caller performs the actual explosion;
        /// the step only records it, because a detonation plays sounds, resolves damage and releases
        /// a pooled object — none of which belongs inside a tick.
        /// </summary>
        public bool Detonated;

        /// <summary>AS3 <c>vse</c>: left the room, so the object is removed.</summary>
        public bool Removed;

        /// <summary>
        /// AS3 <c>prilip</c>: a sticky object (<c>lip</c>, i.e. <c>throwTip == 2</c>) has touched a
        /// tile and is now frozen in place, waiting for its fuse.
        ///
        /// <para>The oracle sets it on the first tile contact and then guards the whole movement
        /// block with <c>if(!babah &amp;&amp; !this.prilip)</c> (<c>PhisBullet.as:80</c>), so the object
        /// stops <i>from the next frame</i> — the current frame's remaining sub-steps still run. That
        /// ordering is reproduced: <see cref="ThrownObjectPhysics.Run"/> latches, and
        /// <see cref="ThrownObjectPhysics.Step"/> skips the movement block only once latched.</para>
        /// </summary>
        public bool Latched;

        /// <summary>Sub-steps the last <see cref="ThrownObjectPhysics.Step"/> used. Diagnostics.</summary>
        public int SubStepCount;

        /// <summary>Tile contacts resolved since the last reset. Diagnostics.</summary>
        public int ContactCount;
    }

    /// <summary>
    /// AS3 <c>PhisBullet.step()</c> and <c>PhisBullet.run()</c> — the thrown-object flight model —
    /// as pure arithmetic.
    ///
    /// <para><b>This is a transcription, and the shape is load-bearing.</b> AS3 moves on <b>X first,
    /// resolves X, then moves on Y and resolves Y</b> (<c>PhisBullet.as:213-345</c>), so a corner hit
    /// resolves against the vertical face. It also re-tests the tile after each axis move, and it
    /// <i>re-enters</i> the second vertical branch when the first one flips the sign of <c>dy</c>
    /// (a ceiling bounce sets <c>dy</c> positive, and the following <c>if(dy &gt; 0)</c> then runs).
    /// Both are deliberate: reordering these branches or fusing them into one swept test changes
    /// which face a grenade bounces off.</para>
    ///
    /// <para><b>Tunnelling is prevented by sub-stepping, not by a sweep.</b> AS3 has no swept query
    /// at all: it divides a step into <c>floor(max(|dx|,|dy|) / 9) + 1</c> sub-steps of at most 9 px
    /// (<c>World.as:48</c>, <c>PhisBullet.as:82-95</c>) and does a <b>point</b> test against the cell
    /// the point occupies. Note the count is <c>floor(d/9) + 1</c>, not <c>ceil(d/9)</c> — the two
    /// agree everywhere except at exact multiples of 9, where AS3 uses one extra sub-step. That is
    /// reproduced, not "fixed".</para>
    ///
    /// <para><b>What is deliberately not modelled.</b> (1) <c>loc.sky</c>
    /// (<c>PhisBullet.as:223-231</c>) — an open-sky location skips all tile collision; the port has no
    /// such flag on a room. (2) <c>levit</c> (<c>:50-54</c>) — halves both axes instead of applying
    /// gravity; <c>WThrow.as:177</c> never sets it on the bullet, so no thrown object has it today.
    /// (3) <c>inWater</c> (<c>:75-79</c>) — the same 0.8 damping while inside a water tile.
    /// (4) <c>lip</c>/<c>prilip</c> (<c>:214-222</c>) — <b>the crate-latching half only</b>. The
    /// freeze-on-contact half IS modelled (<c>Latched</c>, driven by <c>sticky</c>); what is missing
    /// is the branch that binds the object to a cracked crate (<c>loc.celObj is Box &amp;&amp;
    /// explcrack</c>) so the blast damages that crate, which needs <c>loc.celObj</c> and a
    /// destructible-prop query the port does not have. (5) <c>sndHit</c> — impact sounds; the port's
    /// thrown object has no impact sound at all, which is a pre-existing gap and not this flip's to
    /// close. Each omission is named so it reads as a decision, not a miss.</para>
    /// </summary>
    public static class ThrownObjectPhysics
    {
        /// <summary>
        /// <c>World.as:48</c> <c>maxdelta = 9</c> — the largest distance one collision sub-step may
        /// cover. AS3's bullet does not sweep, so this <i>is</i> its continuous collision detection.
        /// </summary>
        public const float MaxDeltaPx = TileQueryConstants.MaxDelta;

        /// <summary><c>World.as:46</c> <c>ddy = 1</c>, px/frame². <c>PhisBullet.as:57</c> adds it every frame.</summary>
        public const float GravityPxPerFrame2 = TileQueryConstants.Gravity;

        /// <summary>
        /// <c>PhisBullet.as:61/65</c> <c>if(dx &gt; 1) dx -= brake</c> — the resting-friction
        /// threshold, a <b>velocity</b> in px/frame. Not to be confused with
        /// <see cref="SettleThresholdPxPerFrame"/>, which is the same unit but a different decision.
        /// </summary>
        public const float RestThresholdPxPerFrame = 1f;

        /// <summary>
        /// <c>PhisBullet.as:330</c> <c>if(dy &gt; 2)</c> — a floor impact bounces only while the
        /// vertical speed exceeds this; below it the object settles. Also a velocity.
        /// </summary>
        public const float SettleThresholdPxPerFrame = 2f;

        /// <summary>
        /// <c>PhisBullet.as:116-124</c> — <c>liv</c> counts down from <c>detTime</c> and the
        /// explosion fires when it reaches <b>3</b>, not 0. So a 75-frame fuse burns for 72 frames
        /// (2.4 s at 30 Hz), and the remaining 3 are the explosion animation.
        /// </summary>
        public const int ExplosionLeadInFrames = 3;

        /// <summary>
        /// The 1 px gap AS3 leaves between a bounced object's centre and the face it bounced off:
        /// <c>X = phX2 + 1</c> (<c>PhisBullet.as:252</c>), <c>Y = phY1 - 1</c> (<c>:325</c>).
        /// </summary>
        public const float FaceOffsetPx = 1f;

        /// <summary>
        /// <c>PhisBullet.as:82-95</c>. One sub-step when both axes are under
        /// <see cref="MaxDeltaPx"/>, otherwise <c>floor(max / 9) + 1</c>.
        ///
        /// <para>The <c>+1</c> is why this is not <c>ceil</c>: at <c>max = 9</c> AS3 uses 2 sub-steps
        /// where <c>ceil</c> would use 1, so a step of exactly 9 px is subdivided. Both forms are
        /// "correct" in the sense of never exceeding 9 px; only AS3's is the game.</para>
        /// </summary>
        public static int SubStepCount(float dxPx, float dyPx)
        {
            float max = Mathf.Max(Mathf.Abs(dxPx), Mathf.Abs(dyPx));
            if (max < MaxDeltaPx) return 1;

            return Mathf.FloorToInt(max / MaxDeltaPx) + 1;
        }

        /// <summary>
        /// One AS3 frame of flight: <c>PhisBullet.step()</c> (<c>PhisBullet.as:46-96</c>) without the
        /// fuse, the water check and the view update, which are the caller's.
        ///
        /// <para><b>Call this exactly once per canonical 30 Hz frame.</b> Every quantity in
        /// <paramref name="state"/> is per-frame; calling it at a different rate does not scale, it
        /// changes the physics. That is the whole reason the Stage C flip exists — the legacy path
        /// drives the same maths from <c>FixedUpdate</c> at 50 Hz.</para>
        /// </summary>
        /// <param name="bounceRetention">AS3 <c>skok</c> — 0.4 for a thrown object
        /// (<c>WThrow.as:22</c>), which overrides <c>PhisBullet.as:23</c>'s 0.5 default.</param>
        /// <param name="floorDamping">AS3 <c>tormoz</c> — 0.6 for a thrown object
        /// (<c>WThrow.as:24</c>), overriding <c>PhisBullet.as:25</c>'s 0.7.</param>
        /// <param name="brakePxPerFrame2">AS3 <c>brake</c> — 2 px/frame² (<c>WThrow.as:20</c>),
        /// applied subtractively so it is an acceleration, not a velocity.</param>
        /// <param name="detonateOnContact">AS3 <c>bumc</c> — detonate on the first tile contact
        /// instead of bouncing (<c>PhisBullet.as:248/269/292/317</c>).</param>
        /// <param name="sticky">AS3 <c>lip</c> — latch on the first tile contact instead of bouncing
        /// (<c>PhisBullet.as:254/275/298/326</c>, set from <c>WThrow.as:193</c> for
        /// <c>throwTip == 2</c>).</param>
        public static void Step(ref ThrownObjectState state, IThrownTileProbe probe,
                                float bounceRetention, float floorDamping,
                                float brakePxPerFrame2, bool detonateOnContact, bool sticky = false)
        {
            // AS3 PhisBullet.as:50-58. The `levit` branch halves both axes instead of applying
            // gravity; see the class note — no thrown object sets it, so only the else is ported.
            // AS3 `dy += ddy` is downward; this state is Y-up, so it subtracts.
            state.VelocityPxPerFrame.y -= GravityPxPerFrame2;

            // AS3 PhisBullet.as:59-74. Reproduce the shape exactly: the test is `|dx| > 1` and the
            // subtraction is a full `brake` (2), so a resting dx of 1.5 becomes -0.5 and the NEXT
            // frame snaps it to 0. Writing this as `|dx| > brake` — the shape the port shipped —
            // leaves a resting object creeping for a frame instead of stopping.
            if (state.Stay)
            {
                float dx = state.VelocityPxPerFrame.x;
                if (dx > RestThresholdPxPerFrame)       dx -= brakePxPerFrame2;
                else if (dx < -RestThresholdPxPerFrame) dx += brakePxPerFrame2;
                else                                    dx = 0f;

                state.VelocityPxPerFrame.x = dx;
            }

            // AS3 PhisBullet.as:80 — `if(!babah && !this.prilip)`. A latched sticky object is still
            // gravity-integrated (that is the oracle: only the movement block is guarded) but it does
            // not move, so it sits where it struck.
            if (state.Latched) return;

            // AS3 PhisBullet.as:80-96. `babah` is the detonated flag; the `prilip` half is now the
            // Latched flag above.
            int subSteps = SubStepCount(state.VelocityPxPerFrame.x, state.VelocityPxPerFrame.y);
            state.SubStepCount = subSteps;

            for (int i = 0; i < subSteps; i++)
            {
                Run(ref state, probe, bounceRetention, floorDamping, subSteps, detonateOnContact, sticky);

                // AS3's `while(_loc2_ < _loc1_ && !babah)` breaks on detonation only. Leaving the
                // room does not break it — but every later `run()` re-tests X, sets `vse` again and
                // returns before touching anything, so breaking here is equivalent and cheaper.
                if (state.Detonated || state.Removed) return;
            }
        }

        /// <summary>
        /// One AS3 sub-step: <c>PhisBullet.run(param1)</c> (<c>PhisBullet.as:209-347</c>).
        ///
        /// <para><b>The divisor does not change mid-step.</b> <c>param1</c> is the count computed from
        /// the <i>original</i> deltas, so after a bounce alters <c>dx</c> the remaining sub-steps move
        /// by the new <c>dx</c> divided by the old count. That is why <paramref name="subSteps"/> is a
        /// parameter rather than being recomputed here.</para>
        ///
        /// <para>The two X branches are separate <c>if</c>s, not <c>if/else</c>, and so are the two Y
        /// branches — because AS3 writes them that way and the difference is observable. A leftward
        /// bounce sets <c>dx</c> positive, so AS3's following <c>if(dx &gt; 0)</c> does run; it is
        /// harmless only because the bounce has already moved the point 1 px clear of the tile, and a
        /// sub-step can never be long enough (≤ 9 px) to reach the next column. A ceiling bounce is
        /// the case that actually does something: it leaves <c>dy</c> positive, so the floor branch
        /// then advances Y by the new velocity and re-tests for a floor.</para>
        /// </summary>
        public static void Run(ref ThrownObjectState state, IThrownTileProbe probe,
                               float bounceRetention, float floorDamping,
                               int subSteps, bool detonateOnContact, bool sticky = false)
        {
            // AS3 PhisBullet.as:213 — `X += dx / param1`.
            state.PositionPx.x += state.VelocityPxPerFrame.x / subSteps;

            // AS3 PhisBullet.as:223-231 (`loc.sky`) is not modelled; see the class note.
            //
            // AS3 PhisBullet.as:234-238 — `X < 0 || X >= loc.spaceX * Tile.tileX` removes the object.
            // X only: AS3 has no equivalent test on this path for Y.
            Rect room = probe.RoomBoundsPx;
            if (state.PositionPx.x < room.xMin || state.PositionPx.x >= room.xMax)
            {
                state.Removed = true;
                return;
            }

            // ── X axis. AS3 tests the cell the point now occupies, in the direction of travel. ──

            // AS3 PhisBullet.as:239-259 — `if(dx < 0)`: entered a solid from the right.
            if (state.VelocityPxPerFrame.x < 0f)
            {
                if (probe.IsSolidAt(state.PositionPx))
                {
                    Rect cell = probe.CellBoundsAt(state.PositionPx);
                    if (detonateOnContact) Detonate(ref state);

                    // AS3 `X = _loc2_.phX2 + 1` — 1 px to the RIGHT of the cell, in Y-up terms.
                    state.PositionPx.x = cell.xMax + FaceOffsetPx;
                    state.VelocityPxPerFrame.x = Mathf.Abs(state.VelocityPxPerFrame.x * bounceRetention);
                    if (sticky) state.Latched = true;   // AS3 PhisBullet.as:254 `if(lip) prilip = true`
                    state.ContactCount++;
                }
            }

            // AS3 PhisBullet.as:260-280 — `if(dx > 0)`: entered a solid from the left.
            if (state.VelocityPxPerFrame.x > 0f)
            {
                if (probe.IsSolidAt(state.PositionPx))
                {
                    Rect cell = probe.CellBoundsAt(state.PositionPx);
                    if (detonateOnContact) Detonate(ref state);

                    // AS3 `X = _loc2_.phX1 - 1` — 1 px to the LEFT of the cell.
                    state.PositionPx.x = cell.xMin - FaceOffsetPx;
                    state.VelocityPxPerFrame.x = -Mathf.Abs(state.VelocityPxPerFrame.x * bounceRetention);
                    if (sticky) state.Latched = true;   // AS3 PhisBullet.as:275
                    state.ContactCount++;
                }
            }

            // ── Y axis. AS3 moves Y inside each branch, not before them. ──

            // AS3 PhisBullet.as:281-303 — `if(dy < 0)`: rising, so this is a CEILING.
            // In Y-up, AS3's `dy < 0` is `dy > 0`; written here as the ceiling case it is.
            if (state.VelocityPxPerFrame.y > 0f)
            {
                state.Stay = false;   // AS3 `stay = false` before the move

                state.PositionPx.y += state.VelocityPxPerFrame.y / subSteps;

                if (probe.IsSolidAt(state.PositionPx))
                {
                    Rect cell = probe.CellBoundsAt(state.PositionPx);
                    if (detonateOnContact) Detonate(ref state);

                    // AS3 `Y = _loc2_.phY2 + 1` — phY2 is the cell's BOTTOM in Y-down, so this is
                    // 1 px below it: in Y-up, 1 px under `cell.yMin`.
                    state.PositionPx.y = cell.yMin - FaceOffsetPx;
                    state.VelocityPxPerFrame.y = -Mathf.Abs(state.VelocityPxPerFrame.y * bounceRetention);
                    if (sticky) state.Latched = true;   // AS3 PhisBullet.as:298
                    state.ContactCount++;
                }
            }

            // AS3 PhisBullet.as:305-345 — `if(dy > 0)`: falling, so this is a FLOOR.
            if (state.VelocityPxPerFrame.y < 0f)
            {
                state.Stay = false;   // AS3 `stay = false` before the move

                state.PositionPx.y += state.VelocityPxPerFrame.y / subSteps;

                // AS3 checks the room's floor BEFORE the tile test, and only on this branch.
                // AS3 `Y >= loc.spaceY * Tile.tileY` is the room's bottom in Y-down; Y-up it is yMin.
                if (state.PositionPx.y <= room.yMin)
                {
                    state.Removed = true;
                    return;
                }

                if (probe.IsSolidAt(state.PositionPx))
                {
                    Rect cell = probe.CellBoundsAt(state.PositionPx);
                    if (detonateOnContact) Detonate(ref state);

                    // AS3 `Y = _loc2_.phY1 - 1` — phY1 is the cell's TOP in Y-down, so 1 px above it:
                    // in Y-up, 1 px over `cell.yMax`.
                    state.PositionPx.y = cell.yMax + FaceOffsetPx;
                    if (sticky) state.Latched = true;   // AS3 PhisBullet.as:326

                    // AS3 `if(dy > 2)`: a floor bounce needs more than 2 px/frame of fall speed.
                    // Read against the magnitude, so the Y-up sign does not enter the test.
                    if (-state.VelocityPxPerFrame.y > SettleThresholdPxPerFrame)
                    {
                        state.VelocityPxPerFrame.y =
                            Mathf.Abs(state.VelocityPxPerFrame.y * bounceRetention);
                        state.VelocityPxPerFrame.x *= floorDamping;
                    }
                    else
                    {
                        state.VelocityPxPerFrame.y = 0f;
                        state.Stay = true;
                    }

                    state.ContactCount++;
                }
            }
        }

        /// <summary>
        /// AS3 <c>PhisBullet.popadalo()</c> (<c>PhisBullet.as:187-207</c>) as far as the step is
        /// concerned: zero both axes and raise the detonation flag.
        ///
        /// <para>AS3 also fires the explosion and clamps <c>liv</c> to 1 here. Both are the caller's,
        /// because an explosion resolves damage and plays a sound — work a tick must not do. Note the
        /// zeroing is <b>not</b> cosmetic: it is what makes the surrounding bounce block write
        /// <c>dx = |0 * skok| = 0</c> and what makes the floor branch's <c>dy &gt; 2</c> test false,
        /// so a <c>bumc</c> object stops dead instead of bouncing.</para>
        /// </summary>
        private static void Detonate(ref ThrownObjectState state)
        {
            state.VelocityPxPerFrame = Vector2.zero;
            state.Detonated = true;
        }
    }
}
