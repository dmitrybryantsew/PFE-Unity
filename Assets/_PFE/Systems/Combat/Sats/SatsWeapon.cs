using UnityEngine;
using PFE.Data.Definitions;
using PFE.Sim.Combat.Sats;
using PFE.Systems.RPG;
using PFE.Systems.Weapons;

namespace PFE.Systems.Combat.Sats
{
    /// <summary>
    /// Everything S.A.T.S. reads off the equipped weapon, sampled in one place.
    ///
    /// <para><b>Why a snapshot rather than live reads.</b> Three of these values do not live on the
    /// weapon at all — <c>satsCons</c>, <c>satsQue</c> and the two presence flags are root attributes
    /// of the <c>&lt;weapon&gt;</c> row in <c>AllData</c>, which <c>WeaponDefinition</c> does not
    /// carry (<c>WeaponXmlExtrasReader</c> is the reader). Two more are products the Oracle caches on
    /// the instance in <c>setPers</c> (<c>precMult</c>, <c>consMult</c>). Composing them at the point
    /// of use would put the same four lines in six places, and one of them would get the perk pass
    /// wrong.</para>
    ///
    /// <para><b>What is NOT here: the situational precision multiplier.</b> <c>Weapon.resultPrec</c>
    /// deliberately excludes <c>owner.precMult</c> — see <see cref="SatsRules.HitChance"/> — so
    /// <see cref="PrecMult"/> is <c>allPrecMult * perkPrec</c> and nothing else.</para>
    /// </summary>
    public readonly struct SatsWeapon
    {
        /// <summary><c>&lt;sats no&gt;</c> present — the weapon cannot use S.A.T.S. at all. Presence,
        /// not value: <c>&lt;sats no='0'/&gt;</c> still means no (<c>Weapon.as:447-452</c>).</summary>
        public readonly bool NoSats;

        /// <summary><c>&lt;sats noperc&gt;</c> present — no percentage is shown, <b>and no unit can be
        /// locked</b> (the hover glow is what <c>setCel</c> hit-tests).</summary>
        public readonly bool NoPerc;

        public readonly float SatsCons;
        public readonly int SatsQue;
        public readonly float SatsMult;

        /// <summary>The weapon perk family's <c>Cons</c> stat — 0.75 for pistols with the
        /// <c>pistol</c> perk, 1 otherwise (<c>Weapon.as:1010</c>).</summary>
        public readonly float ConsMult;

        /// <summary><c>weapon.precision</c> in pixels — the data <c>@prec</c> × 40.</summary>
        public readonly float PrecisionPx;

        /// <summary><c>weapon.antiprec</c> in pixels — the data <c>@antiprec</c> × 40.</summary>
        public readonly float AntiPrecisionPx;

        public readonly float DeviationDeg;
        public readonly float Mazil;

        /// <summary><c>weapon.precMult</c> = <c>allPrecMult * perkPrec</c> — the <i>standing</i>
        /// precision multiplier <c>resultPrec</c> uses. Deliberately not the situational one.</summary>
        public readonly float PrecMult;

        /// <summary><c>pers.weaponSkills[skill]</c> — the multiplier, not the tier.</summary>
        public readonly float SkillMultiplier;

        /// <summary><c>pers.getWeapLevel(skill)</c> — the tier, used only for the entry gate.</summary>
        public readonly int SkillTier;

        public readonly int WeaponLevel;
        public readonly int Status;
        public readonly bool IsMelee;
        public readonly float ExplRadius;

        private SatsWeapon(
            bool noSats, bool noPerc, float satsCons, int satsQue, float satsMult, float consMult,
            float precisionPx, float antiPrecisionPx, float deviationDeg, float mazil,
            float precMult, float skillMultiplier, int skillTier, int weaponLevel, int status,
            bool isMelee, float explRadius)
        {
            NoSats = noSats;
            NoPerc = noPerc;
            SatsCons = satsCons;
            SatsQue = satsQue;
            SatsMult = satsMult;
            ConsMult = consMult;
            PrecisionPx = precisionPx;
            AntiPrecisionPx = antiPrecisionPx;
            DeviationDeg = deviationDeg;
            Mazil = mazil;
            PrecMult = precMult;
            SkillMultiplier = skillMultiplier;
            SkillTier = skillTier;
            WeaponLevel = weaponLevel;
            Status = status;
            IsMelee = isMelee;
            ExplRadius = explRadius;
        }

        /// <summary>
        /// Sample the live weapon.
        ///
        /// <para><b><c>tip</c> decides the reticle, and <c>tip == 1</c> is the only melee.</b>
        /// <c>Sats.as:121-135</c> branches on <c>weapon.tip &gt; 1</c>: above that it draws the
        /// ballistic tracer, otherwise the melee reach circle. <c>WeaponType</c> carries the same
        /// numbering as <c>tip</c> (<c>WeaponDefinition.cs:116</c>), so the comparison is direct.
        /// <c>satsMelee</c> — the flag that selects the range override in <c>getUnits</c> — is set in
        /// exactly one place, <c>WClub.as:114</c>, which is the tip-1 class.</para>
        /// </summary>
        public static SatsWeapon Read(WeaponRuntimeState state, CharacterStats stats, PlayerWeaponLoadout loadout)
        {
            IWeaponStats def = state.Def;
            WeaponXmlExtras extras = SatsWeaponExtras.Read(def.weaponId);

            // The Oracle's perk pass (Weapon.as:999-1041): at most one family applies, because `opt`
            // is written from the row's single root `perk`.
            float consMult = 1f;
            float precMult = stats != null ? stats.allPrecMult : 1f;
            if (stats != null && CharacterStats.IsWeaponPerkId(extras.Perk))
            {
                consMult = stats.WeaponPerkStat(extras.Perk, "Cons", 1f);
                precMult *= stats.WeaponPerkStat(extras.Perk, "Prec", 1f);
            }

            IWeaponStatSource pers = stats;
            float skillMult = pers != null ? pers.WeaponSkillMultiplier(def.skillLevel) : 1f;
            int skillTier = pers != null ? pers.OwnerWeaponSkillLevel(def.skillLevel) : 0;
            float mazil = pers != null ? pers.MazilAdd : 0f;

            int reserve = 0;
            if (loadout != null && loadout.AmmoSource != null && !string.IsNullOrEmpty(state.ResolvedAmmoType))
            {
                reserve = loadout.AmmoSource.GetAmmoCount(state.ResolvedAmmoType);
            }

            float mana = stats != null ? stats.MagicMana : 0f;
            float maxMana = stats != null ? stats.MaxMagicMana : 0f;

            int status = SatsWeaponStatus.Compute(
                broken:            state.IsBroken,
                jammed:            state.Jammed,
                magazineEmpty:     state.NeedsReload,
                reserveEmpty:      reserve < def.ammoPerShot,
                manaShort:         def.magicPoolCost > mana,
                manaAtFull:        maxMana > 0f && !(mana < maxMana * 0.99f),
                attacking:         state.TAttack > 0,
                reloading:         state.TReload > 0,
                consumesMagazine:  def.magazineSize > 0
                                   && def.ammoType != "recharg"
                                   && def.ammoType != "not");

            return new SatsWeapon(
                noSats:          extras.NoSats,
                noPerc:          extras.NoPerc,
                satsCons:        extras.SatsCons,
                satsQue:         extras.SatsQue > 0 ? extras.SatsQue : 1,
                satsMult:        stats != null ? stats.satsMult : 1f,
                consMult:        consMult,
                precisionPx:     def.precision,
                antiPrecisionPx: def.antiPrecision,
                deviationDeg:    def.deviation,
                mazil:           mazil,
                precMult:        precMult,
                skillMultiplier: skillMult,
                skillTier:       skillTier,
                weaponLevel:     def.weaponLevel,
                status:          status,
                isMelee:         def.weaponType == WeaponType.Melee,
                explRadius:      def.explRadius);
        }
    }

    /// <summary>
    /// <c>WeaponXmlExtrasReader</c> for a weapon id, with the <c>AllData</c> document loaded once.
    ///
    /// <para><b>Why a second cache.</b> <c>PipPageInvView</c> has the same three lines privately.
    /// Sharing it would mean either moving the loader into the PipBuck (a dependency the combat side
    /// must not take) or reaching into a page view from a mode. The load itself is a single
    /// <c>Resources.Load</c> held in a static; the duplication is four lines against a coupling that
    /// would be permanent.</para>
    /// </summary>
    internal static class SatsWeaponExtras
    {
        private static string _allData;
        private static bool _read;

        public static WeaponXmlExtras Read(string weaponId)
        {
            if (string.IsNullOrEmpty(weaponId)) return WeaponXmlExtras.Empty;

            if (!_read)
            {
                _read = true;
                TextAsset asset = Resources.Load<TextAsset>("Loot/AllData");
                _allData = asset != null ? asset.text : null;
            }

            if (_allData == null) return WeaponXmlExtras.Empty;

            return WeaponXmlBlocks.TryGetBlock(_allData, weaponId, out WeaponXmlBlock block)
                ? WeaponXmlExtrasReader.Read(block)
                : WeaponXmlExtras.Empty;
        }
    }
}
