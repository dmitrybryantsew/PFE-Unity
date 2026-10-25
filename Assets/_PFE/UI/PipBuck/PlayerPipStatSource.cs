using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Effects;
using PFE.Systems.Inventory;
using PFE.Systems.RPG;
using PerkDef = PFE.Systems.RPG.Data.PerkDefinition;
using SkillDatabase = PFE.Systems.RPG.Data.SkillDefinitionDatabase;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// Where the character sheet's rows come from. An interface so the page can be built and its rules
    /// exercised against a hand-made snapshot — no player, no <c>ScriptableObject</c>, no
    /// <c>MonoBehaviour</c>.
    ///
    /// <para><b>One method, returning everything.</b> The Oracle's <c>setSubPages</c> switches on
    /// <c>page2</c> and reads a different slice of the player in each branch. The port reads all of them
    /// and lets <see cref="PipStatList"/> choose, because "which rows belong to which sub-page" is
    /// exactly the rule that has to stay in the half that can be run offline.</para>
    /// </summary>
    public interface IPipStatSource
    {
        /// <summary>Everything the character sheet reads, as plain data. Never null.</summary>
        PipStatSnapshot Read();

        /// <summary>
        /// Spend one perk point on <paramref name="perkId"/> — the picker's commit, and the <b>only</b>
        /// write on this seam (<c>PipPageStat.as:1016-1018</c>). Returns false when the pick cannot be
        /// paid for.
        /// </summary>
        bool TryCommitPerk(string perkId);
    }

    /// <summary>
    /// Reads the character sheet out of the live player — the port of the five data-gathering branches of
    /// <c>PipPageStat.setSubPages</c> (<c>PipPageStat.as:65-451</c>).
    ///
    /// <para><b>Every owner is a <see cref="Func{TResult}"/>, and that is deliberate.</b> The page's data
    /// lives on three different objects — the player's <c>CharacterStats</c>, its <c>UnitStats</c> and
    /// its <c>GameInventory</c> — and none of them exists when the PipBuck is built: the player is
    /// assembled in <c>PlayerController.Awake</c>, which Unity does not order against the bootstrapper's
    /// <c>Start</c>. A constructor that captured concrete instances would bind null once and then show an
    /// empty sheet forever, which is the failure <c>HudBootstrapper</c> documents. Resolving per read
    /// means a player who appears a second later simply appears in the sheet.</para>
    ///
    /// <para><b>What the port cannot read produces no row, never a zero.</b> Four of the param table's
    /// variables (<c>armor</c>, <c>marmor</c>, <c>armor_qual</c>, <c>visiMult</c>) and four of the Health
    /// sub-page's six exposure readings have no port member; the resolver returns <c>null</c> for them,
    /// which is the Oracle's own "this variable does not exist" path and pushes <i>no row</i>. See
    /// <see cref="PipStatList"/>'s class summary for why an absent row is the honest answer and a zero
    /// is not.</para>
    /// </summary>
    public sealed class PlayerPipStatSource : IPipStatSource
    {
        private readonly Func<CharacterStats> _characterStats;
        private readonly Func<UnitStats> _unitStats;
        private readonly Func<GameInventory> _inventory;

        // ── Cached lists ──────────────────────────────────────────────────────
        //
        // Cleared at the start of every Read rather than appended to: a stale entry would show a perk
        // the player no longer holds, or a medicine they have spent. Reusing the instances keeps a
        // rebuild — which happens on every sub-tab press and every scroll step — allocation-free.
        //
        // Aliasing them into the snapshot is safe because the snapshot is only ever read before the next
        // Read replaces it: PipPageStatView.Rebuild assigns `_snapshot = _source.Read()` and then
        // consumes it, and the hover path (which does not call Read) reads the same instance back.

        private readonly List<PipStatSkill> _skills = new List<PipStatSkill>();
        private readonly List<PipStatPerk> _perks = new List<PipStatPerk>();
        private readonly List<PipStatPerk> _choices = new List<PipStatPerk>();
        private readonly List<PipStatEffect> _effects = new List<PipStatEffect>();
        private readonly List<PipStatAddiction> _addictions = new List<PipStatAddiction>();
        private readonly Dictionary<string, int> _medicine = new Dictionary<string, int>(StringComparer.Ordinal);

        public PlayerPipStatSource(
            Func<CharacterStats> characterStats,
            Func<UnitStats> unitStats,
            Func<GameInventory> inventory)
        {
            _characterStats = characterStats;
            _unitStats = unitStats;
            _inventory = inventory;
        }

        // ── Optional providers ────────────────────────────────────────────────
        //
        // Left null until the owning system exists, and each null has a stated default rather than a
        // guess. Same arrangement, and the same reason, as PlayerPipStatusSource's providers.

        /// <summary>
        /// <c>gg.pers.persName</c>. Null leaves the name row's value empty, which is what the Oracle
        /// shows before a name has been given.
        /// </summary>
        public Func<string> PlayerNameProvider { get; set; }

        /// <summary>
        /// <c>World.w.game.globalDif</c> — the index into the <c>gui</c> table's <c>dif0</c>..<c>dif4</c>.
        /// Null uses <see cref="DefaultDifficulty"/>, which is the Oracle's own default
        /// (<c>Game.as:41</c>) and the port's <c>NewGameSettings</c> default alike.
        /// </summary>
        public Func<int> GlobalDifficultyProvider { get; set; }

        /// <summary>
        /// <c>gg.pers.rep</c>. Null reads 0, the Oracle's own starting value (<c>Pers.as:55</c>), which
        /// is also the truth for a port with no reputation system — nothing can raise it.
        /// </summary>
        public Func<int> ReputationProvider { get; set; }

        /// <summary>
        /// <c>World.w.game.gameTime()</c>, already formatted. Null leaves the bottom strip's value empty.
        ///
        /// <para><b>The Oracle's clock is wall time, and the port keeps none.</b> <c>gameTime()</c> is
        /// <c>t_save + (now - dBeg)</c> — real seconds accumulated across saves (<c>Game.as:753-762</c>),
        /// and <c>CampaignVariableCatalog</c> records the accounting as not ported. The obvious
        /// substitute, <c>SimLoop.TickIndex / 30</c>, is a <i>different quantity</i>: it counts simulated
        /// time since the scene loaded, so it stops while the device is open and resets on load. A row
        /// that guesses is worse than a row that is empty.</para>
        /// </summary>
        public Func<string> GameTimeProvider { get; set; }

        /// <summary>
        /// <c>World.w.game.triggers["nomed"] &gt; 0</c> — the medical module is damaged, so the Health
        /// sub-page shows one warning instead of its rows (<c>PipPageStat.as:179-184</c>).
        ///
        /// <para><b>Null means "module intact", and that is a choice, not a default.</b> The port has no
        /// trigger table. The Oracle sets <c>nomed</c> on a new game and clears it on the first entry to
        /// the base camp (<c>Game.as:307, :376</c>), so a fresh Oracle character does see the warning —
        /// but <c>PlayerPipStatusSource</c> already decided to draw the five organs for the port's player
        /// rather than hide them behind a trigger that cannot be set, and the health sub-page shows those
        /// same five organs. Keeping the two views in agreement is worth more than reproducing a gate
        /// whose value nothing in the port can change.</para>
        /// </summary>
        public Func<bool> MedicineDisabledProvider { get; set; }

        /// <summary>
        /// The player's radiation state — <c>gg.rad</c>. Null means no radiation model in this rig, and
        /// then the two radiation rows stay absent instead of printing zeroes.
        /// </summary>
        public Func<PFE.Entities.Player.PlayerRadiationState> RadiationProvider { get; set; }

        /// <summary>
        /// <c>Game.as:41</c> — <c>globalDif</c>'s own default, and <c>NewGameDifficulty.Normal</c>'s
        /// value in the port's new-game flow. The two agree, which is why the index needs no translation.
        /// </summary>
        public const int DefaultDifficulty = 2;

        // ── Write ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Spend one perk point on <paramref name="perkId"/> — the picker's commit
        /// (<c>PipPageStat.as:1016-1018</c>, reached from <c>butOk</c> via <c>transOk</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The <c>true</c> is load-bearing.</b> The Oracle's accept runs
        /// <c>addPerk(this.selectedPerk, true)</c> — the <c>minus</c> form, which costs a point. The
        /// default form is <b>free</b> and is what a script uses (<c>Script.as:327</c>), so a picker
        /// built on the default would let the player take every perk in the game for nothing.</para>
        ///
        /// <para><b>Resolved per call</b>, like every other owner in this class: the player is assembled
        /// in <c>PlayerController.Awake</c>, which Unity does not order against the bootstrapper, so a
        /// captured <c>CharacterStats</c> would write to a destroyed object and throw nothing.</para>
        /// </remarks>
        public bool TryCommitPerk(string perkId)
        {
            if (string.IsNullOrEmpty(perkId)) return false;

            CharacterStats stats = _characterStats?.Invoke();
            if (stats == null) return false;

            return stats.AddPerk(perkId, true);
        }

        // ── Read ──────────────────────────────────────────────────────────────

        public PipStatSnapshot Read()
        {
            CharacterStats stats = _characterStats?.Invoke();
            UnitStats unit = _unitStats?.Invoke();

            ReadSkills(stats);
            ReadPerks(stats);
            ReadEffects(unit);
            ReadMedicine();

            return new PipStatSnapshot
            {
                PlayerName = PlayerNameProvider?.Invoke() ?? string.Empty,

                Level = stats != null ? stats.Level : 0,
                XpCur = stats != null ? stats.Xp : 0,
                XpNext = XpNextFor(stats),

                GlobalDifficulty = GlobalDifficultyProvider != null
                    ? GlobalDifficultyProvider()
                    : DefaultDifficulty,

                Reputation = ReputationProvider != null ? ReputationProvider() : 0,

                GameTime = GameTimeProvider?.Invoke() ?? string.Empty,

                // `gg.armor != 0 || gg.marmor != 0` — the show='2' gate. Both operands are unreadable in
                // the port (the worn armour's condition is tracked, its damage resistance is not), so
                // this flag is inert: the only row it gates is `aqual`, whose variable `armor_qual` is
                // null and therefore produces no row whichever value the flag takes.
                ArmourPresent = false,

                // `World.w.game.triggers["story_canter"] > 0` — no trigger table. False hides `pink`,
                // the one show='3' row, which is also the Oracle's state on a new game.
                StoryCanter = false,

                Variable = (name, tip) => ReadVariable(stats, unit, name, tip),

                Skills = _skills,
                SkillPoints = stats != null ? stats.SkillPoints : 0,

                Perks = _perks,
                PerkPoints = stats != null ? stats.PerkPoints : 0,

                Effects = _effects,

                Vitals = ReadVitals(stats, unit),
                MedicineCounts = _medicine,

                PerkChoices = _choices,

                // `this.selectedPerk` and `this.infoItemId` are page state, not player state: the page
                // owns the selection and sets the hovered id itself. See PipStatSnapshot.
                SelectedPerk = string.Empty,
                HoveredRowId = string.Empty,
            };
        }

        // ── The param table's variables (PipPageStat.as:90-175) ───────────────

        /// <summary>
        /// Reads one param-table variable, or <c>null</c> when the port has no such reading.
        ///
        /// <para><b>Null is a load-bearing answer.</b> <c>PipPageStat.as:117-118</c> traces and
        /// <c>continue</c>s when the variable does not exist, pushing no row at all — so a reading the
        /// port cannot take must produce <i>nothing</i>, not a zero. A zero is a claim about the player;
        /// an absent row is a claim about the port, and only one of the two is true.</para>
        /// </summary>
        private static float? ReadVariable(CharacterStats stats, UnitStats unit, string name, int tip)
        {
            // `PipPageStat.as:105-121` — a tip-4 row reads `gg.vulner[@v]`, where `@v` is a damage-type
            // INDEX rather than a name (`bullet` → 0, `blade` → 1, …). The port's `DamageType` is the
            // Oracle's own `Unit.D_*` numbering, so the digit is the enum value and nothing has to
            // translate it. `gg.vulner` is the unit's table, which is where an effect's resistance write
            // lands (CharacterStats.ApplyEffectParam).
            if (tip == 4)
            {
                if (unit == null) return null;
                if (!int.TryParse(name, out int damageIndex)) return null;
                if (!Enum.IsDefined(typeof(DamageType), damageIndex)) return null;
                return unit.Vulnerabilities.GetVulnerability((DamageType)damageIndex);
            }

            if (stats == null) return null;

            switch (name)
            {
                // `param_atk` — the attack multipliers.
                case "allDamMult": return stats.AllDamMult;
                case "meleeDamMult": return stats.meleeDamMult;
                case "meleeSpdMult": return stats.meleeSpdMult;
                case "gunsDamMult": return stats.gunsDamMult;
                case "allPrecMult": return stats.allPrecMult;
                case "reloadMult": return stats.reloadMult;
                case "spellsDamMult": return stats.spellsDamMult;
                case "punchDamMult": return stats.punchDamMult;

                // `param_def`.
                case "skin": return stats.skin;
                case "dexter": return stats.dexter;
                case "allVulnerMult": return stats.AllVulnerMult;

                // `param_oth`.
                case "allSpeedMult": return stats.allSpeedMult;
                case "runSpeedMult": return stats.runSpeedMult;
                case "stamRun": return stats.stamRun;
                case "allDManaMult": return stats.AllDManaMult;
                case "warlockDManaMult": return stats.warlockDManaMult;
                case "healMult": return stats.healMult;
                case "lockPick": return stats.lockPick;
                case "hacker": return stats.hacker;
                case "maxOd": return stats.maxOd;

                default:
                    // `armor` / `marmor` / `armor_qual` — the worn armour's own defence value. The port
                    // tracks the armour instance's *condition* but not its damage resistance.
                    // `visiMult` — the Oracle's sneak multiplier; the port's closest member is
                    // `stealthMult`, which is a different quantity and is deliberately not substituted.
                    return null;
            }
        }

        /// <summary>
        /// <c>gg.pers.xpNext</c> — <c>Pers.xpProgress(level)</c> (<c>Pers.as:580</c>, <c>:1144-1152</c>),
        /// which is what the row's second number subtracts from.
        ///
        /// <para><b>The same closed form on both sides.</b> <c>LevelCurve.GetXpForLevel</c> is the
        /// Oracle's expression term for term, and <c>CharacterStats</c> already uses it with the same
        /// argument (<c>CharacterStats.cs:1009</c>), so the displayed "XP still needed" and the level-up
        /// rule cannot disagree. The fallback is that same call site's fallback, for the case where the
        /// curve asset is missing.</para>
        /// </summary>
        private static int XpNextFor(CharacterStats stats)
        {
            if (stats == null) return 0;

            var curve = stats.Curve;
            if (curve != null) return curve.GetXpForLevel(stats.Level);

            return 5000 * stats.Level * (stats.Level + 1) / 2;
        }

        // ── Sub-page 2: skills (PipPageStat.as:299-322) ───────────────────────

        /// <summary>
        /// Every skill, in the Oracle's own order — <c>Pers.skill_ids</c>, which the port keeps as
        /// <c>CharacterStats.GetAllSkillIds()</c> and which is the same sixteen ids.
        /// </summary>
        private void ReadSkills(CharacterStats stats)
        {
            _skills.Clear();
            if (stats == null) return;

            foreach (string id in stats.GetAllSkillIds())
            {
                int level = stats.GetSkillLevel(id);
                bool isPost = CharacterStats.IsSkillPost(id);

                // `Pers.getPostSkLevel(level)` — the post-skill tier, which the row prints alongside the
                // level. A regular skill's tier is derived by the model instead, because the Oracle
                // derives it there too (setStatItem, :526) rather than storing it.
                int tier = isPost ? CharacterStats.GetPostSkillTier(level) : 0;

                _skills.Add(new PipStatSkill(id, level, isPost, tier, PostTargetFor(tier)));
            }
        }

        /// <summary>
        /// <c>Pers.postSkTab[tier]</c> — the ceiling the current tier would reach, or 100 past the end of
        /// the table, which is the Oracle's own out-of-range answer (<c>PipPageStat.as:516-520</c>).
        /// </summary>
        private static int PostTargetFor(int tier)
        {
            int[] table = CharacterStats.PostSkTab;
            return tier >= 0 && tier < table.Length ? table[tier] : 100;
        }

        // ── Sub-pages 3 and 6: perks (PipPageStat.as:323-362, :411-449) ───────

        /// <summary>
        /// Both perk lists, from one walk of the perk database — the held list and the picker's
        /// candidates. They are read together because they share the same definitions and the same
        /// database handle; the model decides which one a sub-page shows.
        /// </summary>
        private void ReadPerks(CharacterStats stats)
        {
            _perks.Clear();
            _choices.Clear();
            if (stats == null) return;

            SkillDatabase database = stats.SkillDatabase;

            ReadHeldPerks(stats, database);
            ReadPerkChoices(stats, database);
        }

        /// <summary>
        /// The perks the player <b>holds</b> — <c>PipPageStat.as:328-361</c>, which walks
        /// <c>this.pers.perks</c>. The port's <c>PerkRanks</c> is that same table: it is written only
        /// when a perk is added or a save is loaded, so it holds no zero-rank entries and needs no
        /// filter beyond a defensive one.
        /// </summary>
        private void ReadHeldPerks(CharacterStats stats, SkillDatabase database)
        {
            foreach (KeyValuePair<string, int> pair in stats.PerkRanks)
            {
                if (pair.Value <= 0) continue;

                PerkDef def = database != null ? database.GetPerk(pair.Key) : null;

                // `maxlvl = 1` unless the perk's own row says otherwise — the Oracle's default
                // (:329-336). The held list does not print it (only the picker does), but the row's
                // model carries it so a later renderer need not re-derive it.
                int maxRank = def != null && def.MaxRank > 0 ? def.MaxRank : 1;

                // :342 — `sort: (AllData.d.perk.(@id==pid).@tip == "0") ? 2 : 1`. The port has no `tip`
                // attribute, but `isPlayerSelectable` IS the importer's read of `tip == "1"`
                // (SkillAndPerkDataImporter.cs:150,169; RPGDataImporter.cs:295), so `tip == 0` is its
                // negation. A perk with no definition gets the Oracle's own default of 1, because its
                // `xperk` lookup is empty and `@tip` on an empty XMLList is undefined.
                int sort = def != null && !def.IsPlayerSelectable ? 2 : 1;

                _perks.Add(new PipStatPerk(pair.Key, pair.Value, maxRank, sort, false));
            }
        }

        /// <summary>
        /// The perks the player <b>could take</b> — <c>PipPageStat.as:414-445</c>, which walks the whole
        /// perk table and keeps the ones the player is eligible for.
        ///
        /// <para><b>Not <c>SkillDefinitionDatabase.GetAvailablePerks</c>, deliberately.</b> That method
        /// filters to the perks that can be unlocked <i>right now</i>; the Oracle lists the ones that are
        /// merely <i>not maxed</i> and dims the rest (<c>setStatItem</c>, <c>:550-553</c>). Using it would
        /// silently drop every candidate the player is still growing into, which is most of the list.</para>
        /// </summary>
        private void ReadPerkChoices(CharacterStats stats, SkillDatabase database)
        {
            if (database == null) return;

            foreach (PerkDef perk in database.GetAllPerks())
            {
                if (perk == null || string.IsNullOrEmpty(perk.PerkId)) continue;

                // :417 — `if(dp.@tip == 1)`. The picker lists selectable perks only; the traits the
                // player cannot choose never appear on it.
                if (!perk.IsPlayerSelectable) continue;

                int rank = stats.GetPerkRank(perk.PerkId);
                int maxRank = perk.MaxRank > 0 ? perk.MaxRank : 1;

                // :1427-1430 — `if(numb >= maxlvl) return -1;`, and the page skips `res < 0`, so a maxed
                // perk is not listed at all. This test cannot be left to CanUnlock: CanUnlock also
                // returns false for a maxed perk, but the page needs it EXCLUDED rather than dimmed.
                if (rank >= maxRank) continue;

                // :438-439 — `ok: res > 0` and `sort: 1 - res`, with res in {0,1} from perkPoss. So a
                // perk the player can take now sorts above one they cannot, and one they cannot take yet
                // is listed dimmed rather than hidden.
                bool ok = perk.CanUnlock(stats, rank);

                _choices.Add(new PipStatPerk(perk.PerkId, rank, maxRank, ok ? 0 : 1, ok));
            }
        }

        // ── Sub-page 4: effects (PipPageStat.as:363-410) ──────────────────────

        /// <summary>
        /// The active effects, in the set's own order — <c>for(sk in gg.effects)</c>.
        ///
        /// <para>The Oracle iterates a dictionary keyed by effect id; the port's <c>ActiveEffectSet</c>
        /// keeps a list in insertion order. That is the closest available match, and it is the one a
        /// player can compare against a screenshot: effects appear in the order they were taken.</para>
        /// </summary>
        private void ReadEffects(UnitStats unit)
        {
            _effects.Clear();
            if (unit == null) return;

            ActiveEffectSet set = unit.Effects;
            if (set == null) return;

            foreach (ActiveEffect effect in set.Effects)
            {
                if (effect == null) continue;

                // :390-393 — `Math.round(ef.t / 30)`. `ef.t` counts 30 Hz frames and the port's
                // TicksRemaining counts canonical 30 Hz ticks whatever the configured tick rate is
                // (ActiveEffectSet.CanonicalTicksPerSecond), so the divisor is the same 30. As3Round is
                // the Oracle's Math.round — half toward +Infinity — not Mathf.Round's banker's rounding.
                int seconds = PipBuckReadoutFormat.As3Round(
                    (float)effect.TicksRemaining / ActiveEffectSet.CanonicalTicksPerSecond);

                _effects.Add(new PipStatEffect(
                    id: effect.Id,
                    forever: effect.Forever,
                    seconds: seconds,
                    level: effect.Level,

                    // :376 — `ef.ad` is the effect's own addiction flag. The port keeps it on the
                    // definition (EffectDefinition.add, AS3 `@ad`), which is where the Oracle's
                    // Effect constructor reads it from too.
                    isAddiction: effect.Definition != null && effect.Definition.add,

                    // :394 — `ef.tip == 3` is the food channel.
                    isFood: effect.Tip == EffectType.Food,

                    // :385 — `drunk` is the one effect whose caption is chosen by its level.
                    isDrunk: effect.Id == "drunk"));
            }
        }

        // ── Sub-page 5: health (PipPageStat.as:177-298) ───────────────────────

        /// <summary>
        /// The body readings, gathered from both halves of the player: the organs are <c>Pers</c>'s
        /// (<c>CharacterStats</c>) and the HP pool and the resistances are the <c>Unit</c>'s
        /// (<c>UnitStats</c>) — the Oracle's <c>gg</c> is the unit, which is why it can read both.
        /// </summary>
        private PipStatVitals ReadVitals(CharacterStats stats, UnitStats unit)
        {
            var vitals = new PipStatVitals();

            if (stats != null)
            {
                vitals.HeadHp = stats.headHp;
                vitals.TorsoHp = stats.torsHp;
                vitals.LegsHp = stats.legsHp;
                vitals.BloodHp = stats.bloodHp;
                vitals.ManaOrganHp = stats.manaHp;

                vitals.InMaxHp = stats.InMaxHP;
                vitals.InMaxMana = stats.MaxMana;

                // The row id carries the trauma stage — `"statHead" + gg.pers.headSt` (:202) — and the
                // info pane parses the digit back out to pick the matching trauma perk (:625-680), so
                // these are data rather than decoration.
                vitals.HeadSt = stats.headSt;
                vitals.TorsoSt = stats.torsSt;
                vitals.LegsSt = stats.legsSt;
                vitals.BloodSt = stats.bloodSt;
                vitals.ManaSt = stats.manaSt;
            }

            if (unit != null)
            {
                // :190-195 — the HP row is `gg.hp` / `gg.maxhp`: the UNIT's pool, not Pers's. The port
                // keeps the same split (UnitStats.CurrentHp against CharacterStats.MaxHp), so the
                // denominator here must be the unit's own or the row would mix two systems' ceilings.
                vitals.Hp = unit.CurrentHp.Value;
                vitals.MaxHp = unit.MaxHp.Value;

                // :242-258 — `resbleeding` and `respoison`, from the same vulnerability table the param
                // table's tip-4 rows read. The port CAN supply these, which is why they are gated
                // separately from the four readings below.
                vitals.HasResistanceReadings = true;
                vitals.ResBleeding = unit.Vulnerabilities.GetVulnerability(DamageType.Bleed);
                vitals.ResPoison = unit.Vulnerabilities.GetVulnerability(DamageType.Poison);
            }

            // `gg.rad` and `gg.radX` are real now (PlayerRadiationState / CharacterStats.radX), so the
            // radiation pair is supplied. `gg.cut` / `gg.poison` still have no port member and stay
            // behind their own flag — the Oracle pushes all six rows in one sequence, and the builder
            // asks per row, so the order is intact whichever flags are on.
            PFE.Entities.Player.PlayerRadiationState radiation = RadiationProvider?.Invoke();
            vitals.HasExposureReadings = radiation != null;
            vitals.HasWoundReadings = false;
            if (radiation != null)
            {
                vitals.Rad = radiation.Rad;
            }
            if (stats != null)
            {
                // `gg.radX` lives on the Pers side (UnitPlayer.as:223, written by <sk> and by the
                // equipped armour's radVul), so it is read from CharacterStats even when the radiation
                // state is missing — the resistance is meaningful without an exposure value.
                vitals.RadX = stats.radX;
            }

            // `gg.pets["phoenix"]` — no pet system, so no row. The Oracle's own guard is
            // `Boolean(gg.pets["phoenix"]) && Boolean(triggers["pet_phoenix"])`, i.e. absent is a state
            // it already handles.
            vitals.HasPhoenix = false;

            // `this.pers.addictions` — no addiction model in the port, which ActiveEffect records as a
            // divergence. The list stays empty, and the page then pushes no addiction rows.
            vitals.Addictions = _addictions;

            // `World.w.game.triggers["nomed"] > 0`. See MedicineDisabledProvider for why the port's
            // default is "module intact" rather than the Oracle's new-game value.
            vitals.MedicineDisabled = MedicineDisabledProvider != null && MedicineDisabledProvider();

            return vitals;
        }

        // ── The Health strip's medicine counts (PipPageStat.as:797-859) ───────

        /// <summary>
        /// The player's held quantities, by item id — <c>inv.items[id].kol</c>.
        ///
        /// <para><b>Everything held is copied, not just the ids the strip can name.</b> Which ids the
        /// strip asks for is the model's rule (<c>PipStatList.HealthBottomText</c>), and a source that
        /// pre-filtered to the eight known medicines would move that rule out of the testable half —
        /// which is the same reason the inventory source returns the bag unfiltered.</para>
        /// </summary>
        private void ReadMedicine()
        {
            _medicine.Clear();

            GameInventory inventory = _inventory?.Invoke();
            if (inventory == null) return;

            foreach (KeyValuePair<string, GameItemInstance> pair in inventory.Items)
            {
                if (pair.Value == null) continue;
                _medicine[pair.Key] = pair.Value.Quantity;
            }
        }
    }
}
