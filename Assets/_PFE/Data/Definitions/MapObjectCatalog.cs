using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Lightweight index of imported map object definitions.
    /// Used by importers and room conversion to resolve definitions quickly.
    /// </summary>
    [CreateAssetMenu(fileName = "MapObjectCatalog", menuName = "PFE/Map/Map Object Catalog")]
    public class MapObjectCatalog : ScriptableObject
    {
        public string sourceFilePath;
        public List<MapObjectDefinition> definitions = new List<MapObjectDefinition>();

        Dictionary<string, MapObjectDefinition> _definitionById;

        public int Count => definitions?.Count ?? 0;

        public void RebuildIndex()
        {
            _definitionById = new Dictionary<string, MapObjectDefinition>(System.StringComparer.OrdinalIgnoreCase);

            if (definitions == null)
            {
                return;
            }

            for (int i = 0; i < definitions.Count; i++)
            {
                MapObjectDefinition definition = definitions[i];
                if (definition == null || string.IsNullOrWhiteSpace(definition.objectId))
                {
                    continue;
                }

                _definitionById[definition.objectId] = definition;
            }
        }

        public bool TryGetDefinition(string objectId, out MapObjectDefinition definition)
        {
            if (_definitionById == null)
            {
                RebuildIndex();
            }

            if (string.IsNullOrWhiteSpace(objectId))
            {
                definition = null;
                return false;
            }

            return _definitionById.TryGetValue(objectId, out definition);
        }

        public MapObjectDefinition GetDefinition(string objectId)
        {
            TryGetDefinition(objectId, out MapObjectDefinition definition);
            return definition;
        }

        public void SetDefinitions(IEnumerable<MapObjectDefinition> newDefinitions)
        {
            definitions = newDefinitions == null
                ? new List<MapObjectDefinition>()
                : newDefinitions
                    .Where(def => def != null)
                    .OrderBy(def => def.objectId, System.StringComparer.OrdinalIgnoreCase)
                    .ToList();

            RebuildIndex();
        }
    }
}
