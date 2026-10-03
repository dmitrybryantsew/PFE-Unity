using System.Collections.Generic;
using System.Text;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Debug commands exposed to the Lua console as the global <c>rpg</c> table, plus the console verb
    /// <c>rpg</c> (sugar over the same methods).
    ///
    /// <para><b>Why this exists.</b> The RPG modifier engine is observable only through a final stat
    /// value, and a wrong value is indistinguishable from an engine that never ran. That is not
    /// hypothetical here: the audit fixed nine defects and every one of them was <i>inert</i>, because
    /// <c>skillLevels</c> was empty and <c>RecalculateStats</c> iterated nothing. Nine correct fixes
    /// with zero effect looks exactly like nine broken fixes.</para>
    ///
    /// <para>So the report is built to name the failing link rather than print numbers.
    /// <c>rpg status</c> prints the one figure that settles liveness
    /// (<see cref="CharacterStats.FactorCount"/> — the modifier rows the last recalculation actually
    /// recorded), and <c>rpg factors &lt;statId&gt;</c> names which skill or perk produced a value, in
    /// application order. A bare <c>rpg</c> is <b>status</b>, never a toggle, matching <c>col</c> and
    /// <c>prof</c>.</para>
    ///
    /// <para><b>MoonSharp visibility.</b> <c>Wire</c> is <c>internal</c>; MoonSharp's default
    /// reflection interop exposes public members only, so only the command methods below become
    /// callable from Lua. Do not make <c>_playerProvider</c> or <c>Wire</c> public.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsoleRpgCommands
    {
        /// <summary>
        /// Late-resolved so the commands survive a respawn. Resolved per call rather than cached: a
        /// cached reference to a destroyed player is the classic "MissingReferenceException in a debug
        /// tool" failure, and this one would take the console down with it.
        /// </summary>
        private System.Func<PlayerController> _playerProvider;

        internal void Wire(System.Func<PlayerController> playerProvider)
        {
            _playerProvider = playerProvider;
        }

        private static PlayerController FindPlayer()
            => UnityEngine.Object.FindFirstObjectByType<PlayerController>();

        private CharacterStats Resolve()
        {
            var player = _playerProvider != null ? _playerProvider() : null;
            if (player == null) player = FindPlayer();
            return player != null ? player.CharacterStats : null;
        }

        // ── Commands ──────────────────────────────────────────────────────────

        /// <summary>
        /// Engine health. Answers "is the modifier engine live, and from what data?" and ends with a
        /// verdict line that names the broken link when it is not.
        /// </summary>
        public string Status()
        {
            var stats = Resolve();
            if (stats == null)
                return "[rpg] No PlayerController with a CharacterStats in the scene.";

            SkillDefinitionDatabase db = stats.SkillDatabase;
            LevelCurve curve = stats.Curve;
            int authored = stats.GetAllSkillIds().Count;

            var sb = new StringBuilder();
            sb.AppendLine("[rpg] --- RPG engine health ---");
            sb.AppendLine($"  bound UnitStats          : {(stats.BoundUnitStats != null ? "YES" : "NO   <- HP/mana will not reach the unit")}");
            sb.AppendLine($"  SkillDefinitionDatabase  : {DescribeDatabase(db)}");
            sb.AppendLine($"  LevelCurve               : {(curve != null ? $"LOADED -- xpDelta={curve.XpDelta}, {curve.SkillPointsPerLevel} sp/level" : "NULL -- using built-in defaults")}");
            sb.AppendLine($"  skill table              : {stats.SkillLevels.Count} entries (authored: {authored})");
            sb.AppendLine($"  perk table               : {stats.PerkRanks.Count} entries");
            sb.AppendLine($"  level / xp               : {stats.Level} / {stats.Xp}");
            sb.AppendLine($"  modifiers applied        : {stats.FactorCount} factor rows in the last recalc");
            sb.AppendLine($"  stat ids touched         : {Count(stats.TrackedStatIds)}");
            sb.AppendLine();
            sb.AppendLine("  " + Verdict(stats, db));
            return sb.ToString();
        }

        /// <summary>
        /// The skill table: points, tier, how many modifiers the database carries for the skill, and
        /// whether any of them actually reached <c>ApplyNamedStat</c> this recalculation. The last
        /// column is the one that matters — a skill can be present with a full modifier list and still
        /// contribute nothing.
        /// </summary>
        public string Skills()
        {
            var stats = Resolve();
            if (stats == null) return "[rpg] No PlayerController with a CharacterStats in the scene.";

            SkillDefinitionDatabase db = stats.SkillDatabase;
            HashSet<string> applied = CollectAppliedSources(stats);

            var sb = new StringBuilder();
            sb.AppendLine("[rpg] --- skill table ---");
            sb.AppendLine("  skill         pts  tier  mods  applied");
            foreach (string id in stats.GetAllSkillIds())
            {
                SkillDefinition def = db != null ? db.GetSkill(id) : null;
                int mods = (def != null && def.Modifiers != null) ? def.Modifiers.Length : 0;
                sb.AppendLine($"  {id,-12} {stats.GetSkillLevel(id),3}  {stats.GetSkillTier(id),4}  {mods,4}  {(applied.Contains(id) ? "yes" : "-")}");
            }

            if (db == null)
                sb.AppendLine("  !! No database -- every 'mods' column is 0 because RecalculateStats is on the fallback path.");

            return sb.ToString();
        }

        /// <summary>The derived stats after the last recalculation.</summary>
        public string Derived()
        {
            var stats = Resolve();
            if (stats == null) return "[rpg] No PlayerController with a CharacterStats in the scene.";

            var sb = new StringBuilder();
            sb.AppendLine("[rpg] --- derived stats (after the last recalc) ---");
            sb.AppendLine($"  maxHp          {stats.MaxHp,10:F2}      inMaxHP        {stats.InMaxHP,10:F2}");
            sb.AppendLine($"  allDamMult     {stats.AllDamMult,10:F4}      allVulnerMult  {stats.AllVulnerMult,10:F4}");
            sb.AppendLine($"  maxTeleMassa   {stats.MaxTeleMassa,10:F4}      teleDist       {stats.TeleDist,10:F2}");
            sb.AppendLine($"  telePorog      {stats.TelePorog,10:F2}      teleMult       {stats.TeleMult,10:F4}");
            sb.AppendLine($"  throwForce     {stats.ThrowForce,10:F2}      throwDmagic    {stats.ThrowDmagic,10:F2}");
            sb.AppendLine($"  allDManaMult   {stats.AllDManaMult,10:F4}      telemaster     {stats.Telemaster,10}");
            return sb.ToString();
        }

        /// <summary>Every stat id that recorded a factor, with its contribution count.</summary>
        public string Tracked()
        {
            var stats = Resolve();
            if (stats == null) return "[rpg] No PlayerController with a CharacterStats in the scene.";

            var sb = new StringBuilder();
            sb.AppendLine($"[rpg] --- {stats.FactorCount} factor rows across these stat ids ---");
            if (stats.FactorCount == 0)
                sb.AppendLine("  (none) -- the last recalculation recorded no contributions at all.");

            foreach (string id in stats.TrackedStatIds)
                sb.AppendLine($"  {id,-20} {stats.GetFactorsForStat(id).Count,4}");

            return sb.ToString();
        }

        /// <summary>
        /// Which skill or perk produced a stat's value, in the order the modifiers were applied. This
        /// is the command that turns "allDamMult is 1.37 and I don't know why" into a named source.
        /// </summary>
        public string Factors(string statId)
        {
            var stats = Resolve();
            if (stats == null) return "[rpg] No PlayerController with a CharacterStats in the scene.";

            if (string.IsNullOrEmpty(statId))
                return "Usage: rpg factors <statId>    e.g. rpg factors maxTeleMassa\n" +
                       "       rpg tracked              list every stat id that recorded a factor";

            List<CharacterStats.StatFactor> list = stats.GetFactorsForStat(statId);
            if (list.Count == 0)
                return $"[rpg] '{statId}' recorded no factors in the last recalc.\n" +
                       $"      Tracked ids: {Join(stats.TrackedStatIds)}";

            var sb = new StringBuilder();
            sb.AppendLine($"[rpg] --- '{statId}': {list.Count} contribution(s), in application order ---");
            sb.AppendLine("  source           type    value      ->  running result");
            foreach (var f in list)
                sb.AppendLine($"  {f.sourceId,-16} {f.sourceType,-6} {f.value,10:F4}  ->  {f.result,10:F4}");
            return sb.ToString();
        }

        /// <summary>
        /// The damage multipliers (AS3 <c>gg.vulner</c>). This is the readback for the <c>tip='res'</c>
        /// deductions — before the fix they were written to a dictionary nothing ever read, so all 30
        /// authored resistance deductions were invisible here.
        /// </summary>
        public string Resist()
        {
            var stats = Resolve();
            if (stats == null) return "[rpg] No PlayerController with a CharacterStats in the scene.";

            UnitStats unit = stats.BoundUnitStats;
            if (unit == null)
                return "[rpg] CharacterStats is not bound to a UnitStats, so the tip='res' deductions " +
                       "have nowhere to go (they are published via SetVulnerabilityBaseline).";

            VulnerabilityData vuln = unit.Vulnerabilities;
            var sb = new StringBuilder();
            sb.AppendLine("[rpg] --- damage multipliers (UnitStats.Vulnerabilities = AS3 gg.vulner) ---");
            sb.AppendLine("  <1 = resistant, 1 = neutral, >1 = vulnerable. emp is 0 in AS3's baseline (immune).");
            foreach (DamageType t in System.Enum.GetValues(typeof(DamageType)))
                sb.AppendLine($"  {t,-16} {vuln.GetVulnerability(t),8:F4}");
            return sb.ToString();
        }

        /// <summary>
        /// Set a skill level and recalculate, printing before/after for a few stats so the response is
        /// visible rather than asserted. This is the interactive half of <c>rpg status</c>: if the
        /// engine is live, something must move.
        /// </summary>
        public string Set(string skillId, int points)
        {
            var stats = Resolve();
            if (stats == null) return "[rpg] No PlayerController with a CharacterStats in the scene.";

            if (string.IsNullOrEmpty(skillId))
                return "Usage: rpg set <skillId> <points>    e.g. rpg set tele 5";

            bool known = false;
            foreach (string id in stats.GetAllSkillIds())
            {
                if (id == skillId) { known = true; break; }
            }

            if (!known)
                return $"[rpg] '{skillId}' is not one of the {stats.GetAllSkillIds().Count} authored skill ids. " +
                       "Run `rpg skills` to list them.";

            float beforeMaxHp = stats.MaxHp;
            float beforeTele = stats.MaxTeleMassa;
            float beforeDam = stats.AllDamMult;
            int before = stats.GetSkillLevel(skillId);

            stats.SetSkillLevel(skillId, points);

            var sb = new StringBuilder();
            sb.AppendLine($"[rpg] {skillId}: {before} -> {stats.GetSkillLevel(skillId)} points " +
                          $"(tier {stats.GetSkillTier(skillId)}), recalculated.");
            sb.AppendLine($"  maxHp          {beforeMaxHp,10:F2}  ->  {stats.MaxHp,10:F2}");
            sb.AppendLine($"  maxTeleMassa   {beforeTele,10:F4}  ->  {stats.MaxTeleMassa,10:F4}");
            sb.AppendLine($"  allDamMult     {beforeDam,10:F4}  ->  {stats.AllDamMult,10:F4}");
            sb.AppendLine($"  factors now    {stats.FactorCount}");
            sb.AppendLine("  If nothing moved, that skill's modifiers are not reaching ApplyNamedStat -- " +
                          "run `rpg skills` and check its 'applied' column.");
            return sb.ToString();
        }

        /// <summary>Usage for the <c>rpg</c> verb.</summary>
        public string Help()
        {
            return
                "rpg                   - engine health: is the modifier engine live, and from what data?\n" +
                "rpg skills            - skill table: points, tier, imported modifier count, applied?\n" +
                "rpg derived           - the derived stats after the last recalculation\n" +
                "rpg tracked           - every stat id that recorded a factor, with its count\n" +
                "rpg factors <statId>  - which skill/perk produced a stat's value, in application order\n" +
                "rpg res               - damage multipliers (AS3 gg.vulner)\n" +
                "rpg set <skill> <n>   - set a skill level, recalculate, print before/after\n" +
                "Lua: rpg:Status() | rpg:Skills() | rpg:Factors(\"maxTeleMassa\") | rpg:Set(\"tele\",5)";
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string DescribeDatabase(SkillDefinitionDatabase db)
        {
            if (db == null)
                return "NULL   <- fallback path: the imported AllData.as <sk> modifiers are NOT in use";

            int skills = db.Skills != null ? db.Skills.Length : 0;
            int perks = db.Perks != null ? db.Perks.Length : 0;
            return $"LOADED -- {skills} skills, {perks} perks";
        }

        /// <summary>
        /// The verdict names the broken link, because "0 modifiers" has three unrelated causes and they
        /// are indistinguishable from the numbers alone.
        /// </summary>
        private static string Verdict(CharacterStats stats, SkillDefinitionDatabase db)
        {
            if (db == null)
                return "VERDICT: FALLBACK -- no database, so RecalculateStats used " +
                       "ApplySkillEffectsFallback. The imported AllData.as modifiers are not applied.";

            if (stats.SkillLevels.Count == 0)
                return "VERDICT: DEAD -- the skill table is empty, so the skills loop iterated nothing. " +
                       "BindUnitStats (and therefore SeedSkillIds) did not run.";

            if (stats.FactorCount == 0)
                return "VERDICT: DEAD -- the table is seeded but no modifier reached ApplyNamedStat. " +
                       "Run `rpg skills`; a database whose modifier arrays are empty looks exactly like this.";

            return $"VERDICT: LIVE -- {stats.FactorCount} modifiers applied from {stats.SkillLevels.Count} skills. " +
                   "Try `rpg set tele 5` then `rpg derived`.";
        }

        private static HashSet<string> CollectAppliedSources(CharacterStats stats)
        {
            var applied = new HashSet<string>();
            foreach (string statId in stats.TrackedStatIds)
            {
                foreach (var f in stats.GetFactorsForStat(statId))
                {
                    if (!string.IsNullOrEmpty(f.sourceId)) applied.Add(f.sourceId);
                }
            }
            return applied;
        }

        private static int Count(IEnumerable<string> ids)
        {
            int n = 0;
            foreach (string _ in ids) n++;
            return n;
        }

        private static string Join(IEnumerable<string> ids)
        {
            var sb = new StringBuilder();
            foreach (string id in ids)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(id);
            }
            return sb.ToString();
        }
    }
}
