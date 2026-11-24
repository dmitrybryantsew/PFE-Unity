using System;

namespace PFE.ModAPI
{
    /// <summary>
    /// Marks an assembly as containing a PFE code mod plugin entry point.
    /// Scanned at startup across loaded assemblies without full-type reflection.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class PfeModPluginAttribute : Attribute
    {
        public Type PluginType { get; }

        public PfeModPluginAttribute(Type pluginType)
        {
            PluginType = pluginType;
        }
    }
}
