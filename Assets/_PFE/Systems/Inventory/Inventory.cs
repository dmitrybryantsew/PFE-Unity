using System;
using System.Collections.Generic;
using System.Linq;
using R3;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// Runtime inventory class for item management
    /// Provides type-safe item lookup, capacity management, and reactive properties
    /// </summary>
    public class Inventory
    {
        // Inventory configuration
        private readonly int _maxSlots;
        private readonly float _maxWeight;

        // Item storage: itemId -> InventoryItem
        private readonly Dictionary<string, InventoryItem> _items;

        // Reactive properties
        public ReactiveProperty<int> ItemCount { get; private set; }
        public ReactiveProperty<float> TotalWeight { get; private set; }
        public ReactiveProperty<int> UsedSlots { get; private set; }

        // Events for item changes
        public Observable<ItemChangedEventArgs> ItemAdded => _itemAdded;
        public Observable<ItemChangedEventArgs> ItemRemoved => _itemRemoved;
        public Observable<ItemChangedEventArgs> ItemChanged => _itemChanged;

        private readonly Subject<ItemChangedEventArgs> _itemAdded = new();
        private readonly Subject<ItemChangedEventArgs> _itemRemoved = new();
        private readonly Subject<ItemChangedEventArgs> _itemChanged = new();

        /// <summary>
        /// Create a new inventory
        /// </summary>
        /// <param name="maxSlots">Maximum number of unique item slots</param>
        /// <param name="maxWeight">Maximum weight capacity (0 = unlimited)</param>
        public Inventory(int maxSlots = 20, float maxWeight = 100f)
        {
            if (maxSlots <= 0)
            {
                throw new ArgumentException("maxSlots must be greater than 0", nameof(maxSlots));
            }

            if (maxWeight < 0)
            {
                throw new ArgumentException("maxWeight cannot be negative", nameof(maxWeight));
            }

            _maxSlots = maxSlots;
            _maxWeight = maxWeight;
            _items = new Dictionary<string, InventoryItem>();

            ItemCount = new ReactiveProperty<int>(0);
            TotalWeight = new ReactiveProperty<float>(0);
            UsedSlots = new ReactiveProperty<int>(0);
        }

        #region Public API

        /// <summary>
        /// Add an item to the inventory
        /// </summary>
        /// <param name="itemDefinition">Item definition to add</param>
        /// <param name="quantity">Quantity to add</param>
        /// <returns>Actual quantity added (may be less due to capacity/stacking limits)</returns>
        public int AddItem(ItemDefinition itemDefinition, int quantity = 1)
        {
            if (itemDefinition == null)
            {
                Debug.LogWarning("[Inventory] Cannot add null item");
                return 0;
            }

            if (quantity <= 0)
            {
                Debug.LogWarning($"[Inventory] Invalid quantity: {quantity}");
                return 0;
            }

            // Check if already have this item
            if (_items.TryGetValue(itemDefinition.itemId, out var existingItem))
            {
                return AddToExistingStack(existingItem, itemDefinition, quantity);
            }
            else
            {
                return AddNewItem(itemDefinition, quantity);
            }
        }

        /// <summary>
        /// Remove an item from the inventory
        /// </summary>
        /// <param name="itemId">Item ID to remove</param>
        /// <param name="quantity">Quantity to remove</param>
        /// <returns>Actual quantity removed (may be less if insufficient quantity)</returns>
        public int RemoveItem(string itemId, int quantity = 1)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                Debug.LogWarning("[Inventory] Invalid itemId");
                return 0;
            }

            if (quantity <= 0)
            {
                Debug.LogWarning($"[Inventory] Invalid quantity: {quantity}");
                return 0;
            }

            if (!_items.TryGetValue(itemId, out var item))
            {
                Debug.LogWarning($"[Inventory] Item not found: {itemId}");
                return 0;
            }

            int actualQuantity = Math.Min(quantity, item.Quantity);

            if (actualQuantity >= item.Quantity)
            {
                // Remove entire stack
                _items.Remove(itemId);
                UpdateStatistics();
                _itemRemoved.OnNext(new ItemChangedEventArgs
                {
                    ItemDefinition = item.ItemDefinition,
                    Quantity = actualQuantity,
                    CurrentQuantity = 0
                });
            }
            else
            {
                // Reduce stack
                item.Quantity -= actualQuantity;
                UpdateStatistics();
                _itemChanged.OnNext(new ItemChangedEventArgs
                {
                    ItemDefinition = item.ItemDefinition,
                    Quantity = actualQuantity,
                    CurrentQuantity = item.Quantity
                });
            }

            return actualQuantity;
        }

        /// <summary>
        /// Get an inventory item by ID
        /// </summary>
        public InventoryItem GetItem(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId) || !_items.TryGetValue(itemId, out var item))
            {
                return null;
            }
            return item;
        }

        /// <summary>
        /// Get an inventory item by type
        /// </summary>
        public InventoryItem GetItem<T>(string itemId) where T : ItemDefinition
        {
            var item = GetItem(itemId);
            if (item?.ItemDefinition is T typedData)
            {
                return item;
            }
            return null;
        }

        /// <summary>
        /// Check if inventory contains an item
        /// </summary>
        public bool HasItem(string itemId)
        {
            return !string.IsNullOrWhiteSpace(itemId) && _items.ContainsKey(itemId);
        }

        /// <summary>
        /// Check if inventory has at least the specified quantity of an item
        /// </summary>
        public bool HasItem(string itemId, int quantity)
        {
            if (!HasItem(itemId))
            {
                return false;
            }
            return _items[itemId].Quantity >= quantity;
        }

        /// <summary>
        /// Get the quantity of an item in inventory
        /// </summary>
        public int GetQuantity(string itemId)
        {
            return HasItem(itemId) ? _items[itemId].Quantity : 0;
        }

        /// <summary>
        /// Get all items in inventory
        /// </summary>
        public IReadOnlyCollection<InventoryItem> GetAllItems()
        {
            return _items.Values;
        }

        /// <summary>
        /// Get all items of a specific type
        /// </summary>
        public IEnumerable<InventoryItem> GetItemsOfType<T>() where T : ItemDefinition
        {
            return _items.Values.Where(item => item.ItemDefinition is T);
        }

        /// <summary>
        /// Get all items of a specific item type enum
        /// </summary>
        public IEnumerable<InventoryItem> GetItemsByType(ItemType itemType)
        {
            return _items.Values.Where(item => item.ItemDefinition.type == itemType);
        }

        /// <summary>
        /// Clear all items from inventory
        /// </summary>
        public void Clear()
        {
            _items.Clear();
            UpdateStatistics();
        }

        /// <summary>
        /// Check if inventory can accept an item
        /// </summary>
        public bool CanAddItem(ItemDefinition itemDefinition, int quantity = 1)
        {
            if (itemDefinition == null || quantity <= 0)
            {
                return false;
            }

            // Check weight capacity
            if (_maxWeight > 0)
            {
                float addedWeight = itemDefinition.weight * quantity;
                if (TotalWeight.Value + addedWeight > _maxWeight)
                {
                    return false;
                }
            }

            // Check slot capacity
            if (HasItem(itemDefinition.itemId))
            {
                // Item exists - check if stack can accept quantity
                var existing = _items[itemDefinition.itemId];
                if (itemDefinition.stackSize > 1)
                {
                    return existing.Quantity + quantity <= itemDefinition.stackSize;
                }
                else
                {
                    return false; // Non-stackable item already exists
                }
            }
            else
            {
                // New item - check if slots available
                return UsedSlots.Value < _maxSlots;
            }
        }

        #endregion

        #region Private Methods

        private int AddToExistingStack(InventoryItem existingItem, ItemDefinition itemDefinition, int quantity)
        {
            if (itemDefinition.stackSize <= 1)
            {
                Debug.LogWarning($"[Inventory] Cannot add non-stackable item {itemDefinition.itemId} - already exists");
                return 0;
            }

            int spaceInStack = itemDefinition.stackSize - existingItem.Quantity;
            int actualQuantity = Math.Min(quantity, spaceInStack);

            if (actualQuantity > 0)
            {
                existingItem.Quantity += actualQuantity;
                UpdateStatistics();
                _itemChanged.OnNext(new ItemChangedEventArgs
                {
                    ItemDefinition = itemDefinition,
                    Quantity = actualQuantity,
                    CurrentQuantity = existingItem.Quantity
                });
            }
            else
            {
                Debug.LogWarning($"[Inventory] Cannot add {quantity} {itemDefinition.itemId} - stack full ({existingItem.Quantity}/{itemDefinition.stackSize})");
            }

            return actualQuantity;
        }

        private int AddNewItem(ItemDefinition itemDefinition, int quantity)
        {
            // Check slot availability
            if (UsedSlots.Value >= _maxSlots)
            {
                Debug.LogWarning($"[Inventory] Cannot add {itemDefinition.itemId} - inventory full ({UsedSlots.Value}/{_maxSlots} slots)");
                return 0;
            }

            // Check weight capacity
            if (_maxWeight > 0)
            {
                float addedWeight = itemDefinition.weight * quantity;
                if (TotalWeight.Value + addedWeight > _maxWeight)
                {
                    Debug.LogWarning($"[Inventory] Cannot add {quantity} {itemDefinition.itemId} - overweight ({TotalWeight.Value + addedWeight}/{_maxWeight})");
                    return 0;
                }
            }

            // Create new inventory item
            var newItem = new InventoryItem
            {
                ItemDefinition = itemDefinition,
                Quantity = itemDefinition.stackSize > 1 ? quantity : 1
            };

            _items[itemDefinition.itemId] = newItem;
            UpdateStatistics();
            _itemAdded.OnNext(new ItemChangedEventArgs
            {
                ItemDefinition = itemDefinition,
                Quantity = newItem.Quantity,
                CurrentQuantity = newItem.Quantity
            });

            return newItem.Quantity;
        }

        private void UpdateStatistics()
        {
            // Count total items (sum of all quantities)
            int totalCount = _items.Values.Sum(item => item.Quantity);
            ItemCount.Value = totalCount;

            // Calculate total weight
            float totalWeight = _items.Values.Sum(item => item.ItemDefinition.weight * item.Quantity);
            TotalWeight.Value = totalWeight;

            // Count used slots
            UsedSlots.Value = _items.Count;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Maximum number of unique item slots
        /// </summary>
        public int MaxSlots => _maxSlots;

        /// <summary>
        /// Maximum weight capacity (0 = unlimited)
        /// </summary>
        public float MaxWeight => _maxWeight;

        /// <summary>
        /// Available slots
        /// </summary>
        public int AvailableSlots => _maxSlots - UsedSlots.Value;

        /// <summary>
        /// Remaining weight capacity
        /// </summary>
        public float AvailableWeight => _maxWeight > 0 ? _maxWeight - TotalWeight.Value : float.MaxValue;

        #endregion
    }

    /// <summary>
    /// Represents an item instance in inventory
    /// </summary>
    public class InventoryItem
    {
        public ItemDefinition ItemDefinition { get; set; }
        public int Quantity { get; set; }

        /// <summary>
        /// Total weight of this stack
        /// </summary>
        public float TotalWeight => ItemDefinition?.weight * Quantity ?? 0f;
    }

    /// <summary>
    /// Event arguments for item changes
    /// </summary>
    public class ItemChangedEventArgs
    {
        public ItemDefinition ItemDefinition { get; set; }
        public int Quantity { get; set; } // Quantity that was added/removed/changed
        public int CurrentQuantity { get; set; } // Total quantity after the change
    }
}
