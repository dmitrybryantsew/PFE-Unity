using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Resolves a <see cref="MapObjectDefinition"/> by its object id.
    ///
    /// <para><b>Why this seam exists — the map-object twin of
    /// <c>PFE.Entities.Units.IUnitDefinitionProvider</c>, for the same reason.</b>
    /// <c>GameDatabase.GetMapObjectDefinition</c> is an instance method on a DI-owned <c>GameDatabase</c>,
    /// and <c>MapObjectCatalog.GetDefinition</c> needs a catalog instance; the consumer that needs this
    /// (<c>RoomPopulator.CreateUnit</c>, filling in the definition row's <c>@cid</c> on the
    /// random-enemy path) is <c>static</c> and can reach neither. Injecting the lookup also keeps the
    /// behaviour testable, since a <see cref="MapObjectDefinition"/> is a <c>ScriptableObject</c> and
    /// cannot be built in the offline test harness.</para>
    ///
    /// <para><b>The specific defect this unblocks.</b> AS3 <c>Unit.create</c> reads the alias seed off
    /// the DEFINITION row — <c>node = AllData.d.obj.(@id == id)[0]</c> then
    /// <c>if(node.@cid.length()) cid = node.@cid</c> (<c>Unit.as:853-858</c>). The port supplies that on
    /// the authored path (<c>RoomPopulator.ProcessObjectSpawn</c>, from
    /// <c>spawnData.definition</c>), but the random-enemy path has no authored definition row for the
    /// family <c>Location.randomUnit()</c> just chose, so <c>trridge</c>/<c>trplate</c>/<c>trcans</c>/
    /// <c>trlaser</c> composed from a null seed and fell back to an id with no asset, and
    /// <c>landturret</c> silently resolved to the floor turret.</para>
    /// </summary>
    public interface IObjectDefinitionProvider
    {
        bool TryGetObject(string objectId, out MapObjectDefinition definition);
    }

    /// <summary>
    /// <c>Resources</c>-backed lookup over <c>MapObjects/Definitions</c>, following the same shape as
    /// <c>ResourcesUnitDefinitionProvider</c>: load the folder once, index by id, keep the index. The
    /// folder holds ~404 assets, so the load is deferred to first use and a caller that injects its own
    /// provider never pays for the scan.
    /// </summary>
    public sealed class ResourcesObjectDefinitionProvider : IObjectDefinitionProvider
    {
        public const string DefinitionsResourcesRoot = "MapObjects/Definitions";

        static ResourcesObjectDefinitionProvider s_shared;

        /// <summary>The production instance. Lazy — see the class note.</summary>
        public static ResourcesObjectDefinitionProvider Shared =>
            s_shared ??= new ResourcesObjectDefinitionProvider();

        Dictionary<string, MapObjectDefinition> _byId;

        public bool TryGetObject(string objectId, out MapObjectDefinition definition)
        {
            definition = null;
            if (string.IsNullOrWhiteSpace(objectId))
            {
                return false;
            }

            EnsureLoaded();
            return _byId.TryGetValue(objectId, out definition);
        }

        /// <summary>Drop the index so the next lookup re-reads <c>Resources</c>. For tests and re-imports.</summary>
        public void ClearCache()
        {
            _byId = null;
        }

        void EnsureLoaded()
        {
            if (_byId != null)
            {
                return;
            }

            _byId = new Dictionary<string, MapObjectDefinition>(StringComparer.OrdinalIgnoreCase);

            MapObjectDefinition[] loaded = Resources.LoadAll<MapObjectDefinition>(DefinitionsResourcesRoot);
            for (int i = 0; i < loaded.Length; i++)
            {
                MapObjectDefinition definition = loaded[i];
                if (definition == null || string.IsNullOrWhiteSpace(definition.objectId))
                {
                    continue;
                }

                // Name what we dropped, for the same reason the unit provider does: a duplicate object id
                // means two assets claim the same row, and silently keeping whichever LoadAll returned
                // first is how a wrong definition ships.
                if (_byId.TryGetValue(definition.objectId, out MapObjectDefinition incumbent) &&
                    incumbent != definition)
                {
                    Debug.LogWarning(
                        $"[ResourcesObjectDefinitionProvider] Two MapObjectDefinitions claim id " +
                        $"'{definition.objectId}': keeping '{incumbent.name}', dropping " +
                        $"'{definition.name}'. A duplicate object id is a data bug.");
                    continue;
                }

                _byId[definition.objectId] = definition;
            }
        }
    }
}
