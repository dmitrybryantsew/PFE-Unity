using PFE.Data.Definitions;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// AS3's <c>invCat</c> — the inventory <b>storage</b> category, and the reason <c>Invent.as</c> keeps a
    /// per-category mass total with its own cap (<c>maxm1/2/3</c>).
    ///
    /// <para><b>This is not the same thing as <see cref="InventoryCategory"/>.</b> That enum is a UI
    /// grouping; AS3's <c>invCat</c> is a small integer derived at <c>Item</c> construction time and used
    /// only for mass accounting and the vault's tabs. Conflating the two is what produced the fabricated
    /// translation this class replaces (see <c>GameInventory.MapToWeightCategory</c>, which sent every
    /// un-categorised item to "not tracked" and therefore dropped 451 of the 500 items out of the mass
    /// total entirely).</para>
    ///
    /// <para><b>The oracle rule, verbatim</b> — <c>fe/serv/Item.as:83</c> and <c>:271-293</c>:</para>
    /// <code>
    /// public var invCat:int = 3;                      // :83   default is STUFF
    /// if (tip == "a" || tip == "e") invCat = 2;       // :271  ammo and explosives
    /// if (xml.@us > 0 &amp;&amp; tip != "food" &amp;&amp; tip != "eda" &amp;&amp; tip != "book")
    ///     invCat = 1;                                 // :275  usable (consumed by useItem)
    /// if (xml.@invcat.length()) invCat = xml.@invcat; // :293  explicit override WINS
    /// </code>
    ///
    /// <para><b>Order matters and the override is last on purpose.</b> The explicit <c>@invcat</c> is
    /// applied after both derived rules, so it can move a row <i>out of</i> the usable category — and it
    /// does: measured against <c>AllData.as</c>, 9 of the 16 <c>equip</c> rows and the single <c>sphera</c>
    /// row carry <c>@invcat='3'</c> and are therefore STUFF despite having <c>@us &gt; 0</c>. Treating the
    /// override as a no-op (all ten values equal the default 3, which is tempting) would put 10 rows in the
    /// wrong mass category.</para>
    ///
    /// <para><b>Measured oracle distribution over all 500 rows</b> (the numbers the fixtures pin):
    /// <c>invCat 1 = 87</c>, <c>2 = 71</c>, <c>3 = 342</c>. <c>2</c> is exactly <c>a</c>(49) + <c>e</c>(22);
    /// <c>1</c> is the eight usable tips minus the ten override rows.</para>
    ///
    /// <para><b>Unity-free on purpose.</b> Nothing here touches <c>UnityEngine</c> or a
    /// <c>ScriptableObject</c>, so the whole decision table runs on the offline wall. The
    /// <see cref="ItemDefinition"/> adapter lives at the bottom and is the only part that mentions an
    /// engine type.</para>
    /// </summary>
    public static class InventoryCategoryRules
    {
        /// <summary>AS3 <c>invCat = 3</c> — general stuff. The default, and the largest bucket.</summary>
        public const int Stuff = 3;

        /// <summary>AS3 <c>invCat = 2</c> — ammunition and explosives (<c>tip</c> <c>a</c>/<c>e</c>).</summary>
        public const int Ammo = 2;

        /// <summary>AS3 <c>invCat = 1</c> — usable items, i.e. rows carrying <c>@us &gt; 0</c>.</summary>
        public const int Usable = 1;

        /// <summary>
        /// AS3 <c>invCat = 0</c> — never assigned by the derivation, but reachable through an explicit
        /// <c>@invcat='0'</c>. Kept so the constant exists for the vault/UI side; no row uses it.
        /// </summary>
        public const int NotTracked = 0;

        /// <summary>
        /// The highest category index the mass arrays hold (<c>categoryMass[4]</c>). AS3's
        /// <c>mass[invCat]</c> is indexed 1..3, with slot 0 present but unused.
        /// </summary>
        public const int MaxCategory = 3;

        /// <summary>
        /// The raw AS3 <c>tip</c> values that mean "ammunition or explosive" and therefore force
        /// <see cref="Ammo"/> — <c>Item.as:271</c>.
        /// </summary>
        public static readonly string[] AmmoOrExplosiveTips = { "a", "e" };

        /// <summary>
        /// The raw AS3 <c>tip</c> values the usable rule <b>excludes</b> even when <c>@us &gt; 0</c> —
        /// <c>Item.as:275</c>. <c>eda</c> is food-adjacent and does not appear in the current
        /// <c>AllData.as</c>; it is kept because the oracle names it and a future data drop may add it.
        /// </summary>
        public static readonly string[] UsableExemptTips = { "food", "eda", "book" };

        /// <summary>Whether a raw AS3 <c>tip</c> forces the ammo/explosive category.</summary>
        public static bool IsAmmoOrExplosiveTip(string rawTip)
        {
            if (string.IsNullOrEmpty(rawTip)) return false;
            for (int i = 0; i < AmmoOrExplosiveTips.Length; i++)
            {
                if (AmmoOrExplosiveTips[i] == rawTip) return true;
            }
            return false;
        }

        /// <summary>Whether a raw AS3 <c>tip</c> is exempt from the usable rule.</summary>
        public static bool IsUsableExemptTip(string rawTip)
        {
            if (string.IsNullOrEmpty(rawTip)) return false;
            for (int i = 0; i < UsableExemptTips.Length; i++)
            {
                if (UsableExemptTips[i] == rawTip) return true;
            }
            return false;
        }

        /// <summary>
        /// The faithful AS3 derivation, over the inputs the oracle actually reads.
        ///
        /// <para><paramref name="rawTip"/> is the <b>raw</b> AS3 tip string (<c>"a"</c>, <c>"e"</c>,
        /// <c>"equip"</c>, …), not the collapsed <see cref="ItemType"/>. That distinction is
        /// load-bearing: <c>e</c> is ammo-category and <c>equip</c> is usable-category, and the port's
        /// <see cref="ItemType"/> maps <i>both</i> to <c>Equipment</c>, so the enum cannot express this
        /// rule. See <see cref="ResolveFromItemType"/> for the honest approximation.</para>
        /// </summary>
        /// <param name="rawTip">AS3 <c>tip</c>. Null/empty is treated as "no tip".</param>
        /// <param name="usesCount">AS3 <c>@us</c>. Only <c>&gt; 0</c> is meaningful.</param>
        /// <param name="hasExplicitCategory">Whether AS3 <c>@invcat</c> was present.</param>
        /// <param name="explicitCategory">The <c>@invcat</c> value; ignored unless the flag is set.</param>
        public static int Resolve(string rawTip, int usesCount, bool hasExplicitCategory, int explicitCategory)
        {
            int category = Stuff;

            if (IsAmmoOrExplosiveTip(rawTip))
            {
                category = Ammo;
            }

            if (usesCount > 0 && !IsUsableExemptTip(rawTip))
            {
                category = Usable;
            }

            // Last, so it can move a row back OUT of Usable — which is what it does for 9 `equip`
            // rows and the single `sphera` row. Not a no-op, despite every recorded value being 3.
            // (Mutation-tested: moving this block above the two rules above kills exactly the three
            // ExplicitOverride_* fixtures.)
            if (hasExplicitCategory)
            {
                category = explicitCategory;
            }

            return category;
        }

        /// <summary>
        /// The approximation available from the port's <b>collapsed</b> <see cref="ItemType"/>, because
        /// the raw tip and <c>@us</c> are not imported (see the file header).
        ///
        /// <para><b>What it gets right:</b> <see cref="ItemType.Ammo"/> → <see cref="Ammo"/>, and
        /// everything the port knows to be non-usable → <see cref="Stuff"/>.</para>
        ///
        /// <para><b>What it cannot get right, measured:</b> <c>ItemType.Equipment</c> merges AS3's
        /// <c>e</c> (22 rows, category 2) with <c>equip</c> (16 rows: 7 at category 1, 9 at category 3).
        /// No function of <see cref="ItemType"/> can separate them, so this overload deliberately returns
        /// <see cref="Stuff"/> for <c>Equipment</c> rather than guessing: that is correct for 9 of the 38
        /// rows and wrong for 29, which is <i>worse</i> than the 22/38 a "call it ammo" guess would score
        /// — and a guess that is right by accident is the failure shape this project keeps paying for.
        /// Fixing it needs <c>tip</c> imported, which is an importer change (owner-only, rule #5).</para>
        /// </summary>
        /// <param name="type">The port's collapsed item type.</param>
        /// <param name="usesCount">AS3 <c>@us</c> if it ever becomes available; 0 today.</param>
        /// <param name="hasExplicitCategory">AS3 <c>@invcat</c> presence; false today.</param>
        /// <param name="explicitCategory">The <c>@invcat</c> value; ignored unless the flag is set.</param>
        public static int ResolveFromItemType(ItemType type, int usesCount = 0,
                                              bool hasExplicitCategory = false, int explicitCategory = Stuff)
        {
            // Ammo is the one tip the collapsed enum preserves exactly (AS3 `a` is the only tip that maps
            // to ItemType.Ammo). `e` is NOT recoverable — it went to Equipment with `equip`.
            if (type == ItemType.Ammo)
            {
                return Resolve("a", usesCount, hasExplicitCategory, explicitCategory);
            }

            // Everything else: the derived rules cannot be evaluated without `tip`, so apply only the
            // default and the explicit override, in AS3's order.
            return Resolve(null, usesCount, hasExplicitCategory, explicitCategory);
        }

        /// <summary>
        /// The category the mass total should charge this item to, using the best inputs the port has.
        ///
        /// <para>This is the seam the inventory calls. When the importer starts carrying <c>tip</c>,
        /// <c>@us</c> and <c>@invcat</c>, only this method changes — every caller and every fixture stays
        /// as it is.</para>
        /// </summary>
        public static int ForItem(ItemDefinition item)
        {
            if (item == null) return NotTracked;
            return ResolveFromItemType(item.type);
        }
    }
}
