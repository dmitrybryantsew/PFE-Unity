namespace PFE.Systems.Magic
{
    /// <summary>
    /// The <b>caster's own</b> state and mutations — everything <see cref="Spell"/> reads off AS3's
    /// <c>owner</c> plus the <c>gg</c>/<c>Pers</c> block behind it.
    ///
    /// <para><b>Why this exists instead of a reference to the live unit.</b> <see cref="ISpellHost"/>
    /// was the first cut of the seam, and it has no implementation because its members came from three
    /// different places: the unit's own state (here), the world and the presentation (see
    /// <see cref="ISpellWorld"/>), and the caster's identity (also here). Naming the live types —
    /// <c>CharacterStats</c> is a <c>MonoBehaviour</c> — would make the adapter un-runnable outside the
    /// editor, which is the <c>ECall</c> wall this project keeps paying for. Two narrow interfaces let
    /// the whole mapping be proved offline and leave the live wiring as a mechanical read of public
    /// fields.</para>
    ///
    /// <para><b>Every member names its oracle site</b>, and the live implementer should be able to fill
    /// this in without reading <c>Spell.as</c>.</para>
    /// </summary>
    public interface ISpellCaster
    {
        // ---- identity and permission -----------------------------------------------------------------

        /// <summary>
        /// AS3 <c>Spell.player</c> (<c>Spell.as:76-81</c>): true when the owner is the player. The whole
        /// first gate block is inside <c>if(this.player)</c>, so an NPC caster skips every check below —
        /// including the mana cost — and goes straight to the effect. Reproduced, not "corrected".
        /// </summary>
        bool IsPlayer { get; }

        /// <summary>
        /// AS3 <c>gg.rat</c> (<c>UnitPlayer.as:311</c>, default 0) — the rat-potion transformation
        /// state, set at <c>:4018</c> and cleared at <c>:4055</c>. Non-zero refuses every spell
        /// (<c>Spell.as:193-196</c>) and also silences the Def key
        /// (<c>UnitPlayer.as:2226</c>).
        /// </summary>
        int Rat { get; }

        /// <summary>
        /// AS3 <c>World.w.pers.spellsPoss</c> (<c>Pers.as:423</c>, default 1) — the mana-trauma
        /// permission flag. 0 refuses. The port already owns this as
        /// <c>CharacterStats.spellsPoss</c>, produced by the mana-organ trauma path.
        /// </summary>
        int SpellsPossible { get; }

        /// <summary>
        /// AS3 <c>gg.atkPoss</c> (<c>UnitPlayer.as:205</c>, default 1) — a <b>second</b> permission flag,
        /// distinct from <see cref="SpellsPossible"/>, zeroed by <c>&lt;sk id='atkPoss' v1='0'/&gt;</c>
        /// (<c>AllData.as:6152</c>, <c>:6191</c>). Only consulted when the spell itself carries
        /// <c>atk='1'</c> (<c>Spell.as:203</c>), which is 7 of the 9 rows.
        /// </summary>
        bool AtkPossible { get; }

        /// <summary>
        /// AS3 <c>gg.invent.weapons[id].respect == 1</c> (<c>Spell.as:198</c>) — <b>weapon-instance</b>
        /// state, looked up by the <i>spell's</i> id. The refusal is shared with the selection path:
        /// <c>UnitPlayer.as:3613-3624</c> refuses a hidden spell weapon before ever reaching the cast.
        /// </summary>
        bool WeaponRespect(string spellId);

        // ---- the mana pair ---------------------------------------------------------------------------

        /// <summary>AS3 <c>owner.mana</c> (<c>Unit.as:138</c>) — the regenerating budget the cast <i>checks</i> against.</summary>
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

        /// <summary>
        /// AS3 <c>Pers.spellDown</c> (<c>Pers.as:427</c>, default 1) — scales the cooldown
        /// (<c>Spell.as:284</c>). The port already owns it as <c>CharacterStats.spellDown</c>.
        /// </summary>
        float SpellDown { get; }

        /// <summary>
        /// AS3 <c>Unit.spellPower</c> (<c>Unit.as:316</c>, default 1) — the magnitude multiplier every
        /// effect uses (<c>Spell.as:248</c>). The port owns it as <c>CharacterStats.spellPower</c>.
        /// </summary>
        float SpellPower { get; }

        /// <summary>
        /// AS3 <c>Pers.telePower</c> (<c>Pers.as:247</c>, default 1) — replaces
        /// <see cref="SpellPower"/> for a player casting a <c>tele</c> spell (<c>Spell.as:246-249</c>).
        /// </summary>
        float TelePower { get; }

        /// <summary>
        /// Both halves of <c>UnitPlayer.manaSpell</c> (<c>:1710-1719</c>): the budget debit and the
        /// organ wound. <b>Amounts arrive already multiplied</b> — the caller applies
        /// <c>warlockDManaMult</c> and this method applies <c>allDManaMult</c> exactly as
        /// <c>manaSpell</c> does, so a live implementation may forward straight to the port's existing
        /// <c>IManaSource.SpendMana(poolCost, organCost)</c>, which has the same shape and the same
        /// contract. A half of 0 must be a no-op.
        /// </summary>
        void SpendMana(float poolCost, float organCost);

        // ---- placement -------------------------------------------------------------------------------

        /// <summary>
        /// AS3 <c>owner.magicX</c> (<c>Unit.as:340</c>, set at <c>:3292</c> to <c>X</c>). The port
        /// already computes this as the magic weapon mount point (<c>WeaponMounts</c>), so it is a read
        /// rather than a new field.
        /// </summary>
        float MagicX { get; }

        /// <summary>AS3 <c>owner.magicY</c> (<c>Unit.as:342</c>, set at <c>:3293</c> to <c>Y - scY*0.5</c>).</summary>
        float MagicY { get; }

        // ---- effect surface --------------------------------------------------------------------------

        /// <summary>
        /// AS3 <c>owner.addEffect(id, value)</c>. The port already has this as
        /// <c>ActiveEffectSet.AddEffect(string, float, int, bool)</c> on <c>UnitStats.Effects</c>, so
        /// the live implementation is a one-line forward with the default duration and announce flags.
        /// </summary>
        void AddEffect(string effectId, float value);

        /// <summary>
        /// AS3 <c>owner.shithp = value</c> (<c>Spell.as:314-318</c>). The port owns the field as
        /// <c>UnitStats.ShitHp</c>; see <see cref="SpellShield"/> for the rule that consumes it.
        /// </summary>
        void SetShitHp(float value);

        /// <summary>AS3 <c>World.w.pers.alicornShitHP</c> (<c>Pers.as:467</c>, default 2000).</summary>
        float AlicornShitHp { get; }

        /// <summary>
        /// AS3 <c>gg.t_cryst</c> (<c>UnitPlayer.as:147</c>). Read and written by <c>cast_cryst</c>
        /// (<c>Spell.as:323-331</c>): <c>&gt; 0</c> downgrades the cast to <c>est = 2</c>, then it is set
        /// to 5 regardless. <b>Per-player state, not per-spell</b> — a second crystal cast inside the
        /// window is the one that sets <c>est = 2</c>, which is why it cannot live on
        /// <see cref="Spell"/>.
        /// </summary>
        int CrystalCooldown { get; set; }
    }
}
