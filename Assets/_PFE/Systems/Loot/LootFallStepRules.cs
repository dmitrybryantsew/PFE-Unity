using System;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// A tile's solid box, in <b>room-local pixels, y-up</b> — the four numbers AS3 keeps as
    /// <c>phX1</c>/<c>phX2</c>/<c>phY1</c>/<c>phY2</c>, already mirrored.
    ///
    /// <para><b>Why a struct and not <c>TileData</c>.</b> <see cref="LootFallStepRules"/> has to be
    /// executable in an offline harness, and <c>TileData</c> is a Unity type (<c>Rect</c>,
    /// <c>Vector2Int</c>). Handing the rule four floats lets the fixture build a tile grid out of
    /// nothing and run the real decision code — not a copy of it.</para>
    ///
    /// <para><c>Top</c> is <see cref="PFE.Systems.Map.TileData.SolidTopY"/> (<c>phY1</c>) and
    /// <c>Bottom</c> is <see cref="PFE.Systems.Map.TileData.SolidBottomY"/> (<c>phY2</c>). They are
    /// <i>not</i> the cell edges for a raised (<c>zForm</c>) tile: the top comes down and the bottom
    /// stays put.</para>
    /// </summary>
    public readonly struct LootTileBox
    {
        /// <summary>AS3 <c>phX1</c> — the left edge.</summary>
        public readonly float Left;

        /// <summary>AS3 <c>phX2</c> — the right edge.</summary>
        public readonly float Right;

        /// <summary>AS3 <c>phY1</c> — the surface a loot lands on, in the port's y-up space.</summary>
        public readonly float Top;

        /// <summary>AS3 <c>phY2</c> — the underside, in the port's y-up space.</summary>
        public readonly float Bottom;

        public LootTileBox(float left, float right, float top, float bottom)
        {
            Left = left;
            Right = right;
            Top = top;
            Bottom = bottom;
        }
    }

    /// <summary>What stopped a collision sub-step, if anything.</summary>
    public enum LootContact
    {
        /// <summary>Nothing was hit; the loot is still moving.</summary>
        None = 0,

        /// <summary>A floor, a shelf or a shelf prop caught it from above — AS3's <c>stay = true</c>.</summary>
        Floor = 1,

        /// <summary>Its bottom entered a solid tile while rising — <c>Loot.as:444-448</c>.</summary>
        Ceiling = 2,

        /// <summary>A wall stopped a leftward move — <c>Loot.as:417-425</c>.</summary>
        WallLeft = 3,

        /// <summary>A wall stopped a rightward move — <c>Loot.as:426-434</c>.</summary>
        WallRight = 4,

        /// <summary>It reached the room's bottom edge — <c>Loot.as:454-462</c>.</summary>
        FellOutOfRoom = 5,
    }

    /// <summary>
    /// One sub-step of <c>Loot.run</c>, in and out. Every coordinate is <b>room-local pixels, y-up</b>:
    /// <see cref="CentreX"/> is the loot's horizontal centre and <see cref="Bottom"/> is its
    /// <b>bottom edge</b>, which is the edge the oracle's <c>Y</c> names.
    /// </summary>
    public readonly struct LootFallState
    {
        /// <summary>Horizontal centre after the step, room-local px.</summary>
        public readonly float CentreX;

        /// <summary>Bottom edge after the step, room-local px.</summary>
        public readonly float Bottom;

        /// <summary>Horizontal speed after the step, px/frame, positive right.</summary>
        public readonly float Vx;

        /// <summary>Vertical speed after the step, px/frame, positive <b>up</b>.</summary>
        public readonly float Vy;

        /// <summary>What the step hit.</summary>
        public readonly LootContact Contact;

        /// <summary>
        /// The surface that caught it, room-local px, or <see cref="float.NaN"/> when nothing did.
        /// Only meaningful for <see cref="LootContact.Floor"/>.
        /// </summary>
        public readonly float SurfaceTop;

        /// <summary>
        /// The <b>downward</b> speed at the moment of a landing, px/frame — the oracle's <c>dy</c> at
        /// <c>Loot.as:481</c>, which is what gates and scales the landing sample. Zero for every other
        /// contact, because <see cref="Vy"/> is already zeroed by the time the caller sees it.
        /// </summary>
        public readonly float ImpactSpeed;

        public LootFallState(float centreX, float bottom, float vx, float vy,
                             LootContact contact, float surfaceTop, float impactSpeed)
        {
            CentreX = centreX;
            Bottom = bottom;
            Vx = vx;
            Vy = vy;
            Contact = contact;
            SurfaceTop = surfaceTop;
            ImpactSpeed = impactSpeed;
        }

        /// <summary>
        /// Whether the fall is over — the oracle's <c>stay</c> set, or the room's bottom edge reached.
        /// A wall or ceiling contact leaves the loot in the air.
        /// </summary>
        public bool Stopped => Contact == LootContact.Floor || Contact == LootContact.FellOutOfRoom;
    }

    /// <summary>
    /// The tile at a room-local pixel point, or <c>false</c> when there is no tile there (outside the
    /// room, or the grid is not filled in).
    /// </summary>
    /// <param name="roomLocalX">Point X, room-local px.</param>
    /// <param name="roomLocalY">Point Y, room-local px, y-up.</param>
    /// <param name="box">That tile's solid box.</param>
    /// <param name="solid">AS3 <c>phis == 1</c> — the only kind a loot collides with.</param>
    /// <param name="shelf">AS3 <c>shelf</c> — a one-way catwalk, catchable from above only.</param>
    public delegate bool LootTileProbe(
        float roomLocalX, float roomLocalY, out LootTileBox box, out bool solid, out bool shelf);

    /// <summary>
    /// A shelf <b>prop</b> under the loot — AS3 <c>Loot.checkShelf</c> (<c>Loot.as:511-525</c>).
    /// </summary>
    /// <param name="centreX">The loot's centre X, room-local px.</param>
    /// <param name="bottom">Its bottom edge, room-local px.</param>
    /// <param name="deltaY">This sub-step's motion; <b>negative is downward</b> in the port's y-up space.</param>
    /// <param name="surfaceTop">The support's top edge, room-local px.</param>
    public delegate bool LootShelfProbe(
        float centreX, float bottom, float deltaY, out float surfaceTop);

    /// <summary>
    /// The collision half of AS3 <c>Loot.run</c> (<c>fe/loc/Loot.as:402-494</c>) as one pure function —
    /// the port's whole falling-loot physics decision, with no <c>MonoBehaviour</c> in it.
    ///
    /// <para><b>Why this exists, and why it is a rewrite rather than a tidy-up.</b> The first port of
    /// <c>run()</c> lived inside <c>WorldItemPickup.StepOnce</c> and had <b>two independent defects that
    /// cancelled into the same symptom — loot falls through the floor</b>:</para>
    ///
    /// <list type="number">
    ///   <item><description><b>The vertical comparisons were not mirrored.</b> The oracle is y-DOWN: it
    ///     catches a tile when <c>Y + dy/param1 &gt;= phY1</c> (the new bottom has crossed the tile's
    ///     <i>top</i>) <b>and</b> <c>Y &lt;= phY2</c> (the old bottom was above its underside). Mirrored
    ///     to y-up that is <c>bottomAfter &lt;= Top</c> and <c>bottom &gt;= Bottom</c>. The port wrote
    ///     both with the opposite sense — <c>bottomAfter &gt;= Top &amp;&amp; bottom &lt;= Bottom</c> —
    ///     which is <b>unsatisfiable by construction</b>: the tile is chosen as the one containing
    ///     <c>bottomAfter</c>, so <c>bottomAfter &lt; Top</c> always holds (a value on the boundary
    ///     floors to the row above). The branch could therefore never fire, and the loot fell straight
    ///     through every floor in the game.</description></item>
    ///   <item><description><b>The tile geometry was compared against world pixels.</b> The loot's
    ///     position was converted to <i>world</i> pixels but <c>TileData.GetBounds()</c> is
    ///     <i>room-local</i>, so every edge comparison was off by the room's origin — zero for a room at
    ///     land (0,0), which is the room every quick test visits, and one room width or height anywhere
    ///     else. The same mix-up fed world coordinates to
    ///     <c>RoomObjectPhysicsLayer.TryFindLootSupport</c>, whose parameters are documented room-local.
    ///     </description></item>
    /// </list>
    ///
    /// <para>Both are invisible to any test that cannot run the decision, which is why the decision now
    /// lives here: <c>WorldItemPickup.StepOnce</c> reads a transform, converts once, calls this, and
    /// writes the answer back. The arithmetic is pinned by
    /// <c>LootFallStepRulesTests</c> against the oracle's lines.</para>
    ///
    /// <para><b>What is deliberately not modelled.</b> The oracle's <c>vsos</c> (the loot magnet)
    /// suppresses gravity and the shelf search. A magnetised pickup never reaches this function —
    /// <c>FallTick</c> clears <c>_falling</c> first — so it is a constant <c>false</c> here rather than a
    /// parameter nobody varies. The <c>loc.active == false</c> floor override (<c>Loot.as:472-475</c>) is
    /// likewise absent: the port has no inactive-location loot to place.</para>
    ///
    /// <para><b><c>levit</c> used to be in that list, and was wrong to be.</b> AS3's loot IS liftable —
    /// <c>Loot.as:245</c> sets <c>levitPoss = true</c> and <c>:356-360</c> damps it while <c>levit</c> is
    /// set — so "loot is never levitated" was an assumption about the port, not a fact about the oracle.
    /// <see cref="Step"/> now takes it as <paramref name="levitating"/>, and the three things it changes
    /// are all from <c>Loot.run</c>: the tile's <c>shelf</c> clause is skipped (<c>:464</c> is
    /// <c>_loc2_.shelf &amp;&amp; !levit &amp;&amp; !this.vsos</c>, so a carried object passes THROUGH a
    /// beam), the shelf-prop search is skipped (<c>:468</c> is
    /// <c>if(_loc4_ == 0 &amp;&amp; !levit &amp;&amp; !this.vsos)</c>) and a floor contact <b>clamps but does
    /// not stop</b> the loot (<c>:476-487</c> zeroes <c>dx</c>/<c>dy</c> only inside
    /// <c>if(!levit)</c>).</para>
    /// </summary>
    public static class LootFallStepRules
    {
        /// <summary>
        /// <c>Y = _loc4_ - 1</c> (<c>Loot.as:478</c>) — a landed loot rests <b>one pixel clear</b> of the
        /// surface, which is why its bottom edge is not exactly the tile's top.
        ///
        /// <para><b>This ADDS in the port's space and the sign is not a detail.</b> The oracle subtracts
        /// because its Y runs down: <c>phY1 - 1</c> is one pixel <i>above</i> the surface. Mirrored to
        /// y-up the same point is <c>Top + 1</c>. Writing <c>Top - 1</c> — which the first port did —
        /// buries the loot one pixel inside the tile it landed on, and the error is a single character.
        /// It is the same <c>+1</c> seat <c>WorldCoordinates.As3BottomCentreAnchorPixels</c> applies to
        /// every imported object and <c>UnitGroundProbe.SeatPixels</c> undoes for a unit's ground probe,
        /// so the three agree.</para>
        /// </summary>
        public const float LandClearancePx = 1f;

        /// <summary>
        /// <c>X = _loc2_.phX2 + 1</c> / <c>X = _loc2_.phX1 - 1</c> (<c>Loot.as:422</c>, <c>:431</c>) — the
        /// same one-pixel clearance on the horizontal walls.
        /// </summary>
        public const float WallClearancePx = 1f;

        /// <summary>
        /// One collision sub-step — the body of <c>Loot.run(param1)</c>, translated out of AS3's y-down
        /// space.
        ///
        /// <para><b>The order of the branches is the oracle's and it matters.</b> Horizontal first (with
        /// two <i>independent</i> <c>if</c>s, not an <c>else</c> — the left branch flips <c>dx</c> to
        /// positive, so the right branch legitimately runs in the same sub-step, which is how a loot
        /// wedged between two walls bounces), then the rising branch, then the falling branch. The
        /// rising branch zeroes <c>vy</c> on a ceiling hit, so the falling branch cannot run after it —
        /// exactly as <c>dy = 0</c> does in the oracle.</para>
        /// </summary>
        /// <param name="centreX">The loot's centre X, room-local px.</param>
        /// <param name="bottom">Its bottom edge, room-local px.</param>
        /// <param name="halfWidth">Half its drawn width, px — <c>scX / 2</c>. Only the room-edge clamps
        /// use it; the oracle's tile tests compare the <i>centre</i> only.</param>
        /// <param name="height">Its drawn height, px — <c>scY</c>. Only the ceiling clamp uses it.</param>
        /// <param name="vx">Horizontal speed, px/frame, positive right.</param>
        /// <param name="vy">Vertical speed, px/frame, positive <b>up</b>.</param>
        /// <param name="divisor">The sub-step count (<c>LootFallRules.SubstepCount</c>), <c>&gt;= 1</c>.</param>
        /// <param name="roomWidthPx">The room's width, px (<c>loc.spaceX * Tile.tileX</c>).</param>
        /// <param name="roomHeightPx">The room's height, px (<c>loc.spaceY * Tile.tileY</c>).</param>
        /// <param name="tileProbe">Tile lookup; null means "no tiles", i.e. fall freely.</param>
        /// <param name="shelfProbe">Shelf-prop lookup; null means "no props".</param>
        /// <param name="levitating">
        /// AS3 <c>levit</c> — the loot is being carried by telekinesis. It skips <b>both</b> shelf tests
        /// (the tile's <c>shelf</c> clause at <c>Loot.as:464</c> and the prop search at <c>:468</c>) and a
        /// solid floor contact clamps its bottom without stopping it or reporting a contact. Default
        /// <c>false</c> so every pre-existing call site keeps the falling behaviour.
        /// </param>
        public static LootFallState Step(
            float centreX, float bottom, float halfWidth, float height,
            float vx, float vy, int divisor,
            float roomWidthPx, float roomHeightPx,
            LootTileProbe tileProbe, LootShelfProbe shelfProbe, bool levitating = false)
        {
            if (divisor < 1) divisor = 1;

            // ── Horizontal: `X += dx / param1`, then the two room edges ────────────────────────
            float x = centreX + vx / divisor;

            // Loot.as:407-411 — `if(X - scX/2 < 0) { X = scX/2; dx = Math.abs(dx); }`
            if (x - halfWidth < 0f)
            {
                x = halfWidth;
                vx = Math.Abs(vx);
            }

            // Loot.as:412-416 — `if(X + scX/2 >= loc.spaceX * Tile.tileX) { X = … - 1 - scX/2; dx = -|dx|; }`
            if (x + halfWidth >= roomWidthPx)
            {
                x = roomWidthPx - 1f - halfWidth;
                vx = -Math.Abs(vx);
            }

            LootContact contact = LootContact.None;

            // ── Horizontal tile pushes. Two INDEPENDENT ifs, exactly as Loot.as:417/:426 has them.
            if (vx < 0f && tileProbe != null &&
                tileProbe(x, bottom, out LootTileBox left, out bool leftSolid, out _) && leftSolid &&
                x <= left.Right && x >= left.Left && bottom <= left.Top && bottom >= left.Bottom)
            {
                // `X = phX2 + 1` — just clear of the tile's right edge.
                x = left.Right + WallClearancePx;
                vx = Math.Abs(vx);
                contact = LootContact.WallLeft;
            }

            if (vx > 0f && tileProbe != null &&
                tileProbe(x, bottom, out LootTileBox right, out bool rightSolid, out _) && rightSolid &&
                x >= right.Left && x <= right.Right && bottom <= right.Top && bottom >= right.Bottom)
            {
                // `X = phX1 - 1` — just clear of the tile's left edge.
                x = right.Left - WallClearancePx;
                vx = -Math.Abs(vx);
                contact = LootContact.WallRight;
            }

            // ── Rising (the oracle's `dy < 0`, Loot.as:435-449) ───────────────────────────────
            if (vy > 0f)
            {
                bottom += vy / divisor;

                // `if(Y - scY < 0) Y = scY` — the loot's TOP may not leave the room.
                if (bottom + height > roomHeightPx)
                {
                    bottom = roomHeightPx - height;
                }

                // `if(phis == 1 && Y <= phY2 && Y >= phY1 && X in [phX1, phX2]) Y = phY2 + 1; dy = 0;`
                // — the oracle tests the loot's BOTTOM here, not its top, and snaps it to one pixel
                // below the tile's underside. Mirrored: `bottom = Bottom - 1`.
                if (tileProbe != null &&
                    tileProbe(x, bottom, out LootTileBox above, out bool aboveSolid, out _) && aboveSolid &&
                    bottom <= above.Top && bottom >= above.Bottom && x >= above.Left && x <= above.Right)
                {
                    bottom = above.Bottom - LandClearancePx;
                    vy = 0f;
                    contact = LootContact.Ceiling;
                }
            }

            // ── Falling (the oracle's `dy > 0`, Loot.as:450-493) ───────────────────────────────
            if (vy < 0f)
            {
                float bottomAfter = bottom + vy / divisor;

                // Loot.as:454-462 — `if(Y + dy/param1 >= loc.spaceY * Tile.tileY) { if(auto2) take(true);
                // dx = 0; return; }`. In y-up the room's floor is y = 0, so "past the bottom" is `<= 0`.
                if (bottomAfter <= 0f)
                {
                    return new LootFallState(x, bottom, 0f, 0f, LootContact.FellOutOfRoom, float.NaN, 0f);
                }

                float surface = float.NaN;

                // Loot.as:463-467, both clauses. The solid one needs the old bottom above the tile's
                // underside; the shelf one needs it above the shelf's TOP, which is what makes a catwalk
                // catchable from above only -- AND it is gated on `!levit`, like every other shelf test
                // in the oracle (`Loot.as:464`, `:468`; the prop equivalent is `Box.collisionTile`'s
                // `|| levit || this.isThrow` at `Box.as:1270`).
                //
                // Missing that guard is what "I cannot drop a carried object through a ledge" was: a
                // levitated pickup was caught by every beam it was pushed onto, because the clamp below
                // (`Y = _loc4_ - 1`) runs even for a levitated loot while the `if(!levit)` that follows
                // only guards the STOP. AS3 leaves `_loc4_` at 0 for a levitated loot on a shelf, so the
                // beam is simply not there for a carried object and it passes through. Solid tiles are
                // NOT gated -- a levitated loot still cannot be pushed into a wall (`phis == 1` has no
                // `levit` term in `Loot.run`), which is the difference between "passes through a catwalk"
                // and "passes through the world".
                if (tileProbe != null &&
                    tileProbe(x, bottomAfter, out LootTileBox below, out bool belowSolid, out bool belowShelf) &&
                    x >= below.Left && x <= below.Right)
                {
                    if (belowSolid && bottomAfter <= below.Top && bottom >= below.Bottom)
                    {
                        surface = below.Top;
                    }
                    else if (belowShelf && !levitating && bottomAfter <= below.Top && bottom >= below.Top)
                    {
                        surface = below.Top;
                    }
                }

                // `if(_loc4_ == 0 && !levit && !this.vsos) _loc4_ = this.checkShelf(dy / param1);`
                // — only when the tile search found nothing, and only for a free loot: a levitated one
                // passes over crates rather than landing on them (`Loot.as:468`).
                if (float.IsNaN(surface) && !levitating && shelfProbe != null &&
                    shelfProbe(x, bottom, bottomAfter - bottom, out float propTop))
                {
                    surface = propTop;
                }

                if (!float.IsNaN(surface))
                {
                    // `if(_loc4_) { Y = _loc4_ - 1; … dy = dx = 0; }` — and the sound is emitted AFTER
                    // the move, at the landed position (Loot.as:478-483). The clearance is added, not
                    // subtracted: see LandClearancePx.
                    float impact = -vy;

                    // `Y = _loc4_ - 1;` happens unconditionally, but everything after it — the sample,
                    // `stay = true`, and `dy = dx = 0` — sits inside `if(!levit)` (`Loot.as:479-487`).
                    // So a levitated loot resting on the floor keeps its velocity and reports NO contact:
                    // the controller is still driving it, and the next tick's accel continues from
                    // whatever speed the damping left. Reporting `Floor` here would make `Stopped` true
                    // and the caller would end the carry the first time the player pushed the item down.
                    if (levitating)
                    {
                        return new LootFallState(x, surface + LandClearancePx, vx, vy,
                                                 LootContact.None, surface, impact);
                    }

                    return new LootFallState(x, surface + LandClearancePx, 0f, 0f,
                                             LootContact.Floor, surface, impact);
                }

                bottom = bottomAfter;
            }

            return new LootFallState(x, bottom, vx, vy, contact, float.NaN, 0f);
        }
    }
}
