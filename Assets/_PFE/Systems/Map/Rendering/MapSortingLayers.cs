namespace PFE.Systems.Map.Rendering
{
    public static class MapSortingLayers
    {
        public const string Backwall = "Backwall";
        public const string BackgroundTiles = "BackgroundTiles";
        public const string BackgroundDecor = "BackgroundDecor";
        public const string MainTiles = "MainTiles";
        public const string BackgroundObject = "BackgroundObject";
        public const string BackgroundPhysicalObjects = "BackgroundPhysicalObjects";
        // Water renders in front of entities so submerged units appear behind it.
        public const string Water = "Water";
        public const string Foreground = "Foreground";
    }
}
