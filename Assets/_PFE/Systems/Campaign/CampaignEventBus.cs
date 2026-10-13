using System;
using System.Collections.Generic;
using MessagePipe;
using PFE.Core.Messages;
using UnityEngine;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// The campaign's event surface, and the one place a re-entrant dispatch is bounded.
    ///
    /// <para><b>Why the campaign needs this at all.</b> <c>LandTransitionMessage</c> /
    /// <c>LandBuildRequestMessage</c> are the project's model seam: a state owner publishes, a builder
    /// subscribes, neither knows the other (<c>Core/Messages/TriggerMessages.cs</c>). The campaign had no
    /// equivalent for "a trigger changed", "a quest advanced", "a checkpoint activated" — it mutated its
    /// own collections and told nobody, so nothing could bind a hook to it. This class is that surface.</para>
    ///
    /// <para><b>The hazard this class exists to close.</b> Once Lua or an authored script can run *on* a
    /// campaign event, a cycle becomes expressible: <c>set_trigger</c> fires
    /// <see cref="TriggerChangedMessage"/> → a subscriber runs a script → that script sets a trigger →
    /// …. The oracle has no such loop, because its scripts are synchronous command lists rather than
    /// event handlers, so there is no oracle behaviour to copy here — only a bound to impose. The
    /// instruction quota in <c>MoonSharpScriptEngine</c> bounds one *call*; it does not bound a cycle
    /// across calls.</para>
    ///
    /// <para><b>How it is bounded.</b> Two rules, both visible in a log:</para>
    /// <list type="number">
    /// <item>a dispatch that would nest deeper than <see cref="MaxDispatchDepth"/> is <b>queued</b>
    /// instead of published, and reported once by name — so the cycle makes progress in bounded steps
    /// rather than blowing the stack;</item>
    /// <item>the queue is drained only when the outermost dispatch has returned, so an event raised
    /// <i>during</i> a dispatch is never delivered re-entrantly.</item>
    /// </list>
    ///
    /// <para><b>Null publishers are tolerated.</b> Headless hosts and tests construct the campaign with no
    /// container, and a missing broker must degrade to "the event is dropped" rather than to a
    /// <c>NullReferenceException</c> thrown out of gameplay. That is the project's convention
    /// (<c>ObjectActionDispatcher</c>: a missing host is reported, not thrown).</para>
    /// </summary>
    public sealed class CampaignEventBus
    {
        /// <summary>
        /// How deep a chain of "an event handler raises another event" may nest before the extra events
        /// are deferred to the queue.
        ///
        /// <para>Eight is a judgement, not an oracle value — the oracle has no events at all. It is deep
        /// enough for the shapes that are legitimate (a quest closing a sub, which closes the parent,
        /// which runs its end script) and shallow enough that a cycle is reported within a frame rather
        /// than discovered as a stack overflow.</para>
        /// </summary>
        public const int MaxDispatchDepth = 8;

        private readonly IPublisher<TriggerChangedMessage> _triggerChanged;
        private readonly IPublisher<QuestStateChangedMessage> _questStateChanged;
        private readonly IPublisher<LandEnteredMessage> _landEntered;
        private readonly IPublisher<CheckpointActivatedMessage> _checkpointActivated;

        /// <summary>Events deferred because the dispatch stack was already at the cap.</summary>
        private readonly Queue<Action> _deferred = new Queue<Action>();

        private int _depth;

        /// <summary>The deepest nesting actually reached, so a play-test can tell "the guard fired" from
        /// "the guard was never needed".</summary>
        public int PeakDepth { get; private set; }

        /// <summary>How many events were deferred by the depth cap. Non-zero means a cycle exists.</summary>
        public int DeferredCount { get; private set; }

        /// <summary>Whether the cap has ever been hit.</summary>
        public bool DepthCapReached { get; private set; }

        private readonly Action<string> _report;

        public CampaignEventBus(
            IPublisher<TriggerChangedMessage> triggerChanged = null,
            IPublisher<QuestStateChangedMessage> questStateChanged = null,
            IPublisher<LandEnteredMessage> landEntered = null,
            IPublisher<CheckpointActivatedMessage> checkpointActivated = null,
            Action<string> report = null)
        {
            _triggerChanged = triggerChanged;
            _questStateChanged = questStateChanged;
            _landEntered = landEntered;
            _checkpointActivated = checkpointActivated;
            _report = report ?? (message => Debug.LogWarning(message));
        }

        public void PublishTriggerChanged(string name, TriggerValue oldValue, TriggerValue newValue)
        {
            if (string.IsNullOrEmpty(name)) return;

            var message = new TriggerChangedMessage(name, oldValue, newValue);
            Dispatch(() => _triggerChanged?.Publish(message), $"TriggerChangedMessage('{name}')");
        }

        public void PublishQuestStateChanged(string questId, int oldState, int newState, string stage = null)
        {
            if (string.IsNullOrEmpty(questId)) return;

            var message = new QuestStateChangedMessage(questId, oldState, newState, stage);
            Dispatch(() => _questStateChanged?.Publish(message),
                     $"QuestStateChangedMessage('{questId}', {oldState} -> {newState})");
        }

        public void PublishLandEntered(string landId, string previousLandId, int stage)
        {
            if (string.IsNullOrEmpty(landId)) return;

            var message = new LandEnteredMessage(landId, previousLandId, stage);
            Dispatch(() => _landEntered?.Publish(message), $"LandEnteredMessage('{landId}')");
        }

        public void PublishCheckpointActivated(string landId, int roomX, int roomY, int roomZ, string code)
        {
            if (string.IsNullOrEmpty(landId)) return;

            var message = new CheckpointActivatedMessage(landId, roomX, roomY, roomZ, code ?? string.Empty);
            Dispatch(() => _checkpointActivated?.Publish(message),
                     $"CheckpointActivatedMessage('{landId}')");
        }

        /// <summary>
        /// Deliver one event, or defer it when the stack is already at the cap.
        ///
        /// <para><paramref name="publish"/> is the caller's own typed publish lambda. The bus deliberately
        /// does not switch on the message type to find a publisher: a single generic path that did so
        /// would need reflection or a type map, and would make "which broker does this go to" a runtime
        /// lookup instead of a line of code.</para>
        /// </summary>
        private void Dispatch(Action publish, string describe)
        {
            if (_depth >= MaxDispatchDepth)
            {
                DeferredCount++;
                if (!DepthCapReached)
                {
                    DepthCapReached = true;
                    _report(
                        $"[CampaignEventBus] Dispatch depth reached {MaxDispatchDepth} on {describe}; " +
                        "further events this frame are queued and delivered after the stack unwinds. " +
                        "This means an event handler is raising campaign events — most likely a Lua " +
                        "hook or an authored script that sets a trigger on TriggerChanged. The loop is " +
                        "bounded here, not solved: find the handler.");
                }

                _deferred.Enqueue(() => Dispatch(publish, describe));
                return;
            }

            _depth++;
            if (_depth > PeakDepth) PeakDepth = _depth;

            try
            {
                publish();
            }
            finally
            {
                _depth--;
            }

            // Only the outermost frame drains. A nested publish returning early must not deliver the
            // queue, or "queued rather than re-entered" would not be true.
            if (_depth == 0) DrainDeferred();
        }

        /// <summary>
        /// Deliver what the cap deferred. Public so a test can assert the queue drains in order, and so a
        /// host that ticks can drain at a frame boundary rather than mid-callback.
        /// </summary>
        public void DrainDeferred()
        {
            // The count is read before the loop because a deferred event can itself defer another; the
            // loop must not run for ever on a genuinely infinite cycle.
            int remaining = _deferred.Count;
            while (remaining-- > 0 && _deferred.Count > 0)
            {
                Action next = _deferred.Dequeue();
                next?.Invoke();
            }
        }

        /// <summary>How many events are still queued.</summary>
        public int PendingCount => _deferred.Count;
    }
}
