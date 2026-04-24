using UnityEngine;
using System.Collections.Generic;

namespace PFE.Core.Pooling
{
    /// <summary>
    /// Generic GameObject pool for object pooling to reduce instantiation overhead.
    /// Particularly important for "bullet hell" scenarios with many projectiles.
    /// Uses Unity's built-in pooling pattern for optimal performance.
    /// </summary>
    /// <typeparam name="T">Component type that will be pooled</typeparam>
    public class GameObjectPool<T> where T : Component
    {
        private readonly T _prefab;
        private readonly Stack<T> _stack;
        private readonly Transform _parent;
        private readonly int _initialSize;
        private readonly int _maxSize;
        private readonly System.Action<T> _onGet;
        private readonly System.Action<T> _onRelease;
        private readonly System.Action<T> _onCreate;

        /// <summary>
        /// Current number of active objects from this pool
        /// </summary>
        public int ActiveCount { get; private set; }

        /// <summary>
        /// Current number of inactive objects available in the pool
        /// </summary>
        public int InactiveCount => _stack.Count;

        /// <summary>
        /// Create a new GameObject pool.
        /// </summary>
        /// <param name="prefab">Prefab to pool</param>
        /// <param name="initialSize">Initial number of objects to pre-warm</param>
        /// <param name="maxSize">Maximum number of objects to pool (0 = unlimited)</param>
        /// <param name="parent">Transform parent for pooled objects</param>
        /// <param name="onGet">Optional callback when getting an object</param>
        /// <param name="onRelease">Optional callback when releasing an object</param>
        /// <param name="onCreate">Optional callback when creating a brand-new pooled object</param>
        public GameObjectPool(
            T prefab,
            int initialSize = 10,
            int maxSize = 100,
            Transform parent = null,
            System.Action<T> onGet = null,
            System.Action<T> onRelease = null,
            System.Action<T> onCreate = null)
        {
            _prefab = prefab;
            _initialSize = initialSize;
            _maxSize = maxSize;
            _parent = parent;
            _onGet = onGet;
            _onRelease = onRelease;
            _onCreate = onCreate;
            _stack = new Stack<T>(initialSize);

            // Pre-warm the pool with initial instances
            Prewarm();
        }

        /// <summary>
        /// Get an instance from the pool.
        /// Creates a new instance if pool is empty (under maxSize).
        /// Optional <paramref name="beforeActivate"/> runs while the object is still inactive,
        /// which is useful for pooled physics objects that must have their pose restored before
        /// Unity re-enables simulation callbacks.
        /// </summary>
        public T Get(System.Action<T> beforeActivate = null)
        {
            T element;

            if (_stack.Count == 0)
            {
                // Pool is empty, create new instance
                element = CreateInactiveElement();
            }
            else
            {
                // Get from pool
                element = _stack.Pop();
            }

            // Allow callers to restore transform / physics state before activation.
            beforeActivate?.Invoke(element);

            // Activate the object
            element.gameObject.SetActive(true);

            // Invoke custom callback
            _onGet?.Invoke(element);

            ActiveCount++;

            return element;
        }

        /// <summary>
        /// Return an instance to the pool.
        /// The object is deactivated and stored for reuse.
        /// </summary>
        public void Release(T element)
        {
            if (element == null) return;

            ActiveCount--;

            // Deactivate the object
            element.gameObject.SetActive(false);

            // Invoke custom callback
            _onRelease?.Invoke(element);

            // Return to stack if under max size
            if (_maxSize <= 0 || _stack.Count < _maxSize)
            {
                _stack.Push(element);
            }
            else
            {
                // Pool is full, destroy the object
                // Use DestroyImmediate in edit mode, Destroy in play mode
                #if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    Object.DestroyImmediate(element.gameObject);
                }
                else
                {
                    Object.Destroy(element.gameObject);
                }
                #else
                Object.Destroy(element.gameObject);
                #endif
            }
        }

        /// <summary>
        /// Clear the pool, destroying all pooled objects.
        /// </summary>
        public void Clear()
        {
            while (_stack.Count > 0)
            {
                var element = _stack.Pop();
                if (element != null)
                {
                    // Use DestroyImmediate in edit mode, Destroy in play mode
                    #if UNITY_EDITOR
                    if (!Application.isPlaying)
                    {
                        Object.DestroyImmediate(element.gameObject);
                    }
                    else
                    {
                        Object.Destroy(element.gameObject);
                    }
                    #else
                    Object.Destroy(element.gameObject);
                    #endif
                }
            }
            ActiveCount = 0;
        }

        /// <summary>
        /// Pre-warm the pool with initial instances.
        /// </summary>
        private void Prewarm()
        {
            for (int i = 0; i < _initialSize; i++)
            {
                var element = CreateInactiveElement();
                _stack.Push(element);
            }
        }

        private T CreateInactiveElement()
        {
            var element = Object.Instantiate(_prefab, _parent);
            if (_parent != null)
            {
                element.transform.SetParent(_parent, false);
            }

            element.gameObject.SetActive(false);
            _onCreate?.Invoke(element);
            return element;
        }
    }
}
