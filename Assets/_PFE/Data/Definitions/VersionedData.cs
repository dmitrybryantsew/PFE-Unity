using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Base class for all versioned ScriptableObject data.
    /// Provides common functionality for data validation, migration, and serialization.
    /// </summary>
    public abstract class VersionedData : ScriptableObject
    {
        [Header("Version Info")]
        [SerializeField]
        protected int dataVersion = 1;

        [SerializeField]
        protected string gameVersion = "0.1.0";

        /// <summary>
        /// Unique identifier for this data entry.
        /// Must be unique across all instances of this type.
        /// </summary>
        public abstract string DataId { get; }

        /// <summary>
        /// Display name for UI.
        /// </summary>
        public abstract string DisplayName { get; }

        /// <summary>
        /// Validate this data entry.
        /// Called by editor tools and at runtime in debug builds.
        /// </summary>
        public virtual bool Validate()
        {
            if (string.IsNullOrEmpty(DataId))
            {
                Debug.LogWarning($"{GetType().Name}: DataId is null or empty");
                return false;
            }

            return OnValidateData();
        }

        /// <summary>
        /// Override to implement custom validation logic.
        /// </summary>
        protected virtual bool OnValidateData()
        {
            return true;
        }

        /// <summary>
        /// Called when data is loaded or deserialized.
        /// Use for migrating old data formats.
        /// </summary>
        protected virtual void OnDataLoaded()
        {
            // Override in derived classes to handle data migration
        }

        /// <summary>
        /// Get all data references for validation.
        /// Override to return IDs of referenced data objects.
        /// </summary>
        public virtual string[] GetReferencedDataIds()
        {
            return System.Array.Empty<string>();
        }

        /// <summary>
        /// Reset data to default values.
        /// </summary>
        public virtual void ResetData()
        {
            dataVersion = 1;
            gameVersion = "0.1.0";
            OnResetData();
        }

        /// <summary>
        /// Override to implement custom reset logic.
        /// </summary>
        protected virtual void OnResetData()
        {
            // Override in derived classes
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only validation.
        /// Called by Unity editor when script is reloaded or values change.
        /// </summary>
        protected virtual void OnValidate()
        {
            // Auto-increment version on save in editor
            if (!string.IsNullOrEmpty(DataId))
            {
                // Ensure data version is set
                if (dataVersion == 0)
                    dataVersion = 1;
            }
        }
#endif
    }
}
