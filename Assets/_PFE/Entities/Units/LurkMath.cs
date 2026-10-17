// LurkMath — the pure geometry of AS3's lurk (take-cover) verb.
//
// Ports `unit/UnitPlayer.as:2992-3053` (lurk/unlurk) and the per-tick block at `:1030-1056`
// (the peek drive and the auto-exit). Nothing in here touches Unity: the caller supplies
// the boxes, the tile predicate and the unit's position, exactly as `StealthMath` does.
//
// **Y convention.** Every vertical quantity is in **AS3 terms**, i.e. `y - 10` means ten
// units in the oracle's up direction. AS3's Y runs *down* and the port's runs *up*
// (project rule #18: a mirror, never a floor), so the wiring half must negate once, here —
// not inside this class. Getting that wrong produces a probe point 10 px *below* the feet,
// which still returns an answer for any box big enough, so it will not look like a bug.

using System;
using System.Collections.Generic;

namespace PFE.Entities.Units
{
    /// <summary>
    /// One lurk-capable box — the geometry half of AS3's `Box` that <c>UnitPlayer.lurk()</c>
    /// reads. Deliberately a port-local struct rather than the port's prop type: this class is
    /// Unity-free, and the wiring half owns the mapping from whatever the prop importer produced.
    /// </summary>
    /// <remarks>
    /// <para>Only four fields of the oracle's <c>Box</c> matter to lurk: <c>lurk</c> (the cover
    /// grade, authored 1..3), the rect (<c>X1/Y1/X2/Y2</c>), the centre <c>X</c>, and
    /// <c>stay</c>. <c>wall</c> is read only by the auto-exit test, so it travels with the box
    /// rather than being resolved by index.</para>
    /// </remarks>
    public struct LurkBox
    {
        /// <summary>The cover grade — AS3 <c>Box.lurk</c>. 0 = not cover at all.</summary>
        public int Lurk;

        /// <summary>AS3 <c>Box.X</c> — the box's centre, which is what a grade-1 snap aims at.</summary>
        public float X;

        public float X1;
        public float Y1;
        public float X2;
        public float Y2;

        /// <summary>
        /// AS3 <c>Box.stay</c>. The auto-exit drops lurk when the box is not <c>stay</c> — a
        /// movable crate is cover you cannot hold.
        /// </summary>
        public bool Stay;

        /// <summary>AS3 <c>Box.wall</c>. Zero means "not a wall", which also drops lurk.</summary>
        public int Wall;

        public LurkBox(int lurk, float x, float x1, float y1, float x2, float y2, bool stay = true, int wall = 1)
        {
            Lurk = lurk;
            X = x;
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
            Stay = stay;
            Wall = wall;
        }

        /// <summary>Convenience: a box centred on <paramref name="x"/> with the given size.</summary>
        public static LurkBox Centered(int lurk, float x, float y, float halfWidth, float halfHeight,
            bool stay = true, int wall = 1)
            => new LurkBox(lurk, x, x - halfWidth, y - halfHeight, x + halfWidth, y + halfHeight, stay, wall);
    }

    /// <summary>What a lurk attempt resolved to.</summary>
    public struct LurkPlacement
    {
        /// <summary>True when the unit actually took cover (either path).</summary>
        public bool Lurked;

        /// <summary>AS3 <c>lurkTip</c> — 0 when nothing was found, else the winning grade (1..3).</summary>
        public int LurkTip;

        /// <summary>
        /// Where the unit ends up standing horizontally — AS3 <c>lurkX</c>. Only meaningful when
        /// <see cref="Lurked"/>.
        /// </summary>
        public float LurkX;

        /// <summary>
        /// AS3 <c>storona</c> as *written by lurk*: 1 = snap to the box's right edge,
        /// -1 = its left, 0 = untouched (a grade-1 box or the tile path both leave it alone).
        /// </summary>
        public int Storona;

        /// <summary>
        /// Index into the caller's box list, or <see cref="NoBox"/> for the tile fallback and
        /// <see cref="Nothing"/> for the "no cover anywhere" path.
        /// </summary>
        public int BoxIndex;
    }

    /// <summary>The lurk scan and its exit test, as pure functions.</summary>
    /// <remarks>
    /// <para>Offline half of S7. The wiring half is the tap/hold input split, the
    /// <c>TileData.lurk</c> reader, the animation trigger and the input lock — all of which need
    /// a Unity run (project rule #5).</para>
    /// </remarks>
    public static class LurkMath
    {
        /// <summary>AS3 <c>Tile.tileX</c> = 40; the port's <c>TileQueryConstants.TileSize</c> = 40.</summary>
        public const float TileSize = 40f;

        /// <summary>
        /// The containment probe sits <c>10</c> above the unit — AS3's literal <c>Y - 10</c>,
        /// used by both the box scan and the tile fallback. A unit's feet at a wall's base is
        /// therefore *not* inside it; its chest is.
        /// </summary>
        public const float ProbeOffsetY = 10f;

        /// <summary>
        /// The tile fallback probes <c>X ± 20</c> — half a tile, so it asks about the two tiles
        /// the unit is standing between rather than the one it is centred on.
        /// </summary>
        public const float FlankOffsetX = 20f;

        /// <summary>The inset applied when snapping to a grade-2 box's edge: <c>X2 - 10</c> / <c>X1 + 10</c>.</summary>
        public const float SnapInset = 10f;

        /// <summary>The grade at which the unit snaps to an edge instead of the centre.</summary>
        public const int EdgeSnapGrade = 2;

        /// <summary>Auto-exit once the unit has drifted this far from <c>lurkX</c>.</summary>
        public const float ExitDriftPixels = 10f;

        /// <summary>The peek drive's deadband — inside it the unit stands still.</summary>
        public const float DriveDeadband = 3f;

        /// <summary>The peek drive's speed, px/frame.</summary>
        public const float DriveSpeed = 4f;

        /// <summary>Returned by <see cref="TryLurk"/> when the tile fallback produced the cover.</summary>
        public const int NoBox = -1;

        /// <summary>Returned by <see cref="TryLurk"/> when there was no cover anywhere.</summary>
        public const int Nothing = -2;

        /// <summary>
        /// The gate in front of the whole verb — AS3's <c>if(stay &amp;&amp; !isSit &amp;&amp; dx &lt; 5 &amp;&amp;
        /// dx &gt; -5 &amp;&amp; stayPhis &gt;= 1 &amp;&amp; this.work == "")</c>.
        /// </summary>
        /// <remarks>
        /// <para><c>work == ""</c> is deliberately **not** a parameter: it is animation state the
        /// caller owns, and a free function cannot know it. Everything else is asked here, in the
        /// oracle's order, because the order is what documents that the check is a conjunction and
        /// not a priority list.</para>
        /// <para>The <c>dx</c> window is <c>|dx| &lt; 5</c> — strictly, so exactly 5 is rejected.
        /// That is the oracle's literal text, quirks and all.</para>
        /// </remarks>
        public static bool CanLurk(bool stay, bool isSit, float dx, int stayPhis)
            => stay && !isSit && dx < 5f && dx > -5f && stayPhis >= 1;

        /// <summary>
        /// AS3's <c>for each(_loc1_ in loc.objs)</c> scan — the box that wins the cover, or -1.
        /// </summary>
        /// <remarks>
        /// <para>Three details decide this, and all three are "off by one in the boring direction":</para>
        /// <para><b>1. Strictly-inside on all four edges.</b> The oracle is
        /// <c>X &gt; X1 &amp;&amp; X &lt; X2 &amp;&amp; (Y-10) &gt; Y1 &amp;&amp; (Y-10) &lt; Y2</c>. A unit standing
        /// exactly on a wall's left face is not in cover — which matters at tile boundaries, where
        /// exact equality is the common case rather than the rare one.</para>
        /// <para><b>2. The probe is the unit's chest, not its feet.</b> <see cref="ProbeOffsetY"/>
        /// is subtracted before the vertical test.</para>
        /// <para><b>3. The best-so-far is strictly greater.</b> <c>lurk &gt; this.lurkTip</c>, with
        /// <c>lurkTip</c> starting at 0. So a later box of *equal* grade never displaces an
        /// earlier one — <b>first wins on ties</b>, and iteration order is load-bearing. A
        /// <c>&gt;=</c> here would silently make the *last* overlapping box win, which is a
        /// different game when two cover props share a wall.</para>
        /// </remarks>
        public static int BestLurkBox(IList<LurkBox> boxes, float x, float y)
        {
            if (boxes == null) return -1;

            int best = -1;
            int bestLurk = 0; // AS3's this.lurkTip, which starts at 0 and gates the first pick
            float probeY = y - ProbeOffsetY;

            for (int i = 0; i < boxes.Count; i++)
            {
                LurkBox b = boxes[i];
                if (b.Lurk > bestLurk
                    && x > b.X1 && x < b.X2
                    && probeY > b.Y1 && probeY < b.Y2)
                {
                    bestLurk = b.Lurk;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// Where a winning box puts the unit: its centre for grade 1, its nearest <b>edge</b> for
        /// grade 2 — AS3's <c>if(this.lurkBox.lurk == 2) { … } else { this.lurkX = this.lurkBox.X; }</c>.
        /// </summary>
        /// <remarks>
        /// <para>The edge snap is a *peek* mechanic, not a placement nicety: clinging to the
        /// near face of a high box is what exposes the unit to fire from the other side. It
        /// compares the unit against the box's <b>centre</b> (<c>X &gt; this.lurkBox.X</c>,
        /// strictly) and then walks <see cref="SnapInset"/> back in from the far edge — so the
        /// unit ends up hugging the face it approached from, never flush against it.</para>
        /// <para>Grade 3 is not special-cased: the oracle's <c>== 2</c> is an equality, so a
        /// grade-3 box snaps to the centre like grade 1. Authored data only ever writes 1..2,
        /// but a hand-edited 3 would behave as "centre", and that is what is ported.</para>
        /// </remarks>
        public static float SnapLurkX(LurkBox box, float x)
        {
            if (box.Lurk == EdgeSnapGrade)
                return x > box.X ? box.X2 - SnapInset : box.X1 + SnapInset;

            return box.X;
        }

        /// <summary>
        /// The facing the edge snap implies — AS3's <c>storona</c> write. 0 when the snap did not
        /// run, because the oracle leaves <c>storona</c> alone on both the grade-1 and tile paths,
        /// and a caller that reset it would lose the direction the unit was already facing.
        /// </summary>
        public static int SnapStorona(LurkBox box, float x)
            => box.Lurk == EdgeSnapGrade ? (x > box.X ? 1 : -1) : 0;

        /// <summary>
        /// AS3's <c>Math.round(X / Tile.tileX) * Tile.tileX</c> — the tile fallback's snap.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the one place .NET is not a drop-in.</b> AS3's <c>Math.round</c> is
        /// <c>floor(x + 0.5)</c>; <c>MathF.Round</c> is banker's rounding — <c>Round(0.5)</c> is
        /// <b>0</b>, where the oracle gives 1, and <c>Round(2.5)</c> is 2 where the oracle gives 3.
        /// Half-tile positions are exactly where the fallback fires, so using the framework
        /// method would silently snap every unit half a tile to the left of where the oracle puts
        /// it. Hence <c>Floor(x / size + 0.5f)</c>. Asserted directly by the fixture.</para>
        /// </remarks>
        public static float SnapToTile(float x, float tileSize = TileSize)
        {
            if (tileSize <= 0f) return x;
            return MathF.Floor(x / tileSize + 0.5f) * tileSize;
        }

        /// <summary>
        /// The tile fallback's test — AS3's
        /// <c>Boolean(loc.getAbsTile(X - 20, Y - 10).lurk) &amp;&amp; Boolean(loc.getAbsTile(X + 20, Y - 10).lurk)</c>.
        /// </summary>
        /// <remarks>
        /// <b>Both flanks, not one.</b> Cover against a lone protruding tile is rejected — the
        /// oracle requires a run of lurk floor at least a tile wide. The vertical probe is the
        /// same <see cref="ProbeOffsetY"/> as the box scan, which is easy to drop and produces a
        /// test that passes for every full-height tile.
        /// </remarks>
        public static bool FlankTilesLurk(Func<float, float, bool> tileIsLurk, float x, float y)
        {
            if (tileIsLurk == null) return false;
            float probeY = y - ProbeOffsetY;
            return tileIsLurk(x - FlankOffsetX, probeY) && tileIsLurk(x + FlankOffsetX, probeY);
        }

        /// <summary>
        /// The whole verb, in the oracle's order: the box scan, then the grade-2 snap, then the
        /// tile fallback, then nothing (which in AS3 falls through to <c>armorAbil()</c> — out of
        /// scope here, so the caller sees <see cref="Nothing"/> and decides).
        /// </summary>
        /// <param name="boxes">All boxes in the location, in the location's iteration order —
        /// that order is load-bearing (see <see cref="BestLurkBox"/>).</param>
        /// <param name="tileIsLurk">"Is this tile lurk floor", in absolute AS3 coordinates.</param>
        /// <param name="x">The unit's X.</param>
        /// <param name="y">The unit's Y, in AS3 terms (up is negative).</param>
        /// <param name="tileSize">Tile width, defaulting to 40 on both sides.</param>
        public static LurkPlacement TryLurk(IList<LurkBox> boxes, Func<float, float, bool> tileIsLurk,
            float x, float y, float tileSize = TileSize)
        {
            var placement = new LurkPlacement { BoxIndex = Nothing };

            int index = BestLurkBox(boxes, x, y);
            if (index >= 0)
            {
                LurkBox box = boxes[index];
                placement.Lurked = true;
                placement.LurkTip = box.Lurk;
                placement.BoxIndex = index;
                placement.LurkX = SnapLurkX(box, x);
                placement.Storona = SnapStorona(box, x);
                return placement;
            }

            if (FlankTilesLurk(tileIsLurk, x, y))
            {
                placement.Lurked = true;
                placement.LurkTip = 1;
                placement.BoxIndex = NoBox;
                placement.LurkX = SnapToTile(x, tileSize);
                return placement;
            }

            return placement;
        }

        /// <summary>
        /// AS3's per-tick exit block (<c>UnitPlayer.as:1041-1055</c>) — any one of three
        /// independent conditions drops lurk, and they are a disjunction, not a cascade.
        /// </summary>
        /// <remarks>
        /// <para><b>The three conditions are not equally reachable.</b> <c>!stay</c> is airborne
        /// or unsupported; <c>wall == 0 || !stay</c> on the box is cover that stopped being
        /// cover (a crate that moved, or a wall that broke); the drift test is
        /// <c>work != "lurk" &amp;&amp; |X - lurkX| &gt; 10</c> — so it cannot fire while the entry
        /// animation is still driving the unit toward its snap point, which would otherwise
        /// cancel the verb on its own first frame for a grade-2 box snapped 10+ px away.</para>
        /// </remarks>
        /// <param name="boxIndex">The winning index from <see cref="TryLurk"/>, or
        /// <see cref="NoBox"/> for the tile path (which has no box to invalidate).</param>
        public static bool ShouldExitLurk(IList<LurkBox> boxes, int boxIndex, bool stay,
            float x, float lurkX, bool inLurkWork)
        {
            if (!stay) return true;

            if (boxIndex >= 0 && boxes != null && boxIndex < boxes.Count)
            {
                LurkBox box = boxes[boxIndex];
                if (box.Wall == 0 || !box.Stay) return true;
            }

            if (!inLurkWork && (x - lurkX > ExitDriftPixels || x - lurkX < -ExitDriftPixels)) return true;

            return false;
        }

        /// <summary>
        /// AS3's peek drive (<c>:1030-1038</c>): while the entry animation runs, nudge the unit
        /// toward <c>lurkX</c> at 4 px/frame, deadbanded at 3.
        /// </summary>
        /// <remarks>
        /// Both branches can hold in the same tick in principle (they cannot — the deadbands are
        /// disjoint) and AS3 writes <c>dx</c> rather than adding to it, so a unit already being
        /// pushed is *replaced*, not compounded. Returns 0 inside the deadband, which is what
        /// "stand still behind cover" means.
        /// </remarks>
        public static float LurkDriveDx(float x, float lurkX, bool inLurkWork)
        {
            if (!inLurkWork) return 0f;
            if (lurkX - x > DriveDeadband) return DriveSpeed;
            if (lurkX - x < -DriveDeadband) return -DriveSpeed;
            return 0f;
        }
    }
}

