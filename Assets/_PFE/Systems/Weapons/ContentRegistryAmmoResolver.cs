using PFE.Data;
using PFE.Data.Definitions;
using PFE.ModAPI;

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
    /// </summary>
    public sealed class ContentRegistryAmmoResolver : IAmmoResolver
    {
        private readonly ContentRegistry _registry;

        public ContentRegistryAmmoResolver(ContentRegistry registry)
        {
            _registry = registry;
        }

        /// <inheritdoc/>
        public AmmoDefinition Resolve(string ammoId)
        {
            if (_registry == null || string.IsNullOrEmpty(ammoId))
                return null;

            return _registry.Get<AmmoDefinition>(ContentType.Ammo, ammoId);
        }
    }
}
