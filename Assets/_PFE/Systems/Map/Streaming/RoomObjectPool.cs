using UnityEngine;
using System.Collections.Generic;

namespace PFE.Systems.Map.Streaming
{
    /// <summary>
    /// Object pool for tile GameObjects to improve performance when rendering rooms.
    /// Reduces instantiation/destruction overhead when switching between rooms.
    /// </summary>
    public class RoomObjectPool : MonoBehaviour
    {
        private static RoomObjectPool _instance;
        public static RoomObjectPool Instance => _instance;

        [Header("Pool Settings")]
        [Tooltip("Prefab to use for tile GameObjects")]
        [SerializeField] private GameObject tilePrefab;

        [Tooltip("Initial pool size")]
        [SerializeField] private int initialPoolSize = 1000;

        [Tooltip("Maximum pool size (0 = unlimited)")]
        [SerializeField] private int maxPoolSize = 5000;

        [Tooltip("Grow pool automatically if needed")]
        [SerializeField] private bool autoGrow = true;

        private Queue<GameObject> pool = new Queue<GameObject>();
        private HashSet<GameObject> activeObjects = new HashSet<GameObject>();
        private Transform poolContainer;

        #region Initialization

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;

            // Create container for pooled objects
            poolContainer = new GameObject("TilePoolContainer").transform;
            poolContainer.SetParent(transform);

            // Initialize pool
            if (tilePrefab != null)
            {
                for (int i = 0; i < initialPoolSize; i++)
                {
                    CreatePooledObject();
                }
            }
            else
            {
                Debug.LogWarning("RoomObjectPool: No tilePrefab assigned. Pool will be empty.");
            }

            Debug.Log($"RoomObjectPool: Initialized with {pool.Count} objects");
        }

        #endregion

        #region Public API

        /// <summary>
        /// Get a tile GameObject from the pool.
        /// </summary>
        public GameObject GetTile()
        {
            GameObject obj = null;

            // Try to get from pool
            if (pool.Count > 0)
            {
                obj = pool.Dequeue();
            }
            // Create new if allowed
            else if (autoGrow && (maxPoolSize == 0 || activeObjects.Count < maxPoolSize))
            {
                obj = CreatePooledObject();
            }
            // Pool is exhausted
            else
            {
                Debug.LogWarning("RoomObjectPool: Pool exhausted! Consider increasing pool size.");
                return null;
            }

            // Activate and track
            obj.SetActive(true);
            activeObjects.Add(obj);

            return obj;
        }

        /// <summary>
        /// Return a tile GameObject to the pool.
        /// </summary>
        public void ReturnTile(GameObject obj)
        {
            if (obj == null) return;

            if (!activeObjects.Contains(obj))
            {
                Debug.LogWarning("RoomObjectPool: Returning object that wasn't borrowed from pool");
                return;
            }

            // Deactivate and return to pool
            obj.SetActive(false);
            obj.transform.SetParent(poolContainer);
            activeObjects.Remove(obj);
            pool.Enqueue(obj);
        }

        /// <summary>
        /// Return all active objects to the pool.
        /// </summary>
        public void ReturnAll()
        {
            var objects = new List<GameObject>(activeObjects);
            foreach (var obj in objects)
            {
                ReturnTile(obj);
            }
        }

        /// <summary>
        /// Pre-warm the pool with a specific number of objects.
        /// </summary>
        public void Prewarm(int count)
        {
            if (maxPoolSize > 0 && pool.Count + count > maxPoolSize)
            {
                count = maxPoolSize - pool.Count;
            }

            for (int i = 0; i < count; i++)
            {
                if (maxPoolSize == 0 || pool.Count < maxPoolSize)
                {
                    CreatePooledObject();
                }
            }

            Debug.Log($"RoomObjectPool: Prewarmed to {pool.Count} objects");
        }

        /// <summary>
        /// Clear the pool (destroy all objects).
        /// </summary>
        public void ClearPool()
        {
            ReturnAll();

            while (pool.Count > 0)
            {
                GameObject obj = pool.Dequeue();
                if (obj != null)
                {
                    Destroy(obj);
                }
            }

            Debug.Log("RoomObjectPool: Pool cleared");
        }

        #endregion

        #region Private Methods

        private GameObject CreatePooledObject()
        {
            if (tilePrefab == null) return null;

            GameObject obj = Instantiate(tilePrefab, poolContainer);
            obj.SetActive(false);
            pool.Enqueue(obj);

            return obj;
        }

        #endregion

        #region Debug

        private void OnGUI()
        {
            if (!Debug.isDebugBuild) return;

            GUILayout.BeginArea(new Rect(10, 220, 300, 150));
            GUILayout.Label("Object Pool Status");
            GUILayout.Label($"Pool Size: {pool.Count}");
            GUILayout.Label($"Active Objects: {activeObjects.Count}");
            GUILayout.Label($"Total Created: {pool.Count + activeObjects.Count}");
            GUILayout.Label($"Max Pool Size: {(maxPoolSize == 0 ? "Unlimited" : maxPoolSize.ToString())}");
            GUILayout.EndArea();
        }

        #endregion
    }
}
