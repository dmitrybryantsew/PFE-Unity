using System;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// The <c>Pers.setGlobalDif(n)</c> parameter table (<c>Pers.as:759-839</c>), as pure data.
    ///
    /// <para><b>This is the spec, transcribed value for value.</b> Every cell below is a literal from
    /// the oracle; do not "tidy" a number, because the table <i>is</i> the behaviour. The
    /// <see cref="DifficultyRules.PersTable"/> factory is the only reader of the index, and
    /// <c>CharacterStats.SetGlobalDifficulty</c> is the applier.</para>
    ///
    /// <para><b>Which cells actually do something, and which are carried for the record.</b> A port that
    /// assigns a field nothing reads has changed nothing (project rule: "a field with no consumer is not
    /// done"), so the applier writes only the cells whose destination exists and is read in this port:</para>
    /// <list type="table">
    /// <item><term>applied</term><description><see cref="OrganMult"/> (organ damage scale,
    /// <c>CharacterStats.organMult</c>), <see cref="DieDamage"/> (fatal fraction,
    /// <c>CharacterStats.dieDamage</c>), <see cref="TeleMana"/> (telekinesis organ upkeep,
    /// <c>CharacterStats.teleMana</c>), <see cref="DifCapsMult"/> (the loot money/bits multiplier,
    /// <c>LootGen.as:245-252</c>).</description></item>
    /// <item><term>spec only</term><description><see cref="BegHp"/>, <see cref="LvlHp"/>,
    /// <see cref="CritHeal"/>, the five <c>Price*</c> values and <see cref="HimBadDif"/>. The oracle reads
    /// each of them (organ init, level-up, healing, med vendors, addiction) but the port has no destination
    /// field for any of them yet, so they are carried here so that the day a consumer lands the value is
    /// already correct rather than re-derived from a guess.</description></item>
    /// <item><term>not reproduced</term><description><see cref="PetRes"/> — the oracle assigns it per
    /// difficulty and then <c>defaultParams()</c> overwrites it with the literal 30 on every
    /// <c>setParameters()</c> (<c>Pers.as:861</c>), so the effective value is always 30. The cell is
    /// recorded but never applied; see <c>docs/DifficultyModel/05_VERIFICATION_AND_QUIRKS.md</c> Q4.
    /// <c>neujazMax</c> and <c>bonusHealMult</c> are set by levels 3 and 4 only and are omitted entirely:
    /// neither field exists in the port, so transcribing them would be transcribing dead data twice
    /// over.</description></item>
    /// </list>
    /// </summary>
    public readonly struct PersDifficultyTable
    {
        /// <summary>The difficulty index this row was built for, 0..4.</summary>
        public readonly int Index;

        /// <summary>AS3 <c>begHP</c> — starting organ HP (<c>Pers.as:763,778,793,808,825</c>). Spec only.</summary>
        public readonly int BegHp;

        /// <summary>AS3 <c>lvlHP</c> — HP gained per level. Spec only.</summary>
        public readonly int LvlHp;

        /// <summary>AS3 <c>organMult</c> — the organ wound multiplier. <b>Applied.</b></summary>
        public readonly float OrganMult;

        /// <summary>AS3 <c>dieDamage</c> — the fatal fraction of max organ HP. <b>Applied.</b></summary>
        public readonly float DieDamage;

        /// <summary>AS3 <c>critHeal</c> — the critical-heal fraction. Spec only.</summary>
        public readonly float CritHeal;

        /// <summary>AS3 <c>teleMana</c> — the telekinesis upkeep. <b>Applied.</b></summary>
        public readonly float TeleMana;

        /// <summary>AS3 <c>priceBlood</c>. Spec only.</summary>
        public readonly float PriceBlood;

        /// <summary>AS3 <c>priceOrgan</c>. Spec only.</summary>
        public readonly float PriceOrgan;

        /// <summary>AS3 <c>priceRad</c>. Spec only.</summary>
        public readonly float PriceRad;

        /// <summary>AS3 <c>pricePoison</c>. Spec only.</summary>
        public readonly float PricePoison;

        /// <summary>AS3 <c>priceCut</c>. Spec only.</summary>
        public readonly float PriceCut;

        /// <summary>
        /// AS3 <c>difCapsMult</c> — the difficulty term on loot money and bits
        /// (<c>LootGen.as:245-252</c>). <b>Applied.</b> It is <b>1 at every level except 4</b>, where it is
        /// <b>0.5</b> — Super-hard halves money, it does not scale loot quality.
        /// </summary>
        public readonly float DifCapsMult;

        /// <summary>AS3 <c>himBadDif</c> — chem/addiction severity. Spec only.</summary>
        public readonly float HimBadDif;

        /// <summary>
        /// AS3 <c>petRes</c> as the table assigns it (20/20/30/60/90) — <b>recorded, never applied</b>,
        /// because the oracle's own <c>defaultParams()</c> clobbers it to 30 on every call. See the type
        /// remarks and Q4.
        /// </summary>
        public readonly float PetRes;

        public PersDifficultyTable(
            int index, int begHp, int lvlHp, float organMult, float dieDamage, float critHeal,
            float teleMana, float priceBlood, float priceOrgan, float priceRad, float pricePoison,
            float priceCut, float difCapsMult, float himBadDif, float petRes)
        {
            Index = index;
            BegHp = begHp;
            LvlHp = lvlHp;
            OrganMult = organMult;
            DieDamage = dieDamage;
            CritHeal = critHeal;
            TeleMana = teleMana;
            PriceBlood = priceBlood;
            PriceOrgan = priceOrgan;
            PriceRad = priceRad;
            PricePoison = pricePoison;
            PriceCut = priceCut;
            DifCapsMult = difCapsMult;
            HimBadDif = himBadDif;
            PetRes = petRes;
        }
    }

    /// <summary>
    /// The pure half of the difficulty model: every rule that can be stated as a function of the
    /// setting/land/level, with no engine types, so it is assertable offline.
    ///
    /// <para><b>Why this exists separately from the appliers.</b> The oracle states these rules inside
    /// engine objects (<c>Land.setLocDif</c>, the <c>Unit</c> constructor, <c>Location.createObj</c>,
    /// <c>LootGen</c>), and the port's appliers live in MonoBehaviours and room-streaming code that an
    /// offline fixture cannot construct. Putting the arithmetic here means the rule is <i>executed</i> by
    /// the fast loop, and the applier is a one-line call whose only job is to be in the right place.
    /// Every rule names its oracle line.</para>
    /// </summary>
    public static class DifficultyRules
    {
        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  AXIS A — the setting's parameter table (Pers.as:759-839)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Pers.setGlobalDif(param1:int = 2)</c> (<c>Pers.as:759-839</c>), as a value.
        ///
        /// <para><b>The oracle does not clamp here</b> — <c>changeDif</c> clamps before calling
        /// (<c>Game.as:318-325</c>). Pass an already-clamped 0..4; this method clamps anyway so a caller
        /// that skipped it cannot read a row that does not exist. Note the oracle has <b>no else
        /// branch</b>: an index outside 0..4 assigns nothing at all and leaves the previous table in
        /// place, which is a different behaviour from "use Normal" — the clamp here is the port's
        /// defensive choice, recorded rather than hidden.</para>
        /// </summary>
        public static PersDifficultyTable PersTable(int globalDif)
        {
            switch (GlobalDifficulty.Clamp(globalDif))
            {
                case 0:
                    return new PersDifficultyTable(
                        index: 0, begHp: 200, lvlHp: 25, organMult: 0.2f, dieDamage: 0f, critHeal: 0.2f,
                        teleMana: 0f, priceBlood: 0.5f, priceOrgan: 0.5f, priceRad: 1f,
                        pricePoison: 4f, priceCut: 4f, difCapsMult: 1f, himBadDif: 1f, petRes: 20f);

                case 1:
                    return new PersDifficultyTable(
                        index: 1, begHp: 150, lvlHp: 25, organMult: 0.5f, dieDamage: 0f, critHeal: 0.2f,
                        teleMana: 0f, priceBlood: 0.5f, priceOrgan: 0.5f, priceRad: 1f,
                        pricePoison: 4f, priceCut: 4f, difCapsMult: 1f, himBadDif: 1f, petRes: 20f);

                case 3:
                    return new PersDifficultyTable(
                        index: 3, begHp: 70, lvlHp: 15, organMult: 1f, dieDamage: 0.25f, critHeal: 0.1f,
                        teleMana: 1f, priceBlood: 1f, priceOrgan: 1f, priceRad: 1.5f,
                        pricePoison: 6f, priceCut: 6f, difCapsMult: 1f, himBadDif: 2f, petRes: 60f);

                case 4:
                    return new PersDifficultyTable(
                        index: 4, begHp: 40, lvlHp: 10, organMult: 1f, dieDamage: 0.35f, critHeal: 0.1f,
                        teleMana: 1f, priceBlood: 1f, priceOrgan: 1f, priceRad: 1.5f,
                        pricePoison: 6f, priceCut: 6f, difCapsMult: 0.5f, himBadDif: 2.5f, petRes: 90f);

                default: // 2 — the oracle's default row
                    return new PersDifficultyTable(
                        index: 2, begHp: 100, lvlHp: 20, organMult: 1f, dieDamage: 0.15f, critHeal: 0.2f,
                        teleMana: 0.5f, priceBlood: 0.5f, priceOrgan: 0.5f, priceRad: 1f,
                        pricePoison: 4f, priceCut: 4f, difCapsMult: 1f, himBadDif: 1.3f, petRes: 30f);
            }
        }

        /// <summary>
        /// The effective <c>petRes</c> — always the literal <b>30</b>, at every difficulty, because
        /// <c>defaultParams()</c> overwrites the table's value on every <c>setParameters()</c>
        /// (<c>Pers.as:861</c>). Named so the constant is a decision with a citation rather than a
        /// coincidence of the field initialiser.
        /// </summary>
        public const float EffectivePetRes = 30f;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  AXIS A — the three remaining gameplay readers
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Unit</c>'s constructor HP scaling (<c>Unit.as:1130-1141</c>): a <b>non-player</b> unit at
        /// <c>globalDif &lt;= 1</c> has its max HP multiplied by <b>0.4</b> (level 0) or <b>0.7</b>
        /// (level 1), and everything else is <b>×1</b>.
        ///
        /// <para><b>Two arms, not a range.</b> The oracle's outer guard is <c>&lt;= 1</c> while the two
        /// arms are <c>== 0</c> / <c>== 1</c>, so writing <c>&lt;= 1 → ×0.4</c> or <c>&gt;= 2 → ×1.0</c>
        /// collapses them and is wrong at level 1. Asserted by the fixture.</para>
        /// </summary>
        public static float EnemyHpMultiplier(int globalDif)
        {
            if (globalDif == 0) return 0.4f;
            if (globalDif == 1) return 0.7f;
            return 1f;
        }

        /// <summary>
        /// The outer half of the same guard — <c>this.fraction != F_PLAYER &amp;&amp; globalDif &lt;= 1</c>
        /// (<c>Unit.as:1130</c>). Both halves are load-bearing: the player's HP is never scaled, at any
        /// difficulty.
        /// </summary>
        public static bool ScalesUnitHp(int globalDif, bool isPlayer) => !isPlayer && globalDif <= 1;

        /// <summary>
        /// AS3 <c>Unit.isNoResBoss()</c> (<c>Unit.as:1743-1754</c>):
        /// <c>globalDif &lt;= 3 &amp;&amp; loc &amp;&amp; loc.land.act.tip != "base"</c>.
        ///
        /// <para>The oracle wraps the expression in a <c>try</c>/<c>catch</c> that answers <c>false</c>,
        /// which is how a unit with no location behaves — modelled here as
        /// <paramref name="hasLocation"/> rather than as an exception.</para>
        ///
        /// <para><b>DEFERRED — no consumer, and that is the finding rather than an omission.</b> The
        /// oracle has exactly three readers, all on the death path, and none of them is ported:</para>
        /// <list type="bullet">
        /// <item><description><c>Unit.setNull</c> (<c>Unit.as:1700</c>):
        /// <c>if(this.boss &amp;&amp; this.isNoResBoss()) param1 = false;</c> — the corpse is not removed.
        /// The port's <c>UnitController.OnDeath</c> has no removal parameter at all ("Base class doesn't
        /// destroy the GameObject"), so there is nothing for this to gate.</description></item>
        /// <item><description><c>UnitBossAlicorn.setNull</c> (<c>:204</c>): <c>if(!isNoResBoss())
        /// this.isShit = true;</c> — the alicorn's shield breaks on death. The port's
        /// <c>BossAlicornController</c> documents that the play-once dome driver is missing
        /// (<c>UnitController</c>'s <c>UsesBossShieldOverlay</c> remarks).</description></item>
        /// <item><description><c>UnitBossNecr.setNull</c> (<c>:239</c>): <c>if(isNoResBoss())
        /// param1 = false;</c> — same removal gate as the base, and the port has no
        /// <c>setNull</c>.</description></item>
        /// </list>
        /// <para>The predicate is pinned by a fixture so the rule cannot drift, and must be wired the day
        /// <c>setNull</c> (or the alicorn's death dome) lands. Adding a reader now would be inventing a
        /// death-removal model the port does not have — the same call as D10.</para>
        /// </summary>
        public static bool IsNoResBoss(int globalDif, bool hasLocation, string landTip)
        {
            if (!hasLocation) return false;
            if (globalDif > 3) return false;
            return !string.Equals(landTip, "base", StringComparison.OrdinalIgnoreCase);
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  AXIS B — the land/run difficulty inputs (Game.as:353-361, Land.as:120-140)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3's <c>param3</c> — the <b>run</b> difficulty the land is built at
        /// (<c>Game.as:353-361</c>, inside <c>enterToCurLand</c>).
        ///
        /// <code>
        /// _loc2_ = 0;
        /// if(this.triggers["firstroom"] > 0) { _loc2_ = 1; if(pers.level > 1) _loc2_ = pers.level - 1; }
        /// </code>
        ///
        /// <para><b>It is a function of the player's LEVEL, not of <c>globalDif</c>.</b> That is the
        /// single easiest thing to get wrong about this model. <c>firstroom</c> is set to 1 at the end of
        /// the <i>first</i> land entry (<c>Game.as:365-368</c>), so the first land is always built at 0
        /// and every later one at <c>max(1, level-1)</c>.</para>
        /// </summary>
        public static int RunDifficulty(bool firstRoomSeen, int playerLevel)
        {
            if (!firstRoomSeen) return 0;
            return playerLevel > 1 ? playerLevel - 1 : 1;
        }

        /// <summary>
        /// AS3 <c>Land.landDifLevel</c> (<c>Land.as:118-138</c>) — the land's difficulty floor.
        ///
        /// <code>
        /// if(rnd) { landDifLevel = param3; if(landDifLevel &lt; act.dif) landDifLevel = act.dif; }
        /// else    { landDifLevel = act.dif; if(act.autoLevel) landDifLevel = param3; }
        /// </code>
        ///
        /// <para><b>The two branches differ in an easy-to-miss way.</b> A procedural land takes
        /// <c>max(run, act.dif)</c> — the authored difficulty is a <i>floor</i>. An authored land takes
        /// <c>act.dif</c>, and <c>autoLevel</c> makes it take <c>run</c> <b>instead</b> (not
        /// <c>max</c>) — so an <c>autoLevel</c> land can be built <i>below</i> its authored difficulty.
        /// The port imports <c>autoLevel</c> and previously ignored it, which froze such a land at its
        /// authored value forever.</para>
        /// </summary>
        public static float LandDifficulty(bool procedural, float actDif, int runDifficulty, bool autoLevel)
        {
            if (procedural)
            {
                return runDifficulty < actDif ? actDif : runDifficulty;
            }

            return autoLevel ? runDifficulty : actDif;
        }

        /// <summary>
        /// AS3 <c>Land.lootLimit</c> (<c>Land.as:139</c>): <c>param3 + 3</c>, using the <b>raw run
        /// difficulty</b>, not the floored <c>landDifLevel</c>.
        ///
        /// <para><b>These differ, and the difference is the oracle's.</b> On a procedural land with
        /// <c>act.dif = 8</c> and <c>run = 2</c>, <c>landDifLevel</c> is 8 while <c>lootLimit</c> is 5 —
        /// the loot cap sits <i>below</i> the difficulty. Reproduce it; do not "correct" it to
        /// <c>landDifLevel + 3</c> (Q2).</para>
        /// </summary>
        public static int LootLimit(int runDifficulty) => runDifficulty + 3;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  AXIS A — the checkpoint rule (Location.as:2040-2046)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Location.createObj</c>'s checkpoint branch (<c>Location.as:2043</c>):
        /// <c>globalDif &lt;= 1 || (land.rnd &amp;&amp; globalDif == 2 &amp;&amp; Math.random() &lt; 0.33)</c>
        /// makes <c>teleOn = true</c>.
        /// </summary>
        /// <param name="roll">
        /// The 0..1 draw. <b>Only consulted when the guard reaches it</b> — see
        /// <see cref="CheckpointRollsForTeleOn"/>, which the caller must test <i>before</i> drawing, or
        /// every authored land shifts the shared stream.
        /// </param>
        public static bool CheckpointTeleOn(int globalDif, bool landRnd, float roll)
        {
            if (globalDif <= 1) return true;
            return landRnd && globalDif == 2 && roll < 0.33f;
        }

        /// <summary>
        /// Whether <see cref="CheckpointTeleOn"/>'s <c>&amp;&amp;</c> chain reaches its random draw. The
        /// oracle short-circuits, so a caller that draws unconditionally spends one extra value from the
        /// shared stream on every checkpoint in an authored land or at a difficulty other than 2.
        /// </summary>
        public static bool CheckpointRollsForTeleOn(int globalDif, bool landRnd) => globalDif == 2 && landRnd;
    }
}
