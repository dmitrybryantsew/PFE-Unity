using System;

namespace PFE.Systems.Magic
{
    /// <summary>
    /// Which check refused a cast, in the oracle's own order. <see cref="None"/> means the cast may
    /// proceed. The order is load-bearing: AS3's <c>Spell.cast()</c> returns at the first failing
    /// check, so a spell that is both out of range and out of mana reports the <i>earlier</i> one, and
    /// only the earlier one's feedback fires.
    /// </summary>
    public enum SpellCastRefusal
    {
        /// <summary>No check refused the cast.</summary>
        None = 0,

        /// <summary><c>Spell.as:186-189</c> — <c>cf == null</c>: the id matched no <c>cast_*</c> function.</summary>
        NoEffect,

        /// <summary><c>:193-196</c> — alicorn mode refuses every spell except <c>sp_mshit</c>.</summary>
        Alicorn,

        /// <summary><c>:197-200</c> — <c>gg.rat &gt; 0</c>.</summary>
        Rat,

        /// <summary><c>:201-206</c> — the matching spell-weapon is <c>respect == 1</c>.</summary>
        WeaponRespect,

        /// <summary><c>:207-213</c> — <c>spellsPoss == 0</c>, or an <c>atk</c> spell with <c>atkPoss</c> false.</summary>
        NoSpells,

        /// <summary><c>:214-226</c> — <c>t_culd &gt; 0</c>.</summary>
        Cooldown,

        /// <summary><c>:229-234</c> — <c>owner.mana &lt; dmagic</c>.</summary>
        OverMana,

        /// <summary><c>:235-240</c> — <c>dmana &gt; pers.manaHP</c>: the mana organ cannot absorb it.</summary>
        NoMana,

        /// <summary><c>:262-269</c> — <c>line == 1</c> and the target is not visible.</summary>
        NoLineOfSight,

        /// <summary>
        /// <c>:292-296</c> — the effect ran but set <c>est = 0</c>, which means "refuse and charge
        /// nothing". Only <c>cast_gwall</c> and <c>cast_invulner</c> can do this.
        /// </summary>
        EffectRefused,

        /// <summary>
        /// <b>No AS3 counterpart — a port-progress marker, deliberately last.</b> The id is one of the
        /// nine and AS3 has a <c>cast_*</c> function for it, but the port has not implemented that
        /// effect yet, so the cast is refused rather than silently doing nothing.
        ///
        /// <para>Distinct from <see cref="NoEffect"/> on purpose: <c>NoEffect</c> means "this id is not
        /// a spell at all" (<c>cf == null</c>), which is a data fault; this means "the port is
        /// incomplete", which is a known gap. Collapsing them would make the port's remaining work
        /// indistinguishable from bad data in any log.</para>
        /// </summary>
        EffectNotPorted,
    }

    /// <summary>
    /// Everything <see cref="SpellCastRules.Evaluate"/> needs, as plain values. Deliberately a struct of
    /// primitives with <b>no host and no Unity type</b>, so the whole gate order is offline-testable.
    ///
    /// <para>The fields mirror the oracle's reads one-for-one; where a field is only consulted on one
    /// branch that is stated, because "the check never runs" is exactly the kind of fact a later
    /// refactor deletes.</para>
    /// </summary>
    public readonly struct SpellCastContext
    {
        // --- the cf == null guard ---
        /// <summary><c>this.cf != null</c> — the spell id resolved to an effect function.</summary>
        public readonly bool HasEffect;

        // --- the player-only block ---
        /// <summary><c>this.player</c>. False skips the entire block below, including the mana cost.</summary>
        public readonly bool IsPlayer;
        /// <summary>The spell's id; only read for the <c>sp_mshit</c> alicorn exemption.</summary>
        public readonly string SpellId;
        /// <summary>The row's <c>atk='1'</c> flag — gates the <c>atkPoss</c> half of the check.</summary>
        public readonly bool Atk;
        /// <summary><c>World.w.alicorn</c>.</summary>
        public readonly bool Alicorn;
        /// <summary><c>gg.rat</c>.</summary>
        public readonly int Rat;
        /// <summary>The spell-weapon's <c>respect == 1</c>. Precomputed by the host.</summary>
        public readonly bool WeaponRespect;
        /// <summary><c>pers.spellsPoss</c>.</summary>
        public readonly int SpellsPossible;
        /// <summary><c>gg.atkPoss</c>.</summary>
        public readonly bool AtkPossible;
        /// <summary><c>t_culd</c>, in frames.</summary>
        public readonly int CooldownTicks;
        /// <summary><c>owner.mana</c>.</summary>
        public readonly float OwnerMana;
        /// <summary><c>pers.manaHP</c>.</summary>
        public readonly float ManaHp;
        /// <summary>The row's <c>magic</c> attribute.</summary>
        public readonly float Magic;
        /// <summary>The row's <c>mana</c> attribute.</summary>
        public readonly float Mana;
        /// <summary><c>pers.allDManaMult</c>.</summary>
        public readonly float AllDManaMult;

        // --- the line-of-sight check (runs for player AND npc) ---
        /// <summary>The row's <c>line='1'</c> flag.</summary>
        public readonly bool Line;
        /// <summary><c>this.owner != null</c> — the LOS test is skipped when there is no owner.</summary>
        public readonly bool HasOwner;
        /// <summary>The host's answer to <c>loc.isLine(X, Y, cx, cy)</c>, precomputed.</summary>
        public readonly bool LineOfSight;

        public SpellCastContext(
            bool hasEffect,
            bool isPlayer,
            string spellId,
            bool atk,
            bool alicorn,
            int rat,
            bool weaponRespect,
            int spellsPossible,
            bool atkPossible,
            int cooldownTicks,
            float ownerMana,
            float manaHp,
            float magic,
            float mana,
            float allDManaMult,
            bool line,
            bool hasOwner,
            bool lineOfSight)
        {
            HasEffect       = hasEffect;
            IsPlayer        = isPlayer;
            SpellId         = spellId;
            Atk             = atk;
            Alicorn         = alicorn;
            Rat             = rat;
            WeaponRespect   = weaponRespect;
            SpellsPossible  = spellsPossible;
            AtkPossible     = atkPossible;
            CooldownTicks   = cooldownTicks;
            OwnerMana       = ownerMana;
            ManaHp          = manaHp;
            Magic           = magic;
            Mana            = mana;
            AllDManaMult    = allDManaMult;
            Line            = line;
            HasOwner        = hasOwner;
            LineOfSight     = lineOfSight;
        }
    }

    /// <summary>
    /// The pure half of <c>Spell.cast()</c> — the port of the prologue (<c>Spell.as:184-269</c>), the
    /// mana spend (<c>:281-283</c> with <c>UnitPlayer.manaSpell</c> at <c>:1710-1719</c>) and the
    /// cooldown stamp (<c>:282</c>).
    ///
    /// <para><b>Why this is a separate pure type.</b> Same reason as <c>HitAvoidance</c> and
    /// <c>WeaponSpreadMath</c>: the whole cast path is otherwise reachable only from a live unit, and
    /// anything that constructs one dies on the <c>ECall</c> wall — <c>MagicManaTests</c> could not run
    /// offline for exactly that reason until an editor run proved it. Every rule here executes with no
    /// editor and no engine.</para>
    /// </summary>
    public static class SpellCastRules
    {
        /// <summary>
        /// AS3 <c>Spell.as:228-231</c>: <c>if(this.dmagic &gt; 999) this.dmagic = 999;</c> — a literal
        /// ceiling on the <b>check</b>, not on the spend. See <see cref="ManaSpend"/>.
        /// </summary>
        public const float MaxMagicCheck = 999f;

        /// <summary>
        /// AS3 <c>Spell.as:214</c> gates on <c>t_culd &gt; 0</c>, and <c>:220</c> only shows the
        /// cooldown feedback when <c>culd &gt;= 100</c> (about 3.3 s at 30 fps).
        /// </summary>
        public const int CooldownFeedbackThreshold = 100;

        /// <summary>
        /// The ordered gate pass, exactly as <c>Spell.cast()</c> runs it.
        ///
        /// <para><b>Order is the whole point.</b> Each check returns immediately in the oracle, so the
        /// first refusal wins. Two consequences worth stating because they are easy to "tidy" away:
        /// the mana cost is never even computed for an NPC (<c>:222-240</c> is inside
        /// <c>if(this.player)</c>), and the line-of-sight test runs for NPCs too (<c>:262</c> is
        /// outside it) — only its feedback is player-only.</para>
        /// </summary>
        public static SpellCastRefusal Evaluate(in SpellCastContext ctx)
        {
            if (!ctx.HasEffect) return SpellCastRefusal.NoEffect;

            if (ctx.IsPlayer)
            {
                // `World.w.alicorn && this.id != "sp_mshit"` — the exemption is a literal id compare.
                if (ctx.Alicorn && ctx.SpellId != "sp_mshit") return SpellCastRefusal.Alicorn;
                if (ctx.Rat > 0) return SpellCastRefusal.Rat;
                if (ctx.WeaponRespect) return SpellCastRefusal.WeaponRespect;

                // `spellsPoss == 0 || this.atk && !this.gg.atkPoss` — note the `atk` flag is read off
                // the SPELL, not off any equipped weapon.
                if (ctx.SpellsPossible == 0 || (ctx.Atk && !ctx.AtkPossible))
                    return SpellCastRefusal.NoSpells;

                if (ctx.CooldownTicks > 0) return SpellCastRefusal.Cooldown;

                if (ctx.OwnerMana < CheckMagicCost(ctx.Magic, ctx.AllDManaMult))
                    return SpellCastRefusal.OverMana;

                if (CheckManaCost(ctx.Mana, ctx.AllDManaMult) > ctx.ManaHp)
                    return SpellCastRefusal.NoMana;
            }

            // Outside the player block: an NPC's line spells are refused too.
            if (ctx.Line && ctx.HasOwner && !ctx.LineOfSight) return SpellCastRefusal.NoLineOfSight;

            return SpellCastRefusal.None;
        }

        /// <summary>
        /// AS3 <c>this.dmagic = this.magic * World.w.pers.allDManaMult;</c> followed by the 999 clamp
        /// (<c>Spell.as:222-231</c>). This is the value compared against <c>owner.mana</c>.
        /// </summary>
        public static float CheckMagicCost(float magic, float allDManaMult)
        {
            float d = magic * allDManaMult;
            return d > MaxMagicCheck ? MaxMagicCheck : d;
        }

        /// <summary>
        /// AS3 <c>this.dmana = this.mana * World.w.pers.allDManaMult;</c> (<c>Spell.as:232</c>) — the
        /// second component of the cost, compared against the mana organ's own HP. <b>Not clamped</b>;
        /// the 999 ceiling is applied to <c>dmagic</c> alone.
        /// </summary>
        public static float CheckManaCost(float mana, float allDManaMult) => mana * allDManaMult;

        /// <summary>
        /// What the cast actually costs, once it is allowed to happen.
        ///
        /// <para><b>The check and the spend use different multipliers, and this is a real oracle
        /// quirk, not a transcription slip.</b> The check is
        /// <c>magic * allDManaMult</c> clamped to 999 (<c>:222-231</c>). The spend calls
        /// <c>manaSpell(this.magic * this.gg.pers.warlockDManaMult, …)</c> (<c>:281</c>), and
        /// <c>manaSpell</c> multiplies its argument by <c>allDManaMult</c> <i>again</i>
        /// (<c>UnitPlayer.as:1712</c>) — so the mana removed is
        /// <c>magic * warlockDManaMult * allDManaMult</c>, unclamped.</para>
        ///
        /// <para>With both multipliers at their defaults of 1 the two agree, which is why this is
        /// invisible until a perk raises one. It is reproduced rather than unified: making the spend
        /// match the check would change real behaviour for a warlock.</para>
        /// </summary>
        public static float ManaSpend(float magic, float warlockDManaMult, float allDManaMult)
            => magic * warlockDManaMult * allDManaMult;

        /// <summary>
        /// AS3 <c>this.t_culd = Math.round(this.culd * this.gg.pers.spellDown);</c> (<c>Spell.as:282</c>).
        ///
        /// <para><b><c>Math.floor(x + 0.5)</c>, not <c>Math.Round</c>.</b> AS3's <c>Math.round</c>
        /// breaks a tie <i>upward</i> (toward +Infinity), so <c>Math.round(2.5) == 3</c> and
        /// <c>Math.round(-2.5) == -2</c>. .NET's <c>Math.Round</c> defaults to banker's rounding and
        /// would give 2 and -2. A <c>culd</c> that lands on a half-frame is reachable — the attribute is
        /// an arbitrary number — so the two differ in practice.</para>
        /// </summary>
        public static int CooldownAfterCast(float culd, float spellDown)
            => (int)Math.Floor(culd * spellDown + 0.5);

        /// <summary>
        /// AS3 <c>Spell.as:270-279</c>: with <c>dist &gt; 0</c>, a target beyond range is pulled back
        /// onto the range circle, <b>along the same bearing</b>.
        ///
        /// <para>Faithful in three details that a "clean" rewrite tends to lose:</para>
        /// <list type="bullet">
        /// <item><description>the test is on the <b>squared</b> distance against <c>dist * dist</c>, so
        /// no square root is taken for an in-range target;</description></item>
        /// <item><description>only the <b>target</b> moves — the origin is untouched;</description></item>
        /// <item><description>the arithmetic is AS3 <c>Number</c> (double), and the division is by the
        /// <b>true</b> length, so the result lands exactly on the circle rather than near it. Computed
        /// in double here for the same reason, with the <c>float</c> narrowing only at the end.</description></item>
        /// </list>
        ///
        /// <para><c>dist &lt;= 0</c> returns immediately — the oracle's <c>if(this.dist &gt; 0)</c>
        /// guard, which is also what keeps the division safe (the branch is only entered when the
        /// squared distance exceeds <c>dist * dist</c>, so the length is strictly positive).</para>
        /// </summary>
        public static void ClampToRange(
            float originX, float originY, float dist, ref float targetX, ref float targetY)
        {
            if (dist <= 0f) return;

            double dx = (double)originX - targetX;
            double dy = (double)originY - targetY;
            double squared = dx * dx + dy * dy;
            double d = dist;
            if (squared <= d * d) return;

            double length = Math.Sqrt(squared);
            targetX = (float)(originX - dx * d / length);
            targetY = (float)(originY - dy * d / length);
        }
    }
}
