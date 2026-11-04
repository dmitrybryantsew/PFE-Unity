using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The single source of truth for where a tile GameObject sits, how deep it sorts, and how tall it
    /// draws — the three numbers every tile-rendering path must agree on.
    ///
    /// <para><b>Why this class exists.</b> These formulas used to be written down independently in
    /// <c>TileRenderer.GetLocalPosition</c>, <c>TileCollisionBuilder.LocalPositionFor</c>,
    /// <c>TileRenderer.GetHeightScale</c>, <c>TileVisualManager.ResolveVisualData</c>,
    /// <c>TileData.HeightScale</c> and <c>RoomTerrainGeometry.HeightScale</c>. They are the same
    /// arithmetic, and the A/B contract between the shipped path and the quarter renderer is precisely
    /// that they never drift: two copies that disagree do not read as "the physics moved" or "the
    /// picture moved", they read as "the player floats" next to a perfect-looking room.</para>
    ///
    /// <para><b>What is pinned where.</b> The existing call sites keep their public API and delegate
    /// here, so the tests that pin them (<c>TileCollisionBuilderTests.LocalPositionFor_*</c>,
    /// <c>RoomTerrainWiringTests.CellSize_MatchesTheShippedTileSize</c>,
    /// <c>RoomTerrainGeometryTests.HeightScale_And_Drop_MatchTheShippedRenderer</c>) now pin this
    /// class through them. New code should call this class directly rather than add a fourth copy.</para>
    ///
    /// <para><b>The one formula that changed.</b> <see cref="HeightScale(TileData)"/> clamps
    /// <c>heightLevel</c> to <c>[0, 3]</c> before scaling. The two copies in the shipped sprite path
    /// (<c>TileRenderer.GetHeightScale</c>, <c>TileVisualManager.ResolveVisualData</c>) did not clamp,
    /// so an out-of-range <c>heightLevel</c> produced a negative scale there while
    /// <c>TileData.HeightScale</c> and the quarter renderer clamped. In-range values — every shipped
    /// tile — are identical either way, so this unifies on the clamped oracle answer.</para>
    /// </summary>
    public static class TileLayoutMath
    {
        /// <summary>One tile in world units: <c>TILE_SIZE</c> px at this project's 100 px/unit import.</summary>
        /// <remarks>
        /// This is the same value as <c>RoomTerrainGeometry.DefaultCellSize</c> (0.4) and
        /// <c>RoomTerrainWiring.ShippedCellSize</c>. Those stay as the quarter renderer's own names for
        /// it; this is the name the tile-object paths use. The cross-check that they can never drift is
        /// <c>RoomTerrainWiringTests.CellSize_MatchesTheShippedTileSize</c>.
        /// </remarks>
        public static float CellSizeUnits => WorldConstants.TILE_SIZE / 100f;

        /// <summary>
        /// Depth per grid row: a tile one row up sits this much further forward. It does nothing to a
        /// 2D collider but keeps a collider-only tile indistinguishable from a drawn one in the
        /// hierarchy and in any tool that sorts by z.
        /// </summary>
        public const float DepthPerRow = 0.01f;

        /// <summary>
        /// Room-local position of a tile object: the centre of <see cref="TileData.GetBounds"/> — the
        /// tile's <i>solid</i> rect, which a <c>heightLevel &gt; 0</c> tile shortens from the top — with
        /// z set to the row's depth order.
        ///
        /// <para><b>Why the solid-rect centre and not the cell centre.</b> The collider is built with
        /// no offset from the object at this position, so the two must agree: this rect's centre and
        /// this rect's size are the whole geometry. A collider half a cell off would not read as "the
        /// physics moved", it would read as "the player stands on air", and the picture would look
        /// perfect.</para>
        /// </summary>
        public static Vector3 LocalPosition(TileData tile)
        {
            if (tile == null) return Vector3.zero;

            Vector3 unityPos = WorldCoordinates.PixelToUnity(tile.GetBounds().center);
            unityPos.z = tile.gridPosition.y * DepthPerRow;
            return unityPos;
        }

        /// <summary>
        /// Vertical squash for a lowered ceiling: <c>heightLevel</c> 0 keeps the full tile, 3 keeps a
        /// quarter. This is <see cref="TileData.HeightScale"/> — the oracle's answer — with the
        /// <c>heightLevel</c> clamp applied, matching <c>RoomTerrainGeometry.HeightScale</c> exactly.
        /// A height difference between the two sides of the A/B window is a bug, not a design choice.
        /// </summary>
        public static float HeightScale(TileData tile)
        {
            if (tile == null) return 1f;
            return HeightScale(tile.heightLevel);
        }

        /// <summary>
        /// Vertical squash for a lowered ceiling by level. Clamps <paramref name="heightLevel"/> to
        /// <c>[0, 3]</c> before scaling — see the class summary for why this is the clamped form.
        /// </summary>
        public static float HeightScale(int heightLevel)
        {
            return Mathf.Clamp01(1f - Mathf.Clamp(heightLevel, 0, 3) * TileData.HeightStepFraction);
        }
    }
}
