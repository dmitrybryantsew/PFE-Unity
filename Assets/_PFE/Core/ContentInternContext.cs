using System;
using System.Collections.Generic;
using PFE.Sim.Content;

namespace PFE.Core
{
    /// <summary>
    /// The ambient carrier for the interned content-id tables that <c>SimUnitState.DefinitionId</c>
    /// and <c>SimProjectileState.DefinitionId</c> address.
    ///
    /// <para><b>Ambient for the same reason as the other three.</b> The producers are a plain DI
    /// singleton (<c>ProjectileFactory</c>) and an <c>AddComponent</c>-built controller reached from
    /// <c>RoomUnitSpawner</c> — VContainer observes neither, and a static spawner cannot be
    /// constructor-injected at all. Same push-at-build shape as <see cref="RuntimeEntityIdContext"/>,
    /// <c>SinglePlayerSlotContext</c> and <c>SimAuthorityContext</c>.</para>
    ///
    /// <para><b>The tables are built lazily, and that is not premature optimisation.</b> The content
    /// registry is <i>empty</i> at container build — <c>GameDatabase</c>'s constructor creates it and
    /// the mod loader populates it afterwards — so building the tables inside the build callback would
    /// produce two empty tables and every projected <c>DefinitionId</c> would be 0 forever. The
    /// callback therefore pushes a <i>source</i>, and the table is materialised on first access. The
    /// first access cannot precede the content load: the only callers are spawn paths, and nothing can
    /// spawn before its definition exists.</para>
    ///
    /// <para><b>Two tables, not one.</b> A unit definition and a weapon definition are different
    /// content namespaces and both are addressed by a <c>ushort</c> on different records. Sharing one
    /// table would make an unrelated unit addition renumber every weapon, and would let a unit key and
    /// a weapon key be equal — two answers that must never be confused.</para>
    ///
    /// <para><b>Defaults are usable, not null.</b> Offline fixtures, editor tooling and a headless room
    /// build have no container. An empty table keeps those paths working and makes every lookup answer
    /// <see cref="ContentInternTable.Uninterned"/>, which is observable — unlike a null table, which
    /// would throw inside a per-tick projection and take the tick with it.</para>
    /// </summary>
    public static class ContentInternContext
    {
        private static ContentInternTable _units = ContentInternTable.Empty;
        private static ContentInternTable _weapons = ContentInternTable.Empty;

        private static Func<IEnumerable<string>> _unitSource;
        private static Func<IEnumerable<string>> _weaponSource;

        private static bool _unitsResolved;
        private static bool _weaponsResolved;

        /// <summary>Unit definitions (<c>ContentType.Unit</c>). Never null; built on first access.</summary>
        public static ContentInternTable Units
        {
            get
            {
                if (!_unitsResolved) Resolve(ref _units, _unitSource, ref _unitsResolved);
                return _units;
            }
        }

        /// <summary>Weapon definitions (<c>ContentType.Weapon</c>). Never null; built on first access.</summary>
        public static ContentInternTable Weapons
        {
            get
            {
                if (!_weaponsResolved) Resolve(ref _weapons, _weaponSource, ref _weaponsResolved);
                return _weapons;
            }
        }

        /// <summary>
        /// Whether a real content set has been built. False means every projected
        /// <c>DefinitionId</c> is <see cref="ContentInternTable.Uninterned"/> — a fixture or a
        /// containerless build, not a live session.
        /// </summary>
        public static bool IsConfigured => Units.Count > 0 || Weapons.Count > 0;

        /// <summary>
        /// Push the content-id sources at container build. Nulls degrade to empty rather than throwing;
        /// passing a source drops any table already built, so a content reload can call this again.
        /// </summary>
        public static void SetSources(
            Func<IEnumerable<string>> unitContentIds,
            Func<IEnumerable<string>> weaponContentIds)
        {
            _unitSource = unitContentIds;
            _weaponSource = weaponContentIds;

            _units = ContentInternTable.Empty;
            _weapons = ContentInternTable.Empty;
            _unitsResolved = unitContentIds == null;
            _weaponsResolved = weaponContentIds == null;
        }

        /// <summary>
        /// Push ready-made tables. The fixture and tooling path — no container, no content registry.
        /// </summary>
        public static void Configure(ContentInternTable units, ContentInternTable weapons)
        {
            _units = units ?? ContentInternTable.Empty;
            _weapons = weapons ?? ContentInternTable.Empty;
            _unitSource = null;
            _weaponSource = null;
            _unitsResolved = true;
            _weaponsResolved = true;
        }

        /// <summary>
        /// Drop the built tables so the next access rebuilds from the sources. For a content reload —
        /// a mod hot-load, or a test that swaps the content set.
        /// </summary>
        public static void Invalidate()
        {
            _units = ContentInternTable.Empty;
            _weapons = ContentInternTable.Empty;
            _unitsResolved = _unitSource == null;
            _weaponsResolved = _weaponSource == null;
        }

        /// <summary>Restore the containerless defaults. Bootstrap/test hook.</summary>
        public static void Reset()
        {
            _unitSource = null;
            _weaponSource = null;
            _units = ContentInternTable.Empty;
            _weapons = ContentInternTable.Empty;
            _unitsResolved = true;
            _weaponsResolved = true;
        }

        private static void Resolve(
            ref ContentInternTable table,
            Func<IEnumerable<string>> source,
            ref bool resolved)
        {
            if (resolved) return;

            table = ContentInternTable.Build(source != null ? source() : null);
            resolved = true;
        }
    }
}
