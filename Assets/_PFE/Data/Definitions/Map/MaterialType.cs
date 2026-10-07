namespace PFE.Systems.Map
{
    /// <summary>
    /// Material types for tiles, affects destruction and sound.
    /// </summary>
    public enum MaterialType
    {
        /// <summary>
        /// Default material
        /// </summary>
        Default = 0,

        /// <summary>
        /// Metal - high durability, metallic sounds
        /// </summary>
        Metal = 1,

        /// <summary>
        /// Wood - medium durability, wooden sounds, flammable
        /// </summary>
        Wood = 2,

        /// <summary>
        /// Stone - very high durability, stone sounds
        /// </summary>
        Stone = 3,

        /// <summary>
        /// Glass - low durability, breaks easily
        /// </summary>
        Glass = 4
    }
}
