namespace PFE.UI.PipBuck
{
    /// <summary>
    /// What an inventory row calls the thing it lists — the port of the name half of
    /// <c>fe.serv.Item</c>'s constructor (<c>Item.as:206-267</c>).
    ///
    /// <para><b>Why this is a port and not "just read the definition's display name".</b> The Oracle
    /// has no name on the row at all: <c>Item.nazv</c> is <b>assigned</b>, from the localisation
    /// document, by a rule that branches on the row's <i>type</i> and on two raw XML attributes. The
    /// port's <see cref="PFE.Data.Definitions.ItemDefinition.displayName"/> is an unrelated field —
    /// its C# initialiser is the literal <c>"Item Name"</c> and the importer never overwrites it, so
    /// every one of the 500 shipped item assets carries that placeholder. A list that printed it
    /// would say "Item Name" 500 times and look like a layout bug rather than a missing lookup. That
    /// is exactly what a play-test reported.</para>
    ///
    /// <para><b>The rule, branch for branch</b> (<c>Item.as:206-267</c>), with the shipped data's
    /// coverage measured against <c>text_en.xml</c> — the document the Oracle's own <c>Res.txt</c>
    /// reads:</para>
    /// <list type="number">
    /// <item><description><b><c>tip</c> is a weapon or an explosive</b> (<c>:206-220</c>) — named from
    /// the <b>weapon</b> table (<c>Res.txt("w", id)</c>), never the item table. A variant looks up
    /// <c>id + "^" + variant</c> first and falls back to <c>name + " - II"</c>. Measured: all 22
    /// <c>tip='e'</c> rows are in the weapon table, so every explosive names itself.</description></item>
    /// <item><description><b>Armour</b> (<c>:229-231</c>) — <c>Res.txt("a", id)</c>. Reached from the
    /// armour reader, because armour is a <c>&lt;armor&gt;</c> element and never a <c>&lt;item&gt;</c>
    /// row.</description></item>
    /// <item><description><b>A row carrying <c>@base</c></b> (<c>:240-249</c>) — named after the
    /// <b>base</b> item, with the modification in parentheses (<c>Res.pipText("am_" + @mod)</c>).
    /// This is what names the 26 ammo variants whose own id has no row: <c>p10_1</c> is
    /// "10 mm round (AP)". Measured: every one of the 26 resolves through its <c>@base</c>.</description></item>
    /// <item><description><b>Everything else</b> (<c>:252</c>) — <c>Res.txt("i", id)</c>.</description></item>
    /// <item><description><b>The schematic override</b> (<c>:257-267</c>) — when the row is
    /// <c>tip='scheme'</c> <b>and</b> the item table has no row of its own id, the name becomes
    /// <c>"Scheme «…»"</c> or <c>"Recipe «…»"</c> around the name of the item the id names after its
    /// two-character prefix (<c>id.substr(2)</c>). Measured: 52 of the 75 schematics have no row of
    /// their own and all 52 resolve this way, and all 75 carry a <c>@work</c>.</description></item>
    /// </list>
    ///
    /// <para>Together those branches name <b>500 of the 500</b> shipped item rows. That is the number
    /// the fixture pins, and it is the reason none of the branches could be dropped: each one is the
    /// only thing that names a disjoint set.</para>
    ///
    /// <para><b>Unity-free on purpose.</b> No <c>UnityEngine</c> — the same reason
    /// <see cref="PipInventoryList"/> and <see cref="PipTextTable"/> are: this branches five ways over
    /// data, and branching rules are what a compile cannot check. A plain static class can be executed
    /// from a shell against the shipped XML.</para>
    ///
    /// <para><b>The fallback is the port's, and it is deliberately last.</b> A row the document cannot
    /// name — a mod's item, or an asset imported before <c>@base</c> was read — falls back to the
    /// definition's own <c>displayName</c> when that is not the placeholder, and then to the raw id.
    /// An id on screen is ugly; <c>"Item Name"</c> 500 times is indistinguishable from a broken list,
    /// and a fabricated word would be worse than both.</para>
    /// </summary>
    public static class PipItemNames
    {
        /// <summary>
        /// The placeholder <see cref="PFE.Data.Definitions.ItemDefinition.displayName"/> ships with —
        /// the field's own C# initialiser, present on all 500 item assets because the importer never
        /// writes the field.
        ///
        /// <para>The same string, and the same treatment, as <c>SpellFacts.IsPlaceholderName</c>: the
        /// spell list already refuses to print it, and a second definition of "this asset has no name"
        /// would let the two drift.</para>
        /// </summary>
        public const string PlaceholderName = "Item Name";

        /// <summary>The Oracle's <c>L_EXPL</c> — the raw <c>@tip</c> that takes the weapon branch.</summary>
        public const string ExplosiveTip = "e";

        /// <summary>The Oracle's <c>L_SCHEME</c> — the raw <c>@tip</c> the override keys on.</summary>
        public const string SchemeTip = "scheme";

        /// <summary>
        /// The Oracle's <c>@work == "work"</c> test (<c>Item.as:262</c>): a workbench schematic reads
        /// "Scheme", every other station (lab, stove, expl) reads "Recipe".
        /// </summary>
        public const string WorkbenchStation = "work";

        /// <summary>
        /// A weapon's name — <c>Item.as:206-220</c>. Also the name of an explosive, which is a weapon
        /// row in this game's data (<c>tip='e'</c>) and is named from the same table.
        /// </summary>
        /// <param name="text">The localisation document, or null.</param>
        /// <param name="id">The weapon id.</param>
        /// <param name="variant">AS3 <c>@variant</c>; 0 is the plain weapon.</param>
        /// <param name="fallback">The definition's own name, or null. See <see cref="Finish"/>.</param>
        public static string Weapon(PipTextTable text, string id, int variant, string fallback)
        {
            // :208-210 — the plain weapon.
            if (variant == 0) return Finish(Lookup(text, "w", id), fallback, id);

            // :212-214 — a variant with its own row (`minigun^1` is "Unique minigun").
            string variantName = Lookup(text, "w", id + "^" + variant);
            if (variantName != null) return variantName;

            // :216-218 — a variant with no row of its own. The Oracle appends the literal " - II"
            // rather than degrading to the base name, so a player can tell the two apart.
            string baseName = Lookup(text, "w", id);
            return baseName != null ? baseName + " - II" : Finish(null, fallback, id);
        }

        /// <summary>An armour's name — <c>Item.as:229-231</c>, <c>Res.txt("a", id)</c>.</summary>
        public static string Armor(PipTextTable text, string id, string fallback)
            => Finish(Lookup(text, "a", id), fallback, id);

        /// <summary>
        /// An item's name — <c>Item.as:240-267</c>: the <c>@base</c> branch, the plain item table, and
        /// then the schematic override.
        /// </summary>
        /// <param name="text">The localisation document, or null.</param>
        /// <param name="id">The item id — <c>obj.id</c>.</param>
        /// <param name="legacyTip">The row's raw <c>@tip</c>. Only <see cref="ExplosiveTip"/> and
        /// <see cref="SchemeTip"/> change the answer; every other value takes the item table.</param>
        /// <param name="baseItemId">The row's raw <c>@base</c>, or empty. Read from the asset; empty on
        /// an asset imported before the importer read it, which is why the plain-id branch below still
        /// has to work.</param>
        /// <param name="modifierType">The row's raw <c>@mod</c>; 0 when absent.</param>
        /// <param name="workStation">The row's raw <c>@work</c>, or empty.</param>
        /// <param name="fallback">The definition's own name, or null. See <see cref="Finish"/>.</param>
        public static string Item(PipTextTable text, string id, string legacyTip, string baseItemId,
            int modifierType, string workStation, string fallback)
        {
            // :206 — an explosive is a weapon row and is named from the weapon table. Checked first,
            // because the row also has no @base and would otherwise take the item-table branch and miss.
            if (legacyTip == ExplosiveTip) return Weapon(text, id, 0, fallback);

            string name;

            // :240-249 — a variant is named after its BASE, not after itself.
            if (!string.IsNullOrEmpty(baseItemId))
            {
                name = Lookup(text, "i", baseItemId);

                if (name != null && modifierType != 0)
                {
                    // :247 — `" (" + Res.pipText("am_" + @mod) + ")"`. The suffix is dropped when the
                    // document has no `am_N` row, rather than printing a missing-key marker inside a
                    // name the player is reading.
                    string modifier = Pip(text, "am_" + modifierType);
                    if (modifier != null) name += " (" + modifier + ")";
                }
            }
            else
            {
                // :252.
                name = Lookup(text, "i", id);
            }

            // :257-267 — the schematic override. Gated on the item table NOT having this row's own id,
            // which is what makes it an override rather than a rename: a schematic that the document
            // names directly (`s_hsword`) keeps that name.
            if (legacyTip == SchemeTip && Lookup(text, "i", id) == null)
            {
                // `id.substr(2)` — the two-character `s_` prefix is stripped. Guarded, because
                // `Substring` past the end throws where AS3's `substr` returns "".
                string target = id != null && id.Length > 2 ? id.Substring(2) : null;
                string targetName = Lookup(text, "i", target);

                if (targetName != null)
                {
                    // :262-266 — `@work == "work"` is a workbench schematic; every other station
                    // (lab, stove, expl) makes a recipe. Both words come from the `pip` table.
                    string prefix = Pip(text, workStation == WorkbenchStation ? "scheme1" : "recipe");
                    if (prefix != null) name = prefix + " «" + targetName + "»";
                }
            }

            return Finish(name, fallback, id);
        }

        /// <summary>
        /// Whether a name is the importer's placeholder rather than a name — the test
        /// <see cref="Finish"/> applies to <c>fallback</c>.
        /// </summary>
        public static bool IsPlaceholder(string name)
            => string.IsNullOrWhiteSpace(name) || name == PlaceholderName;

        /// <summary>
        /// The last step of every branch: the document's answer, else the definition's own name when it
        /// is a name, else the id. See the class remarks for why an id beats a fabricated word.
        /// </summary>
        private static string Finish(string fromDocument, string fallback, string id)
        {
            if (fromDocument != null) return fromDocument;
            if (!IsPlaceholder(fallback)) return fallback;
            return id ?? string.Empty;
        }

        /// <summary>
        /// <c>Res.txt(alias, id)</c> at the <c>n</c> child, or <b>null</b> when the document has no
        /// such row.
        ///
        /// <para><b>Null, not the missing-key marker.</b> <see cref="PipTextTable.Text"/> reproduces
        /// the Oracle's <c>*table_id</c> marker, which is right for a caption and wrong here: this
        /// method's result is either used as a name or discarded in favour of a fallback, and a marker
        /// that reads as a name would hide the fact that the lookup failed. <c>TryGet</c> is the
        /// marker-free form of the same lookup.</para>
        /// </summary>
        private static string Lookup(PipTextTable text, string alias, string id)
        {
            if (text == null || string.IsNullOrEmpty(id)) return null;

            return text.TryGet(alias, id, 0, out string value) && !string.IsNullOrEmpty(value)
                ? value
                : null;
        }

        /// <summary><c>Res.pipText(id)</c> — the same lookup through the PipBuck's own table.</summary>
        private static string Pip(PipTextTable text, string id) => Lookup(text, "p", id);
    }
}
