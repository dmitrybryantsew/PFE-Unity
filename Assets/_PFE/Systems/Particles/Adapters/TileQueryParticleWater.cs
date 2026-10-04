using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Systems.Particles.Adapters
{
    /// <summary>
    /// Answers <see cref="IParticleTileWater"/> from the room's tile query — the Unity-side adapter that
    /// lets <c>ParticleWorld</c> stay Unity-free while still honouring the <c>water=</c> lifetime rule.
    ///
    /// <para><b>Why this lives in <c>Adapters/</c> and not beside <c>ParticleWorld</c>.</b>
    /// <c>Systems/Particles/</c> is Unity-free by discipline — that is what makes the 138 offline particle
    /// fixtures possible (lesson #43). This file touches <c>UnityEngine.Vector2Int</c> and
    /// <c>ITileQueryService</c>, so putting it in that folder would quietly break the invariant for
    /// everyone who later assumes it holds. <c>Rendering/</c> is the wrong home for the same reason in
    /// reverse: this is not a renderer.</para>
    ///
    /// <para><b>Coordinate space — the part that is easy to get wrong, and was.</b>
    /// <c>ParticleState.X/Y</c> are <b>AS3 room-local pixels</b> — the space <c>Emitter.emit</c> is
    /// called in, and the space <c>ParticleRules</c> integrates in (its gravity is AS3's
    /// <c>Y += ddy</c>, i.e. <b>downward</b>). <see cref="ITileQueryService.Classify"/> takes a
    /// <b>port</b> tile coordinate, whose rows run the other way: AS3 row 0 is the ceiling and port
    /// row 0 is the floor. So the two axes are <i>not</i> converted the same way:</para>
    /// <list type="bullet">
    /// <item><description><b>X</b> is a bare divide by <see cref="WorldConstants.TILE_SIZE"/> —
    /// both spaces run left-to-right.</description></item>
    /// <item><description><b>Y</b> is a divide <i>and then a mirror about the room height</i>
    /// (<see cref="WorldCoordinates.As3YToUnityRow"/>). A plain floor agrees with the mirror
    /// everywhere except exactly on a tile boundary, where it names the row above — the
    /// single-cell error <c>ThrownObject.CellAt</c> documents as a real defect. This adapter
    /// shipped with the plain floor and was corrected on review.</description></item>
    /// </list>
    /// <para>Neither axis takes an <see cref="ITileQueryService.OriginPixel"/> term: both sides are
    /// room-local, and adding the origin would be the classic bug this project has already paid for
    /// once — it is zero for a room at the origin, so a room-relative mistake looks perfect in every
    /// test built on room 0 and breaks one land step away. The origin belongs to the
    /// world↔room-local step, which is the <i>caller's</i> job, not this one's.</para>
    ///
    /// <para><b>The service is pushed, not injected, and that is deliberate.</b>
    /// <see cref="ITileQueryService"/> is not a container registration — every other consumer receives it
    /// through <c>SetTileQuery</c> / <c>Construct</c> when the room builds (units, the telekinesis
    /// controller, the projectile seam). Water is a property of <i>the current room</i>, so a container
    /// singleton holding one would be wrong the moment the player changes rooms. Until a room pushes a
    /// service the answer is <b>dry</b>, which is exactly <see cref="DryParticleTileWater"/>'s honest
    /// answer rather than an arbitrary one.</para>
    /// </summary>
    public sealed class TileQueryParticleWater : IParticleTileWater
    {
        private ITileQueryService _tiles;

        /// <summary>True once a room has pushed a tile service. The readback for "water effects never appear".</summary>
        public bool HasService => _tiles != null;

        /// <summary>
        /// Hands the adapter the current room's tile service. Called by the room when it builds; pass null
        /// on teardown so a stale room cannot answer for the next one.
        /// </summary>
        public void SetTileQuery(ITileQueryService tiles) => _tiles = tiles;

        /// <inheritdoc />
        public int WaterAt(float x, float y)
        {
            // No room yet — dry, not an error. Only 6 of the 118 rows read this at all.
            if (_tiles == null) return 0;

            RoomInstance room = _tiles.Room;
            if (room == null) return 0;

            var coord = new Vector2Int(
                Mathf.FloorToInt(x / WorldConstants.TILE_SIZE),
                WorldCoordinates.As3YToUnityRow(y, room.height));

            // The oracle's `tile.water` is a boolean in AS3 and the rows only test it for zero/non-zero
            // (Part.as:192-199), so a bool mapped to 1/0 is the whole contract. `Classify` is the seam
            // that already reads `TileData.hasWater` (UnifiedTileQueryService.cs:91). An out-of-range
            // row resolves to no tile and therefore to dry, so a part above the ceiling reads dry
            // rather than throwing.
            return (_tiles.Classify(coord) & TileQueryFlags.Water) != 0 ? 1 : 0;
        }
    }
}
