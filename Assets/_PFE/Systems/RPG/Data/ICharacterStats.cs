namespace PFE.Systems.RPG.Data
{
    /// <summary>
    /// Interface for character stats to avoid circular dependencies.
    /// Used by PerkDefinition to check requirements without depending on CharacterStats.
    /// </summary>
    public interface ICharacterStats
    {
        int Level { get; }
        int GetSkillLevel(string skillId);
        int GetPerkRank(string perkId);
    }
}
