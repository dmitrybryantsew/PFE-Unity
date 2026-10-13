using System;
using System.Collections.Generic;
using PFE.Systems.Inventory;

namespace PFE.UI.PipBuck
{
    /// <summary>Which of the Oracle's three row shapes a page row uses.</summary>
    public enum PipInventoryKind
    {
        /// <summary>Page 1 — reads <c>inv.weapons</c>. Columns: fav, name, condition %, rounds.</summary>
        Weapon,

        /// <summary>Page 2 — reads <c>inv.armors</c>. Columns: fav, name, condition %.</summary>
        Armor,

        /// <summary>Pages 3/4/5 — reads <c>inv.items</c>. Columns: fav, name, count, mass, category.</summary>
        Item,
    }

    /// <summary>
    /// One thing the inventory page may list, in plain data.
    ///
    /// <para><b>Why the entry is a flat struct of public fields and not a reference to the live
    /// instance.</b> The filtering and sorting rules in <see cref="PipInventoryList"/> are the part of
    /// <c>PipPageInv.setSubPages</c> that has branches worth pinning, and pinning them requires
    /// running them — which in this project means running them offline, with no editor and no
    /// <c>ScriptableObject</c>. A struct of primitives can be built by a fixture in six lines; a
    /// <c>GameWeaponInstance</c> cannot be built without a definition asset. The live reader
    /// (<c>PlayerPipInventorySource</c>) is the only thing that knows about
    /// <c>GameInventory</c>.</para>
    ///
    /// <para><b>Fields, not <c>init</c> properties.</b> This project targets .NET Standard 2.1, which
    /// does not ship <c>System.Runtime.CompilerServices.IsExternalInit</c> — so an <c>init</c>
    /// accessor is a compile error (CS0518) rather than a style choice, and no other file here uses
    /// one. Public fields plus an object initializer is the pattern
    /// <c>PipStatusSnapshot</c> already uses for exactly this kind of value.</para>
    ///
    /// <para><b>The sort slots are the Oracle's keys, not a port invention.</b>
    /// <c>setSubPages</c> sorts each page with its own <c>sortOn</c> key list
    /// (<c>PipPageInv.as:174</c> weapons, <c>:211</c> armour, <c>:303</c> items) and the three lists
    /// have different lengths and types. Rather than three comparators, the reader fills a single
    /// five-slot key (<see cref="CategoryKey"/>, <see cref="Sort1"/>..<see cref="Sort3"/>,
    /// <see cref="Name"/>) and <see cref="PipInventoryList.RowsFor"/> compares that. The mapping is
    /// written out on <see cref="PipInventoryList.RowsFor"/>.</para>
    /// </summary>
    public struct PipInventoryEntry
    {
        /// <summary>The Oracle's item/weapon/armour id — <c>obj.id</c>.</summary>
        public string Id;

        /// <summary>Display name — the Oracle's <c>nazv</c>.</summary>
        public string Name;

        public PipInventoryKind Kind;

        /// <summary>
        /// Which of the Oracle's <c>page2</c> values lists this entry: <b>1</b> weapons, <b>2</b>
        /// armour, <b>3</b> aid, <b>4</b> misc, <b>5</b> ammo.
        /// </summary>
        public int SubPage;

        /// <summary>
        /// The primary sort key, as a string. Empty for weapons (which have no string key); the raw
        /// <c>@tip</c> for items; <c>"armor" + tip</c> for armour — the Oracle's <c>trol</c>.
        /// </summary>
        public string CategoryKey;

        /// <summary>Second sort key. Weapons <c>sort1</c>, armour <c>sort</c>, items <c>sort2</c>.</summary>
        public int Sort1;

        /// <summary>Third sort key. Weapons <c>sort2</c>; unused by armour and items.</summary>
        public int Sort2;

        /// <summary>Fourth sort key. Weapons <c>sort3</c>; unused by armour and items.</summary>
        public int Sort3;

        /// <summary>
        /// The value column. A weapon's or armour's <b>condition percentage</b> (0..100), or an item's
        /// <b>stack count</b> — the Oracle writes both into <c>item.hp</c>
        /// (<c>PipPageInv.as:142</c>, <c>:199</c>, <c>:461</c>).
        /// </summary>
        public float Value;

        /// <summary>
        /// How the value column renders. <c>true</c> for the weapons/armour condition percentage,
        /// <c>false</c> for an item's bare count. The Oracle reaches these by two different code
        /// paths and they are not interchangeable — a count rendered as <c>"37%"</c> would read as a
        /// condition.
        /// </summary>
        public bool ValueIsPercent;

        /// <summary>Per-unit mass — the Oracle's <c>item.mass</c>. Used by the mass column.</summary>
        public float Mass;

        /// <summary>Stack count, for the mass column's <c>kol * mass</c> and the drop amount.</summary>
        public int Count;

        /// <summary>The Oracle's <c>avail</c> — a weapon that cannot currently be used.</summary>
        public bool Available;

        /// <summary>
        /// The Oracle's <c>respect == 1</c> — a weapon stored away rather than carried. Listed only
        /// when <c>pip.showHidden</c> is set.
        /// </summary>
        public bool Hidden;

        /// <summary>Whether this is the currently equipped weapon or armour — the Oracle's <c>ramka</c>.</summary>
        public bool Equipped;

        /// <summary>The Oracle's <c>nov == 1</c> — newly acquired, marked with <c>☩</c>.</summary>
        public bool IsNew;

        /// <summary>The Oracle's <c>nov == 2</c> — recently acquired, marked with <c>+</c>.</summary>
        public bool IsRecent;

        /// <summary>
        /// The favourite column. The Oracle resolves a bound slot to a <b>key name</b> through
        /// <c>Ctr.retKey</c>; the port has no key-name table, so the reader puts the slot number here
        /// and the gap is recorded rather than papered over.
        /// </summary>
        public string Favorite;

        /// <summary>
        /// The <c>ammo</c> column. For a weapon, the rounds in it; for a valuable item, its price.
        /// </summary>
        public string Extra;

        /// <summary>
        /// The <c>ammotip</c> column. For a weapon, the loaded ammo's name; for an item, its category
        /// or the pending drop amount.
        /// </summary>
        public string ExtraTip;

        /// <summary>
        /// The Oracle's <c>@tip == "valuables"</c> — the only items whose price is shown.
        /// <c>PipPageInv.as:273-276,466-469</c>.
        /// </summary>
        public bool ShowPrice;

        /// <summary>
        /// Set for the ammo page when this round is what the equipped weapon is loaded with. The
        /// Oracle prefixes such a row's sort key with <c>"0"</c> so it sorts first
        /// (<c>PipPageInv.as:285-288</c>).
        /// </summary>
        public bool IsLoadedAmmo;

        /// <summary>
        /// The Oracle's <c>drop</c> counter — how many of this stack the player has queued to drop.
        /// Shown in the <c>ammotip</c> column while non-zero (<c>PipPageInv.as:485-492</c>).
        /// </summary>
        public int PendingDrop;
    }

    /// <summary>
    /// One rendered row of the inventory page — the port of what <c>PipPageInv.setStatItem</c> writes
    /// into a row clip (<c>PipPageInv.as:345-494</c>).
    ///
    /// <para><b>The six fields are the Oracle's six columns</b> (<c>fav</c>, <c>nazv</c>, <c>hp</c>,
    /// <c>ammo</c>, <c>mass</c>, <c>ammotip</c>), kept under those names so a reader can diff this
    /// against <c>setStatItem</c> without a translation table.</para>
    /// </summary>
    public readonly struct PipInventoryRow
    {
        public PipInventoryRow(PipInventoryEntry entry, string fav, string name, string value,
            string extra, string mass, string extraTip)
        {
            Id = entry.Id;
            Kind = entry.Kind;
            Equipped = entry.Equipped;
            Available = entry.Available;
            Fav = fav;
            Name = name;
            Value = value;
            Extra = extra;
            Mass = mass;
            ExtraTip = extraTip;
        }

        /// <summary>The id a click sends back — the Oracle reads it off <c>item.id.text</c>.</summary>
        public string Id { get; }

        public PipInventoryKind Kind { get; }

        /// <summary>Whether this row draws the selection frame (the Oracle's <c>ramka</c>).</summary>
        public bool Equipped { get; }

        /// <summary>
        /// Whether the row is usable. The Oracle dims the name when a weapon is unavailable
        /// (<c>alpha = 0.6</c>, <c>PipPageInv.as:415-418</c>).
        /// </summary>
        public bool Available { get; }

        public string Fav { get; }
        public string Name { get; }
        public string Value { get; }
        public string Extra { get; }
        public string Mass { get; }
        public string ExtraTip { get; }

        /// <summary>One line, for the log and for a fixture's readable assertion.</summary>
        public string Format()
        {
            return Fav + " | " + Name + " | " + Value + " | " + Extra + " | " + Mass + " | " + ExtraTip;
        }
    }

    /// <summary>The header row's six captions, one per Oracle column.</summary>
    public readonly struct PipInventoryHeader
    {
        public PipInventoryHeader(string fav, string name, string value, string extra, string mass, string extraTip)
        {
            Fav = fav;
            Name = name;
            Value = value;
            Extra = extra;
            Mass = mass;
            ExtraTip = extraTip;
        }

        public string Fav { get; }
        public string Name { get; }
        public string Value { get; }
        public string Extra { get; }
        public string Mass { get; }
        public string ExtraTip { get; }

        /// <summary>The six captions in column order, for a caller that iterates them.</summary>
        public string[] ToArray() => new[] { Fav, Name, Value, Extra, Mass, ExtraTip };
    }

    /// <summary>
    /// The inventory page's bottom strip inputs — the Oracle's <c>pip.money</c>,
    /// <c>World.w.hardInv</c> and <c>inv.retMass(cat)</c>, gathered into one value so the view can
    /// refresh them from a single provider instead of three.
    ///
    /// <para>Public fields rather than <c>init</c> properties: see <see cref="PipInventoryEntry"/> for
    /// why an <c>init</c> accessor does not compile on this project's .NET Standard 2.1 target.</para>
    /// </summary>
    public struct PipInventoryFooter
    {
        /// <summary>Oracle <c>pip.money</c>.</summary>
        public int Money;

        /// <summary>Oracle <c>World.w.hardInv</c>.</summary>
        public bool HardInventory;

        /// <summary>
        /// Oracle <c>inv.retMass(cat)</c>. Null leaves the mass readouts off while keeping the caps
        /// total — which is what a caller with no inventory to hand should produce.
        /// </summary>
        public Func<int, float> CategoryMass;
    }

    /// <summary>
    /// The inventory page's list model: the port of the filtering, sorting and column rules in
    /// <c>PipPageInv.setSubPages</c> and <c>setStatItem</c>.
    ///
    /// <para><b>Unity-free on purpose.</b> No <c>MonoBehaviour</c>, no <c>RectTransform</c>, no
    /// <c>ScriptableObject</c> — everything here is a function of its arguments, which is what lets it
    /// be executed against the shipped assembly from a plain shell. That matters more than usual
    /// here: the rules below are where the Oracle's branches are, and they are exactly the sort that
    /// a compile cannot check.</para>
    ///
    /// <para><b>Sub-pages are the Oracle's <c>page2</c>, 1..5</b> — not the port's
    /// <see cref="InventoryPage"/>, which is zero-based. <see cref="SubPageFor"/> and
    /// <see cref="PageForSubPage"/> are the only places the two are joined, because an off-by-one
    /// here would list the armour page's contents under Aid and look like a data problem.</para>
    /// </summary>
    public static class PipInventoryList
    {
        /// <summary>Weapons — <c>PipPageInv.as:64</c>.</summary>
        public const int WeaponsSubPage = 1;

        /// <summary>Armour — <c>PipPageInv.as:178</c>.</summary>
        public const int ArmorSubPage = 2;

        /// <summary>Aid: usable items — <c>PipPageInv.as:215</c>, <c>:253</c>.</summary>
        public const int AidSubPage = 3;

        /// <summary>Misc: everything else — <c>PipPageInv.as:215</c>, <c>:253</c>.</summary>
        public const int MiscSubPage = 4;

        /// <summary>Ammo and explosives — <c>PipPageInv.as:215</c>, <c>:253</c>.</summary>
        public const int AmmoSubPage = 5;

        /// <summary>How many sub-pages the page has. Mirrors AS3's <c>tips</c> table (<c>:33</c>).</summary>
        public const int SubPageCount = 5;

        /// <summary>The port's zero-based page for an Oracle sub-page.</summary>
        public static InventoryPage PageForSubPage(int subPage)
        {
            if (subPage < 1 || subPage > SubPageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(subPage), subPage,
                    $"The Oracle's inventory sub-page is 1..{SubPageCount} (PipPageInv.as:33).");
            }

            return (InventoryPage)(subPage - 1);
        }

        /// <summary>The Oracle sub-page for the port's zero-based page.</summary>
        public static int SubPageFor(InventoryPage page) => (int)page + 1;

        /// <summary>
        /// The sub-category tab caption. The Oracle reads <c>Res.pipText("inv" + page2)</c>
        /// (<c>PipPage.as:148</c>, where <c>this.pp</c> is the page identifier <c>"inv"</c> and
        /// <c>_loc4_</c> is 1..5) — see <see cref="SubPageName(int, PipTextTable)"/> for the resolved
        /// form. This overload is the no-document path and returns the page name from
        /// <see cref="InventoryPageRules.Name"/>, the same string the AS3 page's own comments use.
        /// </summary>
        public static string SubPageName(int subPage)
        {
            if (subPage < 1 || subPage > SubPageCount) return "?";
            return InventoryPageRules.Name(PageForSubPage(subPage));
        }

        /// <summary>
        /// The sub-category tab caption, from the Oracle's own localisation document.
        ///
        /// <para><c>text_en.xml</c> gives <c>inv1</c>…<c>inv5</c> as
        /// <c>Weapons / Armor / Equipment / Other / Ammunition</c>. Note that two of those disagree
        /// with the port's old prose: sub-page 3 is <b>Equipment</b>, not the "Aid" the port's own
        /// page enum says, and sub-page 5 is <b>Ammunition</b>, not "Ammo". Both were the port's
        /// reading of the AS3's internal names, and the table is the authority.</para>
        /// </summary>
        public static string SubPageName(int subPage, PipTextTable text)
        {
            if (subPage < 1 || subPage > SubPageCount) return "?";
            if (text != null && text.TryGet("p", "inv" + subPage, 0, out string localised)) return localised;
            return SubPageName(subPage);
        }

        /// <summary>
        /// The Oracle's header captions, as string-table keys — the exact calls the AS3 makes:
        /// <c>PipPageInv.as:68-73</c> for weapons, <c>:180-185</c> for armour, <c>:218-226</c> for the
        /// three item pages. An empty string is a column the Oracle deliberately leaves blank
        /// (<c>statHead.ammo.text = ""</c>), not a missing key.
        ///
        /// <para>Every key here is present in <c>text_en.xml</c>, so <see cref="HeaderFor(int, PipTextTable)"/>
        /// resolves all of them.</para>
        /// </summary>
        public static string[] HeaderKeysFor(int subPage)
        {
            switch (subPage)
            {
                // PipPageInv.as:68-73.
                case WeaponsSubPage: return new[] { "ii1", "ii2", "ii3", "", "", "ii4" };

                // PipPageInv.as:180-185 — note there is no ammotip key here.
                case ArmorSubPage: return new[] { "ii1", "ii2", "ii3", "", "", "" };

                // PipPageInv.as:218-226 — `ii5` is the count column, `ii6` the category, `ii8` mass.
                default: return new[] { "ii1", "ii2", "ii5", "", "ii8", "ii6" };
            }
        }

        /// <summary>
        /// The header row's captions.
        ///
        /// <para><b>These English words are the port's, not the Oracle's</b>, and this overload is now
        /// only the no-document path — <see cref="HeaderFor(int, PipTextTable)"/> resolves the Oracle's
        /// own <c>ii*</c> keys and is what the page uses. They survive for the same reason
        /// <c>PipBuckPages.Captions</c> does: a build with the localisation <c>TextAsset</c> stripped
        /// should still show a readable grid rather than six blank headings.</para>
        ///
        /// <para>Set <c>PipPageInvView.ShowHeaderLabels</c> false to draw <see cref="HeaderKeysFor"/>
        /// instead — the Oracle's own <c>ii1 ii2 ii3</c> keys, which are less readable and entirely
        /// attested. The two modes exist because the honest one and the usable one are not the same
        /// thing, and the choice should be visible rather than baked in.</para>
        /// </summary>
        public static PipInventoryHeader HeaderFor(int subPage)
        {
            switch (subPage)
            {
                case WeaponsSubPage:
                    return new PipInventoryHeader("FAV", "WEAPON", "COND", "ROUNDS", "", "AMMO");
                case ArmorSubPage:
                    return new PipInventoryHeader("FAV", "ARMOR", "COND", "", "", "");
                case AmmoSubPage:
                    return new PipInventoryHeader("FAV", "AMMO", "COUNT", "", "MASS", "TYPE");
                default:
                    return new PipInventoryHeader("FAV", "ITEM", "COUNT", "", "MASS", "CATEGORY");
            }
        }

        /// <summary>
        /// The header row's captions, from the Oracle's own localisation document.
        ///
        /// <para><b>The key list is the Oracle's, per sub-page, and the columns are not uniform.</b>
        /// Weapons and armour put condition in column 2 and leave 3 and 4 blank; the three item pages
        /// put the stack count in column 2, mass in 4 and the category in 5. That asymmetry is in
        /// <c>PipPageInv.as</c> and is carried by <see cref="HeaderKeysFor"/> rather than guessed at
        /// here.</para>
        ///
        /// <para>A blank key resolves to the empty string — <c>Res.txt</c> returns <c>""</c> for an
        /// empty id (<c>Res.as:64-67</c>) — so a deliberately blank column stays blank.</para>
        /// </summary>
        public static PipInventoryHeader HeaderFor(int subPage, PipTextTable text)
        {
            if (text == null || text.IsEmpty) return HeaderFor(subPage);

            string[] keys = HeaderKeysFor(subPage);

            return new PipInventoryHeader(
                text.Pip(keys[0]), text.Pip(keys[1]), text.Pip(keys[2]),
                text.Pip(keys[3]), text.Pip(keys[4]), text.Pip(keys[5]));
        }

        /// <summary>
        /// The rows the page shows for a sub-page, sorted — the port of <c>setSubPages</c>'s three
        /// item loops.
        ///
        /// <para><b>The filter.</b> An entry is listed when <c>entry.SubPage == subPage</c>, plus one
        /// extra gate: a <b>hidden weapon</b> (<c>respect == 1</c>) is dropped unless
        /// <paramref name="showHidden"/> is set — <c>PipPageInv.as:92-95</c>. The Oracle also skips
        /// <c>respect == 3</c> (scheme-only) outright (<c>:79-80</c>) and skips spells the character
        /// cannot cast; both are the reader's job, because they need state the entry does not carry,
        /// and both are recorded on <c>PlayerPipInventorySource</c>.</para>
        ///
        /// <para><b>The sort.</b> One five-slot comparison stands in for the Oracle's three
        /// <c>sortOn</c> calls. The reader fills the slots, so the mapping is:</para>
        /// <list type="bullet">
        /// <item><description><b>Weapons</b> <c>[sort1, sort2, sort3, nazv]</c> (<c>:174</c>) →
        /// <c>CategoryKey</c> empty, <c>Sort1</c>=sort1, <c>Sort2</c>=sort2, <c>Sort3</c>=sort3,
        /// then name.</description></item>
        /// <item><description><b>Armour</b> <c>[trol, sort]</c> (<c>:211</c>) → <c>CategoryKey</c>=trol,
        /// <c>Sort1</c>=sort, then name.</description></item>
        /// <item><description><b>Items</b> <c>[sort, sort2, nazv]</c> (<c>:303</c>) where
        /// <c>sort == cat</c> (<c>:283</c>) → <c>CategoryKey</c>=cat, <c>Sort1</c>=sort2, then
        /// name.</description></item>
        /// </list>
        ///
        /// <para><b>One deliberate addition: the name tiebreak.</b> The Oracle's key lists do not
        /// include the name for armour, so two armours with the same <c>trol</c> and <c>sort</c> come
        /// out in whatever order the dictionary happened to iterate. The port breaks that tie on the
        /// name so the list is <i>deterministic</i> — a fixture that pinned the Oracle's arbitrary
        /// order would be pinning an accident. It cannot reorder anything the Oracle's own keys
        /// distinguish.</para>
        ///
        /// <para><b>The loaded-ammo nudge</b> (<c>:285-288</c>) is applied here rather than in the
        /// reader because it is a <i>sort</i> rule: on the ammo page, a round the equipped weapon is
        /// loaded with gets its category key prefixed with <c>"0"</c>, which floats it to the top of
        /// the list.</para>
        /// </summary>
        /// <param name="entries">Everything the player holds. May be null.</param>
        /// <param name="subPage">The Oracle's <c>page2</c>, 1..5.</param>
        /// <param name="showHidden">The Oracle's <c>pip.showHidden</c>.</param>
        public static List<PipInventoryRow> RowsFor(
            IReadOnlyList<PipInventoryEntry> entries, int subPage, bool showHidden)
        {
            var rows = new List<PipInventoryRow>();

            if (entries == null) return rows;

            if (subPage < 1 || subPage > SubPageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(subPage), subPage,
                    $"The Oracle's inventory sub-page is 1..{SubPageCount} (PipPageInv.as:33).");
            }

            var matched = new List<PipInventoryEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                PipInventoryEntry entry = entries[i];
                if (entry.SubPage != subPage) continue;

                // PipPageInv.as:92-95 — a stored weapon is listed only while showHidden is on.
                if (entry.Hidden && !showHidden) continue;

                matched.Add(entry);
            }

            matched.Sort((a, b) => Compare(a, b, subPage));

            for (int i = 0; i < matched.Count; i++)
            {
                rows.Add(BuildRow(matched[i]));
            }

            return rows;
        }

        /// <summary>
        /// The five-slot comparison described on <see cref="RowsFor"/>. Ordinal on the two string
        /// keys — AS3's default string compare is case-sensitive and code-unit ordered, which is what
        /// <see cref="StringComparer.Ordinal"/> gives.
        /// </summary>
        private static int Compare(PipInventoryEntry a, PipInventoryEntry b, int subPage)
        {
            string keyA = SortCategoryKey(a, subPage);
            string keyB = SortCategoryKey(b, subPage);

            int c = string.CompareOrdinal(keyA, keyB);
            if (c != 0) return c;

            c = a.Sort1.CompareTo(b.Sort1);
            if (c != 0) return c;

            c = a.Sort2.CompareTo(b.Sort2);
            if (c != 0) return c;

            c = a.Sort3.CompareTo(b.Sort3);
            if (c != 0) return c;

            return string.CompareOrdinal(a.Name ?? string.Empty, b.Name ?? string.Empty);
        }

        /// <summary>The primary sort key, with the ammo page's loaded-round prefix applied.</summary>
        private static string SortCategoryKey(PipInventoryEntry entry, int subPage)
        {
            string key = entry.CategoryKey ?? string.Empty;

            // PipPageInv.as:285-288 — `_loc5_.sort = "0" + _loc5_.sort` for the loaded round.
            if (subPage == AmmoSubPage && entry.IsLoadedAmmo)
            {
                key = "0" + key;
            }

            return key;
        }

        /// <summary>
        /// The port of <c>setStatItem</c>'s column writes (<c>PipPageInv.as:345-494</c>). Split out
        /// from <see cref="RowsFor"/> so the column rules are reachable by a fixture without going
        /// through the filter and sort.
        /// </summary>
        public static PipInventoryRow BuildRow(PipInventoryEntry entry)
        {
            string fav = entry.Favorite ?? string.Empty;

            // PipPageInv.as:474-484 — an unbound item shows the new-item markers in the fav column.
            if (fav.Length == 0)
            {
                if (entry.IsNew) fav = "☩";
                else if (entry.IsRecent) fav = "+";
            }

            // PipPageInv.as:142, :199 — `Math.round(hp / maxhp * 100) + "%"` where the denominator is
            // always 100 (both HealthPercent properties are already a percentage). :461 — a plain
            // count, with no suffix.
            string value = entry.ValueIsPercent
                ? PipBuckReadoutFormat.As3Round(entry.Value) + "%"
                : PipBuckReadoutFormat.As3Round(entry.Value).ToString();

            // PipPageInv.as:462-465 — the mass column is `kol * mass`, and only when the item has
            // mass. The Oracle gates the whole column on `World.w.hardInv`; that gate is applied by
            // the reader, which is the only place that knows the flag (see
            // PlayerPipInventorySource, which zeroes Mass when hard mode is off).
            string mass = string.Empty;
            if (entry.Kind == PipInventoryKind.Item && entry.Mass > 0f && entry.Count > 0)
            {
                mass = PipBuckReadoutFormat.As3Round(entry.Count * entry.Mass).ToString();
            }

            // PipPageInv.as:466-473 — the `ammo` column is the price, and only for valuables. A
            // weapon's ammo column is written unconditionally (:402-410), which is why the kind is
            // tested rather than just ShowPrice.
            string extra = entry.Kind == PipInventoryKind.Item
                ? (entry.ShowPrice ? entry.Extra ?? string.Empty : string.Empty)
                : entry.Extra ?? string.Empty;

            // PipPageInv.as:485-492 — a pending drop amount takes over the ammotip column.
            string extraTip = entry.PendingDrop > 0
                ? "DROP: " + entry.PendingDrop
                : entry.ExtraTip ?? string.Empty;

            return new PipInventoryRow(entry, fav, entry.Name ?? string.Empty, value, extra, mass, extraTip);
        }

        /// <summary>
        /// The port of <c>PipPage.setStatItems</c>'s windowing (<c>PipPage.as:983-1003</c>): the page
        /// has <c>maxrows</c> row clips, and row <i>i</i> shows entry <c>i + scroll</c>. A row whose
        /// index runs past the end is hidden rather than left showing the previous page's contents.
        /// </summary>
        /// <param name="rowIndex">The row clip's index, 0-based.</param>
        /// <param name="rowCount">How many entries there are.</param>
        /// <param name="scroll">The scroll offset, in rows.</param>
        /// <param name="maxRows">How many row clips exist.</param>
        /// <returns>The entry index this row shows, or <c>-1</c> when the row must be hidden.</returns>
        public static int EntryIndexForRow(int rowIndex, int rowCount, int scroll, int maxRows)
        {
            if (rowIndex < 0 || rowIndex >= maxRows) return -1;

            int index = rowIndex + scroll;
            if (index < 0 || index >= rowCount) return -1;

            return index;
        }

        /// <summary>
        /// The largest legal scroll offset — the Oracle's <c>arr.length - maxrows</c>
        /// (<c>PipPage.as:947</c>), clamped at 0 so a short list cannot be scrolled at all.
        /// </summary>
        public static int MaxScroll(int rowCount, int maxRows)
        {
            int max = rowCount - maxRows;
            return max > 0 ? max : 0;
        }

        /// <summary>
        /// The bottom strip — the port of <c>PipPageInv.showBottext</c>
        /// (<c>PipPageInv.as:321-343</c>): always the caps total, then the page's own mass category in
        /// hard mode.
        /// </summary>
        /// <param name="money">The Oracle's <c>pip.money</c>.</param>
        /// <param name="hardInventory">The Oracle's <c>World.w.hardInv</c>.</param>
        /// <param name="subPage">The Oracle's <c>page2</c>.</param>
        /// <param name="categoryMass">A <c>Func&lt;int,float&gt;</c> for <c>inv.retMass(cat)</c>, or null.</param>
        public static string BottomText(int money, bool hardInventory, int subPage, Func<int, float> categoryMass)
        {
            // PipPageInv.as:323 — the caps total is written unconditionally, in the `yel` class.
            string text = PipBuckReadoutFormat.Caps(money);

            if (!hardInventory || categoryMass == null) return text;

            // PipPageInv.as:324-342 — the mass category shown depends on the page, and the mapping is
            // not the page number: weapons report BOTH the weapon and magic categories (4 and 5), and
            // the three item pages report 1/3/2 for aid/misc/ammo in that order.
            switch (subPage)
            {
                case WeaponsSubPage:
                    return text + "    " + FormatMass(categoryMass(4)) + "    " + FormatMass(categoryMass(5));
                case AidSubPage:
                    return text + "    " + FormatMass(categoryMass(1));
                case MiscSubPage:
                    return text + "    " + FormatMass(categoryMass(3));
                case AmmoSubPage:
                    return text + "    " + FormatMass(categoryMass(2));
                default:
                    return text;
            }
        }

        private static string FormatMass(float mass) => PipBuckReadoutFormat.As3Round(mass).ToString();

        /// <summary>The bottom strip, from a gathered <see cref="PipInventoryFooter"/>.</summary>
        public static string BottomText(PipInventoryFooter footer, int subPage)
            => BottomText(footer.Money, footer.HardInventory, subPage, footer.CategoryMass);
    }
}
