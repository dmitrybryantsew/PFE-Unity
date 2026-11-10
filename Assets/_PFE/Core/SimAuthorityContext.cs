using System;

namespace PFE.Core
{
    /// <summary>
    /// Which side of a future session this <b>process</b> is. Pushed once at bootstrap, not derived
    /// per write. Single-player and a listen server are <see cref="Host"/>; a joined peer is
    /// <see cref="Client"/>.
    /// </summary>
    public enum SimPeerRole
    {
        Host = 0,
        Client = 1,
    }

    /// <summary>
    /// Which kind of simulation a given <see cref="SimLoop"/> instance drives. Pushed (not injected),
    /// because six call sites across five fixtures construct <c>new SimLoop(clock, settings)</c> and a
    /// constructor parameter would either break them or make the container's constructor choice load
    /// bearing. The default is <see cref="Host"/>.
    /// </summary>
    public enum SimLoopRole
    {
        Host = 0,
        ClientPrediction = 1,
    }

    /// <summary>The three situations in which a write to Sim state can happen.</summary>
    public enum SimWriteContext
    {
        /// <summary>Host-originated write: a host tick, or any non-tick host path (console, load, Awake).</summary>
        HostSimulation = 0,

        /// <summary>A peer <b>mirroring</b> host truth from a snapshot. Legal on any peer, including a client.</summary>
        SnapshotApplication = 1,

        /// <summary>A client's own prediction, or any client path outside the snapshot pipeline.</summary>
        ClientPrediction = 2,
    }

    /// <summary>
    /// Phase 2.4 (mechanism b): the ambient write authority of the current call stack.
    ///
    /// <para><b>The rule this encodes.</b> <c>docs/Roadmap/05</c> §1.5 rule 2 and
    /// <see cref="Authority"/> agree: a client never <b>originates</b> authoritative state changes, it
    /// only <b>mirrors</b> host-originated ones. The first draft of row 2.4 instead said "a write
    /// outside a host tick is forbidden", which is wrong twice over: applying a host snapshot is a
    /// legitimate client write, and single-player legitimately writes authoritative state outside a
    /// tick (console commands, save/load, <c>Awake</c>, message handlers). Hence three contexts rather
    /// than a two-state "inside a host tick" flag.</para>
    ///
    /// <para><b>Ambient default is the peer role, not a globally-permissive Host.</b> Outside any scope,
    /// a <see cref="SimPeerRole.Host"/> process is <see cref="SimWriteContext.HostSimulation"/> and a
    /// <see cref="SimPeerRole.Client"/> process is <see cref="SimWriteContext.ClientPrediction"/>. A
    /// global Host default would silently allow the most likely first Phase-4 bug — a client message
    /// handler or HUD action writing authoritative state between ticks. <see cref="GameLifetimeScope"/>
    /// pushes <see cref="SimPeerRole.Host"/> at bootstrap, so today's single-player game is entirely
    /// silent: the guard changes no behaviour.</para>
    ///
    /// <para><b>Scopes are save/restore, not set/clear.</b> Snapshot application can nest inside a frame
    /// that also ticks, and a bare bool set/clear corrupts itself on re-entry. Each
    /// <see cref="Scope"/> remembers the previous context and restores it; <see cref="IsPredictionTickActive"/>
    /// is a depth, not a flag, for the same reason.</para>
    ///
    /// <para><b>Plain static, not thread-static.</b> The loop is Unity main-thread only
    /// (<c>SimLoop.Tick</c> is VContainer's <c>ITickable</c>), and a thread-static carrier would make a
    /// write on a worker thread look like a brand-new ambient context rather than a bug. If off-thread
    /// simulation is ever introduced, this is one of the places that must be revisited.</para>
    /// </summary>
    public static class SimAuthorityContext
    {
        private static SimWriteContext _current = SimWriteContext.HostSimulation;
        private static int _scopeDepth;
        private static int _predictionDepth;

        /// <summary>Pushed once by the DI root. Defaults to Host so headless fixtures stay silent.</summary>
        public static SimPeerRole PeerRole { get; private set; } = SimPeerRole.Host;

        /// <summary>The context a <see cref="HostWriteGuard"/> call sees right now.</summary>
        public static SimWriteContext Current => _current;

        /// <summary>
        /// True while any prediction <see cref="SimLoop"/> dispatch is on the stack. A snapshot applied
        /// inside one is an ordering bug even if it wrapped itself in a snapshot scope, so the restore
        /// guard checks this depth too.
        /// </summary>
        public static bool IsPredictionTickActive => _predictionDepth > 0;

        /// <summary>Diagnostics for the offline fixtures; a leaked scope is a failed test, not a silent skip.</summary>
        public static int ScopeDepth => _scopeDepth;

        /// <summary>
        /// Called exactly once at bootstrap. Only recomputes the ambient context when no scope is
        /// active, so a late re-push cannot clobber an in-flight tick.
        /// </summary>
        public static void SetPeerRole(SimPeerRole role)
        {
            PeerRole = role;
            if (_scopeDepth == 0)
            {
                _current = role == SimPeerRole.Host
                    ? SimWriteContext.HostSimulation
                    : SimWriteContext.ClientPrediction;
            }
        }

        /// <summary>Restores the process to the single-player default. Bootstrap/test hook.</summary>
        public static void Reset()
        {
            PeerRole = SimPeerRole.Host;
            _current = SimWriteContext.HostSimulation;
            _scopeDepth = 0;
            _predictionDepth = 0;
        }

        /// <summary>Scope a whole <c>SimLoop.StepOnce()</c> dispatch. Compiled out of release builds at the call site.</summary>
        public static Scope EnterSimulation(SimLoopRole role)
        {
            bool prediction = role == SimLoopRole.ClientPrediction;
            return Push(
                prediction ? SimWriteContext.ClientPrediction : SimWriteContext.HostSimulation,
                prediction);
        }

        /// <summary>
        /// Scope the snapshot deserializer/applier. Legal on any peer. A Phase 3/4 applier MUST wrap
        /// its <c>ApplyUnitState</c>/<c>ApplyMotorState</c> calls in this scope; nothing in the tree
        /// calls those surfaces yet, so the obligation is documented in the readiness doc, not wired.
        /// </summary>
        public static Scope EnterSnapshotApplication()
        {
            return Push(SimWriteContext.SnapshotApplication, false);
        }

        private static Scope Push(SimWriteContext next, bool prediction)
        {
            var scope = new Scope(_current, prediction);
            _current = next;
            _scopeDepth++;
            if (prediction) _predictionDepth++;
            return scope;
        }

        private static void Pop(SimWriteContext previous, bool prediction)
        {
            _current = previous;
            _scopeDepth--;
            if (prediction) _predictionDepth--;
        }

        /// <summary>
        /// A class (not a struct) so a copied reference cannot double-decrement the depths. Debug-only:
        /// the <c>SimLoop</c> call site is inside <c>#if UNITY_ASSERTIONS</c>.
        /// </summary>
        public sealed class Scope : IDisposable
        {
            private readonly SimWriteContext _previous;
            private readonly bool _prediction;
            private bool _disposed;

            internal Scope(SimWriteContext previous, bool prediction)
            {
                _previous = previous;
                _prediction = prediction;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Pop(_previous, _prediction);
            }
        }
    }

    /// <summary>
    /// Phase 2.4 (mechanism c, with (a) folded in as <see cref="CheckRestore"/>): the runtime tripwire
    /// for writes to <c>[Authoritative]</c> state.
    ///
    /// <para><b>Why spot-guards are the strongest legal mechanism.</b> The marked fields are private,
    /// so writes from outside the declaring class are already funnelled through a countable set of
    /// public/protected mutators. Converting all of them to guarded properties (mechanism d) is dead,
    /// not deferred: <c>SimUnitState</c>/<c>SimMotorState</c> live in <c>PFE.Sim</c>, which is kept
    /// Unity-free and references neither <c>PFE.Core</c> nor Unity's assertions — and the tick code
    /// writes struct fields directly, where no property setter runs at all.</para>
    ///
    /// <para><b>Zero release cost.</b> Both check methods are
    /// <c>[Conditional("UNITY_ASSERTIONS")]</c>, so the call AND its arguments are removed at every
    /// call site in a release player. <c>UNITY_ASSERTIONS</c> is present in the generated
    /// <c>PFE.View.csproj</c> and <c>PFE.Tests.csproj</c> DefineConstants, so the offline harness sees
    /// the real guard.</para>
    ///
    /// <para><b>Observable failure sink.</b> Default is a thrown
    /// <see cref="InvalidOperationException"/> (a hard debug assert with no Unity dependency). Tests
    /// swap in a recorder so the mutation control does not depend on define constants or on a
    /// <c>MonoBehaviour</c> — those cannot be instantiated outside the editor (ECall).</para>
    /// </summary>
    public static class HostWriteGuard
    {
        private static Action<string> _failureSink = ThrowOnForbiddenWrite;

        /// <summary>Replace the failure sink. Pass null to restore the default.</summary>
        public static void SetFailureSink(Action<string> sink)
        {
            _failureSink = sink ?? ThrowOnForbiddenWrite;
        }

        /// <summary>Restore the default throwing sink. Called by every offline fixture's TearDown.</summary>
        public static void ResetFailureSink()
        {
            _failureSink = ThrowOnForbiddenWrite;
        }

        /// <summary>
        /// Call at the top of every reviewed mutator that <b>originates</b> <c>[Authoritative]</c>
        /// state. Non-authoritative classifications are deliberately ignored: motor state is
        /// <c>[ClientPredicted]</c> and a client is allowed to write it.
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        public static void Check(SimAuthority authority)
        {
            if (authority != SimAuthority.Authoritative) return;
            if (SimAuthorityContext.Current != SimWriteContext.ClientPrediction) return;

            _failureSink(
                "HostWriteGuard: a client originated a write to [Authoritative] Sim state. " +
                "Clients send intent through PlayerCommand and mirror host truth through " +
                "SimAuthorityContext.EnterSnapshotApplication(); they never originate it. " +
                "(docs/Roadmap/05 §1.5 rule 2)");
        }

        /// <summary>
        /// Mechanism (a), the snapshot-boundary case. Call on restore surfaces
        /// (<c>ApplyUnitState</c>, <c>ApplyMotorState</c>). This is NOT a host-only check: a client
        /// applying a host snapshot is the correct path. What it forbids is applying one outside the
        /// snapshot pipeline, or interleaving host truth into an in-flight prediction tick.
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        public static void CheckRestore()
        {
            bool outsideSnapshotPipeline =
                SimAuthorityContext.Current == SimWriteContext.ClientPrediction;
            bool insidePredictionTick = SimAuthorityContext.IsPredictionTickActive;

            if (!outsideSnapshotPipeline && !insidePredictionTick) return;

            _failureSink(
                "HostWriteGuard: a Sim state restore ran outside SnapshotApplication, or inside a " +
                "client prediction tick. Wrap the snapshot applier in " +
                "SimAuthorityContext.EnterSnapshotApplication() and apply it between ticks.");
        }

        private static void ThrowOnForbiddenWrite(string message)
        {
            throw new InvalidOperationException(message);
        }
    }
}
