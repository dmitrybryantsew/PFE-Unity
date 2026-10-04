using System.Collections.Generic;
using UnityEngine;
using VContainer.Unity;

namespace PFE.Core
{
    /// <summary>
    /// Puts every always-on simulation service onto <see cref="SimLoop"/> — the single place a
    /// container-registered system becomes a tick.
    ///
    /// <para><b>What this replaces, and why it is worth a class.</b> <see cref="ISimTickable"/> is not a
    /// VContainer entry point: the container will happily construct and inject a tickable and then never
    /// call <see cref="ISimTickable.SimTick"/>, because the dispatcher only resolves
    /// <c>IStartable</c>/<c>ITickable</c>. So every system used to need its own <c>IStartable</c> whose
    /// only job was <c>simLoop.Register(this)</c> — and forgetting that line produced a system that
    /// constructed, injected, looked wired, and did nothing. That is a silent failure with no symptom
    /// except missing behaviour, which is this project's most expensive class of bug.</para>
    ///
    /// <para>Now a system declares itself with <see cref="IAutoRegisteredSimTickable"/> and registration
    /// stops being something anyone can forget. Note the boundary: this covers <b>always-on container
    /// services only</b>. The nine lifecycle-bound tickables (<c>Projectile</c>, <c>UnitController</c>,
    /// <c>ThrownObject</c>, <c>TilePhysicsController</c>, <c>PlayerManaTicker</c>,
    /// <c>PlayerActionInteractor</c>, <c>PlayerSpellCaster</c>, …) keep registering themselves, because
    /// they must also <i>unregister</i> — see <see cref="IAutoRegisteredSimTickable"/> for the full
    /// accounting. Auto-registering those would leak dead objects onto the loop forever.</para>
    ///
    /// <para><b>Why the marker and not <c>IReadOnlyList&lt;ISimTickable&gt;</c>.</b> VContainer resolves
    /// both, and the plain interface list would be shorter to write — but it would also collect
    /// <c>GameLoopManager</c>, whose registration is gated on the <c>SimTickRoom</c> debug flag, and
    /// registering it unconditionally would move the room heartbeat from the per-frame path to 30 Hz for
    /// every session. A behaviour switch must not be flipped by a DI convenience.</para>
    ///
    /// <para><b>Ordering is unaffected.</b> <see cref="SimLoop.Register"/> de-duplicates and the loop
    /// resolves its order lazily from <see cref="ISimTickable.TickOrder"/> on the next tick, so
    /// registration order — and therefore the order this registrar happens to iterate — does not
    /// matter.</para>
    /// </summary>
    public sealed class SimTickRegistrar : IStartable
    {
        private readonly SimLoop _simLoop;
        private readonly IReadOnlyList<IAutoRegisteredSimTickable> _tickables;

        private int _registeredCount;

        /// <summary>
        /// Wires the registrar. Both arguments may be null — a container built without a loop (a test
        /// scene, a tool) must degrade to a warning rather than a throw.
        /// </summary>
        public SimTickRegistrar(SimLoop simLoop, IReadOnlyList<IAutoRegisteredSimTickable> tickables)
        {
            _simLoop = simLoop;
            _tickables = tickables;
        }

        /// <summary>
        /// How many systems this registrar actually attached. The readback for "the system exists, was
        /// injected, and still does nothing" — 0 here with a non-empty declaration set means the
        /// container never built the registrar.
        /// </summary>
        public int RegisteredCount => _registeredCount;

        /// <summary>Attaches every declared always-on tickable. Called by VContainer.</summary>
        public void Start()
        {
            if (_simLoop == null)
            {
                Debug.LogWarning(
                    "[SimTickRegistrar] No SimLoop; every always-on simulation system will be inert. "
                    + "Systems will construct and inject correctly and never advance.");
                return;
            }

            if (_tickables == null) return;

            for (int i = 0; i < _tickables.Count; i++)
            {
                IAutoRegisteredSimTickable tickable = _tickables[i];
                if (tickable == null) continue;

                _simLoop.Register(tickable);
                _registeredCount++;
            }
        }
    }
}
