namespace PFE.Systems.Audio
{
    /// <summary>
    /// Surface material type used for impact sound selection.
    /// Maps from TileData.MaterialType and IDamageable implementations.
    ///
    /// AS3 equivalent: the mat property on Tile and the return value of
    /// Unit.udarBullet() / Box.udarBullet() fed into Bullet.sound().
    /// </summary>
    public enum SurfaceMaterial
    {
        /// <summary>No material — silent impact (triggers only weapon soundHit).</summary>
        Default   = 0,

        /// <summary>Metal walls, robots, armoured objects. AS3 mat=1 → "hit_metal".</summary>
        Metal     = 1,

        /// <summary>Concrete/stone walls. AS3 mat=2,4,6 → "hit_concrete".</summary>
        Concrete  = 2,

        /// <summary>Wooden floors, crates. AS3 mat=3 → "hit_wood".</summary>
        Wood      = 3,

        /// <summary>Glass panes. AS3 mat=5 → "hit_glass".</summary>
        Glass     = 4,

        /// <summary>Pole/pipe surfaces. AS3 mat=7 → "hit_pole".</summary>
        Pole      = 5,

        /// <summary>Living unit — sound depends on DamageType. AS3 mat=10.</summary>
        Flesh     = 6,

        /// <summary>Water surface. AS3 mat=11 → "hit_water".</summary>
        Water     = 7,

        /// <summary>Slime/acid surfaces. AS3 mat=12 → "hit_slime".</summary>
        Slime     = 8,

        /// <summary>Necrotic/undead surfaces. → "hit_necr".</summary>
        Necrotic  = 9,
    }
}
