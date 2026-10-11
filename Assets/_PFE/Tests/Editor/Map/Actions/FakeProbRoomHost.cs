using System.Collections.Generic;
using UnityEngine;
using PFE.Systems.Map.Actions;

namespace PFE.Tests.Editor.Map.Actions
{
    /// <summary>
    /// Test double for <see cref="IProbRoomHost"/>.
    ///
    /// <para><b>It records the prob id and the door position, not just "it was called".</b> The two facts
    /// the dispatcher is responsible for are <i>which</i> prob the object named and <i>that the door's own
    /// position</i> was handed over rather than the player's — AS3 prefers the door's
    /// (<c>Interact.as:1565</c> passes <c>this.owner.X, this.owner.Y</c> into
    /// <c>Land.gotoProb</c>'s <c>param2</c>/<c>param3</c>). A double that only counted calls would let a
    /// regression that passed <c>Vector3.zero</c> through stay green.</para>
    /// </summary>
    internal sealed class FakeProbRoomHost : IProbRoomHost
    {
        /// <summary>Every prob id the dispatcher asked to enter, in order.</summary>
        public readonly List<string> EnteredProbIds = new List<string>();

        /// <summary>Every door position handed over, in order — parallel to <see cref="EnteredProbIds"/>.</summary>
        public readonly List<Vector3> EnteredFromPositions = new List<Vector3>();

        public int ReturnCalls;

        /// <summary>AS3 <c>ativateLoc</c>'s guard: a prob whose room was never built cannot be entered.</summary>
        public bool CanEnterProbValue = true;

        /// <summary>Whether a return point is currently saved — AS3's <c>landProb != ""</c>.</summary>
        public bool IsInProbRoomValue;

        /// <summary>What an entry attempt reports. <c>false</c> is the oracle's refusal.</summary>
        public bool EnterResult = true;

        /// <summary>What a return attempt reports.</summary>
        public bool ReturnResult = true;

        public bool CanEnterProb => CanEnterProbValue;

        public bool IsInProbRoom => IsInProbRoomValue;

        public bool TryEnterProb(string probId, Vector3 doorWorldPosition)
        {
            EnteredProbIds.Add(probId);
            EnteredFromPositions.Add(doorWorldPosition);
            return EnterResult;
        }

        public bool TryReturnFromProb()
        {
            ReturnCalls++;
            return ReturnResult;
        }
    }
}
