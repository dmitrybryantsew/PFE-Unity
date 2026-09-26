using System;
using System.Collections.Generic;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// Serializable snapshot of a player's GameInventory.
    /// Captures weapons, armors, items, favorite slots, category mass, and equipped item IDs.
    /// </summary>
    [Serializable]
    public class GameInventorySaveData
    {
        public List<GameWeaponSaveData> weapons = new List<GameWeaponSaveData>();
        public List<GameArmorSaveData> armors = new List<GameArmorSaveData>();
        public List<GameItemSaveData> items = new List<GameItemSaveData>();
        public List<FavoriteSlotSaveEntry> favoriteSlots = new List<FavoriteSlotSaveEntry>();
        public float[] categoryMass = new float[4];
        public string currentWeaponId = "";
        public string currentArmorId = "";

        [Serializable]
        public class FavoriteSlotSaveEntry
        {
            public string itemId;
            public int slot;
        }
    }
}
