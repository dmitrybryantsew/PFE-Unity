using PFE.Systems.Map.TileQuery;

namespace PFE.Entities.Units
{
    /// <summary>
    /// The unit body's size, expressed in the two conventions the port keeps it in — and the one
    /// conversion between them, in a place a test can reach.
    ///
    /// <para><b>Why this type exists.</b> A unit's collision box is authored once (AS3
    /// <c>&lt;phis sX='55' sY='70'&gt;</c>) but consumed in two different spaces:</para>
    ///
    /// <list type="bullet">
    /// <item><c>UnitDefinition.Width</c>/<c>Height</c> hold <b>world units</b> — <c>0.55</c>/<c>0.70</c>.
    /// That is what <c>RoomUnitSpawner</c> hands <c>BoxCollider2D.size</c>, and what the collider debug
    /// overlay draws. <c>XMLConverter</c> stores <c>sX / 100f</c> and the field's own tooltip says
    /// "55px = 0.55 units", with a <c>[Range(0.1f, 2f)]</c> that would silently clamp a pixel value to
    /// 2 if anyone tried to "fix" it in the Inspector.</item>
    /// <item><c>TilePhysicsController.collisionWidth</c>/<c>collisionHeight</c> hold <b>pixels</b> —
    /// <c>55</c>/<c>70</c>. They go straight into <c>TileCollisionMath.CheckTileCollisionAt</c>, which
    /// divides by <c>WorldConstants.TILE_SIZE</c> to reach tile coordinates.</item>
    /// </list>
    ///
    /// <para><b>The failure this prevents is silent and 100x.</b> Feeding the world value straight into
    /// the motor gave every spawned unit a collision box of <c>0.55 x 0.70</c> <i>pixels</i> instead of
    /// <c>55 x 70</c>. Nothing throws: the motor simply resolves every wall, ceiling and ground test
    /// against a box a hundred times too small, so the unit ignores walls it should hit, never detects a
    /// ceiling, and finds ground only under its exact centre column. Meanwhile the <c>BoxCollider2D</c>
    /// — and therefore the box the F6 overlay draws — is the correct size, so the picture and the
    /// physics disagree while both look self-consistent.</para>
    /// </summary>
    public static class UnitCollisionBoxMath
    {
        /// <summary>
        /// A unit dimension from <see cref="PFE.Data.Definitions.UnitDefinition"/>'s world units to the
        /// pixels the tile motor works in.
        ///
        /// <para>A non-positive input maps to <c>0</c> rather than to a negative box, which is the
        /// sentinel <c>TilePhysicsController.ConfigureCollisionSize</c> already treats as "the
        /// definition did not author a size, keep the serialized default". Returning the raw product
        /// would turn that case into a negative collision box.</para>
        /// </summary>
        public static float PixelsFromWorldUnits(float worldUnits)
        {
            if (worldUnits <= 0f)
            {
                return 0f;
            }

            return worldUnits / TileQueryConstants.PixelToUnit;
        }
    }
}
