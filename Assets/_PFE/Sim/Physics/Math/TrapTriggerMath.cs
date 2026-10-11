using UnityEngine;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// AS3 <c>loc.Trap</c> (<c>fe/loc/Trap.as</c>) as pure functions — the static floor/ceiling spike
    /// objects (<c>spikes</c>, <c>fspikes</c>), <b>not</b> the <c>UnitTrap</c> bear trap.
    ///
    /// <para><b>Why this file exists.</b> A placed <c>spikes</c> object was inert: <c>RoomPopulator</c>
    /// built an <c>ObjectInstance</c> with <c>objectType = "trap"</c> and there was no reader for it
    /// anywhere, so the object drew the shared <c>vismine</c> sprite and did nothing. 540 of the 639
    /// shipped rooms place one of these, which is what the player sees as "the traps are decorative".</para>
    ///
    /// <para><b>Two spaces, and the sign between them is the trap.</b> The oracle is AS3: Y grows
    /// <i>down</i>, and <c>dy</c> is positive while falling. This port's Y grows <b>up</b> and its
    /// velocity is up-positive — a mirror, not a translation. Everything in this class that deals with a
    /// <i>value</i> is therefore written in <b>AS3</b> terms (<c>dy &gt; 8</c> means falling fast, the
    /// band is computed from an AS3 anchor), and every quantity that crosses the boundary does so through
    /// <see cref="ComputeBox"/> or <see cref="ToAs3Dy"/>. Mixing the two silently inverts "spikes catch
    /// you falling" into "spikes catch you jumping", which reads as working code.</para>
    /// </summary>
    public static class TrapTriggerMath
    {
        /// <summary>AS3 <c>World.tileX</c>/<c>tileY</c> — both 40 (<c>World.as:36-37</c>).</summary>
        public const float TileSizePixels = 40f;

        /// <summary>
        /// AS3 <c>@att</c> value 1 — the trap fires on a unit moving <b>down</b> into it
        /// (<c>Trap.as:186</c>). <c>spikes</c> is the shipped example.
        /// </summary>
        public const int TriggerFalling = 1;

        /// <summary>
        /// AS3 <c>@att</c> value 2 — the trap fires on a unit moving <b>up</b> into it, tested against
        /// the unit's <b>head</b> rather than its feet (<c>Trap.as:191</c>). <c>fspikes</c> is the
        /// shipped example.
        /// </summary>
        public const int TriggerRising = 2;

        /// <summary>
        /// AS3 <c>Trap.as:186</c> — <c>param1.dy &gt; 8</c>. A unit has to be falling faster than
        /// 8 px/frame to land on spikes; a unit standing on them or stepping across them is untouched.
        /// </summary>
        public const float FallingSpeedThreshold = 8f;

        /// <summary>
        /// The trap's trigger volume in <b>room-local pixels</b>, Y up. <c>LowerY</c>/<c>UpperY</c> are
        /// already mirrored into this port's convention by <see cref="ComputeBox"/>.
        /// </summary>
        public readonly struct TrapBox
        {
            public readonly float LeftX;
            public readonly float RightX;
            public readonly float LowerY;
            public readonly float UpperY;

            public TrapBox(float leftX, float rightX, float lowerY, float upperY)
            {
                LeftX = leftX;
                RightX = rightX;
                LowerY = lowerY;
                UpperY = upperY;
            }

            public float Width => RightX - LeftX;

            public float Height => UpperY - LowerY;

            /// <summary>AS3 <c>Trap.as:186</c> — <c>param1.X &lt;= X2 &amp;&amp; param1.X &gt;= X1</c>.</summary>
            public bool ContainsX(float x) => x <= RightX && x >= LeftX;

            /// <summary>The vertical half of the same test, in this port's Y-up space.</summary>
            public bool ContainsY(float y) => y <= UpperY && y >= LowerY;
        }

        /// <summary>
        /// AS3 <c>Trap.as:54-65</c> — the trigger volume, mirrored into room-local (Y-up) pixels.
        ///
        /// <para>Oracle:</para>
        /// <code>
        /// X1 = X - scX / 2;  X2 = X + scX / 2;
        /// if (floor) { Y1 = Y - scY;      Y2 = Y; }
        /// else       { Y1 = Y - tileY;    Y2 = Y1 + scY; }
        /// </code>
        ///
        /// <para><paramref name="anchorBottomY"/> is the port's bottom anchor — the same physical point
        /// as the oracle's <c>Y</c>, because AS3 places an object at
        /// <c>(tileY + 1) * tileY - 1</c>, i.e. its tile's bottom edge, and
        /// <c>RoomPopulator.ResolveLegacyBottomAnchorPixels</c> reproduces that. The two branches are
        /// kept separate rather than unified because they genuinely differ: the <c>floor</c> arm is
        /// <c>scY</c> tall ending at the anchor, the other is <b>one tile</b> tall starting there.</para>
        /// </summary>
        public static TrapBox ComputeBox(
            float anchorCenterX, float anchorBottomY, float extentX, float extentY, bool floor)
        {
            float halfWidth = extentX * 0.5f;
            float leftX = anchorCenterX - halfWidth;
            float rightX = anchorCenterX + halfWidth;

            if (floor)
            {
                // AS3 Y1 = Y - scY, Y2 = Y. Both are at-or-above the anchor on screen, so in Y-up
                // space the band runs from the anchor upward by scY.
                return new TrapBox(leftX, rightX, anchorBottomY, anchorBottomY + extentY);
            }

            // AS3 Y1 = Y - tileY, Y2 = Y1 + scY. Screen [Y - tileY, Y - tileY + scY] becomes
            // Y-up [anchor - (scY - tileY), anchor + tileY].
            float upper = anchorBottomY + TileSizePixels;
            return new TrapBox(leftX, rightX, upper - extentY, upper);
        }

        /// <summary>
        /// AS3 <c>Trap.as:79-86</c> — <c>scX = (@sX &gt; 0) ? @sX : @size * tileX</c>.
        /// <c>@sX</c> is an explicit pixel width and wins when present.
        /// </summary>
        public static float ResolveExtentX(int sizeAttribute, float sXAttribute)
        {
            return sXAttribute > 0f ? sXAttribute : sizeAttribute * TileSizePixels;
        }

        /// <summary>
        /// AS3 <c>Trap.as:87-94</c> — <c>scY = (@sY &gt; 0) ? @sY : @wid * tileY</c>.
        /// </summary>
        public static float ResolveExtentY(int widAttribute, float sYAttribute)
        {
            return sYAttribute > 0f ? sYAttribute : widAttribute * TileSizePixels;
        }

        /// <summary>
        /// The port's <c>VelocityPixelsPerFrame.y</c> as AS3's <c>dy</c>.
        ///
        /// <para><b>This negation is the whole reason the conversion is a named function.</b>
        /// <c>TilePhysicsController.ApplyGravity</c> does <c>dy -= gravity</c> and integrates
        /// <c>targetY = posY + stepMoveY</c>, so a falling unit has a <b>negative</b> y velocity here;
        /// the oracle's <c>dy</c> is <b>positive</b> while falling. Comparing the raw port value against
        /// <see cref="FallingSpeedThreshold"/> would make every trap fire on the wrong motion — and it
        /// would still look like it worked, because a unit that jumps also has a nonzero dy.</para>
        /// </summary>
        public static float ToAs3Dy(float portVelocityPixelsPerFrameY) => -portVelocityPixelsPerFrameY;

        /// <summary>
        /// AS3 <c>Trap.attKorp</c> (<c>Trap.as:180-197</c>) — whether this trap fires on this unit, and
        /// for how much.
        ///
        /// <para>Both arms require <c>!isFly</c>: a flyer passes over spikes. The port has no flying flag
        /// on a unit yet, so <paramref name="isFlying"/> is threaded from <c>UnitController.IsFlying</c>,
        /// whose default is <c>false</c> — i.e. today every unit is treated as ground-bound, which is the
        /// oracle's behaviour for every unit the base map spawns except the bloat emitter. Recorded
        /// rather than hidden.</para>
        ///
        /// <para><paramref name="unitDyAs3"/> must already be in AS3 sign — use
        /// <see cref="ToAs3Dy"/>. <paramref name="unitOsnDyAs3"/> is AS3 <c>osndy</c>, the <i>support's</i>
        /// last-tick vertical displacement; the port does not carry it for tile support, so callers pass
        /// <c>0</c> and the rising arm degenerates to <c>dy &lt; 0</c>. That is the dominant term of
        /// <c>dy + osndy &lt; 0</c> for a unit jumping off a static tile, and it is the one gap this port
        /// leaves in the trap rule.</para>
        /// </summary>
        public static bool TryResolveDamage(
            in TrapBox box,
            int triggerAttribute,
            float damageAttribute,
            float unitCenterX,
            float unitFeetY,
            float unitHeadY,
            float unitDyAs3,
            float unitOsnDyAs3,
            float massa,
            float difficulty,
            bool isFlying,
            out float damage)
        {
            damage = 0f;

            if (isFlying)
            {
                return false;
            }

            // AS3 multiplies by `(1 + loc.locDifLevel * 0.1)`, so difficulty 0 is a 1.0x multiplier.
            float difficultyScale = 1f + difficulty * 0.1f;

            if (triggerAttribute == TriggerFalling)
            {
                // Trap.as:186 — feet inside the box, and falling faster than the threshold.
                if (unitDyAs3 > FallingSpeedThreshold &&
                    box.ContainsX(unitCenterX) &&
                    box.ContainsY(unitFeetY))
                {
                    damage = massa * unitDyAs3 / 20f * damageAttribute * difficultyScale;
                    return damage > 0f;
                }

                return false;
            }

            if (triggerAttribute == TriggerRising)
            {
                // Trap.as:191 — the unit's HEAD inside the box, moving up. `Y1` in the oracle is the
                // unit's top edge; in this port's Y-up space that is the feet rect's yMax.
                if (unitDyAs3 + unitOsnDyAs3 < 0f &&
                    box.ContainsX(unitCenterX) &&
                    box.ContainsY(unitHeadY))
                {
                    damage = massa * damageAttribute * difficultyScale;
                    return damage > 0f;
                }

                return false;
            }

            // An unrecognised @att. AS3 would take neither branch and the trap would be inert; say so
            // rather than guessing an arm.
            return false;
        }
    }
}
