#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using PFE.Systems.Map;

namespace PFE.Editor
{
    /// <summary>
    /// Editor utility to import tile_forms.json into a TileFormDatabase ScriptableObject.
    /// 
    /// Usage:
    ///   1. Place tile_forms.json anywhere in Assets/
    ///   2. Menu: PFE > Import Tile Forms JSON
    ///   3. Select the json file
    ///   4. ScriptableObject is created at Assets/_PFE/Data/TileFormDatabase.asset
    /// </summary>
    public static class TileFormImporter
    {
        private const string DefaultOutputPath = "Assets/_PFE/Data/TileFormDatabase.asset";

        [MenuItem("PFE/Import Tile Forms JSON")]
        public static void ImportFromMenu()
        {
            string jsonPath = EditorUtility.OpenFilePanel("Select tile_forms.json", "Assets", "json");
            if (string.IsNullOrEmpty(jsonPath))
                return;

            Import(jsonPath, DefaultOutputPath);
        }

        public static void Import(string jsonPath, string outputAssetPath)
        {
            // Read JSON
            string jsonText = File.ReadAllText(jsonPath, System.Text.Encoding.UTF8);
            
            // Try Newtonsoft.Json first if available (gives better error messages)
            TileFormsJson jsonData = null;
            bool parsed = false;
            
            #if NEWTONSOFT_JSON
            try
            {
                jsonData = Newtonsoft.Json.JsonConvert.DeserializeObject<TileFormsJson>(jsonText);
                parsed = jsonData != null;
                if (parsed)
                    Debug.Log("[TileFormImporter] Parsed using Newtonsoft.Json");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TileFormImporter] Newtonsoft.Json parse failed: {ex.Message}");
            }
            #endif
            
            // Fall back to Unity's JsonUtility
            if (!parsed)
            {
                try
                {
                    // JsonUtility requires the JSON to start with {"instanceID":0} wrapper sometimes
                    // but usually works with plain objects. Let's try direct parsing first.
                    jsonData = JsonUtility.FromJson<TileFormsJson>(jsonText);
                    
                    // If that fails, try wrapping the JSON (Unity sometimes needs this for arrays)
                    if (jsonData == null || (jsonData.fForms == null && jsonData.oForms == null))
                    {
                        string wrappedJson = "{ \"TileFormsJson\": " + jsonText + " }";
                        jsonData = JsonUtility.FromJson<TileFormsJson>(wrappedJson);
                    }
                    
                    parsed = jsonData != null && (jsonData.fForms != null || jsonData.oForms != null);
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[TileFormImporter] JsonUtility parse error: {ex.Message}\n{ex.StackTrace}");
                }
            }

            if (!parsed || jsonData == null)
            {
                Debug.LogError("[TileFormImporter] Failed to parse JSON! Check that the file is valid JSON and matches the expected structure.");
                Debug.LogError($"[TileFormImporter] JSON file: {jsonPath}");
                Debug.LogError($"[TileFormImporter] First 200 chars: {jsonText.Substring(0, System.Math.Min(200, jsonText.Length))}");
                return;
            }

            // Ensure output directory exists
            string dir = Path.GetDirectoryName(outputAssetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            // Create or load existing ScriptableObject
            var database = AssetDatabase.LoadAssetAtPath<TileFormDatabase>(outputAssetPath);
            if (database == null)
            {
                database = ScriptableObject.CreateInstance<TileFormDatabase>();
                AssetDatabase.CreateAsset(database, outputAssetPath);
            }

            // Clear and populate
            database.Clear();

            int fCount = 0;
            int oCount = 0;

            if (jsonData.fForms != null)
            {
                foreach (var jf in jsonData.fForms)
                {
                    database.AddFForm(ConvertForm(jf));
                    fCount++;
                }
            }

            if (jsonData.oForms != null)
            {
                foreach (var jf in jsonData.oForms)
                {
                    database.AddOForm(ConvertForm(jf));
                    oCount++;
                }
            }

            EditorUtility.SetDirty(database);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[TileFormImporter] Imported {fCount} fForms + {oCount} oForms into {outputAssetPath}");

            // Validate by initializing
            database.Initialize();

            // Select the created asset
            Selection.activeObject = database;
            EditorGUIUtility.PingObject(database);
        }

        private static TileForm ConvertForm(TileFormJsonEntry entry)
        {
            return new TileForm
            {
                id = entry.id ?? "",
                ed = entry.ed,
                front = entry.front ?? "",
                back = entry.back ?? "",
                vid = entry.vid,
                rear = entry.rear,
                phis = entry.phis,
                shelf = entry.shelf,
                diagon = entry.diagon,
                stair = entry.stair,
                mat = entry.mat,
                hp = entry.hp,
                thre = entry.thre,
                indestruct = entry.indestruct,
                lurk = entry.lurk,
                mirror = entry.mirror ?? ""
            };
        }

        // --- JSON deserialization types (match tile_forms.json structure) ---
        // IMPORTANT: These must be public for Unity's JsonUtility to deserialize properly

        [System.Serializable]
        public class TileFormsJson
        {
            public int version;
            public string extracted_from;
            public FormCountJson form_count;
            public List<TileFormJsonEntry> fForms;
            public List<TileFormJsonEntry> oForms;
        }

        [System.Serializable]
        public class FormCountJson
        {
            public int fForms;
            public int oForms;
        }

        [System.Serializable]
        public class TileFormJsonEntry
        {
            public string id;
            public int ed;
            public string front;
            public string back;
            public int vid;
            public int mat;
            public int hp;
            public int thre;
            public bool indestruct;
            public int phis;
            public bool shelf;
            public int diagon;
            public int stair;
            public int lurk;
            public bool rear;
            public string mirror;
        }
    }
}
#endif