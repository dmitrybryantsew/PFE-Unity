namespace PFE.Systems.Loot
{
    /// <summary>
    /// One item the roller produced — the port's stand-in for the <c>Item</c> object
    /// <c>LootGen.newLoot</c> constructs before handing it to <c>new Loot(...)</c>
    /// (<c>LootGen.as:231-293</c>).
    ///
    /// <para><b>What is deliberately not here.</b> AS3's <c>Item</c> carries the whole item model (nazv,
    /// mass, invCat, price, xml …). The roller's job is to decide <i>what</i> drops and in <i>what
    /// state</i>; the item model belongs to the inventory half. So this carries the identity plus the
    /// three fields the roll itself computes: the stack size, the <c>lootBroken</c> HP multiplier, and
    /// the condition.</para>
    /// </summary>
    public readonly struct LootRoll
    {
        /// <summary>The pool key that produced it — AS3 <c>Item.tip</c>, after the <c>eda</c>/<c>co</c>
        /// rewrite (<c>LootGen.as:232-241</c>) and after the <c>@tip</c> override an <c>item</c>-tip row
        /// gets from its own XML (<c>Item.as:254-257</c>).</summary>
        public readonly string Tip;

        /// <summary>
        /// The resolved item or weapon id, with any <c>^N</c> variant suffix <b>stripped</b> — AS3
        /// <c>Item.id</c> after <c>:132-140</c>. A <c>uniq</c> roll therefore reports <c>lsword</c> with
        /// <see cref="Variant"/> 1, not the string <c>"lsword^1"</c>.
        /// </summary>
        public readonly string ItemId;

        /// <summary>AS3 <c>Item.variant</c> — non-zero only for <c>^N</c> ids (<c>Item.as:132-136</c>).</summary>
        public readonly int Variant;

        /// <summary>
        /// AS3 <c>Item.kol</c> — the stack size, <b>after</b> the <c>money</c>/<c>bit</c> perk
        /// multipliers and the <c>lootBroken</c> halving (<c>LootGen.as:245-256</c>). Weapons and armour
        /// are forced to 1 (<c>Item.as:176-178</c>) whatever <c>kol</c> was asked for.
        /// </summary>
        public readonly int Quantity;

        /// <summary>
        /// AS3 <c>Item.sost</c> — condition, <c>1</c> unless a weapon/armour was rolled with
        /// <c>kol == 0</c> (<c>0.05..0.20</c>) or <c>kol == 1</c> (<c>0.60..0.85</c>)
        /// (<c>Item.as:185-193</c>). <b>This consumes the RNG</b>, so it cannot be skipped without
        /// shifting every later roll.
        /// </summary>
        public readonly float Condition;

        /// <summary>
        /// AS3 <c>Item.imp</c> — the "important" flag that <b>skips the per-run <c>@limit</c> check</b>
        /// (<c>LootGen.as:261</c>, guarded on <c>param5 == 0</c>). Note that <b>no table call site ever
        /// passes it</b>: all 267 <c>newLoot</c> sites in <c>lootCont</c>/<c>lootDrop</c> use the
        /// default 0, so the limit check is always live for a table roll. Only <c>lootId</c> — the
        /// container's own <c>&lt;item&gt;</c> children — passes 1 or 2 (<c>Interact.as:1987-1995</c>).
        /// </summary>
        public readonly bool Important;

        /// <summary>
        /// AS3 <c>Item.multHP</c> — <c>1</c> normally, <c>0.4</c> when the container was broken open,
        /// further halved to <c>0.2</c> for a weapon whose pool lookup failed at the level-filtered
        /// attempt (<c>LootGen.as:179-206</c>).
        /// </summary>
        public readonly float HpMultiplier;

        /// <summary>
        /// The table key that produced this roll (<c>"chest"</c>, <c>"raider"</c>, …). Carried so a
        /// caller can report provenance — and so an unmatched key can be reported rather than silently
        /// yielding nothing (Q1).
        /// </summary>
        public readonly string SourceTable;

        public LootRoll(string tip, string itemId, int variant, int quantity, float condition, bool important,
                        float hpMultiplier, string sourceTable)
        {
            Tip = tip;
            ItemId = itemId;
            Variant = variant;
            Quantity = quantity;
            Condition = condition;
            Important = important;
            HpMultiplier = hpMultiplier;
            SourceTable = sourceTable;
        }

        public override string ToString()
        {
            string v = Variant > 0 ? "^" + Variant : string.Empty;
            return ItemId + v + " x" + Quantity + " (" + Tip + ")" + (Important ? " [imp]" : string.Empty);
        }
    }
}
