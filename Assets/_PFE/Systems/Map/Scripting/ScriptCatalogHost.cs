using System;
using System.Collections.Generic;
using System.Globalization;
using PFE.Data.Definitions.Campaign;
using UnityEngine;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// The port of AS3 <c>Game.getScript(id, area)</c> (<c>Game.as:722-750</c>): resolves an authored
    /// <c>&lt;scr id="…"&gt;</c> entry and hands the runner the same action shape an inline
    /// <c>&lt;scr&gt;</c> block produces.
    ///
    /// <para><b>Why an adapter rather than a second interpreter entry point.</b> An authored script and
    /// an inline script are the same thing to the oracle — <c>new Script(node, land, area)</c> in both
    /// cases (<c>Area.as:139-158</c>), differing only in whether the node came from
    /// <c>GameData.d.scr</c> or from the room. Giving the runner two action types would mean two
    /// switches to keep in step, and this port already has one scar from exactly that: an <c>exit</c>
    /// branch that existed in a second copy of the vocabulary and could never fire.</para>
    ///
    /// <para><b>The field rename is where a silent bug would live.</b> The imported
    /// <see cref="CampaignScriptAction"/> uses descriptive names (<c>action</c>, <c>value</c>,
    /// <c>numericParam</c>, <c>delaySeconds</c>) while the map-object shape uses the oracle's own
    /// attribute names (<c>act</c>, <c>val</c>, <c>n</c>, <c>t</c>). A mapping that swapped
    /// <c>n</c> and <c>opt1</c> would still compile and still run — it would just make
    /// <c>gotoland @n="1"</c> take the plain branch, which is the kind of defect that only shows up as
    /// "the entrance is wrong". The mapping is therefore asserted field-by-field in
    /// <c>ScriptCatalogHostTests</c> rather than eyeballed.</para>
    /// </summary>
    public sealed class ScriptCatalogHost : IScriptCatalogHost
    {
        private readonly CampaignCatalog _catalog;
        private readonly Action<string> _report;

        /// <summary>Ids already reported as unresolvable, so a room that is entered repeatedly does not
        /// repeat the warning for ever.</summary>
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        public ScriptCatalogHost(CampaignCatalog catalog, Action<string> report = null)
        {
            _catalog = catalog;
            _report = report ?? (message => Debug.LogWarning(message));
        }

        public bool TryGetScript(string scriptId, out MapObjectScriptData script)
        {
            script = null;
            if (string.IsNullOrEmpty(scriptId)) return false;

            CampaignScriptDefinition definition = _catalog != null ? _catalog.GetScript(scriptId) : null;
            if (definition == null)
            {
                Report(scriptId);
                return false;
            }

            script = Convert(definition);
            return script != null && script.actions.Count > 0;
        }

        private void Report(string scriptId)
        {
            if (_reported.Add(scriptId))
            {
                _report($"[ScriptCatalogHost] no authored script carries the id '{scriptId}'. The room " +
                        "data references it (an area's `scr`/`scrout` attribute), so whatever that area " +
                        "was meant to do will not happen. Either the script was never imported, or the " +
                        "id in the room does not match the one in the catalogue.");
            }
        }

        /// <summary>
        /// Maps one authored script onto the map-object action shape. Public and static so the field
        /// mapping is directly assertable without a catalogue asset.
        /// </summary>
        public static MapObjectScriptData Convert(CampaignScriptDefinition definition)
        {
            if (definition == null) return null;
            return Convert(definition.actions);
        }

        /// <summary>
        /// The same mapping for an action list that has no <c>id</c> — the land-level
        /// <c>&lt;scr eve='take' item='…'&gt;</c> scripts (<see cref="LandItemScript"/>).
        ///
        /// <para><b>Why the overload rather than a second converter.</b> The oracle builds a
        /// <c>fe.serv.Script</c> from the node in both cases — <c>new Script(node, land)</c> for an item
        /// script (<c>Land.as:146</c>) and <c>new Script(node, land, area)</c> for an authored one
        /// (<c>Area.as:151</c>) — and the interpreter never learns which it was. Two converters would be
        /// two places for the <c>n</c>/<c>opt1</c> mapping to drift, which is the exact failure this
        /// class was written to make impossible.</para>
        /// </summary>
        public static MapObjectScriptData Convert(IList<CampaignScriptAction> actions)
        {
            var script = new MapObjectScriptData
            {
                // The event name is not part of the authored-script vocabulary — `getScript` returns a
                // script that is *started*, not one that is selected by an event. Empty is therefore the
                // honest value and is what the runner treats as "run it".
                eventName = string.Empty,
                actions = new List<MapObjectScriptActionData>(),
            };

            if (actions == null) return script;

            for (int i = 0; i < actions.Count; i++)
            {
                CampaignScriptAction action = actions[i];
                if (action == null) continue;

                script.actions.Add(new MapObjectScriptActionData
                {
                    act = action.action,
                    targ = action.target,
                    val = action.value,
                    n = action.numericParam.ToString(CultureInfo.InvariantCulture),
                    // "R" so a round trip through the string is exact: `analiz` converts `@t` to frames
                    // once (Script.as:91), and a lossy decimal would move an authored 0.2 s off its
                    // 6-frame boundary.
                    t = action.delaySeconds.ToString("R", CultureInfo.InvariantCulture),
                    opt1 = action.opt1.ToString(CultureInfo.InvariantCulture),
                    opt2 = action.opt2.ToString(CultureInfo.InvariantCulture),
                });
            }

            return script;
        }
    }
}
