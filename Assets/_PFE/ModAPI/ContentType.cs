namespace PFE.ModAPI
{
    /// <summary>
    /// Categories of moddable game content.
    /// Each type gets its own registry namespace — IDs only need to be unique within a type.
    /// </summary>
    public enum ContentType
    {
        Unit,
        Weapon,
        Ammo,
        Item,
        Perk,
        Skill,
        Effect,
        RoomTemplate,
        CharacterAnimation,
        Tile,
        Campaign,
        Mission,
        Audio,
        MainMenuComposition,
        MapObjectDefinition,
    }
}
