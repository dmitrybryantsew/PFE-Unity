using System;
using System.Collections.Generic;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// One entry of the F2 overlay's <b>family</b> dropdown: a named set of unit ids that a tester
    /// thinks of as one thing, plus the variants inside it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a curated grouping rather than the data's own parent links.</b> The port's 148 unit
    /// assets carry <c>parentId</c> only sometimes — <c>raider1</c> has <c>parent=raider</c> but
    /// <c>dron2</c>, <c>turret2</c>, <c>bossraider</c>, <c>cryoslime</c> and <c>molerat</c> have none —
    /// so grouping by it alone scatters the robots across nine unrelated entries. Grouping by the AS3
    /// <i>behaviour family</i> (<c>docs/EnemyImplementationGuides/04_ROSTER_AND_FAMILY_MAP.md</c>) has the
    /// opposite problem: one family holds six factions, so a "tier" no longer names a unit (tier 1 of
    /// ArmedShooter is <c>raider1</c>, <c>slaver1</c>, <c>zebra1</c>, <c>ranger1</c>, <c>encl1</c> and
    /// <c>merc1</c>). This table is the middle: fine enough that family + tier is a unique key, coarse
    /// enough that the second dropdown stays short.</para>
    ///
    /// <para><b>Membership is by ROOT, not by an id list.</b> A unit id belongs to the first rule whose
    /// roots contain <see cref="UnitSpawnCatalog.RootOf"/> of that id — the id with its trailing digits
    /// stripped. So <c>"raider"</c> as a root claims <c>raider</c>, <c>raider1</c> … <c>raider9</c>
    /// without enumerating them, and an asset added tomorrow under an existing root is picked up with no
    /// edit here. An id that <i>no</i> rule claims is not dropped: it lands in a trailing
    /// <c>Unclassified</c> group so a new asset can never be silently invisible in the picker.</para>
    /// </remarks>
    public sealed class UnitSpawnGroup
    {
        public UnitSpawnGroup(string name, IReadOnlyList<string> variantIds, bool isRooted)
        {
            Name = name ?? string.Empty;
            VariantIds = variantIds ?? Array.Empty<string>();
            IsRooted = isRooted;
        }

        /// <summary>Display name of the family row, e.g. <c>Raiders</c>.</summary>
        public string Name { get; }

        /// <summary>The variants in this family, ordered by root then tier. Never empty.</summary>
        public IReadOnlyList<string> VariantIds { get; }

        /// <summary>
        /// True when every variant shares one root, i.e. the variants really are tiers of one unit
        /// (<c>raider1..raider9</c>). False for a family that folds several units together
        /// (<c>Robots</c> = <c>dron*</c> + <c>gutsy*</c> + <c>protect*</c> …), where a bare number would
        /// not name anything.
        /// </summary>
        public bool IsRooted { get; }

        public int Count => VariantIds.Count;

        public override string ToString() => Name + " (" + Count + ")";
    }

    /// <summary>
    /// One <c>(family, root)</c> pair of the family table, flattened so a test can police it.
    /// </summary>
    /// <remarks>
    /// <b>Why this is public and not an implementation detail.</b> The table's one silent failure mode
    /// is a root written with its digits still attached — <c>"damexpl1"</c> instead of <c>"damexpl"</c>.
    /// Nothing would throw: the rule would simply never match an id, and the unit would quietly appear in
    /// <c>Unclassified</c> instead of <c>Damagers</c>. A test cannot assert that without seeing the table.
    /// </remarks>
    public readonly struct UnitSpawnRule
    {
        public UnitSpawnRule(string family, string root)
        {
            Family = family;
            Root = root;
        }

        public string Family { get; }
        public string Root { get; }
    }

    /// <summary>
    /// The F2 overlay's unit-spawn picker: family grouping, variant ordering and the "is this a real
    /// unit" judgement. <b>Unity-free on purpose</b> — no <c>UnityEngine</c> type appears in any
    /// signature, so the whole decision table runs on the offline wall
    /// (<c>execute-unity-editmode-fixtures-offline</c>) rather than only inside the editor.
    /// </summary>
    public static class UnitSpawnCatalog
    {
        /// <summary>Caption of the trailing bucket for ids no rule claims.</summary>
        public const string UnclassifiedGroupName = "Unclassified";

        /// <summary>
        /// The family table, in the order the dropdown shows it. Roots are compared against
        /// <see cref="RootOf"/>, so they are written <b>without</b> their trailing digits
        /// (<c>"damexpl"</c>, not <c>"damexpl1"</c>).
        /// </summary>
        static readonly (string Name, string[] Roots)[] GroupRules =
        {
            ("Raiders",       new[] { "raider" }),
            ("Slavers",       new[] { "slaver" }),
            ("Zebras",        new[] { "zebra" }),
            ("Rangers",       new[] { "ranger" }),
            ("Enclave",       new[] { "encl" }),
            ("Mercs",         new[] { "merc" }),
            ("Necros",        new[] { "necros" }),
            ("Zombies",       new[] { "zombie" }),
            ("Spectres",      new[] { "spectre" }),
            ("Alicorns",      new[] { "alicorn" }),

            // One row because a tester says "robot", not "Dron". Each root is a separate AS3 class
            // (UnitRobobrain, UnitDron, UnitProtect, UnitGutsy, UnitSentinel, UnitSpriteBot,
            // UnitVortex, UnitRoller, UnitMsp, UnitEqd) that share one brain.
            ("Robots",        new[] { "robot", "robobrain", "dron", "dront", "protect", "gutsy",
                                      "sentinel", "spritebot", "vortex", "roller", "msp", "eqd" }),

            ("Flyers",        new[] { "bloat", "bloodwing", "phoenix", "ebloat", "eant" }),
            ("Ants",          new[] { "ant" }),

            // UnitMonstrik, UnitHellhound and UnitSlime — one brain, three very different bodies.
            ("Critters",      new[] { "rat", "molerat", "scorp", "tarakan", "hellhound", "slime",
                                      "cryoslime", "pinkslime" }),

            ("Fish",          new[] { "fish" }),

            // `ttur` is UnitThunderTurret, its own class — folded here because it is a fixed gun.
            ("Turrets",       new[] { "turret", "ttur" }),
            ("Traps",         new[] { "mtrap", "trigcans", "triglaser", "trigplate", "trigridge" }),
            ("Damagers",      new[] { "damgren", "damshot", "damexpl" }),

            ("Bosses",        new[] { "bossraider", "bossencl", "bossnecr", "bossalicorn", "bossdron",
                                      "bossultra", "thunderhead" }),

            ("Props & misc",  new[] { "mwall", "destr", "transmitter", "training", "moon" }),

            ("NPCs & other",  new[] { "npc", "doctor", "vendor", "captive", "ponpon", "pony", "owl",
                                      "littlepip", "monster", "other", "scythe", "bigrobot", "smallrobot" }),
        };

        /// <summary>
        /// Ids that are <b>not a unit you can spawn and fight</b>, so the picker hides them behind a
        /// toggle instead of deleting them (a family template is exactly what you want when the question
        /// is "did the importer write a sheet for this row?").
        /// </summary>
        /// <remarks>
        /// <para>Two kinds, both taken from
        /// <c>docs/EnemyImplementationGuides/04_ROSTER_AND_FAMILY_MAP.md</c>:</para>
        /// <list type="bullet">
        /// <item><b>Family / category templates and markers</b> — <c>cat='2'</c> nodes with no
        /// <c>&lt;vis&gt;</c> sheet: <c>raider</c>, <c>slaver</c>, <c>zebra</c>, <c>ranger</c>,
        /// <c>encl</c>, <c>merc</c>, <c>zombie</c>, <c>alicorn</c>, <c>ant</c>, <c>fish</c>,
        /// <c>bloat</c>, <c>bloodwing</c>, <c>turret</c>, <c>mtrap</c>, <c>hellhound</c>,
        /// <c>robot</c>, <c>bigrobot</c>, <c>smallrobot</c>, <c>monster</c>, <c>other</c>,
        /// <c>scythe</c>, <c>pony</c>, <c>mwall</c>.</item>
        /// <item><b>Non-combatants</b> — the player and the NPC / pet / captive roles:
        /// <c>littlepip</c>, <c>npc</c>, <c>doctor</c>, <c>vendor</c>, <c>captive</c>,
        /// <c>ponpon</c>, <c>owl</c>.</item>
        /// </list>
        ///
        /// <para><b>Deliberately NOT in this set:</b> <c>training</c> (a real, damageable
        /// <c>UnitTrain</c> with a controller and art — the one unit built for testing), <c>moon</c>
        /// (a real if tiny unit, <c>hp 10</c>), and <c>eqd</c> / <c>dront</c> / <c>spectre</c> (real
        /// classes). Whether they <i>draw</i> is a separate question answered per row by the overlay's
        /// <c>sheet</c> readout, not by hiding them here — a row that spawns as an invisible collider is
        /// a fact worth seeing, not one worth concealing.</para>
        /// </remarks>
        static readonly HashSet<string> NonSpawnable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "raider", "slaver", "zebra", "ranger", "encl", "merc", "zombie", "alicorn", "ant", "fish",
            "bloat", "bloodwing", "turret", "mtrap", "hellhound", "robot", "bigrobot", "smallrobot",
            "monster", "other", "scythe", "pony", "mwall",

            "littlepip", "npc", "doctor", "vendor", "captive", "ponpon", "owl",
        };

        /// <summary>How many ids this class refuses to offer as a spawn.</summary>
        public static int NonSpawnableCount => NonSpawnable.Count;

        /// <summary>
        /// <see cref="GroupRules"/> flattened to one entry per root, in dropdown order. Read by
        /// <c>UnitSpawnCatalogTests</c> to prove no root is listed twice and none carries its digits.
        /// </summary>
        public static readonly IReadOnlyList<UnitSpawnRule> Rules = FlattenRules();

        static IReadOnlyList<UnitSpawnRule> FlattenRules()
        {
            var flattened = new List<UnitSpawnRule>();
            for (int i = 0; i < GroupRules.Length; i++)
            {
                string[] roots = GroupRules[i].Roots;
                for (int r = 0; r < roots.Length; r++)
                {
                    flattened.Add(new UnitSpawnRule(GroupRules[i].Name, roots[r]));
                }
            }

            return flattened;
        }

        /// <summary>
        /// The unit id with its trailing digits removed — <c>"bloat10"</c> → <c>"bloat"</c>,
        /// <c>"molerat"</c> → <c>"molerat"</c>. This is the key every rule matches on.
        /// </summary>
        public static string RootOf(string unitId)
        {
            if (string.IsNullOrEmpty(unitId))
            {
                return string.Empty;
            }

            int end = unitId.Length;
            while (end > 0 && IsDigit(unitId[end - 1]))
            {
                end--;
            }

            return unitId.Substring(0, end);
        }

        /// <summary>
        /// The AS3 variant index (<c>tr</c>) read off the id's trailing digits, or <b>-1</b> when the id
        /// carries none (<c>"molerat"</c>, <c>"robot"</c>). Never throws: a pathological id with more
        /// than six trailing digits is reported as "no tier" rather than overflowing.
        /// </summary>
        public static int TierOf(string unitId)
        {
            if (string.IsNullOrEmpty(unitId))
            {
                return -1;
            }

            int end = unitId.Length;
            int start = end;
            while (start > 0 && IsDigit(unitId[start - 1]))
            {
                start--;
            }

            if (start == end || end - start > 6)
            {
                return -1;
            }

            int value = 0;
            for (int i = start; i < end; i++)
            {
                value = value * 10 + (unitId[i] - '0');
            }

            return value;
        }

        /// <summary>
        /// True for an id that is a family template, a category marker, the player, or an NPC / pet role
        /// — i.e. not something to spawn as an opponent. See <see cref="NonSpawnable"/> for the list and
        /// the reasoning.
        /// </summary>
        public static bool IsNonSpawnable(string unitId)
        {
            return !string.IsNullOrEmpty(unitId) && NonSpawnable.Contains(unitId);
        }

        /// <summary>
        /// The variant ordering used everywhere in the picker: by root, then by <b>numeric</b> tier.
        /// </summary>
        /// <remarks>
        /// <para><b>The numeric part is not cosmetic.</b> An ordinal sort of the real ids gives
        /// <c>bloat0, bloat1, bloat10, bloat2 …</c> and <c>zombie0, zombie1, zombie2 … zombie9</c> looks
        /// fine right up until <c>bloat10</c> lands in the middle — which reads as a missing variant and a
        /// present-but-wrong one, the exact pair this project keeps getting bitten by. Tier <c>-1</c>
        /// (no digits) sorts before tier 0, so a template row sits above its variants.</para>
        ///
        /// <para>The root key first is what makes the same rule correct for a folded family:
        /// <c>Robots</c> comes out <c>dron, dron1..3, dront, gutsy, gutsy1, msp, protect…</c> instead of
        /// interleaving every root's tier 1 together.</para>
        /// </remarks>
        public static int CompareVariants(string a, string b)
        {
            int tierA = TierOf(a);
            int tierB = TierOf(b);
            if (tierA != tierB)
            {
                return tierA.CompareTo(tierB);
            }

            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Group every id it is given, in <see cref="GroupRules"/> order. Groups that end up with no
        /// members are omitted entirely, and any id no rule claims is appended in a trailing
        /// <see cref="UnclassifiedGroupName"/> group — so <c>Build(ids)</c> always accounts for every id
        /// it was handed.
        /// </summary>
        public static List<UnitSpawnGroup> Build(IEnumerable<string> unitIds)
        {
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (unitIds != null)
            {
                foreach (string raw in unitIds)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    string id = raw.Trim();
                    if (seen.Add(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            var buckets = new List<string>[GroupRules.Length];
            for (int i = 0; i < buckets.Length; i++)
            {
                buckets[i] = new List<string>();
            }

            var unclassified = new List<string>();

            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                int rule = IndexOfRule(RootOf(id));
                if (rule < 0)
                {
                    unclassified.Add(id);
                }
                else
                {
                    buckets[rule].Add(id);
                }
            }

            var groups = new List<UnitSpawnGroup>();
            for (int i = 0; i < GroupRules.Length; i++)
            {
                if (buckets[i].Count == 0)
                {
                    continue;
                }

                buckets[i].Sort(CompareVariants);
                groups.Add(new UnitSpawnGroup(GroupRules[i].Name, buckets[i], AllShareOneRoot(buckets[i])));
            }

            if (unclassified.Count > 0)
            {
                unclassified.Sort(CompareVariants);
                groups.Add(new UnitSpawnGroup(UnclassifiedGroupName, unclassified, AllShareOneRoot(unclassified)));
            }

            return groups;
        }

        /// <summary>
        /// Narrow every group to the variants <paramref name="keep"/> accepts, <b>dropping any group left
        /// with nothing</b> and recomputing <see cref="UnitSpawnGroup.IsRooted"/> from what survived.
        /// </summary>
        /// <remarks>
        /// This is what the overlay's "show non-spawnable" toggle goes through, which is why it lives
        /// here: a family whose every member is hidden (<c>NPCs &amp; other</c> holds only non-combatants)
        /// must vanish from the dropdown rather than render as an empty list — an empty dropdown reads as
        /// a broken filter, not as "everything here is hidden".
        /// </remarks>
        public static List<UnitSpawnGroup> Filter(IReadOnlyList<UnitSpawnGroup> groups, Func<string, bool> keep)
        {
            var result = new List<UnitSpawnGroup>();
            if (groups == null)
            {
                return result;
            }

            for (int i = 0; i < groups.Count; i++)
            {
                UnitSpawnGroup group = groups[i];
                if (group == null)
                {
                    continue;
                }

                var kept = new List<string>();
                for (int v = 0; v < group.VariantIds.Count; v++)
                {
                    string id = group.VariantIds[v];
                    if (keep == null || keep(id))
                    {
                        kept.Add(id);
                    }
                }

                if (kept.Count == 0)
                {
                    continue;
                }

                result.Add(new UnitSpawnGroup(group.Name, kept, AllShareOneRoot(kept)));
            }

            return result;
        }

        /// <summary>The family dropdown's row caption: the name, with its variant count.</summary>
        public static string FamilyLabel(UnitSpawnGroup group)
        {
            return group == null ? string.Empty : group.Name + "  (" + group.Count + ")";
        }

        /// <summary>
        /// The variant dropdown's row caption. For a single-root family the number <i>is</i> the tier, so
        /// it is named; for a folded family (<c>Robots</c>) a number would not identify anything, so the
        /// id stands alone.
        /// </summary>
        public static string VariantLabel(UnitSpawnGroup group, string unitId)
        {
            if (string.IsNullOrEmpty(unitId))
            {
                return string.Empty;
            }

            if (group != null && group.IsRooted)
            {
                int tier = TierOf(unitId);
                if (tier >= 0)
                {
                    return "Tier " + tier + "  ·  " + unitId;
                }
            }

            return unitId;
        }

        static bool AllShareOneRoot(List<string> ids)
        {
            if (ids.Count == 0)
            {
                return false;
            }

            string first = RootOf(ids[0]);
            for (int i = 1; i < ids.Count; i++)
            {
                if (!string.Equals(RootOf(ids[i]), first, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        static int IndexOfRule(string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                return -1;
            }

            for (int i = 0; i < GroupRules.Length; i++)
            {
                string[] roots = GroupRules[i].Roots;
                for (int r = 0; r < roots.Length; r++)
                {
                    if (string.Equals(roots[r], root, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        static bool IsDigit(char c) => c >= '0' && c <= '9';
    }

    /// <summary>
    /// How tall an expanded dropdown list should be, and how many rows fit in it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a named call and not four lines at the draw site.</b> The overlay sized the
    /// list's viewport with <c>options.Count * 22f</c> while drawing 20 px rows, so the viewport and the
    /// rows disagreed by two independent errors and the last row was cut in half. The rule here is a
    /// <i>whole-row</i> rule — a viewport that stops mid-row reads as a rendering bug rather than as
    /// "there is more below", which the scrollbar already says — and a whole-row rule is assertable
    /// offline, which an inline <c>* 22f</c> never was.</para>
    ///
    /// <para><b>Unity-free, and <paramref name="rowAdvance"/> is a parameter.</b> The row height is a
    /// property of the running skin (<c>GUIStyle.CalcSize(...).y + margin.vertical</c>), not of this
    /// class, so it is passed in. Nothing here touches <c>UnityEngine</c>, which is what lets the
    /// arithmetic run on the offline wall instead of only inside the editor.</para>
    /// </remarks>
    public static class UnitPickerLayout
    {
        /// <summary>
        /// Slack added to the viewport so the list's own frame cannot clip the last row it was sized for.
        /// </summary>
        public const float ViewportSlack = 2f;

        /// <summary>
        /// How many whole rows are visible: all of them when they fit, otherwise as many as the cap
        /// allows — and never fewer than one, so an absurdly small cap shows one row rather than none.
        /// </summary>
        public static int VisibleRows(float rowAdvance, int optionCount, float maxHeight)
        {
            if (optionCount <= 0)
            {
                return 0;
            }

            if (rowAdvance <= 0f)
            {
                rowAdvance = 1f;
            }

            int fitsWhole = (int)Math.Floor((maxHeight - ViewportSlack) / rowAdvance);
            if (fitsWhole < 1)
            {
                fitsWhole = 1;
            }

            return Math.Min(optionCount, fitsWhole);
        }

        /// <summary>
        /// The viewport height for <paramref name="optionCount"/> rows of <paramref name="rowAdvance"/>
        /// pixels, capped at <paramref name="maxHeight"/>. Always a whole number of rows plus
        /// <see cref="ViewportSlack"/>, so no row is ever half-drawn; 0 for an empty list.
        /// </summary>
        public static float ViewportHeight(float rowAdvance, int optionCount, float maxHeight)
        {
            if (optionCount <= 0)
            {
                return 0f;
            }

            if (rowAdvance <= 0f)
            {
                rowAdvance = 1f;
            }

            return VisibleRows(rowAdvance, optionCount, maxHeight) * rowAdvance + ViewportSlack;
        }

        /// <summary>
        /// True when the list is taller than its cap and therefore scrolls — the state in which the
        /// overlay must show a scrollbar rather than silently truncate.
        /// </summary>
        public static bool Scrolls(float rowAdvance, int optionCount, float maxHeight)
        {
            return optionCount > VisibleRows(rowAdvance, optionCount, maxHeight);
        }
    }
}
