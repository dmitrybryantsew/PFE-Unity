namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Resolves an ammo <b>id</b> to its ballistics row.
    ///
    /// <para><b>Why this exists.</b> <c>WeaponRuntimeState.ResolvedAmmoType</c> is a string — the id a
    /// weapon names (AS3 <c>&lt;a&gt;</c> / the debug override). AS3 turns that into an ammo row
    /// declaratively: <c>Weapon.setAmmo</c> does
    /// <c>param2 = World.w.invent.items[this.ammo].xml</c> and then copies nine attributes off it
    /// (<c>Weapon.as:1746-1809</c>). The port had no equivalent lookup, so <c>AmmoDefinition</c> was
    /// reachable only from editor tooling and the debug overlay — a weapon's <c>damageMultiplier</c>,
    /// <c>armorPiercingBonus</c>, <c>penetrationBudget</c> and the rest never reached a shot. This is
    /// that lookup, and it is the seam every ammo property hangs off.</para>
    ///
    /// <para><b>Null is a legitimate answer and must not be treated as an error.</b> Three states
    /// produce it: no resolver wired (tests, headless), an id that names nothing (<c>"recharg"</c> and
    /// <c>"not"</c> are AS3 sentinels, not rows), and a component-fed weapon whose ammo row is a
    /// <c>compw</c>/<c>stuff</c> entry that may not exist as an <c>AmmoDefinition</c>. In AS3 a failing
    /// lookup <c>trace</c>s "Неправильный патрон" and leaves every ammo multiplier at its default
    /// (<c>setAmmo</c> assigns the defaults <i>before</i> it reads, and returns on a null row after
    /// only the <c>ammo</c> id is set). Callers must reproduce that: an unresolved id means
    /// <b>default multipliers</b>, never zeroed damage.</para>
    ///
    /// <para>Mirrors <see cref="PFE.Systems.Inventory.IAmmoSource"/>, which is the <i>count</i> half —
    /// that answers "how many rounds are in the bag", this answers "what does one round do".</para>
    /// </summary>
    public interface IAmmoResolver
    {
        /// <summary>
        /// The ballistics row for <paramref name="ammoId"/>, or <c>null</c> when it cannot be resolved.
        /// Implementations must not throw and must treat null/empty as unresolvable.
        /// </summary>
        PFE.Systems.Combat.IAmmoStats Resolve(string ammoId);
    }
}
