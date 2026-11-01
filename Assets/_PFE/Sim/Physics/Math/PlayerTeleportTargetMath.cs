using UnityEngine;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// Where the player's teleport lands, and which cell the destination marker covers — the whole of
    /// <c>UnitPlayer.actPort</c>'s targeting arithmetic (<c>fe/unit/UnitPlayer.as:1627-1685</c>,
    /// <c>checkPort</c> at <c>:1690-1706</c>).
    ///
    /// <para><b>The oracle, verbatim.</b> Both the landing point and the marker's anchor come from one
    /// snap:</para>
    /// <code>
    /// _loc1_ = Math.round(World.w.celX / World.tileX) * World.tileX;              // :1641
    /// _loc2_ = Math.round(World.w.celY / World.tileY + 1) * World.tileY - 1;      // :1642
    /// </code>
    /// <para><c>_loc2_</c> expands to <c>round(celY / tileY) * tileY + tileY - 1</c>: AS3's Y grows
    /// <i>downward</i>, so the landing cell is row <c>round(celY / tileY)</c> and the feet sit one pixel
    /// above that cell's bottom seam (<c>:1642</c>). The port's feet coordinate grows <i>upward</i>
    /// (<c>WorldCoordinates</c>, <c>TilePhysicsController.SyncUnityPosition</c>), so the same point is
    /// the destination cell's <b>bottom + 1 px</b>, not its top − 1 px.</para>
    ///
    /// <para><b>Why the mirror is not cosmetic.</b> Copying the oracle's expression into the port's
    /// y-up space evaluates to <c>round(y / tile) * tile + tile - 1</c> — one whole cell <i>above</i>
    /// the cursor's cell, measured upward. Every indoor destination therefore landed in the ceiling,
    /// <c>CanTeleportTo</c> refused it, and teleporting appeared to do nothing in any room with a roof
    /// while still "working" outdoors, where the sky is open. The two expressions differ by
    /// <c>2 * tile - 2</c> px (78 px at the oracle's 40 px tile) — about two cells.</para>
    ///
    /// <para><b>The cell the marker covers.</b> <c>GUI.showPortCel</c> (<c>GUI.as:1531-1552</c>) draws
    /// the clip at the same snapped point, so the highlighted cell is the one whose bottom seam the feet
    /// stand on — <see cref="Target.CellY"/> — and its left seam is <see cref="Target.CellX"/>.</para>
    ///
    /// <para><b>Unity-free</b> apart from <c>Vector2</c>/<c>Rect</c>, so the mirror and the seam can be
    /// pinned by an offline fixture without a room, a motor or an engine.</para>
    /// </summary>
    public static class PlayerTeleportTargetMath
    {
        /// <summary>
        /// The oracle's <c>- 1</c> (<c>:1642</c>): the feet stand one pixel off the seam, inside the
        /// destination cell. Mirrored to a y-up space that is <c>+ 1</c> above the cell's bottom.
        /// </summary>
        public const float SeamInsetPixels = 1f;

        /// <summary><c>Pers.portTime</c> (<c>Pers.as:433</c>) — frames of charge before release fires.</summary>
        public const int PortTimeFrames = 25;

        /// <summary>
        /// <c>GUI.as:1977</c> — <c>if(this.gg.t_port &gt; 15) showPortCel();</c>. The destination square
        /// is hidden for the first 15 frames of a 25-frame charge and shown for the last ten.
        /// </summary>
        public const int MarkerFirstFrame = 15;

        /// <summary>
        /// <see cref="MarkerFirstFrame"/> as a fraction of <see cref="PortTimeFrames"/> (0.6).
        ///
        /// <para><b>A fraction, not a frame count, because the port's charge is not the oracle's.</b>
        /// The serialized <c>teleportChargeTimeSeconds</c> is 0.42 s (player prefab), while 25 frames at
        /// the oracle's 30 fps is 0.833 s. Thresholding on "15 frames" at the port's rate would put the
        /// marker past the end of the charge and it would never appear at all; scaling by the ratio the
        /// oracle actually uses keeps the marker's <i>share</i> of the charge correct whatever the
        /// serialized duration is. The 0.42-vs-0.833 discrepancy is a separate, pre-existing defect —
        /// see <c>docs/Movement/TeleportDestinationSquare_2026-10-31.md</c> §4.</para>
        /// </summary>
        public const float MarkerVisibleFromChargeFraction = (float)MarkerFirstFrame / PortTimeFrames;

        /// <summary>
        /// A resolved teleport destination: where the feet go, and the cell the marker covers.
        /// </summary>
        public readonly struct Target
        {
            /// <summary>Feet position, room-local pixel X — the body is centred on it.</summary>
            public readonly float FeetPixelX;

            /// <summary>Feet position, room-local pixel Y (y-up) — the body's bottom edge.</summary>
            public readonly float FeetPixelY;

            /// <summary>Destination cell column (its left seam is <c>CellX * tileSize</c>).</summary>
            public readonly int CellX;

            /// <summary>Destination cell row (its bottom seam is <c>CellY * tileSize</c>).</summary>
            public readonly int CellY;

            public Target(float feetPixelX, float feetPixelY, int cellX, int cellY)
            {
                FeetPixelX = feetPixelX;
                FeetPixelY = feetPixelY;
                CellX = cellX;
                CellY = cellY;
            }

            /// <summary>The destination cell in pixel space, y-up — what the marker draws over.</summary>
            public Rect CellRectPixels(float tileSize)
                => new Rect(CellX * tileSize, CellY * tileSize, tileSize, tileSize);

            /// <summary>Centre of <see cref="CellRectPixels"/>, for a centre-pivoted sprite.</summary>
            public Vector2 CellCentrePixels(float tileSize)
                => new Vector2((CellX + 0.5f) * tileSize, (CellY + 0.5f) * tileSize);
        }

        /// <summary>
        /// Resolve the destination for a cursor position in room-local pixels.
        ///
        /// <para><b>Both snaps round, and neither clamps.</b> The oracle rounds the cursor's cell index
        /// (<c>Math.round</c>, half up) and does not test the result against the room bounds — an
        /// out-of-room cell is caught later by <c>loc.collisionUnit</c>, which returns <c>false</c> for
        /// cells outside <c>spaceX/spaceY</c> (<c>Location.as:2354</c>), i.e. it reads as "clear" and the
        /// teleport is allowed. That is the oracle's behaviour and it is deliberately preserved here; the
        /// bound check belongs to the caller's room query, not to the snap.</para>
        /// </summary>
        public static Target Resolve(Vector2 cursorPixels, float tileSize)
        {
            if (tileSize <= 0f)
            {
                return default;
            }

            // AS3's Math.round is floor(x + 0.5) — half always goes UP, including for negatives.
            // Mathf.RoundToInt is banker's rounding, so it disagrees on every exact half (the cursor
            // sitting on a cell seam, which is precisely where a player aiming at a floor tile puts it).
            // The project has already paid for this difference once — see the crafting repair sort.
            int cellX = As3Round(cursorPixels.x / tileSize);
            int cellY = As3Round(cursorPixels.y / tileSize) - 1;

            return new Target(
                feetPixelX: cellX * tileSize,
                feetPixelY: cellY * tileSize + SeamInsetPixels,
                cellX: cellX,
                cellY: cellY);
        }

        /// <summary>
        /// <c>Math.round</c> as AS3 defines it: <c>floor(x + 0.5)</c>. Kept here rather than using
        /// <c>Mathf.RoundToInt</c> because that one rounds halves to even.
        /// </summary>
        public static int As3Round(float value) => Mathf.FloorToInt(value + 0.5f);

        /// <summary>
        /// Whether the marker should be on screen for a charge of <paramref name="chargedSeconds"/>
        /// against <paramref name="chargeSeconds"/> (<c>GUI.as:1977</c>).
        /// </summary>
        public static bool ShouldShowMarker(float chargedSeconds, float chargeSeconds)
        {
            if (chargedSeconds <= 0f || chargeSeconds <= 0f)
            {
                return false;
            }

            return chargedSeconds >= chargeSeconds * MarkerVisibleFromChargeFraction;
        }

        /// <summary>
        /// Which of the clip's three frames the marker shows (<c>GUI.as:1536-1551</c>):
        /// 1 = charging, 2 = ready, 3 = the destination is refused.
        /// </summary>
        public static int ResolveMarkerFrame(bool destinationAllowed, bool chargeComplete)
        {
            if (!destinationAllowed)
            {
                return 3;
            }

            return chargeComplete ? 2 : 1;
        }
    }
}
