namespace PFE.Systems.Magic
{
    /// <summary>
    /// The production <see cref="ISpellHost"/> — the adapter that binds the pure cast path to the live
    /// caster and the world.
    ///
    /// <para><b>What this class is for.</b> <see cref="Spell"/> and <see cref="SpellCastRules"/> are
    /// deliberately free of Unity types so they run offline, which means every read they make has to
    /// arrive through <see cref="ISpellHost"/>. This is the only place that knows which port member
    /// answers which AS3 read, so it is the only place that can get the mapping wrong. It is therefore
    /// the place the tests point at.</para>
    ///
    /// <para><b>The mapping, one line per member</b> — the oracle read on the left, the port's home on
    /// the right. Anything absent from this table is a gap, not an omission:</para>
    /// <list type="table">
    /// <item><term><c>gg.rat</c></term><description><see cref="ISpellCaster.Rat"/> — no producer in the
    /// port yet (AS3 sets it from the <c>potion_rat</c> effect, <c>UnitPlayer.as:4018</c>)</description></item>
    /// <item><term><c>gg.atkPoss</c></term><description><see cref="ISpellCaster.AtkPossible"/> — AS3's
    /// default is 1 and the port must match it, since the zeroing skill rows exist in the data
    /// (<c>AllData.as:6152</c>) but the skill-effect path does not write the port field yet</description></item>
    /// <item><term><c>owner.mana</c></term><description><c>CharacterStats.MagicMana</c></description></item>
    /// <item><term><c>pers.manaHP</c></term><description><c>CharacterStats.manaHp</c></description></item>
    /// <item><term><c>pers.allDManaMult</c> / <c>warlockDManaMult</c> / <c>spellDown</c></term>
    /// <description><c>CharacterStats</c> fields of the same names</description></item>
    /// <item><term><c>Unit.spellPower</c> / <c>Pers.telePower</c> / <c>Pers.alicornShitHP</c></term>
    /// <description><c>CharacterStats.spellPower</c> / <c>.telePower</c> / <c>.alicornShitHP</c></description></item>
    /// <item><term><c>owner.magicX</c> / <c>magicY</c></term><description>the weapon mount point
    /// (<c>WeaponMounts</c>) — computed, not a field</description></item>
    /// <item><term><c>owner.shithp</c></term><description><c>UnitStats.ShitHp</c></description></item>
    /// <item><term><c>owner.addEffect</c></term><description><c>UnitStats.Effects.AddEffect</c></description></item>
    /// <item><term><c>loc.isLine</c></term><description><c>ITileQueryService.Raycast</c>, negated</description></item>
    /// <item><term><c>Snd.ps</c> / <c>gui.infoText</c> / <c>gui.bulb</c></term><description><b>the
    /// message layer, which the port does not have</b> — see <see cref="ISpellWorld.ShowInfoText"/></description></item>
    /// </list>
    ///
    /// <para><b>No Unity type and no <c>ECall</c> appears here</b>, so unlike the live caster this class
    /// is fully offline-testable: the two collaborators are interfaces, and a fixture supplies fakes.
    /// That is the whole reason the seam is split in two.</para>
    /// </summary>
    public sealed class LiveSpellHost : ISpellHost
    {
        private readonly ISpellCaster _caster;
        private readonly ISpellWorld _world;

        public LiveSpellHost(ISpellCaster caster, ISpellWorld world)
        {
            _caster = caster ?? throw new System.ArgumentNullException(nameof(caster));
            _world = world ?? throw new System.ArgumentNullException(nameof(world));
        }

        // ---- identity and permission ---------------------------------------------------------------

        bool ISpellHost.IsPlayer => _caster.IsPlayer;
        int ISpellHost.Rat => _caster.Rat;
        int ISpellHost.SpellsPossible => _caster.SpellsPossible;
        bool ISpellHost.AtkPossible => _caster.AtkPossible;
        bool ISpellHost.WeaponRespect(string spellId) => _caster.WeaponRespect(spellId);

        // ---- alicorn mode --------------------------------------------------------------------------
        // The one member that comes from the world rather than the caster. AS3 reads it as
        // `World.w.alicorn`, a global, and both of its uses are world-shaped (a global refusal and a
        // global spell substitution), so it is not caster state.
        bool ISpellHost.Alicorn => _world.Alicorn;

        // ---- the mana pair -------------------------------------------------------------------------

        float ISpellHost.Mana => _caster.Mana;
        float ISpellHost.ManaHp => _caster.ManaHp;
        float ISpellHost.AllDManaMult => _caster.AllDManaMult;
        float ISpellHost.WarlockDManaMult => _caster.WarlockDManaMult;
        float ISpellHost.SpellDown => _caster.SpellDown;

        // AS3 `manaSpell(param1, param2)` does both halves in one call, but `Spell.cast()` reaches it
        // as two separate statements and the two amounts are computed from different attributes
        // (`magic` and `mana`), so they arrive here one at a time. Forwarding each with a zero in the
        // other slot is exactly equivalent — and a zero half must be a no-op on the live side, which
        // the port's existing `CharacterStats.ApplyManaDamage` already is (`if (damage <= 0f) return`).
        void ISpellHost.SpendMana(float amount) => _caster.SpendMana(amount, 0f);
        void ISpellHost.DamageManaOrgan(float amount) => _caster.SpendMana(0f, amount);

        // ---- placement -----------------------------------------------------------------------------

        float ISpellHost.MagicX => _caster.MagicX;
        float ISpellHost.MagicY => _caster.MagicY;
        float ISpellHost.SpellPower => _caster.SpellPower;
        float ISpellHost.TelePower => _caster.TelePower;

        /// <summary>
        /// The one place the oracle's double negative is resolved: AS3 asks
        /// <c>loc.isLine(from, to)</c> — "is the way clear" — and the port's primitive answers "what did
        /// the ray hit". See <see cref="ISpellWorld.IsBlocked"/> for why the negation lives here.
        /// </summary>
        bool ISpellHost.IsLineVisible(float fromX, float fromY, float toX, float toY)
            => !_world.IsBlocked(fromX, fromY, toX, toY);

        // ---- effect surface ------------------------------------------------------------------------

        void ISpellHost.AddEffect(string effectId, float value) => _caster.AddEffect(effectId, value);
        void ISpellHost.SetShitHp(float value) => _caster.SetShitHp(value);
        float ISpellHost.AlicornShitHp => _caster.AlicornShitHp;

        int ISpellHost.CrystalCooldown
        {
            get => _caster.CrystalCooldown;
            set => _caster.CrystalCooldown = value;
        }

        // ---- refusal feedback ----------------------------------------------------------------------

        void ISpellHost.PlaySound(string soundId, float x, float y) => _world.PlaySound(soundId, x, y);
        void ISpellHost.ShowInfoText(string key) => _world.ShowInfoText(key);
        void ISpellHost.ShowInfoText(string key, float number) => _world.ShowInfoText(key, number);
        void ISpellHost.ShowBulb(float x, float y) => _world.ShowBulb(x, y);
    }
}
