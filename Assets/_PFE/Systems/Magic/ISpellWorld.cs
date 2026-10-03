namespace PFE.Systems.Magic
{
    /// <summary>
    /// What a cast needs from the world <b>around</b> the caster: the line-of-sight test, the
    /// alicorn-mode flag, and the three presentation channels.
    ///
    /// <para><b>Split from <see cref="ISpellCaster"/> so the two have different lifetimes.</b> The
    /// caster's state is per-unit and changes every tick; the world is shared, and the presentation is
    /// the only part of the cast path the port has not modelled at all. Keeping them apart means the
    /// live caster can be written and reviewed without the HUD, and the HUD can be filled in later
    /// without touching the caster.</para>
    /// </summary>
    public interface ISpellWorld
    {
        /// <summary>
        /// AS3 <c>World.w.alicorn</c> — the global alicorn-mode flag. Read twice in the cast path: it
        /// refuses every spell except <c>sp_mshit</c> (<c>Spell.as:193-196</c>) and it substitutes
        /// <c>sp_mshit</c> for the selected spell on the Def key (<c>UnitPlayer.as:2234</c>).
        /// </summary>
        bool Alicorn { get; }

        /// <summary>
        /// AS3 <c>owner.loc.isLine(X, Y, cx, cy)</c> (<c>Location.as:2368</c>), <b>inverted</b>: true
        /// when something solid blocks the segment. Only consulted when the spell carries
        /// <c>line='1'</c>.
        ///
        /// <para>Inverted deliberately. The port's primitive is a raycast
        /// (<c>ITileQueryService.Raycast</c>, used exactly this way by
        /// <c>PlayerTelekinesisController</c>), which answers "what did I hit"; the oracle asks "is the
        /// way clear". Naming this member after the <i>primitive</i> rather than after the oracle keeps
        /// the double negative in one place — <see cref="LiveSpellHost.IsLineVisible"/> — instead of
        /// spreading it across every implementation.</para>
        /// </summary>
        bool IsBlocked(float fromX, float fromY, float toX, float toY);

        /// <summary>AS3 <c>Snd.ps(id, x, y)</c> (<c>Spell.as:287</c>, and the <c>"nomagic"</c> refusal cue).</summary>
        void PlaySound(string soundId, float x, float y);

        /// <summary>
        /// AS3 <c>World.w.gui.infoText(key, null, null, false)</c> — a localisation key with no number,
        /// e.g. <c>"disSpell"</c>, <c>"noSpells"</c>, <c>"overMana"</c>, <c>"noMana"</c>,
        /// <c>"noVisible"</c>.
        ///
        /// <para><b>The port has no message layer</b>, and that is a known, recorded gap rather than an
        /// oversight: <c>HitAvoidance</c> and <c>MagicWeaponController</c> both carry the same note for
        /// their own refusals. A live implementation may legitimately no-op this until the layer exists;
        /// what it must <i>not</i> do is turn it into a log line that cannot be false.</para>
        /// </summary>
        void ShowInfoText(string key);

        /// <summary>
        /// AS3 <c>World.w.gui.infoText("spellCuld", Math.ceil(t_culd / World.fps), …)</c>
        /// (<c>Spell.as:220</c>). A separate overload, not a nullable number, because AS3 passes
        /// <c>null</c> in the other case and only this one caller passes a value.
        /// </summary>
        void ShowInfoText(string key, float number);

        /// <summary>AS3 <c>World.w.gui.bulb(x, y)</c> — the on-screen attention marker.</summary>
        void ShowBulb(float x, float y);
    }
}
