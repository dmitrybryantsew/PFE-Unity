using System;
using System.Collections.Generic;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// One row of the character sheet — the Oracle's <c>arr</c> entry
    /// (<c>PipPageStat.as:68-449</c>).
    ///
    /// <para><b>Three fields, because the Oracle's row clip has three.</b> <c>setStatItem</c>
    /// (<c>PipPageStat.as:474-560</c>) writes <c>nazv</c> (the label), <c>numb</c> (the value) and
    /// optionally <c>hpbar</c> (a proportional bar), and dims the whole row to <c>alpha = 0.4</c> on the
    /// perk-pick page. Everything else in the clip (<c>id</c>, <c>cat</c>, <c>progress</c>) is either
    /// hidden or carries data for the info pane, which the port does not have yet.</para>
    /// </summary>
    public readonly struct PipStatRow
    {
        /// <summary>No bar on this row. The Oracle's <c>param2.bar == null</c>.</summary>
        public const float NoBar = -1f;

        public PipStatRow(string id, string name, string value, float alpha = 1f, float bar = NoBar)
        {
            Id = id ?? string.Empty;
            Name = name ?? string.Empty;
            Value = value ?? string.Empty;
            Alpha = alpha;
            Bar = bar;
        }

        /// <summary>
        /// The Oracle's <c>param2.id</c> — the param-table id, or the page's own id for a fixed row
        /// (<c>diff</c>, <c>reput</c>, <c>hp</c>, <c>statHead1</c>…). Empty for the first three rows of
        /// the Main sub-page, which the Oracle pushes with no id at all.
        ///
        /// <para>Carried rather than dropped because it is the info pane's key: <c>statInfo</c> reads
        /// <c>event.currentTarget.id.text</c> and looks the description up by it
        /// (<c>PipPageStat.as:567</c>). A row that loses its id cannot be explained later.</para>
        /// </summary>
        public string Id { get; }

        /// <summary>The label column — the Oracle's <c>nazv</c>.</summary>
        public string Name { get; }

        /// <summary>The value column — the Oracle's <c>numb</c>, already formatted.</summary>
        public string Value { get; }

        /// <summary>The Oracle's <c>param1.alpha</c>; 0.4 marks a perk the player cannot take yet.</summary>
        public float Alpha { get; }

        /// <summary>0..1 for the row's bar, or <see cref="NoBar"/> when the row has none.</summary>
        public float Bar { get; }

        /// <summary>Whether the Oracle would set <c>hpbar.visible = true</c> for this row.</summary>
        public bool HasBar => Bar > NoBar;
    }

    /// <summary>
    /// One entry of the Oracle's character-sheet parameter table — <c>AllData.d.param</c>, the 41
    /// <c>&lt;param&gt;</c> elements at <c>AllData.as:6370-6413</c>.
    ///
    /// <para><b>This is data, and it is transcribed rather than inferred.</b> Each field is an
    /// attribute of that table and each is read by <c>PipPageStat.setSubPages</c>:</para>
    /// <list type="bullet">
    /// <item><description><c>@id</c> → the label, through <c>Res.pipText(xml.@id)</c> (<c>:101</c>) and
    /// the info pane's key.</description></item>
    /// <item><description><c>@v</c> → <b>which variable to read</b>, or empty for a section header
    /// (<c>:102-121</c>). For <c>tip == 4</c> it is a <i>damage-type index</i>, not a name — the Oracle
    /// reads <c>gg.vulner[xml.@v]</c>.</description></item>
    /// <item><description><c>@tip</c> → which of the four formatting rules applies
    /// (<c>:122-162</c>).</description></item>
    /// <item><description><c>@show</c> → 0 hides the row outright, 2 hides it with no armour worn, 3
    /// hides it until the <c>story_canter</c> trigger fires (<c>:95-99</c>).</description></item>
    /// <item><description><c>@f</c> → only used by the info pane's <c>factor()</c> block
    /// (<c>:594</c>), never by the list. Carried so the info pane can land without re-transcribing the
    /// table.</description></item>
    /// </list>
    ///
    /// <para><b><c>@nobeg</c> is carried and never read.</b> It appears on <c>healeff</c>,
    /// <c>lockpick</c> and <c>hacker</c> and has <i>no consumer anywhere in <c>PipPageStat</c></i> —
    /// grepping the Oracle for it returns the three declarations and nothing else. It is recorded
    /// rather than dropped because an attribute with no reader is exactly the kind of thing that gets
    /// "restored" as a behaviour later (rule #12).</para>
    /// </summary>
    public readonly struct PipStatParam
    {
        public PipStatParam(string id, string variable, int tip, int show, bool hasFactor = false,
            bool noBeginnerPrefix = false)
        {
            Id = id;
            Variable = variable;
            Tip = tip;
            Show = show;
            HasFactor = hasFactor;
            NoBeginnerPrefix = noBeginnerPrefix;
        }

        /// <summary><c>@id</c> — the label key and the info-pane key.</summary>
        public string Id { get; }

        /// <summary><c>@v</c> — the variable (or damage-type index for <c>tip == 4</c>); empty = section header.</summary>
        public string Variable { get; }

        /// <summary><c>@tip</c> — 0 raw, 1 a delta in %, 2 a fraction in %, 3/4 a reduction in %.</summary>
        public int Tip { get; }

        /// <summary><c>@show</c> — 0 hidden, 1 always, 2 needs armour, 3 needs the <c>story_canter</c> trigger.</summary>
        public int Show { get; }

        /// <summary><c>@f</c> — the info pane's <c>factor()</c> flag. Not read by the list.</summary>
        public bool HasFactor { get; }

        /// <summary><c>@nobeg</c> — read by nothing. See the type summary.</summary>
        public bool NoBeginnerPrefix { get; }

        /// <summary>A section header (<c>@v=''</c>) — a name with no value.</summary>
        public bool IsSection => Variable.Length == 0;
    }

    /// <summary>One skill row's inputs — <c>PipPageStat.as:305-320</c>.</summary>
    public readonly struct PipStatSkill
    {
        public PipStatSkill(string id, int level, bool isPost, int postTier, int postTarget)
        {
            Id = id;
            Level = level;
            IsPost = isPost;
            PostTier = postTier;
            PostTarget = postTarget;
        }

        public string Id { get; }
        public int Level { get; }

        /// <summary><c>Pers.skillIsPost</c> — one of <c>attack</c>, <c>defense</c>, <c>knowl</c>.</summary>
        public bool IsPost { get; }

        /// <summary><c>Pers.getPostSkLevel(level)</c> — the post-skill tier, 0..10.</summary>
        public int PostTier { get; }

        /// <summary><c>postSkTab[tier]</c> — what the tier's ceiling would be, or 100 past the table.</summary>
        public int PostTarget { get; }
    }

    /// <summary>One perk row's inputs — <c>PipPageStat.as:328-361</c> and <c>:416-444</c>.</summary>
    public readonly struct PipStatPerk
    {
        public PipStatPerk(string id, int rank, int maxRank, int sort, bool selectable)
        {
            Id = id;
            Rank = rank;
            MaxRank = maxRank;
            Sort = sort;
            Selectable = selectable;
        }

        public string Id { get; }

        /// <summary>The rank the player holds (<c>this.pers.perks[pid]</c>), or 0 for none.</summary>
        public int Rank { get; }

        /// <summary><c>AllData.d.perk.(@id==pid).@lvl</c>, defaulting to 1 — the Oracle's own default.</summary>
        public int MaxRank { get; }

        /// <summary>
        /// The Oracle's sort key. On the Perks page it is <c>@tip == "0" ? 2 : 1</c>
        /// (<c>:342</c>); on the perk-pick page it is <c>1 - perkPoss</c> (<c>:439</c>) — which is how
        /// the perks the player <i>can</i> take float to the top. Lower sorts first.
        /// </summary>
        public int Sort { get; }

        /// <summary>Perk-pick only: <c>perkPoss &gt; 0</c>, i.e. the perk is takeable now (<c>:438</c>).</summary>
        public bool Selectable { get; }
    }

    /// <summary>One active-effect row's inputs — <c>PipPageStat.as:368-399</c>.</summary>
    public readonly struct PipStatEffect
    {
        public PipStatEffect(string id, bool forever, int seconds, int level, bool isAddiction,
            bool isFood, bool isDrunk)
        {
            Id = id;
            Forever = forever;
            Seconds = seconds;
            Level = level;
            IsAddiction = isAddiction;
            IsFood = isFood;
            IsDrunk = isDrunk;
        }

        public string Id { get; }

        /// <summary><c>ef.forever</c> — a permanent effect shows <c>∞</c> instead of a countdown.</summary>
        public bool Forever { get; }

        /// <summary><c>ef.t / 30</c>, rounded — the Oracle's own seconds.</summary>
        public int Seconds { get; }

        /// <summary><c>ef.lvl</c> — picks the <c>ad0</c>..<c>ad3</c> / <c>drunk1</c>..<c>drunk3</c> caption.</summary>
        public int Level { get; }

        /// <summary><c>ef.ad</c> — an addiction, which carries its own <c>&lt;id&gt;_ad</c> name.</summary>
        public bool IsAddiction { get; }

        /// <summary><c>ef.tip == 3</c> — food, which the Oracle renames to a single shared word.</summary>
        public bool IsFood { get; }

        /// <summary><c>ef.id == "drunk"</c> — the one effect whose caption is built from its level.</summary>
        public bool IsDrunk { get; }
    }

    /// <summary>One addiction row on the Health sub-page — <c>PipPageStat.as:269-297</c>.</summary>
    public readonly struct PipStatAddiction
    {
        public PipStatAddiction(string id, float value, int stage)
        {
            Id = id;
            Value = value;
            Stage = stage;
        }

        public string Id { get; }

        /// <summary><c>this.pers.addictions[i]</c> — a percentage.</summary>
        public float Value { get; }

        /// <summary>0..3 — which of <c>ad0</c>..<c>ad3</c> the Oracle prints beside the value.</summary>
        public int Stage { get; }
    }

    /// <summary>
    /// The body readings the Health sub-page needs — <c>PipPageStat.as:187-268</c>.
    ///
    /// <para><b>Deliberately not the whole of <c>gg</c>.</b> Only the quantities that page actually
    /// prints are here, so the page cannot accidentally grow a reading the Oracle never shows.</para>
    /// </summary>
    public sealed class PipStatVitals
    {
        public float Hp, MaxHp;

        /// <summary><c>Pers.headHP</c> and its three siblings — the four limb/blood organs.</summary>
        public float HeadHp, TorsoHp, LegsHp, BloodHp;

        /// <summary><c>Pers.manaHP</c> — the mana organ.</summary>
        public float ManaOrganHp;

        /// <summary><c>Pers.inMaxHP</c> — the denominator for the four organs.</summary>
        public float InMaxHp;

        /// <summary><c>Pers.inMaxMana</c> — the denominator for the mana organ.</summary>
        public float InMaxMana;

        /// <summary>
        /// The trauma stage of each organ, 0..3 — it is <b>part of the row's id</b>
        /// (<c>"statHead" + gg.pers.headSt</c>, <c>:202</c>), not just decoration, because the info
        /// pane parses the digit back out to pick the trauma perk's description (<c>:625-680</c>).
        /// </summary>
        public int HeadSt, TorsoSt, LegsSt, BloodSt, ManaSt;

        /// <summary>
        /// Radiation, its resistance, open wounds and poison — four of the Oracle's six exposure rows
        /// (<c>PipPageStat.as:231-258</c>). <b>False in this port</b>: it keeps no radiation, wound or
        /// poison value at all. The Oracle pushes these four unconditionally, so with the flag false the
        /// page omits them rather than printing zeroes — see <see cref="PipStatSnapshot"/> for why an
        /// absent row is the honest answer.
        /// </summary>
        public bool HasExposureReadings;
        public float Rad, RadX, Cut, Poison;

        /// <summary>
        /// The bleeding and poison <i>resistances</i> — the other two of the Oracle's six exposure rows.
        ///
        /// <para><b>Gated separately from <see cref="HasExposureReadings"/>, and the split is the
        /// point.</b> The port <i>can</i> supply these two: they are entries 13 and 12 of the bound
        /// unit's vulnerability table (<c>UnitStats.Vulnerabilities.GetVulnerability(DamageType)</c>) —
        /// the very table the Main sub-page's <c>tip == 4</c> rows read. Folding them in with the four
        /// unavailable readings would omit two rows the port can fill, which is a claim about the port
        /// and a false one.</para>
        /// </summary>
        public bool HasResistanceReadings;
        public float ResBleeding, ResPoison;

        /// <summary>The <c>phoenix</c> pet's health row, shown only when the pet and its trigger exist.</summary>
        public bool HasPhoenix;
        public string PhoenixName = string.Empty;
        public float PhoenixHp, PhoenixMaxHp;

        /// <summary>The player's active addictions. Empty when there are none.</summary>
        public IReadOnlyList<PipStatAddiction> Addictions = Array.Empty<PipStatAddiction>();

        /// <summary>
        /// True when the <c>nomed</c> trigger is set — the medical module is damaged, so the Oracle
        /// replaces the whole page with one warning (<c>PipPageStat.as:179-184</c>).
        /// </summary>
        public bool MedicineDisabled;
    }

    /// <summary>
    /// Everything the character sheet reads, as plain data.
    ///
    /// <para><b>Why a snapshot and not the live objects.</b> <see cref="PipStatList"/> has to run
    /// offline, against the shipped assembly, with no player and no editor — that is the only way its
    /// rules are testable in this project (the editor is owner-only). A snapshot makes every builder a
    /// pure function of its input.</para>
    ///
    /// <para><b><see cref="Variable"/> is how the param table reaches the model.</b> The Oracle's loop
    /// reads <c>gg[xml.@v]</c>, or <c>gg.pers[xml.@v]</c> when the global has no such property, or
    /// <c>gg.vulner[xml.@v]</c> for <c>tip == 4</c> (<c>PipPageStat.as:105-121</c>). A <b>null result
    /// means the variable does not exist</b>, and the Oracle then <c>continue</c>s — it prints a trace
    /// and pushes <i>no row</i> (<c>:117-118</c>). The port keeps that meaning: a reading it cannot
    /// take produces no row, rather than a row reading zero. A zero is a claim about the player; an
    /// absent row is a claim about the port, and only one of them is true.</para>
    /// </summary>
    public sealed class PipStatSnapshot
    {
        // ── The five fixed rows of the Main sub-page (PipPageStat.as:68-89) ────

        /// <summary><c>gg.pers.persName</c>. Empty when the port has no name for the player.</summary>
        public string PlayerName = string.Empty;

        public int Level;

        /// <summary><c>gg.pers.xpCur</c> and <c>gg.pers.xpNext</c> — printed as <c>cur (next-cur)</c>.</summary>
        public int XpCur, XpNext;

        /// <summary><c>World.w.game.globalDif</c> — the index into the <c>gui</c> table's <c>difN</c> keys.</summary>
        public int GlobalDifficulty;

        /// <summary><c>gg.pers.rep</c> — the reputation number.</summary>
        public int Reputation;

        /// <summary>
        /// <c>gg.pers.repTex()</c> — the tier word. <b>Optional</b>: left empty, the model derives the
        /// word from <see cref="Reputation"/> using the Oracle's own thresholds
        /// (<see cref="PipStatList.ReputationTier"/>), which is the exact answer rather than a
        /// substitute, because <c>repTex()</c> is a pure function of the number. A source that owns a
        /// reputation system may set it instead.
        /// </summary>
        public string ReputationText = string.Empty;

        /// <summary><c>World.w.game.gameTime()</c> — already formatted, e.g. <c>1:26:04</c>.</summary>
        public string GameTime = string.Empty;

        // ── The param table's gates (PipPageStat.as:97-99) ───────────────────

        /// <summary>
        /// <c>gg.armor != 0 || gg.marmor != 0</c> — the <c>show='2'</c> gate. False when the port
        /// cannot read the worn armour's own defence value; see the class summary of
        /// <see cref="PipStatList"/> for which rows that costs.
        /// </summary>
        public bool ArmourPresent;

        /// <summary><c>World.w.game.triggers["story_canter"] &gt; 0</c> — the <c>show='3'</c> gate.</summary>
        public bool StoryCanter;

        /// <summary>
        /// Reads a param-table variable. <paramref name="tip"/> is the row's <c>@tip</c>, because
        /// <c>tip == 4</c> reads the vulnerability table rather than the stat blocks. Returns null when
        /// the variable does not exist — the Oracle's own <c>continue</c>.
        /// </summary>
        public Func<string, int, float?> Variable;

        // ── Sub-page 2: skills ────────────────────────────────────────────────

        public IReadOnlyList<PipStatSkill> Skills = Array.Empty<PipStatSkill>();

        /// <summary><c>Pers.skillPoint</c> — unspent points, shown in the bottom strip.</summary>
        public int SkillPoints;

        // ── Sub-page 3: perks ─────────────────────────────────────────────────

        public IReadOnlyList<PipStatPerk> Perks = Array.Empty<PipStatPerk>();

        /// <summary><c>Pers.perkPoint</c> — unspent perks, shown in the bottom strip.</summary>
        public int PerkPoints;

        // ── Sub-page 4: effects ───────────────────────────────────────────────

        public IReadOnlyList<PipStatEffect> Effects = Array.Empty<PipStatEffect>();

        // ── Sub-page 5: health ────────────────────────────────────────────────

        public PipStatVitals Vitals = new PipStatVitals();

        /// <summary>
        /// Medicine the player holds, by item id — what the Health sub-page's bottom strip counts
        /// (<c>PipPageStat.as:797-859</c>). A missing key reads as zero, which is the Oracle's own
        /// behaviour for an item the inventory has no row for.
        /// </summary>
        public IReadOnlyDictionary<string, int> MedicineCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        // ── Sub-page 6: the perk picker ───────────────────────────────────────

        /// <summary>The perks the player could take, already filtered and sorted. See <see cref="PipStatPerk.Sort"/>.</summary>
        public IReadOnlyList<PipStatPerk> PerkChoices = Array.Empty<PipStatPerk>();

        /// <summary><c>this.selectedPerk</c> — empty until the player picks one.</summary>
        public string SelectedPerk = string.Empty;

        /// <summary>
        /// <c>this.infoItemId</c> — <b>the id of the row the pointer is over</b>, used by the Health
        /// sub-page's bottom strip to name the medicine that row would use (<c>PipPageStat.as:797</c>).
        /// Set by the page on hover, and left empty when nothing is hovered, which is why the strip is
        /// blank until the player points at something.
        /// </summary>
        public string HoveredRowId = string.Empty;
    }

    /// <summary>
    /// The character sheet's row model — the port of <c>fe.inter.PipPageStat</c>'s <c>setSubPages</c>
    /// (<c>PipPageStat.as:40-451</c>) and the per-row half of <c>setStatItem</c> (<c>:474-560</c>).
    ///
    /// <para><b>Unity-free on purpose</b>, like <see cref="PipInventoryList"/> and
    /// <see cref="PipSubTabs"/>: every method here is a function of its arguments, so the whole rule set
    /// runs against the shipped assembly from a plain shell. That matters more for this page than for
    /// the inventory — the param table is 41 rows of formatting rules and gates, and none of it is
    /// checkable by a compiler.</para>
    ///
    /// <para><b>What the port cannot read, and what it therefore does.</b> The Oracle's param table
    /// names 41 variables; the port's <c>CharacterStats</c> exposes most of them by the same name.
    /// Three groups are genuinely absent, and each produces <i>no row</i> (the Oracle's own
    /// missing-variable path) rather than a zero:</para>
    /// <list type="bullet">
    /// <item><description><b><c>armor</c> / <c>marmor</c> / <c>aqual</c></b> — the worn armour's own
    /// defence value. The port tracks the armour instance's <i>condition</i> but not its damage
    /// resistance, so the Defence block's armour lines cannot be filled. All three are <c>tip 0/2</c>,
    /// i.e. hidden at zero anyway.</description></item>
    /// <item><description><b><c>sneak</c></b> (<c>visiMult</c>) — no such member; the port's closest is
    /// <c>stealthMult</c>, which is a different quantity and is not substituted.</description></item>
    /// <item><description><b>Four of the six exposure readings</b> (<c>rad</c>, <c>radX</c>, <c>cut</c>,
    /// <c>poison</c>) on the Health sub-page — the port keeps no radiation, wound or poison value at
    /// all. See <see cref="PipStatVitals.HasExposureReadings"/>. <b>The two resistances are not in this
    /// group</b> — the port can read them, see below.</description></item>
    /// </list>
    ///
    /// <para><b>The fourteen resistances ARE readable, and saying so matters.</b> The Oracle reads them
    /// out of <c>gg.vulner</c> — <c>PipPageStat.as:105-121</c> for the param rows and <c>:242-258</c>
    /// for the two on the Health sub-page. The port keeps that same table: the bound unit's
    /// <c>Vulnerabilities</c> (<c>UnitStats</c>), read through
    /// <c>VulnerabilityData.GetVulnerability(DamageType)</c> and indexed by the very <c>@v</c> digit the
    /// param table carries (<c>bullet</c> → 0, <c>blade</c> → 1, …), because the port's
    /// <c>DamageType</c> is the Oracle's own <c>Unit.D_*</c> numbering. So the <c>tip == 4</c> rows and
    /// <c>resbleeding</c>/<c>respoison</c> are <i>filled</i>, not omitted — they are merely invisible on
    /// a default character, because a <c>tip == 4</c> row is hidden while its multiplier is exactly 1.
    /// That invisibility is why their absence was easy to assume.</para>
    /// </summary>
    public static class PipStatList
    {
        // ── Sub-page ids — the Oracle's `page2` (PipPageStat.as:65-449) ───────

        public const int MainSubPage = 1;
        public const int SkillsSubPage = 2;
        public const int PerksSubPage = 3;
        public const int EffectsSubPage = 4;
        public const int HealthSubPage = 5;

        /// <summary>
        /// The level-up perk picker. <b>Not a sub-tab</b> — the Oracle reaches it from Perks through
        /// <c>transOk</c> (<c>PipPageStat.as:1007-1012</c>) and leaves it through <c>butDef</c>, and it
        /// has no button of its own.
        /// </summary>
        public const int PerkPickSubPage = 6;

        /// <summary>How many rows the page shows at once — <c>PipPage.maxrows = 18</c>.</summary>
        public const int VisibleRows = PipBuckLayout.PageRowCount;

        /// <summary><c>Pers.maxSkLvl</c> — the pip bar's length for a regular skill (<c>Pers.as:11</c>).</summary>
        public const int MaxSkillLevel = 20;

        /// <summary><c>Pers.postPersLevel</c> — below this level the three post-game skills are hidden (<c>Pers.as:17</c>).</summary>
        public const int PostSkillVisibleLevel = 20;

        // ── Reputation tiers — `Pers.rep1`…`rep4` / `repGood` (Pers.as:57-65) ──
        //
        // Private fields in the Oracle, transcribed here because they are part of this page's *output*
        // rather than of any one system: `repTex()` is a pure function of `rep` and these five numbers,
        // so the page can produce the Oracle's own tier word from the number alone.

        /// <summary><c>Pers.rep1</c> — the first reputation tier's floor.</summary>
        public const int Reputation1 = 10;

        /// <summary><c>Pers.rep2</c>.</summary>
        public const int Reputation2 = 20;

        /// <summary><c>Pers.rep3</c>.</summary>
        public const int Reputation3 = 35;

        /// <summary><c>Pers.rep4</c>.</summary>
        public const int Reputation4 = 50;

        /// <summary><c>Pers.repGood</c> — the ceiling tier.</summary>
        public const int ReputationGood = 70;

        // ── The parameter table ───────────────────────────────────────────────

        /// <summary>
        /// <c>AllData.d.param</c> — the 41 <c>&lt;param&gt;</c> elements at <c>AllData.as:6370-6413</c>,
        /// in document order.
        ///
        /// <para><b>Order is the display order</b>, and three entries are section headers
        /// (<c>@v=''</c>): <c>param_atk</c> "Attack parameters", <c>param_def</c> "Defense parameters",
        /// <c>param_oth</c> "Other parameters". They render as a label with no value, which is why the
        /// reference screen shows those three words standing alone.</para>
        /// </summary>
        public static readonly PipStatParam[] ParamTable =
        {
            new("param_atk",   "",                  0, 1),
            new("alldamage",   "allDamMult",        1, 1, hasFactor: true),
            new("meleedamage", "meleeDamMult",      1, 1, hasFactor: true),
            new("meleerapid",  "meleeSpdMult",      1, 1, hasFactor: true),
            new("gunsdamage",  "gunsDamMult",       1, 1, hasFactor: true),
            new("allprec",     "allPrecMult",       1, 1, hasFactor: true),
            new("reload",      "reloadMult",        1, 1, hasFactor: true),
            new("spelldamage", "spellsDamMult",     1, 1, hasFactor: true),
            new("punchdamage", "punchDamMult",      1, 1, hasFactor: true),

            new("param_def",   "",                  0, 1),
            new("skin",        "skin",              0, 1, hasFactor: true),
            new("armor",       "armor",             0, 1),
            new("marmor",      "marmor",            0, 1),
            new("aqual",       "armor_qual",        2, 2),
            new("dexter",      "dexter",            1, 1, hasFactor: true),
            new("allresist",   "allVulnerMult",     3, 1, hasFactor: true),
            new("bullet",      "0",                 4, 1, hasFactor: true),
            new("blade",       "1",                 4, 1, hasFactor: true),
            new("phis",        "2",                 4, 1, hasFactor: true),
            new("fang",        "14",                4, 1, hasFactor: true),
            new("expl",        "4",                 4, 1, hasFactor: true),
            new("fire",        "3",                 4, 1, hasFactor: true),
            new("crio",        "11",                4, 1, hasFactor: true),
            new("laser",       "5",                 4, 1, hasFactor: true),
            new("plasma",      "6",                 4, 1, hasFactor: true),
            new("spark",       "9",                 4, 1, hasFactor: true),
            new("venom",       "7",                 4, 1, hasFactor: true),
            new("acid",        "10",                4, 1, hasFactor: true),
            new("pink",        "19",                4, 3, hasFactor: true),
            new("necro",       "16",                4, 1, hasFactor: true),

            new("param_oth",   "",                  0, 1),
            new("sneak",       "visiMult",          3, 1, hasFactor: true),
            new("allspeed",    "allSpeedMult",      1, 1, hasFactor: true),
            new("runspeed",    "runSpeedMult",      1, 1, hasFactor: true),
            new("stamrun",     "stamRun",           2, 1, hasFactor: true),
            new("alldmana",    "allDManaMult",      2, 1, hasFactor: true),
            new("wmana",       "warlockDManaMult",  2, 1, hasFactor: true),
            new("healeff",     "healMult",          2, 1, hasFactor: true, noBeginnerPrefix: true),
            new("lockpick",    "lockPick",          0, 1, noBeginnerPrefix: true),
            new("hacker",      "hacker",            0, 1, noBeginnerPrefix: true),
            new("od",          "maxOd",             0, 1),
        };

        /// <summary>The prefix the Oracle puts on every param-table row that reads a variable (<c>PipPageStat.as:104</c>).</summary>
        private const string ValueRowPrefix = "-  ";

        // ── The row builders ──────────────────────────────────────────────────

        /// <summary>
        /// Every row of a sub-page — the port of <c>setSubPages</c>'s <c>arr</c>, per branch.
        /// <paramref name="subPage"/> is the Oracle's <c>page2</c>, 1..6.
        /// </summary>
        public static List<PipStatRow> RowsFor(PipStatSnapshot s, int subPage, PipTextTable text)
        {
            switch (subPage)
            {
                case MainSubPage: return MainRows(s, text);
                case SkillsSubPage: return SkillRows(s, text);
                case PerksSubPage: return PerkRows(s, text);
                case EffectsSubPage: return EffectRows(s, text);
                case HealthSubPage: return HealthRows(s, text);
                case PerkPickSubPage: return PerkChoiceRows(s, text);
                default:
                    // Not a sub-page of this page. An empty list is the honest answer; the shell draws
                    // its own notice for a page that is not implemented, and this page has no others.
                    return new List<PipStatRow>();
            }
        }

        // ── Sub-page 1: Main (PipPageStat.as:65-176) ──────────────────────────

        private static List<PipStatRow> MainRows(PipStatSnapshot s, PipTextTable text)
        {
            var rows = new List<PipStatRow>(ParamTable.Length + 5);

            // PipPageStat.as:68-89. The first three carry NO id — the Oracle pushes them with only
            // `nazv` and `lvl`, and `setStatItem` writes an empty id for them (:483-486).
            rows.Add(new PipStatRow("", Localise("p", "name", text),
                s.PlayerName));

            rows.Add(new PipStatRow("", Localise("p", "level", text),
                s.Level.ToString()));

            // `gg.pers.xpCur + " (" + (gg.pers.xpNext - gg.pers.xpCur) + ")"` — the second number is
            // the XP still needed, not the total for the next level.
            rows.Add(new PipStatRow("", Localise("p", "expa", text),
                s.XpCur + " (" + (s.XpNext - s.XpCur) + ")"));

            rows.Add(new PipStatRow("diff", Localise("p", "diff", text),
                Localise("g", "dif" + s.GlobalDifficulty, text)));

            rows.Add(new PipStatRow("reput", Localise("p", "reput", text),
                s.Reputation + " (" + ReputationTextFor(s, text) + ")"));

            AppendParamRows(rows, s, text);

            return rows;
        }

        /// <summary>
        /// The param-table half of the Main sub-page — <c>PipPageStat.as:90-175</c>.
        ///
        /// <para><b>Every branch is the Oracle's, including the ones that look like mistakes.</b> A
        /// <c>tip == 1</c> row is <i>hidden when its value is exactly 1</i> — which is why a default
        /// character's Attack block is nearly empty: every damage multiplier starts at 1.0. (The lone
        /// "Reload time" row on the reference screen is the Oracle's <i>other</i> tip-1 operand at work
        /// — a row also shows when something modified the variable, even though the modifiers cancelled
        /// to exactly 1. See <see cref="FormatParam"/>'s tip-1 note for the port's divergence there.) A
        /// <c>tip == 0</c> row is hidden unless the value is positive, so
        /// <c>skin</c> and the two armour lines are absent on a fresh character. Both are deliberate:
        /// the page is a list of what is <i>different</i>, not a dump of every stat.</para>
        /// </summary>
        private static void AppendParamRows(List<PipStatRow> rows, PipStatSnapshot s, PipTextTable text)
        {
            foreach (PipStatParam param in ParamTable)
            {
                // PipPageStat.as:95 — `if(xml.@show > 0)`. show is 1..3 in the table, so this is a
                // guard rather than a filter today; kept because show='0' is the documented "hide me".
                if (param.Show <= 0) continue;

                // :97 — a show='2' row needs armour to be worn.
                if (param.Show == 2 && !s.ArmourPresent) continue;

                // :99 — a show='3' row waits for the story trigger. (The Oracle's own expression is
                // `!(xml.@show == "3" && !World.w.game.triggers["story_canter"] > 0)`, whose precedence
                // makes the trigger comparison the operand of `!`; the intent — and the effect — is
                // "hide until story_canter is set", which is what is written here.)
                if (param.Show == 3 && !s.StoryCanter) continue;

                string name = Localise("p", param.Id, text);

                if (param.IsSection)
                {
                    // :164-171 — a section header: the name, an empty value, and NO "-  " prefix
                    // because the prefix is applied inside the `@v != ""` branch only.
                    rows.Add(new PipStatRow(param.Id, name, string.Empty));
                    continue;
                }

                float? value = s.Variable?.Invoke(param.Variable, param.Tip);
                if (value == null)
                {
                    // :117-118 — the Oracle traces and `continue`s. A row whose variable the port
                    // cannot read produces no row at all; see PipStatSnapshot's summary.
                    continue;
                }

                float v = value.Value;
                string formatted = FormatParam(v, param.Tip);
                if (formatted == null) continue;   // the tip's own "nothing to report" branch

                rows.Add(new PipStatRow(param.Id, ValueRowPrefix + name, formatted));
            }
        }

        /// <summary>
        /// The Oracle's four <c>@tip</c> formatting rules (<c>PipPageStat.as:122-162</c>), or
        /// <c>null</c> when the rule suppresses the row.
        /// </summary>
        private static string FormatParam(float value, int tip)
        {
            switch (tip)
            {
                // :122-132 — tip 0: the raw number, shown only when it is positive.
                case 0:
                    return value > 0f ? PipBuckReadoutFormat.Numb(value) : null;

                // :133-143 — tip 1: a multiplier, printed as the percentage it differs from 1 by.
                //
                // The Oracle's guard is `if(param != 1 || (factor[v] && factor[v].length > 1))`: it
                // shows the row when the value is not 1 **or when something modified the variable**,
                // even if the modifiers cancelled out to exactly 1. The port evaluates the value test
                // only, so a variable two modifiers cancel on is hidden here and shown by the Oracle.
                //
                // That is a recorded divergence, not a simplification, and the reason is worth stating
                // because the obvious reading of it is wrong: the port DOES keep a factor table
                // (CharacterStats.GetFactorsForStat, fed by TrackFactor). The counts are simply not
                // comparable — the Oracle seeds a baseline entry for every `@f` param (setBegFactor,
                // Pers.as:1575-1584) and pushes one more per non-trivial modifier, so `length > 1` means
                // "at least one modifier"; the port's list holds writes and no baseline row. Deciding
                // the translation needs a fixture against a real recalculation, so it is left open
                // rather than guessed.
                case 1:
                    if (value == 1f) return null;
                    return (value >= 1f ? "+" : "") + PipBuckReadoutFormat.Numb((value - 1f) * 100f) + "%";

                // :144-151 — tip 2: a fraction, printed as a percentage, always.
                case 2:
                    return PipBuckReadoutFormat.Numb(value * 100f) + "%";

                // :152-162 — tip 3 and 4: a damage multiplier, printed as the percentage it
                // <i>reduces</i> by — note the reversal, so 0.99 reads "+1%" (a 1 % reduction).
                case 3:
                case 4:
                    if (value == 1f) return null;
                    return (value < 1f ? "+" : "") + PipBuckReadoutFormat.Numb((1f - value) * 100f) + "%";

                default:
                    // An unknown tip. The Oracle has no default branch, so the row would be pushed
                    // with an undefined `lvl`. Nothing is emitted and the caller keeps going; the
                    // table above is the only source of tips and a new one would be a deliberate edit.
                    return null;
            }
        }

        // ── Sub-page 2: Skills (PipPageStat.as:299-322, 511-547) ──────────────

        private static List<PipStatRow> SkillRows(PipStatSnapshot s, PipTextTable text)
        {
            var rows = new List<PipStatRow>(s.Skills.Count);

            foreach (PipStatSkill skill in s.Skills)
            {
                // :307 — `if(!(this.pers.level < Pers.postPersLevel && sk.post > 0))`. The three
                // post-game skills do not exist below level 20.
                if (s.Level < PostSkillVisibleLevel && skill.IsPost) continue;

                string name = Localise("e", skill.Id, text);

                // :521 and :526 — the displayed value is NOT the raw level. A regular skill shows its
                // TIER (0..5, from getSkLevel) because the twenty pips beside it carry the level; a
                // post skill shows the level, the remainder to the tier's ceiling, and the tier.
                string value;
                if (skill.IsPost)
                {
                    // :521 — the Oracle writes `lvl + "  (+" + (target - lvl) + ")\t         " + level
                    // + ": " + tier`. The port substitutes spaces for the tab run, because a uGUI Text
                    // does not expand `\t` — a PORT CHOICE, and the only difference in the string.
                    value = skill.Level + "  (+" + (skill.PostTarget - skill.Level) + ")  " +
                            Localise("p", "level", text) + ": " + skill.PostTier;
                }
                else
                {
                    value = PipStatList.SkillTier(skill.Level).ToString();
                }

                // The pip bar's fill is the level over the cap, so a later renderer can draw the
                // twenty pips the Oracle draws without re-deriving the rule (PipPageStat.as:527-544).
                float bar = MaxSkillLevel > 0 ? (float)skill.Level / MaxSkillLevel : 0f;
                rows.Add(new PipStatRow(skill.Id, name, value, 1f, bar));
            }

            return rows;
        }

        /// <summary>
        /// <c>Pers.getSkLevel</c> (<c>Pers.as:995-1017</c>) — a regular skill's tier, 0..5. The port's
        /// <c>CharacterStats.ComputeSkillTier</c> is the same table; this copy exists so the page's
        /// rules stay Unity-free and runnable offline.
        /// </summary>
        public static int SkillTier(int level)
        {
            if (level >= 20) return 5;
            if (level >= 14) return 4;
            if (level >= 9) return 3;
            if (level >= 5) return 2;
            if (level >= 2) return 1;
            return 0;
        }

        // ── Sub-page 3: Perks (PipPageStat.as:323-362) ────────────────────────

        private static List<PipStatRow> PerkRows(PipStatSnapshot s, PipTextTable text)
        {
            // :360 — `arr.sortOn(["sort","nazv"])`. The sort happens on the *inputs*, before the rows
            // exist, because a PipStatRow carries no sort key — the row clip does not have one. Sorting
            // the rows by name alone would drop the primary key, which is how the perk the player can
            // actually take ends up buried.
            var sorted = new List<PipStatPerk>(s.Perks);
            sorted.Sort((a, b) => ComparePerks(a, b, text));

            var rows = new List<PipStatRow>(sorted.Count);
            foreach (PipStatPerk perk in sorted)
            {
                // :328-345 — `nazv: Res.txt("e", pid)`, `lvl: rank`, and a max from the perk table.
                rows.Add(new PipStatRow(perk.Id, Localise("e", perk.Id, text), perk.Rank.ToString()));
            }

            return rows;
        }

        // ── Sub-page 4: Effects (PipPageStat.as:363-410) ──────────────────────

        private static List<PipStatRow> EffectRows(PipStatSnapshot s, PipTextTable text)
        {
            var rows = new List<PipStatRow>(s.Effects.Count);

            foreach (PipStatEffect effect in s.Effects)
            {
                string name = Localise("e", effect.Id, text);
                string id = effect.Id;
                string value = "∞";   // :374 — the default, for a `forever` effect

                // :376-384 — an addiction is renamed from its `<id>_ad` entry and its stage appended.
                // The rename only happens when that entry actually exists (`str != ""`).
                if (effect.IsAddiction &&
                    TryLocalise("e", effect.Id + "_ad", text, out string addictionName) &&
                    addictionName.Length > 0)
                {
                    name = addictionName + " (" + Localise("p", "ad" + effect.Level, text) + ")";
                    id += "_ad";
                }

                // :385-389 — `drunk` is the one effect whose caption is chosen by its level.
                if (effect.IsDrunk)
                {
                    name = Localise("p", "drunk" + effect.Level, text);
                }

                // :390-393 — a timed effect shows whole seconds.
                if (!effect.Forever)
                {
                    value = effect.Seconds.ToString();
                }

                // :394-397 — food is renamed to one shared word, whatever it is. The Oracle reads it
                // from the `e` table (`this.n_food = Res.txt("e","food")`, :37), not from `pip`.
                if (effect.IsFood)
                {
                    name = Localise("e", "food", text);
                }

                rows.Add(new PipStatRow(id, name, value));
            }

            return rows;
        }

        // ── Sub-page 5: Health / self-treatment (PipPageStat.as:177-298) ──────

        private static List<PipStatRow> HealthRows(PipStatSnapshot s, PipTextTable text)
        {
            var rows = new List<PipStatRow>(14);
            PipStatVitals v = s.Vitals;

            // :179-184 — a damaged medical module replaces the page with one warning. The Oracle sets
            // `statHead.visible = false` and returns before pushing a single row; the caller draws the
            // empty text.
            if (v.MedicineDisabled) return rows;

            // :190-195 — health, with a bar.
            rows.Add(new PipStatRow("hp", Localise("p", "hp", text),
                PipBuckReadoutFormat.As3Round(v.Hp) + "/" + PipBuckReadoutFormat.As3Round(v.MaxHp),
                1f, Fraction(v.Hp, v.MaxHp)));

            // :196-200 — the organs' own header, with no value.
            rows.Add(new PipStatRow("organism", Localise("p", "organism", text) + ":", string.Empty));

            // :201-230 — the five organs. The row id carries the trauma stage, which is how the info
            // pane finds the matching trauma perk (`:625-680`).
            rows.Add(new PipStatRow("statHead" + v.HeadSt, "   " + Localise("p", "head", text),
                PipBuckReadoutFormat.As3Round(v.HeadHp) + "/" + PipBuckReadoutFormat.As3Round(v.InMaxHp),
                1f, Fraction(v.HeadHp, v.InMaxHp)));

            rows.Add(new PipStatRow("statTors" + v.TorsoSt, "   " + Localise("p", "tors", text),
                PipBuckReadoutFormat.As3Round(v.TorsoHp) + "/" + PipBuckReadoutFormat.As3Round(v.InMaxHp),
                1f, Fraction(v.TorsoHp, v.InMaxHp)));

            rows.Add(new PipStatRow("statLegs" + v.LegsSt, "   " + Localise("p", "legs", text),
                PipBuckReadoutFormat.As3Round(v.LegsHp) + "/" + PipBuckReadoutFormat.As3Round(v.InMaxHp),
                1f, Fraction(v.LegsHp, v.InMaxHp)));

            rows.Add(new PipStatRow("statBlood" + v.BloodSt, "   " + Localise("p", "blood", text),
                PipBuckReadoutFormat.As3Round(v.BloodHp) + "/" + PipBuckReadoutFormat.As3Round(v.InMaxHp),
                1f, Fraction(v.BloodHp, v.InMaxHp)));

            rows.Add(new PipStatRow("statMana" + v.ManaSt, "   " + Localise("p", "mana", text),
                PipBuckReadoutFormat.As3Round(v.ManaOrganHp) + "/" + PipBuckReadoutFormat.As3Round(v.InMaxMana),
                1f, Fraction(v.ManaOrganHp, v.InMaxMana)));

            // :231-260 — the six exposure rows, in the Oracle's order. Each is gated on what the port
            // can actually read, and the two groups are NOT contiguous: the four radiation/wound/poison
            // readings have no counterpart, while the two resistances come from the unit's vulnerability
            // table and are available. Asking per row is what keeps the Oracle's order intact when the
            // two flags disagree — which, in this port, they always do.
            AddExposureRow(rows, "rad", v.HasExposureReadings, text,
                PipBuckReadoutFormat.As3Round(v.Rad).ToString());
            AddExposureRow(rows, "radx", v.HasExposureReadings, text,
                PipBuckReadoutFormat.As3Round((1f - v.RadX) * 100f) + "%");
            AddExposureRow(rows, "cut", v.HasExposureReadings, text,
                PipBuckReadoutFormat.Numb(v.Cut));
            AddExposureRow(rows, "resbleeding", v.HasResistanceReadings, text,
                PipBuckReadoutFormat.As3Round((1f - v.ResBleeding) * 100f) + "%");
            AddExposureRow(rows, "poison", v.HasExposureReadings, text,
                PipBuckReadoutFormat.Numb(v.Poison));
            AddExposureRow(rows, "respoison", v.HasResistanceReadings, text,
                PipBuckReadoutFormat.As3Round((1f - v.ResPoison) * 100f) + "%");

            // :261-268 — the phoenix pet, when it exists and its trigger is set.
            if (v.HasPhoenix)
            {
                rows.Add(new PipStatRow("phoenix", v.PhoenixName,
                    PipBuckReadoutFormat.As3Round(v.PhoenixHp) + "/" +
                    PipBuckReadoutFormat.As3Round(v.PhoenixMaxHp)));
            }

            // :269-297 — the addictions, each labelled by its stage.
            foreach (PipStatAddiction addiction in v.Addictions)
            {
                if (addiction.Value <= 0f) continue;

                int stage = Math.Min(Math.Max(addiction.Stage, 0), 3);
                rows.Add(new PipStatRow(addiction.Id,
                    Localise("e", addiction.Id + "_ad", text),
                    PipBuckReadoutFormat.As3Round(addiction.Value) + "% (" +
                    Localise("p", "ad" + stage, text) + ")"));
            }

            return rows;
        }

        /// <summary>
        /// One of the six exposure rows, emitted only when the port can supply it. <b>The per-row
        /// argument is what preserves the Oracle's order</b>: it pushes all six in one sequence
        /// (<c>PipPageStat.as:231-260</c>), so the caller asks for all six and each decides for itself —
        /// gating them in two blocks instead would reorder the list whenever the flags disagreed.
        /// </summary>
        private static void AddExposureRow(List<PipStatRow> rows, string id, bool available,
            PipTextTable text, string value)
        {
            if (!available) return;
            rows.Add(new PipStatRow(id, Localise("p", id, text), value));
        }

        // ── Sub-page 6: the perk picker (PipPageStat.as:411-449) ──────────────

        private static List<PipStatRow> PerkChoiceRows(PipStatSnapshot s, PipTextTable text)
        {
            // :445 — `arr.sortOn(["sort","nazv"])`, on the inputs, for the same reason as PerkRows.
            var sorted = new List<PipStatPerk>(s.PerkChoices);
            sorted.Sort((a, b) => ComparePerks(a, b, text));

            var rows = new List<PipStatRow>(sorted.Count);

            foreach (PipStatPerk choice in sorted)
            {
                // :436 — `lvl: numb + 1` — the picker previews the rank the perk WOULD be, so a perk
                // the player has never taken reads "1", not "0".
                string value = (choice.Rank + 1).ToString();
                if (choice.MaxRank > 1 && choice.MaxRank < 1000)
                {
                    // :502-505 — the "/max" suffix, which the Oracle's own `maxlvl` range check bounds.
                    value += "/" + choice.MaxRank;
                }

                // :550-553 — a perk the player cannot take is dimmed rather than hidden.
                float alpha = choice.Selectable ? 1f : 0.4f;

                rows.Add(new PipStatRow(choice.Id, Localise("e", choice.Id, text), value, alpha));
            }

            return rows;
        }

        // ── The row clip's other halves ───────────────────────────────────────

        /// <summary>
        /// The header row's two captions — <c>statHead.nazv</c> / <c>statHead.numb</c>, assigned at the
        /// top of each <c>setSubPages</c> branch (<c>PipPageStat.as:189, 303-304, 326-327, 365-367,
        /// 414-415</c>). Empty strings mean "the header is blank on this sub-page", which is the
        /// Oracle's own state for Main and Health.
        /// </summary>
        public static (string Name, string Value) HeaderFor(int subPage, PipTextTable text)
        {
            switch (subPage)
            {
                case SkillsSubPage: return (Localise("p", "is1", text), Localise("p", "is2", text));
                case PerksSubPage: return (Localise("p", "is5", text), Localise("p", "is2", text));
                case EffectsSubPage: return (Localise("p", "is3", text), Localise("p", "is4", text));
                case PerkPickSubPage: return (Localise("p", "is5", text), Localise("p", "is2", text));
                default: return (string.Empty, string.Empty);
            }
        }

        /// <summary>
        /// The notice a sub-page shows <b>instead of</b> its list, or null. The Oracle writes these into
        /// <c>vis.emptytext</c> and hides the header, so a caller must suppress the header when this is
        /// non-null (<c>PipPageStat.as:181, 353, 402</c>).
        /// </summary>
        public static string EmptyTextFor(int subPage, PipStatSnapshot s, PipTextTable text)
        {
            switch (subPage)
            {
                // :179-181 — the damaged medical module. This one is a *page-level* message: it fires
                // before any row is built, not because the list is empty.
                case HealthSubPage when s.Vitals.MedicineDisabled:
                    return Localise("p", "emptymed", text);

                // :351-355 — no perks at all.
                case PerksSubPage when s.Perks.Count == 0:
                    return Localise("p", "emptyperk", text);

                // :400-404 — no active effects.
                case EffectsSubPage when s.Effects.Count == 0:
                    return Localise("p", "emptyeff", text);

                default:
                    return null;
            }
        }

        /// <summary>
        /// The bottom strip — the port of <c>PipPageStat.showBottext</c>
        /// (<c>PipPageStat.as:770-861</c>).
        /// </summary>
        public static string BottomText(PipStatSnapshot s, int subPage, PipTextTable text)
        {
            switch (subPage)
            {
                // :774-777 — the in-game clock. Note the label comes from the `pip` table and the
                // value is `game.gameTime()`, which the port formats at the source.
                case MainSubPage:
                    return Localise("p", "tgame", text) + ": " + s.GameTime;

                // :778-781 — unspent skill points, wrapped in the Oracle's `pink()`.
                case SkillsSubPage:
                    return Localise("p", "skillpoint", text) + ": " + Pink(s.SkillPoints.ToString());

                // :782-785 — unspent perks.
                case PerksSubPage:
                    return Localise("p", "perkpoint", text) + ": " + Pink(s.PerkPoints.ToString());

                // :786-796 — the picker: an instruction until something is selected, then its name.
                case PerkPickSubPage:
                    return s.SelectedPerk.Length == 0
                        ? Localise("p", "chooseperk", text)
                        : Pink(Localise("e", s.SelectedPerk, text));

                case HealthSubPage:
                    return HealthBottomText(s, text);

                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// The Health sub-page's bottom strip — <c>PipPageStat.as:797-859</c>. It names the medicine
        /// the selected row would use and how many the player holds. The Oracle resolves each row's
        /// medicine by a <c>substr</c> prefix test on the row id, which is why the ids carry their
        /// trauma stage: <c>statHead2</c> still starts with <c>statHead</c>.
        /// </summary>
        private static string HealthBottomText(PipStatSnapshot s, PipTextTable text)
        {
            // The Oracle's `infoItemId` — the row the pointer is over, not the selected perk. On this
            // sub-page nothing is selectable, so the strip is blank until the player hovers a row.
            string id = s.HoveredRowId;
            if (string.IsNullOrEmpty(id)) return string.Empty;

            // `inv.items[id].kol` — a missing row reads 0, the Oracle's own answer.
            int Count(string itemId) =>
                s.MedicineCounts != null && s.MedicineCounts.TryGetValue(itemId, out int n) ? n : 0;

            if (id == "hp")
            {
                // :800-803 — the three health potions summed, under one label.
                return Localise("p", "healpotions", text) + ": " +
                       Yellow((Count("pot1") + Count("pot2") + Count("pot3")).ToString());
            }

            if (id.StartsWith("statMana", StringComparison.Ordinal))
            {
                // :820-823 — the three mana potions, under a label from the `i` table.
                return Localise("i", "potm1", text) + ": " +
                       Yellow((Count("potm1") + Count("potm2") + Count("potm3")).ToString());
            }

            // :804-819 — every other row names a single item id.
            string item;
            if (id == "rad") item = "antiradin";
            else if (id == "cut") item = "pot0";
            else if (id == "poison") item = "antidote";
            else if (id.StartsWith("statBlood", StringComparison.Ordinal)) item = "bloodpak";
            else if (id == "phoenix") item = "radcookie";
            else if (id.StartsWith("post_", StringComparison.Ordinal)) item = "detoxin";
            else return string.Empty;

            return Localise("i", item, text) + ": " + Yellow(Count(item).ToString());
        }

        // ── Scrolling ─────────────────────────────────────────────────────────
        //
        // The page uses the same 18-row clip and the same `maxrows` arithmetic as the inventory, so the
        // two helpers are shared rather than re-written — a second copy of "which row index is which
        // entry" is exactly the kind of duplication that drifts (PipPage.as:944-1002).

        /// <summary>The list entry a row clip shows, or -1 for an empty clip.</summary>
        public static int EntryIndexForRow(int rowIndex, int rowCount, int scroll) =>
            PipInventoryList.EntryIndexForRow(rowIndex, rowCount, scroll, VisibleRows);

        /// <summary>The largest scroll offset a list of <paramref name="rowCount"/> rows allows.</summary>
        public static int MaxScroll(int rowCount) =>
            PipInventoryList.MaxScroll(rowCount, VisibleRows);

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// <c>Res.pipText</c> / <c>Res.txt</c> — a lookup in the localisation document, falling back to
        /// the Oracle's own missing-key marker. <b>Never a hard-coded English word</b>: a caption the
        /// port invented would read as a translation of a string the game does not have.
        /// </summary>
        private static string Localise(string alias, string id, PipTextTable text)
        {
            if (TryLocalise(alias, id, text, out string value)) return value;

            // Res.as:120-130 — the Oracle's own "this key is missing" marker.
            return "*" + alias + "_" + id;
        }

        /// <summary>
        /// <see cref="Localise"/> for a caller that must branch on <i>whether the document has the key
        /// at all</i> — the addiction rename (<c>PipPageStat.as:376-384</c>) only happens when the
        /// <c>&lt;id&gt;_ad</c> entry exists.
        /// </summary>
        private static bool TryLocalise(string alias, string id, PipTextTable text, out string value)
        {
            if (text != null && text.TryGet(alias, id, 0, out value)) return true;

            value = string.Empty;
            return false;
        }

        /// <summary>The Oracle's <c>pink()</c> — the emphasis colour the bottom strip uses (<c>PipPage.as</c>).</summary>
        private static string Pink(string value) =>
            PipBuckReadoutFormat.Wrap(value, PipBuckReadoutFormat.ClassToHex("pink"));

        /// <summary>
        /// The tier word on the reputation row. A source may supply it
        /// (<see cref="PipStatSnapshot.ReputationText"/>) because a reputation system would know the
        /// tier directly; when it does not, the port derives it from the number.
        ///
        /// <para><b>Deriving it is the exact answer, not a fallback.</b> <c>Pers.repTex()</c> is a pure
        /// function of <c>rep</c> and the five thresholds above, so a port holding the number can
        /// produce the same word — which is why a fresh character reads <c>0 (Mysterious stranger)</c>
        /// here and in the Oracle alike.</para>
        /// </summary>
        private static string ReputationTextFor(PipStatSnapshot s, PipTextTable text)
        {
            if (!string.IsNullOrEmpty(s.ReputationText)) return s.ReputationText;
            return Localise("p", "reput" + ReputationTier(s.Reputation), text);
        }

        /// <summary>
        /// <c>Pers.repTex()</c>'s tier suffix (<c>Pers.as:2391-2413</c>) — the key tail for
        /// <c>reput0</c>..<c>reput4</c> / <c>reputmax</c>.
        /// </summary>
        public static string ReputationTier(int reputation)
        {
            if (reputation >= ReputationGood) return "max";
            if (reputation >= Reputation4) return "4";
            if (reputation >= Reputation3) return "3";
            if (reputation >= Reputation2) return "2";
            if (reputation >= Reputation1) return "1";
            return "0";
        }

        /// <summary>The Oracle's <c>yel()</c> — the medicine counts' colour (<c>PipPageStat.as:802</c>).</summary>
        private static string Yellow(string value) =>
            PipBuckReadoutFormat.Wrap(value, PipBuckReadoutFormat.ClassToHex("yel"));

        /// <summary>A 0..1 bar fraction, clamped — the Oracle's <c>Math.max(0, param2.bar)</c> (<c>:558</c>).</summary>
        private static float Fraction(float value, float max)
        {
            if (max <= 0f) return 0f;
            float f = value / max;
            if (f < 0f) return 0f;
            if (f > 1f) return 1f;
            return f;
        }

        /// <summary>
        /// <c>arr.sortOn(["sort","nazv"])</c> — the Oracle's two-key sort: the numeric sort key first,
        /// then the display name. <b>The name tiebreak is not decoration</b>: without it two perks
        /// sharing a sort key would come back in whatever order the source dictionary enumerated, so
        /// the list would reshuffle on every rebuild and look like a rendering fault. The name compared
        /// is the <i>localised</i> one, which is what `sortOn` sees in the Oracle too.
        /// </summary>
        private static int ComparePerks(PipStatPerk a, PipStatPerk b, PipTextTable text)
        {
            int bySort = a.Sort.CompareTo(b.Sort);
            if (bySort != 0) return bySort;

            return string.CompareOrdinal(Localise("e", a.Id, text), Localise("e", b.Id, text));
        }
    }
}
