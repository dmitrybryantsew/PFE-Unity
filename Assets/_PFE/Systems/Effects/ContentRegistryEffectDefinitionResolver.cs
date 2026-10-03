using PFE.Data;
using PFE.Data.Definitions;
using PFE.ModAPI;

namespace PFE.Systems.Effects
{
    /// <summary>
    /// <see cref="IEffectDefinitionResolver"/> backed by a <see cref="ContentRegistry"/> — the
    /// production wiring, matching <c>ContentRegistryAmmoResolver</c> so there is one resolution story
    /// rather than one per subsystem.
    ///
    /// <para><b>A null registry is tolerated and answers <c>null</c> for every id.</b> That is not a
    /// silent failure: <see cref="ActiveEffectSet.AddEffect"/> refuses to materialise an effect with no
    /// definition, so a missing registry shows up as "effects do nothing" with nothing thrown inside a
    /// tick — the same defensive shape the ammo resolver adopted after the HUD crash of 2026-10-03,
    /// where assuming an initialisation hook had run took the whole overlay down.</para>
    /// </summary>
    public sealed class ContentRegistryEffectDefinitionResolver : IEffectDefinitionResolver
    {
        private readonly ContentRegistry _registry;

        public ContentRegistryEffectDefinitionResolver(ContentRegistry registry)
        {
            _registry = registry;
        }

        /// <inheritdoc/>
        public IEffectTemplate Resolve(string effectId)
        {
            if (_registry == null || string.IsNullOrEmpty(effectId))
            {
                return null;
            }

            return _registry.Get<EffectDefinition>(ContentType.Effect, effectId);
        }
    }
}
