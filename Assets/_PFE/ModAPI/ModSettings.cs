using System;
using System.Collections.Generic;

namespace PFE.ModAPI
{
    /// <summary>
    /// Value kind of a single mod setting. The host renders a matching control for each kind.
    /// </summary>
    public enum ModSettingType
    {
        Int,
        Float,
        Bool,
        Choice,
        String
    }

    /// <summary>
    /// Self-describing setting declared by a mod. Values are persisted by the host as
    /// invariant-culture strings keyed by <see cref="Key"/>; the mod reads them back through
    /// <see cref="IModSettings"/> at the moment it needs them (e.g. when building a land).
    /// </summary>
    [Serializable]
    public sealed class ModSettingDescriptor
    {
        /// <summary>Stable key the value is stored under. Never show to players.</summary>
        public string Key;

        /// <summary>Human-readable label shown in the host's mod settings panel.</summary>
        public string DisplayName;

        /// <summary>Optional longer explanation shown under the label.</summary>
        public string Description;

        public ModSettingType Type = ModSettingType.String;

        /// <summary>Invariant-culture default ("5", "true", "1.5", option id, raw string).</summary>
        public string DefaultValue = "";

        /// <summary>Lower bound for Int/Float settings.</summary>
        public float MinValue;

        /// <summary>Upper bound for Int/Float settings.</summary>
        public float MaxValue;

        /// <summary>Allowed values for Choice settings (first is the implicit default if DefaultValue is empty).</summary>
        public string[] Options = Array.Empty<string>();
    }

    /// <summary>
    /// Read access to one mod's own persisted settings, handed to the mod by the host.
    /// Live: values changed in the host UI are visible on the next read without re-registration.
    /// </summary>
    public interface IModSettings
    {
        int GetInt(string key, int fallback = 0);
        float GetFloat(string key, float fallback = 0f);
        bool GetBool(string key, bool fallback = false);
        string GetString(string key, string fallback = "");
    }

    /// <summary>
    /// Optional plugin capability: the mod exposes a settings schema which the host renders
    /// in the Main Menu Mods tab (a "Settings" button appears on the mod's row).
    /// Implement on the same class as <see cref="IModPlugin"/>.
    /// </summary>
    public interface IModSettingsProvider
    {
        IReadOnlyList<ModSettingDescriptor> GetSettingsSchema();
    }
}