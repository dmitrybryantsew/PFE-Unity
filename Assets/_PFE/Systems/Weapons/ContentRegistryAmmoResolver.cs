using PFE.Data;
using PFE.Data.Definitions;
using PFE.ModAPI;
using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// <see cref="IAmmoResolver"/> backed by a <see cref="ContentRegistry"/> — the production wiring.
    ///
    /// <para><b>Why the registry rather than <c>Resources.Load</c>.</b> <c>AmmoDefinition</c> is
    /// <see cref="IGameContent"/>, so the registry already indexes every row by both its qualified id
    /// (<c>pfe.base.p5_1</c>) and its bare id (<c>p5_1</c>), and it is the same table mods register
    /// into. A <c>Resources.Load($"Ammo/{id}")</c> would answer only for the base game and would
    /// silently miss a modded round — and it allocates a path string per shot. This mirrors how
    /// content is resolved everywhere else in production.</para>
    ///
    /// <para><b>A null registry is tolerated.</b> The HUD crash of 2026-10-03 came from assuming an
    /// initialisation hook had run; this class instead answers <c>null</c> for every id when it has no
    /// registry, which puts the caller on AS3's "неправильный патрон" default-multipliers path rather
    /// than throwing inside a shot.</para>
    ///
    /// <para><b>A registered-but-EMPTY registry is the dangerous case, and it is now loud.</b> It
    /// answers <c>null</c> exactly like the null-registry case, so the weapon keeps firing with the
    /// weapon's own numbers and every round's ballistics is silently dropped — which is
    /// indistinguishable from "ammo has no effect" and cost a whole session on 2026-10-03:
    /// <c>GameLifetimeScope</c> registered a second, never-initialized <c>ContentRegistry</c> while
    /// <c>GameDatabase</c> populated its own private one, so selecting armour-piercing <c>p5_1</c>
    /// changed nothing and a 20-armour dummy kept taking 0 from the minigun. The check below is the
    /// cheap tell that was missing: it fires once, on the first resolution attempt, and only when the
    /// table has no <see cref="ContentType.Ammo"/> rows at all — a state no correct wiring can be in.
    /// It deliberately does NOT fire for a legitimate miss (an id that names nothing), because a
    /// modded round absent from this install is normal and would otherwise spam.</para>
    /// </summary>
    public sealed class ContentRegistryAmmoResolver : IAmmoResolver
    {
        private readonly ContentRegistry _registry;

        /// <summary>True once the empty-registry warning has been emitted, so it logs once per resolver.</summary>
        private bool _warnedEmptyRegistry;

        public ContentRegistryAmmoResolver(ContentRegistry registry)
        {
            _registry = registry;
        }

        /// <inheritdoc/>
        public AmmoDefinition Resolve(string ammoId)
        {
            if (_registry == null || string.IsNullOrEmpty(ammoId))
                return null;

            // See the type doc: a registry with no Ammo rows at all cannot be the one GameDatabase
            // initialized, so say so rather than letting every shot quietly carry identity ammo terms.
            if (!_warnedEmptyRegistry && _registry.GetCount(ContentType.Ammo) == 0)
            {
                _warnedEmptyRegistry = true;
                Debug.LogError(
                    "[ContentRegistryAmmoResolver] The ContentRegistry has no Ammo rows, so no round's " +
                    "ballistics (damage multiplier, armour piercing, armour multiplier, knockback, " +
                    "precision, penetration budget, damage-type override) can reach any shot. " +
                    "This is a wiring fault, not a data one: the resolver is holding a registry that " +
                    "was never initialized. GameDatabase populates ITS OWN registry in Initialize(); " +
                    "every consumer must be given that same instance. See GameLifetimeScope's data " +
                    "system block.");
            }

            return _registry.Get<AmmoDefinition>(ContentType.Ammo, ammoId);
        }
    }
}
