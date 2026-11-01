using System;
using System.Collections.Generic;
using PFE.Systems.Items;

namespace PFE.Systems.Crafting
{
    /// <summary>
    /// One craftable recipe — the port of a <c>&lt;item tip='scheme'&gt;</c> row as
    /// <c>fe.inter.PipPageWork</c> reads it.
    ///
    /// <para><b>Ids only; no Unity types.</b> The table is built from <c>AllData.as</c> at runtime
    /// (see <see cref="CraftingRecipeTable"/>) so crafting does not depend on an importer run, which is
    /// an owner-only step here. <c>docs/CraftingAndRepair/00_INDEX.md</c> §2 D1 records that as a
    /// deliberate divergence from the asset-per-row shape the rest of the content uses.</para>
    ///
    /// <para><b><see cref="OutputId"/> is the scheme id minus its <c>s_</c> prefix</b> — the oracle
    /// does exactly that (<c>PipPageWork.as:89</c>: <c>wid = s.substr(2)</c>) and then asks the three
    /// inventory tables in turn whether they know it. The prefix is therefore structural, not
    /// cosmetic: <c>s_</c> is how a scheme names its product.</para>
    /// </summary>
    public readonly struct CraftingRecipe
    {
        /// <summary><c>@id</c>, e.g. <c>s_hsword</c>. The id the bag holds and the list keys on.</summary>
        public readonly string SchemeId;

        /// <summary>The product's id — <see cref="SchemeId"/> without its <c>s_</c> prefix.</summary>
        public readonly string OutputId;

        /// <summary><c>@work</c> — <c>work</c>, <c>lab</c>, <c>stove</c> or <c>expl</c>.</summary>
        public readonly string Station;

        /// <summary><c>@skill</c> — the single skill gate (<c>repair</c>, <c>science</c>,
        /// <c>survival</c>, <c>medic</c>, <c>magic</c>, <c>explosives</c>). Empty when the row has none,
        /// which no shipped row does.</summary>
        public readonly string Skill;

        /// <summary><c>@lvl</c> — the skill level required. 0 means "no level", not "no gate".</summary>
        public readonly int SkillLevel;

        /// <summary><c>@kol</c> — how many are produced, before the perk doubling. Defaults to 1.</summary>
        public readonly int OutputCount;

        /// <summary><c>@perk</c> — the perk that doubles the output. Only <c>potmaster</c> exists in the
        /// shipped data (10 of the 75 rows).</summary>
        public readonly string Perk;

        /// <summary>The <c>&lt;craft&gt;</c> children, in file order.</summary>
        public readonly CraftMaterialRow[] Materials;

        public CraftingRecipe(string schemeId, string outputId, string station, string skill,
                              int skillLevel, int outputCount, string perk, CraftMaterialRow[] materials)
        {
            SchemeId = schemeId ?? string.Empty;
            OutputId = outputId ?? string.Empty;
            Station = station ?? string.Empty;
            Skill = skill ?? string.Empty;
            SkillLevel = skillLevel;
            OutputCount = outputCount < 1 ? 1 : outputCount;
            Perk = perk ?? string.Empty;
            Materials = materials ?? Array.Empty<CraftMaterialRow>();
        }

        /// <summary>
        /// Whether this row is gated on a skill at all — <c>@skill</c> present <b>and</b>
        /// <c>@lvl &gt; 0</c>.
        ///
        /// <para><b>Not "both attributes present", which is what the oracle's guard literally tests.</b>
        /// <c>PipPageWork.as:85</c> and <c>:374</c> both read
        /// <c>Boolean(node.@skill.length()) &amp;&amp; Boolean(node.@lvl.length()) &amp;&amp; getSkillLevel(...) &lt; node.@lvl</c>.
        /// When <c>@lvl='0'</c> the comparison is <c>level &lt; 0</c>, which no skill level satisfies, so
        /// the guard is inert — the row is craftable by anyone. Testing <c>SkillLevel &gt; 0</c> instead of
        /// "attribute present" is therefore the same behaviour with less to get wrong, and
        /// <c>Check</c> never has to ask the caller for a level it will not use.</para>
        ///
        /// <para><b>Three of the 75 rows are inert</b>: <c>s_arson</c>, <c>s_rollup</c>, <c>s_frmeat</c>
        /// all carry <c>lvl='0'</c>. Do not assume "every scheme has a gate" — that assumption costs a
        /// red test in <c>CraftingRulesTests.ParsingTheRealAllData_…</c>, which is where it belongs.</para>
        /// </summary>
        public bool HasSkillGate => Skill.Length > 0 && SkillLevel > 0;
    }

    /// <summary>
    /// The recipe table and the station rules — the port of the filter at <c>PipPageWork.as:75-146</c>
    /// and the normalisation at <c>:40-47</c>.
    ///
    /// <para><b>Station matching has one quirk and it is the reason explosives are reachable at all.</b>
    /// There is no world object with <c>allact='expl'</c>: the three crafting objects are <c>work</c>,
    /// <c>himlab</c> and <c>stove</c> (<c>AllData.as:5013-5015</c>). Explosives are built <i>at the
    /// workbench</i>, which the oracle expresses as an extra clause —
    /// <c>node.@work == "expl" &amp;&amp; pip.workTip == "work"</c> (<c>:82</c>). Drop that clause and 11
    /// of the 75 recipes become unreachable in a way no menu would show.</para>
    /// </summary>
    public static class CraftingRecipeTable
    {
        /// <summary>The scheme id prefix. <c>s_</c> is stripped to find the product
        /// (<c>PipPageWork.as:89</c>).</summary>
        public const string SchemePrefix = "s_";

        /// <summary>The workbench station id — the only one that also lists <c>expl</c>.</summary>
        public const string WorkStation = "work";

        /// <summary>AS3 <c>pip.workTip</c> values that are really aliases of another station
        /// (<c>PipPageWork.as:40-47</c>). The page rewrites them before anything else runs.</summary>
        public static string NormaliseStation(string workTip)
        {
            if (workTip == "mworklab") return "lab";
            if (workTip == "mworkexpl") return "expl";
            return workTip ?? string.Empty;
        }

        /// <summary>
        /// Whether a station's page lists a recipe — <c>PipPageWork.as:82</c>, verbatim:
        /// <code>
        /// node.@work.length() == 0 || node.@work == pip.workTip
        ///     || (node.@work == "expl" &amp;&amp; pip.workTip == "work")
        /// </code>
        /// The empty-station arm is kept even though no shipped row needs it: a row without
        /// <c>@work</c> is listed everywhere in the oracle, and that is a data-shape decision, not an
        /// unreachable branch to delete.
        /// </summary>
        public static bool StationAccepts(string recipeStation, string workTip)
        {
            string station = NormaliseStation(workTip);
            if (station == "mworkbench") return false;   // the portable bench only repairs (:49-53)
            if (string.IsNullOrEmpty(recipeStation)) return true;
            if (recipeStation == station) return true;
            return recipeStation == "expl" && station == WorkStation;
        }

        /// <summary>
        /// How many units one craft produces — <c>@kol</c> (default 1) doubled by <c>potmaster</c>
        /// (<c>PipPageWork.as:428-436</c>).
        ///
        /// <para><b>The perk test is by name, not by level.</b> <c>@perk == "potmaster" &amp;&amp;
        /// gg.pers.potmaster</c> — <c>Pers.potmaster</c> is an int (a perk level) used as a boolean, so
        /// any level doubles, and no other perk is consulted even though the attribute could name one.
        /// Ten rows carry it, all in the lab.</para>
        /// </summary>
        public static int OutputCount(in CraftingRecipe recipe, bool hasPotMaster)
        {
            int count = recipe.OutputCount < 1 ? 1 : recipe.OutputCount;
            if (hasPotMaster && recipe.Perk == "potmaster") count *= 2;
            return count;
        }

        /// <summary>
        /// Builds the table from the whole <c>AllData.as</c> text. Rows that are not schemes, and
        /// schemes with no id, are skipped; everything else is kept in file order, so
        /// <see cref="Find"/> can take the first match the way AS3's <c>(@id == id)[0]</c> does.
        /// </summary>
        public static List<CraftingRecipe> Build(string allData)
        {
            var recipes = new List<CraftingRecipe>();
            if (string.IsNullOrEmpty(allData)) return recipes;

            foreach (ItemXmlRow row in ItemXmlRows.Parse(allData))
            {
                if (row.Tip != "scheme" || string.IsNullOrEmpty(row.Id)) continue;

                string output = row.Id.StartsWith(SchemePrefix, StringComparison.Ordinal)
                    ? row.Id.Substring(SchemePrefix.Length)
                    : row.Id;

                recipes.Add(new CraftingRecipe(
                    row.Id,
                    output,
                    PFE.Systems.Weapons.WeaponXmlAttrs.Attr(row.Attrs, "work"),
                    PFE.Systems.Weapons.WeaponXmlAttrs.Attr(row.Attrs, "skill"),
                    PFE.Systems.Weapons.WeaponXmlAttrs.AttrI(row.Attrs, "lvl", 0),
                    PFE.Systems.Weapons.WeaponXmlAttrs.AttrI(row.Attrs, "kol", 1),
                    PFE.Systems.Weapons.WeaponXmlAttrs.Attr(row.Attrs, "perk"),
                    ItemXmlRows.ParseCraftRows(row.Body).ToArray()));
            }

            return recipes;
        }

        /// <summary>The first recipe with this scheme id, or null. AS3's <c>.(@id == id)[0]</c>.</summary>
        public static CraftingRecipe? Find(List<CraftingRecipe> recipes, string schemeId)
        {
            if (recipes == null || string.IsNullOrEmpty(schemeId)) return null;
            for (int i = 0; i < recipes.Count; i++)
            {
                if (string.Equals(recipes[i].SchemeId, schemeId, StringComparison.Ordinal))
                    return recipes[i];
            }

            return null;
        }

        /// <summary>The first recipe whose product is this id, or null.</summary>
        public static CraftingRecipe? FindByOutput(List<CraftingRecipe> recipes, string outputId)
        {
            if (recipes == null || string.IsNullOrEmpty(outputId)) return null;
            for (int i = 0; i < recipes.Count; i++)
            {
                if (string.Equals(recipes[i].OutputId, outputId, StringComparison.Ordinal))
                    return recipes[i];
            }

            return null;
        }
    }
}
