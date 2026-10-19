namespace PFE.Systems.Inventory
{
    /// <summary>
    /// What using an item from the PipBuck does — the branch <c>Invent.useItem</c> selects
    /// (<c>Invent.as:546-731</c>).
    /// </summary>
    public enum ItemUseOutcome
    {
        /// <summary>The Oracle refuses, with a message key. Nothing is consumed.</summary>
        Refused = 0,

        /// <summary><c>gg.changeSpell(id)</c> — selects (or toggles off) the spell. <b>Not</b> a cast.</summary>
        Spell = 1,

        /// <summary><c>usePotion(id)</c> — heals, applies an effect, rolls an addiction, consumes one.</summary>
        Potion = 2,

        /// <summary>A book: <c>addPerk</c> or <c>upSkill</c>, plus one <c>lbook</c>, consumes the book.</summary>
        Book = 3,

        /// <summary><c>sphera</c> — one skill point, consumes it.</summary>
        SkillPoint = 4,

        /// <summary><c>@chdif</c> — changes the difficulty tier.</summary>
        Difficulty = 5,

        /// <summary><c>mworkbench</c> / <c>mworkexpl</c> / <c>mworklab</c> — opens the crafting page.</summary>
        Workshop = 6,

        /// <summary><c>@text</c> — shows the item's document and consumes it.</summary>
        Reading = 7,

        /// <summary><c>@paint</c> — recolours the held weapon.</summary>
        Paint = 8,

        /// <summary><c>@pet</c> — summons the item's pet.</summary>
        Pet = 9,

        /// <summary><c>rep</c> — repairs the held weapon.</summary>
        RepairWeapon = 10,

        /// <summary><c>stealth</c> — applies the stealth effect.</summary>
        Stealth = 11
    }

    /// <summary>
    /// The branch <see cref="ItemUseRules.Decide"/> chose, plus the Oracle's own refusal key.
    /// </summary>
    public readonly struct ItemUseDecision
    {
        public ItemUseDecision(ItemUseOutcome outcome, string refusalKey = null)
        {
            Outcome = outcome;
            RefusalKey = refusalKey;
        }

        public ItemUseOutcome Outcome { get; }

        /// <summary>
        /// The <c>gui.infoText</c> key the Oracle prints when it refuses — <c>noUseCombat</c>,
        /// <c>alicornNot</c>, <c>noBase</c>. Null when the decision is not a refusal, and also for the
        /// two silent refusals (<c>runa</c>/<c>reboot</c>), which the Oracle declines without a word.
        /// </summary>
        public string RefusalKey { get; }

        public bool IsRefused => Outcome == ItemUseOutcome.Refused;
    }

    /// <summary>
    /// <c>Invent.useItem(id)</c>'s guard ladder, as a pure function.
    ///
    /// <para><b>Why this is separated from the effect.</b> The interesting part of <c>useItem</c> is not
    /// "subtract one and heal" — it is <i>which</i> of fifteen branches an item takes, and the Oracle
    /// orders them so that several are easy to get wrong: <c>mworkbench</c> is tested by <b>id</b>
    /// before the <c>tip</c> branches and before the stack check; <c>@paint</c> and <c>@text</c> are
    /// tested by <b>attribute presence</b> before <c>tip</c>; a <c>food</c> item and a <c>med</c> item
    /// reach the same <c>usePotion</c> through different guards. The ordering is the rule, so it is
    /// expressed here where an offline fixture can pin it, and
    /// <see cref="PlayerInventory.UseItem"/> only carries out the answer.</para>
    ///
    /// <para><b>Nothing here reads Unity, the inventory or the player.</b> Every fact it branches on is
    /// a parameter, which is what lets the whole ladder be tested without a scene.</para>
    /// </summary>
    public static class ItemUseRules
    {
        /// <summary>The Oracle's keys for the four refusals this ladder can produce.</summary>
        public const string RefusalNoCombat = "noUseCombat";
        public const string RefusalAlicorn = "alicornNot";
        public const string RefusalNoBase = "noBase";
        public const string RefusalNoHeal = "noHeal";

        /// <summary><c>Invent.as:563-571</c> — the three crafting benches, tested by id.</summary>
        public static bool IsWorkshopItem(string id)
            => id == "mworkbench" || id == "mworkexpl" || id == "mworklab";

        /// <summary>
        /// The three tips <c>usePotion</c> accepts directly (<c>Invent.as:620-623</c>). <c>food</c> is
        /// <b>not</b> one of them: it reaches the same function through two extra guards
        /// (<c>:624-633</c>).
        /// </summary>
        public static bool IsPotionTip(string tip)
            => tip == "med" || tip == "him" || tip == "pot";

        /// <summary>
        /// The ladder. Parameters mirror exactly the facts <c>useItem</c> reads, and nothing else —
        /// a parameter that no branch consults would be a second source of truth for the same answer.
        /// </summary>
        /// <param name="id">The item id. Several branches are id tests, not tip tests.</param>
        /// <param name="tip">The row's <c>@tip</c>.</param>
        /// <param name="hasPaint"><c>@paint</c> is present and non-empty.</param>
        /// <param name="hasText"><c>@text</c> is present and non-empty.</param>
        /// <param name="hasPet"><c>@pet</c> is present and non-empty.</param>
        /// <param name="hasDifficulty"><c>@chdif</c> is present and non-empty.</param>
        /// <param name="inBattle">AS3 <c>World.w.t_battle &gt; 0</c>.</param>
        /// <param name="atBase">AS3 <c>World.w.loc.base</c>.</param>
        /// <param name="hardInventory">AS3 <c>World.w.hardInv</c>.</param>
        /// <param name="alicorn">AS3 <c>World.w.alicorn</c> — the "you are the alicorn" mode.</param>
        public static ItemUseDecision Decide(
            string id, string tip,
            bool hasPaint, bool hasText, bool hasPet, bool hasDifficulty,
            bool inBattle, bool atBase, bool hardInventory, bool alicorn)
        {
            // :563-571 — the benches come FIRST, before the stack check and before every tip branch.
            // They also return false, i.e. they are never consumed; opening the crafting page is not
            // using the object up.
            if (IsWorkshopItem(id))
            {
                return inBattle
                    ? new ItemUseDecision(ItemUseOutcome.Refused, RefusalNoCombat)
                    : new ItemUseDecision(ItemUseOutcome.Workshop);
            }

            // :576-583 — `@paint`, then `@text`. Both are attribute-presence tests, so an item whose
            // tip is also `med` still recolours rather than healing.
            if (hasPaint) return new ItemUseDecision(ItemUseOutcome.Paint);

            if (hasText)
            {
                return inBattle
                    ? new ItemUseDecision(ItemUseOutcome.Refused, RefusalNoCombat)
                    : new ItemUseDecision(ItemUseOutcome.Reading);
            }

            // :620-623 — med / him / pot. No guards at all: a stim is usable in a firefight.
            if (IsPotionTip(tip)) return new ItemUseDecision(ItemUseOutcome.Potion);

            // :624-633 — food: the alicorn cannot eat, and eating mid-fight is refused.
            if (tip == "food")
            {
                if (alicorn) return new ItemUseDecision(ItemUseOutcome.Refused, RefusalAlicorn);
                if (inBattle) return new ItemUseDecision(ItemUseOutcome.Refused, RefusalNoCombat);
                return new ItemUseDecision(ItemUseOutcome.Potion);
            }

            // :634-638 — a spell item selects the spell; the cast happens later, on the Def key.
            if (tip == "spell")
            {
                return alicorn
                    ? new ItemUseDecision(ItemUseOutcome.Refused, RefusalAlicorn)
                    : new ItemUseDecision(ItemUseOutcome.Spell);
            }

            // :639-661 — books and the skill-point orb share the same two gates.
            if (tip == "book" || id == "sphera")
            {
                if (inBattle) return new ItemUseDecision(ItemUseOutcome.Refused, RefusalNoCombat);
                if (hardInventory && !atBase) return new ItemUseDecision(ItemUseOutcome.Refused, RefusalNoBase);

                return new ItemUseDecision(tip == "book" ? ItemUseOutcome.Book : ItemUseOutcome.SkillPoint);
            }

            // :662-664 — two ids the Oracle declines without a message.
            if (id == "runa" || id == "reboot") return new ItemUseDecision(ItemUseOutcome.Refused);

            // :665-670 — the weapon repair kit.
            if (id == "rep") return new ItemUseDecision(ItemUseOutcome.RepairWeapon);

            // :671-679 — the stealth field generator.
            if (id == "stealth")
            {
                return alicorn
                    ? new ItemUseDecision(ItemUseOutcome.Refused, RefusalAlicorn)
                    : new ItemUseDecision(ItemUseOutcome.Stealth);
            }

            // :680-694 — a pet in a box, then a difficulty token.
            if (hasPet)
            {
                return alicorn
                    ? new ItemUseDecision(ItemUseOutcome.Refused, RefusalAlicorn)
                    : new ItemUseDecision(ItemUseOutcome.Pet);
            }

            if (hasDifficulty) return new ItemUseDecision(ItemUseOutcome.Difficulty);

            // :697-700 — `if(!_loc2_.@chdif.length()) return false;` — the fall-through refuses.
            return new ItemUseDecision(ItemUseOutcome.Refused);
        }

        /// <summary>
        /// Whether the branch consumes one unit — i.e. whether it reaches
        /// <c>this.minusItem(param1)</c> at <c>Invent.as:721</c>.
        ///
        /// <para><b>Five branches return before that line, and every one of them is easy to get
        /// wrong</b> because the code <i>looks</i> like a use. The tell is the return value, not the
        /// action:</para>
        /// <list type="bullet">
        /// <item><c>mworkbench</c>/<c>mworkexpl</c>/<c>mworklab</c> — <c>return false</c> at
        /// <c>:568-570</c>. Opening a bench does not eat the bench.</item>
        /// <item><c>@paint</c> — <c>return true</c> at <c>:588</c>, one line <i>before</i>
        /// <c>minusItem</c>. A paint can is reusable.</item>
        /// <item><c>@text</c> — <c>return true</c> at <c>:602</c>. A readable document is kept; the
        /// same is true of the <c>lbook</c> a skill book yields, so books are the one case where a
        /// consumption and a non-consumption sit side by side.</item>
        /// <item><c>tip == "spell"</c> — <c>return false</c> at <c>:637</c>, right after
        /// <c>changeSpell</c>. The nine supportive-magic items are permanent: owning the item <i>is</i>
        /// owning the spell, and "using" it only selects the spell.</item>
        /// <item><c>@pet</c> — <c>return true</c> at <c>:690</c>. The summoning item is kept.</item>
        /// </list>
        /// </summary>
        public static bool ConsumesItem(ItemUseOutcome outcome)
        {
            switch (outcome)
            {
                case ItemUseOutcome.Potion:
                case ItemUseOutcome.Book:
                case ItemUseOutcome.SkillPoint:
                case ItemUseOutcome.RepairWeapon:
                case ItemUseOutcome.Stealth:
                case ItemUseOutcome.Difficulty:
                    return true;

                default:
                    // Paint, Reading, Pet, Spell, Workshop, Refused.
                    return false;
            }
        }
    }
}
