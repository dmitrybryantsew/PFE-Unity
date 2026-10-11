namespace PFE.Systems.Loot
{
    /// <summary>
    /// The AS3 <c>Item.L_*</c> pool-key constants — <c>fe/serv/Item.as:9-57</c>.
    ///
    /// <para><b>Why these are strings and not the port's <see cref="PFE.Data.Definitions.ItemType"/>
    /// enum.</b> The loot tables are keyed on the AS3 <c>tip</c> string, and that string is finer-grained
    /// than the enum: <c>FixDataImport.GetItemTypeFromSource</c>
    /// (<c>Editor/Importers/FixDataImport.cs:140-144</c>) collapses <c>compa</c>/<c>compm</c>/<c>compe</c>/
    /// <c>compp</c>/<c>compw</c> all to <c>ItemType.Component</c>, so <c>newLoot(0.85, L_COMPA)</c> cannot
    /// be expressed against it. Porting the constants verbatim keeps the tables readable beside the
    /// oracle.</para>
    ///
    /// <para>Note <c>L_AMMO</c> is <c>"a"</c> and <c>L_EXPL</c> is <c>"e"</c> — one-letter keys, not
    /// typos.</para>
    /// </summary>
    public static class LootTips
    {
        public const string Item = "item";
        public const string Armor = "armor";
        public const string Weapon = "weapon";
        public const string Uniq = "uniq";
        public const string Spell = "spell";
        public const string Ammo = "a";
        public const string Expl = "e";
        public const string Med = "med";
        public const string Book = "book";
        public const string Him = "him";
        public const string Pot = "pot";
        public const string Food = "food";
        public const string Scheme = "scheme";
        public const string Paint = "paint";
        public const string Compa = "compa";
        public const string Compw = "compw";
        public const string Compe = "compe";
        public const string Compm = "compm";
        public const string Compp = "compp";
        public const string Spec = "spec";
        public const string Instr = "instr";
        public const string Stuff = "stuff";
        public const string Art = "art";
        public const string Impl = "impl";
        public const string Key = "key";

        /// <summary>
        /// AS3 <c>Item.itemTip</c> (<c>Item.as:59</c>) — the tips that are real item-pool categories. The
        /// two <c>tip2</c> keys the tables also pass (<c>"eda"</c>, <c>"co"</c>) are deliberately absent.
        /// </summary>
        public static readonly string[] ItemTips =
        {
            Weapon, Spell, Ammo, Expl, Med, Book, Him, Scheme, Compa, Compw, Compe, Compm, Compp,
            Paint, Art, Impl, Key,
        };

        /// <summary>
        /// The <c>tip2</c> keys the tables pass as literal pool keys — <c>LootGen.as:232-241</c> rewrites
        /// them to a real tip at roll time (<c>eda → food</c>, <c>co → scheme</c>).
        /// </summary>
        public const string Tip2Eda = "eda";
        public const string Tip2Co = "co";
    }
}
