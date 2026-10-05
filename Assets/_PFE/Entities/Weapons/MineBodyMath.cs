using UnityEngine;
using PFE.Entities.Units;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;

namespace PFE.Entities.Weapons
{
    /// <summary>
    /// What AS3's mine placement retry decided — <c>WThrow.as:163-172</c>.
    /// </summary>
    public enum MinePlacement
    {
        /// <summary>The hand position was legal; the mine stays where it was spawned.</summary>
        Keep,

        /// <summary>The hand position was inside a tile but the owner's feet are not — retry there.</summary>
        RetryAtOwner,

        /// <summary>Both positions are inside geometry — AS3 <c>fixed = true</c>; the mine never falls.</summary>
        Pin,
    }

    /// <summary>
    /// One step's outcome: AS3 <c>Unit.dx</c>/<c>dy</c> after <c>forces()</c>, plus the origin the
    /// step resolved to.
    /// </summary>
    public readonly struct MineBodyStep
    {
        /// <summary>Velocity after this step, in Unity units per second.</summary>
        public readonly Vector2 Velocity;

        /// <summary>The mine's origin after this step, in <b>world pixels</b>.</summary>
        public readonly Vector2 OriginPixels;

        /// <summary>
        /// True when the origin actually changed, i.e. the caller must write the position. False for a
        /// pinned mine and for a step that both started and ended seated.
        /// </summary>
        public readonly bool Moved;

        /// <summary>
        /// AS3 <c>isLaz</c> at the start of this step — the answer <see cref="Step"/> already asked.
        ///
        /// <para>Returned rather than left to the caller to re-ask, because the question is about the
        /// position the step <i>started</i> from: a second query after the move would sample a
        /// different point and could legitimately disagree. One step, one probe.</para>
        /// </summary>
        public readonly bool Grounded;

        public MineBodyStep(Vector2 velocity, Vector2 originPixels, bool moved, bool grounded)
        {
            Velocity     = velocity;
            OriginPixels = originPixels;
            Moved        = moved;
            Grounded     = grounded;
        }
    }

    /// <summary>
    /// The mine's body — AS3 <c>Unit.forces()</c> + <c>Unit.run()</c> for the non-flying branch, as a
    /// function of its inputs.
    ///
    /// <para><b>Why this is not inline in <see cref="MineObject"/>.</b> The same reason
    /// <c>UnitFallPhysics</c> and <c>ProjectilePhysicsMath</c> exist: while the arithmetic is computed
    /// inside a <c>MonoBehaviour</c>, pinning it needs a GameObject, a collider, a running physics
    /// simulation and a room, so the test never gets written and the value drifts. Everything below is
    /// a function of numbers plus the tile query, so a fixture can drive a real fall over a synthetic
    /// room and assert where it lands — no scene, no <c>MovePosition</c>, no physics step.</para>
    ///
    /// <para><b>The mine is a Unit, so these are Unit rules, not mine-specific inventions.</b>
    /// <c>WThrow.as:142-174</c> does <c>new Mine(id)</c> and <c>loc.units.push(_loc5_)</c>, so a placed
    /// mine joins the ordinary unit loop; <c>Mine.as:178</c> sets <c>isFly = false</c> and <c>grav</c>
    /// keeps its <c>Unit.as:256</c> default of 1, so the non-flying branch of <c>Unit.as</c> runs and
    /// the mine falls from the hand to the floor. Only <c>WThrow.as:171</c>'s <c>fixed = true</c>
    /// stops it being a physics object, and that is the rare case.</para>
    /// </summary>
    public static class MineBodyMath
    {
        /// <summary>
        /// AS3 <c>sX</c> on every mine weapon row — <c>&lt;weapon id='hmine' … sX='30' sY='20'&gt;</c>
        /// (<c>AllData.as</c>). All eight mine rows carry the same pair, which is why this is a
        /// constant rather than an imported field.
        /// </summary>
        public const float BoxWidthPixels = 30f;

        /// <inheritdoc cref="BoxWidthPixels"/>
        public const float BoxHeightPixels = 20f;

        /// <summary>
        /// AS3 <c>Unit.setPos</c> (<c>Unit.as:1872-1880</c>) — the collision box from the unit's own
        /// origin:
        /// <code>
        /// X1 = X - scX / 2;   X2 = X + scX / 2;
        /// Y1 = Y - scY;       Y2 = Y;
        /// </code>
        /// <para><b>The origin is the box's bottom centre, not its centre.</b> AS3's Y axis is
        /// down-positive, so <c>Y2 = Y</c> is the box's <i>lowest</i> edge and <c>Y1 = Y - scY</c> is
        /// its highest — the box hangs entirely <i>above</i> the origin. In Unity's up-positive space
        /// that is <c>yMin == origin.y</c>, which is also why <c>Unit.setPos(owner.X, owner.Y)</c>
        /// drops a mine at the owner's <b>feet</b>: a unit's origin is its collider bottom
        /// (<c>UnitGroundProbe</c>, <c>RoomUnitSpawner</c>'s <c>offset = (0, +Height/2)</c>).</para>
        ///
        /// <para>This is the whole point of the function. A box centred on the origin — which is what
        /// the mine prefab used to carry, as a 0.5-unit circle — rests the mine half its own height
        /// too high, and the art, which is anchored at the origin (<c>Unit.setVisPos</c> does
        /// <c>vis.y = Y</c>), visibly floats.</para>
        /// </summary>
        public static Rect BoxRectPixels(
            Vector2 originPx,
            float widthPx = BoxWidthPixels,
            float heightPx = BoxHeightPixels)
        {
            return new Rect(
                originPx.x - widthPx * 0.5f,
                originPx.y,
                widthPx,
                heightPx);
        }

        /// <summary>
        /// The rect to hand <c>ITileQueryService.IsOnGround</c> for a mine standing at this origin.
        /// <see cref="UnitGroundProbe"/> owns the convention (the 1 px seat is undone so the sample
        /// lands <i>inside</i> the ground tile rather than on its boundary); this is that convention
        /// applied to the mine's own box.
        /// </summary>
        public static Rect GroundProbeRectPixels(Vector2 originPx)
        {
            Rect box = BoxRectPixels(originPx);

            return new Rect(
                box.xMin,
                box.yMin - UnitGroundProbe.SeatPixels,
                box.width,
                box.height);
        }

        /// <summary>
        /// The point <c>ITileQueryService.IsOnGround</c> actually samples for a mine at this origin:
        /// the box bottom lowered by the seat and then by the query's own 1 px probe
        /// (<c>TileCollisionMath.IsOnGround</c> reads <c>boundsPx.yMin - 1f</c>).
        ///
        /// <para>Used for the landing snap, which must ask about <i>the same tile</i> the groundedness
        /// test just found — asking about a different point would let the two disagree and seat the
        /// mine on the wrong surface.</para>
        /// </summary>
        public static Vector2 GroundProbePoint(Vector2 originPx)
        {
            Rect box = BoxRectPixels(originPx);

            return new Vector2(
                box.center.x,
                box.yMin - UnitGroundProbe.SeatPixels - 1f);
        }

        /// <summary>
        /// One step of AS3 <c>Unit.forces()</c> — the non-flying branch (<c>Unit.as:1958-2000</c>) —
        /// plus the horizontal brake, as a pure function of the current velocity.
        /// </summary>
        /// <param name="grounded">AS3 <c>isLaz</c> — "standing on something" (<c>Unit.as:1962</c>).</param>
        public static Vector2 StepVelocity(Vector2 velocity, bool grounded, float deltaTime)
        {
            float velocityY;

            if (grounded)
            {
                // AS3 integrates nothing while `isLaz`. Only a DOWNWARD dy is cancelled — an upward
                // one is left alone, which is what lets a jump (or a blast's knock-up) leave the
                // ground on the tick it starts. Cancelling both would swallow it.
                velocityY = velocity.y < 0f ? 0f : velocity.y;
            }
            else
            {
                velocityY = UnitFallPhysics.FallSpeed(velocity.y, deltaTime);
            }

            return new Vector2(UnitFallPhysics.GroundBrake(velocity.x, deltaTime), velocityY);
        }

        /// <summary>
        /// AS3 <c>if(!this.fixed) run()</c> (<c>Unit.as:1809</c>) — the single gate on the position
        /// write.
        ///
        /// <para><b><c>forces()</c> sits ABOVE that gate</b> (<c>Unit.as:1807-1810</c>), so a pinned
        /// mine keeps accumulating velocity and simply never applies it. That is why the gate belongs
        /// to the move and not to the integration, and why it is a separate function rather than an
        /// early return inside the step.</para>
        /// </summary>
        public static bool AppliesMotion(bool pinned)
        {
            return !pinned;
        }

        /// <summary>
        /// The whole body step: AS3 <c>Unit.forces()</c> then <c>Unit.run()</c>'s vertical half,
        /// returning the velocity and the origin the mine ends the step at.
        ///
        /// <para><b>The landing SNAPS to the tile edge.</b> AS3 does not leave the unit wherever the
        /// step happened to put it — <c>run()</c> resolves the contact and assigns
        /// <c>Y = _loc2_.phY1</c>, the tile's top edge (<c>Unit.as:2308</c>, <c>:2330</c>). That
        /// matters here far more than it does for a walking unit: a mine falls from the hand, and at
        /// 50 Hz a fall of one metre arrives at ~10 px per step — half the mine's own height. Without
        /// the snap the mine stops up to a full step inside the floor, and because the art is anchored
        /// at the origin the whole sprite sinks with it.</para>
        ///
        /// <para><b>No room ⇒ no motion.</b> A null tile query is the reduced answer a unit with no
        /// room already gets; inventing a fall towards nothing would be worse than standing still.</para>
        /// </summary>
        /// <param name="tileQuery">The room's tile grid, or null.</param>
        /// <param name="velocity">Current velocity in Unity units per second.</param>
        /// <param name="originPixels">Current origin in world pixels — AS3 <c>X</c>/<c>Y</c>.</param>
        /// <param name="pinned">AS3 <c>Unit.fixed</c>.</param>
        /// <param name="deltaTime">Seconds covered by this step.</param>
        public static MineBodyStep Step(
            ITileQueryService tileQuery,
            Vector2 velocity,
            Vector2 originPixels,
            bool pinned,
            float deltaTime)
        {
            if (tileQuery == null)
            {
                // The velocity is handed back untouched rather than zeroed: nothing was integrated,
                // so there is no result to report, and a caller's state is not this function's to
                // discard. `Moved: false` is the whole answer.
                return new MineBodyStep(velocity, originPixels, moved: false, grounded: false);
            }

            bool grounded = tileQuery.IsOnGround(GroundProbeRectPixels(originPixels));

            velocity = StepVelocity(velocity, grounded, deltaTime);

            if (!AppliesMotion(pinned))
            {
                return new MineBodyStep(velocity, originPixels, moved: false, grounded);
            }

            Vector2 nextOrigin;

            if (grounded)
            {
                // AS3 `Unit.run()` — `Y = _loc5_` with `_loc5_ = _loc2_.phY1` (`Unit.as:2343-2356`),
                // i.e. the ground tile's TOP edge, followed by `Y1 = Y - scY; Y2 = Y` so the box's
                // bottom lands exactly on it. `GetGroundHeight` returns that same edge for a wall or a
                // platform (`TileCollisionMath.cs:392-395`) and the interpolated surface for a slope,
                // so this is the oracle's value rather than a re-derivation of it.
                //
                // It is deliberately NOT `surfacePx + UnitGroundProbe.SeatPixels`. The +1 seat is
                // `RoomPopulator.ResolveLegacyBottomAnchorPixels`'s translation of a legacy room's
                // bottom anchor (`RoomPopulator.cs:605`) — a PLACEMENT convention, not a physics one.
                // AS3's landing has no such offset, and `UnitController` has no tile-edge snap at all
                // (it only snaps onto a crate, `Move()`), so there is no second port convention here
                // for the mine to agree with. A mine rests with its box bottom on the edge.
                float surfacePx = tileQuery.GetGroundHeight(GroundProbePoint(originPixels));

                // The x half of the step still applies: the oracle's ground branch moves X and only
                // pins Y to the surface.
                nextOrigin = new Vector2(
                    originPixels.x + velocity.x * deltaTime * TileQueryConstants.UnitToPixel,
                    surfacePx);
            }
            else
            {
                nextOrigin = originPixels + velocity * deltaTime * TileQueryConstants.UnitToPixel;
            }

            bool moved = (nextOrigin - originPixels).sqrMagnitude > 0f;

            return new MineBodyStep(velocity, nextOrigin, moved, grounded);
        }

        /// <summary>
        /// AS3 <c>WThrow.as:163-172</c>:
        /// <code>
        /// if(_loc5_.collisionAll()) { _loc5_.setPos(owner.X, owner.Y); }
        /// if(_loc5_.collisionAll()) { _loc5_.fixed = true; }
        /// </code>
        ///
        /// <para>The two tests are sequential and the second asks about the position the first one
        /// moved the mine to — which is why this cannot be collapsed into a single branch on the
        /// spawn position.</para>
        /// </summary>
        /// <param name="collidesAtHand"><c>collisionAll()</c> at the weapon's own position.</param>
        /// <param name="canRetry">
        /// Whether there is an owner to fall back to. AS3 dereferences <c>owner</c> unconditionally
        /// here and would throw, so the port's reduced answer is to treat "no owner" as "cannot
        /// retry" and let the pin decide.
        /// </param>
        /// <param name="collidesAfterRetry">
        /// The same test re-run after <c>setPos(owner.X, owner.Y)</c>. Ignored when
        /// <paramref name="canRetry"/> is false.
        /// </param>
        public static MinePlacement ResolvePlacement(
            bool collidesAtHand,
            bool canRetry,
            bool collidesAfterRetry)
        {
            if (!collidesAtHand) return MinePlacement.Keep;
            if (canRetry && !collidesAfterRetry) return MinePlacement.RetryAtOwner;
            return MinePlacement.Pin;
        }
    }
}
