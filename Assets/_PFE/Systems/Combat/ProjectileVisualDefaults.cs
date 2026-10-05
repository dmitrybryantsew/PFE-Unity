using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The fallback art for a round whose weapon declares no bullet visual.
    ///
    /// <para><b>This is the oracle's behaviour, not a convenience.</b> AS3 <c>Weapon.as:521-525</c>:</para>
    /// <code>
    /// if(this.visbul) { this.vBullet = getDefinitionByName("visbul" + this.visbul) as Class; }
    /// else            { this.vBullet = visualBullet; }
    /// </code>
    /// <para>— so a weapon with no <c>vbul</c> fires the real class <c>visualBullet</c>
    /// (<c>pfe/scripts/visualBullet.as</c>), never "nothing". The port imports that symbol as
    /// <c>Resources/ProjectileVisuals/default_ballistic.asset</c> (<c>sourceClassName: visualBullet</c>,
    /// symbol 3827), and 35 weapons already point at it.</para>
    ///
    /// <para><b>What went wrong without this.</b> <c>Projectile.ApplyVisual(null)</c> falls to
    /// <c>RestoreDefaultVisual()</c>, which restores the <i>template's</i> defaults — and the shipped
    /// prefab carried <c>m_Sprite: {fileID: 0}</c>. So the renderer ended up enabled with a null sprite:
    /// a live, ticking, invisible round. The importer's own fallback
    /// (<c>ProjectileGraphicsImportWindow.ShouldUseDefaultBallistic</c>) only fires for
    /// <c>WeaponType.Guns</c>/<c>BigGun</c>, so every <c>WeaponType.Internal</c> (tip='0') weapon — the
    /// base <i>ranged</i> class, and the trap that file's own comments name — was left with a null visual.
    /// Measured: 6 real guns, including <c>robominigun</c> and the turrets.</para>
    ///
    /// <para>Resolving here rather than in the importer is deliberate: it also covers a weapon whose
    /// <c>vbul</c> art failed to import, which no data-side guard can see.</para>
    /// </summary>
    public static class ProjectileVisualDefaults
    {
        /// <summary>The imported id of AS3's <c>visualBullet</c>.</summary>
        public const string VisualId = "default_ballistic";

        /// <summary>Path under a <c>Resources</c> folder, for <c>Resources.Load</c>.</summary>
        public const string ResourcePath = "ProjectileVisuals/" + VisualId;

        /// <summary>
        /// True when the weapon resolved to no art at all and the default must stand in. A null
        /// <i>definition</i> is the only trigger — a definition with no frames is a different fault and
        /// is left to <c>ApplyVisual</c>, which already treats it as "use the default visual".
        /// </summary>
        public static bool NeedsFallback(ProjectileVisualDefinition weaponVisual)
            => weaponVisual == null;

        /// <summary>
        /// The art to apply: the weapon's own when it has one, otherwise <paramref name="fallback"/>.
        /// Pure, so both arms are pinnable offline.
        /// </summary>
        public static ProjectileVisualDefinition Resolve(
            ProjectileVisualDefinition weaponVisual,
            ProjectileVisualDefinition fallback)
            => weaponVisual != null ? weaponVisual : fallback;

        /// <summary>
        /// Importer-side: should this weapon be wired to the default visual because it declares no
        /// <c>vbul</c>?
        ///
        /// <para>Lives here rather than in <c>PFE.Editor</c> on purpose — the offline wall cannot see the
        /// editor assembly (rule #13/#64), so a predicate that only exists there can never be pinned.</para>
        ///
        /// <para><b>Why it takes the weapon's shape rather than just <c>vbul</c>.</b> The old predicate
        /// also required <c>weaponType == Guns || BigGun</c>, which excluded
        /// <see cref="WeaponType.Internal"/> — <b>tip='0', the base <i>ranged</i> class</b>, and the exact
        /// trap <c>DataEnums.cs</c> and <c>WeaponDefinition.cs</c> both warn about by name. That left 6
        /// real guns with a null visual (measured: <c>robominigun</c>, <c>turret1</c>, <c>turret6</c>,
        /// <c>ttweap1</c>, <c>paint</c>, <c>punch</c>), and a null visual draws nothing. The oracle
        /// restricts nothing: <c>Weapon.as:521-525</c> hands <c>visualBullet</c> to every weapon without a
        /// <c>vbul</c>, and the value is inert unless the weapon fires — so only the families that never
        /// spawn a bullet are skipped.</para>
        /// </summary>
        /// <param name="vbul">The weapon's <c>vis.@vbul</c> symbol, or null/empty for none.</param>
        /// <param name="archetype">The weapon's projectile archetype.</param>
        /// <param name="weaponType">The weapon's class, derived from <c>tip</c> plus <c>punch</c>.</param>
        /// <param name="isUnarmed">AS3 <c>punch &gt; 0</c> — the WPunch/WKick family, which spawns no bullet.</param>
        public static bool ShouldWireDefaultVisual(
            string vbul,
            ProjectileArchetype archetype,
            WeaponType weaponType,
            bool isUnarmed)
        {
            if (!string.IsNullOrWhiteSpace(vbul)) return false;
            if (archetype != ProjectileArchetype.Ballistic) return false;
            if (isUnarmed) return false;
            return weaponType != WeaponType.Melee && weaponType != WeaponType.Thrown;
        }
    }
}
