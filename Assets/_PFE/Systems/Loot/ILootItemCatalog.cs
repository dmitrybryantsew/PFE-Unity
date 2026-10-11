namespace PFE.Systems.Loot
{
    /// <summary>
    /// The subset of an <c>AllData</c> item/weapon row that <c>Item</c>'s constructor reads when
    /// <c>newLoot</c> builds an item (<c>Item.as:121-312</c>). Supplied by an injected catalog so the
    /// roller stays free of Unity types and of the importer.
    ///
    /// <para><b>Why the roller needs the item's own attributes at all.</b> <c>newLoot</c> cannot finish
    /// without them: the stack size comes from <c>@kol</c> when the caller passed none
    /// (<c>Item.as:195-205</c>), the condition comes from the caller's <c>kol</c> argument
    /// (<c>:176-193</c>), and the per-run limit check reads <c>@limit</c>/<c>@mlim</c>/<c>@maxlim</c>
    /// (<c>LootGen.as:261-286</c>). A roller that skipped the lookup would emit every stackable item at
    /// quantity 1 and never enforce a limit.</para>
    /// </summary>
    public readonly struct LootItemRow
    {
        /// <summary>AS3 <c>@id</c> (already variant-stripped by the caller).</summary>
        public readonly string Id;

        /// <summary>
        /// AS3 the row's own <c>@tip</c> — read only for an <c>item</c>-tip roll, where it <b>replaces</b>
        /// the tip (<c>Item.as:254-257</c>). This is how <c>newLoot(…, L_ITEM, "money")</c> ends up with
        /// <c>Item.tip == "money"</c>, which is what makes the money multiplier fire. Empty when absent.
        /// </summary>
        public readonly string Tip;

        /// <summary>AS3 <c>@kol</c> — the authored stack size. <c>null</c> = absent.</summary>
        public readonly int? Kol;

        /// <summary>AS3 <c>@limit</c> — the per-run limit key. Empty when absent.</summary>
        public readonly string LimitKey;

        /// <summary>AS3 <c>@mlim</c> — multiplier on the run's loot limit. <c>null</c> = absent (treat as 1).</summary>
        public readonly float? LimitMultiplier;

        /// <summary>AS3 <c>@maxlim</c> — an absolute cap. <c>null</c> = absent (no absolute cap).</summary>
        public readonly int? LimitAbsolute;

        /// <summary>
        /// AS3 <c>Armor.tip</c> for armour rows. Only <c>3</c> is load-bearing here: an amulet
        /// (<c>tip == "3"</c>) is forced to condition 1 with no RNG draw (<c>Item.as:179-182</c>).
        /// Empty for items and weapons.
        /// </summary>
        public readonly string ArmorTip;

        public LootItemRow(string id, string tip, int? kol = null, string limitKey = null,
                           float? limitMultiplier = null, int? limitAbsolute = null, string armorTip = null)
        {
            Id = id;
            Tip = tip;
            Kol = kol;
            LimitKey = limitKey;
            LimitMultiplier = limitMultiplier;
            LimitAbsolute = limitAbsolute;
            ArmorTip = armorTip;
        }
    }

    /// <summary>
    /// Resolves an id to the row facts the roll needs — the port's stand-in for AS3's
    /// <c>AllData.d.item.(@id == id)</c> / <c>.weapon</c> / <c>.armor</c> lookups (<c>Item.as:151-170</c>,
    /// <c>:314-348</c>).
    ///
    /// <para><b>The lookup order is AS3's, and it is not "search everything".</b> <c>Item</c> picks the
    /// collection from the <i>tip</i>: <c>armor</c> → <c>d.armor</c>, <c>weapon</c> → <c>d.weapon</c>,
    /// anything else → <c>d.item</c>. So the interface takes both, and an implementation must not fall
    /// back to a global search — a weapon id that also existed as an item id would resolve differently
    /// from the oracle.</para>
    ///
    /// <para>The one exception is <c>tip == ""</c>, where <c>Item</c> runs <c>itemTip()</c> and tries
    /// item → weapon → armour in that order (<c>:314-348</c>). That path exists for
    /// <c>LootGen.lootId</c>, which calls <c>newLoot(1, "", id, …)</c>.</para>
    /// </summary>
    public interface ILootItemCatalog
    {
        /// <summary>
        /// Find the row for a tip/id pair. <paramref name="tip"/> is the <b>effective</b> tip — the
        /// caller has already mapped <c>uniq</c> to <c>weapon</c> (<c>Item.as:147-150</c>) and stripped
        /// any <c>^N</c> suffix. Returns <c>null</c> when the id is unknown, which is not an error: the
        /// oracle proceeds with a null <c>xml</c> and a stack size of whatever it was given.
        /// </summary>
        LootItemRow? Find(string tip, string id);

        /// <summary>
        /// AS3 <c>Item.itemTip()</c> (<c>:314-348</c>) — resolve an id with no tip by trying
        /// <c>item</c>, then <c>weapon</c>, then <c>armor</c>. Returns the row and the tip it resolved
        /// under via <paramref name="resolvedTip"/>.
        /// </summary>
        LootItemRow? FindByAnyId(string id, out string resolvedTip);
    }
}
