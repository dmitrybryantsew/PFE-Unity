namespace PFE.Systems.Magic
{
    /// <summary>
    /// What a spell needs from the unit and the world around it — the seam that keeps
    /// <see cref="Spell"/> free of Unity types so the cast prologue is offline-testable.
    ///
    /// <para><b>Why an interface and not a reference to the live unit.</b> AS3's <c>Spell</c> holds
    /// <c>owner:Unit</c> and reaches straight through it: <c>this.owner.mana</c>,
    /// <c>World.w.pers.allDManaMult</c>, <c>this.owner.loc.isLine(…)</c>. The port cannot name those
    /// types here without pulling a <c>MonoBehaviour</c> into the class, and every fixture that then
    /// tried to drive a cast would die on the <c>ECall</c> wall the way <c>MagicManaTests</c> did.
    /// Same shape as <see cref="PFE.Systems.Effects.IEffectHost"/> and
    /// <see cref="PFE.Systems.Effects.IEffectDefinitionResolver"/>: one adapter bridges to the live
    /// unit, a plain fake drives the tests, and the <b>production</b> code path is what runs in both.
    /// </para>
    ///
    /// <para><b>Every member names its oracle site</b>, so a missing adapter is traceable rather than
    /// vague.</para>
    /// </summary>
    public interface ISpellHost
    {
        // ---- the player-only gate block (Spell.as:191-220) ------------------------------------

        /// <summary>
        /// AS3 <c>Spell.player</c> (<c>Spell.as:76-81</c>): true when the owner is the player. The
        /// whole first gate block is inside <c>if(this.player)</c>, so an NPC caster skips every check
        /// below — including the mana cost — and goes straight to the effect. Reproduced, not
        /// "corrected".
        /// </summary>
        bool IsPlayer { get; }

        /// <summary>AS3 <c>gg.rat</c> (<c>UnitPlayer.as:311</c>). Non-zero refuses every spell.</summary>
        int Rat { get; }

        /// <summary>AS3 <c>World.w.alicorn</c> — the alicorn-mode flag.</summary>
        bool Alicorn { get; }

        /// <summary>
        /// AS3 <c>World.w.pers.spellsPoss</c> (<c>Pers.as:423</c>) — the mana-trauma permission flag.
        /// 0 refuses. Shares its oracle with <c>HitAvoidance.CanCastSpells</c>, which gates the
        /// <i>assault</i> path; this is the supportive twin.
        /// </summary>
        int SpellsPossible { get; }

        /// <summary>
        /// AS3 <c>gg.atkPoss</c> (<c>UnitPlayer.as:205</c>) — a <b>second</b> permission flag, distinct
        /// from <see cref="SpellsPossible"/>, zeroed by <c>&lt;sk id='atkPoss' v1='0'/&gt;</c>. Only
        /// consulted when the spell itself carries <c>atk='1'</c> (<c>Spell.as:203</c>), which is 7 of
        /// the 9 rows.
        /// </summary>
        bool AtkPossible { get; }

        /// <summary>
        /// AS3 <c>gg.invent.weapons[id].respect == 1</c> (<c>Spell.as:198</c>). <b>Weapon-instance</b>
        /// state, looked up by the <i>spell's</i> id — the same id the nine bare
        /// <c>&lt;weapon tip='5' spell='1'/&gt;</c> rows carry. Currently unreachable on an equipped
        /// weapon (<c>GameInventory.ToggleWeaponRespect</c> unequips a hidden weapon), exactly as
        /// <c>MagicWeaponController</c>'s identical gate is.
        /// </summary>
        bool WeaponRespect(string spellId);

        // ---- mana (Spell.as:222-234, and the spend at :281-283) -------------------------------

        /// <summary>AS3 <c>owner.mana</c> — the resource the cast <i>checks</i> against.</summary>
        float Mana { get; }

        /// <summary>
        /// AS3 <c>World.w.pers.manaHP</c> (<c>Pers.as:141</c>) — the mana-organ's own HP pool, which
        /// the cast's second check compares the cost against. Not the unit's mana.
        /// </summary>
        float ManaHp { get; }

        /// <summary>AS3 <c>World.w.pers.allDManaMult</c> (<c>Pers.as:227</c>, default 1).</summary>
        float AllDManaMult { get; }

        /// <summary>
        /// AS3 <c>gg.pers.warlockDManaMult</c> (<c>Pers.as:323</c>, default 1). Read <b>only</b> at the
        /// spend (<c>Spell.as:281-282</c>), never in the check — see
        /// <see cref="SpellCastRules.ManaSpend"/> for why that asymmetry matters.
        /// </summary>
        float WarlockDManaMult { get; }

        /// <summary>AS3 <c>gg.pers.spellDown</c> (<c>Pers.as:427</c>, default 1) — cooldown scaling.</summary>
        float SpellDown { get; }

        /// <summary>AS3 <c>mana -= amount</c> (inside <c>UnitPlayer.manaSpell</c>, <c>:1712</c>).</summary>
        void SpendMana(float amount);

        /// <summary>
        /// AS3 <c>pers.manaDamage(amount)</c> (<c>UnitPlayer.as:1713</c>) — the mana organ takes its own
        /// damage, which is what can eventually zero <see cref="SpellsPossible"/>.
        /// </summary>
        void DamageManaOrgan(float amount);

        // ---- placement (Spell.as:238-249) -----------------------------------------------------

        /// <summary>AS3 <c>owner.magicX</c> (<c>Unit.as:340</c>, set at <c>:3292</c> to <c>X</c>).</summary>
        float MagicX { get; }

        /// <summary>AS3 <c>owner.magicY</c> (<c>Unit.as:342</c>, set at <c>:3293</c> to <c>Y - scY*0.5</c>).</summary>
        float MagicY { get; }

        /// <summary>AS3 <c>owner.spellPower</c> (<c>Unit.as:316</c>, default 1).</summary>
        float SpellPower { get; }

        /// <summary>
        /// AS3 <c>gg.pers.telePower</c> (<c>Pers.as:247</c>, default 1) — replaces
        /// <see cref="SpellPower"/> for a player casting a <c>tele</c> spell (<c>Spell.as:246-249</c>).
        /// </summary>
        float TelePower { get; }

        /// <summary>
        /// AS3 <c>owner.loc.isLine(X, Y, cx, cy)</c> (<c>Location.as:2368</c>). Only consulted when the
        /// spell carries <c>line='1'</c>.
        /// </summary>
        bool IsLineVisible(float fromX, float fromY, float toX, float toY);

        // ---- effect surface (only what the ported effects need) -------------------------------

        /// <summary>
        /// AS3 <c>owner.addEffect(id, value)</c>. Backed by the existing
        /// <c>ActiveEffectSet.AddEffect(string, float, int, bool)</c>, so this is an adapter over a
        /// seam that already exists rather than a new one.
        /// </summary>
        void AddEffect(string effectId, float value);

        /// <summary>
        /// AS3 <c>owner.shithp = value</c> (<c>Spell.as:309-316</c>, the <c>shithp</c> shield). <b>Not
        /// yet a field on the port's unit</b> — see the note on <see cref="Spell"/>'s
        /// <c>sp_mshit</c> handling.
        /// </summary>
        void SetShitHp(float value);

        /// <summary>AS3 <c>World.w.pers.alicornShitHP</c> (<c>Pers.as:467</c>, default 2000).</summary>
        float AlicornShitHp { get; }

        /// <summary>
        /// AS3 <c>gg.t_cryst</c> (<c>UnitPlayer.as:147</c>). Read and written by <c>cast_cryst</c>
        /// (<c>Spell.as:323-331</c>): <c>&gt; 0</c> downgrades the cast to <c>est = 2</c>, then it is
        /// set to 5 regardless.
        /// </summary>
        int CrystalCooldown { get; set; }

        /// <summary>
        /// AS3 <c>Snd.ps(id, x, y)</c>. Called on success when the row carries <c>snd</c> (all nine
        /// do), and as the <c>"nomagic"</c> refusal cue.
        /// </summary>
        void PlaySound(string soundId, float x, float y);

        // ---- refusal feedback (Spell.as:202-239) ----------------------------------------------

        /// <summary>
        /// AS3 <c>World.w.gui.infoText(key, null, null, false)</c> — a localisation key with no number,
        /// e.g. <c>"disSpell"</c>, <c>"noSpells"</c>, <c>"overMana"</c>, <c>"noMana"</c>,
        /// <c>"noVisible"</c>.
        ///
        /// <para><b>Two overloads, not a nullable, because AS3 passes <c>null</c> as the number</b> and
        /// the one caller that does pass one (<c>"spellCuld"</c>, <c>:220</c>) passes a rounded
        /// seconds value. Keeping them separate makes "this message has a number" a compile-time fact.
        /// </para>
        /// </summary>
        void ShowInfoText(string key);

        /// <summary>AS3 <c>World.w.gui.infoText("spellCuld", Math.ceil(t_culd / World.fps), …)</c> (<c>:220</c>).</summary>
        void ShowInfoText(string key, float number);

        /// <summary>AS3 <c>World.w.gui.bulb(x, y)</c> — the on-screen attention marker.</summary>
        void ShowBulb(float x, float y);
    }
}
