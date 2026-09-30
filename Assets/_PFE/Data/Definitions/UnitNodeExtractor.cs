using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Slices a unit's node text out of <c>AllData.as</c> — the one piece of XML handling that
    /// <c>UnitDataImporter</c> cannot get wrong without silently corrupting whole assets.
    ///
    /// <para><b>Why this is a type and not three lines inside the importer.</b> The naive form —
    /// "find the next <c>&lt;/unit&gt;</c>" — is wrong for a self-closing tag, and it was wrong here for
    /// real. <c>AllData.as</c> contains <b>seven</b> self-closing marker units with no children:</para>
    ///
    /// <list type="table">
    /// <item><term>pony</term><description>swallowed raider</description></item>
    /// <item><term>monster</term><description>swallowed alicorn</description></item>
    /// <item><term>other</term><description>swallowed bloodwing</description></item>
    /// <item><term>robot</term><description>swallowed bigrobot <i>and</i> robobrain</description></item>
    /// <item><term>bigrobot</term><description>swallowed robobrain</description></item>
    /// <item><term>smallrobot</term><description>swallowed spritebot</description></item>
    /// <item><term>turret</term><description>swallowed turret0</description></item>
    /// </list>
    ///
    /// <para>Each of those ids appears <b>exactly once</b> in the file, so there was no later, correct
    /// node to overwrite the damage: <c>pony.asset</c> was written with raider's physics, combat, vis and
    /// animations, and <c>turret.asset</c> with turret0's. Nothing failed, and the only visible symptom
    /// was an aggregate count that was too high by exactly two — which is why the count is checked
    /// against the oracle rather than eyeballed.</para>
    ///
    /// <para>It lives in <c>PFE.Core</c> because <c>PFE.Tests</c> references <c>PFE.Core</c> and not
    /// <c>PFE.Editor</c>, so an extraction living in the importer could not be regression-tested.</para>
    /// </summary>
    public static class UnitNodeExtractor
    {
        /// <summary>The opening tag of a unit. Matches self-closing and paired forms alike.</summary>
        public static readonly Regex UnitOpenTag =
            new Regex(@"<unit\s+id='([^']+)'([^>]*)(?:\s*/)?>", RegexOptions.Compiled);

        /// <summary>
        /// True when <paramref name="tagText"/> ends in <c>/&gt;</c>, i.e. the unit has no body.
        /// Checked on the matched tag rather than on the captured attributes, because the attribute
        /// group is greedy and swallows a trailing slash.
        /// </summary>
        public static bool IsSelfClosing(string tagText)
        {
            return !string.IsNullOrEmpty(tagText) && tagText.TrimEnd().EndsWith("/>");
        }

        /// <summary>
        /// The node text for one unit: the tag alone when it is self-closing, otherwise the tag through
        /// its closing <c>&lt;/unit&gt;</c>.
        /// </summary>
        public static string ExtractNode(string content, int startIndex, string tagText)
        {
            if (IsSelfClosing(tagText))
            {
                return tagText;
            }

            int end = content.IndexOf("</unit>", startIndex);
            if (end == -1)
            {
                int nextUnit = content.IndexOf("<unit", startIndex + 1);
                end = nextUnit != -1 ? nextUnit : content.Length;
            }
            else
            {
                end += "</unit>".Length;
            }

            return end > startIndex ? content.Substring(startIndex, end - startIndex) : tagText;
        }

        /// <summary>
        /// Every unit in the file, keyed by id, in document order. A repeated id overwrites — none
        /// repeat today, and if one ever does, the later node is the one AS3's
        /// <c>AllData.d.unit.(@id == mid)[0]</c> would <i>not</i> pick, so a duplicate is worth a
        /// warning rather than a silent overwrite. <paramref name="duplicateIds"/> reports them.
        /// </summary>
        public static Dictionary<string, string> ExtractAllNodes(
            string content, List<string> duplicateIds = null)
        {
            var nodes = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(content))
            {
                return nodes;
            }

            foreach (Match m in UnitOpenTag.Matches(content))
            {
                string id = m.Groups[1].Value;
                if (nodes.ContainsKey(id))
                {
                    duplicateIds?.Add(id);
                }

                nodes[id] = ExtractNode(content, m.Index, m.Value);
            }

            return nodes;
        }
    }
}
