using System;
using System.Collections.Generic;
using PFE.Systems.Campaign;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The seven price coefficients and the two cap thresholds a doctor charges with — the port of
    /// <c>PipPageMed</c>'s own fields (<c>PipPageMed.as:16-32</c>).
    ///
    /// <para><b>Two of the nine are constants and seven are not, and the split is the Oracle's.</b>
    /// <c>setSubPages</c> copies all seven <c>price*</c> values off <c>Pers</c>
    /// (<c>PipPageMed.as:64-70</c>), but <c>Pers.setGlobalDif</c> only ever writes five of them
    /// (<c>Pers.as:769-771, 784-786, 799-801, 814-816, 831-833</c>): <c>priceBlood</c>,
    /// <c>priceOrgan</c>, <c>priceRad</c>, <c>pricePoison</c> and <c>priceCut</c>. <c>priceHP</c>
    /// (0.5) and <c>priceMana</c> (1) keep their <c>Pers</c> field initialisers at every difficulty —
    /// <c>Pers.as:91</c> and <c>:97</c> — so they are constants here, not table cells. Deriving them
    /// from the difficulty row would be inventing a curve the Oracle does not have.</para>
    ///
    /// <para><b><c>raz</c>/<c>razMana</c> are not prices.</b> They are the most HP one click can buy
    /// (<c>PipPageMed.as:97-99</c>): an organ 3 HP below its ceiling costs 1.5 caps, an organ 300 below
    /// costs <c>raz * priceOrgan</c> — 100 caps, not 150. The mana organ has its own, smaller cap.</para>
    /// </summary>
    public readonly struct PipMedPrices
    {
        /// <summary><c>PipPageMed.as:30</c> — <c>raz</c>, the organ purchase ceiling.</summary>
        public const int DefaultRaz = 200;

        /// <summary><c>PipPageMed.as:32</c> — <c>razMana</c>, the mana organ's own ceiling.</summary>
        public const int DefaultRazMana = 100;

        /// <summary><c>Pers.as:91</c> — never written by <c>setGlobalDif</c>.</summary>
        public const float DefaultHpPrice = 0.5f;

        /// <summary><c>Pers.as:97</c> — never written by <c>setGlobalDif</c>.</summary>
        public const float DefaultManaPrice = 1f;

        public PipMedPrices(float hp, float blood, float rad, float organ, float cut, float poison,
            float mana, int raz = DefaultRaz, int razMana = DefaultRazMana)
        {
            Hp = hp;
            Blood = blood;
            Rad = rad;
            Organ = organ;
            Cut = cut;
            Poison = poison;
            Mana = mana;
            Raz = raz;
            RazMana = razMana;
        }

        /// <summary><c>priceHP</c> — caps per HP of the health pool.</summary>
        public float Hp { get; }

        /// <summary><c>priceBlood</c> — caps per HP of the blood organ.</summary>
        public float Blood { get; }

        /// <summary><c>priceRad</c> — caps per point of radiation.</summary>
        public float Rad { get; }

        /// <summary><c>priceOrgan</c> — caps per HP of head, torso and legs.</summary>
        public float Organ { get; }

        /// <summary><c>priceCut</c> — caps per point of open wounds.</summary>
        public float Cut { get; }

        /// <summary><c>pricePoison</c> — caps per point of poison.</summary>
        public float Poison { get; }

        /// <summary><c>priceMana</c> — caps per HP of the mana organ.</summary>
        public float Mana { get; }

        /// <summary><c>raz</c> — the most organ HP one click can buy.</summary>
        public int Raz { get; }

        /// <summary><c>razMana</c> — the most mana-organ HP one click can buy.</summary>
        public int RazMana { get; }

        /// <summary>
        /// The Oracle's own field initialisers (<c>PipPageMed.as:16-32</c>), i.e. what the page charges
        /// before <c>setSubPages</c> overwrites it. Also the Normal-difficulty row, since
        /// <c>Pers.setGlobalDif(2)</c> writes exactly these five values (<c>Pers.as:799-801</c>).
        /// </summary>
        public static PipMedPrices Default =>
            new PipMedPrices(DefaultHpPrice, 0.5f, 1f, 0.5f, 4f, 4f, DefaultManaPrice);

        /// <summary>
        /// The five difficulty-dependent prices for <paramref name="globalDif"/>, with the two
        /// constants left alone — the port of <c>PipPageMed.setSubPages</c>'s copy off
        /// <c>Pers</c> (<c>PipPageMed.as:64-70</c>) after <c>Pers.setGlobalDif</c> has run.
        ///
        /// <para><b>Read from the port's own difficulty table, not re-transcribed.</b>
        /// <see cref="DifficultyRules.PersTable"/> is already the verbatim transcription of
        /// <c>Pers.as:759-839</c>, and its <c>Price*</c> cells were recorded as "spec only" precisely
        /// so that the day a consumer landed the values would already be correct. This is that
        /// consumer.</para>
        /// </summary>
        public static PipMedPrices ForDifficulty(int globalDif)
        {
            PersDifficultyTable row = DifficultyRules.PersTable(globalDif);
            return new PipMedPrices(
                DefaultHpPrice, row.PriceBlood, row.PriceRad, row.PriceOrgan,
                row.PriceCut, row.PricePoison, DefaultManaPrice);
        }
    }

    /// <summary>
    /// One row of the doctor's price list — the Oracle's <c>arr</c> entry
    /// (<c>PipPageMed.as:85-192</c>).
    ///
    /// <para><b>Four visible fields plus the two the click needs.</b> <c>setStatItem</c>
    /// (<c>PipPageMed.as:197-224</c>) writes <c>nazv</c> (label), <c>numb</c> (the reading), <c>price</c>
    /// and optionally <c>hpbar</c>; <c>id</c> is written and then <b>hidden</b> (<c>:201-207</c>), because
    /// the Oracle uses it as the row's key rather than as text. The port keeps <see cref="Id"/> for the
    /// same reason — it is what <c>itemClick</c> dispatches on — and <see cref="Price"/> numerically,
    /// because the Oracle re-derives the cost inside <c>itemClick</c> and the two must not be able to
    /// disagree about what the row advertised.</para>
    /// </summary>
    public readonly struct PipMedRow
    {
        /// <summary>No bar — the Oracle's <c>param2.bar == null</c>. Only <c>hp</c> and the five organs have one.</summary>
        public const float NoBar = -1f;

        public PipMedRow(string id, string name, string value, float price, float bar = NoBar,
            bool hasPrice = true)
        {
            Id = id ?? string.Empty;
            Name = name ?? string.Empty;
            Value = value ?? string.Empty;
            Price = price;
            Bar = bar;
            HasPrice = hasPrice;
        }

        /// <summary>
        /// The row's key — <c>hp</c>, <c>organism</c>, <c>statHead1</c>, <c>rad</c>… The Oracle writes it
        /// into the row clip's hidden <c>id</c> field and dispatches on it
        /// (<c>PipPageMed.as:287-441</c>), so it is <b>behaviour, not a label</b>: the trailing digit is
        /// the organ's trauma stage (<c>:106</c>), and the prefix is which branch the click takes.
        /// </summary>
        public string Id { get; }

        /// <summary>The label column — the Oracle's <c>nazv</c>.</summary>
        public string Name { get; }

        /// <summary>The reading column — the Oracle's <c>numb</c>, already formatted.</summary>
        public string Value { get; }

        /// <summary>
        /// What one click on this row costs, before the balance clamp — the Oracle's <c>_loc2_</c>.
        /// Meaningless when <see cref="HasPrice"/> is false.
        /// </summary>
        public float Price { get; }

        /// <summary>0..1 for the row's <c>hpbar</c>, or <see cref="NoBar"/>.</summary>
        public float Bar { get; }

        /// <summary>
        /// Whether the Oracle sets <c>price.text</c> at all (<c>PipPageMed.as:211-218</c>). False for the
        /// <c>organism</c> section header, which pushes no <c>price</c> key and so gets an empty field.
        /// </summary>
        public bool HasPrice { get; }

        /// <summary>Whether the Oracle would show <c>hpbar</c> for this row.</summary>
        public bool HasBar => Bar > NoBar;

        /// <summary>The Oracle's <c>Math.round(param2.price)</c> — the number the player is quoted.</summary>
        public int DisplayPrice => PipBuckReadoutFormat.As3Round(Price);
    }

    /// <summary>
    /// Everything the doctor's page reads off the player, as plain data — the inputs of
    /// <c>PipPageMed.setSubPages</c> (<c>PipPageMed.as:63-192</c>) and of <c>itemClick</c>
    /// (<c>:278-450</c>).
    ///
    /// <para><b>The organ readings are the same five the Health sub-page shows, and that is not a
    /// coincidence.</b> The Oracle reads <c>gg.pers.headHP</c> and friends on both pages, so a doctor
    /// charging for a repair and the character sheet reporting the wound cannot disagree — they are one
    /// set of numbers. <c>PipStatVitals</c> already carries them; this type exists separately because
    /// the med page also needs the <i>prices</i>, the <i>balance</i> and the <i>level</i>, which the
    /// character sheet has no business knowing.</para>
    /// </summary>
    public sealed class PipMedSnapshot
    {
        /// <summary>False when there is no player — every row then reads zero and every click does nothing.</summary>
        public bool HasPlayer;

        /// <summary>
        /// What the open doctor charges. <b>Part of the snapshot, not a second argument</b>, so the rows
        /// the page draws and the price a click charges cannot be built from two different tables.
        /// </summary>
        public PipMedPrices Prices = PipMedPrices.Default;

        /// <summary><c>gg.pers.level</c> — the charity rule's third operand (<c>PipPageMed.as:343</c>).</summary>
        public int Level;

        // ── The health pool (the UNIT's, not the organs') ─────────────────────
        //
        // PipPageMed.as:88 — `Math.round(gg.hp) + "/" + Math.round(gg.maxhp)`. These are the unit's
        // pool, which is why they are separate from the organ readings below.

        public float Hp, MaxHp;

        // ── The five organs (Pers's) ──────────────────────────────────────────

        public float HeadHp, TorsoHp, LegsHp, BloodHp, ManaOrganHp;

        /// <summary><c>Pers.inMaxHP</c> — the denominator of the four physical organs.</summary>
        public float InMaxHp;

        /// <summary><c>Pers.inMaxMana</c> — the denominator of the mana organ.</summary>
        public float InMaxMana;

        /// <summary>
        /// The trauma stage of each organ, 0..3 — <b>part of the row's id</b> (<c>PipPageMed.as:106</c>),
        /// exactly as on the character sheet. Carried so the id can be built rather than guessed.
        /// </summary>
        public int HeadSt, TorsoSt, LegsSt, BloodSt, ManaSt;

        // ── The three exposure readings ───────────────────────────────────────
        //
        // `gg.rad`, `gg.cut`, `gg.poison` — PipPageMed.as:172-192. The Oracle pushes these three rows
        // UNCONDITIONALLY. The port keeps no radiation, wound or poison value at all (see
        // PipStatVitals.HasExposureReadings), so the flag is what stops the page printing three zeroes
        // with a zero price for readings nothing in the port takes. Same arrangement, and the same
        // reason, as the character sheet's Health sub-page.

        /// <summary>
        /// Whether the radiation reading exists. <b>True now that the port has a radiation model</b> —
        /// <c>PlayerRadiationState.Rad</c>.
        /// </summary>
        public bool HasExposureReadings;

        /// <summary>
        /// Whether the wound and poison readings exist. <b>Still false</b>: the port keeps neither, so
        /// the clinic omits those two rows rather than pricing a treatment it cannot perform.
        ///
        /// <para>Separate from <see cref="HasExposureReadings"/> for the reason recorded on
        /// <c>PipStatVitals.HasWoundReadings</c>: one flag used to cover all three because all three
        /// were absent for the same reason, and radiation no longer is.</para>
        /// </summary>
        public bool HasWoundReadings;

        public float Rad, Cut, Poison;

        // ── The purse ─────────────────────────────────────────────────────────

        /// <summary>
        /// <c>inv.money</c> / <c>inv.gel</c> / <c>inv.good</c> — the item row the doctor is paid in,
        /// chosen by <c>pip.npcInter</c> (<c>PipPageMed.as:48-62</c>). All three are ordinary inventory
        /// rows in the Oracle, so all three are ordinary inventory ids here.
        /// </summary>
        public string CurrencyItemId = "money";

        /// <summary>The balance the footer prints and every click clamps against — <c>this.plata.kol</c>.</summary>
        public int CurrencyBalance;

        /// <summary>
        /// <c>pip.noAct</c> — the device is not accepting actions (a cutscene owns the interface).
        /// <c>itemClick</c> refuses on it (<c>PipPageMed.as:282-286</c>) and says so through the GUI's
        /// <c>noAct</c> message.
        /// </summary>
        public bool ActionsBlocked;
    }

    /// <summary>
    /// The click a row resolves to, in the numbers the systems underneath take — the body of
    /// <c>PipPageMed.itemClick</c> (<c>PipPageMed.as:278-450</c>), with the strings and the sound
    /// removed.
    ///
    /// <para><b>Why the cost is computed here and not carried on the row.</b> The Oracle does exactly
    /// that: the row's <c>price</c> is display data, and <c>itemClick</c> recomputes <c>_loc2_</c> from
    /// the live organ reading before charging. The recomputation is not redundant — the page can sit
    /// open while the reading changes underneath it (a heal effect still ticking, the doctor's own
    /// auto-repair), and the Oracle charges for the state at the moment of the click. Reusing the row's
    /// number would charge for a state the player is no longer in.</para>
    /// </summary>
    public readonly struct PipMedPlan
    {
        public PipMedPlan(PipMedTarget target, float cost, float heal, int organType, bool charity,
            bool spendsMoney)
        {
            Target = target;
            Cost = cost;
            Heal = heal;
            OrganType = organType;
            IsCharity = charity;
            SpendsMoney = spendsMoney;
        }

        /// <summary>Which branch of the Oracle's ladder fired.</summary>
        public PipMedTarget Target { get; }

        /// <summary>
        /// <c>_loc2_</c> <b>after</b> the balance clamp — what the player is actually charged, as a
        /// float. The Oracle subtracts <c>Math.round(this.Cost)</c>.
        /// </summary>
        public float Cost { get; }

        /// <summary>
        /// What is handed to <c>heal()</c> — <c>_loc2_ / price</c> for every paid branch, or the literal
        /// 49 for the charity branch.
        /// </summary>
        public float Heal { get; }

        /// <summary>
        /// <c>Pers.heal</c>'s organ argument: 0 the HP pool, 1 head, 2 torso, 3 legs, 4 the open
        /// wounds, 5 blood, 6 mana. <b>Not the same numbering as the character sheet's row ids</b> —
        /// see <see cref="PipMedTarget"/>.
        /// </summary>
        public int OrganType { get; }

        /// <summary>
        /// The free repair — <c>PipPageMed.as:343</c>: a nearly-destroyed organ, an empty purse and a
        /// player below level 6. See <see cref="PipMedList.PlanFor"/>.
        /// </summary>
        public bool IsCharity { get; }

        /// <summary>
        /// Whether the purse is the caps row — <c>this.plata.id == "money"</c> (<c>PipPageMed.as:442</c>).
        /// Only a caps-paying doctor's takings are credited to the doctor's own wallet.
        /// </summary>
        public bool SpendsMoney { get; }

        /// <summary>Whether the click does anything at all — the <c>organism</c> header's is a no-op.</summary>
        public bool Acts => Target != PipMedTarget.None;
    }

    /// <summary>Which of the Oracle's seven payment branches a row selects.</summary>
    public enum PipMedTarget
    {
        /// <summary>Nothing — the section header, or a row id nothing matches.</summary>
        None,

        /// <summary><c>hp</c> — the health pool, <c>heal(n, 0)</c> (<c>PipPageMed.as:289-298</c>).</summary>
        Hp,

        /// <summary>
        /// <c>statHead</c> / <c>statTors</c> / <c>statLegs</c> / <c>statBlood</c> — one organ,
        /// <c>pers.heal(n, 1|2|3|5)</c> (<c>:329-441</c>).
        /// </summary>
        Organ,

        /// <summary><c>statMana</c> — the mana organ, <c>pers.heal(n, 6)</c> (<c>:353-369</c>).</summary>
        Mana,

        /// <summary><c>rad</c> — <c>heal(n, 2)</c> (<c>:299-308</c>).</summary>
        Rad,

        /// <summary><c>cut</c> — <c>heal(n, 3)</c> (<c>:309-318</c>).</summary>
        Cut,

        /// <summary><c>poison</c> — <c>heal(n, 4)</c> (<c>:319-328</c>).</summary>
        Poison,
    }

    /// <summary>
    /// The doctor's page as data: which rows it lists, what each one says, what a click on it costs and
    /// does. The port of <c>PipPageMed.setSubPages</c>'s <c>page2 == 1</c> branch
    /// (<c>PipPageMed.as:76-193</c>) and of <c>itemClick</c> (<c>:278-450</c>), with no engine types so
    /// the arithmetic can be asserted offline.
    ///
    /// <para><b>There is only one sub-page, and <c>page2 == 2</c> is not one.</b> A doctor's second tab
    /// is the merchant page: <c>PipPageMed.page2Click</c> sets <c>page2 = 2</c>, then immediately
    /// <c>page2 = 1</c> and <c>pip.onoff(4)</c> (<c>:247-252</c>). So <c>setSubPages</c>'s
    /// <c>if (page2 == 1)</c> guard never sees anything else, and a page that listed rows for sub-page 2
    /// would be inventing a screen the Oracle does not have. The redirect itself is
    /// <see cref="PipSubTabs.NavigatesToPage"/>'s, where the vendor's mirror-image case lives too.</para>
    ///
    /// <para><b>The row set is fixed, so there is no scroll.</b> Eleven rows at most against
    /// <see cref="PipBuckLayout.PageRowCount"/> 18, so the Oracle's scrollbar never appears
    /// (<c>PipPage.as:942-953</c>). The page still builds one, because the rule that hides it is the
    /// same rule every other list page uses and a second copy of "when does the bar show" is exactly
    /// what <see cref="PipRowChrome"/> exists to prevent.</para>
    /// </summary>
    public static class PipMedList
    {
        // ── Row ids, which are also the dispatch keys ─────────────────────────
        //
        // PipPageMed.as:86-192. Written out because `itemClick` matches on them (a prefix match for
        // the organs, an exact match for the rest) and because the trailing digit of an organ id is
        // the trauma stage, so the id is built rather than typed.

        public const string HpRowId = "hp";
        public const string OrganismRowId = "organism";
        public const string RadRowId = "rad";
        public const string CutRowId = "cut";
        public const string PoisonRowId = "poison";

        /// <summary>The four organ prefixes, in the Oracle's own order (<c>:106-156</c>).</summary>
        public const string HeadPrefix = "statHead";
        public const string TorsoPrefix = "statTors";
        public const string LegsPrefix = "statLegs";
        public const string BloodPrefix = "statBlood";
        public const string ManaPrefix = "statMana";

        /// <summary>
        /// The organ id prefix whose healing goes to <paramref name="organType"/> — the four the
        /// Oracle dispatches on. Returns null for anything else.
        /// </summary>
        public static string PrefixForOrganType(int organType)
        {
            switch (organType)
            {
                case 1: return HeadPrefix;
                case 2: return TorsoPrefix;
                case 3: return LegsPrefix;
                case 5: return BloodPrefix;
                case 6: return ManaPrefix;
                default: return null;
            }
        }

        /// <summary>
        /// The label column's key in the Oracle's document — <c>Res.pipText("hp")</c> and friends
        /// (<c>PipPageMed.as:87, 94, 107, 122, 137, 152, 167, 175, 182, 189</c>).
        /// </summary>
        public static string LabelKeyForOrganType(int organType)
        {
            switch (organType)
            {
                case 1: return "head";
                case 2: return "tors";
                case 3: return "legs";
                case 5: return "blood";
                case 6: return "mana";
                default: return null;
            }
        }

        /// <summary>
        /// The <c>pip</c>-table key the header row prints in its price column —
        /// <c>statHead.price.text = Res.pipText("medprice")</c> (<c>PipPageMed.as:74</c>).
        /// </summary>
        public const string PriceHeaderKey = "medprice";

        /// <summary>
        /// The page's title strip — <c>setTopText("usemed2")</c> (<c>PipPageMed.as:79</c>), read through
        /// <c>Res.txt("p", key, 0, true)</c> (<c>PipPage.as:1507</c>). The <c>2</c> in the key is the
        /// sub-page number, exactly as the sub-tab captions are <c>med1</c>/<c>med2</c>.
        /// </summary>
        public const string TopTextKey = "usemed2";

        /// <summary>
        /// The most a player can be charged for one click on an organ, before the balance clamp — the
        /// <c>raz</c>/<c>razMana</c> ceiling (<c>PipPageMed.as:97-171</c>).
        ///
        /// <para><b>Why it is a cap on HP and not on caps.</b> The Oracle compares the organ's <i>missing
        /// HP</i> against <c>raz</c> and then multiplies the smaller of the two by the price. So a
        /// doctor's single click repairs at most 200 organ HP; a nearly-dead limb needs several clicks.
        /// The cap is the same at every difficulty — only the price per HP moves.</para>
        /// </summary>
        public static float OrganCost(float organHp, float inMaxHp, float pricePerHp, int raz)
        {
            float missing = inMaxHp - organHp;
            return (missing > raz ? raz : missing) * pricePerHp;
        }

        // ── The rows ──────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the page's rows — the port of <c>PipPageMed.setSubPages</c>'s <c>page2 == 1</c> branch
        /// (<c>PipPageMed.as:76-193</c>), in the Oracle's own push order.
        ///
        /// <para><b>The order is load-bearing, not cosmetic.</b> The <c>organism</c> header is pushed
        /// between the health row and the four organs (<c>:92-96</c>), and the mana organ sits after
        /// blood (<c>:157-171</c>) even though it belongs to a different pool — the Oracle groups the
        /// four physical organs under one caption and then appends mana, radiation, wounds and poison.
        /// A sorted or regrouped list would read better and would not be this page.</para>
        /// </summary>
        public static List<PipMedRow> RowsFor(PipMedSnapshot s, PipTextTable text)
        {
            var rows = new List<PipMedRow>(11);
            if (s == null) return rows;

            PipMedPrices p = s.Prices;

            // :80 — `gg.pers.checkHP()`, which clamps each organ down to its ceiling before anything is
            // priced. A reading above the ceiling would otherwise produce a NEGATIVE cost, and the
            // Oracle's `if(_loc1_ < 0) _loc1_ = 0` guard exists only on the health row (:81-84) — the
            // organ rows have no such guard, so they rely on the clamp having already happened.
            float inMaxHp = s.InMaxHp;
            float inMaxMana = s.InMaxMana;
            float headHp = Math.Min(s.HeadHp, inMaxHp);
            float torsoHp = Math.Min(s.TorsoHp, inMaxHp);
            float legsHp = Math.Min(s.LegsHp, inMaxHp);
            float bloodHp = Math.Min(s.BloodHp, inMaxHp);
            float manaHp = Math.Min(s.ManaOrganHp, inMaxMana);

            // :85-91 — the health pool. `(maxhp - hp - rad)` is the Oracle's expression: radiation
            // occupies part of the pool and cannot be healed by buying HP, so it is subtracted from what
            // a doctor can top up. The port has no `rad`, so that term is zero.
            float hpCost = (s.MaxHp - s.Hp - (s.HasExposureReadings ? s.Rad : 0f)) * p.Hp;
            if (hpCost < 0f) hpCost = 0f;

            rows.Add(new PipMedRow(
                HpRowId,
                Localise("p", "hp", text),
                PipBuckReadoutFormat.As3Round(s.Hp) + "/" + PipBuckReadoutFormat.As3Round(s.MaxHp),
                hpCost,
                Bar(s.Hp, s.MaxHp)));

            // :92-96 — the section caption. No value, no price, no bar.
            rows.Add(new PipMedRow(
                OrganismRowId,
                Localise("p", "organism", text) + ":",
                string.Empty,
                0f,
                PipMedRow.NoBar,
                hasPrice: false));

            // :97-156 — the four physical organs, each `"   " + label` so it reads as a child of the
            // caption above it. Note the price coefficient differs per organ: head/torso/legs use
            // `priceOrgan`, blood uses `priceBlood`.
            rows.Add(OrganRow(1, HeadPrefix, headHp, s.HeadSt, inMaxHp, p.Organ, p.Raz, text));
            rows.Add(OrganRow(2, TorsoPrefix, torsoHp, s.TorsoSt, inMaxHp, p.Organ, p.Raz, text));
            rows.Add(OrganRow(3, LegsPrefix, legsHp, s.LegsSt, inMaxHp, p.Organ, p.Raz, text));
            rows.Add(OrganRow(5, BloodPrefix, bloodHp, s.BloodSt, inMaxHp, p.Blood, p.Raz, text));

            // :157-171 — the mana organ. Its denominator is `inMaxMana`, not `inMaxHP`, and its cap is
            // `razMana` (100) rather than `raz` (200).
            rows.Add(OrganRow(6, ManaPrefix, manaHp, s.ManaSt, inMaxMana, p.Mana, p.RazMana, text));

            // :172-192 — the three exposure rows. Each has a value and a price and NO bar (the Oracle
            // pushes no `bar` key for them), and wounds and poison print one decimal while radiation
            // prints an integer — the Oracle's own inconsistency at :176 vs :183 and :190.
            //
            // Each is gated on its own flag: the radiation row exists now, the wound and poison rows
            // do not. An absent row is a claim about the port, a row of zeroes is a false claim about
            // the player — and a clinic would charge for a treatment it cannot perform.
            if (s.HasExposureReadings)
            {
                rows.Add(new PipMedRow(
                    RadRowId,
                    Localise("p", "rad", text),
                    PipBuckReadoutFormat.As3Round(s.Rad).ToString(),
                    s.Rad * p.Rad));
            }

            if (s.HasWoundReadings)
            {
                rows.Add(new PipMedRow(
                    CutRowId,
                    Localise("p", "cut", text),
                    PipBuckReadoutFormat.Numb(s.Cut),
                    s.Cut * p.Cut));

                rows.Add(new PipMedRow(
                    PoisonRowId,
                    Localise("p", "poison", text),
                    PipBuckReadoutFormat.Numb(s.Poison),
                    s.Poison * p.Poison));
            }

            return rows;
        }

        /// <summary>
        /// One organ row — the shared body of the five pushes at <c>PipPageMed.as:97-171</c>, which
        /// differ only in their label key, their price coefficient and their ceiling.
        /// </summary>
        private static PipMedRow OrganRow(int organType, string prefix, float organHp, int stage,
            float ceiling, float pricePerHp, int raz, PipTextTable text)
        {
            // :107 — `"   " + Res.pipText("head")`. The indent is the Oracle's, and it is what makes the
            // four organs read as members of the "Body condition" group above them.
            string label = "   " + Localise("p", LabelKeyForOrganType(organType), text);

            return new PipMedRow(
                prefix + stage,
                label,
                PipBuckReadoutFormat.As3Round(organHp) + "/" + PipBuckReadoutFormat.As3Round(ceiling),
                OrganCost(organHp, ceiling, pricePerHp, raz),
                Bar(organHp, ceiling));
        }

        /// <summary>
        /// The header row's price-column caption — <c>statHead.price.text = Res.pipText("medprice")</c>
        /// (<c>PipPageMed.as:74</c>). The header row's own <c>nazv</c> and <c>numb</c> are cleared
        /// (<c>:72-73</c>) and its <c>hpbar</c> hidden (<c>:71</c>), so this is the only text on it.
        /// </summary>
        public static string HeaderPrice(PipTextTable text) => Localise("p", PriceHeaderKey, text);

        // ── The click ─────────────────────────────────────────────────────────

        /// <summary>
        /// What a click on <paramref name="rowId"/> does — the port of <c>PipPageMed.itemClick</c>
        /// (<c>PipPageMed.as:278-450</c>), reduced to the numbers the systems underneath take.
        ///
        /// <para><b>The clamp is not an affordability check.</b> <c>if(_loc2_ &gt; this.plata.kol)
        /// _loc2_ = this.plata.kol;</c> (<c>:292-295</c> and every sibling) means the doctor charges
        /// <i>whatever the player has</i> and heals for that much — a player with 3 caps and a broken
        /// arm gets 6 organ HP, not a refusal. The Oracle never rejects a treatment for want of money,
        /// and a port that added a check would be a different game.</para>
        ///
        /// <para><b>The charity branch is a real rule and it is deliberately narrow.</b> At
        /// <c>:343</c>, <c>:384</c>, <c>:408</c> and <c>:432</c>: if the organ is at or below 2 HP, the
        /// purse is <i>already</i> empty, and the player is below level 6, the doctor repairs 49 HP for
        /// nothing. Three conditions, all required, and only on the four organs — the health pool, the
        /// mana organ, radiation, wounds and poison have no such branch. It exists so a broke beginner
        /// cannot be permanently crippled by a limb they can neither use nor afford.</para>
        /// </summary>
        public static PipMedPlan PlanFor(PipMedSnapshot s, string rowId)
        {
            if (s == null || string.IsNullOrEmpty(rowId)) return Nothing(s);
            if (s.ActionsBlocked) return Nothing(s);

            PipMedPrices p = s.Prices;
            float balance = s.CurrencyBalance;
            bool spendsMoney = s.CurrencyItemId == "money";

            if (rowId == HpRowId)
            {
                // :289-298. No charity branch here — and none is needed: a full pool prices at 0.
                float cost = (s.MaxHp - s.Hp - (s.HasExposureReadings ? s.Rad : 0f)) * p.Hp;
                cost = Clamp(cost, balance);
                return new PipMedPlan(PipMedTarget.Hp, cost, Divide(cost, p.Hp), 0, false, spendsMoney);
            }

            if (rowId == RadRowId)
            {
                // :299-308 — `gg.rad * priceRad`, healed through the unit's type 2.
                float cost = Clamp(s.Rad * p.Rad, balance);
                return new PipMedPlan(PipMedTarget.Rad, cost, Divide(cost, p.Rad), 2, false, spendsMoney);
            }

            if (rowId == CutRowId)
            {
                // :309-318 — `gg.cut * priceCut`, unit type 3.
                float cost = Clamp(s.Cut * p.Cut, balance);
                return new PipMedPlan(PipMedTarget.Cut, cost, Divide(cost, p.Cut), 3, false, spendsMoney);
            }

            if (rowId == PoisonRowId)
            {
                // :319-328 — `gg.poison * pricePoison`, unit type 4.
                float cost = Clamp(s.Poison * p.Poison, balance);
                return new PipMedPlan(PipMedTarget.Poison, cost, Divide(cost, p.Poison), 4, false,
                    spendsMoney);
            }

            // The organs. Prefix-matched in the Oracle's own order, and the order matters only because
            // `statBlood` is nine characters where the rest are eight — `substr(0,9)` vs `substr(0,8)`
            // (:329 vs :353). A single `StartsWith` is the same test without the arithmetic.
            if (rowId.StartsWith(BloodPrefix, StringComparison.Ordinal))
            {
                return OrganPlan(s, p.Blood, s.BloodHp, s.InMaxHp, p.Raz, 5, spendsMoney);
            }

            if (rowId.StartsWith(ManaPrefix, StringComparison.Ordinal))
            {
                // :353-369 — the only organ with no charity branch and its own ceiling.
                float cost = Clamp(OrganCost(s.ManaOrganHp, s.InMaxMana, p.Mana, p.RazMana), balance);
                return new PipMedPlan(PipMedTarget.Mana, cost, Divide(cost, p.Mana), 6, false,
                    spendsMoney);
            }

            if (rowId.StartsWith(HeadPrefix, StringComparison.Ordinal))
            {
                return OrganPlan(s, p.Organ, s.HeadHp, s.InMaxHp, p.Raz, 1, spendsMoney);
            }

            if (rowId.StartsWith(TorsoPrefix, StringComparison.Ordinal))
            {
                return OrganPlan(s, p.Organ, s.TorsoHp, s.InMaxHp, p.Raz, 2, spendsMoney);
            }

            if (rowId.StartsWith(LegsPrefix, StringComparison.Ordinal))
            {
                return OrganPlan(s, p.Organ, s.LegsHp, s.InMaxHp, p.Raz, 3, spendsMoney);
            }

            // `organism`, and anything else. The Oracle's ladder has no branch for either, so the click
            // falls through to the sound and the repaint (:446-449) and heals nothing.
            return Nothing(s);
        }

        /// <summary>
        /// The shared body of the four organ branches — <c>PipPageMed.as:329-352, 370-441</c>.
        /// </summary>
        private static PipMedPlan OrganPlan(PipMedSnapshot s, float pricePerHp, float organHp,
            float ceiling, int raz, int organType, bool spendsMoney)
        {
            float cost = Clamp(OrganCost(organHp, ceiling, pricePerHp, raz), s.CurrencyBalance);

            // The charity rule, all three terms. `plata.kol <= 0` is tested BEFORE the subtraction, and
            // it is the pre-payment balance — which is the same number the clamp above used, so a
            // charity click necessarily has cost 0.
            bool charity = organHp <= 2f && s.CurrencyBalance <= 0 && s.Level < 6;

            return new PipMedPlan(
                PipMedTarget.Organ,
                cost,
                charity ? 49f : Divide(cost, pricePerHp),
                organType,
                charity,
                spendsMoney);
        }

        private static PipMedPlan Nothing(PipMedSnapshot s) =>
            new PipMedPlan(PipMedTarget.None, 0f, 0f, 0, false,
                s != null && s.CurrencyItemId == "money");

        // ── The footer ────────────────────────────────────────────────────────

        /// <summary>
        /// The footer's balance line — <c>showBottext</c> (<c>PipPageMed.as:262-276</c>): a caps doctor
        /// says <c>Res.pipText("caps")</c>, a gel doctor and a goods doctor name their item instead
        /// (<c>Res.txt("i","gel")</c> / <c>("i","good")</c>).
        ///
        /// <para><b>Why the currency's <i>name</i> comes from two different tables.</b> Caps have their
        /// own <c>pip</c> entry — the word the HUD and the character sheet both use — while gel and
        /// goods are ordinary items, so the Oracle reaches for the item table. Reading all three from
        /// <c>i</c> would print the inventory's name for caps ("Bag with caps"), which is not what the
        /// footer says.</para>
        /// </summary>
        public static string BottomText(PipMedSnapshot s, PipTextTable text)
        {
            if (s == null) return string.Empty;

            string label;
            if (s.CurrencyItemId == "gel" || s.CurrencyItemId == "good")
            {
                label = Localise("i", s.CurrencyItemId, text);
            }
            else
            {
                label = Localise("p", "caps", text);
            }

            return label + ": " + Yellow(s.CurrencyBalance.ToString());
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>The Oracle's <c>yel()</c> — the balance's colour (<c>PipPageMed.as:266</c>).</summary>
        private static string Yellow(string value) =>
            PipBuckReadoutFormat.Wrap(value, PipBuckReadoutFormat.ClassToHex("yel"));

        /// <summary>A 0..1 bar fraction, clamped — the Oracle's <c>param2.bar</c> feeds a <c>scaleX</c>.</summary>
        private static float Bar(float value, float max)
        {
            if (max <= 0f) return 0f;
            float f = value / max;
            if (f < 0f) return 0f;
            if (f > 1f) return 1f;
            return f;
        }

        /// <summary>
        /// The Oracle's <c>if(_loc2_ &gt; this.plata.kol) _loc2_ = this.plata.kol;</c>, plus the
        /// negative floor the health row applies on its own (<c>PipPageMed.as:81-84</c>) and the organ
        /// rows get from <c>checkHP()</c>. Applied uniformly here so no branch can forget it.
        /// </summary>
        private static float Clamp(float cost, float balance)
        {
            if (cost < 0f) return 0f;
            return cost > balance ? balance : cost;
        }

        /// <summary>
        /// <c>_loc2_ / price</c> with the division guarded. The Oracle divides without a check and would
        /// produce <c>Infinity</c> on a zero price; no shipped difficulty has one, but a price table
        /// built by hand could, and an infinity handed to <c>heal()</c> would max every organ at once.
        /// </summary>
        private static float Divide(float cost, float price)
        {
            if (price <= 0f) return 0f;
            return cost / price;
        }

        /// <summary>
        /// A caption, or the Oracle's own missing-key marker (<c>Res.as:120-130</c>) — the same
        /// arrangement <c>PipStatList</c> uses, so a document that failed to load says so in the place
        /// the word should be rather than leaving a blank.
        /// </summary>
        private static string Localise(string alias, string id, PipTextTable text)
        {
            if (text != null && text.TryGet(alias, id, 0, out string value)) return value;
            return "*" + alias + "_" + id;
        }
    }
}
