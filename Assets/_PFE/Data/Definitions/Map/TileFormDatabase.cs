using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Database of all tile forms, loaded from tile_forms.json.
    /// Port of AS3 Form.fForms[] and Form.oForms[] static arrays.
    /// 
    /// Usage at runtime:
    ///   var wallForm = database.GetFForm("A");  // primary wall type
    ///   var bgForm = database.GetOForm("C");    // background texture overlay
    /// 
    /// Created via Editor menu: PFE > Import Tile Forms JSON
    /// </summary>
    [CreateAssetMenu(fileName = "TileFormDatabase", menuName = "PFE/Map/Tile Form Database")]
    public class TileFormDatabase : ScriptableObject
    {
        [Header("Form Data")]
        [SerializeField] private List<TileForm> fFormsList = new List<TileForm>();
        [SerializeField] private List<TileForm> oFormsList = new List<TileForm>();

        // Runtime lookup dictionaries (built on first access)
        [NonSerialized] private Dictionary<string, TileForm> _fForms;
        [NonSerialized] private Dictionary<string, TileForm> _oForms;
        [NonSerialized] private bool _initialized;

        /// <summary>Total number of primary forms.</summary>
        public int FFormCount => fFormsList.Count;

        /// <summary>Total number of overlay forms.</summary>
        public int OFormCount => oFormsList.Count;

        /// <summary>
        /// Ensure runtime dictionaries are built.
        /// Called automatically on first lookup; can also be called explicitly.
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;

            _fForms = new Dictionary<string, TileForm>(fFormsList.Count);
            foreach (var form in fFormsList)
            {
                if (!string.IsNullOrEmpty(form.id))
                {
                    if (_fForms.ContainsKey(form.id))
                        Debug.LogWarning($"[TileFormDatabase] Duplicate fForm id: '{form.id}'");
                    else
                        _fForms[form.id] = form;
                }
            }

            _oForms = new Dictionary<string, TileForm>(oFormsList.Count);
            foreach (var form in oFormsList)
            {
                if (!string.IsNullOrEmpty(form.id))
                {
                    if (_oForms.ContainsKey(form.id))
                        Debug.LogWarning($"[TileFormDatabase] Duplicate oForm id: '{form.id}'");
                    else
                        _oForms[form.id] = form;
                }
            }

            _initialized = true;
            Debug.Log($"[TileFormDatabase] Initialized: {_fForms.Count} fForms, {_oForms.Count} oForms");
        }

        /// <summary>
        /// Get a primary form (first character of tile string).
        /// Returns null if not found.
        /// </summary>
        public TileForm GetFForm(string id)
        {
            if (!_initialized) Initialize();
            _fForms.TryGetValue(id, out var form);
            return form;
        }

        /// <summary>
        /// Get an overlay form (subsequent characters of tile string).
        /// Returns null if not found.
        /// </summary>
        public TileForm GetOForm(string id)
        {
            if (!_initialized) Initialize();
            _oForms.TryGetValue(id, out var form);
            return form;
        }

        /// <summary>
        /// Check if a character is a valid fForm key.
        /// AS3 logic: charCode > 64 and charCode != 95 (underscore).
        /// </summary>
        public bool IsFFormChar(char c)
        {
            if (!_initialized) Initialize();
            return _fForms.ContainsKey(c.ToString());
        }

        /// <summary>
        /// Check if a character is a valid oForm key.
        /// </summary>
        public bool IsOFormChar(string key)
        {
            if (!_initialized) Initialize();
            return _oForms.ContainsKey(key);
        }

        // --- Editor population methods ---

        /// <summary>
        /// Clear all forms (editor use only).
        /// </summary>
        public void Clear()
        {
            fFormsList.Clear();
            oFormsList.Clear();
            _fForms = null;
            _oForms = null;
            _initialized = false;
        }

        /// <summary>
        /// Add a primary form (editor use only).
        /// </summary>
        public void AddFForm(TileForm form)
        {
            fFormsList.Add(form);
            _initialized = false; // Force rebuild on next access
        }

        /// <summary>
        /// Add an overlay form (editor use only).
        /// </summary>
        public void AddOForm(TileForm form)
        {
            oFormsList.Add(form);
            _initialized = false;
        }

        /// <summary>
        /// Get all fForm IDs (for debugging/editor display).
        /// </summary>
        public List<string> GetAllFFormIds()
        {
            var ids = new List<string>(fFormsList.Count);
            foreach (var f in fFormsList) ids.Add(f.id);
            return ids;
        }

        /// <summary>
        /// Get all oForm IDs (for debugging/editor display).
        /// </summary>
        public List<string> GetAllOFormIds()
        {
            var ids = new List<string>(oFormsList.Count);
            foreach (var f in oFormsList) ids.Add(f.id);
            return ids;
        }

        private void OnEnable()
        {
            // Reset so dictionaries rebuild with fresh deserialized data
            _initialized = false;
        }
    }
}