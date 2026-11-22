using System;
using PFE.Sim.State;

namespace PFE.Sim.Content
{
    /// <summary>
    /// The conversion boundary for the command uplink's interned weapon key: producer side
    /// (content id → key) and host side (key → the <c>SimUnitState.WeaponIdHash</c> content hash).
    /// Pure C# and explicit about its table so the whole round trip executes in the offline tests:
    /// the ambient <c>ContentInternContext.Weapons</c> is bound by the call sites, not here, because
    /// <c>PFE.Sim</c> must stay executable without the Assembly-CSharp ambient carriers.
    ///
    /// <para><b>Why one home and not two inline conversions.</b> Both directions encode the same
    /// contract — "the command carries the content-versioned key; the unit record carries the
    /// content hash" — and a conversion written twice would drift without either side noticing.
    /// The host-side rule is the one the brief pins: the hash stamped into
    /// <see cref="SimUnitState.WeaponIdHash"/> must be <c>LootItemKey(id, true)</c> of the id the
    /// key resolves to, exactly as when the command carried the hash itself.</para>
    ///
    /// <para><b>The table is caller-supplied, like every rule in this assembly.</b> The offline wall
    /// has no container and no content registry; a rule that reaches for the ambient context
    /// directly would be untestable there. Both production call sites pass
    /// <c>ContentInternContext.Weapons</c> (<c>PlayerController</c> at production,
    /// <c>PfeRemoteBody</c> at the host boundary).</para>
    /// </summary>
    public static class SimWeaponKeyRules
    {
        /// <summary>
        /// The producer direction: a weapon content id as the interned key the wire carries, or
        /// <see cref="ContentInternTable.Uninterned"/> when unarmed or not in this build's table.
        /// An unregistered id degrades to "unarmed" rather than throwing inside a per-tick command
        /// build — the same visible answer the intern table gives a unit with an unknown definition.
        /// </summary>
        public static ushort ResolveWeaponKey(ContentInternTable weapons, string weaponId)
        {
            if (weapons == null) throw new ArgumentNullException(nameof(weapons));
            return weapons.GetKeyOrDefault(weaponId);
        }

        /// <summary>
        /// The host direction: the key an admitted command carries, converted back to the content
        /// hash <see cref="SimUnitState.WeaponIdHash"/> speaks. <see cref="ContentInternTable.Uninterned"/>
        /// (or a key this build's table does not hold — a content-version disagreement) resolves to 0,
        /// "nothing equipped", never a guessed or truncated hash.
        /// </summary>
        public static ulong ResolveWeaponHash(ContentInternTable weapons, ushort weaponKey)
        {
            if (weapons == null) throw new ArgumentNullException(nameof(weapons));
            if (weaponKey == ContentInternTable.Uninterned) return 0UL;

            string id = weapons.GetContentId(weaponKey);
            return id == null
                ? 0UL
                : SimSnapshotProjectionRules.LootItemKey(id, isWeapon: true);
        }
    }
}
