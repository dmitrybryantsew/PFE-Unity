namespace PFE.Systems.Inventory
{
    /// <summary>
    /// Provides ammo count and consumption for weapon reload.
    ///
    /// Implemented by GameInventory for the player character.
    /// Pass null to any consumer to get infinite-ammo fallback behaviour
    /// (the weapon fills its magazine unconditionally, as if in training mode).
    ///
    /// AS3 reference: Weapon.reloadWeapon() — pulls rounds from
    ///   World.w.invent.items[ammo].kol up to (holder - hold).
    /// </summary>
    public interface IAmmoSource
    {
        /// <summary>How many units of <paramref name="ammoType"/> are available.</summary>
        int GetAmmoCount(string ammoType);

        /// <summary>
        /// Remove up to <paramref name="amount"/> units from the source.
        /// Returns the number actually removed (may be less than requested if insufficient).
        /// </summary>
        int ConsumeAmmo(string ammoType, int amount);
    }
}
