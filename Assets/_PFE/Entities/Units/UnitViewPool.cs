using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Pools <see cref="UnitView"/>s across room transitions — Thread J's UnitViewPool.
    ///
    /// <para><b>The problem it removes.</b> <c>RoomUnitSpawner.RefreshAll</c> destroys every unit
    /// GameObject on room exit and rebuilds one per record on entry, so each transition paid a full
    /// visual construction per unit: GameObject, <c>SpriteRenderer</c>, <c>UnitAnimator</c>, sprite
    /// anchor and material lookup. The controller, motor and stats are still rebuilt per entry (they
    /// are the simulation, and their state is written back to the record first) — but the view is
    /// pure presentation, so it can be deactivated and reused instead of destroyed.</para>
    ///
    /// <para><b>How a view moves.</b> <see cref="Acquire"/> parents the view under the unit root, so
    /// every hierarchy search the controllers already do (<c>GetComponentInChildren&lt;UnitAnimator&gt;</c>,
    /// <c>ClimberBrain</c>'s <c>Find("Visual")</c>) keeps working unchanged — the pooled view is
    /// indistinguishable from the inline-built child it replaces. <see cref="Release"/> detaches it
    /// back to the pool root <i>before</i> the unit root is destroyed, which is what lets the view
    /// outlive the room.</para>
    ///
    /// <para><b>Only clean views are recycled.</b> A view that gained components or children since
    /// its configure — an NPC's <c>NpcVisualAnimator</c>, a shield dome, turret parts — is specific
    /// to the unit that added them, and reusing it would leak that unit's extras into the next
    /// occupant. Those are destroyed on release instead; the common case (raiders, zombies, beasts,
    /// robots — one renderer, one animator, nothing else) is what the pool recycles.</para>
    ///
    /// <para><b>One pool per session.</b> Registered as a container singleton so every room's
    /// spawner draws from the same stack — a view released by the room being left is available to
    /// the room being entered. Plain C# (no MonoBehaviour) so fixtures construct it directly and
    /// own its lifetime via <see cref="Shutdown"/>.</para>
    /// </summary>
    public sealed class UnitViewPool
    {
        /// <summary>
        /// The components a fresh view carries: Transform, SpriteRenderer, UnitAnimator, UnitView.
        /// Anything beyond this arrived after the configure and makes the view non-recyclable.
        /// </summary>
        const int BaseComponentCount = 4;

        readonly Stack<UnitView> _idle = new Stack<UnitView>();
        Transform _root;

        /// <summary>Views currently parented under unit roots.</summary>
        public int ActiveCount { get; private set; }

        /// <summary>Views parked under the pool root, ready to reuse.</summary>
        public int IdleCount => _idle.Count;

        /// <summary>
        /// Take a view for a unit: reuse an idle one when available, build a new one otherwise,
        /// then parent it under the unit root and configure it for the unit. The view is named
        /// <c>"Visual"</c> because that is the name the inline build used and the name
        /// <c>ClimberBrain</c> searches for.
        /// </summary>
        public UnitView Acquire(UnitDefinition definition, UnitInstance unit, Transform parent, int sortingOrder)
        {
            UnitView view = PopIdle();
            if (view == null)
            {
                view = CreateView();
            }

            view.transform.SetParent(parent, false);
            view.gameObject.SetActive(true);
            view.Configure(definition, unit, sortingOrder);
            ActiveCount++;
            return view;
        }

        /// <summary>
        /// Return a view. A clean view is deactivated and parked for reuse; a decorated one (extra
        /// components or children — see the class remarks) is destroyed. Safe for a view that was
        /// never acquired and for one Unity already destroyed.
        /// </summary>
        public void Release(UnitView view)
        {
            if (view == null)
            {
                return;
            }

            if (ActiveCount > 0)
            {
                ActiveCount--;
            }

            if (!IsRecyclable(view))
            {
                DestroyObject(view.gameObject);
                return;
            }

            view.gameObject.SetActive(false);
            view.transform.SetParent(Root, false);
            _idle.Push(view);
        }

        /// <summary>
        /// Destroy every parked view and the pool root. Active views belong to their spawners —
        /// release them first. Called on session teardown, and by fixtures so a test leaves no
        /// objects behind.
        /// </summary>
        public void Shutdown()
        {
            while (_idle.Count > 0)
            {
                UnitView view = _idle.Pop();
                if (view != null)
                {
                    DestroyObject(view.gameObject);
                }
            }

            if (_root != null)
            {
                DestroyObject(_root.gameObject);
                _root = null;
            }
        }

        /// <summary>
        /// A view is recyclable only if it still carries exactly the base component set and no
        /// children. The check is structural rather than a flag because the decorations are added
        /// by OTHER systems (the NPC controller, the shield overlay, the turret presenter), not
        /// through this class — a flag would rely on every one of them remembering to set it.
        /// </summary>
        static bool IsRecyclable(UnitView view)
        {
            if (view.transform.childCount > 0)
            {
                return false;
            }

            return view.GetComponents<Component>().Length <= BaseComponentCount;
        }

        UnitView PopIdle()
        {
            // Unity fake-null: a parked view whose pool root was destroyed out from under it (scene
            // unload without a Shutdown) compares equal to null and is dropped rather than reused.
            while (_idle.Count > 0)
            {
                UnitView view = _idle.Pop();
                if (view != null)
                {
                    return view;
                }
            }

            return null;
        }

        static UnitView CreateView()
        {
            var viewObject = new GameObject("Visual");
            return viewObject.AddComponent<UnitView>();
        }

        Transform Root
        {
            get
            {
                if (_root == null)
                {
                    _root = new GameObject("UnitViewPool").transform;
                }

                return _root;
            }
        }

        static void DestroyObject(GameObject target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(target);
            }
            else
            {
                Object.DestroyImmediate(target);
            }
        }
    }
}
