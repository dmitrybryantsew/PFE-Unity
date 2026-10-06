using PFE.Data.Definitions;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// Pure simulation contract for item statistics.
    /// Decouples inventory rules and queries from Unity ScriptableObject ItemDefinition.
    /// </summary>
    public interface IItemStats
    {
        string itemId { get; }
        ItemType type { get; }

        string ItemId => itemId;
        ItemType Type => type;
    }

    /// <summary>
    /// Pure C# data container for item stats in simulation and tests.
    /// </summary>
    public class ItemSpec : IItemStats
    {
        public string itemId { get; set; } = string.Empty;
        public ItemType type { get; set; } = ItemType.Misc;
    }
}
