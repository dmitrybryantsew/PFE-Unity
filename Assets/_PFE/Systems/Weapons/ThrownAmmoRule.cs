namespace PFE.Systems.Weapons
{
    /// <summary>
    /// AS3 <c>WThrow</c>'s ammo model, as pure functions — no Unity types, so the offline wall can
    /// pin it (a rule that only exists inside a controller method is unreachable from a headless run).
    ///
    /// <para><b>Two genuinely different halves, and using one for both is a live defect.</b>
    /// <c>WThrow.getAmmo()</c> (<c>WThrow.as:268-280</c>) is:</para>
    /// <code>
    /// if(owner.player)
    ///    return (owner as UnitPlayer).getInvAmmo(ammo, 1, 1, true) &gt; 0;   // one ITEM from the inventory
    /// if(this.kolAmmo &lt;= 0)
    ///    return false;
    /// --this.kolAmmo;                                                      // an NPC's own counter, 4 to start
    /// return true;
    /// </code>
    ///
    /// <para>The counter is <b>never refilled</b> — <c>WThrow.reloadWeapon()</c> is an empty override
    /// (<c>:264</c>) — so running it for the player caps a session at <c>kolAmmo</c> throws and then
    /// refuses forever. That is the recorded divergence this type closes.</para>
    /// </summary>
    public static class ThrownAmmoRule
    {
        /// <summary>
        /// What the caller should do about one throw attempt.
        /// </summary>
        public enum Outcome
        {
            /// <summary>The inventory holds a round: consume one item and throw.</summary>
            ConsumeInventory,

            /// <summary>The inventory is wired but empty: refuse (AS3's <c>getInvAmmo</c> returns -1).</summary>
            RefuseInventoryEmpty,

            /// <summary>No inventory is wired — the port's training mode, as everywhere else. Throw.</summary>
            TrainingInfinite,

            /// <summary>A counter owner with rounds left: decrement and throw.</summary>
            ConsumeCounter,

            /// <summary>A counter owner with the counter spent: refuse.</summary>
            RefuseCounterEmpty,
        }

        /// <summary>
        /// AS3 <c>WThrow.as:42</c> — <c>ammo = id;</c> in the constructor. A thrown weapon's ammo
        /// <b>is its own id</b>, which is why every throwable has an item row of the same name
        /// (<c>fgren.asset</c>, <c>molotov.asset</c>, <c>x37.asset</c>).
        ///
        /// <para>The importer leaves <c>ammoType</c> empty for these — the <c>&lt;weapon&gt;</c> row
        /// carries no <c>ammo</c> attribute, and <c>WThrow</c> overwrites whatever
        /// <c>Weapon.as:560</c> read anyway. So the override is applied here rather than by a re-bake:
        /// an empty id would otherwise ask the inventory for a nameless item and always refuse.</para>
        /// </summary>
        /// <param name="resolvedAmmoType">
        /// <c>WeaponRuntimeState.ResolvedAmmoType</c> — non-empty only when something has overridden
        /// it (the debug ammo swap), in which case the override wins, exactly as it does for the
        /// ballistics lookup.
        /// </param>
        public static string AmmoIdFor(string resolvedAmmoType, string weaponId) =>
            !string.IsNullOrEmpty(resolvedAmmoType) ? resolvedAmmoType : weaponId;

        /// <summary>
        /// The port's stand-in for AS3's <c>owner.player</c> test.
        ///
        /// <para><b>Why "a wired inventory" is the right proxy, and not a guess.</b> The port has no
        /// per-owner flag on the controller, but <see cref="PFE.Systems.Inventory.IAmmoSource"/> and
        /// <c>RangedWeaponController</c> already give that interface one documented meaning:
        /// <i>null means training / infinite ammo</i>. Assigning it is what makes a weapon
        /// inventory-limited.</para>
        ///
        /// <para><b>Since 2026-10-05 that is the live case, not a debug-only one.</b>
        /// <c>PlayerController</c> builds a <c>PlayerInventory</c>, which assigns
        /// <c>PlayerWeaponLoadout.AmmoSource</c> — so a live session now takes the <b>inventory</b>
        /// branch for every weapon. Before that wiring only <c>PlayerDebugEditorOverlay</c> ever assigned
        /// it, so every weapon ran in training mode and this paragraph used to say a live session was
        /// "in training mode for every weapon". It is not any more.</para>
        ///
        /// <para><b>The consequence for a thrown weapon is a refusal, not a free throw.</b> An empty
        /// inventory yields <see cref="Outcome.RefuseInventoryEmpty"/>, and
        /// <c>ThrownWeaponController.ConsumeAmmo</c> maps that to <c>false</c> — so the throw simply does
        /// not happen. That is the AS3 player branch and is intended, but it is a <b>behaviour flip</b>
        /// worth watching in play-testing: with an empty starting inventory nothing can be thrown until
        /// the ammo id is stocked, and only 28 of the 75 ammo ids have the <c>ItemDefinition</c> row that
        /// stocking requires (see <c>PlayerDebugEditorOverlay</c>).</para>
        /// </summary>
        public static bool UsesInventory(bool hasAmmoSource) => hasAmmoSource;

        /// <summary>
        /// The decision for one throw attempt. <paramref name="inventoryRounds"/> is ignored when
        /// <paramref name="hasAmmoSource"/> is false, which is how "no inventory" stays distinct from
        /// "an empty inventory" — collapsing those two is what made the counter defect silent.
        /// </summary>
        public static Outcome Decide(bool hasAmmoSource, int inventoryRounds, int kolAmmo)
        {
            if (!UsesInventory(hasAmmoSource))
            {
                // No inventory: the port's training mode. Not the NPC counter — that is a
                // non-player rule, and the player is the only owner this port drives from input.
                return Outcome.TrainingInfinite;
            }

            if (inventoryRounds > 0) return Outcome.ConsumeInventory;
            return Outcome.RefuseInventoryEmpty;
        }

        /// <summary>
        /// The counter half on its own — AS3's non-player branch, kept for a future NPC thrower and
        /// pinned by fixture. <see cref="Decide"/> deliberately does not reach it: the port has no
        /// owner that is known to be an NPC.
        /// </summary>
        public static Outcome DecideForCounterOwner(int kolAmmo) =>
            kolAmmo > 0 ? Outcome.ConsumeCounter : Outcome.RefuseCounterEmpty;
    }
}
