using System;
using System.Collections.Generic;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// The per-land <em>runtime</em> state that AS3 keeps on <c>fe.loc.LandAct</c>
    /// (<c>LandAct.as:90-101</c>) and persists in <c>LandAct.save/load</c>
    /// (<c>LandAct.as:267-301</c>, stored as <c>{cp, st, access, visited, passed}</c>).
    ///
    /// <para><b>Why this is not on <c>LandDefinition</c>:</b> that is a <c>ScriptableObject</c> — one
    /// shared instance per land, and the values are per-save. Putting <c>landStage</c> there would make
    /// two saves, or two lands, share a depth counter.</para>
    /// </summary>
    [Serializable]
    public sealed class LandRuntimeState
    {
        /// <summary>The land this state belongs to.</summary>
        public string landId = "";

        /// <summary>AS3 <c>landStage</c> (<c>st</c>) — the descent counter for this land.</summary>
        public int landStage;

        /// <summary>AS3 <c>upStage</c> — set by <c>upLandLevel</c> to prevent a double increment
        /// (<c>Game.as:474-481</c>).</summary>
        public bool upStage;

        /// <summary>AS3 <c>lastCpCode</c> — the checkpoint code for "return to last checkpoint".</summary>
        public string lastCpCode = "";

        /// <summary>AS3 <c>access</c> — unlocked on the travel map by the <c>openland</c> script action
        /// (<c>Script.as:458-468</c>).</summary>
        public bool access;

        /// <summary>AS3 <c>visited</c> — set on the first entry to a non-procedural land
        /// (<c>Game.as:386-389</c>).</summary>
        public bool visited;

        /// <summary>AS3 <c>passed</c> — set by the <c>passed</c> script action (<c>Script.as:469-472</c>).</summary>
        public bool passed;

        public LandRuntimeState Clone()
        {
            return new LandRuntimeState
            {
                landId = landId,
                landStage = landStage,
                upStage = upStage,
                lastCpCode = lastCpCode,
                access = access,
                visited = visited,
                passed = passed,
            };
        }
    }

    /// <summary>Serialisable form of one land's runtime state. Field names are short because AS3's are
    /// (<c>st</c>, <c>cp</c>) and the save file is diffed against the oracle's.</summary>
    [Serializable]
    public struct LandRuntimeStateSaveData
    {
        public string id;
        public int st;
        public bool up;
        public string cp;
        public bool acc;
        public bool vis;
        public bool pass;
    }

    /// <summary>
    /// One <see cref="LandRuntimeState"/> per land id. This is the runtime half of AS3's
    /// <c>Game.lands[id]</c> plus the <c>LandAct</c> save payload.
    ///
    /// <para>Owned by <c>CampaignManager</c>. <b>Never</b> let two lands share an instance — a fresh
    /// state per id is what stops the second land you visit inheriting the first's depth
    /// (<c>docs/LandGameplayLoop/05_VERIFICATION_AND_QUIRKS.md</c> Q10).</para>
    /// </summary>
    public sealed class LandRuntimeStateRegistry
    {
        private readonly Dictionary<string, LandRuntimeState> _states =
            new Dictionary<string, LandRuntimeState>(StringComparer.OrdinalIgnoreCase);

        public int Count => _states.Count;

        /// <summary>Get the state for <paramref name="landId"/>, creating an empty one on first use.</summary>
        public LandRuntimeState Get(string landId)
        {
            if (string.IsNullOrEmpty(landId)) return new LandRuntimeState();

            if (!_states.TryGetValue(landId, out LandRuntimeState state))
            {
                state = new LandRuntimeState { landId = landId };
                _states[landId] = state;
            }
            return state;
        }

        public bool TryGet(string landId, out LandRuntimeState state)
        {
            if (string.IsNullOrEmpty(landId))
            {
                state = null;
                return false;
            }
            return _states.TryGetValue(landId, out state);
        }

        public void Clear() => _states.Clear();

        public IEnumerable<LandRuntimeState> All => _states.Values;

        /// <summary>
        /// AS3 <c>Game.upLandLevel()</c> (<c>Game.as:474-481</c>) — the "go lower" step.
        /// Increments <c>landStage</c> <b>once</b> per land: the first call bumps it and sets
        /// <c>upStage</c>; every later call is a no-op until the flag is cleared.
        /// </summary>
        /// <returns><c>true</c> if this call incremented <c>landStage</c>.</returns>
        public bool UpLandLevel(string landId)
        {
            LandRuntimeState state = Get(landId);
            bool incremented = false;
            if (!state.upStage)
            {
                state.landStage++;
                incremented = true;
            }
            state.upStage = true;
            return incremented;
        }

        /// <summary>AS3 <c>Script.as:458-468</c> — the <c>openland</c> action.</summary>
        public void SetAccess(string landId, bool value)
        {
            Get(landId).access = value;
        }

        /// <summary>AS3 <c>Game.as:386-389</c> — a land becomes visible after its first entry.</summary>
        public void MarkVisited(string landId)
        {
            Get(landId).visited = true;
        }

        /// <summary>AS3 <c>Script.as:469-472</c> — the <c>passed</c> action.</summary>
        public void MarkPassed(string landId, bool value = true)
        {
            Get(landId).passed = value;
        }

        /// <summary>
        /// AS3 <c>Game.enterToCurLand()</c> (<c>Game.as:396-399</c>) — clears <c>upStage</c> when the land
        /// is (re-)entered, which is what lets the <em>next</em> run's <c>upland</c> increment again.
        /// </summary>
        public void ResetUpStage(string landId)
        {
            LandRuntimeState state = Get(landId);
            if (state.upStage) state.upStage = false;
        }

        public LandRuntimeStateSaveData[] ToSaveData()
        {
            var list = new List<LandRuntimeStateSaveData>(_states.Count);
            foreach (LandRuntimeState s in _states.Values)
            {
                list.Add(new LandRuntimeStateSaveData
                {
                    id = s.landId,
                    st = s.landStage,
                    up = s.upStage,
                    cp = s.lastCpCode,
                    acc = s.access,
                    vis = s.visited,
                    pass = s.passed,
                });
            }
            return list.ToArray();
        }

        public void LoadFromSaveData(LandRuntimeStateSaveData[] data)
        {
            Clear();
            if (data == null) return;

            foreach (LandRuntimeStateSaveData d in data)
            {
                if (string.IsNullOrEmpty(d.id)) continue;
                _states[d.id] = new LandRuntimeState
                {
                    landId = d.id,
                    landStage = d.st,
                    upStage = d.up,
                    lastCpCode = d.cp ?? "",
                    access = d.acc,
                    visited = d.vis,
                    passed = d.pass,
                };
            }
        }
    }
}
