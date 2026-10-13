using System;
using System.Collections.Generic;
using PFE.Core;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Campaign;
using UnityEngine;
using VContainer.Unity;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// The always-on owner of the map-script interpreter: it puts every live
    /// <see cref="ScriptRunner"/> on the simulation clock at <see cref="SimTickOrder.Scripts"/>, and it
    /// is the one place the runner's hosts are assembled.
    ///
    /// <para><b>Why a container service rather than a tick on the room controller.</b> Scripts are the
    /// only thing in the port whose <i>whole</i> behaviour is a function of time — an authored
    /// <c>@t</c> is a frame count, and a cutscene that runs on the render frame advances at whatever the
    /// frame rate happens to be. <see cref="ISimTickable"/> is the project's contract for "this advances
    /// at 30 Hz, deterministically, and may not read <c>Time</c> or <c>Input</c>"
    /// (<c>ISimTickable.cs:10-29</c>), so the runner has to be on that clock and not on
    /// <c>Update</c>. Registering it through <see cref="IAutoRegisteredSimTickable"/> is what makes that
    /// a declaration the compiler enforces instead of a line someone has to remember — the failure mode
    /// otherwise is a script that constructs, injects, looks wired, and never advances.</para>
    ///
    /// <para><b>Why the runner is per room and the system is not.</b> <see cref="IScriptObjectHost"/> is
    /// the seam that resolves <c>@targ</c> against a room's object table, and a room is the unit that
    /// streams in and out — so a runner belongs to the room that created it. The clock, the campaign and
    /// the HUD do not. This type is therefore a registry: rooms register on
    /// <c>RoomVisualController.Initialize</c> and unregister in <c>ClearVisuals</c>, and a script whose
    /// room has gone away stops being stepped, which is the behaviour a streamed-out cutscene must
    /// have.</para>
    ///
    /// <para><b>The static accessor follows <c>CampaignManager.Current</c>, for the same reason.</b>
    /// <c>RoomVisualController</c> is added with <c>AddComponent</c> by <c>MapBridge</c>, so VContainer
    /// never observes it and <c>[Inject]</c> never runs on it
    /// (<c>CampaignManager.cs:26-38</c>). It therefore cannot be handed this service by the container,
    /// and a static over a container-lifetime instance is the pattern this project already uses. Set in
    /// <see cref="Initialize"/> and cleared in <see cref="Dispose"/>, so it tracks the container's
    /// lifetime rather than a stray construction.</para>
    /// </summary>
    public sealed class ScriptSystem : IAutoRegisteredSimTickable, IInitializable, IDisposable, ITickable
    {
        /// <summary>The live instance, for scene components the container cannot inject.</summary>
        public static ScriptSystem Current { get; private set; }

        private readonly IScriptPresentationHost _presentation;
        private readonly IScriptPlayerHost _player;
        private readonly ScriptInputProbe _input;
        private readonly Action<string> _report;

        /// <summary>
        /// Runners currently on the clock, newest last. A list and not a set: a runner is a reference
        /// type with no meaningful equality, and the same runner must never appear twice — see
        /// <see cref="Register"/>.
        /// </summary>
        private readonly List<ScriptRunner> _runners = new List<ScriptRunner>();

        /// <summary>Runners registered minus runners disposed. The readback for "the system exists and
        /// is ticking something".</summary>
        public int RegisteredRunnerCount => _runners.Count;

        /// <summary>How many scripts are in flight across every room. For tests and the debug
        /// overlay.</summary>
        public int RunningScriptCount
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _runners.Count; i++)
                {
                    if (_runners[i] != null) total += _runners[i].RunningCount;
                }

                return total;
            }
        }

        public int TickOrder => SimTickOrder.Scripts;

        /// <summary>
        /// The authored-script catalogue, wrapped as the runner's lookup seam. Never null — a container
        /// built without a catalogue still produces a host that reports each unresolvable id by name,
        /// rather than throwing on the room-build path.
        /// </summary>
        public IScriptCatalogHost Catalog { get; }

        public ScriptSystem(
            CampaignCatalog catalog,
            IScriptPresentationHost presentation,
            IScriptPlayerHost player,
            ScriptInputProbe input,
            Action<string> report = null)
        {
            _presentation = presentation;
            _player = player;
            _input = input;
            _report = report;
            Catalog = new ScriptCatalogHost(catalog, report);
        }

        /// <summary>
        /// The frame-boundary half of the input seam — reads the device into the probe's latches.
        ///
        /// <para><b>Why this type is on both clocks and that is not a mistake.</b> It is an
        /// <see cref="ITickable"/> for exactly one purpose: input must be gathered on the frame and
        /// consumed on the tick, because <see cref="ISimTickable"/> forbids a tick from reading
        /// <c>Input</c> (<c>ISimTickable.cs:10-29</c>). Everything the simulation does lives in
        /// <see cref="SimTick"/>; this method touches no game state and writes only the two latches
        /// <see cref="ScriptInputProbe.Poll"/> owns. Splitting it into a second class would add a
        /// container registration to express "this one line runs per frame".</para>
        /// </summary>
        public void Tick() => _input?.Poll();

        public void Initialize() => Current = this;

        public void Dispose()
        {
            if (ReferenceEquals(Current, this)) Current = null;
            _runners.Clear();
        }

        /// <summary>
        /// Builds a runner bound to <paramref name="objects"/> — the per-room half of the vocabulary.
        ///
        /// <para><b>The campaign and quest hosts are passed as factories, not as values.</b> A room is
        /// built while the world is assembled, which can be earlier than <c>CampaignManager</c> exists;
        /// capturing a null here would make <c>upland</c>, <c>trigger</c> and <c>gotoland</c> silent
        /// no-ops for the whole session while every other command worked. See
        /// <see cref="ScriptRunner"/>'s <c>_campaign</c> field.</para>
        /// </summary>
        /// <param name="objects">The per-room object host.</param>
        /// <param name="campaign">
        /// Overrides the campaign factory. A caller that already holds an <c>ILandScriptHost</c> — a
        /// fixture with a fake, or a scene component with an injected one — must win over
        /// <c>CampaignManager.Current</c>, which is a process-wide static that may belong to another test.
        /// </param>
        public ScriptRunner CreateRunner(IScriptObjectHost objects, Func<ILandScriptHost> campaign = null)
        {
            return new ScriptRunner(
                campaign: campaign ?? (() => CampaignManager.Current),
                quests: () => CampaignManager.Current,
                presentation: _presentation,
                objects: objects,
                player: _player,
                input: _input,
                catalog: Catalog,
                report: _report);
        }

        /// <summary>
        /// Puts <paramref name="runner"/> on the clock. Idempotent: a room re-initialized without an
        /// intervening <c>ClearVisuals</c> must not end up stepping its scripts twice, which would run
        /// every <c>@t</c> delay at double rate and be blamed on the timing model.
        /// </summary>
        public void Register(ScriptRunner runner)
        {
            if (runner == null) return;
            for (int i = 0; i < _runners.Count; i++)
            {
                if (ReferenceEquals(_runners[i], runner)) return;
            }

            _runners.Add(runner);
        }

        /// <summary>Removes <paramref name="runner"/> from the clock. Called when its room is torn
        /// down.</summary>
        public void Unregister(ScriptRunner runner)
        {
            if (runner == null) return;
            for (int i = 0; i < _runners.Count; i++)
            {
                if (ReferenceEquals(_runners[i], runner))
                {
                    _runners.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Advances every registered runner by one tick. Iterated <b>backwards</b> because a script can
        /// finish (and be removed) or start another script within its own step, and because a room torn
        /// down mid-step may unregister the very runner being stepped.
        /// </summary>
        public void SimTick(int tickIndex)
        {
            for (int i = _runners.Count - 1; i >= 0; i--)
            {
                ScriptRunner runner = _runners[i];
                if (runner == null)
                {
                    _runners.RemoveAt(i);
                    continue;
                }

                runner.SimTick();
            }
        }
    }
}
