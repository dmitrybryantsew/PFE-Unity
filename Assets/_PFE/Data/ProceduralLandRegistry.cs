using System;
using System.Collections.Generic;
using PFE.ModAPI;

namespace PFE.Data
{
    /// <summary>
    /// Runtime registry of enabled <see cref="IProceduralLandProvider"/> instances and
    /// mod console command handlers registered through <see cref="IModHostContext"/>.
    /// Owned by <see cref="ModLoader"/> / DI container (never a static singleton).
    /// </summary>
    public sealed class ProceduralLandRegistry
    {
        private readonly List<IProceduralLandProvider> _providers = new List<IProceduralLandProvider>();
        private readonly Dictionary<string, object> _consoleHandlers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<IProceduralLandProvider> Providers => _providers;
        public IReadOnlyDictionary<string, object> ConsoleHandlers => _consoleHandlers;

        /// <summary>
        /// The live console context for mod commands, set by the scene bridge while a land is
        /// active and cleared when it tears down. Null outside gameplay.
        /// </summary>
        public IModConsoleContext ModConsoleContext { get; set; }

        public void Clear()
        {
            _providers.Clear();
            _consoleHandlers.Clear();
        }

        public void RegisterProvider(IProceduralLandProvider provider)
        {
            if (provider == null || _providers.Contains(provider))
            {
                return;
            }

            _providers.Add(provider);
        }

        public void RegisterConsoleCommands(string prefix, object commandHandler)
        {
            if (string.IsNullOrWhiteSpace(prefix) || commandHandler == null)
            {
                return;
            }

            _consoleHandlers[prefix.Trim()] = commandHandler;
        }

        public IProceduralLandProvider GetProviderForLand(string landId)
        {
            if (string.IsNullOrWhiteSpace(landId))
            {
                return null;
            }

            for (int i = 0; i < _providers.Count; i++)
            {
                IProceduralLandProvider provider = _providers[i];
                if (provider != null && provider.HandlesLand(landId))
                {
                    return provider;
                }
            }

            return null;
        }

        public ProceduralLandDescriptor GetDescriptor(string landId)
        {
            IProceduralLandProvider provider = GetProviderForLand(landId);
            return provider?.GetDescriptor(landId);
        }

        public List<ProceduralLandDescriptor> GetAllDescriptors()
        {
            var result = new List<ProceduralLandDescriptor>();
            for (int i = 0; i < _providers.Count; i++)
            {
                IReadOnlyList<ProceduralLandDescriptor> descriptors = _providers[i]?.GetDescriptors();
                if (descriptors == null) continue;
                for (int j = 0; j < descriptors.Count; j++)
                {
                    if (descriptors[j] != null)
                    {
                        result.Add(descriptors[j]);
                    }
                }
            }

            return result;
        }
    }
}
