using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PFE.Systems.RPG.Data
{
    /// <summary>
    /// Serializable dictionary for Unity.
    /// Enables serialization of dictionaries in Inspector and for save/load.
    /// Required for storing skill levels and perk ranks in CharacterStats.
    /// </summary>
    [System.Serializable]
    public class SerializableDictionary<TKey, TValue> : ISerializationCallbackReceiver
    {
        [SerializeField] private List<TKey> keys = new List<TKey>();
        [SerializeField] private List<TValue> values = new List<TValue>();

        private Dictionary<TKey, TValue> dictionary = new Dictionary<TKey, TValue>();

        /// <summary>
        /// Access the underlying dictionary.
        /// </summary>
        public Dictionary<TKey, TValue> Dictionary => dictionary;

        /// <summary>
        /// Get or set a value by key.
        /// </summary>
        public TValue this[TKey key]
        {
            get => dictionary[key];
            set => dictionary[key] = value;
        }

        /// <summary>
        /// Get all keys.
        /// </summary>
        public Dictionary<TKey, TValue>.KeyCollection Keys => dictionary.Keys;

        /// <summary>
        /// Get all values.
        /// </summary>
        public Dictionary<TKey, TValue>.ValueCollection Values => dictionary.Values;

        /// <summary>
        /// Get the number of items.
        /// </summary>
        public int Count => dictionary.Count;

        /// <summary>
        /// Check if the dictionary contains a key.
        /// </summary>
        public bool ContainsKey(TKey key) => dictionary.ContainsKey(key);

        /// <summary>
        /// Try to get a value by key.
        /// </summary>
        public bool TryGetValue(TKey key, out TValue value) => dictionary.TryGetValue(key, out value);

        /// <summary>
        /// Add a key-value pair.
        /// </summary>
        public void Add(TKey key, TValue value) => dictionary.Add(key, value);

        /// <summary>
        /// Remove a key-value pair.
        /// </summary>
        public bool Remove(TKey key) => dictionary.Remove(key);

        /// <summary>
        /// Clear all items.
        /// </summary>
        public void Clear() => dictionary.Clear();

        /// <summary>
        /// Copy constructor.
        /// </summary>
        public SerializableDictionary()
        {
        }

        /// <summary>
        /// Copy constructor from another dictionary.
        /// </summary>
        public SerializableDictionary(IDictionary<TKey, TValue> dict)
        {
            dictionary = new Dictionary<TKey, TValue>(dict);
        }

        /// <summary>
        /// Convert to a new Dictionary.
        /// </summary>
        public Dictionary<TKey, TValue> ToDictionary()
        {
            return new Dictionary<TKey, TValue>(dictionary);
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            keys.Clear();
            values.Clear();

            foreach (var kvp in dictionary)
            {
                keys.Add(kvp.Key);
                values.Add(kvp.Value);
            }
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            dictionary.Clear();

            for (int i = 0; i < Mathf.Min(keys.Count, values.Count); i++)
            {
                try
                {
                    dictionary[keys[i]] = values[i];
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to deserialize dictionary entry at index {i}: {ex.Message}");
                }
            }
        }
    }
}
